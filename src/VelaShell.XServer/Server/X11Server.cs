// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(显示号与传输)
//   架构:velashell-docs/zh/xserver/design/architecture.md §5(线程模型)、§6(宿主接口)
//
//   这个文件是服务端的全部公开面:构造、生命周期、宿主注入。各方法只校验参数、把工作排进执行线程,
//   真正的处理在各领域的 partial 文件里(Apply* 系列);其余 partial 文件里没有公开成员。

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using VelaShell.XServer.Fonts;
using VelaShell.XServer.Input;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

/// <summary>
/// 一个可嵌入的 X11 服务端(rootless,软件绘图)。
/// </summary>
/// <remarks>
/// <para>
/// <b>一个执行线程</b>执行全部客户端请求、宿主注入的输入与收尾工作 —— X 的语义是全局串行的,
/// 所有可变状态只在这个线程上被碰,因此不加锁(架构 §5)。唯一的例外是像素:宿主在别的线程上经
/// <see cref="XTopLevelWindow.ReadPixels" /> 读顶层窗口的像素,执行线程在执行一批工作项期间持有同一把像素锁。
/// </para>
/// <para>
/// 连接可以来自 TCP 与 Unix 套接字(<see cref="StartAsync" />),也可以直接交一条流进来
/// (<see cref="ServeAsync" />)—— 后者让宿主不必经本机端口:SSH 的 x11 通道本身就是一条双工流。
/// </para>
/// <para>
/// 宿主方法的命名:<c>Inject*</c> 是合成的用户输入;<c>*TopLevel</c> 是宿主作为窗口管理器对某个顶层窗口的动作;
/// <c>Set*</c> 是运行中换配置。它们都可以在任意线程上调、立即返回,参数不合法时当场抛异常;
/// 指名的窗口在执行时已经不在(客户端刚销毁了它)时静默忽略。
/// </para>
/// <para>
/// 构造时执行线程就开始运行:构造出来的实例即使从没 <see cref="StartAsync" />,也要 <see cref="DisposeAsync" />。
/// </para>
/// </remarks>
public sealed partial class X11Server : IAsyncDisposable
{
    /// <summary>显示器上限:RANDR 的 CRTC / 输出 / 模式 ID 各占 16 个服务端 ID。</summary>
    public const int MaxMonitors = 16;

    internal const uint RootWindowId = 0x00000100;
    internal const uint DefaultColormapId = 0x00000020;
    internal const uint RootVisualId = 0x00000021;
    internal const uint ArgbVisualId = 0x00000022;

    private readonly X11ServerOptions _options;

    /// <summary>对宿主的回调都经它排队,执行线程放锁之后再调(见 RunLoopAsync)。</summary>
    private readonly DeferredHost _host;

    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<uint, XResource> _resources = [];
    private readonly Dictionary<int, XClient> _clients = [];
    private readonly FontCatalog _fonts = new();
    private readonly Keymap _keymap = new();
    private readonly Dictionary<XWindow, List<XRect>> _damage = [];
    private readonly Dictionary<XWindow, XTopLevelWindow> _topLevelHandles = [];

    /// <summary>像素锁与宿主的让行计数(见 <see cref="PixelGate" />)。</summary>
    private readonly PixelGate _pixelGate = new();

    /// <summary>GLX 扩展(自成一体的一个类;别的扩展还是 partial 文件)。</summary>
    private readonly GlxExtension _glx;

    private readonly Task _loopTask;
    private int _started;
    private int _disposed;

    /// <summary>用选项与宿主构造;宿主为 null 时不显示任何东西(无头,测试与诊断用)。</summary>
    /// <exception cref="ArgumentException">选项不合法(见 <see cref="X11ServerOptions" /> 各项的取值范围)。</exception>
    public X11Server(X11ServerOptions? options = null, IX11ServerHost? host = null)
    {
        _options = options ?? new X11ServerOptions();
        _options.Validate();
        _host = new DeferredHost(host ?? NullHost.Instance, Log);
        Root = CreateRootWindow();
        _resources[Root.Id] = Root;
        InitMonitors();
        RebuildRandRModes();
        _resources[DefaultColormapId] = new XColormap(DefaultColormapId, null, RootVisualId);
        InitAtoms();
        _glx = new GlxExtension(this);
        InitExtensions();
        InitXSettings();
        InitSyncCounters();
        PublishXkbRulesNames();
        InitEwmh();
        _pointerWindow = Root;
        _focus = Root;   // 初始焦点是 PointerRoot(与 X.Org 一致;窗口管理器 —— 这里是宿主 —— 之后再把焦点给具体的顶层)
        _loopTask = Task.Run(RunLoopAsync);
    }

    /// <summary>显示号 N(<see cref="X11ServerOptions.DisplayNumber" />)。</summary>
    public int DisplayNumber => _options.DisplayNumber;

    /// <summary>
    /// 给本机 X 客户端用的 <c>DISPLAY</c>:在 Unix 套接字上监听时是 <c>:N</c>(走套接字,MIT-SHM 可用),
    /// 只有 TCP 时是 <c>localhost:N.0</c>;<see cref="StartAsync" /> 之前、或者两种都没在监听(只经 <see cref="ServeAsync" /> 喂流)时为 null。
    /// </summary>
    public string? Display { get; private set; }

    /// <summary>TCP 监听的端口(6000 + 显示号);没监听时为 0。</summary>
    public int Port { get; private set; }

    internal XWindow Root { get; }

    internal X11ServerOptions Options => _options;

    /// <summary>服务端时间(毫秒,32 位回绕)—— 事件里的 time 字段。</summary>
    internal uint Now => unchecked((uint)_clock.ElapsedMilliseconds);

    // ================================================================== 生命周期

    /// <summary>
    /// 开始监听:TCP 6000 + N(<see cref="X11ServerOptions.ListenTcp" />)与 Unix 套接字(<see cref="X11ServerOptions.UnixSocketPath" />)。
    /// TCP 端口被占用时抛 <see cref="SocketException" />;Unix 套接字建不起来只记日志。
    /// </summary>
    /// <exception cref="InvalidOperationException">已经开始监听了。</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("服务端已经在监听了。");
        }
        if (_options.ListenTcp)
        {
            StartTcpListener();
        }
        StartUnixListeners(_lifetime.Token);
        Display = DisplayAddress();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 在一条已经建立的双工流上服务一个 X 客户端,直到它断开或服务端收工。
    /// </summary>
    /// <param name="stream">双工流(TCP、Unix 套接字、SSH 的 x11 通道……)。服务端<b>不释放</b>它:任务结束后由调用方释放。</param>
    /// <param name="isLocal">
    /// 对端算不算本机连接。没配置 <see cref="X11ServerOptions.AuthorizationCookie" /> 时只接受本机连接 ——
    /// 只有确实来自本机、或已由别的环节验过身份(比如 SSH 转发已核对过假 cookie)的流才该传 true。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task ServeAsync(Stream stream, bool isLocal, CancellationToken cancellationToken = default) =>
        ServeCoreAsync(stream, isLocal, sameHost: false, peerUid: null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _listener?.Stop();
        StopUnixListeners();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        foreach (Task? task in (Task?[])[_acceptTask, _loopTask])
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 收工。
            }
        }
        foreach (XClient client in _clients.Values)
        {
            client.Abort();
        }
        DetachShmSegments(_resources.Values);
        await WaitForConnectionsAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // ================================================================== 宿主注入:输入

    /// <summary>指针在顶层窗口里移动(内区坐标,物理像素)。</summary>
    public void InjectPointerMotion(XTopLevelWindow window, int x, int y)
    {
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyPointerMotion(top, x, y);
            }
        });
    }

    /// <summary>
    /// 按钮按下 / 松开(内区坐标)。1 左、2 中、3 右;滚轮向上 4、向下 5、向左 6、向右 7(宿主应当为每格滚动注入一次按下 + 松开);
    /// 8、9 是后退 / 前进侧键。
    /// </summary>
    public void InjectPointerButton(XTopLevelWindow window, int x, int y, int button, bool pressed)
    {
        CheckHandle(window);
        ArgumentOutOfRangeException.ThrowIfLessThan(button, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(button, 255);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyPointerButton(top, x, y, button, pressed);
            }
        });
    }

    /// <summary>指针离开了所有顶层窗口(移到了宿主的其他窗口或桌面上)。</summary>
    public void InjectPointerLeave() => Post(null, ApplyPointerLeave);

    /// <summary>按键按下 / 松开(X 键码,见 <see cref="XKeycodes" />)。按键送往当前的键盘焦点(<see cref="FocusTopLevel" />)。</summary>
    public void InjectKey(byte keycode, bool pressed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keycode, XKeymap.MinKeycode);
        Post(null, () => ApplyKey(keycode, pressed));
    }

    // ================================================================== 宿主注入:窗口管理器

    /// <summary>宿主让某个顶层窗口得到键盘焦点(用户激活了它的原生窗口);null = 所有顶层都失去焦点。</summary>
    public void FocusTopLevel(XTopLevelWindow? window)
    {
        if (window is null)
        {
            Post(null, () => ApplyFocus(null));
            return;
        }
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyFocus(top);
            }
        });
    }

    /// <summary>用户移动了原生窗口:外框左上角移到根窗口坐标 (<paramref name="x" />, <paramref name="y" />),并按 ICCCM 发一条合成的 ConfigureNotify。</summary>
    public void MoveTopLevel(XTopLevelWindow window, int x, int y)
    {
        CheckHandle(window);
        CheckCoordinate(x, nameof(x));
        CheckCoordinate(y, nameof(y));
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyMove(top, x, y);
            }
        });
    }

    /// <summary>用户缩放了原生窗口:改内区尺寸(客户端收到 ConfigureNotify 与 Expose,重画)。</summary>
    public void ResizeTopLevel(XTopLevelWindow window, int width, int height)
    {
        CheckHandle(window);
        CheckSize(width, nameof(width));
        CheckSize(height, nameof(height));
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyResize(top, width, height);
            }
        });
    }

    /// <summary>
    /// 用户点了原生窗口的关闭按钮:客户端声明了 WM_DELETE_WINDOW 就礼貌地请它自己关(ICCCM §4.2.8),
    /// 否则断开该客户端(与窗口管理器的 XKillClient 一致)。
    /// </summary>
    public void CloseTopLevel(XTopLevelWindow window)
    {
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyClose(top);
            }
        });
    }

    /// <summary>
    /// 宿主(窗口管理器)设定了窗口状态 —— 通常是照办了一个 <see cref="XStateChangeRequest" />,或用户点了原生窗口的最大化按钮。
    /// 服务端写 <c>_NET_WM_STATE</c> 与 <c>WM_STATE</c>,客户端据此更新外观。<see cref="XWindowStates.Focused" /> 由服务端按焦点维护,这里给的会被忽略。
    /// </summary>
    public void SetTopLevelStates(XTopLevelWindow window, XWindowStates states)
    {
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyStates(top, states);
            }
        });
    }

    /// <summary>宿主给窗口加的装饰有多宽(<c>_NET_FRAME_EXTENTS</c>,物理像素,不能为负)。客户端据此计算外框位置。</summary>
    public void SetTopLevelFrameExtents(XTopLevelWindow window, XFrameExtents extents)
    {
        CheckHandle(window);
        if (extents.Left < 0 || extents.Right < 0 || extents.Top < 0 || extents.Bottom < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extents), extents, "外框宽度不能为负。");
        }
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyFrameExtents(top, extents);
            }
        });
    }

    // ================================================================== 宿主注入:配置

    /// <summary>
    /// 换键位表(宿主的键盘布局变了):<paramref name="keymap" /> 里列出的键码连同布局名、右 Alt 的角色一次换掉,
    /// XKB 描述随之重新推出。客户端各收到一次 MappingNotify(键盘,修饰键表变了时再加一次修饰键)与 XKB 的 MapNotify。
    /// </summary>
    public void SetKeymap(XKeymap keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        KeymapChange change = KeymapChange.From(keymap);
        Post(null, () => ApplyKeymap(change));
    }

    /// <summary>
    /// 宿主的显示器布局变了:把根窗口(虚拟桌面)改成 <paramref name="width" /> × <paramref name="height" />,
    /// 显示器换成 <paramref name="monitors" />(null 或空 = 一台覆盖全部;最多 <see cref="MaxMonitors" /> 台)。
    /// 客户端收到根窗口的 ConfigureNotify 与 RANDR 的 ScreenChangeNotify / RRNotify。
    /// </summary>
    public void SetScreenLayout(int width, int height, IReadOnlyList<XMonitor>? monitors = null)
    {
        CheckSize(width, nameof(width));
        CheckSize(height, nameof(height));
        IReadOnlyList<XMonitor> normalized = NormalizeMonitors(monitors, width, height, nameof(monitors));
        Post(null, () => ApplyScreenLayout(width, height, normalized));
    }

    /// <summary>
    /// 宿主的 DPI / 缩放变了(窗口挪到了另一台显示器、用户改了系统缩放):更新 XSETTINGS(Xft/DPI、Gdk/WindowScalingFactor、
    /// Gdk/UnscaledDPI)与根窗口的 RESOURCE_MANAGER(Xft.dpi)。GTK 立刻按新值重排;Xlib / Xft 与 Qt 程序在下次启动时生效。
    /// </summary>
    /// <param name="dpi">每英寸像素数(实际像素,如 2 倍缩放的 192),≥ 1。</param>
    /// <param name="scale">整数缩放倍数(GTK 的窗口缩放),≥ 1。</param>
    public void SetDisplayScale(int dpi, int scale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        Post(null, () => ApplyDisplayScale(dpi, scale));
    }

    /// <summary>
    /// 宿主的剪贴板有了新文本:服务端占有 CLIPBOARD(<see cref="X11ServerOptions.SyncPrimary" /> 时连同 PRIMARY),
    /// 之后 X 客户端粘贴拿到的就是它。与刚交给宿主的文本相同时什么也不做(那是宿主把我们给的写回来了)。
    /// </summary>
    public void SetClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Post(null, () => ApplyClipboardText(text));
    }

    // ================================================================== 参数校验

    private void CheckHandle(XTopLevelWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.BelongsTo(_pixelGate))
        {
            throw new ArgumentException("这个窗口不是本服务端的。", nameof(window));
        }
    }

    private static void CheckCoordinate(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, short.MinValue, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, short.MaxValue, name);
    }

    private static void CheckSize(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, X11ServerOptions.MaxScreenSize, name);
    }

    /// <summary>句柄指的窗口此刻还是它自己的那个顶层窗口(没被销毁、没被 reparent 走)时返回窗口,否则 null。只在执行线程上调。</summary>
    private XWindow? LiveTopLevel(XTopLevelWindow handle) =>
        handle.Window is { IsTopLevel: true } top && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? current)
        && ReferenceEquals(current, handle)
            ? top
            : null;

    /// <summary>当前监听着的传输对应的 DISPLAY(见 <see cref="Display" />)。</summary>
    private string? DisplayAddress()
    {
        int n = _options.DisplayNumber;
        if (_unixListeners.Count != 0)
        {
            return $":{n}";
        }
        if (_listener is null)
        {
            return null;
        }
        IPAddress address = _options.ListenAddress;
        bool anyOrLoopback = IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        return $"{(anyOrLoopback ? "localhost" : address.ToString())}:{n}.0";
    }
}
