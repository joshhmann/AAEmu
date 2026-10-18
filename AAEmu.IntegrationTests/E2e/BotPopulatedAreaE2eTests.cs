using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Xunit;

using static AAEmu.IntegrationTests.E2e.BotStartupArmingE2eTests;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// "Small populated area" live lane: N PlayerBots armed at boot by the
/// supported startup switch, every one running the ordinary autonomous loop
/// (quest bootstrap + self-defence + roam under the one arbiter), sharing one
/// starter area. It asks whether the single-bot loop survives company:
///
///   - every bot makes its own progress (saved XP and completed quests grow);
///   - no bot is parked (its live position never stays put for the stall window);
///   - a REAL restart re-enrols the same characters, and every one of them
///     makes new saved progress afterwards (the loop resumes from saved state);
///   - and what it cost: tick timings from the server's own metrics lines,
///     plus how often the arbiter had no owner and how often bots defended
///     themselves.
///
/// Nothing is seeded, assigned or repaired: the bots are provisioned by the
/// switch under test, progress is read from ordinary saved rows (characters,
/// completed_quests), and the only writes are the switch's own. Startup arming
/// provisions level-5 Nuian bots at the shared template spawn — disclosed in
/// the report, not hidden.
/// </summary>
[Collection("e2e")]
public class BotPopulatedAreaE2eTests
{
    private const string Token = "e2e-population-token";

    private static int BotCount => int.TryParse(Environment.GetEnvironmentVariable("E2E_POP_BOTS"), out var n)
        ? Math.Clamp(n, 2, 10) : 6;

    private static double PlaySeconds => double.TryParse(Environment.GetEnvironmentVariable("E2E_POP_PLAY_SECONDS"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Max(360, s) : 900;

    /// <summary>One autosave interval (World.json AutoSaveInterval 5 min) plus margin.</summary>
    private const double AfterRestartSeconds = 390;

    /// <summary>A bot whose live position does not change for this long is parked.</summary>
    private const double StallSeconds = 300;

    private static string GameLog => Path.Combine(E2eStack.E2eRoot, "logs", "game.log");

    private static string RestartLog => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    private static string ReportPath => Path.Combine(E2eStack.E2eRoot, "logs", "bot-populated-area-report.json");

    private static string[] ManagedAccounts => Enumerable.Range(1, BotCount)
        .Select(i => $"bot_managed_startup_{i:D3}").ToArray();

    private sealed record Saved(uint Id, string Name, int Level, long Experience, int CompletedQuests, long ObjectiveProgress);

    private sealed class Watch
    {
        public required string Name { get; init; }
        public (float X, float Y, float Z) LastPosition { get; set; }
        public DateTime LastMovedUtc { get; set; }
        public double LongestStillSeconds { get; set; }
        public Dictionary<string, int> Activities { get; } = [];
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PopulatedArea_BotsArmedAtBoot_EachProgressesCoexists_AndResumesAfterRestart()
    {
        var wall = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail)>();
        var evidence = new StringBuilder();
        void Leg(string leg, bool ok, string detail)
        {
            legs.Add((leg, ok, detail));
            evidence.AppendLine($"- [{(ok ? "x" : " ")}] {leg} ({wall.Elapsed.TotalSeconds:0.0}s): {detail}");
        }

        var passed = false;
        var bots = new List<(string Name, uint Id, string State)>();
        var watches = new Dictionary<string, Watch>();
        Dictionary<uint, Saved> start = [], played = [], resumed = [];
        object? cost = null;

        Environment.SetEnvironmentVariable("AAEMU_STARTUP_ARMING", "1");
        Environment.SetEnvironmentVariable("AAEMU_STARTUP_BOT_COUNT", BotCount.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS", BotCount.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_IDLE_WAKE", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        try
        {
            E2eStack.RequireFlags("AAEMU_STARTUP_ARMING", "AAEMU_STARTUP_BOT_COUNT", "AAEMU_QUEST_BOOTSTRAP_ENABLED");
            evidence.AppendLine($"Revision: {E2eStack.SourceRevision} · E2E_ROOT {E2eStack.E2eRoot} · bots {BotCount} · "
                + $"play {PlaySeconds:0}s · startup roster: level-5 Nuian male at the template spawn (switch defaults)");
            evidence.AppendLine();

            E2eStack.EnsureUp();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));
            using var authed = NewClient(Token);

            // ------------------------------------------------------ armed, N real
            var enrolLine = await WaitForLogLineAsync(GameLog, "Startup arming: ", TimeSpan.FromSeconds(300));
            Assert.NotNull(enrolLine);
            Assert.Contains($"enrolled {BotCount} bot(s)", enrolLine);
            bots = await WaitForActiveBotsAsync(authed, BotCount, TimeSpan.FromSeconds(120));
            Assert.Equal(BotCount, bots.Count);
            start = ReadSaved(bots);
            Assert.Equal(BotCount, start.Count);
            Leg("armed", true, $"`{enrolLine.Trim()}`; active: {string.Join(", ", bots.Select(b => $"{b.Name}(id {b.Id})"))}");

            // ---------------------------------------------------------- play
            foreach (var (name, _, _) in bots)
                watches[name] = new Watch { Name = name, LastMovedUtc = DateTime.UtcNow };
            var playStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - playStart).TotalSeconds < PlaySeconds)
            {
                await SampleAsync(authed, watches);
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
            await SampleAsync(authed, watches);
            played = ReadSaved(bots);

            var progressed = bots.Where(b => played[b.Id].Experience > start[b.Id].Experience
                && played[b.Id].CompletedQuests > start[b.Id].CompletedQuests).ToList();
            var parked = watches.Values.Where(w => w.LongestStillSeconds >= StallSeconds).ToList();
            Leg("each-progresses", progressed.Count == BotCount,
                string.Join(" | ", bots.Select(b => Progress(b.Name, start[b.Id], played[b.Id]))));
            Leg("none-parked", parked.Count == 0,
                string.Join(" | ", watches.Values.Select(w => $"{w.Name}: longest still {w.LongestStillSeconds:0}s, "
                    + string.Join(",", w.Activities.OrderByDescending(a => a.Value).Take(4).Select(a => $"{a.Key}×{a.Value}")))));

            var log = await ReadSharedAsync(GameLog);
            cost = Cost(log, bots);
            Leg("cost-recorded", true, JsonSerializer.Serialize(cost));

            // ----------------------------------------------- restart, resume
            var killed = E2eStack.RestartGameServer();
            Assert.True(killed > 0, "RestartGameServer killed no live game process");
            var reEnrol = await WaitForLogLineAsync(RestartLog, "Startup arming: ", TimeSpan.FromSeconds(300));
            Assert.NotNull(reEnrol);
            var reBots = await WaitForActiveBotsAsync(authed, BotCount, TimeSpan.FromSeconds(120));
            var sameRoster = reBots.OrderBy(b => b.Id).Select(b => (b.Id, b.Name))
                .SequenceEqual(bots.OrderBy(b => b.Id).Select(b => (b.Id, b.Name)));
            Leg("restart-same-roster", sameRoster,
                $"killed pid {killed}; re-enrolled {string.Join(", ", reBots.Select(b => $"{b.Name}(id {b.Id})"))}");
            var afterRestartBase = ReadSaved(bots);
            await Task.Delay(TimeSpan.FromSeconds(AfterRestartSeconds));
            resumed = ReadSaved(bots);
            // Any saved progress counts: XP, a completed quest, or an
            // in-progress objective counter (run 1: Anders resumed a level-1
            // rabbit hunt, kills 4->8, which gives a level-6 bot no XP).
            var resumedBots = bots.Where(b => resumed[b.Id].Experience > afterRestartBase[b.Id].Experience
                || resumed[b.Id].CompletedQuests > afterRestartBase[b.Id].CompletedQuests
                || resumed[b.Id].ObjectiveProgress > afterRestartBase[b.Id].ObjectiveProgress).ToList();
            Leg("resumes-after-restart", resumedBots.Count == BotCount,
                string.Join(" | ", bots.Select(b => Progress(b.Name, afterRestartBase[b.Id], resumed[b.Id]))));

            var stop = await PostJsonAsync(authed, "/api/bots/host/stop", "{\"reason\":\"population-lane-stop\"}");
            var converged = stop.GetProperty("Report").GetProperty("Converged").GetBoolean();
            Leg("stop-converges", converged, stop.GetProperty("Report").ToString());

            passed = legs.All(l => l.Passed);
            Assert.True(passed, "populated area NOT_PROVED: " + string.Join("; ", legs.Where(l => !l.Passed).Select(l => l.Leg)));
        }
        catch (Exception ex)
        {
            evidence.AppendLine($"- exception: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            await WriteReportAsync(passed, legs, evidence.ToString(), wall.Elapsed.TotalSeconds,
                bots, start, played, resumed, watches, cost);
            foreach (var key in new[] { "AAEMU_STARTUP_ARMING", "AAEMU_STARTUP_BOT_COUNT", "AAEMU_PRESENCE_MAX_BOTS",
                         "AAEMU_QUEST_BOOTSTRAP_ENABLED", "AAEMU_PRESENCE_IDLE_WAKE", "AAEMU_BOT_CTRL", "AAEMU_BOT_CTRL_TOKEN" })
                Environment.SetEnvironmentVariable(key, null);
            try
            {
                E2eStack.CleanupBotRows(ManagedAccounts);
            }
            catch
            {
                // Best-effort; the isolated lane DB is disposable.
            }
        }
    }

    private static string Progress(string name, Saved from, Saved to)
        => $"{name}: level {from.Level}→{to.Level}, xp {from.Experience}→{to.Experience}, "
            + $"completed quests {from.CompletedQuests}→{to.CompletedQuests}, objective counters {from.ObjectiveProgress}→{to.ObjectiveProgress}";

    private static async Task SampleAsync(HttpClient client, Dictionary<string, Watch> watches)
    {
        var now = DateTime.UtcNow;
        foreach (var watch in watches.Values)
        {
            JsonElement brain;
            try
            {
                brain = await GetBrainAsync(client, watch.Name);
            }
            catch
            {
                continue;
            }
            var position = ReadPosition(brain);
            var activity = Str(brain, "Activity") ?? "(none)";
            watch.Activities[activity] = watch.Activities.GetValueOrDefault(activity) + 1;
            if (Dist(position, watch.LastPosition) > 0.5f)
            {
                watch.LastPosition = position;
                watch.LastMovedUtc = now;
            }
            watch.LongestStillSeconds = Math.Max(watch.LongestStillSeconds, (now - watch.LastMovedUtc).TotalSeconds);
        }
    }

    /// <summary>Saved level, XP and completed-quest count (set bits in the completed blocks) per bot.</summary>
    private static Dictionary<uint, Saved> ReadSaved(IEnumerable<(string Name, uint Id, string State)> bots)
    {
        var result = new Dictionary<uint, Saved>();
        using var conn = E2eStack.OpenDb("aaemu_game");
        foreach (var (name, id, _) in bots)
        {
            int level;
            long experience;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT level, experience FROM characters WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    continue;
                level = reader.GetInt32(0);
                experience = reader.GetInt64(1);
            }

            var completed = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT data FROM completed_quests WHERE owner = @id";
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    completed += ((byte[])reader[0]).Sum(b => System.Numerics.BitOperations.PopCount(b));
            }

            // In-progress objective counters: the saved quest blob starts with
            // the quest's five Int32 objective counts (the same layout the
            // bridge's observe decodes from Quest.WriteData).
            long objectives = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT data FROM quests WHERE owner = @id";
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var data = (byte[])reader[0];
                    for (var i = 0; i + 4 <= Math.Min(data.Length, 20); i += 4)
                        objectives += BitConverter.ToInt32(data, i);
                }
            }

            result[id] = new Saved(id, name, level, experience, completed, objectives);
        }

        return result;
    }

    private static object Cost(string log, IEnumerable<(string Name, uint Id, string State)> bots)
    {
        var p95 = Regex.Matches(log, @"Tick metrics: invoke p50=([\d.]+)ms p95=([\d.]+)ms max=([\d.]+)ms")
            .Select(m => (P95: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                Max: double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))).ToList();
        var lines = log.Split('\n');
        var noOwner = bots.ToDictionary(b => b.Name, b => lines.Count(l =>
            l.Contains($"BotGoalArbiter: bot {b.Id} activity ", StringComparison.Ordinal)
            && l.Contains("-> (none)", StringComparison.Ordinal)
            && !l.Contains("activity (none) -> (none)", StringComparison.Ordinal)));
        var defends = bots.ToDictionary(b => b.Name, b => lines.Count(l =>
            l.Contains($"BotGoalArbiter: bot {b.Id} activity ", StringComparison.Ordinal)
            && l.Contains("-> defend.self", StringComparison.Ordinal)));
        return new
        {
            tickSamples = p95.Count,
            tickP95MaxMs = p95.Count == 0 ? 0 : p95.Max(t => t.P95),
            tickP95MedianMs = p95.Count == 0 ? 0 : p95.Select(t => t.P95).OrderBy(v => v).ElementAt(p95.Count / 2),
            tickMaxMs = p95.Count == 0 ? 0 : p95.Max(t => t.Max),
            electionsToNone = noOwner,
            selfDefenceElections = defends,
        };
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
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

    private static async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail)> legs,
        string evidence, double wallSeconds, List<(string Name, uint Id, string State)> bots,
        Dictionary<uint, Saved> start, Dictionary<uint, Saved> played, Dictionary<uint, Saved> resumed,
        Dictionary<string, Watch> watches, object? cost)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        var report = new JsonObject
        {
            ["test"] = "BotPopulatedArea",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["botCount"] = BotCount,
            ["playSeconds"] = PlaySeconds,
            ["roster"] = "startup arming defaults: level-5 Nuian male, shared template spawn; nothing seeded",
            ["legs"] = new JsonArray(legs.Select(l => (JsonNode)new JsonObject
            {
                ["leg"] = l.Leg, ["passed"] = l.Passed, ["detail"] = l.Detail,
            }).ToArray()),
            ["bots"] = new JsonArray(bots.Select(b => (JsonNode)new JsonObject
            {
                ["name"] = b.Name, ["id"] = b.Id,
                ["start"] = JsonSerializer.SerializeToNode(start.GetValueOrDefault(b.Id)),
                ["afterPlay"] = JsonSerializer.SerializeToNode(played.GetValueOrDefault(b.Id)),
                ["afterRestart"] = JsonSerializer.SerializeToNode(resumed.GetValueOrDefault(b.Id)),
                ["longestStillSeconds"] = watches.TryGetValue(b.Name, out var w) ? w.LongestStillSeconds : null,
                ["activities"] = watches.TryGetValue(b.Name, out var w2) ? JsonSerializer.SerializeToNode(w2.Activities) : null,
            }).ToArray()),
            ["cost"] = cost == null ? null : JsonSerializer.SerializeToNode(cost),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
