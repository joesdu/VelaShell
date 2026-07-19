using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// One clickable segment of the header path breadcrumb (§6): the segment text and the
/// absolute remote path it navigates to.
/// </summary>
public sealed record BreadcrumbSegment(string Name, string Path);

/// <summary>
/// SFTP 文件浏览面板的视图模型:承载目录列举、导航、上传/下载、增删改与属性/权限
/// 编辑等操作,并把传输进度反馈到右上角传输浮窗。每个已连接会话绑定一个实例。
/// </summary>
public class FileBrowserViewModel : ReactiveObject
{
    private const long MaxBuiltInEditSize = 5 * 1024 * 1024;

    // 各列的最小像素宽度。列宽钳制(本类)与视图侧的拖拽/双击自适应共用这一份下限,
    // 否则两边各写一套魔数,改一处就会错位(见 FileBrowserView.OnColumnSplitterPointerMoved)。

    /// <summary>“名称”列的最小像素宽度。</summary>
    public const double MinNameWidth = 180;

    /// <summary>“大小”列的最小像素宽度。</summary>
    public const double MinSizeWidth = 70;

    /// <summary>“权限”列的最小像素宽度。</summary>
    public const double MinPermissionsWidth = 80;

    /// <summary>“所有者”列的最小像素宽度。</summary>
    public const double MinOwnerWidth = 70;

    /// <summary>“用户组”列的最小像素宽度。</summary>
    public const double MinGroupWidth = 70;

    /// <summary>“类型”列的最小像素宽度。</summary>
    public const double MinTypeWidth = 80;

    /// <summary>“修改时间”列(末列,吸收剩余宽度)的最小像素宽度。</summary>
    public const double MinModifiedWidth = 110;

    /// <summary>列间拖拽条的宽度;所属列隐藏时随之塌缩为 0。</summary>
    private static readonly GridLength SplitterWidth = new(6);

    /// <summary>列隐藏时的塌缩宽度。</summary>
    private static readonly GridLength CollapsedWidth = new(0);

    /// <summary>
    /// The raw directory listing before the hidden-files filter/sort; the visible
    /// <see cref="Files" /> collection is rebuilt from this.
    /// </summary>
    private readonly List<RemoteFileInfoViewModel> _allFiles = [];
    private readonly BatchObservableCollection<RemoteFileInfoViewModel> _files;

    private readonly Guid _sessionId;
    private readonly ISftpService _sftpService;

    private string _currentPath;
    private long _navigationVersion;

    /// <summary>Cancels the running delete; tripped from the delete overlay's cancel button.</summary>
    private CancellationTokenSource? _deleteCts;

    /// <summary>在飞的初始加载(合流用,见 LoadInitialAsync)。</summary>
    private Task? _initialLoad;

    /// <summary>
    /// Cancelled when this browser instance is discarded (its tab closed, or the panel rebound to
    /// another session), so in-flight SFTP work stops racing the session teardown (#tab-close NRE).
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    private bool _isVisible;

    /// <summary>
    /// 为指定 SSH 会话创建文件浏览视图模型,初始化各命令并把当前路径置于根目录;
    /// <paramref name="sftpService" /> 为 null 时构成未绑定会话的占位面板。
    /// </summary>
    public FileBrowserViewModel(ISftpService? sftpService, Guid sessionId)
    {
        _sftpService = sftpService!;
        _sessionId = sessionId;
        SessionId = sessionId;
        _currentPath = "/";
        _isVisible = false;
        _files = [];
        Files = _files;
        SelectedFiles = [];
        NavigateToCommand = ReactiveCommand.CreateFromTask<string>(NavigateToAsync);
        ActivateCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(ActivateAsync);
        GoUpCommand = ReactiveCommand.CreateFromTask(GoUpAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        LoadInitialCommand = ReactiveCommand.CreateFromTask(LoadInitialAsync);
        UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
        UploadFolderCommand = ReactiveCommand.CreateFromTask(UploadFolderAsync);
        NewFolderCommand = ReactiveCommand.CreateFromTask(NewFolderAsync);
        NewFileCommand = ReactiveCommand.CreateFromTask(NewFileAsync);
        DownloadItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(
            DownloadItemAsync
        );
        RenameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(RenameAsync);
        MoveCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(MoveAsync);
        CopyPathCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyPathAsync);
        CopyNameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyNameAsync);
        PropertiesCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(
            ShowPropertiesAsync
        );
        DeleteItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(
            DeleteItemAsync
        );
        OpenItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(OpenItemAsync);
        OpenWithDefaultEditorCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(
            OpenWithDefaultEditorAsync
        );
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(DownloadSelectedAsync);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync);
        CancelDeleteCommand = ReactiveCommand.Create(CancelDelete);
        ShowTransfersCommand = ReactiveCommand.Create(() => TransferSink?.ShowPanel());
        ToggleVisibilityCommand = ReactiveCommand.Create(ToggleVisibility);
        ToggleHiddenFilesCommand = ReactiveCommand.Create(() =>
        {
            ShowHiddenFiles = !ShowHiddenFiles;

            // 工具栏切换写回持久化设置(设置审计 C-04):与设置中心共用同一状态来源。
            ShowHiddenFilesToggled?.Invoke(ShowHiddenFiles);
        });
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
    }

    /// <summary>The SSH session this browser is rooted at.</summary>
    public Guid SessionId { get; }

    /// <summary>
    /// 面板头部身份徽章显示的服务器名(配置显示名,未配置时用主机地址);
    /// 空串 = 未绑定会话的占位面板,徽章隐藏。
    /// </summary>
    public string ServerDisplayName { get; init; } = string.Empty;

    /// <summary>该连接的稳定标识色(与标签页色条同色,见 ConnectionAccent)。</summary>
    public Avalonia.Media.IBrush? AccentBrush { get; init; }

    /// <summary>
    /// 是否已成功加载过至少一次目录列表。宿主用它区分"切回缓存面板 → 静默刷新"
    /// 与"首次展示 → 完整初始加载"。
    /// </summary>
    public bool HasLoaded { get; private set; }

    /// <summary>
    /// 静默刷新当前目录:不弹加载遮罩、失败时保留现有列表。用于切回已缓存的面板时
    /// 后台更新数据——旧列表先显示(秒切),新列表到达后原地替换。
    /// </summary>
    public async Task RefreshSilentlyAsync()
    {
        if (_sftpService is null || _sessionId == Guid.Empty)
        {
            return;
        }
        // 静默刷新是后台对账,绝不能盖过用户的显式导航。捕获入口时的导航版本与目录;
        // 这里不递增版本(递增会取消用户正在进行的导航),仅在 await 之后据此判断是否已过期。
        long navigationVersion = Volatile.Read(ref _navigationVersion);
        string path = CurrentPath;
        try
        {
            var selectedPaths = SelectedFiles
                .Where(file => !file.IsParentEntry)
                .Select(file => file.FullPath)
                .ToHashSet(StringComparer.Ordinal);
            List<RemoteFileInfo> files = await _sftpService.ListDirectoryAsync(
                _sessionId,
                path,
                _lifetime.Token
            );

            // 关键:await 期间若发生了导航(版本变化)或当前目录已不再是本次列举的目录,
            // 说明这份结果已过期。此时若继续写入 _allFiles,会用旧目录内容覆盖较新的导航结果,
            // 造成"列表显示 A 目录、面包屑却是 B 目录"——因行路径是绝对路径,后续删除/下载会
            // 作用到错误的文件。故直接丢弃过期结果。
            if (
                navigationVersion != Volatile.Read(ref _navigationVersion)
                || !string.Equals(path, CurrentPath, StringComparison.Ordinal)
            )
            {
                return;
            }

            // 内容没变(切回标签的常见情形)就不动列表:整表 Clear+重建会让 ListBox
            // 全量重新虚拟化,恰好落在切换标签的瞬间,是可感知的顿挫来源。
            if (ListingUnchanged(files))
            {
                ErrorMessage = null;
                return;
            }
            _allFiles.Clear();
            _allFiles.AddRange(files.Select(f => new RemoteFileInfoViewModel(f)));
            RebuildVisibleFiles();
            RestoreSelection(selectedPaths);
            ErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
            // 面板已被驱逐 —— 静默退出。
        }
        catch
        {
            // 静默刷新失败(网络抖动/目录被删)不打扰用户,保留手头的旧列表;
            // 用户显式操作(刷新按钮/导航)仍会正常报错。
        }
    }

    /// <summary>新列举与当前原始列表逐项等价(名称/路径/大小/权限/时间/属主)。</summary>
    private bool ListingUnchanged(List<RemoteFileInfo> fresh)
    {
        if (fresh.Count != _allFiles.Count)
        {
            return false;
        }
        for (int i = 0; i < fresh.Count; i++)
        {
            RemoteFileInfo a = fresh[i];
            RemoteFileInfo b = _allFiles[i].Model;
            if (
                a.Name != b.Name
                || a.FullPath != b.FullPath
                || a.Size != b.Size
                || a.IsDirectory != b.IsDirectory
                || a.Permissions != b.Permissions
                || a.LastModified != b.LastModified
                || a.Owner != b.Owner
                || a.Group != b.Group
            )
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Called by the host when this instance is being replaced (tab closed / panel rebound):
    /// cancels in-flight SFTP operations so they don't race the SFTP channel teardown.
    /// </summary>
    public void Detach()
    {
        try
        {
            _lifetime.Cancel();
            _deleteCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already detached — nothing left to cancel.
        }
    }

    /// <summary>
    /// Links an operation token to this browser's lifetime. Commands never pass a token, so the
    /// common case is simply the lifetime token itself (no allocation).
    /// </summary>
    private CancellationToken WithLifetime(CancellationToken ct) =>
        ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token).Token
            : _lifetime.Token;

    /// <summary>危险操作确认文案加服务器名前缀,多标签下防止删错服务器上的文件。</summary>
    private string WithServerTag(string message) =>
        string.IsNullOrEmpty(ServerDisplayName) ? message : $"[{ServerDisplayName}] {message}";

    /// <summary>当前目录中可见的行(已过滤隐藏文件、已排序,非根目录时含首行 ".." 返回项)。</summary>
    public ObservableCollection<RemoteFileInfoViewModel> Files { get; }

    /// <summary>列表中当前被多选中的条目(批量下载/删除的作用对象)。</summary>
    public ObservableCollection<RemoteFileInfoViewModel> SelectedFiles { get; }

    /// <summary>成功进入不同目录后触发,视图据此把滚动条重置到顶部。</summary>
    public event EventHandler? DirectoryChanged;

    /// <summary>当前浏览的远程目录绝对路径;赋值时同步刷新 <see cref="Breadcrumbs" />。</summary>
    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (_currentPath == value)
            {
                return;
            }
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
            string path = "";
            foreach (string part in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path += "/" + part;
                segments.Add(new(part, path));
            }
            return segments;
        }
    }

    /// <summary>是否正在执行需要阻塞面板的操作,当前仅用于删除进度。</summary>
    public bool IsLoading
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Message shown on the busy overlay (loading a directory, or delete progress).</summary>
    public string BusyText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Loading;

    /// <summary>是否正在后台读取目录;只显示路径栏轻量状态。</summary>
    public bool IsDirectoryLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>该文件浏览面板当前是否展示(隐藏时可跳过后台刷新等工作)。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    /// <summary>需要展示给用户的错误提示;为 null 表示无错误。</summary>
    public string? ErrorMessage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 工具栏切换“显示隐藏文件”后的回调(宿主用它把新值写回 Transfer.ShowHiddenFiles);
    /// 仅由用户点击工具栏触发,宿主程序化赋值 <see cref="ShowHiddenFiles" /> 不触发。
    /// </summary>
    public Action<bool>? ShowHiddenFilesToggled { get; set; }

    /// <summary>Whether dotfiles are listed. Off by default per §6 (hidden-files toggle).</summary>
    public bool ShowHiddenFiles
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RebuildVisibleFiles();
        }
    }

    /// <summary>“名称”列的用户可调宽度(有最小像素下限约束)。</summary>
    public GridLength NameColumnWidth
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, ClampColumnWidth(value, MinNameWidth));
    } = new(280);

    /// <summary>“大小”列的用户可调宽度(有最小像素下限约束)。</summary>
    public GridLength SizeColumnWidth
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, ClampColumnWidth(value, MinSizeWidth));
            this.RaisePropertyChanged(nameof(SizeGridWidth));
        }
    } = new(100);

    /// <summary>“权限”列的用户可调宽度(有最小像素下限约束)。</summary>
    public GridLength PermissionsColumnWidth
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, ClampColumnWidth(value, MinPermissionsWidth));
            this.RaisePropertyChanged(nameof(PermissionsGridWidth));
        }
    } = new(110);

    /// <summary>“所有者”列的用户可调宽度(有最小像素下限约束)。</summary>
    public GridLength OwnerColumnWidth
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, ClampColumnWidth(value, MinOwnerWidth));
            this.RaisePropertyChanged(nameof(OwnerGridWidth));
        }
    } = new(95);

    /// <summary>“用户组”列的用户可调宽度(有最小像素下限约束)。</summary>
    public GridLength GroupColumnWidth
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, ClampColumnWidth(value, MinGroupWidth));
            this.RaisePropertyChanged(nameof(GroupGridWidth));
        }
    } = new(95);

    /// <summary>“类型”列的用户可调宽度(有最小像素下限约束)。</summary>
    public GridLength TypeColumnWidth
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, ClampColumnWidth(value, MinTypeWidth));
            this.RaisePropertyChanged(nameof(TypeGridWidth));
        }
    } = new(100);

    // —— 列显示开关(表头右键切换)——————————————————————————————
    // “文件名”列没有开关:它是行的标识,关掉就只剩一排没有主语的元数据。
    // 每个开关都要连带通知自己那组派生的表格几何(宽度/最小宽度/拖拽条),
    // 因为 Grid 靠把列宽压成 0 来“隐藏”列。

    /// <summary>是否显示“大小”列。</summary>
    public bool ShowSizeColumn
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseColumnGeometryChanged(
                nameof(SizeGridWidth),
                nameof(SizeGridMinWidth),
                nameof(SizeSplitterWidth)
            );
            ColumnVisibilityToggled?.Invoke("size", value);
        }
    } = true;

    /// <summary>是否显示“权限”列。</summary>
    public bool ShowPermissionsColumn
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseColumnGeometryChanged(
                nameof(PermissionsGridWidth),
                nameof(PermissionsGridMinWidth),
                nameof(PermissionsSplitterWidth)
            );
            ColumnVisibilityToggled?.Invoke("permissions", value);
        }
    } = true;

    /// <summary>是否显示“所有者”列。</summary>
    public bool ShowOwnerColumn
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseColumnGeometryChanged(
                nameof(OwnerGridWidth),
                nameof(OwnerGridMinWidth),
                nameof(OwnerSplitterWidth)
            );
            ColumnVisibilityToggled?.Invoke("owner", value);
        }
    } = true;

    /// <summary>是否显示“用户组”列。</summary>
    public bool ShowGroupColumn
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseColumnGeometryChanged(
                nameof(GroupGridWidth),
                nameof(GroupGridMinWidth),
                nameof(GroupSplitterWidth)
            );
            ColumnVisibilityToggled?.Invoke("group", value);
        }
    } = true;

    /// <summary>是否显示“类型”列。</summary>
    public bool ShowTypeColumn
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseColumnGeometryChanged(
                nameof(TypeGridWidth),
                nameof(TypeGridMinWidth),
                nameof(TypeSplitterWidth)
            );
            ColumnVisibilityToggled?.Invoke("type", value);
        }
    } = true;

    /// <summary>
    /// 是否显示“修改时间”列。末列吃 * 宽度,没有自己的宽度/拖拽条,
    /// 隐藏它只是把表头与单元格藏起来,那段宽度留作空白。
    /// </summary>
    public bool ShowModifiedColumn
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            ColumnVisibilityToggled?.Invoke("modified", value);
        }
    } = true;

    /// <summary>
    /// 表头右键切换列显示后写回持久化设置(与“显示隐藏文件”同构,设置审计 C-04):
    /// 参数为列键("size"/"permissions"/"owner"/"group"/"type"/"modified")与新的可见性。
    /// </summary>
    public Action<string, bool>? ColumnVisibilityToggled { get; set; }

    // —— 表格几何:列关闭时宽度与拖拽条一并塌缩为 0,最小宽度同时放开(否则塌不到 0)——

    /// <summary>“大小”列在表格中的实际宽度。</summary>
    public GridLength SizeGridWidth => ShowSizeColumn ? SizeColumnWidth : CollapsedWidth;

    /// <summary>“大小”列在表格中的最小宽度。</summary>
    public double SizeGridMinWidth => ShowSizeColumn ? MinSizeWidth : 0;

    /// <summary>“大小”列右侧拖拽条的宽度。</summary>
    public GridLength SizeSplitterWidth => ShowSizeColumn ? SplitterWidth : CollapsedWidth;

    /// <summary>“权限”列在表格中的实际宽度。</summary>
    public GridLength PermissionsGridWidth =>
        ShowPermissionsColumn ? PermissionsColumnWidth : CollapsedWidth;

    /// <summary>“权限”列在表格中的最小宽度。</summary>
    public double PermissionsGridMinWidth => ShowPermissionsColumn ? MinPermissionsWidth : 0;

    /// <summary>“权限”列右侧拖拽条的宽度。</summary>
    public GridLength PermissionsSplitterWidth =>
        ShowPermissionsColumn ? SplitterWidth : CollapsedWidth;

    /// <summary>“所有者”列在表格中的实际宽度。</summary>
    public GridLength OwnerGridWidth => ShowOwnerColumn ? OwnerColumnWidth : CollapsedWidth;

    /// <summary>“所有者”列在表格中的最小宽度。</summary>
    public double OwnerGridMinWidth => ShowOwnerColumn ? MinOwnerWidth : 0;

    /// <summary>“所有者”列右侧拖拽条的宽度。</summary>
    public GridLength OwnerSplitterWidth => ShowOwnerColumn ? SplitterWidth : CollapsedWidth;

    /// <summary>“用户组”列在表格中的实际宽度。</summary>
    public GridLength GroupGridWidth => ShowGroupColumn ? GroupColumnWidth : CollapsedWidth;

    /// <summary>“用户组”列在表格中的最小宽度。</summary>
    public double GroupGridMinWidth => ShowGroupColumn ? MinGroupWidth : 0;

    /// <summary>“用户组”列右侧拖拽条的宽度。</summary>
    public GridLength GroupSplitterWidth => ShowGroupColumn ? SplitterWidth : CollapsedWidth;

    /// <summary>“类型”列在表格中的实际宽度。</summary>
    public GridLength TypeGridWidth => ShowTypeColumn ? TypeColumnWidth : CollapsedWidth;

    /// <summary>“类型”列在表格中的最小宽度。</summary>
    public double TypeGridMinWidth => ShowTypeColumn ? MinTypeWidth : 0;

    /// <summary>“类型”列右侧拖拽条的宽度。</summary>
    public GridLength TypeSplitterWidth => ShowTypeColumn ? SplitterWidth : CollapsedWidth;

    /// <summary>Whether the loading overlay should show a delete progress bar.</summary>
    public bool IsDeleteProgressVisible
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Delete progress percentage [0,100] for the overlay progress bar.</summary>
    public double DeleteProgressPercent
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>When true, delete progress is shown as indeterminate (e.g., before total is known).</summary>
    public bool IsDeleteProgressIndeterminate
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>导航到指定绝对路径的目录(面包屑点击等)。</summary>
    public ReactiveCommand<string, Unit> NavigateToCommand { get; }

    /// <summary>
    /// Row activation (double-click / Enter): descend into directories, or download a
    /// file to a temp folder and open it with the OS default program (§6).
    /// </summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> ActivateCommand { get; }

    /// <summary>返回上一级目录(已在根目录时无操作)。</summary>
    public ReactiveCommand<Unit, Unit> GoUpCommand { get; }

    /// <summary>重新列举当前目录。</summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Loads the account's home directory (spec: land in ~, not filesystem root).</summary>
    public ReactiveCommand<Unit, Unit> LoadInitialCommand { get; }

    /// <summary>Uploads OS-picked files into the current directory (toolbar + right-click).</summary>
    public ReactiveCommand<Unit, Unit> UploadCommand { get; }

    /// <summary>Uploads an OS-picked folder (recursively) into the current directory.</summary>
    public ReactiveCommand<Unit, Unit> UploadFolderCommand { get; }

    // Right-click context-menu actions (spec: file operations live in the SFTP context menu).
    /// <summary>在当前目录下新建文件夹(提示输入名称)。</summary>
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }

    /// <summary>在当前目录下新建空文件(提示输入名称)。</summary>
    public ReactiveCommand<Unit, Unit> NewFileCommand { get; }

    /// <summary>下载选中的单个文件或目录到本地(目录递归)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> DownloadItemCommand { get; }

    /// <summary>在同目录内重命名选中的条目(提示输入新名称)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> RenameCommand { get; }

    /// <summary>把选中条目移动到输入的目标路径。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> MoveCommand { get; }

    /// <summary>把选中条目的完整远程路径复制到剪贴板。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> CopyPathCommand { get; }

    /// <summary>把选中条目的名称复制到剪贴板。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> CopyNameCommand { get; }

    /// <summary>属性弹窗(合并了 chmod 权限编辑,确定时应用变更)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, Unit> PropertiesCommand { get; }

    /// <summary>删除选中的单个文件或目录(先弹确认)。</summary>
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

    /// <summary>
    /// Reopens the transfer toast so past/active transfers can be reviewed (toolbar button
    /// next to Upload). Without it the toast auto-hides and there's no way back to the history.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ShowTransfersCommand { get; }

    /// <summary>切换文件浏览面板的显示/隐藏。</summary>
    public ReactiveCommand<Unit, Unit> ToggleVisibilityCommand { get; }

    /// <summary>Toggles dotfile visibility (§6 header switch).</summary>
    public ReactiveCommand<Unit, Unit> ToggleHiddenFilesCommand { get; }

    /// <summary>
    /// Sorts by a column key ("name" | "size" | "permissions" | "modified"); clicking the
    /// active column again flips the direction.
    /// </summary>
    public ReactiveCommand<string, Unit> SortCommand { get; }

    /// <summary>The column the list is currently ordered by.</summary>
    public string SortColumn
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "name";

    /// <summary>Whether the current sort is descending.</summary>
    public bool SortDescending
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>“名称”列表头的排序方向箭头(仅当前排序列显示,否则为空)。</summary>
    public string NameSortGlyph => GlyphFor("name");

    /// <summary>“大小”列表头的排序方向箭头(仅当前排序列显示,否则为空)。</summary>
    public string SizeSortGlyph => GlyphFor("size");

    /// <summary>“权限”列表头的排序方向箭头(仅当前排序列显示,否则为空)。</summary>
    public string PermissionsSortGlyph => GlyphFor("permissions");

    /// <summary>“所有者”列表头的排序方向箭头(仅当前排序列显示,否则为空)。</summary>
    public string OwnerSortGlyph => GlyphFor("owner");

    /// <summary>“用户组”列表头的排序方向箭头(仅当前排序列显示,否则为空)。</summary>
    public string GroupSortGlyph => GlyphFor("group");

    /// <summary>“类型”列表头的排序方向箭头(仅当前排序列显示,否则为空)。</summary>
    public string TypeSortGlyph => GlyphFor("type");

    /// <summary>“修改时间”列表头的排序方向箭头(仅当前排序列显示,否则为空)。</summary>
    public string ModifiedSortGlyph => GlyphFor("modified");

    /// <summary>当前路径按 "/" 拆分后的各级目录名(用于面包屑等)。</summary>
    public string[] PathSegments => CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Set by the view: opens the OS file picker (multi-select) and returns local paths.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFilesForUpload { get; set; }

    /// <summary>Set by the view: opens the OS folder picker and returns the chosen folder, or null.</summary>
    public Func<Task<string?>>? PickFolderForUpload { get; set; }

    /// <summary>Set by the view: asks where to save a download (arg = suggested file name).</summary>
    public Func<string, Task<string?>>? PickSavePathForDownload { get; set; }

    /// <summary>Set by the view: picks a local destination folder for folder/batch downloads.</summary>
    public Func<Task<string?>>? PickFolderForDownload { get; set; }

    /// <summary>
    /// Set by the view: prompts for a line of text (title, initial value) → entered text or
    /// null if cancelled. Used by new folder / new file / rename / move.
    /// </summary>
    public Func<string, string, Task<string?>>? PromptForText { get; set; }

    /// <summary>Set by the view: writes text to the OS clipboard (copy path / copy name).</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>
    /// Set by the view: shows the combined properties + permissions modal (参考 WinSCP:
    /// 属性与权限矩阵在同一弹窗)。Returns the changed mode as three octal digits written in
    /// decimal (e.g. 755), or null when cancelled / unchanged.
    /// </summary>
    public Func<RemoteFileInfoViewModel, Task<short?>>? ShowFileProperties { get; set; }

    /// <summary>
    /// Set by the view: asks the user to confirm a destructive action (arg = message) →
    /// true to proceed. Used before deleting.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>Set by the view: opens a local file with the OS default program.</summary>
    public Func<string, Task>? OpenLocalFile { get; set; }

    /// <summary>
    /// Set by the view: opens the built-in AvaloniaEdit editor window.
    /// (file, localTempPath, uploadCallback) — the editor invokes the callback after each save.
    /// </summary>
    public Func<
        RemoteFileInfoViewModel,
        string,
        Func<Task>,
        Task
    >? OpenInBuiltInEditor
    { get; set; }

    /// <summary>Set by the host: resolves the configured default editor (设置 → 文件传输)。</summary>
    public Func<Task<string?>>? GetDefaultEditorPath { get; set; }

    /// <summary>Set by the view: 未配置默认编辑器时的弹窗引导(含"打开设置"直达)。</summary>
    public Func<Task>? PromptConfigureEditor { get; set; }

    /// <summary>The floating transfer toast fed by uploads/downloads started here (spec §9).</summary>
    public FileTransferViewModel? TransferSink { get; set; }

    /// <summary>设置 → 文件传输 的选项快照(宿主在绑定与设置保存时刷新)。</summary>
    public TransferOptions TransferOptions { get; set; } = new();

    /// <summary>
    /// Set by the view: 下载遇到本地同名文件且策略为“询问”时的覆盖确认
    /// (arg = 本地路径;true = 覆盖,false = 跳过该文件)。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Set by the view: 上传遇到远端同名文件且策略为“询问”时的覆盖确认
    /// (arg = 远端路径;true = 覆盖,false = 跳过该文件)。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmRemoteOverwrite { get; set; }

    /// <summary>
    /// 按列键取该列当前的用户可调宽度(视图侧的拖拽与双击自适应用)。
    /// 末列“修改时间”吃 * 宽度、不可调,故不在此列。
    /// </summary>
    public GridLength GetColumnWidth(string columnKey) =>
        columnKey switch
        {
            "size" => SizeColumnWidth,
            "permissions" => PermissionsColumnWidth,
            "owner" => OwnerColumnWidth,
            "group" => GroupColumnWidth,
            "type" => TypeColumnWidth,
            _ => NameColumnWidth,
        };

    /// <summary>按列键设置列宽(视图侧的拖拽与双击自适应用);越界由各列的下限钳制。</summary>
    public void SetColumnWidth(string columnKey, double pixels)
    {
        switch (columnKey)
        {
            case "size":
                SizeColumnWidth = new(pixels);
                break;
            case "permissions":
                PermissionsColumnWidth = new(pixels);
                break;
            case "owner":
                OwnerColumnWidth = new(pixels);
                break;
            case "group":
                GroupColumnWidth = new(pixels);
                break;
            case "type":
                TypeColumnWidth = new(pixels);
                break;
            default:
                NameColumnWidth = new(pixels);
                break;
        }
    }

    /// <summary>按列键取该列是否显示(“文件名”列固定常显)。</summary>
    public bool IsColumnVisible(string columnKey) =>
        columnKey switch
        {
            "size" => ShowSizeColumn,
            "permissions" => ShowPermissionsColumn,
            "owner" => ShowOwnerColumn,
            "group" => ShowGroupColumn,
            "type" => ShowTypeColumn,
            "modified" => ShowModifiedColumn,
            _ => true,
        };

    /// <summary>按列键取该列的最小像素宽度。</summary>
    public static double MinWidthFor(string columnKey) =>
        columnKey switch
        {
            "size" => MinSizeWidth,
            "permissions" => MinPermissionsWidth,
            "owner" => MinOwnerWidth,
            "group" => MinGroupWidth,
            "type" => MinTypeWidth,
            "modified" => MinModifiedWidth,
            _ => MinNameWidth,
        };

    private static GridLength ClampColumnWidth(GridLength value, double min)
    {
        // We only support pixel-sized user-resizable columns here.
        double px = value.IsAbsolute ? value.Value : min;
        return new(Math.Max(min, px));
    }

    private void RaiseColumnGeometryChanged(
        string widthName,
        string minWidthName,
        string splitterName
    )
    {
        this.RaisePropertyChanged(widthName);
        this.RaisePropertyChanged(minWidthName);
        this.RaisePropertyChanged(splitterName);
    }

    private string GlyphFor(string column) =>
        SortColumn == column
            ? SortDescending
                ? " ▼"
                : " ▲"
            : string.Empty;

    private async Task NavigateToAsync(string path, CancellationToken ct = default)
    {
        ct = WithLifetime(ct);
        long navigationVersion = Interlocked.Increment(ref _navigationVersion);
        try
        {
            ErrorMessage = null;
            IsDirectoryLoading = true;
            var selectedPaths = SelectedFiles
                .Where(file => !file.IsParentEntry)
                .Select(file => file.FullPath)
                .ToHashSet(StringComparer.Ordinal);
            List<RemoteFileInfo> files = await _sftpService.ListDirectoryAsync(
                _sessionId,
                path,
                ct
            );
            if (navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return;
            }
            bool pathChanged = !string.Equals(CurrentPath, path, StringComparison.Ordinal);
            bool visibleRowsInitialized =
                path == "/" || _files.FirstOrDefault()?.IsParentEntry == true;
            if (!pathChanged && visibleRowsInitialized && ListingUnchanged(files))
            {
                HasLoaded = true;
                return;
            }
            _allFiles.Clear();
            _allFiles.AddRange(files.Select(f => new RemoteFileInfoViewModel(f)));
            CurrentPath = path;
            if (pathChanged)
            {
                SelectedFiles.Clear();
            }
            RebuildVisibleFiles();
            if (!pathChanged)
            {
                RestoreSelection(selectedPaths);
            }
            HasLoaded = true;
            if (pathChanged)
            {
                DirectoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Detached (tab closed / panel rebound) mid-listing — nobody is looking at this
            // instance any more, so surface no error.
        }
        catch (Exception ex)
        {
            if (navigationVersion == Volatile.Read(ref _navigationVersion))
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (navigationVersion == Volatile.Read(ref _navigationVersion))
            {
                IsDirectoryLoading = false;
            }
        }
    }

    /// <summary>
    /// 始终打开登录账户的家目录(登录后的工作目录,如 pi → /home/pi、root → /root)。
    /// 家目录在服务器上不存在或不可访问(如 realpath(".") 返回的目录未创建/被 chroot)时,
    /// 自动回退到根目录 "/",避免停在报错的空白页。
    /// </summary>
    public Task LoadInitialAsync(CancellationToken ct = default)
    {
        // 合流:连接完成路径与激活标签订阅可能各触发一次初始加载,不加闸时两条
        // LoadInitial 并发各跑一遍 GetWorkingDirectory + 列目录(命令与调用方都在
        // UI 线程,无并发写竞争,引用比较即可)。
        if (_initialLoad is { IsCompleted: false })
        {
            return _initialLoad;
        }
        _initialLoad = LoadInitialCoreAsync(ct);
        return _initialLoad;
    }

    private async Task LoadInitialCoreAsync(CancellationToken ct)
    {
        ct = WithLifetime(ct);
        var candidates = new List<string>();
        try
        {
            string working = await _sftpService.GetWorkingDirectoryAsync(_sessionId, ct);
            if (!string.IsNullOrWhiteSpace(working))
            {
                candidates.Add(working);
            }
        }
        catch
        {
            // 解析家目录尽力而为,失败则继续走根目录。
        }
        candidates.Add("/");
        foreach (string path in candidates.Distinct())
        {
            await NavigateToAsync(path, ct);
            // NavigateToAsync 会吞掉异常并写入 ErrorMessage;为空即表示该目录成功打开。
            if (string.IsNullOrEmpty(ErrorMessage))
            {
                return;
            }
        }
    }

    /// <summary>Sets or flips the sort, then reorders the currently loaded rows in place.</summary>
    private void ToggleSort(string column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return;
        }
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
        this.RaisePropertyChanged(nameof(OwnerSortGlyph));
        this.RaisePropertyChanged(nameof(GroupSortGlyph));
        this.RaisePropertyChanged(nameof(TypeSortGlyph));
        this.RaisePropertyChanged(nameof(ModifiedSortGlyph));
        RebuildVisibleFiles();
    }

    /// <summary>
    /// Rebuilds the visible rows from the raw listing: hidden-files filter, the active
    /// sort, and (outside the root) a leading ".." row that navigates to the parent (§6).
    /// </summary>
    private void RebuildVisibleFiles()
    {
        IEnumerable<RemoteFileInfoViewModel> visible = _allFiles.Where(f =>
            ShowHiddenFiles || !f.Name.StartsWith('.')
        );
        var rebuilt = new List<RemoteFileInfoViewModel>();
        if (CurrentPath != "/")
        {
            rebuilt.Add(RemoteFileInfoViewModel.CreateParentEntry(ParentOf(CurrentPath)));
        }
        foreach (RemoteFileInfoViewModel file in SortFiles(visible))
        {
            rebuilt.Add(file);
        }
        _files.ReplaceAll(rebuilt);
    }

    private void RestoreSelection(HashSet<string> selectedPaths)
    {
        SelectedFiles.Clear();
        foreach (
            RemoteFileInfoViewModel file in Files.Where(file =>
                !file.IsParentEntry && selectedPaths.Contains(file.FullPath)
            )
        )
        {
            SelectedFiles.Add(file);
        }
    }

    /// <summary>
    /// Orders rows by the active column and direction, keeping directories grouped first
    /// (a directory's size is meaningless, so mixing them into a size sort reads badly).
    /// </summary>
    private IEnumerable<RemoteFileInfoViewModel> SortFiles(
        IEnumerable<RemoteFileInfoViewModel> items
    )
    {
        IOrderedEnumerable<RemoteFileInfoViewModel> dirsFirst = items.OrderByDescending(f =>
            f.IsDirectory
        );
        return SortColumn switch
        {
            "size" => SortDescending
                ? dirsFirst.ThenByDescending(f => f.SizeBytes)
                : dirsFirst.ThenBy(f => f.SizeBytes),
            "permissions" => SortDescending
                ? dirsFirst.ThenByDescending(f => f.Permissions, StringComparer.Ordinal)
                : dirsFirst.ThenBy(f => f.Permissions, StringComparer.Ordinal),

            // 属主/属组查得到名字时排的是名字,查不到时排的是数字 id 的字符串形式
            // (即 "1000" 排在 "999" 前)—— 混排两种形式的价值不足以为此引入数值特判。
            "owner" => SortDescending
                ? dirsFirst.ThenByDescending(f => f.Owner, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenBy(f => f.Owner, StringComparer.OrdinalIgnoreCase),
            "group" => SortDescending
                ? dirsFirst.ThenByDescending(f => f.Group, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenBy(f => f.Group, StringComparer.OrdinalIgnoreCase),
            "type" => SortDescending
                ? dirsFirst.ThenByDescending(
                    f => f.FileTypeDisplay,
                    StringComparer.CurrentCultureIgnoreCase
                )
                : dirsFirst.ThenBy(f => f.FileTypeDisplay, StringComparer.CurrentCultureIgnoreCase),
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
        {
            return;
        }
        if (file.IsDirectory)
        {
            await NavigateToAsync(file.FullPath, ct);
            return;
        }
        await DownloadAndOpenAsync(file, ct);
    }

    /// <summary>
    /// §6: double-clicking a file downloads it to a per-session temp folder (progress in
    /// the transfer toast) and opens it with the OS default program.
    /// </summary>
    private async Task DownloadAndOpenAsync(RemoteFileInfoViewModel file, CancellationToken ct)
    {
        if (OpenLocalFile is null)
        {
            return;
        }
        try
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(),
                "VelaShell",
                _sessionId.ToString("N")
            );
            if (!LocalPathSafety.TryResolveDestination(tempDir, file.Name, out string localPath))
            {
                ErrorMessage = Strings.Get("KeySvc_InvalidName");
                return;
            }
            Directory.CreateDirectory(tempDir);
            PlannedFileTransfer[] plan = [new(TransferType.Download, localPath, file.FullPath)];
            bool ok = await RunTransferBatchAsync(plan, ct);
            if (ok)
            {
                await OpenLocalFile(localPath);
            }
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

    /// <summary>
    /// 编辑保存的回传统一走右上角传输浮窗(设计 9Ralg):新增一行上传任务、
    /// 流式进度、完成/失败落状态,随后浮窗按既有规则自动淡出。失败会向上抛,调用方
    /// (编辑器状态栏 / 外部编辑会话)据此提示。必须在 UI 线程调用。
    /// </summary>
    private async Task UploadEditedFileAsync(string localPath, string remotePath)
    {
        if (_sftpService is null)
        {
            throw new InvalidOperationException(Strings.Get("Msg_SftpUnavailable"));
        }
        var task = new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = TransferType.Upload,
            LocalPath = localPath,
            RemotePath = remotePath,
            Status = TransferStatus.InProgress,
        };
        TransferSink?.AddTransfer(task);
        TransferItemViewModel? item = TransferSink?.FindTransfer(task.Id);
        var progress = new Progress<TransferProgress>(p => item?.UpdateProgress(p));
        try
        {
            await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress);
            item?.Status = TransferStatus.Completed;
        }
        catch
        {
            item?.Status = TransferStatus.Failed;
            throw;
        }
        finally
        {
            TransferSink?.NotifyTaskSettled();
        }
    }

    /// <summary>
    /// 「打开」:文件下载到独占临时目录后,交给视图打开内置编辑器;
    /// 编辑器保存时通过回调把临时副本上传回原远程路径。
    /// </summary>
    private async Task OpenItemAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (OpenInBuiltInEditor is null || file is null || !file.IsRegularFile)
        {
            return;
        }
        if (file.SizeBytes > MaxBuiltInEditSize)
        {
            ErrorMessage = Strings.Get("Msg_FileTooLargeForBuiltInEditor");
            return;
        }
        try
        {
            ErrorMessage = null;
            string tempDir = Path.Combine(
                Path.GetTempPath(),
                "VelaShell",
                "builtin-edit",
                Guid.NewGuid().ToString("N")[..8]
            );
            if (!LocalPathSafety.TryResolveDestination(tempDir, file.Name, out string localPath))
            {
                ErrorMessage = Strings.Get("KeySvc_InvalidName");
                return;
            }
            Directory.CreateDirectory(tempDir);
            await _sftpService.DownloadFileAsync(_sessionId, file.FullPath, localPath, null, ct);
            string remotePath = file.FullPath;
            await OpenInBuiltInEditor(
                file,
                localPath,
                () => UploadEditedFileAsync(localPath, remotePath)
            );
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 「使用默认编辑器打开」:交给 ExternalEditSessionManager(下载 → 启动配置的
    /// 编辑器 → 侦听保存自动上传 → 退出清理 temp)。
    /// </summary>
    private async Task OpenWithDefaultEditorAsync(
        RemoteFileInfoViewModel? file,
        CancellationToken ct = default
    )
    {
        if (file is null || !file.IsRegularFile)
        {
            return;
        }
        if (!LocalPathSafety.IsSafeLeafName(file.Name))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        string? editor = GetDefaultEditorPath is null ? null : await GetDefaultEditorPath();
        if (string.IsNullOrWhiteSpace(editor))
        {
            // 弹窗引导配置(视图实现里含"打开设置"直达);无视图委托时退回面板报错。
            if (PromptConfigureEditor is not null)
            {
                await PromptConfigureEditor();
            }
            else
            {
                ErrorMessage = Strings.Get("Msg_DefaultEditorNotConfigured");
            }
            return;
        }
        try
        {
            ErrorMessage = null;
            await ExternalEditSessionManager.OpenAsync(
                _sftpService,
                _sessionId,
                file.FullPath,
                file.Name,
                editor,
                message => Dispatcher.UIThread.Post(() => ErrorMessage = message),
                // 保存回传经传输浮窗提示;监听回调在线程池,需切回 UI 线程。
                (local, remote) =>
                    Dispatcher.UIThread.InvokeAsync(() => UploadEditedFileAsync(local, remote)),
                ct
            );
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task GoUpAsync(CancellationToken ct = default)
    {
        if (CurrentPath == "/")
        {
            return;
        }
        await NavigateToAsync(ParentOf(CurrentPath), ct);
    }

    private async Task RefreshAsync(CancellationToken ct = default) =>
        await NavigateToAsync(CurrentPath, ct);

    private async Task UploadAsync(CancellationToken ct = default)
    {
        if (PickFilesForUpload is null)
        {
            return;
        }
        IReadOnlyList<string> files = await PickFilesForUpload();
        await UploadLocalPathsAsync(files, ct);
    }

    private async Task UploadFolderAsync(CancellationToken ct = default)
    {
        if (PickFolderForUpload is null)
        {
            return;
        }
        string? folder = await PickFolderForUpload();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }
        await UploadLocalPathsAsync([folder], ct);
    }

    /// <summary>
    /// Uploads any mix of local files and folders into the current directory, recursing
    /// into folders (creating the matching remote directories). Shared by the upload menu items and
    /// drag-and-drop, so multi-select and dropped folders all funnel through here.
    /// </summary>
    public async Task UploadLocalPathsAsync(
        IReadOnlyList<string> localPaths,
        CancellationToken ct = default
    )
    {
        if (localPaths.Count == 0)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            // 扫描大文件夹可能耗时:先让传输面板进入"准备中",徽标随发现的文件数递增。
            TransferSink?.BeginPreparing();
            var plan = new List<PlannedFileTransfer>();
            foreach (string path in localPaths)
            {
                await BuildUploadPlanAsync(path, CurrentPath, plan, ct);
            }
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
        finally
        {
            // BeginBatch 正常接管后这是空操作;计划为空/出错/取消时确保退出准备态。
            TransferSink?.EndPreparing();
        }
        await RefreshAsync(ct);
    }

    /// <summary>
    /// Walks a local file/folder and appends one planned upload per file into
    /// <paramref name="plan" />, creating the matching remote directories as it goes. Planning up
    /// front gives the toast an accurate remaining-file count and makes the batch cancellable.
    /// </summary>
    private async Task BuildUploadPlanAsync(
        string localPath,
        string remoteDir,
        List<PlannedFileTransfer> plan,
        CancellationToken ct
    )
    {
        if (Directory.Exists(localPath))
        {
            string name = Path.GetFileName(
                localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            );
            string remoteSub = CombinePath(remoteDir, name);
            await _sftpService.EnsureDirectoryAsync(_sessionId, remoteSub, ct);
            foreach (string child in Directory.EnumerateFileSystemEntries(localPath))
            {
                await BuildUploadPlanAsync(child, remoteSub, plan, ct);
            }
        }
        else if (File.Exists(localPath))
        {
            string remotePath = CombinePath(remoteDir, Path.GetFileName(localPath));
            plan.Add(new(TransferType.Upload, localPath, remotePath));
            TransferSink?.UpdatePreparingCount(plan.Count);
        }
    }

    private async Task DownloadItemAsync(
        RemoteFileInfoViewModel? file,
        CancellationToken ct = default
    )
    {
        if (file is null || file.IsParentEntry)
        {
            return;
        }
        if (file.IsDirectory)
        {
            if (PickFolderForDownload is null)
            {
                return;
            }
            string? localDir = await PickFolderForDownload();
            if (string.IsNullOrEmpty(localDir))
            {
                return;
            }
            try
            {
                ErrorMessage = null;
                // 远端目录树的枚举同样可能耗时:面板先进入"准备中"给出扫描反馈。
                TransferSink?.BeginPreparing();
                var plan = new List<PlannedFileTransfer>();
                await BuildDownloadPlanAsync(file.FullPath, file.Name, true, localDir, plan, ct);
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
            finally
            {
                TransferSink?.EndPreparing();
            }
            return;
        }
        if (PickSavePathForDownload is null)
        {
            return;
        }
        string? localPath = await PickSavePathForDownload(file.Name);
        if (string.IsNullOrEmpty(localPath))
        {
            return;
        }
        PlannedFileTransfer[] single = [new(TransferType.Download, localPath, file.FullPath)];
        await RunTransferBatchAsync(single, ct);
    }

    /// <summary>
    /// Walks a remote file or directory (recursively) and appends one planned download per
    /// file into <paramref name="plan" />, mirroring the remote structure into <paramref name="localDir" />
    /// (creating the local directories as it goes). Planning up front lets the toast show an accurate
    /// remaining-file count and lets the whole batch be cancelled. Shared by folder and batch download.
    /// </summary>
    private async Task BuildDownloadPlanAsync(
        string remotePath,
        string name,
        bool isDirectory,
        string localDir,
        List<PlannedFileTransfer> plan,
        CancellationToken ct
    )
    {
        if (isDirectory)
        {
            if (!LocalPathSafety.TryResolveDestination(localDir, name, out string localSub))
            {
                throw new InvalidOperationException(Strings.Get("KeySvc_InvalidName"));
            }
            Directory.CreateDirectory(localSub);
            List<RemoteFileInfo> children = await _sftpService.ListDirectoryAsync(
                _sessionId,
                remotePath,
                ct
            );
            foreach (RemoteFileInfo child in children)
            {
                await BuildDownloadPlanAsync(
                    child.FullPath,
                    child.Name,
                    child.IsDirectory,
                    localSub,
                    plan,
                    ct
                );
            }
        }
        else
        {
            if (!LocalPathSafety.TryResolveDestination(localDir, name, out string localPath))
            {
                throw new InvalidOperationException(Strings.Get("KeySvc_InvalidName"));
            }
            plan.Add(new(TransferType.Download, localPath, remotePath));
            TransferSink?.UpdatePreparingCount(plan.Count);
        }
    }

    /// <summary>
    /// Downloads explicitly selected remote entries into a caller-owned local directory.
    /// The existing planning, conflict, progress and recursive-directory logic is reused.
    /// </summary>
    public async Task DownloadRemoteEntriesAsync(
        IReadOnlyList<RemoteFileInfoViewModel> entries,
        string localDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        if (entries.Count == 0)
        {
            return;
        }
        var targets = entries.Where(f => !f.IsParentEntry).ToList();
        if (targets.Count == 0)
        {
            return;
        }
        string localDir = Path.GetFullPath(localDirectory);
        try
        {
            ErrorMessage = null;
            TransferSink?.BeginPreparing();
            var plan = new List<PlannedFileTransfer>();
            foreach (RemoteFileInfoViewModel item in targets)
            {
                await BuildDownloadPlanAsync(
                    item.FullPath,
                    item.Name,
                    item.IsDirectory,
                    localDir,
                    plan,
                    ct
                );
            }
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
        finally
        {
            TransferSink?.EndPreparing();
        }
    }

    private async Task DownloadSelectedAsync(CancellationToken ct = default)
    {
        if (PickFolderForDownload is null)
        {
            return;
        }
        string? localDirectory = await PickFolderForDownload();
        if (!string.IsNullOrEmpty(localDirectory))
        {
            await DownloadRemoteEntriesAsync(SelectedFiles, localDirectory, ct);
        }
    }

    /// <summary>
    /// Runs a batch of planned transfers one after another behind a shared cancellation
    /// scope that the toast's "cancel remaining" control (and folder-download cancellation) can
    /// trip. The toast shows the remaining-file count; the return value says whether every file
    /// completed (false if the user cancelled).
    /// </summary>
    private async Task<bool> RunTransferBatchAsync(
        IReadOnlyList<PlannedFileTransfer> plan,
        CancellationToken ct
    )
    {
        if (plan.Count == 0)
        {
            return true;
        }

        // 冲突处理(设置 → 文件传输 → 文件已存在时):下载对本地同名文件、上传对远端
        // 同名文件,均按策略覆盖/跳过/重命名/逐个询问。
        // 上传的存在性检查按目录一次列举、内存比对:逐文件 ExistsAsync 在 SSH.NET 里以
        // "stat 不存在则抛异常"实现,批量上传时每个文件多一次网络往返、还刷一条
        // SftpPathNotFoundException 调试输出刷屏。
        Dictionary<string, HashSet<string>> remoteNames = await ListRemoteNamesForUploadsAsync(
            plan,
            ct
        );
        var resolved = new List<PlannedFileTransfer>(plan.Count);
        foreach (PlannedFileTransfer item in plan)
        {
            PlannedFileTransfer? settled =
                item.Type == TransferType.Download
                    ? await ResolveLocalConflictAsync(item)
                    : await ResolveRemoteConflictAsync(item, remoteNames, ct);
            if (settled is not null)
            {
                resolved.Add(settled);
            }
        }
        if (resolved.Count == 0)
        {
            return true;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TransferSink?.BeginBatch(resolved.Count, cts);
        bool completed = false;
        try
        {
            // 最大并发传输数(设置 → 文件传输):1 = 既有的顺序行为。
            int maxConcurrent = Math.Clamp(TransferOptions.MaxConcurrentTransfers, 1, 16);
            if (maxConcurrent <= 1 || resolved.Count == 1)
            {
                foreach (PlannedFileTransfer item in resolved)
                {
                    await RunTransferAsync(item.Type, item.LocalPath, item.RemotePath, cts.Token);
                    TransferSink?.NotifyBatchItemSettled();
                }
            }
            else
            {
                using var gate = new SemaphoreSlim(maxConcurrent);
                var workers = resolved
                    .Select(async item =>
                    {
                        await gate.WaitAsync(cts.Token);
                        try
                        {
                            await RunTransferAsync(
                                item.Type,
                                item.LocalPath,
                                item.RemotePath,
                                cts.Token
                            );
                            TransferSink?.NotifyBatchItemSettled();
                        }
                        finally
                        {
                            gate.Release();
                        }
                    })
                    .ToList();
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

            // 传输完成后显示通知(设置 → 文件传输):提示音 + 临时展开传输面板。
            // 用 ShowPanelTransient 而非 ShowPanel:后者会钉住面板、杀掉自动隐藏倒计时,
            // 导致完成后面板常驻只能手动关闭。
            if (completed && TransferOptions.NotifyOnComplete)
            {
                SystemSound.Alert();
                TransferSink?.ShowPanelTransient();
            }
        }
    }

    /// <summary>
    /// 按冲突策略处理一个计划中的下载:返回 null 表示跳过,或返回(可能改了
    /// 本地路径的)计划项。非下载或无冲突原样返回。
    /// </summary>
    private async Task<PlannedFileTransfer?> ResolveLocalConflictAsync(PlannedFileTransfer item)
    {
        if (item.Type != TransferType.Download || !File.Exists(item.LocalPath))
        {
            return item;
        }
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
                {
                    return item;
                }
                return await ConfirmOverwrite(item.LocalPath) ? item : null;
        }
    }

    /// <summary>
    /// 上传冲突检测的目录名单:对计划中所有上传目标的父目录各列举一次,
    /// 返回 目录 → 现存条目名 的映射。策略为“覆盖”或没有上传项时返回空表;某个目录
    /// 列举失败(权限/瞬时错误)则不入表,该目录退回逐文件 ExistsAsync 兜底。
    /// </summary>
    private async Task<Dictionary<string, HashSet<string>>> ListRemoteNamesForUploadsAsync(
        IReadOnlyList<PlannedFileTransfer> plan,
        CancellationToken ct
    )
    {
        var map = new Dictionary<string, HashSet<string>>();
        if (TransferOptions.ConflictPolicy == "overwrite")
        {
            return map;
        }
        foreach (
            string dir in plan.Where(p => p.Type == TransferType.Upload)
                .Select(p => ParentOf(p.RemotePath))
                .Distinct()
        )
        {
            try
            {
                List<RemoteFileInfo> entries = await _sftpService.ListDirectoryAsync(
                    _sessionId,
                    dir,
                    ct
                );
                map[dir] = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 该目录退回 ExistsAsync 兜底。
            }
        }
        return map;
    }

    /// <summary>
    /// 远端路径是否存在:优先查预先列举的目录名单(零网络往返、零内部异常),
    /// 目录不在名单中才退回逐路径 ExistsAsync。
    /// </summary>
    private async Task<bool> RemoteExistsAsync(
        string remotePath,
        Dictionary<string, HashSet<string>> remoteNames,
        CancellationToken ct
    )
    {
        if (remoteNames.TryGetValue(ParentOf(remotePath), out HashSet<string>? names))
        {
            return names.Contains(NameOf(remotePath));
        }
        return await _sftpService.ExistsAsync(_sessionId, remotePath, ct);
    }

    private static string NameOf(string remotePath) =>
        remotePath[(remotePath.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>
    /// 按冲突策略处理一个计划中的上传:对照预列举的远端目录名单检查同名文件
    /// (“覆盖”策略下连列举都省去,直接沿用 SFTP 覆盖语义),冲突时返回 null 表示跳过,
    /// 或返回(可能改了远端路径的)计划项。
    /// </summary>
    private async Task<PlannedFileTransfer?> ResolveRemoteConflictAsync(
        PlannedFileTransfer item,
        Dictionary<string, HashSet<string>> remoteNames,
        CancellationToken ct
    )
    {
        if (item.Type != TransferType.Upload || TransferOptions.ConflictPolicy == "overwrite")
        {
            return item;
        }
        if (!await RemoteExistsAsync(item.RemotePath, remoteNames, ct))
        {
            return item;
        }
        switch (TransferOptions.ConflictPolicy)
        {
            case "skip":
                return null;
            case "rename":
                return item with
                {
                    RemotePath = await NextAvailableRemoteNameAsync(
                        item.RemotePath,
                        remoteNames,
                        ct
                    ),
                };
            default: // ask
                if (ConfirmRemoteOverwrite is null)
                {
                    return item;
                }
                return await ConfirmRemoteOverwrite(item.RemotePath) ? item : null;
        }
    }

    /// <summary>
    /// 远端 "file.txt" → "file (1).txt"(取第一个不存在的序号)。选中的名字会记入
    /// 目录名单,同批次里后续重命名不会撞上它。
    /// </summary>
    private async Task<string> NextAvailableRemoteNameAsync(
        string remotePath,
        Dictionary<string, HashSet<string>> remoteNames,
        CancellationToken ct
    )
    {
        string dir = ParentOf(remotePath);
        string name = NameOf(remotePath);
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[dot..] : "";
        for (int i = 1; i < 10000; i++)
        {
            string candidate = CombinePath(dir, $"{stem} ({i}){ext}");
            if (!await RemoteExistsAsync(candidate, remoteNames, ct))
            {
                if (remoteNames.TryGetValue(dir, out HashSet<string>? names))
                {
                    names.Add(NameOf(candidate));
                }
                return candidate;
            }
        }
        return remotePath;
    }

    /// <summary>"file.txt" → "file (1).txt"(取第一个不存在的序号)。</summary>
    private static string NextAvailableLocalName(string localPath)
    {
        string dir = Path.GetDirectoryName(localPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(localPath);
        string ext = Path.GetExtension(localPath);
        for (int i = 1; i < 10000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return localPath;
    }

    /// <summary>
    /// Runs one transfer end to end: registers it with the toast, streams progress into
    /// it, and settles the final state. A failure marks the row red and returns; a cancellation
    /// marks the row cancelled, removes any partial local file, and propagates so the batch stops.
    /// </summary>
    private async Task RunTransferAsync(
        TransferType type,
        string localPath,
        string remotePath,
        CancellationToken ct
    )
    {
        var task = new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = type,
            LocalPath = localPath,
            RemotePath = remotePath,
            Status = TransferStatus.InProgress,
        };
        TransferStatus finalStatus = TransferStatus.Failed;
        TransferSink?.AddTransfer(task);
        TransferItemViewModel? item = TransferSink?.FindTransfer(task.Id);
        var progress = new Progress<TransferProgress>(p => item?.UpdateProgress(p));
        try
        {
            if (type == TransferType.Upload)
            {
                await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress, ct);
            }
            else
            {
                await _sftpService.DownloadFileAsync(
                    _sessionId,
                    remotePath,
                    localPath,
                    progress,
                    ct
                );
            }
            item?.Status = TransferStatus.Completed;
            finalStatus = TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            item?.Status = TransferStatus.Cancelled;
            finalStatus = TransferStatus.Cancelled;

            // A cancelled download leaves a half-written file behind; drop it.
            if (type == TransferType.Download)
            {
                TryDeleteLocalFile(localPath);
            }
            throw;
        }
        catch (Exception ex)
        {
            item?.Status = TransferStatus.Failed;
            ErrorMessage = ex.Message;
        }
        finally
        {
            TransferSink?.NotifyTaskSettled();

            // 记录传输日志(设置 → 文件传输 → 日志记录)。
            if (TransferOptions.TransferLogging)
            {
                TransferLogService.Append(
                    TransferOptions.LogDirectory,
                    type,
                    localPath,
                    remotePath,
                    finalStatus
                );
            }
        }
    }

    private static void TryDeleteLocalFile(string localPath)
    {
        try
        {
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
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
        if (PromptForText is null)
        {
            return;
        }
        string? name = await PromptForText(Strings.NewFolder, "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        string trimmedName = name.Trim();
        if (!LocalPathSafety.IsSafeLeafName(trimmedName))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        try
        {
            ErrorMessage = null;
            await _sftpService.CreateDirectoryAsync(
                _sessionId,
                CombinePath(CurrentPath, trimmedName),
                ct
            );
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task NewFileAsync(CancellationToken ct = default)
    {
        if (PromptForText is null)
        {
            return;
        }
        string? name = await PromptForText(Strings.NewFile, "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        string trimmedName = name.Trim();
        if (!LocalPathSafety.IsSafeLeafName(trimmedName))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        try
        {
            ErrorMessage = null;
            await _sftpService.CreateFileAsync(
                _sessionId,
                CombinePath(CurrentPath, trimmedName),
                ct
            );
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task RenameAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (PromptForText is null || file is null || file.IsParentEntry)
        {
            return;
        }
        string? newName = await PromptForText(Strings.Rename, file.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == file.Name)
        {
            return;
        }
        string trimmedName = newName.Trim();
        if (!LocalPathSafety.IsSafeLeafName(trimmedName))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        try
        {
            ErrorMessage = null;
            string target = CombinePath(ParentOf(file.FullPath), trimmedName);
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
        if (PromptForText is null || file is null || file.IsParentEntry)
        {
            return;
        }
        string? destination = await PromptForText(Strings.MoveToPrompt, file.FullPath);
        if (string.IsNullOrWhiteSpace(destination) || destination.Trim() == file.FullPath)
        {
            return;
        }
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
        {
            return;
        }
        await CopyToClipboard(file.FullPath);
    }

    private async Task CopyNameAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (CopyToClipboard is null || file is null || file.IsParentEntry)
        {
            return;
        }
        await CopyToClipboard(file.Name);
    }

    private async Task ShowPropertiesAsync(
        RemoteFileInfoViewModel? file,
        CancellationToken ct = default
    )
    {
        if (ShowFileProperties is null || file is null || file.IsParentEntry)
        {
            return;
        }

        // 属性弹窗内含权限矩阵;确定且权限有变化时返回新 mode,由这里落到 chmod。
        short? mode = await ShowFileProperties(file);
        if (mode is null)
        {
            return;
        }
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

    private async Task DeleteItemAsync(
        RemoteFileInfoViewModel? file,
        CancellationToken ct = default
    )
    {
        if (file is null || file.IsParentEntry)
        {
            return;
        }
        if (ConfirmDelete is not null)
        {
            string template = file.IsDirectory
                ? Strings.ConfirmDeleteFolder
                : Strings.ConfirmDeleteFile;
            bool ok = await ConfirmDelete(WithServerTag(string.Format(template, file.Name)));
            if (!ok)
            {
                return;
            }
        }
        await DeleteManyAsync([file], ct);
    }

    private async Task DeleteSelectedAsync(CancellationToken ct = default)
    {
        var targets = SelectedFiles.Where(f => !f.IsParentEntry).ToList();
        if (targets.Count == 0)
        {
            return;
        }
        if (ConfirmDelete is not null)
        {
            bool ok = await ConfirmDelete(
                WithServerTag(
                    targets.Count == 1
                        ? string.Format(
                            targets[0].IsDirectory
                                ? Strings.ConfirmDeleteFolder
                                : Strings.ConfirmDeleteFile,
                            targets[0].Name
                        )
                        : string.Format(Strings.ConfirmDeleteMultiple, targets.Count)
                )
            );
            if (!ok)
            {
                return;
            }
        }
        await DeleteManyAsync(targets, ct);
    }

    /// <summary>
    /// Deletes the given entries one after another behind a single busy overlay; the
    /// per-entry recursive progress is folded into one running "deleted / total" readout.
    /// </summary>
    private async Task DeleteManyAsync(
        IReadOnlyList<RemoteFileInfoViewModel> targets,
        CancellationToken ct
    )
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
            for (int i = 0; i < targets.Count; i++)
            {
                int index = i;
                var progress = new Progress<SftpDeleteProgress>(p =>
                {
                    if (p.TotalCount > 0)
                    {
                        IsDeleteProgressIndeterminate = false;
                        // Weight each entry equally so a huge folder among small files still moves the bar.
                        DeleteProgressPercent = ((index * 100.0) + p.Percentage) / targets.Count;
                        BusyText = string.Format(
                            Strings.DeletingProgress,
                            p.DeletedCount,
                            p.TotalCount
                        );
                    }
                    else
                    {
                        IsDeleteProgressIndeterminate = true;
                        BusyText = Strings.Deleting;
                    }
                });
                await _sftpService.DeleteAsync(
                    _sessionId,
                    targets[i].FullPath,
                    progress,
                    cts.Token
                );
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
        string trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }

    private void ToggleVisibility() => IsVisible = !IsVisible;

    /// <summary>
    /// A single file scheduled for transfer, resolved up front so the whole batch can be
    /// counted and cancelled as one unit.
    /// </summary>
    private sealed record PlannedFileTransfer(
        TransferType Type,
        string LocalPath,
        string RemotePath
    );
}
