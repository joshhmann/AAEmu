using System.Security.Cryptography;
using System.Text;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;
using AAEmu.Login.Core.Packets.C2L;
using AAEmu.Login.Core.Packets.L2C;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M2b-E2E network bot session — the REAL login/enter-world flow over real
/// TCP, byte-for-byte the protocol a 1.2 client speaks:
///
///   login : CARequestAuthTrion (sha256-hex password, the EU/Trion auth path)
///           -> ACJoinResponse + ACAuthResponse (accountId)
///           -> CAListWorld -> ACWorldList
///           -> CAEnterWorld(gs 1) -> ACWorldCookie (cookie + game address)
///   game  : X2EnterWorld(accountId, cookie) -> X2EnterWorldResponse (token)
///           -> CSListCharacter -> SCCharacterList
///           -> CSCreateCharacter (REAL character provisioning through the
///              server's create handler — no direct DB writes) / select
///           -> CSSelectCharacter -> SCCharacterState burst
///           -> CSSpawnCharacter -> SCUnitState
///           -> CSNotifyInGame + CSNotifyInGameCompleted  =  IN WORLD
///
/// The bot keeps its game link open (ping keep-alive + frame drain) so the
/// server sees a live session; quests are driven by the E2E runner over the
/// BotDriveBridge. NO auth bypass: the session exists only because the real
/// login server authenticated the account and issued the cookie.
/// </summary>
public sealed class BotNetworkSession : IDisposable
{
    public string BotName { get; }
    public string AccountName { get; }
    public uint AccountId { get; private set; }
    public uint Cookie { get; private set; }
    public uint GameToken { get; private set; }
    public uint StreamPort { get; private set; }
    public uint CharacterId { get; private set; }
    public string CharacterName { get; private set; }
    public bool InWorld { get; private set; }

    private BotTcpLink _login;
    private BotTcpLink _game;
    private BotTcpLink _stream;
    private CancellationTokenSource _keepAliveCts;
    private Task _keepAliveTask;
    private Task _drainTask;

    private BotNetworkSession(string botName, string accountName)
    {
        BotName = botName;
        AccountName = accountName;
        CharacterName = botName;
    }

    public static string Sha256Hex(string password)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    /// <summary>
    /// Runs the full real login/enter-world flow. Creates the character
    /// through the server's real create handler when it does not exist yet.
    /// </summary>
    public static async Task<BotNetworkSession> ConnectAsync(
        string botName, string accountName, string password,
        string loginHost, int loginPort, string gameHost, int gamePort, string streamHost, int streamPort)
    {
        var session = new BotNetworkSession(botName, accountName);

        // ---- Login server (1237): auth + world list + enter-world cookie ----
        session._login = new BotTcpLink($"{botName}:login", loginHost, loginPort, isGame: false);

        var ticket = $"<auth><username>{accountName}</username><password>{Sha256Hex(password)}</password></auth>";
        session._login.SendLoginFrame(CLOffsets.CARequestAuthTrionPacket, body =>
        {
            body.Write(0u); // pFrom
            body.Write(0u); // pTo
            body.Write(false); // dev
            body.Write(Array.Empty<byte>(), true); // mac — Int16 length-prefixed (ReadBytes())
            body.Write(ticket);
            body.Write(string.Empty); // signature
            body.Write(true); // isLast
        });

        var (authType, authBody) = session._login.ReadAnyOf(
            new[] { LCOffsets.ACAuthResponsePacket, LCOffsets.ACLoginDeniedPacket }, 20000);
        if (authType == LCOffsets.ACLoginDeniedPacket)
            throw new InvalidOperationException($"{botName}: login DENIED by the login server (bad account or password)");

        var authStream = new PacketStream();
        authStream.Write(authBody);
        session.AccountId = authStream.ReadUInt32();

        session._login.SendLoginFrame(CLOffsets.CAListWorldPacket, body => body.Write(0ul));
        _ = session._login.ReadFrameUntil(LCOffsets.ACWorldListPacket, 20000); // world list (drained)

        session._login.SendLoginFrame(CLOffsets.CAEnterWorldPacket, body =>
        {
            body.Write(0ul); // flag
            body.Write((byte)1); // gsId
        });
        var cookieBody = session._login.ReadFrameUntil(LCOffsets.ACWorldCookiePacket, 20000);
        var cookieStream = new PacketStream();
        cookieStream.Write(cookieBody);
        session.Cookie = cookieStream.ReadUInt32();
        // 4 x (ip u32, port u16) — we dial the configured dev host/port.
        session._login.Close();

        // ---- Game server (1239): enter world + character + spawn ----
        session._game = new BotTcpLink($"{botName}:game", gameHost, gamePort, isGame: true);

        session._game.SendGameFrame(CSOffsets.X2EnterWorldPacket, 1, body =>
        {
            body.Write(0u); // pFrom
            body.Write(0u); // pTo
            body.Write(session.AccountId);
            body.Write(session.Cookie);
            body.Write(0); // zoneId
            body.Write((byte)0); // tb
            body.Write(0ul); // revision
        });

        var x2Body = session._game.ReadFrameUntil(SCOffsets.X2EnterWorldResponsePacket, 20000);
        var x2 = new PacketStream();
        x2.Write(x2Body);
        var reason = x2.ReadInt16();
        _ = x2.ReadBoolean(); // gm
        session.GameToken = x2.ReadUInt32();
        session.StreamPort = x2.ReadUInt16();
        if (reason != 0)
            throw new InvalidOperationException($"{botName}: enter world refused (reason {reason})");

        // Character list.
        session.CharacterId = session.RequestCharacterList().FirstOrDefault();

        // Create the character through the REAL server create handler when absent.
        if (session.CharacterId == 0)
        {
            session.CreateCharacter();
            session.CharacterId = session.RequestCharacterList().FirstOrDefault();
            if (session.CharacterId == 0)
                throw new InvalidOperationException($"{botName}: character creation did not produce a character row");
        }

        // Select + spawn.
        session._game.SendGameFrame(CSOffsets.CSSelectCharacterPacket, 1, body =>
        {
            body.Write(session.CharacterId);
            body.Write(false); // gm
            body.Write((byte)0);
        });
        _ = session._game.ReadFrameUntil(SCOffsets.SCCharacterStatePacket, 20000);

        // Settle: the select burst (inventory, quests, factions...) is large.
        var settleDeadline = Environment.TickCount64 + 1000;
        while (Environment.TickCount64 < settleDeadline)
        {
            session._game.DrainAll();
            await Task.Delay(100).ConfigureAwait(false);
        }

        session._game.SendGameFrame(CSOffsets.CSSpawnCharacterPacket, 1, body =>
            body.Write((byte)0)); // VisualOptions flag 0
        _ = session._game.ReadFrameUntil(SCOffsets.SCUnitStatePacket, 20000);

        session._game.SendGameFrame(CSOffsets.CSNotifyInGamePacket, 1, _ => { });
        session._game.SendGameFrame(CSOffsets.CSNotifyInGameCompletedPacket, 1, _ => { });

        // ---- Stream server (1250): join (part of the real enter-world flow) ----
        try
        {
            session._stream = new BotTcpLink($"{botName}:stream", streamHost, (int)session.StreamPort, isGame: false);
            session._stream.SendLoginFrame(AAEmu.Game.Core.Packets.C2S.CTOffsets.CTJoinPacket, body =>
            {
                body.Write(session.AccountId);
                body.Write(session.Cookie);
            });
        }
        catch (Exception ex)
        {
            // Stream join is fire-and-forget for the quest drive; a failure here
            // is logged but not fatal (the server does not require the stream
            // connection for quest state).
            Console.WriteLine($"[e2e] {botName}: stream join skipped ({ex.Message})");
        }

        session.InWorld = true;

        // Keep-alive: the server disconnects accounts silent for 30s
        // (AccountManager.RemoveDeadConnections). Ping every 8s.
        session._keepAliveCts = new CancellationTokenSource();
        session._keepAliveTask = Task.Run(() => session.PingLoopAsync(session._keepAliveCts.Token));
        session._drainTask = Task.Run(() => session.DrainLoopAsync(session._keepAliveCts.Token));

        return session;
    }

    /// <summary>CSListCharacter -> SCCharacterList; returns character ids.</summary>
    private List<uint> RequestCharacterList()
    {
        _game.SendGameFrame(CSOffsets.CSListCharacterPacket, 1, body =>
        {
            body.Write(0); // size
            body.Write(Array.Empty<byte>(), true); // data — Int16 length-prefixed (ReadBytes())
        });

        var body = _game.ReadFrameUntil(SCOffsets.SCCharacterListPacket, 20000);
        var stream = new PacketStream();
        stream.Write(body);
        _ = stream.ReadBoolean(); // last
        var count = stream.ReadByte();
        var ids = new List<uint>();
        for (var i = 0; i < count; i++)
        {
            var id = stream.ReadUInt32();
            _ = stream.ReadString(); // name
            _ = stream.ReadByte(); // race
            _ = stream.ReadByte(); // gender
            _ = stream.ReadByte(); // level
            ids.Add(id);
        }

        return ids;
    }

    private void CreateCharacter()
    {
        _game.SendGameFrame(CSOffsets.CSCreateCharacterPacket, 1, body =>
        {
            body.Write(CharacterName);
            body.Write((byte)1); // race Nuian
            body.Write((byte)1); // gender Male
            for (var i = 0; i < 7; i++)
                body.Write(0u); // starting items
            body.Write((byte)0); // UnitCustomModelParams type None (1 byte)
            body.Write((byte)1); // ability1 Fight
            body.Write((byte)7); // ability2 Magic
            body.Write((byte)4); // ability3 Will
            body.Write((byte)1); // level
        });

        var (type, createBody) = _game.ReadAnyOf(
            new[] { SCOffsets.SCCreateCharacterResponsePacket, SCOffsets.SCCharacterCreationFailedPacket }, 20000);
        if (type == SCOffsets.SCCharacterCreationFailedPacket)
            throw new InvalidOperationException($"{BotName}: character creation FAILED (server rejected the create packet)");
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(8000, ct).ConfigureAwait(false);
                if (!_game.Connected)
                    break;
                _game.SendGameFrame(PPOffsets.PingPacket, 2, body =>
                {
                    body.Write(0L); // tPhy
                    body.Write(0L); // ping
                    body.Write(0u); // local
                });
            }
        }
        catch
        {
        }
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                _game.DrainAll();
            }
        }
        catch
        {
        }
    }

    /// <summary>Graceful disconnect: close the game socket (server saves on
    /// disconnect) then the rest.</summary>
    public void Disconnect()
    {
        try
        {
            _keepAliveCts?.Cancel();
        }
        catch
        {
        }

        _game?.Close(); // close first: triggers the server's save-on-disconnect path
        _stream?.Close();
        _login?.Close();
        InWorld = false;
    }

    public void Dispose() => Disconnect();
}
