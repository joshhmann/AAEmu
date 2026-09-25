using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// G6 gate (SETTLED-START) — quest-owned first combat action (AutoAttack, never Cast).
///
/// Production chain under test:
///   scheduler wake → BotGoalArbiterStepExecutor → BotRoamStepExecutor.StepQuestLeg
///   → QuestBehavior.Run (G4 251 path + G5 pursuit proposal + G6 combat proposal)
///   → QuestObjectiveTargetSelector.Evaluate (G4 selection, reused)
///   → Target proposal (25, yields once assigned) → pursuit Stop proposal (24,
///     hold-confirm withdrawal once settled) → combat AutoAttack proposal (23)
///   → BotDecisionSelector → BotDecisionCycle.Execute → Dispatch →
///     GameplayActor.AutoAttack only.
///
/// FIXTURE: fresh bot, level 10, vitals refilled (≥0.95), quest 251 PRE-HELD
/// via POST /api/actors/accept_quest at the true giver 3512, staged at a live
/// 3475 spawner then Fixture-SAFE placed ~2 m off (inside the 3.0 m fixture
/// stop radius), standstill-settled. Setup reads ONE authoritative START
/// snapshot (charId, quest state, target ObjId/template, snapshot/actor
/// identity, flat distance ≤3.0 m, target liveness + HP, activity, CycleId +
/// counter baselines), then STARTs (scheduler wakes, then observe-only).
/// The test issues no gameplay commands itself after START (wake + observe +
/// read-only npcState HP polls only — never AutoAttack/Cast/Loot/move/kill).
///
/// Recorded-but-NOT-enforced assumptions (do not gate on unproven surface):
///   A1 skill-2 template row presence headless/live (AutoAttack fails closed
///      with "unknown auto attack skill 2" — observable, never silent);
///   A2 Fight-starter (melee role → skill 2; a RangedPhysical starter could
///      pick skill 4 with a 4.0 m minimum vs the 3.0 m hold → TooCloseRange).
/// Neither is asserted; the verdict reads only observed terminals + HP.
///
/// PASS-BEHAVIOR (all conjuncts): valid START + fresh per-bot decision +
/// target held (selection stable, CurrentTarget == selected) + combat proposal
/// selected (decide shows landed AutoAttack) + dispatch Completed
/// ("auto-attack started") + loop live (isAutoAttack) + target pinned +
/// authoritative target HP decreases on a trailing wake, then STOP (no death
/// required; a death is recorded honestly without expanding scope).
/// FAIL-BEHAVIOR/<predicate> names the first failing production predicate:
/// TARGET-NO-SELECTION / TARGET-NOT-ASSIGNED / TARGET-LOST / RANGE-NEVER-REACHED /
/// COMBAT-NO-PROPOSAL / COMBAT-NOT-DISPATCHED / COMBAT-REJECTED / COMBAT-NO-LOOP /
/// COMBAT-HP-NO-FALL / COMBAT-CAST-DETECTED / COMBAT-MULTI-ATTACK.
/// UNKNOWN/HARNESS/<predicate> (NO-FRESH-DECISION / START-RANGE / lane/setup)
/// means the behavior was never observable, never a behavior fail.
///
/// STOPS at first damage: no kill, no loot, no credit, no turn-in — meat 4058
/// unchanged, quest still Progress, turnIns 0.
[Collection("e2e")]
public class G6QuestCombatGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G6Combat" + Stamp;
    private static readonly string BotAccount = ("g6combat" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private const uint Quest251 = 251;
    private const uint GiverNpc3512 = 3512;
    private const uint BoarTemplate3475 = 3475;
    private const uint BoarMeat4058 = 4058;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;
    private const float StopRadiusM = 3.0f;
    private const float StageOffsetM = 2.0f;
    private const int CombatBudgetMs = 300_000;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "g6-first-combat-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");
    [Fact]
    [Trait("Category", "e2e")]
    public async Task FirstCombatActionAutoAttackThenStop()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G6 first-combat gate (251-selected 3475 → quest-owned AutoAttack → HP falls → stop) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        evidence.AppendLine("- assumption A1 (RECORDED, not enforced): skill-2 template row presence — AutoAttack fails closed with 'unknown auto attack skill 2' if absent");
        evidence.AppendLine("- assumption A2 (RECORDED, not enforced): Fight-starter melee role → skill 2; a RangedPhysical starter could pick skill 4 (4.0m minimum) vs the 3.0m hold");
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
        uint selectedObjId = 0;
        var distTrail = new List<double>();
        string decideTarget = "", decideStop = "", decideCombat = "";
        int meatPre = -1, meatPost = -1;
        uint ourCharacterId = 0;
        var spawnerXyz = "ABSENT";
        var stagedDist = double.NaN;
        var castLootHits = new List<string>();
        var autoAttackLandings = new List<string>();
        string stopOutcome = "", stopState = "";
        var startDistPinned = double.NaN;
        var wakeCount = 0; var wakeProven = 0;
        string firstFreshDecision = "";
        var firstFreshWake = 0;
        var selectedStable = true;
        var retrackCount = 0;
        var targetSamples = new List<string>();
        var seenTarget = false; var seenStop = false; var seenCombat = false;
        var meatDelta = -999; var moneyDelta = -999L; var creditDelta = -999;
        var hpTrail = new List<string>();
        var hpBeforeCombat = -1; var hpMinPost = -1;
        var loopLive = false; var targetPinned = false;
        var combatLandingWakes = 0;
        string lastCountedCombatDecide = "";
        var hpLastPreLanding = -1;
        var preyAliveEnd = false;
        var preyDied = false;
        var startQuestState = new E2eQuestDriver.QuestStateSnapshot(false, null, null, []);
        uint startTargetObjId = 0;
        var startSnapGap = double.NaN;
        var startActivity = "UNOBSERVED";
        string startLiveAction = "", startLiveState = "";
        var startCycleBaseline = "ABSENT";
        string startDecideBaseline = "";
        var startQuestActionCount = -1;
        var startWakeSeq = -1;
        long startMoney = -1, startTurnInsL = -1;
        var startTargetAlive = false;
        var startTargetHp = -1;
        var endDist = double.NaN;

        try
        {
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
                Assert.Fail("warm lane not answering; refusing to rebuild per workstream constraints");
            }

            AdoptLaneDbPassword();
            var bridge = new BotDriveClient(E2eStack.BridgePort);
            using var http = NewClient();
            laneBridge = bridge;

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

            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            ourCharacterId = GetUInt(enroll, "id");
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("enroll", !activeBefore,
                $"charId={ourCharacterId} activeBefore={activeBefore} (must be False)",
                enrollSw.Elapsed.TotalSeconds);
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest already active after enroll wake; re-run with a fresh account");
            }

            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{FixtureLevel}}}", 30_000);
            var refillSw = Stopwatch.StartNew();
            double hpFrac = double.NaN, mpFrac = double.NaN;
            try
            {
                bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"itemTemplate\":8518,\"count\":1}}", 30_000);
                bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"use\",\"itemTemplate\":8518}}", 30_000);
                Thread.Sleep(2000);
                bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"itemTemplate\":8519,\"count\":1}}", 30_000);
                bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"use\",\"itemTemplate\":8519}}", 30_000);
            }
            catch (Exception ex)
            {
                evidence.AppendLine($"- refill call failed ({ex.GetType().Name}: {ex.Message})");
            }
            var refillDeadline = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < refillDeadline)
            {
                try
                {
                    var cs = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}", 30_000);
                    var hp = GetInt(cs, "hp"); var maxHp = GetInt(cs, "maxHp");
                    var mp = GetInt(cs, "mp"); var maxMp = GetInt(cs, "maxMp");
                    if (maxHp > 0) hpFrac = (double)hp / maxHp;
                    if (maxMp > 0) mpFrac = (double)mp / maxMp;
                    if (hpFrac >= 0.95 && mpFrac >= 0.95)
                        break;
                }
                catch { break; }
                Thread.Sleep(1000);
            }
            var refillOk = hpFrac >= 0.95 && mpFrac >= 0.95;
            Leg("fixture-refill", refillOk, $"hpFrac={hpFrac:0.000} mpFrac={mpFrac:0.000} (REQUIRE both >=0.95)",
                refillSw.Elapsed.TotalSeconds);
            if (!refillOk)
            {
                failBoundary = "SETUP/refill-ineffective";
                Assert.Fail($"fixture refill ineffective (hpFrac={hpFrac:0.000} mpFrac={mpFrac:0.000}); refusing a recovery-triggered START");
            }

            var holdSw = Stopwatch.StartNew();
            var preHoldOk = false;
            var preHoldDetail = "";
            var giverObjId = 0u;
            try
            {
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{GiverNpc3512}}}", 60_000);
                giverObjId = PollNpcObjId(bridge, GiverNpc3512);
                if (giverObjId == 0)
                {
                    preHoldDetail = "giver 3512 never materialized (objId=0 after 30s poll)";
                }
                else
                {
                    var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest251},\"acceptorType\":\"Npc\",\"acceptorId\":{GiverNpc3512},\"idempotencyKey\":\"g6-prehold-{BotAccount}\"}}";
                    var accept = await PostJsonAsync(http, "/api/actors/accept_quest", acceptBody);
                    var acceptTrace = accept.GetProperty("trace_id").GetGuid();
                    var acceptPoll = await PollTerminalAsync(http, acceptTrace, TimeSpan.FromSeconds(30));
                    var acceptState = acceptPoll.GetProperty("state").GetString() ?? "";
                    var heldNow = acceptState == "Completed" && E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
                    preHoldOk = heldNow;
                    preHoldDetail = $"state={acceptState} active251={E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251)} giverObjId={giverObjId} trace={acceptTrace}";
                }
            }
            catch (Exception ex)
            {
                preHoldDetail = $"Q0 pre-hold threw {ex.GetType().Name}: {ex.Message}";
            }
            Leg("quest-prehold", preHoldOk, preHoldDetail, holdSw.Elapsed.TotalSeconds);
            if (!preHoldOk)
            {
                failBoundary = "SETUP/quest-prehold";
                Assert.Fail($"quest 251 Q0 pre-hold failed before settle ({preHoldDetail})");
            }

            var preStageStopSw = Stopwatch.StartNew();
            stopOutcome = "";
            stopState = "";
            try
            {
                var stopFire = await PostJsonAsync(http, "/api/actors/stop", $"{{\"bot\":\"{BotName}\"}}");
                var stopTrace = stopFire.GetProperty("trace_id").GetGuid();
                var stopPoll = await PollTerminalAsync(http, stopTrace, TimeSpan.FromSeconds(30));
                stopState = stopPoll.GetProperty("state").GetString() ?? "";
                stopOutcome = $"pre-stage Stop -> 200 trace={stopTrace} state={stopState} (clear-only, no hold)";
            }
            catch (Exception ex)
            {
                stopOutcome = $"pre-stage Stop ATTEMPT-FAILED ({ex.GetType().Name}: {ex.Message}) (clear-only; proceeding to stage)";
            }
            evidence.AppendLine($"- pre-stage stop (clear-only): {stopOutcome}");
            Leg("pre-stage-stop", true, stopOutcome, preStageStopSw.Elapsed.TotalSeconds);

            // ---- FIXTURE: stage INSIDE the 3.0 m stop radius (~2 m off) ----
            var stageSw = Stopwatch.StartNew();
            var teleportResp = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{BoarTemplate3475}}}", 60_000);
            spawnerXyz = teleportResp.TryGetProperty("x", out var stx) && teleportResp.TryGetProperty("y", out var sty) && teleportResp.TryGetProperty("z", out var stz)
                ? FormattableString.Invariant($"{stx.GetDouble():F1},{sty.GetDouble():F1},{stz.GetDouble():F1}") : "ABSENT";
            var spawnerId = teleportResp.TryGetProperty("spawnerId", out var sspEl) ? sspEl.ToString() : "ABSENT";
            var boarObjId = PollNpcObjId(bridge, BoarTemplate3475);
            var placeOk = false;
            if (boarObjId != 0 && spawnerXyz != "ABSENT")
            {
                var sp = spawnerXyz.Split(',');
                var px = double.Parse(sp[0], CultureInfo.InvariantCulture) + StageOffsetM;
                var place = bridge.Call(FormattableString.Invariant(
                    $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"place\",\"x\":{px:F1},\"y\":{sp[1]},\"z\":{sp[2]}}}"), 60_000);
                var charPos = ReadPos(bridge);
                stagedDist = FlatDist(charPos, spawnerXyz);
                placeOk = !double.IsNaN(stagedDist) && stagedDist >= 1.0 && stagedDist <= StopRadiusM;
                evidence.AppendLine($"- place RESPONSE verbatim: {place}");
            }
            Leg("combat-stage", placeOk,
                $"spawnerId={spawnerId} spawner=[{spawnerXyz}] flat={stagedDist:0.0}m (REQUIRE 1..3m) live3475={boarObjId}",
                stageSw.Elapsed.TotalSeconds);
            if (!placeOk)
            {
                failBoundary = "SETUP/stage-range";
                Assert.Fail($"bot not staged 1-3m off the 3475 spawner (flat={stagedDist:0.0}m live3475={boarObjId})");
            }

            // ---- SETTLE (standstill; the G6 START needs settled) ----
            var (settled, settleTrail) = SettleRoute(bridge, 30_000);
            evidence.AppendLine($"- pre-START settle trail: {settleTrail}");
            Leg("settle", settled, $"settled={settled} trail=[{settleTrail}]", 0);
            if (!settled)
            {
                failBoundary = "SETUP/settle";
                Assert.Fail($"bot never stood still before START (trail=[{settleTrail}])");
            }

            // ---- AUTHORITATIVE START SNAPSHOT (read ONCE, then START immediately) ----
            var startSnapSw = Stopwatch.StartNew();
            meatPre = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var preStart = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            startQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            startTargetObjId = (uint)boarObjId;
            var startPosA = ReadPos(bridge);
            startDistPinned = FlatDist(startPosA, spawnerXyz);
            startDecideBaseline = GetStr(preStart, "questDecideDetail");
            startCycleBaseline = Regex.Match(startDecideBaseline, @"cycle=([^\s\]]+)").Success
                ? Regex.Match(startDecideBaseline, @"cycle=([^\s\]]+)").Groups[1].Value : "ABSENT";
            startActivity = GetStr(preStart, "arbiterActivity");
            if (string.IsNullOrEmpty(startActivity)) startActivity = "FIELD-ABSENT";
            startLiveAction = GetStr(preStart, "liveAction");
            startLiveState = GetStr(preStart, "liveState");
            startSnapGap = FlatDist(
                FormattableString.Invariant($"{GetDbl(preStart, "x"):F1},{GetDbl(preStart, "y"):F1}"),
                FormattableString.Invariant($"{GetDbl(preStart, "actorX"):F1},{GetDbl(preStart, "actorY"):F1}"));
            startQuestActionCount = GetInt(preStart, "questActionCount");
            startWakeSeq = GetInt(preStart, "wakeSeq");
            startMoney = preStart.TryGetProperty("money", out var smEl) && smEl.ValueKind == JsonValueKind.Number && smEl.TryGetInt64(out var smStart) ? smStart : -1L;
            startTurnInsL = GetInt(preStart, "turnIns");
            startTargetAlive = startTargetObjId != 0;
            startTargetHp = ReadNpcHp(bridge, startTargetObjId);
            evidence.AppendLine($"- START snapshot: charId={ourCharacterId} quest251 active={startQuestState.Active} step={startQuestState.Step} status={startQuestState.Status} objectives=[{string.Join(",", startQuestState.Objectives)}]");
            evidence.AppendLine($"- START snapshot: target objId={startTargetObjId} template={BoarTemplate3475} alive={startTargetAlive} hp={startTargetHp} spawner=[{spawnerXyz}]");
            evidence.AppendLine($"- START snapshot: snapActorGap={startSnapGap:0.0}m pos=[{startPosA}] flatToPinned={startDistPinned:0.0}m activity=[{startActivity}] live={startLiveAction}/{startLiveState}");
            evidence.AppendLine($"- START snapshot: cycleBaseline=[{startCycleBaseline}] questActionCount={startQuestActionCount} wakeSeq={startWakeSeq} meat4058={meatPre} money={startMoney} turnIns={startTurnInsL}");
            var startRangeOk = !double.IsNaN(startDistPinned) && startDistPinned <= StopRadiusM;
            var startIdentityOk = !double.IsNaN(startSnapGap) && startSnapGap < 0.5;
            Leg("start-snapshot", startRangeOk && startIdentityOk,
                $"charId={ourCharacterId} active251={startQuestState.Active} target={startTargetObjId}/t{BoarTemplate3475} hp={startTargetHp} gap={startSnapGap:0.0}m (REQUIRE <0.5) flat={startDistPinned:0.0}m (REQUIRE <={StopRadiusM:0.0}) activity=[{startActivity}] cycle=[{startCycleBaseline}] meat={meatPre}",
                startSnapSw.Elapsed.TotalSeconds);
            if (!startRangeOk || !startIdentityOk)
            {
                failBoundary = "HARNESS/START-RANGE";
                Assert.Fail($"START outside in-range+identity window (flat={startDistPinned:0.0}m gap={startSnapGap:0.0}m); refusing START");
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- START: bounded wake loop, observe ONLY (no gameplay commands) ----
            var gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            var gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            var execSw = Stopwatch.StartNew();
            var decideHistory = new List<string>();
            var liveHistory = new List<string>();
            var posTrail = new List<string> { startPosA };
            var deadline = Environment.TickCount64 + CombatBudgetMs;
            JsonElement observe = preStart;
            // K1 failfast state (cold-lane only): counts consecutive wakes with a
            // byte-identical questDecideDetail and no per-bot step signal. Fires
            // at 2; gated on no fresh decision ever, no landed combat, and no HP
            // fall, so a warm PASS (fresh by wake 1-2) can never trip it. Budgets
            // stay ceilings.
            var prevDecide = startDecideBaseline;
            var idleWakes = 0;
            string idleExitBoundary = "";
            var startTraceWakeSeq = GetSeq(preStart, "traceWakeSeq");
            var startMaxTraceWakeSeq = GetSeq(preStart, "maxTraceWakeSeq");
            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    observe = bridge.Call(
                        $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                }
                catch (Exception ex)
                {
                    evidence.AppendLine($"- wake call failed ({ex.GetType().Name}); retrying");
                    Thread.Sleep(2000);
                    continue;
                }
                if (ourCharacterId == 0)
                    ourCharacterId = GetUInt(observe, "id");
                try
                {
                    observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                }
                catch { /* keep wake response */ }
                var decide = GetStr(observe, "questDecideDetail");
                if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                    decideHistory.Add(decide);
                if (string.IsNullOrEmpty(firstFreshDecision) && !string.IsNullOrEmpty(decide) && !string.Equals(decide, startDecideBaseline, StringComparison.Ordinal))
                {
                    firstFreshDecision = decide;
                    firstFreshWake = wakeCount + 1;
                }
                var live = $"liveAction={GetStr(observe, "liveAction")} liveState={GetStr(observe, "liveState")} lastAction={GetStr(observe, "lastAction")}";
                if (liveHistory.Count == 0 || liveHistory[^1] != live)
                    liveHistory.Add(live);
                posTrail.Add(ReadPos(bridge));
                wakeCount++;
                if (!string.IsNullOrEmpty(firstFreshDecision))
                    wakeProven++;

                var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                foreach (var f in funnels)
                {
                    if (distTrail.Count == 0 || Math.Abs(distTrail[^1] - f.Dist) > 1e-9)
                        distTrail.Add(f.Dist);
                    if (selectedObjId == 0 && f.Selected != 0)
                        selectedObjId = f.Selected;
                    else if (f.Selected != 0 && selectedObjId != 0 && f.Selected != selectedObjId)
                    {
                        selectedStable = false;
                        retrackCount++;
                    }
                    if (targetSamples.Count < 40)
                        targetSamples.Add($"wake={wakeCount} selected={f.Selected} distM={f.Dist:0.0} pursuit=[{f.Pursuit}] combat=[{f.Combat}]");
                }
                var landedTarget = Regex.Match(decide, @"landed Target \(targeting (\d+)\)");
                if (landedTarget.Success)
                {
                    seenTarget = true;
                    decideTarget = decide;
                    var landedId = uint.Parse(landedTarget.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (selectedObjId == 0)
                        selectedObjId = landedId;
                }
                if (decide.Contains("landed Stop", StringComparison.Ordinal))
                {
                    seenStop = true;
                    decideStop = decide;
                }
                var landedCombat = Regex.Match(decide, @"landed AutoAttack \(([^)]*)\)");
                if (landedCombat.Success && !string.Equals(decide, lastCountedCombatDecide, StringComparison.Ordinal))
                {
                    lastCountedCombatDecide = decide;
                    seenCombat = true;
                    decideCombat = decide;
                    combatLandingWakes++;
                    if (hpBeforeCombat < 0)
                        hpBeforeCombat = hpLastPreLanding >= 0 ? hpLastPreLanding : ReadNpcHp(bridge, selectedObjId != 0 ? selectedObjId : startTargetObjId);
                }
                else if (landedCombat.Success)
                {
                    seenCombat = true;
                }
                ScanCombatExclusion(decide, live, castLootHits);
                if (GetBool(observe, "isAutoAttack"))
                    loopLive = true;
                if (selectedObjId != 0 && GetUInt(observe, "currentTargetObjId") == selectedObjId)
                    targetPinned = true;
                var hpNow = ReadNpcHp(bridge, selectedObjId != 0 ? selectedObjId : startTargetObjId);
                if (hpNow >= 0)
                {
                    if (!seenCombat)
                        hpLastPreLanding = hpNow;
                    if (hpTrail.Count == 0 || hpTrail[^1] != $"wake={wakeCount} hp={hpNow}")
                        hpTrail.Add($"wake={wakeCount} hp={hpNow}");
                    if (hpMinPost < 0 || hpNow < hpMinPost)
                        hpMinPost = hpNow;
                    if (hpNow == 0)
                        preyDied = true;
                }

                // DONE: combat landed + loop live + target pinned + HP fell, then STOP.
                if (seenCombat && loopLive && targetPinned && hpBeforeCombat > 0 && hpMinPost >= 0 && hpMinPost < hpBeforeCombat)
                    break;
                // K1 failfast (cold-lane only): 2 consecutive wakes with unchanged
                // questDecideDetail and HasPerBotStepSignal == false end the window
                // now with the frozen G3/G4 boundary name — no new strings. The
                // firstZero=sweep-empty payload form maps to DISCOVERY/sweep-empty,
                // anything else to ARBITRATION/no-quest-activity. Unreachable on a
                // warm PASS (fresh decision by wake 1-2 keeps firstFreshDecision
                // non-empty; landed combat or fallen HP gate it out below).
                // Placed after the DONE break so the PASS path precedes it.
                if (string.IsNullOrEmpty(firstFreshDecision) && !seenCombat
                    && !(startTargetHp > 0 && hpMinPost >= 0 && hpMinPost < startTargetHp)
                    && string.Equals(decide, prevDecide, StringComparison.Ordinal)
                    && !HasPerBotStepSignal(observe, ourCharacterId, startQuestActionCount, startWakeSeq, startTraceWakeSeq, startMaxTraceWakeSeq))
                    idleWakes++;
                else
                    idleWakes = 0;
                prevDecide = decide;
                if (idleWakes >= 2)
                {
                    var fz = Regex.Match(decide, @"firstZero=([a-z-]+)");
                    idleExitBoundary = fz.Success && fz.Groups[1].Value == "sweep-empty"
                        ? "DISCOVERY/sweep-empty"
                        : "ARBITRATION/no-quest-activity";
                    evidence.AppendLine($"- K1 idle-window exit after {wakeCount} wakes ({idleWakes} consecutive identical-decide no-signal wakes): {idleExitBoundary}");
                    break;
                }
                Thread.Sleep(2000);
            }
            // Count AutoAttack landings across the live log too (observe-side can
            // miss a single-cycle landing); exactly one is the G6 contract.
            try
            {
                if (File.Exists(GameLogPath))
                {
                    using var liveFs = new FileStream(GameLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (liveFs.Length >= gameLogOffset)
                    {
                        liveFs.Seek(gameLogOffset, SeekOrigin.Begin);
                        using var liveSr = new StreamReader(liveFs);
                        string? liveLine;
                        while ((liveLine = liveSr.ReadLine()) != null)
                        {
                            if (!liveLine.Contains("QuestSweepDiag", StringComparison.Ordinal)
                                || !liveLine.Contains($"char={ourCharacterId}", StringComparison.Ordinal))
                                continue;
                            var dm = Regex.Match(liveLine, @"decide=\[([^\]]*)\]");
                            if (!dm.Success)
                                continue;
                            var liveDecide = dm.Groups[1].Value;
                            var liveCombat = Regex.Match(liveDecide, @"landed AutoAttack \(([^)]*)\)");
                            // Dedupe against observe-side: the same landing is
                            // visible in both surfaces (same core detail). Only
                            // a core never seen on observe-side counts extra.
                            var seenCores = new HashSet<string>(decideHistory
                                .Select(d => Regex.Match(d, @"landed AutoAttack \(([^)]*)\)"))
                                .Where(m => m.Success)
                                .Select(m => m.Groups[1].Value));
                            if (liveCombat.Success && !seenCores.Contains(liveCombat.Groups[1].Value) && !autoAttackLandings.Contains(liveDecide))
                                autoAttackLandings.Add(liveDecide);
                            var lt = Regex.Match(liveDecide, @"landed Target \(targeting (\d+)\)");
                            if (lt.Success && !seenTarget)
                            {
                                seenTarget = true;
                                decideTarget = liveDecide + " (live-log supplement)";
                                if (selectedObjId == 0)
                                    selectedObjId = uint.Parse(lt.Groups[1].Value, CultureInfo.InvariantCulture);
                            }
                            if (liveDecide.Contains("landed Stop", StringComparison.Ordinal) && !seenStop)
                            {
                                seenStop = true;
                                decideStop = liveDecide + " (live-log supplement)";
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best-effort scoring supplement.
            }
            execSeconds = execSw.Elapsed.TotalSeconds;
            evidence.AppendLine($"- first fresh decision (vs START cycle baseline [{startCycleBaseline}]): {(string.IsNullOrEmpty(firstFreshDecision) ? "NONE" : $"wake#{firstFreshWake} [{firstFreshDecision}]")}");
            evidence.AppendLine($"- decide history ({decideHistory.Count}):");
            foreach (var d in decideHistory.Take(20))
                evidence.AppendLine($"  [{d}]");
            evidence.AppendLine($"- live history ({liveHistory.Count}): {string.Join(" | ", liveHistory.Take(30))}");
            evidence.AppendLine($"- pos trail: {string.Join(" -> ", posTrail.Take(40))}");
            evidence.AppendLine($"- funnel dist trail: {string.Join(" -> ", distTrail.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))}");
            evidence.AppendLine($"- target samples ({targetSamples.Count}):");
            foreach (var s in targetSamples.Take(40))
                evidence.AppendLine($"  [{s}]");
            evidence.AppendLine($"- combat: seenTarget={seenTarget} seenStop={seenStop} seenCombat={seenCombat} landingWakes={combatLandingWakes} liveLogLandings={autoAttackLandings.Count} loopLive={loopLive} targetPinned={targetPinned}");
            evidence.AppendLine($"- HP trail: {string.Join(" | ", hpTrail.Take(30))} (beforeCombat={hpBeforeCombat} minPost={hpMinPost} preyDied={preyDied})");
            foreach (var l in autoAttackLandings.Take(5))
                evidence.AppendLine($"  [live-log AutoAttack: {l}]");

            // ---- POST proofs (read-only): meat, quest state, prey, log combat scan ----
            meatPost = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var questStillActive = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            var postObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            var turnIns = GetInt(postObs, "turnIns");
            var moneyPost = postObs.TryGetProperty("money", out var mpEl) && mpEl.ValueKind == JsonValueKind.Number && mpEl.TryGetInt64(out var mpPost) ? mpPost : -1L;
            preyAliveEnd = PollNpcObjId(bridge, BoarTemplate3475, 5) != 0;
            ScanCombatExclusion(GetStr(postObs, "questDecideDetail"), $"lastAction={GetStr(postObs, "lastAction")}", castLootHits);
            var logCastLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
            castLootHits.AddRange(logCastLoot);
            var totalAutoAttackLandings = combatLandingWakes + autoAttackLandings.Count;
            meatDelta = meatPre >= 0 && meatPost >= 0 ? meatPost - meatPre : -999;
            moneyDelta = startMoney >= 0 && moneyPost >= 0 ? moneyPost - startMoney : -999L;
            creditDelta = (turnIns - startTurnInsL) != 0 || (questStillActive != startQuestState.Active) ? 1 : 0;
            evidence.AppendLine($"- exclusion scan: castLootHits={(castLootHits.Count == 0 ? "NONE" : string.Join("; ", castLootHits.Take(10)))} totalAutoAttackLandings~{totalAutoAttackLandings}");
            evidence.AppendLine($"- side effects: meatDelta={meatDelta} moneyDelta={moneyDelta} creditDelta={creditDelta} (turnIns {startTurnInsL}->{turnIns}, active {startQuestState.Active}->{questStillActive}, preyAliveEnd={preyAliveEnd} preyDied={preyDied})");

            endDist = distTrail.Count > 0 ? distTrail[^1] : double.NaN;
            var rangeOk = !double.IsNaN(endDist) && endDist <= StopRadiusM;
            var freshDecision = !string.IsNullOrEmpty(firstFreshDecision);
            var hpFell = hpBeforeCombat > 0 && hpMinPost >= 0 && hpMinPost < hpBeforeCombat;
            if (string.IsNullOrEmpty(firstFreshDecision) && !string.IsNullOrEmpty(idleExitBoundary))
            {
                // K1 idle-window exit: the window ended early (2 consecutive
                // identical-decide no-signal wakes) with the frozen G3/G4 boundary
                // name — same name the spent-budget path would converge on, only
                // an earlier timestamp. Full-budget HARNESS/NO-FRESH-DECISION below
                // is untouched for non-idle exits.
                verdict = "FAIL-BEHAVIOR";
                failBoundary = idleExitBoundary;
                failingCondition = new { boundary = failBoundary, wakeCount, idleWakes, startCycleBaseline, interpretation = "K1 idle-window exit: 2 consecutive wakes with byte-identical questDecideDetail and no per-bot step signal; window ended early with the frozen boundary name, budget kept as ceiling" };
            }
            else if (!freshDecision)
            {
                verdict = "UNKNOWN/HARNESS/NO-FRESH-DECISION";
                failBoundary = "HARNESS/NO-FRESH-DECISION";
                failingCondition = new { boundary = failBoundary, wakeCount, startCycleBaseline, interpretation = "no quest tick ran for OUR bot after START; behavior unobservable, never a behavior fail" };
            }
            else if (!seenTarget || selectedObjId == 0)
            {
                verdict = "FAIL-BEHAVIOR/TARGET-NO-SELECTION";
                failBoundary = "TARGET-NO-SELECTION";
                failingCondition = new { boundary = failBoundary, interpretation = "production G4 leg never selected a live 3475 (or never assigned it)" };
            }
            else if (string.IsNullOrEmpty(decideTarget) || !decideTarget.Contains($"targeting {selectedObjId}", StringComparison.Ordinal))
            {
                verdict = "FAIL-BEHAVIOR/TARGET-NOT-ASSIGNED";
                failBoundary = "TARGET-NOT-ASSIGNED";
                failingCondition = new { boundary = failBoundary, selectedObjId, decideTarget, interpretation = "selection exists but SetTarget never landed it" };
            }
            else if (!selectedStable)
            {
                verdict = "FAIL-BEHAVIOR/TARGET-LOST";
                failBoundary = "TARGET-LOST";
                failingCondition = new { boundary = failBoundary, selectedObjId, retrackCount, interpretation = "funnel selection changed mid-run (commitment broken)" };
            }
            else if (!rangeOk)
            {
                verdict = "FAIL-BEHAVIOR/RANGE-NEVER-REACHED";
                failBoundary = "RANGE-NEVER-REACHED";
                failingCondition = new { boundary = failBoundary, endDist, stopRadiusM = StopRadiusM, interpretation = "final funnel distance above the stop radius" };
            }
            else if (!seenCombat)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-NO-PROPOSAL";
                failBoundary = "COMBAT-NO-PROPOSAL";
                failingCondition = new { boundary = failBoundary, selectedObjId, seenStop, endDist, interpretation = "in-range + assigned + settled but no AutoAttack proposal won a wake" };
            }
            else if (!loopLive)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-NO-LOOP";
                failBoundary = "COMBAT-NO-LOOP";
                failingCondition = new { boundary = failBoundary, decideCombat, interpretation = "AutoAttack landed but the loop never read live" };
            }
            else if (!targetPinned)
            {
                verdict = "FAIL-BEHAVIOR/TARGET-LOST";
                failBoundary = "TARGET-LOST";
                failingCondition = new { boundary = failBoundary, selectedObjId, interpretation = "loop live but CurrentTarget != selected" };
            }
            else if (!hpFell)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-HP-NO-FALL";
                failBoundary = "COMBAT-HP-NO-FALL";
                failingCondition = new { boundary = failBoundary, hpBeforeCombat, hpMinPost, hpTrail = hpTrail.Take(20).ToList(), interpretation = "loop live + pinned but authoritative target HP never decreased" };
            }
            else if (castLootHits.Count != 0)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-CAST-DETECTED";
                failBoundary = "COMBAT-CAST-DETECTED";
                failingCondition = new { boundary = failBoundary, hits = castLootHits.Take(10).ToList(), interpretation = "Cast/Loot record observed — AutoAttack-only violated" };
            }
            else if (totalAutoAttackLandings != 1)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-MULTI-ATTACK";
                failBoundary = "COMBAT-MULTI-ATTACK";
                failingCondition = new { boundary = failBoundary, totalAutoAttackLandings, combatLandingWakes, liveLogLandings = autoAttackLandings.Count, interpretation = "expected exactly one landed AutoAttack (no re-dispatch once live)" };
            }
            else if (meatDelta != 0 || !questStillActive || turnIns != 0)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-CREDIT-LEAK";
                failBoundary = "COMBAT-CREDIT-LEAK";
                failingCondition = new { boundary = failBoundary, meatPre, meatPost, questStillActive, turnIns, interpretation = "loot/credit proxy moved (meat delta, quest inactive, or turn-in recorded)" };
            }
            else
            {
                verdict = "PASS-BEHAVIOR";
                failBoundary = "none";
                passed = true;
                claim = $"from 251-ACTIVE + G4-selected live 3475 {selectedObjId} at {endDist:0.0}m, autonomy landed one AutoAttack [{decideCombat}], loop live + target pinned, HP {hpBeforeCombat}->{hpMinPost}{(preyDied ? " (prey died; recorded, scope unchanged)" : "")}, meat {meatPre}->{meatPost}, quest still Progress — and stops there";
                failingCondition = new
                {
                    boundary = "none",
                    selected = selectedObjId,
                    endDist,
                    hpBeforeCombat,
                    hpMinPost,
                    preyDied,
                    meatPre,
                    meatPost,
                    questStillActive,
                    turnIns,
                    preyAliveEnd
                };
            }
            Leg("gate", passed, $"{verdict} at {failBoundary}: selected={selectedObjId} hp={hpBeforeCombat}->{hpMinPost}",
                execSeconds);
            Assert.True(passed, $"G6 {verdict} at {failBoundary}: selected={selectedObjId}");
        }
        finally
        {
            totalWall.Stop();
            string cleanupSummary;
            try
            {
                try { session?.Dispose(); } catch { }
                cleanupSummary = TryReleaseBot(laneBridge, BotName, ourCharacterId);
            }
            catch (Exception ex)
            {
                cleanupSummary = $"UNAVAILABLE (release threw {ex.GetType().Name}: {ex.Message})";
            }
            try
            {
                await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, legs, evidence.ToString(),
                    new
                    {
                        quest = Quest251,
                        giverNpc = GiverNpc3512,
                        preyTemplate = BoarTemplate3475,
                        preyItem = BoarMeat4058,
                        selectedObjId,
                        spawner = spawnerXyz,
                        stagedDistM = stagedDist,
                        stopOutcome,
                        stopState,
                        start = new
                        {
                            charId = ourCharacterId,
                            questActive = startQuestState.Active,
                            questStep = startQuestState.Step,
                            questStatus = startQuestState.Status,
                            questObjectives = startQuestState.Objectives,
                            targetObjId = startTargetObjId,
                            targetTemplate = BoarTemplate3475,
                            targetAlive = startTargetAlive,
                            targetHp = startTargetHp,
                            snapActorGapM = startSnapGap,
                            flatToPinnedM = startDistPinned,
                            activity = startActivity,
                            live = startLiveAction + "/" + startLiveState,
                            cycleBaseline = startCycleBaseline,
                            decideBaseline = startDecideBaseline,
                            questActionCount = startQuestActionCount,
                            wakeSeq = startWakeSeq,
                            meatPre,
                            money = startMoney,
                            turnIns = startTurnInsL
                        },
                        combat = new
                        {
                            firstFreshDecision,
                            firstFreshWake,
                            decideTarget,
                            decideStop,
                            decideCombat,
                            seenTarget,
                            seenStop,
                            seenCombat,
                            landingWakes = combatLandingWakes,
                            liveLogLandings = autoAttackLandings,
                            loopLive,
                            targetPinned,
                            selectedStable,
                            retrackCount,
                            distTrail,
                            targetSamples,
                            hpTrail,
                            hpBeforeCombat,
                            hpMinPost,
                            preyDied,
                            preyAliveEnd
                        },
                        range = new
                        {
                            stopRadiusM = StopRadiusM,
                            finalDistM = endDist
                        },
                        sideEffects = new
                        {
                            castLootHits = castLootHits.Count,
                            autoAttackLandings = combatLandingWakes + autoAttackLandings.Count,
                            item4058Delta = meatDelta,
                            creditDelta,
                            moneyDelta,
                            meatPre,
                            meatPost
                        },
                        assumptions = new
                        {
                            skill2 = "RECORDED-only: skill-2 template row presence; failure would read 'unknown auto attack skill 2'",
                            starter = "RECORDED-only: melee (Fight) role → skill 2; RangedPhysical could pick skill 4 (4.0m min)"
                        },
                        wakeCount,
                        wakeProven,
                        meatPre,
                        meatPost,
                        ourCharacterId,
                        cleanupSummary
                    },
                    setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(EvidenceDir);
                    File.WriteAllText(ReportPath + ".fallback.json",
                        $"{{\"scenario\":\"g6-first-combat\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"G6-GATE verdict={verdict} boundary={failBoundary} selected={selectedObjId} " +
                $"hp={hpBeforeCombat}>{hpMinPost} meat={meatPre}>{meatPost} castLoot={castLootHits.Count} legs={legs.Count}");
        }
    }

    // ---- funnel evidence (server-side, game log) ----
    private sealed record FunnelLine(
        string Cycle, bool ObjectiveOk, string Objective, bool SourceOk, string Source,
        bool Relevance, int Have, int Need, int Raw, int Relevant, int Legal,
        uint Selected, uint Template, double Dist, string Rejects, string CandidatesRaw, string Pursuit, string Combat, string Summary);

    private static List<FunnelLine> ReadFunnels(long gameLogOffset, long gameRestartLogOffset, uint charId)
    {
        var lines = new List<string>();
        foreach (var (path, offset) in new[] { (GameLogPath, gameLogOffset), (GameRestartLogPath, gameRestartLogOffset) })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < offset)
                    continue;
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Contains("QuestObjectiveTargetDiag", StringComparison.Ordinal)
                        && line.Contains($"char={charId}", StringComparison.Ordinal))
                        lines.Add(line);
                }
            }
            catch
            {
            }
        }
        var out_ = new List<FunnelLine>();
        foreach (var line in lines)
        {
            var parsed = ParseFunnel(line);
            if (parsed != null)
                out_.Add(parsed);
        }
        return out_;
    }

    private static FunnelLine? ParseFunnel(string line)
    {
        try
        {
            string Field(string name)
            {
                var m = Regex.Match(line, $@"\b{name}=(\S+)");
                return m.Success ? m.Groups[1].Value : "";
            }
            string Block(string name)
            {
                var m = Regex.Match(line, $@"\b{name}=\[(.*?)\]");
                return m.Success ? m.Groups[1].Value : "";
            }
            var objective = Block("objective");
            var source = Block("source");
            var rejects = Block("rejects");
            var candidates = Block("candidates");
            var pursuit = Block("pursuit");
            var combat = Block("combat");
            static int Int(string v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
            static uint UInt(string v) => uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var selected = Field("selected");
            var template = Field("template");
            var distRaw = Field("distM");
            return new FunnelLine(
                Field("cycle"), objective.StartsWith("ok:", StringComparison.Ordinal), objective,
                source.StartsWith("ok:", StringComparison.Ordinal), source,
                Field("relevance") == "true", Int(Field("have")), Int(Field("need")),
                Int(Field("raw")), Int(Field("relevant")), Int(Field("legal")),
                selected == "-" ? 0 : UInt(selected), template == "-" ? 0 : UInt(template),
                double.TryParse(distRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
                rejects, candidates, pursuit, combat,
                $"cycle={Field("cycle")} selected={selected} distM={distRaw} pursuit=[{pursuit}] combat=[{combat}]");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// G6 inverted exclusion scan: Cast/Loot always void the run (AutoAttack is
    /// the only legal combat verb — counted separately, exactly once).
    /// </summary>
    private static void ScanCombatExclusion(string decide, string live, List<string> hits)
    {
        foreach (var text in new[] { decide, live })
        {
            if (string.IsNullOrEmpty(text))
                continue;
            var m = Regex.Match(text, @"landed (Cast|Loot)\b|(liveAction=(Cast))");
            if (m.Success && !hits.Contains(text))
                hits.Add(text);
        }
    }

    private static List<string> ScanGameLogCastLoot(long gameLogOffset, long gameRestartLogOffset, uint charId, string botName)
    {
        var hits = new List<string>();
        foreach (var (path, offset) in new[] { (GameLogPath, gameLogOffset), (GameRestartLogPath, gameRestartLogOffset) })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < offset)
                    continue;
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if ((line.Contains($"char={charId}", StringComparison.Ordinal)
                            || line.Contains(botName, StringComparison.Ordinal))
                        && Regex.IsMatch(line, @"\b(Cast|Loot)\b")
                        && !line.Contains("AutoAttack", StringComparison.Ordinal)
                        && !line.Contains("StopAutoAttack", StringComparison.Ordinal))
                        hits.Add(line.Length > 220 ? line[..220] : line);
                    if (hits.Count >= 10)
                        break;
                }
            }
            catch
            {
            }
        }
        return hits;
    }

    private static int ReadNpcHp(BotDriveClient bridge, uint objId)
    {
        try
        {
            var el = objId != 0
                ? bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000)
                : bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npc\":{BoarTemplate3475}}}", 30_000);
            return el.TryGetProperty("hp", out var v) && v.TryGetInt32(out var n) ? n : -1;
        }
        catch
        {
            return -1;
        }
    }

    // ---- helpers (adopt-only; observe-only gate) ----
    private static (bool Settled, string Trail) SettleRoute(BotDriveClient bridge, int budgetMs)
    {
        var trail = new StringBuilder(ReadPos(bridge));
        var prev = trail.ToString();
        var settledNow = false;
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(2000);
            var cur = ReadPos(bridge);
            trail.Append(" -> ").Append(cur);
            if (FlatDist(prev, cur) < 0.5)
            {
                settledNow = true;
                prev = cur;
                break;
            }
            prev = cur;
        }
        var rearmed = false;
        if (settledNow)
        {
            Thread.Sleep(5000);
            var conf = ReadPos(bridge);
            trail.Append(" -> ").Append(conf);
            rearmed = !(FlatDist(prev, conf) < 1.0);
        }
        return (settledNow && !rearmed, trail.ToString());
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

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {path} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PollTerminalAsync(HttpClient client, Guid traceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement.Clone();
                var state = root.GetProperty("state").GetString();
                if (state is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                    return root;
            }
            await Task.Delay(300);
        }
        throw new TimeoutException($"trace {traceId} never reached a terminal state within {timeout}");
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

    private static double FlatDist(string a, string b)
    {
        try
        {
            var pa = a.Split(',');
            var pb = b.Split(',');
            var dx = double.Parse(pa[0], CultureInfo.InvariantCulture) - double.Parse(pb[0], CultureInfo.InvariantCulture);
            var dy = double.Parse(pa[1], CultureInfo.InvariantCulture) - double.Parse(pb[1], CultureInfo.InvariantCulture);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        catch
        {
            return double.NaN;
        }
    }

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// K1 per-bot step proof for OUR characterId (G3 HasPerBotStepSignal
    /// vocabulary, copied semantics — never GLOBAL scheduler counters):
    /// questLegActive true, non-empty questTravelReason, quest-action audit
    /// rows above the START baseline, or per-bot wake-sequence growth above
    /// the START baselines (wakeSeq / traceWakeSeq / maxTraceWakeSeq). Absent
    /// fields read 0 and stay inert. False across consecutive wakes means no
    /// quest tick ran for OUR bot.
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

    private static bool GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

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
                Console.WriteLine("[g6] " + line);
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
                    Console.WriteLine("[g6] " + kline);
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
                return $"UNAVAILABLE (bridge deactivate refused for networked char '{botName}' charId={characterId}: {err.GetString()}; TCP disposed, registry entry left in place)";
            return $"UNAVAILABLE (bridge deactivate unexpected shape for networked char '{botName}' charId={characterId}: {res}; TCP disposed, registry entry left in place)";
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE (bridge deactivate threw {ex.GetType().Name} for networked char '{botName}' charId={characterId}; TCP disposed, registry entry left in place)";
        }
    }

    private async Task WriteReportAsync(bool passed, string verdict, string failBoundary, string claim, object? failingCondition,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText, object fixture,
        double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "g6-first-combat-settled-start",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "kill", "loot", "objective-credit", "turn-in", "rotation", "any other quest" },
                callPath = "bridge quest wake → manager Spawn/Activate + scheduler Wake → BotGoalArbiterStepExecutor (arbitrate → BotRoamStepExecutor.StepQuestLeg → QuestBehavior.Run G4 251 path + G5 pursuit proposal + G6 combat proposal → QuestObjectiveTargetSelector.Evaluate (G4 selection, reused production path) → Target proposal (25, yields once assigned) → pursuit Stop proposal (24, hold-confirm withdrawal once settled) → combat AutoAttack proposal (23) → BotDecisionCycle.Execute → GameplayActor.AutoAttack)",
                g4Leg = "reused (production G4 path selects the 3475 ObjId; the gate stages only position/vitals/quest-state, never a selection)",
                g5Leg = "reused (production G5 pursuit closes/holds; the gate starts inside 3.0m settled)",
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
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
            var stampedPath = Path.Combine(EvidenceDir,
                Path.GetFileNameWithoutExtension(ReportPath) + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
            await File.WriteAllTextAsync(stampedPath, reportJson);
            File.Copy(stampedPath, ReportPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"G6-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
