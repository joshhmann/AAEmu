using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AAEmu.Game.Core.Managers.Bots;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// T1 live-lane evidence (plan §5 legs / §8 live-lane row): the operator
/// assignment plane on a REAL server.
///
/// A managed bot is provisioned through the management surface, handed ONE
/// bounded activity (<c>assigned.travel</c> with a destination and an expiry)
/// through the token-gated operator route, and then observed end to end:
///
///   - the token gate refuses the assignment without the operator token
///   - the assignment is accepted, recorded and readable back
///   - the arbiter elects the assignment band and reports <c>assigned.travel</c>
///   - the bot WALKS: the leg reports arrival from its own terminal state
///   - a destination BEYOND one route leg's reach still walks and arrives
///   - the bridge frontend applies the same coordinate validation as the route
///     AND addresses the same managed bot the route addresses (its own session
///     resolution could not name a management-provisioned bot, which made the op
///     unusable for the population this lane runs)
///   - MySQL agrees with the live position through the ordinary persistence path
///   - the assignment is no longer live afterwards, so ordinary election resumes
///
/// Nothing is seeded and nothing is repaired in-run: the destination is read
/// from the bot's live position, and the walk is the ordinary route layer acting
/// on the ordinary character. The bot roams on its own after arrival, so the
/// persistence leg bounds the drift instead of demanding an exact point.
/// </summary>
[Collection("e2e")]
public class BotAssignmentE2eTests
{
    private const string BotName = "Assign01";
    private const string Token = "e2e-assignment-token";
    private const float LegMeters = 5f;

    /// <summary>
    /// The far leg: beyond the 75 m chunk envelope at the production defaults
    /// (RoamSpeed 5 m/s x RoamLegTimeout 60 s x 0.5, capped by the stuck-recovery
    /// envelope), so it must walk on a SECOND chunk rather than fail its first
    /// leg. Kept modest because the lane's terrain, not the code, decides whether
    /// a straight line of this length is walkable.
    /// </summary>
    private const float FarLegMeters = 90f;

    /// <summary>
    /// Arrival is decided by the leg's own terminal state at this radius; the
    /// walk proof parses that verdict, so later movement cannot fake it.
    /// </summary>
    private const float ArrivalRadiusMeters = 0.5f;

    /// <summary>
    /// Roaming the bot may do between the arrival wake and the forced save; it is
    /// added to the leg length when the persisted row is compared with the
    /// destination, because the bot's ordinary life resumes immediately.
    /// </summary>
    private const float PersistedDriftBoundMeters = 4f;

    /// <summary>
    /// How far the persisted row may sit from the live position sampled just
    /// before the forced save (one roam step of drift at most).
    /// </summary>
    private const float PersistedLiveBoundMeters = 3f;

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    private static string ReportPath => Path.Combine(E2eStack.E2eRoot, "logs", "bot-assignment-lane-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task BotAssignment_AssignedTravel_WalksArrivesAndReportsBack()
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
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        try
        {
            E2eStack.EnsureUp();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));
            using var authed = NewClient(Token);

            // --------------------------------------------------------- refusal
            using (var anonymous = new HttpClient { BaseAddress = new Uri(WebApiBase) })
            {
                using var refusedBody = new StringContent(
                    $"{{\"nameOrId\":\"{BotName}\",\"activity\":\"assigned.travel\",\"x\":0,\"y\":0,\"z\":0}}",
                    Encoding.UTF8, "application/json");
                using var refused = await anonymous.PostAsync("/api/bots/assign", refusedBody);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }
            Leg("gate-refuses", true, "POST /api/bots/assign without X-Auth-Token → 401");

            // ------------------------------------------------------- provision
            var add = await PostJsonAsync(authed, "/api/bots", $"{{\"name\":\"{BotName}\"}}");
            Assert.True(add.TryGetProperty("success", out var addOk) ? addOk.GetBoolean()
                    : (add.TryGetProperty("Success", out var addOkP) && addOkP.GetBoolean()),
                "management add must succeed: " + add);

            var brain = await WaitForBrainAsync(authed, BotName, TimeSpan.FromSeconds(120));
            var characterId = brain.GetProperty("CharacterId").GetUInt32();
            var start = ReadPosition(brain);
            var baselineActivity = ActivityOf(brain);
            Leg("provision", true,
                $"bot {BotName} charId={characterId} at ({start.X:F1},{start.Y:F1},{start.Z:F1}) | {Describe(brain)}");

            // The scheduler must be ticking this bot before anything else can be
            // true; a lane that never wakes would otherwise look like a broken
            // assignment.
            var wake = await WaitForTickedAsync(authed, BotName, TimeSpan.FromSeconds(30));
            Leg("ticked", wake != null,
                wake == null
                    ? "the bot never recorded a wake sequence — the scheduler is not ticking it"
                    : $"wake sequence {wake} recorded for {BotName}");

            Assert.Equal(JsonValueKind.Null, brain.GetProperty("Assignment").ValueKind);
            Assert.NotEqual(BotAssignmentActivities.Travel, baselineActivity);

            // ------------------------------------------------------ assignment
            // The destination comes from the LIVE position: four short legs are
            // tried in order so the lane does not depend on one direction being
            // walkable from wherever the bot happens to be standing.
            var offsets = new (float Dx, float Dy)[] { (LegMeters, 0f), (-LegMeters, 0f), (0f, LegMeters), (0f, -LegMeters) };
            var attempts = new List<string>();
            (float X, float Y, float Z) destination = default;
            string? arrivedReason = null;
            var sawAssignedActivity = false;
            JsonElement lastBrain = default;
            foreach (var offset in offsets)
            {
                destination = (start.X + offset.Dx, start.Y + offset.Dy, start.Z);
                var assign = await PostJsonAsync(authed, "/api/bots/assign",
                    $"{{\"nameOrId\":\"{BotName}\",\"activity\":\"assigned.travel\","
                    + $"\"x\":{destination.X:F2},\"y\":{destination.Y:F2},\"z\":{destination.Z:F2},\"expirySeconds\":120}}");
                Assert.True(assign.TryGetProperty("Success", out var accepted) && accepted.GetBoolean(),
                    "the assignment must be accepted: " + assign);
                var recorded = assign.GetProperty("Assignment");
                Assert.Equal("Accepted", recorded.GetProperty("StateName").GetString());
                Assert.Equal(BotAssignmentActivities.Travel, recorded.GetProperty("Activity").GetString());

                // The band is elected on the next wake, and a short leg finishes
                // within a second — so the read-back is sampled immediately and
                // often, not once after the walk is already over.
                var liveWatch = Stopwatch.StartNew();
                while (liveWatch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    var sample = await GetBrainAsync(authed, BotName);
                    if (ActivityOf(sample) == BotAssignmentActivities.Travel)
                    {
                        sawAssignedActivity = true;
                        break;
                    }
                    if (sample.GetProperty("Assignment").GetProperty("StateName").GetString() is "Completed" or "Failed" or "Expired")
                        break;
                    await Task.Delay(100);
                }

                // Two samples per attempt so a failure is diagnosable: what the
                // arbiter elected a moment later, and where the leg ended up.
                await Task.Delay(3000);
                var early = await GetBrainAsync(authed, BotName);
                if (ActivityOf(early) == BotAssignmentActivities.Travel)
                    sawAssignedActivity = true;
                evidence.AppendLine($"- attempt ({offset.Dx:F0},{offset.Dy:F0})m +3s: {Describe(early)}");

                var terminal = await WaitForAssignmentTerminalAsync(authed, BotName, TimeSpan.FromSeconds(60));
                sawAssignedActivity |= terminal.SawAssignedActivity;
                lastBrain = terminal.Brain;
                var state = terminal.Brain.GetProperty("Assignment").GetProperty("StateName").GetString();
                var reason = terminal.Brain.GetProperty("Assignment").GetProperty("LastReason").GetString();
                attempts.Add($"({offset.Dx:F0},{offset.Dy:F0})m → {state}: {reason}");
                evidence.AppendLine($"- attempt ({offset.Dx:F0},{offset.Dy:F0})m → {state}: {reason} | {Describe(terminal.Brain)}");
                if (state == "Completed")
                {
                    arrivedReason = reason;
                    break;
                }
            }

            Leg("arbiter-elected", sawAssignedActivity, "the brain read-back reported arbiter activity assigned.travel while the assignment was live");
            if (arrivedReason == null)
                evidence.AppendLine($"- last brain: {lastBrain.GetRawText()}");
            Assert.NotNull(arrivedReason);
            Assert.Contains("arrived", arrivedReason!);
            Leg("walk-arrived", true, string.Join(" | ", attempts));

            // -------------------------------------------------------- read-back
            var afterArrival = await GetBrainAsync(authed, BotName);
            var finalAssignment = afterArrival.GetProperty("Assignment");
            Assert.Equal("Completed", finalAssignment.GetProperty("StateName").GetString());
            Assert.Equal(JsonValueKind.False, finalAssignment.GetProperty("IsLive").ValueKind);
            var electionReason = finalAssignment.GetProperty("LastReason").GetString();
            Leg("read-back-terminal", true,
                $"GET /api/bots/brain/{BotName}: assignment {finalAssignment.GetProperty("StateName").GetString()} — {electionReason}");

            // --------------------------------------------------- persistence
            // The ordinary persistence path (the same forced save every lane
            // uses), then the row is compared with the assigned destination.
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                // The live position is sampled immediately before the save, so
                // the row can be compared with the server as well as with the
                // destination: the destination proves the walk, the live sample
                // proves the row is the ordinary persisted state.
                var liveBeforeSave = ReadPosition(await GetBrainAsync(authed, BotName));
                var landed = ForceSaveAndSample(bridge, characterId);
                var off = Dist(landed, destination);
                var moved = Dist(landed, start);
                var offLive = Dist(landed, liveBeforeSave);
                // The walk itself is proved by the leg's own arrival verdict, which
                // reports the flat distance at the moment the route layer halted the
                // bot. The row is then bounded by the leg length plus the roaming the
                // bot may do between arrival and the forced save.
                var arrivalMatch = Regex.Match(arrivedReason!, @"arrived \((\d+(?:\.\d+)?)m from the destination\)");
                Assert.True(arrivalMatch.Success, $"the arrival verdict must report its distance: {arrivedReason}");
                var arrivalDistance = float.Parse(arrivalMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.True(arrivalDistance <= ArrivalRadiusMeters,
                    $"the walk stopped {arrivalDistance:F2} m from the destination — the arrival radius is {ArrivalRadiusMeters:F1} m");
                Assert.True(moved >= 1f, $"the bot never left its start position ({moved:F2} m) — no walk happened");
                Assert.True(off <= LegMeters + PersistedDriftBoundMeters,
                    $"characters row ({landed.X:F1},{landed.Y:F1},{landed.Z:F1}) is {off:F2} m from the assigned destination "
                    + $"(bound {LegMeters + PersistedDriftBoundMeters:F1} m = the leg plus post-arrival roaming; bot moved {moved:F2} m from the start)");
                Assert.True(offLive <= PersistedLiveBoundMeters,
                    $"characters row ({landed.X:F1},{landed.Y:F1},{landed.Z:F1}) is {offLive:F2} m from the live position "
                    + $"({liveBeforeSave.X:F1},{liveBeforeSave.Y:F1},{liveBeforeSave.Z:F1}) — bound {PersistedLiveBoundMeters:F1} m");
                Leg("mysql-position", true,
                    $"characters.x/y/z ({landed.X:F1},{landed.Y:F1},{landed.Z:F1}) is {off:F2} m from the destination "
                    + $"(arrival verdict {arrivalDistance:F2} m), {offLive:F2} m from the live position and {moved:F2} m from the start");
            }

            // ------------------------------------------- ordinary election resumes
            var resumed = await WaitForAssignmentReleaseAsync(authed, BotName, TimeSpan.FromSeconds(45));
            Leg("election-resumes", resumed != null,
                resumed == null
                    ? "the arbiter still reports assigned.travel after the assignment completed"
                    : $"the assignment band released election — the arbiter now reports {resumed}");

            // -------------------------------------------- bridge frontend parity
            // The E2E bridge writes the SAME registry the token-gated HTTP route
            // writes, so it must apply the same coordinate validation: a call
            // without x/y/z used to read them as 0 and silently walk the bot to
            // the world origin (code review 2026-10-16, P3-5).
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                var malformed = bridge.CallResponse(JsonSerializer.SerializeToElement(new
                {
                    cmd = "assign",
                    bot = BotName,
                    activity = BotAssignmentActivities.Travel,
                }), timeoutMs: 15_000);
                Assert.False(malformed.GetProperty("ok").GetBoolean(),
                    "a bridge assignment without x/y/z must be refused: " + malformed);
                var malformedError = malformed.GetProperty("error").GetString();
                Assert.Contains("x, y and z", malformedError!);

                // The refusal must be the VALIDATION, not a broken frontend: the
                // same op with the bot's live coordinates is accepted through the
                // identical registry entry the HTTP route uses. The bridge
                // addresses the same MANAGED bot population the HTTP route does.
                var here = ReadPosition(await GetBrainAsync(authed, BotName));
                var bridged = bridge.Call(JsonSerializer.SerializeToElement(new
                {
                    cmd = "assign",
                    bot = BotName,
                    activity = BotAssignmentActivities.Travel,
                    x = here.X,
                    y = here.Y,
                    z = here.Z,
                    expirySeconds = 60,
                }), timeoutMs: 15_000);
                Assert.Equal(BotAssignmentActivities.Travel, bridged.GetProperty("activity").GetString());
                Leg("bridge-assign-validation", true,
                    $"bridge assign without x/y/z → \"{malformedError}\"; with the live coordinates "
                    + $"({here.X:F1},{here.Y:F1},{here.Z:F1}) → accepted (activity "
                    + $"{bridged.GetProperty("activity").GetString()}, charId {bridged.GetProperty("characterId").GetUInt32()})");
            }

            // That bridged assignment targets the bot's own position, so it must
            // end on its own before the next leg arms another one.
            var bridgedTerminal = await WaitForAssignmentTerminalAsync(authed, BotName, TimeSpan.FromSeconds(45));
            var bridgedState = bridgedTerminal.Brain.GetProperty("Assignment").GetProperty("StateName").GetString();
            evidence.AppendLine($"- bridged assignment at the live position → {bridgedState}: "
                + $"{bridgedTerminal.Brain.GetProperty("Assignment").GetProperty("LastReason").GetString()}");
            Assert.Equal("Completed", bridgedState);

            // --------------------------------------- beyond one chunk's reach
            // One route leg honestly walks RoamSpeed x RoamLegTimeout x 0.5,
            // capped by the stuck-recovery envelope — 75 m at the production
            // defaults. An assignment beyond that must walk on the NEXT chunk
            // instead of failing its first leg (code review 2026-10-16, P1-1).
            // The route is the same straight line either way, so the proof is the
            // walk itself: the bot must cover the whole distance and arrive.
            var farAttempts = new List<string>();
            var farWalked = 0f;
            foreach (var offset in new (float Dx, float Dy)[] { (FarLegMeters, 0f), (-FarLegMeters, 0f), (0f, FarLegMeters) })
            {
                var from = ReadPosition(await GetBrainAsync(authed, BotName));
                (float X, float Y, float Z) farDestination = (from.X + offset.Dx, from.Y + offset.Dy, from.Z);
                var farAssign = await PostJsonAsync(authed, "/api/bots/assign",
                    $"{{\"nameOrId\":\"{BotName}\",\"activity\":\"assigned.travel\","
                    + $"\"x\":{farDestination.X:F2},\"y\":{farDestination.Y:F2},\"z\":{farDestination.Z:F2},\"expirySeconds\":300}}");
                Assert.True(farAssign.TryGetProperty("Success", out var farAccepted) && farAccepted.GetBoolean(),
                    "the far assignment must be accepted: " + farAssign);

                var farTerminal = await WaitForAssignmentTerminalAsync(authed, BotName, TimeSpan.FromSeconds(90));
                var farState = farTerminal.Brain.GetProperty("Assignment").GetProperty("StateName").GetString();
                var farReason = farTerminal.Brain.GetProperty("Assignment").GetProperty("LastReason").GetString();
                var farLanded = ReadPosition(farTerminal.Brain);
                var walked = Dist(farLanded, from);
                farAttempts.Add($"({offset.Dx:F0},{offset.Dy:F0})m → {farState}: {farReason} (walked {walked:F1} m)");
                evidence.AppendLine($"- far attempt ({offset.Dx:F0},{offset.Dy:F0})m → {farState}: {farReason} | walked {walked:F1} m");
                if (farState != "Completed")
                    continue;

                var farMatch = Regex.Match(farReason!, @"arrived \((\d+(?:\.\d+)?)m from the destination\)");
                Assert.True(farMatch.Success, $"the far arrival verdict must report its distance: {farReason}");
                var farArrival = float.Parse(farMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.True(farArrival <= ArrivalRadiusMeters,
                    $"the far walk stopped {farArrival:F2} m from the destination — the arrival radius is {ArrivalRadiusMeters:F1} m");
                farWalked = walked;
                break;
            }
            Assert.True(farWalked >= FarLegMeters - PersistedDriftBoundMeters,
                $"{FarLegMeters:F0} m is beyond one route leg's reach (the chunk envelope is 75 m at the production "
                + $"defaults), so the walk must cover the whole distance: {string.Join(" | ", farAttempts)}");
            Leg("far-destination-chunked", true,
                $"a {FarLegMeters:F0} m assignment walked past the chunk envelope and arrived: {string.Join(" | ", farAttempts)}");

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
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }

    // --------------------------------------------------------------- reads

    private static async Task<JsonElement> GetBrainAsync(HttpClient client, string bot)
    {
        using var response = await client.GetAsync($"/api/bots/brain/{bot}");
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET /api/bots/brain/{bot} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Waits for the per-bot wake counter to appear — the proof the production
    /// scheduler is ticking this bot at all.
    /// </summary>
    private static async Task<long?> WaitForTickedAsync(HttpClient client, string bot, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var brain = await GetBrainAsync(client, bot);
            if (brain.TryGetProperty("WakeSequence", out var wake) && wake.ValueKind == JsonValueKind.Number)
                return wake.GetInt64();
            await Task.Delay(1000);
        }

        return null;
    }

    private static async Task<JsonElement> WaitForBrainAsync(HttpClient client, string bot, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var brain = await GetBrainAsync(client, bot);
                if (brain.TryGetProperty("CharacterId", out var id) && id.GetUInt32() != 0)
                    return brain;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"bot {bot} never appeared in the brain read-back: {last?.Message}");
    }

    /// <summary>
    /// Polls the read-back until the recorded assignment reaches a terminal
    /// state, remembering whether the arbiter ever reported the assignment
    /// band's activity (the election leg).
    /// </summary>
    private static async Task<(JsonElement Brain, bool SawAssignedActivity)> WaitForAssignmentTerminalAsync(
        HttpClient client, string bot, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var sawAssigned = false;
        JsonElement lastBrain = default;
        while (DateTime.UtcNow < deadline)
        {
            var brain = await GetBrainAsync(client, bot);
            lastBrain = brain;
            if (ActivityOf(brain) == BotAssignmentActivities.Travel)
                sawAssigned = true;

            if (brain.TryGetProperty("Assignment", out var assignment) && assignment.ValueKind == JsonValueKind.Object)
            {
                var state = assignment.GetProperty("StateName").GetString();
                if (state is "Completed" or "Failed" or "Expired")
                    return (brain, sawAssigned);
            }

            await Task.Delay(500);
        }

        return (lastBrain, sawAssigned);
    }

    /// <summary>
    /// Waits until the assignment no longer holds election. "(none)" is a valid
    /// answer: this bot's ordinary life is driven by its own roam route rather
    /// than by another arbiter band, so the honest claim is that the assignment
    /// band released election — not that some other module took over.
    /// </summary>
    private static async Task<string?> WaitForAssignmentReleaseAsync(HttpClient client, string bot, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var activity = ActivityOf(await GetBrainAsync(client, bot));
            if (activity != BotAssignmentActivities.Travel)
                return $"'{activity ?? "(none)"}'";
            await Task.Delay(500);
        }

        return null;
    }

    private static string? ActivityOf(JsonElement brain)
        => brain.TryGetProperty("Activity", out var activity) && activity.ValueKind == JsonValueKind.String
            ? activity.GetString()
            : null;

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Compact one-line state summary for the lane evidence.</summary>
    private static string Describe(JsonElement brain)
    {
        var activity = ActivityOf(brain) ?? "(none)";
        var live = brain.TryGetProperty("LiveAction", out var action) && action.ValueKind == JsonValueKind.String
            ? $"{action.GetString()}/{Str(brain, "LiveState")}/{Str(brain, "LiveDetail")}"
            : "idle";
        var assignment = brain.TryGetProperty("Assignment", out var recorded) && recorded.ValueKind == JsonValueKind.Object
            ? $"{Str(recorded, "StateName")}: {Str(recorded, "LastReason")}"
            : "(none)";
        return $"activity={activity} live={live} assignment={assignment} status=\"{Str(brain, "StatusLine")}\"";
    }

    private static (float X, float Y, float Z) ReadPosition(JsonElement brain)
        => (brain.GetProperty("X").GetSingle(), brain.GetProperty("Y").GetSingle(), brain.GetProperty("Z").GetSingle());

    // ---------------------------------------------------------- persistence

    private static (float X, float Y, float Z) ForceSaveAndSample(BotDriveClient bridge, uint characterId)
    {
        bridge.Call("{\"cmd\":\"save\"}", 180_000);
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT x, y, z FROM characters WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"character {characterId} has no row to sample");
        return (reader.GetFloat(0), reader.GetFloat(1), reader.GetFloat(2));
    }

    private static float Dist((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
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

    private static async Task WriteReportAsync(
        bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        var report = new JsonObject
        {
            ["test"] = "BotAssignment",
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
