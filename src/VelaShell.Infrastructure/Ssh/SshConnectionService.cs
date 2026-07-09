using System.Collections.Concurrent;
using DynamicData;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

public class SshConnectionService : ISshConnectionService
{
    private readonly ILogger<SshConnectionService>? _logger;
    private readonly Func<ConnectionInfo, ISshClientWrapper> _clientFactory;
    private readonly SourceList<SshSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, ISshClientWrapper> _clients = new();

    /// <summary>只保护 <see cref="_sessions"/> 列表的增删/读取(微秒级、无网络 I/O)。
    /// 握手不在此锁内进行,因此一条高延迟连接不再阻塞其它并发连接。</summary>
    private readonly object _sessionsGate = new();

    public SshConnectionService(
        Func<ConnectionInfo, ISshClientWrapper> clientFactory,
        ILogger<SshConnectionService>? logger = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger;
    }

    public IObservableList<SshSession> Sessions => _sessions.AsObservableList();

    public Task<SshSession> ConnectAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
    {
        // 并发连接:握手不再串行,多个会话可同时建连,单条慢连接不阻塞其它连接。
        return ConnectInternalAsync(connectionInfo, cancellationToken);
    }

    private async Task<SshSession> ConnectInternalAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        var session = new SshSession
        {
            ConnectionInfo = connectionInfo,
            Status = SessionStatus.Connecting
        };

        lock (_sessionsGate)
        {
            _sessions.Add(session);
        }

        ISshClientWrapper? client = null;
        try
        {
            client = _clientFactory(connectionInfo);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (!client.IsConnected)
            {
                client.Dispose();
                client = null;
                throw new InvalidOperationException("Client connection failed without exception");
            }

            _clients[session.SessionId] = client;

            session.Status = SessionStatus.Connected;
            session.ConnectedAt = DateTime.UtcNow;

            _logger?.LogInformation("SSH session {SessionId} connected to {Host}:{Port}",
                session.SessionId, connectionInfo.Host, connectionInfo.Port);

            return session;
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();

            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Connection to {connectionInfo.Host}:{connectionInfo.Port} timed out. Please check the host and port, then retry.";
            lock (_sessionsGate)
            {
                _sessions.Remove(session);
            }

            _logger?.LogWarning("SSH session {SessionId} to {Host}:{Port} timed out or was cancelled",
                session.SessionId, connectionInfo.Host, connectionInfo.Port);

            throw new TimeoutException(session.ErrorMessage);
        }
        catch (Exception ex)
        {
            client?.Dispose();

            session.Status = SessionStatus.Error;
            session.ErrorMessage = ex.Message;
            lock (_sessionsGate)
            {
                _sessions.Remove(session);
            }

            _logger?.LogError(ex, "Failed to connect SSH session {SessionId} to {Host}:{Port}",
                session.SessionId, connectionInfo.Host, connectionInfo.Port);

            throw;
        }
    }

    public async Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // 断开也不再走全局锁:每个会话的网络拆除各自并发进行,不阻塞其它连接/断开。
        var session = GetSession(sessionId);
        if (session == null)
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        if (session.Status == SessionStatus.Disconnected)
        {
            return;
        }

        if (_clients.TryRemove(sessionId, out var client))
        {
            await Task.Run(() =>
            {
                client.Disconnect();
                client.Dispose();
            }, cancellationToken).ConfigureAwait(false);
        }

        session.Status = SessionStatus.Disconnected;

        _logger?.LogInformation("SSH session {SessionId} disconnected", sessionId);
    }

    public SshSession? GetSession(Guid sessionId)
    {
        lock (_sessionsGate)
        {
            return _sessions.Items.FirstOrDefault(s => s.SessionId == sessionId);
        }
    }

    public ISshClientWrapper? GetClient(Guid sessionId)
    {
        _clients.TryGetValue(sessionId, out var client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        var clientEntries = _clients.ToArray();
        _clients.Clear();

        // Disconnect every session concurrently: each Disconnect() is a blocking network teardown,
        // so a sequential loop would make app exit take (sessions × teardown) time and stall on any
        // single unresponsive connection.
        var teardowns = clientEntries.Select(entry => Task.Run(() =>
        {
            var (sessionId, client) = entry;
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                client.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing SSH client for session {SessionId}", sessionId);
            }
        }));

        await Task.WhenAll(teardowns).ConfigureAwait(false);

        _sessions.Dispose();
        GC.SuppressFinalize(this);
    }
}
