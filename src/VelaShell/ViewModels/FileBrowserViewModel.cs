using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 头部路径面包屑的一个可点击分段(§6):分段文本及其
/// 所导航到的绝对远程路径。
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
    /// 隐藏文件过滤与排序之前的原始目录列举;可见的
    /// <see cref="Files" /> 集合据此重建。
    /// </summary>
    private readonly List<RemoteFileInfoViewModel> _allFiles = [];
    private readonly BatchObservableCollection<RemoteFileInfoViewModel> _files;

    private readonly Guid _sessionId;
    private readonly ISftpService _sftpService;

    private string _currentPath;
    private long _navigationVersion;

    /// <summary>
    /// 当前这次目录列举的取消源。新的导航到来时取消上一次。
    /// </summary>
    /// <remarks>
    /// 光有 <see cref="_navigationVersion" /> 只能做到"回来了也不用",而那次列举本身还在
    /// 跑完:A→B→C 连着点三下,三次列举会真的一起压在同一条 SFTP 通道上,
    /// 最想看的 C 反而排在最后。目录越大越明显。
    /// </remarks>
    private CancellationTokenSource? _navigationCts;

    /// <summary>取消正在进行的删除;由删除浮层的取消按钮触发。</summary>
    private CancellationTokenSource? _deleteCts;

    /// <summary>在飞的初始加载(合流用,见 LoadInitialAsync)。</summary>
    private Task? _initialLoad;

    /// <summary>
    /// 当本浏览器实例被丢弃(标签关闭,或面板重新绑定到
    /// 另一会话)时取消,使在飞的 SFTP 工作停止与会话拆除争抢(#tab-close NRE)。
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
        BeginPathEditCommand = ReactiveCommand.Create(BeginPathEdit);
        CancelPathEditCommand = ReactiveCommand.Create(CancelPathEdit);
        CommitPathEditCommand = ReactiveCommand.CreateFromTask(CommitPathEditAsync);
        ActivateCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(ActivateAsync);
        GoUpCommand = ReactiveCommand.CreateFromTask(GoUpAsync);
        GoBackCommand = ReactiveCommand.CreateFromTask(() => GoBackAsync(), this.WhenAnyValue(x => x.CanGoBack));
        GoForwardCommand = ReactiveCommand.CreateFromTask(() => GoForwardAsync(), this.WhenAnyValue(x => x.CanGoForward));
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        LoadInitialCommand = ReactiveCommand.CreateFromTask(LoadInitialAsync);
        UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
        NewFolderCommand = ReactiveCommand.CreateFromTask(NewFolderAsync);
        NewFileCommand = ReactiveCommand.CreateFromTask(NewFileAsync);
        NewSymbolicLinkCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel?>(NewSymbolicLinkAsync);
        DownloadItemCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(
            DownloadItemAsync
        );
        RenameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(RenameAsync);
        MoveCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(MoveAsync);
        CopyToCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyToAsync);
        CopyPathCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyPathAsync);
        CopyNameCommand = ReactiveCommand.CreateFromTask<RemoteFileInfoViewModel>(CopyNameAsync);
        CopyCurrentPathCommand = ReactiveCommand.CreateFromTask(CopyCurrentPathAsync);
        InvokeProtocolActionCommand = ReactiveCommand.CreateFromTask<ProtocolActionViewModel>(InvokeProtocolActionAsync);
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
        OpenRemoteSearchCommand = ReactiveCommand.CreateFromTask<string?>(
            root => CanSearchRemote && ShowRemoteSearch is { } show ? show(root) : Task.CompletedTask);
        OpenTerminalHereCommand = ReactiveCommand.CreateFromTask<string?>(
            directory => OpenTerminalAt is { } open ? open(directory ?? CurrentPath) : Task.CompletedTask);
        ToggleVisibilityCommand = ReactiveCommand.Create(ToggleVisibility);
        ToggleHiddenFilesCommand = ReactiveCommand.Create(() =>
        {
            ShowHiddenFiles = !ShowHiddenFiles;

            // 工具栏切换写回持久化设置(设置审计 C-04):与设置中心共用同一状态来源。
            ShowHiddenFilesToggled?.Invoke(ShowHiddenFiles);
        });
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
    }

    /// <summary>本浏览器所根植的 SSH 会话。</summary>
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
                || a.IsSymbolicLink != b.IsSymbolicLink
                || a.LinkTarget != b.LinkTarget
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
    /// 当本实例被替换(标签关闭 / 面板重新绑定)时由宿主调用:
    /// 取消在飞的 SFTP 操作,使它们不会与会话通道拆除争抢。
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
            // 已拆除 —— 无需取消。
        }
        Detached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>本面板被拆除(<see cref="Detach" />)之后触发;对着它开的搜索窗口据此跟着关掉。</summary>
    public event EventHandler? Detached;

    /// <summary>
    /// 把一个操作令牌关联到本浏览器的生命周期,并交出**可释放的**作用域。
    /// </summary>
    /// <remarks>
    /// 这里以前返回的是 <c>CreateLinkedTokenSource(…).Token</c> —— 关联源本身当场就没人
    /// 拿得到了,自然也没人 Dispose。关联的实现是往父令牌上挂一个回调,不 Dispose 就一直挂着:
    /// 一个开着不关的文件面板,每做一次可取消的操作(导航、传输、删除)就往
    /// <see cref="_lifetime" /> 上多留一份注册,直到标签关闭才一起释放。
    /// <para>
    /// 常见情形是调用方根本没传令牌(命令不带),此时不分配任何东西,直接用生命周期令牌。
    /// </para>
    /// </remarks>
    private LifetimeScope LinkToLifetime(CancellationToken ct) => new(ct, _lifetime.Token);

    /// <summary>
    /// <see cref="LinkToLifetime" /> 的作用域:持有关联源,离开作用域即释放注册。
    /// </summary>
    private readonly struct LifetimeScope : IDisposable
    {
        private readonly CancellationTokenSource? _linked;

        internal LifetimeScope(CancellationToken operation, CancellationToken lifetime)
        {
            if (!operation.CanBeCanceled || operation == lifetime)
            {
                // 没传令牌,或传进来的就是生命周期令牌本身 —— 再关联一次纯属自缠。
                _linked = null;
                Token = lifetime;
                return;
            }
            _linked = CancellationTokenSource.CreateLinkedTokenSource(operation, lifetime);
            Token = _linked.Token;
        }

        /// <summary>本次操作应当使用的令牌。</summary>
        public CancellationToken Token { get; }

        public void Dispose() => _linked?.Dispose();
    }

    /// <summary>危险操作确认文案加服务器名前缀,多标签下防止删错服务器上的文件。</summary>
    private string WithServerTag(string message) =>
        string.IsNullOrEmpty(ServerDisplayName) ? message : $"[{ServerDisplayName}] {message}";

    /// <summary>当前目录中可见的行(已过滤隐藏文件、已排序,非根目录时含首行 ".." 返回项)。</summary>
    public ObservableCollection<RemoteFileInfoViewModel> Files { get; }

    /// <summary>列表中当前被多选中的条目(批量下载/删除的作用对象)。</summary>
    public ObservableCollection<RemoteFileInfoViewModel> SelectedFiles { get; }

    /// <summary>成功进入不同目录后触发,视图据此把滚动条重置到顶部。</summary>
    public event EventHandler? DirectoryChanged;

    /// <summary>
    /// 远端搜索(<c>find</c> / <c>grep</c>)的服务;由宿主在 SSH 会话的面板上挂(FTP 与插件协议的面板不挂)。
    /// </summary>
    public IRemoteSearchService? RemoteSearch
    {
        get;
        set
        {
            field = value;
            this.RaisePropertyChanged(nameof(CanSearchRemote));
        }
    }

    /// <summary>工具条与右键菜单里的「搜索」是否出现:要有搜索服务,且这个会话有 SSH 连接(有 exec 通道)。</summary>
    public bool CanSearchRemote => RemoteSearch?.IsAvailable(_sessionId) == true;

    /// <summary>打开搜索窗口;参数是起点目录(目录行的右键菜单传它自己),null = 当前目录(工具条与空白处右键菜单)。</summary>
    public ReactiveCommand<string?, RxVoid> OpenRemoteSearchCommand { get; }

    /// <summary>由视图提供:打开(或提到前面)本面板的搜索窗口;参数是起点目录,null = 当前目录。</summary>
    public Func<string?, Task>? ShowRemoteSearch { get; set; }

    /// <summary>
    /// 由宿主在 SSH 会话(连接类型为 SSH 的配置)的面板上挂:开一个连同一配置的终端标签,认证后 cd 进参数给的远端目录。
    /// SFTP 专用配置、FTP、插件协议的面板不挂 —— 那里没有 shell 可开,右键里也就没有这一项。
    /// </summary>
    public Func<string, Task>? OpenTerminalAt
    {
        get;
        set
        {
            field = value;
            this.RaisePropertyChanged(nameof(CanOpenTerminalHere));
        }
    }

    /// <summary>右键菜单里的「在此处打开终端」是否出现。</summary>
    public bool CanOpenTerminalHere => OpenTerminalAt is not null;

    /// <summary>「在此处打开终端」;参数是目录(目录行的右键菜单传它自己),null = 当前目录(空白处右键)。</summary>
    public ReactiveCommand<string?, RxVoid> OpenTerminalHereCommand { get; }

    /// <summary>
    /// 定位到一个远端条目之后触发(参数是已选中的那一行),视图据此把它滚进可见区。
    /// </summary>
    public event EventHandler<RemoteFileInfoViewModel>? RevealRequested;

    /// <summary>
    /// 在面板里定位到一个远端条目(搜索结果被点中时):目录就进去;文件就进它所在的目录并选中它。
    /// </summary>
    /// <remarks>
    /// 文件是点文件而面板隐藏着点文件时,只在<b>这个面板</b>上打开「显示隐藏文件」(不写回设置):
    /// 用户点的就是它,定位过去却看不见,等于没定位;而为一次定位改掉全局设置又太越权。
    /// 进不去那个目录(权限、刚被删)时面板照常报错,不选中任何东西。
    /// </remarks>
    /// <param name="path">远端绝对路径。</param>
    /// <param name="isDirectory">是不是目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>选中了那一行时为 true;目录、或者定位不到那一行时为 false。</returns>
    public async Task<bool> RevealAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (isDirectory)
        {
            await NavigateToAsync(path, ct);
            return false;
        }
        string parent = RemotePath.Parent(path);
        await NavigateToAsync(parent, ct);
        if (!string.Equals(CurrentPath, parent, StringComparison.Ordinal))
        {
            return false;
        }
        if (!ShowHiddenFiles && _allFiles.Exists(f => string.Equals(f.FullPath, path, StringComparison.Ordinal)
                                                      && !Files.Contains(f)))
        {
            ShowHiddenFiles = true;
        }
        // 就在当前目录里时不会换目录,过滤条件还在:它挡着那一行的话同样等于没定位。
        if (_filterText.Length > 0 && _allFiles.Exists(f => string.Equals(f.FullPath, path, StringComparison.Ordinal)
                                                            && !Files.Contains(f)))
        {
            FilterText = string.Empty;
        }
        if (Files.FirstOrDefault(f => !f.IsParentEntry && string.Equals(f.FullPath, path, StringComparison.Ordinal)) is not { } row)
        {
            return false;
        }
        SelectedFiles.Clear();
        SelectedFiles.Add(row);
        RevealRequested?.Invoke(this, row);
        return true;
    }

    private string? _pendingTerminalPath;

    /// <summary>
    /// 「跟随终端目录」(map-pin 按钮):开启时,本会话终端 shell 的 cwd 变化(经 OSC 7)会自动把文件浏览器
    /// 切到该目录。开启当下立即同步到终端当前目录;关闭则不同步。手动切换目录不影响开关——终端下次 cd 到
    /// 新目录时再同步到最新。依赖 shell 发出上报序列;SSH 会话会按对端 shell 的种类自动注入对应的
    /// 钩子(bash / zsh / fish / POSIX sh 各一段,见 <c>ShellIntegrationScript</c>),
    /// 用户自己在 rc 里配的 OSC 7 / 633 / 1337 / 9;9 同样认。
    /// </summary>
    public bool FollowTerminal
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            if (value && _pendingTerminalPath is { Length: > 0 } path)
            {
                SyncToTerminalPath(path); // 开启当下立即同步到终端当前目录
                return;
            }

            // 一次上报都没收到过就打开开关 = 点了之后什么都不会发生。必须说清为什么:
            // Windows 远端(cmd.exe/PowerShell)压根不上报 —— 钩子只对认得出种类的 POSIX shell
            // 注入(#305),而受限 sshd 上探测不出来时也不注入。判据用"有没有真收到过上报序列"
            // 而不是探测结论:用户自己在 rc 里配了 OSC 7 / 633 / 1337 / 9;9 的情形照样算数,不能误伤。
            if (value)
            {
                ErrorMessage = Strings.Get("Sftp_FollowTerminalNoReport");
                _followHintShown = true;
            }
            else if (_followHintShown)
            {
                ClearFollowHint();
            }
        }
    }

    /// <summary>上面那句提示当前是否挂在 <see cref="ErrorMessage" /> 上(只由本类挂、本类摘)。</summary>
    private bool _followHintShown;

    /// <summary>摘掉提示,但不碰别人写进去的错误(导航失败等)。</summary>
    private void ClearFollowHint()
    {
        if (_followHintShown)
        {
            _followHintShown = false;
            if (string.Equals(ErrorMessage, Strings.Get("Sftp_FollowTerminalNoReport"), StringComparison.Ordinal))
            {
                ErrorMessage = null;
            }
        }
    }

    /// <summary>本会话终端 cwd 变化时由宿主调用(仅在终端 cd 到新目录时,已在 TerminalTabViewModel 去重)。
    /// 记住最新终端目录(供开启开关时立即同步);若正在跟随则立刻切换。</summary>
    public void OnTerminalWorkingDirectoryChanged(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        _pendingTerminalPath = path;

        // 上报来了 = 提示的前提不再成立,自己摘掉,不劳用户去点。
        // 本方法在终端 feed 线程上被调用(见下面 SyncToTerminalPath 的注释),
        // ErrorMessage 是绑定到界面的属性,必须回 UI 线程改。
        if (_followHintShown)
        {
            Dispatcher.UIThread.Post(ClearFollowHint);
        }
        if (FollowTerminal)
        {
            SyncToTerminalPath(path);
        }
    }

    private void SyncToTerminalPath(string path)
    {
        // OSC 7 事件源自终端 feed 线程;导航须在 UI 线程。目录相同则不折腾。
        Dispatcher.UIThread.Post(() =>
        {
            if (_sessionId != Guid.Empty && !string.Equals(path, CurrentPath, StringComparison.Ordinal))
            {
                NavigateToCommand.Execute(path).Subscribe(_ => { }, _ => { });
            }
        });
    }

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

    /// <summary>当前路径的可点击面包屑分段,最深的在最后(§6 头部)。</summary>
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

    // ---- 手动输入路径(#226)-------------------------------------------------
    //
    // 面包屑只能沿着「已经列得出来的目录」往下点。可一旦某一层没有读权限(Termux 上的
    // /、受限跳板机上的 /home 等),那条链在半路就断了 —— 目标目录明明可读,却没有任何
    // 路径能点到它。因此路径栏必须能直接敲:面包屑末尾的铅笔按钮(或 Ctrl+L)切成输入框,
    // 回车导航,Esc / 失焦取消。

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

    /// <summary>切入手动输入态(点击路径区 / Ctrl+L)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BeginPathEditCommand { get; }

    /// <summary>放弃手动输入,退回面包屑(Esc / 失焦)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelPathEditCommand { get; }

    /// <summary>提交手动输入的路径并导航(回车)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CommitPathEditCommand { get; }

    /// <summary>进入手动输入态时触发,供视图聚焦输入框并全选文本。</summary>
    public event EventHandler? PathEditActivated;

    /// <summary>
    /// 远端家目录缓存,用来展开输入里的 <c>~</c>。首次需要时才去问服务端,
    /// 问不到就保持 null —— 此时 <c>~</c> 原样透传,由服务端报出真实错误。
    /// </summary>
    private string? _homePath;

    private void BeginPathEdit()
    {
        // 未绑定会话的占位面板没有可导航的目标,别把用户丢进一个提交不了的输入框。
        if (IsPathEditing || _sftpService is null || _sessionId == Guid.Empty)
        {
            return;
        }
        PathEditText = CurrentPath;
        IsPathEditing = true;
        PathEditActivated?.Invoke(this, EventArgs.Empty);

        // 家目录尽力而为地预取:等用户敲完再问会在回车那一刻多一次往返。
        _ = EnsureHomePathAsync();
    }

    private void CancelPathEdit() => IsPathEditing = false;

    private async Task CommitPathEditAsync()
    {
        if (!IsPathEditing)
        {
            return;
        }
        if (PathEditText.Contains('~', StringComparison.Ordinal))
        {
            await EnsureHomePathAsync();
        }
        // ~用户名 本地展开不了:请服务端展开(SFTP 的 expand-path / home-directory);展开不了照旧原样透传。
        string? target = RemotePathInput.UserHomeReference(PathEditText) is { } reference
            && await TryExpandOnServerAsync(reference) is { } expanded
                ? expanded
                : RemotePathInput.Normalize(PathEditText, CurrentPath, _homePath);
        if (target is null)
        {
            IsPathEditing = false;
            return;
        }

        // 输错路径是手动输入的常态。导航失败(ErrorMessage 非空)时保持输入态不收、
        // 也不动已输入的文本,让用户就地改一个字母重按回车;成功才退回面包屑。
        await NavigateToAsync(target);
        if (string.IsNullOrEmpty(ErrorMessage))
        {
            IsPathEditing = false;
        }
    }

    /// <summary>
    /// 展开 <c>~</c> 开头的远程路径(<c>~</c>、<c>~/sub</c>、<c>~用户名/sub</c>),口径同路径栏;展开不了返回 null。
    /// 给目录收藏用:收藏要存下来,不能像路径栏那样把 <c>~</c> 原样透传给服务端去报错。
    /// </summary>
    /// <param name="path">以 <c>~</c> 开头的路径。</param>
    internal async Task<string?> ExpandHomeAsync(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            await EnsureHomePathAsync();
            return _homePath is not { } home
                ? null
                : path.Length <= 2 ? home : home.TrimEnd('/') + "/" + path[2..];
        }
        // ~用户名 本地展开不了(要读远端 passwd),请服务端展开。
        return await TryExpandOnServerAsync(path);
    }

    private async Task<string?> TryExpandOnServerAsync(string path)
    {
        if (_sftpService is null || _sessionId == Guid.Empty)
        {
            return null;
        }
        try
        {
            return await _sftpService.ExpandPathAsync(_sessionId, path, _lifetime.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task EnsureHomePathAsync()
    {
        if (_homePath is not null || _sftpService is null || _sessionId == Guid.Empty)
        {
            return;
        }
        try
        {
            string working = await _sftpService.GetWorkingDirectoryAsync(_sessionId, _lifetime.Token);
            if (!string.IsNullOrWhiteSpace(working))
            {
                _homePath = working;
            }
        }
        catch
        {
            // 拿不到家目录不影响绝对路径输入,静默降级(~ 原样透传)。
        }
    }

    /// <summary>是否正在执行需要阻塞面板的操作,当前仅用于删除进度。</summary>
    public bool IsLoading
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>在忙碌遮罩上显示的文案(加载目录,或删除进度)。</summary>
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

    /// <summary>是否启用行拖拽发起。终端面板中关闭;SFTP 双栏中开启。</summary>
    public bool IsDragEnabled { get; set; }

    /// <summary>
    /// 首次加载优先尝试的目录(连接配置里的「默认打开路径」);null = 不指定,照旧从登录工作目录起步。
    /// <para>
    /// 它只是候选路径里排第一的那个,**不是硬性要求**:进不去就依次回退到登录工作目录与根目录,
    /// 与家目录进不去时回退根目录是同一条纪律。配错一个路径不该把用户堵在一张报错的空白页上。
    /// </para>
    /// <para>只在 <see cref="LoadInitialAsync" /> 那一次生效;之后用户怎么导航就是怎么导航。</para>
    /// </summary>
    public string? InitialRemotePath { get; set; }

    /// <summary>需要展示给用户的错误提示;为 null 表示无错误。</summary>
    public string? ErrorMessage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 当前目录所在分区的剩余空间(「可用 12 GB / 共 50 GB」);查不到(服务端不支持 statvfs、FTP、插件协议)时为 null,表头不显示。
    /// </summary>
    public string? FreeSpaceText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 由视图设置:上传前预检发现目标分区可能放不下时问用户(参数 = 提示消息) → true 表示仍要上传。没设时不拦。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmLowSpace { get; set; }

    /// <summary>
    /// 工具栏切换“显示隐藏文件”后的回调(宿主用它把新值写回 Transfer.ShowHiddenFiles);
    /// 仅由用户点击工具栏触发,宿主程序化赋值 <see cref="ShowHiddenFiles" /> 不触发。
    /// </summary>
    public Action<bool>? ShowHiddenFilesToggled { get; set; }

    /// <summary>是否列出点文件。按 §6(隐藏文件开关)默认关闭。</summary>
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

    // ---- 当前目录过滤(头部工具条上可收起的过滤框)--------------------------------
    //
    // 只过滤显示:按「名字含有」(不分大小写)把不相干的行藏起来,不去服务器上找 —— 那是远端搜索的事。
    // 藏起来的行不在 Files 里,于是全选、键入定位、下载、删除都只对着看得见的行;
    // 换目录时清空(收藏、跟随终端、搜索定位进来的也算),免得进了新目录还对着上一个目录的筛选条件。

    private string _filterText = string.Empty;

    /// <summary>过滤框是否展开。收起即清空过滤 —— 收起来的框还在暗中藏行,只会让人以为文件丢了。</summary>
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

    /// <summary>过滤框里的文字;空 = 不过滤。</summary>
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
            HashSet<string> selected = [.. SelectedFiles.Where(file => !file.IsParentEntry).Select(file => file.FullPath)];
            RebuildVisibleFiles();
            RestoreSelection(selected);
        }
    }

    /// <summary>换了目录:过滤条件作废,框照旧开着,等着输新的。只清字、不重建,调用方接着会重建可见行。</summary>
    private void ClearFilterForNewDirectory()
    {
        if (_filterText.Length > 0)
        {
            this.RaiseAndSetIfChanged(ref _filterText, string.Empty, nameof(FilterText));
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

    /// <summary>
    /// 按设置铺「显示隐藏文件」与六个列开关(宿主建面板、设置保存后广播时用)。
    /// 列开关的赋值照样会回调 <see cref="ColumnVisibilityToggled" />,宿主那边值没变就不落盘,所以不会来回写。
    /// </summary>
    /// <param name="transfer">设置 → 文件传输。</param>
    public void ApplyDisplayOptions(TransferOptions transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ShowHiddenFiles = transfer.ShowHiddenFiles;
        ShowSizeColumn = transfer.ShowSizeColumn;
        ShowPermissionsColumn = transfer.ShowPermissionsColumn;
        ShowOwnerColumn = transfer.ShowOwnerColumn;
        ShowGroupColumn = transfer.ShowGroupColumn;
        ShowTypeColumn = transfer.ShowTypeColumn;
        ShowModifiedColumn = transfer.ShowModifiedColumn;
    }

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

    /// <summary>加载遮罩是否应显示删除进度条。</summary>
    public bool IsDeleteProgressVisible
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>用于遮罩进度条的删除进度百分比 [0,100]。</summary>
    public double DeleteProgressPercent
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>为 true 时,删除进度显示为不确定态(例如总数未知之前)。</summary>
    public bool IsDeleteProgressIndeterminate
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>导航到指定绝对路径的目录(面包屑点击等)。</summary>
    public ReactiveCommand<string, RxVoid> NavigateToCommand { get; }

    /// <summary>
    /// 行激活(双击 / Enter):进入目录,或将文件下载到临时文件夹并用
    /// 操作系统默认程序打开(§6)。
    /// </summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> ActivateCommand { get; }

    /// <summary>返回上一级目录(已在根目录时无操作)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> GoUpCommand { get; }

    /// <summary>重新列举当前目录。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>加载账户的主目录(规范:落在 ~,而非文件系统根)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadInitialCommand { get; }

    /// <summary>
    /// 上传到当前目录(工具栏 + 右键)。文件与文件夹走同一个入口:选择器可以混选,
    /// 文件夹按整棵目录递归上传,文件直接传,由 <see cref="UploadLocalPathsAsync" /> 分派。
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> UploadCommand { get; }

    // 右键上下文菜单动作(规范:文件操作置于 SFTP 上下文菜单中)。
    /// <summary>在当前目录下新建文件夹(提示输入名称)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewFolderCommand { get; }

    /// <summary>在当前目录下新建空文件(提示输入名称)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewFileCommand { get; }

    /// <summary>在当前目录下新建符号链接(依次提示目标与名称;参数为右键所在行,用来预填目标)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel?, RxVoid> NewSymbolicLinkCommand { get; }

    /// <summary>
    /// 下载单个文件或目录到本地(目录递归)。文件走系统「另存为」对话框(右键「下载为…」),
    /// 对话框里的覆盖确认就是冲突决定;目录选一个本地文件夹。
    /// </summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> DownloadItemCommand { get; }

    /// <summary>在同目录内重命名选中的条目(提示输入新名称)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> RenameCommand { get; }

    /// <summary>把选中条目移动到输入的目标路径。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> MoveCommand { get; }

    /// <summary>把选中条目复制到另一个远程目录。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> CopyToCommand { get; }

    /// <summary>把选中条目的完整远程路径复制到剪贴板。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> CopyPathCommand { get; }

    /// <summary>把选中条目的名称复制到剪贴板。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> CopyNameCommand { get; }

    /// <summary>
    /// 把<b>当前所在目录</b>的完整远程路径复制到剪贴板(空白区右键)。
    /// 与 <see cref="CopyPathCommand" /> 的区别是它不需要选中任何条目 ——
    /// 否则想拿当前目录的路径就得先退回上级、再右键那个文件夹。
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyCurrentPathCommand { get; }

    /// <summary>
    /// 执行一条协议专属动作(插件协议贡献的右键菜单项,如 S3 的「复制分享链接」)。
    /// 参数携带动作与目标条目 —— 菜单项是数据驱动的,宿主不认识任何具体协议。
    /// </summary>
    public ReactiveCommand<ProtocolActionViewModel, RxVoid> InvokeProtocolActionCommand { get; }

    /// <summary>属性弹窗(合并了 chmod 权限与 chown 属主/属组编辑,确定时应用变更)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> PropertiesCommand { get; }

    /// <summary>删除选中的单个文件或目录(先弹确认)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> DeleteItemCommand { get; }

    /// <summary>「打开」:下载到临时副本后交给内置 AvaloniaEdit 编辑器(保存即上传)。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> OpenItemCommand { get; }

    /// <summary>「使用默认编辑器打开」:下载到 temp 交给设置里配置的编辑器,保存即上传。</summary>
    public ReactiveCommand<RemoteFileInfoViewModel, RxVoid> OpenWithDefaultEditorCommand { get; }

    /// <summary>批量下载所有选中的条目的本地文件夹(§6 多选)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DownloadSelectedCommand { get; }

    /// <summary>一次确认后批量删除所有选中的条目(§6 多选)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSelectedCommand { get; }

    /// <summary>取消进行中的删除,已完成的条目保留不移除。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelDeleteCommand { get; }

    /// <summary>
    /// 重新打开传输浮窗,以便回顾历史/活动传输记录(上传按钮旁的工具栏按钮)。
    /// 没有它浮窗自动隐藏后就再也回不到传输历史了。
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowTransfersCommand { get; }

    /// <summary>切换文件浏览面板的显示/隐藏。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleVisibilityCommand { get; }

    /// <summary>切换点文件可见性(§6 头部开关)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleHiddenFilesCommand { get; }

    /// <summary>
    /// 按列键排序("name" | "size" | "permissions" | "modified");再次点击当前排序列则翻转方向。
    /// </summary>
    public ReactiveCommand<string, RxVoid> SortCommand { get; }

    /// <summary>列表当前按哪一列排序。</summary>
    public string SortColumn
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "name";

    /// <summary>当前是否为降序排序。</summary>
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

    /// <summary>
    /// 由视图设置:打开本地路径选择器,返回用户选中的文件与文件夹(可混选、可多选)。
    /// <para>
    /// 用的是应用内的选择器而不是系统对话框:系统对话框在 Windows/Linux 上只能"要么选文件、
    /// 要么选文件夹"(Avalonia 的 IStorageProvider 也只有 OpenFilePicker / OpenFolderPicker
    /// 两个入口),这正是上传以前被迫拆成两个菜单项的原因。
    /// </para>
    /// </summary>
    public Func<Task<IReadOnlyList<string>>>? PickLocalPathsForUpload { get; set; }

    /// <summary>
    /// 由视图设置:询问下载保存位置(参数 = 建议的文件名)。选了已有的文件时,由对话框自己问「要替换吗」——
    /// 返回的路径即视为用户已同意覆盖(「下载为…」不再走「文件已存在时」策略)。
    /// </summary>
    public Func<string, Task<string?>>? PickSavePathForDownload { get; set; }

    /// <summary>由视图设置:为文件夹/批量下载选取本地目标文件夹。</summary>
    public Func<Task<string?>>? PickFolderForDownload { get; set; }

    /// <summary>
    /// 由视图设置:提示输入一行文本(标题, 初始值) → 输入的文本,取消则返回 null。
    /// 用于新文件夹 / 新文件 / 重命名 / 移动。
    /// </summary>
    public Func<string, string, Task<string?>>? PromptForText { get; set; }

    /// <summary>由视图设置:将文本写入系统剪贴板(复制路径 / 复制名称)。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>
    /// 当前会话可用的协议专属右键动作(来自插件协议的声明);非插件协议下为空,
    /// 对应的菜单项随之隐藏。
    /// </summary>
    public ObservableCollection<ProtocolActionViewModel> ProtocolActions { get; } = [];

    /// <summary>协议动作菜单的分组名(即协议显示名);无动作时菜单项不显示。</summary>
    public string ProtocolActionsHeader
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasProtocolActions));
        }
    } = string.Empty;

    /// <summary>是否有协议专属动作可显示。</summary>
    public bool HasProtocolActions => ProtocolActions.Count > 0;

    /// <summary>
    /// 由宿主设置:执行一条协议动作(动作 id + 目标路径)。为 null 表示当前会话不是插件协议。
    /// </summary>
    public Func<string, string, Task>? InvokeProtocolAction { get; set; }

    /// <summary>
    /// 挂上某个协议的动作集合。宿主在打开插件协议文档时调用一次 ——
    /// 动作是**声明式**的(随协议描述一起给出),因此右键菜单在按下那一帧就能画出来,
    /// 不必为了建菜单先去问一次插件。
    /// </summary>
    /// <param name="protocolName">协议显示名(作为菜单分组标题)。</param>
    /// <param name="actions">动作列表。</param>
    public void SetProtocolActions(string protocolName, IEnumerable<ProtocolAction> actions)
    {
        _declaredActions = [.. actions];
        ProtocolActionsHeader = protocolName;
        RefreshProtocolActions(target: null);
    }

    /// <summary>
    /// 右键命中的那一行。**协议动作必须作用在它身上**:插件把「复制分享链接」「对象检视」
    /// 声明为 <c>ProtocolActionScope.File</c>,若一律拿当前目录去调,预签名 URL 与检视器
    /// 就永远打在错误的目标上。由视图在右键时设置。
    /// </summary>
    public RemoteFileInfoViewModel? ContextTarget
    {
        get;
        set
        {
            field = value;
            // 菜单按命中行重建:不适用的动作直接不进菜单,而不是灰着 ——
            // 灰掉的菜单项只会让人反复去点它。
            RefreshProtocolActions(value);
        }
    }

    /// <summary>按命中行重建协议动作菜单。</summary>
    private void RefreshProtocolActions(RemoteFileInfoViewModel? target)
    {
        ProtocolActions.Clear();
        foreach (ProtocolAction action in _declaredActions)
        {
            var candidate = new ProtocolActionViewModel(action, target);
            if (candidate.AppliesTo(target))
            {
                ProtocolActions.Add(candidate);
            }
        }
        this.RaisePropertyChanged(nameof(HasProtocolActions));
    }

    /// <summary>协议声明的动作原始列表(菜单每次右键按命中行从它重建)。</summary>
    private IReadOnlyList<ProtocolAction> _declaredActions = [];

    /// <summary>
    /// 由视图设置:展示合并的属性 + 权限 + 属主弹窗(参考 WinSCP:属性、权限矩阵与属主/属组在同一弹窗)。
    /// 第二个参数是可选的用户名与组名;为 null 表示后端改不了属主,弹窗里属主/属组只读。
    /// 返回改了的项,取消时返回 null。
    /// </summary>
    public Func<RemoteFileInfoViewModel, RemoteOwnerChoices?, Task<FilePropertiesChange?>>? ShowFileProperties { get; set; }

    /// <summary>
    /// 由视图设置:要求用户确认危险操作(参数 = 提示消息) → true 表示继续。在删除前使用。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>由视图设置:用系统默认程序打开本地文件。</summary>
    public Func<string, Task>? OpenLocalFile { get; set; }

    /// <summary>
    /// 由视图设置:打开内置 AvaloniaEdit 编辑器窗口。
    /// (文件, 本地临时路径, 上传回调) —— 编辑器在每次保存后调用此回调。
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

    /// <summary>此处发起的上传/下载所驱动的浮动传输提示窗(设计 §9)。</summary>
    public FileTransferViewModel? TransferSink { get; set; }

    /// <summary>设置 → 文件传输 的选项快照(宿主在绑定与设置保存时刷新)。</summary>
    public TransferOptions TransferOptions { get; set; } = new();

    /// <summary>
    /// Set by the view: 下载遇到本地同名文件且策略为“询问”时的覆盖确认
    /// (arg = 本地路径;返回覆盖/跳过/全部覆盖/全部跳过,见 <see cref="FileConflictResolution" />)。
    /// </summary>
    public Func<string, Task<FileConflictResolution>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Set by the view: 上传遇到远端同名文件且策略为“询问”时的覆盖确认
    /// (arg = 远端路径;返回覆盖/跳过/全部覆盖/全部跳过,见 <see cref="FileConflictResolution" />)。
    /// </summary>
    public Func<string, Task<FileConflictResolution>>? ConfirmRemoteOverwrite { get; set; }

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
        // 仅支持像素值的用户可调列宽。
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

    /// <summary>导航到指定绝对路径的目录(面包屑点击等)。失败时保留原目录。进了别的目录就记进后退历史。</summary>
    public Task NavigateToAsync(string path, CancellationToken ct = default) =>
        NavigateCoreAsync(path, recordHistory: true, ct);

    /// <param name="path">目标目录。</param>
    /// <param name="recordHistory">进了别的目录时要不要把原目录记进后退历史;后退 / 前进自己挪历史,传 false。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task NavigateCoreAsync(string path, bool recordHistory, CancellationToken ct)
    {
        using LifetimeScope lifetime = LinkToLifetime(ct);
        long navigationVersion = Interlocked.Increment(ref _navigationVersion);

        // 上一次列举没用了就让它尽快退出,而不是任由它跑完还占着通道。
        // 只 Cancel、不 Dispose —— 拥有它的那次调用在自己的 finally 里释放。
        var navigation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _navigationCts, navigation);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 上一次已经收尾完毕。
        }
        ct = navigation.Token;

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
            _ = RefreshFreeSpaceAsync(path, navigationVersion);
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
            string previousPath = CurrentPath;
            CurrentPath = path;
            if (pathChanged)
            {
                SelectedFiles.Clear();
                ClearFilterForNewDirectory();
                // 首次加载之前的 CurrentPath 只是占位(构造时的 "/"),用户没在那儿待过,不进历史。
                if (recordHistory && HasLoaded)
                {
                    PushHistory(previousPath);
                }
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
            // 被下一次导航取代,或列举中途被拆除(标签关闭 / 面板重绑定)——
            // 已无人关注这次结果,故不报错。
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
            Interlocked.CompareExchange(ref _navigationCts, null, navigation);
            navigation.Dispose();
        }
    }

    /// <summary>
    /// 打开配置里的「默认打开路径」(<see cref="InitialRemotePath" />),没配则打开登录账户的家目录
    /// (登录后的工作目录,如 pi → /home/pi、root → /root)。
    /// 目录在服务器上不存在或不可访问(路径配错、realpath(".") 返回的目录未创建/被 chroot)时,
    /// 依次回退到家目录与根目录 "/",避免停在报错的空白页。
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
        using LifetimeScope lifetime = LinkToLifetime(ct);
        ct = lifetime.Token;
        var candidates = new List<string>();
        // 配置里的「默认打开路径」排第一;它进不去时下面两个候选照旧兜底。
        if (!string.IsNullOrWhiteSpace(InitialRemotePath))
        {
            candidates.Add(InitialRemotePath);
        }
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

    /// <summary>设置或翻转排序,然后原地重排当前已加载的行。</summary>
    private void ToggleSort(string column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return;
        }
        (SortColumn, SortDescending) = RemoteFileSort.NextSortState(column, SortColumn, SortDescending);
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
    /// 从原始列举重建可见行:隐藏文件过滤、当前排序,以及在根目录之外的一个在最上方、
    /// 导航回父目录的".."行(§6)。
    /// </summary>
    private void RebuildVisibleFiles()
    {
        IEnumerable<RemoteFileInfoViewModel> visible =
            RemoteFileSort.ApplyHiddenFilter(_allFiles, ShowHiddenFiles);
        if (_filterText.Length > 0)
        {
            visible = visible.Where(file => file.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase));
        }
        var rebuilt = new List<RemoteFileInfoViewModel>();
        if (CurrentPath != "/")
        {
            rebuilt.Add(RemoteFileInfoViewModel.CreateParentEntry(RemotePath.Parent(CurrentPath)));
        }
        foreach (RemoteFileInfoViewModel file in RemoteFileSort.Sort(visible, SortColumn, SortDescending))
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

        // 双击要做什么由设置决定(设置 → 文件传输 → 双击文件时)。默认仍是"系统默认程序"
        // ——原有的肌肉记忆不动,变的只是它现在也会回传(#396)。
        switch (TransferOptions.DoubleClickAction)
        {
            case "builtin":
                await OpenItemAsync(file, ct);
                return;
            case "editor":
                await OpenWithDefaultEditorAsync(file, ct);
                return;
            default:
                await DownloadAndOpenAsync(file, ct);
                return;
        }
    }

    /// <summary>
    /// §6:双击文件将其下载到独占的临时子目录(进度显示在传输浮窗中),用系统默认程序打开,
    /// <b>并侦听本地保存自动回传</b>。
    /// </summary>
    /// <remarks>
    /// 这条路径此前只下载 + 打开,全程没有任何监视:用户在记事本里改完保存,远端纹丝不动,
    /// 而右键「使用默认编辑器打开」是会回传的 —— 两个入口在界面上长得一样,行为却相反,
    /// 于是有了 #396 里那句「第一次保存有效,后面再编辑保存就没有效果了」
    /// (第一次用的是右键菜单,后面用的是双击)。现在三个入口共用同一套编辑会话。
    /// </remarks>
    private async Task DownloadAndOpenAsync(RemoteFileInfoViewModel file, CancellationToken ct)
    {
        if (OpenLocalFile is null)
        {
            return;
        }
        await OpenRemoteEditSessionAsync(file, RemoteEditOpenWith.SystemDefault, editor: null, ct);
    }

    /// <summary>
    /// 编辑保存的回传统一走右上角传输浮窗(设计 9Ralg):新增一行上传任务、
    /// 流式进度、完成/失败落状态,随后浮窗按既有规则自动淡出。失败会向上抛,调用方
    /// (编辑器状态栏 / 外部编辑会话)据此提示。必须在 UI 线程调用。
    /// </summary>
    /// <summary>
    /// 从消息中心重传外部编辑草稿时用这个面板的连接(见 <see cref="RemoteEditDraftReuploader" />):查远端走它的会话,
    /// 上传与外部编辑的自动回传同一条路、进传输浮窗。面板没绑会话时为 null。
    /// </summary>
    internal IRemoteEditDraftTarget? EditDraftTarget =>
        _sftpService is null || _sessionId == Guid.Empty
            ? null
            : new RemoteFileServiceDraftTarget(_sftpService, _sessionId, UploadEditedFileAsync);

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
            // 同批量传输:先"等待中",第一个进度回调到达才算真的开跑
            // (FTP 单连接对端上,这一份保存也可能排在别人后面)。
            Status = TransferStatus.Queued,
        };
        TransferSink?.AddTransfer(task);
        TransferItemViewModel? item = TransferSink?.FindTransfer(task.Id);
        var progress = new Progress<TransferProgress>(p =>
        {
            item?.UpdateProgress(p);
            if (item?.Status == TransferStatus.Queued)
            {
                item.Status = TransferStatus.InProgress;
            }
        });
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
            // 编辑器关窗时删掉整个子目录;崩溃留下的由启动时的 BuiltInEditTempFiles.SweepStale 兜底。
            string tempDir = BuiltInEditTempFiles.NewDirectory();
            if (!LocalPathSafety.TryResolveDestination(tempDir, file.Name, out string localPath))
            {
                ErrorMessage = Strings.Get("KeySvc_InvalidName");
                return;
            }
            Directory.CreateDirectory(tempDir);
            await _sftpService.DownloadFileAsync(_sessionId, file.FullPath, localPath, null, cancellationToken: ct);
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
    /// 「使用默认编辑器打开」:交给 <see cref="RemoteEditSessionManager" />(下载 → 启动配置的
    /// 编辑器 → 侦听保存自动上传)。
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
        await OpenRemoteEditSessionAsync(file, RemoteEditOpenWith.ConfiguredEditor, editor, ct);
    }

    /// <summary>
    /// 开一个远程编辑会话:下载到独占临时目录 → 按 <paramref name="openWith" /> 打开 →
    /// 侦听保存回传。双击与「使用默认编辑器打开」共用这一条。
    /// </summary>
    /// <param name="file">远端文件。</param>
    /// <param name="openWith">本地副本由谁打开。</param>
    /// <param name="editor">配置的编辑器命令(仅 <see cref="RemoteEditOpenWith.ConfiguredEditor" /> 用)。</param>
    /// <param name="ct">取消下载。</param>
    private async Task OpenRemoteEditSessionAsync(
        RemoteFileInfoViewModel file,
        RemoteEditOpenWith openWith,
        string? editor,
        CancellationToken ct
    )
    {
        if (!LocalPathSafety.IsSafeLeafName(file.Name))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        string remotePath = file.FullPath;
        try
        {
            ErrorMessage = null;
            await RemoteEditSessionManager.OpenAsync(
                new()
                {
                    SftpService = _sftpService,
                    SessionId = _sessionId,
                    RemotePath = remotePath,
                    FileName = file.Name,
                    OpenWith = openWith,
                    EditorCommand = editor,
                    OpenLocalAsync = OpenLocalFile,
                    AutoUpload = TransferOptions.AutoUploadOnEdit,
                    // 写进副本旁边的状态标记:崩溃之后消息中心要说得出草稿是哪台机器上的哪个文件。
                    Server = QueueOrigin?.ServerName ?? (string.IsNullOrEmpty(ServerDisplayName) ? null : ServerDisplayName),
                    ProfileId = QueueOrigin?.ProfileId,
                    OnError = message => Dispatcher.UIThread.Post(() => ErrorMessage = message),

                    // 下载与回传都经传输浮窗(进度、限速、可取消)。
                    // 下载由本方法(UI 线程)直接驱动;回传来自 watcher 线程,要切回 UI 线程。
                    DownloadAsync = (local, token) =>
                        RunTransferBatchAsync([new(TransferType.Download, local, remotePath)], token),
                    UploadAsync = (local, remote) =>
                        Dispatcher.UIThread.InvokeAsync(() => UploadEditedFileAsync(local, remote)),
                },
                ct
            );
        }
        catch (OperationCanceledException)
        {
            // 用户取消了下载;不算错误。
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
        await NavigateToAsync(RemotePath.Parent(CurrentPath), ct);
    }

    // ---- 后退 / 前进(Alt+← / Alt+→ 与工具条按钮)--------------------------------
    //
    // 每个面板各有一对历史栈,只活在这个实例里:断线重连后宿主建的是新面板,历史自然不留 ——
    // 重连之后的会话可能已经换了一台机器(同名配置改过地址),旧路径未必还有意义。
    // 凡是进了别的目录都记(面包屑、双击、上一级、手动输入、收藏、跟随终端、搜索定位),刷新同一目录不记。

    /// <summary>历史栈的上限:够用,又不至于让一个开了一整天的面板无限长下去。</summary>
    internal const int MaxHistory = 50;

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

    /// <summary>进了别的目录:原目录压进后退栈,前进栈作废(与浏览器一样,从历史中间岔出去就没有「前进」了)。</summary>
    private void PushHistory(string previous)
    {
        if (_backHistory.Count == 0 || !string.Equals(_backHistory[^1], previous, StringComparison.Ordinal))
        {
            _backHistory.Add(previous);
            if (_backHistory.Count > MaxHistory)
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

    /// <summary>后退一步。目标目录进不去(刚被删、没权限)时照常报错,历史原样不动。</summary>
    public Task GoBackAsync(CancellationToken ct = default) => MoveInHistoryAsync(_backHistory, _forwardHistory, ct);

    /// <summary>前进一步;规则同 <see cref="GoBackAsync" />。</summary>
    public Task GoForwardAsync(CancellationToken ct = default) => MoveInHistoryAsync(_forwardHistory, _backHistory, ct);

    private async Task MoveInHistoryAsync(List<string> from, List<string> to, CancellationToken ct)
    {
        if (from.Count == 0)
        {
            return;
        }
        string target = from[^1];
        string origin = CurrentPath;
        await NavigateCoreAsync(target, recordHistory: false, ct);

        // 只认「确实到了那儿」:失败、或者半路被另一次导航顶掉了,历史都不挪。
        if (!string.Equals(CurrentPath, target, StringComparison.Ordinal)
            || from.Count == 0
            || !string.Equals(from[^1], target, StringComparison.Ordinal))
        {
            return;
        }
        from.RemoveAt(from.Count - 1);
        to.Add(origin);
        RaiseHistoryChanged();
    }

    private async Task RefreshAsync(CancellationToken ct = default) =>
        await NavigateToAsync(CurrentPath, ct);

    /// <summary>刷新表头的剩余空间。查不到、出错都只是不显示 —— 它是附带的信息,不该变成一条错误。</summary>
    private async Task RefreshFreeSpaceAsync(string path, long navigationVersion)
    {
        RemoteSpaceInfo? space;
        try
        {
            space = await _sftpService.GetSpaceAsync(_sessionId, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            space = null;
        }

        // 这期间又跳到了别的目录:那一次会自己刷新,这一份过时了。
        if (navigationVersion != Volatile.Read(ref _navigationVersion))
        {
            return;
        }
        FreeSpaceText = space is null
            ? null
            : Strings.Format("FileBrowser_FreeSpace", FormatBytes(space.AvailableBytes), FormatBytes(space.TotalBytes));
    }

    private static string FormatBytes(ulong bytes) => RemoteFileInfoViewModel.FormatSize((long)Math.Min(bytes, long.MaxValue));

    /// <summary>
    /// 上传前预检:这批文件放得下吗(SFTP 的 statvfs)。可能放不下时问一句,用户说不传就不传 ——
    /// 传到一半磁盘满,远端会留下半个文件。查不到用量(服务端不支持、FTP)时不拦。
    /// </summary>
    private async Task<bool> ConfirmEnoughSpaceAsync(IReadOnlyList<PlannedFileTransfer> plan, CancellationToken ct)
    {
        if (ConfirmLowSpace is null || plan.Count == 0)
        {
            return true;
        }

        long needed = 0;
        foreach (PlannedFileTransfer item in plan)
        {
            if (item.Type != TransferType.Upload || item.Source is not null)
            {
                continue;   // 只算从本机传上去的;另一栏中转过来的大小这里不知道
            }
            try
            {
                needed += Math.Max(0, new FileInfo(item.LocalPath).Length - item.ResumeOffset);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到大小的文件不计:真读不了的话上传那一步自己会报。
            }
        }

        RemoteSpaceInfo? space;
        try
        {
            space = await _sftpService.GetSpaceAsync(_sessionId, CurrentPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return true;
        }

        if (space is null || (ulong)needed <= space.AvailableBytes)
        {
            return true;
        }
        return await ConfirmLowSpace(
            Strings.Format("FileBrowser_LowSpaceMessage", FormatBytes(space.AvailableBytes), FormatBytes((ulong)needed)));
    }

    /// <summary>
    /// 上传入口:选一次,文件和文件夹一起选。选完原样交给
    /// <see cref="UploadLocalPathsAsync" /> —— 它本来就按每个路径的实际类型分派,
    /// 所以这里不需要(也不该)预先把两类分开。
    /// </summary>
    private async Task UploadAsync(CancellationToken ct = default)
    {
        if (PickLocalPathsForUpload is null)
        {
            return;
        }
        IReadOnlyList<string> picked = await PickLocalPathsForUpload();
        await UploadLocalPathsAsync(picked, ct);
    }

    /// <summary>
    /// 上传任意混合的本地文件和文件夹到当前目录,对文件夹做递归
    /// (创建对应的远程目录)。供上传菜单项与拖放共用,因此多选和拖放文件夹都汇聚到这里。
    /// </summary>
    public async Task UploadLocalPathsAsync(
        IReadOnlyList<string> localPaths,
        CancellationToken ct = default
    )
    {
        IReadOnlyList<string> roots = NormalizeUploadRoots(localPaths);
        if (roots.Count == 0)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            // 扫描大文件夹可能耗时:先让传输面板进入"准备中",徽标随发现的文件数递增。
            TransferSink?.BeginPreparing();
            var plan = new List<PlannedFileTransfer>();
            foreach (string path in roots)
            {
                await BuildUploadPlanAsync(path, CurrentPath, plan, ct);
            }
            if (!await ConfirmEnoughSpaceAsync(plan, ct))
            {
                return;
            }
            await RunTransferBatchAsync(plan, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户在上传规划/执行过程中取消了;不算错误。
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
    /// 把一批用户选中的本地路径收拾成互不重叠的上传根:去掉重复项,并丢掉那些已经被
    /// 同批次某个文件夹包住的路径。
    /// <para>
    /// 不做这一步,"文件夹 + 它里面的某个文件"一起选(拖放里很常见)会把同一个文件传两遍、
    /// 写同一个远端路径 —— 白费带宽还自己跟自己抢。比较用
    /// <see cref="StringComparison.OrdinalIgnoreCase" />:Windows 与 macOS 的默认卷都不区分大小写,
    /// 而在区分大小写的文件系统上这最多是少传一份重复,不会传错。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> NormalizeUploadRoots(IReadOnlyList<string> localPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>(localPaths.Count);
        foreach (string path in localPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(TrimTrailingSeparators(path)))
            {
                roots.Add(path);
            }
        }

        // 长的路径才可能被短的包住,按长度升序扫一遍即可:每个候选只需对已保留的根做前缀判断。
        roots.Sort(static (a, b) => TrimTrailingSeparators(a).Length - TrimTrailingSeparators(b).Length);
        var kept = new List<string>(roots.Count);
        foreach (string path in roots)
        {
            if (!kept.Any(root => IsUnder(path, root)))
            {
                kept.Add(path);
            }
        }
        return kept;
    }

    /// <summary>路径 <paramref name="path" /> 是否位于目录 <paramref name="root" /> 之下(不含自身)。</summary>
    private static bool IsUnder(string path, string root)
    {
        string prefix = TrimTrailingSeparators(root) + Path.DirectorySeparatorChar;
        return TrimTrailingSeparators(path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .StartsWith(
                prefix.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 遍历本地文件/文件夹,把每个文件的一条计划上传追加到 <paramref name="plan" /> 中,
    /// 并在过程中创建对应的远程目录。先做规划让传输浮窗获得准确的剩余文件数,
    /// 并使整批任务可被取消。
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
            string remoteSub = RemotePath.Combine(remoteDir, name);
            await _sftpService.EnsureDirectoryAsync(_sessionId, remoteSub, ct);
            foreach (string child in Directory.EnumerateFileSystemEntries(localPath))
            {
                await BuildUploadPlanAsync(child, remoteSub, plan, ct);
            }
        }
        else if (File.Exists(localPath))
        {
            string remotePath = RemotePath.Combine(remoteDir, Path.GetFileName(localPath));
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
                // 用户在下载规划/执行过程中取消了;不算错误。
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
        // 「下载为…」:落点与名字是用户在系统「另存为」对话框里挑的,同名时对话框也已经问过「要替换吗」——
        // 那一下就是这次的冲突决定。所以既不走「文件已存在时」策略(「询问」会再弹一遍,「跳过 / 重命名」
        // 还会推翻用户刚选定的名字),也不做续传探测(那里原有的同名文件是用户确认要替换掉的,不是这次的半截)。
        // 其余照常:进浮窗、占全窗口名额、可取消、记进传输队列(断线停下之后「继续」从这次的半截接着传)。
        PlannedFileTransfer[] single = [new(TransferType.Download, localPath, file.FullPath)];
        ErrorMessage = null;
        await ExecuteResolvedBatchAsync(single, new BatchConflictDecision { OverwriteAll = true }, settled: null, ct, persist: true);
    }

    /// <summary>
    /// 遍历远端文件或目录(递归),把每个文件的一条计划下载追加到 <paramref name="plan" /> 中,
    /// 将远端结构镜像到 <paramref name="localDir" /> 中(过程中创建本地目录)。先做规划让传输浮窗
    /// 显示准确的剩余文件数,并使整批任务可被取消。供文件夹下载与批量下载共用。
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
                // 目录里嵌套的「指向目录的链接」不跟进去(rsync -r 不带 -L 的口径):链接可以指回祖先
                // 形成无限展开,也可以指向 / 把整台机器拖下来。用户显式选中的那一个链接照常跟随;
                // 指向文件的链接照常下载其内容(Windows 本地建不了链接,内容才是有用的那份)。
                if (child is { IsSymbolicLink: true, IsDirectory: true })
                {
                    continue;
                }
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
    /// 将用户显式选中的远端条目下载到调用者自有本地目录中。
    /// 复用现有的规划、冲突处理、进度与递归目录逻辑。
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
            // 用户取消了批量下载;不算错误。
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

    /// <summary>
    /// 双栏远程文档里与本栏并排的另一栏;单栏文档与终端侧边栏为 null。
    /// 只有它的条目能经 <see cref="ReceiveFromPeerAsync" /> 中转进来 —— 跨会话传输是双栏远程文档的能力,
    /// 不向别处的面板开放。
    /// </summary>
    public FileBrowserViewModel? DualPeer { get; set; }

    /// <summary>
    /// 本栏能不能接收从 <see cref="DualPeer" /> 流式搬来的文件。插件协议没实现流式上传时为 false:
    /// 这一栏仍可作为源,但往它这边搬会被拒(拖放不接、按钮置灰)。
    /// </summary>
    public bool AcceptsStreamedUploads { get; init; } = true;

    /// <summary>本栏作为中转源时先完整下载并确认完成;仅内建 FTP 栏启用。</summary>
    internal bool UseStagedRelaySource { get; init; }

    /// <summary>
    /// FTP/FTPS 源优先经有界流管道写入远端临时文件;目标不支持安全提交或需要续传时回退完整缓存。
    /// </summary>
    internal bool UseStreamedRelay { get; init; }

    /// <summary>
    /// 把另一栏(<see cref="DualPeer" />)选中的条目搬进本栏当前目录:文件夹递归,冲突策略、并发上限、
    /// 进度、取消、失败重试、断点续传与普通上传完全共用;FTP 源先完整缓存,其它源保持流式中转。
    /// </summary>
    /// <param name="entries">另一栏里的条目(上级目录行会被忽略)。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task ReceiveFromPeerAsync(IReadOnlyList<RemoteFileInfoViewModel> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (DualPeer is not { } peer)
        {
            return;
        }
        if (!AcceptsStreamedUploads)
        {
            // 界面上的入口都已经关了(按钮置灰、拖放不接),走到这里是代码调用 —— 说清楚,别静默什么都不做。
            ErrorMessage = Strings.Format("DualSftp_CannotReceive", ServerDisplayName);
            return;
        }
        RemoteFileInfoViewModel[] targets = [.. entries.Where(static f => !f.IsParentEntry)];
        if (targets.Length == 0)
        {
            return;
        }
        var source = new RelaySource(peer._sftpService, peer._sessionId, peer.ServerDisplayName, peer.UseStagedRelaySource, peer.UseStreamedRelay);
        string destination = CurrentPath;
        try
        {
            ErrorMessage = null;
            TransferSink?.BeginPreparing();
            var plan = new List<PlannedFileTransfer>();
            foreach (RemoteFileInfoViewModel item in targets)
            {
                await BuildRelayPlanAsync(source, item.FullPath, item.Name, item.IsDirectory, destination, plan, ct);
            }
            await RunTransferBatchAsync(plan, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户在规划/执行过程中取消了;不算错误。
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            TransferSink?.EndPreparing();
        }
        await RefreshAsync(ct);
    }

    /// <summary>
    /// 遍历另一栏的文件或目录(递归),把每个文件的一条中转计划追加到 <paramref name="plan" />,
    /// 并在本会话上建好对应的目录。链接口径与下载一致:用户显式选中的那一个照常跟随,
    /// 目录里嵌套的「指向目录的链接」不进去(可能指回祖先形成无限展开)。
    /// </summary>
    private async Task BuildRelayPlanAsync(
        RelaySource source,
        string sourcePath,
        string name,
        bool isDirectory,
        string destinationDir,
        List<PlannedFileTransfer> plan,
        CancellationToken ct
    )
    {
        string destination = RemotePath.Combine(destinationDir, name);
        if (!isDirectory)
        {
            plan.Add(new(TransferType.Relay, sourcePath, destination, Source: source));
            TransferSink?.UpdatePreparingCount(plan.Count);
            return;
        }
        await _sftpService.EnsureDirectoryAsync(_sessionId, destination, ct);
        List<RemoteFileInfo> children = await source.Service.ListDirectoryAsync(source.SessionId, sourcePath, ct);
        foreach (RemoteFileInfo child in children)
        {
            if (child is { IsSymbolicLink: true, IsDirectory: true })
            {
                continue;
            }
            await BuildRelayPlanAsync(source, child.FullPath, child.Name, child.IsDirectory, destination, plan, ct);
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
    /// 串行执行一批已规划的传输,共享一个取消域,浮窗的"取消剩余"控件(以及
    /// 文件夹下载取消)均可以此触发。浮窗显示剩余文件数;返回值指示是否所有文件
    /// 均已完成(false 表示用户取消了)。
    /// </summary>
    /// <summary>
    /// 取一个全局并发传输名额。没有传输浮窗(单元测试、无 SFTP 的宿主)时不限流 ——
    /// 那种场景下也不存在"多个面板共享一个窗口"这回事,并发本来就由工作任务数封顶。
    /// </summary>
    private Task<IDisposable> AcquireTransferSlotAsync(int limit, CancellationToken ct) =>
        TransferSink is { } sink
            ? sink.AcquireTransferSlotAsync(limit, ct)
            : Task.FromResult<IDisposable>(UnlimitedSlot.Instance);

    /// <summary>没有浮窗时的空名额:释放它什么也不做。</summary>
    private sealed class UnlimitedSlot : IDisposable
    {
        public static readonly UnlimitedSlot Instance = new();

        public void Dispose()
        {
        }
    }

    /// <param name="plan">规划好的传输。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="resumes">
    /// 接着传的是传输队列里停下的哪一批(见 <see cref="ResumeQueuedTransfersAsync" />);新发起的为 <see cref="Guid.Empty" />。
    /// 接着传时<b>不再走同名冲突策略</b>:这些项在最初规划时已经过了那一关(覆盖、改名后的目标、或本来就不冲突),
    /// 目标上现在有东西,多半是它自己上次写下的半截或已写完的那份 —— 照「询问」再弹一轮只是让用户把同一个决定再做一遍。
    /// 半截照常走续传探测;起点核实对不上(源或目标在这期间变了)时按覆盖处理。
    /// </param>
    private async Task<bool> RunTransferBatchAsync(
        IReadOnlyList<PlannedFileTransfer> plan,
        CancellationToken ct,
        Guid resumes = default
    )
    {
        if (plan.Count == 0)
        {
            if (resumes != Guid.Empty)
            {
                TransferSink?.RetirePausedBatch(resumes);
            }
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
        // 本批次“全部覆盖/全部跳过”的粘性决定:一旦用户在某个冲突上选了“全部…”,
        // 其余冲突不再逐个弹窗(拖入上千文件时的关键)。null = 尚未决定,仍逐个询问。
        var conflictDecision = new BatchConflictDecision { OverwriteAll = resumes != Guid.Empty ? true : null };
        foreach (PlannedFileTransfer item in plan)
        {
            // 先做断点续传检查:若续传已启用且存在部分文件,从该偏移处续传。
            // 传入 remoteNames:续传探测必须复用同一份预列举名单,否则每个文件都要多打一次
            // 远端(拖入大文件夹 = 上千次无谓往返;链路/通道有问题时还会逐文件抛一次异常刷屏)。
            PlannedFileTransfer? resumed = await TryResumeAsync(item, remoteNames, ct);
            if (resumed is not null)
            {
                resolved.Add(resumed);
                continue;
            }

            // 接着传一批停下的传输:冲突在最初规划时已经定过了(见 resumes 参数的说明)。
            if (resumes != Guid.Empty)
            {
                resolved.Add(item);
                continue;
            }

            // 常规冲突处理。
            PlannedFileTransfer? settled =
                item.Type == TransferType.Download
                    ? await ResolveLocalConflictAsync(item, conflictDecision)
                    : await ResolveRemoteConflictAsync(item, remoteNames, conflictDecision, ct);
            if (settled is not null)
            {
                resolved.Add(settled);
            }
        }
        return await ExecuteResolvedBatchAsync(resolved, conflictDecision, settled: null, ct, persist: true, replaces: resumes);
    }

    /// <summary>
    /// 本面板发起的传输来自哪条连接(宿主按连接配置设置)。设了它,上传与下载批次就记进传输队列:
    /// 断线停下、或退出时还没传完的,在浮窗上挂成「已暂停」,点「继续」时按这条连接找回会话接着传。
    /// 为 null(临时占位面板、没有配置的会话)时不记,失败照旧标红。
    /// </summary>
    public TransferQueueOrigin? QueueOrigin { get; init; }

    /// <summary>
    /// 接着传一批停下来的传输(断线之后,或上次退出时还没传完的):每一项先探续传起点,有半截就从半截接着传,
    /// 没有就整份传;冲突策略不再过一遍(理由见 <see cref="RunTransferBatchAsync" /> 的 resumes 参数)。
    /// </summary>
    /// <remarks>
    /// 在这一批登记进队列的那一刻,原来那一批从队列与浮窗上撤掉(交接);之后照常是一批普通的传输 ——
    /// 进度、并发上限、自动重试、取消、再断线时再停下,都与新发起的一样。
    /// 本地或远端文件在这期间没了、没了权限,那一项按永久失败标红,其余照传。
    /// </remarks>
    /// <param name="batch">要接着传的那一批。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>false = 没能开始(原因在 <see cref="ErrorMessage" /> 里),那一批原样留在队列里。</returns>
    internal async Task<bool> ResumeQueuedTransfersAsync(PausedTransferBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        PlannedFileTransfer[] plan = [.. batch.Items.Select(static i => new PlannedFileTransfer(i.Type, i.LocalPath, i.RemotePath))];
        try
        {
            ErrorMessage = null;
            await RunTransferBatchAsync(plan, ct, batch.BatchId);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 交接之后的取消由批次自己收尾(不会抛到这里);走到这里说明还没交接,那一批原样留着。
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 执行一批已经过冲突与续传处理的传输:登记批次、按全窗口并发上限分派、收尾通知。
    /// <paramref name="settled" /> 在每一项落定(完成/失败/跳过,以及浮窗那一行的 × 单条取消)后回调,整批取消时不回调。
    /// </summary>
    /// <param name="resolved">要传的项。</param>
    /// <param name="conflictDecision">本批次的粘性冲突决定。</param>
    /// <param name="settled">每一项落定后的回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="persist">
    /// 记不记进传输队列(用户发起的上传 / 下载批次记;目录同步不记 —— 它的计划是在预览里逐条确认的,
    /// 重启之后应当重新比较)。记了的批次在连接断掉时整批停下、挂成「已暂停」,而不是剩下的文件一个接一个失败。
    /// </param>
    /// <param name="replaces">这一批接替的是队列里的哪一批(登记之后把那一批撤掉)。</param>
    private async Task<bool> ExecuteResolvedBatchAsync(
        IReadOnlyList<PlannedFileTransfer> resolved,
        BatchConflictDecision conflictDecision,
        Func<PlannedFileTransfer, TransferStatus, Task>? settled,
        CancellationToken ct,
        bool persist = false,
        Guid replaces = default
    )
    {
        // 传输队列:只记上传与下载,只在知道发起连接的面板上记(恢复时得知道去连哪台)。
        TransferQueueJournal? journal = persist && QueueOrigin is not null ? TransferSink?.Journal : null;
        Guid queueId = Guid.Empty;
        int[] queueIndex = new int[resolved.Count];
        if (journal is not null)
        {
            var queued = new List<TransferQueueItem>(resolved.Count);
            for (int i = 0; i < resolved.Count; i++)
            {
                PlannedFileTransfer item = resolved[i];
                queueIndex[i] = TransferQueueJournal.CanPersist(item.Type) && item.Source is null ? queued.Count : -1;
                if (queueIndex[i] >= 0)
                {
                    queued.Add(new(item.Type, item.LocalPath, item.RemotePath));
                }
            }
            if (queued.Count > 0)
            {
                queueId = journal.Begin(QueueOrigin!, queued);
            }
            else
            {
                journal = null;
            }
        }
        // 交接:新的一批已经登记(或者一项都不用再传了),接替的那一批从队列与浮窗上撤掉。
        // 顺序要紧 —— 先登记新的再撤旧的,中间崩掉也最多是两份都在,而不是两份都没了。
        if (replaces != Guid.Empty)
        {
            TransferSink?.RetirePausedBatch(replaces);
        }
        if (resolved.Count == 0)
        {
            return true;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 批次 id 一路带到结算与收尾:多个面板同时传输时,谁也不能替别人宣布结束
        // (见 FileTransferViewModel._batches 的说明)。
        Guid batchId = TransferSink?.BeginBatch(resolved.Count, cts) ?? Guid.Empty;
        bool completed = false;
        // 有一项因为连接没了(或重试用完)停下:剩下的不再开始 —— 它们多半也传不动,
        // 一个接一个去撞只会刷出一排红行。停下的整批等用户「继续」。
        int paused = 0;
        try
        {
            // 最大并发传输数(设置 → 文件传输):1 = 既有的顺序行为。
            // 名额是**全窗口共享**的(见 FileTransferViewModel._transferSlots):设置里写 4,
            // 无论开几个面板、拖几批,同时真正在传的就是 4 个。
            int maxConcurrent = Math.Clamp(TransferOptions.MaxConcurrentTransfers, 1, 16);
            if (maxConcurrent <= 1 || resolved.Count == 1)
            {
                for (int position = 0; position < resolved.Count && Volatile.Read(ref paused) == 0; position++)
                {
                    PlannedFileTransfer item = resolved[position];
                    // 顺序路径同样要过闸:否则"上限 1"的两个批次会各跑各的,合起来是 2。
                    TransferStatus status;
                    using (await AcquireTransferSlotAsync(maxConcurrent, cts.Token))
                    {
                        status = await RunTransferAsync(item.Type, item.LocalPath, item.RemotePath, item.ResumeOffset, cts.Token, conflictDecision, item.Source, pausable: journal is not null);
                    }
                    Account(position, status);
                    TransferSink?.NotifyBatchItemSettled(batchId);
                    if (settled is not null)
                    {
                        await settled(item, status);
                    }
                }
            }
            else
            {
                // 固定数量的工作任务消费一条队列,而不是"每个文件一个 async 状态机 +
                // 一次信号量等待"。拖入一个十万文件的目录时,后者要当场造出十万个
                // Task 与十万个等待者 —— 那笔开销全压在按下回车到第一个文件开始传之间。
                //
                // 工作任务**刻意不用 Task.Run**:RunTransferAsync 与传输面板的记账都在
                // UI 线程上做(ObservableCollection),从 UI 线程直接调用异步方法,续体才会
                // 回到 UI 线程。丢到线程池上就是跨线程改绑定集合。
                // 队列必须是线程安全的。曾经这里是普通的 Queue<T>,理由写着"只在 UI 线程上
                // 进出" —— 那个假设站不住:续体回不回 UI 线程取决于有没有同步上下文
                // (单元测试里就没有),而且任何一层 ConfigureAwait(false) 都能把它打破。
                // 两个工作任务同时 Dequeue 的后果不是抛异常那么显眼,而是**静默漏传文件**:
                // 实测在 Linux 上每批稳定少传一到两个,界面上还显示成功。
                var queue = new ConcurrentQueue<int>(Enumerable.Range(0, resolved.Count));
                int workerCount = Math.Min(maxConcurrent, resolved.Count);
                var workers = new Task[workerCount];
                for (int i = 0; i < workerCount; i++)
                {
                    workers[i] = RunWorkerAsync();
                }
                await Task.WhenAll(workers);

                async Task RunWorkerAsync()
                {
                    while (true)
                    {
                        if (cts.IsCancellationRequested
                            || Volatile.Read(ref paused) != 0
                            || !queue.TryDequeue(out int position))
                        {
                            return;
                        }
                        PlannedFileTransfer item = resolved[position];
                        // 名额在**每个文件**上取放,不是一个工作任务霸着一个:这样上限
                        // 才是"同时在传几个文件",而不是"起了几个工作任务"。
                        TransferStatus status;
                        using (await AcquireTransferSlotAsync(maxConcurrent, cts.Token))
                        {
                            status = await RunTransferAsync(
                                item.Type,
                                item.LocalPath,
                                item.RemotePath,
                                item.ResumeOffset,
                                cts.Token,
                                conflictDecision,
                                item.Source,
                                pausable: journal is not null
                            );
                        }
                        Account(position, status);
                        TransferSink?.NotifyBatchItemSettled(batchId);
                        if (settled is not null)
                        {
                            await settled(item, status);
                        }
                    }
                }
            }
            completed = Volatile.Read(ref paused) == 0;
            return completed;
        }
        catch (OperationCanceledException)
        {
            // 用户取消了:当前传输被中止,其余已跳过。剩下的也不再要了 —— 从队列里一并删掉。
            journal?.Drop(queueId);
            journal = null;
            return false;
        }
        finally
        {
            TransferSink?.EndBatch(batchId);

            // 停下的批次挂成一行「已暂停」(它在队列里的账原样留着,重启之后也还在)。
            if (journal is not null && Volatile.Read(ref paused) != 0)
            {
                TransferSink?.ShowPausedBatch(queueId, PausedBatchReason.Interrupted);
            }

            // 传输完成后显示通知(设置 → 文件传输):提示音 + 临时展开传输面板。
            // 用 ShowPanelTransient 而非 ShowPanel:后者会钉住面板、杀掉自动隐藏倒计时,
            // 导致完成后面板常驻只能手动关闭。
            if (completed && TransferOptions.NotifyOnComplete)
            {
                SystemSound.Alert();
                TransferSink?.ShowPanelTransient();
            }
        }

        // 一项的结局记进传输队列:停下的(连接没了、重试用完)留着,等「继续」;
        // 其余(传完、跳过、永久失败)从队列里拿掉 —— 永久失败换一条连接也传不过去。
        void Account(int position, TransferStatus status)
        {
            if (status == TransferStatus.Paused)
            {
                Volatile.Write(ref paused, 1);
                return;
            }
            if (journal is not null && queueIndex[position] >= 0)
            {
                journal.Settle(queueId, queueIndex[position]);
            }
        }
    }

    /// <summary>
    /// 按冲突策略处理一个计划中的下载:返回 null 表示跳过,或返回(可能改了
    /// 本地路径的)计划项。非下载或无冲突原样返回。
    /// </summary>
    /// <summary>
    /// 检查是否存在可续传的部分传输。若续传已启用且目标文件存在但小于源文件,
    /// 返回将 ResumeOffset 设为部分大小的计划项。
    /// </summary>
    private async Task<PlannedFileTransfer?> TryResumeAsync(
        PlannedFileTransfer item,
        Dictionary<string, HashSet<string>> remoteNames,
        CancellationToken ct
    )
    {
        // 上传、下载与两台远端之间的中转支持续传(同会话复制不支持)。
        if (item.Type is not (TransferType.Upload or TransferType.Download or TransferType.Relay))
        {
            return null;
        }

        // 检查设置中是否启用了续传。
        if (!TransferOptions.ResumeEnabled)
        {
            return null;
        }

        try
        {
            if (item.Type == TransferType.Download)
            {
                // 下载:检查本地文件是否已存在(部分)。
                long localSize = File.Exists(item.LocalPath) ? new FileInfo(item.LocalPath).Length : -1;
                if (localSize <= 0)
                {
                    return null;
                }
                RemoteFileInfo remoteInfo = await _sftpService.GetFileInfoAsync(
                    _sessionId,
                    item.RemotePath,
                    ct
                );
                if (localSize < remoteInfo.Size)
                {
                    return item with { ResumeOffset = localSize };
                }
            }
            else
            {
                // 上传 / 中转:检查目标文件是否已存在(部分)。
                long remoteSize = await GetRemoteFileSizeAsync(item.RemotePath, remoteNames, ct);
                if (remoteSize <= 0)
                {
                    return null;
                }
                // 源的长度:上传看本地文件,中转看另一栏那台机器上的文件。
                long sourceSize = item.Source is { } source
                    ? (await source.Service.GetFileInfoAsync(source.SessionId, item.LocalPath, ct)).Size
                    : new FileInfo(item.LocalPath).Length;
                if (remoteSize < sourceSize)
                {
                    return item with { ResumeOffset = remoteSize };
                }
            }
        }
        catch
        {
            // 任意检查失败时退回到常规冲突处理。
        }

        return null;
    }

    /// <summary>获取远端文件大小;不存在返回 -1。</summary>
    private async Task<long> GetRemoteFileSizeAsync(
        string remotePath,
        Dictionary<string, HashSet<string>> remoteNames,
        CancellationToken ct
    )
    {
        try
        {
            // 上传目标通常尚不存在:先判存在性,不存在直接返回。
            // 走 RemoteExistsAsync 而非裸 ExistsAsync —— 前者优先查本批预列举的目录名单,
            // 命中即零网络往返;拖入一个大文件夹时这是"每个文件一次往返"与"每个目录一次列举"
            // 的差别,链路异常时也不会逐文件抛异常刷屏。
            // 直接 GetFileInfoAsync 则会为每个新文件抛一次 FileNotFoundException(异常做控制流)。
            if (!await RemoteExistsAsync(remotePath, remoteNames, ct))
            {
                return -1;
            }
            RemoteFileInfo info = await _sftpService.GetFileInfoAsync(_sessionId, remotePath, ct);
            return info.Size;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private async Task<PlannedFileTransfer?> ResolveLocalConflictAsync(
        PlannedFileTransfer item,
        BatchConflictDecision decision
    )
    {
        if (item.Type != TransferType.Download || !File.Exists(item.LocalPath))
        {
            return item;
        }
        return TransferOptions.ConflictPolicy switch
        {
            "overwrite" => item,
            "skip" => null,
            "rename" => item with { LocalPath = NextAvailableLocalName(item.LocalPath) },
            // ask
            _ => await AskConflictAsync(item, item.LocalPath, ConfirmOverwrite, decision),
        };
    }

    /// <summary>本批次“全部覆盖/全部跳过”的粘性决定(见 <see cref="FileConflictResolution" />)。</summary>
    internal sealed class BatchConflictDecision
    {
        /// <summary>true = 全部覆盖,false = 全部跳过,null = 尚未决定(仍逐个询问)。</summary>
        public bool? OverwriteAll { get; set; }
    }

    /// <summary>
    /// “询问”策略下决定单个冲突:覆盖则返回原计划项,跳过则返回 null。粘性决定的判定与
    /// 记录见 <see cref="DecideConflictAsync" />。
    /// </summary>
    private static async Task<PlannedFileTransfer?> AskConflictAsync(
        PlannedFileTransfer item,
        string displayPath,
        Func<string, Task<FileConflictResolution>>? confirm,
        BatchConflictDecision decision
    ) => await DecideConflictAsync(displayPath, confirm, decision) ? item : null;

    /// <summary>
    /// 决定单个冲突是否覆盖(true)还是跳过(false)。已有本批次粘性决定则直接沿用、
    /// 不再弹窗(拖入上千文件时的关键);否则调 <paramref name="confirm" /> 弹窗,并在用户选
    /// “全部覆盖/全部跳过”时把决定记入 <paramref name="decision" />,沿用到本批次其余冲突。
    /// 无 UI 回调(<paramref name="confirm" /> 为 null)时保持既有行为:默认覆盖。
    /// </summary>
    internal static async Task<bool> DecideConflictAsync(
        string displayPath,
        Func<string, Task<FileConflictResolution>>? confirm,
        BatchConflictDecision decision
    )
    {
        if (decision.OverwriteAll is { } sticky)
        {
            return sticky;
        }
        if (confirm is null)
        {
            return true;
        }
        switch (await confirm(displayPath))
        {
            case FileConflictResolution.OverwriteAll:
                decision.OverwriteAll = true;
                return true;
            case FileConflictResolution.SkipAll:
                decision.OverwriteAll = false;
                return false;
            case FileConflictResolution.Overwrite:
                return true;
            default: // Skip
                return false;
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

        // “覆盖”策略本身不需要名单(直接沿用 SFTP 覆盖语义,省掉列举);
        // 但断点续传开启时仍要判断远端是否已有半截文件,没有名单就会退化成逐文件 ExistsAsync。
        // 所以只有“覆盖 且 不续传”才真的可以跳过列举。
        if (TransferOptions.ConflictPolicy == "overwrite" && !TransferOptions.ResumeEnabled)
        {
            return map;
        }
        foreach (
            string dir in plan.Where(p => WritesRemote(p.Type))
                .Select(p => RemotePath.Parent(p.RemotePath))
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
        if (remoteNames.TryGetValue(RemotePath.Parent(remotePath), out HashSet<string>? names))
        {
            return names.Contains(NameOf(remotePath));
        }
        return await _sftpService.ExistsAsync(_sessionId, remotePath, ct);
    }

    private static string NameOf(string remotePath) =>
        remotePath[(remotePath.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>
    /// 这类传输的目标是不是本会话上的一个新文件(上传与中转):远端同名冲突的检查只对它们做。
    /// 同会话复制(Copy)沿用既有行为,不在这里。
    /// </summary>
    private static bool WritesRemote(TransferType type) => type is TransferType.Upload or TransferType.Relay;

    /// <summary>
    /// 按冲突策略处理一个计划中的上传:对照预列举的远端目录名单检查同名文件
    /// (“覆盖”策略下连列举都省去,直接沿用 SFTP 覆盖语义),冲突时返回 null 表示跳过,
    /// 或返回(可能改了远端路径的)计划项。
    /// </summary>
    private async Task<PlannedFileTransfer?> ResolveRemoteConflictAsync(
        PlannedFileTransfer item,
        Dictionary<string, HashSet<string>> remoteNames,
        BatchConflictDecision decision,
        CancellationToken ct
    )
    {
        if (!WritesRemote(item.Type) || TransferOptions.ConflictPolicy == "overwrite")
        {
            return item;
        }
        if (!await RemoteExistsAsync(item.RemotePath, remoteNames, ct))
        {
            return item;
        }
        return TransferOptions.ConflictPolicy switch
        {
            "skip" => null,
            "rename" => item with
            {
                RemotePath = await NextAvailableRemoteNameAsync(
                                    item.RemotePath,
                                    remoteNames,
                                    ct
                                ),
            },
            // ask
            _ => await AskConflictAsync(
                                item,
                                item.RemotePath,
                                ConfirmRemoteOverwrite,
                                decision
                            ),
        };
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
        string dir = RemotePath.Parent(remotePath);
        foreach (string name in UniqueNames.Candidates(NameOf(remotePath)))
        {
            string candidate = RemotePath.Combine(dir, name);
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
        foreach (string name in UniqueNames.Candidates(Path.GetFileName(localPath)))
        {
            string candidate = Path.Combine(dir, name);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return localPath;
    }

    /// <summary>
    /// 端到端执行一次传输:在传输浮窗中注册,将进度流式推送进去,
    /// 并落定最终状态。失败将行标红并返回;取消将行标为取消、清理本地部分文件,
    /// 并传播取消使批量任务中止。只停这一项(浮窗那一行的 ×)时不传播:返回已取消,这一批其余照传。
    /// </summary>
    private async Task<TransferStatus> RunTransferAsync(
        TransferType type,
        string localPath,
        string remotePath,
        long resumeOffset,
        CancellationToken ct,
        BatchConflictDecision? conflictDecision = null,
        RelaySource? source = null,
        bool pausable = false
    )
    {
        if (type == TransferType.Relay && source is null)
        {
            throw new ArgumentNullException(nameof(source), "A relay transfer needs its source session.");
        }
        var task = new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = type,
            LocalPath = localPath,
            RemotePath = remotePath,
            // 建行即"等待中":这一刻它只是被排进了队,真正开跑要等拿到传输名额
            // (FTP 单连接对端上可能要等很久)。第一次进度回调到达才算开始。
            Status = TransferStatus.Queued,
        };
        TransferStatus finalStatus = TransferStatus.Failed;
        TransferSink?.AddTransfer(task);
        TransferItemViewModel? item = TransferSink?.FindTransfer(task.Id);
        if (item is not null && type is TransferType.Upload or TransferType.Download or TransferType.Relay)
        {
            // 失败后可从传输面板重试:闭包捕获本会话与路径(中转还有源端),重试时重新探测续传起点。
            item.RetryAsync = () => RetryTransferAsync(new(type, localPath, remotePath, Source: source));
        }
        // 这一项自己的取消源,链在批次的上面:「全部取消」照样停得下它,浮窗那一行的 × 只停它。
        // 下面凡是属于这一项的等待(传输本身、重试前的退避、续传探测、冲突改名)都吃它的令牌;
        // 改名重传另起一行、另有自己的取消源,所以那一句仍传批次的令牌。
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken itemCt = itemCts.Token;
        item?.CancelItem = itemCts.Cancel;
        // 本次尝试的起点:续传时 > 0;自动重试会把它挪到这个文件自己已经传过去的地方。
        long offset = resumeOffset;
        // 进入本方法时的起点:一次「一个字节都没动」的失败之后照原样重试,既不是 0(那会整份重传),
        // 也不是上次探测出来的位置(目标没被碰过,那份半截与本次无关)。
        long transferCheckpoint = resumeOffset;
        var progress = new AttemptProgress(new Progress<TransferProgress>(p =>
        {
            item?.UpdateProgress(p);
            // 第一个进度回调 = 字节真的开始流动了:排队结束。续传的第一拍先亮一下"续传中",
            // 之后的回调再落到"进行中"(与改动前的观感一致,只是把它前面那段等待如实标出来了)。
            item?.Status = item.Status switch
            {
                TransferStatus.Queued when offset > 0 => TransferStatus.Resuming,
                TransferStatus.Queued or TransferStatus.Resuming => TransferStatus.InProgress,
                _ => item.Status,
            };
        }));
        // 目标"同名但内容对不上"时改名重传的落点(仅"重命名"策略):当前这一行按跳过收尾,
        // 改名后的整份重传在本方法收尾之后另起一行,免得一行的标题与它实际写的目标对不上。
        PlannedFileTransfer? renamedRetry = null;
        // 中转的源端可能在规划之后被删掉/改名:那种失败发生在目标被碰之前,目标若是用户原有的同名文件,
        // 它完好无损,绝不能按"半截文件"清掉。其余类型维持原先的判断。
        bool targetTouched = type != TransferType.Relay;
        // 失败自动重试(设置 → 文件传输):只管上传、下载与中转 —— 面板上本来就带「重试」的那几种。
        // 同会话复制是服务端那边的一次操作,没有半截可接,失败交给用户决定。
        int maxRetries = type is TransferType.Upload or TransferType.Download or TransferType.Relay
            ? TransferRetryPolicy.Clamp(TransferOptions.TransferMaxRetries)
            : 0;
        int retries = 0;
        // 一次运行内复用已确认完成的源;手动重试/改名另起运行,不跨运行保留缓存。
        StagedRelaySource? stagedSource = null;
        bool remoteCommitRelay = false;
        try
        {
            while (true)
            {
                progress.BeginAttempt(offset);
                try
                {
                    await TransferOnceAsync(offset);
                    item?.Status = TransferStatus.Completed;
                    finalStatus = TransferStatus.Completed;
                    break;
                }

                // 续传起点核实失败 = 目标那半截并不是源文件的开头 —— 文件变了,这是冲突,不是续传。
                // 以前直接失败并让用户自己去删,现在按既有冲突策略处理(询问策略下弹的就是常规
                // 冲突对话框,"全部覆盖/全部跳过"的粘性决定照常沿用)。
                // when 条件保证最多重来一次:改判后的整份重传 offset 为 0,不会再走到这里。
                catch (VelaSftpResumeMismatchException) when (offset > 0)
                {
                    ChangedTargetAction action = await ResolveChangedTargetAsync(
                        type,
                        localPath,
                        remotePath,
                        conflictDecision,
                        source,
                        itemCt
                    );
                    if (action.Renamed is { } target)
                    {
                        renamedRetry = target;
                    }
                    if (action.Renamed is not null || !action.Overwrite)
                    {
                        // 跳过:目标是用户自己的文件,绝不能顺手清掉。
                        item?.Status = TransferStatus.Cancelled;
                        finalStatus = TransferStatus.Cancelled;
                        break;
                    }
                    // 覆盖:整份重传。
                    offset = 0;
                    item?.Status = TransferStatus.InProgress;
                }

                // 断线、超时这类临时失败:退避一会儿,在原会话上再试(权限、磁盘满、路径不存在、
                // 服务端拒写不在此列,见 TransferRetryPolicy)。等待期间这一行显示「等待重试」,
                // 全部取消与这一行的 × 照样能把它停下 —— 等待吃的是这一项的取消令牌。
                // 名额在等待期间不放:链路坏了的时候别的文件也传不动,放出去只是让它们也去撞一遍。
                catch (Exception ex) when (retries < maxRetries
                                           && ex is not OperationCanceledException
                                           && !itemCt.IsCancellationRequested
                                           && !_lifetime.IsCancellationRequested
                                           && TransferRetryPolicy.Classify(ex) == TransferFailureKind.Transient)
                {
                    if (TransferOptions.TransferLogging)
                    {
                        TransferLogService.AppendFailure(TransferOptions.LogDirectory, task.Id, type,
                            retries + 1, offset, progress.MovedBytes, willRetry: true, ex);
                    }
                    retries++;
                    bool movedBytes = progress.MovedBytes;
                    long attemptStart = offset;
                    item?.MarkRetrying(retries, maxRetries);
                    await RetryDelay(TransferRetryPolicy.DelayBeforeRetry(retries), itemCt);
                    // 这一次一个字节都没动过(打开就失败、目标根本没被碰):目标上那份东西不是这次传出来的,
                    // 没有半截可接。以前这里照样去做一次续传探测(它自己会 stat 再把远端截到探测点),
                    // 一次打开失败就白白多出几轮往返 —— 传输日志里成片的 start=0 moved=False 就是它们。
                    offset = !movedBytes
                        ? transferCheckpoint
                        : remoteCommitRelay
                            ? 0
                            : await NextRetryOffsetAsync(type, localPath, remotePath, source, attemptStart, movedBytes, itemCt,
                                hasStagedSource: stagedSource is not null);
                }
            }
        }
        // 只停这一项(浮窗那一行的 ×),批次没取消:这一项按已取消落定、不往上抛,这一批其余照传。
        // 落定之后批次把它从传输队列里摘掉(见 ExecuteResolvedBatchAsync 的 Account),这一批以后
        // 再停下、点「继续」时不会再传它。不只认 OperationCanceledException:取消把流关掉时,
        // 底下冒出来的也可能是一个「通道已关闭」之类的异常 —— 那是取消的后果,不能算这个文件失败,
        // 更不能当成断线把整批挂起。半截目标与整批取消一样,按「断点续传 / 清理半截文件」处理。
        catch (Exception ex) when (ex is not RemoteRelayCommitException && itemCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            item?.Status = TransferStatus.Cancelled;
            finalStatus = TransferStatus.Cancelled;
            if (targetTouched)
            {
                await CleanupPartialTargetAsync(type, localPath, remotePath);
            }
        }
        catch (OperationCanceledException)
        {
            item?.Status = TransferStatus.Cancelled;
            finalStatus = TransferStatus.Cancelled;
            if (targetTouched)
            {
                await CleanupPartialTargetAsync(type, localPath, remotePath);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (TransferOptions.TransferLogging)
            {
                TransferLogService.AppendFailure(TransferOptions.LogDirectory, task.Id, type,
                    retries + 1, offset, progress.MovedBytes, willRetry: false, ex);
            }
            // 连接没了(或临时失败重试用完了):记进传输队列的批次里,这不算这个文件失败 ——
            // 换一条连接就能接着传。面板拆掉之后冒出来的任何失败都按这一类算:那是会话被关掉,不是文件的问题。
            TransferFailureKind kind = _lifetime.IsCancellationRequested
                ? TransferFailureKind.SessionLost
                : TransferRetryPolicy.Classify(ex);
            bool pause = pausable && kind != TransferFailureKind.Permanent;
            item?.Status = pause ? TransferStatus.Paused : TransferStatus.Failed;
            finalStatus = pause ? TransferStatus.Paused : TransferStatus.Failed;
            // 重试过还是失败的,说清楚试过几次:否则用户看到的只是最后那一次的错误,
            // 会以为它一断就放弃了。
            ErrorMessage = retries > 0
                ? Strings.Format("Transfer_FailedAfterRetries", retries, ex.Message)
                : ex.Message;
            if (targetTouched)
            {
                await CleanupPartialTargetAsync(type, localPath, remotePath);
            }
        }
        finally
        {
            // 缓存释放失败不能覆盖原始异常/取消,也不能把已完成改成失败或阻止行的收尾。
            try
            {
                if (stagedSource is not null)
                {
                    await stagedSource.DisposeAsync();
                }
            }
            catch (Exception)
            {
                // 传输结果已落定;清理仅尽力而为,不把路径或服务器消息写入日志。
            }
            stagedSource = null;
            // 落定了:摘掉 ×(取消源随方法返回释放,不能再让那一行去点它)。
            item?.CancelItem = null;
            TransferSink?.NotifyTaskSettled();

            // 记录传输日志(设置 → 文件传输 → 日志记录)。中转的"本地"一列写源端,带上它是哪台机器 ——
            // 光一个远端路径,事后看日志分不出是从哪边搬过来的。
            if (TransferOptions.TransferLogging)
            {
                TransferLogService.Append(
                    TransferOptions.LogDirectory,
                    type,
                    source is null ? localPath : $"{source.DisplayName}:{localPath}",
                    remotePath,
                    finalStatus
                );
            }
        }

        // 改名重传另起一行(目标是新名字,不存在同名冲突,不会再撞上续传核实)。
        if (renamedRetry is { } fresh)
        {
            await RunTransferAsync(fresh.Type, fresh.LocalPath, fresh.RemotePath, 0, ct, conflictDecision, fresh.Source);
        }
        return finalStatus;

        Task TransferOnceAsync(long startAt) => type switch
        {
            TransferType.Upload =>
                _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress, startAt, itemCt),
            // Copy:LocalPath = 远端源路径,RemotePath = 远端目标路径。
            TransferType.Copy =>
                _sftpService.CopyAsync(_sessionId, localPath, remotePath, progress, itemCt),
            // Relay:LocalPath = 另一栏会话上的源路径,RemotePath = 本会话上的目标路径。
            TransferType.Relay => TransferRelayAsync(startAt),
            _ =>
                _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath, progress, startAt, itemCt),
        };

        async Task TransferRelayAsync(long startAt)
        {
            // 每次尝试重新读取源元数据;流式重试会重新下载,不能沿用上次内容的长度或时间。
            if (source!.UseStagedRelaySource || source.UseStreamedRelay)
            {
                // 目标支持安全提交时，先把有界内存流直接写到目标同目录的临时文件，
                // 源确认完成后再原子发布；这样大文件不需要在本机落一份完整缓存。
                if (stagedSource is null && startAt == 0 && await RemoteRelay.TryCopyFileWithRemoteCommitAsync(
                        source.Service, source.SessionId, localPath, _sftpService, _sessionId, remotePath,
                        progress, () => remoteCommitRelay = true, cancellationToken: itemCt))
                {
                    return;
                }

                if (stagedSource is null)
                {
                    item?.IsPreparingSource = true;
                    try
                    {
                        // 准备没有目标进度,也绝不能标记 targetTouched。失败时不缓存半成品。
                        stagedSource = await StagedRelaySource.PrepareAsync(source.Service, source.SessionId, localPath, itemCt);
                    }
                    finally
                    {
                        item?.IsPreparingSource = false;
                    }
                }
                await stagedSource.UploadAsync(_sftpService, _sessionId, remotePath,
                    progress, () => targetTouched = true, startAt, itemCt);
                return;
            }

            await RemoteRelay.CopyFileAsync(
                source.Service, source.SessionId, localPath, _sftpService, _sessionId, remotePath,
                progress, () => targetTouched = true, startAt, cancellationToken: itemCt);
        }
    }

    /// <summary>
    /// 自动重试之前的等待(退避时长见 <see cref="TransferRetryPolicy.DelayBeforeRetry" />)。
    /// 用例换成立即返回并记下时长,不必真的睡几秒。
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> RetryDelay { get; set; } =
        static (delay, ct) => Task.Delay(delay, ct);

    /// <summary>
    /// 自动重试从哪儿接着传。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>断点续传关着:整份重来(0)。</item>
    /// <item>上一次尝试一个字节都没动(连都没连上、打开就失败):原样再试一次同一个起点 ——
    /// 目标上那份东西不是这次传出来的,不能凭它的长度去续(目录同步的目标比源短正是「内容不同」的常态)。</item>
    /// <item>动过:目标上的半截是这个文件自己刚写的,重新探一次它的长度,从那里接着传;起点照常由
    /// SftpService 核实(对不上就落回同名冲突策略),探测失败退回整份重传。</item>
    /// </list>
    /// </remarks>
    private async Task<long> NextRetryOffsetAsync(
        TransferType type,
        string localPath,
        string remotePath,
        RelaySource? source,
        long attemptStart,
        bool movedBytes,
        CancellationToken ct,
        bool hasStagedSource = false
    )
    {
        if (!TransferOptions.ResumeEnabled)
        {
            return 0;
        }
        if (!movedBytes)
        {
            return attemptStart;
        }
        if (hasStagedSource)
        {
            // 源已成为本次运行的不可变快照,不能再用原远端文件的大小/存在性判断续传。
            // 非零只表示请求核实;真实起点、尾部比对与 durable 截断仍由 UploadStreamAsync 处理。
            return Math.Max(0, await GetRemoteFileSizeAsync(remotePath, [], ct));
        }
        PlannedFileTransfer? resumed = await TryResumeAsync(new(type, localPath, remotePath, Source: source), [], ct);
        return resumed?.ResumeOffset ?? 0;
    }

    /// <summary>
    /// 一个文件的进度接收方,顺带记下<b>本次尝试</b>有没有真的传出字节(自动重试据此决定从哪儿接着传)。
    /// </summary>
    /// <remarks>
    /// 记账在 <see cref="Report" /> 里同步做,而不是在 <see cref="Progress{T}" /> 的回调里:
    /// 后者经同步上下文投递,失败的续体先跑到的话就会以为一个字节都没动。
    /// 进度只在字节真的确认之后才上报(见 <see cref="TransferProgressThrottle" />),所以报上来的位置
    /// 只要不等于传进去的起点就算动过 —— 包括比起点还小的情形:续传起点在服务里还会再核实、可能往回退,
    /// 退回去之后写出的那一段报上来就比传进去的起点小。
    /// </remarks>
    private sealed class AttemptProgress(IProgress<TransferProgress> inner) : IProgress<TransferProgress>
    {
        private long _start;
        private int _moved;

        /// <summary>开始新的一次尝试。</summary>
        public void BeginAttempt(long start)
        {
            Interlocked.Exchange(ref _start, start);
            Interlocked.Exchange(ref _moved, 0);
        }

        /// <summary>本次尝试有没有真的传出过字节。</summary>
        public bool MovedBytes => Volatile.Read(ref _moved) != 0;

        public void Report(TransferProgress value)
        {
            if (value.BytesTransferred != Interlocked.Read(ref _start))
            {
                Volatile.Write(ref _moved, 1);
            }
            inner.Report(value);
        }
    }

    /// <summary>目标"同名但内容对不上"时的处置。</summary>
    internal enum ChangedTargetChoice
    {
        /// <summary>整份重传,覆盖已经变了的目标。</summary>
        Overwrite,

        /// <summary>不动目标,这一项作罢。</summary>
        Skip,

        /// <summary>换一个不冲突的新名字,另传一份。</summary>
        Rename,
    }

    /// <summary>
    /// 决定一次"续传起点核实失败"怎么办。核实失败意味着目标那半截并不是源文件的开头,
    /// 也就是<b>文件变了</b> —— 那是一次普通的同名冲突,不该以"请自行删除后重传"收场。
    /// 这里沿用设置里的冲突策略(覆盖/跳过/重命名/询问);"询问"走的就是常规冲突对话框,
    /// 并沿用本批次"全部覆盖/全部跳过"的粘性决定,免得几百个变化文件逐个弹窗。
    /// </summary>
    internal static async Task<ChangedTargetChoice> DecideChangedTargetAsync(
        string? policy,
        string displayPath,
        Func<string, Task<FileConflictResolution>>? confirm,
        BatchConflictDecision decision
    ) =>
        policy switch
        {
            "overwrite" => ChangedTargetChoice.Overwrite,
            "skip" => ChangedTargetChoice.Skip,
            "rename" => ChangedTargetChoice.Rename,
            // ask
            _ => await DecideConflictAsync(displayPath, confirm, decision)
                ? ChangedTargetChoice.Overwrite
                : ChangedTargetChoice.Skip,
        };

    /// <summary>把 <see cref="DecideChangedTargetAsync" /> 的选择落成具体动作(改名时算出新目标路径)。</summary>
    private async Task<ChangedTargetAction> ResolveChangedTargetAsync(
        TransferType type,
        string localPath,
        string remotePath,
        BatchConflictDecision? decision,
        RelaySource? source,
        CancellationToken ct
    )
    {
        bool download = type == TransferType.Download;
        ChangedTargetChoice choice = await DecideChangedTargetAsync(
            TransferOptions.ConflictPolicy,
            download ? localPath : remotePath,
            download ? ConfirmOverwrite : ConfirmRemoteOverwrite,
            decision ?? new()
        );
        return choice switch
        {
            ChangedTargetChoice.Overwrite => new(true),
            ChangedTargetChoice.Rename => new(
                false,
                download
                    ? new PlannedFileTransfer(type, NextAvailableLocalName(localPath), remotePath)
                    : new PlannedFileTransfer(
                        type,
                        localPath,
                        await NextAvailableRemoteNameAsync(remotePath, [], ct),
                        // 中转改名重传时源端不变,得带着它 —— 否则那一行不知道从哪台机器读。
                        Source: source
                    )
            ),
            _ => new(false),
        };
    }

    /// <summary>目标"同名但内容对不上"时的处置:覆盖整份重传、跳过,或改名后另传一份。</summary>
    private readonly record struct ChangedTargetAction(bool Overwrite, PlannedFileTransfer? Renamed = null);

    /// <summary>
    /// 从传输面板重试一个失败项:重新探测续传起点(半截文件还在就从断点继续,起点核实与
    /// 安全回退由 SftpService 兜底)后单文件重跑。
    /// </summary>
    /// <remarks>
    /// <b>重试登记成一个单文件批次</b>,而不是像以前那样全程 <c>CancellationToken.None</c>。
    /// 用 None 意味着「全部取消」按它无可奈何:面板上明明写着已取消,这一条却还在传,
    /// 而且没有任何办法停下来 —— 只能等它自己传完。
    /// </remarks>
    private async Task RetryTransferAsync(PlannedFileTransfer planned)
    {
        PlannedFileTransfer resolved = planned;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Guid batchId = TransferSink?.BeginBatch(1, cts) ?? Guid.Empty;
        try
        {
            try
            {
                Dictionary<string, HashSet<string>> remoteNames =
                    await ListRemoteNamesForUploadsAsync([planned], cts.Token);
                resolved = await TryResumeAsync(planned, remoteNames, cts.Token) ?? planned;
            }
            catch
            {
                // 续传探测失败不阻断重试:退回整份重传。
            }
            // 重试也占一个全局名额:否则连点几个失败项的"重试",并发就绕过上限跑出去了。
            using (await AcquireTransferSlotAsync(
                       Math.Clamp(TransferOptions.MaxConcurrentTransfers, 1, 16), cts.Token))
            {
                await RunTransferAsync(resolved.Type, resolved.LocalPath, resolved.RemotePath, resolved.ResumeOffset, cts.Token, source: resolved.Source);
            }
            TransferSink?.NotifyBatchItemSettled(batchId);
        }
        catch (OperationCanceledException)
        {
            // 用户按了取消。
        }
        finally
        {
            TransferSink?.EndBatch(batchId);
        }
    }

    /// <summary>
    /// 失败/取消后半截目标文件的统一处置(修复四条路径各行其是的老问题):
    /// 断点续传开启 → 一律<b>保留</b>——半截文件是续传素材,下次传同一文件自动从断点继续
    /// (此前"下载取消即删本地半截"恰好毁掉素材,已纠正);
    /// 续传关闭且开了"清理半截文件" → 上传删远端半截、下载删本地半截,失败与取消同样对待;
    /// 两者皆关 → 保留,由用户自行处理。Copy 是远端到远端的原子操作,不产生半截目标。
    /// 中转(Relay)与上传同一口径:目标上的半截同样是下次续传的素材。
    /// </summary>
    private async Task CleanupPartialTargetAsync(TransferType type, string localPath, string remotePath)
    {
        if (TransferOptions.ResumeEnabled || !TransferOptions.AutoCleanTempFiles)
        {
            return;
        }
        if (type == TransferType.Download)
        {
            TryDeleteLocalFile(localPath);
        }
        else if (WritesRemote(type))
        {
            try
            {
                await _sftpService.DeleteAsync(_sessionId, remotePath);
            }
            catch
            {
                // 尽力而为:会话可能已断开;残留的半截文件下次上传会按冲突策略处理。
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
                RemotePath.Combine(CurrentPath, trimmedName),
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
                RemotePath.Combine(CurrentPath, trimmedName),
                ct
            );
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 在当前目录新建符号链接:先问指向哪里(右键某一行时预填该行路径),再问链接叫什么
    /// (预填目标的最后一段)。目标原样写入,相对路径按链接所在目录解析(ln -s 语义)。
    /// 后端不支持时由服务抛 NotSupportedException,错误条如实说出来。
    /// </summary>
    private async Task NewSymbolicLinkAsync(RemoteFileInfoViewModel? source, CancellationToken ct = default)
    {
        if (PromptForText is null)
        {
            return;
        }
        string suggestedTarget = source is { IsParentEntry: false } ? source.FullPath : string.Empty;
        string? target = await PromptForText(Strings.Get("Sftp_SymlinkTargetPrompt"), suggestedTarget);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }
        string trimmedTarget = target.Trim();
        string targetLeaf = trimmedTarget.TrimEnd('/');
        targetLeaf = targetLeaf[(targetLeaf.LastIndexOf('/') + 1)..];
        string? name = await PromptForText(Strings.Get("Sftp_SymlinkNamePrompt"), targetLeaf);
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
            await _sftpService.CreateSymbolicLinkAsync(
                _sessionId,
                RemotePath.Combine(CurrentPath, trimmedName),
                trimmedTarget,
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
            string target = RemotePath.Combine(RemotePath.Parent(file.FullPath), trimmedName);
            await _sftpService.RenameAsync(_sessionId, file.FullPath, target, ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 菜单动作的作用对象:当前选中的真实条目,再并上右键所指的那一行。
    /// <para>
    /// 右键本身已经会把所指行并入选区(见 FileBrowserView.FileRow_PointerPressed),这里再兜一次底,
    /// 好让"对着一行点菜单"和"框选一片再点菜单"走的是同一条路 —— 前者就是后者的 n=1 情形。
    /// </para>
    /// </summary>
    private List<RemoteFileInfoViewModel> SelectionOrRow(RemoteFileInfoViewModel? row)
    {
        List<RemoteFileInfoViewModel> targets = [.. SelectedFiles.Where(f => !f.IsParentEntry)];
        if (row is not null && !row.IsParentEntry && !targets.Any(f => f.FullPath == row.FullPath))
        {
            targets.Add(row);
        }
        return targets;
    }

    private async Task MoveAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (PromptForText is null)
        {
            return;
        }
        List<RemoteFileInfoViewModel> targets = SelectionOrRow(file);
        if (targets.Count == 0)
        {
            return;
        }

        // 单个:仍按"完整目标路径"提示 —— 顺手改个名是这个入口的老用法,不该因为支持批量就丢掉。
        if (targets.Count == 1)
        {
            RemoteFileInfoViewModel only = targets[0];
            string? destination = await PromptForText(Strings.MoveToPrompt, only.FullPath);
            if (string.IsNullOrWhiteSpace(destination) || destination.Trim() == only.FullPath)
            {
                return;
            }
            try
            {
                ErrorMessage = null;
                await _sftpService.RenameAsync(_sessionId, only.FullPath, destination.Trim(), ct);
                await RefreshAsync(ct);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            return;
        }

        // 多个:只问一次目标目录,各自按原名落进去(逐个问完整路径没法用)。
        string? directory = await PromptForText(Strings.Get("MoveSelectedToPrompt"), CurrentPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        string destDir = directory.Trim();
        ErrorMessage = null;
        var failures = new List<string>();
        foreach (RemoteFileInfoViewModel item in targets)
        {
            string dest = RemotePath.Combine(destDir, item.Name);
            if (dest == item.FullPath)
            {
                continue;
            }
            try
            {
                await _sftpService.RenameAsync(_sessionId, item.FullPath, dest, ct);
            }
            catch (Exception ex)
            {
                // 一条失败不该把剩下的也拦住:批量操作半途而废比"跑完再报告"更难收拾。
                failures.Add($"{item.Name}: {ex.Message}");
            }
        }
        // 先刷新再报告:RefreshAsync 会把 ErrorMessage 清掉,反过来写就等于什么都没报。
        await RefreshAsync(ct);
        ReportBatchFailures(failures);
    }

    /// <summary>把批量操作里失败的那几条汇总到错误条上(全成功则清空)。</summary>
    private void ReportBatchFailures(List<string> failures) =>
        ErrorMessage = failures.Count == 0
            ? null
            : Strings.Format("BatchOpPartialFailure", failures.Count, failures[0]);

    private async Task CopyToAsync(RemoteFileInfoViewModel? file, CancellationToken ct = default)
    {
        if (PromptForText is null)
        {
            return;
        }
        List<RemoteFileInfoViewModel> targets = SelectionOrRow(file);
        if (targets.Count == 0)
        {
            return;
        }

        List<PlannedFileTransfer> plan;
        if (targets.Count == 1)
        {
            // 单个:提示完整目标路径(预填同目录同名,方便就地复制一份)。
            RemoteFileInfoViewModel only = targets[0];
            string suggested = RemotePath.Combine(RemotePath.Parent(only.FullPath), only.Name);
            string? destination = await PromptForText(Strings.SftpCopyToPrompt, suggested);
            if (string.IsNullOrWhiteSpace(destination) || destination.Trim() == only.FullPath)
            {
                return;
            }
            plan = [new(TransferType.Copy, only.FullPath, destination.Trim())];
        }
        else
        {
            // 多个:只问一次目标目录,各自按原名落进去。
            string? directory = await PromptForText(Strings.Get("CopySelectedToPrompt"), CurrentPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            string destDir = directory.Trim();
            plan =
            [
                .. targets
                    .Select(item => new PlannedFileTransfer(TransferType.Copy, item.FullPath, RemotePath.Combine(destDir, item.Name)))
                    .Where(item => item.RemotePath != item.LocalPath),
            ];
            if (plan.Count == 0)
            {
                return;
            }
        }

        try
        {
            ErrorMessage = null;

            // 走统一的传输管线:进度、取消、失败状态与浮窗生命周期都是现成的。
            bool ok = await RunTransferBatchAsync(plan, ct);
            if (ok)
            {
                await RefreshAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled.
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

    private async Task CopyCurrentPathAsync(CancellationToken ct = default)
    {
        if (CopyToClipboard is null || string.IsNullOrEmpty(CurrentPath))
        {
            return;
        }
        await CopyToClipboard(CurrentPath);
    }

    /// <summary>
    /// 执行一条协议专属动作。宿主只负责把「哪个动作 + 哪个路径」转交给插件,
    /// 具体做什么(生成预签名 URL、开一扇面板…)完全由插件决定 ——
    /// 这正是文件浏览器能对新协议零改动的原因。
    /// </summary>
    private async Task InvokeProtocolActionAsync(ProtocolActionViewModel? action, CancellationToken ct = default)
    {
        if (action is null || InvokeProtocolAction is null)
        {
            return;
        }
        // Background 作用域的动作(如「桶管理」)针对当前目录,其余针对右键命中的那一行。
        string path = action.Target is { IsParentEntry: false } entry ? entry.FullPath : CurrentPath;
        try
        {
            ErrorMessage = null;
            await InvokeProtocolAction(action.Id, path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
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

        // 能改属主的后端才给候选名单(FTP、插件协议给 null,弹窗里属主/属组只读)。
        // 名单取不到不该挡住看属性,当作改不了属主就是。
        RemoteOwnerChoices? ownerChoices = null;
        try
        {
            ownerChoices = await _sftpService.GetOwnerChoicesAsync(_sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 退回只读。
        }

        // 属性弹窗内含权限矩阵与属主/属组;确定后只带回改了的项,由这里落到 chown / chmod。
        FilePropertiesChange? change = await ShowFileProperties(file, ownerChoices);
        if (change is null || change.IsEmpty)
        {
            return;
        }
        bool applied = false;
        string? error = null;
        try
        {
            ErrorMessage = null;

            // 先 chown 再 chmod:属主是最容易被拒的一项(非 root 只能把属组改成自己所在的组),
            // 放在前面,它被拒时什么都还没改。
            if (change.Owner is not null || change.Group is not null)
            {
                await _sftpService.SetOwnerAsync(_sessionId, file.FullPath, change.Owner, change.Group, ct);
                applied = true;
            }
            if (change.Mode is { } mode)
            {
                await _sftpService.SetPermissionsAsync(_sessionId, file.FullPath, mode, ct);
                applied = true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // 只成了一半(chown 过了、chmod 被拒)也要刷新,列表才与远端对得上。
        // 刷新会清错误条,所以错误在刷新之后再写。
        if (applied)
        {
            await RefreshAsync(ct);
        }
        if (error is not null)
        {
            ErrorMessage = error;
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
    private async Task DeleteManyAsync(List<RemoteFileInfoViewModel> targets, CancellationToken ct)
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

    private void ToggleVisibility() => IsVisible = !IsVisible;

    /// <summary>
    /// 目录同步专用的传输入口:计划已经由用户在预览里逐条确认过,所以<b>不走</b>冲突策略
    /// (否则「文件已存在时:询问」会对每个要覆盖的文件再弹一次窗),也<b>不做</b>续传探测
    /// (目标比源小正是「内容不同」的常态,当成半截文件去续传会拼出一个错的文件)。
    /// 进度、并发上限、取消、传输日志与普通传输完全共用。
    /// </summary>
    /// <param name="requests">要执行的传输(目标父目录须已存在)。</param>
    /// <param name="settled">每项落定后回调,用于回写修改时间;单条取消按已取消回调,整批取消时不回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>false 表示被取消。</returns>
    internal Task<bool> RunSyncTransfersAsync(
        IReadOnlyList<SyncTransferRequest> requests,
        Func<SyncTransferRequest, TransferStatus, Task>? settled,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(requests);
        PlannedFileTransfer[] plan = [.. requests.Select(r => new PlannedFileTransfer(r.Type, r.LocalPath, r.RemotePath))];
        return ExecuteResolvedBatchAsync(
            plan,
            new BatchConflictDecision { OverwriteAll = true },
            settled is null ? null : (item, status) => settled(new(item.Type, item.LocalPath, item.RemotePath), status),
            ct
        );
    }

    /// <summary>
    /// A single file scheduled for transfer, resolved up front so the whole batch can be
    /// counted and cancelled as one unit.
    /// For Copy: LocalPath = remote source, RemotePath = remote destination.
    /// For Relay: LocalPath = source path on <see cref="Source" />'s session, RemotePath = destination here.
    /// ResumeOffset > 0 indicates a breakpoint resume from that byte position.
    /// </summary>
    private sealed record PlannedFileTransfer(
        TransferType Type,
        string LocalPath,
        string RemotePath,
        long ResumeOffset = 0,
        RelaySource? Source = null
    );

    /// <summary>中转传输的源端:另一栏的文件服务、会话与显示名(日志与传输面板用)。</summary>
    private sealed record RelaySource(
        ISftpService Service,
        Guid SessionId,
        string DisplayName,
        bool UseStagedRelaySource = false,
        bool UseStreamedRelay = false);
}

/// <summary>目录同步交给传输管道的一项:方向、本地路径、远端路径。</summary>
internal readonly record struct SyncTransferRequest(TransferType Type, string LocalPath, string RemotePath);
