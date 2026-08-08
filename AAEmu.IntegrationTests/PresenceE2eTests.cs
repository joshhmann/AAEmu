using System.Globalization;
using System.Text;
using AAEmu.IntegrationTests.E2e;
using Xunit;

namespace AAEmu.IntegrationTests;

/// <summary>
/// PRESENCE PROOF live demo (t_6bad0654): a REAL game server boots with the
/// presence demo enabled (AAEMU_PRESENCE_DEMO=1 — the same binaries, same
/// MySQL, same config precedence as prod), provisions >=3 citizen bots
/// through the production HeadlessSession path, and a REAL client session
/// (full X2 handshake — the protocol a 1.2 client speaks) enters the world
/// at the same spawn and observes them:
///
///   - SCUnitStatePacket (0x69) for Citizen01..NN — bot visible in-world
///   - SCOneUnitMovementPacket (0x6C) from those objIds at the throttled
///     4-6 Hz cadence — bot seen walking (Option A visibility)
///
/// Evidence: the client-side wire log (E2E_WIRE_DUMP) is the raw RX stream
/// a real client would receive, plus the server's own log lines. Runs in the
/// e2e collection (serialized with the other stack tests).
/// </summary>
[Collection("e2e")]
public class PresenceE2eTests
{
    private const string ObserverAccount = "presenceobserver";
    private const string ObserverChar = "PresenceSee";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PresenceE2e_BotsProvisioned_AndVisibleToRealClientSession()
    {
        // The demo gate is env-driven so the game server process (spawned by
        // EnsureUp) inherits it. Normal E2E runs never set it — no-op.
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", "3");
        var wireDump = Path.Combine(E2eStack.E2eRoot, "logs", "presence-wire.log");
        Environment.SetEnvironmentVariable("E2E_WIRE_DUMP", wireDump);
        if (File.Exists(wireDump))
            File.Delete(wireDump);

        try
        {
            E2eStack.EnsureUp();

            // Wait for the living loop to come up (provision -> spawn ->
            // activate -> fidelity Full -> roam route armed). The demo logs
            // the final state line; 120s covers boot + world-ready + MySQL.
            var gameLog = Path.Combine(E2eStack.E2eRoot, "logs", "game.log");
            var upLine = await WaitForLogLineAsync(gameLog, "presence demo up", TimeSpan.FromSeconds(180));
            Assert.NotNull(upLine);
            Assert.Contains("3/3 citizen bots roaming", upLine);

            // A REAL client session (login auth -> world cookie -> enter
            // world -> create/select -> spawn -> notify in-game).
            using var observer = await BotNetworkSession.ConnectAsync(
                ObserverChar, ObserverAccount, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.True(observer.InWorld, "observer must be in-world");

            // Observe for 6s: bots broadcast at 4-6 Hz, so a visible bot
            // sends ~24-36 movement packets in the window.
            await Task.Delay(TimeSpan.FromSeconds(6));

            // Parse the CLIENT-side wire stream (what the observer session
            // actually received).
            var frames = ParseWireFrames(wireDump);

            // Map bot objIds from their SCUnitStatePacket frames.
            var botObjIds = new Dictionary<uint, string>();
            var unitStates = 0;
            var movementByObjId = new Dictionary<uint, int>();
            foreach (var frame in frames)
            {
                if (frame.Type != 0x69 && frame.Type != 0x6C)
                    continue;

                var objId = ReadObjId(frame.Body, out var consumed);
                if (frame.Type == 0x69)
                {
                    // [bc objId][i16 len][name] — only count CHARACTER frames
                    // whose name matches the demo set.
                    unitStates++;
                    var name = ReadName(frame.Body, consumed);
                    if (name is not null && name.StartsWith("Citizen", StringComparison.Ordinal))
                        botObjIds.TryAdd(objId, name);
                }
                else
                {
                    movementByObjId[objId] = movementByObjId.GetValueOrDefault(objId) + 1;
                }
            }

            // 1. All three bots are visible: their SCUnitStatePacket reached
            //    the client session (region-graph placement worked).
            Assert.True(botObjIds.Count >= 3,
                $"expected >=3 citizen bots visible, saw {botObjIds.Count}: {string.Join(",", botObjIds.Values)}");

            // 2. Every visible bot is WALKING: movement broadcasts from its
            //    objId at the throttled cadence (>=4 packets in the 6s
            //    window — the 4-6 Hz reduced-rate broadcast).
            var frozen = botObjIds.Keys.Where(id => movementByObjId.GetValueOrDefault(id) < 4).ToList();
            Assert.Empty(frozen);

            // 3. Total movement evidence is substantial (all three bots were
            //    in the around-set most of the window).
            var totalMovement = botObjIds.Keys.Sum(id => movementByObjId.GetValueOrDefault(id));
            Assert.True(totalMovement >= 30, $"expected >=30 bot movement packets, saw {totalMovement}");

            // Write the evidence report next to the other E2E artifacts.
            WriteEvidenceReport(gameLog, wireDump, botObjIds, movementByObjId, unitStates, totalMovement);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", null);
            Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", null);
            Environment.SetEnvironmentVariable("E2E_WIRE_DUMP", null);
            E2eStack.CleanupBotRows(ObserverAccount);
        }
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PresenceE2e_RestartWithoutDbWipe_ReembodiesAllBots_NoNameAlreadyExists()
    {
        // Restart-idempotency (t_db5b2be7): boot the demo, then restart ONLY
        // the game process — MySQL keeps the bot rows. The second boot must
        // ADOPT the existing Citizen01-03 rows (owned by the bot_managed_*
        // accounts) and come up 3/3 again with ZERO NameAlreadyExists errors.
        // The create-only path failed this with 0/3 on every boot after the
        // first (the reported prod defect).
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", "3");
        try
        {
            E2eStack.EnsureUp();

            // Boot 1: provisions (or adopts leftover rows from a prior run) —
            // must come up 3/3 either way.
            var gameLog = Path.Combine(E2eStack.E2eRoot, "logs", "game.log");
            var up1 = await WaitForLogLineAsync(gameLog, "presence demo up", TimeSpan.FromSeconds(180));
            Assert.NotNull(up1);
            Assert.Contains("3/3 citizen bots roaming", up1);

            // Restart ONLY the game process — no DB wipe, no row cleanup.
            E2eStack.RestartGameServer();

            // Boot 2 writes to game-restart.log (FileMode.Create — a fresh
            // file, so every line in it is from the second boot).
            var restartLog = Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");
            var up2 = await WaitForLogLineAsync(restartLog, "presence demo up", TimeSpan.FromSeconds(180));
            Assert.NotNull(up2);
            Assert.Contains("3/3 citizen bots roaming", up2);

            // The second boot adopted the existing rows: the log shows the
            // adopt path and zero NameAlreadyExists / provisioning rejections.
            var secondBoot = string.Join("\n", File.ReadAllLines(restartLog));
            Assert.DoesNotContain("NameAlreadyExists", secondBoot);
            Assert.DoesNotContain("rejected by NameManager", secondBoot);
            Assert.DoesNotContain("failed to provision", secondBoot);
            Assert.Contains("adopted existing character", secondBoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", null);
            Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", null);
            E2eStack.CleanupBotRows(ObserverAccount,
                "bot_managed_presence_001", "bot_managed_presence_002", "bot_managed_presence_003");
        }
    }

    private static async Task<string?> WaitForLogLineAsync(string logPath, string needle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                var tail = File.ReadAllLines(logPath, Encoding.UTF8).LastOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
                if (tail is not null)
                    return tail;
            }
            await Task.Delay(1000);
        }
        return null;
    }

    // ------------------------------------------------------------------ wire parsing

    private sealed record WireFrame(ushort Type, byte[] Body);

    private static List<WireFrame> ParseWireFrames(string path)
    {
        var frames = new List<WireFrame>();
        if (!File.Exists(path))
            return frames;

        foreach (var line in File.ReadAllLines(path))
        {
            // Format: HH:mm:ss.fff RX <name>[0x<TYPE>] [<N> B] <HEX>
            var typeIdx = line.IndexOf("[0x", StringComparison.Ordinal);
            if (typeIdx < 0)
                continue;
            var typeHex = line.Substring(typeIdx + 3, 3);
            if (!ushort.TryParse(typeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var type))
                continue;
            // Walk the two brackets explicitly: [0xNNN] then [NNB] — the hex
            // starts after the size bracket's ']'.
            var typeClose = line.IndexOf(']', typeIdx + 3);
            if (typeClose < 0)
                continue;
            var sizeOpen = line.IndexOf('[', typeClose + 1);
            if (sizeOpen < 0)
                continue;
            var sizeClose = line.IndexOf(']', sizeOpen + 1);
            if (sizeClose < 0)
                continue;
            var body = Convert.FromHexString(line[(sizeClose + 2)..].Trim());
            frames.Add(new WireFrame(type, body));
        }
        return frames;
    }

    /// <summary>WriteBc(objId) = 3-byte little-endian (PacketStream.cs:1239).</summary>
    private static uint ReadObjId(byte[] body, out int consumed)
    {
        if (body.Length < 3)
        {
            consumed = 0;
            return 0;
        }
        consumed = 3;
        return (uint)(body[0] | (body[1] << 8) | (body[2] << 16));
    }

    /// <summary>Reads an i16-length-prefixed UTF8 string at offset (name field).</summary>
    private static string? ReadName(byte[] body, int offset)
    {
        if (offset + 2 > body.Length)
            return null;
        var len = BitConverter.ToInt16(body, offset);
        if (len < 0 || offset + 2 + len > body.Length)
            return null;
        return Encoding.UTF8.GetString(body, offset + 2, len);
    }

    // ------------------------------------------------------------------ evidence

    private static void WriteEvidenceReport(
        string gameLog, string wireDump,
        Dictionary<uint, string> botObjIds,
        Dictionary<uint, int> movementByObjId,
        int unitStates, int totalMovement)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PRESENCE PROOF — bots visible in the live world (t_6bad0654)");
        sb.AppendLine();
        sb.AppendLine("> Generated by PresenceE2eTests on a REAL Login + Game + MySQL stack");
        sb.AppendLine("> (same binaries, same config precedence as prod; E2E_REBUILD=1 publish of feat/bot-presence-integration).");
        sb.AppendLine();
        sb.AppendLine("## Living loop (server side, game.log)");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var line in File.ReadAllLines(gameLog, Encoding.UTF8))
        {
            if (line.Contains("presence demo", StringComparison.Ordinal) ||
                line.Contains("Roam route assigned", StringComparison.Ordinal) ||
                line.Contains("PlayerBot embodied", StringComparison.Ordinal))
                sb.AppendLine(line);
        }
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Client-side observation (real session RX stream)");
        sb.AppendLine();
        sb.AppendLine("Observer: real X2 enter-world session (login auth -> cookie -> create/select -> spawn), 6s observation window.");
        sb.AppendLine();
        sb.AppendLine($"| Bot | objId | SCUnitStatePacket | SCOneUnitMovementPacket |");
        sb.AppendLine($"|---|---|---|---|");
        foreach (var (objId, name) in botObjIds.OrderBy(kv => kv.Value))
            sb.AppendLine($"| {name} | {objId} | yes | {movementByObjId.GetValueOrDefault(objId)} |");
        sb.AppendLine();
        sb.AppendLine($"Total SCUnitStatePacket frames received (all units): {unitStates}");
        sb.AppendLine($"Total bot movement frames received: {totalMovement} (3 bots x 4-6 Hz x ~6s)");
        sb.AppendLine();
        sb.AppendLine("Raw wire capture: " + wireDump);
        sb.AppendLine();

        var outPath = Path.Combine(E2eStack.E2eRoot, "logs", "presence-proof.md");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine("[presence] evidence report: " + outPath);
    }
}
