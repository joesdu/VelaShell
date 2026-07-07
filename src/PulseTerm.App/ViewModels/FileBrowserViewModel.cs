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
    private string _sortColumn = "name";
    private bool _sortDescending;

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
        ActivateCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(ActivateAsync);
        GoUpCommand = ReactiveCommand.CreateFromTask(GoUpAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        LoadInitialCommand = ReactiveCommand.CreateFromTask(LoadInitialAsync);
        UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
        NewFolderCommand = ReactiveCommand.CreateFromTask(NewFolderAsync);
        NewFileCommand = ReactiveCommand.CreateFromTask(NewFileAsync);
        DownloadItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(DownloadItemAsync);
        RenameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(RenameAsync);
        MoveCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(MoveAsync);
        CopyPathCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyPathAsync);
        CopyNameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyNameAsync);
        PropertiesCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(ShowPropertiesAsync);
        ToggleVisibilityCommand = ReactiveCommand.Create(ToggleVisibility);
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
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

    /// <summary>Row activation (double-click / Enter): descend into directories.</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> ActivateCommand { get; }
    public ReactiveCommand<Unit, Unit> GoUpCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Loads the account's home directory (spec: land in ~, not filesystem root).</summary>
    public ReactiveCommand<Unit, Unit> LoadInitialCommand { get; }
    /// <summary>Uploads OS-picked files into the current directory (toolbar + right-click).</summary>
    public ReactiveCommand<Unit, Unit> UploadCommand { get; }

    // Right-click context-menu actions (spec: file operations live in the SFTP context menu).
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> NewFileCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> DownloadItemCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> RenameCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> MoveCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> CopyPathCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> CopyNameCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> PropertiesCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVisibilityCommand { get; }

    /// <summary>Sorts by a column key ("name" | "size" | "permissions" | "modified"); clicking the
    /// active column again flips the direction.</summary>
    public ReactiveCommand<string, Unit> SortCommand { get; }

    /// <summary>The column the list is currently ordered by.</summary>
    public string SortColumn
    {
        get => _sortColumn;
        private set => this.RaiseAndSetIfChanged(ref _sortColumn, value);
    }

    /// <summary>Whether the current sort is descending.</summary>
    public bool SortDescending
    {
        get => _sortDescending;
        private set => this.RaiseAndSetIfChanged(ref _sortDescending, value);
    }

    public string NameSortGlyph => GlyphFor("name");
    public string SizeSortGlyph => GlyphFor("size");
    public string PermissionsSortGlyph => GlyphFor("permissions");
    public string ModifiedSortGlyph => GlyphFor("modified");

    private string GlyphFor(string column) =>
        SortColumn == column ? (SortDescending ? " ▼" : " ▲") : string.Empty;

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
            var items = files.Select(f => new RemoteFileInfoViewModel(f));

            Files.Clear();
            foreach (var file in SortFiles(items))
            {
                Files.Add(file);
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

    /// <summary>Loads the SFTP account's home directory (its post-login working directory) rather
    /// than the filesystem root, falling back to "/" if it can't be resolved.</summary>
    private async Task LoadInitialAsync(CancellationToken ct = default)
    {
        var home = "/";
        if (_sftpService is not null)
        {
            try
            {
                var working = await _sftpService.GetWorkingDirectoryAsync(_sessionId, ct);
                if (!string.IsNullOrWhiteSpace(working))
                    home = working;
            }
            catch
            {
                // Resolving the home directory is best-effort; fall back to root.
            }
        }

        await NavigateToAsync(home, ct);
    }

    /// <summary>Sets or flips the sort, then reorders the currently loaded rows in place.</summary>
    private void ToggleSort(string column)
    {
        if (string.IsNullOrWhiteSpace(column))
            return;

        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        this.RaisePropertyChanged(nameof(NameSortGlyph));
        this.RaisePropertyChanged(nameof(SizeSortGlyph));
        this.RaisePropertyChanged(nameof(PermissionsSortGlyph));
        this.RaisePropertyChanged(nameof(ModifiedSortGlyph));

        var sorted = SortFiles(Files.ToList());
        Files.Clear();
        foreach (var file in sorted)
        {
            Files.Add(file);
        }
    }

    /// <summary>Orders rows by the active column and direction, keeping directories grouped first
    /// (a directory's size is meaningless, so mixing them into a size sort reads badly).</summary>
    private IEnumerable<RemoteFileInfoViewModel> SortFiles(IEnumerable<RemoteFileInfoViewModel> items)
    {
        var dirsFirst = items.OrderByDescending(f => f.IsDirectory);

        return SortColumn switch
        {
            "size" => SortDescending
                ? dirsFirst.ThenByDescending(f => f.SizeBytes)
                : dirsFirst.ThenBy(f => f.SizeBytes),
            "permissions" => SortDescending
                ? dirsFirst.ThenByDescending(f => f.Permissions, StringComparer.Ordinal)
                : dirsFirst.ThenBy(f => f.Permissions, StringComparer.Ordinal),
            "modified" => SortDescending
                ? dirsFirst.ThenByDescending(f => f.LastModified)
                : dirsFirst.ThenBy(f => f.LastModified),
            _ => SortDescending
                ? dirsFirst.ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    private async Task ActivateAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (file is null || !file.IsDirectory)
            return;

        await NavigateToAsync(file.FullPath, ct);
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

    /// <summary>Set by the view: prompts for a line of text (title, initial value) → entered text or
    /// null if cancelled. Used by new folder / new file / rename / move.</summary>
    public Func<string, string, Task<string?>>? PromptForText { get; set; }

    /// <summary>Set by the view: writes text to the OS clipboard (copy path / copy name).</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Set by the view: shows a file's properties in a modal.</summary>
    public Func<RemoteFileInfoViewModel, Task>? ShowFileProperties { get; set; }

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

    private async Task DownloadItemAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (_sftpService is null || PickSavePathForDownload is null || file is null || file.IsDirectory)
            return;

        var localPath = await PickSavePathForDownload(file.Name);
        if (string.IsNullOrEmpty(localPath))
            return;

        await RunTransferAsync(TransferType.Download, localPath, file.FullPath, ct);
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

    private async Task NewFolderAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PromptForText is null)
            return;

        var name = await PromptForText("新建文件夹", "");
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            ErrorMessage = null;
            await _sftpService.CreateDirectoryAsync(_sessionId, CombinePath(CurrentPath, name.Trim()), ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task NewFileAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PromptForText is null)
            return;

        var name = await PromptForText("新建文件", "");
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            ErrorMessage = null;
            await _sftpService.CreateFileAsync(_sessionId, CombinePath(CurrentPath, name.Trim()), ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task RenameAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (_sftpService is null || PromptForText is null || file is null)
            return;

        var newName = await PromptForText("重命名", file.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == file.Name)
            return;

        try
        {
            ErrorMessage = null;
            var target = CombinePath(ParentOf(file.FullPath), newName.Trim());
            await _sftpService.RenameAsync(_sessionId, file.FullPath, target, ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task MoveAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (_sftpService is null || PromptForText is null || file is null)
            return;

        var destination = await PromptForText("移动到（目标完整路径）", file.FullPath);
        if (string.IsNullOrWhiteSpace(destination) || destination.Trim() == file.FullPath)
            return;

        try
        {
            ErrorMessage = null;
            await _sftpService.RenameAsync(_sessionId, file.FullPath, destination.Trim(), ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task CopyPathAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (CopyToClipboard is null || file is null)
            return;

        await CopyToClipboard(file.FullPath);
    }

    private async Task CopyNameAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (CopyToClipboard is null || file is null)
            return;

        await CopyToClipboard(file.Name);
    }

    private async Task ShowPropertiesAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (ShowFileProperties is null || file is null)
            return;

        await ShowFileProperties(file);
    }

    /// <summary>Joins a directory and a leaf name into a Unix-style remote path.</summary>
    private static string CombinePath(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    /// <summary>The parent directory of a Unix-style remote path.</summary>
    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }

    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }
}
