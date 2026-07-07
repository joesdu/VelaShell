using PulseTerm.Core.Services;
using PulseTerm.Core.Ssh;

namespace PulseTerm.Infrastructure.Ssh;

/// <summary>Runs the metrics probe over the session's existing SSH connection (§11).</summary>
public sealed class SessionMetricsService : ISessionMetricsService
{
    private readonly ISshConnectionService _connectionService;

    public SessionMetricsService(ISshConnectionService connectionService)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    }

    public async Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var client = _connectionService.GetClient(sessionId);
        if (client is null || !client.IsConnected)
            return null;

        try
        {
            var output = await client.RunCommandAsync(SessionMetrics.MetricsCommand, cancellationToken)
                .ConfigureAwait(false);
            return SessionMetrics.Parse(output);
        }
        catch
        {
            // A failed probe (timeout, non-Linux host, dropped session) is "data unavailable".
            return null;
        }
    }
}
