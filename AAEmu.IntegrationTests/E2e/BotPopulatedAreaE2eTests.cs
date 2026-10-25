using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Xunit;
using AAEmu.Game.Core.Managers.Bots;

using static AAEmu.IntegrationTests.E2e.BotStartupArmingE2eTests;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// "Small populated area" live lane: N PlayerBots armed at boot by the
/// supported startup switch, every one running the ordinary autonomous loop
/// (quest bootstrap + self-defence + roam under the one arbiter), sharing one
/// starter area. It asks whether the single-bot loop survives company:
///
///   - every bot makes its own progress (saved XP and completed quests grow);
///   - no bot stays still without observed gameplay gains for the stall window;
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

    internal static int ParseBotCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 6;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            || count is < 2 or > 50)
            throw new ArgumentOutOfRangeException(nameof(value), "E2E_POP_BOTS must be an integer from 2 to 50.");
        return count;
    }

    private static double PlaySeconds => double.TryParse(Environment.GetEnvironmentVariable("E2E_POP_PLAY_SECONDS"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Max(360, s) : 900;

    /// <summary>One autosave interval (World.json AutoSaveInterval 5 min) plus margin.</summary>
    private const double AfterRestartSeconds = 390;

    /// <summary>Maximum time standing still without observed gameplay gains.</summary>
    private const double StallSeconds = 300;

    private static string GameLog => Path.Combine(E2eStack.E2eRoot, "logs", "game.log");

    private static string RestartLog => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    private static string ReportPath => Path.Combine(E2eStack.E2eRoot, "logs", "bot-populated-area-report.json");

    private sealed record Saved(uint Id, string Name, int Level, long Experience, int CompletedQuests, long ObjectiveProgress);

    internal sealed class Watch
    {
        public required string Name { get; init; }
        public required DateTime StartedUtc { get; init; }
        public DateTime? LastObservedUtc { get; private set; }
        public int Observations { get; private set; }
        public int TimedOutObservations { get; set; }
        public int FailedObservations { get; set; }
        public double LongestObservationGapSeconds { get; private set; }
        public (float X, float Y, float Z) LastPosition { get; private set; }
        public DateTime LastMovedUtc { get; private set; }
        public double LongestStillSeconds { get; private set; }
        public Dictionary<string, int> Activities { get; } = [];
        public DateTime LastUsefulWorkUtc { get; private set; }
        public double LongestWithoutUsefulWorkSeconds { get; private set; }
        private BotQuestLoopObservation.Snapshot? _previousProgress;
        public sealed record Sample(DateTime ReceivedUtc, float X, float Y, float Z,
            BotQuestLoopObservation.Snapshot? GameplayProgress, bool ObservedGain);
        public List<Sample> Samples { get; } = [];

        private static bool Gained(BotQuestLoopObservation.Snapshot before, BotQuestLoopObservation.Snapshot after)
        {
            if (after.CharacterId != before.CharacterId || after.ObservedAtUtc <= before.ObservedAtUtc)
                return false;
            if (after.Level > before.Level || after.Experience > before.Experience
                || after.CompletedCount > before.CompletedCount)
                return true;
            foreach (var quest in after.ActiveQuests)
            {
                var prior = before.ActiveQuests.FirstOrDefault(q => q.QuestId == quest.QuestId);
                if (prior == null)
                    continue; // Accepting a quest is not earned objective progress.
                for (var i = 0; i < Math.Min(prior.Objectives.Length, quest.Objectives.Length); i++)
                    if (quest.Objectives[i] > prior.Objectives[i])
                        return true;
            }
            return after.ObjectiveItemCounts != null && before.ObjectiveItemCounts != null
                && after.ObjectiveItemCounts.Any(item => before.ObjectiveItemCounts.TryGetValue(item.Key, out var old)
                    && item.Value > old);
        }

        public void Observe((float X, float Y, float Z) position, string activity, DateTime now, BotQuestLoopObservation.Snapshot? progress = null)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new FormatException("Bot position must have finite coordinates.");
            LongestObservationGapSeconds = Math.Max(LongestObservationGapSeconds,
                (now - (LastObservedUtc ?? StartedUtc)).TotalSeconds);
            var moved = Observations == 0 || Dist(position, LastPosition) > 0.5f;
            var gained = progress != null && _previousProgress != null && Gained(_previousProgress, progress);
            if (Observations > 0)
                LongestWithoutUsefulWorkSeconds = Math.Max(LongestWithoutUsefulWorkSeconds,
                    (now - LastUsefulWorkUtc).TotalSeconds);
            if (moved || gained)
                LastUsefulWorkUtc = now;
            // Missing or stale native facts cannot create progress or replace the last good baseline.
            if (progress != null && (_previousProgress == null || progress.ObservedAtUtc > _previousProgress.ObservedAtUtc))
                _previousProgress = progress;
            Samples.Add(new Sample(now, position.X, position.Y, position.Z, progress, gained));
            if (moved)
            {
                LastPosition = position;
                LastMovedUtc = now;
            }
            LongestStillSeconds = Math.Max(LongestStillSeconds, (now - LastMovedUtc).TotalSeconds);
            LastObservedUtc = now;
            Observations++;
            Activities[activity] = Activities.GetValueOrDefault(activity) + 1;
        }

        public double ObservationGapSeconds(DateTime now) => Math.Max(LongestObservationGapSeconds,
            (now - (LastObservedUtc ?? StartedUtc)).TotalSeconds);

        public void FinishObservation(DateTime now)
        {
            LongestObservationGapSeconds = ObservationGapSeconds(now);
            if (Observations > 0)
                LongestWithoutUsefulWorkSeconds = Math.Max(LongestWithoutUsefulWorkSeconds,
                    (now - LastUsefulWorkUtc).TotalSeconds);
        }

        public bool HasContinuedWorkEvidence(DateTime now) => Observations >= 2
            && ObservationGapSeconds(now) < StallSeconds
            && Math.Max(LongestWithoutUsefulWorkSeconds, (now - LastUsefulWorkUtc).TotalSeconds) < StallSeconds;
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PopulatedArea_BotsArmedAtBoot_EachProgressesCoexists_AndResumesAfterRestart()
    {
        // Refuse unsupported populations before changing flags or starting servers.
        var botCount = ParseBotCount(Environment.GetEnvironmentVariable("E2E_POP_BOTS"));
        var managedAccounts = Enumerable.Range(1, botCount)
            .Select(i => $"bot_managed_startup_{i:D3}").ToArray();
        var playSeconds = PlaySeconds;
        var samplingSeconds = new List<double>();
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
        Environment.SetEnvironmentVariable("AAEMU_STARTUP_BOT_COUNT", botCount.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS", botCount.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_IDLE_WAKE", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        try
        {
            E2eStack.RequireFlags("AAEMU_STARTUP_ARMING", "AAEMU_STARTUP_BOT_COUNT", "AAEMU_QUEST_BOOTSTRAP_ENABLED");
            evidence.AppendLine($"Revision: {E2eStack.SourceRevision} · E2E_ROOT {E2eStack.E2eRoot} · bots {botCount} · "
                + $"play {playSeconds:0}s · startup roster: level-5 Nuian male at the template spawn (switch defaults)");
            evidence.AppendLine();

            E2eStack.EnsureUp();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));
            using var authed = NewClient(Token);

            // ------------------------------------------------------ armed, N real
            var enrolLine = await WaitForLogLineAsync(GameLog, "Startup arming: ", TimeSpan.FromSeconds(300));
            Assert.NotNull(enrolLine);
            Assert.Contains($"enrolled {botCount} bot(s)", enrolLine);
            bots = await WaitForActiveBotsAsync(authed, botCount, TimeSpan.FromSeconds(120));
            Assert.Equal(botCount, bots.Count);
            start = ReadSaved(bots);
            Assert.Equal(botCount, start.Count);
            Leg("armed", true, $"`{enrolLine.Trim()}`; active: {string.Join(", ", bots.Select(b => $"{b.Name}(id {b.Id})"))}");

            // ---------------------------------------------------------- play
            var playStart = DateTime.UtcNow;
            foreach (var (name, _, _) in bots)
                watches[name] = new Watch { Name = name, StartedUtc = playStart };
            while ((DateTime.UtcNow - playStart).TotalSeconds < playSeconds)
            {
                samplingSeconds.Add(await SampleAsync(authed, watches));
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
            samplingSeconds.Add(await SampleAsync(authed, watches));
            played = ReadSaved(bots);

            var progressed = bots.Where(b => played[b.Id].Experience > start[b.Id].Experience
                && played[b.Id].CompletedQuests > start[b.Id].CompletedQuests).ToList();
            var observationEnd = DateTime.UtcNow;
            foreach (var watch in watches.Values)
                watch.FinishObservation(observationEnd);
            var unprovedWork = watches.Values.Where(w => !w.HasContinuedWorkEvidence(observationEnd)).ToList();
            Leg("each-progresses", progressed.Count == botCount,
                string.Join(" | ", bots.Select(b => Progress(b.Name, start[b.Id], played[b.Id]))));
            Leg("none-parked", unprovedWork.Count == 0,
                string.Join(" | ", watches.Values.Select(w => $"{w.Name}: longest still {w.LongestStillSeconds:0}s, longest without movement or native gain {w.LongestWithoutUsefulWorkSeconds:0}s, observations {w.Observations}, "
                    + $"failed {w.FailedObservations}, longest observation gap {w.ObservationGapSeconds(observationEnd):0.0}s, "
                    + string.Join(",", w.Activities.OrderByDescending(a => a.Value).Take(4).Select(a => $"{a.Key}×{a.Value}")))));

            var log = await ReadSharedAsync(GameLog);
            cost = Cost(log, bots);
            Leg("cost-recorded", true, JsonSerializer.Serialize(cost));

            // ----------------------------------------------- restart, resume
            var killed = E2eStack.RestartGameServer();
            Assert.True(killed > 0, "RestartGameServer killed no live game process");
            var reEnrol = await WaitForLogLineAsync(RestartLog, "Startup arming: ", TimeSpan.FromSeconds(300));
            Assert.NotNull(reEnrol);
            var reBots = await WaitForActiveBotsAsync(authed, botCount, TimeSpan.FromSeconds(120));
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
            Leg("resumes-after-restart", resumedBots.Count == botCount,
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
                bots, start, played, resumed, watches, cost, botCount, playSeconds, samplingSeconds);
            foreach (var key in new[] { "AAEMU_STARTUP_ARMING", "AAEMU_STARTUP_BOT_COUNT", "AAEMU_PRESENCE_MAX_BOTS",
                         "AAEMU_QUEST_BOOTSTRAP_ENABLED", "AAEMU_PRESENCE_IDLE_WAKE", "AAEMU_BOT_CTRL", "AAEMU_BOT_CTRL_TOKEN" })
                Environment.SetEnvironmentVariable(key, null);
            try
            {
                E2eStack.CleanupBotRows(managedAccounts);
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

    internal static async Task<double> SampleAsync(HttpClient client, Dictionary<string, Watch> watches)
    {
        var elapsed = Stopwatch.StartNew();
        // Bound observer load and time: one failed HTTP request cannot delay
        // every later bot by the shared client's full 60-second timeout.
        await Parallel.ForEachAsync(watches.Values, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (watch, _) =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var brain = await GetBrainAsync(client, watch.Name, timeout.Token);
                    BotQuestLoopObservation.Snapshot? progress = null;
                    if (brain.TryGetProperty("GameplayProgress", out var facts) && facts.ValueKind != JsonValueKind.Null)
                        progress = facts.Deserialize<BotQuestLoopObservation.Snapshot>();
                    watch.Observe(ReadPosition(brain), Str(brain, "Activity") ?? "(none)", DateTime.UtcNow, progress);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                    or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    watch.FailedObservations++;
                    if (ex is OperationCanceledException)
                        watch.TimedOutObservations++;
                }
            });
        return elapsed.Elapsed.TotalSeconds;
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
        Dictionary<string, Watch> watches, object? cost, int botCount, double playSeconds, List<double> samplingSeconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        var report = new JsonObject
        {
            ["test"] = "BotPopulatedArea",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["botCount"] = botCount,
            ["playSeconds"] = playSeconds,
            ["sampling"] = JsonSerializer.SerializeToNode(new
            {
                batches = samplingSeconds.Count,
                totalSeconds = samplingSeconds.Sum(),
                maxBatchSeconds = samplingSeconds.Count == 0 ? 0 : samplingSeconds.Max(),
                maxConcurrentRequests = 4,
                requestTimeoutSeconds = 5,
            }),
            ["roster"] = "startup arming defaults: level-5 Nuian male, shared template spawn and normal startup supplies; no assigned quests or targets",
            ["stationaryWorkMeasure"] = "300 seconds without movement or native XP/level/completed-quest/objective/relevant-item gain; raw stationary duration retained separately; timestamped facts are sampled, not atomic",
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
                ["longestWithoutUsefulWorkSeconds"] = watches.TryGetValue(b.Name, out var useful) ? useful.LongestWithoutUsefulWorkSeconds : null,
                ["samples"] = watches.TryGetValue(b.Name, out var sampled) ? JsonSerializer.SerializeToNode(sampled.Samples) : null,
                ["longestStillSeconds"] = watches.TryGetValue(b.Name, out var w) ? w.LongestStillSeconds : null,
                ["observations"] = watches.TryGetValue(b.Name, out var observed) ? observed.Observations : 0,
                ["failedObservations"] = watches.TryGetValue(b.Name, out var failed) ? failed.FailedObservations : 0,
                ["timedOutObservations"] = watches.TryGetValue(b.Name, out var timedOut) ? timedOut.TimedOutObservations : 0,
                ["longestObservationGapSeconds"] = watches.TryGetValue(b.Name, out var gap)
                    ? gap.LongestObservationGapSeconds : null,
                ["activities"] = watches.TryGetValue(b.Name, out var w2) ? JsonSerializer.SerializeToNode(w2.Activities) : null,
            }).ToArray()),
            ["cost"] = cost == null ? null : JsonSerializer.SerializeToNode(cost),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
