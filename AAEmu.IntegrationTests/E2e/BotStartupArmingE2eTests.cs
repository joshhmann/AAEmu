using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// T2 live-lane evidence (plan §7 T2 row / §8 live-lane row): supported arming
/// on a REAL server, with the switch this deployment actually reads.
///
/// The lane boots the stack with <c>AAEMU_STARTUP_ARMING=1</c> and
/// <c>AAEMU_STARTUP_BOT_COUNT=2</c> — the two keys a deployment sets, absent
/// from the shipped default — and then observes the whole switch:
///
///   - boot arms the host through the ONE owner and enrols exactly N bots
///   - the enrolment is real: two managed accounts own one character each and
///     the bot plane reports both Active
///   - the bots WALK (live positions move and their wake counter advances)
///   - a REAL process restart re-enrols the SAME characters (same account ids,
///     same character ids, no third row): the roster index is its managed
///     account, not a fresh name generated per boot
///   - the token-gated host stop disarms through the same one owner and
///     converges: every enrolled bot deactivated, none left Active, no step
///     queued for a host that is no longer running
///
/// Nothing is seeded and nothing is repaired in-run: the bots are provisioned
/// by the switch under test through the ordinary provisioning path, and the
/// only state the lane writes is the disarm it is asserting.
///
/// The host-shutdown ordering of the same owner (GameService.StopAsync →
/// DisarmBotHost before SaveManager/TaskManager/networks) is stage A evidence
/// (E312) and is deliberately NOT re-proved here: this lane uses the second
/// production entry point of the same disarm, the token-gated control route.
/// </summary>
[Collection("e2e")]
public class BotStartupArmingE2eTests
{
    private const int BotCount = 2;
    private const string Token = "e2e-arming-token";

    /// <summary>The roster identity of the switch: managed account per index.</summary>
    private static readonly string[] ManagedAccounts =
    {
        "bot_managed_startup_001", "bot_managed_startup_002"
    };

    /// <summary>Roam speed is 2.5 m/s, so a walking bot covers far more than
    /// this inside the wait window; a parked one covers nothing.</summary>
    private const float MovedMeters = 0.5f;

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    private static string GameLog => Path.Combine(E2eStack.E2eRoot, "logs", "game.log");

    private static string RestartLog => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    private static string ReportPath => Path.Combine(E2eStack.E2eRoot, "logs", "bot-startup-arming-lane-report.json");

    private sealed record RosterSnapshot(
        List<(uint Id, string Username)> Accounts,
        List<(uint Id, uint AccountId, string Name)> Characters);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task BotStartupArming_ArmsTwoAtBoot_ReEnrolsWithoutDuplicates_AndDisarmsAtStop()
    {
        var wall = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({wall.Elapsed.TotalSeconds:0.0}s): {detail}");
        }

        var passed = false;

        // The switch under test is read from the process environment (the game
        // server inherits the runner env at boot), which is how a deployment
        // turns it on. The operator token is what the lane reads the bot plane
        // with — the same gate every other lane uses.
        Environment.SetEnvironmentVariable("AAEMU_STARTUP_ARMING", "1");
        Environment.SetEnvironmentVariable("AAEMU_STARTUP_BOT_COUNT", BotCount.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        try
        {
            E2eStack.RequireFlags("AAEMU_STARTUP_ARMING", "AAEMU_STARTUP_BOT_COUNT");
            evidence.AppendLine($"Lane manifest: {string.Join("; ", E2eStack.CapabilityManifest()
                .Where(f => f.Name.StartsWith("AAEMU_STARTUP", StringComparison.Ordinal) || f.Name == "AAEMU_BOT_CTRL")
                .Select(f => $"{f.Name}={f.Value ?? "<unset>"} ({f.Source})"))}");
            evidence.AppendLine($"Revision: {E2eStack.SourceRevision} · E2E_ROOT {E2eStack.E2eRoot}");
            evidence.AppendLine();

            E2eStack.EnsureUp();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));
            using var authed = NewClient(Token);

            // ---------------------------------------------------- armed at boot
            // The lane must run the E2E bridge, and the bridge arms the host at
            // module-init (before the world is ready), so on a lane the armer is
            // "e2e-bridge" and the switch's line reads "already armed by
            // e2e-bridge; startup-arming needs no second arming". Either variant
            // proves the host is armed at boot and that the switch ran; what the
            // switch OWNS is the enrollment, which the next leg checks against
            // real rows. A deployment with the bridge off (the shipped default)
            // gets the switch as the first armer — pinned by the unit/rig.
            var armedLine = await WaitForLogLineAsync(GameLog, "Bot host armed by ", TimeSpan.FromSeconds(240));
            Assert.NotNull(armedLine);
            var enrolLine = await WaitForLogLineAsync(GameLog, "Startup arming: ", TimeSpan.FromSeconds(60));
            Assert.NotNull(enrolLine);
            // The report states the count the enrollment actually did, not the
            // roster size (review D1) — on a lane the bridge armed first, so the
            // line is "already armed by e2e-bridge; enrolled 2 bot(s) of a
            // roster of up to 2".
            Assert.Contains($"enrolled {BotCount} bot(s) of a roster of up to {BotCount}", enrolLine);
            Leg("boot-armed", true, $"boot log: `{armedLine.Trim()}` / `{enrolLine.Trim()}`");

            // -------------------------------------------------- enrolled, real
            var bots = await WaitForActiveBotsAsync(authed, BotCount, TimeSpan.FromSeconds(90));
            Assert.Equal(BotCount, bots.Count);
            Assert.Equal(BotCount, bots.Select(b => b.Id).Distinct().Count());
            Assert.Equal(BotCount, bots.Select(b => b.Name).Distinct().Count());

            var preRoster = SnapshotRoster();
            Assert.Equal(BotCount, preRoster.Accounts.Count);
            Assert.Equal(BotCount, preRoster.Characters.Count);
            foreach (var (name, id, _) in bots)
            {
                var row = preRoster.Characters.SingleOrDefault(c => c.Id == id);
                Assert.True(row.Name == name,
                    $"bot plane reports {name} (id {id}) but no managed account row matches: "
                    + string.Join(" | ", preRoster.Characters.Select(c => $"{c.Id}:{c.Name}")));
                Assert.Equal(1, preRoster.Characters.Count(c => c.AccountId == row.AccountId));
            }

            Leg("enrolled-two", true,
                $"{bots.Count} active: {string.Join(", ", bots.Select(b => $"{b.Name}(id {b.Id}, {b.State})"))}; "
                + $"accounts {string.Join(", ", preRoster.Accounts.Select(a => $"{a.Username}=id {a.Id}"))} own one character each");

            // ------------------------------------------------------------ walking
            var walks = new List<string>();
            foreach (var (name, id, _) in bots)
            {
                var walk = await WaitForMoveAsync(authed, name, MovedMeters, TimeSpan.FromSeconds(30));
                Assert.True(walk.Moved,
                    $"bot {name} (id {id}) never moved from ({walk.From.X:F1},{walk.From.Y:F1},{walk.From.Z:F1}) — "
                    + $"a bot that is enrolled but parked is not armed");
                Assert.True(walk.WakeEnd > walk.WakeStart,
                    $"bot {name} (id {id}) did not advance its wake sequence ({walk.WakeStart} → {walk.WakeEnd})");
                walks.Add($"{name}: {Dist(walk.From, walk.To):F1} m, wake {walk.WakeStart}→{walk.WakeEnd}, activity {walk.Activity ?? "(none)"}");
            }
            Leg("walking", true, string.Join(" | ", walks));

            // ------------------------------------------------ restart, same rows
            // The hole this leg exists for (audit L1-e): a boot that generates a
            // fresh name per index provisions ANOTHER character row every boot.
            // The roster index is its managed account, so the second boot must
            // re-enrol the characters the accounts already own.
            var killedPid = E2eStack.RestartGameServer();
            Assert.True(killedPid > 0,
                "RestartGameServer killed no live game process — a silent no-kill must not pass as a restart");

            var reArmed = await WaitForLogLineAsync(RestartLog, "Bot host armed by ", TimeSpan.FromSeconds(240));
            Assert.NotNull(reArmed);
            var reEnrolled = await WaitForLogLineAsync(RestartLog, "Startup arming: ", TimeSpan.FromSeconds(60));
            Assert.NotNull(reEnrolled);

            var postRoster = SnapshotRoster();
            Assert.Equal(preRoster.Accounts.Count, postRoster.Accounts.Count);
            Assert.Equal(preRoster.Characters.Count, postRoster.Characters.Count);
            Assert.Equal(
                preRoster.Accounts.OrderBy(a => a.Id),
                postRoster.Accounts.OrderBy(a => a.Id));
            Assert.Equal(
                preRoster.Characters.OrderBy(c => c.Id).Select(c => (c.Id, c.AccountId, c.Name)),
                postRoster.Characters.OrderBy(c => c.Id).Select(c => (c.Id, c.AccountId, c.Name)));

            var restartText = await File.ReadAllTextAsync(RestartLog);
            Assert.Contains("adopted existing character", restartText);
            Assert.DoesNotContain("NameAlreadyExists", restartText);
            Assert.DoesNotContain("failed to provision", restartText);

            var reBots = await WaitForActiveBotsAsync(authed, BotCount, TimeSpan.FromSeconds(90));
            Assert.Equal(
                bots.OrderBy(b => b.Id).Select(b => (b.Id, b.Name)),
                reBots.OrderBy(b => b.Id).Select(b => (b.Id, b.Name)));
            Leg("restart-reconciles", true,
                $"killed pid {killedPid}; second boot re-enrolled the SAME {reBots.Count} characters "
                + $"({string.Join(", ", reBots.Select(b => $"{b.Name}(id {b.Id})"))}) — "
                + $"{postRoster.Characters.Count} rows for {postRoster.Accounts.Count} accounts, adoption logged, no duplicate row");

            // ------------------------------------------------------- disarm at stop
            // The second production entry point of the same one disarm owner
            // (the first is host shutdown, stage A evidence E312). It must
            // converge: every enrolled bot deactivated, none left Active.
            var stop = await PostJsonAsync(authed, "/api/bots/host/stop", "{\"reason\":\"lane-stop\"}");
            Assert.True(stop.GetProperty("Success").GetBoolean(), "host stop must report success: " + stop);
            var report = stop.GetProperty("Report");
            var deactivated = report.GetProperty("BotsDeactivated").GetInt32();
            var failed = report.GetProperty("BotsFailed").GetInt32();
            var converged = report.GetProperty("Converged").GetBoolean();
            Assert.True(converged, "the teardown must converge: " + report);
            Assert.Equal(BotCount, deactivated);
            Assert.Equal(0, failed);

            var stopLine = await WaitForLogLineAsync(RestartLog, "BotControl: host stop (lane-stop)", TimeSpan.FromSeconds(30));
            Assert.NotNull(stopLine);

            var afterStop = await StatusBotsAsync(authed);
            Assert.DoesNotContain(afterStop, b => b.State == "Active");
            Assert.Equal(BotCount, afterStop.Count);
            Assert.Equal(BotCount, SnapshotRoster().Characters.Count);
            Leg("stop-disarmed", true,
                $"POST /api/bots/host/stop → {report.GetProperty("Reason").GetString()}: deactivated={deactivated} "
                + $"failed={failed} dropped={report.GetProperty("QueuedStepsDropped").GetInt32()} converged={converged}; "
                + $"bot plane now {string.Join(", ", afterStop.Select(b => $"{b.Name}={b.State}"))}; rows kept ({SnapshotRoster().Characters.Count})");

            // ------------------------------------------- absent keys, no arming
            // The card's shipped-default clause: with the two keys ABSENT nothing
            // enrols. The host itself is still armed here by the bridge (this lane
            // must run it), so the observable is the ENROLLMENT: the same boot flow
            // that provisioned two managed accounts a moment ago must now provision
            // none, create no character and leave the roster untouched.
            Environment.SetEnvironmentVariable("AAEMU_STARTUP_ARMING", null);
            Environment.SetEnvironmentVariable("AAEMU_STARTUP_BOT_COUNT", null);
            var controlKilled = E2eStack.RestartGameServer();
            Assert.True(controlKilled > 0, "the keys-absent control boot killed no live game process");
            var worldLine = await WaitForLogLineAsync(RestartLog, "Gimmicks spawned", TimeSpan.FromSeconds(240));
            Assert.NotNull(worldLine);
            // The switch logs ~1 s after world load when the keys are on; a 45 s
            // margin makes the absence below a real "the moment has passed".
            await Task.Delay(TimeSpan.FromSeconds(45));
            var controlText = await File.ReadAllTextAsync(RestartLog);
            Assert.DoesNotContain("Startup arming:", controlText);
            Assert.DoesNotContain("created managed bot account", controlText);
            Assert.DoesNotContain("provisioning 2 citizen bots", controlText);
            var controlRoster = SnapshotRoster();
            Assert.Equal(preRoster.Accounts.Count, controlRoster.Accounts.Count);
            Assert.Equal(preRoster.Characters.Count, controlRoster.Characters.Count);
            Assert.Equal(
                preRoster.Characters.OrderBy(c => c.Id).Select(c => (c.Id, c.AccountId, c.Name)),
                controlRoster.Characters.OrderBy(c => c.Id).Select(c => (c.Id, c.AccountId, c.Name)));
            Leg("keys-absent-control", true,
                $"keys cleared, third boot killed pid {controlKilled}: no `Startup arming:` line, no managed account created, "
                + $"no citizen bots provisioned, roster still {controlRoster.Characters.Count} rows for {controlRoster.Accounts.Count} accounts "
                + "(the host stays armed by the bridge on a lane — the enrollment is the switch's observable)");

            passed = true;
        }
        catch (Exception ex)
        {
            evidence.AppendLine($"- exception: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            await WriteReportAsync(passed, legs, evidence.ToString(), wall.Elapsed.TotalSeconds);
            Environment.SetEnvironmentVariable("AAEMU_STARTUP_ARMING", null);
            Environment.SetEnvironmentVariable("AAEMU_STARTUP_BOT_COUNT", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
            try
            {
                E2eStack.CleanupBotRows(ManagedAccounts);
            }
            catch
            {
                // Cleanup is best-effort; the isolated lane DB is disposable.
            }
        }
    }

    // --------------------------------------------------------------- reads

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {path} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<List<(string Name, uint Id, string State)>> StatusBotsAsync(HttpClient client)
    {
        var status = await GetJsonAsync(client, "/api/bots/status");
        var bots = new List<(string Name, uint Id, string State)>();
        if (status.TryGetProperty("Bots", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                bots.Add((
                    row.GetProperty("Name").GetString() ?? "",
                    row.GetProperty("Id").GetUInt32(),
                    row.GetProperty("State").GetString() ?? ""));
            }
        }

        return bots;
    }

    private static async Task<List<(string Name, uint Id, string State)>> WaitForActiveBotsAsync(
        HttpClient client, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = new List<(string Name, uint Id, string State)>();
        while (DateTime.UtcNow < deadline)
        {
            last = await StatusBotsAsync(client);
            if (last.Count(b => b.State == "Active") >= count)
                return last.Where(b => b.State == "Active").ToList();
            await Task.Delay(1000);
        }

        return last.Where(b => b.State == "Active").ToList();
    }

    private static async Task<JsonElement> GetBrainAsync(HttpClient client, string bot)
    {
        using var response = await client.GetAsync($"/api/bots/brain/{bot}");
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET /api/bots/brain/{bot} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private sealed record MoveSample(bool Moved, (float X, float Y, float Z) From, (float X, float Y, float Z) To,
        long WakeStart, long WakeEnd, string? Activity);

    /// <summary>
    /// Samples the bot's live position and wake counter until it has moved
    /// (roam speed 2.5 m/s, so the window is generous for a walking bot and
    /// empty for a parked one).
    /// </summary>
    private static async Task<MoveSample> WaitForMoveAsync(
        HttpClient client, string bot, float minMeters, TimeSpan timeout)
    {
        var first = await GetBrainAsync(client, bot);
        var from = ReadPosition(first);
        var wakeStart = WakeOf(first);
        var activity = Str(first, "Activity");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var brain = await GetBrainAsync(client, bot);
            var to = ReadPosition(brain);
            var wakeEnd = WakeOf(brain);
            if (Dist(from, to) > minMeters && wakeEnd > wakeStart)
                return new MoveSample(true, from, to, wakeStart, wakeEnd, Str(brain, "Activity") ?? activity);
            await Task.Delay(1000);
        }

        var last = await GetBrainAsync(client, bot);
        return new MoveSample(false, from, ReadPosition(last), wakeStart, WakeOf(last), Str(last, "Activity") ?? activity);
    }

    private static (float X, float Y, float Z) ReadPosition(JsonElement brain)
        => (brain.GetProperty("X").GetSingle(), brain.GetProperty("Y").GetSingle(), brain.GetProperty("Z").GetSingle());

    private static long WakeOf(JsonElement brain)
        => brain.TryGetProperty("WakeSequence", out var wake) && wake.ValueKind == JsonValueKind.Number
            ? wake.GetInt64()
            : 0;

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float Dist((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ---------------------------------------------------------- persistence

    /// <summary>
    /// The roster identity of the switch, read from the two stores that own
    /// it: the managed accounts (login) and the characters they own (game).
    /// </summary>
    private static RosterSnapshot SnapshotRoster()
    {
        var accounts = new List<(uint Id, string Username)>();
        using (var conn = E2eStack.OpenDb("aaemu_login"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, username FROM users WHERE username LIKE 'bot_managed_startup%' ORDER BY id";
            cmd.Prepare();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                accounts.Add((reader.GetUInt32(0), reader.GetString(1)));
        }

        var characters = new List<(uint Id, uint AccountId, string Name)>();
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.id, c.account_id, c.name FROM characters c " +
                "JOIN aaemu_login.users u ON u.id = c.account_id " +
                "WHERE u.username LIKE 'bot_managed_startup%' ORDER BY c.id";
            cmd.Prepare();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                characters.Add((reader.GetUInt32(0), reader.GetUInt32(1), reader.GetString(2)));
        }

        return new RosterSnapshot(accounts, characters);
    }

    // -------------------------------------------------------------- helpers

    private static HttpClient NewClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", token);
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

    private static async Task WaitForWebApiAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", E2eStack.WebApiPort);
                return;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }

        throw new TimeoutException("WebApi never came up on port " + E2eStack.WebApiPort);
    }

    private static async Task<string?> WaitForLogLineAsync(string logPath, string needle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                string[] lines;
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var all = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        all.Add(line);
                    lines = all.ToArray();
                }

                var hit = lines.LastOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
                if (hit != null)
                    return hit;
            }

            await Task.Delay(1000);
        }

        return null;
    }

    private static async Task WriteReportAsync(
        bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        var report = new JsonObject
        {
            ["test"] = "BotStartupArming",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["legs"] = new JsonArray(legs.Select(l => new JsonObject
            {
                ["leg"] = l.Leg,
                ["passed"] = l.Passed,
                ["detail"] = l.Detail,
                ["ms"] = l.Ms,
            }).ToArray()),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
