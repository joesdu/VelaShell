using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 隧道管理面板(设计 B3Rth),以服务器为中心:从已保存会话中选一台服务器,
/// 创建/启动隧道时后台自动建立专用 SSH 连接(不打开终端标签)。
/// 隧道生命周期独立于终端会话;该服务器最后一条隧道删除后,后台连接自动断开。
/// </summary>
public class TunnelPanelViewModel : ReactiveObject, IDisposable, IDraggablePanel
{
    /// <summary>隧道配置持久化集合:每台服务器一份文档(id = profileId),内容为配置列表。</summary>
    private const string TunnelCollection = "tunnels";

    /// <summary>浮层位置的存放处;与文件传输提示、消息中心同一个集合,各占一个文档 Id。</summary>
    private const string LayoutCollection = "ui-layout";

    private const string PanelPositionId = "tunnel-panel";

    /// <summary>后台建立到某台服务器的连接;第二个参数 = 这是断线后的自动恢复(主机指纹要人裁决时直接拒绝,不弹窗)。</summary>
    private readonly Func<SessionProfile, bool, CancellationToken, Task<Guid>>? _backgroundConnector;

    private readonly IAppDataStore? _dataStore;

    /// <summary>已从存储恢复过配置的服务器(每次运行每台只恢复一次)。</summary>
    private readonly HashSet<Guid> _restoredProfiles = [];

    /// <summary>面板持有的后台隧道连接:profileId → sessionId。</summary>
    private readonly Dictionary<Guid, Guid> _hostSessions = [];

    private readonly Func<Guid, bool>? _isSessionAlive;

    /// <summary>每台服务器的隧道条目缓存:切换服务器/后台连接掉线后配置仍在,可一键重启。</summary>
    private readonly Dictionary<Guid, ObservableCollection<TunnelItemViewModel>> _itemsByProfile = [];

    private readonly HashSet<Guid> _deletingTunnelIds = [];

    /// <summary>正在自动恢复的服务器,防止时钟每 5 秒重复排一次同样的恢复。</summary>
    private readonly HashSet<Guid> _autoReconnectingProfiles = [];

    /// <summary>自动恢复的连续失败次数,用于按 <see cref="BackoffFor" /> 拉长重试间隔。</summary>
    private readonly Dictionary<Guid, int> _autoReconnectFailures = [];

    /// <summary>自动恢复的下次可尝试时刻(退避窗口)。</summary>
    private readonly Dictionary<Guid, DateTime> _autoReconnectNotBefore = [];

    /// <summary>
    /// 上一次连接败在主机指纹上的服务器:不再自动恢复,等用户手动启动隧道(那一次会弹窗问他)、连上之后才解除。
    /// </summary>
    /// <remarks>
    /// 退避只是拉长间隔,指纹的事再试多少次结果都一样 —— 而在自动恢复不弹窗之前,每试一次就弹一遍确认框,
    /// 最长每 5 分钟一次,一直弹到有人去点。
    /// </remarks>
    private readonly HashSet<Guid> _hostKeyRejectedProfiles = [];

    /// <summary>认证需要用户处理或已取消的服务器:手动连接成功后才恢复后台重建。</summary>
    private readonly HashSet<Guid> _authenticationBlockedProfiles = [];

    private readonly Lock _deletingTunnelIdsGate = new();

    private readonly DispatcherTimer? _liveTimer;

    /// <summary>
    /// 面板开着时每秒一拍,只刷流量(速率、连接列表、累计)。5 秒一拍的 <see cref="_liveTimer" /> 管的是存活与自动恢复,
    /// 拿它刷速率的话,读数比实际晚五秒才动 —— 而速率本来就取最近三秒的平均。面板收起时停表,不白白每秒同步一遍。
    /// </summary>
    private readonly DispatcherTimer? _trafficTimer;

    /// <summary>
    /// 把面板外发生的错误报给用户(宿主接的是右下角的错误提示)。启动时自动建立隧道那会儿面板多半没开,
    /// 写进表单下的 <see cref="ErrorMessage" /> 没人看得见。第二个参数非空时提示上带「重试」,点了就执行它。
    /// </summary>
    private readonly Action<string, Action?>? _errorReporter;

    /// <summary>
    /// 面板外的一句告知(宿主接的是右下角的信息提示)。只用在「点了重试、隧道建起来了」:
    /// 那时面板多半没开,点完提示就消失,不报一声用户不知道成没成。
    /// </summary>
    private readonly Action<string>? _infoReporter;

    private readonly Func<Task<IReadOnlyList<SessionProfile>>>? _savedProfilesProvider;
    private readonly Func<Guid, Task>? _sessionDisconnector;
    private readonly ITunnelWorkflowService _workflowService;
    private Guid? _editingTunnelId;
    private bool _isConnectingHost;

    /// <summary>正在重建 <see cref="Servers" />:期间下拉框回写的 null 选中项不是用户换了服务器。</summary>
    private bool _isReloadingServers;

    private SessionProfile? _selectedServer;
    private ObservableCollection<TunnelItemViewModel> _tunnels;

    /// <summary>
    /// 构造隧道面板视图模型;可注入已保存会话来源、后台连接/断开与存活探测委托、配置持久化存储
    /// 及面板外的错误 / 信息提示出口(均可为空,便于测试)。
    /// </summary>
    /// <param name="workflowService">隧道工作流。</param>
    /// <param name="savedProfilesProvider">已保存的会话。</param>
    /// <param name="backgroundConnector">后台连接;第二个参数 = 这是断线后的自动恢复。</param>
    /// <param name="isSessionAlive">后台会话是否还活着。</param>
    /// <param name="sessionDisconnector">断开后台会话。</param>
    /// <param name="dataStore">隧道配置与面板位置的存储。</param>
    /// <param name="errorReporter">面板外的错误提示;第二个参数非空时提示上带「重试」。</param>
    /// <param name="infoReporter">面板外的信息提示(重试成功时报一声)。</param>
    public TunnelPanelViewModel(
        ITunnelWorkflowService workflowService,
        Func<Task<IReadOnlyList<SessionProfile>>>? savedProfilesProvider = null,
        Func<SessionProfile, bool, CancellationToken, Task<Guid>>? backgroundConnector = null,
        Func<Guid, bool>? isSessionAlive = null,
        Func<Guid, Task>? sessionDisconnector = null,
        IAppDataStore? dataStore = null,
        Action<string, Action?>? errorReporter = null,
        Action<string>? infoReporter = null)
    {
        _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
        _savedProfilesProvider = savedProfilesProvider;
        _backgroundConnector = backgroundConnector;
        _isSessionAlive = isSessionAlive;
        _sessionDisconnector = sessionDisconnector;
        _dataStore = dataStore;
        _errorReporter = errorReporter;
        _infoReporter = infoReporter;
        RestorePanelPosition();
        Servers = [];
        _tunnels = [];
        // 表单填全了没有:任何一项表单字段变了就重算。字段随类型增减(远程动态要放行名单,
        // 本地端口对它没有意义),不再一个个塞进 WhenAnyValue。
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && FormProperties.Contains(name))
            {
                this.RaisePropertyChanged(nameof(IsFormValid));
            }
        };
        IObservable<bool> canCreate = this.WhenAnyValue(vm => vm.IsFormValid);
        CreateTunnelCommand = ReactiveCommand.CreateFromTask(SubmitAsync, canCreate);
        StopTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StopTunnelAsync);
        StartTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StartTunnelAsync);
        DeleteTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(DeleteTunnelAsync);
        EditTunnelCommand = ReactiveCommand.Create<Guid>(BeginEdit);
        // 取消:编辑模式下退出编辑并清空表单;普通状态表单默认值清空不易察觉,直接收起面板。
        ResetFormCommand = ReactiveCommand.Create(() =>
        {
            ErrorMessage = null;
            bool wasEditing = IsEditing;
            ResetForm();
            if (!wasEditing)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        });
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        // 每 5 秒刷新条目(运行时长/服务侧状态与错误)并核对后台连接是否还活着;
        // 无 Avalonia 应用(单元测试)时跳过。
        if (Application.Current is not null)
        {
            _liveTimer = new()
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _liveTimer.Tick += (_, _) => RefreshLiveState();
            _liveTimer.Start();
            _trafficTimer = new()
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _trafficTimer.Tick += (_, _) => RefreshTraffic();
        }
    }

    /// <summary>
    /// 面板是否展开(宿主随面板的显隐设置)。开着时每秒刷一次速率与连接列表,收起就停。
    /// </summary>
    public bool IsOpen
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            if (value)
            {
                RefreshTraffic();
                _trafficTimer?.Start();
            }
            else
            {
                _trafficTimer?.Stop();
            }
        }
    }

    /// <summary>隧道走哪台服务器:全部已保存会话。</summary>
    public ObservableCollection<SessionProfile> Servers { get; }

    /// <summary>当前选中的目标服务器;切换时会加载其隧道条目并刷新连接状态。</summary>
    public SessionProfile? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (ReferenceEquals(_selectedServer, value))
            {
                return;
            }
            // 重建列表时 Servers.Clear() 会让下拉框把选中项回写成 null。照单全收的话条目被换成空集合,
            // 重建完选回同一台时又因「没变」不再加载 —— 面板重开就看不到这台服务器上已开的隧道。
            if (_isReloadingServers)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _selectedServer, value);
            OnServerChanged();
        }
    }

    /// <summary>当前服务器的隧道条目(按服务器缓存,切换不丢配置)。</summary>
    public ObservableCollection<TunnelItemViewModel> Tunnels
    {
        get => _tunnels;
        private set => this.RaiseAndSetIfChanged(ref _tunnels, value);
    }

    /// <summary>服务器行下方的连接状态说明。</summary>
    public string ServerStatusText
    {
        get
        {
            if (SelectedServer is null)
            {
                return Strings.Get("Msg_SelectServer");
            }
            if (_autoReconnectingProfiles.Contains(SelectedServer.Id))
            {
                return Strings.Get("Msg_TunnelAutoReconnecting");
            }
            if (_isConnectingHost)
            {
                return Strings.Get("Msg_ConnectingInBackground");
            }
            return ResolveLiveSession(SelectedServer.Id) is not null
                       ? Strings.Get("Msg_TunnelConnectedBackground")
                       : Strings.Get("Msg_TunnelNotConnectedAuto");
        }
    }

    /// <summary>当前服务器是否已有活着的后台隧道连接。</summary>
    public bool IsServerConnected => SelectedServer is not null && ResolveLiveSession(SelectedServer.Id) is not null;

    /// <summary>参与「表单填全了没有」(<see cref="IsFormValid" />)的属性。</summary>
    private static readonly HashSet<string> FormProperties =
    [
        nameof(SelectedServer),
        nameof(NewTunnelType),
        nameof(NewLocalHost),
        nameof(NewLocalPort),
        nameof(NewRemoteHost),
        nameof(NewRemotePort),
        nameof(ForwardToServerLoopback),
        nameof(NewPermitRemoteOpen),
        nameof(NewUseUnixSocket),
        nameof(NewLocalSocketPath),
        nameof(NewRemoteSocketPath),
    ];

    /// <summary>
    /// 表单填全了没有(「创建 / 保存」键据此可点):选了服务器;本机这头要端口的有端口;服务器那头要地址的有地址与端口;
    /// 远程动态转发的放行名单至少一条、每条都写对。
    /// </summary>
    /// <remarks>
    /// 本地 / 远程转发改用 Unix 套接字时,用上套接字的那一头看路径(要写对),不再看主机与端口。
    /// </remarks>
    public bool IsFormValid =>
        SelectedServer is not null && NewTunnelType switch
        {
            TunnelType.DynamicForward => IsLocalEndpointFilled,
            TunnelType.RemoteDynamicForward => IsServerEndpointFilled && RemoteOpenRules.TryParse(NewPermitRemoteOpen, out _, out _),
            _ => (UsesLocalSocket ? LocalSocketPathProblem(NewLocalSocketPath) is null : IsLocalEndpointFilled)
                 && (UsesRemoteSocket ? RemoteSocketPathProblem(NewRemoteSocketPath) is null : IsServerEndpointFilled)
        };

    private bool IsLocalEndpointFilled => !string.IsNullOrWhiteSpace(NewLocalHost) && NewLocalPort is >= 1 and <= 65535;

    private bool IsServerEndpointFilled => !string.IsNullOrWhiteSpace(NewRemoteHost) && NewRemotePort is >= 1 and <= 65535;

    /// <summary>ComboBox 适配器:0 = 本地转发, 1 = 远程转发, 2 = 动态转发 (SOCKS), 3 = 远程动态转发 (SOCKS)。</summary>
    public int NewTunnelTypeIndex
    {
        get =>
            NewTunnelType switch
            {
                TunnelType.RemoteForward => 1,
                TunnelType.DynamicForward => 2,
                TunnelType.RemoteDynamicForward => 3,
                _ => 0
            };
        set =>
            NewTunnelType = value switch
            {
                1 => TunnelType.RemoteForward,
                2 => TunnelType.DynamicForward,
                3 => TunnelType.RemoteDynamicForward,
                _ => TunnelType.LocalForward
            };
    }

    /// <summary>动态转发(SOCKS)没有固定目标,隐藏目标主机/端口输入。远程动态转发用这两栏填服务器上的监听地址与端口。</summary>
    public bool IsRemoteTargetVisible => NewTunnelType != TunnelType.DynamicForward;

    /// <summary>本地端口那一栏:远程动态转发两头都不在本机监听、也没有本机目标,用不上它。</summary>
    public bool IsLocalPortVisible => NewTunnelType != TunnelType.RemoteDynamicForward;

    /// <summary>放行名单只有远程动态转发有。</summary>
    public bool IsPermitRemoteOpenVisible => NewTunnelType == TunnelType.RemoteDynamicForward;

    /// <summary>
    /// 主机那一栏的标签:远程 / 远程动态转发填的是服务器上的监听地址,不是目标 —— 远程转发的目标是本机的服务(本地端口那栏)。
    /// 曾经三种类型共用「目标主机(服务器视角)」,远程转发填的明明是服务器在哪个地址上监听。
    /// </summary>
    public string RemoteHostLabel => Strings.Get(ListensOnServer ? "Tunnel_ServerBindHostLabel" : "Tunnel_TargetHostLabel");

    /// <summary>这个类型是请服务器监听(远程 / 远程动态转发):主机与端口两栏说的是服务器上的监听端。</summary>
    private bool ListensOnServer => NewTunnelType is TunnelType.RemoteForward or TunnelType.RemoteDynamicForward;

    /// <summary>
    /// 「锁定 127.0.0.1」那个勾选框的字:远程 / 远程动态转发是「只在服务器本机监听」(不让服务器所在网络里的别人连上来),
    /// 本地转发是「转发到服务器本机」。
    /// </summary>
    public string LoopbackOptionText => Strings.Get(ListensOnServer ? "Tunnel_ListenOnServerLoopback" : "Tunnel_ForwardToLoopback");

    /// <summary>
    /// 表单中的放行名单(远程动态转发):<c>主机:端口</c> 空白或逗号隔开,单独一条 <c>any</c> 是全放。默认空 —— 不替用户全放。
    /// </summary>
    public string NewPermitRemoteOpen
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(PermitRemoteOpenError));
            this.RaisePropertyChanged(nameof(HasPermitRemoteOpenError));
        }
    } = string.Empty;

    /// <summary>放行名单里写错的那一条,当场指出来(空着不算写错,只是「创建」点不了)。</summary>
    public string? PermitRemoteOpenError =>
        IsPermitRemoteOpenVisible
        && !RemoteOpenRules.TryParse(NewPermitRemoteOpen, out _, out string? invalid)
        && invalid is not null
            ? Strings.Format("Tunnel_PermitRemoteOpenInvalid", invalid)
            : null;

    /// <summary>放行名单有写错的规则。</summary>
    public bool HasPermitRemoteOpenError => PermitRemoteOpenError is not null;

    // ---- Unix 域套接字 ----

    /// <summary>
    /// 「改用 Unix 域套接字」只对本地 / 远程转发有:SSH 库的本地转发与远程转发两头都各自可以是套接字(各四种组合);
    /// 动态与远程动态转发没有套接字的写法。
    /// </summary>
    public bool IsUnixSocketOptionVisible => NewTunnelType is TunnelType.LocalForward or TunnelType.RemoteForward;

    /// <summary>表单中的「改用 Unix 域套接字」。默认不勾。</summary>
    public bool NewUseUnixSocket
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseSocketChanged();
        }
    }

    /// <summary>
    /// 表单中本机这头的套接字路径:本地转发是在它上面监听,远程转发是回连到它;留空则仍用本地端口。
    /// </summary>
    public string NewLocalSocketPath
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseSocketChanged();
        }
    } = string.Empty;

    /// <summary>
    /// 表单中服务器那头的套接字路径:本地转发是转到它(留空则仍用目标主机:端口),
    /// 远程转发是请服务器在它上面监听(留空则仍用服务器监听地址:端口)。
    /// </summary>
    public string NewRemoteSocketPath
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseSocketChanged();
        }
    } = string.Empty;

    /// <summary>套接字那两栏要不要显示:勾了「改用 Unix 域套接字」。</summary>
    public bool IsUnixSocketFormVisible => IsUnixSocketOptionVisible && NewUseUnixSocket;

    /// <summary>
    /// 本机这头用不用套接字:勾了「改用 Unix 域套接字」、填了路径才用,本地与远程转发一个口径。
    /// 曾经远程转发勾了就两头都要(SSH 库当时只有两头都是套接字一种组合),只填一头点不了「创建」。
    /// </summary>
    private bool UsesLocalSocket => IsUnixSocketFormVisible && !string.IsNullOrWhiteSpace(NewLocalSocketPath);

    /// <summary>服务器那头用不用套接字;口径同 <see cref="UsesLocalSocket" />。</summary>
    private bool UsesRemoteSocket => IsUnixSocketFormVisible && !string.IsNullOrWhiteSpace(NewRemoteSocketPath);

    /// <summary>本机套接字那一栏的标签:本地转发是「监听」,远程转发是「回连到」;都可留空(留空用本地端口)。</summary>
    public string LocalSocketLabel => Strings.Get(NewTunnelType == TunnelType.RemoteForward ? "Tunnel_LocalSocketTargetLabel" : "Tunnel_LocalSocketListenLabel");

    /// <summary>服务器套接字那一栏的标签:本地转发是「目标」,远程转发是「监听」;都可留空(留空用主机与端口那两栏)。</summary>
    public string RemoteSocketLabel => Strings.Get(NewTunnelType == TunnelType.RemoteForward ? "Tunnel_RemoteSocketListenLabel" : "Tunnel_RemoteSocketTargetLabel");

    /// <summary>
    /// 本机套接字栏的示例路径:放在只有自己进得去的目录里(Linux 的 <c>$XDG_RUNTIME_DIR</c>、Windows 的用户临时目录),
    /// 从建好套接字到改权限之间的那一小段窗口里别人也够不着它。
    /// </summary>
    public static string LocalSocketPlaceholder { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && !OperatingSystem.IsWindows()
            ? runtime
            : Path.GetTempPath(),
        "app.sock");

    /// <summary>本机套接字路径写错了(不是完整路径、太长)时的提示;空着不算错。</summary>
    public string? LocalSocketPathError => UsesLocalSocket && !string.IsNullOrWhiteSpace(NewLocalSocketPath) ? LocalSocketPathProblem(NewLocalSocketPath) : null;

    /// <summary>服务器套接字路径写错了(不是 / 开头的绝对路径)时的提示;空着不算错。</summary>
    public string? RemoteSocketPathError => UsesRemoteSocket && !string.IsNullOrWhiteSpace(NewRemoteSocketPath) ? RemoteSocketPathProblem(NewRemoteSocketPath) : null;

    /// <summary>本机套接字路径有错。</summary>
    public bool HasLocalSocketPathError => LocalSocketPathError is not null;

    /// <summary>服务器套接字路径有错。</summary>
    public bool HasRemoteSocketPathError => RemoteSocketPathError is not null;

    /// <summary>本地端口那一栏可编辑:本机这头没改用套接字。</summary>
    public bool IsLocalPortEditable => !UsesLocalSocket;

    /// <summary>目标(或服务器监听)主机与端口可编辑:服务器那头没改用套接字。</summary>
    public bool IsRemoteEndpointEditable => !UsesRemoteSocket;

    /// <summary>
    /// 本机套接字路径的上限(UTF-8 字节,不含结尾的 0):<c>sun_path</c> 在 Linux 与 Windows 上是 108 字节、macOS 上是 104 字节。
    /// 超了 SSH 库当场报错,表单上先拦住、说清楚。
    /// </summary>
    internal static int MaxLocalSocketPathBytes => OperatingSystem.IsMacOS() ? 103 : 107;

    /// <summary>本机套接字路径的问题:要是完整路径(相对路径落在哪全看程序的当前目录)、不能太长。没问题返回 <see langword="null" />。</summary>
    private static string? LocalSocketPathProblem(string path)
    {
        string trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            return Strings.Get("Tunnel_SocketPathRequired");
        }
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return Strings.Get("Tunnel_LocalSocketPathNotAbsolute");
        }
        int bytes = Encoding.UTF8.GetByteCount(trimmed);
        return bytes > MaxLocalSocketPathBytes ? Strings.Format("Tunnel_LocalSocketPathTooLong", bytes, MaxLocalSocketPathBytes) : null;
    }

    /// <summary>服务器套接字路径的问题:要是 <c>/</c> 开头的绝对路径(服务器上的 sshd 按它自己的文件系统解析)。</summary>
    private static string? RemoteSocketPathProblem(string path)
    {
        string trimmed = path.Trim();
        return trimmed.Length == 0 ? Strings.Get("Tunnel_SocketPathRequired")
            : !trimmed.StartsWith('/') ? Strings.Get("Tunnel_RemoteSocketPathNotAbsolute")
            : null;
    }

    private void RaiseSocketChanged()
    {
        this.RaisePropertyChanged(nameof(IsUnixSocketOptionVisible));
        this.RaisePropertyChanged(nameof(IsUnixSocketFormVisible));
        this.RaisePropertyChanged(nameof(LocalSocketLabel));
        this.RaisePropertyChanged(nameof(RemoteSocketLabel));
        this.RaisePropertyChanged(nameof(LocalSocketPathError));
        this.RaisePropertyChanged(nameof(RemoteSocketPathError));
        this.RaisePropertyChanged(nameof(HasLocalSocketPathError));
        this.RaisePropertyChanged(nameof(HasRemoteSocketPathError));
        this.RaisePropertyChanged(nameof(IsLocalPortEditable));
        this.RaisePropertyChanged(nameof(IsRemoteEndpointEditable));
        this.RaisePropertyChanged(nameof(IsRemoteHostEditable));
    }

    /// <summary>
    /// 默认转发到"服务器本机":目标主机锁定为 127.0.0.1(从服务器视角,
    /// 填服务器公网 IP 会被服务器自己拒绝)。取消勾选可填内网第三方主机。
    /// </summary>
    public bool ForwardToServerLoopback
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (value)
            {
                NewRemoteHost = "127.0.0.1";
            }
            this.RaisePropertyChanged(nameof(IsRemoteHostEditable));
        }
    } = true;

    /// <summary>目标主机是否可编辑:仅在未勾选"转发到服务器本机"、且服务器那头没改用套接字时开放输入。</summary>
    public bool IsRemoteHostEditable => !ForwardToServerLoopback && IsRemoteEndpointEditable;

    /// <summary>新建/编辑表单选择的隧道类型(本地/远程/动态/远程动态转发)。</summary>
    public TunnelType NewTunnelType
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(NewTunnelTypeIndex));
            this.RaisePropertyChanged(nameof(IsRemoteTargetVisible));
            this.RaisePropertyChanged(nameof(IsLocalPortVisible));
            this.RaisePropertyChanged(nameof(IsPermitRemoteOpenVisible));
            this.RaisePropertyChanged(nameof(RemoteHostLabel));
            this.RaisePropertyChanged(nameof(LoopbackOptionText));
            this.RaisePropertyChanged(nameof(PermitRemoteOpenError));
            this.RaisePropertyChanged(nameof(HasPermitRemoteOpenError));
            RaiseSocketChanged();
        }
    }

    /// <summary>表单中的本地监听主机,默认 127.0.0.1。</summary>
    public string NewLocalHost
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "127.0.0.1";

    /// <summary>表单中的本地监听端口。</summary>
    public int NewLocalPort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 27017;

    /// <summary>表单中的目标(远端)主机,默认 127.0.0.1。</summary>
    public string NewRemoteHost
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "127.0.0.1";

    /// <summary>表单中的目标(远端)端口。</summary>
    public int NewRemotePort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 27017;

    /// <summary>
    /// 表单中的「承载会话掉线后自动重连并重建」开关。默认关闭:自动重拨可能弹凭据提示,
    /// 也可能对着一台真的下线了的服务器反复重试,该由用户按隧道自己决定。
    /// </summary>
    public bool NewAutoReconnect
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 表单中的「程序启动时自动建立」开关。默认关闭:启停状态不落盘,重启后隧道一律是已停止、
    /// 等用户手动启动;勾上的才在启动时替用户连上服务器、建好隧道。
    /// </summary>
    public bool NewAutoStart
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 表单中的限速(KB/s,每个方向,这条隧道的全部连接共用);0 = 不限(默认)。
    /// </summary>
    public int NewRateLimitKiB
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, Math.Clamp(value, 0, MaxRateLimitKiB));
    }

    /// <summary>限速输入框的上限(KB/s):1 GB/s,再大就等于不限。</summary>
    public const int MaxRateLimitKiB = 1024 * 1024;

    /// <summary>表单中的隧道别名(可选,留空时用路由描述兜底)。</summary>
    public string NewTunnelName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>表单区展示的错误提示;无错误时为空。</summary>
    public string? ErrorMessage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>非空 = 表单处于"编辑既有隧道"模式;提交按钮显示"保存"。</summary>
    public bool IsEditing => _editingTunnelId is not null;

    /// <summary>表单标题:根据是否处于编辑模式显示"编辑隧道"或"新建隧道"。</summary>
    public string FormTitle => IsEditing ? Strings.Get("Msg_EditTunnel") : Strings.Get("NewTunnel");

    /// <summary>换语言后让绑定重取 C# 侧拼的文案(服务器状态行、表单标题与按钮、各隧道行)。</summary>
    public void RefreshLocalizedText()
    {
        this.RaisePropertyChanged(nameof(ServerStatusText));
        this.RaisePropertyChanged(nameof(FormTitle));
        this.RaisePropertyChanged(nameof(SubmitButtonText));
        this.RaisePropertyChanged(nameof(RemoteHostLabel));
        this.RaisePropertyChanged(nameof(LoopbackOptionText));
        this.RaisePropertyChanged(nameof(PermitRemoteOpenError));
        RaiseSocketChanged();
        foreach (TunnelItemViewModel tunnel in Tunnels)
        {
            tunnel.RefreshLocalizedText();
        }
    }

    /// <summary>提交按钮文案:编辑模式显示"保存",否则显示"创建"。</summary>
    public string SubmitButtonText => IsEditing ? Strings.Get("Save") : Strings.Get("Msg_Create");

    /// <summary>按表单当前配置创建新隧道(或保存正在编辑的隧道)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CreateTunnelCommand { get; }

    /// <summary>停止指定隧道(按隧道 Id)。</summary>
    public ReactiveCommand<Guid, RxVoid> StopTunnelCommand { get; }

    /// <summary>启动一条已停止的隧道(按隧道 Id),必要时先建立后台连接。</summary>
    public ReactiveCommand<Guid, RxVoid> StartTunnelCommand { get; }

    /// <summary>删除指定隧道(按隧道 Id),并在无隧道时释放后台连接。</summary>
    public ReactiveCommand<Guid, RxVoid> DeleteTunnelCommand { get; }

    /// <summary>由视图设置:向用户确认删除某条隧道;返回 false 或未设置则取消。</summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>把某条隧道的配置填回表单进入编辑模式;保存时按新配置重建。</summary>
    public ReactiveCommand<Guid, RxVoid> EditTunnelCommand { get; }

    /// <summary>取消按钮:清空表单与错误提示,并退出编辑模式。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ResetFormCommand { get; }

    /// <summary>收起面板。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    /// <summary>释放面板资源:停止实时刷新计时器。</summary>
    public void Dispose()
    {
        _liveTimer?.Stop();
        _trafficTimer?.Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>面板右上角关闭按钮(设计 B3Rth tunCloseBtn),由宿主收起面板。</summary>
    public event EventHandler? CloseRequested;

    // ---- 面板拖拽位置(与文件传输提示、消息中心同一套:见 IDraggablePanel) ----

    /// <summary>面板相对默认锚点(菜单栏下方右侧)的水平偏移(像素,向左为负)。</summary>
    public double PanelOffsetX
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>面板相对默认锚点的垂直偏移(像素,向下为正)。</summary>
    public double PanelOffsetY
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>拖拽结束时由视图调用:把当前位置落盘,供下次打开恢复。失败不影响使用。</summary>
    public void PersistPanelPosition()
    {
        if (_dataStore is null)
        {
            return;
        }
        var position = new PanelPosition { OffsetX = PanelOffsetX, OffsetY = PanelOffsetY };
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await _dataStore.UpsertAsync(LayoutCollection, PanelPositionId, position).ConfigureAwait(false);
            }
            catch
            {
                // 位置记不住不该影响隧道本身;下次拖动会再试一次。
            }
        }
    }

    /// <summary>构造时异步取回上次的位置。取不到就保持默认锚点。</summary>
    private void RestorePanelPosition()
    {
        if (_dataStore is null)
        {
            return;
        }
        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                PanelPosition? saved = await _dataStore
                                            .GetAsync<PanelPosition>(LayoutCollection, PanelPositionId)
                                            .ConfigureAwait(true);
                if (saved is null)
                {
                    return;
                }
                PanelOffsetX = saved.OffsetX;
                PanelOffsetY = saved.OffsetY;
            }
            catch
            {
                // 读不出来就用默认位置,不打扰用户。
            }
        }
    }

    /// <summary>打开面板时调用:刷新服务器列表并(可选)预选某台服务器。</summary>
    public async Task OpenAsync(Guid? preferredProfileId = null)
    {
        await LoadServersAsync();
        SessionProfile? preferred = preferredProfileId is { } id
                                        ? Servers.FirstOrDefault(p => p.Id == id)
                                        : null;
        if (preferred is not null)
        {
            SelectedServer = preferred;
        }
        else
        {
            SelectedServer ??= Servers.FirstOrDefault();
        }

        RefreshServerStatus();
    }

    /// <summary>
    /// 程序启动时调用一次:把勾了「程序启动时自动建立」的隧道按服务器逐台在后台连上、建好。
    /// <para>
    /// 其余隧道一条也不动 —— 启停状态从不落盘(持久化的只有 <see cref="TunnelConfig" />),
    /// 重启后一律是已停止,等用户手动启动。
    /// </para>
    /// <para>
    /// 连不上服务器、端口被占等失败经 <c>errorReporter</c> 报到右下角的错误提示,提示上带「重试」;用户取消认证则安静结束。
    /// 这时面板多半没开,写进表单下面没人看得见。一台服务器失败不耽误其它服务器;
    /// 同一台上一条建不起来也不耽误其余几条。从不抛出(调用方不等它)。
    /// </para>
    /// </summary>
    public async Task AutoStartTunnelsAsync()
    {
        if (_dataStore is null || _backgroundConnector is null)
        {
            return;
        }
        // 自动恢复(掉线重建)按 Servers 找服务器;面板还没开过时它是空的,这里先填上。
        await LoadServersAsync().ConfigureAwait(true);
        foreach (SessionProfile profile in Servers.ToList())
        {
            await RunAutoStartAsync(profile, only: null, userRequested: false).ConfigureAwait(true);
        }
    }

    /// <summary>跑一次「建好这台服务器上该自启的隧道」,并把失败报出去;从不抛出。</summary>
    /// <param name="profile">服务器。</param>
    /// <param name="only">只建这一条(提示上的「重试」点名的那条);<see langword="null" /> = 这台上勾了自启、还没起来的全部。</param>
    /// <param name="userRequested">这是用户在提示上点的「重试」,而不是程序启动时的那一轮。</param>
    private async Task RunAutoStartAsync(SessionProfile profile, TunnelItemViewModel? only, bool userRequested)
    {
        try
        {
            await AutoStartProfileAsync(profile, only, userRequested).ConfigureAwait(true);
        }
        catch (VelaSshAuthenticationCancelledException)
        {
            // 用户取消认证,不作为启动失败报告。
        }
        catch (Exception ex)
        {
            ReportAutoStartFailure(Strings.Format("Msg_TunnelAutoStartConnectFailed", profile.Name, FriendlyError(ex)), profile, only);
        }
    }

    /// <summary>
    /// 报一条自动建立的失败,提示上带「重试」。
    /// </summary>
    /// <remarks>
    /// 原先只是一句话:用户得打开面板、切到那台服务器、再点那条隧道的启动键 —— 而他看到这条提示时想做的就只有「再来一次」。
    /// 重试走的是同一条路(连不上就重连,哪条没建起来建哪条),只是由人点的:认证要人处理的那台照样会问他,
    /// 不受「之前取消过认证、自动的不再试」那一条拦着。
    /// </remarks>
    /// <param name="message">提示正文。</param>
    /// <param name="profile">失败的那台服务器。</param>
    /// <param name="tunnel">失败的那条隧道;连不上服务器时为 <see langword="null" />(重试这台上该自启的全部)。</param>
    private void ReportAutoStartFailure(string message, SessionProfile profile, TunnelItemViewModel? tunnel) =>
        _errorReporter?.Invoke(message, () => _ = RunAutoStartAsync(profile, tunnel, userRequested: true));

    /// <summary>建好一台服务器上勾了「程序启动时自动建立」的隧道。</summary>
    /// <param name="profile">服务器。</param>
    /// <param name="only">只建这一条;<see langword="null" /> = 这台上勾了自启、还没起来的全部。</param>
    /// <param name="userRequested">这是用户点的「重试」:不受「认证要人处理、自动的不再试」拦着;建成了报一声。</param>
    private async Task AutoStartProfileAsync(SessionProfile profile, TunnelItemViewModel? only, bool userRequested)
    {
        if (!_itemsByProfile.TryGetValue(profile.Id, out ObservableCollection<TunnelItemViewModel>? items))
        {
            items = [];
            _itemsByProfile[profile.Id] = items;
        }
        // 与切到这台服务器时同一份恢复(每台只恢复一次):之后打开面板,看到的就是这里建好的条目。
        await RestorePersistedTunnelsAsync(profile.Id, items).ConfigureAwait(true);
        if (!userRequested && _authenticationBlockedProfiles.Contains(profile.Id))
        {
            return;
        }
        // 点「重试」的那会儿,那条可能已经被用户在面板里启动(换成了新条目)、改过或删掉 —— 不在列表里了就不再建。
        List<TunnelItemViewModel> pending = only is null
            ? [.. items.Where(t => t is { AutoStart: true, IsActive: false, IsBusy: false })]
            : [.. items.Where(t => ReferenceEquals(t, only) && t is { IsActive: false, IsBusy: false })];
        if (pending.Count == 0)
        {
            return;
        }
        foreach (TunnelItemViewModel tunnel in pending)
        {
            tunnel.IsStarting = true;
        }
        try
        {
            Guid sessionId;
            try
            {
                sessionId = await EnsureSessionAsync(profile, automaticReconnect: false, CancellationToken.None).ConfigureAwait(true);
            }
            catch (VelaSshAuthenticationCancelledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ReportAutoStartFailure(Strings.Format("Msg_TunnelAutoStartConnectFailed", profile.Name, FriendlyError(ex)), profile, only);
                return;
            }
            List<string> started = [];
            foreach (TunnelItemViewModel tunnel in pending.Where(items.Contains))
            {
                try
                {
                    TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, tunnel.Config, CancellationToken.None).ConfigureAwait(true);
                    int index = items.IndexOf(tunnel);
                    if (index >= 0)
                    {
                        items[index] = new(result);
                    }
                    started.Add(tunnel.Name);
                }
                catch (VelaSshAuthenticationCancelledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ReportAutoStartFailure(Strings.Format("Msg_TunnelAutoStartFailed", tunnel.Name, profile.Name,
                        FriendlyError(ex, tunnel.Config.LocalPort)), profile, tunnel);
                }
            }
            if (started.Count == 0)
            {
                // 一条都没建起来:专为它们拨的后台连接没有存在的必要。
                await ReleaseHostIfUnusedAsync(profile.Id).ConfigureAwait(true);
            }
            else if (userRequested)
            {
                foreach (string name in started)
                {
                    _infoReporter?.Invoke(Strings.Format("Msg_TunnelAutoStartRetried", name, profile.Name));
                }
            }
        }
        finally
        {
            // 建成的已被新条目替换;没建成的留在列表里,键回到「启动」。
            foreach (TunnelItemViewModel tunnel in pending)
            {
                tunnel.IsStarting = false;
            }
            if (SelectedServer?.Id == profile.Id)
            {
                RefreshServerStatus();
            }
        }
    }

    private async Task LoadServersAsync()
    {
        if (_savedProfilesProvider is null)
        {
            return;
        }
        try
        {
            IReadOnlyList<SessionProfile> profiles = await _savedProfilesProvider();
            Guid? selectedId = SelectedServer?.Id;
            _isReloadingServers = true;
            try
            {
                Servers.Clear();
                // 隧道是 SSH 的能力(端口转发跑在 SSH 通道上),而保存的会话里还有 SFTP / FTP /
                // 插件协议 —— 它们进了下拉框只会让人选中一条永远连不起来的服务器。
                foreach (SessionProfile profile in profiles.Where(p => p.ConnectionType == ConnectionType.SSH))
                {
                    Servers.Add(profile);
                }
            }
            finally
            {
                _isReloadingServers = false;
            }
            if (selectedId is { } sid && Servers.FirstOrDefault(p => p.Id == sid) is { } reselected)
            {
                // 还是同一台服务器,只是仓库给了新实例:条目按 Id 缓存,换上新实例即可,不必重新加载;
                // 通知一声让下拉框重新选中它(Clear 时它已经把选中项丢了)。
                _selectedServer = reselected;
                this.RaisePropertyChanged(nameof(SelectedServer));
            }
            else
            {
                // 选中的服务器已被删掉:走正常的切换,把它的条目一并撤下。
                SelectedServer = null;
            }
        }
        catch
        {
            // 列表刷新失败保留旧数据。
        }
    }

    /// <summary>
    /// 面板开着时的每秒一拍:从服务同步读数,刷当前服务器各条隧道的流量行。
    /// 无 Avalonia 应用(单元测试)时没有计时器,测试直接调它。
    /// </summary>
    internal void RefreshTraffic()
    {
        try
        {
            _workflowService.RefreshStatistics();
        }
        catch
        {
            // 同 RefreshLiveState:服务正在拆除时读不到,下一拍再说。
        }
        foreach (TunnelItemViewModel tunnel in Tunnels)
        {
            tunnel.RefreshTraffic();
        }
    }

    // ---- 服务器/会话解析 ----

    /// <summary>面板持有的、仍然活着的后台会话;掉线的顺手清掉映射。</summary>
    private Guid? ResolveLiveSession(Guid profileId)
    {
        if (!_hostSessions.TryGetValue(profileId, out Guid sessionId))
        {
            return null;
        }
        if (_isSessionAlive?.Invoke(sessionId) == false)
        {
            _hostSessions.Remove(profileId);
            return null;
        }
        return sessionId;
    }

    /// <summary>取得(或后台建立)到指定服务器的隧道连接。</summary>
    /// <param name="profile">服务器。</param>
    /// <param name="automaticReconnect">这是断线后的自动恢复:主机指纹要人裁决时直接拒绝,不弹窗。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<Guid> EnsureSessionAsync(SessionProfile profile, bool automaticReconnect, CancellationToken ct)
    {
        if (ResolveLiveSession(profile.Id) is { } alive)
        {
            return alive;
        }
        if (_backgroundConnector is null)
        {
            throw new InvalidOperationException(Strings.Get("Msg_BackgroundConnectorNotConfigured"));
        }
        _isConnectingHost = true;
        RefreshServerStatus();
        try
        {
            Guid sessionId = await _backgroundConnector(profile, automaticReconnect, ct);
            _hostSessions[profile.Id] = sessionId;
            _hostKeyRejectedProfiles.Remove(profile.Id);
            _authenticationBlockedProfiles.Remove(profile.Id);
            return sessionId;
        }
        catch (Exception ex) when (ex is VelaSshTailscaleAuthenticationException { RequiresUserAction: true }
                                       or VelaSshAuthenticationCancelledException)
        {
            _authenticationBlockedProfiles.Add(profile.Id);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _authenticationBlockedProfiles.Add(profile.Id);
            throw;
        }
        catch (VelaSshHostKeyRejectedException)
        {
            // 用户在弹窗里拒绝了,或者自动恢复时没法问人:之后不再自动恢复这台,等用户手动启动。
            _hostKeyRejectedProfiles.Add(profile.Id);
            throw;
        }
        finally
        {
            _isConnectingHost = false;
            RefreshServerStatus();
        }
    }

    /// <summary>该服务器最后一条隧道删除后,后台连接没有存在的必要,自动断开。</summary>
    private async Task ReleaseHostIfUnusedAsync(Guid profileId)
    {
        if (!_hostSessions.TryGetValue(profileId, out Guid sessionId))
        {
            return;
        }
        bool hasTunnels;
        try
        {
            hasTunnels = _workflowService.GetActiveTunnels(sessionId).Count > 0;
        }
        catch
        {
            hasTunnels = false;
        }
        if (hasTunnels)
        {
            return;
        }
        _hostSessions.Remove(profileId);
        if (_sessionDisconnector is not null)
        {
            try
            {
                await _sessionDisconnector(sessionId);
            }
            catch
            {
                // 会话可能已经掉线。
            }
        }
        RefreshServerStatus();
    }

    private void OnServerChanged()
    {
        ErrorMessage = null;
        if (IsEditing)
        {
            ResetForm();
        }
        if (SelectedServer is { } server)
        {
            if (!_itemsByProfile.TryGetValue(server.Id, out ObservableCollection<TunnelItemViewModel>? items))
            {
                items = [];
                _itemsByProfile[server.Id] = items;

                // 找回该服务器后台会话上已建的隧道(面板重开/重建的场景)。
                if (ResolveLiveSession(server.Id) is { } sessionId)
                {
                    try
                    {
                        IReadOnlyList<TunnelInfo> existing = _workflowService.GetActiveTunnels(sessionId);
                        foreach (TunnelInfo info in existing.OrderBy(t => t.CreatedAt))
                        {
                            items.Add(new(info));
                        }
                    }
                    catch
                    {
                        // 找不回历史隧道不影响面板可用性。
                    }
                }

                // 恢复上次运行持久化的隧道配置(以"已停止"状态展示,由用户手动启动)。
                _ = RestorePersistedTunnelsAsync(server.Id, items);
            }
            Tunnels = items;
        }
        else
        {
            Tunnels = [];
        }
        RefreshServerStatus();
    }

    private void RefreshServerStatus()
    {
        this.RaisePropertyChanged(nameof(ServerStatusText));
        this.RaisePropertyChanged(nameof(IsServerConnected));
    }

    /// <summary>
    /// 状态时钟的一拍:同步流量读数、刷新条目运行时长与服务侧状态;后台会话掉线时把条目
    /// 标为已停止,并为勾了「自动重连」的隧道排一次恢复。
    /// <para>
    /// 由面板自己的 5 秒计时器驱动。无 Avalonia 应用(单元测试)时没有计时器,
    /// 测试直接调它来推进一拍。
    /// </para>
    /// </summary>
    internal void RefreshLiveState()
    {
        DetectDroppedSessions();
        ScheduleAutoReconnect();
        try
        {
            _workflowService.RefreshStatistics();
        }
        catch
        {
            // 读数同步失败(服务正在拆除)不该让整个时钟停摆。
        }
        foreach (TunnelItemViewModel tunnel in Tunnels)
        {
            tunnel.RefreshLive();
        }
        RefreshServerStatus();
    }

    /// <summary>
    /// 检出掉线的后台会话并把其隧道条目标为已停止。
    /// <para>
    /// 查的是面板持有的**每一台**服务器,而不只是当前选中的那台:隧道跑在后台,
    /// 用户不会为了让某台的隧道被照看到而一直停在它的页面上。
    /// </para>
    /// </summary>
    private void DetectDroppedSessions()
    {
        if (_isSessionAlive is null)
        {
            return;
        }
        foreach (Guid profileId in _hostSessions.Keys.ToList())
        {
            if (!_hostSessions.TryGetValue(profileId, out Guid sessionId) || _isSessionAlive(sessionId))
            {
                continue;
            }
            _hostSessions.Remove(profileId);
            if (!_itemsByProfile.TryGetValue(profileId, out ObservableCollection<TunnelItemViewModel>? items))
            {
                continue;
            }
            foreach (TunnelItemViewModel tunnel in items)
            {
                tunnel.Status = TunnelStatus.Stopped;
            }
        }
    }

    // ---- 断线自动恢复 ----

    /// <summary>
    /// 为每台「有隧道勾了自动重连、且后台连接已断」的服务器排一次恢复。
    /// 失败按指数退避,避免服务器真的下线时每 5 秒重拨一次、把凭据提示刷满屏幕。
    /// </summary>
    private void ScheduleAutoReconnect()
    {
        if (_backgroundConnector is null)
        {
            return;
        }
        foreach ((Guid profileId, ObservableCollection<TunnelItemViewModel> items) in _itemsByProfile)
        {
            if (_autoReconnectingProfiles.Contains(profileId) || ResolveLiveSession(profileId) is not null)
            {
                continue;
            }
            // 指纹或认证需要用户处理时,仅显式手动连接可以再试。
            if (_hostKeyRejectedProfiles.Contains(profileId) || _authenticationBlockedProfiles.Contains(profileId))
            {
                continue;
            }
            // 主密码锁着时拦下的:等解锁(RetryAfterUnlock),不每 5 秒再撞一次。
            if (_lockedProfiles.Contains(profileId))
            {
                continue;
            }
            if (!items.Any(IsAutoReconnectCandidate))
            {
                continue;
            }
            if (_autoReconnectNotBefore.TryGetValue(profileId, out DateTime notBefore) && DateTime.UtcNow < notBefore)
            {
                continue;
            }
            SessionProfile? profile = Servers.FirstOrDefault(p => p.Id == profileId);
            if (profile is null)
            {
                continue;
            }
            _autoReconnectingProfiles.Add(profileId);
            _ = AutoReconnectAsync(profile, items);
        }
    }

    /// <summary>
    /// 该由自动恢复重建的隧道:勾了自动重连、已停、不是用户按停的,且没有正在启停
    /// (用户手动点了启动、还没走完的那条不该再被重建一次)。
    /// </summary>
    private static bool IsAutoReconnectCandidate(TunnelItemViewModel tunnel) =>
        tunnel is { AutoReconnect: true, IsActive: false, StoppedByUser: false, IsBusy: false };

    /// <summary>重连服务器并按原配置重建其勾了自动重连的隧道。</summary>
    private async Task AutoReconnectAsync(SessionProfile profile, ObservableCollection<TunnelItemViewModel> items)
    {
        bool isSelected = SelectedServer?.Id == profile.Id;
        // 重拨加重建要等一会儿:这几条的启停键同样换成转圈,免得用户在途中再点一次启动。
        List<TunnelItemViewModel> pending = [.. items.Where(IsAutoReconnectCandidate)];
        foreach (TunnelItemViewModel tunnel in pending)
        {
            tunnel.IsStarting = true;
        }
        try
        {
            if (isSelected)
            {
                RefreshServerStatus();
            }
            Guid sessionId = await EnsureSessionAsync(profile, automaticReconnect: true, CancellationToken.None).ConfigureAwait(true);
            foreach (TunnelItemViewModel tunnel in pending.Where(items.Contains))
            {
                // 服务侧还留着停止状态的旧记录,先清掉再重建,避免列表里越积越多。
                await _workflowService.RemoveTunnelAsync(tunnel.Id, CancellationToken.None).ConfigureAwait(true);
                TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, tunnel.Config, CancellationToken.None).ConfigureAwait(true);
                int index = items.IndexOf(tunnel);
                if (index >= 0)
                {
                    items[index] = new(result);
                }
            }
            _autoReconnectNotBefore.Remove(profile.Id);
            _autoReconnectFailures.Remove(profile.Id);
            if (isSelected)
            {
                ErrorMessage = null;
            }
        }
        catch (VelaSshAuthenticationCancelledException)
        {
            // EnsureSessionAsync 已暂停这台服务器的自动恢复;取消不报错也不进入退避。
        }
        catch (VelaSshTailscaleAuthenticationException ex) when (ex.RequiresUserAction)
        {
            // 认证途中断线、没等到验证链接就超时不在此列:落到下面的通用分支退避重试。
            _errorReporter?.Invoke(ex.Message, null);
            if (isSelected)
            {
                ErrorMessage = ex.Message;
            }
        }
        catch (VelaSshHostKeyRejectedException ex)
        {
            // 自动恢复到此为止(EnsureSessionAsync 已把这台记进 _hostKeyRejectedProfiles)。
            // 不管当前选中的是不是这台都报一声:只有用户手动启动、在弹窗里核对过指纹,这些隧道才回得来。
            _errorReporter?.Invoke(FriendlyError(ex), null);
            if (isSelected)
            {
                ErrorMessage = FriendlyError(ex);
            }
        }
        catch (Core.Data.SecretsLockedException ex)
        {
            // 主密码锁着:自动重建不弹解锁框,也不进退避 —— 解锁后由 RetryAfterUnlock 各重试一次。
            _lockedProfiles.Add(profile.Id);
            if (isSelected)
            {
                ErrorMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            int failures = _autoReconnectFailures.GetValueOrDefault(profile.Id) + 1;
            _autoReconnectFailures[profile.Id] = failures;
            _autoReconnectNotBefore[profile.Id] = DateTime.UtcNow + BackoffFor(failures);
            if (isSelected)
            {
                ErrorMessage = Strings.Format("Msg_TunnelAutoReconnectFailed", FriendlyError(ex));
            }
        }
        finally
        {
            // 重建成功的已被新条目替换;没轮到或失败的留在列表里,键回到「启动」。
            foreach (TunnelItemViewModel tunnel in pending)
            {
                tunnel.IsStarting = false;
            }
            _autoReconnectingProfiles.Remove(profile.Id);
            if (isSelected)
            {
                RefreshServerStatus();
            }
        }
    }

    /// <summary>主密码锁着时被拦下自动重建的服务器。</summary>
    private readonly HashSet<Guid> _lockedProfiles = [];

    /// <summary>主密码解锁之后:锁着时被拦下的自动重建各重试一次。</summary>
    internal void RetryAfterUnlock()
    {
        if (_lockedProfiles.Count == 0)
        {
            return;
        }
        _lockedProfiles.Clear();
        ScheduleAutoReconnect();
    }

    /// <summary>失败重试的退避间隔:10s → 30s → 1min → 2min → 5min 封顶。</summary>
    private static TimeSpan BackoffFor(int failures) =>
        failures switch
        {
            1 => TimeSpan.FromSeconds(10),
            2 => TimeSpan.FromSeconds(30),
            3 => TimeSpan.FromMinutes(1),
            4 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };

    // ---- 配置持久化(仅配置,不含运行状态;重启后恢复为"已停止",由用户手动启动) ----

    /// <summary>配置身份键:恢复时用来跳过面板里已存在的同配置条目。</summary>
    private static string ConfigKey(TunnelConfig config) =>
        $"{config.Type}|{config.LocalHost}|{config.LocalPort}|{config.RemoteHost}|{config.RemotePort}|{config.Name}|{RemoteOpenRules.Format(config.PermitRemoteOpen)}|{config.LocalSocketPath}|{config.RemoteSocketPath}";

    /// <summary>从存储恢复该服务器的隧道配置(每次运行每台只执行一次);失败不影响面板。</summary>
    private async Task RestorePersistedTunnelsAsync(Guid profileId, ObservableCollection<TunnelItemViewModel> items)
    {
        if (_dataStore is null || !_restoredProfiles.Add(profileId))
        {
            return;
        }
        try
        {
            List<TunnelConfig>? saved = await _dataStore
                                              .GetAsync<List<TunnelConfig>>(TunnelCollection, profileId.ToString("D"))
                                              .ConfigureAwait(true);
            if (saved is not { Count: > 0 })
            {
                return;
            }
            HashSet<string> existing = [.. items.Select(t => ConfigKey(t.Config))];
            foreach (TunnelConfig config in saved)
            {
                if (!existing.Add(ConfigKey(config)))
                {
                    continue;
                }
                items.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = config,
                    Status = TunnelStatus.Stopped,
                    SessionId = Guid.Empty,
                    CreatedAt = DateTime.UtcNow,
                    BytesTransferred = 0
                }));
            }
        }
        catch
        {
            // 恢复失败(存储损坏/旧格式)不影响面板可用性,后续保存会覆盖为新格式。
        }
    }

    /// <summary>把该服务器当前的隧道配置快照写入存储;在创建/编辑/删除后调用,失败不打断操作。</summary>
    private void PersistTunnels(Guid profileId)
    {
        if (_dataStore is null || !_itemsByProfile.TryGetValue(profileId, out ObservableCollection<TunnelItemViewModel>? items))
        {
            return;
        }
        // 先在调用线程(UI)拍快照再落盘,避免后台线程枚举 ObservableCollection。
        var configs = items.Select(t => t.Config).ToList();
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await _dataStore.UpsertAsync(TunnelCollection, profileId.ToString("D"), configs).ConfigureAwait(false);
            }
            catch
            {
                // 落盘失败不影响运行中的隧道;下次变更会再次尝试。
            }
        }
    }

    // ---- 表单/条目操作 ----

    private void BeginEdit(Guid tunnelId)
    {
        TunnelItemViewModel? item = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
        if (item is null)
        {
            return;
        }
        if (item.IsActive || item.IsBusy)
        {
            return;
        }
        _editingTunnelId = tunnelId;
        NewTunnelName = item.Config.Name;
        NewLocalHost = item.Config.LocalHost;
        // 远程动态转发不用本地端口(存的是 0):表单那一栏保持原样,换回别的类型时不至于是个非法的 0。
        if (item.Config.LocalPort != 0)
        {
            NewLocalPort = (int)item.Config.LocalPort;
        }
        NewTunnelType = item.TunnelType;
        NewPermitRemoteOpen = RemoteOpenRules.Format(item.Config.PermitRemoteOpen);
        NewUseUnixSocket = item.Config.LocalSocketPath is not null || item.Config.RemoteSocketPath is not null;
        NewLocalSocketPath = item.Config.LocalSocketPath ?? string.Empty;
        NewRemoteSocketPath = item.Config.RemoteSocketPath ?? string.Empty;
        ForwardToServerLoopback = item.TunnelType != TunnelType.DynamicForward && item.Config.RemoteHost is "" or "127.0.0.1" or "localhost";
        if (!ForwardToServerLoopback)
        {
            NewRemoteHost = item.Config.RemoteHost;
        }
        NewRemotePort = (int)item.Config.RemotePort;
        NewAutoReconnect = item.Config.AutoReconnect;
        NewAutoStart = item.Config.AutoStart;
        // 配置里存的是字节 / 秒;表单按 KB/s 填,不整除时向上取整(别把限速悄悄放宽成 0 = 不限)。
        NewRateLimitKiB = item.Config.MaxBytesPerSecond is { } bytesPerSecond
            ? (int)Math.Min(MaxRateLimitKiB, Math.Max(1, (bytesPerSecond + 1023) / 1024))
            : 0;
        ErrorMessage = null;
        RaiseEditingChanged();
    }

    /// <summary>创建新隧道,或保存编辑(旧隧道先停止移除,再按新配置重建)。</summary>
    private async Task SubmitAsync(CancellationToken ct)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }
        TunnelItemViewModel? existing = null;
        try
        {
            ErrorMessage = null;
            TunnelConfig config = BuildConfig();
            if (_editingTunnelId is { } editingId)
            {
                existing = Tunnels.FirstOrDefault(t => t.Id == editingId);
                int index = existing is null ? -1 : Tunnels.IndexOf(existing);
                bool wasActive = existing?.IsActive ?? true;
                if (wasActive)
                {
                    existing?.IsStarting = true;
                    Guid sessionId = await EnsureSessionAsync(server, automaticReconnect: false, ct).ConfigureAwait(true);
                    ct.ThrowIfCancellationRequested();
                    await _workflowService.RemoveTunnelAsync(editingId, ct).ConfigureAwait(true);
                    TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, config, ct).ConfigureAwait(true);
                    ReplaceOrAdd(index, new(result));
                }
                else
                {
                    await _workflowService.RemoveTunnelAsync(editingId, ct).ConfigureAwait(true);
                    // 停止状态下编辑:只更新配置,不自动拉起。
                    var stopped = new TunnelInfo
                    {
                        Id = Guid.NewGuid(),
                        Config = config,
                        Status = TunnelStatus.Stopped,
                        SessionId = ResolveLiveSession(server.Id) ?? Guid.Empty,
                        CreatedAt = DateTime.UtcNow,
                        BytesTransferred = 0
                    };
                    ReplaceOrAdd(index, new(stopped));
                }
            }
            else
            {
                Guid sessionId = await EnsureSessionAsync(server, automaticReconnect: false, ct).ConfigureAwait(true);
                TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, config, ct).ConfigureAwait(true);
                Tunnels.Add(new(result));
            }
            PersistTunnels(server.Id);
            ResetForm();
        }
        catch (VelaSshAuthenticationCancelledException)
        {
            // 留在当前表单和旧条目上,用户可以手动重试。
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消同样保留表单、存储配置和服务侧旧记录。
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
        finally
        {
            existing?.IsStarting = false;
        }
    }

    private void ReplaceOrAdd(int index, TunnelItemViewModel item)
    {
        if (index >= 0 && index < Tunnels.Count)
        {
            Tunnels[index] = item;
        }
        else
        {
            Tunnels.Add(item);
        }
    }

    private TunnelConfig BuildConfig()
    {
        bool isDynamic = NewTunnelType == TunnelType.DynamicForward;
        bool isRemoteDynamic = NewTunnelType == TunnelType.RemoteDynamicForward;
        bool loopback = !isDynamic && ForwardToServerLoopback;
        IReadOnlyList<string>? permit = isRemoteDynamic && RemoteOpenRules.TryParse(NewPermitRemoteOpen, out IReadOnlyList<string> rules, out _)
            ? rules
            : null;
        return new()
        {
            Type = NewTunnelType,
            // 别名可选(设计 B3Rth):留空时用路由描述兜底,列表里始终有可读名称。
            Name = NewTunnelName.Trim(),
            LocalHost = NewLocalHost.Trim(),
            LocalPort = isRemoteDynamic ? 0u : (uint)NewLocalPort,
            RemoteHost = isDynamic
                             ? string.Empty
                             : loopback
                                 ? "127.0.0.1"
                                 : NewRemoteHost.Trim(),
            RemotePort = isDynamic ? 0u : (uint)NewRemotePort,
            AutoReconnect = NewAutoReconnect,
            AutoStart = NewAutoStart,
            MaxBytesPerSecond = NewRateLimitKiB > 0 ? NewRateLimitKiB * 1024L : null,
            PermitRemoteOpen = permit,
            LocalSocketPath = UsesLocalSocket ? NewLocalSocketPath.Trim() : null,
            RemoteSocketPath = UsesRemoteSocket ? NewRemoteSocketPath.Trim() : null
        };
    }

    private async Task StopTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        TunnelItemViewModel? tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
        // 启停途中再点一次(键已换成转圈,这里兜住键盘 / 命令的路径)不再重复下发。
        if (tunnel is { IsBusy: true })
        {
            return;
        }
        tunnel?.IsStopping = true;
        try
        {
            ErrorMessage = null;
            await _workflowService.StopTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            if (tunnel is not null)
            {
                tunnel.Status = TunnelStatus.Stopped;
                // 记下"是用户按停的",免得自动恢复过一会儿把它又拉起来。
                tunnel.StoppedByUser = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
        finally
        {
            tunnel?.IsStopping = false;
        }
    }

    /// <summary>启动一条已停止的隧道:必要时先后台连上服务器,再按原配置重建转发。</summary>
    private async Task StartTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }
        TunnelItemViewModel? existing = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
        // 自动恢复正在重建它,或者上一次点的还没走完:不再重复建一条。
        if (existing is null || existing.IsBusy)
        {
            return;
        }
        existing.IsStarting = true;
        try
        {
            ErrorMessage = null;
            Guid sessionId = await EnsureSessionAsync(server, automaticReconnect: false, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            // 认证成功后才清除服务侧旧记录;取消认证仍保留可重试的隧道定义。
            await _workflowService.RemoveTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, existing.Config, ct).ConfigureAwait(true);
            int index = Tunnels.IndexOf(existing);
            ReplaceOrAdd(index, new(result));
        }
        catch (VelaSshAuthenticationCancelledException)
        {
            // 取消认证不报连接失败,旧条目由 finally 复位后可再次启动。
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消不报错,也不移除旧隧道定义。
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex, existing.Config.LocalPort);
        }
        finally
        {
            // 成功时这一条已被重建出来的新条目替换,复位的是旧对象,无害;失败时它留在列表里,键回到「启动」。
            existing.IsStarting = false;
        }
    }

    private async Task DeleteTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        TunnelItemViewModel? tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
        // 启停途中不让删:删掉的条目会被随后完成的启动又加回来,或者停到一半的连接没人收拾。
        if (tunnel is null || tunnel.IsBusy || ConfirmDelete is null)
        {
            return;
        }
        bool isDeleteInFlight;
        lock (_deletingTunnelIdsGate)
        {
            isDeleteInFlight = !_deletingTunnelIds.Add(tunnelId);
        }
        if (isDeleteInFlight)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            if (!await ConfirmDelete(Strings.Get("Tunnel_DeleteConfirmationBody")).ConfigureAwait(true))
            {
                return;
            }
            tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (tunnel is null)
            {
                return;
            }
            await _workflowService.RemoveTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            Tunnels.Remove(tunnel);
            if (_editingTunnelId == tunnelId)
            {
                ResetForm();
            }
            if (SelectedServer is { } server)
            {
                PersistTunnels(server.Id);
                await ReleaseHostIfUnusedAsync(server.Id).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
        finally
        {
            lock (_deletingTunnelIdsGate)
            {
                _deletingTunnelIds.Remove(tunnelId);
            }
        }
    }

    /// <summary>
    /// 把服务层异常翻译成用户能看懂的提示。<paramref name="localPort" /> 是出错那条隧道的本地端口;
    /// 不给时取表单里的(创建 / 保存走的就是表单)—— 启动一条已有的隧道时表单里可能是另一个端口,
    /// 拿它报「端口被占用」会指错地方。
    /// </summary>
    private string FriendlyError(Exception ex, uint? localPort = null) =>
        ex switch
        {
            VelaSshTailscaleAuthenticationException => ex.Message,
            // 本机监听没起来:服务层已经说清在哪儿、为什么。排在端口被占之前 —— 套接字路径上的「地址已被占用」不是端口的事。
            TunnelBindFailedException => ex.Message,
            OperationCanceledException => Strings.Get("Msg_OperationCancelled"),
            InvalidOperationException when ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                => Strings.Get("Msg_ServerConnectionUnavailable"),
            _ when IsAddressInUse(ex) => Strings.Format("Msg_LocalPortInUse", localPort ?? (uint)NewLocalPort),
            _ => ex.Message
        };

    /// <summary>
    /// 端口被占用:沿 InnerException 链找 —— 绑定失败被 SSH 库包成转发异常、
    /// 又被宿主的异常翻译再包一层,只看最外层的话这条本地化提示永远出不来。
    /// </summary>
    private static bool IsAddressInUse(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }
        return false;
    }

    private void ResetForm()
    {
        _editingTunnelId = null;
        NewTunnelName = string.Empty;
        NewLocalHost = "127.0.0.1";
        NewLocalPort = 27017;
        NewRemotePort = 27017;
        NewAutoReconnect = false;
        NewAutoStart = false;
        NewRateLimitKiB = 0;
        NewPermitRemoteOpen = string.Empty;
        NewUseUnixSocket = false;
        NewLocalSocketPath = string.Empty;
        NewRemoteSocketPath = string.Empty;
        NewTunnelTypeIndex = 0;
        ForwardToServerLoopback = true; // 同时把目标主机复位为 127.0.0.1
        RaiseEditingChanged();
    }

    private void RaiseEditingChanged()
    {
        this.RaisePropertyChanged(nameof(IsEditing));
        this.RaisePropertyChanged(nameof(FormTitle));
        this.RaisePropertyChanged(nameof(SubmitButtonText));
    }
}
