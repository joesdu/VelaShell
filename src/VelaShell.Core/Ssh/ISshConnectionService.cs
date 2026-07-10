using DynamicData;
using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

public interface ISshConnectionService : IAsyncDisposable
{
    IObservableList<SshSession> Sessions { get; }

    Task<SshSession> ConnectAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken = default);
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
    SshSession? GetSession(Guid sessionId);
    ISshClientWrapper? GetClient(Guid sessionId);
}
