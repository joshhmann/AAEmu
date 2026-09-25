using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// G7d gate (ACQUISITION → CREDIT, quest-owned kills+loots) — production
/// death/loot cycles grant the remaining 4058 meat, each acquisition fires
/// the engine OnItemGather fanout, the gather act recounts its objective
/// from the live bag, and quest 251 advances Progress → Ready. The harness
/// never loots, casts, damages, or mutates: wake + observe + read-only
/// npcState HP polls only. No turn-in (G8 owns Ready → turn-in).
///
/// SOLE EXCEPTION (staging only, blocked path): when the 90 s stage watch
/// finds no live 3475 but does find a 3475 corpse holding the spawner and
/// its container still carries items (npcState lootable=true), the gate
/// dispatches ONE loot through the same real actor path the quest leg uses
/// (drive op 'loot' → GameplayActor.Loot, lootAll) to drain the blocker and
/// start the engine's ~2 s post-loot despawn clock. That verb happens only
/// while staging is blocked — never during drive/kill/loot/credit phases,
/// and it never touches grant-cycle semantics.
///
/// Production chain under test (g7-loot-credit.md §E.2→G.1): Unit.DoDie →
/// LootingContainer.GenerateLoot → GameplayActor.Loot (quest-leg proposal)
/// → OpenBag lootAll → Bag.AcquireDefaultItem → Inventory.OnAcquiredItem →
/// QuestManager.DoItemsAcquiredEvents → QuestActObjItemGather.OnItemGather
/// (act-id + item-4058 match) → recount SetObjective(GetItemsCount(4058))
/// → RequestEvaluation → step machine Progress→Ready at ≥3.
///
/// PASS needs ALL of: meat trajectory 0→3 across production grants only +
/// quest objectives[0]==3 (recount proof) + quest Step/Status Ready +
/// quest still held (no turn-in performed by anyone).
///
/// WITHHOLD-ON DEPENDENCY (explicit, G8b precedent): the E2E-only bridge flag
/// (`sub=withhold`) drives `QuestOptions.WithholdTurnIn`, which the quest leg
/// enforces at QuestBehavior.cs:556 (`opts.WithholdTurnIn &&
/// selected.Goal == TurnInGoal` → `Fail("WITHHELD")` — proposal observable,
/// dispatch withheld, quest stays Ready/active). G7d REQUIRES it ON: the
/// instant credit lands Ready, the next quest tick would otherwise dispatch
/// the production GameplayActor.TurnInQuest (no range gate), complete 251 and
/// destroy the fixture under observation — exactly the sweep's
/// QUEST/not-active race (g7d-credit-report.20260924T023257987Z.json:
/// have=3 → return → FinalizeQuest 104 → "251 removed", verdict sample found
/// the quest inactive / meat-0). The bot played correctly; only the fixture
/// could not hold still. Armed at fixture setup (before grant cycle 1),
/// echoed in the START snapshot and the final evidence, and NEVER released
/// mid-gate (G7d never turns in — G8 owns Ready → turn-in).
///
/// ATTEMPT BUDGET (strict, stated; G7 plan §J.2 bounded repeats):
/// MaxGrantCycles=4 kill+loot iterations (3 needed at ×1 meat/corpse +
/// 1 spare for a dry/empty/despawned corpse); per cycle KillBudget 600 s,
/// LootBudget 180 s; per (re)drive 300 s with ≤2 restages. Early stop the
/// moment meat≥3. Budget exhausted with meat<3 → ITEM/no-grant.
///
/// FAIL boundaries are predicate-named:
///   ITEM/no-grant       — budget exhausted, bag never reached 4058×3
///   CREDIT/no-event     — meat==3 but objective<3 (recount never landed;
///                         OnItemGather never effective while quest active)
///   CREDIT/no-progress  — objective==3 but quest still Progress
///                         (evaluation never advanced the step machine)
///   HARNESS/*           — setup/overshoot/corpse-gone/leash races (never
///                         a behavior verdict)
///   HARNESS/stage-range-blocked — the stage watch expired with NO live 3475
///                         but with a 3475 corpse holding that spawner (its
///                         item container was probed and drained once when
///                         lootable). Distinct from HARNESS/stage-range,
///                         which now means the spawner was EMPTY: no 3475
///                         (live or dead) was ever observed for the watch.
///                         Both are harness races; neither is a verdict.
///
/// FIXTURE per cycle: fresh-stage at a live 3475 spawner (~2 m off,
/// standstill-settled), G7a-style observe-only drive to the G6 end state
/// (loop-live + pinned + damaged-but-alive), authoritative START, then
/// observe-only kill → corpse START (re-taken at the fresh corpse) →
/// observe-only loot. Corpse-gone at START = HARNESS/corpse-gone and
/// consumes one spare cycle — never a loot failure.
///
/// If credit never lands despite meat reaching 3, the boundary is
/// CREDIT/no-progress (or CREDIT/no-event): diagnose exactly (active quest
/// present? objectives[] values? step/status? funnel have/need trail?
/// LootDiag grants?) and stop — NO quest-engine patch.
///
/// STOPS at Ready: no turn-in, no second quest, no rotation. G8 owns
/// Ready → turn-in.
[Collection("e2e")]
public class G7dQuestCreditGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G7dCredit" + Stamp;
    private static readonly string BotAccount = ("g7dcredit" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private const uint Quest251 = 251;
    private const uint GiverNpc3512 = 3512;
    private const uint BoarTemplate3475 = 3475;
    private const uint BoarMeat4058 = 4058;
    private const int MeatRequired = 3;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;
    private const float StopRadiusM = 3.0f;
    private const float StageOffsetM = 2.0f;
    private const int DriveBudgetMs = 300_000;
    private const int KillBudgetMs = 600_000;
    private const int LootBudgetMs = 180_000;
    private const int MaxRestages = 2;
    private const int MaxGrantCycles = 4;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "g7d-credit-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    private sealed record CycleResult(
        int Cycle, uint Target, int StartHp,
        bool Dead, int Granted, bool Completed,
        int MeatBefore, int MeatAfter, int[] ObjectivesAfter,
        string QuestStatusAfter, string QuestStepAfter,
        string FunnelHaveNeed, string Outcome);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ProductionLootCycleGrantsMeatToReady()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G7d acquisition→credit gate (observe-only production kill+loot cycles to Ready) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        evidence.AppendLine($"- ATTEMPT BUDGET: MaxGrantCycles={MaxGrantCycles} (3 needed at x1/corpse + 1 spare); per-cycle kill={KillBudgetMs / 1000}s loot={LootBudgetMs / 1000}s; per-drive={DriveBudgetMs / 1000}s restages<={MaxRestages}; early stop at meat>={MeatRequired}");
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
        uint ourCharacterId = 0;
        var spawnerXyz = "ABSENT";
        var stagedDist = double.NaN;
        var castHits = new List<string>();
        var decideHistory = new List<string>();
        var wakeCount = 0;
        long gameLogOffset = 0;
        long gameRestartLogOffset = 0;
        var cycles = new List<CycleResult>();
        var meatTrajectory = new List<int>();
        var objectiveTrajectory = new List<string>();
        var statusTrajectory = new List<string>();
        var funnelTrail = new List<string>();
        var lootDiagTotal = new List<string>();
        var killEngineTotal = new List<string>();
        var readyReached = false;
        string endStep = "", endStatus = "";
        int[] endObjectives = [];
        var meatFinal = -1;
        // Withhold audit trail (E2E fixture-hold; see WITHHOLD-ON DEPENDENCY).
        var withholdArmed = false;
        var startWithhold = false;
        var endWithhold = false;
        var withholdEveryCycle = true;

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
                    var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest251},\"acceptorType\":\"Npc\",\"acceptorId\":{GiverNpc3512},\"idempotencyKey\":\"g7d-prehold-{BotAccount}\"}}";
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

            // WITHHOLD ON (fixture-hold, G8b precedent — see the WITHHOLD-ON
            // DEPENDENCY note above): production TurnInQuest must NOT land.
            // The instant the recount flips 251 to Ready (cycle ≥3), the next
            // quest tick would otherwise dispatch GameplayActor.TurnInQuest
            // (no range gate), complete 251 and destroy the fixture the gate
            // samples — the exact sweep QUEST/not-active race where the verdict
            // read an inactive quest / meat-0. Armed here, before grant cycle 1,
            // and NEVER released: G7d never turns in (G8 owns Ready →
            // turn-in). No gameplay verb, no quest state touched.
            var withholdResp = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"withhold\",\"on\":true}}", 30_000);
            withholdArmed = withholdResp.TryGetProperty("withholdTurnIn", out var whEl) && whEl.ValueKind == JsonValueKind.True;
            Leg("withhold-arm", withholdArmed,
                $"withholdTurnIn={withholdArmed} (REQUIRED before cycle 1 and throughout: Ready fixture survives only while ON; no turn-in, G8 owns it)", 0);
            evidence.AppendLine($"- WITHHOLD: arm response verbatim: {withholdResp}");
            if (!withholdArmed)
            {
                failBoundary = "HARNESS/withhold-refused";
                Assert.Fail("withhold seam refused to arm; refusing to run Ready-capable grant cycles unprotected");
            }

            gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- local phases (all observe-only; the harness adds no verbs) ----
            string ObserveDecide()
            {
                var obs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                return GetStr(obs, "questDecideDetail");
            }

            void NoteDecide(string decide)
            {
                if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                    decideHistory.Add(decide);
            }

            // Drive one fresh staging to the G6 end state. Returns the
            // selected live 3475 (0 on failure) with stage HP readings.
            (bool Ok, string Fail, uint Selected, int StageHp, int StageMaxHp, double LiveDist, int Wakes) DrivePhase(int cycle)
            {
                var stageSw = Stopwatch.StartNew();
                uint stageBoarObjId = 0;
                var staged = StageAtBoar(bridge, evidence, out spawnerXyz, out stagedDist, out stageBoarObjId, out var stageFail);
                var stageNpc = ReadNpcFull(bridge, stageBoarObjId);
                Leg($"c{cycle}-stage", staged,
                    $"spawner=[{spawnerXyz}] flat={stagedDist:0.0}m (REQUIRE 1..3m) live3475={stageBoarObjId} hp={stageNpc.Hp}/{stageNpc.MaxHp}",
                    stageSw.Elapsed.TotalSeconds);
                if (!staged)
                    return (false, stageFail, 0, stageNpc.Hp, stageNpc.MaxHp, stagedDist, 0);
                var (settled, settleTrail) = SettleRoute(bridge, 30_000);
                evidence.AppendLine($"- c{cycle} settle trail: {settleTrail}");
                if (!settled)
                    return (false, "HARNESS/settle", 0, stageNpc.Hp, stageNpc.MaxHp, stagedDist, 0);
                // Scope funnel observation to THIS cycle: ReadFunnels replays
                // every line since test start, and stale arms still name the
                // previous cycle's corpse (now Hp 0/-1). Latching onto one
                // pins hpNow<=0 forever and burns restages — a harness
                // scoping artifact, never a behavior signal.
                var funnelsBefore = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId).Count;

                uint selected = 0;
                var loopLive = false; var pinned = false; var hpFell = false;
                var hpNow = -1; var lastDist = double.NaN;
                var stable = true; var retracks = 0;
                string firstFresh = "";
                var wakes = 0;
                var stageHp = stageNpc.Hp;
                var deadline = Environment.TickCount64 + DriveBudgetMs;
                var restages = 0;
                // K1 failfast state (drive only): counts consecutive wakes with a
                // byte-identical questDecideDetail and no per-bot step signal. Fires
                // at 2; gated on zero drive progress (no loop live, no HP fall), so
                // a warm drive can never trip it. Per-bot baselines anchor on the
                // first wake (no pre-drive snapshot exists); budgets stay ceilings.
                var prevDecide = "";
                var idleWakes = 0;
                string idleExitBoundary = "";
                var k1ActionCount = 0;
                long k1WakeSeq = 0, k1TraceWakeSeq = 0, k1MaxTraceWakeSeq = 0;
                var k1Anchored = false;
                while (true)
                {
                    JsonElement observe;
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
                    try { observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000); }
                    catch { /* keep wake response */ }
                    var decide = GetStr(observe, "questDecideDetail");
                    NoteDecide(decide);
                    if (string.IsNullOrEmpty(firstFresh) && !string.IsNullOrEmpty(decide))
                        firstFresh = decide;
                    wakeCount++; wakes++;
                    foreach (var f in ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId).Skip(funnelsBefore))
                    {
                        if (selected == 0 && f.Selected != 0)
                            selected = f.Selected;
                        else if (f.Selected != 0 && selected != 0 && f.Selected != selected)
                        {
                            stable = false;
                            retracks++;
                        }
                        if (f.Selected != 0 && (selected == 0 || f.Selected == selected))
                            lastDist = f.Dist;
                    }
                    var landedTarget = Regex.Match(decide, @"landed Target \(targeting (\d+)\)");
                    if (landedTarget.Success && selected == 0)
                        selected = uint.Parse(landedTarget.Groups[1].Value, CultureInfo.InvariantCulture);
                    ScanCastHits(decide, $"liveAction={GetStr(observe, "liveAction")}", castHits);
                    if (GetBool(observe, "isAutoAttack"))
                        loopLive = true;
                    if (selected != 0 && GetUInt(observe, "currentTargetObjId") == selected)
                        pinned = true;
                    hpNow = ReadNpcHp(bridge, selected != 0 ? selected : stageBoarObjId);
                    if (hpNow >= 0 && stageHp > 0 && hpNow < stageHp)
                        hpFell = true;
                    if (hpNow == 0)
                        break; // died during drive — restage below
                    if (loopLive && pinned && hpFell && hpNow > 0 && !double.IsNaN(lastDist) && lastDist <= StopRadiusM)
                        break; // G6 end state at live in-range
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
                        evidence.AppendLine($"- K1 idle-window exit after {wakes} wakes ({idleWakes} consecutive identical-decide no-signal wakes): {idleExitBoundary}");
                        break;
                    }
                    if (Environment.TickCount64 >= deadline)
                        break;
                    Thread.Sleep(2000);
                }

                while (hpNow == 0 && restages < MaxRestages)
                {
                    restages++;
                    evidence.AppendLine($"- c{cycle} drive overshoot #{restages}: prey {selected} died before START; restaging");
                    var reOk = StageAtBoar(bridge, evidence, out spawnerXyz, out stagedDist, out stageBoarObjId, out _);
                    var reNpc = ReadNpcFull(bridge, stageBoarObjId);
                    stageHp = reNpc.Hp;
                    var (reSettled, _) = reOk ? SettleRoute(bridge, 30_000) : (false, "stage-failed");
                    if (!reOk || !reSettled)
                        return (false, "HARNESS/fixture-overshoot", 0, stageHp, reNpc.MaxHp, stagedDist, wakes);
                    selected = 0; stable = true; loopLive = false; pinned = false; hpFell = false; hpNow = -1;
                    idleWakes = 0;
                    prevDecide = "";
                    k1Anchored = false;
                    deadline = Environment.TickCount64 + DriveBudgetMs;
                    while (Environment.TickCount64 < deadline)
                    {
                        JsonElement observe;
                        try
                        {
                            observe = bridge.Call(
                                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                        }
                        catch { Thread.Sleep(2000); continue; }
                        try { observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000); }
                        catch { /* keep wake response */ }
                        var decide = GetStr(observe, "questDecideDetail");
                        NoteDecide(decide);
                        if (string.IsNullOrEmpty(firstFresh) && !string.IsNullOrEmpty(decide))
                            firstFresh = decide;
                        wakeCount++; wakes++;
                    foreach (var f in ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId).Skip(funnelsBefore))
                        {
                            if (selected == 0 && f.Selected != 0)
                                selected = f.Selected;
                            else if (f.Selected != 0 && selected != 0 && f.Selected != selected)
                            {
                                stable = false;
                                retracks++;
                            }
                            if (f.Selected != 0 && (selected == 0 || f.Selected == selected))
                                lastDist = f.Dist;
                        }
                        var landedTarget = Regex.Match(decide, @"landed Target \(targeting (\d+)\)");
                        if (landedTarget.Success && selected == 0)
                            selected = uint.Parse(landedTarget.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (GetBool(observe, "isAutoAttack"))
                            loopLive = true;
                        if (selected != 0 && GetUInt(observe, "currentTargetObjId") == selected)
                            pinned = true;
                        hpNow = ReadNpcHp(bridge, selected != 0 ? selected : stageBoarObjId);
                        if (hpNow >= 0 && stageHp > 0 && hpNow < stageHp)
                            hpFell = true;
                        if (hpNow == 0)
                            break;
                        if (loopLive && pinned && hpFell && hpNow > 0 && !double.IsNaN(lastDist) && lastDist <= StopRadiusM)
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
                            evidence.AppendLine($"- K1 idle-window exit after {wakes} wakes ({idleWakes} consecutive identical-decide no-signal wakes): {idleExitBoundary}");
                            break;
                        }
                        Thread.Sleep(2000);
                    }
                }

                if (hpNow == 0)
                    return (false, "HARNESS/fixture-overshoot", selected, stageHp, stageNpc.MaxHp, lastDist, wakes);
                var ok = loopLive && pinned && hpFell && hpNow > 0 && selected != 0 && stable && !double.IsNaN(lastDist) && lastDist <= StopRadiusM;
                Leg($"c{cycle}-drive", ok,
                    $"loopLive={loopLive} pinned={pinned} hpFell={hpFell} hpNow={hpNow} stageHp={stageHp} selected={selected} stable={stable} liveDist={lastDist:0.0}m restages={restages} wakes={wakes}",
                    0);
                // K1 idle-drive exit takes precedence: the cycle's drive ended early
                // with the frozen boundary name; the grant-cycle loop consumes it via
                // Fail (harnessFail -> failBoundary), structure and MaxGrantCycles
                // unchanged.
                return (ok, ok ? "" : !string.IsNullOrEmpty(idleExitBoundary) ? idleExitBoundary : string.IsNullOrEmpty(firstFresh) ? "HARNESS/NO-FRESH-DECISION" : "COMBAT/no-loop-to-sustain",
                    selected, stageHp, stageNpc.MaxHp, lastDist, wakes);
            }

            // Observe-only kill of OUR target to authoritative Hp==0.
            (bool Dead, List<string> HpTrail, int Min, int LastHp, bool Leash, string LeashDetail) KillPhase(uint target, int startHp, int stageMax, int cycle)
            {
                var trail = new List<string>();
                var min = startHp; var lastHp = startHp;
                var dead = false; var leash = false; var leashDetail = "";
                var deadline = Environment.TickCount64 + KillBudgetMs;
                while (Environment.TickCount64 < deadline)
                {
                    JsonElement observe;
                    try
                    {
                        observe = bridge.Call(
                            $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                    }
                    catch { Thread.Sleep(2000); continue; }
                    try { observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000); }
                    catch { /* keep wake response */ }
                    NoteDecide(GetStr(observe, "questDecideDetail"));
                    wakeCount++;
                    ScanCastHits(GetStr(observe, "questDecideDetail"), $"liveAction={GetStr(observe, "liveAction")}", castHits);
                    var hpNow = ReadNpcHp(bridge, target);
                    if (hpNow == 0)
                    {
                        dead = true; min = 0; lastHp = 0;
                        trail.Add($"wake={wakeCount} hp=0 DEAD");
                        break;
                    }
                    if (hpNow < 0)
                    {
                        lastHp = min;
                        break;
                    }
                    lastHp = hpNow;
                    if (hpNow < min) min = hpNow;
                    if (trail.Count == 0 || trail[^1] != $"wake={wakeCount} hp={hpNow}")
                        trail.Add($"wake={wakeCount} hp={hpNow}");
                    if (stageMax > 0 && min > 0 && min < stageMax && hpNow - min > 0.25 * stageMax && !leash)
                    {
                        leash = true;
                        leashDetail = $"c{cycle} wake={wakeCount} hp rose {min}->{hpNow} (max {stageMax}; >25% rebound — leash-reset suspect)";
                        evidence.AppendLine($"- LEASH-SUSPECT: {leashDetail}");
                    }
                    Thread.Sleep(2000);
                }
                evidence.AppendLine($"- c{cycle} kill trail: {string.Join(" | ", trail.Take(40))} (startHp={startHp} min={min} dead={dead})");
                Leg($"c{cycle}-kill", dead, $"target={target} hp {startHp}->0 dead={dead}", 0);
                return (dead, trail, min, lastHp, leash, leashDetail);
            }

            // Observe-only loot watch for OUR corpse. Returns grant accounting.
            (int Landed, int Granted, bool Completed, int ContainerAfter, string RemainingProof, int Foreign, int Wakes, int GoneWake) LootPhase(uint corpse, int containerBefore, int cycle)
            {
                var lootWakes = 0; var landed = 0; var granted = -1;
                var completed = false; var after = -1; var proof = "none";
                var foreign = 0; var goneWake = 0; var postWakes = 0;
                var diagSeen = lootDiagTotal.Count;
                var deadline = Environment.TickCount64 + LootBudgetMs;
                while (Environment.TickCount64 < deadline)
                {
                    JsonElement observe;
                    try
                    {
                        observe = bridge.Call(
                            $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":2000}}", 60_000);
                    }
                    catch { Thread.Sleep(1000); continue; }
                    try { observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000); }
                    catch { /* keep wake response */ }
                    wakeCount++; lootWakes++;
                    var decide = GetStr(observe, "questDecideDetail");
                    NoteDecide(decide);
                    ScanCastHits(decide, $"liveAction={GetStr(observe, "liveAction")} lastAction={GetStr(observe, "lastAction")}", castHits);
                    foreach (var line in ScanLootDiags(gameLogOffset, gameRestartLogOffset, ourCharacterId, lootDiagTotal.Count))
                        lootDiagTotal.Add(line);
                    foreach (Match m in Regex.Matches(string.Join("\n", decideHistory), @"landed Loot \(looted (\d+) item\(s\) from (\d+)\)"))
                    {
                        if (m.Groups[2].Value == corpse.ToString(CultureInfo.InvariantCulture))
                        {
                            landed = Regex.Matches(string.Join("\n", decideHistory), $@"landed Loot \(looted \d+ item\(s\) from {corpse}\)").Count;
                            granted = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        }
                    }
                    foreign = Regex.Matches(string.Join("\n", decideHistory), @"landed Loot \(looted (\d+) item\(s\) from (\d+)\)")
                        .Cast<Match>().Count(m => m.Groups[2].Value != corpse.ToString(CultureInfo.InvariantCulture));
                    foreach (var d in lootDiagTotal.Skip(diagSeen))
                    {
                        var m = Regex.Match(d, @"target=(\d+).*state=(\w+).*grant=(\d+)");
                        if (m.Success && m.Groups[1].Value == corpse.ToString(CultureInfo.InvariantCulture)
                            && m.Groups[2].Value == "Completed" && int.TryParse(m.Groups[3].Value, out var g))
                        {
                            completed = true;
                            if (granted < 0) granted = g;
                        }
                    }
                    if (goneWake == 0 && ReadNpcHp(bridge, corpse) < 0)
                    {
                        goneWake = wakeCount;
                        evidence.AppendLine($"- c{cycle} corpse despawned at wake={wakeCount} (recorded only)");
                    }
                    if (landed >= 1 && after < 0)
                        after = ScanContainerAfter(gameLogOffset, gameRestartLogOffset, ourCharacterId, corpse);
                    if (landed >= 1)
                        postWakes++;
                    if (landed >= 1 && postWakes >= 3 && (after >= 0 || goneWake != 0))
                        break;
                    Thread.Sleep(1000);
                }
                // Final exactly-once recount over the FULL decide history.
                landed = decideHistory
                    .SelectMany(d => Regex.Matches(d, @"landed Loot \(looted (\d+) item\(s\) from (\d+)\)").Cast<Match>())
                    .Count(m => m.Groups[2].Value == corpse.ToString(CultureInfo.InvariantCulture));
                if (after < 0)
                    after = ScanContainerAfter(gameLogOffset, gameRestartLogOffset, ourCharacterId, corpse);
                if (after < 0 && goneWake != 0 && completed && granted == containerBefore)
                {
                    after = 0;
                    proof = "drain-accounting";
                }
                else if (after == 0)
                {
                    proof = "container-zero-read";
                }
                evidence.AppendLine($"- c{cycle} loot: wakes={lootWakes} landed={landed} granted={granted} completed={completed} {containerBefore}->{after} ({proof}) foreign={foreign} goneWake={goneWake}");
                return (landed, granted, completed, after, proof, foreign, lootWakes, goneWake);
            }

            // ---- GRANT CYCLES (bounded repeats; early stop at meat>=3) ----
            var execSw = Stopwatch.StartNew();
            string? harnessFail = null;
            for (var cycle = 1; cycle <= MaxGrantCycles; cycle++)
            {
                var meatBefore = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                if (meatTrajectory.Count == 0)
                    meatTrajectory.Add(meatBefore);
                var qsBefore = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                if (cycle == 1)
                {
                    // START snapshot withhold echo + assert (audit trail): the
                    // bridge observe re-reads the per-bot flag, so the fixture
                    // proves the seam is live before the first grant cycle.
                    var startObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                    startWithhold = startObs.TryGetProperty("withholdTurnIn", out var swh) && swh.ValueKind == JsonValueKind.True;
                    evidence.AppendLine($"- START: meat={meatBefore} quest={qsBefore.Status} step={qsBefore.Step} objectives=[{string.Join(",", qsBefore.Objectives)}] active={qsBefore.Active} withhold={startWithhold}");
                    if (!startWithhold)
                    {
                        failBoundary = "HARNESS/withhold-off-at-start";
                        Assert.Fail("START snapshot echoes withholdTurnIn=false; refusing to run Ready-capable cycles unprotected");
                    }
                }

                // Meat already complete (e.g. pre-held bag) — no cycle needed.
                if (meatBefore >= MeatRequired)
                {
                    evidence.AppendLine($"- c{cycle}: meat {meatBefore}>=3 already; skipping further kills");
                    break;
                }

                var drive = DrivePhase(cycle);
                if (!drive.Ok)
                {
                    harnessFail = drive.Fail;
                    evidence.AppendLine($"- c{cycle} drive failed at {drive.Fail}; consuming the cycle");
                    cycles.Add(new CycleResult(cycle, 0, -1, false, -1, false, meatBefore, meatBefore, [], "", "", "n/a", drive.Fail));
                    if (cycle == MaxGrantCycles) break;
                    continue;
                }

                var startHp = ReadNpcHp(bridge, drive.Selected);
                var startQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                evidence.AppendLine($"- c{cycle} START: target={drive.Selected} hp={startHp}/{drive.StageMaxHp} quest={startQs.Status} meat={meatBefore} dist={drive.LiveDist:0.0}m");

                var kill = KillPhase(drive.Selected, startHp, drive.StageMaxHp, cycle);
                if (!kill.Dead)
                {
                    var hpVanished = ReadNpcHp(bridge, drive.Selected) < 0;
                    harnessFail = hpVanished && kill.LastHp > 0 ? "CORPSE/target-lost"
                        : kill.Leash ? "HARNESS/leash-reset" : "LOOT/no-kill";
                    evidence.AppendLine($"- c{cycle} kill failed at {harnessFail} (lastHp={kill.LastHp} leash={kill.Leash})");
                    cycles.Add(new CycleResult(cycle, drive.Selected, startHp, false, -1, false, meatBefore, meatBefore, [], "", "", "n/a", harnessFail));
                    if (harnessFail is "HARNESS/leash-reset")
                        break; // fixture race — stop, never a behavior verdict
                    if (cycle == MaxGrantCycles) break;
                    continue;
                }
                killEngineTotal.AddRange(ScanKillLines(gameLogOffset, gameRestartLogOffset, drive.Selected));

                // Corpse START re-taken at the fresh corpse.
                var corpseHp = ReadNpcHp(bridge, drive.Selected);
                if (corpseHp < 0)
                {
                    evidence.AppendLine($"- c{cycle} corpse {drive.Selected} gone at START (despawned before observation); consuming one spare cycle");
                    cycles.Add(new CycleResult(cycle, drive.Selected, startHp, true, -1, false, meatBefore, meatBefore, [], "", "", "n/a", "HARNESS/corpse-gone"));
                    if (cycle == MaxGrantCycles) break;
                    continue;
                }
                var corpseQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var lootOk = ScanLootOk(gameLogOffset, gameRestartLogOffset, ourCharacterId, drive.Selected);
                var containerBefore = lootOk.Count == 0 ? -1 : lootOk.Max(d => d.Container);
                var corpseMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                evidence.AppendLine($"- c{cycle} CORPSE START: objId={drive.Selected} hp={corpseHp} quest={corpseQs.Status} meat={corpseMeat} container(quest-side)={containerBefore} lootOkDiags={lootOk.Count}");
                if (corpseHp != 0 || !corpseQs.Active || containerBefore < 1)
                {
                    var reason = corpseHp != 0 || !corpseQs.Active ? "HARNESS/corpse-start" : "CORPSE/empty-or-unobserved";
                    evidence.AppendLine($"- c{cycle} corpse START unusable ({reason}); consuming the cycle");
                    cycles.Add(new CycleResult(cycle, drive.Selected, startHp, true, -1, false, meatBefore, corpseMeat, [], "", "", "n/a", reason));
                    if (cycle == MaxGrantCycles) break;
                    continue;
                }

                var loot = LootPhase(drive.Selected, containerBefore, cycle);
                var meatAfter = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var qsAfter = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var funnelHN = ScanFunnelHaveNeed(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                funnelTrail.Add($"c{cycle}:{funnelHN}");
                meatTrajectory.Add(meatAfter);
                objectiveTrajectory.Add($"[{string.Join(",", qsAfter.Objectives)}]");
                statusTrajectory.Add($"{qsAfter.Step}/{qsAfter.Status}");
                Leg($"c{cycle}-grant", loot.Landed == 1 && loot.Completed,
                    $"corpse={drive.Selected} landed={loot.Landed} granted={loot.Granted} {containerBefore}->{loot.ContainerAfter}({loot.RemainingProof}) meat={meatBefore}->{meatAfter} objectives=[{string.Join(",", qsAfter.Objectives)}] quest={qsAfter.Step}/{qsAfter.Status}",
                    0);
                var outcome = loot.Landed != 1 ? "LOOT/dispatch"
                    : !loot.Completed ? "LOOT/not-completed"
                    : loot.Granted != containerBefore - loot.ContainerAfter || loot.ContainerAfter != 0 ? "ITEM/conservation"
                    : meatAfter < meatBefore ? "ITEM/gains" : "granted";
                cycles.Add(new CycleResult(cycle, drive.Selected, startHp, true, loot.Granted, loot.Completed,
                    meatBefore, meatAfter, qsAfter.Objectives, qsAfter.Status ?? "", qsAfter.Step ?? "", funnelHN, outcome));
                // Per-cycle withhold echo (audit trail): the flag must stay ON
                // across every grant cycle — the Ready flip can land at any
                // cycle boundary, and the next tick would turn in if released.
                var cycleWithhold = false;
                try
                {
                    var cycleObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                    cycleWithhold = cycleObs.TryGetProperty("withholdTurnIn", out var cwh) && cwh.ValueKind == JsonValueKind.True;
                }
                catch { /* echo only: a missing cycle echo never changes a verdict */ }
                if (!cycleWithhold) withholdEveryCycle = false;
                evidence.AppendLine($"- c{cycle} post: meat={meatBefore}->{meatAfter} objectives=[{string.Join(",", qsAfter.Objectives)}] quest={qsAfter.Step}/{qsAfter.Status} funnel[{funnelHN}] outcome={outcome} withhold={cycleWithhold}");

                if (meatAfter >= MeatRequired)
                {
                    evidence.AppendLine($"- meat {meatAfter}>=3 after cycle {cycle}; stopping kills (early stop, {MaxGrantCycles - cycle} spare cycles unused)");
                    break;
                }
            }
            execSeconds = execSw.Elapsed.TotalSeconds;

            // ---- POST proofs (read-only): bag, objectives, quest, logs ----
            meatFinal = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var endQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            // Final withhold echo (audit trail): must still read ON — the flag is
            // never released in G7d, so a false here names a mid-gate release
            // (fixture audit only; the verdict arms below stay unchanged).
            var endObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            endWithhold = endObs.TryGetProperty("withholdTurnIn", out var ewh) && ewh.ValueKind == JsonValueKind.True;
            evidence.AppendLine($"- WITHHOLD: start={startWithhold} armed={withholdArmed} perCycle={withholdEveryCycle} end={endWithhold} (REQUIRE ON from before cycle 1 through verdict: Ready fixture survives only while ON; no turn-in, G8 owns it)");
            endObjectives = endQs.Objectives;
            endStep = endQs.Step ?? "";
            endStatus = endQs.Status ?? "";
            readyReached = string.Equals(endStep, "Ready", StringComparison.Ordinal)
                || string.Equals(endStatus, "Ready", StringComparison.Ordinal);
            var questActive = endQs.Active;
            var logCastLoot = ScanGameLogCastLoot(gameLogOffset, gameRestartLogOffset, ourCharacterId, BotName);
            castHits.AddRange(logCastLoot.Where(l => Regex.IsMatch(l, @"\bCast\b")));
            var gatherLines = ScanGatherLines(gameLogOffset, gameRestartLogOffset, ourCharacterId);
            var finalFunnel = ScanFunnelHaveNeed(gameLogOffset, gameRestartLogOffset, ourCharacterId);
            var objective0 = endObjectives.Length > 0 ? endObjectives[0] : -1;
            evidence.AppendLine($"- FINAL: meat trajectory [{string.Join("->", meatTrajectory)}] final={meatFinal}");
            evidence.AppendLine($"- FINAL: objective trajectory [{string.Join(" ", objectiveTrajectory)}] final=[{string.Join(",", endObjectives)}]");
            evidence.AppendLine($"- FINAL: status trajectory [{string.Join(" ", statusTrajectory)}] final={endStep}/{endStatus} active={questActive} ready={readyReached}");
            evidence.AppendLine($"- FINAL: funnel have/need trail [{string.Join(" ", funnelTrail)}] latest=[{finalFunnel}]");
            evidence.AppendLine($"- engine kill lines ({killEngineTotal.Count}) loot-diag lines ({lootDiagTotal.Count}) gather-act lines ({gatherLines.Count}):");
            foreach (var l in lootDiagTotal.Take(8))
                evidence.AppendLine($"  [LOOT-DIAG {l}]");
            foreach (var l in gatherLines.Take(8))
                evidence.AppendLine($"  [GATHER {l}]");
            evidence.AppendLine($"- exclusion: castHits={(castHits.Count == 0 ? "NONE" : string.Join("; ", castHits.Take(10)))}");

            if (castHits.Count != 0)
            {
                verdict = "FAIL-BEHAVIOR/CAST-DETECTED";
                failBoundary = "LOOT/cast-detected";
                failingCondition = new { boundary = failBoundary, hits = castHits.Take(10).ToList(), interpretation = "a Cast record fired during the credit window — the quest loot path casts nothing" };
            }
            else if (!questActive)
            {
                verdict = "FAIL-BEHAVIOR/QUEST-LOST";
                failBoundary = "QUEST/not-active";
                failingCondition = new { boundary = failBoundary, endStep, endStatus, interpretation = "quest 251 is no longer active at verdict time — abandoned/completed/turned-in by someone; credit unprovable" };
            }
            else if (meatFinal < MeatRequired)
            {
                verdict = harnessesOnly(harnessFail) ? "UNKNOWN/HARNESS" : "FAIL-BEHAVIOR/NO-GRANT";
                failBoundary = harnessFail ?? "ITEM/no-grant";
                failingCondition = new
                {
                    boundary = failBoundary,
                    meatTrajectory,
                    meatFinal,
                    required = MeatRequired,
                    cycles = cycles.Select(c => new { c.Cycle, c.Target, c.Dead, c.Granted, c.Completed, c.Outcome }).ToList(),
                    interpretation = "bounded grant budget exhausted before the bag reached 4058x3 — production loot never granted the remaining meat"
                };
            }
            else if (objective0 < MeatRequired)
            {
                // Meat reached 3 but the recount never landed: the CREDIT
                // boundary. Diagnose exactly, patch nothing.
                verdict = "FAIL-BEHAVIOR/CREDIT-NO-EVENT";
                failBoundary = "CREDIT/no-event";
                failingCondition = new
                {
                    boundary = failBoundary,
                    meatFinal,
                    objectives = endObjectives,
                    step = endStep,
                    status = endStatus,
                    questActive,
                    funnelTrail,
                    latestFunnel = finalFunnel,
                    lootGrants = cycles.Select(c => new { c.Cycle, c.Target, c.Granted, c.Completed }).ToList(),
                    gatherActLines = gatherLines.Take(8).ToList(),
                    interpretation = "bag holds 4058x3 from production loot yet the gather-act objective recount never reached 3 — OnItemGather never effective while 251 was Progress (subscription? act-id/item mismatch?). Engine untouched; diagnosis only."
                };
            }
            else if (!readyReached)
            {
                verdict = "FAIL-BEHAVIOR/CREDIT-NO-PROGRESS";
                failBoundary = "CREDIT/no-progress";
                failingCondition = new
                {
                    boundary = failBoundary,
                    meatFinal,
                    objectives = endObjectives,
                    step = endStep,
                    status = endStatus,
                    questActive,
                    funnelTrail,
                    latestFunnel = finalFunnel,
                    interpretation = "objective recount reached 3 but the step machine never advanced Progress->Ready — evaluation/advance gap. Engine untouched; diagnosis only."
                };
            }
            else
            {
                passed = true;
                verdict = "PASS-BEHAVIOR";
                failBoundary = "none";
                claim = $"production death/loot cycles granted meat [{string.Join("->", meatTrajectory)}] (final {meatFinal}), OnItemGather recount drove objectives to [{string.Join(",", endObjectives)}], quest 251 {endStep}/{endStatus} Ready — turn-in NOT performed (G8)";
                failingCondition = new { boundary = "none", meatTrajectory, objectives = endObjectives, step = endStep, status = endStatus, ready = readyReached };
            }
            Leg("gate", passed, $"{verdict} at {failBoundary}: meat=[{string.Join("->", meatTrajectory)}] objectives=[{string.Join(",", endObjectives)}] quest={endStep}/{endStatus} ready={readyReached}",
                execSeconds);
            Assert.True(passed, $"G7d {verdict} at {failBoundary}: meat=[{string.Join("->", meatTrajectory)}] objectives=[{string.Join(",", endObjectives)}] quest={endStep}/{endStatus}");

            static bool harnessesOnly(string? f) => f is "HARNESS/stage-range" or "HARNESS/stage-range-blocked" or "HARNESS/settle" or "HARNESS/fixture-overshoot" or "HARNESS/leash-reset" or "CORPSE/target-lost";
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
                        required = MeatRequired,
                        attemptBudget = new { maxGrantCycles = MaxGrantCycles, killBudgetMs = KillBudgetMs, lootBudgetMs = LootBudgetMs, driveBudgetMs = DriveBudgetMs, maxRestages = MaxRestages },
                        spawner = spawnerXyz,
                        stagedDistM = stagedDist,
                        withhold = new
                        {
                            armed = withholdArmed,
                            start = startWithhold,
                            perCycle = withholdEveryCycle,
                            end = endWithhold,
                            throughout = startWithhold && withholdEveryCycle && endWithhold,
                            dependency = "withhold ON is REQUIRED before cycle 1 and throughout so the Ready fixture survives: without it the first Ready tick would dispatch production GameplayActor.TurnInQuest (no range gate), complete 251, and destroy the fixture under observation (the sweep's QUEST/not-active race). Never released — G7d performs no turn-in; G8 owns Ready → turn-in."
                        },
                        cycles = cycles.Select(c => new
                        {
                            c.Cycle, c.Target, c.StartHp, c.Dead, c.Granted, c.Completed,
                            c.MeatBefore, c.MeatAfter, objectivesAfter = c.ObjectivesAfter,
                            c.QuestStatusAfter, c.QuestStepAfter, c.FunnelHaveNeed, c.Outcome
                        }).ToList(),
                        meatTrajectory,
                        objectiveTrajectory,
                        statusTrajectory,
                        funnelTrail,
                        meatFinal,
                        endObjectives,
                        endStep,
                        endStatus,
                        readyReached,
                        lootDiagLines = lootDiagTotal.Take(8).ToList(),
                        killEngineLines = killEngineTotal.Take(5).ToList(),
                        castHits = castHits.Count,
                        wakeCount,
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
                        $"{{\"scenario\":\"g7d-credit\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"G7D-GATE verdict={verdict} boundary={failBoundary} meat=[{string.Join("->", meatTrajectory)}] " +
                $"objectives=[{string.Join(",", endObjectives)}] quest={endStep}/{endStatus} ready={readyReached} legs={legs.Count}");
        }
    }

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
            if (!loot.Contains(id, StringComparison.Ordinal))
                continue;
            if (!loot.Contains("validate=ok", StringComparison.Ordinal) || !loot.Contains("lootable=true", StringComparison.Ordinal))
                continue;
            var cm = Regex.Match(loot, @"container=(\d+)");
            var wake = Regex.Match(line, @"wake=(\d+)");
            out_.Add(new LootOkDiag(
                wake.Success ? int.Parse(wake.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                cm.Success ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : -1,
                loot.Length > 200 ? loot[..200] : loot));
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

    // ---- funnel have/need scan: latest quest-side relevance reading for
    // OUR char (bag-count trajectory as the quest leg sees it).
    private static string ScanFunnelHaveNeed(long gameLogOffset, long gameRestartLogOffset, uint charId)
    {
        string last = "none";
        foreach (var line in ReadQuestLines(gameLogOffset, gameRestartLogOffset, charId, "QuestObjectiveTargetDiag"))
        {
            var have = Regex.Match(line, @"have=(\d+)").Groups.Cast<Group>().Skip(1).FirstOrDefault()?.Value ?? "?";
            var need = Regex.Match(line, @"need=(\d+)").Groups.Cast<Group>().Skip(1).FirstOrDefault()?.Value ?? "?";
            var rel = Regex.Match(line, @"relevance=(\S+)").Success
                ? Regex.Match(line, @"relevance=(\S+)").Groups[1].Value : "?";
            last = $"have={have} need={need} relevance={rel}";
        }
        return last;
    }

    // ---- gather-act lines: any QuestActObjItemGather engine log for OUR
    // char (InitializeQuest/RunAct — recount-side corroboration).
    private static List<string> ScanGatherLines(long gameLogOffset, long gameRestartLogOffset, uint charId)
    {
        return ReadQuestLines(gameLogOffset, gameRestartLogOffset, charId, "QuestActObjItemGather")
            .Select(l => l.Length > 240 ? l[..240] : l).Take(20).ToList();
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
    // stageFail distinguishes an EMPTY spawner from a spawner BLOCKED by a corpse
    // (engine: NpcSpawner.CanSpawn denies while the dead unit still holds the
    // ObjId). While the 90 s watch runs it probes the blocking corpse's container
    // through npcState (read-only), drains it ONCE through the existing real
    // actor path when lootable (starting the engine's ~2 s post-loot despawn
    // clock), and otherwise just keeps watching — the engine's own 320 s
    // (unlooted-with-items) / 20 s respawn clocks own the timeline and the 90 s
    // watch budget is NEVER extended. Expiry with a corpse seen =>
    // HARNESS/stage-range-blocked (new string, ONLY for this blocked-vs-empty
    // distinction); expiry with no 3475 ever observed => HARNESS/stage-range.
    private bool StageAtBoar(BotDriveClient bridge, StringBuilder evidence, out string spawner, out double flat, out uint boarObjId, out string stageFail)
    {
        spawner = "ABSENT";
        flat = double.NaN;
        boarObjId = 0;
        stageFail = "HARNESS/stage-range";
        try
        {
            var teleportResp = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{BoarTemplate3475}}}", 60_000);
            spawner = teleportResp.TryGetProperty("x", out var stx) && teleportResp.TryGetProperty("y", out var sty) && teleportResp.TryGetProperty("z", out var stz)
                ? FormattableString.Invariant($"{stx.GetDouble():F1},{sty.GetDouble():F1},{stz.GetDouble():F1}") : "ABSENT";
            // PollNpcObjId returns the nearest 3475 — which may be a lingering
            // corpse (Hp 0) holding this spawner (NpcSpawner.CanSpawn denies
            // while HasCorpse). Never stage onto a corpse: wait bounded for a
            // live boar, probing the blocker's container while we wait. The
            // 90 s watch is a ceiling and is NEVER extended — the engine's own
            // unlooted-with-items (DespawnTime+300 s) / looted-to-empty (~2 s)
            // clocks own the timeline.
            var liveDeadline = Environment.TickCount64 + 90_000;
            uint blockerObjId = 0;      // corpse seen holding the spawner this watch
            var drainDispatched = false; // exactly ONE drain per blocked watch
            var unlootedSeen = 0;       // observations of a corpse still carrying items
            var emptySeen = 0;          // observations of an emptied/unobserved corpse
            while (Environment.TickCount64 < liveDeadline)
            {
                boarObjId = PollNpcObjId(bridge, BoarTemplate3475, 5);
                if (boarObjId != 0)
                {
                    var probe = ProbeNpc(bridge, boarObjId);
                    if (probe.Hp > 0)
                        break;
                    blockerObjId = boarObjId;
                    if (probe.Lootable)
                    {
                        // BLOCKER/unlooted-wait: the corpse still carries items,
                        // so it lingers on the engine's DespawnTime+300 s clock
                        // and NpcSpawner.CanSpawn stays denied. Drain it ONCE
                        // through the existing real actor path — that starts the
                        // ~2 s post-loot despawn clock, so the respawn can land
                        // inside the SAME 90 s watch (budget never extended).
                        unlootedSeen++;
                        evidence.AppendLine($"- stage: BLOCKER/unlooted-wait — nearest 3475 objId={boarObjId} is a corpse (hp={probe.Hp}) holding {probe.Container} container item(s); spawner BLOCKED by corpse");
                        if (!drainDispatched)
                        {
                            drainDispatched = true;
                            // The drain call itself is capped to the remaining
                            // watch time so a stalled bridge can never extend
                            // the 90 s gate budget.
                            var drainMs = (int)Math.Max(5_000, liveDeadline - Environment.TickCount64);
                            evidence.AppendLine($"- stage: drain (ONE dispatch, real actor path, timeout={drainMs}ms) RESPONSE verbatim: {DispatchCorpseDrain(bridge, boarObjId, drainMs)}");
                        }
                    }
                    else
                    {
                        // BLOCKER/empty-wait: container empty or unobserved —
                        // nothing we can drain; only the engine's own clocks
                        // (320 s unlooted-despawn / respawn window) can clear it.
                        emptySeen++;
                        evidence.AppendLine($"- stage: BLOCKER/empty-wait — nearest 3475 objId={boarObjId} is a corpse (hp={probe.Hp}, containerExists={probe.ContainerExists}, container={probe.Container}); spawner BLOCKED, no drain possible");
                    }
                    boarObjId = 0;
                }
                Thread.Sleep(2000);
            }
            if (boarObjId == 0)
            {
                if (blockerObjId != 0)
                {
                    // Blocked, not empty: the spawner is denied by a corpse.
                    // Same harness category as stage-range (never a verdict),
                    // named separately so the report distinguishes the two.
                    stageFail = "HARNESS/stage-range-blocked";
                    evidence.AppendLine($"- stage: no live 3475 within 90s; spawner BLOCKED by corpse objId={blockerObjId} (BLOCKER/unlooted-wait x{unlootedSeen} vs BLOCKER/empty-wait x{emptySeen}, drainDispatched={drainDispatched}); 90s gate budget NOT extended");
                }
                else
                {
                    evidence.AppendLine("- stage: no live 3475 materialized within 90s (spawner EMPTY — no 3475 corpse observed holding it)");
                }
                return false;
            }
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

    private sealed record NpcProbe(int Hp, int MaxHp, bool ContainerExists, int Container, bool Lootable);

    // Read-only container probe for a blocking corpse (npcState already exposes
    // container/containerExists/lootable — the lootable predicate mirrors
    // GameplayActor.Loot's own gates as a read: resolves + in range + non-empty).
    private static NpcProbe ProbeNpc(BotDriveClient bridge, uint objId)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            var hp = el.TryGetProperty("hp", out var h) && h.TryGetInt32(out var hn) ? hn : -1;
            var maxHp = el.TryGetProperty("maxHp", out var m) && m.TryGetInt32(out var mn) ? mn : -1;
            var exists = el.TryGetProperty("containerExists", out var ce) && ce.ValueKind == JsonValueKind.True;
            var container = el.TryGetProperty("container", out var c) && c.TryGetInt32(out var cn) ? cn : -1;
            var lootable = el.TryGetProperty("lootable", out var lv) && lv.ValueKind == JsonValueKind.True;
            return new NpcProbe(hp, maxHp, exists, container, lootable);
        }
        catch
        {
            return new NpcProbe(-1, -1, false, -1, false);
        }
    }

    // One drain dispatch through the SAME real actor path Q4 uses for live loot
    // (GameplayActor.Loot with lootAll semantics) — never a fixture shortcut.
    private static string DispatchCorpseDrain(BotDriveClient bridge, uint objId, int timeoutMs)
    {
        try
        {
            return bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"loot\",\"npcObjId\":{objId}}}", timeoutMs).ToString();
        }
        catch (Exception ex)
        {
            return $"THREW {ex.GetType().Name}: {ex.Message}";
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
                Console.WriteLine("[g7d] " + line);
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
                    Console.WriteLine("[g7d] " + kline);
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
                scenario = "g7d-credit",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "turn-in", "rotation", "kill-teardown", "any other quest" },
                callPath = "bridge quest wake → scheduler Wake → quest leg (QuestBehavior.Run loot path: LastCorpse + ProbeCorpse → LootProposal → GameplayActor.Loot → LootingContainer.OpenBag lootAll) → BotDecisionCycle; engine owns death (Unit.DoDie → LootingContainer.GenerateLoot) + grant (Bag.AcquireDefaultItem → OnAcquiredItem → DoItemsAcquiredEvents → OnItemGather act-id+4058 → recount SetObjective) + step machine (Progress→Ready); the gate adds no verbs during drive/kill/loot/credit — wake + observe + read-only npcState HP/container polls only; the ONLY staging verb is one drive-op 'loot' drain (same GameplayActor.Loot path) dispatched when the stage watch is blocked by a still-lootable 3475 corpse",
                g7cLeg = "G7c-equivalent first cycle inside this test (fresh G7a-style run to authoritative death; corpse START re-taken at the fresh corpse with quest-side lootable + non-empty-container proof); bounded repeats to meat>=3",
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
            Console.WriteLine($"G7D-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
