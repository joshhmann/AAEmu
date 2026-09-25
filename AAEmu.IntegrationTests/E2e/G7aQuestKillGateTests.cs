using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// G7a gate (KILL-ONLY) — quest-owned loop sustains to authoritative prey death,
/// then self-terminates. No loot, no credit, no turn-in.
///
/// Production chain under test: the G6 end state (251 ACTIVE, G4-selected live
/// 3475, quest-owned AutoAttack loop live via CombatProposal, target
/// damaged-but-alive) sustained observe-only until engine death
/// (Unit.ReduceCurrentHp → PostUpdateCurrentHp → Unit.DoDie, Hp<=0) plus loop
/// teardown (UseAutoAttackSkillTask next-tick self-termination → StopAutoAttack
/// → IsAutoAttack false; CombatProposal target-dead withdrawal blocks
/// re-dispatch into the corpse).
///
/// FIXTURE: fresh bot, level 10, vitals refilled (≥0.95), quest 251 PRE-HELD
/// via POST /api/actors/accept_quest at the true giver 3512, staged at a live
/// 3475 spawner then Fixture-SAFE placed ~2 m off (inside the 3.0 m fixture
/// stop radius), standstill-settled — then DRIVEN (scheduler wakes, observe
/// only) to the G6 end state: loop live + target pinned + HP fallen but target
/// still alive. ONE authoritative START snapshot is read at that point
/// (charId, quest state, target ObjId/template/HP-damaged, snapshot/actor
/// identity, flat distance ≤3.0 m, loop live + pinned, CycleId baseline, meat
/// 4058, money, turnIns), then the KILL watch runs observe-only to death plus
/// a TEARDOWN watch to IsAutoAttack false. The test issues no gameplay
/// commands itself after START (wake + observe + read-only npcState HP polls
/// only — never AutoAttack/Cast/Loot/move/kill).
///
/// PASS-BEHAVIOR (all conjuncts): valid G6-end-state START + OUR target objId
/// reaches authoritative dead (npcState Hp==0; later objId-gone reads as
/// despawn corroboration) + loop self-terminates (IsAutoAttack false observed
/// post-death, no post-death AutoAttack landing naming OUR objId) + quest-side
/// target-dead withdrawal visible (combat diag validate=target-dead) +
/// meat/money/credit/turn-ins unchanged (death alone grants nothing for 251).
/// FAIL-BEHAVIOR/<predicate> names the first failing production predicate:
/// COMBAT/no-kill (timeout without authoritative death) / TARGET/lost (OUR
/// objId gone while last-HP>0, or funnel selection moved pre-death) /
/// LOOP/not-torn-down (IsAutoAttack stuck live past the teardown budget, or a
/// post-death AutoAttack landed on OUR corpse) / COMBAT-CAST-DETECTED /
/// COMBAT-CREDIT-LEAK. UNKNOWN/HARNESS/<predicate> (NO-FRESH-DECISION /
/// START-RANGE / fixture-overshoot / leash-reset / lane/setup) means the
/// behavior was never observable, never a behavior fail.
///
/// STOPS at death+teardown: no loot, no credit, no turn-in — meat 4058
/// unchanged, quest still Progress, turnIns 0. G7c owns loot even on PASS.
[Collection("e2e")]
public class G7aQuestKillGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G7aKill" + Stamp;
    private static readonly string BotAccount = ("g7akill" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private const uint Quest251 = 251;
    private const uint GiverNpc3512 = 3512;
    private const uint BoarTemplate3475 = 3475;
    private const uint BoarMeat4058 = 4058;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;
    private const int TeardownWakeWaitMs = 5000;
    private const float StopRadiusM = 3.0f;
    private const float StageOffsetM = 2.0f;
    private const int DriveBudgetMs = 300_000;
    private const int KillBudgetMs = 600_000;
    private const int TeardownBudgetMs = 120_000;
    private const int MaxRestages = 2;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "g7a-kill-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");
    [Fact]
    [Trait("Category", "e2e")]
    public async Task QuestLoopSustainsToKillThenTearsDown()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G7a kill gate (G6 end state → observe-only kill → loop teardown) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        // Teardown-bounded exclusion window (option c): the loot-exclusion
        // window ends AT the teardown witness (teardownFalseWake), back-dated
        // to the LAST wake whose IsAutoAttack still read true after death
        // (see teardownLastLiveWake) — or, when production releases the
        // corpse loot in the SAME observe cycle as the death that no teardown
        // sample ever sees live (see teardownDeathWakeFallback), to the death
        // wake via the pre-corpse anchor. Loot records after that witness are
        // G7c-owned — evidence only, never gate input. Witness-time records
        // bound the gate.
        var postTeardownLootEvidence = new List<string>();
        var witnessLogCastLoot = new List<string>();
        long witnessMoneyPost = -1;
        string stopOutcome = "", stopState = "";
        var wakeCount = 0; var wakeProven = 0;
        string firstFreshDecision = "";
        var firstFreshWake = 0;
        var selectedStable = true;
        var retrackCount = 0;
        var targetSamples = new List<string>();
        var seenTarget = false; var seenStop = false; var seenCombat = false;
        var meatDelta = -999; var moneyDelta = -999L; var creditDelta = -999;
        var hpTrail = new List<string>();
        var startQuestState = new E2eQuestDriver.QuestStateSnapshot(false, null, null, []);
        string startQuestStatus = "";
        var startTargetObjId = 0u;
        var startSnapGap = double.NaN;
        var startDistPinned = double.NaN;
        var startActivity = "UNOBSERVED";
        string startLiveAction = "", startLiveState = "";
        var startCycleBaseline = "ABSENT";
        string startDecideBaseline = "";
        var startQuestActionCount = -1;
        var startWakeSeq = -1;
        long startMoney = -1, startTurnInsL = -1;
        var startTargetHp = -1;
        var startLoopLive = false;
        var startPinned = false;
        var stageHp = -1;
        var stageMaxHp = -1;
        var restages = 0;
        var driveSeconds = 0.0;
        // Kill + teardown state.
        var killHpTrail = new List<string>();
        var killHpMin = -1;
        var killHpMaxSeen = -1;
        var deathWake = 0;
        var deathObserved = false;
        // Death-wake witness anchor for the zero-live-wake teardown edge
        // below: the LAST kill-watch sample where the target was still alive
        // and the loop still read live — a pre-corpse, therefore pre-corpse-
        // loot instant (read-only meat/money + log loot window + gate-hit
        // count frozen there). Production releases the corpse loot in the
        // SAME observe cycle that recognizes the corpse (QuestBehavior
        // LootProposal, released at loop teardown), so when death and loot
        // land in one cycle no teardown sample ever reads IsAutoAttack true
        // after death; this anchor is the honest pre-release witness and the
        // corpse loot is G7c-owned evidence, never gate input.
        var deathAnchorWake = 0;
        var deathAnchorMeat = -1;
        long deathAnchorMoney = -1L;
        var deathAnchorLogLoot = new List<string>();
        var deathAnchorCastLootCount = 0;
        var teardownDeathWakeFallback = false;
        var despawned = false;
        var leashSuspect = false;
        string leashDetail = "";
        var lastHpBeforeEnd = -1;
        var teardownWakes = 0;
        var teardownProven = false;
        var teardownFalseWake = 0;
        // Back-dated witness anchor: the LAST teardown wake where IsAutoAttack
        // still read true after deathObserved (death observed + loop still
        // live). The engine-side teardown flip is only SAMPLED on the next
        // observe — the teardown loop's wake(waitMs)+observe cadence lags the
        // real transition by one cycle — so loot released between this
        // last-true wake and the first-false wake is post-teardown G7c-owned
        // evidence, never gate input. 0 = no prior true wake (edge) → witness
        // falls back to the first-false wake (previous behavior).
        var teardownLastLiveWake = 0;
        var teardownLastLiveMeat = -1;
        long teardownLastLiveMoney = -1L;
        var lastDistOurs = double.NaN;
        var isAutoAttackTrail = new List<string>();
        var targetDeadDiags = new List<string>();
        var postDeathLandingsOurs = new List<string>();
        var preDeathLandingsOurs = 0;
        var newLoopElsewhere = "";
        uint endCurrentTarget = 0;
        var endIsAutoAttack = true;
        string endQuestStatus = "";
        var questStillActive = false;
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
                    var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest251},\"acceptorType\":\"Npc\",\"acceptorId\":{GiverNpc3512},\"idempotencyKey\":\"g7a-prehold-{BotAccount}\"}}";
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
            var staged = StageAtBoar(bridge, evidence, out spawnerXyz, out stagedDist, out var stageBoarObjId);
            var stageNpc = ReadNpcFull(bridge, stageBoarObjId);
            stageHp = stageNpc.Hp;
            stageMaxHp = stageNpc.MaxHp;
            Leg("combat-stage", staged,
                $"spawner=[{spawnerXyz}] flat={stagedDist:0.0}m (REQUIRE 1..3m) live3475={stageBoarObjId} hp={stageHp}/{stageMaxHp}",
                stageSw.Elapsed.TotalSeconds);
            if (!staged)
            {
                failBoundary = "SETUP/stage-range";
                Assert.Fail($"bot not staged 1-3m off the 3475 spawner (flat={stagedDist:0.0}m live3475={stageBoarObjId})");
            }

            // ---- SETTLE (standstill; the G7a START needs settled) ----
            var (settled, settleTrail) = SettleRoute(bridge, 30_000);
            evidence.AppendLine($"- pre-drive settle trail: {settleTrail}");
            Leg("settle", settled, $"settled={settled} trail=[{settleTrail}]", 0);
            if (!settled)
            {
                failBoundary = "SETUP/settle";
                Assert.Fail($"bot never stood still before drive (trail=[{settleTrail}])");
            }

            // ---- DRIVE to the G6 end state (observe-only; production loop starts
            // the fight, never the harness). Breaks on loop-live + pinned +
            // damaged-but-alive. A kill before START restages (bounded).
            var gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            var gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            var decideHistory = new List<string>();
            var liveHistory = new List<string>();
            var driveSw = Stopwatch.StartNew();
            var driveDeadline = Environment.TickCount64 + DriveBudgetMs;
            JsonElement observe = default;
            var driveLoopLive = false;
            var drivePinned = false;
            var driveHpFell = false;
            var driveHpNow = -1;
            selectedObjId = 0;
            // K1 failfast state (drive only): counts consecutive wakes with a
            // byte-identical questDecideDetail and no per-bot step signal. Fires
            // at 2; gated on zero drive progress (no loop live, no HP fall, no
            // combat landed), so a warm drive can never trip it. Per-bot baselines
            // anchor on the first wake (no pre-drive snapshot exists); budgets
            // stay ceilings.
            var prevDecide = "";
            var idleWakes = 0;
            string idleExitBoundary = "";
            var k1ActionCount = 0;
            long k1WakeSeq = 0, k1TraceWakeSeq = 0, k1MaxTraceWakeSeq = 0;
            var k1Anchored = false;
            while (Environment.TickCount64 < driveDeadline)
            {
                try
                {
                    observe = bridge.Call(
                        $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                }
                catch (Exception ex)
                {
                    evidence.AppendLine($"- drive wake call failed ({ex.GetType().Name}); retrying");
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
                if (string.IsNullOrEmpty(firstFreshDecision) && !string.IsNullOrEmpty(decide))
                {
                    firstFreshDecision = decide;
                    firstFreshWake = wakeCount + 1;
                }
                var live = $"liveAction={GetStr(observe, "liveAction")} liveState={GetStr(observe, "liveState")} lastAction={GetStr(observe, "lastAction")}";
                if (liveHistory.Count == 0 || liveHistory[^1] != live)
                    liveHistory.Add(live);
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
                    if (f.Selected != 0 && (selectedObjId == 0 || f.Selected == selectedObjId))
                        lastDistOurs = f.Dist;
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
                if (landedCombat.Success)
                {
                    seenCombat = true;
                    decideCombat = decide;
                }
                ScanCombatExclusion(decide, live, castLootHits);
                if (GetBool(observe, "isAutoAttack"))
                    driveLoopLive = true;
                if (selectedObjId != 0 && GetUInt(observe, "currentTargetObjId") == selectedObjId)
                    drivePinned = true;
                var hpNow = ReadNpcHp(bridge, selectedObjId != 0 ? selectedObjId : (uint)stageBoarObjId);
                if (hpNow >= 0)
                {
                    if (hpTrail.Count == 0 || hpTrail[^1] != $"wake={wakeCount} hp={hpNow}")
                        hpTrail.Add($"wake={wakeCount} hp={hpNow}");
                    if (stageHp > 0 && hpNow < stageHp)
                        driveHpFell = true;
                    driveHpNow = hpNow;
                    if (hpNow == 0)
                        break; // died during drive — restage below
                }

                if (driveLoopLive && drivePinned && driveHpFell && driveHpNow > 0 && !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM)
                    break; // G6 end state reached at live in-range (boar shuffles; spawner-flat goes stale)
                // K1 failfast (drive only): 2 consecutive wakes with unchanged
                // questDecideDetail and HasPerBotStepSignal == false end the drive
                // now with the frozen G3/G4 boundary name — no new strings. The
                // firstZero=sweep-empty payload form maps to DISCOVERY/sweep-empty,
                // anything else to ARBITRATION/no-quest-activity. Unreachable once
                // the drive shows life (end-state break above; loop/HP/combat
                // guards below). Budgets stay ceilings.
                if (!k1Anchored)
                {
                    k1ActionCount = GetInt(observe, "questActionCount");
                    k1WakeSeq = GetSeq(observe, "wakeSeq");
                    k1TraceWakeSeq = GetSeq(observe, "traceWakeSeq");
                    k1MaxTraceWakeSeq = GetSeq(observe, "maxTraceWakeSeq");
                    prevDecide = decide;
                    k1Anchored = true;
                    idleWakes = 0;
                }
                else if (!driveLoopLive && !driveHpFell && !seenCombat
                    && string.Equals(decide, prevDecide, StringComparison.Ordinal)
                    && !HasPerBotStepSignal(observe, ourCharacterId, k1ActionCount, k1WakeSeq, k1TraceWakeSeq, k1MaxTraceWakeSeq))
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
            driveSeconds = driveSw.Elapsed.TotalSeconds;

            // Kill-before-START: restage onto a fresh boar (bounded), then
            // re-drive. The gate START demands damaged-but-alive.
            while (driveHpNow == 0 && restages < MaxRestages)
            {
                restages++;
                evidence.AppendLine($"- drive overshoot #{restages}: prey {selectedObjId} died before START (hp trail [{string.Join(" | ", hpTrail.Take(10))}]); restaging");
                var reSw = Stopwatch.StartNew();
                var reOk = StageAtBoar(bridge, evidence, out spawnerXyz, out stagedDist, out stageBoarObjId);
                var reNpc = ReadNpcFull(bridge, stageBoarObjId);
                stageHp = reNpc.Hp;
                stageMaxHp = reNpc.MaxHp;
                var (reSettled, reTrail) = reOk ? SettleRoute(bridge, 30_000) : (false, "stage-failed");
                evidence.AppendLine($"- restage #{restages} trail: {reTrail} ok={reOk} settled={reSettled} live3475={stageBoarObjId} hp={stageHp}/{stageMaxHp} ({reSw.Elapsed.TotalSeconds:0.0}s)");
                if (!reOk || !reSettled)
                {
                    failBoundary = "HARNESS/fixture-overshoot";
                    Assert.Fail($"restage #{restages} failed after a drive-phase kill (ok={reOk} settled={reSettled}); refusing START");
                }
                selectedObjId = 0;
                selectedStable = true;
                driveLoopLive = false;
                drivePinned = false;
                driveHpFell = false;
                driveHpNow = -1;
                idleWakes = 0;
                prevDecide = "";
                k1Anchored = false;
                hpTrail.Clear();
                driveDeadline = Environment.TickCount64 + DriveBudgetMs;
                while (Environment.TickCount64 < driveDeadline)
                {
                    try
                    {
                        observe = bridge.Call(
                            $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                    }
                    catch
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                    try
                    {
                        observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                    }
                    catch { /* keep wake response */ }
                    var decide = GetStr(observe, "questDecideDetail");
                    if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                        decideHistory.Add(decide);
                    if (string.IsNullOrEmpty(firstFreshDecision) && !string.IsNullOrEmpty(decide))
                    {
                        firstFreshDecision = decide;
                        firstFreshWake = wakeCount + 1;
                    }
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
                    if (f.Selected != 0 && (selectedObjId == 0 || f.Selected == selectedObjId))
                        lastDistOurs = f.Dist;
                    if (targetSamples.Count < 60)
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
                    if (landedCombat.Success)
                    {
                        seenCombat = true;
                        decideCombat = decide;
                    }
                    if (GetBool(observe, "isAutoAttack"))
                        driveLoopLive = true;
                    if (selectedObjId != 0 && GetUInt(observe, "currentTargetObjId") == selectedObjId)
                        drivePinned = true;
                    var hpNow = ReadNpcHp(bridge, selectedObjId != 0 ? selectedObjId : (uint)stageBoarObjId);
                    if (hpNow >= 0)
                    {
                        if (hpTrail.Count == 0 || hpTrail[^1] != $"wake={wakeCount} hp={hpNow}")
                            hpTrail.Add($"wake={wakeCount} hp={hpNow}");
                        if (stageHp > 0 && hpNow < stageHp)
                            driveHpFell = true;
                        driveHpNow = hpNow;
                        if (hpNow == 0)
                            break;
                    }
                    if (driveLoopLive && drivePinned && driveHpFell && driveHpNow > 0 && !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM)
                        break;
                    // K1 failfast (drive only, post-restage): same shape as above — the
                    // end-state break precedes it and the loop/HP/combat guards gate it
                    // out once the fresh staging shows life. Budgets stay ceilings.
                    if (!k1Anchored)
                    {
                        k1ActionCount = GetInt(observe, "questActionCount");
                        k1WakeSeq = GetSeq(observe, "wakeSeq");
                        k1TraceWakeSeq = GetSeq(observe, "traceWakeSeq");
                        k1MaxTraceWakeSeq = GetSeq(observe, "maxTraceWakeSeq");
                        prevDecide = decide;
                        k1Anchored = true;
                        idleWakes = 0;
                    }
                    else if (!driveLoopLive && !driveHpFell && !seenCombat
                        && string.Equals(decide, prevDecide, StringComparison.Ordinal)
                        && !HasPerBotStepSignal(observe, ourCharacterId, k1ActionCount, k1WakeSeq, k1TraceWakeSeq, k1MaxTraceWakeSeq))
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
            }
            var g6EndState = driveLoopLive && drivePinned && driveHpFell && driveHpNow > 0 && selectedObjId != 0 && selectedStable && !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM;
            Leg("drive-to-g6-end", g6EndState,
                $"loopLive={driveLoopLive} pinned={drivePinned} hpFell={driveHpFell} hpNow={driveHpNow} stageHp={stageHp} selected={selectedObjId} stable={selectedStable} liveDist={lastDistOurs:0.0}m restages={restages} wakes={wakeCount}",
                driveSeconds);
            evidence.AppendLine($"- drive HP trail: {string.Join(" | ", hpTrail.Take(30))}");
            if (driveHpNow == 0)
            {
                failBoundary = "HARNESS/fixture-overshoot";
                Assert.Fail($"prey keeps dying before a damaged-but-alive START is observable ({restages} restages); refusing to force the fixture");
            }
            if (!g6EndState)
            {
                // K1 idle-drive exit takes precedence: the drive ended early with the
                // frozen boundary name (same name the spent-budget path converges on).
                failBoundary = !string.IsNullOrEmpty(idleExitBoundary) ? idleExitBoundary
                    : string.IsNullOrEmpty(firstFreshDecision) ? "HARNESS/NO-FRESH-DECISION" : "COMBAT/no-loop-to-sustain";
                Assert.Fail($"G6 end state unreachable (loopLive={driveLoopLive} pinned={drivePinned} hpFell={driveHpFell} hpNow={driveHpNow} selected={selectedObjId} stable={selectedStable})");
            }

            // ---- AUTHORITATIVE START SNAPSHOT (G6 end state; read ONCE) ----
            var startSnapSw = Stopwatch.StartNew();
            meatPre = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var preStart = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            startQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            startQuestStatus = startQuestState.Status?.ToString() ?? "";
            startTargetObjId = selectedObjId;
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
            startLoopLive = GetBool(preStart, "isAutoAttack");
            startPinned = GetUInt(preStart, "currentTargetObjId") == startTargetObjId;
            startTargetHp = ReadNpcHp(bridge, startTargetObjId);
            evidence.AppendLine($"- START snapshot: charId={ourCharacterId} quest251 active={startQuestState.Active} step={startQuestState.Step} status={startQuestStatus} objectives=[{string.Join(",", startQuestState.Objectives)}]");
            evidence.AppendLine($"- START snapshot: target objId={startTargetObjId} template={BoarTemplate3475} hp={startTargetHp}/{stageMaxHp} (stageHp={stageHp}; REQUIRE 0<hp<stage) loopLive={startLoopLive} pinned={startPinned} spawner=[{spawnerXyz}]");
            evidence.AppendLine($"- START snapshot: snapActorGap={startSnapGap:0.0}m pos=[{startPosA}] spawnerFlat={startDistPinned:0.0}m (evidence only; boar shuffles) liveDist={lastDistOurs:0.0}m (REQUIRE <={StopRadiusM:0.0}) activity=[{startActivity}] live={startLiveAction}/{startLiveState}");
            evidence.AppendLine($"- START snapshot: cycleBaseline=[{startCycleBaseline}] questActionCount={startQuestActionCount} wakeSeq={startWakeSeq} meat4058={meatPre} money={startMoney} turnIns={startTurnInsL}");
            var startRangeOk = !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM;
            var startIdentityOk = !double.IsNaN(startSnapGap) && startSnapGap < 0.5;
            var startDamagedAlive = startTargetHp > 0 && stageHp > 0 && startTargetHp < stageHp;
            var startOk = startRangeOk && startIdentityOk && startLoopLive && startPinned && startDamagedAlive && startQuestState.Active;
            Leg("start-snapshot", startOk,
                $"charId={ourCharacterId} active251={startQuestState.Active} target={startTargetObjId}/t{BoarTemplate3475} hp={startTargetHp}(<stage {stageHp}) loopLive={startLoopLive} pinned={startPinned} gap={startSnapGap:0.0}m (REQUIRE <0.5) liveDist={lastDistOurs:0.0}m (REQUIRE <={StopRadiusM:0.0}) cycle=[{startCycleBaseline}] meat={meatPre}",
                startSnapSw.Elapsed.TotalSeconds);
            if (!startOk)
            {
                failBoundary = "HARNESS/START-RANGE";
                Assert.Fail($"START outside the G6-end-state window (liveDist={lastDistOurs:0.0}m gap={startSnapGap:0.0}m hp={startTargetHp} loopLive={startLoopLive} pinned={startPinned}); refusing START");
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- KILL watch: bounded, observe ONLY (no gameplay commands) ----
            var execSw = Stopwatch.StartNew();
            var killDeadline = Environment.TickCount64 + KillBudgetMs;
            killHpMin = startTargetHp;
            killHpMaxSeen = startTargetHp;
            lastHpBeforeEnd = startTargetHp;
            while (Environment.TickCount64 < killDeadline)
            {
                try
                {
                    observe = bridge.Call(
                        $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                }
                catch (Exception ex)
                {
                    evidence.AppendLine($"- kill wake call failed ({ex.GetType().Name}); retrying");
                    Thread.Sleep(2000);
                    continue;
                }
                try
                {
                    observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                }
                catch { /* keep wake response */ }
                var decide = GetStr(observe, "questDecideDetail");
                if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                    decideHistory.Add(decide);
                wakeCount++;
                wakeProven++;
                var live = $"liveAction={GetStr(observe, "liveAction")} liveState={GetStr(observe, "liveState")} lastAction={GetStr(observe, "lastAction")}";
                if (liveHistory.Count == 0 || liveHistory[^1] != live)
                    liveHistory.Add(live);

                var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                foreach (var f in funnels)
                {
                    if (distTrail.Count == 0 || Math.Abs(distTrail[^1] - f.Dist) > 1e-9)
                        distTrail.Add(f.Dist);
                    if (f.Selected != 0 && f.Selected != startTargetObjId)
                    {
                        // Pre-death reselection away from OUR corpse-to-be breaks
                        // commitment; post-death reselection is production-
                        // correct (dead targets are funnel-rejected).
                        if (!deathObserved)
                        {
                            selectedStable = false;
                            retrackCount++;
                        }
                    }
                    if (targetSamples.Count < 80)
                        targetSamples.Add($"wake={wakeCount} selected={f.Selected} distM={f.Dist:0.0} pursuit=[{f.Pursuit}] combat=[{f.Combat}]");
                    if (f.Combat.Contains("validate=target-dead", StringComparison.Ordinal)
                        && f.Combat.Contains(startTargetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        && targetDeadDiags.Count < 10)
                        targetDeadDiags.Add($"wake={wakeCount} combat=[{f.Combat}]");
                }
                var landedCombat = Regex.Match(decide, @"landed AutoAttack \(([^)]*)\)");
                if (landedCombat.Success)
                {
                    var core = landedCombat.Groups[1].Value;
                    var ours = core.Contains($"on {startTargetObjId}", StringComparison.Ordinal);
                    if (deathObserved)
                    {
                        if (ours && !postDeathLandingsOurs.Contains(decide))
                            postDeathLandingsOurs.Add(decide);
                    }
                    else if (ours)
                    {
                        preDeathLandingsOurs++;
                    }
                }
                ScanCombatExclusion(decide, live, castLootHits);
                endIsAutoAttack = GetBool(observe, "isAutoAttack");
                endCurrentTarget = GetUInt(observe, "currentTargetObjId");
                isAutoAttackTrail.Add($"wake={wakeCount} auto={endIsAutoAttack} cur={endCurrentTarget}");

                var hpNow = ReadNpcHp(bridge, startTargetObjId);
                if (hpNow == 0)
                {
                    deathObserved = true;
                    deathWake = wakeCount;
                    killHpMin = 0;
                    lastHpBeforeEnd = 0;
                    // The witness for this death is the LAST ALIVE sample's
                    // anchor (deathAnchor*, frozen above): production can
                    // release the corpse loot in the SAME observe cycle that
                    // recognizes the corpse, so sampling here would already
                    // read post-loot. The teardown edge below back-dates to
                    // that anchor when no live wake is ever observed.
                    if (killHpTrail.Count == 0 || killHpTrail[^1] != $"wake={wakeCount} hp=0 DEAD")
                        killHpTrail.Add($"wake={wakeCount} hp=0 DEAD");
                    break;
                }
                if (hpNow < 0)
                {
                    // ObjId gone from the world without observed Hp==0: NOT
                    // death evidence — the target was lost (never despawn-
                    // tracked, since despawn follows DoDie).
                    lastHpBeforeEnd = killHpMin;
                    break;
                }
                lastHpBeforeEnd = hpNow;
                if (hpNow < killHpMin)
                    killHpMin = hpNow;
                if (hpNow > killHpMaxSeen)
                    killHpMaxSeen = hpNow;
                if (killHpTrail.Count == 0 || killHpTrail[^1] != $"wake={wakeCount} hp={hpNow}")
                    killHpTrail.Add($"wake={wakeCount} hp={hpNow}");
                // Freeze the pre-corpse witness anchor: target alive + loop
                // live at this sample (the last such sample is the instant
                // before production's death/loot cycle). All three reads are
                // read-only and committed together so a partial failure never
                // mixes a later log window with an earlier meat/money value.
                if (endIsAutoAttack)
                {
                    try
                    {
                        var anchorLogLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
                        var anchorMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                        var anchorMoney = observe.TryGetProperty("money", out var aMoneyEl) && aMoneyEl.ValueKind == JsonValueKind.Number && aMoneyEl.TryGetInt64(out var aMoney) ? aMoney : -1L;
                        deathAnchorLogLoot = anchorLogLoot;
                        deathAnchorMeat = anchorMeat;
                        deathAnchorMoney = anchorMoney;
                        deathAnchorCastLootCount = castLootHits.Count;
                        deathAnchorWake = wakeCount;
                    }
                    catch
                    {
                        // Keep the last fully committed anchor.
                    }
                }
                if (stageMaxHp > 0 && killHpMin > 0 && killHpMin < stageMaxHp
                    && hpNow - killHpMin > 0.25 * stageMaxHp && !leashSuspect)
                {
                    leashSuspect = true;
                    leashDetail = $"wake={wakeCount} hp rose {killHpMin}->{hpNow} (max {stageMaxHp}; >25% rebound after a fall — leash-reset suspect)";
                    evidence.AppendLine($"- LEASH-SUSPECT: {leashDetail}");
                }
                Thread.Sleep(2000);
            }
            evidence.AppendLine($"- kill HP trail: {string.Join(" | ", killHpTrail.Take(40))} (startHp={startTargetHp} min={killHpMin} maxSeen={killHpMaxSeen} dead={deathObserved} lastHp={lastHpBeforeEnd})");
            evidence.AppendLine($"- quest-side target-dead withdrawals ({targetDeadDiags.Count}):");
            foreach (var d in targetDeadDiags.Take(10))
                evidence.AppendLine($"  [{d}]");

            // ---- TEARDOWN watch: post-death, short wakes to catch the
            // IsAutoAttack true→false transition even if the quest leg later
            // starts a legitimate new loop on another boar.
            if (deathObserved)
            {
                var teardownDeadline = Environment.TickCount64 + TeardownBudgetMs;
                while (Environment.TickCount64 < teardownDeadline && !teardownProven)
                {
                    try
                    {
                        observe = bridge.Call(
                            $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{TeardownWakeWaitMs}}}", 60_000);
                    }
                    catch
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    try
                    {
                        observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                    }
                    catch { /* keep wake response */ }
                    var decide = GetStr(observe, "questDecideDetail");
                    if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                        decideHistory.Add(decide);
                    wakeCount++;
                    teardownWakes++;
                    endIsAutoAttack = GetBool(observe, "isAutoAttack");
                    endCurrentTarget = GetUInt(observe, "currentTargetObjId");
                    isAutoAttackTrail.Add($"wake={wakeCount} auto={endIsAutoAttack} cur={endCurrentTarget} (teardown)");
                    var landedCombat = Regex.Match(decide, @"landed AutoAttack \(([^)]*)\)");
                    if (landedCombat.Success)
                    {
                        var core = landedCombat.Groups[1].Value;
                        if (core.Contains($"on {startTargetObjId}", StringComparison.Ordinal) && !postDeathLandingsOurs.Contains(decide))
                            postDeathLandingsOurs.Add(decide);
                        else if (!core.Contains($"on {startTargetObjId}", StringComparison.Ordinal) && string.IsNullOrEmpty(newLoopElsewhere))
                            newLoopElsewhere = $"wake={wakeCount} [{decide}] (legitimate new-target loop; recorded, never gated)";
                    }
                    var teardownLive = $"liveAction={GetStr(observe, "liveAction")}";
                    var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                    foreach (var f in funnels)
                    {
                        if (f.Combat.Contains("validate=target-dead", StringComparison.Ordinal)
                            && f.Combat.Contains(startTargetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                            && targetDeadDiags.Count < 10)
                            targetDeadDiags.Add($"wake={wakeCount} combat=[{f.Combat}]");
                    }
                    if (endIsAutoAttack)
                    {
                        // LAST live wake (deathObserved + loop still live). The
                        // engine-side IsAutoAttack true→false flip happens between
                        // observes; the wake(waitMs)+observe cadence samples the
                        // false only a wake later. Anchor the witness here and
                        // freeze the exclusion window (log scan + meat/money) at
                        // this back-dated point, so loot released between this
                        // wake and the first-false sample stays post-teardown
                        // G7c-owned evidence, never gate input.
                        try
                        {
                            // Commit all three atomically: a partial read must
                            // never mix a later log window with an earlier
                            // meat/money anchor.
                            var liveLogLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
                            var liveMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                            var liveMoney = observe.TryGetProperty("money", out var wLiveEl) && wLiveEl.ValueKind == JsonValueKind.Number && wLiveEl.TryGetInt64(out var wLiveMoney) ? wLiveMoney : -1L;
                            witnessLogCastLoot = liveLogLoot;
                            teardownLastLiveMeat = liveMeat;
                            teardownLastLiveMoney = liveMoney;
                            teardownLastLiveWake = wakeCount;
                        }
                        catch
                        {
                            // Snapshot read failed: keep the last fully committed
                            // anchor; when none exists the first-false wake
                            // witness below (previous behavior) is used instead
                            // of aborting the teardown watch.
                            if (teardownLastLiveWake == 0)
                                witnessLogCastLoot = new List<string>();
                        }
                        ScanCombatExclusion(decide, teardownLive, castLootHits);
                    }
                    else
                    {
                        teardownProven = true;
                        // Back-date the witness to the last live wake when one
                        // exists (observe-cadence lag); edge fallback = this
                        // first-false wake (previous behavior).
                        teardownFalseWake = teardownLastLiveWake != 0 ? teardownLastLiveWake : wakeCount;
                        if (teardownLastLiveWake != 0)
                        {
                            // Witness window already frozen at the last live wake
                            // above — this first-false wake's samples and its
                            // decide/live record are post-witness (G7c-owned).
                            meatPost = teardownLastLiveMeat;
                            witnessMoneyPost = teardownLastLiveMoney;
                            ScanCombatExclusion(decide, teardownLive, postTeardownLootEvidence);
                        }
                        else
                        {
                            // Zero-live-wake edge. Two sub-cases, both
                            // bounded to the FIRST-FALSE teardown sample:
                            // (a) clean death (deathObserved with Hp==0 and
                            //     teardownProven) AND a post-death
                            //     QuestObjectiveLootDiag Completed grant for
                            //     OUR objId: the death and the corpse loot
                            //     landed in the SAME observe cycle, so
                            //     production's LootProposal release (released
                            //     at loop teardown) beats the first teardown
                            //     sample that can read IsAutoAttack false — a
                            //     same-observe-cycle death+loot race. Back-
                            //     date the witness to the death wake via the
                            //     pre-corpse anchor frozen at the last alive
                            //     kill-watch sample; the corpse loot is
                            //     G7c-owned evidence, never gate input.
                            // (b) otherwise: previous behavior — freeze at
                            //     this first-false wake.
                            var nowLogLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
                            // A Completed grant naming OUR objId cannot predate
                            // the corpse, so plain existence (with a live
                            // pre-death anchor present) is the post-death
                            // same-cycle-loot proof.
                            var deathWakeGrant = ScanLootGrantLines(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId).Count != 0;
                            // Clean death only (deathObserved with Hp==0 and
                            // teardownProven); otherwise leave the previous
                            // fallback behavior untouched.
                            var cleanDeath = deathObserved && killHpMin == 0 && lastHpBeforeEnd == 0;
                            if (cleanDeath && deathAnchorWake != 0 && deathAnchorMeat >= 0 && deathAnchorMoney >= 0 && deathWakeGrant)
                            {
                                // Back-dated death-wake witness: meat/money/log
                                // window are the pre-corpse anchor values.
                                // Gate hits collected during the death wake
                                // (the same-cycle corpse loot) are ROUTED to
                                // G7c-owned evidence, never gate input.
                                teardownDeathWakeFallback = true;
                                teardownFalseWake = deathWake;
                                witnessLogCastLoot = deathAnchorLogLoot;
                                meatPost = deathAnchorMeat;
                                witnessMoneyPost = deathAnchorMoney;
                                for (var i = castLootHits.Count - 1; i >= deathAnchorCastLootCount; i--)
                                {
                                    var hit = castLootHits[i];
                                    if (hit.Contains(startTargetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                                        && hit.Contains("Loot", StringComparison.Ordinal)
                                        && !hit.Contains("Cast", StringComparison.Ordinal))
                                    {
                                        if (!postTeardownLootEvidence.Contains(hit))
                                            postTeardownLootEvidence.Add(hit);
                                        castLootHits.RemoveAt(i);
                                    }
                                }
                                ScanCombatExclusion(decide, teardownLive, postTeardownLootEvidence);
                            }
                            else
                            {
                                // No prior live wake observed (loop already torn
                                // down at the first teardown sample): freeze here.
                                witnessLogCastLoot = nowLogLoot;
                                meatPost = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                                witnessMoneyPost = observe.TryGetProperty("money", out var wMoneyEl) && wMoneyEl.ValueKind == JsonValueKind.Number && wMoneyEl.TryGetInt64(out var wMoney) ? wMoney : -1L;
                                ScanCombatExclusion(decide, teardownLive, castLootHits);
                            }
                        }
                    }
                    Thread.Sleep(1000);
                }
                evidence.AppendLine($"- teardown: proven={teardownProven} falseAtWake={teardownFalseWake} lastLiveWake={teardownLastLiveWake} deathWakeFallback={teardownDeathWakeFallback} teardownWakes={teardownWakes} postDeathLandingsOnOurs={postDeathLandingsOurs.Count} {newLoopElsewhere}");
            }

            // ---- DESPAWN corroboration (honest, never gating): the dead Npc IS
            // the corpse until Spawner.DoDespawn releases the ObjId.
            if (deathObserved)
            {
                var despawnDeadline = Environment.TickCount64 + 60_000;
                while (Environment.TickCount64 < despawnDeadline && !despawned)
                {
                    if (ReadNpcHp(bridge, startTargetObjId) < 0)
                    {
                        despawned = true;
                        break;
                    }
                    Thread.Sleep(10_000);
                }
                evidence.AppendLine($"- despawn corroboration: despawned={despawned} (objId-gone within 60s post-death; recorded only)");
            }
            execSeconds = execSw.Elapsed.TotalSeconds;
            evidence.AppendLine($"- first fresh decision (drive): {(string.IsNullOrEmpty(firstFreshDecision) ? "NONE" : $"wake#{firstFreshWake} [{firstFreshDecision}]")}");
            evidence.AppendLine($"- decide history ({decideHistory.Count}):");
            foreach (var d in decideHistory.Take(25))
                evidence.AppendLine($"  [{d}]");
            evidence.AppendLine($"- live history ({liveHistory.Count}): {string.Join(" | ", liveHistory.Take(30))}");
            evidence.AppendLine($"- funnel dist trail: {string.Join(" -> ", distTrail.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))}");
            evidence.AppendLine($"- isAutoAttack trail: {string.Join(" | ", isAutoAttackTrail.Take(60))}");

            // ---- POST proofs (read-only, teardown-witness bounded): meat/money
            // sampled at the back-dated teardownFalseWake witness above;
            // castLootHits already ends AT that witness (the witness window is
            // frozen at the last live wake and the teardown loop exits on
            // teardownProven), so only the witness-time log scan joins gate
            // input. Post-witness loot is G7c-owned evidence only, never gate
            // input.
            if (meatPost < 0)
                meatPost = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            questStillActive = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            var endQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            endQuestStatus = endQuestState.Status?.ToString() ?? "";
            var postObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            var turnIns = GetInt(postObs, "turnIns");
            var moneyPost = witnessMoneyPost >= 0 ? witnessMoneyPost : postObs.TryGetProperty("money", out var mpEl) && mpEl.ValueKind == JsonValueKind.Number && mpEl.TryGetInt64(out var mpPost) ? mpPost : -1L;
            if (teardownProven)
            {
                // Witness-bounded: castLootHits already ends AT the teardown
                // witness (the teardown loop exits on teardownProven), so only
                // the witness-time log scan joins gate input. The post-witness
                // observe decide + any log loot after the witness snapshot are
                // G7c-owned evidence only, never gate input.
                castLootHits.AddRange(witnessLogCastLoot.Where(l => !castLootHits.Contains(l)));
                ScanCombatExclusion(GetStr(postObs, "questDecideDetail"), $"lastAction={GetStr(postObs, "lastAction")}", postTeardownLootEvidence);
                var fullLogLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
                foreach (var l in fullLogLoot.Where(l => !witnessLogCastLoot.Contains(l) && !postTeardownLootEvidence.Contains(l)))
                    postTeardownLootEvidence.Add(l);
            }
            else
            {
                ScanCombatExclusion(GetStr(postObs, "questDecideDetail"), $"lastAction={GetStr(postObs, "lastAction")}", castLootHits);
                castLootHits.AddRange(ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName));
            }
            meatDelta = meatPre >= 0 && meatPost >= 0 ? meatPost - meatPre : -999;
            moneyDelta = startMoney >= 0 && moneyPost >= 0 ? moneyPost - startMoney : -999L;
            creditDelta = (turnIns - startTurnInsL) != 0 || (questStillActive != startQuestState.Active) || !string.Equals(endQuestStatus, startQuestStatus, StringComparison.Ordinal) ? 1 : 0;
            evidence.AppendLine($"- exclusion scan (teardown-witness bounded at wake#{teardownFalseWake}): castLootHits={(castLootHits.Count == 0 ? "NONE" : string.Join("; ", castLootHits.Take(10)))} postTeardownLootEvidence={(postTeardownLootEvidence.Count == 0 ? "NONE (G7c-owned)" : string.Join("; ", postTeardownLootEvidence.Take(10)))}");
            evidence.AppendLine($"- side effects (teardown-witness sampled): meatDelta={meatDelta} ({meatPre}->{meatPost}) moneyDelta={moneyDelta} creditDelta={creditDelta} (turnIns {startTurnInsL}->{turnIns}, active {startQuestState.Active}->{questStillActive}, status {startQuestStatus}->{endQuestStatus})");

            endDist = distTrail.Count > 0 ? distTrail[^1] : double.NaN;
            if (!deathObserved && lastHpBeforeEnd >= 0 && killHpMin > 0)
            {
                // ObjId-gone vs timeout split: npcState -1 with last HP>0 is a
                // lost target, never a kill.
                var hpVanished = ReadNpcHp(bridge, startTargetObjId) < 0;
                if (hpVanished)
                {
                    verdict = "FAIL-BEHAVIOR/TARGET-LOST";
                    failBoundary = "TARGET/lost";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, lastHp = lastHpBeforeEnd, killHpTrail = killHpTrail.Take(20).ToList(), interpretation = "OUR objId left the world with last-HP>0 and no Hp==0 ever observed — lost, never dead" };
                }
                else if (!selectedStable)
                {
                    verdict = "FAIL-BEHAVIOR/TARGET-LOST";
                    failBoundary = "TARGET/lost";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, retrackCount, interpretation = "funnel selection moved off OUR target before death (commitment broken)" };
                }
                else if (leashSuspect)
                {
                    verdict = "UNKNOWN/HARNESS/LEASH-RESET";
                    failBoundary = "HARNESS/leash-reset";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, leashDetail, killHpTrail = killHpTrail.Take(20).ToList(), interpretation = "HP sawtooth (heal/leash rebound) coincided with the kill timeout — fixture DPS/leash race, never a behavior fail" };
                }
                else
                {
                    verdict = "FAIL-BEHAVIOR/COMBAT-NO-KILL";
                    failBoundary = "COMBAT/no-kill";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, startHp = startTargetHp, killHpMin, lastHp = lastHpBeforeEnd, killBudgetMs = KillBudgetMs, killHpTrail = killHpTrail.Take(20).ToList(), interpretation = "sustained quest-owned loop never drove OUR target to Hp<=0 inside the kill budget" };
                }
            }
            else if (!deathObserved)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-NO-KILL";
                failBoundary = "COMBAT/no-kill";
                failingCondition = new { boundary = failBoundary, target = startTargetObjId, startHp = startTargetHp, killHpTrail = killHpTrail.Take(20).ToList(), interpretation = "kill watch ended without authoritative death evidence" };
            }
            else if (postDeathLandingsOurs.Count != 0)
            {
                verdict = "FAIL-BEHAVIOR/LOOP-NOT-TORN-DOWN";
                failBoundary = "LOOP/not-torn-down";
                failingCondition = new { boundary = failBoundary, target = startTargetObjId, deathWake, landings = postDeathLandingsOurs.Take(5).ToList(), interpretation = "quest leg re-dispatched AutoAttack into OUR corpse after authoritative death (proposal keeps dispatching into a corpse)" };
            }
            else if (!teardownProven)
            {
                verdict = "FAIL-BEHAVIOR/LOOP-NOT-TORN-DOWN";
                failBoundary = "LOOP/not-torn-down";
                failingCondition = new { boundary = failBoundary, target = startTargetObjId, deathWake, teardownWakes, isAutoAttackTrail = isAutoAttackTrail.Take(30).ToList(), interpretation = "IsAutoAttack never read false inside the teardown budget after authoritative death (stuck loop)" };
            }
            else if (castLootHits.Count != 0)
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-CAST-DETECTED";
                failBoundary = "COMBAT-CAST-DETECTED";
                failingCondition = new { boundary = failBoundary, hits = castLootHits.Take(10).ToList(), interpretation = "Cast/Loot record observed — AutoAttack-only violated" };
            }
            else if (meatDelta != 0 || moneyDelta != 0 || !questStillActive || turnIns != startTurnInsL || !string.Equals(endQuestStatus, startQuestStatus, StringComparison.Ordinal))
            {
                verdict = "FAIL-BEHAVIOR/COMBAT-CREDIT-LEAK";
                failBoundary = "COMBAT-CREDIT-LEAK";
                failingCondition = new { boundary = failBoundary, meatPre, meatPost, meatDelta, moneyDelta, questStillActive, startQuestStatus, endQuestStatus, turnIns, interpretation = "death-adjacent proxy moved (meat/money delta, quest left Progress, or turn-in recorded) — death alone must grant nothing for 251" };
            }
            else
            {
                verdict = "PASS-BEHAVIOR";
                failBoundary = "none";
                passed = true;
                claim = $"from G6-end START (251-ACTIVE, 3475 {startTargetObjId} hp {startTargetHp}/{stageMaxHp} damaged, loop live+pinned at live {lastDistOurs:0.0}m), observe-only sustain killed OUR target (Hp {startTargetHp}->0 at wake#{deathWake}, despawned={despawned}), loop self-terminated (IsAutoAttack false at wake#{teardownFalseWake}, target-dead withdrawals={targetDeadDiags.Count}, zero post-death landings on {startTargetObjId}), meat {meatPre}->{meatPost}, moneyΔ 0, quest still {endQuestStatus}, turnIns 0 — and stops there (no loot: G7c)";
                failingCondition = new
                {
                    boundary = "none",
                    target = startTargetObjId,
                    startHp = startTargetHp,
                    deathWake,
                    despawned,
                    teardownFalseWake,
                    targetDeadWithdrawals = targetDeadDiags.Count,
                    endCurrentTarget,
                    meatPre,
                    meatPost,
                    questStillActive,
                    endQuestStatus,
                    turnIns
                };
            }
            Leg("gate", passed, $"{verdict} at {failBoundary}: target={startTargetObjId} hp={startTargetHp}->0 dead={deathObserved} tornDown={teardownProven}",
                execSeconds);
            Assert.True(passed, $"G7a {verdict} at {failBoundary}: target={startTargetObjId}");
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
                        startTargetObjId,
                        spawner = spawnerXyz,
                        stagedDistM = stagedDist,
                        stageHp,
                        stageMaxHp,
                        restages,
                        driveSeconds,
                        stopOutcome,
                        stopState,
                        start = new
                        {
                            charId = ourCharacterId,
                            questActive = startQuestState.Active,
                            questStep = startQuestState.Step,
                            questStatus = startQuestStatus,
                            questObjectives = startQuestState.Objectives,
                            targetObjId = startTargetObjId,
                            targetTemplate = BoarTemplate3475,
                            targetHp = startTargetHp,
                            snapActorGapM = startSnapGap,
                            flatToPinnedM = startDistPinned,
                            liveDistM = lastDistOurs,
                            activity = startActivity,
                            live = startLiveAction + "/" + startLiveState,
                            cycleBaseline = startCycleBaseline,
                            decideBaseline = startDecideBaseline,
                            questActionCount = startQuestActionCount,
                            wakeSeq = startWakeSeq,
                            loopLive = startLoopLive,
                            pinned = startPinned,
                            meatPre,
                            money = startMoney,
                            turnIns = startTurnInsL
                        },
                        kill = new
                        {
                            firstFreshDecision,
                            firstFreshWake,
                            decideTarget,
                            decideStop,
                            decideCombat,
                            seenTarget,
                            seenStop,
                            seenCombat,
                            preDeathLandingsOurs,
                            killHpTrail,
                            startHp = startTargetHp,
                            killHpMin,
                            killHpMaxSeen,
                            lastHpBeforeEnd,
                            deathObserved,
                            deathWake,
                            despawned,
                            leashSuspect,
                            leashDetail,
                            selectedStable,
                            retrackCount,
                            distTrail,
                            targetSamples
                        },
                        teardown = new
                        {
                            teardownProven,
                            teardownFalseWake,
                            teardownWakes,
                            endIsAutoAttack,
                            endCurrentTarget,
                            targetDeadWithdrawals = targetDeadDiags,
                            postDeathLandingsOurs,
                            newLoopElsewhere,
                            isAutoAttackTrail
                        },
                        range = new
                        {
                            stopRadiusM = StopRadiusM,
                            finalDistM = endDist
                        },
                        sideEffects = new
                        {
                            castLootHits = castLootHits.Count,
                            item4058Delta = meatDelta,
                            creditDelta,
                            moneyDelta,
                            meatPre,
                            meatPost,
                            questStillActive,
                            endQuestStatus
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
                        $"{{\"scenario\":\"g7a-kill\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"G7A-GATE verdict={verdict} boundary={failBoundary} target={startTargetObjId} " +
                $"hp={startTargetHp}>0 dead={deathObserved} tornDown={teardownProven} meat={meatPre}>{meatPost} castLoot={castLootHits.Count} legs={legs.Count}");
        }
    }

    // ---- fixture: teleport to a live 3475 spawner + Fixture-SAFE place ~2 m off ----
    private bool StageAtBoar(BotDriveClient bridge, StringBuilder evidence, out string spawner, out double flat, out uint boarObjId)
    {
        spawner = "ABSENT";
        flat = double.NaN;
        boarObjId = 0;
        try
        {
            var teleportResp = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{BoarTemplate3475}}}", 60_000);
            spawner = teleportResp.TryGetProperty("x", out var stx) && teleportResp.TryGetProperty("y", out var sty) && teleportResp.TryGetProperty("z", out var stz)
                ? FormattableString.Invariant($"{stx.GetDouble():F1},{sty.GetDouble():F1},{stz.GetDouble():F1}") : "ABSENT";
            boarObjId = PollNpcObjId(bridge, BoarTemplate3475);
            if (boarObjId != 0 && spawner != "ABSENT")
            {
                var sp = spawner.Split(',');
                var px = double.Parse(sp[0], CultureInfo.InvariantCulture) + StageOffsetM;
                var place = bridge.Call(FormattableString.Invariant(
                    $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"place\",\"x\":{px:F1},\"y\":{sp[1]},\"z\":{sp[2]}}}"), 60_000);
                var charPos = ReadPos(bridge);
                flat = FlatDist(charPos, spawner);
                evidence.AppendLine($"- place RESPONSE verbatim: {place}");
                return !double.IsNaN(flat) && flat >= 1.0 && flat <= StopRadiusM;
            }
            return false;
        }
        catch (Exception ex)
        {
            evidence.AppendLine($"- stage threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private sealed record NpcFull(int Hp, int MaxHp);

    private static NpcFull ReadNpcFull(BotDriveClient bridge, uint objId)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            var hp = el.TryGetProperty("hp", out var h) && h.TryGetInt32(out var hn) ? hn : -1;
            var maxHp = el.TryGetProperty("maxHp", out var m) && m.TryGetInt32(out var mn) ? mn : -1;
            return new NpcFull(hp, maxHp);
        }
        catch
        {
            return new NpcFull(-1, -1);
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
    /// G7a inverted exclusion scan: Cast/Loot always void the run (AutoAttack is
    /// the only legal combat verb — landings are counted separately per target).
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
                    // QuestObjectiveTargetDiag lines are selection-time proposal diagnostics
                    // (QuestBehavior.LogObjectiveFunnel emits pre-arbitration, pre-dispatch),
                    // never dispatched combat — their combat=[...verb=AutoAttack...] fragment
                    // is a proposal description, not an audit row. Exclude them so only
                    // real combat audit rows can fail the run.
                    if (line.Contains("QuestObjectiveTargetDiag", StringComparison.Ordinal))
                        continue;
                    if ((line.Contains($"char={charId}", StringComparison.Ordinal)
                            || line.Contains(botName, StringComparison.Ordinal))
                        && Regex.IsMatch(line, @"\b(Cast|Loot)\b")
                        && !line.Contains("AutoAttack", StringComparison.Ordinal)
                        && !line.Contains("StopAutoAttack", StringComparison.Ordinal))
                        hits.Add(line);
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

    /// <summary>
    /// G7a death-wake grant probe: uncapped QuestObjectiveLootDiag Completed
    /// grant lines naming OUR char + quest 251 + OUR objId, read from the
    /// same log windows the gate scans. Used only by the zero-live-wake
    /// teardown edge to prove the corpse loot landed in the death's own
    /// observe cycle; never gate input.
    /// </summary>
    private static List<string> ScanLootGrantLines(long gameLogOffset, long gameRestartLogOffset, uint charId, uint targetObjId)
    {
        var hits = new List<string>();
        if (charId == 0 || targetObjId == 0)
            return hits;
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
                    if (line.Contains("QuestObjectiveLootDiag", StringComparison.Ordinal)
                        && Regex.IsMatch(line, $@"\bchar={charId}\b")
                        && line.Contains("quest=251", StringComparison.Ordinal)
                        && Regex.IsMatch(line, $@"\btarget={targetObjId}\b")
                        && line.Contains("state=Completed", StringComparison.Ordinal)
                        && Regex.IsMatch(line, @"\bgrant=[1-9]\d*\b"))
                        hits.Add(line);
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
                Console.WriteLine("[g7a] " + line);
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
                    Console.WriteLine("[g7a] " + kline);
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
                scenario = "g7a-kill-loop-teardown",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "loot", "objective-credit", "turn-in", "rotation", "corpse-detection", "any other quest" },
                callPath = "bridge quest wake → manager Spawn/Activate + scheduler Wake → BotGoalArbiterStepExecutor (arbitrate → BotRoamStepExecutor.StepQuestLeg → QuestBehavior.Run G4 251 path + G5 pursuit proposal + G6 combat proposal → QuestObjectiveTargetSelector.Evaluate (G4 selection, reused production path) → Target proposal (25) → pursuit Stop proposal (24, hold-confirm withdrawal once settled) → combat AutoAttack proposal (23) → BotDecisionCycle.Execute → GameplayActor.AutoAttack; engine owns damage (TaskManager UseAutoAttackSkillTask ticks) + death (Unit.DoDie) + loop self-termination (next-tick StopAutoAttack); CombatProposal target-dead withdrawal blocks corpse re-dispatch",
                g4Leg = "reused (production G4 path selects the 3475 ObjId; the gate stages only position/vitals/quest-state, never a selection)",
                g5Leg = "reused (production G5 pursuit closes/holds; the gate starts inside 3.0m settled)",
                g6Leg = "reused (production G6 combat proposal started the loop during drive; the gate STARTs from that live end state)",
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
            Console.WriteLine($"G7A-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
