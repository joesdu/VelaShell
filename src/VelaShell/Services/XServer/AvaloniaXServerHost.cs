using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;
using VelaShell.Views;
using VelaShell.Views.XServer;
using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// 内置 X 服务端的 Avalonia 宿主:每个 X 顶层窗口一个原生窗口(rootless),宿主扮演窗口管理器。
/// </summary>
/// <remarks>
/// <para>
/// 服务端的回调在它的执行线程上来,这里一律 <see cref="Dispatcher.Post(Action, DispatcherPriority)" /> 到 UI 线程,
/// 不阻塞执行线程;注入方向(指针、键盘、移动、缩放)只是把工作项排进服务端,本身不阻塞 UI 线程。
/// </para>
/// <para>
/// <b>根窗口 = 整个虚拟桌面。</b>所有显示器的外接矩形平移到原点 (0,0) 交给服务端当根窗口,
/// 每台显示器一个 RANDR 输出;<see cref="RootOrigin" /> 是这个外接矩形的左上角(物理像素),
/// X 坐标加上它就是系统坐标。显示器增减、DPI 变化时重新告诉服务端。
/// </para>
/// </remarks>
public sealed class AvaloniaXServerHost : IEmbeddedXServerHost
{
    private readonly Dictionary<XTopLevelWindow, XNativeWindow> _windows = [];

    /// <summary>映射着、但不给原生窗口的桌面类窗口(见 <see cref="IsDesktop" />)。只在 UI 线程上碰。</summary>
    private readonly HashSet<XTopLevelWindow> _desktops = [];
    private volatile X11Server? _server;
    private Screens? _watchedScreens;
    private string? _lastClipboard;
    private (object? Source, WindowIcon? Icon) _iconCache;
    private nint _keyboardLayout;
    private HostKeymapResult? _appliedKeymap;
    private volatile string _chosenLayout = "";

    /// <summary>每个窗口攒着、还没投递到 UI 线程的损伤矩形上限;再多就合成外接矩形。</summary>
    private const int MaxQueuedDamageRects = 32;

    private readonly Lock _damageGate = new();
    private readonly Action _deliverDamage;
    private Dictionary<XTopLevelWindow, List<XRect>> _incomingDamage = [];
    private Dictionary<XTopLevelWindow, List<XRect>> _deliveringDamage = [];
    private bool _damagePosted;

    /// <summary>新建一个宿主;经 <see cref="AttachAsync" /> 接到服务端上。</summary>
    /// <param name="dropUploader">本机文件拖进经 SSH 转发来的 X 程序时,把文件传到远端(F16);没有时那类窗口不接文件。</param>
    public AvaloniaXServerHost(IXServerDropUploader? dropUploader = null)
    {
        _deliverDamage = DeliverDamage;
        DropUploader = dropUploader;
        _trayIcons = new XTrayIcons(CurrentServer);
    }

    /// <summary>X 程序的托盘图标(F12),画成宿主的托盘图标。只在 UI 线程上碰。</summary>
    private readonly XTrayIcons _trayIcons;

    /// <summary>此刻挂着的 X 托盘图标数(测试看)。</summary>
    internal int TrayIconCount => _trayIcons.Count;

    /// <summary>本机文件拖进经 SSH 转发来的 X 程序时,把文件传到远端(见 <see cref="Views.XServer.XDropTarget" />)。</summary>
    internal IXServerDropUploader? DropUploader { get; }

    /// <summary>给用户一条提示(主窗口右下的提示浮层;主窗口不在时不提示)。UI 线程上调。</summary>
    internal static void NotifyUser(string message, bool error)
    {
        if (MainWindow()?.DataContext is ViewModels.MainWindowViewModel { Toasts: { } toasts })
        {
            if (error)
            {
                toasts.Error(message);
            }
            else
            {
                toasts.Info(message);
            }
        }
    }

    /// <summary>当前附着的服务端;没在运行时为 <see langword="null" />。窗口的注入经它走。</summary>
    public X11Server? Server => _server;

    /// <summary>
    /// 句柄是此刻附着的服务端发出的。停掉服务端再起一个时 UI 队列里还排着旧服务端的回调,新服务端的 XID 又与旧的重合:
    /// 原先按 XID 找窗口,旧回调会用旧句柄建原生窗口(之后注入时新服务端抛 ArgumentException)、误关新窗口。
    /// </summary>
    private bool IsCurrent(XTopLevelWindow handle) => _server is { } server && ReferenceEquals(handle.Server, server);

    /// <summary>句柄所属的服务端,只在它就是此刻附着的那个时给出(交给别的服务端会抛异常)。</summary>
    internal X11Server? CurrentServer(XTopLevelWindow handle) => IsCurrent(handle) ? handle.Server : null;

    /// <summary>当前开着的原生窗口(UI 线程上读;测试用)。</summary>
    internal IReadOnlyCollection<XNativeWindow> Windows => _windows.Values;

    /// <summary>用户此刻在用 X 窗口(某个 X 窗口是活动窗口)。UI 线程上读。</summary>
    public bool XActive => _windows.Values.Any(w => w.IsActive);

    /// <summary>根窗口原点在系统虚拟桌面里的位置(物理像素)。只在 UI 线程上读写。</summary>
    public (int X, int Y) RootOrigin { get; private set; }

    /// <summary>
    /// 最近一个有系统边框的原生窗口量到的边框尺寸(物理像素):新窗口显示之前按它预估,第一帧就摆在对的位置。只在 UI 线程上读写。
    /// </summary>
    internal XFrameExtents LastDecoratedFrame { get; set; }

    // ================================================================== 生命周期

    /// <summary>
    /// 当前附着着的宿主:本机活动上报给它们的服务端(见 <see cref="HookLocalActivity" />)。开了「每个 SSH 会话一个显示」时不止一个。
    /// 整份替换(写时复制),读的一方不加锁。
    /// </summary>
    private static volatile AvaloniaXServerHost[] s_attached = [];

    private static readonly Lock s_attachedGate = new();

    private static bool s_activityHooked;

    /// <summary>
    /// 用户在 VelaShell 自己的任何窗口里按键、点击、滚动、移动鼠标时告诉服务端(<see cref="X11Server.NoteUserActivity" />):
    /// 远端程序看到的空闲时间不再只按 X 窗口里的输入算 —— 原先用户整小时在本机终端里打字,远端的「离开」状态与空闲锁屏照样触发。
    /// 挂一次全局的类处理器(隧道阶段、已处理的事件也算),服务端那边自己节流。只在 UI 线程上调。
    /// </summary>
    private static void HookLocalActivity()
    {
        if (s_activityHooked)
        {
            return;
        }
        s_activityHooked = true;
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void ReportLocalActivity()
    {
        foreach (AvaloniaXServerHost host in s_attached)
        {
            host._server?.NoteUserActivity();
        }
    }

    /// <inheritdoc />
    public async Task AttachAsync(X11Server server, CancellationToken cancellationToken)
    {
        _server = server;
        lock (s_attachedGate)
        {
            s_attached = [.. s_attached.Where(h => !ReferenceEquals(h, this)), this];
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HookLocalActivity();
            _keyboardLayout = 0;
            _appliedKeymap = null;   // 新起的服务端是 US 键位表:按当前布局重推一次
            ApplyKeyboardLayout(server);
            if (server.Screen is { } screen)
            {
                // 单窗口模式(F13):屏幕尺寸是屏幕窗口的尺寸,不跟本机的显示器布局;窗口随即显示出来(远端桌面还没连上时是黑的)。
                if (MainWindow() is { } owner)
                {
                    ApplyScreenSize(server, owner.Screens);
                }
                Map(screen);
            }
            else if (MainWindow() is { } main)
            {
                ApplyLayout(server, main.Screens);
                if (!ReferenceEquals(_watchedScreens, main.Screens))
                {
                    _watchedScreens?.Changed -= OnScreensChanged;
                    _watchedScreens = main.Screens;
                    _watchedScreens.Changed += OnScreensChanged;
                }
            }
        }, DispatcherPriority.Normal, cancellationToken);
    }

    /// <summary>显示器布局变了:重报给服务端;根原点挪了时各窗口重报位置。</summary>
    private void OnScreensChanged(object? sender, EventArgs e)
    {
        if (_server is { } current && _watchedScreens is { } screens)
        {
            (int, int) before = RootOrigin;
            ApplyLayout(current, screens);
            if (RootOrigin != before)
            {
                // 左侧 / 上方的显示器插拔:根原点挪了,原生窗口没动,X 坐标却整体差了这么多(菜单、对话框会摆到别处)。
                // 按每个窗口此刻的原生位置重报一次。
                foreach (XNativeWindow window in _windows.Values)
                {
                    window.ReportPosition();
                }
            }
        }
    }

    /// <inheritdoc />
    public void Detach()
    {
        _server = null;
        lock (s_attachedGate)
        {
            s_attached = [.. s_attached.Where(h => !ReferenceEquals(h, this))];
        }
        Dispatcher.UIThread.Post(() =>
        {
            _trayIcons.Clear();
            // 按会话分出来的显示收掉时这个宿主就不再用了:别让显示器的事件一直拽着它。
            _watchedScreens?.Changed -= OnScreensChanged;
            _watchedScreens = null;
            XNativeWindow[] windows = [.. _windows.Values];
            _windows.Clear();
            _desktops.Clear();
            // 先全部打上标记再关:关 owner 时 Avalonia 先问它的子窗口,子窗口不拦,owner 才关得掉(原先留下关不掉的空壳)。
            foreach (XNativeWindow window in windows)
            {
                window.MarkClosingByHost();
            }
            foreach (XNativeWindow window in windows)
            {
                window.CloseByHost();
            }
        });
    }

    /// <summary>
    /// 显示器布局与 DPI 告诉服务端:根窗口是所有显示器的外接矩形,每台一个输出;DPI 取主显示器的缩放。
    /// </summary>
    /// <summary>设置里选的窗口模式(见 <see cref="UseWindowMode" />)。</summary>
    private string _windowMode = XServerWindowModes.MultiWindow;

    /// <inheritdoc />
    public void UseWindowMode(string mode) => _windowMode = mode;

    /// <summary>单窗口模式的屏幕窗口不带边框(窗口模式「无边框」)。</summary>
    internal bool ScreenUndecorated => _windowMode == XServerWindowModes.NoDecoration;

    /// <summary>单窗口模式的屏幕窗口一开始就全屏(窗口模式「全屏」)。</summary>
    internal bool ScreenFullscreen => _windowMode == XServerWindowModes.Fullscreen;

    /// <summary>按会话分出来的显示:是哪个会话的(见 <see cref="UseSessionLabel" />);共用的显示为 null。</summary>
    private string? _sessionLabel;

    /// <inheritdoc />
    public void UseSessionLabel(string label) => _sessionLabel = label;

    /// <summary>屏幕窗口的标题:「X 桌面 :N」;按会话分出来的显示写会话的来历(「X 桌面 user@host:22」)。</summary>
    internal string ScreenTitle(X11Server server) =>
        Strings.Format("XServer_ScreenTitle", _sessionLabel
            ?? (server.Display is { } display ? display.Split('.')[0].Replace("localhost", "") : $":{server.DisplayNumber}"));

    /// <summary>
    /// 单窗口模式起步的屏幕尺寸(物理像素,一台显示器覆盖全部):全屏时是主显示器的大小,否则是主显示器工作区的八成 ——
    /// 之后跟着用户把屏幕窗口拖到多大。DPI 照主显示器的报。
    /// </summary>
    private void ApplyScreenSize(X11Server server, Screens screens)
    {
        if ((screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null)) is not { } primary)
        {
            return;
        }
        (int width, int height) = ScreenFullscreen
            ? (primary.Bounds.Width, primary.Bounds.Height)
            : ((int)(primary.WorkingArea.Width * 0.8), (int)(primary.WorkingArea.Height * 0.8));
        width = Math.Clamp(width, 1, X11ServerOptions.MaxScreenSize);
        height = Math.Clamp(height, 1, X11ServerOptions.MaxScreenSize);
        server.SetScreenLayout(width, height);
        double scaling = primary.Scaling;
        server.SetDisplayScale(Math.Max(1, (int)Math.Round(96 * scaling)), scaling >= 2 ? (int)Math.Floor(scaling) : 1);
    }

    /// <summary>用户关了屏幕窗口:当作要停 X Server(有程序连着时先确认),与标题栏的停止一样。</summary>
    internal static void RequestStop()
    {
        if (MainWindow()?.DataContext is ViewModels.MainWindowViewModel { XServer: { } xserver })
        {
            xserver.StopCommand.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    private void ApplyLayout(X11Server server, Screens screens)
    {
        IReadOnlyList<Screen> all = LimitScreens(screens.All);
        if (all.Count == 0)
        {
            return;
        }
        int minX = all.Min(s => s.Bounds.X), minY = all.Min(s => s.Bounds.Y);
        // 虚拟桌面超出根窗口的上限时截到上限(多出来的部分 X 程序摆不过去);服务端对超限的参数抛异常,原先异常落在 UI 线程上。
        int maxX = Math.Min(all.Max(s => s.Bounds.Right), minX + X11ServerOptions.MaxScreenSize);
        int maxY = Math.Min(all.Max(s => s.Bounds.Bottom), minY + X11ServerOptions.MaxScreenSize);
        RootOrigin = (minX, minY);
        List<XMonitor> monitors = [];
        for (int i = 0; i < all.Count; i++)
        {
            Screen screen = all[i];
            PixelRect b = screen.Bounds;
            double dpi = 96 * Math.Max(1, screen.Scaling);
            // 工作区(去掉任务栏 / Dock):服务端据此算 _NET_WORKAREA,菜单、最大化、对话框才不会落到任务栏后面。
            PixelRect work = screen.WorkingArea.Intersect(b);
            monitors.Add(new XMonitor(b.X - minX, b.Y - minY, b.Width, b.Height)
            {
                Name = string.IsNullOrWhiteSpace(screen.DisplayName) ? $"SCREEN-{i + 1}" : screen.DisplayName,
                Primary = screen.IsPrimary,
                WidthMillimeters = (int)Math.Round(b.Width / dpi * 25.4),
                HeightMillimeters = (int)Math.Round(b.Height / dpi * 25.4),
                WorkArea = work.Width > 0 && work.Height > 0 && work != b
                    ? new XRect(work.X - minX, work.Y - minY, work.Width, work.Height)
                    : null,
            });
        }
        server.SetScreenLayout(maxX - minX, maxY - minY, monitors);

        // 分数缩放(125%、150%)只调 DPI,整数倍(200%)时再让 GTK 按倍数放大控件 —— GTK 的窗口缩放只认整数。
        double scaling = (screens.Primary ?? all[0]).Scaling;
        int scale = scaling >= 2 ? (int)Math.Floor(scaling) : 1;
        server.SetDisplayScale(Math.Max(1, (int)Math.Round(96 * scaling)), scale);
    }

    /// <summary>
    /// 服务端最多接受 <see cref="X11Server.MaxMonitors" /> 台显示器:多了只交主显示器与排在前面的几台(原先整个列表交过去,
    /// 服务端抛的异常落在 UI 线程上,布局一次也没换成)。
    /// </summary>
    private static IReadOnlyList<Screen> LimitScreens(IReadOnlyList<Screen> all) => LimitScreens(all, s => s.IsPrimary);

    internal static IReadOnlyList<T> LimitScreens<T>(IReadOnlyList<T> all, Func<T, bool> isPrimary)
    {
        if (all.Count <= X11Server.MaxMonitors)
        {
            return all;
        }
        T[] primary = [.. all.Where(isPrimary).Take(1)];
        return [.. primary, .. all.Where(s => !isPrimary(s)).Take(X11Server.MaxMonitors - primary.Length)];
    }

    /// <summary>
    /// 按键盘布局换服务端的键位表:设置里手选了布局时用随程序带的表(<see cref="BundledKeymaps" />);否则跟随系统当前的布局 ——
    /// Windows 见 <see cref="WindowsKeymap" />,macOS 见 <see cref="MacKeymap" />,Linux 见 <see cref="LinuxKeymap" />。
    /// 算出来的与上次推给服务端的一样时什么也不做;取不到布局时沿用服务端内置的 US 键位表。
    /// Linux 上要连桌面的 X 显示、拉一遍完整的 XKB 表,放到后台去读(见 <see cref="RefreshFromDesktop" />),连同锁定键一起。
    /// </summary>
    private void ApplyKeyboardLayout(X11Server server)
    {
        if (DesktopKeyboardReader is not null)
        {
            RefreshFromDesktop(server);   // 锁定键总要读;键位表在没手选布局时才用(见 OnDesktopRead)
            if (ChosenKeymap() is null)
            {
                return;
            }
        }
        HostKeymapResult? keymap;
        try
        {
            keymap = BuildHostKeymap();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return;   // 系统库缺了哪一个:沿用现在的键位表
        }
        ApplyKeymap(server, keymap);
    }

    private void ApplyKeymap(X11Server server, HostKeymapResult? keymap)
    {
        if (keymap is null || keymap.SameAs(_appliedKeymap))
        {
            return;
        }
        _appliedKeymap = keymap;
        // 一次交过去:键值、右 Alt 是不是 AltGr(macOS 上是右 Option)、布局名 —— 服务端只通知客户端一轮。
        HasAltGr = keymap.HasAltGr;
        server.SetKeymap(keymap.ToXKeymap());
    }

    /// <summary>
    /// 读桌面的键位表与锁定键(Linux:<see cref="LinuxKeymap.ReadDesktop" />;其余平台为 null,在 UI 线程上直接读)。
    /// 参数是内置服务端自己的显示号。测试可以换掉。
    /// </summary>
    internal Func<int, DesktopKeyboard?>? DesktopKeyboardReader { get; set; } =
        OperatingSystem.IsLinux() ? display => OperatingSystem.IsLinux() ? LinuxKeymap.ReadDesktop(display) : null : null;

    /// <summary>后台读桌面键盘的那一次;没在读为 null。只在 UI 线程上碰。</summary>
    private Task? _desktopRead;

    /// <summary>读的期间又有窗口激活了:读完再读一次(只再读一次,不排队)。</summary>
    private bool _desktopReadAgain;

    /// <summary>
    /// 在后台读桌面的键位表与锁定键,读完回到 UI 线程交给服务端。原先 Linux 上每次激活 X 窗口都在 UI 线程上 <c>xcb_connect</c>
    /// 桌面、完整拉两遍 XKB 表(键位表、锁定键各一遍):<c>$DISPLAY</c> 指向慢的显示时,切一次窗口界面就卡一下。
    /// 同一时刻只读一次,读的期间再激活的合成读完之后的一次。
    /// </summary>
    private void RefreshFromDesktop(X11Server server)
    {
        if (DesktopKeyboardReader is not { } reader)
        {
            return;
        }
        if (_desktopRead is not null)
        {
            _desktopReadAgain = true;
            return;
        }
        int display = server.DisplayNumber;
        Task<DesktopKeyboard?> read = Task.Run(() => reader(display));
        _desktopRead = read.ContinueWith(done => Dispatcher.UIThread.Post(() => OnDesktopRead(server, done)), TaskScheduler.Default);
    }

    private void OnDesktopRead(X11Server server, Task<DesktopKeyboard?> read)
    {
        _desktopRead = null;
        if (read.IsFaulted)
        {
            Trace.WriteLine($"[XServer] cannot read the desktop keyboard: {read.Exception.InnerException?.Message}");
        }
        // 读的期间服务端停了 / 换了一个:结果不交给它(新服务端附着时自己会再读)。
        if (ReferenceEquals(_server, server) && read.IsCompletedSuccessfully && read.Result is { } desktop)
        {
            if (desktop.Locks is var (capsLock, numLock))
            {
                server.SetLockState(capsLock, numLock);
            }
            if (ChosenKeymap() is null)
            {
                ApplyKeymap(server, desktop.Keymap);
            }
        }
        if (_desktopReadAgain && _server is { } current)
        {
            _desktopReadAgain = false;
            RefreshFromDesktop(current);
        }
    }

    /// <inheritdoc />
    public void UseKeyboardLayout(string layout) => _chosenLayout = layout ?? "";

    private long _layoutCheckedAt;

    /// <summary>
    /// X 窗口里按下了一个键:系统布局可能刚在 X 窗口里切过(Win+Space、Alt+Shift、输入法的切换)—— 先看一眼,变了就把新的键位表推过去,
    /// 再注入这个键(同一个工作队列,按先后处理)。原先只在激活 X 窗口时重推,在 X 窗口里切了布局,继续敲出的仍是旧布局。
    /// Windows 上只比一下布局句柄(很便宜);别的系统算一遍键位表较贵,至多每秒看一次(Linux 上在后台读,读完才推,
    /// 紧接着的这个键可能还按旧布局)。设置里手选了布局时不跟随系统。
    /// 服务端只改与上次不同的键,用户在 X 里做的 xmodmap 改动不受影响。
    /// </summary>
    internal void RefreshKeyboardLayoutOnKey()
    {
        if (_server is not { } server || _chosenLayout.Length != 0)
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            long now = Environment.TickCount64;
            if (now - _layoutCheckedAt < 1000)
            {
                return;
            }
            _layoutCheckedAt = now;
        }
        ApplyKeyboardLayout(server);
    }

    /// <summary>设置里手选了布局:随程序带的键位表(不再跟随系统);没手选、或选的名字表里没有时为 null。</summary>
    private HostKeymapResult? ChosenKeymap() => _chosenLayout.Length != 0 ? HostKeymap.FromBundled(_chosenLayout) : null;

    private HostKeymapResult? BuildHostKeymap()
    {
        if (ChosenKeymap() is { } chosen)
        {
            return chosen;
        }
        if (OperatingSystem.IsWindows())
        {
            // Windows 上布局句柄没变就不必重算(句柄是逐线程的,取一次很便宜)。
            nint layout = WindowsKeymap.CurrentLayout();
            if (layout == 0 || layout == _keyboardLayout)
            {
                return null;
            }
            _keyboardLayout = layout;
            return WindowsKeymap.Build(layout);
        }
        if (OperatingSystem.IsMacOS())
        {
            return MacKeymap.Build();
        }
        return null;
    }

    /// <summary>
    /// 当前布局有 AltGr 层(macOS 上是 Option 层)。Windows 上这时按 AltGr 系统会先补一个假的左 Ctrl 按下,
    /// 窗口要把它从 X 那边撤掉(见 <see cref="XNativeWindow" />)。
    /// </summary>
    public bool HasAltGr { get; private set; }

    private static Window? MainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main } ? main : null;

    // ================================================================== 服务端回调(执行线程上来)

    /// <inheritdoc />
    public void TopLevelMapped(XTopLevelWindow window) => Dispatcher.UIThread.Post(() => Map(window));

    /// <inheritdoc />
    public void TopLevelUnmapped(XTopLevelWindow window) => Dispatcher.UIThread.Post(() =>
    {
        _desktops.Remove(window);
        if (_windows.Remove(window, out XNativeWindow? native))
        {
            CloseWithOwnedWindows(native);
        }
    });

    /// <summary>
    /// 收掉一个原生窗口:Avalonia 关 owner 时连带关掉它拥有的窗口(对话框、瞬态窗口)。它们在 X 里可能还映射着 ——
    /// 主窗口先于对话框取消映射、程序只把主窗口藏起来 —— 那样就成了看不见的幽灵。所以先把它们一起收掉,
    /// 再把 X 里还映射着的不带 owner 重新显示。
    /// </summary>
    private void CloseWithOwnedWindows(XNativeWindow native)
    {
        XNativeWindow[] owned = [.. _windows.Values.Where(w => ReferenceEquals(w.Owner, native))];
        foreach (XNativeWindow child in owned)
        {
            _windows.Remove(child.Handle);
            child.MarkClosingByHost();
        }
        native.CloseByHost();
        foreach (XNativeWindow child in owned)
        {
            child.CloseByHost();   // 已经随 owner 关了的,再关一次是空操作
            if (child.Handle.Snapshot.IsMapped)
            {
                Map(child.Handle);
            }
        }
    }

    /// <inheritdoc />
    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes) => Dispatcher.UIThread.Post(() =>
    {
        if (_windows.TryGetValue(window, out XNativeWindow? native))
        {
            if (IsDesktop(window.Snapshot))
            {
                // 映射之后才把类型改成桌面:收掉原生窗口(见 IsDesktop)。
                _windows.Remove(window);
                _desktops.Add(window);
                CloseWithOwnedWindows(native);
                return;
            }
            native.ApplyProperties(changes);
        }
        else if (_desktops.Contains(window) && !IsDesktop(window.Snapshot))
        {
            _desktops.Remove(window);   // 不再是桌面了:照常给一个原生窗口
            Map(window);
        }
    });

    /// <summary>
    /// 桌面类窗口(<c>_NET_WM_WINDOW_TYPE_DESKTOP</c>:xfdesktop、caja、pcmanfm 画图标的底层窗口)不给原生窗口。rootless 下没有能放它的
    /// 「桌面底层」:原先它成了一个与整个虚拟桌面一样大、无边框的普通窗口,一激活就挡住本机所有程序(xs_plan CP-24)。
    /// 它在 X 里照常映射着,只是看不见。
    /// </summary>
    private static bool IsDesktop(XTopLevelSnapshot snapshot) => snapshot.WindowType == XWindowType.Desktop && !snapshot.OverrideRedirect;

    /// <inheritdoc />
    /// <remarks>
    /// 执行线程每放一次锁就可能报一批损伤(负载重时一秒几百批):先按窗口攒在这里,UI 线程上同一时刻最多排着一次投递,
    /// 不为每批各 Post 一次。像素由窗口在下一帧按攒下的矩形去取。
    /// </remarks>
    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
    {
        lock (_damageGate)
        {
            if (!_incomingDamage.TryGetValue(window, out List<XRect>? rects))
            {
                _incomingDamage[window] = rects = [];
            }
            rects.AddRange(damage);
            if (rects.Count > MaxQueuedDamageRects)
            {
                int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
                foreach (XRect r in rects)
                {
                    (x1, y1) = (Math.Min(x1, r.X), Math.Min(y1, r.Y));
                    (x2, y2) = (Math.Max(x2, r.X + r.Width), Math.Max(y2, r.Y + r.Height));
                }
                rects.Clear();
                rects.Add(new XRect(x1, y1, x2 - x1, y2 - y1));
            }
            if (_damagePosted)
            {
                return;
            }
            _damagePosted = true;
        }
        Dispatcher.UIThread.Post(_deliverDamage, DispatcherPriority.Render);
    }

    /// <summary>UI 线程:把攒下的损伤交给各自的原生窗口。</summary>
    private void DeliverDamage()
    {
        Dictionary<XTopLevelWindow, List<XRect>> batch;
        lock (_damageGate)
        {
            (batch, _incomingDamage, _deliveringDamage) = (_incomingDamage, _deliveringDamage, _incomingDamage);
            _damagePosted = false;
        }
        foreach ((XTopLevelWindow handle, List<XRect> rects) in batch)
        {
            if (_windows.TryGetValue(handle, out XNativeWindow? native))
            {
                native.AddDamage(rects);
            }
            else
            {
                _trayIcons.Damaged(handle);   // 托盘图标重画了:过一会儿换图标
            }
        }
        batch.Clear();
    }

    /// <inheritdoc />
    public void CursorChanged(XTopLevelWindow? window, XCursor cursor) => Dispatcher.UIThread.Post(() =>
    {
        if (window is not null && _windows.TryGetValue(window, out XNativeWindow? native))
        {
            native.ApplyCursor(cursor);
        }
    });

    /// <inheritdoc />
    public void BellRequested(int volume)
    {
        if (volume > 0)   // 系统提示音没有音量可调;音量 0(xset b 0、Bell -100)就是不响
        {
            Dispatcher.UIThread.Post(SystemSound.Alert);
        }
    }

    /// <inheritdoc />
    public void SystemTrayIconAdded(XTopLevelWindow icon, string title) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (IsCurrent(icon))
            {
                _trayIcons.Add(icon, title);
            }
        });

    /// <inheritdoc />
    public void SystemTrayIconRemoved(XTopLevelWindow icon) => Dispatcher.UIThread.Post(() => _trayIcons.Remove(icon));

    /// <inheritdoc />
    public event EventHandler<XServerGrabStall>? GrabStallReported;

    /// <summary>
    /// 有个客户端抓着整个服务端太久:原样转给 <see cref="GrabStallReported" />(<c>BuiltInLocalXServer</c> 查出是哪个程序,
    /// 标题栏的 X Server 按钮据此提示用户断开它)。在服务端的执行线程上,不切 UI 线程。
    /// </summary>
    public void ServerGrabStalled(XServerGrabStall stall) => GrabStallReported?.Invoke(this, stall);

    /// <inheritdoc />
    public void ClipboardChanged(string text) => Dispatcher.UIThread.Post(() => FireAndForget.Run(async () =>
    {
        _lastClipboard = text;
        if (MainWindow()?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[XServer] clipboard write failed: {ex.Message}");
            }
        }
    }));

    /// <inheritdoc />
    public void WindowManagerRequested(XWindowManagerRequest request) => Dispatcher.UIThread.Post(() =>
    {
        if (!_windows.TryGetValue(request.Window, out XNativeWindow? native))
        {
            return;
        }
        switch (request)
        {
            case XMoveResizeRequest move:
                native.BeginInteractive(move.Direction);
                break;
            case XStateChangeRequest state:
                native.ApplyStateRequest(state.Add, state.Remove);
                break;
            case XActivateRequest activate when activate.UserInitiated && XActive:
                native.Activate();
                break;
            case XActivateRequest:
                // 不是用户操作引起的(CurrentTime、过期的时间戳),或用户此刻在用本机窗口:只闪任务栏,不切前台 ——
                // 原先无条件激活,远端程序能在用户输 sudo 口令时跳到前台接走按键。
                WindowAttention.Request(native);
                break;
            case XRaiseRequest when !ReferenceEquals(native, _windows.Values.FirstOrDefault(w => w.IsActive))
                                    && _windows.Values.Any(w => w.IsActive):
                // 只是抬高次序:用户此刻正在用这个 X 程序(另一个 X 窗口是活动的)才照办,不从本机窗口那里抢走前台。
                native.Activate();
                break;
            case XFocusRequest when !native.IsActive && _windows.Values.Any(w => w.IsActive):
                // X 客户端自己把键盘焦点挪到了这个窗口:按键已经送往它,把它的原生窗口激活,用户才看得出键盘去了哪儿。
                // 用户正在用本机的其它窗口时不抢前台 —— 那时按键本来就不进 X,用户回到某个 X 窗口时焦点随激活重新给出。
                native.Activate();
                break;
            case XMinimizeRequest:
                native.WindowState = WindowState.Minimized;
                break;
            case XCloseRequest:
                CurrentServer(request.Window)?.CloseTopLevel(request.Window);
                break;
            case XNotRespondingRequest:
                FireAndForget.Run(() => ConfirmKillAsync(native, request.Window));
                break;
        }
    });

    /// <summary>
    /// 用户点了关闭,窗口却对 <c>_NET_WM_PING</c> 没有回应(程序卡住了):问用户要不要强制结束这个 X 程序 ——
    /// 原先声明了 WM_DELETE_WINDOW 却卡死的程序关不掉,只能停掉整个 X Server,所有会话的程序一起断。
    /// </summary>
    private async Task ConfirmKillAsync(XNativeWindow native, XTopLevelWindow handle)
    {
        XTopLevelSnapshot snapshot = handle.Snapshot;
        string name = snapshot.Title.Length > 0 ? snapshot.Title : snapshot.ClassName;
        bool kill = await MessageDialog.ConfirmAsync(native,
            Strings.Get("XServer_NotRespondingTitle"),
            Strings.Format("XServer_NotRespondingMessage", name),
            Strings.Get("XServer_ForceQuit"),
            kind: MessageDialogKind.Warning,
            danger: true);
        if (kill)
        {
            CurrentServer(handle)?.KillTopLevelClient(handle);   // 等用户回答期间服务端可能已经换了一个
        }
    }

    // ================================================================== UI 线程

    private void Map(XTopLevelWindow handle)
    {
        // 旧服务端的句柄、已经没了的窗口不建;InputOnly 的顶层(GtkInvisible 之类)看不见:不开原生窗口(原先多出一个黑窗口)。
        if (!IsCurrent(handle) || !handle.IsAlive || _windows.ContainsKey(handle) || handle.Snapshot.InputOnly)
        {
            return;
        }
        if (IsDesktop(handle.Snapshot))
        {
            _desktops.Add(handle);
            return;
        }
        XNativeWindow window = new(this, handle);
        _windows[handle] = window;
        if (!window.IsScreen)
        {
            PlaceIfUnpositioned(handle, window);
        }
        window.ApplyProperties(XTopLevelChanges.All);
        window.ApplyInitialStates();   // 映射前就设好的最大化 / 全屏 / initial_state = Iconic

        // 对话框、瞬态窗口(连同声明了 WM_TRANSIENT_FOR 的弹出菜单)压在父窗口之上。没声明的弹层不借用「当前活动的 X 窗口」当 owner:
        // 那个窗口可能属于别的程序甚至别的会话,owner 关闭时会把它连带关掉(弹层本身照样置顶,不需要 owner)。
        XTopLevelSnapshot snapshot = handle.Snapshot;
        XNativeWindow? owner = snapshot.TransientFor is { } transientFor && _windows.TryGetValue(transientFor, out XNativeWindow? parent)
            ? parent
            : null;
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// 客户端没给位置的普通窗口像窗口管理器那样摆:对话框居中压在父窗口上,其余放在主显示器工作区正中。「没给位置」指请求的位置是 (0, 0)
    /// 而且不是用户指定的(没有 USPosition;程序自己设的 PPosition 在 (0, 0) 时多半只是默认值)—— 原先只看坐标是不是 0,
    /// <c>xterm -geometry +0+0</c> 也被挪到屏幕中央。给了位置的由原生窗口按重力摆外框(见 <see cref="XNativeWindow.ApplyGeometry" />)。
    /// </summary>
    private void PlaceIfUnpositioned(XTopLevelWindow handle, XNativeWindow window)
    {
        XTopLevelSnapshot snapshot = handle.Snapshot;
        if (!snapshot.NeedsPlacement || snapshot.UserPosition || snapshot.X != 0 || snapshot.Y != 0)
        {
            return;
        }
        (int ox, int oy) = RootOrigin;
        PixelRect area;
        if (snapshot.TransientFor is { } transientFor && _windows.TryGetValue(transientFor, out XNativeWindow? parent))
        {
            XTopLevelSnapshot p = parent.Handle.Snapshot;
            area = new PixelRect(p.X + p.BorderWidth + ox, p.Y + p.BorderWidth + oy, p.Width, p.Height);
        }
        else if ((MainWindow()?.Screens ?? window.Screens).Primary is { } primary)
        {
            area = primary.WorkingArea;
        }
        else
        {
            return;
        }
        // 居中的是外框(内容区加系统边框);边框尺寸要等显示出来才量得到,之前按 0 算。
        XFrameExtents frame = window.FrameExtents;
        int outerWidth = snapshot.Width + frame.Left + frame.Right, outerHeight = snapshot.Height + frame.Top + frame.Bottom;
        window.PlaceFrameAt(area.X + Math.Max(0, (area.Width - outerWidth) / 2) - ox, area.Y + Math.Max(0, (area.Height - outerHeight) / 2) - oy);
    }

    /// <summary>某个 X 窗口成了活动窗口:键盘焦点给它;顺带把系统剪贴板里别的程序复制的新文本交给 X。</summary>
    public void OnWindowActivated(XNativeWindow window)
    {
        if (CurrentServer(window.Handle) is not { } server)
        {
            return;
        }
        UpdateTopmost(xActive: true);
        server.FocusTopLevel(window.Handle);
        if (HostLockState.Read() is var (capsLock, numLock))
        {
            server.SetLockState(capsLock, numLock);   // 用户可能在别的程序里切过 CapsLock / NumLock(Linux 上随键位表在后台读)
        }
        ApplyKeyboardLayout(server);   // 用户可能在别的程序里切了输入法 / 布局
        FireAndForget.Run(() => OfferSystemClipboardAsync(server, window));
    }

    /// <summary>系统剪贴板里有别的程序复制的新文本时交给 X(没有剪贴板变化事件可用,活动窗口切进来时看一眼)。</summary>
    private async Task OfferSystemClipboardAsync(X11Server server, XNativeWindow window)
    {
        try
        {
            if (window.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text
                && text != _lastClipboard)
            {
                _lastClipboard = text;
                server.SetClipboardText(text);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[XServer] clipboard read failed: {ex.Message}");
        }
    }

    /// <summary>某个 X 窗口不再活动:如果活动窗口也不是别的 X 窗口,X 这边就没有焦点。</summary>
    public void OnWindowDeactivated(XNativeWindow window) => Dispatcher.UIThread.Post(() =>
    {
        if (_server is { } server && !_windows.Values.Any(w => w.IsActive))
        {
            server.FocusTopLevel(null);
        }
        if (!XActive)
        {
            UpdateTopmost(xActive: false);   // 用户回到了本机窗口:X 的弹出层与「总在最前」的窗口退到后面
        }
    });

    private void UpdateTopmost(bool xActive)
    {
        foreach (XNativeWindow window in _windows.Values)
        {
            window.UpdateTopmost(xActive);
        }
    }

    /// <summary>原生窗口已关闭(宿主关的,或系统强制关的)。</summary>
    public void OnWindowClosed(XNativeWindow window)
    {
        if (_windows.TryGetValue(window.Handle, out XNativeWindow? current) && ReferenceEquals(current, window))
        {
            _windows.Remove(window.Handle);
        }
    }

    /// <summary>窗口图标:取不超过 256 的最大一幅(<c>_NET_WM_ICON</c> 是非预乘的 ARGB)。同一份图标只转换一次。</summary>
    public WindowIcon? IconFor(IReadOnlyList<XWindowIcon> icons)
    {
        if (ReferenceEquals(_iconCache.Source, icons))
        {
            return _iconCache.Icon;
        }
        XWindowIcon? best = icons.Where(i => i.Width <= 256 && i.Height <= 256).MaxBy(i => i.Width * i.Height);
        WindowIcon? icon = null;
        if (best is { Width: > 0, Height: > 0 })
        {
            // 位图归图标所有,不释放(图标换掉时随缓存一起被回收)。
            WriteableBitmap bitmap = new(new PixelSize(best.Width, best.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (ILockedFramebuffer frame = bitmap.Lock())
            {
                int[] row = new int[best.Width];
                for (int y = 0; y < best.Height; y++)
                {
                    System.Runtime.InteropServices.MemoryMarshal.Cast<uint, int>(best.Pixels.Span.Slice(y * best.Width, best.Width)).CopyTo(row);
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + (y * frame.RowBytes), best.Width);
                }
            }
            icon = new WindowIcon(bitmap);
        }
        _iconCache = (icons, icon);
        return icon;
    }
}
