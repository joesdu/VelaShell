using System;
using PulseTerm.Core.Models;

namespace PulseTerm.App.ViewModels;

public class RemoteFileInfoViewModel
{
    private readonly RemoteFileInfo _model;

    public RemoteFileInfoViewModel(RemoteFileInfo model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>The synthetic ".." first row (§6): navigates to the parent directory on
    /// activation and offers no file operations.</summary>
    public static RemoteFileInfoViewModel CreateParentEntry(string parentPath) =>
        new(new RemoteFileInfo
        {
            Name = "..",
            FullPath = parentPath,
            Size = 0,
            Permissions = string.Empty,
            IsDirectory = true,
            LastModified = DateTime.MinValue,
            Owner = string.Empty,
            Group = string.Empty,
        })
        { IsParentEntry = true };

    /// <summary>True only for the synthetic ".." row.</summary>
    public bool IsParentEntry { get; private init; }

    /// <summary>A real directory row (excludes the synthetic ".." row) — drives the amber
    /// folder icon and blue name styling from design dyuii.</summary>
    public bool IsRegularDirectory => IsDirectory && !IsParentEntry;

    public string Name => _model.Name;

    /// <summary>List display name: the plain entry name. The amber folder icon already marks
    /// directories, so no trailing slash is appended.</summary>
    public string DisplayName => IsParentEntry ? ".." : Name;

    public string FullPath => _model.FullPath;

    public bool IsDirectory => _model.IsDirectory;

    public string Permissions => _model.Permissions;

    public string Owner => _model.Owner;

    public string Group => _model.Group;

    public string Icon => _model.IsDirectory ? "folder" : "file";

    // Directories show their reported size too (design dyuii lists "4.0 KB" for folders).
    public string FormattedSize => IsParentEntry ? string.Empty : FormatSize(_model.Size);

    public string FormattedModifiedTime => IsParentEntry ? string.Empty : FormatModifiedTime(_model.LastModified);

    public long SizeBytes => _model.Size;

    public DateTime LastModified => _model.LastModified;

    public static string FormatSize(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }

    /// <summary>ls -l style timestamp per design dyuii ("Jan 12 09:15"): time within the current
    /// year, otherwise the year replaces the clock.</summary>
    public static string FormatModifiedTime(DateTime dateTime)
    {
        var local = dateTime.Kind == DateTimeKind.Unspecified
            ? dateTime
            : dateTime.ToLocalTime();

        return local.Year == DateTime.Now.Year
            ? local.ToString("MMM d HH:mm")
            : local.ToString("MMM d, yyyy");
    }
}
