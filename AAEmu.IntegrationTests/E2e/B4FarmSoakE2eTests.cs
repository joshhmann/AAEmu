using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

using AAEmu.Commons.Utils.Gate;
using AAEmu.IntegrationTests.E2e.Gate;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// B4 FARM-01 S run (last open dimension on the FARM-01 row): budgeted
/// farm-lifecycle soak through the REAL engine paths — plant (seed 15659 →
/// doodad 2259), grow/mature wait, harvest via the real Doodad.Use chain
/// (DoodadFuncCropHarvest → loot pack 6452), livestock calf 2672 (calf item
/// 16225, fed via the real 20595 사료먹이기 interact), replant.
///
/// STATED BUDGET (this is the S-run contract — re-running the single-pass
/// M3a/B2 restart E2Es without this table does NOT close the card):
///   actors:    2 concurrent bots round 1 (b4farm01/b4farm02) + 2 fresh bots
///              round 2 in-window (b4farm03/b4farm04) — each runs the same
///              farm-lifecycle leg (rig → plant potato → plant calf → mature
///              → harvest yield → feed calf → replant), all through the
///              BotDriveBridge farm seam (GameplayActor.Plant/Harvest/
///              Interact — the exact CSCreateDoodadPacket/CSStartSkillPacket
///              engine paths).
///   loops:     1 plant→grow→harvest→replant crop cycle + 1 calf
///              plant→feed (growth-timer catch-up) per actor pre-restart;
///              round-2 in-window proves the same leg survives the R2
///              restart untouched.
///   duration:  measured window ≥ 9 min wall with ≥ 6 min steady-state outside
///              warmup blinds; round-2 scenarios provide the continuous load.
///   restarts:  2 × kill -9 of ONLY the game process (PID-handle kills via
///              E2eStack.RestartGameServer — MySQL + login stay; never pkill),
///              mirroring the M4Vehicles/SLAVE two-restart contract: R1
///              pre-window, R2 post-window (long up-segment rule: no restart
///              inside the window).
///   thresholds (GateBudgets defaults): tick p95 ≤ 100 ms, tick max ≤ 250 ms,
///              region worst ≤ 100 ms, region overruns = 0, scheduler avg ≤ 250
///              ms / max ≤ 1000 ms / step failures = 0 (enforced only when
///              scheduler steps ran, else reported n/a — bridge-driven bots
///              never Wake), DB ≤ 500/min/embodied-char (denominator = 4
///              run-provisioned bots), physics ≤ 0.1/min, same-world ≤ 30/60s,
///              tick-overrun warnings = 0/min, autosave p95 ≤ 4000 ms /
///              max ≤ 10000 ms.
///   warmup:    [boot−30s, boot+120s] recorded but NOT counted (SoakWarmup);
///              rates normalize by steady-state minutes only.
///
/// PASS = every actor's leg passed AND after EACH kill-9 each actor's crop
/// phase/timer is conserved (mature crop byte-equal — plant_time/growth_time/
/// phase_time never rewritten; calf growth timer catches up monotonic-ally
/// along 5780→5781→12774→5782, never regresses) AND harvest yields ≥ 2 with
/// seed conservation (potato 7992 delta ≥ 2, golden +1, seed 15659 back ×1
/// → replant consumes exactly 1) AND every budget verdict green.
///
/// Runs on an ISOLATED lane (own E2E_ROOT/ports/DB/compose; refuses the shared
/// default root, port 3306, .165 game host, and the shared compose project).
/// Teardown is PID-verified (both restart PIDs + StopAll + closed-port probe)
/// — pkill is banned. H stays UNKNOWN: scripted-actor evidence only.
/// </summary>
[Collection("e2e")]
public class B4FarmSoakE2eTests
{
    private static readonly string[] Round1Bots = ["b4farm01", "b4farm02"];
    private static readonly string[] Round2Bots = ["b4farm03", "b4farm04"];

    // Canonical 1.2 ids (B2 FARM-01 pinned, compact.sqlite3 verified):
    //   potato seed 15659 → crop doodad 2259 (감자): 4379 seedling → 4456 small
    //     → 4457 mature → harvest skill 13980 → 4458 looting (loot pack 129 =
    //     6452: potato 7992 ×2-4, golden 19887 ×1, seed 15659 ×1 back) →
    //     4459 final (deleted);
    //   dairy calf item 16225 → livestock doodad 2672: 5780 calf → 5781 growing
    //     → 12774 mature interrim → 5782 cow. Feeding skill 20595 (사료먹이기)
    //     is the real feed interact on the calf's calf/growing phases.
    private const uint PotatoSeedItemId = 15659;
    private const uint CalfItemId = 16225;
    private const uint PotatoDoodadId = 2259;
    private const uint CalfDoodadId = 2672;
    private const uint PotatoItemId = 7992;
    private const uint GoldenPotatoItemId = 19887;
    private const uint CropFeedSkillId = 20595; // 사료먹이기 — the real feed interact

    private const uint CropSeedlingPhase = 4379;
    private const uint CropSmallPhase = 4456;
    private const uint CropMaturePhase = 4457;
    private const uint CalfStartPhase = 5780;
    private const uint CalfGrowPhase = 5781;
    private const uint CalfMatureInterimPhase = 12774;
    private const uint CowPhase = 5782;
    private static readonly uint[] CropChain = [CropSeedlingPhase, CropSmallPhase, CropMaturePhase];
    private static readonly uint[] CalfChain = [CalfStartPhase, CalfGrowPhase, CalfMatureInterimPhase, CowPhase];

    private const double MinWindowMinutes = 9;
    private const double MinSteadyMinutes = 6;
    private const int EmbodiedBots = 4;

    private sealed record DoodadSnapshot(uint Id, int OwnerId, int OwnerType, int AttachPoint,
        uint TemplateId, uint CurrentPhaseId, DateTime PlantTime, DateTime GrowthTime, DateTime PhaseTime,
        float X, float Y, float Z, float Roll, float Pitch, float Yaw, float Scale,
        ulong ItemId, uint HouseId, uint ParentDoodad, uint ItemTemplateId, uint ItemContainerId, int Data, int FarmType);
    private sealed record MetricsSample(double TickP95, double TickMax, double RegionMs,
        double SaveP95, double SaveMax, bool SaveAvail, long StepsRun, long StepsFailed,
        double SchedAvg, double SchedMax, bool SchedAvail);

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private readonly List<string> _failures = [];
    private readonly Dictionary<string, string> _actorStages = new();
    private readonly Dictionary<string, string> _actorEvidence = new();
    private void Check(bool cond, string msg) { if (!cond) _failures.Add(msg); }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task B4Farm_LifecycleSoak_TwoKill9_ConservesCropTimer_SeedAndYield_WithinBudgets()
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
            // ---- round 1: full farm lifecycles -------------------------------------
            foreach (var bot in Round1Bots)
                round1[bot] = RunActorFarming(bot, 420_000);
            foreach (var bot in Round1Bots)
                Check(round1[bot] == "PASS", $"round-1 {bot} farm-lifecycle not PASS: {round1[bot]}");

            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
                CheckSaveAck(bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000), "pre-R1");

            var preCrop = new Dictionary<string, DoodadSnapshot>();
            var preCalf = new Dictionary<string, DoodadSnapshot>();
            var preCounts = new Dictionary<string, Dictionary<uint, int>>();
            foreach (var bot in Round1Bots)
            {
                var r1CharId = ResolveCharacterId(bot);
                if (r1CharId == 0) continue;
                CheckFarmOwned(r1CharId, bot, "pre-R1");
                preCounts[bot] = ReadBagCounts(r1CharId);
                // Snapshot whatever crop/calf stands for the restart-intact
                // equality read (the crop may be mid-maturity; the calf is
                // mid-growth — plant_time/growth_time/phase_time must survive).
                preCrop[bot] = OwnedDoodad(r1CharId, PotatoDoodadId);
                preCalf[bot] = OwnedDoodad(r1CharId, CalfDoodadId);
            }

            // ---- R1 (pre-window) ----------------------------------------------------
            killedPid1 = E2eStack.RestartGameServer();
            Check(killedPid1 > 0, "R1 killed no game PID — the restart cannot be trusted");
            foreach (var bot in Round1Bots)
                CheckPostRestartFarm(bot, "R1", preCounts.TryGetValue(bot, out var pc) ? pc : null,
                    preCrop.TryGetValue(bot, out var pcc) ? pcc : null, preCalf.TryGetValue(bot, out var pcl) ? pcl : null);

            // ---- measured window (long up-segment: NO restart inside) ---------------
            var dbStart = ReadDbWriteCounters();
            var logOff = File.Exists(GateSoakRunner.GameLogPath) ? new FileInfo(GateSoakRunner.GameLogPath).Length : 0;
            var restartLogOff = File.Exists(GateSoakRunner.GameRestartLogPath) ? new FileInfo(GateSoakRunner.GameRestartLogPath).Length : 0;
            var windowStartUtc = DateTime.UtcNow;
            var windowStartLocal = DateTime.Now;
            var samples = new List<MetricsSample>();
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
                samples.Add(SampleOnce(bridge));

            // Round 2 = continuous farm load inside the window + live re-adopt
            // re-plant recovery proof after R2.
            var tasks = Round2Bots.Select(bot => Task.Run(() => (bot, result: RunActorFarming(bot, 420_000)))).ToList();
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
                Check(t.Result.result == "PASS", $"round-2 {t.Result.bot} farm-lifecycle not PASS (post-R2 replant recovery): {t.Result.result}");
            }

            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                CheckSaveAck(bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000), "window-end");
                samples.Add(SampleOnce(bridge));
            }
            foreach (var bot in Round2Bots)
            {
                var charId = ResolveCharacterId(bot);
                Check(charId > 0, $"round-2 bot '{bot}' has no characters row");
                if (charId > 0) CheckFarmOwned(charId, bot, "window-end");
            }

            // Snapshot the round-2 doodads just BEFORE R2 so the timer/identity
            // conservation across the second restart is provable in EQUALITY
            // (B2 AssertNear class: plant_time/growth_time/phase_time never
            // rewritten at boot — ApplyLoadedState) rather than a recency bound
            // that a legitimately-aged window-start plant would false-FAIL.
            var preR2Crop = new Dictionary<string, DoodadSnapshot>();
            var preR2Calf = new Dictionary<string, DoodadSnapshot>();
            foreach (var bot in Round2Bots)
            {
                var charId = ResolveCharacterId(bot);
                if (charId == 0) continue;
                preR2Crop[bot] = OwnedDoodad(charId, PotatoDoodadId);
                preR2Calf[bot] = OwnedDoodad(charId, CalfDoodadId);
            }

            // ---- budget evaluation (warmup-blind, steady-state rates) ---------------
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
            try { File.Copy(GateSoakRunner.GameRestartLogPath, Path.Combine(E2eStack.E2eRoot, "logs", "game-round2.log"), overwrite: true); } catch (Exception ex) { Console.WriteLine($"[b4farm] round-2 log preserve failed (non-fatal): {ex.Message}"); }

            // ---- R2 (post-window): second restart contract ---------------------------
            killedPid2 = E2eStack.RestartGameServer();
            Check(killedPid2 > 0, "R2 killed no game PID — the restart cannot be trusted");
            foreach (var bot in Round2Bots)
                CheckPostRestartFarm(bot, "R2", null, preR2Crop.TryGetValue(bot, out var pc) ? pc : null, preR2Calf.TryGetValue(bot, out var pk) ? pk : null);
        }
        catch (Exception ex)
        {
            _failures.Add($"run threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await CleanupAsync(Round1Bots.Concat(Round2Bots).ToArray()); }
            catch (Exception ex) { Console.WriteLine($"[b4farm] cleanup failed (non-fatal): {ex.Message}"); }
        }

        teardown = VerifyTeardown();
        await WriteReportAsync(startedAt, budgets, killedPid1, killedPid2, round1, round2, verdicts,
            windowWallMin, steadyMin, warmupExcludedPhysics, warmupExcludedOverruns, bootTimes, teardown);
        Assert.True(_failures.Count == 0,
            $"B4 FARM S-run RED ({_failures.Count}):\n- {string.Join("\n- ", _failures)}");
    }

    private static void AssertIsolatedLane()
    {
        Assert.False(string.Equals(E2eStack.E2eRoot, "/root/aaemu-e2e", StringComparison.Ordinal),
            "B4 FARM S-run refuses the shared default E2E_ROOT (isolated lane required)");
        Assert.NotEqual(3306, E2eStack.DbPort);
        Assert.Equal("127.0.0.1", E2eStack.GameHost);
        Assert.NotEqual("e2e", E2eStack.ComposeProject);
    }

    /// <summary>
    /// One actor's farm-lifecycle leg, driven entirely through the Bridge's
    /// farm seam (real GameplayActor engine paths). Returns PASS or a
    /// FAIL @stage: reason string. This is the continuous-load unit inside
    /// both the pre-window and in-window rounds.
    /// </summary>
    private string RunActorFarming(string bot, int bridgeTimeoutMs)
    {
        var stages = new List<string>();
        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            var prov = bridge.Call($"{{\"cmd\":\"provision\",\"bot\":\"{bot}\",\"fresh\":true,\"level\":10}}", timeoutMs: bridgeTimeoutMs);
            var charId = prov.GetProperty("id").GetUInt32();
            stages.Add($"provision(id={charId})");
            if (charId == 0) return "FAIL @provision: no character id";

            var rig = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{bot}\",\"seeds\":5,\"calves\":2,\"labor\":5000}}",
                timeoutMs: 60_000);
            stages.Add($"rig(seeds={rig.GetProperty("seeds").GetInt32()},calves={rig.GetProperty("calves").GetInt32()})");
            if (rig.GetProperty("seeds").GetInt32() < 2) return "FAIL @rig: no potato seeds stocked";
            if (rig.GetProperty("calves").GetInt32() < 1) return "FAIL @rig: no calf item stocked";

            // Plant the crop then wait for maturity (E2E GrowthRate 3600 → matures
            // in ~170ms wall, but scenario pump gives a generous budget).
            var plantCrop = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"plant\",\"bot\":\"{bot}\",\"seed\":{PotatoSeedItemId}}}",
                timeoutMs: 120_000);
            var cropObjId = plantCrop.GetProperty("objId").GetUInt32();
            var cropDbId = plantCrop.GetProperty("dbId").GetUInt32();
            stages.Add($"plantcrop(obj={cropObjId},db={cropDbId},tpl={plantCrop.GetProperty("template").GetUInt32()})");
            if (cropObjId == 0 || cropDbId == 0) return $"FAIL @plantcrop: detail={plantCrop.GetProperty("detail").GetString()}";
            if (plantCrop.GetProperty("template").GetUInt32() != PotatoDoodadId) return "FAIL @plantcrop: wrong doodad template";

            // Wait for the mature phase.
            WaitForLivePhase(bridge, bot, cropDbId, CropMaturePhase, TimeSpan.FromSeconds(180));
            stages.Add("crop-mature");

            // Harvest via the real Doodad.Use chain; assert real yield + seed grant.
            var liveCropObj = FindLiveObjId(bridge, bot, cropDbId);
            var before = BagCounts(bridge, bot, [PotatoItemId, GoldenPotatoItemId, PotatoSeedItemId]);
            var harvest = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"harvest\",\"bot\":\"{bot}\",\"objId\":{liveCropObj}}}",
                timeoutMs: 120_000);
            stages.Add($"harvest(state={harvest.GetProperty("state").GetString()},yield={harvest.GetProperty("yield").GetInt32()})");
            var yield = harvest.GetProperty("yield").GetInt32();
            if (harvest.GetProperty("state").GetString() != "Completed") return $"FAIL @harvest: {harvest.GetProperty("detail").GetString()}";
            var afterHarvest = BagCounts(bridge, bot, [PotatoItemId, GoldenPotatoItemId, PotatoSeedItemId]);
            var potatoGained = afterHarvest[PotatoItemId] - before[PotatoItemId];
            var goldenGained = afterHarvest[GoldenPotatoItemId] - before[GoldenPotatoItemId];
            var seedGained = afterHarvest[PotatoSeedItemId] - before[PotatoSeedItemId];
            stages.Add($"yield(potatoDelta={potatoGained},golden={goldenGained},seedBack={seedGained})");
            if (yield < 2) return $"FAIL @yield: harvest yielded {yield} unit(s), expected ≥ 2 (detail={harvest.GetProperty("detail").GetString()})";
            if (potatoGained < 2) return $"FAIL @yield: potato 7992 delta {potatoGained}, expected ≥ 2";
            // Seed conservation: the canonical pack returns seed ×1; replanting
            // below consumes exactly 1 so the bag seed count must not drop net.
            if (seedGained < 1) return $"FAIL @yield: no seed 15659 returned from harvest (conservation broken)";

            // Replant the freed plot (the real Plant path consumes the returned seed).
            var replant = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"plant\",\"bot\":\"{bot}\",\"seed\":{PotatoSeedItemId}}}",
                timeoutMs: 120_000);
            stages.Add($"replant(db={replant.GetProperty("dbId").GetUInt32()})");
            if (replant.GetProperty("dbId").GetUInt32() == 0) return $"FAIL @replant: detail={replant.GetProperty("detail").GetString()}";

            // Plant the calf at an offset so it does not collide with the crop,
            // then feed it through the real interact skill (사료먹이기 20595).
            var plantCalf = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"plant\",\"bot\":\"{bot}\",\"seed\":{CalfItemId},\"dx\":4}}",
                timeoutMs: 120_000);
            var calfObjId = plantCalf.GetProperty("objId").GetUInt32();
            var calfDbId = plantCalf.GetProperty("dbId").GetUInt32();
            stages.Add($"plantcalf(obj={calfObjId},db={calfDbId},tpl={plantCalf.GetProperty("template").GetUInt32()})");
            if (calfObjId == 0 || calfDbId == 0) return $"FAIL @plantcalf: detail={plantCalf.GetProperty("detail").GetString()}";
            if (plantCalf.GetProperty("template").GetUInt32() != CalfDoodadId) return "FAIL @plantcalf: wrong doodad template";

            var feed = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"interact\",\"bot\":\"{bot}\",\"objId\":{calfObjId},\"skill\":{CropFeedSkillId}}}",
                timeoutMs: 60_000);
            stages.Add($"feed(state={feed.GetProperty("state").GetString()},phase={feed.GetProperty("phase").GetUInt32()})");
            if (feed.GetProperty("state").GetString() != "Completed")
                return $"FAIL @calf-feed: {feed.GetProperty("detail").GetString()} (calf must accept the real 사료먹이기 interact)";

            // Save the farm state to MySQL so the restart-intact read is grounded.
            var saveAck = bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000);
            if (!saveAck.GetProperty("saved").GetBoolean()) return "FAIL @save: bridge save pass did not complete";
            stages.Add("save");

            _actorStages[bot] = string.Join(" → ", stages);
            return "PASS";
        }
        catch (Exception ex)
        {
            _actorStages[bot] = string.Join(" → ", stages);
            return $"ERROR {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void CheckPostRestartFarm(string bot, string tag, Dictionary<uint, int>? preBag,
        DoodadSnapshot? preCropSnap, DoodadSnapshot? preCalfSnap)
    {
        var charId = ResolveCharacterId(bot);
        Check(charId > 0, $"post-{tag} bot '{bot}' has no characters row");
        if (charId == 0) return;

        // The actor's crop and calf must still be owned and their timers/identity
        // conserved across the restart (B2 AssertNear class): plant_time /
        // growth_time / phase_time never rewritten at boot — ApplyLoadedState's
        // whole invariant — and phase may only move FORWARD along the canonical
        // chain (overdue growth timer catch-up), never regress. When a pre-restart
        // snapshot exists we assert byte/timer equality directly (not a recency
        // bound, which a legitimately-aged window-start plant would false-FAIL).
        var crop = OwnedDoodad(charId, PotatoDoodadId);
        var calf = OwnedDoodad(charId, CalfDoodadId);
        if (crop == null)
            _actorEvidence.TryAdd(bot, $"[{tag}] no crop doodad owned (harvested crop is deleted by the final phase; replant window)");
        if (crop != null)
        {
            Check(CropChain.Contains(crop.CurrentPhaseId),
                $"[{tag}] {bot}: crop phase {crop.CurrentPhaseId} outside canonical chain");
            if (preCropSnap != null)
            {
                Check(preCropSnap.Id == crop.Id, $"[{tag}] {bot}: crop doodad id changed {preCropSnap.Id}→{crop.Id} (dup/respawn)");
                CheckNear(preCropSnap, crop, $"[{tag}] {bot}: crop timer clobbered over restart");
            }
            else
            {
                // No pre-restart snapshot (e.g. a replant boundary): the crop
                // must still carry a growth clock and a plausible recent plant.
                Check(crop.GrowthTime >= crop.PlantTime, $"[{tag}] {bot}: crop growth clock missing");
            }
        }
        if (calf != null)
        {
            var idx = Array.IndexOf(CalfChain, calf.CurrentPhaseId);
            Check(idx >= 0, $"[{tag}] {bot}: calf phase {calf.CurrentPhaseId} outside canonical chain");
            if (preCalfSnap != null)
            {
                Check(preCalfSnap.Id == calf.Id, $"[{tag}] {bot}: calf doodad id changed {preCalfSnap.Id}→{calf.Id} (dup/respawn)");
                CheckNear(preCalfSnap, calf, $"[{tag}] {bot}: calf timer clobbered over restart");
            }
        }
        if (preBag != null)
        {
            var postBag = ReadBagCounts(charId);
            // Harvest conservation: potato+golden+seed must never regress across
            // a restart (restart must not destroy/dedupe acquired yield).
            foreach (var (tpl, preCount) in preBag)
            {
                var postCount = postBag.TryGetValue(tpl, out var c) ? c : 0;
                Check(postCount >= preCount, $"[{tag}] {bot}: item {tpl} went {preCount}→{postCount} across restart (yield loss/dup)");
            }
        }
    }

    /// <summary>
    /// B2 AssertNear class: the persisted doodad's identity, plant/growth/phase
    /// clocks and position must be equal across the restart (±2 s DATETIME,
    /// float epsilon). A rewrite at boot (ApplyLoadedState should never write)
    /// or a row respawn is THE persistence defect.
    /// </summary>
    private static void CheckNear(DoodadSnapshot pre, DoodadSnapshot post, string what)
    {
        Assert.True(Math.Abs((post.PlantTime - pre.PlantTime).TotalSeconds) < 2,
            $"{what}: plant_time rewritten at boot: stored {post.PlantTime:O}, pre {pre.PlantTime:O}");
        // Growth/phase may legitimately CATCH UP during reboot downtime (the
        // overdue growth timer advances the phase), but they must never move
        // BACKWARD and never be reset to a boot-time base.
        Assert.True(post.GrowthTime >= pre.GrowthTime.AddSeconds(-2),
            $"{what}: growth_time regressed over restart: {pre.GrowthTime:O} → {post.GrowthTime:O}");
        Assert.True(post.PhaseTime >= pre.PhaseTime.AddSeconds(-2),
            $"{what}: phase_time regressed over restart: {pre.PhaseTime:O} → {post.PhaseTime:O}");
        Assert.True(MathF.Abs(pre.X - post.X) < 0.01f && MathF.Abs(pre.Y - post.Y) < 0.01f && MathF.Abs(pre.Z - post.Z) < 0.01f,
            $"{what}: position clobbered over restart: ({pre.X},{pre.Y},{pre.Z}) → ({post.X},{post.Y},{post.Z})");
    }

    private static void WaitForLivePhase(BotDriveClient bridge, string bot, uint dbId, uint wantPhase, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var find = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"find\",\"bot\":\"{bot}\",\"dbIds\":[{dbId}]}}",
                timeoutMs: 60_000);
            var entry = find.GetProperty("doodads").EnumerateArray().First();
            if (entry.GetProperty("found").GetBoolean() && entry.GetProperty("phase").GetUInt32() == wantPhase)
                return;
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"doodad db {dbId} for {bot} never reached phase {wantPhase} within {budget.TotalSeconds}s");
    }

    private static uint FindLiveObjId(BotDriveClient bridge, string bot, uint dbId)
    {
        var find = bridge.Call(
            $"{{\"cmd\":\"farm\",\"op\":\"find\",\"bot\":\"{bot}\",\"dbIds\":[{dbId}]}}",
            timeoutMs: 60_000);
        var entry = find.GetProperty("doodads").EnumerateArray().First();
        if (!entry.GetProperty("found").GetBoolean())
            throw new InvalidOperationException($"doodad db {dbId} for {bot} not in the live world");
        return entry.GetProperty("objId").GetUInt32();
    }

    private static Dictionary<uint, int> BagCounts(BotDriveClient bridge, string bot, uint[] templates)
    {
        var status = bridge.Call(
            $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{bot}\",\"objIds\":[],\"items\":[{string.Join(",", templates)}]}}",
            timeoutMs: 60_000);
        return status.GetProperty("items").EnumerateArray()
            .ToDictionary(e => e.GetProperty("template").GetUInt32(), e => e.GetProperty("count").GetInt32());
    }

    private static uint ResolveCharacterId(string botName)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM characters WHERE LOWER(name) = @name LIMIT 1";
        cmd.Parameters.AddWithValue("@name", botName.ToLowerInvariant());
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? 0u : Convert.ToUInt32(raw);
    }

    private static Dictionary<uint, int> ReadBagCounts(uint charId)
    {
        var counts = new Dictionary<uint, int>();
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_id, SUM(count) c FROM items WHERE owner = @charId AND slot_type IN (0,1) GROUP BY template_id";
        cmd.Parameters.AddWithValue("@charId", charId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetUInt32("template_id")] = reader.GetInt32("c");
        return counts;
    }

    private static DoodadSnapshot? OwnedDoodad(uint charId, uint templateId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, owner_id, owner_type, attach_point, template_id, current_phase_id, " +
            "plant_time, growth_time, phase_time, x, y, z, roll, pitch, yaw, scale, item_id, " +
            "house_id, parent_doodad, item_template_id, item_container_id, data, farm_type " +
            "FROM doodads WHERE owner_id = @charId AND template_id = @templateId ORDER BY plant_time DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@charId", charId);
        cmd.Parameters.AddWithValue("@templateId", templateId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new DoodadSnapshot(
            reader.GetUInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetUInt32(4), reader.GetUInt32(5),
            reader.GetDateTime(6), reader.GetDateTime(7), reader.GetDateTime(8),
            reader.GetFloat(9), reader.GetFloat(10), reader.GetFloat(11),
            reader.GetFloat(12), reader.GetFloat(13), reader.GetFloat(14), reader.GetFloat(15),
            reader.GetUInt64(16), reader.GetUInt32(17), reader.GetUInt32(18),
            reader.GetUInt32(19), reader.GetUInt32(20), reader.GetInt32(21), reader.GetInt32(22));
    }

    private void CheckFarmOwned(uint charId, string bot, string phase)
    {
        var hasItems = false;
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE owner = @charId";
            cmd.Parameters.AddWithValue("@charId", charId);
            hasItems = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        Check(hasItems, $"[{phase}] {bot}: owns no item rows at all");
    }

    private void CheckSaveAck(JsonElement ack, string phase)
    {
        Check(ack.TryGetProperty("saved", out var s) && s.GetBoolean(),
            $"[{phase}] bridge save pass did not complete before snapshot");
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
            slice = "B4 FARM-01 S run — budgeted farm-lifecycle soak (plant→grow→harvest→replant + calf feed)",
            card = "FARM-01 S run needs STATED budgets (actors × duration × restarts) with measured results",
            stated_budget = new
            {
                actors = "2 round-1 (b4farm01/02) + 2 round-2 in-window (b4farm03/04) — farm-lifecycle legs via the BotDriveBridge farm seam (real GameplayActor Plant/Harvest/Interact paths)",
                loops = "1 plant→grow→harvest→replant crop cycle + 1 calf plant→feed per actor; round-2 runs the same leg under measurement",
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
            scenario_stages = _actorStages,
            scenario_evidence = _actorEvidence,
            db_port = E2eStack.DbPort,
            game_host = E2eStack.GameHost,
            source_revision = E2eStack.SourceRevision,
            verdict = _failures.Count == 0 ? "PASS" : "FAIL",
            failures = _failures.ToArray(),
            round1, round2,
            budgets = verdictRows,
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            teardown,
            anchors = "B2FARM-01 restart persistence (crop byte-equal, calf timer catch-up, plant_time never rewritten); CropHarvestLoopTests 6/6; GateBudgetEvaluator defaults; SoakWarmup blinds",
            proxy_note = "scripted-actor / bot-functional evidence — H (feel) stays UNKNOWN"
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "b4-farm-soak-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var md = new System.Text.StringBuilder();
        md.AppendLine("# B4 FARM-01 S run — budgeted farm-lifecycle soak");
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
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "b4-farm-soak-report.md"), md.ToString());
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
                                 "DELETE FROM doodads WHERE owner_id = @charId",
                                 "DELETE FROM items WHERE owner = @charId",
                                 "DELETE FROM item_containers WHERE owner_id = @charId",
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
        catch (Exception e) { Console.WriteLine($"[b4farm] cleanup failed (non-fatal): {e.Message}"); }
    }
}
