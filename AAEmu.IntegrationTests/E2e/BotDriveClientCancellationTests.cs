using System.Net;
using System.Net.Sockets;
using System.Text;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

public sealed class BotDriveClientCancellationTests
{
    [Fact]
    public async Task CallAsync_CancellationStopsPendingResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var bridge = new BotDriveClient(((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bridge.CallAsync("{\"cmd\":\"never-replies\"}", 5000, cancellation.Token));
    }

    [Fact]
    public async Task CallAsync_TimeoutPreservesTimeoutException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var bridge = new BotDriveClient(((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            bridge.CallAsync("{\"cmd\":\"never-replies\"}", 100));
    }

    [Fact]
    public async Task Call_SynchronousBridgeBehaviorStillWorks()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var bridge = new BotDriveClient(((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var server = Task.Run(async () =>
        {
            using var reader = new StreamReader(accepted.GetStream());
            using var writer = new StreamWriter(accepted.GetStream(), new UTF8Encoding(false))
            {
                NewLine = "\n",
                AutoFlush = true
            };
            Assert.NotNull(await reader.ReadLineAsync());
            await writer.WriteLineAsync("{\"ok\":true,\"data\":{\"pong\":true}}");
        });

        var response = bridge.Call("{\"cmd\":\"ping\"}", 5000);

        Assert.True(response.GetProperty("pong").GetBoolean());
        await server;
    }
}
