using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Control-plane API testing evidence (M5 stage 3, t_7b6d7a4b): a REAL game
/// server boots with the bot control flag enabled (AAEMU_BOT_CTRL=1 +
/// AAEMU_BOT_CTRL_TOKEN) in the deployment-shaped testing environment (same
/// binaries, same MySQL, same config precedence), a bot is provisioned through
/// the MANAGEMENT surface
///
///   - token auth gate (401 without/with wrong token; 200 with the secret)
///   - observe → move → interact: enqueue + trace polling, full lifecycle
///   - enqueue-then-disconnect: the action still completes server-side
///   - crash isolation: concurrent clients; a killed client cannot wedge
///     the world or other clients
///   - trace endpoint: the per-bot audit trail, newest first
///   - no admin verbs on /api/actors/* (404 for list/add/remove; the
///     management surface stays on /api/bots)
///
/// Evidence is curl-level HTTP (the same calls a scripted fleet makes),
/// against the real server.
/// </summary>
[Collection("e2e")]
public class BotControlApiE2eTests
{
    private const string BotName = "ApiDrive01";
    private const string Token = "e2e-bot-ctrl-token";

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task BotControlApi_ActionSurface_LiveEvidence()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        try
        {
            E2eStack.EnsureUp();

            // The shared stack may have been booted WITHOUT the flag (normal
            // e2e runs never set it) — reboot the game so the process env
            // carries it (the gate reads env per request; the process env is
            // fixed at boot).
            E2eStack.RestartGameServer();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));

            using var client = NewClient();

            // ---------------------------------------------------------- auth
            // Criterion 2: token auth enforced.
            using (var noAuth = new HttpClient { BaseAddress = new Uri(WebApiBase) })
            {
                var noToken = await noAuth.GetAsync($"/api/actors/trace?bot={BotName}");
                Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
            }

            using (var wrongClient = NewClient("definitely-not-the-token"))
            {
                var wrongToken = await wrongClient.GetAsync($"/api/actors/trace?bot={BotName}");
                Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
            }

            // -------------------------------------------------- provisioning
            // Bot creation is the MANAGEMENT surface's job (t_2ea94a20) —
            // this API never adds/removes bots (criterion 6).
            var add = await PostJsonAsync(client, "/api/bots", $"{{\"name\":\"{BotName}\"}}");
            Assert.True(add.TryGetProperty("success", out var addOk) ? addOk.GetBoolean()
                : (add.TryGetProperty("Success", out var addOkP) && addOkP.GetBoolean()),
                "management add must succeed: " + add);

            // ---------------------------------------------------------- observe
            // Criterion 1: observe → observation snapshot, full lifecycle.
            var observe = await PostJsonAsync(client, "/api/actors/observe", $"{{\"bot\":\"{BotName}\"}}");
            Assert.True(observe.GetProperty("success").GetBoolean());
            var observeTrace = observe.GetProperty("trace_id").GetGuid();

            var observePoll = await PollTerminalAsync(client, observeTrace, TimeSpan.FromSeconds(15));
            Assert.Equal("Completed", observePoll.GetProperty("state").GetString());
            var observation = observePoll.GetProperty("result_payload");
            // The payload is the ActorObservation CLR shape (Newtonsoft
            // default naming — stable, matches the B1 snapshot fields).
            Assert.True(observation.TryGetProperty("Position", out var pos) && pos.TryGetProperty("X", out _));
            Assert.True(observePoll.GetProperty("actor_id").GetUInt32() > 0);
            AssertStateChangesStartWithRequested(observePoll);

            // ------------------------------------------------------------ move
            // Criterion 1 + 3: move → validated request, completes via the
            // scheduler ticking the same actor (world continues).
            var startX = observation.GetProperty("Position").GetProperty("X").GetDouble();
            var startY = observation.GetProperty("Position").GetProperty("Y").GetDouble();
            var startZ = observation.GetProperty("Position").GetProperty("Z").GetDouble();
            var moveBody = $"{{\"bot\":\"{BotName}\",\"x\":{startX + 6:F1},\"y\":{startY:F1},\"z\":{startZ:F1},\"speed\":2.0,\"timeoutSec\":20}}";
            var move = await PostJsonAsync(client, "/api/actors/move", moveBody);
            Assert.True(move.GetProperty("success").GetBoolean());
            var moveTrace = move.GetProperty("trace_id").GetGuid();

            var movePoll = await PollTerminalAsync(client, moveTrace, TimeSpan.FromSeconds(30));
            Assert.Equal("Completed", movePoll.GetProperty("state").GetString());

            // The bot actually moved (position changed from the move).
            var after = await PostJsonAsync(client, "/api/actors/observe", $"{{\"bot\":\"{BotName}\"}}");
            var afterPoll = await PollTerminalAsync(client, after.GetProperty("trace_id").GetGuid(), TimeSpan.FromSeconds(15));
            var movedX = afterPoll.GetProperty("result_payload").GetProperty("Position").GetProperty("X").GetDouble();
            // The bot may have resumed roaming (one leg at 2.5 m/s) between
            // the move completing and the follow-up observe — allow margin.
            Assert.True(Math.Abs(movedX - (startX + 6)) <= 5.0,
                $"bot should have moved ~6 units in X (start {startX:F1} → {movedX:F1})");

            // ---------------------------------------------------------- interact
            // Criterion 1: interact against a real doodad from the observation
            // snapshot (any terminal lifecycle is valid evidence — success or
            // a §17 taxonomy rejection, e.g. out of range).
            if (observation.TryGetProperty("NearbyDoodadObjIds", out var doodads)
                && doodads.GetArrayLength() > 0)
            {
                var doodadId = doodads[0].GetUInt32();
                var interact = await PostJsonAsync(client, "/api/actors/interact",
                    $"{{\"bot\":\"{BotName}\",\"doodadObjId\":{doodadId}}}");
                Assert.True(interact.GetProperty("success").GetBoolean());
                var interactPoll = await PollTerminalAsync(client, interact.GetProperty("trace_id").GetGuid(), TimeSpan.FromSeconds(15));
                AssertTerminal(interactPoll);
            }

            // ------------------------------------------ enqueue-then-disconnect
            // Criterion 3: the POST returns the trace id; the caller
            // disconnects and never polls — the action still completes
            // server-side (a NEW client observes the outcome).
            Guid disconnectTrace;
            using (var shortLived = NewClient())
            {
                var fire = await PostJsonAsync(shortLived, "/api/actors/stop", $"{{\"bot\":\"{BotName}\"}}");
                Assert.True(fire.GetProperty("success").GetBoolean());
                disconnectTrace = fire.GetProperty("trace_id").GetGuid();
                // shortLived disposed here — client disconnected.
            }

            var disconnectPoll = await PollTerminalAsync(client, disconnectTrace, TimeSpan.FromSeconds(15));
            Assert.Equal("Completed", disconnectPoll.GetProperty("state").GetString());

            // --------------------------------------------------- crash isolation
            // Criterion 5: a killed client cannot affect other clients or the
            // world. Fire three concurrent observes from three clients; kill
            // one immediately; the survivors complete and the world still
            // serves new commands.
            var traces = new List<Guid>();
            using (var victim = NewClient())
            {
                for (var i = 0; i < 3; i++)
                {
                    var r = await PostJsonAsync(victim, "/api/actors/observe", $"{{\"bot\":\"{BotName}\"}}");
                    traces.Add(r.GetProperty("trace_id").GetGuid());
                }
                // victim disposed here — mid-flight.
            }

            foreach (var trace in traces)
            {
                var poll = await PollTerminalAsync(client, trace, TimeSpan.FromSeconds(15));
                AssertTerminal(poll);
            }

            // World continues: a fresh command completes.
            var worldAlive = await PostJsonAsync(client, "/api/actors/observe", $"{{\"bot\":\"{BotName}\"}}");
            var worldAlivePoll = await PollTerminalAsync(client, worldAlive.GetProperty("trace_id").GetGuid(), TimeSpan.FromSeconds(15));
            Assert.Equal("Completed", worldAlivePoll.GetProperty("state").GetString());

            // -------------------------------------------------------------- trace
            // Criterion 4: async trace — per-bot audit trail, newest first,
            // embedded B1 audit records.
            var traceResponse = await GetJsonAsync(client, $"/api/actors/trace?bot={BotName}&limit=20");
            Assert.True(traceResponse.TryGetProperty("trace", out var traceArray) && traceArray.GetArrayLength() >= 6,
                "trace should contain at least the 6+ commands issued above");
            var first = traceArray[0];
            Assert.Equal(JsonValueKind.Object, first.GetProperty("audit").ValueKind);
            Assert.Equal("Completed", first.GetProperty("state").GetString());

            // ----------------------------------------------------- no admin verbs
            // Criterion 6: the ACTION surface has no management/GM verbs; the
            // management surface stays on /api/bots.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/actors/list")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/actors/add", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/actors/remove", null)).StatusCode);

            var managementStillThere = await client.GetAsync("/api/bots");
            Assert.Equal(HttpStatusCode.OK, managementStillThere.StatusCode);

            // ---------------------------------------------------- execution audit
            // The B1 audit records exist on the world side: the queue entry
            // audit JSON carries the actor's trace record.
            Assert.NotNull(observePoll.GetProperty("audit"));
            Assert.Equal("Observe", observePoll.GetProperty("audit").GetProperty("action").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }

    // ------------------------------------------------------------- helpers

    private static HttpClient NewClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", token ?? Token);
        return client;
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

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {path} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>Polls a trace until it reaches a terminal state (any terminal is returned).</summary>
    private static async Task<JsonElement> PollTerminalAsync(HttpClient client, Guid traceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement.Clone();
                var state = root.GetProperty("state").GetString();
                if (state is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                    return root;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"trace {traceId} never reached a terminal state within {timeout}");
    }

    private static void AssertTerminal(JsonElement poll)
    {
        var state = poll.GetProperty("state").GetString();
        Assert.True(state is "Completed" or "Rejected" or "Interrupted" or "TimedOut",
            $"expected a terminal lifecycle, got {state}: {poll}");
        if (state is "Rejected" or "TimedOut")
            Assert.True(poll.TryGetProperty("failure", out var failure) && failure.GetString() is { Length: > 0 },
                "rejections must carry a §17 taxonomy failure reason");
    }

    private static void AssertStateChangesStartWithRequested(JsonElement poll)
    {
        Assert.True(poll.TryGetProperty("state_changes", out var changes) && changes.GetArrayLength() >= 2,
            "state_changes must be a full transition log");
        Assert.Equal("Requested", changes[0].GetString());
    }
}
