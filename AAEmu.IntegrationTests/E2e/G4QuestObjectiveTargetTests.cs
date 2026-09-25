using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// G4 gate — quest-251 objective→target selection (SELECT ONLY).
///
/// Production chain under test (code-verified 2026-09-20):
///   scheduler wake → BotGoalArbiterStepExecutor → BotRoamStepExecutor.StepQuestLeg
///   → QuestBehavior.Run (per-active-quest proposal path for 251)
///   → QuestObjectiveTargetSelector.Evaluate (pure per-wake: snapshot NearbyNpcObjIds
///     → ParentWorld.GetNpc → TemplateId==3475 + Hp&gt;0 + IsHostileTarget + CanAttack
///     + CanSeeTarget → nearest-first + ObjId tiebreak)
///   → Target proposal (priority 25) → BotDecisionSelector → BotDecisionCycle.Execute
///   → Dispatch → GameplayActor.SetTarget ONLY.
///
/// FIXTURE: fresh bot, level 10, vitals refilled (≥0.95), quest 251 ACTIVE (accepted
/// at the true giver 3512 through the real accept gate), staged at a live 3475
/// spawner (teleportToNpc(3475) + PollNpcObjId proof + standstill settle), bag
/// 4058 &lt; 3, identity proof (snap==actor), route settled. START = one bounded
/// scheduler wake, then observe ONLY.
///
/// PASS: deterministic selected ObjId with template 3475, relevance true,
/// legality true, assigned via SetTarget ("landed Target (targeting X)"), full
/// funnel evidence (game-log QuestObjectiveTargetDiag line + decide detail).
/// FAILs are predicate-named: FAIL-OBJECTIVE / FAIL-SOURCE / FAIL-SWEEP-EMPTY /
/// FAIL-ALL-FILTERED / FAIL-ORDERING / FAIL-ASSIGN / FAIL-STALE.
///
/// STOPS at selection: no travel, no combat, no credit, no turn-in — even on PASS
/// the test issues no cast/loot/move after START.
/// </summary>
[Collection("e2e")]
public class G4QuestObjectiveTargetTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G4ObjTgt" + Stamp;
    private static readonly string BotAccount = ("g4objtgt" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";

    private const uint Quest251 = 251;
    private const uint GiverNpc3512 = 3512;
    private const uint BoarTemplate3475 = 3475;
    private const uint BoarMeat4058 = 4058;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "g4-objective-target-gate-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(EvidenceDir, "game-restart.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task StartObserveOnly_SelectsLive3475ViaSetTarget()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G4 objective→target gate (quest 251 → live 3475, select-only) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        string funnelSummary = "UNAVAILABLE";
        string decideDetailPost = "";
        int meatPre = -1, meatPost = -1;
        uint ourCharacterId = 0;

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

            // ---- ENROLL (per-bot baselines; GLOBAL stepped is contrast-only) ----
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            ourCharacterId = GetUInt(enroll, "id");
            var baselineQuestActions = GetInt(enroll, "questActionCount");
            var baselineWakeSeq = GetSeq(enroll, "wakeSeq");
            var baselineTraceWakeSeq = GetSeq(enroll, "traceWakeSeq");
            var baselineMaxTraceWakeSeq = GetSeq(enroll, "maxTraceWakeSeq");
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("enroll", !activeBefore,
                $"charId={ourCharacterId} baselineQuestActions={baselineQuestActions} activeBefore={activeBefore} (must be False)",
                enrollSw.Elapsed.TotalSeconds);
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest already active after enroll wake; re-run with a fresh account");
            }

            // ---- ROUTE SETTLE (enroll-armed route must hold still) ----
            var settleSw = Stopwatch.StartNew();
            var (settled, settleTrail) = SettleRoute(bridge, 60_000);
            evidence.AppendLine($"- settle trail (2s cadence + 5s no-wake window): {settleTrail}");
            Leg("route-clear", settled, $"settled={settled}", settleSw.Elapsed.TotalSeconds);
            if (!settled)
            {
                failBoundary = "SETUP/route-unsettled";
                Assert.Fail("enroll-armed route would not hold still for staging; refusing to stage into drift");
            }

            // ---- FIXTURE: level + vitals refill (G3 refill-gate pattern) ----
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

            // ---- FIXTURE: hold quest 251 (real accept gate at the true giver) ----
            var holdSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{GiverNpc3512}}}", 60_000);
            var giverObjId = PollNpcObjId(bridge, GiverNpc3512);
            var accept = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"accept\",\"quest\":{Quest251},\"acceptor\":\"Npc\",\"acceptorId\":{GiverNpc3512}}}",
                60_000);
            var accepted = accept.TryGetProperty("accepted", out var accEl) && accEl.GetBoolean();
            var held = accepted && E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("quest-hold", held, $"accept251={accepted} giverObjId={giverObjId} active={E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251)}",
                holdSw.Elapsed.TotalSeconds);
            if (!held)
            {
                failBoundary = "SETUP/accept-251";
                Assert.Fail("quest 251 must be held live at giver 3512 before START");
            }

            // ---- FIXTURE: stage at the boar ground (teleportToNpc lands ON a 3475 spawner) ----
            var stageSw = Stopwatch.StartNew();
            var teleportResp = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{BoarTemplate3475}}}", 60_000);
            var spawnerXyz = teleportResp.TryGetProperty("x", out var stx) && teleportResp.TryGetProperty("y", out var sty) && teleportResp.TryGetProperty("z", out var stz)
                ? FormattableString.Invariant($"{stx.GetDouble():F1},{sty.GetDouble():F1},{stz.GetDouble():F1}") : "ABSENT";
            var spawnerId = teleportResp.TryGetProperty("spawnerId", out var sspEl) ? sspEl.ToString() : "ABSENT";
            var boarObjId = PollNpcObjId(bridge, BoarTemplate3475);
            var charPos = ReadPos(bridge);
            var stagedDist = FlatDist(charPos, spawnerXyz);
            var staged = boarObjId != 0 && !double.IsNaN(stagedDist) && stagedDist <= 45.0;
            Leg("boar-stage", staged,
                $"spawnerId={spawnerId} spawner=[{spawnerXyz}] char=[{charPos}] flat={stagedDist:0.0}m (REQUIRE <=45m) live3475={boarObjId}",
                stageSw.Elapsed.TotalSeconds);
            evidence.AppendLine($"- teleportToNpc(3475) RESPONSE verbatim: {teleportResp}");
            if (!staged)
            {
                failBoundary = "SETUP/boar-unresolved";
                Assert.Fail($"no live 3475 staged within 45m (live3475={boarObjId} flat={stagedDist:0.0}m)");
            }

            // ---- SETTLE at the boar ground (teleport may leave a stale route walking) ----
            var settle2Sw = Stopwatch.StartNew();
            var (settled2, settleTrail2) = SettleRoute(bridge, 45_000);
            evidence.AppendLine($"- boar-ground settle trail: {settleTrail2}");
            Leg("boar-settle", settled2, $"settled={settled2}", settle2Sw.Elapsed.TotalSeconds);
            if (!settled2)
            {
                failBoundary = "SETUP/boar-ground-drift";
                Assert.Fail("bot would not hold still at the boar ground; refusing a drifted START");
            }

            // ---- PRE-START proof (read-only): identity, relevance, baselines ----
            var preSw = Stopwatch.StartNew();
            meatPre = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var preStart = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            var decideDetailPre = GetStr(preStart, "questDecideDetail");
            var questActionPreStart = GetInt(preStart, "questActionCount");
            var wakeSeqPreStart = GetSeq(preStart, "wakeSeq");
            var traceWakeSeqPreStart = GetSeq(preStart, "traceWakeSeq");
            var maxTraceWakeSeqPreStart = GetSeq(preStart, "maxTraceWakeSeq");
            var charPosPreStart = ReadPos(bridge);
            var snapGap = FlatDist(
                FormattableString.Invariant($"{GetDbl(preStart, "x"):F1},{GetDbl(preStart, "y"):F1}"),
                FormattableString.Invariant($"{GetDbl(preStart, "actorX"):F1},{GetDbl(preStart, "actorY"):F1}"));
            var identityOk = !double.IsNaN(snapGap) && snapGap < 0.5;
            var relevantPre = meatPre >= 0 && meatPre < 3;
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            Leg("pre-start", identityOk && relevantPre,
                $"char=[{charPosPreStart}] snapActorGap={snapGap:0.0}m (REQUIRE <0.5) meat4058={meatPre} (REQUIRE <3) active251={E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251)}",
                preSw.Elapsed.TotalSeconds);
            evidence.AppendLine($"- pre-START decide detail: [{decideDetailPre}]");
            if (!identityOk || !relevantPre)
            {
                verdict = "FAIL-STALE";
                failBoundary = !identityOk ? "FAIL-STALE" : "FAIL-ALL-FILTERED";
                failingCondition = new { boundary = failBoundary, snapGap, meatPre, interpretation = !identityOk ? "snap!=actor voids the run, not the code" : "bag already holds 3x4058: relevance false before START" };
                Leg("gate", false, $"pre-START void at {failBoundary}", 0);
                Assert.Fail($"G4 {verdict} at {failBoundary}: identity/relevance void before START");
            }

            // ---- START: one bounded scheduler wake, then observe ONLY ----
            var gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            var gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
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
                    evidence.AppendLine($"- START observe poll failed ({ex.GetType().Name}); keeping wake response");
                    break;
                }
                wakeProven = HasPerBotStepSignal(observe, ourCharacterId, baselineQuestActions, baselineWakeSeq, baselineTraceWakeSeq, baselineMaxTraceWakeSeq);
            }
            Leg("wake", wakeProven, $"per-bot step signal charId={ourCharacterId} [{SummarizeWake(observe)}]", execSw.Elapsed.TotalSeconds);

            // ---- OBSERVE ONLY from here: no gameplay commands after START ----
            decideDetailPost = GetStr(observe, "questDecideDetail");
            evidence.AppendLine($"- START decide detail: [{decideDetailPost}]");
            meatPost = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var funnels = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
            evidence.AppendLine($"- funnel lines for charId={ourCharacterId} in window: {funnels.Count}");
            execSeconds = execSw.Elapsed.TotalSeconds;

            // ---- DERIVED VERDICT (first-zero rule over the §J predicates) ----
            var landed = Regex.Match(decideDetailPost, @"landed Target \(targeting (\d+)\)");
            var landedObjId = landed.Success ? uint.Parse(landed.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (!wakeProven && string.Equals(decideDetailPost, decideDetailPre, StringComparison.Ordinal))
            {
                verdict = "FAIL-STALE";
                failBoundary = "FAIL-STALE";
                failingCondition = new { boundary = failBoundary, interpretation = "no per-bot step signal and decide detail unchanged: wake proof void" };
            }
            else if (funnels.Count == 0)
            {
                verdict = "FAIL-STALE";
                failBoundary = "FAIL-STALE";
                failingCondition = new { boundary = failBoundary, decideDetailPost, interpretation = "wake ran but the 251 selector emitted no funnel for our charId (stale binary or leg never evaluated)" };
            }
            else
            {
                var funnel = funnels[^1];
                funnelSummary = funnel.Summary;
                selectedObjId = funnel.Selected;
                evidence.AppendLine($"- funnel: [{funnel.Summary}]");
                evidence.AppendLine($"- funnel candidates: [{funnel.CandidatesRaw}]");
                if (!funnel.ObjectiveOk)
                {
                    verdict = "FAIL-OBJECTIVE";
                    failBoundary = "FAIL-OBJECTIVE";
                    failingCondition = new { boundary = failBoundary, funnel.Objective, interpretation = "Progress acts of 251 unreadable or act 10473 (4058x3) not resolved" };
                }
                else if (!funnel.SourceOk)
                {
                    verdict = "FAIL-SOURCE";
                    failBoundary = "FAIL-SOURCE";
                    failingCondition = new { boundary = failBoundary, funnel.Source, interpretation = "4058→pack4530→3475 loot link unresolvable in live game data" };
                }
                else if (funnel.Raw == 0)
                {
                    verdict = "FAIL-SWEEP-EMPTY";
                    failBoundary = "FAIL-SWEEP-EMPTY";
                    failingCondition = new { boundary = failBoundary, interpretation = "no candidates in the 25m snapshot at START" };
                }
                else if (funnel.Selected == 0)
                {
                    verdict = "FAIL-ALL-FILTERED";
                    failBoundary = "FAIL-ALL-FILTERED";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        funnel.Raw,
                        funnel.Relevant,
                        funnel.Legal,
                        funnel.Rejects,
                        interpretation = "candidates exist, none relevant+legal — tallies say which half"
                    };
                }
                else if (funnel.Template != BoarTemplate3475)
                {
                    verdict = "FAIL-OBJECTIVE";
                    failBoundary = "FAIL-OBJECTIVE";
                    failingCondition = new { boundary = failBoundary, funnel.Selected, funnel.Template, interpretation = "selected template is not the 251 loot-source template: objective resolution fault" };
                }
                else if (!funnel.Relevance || funnel.Have >= 3)
                {
                    verdict = "FAIL-ALL-FILTERED";
                    failBoundary = "FAIL-ALL-FILTERED";
                    failingCondition = new { boundary = failBoundary, funnel.Relevance, funnel.Have, interpretation = "selection exists but relevance false at START" };
                }
                else if (!FirstLegalIsSelected(funnel))
                {
                    verdict = "FAIL-ORDERING";
                    failBoundary = "FAIL-ORDERING";
                    failingCondition = new { boundary = failBoundary, funnel.Selected, funnel.CandidatesRaw, interpretation = "selected is not the nearest-first + ObjId-tiebreak head of the legal set" };
                }
                else if (landedObjId != funnel.Selected)
                {
                    verdict = "FAIL-ASSIGN";
                    failBoundary = "FAIL-ASSIGN";
                    failingCondition = new { boundary = failBoundary, funnel.Selected, landedObjId, decideDetailPost, interpretation = "selection exists but SetTarget did not land it (rejected or out-competed)" };
                }
                else
                {
                    // Determinism across ticks: second wake must reselect the
                    // same ObjId when the candidate set is unchanged.
                    var wake2 = bridge.Call(
                        $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                    Thread.Sleep(1000);
                    var observe2 = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                    var funnels2 = ReadFunnels(gameLogOffset, gameRestartLogOffset, ourCharacterId);
                    var decide2 = GetStr(observe2, "questDecideDetail");
                    var landed2 = Regex.Match(decide2, @"landed Target \(targeting (\d+)\)");
                    var landedObjId2 = landed2.Success ? uint.Parse(landed2.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    if (funnels2.Count >= 2)
                    {
                        var prev = funnels2[^2];
                        var cur = funnels2[^1];
                        evidence.AppendLine($"- ordering re-tick: prev=[{prev.Summary}] cur=[{cur.Summary}] landed2={landedObjId2}");
                        if (prev.RawObjIds == cur.RawObjIds && cur.Selected != prev.Selected)
                        {
                            verdict = "FAIL-ORDERING";
                            failBoundary = "FAIL-ORDERING";
                            failingCondition = new { boundary = failBoundary, prev = prev.Summary, cur = cur.Summary, interpretation = "identical candidate set, different selection across ticks" };
                        }
                        else if (cur.Selected != funnel.Selected && cur.RawObjIds == funnel.RawObjIds)
                        {
                            verdict = "FAIL-ORDERING";
                            failBoundary = "FAIL-ORDERING";
                            failingCondition = new { boundary = failBoundary, first = funnel.Summary, cur = cur.Summary, interpretation = "re-tick changed the selection over an unchanged candidate set" };
                        }
                    }
                    if (verdict == "UNKNOWN")
                    {
                        verdict = "PASS";
                        failBoundary = "none";
                        passed = true;
                        claim = $"from 251-ACTIVE the gate observes deterministic selected ObjId {funnel.Selected} " +
                            $"(template 3475, relevance true, legality true) assigned via SetTarget, with full funnel evidence — and stops there";
                        failingCondition = new
                        {
                            boundary = "none",
                            selected = funnel.Selected,
                            template = funnel.Template,
                            distanceM = funnel.Dist,
                            relevance = funnel.Relevance,
                            have = funnel.Have,
                            need = funnel.Need,
                            raw = funnel.Raw,
                            relevant = funnel.Relevant,
                            legal = funnel.Legal,
                            rejects = funnel.Rejects,
                            cycle = funnel.Cycle,
                            landedTarget = landedObjId,
                            landedTargetRetick = landedObjId2,
                            meatPre,
                            meatPost
                        };
                    }
                }
            }

            Leg("gate", passed, $"{verdict} at {failBoundary}: selected={selectedObjId} funnel=[{funnelSummary}] decide=[{decideDetailPost}]",
                execSeconds);
            Assert.True(passed, $"G4 {verdict} at {failBoundary}: quest={Quest251} selected={selectedObjId} funnel=[{funnelSummary}] decide=[{decideDetailPost}]");
        }
        finally
        {
            // STOP at selected: no travel/combat/credit/turn-in commands were
            // issued after START by construction (observe-only + one re-tick
            // wake for the ordering proof, which itself only observes).
            totalWall.Stop();
            session?.Dispose();
            var cleanupSummary = TryReleaseBot(laneBridge, BotName, ourCharacterId);
            await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, legs, evidence.ToString(),
                new
                {
                    quest = Quest251,
                    giverNpc = GiverNpc3512,
                    preyTemplate = BoarTemplate3475,
                    preyItem = BoarMeat4058,
                    selectedObjId,
                    funnel = funnelSummary,
                    decideDetailPost,
                    meatPre,
                    meatPost,
                    ourCharacterId,
                    cleanupSummary
                },
                setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
        }
    }

    // ---- funnel evidence (server-side, game log) ----
    private sealed record FunnelLine(
        string Cycle, bool ObjectiveOk, string Objective, bool SourceOk, string Source,
        bool Relevance, int Have, int Need, int Raw, int Relevant, int Legal,
        uint Selected, uint Template, double Dist, string Rejects, string CandidatesRaw, string RawObjIds, string Summary);

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
                // Log read is best-effort; absence yields FAIL-STALE, never a throw.
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
            var rawObjIds = string.Join(",", candidates.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Split(':')[0]));
            static int Int(string v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
            static uint UInt(string v) => uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var selected = Field("selected");
            var template = Field("template");
            // NOTE: "template=" also opens the rejects block; Field takes the
            // first match which is the top-level selection template (rejects
            // use "template=N" only inside rejects=[...], matched later).
            var distRaw = Field("distM");
            return new FunnelLine(
                Field("cycle"), objective.StartsWith("ok:", StringComparison.Ordinal), objective,
                source.StartsWith("ok:", StringComparison.Ordinal), source,
                Field("relevance") == "true", Int(Field("have")), Int(Field("need")),
                Int(Field("raw")), Int(Field("relevant")), Int(Field("legal")),
                selected == "-" ? 0 : UInt(selected), template == "-" ? 0 : UInt(template),
                double.TryParse(distRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
                rejects, candidates, rawObjIds,
                $"cycle={Field("cycle")} objective=[{objective}] source=[{source}] relevance={Field("relevance")} " +
                $"have={Field("have")} need={Field("need")} raw={Field("raw")} relevant={Field("relevant")} legal={Field("legal")} " +
                $"selected={selected} template={(selected == "-" ? "-" : template)} distM={distRaw} rejects=[{rejects}]");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Single-tick ordering proof: the selected ObjId must head the legal set
    /// ordered nearest-first (F1 log distance) with an ObjId tiebreak. When the
    /// F1 head is tied (ambiguous at log precision) the re-tick comparison owns
    /// the ordering verdict instead — never a false FAIL here.
    /// </summary>
    private static bool FirstLegalIsSelected(FunnelLine funnel)
    {
        try
        {
            var rows = funnel.CandidatesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Split(':'))
                .Where(p => p.Length == 6 && p[2] == "1" && p[3] == "1")
                .Select(p => new
                {
                    ObjId = uint.Parse(p[0], CultureInfo.InvariantCulture),
                    Dist = double.Parse(p[4], CultureInfo.InvariantCulture)
                })
                .OrderBy(r => r.Dist)
                .ThenBy(r => r.ObjId)
                .ToList();
            if (rows.Count == 0)
                return false;
            if (rows.Count > 1 && rows[0].Dist == rows[1].Dist)
                return true; // F1 tie at the head: re-tick comparison decides.
            return rows[0].ObjId == funnel.Selected;
        }
        catch
        {
            return false;
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

    private static long GetSeq(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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

    private static string SummarizeWake(JsonElement wake)
    {
        try
        {
            var stepped = wake.TryGetProperty("stepped", out var s) && s.GetBoolean();
            var leg = wake.TryGetProperty("questLegActive", out var l) && l.GetBoolean();
            var reason = wake.TryGetProperty("questTravelReason", out var r) ? r.GetString() ?? "" : "";
            return $"stepped={stepped} questLegActive={leg} accepts={GetInt(wake, "accepts")} activeCount={GetInt(wake, "activeCount")} " +
                $"snap=[{GetDbl(wake, "x"):F1},{GetDbl(wake, "y"):F1}] actor=[{GetDbl(wake, "actorX"):F1},{GetDbl(wake, "actorY"):F1}] reason=[{reason}]";
        }
        catch
        {
            return "UNAVAILABLE (wake shape drift)";
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
                Console.WriteLine("[g4] " + line);
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
                    Console.WriteLine("[g4] " + kline);
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
                scenario = "g4-quest-objective-target",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "navigation-to-prey", "combat", "objective-credit", "turn-in", "kill quest", "any other quest" },
                callPath = "bridge quest wake → manager Spawn/Activate + scheduler Wake → BotGoalArbiterStepExecutor (arbitrate → BotRoamStepExecutor.StepQuestLeg → QuestBehavior.Run per-active-quest 251 path → QuestObjectiveTargetSelector.Evaluate → Target proposal → BotDecisionCycle.Execute → GameplayActor.SetTarget)",
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
