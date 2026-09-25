using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
///
/// Production decision chain under test (code-verified 2026-09-18):
///   scheduler wake → BotGoalArbiterStepExecutor.StepAsync: arbitrate, then
///     inner step (BotGoalArbiterStepExecutor.cs:32-41)
///   → arbiter needs QuestBootstrapActivityModule.CanActivate = Allow, which
///     requires AAEMU_QUEST_BOOTSTRAP_ENABLED=1|true|True
///     (QuestBootstrapActivityModule.cs:23-24,68-69) + alive + not in battle
///     + pressure below High + (active quests OR money below GoldTarget 1000)
///     (QuestBootstrapActivityModule.cs:84-87); priority 58 beats
///     PresenceRoam 50 when schedules are off (lane default)
///   → BotRoamStepExecutor.StepQuestLeg runs QuestDecisionScenario.Run on the
///     EXECUTOR's live actor with default band [BandMin 1, BandMax 9]
///     (BotRoamStepExecutor.cs:1191-1195; QuestDecisionScenario.cs:53-56)
///   → perception: nearbyNpcs(character, MaxQuestDiscoverRange = 25f flat)
///     (QuestDecisionScenario.cs:125; GameplayActor.cs:1160,1396) via the
///     region scan DefaultNearbyNpcs (BotRoamStepExecutor.cs:2040-2047)
///   → in-band offerings accepted lowest-level-first through the LIVE actor
///     AcceptQuest (QuestDecisionScenario.cs:154-157,316-317) — autonomy
///     evidence lands in the executor live-actor audit trace.
///
/// FIXTURE staged INSIDE those conditions (nothing invented): quest 251
/// (LEVEL 2 ∈ [1,9]) at giver NPC 2425 (Q0 live-proven materializable),
/// fresh account (activeBefore must be False), setLevel 10 (Q0-proven accept
/// gates), staged at the spawner (≤25 m perception), fresh-bot money below
/// the 1000-copper bootstrap threshold (recorded live).
///
/// HARNESS (fixed 2026-09-19; prior runs are harness-UNKNOWN, not production
/// verdicts): (1) wake proof is PER-BOT — the bridge `stepped` flag derives
/// from GLOBAL scheduler TotalStepsRun (BotDriveBridge.cs:4381,4405), which
/// stale never-Removed bots from earlier tests can satisfy. This test polls
/// observe-only `quest observe` within the same 30s budget until one of OUR
/// characterId's signals flips (questLegActive, non-empty questTravelReason,
/// quest-action audit rows above baseline, audit-trace growth for our actor);
/// none flipping records wake=UNPROVEN, never `stepped`. (2) bootstrapOn is
/// read from the GAME side — /proc/&lt;game-pid&gt;/environ of the live game
/// process — never runner env (E2eStack.CapabilityManifest is runner-env by
/// construction, E2eStack.cs:1029-1042, and diverges on an adopted warm lane).
/// NOT IMPLEMENTED only when the game side truly lacks the flag. (3) cleanup:
/// bridge `deactivate` covers provisioned PersistentBotSessions only
/// (BotDriveBridge.cs:2632-2633), no bridge op releases networked
/// characters, so post-Dispose cleanup is recorded UNAVAILABLE, not asserted.
/// Fixture unchanged: quest 251, giver 2425, level 10, waitMs 30000, fresh
/// accounts, observe-only after START.
///
///
/// CLAIM (only, if it passed): the production scheduler autonomously accepts
/// an in-band quest for a correctly staged bot through the live actor.
/// NOT claimed: navigation-to-giver, talk/kill pursuit, turn-in, any other
/// quest, any autonomy beyond one accept leg.
/// </summary>
[Collection("e2e")]
public class G3AutonomousQuestAcceptTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G3QuestAuto" + Stamp;
    private static readonly string BotAccount = ("g3questauto" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";

    private const uint Quest251 = 251;
    private const uint GiverNpc2425 = 2425;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "g3-quest-autonomy-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task SchedulerWake_StagedInRangeBot_AcceptsViaLiveActor()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G3 quest-autonomy gate — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        void Leg(string leg, bool passed, string detail, double ms)
        {
            legs.Add((leg, passed, detail, ms));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        BotNetworkSession? session = null;
        BotDriveClient? laneBridge = null;
        var setupSeconds = 0.0;
        var execSeconds = 0.0;
        var passed = false;
        string verdict = "UNKNOWN";
        string failBoundary = "";
        string claim = "";
        object? failingCondition = null;
        uint npcObjId = 0;
        string charPos = "UNAVAILABLE";
        string wakeSummary = "UNAVAILABLE", auditSummary = "UNAVAILABLE";
        string cleanupSummary = "UNAVAILABLE";
        string bootstrapSummary = "UNAVAILABLE";
        uint ourCharacterId = 0;
        int baselineQuestActions = 0;
        long baselineWakeSeq = 0, baselineTraceWakeSeq = 0, baselineMaxTraceWakeSeq = 0;

        try
        {
            // ---- ADOPT (probe-only; never EnsureUp/Restart) ----
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = KillForeignWebApiListeners();
            evidence.AppendLine($"- stale-proc guard (shared :{E2eStack.WebApiPort} listeners outside this lane): {staleGuardDetail}");
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "warm lane answering (WebApi TCP + bridge ping)" : "lane cold — refusing to rebuild",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                failBoundary = "SETUP/lane-down";
                Assert.Fail("warm Q0 lane not answering; refusing to rebuild per workstream constraints");
            }

            // Lane password adopt (mirrors EnsureEnvFile DB_PASSWORD read,
            // E2eStack.cs:267-270, without rebuilding).
            AdoptLaneDbPassword();
            var bridge = new BotDriveClient(E2eStack.BridgePort);
            laneBridge = bridge;

            // ---- SETUP: real TCP enter-world ----
            E2eStack.CleanupBotRows(BotAccount);
            session = await BotNetworkSession.ConnectAsync(
                BotName, BotAccount, Password,
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!session.InWorld)
            {
                failBoundary = "SETUP/enter-world";
                Leg("enter-world", false, "real login flow did not reach in-world", setupSw.Elapsed.TotalSeconds);
                Assert.Fail("bot must enter the live world through the real login flow");
            }
            Leg("enter-world", true, $"inWorld={session.InWorld}", setupSw.Elapsed.TotalSeconds);

            // ---- ENROLL at spawn: must NOT pre-empt (activeBefore False) ----
            // Per-bot baseline for the wake proof (the bridge `stepped` flag is
            // GLOBAL scheduler TotalStepsRun — satisfiable by stale bots — so it
            // is recorded here for contrast only, never used as proof).
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var enrollSteppedGlobal = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            ourCharacterId = GetUInt(enroll, "id");
            baselineQuestActions = GetInt(enroll, "questActionCount");
            baselineWakeSeq = GetSeq(enroll, "wakeSeq");
            baselineTraceWakeSeq = GetSeq(enroll, "traceWakeSeq");
            baselineMaxTraceWakeSeq = GetSeq(enroll, "maxTraceWakeSeq");
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("enroll", !activeBefore,
                $"charId={ourCharacterId} baselineQuestActions={baselineQuestActions} baselineWakeSeq={baselineWakeSeq} baselineTraceWakeSeq={baselineTraceWakeSeq} steppedGlobal={enrollSteppedGlobal}(contrast-only) activeBefore={activeBefore} (must be False)",
                enrollSw.Elapsed.TotalSeconds);
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest already active after enroll wake; re-run with a fresh account");
            }

            // ---- FIXTURE (PRE-START, disclosed): level 10 + stage at giver 2425 ----
            var fixSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{FixtureLevel}}}", 30_000);
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{GiverNpc2425}}}", 60_000);
            npcObjId = PollNpcObjId(bridge, GiverNpc2425);
            charPos = ReadPos(bridge);
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            var staged = npcObjId != 0;
            Leg("fixture-stage", staged,
                $"lvl={FixtureLevel} giverObjId={npcObjId} char=[{charPos}] (staging only; inside 25m perception + band [1,9] preconditions)",
                fixSw.Elapsed.TotalSeconds);
            if (!staged)
            {
                failBoundary = "SETUP/npc-unresolved";
                Assert.Fail($"giver NPC template {GiverNpc2425} never materialized (objId=0 after 30s poll)");
            }

            // ---- START: one bounded scheduler wake, then observe ONLY ----
            // PER-BOT step proof (never the GLOBAL `stepped` flag): poll
            // observe-only `quest observe` until one of OUR characterId's signals
            // flips — questLegActive, non-empty questTravelReason, quest-action
            // audit rows above the enroll baseline, or audit-trace growth for our
            // actor (lastActorId == our id) — within the existing 30s budget
            // (WakeWaitMs; no timeout increase). None flipping => wake=UNPROVEN.
            var execSw = Stopwatch.StartNew();
            var wakeStart = Environment.TickCount64;
            var wake = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
            if (ourCharacterId == 0)
                ourCharacterId = GetUInt(wake, "id");
            var observe = wake;
            var wakeProven = HasPerBotStepSignal(observe, ourCharacterId, baselineQuestActions, baselineWakeSeq, baselineTraceWakeSeq, baselineMaxTraceWakeSeq);
            while (!wakeProven && Environment.TickCount64 - wakeStart < WakeWaitMs)
            {
                Thread.Sleep(1000);
                try
                {
                    observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                }
                catch (Exception ex)
                {
                    wakeSummary = $"UNPROVEN (observe poll failed: {ex.GetType().Name})";
                    break;
                }
                wakeProven = HasPerBotStepSignal(observe, ourCharacterId, baselineQuestActions, baselineWakeSeq, baselineTraceWakeSeq, baselineMaxTraceWakeSeq);
            }
            var wakeSteppedGlobal = wake.TryGetProperty("stepped", out var wsEl) && wsEl.GetBoolean();
            wakeSummary = (wakeProven ? "PROVEN per-bot" : "UNPROVEN (no per-bot step signal within 30s)")
                + $" charId={ourCharacterId} [{SummarizeWake(observe)}] steppedGlobal={wakeSteppedGlobal}(contrast-only, GLOBAL TotalStepsRun)";
            Leg("wake", wakeProven, wakeSummary, execSw.Elapsed.TotalSeconds);

            // ---- OBSERVE ONLY: no gameplay commands after START ----
            var activeAfter = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            auditSummary = SummarizeAudit(observe);
            execSeconds = execSw.Elapsed.TotalSeconds;

            var accepts = GetInt(observe, "accepts");
            var questActionCount = GetInt(observe, "questActionCount");
            var lastAction = observe.TryGetProperty("lastAction", out var laEl) ? laEl.GetString() ?? "" : "";
            var lastResult = observe.TryGetProperty("lastResult", out var lrEl) ? lrEl.GetString() ?? "" : "";
            passed = activeAfter && accepts >= 1 && lastAction == "AcceptQuest" && lastResult == "Completed";
            // Derived verdict (never hardcoded): the flag state is READ from the
            // GAME side (/proc environ of the live game process — never runner
            // env: E2eStack.CapabilityManifest is runner-env by construction and
            // diverges on an adopted warm lane). quest.progress held = the arbiter
            // yielded a quest activity this wake (leg ran, or a travel reason was
            // armed) — the flag-on signature. Earliest behavior boundary:
            // ARBITRATION (no quest activity) -> DISCOVERY (activity, zero
            // offers) -> FILTERING (leg dispatched, no accept).
            var (bootstrapOn, bootstrapGameValue, bootstrapSource) = ReadGameSideBootstrapFlag();
            bootstrapSummary = $"gameSide={bootstrapGameValue ?? "<absent>"} [{bootstrapSource}]";
            var bootstrapProvenOff = bootstrapSource.StartsWith("game-proc-environ", StringComparison.Ordinal) && !bootstrapOn;
            var wakeLegActive = observe.TryGetProperty("questLegActive", out var wlEl) && wlEl.GetBoolean();
            var travelReason = observe.TryGetProperty("questTravelReason", out var trEl) ? trEl.GetString() ?? "" : "";
            var travelDistanceM = MeasureTravelDistance(travelReason);
            var progressHeld = wakeLegActive || travelReason.Length > 0;
            if (passed)
            {
                verdict = "PASS-CANONICAL";
                failBoundary = "none";
                claim = "the production scheduler autonomously accepted in-band quest 251 for a staged bot through the executor live actor (direct live-actor AcceptQuest dispatch, NOT via BotActionCommandQueue)";
                failingCondition = null;
            }
            else if (!wakeProven)
            {
                verdict = "HARNESS-UNKNOWN";
                failBoundary = "HARNESS/wake-unproven";
                claim = "no production verdict: OUR bot showed no per-bot step signal within the 30s budget (questLegActive false, travel reason empty, quest-action audit rows at baseline, no audit-trace growth for our actor)";
                failingCondition = new
                {
                    boundary = failBoundary,
                    wakeSummary,
                    auditSummary,
                    ourCharacterId,
                    baselineQuestActions,
                    questActionCount,
                    bootstrap = bootstrapSummary
                };
            }
            else if (accepts == 0 && questActionCount == baselineQuestActions && bootstrapProvenOff)
            {
                verdict = "NOT IMPLEMENTED";
                failBoundary = "CONFIG/bootstrap-off (game side lacks the flag)";
                claim = "the production scheduler autonomously accepts in-band quest 251 for a staged bot through the executor live actor (NOT IMPLEMENTED on this lane: bootstrap module off in the GAME process)";
                failingCondition = new
                {
                    file = "AAEmu.Game/Core/Managers/Bots/QuestBootstrapActivityModule.cs",
                    lines = "23-24 (env gate), 68-69 (CanActivate deny), 84-87 (broke/active-work rule)",
                    missing = "AAEMU_QUEST_BOOTSTRAP_ENABLED=1 in the lane game environment",
                    gameSideValue = bootstrapGameValue ?? "<absent>",
                    gameSideSource = bootstrapSource,
                    arbitration = "without quest.progress the arbiter yields presence.roam/idle; StepQuestLeg never runs",
                    perception = "MaxQuestDiscoverRange 25f, band [1,9], quest 251 LEVEL 2 in band"
                };
            }
            else if (accepts == 0 && questActionCount == baselineQuestActions)
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = progressHeld ? "DISCOVERY/no-legal-proposal" : "ARBITRATION/no-quest-activity";
                claim = "the production scheduler autonomously accepts in-band quest 251 for a staged bot through the executor live actor";
                failingCondition = new
                {
                    boundary = failBoundary,
                    bootstrapEnabledGameSide = bootstrapOn,
                    bootstrap = bootstrapSummary,
                    questProgressHeld = progressHeld,
                    wakeLegActive,
                    travelReason,
                    travelDistanceM,
                    discoverSweepM = 25,
                    discoverMaxTargets = 3,
                    band = "[1,9]",
                    questLevel = 2,
                    interpretation = progressHeld
                        ? "chain engaged (arbitration yielded quest activity) but discovery produced zero in-band offers, so no Dispatch ran; travel fallback armed instead"
                        : "flag on but the arbiter never yielded a quest activity this wake (no leg, no travel reason)"
                };
            }
            else
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = "FILTERING/leg-dispatched-no-accept";
                claim = "the production scheduler autonomously accepts in-band quest 251 for a staged bot through the executor live actor";
                failingCondition = new
                {
                    boundary = failBoundary,
                    accepts,
                    questActionCount,
                    lastAction,
                    lastResult
                };
            }
            Leg("autonomous-accept", passed,
                $"{verdict} active={activeAfter} accepts={accepts} questActions={questActionCount} last={lastAction}/{lastResult} [{auditSummary}]",
                execSw.Elapsed.TotalSeconds);
            Assert.True(passed,
                $"G3 {verdict} at {failBoundary}: quest={Quest251} " +
                $"giver={GiverNpc2425}/{npcObjId} char=[{charPos}] active={activeAfter} [{wakeSummary}] [{auditSummary}]");
        }
        finally
        {
            totalWall.Stop();
            session?.Dispose();
            cleanupSummary = TryReleaseBot(laneBridge, BotName, ourCharacterId);
            await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, legs, evidence.ToString(),
                new { quest = Quest251, giverNpc = GiverNpc2425, npcObjId, charPos, wakeSummary, auditSummary, bootstrapSummary, cleanupSummary },
                setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
        }
    }

    // ---- helpers (adopt-only; no enqueue shapes needed — observe-only gate) ----

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    /// <summary>
    /// PER-BOT step proof for OUR characterId (never GLOBAL TotalStepsRun):
    /// questLegActive true, non-empty questTravelReason, quest-action audit
    /// rows above the enroll baseline, or audit-trace growth attributed to our
    /// actor (lastActorId == our id with rows above baseline).
    /// Phase 1 wake identity (additive, backward compatible — absent fields
    /// read 0 and stay inert): the decorator's per-bot wake counter above
    /// its enroll baseline, the newest trace row's stamped sequence above
    /// baseline, or the max stamped trace sequence above baseline (the staged
    /// sequence is consumed once by each wake's first request, so the max —
    /// not the last — is the per-bot wake proof).
    /// </summary>
    private static bool HasPerBotStepSignal(JsonElement sample, uint ourCharacterId, int baselineQuestActions, long baselineWakeSeq = 0, long baselineTraceWakeSeq = 0, long baselineMaxTraceWakeSeq = 0)
    {
        if (sample.TryGetProperty("questLegActive", out var leg) && leg.GetBoolean())
            return true;
        if (sample.TryGetProperty("questTravelReason", out var reason)
            && !string.IsNullOrEmpty(reason.GetString()))
            return true;
        if (GetInt(sample, "questActionCount") > baselineQuestActions)
            return true;
        if (ourCharacterId != 0
            && GetUInt(sample, "lastActorId") == ourCharacterId
            && GetInt(sample, "questActionCount") > baselineQuestActions)
            return true;
        if (GetSeq(sample, "wakeSeq") > baselineWakeSeq && GetSeq(sample, "wakeSeq") > 0)
            return true;
        if (GetSeq(sample, "traceWakeSeq") > baselineTraceWakeSeq && GetSeq(sample, "traceWakeSeq") > 0)
            return true;
        if (GetSeq(sample, "maxTraceWakeSeq") > baselineMaxTraceWakeSeq && GetSeq(sample, "maxTraceWakeSeq") > 0)
            return true;
        return false;
    }

    private static long GetSeq(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// GAME-side bootstrap flag read (never runner env): scans
    /// /proc/&lt;pid&gt;/environ of the live game process (dotnet hosting
    /// AAEmu.Game.dll) for AAEMU_QUEST_BOOTSTRAP_ENABLED. Returns (on, raw,
    /// source). A warm adopted lane's game env can diverge from the runner env
    /// E2eStack.CapabilityManifest reports, so only an affirmative game-side
    /// absence/off gates NOT IMPLEMENTED.
    /// </summary>
    private static (bool On, string? Raw, string Source) ReadGameSideBootstrapFlag()
    {
        const string flag = "AAEMU_QUEST_BOOTSTRAP_ENABLED";
        try
        {
            foreach (var proc in Process.GetProcessesByName("dotnet"))
            {
                string cmdline;
                try { cmdline = File.ReadAllText($"/proc/{proc.Id}/cmdline").Replace('\0', ' '); }
                catch { continue; }
                if (!cmdline.Contains("AAEmu.Game.dll", StringComparison.Ordinal))
                    continue;
                string env;
                try { env = File.ReadAllText($"/proc/{proc.Id}/environ"); }
                catch (Exception ex) { return (false, null, $"game-proc-environ pid={proc.Id} UNREADABLE ({ex.GetType().Name})"); }
                foreach (var entry in env.Split('\0'))
                {
                    if (!entry.StartsWith(flag + "=", StringComparison.Ordinal))
                        continue;
                    var raw = entry[(flag.Length + 1)..];
                    return (raw is "1" or "true" or "True", raw, $"game-proc-environ pid={proc.Id}");
                }
                return (false, null, $"game-proc-environ pid={proc.Id} (flag absent)");
            }
            return (false, null, "game-proc-missing (no dotnet/AAEmu.Game.dll process found)");
        }
        catch (Exception ex)
        {
            return (false, null, $"game-proc-environ UNAVAILABLE ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Post-Dispose release attempt using EXISTING ops only. Bridge
    /// `deactivate` covers provisioned PersistentBotSessions, never networked
    /// characters (BotDriveBridge HandleDeactivate: "not provisioned" for
    /// non-provisioned names), and no bridge op fronts IPlayerBotManager.Remove
    /// — so for a real-TCP bot this records cleanup=UNAVAILABLE with the
    /// bridge's own refusal as proof. No API is added.
    /// </summary>
    private static string TryReleaseBot(BotDriveClient? bridge, string botName, uint characterId)
    {
        if (bridge == null)
            return "UNAVAILABLE (no bridge handle; TCP session disposed, manager/arbiter/executor registry entry left in place)";
        try
        {
            var res = bridge.Call($"{{\"cmd\":\"deactivate\",\"bot\":\"{botName}\"}}", 30_000);
            if (res.TryGetProperty("removed", out var removed) && removed.GetBoolean())
                return $"RELEASED via bridge deactivate (charId={characterId})";
            if (res.TryGetProperty("error", out var err))
                return $"UNAVAILABLE (bridge deactivate refused for networked char '{botName}' charId={characterId}: {err.GetString()}; no existing op covers networked characters — TCP disposed, registry entry left in place)";
            return $"UNAVAILABLE (bridge deactivate unexpected shape for networked char '{botName}' charId={characterId}: {res}; TCP disposed, registry entry left in place)";
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE (bridge deactivate threw {ex.GetType().Name} for networked char '{botName}' charId={characterId}; TCP disposed, registry entry left in place)";
        }
    }

    /// <summary>
    /// Stale-lane guard: before the gate adopts a warm lane, ANY process
    /// LISTENING on the shared WebApi port whose cwd is NOT this lane's
    /// E2E_ROOT runtime dir is a foreign leftover (a sibling lane or an
    /// orphaned prior run) and would answer the adopt probes for the wrong
    /// stack. Such a listener is SIGTERM'd, then SIGKILL'd after 10 s if it
    /// is still alive. Only listeners are considered, so the current lane's
    /// own pids (which appear as CLIENTS on 127.0.0.1:PORT) are never
    /// touched; the log line is emitted for every kill and returned as
    /// evidence.
    /// </summary>
    private static string KillForeignWebApiListeners()
    {
        var laneRoot = Path.GetFullPath(Path.Combine(E2eStack.E2eRoot, "runtime"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var summary = new List<string>();
        foreach (var pid in ListenersOnPort(E2eStack.WebApiPort))
        {
            try
            {
                var cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget;
                if (cwd == null)
                    continue;
                var full = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);
                var inThisLane = full.Equals(laneRoot, StringComparison.Ordinal)
                    || full.StartsWith(laneRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inThisLane)
                    continue;
                var cmdline = "";
                try { cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { }
                var line = $"stale-proc-guard: pid={pid} listening :{E2eStack.WebApiPort} cwd={full} (not this lane {laneRoot}) cmd=[{cmdline}] — SIGTERM";
                Console.WriteLine("[g3] " + line);
                summary.Add(line);
                using (var term = Process.Start(new ProcessStartInfo("kill", $"-TERM {pid}")
                       { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
                {
                    term!.WaitForExit(5_000);
                }
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var killed = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (!Directory.Exists($"/proc/{pid}"))
                    {
                        killed = true;
                        break;
                    }
                    Thread.Sleep(500);
                }
                if (!killed)
                {
                    var kline = $"stale-proc-guard: pid={pid} alive 10s after SIGTERM — SIGKILL";
                    Console.WriteLine("[g3] " + kline);
                    summary.Add(kline);
                    using var hard = Process.Start(new ProcessStartInfo("kill", $"-KILL {pid}")
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    hard!.WaitForExit(5_000);
                }
            }
            catch (Exception ex)
            {
                summary.Add($"stale-proc-guard: pid={pid} check/kill failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
        return summary.Count == 0 ? "none" : string.Join(" | ", summary);
    }

    /// <summary>
    /// PIDs holding a LISTEN socket on <paramref name="port"/> (IPv4/IPv6),
    /// read from /proc/net/tcp{,6} + inode → /proc/*/fd. Never matches a mere
    /// outbound client connection, so the current lane's own pids cannot be
    /// returned by accident.
    /// </summary>
    private static List<int> ListenersOnPort(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                var lines = File.ReadAllLines(table);
                for (var i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 10)
                        continue;
                    if (!parts[3].Equals("0A", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var local = parts[1];
                    var colon = local.LastIndexOf(':');
                    if (colon < 0)
                        continue;
                    if (!int.TryParse(local[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) || p != port)
                        continue;
                    inodes.Add(parts[9]);
                }
            }
            catch
            {
            }
        }
        var pids = new List<int>();
        if (inodes.Count == 0)
            return pids;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid))
                continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                {
                    var link = new FileInfo(fd).LinkTarget;
                    if (link == null || !link.StartsWith("socket:[", StringComparison.Ordinal))
                        continue;
                    var inode = link["socket:[".Length..].TrimEnd(']');
                    if (inodes.Contains(inode))
                    {
                        pids.Add(pid);
                        break;
                    }
                }
            }
            catch
            {
            }
        }
        return pids;
    }

    private static async Task<bool> ProbeLaneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", E2eStack.WebApiPort);
                using var bridge = new BotDriveClient(E2eStack.BridgePort);
                if (bridge.Call("{\"cmd\":\"ping\"}", 3000).GetProperty("pong").GetBoolean())
                    return true;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        return false;
    }

    private static void AdoptLaneDbPassword()
    {
        var envFile = Path.Combine(E2eStack.E2eRoot, ".env");
        foreach (var line in File.ReadAllLines(envFile))
        {
            if (!line.StartsWith("DB_PASSWORD="))
                continue;
            var pwd = line["DB_PASSWORD=".Length..].Trim();
            if (string.IsNullOrEmpty(pwd))
                throw new InvalidOperationException($"lane {envFile} has an empty DB_PASSWORD");
            typeof(E2eStack).GetProperty("DbPassword")!.SetValue(null, pwd);
            return;
        }
        throw new InvalidOperationException($"lane {envFile} has no DB_PASSWORD entry");
    }

    private static uint PollNpcObjId(BotDriveClient bridge, uint templateId, int seconds = 30)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        while (Environment.TickCount64 < deadline)
        {
            var objId = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcObjId\",\"npc\":{templateId}}}",
                30_000).GetProperty("objId").GetUInt32();
            if (objId != 0)
                return objId;
            Thread.Sleep(1000);
        }
        return 0;
    }

    private static string ReadPos(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            if (el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y) && el.TryGetProperty("z", out var z))
                return FormattableString.Invariant($"{x.GetDouble():F1},{y.GetDouble():F1},{z.GetDouble():F1}");
            return el.ToString();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
        }
    }

    private static string SummarizeWake(JsonElement wake)
    {
        try
        {
            var stepped = wake.TryGetProperty("stepped", out var s) && s.GetBoolean();
            var leg = wake.TryGetProperty("questLegActive", out var l) && l.GetBoolean();
            var reason = wake.TryGetProperty("questTravelReason", out var r) ? r.GetString() ?? "" : "";
            var accepts = GetInt(wake, "accepts");
            var active = wake.TryGetProperty("activeCount", out var a) ? a.GetInt32() : -1;
            var money = wake.TryGetProperty("money", out var m) ? m.GetInt64() : -1L;
            return $"stepped={stepped} questLegActive={leg} accepts={accepts} activeCount={active} money={money} wakeSeq={GetSeq(wake, "wakeSeq")} traceWakeSeq={GetSeq(wake, "traceWakeSeq")} snap=[{GetDbl(wake, "x"):F1},{GetDbl(wake, "y"):F1}] actor=[{GetDbl(wake, "actorX"):F1},{GetDbl(wake, "actorY"):F1}] discover={GetInt(wake, "discoverCalls")}/{GetInt(wake, "discoverCompleted")}/{GetInt(wake, "discoverRejected")} reason=[{reason}]";
        }
        catch
        {
            return "UNAVAILABLE (wake shape drift)";
        }
    }

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string SummarizeAudit(JsonElement observe)
    {
        try
        {
            var leg = observe.TryGetProperty("questLegActive", out var l) && l.GetBoolean();
            var actions = GetInt(observe, "questActionCount");
            var accepts = GetInt(observe, "accepts");
            var lastA = observe.TryGetProperty("lastAction", out var a) ? a.GetString() ?? "" : "";
            var lastR = observe.TryGetProperty("lastResult", out var r) ? r.GetString() ?? "" : "";
            var lastD = observe.TryGetProperty("lastDetail", out var d) ? d.GetString() ?? "" : "";
            return $"questLegActive={leg} questActions={actions} accepts={accepts} last={lastA}/{lastR} lastWakeSeq={GetSeq(observe, "lastWakeSeq")} discover={GetInt(observe, "discoverCalls")}/{GetInt(observe, "discoverCompleted")}/{GetInt(observe, "discoverRejected")} detail=[{lastD}]";
        }
        catch
        {
            return "UNAVAILABLE (observe shape drift)";
        }
    }

    /// <summary>
    /// Flat bot→target distance parsed from a quest-travel reason
    /// ("walking to quest target at (X,Y) from (X2,Y2)"); UNAVAILABLE when
    /// no travel target was armed. Invariant culture: server magnitudes.
    /// </summary>
    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static string MeasureTravelDistance(string travelReason)
    {
        try
        {
            var m = Regex.Match(travelReason,
                @"at \(([\d.]+),([\d.]+)\) from \(([\d.]+),([\d.]+)\)");
            if (!m.Success)
                return "UNAVAILABLE (no travel target in reason)";
            var dx = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                - double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var dy = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                - double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            return FormattableString.Invariant($"{Math.Sqrt(dx * dx + dy * dy):0.0}");
        }
        catch
        {
            return "UNAVAILABLE (reason parse failed)";
        }
    }
    private async Task WriteReportAsync(bool passed, string verdict, string failBoundary,
        string claim, object? failingCondition,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText,
        object fixture, double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "g3-quest-autonomy",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "navigation-to-giver", "talk/kill pursuit", "turn-in", "any other quest" },
                callPath = "bridge quest wake → manager Spawn/Activate + scheduler Wake → BotGoalArbiterStepExecutor (arbitrate → BotRoamStepExecutor.StepQuestLeg → QuestDecisionScenario.Run → live-actor AcceptQuest)",
                failingCondition,
                timings = new
                {
                    setupSeconds = Math.Round(setupSeconds, 1),
                    execSeconds = Math.Round(execSeconds, 1),
                    wallSeconds = Math.Round(wallSeconds, 1)
                },
                legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
                fixture,
                provenance = new
                {
                    sourceRevision = E2eStack.SourceRevision,
                    runtimeSqliteMd5 = E2eStack.RuntimeSqliteMd5(),
                    canonicalSqliteMd5 = E2eStack.CanonicalSqliteMd5
                },
                evidence = evidenceText
            };
            // K4 artifact preservation: every run writes a new timestamped file
            // (logs/<gate>-report.<utc>.json); the bare <gate>-report.json is
            // only a latest-pointer COPY refreshed here (copy, not symlink, so
            // the pointer survives stamped-file cleanup and needs no privilege).
            // A rerun therefore never destroys a prior report.
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var stampedPath = Path.Combine(EvidenceDir,
                Path.GetFileNameWithoutExtension(ReportPath) + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
            await File.WriteAllTextAsync(stampedPath, reportJson);
            File.Copy(stampedPath, ReportPath, overwrite: true);
        }
        catch
        {
            // Evidence write is best-effort; the xUnit verdict is authoritative.
        }
    }
}
