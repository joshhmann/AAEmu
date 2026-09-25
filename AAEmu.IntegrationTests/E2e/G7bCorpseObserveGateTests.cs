using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// G7b gate (CORPSE-RECOGNIZED, RECOGNITION-ONLY) — the quest leg RECOGNIZES
/// OUR corpse (names it as an objective target) across wakes, with identity
/// held and a container/despawn transition observed. Loot is NOT gate input:
/// landed Loot, LootDiag Completed grants and meat deltas are recorded as
/// G7c-owned evidence, never failures (see G7C-OWNED EVIDENCE below).
///
/// Production chain under test: the G7a end state (251 ACTIVE, OUR 3475
/// corpse Hp==0, quest-owned loop released at teardown) sustained while the
/// quest leg keeps waking. PASS-BEHAVIOR needs the recognition side to hold:
/// detectionHits>=1 (a quest-path funnel/decide line naming OUR corpse objId
/// in the post-death window), identity held (same ObjId named across wakes,
/// commit never moves off it), and an observed corpse transition (container
/// 2->0 or lootable true->false as reported by the funnel/observe payloads)
/// with corpse-gone/despawn corroboration read-only.
///
/// G7C-OWNED EVIDENCE (recorded, NON-FAILING): the production loop-release
/// path (QuestBehavior.cs LootProposal releases at teardown by construction)
/// dispatches Loot in the SAME decide cycle that recognizes the corpse — so a
/// landed Loot / LootDiag Completed / meat delta here is EXPECTED production
/// behavior, not a violation. The gate records lootObserved / grantObserved /
/// meat-delta lines for the G7c report and never fails on them. There is no
/// wake where corpse-named AND lootable AND verb=none; the old zero-loot
/// clause contradicted the production path.
///
/// SOURCE-READ NOTE (why the old exclusion was stale): earlier the gate
/// expected a corpse signal that withdrew without naming a lootable corpse and
/// therefore classified FAIL-BEHAVIOR/CORPSE-UNOBSERVED. Live evidence (lane
/// report g7b-corpse-report.20260923T063904701Z.json) shows the pursuit/
/// combat/loot funnel fragments co-occur and name the corpse; recognition is
/// therefore a live, provable contract on its own. Container count may still
/// come from the forward-compatible npcState 'container'/'containerBefore'
/// probe (returns -1 when absent = probe gap, not a failure); when the probe
/// is absent the transition is taken from the funnel/observe payloads
/// (lootable true->false). Recognition is NOT gated on the probe.
///
/// FIXTURE: fresh bot, level 10, vitals refilled (≥0.95), quest 251 PRE-HELD
/// via POST /api/actors/accept_quest at the true giver 3512, staged at a live
/// 3475 spawner then Fixture-SAFE placed ~2 m off, standstill-settled, DRIVEN
/// (G7a-style) to the G6 end state, then the KILL watch runs to OUR target's
/// authoritative death. Corpse START is re-taken at the fresh corpse
/// (corpse-gone at START = HARNESS/corpse-gone, never a loot failure). After
/// corpse START the test issues no gameplay commands (wake + observe +
/// read-only npcState HP polls only — never AutoAttack/Cast/Loot/move/kill).
///
/// Back-date witness machinery REMOVED: it existed only to bound the loot
/// exclusion window (option c) and is no longer needed — recognition timing is
/// taken directly from the wakes on which the corpse objId is named
/// (detectionWakes), and loot is recorded unconditionally as G7c-owned
/// evidence with no exclusion predicate. G7c owns loot.
[Collection("e2e")]
public class G7bCorpseObserveGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G7bCorpse" + Stamp;
    private static readonly string BotAccount = ("g7bcorpse" + Stamp).ToLowerInvariant();
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
    private const int DriveBudgetMs = 300_000;
    private const int KillBudgetMs = 600_000;
    private const int ObserveBudgetMs = 180_000;
    private const int MaxRestages = 2;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "g7b-corpse-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task QuestLegNamesCorpseWithoutTouchingIt()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G7b corpse-RECOGNITION gate (G7a end state → recognition-only corpse watch; loot recorded as G7c-owned evidence) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        uint ourCharacterId = 0;
        var spawnerXyz = "ABSENT";
        var stagedDist = double.NaN;
        // G7c-owned evidence buckets: loot/grant/meat records are collected for
        // the G7c report and NEVER join the fail chain (recognition-only gate).
        var g7cLootObserved = new List<string>();
        var g7cGrantObserved = new List<string>();
        var wakeCount = 0;
        var selectedStable = true;
        var retrackCount = 0;
        var lastDistOurs = double.NaN;
        var hpTrail = new List<string>();
        var startQuestState = new E2eQuestDriver.QuestStateSnapshot(false, null, null, []);
        string startQuestStatus = "";
        var startTargetObjId = 0u;
        var startTargetHp = -1;
        var stageHp = -1;
        var stageMaxHp = -1;
        var restages = 0;
        var driveSeconds = 0.0;
        int meatPre = -1, meatPost = -1;
        long startMoney = -1, startTurnInsL = -1;
        // Kill state.
        var killHpTrail = new List<string>();
        var killHpMin = -1;
        var deathObserved = false;
        var deathWake = 0;
        var lastHpBeforeEnd = -1;
        var leashSuspect = false;
        string leashDetail = "";
        // Corpse-START state (re-taken at the fresh corpse).
        var corpseStartTaken = false;
        var corpseGoneAtStart = false;
        var corpseHpAtStart = -999;
        var corpseStartWake = 0;
        int corpseMeat = -1, corpseContainer = -999;
        string corpseQuestStatus = "";
        var corpseQuestActive = false;
        long corpseMoney = -1, corpseTurnIns = -1;
        string corpseCycle = "ABSENT";
        // Observe state.
        var observeWakes = 0;
        var detectionHits = new List<string>();
        var detectionWakes = new List<int>();
        var detectionObjIds = new HashSet<uint>();
        var lootableSeen = new List<string>();
        var targetDeadDiags = new List<string>();
        var corpseGoneWake = 0;
        var endQuestStatus = "";
        var questStillActive = false;
        var decideHistory = new List<string>();
        var meatDelta = -999; var moneyDelta = -999L; var creditDelta = -999;
        var killEngineLines = new List<string>();

        try
        {
            KillForeignWebApiListeners(evidence);
            var adoptSw = Stopwatch.StartNew();
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
            try
            {
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{GiverNpc3512}}}", 60_000);
                var giverObjId = PollNpcObjId(bridge, GiverNpc3512);
                if (giverObjId == 0)
                {
                    preHoldDetail = "giver 3512 never materialized (objId=0 after 30s poll)";
                }
                else
                {
                    var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest251},\"acceptorType\":\"Npc\",\"acceptorId\":{GiverNpc3512},\"idempotencyKey\":\"g7b-prehold-{BotAccount}\"}}";
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

            try
            {
                var stopFire = await PostJsonAsync(http, "/api/actors/stop", $"{{\"bot\":\"{BotName}\"}}");
                var stopTrace = stopFire.GetProperty("trace_id").GetGuid();
                var stopPoll = await PollTerminalAsync(http, stopTrace, TimeSpan.FromSeconds(30));
                evidence.AppendLine($"- pre-stage stop (clear-only): state={stopPoll.GetProperty("state").GetString() ?? ""}");
            }
            catch (Exception ex)
            {
                evidence.AppendLine($"- pre-stage stop ATTEMPT-FAILED ({ex.GetType().Name}: {ex.Message}) (clear-only; proceeding to stage)");
            }
            Leg("pre-stage-stop", true, "clear-only stop attempted", 0);

            // ---- FIXTURE: stage INSIDE the 3.0 m stop radius (~2 m off) ----
            var stageSw = Stopwatch.StartNew();
            uint stageBoarObjId = 0;
            var staged = StageAtBoar(bridge, evidence, out spawnerXyz, out stagedDist, out stageBoarObjId);
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

            var (settled, settleTrail) = SettleRoute(bridge, 30_000);
            evidence.AppendLine($"- pre-drive settle trail: {settleTrail}");
            Leg("settle", settled, $"settled={settled} trail=[{settleTrail}]", 0);
            if (!settled)
            {
                failBoundary = "SETUP/settle";
                Assert.Fail($"bot never stood still before drive (trail=[{settleTrail}])");
            }

            // ---- DRIVE to the G6 end state (observe-only production loop) ----
            var gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            var gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            var driveSw = Stopwatch.StartNew();
            JsonElement observe = default;
            var driveResult = DriveToG6End(bridge, evidence, gameLogOffset, gameRestartLogOffset, ourCharacterId,
                ref observe, ref wakeCount, ref selectedObjId, ref selectedStable, ref retrackCount,
                ref distTrail, ref hpTrail, ref lastDistOurs, ref stageHp, ref stageMaxHp,
                ref spawnerXyz, ref stagedDist, ref restages, decideHistory);
            driveSeconds = driveSw.Elapsed.TotalSeconds;
            var driveHpNow = driveResult.HpNow;
            var driveLoopLive = driveResult.LoopLive;
            var drivePinned = driveResult.Pinned;
            var driveHpFell = driveResult.HpFell;
            Leg("drive-to-g6-end", driveResult.EndState,
                $"loopLive={driveLoopLive} pinned={drivePinned} hpFell={driveHpFell} hpNow={driveHpNow} stageHp={stageHp} selected={selectedObjId} stable={selectedStable} liveDist={lastDistOurs:0.0}m restages={restages} wakes={wakeCount}",
                driveSeconds);
            evidence.AppendLine($"- drive HP trail: {string.Join(" | ", hpTrail.Take(30))}");
            if (driveHpNow == 0)
            {
                failBoundary = "HARNESS/fixture-overshoot";
                Assert.Fail($"prey keeps dying before a damaged-but-alive START is observable ({restages} restages); refusing to force the fixture");
            }
            if (!driveResult.EndState)
            {
                // K1 idle-drive exit takes precedence: the drive ended early with the
                // frozen boundary name (same name the spent-budget path converges on).
                failBoundary = !string.IsNullOrEmpty(driveResult.IdleExit) ? driveResult.IdleExit : "COMBAT/no-loop-to-sustain";
                Assert.Fail($"G6 end state unreachable (loopLive={driveLoopLive} pinned={drivePinned} hpFell={driveHpFell} hpNow={driveHpNow} selected={selectedObjId} stable={selectedStable})");
            }

            // ---- AUTHORITATIVE START (G6 end state; read ONCE) ----
            meatPre = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var preStart = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            startQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            startQuestStatus = startQuestState.Status?.ToString() ?? "";
            startTargetObjId = selectedObjId;
            startMoney = preStart.TryGetProperty("money", out var smEl) && smEl.ValueKind == JsonValueKind.Number && smEl.TryGetInt64(out var smStart) ? smStart : -1L;
            startTurnInsL = GetInt(preStart, "turnIns");
            var startLoopLive = GetBool(preStart, "isAutoAttack");
            var startPinned = GetUInt(preStart, "currentTargetObjId") == startTargetObjId;
            startTargetHp = ReadNpcHp(bridge, startTargetObjId);
            var startSnapGap = FlatDist(
                FormattableString.Invariant($"{GetDbl(preStart, "x"):F1},{GetDbl(preStart, "y"):F1}"),
                FormattableString.Invariant($"{GetDbl(preStart, "actorX"):F1},{GetDbl(preStart, "actorY"):F1}"));
            var startRangeOk = !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM;
            var startIdentityOk = !double.IsNaN(startSnapGap) && startSnapGap < 0.5;
            var startDamagedAlive = startTargetHp > 0 && stageHp > 0 && startTargetHp < stageHp;
            var startOk = startRangeOk && startIdentityOk && startLoopLive && startPinned && startDamagedAlive && startQuestState.Active;
            Leg("start-snapshot", startOk,
                $"charId={ourCharacterId} active251={startQuestState.Active} target={startTargetObjId}/t{BoarTemplate3475} hp={startTargetHp}(<stage {stageHp}) loopLive={startLoopLive} pinned={startPinned} gap={startSnapGap:0.0}m liveDist={lastDistOurs:0.0}m meat={meatPre}",
                0);
            evidence.AppendLine($"- START: target objId={startTargetObjId} hp={startTargetHp}/{stageMaxHp} quest={startQuestStatus} meat={meatPre} money={startMoney} turnIns={startTurnInsL}");
            if (!startOk)
            {
                failBoundary = "HARNESS/START-RANGE";
                Assert.Fail($"START outside the G6-end-state window (liveDist={lastDistOurs:0.0}m gap={startSnapGap:0.0}m hp={startTargetHp} loopLive={startLoopLive} pinned={startPinned}); refusing START");
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- KILL watch: bounded, observe ONLY ----
            var execSw = Stopwatch.StartNew();
            var killDeadline = Environment.TickCount64 + KillBudgetMs;
            killHpMin = startTargetHp;
            lastHpBeforeEnd = startTargetHp;
            while (Environment.TickCount64 < killDeadline)
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
                wakeCount++;
                var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                foreach (var f in funnels)
                {
                    if (f.Selected != 0 && f.Selected != startTargetObjId && !deathObserved)
                    {
                        selectedStable = false;
                        retrackCount++;
                    }
                    if (f.Combat.Contains("validate=target-dead", StringComparison.Ordinal)
                        && f.Combat.Contains(startTargetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        && targetDeadDiags.Count < 10)
                        targetDeadDiags.Add($"wake={wakeCount} combat=[{f.Combat}]");
                }
                ScanCombatExclusion(GetStr(observe, "questDecideDetail"), $"liveAction={GetStr(observe, "liveAction")}", g7cLootObserved);
                var hpNow = ReadNpcHp(bridge, startTargetObjId);
                if (hpNow == 0)
                {
                    deathObserved = true;
                    deathWake = wakeCount;
                    killHpMin = 0;
                    lastHpBeforeEnd = 0;
                    killHpTrail.Add($"wake={wakeCount} hp=0 DEAD");
                    break;
                }
                if (hpNow < 0)
                {
                    lastHpBeforeEnd = killHpMin;
                    break;
                }
                lastHpBeforeEnd = hpNow;
                if (hpNow < killHpMin)
                    killHpMin = hpNow;
                if (killHpTrail.Count == 0 || killHpTrail[^1] != $"wake={wakeCount} hp={hpNow}")
                    killHpTrail.Add($"wake={wakeCount} hp={hpNow}");
                if (stageMaxHp > 0 && killHpMin > 0 && killHpMin < stageMaxHp
                    && hpNow - killHpMin > 0.25 * stageMaxHp && !leashSuspect)
                {
                    leashSuspect = true;
                    leashDetail = $"wake={wakeCount} hp rose {killHpMin}->{hpNow} (max {stageMaxHp}; >25% rebound — leash-reset suspect)";
                    evidence.AppendLine($"- LEASH-SUSPECT: {leashDetail}");
                }
                Thread.Sleep(2000);
            }
            evidence.AppendLine($"- kill HP trail: {string.Join(" | ", killHpTrail.Take(40))} (startHp={startTargetHp} min={killHpMin} dead={deathObserved} lastHp={lastHpBeforeEnd})");
            Leg("kill", deathObserved,
                $"target={startTargetObjId} hp {startTargetHp}->0 dead={deathObserved} atWake={deathWake} targetDeadWithdrawals={targetDeadDiags.Count}",
                0);

            if (!deathObserved)
            {
                var hpVanished = ReadNpcHp(bridge, startTargetObjId) < 0;
                if (hpVanished && lastHpBeforeEnd > 0)
                {
                    verdict = "FAIL-BEHAVIOR/TARGET-LOST";
                    failBoundary = "TARGET/lost";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, lastHp = lastHpBeforeEnd, interpretation = "OUR objId left the world with last-HP>0 and no Hp==0 ever observed — lost, never dead" };
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
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, leashDetail, interpretation = "HP sawtooth coincided with the kill timeout — fixture DPS/leash race, never a behavior fail" };
                }
                else
                {
                    verdict = "FAIL-BEHAVIOR/COMBAT-NO-KILL";
                    failBoundary = "COMBAT/no-kill";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, startHp = startTargetHp, killHpMin, lastHp = lastHpBeforeEnd, killBudgetMs = KillBudgetMs, interpretation = "sustained quest-owned loop never drove OUR target to Hp<=0 inside the kill budget" };
                }
                Leg("gate", false, $"{verdict} at {failBoundary}: target={startTargetObjId}", execSw.Elapsed.TotalSeconds);
                Assert.True(passed, $"G7b {verdict} at {failBoundary}: target={startTargetObjId}");
            }

            // ---- CORPSE START (re-taken at the fresh corpse; read ONCE) ----
            // Corpse-gone HERE is HARNESS/corpse-gone — never a loot failure.
            corpseHpAtStart = ReadNpcHp(bridge, startTargetObjId);
            corpseGoneAtStart = corpseHpAtStart < 0;
            corpseStartWake = wakeCount;
            if (corpseGoneAtStart)
            {
                verdict = "UNKNOWN/HARNESS/CORPSE-GONE";
                failBoundary = "HARNESS/corpse-gone";
                failingCondition = new
                {
                    boundary = failBoundary,
                    corpse = startTargetObjId,
                    deathWake,
                    corpseStartWake,
                    interpretation = "OUR corpse objId left the world between authoritative death (Hp==0 observed) and corpse START — despawned before observation; harness/state race, never a loot failure"
                };
                Leg("corpse-start", false, $"corpse {startTargetObjId} gone at START (deathWake={deathWake}); refusing observation", 0);
                Leg("gate", false, $"{verdict} at {failBoundary}: corpse={startTargetObjId}", execSw.Elapsed.TotalSeconds);
                Assert.True(passed, $"G7b {verdict} at {failBoundary}: corpse={startTargetObjId}");
            }
            var corpseStartObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            corpseMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var corpseQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            corpseQuestActive = corpseQuestState.Active;
            corpseQuestStatus = corpseQuestState.Status?.ToString() ?? "";
            corpseMoney = corpseStartObs.TryGetProperty("money", out var cmEl) && cmEl.ValueKind == JsonValueKind.Number && cmEl.TryGetInt64(out var cmV) ? cmV : -1L;
            corpseTurnIns = GetInt(corpseStartObs, "turnIns");
            corpseCycle = Regex.Match(GetStr(corpseStartObs, "questDecideDetail"), @"cycle=([^\s\]]+)").Success
                ? Regex.Match(GetStr(corpseStartObs, "questDecideDetail"), @"cycle=([^\s\]]+)").Groups[1].Value : "ABSENT";
            corpseContainer = ReadNpcContainer(bridge, startTargetObjId);
            corpseStartTaken = true;
            var corpseStartOk = corpseHpAtStart == 0 && corpseQuestActive;
            Leg("corpse-start", corpseStartOk,
                $"corpse={startTargetObjId} hp={corpseHpAtStart}(REQUIRE 0) quest={corpseQuestStatus} active={corpseQuestActive} meat={corpseMeat} container(probe)={corpseContainer} cycle=[{corpseCycle}]",
                0);
            evidence.AppendLine($"- CORPSE START: objId={startTargetObjId} hp={corpseHpAtStart} quest={corpseQuestStatus} meat={corpseMeat} container(probe)={corpseContainer} cycle=[{corpseCycle}]");
            if (!corpseStartOk)
            {
                failBoundary = "HARNESS/corpse-start";
                failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, hp = corpseHpAtStart, questActive = corpseQuestActive, interpretation = "corpse START outside the observable window (hp!=0 or quest inactive); refusing observation" };
                Leg("gate", false, $"{verdict} at {failBoundary}: corpse={startTargetObjId}", execSw.Elapsed.TotalSeconds);
                Assert.True(passed, $"G7b {verdict} at {failBoundary}: corpse={startTargetObjId}");
            }

            // ---- CORPSE-RECOGNITION watch: bounded, no gameplay commands ----
            // Asserts the quest leg NAMES OUR corpse (recognition), holds that
            // identity across wakes, and exhibits the corpse transition. The
            // harness itself issues only wake + observe + read-only npcState
            // polls; loot the bot's own production loop dispatches is recorded
            // as G7c-owned evidence, never a failure.
            var observeDeadline = Environment.TickCount64 + ObserveBudgetMs;
            while (Environment.TickCount64 < observeDeadline)
            {
                try
                {
                    observe = bridge.Call(
                        $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":5000}}", 60_000);
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
                wakeCount++;
                observeWakes++;
                var decide = GetStr(observe, "questDecideDetail");
                if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                    decideHistory.Add(decide);
                // ---- G7c-owned loot evidence (NON-FAILING) --------------------
                // The production loop-release path dispatches Loot in the SAME
                // decide cycle that recognizes the corpse (QuestBehavior.cs
                // LootProposal releases at teardown), so landed Loot / LootDiag
                // grants here are EXPECTED. Record them for the G7c report;
                // never gate input.
                ScanCombatExclusion(decide, $"liveAction={GetStr(observe, "liveAction")} lastAction={GetStr(observe, "lastAction")}", g7cLootObserved);
                ScanLootHits(decide, g7cLootObserved);
                foreach (var gl in ScanLootDiagGrants(gameLogOffset, gameRestartLogOffset, ourCharacterId, g7cGrantObserved.Count))
                    g7cGrantObserved.Add(gl);
                ScanDetection(decide, startTargetObjId, observeWakes, detectionHits, detectionWakes, detectionObjIds);
                ScanLootableSeen(decide, startTargetObjId, lootableSeen);
                var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                foreach (var f in funnels)
                {
                    if (f.Combat.Contains("validate=target-dead", StringComparison.Ordinal)
                        && f.Combat.Contains(startTargetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        && targetDeadDiags.Count < 10)
                        targetDeadDiags.Add($"wake={wakeCount} combat=[{f.Combat}]");
                    ScanDetection(f.Pursuit, startTargetObjId, observeWakes, detectionHits, detectionWakes, detectionObjIds);
                    ScanDetection(f.Combat, startTargetObjId, observeWakes, detectionHits, detectionWakes, detectionObjIds);
                    ScanDetection(f.Summary, startTargetObjId, observeWakes, detectionHits, detectionWakes, detectionObjIds);
                    ScanLootableSeen(f.Pursuit + " " + f.Combat, startTargetObjId, lootableSeen);
                    ScanLootHits(f.Pursuit + " " + f.Combat, g7cLootObserved);
                }
                if (corpseGoneWake == 0 && ReadNpcHp(bridge, startTargetObjId) < 0)
                {
                    corpseGoneWake = wakeCount;
                    evidence.AppendLine($"- corpse despawned mid-observe at wake={wakeCount} (recorded only; START presence already proven)");
                    break;
                }
                Thread.Sleep(1000);
            }
            evidence.AppendLine($"- observe wakes: {observeWakes} detectionHits={detectionHits.Count} targetDeadWithdrawals={targetDeadDiags.Count} corpseGoneWake={corpseGoneWake}");
            foreach (var d in detectionHits.Take(10))
                evidence.AppendLine($"  [DETECT {d}]");
            foreach (var d in targetDeadDiags.Take(10))
                evidence.AppendLine($"  [WITHDRAW {d}]");

            // ---- POST proofs (read-only, recognition-only) ------------------
            // Meat/money/turn-in are recorded as G7c-owned evidence only: the
            // production loop-release path grants loot by construction, so no
            // side-effect delta can fail this gate.
            killEngineLines = ScanKillLines(gameLogOffset, gameRestartLogOffset, startTargetObjId);
            var containerEnd = ReadNpcContainer(bridge, startTargetObjId);
            if (meatPost < 0)
                meatPost = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            questStillActive = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            var endQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            endQuestStatus = endQuestState.Status?.ToString() ?? "";
            var postObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            var turnIns = GetInt(postObs, "turnIns");
            var moneyPost = postObs.TryGetProperty("money", out var mpEl) && mpEl.ValueKind == JsonValueKind.Number && mpEl.TryGetInt64(out var mpPost) ? mpPost : -1L;
            var fullLogLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
            ScanCombatExclusion(GetStr(postObs, "questDecideDetail"), $"lastAction={GetStr(postObs, "lastAction")}", g7cLootObserved);
            foreach (var l in fullLogLoot)
                if (!g7cLootObserved.Contains(l))
                    g7cLootObserved.Add(l);
            foreach (var gl in ScanLootDiagGrants(gameLogOffset, gameRestartLogOffset, ourCharacterId, g7cGrantObserved.Count))
                g7cGrantObserved.Add(gl);
            meatDelta = meatPre >= 0 && meatPost >= 0 ? meatPost - meatPre : -999;
            moneyDelta = startMoney >= 0 && moneyPost >= 0 ? moneyPost - startMoney : -999L;
            creditDelta = (turnIns - startTurnInsL) != 0 || (questStillActive != startQuestState.Active) || !string.Equals(endQuestStatus, startQuestStatus, StringComparison.Ordinal) ? 1 : 0;
            execSeconds = execSw.Elapsed.TotalSeconds;

            // ---- RECOGNITION assertions (the gate contract) ------------------
            // 1. Detection: at least one quest-path signal named OUR corpse objId.
            var recognitionDetected = detectionHits.Count >= 1;
            // 2. Identity held: every corpse objId named is OUR target, named
            //    across >=2 distinct wakes, and the funnel commitment never
            //    moved off OUR target.
            var identityHeld = selectedStable && retrackCount == 0
                && detectionObjIds.Count > 0 && detectionObjIds.All(id => id == startTargetObjId);
            var namedAcrossWakes = detectionWakes.Distinct().Count() >= 2;
            // 3. Corpse transition observed: container/lootable state moved
            //    (2->0 or lootable true->false). The probe path is preferred
            //    (container>=1 then 0); when it is absent (-1) the funnel/observe
            //    payloads carry the transition. Recognition is not gated on the
            //    probe itself.
            var containerProbeTransition = corpseContainer >= 1 && containerEnd == 0;
            var lootableTrue = lootableSeen.Any(s => s.Contains("lootable=true", StringComparison.Ordinal));
            var lootableFalse = lootableSeen.Any(s => s.Contains("lootable=false", StringComparison.Ordinal));
            var lootableTransition = lootableTrue && lootableFalse;
            var containerTransitionObserved = containerProbeTransition || lootableTransition;

            evidence.AppendLine($"- recognition: detected={recognitionDetected} detectionHits={detectionHits.Count} namedWakes={detectionWakes.Distinct().Count()} identityHeld={identityHeld} (selectedStable={selectedStable} retrack={retrackCount})");
            evidence.AppendLine($"- corpse transition: probe {corpseContainer}->{containerEnd} (probeTransition={containerProbeTransition}) lootableSeen=[{string.Join(" ", lootableSeen.Take(8))}] lootableTransition={lootableTransition} observed={containerTransitionObserved}");
            evidence.AppendLine($"- container probe: start={corpseContainer} end={containerEnd} (-1 = probe absent; funnel/observe payloads carry lootable/container state)");
            evidence.AppendLine($"- engine kill lines ({killEngineLines.Count}):");
            foreach (var l in killEngineLines.Take(5))
                evidence.AppendLine($"  [{l}]");
            evidence.AppendLine($"- G7c-OWNED EVIDENCE (non-failing): lootObserved={(g7cLootObserved.Count == 0 ? "NONE" : string.Join("; ", g7cLootObserved.Take(10)))}");
            evidence.AppendLine($"- G7c-OWNED EVIDENCE (non-failing): grantObserved={(g7cGrantObserved.Count == 0 ? "NONE" : string.Join("; ", g7cGrantObserved.Take(10)))}");
            evidence.AppendLine($"- G7c-OWNED EVIDENCE (non-failing): meatDelta={meatDelta} ({meatPre}->{meatPost}) moneyDelta={moneyDelta} creditDelta={creditDelta} (turnIns {startTurnInsL}->{turnIns}, active {startQuestState.Active}->{questStillActive}, status {startQuestStatus}->{endQuestStatus})");
            Leg("observe", true,
                $"observeWakes={observeWakes} detected={recognitionDetected} detectionHits={detectionHits.Count} namedWakes={detectionWakes.Distinct().Count()} transition={containerTransitionObserved} g7cLoot={g7cLootObserved.Count} g7cGrant={g7cGrantObserved.Count}",
                execSeconds);

            if (!recognitionDetected)
            {
                verdict = "FAIL-BEHAVIOR/CORPSE-UNOBSERVED";
                failBoundary = "CORPSE/unobserved";
                failingCondition = new
                {
                    boundary = failBoundary,
                    corpse = startTargetObjId,
                    observeWakes,
                    targetDeadWithdrawals = targetDeadDiags.Count,
                    containerProbe = corpseContainer,
                    engineKillLines = killEngineLines.Take(3).ToList(),
                    missingPredicate = "no quest-path signal named OUR corpse objId as a target/corpse during the observe window (recognition-only contract): QuestBehavior's post-death path must name the corpse (e.g. corpse={objId} lootable=.../container=... funnel fragment) at least once",
                    interpretation = $"quest leg woke {observeWakes}x over OUR corpse {startTargetObjId} and never named it (only target-dead withdrawals: {targetDeadDiags.Count}); corpse side proven by Hp==0 + engine kill line"
                };
            }
            else if (!identityHeld || !namedAcrossWakes)
            {
                verdict = "FAIL-BEHAVIOR/CORPSE-IDENTITY-LOST";
                failBoundary = "CORPSE/identity";
                failingCondition = new
                {
                    boundary = failBoundary,
                    corpse = startTargetObjId,
                    detectionHits = detectionHits.Take(5).ToList(),
                    namedWakes = detectionWakes.Distinct().Count(),
                    selectedStable,
                    retrackCount,
                    interpretation = "corpse recognized but identity not held across wakes (commitment moved off OUR objId or the ObjId was named on fewer than two wakes)"
                };
            }
            else if (!containerTransitionObserved)
            {
                verdict = "FAIL-BEHAVIOR/CONTAINER-TRANSITION-UNOBSERVED";
                failBoundary = "CONTAINER/unobserved";
                failingCondition = new
                {
                    boundary = failBoundary,
                    corpse = startTargetObjId,
                    detectionHits = detectionHits.Take(5).ToList(),
                    containerProbeStart = corpseContainer,
                    containerProbeEnd = containerEnd,
                    lootableSeen = lootableSeen.Take(8).ToList(),
                    interpretation = "corpse recognized and identity held, but no corpse transition (container 2->0 or lootable true->false) was observed in the probe or funnel/observe payloads"
                };
            }
            else
            {
                passed = true;
                verdict = "PASS-BEHAVIOR";
                failBoundary = "none";
                claim = $"corpse {startTargetObjId} (3475, Hp 0, objId held) RECOGNIZED quest-side ({detectionHits.Count} signal(s) across {detectionWakes.Distinct().Count()} wake(s)), identity held, corpse transition observed (probe {corpseContainer}->{containerEnd}; lootable transition={lootableTransition}); loot/grant/meat deltas recorded as G7c-owned evidence and NOT gated here";
                failingCondition = new
                {
                    boundary = "none",
                    corpse = startTargetObjId,
                    detectionHits = detectionHits.Take(5).ToList(),
                    namedWakes = detectionWakes.Distinct().Count(),
                    containerProbeStart = corpseContainer,
                    containerProbeEnd = containerEnd,
                    lootableTransition,
                    corpseGoneWake,
                    g7cLootObserved = g7cLootObserved.Count,
                    g7cGrantObserved = g7cGrantObserved.Count,
                    meatDelta,
                    moneyDelta
                };
            }
            Leg("gate", passed, $"{verdict} at {failBoundary}: corpse={startTargetObjId} detection={detectionHits.Count} namedWakes={detectionWakes.Distinct().Count()} transition={containerTransitionObserved}",
                execSeconds);
            Assert.True(passed, $"G7b {verdict} at {failBoundary}: corpse={startTargetObjId}");
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
                        start = new
                        {
                            charId = ourCharacterId,
                            questActive = startQuestState.Active,
                            questStatus = startQuestStatus,
                            targetObjId = startTargetObjId,
                            targetHp = startTargetHp,
                            meatPre,
                            money = startMoney,
                            turnIns = startTurnInsL
                        },
                        kill = new
                        {
                            killHpTrail,
                            startHp = startTargetHp,
                            killHpMin,
                            lastHpBeforeEnd,
                            deathObserved,
                            deathWake,
                            leashSuspect,
                            leashDetail,
                            selectedStable,
                            retrackCount,
                            distTrail,
                            hpTrail,
                            killEngineLines
                        },
                        corpseStart = new
                        {
                            taken = corpseStartTaken,
                            goneAtStart = corpseGoneAtStart,
                            corpse = startTargetObjId,
                            hp = corpseHpAtStart,
                            wake = corpseStartWake,
                            questActive = corpseQuestActive,
                            questStatus = corpseQuestStatus,
                            meat = corpseMeat,
                            containerProbe = corpseContainer,
                            money = corpseMoney,
                            turnIns = corpseTurnIns,
                            cycle = corpseCycle
                        },
                        observe = new
                        {
                            observeWakes,
                            detectionHits,
                            detectionNamedWakes = detectionWakes.Distinct().OrderBy(w => w).ToList(),
                            detectionObjIds = detectionObjIds.OrderBy(i => i).ToList(),
                            identityHeld = selectedStable && retrackCount == 0,
                            selectedStable,
                            retrackCount,
                            lootableSeen,
                            targetDeadWithdrawals = targetDeadDiags,
                            corpseGoneWake,
                            g7cOwnedEvidence = new
                            {
                                lootObserved = g7cLootObserved,
                                grantObserved = g7cGrantObserved,
                                meatDelta,
                                moneyDelta,
                                creditDelta
                            }
                        },
                        sideEffects = new
                        {
                            item4058Delta = meatDelta,
                            creditDelta,
                            moneyDelta,
                            meatPre,
                            meatPost,
                            questStillActive,
                            endQuestStatus
                        },
                        wakeCount,
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
                        $"{{\"scenario\":\"g7b-corpse-observe\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"G7B-GATE verdict={verdict} boundary={failBoundary} corpse={startTargetObjId} " +
                $"hp={corpseHpAtStart} detection={detectionHits.Count} namedWakes={detectionWakes.Distinct().Count()} g7cLoot={g7cLootObserved.Count} g7cGrant={g7cGrantObserved.Count} legs={legs.Count}");
        }
    }

    private sealed record DriveEnd(bool EndState, bool LoopLive, bool Pinned, bool HpFell, int HpNow, string IdleExit);

    // ---- DRIVE to the G6 end state (observe-only; production loop fights) ----
    private DriveEnd DriveToG6End(BotDriveClient bridge, StringBuilder evidence,
        long gameLogOffset, long gameRestartLogOffset, uint charId,
        ref JsonElement observe, ref int wakeCount, ref uint selectedObjId,
        ref bool selectedStable, ref int retrackCount, ref List<double> distTrail,
        ref List<string> hpTrail, ref double lastDistOurs, ref int stageHp, ref int stageMaxHp,
        ref string spawnerXyz, ref double stagedDist, ref int restages, List<string> decideHistory)
    {
        var loopLive = false;
        var pinned = false;
        var hpFell = false;
        var hpNow = -1;
        // K1 failfast state (drive only): counts consecutive wakes with a
        // byte-identical questDecideDetail and no per-bot step signal. Fires
        // at 2; gated on zero drive progress (no loop live, no HP fall), so a
        // warm drive can never trip it. Per-bot baselines anchor on the first
        // wake (no pre-drive snapshot exists); budgets stay ceilings.
        var prevDecide = "";
        var idleWakes = 0;
        string idleExitBoundary = "";
        var k1ActionCount = 0;
        long k1WakeSeq = 0, k1TraceWakeSeq = 0, k1MaxTraceWakeSeq = 0;
        var k1Anchored = false;
        var driveDeadline = Environment.TickCount64 + DriveBudgetMs;
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
            wakeCount++;
            var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, charId);
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
            }
            if (GetBool(observe, "isAutoAttack"))
                loopLive = true;
            if (selectedObjId != 0 && GetUInt(observe, "currentTargetObjId") == selectedObjId)
                pinned = true;
            var hp = ReadNpcHp(bridge, selectedObjId);
            if (hp >= 0)
            {
                if (hpTrail.Count == 0 || hpTrail[^1] != $"wake={wakeCount} hp={hp}")
                    hpTrail.Add($"wake={wakeCount} hp={hp}");
                if (stageHp > 0 && hp < stageHp)
                    hpFell = true;
                hpNow = hp;
                if (hpNow == 0)
                    break;
            }
            if (loopLive && pinned && hpFell && hpNow > 0 && !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM)
                break;
            // K1 failfast (drive only): 2 consecutive wakes with unchanged
            // questDecideDetail and HasPerBotStepSignal == false end the drive
            // now with the frozen G3/G4 boundary name — no new strings. The
            // firstZero=sweep-empty payload form maps to DISCOVERY/sweep-empty,
            // anything else to ARBITRATION/no-quest-activity. Unreachable once
            // the drive shows life (end-state break above; loop/HP guards
            // below). Budgets stay ceilings.
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
            else if (!loopLive && !hpFell
                && string.Equals(decide, prevDecide, StringComparison.Ordinal)
                && !HasPerBotStepSignal(observe, charId, k1ActionCount, k1WakeSeq, k1TraceWakeSeq, k1MaxTraceWakeSeq))
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

        // Kill-before-START restage (bounded).
        while (hpNow == 0 && restages < MaxRestages)
        {
            restages++;
            evidence.AppendLine($"- drive overshoot #{restages}: prey {selectedObjId} died before START; restaging");
            var reOk = StageAtBoar(bridge, evidence, out spawnerXyz, out stagedDist, out var reBoar);
            var reNpc = ReadNpcFull(bridge, reBoar);
            stageHp = reNpc.Hp;
            stageMaxHp = reNpc.MaxHp;
            var (reSettled, reTrail) = reOk ? SettleRoute(bridge, 30_000) : (false, "stage-failed");
            evidence.AppendLine($"- restage #{restages}: {reTrail} ok={reOk} settled={reSettled} live3475={reBoar} hp={stageHp}/{stageMaxHp}");
            if (!reOk || !reSettled)
            {
                evidence.AppendLine($"- restage #{restages} FAILED (HARNESS/fixture-overshoot); refusing START");
                Assert.Fail($"restage #{restages} failed after a drive-phase kill (ok={reOk} settled={reSettled}); refusing START");
            }
            selectedObjId = 0;
            selectedStable = true;
            loopLive = false;
            pinned = false;
            hpFell = false;
            hpNow = -1;
            hpTrail.Clear();
            idleWakes = 0;
            prevDecide = "";
            k1Anchored = false;
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
                wakeCount++;
                var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, charId);
                foreach (var f in funnels)
                {
                    if (selectedObjId == 0 && f.Selected != 0)
                        selectedObjId = f.Selected;
                    else if (f.Selected != 0 && selectedObjId != 0 && f.Selected != selectedObjId)
                    {
                        selectedStable = false;
                        retrackCount++;
                    }
                    if (f.Selected != 0 && (selectedObjId == 0 || f.Selected == selectedObjId))
                        lastDistOurs = f.Dist;
                }
                if (GetBool(observe, "isAutoAttack"))
                    loopLive = true;
                if (selectedObjId != 0 && GetUInt(observe, "currentTargetObjId") == selectedObjId)
                    pinned = true;
                var hp = ReadNpcHp(bridge, selectedObjId);
                if (hp >= 0)
                {
                    if (hpTrail.Count == 0 || hpTrail[^1] != $"wake={wakeCount} hp={hp}")
                        hpTrail.Add($"wake={wakeCount} hp={hp}");
                    if (stageHp > 0 && hp < stageHp)
                        hpFell = true;
                    hpNow = hp;
                    if (hpNow == 0)
                        break;
                }
                if (loopLive && pinned && hpFell && hpNow > 0 && !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM)
                    break;
                // K1 failfast (drive only, post-restage): same shape as above — the
                // end-state break precedes it and the loop/HP guards gate it out
                // once the fresh staging shows life. Budgets stay ceilings.
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
                else if (!loopLive && !hpFell
                    && string.Equals(decide, prevDecide, StringComparison.Ordinal)
                    && !HasPerBotStepSignal(observe, charId, k1ActionCount, k1WakeSeq, k1TraceWakeSeq, k1MaxTraceWakeSeq))
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
        var endState = loopLive && pinned && hpFell && hpNow > 0 && selectedObjId != 0 && selectedStable && !double.IsNaN(lastDistOurs) && lastDistOurs <= StopRadiusM;
        return new DriveEnd(endState, loopLive, pinned, hpFell, hpNow, idleExitBoundary);
    }

    // (funnel charId is threaded from enroll; observe payloads carry no id)

    // ---- quest-side corpse-RECOGNITION scan: any quest-path line naming OUR
    // corpse objId with corpse/lootable/container semantics (diag
    // corpse={objId} lootable=.../container=... or equivalent). Recognition is
    // the whole gate contract now; a hit needs only the corpse objId plus
    // corpse semantics. Target-dead withdrawals do NOT match (no corpse naming).
    private static void ScanDetection(string text, uint corpseObjId, int wake, List<string> hits,
        List<int> namedWakes, HashSet<uint> namedObjIds)
    {
        if (string.IsNullOrEmpty(text) || corpseObjId == 0)
            return;
        var id = corpseObjId.ToString(CultureInfo.InvariantCulture);
        if (!text.Contains(id, StringComparison.Ordinal))
            return;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("corpse") || lower.Contains("lootable") || lower.Contains("hascorpsetoloot")
            || lower.Contains("hasloot") || lower.Contains("container"))
        {
            // Collect the corpse objIds this line actually names so the gate can
            // prove the SAME ObjId is held. When the signal names our corpse in
            // another form (no explicit corpse= field) the scan's own objId
            // containment is the proof, so fall back to ours.
            var namedCount = 0;
            foreach (Match cm in Regex.Matches(text, @"corpse=(\d+)"))
                if (uint.TryParse(cm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var named))
                {
                    namedObjIds.Add(named);
                    namedCount++;
                }
            if (namedCount == 0)
                namedObjIds.Add(corpseObjId);
            if (!namedWakes.Contains(wake))
                namedWakes.Add(wake);
            var clipped = text.Length > 240 ? text[..240] : text;
            var entry = $"wake={wake} [{clipped}]";
            if (!hits.Contains(entry))
                hits.Add(entry);
        }
    }

    // ---- corpse-transition scan: record the lootable/container state named
    // with OUR corpse objId so the gate can see the true->false / 2->0 move.
    private static void ScanLootableSeen(string text, uint corpseObjId, List<string> seen)
    {
        if (string.IsNullOrEmpty(text) || corpseObjId == 0)
            return;
        var id = corpseObjId.ToString(CultureInfo.InvariantCulture);
        if (!text.Contains(id, StringComparison.Ordinal))
            return;
        foreach (Match m in Regex.Matches(text, @"lootable=(true|false)"))
        {
            var state = $"wake-state {m.Value} corpse={id}";
            if (!seen.Contains(state))
                seen.Add(state);
        }
        foreach (Match m in Regex.Matches(text, @"container=(\d+)"))
        {
            var state = $"wake-state container={m.Groups[1].Value} corpse={id}";
            if (!seen.Contains(state))
                seen.Add(state);
        }
    }

    // ---- G7c-owned grant scan: QuestObjectiveLootDiag lines for OUR char with
    // a Completed grant (evidence only; never gate input). skipLines skips
    // already-seen lines.
    private static List<string> ScanLootDiagGrants(long gameLogOffset, long gameRestartLogOffset, uint charId, int skipLines)
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
                    if (line.Contains("QuestObjectiveLootDiag", StringComparison.Ordinal)
                        && (charId == 0 || line.Contains($"char={charId}", StringComparison.Ordinal))
                        && line.Contains("state=Completed", StringComparison.Ordinal))
                        lines.Add(line.Length > 240 ? line[..240] : line);
                }
            }
            catch
            {
            }
        }
        return lines.Skip(skipLines).ToList();
    }

    // ---- loot-dispatch scan: any Loot verb record (decide/observe/log text).
    // G7c-owned evidence only; never gate input.
    private static void ScanLootHits(string text, List<string> hits)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (!Regex.IsMatch(text, @"landed Loot\b"))
            return;
        var clipped = text.Length > 220 ? text[..220] : text;
        if (!hits.Contains(clipped))
            hits.Add(clipped);
    }

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

    // ---- engine kill corroboration: LootingContainer attribution line names
    // OUR corpse objId at death (read-only log scan; proves the corpse side,
    // never quest-side detection).
    private static List<string> ScanKillLines(long gameLogOffset, long gameRestartLogOffset, uint objId)
    {
        var hits = new List<string>();
        var needle = $"Unit killed without aggro: {objId} ";
        var idOnly = objId.ToString(CultureInfo.InvariantCulture);
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
                    if (line.Contains(needle, StringComparison.Ordinal)
                        || (line.Contains("Unit killed", StringComparison.Ordinal) && line.Contains(idOnly, StringComparison.Ordinal)))
                    {
                        hits.Add(line.Length > 220 ? line[..220] : line);
                        if (hits.Count >= 5)
                            break;
                    }
                }
            }
            catch
            {
            }
        }
        return hits;
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

    // ---- read-only container probe attempt (forward-compatible): the npcState
    // probe today exposes hp/alive only, so this returns -1 (probe gap, never
    // a loot failure). It reads an optional 'container'/'containerBefore'
    // field if a future probe carries it — WITHOUT dispatching loot.
    private static int ReadNpcContainer(BotDriveClient bridge, uint objId)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            if (el.TryGetProperty("container", out var c) && c.TryGetInt32(out var n))
                return n;
            if (el.TryGetProperty("containerBefore", out var c2) && c2.TryGetInt32(out var n2))
                return n2;
            return -1;
        }
        catch
        {
            return -1;
        }
    }

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
                        && (charId == 0 || line.Contains($"char={charId}", StringComparison.Ordinal)))
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

    // Stale-proc guard: before lane-adopt, kill processes LISTENing on the
    // shared WebApi port (E2E_WEBAPI_PORT, default :1280) whose cwd is NOT the
    // current E2E_ROOT runtime dir — i.e. a stale stack from a previous run /
    // sibling lane still holding the port. Only foreign-CWD listeners; never
    // the current lane's pid. SIGTERM first, SIGKILL after 10s if still alive;
    // each kill is logged to evidence. Linux-only (/proc scan); no-op elsewhere.
    private static void KillForeignWebApiListeners(StringBuilder evidence)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return;
            var port = E2eStack.WebApiPort;
            var laneRoot = Path.GetFullPath(Path.Combine(E2eStack.E2eRoot, "runtime"))
                .TrimEnd(Path.DirectorySeparatorChar);
            string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
            var self = Environment.ProcessId;
            var listeners = new HashSet<int>();
            foreach (var tcpFile in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
            {
                if (!File.Exists(tcpFile))
                    continue;
                foreach (var line in File.ReadAllLines(tcpFile).Skip(1))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 10)
                        continue;
                    // local_address is hex IP:PORT; state 0A = LISTEN.
                    var local = parts[1].Split(':');
                    var state = parts[3];
                    if (local.Length != 2 || !string.Equals(state, "0A", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!int.TryParse(local[1], System.Globalization.NumberStyles.HexNumber, null, out var lp) || lp != port)
                        continue;
                    var inode = parts[9];
                    foreach (var pidDir in Directory.GetDirectories("/proc"))
                    {
                        if (!int.TryParse(Path.GetFileName(pidDir), out var pid) || pid == self)
                            continue;
                        try
                        {
                            foreach (var fd in Directory.GetFiles(Path.Combine(pidDir, "fd")))
                            {
                                try
                                {
                                    var target = new FileInfo(fd).LinkTarget;
                                    if (target != null && target == $"socket:[{inode}]")
                                    {
                                        listeners.Add(pid);
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            foreach (var pid in listeners)
            {
                try
                {
                    string? cwd = null;
                    try { cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget; } catch { }
                    var normCwd = cwd != null ? Norm(cwd) : "";
                    var inThisLane = normCwd.Equals(laneRoot, StringComparison.Ordinal)
                        || normCwd.StartsWith(laneRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                    if (inThisLane)
                        continue; // current lane's own server — never touch.
                    var cmd = "";
                    try { cmd = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { }
                    evidence.AppendLine($"- stale-proc guard: SIGTERM foreign :{port} listener pid={pid} cwd={cwd ?? "?"} cmd=[{cmd}]");
                    Process killProc;
                    try { killProc = Process.GetProcessById(pid); }
                    catch { continue; }
                    try
                    {
                        killProc.Kill(entireProcessTree: false);
                        if (!killProc.WaitForExit(10_000))
                        {
                            evidence.AppendLine($"- stale-proc guard: pid={pid} survived SIGTERM 10s — SIGKILL");
                            killProc.Kill(entireProcessTree: true);
                            killProc.WaitForExit(10_000);
                        }
                        evidence.AppendLine($"- stale-proc guard: pid={pid} exited={killProc.HasExited}");
                    }
                    catch (Exception ex)
                    {
                        evidence.AppendLine($"- stale-proc guard: kill pid={pid} failed ({ex.GetType().Name}: {ex.Message})");
                    }
                }
                catch (Exception ex)
                {
                    evidence.AppendLine($"- stale-proc guard: pid={pid} scan failed ({ex.GetType().Name})");
                }
            }
        }
        catch (Exception ex)
        {
            evidence.AppendLine($"- stale-proc guard: scan skipped ({ex.GetType().Name}: {ex.Message})");
        }
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
                scenario = "g7b-corpse-observe",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "loot (G7c-owned: recorded as evidence only, never gated here)", "objective-credit", "turn-in", "rotation", "kill-teardown", "any other quest" },
                callPath = "bridge quest wake → scheduler Wake → quest leg (QuestBehavior.Run G4 251 path, pursuit + combat/loot proposals) → QuestObjectiveTargetSelector.Evaluate → BotDecisionCycle; recognition-only: the gate asserts the quest leg NAMES our corpse (identity held, transition observed) and records loot/grant/meat deltas as G7c-owned evidence — wake + observe + read-only npcState polls only",
                g7aLeg = "reused inside this test (fresh G7a-style run to authoritative death; corpse START re-taken at the fresh corpse)",
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
            Console.WriteLine($"G7B-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
