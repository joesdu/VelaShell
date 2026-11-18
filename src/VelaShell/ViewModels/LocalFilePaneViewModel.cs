using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>SFTP 双栏文档本地侧的领域视图模型。</summary>
public sealed class LocalFilePaneViewModel : ReactiveObject, IDisposable
{
    private readonly TransferOptions _transferOptions;
    private readonly ILocalFileSystem _fileSystem;
    private readonly ILocalRootProvider _rootProvider;
    private readonly BatchObservableCollection<LocalFileEntry> _entries = [];
    private readonly ObservableCollection<LocalRootEntry> _roots = [];
    private readonly CancellationTokenSource _lifetime = new();
    private long _navigationVersion;
    private bool _isSwitchingRoot;

    /// <summary>使用配置中的传输下载目录创建一个本地面板。</summary>
    public LocalFilePaneViewModel(TransferOptions transferOptions)
        : this(transferOptions, null) { }

    internal LocalFilePaneViewModel(
        TransferOptions transferOptions,
        ILocalFileSystem? fileSystem = null,
        ILocalRootProvider? rootProvider = null
    )
    {
        _transferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions));
        _fileSystem = fileSystem ?? new PhysicalLocalFileSystem();
        _rootProvider = rootProvider ?? new PhysicalLocalRootProvider();
        Entries = _entries;
        Roots = _roots;
        SelectedEntries = [];
        NavigateToCommand = ReactiveCommand.CreateFromTask<string>(path => NavigateToAsync(path, _lifetime.Token));
        ActivateCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => ActivateAsync(entry, _lifetime.Token));
        GoUpCommand = ReactiveCommand.CreateFromTask(() => GoUpAsync(_lifetime.Token));
        GoBackCommand = ReactiveCommand.CreateFromTask(() => GoBackAsync(_lifetime.Token), this.WhenAnyValue(x => x.CanGoBack));
        GoForwardCommand = ReactiveCommand.CreateFromTask(() => GoForwardAsync(_lifetime.Token), this.WhenAnyValue(x => x.CanGoForward));
        RefreshCommand = ReactiveCommand.CreateFromTask(() => RefreshAsync(_lifetime.Token));
        SwitchRootCommand = ReactiveCommand.CreateFromTask<LocalRootEntry>(root => SwitchRootAsync(root, _lifetime.Token));
        RefreshRootsCommand = ReactiveCommand.CreateFromTask(() => RefreshRootsAsync(_lifetime.Token));
        DeleteItemCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => DeleteItemAsync(entry, _lifetime.Token));
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(() => DeleteSelectedAsync(_lifetime.Token));
        UploadSelectedCommand = ReactiveCommand.CreateFromTask(() => UploadSelectedAsync?.Invoke() ?? Task.CompletedTask);
        RenameCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => RenameAsync(entry, _lifetime.Token));
        NewFolderCommand = ReactiveCommand.CreateFromTask(() => NewFolderAsync(_lifetime.Token));
        NewFileCommand = ReactiveCommand.CreateFromTask(() => NewFileAsync(_lifetime.Token));
        OpenItemCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => OpenItemAsync(entry, _lifetime.Token));
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
        BeginPathEditCommand = ReactiveCommand.Create(BeginPathEdit);
        CancelPathEditCommand = ReactiveCommand.Create(CancelPathEdit);
        CommitPathEditCommand = ReactiveCommand.CreateFromTask(() => CommitPathEditAsync(_lifetime.Token));
    }

    /// <summary>可见的本地行,包含文件系统根之外的一个合成父目录行。</summary>
    public ObservableCollection<LocalFileEntry> Entries { get; }

    /// <summary>本地面板可选中的物理根。</summary>
    public ObservableCollection<LocalRootEntry> Roots { get; }

    /// <summary>包含当前路径的根,按最长前缀匹配选出。</summary>
    public LocalRootEntry? SelectedRoot
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>由宿主用于多项操作的已选中行。</summary>
    public ObservableCollection<LocalFileEntry> SelectedEntries { get; }

    /// <summary>导航到规范化的本地目录。</summary>
    public ReactiveCommand<string, RxVoid> NavigateToCommand { get; }

    /// <summary>进入一个被激活的目录行。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> ActivateCommand { get; }

    /// <summary>导航到父目录。</summary>
    public ReactiveCommand<RxVoid, RxVoid> GoUpCommand { get; }

    /// <summary>刷新当前目录列举。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>将当前目录切换到所选根目录。</summary>
    public ReactiveCommand<LocalRootEntry, RxVoid> SwitchRootCommand { get; }

    /// <summary>重新加载可用的本地根列表。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshRootsCommand { get; }

    /// <summary>经确认后永久删除单个本地条目。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> DeleteItemCommand { get; }

    /// <summary>经确认后永久删除选中的本地条目。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSelectedCommand { get; }

    /// <summary>由宿主提供的上传委托,用于独立 SFTP 文档。</summary>
    public ReactiveCommand<RxVoid, RxVoid> UploadSelectedCommand { get; }

    /// <summary>就地重命名选中的本地条目。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> RenameCommand { get; }

    /// <summary>在当前目录中创建新文件夹。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewFolderCommand { get; }

    /// <summary>在当前目录中新建空文件(同名已存在时报错、不覆盖)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewFileCommand { get; }

    /// <summary>用默认程序打开本地文件,或进入某个目录。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> OpenItemCommand { get; }

    /// <summary>由宿主提供、被 UploadSelectedCommand 调用的上传委托。</summary>
    public Func<Task>? UploadSelectedAsync { get; set; }

    /// <summary>按名称、大小或修改时间对可见行排序。</summary>
    public ReactiveCommand<string, RxVoid> SortCommand { get; }

    /// <summary>注入的破坏性操作确认回调。</summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>由视图设置:提示输入一行文本(标题、初始值)→ 输入的文本或 null。</summary>
    public Func<string, string, Task<string?>>? PromptForText { get; set; }

    /// <summary>由视图设置:用操作系统默认程序打开本地文件。</summary>
    public Func<string, Task>? OpenLocalFile { get; set; }

    /// <summary>在文档排空并关闭其 SFTP 通道前,取消本地工作。</summary>
    public void Detach() => _lifetime.Cancel();

    /// <summary>当前列举的规范化目录。</summary>
    public string CurrentPath
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Breadcrumbs));
            this.RaisePropertyChanged(nameof(RootBreadcrumbLabel));
            this.RaisePropertyChanged(nameof(RootBreadcrumbPath));
        }
    } = string.Empty;

    /// <summary>当前路径的可点击面包屑分段,最深的在最后。根由 RootBreadcrumbLabel 处理。</summary>
    public IReadOnlyList<LocalBreadcrumbSegment> Breadcrumbs
    {
        get
        {
            string root = RootBreadcrumbLabel;
            if (string.IsNullOrEmpty(CurrentPath) || CurrentPath == root)
            {
                return [];
            }

            // 守卫:CurrentPath 仍为空时 RootBreadcrumbLabel 可能解析为占位根,
            // 此时下方切片会因 startIndex > length 而报错,需直接返回空。
            if (root.Length >= CurrentPath.Length || !CurrentPath.StartsWith(root, PathComparison))
            {
                return [];
            }

            char sep = Path.DirectorySeparatorChar;
            ReadOnlySpan<char> relative = CurrentPath.AsSpan(root.Length).TrimStart(sep);
            if (relative.Length == 0)
            {
                return [];
            }

            string[] parts = relative.ToString().Split(sep, StringSplitOptions.RemoveEmptyEntries);
            return [.. parts.Select((part, i) =>
            {
                string path = root.TrimEnd(sep) + sep + string.Join(sep, parts.Take(i + 1));
                return new LocalBreadcrumbSegment(part, path);
            })];
        }
    }

    /// <summary>根面包屑按钮的标签(如 Linux 上为“/”,Windows 上为“C:\”)。</summary>
    public string RootBreadcrumbLabel
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath))
            {
                return OperatingSystem.IsWindows() ? "C:\\" : "/";
            }
            string? root = Path.GetPathRoot(CurrentPath);
            if (string.IsNullOrEmpty(root))
            {
                return "/";
            }
            // 确保 Windows 根路径以分隔符结尾(如 "C:\" 而非 "C:")
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
    }

    /// <summary>根面包屑按钮导航所用的路径。</summary>
    public string RootBreadcrumbPath => RootBreadcrumbLabel;

    /// <summary>用于面包屑显示的 OS 路径分隔符字符。</summary>
    public static string PathSeparator => Path.DirectorySeparatorChar.ToString();

    /// <summary>目录列举是否正在进行。</summary>
    public bool IsDirectoryLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次列举或删除的错误。</summary>
    public string? ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前生效的排序列。</summary>
    public string SortColumn
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "name";

    /// <summary>当前排序是否为降序。</summary>
    public bool SortDescending
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>“名称”列表头的排序方向箭头(仅当前排序列显示,否则为空;与远端栏同一套字形)。</summary>
    public string NameSortGlyph => GlyphFor("name");

    /// <summary>“大小”列表头的排序方向箭头。</summary>
    public string SizeSortGlyph => GlyphFor("size");

    /// <summary>“修改时间”列表头的排序方向箭头。</summary>
    public string ModifiedSortGlyph => GlyphFor("modified");

    private string GlyphFor(string column) =>
        SortColumn == column
            ? SortDescending
                ? " ▼"
                : " ▲"
            : string.Empty;

    /// <summary>加载配置的目录,失败时回退到用户主目录。</summary>
    public async Task LoadInitialAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRootsAsync(cancellationToken);
        string configured = ExpandConfiguredPath(_transferOptions.LocalDownloadDirectory);
        string home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (string candidate in new[] { configured, home }.Distinct(StringComparer.Ordinal))
        {
            if (!DirectoryIsAccessible(candidate))
            {
                continue;
            }

            await NavigateToAsync(candidate, cancellationToken);
            if (ErrorMessage is null)
            {
                return;
            }
        }

        CurrentPath = home;
        SyncSelectedRoot(home);
    }

    /// <summary>重新加载本地根列表,保留当前选中的路径。</summary>
    public async Task RefreshRootsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LocalRootEntry> roots = await _rootProvider.EnumerateAsync(cancellationToken);
        _roots.Clear();
        foreach (LocalRootEntry root in roots)
        {
            _roots.Add(root);
        }
        SyncSelectedRoot(CurrentPath);
    }

    /// <summary>若所选根可访问,则切换到其路径。</summary>
    public async Task SwitchRootAsync(LocalRootEntry? root, CancellationToken cancellationToken = default)
    {
        if (_isSwitchingRoot || root is null || !root.IsAccessible)
        {
            return;
        }

        _isSwitchingRoot = true;
        try
        {
            await NavigateToAsync(root.FullPath, cancellationToken);
        }
        finally
        {
            _isSwitchingRoot = false;
        }
    }

    /// <summary>列举目录,列举失败时保留原有行。进了别的目录就把原目录记进后退历史。</summary>
    public Task NavigateToAsync(string path, CancellationToken cancellationToken = default) =>
        NavigateCoreAsync(path, recordHistory: true, cancellationToken);

    internal sealed record PreparedDirectory(string Path, IReadOnlyList<LocalFileEntry> Entries);

    // 只读取,不修改当前目录、列表、历史或监视器。映射的远端导航失败时可直接丢弃。
    internal async Task<PreparedDirectory> PrepareDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        string canonical = Path.GetFullPath(path);
        List<LocalFileEntry> listed = await ListDirectoryAsync(canonical, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        return new(canonical, listed);
    }

    internal void CommitPreparedDirectory(PreparedDirectory prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _navigationVersion);
        ErrorMessage = null;
        ApplyDirectory(prepared.Path, prepared.Entries, recordHistory: true);
        IsDirectoryLoading = false;
    }

    /// <param name="path">目标目录。</param>
    /// <param name="recordHistory">进了别的目录时要不要记历史;后退 / 前进自己挪历史,传 false。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task NavigateCoreAsync(string path, bool recordHistory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonical = Path.GetFullPath(path);
        long version = Interlocked.Increment(ref _navigationVersion);
        try
        {
            ErrorMessage = null;
            IsDirectoryLoading = true;
            List<LocalFileEntry> listed = await ListDirectoryAsync(canonical, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref _navigationVersion))
            {
                return;
            }

            ApplyDirectory(canonical, listed, recordHistory);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _navigationVersion))
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _navigationVersion))
            {
                IsDirectoryLoading = false;
            }
        }
    }

    private void ApplyDirectory(string canonical, IReadOnlyList<LocalFileEntry> listed, bool recordHistory)
    {
        bool changed = !string.Equals(CurrentPath, canonical, PathComparison);
        var selected = SelectedEntries.Where(entry => !entry.IsParentEntry)
            .Select(entry => entry.FullPath).ToHashSet(PathComparisonComparer);
        string previousPath = CurrentPath;
        CurrentPath = canonical;
        if (changed)
        {
            SelectedEntries.Clear();
            if (recordHistory && previousPath.Length > 0)
            {
                PushHistory(previousPath);
            }
            if (_filterText.Length > 0)
            {
                this.RaiseAndSetIfChanged(ref _filterText, string.Empty, nameof(FilterText));
            }
        }
        RebuildEntries(listed);
        SyncSelectedRoot(canonical);
        if (!changed)
        {
            RestoreSelection(selected);
        }
        RearmWatcher(canonical);
    }

    /// <summary>刷新当前目录,或执行初始加载。</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(CurrentPath)
            ? LoadInitialAsync(cancellationToken)
            : NavigateToAsync(CurrentPath, cancellationToken);

    // ---- 手动输入路径 ----
    //
    // 面包屑只能沿着已经列出来的目录一级级点:去 D:\Books\123\35 要先在下拉框里切盘、再点三下,
    // 手里明明有现成的路径(资源管理器地址栏里复制来的)却用不上。所以路径栏要能直接敲、直接贴 ——
    // 与远端浏览器的路径栏(#226)同一套手势:铅笔按钮或 Ctrl+L 切成输入框,回车跳转,Esc / 失焦取消。

    /// <summary>路径栏是否处于手动输入态;为 true 时视图用输入框替换面包屑。</summary>
    public bool IsPathEditing
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>手动输入态下输入框里的文本。</summary>
    public string PathEditText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>切入手动输入态。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BeginPathEditCommand { get; }

    /// <summary>放弃手动输入,退回面包屑。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelPathEditCommand { get; }

    /// <summary>提交手动输入的路径并导航。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CommitPathEditCommand { get; }

    /// <summary>进入手动输入态时触发,供视图聚焦输入框并全选文本。</summary>
    public event EventHandler? PathEditActivated;

    /// <summary>切入手动输入态,输入框预填当前路径。</summary>
    public void BeginPathEdit()
    {
        if (IsPathEditing)
        {
            return;
        }
        PathEditText = CurrentPath;
        IsPathEditing = true;
        PathEditActivated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>放弃手动输入。</summary>
    public void CancelPathEdit() => IsPathEditing = false;

    /// <summary>
    /// 跳到输入的路径。输入的是文件时进它所在的目录并选中它 —— 资源管理器「复制为路径」拷出来的
    /// 往往就是文件,贴进来回车就选好了,再一回车就能传。
    /// </summary>
    public async Task CommitPathEditAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPathEditing)
        {
            return;
        }

        string? target;
        try
        {
            target = ResolvePathInput(PathEditText, CurrentPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorMessage = ex.Message;
            return;
        }
        if (target is null)
        {
            IsPathEditing = false;
            return;
        }

        string? fileName = null;
        string directory = target;
        if (await _fileSystem.GetFileSizeAsync(target, cancellationToken) >= 0)
        {
            fileName = Path.GetFileName(target);
            directory = ParentOf(target);
        }

        // 输错路径是手动输入的常态。导航失败(ErrorMessage 非空)时保持输入态、不动已输入的文本,
        // 让用户就地改一个字再按回车;成功才退回面包屑。
        await NavigateToAsync(directory, cancellationToken);
        if (ErrorMessage is not null)
        {
            return;
        }
        IsPathEditing = false;

        // 输入的文件就在当前目录里时不会换目录,过滤条件还在;它挡着那个文件就清掉,不然选不中。
        if (fileName is not null && _filterText.Length > 0
            && !Entries.Any(entry => !entry.IsParentEntry && string.Equals(entry.Name, fileName, PathComparison)))
        {
            FilterText = string.Empty;
        }

        if (fileName is not null
            && Entries.FirstOrDefault(entry => !entry.IsParentEntry && string.Equals(entry.Name, fileName, PathComparison)) is { } file)
        {
            SelectedEntries.Clear();
            SelectedEntries.Add(file);
        }
    }

    /// <summary>
    /// 把路径栏里的文本解析成绝对路径;只有空白时返回 null。
    /// </summary>
    /// <remarks>
    /// 认这几种写法:两头的双引号(资源管理器「复制为路径」会带上)、<c>%VAR%</c> 环境变量、
    /// <c>~</c> / <c>~/…</c> 开头的家目录、相对路径(相对<b>当前目录</b>,所以 <c>..</c>、子目录名都能用)。
    /// 只写一个盘符的 <c>D:</c> 当作 <c>D:\</c> —— 按 Windows 的规矩它是「D 盘上的当前目录」,
    /// 但没人在地址栏里敲 D: 是想要那个。
    /// </remarks>
    /// <param name="input">输入框里的原文。</param>
    /// <param name="currentDirectory">当前目录,相对路径以它为基准;为空时用用户主目录。</param>
    internal static string? ResolvePathInput(string? input, string currentDirectory)
    {
        string value = (input ?? string.Empty).Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Trim();
        }
        if (value.Length == 0)
        {
            return null;
        }

        value = Environment.ExpandEnvironmentVariables(value);
        string home = UserPathResolver.Home;
        if (value == "~")
        {
            value = home;
        }
        else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Combine(home, value[2..].TrimStart('/', '\\'));
        }

        if (OperatingSystem.IsWindows() && value.Length == 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            value += Path.DirectorySeparatorChar;
        }

        // 双参重载:相对路径只认给定的基准,绝不落到进程工作目录上。
        return Path.GetFullPath(value, string.IsNullOrEmpty(currentDirectory) ? home : currentDirectory);
    }

    // ---- 目录变化自动刷新 ----

    /// <summary>当前目录的监视句柄(切目录时换,释放即停)。</summary>
    private IDisposable? _watcher;

    /// <summary>防抖计时器:解压、批量拷贝会在几十毫秒内打出上百个事件。</summary>
    private Timer? _watchDebounce;

    /// <summary>防抖窗口。够长以合并一次批量操作,够短以让用户觉得"它自己就更新了"。</summary>
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 把目录监视改挂到新的当前目录。
    /// </summary>
    /// <remarks>
    /// 远端面板早就有静默刷新了,本地面板却要手按 F5 —— 在本地解个压、下个文件,
    /// 面板还停在旧内容上。这里补齐对称性:监视不了(网络盘等)就静默退化,不报错。
    /// </remarks>
    private void RearmWatcher(string directory)
    {
        _watcher?.Dispose();
        _watcher = _fileSystem.Watch(directory, OnDirectoryChanged);
    }

    private void OnDirectoryChanged()
    {
        // FileSystemWatcher 的回调在线程池线程上;这里只重置计时器,真正的刷新
        // 由计时器回调发起,并且要回到 UI 线程 —— 刷新会动 ObservableCollection。
        _watchDebounce ??= new(_ => RefreshFromWatcher(), null, Timeout.Infinite, Timeout.Infinite);
        _watchDebounce.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
    }

    private void RefreshFromWatcher()
    {
        if (_lifetime.IsCancellationRequested || string.IsNullOrEmpty(CurrentPath))
        {
            return;
        }
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            // 期间用户可能已经导航走了 —— NavigateToAsync 自带版本号守卫,
            // 这里再挡一次纯粹是少发一次没用的列举。
            if (!_lifetime.IsCancellationRequested && !string.IsNullOrEmpty(CurrentPath))
            {
                _ = NavigateToAsync(CurrentPath, _lifetime.Token);
            }
        });
    }

    private bool _disposed;

    /// <summary>停掉目录监视与防抖计时器,并取消未完成的本地工作。</summary>
    /// <remarks>
    /// 幂等:文档关闭路径与宿主的清理路径都可能调到它,重复调用不该在
    /// 已释放的 <see cref="CancellationTokenSource" /> 上炸掉。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        _watchDebounce?.Dispose();
        _watchDebounce = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>导航到父目录,停留在文件系统根处。</summary>
    public Task GoUpAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(CurrentPath)
            ? LoadInitialAsync(cancellationToken)
            : NavigateToAsync(ParentOf(CurrentPath), cancellationToken);

    // ---- 后退 / 前进(与远端栏同一口径,见 FileBrowserViewModel.GoBackAsync)----
    //
    // 历史只活在这个面板实例里;文档关掉重开、断线后重开的都是新面板,历史不留。

    private readonly List<string> _backHistory = [];
    private readonly List<string> _forwardHistory = [];

    /// <summary>有没有可以后退的目录。</summary>
    public bool CanGoBack => _backHistory.Count > 0;

    /// <summary>有没有可以前进的目录。</summary>
    public bool CanGoForward => _forwardHistory.Count > 0;

    /// <summary>后退到上一个待过的目录(Alt+←)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> GoBackCommand { get; }

    /// <summary>前进到后退之前的目录(Alt+→)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> GoForwardCommand { get; }

    private void PushHistory(string previous)
    {
        if (_backHistory.Count == 0 || !string.Equals(_backHistory[^1], previous, PathComparison))
        {
            _backHistory.Add(previous);
            if (_backHistory.Count > FileBrowserViewModel.MaxHistory)
            {
                _backHistory.RemoveAt(0);
            }
        }
        _forwardHistory.Clear();
        RaiseHistoryChanged();
    }

    private void RaiseHistoryChanged()
    {
        this.RaisePropertyChanged(nameof(CanGoBack));
        this.RaisePropertyChanged(nameof(CanGoForward));
    }

    /// <summary>后退一步。目标目录进不去(刚被删、拔了盘)时照常报错,历史原样不动。</summary>
    public Task GoBackAsync(CancellationToken cancellationToken = default) =>
        MoveInHistoryAsync(_backHistory, _forwardHistory, cancellationToken);

    /// <summary>前进一步;规则同 <see cref="GoBackAsync" />。</summary>
    public Task GoForwardAsync(CancellationToken cancellationToken = default) =>
        MoveInHistoryAsync(_forwardHistory, _backHistory, cancellationToken);

    private async Task MoveInHistoryAsync(List<string> from, List<string> to, CancellationToken cancellationToken)
    {
        if (from.Count == 0)
        {
            return;
        }
        string target = from[^1];
        string origin = CurrentPath;
        await NavigateCoreAsync(target, recordHistory: false, cancellationToken);
        if (ErrorMessage is not null
            || !string.Equals(CurrentPath, Path.GetFullPath(target), PathComparison)
            || from.Count == 0
            || !string.Equals(from[^1], target, PathComparison))
        {
            return;
        }
        from.RemoveAt(from.Count - 1);
        to.Add(origin);
        RaiseHistoryChanged();
    }

    /// <summary>进入一个目录条目;文件与父目录行不响应。</summary>
    public Task ActivateAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default) =>
        entry is { IsDirectory: true } ? NavigateToAsync(entry.FullPath, cancellationToken) : Task.CompletedTask;

    /// <summary>经确认后永久删除单个条目,且不跟随链接。</summary>
    public async Task DeleteItemAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        if (entry is null || entry.IsParentEntry || ConfirmDelete is null)
        {
            return;
        }

        if (!await ConfirmDelete(ConfirmDeleteMessage(entry)))
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            await DeletePathAsync(ToFileSystemEntry(entry), cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>与远端 FileBrowserViewModel 共用同一组文案,按目录/文件区分措辞。</summary>
    private static string ConfirmDeleteMessage(LocalFileEntry entry) =>
        Strings.Format(entry.IsDirectory ? "ConfirmDeleteFolder" : "ConfirmDeleteFile", entry.Name);

    /// <summary>经确认后永久删除选中条目,且不跟随链接。</summary>
    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        LocalFileEntry[] targets = [.. SelectedEntries.Where(entry => !entry.IsParentEntry)];
        if (targets.Length == 0 || ConfirmDelete is null)
        {
            return;
        }
        string message = targets.Length == 1
            ? ConfirmDeleteMessage(targets[0])
            : Strings.Format("ConfirmDeleteMultiple", targets.Length);
        if (!await ConfirmDelete(message))
        {
            return;
        }

        foreach (LocalFileEntry target in targets)
        {
            try
            {
                await DeletePathAsync(ToFileSystemEntry(target), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = null;
                break;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                break;
            }
        }

        await RefreshAsync(cancellationToken);
    }

    private async Task<List<LocalFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<LocalFileSystemEntry> entries = await _fileSystem.EnumerateAsync(
            path,
            cancellationToken
        );
        return [
            .. entries.Select(entry => new LocalFileEntry(
                entry.Name,
                entry.FullPath,
                entry.IsDirectory,
                entry.SizeBytes,
                entry.LastModified,
                entry.IsReparsePoint
            )),
        ];
    }

    private async Task DeletePathAsync(
        LocalFileSystemEntry entry,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.IsReparsePoint)
        {
            await DeleteSingleEntryAsync(entry, cancellationToken);
            return;
        }

        if (!entry.IsDirectory)
        {
            await _fileSystem.DeleteFileAsync(entry.FullPath, cancellationToken);
            return;
        }

        IReadOnlyList<LocalFileSystemEntry> children = await _fileSystem.EnumerateAsync(
            entry.FullPath,
            cancellationToken
        );
        foreach (LocalFileSystemEntry child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeletePathAsync(child, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _fileSystem.DeleteDirectoryAsync(entry.FullPath, cancellationToken);
    }

    private async Task DeleteSingleEntryAsync(
        LocalFileSystemEntry entry,
        CancellationToken cancellationToken
    )
    {
        if (entry.IsDirectory)
        {
            await _fileSystem.DeleteDirectoryAsync(entry.FullPath, cancellationToken);
        }
        else
        {
            await _fileSystem.DeleteFileAsync(entry.FullPath, cancellationToken);
        }
    }

    private static LocalFileSystemEntry ToFileSystemEntry(LocalFileEntry entry) =>
        new(
            entry.Name,
            entry.FullPath,
            entry.IsDirectory,
            entry.SizeBytes,
            entry.LastModified,
            entry.IsReparsePoint
        );

    private void ToggleSort(string? column)
    {
        if (string.IsNullOrWhiteSpace(column) || column is not ("name" or "size" or "modified"))
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
        this.RaisePropertyChanged(nameof(ModifiedSortGlyph));
        // 从完整列举重排,而不是从眼前的行:过滤藏起来的行不在 Entries 里,从那里重排就永远找不回来了。
        RebuildEntries(_listing);
    }

    // ---- 当前目录过滤(与远端栏同一口径,见 FileBrowserViewModel.FilterText)----

    /// <summary>当前目录的完整列举(过滤与排序之前),排序与过滤都从它重建可见行。</summary>
    private List<LocalFileEntry> _listing = [];

    private string _filterText = string.Empty;

    /// <summary>过滤框是否展开。收起即清空过滤。</summary>
    public bool IsFilterOpen
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            if (!value)
            {
                FilterText = string.Empty;
            }
        }
    }

    /// <summary>
    /// 过滤框里的文字:按「名字含有」(不分大小写)只过滤显示,空 = 不过滤;换目录时清空。
    /// 藏起来的行不在 <see cref="Entries" /> 里,全选、上传、删除都只对着看得见的行。
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_filterText, value, StringComparison.Ordinal))
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _filterText, value);
            var selected = SelectedEntries
                .Where(entry => !entry.IsParentEntry)
                .Select(entry => entry.FullPath)
                .ToHashSet(PathComparisonComparer);
            RebuildEntries(_listing);
            RestoreSelection(selected);
        }
    }

    private void RebuildEntries(IEnumerable<LocalFileEntry> entries)
    {
        _listing = [.. entries.Where(entry => !entry.IsParentEntry)];
        IEnumerable<LocalFileEntry> sorted = _filterText.Length == 0
            ? _listing
            : _listing.Where(entry => entry.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase));
        IOrderedEnumerable<LocalFileEntry> directories = sorted.OrderByDescending(entry => entry.IsDirectory);
        sorted = SortColumn switch
        {
            "size" => SortDescending ? directories.ThenByDescending(entry => entry.SizeBytes) : directories.ThenBy(entry => entry.SizeBytes),
            "modified" => SortDescending ? directories.ThenByDescending(entry => entry.LastModified) : directories.ThenBy(entry => entry.LastModified),
            _ => SortDescending ? directories.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase) : directories.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };
        List<LocalFileEntry> rebuilt = string.Equals(
            CurrentPath,
            Path.GetPathRoot(CurrentPath),
            PathComparison
        )
            ? [.. sorted]
            : [LocalFileEntry.CreateParent(ParentOf(CurrentPath)), .. sorted];
        _entries.ReplaceAll(rebuilt);
    }

    private void SyncSelectedRoot(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        LocalRootEntry? root = Roots
            .Where(candidate => candidate.IsAccessible && IsPathWithin(candidate.FullPath, path))
            .OrderByDescending(candidate => candidate.FullPath.Length)
            .FirstOrDefault();
        if (!ReferenceEquals(SelectedRoot, root))
        {
            SelectedRoot = root;
        }
    }

    private static bool IsPathWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string canonicalPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(canonicalRoot, canonicalPath, PathComparison)
            || canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison)
            || canonicalPath.StartsWith(canonicalRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private void RestoreSelection(HashSet<string> selectedPaths)
    {
        SelectedEntries.Clear();
        foreach (LocalFileEntry entry in Entries.Where(entry => selectedPaths.Contains(entry.FullPath)))
        {
            SelectedEntries.Add(entry);
        }
    }

    /// <summary>
    /// 解析配置的下载目录:"~" 与相对路径一律以用户主目录为基准(见 <see cref="UserPathResolver" />),
    /// 空值跟随系统"下载"文件夹;该目录不可用时由调用方回退到用户主目录。
    /// </summary>
    private static string ExpandConfiguredPath(string configured) =>
        UserPathResolver.ResolveOrDownloads(configured);

    private static bool DirectoryIsAccessible(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return false;
            }

            using IEnumerator<FileSystemInfo> entries = new DirectoryInfo(path)
                .EnumerateFileSystemInfos()
                .GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string ParentOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        ?? Path.GetPathRoot(path)
        ?? path;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparisonComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task RenameAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        entry ??= SelectedEntries.FirstOrDefault(static e => !e.IsParentEntry);
        if (PromptForText is null || entry is null || entry.IsParentEntry)
        {
            return;
        }
        string? newName = await PromptForText("Rename", entry.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == entry.Name)
        {
            return;
        }
        string trimmedName = newName.Trim();
        if (!LocalPathSafety.IsSafeLeafName(trimmedName))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        string target = Path.Combine(ParentOf(entry.FullPath), trimmedName);
        try
        {
            ErrorMessage = null;
            await _fileSystem.MoveAsync(entry.FullPath, target, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task NewFolderAsync(CancellationToken cancellationToken = default)
    {
        if (PromptForText is null)
        {
            return;
        }
        string? name = await PromptForText(Strings.Get("NewFolder"), "");
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
        string target = Path.Combine(CurrentPath, trimmedName);
        try
        {
            ErrorMessage = null;
            await _fileSystem.CreateDirectoryAsync(target, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 在当前目录新建一个空文件(与远端栏的「新建文件」同一套校验)。同名的已经在了就报错、不覆盖 ——
    /// 新建从来不该把别人的文件清空。
    /// </summary>
    private async Task NewFileAsync(CancellationToken cancellationToken = default)
    {
        if (PromptForText is null || string.IsNullOrEmpty(CurrentPath))
        {
            return;
        }
        string? name = await PromptForText(Strings.Get("NewFile"), "");
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
        string target = Path.Combine(CurrentPath, trimmedName);
        try
        {
            ErrorMessage = null;
            await using (await _fileSystem.OpenWriteAsync(target, FileMode.CreateNew, cancellationToken))
            {
            }
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task OpenItemAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        entry ??= SelectedEntries.FirstOrDefault(static e => !e.IsParentEntry);
        if (entry is null || entry.IsParentEntry)
        {
            return;
        }
        if (entry.IsDirectory)
        {
            await NavigateToAsync(entry.FullPath, cancellationToken);
        }
        else if (OpenLocalFile is not null)
        {
            await OpenLocalFile(entry.FullPath);
        }
    }
}

/// <summary>本地路径面包屑的一个可点击分段。</summary>
public sealed record LocalBreadcrumbSegment(string Name, string Path);
