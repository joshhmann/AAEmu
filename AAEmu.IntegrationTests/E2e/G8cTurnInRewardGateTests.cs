using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// G8c gate (TURN-IN + REWARD, withhold released at START) —
/// fixture = G8b end state: quest 251 held Ready, reporter 3512 resolved +
/// live, bot staged INSIDE the 25 m gate (≤25 m), vitals refilled, identity
/// proof (charId + name), withhold ON through staging. START releases the
/// withhold (existing control OFF) + scheduler wake, then observe-only: the
/// harness issues no TurnIn/Talk/move/mutation — wake + observe + read-only
/// npcState/npcObjId/charPos/inv/mails polls only. The TurnInQuest dispatch
/// observed is the BEHAVIOR's (that is the chain under test), never
/// harness-issued. Fixture credit (accept + stock 4058x3 through the real
/// acquisition fanout + advance to Ready) is setup, staged BEFORE START.
///
/// Production chain under test (QuestBehavior.TurnInProposals →
/// BotDecisionSelector → goal-guarded Dispatch arms → GameplayActor.TurnIn):
/// Ready check on the live quest Status → GetNpcByTemplateId(3512) per wake
/// (never stored) + template bind → TurnInQuest proposal (priority 30, top
/// of the quest ladder) → DispatchTurnIn → GameplayActor.TurnInQuest
/// (resolve preflight only — NO range gate, so the in-range staging is a
/// gate requirement, not an engine requirement) → real packet path
/// (ReportTurnIn → DoReportEvents) → bounded RunCurrentStep drain ≤ 8 →
/// terminal HasQuestCompleted + questcredit ledger marker + Completed
/// detail "quest 251 completed by turn-in" → DistributeRewards (18791×1 to
/// bag, or quest-reward mail when full — Quest.cs:311-322) + 4058×3 cleanup
/// consume + ActiveQuests drop.
///
/// STAGING-INSIDE DEPENDENCY (explicit): withhold ON is REQUIRED through
/// staging so the fixture (Ready + live reporter) survives the place +
/// snapshot wakes — without it the first Ready tick would dispatch the
/// production GameplayActor.TurnInQuest (no range gate) and destroy the
/// Ready fixture before START. Withhold is released ONLY at the G8c START
/// moment via the existing withhold control (OFF), then the scheduler wake
/// owns every decision.
///
/// PASS needs ALL of: reporter stable (same ObjId + template 3512 across
/// wakes) + ≥1 landed TurnInQuest row with the "completed by turn-in"
/// terminal + HasQuestCompleted(251) true + 251 inactive + turnIns +1 +
/// 18791 0→1 in bag OR mail (bag-full-mailed is pass-with-note, not fail)
/// + meat 4058 3→0 (cleanup) + no other landed gameplay verb
/// (Talk/Cast/Loot/Accept/Advance are foreign here).
///
/// FAIL boundaries are predicate-named:
///   REPORTER/unresolved           — a wake with reporterObjId==0, template
///                                   cross-check mismatch, or reporter never
///                                   live at START
///   REPORTER/flap-replacement     — ObjId flapping across wakes while the
///                                   old ObjId is still live (no respawn)
///   REPORTER/despawned            — reporter live at START, 0 mid-window
///   WITHHOLD/release-refused      — withhold OFF refused at START
///   WITHHOLD/still-on             — post-release observe still echoes
///                                   withholdTurnIn (release didn't stick)
///   TURNIN/no-dispatch            — window ends with zero landed
///                                   TurnInQuest rows (proposal never
///                                   dispatched)
///   TURNIN/target-missing         — "turn-in target … not found in world"
///                                   reject (reporter despawned under the leg)
///   TURNIN/busy                   — TurnIn busy-rejected (actor busy
///                                   StateTransition) with no turn-in landing
///   TURNIN/still-active           — turn-in dispatched but quest still
///                                   active at end (drain incomplete)
///   TURNIN/quest-not-completed    — completed flag not set at end
///   REWARD/turnins                — turnIns delta != +1
///   REWARD/missing                — completed but 18791 delta 0 in bag AND
///                                   mail
///   REWARD/cleanup                — meat 4058 not 3→0
///   HARNESS/*                     — setup/staging/quorum races (never
///                                   a behavior verdict)
///
[Collection("e2e")]
public class G8cTurnInRewardGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G8cTurnIn" + Stamp;
    private static readonly string BotAccount = ("g8cturnin" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    private const uint Quest251 = 251;
    private const uint ReporterNpc3512 = 3512;
    private const uint BoarMeat4058 = 4058;
    private const uint Reward18791 = 18791;
    private const int MeatRequired = 3;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;
    private const int WindowSeconds = 180;
    private const int MaxWakes = 24;
    private const int ConfirmWakes = 3;
    private const double StageDistM = 12.0;
    private const double StageFloorM = 2.0;
    private const double StageCapM = 25.0;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "g8c-turnin-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    private sealed record WakeRow(
        int Wake, bool Stepped,
        uint ReporterObjId, uint ReporterTemplate, double ReporterDistM, uint XCheckObjId,
        string QuestStep, string QuestStatus, bool QuestActive,
        int TurnIns, long Money, int Meat, int Reward,
        bool WithholdOn,
        string LiveAction, string LiveState, string Decide);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task TurnInCompletesQuestAndPaysReward()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G8c turn-in+reward gate (Ready 251 + live 3512 staged ≤25m, withhold released at START) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        evidence.AppendLine($"- OBSERVE-ONLY WINDOW: quest-wake window until landed TurnInQuest completed-by-turn-in + {ConfirmWakes} confirming wakes OR {WindowSeconds}s (waitMs={WakeWaitMs / 1000}s); during the window the harness issues NO gameplay verb — wake + observe + read-only npcState/npcObjId/charPos/inv/mails polls only; the observed TurnInQuest dispatch is the BEHAVIOR's turn-in leg (the chain under test); the single pre-START preempt Stop (staging, before teleport-staging: canonical POST /api/actors/stop + bounded 15s idle verify) is the only harness-issued verb; withhold ON is REQUIRED through staging so the Ready fixture survives (released ONLY at START via the existing withhold control OFF, then the scheduler wake owns every decision)");

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
        long gameLogOffset = 0;
        long gameRestartLogOffset = 0;
        var stabilityMode = "same-ObjId";
        var respawnEpochs = new List<string>();
        string preemptOutcome = "NOT-ATTEMPTED";
        string preemptTerminal = "UNTRACKED";
        bool idleConfirmed = false;
        string idleLive = "UNKNOWN";
        string startLiveAction = "UNKNOWN";
        string startLiveState = "UNKNOWN";
        bool startIdle = false;
        var windowActivities = new List<string>();
        int legActiveWakes = 0;
        var cycleLines = new List<string>();
        var freshCycleIds = new List<string>();

        string startStep = "", startStatus = "";
        int[] startObjectives = [];
        uint startReporter = 0;
        double startDist = double.NaN;
        int startTurnIns = 0, startCompleted = 0, startMeat = -1, startReward = -1, startMailReward = -1;
        long startMoney = -1;
        bool startHasCompleted = true;
        string rewardRoute = "UNRESOLVED";

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
                $"charId={ourCharacterId} activeBefore={activeBefore} (must be False; identity proof charId={ourCharacterId} name={BotName})",
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
            // Fresh-boot patience (harness-only): NPC spawn is player-radius
            // driven and a fresh boot's initial spawn sweep is heavy, so the
            // reporter may need longer than the warm-lane 30 s to materialize
            // after the teleport. Retry the teleport mid-window; START
            // criteria (live 3512 + dist band + template) are unchanged.
            var giverObjId = PollNpcObjId(bridge, ReporterNpc3512, 45);
            if (giverObjId == 0)
            {
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{ReporterNpc3512}}}", 60_000);
                giverObjId = PollNpcObjId(bridge, ReporterNpc3512, 45);
            }
            if (giverObjId == 0)
            {
                failBoundary = "HARNESS/reporter-absent";
                Leg("fixture-reporter", false, "reporter 3512 never materialized (objId=0 after 90s poll + teleport retry)", fixSw.Elapsed.TotalSeconds);
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

            // Vitals refill (fixture must not limp into the turn-in).
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

            // PREEMPT (staging, disclosed, ONE shot): clear any live travel
            // Move owning the actor (stale enroll-era QUEST_TRAVEL leg would
            // busy-reject the turn-in dispatch) via the canonical stop seam
            // BEFORE teleport-staging (shared-:1280 hashed route first, G7c
            // pattern), then verify idle via read-only quest-observe
            // liveAction/liveState (bounded 15s). One preempt, then verify —
            // no Stop-hold loops.
            var preemptSw = Stopwatch.StartNew();
            try
            {
                using var http = await EnsureWebApiClientAsync(evidence);
                using var sc = new StringContent($"{{\"bot\":\"{BotName}\"}}", Encoding.UTF8, "application/json");
                using var sresp = await http.PostAsync("/api/actors/stop", sc);
                var sbody = await sresp.Content.ReadAsStringAsync();
                preemptOutcome = FormattableString.Invariant($"POST /api/actors/stop -> {(int)sresp.StatusCode} {sbody}");
                if (sresp.IsSuccessStatusCode)
                {
                    using var sdoc = JsonDocument.Parse(sbody);
                    if (sdoc.RootElement.TryGetProperty("trace_id", out var tidEl) && tidEl.ValueKind == JsonValueKind.String
                        && Guid.TryParse(tidEl.GetString(), out var tid))
                    {
                        var tdead = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                        while (DateTime.UtcNow < tdead)
                        {
                            using var gresp = await http.GetAsync($"/api/actors/actions/{tid}");
                            if (gresp.IsSuccessStatusCode)
                            {
                                var gtext = await gresp.Content.ReadAsStringAsync();
                                using var gdoc = JsonDocument.Parse(gtext);
                                var tstate = gdoc.RootElement.GetProperty("state").GetString() ?? "UNAVAILABLE";
                                if (tstate is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                                {
                                    preemptTerminal = tstate;
                                    break;
                                }
                            }
                            await Task.Delay(300);
                        }
                        preemptOutcome += $" trace={preemptTerminal}";
                    }
                }
            }
            catch (Exception ex)
            {
                preemptOutcome = $"ATTEMPT-FAILED ({ex.GetType().Name}: {ex.Message})";
            }
            evidence.AppendLine($"- preempt: {preemptOutcome}");
            Leg("preempt-stop", true, $"{preemptOutcome} (record-only; idle verified next, START snapshot decides)", preemptSw.Elapsed.TotalSeconds);
            var idleSw = Stopwatch.StartNew();
            var idleDeadline = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < idleDeadline)
            {
                try
                {
                    var idleObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
                    var ia = GetStr(idleObs, "liveAction");
                    var ist = GetStr(idleObs, "liveState");
                    idleLive = $"{(string.IsNullOrEmpty(ia) ? "FIELD-ABSENT" : ia)}/{(string.IsNullOrEmpty(ist) ? "FIELD-ABSENT" : ist)}";
                    if (!(ia == "Move" && ist == "Running"))
                    {
                        idleConfirmed = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    idleLive = $"OBSERVE-FAILED ({ex.GetType().Name})";
                    break;
                }
                Thread.Sleep(1000);
            }
            evidence.AppendLine($"- preempt-idle: idleConfirmed={idleConfirmed} live={idleLive} (REQUIRE no Move/Running within 15s; START snapshot re-asserts)");
            Leg("preempt-idle", idleConfirmed, $"live={idleLive} (record-only; START snapshot re-asserts)", idleSw.Elapsed.TotalSeconds);

            // STAGE (setup, disclosed, in-range START): place once ~12 m from
            // the live reporter (INSIDE the 25 m gate — the G8b end state;
            // TurnIn itself carries no range gate, so this is a gate
            // requirement, not an engine requirement; withhold stays ON so
            // nothing lands), assert ONCE (npcObjId poll + observe flat dist
            // + template), then START immediately. Place-only staging; no
            // gameplay verb fires.
            var stageSw = Stopwatch.StartNew();
            var probe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
            var repX = GetDbl(probe, "reporterX"); var repY = GetDbl(probe, "reporterY"); var repZ = GetDbl(probe, "reporterZ");
            if (double.IsNaN(repX) || double.IsNaN(repY) || double.IsNaN(repZ))
            {
                failBoundary = "HARNESS/reporter-absent";
                Leg("stage-place", false, "reporter position unavailable in observe probe", stageSw.Elapsed.TotalSeconds);
                Assert.Fail("reporter 3512 position unavailable; cannot stage the turn-in");
            }
            var place = bridge.Call(FormattableString.Invariant(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"place\",\"x\":{repX + StageDistM:F1},\"y\":{repY:F1},\"z\":{repZ:F1}}}"), 60_000);
            evidence.AppendLine($"- place +{StageDistM:0}x RESPONSE verbatim: {place}");
            var stageReporter = PollNpcObjId(bridge, ReporterNpc3512);
            var stageObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
            var stageObsReporter = GetUInt(stageObs, "reporterObjId");
            if (stageReporter == 0) stageReporter = stageObsReporter;
            var stageDist = GetDbl(stageObs, "reporterDistM");
            var stageTemplate = GetReporterTemplate(bridge, stageReporter);
            var stageOk = stageReporter != 0 && stageTemplate == ReporterNpc3512
                && stageDist > StageFloorM && stageDist < StageCapM;
            Leg("stage-place", stageOk,
                $"+{StageDistM:0}x: reporter={stageReporter} template={stageTemplate} dist={stageDist:0.0}m (REQUIRE reporter live + template 3512 + {StageFloorM:0}m<dist<{StageCapM:0}m ONCE; in-range START, withhold ON)",
                stageSw.Elapsed.TotalSeconds);
            if (!stageOk)
            {
                failBoundary = "HARNESS/reporter-range";
                Assert.Fail($"in-range START staging failed (reporter={stageReporter} template={stageTemplate} dist={stageDist:0.0}m; REQUIRE live 3512 + {StageFloorM:0}m<dist<{StageCapM:0}m once)");
            }
            // MOVING-START (disclosed): no stillness requirement — post-place
            // movement is behavior evidence, never setup failure. Record pose
            // once, then START immediately.
            var poseSw = Stopwatch.StartNew();
            var startPose = ReadPos(bridge);
            Leg("stage-start", true, $"moving-START pose {startPose} (no stillness REQUIRE; post-START movement is BEHAVIOR EVIDENCE)",
                poseSw.Elapsed.TotalSeconds);

            // WITHHOLD ON (production TurnInQuest must NOT land before START —
            // that is the point of the withhold, not a failure). Armed after
            // staging so no fixture wake runs Ready-unprotected. Released ONLY
            // at the G8c START moment below.
            var withholdResp = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"withhold\",\"on\":true}}", 30_000);
            var withholdArmed = withholdResp.TryGetProperty("withholdTurnIn", out var whEl) && whEl.ValueKind == JsonValueKind.True;
            Leg("withhold-arm", withholdArmed, $"withholdTurnIn={withholdArmed} (REQUIRED through staging: fixture survives only while ON; released ONLY at START)", 0);
            if (!withholdArmed)
            {
                failBoundary = "HARNESS/withhold-refused";
                Assert.Fail("withhold seam refused to arm; refusing to run Ready wakes unprotected");
            }

            gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // START (authoritative, observe-only — no TurnIn/Talk/move from
            // the harness, only the snapshot polls).
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
            startReward = E2eQuestDriver.InvCount(bridge, BotName, Reward18791);
            startMailReward = CountMailReward(bridge);
            startHasCompleted = E2eQuestDriver.HasCompleted(bridge, BotName, Quest251);
            var startTemplate = GetReporterTemplate(bridge, startReporter);
            var startWithhold = startObs.TryGetProperty("withholdTurnIn", out var swh) && swh.ValueKind == JsonValueKind.True;
            evidence.AppendLine($"- START: quest={startStep}/{startStatus} objectives=[{string.Join(",", startObjectives)}] active={startQs.Active} hasCompleted={startHasCompleted} reporter={startReporter} template={startTemplate} dist={startDist:0.0}m turnIns={startTurnIns} completed={startCompleted} money={startMoney} meat={startMeat} reward18791={startReward} mail18791={startMailReward} withhold={startWithhold}");
            startLiveAction = GetStr(startObs, "liveAction");
            if (string.IsNullOrEmpty(startLiveAction)) startLiveAction = "FIELD-ABSENT";
            startLiveState = GetStr(startObs, "liveState");
            if (string.IsNullOrEmpty(startLiveState)) startLiveState = "FIELD-ABSENT";
            startIdle = !(startLiveAction == "Move" && startLiveState == "Running");
            evidence.AppendLine($"- START-IDLE: live={startLiveAction}/{startLiveState} idle={startIdle} (preemptIdle={idleConfirmed} [{idleLive}] terminal={preemptTerminal})");
            var missing = new List<string>();
            if (!(startQs.Active && IsReady(startQs))) missing.Add($"quest-not-ready({startStep}/{startStatus}/active={startQs.Active})");
            if (startReporter == 0 || startTemplate != ReporterNpc3512) missing.Add($"reporter-unresolved(objId={startReporter}/template={startTemplate})");
            if (!(startDist > StageFloorM && startDist < StageCapM)) missing.Add(FormattableString.Invariant($"dist-band({startDist:0.0}m REQUIRE {StageFloorM:0.0}m<dist<{StageCapM:0.0}m)"));
            if (!startWithhold) missing.Add("withhold-off");
            if (!startIdle) missing.Add($"actor-busy(live={startLiveAction}/{startLiveState})");
            if (startHasCompleted) missing.Add("already-completed");
            if (startTurnIns != 0) missing.Add($"turnIns-dirty({startTurnIns})");
            if (startMeat != MeatRequired) missing.Add($"meat-dirty({startMeat} REQUIRE {MeatRequired})");
            // 18791 baselines are RECORDED, never required zero: the character-creation starter kit already carries 18791 (proven: untouched 18792 fellow-traveller on a fresh account), so the gate asserts deltas (bag +1 OR mail +1), G8b-style.
            if (startMailReward < 0) missing.Add($"mail-unreadable({startMailReward})");
            var startOk = missing.Count == 0;
            Leg("gate-start", startOk,
                $"quest={startStep}/{startStatus} reporter={startReporter} template={startTemplate} dist={startDist:0.0}m withhold={startWithhold} live={startLiveAction}/{startLiveState} idle={startIdle} completed={startHasCompleted} turnIns={startTurnIns} meat={startMeat} reward={startReward}/mail={startMailReward}" + (startOk ? "" : $" MISSING[{string.Join(",", missing)}]"), 0);
            if (!startOk)
            {
                failBoundary = "HARNESS/start-unusable";
                Assert.Fail($"START snapshot unusable: missing {string.Join("; ", missing)}");
            }

            // START RELEASE (the G8c START moment, disclosed): withhold OFF
            // via the existing control, then the scheduler wake owns every
            // decision. Recorded; post-release observe echo decides
            // WITHHOLD/still-on.
            var releaseSw = Stopwatch.StartNew();
            var releaseResp = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"withhold\",\"on\":false}}", 30_000);
            var released = releaseResp.TryGetProperty("withholdTurnIn", out var relEl) && relEl.ValueKind == JsonValueKind.False;
            evidence.AppendLine($"- START-RELEASE RESPONSE verbatim: {releaseResp}");
            Leg("withhold-release", released, $"withholdTurnIn=false echoed={released} (the G8c START moment; scheduler wake owns every decision after)", releaseSw.Elapsed.TotalSeconds);
            if (!released)
            {
                failBoundary = "WITHHOLD/release-refused";
                Assert.Fail("withhold seam refused to release at START; refusing an unprotected-but-unreleased window");
            }

            // OBSERVE-ONLY window: until landed TurnInQuest completed-by-turn-in
            // + ConfirmWakes confirming wakes, or WindowSeconds elapse. The
            // harness dispatches NOTHING gameplay-related; the observed
            // TurnInQuest leg is the behavior's turn-in chain. Each wake is a
            // scheduler wake (bridge quest wake: enroll + scheduler.Wake +
            // step wait + observe).
            var execSw = Stopwatch.StartNew();
            var w = 0;
            var turnInWakes = 0;
            var postTurnIn = 0;
            while (true)
            {
                w++;
                if (w > MaxWakes) break;
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
                var decide = GetStr(observe, "questDecideDetail");
                if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
                    decideHistory.Add(decide);
                var repObj = GetUInt(observe, "reporterObjId");
                var repDist = GetDbl(observe, "reporterDistM");
                var repTemplate = GetReporterTemplate(bridge, repObj);
                var xCheck = PollNpcObjId(bridge, ReporterNpc3512, 5);
                var qs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var liveAction = GetStr(observe, "liveAction");
                if (string.IsNullOrEmpty(liveAction)) liveAction = "FIELD-ABSENT";
                var liveState = GetStr(observe, "liveState");
                if (string.IsNullOrEmpty(liveState)) liveState = "FIELD-ABSENT";
                var activity = GetStr(observe, "arbiterActivity");
                if (string.IsNullOrEmpty(activity)) activity = "FIELD-ABSENT";
                var actKey = FormattableString.Invariant($"{activity}/{liveAction}/{liveState}");
                if (!windowActivities.Contains(actKey)) windowActivities.Add(actKey);
                var legLit = observe.TryGetProperty("questLegActive", out var legEl) && legEl.ValueKind == JsonValueKind.True && legEl.GetBoolean();
                if (legLit) legActiveWakes++;
                var withholdEcho = observe.TryGetProperty("withholdTurnIn", out var whe) && whe.ValueKind == JsonValueKind.True;
                var meat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var reward = E2eQuestDriver.InvCount(bridge, BotName, Reward18791);
                rows.Add(new WakeRow(w, stepped, repObj, repTemplate, repDist, xCheck,
                    qs.Step ?? "", qs.Status ?? "", qs.Active,
                    GetInt(observe, "turnIns"), GetInt64(observe, "money"), meat, reward,
                    withholdEcho,
                    liveAction, liveState, decide.Length > 300 ? decide[..300] : decide));
                evidence.AppendLine(FormattableString.Invariant(
                    $"- wake {w}: stepped={stepped} reporter={repObj} template={repTemplate} dist={repDist:0.0}m xcheck={xCheck} quest={qs.Step}/{qs.Status} active={qs.Active} turnIns={GetInt(observe, "turnIns")} meat={meat} reward={reward} withhold={withholdEcho} live={liveAction}/{liveState} activity={actKey} leg={legLit} decide=[{(decide.Length > 200 ? decide[..200] : decide)}]"));
                if (IsTurnInLanded(decide))
                {
                    turnInWakes++;
                    postTurnIn = 0;
                }
                else if (turnInWakes > 0)
                {
                    postTurnIn++;
                }
                if (turnInWakes > 0 && postTurnIn >= ConfirmWakes) break;
                if (execSw.Elapsed.TotalSeconds >= WindowSeconds) break;
            }
            execSeconds = execSw.Elapsed.TotalSeconds;
            var reporterRows = rows.Where(r => r.ReporterObjId != 0).ToList();
            Leg("gate-wakes", rows.Count > 0,
                $"observedWakes={rows.Count} reporterRows={reporterRows.Count}/{rows.Count} turnInWakes={turnInWakes} window={execSeconds:0.0}s/{WindowSeconds}s", execSeconds);
            if (rows.Count == 0)
            {
                failBoundary = "HARNESS/observe-gap";
                Assert.Fail("no wakes observed at all; refusing a zero-sample verdict");
            }

            // POST proofs (read-only).
            var endQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
            var endObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{ReporterNpc3512}}}", 60_000);
            var endTurnIns = GetInt(endObs, "turnIns");
            var endCompleted = GetInt(endObs, "completedCount");
            var endMoney = GetInt64(endObs, "money");
            var endMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var endReward = E2eQuestDriver.InvCount(bridge, BotName, Reward18791);
            var endMailReward = CountMailReward(bridge);
            var endHasCompleted = E2eQuestDriver.HasCompleted(bridge, BotName, Quest251);
            var endWithhold = endObs.TryGetProperty("withholdTurnIn", out var ewh) && ewh.ValueKind == JsonValueKind.True;
            var anyWithholdEcho = rows.Any(r => r.WithholdOn) || endWithhold;
            var landedTurnIn = decideHistory
                .Where(d => d.StartsWith("landed TurnInQuest", StringComparison.Ordinal)).ToList();
            var completedTurnIn = landedTurnIn
                .Where(d => d.Contains("completed by turn-in", StringComparison.Ordinal)).ToList();
            var landedOtherTurnIn = decideHistory
                .SelectMany(d => Regex.Matches(d, @"landed (TurnInDoodad|AutoTurnIn)\b").Cast<Match>())
                .ToList();
            var targetMissingRows = decideHistory
                .Where(d => d.Contains("turn-in target", StringComparison.Ordinal) && d.Contains("not found in world", StringComparison.Ordinal)).ToList();
            var landedForeign = decideHistory
                .SelectMany(d => Regex.Matches(d, @"landed (Talk|Cast|Loot|AcceptQuest|AdvanceQuest|DiscoverQuests)\b").Cast<Match>())
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            var talkRows = decideHistory
                .Where(d => d.Contains("landed Talk", StringComparison.Ordinal)
                    || (d.Contains("Talk", StringComparison.Ordinal) && d.Contains("no quest change", StringComparison.Ordinal))).ToList();
            var busyRows = decideHistory
                .Where(d => d.Contains("actor busy", StringComparison.Ordinal)).ToList();
            cycleLines = ReadQuestLines(gameLogOffset, gameRestartLogOffset, ourCharacterId, "QuestSweepDiag");
            freshCycleIds = cycleLines
                .SelectMany(l => Regex.Matches(l, @"cycle=(\S+)").Cast<Match>())
                .Select(m => m.Groups[1].Value)
                .Distinct().ToList();
            rewardRoute = endReward > startReward ? "bag" : endMailReward > startMailReward ? "mail" : "UNRESOLVED";
            evidence.AppendLine($"- FINAL: quest step={endQs.Step ?? "null"} status={endQs.Status ?? "null"} active={endQs.Active} hasCompleted={endHasCompleted}");
            evidence.AppendLine($"- FINAL: turnIns {startTurnIns}->{endTurnIns} completed {startCompleted}->{endCompleted} money {startMoney}->{endMoney} meat {startMeat}->{endMeat} reward18791 bag {startReward}->{endReward} mail {startMailReward}->{endMailReward} (route={rewardRoute}) withholdEnd={endWithhold}");
            evidence.AppendLine($"- FINAL: landedTurnInQuest={landedTurnIn.Count} completedByTurnIn={completedTurnIn.Count} otherTurnIn={landedOtherTurnIn.Count} targetMissing={targetMissingRows.Count} foreign=[{string.Join(",", landedForeign)}] talkRows={talkRows.Count} busy={busyRows.Count}");

            evidence.AppendLine($"- FINAL: questLegTicks={legActiveWakes}/{rows.Count} freshCycleIds={freshCycleIds.Count} [{string.Join(",", freshCycleIds.Take(10))}] sweepLines={cycleLines.Count} activities=[{string.Join(",", windowActivities)}]");
            evidence.AppendLine($"- FINAL: preempt terminal={preemptTerminal} preemptIdle={idleConfirmed} [{idleLive}] startIdle={startIdle} [{startLiveAction}/{startLiveState}]");
            // Stability epochs: one ObjId + template 3512 across wakes, or
            // re-baselined after a respawn (old ObjId gone at verdict time).
            var epochBase = 0u;
            var distinctLive = new List<uint>();
            foreach (var r in reporterRows)
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
                var badReporter = rows.FirstOrDefault(r => r.ReporterObjId == 0 || r.ReporterTemplate != ReporterNpc3512 || r.XCheckObjId != r.ReporterObjId);
                if (badReporter != null)
                {
                    var despawned = badReporter.ReporterObjId == 0;
                    failBoundary = despawned ? "REPORTER/despawned" : "REPORTER/unresolved";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        wake = badReporter.Wake,
                        reporterObjId = badReporter.ReporterObjId,
                        reporterTemplate = badReporter.ReporterTemplate,
                        xcheck = badReporter.XCheckObjId,
                        decide = badReporter.Decide,
                        interpretation = despawned
                            ? "reporter live at START, 0 mid-window — despawned mid-turn-in (spawner-walk fallback does NOT arm; legal work exists)"
                            : "a wake failed the template-to-ObjId triple-bind (bridge probe / drive npcObjId cross-check / per-wake npcState template)"
                    };
                    verdict = despawned ? "FAIL-BEHAVIOR/REPORTER-DESPAWNED" : "FAIL-BEHAVIOR/REPORTER-UNRESOLVED";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                if (anyWithholdEcho)
                {
                    failBoundary = "WITHHOLD/still-on";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        endWithhold,
                        withholdWakes = rows.Where(r => r.WithholdOn).Select(r => r.Wake).ToList(),
                        interpretation = "post-release observe still echoes withholdTurnIn — the START release did not stick, so any missing turn-in is a withhold failure, never a turn-in verdict"
                    };
                    verdict = "FAIL-BEHAVIOR/WITHHOLD-STILL-ON";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                if (landedTurnIn.Count == 0)
                {
                    var detail = targetMissingRows.Count > 0 ? $"target-missing x{targetMissingRows.Count}"
                        : busyRows.Count > 0 ? $"busy-reject x{busyRows.Count}"
                        : landedForeign.Count > 0 ? $"foreign dispatch [{string.Join(",", landedForeign)}]"
                        : "no TurnInQuest row at all";
                    failBoundary = targetMissingRows.Count > 0 ? "TURNIN/target-missing"
                        : busyRows.Count > 0 && rows.All(r => r.QuestActive) ? "TURNIN/busy" : "TURNIN/no-dispatch";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        detail,
                        decideSample = decideHistory.Take(5).ToList(),
                        questLegTicks = legActiveWakes,
                        freshCycleIds = freshCycleIds.Count,
                        interpretation = "the priority-30 TurnInQuest proposal never dispatched while 251 stood Ready with a live reporter and withhold OFF"
                    };
                    verdict = "FAIL-BEHAVIOR/TURNIN-NO-DISPATCH";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                if (busyRows.Count > 0 && completedTurnIn.Count == 0)
                {
                    failBoundary = "TURNIN/busy";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        rows = busyRows,
                        interpretation = "turn-in dispatch busy-rejected and no completed-by-turn-in terminal ever landed — a live leg was never preempted"
                    };
                    verdict = "FAIL-BEHAVIOR/TURNIN-BUSY";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                if (endQs.Active)
                {
                    failBoundary = "TURNIN/still-active";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        landedTurnIn = landedTurnIn.Count,
                        completedByTurnIn = completedTurnIn.Count,
                        endStep = endQs.Step,
                        endStatus = endQs.Status,
                        interpretation = "turn-in dispatched but quest 251 still active at end — the bounded RunCurrentStep drain never reached the completed-drop terminal"
                    };
                    verdict = "FAIL-BEHAVIOR/TURNIN-STILL-ACTIVE";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                if (!endHasCompleted)
                {
                    failBoundary = "TURNIN/quest-not-completed";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        landedTurnIn = landedTurnIn.Count,
                        completedByTurnIn = completedTurnIn.Count,
                        interpretation = "quest inactive (or turn-in landed) but HasQuestCompleted(251) is false — the terminal flag never set"
                    };
                    verdict = "FAIL-BEHAVIOR/QUEST-NOT-COMPLETED";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                if (endTurnIns - startTurnIns != 1)
                {
                    failBoundary = "REWARD/turnins";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        startTurnIns,
                        endTurnIns,
                        interpretation = $"REQUIRE turnIns {startTurnIns}->{startTurnIns + 1} (exactly one turn-in credit), got {startTurnIns}->{endTurnIns}"
                    };
                    verdict = "FAIL-BEHAVIOR/TURNINS-DELTA";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                var bagDelta = endReward - startReward;
                var mailDelta = endMailReward - startMailReward;
                if (bagDelta < 1 && mailDelta < 1)
                {
                    failBoundary = "REWARD/missing";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        bagDelta,
                        mailDelta,
                        startReward,
                        endReward,
                        startMailReward,
                        endMailReward,
                        interpretation = "completed but 18791 delta 0 in bag AND mail — DistributeRewards never credited the fixed item (bag-full mail fallback checked first)"
                    };
                    verdict = "FAIL-BEHAVIOR/REWARD-MISSING";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                if (!(startMeat == MeatRequired && endMeat == 0))
                {
                    failBoundary = "REWARD/cleanup";
                    failingCondition = new
                    {
                        boundary = failBoundary,
                        startMeat,
                        endMeat,
                        interpretation = $"REQUIRE meat 4058 {MeatRequired}->0 (quest cleanup consume), got {startMeat}->{endMeat}"
                    };
                    verdict = "FAIL-BEHAVIOR/CLEANUP-DELTA";
                }
            }

            if (string.IsNullOrEmpty(failBoundary))
            {
                passed = true;
                verdict = "PASS-BEHAVIOR";
                failBoundary = "none";
                var routeNote = rewardRoute == "mail" ? " (bag-full-mailed pass-with-note)" : "";
                claim = FormattableString.Invariant($"turn-in landed TurnInQuest Completed x{completedTurnIn.Count} (completed by turn-in) at reporter 3512 objId {epochBase} ({stabilityMode}), quest 251 inactive + HasQuestCompleted, turnIns {startTurnIns}->{endTurnIns}, reward 18791 bag {startReward}->{endReward} mail {startMailReward}->{endMailReward} via {rewardRoute}{routeNote}, meat {startMeat}->{endMeat} (cleanup), start dist {startDist:0.0}m");
                failingCondition = new { boundary = "none" };
            }
            Leg("gate", passed, $"{verdict} at {failBoundary}: turnIn={completedTurnIn.Count}/{landedTurnIn.Count} completed={endHasCompleted} active={endQs.Active} turnIns={startTurnIns}->{endTurnIns} reward={startReward}->{endReward}/mail={startMailReward}->{endMailReward} meat={startMeat}->{endMeat}", execSeconds);
            Assert.True(passed, $"G8c {verdict} at {failBoundary}: turnIn={completedTurnIn.Count}/{landedTurnIn.Count} completed={endHasCompleted} active={endQs.Active} reward={startReward}->{endReward}/mail={startMailReward}->{endMailReward} meat={startMeat}->{endMeat}");
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
                        reporterNpc = ReporterNpc3512,
                        stageDistM = StageDistM,
                        windowSeconds = WindowSeconds,
                        withholdReleasedAtStart = true,
                        start = new
                        {
                            step = startStep, status = startStatus, objectives = startObjectives,
                            reporter = startReporter, distM = startDist,
                            turnIns = startTurnIns, completed = startCompleted, money = startMoney,
                            meat = startMeat, reward18791 = startReward, mail18791 = startMailReward,
                            hasCompleted = startHasCompleted,
                            liveAction = startLiveAction, liveState = startLiveState, idle = startIdle
                        },
                        preempt = new
                        {
                            outcome = preemptOutcome, terminal = preemptTerminal,
                            idleConfirmed, idleLive
                        },
                        questLegTicks = legActiveWakes,
                        freshCycleIds,
                        activities = windowActivities,
                        wakes = rows.Select(r => new
                        {
                            r.Wake, r.Stepped,
                            reporter = r.ReporterObjId, reporterTemplate = r.ReporterTemplate,
                            distM = Math.Round(r.ReporterDistM, 1), xcheck = r.XCheckObjId,
                            questStep = r.QuestStep, questStatus = r.QuestStatus, questActive = r.QuestActive,
                            r.TurnIns, r.Money, r.Meat, r.Reward, r.WithholdOn,
                            r.LiveAction, r.LiveState, decide = r.Decide
                        }).ToList(),
                        stabilityMode,
                        respawnEpochs,
                        rewardRoute,
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
                        $"{{\"scenario\":\"g8c-turnin\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"G8C-GATE verdict={verdict} boundary={failBoundary} turnIn={decideHistory.Count(d => d.StartsWith("landed TurnInQuest", StringComparison.Ordinal))} reward={startReward}->mail={startMailReward} legs={legs.Count}");
        }
    }

    private static bool IsReady(E2eQuestDriver.QuestStateSnapshot qs)
        => string.Equals(qs.Step, "Ready", StringComparison.Ordinal)
        || string.Equals(qs.Status, "Ready", StringComparison.Ordinal);

    private static bool IsTurnInLanded(string decide)
        => decide.StartsWith("landed TurnInQuest", StringComparison.Ordinal)
        && decide.Contains("completed by turn-in", StringComparison.Ordinal);

    private static int CountMailReward(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"mails\"}}", 30_000);
            if (!el.TryGetProperty("mails", out var mails) || mails.ValueKind != JsonValueKind.Array)
                return -1;
            var total = 0;
            foreach (var m in mails.EnumerateArray())
            {
                if (!m.TryGetProperty("attachments", out var atts) || atts.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var a in atts.EnumerateArray())
                {
                    if (a.TryGetProperty("templateId", out var t) && t.TryGetUInt32(out var tid) && tid == Reward18791
                        && a.TryGetProperty("count", out var c) && c.TryGetInt32(out var n))
                        total += n;
                }
            }
            return total;
        }
        catch
        {
            return -1;
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

    private static uint GetReporterTemplate(BotDriveClient bridge, uint objId)
    {
        if (objId == 0)
            return 0;
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            return el.TryGetProperty("templateId", out var v) && v.TryGetUInt32(out var n) ? n : 0;
        }
        catch
        {
            return 0;
        }
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
                Console.WriteLine("[g8c] " + line);
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
                    Console.WriteLine("[g8c] " + kline);
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

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    // ---- shared-port route selection (G7c pattern): return the first
    // HttpClient whose backend answers enabled (bounded re-hash; each fresh
    // client hashes its new connection to one lane). Throws when no enabled
    // backend answers — SETUP/webapi-route, never a turn-in verdict.
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
                scenario = "g8c-turnin",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "any other quest", "journey/scale follow-on" },
                callPath = "bridge quest withhold OFF (START release) → bridge quest wake → scheduler Wake → quest leg (production turn-in path: Ready check on live 251 Status → GetNpcByTemplateId(3512) per wake + template bind → TurnInQuest proposal at priority 30 → DispatchTurnIn → GameplayActor.TurnInQuest resolve preflight + ReportTurnIn → DoReportEvents + bounded RunCurrentStep drain ≤8 → HasQuestCompleted + questcredit ledger marker + Completed 'quest 251 completed by turn-in' → DistributeRewards 18791x1 to bag (or quest-reward mail when full) + 4058x3 cleanup + ActiveQuests drop); the gate adds no verbs — wake + observe + read-only npcState/npcObjId/charPos/inv/mails polls only",
                withholdDependency = "withhold ON is REQUIRED through staging so the Ready fixture survives: without it the first Ready tick would dispatch production GameplayActor.TurnInQuest (no range gate) and destroy the fixture under observation. Withhold is released ONLY at the G8c START moment via the existing withhold control OFF, then the scheduler wake owns every decision.",
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
            Console.WriteLine($"G8C-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
