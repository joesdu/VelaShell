using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

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

    public string Name => Model.Name;

    /// <summary>
    /// List display name: the plain entry name. The amber folder icon already marks
    /// directories, so no trailing slash is appended.
    /// </summary>
    public string DisplayName => IsParentEntry ? ".." : Name;

    public string FullPath => Model.FullPath;

    public bool IsDirectory => Model.IsDirectory;

    public string Permissions => Model.Permissions;

    public string Owner => Model.Owner;

    public string Group => Model.Group;

    public string Icon => Model.IsDirectory ? "folder" : "file";

    // Directories show their reported size too (design dyuii lists "4.0 KB" for folders).
    public string FormattedSize => IsParentEntry ? string.Empty : FormatSize(Model.Size);

    public string FormattedModifiedTime => IsParentEntry ? string.Empty : FormatModifiedTime(Model.LastModified);

    public long SizeBytes => Model.Size;

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
