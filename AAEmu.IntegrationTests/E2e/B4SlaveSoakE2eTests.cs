using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

using AAEmu.Commons.Utils.Gate;
using AAEmu.IntegrationTests.E2e.Gate;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// B4 SLAVE-01 S run: budgeted vehicle-lifecycle soak (summon → BindSlave
/// board → load → drive → unboard → restart → re-summon recovery).
///
/// STATED BUDGET (this is the S-run contract — re-running the single-pass
/// M4Vehicles E2E without this table does NOT close the card):
///   actors:    2 concurrent bots round 1 (b4slave01/b4slave02 m3a-m4-replay
///              vehicle legs) + 2 fresh bots round 2 in-window (b4slave03/
///              b4slave04 m3a-m4-replay = live re-summon-from-item recovery
///              proof after R1).
///   loops:     2 summon→board→load→drive→unboard cycles pre-restart + 2
///              cycles under measurement.
///   duration:  measured window ≥ 9 min wall with ≥ 6 min steady-state outside
///              warmup blinds; round-2 scenarios provide the continuous load.
///   restarts:  2 × kill -9 of ONLY the game process (PID-handle kills via
///              E2eStack.RestartGameServer — MySQL + login stay; never pkill),
///              mirroring the M4Vehicles two-restart contract: R1 pre-window,
///              R2 post-window (long up-segment rule: no restart inside).
///   thresholds (GateBudgets defaults): tick p95 ≤ 100 ms, tick max ≤ 250 ms,
///              region worst ≤ 100 ms, region overruns = 0, scheduler avg ≤ 250
///              ms / max ≤ 1000 ms / step failures = 0 (enforced only when
///              scheduler steps ran, else reported n/a), DB ≤ 500/min/
///              embodied-char (denominator = 4 run-provisioned bots),
///              physics ≤ 0.1/min, same-world ≤ 30/60s, tick-overrun warnings
///              = 0/min, autosave p95 ≤ 4000 ms / max ≤ 10000 ms.
///   warmup:    [boot−30s, boot+120s] recorded but NOT counted (SoakWarmup);
///              rates normalize by steady-state minutes only.
///
/// PASS = every scenario passed AND after EACH kill-9 exactly ONE slaves row
/// per summoner, byte-intact (item binding, owner/summoner, HP/MP, position),
/// attachment byte-equality (same cargo point, same pack link, plant_time not
/// rewritten) AND ledger equality (zero dup, zero loss) AND every budget
/// verdict green. Leg split (stated): despawn gates (owner/312/288/801),
/// BindSlave-324, RidersEscape-640, ghost-free fresh-manager recovery are the
/// SlaveLifecycleTests 29/29 unit legs; summon → BindSlave board → load →
/// drive → unboard → kill-9 → re-summon is the live-soak leg here.
///
/// Runs on an ISOLATED lane (own E2E_ROOT/ports/DB/compose; refuses the shared
/// default root, port 3306, .165 game host, and the shared compose project).
/// Teardown is PID-verified (both restart PIDs + StopAll + closed-port probe)
/// — pkill is banned. H stays UNKNOWN: scripted-actor evidence only.
/// </summary>
[Collection("e2e")]
public class B4SlaveSoakE2eTests
{
    private const string ReplayTemplate = "m3a-m4-replay";
    private static readonly string[] Round1Bots = ["b4slave01", "b4slave02"];
    private static readonly string[] Round2Bots = ["b4slave03", "b4slave04"];

    private const uint FarmWagonSlaveTemplateId = 60;
    private const uint PackItemTemplateId = 26488;
    private const uint PlacedPackDoodadTemplateId = 6068;
    private const uint PlacedPackStartPhaseId = 15677;
    private static readonly uint[] CargoAttachPoints = [9, 10, 11, 12];
    private const int SlotTypeSystem = 255;

    private const double MinWindowMinutes = 9;
    private const double MinSteadyMinutes = 6;
    private const int EmbodiedBots = 4;

    private sealed record SlaveSnapshot(uint Id, ulong ItemId, uint TemplateId, int AttachPoint,
        string Name, uint OwnerType, uint OwnerId, uint Summoner, int Hp, int Mp,
        float X, float Y, float Z);
    private sealed record BindingSnapshot(uint DoodadDbId, uint OwnerDbId, uint OwnerType, uint AttachPoint,
        uint TemplateId, uint CurrentPhaseId, DateTime PlantTime, ulong ItemId, uint HouseId, int Data,
        float X, float Y, float Z, uint ItemTemplateId);
    private sealed record ItemSnapshot(ulong Id, string Type, uint TemplateId, ulong ContainerId,
        int SlotType, int Slot, int Count, uint Owner, uint MadeUnitId);
    private sealed record AttachmentSnapshot(SlaveSnapshot? Slave, int SlaveRowCount, List<BindingSnapshot> Bindings,
        Dictionary<ulong, ItemSnapshot> Items, Dictionary<ulong, ulong> ContainerOwners);
    private sealed record LedgerItemRow(ulong Id, string Type, uint TemplateId, ulong ContainerId,
        int SlotType, int Slot, int Count, uint Owner, uint MadeUnitId, int Grade);
    private sealed record LedgerContainerRow(ulong ContainerId, string ContainerType, int SlotType, int Size, ulong OwnerId);
    private sealed record LedgerMailRow(int Id, int Type, int Status, string Title,
        int Money1, int Money2, int Money3, long[] Attachments);
    private sealed record LedgerSnapshot(long Money, long BankMoney, List<LedgerItemRow> Items,
        List<LedgerContainerRow> Containers, List<LedgerMailRow> Mails);
    private sealed record MetricsSample(double TickP95, double TickMax, double RegionMs,
        double SaveP95, double SaveMax, bool SaveAvail, long StepsRun, long StepsFailed,
        double SchedAvg, double SchedMax, bool SchedAvail);

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private readonly List<string> _failures = [];
    private readonly Dictionary<string, string> _scenarioStages = new();
    private readonly Dictionary<string, string> _scenarioCriteria = new();
    private readonly Dictionary<string, string> _scenarioEvidence = new();
    private void Check(bool cond, string msg) { if (!cond) _failures.Add(msg); }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task B4Slave_LifecycleSoak_TwoKill9_ExactlyOneRow_WithinBudgets()
    {
        AssertIsolatedLane();
        var startedAt = DateTime.UtcNow;
        var budgets = new GateBudgets();
        E2eStack.EnsureUp();

        var killedPid1 = 0;
        var killedPid2 = 0;
        var round1 = new Dictionary<string, string>();
        var round2 = new Dictionary<string, string>();
        List<BudgetVerdict> verdicts = [];
        var windowWallMin = 0.0;
        var steadyMin = 0.0;
        var warmupExcludedPhysics = 0L;
        var warmupExcludedOverruns = 0L;
        var bootTimes = new List<string>();
        var teardown = "not-run";

        try
        {
            // ---- round 1: summon → BindSlave board → load → drive → unboard ---------------
            foreach (var bot in Round1Bots)
                round1[bot] = RunScenario(bot, 420_000);
            foreach (var bot in Round1Bots)
                Check(round1[bot] == "PASS", $"round-1 {bot} m3a-m4-replay not PASS: {round1[bot]}");

            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
                CheckSaveAck(bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000), "pre-R1");

            var pre1Attach = new Dictionary<string, AttachmentSnapshot>();
            var pre1Ledger = new Dictionary<string, LedgerSnapshot>();
            foreach (var bot in Round1Bots)
            {
                var charId = ResolveCharacterId(bot);
                Check(charId > 0, $"round-1 bot '{bot}' has no characters row");
                if (charId == 0) continue;
                pre1Attach[bot] = SnapshotAttachment(charId);
                pre1Ledger[bot] = SnapshotLedger(charId);
                CheckSlaveSingleRow(pre1Attach[bot], charId, bot, "pre-R1");
                CheckAttachmentShape(pre1Attach[bot], charId, bot, "pre-R1");
                CheckLedgerSane(pre1Ledger[bot], bot, "pre-R1");
            }

            // ---- R1 (pre-window) ------------------------------------------------------------
            killedPid1 = E2eStack.RestartGameServer();
            Check(killedPid1 > 0, "R1 killed no game PID — the restart cannot be trusted");
            foreach (var bot in Round1Bots)
            {
                var charId = ResolveCharacterId(bot);
                Check(charId > 0, $"post-R1 bot '{bot}' has no characters row");
                if (charId == 0 || !pre1Attach.TryGetValue(bot, out var preA) || !pre1Ledger.TryGetValue(bot, out var preL)) continue;
                var postA = SnapshotAttachment(charId);
                CheckSlaveSingleRow(postA, charId, bot, "post-R1");
                CheckRestartIntact(preA, postA, charId, bot, "R1");
                CheckLedgerEqual(preL, SnapshotLedger(charId), bot, "R1");
            }

            // ---- measured window (long up-segment: NO restart inside) -----------------------
            var dbStart = ReadDbWriteCounters();
            var logOff = File.Exists(GateSoakRunner.GameLogPath) ? new FileInfo(GateSoakRunner.GameLogPath).Length : 0;
            var restartLogOff = File.Exists(GateSoakRunner.GameRestartLogPath) ? new FileInfo(GateSoakRunner.GameRestartLogPath).Length : 0;
            var windowStartUtc = DateTime.UtcNow;
            var windowStartLocal = DateTime.Now;
            var samples = new List<MetricsSample>();
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
                samples.Add(SampleOnce(bridge));

            // Round 2 = continuous load inside the window + live re-summon recovery proof.
            var tasks = Round2Bots.Select(bot => Task.Run(() => (bot, result: RunScenario(bot, 420_000)))).ToList();
            while (tasks.Any(t => !t.IsCompleted) ||
                   (DateTime.UtcNow - windowStartUtc).TotalMinutes < MinWindowMinutes)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try
                {
                    using var bridge = new BotDriveClient(E2eStack.BridgePort);
                    samples.Add(SampleOnce(bridge));
                }
                catch (Exception ex) { _failures.Add($"window sample failed: {ex.GetType().Name}: {ex.Message}"); break; }
                if ((DateTime.UtcNow - windowStartUtc).TotalMinutes > MinWindowMinutes + 25)
                { _failures.Add("window load overran +25 min past the minimum — aborting sample loop"); break; }
            }
            foreach (var t in tasks)
            {
                round2[t.Result.bot] = t.Result.result;
                Check(t.Result.result == "PASS", $"round-2 {t.Result.bot} m3a-m4-replay not PASS (re-summon recovery): {t.Result.result}");
            }

            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                CheckSaveAck(bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000), "window-end");
                samples.Add(SampleOnce(bridge));
            }
            var pre2Attach = new Dictionary<string, AttachmentSnapshot>();
            var pre2Ledger = new Dictionary<string, LedgerSnapshot>();
            foreach (var bot in Round2Bots)
            {
                var charId = ResolveCharacterId(bot);
                Check(charId > 0, $"round-2 bot '{bot}' has no characters row");
                if (charId == 0) continue;
                pre2Attach[bot] = SnapshotAttachment(charId);
                pre2Ledger[bot] = SnapshotLedger(charId);
                CheckSlaveSingleRow(pre2Attach[bot], charId, bot, "pre-R2");
                CheckAttachmentShape(pre2Attach[bot], charId, bot, "pre-R2");
                CheckLedgerSane(pre2Ledger[bot], bot, "pre-R2");
            }

            // ---- budget evaluation (warmup-blind, steady-state rates) -----------------------
            var windowEndLocal = DateTime.Now;
            windowWallMin = (DateTime.UtcNow - windowStartUtc).TotalMinutes;
            var dbEnd = ReadDbWriteCounters();
            var bootLocalTimes = E2eStack.GameBootTimesUtc.Select(b => b.ToLocalTime()).ToList();
            bootTimes = E2eStack.GameBootTimesUtc.Select(b => b.ToString("O")).ToList();
            var logTail = SoakLogScan.Scan(
                SoakLogScan.ReadWindowLines([(GateSoakRunner.GameLogPath, logOff), (GateSoakRunner.GameRestartLogPath, restartLogOff)]),
                windowStartLocal, bootLocalTimes);
            steadyMin = SoakWarmup.SteadyStateMinutes(windowStartLocal, windowEndLocal, bootLocalTimes);
            warmupExcludedPhysics = logTail.WarmupExcludedPhysics;
            warmupExcludedOverruns = logTail.WarmupExcludedOverruns;
            Check(steadyMin >= MinSteadyMinutes,
                $"steady-state {steadyMin:F1} min < {MinSteadyMinutes} min — window has no evaluable steady state");
            var evalMin = Math.Max(steadyMin > 0 ? steadyMin : windowWallMin, 0.01);
            var last = samples[^1];
            var stepsDelta = last.StepsRun - samples[0].StepsRun;
            var failedDelta = Math.Max(0, last.StepsFailed - samples[0].StepsFailed);
            var regionWorst = samples.Where(s => s.RegionMs >= 0).Select(s => s.RegionMs).DefaultIfEmpty(-1).Max();
            if (logTail.RegionTickWorstMs >= 0) regionWorst = Math.Max(regionWorst, logTail.RegionTickWorstMs);

            string WarmNote(long n) => n > 0 ? $" (+{n} warmup-blind excluded, not counted)" : "";
            if (last.TickP95 >= 0)
            {
                verdicts.Add(last.TickP95 <= budgets.TickP95Ms
                    ? BudgetVerdict.Ok("TickManager invoke p95", last.TickP95, budgets.TickP95Ms, "ms")
                    : BudgetVerdict.Over("TickManager invoke p95", last.TickP95, budgets.TickP95Ms, "ms"));
                verdicts.Add(last.TickMax <= budgets.TickMaxMs
                    ? BudgetVerdict.Ok("TickManager invoke max", last.TickMax, budgets.TickMaxMs, "ms")
                    : BudgetVerdict.Over("TickManager invoke max", last.TickMax, budgets.TickMaxMs, "ms"));
            }
            else verdicts.Add(BudgetVerdict.Over("TickManager invoke", -1, budgets.TickP95Ms, "H2 tick metrics unavailable"));
            verdicts.Add(regionWorst >= 0 && regionWorst <= budgets.RegionTickMaxElapsedMs
                ? BudgetVerdict.Ok("ActiveRegionTick worst pass", regionWorst, budgets.RegionTickMaxElapsedMs, "ms")
                : BudgetVerdict.Over("ActiveRegionTick worst pass", regionWorst, budgets.RegionTickMaxElapsedMs, "ms"));
            verdicts.Add(logTail.RegionOverruns <= budgets.MaxRegionTickOverruns
                ? BudgetVerdict.Ok("ActiveRegionTick overruns", logTail.RegionOverruns, budgets.MaxRegionTickOverruns, "passes")
                : BudgetVerdict.Over("ActiveRegionTick overruns", logTail.RegionOverruns, budgets.MaxRegionTickOverruns, "passes"));
            if (stepsDelta > 0)
            {
                verdicts.Add(last.SchedAvg <= budgets.SchedulerAvgWakeLatencyMs
                    ? BudgetVerdict.Ok("Scheduler avg wake latency", last.SchedAvg, budgets.SchedulerAvgWakeLatencyMs, "ms")
                    : BudgetVerdict.Over("Scheduler avg wake latency", last.SchedAvg, budgets.SchedulerAvgWakeLatencyMs, "ms"));
                verdicts.Add(last.SchedMax <= budgets.SchedulerMaxWakeLatencyMs
                    ? BudgetVerdict.Ok("Scheduler max wake latency", last.SchedMax, budgets.SchedulerMaxWakeLatencyMs, "ms")
                    : BudgetVerdict.Over("Scheduler max wake latency", last.SchedMax, budgets.SchedulerMaxWakeLatencyMs, "ms"));
                verdicts.Add(failedDelta <= budgets.MaxSchedulerStepFailures
                    ? BudgetVerdict.Ok("Scheduler step failures", failedDelta, budgets.MaxSchedulerStepFailures, "steps")
                    : BudgetVerdict.Over("Scheduler step failures", failedDelta, budgets.MaxSchedulerStepFailures, "steps"));
            }
            else
            {
                verdicts.Add(BudgetVerdict.Nx("Scheduler avg wake latency", 0, budgets.SchedulerAvgWakeLatencyMs, "n/a — no scheduler steps ran (bridge-driven bots never Wake)"));
                verdicts.Add(BudgetVerdict.Nx("Scheduler max wake latency", 0, budgets.SchedulerMaxWakeLatencyMs, "n/a — no scheduler steps ran"));
                verdicts.Add(BudgetVerdict.Nx("Scheduler step failures", 0, budgets.MaxSchedulerStepFailures, "n/a — no scheduler steps ran"));
            }
            var dbPerCharMin = Math.Max(0, dbEnd - dbStart) / evalMin / EmbodiedBots;
            verdicts.Add(dbPerCharMin <= budgets.MaxDbWritesPerBotPerMin
                ? BudgetVerdict.Ok("DB writes", dbPerCharMin, budgets.MaxDbWritesPerBotPerMin, $"writes/min/embodied-char (÷{EmbodiedBots} run bots)")
                : BudgetVerdict.Over("DB writes", dbPerCharMin, budgets.MaxDbWritesPerBotPerMin, $"writes/min/embodied-char (÷{EmbodiedBots} run bots)"));
            var physPerMin = logTail.PhysicsWarnings / evalMin;
            verdicts.Add(physPerMin <= budgets.MaxPhysicsWarningsPerMin
                ? BudgetVerdict.Ok("Physics warnings", physPerMin, budgets.MaxPhysicsWarningsPerMin, $"warnings/min{WarmNote(logTail.WarmupExcludedPhysics)}")
                : BudgetVerdict.Over("Physics warnings", physPerMin, budgets.MaxPhysicsWarningsPerMin, $"warnings/min{WarmNote(logTail.WarmupExcludedPhysics)}"));
            verdicts.Add(logTail.MaxSameWorldPhysicsWarningsPer60s <= budgets.MaxPhysicsWarningsSameWorldPer60s
                ? BudgetVerdict.Ok("Physics warnings same-world", logTail.MaxSameWorldPhysicsWarningsPer60s, budgets.MaxPhysicsWarningsSameWorldPer60s, $"max/60s{WarmNote(logTail.WarmupExcludedPhysics)}")
                : BudgetVerdict.Over("Physics warnings same-world", logTail.MaxSameWorldPhysicsWarningsPer60s, budgets.MaxPhysicsWarningsSameWorldPer60s, $"max/60s{WarmNote(logTail.WarmupExcludedPhysics)}"));
            var overPerMin = logTail.TickOverrunWarnings / evalMin;
            verdicts.Add(overPerMin <= budgets.MaxTickOverrunWarningsPerMin
                ? BudgetVerdict.Ok("Tick overrun warnings", overPerMin, budgets.MaxTickOverrunWarningsPerMin, $"warnings/min{WarmNote(logTail.WarmupExcludedOverruns)}")
                : BudgetVerdict.Over("Tick overrun warnings", overPerMin, budgets.MaxTickOverrunWarningsPerMin, $"warnings/min{WarmNote(logTail.WarmupExcludedOverruns)}"));
            if (last.SaveAvail)
            {
                verdicts.Add(last.SaveP95 <= budgets.AutosaveP95Ms
                    ? BudgetVerdict.Ok("Autosave p95", last.SaveP95, budgets.AutosaveP95Ms, "ms")
                    : BudgetVerdict.Over("Autosave p95", last.SaveP95, budgets.AutosaveP95Ms, "ms"));
                verdicts.Add(last.SaveMax <= budgets.AutosaveMaxMs
                    ? BudgetVerdict.Ok("Autosave max", last.SaveMax, budgets.AutosaveMaxMs, "ms")
                    : BudgetVerdict.Over("Autosave max", last.SaveMax, budgets.AutosaveMaxMs, "ms"));
            }
            else
            {
                verdicts.Add(BudgetVerdict.Nx("Autosave p95", 0, budgets.AutosaveP95Ms, "n/a — save metrics unavailable"));
                verdicts.Add(BudgetVerdict.Nx("Autosave max", 0, budgets.AutosaveMaxMs, "n/a — save metrics unavailable"));
            }
            foreach (var v in verdicts.Where(v => !v.Passed))
                _failures.Add($"BUDGET RED: {v.Name}: {v.Detail} (measured {v.Measured} / limit {v.Limit})");

            // Preserve the round-2 (concurrent) game log BEFORE R2 truncates
            // game-restart.log — round-leg interleaving forensics depends on it.
            try { File.Copy(GateSoakRunner.GameRestartLogPath, Path.Combine(E2eStack.E2eRoot, "logs", "game-round2.log"), overwrite: true); } catch (Exception ex) { Console.WriteLine($"[b4slave] round-2 log preserve failed (non-fatal): {ex.Message}"); }
            // ---- R2 (post-window): second restart contract -------------------------------------
            killedPid2 = E2eStack.RestartGameServer();
            Check(killedPid2 > 0, "R2 killed no game PID — the restart cannot be trusted");
            foreach (var bot in Round2Bots)
            {
                var charId = ResolveCharacterId(bot);
                Check(charId > 0, $"post-R2 bot '{bot}' has no characters row");
                if (charId == 0 || !pre2Attach.TryGetValue(bot, out var preA) || !pre2Ledger.TryGetValue(bot, out var preL)) continue;
                var postA = SnapshotAttachment(charId);
                CheckSlaveSingleRow(postA, charId, bot, "post-R2");
                CheckRestartIntact(preA, postA, charId, bot, "R2");
                CheckLedgerEqual(preL, SnapshotLedger(charId), bot, "R2");
            }
        }
        catch (Exception ex)
        {
            _failures.Add($"run threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await CleanupAsync(Round1Bots.Concat(Round2Bots).ToArray()); }
            catch (Exception ex) { Console.WriteLine($"[b4slave] cleanup failed (non-fatal): {ex.Message}"); }
        }

        teardown = VerifyTeardown();
        await WriteReportAsync(startedAt, budgets, killedPid1, killedPid2, round1, round2, verdicts,
            windowWallMin, steadyMin, warmupExcludedPhysics, warmupExcludedOverruns, bootTimes, teardown);
        Assert.True(_failures.Count == 0,
            $"B4 SLAVE S-run RED ({_failures.Count}):\n- {string.Join("\n- ", _failures)}");
    }

    private static void AssertIsolatedLane()
    {
        Assert.False(string.Equals(E2eStack.E2eRoot, "/root/aaemu-e2e", StringComparison.Ordinal),
            "B4 SLAVE S-run refuses the shared default E2E_ROOT (isolated lane required)");
        Assert.NotEqual(3306, E2eStack.DbPort);
        Assert.Equal("127.0.0.1", E2eStack.GameHost);
        Assert.NotEqual("e2e", E2eStack.ComposeProject);
    }

    private string RunScenario(string bot, int timeoutMs)
    {
        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            var resp = bridge.Call(
                $"{{\"cmd\":\"scenario\",\"template\":\"{ReplayTemplate}\",\"bot\":\"{bot}\",\"fresh\":true}}",
                timeoutMs: timeoutMs);
            var passed = resp.TryGetProperty("passed", out var p) && p.GetBoolean();
            var stage = resp.TryGetProperty("failStage", out var fs) ? fs.GetString() ?? "" : "";
            var reason = resp.TryGetProperty("failReason", out var fr) ? fr.GetString() ?? "" : "";
            _scenarioStages[bot] = resp.TryGetProperty("stages", out var st)
                ? string.Join(",", st.EnumerateArray().Select(s => s.TryGetProperty("Stage", out var sn) ? sn.GetString() ?? "?" : "?"))
                : "(no stages)";
            _scenarioCriteria[bot] = resp.TryGetProperty("criteria", out var cr)
                ? string.Join(",", cr.EnumerateArray().Select(c => $"{(c.TryGetProperty("Name", out var cn) ? cn.GetString() : "?")}={(c.TryGetProperty("Passed", out var cp) && cp.GetBoolean() ? "1" : "0")}"))
                : "(no criteria)";
            var ev = resp.TryGetProperty("evidence", out var evEl) ? evEl.GetString() ?? "" : "";
            _scenarioEvidence[bot] = ev.Length > 2500 ? ev[^2500..] + "…(head-truncated; VERIFY detail lives at the tail)" : ev;
            Console.WriteLine($"[b4slave] scenario {ReplayTemplate} bot {bot}: {(passed ? "PASS" : $"FAIL @{stage}: {reason}")} stages={_scenarioStages[bot]}");
            return passed ? "PASS" : $"FAIL @{stage}: {reason}";
        }
        catch (Exception ex) { return $"ERROR {ex.GetType().Name}: {ex.Message}"; }
    }

    private void CheckSaveAck(JsonElement ack, string phase)
    {
        Check(ack.TryGetProperty("saved", out var s) && s.GetBoolean(),
            $"[{phase}] bridge save pass did not complete before snapshot");
    }

    private static uint ResolveCharacterId(string botName)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM characters WHERE LOWER(name) = @name LIMIT 1";
        cmd.Parameters.AddWithValue("@name", char.ToUpperInvariant(botName[0]) + botName[1..].ToLowerInvariant());
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? 0u : Convert.ToUInt32(raw);
    }

    private static AttachmentSnapshot SnapshotAttachment(uint charId)
    {
        var slaves = new List<SlaveSnapshot>();
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, item_id, template_id, attach_point, name, owner_type, owner_id, summoner, hp, mp, x, y, z " +
                    "FROM slaves WHERE summoner = @charId ORDER BY id";
                cmd.Parameters.AddWithValue("@charId", charId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    slaves.Add(new SlaveSnapshot(
                        reader.GetUInt32("id"), reader.GetUInt64("item_id"), reader.GetUInt32("template_id"),
                        reader.GetInt32("attach_point"), reader.GetString("name"),
                        reader.GetUInt32("owner_type"), reader.GetUInt32("owner_id"), reader.GetUInt32("summoner"),
                        reader.GetInt32("hp"), reader.GetInt32("mp"),
                        reader.GetFloat("x"), reader.GetFloat("y"), reader.GetFloat("z")));
            }
            if (slaves.Count == 0)
                return new AttachmentSnapshot(null, 0, [], [], []);
            var bindings = new List<BindingSnapshot>();
            foreach (var slave in slaves)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, owner_id, owner_type, attach_point, template_id, current_phase_id, plant_time, " +
                    "item_id, house_id, data, x, y, z, item_template_id FROM doodads " +
                    "WHERE owner_type = 2 AND house_id = @slaveId ORDER BY id";
                cmd.Parameters.AddWithValue("@slaveId", slave.Id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    bindings.Add(new BindingSnapshot(
                        reader.GetUInt32("id"), reader.GetUInt32("owner_id"), reader.GetUInt32("owner_type"),
                        reader.GetUInt32("attach_point"), reader.GetUInt32("template_id"),
                        reader.GetUInt32("current_phase_id"), reader.GetDateTime("plant_time"),
                        reader.GetUInt64("item_id"), reader.GetUInt32("house_id"), reader.GetInt32("data"),
                        reader.GetFloat("x"), reader.GetFloat("y"), reader.GetFloat("z"),
                        reader.GetUInt32("item_template_id")));
            }
            var items = new Dictionary<ulong, ItemSnapshot>();
            var containerOwners = new Dictionary<ulong, ulong>();
            foreach (var binding in bindings)
            {
                if (binding.ItemId == 0 || items.ContainsKey(binding.ItemId)) continue;
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, type, template_id, container_id, slot_type, slot, count, owner, made_unit_id " +
                    "FROM items WHERE id = @itemId";
                cmd.Parameters.AddWithValue("@itemId", binding.ItemId);
                ulong containerId;
                ItemSnapshot itemRow;
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) continue;
                    itemRow = new ItemSnapshot(
                        reader.GetUInt64("id"), reader.GetString("type"), reader.GetUInt32("template_id"),
                        reader.GetUInt64("container_id"), reader.GetInt32("slot_type"), reader.GetInt32("slot"),
                        reader.GetInt32("count"), reader.GetUInt32("owner"), reader.GetUInt32("made_unit_id"));
                    containerId = itemRow.ContainerId;
                }
                items[binding.ItemId] = itemRow;
                if (containerId > 0 && !containerOwners.ContainsKey(containerId))
                {
                    using var ccmd = conn.CreateCommand();
                    ccmd.CommandText = "SELECT owner_id FROM item_containers WHERE container_id = @cid";
                    ccmd.Parameters.AddWithValue("@cid", containerId);
                    var rawOwner = ccmd.ExecuteScalar();
                    containerOwners[containerId] = rawOwner == null || rawOwner is DBNull ? 0u : Convert.ToUInt64(rawOwner);
                }
            }
            return new AttachmentSnapshot(slaves[0], slaves.Count, bindings, items, containerOwners);
        }
    }

    private static LedgerSnapshot SnapshotLedger(uint charId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        long money, bankMoney;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT money, money2 FROM characters WHERE id = @charId";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException($"characters row {charId} vanished mid-snapshot");
            money = reader.GetInt64("money");
            bankMoney = reader.GetInt64("money2");
        }
        var items = new List<LedgerItemRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, type, template_id, container_id, slot_type, slot, count, owner, made_unit_id, grade " +
                "FROM items WHERE owner = @charId ORDER BY id";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                items.Add(new LedgerItemRow(
                    reader.GetUInt64("id"), reader.GetString("type"), reader.GetUInt32("template_id"),
                    reader.GetUInt64("container_id"), reader.GetInt32("slot_type"), reader.GetInt32("slot"),
                    reader.GetInt32("count"), reader.GetUInt32("owner"), reader.GetUInt32("made_unit_id"),
                    reader.GetInt32("grade")));
        }
        var containers = new List<LedgerContainerRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT container_id, container_type, slot_type, container_size, owner_id " +
                "FROM item_containers WHERE owner_id = @charId ORDER BY container_id";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                containers.Add(new LedgerContainerRow(
                    reader.GetUInt64("container_id"), reader.GetString("container_type"),
                    reader.GetInt32("slot_type"), reader.GetInt32("container_size"),
                    reader.GetUInt64("owner_id")));
        }
        var mails = new List<LedgerMailRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, type, status, title, money_amount_1, money_amount_2, money_amount_3, " +
                "attachment0, attachment1, attachment2, attachment3, attachment4, " +
                "attachment5, attachment6, attachment7, attachment8, attachment9 " +
                "FROM mails WHERE receiver_id = @charId ORDER BY id";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                mails.Add(new LedgerMailRow(
                    reader.GetInt32("id"), reader.GetInt32("type"), reader.GetInt32("status"),
                    reader.GetString("title"),
                    reader.GetInt32("money_amount_1"), reader.GetInt32("money_amount_2"),
                    reader.GetInt32("money_amount_3"),
                    [reader.GetInt64("attachment0"), reader.GetInt64("attachment1"),
                     reader.GetInt64("attachment2"), reader.GetInt64("attachment3"),
                     reader.GetInt64("attachment4"), reader.GetInt64("attachment5"),
                     reader.GetInt64("attachment6"), reader.GetInt64("attachment7"),
                     reader.GetInt64("attachment8"), reader.GetInt64("attachment9")]));
        }
        return new LedgerSnapshot(money, bankMoney, items, containers, mails);
    }

    /// <summary>M4Vehicles contract: exactly ONE slaves row per summoner — no dup, no loss.</summary>
    private void CheckSlaveSingleRow(AttachmentSnapshot snap, uint charId, string bot, string phase)
    {
        Check(snap.SlaveRowCount == 1,
            $"[{phase}] {bot}: slaves rows for summoner {charId} = {snap.SlaveRowCount}, expected exactly 1 (dup/loss)");
    }

    private void CheckAttachmentShape(AttachmentSnapshot snap, uint charId, string bot, string phase)
    {
        Check(snap.Slave != null, $"[{phase}] {bot}: no slaves row for summoner {charId}");
        if (snap.Slave == null) return;
        var slave = snap.Slave;
        Check(slave.TemplateId == FarmWagonSlaveTemplateId, $"[{phase}] {bot}: slave template {slave.TemplateId} != wagon 60");
        Check(slave.OwnerType == 0u && slave.OwnerId == charId && slave.Summoner == charId,
            $"[{phase}] {bot}: slave ownership link broken");
        Check(slave.ItemId > 0, $"[{phase}] {bot}: wagon slave row lost its summoning-item link (re-summon recovery broken)");
        var packRows = snap.Bindings.Where(b => b.ItemId > 0).ToList();
        Check(packRows.Count == 1, $"[{phase}] {bot}: expected exactly ONE pack binding, saw {packRows.Count}");
        if (packRows.Count == 1)
        {
            var binding = packRows[0];
            Check(binding.OwnerType == 2u && binding.HouseId == slave.Id, $"[{phase}] {bot}: binding not keyed to this slave");
            Check(CargoAttachPoints.Contains(binding.AttachPoint), $"[{phase}] {bot}: attach point {binding.AttachPoint} outside wagon capacity");
            Check(binding.TemplateId == PlacedPackDoodadTemplateId && binding.CurrentPhaseId == PlacedPackStartPhaseId,
                $"[{phase}] {bot}: placed-pack {binding.TemplateId}/{binding.CurrentPhaseId} != 6068/15677");
            Check(binding.ItemTemplateId == PackItemTemplateId, $"[{phase}] {bot}: pack item template {binding.ItemTemplateId} != 26488");
            Check(snap.Items.TryGetValue(binding.ItemId, out var item) && item.TemplateId == PackItemTemplateId && item.Count == 1,
                $"[{phase}] {bot}: pack item row missing/wrong");
            if (snap.Items.TryGetValue(binding.ItemId, out var item2))
                Check(item2.SlotType == SlotTypeSystem, $"[{phase}] {bot}: pack not in slave System container");
        }
    }

    private void CheckLedgerSane(LedgerSnapshot snap, string bot, string phase)
    {
        Check(snap.Money >= 0, $"[{phase}] {bot}: negative inventory copper");
        Check(snap.BankMoney >= 0, $"[{phase}] {bot}: negative bank copper");
        Check(snap.Items.Count > 0, $"[{phase}] {bot}: owns no item rows at all");
        Check(snap.Containers.Count > 0, $"[{phase}] {bot}: owns no container rows at all");
    }

    private void CheckRestartIntact(AttachmentSnapshot pre, AttachmentSnapshot post, uint charId, string bot, string tag)
    {
        CheckSlaveSingleRow(post, charId, bot, $"post-{tag}");
        CheckAttachmentShape(post, charId, bot, $"post-{tag}");
        if (pre.Slave == null || post.Slave == null) return;
        var a = pre.Slave;
        var b = post.Slave;
        Check(a.Id == b.Id, $"[{tag}] {bot}: slaves row id changed {a.Id} → {b.Id} (dup/respawn)");
        Check(a.ItemId == b.ItemId, $"[{tag}] {bot}: summoning-item link changed (recovery path broken)");
        Check(a.AttachPoint == b.AttachPoint, $"[{tag}] {bot}: engine-authored attach point changed");
        Check(a.Hp == b.Hp && a.Mp == b.Mp, $"[{tag}] {bot}: HP/MP changed {a.Hp}/{a.Mp} → {b.Hp}/{b.Mp}");
        Check(MathF.Abs(a.X - b.X) < 0.01f && MathF.Abs(a.Y - b.Y) < 0.01f && MathF.Abs(a.Z - b.Z) < 0.01f,
            $"[{tag}] {bot}: slave position moved across restart");
        Check(pre.Bindings.Count == post.Bindings.Count,
            $"[{tag}] {bot}: binding count {pre.Bindings.Count} → {post.Bindings.Count}");
        foreach (var pb in pre.Bindings)
        {
            var qb = post.Bindings.SingleOrDefault(x => x.DoodadDbId == pb.DoodadDbId);
            Check(qb != null, $"[{tag}] {bot}: binding doodad {pb.DoodadDbId} vanished over restart");
            if (qb == null) continue;
            Check(qb.AttachPoint == pb.AttachPoint && qb.HouseId == pb.HouseId && qb.ItemId == pb.ItemId,
                $"[{tag}] {bot}: binding {pb.DoodadDbId} link changed over restart");
            Check(Math.Abs((qb.PlantTime - pb.PlantTime).TotalSeconds) < 2,
                $"[{tag}] {bot}: plant_time clobbered over restart");
            Check(MathF.Abs(qb.X - pb.X) < 0.001f && MathF.Abs(qb.Y - pb.Y) < 0.001f && MathF.Abs(qb.Z - pb.Z) < 0.001f,
                $"[{tag}] {bot}: local cargo transform clobbered over restart");
        }
        foreach (var (itemId, pi) in pre.Items)
        {
            Check(post.Items.TryGetValue(itemId, out var qi), $"[{tag}] {bot}: pack item {itemId} vanished over restart");
            if (post.Items.TryGetValue(itemId, out var qi2))
                Check(qi2.TemplateId == pi.TemplateId && qi2.ContainerId == pi.ContainerId && qi2.Count == pi.Count &&
                      qi2.Owner == pi.Owner && qi2.MadeUnitId == pi.MadeUnitId,
                    $"[{tag}] {bot}: pack item {itemId} row changed over restart");
        }
    }

    private void CheckLedgerEqual(LedgerSnapshot pre, LedgerSnapshot post, string bot, string tag)
    {
        Check(pre.Money == post.Money, $"[{tag}] {bot}: inventory copper {pre.Money} → {post.Money} (dup/loss)");
        Check(pre.BankMoney == post.BankMoney, $"[{tag}] {bot}: bank copper {pre.BankMoney} → {post.BankMoney} (dup/loss)");
        Check(pre.Items.Count == post.Items.Count, $"[{tag}] {bot}: item row count {pre.Items.Count} → {post.Items.Count}");
        foreach (var pi in pre.Items)
        {
            var qi = post.Items.SingleOrDefault(i => i.Id == pi.Id);
            Check(qi != null, $"[{tag}] {bot}: ledger item {pi.Id} vanished over restart");
            if (qi != null)
                Check(qi.TemplateId == pi.TemplateId && qi.ContainerId == pi.ContainerId && qi.Count == pi.Count &&
                      qi.Owner == pi.Owner && qi.MadeUnitId == pi.MadeUnitId && qi.Grade == pi.Grade,
                    $"[{tag}] {bot}: ledger item {pi.Id} changed over restart");
        }
        Check(pre.Containers.Count == post.Containers.Count, $"[{tag}] {bot}: container count changed over restart");
        Check(pre.Mails.Count == post.Mails.Count, $"[{tag}] {bot}: mail count {pre.Mails.Count} → {post.Mails.Count}");
        var preCopper = pre.Money + pre.BankMoney + pre.Mails.Sum(m => (long)m.Money1 + m.Money2 + m.Money3);
        var postCopper = post.Money + post.BankMoney + post.Mails.Sum(m => (long)m.Money1 + m.Money2 + m.Money3);
        Check(preCopper == postCopper, $"[{tag}] {bot}: total copper {preCopper} → {postCopper} (restart created/destroyed money)");
    }

    private static long ReadDbWriteCounters()
    {
        long total = 0;
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SHOW GLOBAL STATUS WHERE Variable_name IN ('Com_insert','Com_update','Com_delete','Com_replace')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) total += reader.GetInt64(1);
        }
        catch { }
        return total;
    }

    private static MetricsSample SampleOnce(BotDriveClient bridge)
    {
        var json = bridge.Call("{\"cmd\":\"metrics\"}", timeoutMs: 30_000);
        var tickP95 = -1.0; var tickMax = -1.0; var regionMs = -1.0;
        var saveP95 = -1.0; var saveMax = -1.0; var saveAvail = false;
        long stepsRun = 0, stepsFailed = 0;
        var schedAvg = -1.0; var schedMax = -1.0; var schedAvail = false;
        if (json.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
            tk.TryGetProperty("available", out var ta) && ta.GetBoolean())
        {
            if (tk.TryGetProperty("invokeP95Ms", out var p)) tickP95 = p.GetDouble();
            if (tk.TryGetProperty("invokeMaxMs", out var m)) tickMax = m.GetDouble();
        }
        if (json.TryGetProperty("regionTick", out var rt) && rt.ValueKind == JsonValueKind.Object &&
            rt.TryGetProperty("available", out var ra) && ra.GetBoolean() &&
            rt.TryGetProperty("elapsedMs", out var el)) regionMs = el.GetDouble();
        if (json.TryGetProperty("save", out var sv) && sv.ValueKind == JsonValueKind.Object &&
            sv.TryGetProperty("available", out var sa) && sa.GetBoolean())
        {
            saveAvail = true;
            if (sv.TryGetProperty("p95Ms", out var p)) saveP95 = p.GetDouble();
            if (sv.TryGetProperty("maxMs", out var m)) saveMax = m.GetDouble();
        }
        if (json.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
            sc.TryGetProperty("available", out var sca) && sca.GetBoolean())
        {
            schedAvail = true;
            if (sc.TryGetProperty("totalStepsRun", out var r)) stepsRun = r.GetInt64();
            if (sc.TryGetProperty("totalStepsFailed", out var f)) stepsFailed = f.GetInt64();
            if (sc.TryGetProperty("avgWakeLatencyMs", out var a)) schedAvg = a.GetDouble();
            if (sc.TryGetProperty("maxWakeLatencyMs", out var m)) schedMax = m.GetDouble();
        }
        return new MetricsSample(tickP95, tickMax, regionMs, saveP95, saveMax, saveAvail,
            stepsRun, stepsFailed, schedAvg, schedMax, schedAvail);
    }

    private static string VerifyTeardown()
    {
        try { E2eStack.StopAll(); } catch { }
        var closed = new List<string>();
        foreach (var (name, port) in new[] { ("login", E2eStack.LoginPort), ("game", E2eStack.GamePort), ("bridge", E2eStack.BridgePort) })
        {
            var shut = false;
            try { using var c = new TcpClient(); c.Connect("127.0.0.1", port); }
            catch { shut = true; }
            closed.Add($"{name}:{port}={(shut ? "closed" : "STILL-OPEN")}");
        }
        return $"StopAll PID-handle teardown; {string.Join(" ", closed)} (no pkill used)";
    }

    private async Task WriteReportAsync(DateTime startedAt, GateBudgets budgets, int killedPid1, int killedPid2,
        Dictionary<string, string> round1, Dictionary<string, string> round2, List<BudgetVerdict> verdicts,
        double wallMin, double steadyMin, long exclPhys, long exclOver, List<string> boots, string teardown)
    {
        Directory.CreateDirectory(EvidenceDir);
        var verdictRows = verdicts.Select(v => new
        {
            metric = v.Name, measured = v.Measured, limit = v.Limit,
            verdict = v.NotApplicable ? "N/A" : v.Passed ? "PASS" : "FAIL", detail = v.Detail
        }).ToList();
        var report = new
        {
            slice = "B4 SLAVE-01 S run — budgeted vehicle-lifecycle soak (summon→BindSlave→load→drive→unboard→restart→re-summon)",
            card = "PACK-01/SLAVE-01 S runs need STATED budgets (actors × duration × restarts) with measured results",
            stated_budget = new
            {
                actors = "2 round-1 (b4slave01/02 m3a-m4-replay) + 2 round-2 in-window (b4slave03/04 m3a-m4-replay, live re-summon recovery proof)",
                loops = "2 summon→board→load→drive→unboard cycles pre-restart + 2 cycles under measurement",
                duration = $"window ≥ {MinWindowMinutes} min wall, ≥ {MinSteadyMinutes} min steady-state; measured {wallMin:F1} wall / {steadyMin:F1} steady",
                restarts = $"2 × kill -9 game-only (PID-handle; R1 pre-window, R2 post-window); killedPids=[{killedPid1},{killedPid2}]",
                thresholds = $"tick p95≤{budgets.TickP95Ms} max≤{budgets.TickMaxMs}; region worst≤{budgets.RegionTickMaxElapsedMs} overruns={budgets.MaxRegionTickOverruns}; " +
                             $"sched avg≤{budgets.SchedulerAvgWakeLatencyMs} max≤{budgets.SchedulerMaxWakeLatencyMs} fails={budgets.MaxSchedulerStepFailures}; " +
                             $"DB≤{budgets.MaxDbWritesPerBotPerMin}/min/char (÷{EmbodiedBots}); phys≤{budgets.MaxPhysicsWarningsPerMin}/min same-world≤{budgets.MaxPhysicsWarningsSameWorldPer60s}/60s; " +
                             $"tick-overrun≤{budgets.MaxTickOverrunWarningsPerMin}/min; autosave p95≤{budgets.AutosaveP95Ms} max≤{budgets.AutosaveMaxMs}",
                warmup = "[boot−30s, boot+120s] recorded-not-counted; " +
                         $"excluded {exclPhys} physics / {exclOver} tick-overrun; boots: {string.Join(",", boots)}"
            },
            lane = E2eStack.E2eRoot,
            compose_project = E2eStack.ComposeProject,
            scenario_stages = _scenarioStages,
            scenario_criteria = _scenarioCriteria,
            scenario_evidence = _scenarioEvidence,
            db_port = E2eStack.DbPort,
            game_host = E2eStack.GameHost,
            source_revision = E2eStack.SourceRevision,
            verdict = _failures.Count == 0 ? "PASS" : "FAIL",
            failures = _failures.ToArray(),
            round1, round2,
            budgets = verdictRows,
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            teardown,
            anchors = "SlaveLifecycleTests 29/29 (despawn gates owner/312/288/801, BindSlave-324, RidersEscape-640, ghost-free recovery); M4VehiclesE2eTests two-restart contract; GateBudgetEvaluator defaults; SoakWarmup blinds",
            proxy_note = "scripted-actor / bot-functional evidence — H (feel) stays UNKNOWN"
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "b4-slave-soak-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var md = new System.Text.StringBuilder();
        md.AppendLine("# B4 SLAVE-01 S run — budgeted vehicle-lifecycle soak");
        md.AppendLine();
        md.AppendLine($"- SHA: {E2eStack.SourceRevision} · lane: {E2eStack.E2eRoot} · compose: {E2eStack.ComposeProject} · DB: {E2eStack.DbPort}");
        md.AppendLine($"- Verdict: {(report.verdict)} · elapsed: {report.elapsed_seconds:F0}s · killedPids: [{killedPid1},{killedPid2}]");
        md.AppendLine($"- Window: {wallMin:F1} min wall / {steadyMin:F1} min steady · boots: {string.Join(",", boots)}");
        md.AppendLine($"- Round 1: {string.Join(", ", round1.Select(kv => $"{kv.Key}={kv.Value}"))}");
        md.AppendLine($"- Round 2: {string.Join(", ", round2.Select(kv => $"{kv.Key}={kv.Value}"))}");
        md.AppendLine();
        md.AppendLine("| Metric | Measured | Limit | Verdict |");
        md.AppendLine("|---|---|---|---|");
        foreach (var v in verdicts)
            md.AppendLine($"| {v.Name} | {v.Measured:F2} | {v.Limit:F2} | {(v.NotApplicable ? "N/A" : v.Passed ? "PASS" : "FAIL")} |");
        md.AppendLine();
        md.AppendLine($"- Teardown: {teardown}");
        if (_failures.Count > 0) { md.AppendLine("- Failures:"); foreach (var f in _failures) md.AppendLine($"  - {f}"); }
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "b4-slave-soak-report.md"), md.ToString());
    }

    private static async Task CleanupAsync(string[] bots)
    {
        try
        {
            foreach (var bot in bots)
            {
                var charId = ResolveCharacterId(bot);
                if (charId > 0)
                {
                    using var conn = E2eStack.OpenDb("aaemu_game");
                    foreach (var sql in new[]
                             {
                                 "DELETE FROM doodads WHERE owner_id = @charId OR house_id IN (SELECT id FROM slaves WHERE summoner = @charId)",
                                 "DELETE FROM items WHERE owner = @charId",
                                 "DELETE FROM item_containers WHERE owner_id = @charId",
                                 "DELETE FROM slaves WHERE summoner = @charId OR owner_id = @charId"
                             })
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = sql;
                        cmd.Parameters.AddWithValue("@charId", charId);
                        try { await cmd.ExecuteNonQueryAsync(); } catch { }
                    }
                }
                var username = "bot_managed_" + bot;
                using var conn2 = E2eStack.OpenDb("aaemu_game");
                foreach (var sql in new[]
                         {
                             "DELETE FROM quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                             "DELETE FROM completed_quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                             "DELETE FROM playerbot_metadata WHERE character_id IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                             "DELETE FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username)"
                         })
                {
                    using var cmd2 = conn2.CreateCommand();
                    cmd2.CommandText = sql;
                    cmd2.Parameters.AddWithValue("@username", username);
                    try { await cmd2.ExecuteNonQueryAsync(); } catch { }
                }
                using var loginConn = E2eStack.OpenDb("aaemu_login");
                using var delUser = loginConn.CreateCommand();
                delUser.CommandText = "DELETE FROM users WHERE username = @username";
                delUser.Parameters.AddWithValue("@username", username);
                try { await delUser.ExecuteNonQueryAsync(); } catch { }
            }
        }
        catch (Exception e) { Console.WriteLine($"[b4slave] cleanup failed (non-fatal): {e.Message}"); }
    }
}
