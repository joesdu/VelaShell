using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using ReactiveUI;

namespace VelaShell.App.ViewModels;

/// <summary>One clickable segment of the header path breadcrumb (§6): the segment text and the
/// absolute remote path it navigates to.</summary>
public sealed record BreadcrumbSegment(string Name, string Path);

public class FileBrowserViewModel : ReactiveObject
{
    private readonly ISftpService _sftpService;
    private readonly Guid _sessionId;

    /// <summary>The raw directory listing before the hidden-files filter/sort; the visible
    /// <see cref="Files"/> collection is rebuilt from this.</summary>
    private readonly List<RemoteFileInfoViewModel> _allFiles = new();

    private string _currentPath;
    private bool _isLoading;
    private bool _isVisible;
    private string? _errorMessage;
    private string _busyText = Strings.Loading;
    private bool _isDeleteProgressVisible;
    private double _deleteProgressPercent;
    private bool _isDeleteProgressIndeterminate;
    private bool _showHiddenFiles;
    private Avalonia.Controls.GridLength _nameColumnWidth = new(280);
    private Avalonia.Controls.GridLength _sizeColumnWidth = new(100);
    private Avalonia.Controls.GridLength _permissionsColumnWidth = new(110);
    private string _sortColumn = "name";
    private bool _sortDescending;

    /// <summary>Cancels the running transfer batch (upload/download); tripped from the toast.</summary>
    private CancellationTokenSource? _transferCts;

    /// <summary>Cancels the running delete; tripped from the delete overlay's cancel button.</summary>
    private CancellationTokenSource? _deleteCts;

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
        UploadFolderCommand = ReactiveCommand.CreateFromTask(UploadFolderAsync);
        NewFolderCommand = ReactiveCommand.CreateFromTask(NewFolderAsync);
        NewFileCommand = ReactiveCommand.CreateFromTask(NewFileAsync);
        DownloadItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(DownloadItemAsync);
        RenameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(RenameAsync);
        MoveCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(MoveAsync);
        CopyPathCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyPathAsync);
        CopyNameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyNameAsync);
        PropertiesCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(ShowPropertiesAsync);
        DeleteItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(DeleteItemAsync);
        OpenItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(OpenItemAsync);
        OpenWithDefaultEditorCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(OpenWithDefaultEditorAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(DownloadSelectedAsync);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync);
        CancelDeleteCommand = ReactiveCommand.Create(CancelDelete);
        ShowTransfersCommand = ReactiveCommand.Create(() => TransferSink?.ShowPanel());
        ToggleVisibilityCommand = ReactiveCommand.Create(ToggleVisibility);
        ToggleHiddenFilesCommand = ReactiveCommand.Create(() => { ShowHiddenFiles = !ShowHiddenFiles; });
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
    }

    /// <summary>The SSH session this browser is rooted at.</summary>
    public Guid SessionId { get; }

    public ObservableCollection<RemoteFileInfoViewModel> Files { get; }

    public ObservableCollection<RemoteFileInfoViewModel> SelectedFiles { get; }

    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (_currentPath == value)
                return;

            this.RaiseAndSetIfChanged(ref _currentPath, value);
            this.RaisePropertyChanged(nameof(Breadcrumbs));
        }
    }

    /// <summary>Clickable breadcrumb segments for the current path, deepest last (§6 header).</summary>
    public IReadOnlyList<BreadcrumbSegment> Breadcrumbs
    {
        get
        {
            var segments = new List<BreadcrumbSegment>();
            var path = "";
            foreach (var part in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path += "/" + part;
                segments.Add(new BreadcrumbSegment(part, path));
            }

            return segments;
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>Message shown on the busy overlay (loading a directory, or delete progress).</summary>
    public string BusyText
    {
        get => _busyText;
        set => this.RaiseAndSetIfChanged(ref _busyText, value);
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

    /// <summary>Whether dotfiles are listed. Off by default per §6 (hidden-files toggle).</summary>
    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        set
        {
            if (_showHiddenFiles == value)
                return;

            this.RaiseAndSetIfChanged(ref _showHiddenFiles, value);
            RebuildVisibleFiles();
        }
    }

    public Avalonia.Controls.GridLength NameColumnWidth
    {
        get => _nameColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _nameColumnWidth, ClampColumnWidth(value, 180));
    }

    public Avalonia.Controls.GridLength SizeColumnWidth
    {
        get => _sizeColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _sizeColumnWidth, ClampColumnWidth(value, 70));
    }

    public Avalonia.Controls.GridLength PermissionsColumnWidth
    {
        get => _permissionsColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _permissionsColumnWidth, ClampColumnWidth(value, 80));
    }

    private static Avalonia.Controls.GridLength ClampColumnWidth(Avalonia.Controls.GridLength value, double min)
    {
        // We only support pixel-sized user-resizable columns here.
        var px = value.IsAbsolute ? value.Value : min;
        return new Avalonia.Controls.GridLength(Math.Max(min, px));
    }

    /// <summary>Whether the loading overlay should show a delete progress bar.</summary>
    public bool IsDeleteProgressVisible
    {
        get => _isDeleteProgressVisible;
        private set => this.RaiseAndSetIfChanged(ref _isDeleteProgressVisible, value);
    }

    /// <summary>Delete progress percentage [0,100] for the overlay progress bar.</summary>
    public double DeleteProgressPercent
    {
        get => _deleteProgressPercent;
        private set => this.RaiseAndSetIfChanged(ref _deleteProgressPercent, value);
    }

    /// <summary>When true, delete progress is shown as indeterminate (e.g., before total is known).</summary>
    public bool IsDeleteProgressIndeterminate
    {
        get => _isDeleteProgressIndeterminate;
        private set => this.RaiseAndSetIfChanged(ref _isDeleteProgressIndeterminate, value);
    }

    public ReactiveCommand<string, Unit> NavigateToCommand { get; }

    /// <summary>Row activation (double-click / Enter): descend into directories, or download a
    /// file to a temp folder and open it with the OS default program (§6).</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> ActivateCommand { get; }
    public ReactiveCommand<Unit, Unit> GoUpCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Loads the account's home directory (spec: land in ~, not filesystem root).</summary>
    public ReactiveCommand<Unit, Unit> LoadInitialCommand { get; }
    /// <summary>Uploads OS-picked files into the current directory (toolbar + right-click).</summary>
    public ReactiveCommand<Unit, Unit> UploadCommand { get; }

    /// <summary>Uploads an OS-picked folder (recursively) into the current directory.</summary>
    public ReactiveCommand<Unit, Unit> UploadFolderCommand { get; }

    // Right-click context-menu actions (spec: file operations live in the SFTP context menu).
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> NewFileCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> DownloadItemCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> RenameCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> MoveCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> CopyPathCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> CopyNameCommand { get; }
    /// <summary>属性弹窗(合并了 chmod 权限编辑,确定时应用变更)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> PropertiesCommand { get; }
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> DeleteItemCommand { get; }

    /// <summary>「打开」:下载到临时副本后交给内置 AvaloniaEdit 编辑器(保存即上传)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> OpenItemCommand { get; }

    /// <summary>「使用默认编辑器打开」:下载到 temp 交给设置里配置的编辑器,保存即上传。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> OpenWithDefaultEditorCommand { get; }

    /// <summary>Batch download of the multi-selection into one picked local folder (§6 multi-select).</summary>
    public ReactiveCommand<Unit, Unit> DownloadSelectedCommand { get; }

    /// <summary>Batch delete of the multi-selection after a single confirmation (§6 multi-select).</summary>
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

    /// <summary>Cancels an in-progress delete, leaving already-deleted entries removed.</summary>
    public ReactiveCommand<Unit, Unit> CancelDeleteCommand { get; }
    /// <summary>Reopens the transfer toast so past/active transfers can be reviewed (toolbar button
    /// next to Upload). Without it the toast auto-hides and there's no way back to the history.</summary>
    public ReactiveCommand<Unit, Unit> ShowTransfersCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVisibilityCommand { get; }

    /// <summary>Toggles dotfile visibility (§6 header switch).</summary>
    public ReactiveCommand<Unit, Unit> ToggleHiddenFilesCommand { get; }

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
            BusyText = Strings.Loading;
            IsLoading = true;
            CurrentPath = path;

            var files = await _sftpService.ListDirectoryAsync(_sessionId, path, ct);

            _allFiles.Clear();
            _allFiles.AddRange(files.Select(f => new RemoteFileInfoViewModel(f)));
            RebuildVisibleFiles();
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

    /// <summary>始终打开登录账户的家目录(登录后的工作目录,如 pi → /home/pi、root → /root)。
    /// 家目录在服务器上不存在或不可访问(如 realpath(".") 返回的目录未创建/被 chroot)时,
    /// 自动回退到根目录 "/",避免停在报错的空白页。</summary>
    private async Task LoadInitialAsync(CancellationToken ct = default)
    {
        var candidates = new List<string>();

        if (_sftpService is not null)
        {
            try
            {
                var working = await _sftpService.GetWorkingDirectoryAsync(_sessionId, ct);
                if (!string.IsNullOrWhiteSpace(working))
                    candidates.Add(working);
            }
            catch
            {
                // 解析家目录尽力而为,失败则继续走根目录。
            }
        }

        candidates.Add("/");

        foreach (var path in candidates.Distinct())
        {
            await NavigateToAsync(path, ct);
            // NavigateToAsync 会吞掉异常并写入 ErrorMessage;为空即表示该目录成功打开。
            if (string.IsNullOrEmpty(ErrorMessage))
                return;
        }
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

        RebuildVisibleFiles();
    }

    /// <summary>Rebuilds the visible rows from the raw listing: hidden-files filter, the active
    /// sort, and (outside the root) a leading ".." row that navigates to the parent (§6).</summary>
    private void RebuildVisibleFiles()
    {
        var visible = _allFiles.Where(f => ShowHiddenFiles || !f.Name.StartsWith('.'));

        Files.Clear();

        if (CurrentPath != "/")
            Files.Add(RemoteFileInfoViewModel.CreateParentEntry(ParentOf(CurrentPath)));

        foreach (var file in SortFiles(visible))
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
        if (file is null)
            return;

        if (file.IsDirectory)
        {
            await NavigateToAsync(file.FullPath, ct);
            return;
        }

        await DownloadAndOpenAsync(file, ct);
    }

    /// <summary>§6: double-clicking a file downloads it to a per-session temp folder (progress in
    /// the transfer toast) and opens it with the OS default program.</summary>
    private async Task DownloadAndOpenAsync(RemoteFileInfoViewModel file, CancellationToken ct)
    {
        if (_sftpService is null || OpenLocalFile is null)
            return;

        try
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VelaShell", _sessionId.ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            var localPath = System.IO.Path.Combine(tempDir, file.Name);

            var plan = new[] { new PlannedFileTransfer(TransferType.Download, localPath, file.FullPath) };
            var ok = await RunTransferBatchAsync(plan, ct);
            if (ok)
                await OpenLocalFile(localPath);
        }
        catch (OperationCanceledException)
        {
            // User cancelled the download; not an error.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private const long MaxBuiltInEditSize = 5 * 1024 * 1024;

    /// <summary>编辑保存的回传统一走右上角传输浮窗(设计 9Ralg):新增一行上传任务、
    /// 流式进度、完成/失败落状态,随后浮窗按既有规则自动淡出。失败会向上抛,调用方
    /// (编辑器状态栏 / 外部编辑会话)据此提示。必须在 UI 线程调用。</summary>
    private async Task UploadEditedFileAsync(string localPath, string remotePath)
    {
        if (_sftpService is null)
            throw new InvalidOperationException("SFTP 服务不可用。");

        var task = new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = TransferType.Upload,
            LocalPath = localPath,
            RemotePath = remotePath,
            Status = TransferStatus.InProgress,
        };

        TransferSink?.AddTransfer(task);
        var item = TransferSink?.FindTransfer(task.Id);
        var progress = new Progress<TransferProgress>(p => item?.UpdateProgress(p));

        try
        {
            await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress);
            if (item is not null)
                item.Status = TransferStatus.Completed;
        }
        catch
        {
            if (item is not null)
                item.Status = TransferStatus.Failed;
            throw;
        }
        finally
        {
            TransferSink?.NotifyTaskSettled();
        }
    }

    /// <summary>「打开」:文件下载到独占临时目录后,交给视图打开内置编辑器;
    /// 编辑器保存时通过回调把临时副本上传回原远程路径。</summary>
    private async Task OpenItemAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (_sftpService is null || OpenInBuiltInEditor is null || file is null || !file.IsRegularFile)
            return;

        if (file.SizeBytes > MaxBuiltInEditSize)
        {
            ErrorMessage = "文件超过 5 MB,内置编辑器仅适合小文本;请下载后在本地编辑。";
            return;
        }

        try
        {
            ErrorMessage = null;
            var tempDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "VelaShell", "builtin-edit",
                Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(tempDir);
            var localPath = System.IO.Path.Combine(tempDir, file.Name);

            await _sftpService.DownloadFileAsync(_sessionId, file.FullPath, localPath, null, ct);

            var remotePath = file.FullPath;
            await OpenInBuiltInEditor(file, localPath,
                () => UploadEditedFileAsync(localPath, remotePath));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>「使用默认编辑器打开」:交给 ExternalEditSessionManager(下载 → 启动配置的
    /// 编辑器 → 侦听保存自动上传 → 退出清理 temp)。</summary>
    private async Task OpenWithDefaultEditorAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (_sftpService is null || file is null || !file.IsRegularFile)
            return;

        var editor = GetDefaultEditorPath is null ? null : await GetDefaultEditorPath();
        if (string.IsNullOrWhiteSpace(editor))
        {
            // 弹窗引导配置(视图实现里含"打开设置"直达);无视图委托时退回面板报错。
            if (PromptConfigureEditor is not null)
                await PromptConfigureEditor();
            else
                ErrorMessage = "未配置默认编辑器,请在 设置 → 文件传输 → 默认编辑器 中填写(如 notepad)。";
            return;
        }

        try
        {
            ErrorMessage = null;
            await Services.ExternalEditSessionManager.OpenAsync(
                _sftpService, _sessionId, file.FullPath, file.Name, editor,
                message => Avalonia.Threading.Dispatcher.UIThread.Post(() => ErrorMessage = message),
                // 保存回传经传输浮窗提示;监听回调在线程池,需切回 UI 线程。
                uploadAsync: (local, remote) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => UploadEditedFileAsync(local, remote)),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task GoUpAsync(CancellationToken ct = default)
    {
        if (CurrentPath == "/") return;

        await NavigateToAsync(ParentOf(CurrentPath), ct);
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        await NavigateToAsync(CurrentPath, ct);
    }

    /// <summary>Set by the view: opens the OS file picker (multi-select) and returns local paths.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFilesForUpload { get; set; }

    /// <summary>Set by the view: opens the OS folder picker and returns the chosen folder, or null.</summary>
    public Func<Task<string?>>? PickFolderForUpload { get; set; }

    /// <summary>Set by the view: asks where to save a download (arg = suggested file name).</summary>
    public Func<string, Task<string?>>? PickSavePathForDownload { get; set; }

    /// <summary>Set by the view: picks a local destination folder for folder/batch downloads.</summary>
    public Func<Task<string?>>? PickFolderForDownload { get; set; }

    /// <summary>Set by the view: prompts for a line of text (title, initial value) → entered text or
    /// null if cancelled. Used by new folder / new file / rename / move.</summary>
    public Func<string, string, Task<string?>>? PromptForText { get; set; }

    /// <summary>Set by the view: writes text to the OS clipboard (copy path / copy name).</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Set by the view: shows the combined properties + permissions modal (参考 WinSCP:
    /// 属性与权限矩阵在同一弹窗)。Returns the changed mode as three octal digits written in
    /// decimal (e.g. 755), or null when cancelled / unchanged.</summary>
    public Func<RemoteFileInfoViewModel, Task<short?>>? ShowFileProperties { get; set; }

    /// <summary>Set by the view: asks the user to confirm a destructive action (arg = message) →
    /// true to proceed. Used before deleting.</summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>Set by the view: opens a local file with the OS default program.</summary>
    public Func<string, Task>? OpenLocalFile { get; set; }

    /// <summary>Set by the view: opens the built-in AvaloniaEdit editor window.
    /// (file, localTempPath, uploadCallback) — the editor invokes the callback after each save.</summary>
    public Func<RemoteFileInfoViewModel, string, Func<Task>, Task>? OpenInBuiltInEditor { get; set; }

    /// <summary>Set by the host: resolves the configured default editor (设置 → 文件传输)。</summary>
    public Func<Task<string?>>? GetDefaultEditorPath { get; set; }

    /// <summary>Set by the view: 未配置默认编辑器时的弹窗引导(含"打开设置"直达)。</summary>
    public Func<Task>? PromptConfigureEditor { get; set; }

    /// <summary>The floating transfer toast fed by uploads/downloads started here (spec §9).</summary>
    public FileTransferViewModel? TransferSink { get; set; }

    /// <summary>设置 → 文件传输 的选项快照(宿主在绑定与设置保存时刷新)。</summary>
    public VelaShell.Core.Models.TransferOptions TransferOptions { get; set; } = new();

    /// <summary>Set by the view: 下载遇到本地同名文件且策略为“询问”时的覆盖确认
    /// (arg = 本地路径;true = 覆盖,false = 跳过该文件)。</summary>
    public Func<string, Task<bool>>? ConfirmOverwrite { get; set; }

    private async Task UploadAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PickFilesForUpload is null)
            return;

        var files = await PickFilesForUpload();
        await UploadLocalPathsAsync(files, ct);
    }

    private async Task UploadFolderAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PickFolderForUpload is null)
            return;

        var folder = await PickFolderForUpload();
        if (string.IsNullOrEmpty(folder))
            return;

        await UploadLocalPathsAsync(new[] { folder }, ct);
    }

    /// <summary>Uploads any mix of local files and folders into the current directory, recursing
    /// into folders (creating the matching remote directories). Shared by the upload menu items and
    /// drag-and-drop, so multi-select and dropped folders all funnel through here.</summary>
    public async Task UploadLocalPathsAsync(IReadOnlyList<string> localPaths, CancellationToken ct = default)
    {
        if (_sftpService is null || localPaths is null || localPaths.Count == 0)
            return;

        try
        {
            ErrorMessage = null;
            var plan = new List<PlannedFileTransfer>();
            foreach (var path in localPaths)
                await BuildUploadPlanAsync(path, CurrentPath, plan, ct);

            await RunTransferBatchAsync(plan, ct);
        }
        catch (OperationCanceledException)
        {
            // User cancelled while the upload was being planned/run; not an error.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        await RefreshAsync(ct);
    }

    /// <summary>Walks a local file/folder and appends one planned upload per file into
    /// <paramref name="plan"/>, creating the matching remote directories as it goes. Planning up
    /// front gives the toast an accurate remaining-file count and makes the batch cancellable.</summary>
    private async Task BuildUploadPlanAsync(string localPath, string remoteDir, List<PlannedFileTransfer> plan, CancellationToken ct)
    {
        if (System.IO.Directory.Exists(localPath))
        {
            var name = System.IO.Path.GetFileName(
                localPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            var remoteSub = CombinePath(remoteDir, name);
            await _sftpService.EnsureDirectoryAsync(_sessionId, remoteSub, ct);

            foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(localPath))
                await BuildUploadPlanAsync(child, remoteSub, plan, ct);
        }
        else if (System.IO.File.Exists(localPath))
        {
            var remotePath = CombinePath(remoteDir, System.IO.Path.GetFileName(localPath));
            plan.Add(new PlannedFileTransfer(TransferType.Upload, localPath, remotePath));
        }
    }

    private async Task DownloadItemAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (_sftpService is null || file is null || file.IsParentEntry)
            return;

        if (file.IsDirectory)
        {
            if (PickFolderForDownload is null)
                return;

            var localDir = await PickFolderForDownload();
            if (string.IsNullOrEmpty(localDir))
                return;

            try
            {
                ErrorMessage = null;
                var plan = new List<PlannedFileTransfer>();
                await BuildDownloadPlanAsync(file.FullPath, file.Name, isDirectory: true, localDir, plan, ct);
                await RunTransferBatchAsync(plan, ct);
            }
            catch (OperationCanceledException)
            {
                // User cancelled while the download was being planned/run; not an error.
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }

            return;
        }

        if (PickSavePathForDownload is null)
            return;

        var localPath = await PickSavePathForDownload(file.Name);
        if (string.IsNullOrEmpty(localPath))
            return;

        var single = new[] { new PlannedFileTransfer(TransferType.Download, localPath, file.FullPath) };
        await RunTransferBatchAsync(single, ct);
    }

    /// <summary>Walks a remote file or directory (recursively) and appends one planned download per
    /// file into <paramref name="plan"/>, mirroring the remote structure into <paramref name="localDir"/>
    /// (creating the local directories as it goes). Planning up front lets the toast show an accurate
    /// remaining-file count and lets the whole batch be cancelled. Shared by folder and batch download.</summary>
    private async Task BuildDownloadPlanAsync(string remotePath, string name, bool isDirectory, string localDir,
        List<PlannedFileTransfer> plan, CancellationToken ct)
    {
        if (isDirectory)
        {
            var localSub = System.IO.Path.Combine(localDir, name);
            System.IO.Directory.CreateDirectory(localSub);

            var children = await _sftpService.ListDirectoryAsync(_sessionId, remotePath, ct);
            foreach (var child in children)
                await BuildDownloadPlanAsync(child.FullPath, child.Name, child.IsDirectory, localSub, plan, ct);
        }
        else
        {
            var localPath = System.IO.Path.Combine(localDir, name);
            plan.Add(new PlannedFileTransfer(TransferType.Download, localPath, remotePath));
        }
    }

    private async Task DownloadSelectedAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PickFolderForDownload is null)
            return;

        var targets = SelectedFiles.Where(f => !f.IsParentEntry).ToList();
        if (targets.Count == 0)
            return;

        var localDir = await PickFolderForDownload();
        if (string.IsNullOrEmpty(localDir))
            return;

        try
        {
            ErrorMessage = null;
            var plan = new List<PlannedFileTransfer>();
            foreach (var item in targets)
                await BuildDownloadPlanAsync(item.FullPath, item.Name, item.IsDirectory, localDir, plan, ct);

            await RunTransferBatchAsync(plan, ct);
        }
        catch (OperationCanceledException)
        {
            // User cancelled the batch download; not an error.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>A single file scheduled for transfer, resolved up front so the whole batch can be
    /// counted and cancelled as one unit.</summary>
    private sealed record PlannedFileTransfer(TransferType Type, string LocalPath, string RemotePath);

    /// <summary>Runs a batch of planned transfers one after another behind a shared cancellation
    /// scope that the toast's "cancel remaining" control (and folder-download cancellation) can
    /// trip. The toast shows the remaining-file count; the return value says whether every file
    /// completed (false if the user cancelled).</summary>
    private async Task<bool> RunTransferBatchAsync(IReadOnlyList<PlannedFileTransfer> plan, CancellationToken ct)
    {
        if (plan.Count == 0)
            return true;

        // 冲突处理(设置 → 文件传输 → 文件已存在时):下载前对本地同名文件按策略
        // 覆盖/跳过/重命名/逐个询问。上传沿用 SFTP 语义(远端同名即覆盖)。
        var resolved = new List<PlannedFileTransfer>(plan.Count);
        foreach (var item in plan)
        {
            var settled = await ResolveLocalConflictAsync(item);
            if (settled is not null)
                resolved.Add(settled);
        }

        if (resolved.Count == 0)
            return true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transferCts = cts;
        TransferSink?.BeginBatch(resolved.Count, cts);
        bool completed = false;

        try
        {
            // 最大并发传输数(设置 → 文件传输):1 = 既有的顺序行为。
            int maxConcurrent = Math.Clamp(TransferOptions.MaxConcurrentTransfers, 1, 16);
            if (maxConcurrent <= 1 || resolved.Count == 1)
            {
                foreach (var item in resolved)
                {
                    await RunTransferAsync(item.Type, item.LocalPath, item.RemotePath, cts.Token);
                    TransferSink?.NotifyBatchItemSettled();
                }
            }
            else
            {
                using var gate = new SemaphoreSlim(maxConcurrent);
                var workers = resolved.Select(async item =>
                {
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        await RunTransferAsync(item.Type, item.LocalPath, item.RemotePath, cts.Token);
                        TransferSink?.NotifyBatchItemSettled();
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();
                await Task.WhenAll(workers);
            }

            completed = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            // The user cancelled: the file in flight was aborted and the rest were skipped.
            return false;
        }
        finally
        {
            TransferSink?.EndBatch();
            _transferCts = null;

            // 传输完成后显示通知(设置 → 文件传输):提示音 + 展开传输面板。
            if (completed && TransferOptions.NotifyOnComplete)
            {
                Services.SystemSound.Alert();
                TransferSink?.ShowPanel();
            }
        }
    }

    /// <summary>按冲突策略处理一个计划中的下载:返回 null 表示跳过,或返回(可能改了
    /// 本地路径的)计划项。非下载或无冲突原样返回。</summary>
    private async Task<PlannedFileTransfer?> ResolveLocalConflictAsync(PlannedFileTransfer item)
    {
        if (item.Type != TransferType.Download || !System.IO.File.Exists(item.LocalPath))
            return item;

        switch (TransferOptions.ConflictPolicy)
        {
            case "overwrite":
                return item;
            case "skip":
                return null;
            case "rename":
                return item with { LocalPath = NextAvailableLocalName(item.LocalPath) };
            default: // ask
                if (ConfirmOverwrite is null)
                    return item;
                return await ConfirmOverwrite(item.LocalPath) ? item : null;
        }
    }

    /// <summary>"file.txt" → "file (1).txt"(取第一个不存在的序号)。</summary>
    private static string NextAvailableLocalName(string localPath)
    {
        var dir = System.IO.Path.GetDirectoryName(localPath) ?? "";
        var stem = System.IO.Path.GetFileNameWithoutExtension(localPath);
        var ext = System.IO.Path.GetExtension(localPath);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!System.IO.File.Exists(candidate))
                return candidate;
        }

        return localPath;
    }

    /// <summary>Runs one transfer end to end: registers it with the toast, streams progress into
    /// it, and settles the final state. A failure marks the row red and returns; a cancellation
    /// marks the row cancelled, removes any partial local file, and propagates so the batch stops.</summary>
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

        var finalStatus = TransferStatus.Failed;
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
            finalStatus = TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            if (item is not null)
                item.Status = TransferStatus.Cancelled;
            finalStatus = TransferStatus.Cancelled;

            // A cancelled download leaves a half-written file behind; drop it.
            if (type == TransferType.Download)
                TryDeleteLocalFile(localPath);

            throw;
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

            // 记录传输日志(设置 → 文件传输 → 日志记录)。
            if (TransferOptions.TransferLogging)
                Services.TransferLogService.Append(TransferOptions.LogDirectory, type, localPath, remotePath, finalStatus);
        }
    }

    private static void TryDeleteLocalFile(string localPath)
    {
        try
        {
            if (System.IO.File.Exists(localPath))
                System.IO.File.Delete(localPath);
        }
        catch
        {
            // Best-effort cleanup of a partial download.
        }
    }

    private void CancelDelete()
    {
        // Guard so a cancellation callback can never crash the app from the cancel button.
        try
        {
            _deleteCts?.Cancel();
        }
        catch
        {
            // Best-effort: the delete loop stops at its next cancellation check regardless.
        }
    }

    private async Task NewFolderAsync(CancellationToken ct = default)
    {
        if (_sftpService is null || PromptForText is null)
            return;

        var name = await PromptForText(Strings.NewFolder, "");
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

        var name = await PromptForText(Strings.NewFile, "");
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
        if (_sftpService is null || PromptForText is null || file is null || file.IsParentEntry)
            return;

        var newName = await PromptForText(Strings.Rename, file.Name);
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
        if (_sftpService is null || PromptForText is null || file is null || file.IsParentEntry)
            return;

        var destination = await PromptForText(Strings.MoveToPrompt, file.FullPath);
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
        if (CopyToClipboard is null || file is null || file.IsParentEntry)
            return;

        await CopyToClipboard(file.FullPath);
    }

    private async Task CopyNameAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (CopyToClipboard is null || file is null || file.IsParentEntry)
            return;

        await CopyToClipboard(file.Name);
    }

    private async Task ShowPropertiesAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (ShowFileProperties is null || file is null || file.IsParentEntry)
            return;

        // 属性弹窗内含权限矩阵;确定且权限有变化时返回新 mode,由这里落到 chmod。
        var mode = await ShowFileProperties(file);
        if (mode is null || _sftpService is null)
            return;

        try
        {
            ErrorMessage = null;
            await _sftpService.SetPermissionsAsync(_sessionId, file.FullPath, mode.Value, ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task DeleteItemAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (_sftpService is null || file is null || file.IsParentEntry)
            return;

        if (ConfirmDelete is not null)
        {
            var template = file.IsDirectory ? Strings.ConfirmDeleteFolder : Strings.ConfirmDeleteFile;
            var ok = await ConfirmDelete(string.Format(template, file.Name));
            if (!ok)
                return;
        }

        await DeleteManyAsync(new[] { file }, ct);
    }

    private async Task DeleteSelectedAsync(CancellationToken ct = default)
    {
        if (_sftpService is null)
            return;

        var targets = SelectedFiles.Where(f => !f.IsParentEntry).ToList();
        if (targets.Count == 0)
            return;

        if (ConfirmDelete is not null)
        {
            var ok = await ConfirmDelete(targets.Count == 1
                ? string.Format(targets[0].IsDirectory ? Strings.ConfirmDeleteFolder : Strings.ConfirmDeleteFile, targets[0].Name)
                : string.Format(Strings.ConfirmDeleteMultiple, targets.Count));
            if (!ok)
                return;
        }

        await DeleteManyAsync(targets, ct);
    }

    /// <summary>Deletes the given entries one after another behind a single busy overlay; the
    /// per-entry recursive progress is folded into one running "deleted / total" readout.</summary>
    private async Task DeleteManyAsync(IReadOnlyList<RemoteFileInfoViewModel> targets, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _deleteCts = cts;

        try
        {
            ErrorMessage = null;
            BusyText = Strings.Deleting;
            IsDeleteProgressVisible = true;
            IsDeleteProgressIndeterminate = true;
            DeleteProgressPercent = 0;
            IsLoading = true;

            for (var i = 0; i < targets.Count; i++)
            {
                var index = i;
                var progress = new Progress<SftpDeleteProgress>(p =>
                {
                    if (p.TotalCount > 0)
                    {
                        IsDeleteProgressIndeterminate = false;
                        // Weight each entry equally so a huge folder among small files still moves the bar.
                        DeleteProgressPercent = (index * 100.0 + p.Percentage) / targets.Count;
                        BusyText = string.Format(Strings.DeletingProgress, p.DeletedCount, p.TotalCount);
                    }
                    else
                    {
                        IsDeleteProgressIndeterminate = true;
                        BusyText = Strings.Deleting;
                    }
                });

                await _sftpService.DeleteAsync(_sessionId, targets[i].FullPath, progress, cts.Token);
            }

            await RefreshAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled mid-delete; already-deleted entries stay gone, so re-list to
            // show what actually remains.
            await RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            _deleteCts = null;
            IsDeleteProgressVisible = false;
            IsDeleteProgressIndeterminate = false;
            DeleteProgressPercent = 0;
            IsLoading = false;
        }
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
