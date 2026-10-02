using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// QUEST-PERCEPTION-LOOP-01: a REAL networked bot — provisioned through the
/// production path, in-world via the real login flow — is driven through the
/// LIVE scheduler wake path with the quest-bootstrap activity arbitrating.
/// No per-action commands after setup: every gameplay step (perceive →
/// discover → accept → advance → turn-in → re-discover) rides the production
/// <c>QuestDecisionScenario</c> leg inside <c>BotRoamStepExecutor</c>, which
/// the arbiter selects at rank 58 once <c>quest.progress</c> is eligible.
///
/// <para><b>Scope — one staged quest, NOT the A1 arc.</b> This belongs to the
/// Wave One conversion backlog item <c>A1</c> ("M1 — Quest and progression
/// spine"), filed in
/// <c>scorecard-explorations/mechanics/wave1-scenario-conversion-2026-09-18.md:57</c>.
/// It proves a SINGLE accept → advance → turn-in → re-discover cycle, not the
/// A1 acceptance criteria (level 1 → 10 by ordinary play, the whole curated
/// route exhausted, death recovery). The previous report labelled this
/// "E1 / M1 spine" with a two-id <c>arc</c>; no such two-quest chain was
/// executed. See <c>questCoverage</c> in the report for what was and was not
/// exercised.</para>
///
/// <para><b>What is actually proven:</b> quest <c>6198</c> (바라기 마을로,
/// level 1, accept at NPC 3511, report at NPC 3512 — per the reference DB) is
/// accepted through the real <c>AddQuest</c> gate, advanced to Ready, turned
/// in at a real spawned reporter via <c>QuestManager.DoReportEvents</c>, and
/// its <c>completed_quests</c> bit is read back from MySQL after a real
/// <c>E2eStack.RestartGameServer()</c>. The bot then re-discovers and accepts
/// whatever legal quest it picks next; the run records that id (observed:
/// 251) as an ACTIVE row after restart. That follow-up is accepted — its
/// completion is NOT observed or asserted.</para>
///
/// <para><b>Quest 330 is NOT exercised.</b> It is passed to the bridge as a
/// tracked id so observe/wake responses report a second flag, but the bot never
/// accepts it and it is never completed. Nothing here proves a 6198 → 330
/// chain, and no such claim may be made from this artifact.</para>
///
/// <para><b>Disclosed setup (never traversal proof):</b> the character is
/// provisioned fresh and STAGED ONCE at the Solzreed village hub
/// ~(15524,15278) — near the first offer. This is labeled setup: the
/// route/travel half of A1 is NOT what this artifact claims.</para>
///
/// <para><b>Negative leg:</b> the same wake loop run with the quest module
/// DISABLED must accept nothing (the module's own gate), proving the accepts
/// above came from the enabled production arbiter, not from ambient code.</para>
///
/// Report: <c>$E2E_ROOT/logs/quest-perception-loop-report.json</c> with the
/// 7-field chain (actor → path → setup → execution → result → artifact →
/// review-flag) plus an explicit <c>questCoverage</c> map.
/// H stays UNKNOWN: proxy/bot-functional evidence only. Evidence layer is
/// Tier 2 (live isolated E2E lane) — NOT H, and NOT a full-route proof.
/// </summary>
[Collection("e2e")]
public class QuestPerceptionLoopE2eTests
{
    private static readonly string NetName = "QpLoop" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
    private static readonly string NetAccount = "e2eqploop" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
    // The ONLY quest this scenario asserts completion of. Verified against the
    // reference DB (compact.sqlite3): id 6198, name 바라기 마을로, LEVEL 1,
    // offer NPC 3511, report NPC 3512.
    private const uint QuestFirst = 6198;

    // TRACKED-ONLY id, never an asserted outcome. The bot does not accept 330
    // and it is never completed; it is passed alongside QuestFirst so the
    // bridge's observe/wake responses carry a second tracked flag (reported
    // as trackedCompletedFlags[1]) and the report can show it stayed false.
    // The follow-up quest the bot ACTUALLY accepted is recorded at runtime
    // (rediscoveredQuestId) and is not hard-coded.
    private const uint QuestSecondTracked = 330;

    // Staging hub: the Solzreed village cluster at (15524,15278), near
    // the first quest offer. Placement is setup, not pathfinding evidence.
    private const float StageX = 15524.4f;
    private const float StageY = 15278.4f;
    private const float StageZ = 130f;

    // The UNTOUCHED natural spawn (CharTemplates.json template id=1) — NOT a
    // staging point. This test deliberately places nothing: the bot must walk
    // from here to any giver itself, via the production quest-travel seam.
    private const float NaturalSpawnX = 15578.042f;
    private const float NaturalSpawnY = 15382.122f;
    private const float NaturalSpawnZ = 126.484f;

    /// <summary>
    /// A1.1 — the first genuinely NEW Tier-2 outcome in this file: quest-giver
    /// DISCOVERY from an untouched natural spawn.
    ///
    /// Every other test here (and G3AutonomousQuestAccept3512Tests) stages the
    /// character NEXT TO a known giver, so none of them proves the bot can find
    /// work on its own. This one places nothing at all: the character is created
    /// at <c>CharTemplates.json</c> template id=1 and the production
    /// <c>ArmQuestTravel</c> → <c>ResolveQuestTravelTarget</c> seam must pick a
    /// lawful offerer spawner by its generic band+legality rule, the bot must
    /// walk there via the ordinary route layer, and the acceptance must come
    /// from that sweep.
    ///
    /// The observable is NOT a copy of recorded fields: it is the character
    /// having MOVED to a position it was never placed at, plus an active quest
    /// row that appeared without any direct accept. Which quest it accepts is
    /// recorded, never asserted — a hardcoded expectation would re-introduce
    /// exactly the staging-dependence this test exists to remove.
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task QuestPerceptionLoop_NaturalSpawn_WalksToGiverAndAcceptsWithoutStaging()
    {
        var wall = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            Console.WriteLine($"[quest-natural-spawn] {(passed ? "PASS" : "FAIL")} {leg}: {detail}");
        }

        var name = "QpNat" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
        var account = "e2eqpnat" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
        BotNetworkSession? net = null;
        BotDriveClient? bridge = null;

        Environment.SetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED", "1");
        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer();
            E2eStack.EnsureUp();
            await Task.Delay(3000);

            net = await ConnectWithRetryAsync(name, account);
            if (!net.InWorld)
            {
                Leg("provision-networked", false, "bot never reached in-world");
                throw new Xunit.Sdk.XunitException("bot must be in-world");
            }
            Leg("provision-networked", true,
                $"REAL-BOT '{name}' (char {net.CharacterId}) in-world via real login; NOT staged");

            bridge = new BotDriveClient(E2eStack.BridgePort);

            // PRECONDITIONS ARE ASSERTED, NOT LOGGED. If the bot is not actually
            // at the untouched natural spawn, or is not level 1 with no active
            // quest, every later claim is meaningless — so each is a real gate.
            var pre = bridge.Call(
                $"{{\"cmd\":\"quest\",\"op2\":\"observe\",\"bot\":\"{name}\",\"arc\":[]}}", timeoutMs: 30_000);
            var startX = pre.GetProperty("x").GetSingle();
            var startY = pre.GetProperty("y").GetSingle();
            var startZone = pre.GetProperty("zoneId").GetUInt32();
            var startLevel = pre.GetProperty("level").GetInt32();
            var startActive = pre.GetProperty("activeCount").GetInt32();
            var spawnDx = startX - NaturalSpawnX;
            var spawnDy = startY - NaturalSpawnY;
            var spawnDist = Math.Sqrt(spawnDx * spawnDx + spawnDy * spawnDy);
            Leg("starts-at-untouched-natural-spawn", spawnDist <= 5.0,
                $"observed ({startX:F3},{startY:F3}) vs CharTemplates id=1 ({NaturalSpawnX:F3},{NaturalSpawnY:F3}) " +
                $"= {spawnDist:F2}m (tolerance 5m); zoneKey={startZone}");
            Leg("fresh-level-one-no-active-quest", startLevel == 1 && startActive == 0,
                $"level={startLevel} activeCount={startActive}");
            // FAIL FAST: the whole claim is about starting from the untouched
            // natural spawn as a fresh level-1 with no quest. If either is false
            // the later movement/accept evidence would be meaningless, so these
            // are hard assertions BEFORE the loop, not logged lines.
            Assert.True(spawnDist <= 5.0,
                $"bot did NOT start at the natural spawn: observed ({startX:F3},{startY:F3}) " +
                $"is {spawnDist:F2}m from CharTemplates id=1 ({NaturalSpawnX:F3},{NaturalSpawnY:F3})");
            Assert.True(startLevel == 1 && startActive == 0,
                $"bot is not a fresh level-1 with no active quest: level={startLevel} activeCount={startActive}");

            // NO place / teleport call is issued anywhere in this test body.
            // That is a property of THIS SOURCE, verifiable by reading it — it
            // is deliberately NOT asserted through a self-set flag, which would
            // be tautological. The runtime proof that nothing moved us is the
            // movement itself, asserted below.
            var accepted = false;
            var moved = 0.0;
            var acceptedQuestIds = new List<uint>();
            var travelReason = "";
            var travelTargetX = 0.0;
            var travelTargetY = 0.0;
            var finalX = startX;
            var finalY = startY;
            var finalZone = startZone;
            var accepts = 0;
            var wakeTrace = new List<string>();
            // Bounded by WALL CLOCK (5 min), not wake count: the walk is
            // distance-driven, so a fixed iteration budget would be a magic
            // number standing in for patience.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            for (var i = 0; !accepted && DateTime.UtcNow < deadline; i++)
            {
                await Task.Delay(1000);
                var w = bridge.Call($"{{\"cmd\":\"quest\",\"op2\":\"wake\",\"bot\":\"{name}\",\"waitMs\":15000,\"arc\":[]}}",
                    timeoutMs: 60_000);
                finalX = w.GetProperty("x").GetSingle();
                finalY = w.GetProperty("y").GetSingle();
                finalZone = w.GetProperty("zoneId").GetUInt32();
                accepts = w.GetProperty("accepts").GetInt32();
                var reason = w.GetProperty("questTravelReason").GetString() ?? "";
                if (reason.Length > 0) travelReason = reason;
                if (w.GetProperty("questTravelTargetX").ValueKind == JsonValueKind.Number)
                {
                    travelTargetX = w.GetProperty("questTravelTargetX").GetSingle();
                    travelTargetY = w.GetProperty("questTravelTargetY").GetSingle();
                }
                moved = Math.Max(moved,
                    Math.Sqrt((finalX - startX) * (finalX - startX) + (finalY - startY) * (finalY - startY)));
                var activeIds = w.GetProperty("activeQuests").EnumerateArray()
                    .Select(e => e.GetProperty("questId").GetUInt32()).ToList();
                if (activeIds.Count > 0)
                {
                    accepted = true;
                    acceptedQuestIds = activeIds;
                }
                if (i % 10 == 0)
                    wakeTrace.Add(
                        $"w{i}:active=[{string.Join(",", activeIds)}] at=({finalX:F1},{finalY:F1}) moved={moved:F1}m " +
                        $"accepts={accepts} travel='{reason}'");
            }

            if (!accepted)
            {
                Leg("walk-to-giver-and-accept", false,
                    $"no quest accepted within the 5-minute window from natural spawn; max movement {moved:F1}m; " +
                    $"last travel reason '{travelReason}'\n" + string.Join("\n", wakeTrace.TakeLast(12)));
                throw new Xunit.Sdk.XunitException(
                    "production quest-travel never delivered the bot to a lawful offerer from natural spawn");
            }

            // The NEW observable: it WALKED there (never placed), and the accept
            // came out of the scheduler's own sweep.
            Leg("walked-beyond-discover-radius", moved > 25.0,
                $"travelled {moved:F1}m from spawn — beyond MaxInteractRange 25m, so the " +
                $"accepter could not have been in range at spawn");
            // OPTIONAL telemetry only. The travel reason string is diagnostic
            // wording and is deliberately NOT asserted — pinning it would make
            // the test brittle against a harmless log rewording. The real
            // evidence is the movement plus the accepted quest, asserted below.
            if (travelReason.Length > 0)
                Console.WriteLine($"[quest-natural-spawn] travel telemetry: '{travelReason}' " +
                                  $"target=({travelTargetX:F1},{travelTargetY:F1})");

            var evidence =
                $"start=({startX:F3},{startY:F3}) zoneKey={startZone} level={startLevel} active={startActive} " +
                $"spawnDist={spawnDist:F2}m; final=({finalX:F3},{finalY:F3}) zoneKey={finalZone}; " +
                $"moved={moved:F1}m; accepts={accepts}; accepted=[{string.Join(",", acceptedQuestIds)}]; " +
                $"travel='{travelReason}'; " +
                $"legs={string.Join(" | ", legs.Select(l => $"{l.Leg}={(l.Passed ? "PASS" : "FAIL")}"))}";
            Console.WriteLine($"[quest-natural-spawn] {evidence}");
            Assert.True(moved > 25.0 && accepts > 0,
                $"natural-spawn discovery FAIL.\nEvidence:\n{evidence}");
        }
        finally
        {
            bridge?.Dispose();
            net?.Dispose();
            Environment.SetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED", null);
        }
    }

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "quest-perception-loop-report.json");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task QuestPerceptionLoop_PerceiveAcceptTurnIn_Rediscover_OnLiveServer()
    {
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            Console.WriteLine($"[quest-perception-loop] {(passed ? "PASS" : "FAIL")} {leg}: {detail}");
        }

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
        BotNetworkSession? net = null;
        BotDriveClient? bridge = null;
        var chain = new Dictionary<string, object>();

        Environment.SetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED", "1");
        try
        {
            E2eStack.EnsureUp();
            // The flag is read at game-process boot: restart so the live server
            // carries it (the BotControlApi pattern — the shared stack boots
            // without it).
            E2eStack.RestartGameServer();
            E2eStack.EnsureUp();
            // The game server must re-register its world with the login
            // server before the login world-list round-trip can succeed;
            // connecting immediately returns an empty list and times out.
            await Task.Delay(3000);

            net = await ConnectWithRetryAsync();
            if (!net.InWorld)
            {
                Leg("provision-networked", false, "bot never reached in-world");
                throw new Xunit.Sdk.XunitException("net bot must be in-world");
            }
            var characterId = net.CharacterId;
            Leg("provision-networked", true,
                $"bot {NetName} in-world via real login flow (char {characterId})");
            chain["actor"] =
                $"REAL-BOT: networked PlayerBot '{NetName}' (char {characterId}, account {NetAccount}) " +
                "in-world through the real login flow (BotNetworkSession.ConnectAsync); " +
                "driven only by production PlayerBotScheduler wakes";

            bridge = new BotDriveClient(E2eStack.BridgePort);

            // Setup (disclosed): stage ONCE at the start hub. Every gameplay
            // step after this rides scheduler wakes only.
            var placed = bridge.Call(
                $"{{\"cmd\":\"quest\",\"op2\":\"place\",\"bot\":\"{NetName}\",\"x\":{StageX},\"y\":{StageY},\"z\":{StageZ}}}",
                timeoutMs: 30_000);
            var stageAt = $"({placed.GetProperty("x").GetSingle():F1},{placed.GetProperty("y").GetSingle():F1}," +
                          $"{placed.GetProperty("z").GetSingle():F1}) zone {placed.GetProperty("zoneId").GetUInt32()}";
            Leg("stage-at-start-hub", true, $"staged once at {stageAt} (disclosed setup, not traversal proof)");
            chain["setup"] =
                $"fresh provisioned level-1 Nuian staged ONCE at {stageAt} (disclosed, not traversal proof); " +
                "boot flag AAEMU_QUEST_BOOTSTRAP_ENABLED=1; " +
                "no level/money/item grant, no per-action commands after this point";

            // Observe the pre-state: fresh character, nothing active/completed.
            var pre = bridge.Call(
                $"{{\"cmd\":\"quest\",\"op2\":\"observe\",\"bot\":\"{NetName}\",\"arc\":[{QuestFirst},{QuestSecondTracked}]}}",
                timeoutMs: 30_000);
            var preActive = pre.GetProperty("activeCount").GetInt32();
            var preCompleted = pre.GetProperty("completedCount").GetInt32();
            Leg("observe-fresh-pre-state", preActive == 0,
                $"active={preActive} completed={preCompleted} level={pre.GetProperty("level").GetInt32()} " +
                $"money={pre.GetProperty("money").GetInt64()} (fresh: no quest work yet)");

            // ---- Leg: perceive + accept the first quest -------------------
            var accepted = false;
            var observedLegActive = false;
            var lastWake = pre;
            var wakeTrace = new List<string>();
            for (var i = 0; i < 40 && !accepted; i++)
            {
                await Task.Delay(400);
                var w = bridge.Call(
                    $"{{\"cmd\":\"quest\",\"op2\":\"wake\",\"bot\":\"{NetName}\",\"waitMs\":20000,\"arc\":[{QuestFirst},{QuestSecondTracked}]}}",
                    timeoutMs: 60_000);
                lastWake = w;
                var activeIds = w.GetProperty("activeQuests").EnumerateArray()
                    .Select(e => e.GetProperty("questId").GetUInt32()).ToList();
                observedLegActive |= w.GetProperty("questLegActive").GetBoolean();
                if (w.TryGetProperty("liveAction", out var la) && la.ValueKind == JsonValueKind.String)
                    wakeTrace.Add($"w{i}:leg={w.GetProperty("questLegActive").GetBoolean()} " +
                                  $"live={la.GetString()}/{w.GetProperty("liveState").GetString()} " +
                                  $"active=[{string.Join(",", activeIds)}] last={w.GetProperty("lastAction").GetString()}");
                if (activeIds.Contains(QuestFirst))
                    accepted = true;
            }
            if (!accepted)
            {
                Leg("perceive-and-accept", false,
                    $"quest {QuestFirst} never became active in 40 wakes; questLegActive seen={observedLegActive}\n" +
                    string.Join("\n", wakeTrace.TakeLast(12)));
                throw new Xunit.Sdk.XunitException("production loop never accepted the first offer");
            }
            Leg("perceive-and-accept", true,
                $"quest {QuestFirst} accepted through the production quest leg (questLegActive seen={observedLegActive}); " +
                $"accepts={lastWake.GetProperty("accepts").GetInt32()}");

            // ---- Leg: advance to Ready + turn in at the real reporter ------
            var firstDone = false;
            var reporterSpawned = false;
            // The bot must WALK to the out-of-range reporter (169 m from the
            // offerer). One scheduler wake advances one actor tick, so the
            // wake cadence sets the walk rate: pace at ~0.8 s so each tick
            // gets real elapsed time (clamped to MaxStepElapsed = 1 s → up to
            // 2.5 m/wake) instead of a few centimetres of back-to-back wakes.
            for (var i = 0; i < 400 && !firstDone; i++)
            {
                await Task.Delay(800);
                var w = bridge.Call(
                    $"{{\"cmd\":\"quest\",\"op2\":\"wake\",\"bot\":\"{NetName}\",\"waitMs\":20000,\"arc\":[{QuestFirst},{QuestSecondTracked}]}}",
                    timeoutMs: 60_000);
                lastWake = w;
                var activeIds = w.GetProperty("activeQuests").EnumerateArray()
                    .Select(e => e.GetProperty("questId").GetUInt32()).ToList();
                if (w.GetProperty("readyQuestIds").ValueKind == JsonValueKind.Array &&
                    w.GetProperty("readyQuestIds").EnumerateArray().Any(e => e.GetUInt32() == QuestFirst))
                    reporterSpawned = true; // reached Ready; reporter must be live to turn in
                var arc = w.GetProperty("arcCompleted").EnumerateArray().Select(e => e.GetBoolean()).ToList();
                if (arc.Count > 0 && arc[0])
                    firstDone = true;
            }
            if (!firstDone)
            {
                Leg("advance-and-turn-in", false,
                    $"quest {QuestFirst} never completed; reachedReady={reporterSpawned}, last={lastWake}");
                throw new Xunit.Sdk.XunitException("turn-in never landed for the first quest");
            }
            Leg("advance-and-turn-in", true,
                $"quest {QuestFirst} completed via the real report path (turnIns={lastWake.GetProperty("turnIns").GetInt32()}, " +
                $"readySeen={reporterSpawned})");

            // ---- Leg: re-discover and accept fresh work --------------------
            // The perception loop must keep working after a completion: the
            // bot re-discovers and accepts another legal offer. Record the id
            // it ACTUALLY accepts — whatever the arbiter legally picks next
            // (observed 251 in the 2026-10-02 run). The tracked-only id 330 is
            // NOT excluded: if the arbiter picks it, trackedSecondId.accepted
            // reports true and the report says so.
            var secondAccepted = false;
            uint secondQuestId = 0;
            for (var i = 0; i < 250 && !secondAccepted; i++)
            {
                await Task.Delay(800);
                var w = bridge.Call(
                    $"{{\"cmd\":\"quest\",\"op2\":\"wake\",\"bot\":\"{NetName}\",\"waitMs\":20000,\"arc\":[{QuestFirst},{QuestSecondTracked}]}}",
                    timeoutMs: 60_000);
                lastWake = w;
                var activeIds = w.GetProperty("activeQuests").EnumerateArray()
                    .Select(e => e.GetProperty("questId").GetUInt32()).ToList();
                if (activeIds.Count > 0)
                {
                    secondAccepted = true;
                    secondQuestId = activeIds[0];
                }
            }
            if (!secondAccepted)
            {
                Leg("rediscover-next-quest", false,
                    "no follow-up quest accepted after the first completion; " +
                    $"last={lastWake.GetProperty("lastAction").GetString()}/{lastWake.GetProperty("lastDetail").GetString()}");
            }
            else
            {
                Leg("rediscover-next-quest", true,
                    $"accepted follow-up quest {secondQuestId} after {QuestFirst} completed");
            }

            var finalWake = bridge.Call(
                $"{{\"cmd\":\"quest\",\"op2\":\"observe\",\"bot\":\"{NetName}\",\"arc\":[{QuestFirst},{QuestSecondTracked}]}}",
                timeoutMs: 30_000);
            var arcFinal = finalWake.GetProperty("arcCompleted").EnumerateArray().Select(e => e.GetBoolean()).ToList();
            var completedFinal = finalWake.GetProperty("completedCount").GetInt32();
            // The capability claim is the full loop: perceive → accept →
            // pursue → turn-in → re-discover. Both halves are required — a
            // completion with no re-discovery would not be the loop.
            var chainComplete = arcFinal.Count > 0 && arcFinal[0] && secondAccepted;
            Leg("perception-loop-complete", chainComplete,
                $"first quest completed [{arcFinal[0]}], follow-up accepted [{secondAccepted} (q{secondQuestId})], " +
                $"completedCount={completedFinal}");

            chain["path"] =
                $"perception → {QuestFirst} accept (GameplayActor.AcceptQuest → PlayerBotController.AcceptQuest → " +
                "CharacterQuests.AddQuest) → advance to Ready (GameplayActor.AdvanceQuest → Quest.RunCurrentStep) → " +
                $"turn-in at real spawned reporter (GameplayActor.TurnInQuest → PlayerBotController.ReportTurnIn → " +
                $"QuestManager.DoReportEvents) → re-discover + accept quest {secondQuestId}";
            chain["execution"] =
                $"production PlayerBotScheduler wakes only; quest.{QuestFirst} then quest.{secondQuestId} driven by the arbiter-selected " +
                "quest.progress activity (rank 58) → QuestDecisionScenario leg inside BotRoamStepExecutor (no per-action commands)";
            chain["result"] =
                $"quest {QuestFirst} completed flag [{string.Join(",", arcFinal)}], rediscoveredQuestId={secondQuestId} " +
                $"(accepted; its completion is NOT asserted — this run never re-checked its flag. " +
                $"tracked-only {QuestSecondTracked} flag " +
                $"[{(arcFinal.Count > 1 ? arcFinal[1].ToString() : "not-reported")}]), " +
                $"completedCount={completedFinal}, accepts={finalWake.GetProperty("accepts").GetInt32()}, " +
                $"advances={finalWake.GetProperty("advances").GetInt32()}, " +
                $"turnIns={finalWake.GetProperty("turnIns").GetInt32()}, level={finalWake.GetProperty("level").GetInt32()}";
            chain["reviewFlag"] =
                "H UNKNOWN — bot-functional proxy evidence only; the route/feel half of A1 (Field Guide §5.1) stays human-owned";

            var passed = chainComplete;

            // Completed quest flags are stored in completed_quests as 64-bit
            // blocks (questId / 64, bit questId % 64); quests holds only ACTIVE
            // rows. Save, restart this lane's game PID, then check both stores.
            bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000);
            net.Dispose();
            var killedPid = E2eStack.RestartGameServer();
            var questRows = E2eStack.DumpQuestRows(NetAccount);
            byte[]? completedBlock;
            using (var conn = E2eStack.OpenDb("aaemu_game"))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT data FROM completed_quests WHERE owner = @owner AND id = @block";
                cmd.Parameters.AddWithValue("@owner", characterId);
                cmd.Parameters.AddWithValue("@block", QuestFirst / 64);
                completedBlock = cmd.ExecuteScalar() as byte[];
            }
            var bitIndex = (int)(QuestFirst % 64);
            var completedPersisted = completedBlock is { Length: 8 } &&
                                     (completedBlock[bitIndex / 8] & (1 << (bitIndex % 8))) != 0;
            var checkpointPassed = killedPid > 0 && completedPersisted;
            Leg("restart-checkpoint-completed-quest", checkpointPassed,
                $"killed pid {killedPid}, rebooted; completed_quests block {QuestFirst / 64} " +
                $"bit {bitIndex} for char {characterId} set={completedPersisted}");
            var activeFollowUpPersisted = secondAccepted && questRows.Any(r => r.QuestId == secondQuestId);
            Leg("restart-checkpoint-active-follow-up", activeFollowUpPersisted,
                $"MySQL active quests for {NetAccount}: " +
                $"[{string.Join(",", questRows.Select(r => $"{r.QuestId}:{r.Status}"))}]; " +
                $"rediscovered quest {secondQuestId} active={activeFollowUpPersisted}");

            passed = passed && checkpointPassed && activeFollowUpPersisted;
            chain["artifact"] =
                $"{(passed ? "PASS" : "FAIL")} — {ReportPath}; post-restart completed {QuestFirst} " +
                $"bit set={completedPersisted}, active rows " +
                $"[{string.Join(",", questRows.Select(r => $"{r.QuestId}:{r.Status}"))}]; " +
                $"game.log tail unhandled={CountLogTailMatches(logOffset, "Unhandled exception")} " +
                $"fatals={CountLogTailMatches(logOffset, "|FATAL|")}";

            var evidence = string.Join("\n", legs.Select(l =>
                $"{(l.Passed ? "PASS" : "FAIL")} {l.Leg} ({l.Ms:F0}ms): {l.Detail}"));
            await WriteReportAsync(passed, legs, chain, evidence, wall.Elapsed.TotalSeconds,
                unhandled: CountLogTailMatches(logOffset, "Unhandled exception"),
                fatals: CountLogTailMatches(logOffset, "|FATAL|"),
                rediscoveredQuestId: secondQuestId,
                rediscoveredAccepted: secondAccepted,
                activeRowPersisted: activeFollowUpPersisted,
                firstQuestAccepted: accepted,
                firstQuestReachedReady: reporterSpawned,
                firstQuestCompletedFlag: arcFinal.Count > 0 ? arcFinal[0] : null,
                completedBitPersisted: completedPersisted,
                // 330 is TRACKED, not forbidden: report whether the engine
                // actually has it active, measured from the final wake.
                trackedAccepted: secondQuestId == QuestSecondTracked,
                trackedQuestCompletedFlag: arcFinal.Count > 1 ? arcFinal[1] : null);

            Assert.True(passed,
                $"quest perception loop FAIL.\nEvidence:\n{evidence}\nReport: {ReportPath}");
        }
        finally
        {
            net?.Dispose();
            Environment.SetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED", null);
        }
    }

    /// <summary>
    /// NEGATIVE leg (its own boot, module OFF): with the quest-bootstrap
    /// module disabled the arbiter can never select <c>quest.progress</c>, so
    /// a staged fresh bot does no quest work at all — no accept, no advance,
    /// no turn-in, and the quest leg never fires. This isolates the positive
    /// run's quest work to the enabled production module rather than to
    /// ambient code that would have quested anyway.
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task QuestPerceptionLoop_ModuleDisabled_DoesNoQuestWork()
    {
        var wall = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            Console.WriteLine($"[quest-perception-loop-neg] {(passed ? "PASS" : "FAIL")} {leg}: {detail}");
        }

        var negName = "QpNeg" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
        var negAccount = "e2eqpneg" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
        BotNetworkSession? net = null;
        BotDriveClient? bridge = null;

        // Boot the lane WITHOUT the module flag.
        Environment.SetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED", null);
        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer();
            E2eStack.EnsureUp();
            await Task.Delay(3000);

            net = await ConnectWithRetryAsync(negName, negAccount);
            if (!net.InWorld)
            {
                Leg("provision-networked", false, "bot never reached in-world");
                throw new Xunit.Sdk.XunitException("neg bot must be in-world");
            }
            Leg("provision-networked", true, $"bot {negName} in-world (char {net.CharacterId})");

            bridge = new BotDriveClient(E2eStack.BridgePort);
            bridge.Call(
                $"{{\"cmd\":\"quest\",\"op2\":\"place\",\"bot\":\"{negName}\",\"x\":{StageX},\"y\":{StageY},\"z\":{StageZ}}}",
                timeoutMs: 30_000);
            Leg("stage-at-start-hub", true, $"staged once at ({StageX},{StageY}) (disclosed setup)");

            var legActive = false;
            var accepts = 0;
            var activeCount = -1;
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(800);
                var w = bridge.Call(
                    $"{{\"cmd\":\"quest\",\"op2\":\"wake\",\"bot\":\"{negName}\",\"waitMs\":12000,\"arc\":[{QuestFirst},{QuestSecondTracked}]}}",
                    timeoutMs: 60_000);
                legActive |= w.GetProperty("questLegActive").GetBoolean();
                accepts = w.GetProperty("accepts").GetInt32();
                activeCount = w.GetProperty("activeCount").GetInt32();
            }

            var passed = !legActive && accepts == 0 && activeCount == 0;
            Leg("module-off-no-quest-work", passed,
                $"questLegActive={legActive}, accepts={accepts}, activeCount={activeCount} " +
                "— no quest work without the production module enabled");
            var evidence = string.Join("\n", legs.Select(l =>
                $"{(l.Passed ? "PASS" : "FAIL")} {l.Leg} ({l.Ms:F0}ms): {l.Detail}"));
            Assert.True(passed, $"negative leg FAIL:\n{evidence}");
        }
        finally
        {
            bridge?.Dispose();
            net?.Dispose();
        }
    }

    private async Task<BotNetworkSession> ConnectWithRetryAsync(string? name = null, string? account = null)
    {
        // The login listener accepts TCP before it will serve an auth
        // round-trip, so an immediate connect can time out on the first frame.
        // Retry the REAL login flow a bounded number of times — never a
        // synthetic in-world shortcut.
        var botName = name ?? NetName;
        var botAccount = account ?? NetAccount;
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var session = await BotNetworkSession.ConnectAsync(
                    botName, botAccount, "e2e-secret",
                    "127.0.0.1", E2eStack.LoginPort,
                    "127.0.0.1", E2eStack.GamePort,
                    "127.0.0.1", E2eStack.StreamPort);
                if (session.InWorld)
                    return session;
                session.Dispose();
                last = new InvalidOperationException("session did not reach in-world");
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(3000);
        }
        throw new Xunit.Sdk.XunitException(
            $"bot {botName} could not complete the real login flow in 4 attempts: {last?.Message}");
    }

    private async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        Dictionary<string, object> chain, string evidence, double wallSeconds,
        int unhandled, int fatals, uint rediscoveredQuestId, bool rediscoveredAccepted,
        bool activeRowPersisted, bool firstQuestAccepted, bool firstQuestReachedReady,
        bool? firstQuestCompletedFlag, bool? completedBitPersisted,
        bool trackedAccepted, bool? trackedQuestCompletedFlag)
    {
        // Every field below is an OBSERVED value from this run; `null` means
        // "not observed" and never means "false". This report is written only
        // after the run reaches the restart checkpoint — the two earlier exits
        // (never accepted / never turned in) throw before this call, so NO
        // report file exists for those failures. Read a missing report as
        // "the run aborted before the checkpoint", not as a failed verdict.
        var trackedIsFollowUp = rediscoveredQuestId == QuestSecondTracked;
        // Provenance is MEASURED at report time, not asserted. lane.revision stays
        // the bare HEAD (unchanged convention); sourceDirty is what tells a bare
        // HEAD apart from a dirty build, and publishSourceIdentity is a separate,
        // narrower question about the published Game/Login binaries.
        var dirty = E2eStack.MeasuredSourceIdentity;

        var report = new
        {
            scenario = "quest-perception-loop",
            verdict = passed ? "PASS" : "FAIL",
            // Conversion-backlog item A1 in wave1-scenario-conversion-2026-09-18.md.
            // This run satisfies ONE accept→advance→turn-in→re-discover cycle,
            // NOT the A1 acceptance criteria (level 1→10, whole curated route,
            // death recovery). Label it by that smaller scope, not as "M1 spine".
            backlogItem = "A1 (M1 quest/progression spine) — single-cycle proof only, not the A1 exit",
            questCoverage = new
            {
                stagedQuest = new
                {
                    questId = QuestFirst,
                    // observed from the wake loop: activeIds contained 6198
                    accepted = (bool?)firstQuestAccepted,
                    // observed: readyQuestIds contained 6198 at least once
                    reachedReady = (bool?)firstQuestReachedReady,
                    // observed: firstDone — arcCompleted[0] became true
                    completedInSession = (bool?)firstQuestCompletedFlag,
                    // observed: completed_quests block/bit read back after restart
                    completedBitPersistedAfterRestart = (bool?)completedBitPersisted,
                },
                // One shape for both branches: no follow-up accepted leaves the
                // measurements null rather than flipping them to false.
                followUpQuest = new
                {
                    questId = (uint?)rediscoveredQuestId,
                    accepted = (bool?)rediscoveredAccepted,
                    activeRowPersistedAfterRestart = (bool?)activeRowPersisted,
                    // null = not observed. This run never re-polled this quest's
                    // completed flag, so absence of a completion is NOT evidence
                    // — say so instead of asserting a value nobody measured.
                    completed = (bool?)null,
                    completionNote = "not observed — the test never re-checked this quest's completed flag",
                    noneAccepted = rediscoveredQuestId == 0,
                },
                trackedSecondId = new
                {
                    questId = QuestSecondTracked,
                    // MEASURED, not assumed. 330 is not forbidden — if the
                    // arbiter legally picks it as the follow-up, this is true.
                    accepted = (bool?)trackedAccepted,
                    completed = (bool?)trackedQuestCompletedFlag,
                    note = trackedIsFollowUp
                        ? "the arbiter picked this id as the follow-up in this run"
                        : "passed to the bridge only so a second tracked flag is reported; not the follow-up chosen here. No 6198→330 chain is claimed.",
                },
                noChainClaimed = "single-cycle only — no multi-quest chain, no A1 route exhaustion, no level 1→10 progression claim",
            },
            pass = passed,
            wallSeconds = Math.Round(wallSeconds, 1),
            lane = new
            {
                root = E2eStack.E2eRoot,
                revision = E2eStack.SourceRevision,
                loginPort = E2eStack.LoginPort,
                gamePort = E2eStack.GamePort,
            },
            provenance = new
            {
                // Bare HEAD, unchanged convention. On its own this cannot tell a
                // clean checkout from a dirty one.
                head = dirty.Head,
                // MEASURED at report time: what the TEST ASSEMBLY was built from.
                sourceDirty = new
                {
                    dirtyPathCount = dirty.DirtyPathCount,
                    trackedChangedPathCount = dirty.TrackedChangedPathCount,
                    untrackedPathCount = dirty.UntrackedPathCount,
                    digest = dirty.Digest,
                    trackedPaths = dirty.TrackedPathList,
                    unavailable = dirty.Unavailable,
                    scope = "whole working tree (tracked changes + untracked files)",
                    digestCovers = "tracked: git diff HEAD text; untracked: path + file bytes, with a 1 MiB per-file content cap (larger files contribute size only)",
                    doesNotProve = "that the built test DLL matches this digest, nor that the published Game/Login binaries match it",
                },
                // NARROWER and different: the stamp that decides whether the lane
                // republishes. Covers only AAEmu.Commons, AAEmu.Commons.Network,
                // AAEmu.Game, AAEmu.Login — NOT AAEmu.IntegrationTests, so it
                // cannot witness this test file's own state.
                publishSourceIdentity = new
                {
                    stamp = E2eStack.PublishSourceIdentity,
                    scope = "AAEmu.Commons AAEmu.Commons.Network AAEmu.Game AAEmu.Login",
                    excludesThisProject = true,
                    stampPath = Path.Combine(E2eStack.E2eRoot, "runtime", ".publish-stamp"),
                },
            },
            chain,
            legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, ms = Math.Round(l.Ms, 0) }),
            logTail = new { unhandledExceptions = unhandled, fatals },
            evidence,
        };
        await File.WriteAllTextAsync(ReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Counts marker lines in the game-log bytes appended since <paramref name="startOffset"/>.</summary>
    private static int CountLogTailMatches(long startOffset, string marker)
    {
        try
        {
            if (!File.Exists(GameLogPath))
                return 0;
            using var fs = File.OpenRead(GameLogPath);
            if (fs.Length <= startOffset)
                return 0;
            fs.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var count = 0;
            while (reader.ReadLine() is { } line)
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    count++;
            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
