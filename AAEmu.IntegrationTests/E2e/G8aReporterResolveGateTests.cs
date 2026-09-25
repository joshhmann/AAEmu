using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// G8a gate (REPORTER RESOLUTION, Ready-gated TurnIn proposal identity) —
/// with quest 251 held Ready, every quest-tick wake must resolve reporter
/// 3512 through the production path (CharacterWorld.GetNpcByTemplateId per
/// wake, never stored) to the SAME live ObjId, and the TurnInQuest proposal
/// must sit present-but-withheld: SelectedAction set (TurnInQuest), Request
/// null (nothing landed), FailStage WITHHELD naming reporter=<ObjId>.
///
/// Production chain under test (QuestBehavior.TurnInProposals →
/// BotDecisionSelector → withhold seam): Ready check on the live quest
/// Status → template Ready-acts reportNpc 3512 → GetNpcByTemplateId(3512)
/// → TurnInProposal(TurnInQuest, reporter.ObjId) competing at
/// TurnInPriority 30 (above advance/accept/objective-target, so a Ready
/// quest still reports first) → selected → withhold seam returns
/// Fail("WITHHELD", SelectedAction=TurnInQuest, Request=null).
///
/// Observe-surface mapping (the gate reads ONLY these): a withheld wake
/// surfaces as questDecideDetail = "WITHHELD: withheld turn-in TurnInQuest
/// quest 251 reporter=<ObjId> ..." — WorkSelected=false (no "landed"
/// prefix) proves Request==null, the named TurnInQuest proves
/// SelectedAction was set, and reporter=<ObjId> is the behavior's own
/// per-wake resolution. A return-leg wake surfaces as questDecideDetail =
/// "landed Move (...)" / "landed Stop (...)" / "landed InteractNpc (...)"
/// (the G8b arbitration: the quest-owned return leg — MoveToUnit on the live
/// reporter ObjId while outside the 25 m InteractNpc gate, audited Stop once
/// inside, InteractNpc once settled — owns the wake, and that ownership
/// SUPPRESSES the priority-30 TurnIn proposal, so no WITHHELD observable
/// exists on such wakes); the behavior's own per-wake reporter resolution for
/// that same wake rides the fresh QuestObjectiveTargetDiag line's
/// return=[validate=ok:reporter=<ObjId>:template=3512:...:dispatch=<token>...]
/// fragment, drained from exactly the log bytes that wake appended (per-wake
/// join, no shared clock). The bridge reporter probe (observe "reporter":
/// 3512 → reporterObjId via the same GetNpcByTemplateId) plus the
/// independent drive npcObjId(3512) cross-check triple-bind template→ObjId.
///
/// WITHHOLD-ON DEPENDENCY (explicit): withhold ON is REQUIRED so the
/// fixture survives — without it the first Ready tick would dispatch the
/// production GameplayActor.TurnInQuest, complete 251, and destroy the
/// Ready fixture the gate is observing. A landed TurnInQuest is therefore
/// a WITHHOLD/leaked-dispatch failure, never an expectation.
///
/// Tick definition: a stepped wake whose OWN appended log bytes carry a
/// fresh quest-leg decide line (BotRoamStepExecutor's per-wake QuestSweepDiag
/// mirror). DECIDE sweep-empty wakes are leg-warming neutral (the executor
/// actor pose still syncing after place — zero discover targets, no proposal
/// built, no reporter named); they are recorded but never judged. The observe
/// surface alone is NOT sufficient for this: it is overwritten per quest wake
/// and therefore survives byte-identical across wakes where the leg was
/// skipped (actor held by another leg), which the frozen run miscounted as 10
/// ticks off a single real leg run — hence the fresh-log requirement. The
/// window runs until ≥10 ticks land or 120 s elapse (tickless instant wakes
/// paced 5 s apart so the window spans time).
///
/// DUAL-ARM REPORTER BIND (the arbitration accepted here): a tick wake
/// passes the reporter predicate on EITHER arm, both keyed on the SAME
/// per-wake ObjId —
///   (a) WITHHELD arm: the wake's decide (its own fresh per-wake decide
///       line, the same string the observe surface mirrors) starts
///       "WITHHELD:" and names "TurnInQuest" + "quest 251" +
///       reporter=<ObjId> (the turn-in proposal was built, selected, and
///       withheld: Request==null), OR
///   (b) RETURN-LEG arm: the wake's decide is a landed return-leg dispatch
///       ("landed Move"/"landed Stop"/"landed InteractNpc") AND the wake's
///       OWN fresh return=[validate=ok:reporter=<ObjId>:template=3512:...
///       :dispatch=<token>...] funnel fragment names the reporter with the
///       matching dispatch token (the return leg resolved the reporter per
///       wake and owns the wake outside the 25 m gate — the priority-30
///       TurnIn proposal is suppressed for that wake, so arm (a) cannot
///       exist there).
/// Both arms require, on every tick wake: the bridge probe, the drive
/// npcObjId cross-check, and the arm's own reported ObjId to be one and the
/// same value; zero landed TurnInQuest/TurnInDoodad/AutoTurnIn; zero reward
/// deltas; and quest 251 Ready/active. REPORTER/unresolved still fires when
/// NEITHER arm names the reporter (or the values disagree) — the boundary
/// name is unchanged.
///
/// DRIFT HOLD (warming wakes only — mechanism choice: per-wake preempt-stop).
/// Before every WARMING wake the harness runs the G8-canonical preempt —
/// POST /api/actors/stop — whenever a live Move leg owns the actor, then
/// restores the stage anchor with the same disclosed `quest place` op the
/// staging used. A ONE-SHOT preempt cannot hold: the route layer re-arms a
/// QUEST_TRAVEL leg on the next idle quest wake (two such legs in the frozen
/// log), and while any Move is live the executor skips the quest leg
/// outright — which is why the frozen run drifted 17.7 m → 204 m across 17
/// warming wakes AND rendered zero real quest legs in that span (the 10
/// "ticks" it counted came from the stale observe mirror). Re-asserting the
/// pose alone cannot hold either: a live leg keeps applying steps and walks
/// the bot off the anchor again. So the preempt is the mechanism and the place
/// is the anchor restore that follows it; both stop entirely once a tick
/// lands, because post-tick movement is the return leg's own behavior
/// evidence. Per-wake distance stays recorded-not-judged.
///
/// The harness issues no gameplay verb during the observation window: quest
/// wake + quest observe + read-only npcState/npcObjId/charPos polls only,
/// plus (warming phase only) the canonical preempt Stop — POST
/// /api/actors/stop, issued whenever a live Move leg owns the actor — and the
/// disclosed SETUP-ONLY `quest place` re-assert that restores the stage
/// anchor. Both stop entirely at the first judged tick.
/// Fixture credit (accept + stock 4058x3 through the real acquisition fanout
/// + advance to Ready) is setup, staged
/// BEFORE START. Staging is a single place ~18 m from reporter 3512 (inside
/// the 25 m sweep so the TurnIn path can engage; withhold ON so nothing
/// lands) asserted ONCE; the re-assert keeps that anchor for the warming
/// wakes and is skipped once a tick lands (post-tick movement is behavior
/// evidence and never judged; no drift fail).
///
/// PASS needs ALL of: window closed with tick quorum (≥10 quest ticks)
/// + every tick wake resolves reporter 3512 to the same live ObjId (one
/// epoch; a respawn re-baselines the epoch — recorded, still PASS) through
/// EITHER arm of the reporter bind (WITHHELD TurnInQuest quest 251
/// reporter=<same>, OR the wake's own validated return-leg fragment
/// return=[validate=ok:reporter=<same>...] on a landed return-leg wake) +
/// 251 Ready/active on every tick wake and at verdict + 0 landed
/// TurnInQuest/TurnInDoodad/AutoTurnIn (decide history + turnIns counter +
/// completedCount) + 0 reward (money delta 0) + dist ~18 m asserted ONCE
/// at START inside the sweep (per-wake dist recorded, never judged; warm
/// wakes re-assert the stage pose so the first judged tick is not walked
/// out of the sweep) + no other landed gameplay dispatch (a landed
/// return-leg Move/Stop/InteractNpc carrying the validated reporter
/// fragment is the arbitration, not a leak). A 0-tick window expiry is
/// UNKNOWN with the yielded arbiter state captured as the finding.
///
/// FAIL boundaries are predicate-named:
///   REPORTER/unresolved           — a tick wake with reporterObjId==0, or
///                                   template cross-check mismatch, or a
///                                   wake where NEITHER arm names the
///                                   reporter (no WITHHELD TurnInQuest-251
///                                   detail and no validated return-leg
///                                   fragment)
///   REPORTER/flap-replacement     — ObjId flapping across wakes while the
///                                   old ObjId is still live (no respawn)
///   WITHHOLD/no-proposal          — a wake whose withhold seam fired on
///                                   something OTHER than the
///                                   TurnInQuest-251 proposal (selection
///                                   lost while Ready)
///   WITHHOLD/leaked-dispatch      — a landed dispatch on a tick wake that
///                                   no return-leg arm explains, any landed
///                                   TurnIn*, turnIns growth, completed
///                                   growth, money delta, or any landed
///                                   foreign verb (Cast/Loot/Accept/Advance/
///                                   Discover)
///   WITHHOLD/quest-lost           — 251 not Ready/active with no landed
///                                   turn-in explaining it
///   HARNESS/*                     — setup/staging/quorum races (never
///                                   a behavior verdict; drift excluded)
///
/// STOPS at observation: no G8b (return leg) even on PASS.
[Collection("e2e")]
public class G8aReporterResolveGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G8aResolve" + Stamp;
    private static readonly string BotAccount = ("g8aresolve" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    private const uint Quest251 = 251;
    private const uint ReporterNpc3512 = 3512;
    private const uint BoarMeat4058 = 4058;
    private const int MeatRequired = 3;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;
    private const int TickQuorum = 10;
    private const int WindowSeconds = 120;
    private const int MaxWakes = 40;
    private const int PaceMs = 5000;
    private const double HoldRadiusM = 25.0;
    private const double StageDistM = 18.0;
    private const double StageFloorM = 10.0;
    /// <summary>
    /// Drift-hold band (flat m) for the warming wakes: before a wake whose tick
    /// is still unrendered, the bot is re-placed at the recorded stage anchor
    /// once it has walked past this band. The frozen run drifted 17.7 m → 204 m
    /// across 17 warming wakes (a stale QUEST_TRAVEL Move leg owning the actor),
    /// so the first judged tick sat 204 m from the reporter and outside the
    /// 25 m InteractNpc gate — unjudged. Held here so the first tick starts
    /// inside the staged sweep.
    /// </summary>
    private const double StageHoldBandM = 1.0;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "g8a-resolve-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    private sealed record WakeRow(
        int Wake, bool Stepped, bool Tick, bool Warming,
        uint ReporterObjId, double ReporterDistM, uint XCheckObjId,
        bool Withheld, uint DetailReporter,
        string QuestStep, string QuestStatus, bool QuestActive,
        int TurnIns, long Money, string Decide)
    {
        /// <summary>The withhold seam label appeared at all (right or wrong selection).</summary>
        public bool WithheldLabel { get; init; }
        /// <summary>
        /// (b) RETURN-LEG arm: this wake's decide is a landed return-leg
        /// dispatch (Move/Stop/InteractNpc) AND the return-leg arbitration
        /// fragment names the reporter with the SAME dispatch token as the
        /// landed action. (Arm (a) is the positional Withheld flag.)
        /// </summary>
        public bool ArmReturnLeg { get; init; }
        /// <summary>Reporter ObjId named by the return-leg fragment (0 = none seen yet).</summary>
        public uint ReturnLegReporter { get; init; }
        /// <summary>Dispatch token the fragment carried (move/stop/interact/...).</summary>
        public string ReturnLegDispatch { get; init; } = "";
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ReporterResolvesStablyWhileTurnInWithheld()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G8a reporter-resolve gate (Ready-gated TurnIn proposal identity under withhold) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        evidence.AppendLine($"- OBSERVE-ONLY: quest-wake window until {TickQuorum} quest-leg ticks OR {WindowSeconds}s (waitMs={WakeWaitMs / 1000}s, pacing {PaceMs / 1000}s between tickless wakes; DECIDE sweep-empty warm-up wakes neutral; a tick requires the wake's OWN fresh quest-leg decide line in the log, not just the observe mirror) + quest observe (arbiterActivity/liveAction/liveState captured per wake) + read-only npcState/npcObjId/charPos polls; DUAL-ARM reporter bind: WITHHELD TurnInQuest-251 (turn-in proposal withheld) OR the return-leg arbitration fragment return=[validate=ok:reporter=<ObjId>...] (G8b return leg owns the wake, TurnIn suppressed); WARMING-PHASE DRIFT HOLD via canonical preempt Stop + disclosed `quest place` re-assert (stops at the first tick); withhold ON is REQUIRED so the Ready fixture survives (a landed TurnInQuest is WITHHOLD/leaked-dispatch, never an expectation)");

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
        var decideHistory = new List<string>();
        var rows = new List<WakeRow>();
        var tickRows = new List<WakeRow>();
        long gameLogOffset = 0;
        long gameRestartLogOffset = 0;
        var stabilityMode = "same-ObjId";
        var respawnEpochs = new List<string>();
        // Drift-hold state (warming-phase only): declared here so the report
        // written in `finally` can carry the counts on every exit path.
        var holdActive = true;
        var holdReasserts = 0;
        var holdPreempts = 0;
        HttpClient? holdPreemptClient = null;

        string startStep = "", startStatus = "";
        int[] startObjectives = [];
        uint startReporter = 0;
        double startDist = double.NaN;
        int startTurnIns = 0, startCompleted = 0, startMeat = -1;
        long startMoney = -1;

        void Leg(string leg, bool ok, string detail, double ms)
        {
            legs.Add((leg, ok, detail, ms));
            evidence.AppendLine($"- [{(ok ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        try
        {
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = KillForeignWebApiListeners();
            evidence.AppendLine($"- stale-proc guard (shared :{E2eStack.WebApiPort} listeners outside this lane): {staleGuardDetail}");
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "warm lane answering (bridge ping)" : "lane cold — refusing to rebuild",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                failBoundary = "HARNESS/lane-down";
                Assert.Fail("warm lane not answering; refusing to rebuild per workstream constraints");
            }

            AdoptLaneDbPassword();
            var bridge = new BotDriveClient(E2eStack.BridgePort);
            laneBridge = bridge;

            E2eStack.CleanupBotRows(BotAccount);
            session = await BotNetworkSession.ConnectAsync(
                BotName, BotAccount, Password,
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!session.InWorld)
            {
                failBoundary = "HARNESS/enter-world";
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
                failBoundary = "HARNESS/autonomy-preempted";
                Assert.Fail("quest already active after enroll wake; re-run with a fresh account");
            }

            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{FixtureLevel}}}", 30_000);

            // FIXTURE (setup, production path): accept 251 at live 3512, then
            // grants to Ready through the real acquisition fanout.
            var fixSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{ReporterNpc3512}}}", 60_000);
            var giverObjId = PollNpcObjId(bridge, ReporterNpc3512);
            if (giverObjId == 0)
            {
                failBoundary = "HARNESS/reporter-absent";
                Leg("fixture-reporter", false, "reporter 3512 never materialized (objId=0 after 30s poll)", fixSw.Elapsed.TotalSeconds);
                Assert.Fail("reporter 3512 never materialized; fixture cannot stage");
            }
            var acceptResp = E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"accept\",\"quest\":{Quest251},\"acceptor\":\"Npc\",\"acceptorId\":{ReporterNpc3512}}}");
            var accepted = acceptResp.TryGetProperty("accepted", out var accEl) && accEl.ValueKind == JsonValueKind.True;
            Leg("fixture-accept", accepted, $"accept251 at live 3512 objId={giverObjId} accepted={accepted}", fixSw.Elapsed.TotalSeconds);
            if (!accepted)
            {
                failBoundary = "HARNESS/fixture-accept";
                Assert.Fail("production accept of quest 251 refused by the engine gate");
            }

            E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"item\":{BoarMeat4058},\"count\":{MeatRequired}}}");
            var qsAfterStock = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            if (qsAfterStock.Objectives.Length == 0 || qsAfterStock.Objectives[0] < MeatRequired)
            {
                E2eQuestDriver.Call(bridge, BotName,
                    $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"gather\",\"quest\":{Quest251},\"item\":{BoarMeat4058},\"count\":{MeatRequired}}}");
                qsAfterStock = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            }
            if (qsAfterStock.Active && !IsReady(qsAfterStock))
            {
                E2eQuestDriver.Call(bridge, BotName,
                    $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"advance\",\"quest\":{Quest251}}}");
            }
            var readyQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            var readyDeadline = Environment.TickCount64 + 60_000;
            while ((!readyQs.Active || !IsReady(readyQs)) && Environment.TickCount64 < readyDeadline)
            {
                Thread.Sleep(1000);
                readyQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            }
            var readyOk = readyQs.Active && IsReady(readyQs);
            Leg("fixture-ready", readyOk,
                $"objectives=[{string.Join(",", qsAfterStock.Objectives)}]->[{string.Join(",", readyQs.Objectives)}] quest={readyQs.Step}/{readyQs.Status} active={readyQs.Active}",
                fixSw.Elapsed.TotalSeconds);
            if (!readyOk)
            {
                failBoundary = "HARNESS/fixture-credit";
                Assert.Fail($"production grants never drove 251 to Ready (objectives=[{string.Join(",", readyQs.Objectives)}] quest={readyQs.Step}/{readyQs.Status} active={readyQs.Active})");
            }

            // Vitals refill (fixture must not limp into the hold).
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
                failBoundary = "HARNESS/refill-ineffective";
                Assert.Fail($"fixture refill ineffective (hpFrac={hpFrac:0.000} mpFrac={mpFrac:0.000}); refusing a recovery-triggered START");
            }

            // STAGE (setup, disclosed, moving-START): place once ~18 m from
            // the live reporter (INSIDE the 25 m quest sweep so the TurnIn
            // path can engage; withhold stays ON so nothing lands), assert
            // ONCE (npcObjId poll + observe flat dist), then START
            // immediately. No settle, no Stop: the bot walks ~1.5 m/s and
            // settle never converges on slopes, so standstill is not a precondition. Place-only
            // staging; no gameplay verb fires. Movement after START is
            // allowed and never judged.
            var stageSw = Stopwatch.StartNew();
            var probe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
            var repX = GetDbl(probe, "reporterX"); var repY = GetDbl(probe, "reporterY"); var repZ = GetDbl(probe, "reporterZ");
            if (double.IsNaN(repX) || double.IsNaN(repY) || double.IsNaN(repZ))
            {
                failBoundary = "HARNESS/reporter-absent";
                Leg("stage-hold", false, "reporter position unavailable in observe probe", stageSw.Elapsed.TotalSeconds);
                Assert.Fail("reporter 3512 position unavailable; cannot stage the hold");
            }
            var place = bridge.Call(FormattableString.Invariant(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"place\",\"x\":{repX + StageDistM:F1},\"y\":{repY:F1},\"z\":{repZ:F1}}}"), 60_000);
            evidence.AppendLine($"- place +{StageDistM:0}x RESPONSE verbatim: {place}");
            var holdReporter = PollNpcObjId(bridge, ReporterNpc3512);
            var holdObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
            var holdObsReporter = GetUInt(holdObs, "reporterObjId");
            if (holdReporter == 0) holdReporter = holdObsReporter;
            var holdDist = GetDbl(holdObs, "reporterDistM");
            var holdOk = holdReporter != 0 && holdDist > StageFloorM && holdDist < HoldRadiusM;
            Leg("stage-hold", holdOk,
                $"+{StageDistM:0}x: reporter={holdReporter} (observe xcheck={holdObsReporter}) dist={holdDist:0.0}m (REQUIRE reporter live + {StageFloorM:0}m<dist<{HoldRadiusM:0}m ONCE; inside sweep, withhold ON)",
                stageSw.Elapsed.TotalSeconds);
            if (!holdOk)
            {
                failBoundary = "HARNESS/reporter-range";
                Assert.Fail($"moving-START staging failed (reporter={holdReporter} dist={holdDist:0.0}m; REQUIRE live + {StageFloorM:0}m<dist<{HoldRadiusM:0}m once)");
            }

            // Drift-hold anchor (setup): the staged pose, re-asserted before
            // every warming wake until the first judged tick lands. Captured
            // from the place response (engine truth), not recomputed.
            var anchorX = GetDbl(place, "x");
            var anchorY = GetDbl(place, "y");
            var anchorZ = GetDbl(place, "z");
            if (double.IsNaN(anchorX) || double.IsNaN(anchorY) || double.IsNaN(anchorZ))
            {
                failBoundary = "HARNESS/reporter-absent";
                Assert.Fail("place response carried no usable anchor position");
            }
            var anchorText = FormattableString.Invariant($"{anchorX:F1},{anchorY:F1},{anchorZ:F1}");

            // WITHHOLD ON (production TurnInQuest must NOT land — that is the
            // point of the withhold, not a failure). Armed after staging so
            // no fixture wake runs Ready-unprotected.
            var withholdResp = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"withhold\",\"on\":true}}", 30_000);
            var withholdArmed = withholdResp.TryGetProperty("withholdTurnIn", out var whEl) && whEl.ValueKind == JsonValueKind.True;
            Leg("withhold-arm", withholdArmed, $"withholdTurnIn={withholdArmed} (REQUIRED: fixture survives only while ON)", 0);
            if (!withholdArmed)
            {
                failBoundary = "HARNESS/withhold-refused";
                Assert.Fail("withhold seam refused to arm; refusing to run Ready wakes unprotected");
            }

            gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // Per-wake arbitration join (incremental, rotation-safe): the quest
            // leg mints cycle=quest-<charId>-<ticks> and stamps it on BOTH the
            // bot-executor QuestSweepDiag line (char=<charId>) and that wake's
            // QuestObjectiveTargetDiag funnel line (char=<charId>), so the
            // wake's OWN return-leg fragment joins to it without a shared clock.
            var gameLogCursor = new LogCursor(GameLogPath);
            var restartLogCursor = new LogCursor(GameRestartLogPath);

            // DRIFT HOLD (warming phase only): the stage anchor re-asserted
            // before every wake until the first judged tick lands. The frozen
            // run drifted 17.7 m → 204 m across 17 warming wakes because a live
            // QUEST_TRAVEL Move leg owned the actor (a one-shot preempt cannot
            // hold: the route layer re-arms a travel leg on every idle quest
            // wake — observed twice in the frozen log), so the anchor is
            // re-applied per warming wake instead. Skipped once a tick lands:
            // post-tick movement is behavior evidence, never setup.
            // (holdActive/holdReasserts/holdPreempts/holdPreemptClient are
            // declared before the try so the report can carry them.)

            // START (authoritative, observe-only).
            var startObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
            var startQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            startStep = startQs.Step ?? "";
            startStatus = startQs.Status ?? "";
            startObjectives = startQs.Objectives;
            startReporter = GetUInt(startObs, "reporterObjId");
            startDist = GetDbl(startObs, "reporterDistM");
            startTurnIns = GetInt(startObs, "turnIns");
            startCompleted = GetInt(startObs, "completedCount");
            startMoney = GetInt64(startObs, "money");
            startMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var startWithhold = startObs.TryGetProperty("withholdTurnIn", out var swh) && swh.ValueKind == JsonValueKind.True;
            evidence.AppendLine($"- START: quest={startStep}/{startStatus} objectives=[{string.Join(",", startObjectives)}] active={startQs.Active} reporter={startReporter} dist={startDist:0.0}m turnIns={startTurnIns} completed={startCompleted} money={startMoney} meat={startMeat} withhold={startWithhold}");
            var startOk = startQs.Active && IsReady(startQs) && startReporter != 0 && startDist > StageFloorM && startDist < HoldRadiusM && startWithhold;
            Leg("gate-start", startOk,
                $"quest={startStep}/{startStatus} reporter={startReporter} dist={startDist:0.0}m withhold={startWithhold}", 0);
            if (!startOk)
            {
                failBoundary = "HARNESS/start-unusable";
                Assert.Fail("START snapshot unusable (quest not Ready, reporter unresolved, hold violated, or withhold off)");
            }

            // OBSERVE-ONLY window: until TickQuorum quest-leg ticks OR
            // WindowSeconds elapse (whichever first), polling quest-leg
            // activity each wake. Tickless wakes that return instantly are
            // paced PaceMs apart so the window spans time instead of burning
            // wakes in seconds. Arbiter state (arbiterActivity/liveAction/
            // liveState) is captured per wake; on a 0-tick expiry it becomes
            // the finding.
            //
            // Per-wake the gate drains only the log bytes appended since the
            // previous wake and takes THIS wake's own QuestObjectiveTargetDiag
            // funnel line (char=<charId>, quest=251) — the return-leg arm's
            // reported ObjId — plus the BotRoamStepExecutor QuestSweepDiag line
            // whose decide=[...] the observe surface mirrors (the same-wake
            // join, and the proof the fragment belongs to this wake's landed
            // dispatch).
            var execSw = Stopwatch.StartNew();
            var activities = new List<string>();
            holdPreemptClient = await TryEnsureWebApiClientAsync(evidence);
            var w = 0;
            while (true)
            {
                w++;
                if (w > MaxWakes) break;

                // DRIFT HOLD (warming phase only, SETUP-ONLY + the single
                // canonical preempt verb): while NO tick has rendered yet,
                // (1) clear any live Move leg owning the actor — a live
                // QUEST_TRAVEL route leg both walks the bot away from the
                // stage and, by the executor's TryBegin guard, suppresses the
                // quest leg entirely (the frozen run rendered ZERO real quest
                // legs across 17 warming wakes for exactly this reason), then
                // (2) re-assert the staged anchor via the same disclosed
                // `quest place` op the staging used. Skipped entirely once a
                // tick lands: post-tick movement is the return leg's own
                // behavior evidence. Read-only charPos + observe first, so an
                // already-idle, already-anchored bot costs no verb.
                if (holdActive)
                {
                    try
                    {
                        var holdProbe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
                        var holdLive = GetStr(holdProbe, "liveAction");
                        var holdState = GetStr(holdProbe, "liveState");
                        var nowPos = ReadPos(bridge);
                        var drift = FlatDist(nowPos, anchorText);
                        // Unreadable position counts as drift: re-asserting is
                        // always safe for the hold, skipping is not.
                        var drifted = double.IsNaN(drift) || drift > StageHoldBandM;
                        var busy = holdLive == "Move" && holdState == "Running";
                        if (busy)
                        {
                            await PreemptStopAsync(holdPreemptClient, evidence);
                            holdPreempts++;
                        }
                        if (drifted || busy)
                        {
                            bridge.Call(FormattableString.Invariant(
                                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"place\",\"x\":{anchorX:F1},\"y\":{anchorY:F1},\"z\":{anchorZ:F1}}}"), 60_000);
                            holdReasserts++;
                        }
                    }
                    catch
                    {
                        // A failed re-assert is not a verdict: the tick
                        // predicate and the recorded dist still decide.
                    }
                }

                JsonElement wakeResp;
                try
                {
                    wakeResp = bridge.Call(
                        $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs},\"reporter\":{ReporterNpc3512}}}", 120_000);
                }
                catch
                {
                    Thread.Sleep(2000);
                    continue;
                }
                var stepped = wakeResp.TryGetProperty("stepped", out var stEl) && stEl.ValueKind == JsonValueKind.True;
                JsonElement observe;
                try
                {
                    observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
                }
                catch
                {
                    Thread.Sleep(2000);
                    continue;
                }
                var repObj = GetUInt(observe, "reporterObjId");
                var repDist = GetDbl(observe, "reporterDistM");
                var xCheck = PollNpcObjId(bridge, ReporterNpc3512, 5);
                // The wake's OWN quest-leg evidence, drained from exactly the
                // bytes this wake appended: the executor's decide line decides
                // whether the leg ran at all (and whether it was warming), and
                // the funnel fragment supplies the return-leg arm's reporter.
                // The observe mirror cannot be trusted for tick classification:
                // it is overwritten per quest wake and therefore survives
                // byte-identical across wakes where the leg was skipped (the
                // actor held by another leg), which the frozen run counted as
                // 10 ticks off a single real leg run.
                var (questLegRan, logWarming, logDecide, returnReporter, returnDispatch, returnValidateOk) =
                    DrainWakeEvidence(gameLogCursor, restartLogCursor, ourCharacterId);
                var warming = questLegRan && logWarming;
                var tick = stepped && questLegRan && !warming;
                // Every judged string is the wake's OWN fresh log line; the
                // observe mirror (overwritten per quest wake, so it survives
                // byte-identical across skipped wakes) is never used for the
                // verdict. A wake where the leg did not run at all therefore
                // carries no decide to judge.
                var decideNow = questLegRan ? logDecide : "";
                var withheld = decideNow.StartsWith("WITHHELD:", StringComparison.Ordinal)
                    && decideNow.Contains("TurnInQuest", StringComparison.Ordinal)
                    && decideNow.Contains($"quest {Quest251}", StringComparison.Ordinal);
                var withheldLabel = decideNow.StartsWith("WITHHELD:", StringComparison.Ordinal);
                var detailRep = ParseDetailReporter(decideNow);
                if (!string.IsNullOrEmpty(decideNow) && (decideHistory.Count == 0 || decideHistory[^1] != decideNow))
                    decideHistory.Add(decideNow);
                var armReturnLeg = tick
                    && DecideActionToken(decideNow) is string action && action is "move" or "stop" or "interact"
                    && returnValidateOk && returnReporter != 0 && returnReporter == repObj
                    && string.Equals(returnDispatch, action, StringComparison.OrdinalIgnoreCase);
                var qs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var echoWithhold = observe.TryGetProperty("withholdTurnIn", out var ewh) && ewh.ValueKind == JsonValueKind.True;
                var activity = GetStr(observe, "arbiterActivity");
                if (string.IsNullOrEmpty(activity)) activity = "FIELD-ABSENT";
                var liveAction = GetStr(observe, "liveAction");
                if (string.IsNullOrEmpty(liveAction)) liveAction = "FIELD-ABSENT";
                var liveState = GetStr(observe, "liveState");
                if (string.IsNullOrEmpty(liveState)) liveState = "FIELD-ABSENT";
                var actKey = FormattableString.Invariant($"{activity}/{liveAction}/{liveState}");
                if (!activities.Contains(actKey)) activities.Add(actKey);
                if (tick) holdActive = false;
                var armText = armReturnLeg
                    ? FormattableString.Invariant($"return:{returnDispatch}:{returnReporter}")
                    : withheld ? FormattableString.Invariant($"withheld:{detailRep}") : "none";
                rows.Add(new WakeRow(w, stepped, tick, warming, repObj, repDist, xCheck, withheld, detailRep,
                    qs.Step ?? "", qs.Status ?? "", qs.Active,
                    GetInt(observe, "turnIns"), GetInt64(observe, "money"), decideNow.Length > 300 ? decideNow[..300] : decideNow)
                {
                    WithheldLabel = withheldLabel,
                    ArmReturnLeg = armReturnLeg,
                    ReturnLegReporter = returnReporter,
                    ReturnLegDispatch = returnDispatch,
                });
                evidence.AppendLine(FormattableString.Invariant(
                    $"- wake {w}: stepped={stepped} legRan={questLegRan} tick={tick} warming={warming} reporter={repObj} dist={repDist:0.0}m xcheck={xCheck} withheld={withheld} detailReporter={detailRep} arm=[{armText}] quest={qs.Step}/{qs.Status} active={qs.Active} turnIns={GetInt(observe, "turnIns")} withholdEcho={echoWithhold} hold=re{holdReasserts}/pe{holdPreempts} activity={actKey}"));
                if (rows.Count(r => r.Tick) >= TickQuorum) break;
                if (execSw.Elapsed.TotalSeconds >= WindowSeconds) break;
                if (!tick) Thread.Sleep(PaceMs);
            }
            execSeconds = execSw.Elapsed.TotalSeconds;
            var tickCount = rows.Count(r => r.Tick);
            Leg("gate-wakes", tickCount >= TickQuorum || execSeconds >= WindowSeconds,
                $"observedWakes={rows.Count} ticks={tickCount}/{TickQuorum} window={execSeconds:0.0}s/{WindowSeconds}s hold=re{holdReasserts}/pe{holdPreempts} activities=[{string.Join(",", activities)}]", execSeconds);
            if (rows.Count == 0)
            {
                failBoundary = "HARNESS/observe-gap";
                Assert.Fail("no wakes observed at all; refusing a zero-sample verdict");
            }
            string activityFinding = "n/a (ticks observed)";
            if (tickCount == 0)
            {
                try
                {
                    var actObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
                    var actQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                    activityFinding = FormattableString.Invariant($"activity={GetStr(actObs, "arbiterActivity")} liveAction={GetStr(actObs, "liveAction")} liveState={GetStr(actObs, "liveState")} quest={actQs.Step}/{actQs.Status} active={actQs.Active} reporter={GetUInt(actObs, "reporterObjId")} dist={GetDbl(actObs, "reporterDistM"):0.0}m decide=[{(decideHistory.Count > 0 ? decideHistory[^1] : "none")}]");
                }
                catch (Exception ex)
                {
                    activityFinding = $"UNAVAILABLE ({ex.GetType().Name}: {ex.Message})";
                }
                evidence.AppendLine($"- ACTIVITY-FINDING (0 ticks over {rows.Count} wakes / {execSeconds:0.0}s): {activityFinding} distinctWindowActivities=[{string.Join(",", activities)}]");
            }

            // POST proofs (read-only).
            var endQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            var endObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
            var endTurnIns = GetInt(endObs, "turnIns");
            var endCompleted = GetInt(endObs, "completedCount");
            var endMoney = GetInt64(endObs, "money");
            var endMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            tickRows = rows.Where(r => r.Tick).ToList();
            var landedTurnIns = decideHistory
                .SelectMany(d => Regex.Matches(d, @"landed (TurnInQuest|TurnInDoodad|AutoTurnIn)\b").Cast<Match>())
                .ToList();
            // Foreign dispatches exclude Move/Stop/InteractNpc: those three are
            // exactly the return-leg arbitration's own verbs (G8b), accepted on
            // a tick wake whose validated fragment named the reporter. Any
            // OTHER landed quest/gameplay verb is still foreign here.
            var landedForeign = decideHistory
                .SelectMany(d => Regex.Matches(d, @"landed (Cast|Loot|AcceptQuest|AdvanceQuest|DiscoverQuests)\b").Cast<Match>())
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            var sweepLines = ReadQuestLines(gameLogOffset, gameRestartLogOffset, ourCharacterId, "QuestSweepDiag");
            var sweepWithheld = sweepLines.Count(l => l.Contains("WITHHELD", StringComparison.Ordinal));
            var sweepLandedTurnIn = sweepLines.Count(l => l.Contains("landed TurnInQuest", StringComparison.Ordinal));
            var sweepReporters = sweepLines
                .SelectMany(l => Regex.Matches(l, @"reporter=(\d+)").Cast<Match>())
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            evidence.AppendLine($"- FINAL: quest={endQs.Step}/{endQs.Status} active={endQs.Active} objectives=[{string.Join(",", endQs.Objectives)}]");
            evidence.AppendLine($"- FINAL: turnIns {startTurnIns}->{endTurnIns} completed {startCompleted}->{endCompleted} money {startMoney}->{endMoney} meat {startMeat}->{endMeat}");
            evidence.AppendLine($"- FINAL: ticks={tickRows.Count}/{rows.Count} warming={rows.Count(r => r.Warming)} withheld={rows.Count(r => r.Withheld)}/{rows.Count} withheldArm={tickRows.Count(r => r.Withheld)} returnArm={tickRows.Count(r => r.ArmReturnLeg)} landedTurnIn={landedTurnIns.Count} foreign=[{string.Join(",", landedForeign)}]");
            evidence.AppendLine($"- FINAL: sweepLines={sweepLines.Count} sweepWithheld={sweepWithheld} sweepLandedTurnIn={sweepLandedTurnIn} sweepReporters=[{string.Join(",", sweepReporters)}]");
            evidence.AppendLine($"- FINAL: drift-hold reasserts={holdReasserts} (warming wakes only; ticks={tickRows.Count})");

            // Stability epochs: one ObjId across tick wakes, or re-baselined
            // after a respawn (old ObjId gone at verdict time).
            var epochBase = 0u;
            var distinctLive = new List<uint>();
            foreach (var r in tickRows)
            {
                if (epochBase == 0) { epochBase = r.ReporterObjId; distinctLive.Add(r.ReporterObjId); continue; }
                if (r.ReporterObjId == epochBase) continue;
                var oldLive = ReadNpcHp(bridge, epochBase) >= 0;
                if (oldLive)
                {
                    failBoundary = "REPORTER/flap-replacement";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        wake = r.Wake,
                        fromObjId = epochBase,
                        toObjId = r.ReporterObjId,
                        interpretation = $"ObjId flapped {epochBase}->{r.ReporterObjId} at wake {r.Wake} while the old ObjId is STILL live (no respawn) — GetNpcByTemplateId re-selection is unstable"
                    };
                    verdict = "FAIL-BEHAVIOR/FLAP-REPLACEMENT";
                }
                else
                {
                    respawnEpochs.Add($"wake{r.Wake}:{epochBase}->{r.ReporterObjId}");
                    distinctLive.Add(r.ReporterObjId);
                    epochBase = r.ReporterObjId;
                }
                if (!string.IsNullOrEmpty(failBoundary)) break;
            }
            if (respawnEpochs.Count > 0) stabilityMode = "re-resolved-after-respawn";
            evidence.AppendLine($"- STABILITY: epochs=[{string.Join(" ", distinctLive)}] respawns=[{string.Join(" ", respawnEpochs)}] mode={stabilityMode}");

            if (string.IsNullOrEmpty(failBoundary))
            {
                // DUAL-ARM REPORTER BIND: a tick wake passes when EITHER
                //   (a) the withhold seam named the reporter on the
                //       TurnInQuest-251 proposal (arm withheld: proposal was
                //       built + selected + withheld), OR
                //   (b) the return-leg arbitration fragment named the reporter
                //       with the same dispatch token as the landed return verb
                //       (arm return-leg: the G8b arbitration suppresses the
                //       priority-30 TurnIn for that wake, so no WITHHELD
                //       observable can exist there).
                // BOTH arms must additionally agree with the bridge probe and
                // the drive npcObjId cross-check — one ObjId everywhere.
                var badReporter = tickRows.FirstOrDefault(r =>
                    r.ReporterObjId == 0
                    || r.XCheckObjId != r.ReporterObjId
                    || (r.Withheld ? r.DetailReporter != r.ReporterObjId
                        : !(r.ArmReturnLeg && r.ReturnLegReporter == r.ReporterObjId)));
                if (badReporter != null)
                {
                    failBoundary = "REPORTER/unresolved";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        wake = badReporter.Wake,
                        reporterObjId = badReporter.ReporterObjId,
                        xcheck = badReporter.XCheckObjId,
                        withheldArm = badReporter.Withheld,
                        withheldDetailReporter = badReporter.DetailReporter,
                        returnLegArm = badReporter.ArmReturnLeg,
                        returnLegReporter = badReporter.ReturnLegReporter,
                        returnLegDispatch = badReporter.ReturnLegDispatch,
                        decide = badReporter.Decide,
                        interpretation = "a tick wake named no reporter on EITHER arbitration arm (no WITHHELD TurnInQuest-251 detail and no validated return-leg fragment), or the named reporter disagreed with the bridge probe / drive npcObjId cross-check"
                    };
                    verdict = "FAIL-BEHAVIOR/REPORTER-UNRESOLVED";
                }
            }

            var freshRows = rows.Where(r => !string.IsNullOrEmpty(r.Decide)).ToList();

            if (string.IsNullOrEmpty(failBoundary))
            {
                // A landed dispatch on ANY wake whose own log line rendered a
                // decision is legal ONLY as the return-leg arm's own verb
                // (validated fragment naming the reporter). Every row's Decide
                // is now a fresh per-wake log line, so this scan cannot be
                // satisfied by a stale mirror string — including on a wake the
                // executor stepped without counting a tick.
                var rowLanded = freshRows.FirstOrDefault(r => r.Decide.StartsWith("landed ", StringComparison.Ordinal) && !r.ArmReturnLeg);
                var leakDetail = rowLanded != null ? $"wake {rowLanded.Wake} decide-landed [{rowLanded.Decide}] (no validated return-leg fragment)"
                    : landedTurnIns.Count > 0 ? $"decide-landed TurnIn x{landedTurnIns.Count}"
                    : landedForeign.Count > 0 ? $"foreign dispatch [{string.Join(",", landedForeign)}]"
                    : endTurnIns != startTurnIns ? $"turnIns {startTurnIns}->{endTurnIns}"
                    : endCompleted != startCompleted ? $"completed {startCompleted}->{endCompleted}"
                    : endMoney != startMoney ? $"money {startMoney}->{endMoney}"
                    : sweepLandedTurnIn > 0 ? $"sweep-log landed TurnInQuest x{sweepLandedTurnIn}"
                    : "";
                if (!string.IsNullOrEmpty(leakDetail))
                {
                    failBoundary = "WITHHOLD/leaked-dispatch";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        leak = leakDetail,
                        interpretation = "a landed dispatch no return-leg arm explains (or withheld work / reward growth) during the hold — the withhold seam leaked"
                    };
                    verdict = "FAIL-BEHAVIOR/LEAKED-DISPATCH";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                // The withhold seam fired on a tick wake but withheld something
                // OTHER than the TurnInQuest-251 proposal for our quest (wrong
                // action/quest in the detail) — selection lost while Ready.
                var noProposal = freshRows.FirstOrDefault(r => r.WithheldLabel && !r.Withheld);
                if (noProposal != null)
                {
                    failBoundary = "WITHHOLD/no-proposal";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        wake = noProposal.Wake,
                        decide = noProposal.Decide,
                        interpretation = "quest 251 Ready yet the withhold seam withheld a selection other than the TurnInQuest-251 proposal — selection lost while Ready"
                    };
                    verdict = "FAIL-BEHAVIOR/NO-PROPOSAL";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                var lost = freshRows.FirstOrDefault(r => !r.QuestActive || !IsReady(new E2eQuestDriver.QuestStateSnapshot(r.QuestActive, r.QuestStep, r.QuestStatus, [])));
                if (lost != null || !endQs.Active || !IsReady(endQs))
                {
                    failBoundary = "WITHHOLD/quest-lost";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        wake = lost?.Wake,
                        endStep = endQs.Step,
                        endStatus = endQs.Status,
                        endActive = endQs.Active,
                        interpretation = "quest 251 left Ready/active with no landed turn-in explaining it — fixture destroyed by someone"
                    };
                    verdict = "FAIL-BEHAVIOR/QUEST-LOST";
                }
            }

            // Moving-ticks: once a tick has landed the drift hold is off and
            // post-START walking is allowed and never judged. Distance is
            // recorded per wake (START asserted ~18m once; warm wakes held at
            // the anchor) but drift never fails the gate.
            {
                var minDist = tickRows.Count > 0 ? tickRows.Min(r => r.ReporterDistM) : double.NaN;
                evidence.AppendLine(FormattableString.Invariant($"- DRIFT-NOTE (not judged): minDist={minDist:0.0}m across {tickRows.Count} ticks; warm-wake anchor held x{holdReasserts}, movement after first tick allowed"));
            }

            if (string.IsNullOrEmpty(failBoundary) && tickRows.Count < TickQuorum)
            {
                failBoundary = "HARNESS/no-tick-quorum";
                failingCondition = new
                {
                    boundary = failBoundary,
                    ticks = tickRows.Count,
                    quorum = TickQuorum,
                    observedWakes = rows.Count,
                    windowSeconds = Math.Round(execSeconds, 1),
                    activityFinding,
                    windowActivities = activities,
                    interpretation = "too few wakes ran the quest leg — the observation window proves nothing (arbiter-state finding captured)"
                };
                verdict = "UNKNOWN/HARNESS";
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                passed = true;
                verdict = "PASS-BEHAVIOR";
                failBoundary = "none";
                var withheldTicks = tickRows.Count(r => r.Withheld);
                var returnTicks = tickRows.Count(r => r.ArmReturnLeg);
                claim = $"reporter 3512 resolved {stabilityMode} to ObjId {epochBase} across {tickRows.Count} quest ticks through the dual arbitration arms (WITHHELD TurnInQuest {withheldTicks}/{tickRows.Count}, return-leg Move/Stop/InteractNpc {returnTicks}/{tickRows.Count}; dist {startDist:0.0}m at START inside {HoldRadiusM:0}m sweep, warm-wake anchor held x{holdReasserts}, post-tick movement not judged), quest 251 {endQs.Step}/{endQs.Status} Ready/active, 0 landed TurnInQuest, 0 reward (money {startMoney}->{endMoney})";
                failingCondition = new { boundary = "none" };
            }
            Leg("gate", passed, $"{verdict} at {failBoundary}: reporter={epochBase} ({stabilityMode}) ticks={tickRows.Count}/{rows.Count} quest={endQs.Step}/{endQs.Status}", execSeconds);
            Assert.True(passed, $"G8a {verdict} at {failBoundary}: reporter={epochBase} ticks={tickRows.Count}/{rows.Count} quest={endQs.Step}/{endQs.Status}");
        }
        finally
        {
            totalWall.Stop();
            try { holdPreemptClient?.Dispose(); } catch { }
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
                        reporterNpc = ReporterNpc3512,
                        holdRadiusM = HoldRadiusM,
                        stageDistM = StageDistM,
                        windowSeconds = WindowSeconds,
                        tickQuorum = TickQuorum,
                        start = new
                        {
                            step = startStep, status = startStatus, objectives = startObjectives,
                            reporter = startReporter, distM = startDist,
                            turnIns = startTurnIns, completed = startCompleted, money = startMoney, meat = startMeat
                        },
                        wakes = rows.Select(r => new
                        {
                            r.Wake, r.Stepped, r.Tick, r.Warming,
                            reporter = r.ReporterObjId, distM = Math.Round(r.ReporterDistM, 1), xcheck = r.XCheckObjId,
                            withheld = r.Withheld, detailReporter = r.DetailReporter,
                            returnArm = r.ArmReturnLeg, returnReporter = r.ReturnLegReporter,
                            returnDispatch = r.ReturnLegDispatch != "" ? r.ReturnLegDispatch : null,
                            questStep = r.QuestStep, questStatus = r.QuestStatus, questActive = r.QuestActive,
                            r.TurnIns, r.Money, decide = r.Decide
                        }).ToList(),
                        driftHoldReasserts = holdReasserts,
                        driftHoldPreempts = holdPreempts,
                        withheldArmTicks = tickRows.Count(r => r.Withheld),
                        returnArmTicks = tickRows.Count(r => r.ArmReturnLeg),
                        stabilityMode,
                        respawnEpochs,
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
                        $"{{\"scenario\":\"g8a-resolve\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"G8A-GATE verdict={verdict} boundary={failBoundary} reporterTicks={rows.Count(r => r.Tick)}/{rows.Count} " +
                $"mode={stabilityMode} legs={legs.Count}");
        }
    }

    private static bool IsReady(E2eQuestDriver.QuestStateSnapshot qs)
        => string.Equals(qs.Step, "Ready", StringComparison.Ordinal)
        || string.Equals(qs.Status, "Ready", StringComparison.Ordinal);

    private static uint ParseDetailReporter(string decide)
    {
        var m = Regex.Match(decide, @"reporter=(\d+)");
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>
    /// Selects an ENABLED WebApi backend on the shared port (bounded re-hash:
    /// each fresh client hashes its new connection to one lane). Returns null
    /// when no enabled backend answers — the gate then relies on the
    /// bridge-only pose re-assert for the drift hold, which is why a null here
    /// is recorded, never fatal.
    /// </summary>
    private static async Task<HttpClient?> TryEnsureWebApiClientAsync(StringBuilder evidence)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
            try
            {
                using var response = await client.GetAsync("/api/bots");
                var text = await response.Content.ReadAsStringAsync();
                if (text.Contains("Bot control API is disabled", StringComparison.Ordinal))
                {
                    client.Dispose();
                    await Task.Delay(1000);
                    continue;
                }
                evidence.AppendLine($"- webapi route attempt {attempt}: enabled backend (drift-hold preempt available)");
                return client;
            }
            catch
            {
                client.Dispose();
                await Task.Delay(1000);
            }
        }
        evidence.AppendLine("- webapi route: no enabled backend (drift-hold preempt unavailable; bridge-only pose re-assert still holds)");
        return null;
    }

    /// <summary>
    /// The G8-canonical preempt seam (POST /api/actors/stop): interrupts any
    /// live Move leg owning the actor so it neither walks the bot off the
    /// stage anchor nor starves the quest leg (the executor skips the quest
    /// leg while a Move is live). Best-effort setup only — a failed preempt is
    /// recorded and the tick predicate still decides.
    /// </summary>
    private static async Task PreemptStopAsync(HttpClient? client, StringBuilder evidence)
    {
        if (client == null)
            return;
        try
        {
            using var sc = new StringContent($"{{\"bot\":\"{BotName}\"}}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/actors/stop", sc);
            var body = await response.Content.ReadAsStringAsync();
            evidence.AppendLine($"- drift-hold preempt: POST /api/actors/stop -> {(int)response.StatusCode} {body}");
        }
        catch (Exception ex)
        {
            evidence.AppendLine($"- drift-hold preempt failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Maps a landed decide string to the actor action token the return-leg
    /// arbitration dispatches with ("landed Move (...)" → "move",
    /// "landed Stop (...)" → "stop", "landed InteractNpc (...)" → "interact").
    /// Empty for anything else.
    /// </summary>
    private static string DecideActionToken(string decide)
        => decide.StartsWith("landed Move", StringComparison.Ordinal) ? "move"
            : decide.StartsWith("landed Stop", StringComparison.Ordinal) ? "stop"
            : decide.StartsWith("landed InteractNpc", StringComparison.Ordinal) ? "interact"
            : "";

    /// <summary>
    /// Drains the quest evidence THIS wake appended and classifies it. Two
    /// independent log seams, both emitted once per real quest-leg run:
    /// <list type="bullet">
    /// <item><c>BotRoamStepExecutor - QuestSweepDiag ... decide=[...]</c> —
    /// the executor's own per-wake decide mirror (the same string the observe
    /// surface projects), so a wake is a TICK only when a FRESH line carries a
    /// non-warming decide. The observe mirror alone is stale-safe only while
    /// the leg keeps running; it is overwritten per quest wake and survives
    /// unchanged across wakes where the leg was skipped (actor busy on another
    /// leg), which would otherwise count a stale string as a new tick.</item>
    /// <item><c>QuestObjectiveTargetDiag ... return=[validate=ok:reporter=&lt;objId&gt;:
    /// template=3512:...:dispatch=&lt;token&gt;...]</c> — the return-leg
    /// arbitration's own per-wake reporter resolution (the arm-(b) evidence).</item>
    /// </list>
    /// Only lines appended since the previous call are scanned, so neither
    /// fragment can be borrowed from a neighbouring wake. Read-only; never
    /// dispatches.
    /// </summary>
    private static (bool QuestLegRan, bool Warming, string LogDecide,
        uint ReturnReporter, string ReturnDispatch, bool ReturnValidateOk)
        DrainWakeEvidence(LogCursor gameCursor, LogCursor restartCursor, uint charId)
    {
        var questLegRan = false;
        var warming = false;
        var logDecide = "";
        uint returnReporter = 0;
        var returnDispatch = "";
        var returnValidateOk = false;
        var charTag = FormattableString.Invariant($"char={charId} ");
        foreach (var cursor in new[] { gameCursor, restartCursor })
        {
            foreach (var line in cursor.Drain())
            {
                if (!line.Contains(charTag, StringComparison.Ordinal))
                    continue;
                if (line.Contains("BotRoamStepExecutor - QuestSweepDiag", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"decide=\[(.*)\]\s*$");
                    if (!m.Success)
                        continue;
                    questLegRan = true;
                    logDecide = m.Groups[1].Value.Trim();
                    warming = logDecide.StartsWith("DECIDE:", StringComparison.Ordinal)
                        && logDecide.Contains("sweep-empty", StringComparison.Ordinal);
                    continue;
                }
                if (!line.Contains("QuestObjectiveTargetDiag", StringComparison.Ordinal))
                    continue;
                var rm = Regex.Match(line, @"return=\[([^\]]*)\]");
                if (!rm.Success)
                    continue;
                var fragment = rm.Groups[1].Value;
                if (!fragment.StartsWith("validate=ok", StringComparison.Ordinal)
                    || !fragment.Contains("template=3512", StringComparison.Ordinal))
                    continue;
                var rep = Regex.Match(fragment, @"reporter=(\d+)");
                var disp = Regex.Match(fragment, @":dispatch=([a-z]+)");
                if (!rep.Success || !disp.Success)
                    continue;
                returnValidateOk = true;
                returnReporter = uint.Parse(rep.Groups[1].Value, CultureInfo.InvariantCulture);
                returnDispatch = disp.Groups[1].Value;
            }
        }
        return (questLegRan, warming, logDecide, returnReporter, returnDispatch, returnValidateOk);
    }

    /// <summary>
    /// Incremental, rotation-safe tail reader: remembers how many bytes of a
    /// file were consumed and returns only the lines appended since the last
    /// read (a truncated/rotated file restarts from 0). Used so each quest wake
    /// judges only the log evidence it produced itself.
    /// </summary>
    private sealed class LogCursor(string path)
    {
        private long _offset;

        public List<string> Drain()
        {
            var lines = new List<string>();
            try
            {
                if (!File.Exists(path))
                    return lines;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < _offset)
                    _offset = 0;
                fs.Seek(_offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                    lines.Add(line);
                _offset = fs.Position;
            }
            catch
            {
                // A log read failure is never a behavior verdict.
            }
            return lines;
        }
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

    private static int ReadNpcHp(BotDriveClient bridge, uint objId)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            return el.TryGetProperty("hp", out var v) && v.TryGetInt32(out var n) ? n : -1;
        }
        catch
        {
            return -1;
        }
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

    private static long GetInt64(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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
                Console.WriteLine("[g8a] " + line);
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
                    Console.WriteLine("[g8a] " + kline);
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
        // Adopt-only: the lane bridge is the q0 lane proof (E2E_BRIDGE_PORT).
        // WebApi is UNSET on this lane — never probed, never required.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
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
                scenario = "g8a-resolve",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "turn-in", "return-leg", "rotation", "any other quest" },
                callPath = "bridge quest wake → scheduler Wake → quest leg (QuestBehavior.Run turn-in path: Ready check → Ready-acts reportNpc 3512 → GetNpcByTemplateId per wake → TurnInProposal TurnInQuest at TurnInPriority 30 → BotDecisionSelector → withhold seam Fail(WITHHELD, SelectedAction=TurnInQuest, Request=null)); the G8b return arbitration (QuestBehavior.cs:356-361) gates TurnInProposals behind returnProposal==null || ReturnLegConverged, so a Ready 251 wake whose return leg owns the wake (additive ReturnGoal Move/Stop/InteractNpc outside/inside 25m) SUPPRESSES that TurnIn proposal and its WITHHELD observable — the gate therefore accepts the return-leg arm, joining the wake's own QuestObjectiveTargetDiag return=[validate=ok:reporter=<ObjId>:...:dispatch=<token>] fragment; the gate adds no verbs beyond the canonical preempt Stop and disclosed `quest place` re-assert used by the warming-phase drift hold",
                withholdDependency = "withhold ON is REQUIRED so the Ready fixture survives: without it the first Ready tick would dispatch production GameplayActor.TurnInQuest, complete 251, and destroy the fixture under observation",
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
            Console.WriteLine($"G8A-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
