using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Core.Data;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.XServer;
using AppXServerOptions = VelaShell.Core.Models.XServerOptions;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// <see cref="ILocalXServer" /> 的内置实现:在进程里跑 <see cref="X11Server" />(<c>src/VelaShell.XServer</c>),
/// 顶层窗口由 <see cref="IEmbeddedXServerHost" />(界面层)画成原生窗口。
/// </summary>
/// <remarks>
/// <para>
/// 不拉外部进程、不装任何东西,各平台都能用。监听环回上的 TCP <c>6000+N</c> 与(类 Unix 上)<c>/tmp/.X11-unix/XN</c>,
/// 本机别的 X 程序照样能用 <c>DISPLAY=localhost:N</c> 连进来;SSH 的 x11 通道则经
/// <see cref="XServerDisplayResolution.Connector" /> 直接接进服务端,不绕本机端口。
/// </para>
/// <para>
/// <b>授权</b>:每次启动生成一个随机的 <c>MIT-MAGIC-COOKIE-1</c>,TCP 连接(包括环回 —— 本机别的进程、别的用户都连得到那个端口)
/// 必须带上它;cookie 写进用户的 <c>.Xauthority</c>(<see cref="XAuthorityFile" />,停下时撤出),本机 X 程序经 Xlib 自动带上。
/// Unix 套接字只有同一个用户连得进来,不要 cookie。SSH 的 x11 通道经连接器进来,转发层已经核对过远端的假 cookie,
/// 走 <see cref="X11Server.ServeAuthenticatedAsync(Stream, string?, CancellationToken)" />。
/// </para>
/// <para>
/// <b>每个 SSH 会话一个显示</b>(设置 <see cref="AppXServerOptions.DisplayPerSession" />,默认关):开着时,带着会话对象
/// (<see cref="XServerChannelSource.Session" />)进来的 x11 通道不进共用的服务端,而进这个会话自己的一个 <see cref="X11Server" />
/// —— 不监听任何端口、只经连接器喂流,有自己的根窗口、选区与剪贴板、XTEST 只碰得到自己;各配一个宿主(窗口、键位表、DPI、
/// 显示器布局、剪贴板各管各的)。第一条通道来时建,会话断开(<see cref="XServerChannelSource.SessionEnded" />)或停服时收掉;
/// 至多 <see cref="MaxSessionDisplays" /> 个。本机程序(<c>DISPLAY=:N</c>)照旧连共用的那个。
/// </para>
/// <para>
/// 状态变化(<see cref="StateChanged" />)在调用启动 / 停止的那个线程上触发,界面侧自己切回 UI 线程。
/// </para>
/// </remarks>
public sealed class BuiltInLocalXServer : ILocalXServer, IAsyncDisposable, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly Func<IEmbeddedXServerHost?> _host;
    private readonly Func<int, CancellationToken, Task<bool>> _isDisplayInUse;
    private readonly Func<CancellationToken, Task<bool>> _hasOtherDisplay;
    private readonly string? _xauthorityPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();

    private X11Server? _server;
    private IEmbeddedXServerHost? _attached;

    /// <summary>每成功启动一次加一:客户端的键(<see cref="XServerClient.Key" />)带着它,停了再开之后旧键不会断开新服务端上同编号的程序。</summary>
    private int _generation;
    private int? _displayNumber;
    private XServerState _state = XServerState.Stopped;
    private bool _disposed;

    /// <summary>写进 .Xauthority 的那一条(停下时按它撤);没写成为 null。</summary>
    private (string Path, string Host, int Display, byte[] Cookie)? _published;

    /// <summary>按会话分显示时同时开着的会话显示上限:每个一条执行线程、一份根窗口与宿主。超了新会话的 X 程序连不上(不落回共用的显示)。</summary>
    internal const int MaxSessionDisplays = 32;

    /// <summary>这一次启动时的设置(按会话建显示时照它配);没在运行时为 null。只在 <see cref="_stateLock" /> 里读写。</summary>
    private AppXServerOptions? _runningOptions;

    /// <summary>按会话分出来的显示(键是会话对象,按引用比);值是建它的任务。只在 <see cref="_stateLock" /> 里改字典。</summary>
    private readonly Dictionary<object, Task<SessionDisplay>> _sessionDisplays = new(ReferenceEqualityComparer.Instance);

    /// <summary>下一个实例的编号(共用的那个是 0):客户端清单用它与客户端编号合成键,收掉的实例的编号不再复用。</summary>
    private int _nextInstanceIndex = 1;

    /// <summary>一个按会话分出来的显示:编号、会话的来历、服务端与它的宿主,以及「会话断开就收掉」的登记。</summary>
    private sealed record SessionDisplay(int Index, string? Label, X11Server Server, IEmbeddedXServerHost Host)
    {
        public CancellationTokenRegistration Ended { get; set; }
    }

    /// <summary>此刻在运行的一个服务端实例(见 <see cref="Instances" />)。</summary>
    /// <param name="Index">编号:共用的那个是 0,按会话分出来的从 1 起,停服之前不复用。</param>
    /// <param name="Label">按会话分出来的那个会话的来历(<c>user@host:port</c>);共用的那个为 null。</param>
    /// <param name="Server">服务端。</param>
    internal sealed record XServerInstance(int Index, string? Label, X11Server Server);

    /// <summary>
    /// 此刻在运行的全部服务端实例:共用的那个排第一(编号 0),之后是按会话分出来的(建好了的)。没在运行时为空。
    /// 客户端清单、强制结束、解除卡住的抓取这类「对所有 X 程序」的动作照它逐个做。
    /// </summary>
    internal IReadOnlyList<XServerInstance> Instances
    {
        get
        {
            lock (_stateLock)
            {
                if (_state != XServerState.Running || _server is not { } shared)
                {
                    return [];
                }
                List<XServerInstance> all = [new(0, null, shared)];
                all.AddRange(_sessionDisplays.Values
                    .Where(t => t.IsCompletedSuccessfully)
                    .Select(t => t.Result)
                    .OrderBy(d => d.Index)
                    .Select(d => new XServerInstance(d.Index, d.Label, d.Server)));
                return all;
            }
        }
    }

    /// <summary>构造。</summary>
    /// <param name="settings">设置服务(显示号、剪贴板、自动启动)。</param>
    /// <param name="host">取宿主;<see langword="null" /> 表示界面层没有提供,启动会失败。</param>
    public BuiltInLocalXServer(ISettingsService settings, Func<IEmbeddedXServerHost?> host)
        : this(settings, host, XDisplayProbe.IsInUseAsync, HasOtherDisplayAsync, VelaShell.Ssh.Forwarding.XAuthority.DefaultPath)
    {
    }

    /// <summary>可注入显示探测、.Xauthority 的位置与本机主机名(单测用;<paramref name="xauthorityPath" /> 为 null 时不写)。</summary>
    internal BuiltInLocalXServer(
        ISettingsService settings,
        Func<IEmbeddedXServerHost?> host,
        Func<int, CancellationToken, Task<bool>> isDisplayInUse,
        Func<CancellationToken, Task<bool>> hasOtherDisplay,
        string? xauthorityPath = null,
        Func<string?>? hostName = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _isDisplayInUse = isDisplayInUse;
        _hasOtherDisplay = hasOtherDisplay;
        _xauthorityPath = xauthorityPath;
        _hostName = hostName ?? HostName;
    }

    /// <summary>本机主机名(.Xauthority 记录的地址;Xlib 连本机时按它找)。</summary>
    private readonly Func<string?> _hostName;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public XServerState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public int? DisplayNumber
    {
        get
        {
            lock (_stateLock)
            {
                return _state == XServerState.Running ? _displayNumber : null;
            }
        }
    }

    /// <inheritdoc />
    public string? Display => DisplayNumber is { } number ? XServerCommandLine.DisplayAddress(number) : null;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <summary>内置引擎没有可执行文件可找。</summary>
    public string? FindExecutable(string? configuredPath) => null;

    /// <inheritdoc />
    public async Task<XServerStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == XServerState.Running)
            {
                return XServerStartResult.Ok;
            }
            AppXServerOptions options = (await _settings.GetSnapshotAsync().ConfigureAwait(false)).XServer;
            return await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>自动选号时,启动失败(号被占)最多换这么多次号。</summary>
    private const int MaxStartAttempts = 4;

    private async Task<XServerStartResult> StartCoreAsync(AppXServerOptions options, CancellationToken cancellationToken)
    {
        if (_host() is not { } host)
        {
            return XServerStartResult.Fail(Strings.Get("XServer_ErrNoHost"));
        }

        int display;
        if (options.DisplayNumber >= 0)
        {
            display = options.DisplayNumber;
            if (await _isDisplayInUse(display, cancellationToken).ConfigureAwait(false))
            {
                return XServerStartResult.Fail(Strings.Format("XServer_ErrDisplayInUse", display));
            }
        }
        else if (await VcXsrvLocalXServer.SelectFreeDisplayAsync(_isDisplayInUse, cancellationToken).ConfigureAwait(false) is { } free)
        {
            display = free;
        }
        else
        {
            return XServerStartResult.Fail(Strings.Format("XServer_ErrNoFreeDisplay", AppXServerOptions.MaxDisplayNumber));
        }

        SetState(XServerState.Starting, display);
        X11Server server;
        byte[] cookie;
        for (int attempt = 1; ; attempt++)
        {
            X11Server? candidate = null;
            try
            {
                cookie = RandomNumberGenerator.GetBytes(16);
                candidate = new(ServerOptions(options, display, cookie, label: null), host);
                // 先让宿主把显示器布局、DPI、键盘布局告诉服务端,再开门 —— 第一个客户端拿到的就是对的屏幕与键位表。
                host.UseKeyboardLayout(options.KeyboardLayout);
                host.UseWindowMode(options.WindowMode);
                await host.AttachAsync(candidate, cancellationToken).ConfigureAwait(false);
                await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
                server = candidate;
                break;
            }
            catch (Exception ex)
            {
                // 一律收尾:原先只接这三类(SocketException、取消、InvalidOperationException),别的异常(宿主附着时抛的、库的参数校验)
                // 一路抛出去,服务端不释放、状态卡在「启动中」,X Server 按钮再也点不动。
                host.Detach();
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
                // 自动选号时,探测说空着的号在绑定时被占了(探测与绑定之间别的程序抢先了;或者别的服务端持着 /tmp/.X{N}-lock、
                // 抽象名被占 —— 探测看不出来,服务端开的时候才知道):换下一个空闲的号再试。原先直接报「显示号被占用」。
                if (ex is SocketException && options.DisplayNumber < 0 && attempt < MaxStartAttempts
                    && await VcXsrvLocalXServer.SelectFreeDisplayAsync(_isDisplayInUse, cancellationToken, display + 1).ConfigureAwait(false) is { } next)
                {
                    Trace.WriteLine($"[XServer] display :{display} was taken while starting; trying :{next}");
                    display = next;
                    SetState(XServerState.Starting, display);
                    continue;
                }
                SetStopped();
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                return XServerStartResult.Fail(ex is SocketException
                    ? Strings.Format("XServer_ErrDisplayInUse", display)
                    : Strings.Format("XServer_ErrLaunch", ex.Message));
            }
        }

        PublishCookie(display, cookie);
        lock (_stateLock)
        {
            _server = server;
            _attached = host;
            _runningOptions = options;
            _state = XServerState.Running;
            _generation++;
        }
        host.GrabStallReported += OnGrabStallReported;
        Trace.WriteLine($"[XServer] built-in server listening on :{display}");
        RaiseStateChanged();
        return XServerStartResult.Ok;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        X11Server? server;
        IEmbeddedXServerHost? host;
        List<Task<SessionDisplay>> sessions;
        lock (_stateLock)
        {
            server = _server;
            host = _attached;
            _server = null;
            _attached = null;
            _runningOptions = null;
            sessions = [.. _sessionDisplays.Values];
            _sessionDisplays.Clear();
        }
        foreach (Task<SessionDisplay> session in sessions)
        {
            await CloseSessionDisplayAsync(session).ConfigureAwait(false);
        }
        if (server is null)
        {
            return;
        }
        host?.GrabStallReported -= OnGrabStallReported;
        host?.Detach();
        await server.DisposeAsync().ConfigureAwait(false);
        RetractCookie();
        SetStopped();
    }

    /// <summary>一个服务端的选项:共用的那个带 cookie、照常监听;按会话分出来的(<paramref name="cookie" /> 为 null)什么都不听,只经连接器喂流。</summary>
    private static X11ServerOptions ServerOptions(AppXServerOptions options, int display, byte[]? cookie, string? label)
    {
        // 窗口模式里除了多窗口与「无根」,其余几种(带框的大窗口、无框、全屏)都是单窗口模式(F13):整个桌面在一个窗口里,远端的窗口管理器接手。
        bool rootful = options.WindowMode is XServerWindowModes.Windowed or XServerWindowModes.NoDecoration or XServerWindowModes.Fullscreen;
        return new()
        {
            DisplayNumber = display,
            AuthorizationCookie = cookie,
            ListenTcp = cookie is null ? false : null,
            UnixSocketPath = cookie is null ? "" : null,
            SyncClipboard = options.Clipboard,
            SyncPrimary = options.Clipboard && options.CopyOnSelection,
            RestrictForwardedClients = options.RestrictForwardedClients,
            // 宿主把 X 程序的托盘图标显示成自己的托盘图标(F12),关闭到托盘的程序找得回来;单窗口模式下托盘归远端桌面的面板。
            SystemTray = !rootful,
            Rootful = rootful,
            Log = label is null ? static line => Trace.WriteLine($"[XServer] {line}") : line => Trace.WriteLine($"[XServer {label}] {line}"),
        };
    }

    /// <summary>把 cookie 写进 .Xauthority,本机 X 程序经 Xlib 自动带上;写不成只记日志(SSH 转发不受影响)。</summary>
    private void PublishCookie(int display, byte[] cookie)
    {
        if (_xauthorityPath is not { } path || _hostName() is not { } hostName)
        {
            return;
        }
        if (XAuthorityFile.Add(path, hostName, display, cookie))
        {
            _published = (path, hostName, display, cookie);
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }
    }

    /// <summary>撤掉启动时写进 .Xauthority 的那一条。</summary>
    private void RetractCookie()
    {
        if (_published is { } published)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _published = null;
            _ = XAuthorityFile.Remove(published.Path, published.Host, published.Display, published.Cookie);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => _ = RepublishCookieAsync();

    /// <summary>
    /// 主机名变了就按新名字重登 cookie、撤掉旧的那一条。记录的地址是登记时的主机名,Xlib 连本机(包括 localhost 的 TCP)时按
    /// <b>连接那一刻</b>的主机名找:macOS 换了网络,主机名常跟着 DHCP / Bonjour 变,按启动时的名字登记的那条从此对不上,
    /// 本机 X 程序一律 <c>Authorization required</c>。网络地址变化时(<see cref="NetworkChange.NetworkAddressChanged" />)核对一次。
    /// </summary>
    internal async Task RepublishCookieAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_published is not { } published || _hostName() is not { } hostName
                || string.Equals(hostName, published.Host, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (XAuthorityFile.Add(published.Path, hostName, published.Display, published.Cookie))
            {
                _ = XAuthorityFile.Remove(published.Path, published.Host, published.Display, published.Cookie);
                _published = published with { Host = hostName };
                Trace.WriteLine($"[XServer] host name changed to {hostName}: the cookie was registered again");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? HostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (SocketException ex)
        {
            Trace.WriteLine($"[XServer] cannot get the host name; the cookie is not published: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<XServerDisplayResolution> ResolveForwardingDisplayAsync(CancellationToken cancellationToken = default)
    {
        if (Current() is { } running)
        {
            return running;
        }

        AppXServerOptions options = (await _settings.GetSnapshotAsync().ConfigureAwait(false)).XServer;
        if (!options.AutoStartForX11Forwarding || await _hasOtherDisplay(cancellationToken).ConfigureAwait(false))
        {
            return XServerDisplayResolution.None;
        }

        XServerStartResult result = await StartAsync(cancellationToken).ConfigureAwait(false);
        return result.Success && Current() is { } started ? started : new(Display: null, result.Error);
    }

    /// <inheritdoc />
    /// <remarks>以 Retain 模式断开、只剩资源的不算:它们已经没有连接了。按会话分出来的显示上的一并算上。</remarks>
    public async Task<int> CountConnectedClientsAsync()
    {
        int count = 0;
        foreach (XServerInstance instance in Instances)
        {
            try
            {
                count += (await instance.Server.GetClientsAsync().ConfigureAwait(false)).Count(c => !c.Retained);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                // 刚好停了
            }
        }
        return count;
    }

    /// <inheritdoc />
    public bool CanManageClients => true;

    /// <inheritdoc />
    public event EventHandler<XServerGrabStallNotice>? ServerGrabStalled;

    /// <inheritdoc />
    /// <remarks>
    /// 程序名取它第一个窗口的 <c>WM_CLASS</c>(类名,没有退到实例名),来历是 SSH 连接器给的连接标签。按会话分出来的显示上的一并列出
    /// (共用的在前)。
    /// </remarks>
    public async Task<IReadOnlyList<XServerClient>> GetClientsAsync()
    {
        int generation = Generation();
        List<XServerClient> all = [];
        foreach (XServerInstance instance in Instances)
        {
            try
            {
                all.AddRange((await instance.Server.GetClientsAsync().ConfigureAwait(false)).Select(c => ToClient(c, generation, instance.Index)));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                // 刚好停了(或者这个会话的显示刚收掉)
            }
        }
        return all;
    }

    /// <inheritdoc />
    public void DisconnectClient(string key)
    {
        int generation = Generation();
        if (ParseKey(key) is { } parsed && parsed.Generation == generation
            && Instances.FirstOrDefault(i => i.Index == parsed.Instance) is { } instance)
        {
            instance.Server.DisconnectClient(parsed.Id);
        }
    }

    /// <inheritdoc />
    /// <remarks>每个服务端实例都做一遍(按会话分出来的显示各有各的抓取)。</remarks>
    public void BreakGrabs()
    {
        foreach (XServerInstance instance in Instances)
        {
            instance.Server.BreakGrabs();
        }
    }

    /// <summary>这是第几次启动(客户端的键带着它:停了再开,旧键作废)。</summary>
    private int Generation()
    {
        lock (_stateLock)
        {
            return _generation;
        }
    }

    /// <summary>客户端的键:「第几次启动:哪个实例:客户端编号」。</summary>
    private static string Key(int generation, int instance, int id) =>
        string.Create(CultureInfo.InvariantCulture, $"{generation}:{instance}:{id}");

    private static (int Generation, int Instance, int Id)? ParseKey(string key) =>
        key.Split(':') is [var g, var n, var i]
        && int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out int generation)
        && int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out int instance)
        && int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            ? (generation, instance, id)
            : null;

    private static XServerClient ToClient(XClientInfo client, int generation, int instance)
    {
        XTopLevelSnapshot? first = client.TopLevels.Select(w => w.Snapshot).FirstOrDefault();
        string name = first is null ? "" : first.ClassName.Length > 0 ? first.ClassName : first.InstanceName;
        return new XServerClient(Key(generation, instance, client.Id), client.Id, name, first?.Title ?? "", client.Label,
            client.TopLevels.Count, client.MemoryBytes, client.Retained, client.HoldsServerGrab);
    }

    /// <summary>报来抓取卡住的宿主对应哪个实例(共用的那个,或某个会话的显示);已经收掉的为 null。</summary>
    private XServerInstance? InstanceOfHost(object? host)
    {
        lock (_stateLock)
        {
            if (_state != XServerState.Running)
            {
                return null;
            }
            if (ReferenceEquals(host, _attached) && _server is { } shared)
            {
                return new XServerInstance(0, null, shared);
            }
            return _sessionDisplays.Values
                .Where(t => t.IsCompletedSuccessfully && ReferenceEquals(t.Result.Host, host))
                .Select(t => new XServerInstance(t.Result.Index, t.Result.Label, t.Result.Server))
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// 服务端说有个客户端抓着整个服务端太久(在它的执行线程上):查出是哪个程序再转给界面 —— 查清单要回到执行线程,
    /// 这里不能同步等,另起一个任务。
    /// </summary>
    private void OnGrabStallReported(object? sender, XServerGrabStall stall) => _ = ReportGrabStallAsync(sender, stall);

    private async Task ReportGrabStallAsync(object? host, XServerGrabStall stall)
    {
        try
        {
            int generation = Generation();
            if (InstanceOfHost(host) is not { } instance)
            {
                return;
            }
            XClientInfo? holder = (await instance.Server.GetClientsAsync().ConfigureAwait(false)).FirstOrDefault(c => c.Id == stall.ClientId);
            if (holder is null || !holder.HoldsServerGrab)
            {
                return;   // 查的这一会儿已经放开(或者断了)
            }
            XServerClient client = ToClient(holder, generation, instance.Index);
            ServerGrabStalled?.Invoke(this, new XServerGrabStallNotice(client.Key, client.Id, client.Name.Length > 0 ? client.Name : client.Title,
                stall.ClientLabel, stall.Held));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // 刚好停了
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[XServer] reporting a stalled server grab failed: {ex}");
        }
    }

    /// <summary>在运行时给出显示地址与连接器。</summary>
    private XServerDisplayResolution? Current()
    {
        X11Server? server;
        int? display;
        lock (_stateLock)
        {
            server = _state == XServerState.Running ? _server : null;
            display = _displayNumber;
        }
        return server is null || display is null
            ? null
            : new(XServerCommandLine.DisplayAddress(display.Value), Connector: ConnectAsync);
    }

    /// <summary>一条直接接进服务端的双工流:一端交给服务端的 <see cref="X11Server.ServeAsync" />,另一端交给 SSH。</summary>
    /// <remarks>
    /// 每条 x11 通道来时才取<b>此刻</b>在运行的服务端,不记住解析显示时的那一个:SSH 会话比服务端活得久,
    /// 用户在标题栏把 X Server 停掉再开之后,已经连着的会话要接到新的那个上 —— 记住旧实例的话,
    /// 每条通道都接进一个已释放的服务端,远端只看到 <c>Failed to open display</c>。
    /// 此刻没在运行就抛 <see cref="InvalidOperationException" />:<see cref="LocalXServerSelector" /> 接住它改走本机 TCP
    /// (用户换成了 VcXsrv),直接用的转发层按「本机显示连不上」处理。
    /// </remarks>
    private ValueTask<Stream> ConnectAsync(XServerChannelSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        X11Server? server;
        Task<SessionDisplay>? session = null;
        lock (_stateLock)
        {
            server = _state == XServerState.Running ? _server : null;
            if (server is not null && _runningOptions is { DisplayPerSession: true } options && source.Session is { } key)
            {
                session = SessionDisplayFor(key, source, options);
            }
        }
        if (server is null)
        {
            Trace.WriteLine("[XServer] x11 channel refused: the built-in server is not running");
            throw new InvalidOperationException("The built-in X server is not running.");
        }
        return session is null ? ValueTask.FromResult<Stream>(Serve(server, source)) : ConnectToSessionAsync(session, source);
    }

    /// <summary>经这个会话自己的显示连进去(见类注释的「每个 SSH 会话一个显示」)。建不起来时这条通道按「本机显示连不上」处理。</summary>
    private static async ValueTask<Stream> ConnectToSessionAsync(Task<SessionDisplay> session, XServerChannelSource source)
    {
        SessionDisplay display;
        try
        {
            display = await session.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"[XServer] x11 channel refused: the display for {source.Label} could not be created: {ex.Message}");
            throw new IOException("The X display for this session could not be created.", ex);
        }
        return Serve(display.Server, source);
    }

    /// <summary>
    /// 这个会话的显示:已经有(或正在建)就用它,没有就建。只在 <see cref="_stateLock" /> 里调。到了上限抛
    /// <see cref="InvalidOperationException" />:不落回共用的显示 —— 那等于悄悄取消了用户要的隔离。
    /// </summary>
    private Task<SessionDisplay> SessionDisplayFor(object key, XServerChannelSource source, AppXServerOptions options)
    {
        if (_sessionDisplays.TryGetValue(key, out Task<SessionDisplay>? existing))
        {
            return existing;
        }
        if (_sessionDisplays.Count >= MaxSessionDisplays)
        {
            Trace.WriteLine($"[XServer] x11 channel refused: already {MaxSessionDisplays} per-session displays");
            throw new InvalidOperationException($"Too many per-session X displays ({MaxSessionDisplays}).");
        }
        Task<SessionDisplay> created = CreateSessionDisplayAsync(key, source, options, _nextInstanceIndex++, _displayNumber ?? 0);
        _sessionDisplays[key] = created;
        return created;
    }

    /// <summary>建一个会话自己的显示:不监听的 <see cref="X11Server" />,配一个新的宿主;会话断开时收掉。</summary>
    private async Task<SessionDisplay> CreateSessionDisplayAsync(object key, XServerChannelSource source, AppXServerOptions options, int index, int display)
    {
        await Task.Yield();   // 别在调用方(持着 _stateLock)的线程上往下走
        if (_host() is not { } host)
        {
            throw new InvalidOperationException(Strings.Get("XServer_ErrNoHost"));
        }
        X11Server server = new(ServerOptions(options, display, cookie: null, source.Label), host);
        try
        {
            host.UseKeyboardLayout(options.KeyboardLayout);
            host.UseWindowMode(options.WindowMode);
            if (source.Label is { } label)
            {
                host.UseSessionLabel(label);   // 单窗口模式下屏幕窗口的标题写上是哪个会话的桌面
            }
            await host.AttachAsync(server, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            host.Detach();
            await server.DisposeAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                RemoveSessionDisplay(key);
            }
            throw;
        }
        SessionDisplay created = new(index, source.Label, server, host);
        host.GrabStallReported += OnGrabStallReported;   // 这个会话的显示上抓得太久也提示(F3)
        Trace.WriteLine($"[XServer] per-session display #{index} for {source.Label}");
        // 会话已经断开时 Register 当场回调:照样收掉。
        created.Ended = source.SessionEnded.Register(() => _ = EndSessionDisplayAsync(key));
        return created;
    }

    /// <summary>会话断开:收掉它的显示(停服时已经一并收掉的就什么都不做)。</summary>
    private async Task EndSessionDisplayAsync(object key)
    {
        Task<SessionDisplay>? session;
        lock (_stateLock)
        {
            session = RemoveSessionDisplay(key);
        }
        if (session is not null)
        {
            await CloseSessionDisplayAsync(session).ConfigureAwait(false);
        }
    }

    private Task<SessionDisplay>? RemoveSessionDisplay(object key) =>
        _sessionDisplays.Remove(key, out Task<SessionDisplay>? session) ? session : null;

    /// <summary>收掉一个会话的显示:关窗、断开它的 X 程序、停执行线程。没建成的跳过。</summary>
    private async Task CloseSessionDisplayAsync(Task<SessionDisplay> session)
    {
        SessionDisplay display;
        try
        {
            display = await session.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;   // 没建成:CreateSessionDisplayAsync 自己收过尾
        }
        await display.Ended.DisposeAsync().ConfigureAwait(false);
        display.Host.GrabStallReported -= OnGrabStallReported;
        display.Host.Detach();
        await display.Server.DisposeAsync().ConfigureAwait(false);
        Trace.WriteLine($"[XServer] per-session display #{display.Index} for {display.Label} closed");
    }

    /// <summary>一条接进 <paramref name="server" /> 的双工流:服务端那一端在后台服务,另一端交给 SSH。</summary>
    private static InMemoryDuplexStream Serve(X11Server server, XServerChannelSource source)
    {
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream clientSide) = InMemoryTransport.CreatePair();
        _ = ServeAsync(server, serverSide, source);
        return clientSide;
    }

    private static async Task ServeAsync(X11Server server, InMemoryDuplexStream stream, XServerChannelSource source)
    {
        try
        {
            // 服务端不拥有流:连接结束(客户端断开、服务端停下)后在这里释放,SSH 那一端随之读到 EOF。
            // SSH 转发层已经核对过远端给的假 cookie:这条流不再查授权(服务端的 cookie 只给 TCP 上的本机程序)。
            // 没勾「受信任」的会话(ssh -X)以非受信级别连进去:服务端按 SECURITY 扩展的语义限制它。
            await server.ServeAuthenticatedAsync(stream, source.Label, source.Trusted ? XClientTrust.Trusted : XClientTrust.Untrusted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
        {
            // 服务端已停,或连接中途断了。
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 本机是否已经有别的 X 显示在用:Windows 上看 <c>localhost:0</c>,而且在那里监听的进程要在当前用户会话里 —— 终端服务器上
    /// 那可能是别的用户开着的 VcXsrv(常带 <c>-ac</c>),原先只要有人在听就不自动启动、转发落到别人的 X 服务端上
    /// (见 <see cref="XDisplayProbe.IsTcpListenerInThisSession" />);其它平台看 <c>DISPLAY</c>。
    /// </summary>
    private static async Task<bool> HasOtherDisplayAsync(CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? await XDisplayProbe.IsTcpListeningAsync(0, cancellationToken).ConfigureAwait(false) && XDisplayProbe.IsTcpListenerInThisSession(0)
            : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));

    private void SetState(XServerState state, int display)
    {
        lock (_stateLock)
        {
            _state = state;
            _displayNumber = display;
        }
        RaiseStateChanged();
    }

    private void SetStopped()
    {
        lock (_stateLock)
        {
            _state = XServerState.Stopped;
            _displayNumber = null;
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[XServer] StateChanged handler failed: {ex}");
        }
    }

    /// <summary>退出时停掉服务端、关掉它的窗口。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>同步释放(容器按同步方式收尾时):最多等 3 秒让服务端收工。</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _ = DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
    }
}
