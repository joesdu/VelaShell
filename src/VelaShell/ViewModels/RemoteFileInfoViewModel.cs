using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

/// <summary>Presents a <see cref="RemoteFileInfo"/> as a file-browser row, adding display formatting and parent-entry handling.</summary>
/// <param name="model">The underlying remote file entry backing this row.</param>
public class RemoteFileInfoViewModel(RemoteFileInfo model)
{
    /// <summary>底层条目(静默刷新的差异比对用,见 FileBrowserViewModel.RefreshSilentlyAsync)。</summary>
    internal RemoteFileInfo Model { get; } = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>True only for the synthetic ".." row.</summary>
    public bool IsParentEntry { get; private init; }

    /// <summary>
    /// A real directory row (excludes the synthetic ".." row) — drives the amber
    /// folder icon and blue name styling from design dyuii.
    /// </summary>
    public bool IsRegularDirectory => IsDirectory && !IsParentEntry;

    /// <summary>A real file row — gates the file-only context actions (打开/编辑器打开等)。</summary>
    public bool IsRegularFile => !IsDirectory && !IsParentEntry;

    /// <summary>The raw entry name from the remote listing.</summary>
    public string Name => Model.Name;

    /// <summary>
    /// List display name: the plain entry name. The amber folder icon already marks
    /// directories, so no trailing slash is appended.
    /// </summary>
    public string DisplayName => IsParentEntry ? ".." : Name;

    /// <summary>The absolute path of the entry on the remote host.</summary>
    public string FullPath => Model.FullPath;

    /// <summary>Whether the entry is a directory.</summary>
    public bool IsDirectory => Model.IsDirectory;

    /// <summary>The permission string (e.g. rwxr-xr-x) reported by the remote host.</summary>
    public string Permissions => Model.Permissions;

    /// <summary>The owning user of the entry.</summary>
    public string Owner => Model.Owner;

    /// <summary>The owning group of the entry.</summary>
    public string Group => Model.Group;

    /// <summary>The icon key ("folder" or "file") used to render the row.</summary>
    public string Icon => Model.IsDirectory ? "folder" : "file";

    // Directories show their reported size too (design dyuii lists "4.0 KB" for folders).
    /// <summary>Human-readable size for display; empty for the synthetic ".." row.</summary>
    public string FormattedSize => IsParentEntry ? string.Empty : FormatSize(Model.Size);

    /// <summary>Human-readable last-modified timestamp for display; empty for the synthetic ".." row.</summary>
    public string FormattedModifiedTime => IsParentEntry ? string.Empty : FormatModifiedTime(Model.LastModified);

    /// <summary>The entry size in bytes.</summary>
    public long SizeBytes => Model.Size;

    /// <summary>The entry's last-modified time.</summary>
    public DateTime LastModified => Model.LastModified;

    /// <summary>
    /// The synthetic ".." first row (§6): navigates to the parent directory on
    /// activation and offers no file operations.
    /// </summary>
    public static RemoteFileInfoViewModel CreateParentEntry(string parentPath) =>
        new(new()
        {
            Name = "..",
            FullPath = parentPath,
            Size = 0,
            Permissions = string.Empty,
            IsDirectory = true,
            LastModified = DateTime.MinValue,
            Owner = string.Empty,
            Group = string.Empty
        })
        { IsParentEntry = true };

    /// <summary>Formats a byte count as a human-readable size string (e.g. "4.0 KB").</summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The formatted size string with an appropriate unit suffix.</returns>
    public static string FormatSize(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }

    /// <summary>
    /// ls -l style timestamp per design dyuii ("Jan 12 09:15"): time within the current
    /// year, otherwise the year replaces the clock.
    /// </summary>
    private static string FormatModifiedTime(DateTime dateTime)
    {
        DateTime local = dateTime.Kind == DateTimeKind.Unspecified
                             ? dateTime
                             : dateTime.ToLocalTime();
        return local.Year == DateTime.Now.Year
                   ? local.ToString("MMM d HH:mm")
                   : local.ToString("MMM d, yyyy");
    }
}
