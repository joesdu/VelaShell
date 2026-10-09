using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VelaShell.Services.XServer;
using VelaShell.XServer;

namespace VelaShell.Views.XServer;

/// <summary>
/// 一个 X 顶层窗口对应的原生窗口:画它的像素,把指针、键盘、移动、缩放、关闭交回服务端。
/// </summary>
/// <remarks>
/// <para>
/// <b>坐标一律按物理像素。</b>X 客户端看到的根窗口是整个虚拟桌面(所有显示器的外接矩形,原点平移到 0,0),
/// 顶层的 X / Y 是它边框外沿在根窗口里的位置,<b>内容区</b>在 (X + 边框宽, Y + 边框宽);X 的边框不画,原生窗口的系统标题栏与边框
/// 在内容区之外,尺寸经 <c>_NET_FRAME_EXTENTS</c> 告诉客户端。客户端自己给的位置像窗口管理器那样按重力摆外框(ICCCM §4.1.2.3),
/// 摆好之后把 X 窗口的实际位置报回服务端。DIP 只在给 Avalonia 设尺寸时换算一次。
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

    /// <summary>按着 Command 时按下的键(见 <see cref="CommandKeyUpMayBeLost" />)。</summary>
    private readonly HashSet<byte> _pressedWithCommand = [];
    private PointerPressedEventArgs? _lastPress;

    /// <summary>
    /// 在 X 那边按着的按钮(X 的按钮号)与最后一次指针位置(内区物理像素)。只记一个 bool 的话,两个按钮同时按着、松开一个
    /// 就当全松开了;失去捕获(系统拖动循环、别的窗口抢走)或窗口失活之后等不到的那些松开,由 <see cref="ReleaseHeldButtons" /> 补上。
    /// </summary>
    private readonly HashSet<int> _heldButtons = [];
    private (int X, int Y) _lastPointer;
    private bool _applying;

    /// <summary>最后一次按服务端的几何设的内容区尺寸(物理像素):迟到的 Resized 与它相同就是我们自己设的(见 OnResized)。</summary>
    private (int Width, int Height) _appliedSize;
    private XWindowStates _reportedStates;
    private WindowState _resizeState = WindowState.Normal;

    /// <summary>原生窗口已经显示出来(<c>Opened</c>):外框尺寸量得到了,摆好的位置才报回服务端。</summary>
    private bool _opened;

    /// <summary>宿主替没给位置的窗口选的外框左上角(根窗口坐标);服务端那边摆好之后清掉。</summary>
    private (int X, int Y)? _frameAt;

    /// <summary>已经按哪一份快照把摆好的位置报回服务端了(同一份快照不重复报)。</summary>
    private XTopLevelSnapshot? _placedFor;

    /// <summary>最近一次摆的:客户端请求的位置,与摆好之后 X 窗口的位置。</summary>
    private ((int X, int Y) Request, (int X, int Y) Placed)? _placement;
    private long _controlLeftDownAt;

    /// <summary>上一份快照要求引起注意(只在变成要求时闪一次任务栏)。</summary>
    private bool _urgent;

    /// <summary>窗口区域裁成了哪个形状(null = 整个矩形)。</summary>
    private IReadOnlyList<XRect>? _regionShape;

    /// <summary>系统边框的尺寸已经报给服务端了(显示之前用的是宿主预估的,不算)。</summary>
    private bool _frameReported;

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
        if (IsScreen)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;   // 屏幕窗口由用户摆:起步居中,之后不跟服务端的几何
        }
        ApplyStyle(handle.Snapshot);
        // 系统边框的尺寸要等显示出来才量得到:先按宿主上一个有边框的窗口量到的预估,第一帧就摆在对的地方,
        // 不必等 Opened 之后再挪(原先按 0 摆,显示出来跳一下)。
        FrameExtents = WindowDecorations == WindowDecorations.None ? default : host.LastDecoratedFrame;

        PositionChanged += (_, _) => OnMovedByUser();
        Resized += OnResized;
        AddHandler(TextInputMethodClientRequestedEvent, OnTextInputMethodClientRequested);
        Activated += (_, _) => _host.OnWindowActivated(this);
        Deactivated += (_, _) => OnDeactivated();
        ScalingChanged += (_, _) => ApplyGeometry();
        Opened += (_, _) => { _opened = true; UpdateFrameExtents(); ApplyGeometry(); UpdateRegion(Handle.Snapshot); _surface.Start(); };
        // 本机的文本、文件拖进 X 程序(F16):服务端替宿主扮演 XDND 的源,见 XDropTarget。
        XDropTarget.Attach(this, _surface, handle, () => Server, ToPixels, host.DropUploader, AvaloniaXServerHost.NotifyUser);
    }

    /// <summary>服务端那边的顶层窗口。</summary>
    public XTopLevelWindow Handle { get; }

    /// <summary>
    /// 单窗口模式的屏幕窗口(F13,<see cref="X11Server.Screen" />):里面是整个 X 屏幕,窗口由远端的窗口管理器管。原生窗口自己的位置、
    /// 边框与状态归用户 —— 不跟服务端的几何摆、不报位置;拖大拖小就是改屏幕尺寸;关掉就是停 X Server。
    /// </summary>
    internal bool IsScreen => ReferenceEquals(Handle, Handle.Server.Screen);

    /// <summary>这个窗口的服务端,只在它就是宿主此刻附着的那个时给出:停服后马上重启,旧窗口的事件不会把旧句柄交给新服务端。</summary>
    private X11Server? Server => _host.CurrentServer(Handle);

    private double Scale => RenderScaling > 0 ? RenderScaling : 1;

    // ================================================================== 服务端 → 窗口

    /// <summary>
    /// 原生窗口(与任务栏)上的标题。转发来的窗口(连接有标签,如 <c>user@host:22</c>)按设置在前面标出来源(xs_plan F18):
    /// 同时转发几台主机时分得清;远端程序把标题设成「Windows 安全中心」也盖不住前面的来源 —— 标在前面,任务栏截断长标题时也还看得到。
    /// </summary>
    internal static string TitleOf(XTopLevelSnapshot s, bool showSource)
    {
        string title = s.Title.Length > 0 ? s.Title : s.ClassName;
        return showSource && !string.IsNullOrEmpty(s.ClientLabel)
            ? VelaShell.Core.Resources.Strings.Format("XServer_WindowTitleWithSource", s.ClientLabel, title)
            : title;
    }

    /// <summary>快照里的属性变了(映射时按 <see cref="XTopLevelChanges.All" /> 调一次):只重新应用变了的那几组。</summary>
    public void ApplyProperties(XTopLevelChanges changes)
    {
        XTopLevelSnapshot s = Handle.Snapshot;
        if ((changes & XTopLevelChanges.Title) != 0)
        {
            Title = IsScreen ? _host.ScreenTitle(Handle.Server) : TitleOf(s, _host.ShowsWindowSource);
        }
        if ((changes & (XTopLevelChanges.Hints | XTopLevelChanges.States | XTopLevelChanges.Shape)) != 0)
        {
            ApplyStyle(s);
        }
        if ((changes & (XTopLevelChanges.Hints | XTopLevelChanges.Shape)) != 0)
        {
            _surface.PropertiesChanged();
            UpdateRegion(s);
        }
        if ((changes & XTopLevelChanges.Hints) != 0)
        {
            if (s.Urgent && !_urgent && !IsActive)
            {
                WindowAttention.Request(this);   // WM_HINTS 的 urgency / DEMANDS_ATTENTION:闪任务栏(原先什么也不做)
            }
            _urgent = s.Urgent;
            Opacity = Math.Clamp(s.Opacity, 0.05, 1);
            double scale = Scale;
            MinWidth = s.MinWidth > 0 ? s.MinWidth / scale : 0;
            MinHeight = s.MinHeight > 0 ? s.MinHeight / scale : 0;
            MaxWidth = s.MaxWidth > 0 ? s.MaxWidth / scale : double.PositiveInfinity;
            MaxHeight = s.MaxHeight > 0 ? s.MaxHeight / scale : double.PositiveInfinity;
            CanResize = !s.OverrideRedirect && (s.Functions & XWindowFunctions.Resize) != 0
                        && (s.MaxWidth == 0 || s.MaxWidth != s.MinWidth || s.MaxHeight != s.MinHeight);
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
    /// 宿主替没给位置的窗口选了外框的位置(根窗口坐标,外框左上角):代替按重力摆,摆好之后照常报回服务端。
    /// </summary>
    public void PlaceFrameAt(int x, int y) => _frameAt = (x, y);

    /// <summary>系统边框的四边宽(物理像素;显示出来之前是 0)。</summary>
    public XFrameExtents FrameExtents { get; private set; }

    /// <summary>
    /// 按服务端的几何摆放原生窗口:内容区对准 X 窗口的内区(边框外沿 + 边框宽)。快照说位置是客户端请求的
    /// (<see cref="XTopLevelSnapshot.NeedsPlacement" />)时像窗口管理器那样摆:外框按重力对准它(ICCCM §4.1.2.3)——
    /// 原先一律让内容区对准请求的坐标,请求 y = 0 的窗口标题栏落在屏幕外 —— 显示出来、外框尺寸量到之后,把 X 窗口摆好的位置报回服务端。
    /// </summary>
    public void ApplyGeometry()
    {
        if (WindowState is WindowState.Maximized or WindowState.FullScreen)
        {
            // 最大化 / 全屏时尺寸与位置由系统定,再按旧几何摆会把它拉回去。客户端自己改了尺寸(XResizeWindow、gtk_window_resize)
            // 就把原生窗口此刻的尺寸推回给它 —— 原先直接返回,X 缓冲从此与原生窗口对不上(多出来的地方是空的,或者内容被裁)。
            PushNativeGeometry();
            return;
        }
        double scale = Scale;
        _applying = true;
        try
        {
            // 设 Width / Height(内容区的 DIP 尺寸)才会真的改原生窗口;显示之后再设 ClientSize 只改了属性值。
            XTopLevelSnapshot s = Handle.Snapshot;
            _appliedSize = (Math.Max(1, s.Width), Math.Max(1, s.Height));
            Width = _appliedSize.Width / scale;
            Height = _appliedSize.Height / scale;
            if (IsScreen)
            {
                return;   // 屏幕窗口只跟尺寸,位置归用户
            }
            (int ox, int oy) = _host.RootOrigin;
            (int x, int y) = (s.X, s.Y);
            if (s.NeedsPlacement)
            {
                if (_placement is { } done && done.Request == (s.X, s.Y))
                {
                    (x, y) = done.Placed;   // 这个请求已经摆过(服务端还没跟上,或者客户端又请求了同一个位置)
                }
                else
                {
                    (x, y) = _frameAt is { } at
                        ? (at.X + FrameExtents.Left - s.BorderWidth, at.Y + FrameExtents.Top - s.BorderWidth)
                        : s.PlaceInFrame(FrameExtents);
                    (x, y) = (Math.Clamp(x, short.MinValue, short.MaxValue), Math.Clamp(y, short.MinValue, short.MaxValue));
                }
                if (_opened && !ReferenceEquals(_placedFor, s) && Server is { } server)
                {
                    _placedFor = s;
                    _placement = ((s.X, s.Y), (x, y));
                    _frameAt = null;   // 选的位置用过了:之后客户端再自己挪,就按重力摆
                    server.MoveTopLevel(Handle, x, y);
                }
            }
            Position = new PixelPoint(x + s.BorderWidth + ox - FrameExtents.Left, y + s.BorderWidth + oy - FrameExtents.Top);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>原生窗口此刻的内容区尺寸与位置报给服务端(与快照不同时):系统定几何(最大化 / 全屏)时客户端改不动它。</summary>
    private void PushNativeGeometry()
    {
        if (!_opened || Server is not { } server)
        {
            return;
        }
        XTopLevelSnapshot s = Handle.Snapshot;
        int width = Math.Clamp((int)Math.Round(ClientSize.Width * Scale), 1, X11ServerOptions.MaxScreenSize);
        int height = Math.Clamp((int)Math.Round(ClientSize.Height * Scale), 1, X11ServerOptions.MaxScreenSize);
        if (width != s.Width || height != s.Height)
        {
            server.ResizeTopLevel(Handle, width, height);
        }
        OnMovedByUser();
    }

    /// <summary>有内容画进来了(顶层内区坐标的矩形):下一帧取这几块像素。</summary>
    public void AddDamage(IReadOnlyList<XRect> damage) => _surface.AddDamage(damage);

    /// <summary>服务端要求的光标:有图像(位图 / ARGB 光标)就显示图像,否则按形状选系统光标。</summary>
    public void ApplyCursor(XCursor cursor)
    {
        if (cursor is { Image: { } image, Shape: not XCursorShape.Hidden })
        {
            Cursor = ImageCursor(image);
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

    /// <summary>这个窗口按图像建过的光标(服务端对同一个光标总给同一份图像),最近用的排在后面。</summary>
    private readonly List<(XCursorImage Image, Cursor Cursor)> _imageCursors = [];

    /// <summary>每个窗口最多留这么多个图像光标;再多就释放最久没用的。</summary>
    private const int MaxImageCursors = 16;

    /// <summary>
    /// 图像光标:建过的直接用,否则建一个。每个光标背后是一个系统光标句柄(Windows 上是 GDI 对象),要显式释放 —— 原先放在
    /// ConditionalWeakTable 里等图像被回收,光标对象没人释放,句柄一直漏到进程退出;不停换光标的程序能把 GDI 对象耗尽。
    /// 现在每个窗口留最近用的几个,多了释放最久没用的(此刻正显示的不动),窗口关闭时全部释放。
    /// </summary>
    private Cursor ImageCursor(XCursorImage image)
    {
        int index = _imageCursors.FindIndex(c => ReferenceEquals(c.Image, image));
        (XCursorImage, Cursor) entry = index >= 0 ? _imageCursors[index] : (image, CreateImageCursor(image));
        if (index >= 0)
        {
            _imageCursors.RemoveAt(index);
        }
        _imageCursors.Add(entry);
        for (int i = 0; _imageCursors.Count > MaxImageCursors && i < _imageCursors.Count - 1;)
        {
            if (ReferenceEquals(_imageCursors[i].Cursor, Cursor))
            {
                i++;   // 正显示着,不释放
                continue;
            }
            _imageCursors[i].Cursor.Dispose();
            _imageCursors.RemoveAt(i);
        }
        return entry.Item2;
    }

    /// <summary>释放这个窗口建过的图像光标(窗口关了)。</summary>
    private void ReleaseImageCursors()
    {
        Cursor = null;
        foreach ((_, Cursor cursor) in _imageCursors)
        {
            cursor.Dispose();
        }
        _imageCursors.Clear();
    }

    /// <summary>这个窗口此刻留着的图像光标数(测试用)。</summary>
    internal int ImageCursorCount => _imageCursors.Count;

    private static unsafe Cursor CreateImageCursor(XCursorImage image)
    {
        // 预乘的 0xAARRGGBB 按小端字节序就是预乘的 BGRA:逐行拷进位图。
        WriteableBitmap bitmap = new(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer frame = bitmap.Lock())
        {
            for (int y = 0; y < image.Height; y++)
            {
                MemoryMarshal.AsBytes(image.Pixels.Span.Slice(y * image.Width, image.Width))
                    .CopyTo(new Span<byte>((void*)(frame.Address + (y * frame.RowBytes)), image.Width * 4));
            }
        }
        return new Cursor(bitmap, new PixelPoint(image.HotspotX, image.HotspotY));
    }

    /// <summary>宿主要关它(客户端取消映射 / 销毁、服务端停下)。</summary>
    public void CloseByHost()
    {
        ClosingByHost = true;
        if (!_closed)
        {
            Close();
        }
    }

    /// <summary>已经关了(随 owner 一起关、或宿主关过一次)。</summary>
    private bool _closed;

    /// <summary>只做标记、先不关(宿主一次收掉一批窗口时,先给全部打上标记,owner 级联关子窗口时子窗口才不会拦)。</summary>
    public void MarkClosingByHost() => ClosingByHost = true;

    /// <summary>客户端经 <c>_NET_WM_MOVERESIZE</c> 要求拖动 / 缩放:用系统的拖动循环(要一次还按着的按下事件)。</summary>
    public void BeginInteractive(XMoveResizeDirection direction)
    {
        if (_lastPress is not { } press || _heldButtons.Count == 0)
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

    /// <summary>
    /// 显示之前按快照里的状态摆:客户端映射前设好的 <c>_NET_WM_STATE</c>(一开始就最大化、全屏),或 <c>WM_HINTS</c> 的
    /// initial_state 为 IconicState(服务端把它写成 Hidden)。原先这些只在收到 ClientMessage 时才生效,映射时一律按普通窗口显示。
    /// 状态本来就是客户端自己写的,不再回报给服务端。
    /// </summary>
    public void ApplyInitialStates()
    {
        XTopLevelSnapshot s = Handle.Snapshot;
        if (IsScreen)
        {
            WindowState = _host.ScreenFullscreen ? WindowState.FullScreen : WindowState.Normal;
            _resizeState = WindowState;
            _reportedStates = StatesFromWindow();
            return;
        }
        if (s.OverrideRedirect)
        {
            return;
        }
        WindowState = (s.States & XWindowStates.Fullscreen) != 0 ? WindowState.FullScreen
            : (s.States & XWindowStates.Hidden) != 0 ? WindowState.Minimized
            : (s.States & XWindowStates.Maximized) == XWindowStates.Maximized ? WindowState.Maximized
            : WindowState.Normal;
        _resizeState = WindowState;
        _reportedStates = StatesFromWindow();
    }

    /// <summary>客户端要求改状态(最大化、全屏、最小化……)。</summary>
    public void ApplyStateRequest(XWindowStates add, XWindowStates remove)
    {
        XWindowStates target = (StatesFromWindow() | add) & ~remove;
        WindowState = (target & XWindowStates.Fullscreen) != 0 ? WindowState.FullScreen
            : (target & XWindowStates.Hidden) != 0 ? WindowState.Minimized
            : (target & XWindowStates.Maximized) == XWindowStates.Maximized ? WindowState.Maximized
            : WindowState.Normal;
        _above = (target & XWindowStates.Above) != 0;
        UpdateTopmost(_host.XActive);
        ReportStates();
        // 原生窗口不管的那几个(SkipTaskbar、Sticky、Below、DemandsAttention……)照客户端要的记进服务端:快照随之变,
        // 是否进任务栏之类按它重新应用。原先只回写原生窗口管的,客户端映射之后再请求的这几个状态都被丢掉。
        XWindowStates extraAdd = add & ~WindowManagedStates & ~XWindowStates.Focused;
        XWindowStates extraRemove = remove & ~WindowManagedStates & ~XWindowStates.Focused & ~extraAdd;
        if (extraAdd != XWindowStates.None || extraRemove != XWindowStates.None)
        {
            Server?.ChangeTopLevelStates(Handle, extraAdd, extraRemove);
        }
    }

    // ================================================================== 窗口 → 服务端

    private void ApplyStyle(XTopLevelSnapshot s)
    {
        if (IsScreen)
        {
            WindowDecorations = _host.ScreenUndecorated ? WindowDecorations.None : WindowDecorations.Full;
            ShowInTaskbar = true;
            ShowActivated = true;
            CanMinimize = true;
            CanMaximize = true;
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            return;
        }
        bool popup = s.OverrideRedirect;
        bool undecorated = popup || !s.Decorated
                           || s.WindowType is XWindowType.Splash or XWindowType.Tooltip or XWindowType.Notification
                               or XWindowType.Dnd or XWindowType.Dock or XWindowType.Desktop;
        WindowDecorations = undecorated ? WindowDecorations.None : WindowDecorations.Full;
        ShowInTaskbar = !popup && s.TransientFor is null && (s.States & XWindowStates.SkipTaskbar) == 0
                        && s.WindowType is XWindowType.Normal or XWindowType.Dialog;
        ShowActivated = !popup && s.AcceptsFocus && (s.States & XWindowStates.Hidden) == 0;   // 一映射就最小化的窗口不抢前台
        _above = (s.States & XWindowStates.Above) != 0;
        UpdateTopmost(_host.XActive);
        CanMinimize = !popup && (s.Functions & XWindowFunctions.Minimize) != 0;
        CanMaximize = !popup && (s.Functions & XWindowFunctions.Maximize) != 0;
        // 有 alpha 的视觉(GTK 的客户端阴影、圆角)、非矩形窗口与半透明的窗口(_NET_WM_WINDOW_OPACITY)要透明底;其余不透明,
        // 省掉系统合成的开销。不透明的底上设 Opacity 只是和窗口自己的底色混,看不到后面的窗口。
        TransparencyLevelHint = s.HasAlpha || s.Shape is not null || s.Opacity < 1
            ? [WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.None];
    }

    /// <summary>按原生窗口此刻的位置把 X 坐标报给服务端(显示器布局变了、根原点挪了之后由宿主调)。</summary>
    public void ReportPosition() => OnMovedByUser();

    private void OnMovedByUser()
    {
        // 显示出来之前外框尺寸还不知道,算出来的位置不对(位置等 Opened 之后由 ApplyGeometry 摆好再报)。屏幕窗口的位置与 X 无关。
        if (_applying || !_opened || IsScreen || Server is not { } server || WindowState is WindowState.Minimized)
        {
            return;
        }
        (int ox, int oy) = _host.RootOrigin;
        XTopLevelSnapshot s = Handle.Snapshot;
        // 内容区对准 X 窗口的内区:X 窗口的位置(边框外沿)再往左上退一个边框宽。
        int x = Position.X + FrameExtents.Left - ox - s.BorderWidth, y = Position.Y + FrameExtents.Top - oy - s.BorderWidth;
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue)
        {
            return;   // X 的坐标是 16 位:离谱的位置不报(服务端会当场拒绝)
        }
        if (x != s.X || y != s.Y)
        {
            server.MoveTopLevel(Handle, x, y);
        }
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        UpdateFrameExtents();
        ReportStates();
        // 只把「不是我们自己按服务端的几何设出来的」尺寸回报给服务端:用户拖边框、窗口状态变了(最大化 / 全屏 / 还原)。
        // 不靠 Reason 判断 —— Avalonia 的 X11 后端在 ConfigureNotify 里一律给 Unspecified(只有 XEmbed 给 User),原先 Linux 上
        // 用户拖大窗口,X 缓冲还是原尺寸。我们自己设的尺寸引起的 Resized 可能晚一拍才到(那时 _applying 早已复位),
        // 按「与最后一次按服务端几何设的尺寸相同(差一个像素以内,分数缩放的取整)」认出来:再回报就会拿旧尺寸把客户端刚设的新尺寸改回去。
        bool stateChanged = WindowState != _resizeState;
        _resizeState = WindowState;
        if (_applying || Server is not { } server || WindowState is WindowState.Minimized)
        {
            return;
        }
        int width = (int)Math.Round(e.ClientSize.Width * Scale), height = (int)Math.Round(e.ClientSize.Height * Scale);
        bool ours = Math.Abs(width - _appliedSize.Width) <= 1 && Math.Abs(height - _appliedSize.Height) <= 1;
        if (ours && e.Reason != WindowResizeReason.User && !stateChanged)
        {
            return;
        }
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
        _pressedWithCommand.Clear();
        ReleaseHeldButtons();
        _host.OnWindowDeactivated(this);
    }

    /// <summary>X 那边还按着的按钮一律松开(在最后一次指针位置)。失去捕获、窗口失活时调:之后的松开不会再送到这个窗口。</summary>
    internal void ReleaseHeldButtons()
    {
        if (_heldButtons.Count == 0)
        {
            return;
        }
        int[] held = [.. _heldButtons];
        _heldButtons.Clear();
        foreach (int button in held)
        {
            Server?.InjectPointerButton(Handle, _lastPointer.X, _lastPointer.Y, button, pressed: false);
        }
    }

    /// <inheritdoc />
    /// <remarks>窗口状态一变就写回服务端:有的平台最小化 / 最大化时尺寸没变,不来 Resized。</remarks>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && _opened)
        {
            ReportStates();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ReleaseHeldButtons();
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
            if (frame != default)
            {
                _host.LastDecoratedFrame = frame;   // 下一个有边框的窗口显示之前就按它摆
            }
        }
        if (frame != FrameExtents || !_frameReported)
        {
            FrameExtents = frame;
            _frameReported = true;
            Server?.SetTopLevelFrameExtents(Handle, FrameExtents);
        }
    }

    /// <summary>
    /// 无装饰的非矩形窗口把命中范围裁成形状(Windows 的窗口区域,见 <see cref="WindowRegion" />):形状以外画成全透明,
    /// 原先却照样接住鼠标,用户点不到下面的窗口。只裁边界形状 —— 窗口区域连绘制一起裁,按更小的输入形状裁会把看得见的部分裁掉。
    /// </summary>
    private void UpdateRegion(XTopLevelSnapshot s)
    {
        IReadOnlyList<XRect>? shape = WindowDecorations == WindowDecorations.None ? s.Shape : null;
        if (_opened && !ReferenceEquals(shape, _regionShape))
        {
            _regionShape = shape;
            WindowRegion.Apply(this, shape);
        }
    }

    /// <summary>客户端要求的「总在最前」(<c>_NET_WM_STATE_ABOVE</c>);实际的 <c>Topmost</c> 还看用户在不在用 X 窗口。</summary>
    private bool _above;

    /// <summary>
    /// 弹出层(override-redirect)与要求「总在最前」的窗口只在用户正在用 X 窗口时才是系统级置顶。原先一直置顶:远端程序映射一个
    /// 全屏的 override-redirect 窗口就能盖住所有本机程序,画一个像系统凭据框的界面;用户回到本机窗口时它们照常退到后面。
    /// </summary>
    public void UpdateTopmost(bool xActive) => Topmost = (_above || Handle.Snapshot.OverrideRedirect) && xActive;

    private XWindowStates StatesFromWindow() => WindowState switch
    {
        WindowState.Maximized => XWindowStates.Maximized,
        WindowState.FullScreen => XWindowStates.Fullscreen,
        WindowState.Minimized => XWindowStates.Hidden,
        _ => XWindowStates.None,
    } | (_above && !Handle.Snapshot.OverrideRedirect ? XWindowStates.Above : XWindowStates.None);

    /// <summary>原生窗口自己管的那几个状态(<see cref="StatesFromWindow" /> 给得出的);其余的(SkipTaskbar、Modal、Sticky……)记在服务端。</summary>
    private const XWindowStates WindowManagedStates =
        XWindowStates.Maximized | XWindowStates.Fullscreen | XWindowStates.Hidden | XWindowStates.Above;

    /// <summary>
    /// 窗口状态(用户点了最大化、系统最小化……)写回 <c>_NET_WM_STATE</c>:只改原生窗口管的那几个。原先整组覆盖,
    /// 第一次最大化 / 最小化就把客户端映射前设的 SkipTaskbar、Modal、Sticky、Below、DemandsAttention 清掉了 ——
    /// 本不进任务栏的窗口出现在任务栏里。
    /// </summary>
    private void ReportStates()
    {
        XWindowStates states = StatesFromWindow();
        if (states != _reportedStates)
        {
            _reportedStates = states;
            Server?.ChangeTopLevelStates(Handle, states, WindowManagedStates & ~states);
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (ClosingByHost)
        {
            return;
        }
        switch (e.CloseReason)
        {
            case WindowCloseReason.WindowClosing:
                // 关闭按钮 / Alt+F4:请客户端自己关(有 WM_DELETE_WINDOW 时),窗口等它取消映射再收。
                // 弹层(override-redirect:菜单、提示框)不归窗口管理器管,它的关闭不转给客户端 —— 原先没有 WM_DELETE_WINDOW
                // 的弹层一关就断开了整个 X 程序。
                e.Cancel = true;
                if (IsScreen)
                {
                    AvaloniaXServerHost.RequestStop();   // 关掉整个 X 桌面 = 停 X Server(有程序连着时先确认)
                }
                else if (!Handle.Snapshot.OverrideRedirect)
                {
                    Server?.CloseTopLevel(Handle);
                }
                break;
            case WindowCloseReason.OwnerWindowClosing when Owner is XNativeWindow { ClosingByHost: true }:
                // owner 被宿主收掉(停服、它在 X 里取消映射了):跟着关。还映射着的,宿主随后会不带 owner 重新显示。
                ClosingByHost = true;
                break;
            case WindowCloseReason.OwnerWindowClosing:
                // 用户点了 owner 的关闭键:Avalonia 先问各个子窗口,有一个不肯 owner 就关不了、它自己的 Closing 也不会来。
                // 子窗口不能就这么跟着关(X 里它还映射着,就成了看不见的幽灵);改为替用户请 owner 的客户端自己关。
                e.Cancel = true;
                if (Owner is XNativeWindow owner && !owner.Handle.Snapshot.OverrideRedirect)
                {
                    Server?.CloseTopLevel(owner.Handle);
                }
                break;
            default:
                // 应用退出、系统注销 / 关机:不拦(原先一律取消,表现为「VelaShell 阻止关机」)。
                ClosingByHost = true;
                break;
        }
    }

    /// <summary>宿主正在收掉这个窗口(<see cref="CloseByHost" />)。</summary>
    public bool ClosingByHost { get; private set; }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
        _surface.Release();
        ReleaseImageCursors();
        _host.OnWindowClosed(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        _lastPointer = (x, y);
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
        _heldButtons.Add(button);
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        _lastPointer = (x, y);
        _lastClick = e.GetPosition(_surface);
        _imeClient?.NotifyCursorMoved();
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
        e.Handled = true;
        if (!_heldButtons.Remove(button))
        {
            return;   // 按下没送到 X(或者失去捕获时已经替它松开了):不补一个没有按下的松开
        }
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        _lastPointer = (x, y);
        Server?.InjectPointerButton(Handle, x, y, button, pressed: false);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Server is not { } server)
        {
            return;
        }
        // 平滑滚动:原样交给服务端(Avalonia 的向上 / 向左为正,X 的滚动轴向下 / 向右为正)。用滚动轴的 XI2 客户端(GTK3/4、Qt、
        // 浏览器)拿到触控板的小数增量;只认滚轮按钮的由服务端攒够一格模拟成按钮 4–7。原先宿主自己攒格子,触控板一格一跳。
        (int x, int y) = ToPixels(e.GetPosition(_surface));
        double dx = Math.Clamp(-e.Delta.X, -10000, 10000), dy = Math.Clamp(-e.Delta.Y, -10000, 10000);
        if (double.IsFinite(dx) && double.IsFinite(dy) && (dx != 0 || dy != 0))
        {
            server.InjectScroll(Handle, x, y, dx, dy);
        }
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_heldButtons.Count == 0)
        {
            Server?.InjectPointerLeave();
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _keyTextPending = false;
        if (e.Key == Key.ImeProcessed)
        {
            return;   // 输入法在组字:这个键归它,组好的字经 OnTextInput 来
        }
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
        // 已经按着又来一次按下:系统的自动重复(Avalonia 的 X11 后端开了 XKB 的 detectable autorepeat,中间没有 KeyUp)。
        // 告诉服务端这是重复,由它按 X 的语义决定发不发、怎么发(xset r off、修饰键不重复、DetectableAutoRepeat)。
        bool repeat = !_heldKeys.Add(keycode);
        if (!repeat)
        {
            _host.RefreshKeyboardLayoutOnKey();   // 布局可能刚在 X 窗口里切过:先换键位表,再注入这个键
        }
        if (CommandKeyUpMayBeLost && (e.KeyModifiers & KeyModifiers.Meta) != 0 && keycode is not (XKeycodes.SuperLeft or XKeycodes.SuperRight))
        {
            if (repeat && _pressedWithCommand.Contains(keycode))
            {
                Server?.InjectKey(keycode, pressed: false);   // 上一次的 KeyUp 没来:先松开,这次当新的按下
                repeat = false;
            }
            _pressedWithCommand.Add(keycode);
        }
        Server?.InjectKey(keycode, pressed: true, repeat);
        _keyTextPending = true;
        e.Handled = true;
    }

    // ================================================================== 本机输入法

    /// <summary>
    /// 刚把一个按键注入了 X:系统随后为它报的文字(Windows 的 WM_CHAR、macOS 的 insertText)就是这个键打出来的,X 那边按键码自己会解释,
    /// 不再当文字输入一遍。下一个按键、这个键松开时作废 —— 不出字的键(方向键、F1)不会让之后输入法上屏的字被吞掉。
    /// </summary>
    private bool _keyTextPending;

    /// <summary>最后一次在窗口里按下指针的位置(DIP):输入法的候选框摆在这里(X 程序不告诉我们插入点在哪,点进输入框的位置最接近)。</summary>
    private Point? _lastClick;

    private XImeClient? _imeClient;

    /// <summary>输入法正在组的字(预编辑);X 程序画不了它,由 <see cref="XSurface" /> 叠在候选框的位置上。</summary>
    internal string? Preedit { get; private set; }

    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        if (!_host.UsesHostInputMethod)
        {
            return;   // 不用本机输入法:没有输入法客户端,系统输入法不在这个窗口里组字,按键原样交给 X
        }
        _imeClient ??= new XImeClient(this);
        e.Client = _imeClient;
    }

    /// <summary>
    /// 系统报来的文字:输入法上屏的字(或者 X 键位表里没有的键打出的字)经 <see cref="X11Server.InjectText" /> 输入给 X 程序;
    /// 刚注入过的按键自己打出的字不重复输入。
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_keyTextPending)
        {
            _keyTextPending = false;
            e.Handled = true;
            return;
        }
        if (!_host.UsesHostInputMethod || string.IsNullOrEmpty(e.Text) || Server is not { } server)
        {
            return;
        }
        int at = 0;
        while (at < e.Text.Length)
        {
            int length = Math.Min(X11Server.MaxInjectedTextLength, e.Text.Length - at);
            if (length < e.Text.Length - at && char.IsHighSurrogate(e.Text[at + length - 1]))
            {
                length--;   // 不把一个代理对切成两半
            }
            server.InjectText(e.Text.Substring(at, length));
            at += length;
        }
        e.Handled = true;
    }

    private void SetPreedit(string? text)
    {
        Preedit = string.IsNullOrEmpty(text) ? null : text;
        _surface.InvalidateVisual();
    }

    /// <summary>候选框与预编辑的位置(DIP,相对 <see cref="XSurface" />):最后一次点击处;还没点过时是左上角附近。</summary>
    internal Rect ImeCursorRect
    {
        get
        {
            Point at = _lastClick ?? new Point(8, 8);
            return new Rect(at.X, at.Y, 1, 18);
        }
    }

    /// <summary>
    /// X 窗口的输入法客户端:只给候选框的位置、接预编辑,不提供环绕文字(插入点左右的字在远端程序里,我们看不到)。
    /// 预编辑必须报支持:Avalonia 的 Win32 后端不让输入法自己画组字窗,不接的话用户看不见自己敲了什么(同终端的输入法客户端)。
    /// </summary>
    private sealed class XImeClient(XNativeWindow owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner._surface;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override Rect CursorRectangle => owner.ImeCursorRect;

        public override TextSelection Selection
        {
            get => default;
            set { }
        }

        public override void SetPreeditText(string? preeditText) => owner.SetPreedit(preeditText);

        public override void SetPreeditText(string? preeditText, int? cursorPosition) => owner.SetPreedit(preeditText);

        public void NotifyCursorMoved() => RaiseCursorRectangleChanged();
    }

    /// <summary>
    /// macOS:AppKit 不给带 Command 的组合键发 KeyUp(❓ 未在真机上确认;Avalonia 若已补上,下面的处理只是多余,不出错)。
    /// X 那边会以为那个键一直按着,由它激活的被动键抓取也不解除。为真时:Command 松开时把按着 Command 时按下、还没松开的键一并松开;
    /// 按着 Command 再按一次同一个键当成新的按下(先补一个松开)而不是自动重复。内部可写,测试在别的系统上打开它。
    /// </summary>
    internal static bool CommandKeyUpMayBeLost { get; set; } = OperatingSystem.IsMacOS();

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
        _keyTextPending = false;
        byte keycode = XInputMap.Keycode(e.PhysicalKey);
        if (keycode == 0)
        {
            return;
        }
        _pressedWithCommand.Remove(keycode);
        if (_heldKeys.Remove(keycode))
        {
            Server?.InjectKey(keycode, pressed: false);
            e.Handled = true;
        }
        if (keycode is XKeycodes.SuperLeft or XKeycodes.SuperRight && _pressedWithCommand.Count != 0)
        {
            // Command 松开了:按着它时按下的键的 KeyUp 不会再来,在 X 那边替它们松开。
            foreach (byte key in _pressedWithCommand)
            {
                if (_heldKeys.Remove(key))
                {
                    Server?.InjectKey(key, pressed: false);
                }
            }
            _pressedWithCommand.Clear();
        }
    }

    /// <summary>内区的物理像素坐标,夹到 X 的 16 位范围(服务端的注入方法超出就抛异常;拖动时指针可以远在窗口外)。</summary>
    private (int X, int Y) ToPixels(Point point) =>
        (ClampCoordinate(Math.Floor(point.X * Scale)), ClampCoordinate(Math.Floor(point.Y * Scale)));

    private static int ClampCoordinate(double value) => (int)Math.Clamp(value, short.MinValue, short.MaxValue);

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
        /// <summary>每帧取像素时最多等像素锁这么久;等不到就把这一帧让给界面,下一帧再取。</summary>
        private static readonly TimeSpan PixelLockWait = TimeSpan.FromMilliseconds(8);

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
                XPixelReadResult read;
                try
                {
                    // 限时拿像素锁:服务端正在执行一条慢请求时跳过这一帧(损伤留着,下一帧再取),UI 线程不陪着等。
                    read = _handle.TryReadPixels(_reader, PixelLockWait);
                }
                finally
                {
                    UnlockTiles();
                }
                if (read == XPixelReadResult.Busy)
                {
                    break;
                }
                if (read == XPixelReadResult.NoBuffer)
                {
                    return;   // 窗口已经没有缓冲(销毁 / 被 reparent 走):等宿主把原生窗口收掉
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
            if (_owner.Preedit is { } preedit)
            {
                DrawPreedit(context, preedit, _owner.ImeCursorRect);
            }
        }

        /// <summary>
        /// 输入法正在组的字:X 程序看不到它,叠在候选框的位置上画一个浮层(<c>VelaBgSurface</c> 底、<c>VelaBorderSecondary</c> 边、
        /// <c>VelaTextPrimary</c> 字、下划线),上屏之后随预编辑清空消失。
        /// </summary>
        private void DrawPreedit(DrawingContext context, string preedit, Rect anchor)
        {
            IBrush background = Brush("VelaBgSurface"), border = Brush("VelaBorderSecondary"), foreground = Brush("VelaTextPrimary");
            double fontSize = this.TryFindResource("VelaFontSize13", out object? size) && size is double s ? s : 13;
            FormattedText text = new(preedit, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface.Default, fontSize, foreground);
            const double padX = 6, padY = 3;
            double width = text.Width + (2 * padX), height = text.Height + (2 * padY);
            double x = Math.Clamp(anchor.X, 0, Math.Max(0, Bounds.Width - width));
            double y = anchor.Bottom + height <= Bounds.Height ? anchor.Bottom : Math.Max(0, anchor.Y - height);
            Rect box = new(x, y, width, height);
            context.DrawRectangle(background, new Pen(border, 1), box, 4, 4);
            context.DrawText(text, new Point(x + padX, y + padY));
            context.DrawLine(new Pen(foreground, 1), new Point(x + padX, y + padY + text.Height), new Point(x + padX + text.Width, y + padY + text.Height));

            IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;
        }
    }
}
