using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Infrastructure.XServer;
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
    private readonly Dictionary<uint, XNativeWindow> _windows = [];
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
    private Dictionary<uint, List<XRect>> _incomingDamage = [];
    private Dictionary<uint, List<XRect>> _deliveringDamage = [];
    private bool _damagePosted;

    /// <summary>新建一个宿主;经 <see cref="AttachAsync" /> 接到服务端上。</summary>
    public AvaloniaXServerHost() => _deliverDamage = DeliverDamage;

    /// <summary>当前附着的服务端;没在运行时为 <see langword="null" />。窗口的注入经它走。</summary>
    public X11Server? Server => _server;

    /// <summary>当前开着的原生窗口(UI 线程上读;测试用)。</summary>
    internal IReadOnlyCollection<XNativeWindow> Windows => _windows.Values;

    /// <summary>根窗口原点在系统虚拟桌面里的位置(物理像素)。只在 UI 线程上读写。</summary>
    public (int X, int Y) RootOrigin { get; private set; }

    // ================================================================== 生命周期

    /// <inheritdoc />
    public async Task AttachAsync(X11Server server, CancellationToken cancellationToken)
    {
        _server = server;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _keyboardLayout = 0;
            _appliedKeymap = null;   // 新起的服务端是 US 键位表:按当前布局重推一次
            ApplyKeyboardLayout(server);
            if (MainWindow() is { } main)
            {
                ApplyLayout(server, main.Screens);
                if (!ReferenceEquals(_watchedScreens, main.Screens))
                {
                    _watchedScreens = main.Screens;
                    _watchedScreens.Changed += (_, _) =>
                    {
                        if (_server is { } current && _watchedScreens is { } screens)
                        {
                            ApplyLayout(current, screens);
                        }
                    };
                }
            }
        }, DispatcherPriority.Normal, cancellationToken);
    }

    /// <inheritdoc />
    public void Detach()
    {
        _server = null;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (XNativeWindow window in _windows.Values.ToArray())
            {
                window.CloseByHost();
            }
            _windows.Clear();
        });
    }

    /// <summary>
    /// 显示器布局与 DPI 告诉服务端:根窗口是所有显示器的外接矩形,每台一个输出;DPI 取主显示器的缩放。
    /// </summary>
    private void ApplyLayout(X11Server server, Screens screens)
    {
        IReadOnlyList<Screen> all = screens.All;
        if (all.Count == 0)
        {
            return;
        }
        int minX = all.Min(s => s.Bounds.X), minY = all.Min(s => s.Bounds.Y);
        int maxX = all.Max(s => s.Bounds.Right), maxY = all.Max(s => s.Bounds.Bottom);
        RootOrigin = (minX, minY);
        List<XMonitor> monitors = [];
        for (int i = 0; i < all.Count; i++)
        {
            Screen screen = all[i];
            PixelRect b = screen.Bounds;
            double dpi = 96 * Math.Max(1, screen.Scaling);
            monitors.Add(new XMonitor(b.X - minX, b.Y - minY, b.Width, b.Height)
            {
                Name = string.IsNullOrWhiteSpace(screen.DisplayName) ? $"SCREEN-{i + 1}" : screen.DisplayName,
                Primary = screen.IsPrimary,
                WidthMillimeters = (int)Math.Round(b.Width / dpi * 25.4),
                HeightMillimeters = (int)Math.Round(b.Height / dpi * 25.4),
            });
        }
        server.SetScreenLayout(maxX - minX, maxY - minY, monitors);

        // 分数缩放(125%、150%)只调 DPI,整数倍(200%)时再让 GTK 按倍数放大控件 —— GTK 的窗口缩放只认整数。
        double scaling = (screens.Primary ?? all[0]).Scaling;
        int scale = scaling >= 2 ? (int)Math.Floor(scaling) : 1;
        server.SetDisplayScale(Math.Max(1, (int)Math.Round(96 * scaling)), scale);
    }

    /// <summary>
    /// 按键盘布局换服务端的键位表:设置里手选了布局时用随程序带的表(<see cref="BundledKeymaps" />);否则跟随系统当前的布局 ——
    /// Windows 见 <see cref="WindowsKeymap" />,macOS 见 <see cref="MacKeymap" />,Linux 见 <see cref="LinuxKeymap" />。
    /// 算出来的与上次推给服务端的一样时什么也不做;取不到布局时沿用服务端内置的 US 键位表。
    /// </summary>
    private void ApplyKeyboardLayout(X11Server server)
    {
        HostKeymapResult? keymap;
        try
        {
            keymap = BuildHostKeymap(server);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return;   // 系统库缺了哪一个:沿用现在的键位表
        }
        if (keymap is null || keymap.SameAs(_appliedKeymap))
        {
            return;
        }
        _appliedKeymap = keymap;
        // 一次交过去:键值、右 Alt 是不是 AltGr(macOS 上是右 Option)、布局名 —— 服务端只通知客户端一轮。
        HasAltGr = keymap.HasAltGr;
        server.SetKeymap(keymap.ToXKeymap());
    }

    /// <inheritdoc />
    public void UseKeyboardLayout(string layout) => _chosenLayout = layout ?? "";

    private HostKeymapResult? BuildHostKeymap(X11Server server)
    {
        // 设置里手选了布局:用随程序带的键位表,不再跟随系统。
        if (_chosenLayout.Length != 0 && HostKeymap.FromBundled(_chosenLayout) is { } chosen)
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
        if (OperatingSystem.IsLinux())
        {
            return LinuxKeymap.Build(server.DisplayNumber);
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
        if (_windows.Remove(window.Id, out XNativeWindow? native))
        {
            native.CloseByHost();
        }
    });

    /// <inheritdoc />
    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes) => Dispatcher.UIThread.Post(() =>
    {
        if (_windows.TryGetValue(window.Id, out XNativeWindow? native))
        {
            native.ApplyProperties(changes);
        }
    });

    /// <inheritdoc />
    /// <remarks>
    /// 执行线程每放一次锁就可能报一批损伤(负载重时一秒几百批):先按窗口攒在这里,UI 线程上同一时刻最多排着一次投递,
    /// 不为每批各 Post 一次。像素由窗口在下一帧按攒下的矩形去取。
    /// </remarks>
    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
    {
        lock (_damageGate)
        {
            if (!_incomingDamage.TryGetValue(window.Id, out List<XRect>? rects))
            {
                _incomingDamage[window.Id] = rects = [];
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
        Dictionary<uint, List<XRect>> batch;
        lock (_damageGate)
        {
            (batch, _incomingDamage, _deliveringDamage) = (_incomingDamage, _deliveringDamage, _incomingDamage);
            _damagePosted = false;
        }
        foreach ((uint id, List<XRect> rects) in batch)
        {
            if (_windows.TryGetValue(id, out XNativeWindow? native))
            {
                native.AddDamage(rects);
            }
        }
        batch.Clear();
    }

    /// <inheritdoc />
    public void CursorChanged(XTopLevelWindow? window, XCursor cursor) => Dispatcher.UIThread.Post(() =>
    {
        if (window is not null && _windows.TryGetValue(window.Id, out XNativeWindow? native))
        {
            native.ApplyCursor(cursor);
        }
    });

    /// <inheritdoc />
    public void BellRequested(int volume) => Dispatcher.UIThread.Post(SystemSound.Alert);   // 系统提示音没有音量可调

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
        if (!_windows.TryGetValue(request.Window.Id, out XNativeWindow? native))
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
            case XActivateRequest:
                native.Activate();
                break;
            case XMinimizeRequest:
                native.WindowState = WindowState.Minimized;
                break;
            case XCloseRequest:
                _server?.CloseTopLevel(request.Window);
                break;
        }
    });

    // ================================================================== UI 线程

    private void Map(XTopLevelWindow handle)
    {
        if (_server is null || _windows.ContainsKey(handle.Id))
        {
            return;
        }
        XNativeWindow window = new(this, handle);
        _windows[handle.Id] = window;
        PlaceIfUnpositioned(handle, window);
        window.ApplyProperties(XTopLevelChanges.All);

        // 对话框、瞬态窗口压在父窗口之上;弹出菜单跟着当前活动的 X 窗口走。
        XTopLevelSnapshot snapshot = handle.Snapshot;
        XNativeWindow? owner = snapshot.TransientFor is { } transientFor && _windows.TryGetValue(transientFor.Id, out XNativeWindow? parent)
            ? parent
            : snapshot.OverrideRedirect ? _windows.Values.FirstOrDefault(w => w.IsActive) : null;
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
    /// 客户端没给位置(映射在 0,0)的普通窗口,像窗口管理器那样摆:对话框居中压在父窗口上,其余放在主显示器工作区正中。
    /// </summary>
    private void PlaceIfUnpositioned(XTopLevelWindow handle, XNativeWindow window)
    {
        XTopLevelSnapshot snapshot = handle.Snapshot;
        if (snapshot.OverrideRedirect || snapshot.X != 0 || snapshot.Y != 0 || _server is not { } server)
        {
            return;
        }
        PixelRect area;
        if (snapshot.TransientFor is { } transientFor && _windows.TryGetValue(transientFor.Id, out XNativeWindow? parent))
        {
            (int ox, int oy) = RootOrigin;
            XTopLevelSnapshot p = parent.Handle.Snapshot;
            area = new PixelRect(p.X + ox, p.Y + oy, p.Width, p.Height);
        }
        else if ((MainWindow()?.Screens ?? window.Screens).Primary is { } primary)
        {
            area = primary.WorkingArea;
        }
        else
        {
            return;
        }
        int x = area.X + Math.Max(0, (area.Width - snapshot.Width) / 2) - RootOrigin.X;
        int y = area.Y + Math.Max(0, (area.Height - snapshot.Height) / 2) - RootOrigin.Y;
        window.PlaceAt(x, y);
        server.MoveTopLevel(handle, x, y);
    }

    /// <summary>某个 X 窗口成了活动窗口:键盘焦点给它;顺带把系统剪贴板里别的程序复制的新文本交给 X。</summary>
    public void OnWindowActivated(XNativeWindow window)
    {
        if (_server is not { } server)
        {
            return;
        }
        server.FocusTopLevel(window.Handle);
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
    });

    /// <summary>原生窗口已关闭(宿主关的,或系统强制关的)。</summary>
    public void OnWindowClosed(XNativeWindow window)
    {
        if (_windows.TryGetValue(window.Handle.Id, out XNativeWindow? current) && ReferenceEquals(current, window))
        {
            _windows.Remove(window.Handle.Id);
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
                    Buffer.BlockCopy(best.Pixels, y * best.Width * 4, row, 0, best.Width * 4);
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + (y * frame.RowBytes), best.Width);
                }
            }
            icon = new WindowIcon(bitmap);
        }
        _iconCache = (icons, icon);
        return icon;
    }
}
