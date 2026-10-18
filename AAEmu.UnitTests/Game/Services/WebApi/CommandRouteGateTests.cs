using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AAEmu.Game.Services.WebApi;
using AAEmu.Game.Services.WebApi.Controllers;

namespace AAEmu.UnitTests.Game.Services.WebApi;

/// <summary>
/// L1-k rig for the generic GM command route (POST /api/commands/{name}). The
/// route runs a command line as any online character, so it must carry the same
/// fail-closed operator gate as the bot control routes.
///
/// These checks drive a REAL WebApi listener over loopback HTTP: the production
/// route table, the production session and the production gate answer the
/// request, so the refusal is observed on the wire rather than by reading the
/// controller. The gate is the first thing the route does, so a refused request
/// never reaches the world lookups and needs no game state.
/// </summary>
[NotInParallel]
public class CommandRouteGateTests
{
    private const string EnvFlag = "AAEMU_BOT_CTRL";
    private const string EnvToken = "AAEMU_BOT_CTRL_TOKEN";
    private const string Token = "command-gate-token";

    [Test]
    public async Task ExecuteCommand_OperatorApiDisabled_IsNotFound()
    {
        using var scope = new EnvScope(flag: null, token: null);
        using var host = await WebApiHost.StartAsync();

        var (status, _) = await host.PostCommandAsync(null);

        await Assert.That(status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ExecuteCommand_EnabledWithoutToken_IsUnauthorized()
    {
        using var scope = new EnvScope(flag: "1", token: Token);
        using var host = await WebApiHost.StartAsync();

        var (status, _) = await host.PostCommandAsync(null);

        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ExecuteCommand_EnabledWithWrongToken_IsUnauthorized()
    {
        using var scope = new EnvScope(flag: "1", token: Token);
        using var host = await WebApiHost.StartAsync();

        var (status, _) = await host.PostCommandAsync("definitely-not-the-token");

        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ShippedConfig_WebApiBindsLoopback()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null &&
               !Directory.Exists(Path.Combine(dir.FullName, ".git")) &&
               !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        var path = Path.Combine(dir!.FullName, "AAEmu.Game", "Config.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var host = document.RootElement.GetProperty("WebApiNetwork").GetProperty("Host").GetString();

        // The shipped default must not expose the operator API on every interface.
        await Assert.That(host).IsEqualTo("127.0.0.1");
    }

    // ---------------------------------------------------------------- rig

    /// <summary>Sets/restores the process environment the gate reads per request.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string? _previousFlag;
        private readonly string? _previousToken;

        public EnvScope(string? flag, string? token)
        {
            _previousFlag = Environment.GetEnvironmentVariable(EnvFlag);
            _previousToken = Environment.GetEnvironmentVariable(EnvToken);
            Environment.SetEnvironmentVariable(EnvFlag, flag);
            Environment.SetEnvironmentVariable(EnvToken, token);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvFlag, _previousFlag);
            Environment.SetEnvironmentVariable(EnvToken, _previousToken);
        }
    }

    /// <summary>A real loopback listener on a free port, stopped on dispose.</summary>
    private sealed class WebApiHost : IDisposable
    {
        private readonly WebApiServer _server;
        private readonly int _port;

        private WebApiHost(WebApiServer server, int port)
        {
            _server = server;
            _port = port;
        }

        public static async Task<WebApiHost> StartAsync()
        {
            var port = FreePort();

            // The production route table cannot be discovered wholesale inside the
            // unit-test assembly (the test project's own IController fakes carry
            // colliding routes), so the rig registers the real command route — the
            // production attribute path and the production controller type — onto a
            // production RouteMapper.
            var mapper = new RouteMapper();
            var method = typeof(CommandController).GetMethod(nameof(CommandController.ExecuteCommand))
                         ?? throw new InvalidOperationException("CommandController.ExecuteCommand not found");
            mapper.RegisterRoute("/api/commands/([^/]+)", HttpMethod.Post, method);

            var server = new WebApiServer(IPAddress.Loopback, port, mapper);
            if (!server.Start())
                throw new InvalidOperationException($"WebApi listener did not start on port {port}");

            var host = new WebApiHost(server, port);
            await host.WaitUntilListeningAsync();
            return host;
        }

        public async Task<(HttpStatusCode Status, string Body)> PostCommandAsync(string? token)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/commands/help");
            request.Content = new StringContent(
                """{"character":"NoSuchCharacter","arguments":"help"}""", Encoding.UTF8, "application/json");
            if (token != null)
                request.Headers.Add("X-Auth-Token", token);

            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private async Task WaitUntilListeningAsync()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, _port);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(20);
                }
            }

            throw new TimeoutException($"WebApi listener never came up on port {_port}");
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose() => _server.Stop();
    }
}
