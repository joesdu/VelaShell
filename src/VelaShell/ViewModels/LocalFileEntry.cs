namespace VelaShell.ViewModels;

/// <summary>One local filesystem entry displayed by the SFTP document's local pane.</summary>
public sealed record LocalFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModified,
    bool IsReparsePoint = false,
    bool IsParentEntry = false
)
{
    /// <summary>The display name, using <c>..</c> for the synthetic parent row.</summary>
    public string DisplayName => IsParentEntry ? ".." : Name;

    /// <summary>Whether this is a real directory rather than the synthetic parent row.</summary>
    public bool IsRegularDirectory => IsDirectory && !IsParentEntry;

    /// <summary>Whether this is a real file rather than the synthetic parent row.</summary>
    public bool IsRegularFile => !IsDirectory && !IsParentEntry;

    /// <summary>The icon key consumed by a future local-pane view.</summary>
    public string Icon => IsDirectory ? "folder" : "file";

    /// <summary>Human-readable size; directories and the synthetic parent have no file size.</summary>
    public string FormattedSize => IsDirectory ? string.Empty : FormatSize(SizeBytes);

    /// <summary>Human-readable modified time; the synthetic parent has no metadata.</summary>
    public string FormattedModifiedTime => IsParentEntry ? string.Empty : FormatModifiedTime(LastModified);

    /// <summary>Creates the synthetic parent-directory row.</summary>
    public static LocalFileEntry CreateParent(string parentPath) =>
        new("..", parentPath, true, 0, DateTime.MinValue, false, true);

    public static string FormatSize(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), units.Length - 1);
        return $"{bytes / Math.Pow(1024, unit):F1} {units[unit]}";
    }

    private static string FormatModifiedTime(DateTime dateTime)
    {
        DateTime local = dateTime.Kind == DateTimeKind.Unspecified ? dateTime : dateTime.ToLocalTime();
        return local.Year == DateTime.Now.Year
            ? local.ToString("MMM d HH:mm")
            : local.ToString("MMM d, yyyy");
    }
}
