using System.Net;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.L2G;
using AAEmu.Game.Models;
using NLog;

namespace AAEmu.Game.Core.Network.Login;

public class LoginNetwork : Singleton<LoginNetwork>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private Client _client;
    private readonly LoginProtocolHandler _handler;
    private LoginConnection _connection;
    private int _stopping;

    internal bool IsStopping => Volatile.Read(ref _stopping) != 0;

    private LoginNetwork()
    {
        _handler = new LoginProtocolHandler();

        RegisterPacket(LGOffsets.LGRegisterGameServerPacket, typeof(LGRegisterGameServerPacket));
        RegisterPacket(LGOffsets.LGPlayerEnterPacket, typeof(LGPlayerEnterPacket));
        RegisterPacket(LGOffsets.LGPlayerReconnectPacket, typeof(LGPlayerReconnectPacket));
        RegisterPacket(LGOffsets.LGRequestInfoPacket, typeof(LGRequestInfoPacket));
    }
    public void Start()
    {
        // Do not touch AppConfiguration after shutdown starts: a disconnect callback
        // can race DI teardown and otherwise trigger an ObjectDisposedException.
        if (IsStopping)
        {
            Logger.Debug("Ignoring LoginNetwork.Start after shutdown began");
            return;
        }

        var config = AppConfiguration.Instance.LoginNetwork;
        _client = new Client(Dns.GetHostAddresses(config.Host).First(), config.Port, _handler);
        _client.ConnectAsync();
    }

    public void Stop()
    {
        // Stop is the shutdown boundary; disconnect callbacks must not reconnect.
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        var client = _client;
        if (client?.IsConnected ?? false)
            client.DisconnectAsync();
    }

    public void SetConnection(LoginConnection con)
    {
        _connection = con;
    }

    public LoginConnection GetConnection()
    {
        return _connection;
    }

    private void RegisterPacket(uint type, Type classType)
    {
        _handler.RegisterPacket(type, classType);
    }
}
