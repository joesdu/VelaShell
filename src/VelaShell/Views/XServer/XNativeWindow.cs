using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Services.XServer;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;

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
    private (int Left, int Right, int Top, int Bottom) _frame;
    private XWindowStates _reportedStates;
    private (int X, int Y)? _placed;
    private WindowState _resizeState = WindowState.Normal;
    private long _controlLeftDownAt;

    internal XNativeWindow(AvaloniaXServerHost host, XTopLevelWindow handle)
    {
        _host = host;
        Handle = handle;
        _surface = new XSurface(handle);
        Content = _surface;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = true;
        ApplyStyle();

        PositionChanged += (_, _) => OnMovedByUser();
        Resized += OnResized;
        Activated += (_, _) => _host.OnWindowActivated(this);
        Deactivated += (_, _) => OnDeactivated();
        ScalingChanged += (_, _) => ApplyGeometry();
        Opened += (_, _) => { UpdateFrameExtents(); ApplyGeometry(); };
    }

    /// <summary>服务端那边的顶层窗口。</summary>
    public XTopLevelWindow Handle { get; }

    private X11Server? Server => _host.Server;

    private double Scale => RenderScaling > 0 ? RenderScaling : 1;

    // ================================================================== 服务端 → 窗口

    /// <summary>标题、装饰、尺寸约束、图标等属性变了(映射时也调一次)。</summary>
    public void ApplyProperties()
    {
        Title = Handle.Title.Length > 0 ? Handle.Title : Handle.ClassName;
        ApplyStyle();
        Opacity = Math.Clamp(Handle.Opacity, 0.05, 1);
        double scale = Scale;
        MinWidth = Handle.MinWidth > 0 ? Handle.MinWidth / scale : 0;
        MinHeight = Handle.MinHeight > 0 ? Handle.MinHeight / scale : 0;
        MaxWidth = Handle.MaxWidth > 0 ? Handle.MaxWidth / scale : double.PositiveInfinity;
        MaxHeight = Handle.MaxHeight > 0 ? Handle.MaxHeight / scale : double.PositiveInfinity;
        CanResize = !Handle.OverrideRedirect && (Handle.MaxWidth == 0 || Handle.MaxWidth != Handle.MinWidth
                                                 || Handle.MaxHeight != Handle.MinHeight);
        if (Handle.Icons.Count > 0 && _host.IconFor(Handle) is { } icon)
        {
            Icon = icon;
        }
        ApplyGeometry();
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
            Width = Math.Max(1, Handle.Width) / scale;
            Height = Math.Max(1, Handle.Height) / scale;
            (int ox, int oy) = _host.RootOrigin;
            (int x, int y) = (Handle.X, Handle.Y);
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

    /// <summary>有内容画进来了:下一帧重新取像素。</summary>
    public void Invalidate() => _surface.Invalidate();

    /// <summary>服务端要求的光标。</summary>
    public void ApplyCursor(int glyph) => Cursor = new Cursor(XInputMap.Cursor(glyph));

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
        Topmost = (target & XWindowStates.Above) != 0 || Handle.OverrideRedirect;
        ReportStates();
    }

    // ================================================================== 窗口 → 服务端

    private void ApplyStyle()
    {
        bool popup = Handle.OverrideRedirect;
        bool undecorated = popup || !Handle.Decorated
                           || Handle.WindowType is XWindowType.Splash or XWindowType.Tooltip or XWindowType.Notification
                               or XWindowType.Dnd or XWindowType.Dock or XWindowType.Desktop;
        WindowDecorations = undecorated ? WindowDecorations.None : WindowDecorations.Full;
        ShowInTaskbar = !popup && Handle.TransientFor == 0 && (Handle.States & XWindowStates.SkipTaskbar) == 0
                        && Handle.WindowType is XWindowType.Normal or XWindowType.Dialog;
        ShowActivated = !popup && Handle.AcceptsFocus;
        Topmost = popup || (Handle.States & XWindowStates.Above) != 0;
        CanMinimize = !popup;
        CanMaximize = !popup;
        // 有 alpha 的视觉(GTK 的客户端阴影、圆角)与非矩形窗口要透明底;其余不透明,省掉系统合成的开销。
        TransparencyLevelHint = Handle.HasAlpha || Handle.Shape is not null
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
        if (x != Handle.X || y != Handle.Y)
        {
            server.MoveTopLevel(Handle.Id, x, y);
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
        if (width > 0 && height > 0 && (width != Handle.Width || height != Handle.Height))
        {
            server.ResizeTopLevel(Handle.Id, width, height);
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
                server.Key(key, pressed: false);
            }
        }
        _heldKeys.Clear();
        _host.OnWindowDeactivated(this);
    }

    /// <summary>系统边框的尺寸(物理像素)告诉服务端(<c>_NET_FRAME_EXTENTS</c>),摆放时也用它把内容区对准 X 的坐标。</summary>
    private void UpdateFrameExtents()
    {
        (int, int, int, int) frame = (0, 0, 0, 0);
        if (WindowDecorations != WindowDecorations.None && FrameSize is { } outer)
        {
            double scale = Scale;
            int side = Math.Max(0, (int)Math.Round((outer.Width - ClientSize.Width) * scale / 2));
            int top = Math.Max(0, (int)Math.Round((outer.Height - ClientSize.Height) * scale) - side);
            frame = (side, side, top, side);
        }
        if (frame != _frame)
        {
            _frame = frame;
            Server?.SetFrameExtents(Handle.Id, _frame.Left, _frame.Right, _frame.Top, _frame.Bottom);
        }
    }

    private XWindowStates StatesFromWindow() => WindowState switch
    {
        WindowState.Maximized => XWindowStates.Maximized,
        WindowState.FullScreen => XWindowStates.Fullscreen,
        WindowState.Minimized => XWindowStates.Hidden,
        _ => XWindowStates.None,
    } | (Topmost && !Handle.OverrideRedirect ? XWindowStates.Above : XWindowStates.None);

    /// <summary>窗口状态(用户点了最大化、系统最小化……)写回 <c>_NET_WM_STATE</c>。</summary>
    private void ReportStates()
    {
        XWindowStates states = StatesFromWindow();
        if (states != _reportedStates)
        {
            _reportedStates = states;
            Server?.SetTopLevelStates(Handle.Id, states);
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
        Server?.CloseTopLevel(Handle.Id);
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
        Server?.PointerMotion(Handle.Id, x, y);
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
        Server?.PointerButton(Handle.Id, x, y, button, pressed: true);
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
        Server?.PointerButton(Handle.Id, x, y, button, pressed: false);
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
            server.PointerButton(Handle.Id, x, y, button, pressed: true);
            server.PointerButton(Handle.Id, x, y, button, pressed: false);
            _wheelRemainder = _wheelRemainder.WithY(_wheelRemainder.Y - Math.Sign(_wheelRemainder.Y));
        }
        while (Math.Abs(_wheelRemainder.X) >= 1)
        {
            int button = _wheelRemainder.X > 0 ? 6 : 7;
            server.PointerButton(Handle.Id, x, y, button, pressed: true);
            server.PointerButton(Handle.Id, x, y, button, pressed: false);
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
            Server?.PointerLeft();
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
        Server?.Key(keycode, pressed: true);
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
        if (!_host.HasAltGr)
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
            Server?.Key(XKeycodes.ControlLeft, pressed: false);
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
        Server?.Key(keycode, pressed: false);
        e.Handled = true;
    }

    private (int X, int Y) ToPixels(Point point) =>
        ((int)Math.Floor(point.X * Scale), (int)Math.Floor(point.Y * Scale));

    /// <summary>画顶层像素的控件:一张与窗口像素一一对应的位图,按 1/缩放 的 DIP 尺寸画,不插值。</summary>
    private sealed class XSurface : Control
    {
        private readonly XTopLevelWindow _handle;
        private WriteableBitmap? _bitmap;
        private bool _pending;

        public XSurface(XTopLevelWindow handle)
        {
            _handle = handle;
            // 位图与窗口像素一一对应,缩放只来自 DPI:不插值,免得文字发虚。(在 Render 里设会让视觉在渲染中途失效。)
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        }

        public void Invalidate()
        {
            if (_pending)
            {
                return;
            }
            _pending = true;
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Render);
        }

        public void Release()
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }

        private void Refresh()
        {
            _pending = false;
            int width = _handle.Width, height = _handle.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }
            uint[] pixels = ArrayPool<uint>.Shared.Rent(width * height);
            try
            {
                (int w, int h) = _handle.CopyPixels(pixels);
                if (w <= 0 || h <= 0)
                {
                    return;
                }
                if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                }
                Blit(pixels, w, h);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(pixels);
            }
            InvalidateVisual();
        }

        /// <summary>
        /// 像素是 0xAARRGGBB(小端内存里恰好是 BGRA)。24 位视觉的 alpha 字节没有意义,补成不透明;
        /// 非矩形窗口在形状之外置成全透明。
        /// </summary>
        private unsafe void Blit(uint[] pixels, int width, int height)
        {
            using ILockedFramebuffer frame = _bitmap!.Lock();
            bool opaque = !_handle.HasAlpha;
            IReadOnlyList<XRect>? shape = _handle.Shape;
            for (int y = 0; y < height; y++)
            {
                Span<uint> row = new((byte*)frame.Address + ((long)y * frame.RowBytes), width);
                ReadOnlySpan<uint> source = pixels.AsSpan(y * width, width);
                if (opaque)
                {
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = source[x] | 0xFF000000;
                    }
                }
                else
                {
                    source.CopyTo(row);
                }
                if (shape is not null)
                {
                    ApplyShape(row, y, shape);
                }
            }
        }

        private static void ApplyShape(Span<uint> row, int y, IReadOnlyList<XRect> shape)
        {
            // 形状覆盖的段原样留下,其余清成全透明:先把覆盖段拷进一行清零的暂存,再整行拷回。
            uint[] kept = ArrayPool<uint>.Shared.Rent(row.Length);
            try
            {
                Span<uint> scratch = kept.AsSpan(0, row.Length);
                scratch.Clear();
                foreach (XRect r in shape)
                {
                    if (y < r.Y || y >= r.Y + r.Height)
                    {
                        continue;
                    }
                    int start = Math.Max(0, r.X), end = Math.Min(row.Length, r.X + r.Width);
                    if (end > start)
                    {
                        row[start..end].CopyTo(scratch[start..end]);
                    }
                }
                scratch.CopyTo(row);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(kept);
            }
        }
        public override void Render(DrawingContext context)
        {
            if (_bitmap is { } bitmap)
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(Bounds.Size));
            }
        }
    }
}
