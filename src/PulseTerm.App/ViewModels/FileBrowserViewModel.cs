using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class FileBrowserViewModel : ReactiveObject
{
    private readonly ISftpService _sftpService;
    private readonly Guid _sessionId;

    private string _currentPath;
    private bool _isLoading;
    private bool _isVisible;
    private string? _errorMessage;

    public FileBrowserViewModel(ISftpService? sftpService, Guid sessionId)
    {
        _sftpService = sftpService!;
        _sessionId = sessionId;
        SessionId = sessionId;
        _currentPath = "/";
        _isVisible = false;

        Files = new ObservableCollection<RemoteFileInfoViewModel>();
        SelectedFiles = new ObservableCollection<RemoteFileInfoViewModel>();

        NavigateToCommand = ReactiveCommand.CreateFromTask<string>(NavigateToAsync);
        GoUpCommand = ReactiveCommand.CreateFromTask(GoUpAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
        DownloadCommand = ReactiveCommand.CreateFromTask(DownloadAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        CreateFolderCommand = ReactiveCommand.CreateFromTask(CreateFolderAsync);
        ToggleVisibilityCommand = ReactiveCommand.Create(ToggleVisibility);
    }

    /// <summary>The SSH session this browser is rooted at.</summary>
    public Guid SessionId { get; }

    public ObservableCollection<RemoteFileInfoViewModel> Files { get; }

    public ObservableCollection<RemoteFileInfoViewModel> SelectedFiles { get; }

    public string CurrentPath
    {
        get => _currentPath;
        set => this.RaiseAndSetIfChanged(ref _currentPath, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public ReactiveCommand<string, Unit> NavigateToCommand { get; }
    public ReactiveCommand<Unit, Unit> GoUpCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVisibilityCommand { get; }

    public string[] PathSegments => CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private async Task NavigateToAsync(string path, CancellationToken ct = default)
    {
        if (_sftpService is null)
        {
            ErrorMessage = null;
            CurrentPath = path;
            return;
        }

        try
        {
            ErrorMessage = null;
            IsLoading = true;
            CurrentPath = path;

            var files = await _sftpService.ListDirectoryAsync(_sessionId, path, ct);

            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(new RemoteFileInfoViewModel(file));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task GoUpAsync(CancellationToken ct = default)
    {
        if (CurrentPath == "/") return;

        var parentIndex = CurrentPath.TrimEnd('/').LastIndexOf('/');
        var parentPath = parentIndex <= 0 ? "/" : CurrentPath.Substring(0, parentIndex);

        await NavigateToAsync(parentPath, ct);
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        await NavigateToAsync(CurrentPath, ct);
    }

    /// <summary>Set by the view: opens the OS file picker and returns local paths to upload.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFilesForUpload { get; set; }

    /// <summary>Set by the view: asks where to save a download (arg = suggested file name).</summary>
    public Func<string, Task<string?>>? PickSavePathForDownload { get; set; }

    /// <summary>The floating transfer toast fed by uploads/downloads started here (spec §9).</summary>
    public FileTransferViewModel? TransferSink { get; set; }

    private async Task UploadAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PickFilesForUpload is null)
            return;

        var files = await PickFilesForUpload();
        foreach (var localPath in files)
        {
            var name = System.IO.Path.GetFileName(localPath);
            var remotePath = CurrentPath.TrimEnd('/') + "/" + name;
            await RunTransferAsync(TransferType.Upload, localPath, remotePath, ct);
        }

        await RefreshAsync(ct);
    }

    private async Task DownloadAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PickSavePathForDownload is null)
            return;

        var selected = SelectedFiles.FirstOrDefault(f => !f.IsDirectory);
        if (selected is null)
            return;

        var localPath = await PickSavePathForDownload(selected.Name);
        if (string.IsNullOrEmpty(localPath))
            return;

        var remotePath = CurrentPath.TrimEnd('/') + "/" + selected.Name;
        await RunTransferAsync(TransferType.Download, localPath, remotePath, ct);
    }

    /// <summary>Runs one transfer end to end: registers it with the toast, streams progress
    /// into it, and settles the final state. Failures mark the row red instead of throwing.</summary>
    private async Task RunTransferAsync(TransferType type, string localPath, string remotePath, CancellationToken ct)
    {
        var task = new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = type,
            LocalPath = localPath,
            RemotePath = remotePath,
            Status = TransferStatus.InProgress,
        };

        TransferSink?.AddTransfer(task);
        var item = TransferSink?.FindTransfer(task.Id);
        var progress = new Progress<TransferProgress>(p => item?.UpdateProgress(p));

        try
        {
            if (type == TransferType.Upload)
                await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress, ct);
            else
                await _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath, progress, ct);

            if (item is not null)
                item.Status = TransferStatus.Completed;
        }
        catch (Exception ex)
        {
            if (item is not null)
                item.Status = TransferStatus.Failed;
            ErrorMessage = ex.Message;
        }
        finally
        {
            TransferSink?.NotifyTaskSettled();
        }
    }

    private async Task DeleteAsync(CancellationToken ct = default)
    {
        if (_sftpService is null)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var toDelete = SelectedFiles.ToList();

            foreach (var file in toDelete)
            {
                await _sftpService.DeleteAsync(_sessionId, file.FullPath, ct);
            }

            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task CreateFolderAsync(CancellationToken ct = default)
    {
        if (_sftpService is null)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var newFolderPath = CurrentPath.TrimEnd('/') + "/New Folder";
            await _sftpService.CreateDirectoryAsync(_sessionId, newFolderPath, ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }
}
