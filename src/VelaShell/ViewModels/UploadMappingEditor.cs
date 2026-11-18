using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.ViewModels;

internal sealed record UploadMappingBinding(string RemotePath, string LocalPath);
internal sealed record UploadDirectoryItem(string Name, string FullPath);
internal sealed record UploadDirectoryListing(string Path, IReadOnlyList<UploadDirectoryItem> Entries);

/// <summary>绑定窗口的目录导航,只提交成功的列举,不持有主资源管理器状态。</summary>
internal sealed class UploadDirectoryPane(
    Func<string, string, CancellationToken, Task<UploadDirectoryListing>> list,
    Func<string, string> parent) : ReactiveObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _navigation;
    private long _version;
    private bool _disposed;
    public ObservableCollection<UploadDirectoryItem> Entries { get; } = [];
    public string CurrentPath { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = string.Empty;
    public string InputPath { get; set => this.RaiseAndSetIfChanged(ref field, value); } = string.Empty;
    public bool IsBusy { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasLoaded { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? ErrorMessage { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public UploadDirectoryItem? SelectedItem
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SelectedPath));
        }
    }
    public string SelectedPath => SelectedItem?.FullPath ?? CurrentPath;
    public Task<bool> GoUpAsync() => NavigateAsync(parent(CurrentPath));

    public async Task<bool> NavigateAsync(string path)
    {
        if (_disposed)
        {
            return false;
        }
        long version = ++_version;
        _navigation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _navigation = cancellation;
        IsBusy = true;
        try
        {
            UploadDirectoryListing listing = await list(path, CurrentPath, cancellation.Token);
            if (_disposed || version != _version)
            {
                return false;
            }
            CurrentPath = InputPath = listing.Path;
            SelectedItem = null;
            Entries.Clear();
            foreach (UploadDirectoryItem item in listing.Entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Entries.Add(item);
            }
            HasLoaded = true;
            ErrorMessage = null;
            this.RaisePropertyChanged(nameof(SelectedPath));
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed && version == _version)
            {
                ErrorMessage = ex.Message;
            }
            return false;
        }
        finally
        {
            if (!_disposed && version == _version)
            {
                IsBusy = false;
                _navigation = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        ++_version;
        _lifetime.Cancel();
        _navigation = null;
        _lifetime.Dispose();
    }
}

internal sealed class UploadMappingEditor : IDisposable
{
    public UploadDirectoryPane Remote { get; }
    public UploadDirectoryPane Local { get; }

    public UploadMappingEditor(ISftpService service, Guid sessionId, ILocalFileSystem? localFileSystem = null)
    {
        Remote = new(async (input, current, ct) =>
        {
            string path = input;
            if (path.StartsWith('~'))
            {
                path = await service.ExpandPathAsync(sessionId, path, ct)
                    ?? throw new IOException(Core.Resources.Strings.Get("Sftp_UploadMappingInvalidRemote"));
            }
            if (!path.StartsWith('/'))
            {
                path = current.TrimEnd('/') + "/" + path;
            }
            path = RemoteDirectoryBookmark.NormalizeRemotePath(path);
            List<RemoteFileInfo> entries = await service.ListDirectoryAsync(sessionId, path, ct);
            return new(path, [.. entries.Where(item => item.IsDirectory && item.Name is not "." and not "..")
                .Select(item => new UploadDirectoryItem(item.Name, item.FullPath))]);
        }, path => RemoteDirectoryBookmark.NormalizeRemotePath(path + "/.."));
        ILocalFileSystem local = localFileSystem ?? new PhysicalLocalFileSystem();
        Local = new(async (input, current, ct) =>
        {
            string path = LocalFilePaneViewModel.ResolvePathInput(input, current)
                ?? throw new IOException(Core.Resources.Strings.Get("Sftp_UploadMappingInvalidLocal"));
            path = RemoteDirectoryBookmark.NormalizeLocalPath(path);
            IReadOnlyList<LocalFileSystemEntry> entries = await local.EnumerateAsync(path, ct);
            return new(path, [.. entries.Where(item => item.IsDirectory)
                .Select(item => new UploadDirectoryItem(item.Name, item.FullPath))]);
        }, path => Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? path);
    }

    public bool CanSave => Remote.HasLoaded && Local.HasLoaded && !Remote.IsBusy && !Local.IsBusy;

    public Task InitializeAsync(string remote, string local) => Task.WhenAll(Remote.NavigateAsync(remote), Local.NavigateAsync(local));

    public async Task<UploadMappingBinding?> ValidateAsync()
    {
        if (!CanSave)
        {
            return null;
        }
        // 单击选中的子目录也可直接绑定;确认时两侧都重新列举以核实仍可访问。
        string remote = Remote.InputPath != Remote.CurrentPath ? Remote.InputPath : Remote.SelectedPath;
        string local = Local.InputPath != Local.CurrentPath ? Local.InputPath : Local.SelectedPath;
        bool[] valid = await Task.WhenAll(Remote.NavigateAsync(remote), Local.NavigateAsync(local));
        return valid.All(value => value) ? new(Remote.CurrentPath, Local.CurrentPath) : null;
    }

    public void Dispose() { Remote.Dispose(); Local.Dispose(); }
}
