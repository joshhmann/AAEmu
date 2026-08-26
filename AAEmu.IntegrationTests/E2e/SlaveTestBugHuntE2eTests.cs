using System.Text;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.Proxy;

using AAEmu.IntegrationTests.E2e;
using Xunit;
namespace AAEmu.IntegrationTests.E2e;


// BUG-HUNT repro: prod report "/slavetest" → packet errors + slaves missing clothes.
// Drives the real GM command (/testslave) as a real CSSendChatMessagePacket over the
// bot's authenticated game link, then captures every game-log error line emitted
// during and after the spawn, plus any SCSlaveCreatedPacket broadcast received.
[Collection("e2e")]
public class SlaveTestBugHuntE2eTests
{
    private const string AccountName = "stest-hunter";
    private const string BotName = "SlaveTestHunter";

    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game.log");
    [Fact]
    [Trait("Category", "e2e")]
    public async Task TestSlave_GMCommand_Spawn_CapturesErrors()
    {
        E2eStack.EnsureUp();

        using var bot = await BotNetworkSession.ConnectAsync(
            BotName, AccountName, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

        Assert.True(bot.InWorld, "bot must be in-world (real login flow)");

        var link = FishingLink.Get(bot);
        FishingLink.StopBackgroundLoops(bot);

        // ---- BASELINE window: in-world settle WITHOUT the command.
        var baselineOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
        await Task.Delay(6000);
        var baselineErrors = CountMarkers(baselineOffset, "Error writing string");
        Console.WriteLine($"[slavetest-bughunt] baseline window: {baselineErrors} marshal errors (expect 0)");

        // ---- run the GM command exactly like a client chat line
        var commandOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
        link.DrainAll(); // discard pre-command frames
        link.SendGameFrame(CSOffsets.CSSendChatMessagePacket, 1, body =>
        {
            body.Write((short)0);   // ChatType.White (say)
            body.Write((short)0);   // unk1
            body.Write(0);          // unk2
            body.Write("");         // targetName
            body.Write("/testslave");
            body.Write((byte)0);    // languageType
            body.Write(0);          // ability
        });

        await Task.Delay(6000);
        const ushort scSlaveCreated = 0x61; // SCOffsets.SCSlaveCreatedPacket
        var slaveCreatedFrames = link.DrainAll().Count(f => f.Type == scSlaveCreated);
        Console.WriteLine($"[slavetest-bughunt] SCSlaveCreatedPacket broadcasts received: {slaveCreatedFrames}");
        var afterErrors = CountMarkers(commandOffset, "Error writing string");
        Console.WriteLine($"[slavetest-bughunt] post-command window: {afterErrors} marshal errors");

        foreach (var h in ReadNewLogLines(commandOffset).Where(l =>
                     l.Contains("Error writing string") || l.Contains("Exception")).Take(30))
            Console.WriteLine("[slavetest-bughunt] LOG: " + h);

        Assert.Equal(0, baselineErrors);
        Assert.True(slaveCreatedFrames > 0,
            "no SCSlaveCreatedPacket broadcast received after /testslave — slave did not spawn");
        Assert.True(afterErrors == 0,
            $"/testslave produced {afterErrors} packet-marshal errors (baseline was {baselineErrors}) — see captured output above");
    }

    private static int CountMarkers(long startOffset, string marker)
    {
        if (!File.Exists(GameLogPath))
            return 0;
        using var fs = File.OpenRead(GameLogPath);
        if (fs.Length <= startOffset)
            return 0;
        fs.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var count = 0;
        while (reader.ReadLine() is { } line)
            if (line.Contains(marker, StringComparison.Ordinal))
                count++;
        return count;
    }

    private static List<string> ReadNewLogLines(long startOffset)
    {
        var found = new List<string>();
        if (!File.Exists(GameLogPath))
            return found;
        using var fs = File.OpenRead(GameLogPath);
        if (fs.Length <= startOffset)
            return found;
        fs.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            found.Add(line.TrimEnd());
        return found;
    }
}

/// <summary>Reflection plumbing shared with the fishing rig pattern.</summary>
internal static class FishingLink
{
    public static BotTcpLink Get(BotNetworkSession session)
        => (BotTcpLink)typeof(BotNetworkSession)
            .GetField("_game", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(session)!;

    public static void StopBackgroundLoops(BotNetworkSession session)
    {
        if (typeof(BotNetworkSession)
                .GetField("_keepAliveCts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(session) is CancellationTokenSource cts)
            cts.Cancel();
    }
}

