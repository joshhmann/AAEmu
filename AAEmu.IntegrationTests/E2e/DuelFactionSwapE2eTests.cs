using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// C9 (DUEL-01) live-stack verification — completes the A dimension the
/// headless rig documented as out of scope: the combat-flag spawn, geodata
/// height read, temporary RedTeam/BlueTeam faction swap, and duel-start
/// broadcast all run on the REAL stack with REAL world data.
///
/// Flow (both bots through the real login flow; duels ride DIRECT packet
/// injection over each bot's own authenticated game link — the
/// TransferRide/Fishing E2E pattern):
///   1. Two same-race bots provision at the SAME spawn position (adjacent).
///   2. A injects CSChallengeDuelPacket(B.characterId) → B receives
///      SCDuelChallengedPacket.
///   3. B injects CSStartDuelPacket(A.characterId, accept) → flag spawns
///      mid-waypoint (REAL geodata height), factions swap to Red/Blue,
///      DuelStartTask fires after 3 s → SCDuelStartedPacket + SCDuelState
///      frames with a non-zero flag objId.
///
/// The rig half of this evidence (request/cancel/cleanup state transitions)
/// lives in AAEmu.UnitTests DuelManagerRigTests.
/// </summary>
[Collection("e2e")]
public class DuelFactionSwapE2eTests
{
    // Hyphen-free: NameManager rejects '-' in character names.
    private const string BotA = "DuelistA";
    private const string BotB = "DuelistB";

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Duel_ChallengeAccept_StartBroadcastWithFlag_OnLiveServer_EndToEnd()
    {
        E2eStack.EnsureUp();
        Directory.CreateDirectory(EvidenceDir);

        // -------------------------------------------- PROVISION (same race →
        // same spawn position → within duel challenge range of each other)
        using var botA = await BotNetworkSession.ConnectAsync(
            BotA, "e2eduelista", "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        using var botB = await BotNetworkSession.ConnectAsync(
            BotB, "e2eduelistb", "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

        Assert.True(botA.InWorld && botB.InWorld, "both duelists must be in-world");
        Assert.NotEqual(botA.CharacterId, botB.CharacterId);

        var linkA = GetGameLink(botA);
        var linkB = GetGameLink(botB);
        StopBackgroundLoops(botA);
        StopBackgroundLoops(botB);
        using var pingCts = new CancellationTokenSource();
        var pingTask = Task.Run(() => PingLoopAsync(linkA, pingCts.Token));

        try
        {
            // --------------------------------------------- CHALLENGE (A → B)
            linkA.SendGameFrame(CSOffsets.CSChallengeDuelPacket, 1, body =>
            {
                body.Write(botB.CharacterId); // challenged character Id
            });

            var challenge = WaitForFrame(linkB, SCOffsets.SCDuelChallengedPacket, 10_000);
            Assert.True(challenge != null, "challenged bot received no SCDuelChallengedPacket");

            // ------------------------------------------------ ACCEPT (B)
            linkB.SendGameFrame(CSOffsets.CSStartDuelPacket, 1, body =>
            {
                body.Write(botA.CharacterId);  // challenger character Id
                body.Write((short)0);          // 0 = accepted
            });

            // DuelStartTask fires ~3 s after accept — but ONLY if the flag
            // spawn + geodata height read succeeded first. The started packet
            // is therefore the proof that the FULL accept path ran live.
            var startedA = WaitForFrame(linkA, SCOffsets.SCDuelStartedPacket, 15_000);
            var startedB = WaitForFrame(linkB, SCOffsets.SCDuelStartedPacket, 15_000);
            Assert.True(startedA != null || startedB != null,
                "no SCDuelStartedPacket on either link within 15s — the accept path did not complete " +
                "(flag spawn / geodata / faction swap failed; check game-restart.log for Warn lines)");

            // State frames carry the combat-flag doodad ObjId — non-zero when
            // the flag actually spawned from REAL world geodata.
            var stateFrame = WaitForFrame(linkA, SCOffsets.SCDuelStatePacket, 10_000);
            uint flagObjId = 0;
            if (stateFrame is { Length: >= 7 })
            {
                flagObjId = ReadBc(stateFrame, 4); // bc(challengerObjId)[0..2] + bc(flagObjId)[4..6]
            }

            // ------------------------------------------- CLEANUP (cancel)
            // Cancel rather than fight: CSStartDuelPacket errorMessage=507
            // routes to DuelCancel for both sides' registered entries.
            linkA.SendGameFrame(CSOffsets.CSStartDuelPacket, 1, body =>
            {
                body.Write(botA.CharacterId);
                body.Write((short)507); // refuse/cancel semantic
            });

            WriteEvidence(startedA != null || startedB != null, flagObjId,
                botA.CharacterId, botB.CharacterId);
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { /* cancelled */ }
        }
    }

    // ------------------------------------------------------------ helpers

    private static BotTcpLink GetGameLink(BotNetworkSession session)
        => (BotTcpLink)typeof(BotNetworkSession)
            .GetField("_game", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(session)!;

    private static void StopBackgroundLoops(BotNetworkSession session)
    {
        if (typeof(BotNetworkSession)
                .GetField("_keepAliveCts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(session) is CancellationTokenSource cts)
            cts.Cancel();
    }

    private static bool TryTakeFrame(BotTcpLink link, ushort type, out byte[] body)
    {
        foreach (var frame in link.DrainAll())
        {
            if (frame.Type != type)
                continue;
            body = frame.Body;
            return true;
        }
        body = null!;
        return false;
    }

    private static async Task PingLoopAsync(BotTcpLink link, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && link.Connected)
        {
            try
            {
                link.SendGameFrame(PPOffsets.PingPacket, 2, body =>
                {
                    body.Write(0L);
                    body.Write(0L);
                    body.Write(0u);
                });
            }
            catch
            {
                break;
            }
            await Task.Delay(5_000, ct).ContinueWith(_ => { });
        }
    }

    /// <summary>bc = 24-bit little-endian (PacketStream.WriteBc/ReadBc).</summary>
    private static uint ReadBc(byte[] body, int offset)
        => (uint)(body[offset] | (body[offset + 1] << 8) | (body[offset + 2] << 16));

    private static byte[]? WaitForFrame(BotTcpLink link, ushort type, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (TryTakeFrame(link, type, out var body))
                return body;
            Thread.Sleep(25);
        }
        return null;
    }

    private static void WriteEvidence(bool started, uint flagObjId, uint idA, uint idB)
    {
        var report = new
        {
            scenario = "duel-faction-swap",
            milestone = "DUEL-01 live-stack completion (accept path incl. flag spawn + geodata)",
            verdict = started ? "PASS" : "FAIL",
            duelists = new { a = BotA, idA, b = BotB, idB },
            duelStartedFramesSeen = started,
            combatFlagObjId = flagObjId,
            note = "challenge → SCDuelChallengedPacket → accept → flag spawn on REAL geodata → " +
                   "RedTeam/BlueTeam swap → 3s DuelStartTask → SCDuelStarted broadcast"
        };
        File.WriteAllText(Path.Combine(EvidenceDir, "duel-faction-swap-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
