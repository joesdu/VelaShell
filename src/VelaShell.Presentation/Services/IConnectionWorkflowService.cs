using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

public interface IConnectionWorkflowService
{
    Task<IReadOnlyList<SessionProfile>> GetSavedProfilesAsync(CancellationToken cancellationToken = default);
    Task<SessionProfile> SaveProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default);
    Task<SshSession> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
