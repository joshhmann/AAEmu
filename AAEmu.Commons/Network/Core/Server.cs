using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetCoreServer;
using NLog;

namespace AAEmu.Commons.Network.Core;

public class Server(IPAddress address, int port, IBaseProtocolHandler protocolHandler)
    : TcpServer(address, port)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<Guid, Session> _sessions = [];

    public IBaseProtocolHandler GetHandler() => protocolHandler;

    protected override TcpSession CreateSession() => new Session(this);

    protected override void OnStarted()
    {
        Logger.Info($"TCP server listening start on {Endpoint}");
    }

    protected override void OnStopped()
    {
        Logger.Info("TCP server listener stopped!");
    }

    protected override void OnConnected(TcpSession session)
    {
        Logger.Info(
            $"Connect from {session.Socket.RemoteEndPoint} established, session id: {session.Id}");
        _sessions.TryAdd(session.Id, (Session)session);
    }

    protected override void OnDisconnected(TcpSession session)
    {
        Logger.Info($"Connect from session id: {session.Id} disconnected");
        _sessions.TryRemove(session.Id, out _);
    }

    protected override void OnError(SocketError error)
    {
        Logger.Error($"TCP server SocketError: {error}");
    }

    public Session GetSession(Func<Session, bool> func)
    {
        return _sessions.Values.SingleOrDefault(func);
    }

    public HashSet<Session> GetSessions()
    {
        return [.. _sessions.Values];
    }

    public IEnumerable<Session> GetSessions(Func<Session, bool> func)
    {
        return _sessions.Values.Where(func).ToArray();
    }
}
