using System.Collections.Concurrent;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>Download ownership lasts through package verification and user confirmation, until explicit discard.</summary>
public static class PluginPackageDownloads
{
    private static readonly ConcurrentDictionary<string, PluginArtifactLease> Pending = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Reserves a unique package path in the private plugin download directory.</summary>
    public static string CreatePath(string directory)
    {
        PluginArtifactLease lease = PluginArtifactLease.Create(directory, "market-", ".vpx", directory: false);
        Pending[lease.Path] = lease;
        return lease.Path;
    }

    /// <summary>Reclaims at most 128 abandoned downloads; active and unmarked legacy packages are retained.</summary>
    public static void Sweep(string directory) => PluginArtifactLease.Sweep(directory, "market-", ".vpx", directory: false);

    /// <summary>Deletes the owned package and any partial download, releasing ownership only afterward.</summary>
    public static void Discard(string path)
    {
        if (Pending.TryRemove(path, out PluginArtifactLease? lease)) lease.Dispose();
    }
}
