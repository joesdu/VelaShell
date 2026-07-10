namespace VelaShell.Core.Services;

/// <summary>Fetches a live resource snapshot for a connected session (resource panel §11).</summary>
public interface ISessionMetricsService
{
    /// <summary>
    /// Returns the current metrics, or null when the session is not connected or the
    /// remote host doesn't expose the expected probes.
    /// </summary>
    Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
