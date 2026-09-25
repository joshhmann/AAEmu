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
/// FIXTURE (verified 2026-09-19, quest_acts linkage authoritative, canonical
/// and runtime compact.sqlite3 md5-identical): quest 251 (LEVEL 2 ∈ [1,9])
/// Start component 383 → QuestActConAcceptNpc detail 77 → giver NPC template
/// 3512. offers(2425)={532,533,534} all level 30 (out-of-band) — the 2425
/// staging used by earlier gates is giver-mismatched for 251. Fresh account
/// (activeBefore must be False), setLevel 10 (Q0-proven accept gates for
/// 251), staged at the 3512 spawner (≤25 m perception), fresh-bot money below
/// the 1000-copper bootstrap threshold (recorded live). Bootstrap flag stays
/// ON (lane has it).
///
/// HARNESS (fixed 2026-09-19; same discipline as G3AutonomousQuestAcceptTests):
/// (1) wake proof is PER-BOT — the bridge `stepped` flag derives from GLOBAL
/// scheduler TotalStepsRun (BotDriveBridge.cs:4381,4405), which stale
/// never-Removed bots from earlier tests can satisfy. This test polls
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
/// Fixture: quest 251, giver 3512, level 10, waitMs 30000, fresh accounts,
/// observe-only after START. No navigation added (teleportToNpc staging is a
/// PRE-LOOP fixture, disclosed).
///
///
/// CLAIM (only, if it passed): the production scheduler autonomously accepts
/// in-range in-band quest 251 for a correctly staged bot through the live
/// actor (QuestBehavior → Talk if 251's flow requires it →
/// GameplayActor.AcceptQuest → quest active).
/// NOT claimed: navigation-to-giver, turn-in, any other quest, any autonomy
/// beyond one accept leg.
/// </summary>
[Collection("e2e")]
public class G3AutonomousQuestAccept3512Tests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G3Auto3512" + Stamp;
    private static readonly string BotAccount = ("g3auto3512" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    private const uint Quest251 = 251;
    private const uint GiverNpc3512 = 3512;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;

    private const string QuestProgressActivity = "quest.progress";

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "g3-quest-autonomy-3512-report.json");
    private static string ArbiterReportPath => Path.Combine(E2eStack.E2eRoot, "logs", "g3-arbiter-gate-report.json");
    private static string RefillReportPath => Path.Combine(E2eStack.E2eRoot, "logs", "g3-refill-gate-report.json");
    [Fact]
    [Trait("Category", "e2e")]
    public async Task SchedulerWake_StagedAtTrueGiver_AcceptsViaLiveActor()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G3 quest-autonomy gate (true giver 3512) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        string teleportRespRaw = "UNAVAILABLE";
        string teleportSpawnerId = "UNAVAILABLE";
        string teleportXyz = "UNAVAILABLE";
        string teleportAtUtc = "UNAVAILABLE";
        string charPosImmediate = "UNAVAILABLE";
        string charPosImmediateAtUtc = "UNAVAILABLE";
        string charPos5s = "UNAVAILABLE";
        string charPos5sAtUtc = "UNAVAILABLE";
        string wakeSummary = "UNAVAILABLE", auditSummary = "UNAVAILABLE";
        string cleanupSummary = "UNAVAILABLE";
        string bootstrapSummary = "UNAVAILABLE";
        uint ourCharacterId = 0;
        int baselineQuestActions = 0;
        long baselineWakeSeq = 0, baselineTraceWakeSeq = 0, baselineMaxTraceWakeSeq = 0;
        string gateVerdict = "UNKNOWN";
        string gateBoundary = "";
        string routeReasonPre = "UNAVAILABLE", routeTargetPre = "UNAVAILABLE", decideDetailPre = "", stopOutcome = "UNAVAILABLE";
        bool routeSettled = false;
        string charPosPreStart = "UNAVAILABLE";
        double startDistM = double.NaN;
        string decideDetailPost = "";
        bool gateWakeProven = false;
        int questActionPreStart = 0;
        long wakeSeqPreStart = 0, traceWakeSeqPreStart = 0, maxTraceWakeSeqPreStart = 0;
        string activityEnroll = "UNOBSERVED", activitySettled = "UNOBSERVED", activityPreStart = "UNOBSERVED";
        string activityWakeFirst = "UNOBSERVED", activityStartLast = "UNOBSERVED";
        bool activityEverProgress = false;
        long moneyPreStart = -1, moneyStart = -1;
        int activePreStart = -1, activeStart = -1;
        string arbiterGateVerdict = "UNKNOWN";
        string arbiterGateBoundary = "";
        object? arbiterCondition = null;
        string refillHpRaw = "UNAVAILABLE", refillMpRaw = "UNAVAILABLE", refillStateRaw = "UNAVAILABLE";
        double hpFracPreStart = double.NaN, mpFracPreStart = double.NaN;
        bool refillOk = false;

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
            activityEnroll = GetActivity(enroll);
            evidence.AppendLine($"- arbiter activity post-enroll: [{activityEnroll}]");
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("enroll", !activeBefore,
                $"charId={ourCharacterId} baselineQuestActions={baselineQuestActions} baselineWakeSeq={baselineWakeSeq} baselineTraceWakeSeq={baselineTraceWakeSeq} steppedGlobal={enrollSteppedGlobal}(contrast-only) activeBefore={activeBefore} (must be False)",
                enrollSw.Elapsed.TotalSeconds);
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest already active after enroll wake; re-run with a fresh account");
            }

            // ---- PRE-STAGE (stable-start gate, test-file-only): still the enroll-armed route ----
            // (1) Identify the live quest-travel route from a read-only quest observe.
            // (2) Attempt the EXISTING canonical stop seam POST /api/actors/stop
            //     (BotActionController.Stop -> BotActionCommandQueue -> the executor's
            //     live GameplayActor.Stop; same actor the route pump drives). No new
            //     API, no direct state mutation.
            // (3) Settle poll (read-only charPos): require standstill, then a no-wake
            //     window proving the route will not immediately re-arm during staging.
            var preStageSw = Stopwatch.StartNew();
            try
            {
                var pre = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                routeReasonPre = GetStr(pre, "questTravelReason");
                routeTargetPre = pre.TryGetProperty("questTravelTargetX", out var rtxEl) && rtxEl.ValueKind == JsonValueKind.Number
                    && pre.TryGetProperty("questTravelTargetY", out var rtyEl) && rtyEl.ValueKind == JsonValueKind.Number
                    ? FormattableString.Invariant($"{rtxEl.GetDouble():F0},{rtyEl.GetDouble():F0}") : "ABSENT";
                decideDetailPre = GetStr(pre, "questDecideDetail");
            }
            catch (Exception ex)
            {
                routeReasonPre = $"OBSERVE-FAILED ({ex.GetType().Name})";
            }
            evidence.AppendLine($"- pre-stage route: reason=[{routeReasonPre}] target=[{routeTargetPre}] decide=[{decideDetailPre}]");
            if (!string.IsNullOrEmpty(routeReasonPre) && !routeReasonPre.StartsWith("OBSERVE-FAILED", StringComparison.Ordinal))
            {
                try
                {
                    using var http = NewClient();
                    using var sc = new StringContent($"{{\"bot\":\"{BotName}\"}}", Encoding.UTF8, "application/json");
                    using var sresp = await http.PostAsync("/api/actors/stop", sc);
                    var sbody = await sresp.Content.ReadAsStringAsync();
                    stopOutcome = FormattableString.Invariant($"POST /api/actors/stop -> {(int)sresp.StatusCode} {sbody}");
                    if (sresp.IsSuccessStatusCode)
                    {
                        using var sdoc = JsonDocument.Parse(sbody);
                        if (sdoc.RootElement.TryGetProperty("trace_id", out var tidEl) && tidEl.ValueKind == JsonValueKind.String
                            && Guid.TryParse(tidEl.GetString(), out var tid))
                        {
                            var tstate = "UNAVAILABLE";
                            var tdead = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                            while (DateTime.UtcNow < tdead)
                            {
                                using var gresp = await http.GetAsync($"/api/actors/actions/{tid}");
                                if (gresp.IsSuccessStatusCode)
                                {
                                    var gtext = await gresp.Content.ReadAsStringAsync();
                                    using var gdoc = JsonDocument.Parse(gtext);
                                    tstate = gdoc.RootElement.GetProperty("state").GetString() ?? "UNAVAILABLE";
                                    if (tstate is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                                        break;
                                }
                                await Task.Delay(300);
                            }
                            stopOutcome += $" trace={tstate}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    stopOutcome = $"ATTEMPT-FAILED ({ex.GetType().Name}: {ex.Message})";
                }
            }
            else
            {
                stopOutcome = "SKIPPED (no live route reason to clear)";
            }
            evidence.AppendLine($"- route-clear seam: {stopOutcome}");
            var settleTrail = new StringBuilder(ReadPos(bridge));
            var sPrev = settleTrail.ToString();
            var settledNow = false;
            var settleDeadline = Environment.TickCount64 + 60_000;
            while (Environment.TickCount64 < settleDeadline)
            {
                Thread.Sleep(2000);
                var sCur = ReadPos(bridge);
                settleTrail.Append(" -> ").Append(sCur);
                if (FlatDist(sPrev, sCur) < 0.5)
                {
                    settledNow = true;
                    sPrev = sCur;
                    break;
                }
                sPrev = sCur;
            }
            var rearmed = false;
            if (settledNow)
            {
                Thread.Sleep(5000);
                var sConf = ReadPos(bridge);
                settleTrail.Append(" -> ").Append(sConf);
                rearmed = !(FlatDist(sPrev, sConf) < 1.0);
            }
            routeSettled = settledNow && !rearmed;
            // Arbiter-gate sample: CURRENT yielded activity once the route has
            // stilled (read-only observe; no wake, no mutation).
            try
            {
                activitySettled = GetActivity(bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000));
            }
            catch (Exception ex)
            {
                activitySettled = $"OBSERVE-FAILED ({ex.GetType().Name})";
            }
            evidence.AppendLine($"- arbiter activity post-settle: [{activitySettled}]");
            evidence.AppendLine($"- settle trail (2s cadence, then 5s no-wake window): {settleTrail}");
            Leg("route-clear", routeSettled,
                $"reason=[{routeReasonPre}] target=[{routeTargetPre}] seam=[{stopOutcome}] settled={settledNow} rearmed={rearmed}",
                preStageSw.Elapsed.TotalSeconds);
            if (!routeSettled)
            {
                verdict = "FAIL-SETUP";
                failBoundary = rearmed ? "SETUP/route-rearms" : "SETUP/route-unsettled";
                gateVerdict = "FAIL-SETUP";
                gateBoundary = failBoundary;
                Assert.Fail($"enroll-armed quest-travel route would not hold still for staging (settled={settledNow} rearmed={rearmed}); refusing to stage into drift");
            }
            // ---- FIXTURE (PRE-START, disclosed): level 10 + stage at giver 3512 ----
            var fixSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{FixtureLevel}}}", 30_000);
            // ---- FIXTURE REFILL (refill-gate, test-file-only, EXISTING paths only) ----
            // setLevel writes Level only (BotDriveBridge.cs:833-835) while MaxHp/MaxMp
            // are level-derived computed getters (Character.cs:488-510), so Hp/Mp stay
            // stale and the fraction drops below the OutOfCombatRecovery(85) trigger,
            // which outranks QuestBootstrap(58). Refill through EXISTING bridge ops
            // (no new game API): `mail stock` (normal acquisition path,
            // BotDriveBridge.cs:3304-3320) + `mail use` (the REAL GameplayActor.UseItem
            // SkillItem contract path, BotDriveBridge.cs:3282-3303).
            //   HP: item 8518 (2단계 치유 물약, fixed 2900 heal, skill 11718, 90s cd,
            //       items.level_requirement=0 — usable at fixture level 10).
            //   MP: item 8519 (fixed 1750 mana, skill 11719, 45s cd, level_requirement=0).
            // (Amounts verified against the canonical Data/compact.sqlite3:
            // heal_effects 80 (2900) via skill 11718; restore_mana_effects 11 (1750)
            // via skill 11719; items 8518/8519 level_requirement=0.)
            var refillSw = Stopwatch.StartNew();
            try
            {
                bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"itemTemplate\":8518,\"count\":1}}", 30_000);
                var useHp = bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"use\",\"itemTemplate\":8518}}", 30_000);
                refillHpRaw = useHp.ToString();
                Thread.Sleep(2000);
                bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"itemTemplate\":8519,\"count\":1}}", 30_000);
                var useMp = bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"use\",\"itemTemplate\":8519}}", 30_000);
                refillMpRaw = useMp.ToString();
            }
            catch (Exception ex)
            {
                refillStateRaw = $"REFILL-CALL-FAILED ({ex.GetType().Name}: {ex.Message})";
            }
            evidence.AppendLine($"- refill HP use response: [{refillHpRaw}]");
            evidence.AppendLine($"- refill MP use response: [{refillMpRaw}]");
            // Read-back via charState (drive op, BotDriveBridge.cs:855-868): poll up
            // to 15s for the instant fixed heals to land, then gate BEFORE START.
            var refillDeadline = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < refillDeadline)
            {
                try
                {
                    var cs = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}", 30_000);
                    refillStateRaw = cs.ToString();
                    var hp = GetInt(cs, "hp"); var maxHp = GetInt(cs, "maxHp");
                    var mp = GetInt(cs, "mp"); var maxMp = GetInt(cs, "maxMp");
                    if (maxHp > 0) hpFracPreStart = (double)hp / maxHp;
                    if (maxMp > 0) mpFracPreStart = (double)mp / maxMp;
                    if (hpFracPreStart >= 0.95 && mpFracPreStart >= 0.95)
                        break;
                }
                catch (Exception ex)
                {
                    refillStateRaw = $"CHARSTATE-FAILED ({ex.GetType().Name})";
                    break;
                }
                Thread.Sleep(1000);
            }
            refillOk = hpFracPreStart >= 0.95 && mpFracPreStart >= 0.95;
            evidence.AppendLine(FormattableString.Invariant($"- refill read-back: charState=[{refillStateRaw}] hpFrac={hpFracPreStart:0.000} mpFrac={mpFracPreStart:0.000} (REQUIRE both >=0.95) refillOk={refillOk}"));
            Leg("fixture-refill", refillOk,
                $"hpFrac={hpFracPreStart:0.000} mpFrac={mpFracPreStart:0.000} hpUse=[{refillHpRaw}] mpUse=[{refillMpRaw}]",
                refillSw.Elapsed.TotalSeconds);
            if (!refillOk)
            {
                verdict = "FAIL-SETUP";
                failBoundary = "SETUP/refill-ineffective";
                gateVerdict = "FAIL-SETUP";
                gateBoundary = failBoundary;
                setupSeconds = setupSw.Elapsed.TotalSeconds;
                Assert.Fail($"fixture refill ineffective (hpFrac={hpFracPreStart:0.000} mpFrac={mpFracPreStart:0.000}, require both >=0.95); refusing a recovery-triggered START");
            }
            var teleportResp = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{GiverNpc3512}}}", 60_000);
            teleportRespRaw = teleportResp.ToString();
            teleportAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            teleportSpawnerId = teleportResp.TryGetProperty("spawnerId", out var tspEl) ? tspEl.ToString() : "ABSENT";
            teleportXyz = teleportResp.TryGetProperty("x", out var ttx) && teleportResp.TryGetProperty("y", out var tty) && teleportResp.TryGetProperty("z", out var ttz)
                ? FormattableString.Invariant($"{ttx.GetDouble():F2},{tty.GetDouble():F2},{ttz.GetDouble():F2}") : "ABSENT";
            evidence.AppendLine($"- teleportToNpc(3512) RESPONSE verbatim: {teleportRespRaw} at {teleportAtUtc}Z");
            charPosImmediate = ReadPos(bridge);
            charPosImmediateAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            evidence.AppendLine($"- charPos IMMEDIATE (<=1s post-response): [{charPosImmediate}] at {charPosImmediateAtUtc}Z");
            Thread.Sleep(5000);
            charPos5s = ReadPos(bridge);
            charPos5sAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            evidence.AppendLine($"- charPos +5s: [{charPos5s}] at {charPos5sAtUtc}Z");
            npcObjId = PollNpcObjId(bridge, GiverNpc3512);
            charPos = ReadPos(bridge);
            // ---- PRE-START proof (read-only): position immediately before START + flat range to the staged giver ----
            charPosPreStart = ReadPos(bridge);
            startDistM = FlatDist(charPosPreStart, teleportXyz);
            questActionPreStart = baselineQuestActions;
            wakeSeqPreStart = baselineWakeSeq; traceWakeSeqPreStart = baselineTraceWakeSeq; maxTraceWakeSeqPreStart = baselineMaxTraceWakeSeq;
            try
            {
                var preStart = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                questActionPreStart = GetInt(preStart, "questActionCount");
                wakeSeqPreStart = GetSeq(preStart, "wakeSeq");
                traceWakeSeqPreStart = GetSeq(preStart, "traceWakeSeq");
                maxTraceWakeSeqPreStart = GetSeq(preStart, "maxTraceWakeSeq");
                activityPreStart = GetActivity(preStart);
                moneyPreStart = preStart.TryGetProperty("money", out var mpsEl) && mpsEl.ValueKind == JsonValueKind.Number && mpsEl.TryGetInt64(out var mps) ? mps : -1;
                activePreStart = GetInt(preStart, "activeCount");
                var preStartDecide = GetStr(preStart, "questDecideDetail");
                if (!string.IsNullOrEmpty(preStartDecide))
                    decideDetailPre = preStartDecide;
            }
            catch (Exception ex)
            {
                evidence.AppendLine($"- pre-START observe failed ({ex.GetType().Name}); keeping enroll baselines");
            }
            var startDriftM = FlatDist(charPosImmediate, charPosPreStart);
            var startInRange = !double.IsNaN(startDistM) && startDistM <= 25.0;
            evidence.AppendLine(FormattableString.Invariant($"- START-range proof: preStart=[{charPosPreStart}] spawner=[{teleportXyz}] flat={startDistM:0.0}m (REQUIRE <=25m) drift-since-teleport={startDriftM:0.0}m giverObjId={npcObjId}"));
            Leg("start-range", npcObjId != 0 && startInRange,
                $"preStart=[{charPosPreStart}] spawner=[{teleportXyz}] flat={startDistM:0.0}m drift={startDriftM:0.0}m giverObjId={npcObjId}",
                fixSw.Elapsed.TotalSeconds);
            if (npcObjId != 0 && !startInRange)
            {
                verdict = "FAIL-SETUP";
                failBoundary = "SETUP/start-drifted";
                gateVerdict = "FAIL-SETUP";
                gateBoundary = failBoundary;
                Assert.Fail($"bot drifted out of START range (flat {startDistM:0.0}m > 25m); refusing a decayed START");
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            var staged = npcObjId != 0;
            Leg("fixture-stage", staged,
                $"lvl={FixtureLevel} giverObjId={npcObjId} char=[{charPos}] teleportResp spawnerId={teleportSpawnerId} xyz=[{teleportXyz}] raw=[{teleportRespRaw}] charImmediate=[{charPosImmediate}] char+5s=[{charPos5s}] (staging only; inside 25m perception + band [1,9] preconditions; giver 3512 = verified 251 starter)",
                fixSw.Elapsed.TotalSeconds);
            if (!staged)
            {
                failBoundary = "SETUP/npc-unresolved";
                Assert.Fail($"giver NPC template {GiverNpc3512} never materialized (objId=0 after 30s poll)");
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
            activityWakeFirst = GetActivity(wake);
            activityEverProgress = activityWakeFirst == QuestProgressActivity;
            evidence.AppendLine($"- arbiter activity at START wake response: [{activityWakeFirst}]");
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
                if (GetActivity(observe) == QuestProgressActivity)
                    activityEverProgress = true;
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
            decideDetailPost = observe.TryGetProperty("questDecideDetail", out var ddpEl) ? ddpEl.GetString() ?? "" : "";
            var decideChanged = decideDetailPost.Length > 0 && !string.Equals(decideDetailPost, decideDetailPre, StringComparison.Ordinal);
            var actionsGrew = questActionCount > questActionPreStart;
            var seqGrew = GetSeq(observe, "wakeSeq") > wakeSeqPreStart || GetSeq(observe, "traceWakeSeq") > traceWakeSeqPreStart || GetSeq(observe, "maxTraceWakeSeq") > maxTraceWakeSeqPreStart;
            var legLit = observe.TryGetProperty("questLegActive", out var glEl) && glEl.GetBoolean();
            // Fresh-signal wake proof only: the enroll-armed questTravelReason persists
            // through stop/settle and MUST NOT prove a START quest tick. A START tick
            // overwrites QuestDecideDetail with a new CycleId + staged actor pose.
            gateWakeProven = decideChanged || actionsGrew || seqGrew || legLit;
            evidence.AppendLine($"- START quest-tick proof: decideChanged={decideChanged} actionsGrew={actionsGrew}({questActionPreStart}->{questActionCount}) seqGrew={seqGrew} legLit={legLit} gateWakeProven={gateWakeProven}");
            evidence.AppendLine($"- START decide detail: [{(decideDetailPost.Length <= 400 ? decideDetailPost : decideDetailPost[..400] + "…")}]");
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
            // ---- ARBITER GATE (activity-around-START; derived, never hardcoded) ----
            // Surface: quest-observe `arbiterActivity` (BotDriveBridge projects
            // IBotGoalArbiter.GetActiveActivity; /api/bots/brain is bot-ctrl-gated
            // 404 on this lane). Same 30s budget, no gameplay commands after START.
            activityStartLast = GetActivity(observe);
            moneyStart = observe.TryGetProperty("money", out var msEl) && msEl.ValueKind == JsonValueKind.Number && msEl.TryGetInt64(out var ms) ? ms : -1;
            activeStart = GetInt(observe, "activeCount");
            if (activityStartLast == QuestProgressActivity)
                activityEverProgress = true;
            var progressAtStart = activityEverProgress;
            var activitySurfaceOk = activityEnroll != "FIELD-ABSENT" && activitySettled != "FIELD-ABSENT"
                && activityPreStart != "FIELD-ABSENT" && activityWakeFirst != "FIELD-ABSENT" && activityStartLast != "FIELD-ABSENT";
            var brokeAtStart = moneyStart >= 0 && moneyStart < 1000;
            var stagedAtStart = npcObjId != 0 && startInRange;
            arbiterCondition = null; // reset per run (method-scope for the finally-block report)
            if (progressAtStart && decideChanged)
            {
                arbiterGateVerdict = "PROCEED-DISCOVERY";
                arbiterGateBoundary = "none";
                arbiterCondition = new { boundary = arbiterGateBoundary, activityStartLast, activityWakeFirst, activityEverProgress, decideDetailPost, interpretation = "quest.progress yielded at START and a fresh quest tick ran (decide detail overwritten); discovery funnel owns the next verdict — no accept chase beyond this window" };
            }
            else if (activitySurfaceOk && !progressAtStart && bootstrapOn && brokeAtStart && stagedAtStart)
            {
                arbiterGateVerdict = "FAIL-ARBITRATION";
                arbiterGateBoundary = "ARBITRATION/no-quest-activity";
                arbiterCondition = new
                {
                    boundary = arbiterGateBoundary,
                    activityStartLast,
                    withholdingPredicate = DescribeWithholding(activityStartLast, GetSeq(observe, "wakeSeq") > wakeSeqPreStart),
                    bootstrapEnabledGameSide = bootstrapOn,
                    moneyStart,
                    activeStart,
                    stagedAtStart,
                    questLegActive = legLit,
                    decideChanged,
                    interpretation = "flag ON + broke + staged, yet the arbiter never yielded quest.progress across the START window"
                };
            }
            else
            {
                arbiterGateVerdict = "UNKNOWN";
                arbiterGateBoundary = "HARNESS/wake-unproven-or-activity-unobserved";
                arbiterCondition = new { boundary = arbiterGateBoundary, activitySurfaceOk, progressAtStart, decideChanged, gateWakeProven, bootstrap = bootstrapSummary, moneyStart, activeStart, interpretation = "no activity observable or no fresh quest tick — wake delivery, back to scheduler owner" };
            }
            evidence.AppendLine($"- arbiter activity around START: enroll=[{activityEnroll}] settled=[{activitySettled}] preStart=[{activityPreStart}] wakeFirst=[{activityWakeFirst}] startLast=[{activityStartLast}] everProgress={activityEverProgress} money={moneyStart} active={activeStart} => {arbiterGateVerdict} at {arbiterGateBoundary}");
            Leg("arbiter-around-start", activitySurfaceOk,
                $"{arbiterGateVerdict} at {arbiterGateBoundary}: enroll=[{activityEnroll}] settled=[{activitySettled}] preStart=[{activityPreStart}] wakeFirst=[{activityWakeFirst}] startLast=[{activityStartLast}] everProgress={activityEverProgress} money={moneyStart} active={activeStart}",
                execSw.Elapsed.TotalSeconds);
            var bootstrapProvenOff = bootstrapSource.StartsWith("game-proc-environ", StringComparison.Ordinal) && !bootstrapOn;
            var wakeLegActive = observe.TryGetProperty("questLegActive", out var wlEl) && wlEl.GetBoolean();
            var travelReason = observe.TryGetProperty("questTravelReason", out var trEl) ? trEl.GetString() ?? "" : "";
            var travelDistanceM = MeasureTravelDistance(travelReason);
            var progressHeld = wakeLegActive || travelReason.Length > 0;
            if (passed)
            {
                verdict = "PASS-CANONICAL";
                failBoundary = "none";
                claim = "the production scheduler autonomously accepted in-range in-band quest 251 at true giver 3512 for a staged bot through the executor live actor (direct live-actor AcceptQuest dispatch, NOT via BotActionCommandQueue)";
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
                claim = "the production scheduler autonomously accepts in-range in-band quest 251 at true giver 3512 for a staged bot through the executor live actor (NOT IMPLEMENTED on this lane: bootstrap module off in the GAME process)";
                failingCondition = new
                {
                    file = "AAEmu.Game/Core/Managers/Bots/QuestBootstrapActivityModule.cs",
                    lines = "23-24 (env gate), 68-69 (CanActivate deny), 84-87 (broke/active-work rule)",
                    missing = "AAEMU_QUEST_BOOTSTRAP_ENABLED=1 in the lane game environment",
                    gameSideValue = bootstrapGameValue ?? "<absent>",
                    gameSideSource = bootstrapSource,
                    arbitration = "without quest.progress the arbiter yields presence.roam/idle; StepQuestLeg never runs",
                    perception = "MaxQuestDiscoverRange 25f, band [1,9], quest 251 LEVEL 2 in band, staged at true giver 3512"
                };
            }
            else if (accepts == 0 && questActionCount == baselineQuestActions)
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = progressHeld ? "DISCOVERY/no-legal-proposal" : "ARBITRATION/no-quest-activity";
                claim = "the production scheduler autonomously accepts in-range in-band quest 251 at true giver 3512 for a staged bot through the executor live actor";
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
                    giver = GiverNpc3512,
                    interpretation = progressHeld
                        ? "chain engaged (arbitration yielded quest activity) but discovery produced zero in-band offers, so no Dispatch ran; travel fallback armed instead"
                        : "flag on but the arbiter never yielded a quest activity this wake (no leg, no travel reason)"
                };
            }
            else
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = "FILTERING/leg-dispatched-no-accept";
                claim = "the production scheduler autonomously accepts in-range in-band quest 251 at true giver 3512 for a staged bot through the executor live actor";
                failingCondition = new
                {
                    boundary = failBoundary,
                    accepts,
                    questActionCount,
                    lastAction,
                    lastResult
                };
            }
            // ---- STABLE-START GATE verdict (4-class; derived, never hardcoded) ----
            var firstZeroM = Regex.Match(decideDetailPost, @"firstZero=([a-z-]+)");
            var firstZero = firstZeroM.Success ? firstZeroM.Groups[1].Value : "";
            if (gateVerdict == "UNKNOWN")
            {
                if (!gateWakeProven)
                {
                    gateVerdict = "UNKNOWN";
                    gateBoundary = "HARNESS/wake-unproven";
                }
                else if (passed)
                {
                    gateVerdict = "PASS-BEHAVIOR";
                    gateBoundary = "none";
                }
                else
                {
                    gateVerdict = "FAIL-BEHAVIOR";
                    gateBoundary = firstZero switch
                    {
                        "sweep-empty" => "DISCOVERY/sweep-empty",
                        "no-offerings" => "DISCOVERY/no-offerings",
                        "all-filtered" => "FILTERING/all-filtered",
                        _ when decideDetailPost.Contains("landed", StringComparison.Ordinal) => "FILTERING/landed-non-accept",
                        _ when accepts > 0 || questActionCount > questActionPreStart => "FILTERING/leg-dispatched-no-accept",
                        _ => "ARBITRATION/no-quest-tick"
                    };
                }
            }
            Leg("stable-start-gate", gateVerdict == "PASS-BEHAVIOR",
                $"{gateVerdict} at {gateBoundary}: routeSettled={routeSettled} startFlat={startDistM:0.0}m gateWake={gateWakeProven} firstZero=[{firstZero}] accepts={accepts} questActions={questActionCount} active251={activeAfter} detail=[{(decideDetailPost.Length <= 200 ? decideDetailPost : decideDetailPost[..200] + "…")}]",
                execSw.Elapsed.TotalSeconds);
            Leg("autonomous-accept", passed,
                $"{verdict} active={activeAfter} accepts={accepts} questActions={questActionCount} last={lastAction}/{lastResult} [{auditSummary}]",
                execSw.Elapsed.TotalSeconds);
            Assert.True(passed,
                $"G3-3512 {verdict} at {failBoundary}: quest={Quest251} " +
                $"giver={GiverNpc3512}/{npcObjId} char=[{charPos}] active={activeAfter} [{wakeSummary}] [{auditSummary}]");
        }
        finally
        {
            totalWall.Stop();
            session?.Dispose();
            cleanupSummary = TryReleaseBot(laneBridge, BotName, ourCharacterId);
            await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, legs, evidence.ToString(),
                new { quest = Quest251, giverNpc = GiverNpc3512, npcObjId, charPos, teleportRespRaw, teleportSpawnerId, teleportXyz, teleportAtUtc, charPosImmediate, charPosImmediateAtUtc, charPos5s, charPos5sAtUtc, wakeSummary, auditSummary, bootstrapSummary, cleanupSummary, gateVerdict, gateBoundary, routeReasonPre, routeTargetPre, decideDetailPre, stopOutcome, routeSettled, charPosPreStart, startDistM, decideDetailPost, gateWakeProven, questActionPreStart, activityEnroll, activitySettled, activityPreStart, activityWakeFirst, activityStartLast, activityEverProgress, moneyPreStart, moneyStart, activePreStart, activeStart, arbiterGateVerdict, arbiterGateBoundary, arbiterCondition, refillHpRaw, refillMpRaw, refillStateRaw, hpFracPreStart, mpFracPreStart, refillOk },
                setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds, "g3-refill-gate", RefillReportPath);
        }
    }

    // ---- helpers (adopt-only; no enqueue shapes needed — observe-only gate) ----
    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    /// <summary>
    /// Arbiter-yield read: quest-observe `arbiterActivity` (BotDriveBridge
    /// projects IBotGoalArbiter.GetActiveActivity — the same per-bot memory
    /// /api/bots/brain serves when bot-ctrl is enabled, 404 on this lane).
    /// FIELD-ABSENT = pre-change binary still serving (stale lane).
    /// </summary>
    private static string GetActivity(JsonElement el)
    {
        if (!el.TryGetProperty("arbiterActivity", out var v))
            return "FIELD-ABSENT";
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrEmpty(v.GetString()) ? "(empty)" : v.GetString()!,
            JsonValueKind.Null => "(none)",
            _ => $"UNEXPECTED({v.ValueKind})",
        };
    }

    /// <summary>
    /// Names the withholding predicate from code + observed state. Modules run
    /// highest-priority-first (Schedules 100 > Recovery 85 > Conflict 75 >
    /// Fishing/Homestead 60 > QuestBootstrap 58 > NeedsFarm 55 > PresenceRoam
    /// 50 > Idle 0); the first Allow wins, lower modules are never consulted.
    /// </summary>
    private static string DescribeWithholding(string activity, bool wakeRanSinceStage)
    {
        return activity switch
        {
            "recovery.rest" or "recovery.consume" => "OutOfCombatRecovery(85) ALLOWED over QuestBootstrap(58): trigger = already-recovering latch (sit/active) OR HP<0.75/MP<0.60 (OutOfCombatRecoveryModule.cs:459-478); quest.progress never consulted (first-allow-wins). HP/MP/stance unobserved on quest-observe",
            "presence.roam" => "PresenceRoam(50) won => QuestBootstrap(58).CanActivate DENIED (higher priority consulted first): remaining deny candidates with flag ON + broke are dead / in-battle / pressure>=High (QuestBootstrapActivityModule.cs:72-79) — all three unobserved on quest-observe",
            "idle" => "Idle(0) won => EVERY higher module denied incl. QuestBootstrap(58): same deny candidates as presence.roam (dead/in-battle/pressure>=High), unobserved on quest-observe",
            "(none)" => wakeRanSinceStage ? "NoCandidate after a wake ran since staging: same deny set as idle (dead/in-battle/pressure>=High), unobserved on quest-observe" : "memory empty (no arbitration for this bot since boot): no wake reached the decorator for our charId",
            var other => $"higher-or-equal module won with [{other}]: QuestBootstrap(58) denied or outranked — consult ladder (Schedules 100, Recovery 85, Conflict 75, Fishing/Homestead 60)",
        };
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
                Console.WriteLine("[g3-3512] " + line);
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
                    Console.WriteLine("[g3-3512] " + kline);
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
    private async Task WriteReportAsync(bool passed, string verdict, string failBoundary, string claim, object? failingCondition,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText, object fixture,
        double setupSeconds, double execSeconds, double wallSeconds, string scenario = "g3-quest-autonomy-3512", string? reportPath = null)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario,
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
            // A rerun therefore never destroys a prior report. The effective path
            // (reportPath ?? ReportPath) covers the arbiter/refill secondary reports.
            var effectivePath = reportPath ?? ReportPath;
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var stampedPath = Path.Combine(EvidenceDir,
                Path.GetFileNameWithoutExtension(effectivePath) + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
            await File.WriteAllTextAsync(stampedPath, reportJson);
            File.Copy(stampedPath, effectivePath, overwrite: true);
        }
        catch
        {
            // Evidence write is best-effort; the xUnit verdict is authoritative.
        }
    }
}
