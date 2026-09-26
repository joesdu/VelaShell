using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Services.XServer;
using VelaShell.XServer;

namespace VelaShell.Views.XServer;

/// <summary>
/// 一个 X 顶层窗口对应的原生窗口:画它的像素,把指针、键盘、移动、缩放、关闭交回服务端。
/// </summary>
/// <remarks>
/// <para>
/// <b>坐标一律按物理像素。</b>X 客户端看到的根窗口是整个虚拟桌面(所有显示器的外接矩形,原点平移到 0,0),
/// 顶层的 X / Y 是它<b>内容区</b>在根窗口里的位置;原生窗口的系统标题栏与边框在内容区之外,
/// 尺寸经 <c>_NET_FRAME_EXTENTS</c> 告诉客户端。DIP 只在给 Avalonia 设尺寸时换算一次。
/// </para>
/// <para>
/// 只在 UI 线程上碰;服务端的回调由 <see cref="AvaloniaXServerHost" /> 切过来。
/// </para>
/// </remarks>
public sealed class XNativeWindow : Window
{
    private readonly AvaloniaXServerHost _host;
    private readonly XSurface _surface;
    private readonly HashSet<byte> _heldKeys = [];
    private PointerPressedEventArgs? _lastPress;
    private bool _buttonHeld;
    private bool _applying;
    private bool _closingByHost;
    private Vector _wheelRemainder;
    private XFrameExtents _frame;
    private XWindowStates _reportedStates;
    private (int X, int Y)? _placed;
    private WindowState _resizeState = WindowState.Normal;
    private long _controlLeftDownAt;

    internal XNativeWindow(AvaloniaXServerHost host, XTopLevelWindow handle)
    {
        _host = host;
        Handle = handle;
        _surface = new XSurface(this, handle);
        Content = _surface;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = true;
        ApplyStyle(handle.Snapshot);

        PositionChanged += (_, _) => OnMovedByUser();
        Resized += OnResized;
        Activated += (_, _) => _host.OnWindowActivated(this);
        Deactivated += (_, _) => OnDeactivated();
        ScalingChanged += (_, _) => ApplyGeometry();
        Opened += (_, _) => { UpdateFrameExtents(); ApplyGeometry(); _surface.Start(); };
    }

    /// <summary>服务端那边的顶层窗口。</summary>
    public XTopLevelWindow Handle { get; }

    private X11Server? Server => _host.Server;

    private double Scale => RenderScaling > 0 ? RenderScaling : 1;

    // ================================================================== 服务端 → 窗口

    /// <summary>快照里的属性变了(映射时按 <see cref="XTopLevelChanges.All" /> 调一次):只重新应用变了的那几组。</summary>
    public void ApplyProperties(XTopLevelChanges changes)
    {
        XTopLevelSnapshot s = Handle.Snapshot;
        if ((changes & XTopLevelChanges.Title) != 0)
        {
            Title = s.Title.Length > 0 ? s.Title : s.ClassName;
        }
        if ((changes & (XTopLevelChanges.Hints | XTopLevelChanges.States | XTopLevelChanges.Shape)) != 0)
        {
            ApplyStyle(s);
        }
        if ((changes & (XTopLevelChanges.Hints | XTopLevelChanges.Shape)) != 0)
        {
            _surface.PropertiesChanged();
        }
        if ((changes & XTopLevelChanges.Hints) != 0)
        {
            Opacity = Math.Clamp(s.Opacity, 0.05, 1);
            double scale = Scale;
            MinWidth = s.MinWidth > 0 ? s.MinWidth / scale : 0;
            MinHeight = s.MinHeight > 0 ? s.MinHeight / scale : 0;
            MaxWidth = s.MaxWidth > 0 ? s.MaxWidth / scale : double.PositiveInfinity;
            MaxHeight = s.MaxHeight > 0 ? s.MaxHeight / scale : double.PositiveInfinity;
            CanResize = !s.OverrideRedirect && (s.MaxWidth == 0 || s.MaxWidth != s.MinWidth || s.MaxHeight != s.MinHeight);
        }
        if ((changes & XTopLevelChanges.Icons) != 0 && s.Icons.Count > 0 && _host.IconFor(s.Icons) is { } icon)
        {
            Icon = icon;
        }
        if ((changes & (XTopLevelChanges.Geometry | XTopLevelChanges.Hints)) != 0)
        {
            ApplyGeometry();
        }
    }

    /// <summary>
    /// 宿主替没给位置的窗口选了一个位置(已经告诉服务端,但服务端那边的几何还没更新过来):在那之前按这个摆。
    /// </summary>
    public void PlaceAt(int x, int y) => _placed = (x, y);

    /// <summary>按服务端的几何摆放原生窗口。</summary>
    public void ApplyGeometry()
    {
        if (WindowState is WindowState.Maximized or WindowState.FullScreen)
        {
            return;   // 最大化 / 全屏时尺寸由系统定,再按旧几何摆会把它拉回去
        }
        double scale = Scale;
        _applying = true;
        try
        {
            // 设 Width / Height(内容区的 DIP 尺寸)才会真的改原生窗口;显示之后再设 ClientSize 只改了属性值。
            XTopLevelSnapshot s = Handle.Snapshot;
            Width = Math.Max(1, s.Width) / scale;
            Height = Math.Max(1, s.Height) / scale;
            (int ox, int oy) = _host.RootOrigin;
            (int x, int y) = (s.X, s.Y);
            if (_placed is { } placed)
            {
                if (placed == (x, y))
                {
                    _placed = null;   // 服务端已经跟上
                }
                (x, y) = placed;
            }
            Position = new PixelPoint(x + ox - _frame.Left, y + oy - _frame.Top);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>有内容画进来了(顶层内区坐标的矩形):下一帧取这几块像素。</summary>
    public void AddDamage(IReadOnlyList<XRect> damage) => _surface.AddDamage(damage);

    /// <summary>服务端要求的光标:有图像(位图 / ARGB 光标)就显示图像,否则按形状选系统光标。</summary>
    public void ApplyCursor(XCursor cursor)
    {
        if (cursor is { Image: { } image, Shape: not XCursorShape.Hidden })
        {
            Cursor = ImageCursors.GetValue(image, CreateImageCursor);
            return;
        }
        StandardCursorType type = XInputMap.Cursor(cursor.Shape);
        if (!StandardCursors.TryGetValue(type, out Cursor? standard))
        {
            StandardCursors[type] = standard = new Cursor(type);   // 指针每跨一个控件就换一次光标:同一种只建一次
        }
        Cursor = standard;
    }

    /// <summary>建过的系统光标(UI 线程上用)。</summary>
    private static readonly Dictionary<StandardCursorType, Cursor> StandardCursors = [];

    /// <summary>按图像建过的光标:服务端对同一个光标总给同一份图像;图像没人引用了,光标随之回收。</summary>
    private static readonly ConditionalWeakTable<XCursorImage, Cursor> ImageCursors = new();

    private static unsafe Cursor CreateImageCursor(XCursorImage image)
    {
        // 预乘的 0xAARRGGBB 按小端字节序就是预乘的 BGRA:逐行拷进位图。
        WriteableBitmap bitmap = new(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer frame = bitmap.Lock())
        {
            for (int y = 0; y < image.Height; y++)
            {
                MemoryMarshal.AsBytes(image.Pixels.AsSpan(y * image.Width, image.Width))
                    .CopyTo(new Span<byte>((void*)(frame.Address + (y * frame.RowBytes)), image.Width * 4));
            }
        }
        return new Cursor(bitmap, new PixelPoint(image.HotspotX, image.HotspotY));
    }

    /// <summary>宿主要关它(客户端取消映射 / 销毁、服务端停下)。</summary>
    public void CloseByHost()
    {
        _closingByHost = true;
        Close();
    }

    /// <summary>客户端经 <c>_NET_WM_MOVERESIZE</c> 要求拖动 / 缩放:用系统的拖动循环(要一次还按着的按下事件)。</summary>
    public void BeginInteractive(XMoveResizeDirection direction)
    {
        if (_lastPress is not { } press || !_buttonHeld)
        {
            return;
        }
        if (direction == XMoveResizeDirection.Move)
        {
            this.BeginWindowMoveDrag(press);   // 走统一入口:修掉系统拖动循环结束后那条 (0,0) 的幽灵弹起(#264)
            return;
        }
        WindowEdge? edge = direction switch
        {
            XMoveResizeDirection.SizeTopLeft => WindowEdge.NorthWest,
            XMoveResizeDirection.SizeTop => WindowEdge.North,
            XMoveResizeDirection.SizeTopRight => WindowEdge.NorthEast,
            XMoveResizeDirection.SizeRight => WindowEdge.East,
            XMoveResizeDirection.SizeBottomRight => WindowEdge.SouthEast,
            XMoveResizeDirection.SizeBottom => WindowEdge.South,
            XMoveResizeDirection.SizeBottomLeft => WindowEdge.SouthWest,
            XMoveResizeDirection.SizeLeft => WindowEdge.West,
            _ => null,
        };
        if (edge is { } e && CanResize)
        {
            BeginResizeDrag(e, press);
        }
    }

    /// <summary>客户端要求改状态(最大化、全屏、最小化……)。</summary>
    public void ApplyStateRequest(XWindowStates add, XWindowStates remove)
    {
        XWindowStates target = (StatesFromWindow() | add) & ~remove;
        WindowState = (target & XWindowStates.Fullscreen) != 0 ? WindowState.FullScreen
            : (target & XWindowStates.Hidden) != 0 ? WindowState.Minimized
            : (target & XWindowStates.Maximized) == XWindowStates.Maximized ? WindowState.Maximized
            : WindowState.Normal;
        Topmost = (target & XWindowStates.Above) != 0 || Handle.Snapshot.OverrideRedirect;
        ReportStates();
    }

    // ================================================================== 窗口 → 服务端

    private void ApplyStyle(XTopLevelSnapshot s)
    {
        bool popup = s.OverrideRedirect;
        bool undecorated = popup || !s.Decorated
                           || s.WindowType is XWindowType.Splash or XWindowType.Tooltip or XWindowType.Notification
                               or XWindowType.Dnd or XWindowType.Dock or XWindowType.Desktop;
        WindowDecorations = undecorated ? WindowDecorations.None : WindowDecorations.Full;
        ShowInTaskbar = !popup && s.TransientFor is null && (s.States & XWindowStates.SkipTaskbar) == 0
                        && s.WindowType is XWindowType.Normal or XWindowType.Dialog;
        ShowActivated = !popup && s.AcceptsFocus;
        Topmost = popup || (s.States & XWindowStates.Above) != 0;
        CanMinimize = !popup;
        CanMaximize = !popup;
        // 有 alpha 的视觉(GTK 的客户端阴影、圆角)与非矩形窗口要透明底;其余不透明,省掉系统合成的开销。
        TransparencyLevelHint = s.HasAlpha || s.Shape is not null
            ? [WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.None];
    }

    private void OnMovedByUser()
    {
        if (_applying || Server is not { } server || WindowState is WindowState.Minimized)
        {
            return;
        }
        (int ox, int oy) = _host.RootOrigin;
        int x = Position.X + _frame.Left - ox, y = Position.Y + _frame.Top - oy;
        if (Handle.Snapshot is var s && (x != s.X || y != s.Y))
        {
            server.MoveTopLevel(Handle, x, y);
        }
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        UpdateFrameExtents();
        ReportStates();
        // 只有用户拖边框、以及窗口状态变了(最大化 / 全屏 / 还原)才回报给服务端。我们自己按服务端的几何设 ClientSize
        // 引起的 Resized 可能晚一拍才到,那时 _applying 早已复位 —— 再回报就会拿旧尺寸把客户端刚设的新尺寸改回去。
        bool stateChanged = WindowState != _resizeState;
        _resizeState = WindowState;
        if (_applying || (e.Reason != WindowResizeReason.User && !stateChanged)
            || Server is not { } server || WindowState is WindowState.Minimized)
        {
            return;
        }
        int width = (int)Math.Round(e.ClientSize.Width * Scale), height = (int)Math.Round(e.ClientSize.Height * Scale);
        if (width > 0 && height > 0 && Handle.Snapshot is var s && (width != s.Width || height != s.Height))
        {
            server.ResizeTopLevel(Handle, width, height);
        }
        OnMovedByUser();   // 最大化 / 左上角拖动缩放时位置也变了
    }

    private void OnDeactivated()
    {
        // 别的程序拿走了键盘:按着的键在 X 那边松开,免得 Alt+Tab 回来之后 Alt 一直按着。
        if (Server is { } server)
        {
            foreach (byte key in _heldKeys)
            {
                server.InjectKey(key, pressed: false);
            }
        }
        _heldKeys.Clear();
        _host.OnWindowDeactivated(this);
    }

    /// <summary>系统边框的尺寸(物理像素)告诉服务端(<c>_NET_FRAME_EXTENTS</c>),摆放时也用它把内容区对准 X 的坐标。</summary>
    private void UpdateFrameExtents()
    {
        XFrameExtents frame = default;
        if (WindowDecorations != WindowDecorations.None && FrameSize is { } outer)
        {
            double scale = Scale;
            int side = Math.Max(0, (int)Math.Round((outer.Width - ClientSize.Width) * scale / 2));
            int top = Math.Max(0, (int)Math.Round((outer.Height - ClientSize.Height) * scale) - side);
            frame = new XFrameExtents(side, side, top, side);
        }
        if (frame != _frame)
        {
            _frame = frame;
            Server?.SetTopLevelFrameExtents(Handle, _frame);
        }
    }

    private XWindowStates StatesFromWindow() => WindowState switch
    {
        WindowState.Maximized => XWindowStates.Maximized,
        WindowState.FullScreen => XWindowStates.Fullscreen,
        WindowState.Minimized => XWindowStates.Hidden,
        _ => XWindowStates.None,
    } | (Topmost && !Handle.Snapshot.OverrideRedirect ? XWindowStates.Above : XWindowStates.None);

    /// <summary>窗口状态(用户点了最大化、系统最小化……)写回 <c>_NET_WM_STATE</c>。</summary>
    private void ReportStates()
    {
        XWindowStates states = StatesFromWindow();
        if (states != _reportedStates)
        {
            _reportedStates = states;
            Server?.SetTopLevelStates(Handle, states);
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingByHost)
        {
            return;
        }
        // 关闭按钮 / Alt+F4:请客户端自己关(有 WM_DELETE_WINDOW 时),窗口等它取消映射再收。
        e.Cancel = true;
        Server?.CloseTopLevel(Handle);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _surface.Release();
        _host.OnWindowClosed(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        Server?.InjectPointerMotion(Handle, x, y);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPointProperties props = e.GetCurrentPoint(_surface).Properties;
        int button = XInputMap.Button(props.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            PointerUpdateKind.XButton1Pressed => MouseButton.XButton1,
            PointerUpdateKind.XButton2Pressed => MouseButton.XButton2,
            _ => MouseButton.None,
        });
        if (button == 0)
        {
            return;
        }
        _lastPress = e;
        _buttonHeld = true;
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        Server?.InjectPointerButton(Handle, x, y, button, pressed: true);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        int button = XInputMap.Button(e.InitialPressMouseButton);
        if (button == 0)
        {
            return;
        }
        _buttonHeld = false;
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        Server?.InjectPointerButton(Handle, x, y, button, pressed: false);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Server is not { } server)
        {
            return;
        }
        // X 的滚轮是按钮:每一格一次按下 + 松开(4 上、5 下、6 左、7 右)。触控板的小数增量攒够一格再发。
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        _wheelRemainder += e.Delta;
        while (Math.Abs(_wheelRemainder.Y) >= 1)
        {
            int button = _wheelRemainder.Y > 0 ? 4 : 5;
            server.InjectPointerButton(Handle, x, y, button, pressed: true);
            server.InjectPointerButton(Handle, x, y, button, pressed: false);
            _wheelRemainder = _wheelRemainder.WithY(_wheelRemainder.Y - Math.Sign(_wheelRemainder.Y));
        }
        while (Math.Abs(_wheelRemainder.X) >= 1)
        {
            int button = _wheelRemainder.X > 0 ? 6 : 7;
            server.InjectPointerButton(Handle, x, y, button, pressed: true);
            server.InjectPointerButton(Handle, x, y, button, pressed: false);
            _wheelRemainder = _wheelRemainder.WithX(_wheelRemainder.X - Math.Sign(_wheelRemainder.X));
        }
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_buttonHeld)
        {
            Server?.InjectPointerLeave();
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        byte keycode = XInputMap.Keycode(e.PhysicalKey);
        if (keycode == 0)
        {
            return;
        }
        if (e.PhysicalKey == PhysicalKey.F4 && e.KeyModifiers == KeyModifiers.Alt)
        {
            return;   // Alt+F4 留给系统:它走关闭按钮那条路
        }
        if (IsSyntheticAltGrControl(e.PhysicalKey))
        {
            e.Handled = true;
            return;
        }
        _heldKeys.Add(keycode);
        Server?.InjectKey(keycode, pressed: true);
        e.Handled = true;
    }

    /// <summary>
    /// Windows 上有 AltGr 的布局,按 AltGr 时系统先补一个假的左 Ctrl 按下(按住时连同自动重复一起补)。
    /// X 那边要是也看见 Ctrl,AltGr+Q 就成了 Ctrl+@ 一类的快捷键。认出来的就不转发:
    /// 左 Ctrl 按下之后几十毫秒内来了右 Alt —— 在 X 那边把刚才的左 Ctrl 松开;右 Alt 按着时再来的左 Ctrl 直接丢掉。
    /// 它们各自的弹起因为不在「按着的键」里,也不会转发。
    /// </summary>
    private bool IsSyntheticAltGrControl(PhysicalKey key)
    {
        if (!OperatingSystem.IsWindows() || !_host.HasAltGr)   // 补假左 Ctrl 的只有 Windows
        {
            return false;
        }
        if (key == PhysicalKey.ControlLeft)
        {
            _controlLeftDownAt = Environment.TickCount64;
            return _heldKeys.Contains(XKeycodes.AltRight);
        }
        if (key == PhysicalKey.AltRight && _heldKeys.Contains(XKeycodes.ControlLeft)
            && Environment.TickCount64 - _controlLeftDownAt < 50)
        {
            _heldKeys.Remove(XKeycodes.ControlLeft);
            Server?.InjectKey(XKeycodes.ControlLeft, pressed: false);
        }
        return false;
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        byte keycode = XInputMap.Keycode(e.PhysicalKey);
        if (keycode == 0 || !_heldKeys.Remove(keycode))
        {
            return;
        }
        Server?.InjectKey(keycode, pressed: false);
        e.Handled = true;
    }

    private (int X, int Y) ToPixels(Point point) =>
        ((int)Math.Floor(point.X * Scale), (int)Math.Floor(point.Y * Scale));

    /// <summary>
    /// 画顶层像素的控件:位图与窗口像素一一对应,按 1/缩放 的 DIP 尺寸画,不插值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>切成 256 × 256 的小块,每块一张位图。</b>位图一改,渲染层下一帧就把整张重新传给 GPU:整窗一张位图时,
    /// 光标闪一下也要传整窗(4K 窗口一帧 33 MB);切块之后只有被改到的块重传。
    /// </para>
    /// <para>
    /// <b>取像素跟着显示器的帧走</b>(<see cref="TopLevel.RequestAnimationFrame" />):一帧之内来多少批损伤都只取一次,
    /// 只取损伤矩形,在像素锁里直接从服务端的缓冲写进位图 —— 一趟拷贝,不经中转数组。
    /// </para>
    /// </remarks>
    private sealed class XSurface : Control
    {
        private const int TileSize = 256;

        /// <summary>攒着没取的损伤矩形上限;再多就合成外接矩形(取多了反而慢)。</summary>
        private const int MaxPendingRects = 32;

        private readonly XNativeWindow _owner;
        private readonly XTopLevelWindow _handle;
        private readonly XPixelReader _reader;
        private readonly Action<TimeSpan> _onFrame;
        private readonly List<XRect> _damage = [];
        private readonly List<int> _lockedTiles = [];
        private WriteableBitmap?[] _tiles = [];
        private ILockedFramebuffer?[] _locks = [];
        private int _columns, _rows;

        /// <summary>位图对应的缓冲尺寸(物理像素)。</summary>
        private int _width, _height;

        private bool _fullDamage = true;
        private bool _frameRequested;
        private bool _started;
        private bool _released;

        /// <summary>上一次拷贝时的形状与透明度:变了就得整窗重拷(形状以外清成透明、不透明窗口补 alpha 都烙在位图里)。</summary>
        private IReadOnlyList<XRect>? _copiedShape;
        private bool _copiedAlpha;

        /// <summary>ReadPixels 回调发现缓冲尺寸与位图不符时,记下新尺寸。</summary>
        private (int Width, int Height)? _resizeTo;

        public XSurface(XNativeWindow owner, XTopLevelWindow handle)
        {
            _owner = owner;
            _handle = handle;
            _reader = CopyDamage;
            _onFrame = _ => OnFrame();
            // 位图与窗口像素一一对应,缩放只来自 DPI:不插值,免得文字发虚。(在 Render 里设会让视觉在渲染中途失效。)
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        }

        /// <summary>服务端报来的损伤(顶层内区坐标):下一帧取这些矩形。</summary>
        public void AddDamage(IReadOnlyList<XRect> rects)
        {
            if (!_fullDamage)
            {
                _damage.AddRange(rects);
                if (_damage.Count > MaxPendingRects)
                {
                    XRect bounds = _damage[0];
                    foreach (XRect r in _damage)
                    {
                        bounds = Union(bounds, r);
                    }
                    _damage.Clear();
                    _damage.Add(bounds);
                }
            }
            RequestFrame();
        }

        /// <summary>原生窗口显示出来了:开始按帧取像素(先整窗取一次)。显示之前攒着的损伤都包含在这一次里。</summary>
        public void Start()
        {
            _started = true;
            InvalidateAll();
        }

        /// <summary>下一帧整窗重取(窗口刚显示、形状或透明度变了)。</summary>
        public void InvalidateAll()
        {
            _fullDamage = true;
            _damage.Clear();
            RequestFrame();
        }

        /// <summary>标题、形状、透明度等属性变了:形状或透明度跟上次拷贝时不同就整窗重取。</summary>
        public void PropertiesChanged()
        {
            if (_handle.Snapshot is var s && (!ReferenceEquals(s.Shape, _copiedShape) || s.HasAlpha != _copiedAlpha))
            {
                InvalidateAll();
            }
        }

        public void Release()
        {
            _released = true;
            foreach (WriteableBitmap? tile in _tiles)
            {
                tile?.Dispose();
            }
            _tiles = [];
            _locks = [];
        }

        private void RequestFrame()
        {
            if (_frameRequested || !_started || _released)
            {
                return;
            }
            _frameRequested = true;
            _owner.RequestAnimationFrame(_onFrame);
        }

        private void OnFrame()
        {
            _frameRequested = false;
            if (_released)
            {
                return;
            }
            // 一般一趟就取完;缓冲尺寸与位图不符时按新尺寸重建位图、整窗再取。尺寸一直在变(拖动缩放中)就留到下一帧。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (_fullDamage)
                {
                    _damage.Clear();
                    _damage.Add(new XRect(0, 0, _width, _height));
                }
                LockDamagedTiles();
                bool read;
                try
                {
                    read = _handle.ReadPixels(_reader);
                }
                finally
                {
                    UnlockTiles();
                }
                if (!read)
                {
                    return;   // 窗口已经没有缓冲(取消映射 / 销毁):等宿主把原生窗口收掉
                }
                if (_resizeTo is { } size)
                {
                    _resizeTo = null;
                    Resize(size.Width, size.Height);
                    _fullDamage = true;
                    continue;
                }
                _fullDamage = false;
                _damage.Clear();
                InvalidateVisual();
                return;
            }
            RequestFrame();
        }

        /// <summary>按新的缓冲尺寸重排小块(位图用到时再建)。</summary>
        private void Resize(int width, int height)
        {
            foreach (WriteableBitmap? tile in _tiles)
            {
                tile?.Dispose();
            }
            _width = width;
            _height = height;
            _columns = (width + TileSize - 1) / TileSize;
            _rows = (height + TileSize - 1) / TileSize;
            _tiles = new WriteableBitmap?[_columns * _rows];
            _locks = new ILockedFramebuffer?[_tiles.Length];
        }

        /// <summary>
        /// 进像素锁之前先把要写的小块锁好:锁位图可能要等渲染线程画完它,不能让服务端的执行线程陪着等。
        /// </summary>
        private void LockDamagedTiles()
        {
            foreach (XRect d in _damage)
            {
                XRect r = d.Intersect(new XRect(0, 0, _width, _height));
                if (r.IsEmpty)
                {
                    continue;
                }
                for (int ty = r.Y / TileSize; ty <= (r.Bottom - 1) / TileSize; ty++)
                {
                    for (int tx = r.X / TileSize; tx <= (r.Right - 1) / TileSize; tx++)
                    {
                        int index = (ty * _columns) + tx;
                        if (_locks[index] is not null)
                        {
                            continue;
                        }
                        WriteableBitmap tile = _tiles[index] ??= new WriteableBitmap(
                            new PixelSize(Math.Min(TileSize, _width - (tx * TileSize)), Math.Min(TileSize, _height - (ty * TileSize))),
                            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                        _locks[index] = tile.Lock();
                        _lockedTiles.Add(index);
                    }
                }
            }
        }

        private void UnlockTiles()
        {
            foreach (int index in _lockedTiles)
            {
                _locks[index]?.Dispose();
                _locks[index] = null;
            }
            _lockedTiles.Clear();
        }

        /// <summary>
        /// 在像素锁里(<see cref="XTopLevelWindow.ReadPixels" /> 的回调):把损伤矩形从服务端的缓冲拷进锁好的小块。
        /// 24 位视觉的 alpha 字节没有意义,补成不透明;非矩形窗口在形状之外置成全透明。
        /// </summary>
        private void CopyDamage(ReadOnlySpan<uint> pixels, int width, int height)
        {
            if (width != _width || height != _height)
            {
                _resizeTo = (width, height);
                return;
            }
            XTopLevelSnapshot s = _handle.Snapshot;   // 像素锁里:快照此刻不会换,与像素一致
            bool opaque = !s.HasAlpha;
            IReadOnlyList<XRect>? shape = s.Shape;
            _copiedAlpha = !opaque;
            _copiedShape = shape;
            foreach (XRect d in _damage)
            {
                XRect r = d.Intersect(new XRect(0, 0, width, height));
                if (r.IsEmpty)
                {
                    continue;
                }
                for (int ty = r.Y / TileSize; ty <= (r.Bottom - 1) / TileSize; ty++)
                {
                    for (int tx = r.X / TileSize; tx <= (r.Right - 1) / TileSize; tx++)
                    {
                        XRect tile = new(tx * TileSize, ty * TileSize, TileSize, TileSize);
                        if (_locks[(ty * _columns) + tx] is { } frame)
                        {
                            CopyPart(pixels, width, r.Intersect(tile), tile, frame, opaque, shape);
                        }
                    }
                }
            }
        }

        private static unsafe void CopyPart(ReadOnlySpan<uint> pixels, int width, XRect part, XRect tile, ILockedFramebuffer frame,
            bool opaque, IReadOnlyList<XRect>? shape)
        {
            int rowPixels = frame.RowBytes / 4;
            uint* origin = (uint*)frame.Address;
            for (int y = part.Y; y < part.Bottom; y++)
            {
                ReadOnlySpan<uint> from = pixels.Slice((y * width) + part.X, part.Width);
                Span<uint> to = new(origin + ((long)(y - tile.Y) * rowPixels) + (part.X - tile.X), part.Width);
                if (shape is null)
                {
                    CopyRow(from, to, opaque);
                    continue;
                }
                // 形状覆盖的段照拷,其余清成全透明。
                to.Clear();
                foreach (XRect s in shape)
                {
                    if (y < s.Y || y >= s.Y + s.Height)
                    {
                        continue;
                    }
                    int start = Math.Max(s.X, part.X), end = Math.Min(s.X + s.Width, part.X + part.Width);
                    if (end > start)
                    {
                        CopyRow(from[(start - part.X)..(end - part.X)], to[(start - part.X)..(end - part.X)], opaque);
                    }
                }
            }
        }

        /// <summary>拷一段;不透明窗口顺手把 alpha 补成 0xFF(按向量宽度一次处理多个像素)。</summary>
        private static void CopyRow(ReadOnlySpan<uint> from, Span<uint> to, bool opaque)
        {
            if (!opaque)
            {
                from.CopyTo(to);
                return;
            }
            int i = 0;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                System.Numerics.Vector<uint> alpha = new(0xFF000000);
                int step = System.Numerics.Vector<uint>.Count;
                for (; i <= from.Length - step; i += step)
                {
                    (new System.Numerics.Vector<uint>(from[i..]) | alpha).CopyTo(to[i..]);
                }
            }
            for (; i < from.Length; i++)
            {
                to[i] = from[i] | 0xFF000000;
            }
        }

        private static XRect Union(XRect a, XRect b)
        {
            int x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y);
            int x2 = Math.Max(a.X + a.Width, b.X + b.Width), y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new XRect(x1, y1, x2 - x1, y2 - y1);
        }

        public override void Render(DrawingContext context)
        {
            double scale = _owner.Scale;
            for (int index = 0; index < _tiles.Length; index++)
            {
                if (_tiles[index] is not { } tile)
                {
                    continue;
                }
                PixelSize size = tile.PixelSize;
                int x = index % _columns * TileSize, y = index / _columns * TileSize;
                context.DrawImage(tile, new Rect(0, 0, size.Width, size.Height),
                    new Rect(x / scale, y / scale, size.Width / scale, size.Height / scale));
            }
        }
    }
}
