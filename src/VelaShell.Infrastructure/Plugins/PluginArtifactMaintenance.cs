using System.Diagnostics;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Manifest;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>Reclaims leased plugin work and recovers unambiguous interrupted upgrades before discovery.</summary>
public static class PluginArtifactMaintenance
{
    internal static FileStream AcquireInstallationLease(string userRoot)
    {
        PluginArtifactLease.CreatePrivateDirectory(userRoot);
        // Keep the lock file: unlinking it allows two processes to lock different inodes.
        return PluginArtifactLease.OpenLease(Path.Combine(userRoot, ".install.lease"), FileMode.OpenOrCreate);
    }

    /// <summary>Safe to call while another host owns artifacts. Unmarked legacy work is retained.</summary>
    public static void Sweep(string? userRoot, string? shadowRoot)
    {
        if (userRoot is not null)
        {
            PluginArtifactLease.Sweep(PluginManager.PackageStagingRoot(userRoot), "", "", directory: true);
            try
            {
                using FileStream lease = AcquireInstallationLease(userRoot);
                RecoverBackups(userRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[VelaShell] Could not recover plugin upgrades: {ex.Message}");
            }
        }
        SweepShadows(shadowRoot);
    }

    internal static void SweepShadows(string? root)
    {
        try
        {
            if (root is null || !Directory.Exists(root) || PluginArtifactLease.IsLink(root)) return;
            foreach (string directory in Directory.EnumerateDirectories(root).Take(PluginArtifactLease.SweepLimit))
                PluginArtifactLease.Sweep(directory, "gen-", "", directory: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[VelaShell] Could not sweep plugin shadows: {ex.Message}");
        }
    }

    private const string BackupMarker = "VelaShell plugin upgrade backup v1";

    internal static void MarkBackup(string backup)
    {
        using var marker = new StreamWriter(new FileStream(backup + ".owned", FileMode.CreateNew, FileAccess.Write, FileShare.None));
        marker.Write(BackupMarker);
    }

    internal static void RemoveBackupMarker(string backup)
    {
        try
        {
            string marker = backup + ".owned";
            if (File.Exists(marker) && !PluginArtifactLease.IsLink(marker) && File.ReadAllText(marker) == BackupMarker)
                File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Only call with the hash of a matching, non-adopted protected installation receipt.</summary>
    internal static void CleanVerifiedBackups(string userRoot, string id, string verifiedHash)
    {
        try
        {
            using FileStream lease = AcquireInstallationLease(userRoot);
            string target = Path.Combine(userRoot, id);
            string root = Path.Combine(userRoot, ".upgrade");
            if (!Directory.Exists(root) || PluginArtifactLease.IsLink(root) || !IsValidBackup(target, id)
                || !string.Equals(PluginContentHash.Compute(target), verifiedHash, StringComparison.Ordinal))
            {
                return;
            }

            foreach (string marker in Directory.EnumerateFiles(root, "*.owned").Take(PluginArtifactLease.SweepLimit))
            {
                string backup = marker[..^6];
                if (!PluginArtifactLease.IsArtifactName(Path.GetFileName(backup), id + "-", "")
                    || PluginArtifactLease.IsLink(backup) || PluginArtifactLease.IsLink(marker)
                    || File.ReadAllText(marker) != BackupMarker)
                {
                    continue;
                }

                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                RemoveBackupMarker(backup);
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Trace.WriteLine($"[VelaShell] Could not clean verified plugin backups: {ex.Message}");
        }
    }

    internal static bool HasBackup(string userRoot, string id)
    {
        try
        {
            string root = Path.Combine(userRoot, ".upgrade");
            return Directory.Exists(root) && Directory.EnumerateDirectories(root)
                .Any(path => PluginArtifactLease.IsArtifactName(Path.GetFileName(path), id + "-", ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // An unreadable backup must not lose its protected receipt.
        }
    }

    private static void RecoverBackups(string userRoot)
    {
        string root = Path.Combine(userRoot, ".upgrade");
        if (!Directory.Exists(root) || PluginArtifactLease.IsLink(root)) return;
        foreach (string marker in Directory.EnumerateFiles(root, "*.owned").Take(PluginArtifactLease.SweepLimit))
        {
            string backup = marker[..^6];
            string name = Path.GetFileName(backup);
            if (name.Length > 33 && name[^33] == '-'
                && PluginArtifactLease.IsArtifactName(name, name[..^32], "")
                && !Directory.Exists(backup) && !File.Exists(backup))
            {
                RemoveBackupMarker(backup);
            }
        }
        var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Do not truncate this scan: an unseen second backup would make recovery ambiguous.
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (name.Length <= 33 || name[^33] != '-' || !Guid.TryParseExact(name.AsSpan(name.Length - 32), "N", out _)) continue;
            string id = name[..^33];
            if (!candidates.TryGetValue(id, out List<string>? paths)) candidates[id] = paths = [];
            paths.Add(directory);
        }
        foreach ((string id, List<string> paths) in candidates)
        {
            string target = Path.Combine(userRoot, id);
            if (Directory.Exists(target) || File.Exists(target) || PluginArtifactLease.IsLink(target)) continue;
            string[] valid = [.. paths.Where(path => IsValidBackup(path, id))];
            if (valid.Length == 1)
            {
                Directory.Move(valid[0], target);
                RemoveBackupMarker(valid[0]);
            }
            // Discovery still checks the protected receipt; recovery never grants trust.
        }
    }

    private static bool IsValidBackup(string directory, string id)
    {
        try
        {
            if (PluginArtifactLease.IsLink(directory)) return false;
            string manifestPath = Path.Combine(directory, PluginManifestReader.FileName);
            if (PluginArtifactLease.IsLink(manifestPath)) return false;
            PluginManifest manifest = PluginManifestReader.Load(manifestPath);
            string entry = Path.GetFullPath(Path.Combine(directory, manifest.Entry));
            return manifest.Id == id
                && entry.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && File.Exists(entry) && !PluginArtifactLease.IsLink(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginManifestException or ArgumentException)
        {
            return false;
        }
    }
}
