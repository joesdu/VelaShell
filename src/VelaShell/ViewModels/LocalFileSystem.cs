namespace VelaShell.ViewModels;

/// <summary>One selectable local filesystem root.</summary>
public sealed record LocalRootEntry(
    string DisplayName,
    string FullPath,
    bool IsAccessible,
    string Tooltip
);

/// <summary>Filesystem metadata used by the local pane's narrow deletion seam.</summary>
internal sealed record LocalFileSystemEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModified,
    bool IsReparsePoint = false
);

/// <summary>Minimal filesystem seam for deterministic local-pane traversal and deletion tests.</summary>
internal interface ILocalFileSystem
{
    Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(
        string path,
        CancellationToken cancellationToken
    );

    Task DeleteFileAsync(string path, CancellationToken cancellationToken);

    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Enumerates platform-local roots without coupling the pane to DriveInfo.</summary>
internal interface ILocalRootProvider
{
    Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken);
}

internal sealed class PhysicalLocalFileSystem : ILocalFileSystem
{
    public Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(
        string path,
        CancellationToken cancellationToken
    ) =>
        Task.Run<IReadOnlyList<LocalFileSystemEntry>>(
            () =>
            {
                var entries = new List<LocalFileSystemEntry>();
                foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool reparse = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    bool directory = info.Attributes.HasFlag(FileAttributes.Directory);
                    long size = directory || reparse ? 0 : ((FileInfo)info).Length;
                    entries.Add(
                        new(info.Name, info.FullName, directory, size, info.LastWriteTime, reparse)
                    );
                }
                return entries;
            },
            cancellationToken
        );

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(path);
            },
            cancellationToken
        );

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(path);
            },
            cancellationToken
        );
}

internal sealed class PhysicalLocalRootProvider : ILocalRootProvider
{
    public Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<LocalRootEntry>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string home = Canonicalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var candidates = new List<(string DisplayName, string FullPath)> { ("~", home) };

                if (OperatingSystem.IsWindows())
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string path;
                        try
                        {
                            path = Canonicalize(drive.Name);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotFoundException)
                        {
                            continue;
                        }
                        candidates.Add((path, path));
                    }
                }
                else
                {
                    candidates.Add(("/", "/"));
                }

                return candidates
                    .DistinctBy(candidate => candidate.FullPath, PathComparer)
                    .Select(candidate =>
                    {
                        bool accessible = IsAccessible(candidate.FullPath);
                        return new LocalRootEntry(
                            candidate.DisplayName,
                            candidate.FullPath,
                            accessible,
                            candidate.FullPath
                        );
                    })
                    .ToArray();
            },
            cancellationToken
        );

    private static string Canonicalize(string path) => Path.GetFullPath(path);

    private static bool IsAccessible(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
