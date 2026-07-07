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

    public string Name => _model.Name;

    public string FullPath => _model.FullPath;

    public bool IsDirectory => _model.IsDirectory;

    public string Permissions => _model.Permissions;

    public string Icon => _model.IsDirectory ? "folder" : "file";

    public string FormattedSize => IsParentEntry ? string.Empty : _model.IsDirectory ? "--" : FormatSize(_model.Size);

    public string FormattedModifiedTime => IsParentEntry ? string.Empty : FormatRelativeTime(_model.LastModified);

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

    public static string FormatRelativeTime(DateTime dateTime)
    {
        var now = DateTime.UtcNow;
        var local = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
        var elapsed = now - local;

        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60)
        {
            var mins = (int)elapsed.TotalMinutes;
            return mins == 1 ? "1 minute ago" : $"{mins} minutes ago";
        }
        if (elapsed.TotalHours < 24)
        {
            var hours = (int)elapsed.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        return dateTime.ToString("MMM d, yyyy");
    }
}
