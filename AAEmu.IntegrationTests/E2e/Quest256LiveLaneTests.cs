using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// Quest-256 live lane run (test-file only, NO game source changes).
///
/// Quest 256 'Stubborn Jenny' (Lv4, zone 125): accept NPC 3516 -> report NPC
/// 7651, no objectives (START lands straight at Ready), no reward acts.
/// The chain prereq is 255-complete and 255 needs 254-complete (unit_reqs
/// kind 0x1F CompleteQuestContext on the Start components), so setup credits
/// 254 (accept 3515, report 3516) then 255 (accept 3516, gather item 13713,
/// report 3516) through the production accept/gather/advance/report ops
/// BEFORE START. After START the harness issues only quest wake + observe +
/// read-only polls: accept of 256, the ~40m report leg, and the turn-in must
/// all come from the production scheduler. Stop at the first failing leg.
[Collection("e2e")]
public class Quest256LiveLaneTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "Q256Lane" + Stamp;
    private static readonly string BotAccount = ("q256lane" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    private const uint Quest254 = 254;
    private const uint Quest255 = 255;
    private const uint Quest256 = 256;
    private const uint Giver3516 = 3516;
    private const uint Giver3515 = 3515;
    private const uint Reporter7651 = 7651;
    private const uint Item13713 = 13713;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 15000;
    private const int MaxAcceptWakes = 40;
    private const int MaxReportWakes = 60;
    // RERUN-254-FULL (harness-only): per-string cap avoids OOM on 40-wake
    // full records; wakes are NEVER dropped (no TakeLast, no %5 sampling).
    private const int DecideFullCap = 8000;
    private const int DetailCap = 1000;
    // D-6 flush margin: save-pass flush + delayed second DB re-read so a
    // turn-in landing at the window edge is not read as "no row".
    private const int DbFlushDelayMs = 30_000;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "q256-lane-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameErrorLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Error.log");
    private static string ArchiveDir => "/tmp/q256-evidence";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task TalkOnlyDelivery256AcceptTravelReportTurnIn()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# Quest-256 live lane run (single fresh bot {BotName}) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        evidence.AppendLine("- SETUP (harness, disclosed): chain credit 254+255 via production ops. START = staged near giver 3516. Post-START: wake + observe + read-only polls only; accept/report/turn-in must be the scheduler's.");

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
        // RERUN-254-FULL: structured per-wake records for the 254 leg (one
        // entry per wake, never sampled) + phase-boundary quest snapshots.
        var wakeRecords254 = new List<object>();
        var boundarySnapshots = new List<object>();
        var archivedFiles = new List<object>();
        int lastGoodWake254 = 0, firstBadWake254 = 0;
        string giverPos = "UNAVAILABLE", reporterPos = "UNAVAILABLE";
        string acceptLegDetail = "UNOBSERVED", reportLegDetail = "UNOBSERVED", turnInDetail = "UNOBSERVED";
        double reportStartDistM = double.NaN, reportEndDistM = double.NaN;
        uint npcObj3515 = 0, npcObj3516 = 0;
        bool dbCompleted = false;
        string dbDetail = "UNOBSERVED";

        void Leg(string leg, bool ok, string detail, double ms)
        {
            legs.Add((leg, ok, detail, ms));
            evidence.AppendLine($"- [{(ok ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        try
        {
            // ---- ADOPT lane (pre-booted isolated stack; never EnsureUp) ----
            var adoptSw = Stopwatch.StartNew();
            AdoptLaneDbPassword();
            var bridge = new BotDriveClient(E2eStack.BridgePort);
            laneBridge = bridge;
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "isolated lane answering (bridge ping)" : "lane cold — refusing to rebuild",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                failBoundary = "SETUP/lane-down";
                Assert.Fail("isolated q256 lane not answering; refusing to rebuild mid-run");
            }

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

            // ---- SETUP: natural low-level start. NO setLevel (it raises the
            // level-derived MaxHp/MaxMp while Hp/Mp stay stale, latching
            // recovery.rest at 85 over quest 58 forever — the 8519 MP
            // potion's mana effect throws Random.Shared.Next(1750,1750) and
            // MP never lands). NO refill, NO food: at spawn vitals the
            // recovery trigger stays false. Chain credit (254 needs L2,
            // 255 needs L3, 256 needs L2) comes from ONE drive addExp grant
            // through the real Character.AddExp engine path (setup-only
            // leveling op, NOT gameplay).
            var chainSw = Stopwatch.StartNew();
            // Natural leveling through the real AddExp engine path (400 XP
            // reaches L2, 1400 reaches L3; chain needs 254:L2, 255:L3,
            // 256:L2). AddExp promotes Level via the XP table, fires
            // broadcasts + DoOnLevelUpEvents, and — critically — Hp/Mp stay
            // consistent (no stale-maxima latch into recovery.rest).
            E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"addExp\",\"exp\":1500}}");
            var lvAfter = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}", 30_000);
            Leg("fixture-level", true, $"natural AddExp 1500 -> [{lvAfter}] (real engine path; chain needs L2/L3, 256 needs L2)", 0);
            // 255 FIRST at 3516 (accept, gather 13713, advance, scheduler
            // turn-in): 255 needs L3 + 254-complete... but 254 needs L2 only
            // and NO quest prereq (kind 0x01 is Level) — so 254 accepts at L3
            // immediately; its TURN-IN is what the scheduler must serve, and
            // the wake loop below does that. Order: accept 254, wake-drain
            // its turn-in (proves the scheduler serves Ready talk-only
            // turn-ins at 3516), then 255, then 256 is offered.
            npcObj3515 = TeleportResolve(bridge, Giver3515);
            boundarySnapshots.Add(SnapBoundary(bridge, "before-accept-254"));
            var a254 = E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"accept\",\"quest\":{Quest254},\"acceptor\":\"Npc\",\"acceptorId\":{Giver3515}}}");
            var ok254 = a254.TryGetProperty("accepted", out var a254el) && a254el.ValueKind == JsonValueKind.True;
            boundarySnapshots.Add(SnapBoundary(bridge, "after-accept-254"));
            // Advance FIRST (Start -> Progress -> Ready drain), THEN dialogue:
            // the report-act handler subscribes at quest creation, but the
            // step machine only reaches Ready via advance; dialogue after the
            // drain sets CurrentTarget for the evaluation the report fires.
            E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"advance\",\"quest\":{Quest254}}}");
            boundarySnapshots.Add(SnapBoundary(bridge, "after-advance-254"));
            // Scheduler-served travel handoff (harness-only): the live world
            // spawns NPCs only within player radius, so dialogue staging
            // must wait for the PRODUCTION scheduler to walk the bot to 3516
            // and materialize it. Wake + observe with reporter 3516 arms
            // quest travel (same pattern as the 256 report leg below); the
            // single interactNpc dialogue op fires ONLY after observe shows
            // proximity/materialization. No teleport back: if the scheduler
            // never materializes 3516 within budget, STOP here.
            uint travelNpc3516 = 0;
            var travelDistM = double.NaN;
            string travelDecide = "";
            var travelWakes = 0;
            for (var k = 0; k < MaxReportWakes; k++)
            {
                travelWakes = k + 1;
                var travelWake = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs},\"reporter\":{Giver3516}}}", 120_000);
                NoteDecide(travelWake, decideHistory);
                var travelObs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{Giver3516}}}", 60_000);
                NoteDecide(travelObs, decideHistory);
                travelDecide = GetStr(travelObs, "questDecideDetail");
                travelDistM = GetDbl(travelObs, "reporterDistM");
                travelNpc3516 = SafeNpcObjId(bridge, Giver3516);
                if (travelNpc3516 != 0)
                    break;
                if (!double.IsNaN(travelDistM) && travelDistM >= 0 && travelDistM <= 25.0)
                    break;
                if (E2eQuestDriver.HasCompleted(bridge, BotName, Quest254))
                    break;
            }
            // Final confirming poll (read-only): the spawn tick may lag
            // arrival. Still zero (and quest not already completed by the
            // scheduler) means the seam never closed — STOP, no teleport,
            // no dialogue op.
            npcObj3516 = SafeNpcObjId(bridge, Giver3516);
            var travelDone254 = E2eQuestDriver.HasCompleted(bridge, BotName, Quest254);
            boundarySnapshots.Add(SnapBoundary(bridge, "after-travel-254"));
            Leg("chain-254-travel", npcObj3516 != 0 || travelDone254, $"wakes={travelWakes} npcObjId3516={npcObj3516} reporterDistM={travelDistM:0.0} completed={travelDone254} decide=[{Trunc(travelDecide, 200)}] (scheduler-served travel to 3516; no teleport)", chainSw.Elapsed.TotalSeconds);
            if (npcObj3516 == 0 && !travelDone254)
            {
                failBoundary = "SETUP/dialogue-no-materialize";
                claim = "production scheduler materializes 3516 via quest travel before dialogue staging";
                failingCondition = new { boundary = failBoundary, wakes = travelWakes, npcObjId3516 = npcObj3516, reporterDistM = travelDistM, completed254 = travelDone254, lastDecide = Trunc(travelDecide, DecideFullCap), withdraws = ExtractToken(travelDecide, "withdraws="), wakeRecords = wakeRecords254.Count, boundarySnapshots = boundarySnapshots.Count };
                evidence.AppendLine($"- chain-254 travel handoff: scheduler never materialized 3516 in {travelWakes} wakes (reporterDistM={travelDistM:0.0} decide=[{Trunc(travelDecide, DecideFullCap)}]) — no teleport back, no dialogue op issued");
                Assert.Fail($"chain prereq 254 dialogue staging blocked: scheduler never materialized 3516 in {travelWakes} wakes (dist={travelDistM:0.0} decide=[{Trunc(travelDecide, 200)}])");
            }
            string dialogue254 = travelDone254
                ? "(skipped: scheduler completed 254 during travel; no dialogue target needed)"
                : E2eQuestDriver.Call(bridge, BotName,
                    $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"interactNpc\",\"npc\":{Giver3516}}}").ToString();
            evidence.AppendLine($"- chain-254 dialogue staging: [{dialogue254}] (live-actor InteractNpc sets CurrentTarget for the report-act gate; issued only after scheduler travel materialized 3516 in {travelWakes} wakes)");
            boundarySnapshots.Add(SnapBoundary(bridge, "after-dialogue-254"));
            var done254 = WakeUntilCompletedFull(bridge, Quest254, 40, decideHistory, wakeRecords254);
            boundarySnapshots.Add(SnapBoundary(bridge, "after-wakes-254"));
            (lastGoodWake254, firstBadWake254) = DropBoundary(wakeRecords254);
            Leg("chain-254", ok254 && done254, $"accept254@{Giver3515}={ok254} schedulerTurnIn={done254} dialogue=[{dialogue254}] lastGoodWake={lastGoodWake254} firstBadWake={firstBadWake254} records={wakeRecords254.Count}", chainSw.Elapsed.TotalSeconds);
            if (!ok254 || !done254)
            {
                failBoundary = "SETUP/chain-254-drop";
                claim = "chain prereq 254 survives the wake loop (present + Ready until scheduler turn-in)";
                failingCondition = new { boundary = failBoundary, accept254 = ok254, schedulerTurnIn = done254, lastGoodWake = lastGoodWake254, firstBadWake = firstBadWake254, wakeRecords = wakeRecords254.Count };
                Assert.Fail($"chain prereq 254 dropped before turn-in (accept={ok254} schedulerTurnIn={done254} lastGoodWake={lastGoodWake254} firstBadWake={firstBadWake254})");
            }

            // 255: accept at 3516, gather item 13713 x1, advance, dialogue, scheduler turn-in.
            boundarySnapshots.Add(SnapBoundary(bridge, "before-accept-255"));
            var a255 = E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"accept\",\"quest\":{Quest255},\"acceptor\":\"Npc\",\"acceptorId\":{Giver3516}}}");
            var ok255 = a255.TryGetProperty("accepted", out var a255el) && a255el.ValueKind == JsonValueKind.True;
            boundarySnapshots.Add(SnapBoundary(bridge, "after-accept-255"));
            E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"gather\",\"quest\":{Quest255},\"item\":{Item13713},\"count\":1}}");
            E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"advance\",\"quest\":{Quest255}}}");
            boundarySnapshots.Add(SnapBoundary(bridge, "after-advance-255"));
            var dialogue255 = E2eQuestDriver.Call(bridge, BotName,
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"interactNpc\",\"npc\":{Giver3516}}}");
            evidence.AppendLine($"- chain-255 dialogue staging: [{dialogue255}]");
            boundarySnapshots.Add(SnapBoundary(bridge, "after-dialogue-255"));
            var done255 = WakeUntilCompleted(bridge, Quest255, 40, decideHistory);
            boundarySnapshots.Add(SnapBoundary(bridge, "after-wakes-255"));
            Leg("chain-255", ok255 && done255, $"accept255@{Giver3516}={ok255} schedulerTurnIn={done255} dialogue=[{dialogue255}]", chainSw.Elapsed.TotalSeconds);
            if (!ok255 || !done255)
            {
                failBoundary = "SETUP/chain-255";
                claim = "chain prereq 255 credited via disclosed staging + scheduler turn-in";
                failingCondition = new { boundary = failBoundary, accept255 = ok255, schedulerTurnIn = done255 };
                Assert.Fail($"chain prereq 255 could not be credited (accept={ok255} schedulerTurnIn={done255})");
            }

            // ---- START: stage near giver 3516 (natural low-level start area) ----
            // DE-STAGE slice: START TeleportResolve deleted; bot starts where 255 turn-in left it.
            var stageSw = Stopwatch.StartNew();
            giverPos = ReadPos(bridge);
            var giverObj = PollNpcObjId(bridge, Giver3516);
            var preActive = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest256);
            Leg("stage-giver", giverObj != 0 && !preActive, $"pos=[{giverPos}] giverObjId={giverObj} active256={preActive} (must be False)", stageSw.Elapsed.TotalSeconds);
            if (giverObj == 0 || preActive)
            {
                failBoundary = giverObj == 0 ? "SETUP/giver-unresolved" : "SETUP/autonomy-preempted";
                Assert.Fail(giverObj == 0 ? "giver 3516 never materialized" : "quest 256 already active before START");
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- LEG 1: offer-band discovery + autonomous accept (wake loop) ----
            var execSw = Stopwatch.StartNew();
            var acceptSw = Stopwatch.StartNew();
            bool accepted = false;
            string lastDecide = "";
            var acceptWakes = 0;
            for (var k = 0; k < MaxAcceptWakes; k++)
            {
                acceptWakes = k + 1;
                var wake = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                NoteDecide(wake, decideHistory);
                var obs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                NoteDecide(obs, decideHistory);
                var decide = GetStr(obs, "questDecideDetail");
                lastDecide = decide;
                if (E2eQuestDriver.IsQuestActive(bridge, BotName, Quest256)) { accepted = true; break; }
                if (E2eQuestDriver.HasCompleted(bridge, BotName, Quest256)) break;
            }
            var qsAccept = E2eQuestDriver.QuestState(bridge, BotName, Quest256);
            acceptLegDetail = $"wakes={acceptWakes} active={qsAccept.Active} step={qsAccept.Step} status={qsAccept.Status} decide=[{Trunc(lastDecide, 200)}]";
            Leg("accept-leg", accepted, acceptLegDetail, acceptSw.Elapsed.TotalSeconds);
            if (!accepted)
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = E2eQuestDriver.HasCompleted(bridge, BotName, Quest256) ? "ACCEPT/already-completed" : "ACCEPT/no-autonomous-accept";
                claim = "production scheduler autonomously accepts in-range in-band quest 256 at giver 3516";
                failingCondition = new { boundary = failBoundary, wakes = acceptWakes, lastDecide = Trunc(lastDecide, DecideFullCap), wakeRecords254 = wakeRecords254.Count, boundarySnapshots = boundarySnapshots.Count };
                execSeconds = execSw.Elapsed.TotalSeconds;
                Assert.Fail($"Q256 {verdict} at {failBoundary}: {acceptLegDetail}");
            }

            // ---- LEG 2: report leg — Ready immediately (no objectives); watch travel to 7651 ----
            var qsReady = E2eQuestDriver.QuestState(bridge, BotName, Quest256);
            var readyOk = qsReady.Active && (string.Equals(qsReady.Step, "Ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(qsReady.Status, "Ready", StringComparison.OrdinalIgnoreCase));
            Leg("ready-check", readyOk, $"quest={qsReady.Step}/{qsReady.Status} active={qsReady.Active} (talk-only: START lands at Ready)", 0);
            if (!readyOk)
            {
                failBoundary = "REPORT/not-ready-after-accept";
                Assert.Fail($"quest 256 not Ready after accept ({qsReady.Step}/{qsReady.Status}) — cannot exercise the report leg");
            }

            var repSw = Stopwatch.StartNew();
            reportStartDistM = ReporterDist(bridge);
            bool turnedIn = false;
            var repWakes = 0;
            for (var k = 0; k < MaxReportWakes; k++)
            {
                repWakes = k + 1;
                var wake = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs},\"reporter\":{Reporter7651}}}", 120_000);
                NoteDecide(wake, decideHistory);
                var obs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{Reporter7651}}}", 60_000);
                NoteDecide(obs, decideHistory);
                var decide = GetStr(obs, "questDecideDetail");
                lastDecide = decide;
                reportEndDistM = GetDbl(obs, "reporterDistM");
                if (E2eQuestDriver.HasCompleted(bridge, BotName, Quest256)) { turnedIn = true; break; }
                if (!E2eQuestDriver.IsQuestActive(bridge, BotName, Quest256))
                {
                    turnedIn = E2eQuestDriver.HasCompleted(bridge, BotName, Quest256);
                    break;
                }
            }
            reporterPos = ReadPos(bridge);
            reportLegDetail = $"wakes={repWakes} dist={reportStartDistM:0.0}m->{reportEndDistM:0.0}m turnedIn={turnedIn} decide=[{Trunc(lastDecide, 200)}]";
            if (turnedIn && (double.IsNaN(reportEndDistM) || reportEndDistM > 25.0))
                reportLegDetail += $" (turnedIn=True at {reportEndDistM:0.0}m beyond 25m gate — completion honors over proximity)";
            var reportOk = turnedIn || (!double.IsNaN(reportEndDistM) && reportEndDistM >= 0 && reportEndDistM <= 25.0);
            Leg("report-leg", reportOk, reportLegDetail, repSw.Elapsed.TotalSeconds);
            if (!reportOk)
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = "REPORT/no-progress";
                claim = "production scheduler walks the Ready report leg to reporter 7651 (<=25m)";
                failingCondition = new { boundary = failBoundary, wakes = repWakes, startDistM = reportStartDistM, endDistM = reportEndDistM, lastDecide = Trunc(lastDecide, DecideFullCap), wakeRecords254 = wakeRecords254.Count };
                execSeconds = execSw.Elapsed.TotalSeconds;
                Assert.Fail($"Q256 {verdict} at {failBoundary}: {reportLegDetail}");
            }

            // ---- LEG 3: turn-in (drain remaining wakes if not yet completed) ----
            var tiSw = Stopwatch.StartNew();
            if (!turnedIn)
            {
                for (var k = 0; k < 20 && !turnedIn; k++)
                {
                    bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs},\"reporter\":{Reporter7651}}}", 120_000);
                    turnedIn = E2eQuestDriver.HasCompleted(bridge, BotName, Quest256);
                }
            }
            var stillActive = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest256);
            turnInDetail = $"completed={turnedIn} active={stillActive}";
            Leg("turn-in", turnedIn && !stillActive, turnInDetail, tiSw.Elapsed.TotalSeconds);
            if (!turnedIn || stillActive)
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = "TURNIN/no-dispatch";
                claim = "production scheduler turns in Ready quest 256 at reporter 7651";
                failingCondition = new { boundary = failBoundary, completed = turnedIn, stillActive, lastDecide = Trunc(lastDecide, DecideFullCap), wakeRecords254 = wakeRecords254.Count };
                execSeconds = execSw.Elapsed.TotalSeconds;
                Assert.Fail($"Q256 {verdict} at {failBoundary}: {turnInDetail}");
            }

            // ---- DB proof (D-6): completed_quests block + per-owner decode +
            // quests.status, read TWICE with a flush margin. The save pass
            // flushes quest rows to MySQL; the second delayed re-read catches
            // a turn-in that landed at the window edge (kill23: +2s past the
            // endpoint read as "no row"). Never read at the window edge only.
            var dbSw = Stopwatch.StartNew();
            var saveAck = bridge.Call("{\"cmd\":\"save\"}", 180_000);
            var saved = saveAck.TryGetProperty("saved", out var sEl) && sEl.ValueKind == JsonValueKind.True;
            dbCompleted = CheckCompletedDb(BotAccount, Quest256, out var dbDetail1);
            var questsDetail1 = DumpQuestsStatus(BotAccount);
            await Task.Delay(DbFlushDelayMs);
            dbCompleted = CheckCompletedDb(BotAccount, Quest256, out var dbDetail2);
            var questsDetail2 = DumpQuestsStatus(BotAccount);
            dbDetail = $"save={saved} read1=[{dbDetail1} | {questsDetail1}] read2(+{DbFlushDelayMs / 1000}s)=[{dbDetail2} | {questsDetail2}]";
            Leg("db-proof", dbCompleted, dbDetail, dbSw.Elapsed.TotalSeconds);
            execSeconds = execSw.Elapsed.TotalSeconds;

            passed = turnedIn && !stillActive && dbCompleted;
            if (passed)
            {
                verdict = "PASS";
                failBoundary = "none";
                claim = "scheduler accepted 256 at 3516, traveled ~40m to 7651, turned in; completed_quests row present";
                failingCondition = null;
            }
            else
            {
                verdict = "FAIL-BEHAVIOR";
                failBoundary = "PERSISTENCE/no-completed-row";
                claim = "completed_quests row for quest 256 after live turn-in";
                failingCondition = new { boundary = failBoundary, dbDetail };
            }
            Assert.True(passed, $"Q256 {verdict} at {failBoundary}: {dbDetail}");
        }
        finally
        {
            totalWall.Stop();
            session?.Dispose();
            TryReleaseBot(laneBridge, BotName);
            // RERUN-254-FULL: archive Server.log + Error.log + quest observe
            // JSONL (when the lane emits one) BEFORE teardown so remover
            // lines survive it. Paths are E2eRoot-relative runtime dirs
            // (E2eStack.E2eRoot, never the shared default); lane teardown
            // stays owned-only (bot deactivate + TCP dispose — no containers
            // touched, never pruned: D-2, E2eStack pattern).
            ArchiveLaneLogs(evidence, archivedFiles);
            await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, legs, evidence.ToString(),
                new
                {
                    quest = Quest256,
                    giverNpc = Giver3516,
                    reporterNpc = Reporter7651,
                    charId = ourCharacterId,
                    giverPos,
                    reporterPos,
                    npcObjId3515 = npcObj3515,
                    npcObjId3516 = npcObj3516,
                    acceptLeg = acceptLegDetail,
                    reportLeg = reportLegDetail,
                    turnIn = turnInDetail,
                    reportStartDistM,
                    reportEndDistM,
                    lastGoodWake254,
                    firstBadWake254,
                    dbCompleted,
                    dbDetail,
                    boundarySnapshots = boundarySnapshots.ToArray(),
                    wakeRecords254 = wakeRecords254.ToArray(),
                    decideHistory = decideHistory.ToArray(),
                    archivedFiles = archivedFiles.ToArray()
                },
                setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
            Console.WriteLine($"Q256-LANE verdict={verdict} boundary={failBoundary}");
        }
    }

    // SETUP-ONLY staging (disclosed, pre-START): teleport near the quest NPC
    // and wait for the live actor to materialize. Returns the polled objId
    // (giver/offering evidence); throws when the NPC never resolves.
    private static uint TeleportResolve(BotDriveClient bridge, uint npc)
    {
        bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{npc}}}", 60_000);
        var deadline = Environment.TickCount64 + 45_000;
        while (Environment.TickCount64 < deadline)
        {
            var objId = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcObjId\",\"npc\":{npc}}}", 30_000)
                .GetProperty("objId").GetUInt32();
            if (objId != 0)
                return objId;
            Thread.Sleep(1000);
        }
        throw new InvalidOperationException($"NPC template {npc} never materialized after teleport (45s)");
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

    // RERUN-254-FULL (harness-only, read-only polls): EVERY wake of the 254
    // leg emits a structured record — Step/Status/active/completed for 254,
    // bag count of item 13713, FULL questDecideDetail (capped per-string,
    // never dropped), dispatch/reason tokens, activity/leg/travel fields,
    // action/turnIn/advance counters, liveAction/liveState/liveDetail, and
    // the FULL activeQuests id list. Answers 1-6 per transition: present
    // before? Step/Status? triggering path/event? legitimate? what removed
    // it? which of {step-machine completion, DropQuest, item-destroy /
    // DropWhenDestroy, client/network input, fixture/harness, other engine
    // path, UNKNOWN}? No drive ops that play the game: wake + observe +
    // read-only questState/isActive/hasCompleted/invCount polls only.
    private static bool WakeUntilCompletedFull(BotDriveClient bridge, uint questId, int maxWakes, List<string> decideHistory, List<object> records)
    {
        for (var k = 0; k < maxWakes; k++)
        {
            try
            {
                var wake = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                var decideFull = GetStr(wake, "questDecideDetail");
                NoteDecide(wake, decideHistory);
                var obs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                NoteDecide(obs, decideHistory);
                var obsDecide = GetStr(obs, "questDecideDetail");
                var qs = E2eQuestDriver.QuestState(bridge, BotName, questId);
                var done = E2eQuestDriver.HasCompleted(bridge, BotName, questId);
                var bag13713 = E2eQuestDriver.InvCount(bridge, BotName, Item13713);
                var rec = new
                {
                    wake = k + 1,
                    quest = questId,
                    step = qs.Step,
                    status = qs.Status,
                    active = qs.Active,
                    completed = done,
                    bagItem13713 = bag13713,
                    questDecideDetail = Trunc(obsDecide, DecideFullCap),
                    dispatch = ExtractToken(obsDecide, "dispatch="),
                    reason = ExtractToken(obsDecide, "reason="),
                    withdraws = ExtractToken(obsDecide, "withdraws="),
                    offerings = ExtractToken(obsDecide, "offerings="),
                    activity = LastActivity(obs),
                    questLegActive = GetBool(obs, "questLegActive"),
                    questTravelReason = GetStr(obs, "questTravelReason"),
                    questActionCount = GetInt(obs, "questActionCount"),
                    accepts = GetInt(obs, "accepts"),
                    turnIns = GetInt(obs, "turnIns"),
                    advances = GetInt(obs, "advances"),
                    lastAction = GetStr(obs, "lastAction"),
                    lastResult = GetStr(obs, "lastResult"),
                    lastDetail = Trunc(GetStr(obs, "lastDetail"), DetailCap),
                    liveAction = GetStr(obs, "liveAction"),
                    liveState = GetStr(obs, "liveState"),
                    liveDetail = Trunc(GetStr(obs, "liveDetail"), DetailCap),
                    activeQuestIds = ActiveQuestIds(obs),
                    readyQuestIds = ReadyIds(obs),
                    withholdTurnIn = GetBool(obs, "withholdTurnIn"),
                    currentTargetObjId = GetUInt(obs, "currentTargetObjId")
                };
                records.Add(rec);
                decideHistory.Add($"[wake{k + 1} q{questId}=step:{qs.Step ?? "(null)"}/status:{qs.Status ?? "(null)"}/active={qs.Active}/completed={done} bag13713={bag13713} ids=[{rec.activeQuestIds}] decide={Trunc(decideFull, DetailCap)}]");
                decideHistory.Add($"[wake{k + 1} DECIDE-FULL {Trunc(decideFull, DecideFullCap)}]");
            }
            catch (Exception ex)
            {
                records.Add(new { wake = k + 1, quest = questId, error = ex.GetType().Name });
                decideHistory.Add($"[wake{k + 1} CALL-FAILED {ex.GetType().Name}]");
            }
            if (E2eQuestDriver.HasCompleted(bridge, BotName, questId))
                return true;
        }
        return E2eQuestDriver.HasCompleted(bridge, BotName, questId);
    }

    private static bool WakeUntilCompleted(BotDriveClient bridge, uint questId, int maxWakes, List<string> decideHistory)
    {
        // 255 leg: sampled wake log (its drop is not under investigation).
        for (var k = 0; k < maxWakes; k++)
        {
            try
            {
                var wake = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
                NoteDecide(wake, decideHistory);
                var qs = E2eQuestDriver.QuestState(bridge, BotName, questId);
                var done = E2eQuestDriver.HasCompleted(bridge, BotName, questId);
                if ((k + 1) % 5 == 1 || done)
                    decideHistory.Add($"[wake{k + 1} activity={LastActivity(wake)} leg={GetBool(wake, "questLegActive")} travel=[{GetStr(wake, "questTravelReason")}] actions={GetInt(wake, "questActionCount")} accepts={GetInt(wake, "accepts")} turnIns={GetInt(wake, "turnIns")} q{questId}={qs.Step}/{qs.Status}/active={qs.Active}/completed={done}]");
            }
            catch (Exception ex) { decideHistory.Add($"[wake{k + 1} CALL-FAILED {ex.GetType().Name}]"); }
            if (E2eQuestDriver.HasCompleted(bridge, BotName, questId))
                return true;
        }
        return E2eQuestDriver.HasCompleted(bridge, BotName, questId);
    }

    private static bool GetBool(System.Text.Json.JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static void NoteDecide(System.Text.Json.JsonElement el, List<string> decideHistory)
    {
        var decide = GetStr(el, "questDecideDetail");
        if (!string.IsNullOrEmpty(decide) && (decideHistory.Count == 0 || decideHistory[^1] != decide))
            decideHistory.Add(decide);
    }

    private static string LastActivity(System.Text.Json.JsonElement el)
    {
        if (el.TryGetProperty("arbiterActivity", out var v))
            return v.ValueKind == JsonValueKind.Null ? "(none)" : v.GetString() ?? "(empty)";
        return "FIELD-ABSENT";
    }

    // Read-only boundary snapshot (observe-only polls, no gameplay ops):
    // npcObjIds for 3515/3516 + IsQuestActive/HasCompleted for 254/255/256
    // + QuestState Step/Status for 254/255/256 at one phase boundary.
    private static object SnapBoundary(BotDriveClient bridge, string phase)
    {
        try
        {
            return new
            {
                phase,
                atUtc = DateTime.UtcNow.ToString("o"),
                npcObjId3515 = SafeNpcObjId(bridge, Giver3515),
                npcObjId3516 = SafeNpcObjId(bridge, Giver3516),
                active254 = SafeActive(bridge, Quest254),
                completed254 = SafeCompleted(bridge, Quest254),
                active255 = SafeActive(bridge, Quest255),
                completed255 = SafeCompleted(bridge, Quest255),
                active256 = SafeActive(bridge, Quest256),
                completed256 = SafeCompleted(bridge, Quest256),
                state254 = SafeState(bridge, Quest254),
                state255 = SafeState(bridge, Quest255),
                state256 = SafeState(bridge, Quest256),
                bagItem13713 = SafeInv(bridge, Item13713)
            };
        }
        catch (Exception ex) { return new { phase, error = ex.GetType().Name }; }
    }

    private static uint SafeNpcObjId(BotDriveClient bridge, uint npc)
    {
        try
        {
            return bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcObjId\",\"npc\":{npc}}}", 30_000).GetProperty("objId").GetUInt32();
        }
        catch { return 0; }
    }

    private static bool SafeActive(BotDriveClient bridge, uint quest)
    {
        try { return E2eQuestDriver.IsQuestActive(bridge, BotName, quest); }
        catch { return false; }
    }

    private static bool SafeCompleted(BotDriveClient bridge, uint quest)
    {
        try { return E2eQuestDriver.HasCompleted(bridge, BotName, quest); }
        catch { return false; }
    }

    private static object SafeState(BotDriveClient bridge, uint quest)
    {
        try
        {
            var qs = E2eQuestDriver.QuestState(bridge, BotName, quest);
            return new { active = qs.Active, step = qs.Step, status = qs.Status };
        }
        catch (Exception ex) { return new { error = ex.GetType().Name }; }
    }

    private static int SafeInv(BotDriveClient bridge, uint item)
    {
        try { return E2eQuestDriver.InvCount(bridge, BotName, item); }
        catch { return -1; }
    }

    // First "active=True -> False without completed=True" transition across
    // the 254 wake records: last-good = last wake still present, first-bad =
    // first wake gone. (0,0) when no drop is observed.
    private static (int LastGood, int FirstBad) DropBoundary(List<object> records)
    {
        dynamic? prev = null;
        foreach (dynamic rec in records)
        {
            try
            {
                if (prev is not null && (bool)prev.active && !(bool)rec.active && !(bool)rec.completed)
                    return ((int)prev.wake, (int)rec.wake);
            }
            catch { }
            prev = rec;
        }
        return (0, 0);
    }

    private static string ExtractToken(string decide, string key)
    {
        try
        {
            var i = decide.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            var rest = decide.Substring(i + key.Length);
            var end = rest.IndexOfAny(new[] { ' ', ']', ';' });
            return Trunc(end < 0 ? rest : rest[..end], 200);
        }
        catch { return ""; }
    }

    private static string ActiveQuestIds(JsonElement obs)
    {
        try
        {
            if (!obs.TryGetProperty("activeQuests", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return GetStr(obs, "activeQuests");
            var ids = new List<string>();
            foreach (var q in arr.EnumerateArray())
            {
                if (q.ValueKind == JsonValueKind.Number && q.TryGetUInt32(out var n)) { ids.Add(n.ToString(CultureInfo.InvariantCulture)); continue; }
                if (q.ValueKind == JsonValueKind.Object && q.TryGetProperty("questId", out var id) && id.TryGetUInt32(out var u))
                    ids.Add($"{u}:{(q.TryGetProperty("step", out var s) ? s.GetString() : "")}/{(q.TryGetProperty("status", out var st) ? st.GetString() : "")}");
                else ids.Add(Trunc(q.ToString(), 60));
            }
            return string.Join(",", ids);
        }
        catch { return "UNREADABLE"; }
    }

    private static string ReadyIds(JsonElement obs)
    {
        try
        {
            if (!obs.TryGetProperty("readyQuestIds", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return "";
            return string.Join(",", arr.EnumerateArray().Select(e => e.ToString()));
        }
        catch { return ""; }
    }

    private static double ReporterDist(BotDriveClient bridge)
    {
        try
        {
            var obs = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\",\"reporter\":{Reporter7651}}}", 60_000);
            return GetDbl(obs, "reporterDistM");
        }
        catch { return double.NaN; }
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
        catch (Exception ex) { return $"UNAVAILABLE ({ex.GetType().Name})"; }
    }

    private static bool CheckCompletedDb(string accountName, uint questId, out string detail)
    {
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            // completed_quests(id=block, data=64-bit blob, owner): quest N sets
            // bit (N%64) in block (N/64). 256 -> block 4, bit 0.
            cmd.CommandText = "SELECT `data` FROM completed_quests WHERE `owner` = (SELECT c.id FROM characters c JOIN aaemu_login.users u ON c.account_id = u.id WHERE u.username = @name) AND `id` = @block";
            cmd.Parameters.AddWithValue("@name", accountName);
            cmd.Parameters.AddWithValue("@block", questId / 64);
            using var reader = cmd.ExecuteReader();
            var found = false;
            string hex = "";
            while (reader.Read())
            {
                var data = (byte[])reader.GetValue(0);
                hex = Convert.ToHexString(data);
                var bit = (int)(questId % 64);
                if (data.Length * 8 > bit && (data[bit / 8] & (1 << (bit % 8))) != 0)
                    found = true;
            }
            detail = $"completed_quests block {questId / 64} bit {questId % 64} for quest {questId} on {accountName}: {(found ? "SET" : "NOT SET")} data={hex}";
            return found;
        }
        catch (Exception ex)
        {
            detail = $"DB check failed ({ex.GetType().Name}: {ex.Message})";
            return false;
        }
    }


    // quests.status per-owner decode (D-3/D-6 companion to the bitflag
    // block): live quests rows for this account, read-only.
    private static string DumpQuestsStatus(string accountName)
    {
        try
        {
            var rows = E2eStack.DumpQuestRows(accountName);
            return rows.Count == 0 ? "quests.status: (no live rows)" : "quests.status: " + string.Join(",", rows.Select(r => $"{r.QuestId}={r.Status}"));
        }
        catch (Exception ex) { return $"quests.status UNREADABLE ({ex.GetType().Name})"; }
    }

    // Owned-only archival (D-2): copy THIS lane's runtime logs (E2eRoot-
    // relative) into ArchiveDir BEFORE teardown. Records archived vs
    // MISSING per file; never touches foreign containers or paths.
    private static void ArchiveLaneLogs(StringBuilder evidence, List<object> archivedFiles)
    {
        Directory.CreateDirectory(ArchiveDir);
        CopyLog(GameLogPath, "Server.log", evidence, archivedFiles);
        CopyLog(GameErrorLogPath, "Error.log", evidence, archivedFiles);
        var jsonl = FindQuestJsonl();
        if (jsonl is not null)
            CopyLog(jsonl, Path.GetFileName(jsonl), evidence, archivedFiles);
        else
        {
            evidence.AppendLine("- quest observe JSONL: MISSING (no jev-shadow/quest JSONL under runtime Logs)");
            archivedFiles.Add(new { source = "quest-observe-jsonl", status = "MISSING" });
        }
    }

    private static void CopyLog(string source, string destName, StringBuilder evidence, List<object> archivedFiles)
    {
        try
        {
            if (File.Exists(source))
            {
                var dest = Path.Combine(ArchiveDir, destName);
                File.Copy(source, dest, overwrite: true);
                evidence.AppendLine($"- archived {source} -> {dest} ({new FileInfo(dest).Length} bytes)");
                archivedFiles.Add(new { source, dest, status = "archived", bytes = new FileInfo(dest).Length });
            }
            else
            {
                evidence.AppendLine($"- {destName} MISSING at {source}");
                archivedFiles.Add(new { source, status = "MISSING" });
            }
        }
        catch (Exception ex)
        {
            evidence.AppendLine($"- {destName} archive failed ({ex.GetType().Name}: {ex.Message})");
            archivedFiles.Add(new { source, status = $"FAILED:{ex.GetType().Name}" });
        }
    }

    private static string? FindQuestJsonl()
    {
        try
        {
            var logsDir = Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs");
            if (!Directory.Exists(logsDir))
                return null;
            return Directory.GetFiles(logsDir, "*.jsonl").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch { return null; }
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

    private static string TryReleaseBot(BotDriveClient? bridge, string botName)
    {
        if (bridge == null)
            return "UNAVAILABLE (no bridge handle)";
        try
        {
            var res = bridge.Call($"{{\"cmd\":\"deactivate\",\"bot\":\"{botName}\"}}", 30_000);
            return $"deactivate -> {res}";
        }
        catch (Exception ex) { return $"UNAVAILABLE ({ex.GetType().Name})"; }
    }

    private static string Trunc(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<bool> ProbeLaneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new BotDriveClient(E2eStack.BridgePort);
                if (probe.Call("{\"cmd\":\"ping\"}", 3000).GetProperty("pong").GetBoolean())
                    return true;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        return false;
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
                scenario = "q256-live-lane",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "any other quest", "combat/loot paths" },
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
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
            var stampedPath = Path.Combine(EvidenceDir,
                "q256-lane-report." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
            await File.WriteAllTextAsync(stampedPath, reportJson);
            File.Copy(stampedPath, ReportPath, overwrite: true);
            File.WriteAllText(Path.Combine("/tmp/q256-evidence", "report.json"), reportJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Q256-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
