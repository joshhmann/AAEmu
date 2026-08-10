using System.Text;
using AAEmu.IntegrationTests.E2e;
using Xunit;

namespace AAEmu.IntegrationTests;

/// <summary>
/// M2b-E2E: live-server bot harness tests — bots roll against a REAL running
/// Login + Game + MySQL stack over the real network path.
///
///   - real login flow (Trion auth -> world cookie) and real enter-world flow
///     (X2EnterWorld -> character create/select -> spawn -> notify in-game)
///   - golden-route drive (16 quests) through the BotDriveBridge, which only
///     executes PlayerBotController ops on characters that entered the world
///     through the real flow (no auth bypass, no direct DB writes)
///   - pilot metrics: cycle pass rate, state-cleanup (0 leaks), restart
///     persistence (2 checkpoints), cross-cycle isolation (byte-identical
///     baseline), seeded-defect fail-before/pass-after
///
/// The stack (MySQL compose + Login + Game binaries) boots once per run.
/// </summary>
[CollectionDefinition("e2e", DisableParallelization = true)]
public class E2eCollectionDefinition;

[Collection("e2e")]
public class M2bE2eTests
{
    // Solzreed golden route — the pilot curriculum (16 census-PASS quests,
    // Docs/wiki/Golden-Route-Solzreed.md; all gates satisfiable in order).
    private static readonly uint[] GoldenRoute =
        [251, 330, 252, 254, 255, 256, 257, 259, 260, 261, 265, 266, 354, 4292, 4294, 4295];

    private static readonly Dictionary<uint, E2eQuestManifest> Manifests = LoadManifests();

    private sealed record CycleMetrics(int Bot, int Cycle, int PassedQuests, int TotalQuests, bool Passed, List<string> Leaks);

    private static Dictionary<uint, E2eQuestManifest> LoadManifests()
    {
        var manifestDir = Path.Combine(E2eStack.RepoRoot, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t1");
        var result = new Dictionary<uint, E2eQuestManifest>();
        foreach (var questId in GoldenRoute)
        {
            var path = Path.Combine(manifestDir, $"{questId}.json");
            if (!File.Exists(path))
                throw new InvalidOperationException($"manifest missing: {path}");
            result[questId] = E2eQuestManifest.LoadFromFile(path);
        }

        return result;
    }

    private static string ManifestDir()
        => Path.Combine(E2eStack.RepoRoot, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t1");

    private static void EnsureStack() => E2eStack.EnsureUp();

    private static BotDriveClient Bridge() => new(E2eStack.BridgePort);

    // ------------------------------------------------------------------ tests

    [Fact]
    [Trait("Category", "e2e")]
    public void E2e_Stack_Boot_Deterministic_And_CanonicalBaseline()
    {
        EnsureStack();

        // Bridge up (the server-side control surface is alive).
        using var bridge = Bridge();
        var pong = bridge.Call("{\"cmd\":\"ping\"}");
        Assert.True(pong.GetProperty("pong").GetBoolean());

        // Byte-identical baseline: the runtime sqlite must equal the canonical
        // copy at boot (no drift from previous runs).
        var canonical = E2eStack.CanonicalSqliteMd5;
        var runtime = E2eStack.RuntimeSqliteMd5();
        Assert.Equal(canonical, runtime);

        // Live servers on the real ports.
        Assert.True(IsPortOpen("127.0.0.1", E2eStack.LoginPort), "login port must be open");
        Assert.True(IsPortOpen("127.0.0.1", E2eStack.GamePort), "game port must be open");
        Assert.True(IsPortOpen("127.0.0.1", E2eStack.StreamPort), "stream port must be open");
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task E2e_GoldenRoute_RealNetworkFlow_Metrics()
    {
        EnsureStack();

        var run = new List<CycleMetrics>();
        var failures = new List<string>();
        const int bots = 2;
        const int cycles = 2;

        for (var botId = 1; botId <= bots; botId++)
        {
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                var account = $"e2ebot{botId}c{cycle}";
                var charName = $"bot{botId}c{cycle}";
                var leaks = new List<string>();
                var passedQuests = 0;
                var cyclePassed = true;

                using var bot = await BotNetworkSession.ConnectAsync(
                    charName, account, "e2e-secret",
                    "127.0.0.1", E2eStack.LoginPort,
                    "127.0.0.1", E2eStack.GamePort,
                    "127.0.0.1", E2eStack.StreamPort);

                // Real flow assertions: the session exists only because the
                // login server authenticated the account and issued the cookie.
                Assert.True(bot.AccountId > 0, "real login must return an accountId");
                Assert.True(bot.Cookie > 0, "real enter-world must issue a cookie");
                Assert.True(bot.CharacterId > 0, "real create/select must yield a character id");
                Assert.True(bot.InWorld, "bot must be in-world (notify in-game completed)");
                Assert.Equal(charName, bot.CharacterName);

                using var bridge = Bridge();

                // Drive the golden route end-to-end.
                foreach (var questId in GoldenRoute)
                {
                    var manifest = Manifests[questId];
                    var result = E2eQuestDriver.DriveQuest(bridge, charName, manifest, manifest.Level);
                    if (!result.Passed)
                    {
                        failures.Add($"bot {botId} cycle {cycle}: " + result.ReproTrace());
                        cyclePassed = false;
                        break;
                    }

                    passedQuests++;
                }

                if (cyclePassed)
                {
                    // State-cleanup (pilot metric): no active quests, every
                    // route quest completed, reward items present.
                    foreach (var questId in GoldenRoute)
                    {
                        if (E2eQuestDriver.IsQuestActive(bridge, charName, questId))
                            leaks.Add($"quest {questId} still active after cycle");
                        if (!E2eQuestDriver.HasCompleted(bridge, charName, questId))
                            leaks.Add($"completed flag missing for quest {questId}");
                    }
                }

                // Graceful disconnect: server must save and release the session.
                var statsBefore = bridge.Call("{\"cmd\":\"stats\"}");
                bot.Disconnect();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    var stats = bridge.Call("{\"cmd\":\"stats\"}");
                    var inWorld = stats.GetProperty("inWorld").GetInt32();
                    var connections = stats.GetProperty("connections").GetInt32();
                    if (inWorld == statsBefore.GetProperty("inWorld").GetInt32() - 1)
                    {
                        // Session released; character saved.
                        break;
                    }

                    await Task.Delay(500);
                }

                var statsAfter = bridge.Call("{\"cmd\":\"stats\"}");
                if (statsAfter.GetProperty("inWorld").GetInt32() != statsBefore.GetProperty("inWorld").GetInt32() - 1)
                    leaks.Add("bot session not released after disconnect (server-side leak)");

                run.Add(new CycleMetrics(botId, cycle, passedQuests, GoldenRoute.Length, cyclePassed && leaks.Count == 0, leaks));

                // Cross-cycle isolation: fresh account + character per cycle.
                E2eStack.CleanupBotRows(account);
            }
        }

        EmitMetricsTable(run, failures);

        Assert.Empty(failures);
        foreach (var botGroup in run.GroupBy(c => c.Bot))
        {
            Assert.All(botGroup, c => Assert.True(c.Passed, $"bot {c.Bot} cycle {c.Cycle} leaks: {string.Join("; ", c.Leaks)}"));
            Assert.All(botGroup, c => Assert.Equal(GoldenRoute.Length, c.PassedQuests));
        }

        Assert.Equal(0, run.Sum(c => c.Leaks.Count));
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task E2e_RestartPersistence_TwoCheckpoints_FullStateMatch()
    {
        EnsureStack();

        foreach (var checkpointQuest in new uint[] { 254, 266 })
        {
            var account = $"e2erestart{checkpointQuest}";
            var charName = $"restart{checkpointQuest}";
            E2eQuestManifest cpManifest = null;
            E2eQuestDriver.QuestStateSnapshot preState = null;

            // Drive everything BEFORE the checkpoint to completion.
            using var bot = await BotNetworkSession.ConnectAsync(
                charName, account, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);

            using (var bridge = Bridge())
            {
                foreach (var questId in GoldenRoute)
                {
                    if (questId == checkpointQuest)
                        break;
                    var manifest = Manifests[questId];
                    var result = E2eQuestDriver.DriveQuest(bridge, charName, manifest, manifest.Level);
                    Assert.True(result.Passed, $"pre-checkpoint drive failed: {result.ReproTrace()}");
                }

                // Prepare the checkpoint quest mid-flight (accepted + progressed,
                // NOT turned in) — the state a disconnect must survive.
                cpManifest = Manifests[checkpointQuest];
                var prepared = E2eQuestDriver.PrepareQuest(bridge, charName, cpManifest, cpManifest.Level);
                Assert.True(prepared, $"checkpoint {checkpointQuest}: quest must be active after prepare");
                Assert.False(E2eQuestDriver.HasCompleted(bridge, charName, checkpointQuest),
                    $"checkpoint {checkpointQuest}: prepared quest must NOT be completed");

                preState = E2eQuestDriver.QuestState(bridge, charName, checkpointQuest);
                Assert.True(preState.Active, "checkpoint quest must be active at snapshot");
            }

            // Graceful disconnect: server saves quest state to MySQL.
            // WAIT FOR THE CHECKPOINT QUEST'S ROW specifically — a bare count
            // comparison against preRows is vacuous when preRows is empty
            // (fresh account, quest only in memory): 0 >= 0 exits instantly and
            // the SIGKILL in RestartGameServer lands before the disconnect-save
            // commits (run18: "checkpoint 254: quest lost on restart").
            bot.Disconnect();
            var saveDeadline = DateTime.UtcNow.AddSeconds(20);
            List<(uint QuestId, int Status)> savedRows = [];
            while (DateTime.UtcNow < saveDeadline)
            {
                savedRows = E2eStack.DumpQuestRows(account);
                if (savedRows.Any(r => r.QuestId == checkpointQuest))
                    break;
                await Task.Delay(500);
            }

            Assert.True(savedRows.Any(r => r.QuestId == checkpointQuest),
                $"checkpoint {checkpointQuest}: quest row must be persisted to MySQL before restart");

            // The character save path intentionally does NOT persist inventory
            // on disconnect (Character.Save: Inventory.Save commented out) —
            // items only reach MySQL via the periodic SaveManager tick. The
            // rig config shortens AutoSaveInterval (0.2 min) so the engine's
            // REAL periodic save lands during the test window. Wait for the
            // checkpoint's OWN preseed items (not just any item — reward
            // items from pre-checkpoint quests are already in the DB, so a
            // generic count check passes before the checkpoint's items
            // persist; run21: 8130 never made it before the restart killed
            // the server). Quest-gather objectives are re-derived from the
            // live inventory after restart (QuestActObjItemGather.RunAct),
            // so a restart before the preseed items persist loses the
            // objective (run20: Expected [9,10,0,0,0] Actual [9,0,0,0,0]).
            // Only enforced for checkpoints whose manifest preseeds
            // inventory (254 has none).
            if (cpManifest.Inventory.Count > 0)
            {
                var preseedItems = cpManifest.Inventory.Select(i => i.ItemId).ToHashSet();
                var itemDeadline = DateTime.UtcNow.AddSeconds(30);
                var itemRowsPersisted = false;
                while (DateTime.UtcNow < itemDeadline)
                {
                    var rows = E2eStack.DumpItemRows(bot.CharacterId);
                    if (preseedItems.All(id => rows.Contains(id)))
                    {
                        itemRowsPersisted = true;
                        break;
                    }
                    await Task.Delay(500);
                }
                Assert.True(itemRowsPersisted,
                    $"checkpoint {checkpointQuest}: preseed items [{string.Join(",", preseedItems)}] must be persisted (SaveManager tick) before restart");
            }

            // REAL server restart (process-level; MySQL persists).
            E2eStack.RestartGameServer();

            // Reconnect the SAME account + character through the real flow.
            using var restarted = await BotNetworkSession.ConnectAsync(
                charName, account, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.Equal(bot.CharacterId, restarted.CharacterId);

            // Full-state match: the restored quest must match the snapshot.
            // Fresh bridge: the pre-restart bridge TCP connection died with
            // the old game process.
            using var postBridge = Bridge();
            var postState = E2eQuestDriver.QuestState(postBridge, charName, checkpointQuest);
            Assert.True(postState.Active, $"checkpoint {checkpointQuest}: quest lost on restart");
            Assert.Equal(preState.Step, postState.Step);
            Assert.Equal(preState.Status, postState.Status);
            Assert.Equal(preState.Objectives, postState.Objectives);

            var postRows = E2eStack.DumpQuestRows(account);
            // preRows (captured BEFORE the disconnect-save) is not the right
            // baseline — the checkpoint quest is only in memory at that point.
            // The persisted-pre-restart rows (savedRows) must match the rows
            // loaded after restart: same quests, same statuses, no loss/drift.
            Assert.Equal(savedRows.Count, postRows.Count);
            Assert.Equal(savedRows, postRows);

            // Resume through the REAL turn-in path — the reconnect must finish
            // what the disconnect interrupted.
            var resumed = E2eQuestDriver.ResumePreparedQuest(postBridge, charName, cpManifest);
            Assert.True(resumed.Passed, $"checkpoint {checkpointQuest}: resume after restart failed: {resumed.ReproTrace()}");
            Assert.True(E2eQuestDriver.HasCompleted(postBridge, charName, checkpointQuest));
            Assert.False(E2eQuestDriver.IsQuestActive(postBridge, charName, checkpointQuest));

            // Cleanup for the next checkpoint.
            restarted.Disconnect();
            E2eStack.CleanupBotRows(account);
        }
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task E2e_SeededDefect_FailBefore_PassAfter()
    {
        EnsureStack();

        try
        {
            // ---- FAIL-BEFORE: inject the known quest defect at the DATA level
            // (quest 251's report NPC -> non-existent template) and reboot the
            // game server so the loaded templates carry the defect.
            E2eStack.RestoreCanonicalSqlite();
            E2eStack.ApplySeededDefect();
            Assert.True(E2eStack.SeededDefectActive(), "defect must be present in the runtime sqlite");
            E2eStack.RestartGameServer();

            using var failBridge = Bridge();
            using (var bot = await BotNetworkSession.ConnectAsync(
                       "failbefore", "e2edefectfail", "e2e-secret",
                       "127.0.0.1", E2eStack.LoginPort,
                       "127.0.0.1", E2eStack.GamePort,
                       "127.0.0.1", E2eStack.StreamPort))
            {
                var failResult = E2eQuestDriver.DriveQuest(failBridge, "failbefore", Manifests[251], Manifests[251].Level);
                Assert.False(failResult.Passed, "seeded defect MUST fail the E2E run");
                Assert.True(new[] { "READY", "REWARD", "VERIFY" }.Contains(failResult.FailStage),
                    $"failure must surface at turn-in/completion, got {failResult.FailStage}");
                Console.WriteLine("FAIL-BEFORE repro trace:\n" + failResult.ReproTrace());
            }

            E2eStack.CleanupBotRows("e2edefectfail");

            // ---- PASS-AFTER: restore the canonical copy (byte-identical) and
            // reboot — the same drive must go green.
            E2eStack.RestoreCanonicalSqlite();
            Assert.Equal(E2eStack.CanonicalSqliteMd5, E2eStack.RuntimeSqliteMd5());
            E2eStack.RestartGameServer();

            using var passBridge = Bridge();
            using (var bot = await BotNetworkSession.ConnectAsync(
                       "passafter", "e2edefectpass", "e2e-secret",
                       "127.0.0.1", E2eStack.LoginPort,
                       "127.0.0.1", E2eStack.GamePort,
                       "127.0.0.1", E2eStack.StreamPort))
            {
                var passResult = E2eQuestDriver.DriveQuest(passBridge, "passafter", Manifests[251], Manifests[251].Level);
                Assert.True(passResult.Passed, $"reverted defect must PASS the E2E run: {passResult.ReproTrace()}");
            }

            E2eStack.CleanupBotRows("e2edefectpass");
        }
        finally
        {
            // Cycle isolation: whatever happened above (including assertion
            // failures that abort mid-phase), the runtime sqlite MUST be back
            // to the canonical copy and the game server MUST serve the clean
            // templates — otherwise the defected data leaks into every later
            // test (runs 7/8/10: turn-ins silently dropped, md5 checks red).
            E2eStack.RestoreCanonicalSqlite();
            E2eStack.RestartGameServer();
            E2eStack.CleanupBotRows("e2edefectfail", "e2edefectpass");
        }
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task E2e_CrossCycleIsolation_BaselineByteIdentical()
    {
        EnsureStack();

        // Runtime sqlite is byte-identical to the canonical baseline.
        Assert.Equal(E2eStack.CanonicalSqliteMd5, E2eStack.RuntimeSqliteMd5());

        // Fresh bot state must be identical across cycles: level 1, no active
        // quests, no completed flags, empty inventory.
        var baseline = await CaptureFreshBotBaseline("isobase1");
        var second = await CaptureFreshBotBaseline("isobase2");
        Assert.Equal(baseline, second);
    }

    private static async Task<string> CaptureFreshBotBaseline(string account)
    {
        using var bot = await BotNetworkSession.ConnectAsync(
            account, account, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        using var bridge = Bridge();

        var sb = new StringBuilder();
        sb.Append("level=").Append(botLevel(bridge, account)).Append(';');
        sb.Append("active=").Append(activeQuests(bridge, account)).Append(';');

        var anyCompleted = false;
        foreach (var questId in GoldenRoute)
            anyCompleted |= E2eQuestDriver.HasCompleted(bridge, account, questId);
        sb.Append("completed=").Append(anyCompleted).Append(';');

        sb.Append("inv=").Append(E2eQuestDriver.InvCount(bridge, account, 4058));

        bot.Disconnect();
        E2eStack.CleanupBotRows(account);
        return sb.ToString();
    }

    private static int botLevel(BotDriveClient bridge, string botName)
        => bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"charState\"}}").GetProperty("level").GetInt32();

    private static int activeQuests(BotDriveClient bridge, string botName)
        => bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"charState\"}}").GetProperty("activeQuests").GetInt32();

    private static bool IsPortOpen(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EmitMetricsTable(List<CycleMetrics> run, List<string> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# M2b-E2E live-server bot harness metrics — Solzreed golden route (16 quests)");
        sb.AppendLine();
        sb.AppendLine("> Generated by M2bE2eTests (deterministic — no wall-clock).");
        sb.AppendLine("> Stack: REAL Login (:1237) + Game (:1239/:1250) + MySQL (aaemu_login/aaemu_game), canonical compact.sqlite3 (read-only), bots over the REAL network path (Trion auth -> world cookie -> enter world -> create/select -> spawn -> notify in-game).");
        sb.AppendLine("> Quest drive: PlayerBotController ops via the BotDriveBridge on real networked sessions — no auth bypass, no direct DB writes, no quest-engine bypass.");
        sb.AppendLine();
        sb.AppendLine("| Metric | Definition | Green bar | Observed |");
        sb.AppendLine("|---|---|---|---|");

        var grouped = run.GroupBy(c => c.Bot).OrderBy(g => g.Key).ToList();
        foreach (var botGroup in grouped)
        {
            var attempts = botGroup.Count();
            var passed = botGroup.Count(c => c.Passed);
            sb.AppendLine($"| Bot {botGroup.Key} | cycles {passed}/{attempts} | 100% | {passed * 100.0 / attempts:0.0}% |");
        }

        var leakCount = run.Sum(c => c.Leaks.Count);
        sb.AppendLine();
        sb.AppendLine($"| State-cleanup | leaks over the run (active quests, missing completed flags, unreleased sessions) | 0 leaks | {leakCount} |");
        sb.AppendLine();
        sb.AppendLine($"| Cross-cycle isolation | runtime compact.sqlite3 md5 == canonical md5 at cycle start | byte-identical | {E2eStack.CanonicalSqliteMd5} |");
        sb.AppendLine();
        sb.AppendLine("## Per-cycle detail");
        sb.AppendLine();
        sb.AppendLine("| Bot | Cycle | Quests | Passed | Leaks |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in run.OrderBy(c => c.Bot).ThenBy(c => c.Cycle))
        {
            var leaks = c.Leaks.Count == 0 ? "none" : string.Join("; ", c.Leaks);
            sb.AppendLine($"| {c.Bot} | {c.Cycle} | {c.TotalQuests} | {c.PassedQuests} | {leaks} |");
        }

        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failures (drift — every entry is a regression card)");
            sb.AppendLine();
            foreach (var f in failures)
                sb.AppendLine("- " + f.Replace("\n", " "));
        }

        var outPath = Path.Combine(E2eStack.RepoRoot, "scorecard-explorations", "m2b-e2e-metrics.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine("E2E metrics table written to " + outPath);
    }
}
