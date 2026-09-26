using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.PluginHost;

/// <summary>
/// 隔离插件独立窗口的自绘卡片壳(与主程序资源监视/任务管理器同规格):8px 圆角卡片 + 40px 标题栏 +
/// min/max/close 三连按钮 + 自绘缩放抓取区。
/// 纯代码构建(PluginHost 不依赖 VelaShell.Controls);配色用宿主下发的 <c>Vela*</c> 令牌;
/// caption 图标用几何 Path(lucide minus/square/x)。
/// </summary>
/// <remarks>
/// 外框按平台区分,与主程序 <c>VelaShell.Views.WindowChrome</c> 同一套规则(本工程按依赖纪律不引用主程序,
/// 故各写一份,改动请两边同步):
/// <list type="bullet">
///   <item>Windows:透明窗口 + 卡片外 16px 边距里自绘投影(DWM 对无边框透明窗口什么都不加);</item>
///   <item>macOS:<c>Full</c> + 扩展客户区,系统红绿灯、圆角与阴影,窗口不透明;自绘的三个窗口按钮隐去,
///   标题栏左侧给红绿灯让位;</item>
///   <item>Linux 原生 Wayland:<c>BorderOnly</c>,阴影与描边由 Avalonia 的装饰层画
///   (<see cref="WaylandDecorationsTheme" />),合成器以描边为窗口边界;</item>
///   <item>Linux X11:不透明直角矩形 —— 合成器只认整个窗口矩形,透明边距只会成为一圈「空白」。</item>
/// </list>
/// 最大化 / 全屏时卡片在各平台都铺满成直角。
/// </remarks>
internal sealed partial class PluginHostShellWindow : Window
{
    private const double InnerRadius = 7;

    /// <summary>卡片外边距,即投影的画布宽度;与主程序 VelaShadowWindow 的最大延展一致。</summary>
    private const double CardGutter = 16;

    /// <summary>卡片描边宽度。Wayland 上描边由装饰层画在内容区外面。</summary>
    private const double CardFrame = 1;

    /// <summary>卡片贴边时(X11 的不透明矩形)的抓取区尺寸,压在内容上故取最小值。</summary>
    private const double FlushGripEdge = 5,
        FlushGripCorner = 10;

    /// <summary>macOS 上给系统红绿灯让出的宽度(加上标题栏原有的左内边距)。</summary>
    private const double TrafficLightInset = 60;

    /// <summary>
    /// 标题栏高度,与主程序全部窗口一致(<c>WindowChrome.TitleBarHeight</c>);窗口按钮高 = 标题栏 − 1px 底边。
    /// macOS 上跟系统标题栏的实际高度走(见 <see cref="ApplyTitleBarHeight" />)。
    /// </summary>
    private const double TitleBarHeight = 28;

    /// <summary>
    /// 卡片投影,与主程序暗色的 VelaShadowWindow 令牌同值(此进程不加载宿主的主题字典,
    /// 拿不到那个令牌,只能照抄;改一处要记得改另一处)。近处一层压出边缘、远处一层给扩散。
    /// </summary>
    private const string CardShadow = "0 2 4 0 #59000000, 0 6 10 0 #A6000000";

    private readonly Border _rootCard;
    private readonly Border _titleStrip;
    private readonly RowDefinition _titleRow;
    private readonly Panel _resizeGrips;
    private readonly ShellPlatform _platform;
    private readonly List<Button> _windowButtons = [];
    private Control? _trafficLightSpacer;

    public PluginHostShellWindow(
        string title, string subtitle, Control content,
        IReadOnlyList<PanelTitleAction>? titleActions = null,
        PluginSdk.PluginIcon? icon = null)
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _titleStrip = BuildTitleBar(title, subtitle, titleActions ?? [], icon);
        _titleRow = new RowDefinition(TitleBarHeight, GridUnitType.Pixel);
        var grid = new Grid { RowDefinitions = { _titleRow, new RowDefinition(1, GridUnitType.Star) } };
        grid.Children.Add(_titleStrip);
        var contentHost = new ContentControl { Content = content };
        Grid.SetRow(contentHost, 1);
        grid.Children.Add(contentHost);

        _rootCard = new Border
        {
            // 外边距是投影的画布:透明窗体的投影超出窗口矩形的部分会被直接裁掉,
            // 故留白必须 ≥ 投影的最大延展(offsetY + blur = 16),与主程序同一口径。
            Margin = new Thickness(CardGutter),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse(CardShadow),
            Child = grid
        };
        Bind(_rootCard, Border.BackgroundProperty, "VelaBgSurface");
        Bind(_rootCard, Border.BorderBrushProperty, "VelaBorderSecondary");

        _resizeGrips = BuildResizeGrips();

        var rootPanel = new Panel();
        rootPanel.Children.Add(_rootCard);
        rootPanel.Children.Add(_resizeGrips);
        Content = rootPanel;

        _platform = DetectPlatform();
        ApplyPlatformChrome();
        ApplyWindowState();
    }

    /// <summary>
    /// 窗口尺寸比按 Windows 口径给的少多少(宽高各):其它平台没有卡片外 16px 的投影留白,
    /// Wayland 的描边又画在窗口尺寸之外。调用方按插件声明的尺寸开窗时减掉它,卡片的可见尺寸才在各平台一致。
    /// </summary>
    internal double SizeReduction => _platform switch
    {
        ShellPlatform.Windows => 0,
        ShellPlatform.Wayland => 2 * (CardGutter + CardFrame),
        _ => 2 * CardGutter
    };

    private enum ShellPlatform
    {
        Windows,
        MacOS,
        Wayland,
        X11
    }

    /// <summary>
    /// 与主程序 <c>WindowChrome.Detect</c> 同一判断:X11 后端在构造时就建好了句柄(描述符 <c>"XID"</c>),
    /// Wayland 后端不提供句柄;两者都不是时归到 X11(最保守的外观)。
    /// </summary>
    private ShellPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return ShellPlatform.Windows;
        }
        if (OperatingSystem.IsMacOS())
        {
            return ShellPlatform.MacOS;
        }
        if (TryGetPlatformHandle()?.HandleDescriptor == "XID")
        {
            return ShellPlatform.X11;
        }
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? ShellPlatform.X11
            : ShellPlatform.Wayland;
    }

    /// <summary>按平台设装饰模式与透明度。Windows 保持构造时的透明无边框,不动。</summary>
    private void ApplyPlatformChrome()
    {
        switch (_platform)
        {
            case ShellPlatform.MacOS:
                // 透明窗口在 macOS 上每帧走全表面 alpha 合成,滚动掉帧:不透明,圆角、描边与阴影交给系统。
                WindowDecorations = WindowDecorations.Full;
                ExtendClientAreaToDecorationsHint = true;
                TransparencyLevelHint = [WindowTransparencyLevel.None];
                Bind(this, BackgroundProperty, "VelaBgSurface");
                foreach (Button button in _windowButtons)
                {
                    button.IsVisible = false; // 用系统红绿灯
                }
                break;
            case ShellPlatform.Wayland:
                // 不扩展客户区:装饰层画在内容外面,并经 set_window_geometry 告诉合成器真正的窗口范围。
                WindowDecorations = WindowDecorations.BorderOnly;
                WindowDecorationsTheme = WaylandDecorationsTheme();
                break;
            case ShellPlatform.X11:
                TransparencyLevelHint = [WindowTransparencyLevel.None];
                Bind(this, BackgroundProperty, "VelaBgSurface");
                break;
        }
    }

    /// <summary>
    /// Wayland 的装饰层:阴影宽 16(与 Windows 的卡片边距一致)、1px 描边、外圆角 8,不画标题栏。
    /// 与主程序 <c>Themes/WindowChrome.axaml</c> 的 <c>VelaWaylandWindowDecorations</c> 同规格,
    /// 这里纯代码构建(本工程没有 XAML)。
    /// </summary>
    private static ControlTheme WaylandDecorationsTheme() => new(typeof(WindowDrawnDecorations))
    {
        Setters =
        {
            new Setter(WindowDrawnDecorations.DefaultFrameThicknessProperty, new Thickness(CardFrame)),
            new Setter(WindowDrawnDecorations.DefaultShadowThicknessProperty, new Thickness(CardGutter)),
            new Setter(WindowDrawnDecorations.TemplateProperty, new WaylandDecorationsTemplate())
        }
    };

    private sealed class WaylandDecorationsTemplate : IWindowDrawnDecorationsTemplate
    {
        public TemplateResult<WindowDrawnDecorationsContent> Build()
        {
            var frame = new Border
            {
                CornerRadius = new CornerRadius(8),
                BoxShadow = BoxShadows.Parse(CardShadow),
                IsHitTestVisible = false
            };
            // 底色与卡片同色:卡片的圆角 7 与描边内沿的圆弧重合,垫一层同色底,抗锯齿的那一圈不漏缝。
            Bind(frame, Border.BackgroundProperty, "VelaBgSurface");
            Bind(frame, Border.BorderBrushProperty, "VelaBorderSecondary");
            frame[!Border.BorderThicknessProperty] = new TemplateBinding(WindowDrawnDecorations.FrameThicknessProperty);
            var underlay = new Panel { Children = { frame } };
            underlay[!MarginProperty] = new TemplateBinding(WindowDrawnDecorations.ShadowThicknessProperty);
            var content = new WindowDrawnDecorationsContent { Underlay = underlay, Overlay = new Panel() };
            return new TemplateResult<WindowDrawnDecorationsContent>(content, new NameScope());
        }

        object? ITemplate.Build() => Build();
    }

    private Border BuildTitleBar(
        string title, string subtitle, IReadOnlyList<PanelTitleAction> titleActions,
        PluginSdk.PluginIcon? icon)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Bind(titleText, TextBlock.ForegroundProperty, "VelaTextPrimary");
        var subtitleText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Bind(subtitleText, TextBlock.ForegroundProperty, "VelaTextMuted");

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        // 给 macOS 的系统红绿灯让位;其余平台与全屏时不显示(见 ApplyWindowState)。
        _trafficLightSpacer = new Panel { Width = TrafficLightInset, IsVisible = false };
        left.Children.Add(_trafficLightSpacer);
        // 插件自报的图标(PanelOptions.Icon)。这一侧原先一个图标都没有,于是隔离插件的窗口
        // 标题栏彼此完全一样;而进程内那扇窗写死通用插头,同样分不出谁是谁。
        if (TitleIcon(icon) is { } glyph)
        {
            left.Children.Add(glyph);
        }
        left.Children.Add(titleText);
        left.Children.Add(subtitleText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        // 插件声明的标题栏动作按钮(PanelOptions.TitleActions)插在最小化键左侧,与主程序的 PluginPanelWindow 同一位置
        foreach (PanelTitleAction action in titleActions)
        {
            buttons.Children.Add(ActionButton(action));
        }
        // 三个窗口按钮单独记下:macOS 上换成系统红绿灯时要隐去它们,插件的动作按钮照常显示。
        _windowButtons.Add(CaptionButton(MinusGeometry(), close: false, () => WindowState = WindowState.Minimized));
        _windowButtons.Add(CaptionButton(SquareGeometry(), close: false, ToggleMaximize));
        _windowButtons.Add(CaptionButton(CrossGeometry(), close: true, Close));
        foreach (Button button in _windowButtons)
        {
            buttons.Children.Add(button);
        }

        var grid = new Grid { ColumnDefinitions = [with("*,Auto")] };
        grid.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var strip = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(InnerRadius, InnerRadius, 0, 0),
            Child = grid
        };
        Bind(strip, Border.BackgroundProperty, "VelaBgSurface");
        Bind(strip, Border.BorderBrushProperty, "VelaBorderPrimary");
        strip.PointerPressed += OnHeaderPressed;
        return strip;
    }

    private Button CaptionButton(Geometry geometry, bool close, Action onClick)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = geometry, StrokeThickness = 1.4, StrokeLineCap = PenLineCap.Round };
        Bind(icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "VelaTextSecondary");
        return CaptionButton(icon, close, onClick);
    }

    /// <summary>
    /// 标题栏最左的插件图标。插件没给、或那段路径解析不了,就不画 ——
    /// 这一侧本来就没有通用兜底图标可退(那是主程序 ConnectionIcon 的事)。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里刻意捕获所有异常,理由与主程序 <c>ConnectionIcon.FromPlugin</c> 逐字相同:
    /// 解析的是插件给的任意字符串,Avalonia 的 <c>PathMarkupParser</c> 抛什么取决于错在哪一位
    /// (试出来的就有 <c>InvalidDataException</c> 与 <c>FormatException</c>),
    /// 枚举类型是在猜,而猜漏一个的代价是整扇窗连带插件进程一起起不来。
    /// </remarks>
    private static Viewbox? TitleIcon(PluginSdk.PluginIcon? icon)
    {
        if (icon is null || string.IsNullOrWhiteSpace(icon.PathData))
        {
            return null;
        }
        Geometry geometry;
        try
        {
            geometry = Geometry.Parse(icon.PathData);
        }
        catch (Exception)
        {
            return null;
        }
        // 视框不报就是 24(lucide)。非正数/非有限值同样按 24 —— 一个写错的值不该让图标消失。
        double box = icon.ViewBoxSize is > 0 and < double.PositiveInfinity ? icon.ViewBoxSize : 24d;
        var path = new Avalonia.Controls.Shapes.Path { Width = box, Height = box, Data = geometry };
        if (icon.IsFilled)
        {
            // 实心图形只填充。再描一圈边等于给每个色块套上轮廓,比不描更糟。
            Bind(path, Avalonia.Controls.Shapes.Shape.FillProperty, "VelaAccent");
        }
        else
        {
            // 描边保持 lucide 2/24 的粗细比例 —— 视框放大多少,笔就跟着粗多少。
            path.StrokeThickness = 2 * box / 24d;
            path.StrokeLineCap = PenLineCap.Round;
            path.StrokeJoin = PenLineJoin.Round;
            Bind(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, "VelaAccent");
        }
        return new Viewbox
        {
            Width = 13,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Child = path
        };
    }

    /// <summary>插件的标题栏动作:lucide 24×24 路径缩到 12px,描边 2 与主程序 LucideIcon 一致。</summary>
    private Button ActionButton(PanelTitleAction action)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            Data = Geometry.Parse(action.IconPathData),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        Bind(icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "VelaTextSecondary");
        Button button = CaptionButton(new Viewbox { Width = 12, Height = 12, Child = icon }, close: false, action.OnClick);
        ToolTip.SetTip(button, action.ToolTip);
        return button;
    }

    private Button CaptionButton(Control icon, bool close, Action onClick)
    {
        var button = new Button
        {
            Width = TitleBarHeight - 1, // 方形:边长 = 标题栏 − 1px 底边
            Height = TitleBarHeight - 1, // 标题栏 − 1px 底边
            Padding = default,
            Background = Brushes.Transparent,
            BorderThickness = default,
            CornerRadius = default,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon
        };
        // hover 反馈:关闭键红,其余中性 —— 直接在事件里改背景(壳无样式表)。
        button.PointerEntered += (_, _) =>
            button.Background = close ? new SolidColorBrush(Color.Parse("#E81123")) : ThemeBrush("VelaBgHover");
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => onClick();
        return button;
    }

    private Panel BuildResizeGrips()
    {
        var panel = new Panel { ZIndex = 200 };
        AddGrip(panel, WindowEdge.North, StandardCursorType.TopSide);
        AddGrip(panel, WindowEdge.South, StandardCursorType.BottomSide, bottom: true);
        AddGrip(panel, WindowEdge.West, StandardCursorType.LeftSide, vertical: true);
        AddGrip(panel, WindowEdge.East, StandardCursorType.RightSide, vertical: true, right: true);
        AddCorner(panel, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, left: true, top: true);
        AddCorner(panel, WindowEdge.NorthEast, StandardCursorType.TopRightCorner, left: false, top: true);
        AddCorner(panel, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, left: true, top: false);
        AddCorner(panel, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, left: false, top: false);
        ApplyGripThickness(panel, rounded: true);
        return panel;
    }

    private void AddGrip(Panel panel, WindowEdge edge, StandardCursorType cursor,
        bool vertical = false, bool bottom = false, bool right = false)
    {
        var border = new Border
        {
            Tag = edge,
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor)
        };
        if (vertical)
        {
            border.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        else
        {
            border.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        }
        border.PointerPressed += (_, e) => BeginResize(edge, e);
        panel.Children.Add(border);
    }

    private void AddCorner(Panel panel, WindowEdge edge, StandardCursorType cursor, bool left, bool top)
    {
        var border = new Border
        {
            Tag = edge,
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom
        };
        border.PointerPressed += (_, e) => BeginResize(edge, e);
        panel.Children.Add(border);
    }

    /// <summary>
    /// 抓取区厚度跟随卡片形态:圆角态铺满 16px 的投影留白(否则那圈留白看得见点不动),
    /// 铺满态收回 5px(否则会压在内容上吃掉最靠边控件的点击,比如滚动条)。
    /// </summary>
    private static void ApplyGripThickness(Panel grips, bool rounded)
    {
        double edge = rounded ? CardGutter : FlushGripEdge;
        double corner = rounded ? CardGutter + 6 : FlushGripCorner;
        foreach (Control child in grips.Children)
        {
            if (child is not Border { Tag: WindowEdge tag })
            {
                continue;
            }
            switch (tag)
            {
                // 上下边让开四角的宽度,否则角上的抓取区被边压住,拿不到斜向缩放。
                case WindowEdge.North or WindowEdge.South:
                    child.Height = edge;
                    child.Margin = new Thickness(corner, 0);
                    break;
                case WindowEdge.West or WindowEdge.East:
                    child.Width = edge;
                    child.Margin = new Thickness(0, corner);
                    break;
                default:
                    child.Width = corner;
                    child.Height = corner;
                    break;
            }
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        BeginWindowMoveDrag(e);
    }

    /// <summary>
    /// 拖动窗口:Windows 上自己走一遍移动模态循环,避开 Avalonia 留下的 (0,0) 幽灵指针。
    /// </summary>
    /// <remarks>
    /// Avalonia 的 Win32 <c>BeginMoveDrag</c> 在系统移动模态循环结束后,会给自己补一条
    /// <c>WM_LBUTTONUP(wParam: 0, lParam: 0)</c> —— 真正那次弹起被模态循环吃掉了,不补
    /// 指针状态会停在"按下"。但 <c>lParam = 0</c> 被解码成客户区 (0,0),pointer-over 因此
    /// 落到窗口左上角,压在那里的正是 NorthWest 缩放抓取区(TopLeftCorner 光标),光标于是
    /// 闪一下对角双箭头(主仓 issue #264)。这里照抄那两步,只把补发弹起的坐标换成光标真实位置。
    ///
    /// 与主程序 <c>VelaShell.Views.WindowMoveDrag</c> 同源;本工程按依赖纪律不引用任何
    /// VelaShell.* 主程序工程,故复制一份,改动请两边同步。
    /// </remarks>
    /// <param name="e">触发拖动的指针按下事件。</param>
    private void BeginWindowMoveDrag(PointerPressedEventArgs e)
    {
        const uint WM_SYSCOMMAND = 0x0112,
            WM_LBUTTONUP = 0x0202;
        const int SC_MOUSEMOVE = 0xF012; // SC_MOVE + HTCAPTION:鼠标发起的标题栏移动
        if (!OperatingSystem.IsWindows() || !e.Pointer.IsPrimary || TryGetPlatformHandle() is not { } handle)
        {
            BeginMoveDrag(e);
            return;
        }
        IntPtr hWnd = handle.Handle;
        e.Pointer.Capture(null);
        // 与 Avalonia 同样后置到派发队列:在输入处理栈里直接进模态循环会把这次派发一起卡住。
        Dispatcher.UIThread.Post(
            () =>
            {
                _ = SendMessage(hWnd, WM_SYSCOMMAND, SC_MOUSEMOVE, IntPtr.Zero); // 阻塞至松键
                if (IsWindow(hWnd))
                {
                    _ = SendMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, CursorLParam(hWnd));
                }
            },
            DispatcherPriority.Send
        );
    }

    /// <summary>把光标当前位置打包成鼠标消息的 lParam(客户区物理坐标,低 16 位 x / 高 16 位 y)。</summary>
    private static IntPtr CursorLParam(IntPtr hWnd)
    {
        if (!GetCursorPos(out POINT cursor) || !ScreenToClient(hWnd, ref cursor))
        {
            return IntPtr.Zero; // 取不到就退回 Avalonia 原本的行为,不会更差
        }
        return unchecked(((cursor.Y & 0xFFFF) << 16) | (cursor.X & 0xFFFF));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            ApplyWindowState();
        }
        else if (change.Property == WindowDecorationMarginProperty)
        {
            ApplyTitleBarHeight();
        }
    }

    /// <summary>
    /// macOS 的红绿灯由系统按它自己的标题栏垂直居中、位置改不了:标题栏跟系统标题栏同高,两者才同一条中线
    /// (与主程序 <c>WindowChrome.TitleBarHeightFor</c> 同一规则)。系统标题栏的实际高度是
    /// <see cref="Window.WindowDecorationMargin" /> 的 Top;全屏时它是 0,保持原高度。
    /// </summary>
    private void ApplyTitleBarHeight()
    {
        if (_platform == ShellPlatform.MacOS && WindowDecorationMargin.Top > 0)
        {
            _titleRow.Height = new GridLength(WindowDecorationMargin.Top, GridUnitType.Pixel);
        }
    }

    /// <summary>
    /// 按平台与窗口状态定卡片形态。最大化 / 全屏时卡片铺满成直角(圆角与投影留白只在普通态成立);
    /// macOS 与 X11 恒是直角;Wayland 的描边与阴影在装饰层里,卡片只留描边内侧的圆角 7。
    /// 抓取区只在普通态、且系统不提供边缘缩放时显示(macOS 的系统外框、Wayland 的装饰层都自带)。
    /// </summary>
    private void ApplyWindowState()
    {
        WindowState state = WindowState;
        bool normal = state == WindowState.Normal;
        bool flat = _platform is ShellPlatform.MacOS or ShellPlatform.X11
            || state is WindowState.Maximized or WindowState.FullScreen;
        bool floating = _platform == ShellPlatform.Windows && !flat;
        _rootCard.Margin = floating ? new Thickness(CardGutter) : default;
        _rootCard.BorderThickness = floating ? new Thickness(1) : default;
        _rootCard.CornerRadius = floating
            ? new CornerRadius(8)
            : flat ? default : new CornerRadius(InnerRadius);
        _rootCard.BoxShadow = floating ? BoxShadows.Parse(CardShadow) : default;
        _titleStrip.CornerRadius = flat ? default : new CornerRadius(InnerRadius, InnerRadius, 0, 0);

        _resizeGrips.IsVisible = normal && _platform is ShellPlatform.Windows or ShellPlatform.X11;
        ApplyGripThickness(_resizeGrips, rounded: floating);
        _trafficLightSpacer?.IsVisible = _platform == ShellPlatform.MacOS && state != WindowState.FullScreen;
    }

    private static void Bind(Control control, AvaloniaProperty property, string resourceKey) =>
        control[!property] = new DynamicResourceExtension(resourceKey);

    private IBrush ThemeBrush(string key) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

    // ---- lucide 图标几何(minus / square / x)----
    private static Geometry MinusGeometry() => Geometry.Parse("M4,7 H14");
    private static Geometry SquareGeometry() => Geometry.Parse("M4,4 H14 V14 H4 Z");
    private static Geometry CrossGeometry() => Geometry.Parse("M4,4 L14,14 M14,4 L4,14");
}
