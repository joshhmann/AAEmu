using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// L1-k integration evidence for the generic GM command route
/// (POST /api/commands/{name}) on a REAL server.
///
/// The unit rig (AAEmu.UnitTests/Game/Services/WebApi/CommandRouteGateTests.cs)
/// proves the refusal on a real loopback listener; this lane proves the other
/// half of the contract: a token-bearing operator still reaches the command
/// layer, so the existing GM tooling path keeps working, and the bot plane's own
/// gate is unchanged.
///
/// The character is a bot provisioned through the management surface (/api/bots)
/// — a real world Character — and the command is <c>online</c>, which the shipped
/// Configurations/AccessLevels.json already grants to level 0, so a normal
/// character may run it. No permission, roster or database grant is added by this
/// lane.
/// </summary>
[Collection("e2e")]
public class CommandRouteGateE2eTests
{
    private const string BotName = "CmdGate01";
    private const string Token = "e2e-command-gate-token";

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task CommandRoute_RefusesWithoutToken_AndRunsTheCommandWithToken()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        try
        {
            E2eStack.EnsureUp();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));

            using var authed = NewClient(Token);
            var commandBody = $"{{\"character\":\"{BotName}\",\"arguments\":\"{BotName}\"}}";

            // ------------------------------------------------------- provision
            // The bot plane's own gate is unchanged: a token-bearing caller still
            // provisions through the management surface.
            var add = await PostJsonAsync(authed, "/api/bots", $"{{\"name\":\"{BotName}\"}}");
            Assert.True(add.TryGetProperty("success", out var addOk) ? addOk.GetBoolean()
                    : (add.TryGetProperty("Success", out var addOkP) && addOkP.GetBoolean()),
                "management add must still succeed behind the bot plane's gate: " + add);

            // ---------------------------------------------------------- refusal
            using (var anonymous = new HttpClient { BaseAddress = new Uri(WebApiBase) })
            {
                using var anonymousBody = new StringContent(commandBody, Encoding.UTF8, "application/json");
                using var noToken = await anonymous.PostAsync("/api/commands/online", anonymousBody);
                Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
            }

            using (var wrong = NewClient("definitely-not-the-token"))
            {
                using var wrongBody = new StringContent(commandBody, Encoding.UTF8, "application/json");
                using var wrongToken = await wrong.PostAsync("/api/commands/online", wrongBody);
                Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
            }

            // ------------------------------------------------------------- admit
            // With the operator token the same call reaches the command layer and
            // returns the command's own output.
            using var content = new StringContent(commandBody, Encoding.UTF8, "application/json");
            using var admitted = await authed.PostAsync("/api/commands/online", content);
            var body = await admitted.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
            Assert.Contains(BotName, body);
            // The `online` command prints one of two count shapes ("N players online."
            // when the filter matches everything, "Showing N/M online players."
            // otherwise), so accept either while still requiring the command's own output.
            Assert.True(
                body.Contains("players online", StringComparison.OrdinalIgnoreCase)
                || body.Contains("online players", StringComparison.OrdinalIgnoreCase),
                "the command's own output must be returned: " + body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }

    // ------------------------------------------------------------- helpers

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
}
