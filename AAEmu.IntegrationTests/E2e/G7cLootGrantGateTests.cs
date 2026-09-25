using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// G7c gate (LOOT-GRANT, quest-owned loot) — the quest leg takes OUR corpse
/// through the canonical verb and the grant conserves. The harness never
/// loots, casts, damages, or mutates: wake + observe + read-only npcState
/// HP polls only.
///
/// Production chain under test: the G7b end state (251 ACTIVE, OUR 3475
/// corpse Hp==0 with ObjId still held, quest-side recognition with
/// lootable=true and a non-empty container) sustained observe-only while the
/// quest leg's G7c loot proposal fires GameplayActor.Loot ONCE for the corpse
/// ObjId (loot-once memory: never re-dispatches for the same corpse).
///
/// PASS needs ALL of: exactly one canonical landed Loot per corpse (OUR corpse
/// never re-dispatched; a further DIFFERENT corpse is legal chaining, evidence
/// only) + Completed + Q4-style conservation (grant == containerBefore -
/// remaining, remaining == 0, every observed bag delta a gain) + meat after >=
/// before (dry meatΔ==0 only with container proof) + quest 251 still held and
/// not past Ready (Progress, or Ready when chaining legitimately reached the
/// objective count — NO credit assertion; G7d owns credit).
///
/// FIXTURE: fresh bot, level 10, vitals refilled (≥0.95), quest 251 PRE-HELD
/// via POST /api/actors/accept_quest at the true giver 3512, staged at a live
/// 3475 spawner then Fixture-SAFE placed ~2 m off, standstill-settled, DRIVEN
/// (G7a-style, observe-only) to the G6 end state, then the KILL watch runs
/// observe-only to OUR target's authoritative death. Corpse START is re-taken
/// at the fresh corpse (corpse-gone at START = HARNESS/corpse-gone, never a
/// loot failure). After corpse START the test issues no gameplay commands.
///
/// STOPS at grant mechanics: no credit assertions, no turn-in, no second
/// corpse. G7d owns credit.
[Collection("e2e")]
public class G7cLootGrantGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G7cLoot" + Stamp;
    private static readonly string BotAccount = ("g7cloot" + Stamp).ToLowerInvariant();
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
    private const int LootBudgetMs = 180_000;
    private const int MaxRestages = 2;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "g7c-loot-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task QuestLegLootsCorpseOnceWithConservingGrant()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G7c loot-grant gate (G7b end state → quest-owned loot, observe-only harness) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        var castHits = new List<string>();
        var lootHits = new List<string>();
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
        long startMoney = -1;
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
        int corpseMeat = -1;
        string corpseQuestStatus = "";
        var corpseQuestActive = false;
        long corpseMoney = -1;
        string corpseCycle = "ABSENT";
        // Corpse fixture proof (quest-side recognition + lootable + container).
        var lootOkDiags = new List<LootOkDiag>();
        var containerBefore = -1;
        // Loot-watch state.
        var lootWakes = 0;
        var lootDiagLines = new List<string>();
        var landedLootCount = 0;
        var lootGranted = -1;
        uint lootTarget = 0;
        var lootCompleted = false;
        var containerAfter = -1;
        var remainingProof = "none";
        var corpseGoneWake = 0;
        var postLootWakes = 0;
        var endQuestStatus = "";
        var questStillProgress = false;
        // Relaxed quest gate: held after the grant and not past Ready — chaining
        // may legally have driven the objective to Ready (credit stays G7d's).
        var questHeldBeforeReady = false;
        var decideHistory = new List<string>();
        var meatDelta = -999; var moneyDelta = -999L;
        var killEngineLines = new List<string>();

        try
        {
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = AssertLaneOwnsWebApiListeners();
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
            // Shared-port routing: every lane game server binds *:1280
            // (Kestrel SO_REUSEPORT), so each TCP connection hashes to ONE
            // lane. A fresh HttpClient re-hashes; keep the first client whose
            // backend answers enabled. Correct-lane delivery is proven later
            // by the bridge-visible quest-prehold check on q0 (2260).
            var routeSw = Stopwatch.StartNew();
            using var http = await EnsureWebApiClientAsync(evidence);
            Leg("webapi-route", true, "enabled WebApi backend reached (hashed route)", routeSw.Elapsed.TotalSeconds);
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
                    var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest251},\"acceptorType\":\"Npc\",\"acceptorId\":{GiverNpc3512},\"idempotencyKey\":\"g7c-prehold-{BotAccount}\"}}";
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

            // ---- DRIVE to the G6 end state (observe-only; production loop starts
            // the fight, never the harness). Breaks on loop-live + pinned +
            // damaged-but-alive. A kill before START restages (bounded).
            // G7a-verbatim entry: HP polls fall back to the staged boar until a
            // funnel names a selection, landed-Target decide arms backstop funnel
            // selection, and an empty fresh-decision discriminates
            // HARNESS/NO-FRESH-DECISION from COMBAT/no-loop-to-sustain.
            var gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            var gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            var liveHistory = new List<string>();
            var targetSamples = new List<string>();
            string firstFreshDecision = "";
            var firstFreshWake = 0;
            var wakeProven = 0;
            var seenTarget = false; var seenStop = false; var seenCombat = false;
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
                    var landedId = uint.Parse(landedTarget.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (selectedObjId == 0)
                        selectedObjId = landedId;
                }
                if (decide.Contains("landed Stop", StringComparison.Ordinal))
                    seenStop = true;
                var landedCombat = Regex.Match(decide, @"landed AutoAttack \(([^)]*)\)");
                if (landedCombat.Success)
                    seenCombat = true;
                ScanCastHits(decide, live, castHits);
                if (GetBool(observe, "isAutoAttack"))
                    driveLoopLive = true;
                if (selectedObjId != 0 && GetUInt(observe, "currentTargetObjId") == selectedObjId)
                    drivePinned = true;
                var hpNow = ReadNpcHp(bridge, selectedObjId != 0 ? selectedObjId : stageBoarObjId);
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
                        var landedId = uint.Parse(landedTarget.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (selectedObjId == 0)
                            selectedObjId = landedId;
                    }
                    if (decide.Contains("landed Stop", StringComparison.Ordinal))
                        seenStop = true;
                    var landedCombat = Regex.Match(decide, @"landed AutoAttack \(([^)]*)\)");
                    if (landedCombat.Success)
                        seenCombat = true;
                    if (GetBool(observe, "isAutoAttack"))
                        driveLoopLive = true;
                    if (selectedObjId != 0 && GetUInt(observe, "currentTargetObjId") == selectedObjId)
                        drivePinned = true;
                    var hpNow = ReadNpcHp(bridge, selectedObjId != 0 ? selectedObjId : stageBoarObjId);
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
            evidence.AppendLine($"- drive decide: firstFreshWake={firstFreshWake} fresh=[{firstFreshDecision}] seenTarget={seenTarget} seenStop={seenStop} seenCombat={seenCombat} wakeProven={wakeProven} liveStates={liveHistory.Count}");
            evidence.AppendLine($"- drive targets: {string.Join(" | ", targetSamples.Take(12))}");
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

            // ---- AUTHORITATIVE START (G6 end state; read ONCE) ----
            meatPre = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var preStart = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            startQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            startQuestStatus = startQuestState.Status?.ToString() ?? "";
            startTargetObjId = selectedObjId;
            startMoney = preStart.TryGetProperty("money", out var smEl) && smEl.ValueKind == JsonValueKind.Number && smEl.TryGetInt64(out var smStart) ? smStart : -1L;
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
            evidence.AppendLine($"- START: target objId={startTargetObjId} hp={startTargetHp}/{stageMaxHp} quest={startQuestStatus} meat={meatPre} money={startMoney}");
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
                ScanCastHits(GetStr(observe, "questDecideDetail"), $"liveAction={GetStr(observe, "liveAction")}", castHits);
                var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                foreach (var f in funnels)
                {
                    if (f.Selected != 0 && f.Selected != startTargetObjId && !deathObserved)
                    {
                        selectedStable = false;
                        retrackCount++;
                    }
                }
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
                $"target={startTargetObjId} hp {startTargetHp}->0 dead={deathObserved} atWake={deathWake}",
                0);

            if (!deathObserved)
            {
                var hpVanished = ReadNpcHp(bridge, startTargetObjId) < 0;
                if (hpVanished && lastHpBeforeEnd > 0)
                {
                    verdict = "FAIL-BEHAVIOR/TARGET-LOST";
                    failBoundary = "CORPSE/target-lost";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, lastHp = lastHpBeforeEnd, interpretation = "OUR objId left the world with last-HP>0 and no Hp==0 ever observed — lost, never dead" };
                }
                else if (!selectedStable)
                {
                    verdict = "FAIL-BEHAVIOR/TARGET-LOST";
                    failBoundary = "CORPSE/target-lost";
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
                    failBoundary = "LOOT/no-kill";
                    failingCondition = new { boundary = failBoundary, target = startTargetObjId, startHp = startTargetHp, killHpMin, lastHp = lastHpBeforeEnd, killBudgetMs = KillBudgetMs, interpretation = "sustained quest-owned loop never drove OUR target to Hp<=0 inside the kill budget" };
                }
                Leg("gate", false, $"{verdict} at {failBoundary}: target={startTargetObjId}", execSw.Elapsed.TotalSeconds);
                Assert.True(passed, $"G7c {verdict} at {failBoundary}: target={startTargetObjId}");
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
                Assert.True(passed, $"G7c {verdict} at {failBoundary}: corpse={startTargetObjId}");
            }
            var corpseStartObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            corpseMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var corpseQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            corpseQuestActive = corpseQuestState.Active;
            corpseQuestStatus = corpseQuestState.Status?.ToString() ?? "";
            corpseMoney = corpseStartObs.TryGetProperty("money", out var cmEl) && cmEl.ValueKind == JsonValueKind.Number && cmEl.TryGetInt64(out var cmV) ? cmV : -1L;
            corpseCycle = Regex.Match(GetStr(corpseStartObs, "questDecideDetail"), @"cycle=([^\s\]]+)").Success
                ? Regex.Match(GetStr(corpseStartObs, "questDecideDetail"), @"cycle=([^\s\]]+)").Groups[1].Value : "ABSENT";
            // Fixture proof (quest-side, read-only): the funnel loot arm must
            // name OUR corpse lootable with a non-empty container. Loop-release
            // seam (QuestBehavior.cs LootProposal withholds validate=ok while
            // character.IsAutoAttack, logging validate=loop-live instead): START
            // re-taken mid-teardown otherwise sees only the withhold arm. A
            // loop-live arm naming OUR corpse with container>=1 is provisional
            // START recognition (corpse named + non-empty container proven,
            // release pending teardown); then wait bounded (existing WakeWaitMs,
            // no new budget) for the validate=ok release arm before opening the
            // loot watch. containerBefore below always reads the release arm.
            lootOkDiags = ScanLootOk(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId);
            if (lootOkDiags.Count == 0)
            {
                var loopLiveDiags = ScanLootLoopLive(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId);
                if (loopLiveDiags.Any(d => d.Container >= 1))
                {
                    evidence.AppendLine($"- CORPSE START: loop-live provisional (validate=ok withheld pre-teardown): {loopLiveDiags.Count} loop-live arm(s), maxContainer={loopLiveDiags.Max(d => d.Container)}; waiting <=WakeWaitMs for the ok release");
                    var okWaitUntil = Environment.TickCount64 + WakeWaitMs;
                    // One scheduler wake releases the quest-owned loop teardown.
                    // Sleeping alone never schedules a fresh quest decision here.
                    try
                    {
                        // Do not spend the only release wake before the engine
                        // has cleared the loop-live guard. Bridge observe is a
                        // read-only projection, unlike queued actor Observe.
                        do
                        {
                            var probeBudgetMs = (int)Math.Max(1, okWaitUntil - Environment.TickCount64);
                            observe = bridge.Call(
                                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", probeBudgetMs);
                            if (!GetBool(observe, "isAutoAttack"))
                                break;
                            Thread.Sleep(100);
                        } while (Environment.TickCount64 < okWaitUntil);
                        if (GetBool(observe, "isAutoAttack"))
                            throw new TimeoutException("auto-attack teardown remained live through START release budget");
                        var remainingMs = (int)Math.Max(1, okWaitUntil - Environment.TickCount64);
                        observe = bridge.Call(
                            $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{remainingMs}}}", remainingMs);
                        wakeCount++;
                        evidence.AppendLine($"- CORPSE START: issued one release wake (wake={wakeCount})");
                    }
                    catch (Exception ex)
                    {
                        failBoundary = "HARNESS/release-wake";
                        verdict = "UNKNOWN/HARNESS/RELEASE-WAKE";
                        evidence.AppendLine($"- CORPSE START: release wake failed ({ex.GetType().Name}); no behavior verdict");
                        Assert.Fail("release wake did not return within the existing START budget");
                    }
                    lootOkDiags = ScanLootOk(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId);
                    while (lootOkDiags.Count == 0 && Environment.TickCount64 < okWaitUntil)
                    {
                        // Teardown already happened (auto-attack cleared above), so the next quest tick releases validate=ok by construction (loop-release seam). Production only ticks on wake — sleeping here watches a log nobody writes to — so wake once per poll (first wake fires immediately, no pre-sleep), then re-scan.
                        var remainingMs = (int)Math.Max(1, okWaitUntil - Environment.TickCount64);
                        var pollWaitMs = Math.Min(WakeWaitMs, remainingMs);
                        try
                        {
                            observe = bridge.Call(
                                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{pollWaitMs}}}", Math.Min(120_000, remainingMs));
                        }
                        catch { /* keep last observe; the re-scan below still runs */ }
                        wakeCount++;
                        lootOkDiags = ScanLootOk(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId);
                    }
                    evidence.AppendLine($"- CORPSE START: ok-release wait done: lootOkDiags={lootOkDiags.Count}");
                }
            }
            // Same-tick ok+loot race carry-over: the release wake's tick can
            // land the grant (LootDiag Completed + decide `landed Loot`) in
            // the SAME tick as the validate=ok arm. The START block above
            // never appends decideHistory, so without this the watch opens
            // at container=0, sees only already-looted, and reports
            // LOOT/not-dispatched for a fully green grant. Read-only
            // accounting of an already-landed grant (never re-dispatched):
            // append the release decide, record the loot hit, and set
            // lootGranted/lootCompleted exactly as the watch loop would.
            // No loot in the release tick -> no-op, watch opens empty.
            if (lootOkDiags.Count > 0)
            {
                var releaseDecide = GetStr(observe, "questDecideDetail");
                var releaseLootPattern = $@"landed Loot \(looted \d+ item\(s\) from {startTargetObjId}\)";
                if (!string.IsNullOrEmpty(releaseDecide) && Regex.IsMatch(releaseDecide, releaseLootPattern)
                    && (decideHistory.Count == 0 || decideHistory[^1] != releaseDecide))
                    decideHistory.Add(releaseDecide);
                ScanLootHits(releaseDecide, lootHits);
                lootDiagLines.AddRange(ScanLootDiags(gameLogOffset, gameRestartLogOffset, ourCharacterId, lootDiagLines.Count));
                foreach (var hit in lootHits)
                {
                    var m = Regex.Match(hit, @"landed Loot \(looted (\d+) item\(s\) from (\d+)\)");
                    if (m.Success && uint.TryParse(m.Groups[2].Value, out var t) && t == startTargetObjId)
                    {
                        landedLootCount = lootHits.Count(h => Regex.IsMatch(h, $@"landed Loot \(looted \d+ item\(s\) from {startTargetObjId}\)"));
                        lootGranted = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        lootTarget = t;
                    }
                }
                foreach (var d in lootDiagLines)
                {
                    var m = Regex.Match(d, @"target=(\d+).*state=(\w+).*grant=(\d+)");
                    if (m.Success && uint.TryParse(m.Groups[1].Value, out var t) && t == startTargetObjId
                        && m.Groups[2].Value == "Completed" && int.TryParse(m.Groups[3].Value, out var g))
                    {
                        lootCompleted = true;
                        if (lootGranted < 0)
                            lootGranted = g;
                    }
                }
                if (landedLootCount >= 1 && containerAfter < 0)
                    containerAfter = ScanContainerAfter(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId);
                if (landedLootCount >= 1 || lootCompleted)
                    evidence.AppendLine($"- CORPSE START: same-tick ok+loot carry-over: landedLoot={landedLootCount} granted={lootGranted} completed={lootCompleted} containerAfter={containerAfter}");
            }
            containerBefore = lootOkDiags.Count == 0 ? -1 : lootOkDiags.Max(d => d.Container);
            corpseStartTaken = true;
            var corpseStartOk = corpseHpAtStart == 0 && corpseQuestActive && containerBefore >= 1;
            Leg("corpse-start", corpseStartOk,
                $"corpse={startTargetObjId} hp={corpseHpAtStart}(REQUIRE 0) quest={corpseQuestStatus} active={corpseQuestActive} meat={corpseMeat} container(quest-side)={containerBefore} lootOkDiags={lootOkDiags.Count} cycle=[{corpseCycle}]",
                0);
            evidence.AppendLine($"- CORPSE START: objId={startTargetObjId} hp={corpseHpAtStart} quest={corpseQuestStatus} meat={corpseMeat} container(quest-side)={containerBefore} lootOkDiags={lootOkDiags.Count} cycle=[{corpseCycle}]");
            foreach (var d in lootOkDiags.Take(5))
                evidence.AppendLine($"  [LOOT-OK wake={d.Wake} loot=[{d.Raw}]]");
            if (corpseHpAtStart != 0 || !corpseQuestActive)
            {
                failBoundary = "HARNESS/corpse-start";
                failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, hp = corpseHpAtStart, questActive = corpseQuestActive, interpretation = "corpse START outside the observable window (hp!=0 or quest inactive); refusing observation" };
                Leg("gate", false, $"{verdict} at {failBoundary}: corpse={startTargetObjId}", execSw.Elapsed.TotalSeconds);
                Assert.True(passed, $"G7c {verdict} at {failBoundary}: corpse={startTargetObjId}");
            }
            if (containerBefore < 1)
            {
                if (lootOkDiags.Count == 0)
                {
                    verdict = "FAIL-BEHAVIOR/CORPSE-UNOBSERVED";
                    failBoundary = "CORPSE/unobserved";
                    failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, corpseStartWake, interpretation = "quest leg never named OUR corpse lootable (no loot=[validate=ok] funnel arm for OUR objId); recognition missing" };
                }
                else
                {
                    verdict = "FAIL-BEHAVIOR/CORPSE-EMPTY";
                    failBoundary = "CORPSE/empty";
                    failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, lootOkDiags = lootOkDiags.Take(5).ToList(), interpretation = "OUR corpse recognized but every quest-side container reading is 0 — nothing to grant; fixture/roll race, never a grant proof" };
                }
                Leg("gate", false, $"{verdict} at {failBoundary}: corpse={startTargetObjId}", execSw.Elapsed.TotalSeconds);
                Assert.True(passed, $"G7c {verdict} at {failBoundary}: corpse={startTargetObjId}");
            }

            // ---- LOOT watch: bounded, observe ONLY ----
            // The quest leg dispatches the canonical Loot itself; the harness
            // only wakes + observes + reads. Fast cadence to catch the
            // container=0 post-loot window before the corpse despawns.
            var lootDeadline = Environment.TickCount64 + LootBudgetMs;
            while (Environment.TickCount64 < lootDeadline)
            {
                try
                {
                    observe = bridge.Call(
                        $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":2000}}", 60_000);
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
                lootWakes++;
                var decide = GetStr(observe, "questDecideDetail");
                if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                    decideHistory.Add(decide);
                ScanCastHits(decide, $"liveAction={GetStr(observe, "liveAction")} lastAction={GetStr(observe, "lastAction")}", castHits);
                ScanLootHits(decide, lootHits);
                var diags = ScanLootDiags(gameLogOffset, gameRestartLogOffset, ourCharacterId, lootDiagLines.Count);
                lootDiagLines.AddRange(diags);
                // Live grant accounting: landed Loot lines + loot-diag grants.
                foreach (var hit in lootHits)
                {
                    var m = Regex.Match(hit, @"landed Loot \(looted (\d+) item\(s\) from (\d+)\)");
                    if (m.Success && uint.TryParse(m.Groups[2].Value, out var t) && t == startTargetObjId)
                    {
                        landedLootCount = lootHits.Count(h => Regex.IsMatch(h, $@"landed Loot \(looted \d+ item\(s\) from {startTargetObjId}\)"));
                        lootGranted = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        lootTarget = t;
                    }
                }
                foreach (var d in lootDiagLines)
                {
                    var m = Regex.Match(d, @"target=(\d+).*state=(\w+).*grant=(\d+)");
                    if (m.Success && uint.TryParse(m.Groups[1].Value, out var t) && t == startTargetObjId
                        && m.Groups[2].Value == "Completed" && int.TryParse(m.Groups[3].Value, out var g))
                    {
                        lootCompleted = true;
                        if (lootGranted < 0)
                            lootGranted = g;
                    }
                }
                if (corpseGoneWake == 0 && ReadNpcHp(bridge, startTargetObjId) < 0)
                {
                    corpseGoneWake = wakeCount;
                    evidence.AppendLine($"- corpse despawned at wake={wakeCount} (recorded only; START presence already proven)");
                }
                // Post-loot container reading: the first quest-side
                // container=0 (or already-looted) arm after the grant, else
                // the gone arm once despawned.
                if (landedLootCount >= 1 && containerAfter < 0)
                    containerAfter = ScanContainerAfter(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId);
                if (landedLootCount >= 1)
                    postLootWakes++;
                // OUR corpse's terminal grant (Completed + a post-grant
                // container reading or despawn) ends the watch: anything the
                // quest leg loots afterwards is chaining onto ANOTHER corpse,
                // which this gate does not adjudicate. While the grant is still
                // unproven, the bounded 3-post-loot-wake window below still
                // gives a same-corpse re-dispatch time to manifest, then stops.
                if (lootCompleted && (containerAfter >= 0 || corpseGoneWake != 0))
                    break;
                if (landedLootCount >= 1 && postLootWakes >= 3 && (containerAfter >= 0 || corpseGoneWake != 0))
                    break;
                Thread.Sleep(1000);
            }
            evidence.AppendLine($"- loot wakes: {lootWakes} landedLoot={landedLootCount} granted={lootGranted} completed={lootCompleted} containerBefore={containerBefore} containerAfter={containerAfter} corpseGoneWake={corpseGoneWake}");

            // ---- POST proofs (read-only): engine kill line, bag, quest ----
            killEngineLines = ScanKillLines(gameLogOffset, gameRestartLogOffset, startTargetObjId);
            meatPost = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var endQuestState = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            endQuestStatus = endQuestState.Status?.ToString() ?? "";
            questStillProgress = endQuestState.Active && string.Equals(endQuestStatus, "Progress", StringComparison.Ordinal);
            // Held-and-not-advanced: Ready is legal here because chaining may
            // have reached the objective count (meat>=3) at another corpse;
            // credit/advance remain G7d's.
            questHeldBeforeReady = endQuestState.Active
                && (string.Equals(endQuestStatus, "Progress", StringComparison.Ordinal)
                    || string.Equals(endQuestStatus, "Ready", StringComparison.Ordinal));
            var postObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            var moneyPost = postObs.TryGetProperty("money", out var mpEl) && mpEl.ValueKind == JsonValueKind.Number && mpEl.TryGetInt64(out var mpPost) ? mpPost : -1L;
            ScanCastHits(GetStr(postObs, "questDecideDetail"), $"lastAction={GetStr(postObs, "lastAction")}", castHits);
            var logCastLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
            castHits.AddRange(logCastLoot.Where(l => Regex.IsMatch(l, @"\bCast\b")));
            // Final exactly-once recount across the FULL decide history.
            landedLootCount = decideHistory
                .SelectMany(d => Regex.Matches(d, @"landed Loot \(looted (\d+) item\(s\) from (\d+)\)").Cast<Match>())
                .Count(m => m.Groups[2].Value == startTargetObjId.ToString(CultureInfo.InvariantCulture));
            var foreignLoot = decideHistory
                .SelectMany(d => Regex.Matches(d, @"landed Loot \(looted (\d+) item\(s\) from (\d+)\)").Cast<Match>())
                .Count(m => m.Groups[2].Value != startTargetObjId.ToString(CultureInfo.InvariantCulture));
            if (containerAfter < 0)
                containerAfter = ScanContainerAfter(gameLogOffset, gameRestartLogOffset, ourCharacterId, startTargetObjId);
            if (containerAfter < 0 && corpseGoneWake != 0 && lootCompleted && lootGranted == containerBefore)
            {
                // The corpse despawned before any container=0 arm logged: full
                // drain accounting (every pre-loot entry granted by a Completed
                // loot-all, engine removes granted entries) proves remaining 0.
                containerAfter = 0;
                remainingProof = "drain-accounting";
            }
            else if (containerAfter == 0)
            {
                remainingProof = "container-zero-read";
            }
            meatDelta = meatPre >= 0 && meatPost >= 0 ? meatPost - meatPre : -999;
            moneyDelta = startMoney >= 0 && moneyPost >= 0 ? moneyPost - startMoney : -999L;
            execSeconds = execSw.Elapsed.TotalSeconds;
            evidence.AppendLine($"- engine kill lines ({killEngineLines.Count}):");
            foreach (var l in killEngineLines.Take(5))
                evidence.AppendLine($"  [{l}]");
            evidence.AppendLine($"- loot lines ({lootDiagLines.Count}):");
            foreach (var l in lootDiagLines.Take(8))
                evidence.AppendLine($"  [{l}]");
            evidence.AppendLine($"- grant: before={containerBefore} granted={lootGranted} after={containerAfter} ({remainingProof}) landedLoot(ours)={landedLootCount} foreign={foreignLoot} completed={lootCompleted}");
            evidence.AppendLine($"- exclusion: castHits={(castHits.Count == 0 ? "NONE" : string.Join("; ", castHits.Take(10)))}");
            evidence.AppendLine($"- side effects: meatDelta={meatDelta} ({meatPre}->{meatPost}) moneyDelta={moneyDelta} quest {corpseQuestStatus}->{endQuestStatus} (turnIns/credit: NOT asserted — G7d owns credit)");
            Leg("loot-watch", true, $"lootWakes={lootWakes} landedLoot={landedLootCount} granted={lootGranted} completed={lootCompleted} after={containerAfter}", execSeconds);

            if (castHits.Count != 0)
            {
                verdict = "FAIL-BEHAVIOR/CAST-DETECTED";
                failBoundary = "LOOT/cast-detected";
                failingCondition = new { boundary = failBoundary, hits = castHits.Take(10).ToList(), interpretation = "a Cast record fired during the loot window — the quest loot path casts nothing" };
            }
            else if (landedLootCount == 0)
            {
                verdict = "FAIL-BEHAVIOR/LOOT-NOT-DISPATCHED";
                failBoundary = "LOOT/not-dispatched";
                failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, lootWakes, containerBefore, interpretation = "no canonical landed Loot for OUR corpse inside the loot budget despite a lootable recognition" };
            }
            else if (landedLootCount > 1 || CorpseLootedTwice(decideHistory))
            {
                verdict = "FAIL-BEHAVIOR/LOOT-DOUBLE-DISPATCH";
                failBoundary = "LOOT/double-dispatch";
                failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, ours = landedLootCount, hits = lootHits.Take(5).ToList(), interpretation = "the canonical Loot must dispatch EXACTLY once per corpse: OUR corpse re-dispatched, or ANY single corpse looted more than once. Looting a DIFFERENT (foreign) corpse is legal chaining, recorded as evidence only" };
            }
            else if (!lootCompleted)
            {
                verdict = "FAIL-BEHAVIOR/LOOT-NOT-COMPLETED";
                failBoundary = "LOOT/not-completed";
                failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, granted = lootGranted, interpretation = "the single landed Loot never reached Completed (rejected/interrupted?) — grant unproven" };
            }
            else if (lootGranted < 0 || containerAfter != 0 || lootGranted != containerBefore - containerAfter)
            {
                verdict = "FAIL-BEHAVIOR/GRANT-MISMATCH";
                failBoundary = "ITEM/conservation";
                failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, containerBefore, granted = lootGranted, containerAfter, remainingProof, interpretation = "Q4 conservation broken: granted must equal containerBefore - remaining with remaining == 0" };
            }
            else if (meatDelta < 0 || moneyDelta != 0)
            {
                verdict = "FAIL-BEHAVIOR/BAG-LOSS";
                failBoundary = "ITEM/gains";
                failingCondition = new { boundary = failBoundary, meatPre, meatPost, meatDelta, moneyDelta, interpretation = "every observed bag delta must be a gain (meat>=0) and money must not move — something took from the bot" };
            }
            else if (meatDelta == 0 && containerBefore < 1)
            {
                verdict = "FAIL-BEHAVIOR/DRY-WITHOUT-PROOF";
                failBoundary = "ITEM/dry-without-proof";
                failingCondition = new { boundary = failBoundary, meatPre, meatPost, containerBefore, interpretation = "dry loot (meatΔ==0) is only acceptable with container proof that entries existed and drained" };
            }
            else if (!questHeldBeforeReady)
            {
                verdict = "FAIL-BEHAVIOR/QUEST-LEFT-PROGRESS";
                failBoundary = "QUEST/not-progress";
                failingCondition = new { boundary = failBoundary, corpse = startTargetObjId, corpseQuestStatus, endQuestStatus, interpretation = "quest 251 must still be HELD after the grant (Active with status Progress, or Ready once chaining legitimately reached the objective count) — no credit assertion, G7d owns credit, but the quest must not have dropped or advanced" };
            }
            else
            {
                passed = true;
                verdict = "PASS-BEHAVIOR";
                failBoundary = "none";
                var dryNote = meatDelta == 0 ? $" dry (meatΔ==0 with container proof: {containerBefore} non-meat entries drained)" : "";
                claim = $"corpse {startTargetObjId} (3475, Hp 0) looted quest-side EXACTLY once (Completed, grant {lootGranted} = {containerBefore} - {containerAfter}, remaining 0 via {remainingProof}), meat {meatPre}->{meatPost}{dryNote}, money Δ0, quest still {endQuestStatus} — and stops there (no credit assertion: G7d)";
                failingCondition = new { boundary = "none", corpse = startTargetObjId, granted = lootGranted, containerBefore, containerAfter, remainingProof, meatPre, meatPost, endQuestStatus };
            }
            Leg("gate", passed, $"{verdict} at {failBoundary}: corpse={startTargetObjId} granted={lootGranted} {containerBefore}->{containerAfter} meat={meatPre}->{meatPost} quest={endQuestStatus}",
                execSeconds);
            Assert.True(passed, $"G7c {verdict} at {failBoundary}: corpse={startTargetObjId}");
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
                            money = startMoney
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
                            containerBefore,
                            lootOkDiags = lootOkDiags.Take(5).ToList(),
                            money = corpseMoney,
                            cycle = corpseCycle
                        },
                        loot = new
                        {
                            lootWakes,
                            landedLootCount,
                            lootTarget,
                            lootGranted,
                            lootCompleted,
                            containerBefore,
                            containerAfter,
                            remainingProof,
                            corpseGoneWake,
                            postLootWakes,
                            lootDiagLines = lootDiagLines.Take(8).ToList(),
                            lootHits = lootHits.Take(5).ToList(),
                            castHits = castHits.Count
                        },
                        sideEffects = new
                        {
                            item4058Delta = meatDelta,
                            moneyDelta,
                            meatPre,
                            meatPost,
                            questStillProgress,
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
                        $"{{\"scenario\":\"g7c-loot-grant\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"G7C-GATE verdict={verdict} boundary={failBoundary} corpse={startTargetObjId} " +
                $"granted={lootGranted} {containerBefore}->{containerAfter} landed={landedLootCount} meat={meatPre}>{meatPost} quest={endQuestStatus} legs={legs.Count}");
        }
    }


    // (funnel charId is threaded from enroll; observe payloads carry no id)

    // ---- quest-side loot-ok scan (fixture proof): funnel loot arms naming
    // OUR corpse with validate=ok + lootable=true, carrying the container
    // count. Read-only log scan; the container is never touched.
    private sealed record LootOkDiag(int Wake, int Container, string Raw);

    private static List<LootOkDiag> ScanLootOk(long gameLogOffset, long gameRestartLogOffset, uint charId, uint corpseObjId)
    {
        var out_ = new List<LootOkDiag>();
        var id = corpseObjId.ToString(CultureInfo.InvariantCulture);
        foreach (var line in ReadQuestLines(gameLogOffset, gameRestartLogOffset, charId, "QuestObjectiveTargetDiag"))
        {
            var loot = Regex.Match(line, @"\bloot=\[(.*?)\]").Success
                ? Regex.Match(line, @"\bloot=\[(.*?)\]").Groups[1].Value : "";
            if (!loot.Contains(id, StringComparison.Ordinal) || !loot.Contains("validate=ok", StringComparison.Ordinal)
                || !loot.Contains("lootable=true", StringComparison.Ordinal))
                continue;
            var cm = Regex.Match(loot, @"container=(\d+)");
            var container = cm.Success ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
            if (container >= 0)
                out_.Add(new LootOkDiag(0, container, loot.Length > 200 ? loot[..200] : loot));
        }
        return out_;
    }

    // ---- quest-side loop-live scan (provisional START recognition): funnel
    // loot arms naming OUR corpse with validate=loop-live + lootable=true,
    // carrying the container count. Same loop-release seam as above: the quest
    // leg withholds the validate=ok release while character.IsAutoAttack is
    // still true, so the withhold arm is the only funnel recognition until
    // teardown completes. Read-only log scan; the container is never touched.
    private static List<LootOkDiag> ScanLootLoopLive(long gameLogOffset, long gameRestartLogOffset, uint charId, uint corpseObjId)
    {
        var out_ = new List<LootOkDiag>();
        var id = corpseObjId.ToString(CultureInfo.InvariantCulture);
        foreach (var line in ReadQuestLines(gameLogOffset, gameRestartLogOffset, charId, "QuestObjectiveTargetDiag"))
        {
            var loot = Regex.Match(line, @"\bloot=\[(.*?)\]").Success
                ? Regex.Match(line, @"\bloot=\[(.*?)\]").Groups[1].Value : "";
            if (!loot.Contains(id, StringComparison.Ordinal) || !loot.Contains("validate=loop-live", StringComparison.Ordinal)
                || !loot.Contains("lootable=true", StringComparison.Ordinal))
                continue;
            var cm = Regex.Match(loot, @"container=(\d+)");
            var container = cm.Success ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
            if (container >= 0)
                out_.Add(new LootOkDiag(0, container, loot.Length > 200 ? loot[..200] : loot));
        }
        return out_;
    }

    // ---- quest-side loot-diag scan: QuestObjectiveLootDiag lines for OUR
    // char (grant/state/target proof). skipLines skips already-seen lines.
    private static List<string> ScanLootDiags(long gameLogOffset, long gameRestartLogOffset, uint charId, int skipLines)
    {
        var lines = ReadQuestLines(gameLogOffset, gameRestartLogOffset, charId, "QuestObjectiveLootDiag");
        return lines.Skip(skipLines).Select(l => l.Length > 240 ? l[..240] : l).ToList();
    }

    // ---- post-loot container scan: the first quest-side container=0 (or
    // already-looted) loot arm for OUR corpse after the grant. -1 when no
    // such arm logged yet (corpse may have despawned first).
    private static int ScanContainerAfter(long gameLogOffset, long gameRestartLogOffset, uint charId, uint corpseObjId)
    {
        var id = corpseObjId.ToString(CultureInfo.InvariantCulture);
        foreach (var line in ReadQuestLines(gameLogOffset, gameRestartLogOffset, charId, "QuestObjectiveTargetDiag"))
        {
            var loot = Regex.Match(line, @"\bloot=\[(.*?)\]").Success
                ? Regex.Match(line, @"\bloot=\[(.*?)\]").Groups[1].Value : "";
            if (!loot.Contains(id, StringComparison.Ordinal))
                continue;
            if (loot.Contains("validate=already-looted", StringComparison.Ordinal))
                return 0;
            var cm = Regex.Match(loot, @"container=(\d+)");
            if (cm.Success && loot.Contains("validate=not-lootable", StringComparison.Ordinal)
                && int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) == 0)
                return 0;
        }
        return -1;
    }

    private static List<string> ReadQuestLines(long gameLogOffset, long gameRestartLogOffset, uint charId, string needle)
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
                    if (line.Contains(needle, StringComparison.Ordinal)
                        && (charId == 0 || line.Contains($"char={charId}", StringComparison.Ordinal)))
                        lines.Add(line);
                }
            }
            catch
            {
            }
        }
        return lines;
    }

    // ---- loot-dispatch scan: canonical landed-Loot records (decide/observe text).
    private static void ScanLootHits(string text, List<string> hits)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (Match m in Regex.Matches(text, @"landed Loot \(looted \d+ item\(s\) from \d+\)"))
        {
            if (!hits.Contains(m.Value))
                hits.Add(m.Value);
        }
    }

    // ---- per-corpse idempotency: the ONLY legal dispatch count is one per
    // source objId. Groups the canonical landed-Loot records in the FULL decide
    // history by their `from <objId>` token and reports whether ANY objId was
    // looted more than once. Looting several DIFFERENT corpses is legal
    // chaining (quest 251's ItemGather 4058x3 is need-driven), so a foreign
    // corpse is never itself a violation.
    private static bool CorpseLootedTwice(IEnumerable<string> decideHistory)
    {
        var perCorpse = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in decideHistory)
        {
            foreach (Match m in Regex.Matches(d, @"landed Loot \(looted \d+ item\(s\) from (\d+)\)"))
            {
                perCorpse.TryGetValue(m.Groups[1].Value, out var n);
                perCorpse[m.Groups[1].Value] = n + 1;
            }
        }
        return perCorpse.Values.Any(c => c > 1);
    }

    // ---- cast exclusion: any Cast record (decide/observe/log text).
    private static void ScanCastHits(string decide, string live, List<string> hits)
    {
        foreach (var text in new[] { decide, live })
        {
            if (string.IsNullOrEmpty(text))
                continue;
            var m = Regex.Match(text, @"landed Cast\b|(liveAction=Cast)");
            if (m.Success && !hits.Contains(text))
                hits.Add(text.Length > 220 ? text[..220] : text);
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

    // ---- shared-port route selection: return the first HttpClient whose
    // backend answers enabled (bounded re-hash; each fresh client hashes its
    // new connection to one lane). Throws when no enabled backend answers —
    // SETUP/webapi-route, never a loot verdict.
    private static async Task<HttpClient> EnsureWebApiClientAsync(StringBuilder evidence)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var client = NewClient();
            try
            {
                using var response = await client.GetAsync("/api/bots");
                var text = await response.Content.ReadAsStringAsync();
                if (text.Contains("Bot control API is disabled", StringComparison.Ordinal))
                {
                    evidence.AppendLine($"- webapi route attempt {attempt}: disabled backend (re-hashing)");
                    client.Dispose();
                    await Task.Delay(1000);
                    continue;
                }
                evidence.AppendLine($"- webapi route attempt {attempt}: enabled backend");
                return client;
            }
            catch (Exception ex)
            {
                evidence.AppendLine($"- webapi route attempt {attempt}: {ex.GetType().Name} (retrying)");
                client.Dispose();
                await Task.Delay(1000);
            }
        }
        throw new InvalidOperationException("no enabled WebApi backend reachable on the shared 1280 port after 12 hashed attempts");
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

    /// <summary>
    /// Require every WebApi listener to belong to the adopted lane.
    /// A conflicting or unresolvable process is an infrastructure failure;
    /// the test must never terminate another lane to make its probes pass.
    /// </summary>
    private static string AssertLaneOwnsWebApiListeners()
    {
        var laneRoot = Path.GetFullPath(Path.Combine(E2eStack.E2eRoot, "runtime"))
            .TrimEnd(Path.DirectorySeparatorChar);
        foreach (var pid in ListenersOnPort(E2eStack.WebApiPort))
        {
            try
            {
                var cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget;
                if (cwd == null)
                    throw new InvalidOperationException($"Cannot resolve listener pid={pid} working directory");
                var full = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);
                var inThisLane = full.Equals(laneRoot, StringComparison.Ordinal)
                    || full.StartsWith(laneRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inThisLane)
                    continue;
                throw new InvalidOperationException(
                    $"port {E2eStack.WebApiPort} is owned by pid={pid} outside lane {laneRoot}; refusing to stop a foreign process");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Cannot establish exclusive lane ownership for pid={pid}", ex);
            }
        }
        return "all observed listeners belong to the adopted lane";
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
                scenario = "g7c-loot-grant",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "objective-credit", "turn-in", "rotation", "kill-teardown", "second-corpse", "any other quest" },
                callPath = "bridge quest wake → scheduler Wake → quest leg (QuestBehavior.Run G7c 251 loot path: LastCorpse + ProbeCorpse → LootProposal → GameplayActor.Loot → LootingContainer.OpenBag lootAll) → BotDecisionCycle; engine owns death (Unit.DoDie → LootingContainer.GenerateLoot) + grant (Bag.AcquireDefaultItem → OnAcquiredItem → inventory); the gate adds no verbs — wake + observe + read-only npcState HP polls only",
                g7bLeg = "reused inside this test (fresh G7a-style run to authoritative death; corpse START re-taken at the fresh corpse with quest-side lootable + non-empty-container proof)",
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
            Console.WriteLine($"G7C-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
