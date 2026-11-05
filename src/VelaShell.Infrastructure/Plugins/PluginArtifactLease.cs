using System.Diagnostics;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>Owns one uniquely named artifact until its consumer finishes, including confirmation dialogs.</summary>
internal sealed class PluginArtifactLease : IDisposable
{
    internal const int SweepLimit = 128;
    private FileStream? _lease;
    internal string Path { get; }
    private readonly bool _directory;

    private PluginArtifactLease(string path, bool directory, FileStream lease)
    {
        Path = path;
        _directory = directory;
        _lease = lease;
    }

    internal static PluginArtifactLease Create(string root, string prefix, string suffix, bool directory)
    {
        CreatePrivateDirectory(root);
        Sweep(root, prefix, suffix, directory);
        string path = System.IO.Path.Combine(root, $"{prefix}{Guid.NewGuid():N}{suffix}");
        var result = new PluginArtifactLease(path, directory, OpenLease(path + ".lease", FileMode.CreateNew));
        try
        {
            if (directory) Directory.CreateDirectory(path);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal static void CreatePrivateDirectory(string root)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (IsLink(root)) throw new IOException("The plugin artifact directory must not be a symbolic link.");
    }

    internal static FileStream OpenLease(string path, FileMode mode)
    {
        if (IsLink(path)) throw new IOException("The plugin artifact lease must not be a symbolic link.");
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows() && mode != FileMode.Open)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    internal static bool IsLink(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal static bool IsArtifactName(string name, string prefix, string suffix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal)
        && name.Length == prefix.Length + 32 + suffix.Length
        && Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _);

    internal static void Sweep(string root, string prefix, string suffix, bool directory)
    {
        try
        {
            if (!Directory.Exists(root) || IsLink(root)) return;
            // Unmarked legacy entries have no ownership protocol: age alone cannot prove they are unused.
            foreach (string marker in Directory.EnumerateFiles(root, "*.lease").Take(SweepLimit))
            {
                string path = marker[..^6];
                if (!IsArtifactName(System.IO.Path.GetFileName(path), prefix, suffix) || IsLink(path)) continue;
                try
                {
                    using var orphan = new PluginArtifactLease(path, directory, OpenLease(marker, FileMode.Open));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An exclusive owner (possibly another host process) still needs this artifact.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[VelaShell] Could not sweep plugin artifacts in '{root}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        FileStream? lease = Interlocked.Exchange(ref _lease, null);
        if (lease is null) return;
        bool deleted = false;
        try
        {
            if (_directory)
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            else
            {
                File.Delete(Path);
                File.Delete(Path + ".partial");
            }
            deleted = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[VelaShell] Could not delete plugin artifact '{Path}': {ex.Message}");
        }
        finally
        {
            lease.Dispose();
            if (deleted)
            {
                try { File.Delete(Path + ".lease"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
