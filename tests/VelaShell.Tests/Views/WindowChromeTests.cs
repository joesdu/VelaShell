using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// <see cref="WindowChrome" /> 的四个平台分支:窗口属性、样式类、卡片外观、窗口尺寸,以及工具窗口的最大化与缩放抓取区。
/// </summary>
/// <remarks>
/// 开发机与 CI 各自只跑得了一个平台,所以一律用显式传平台的重载,把四个分支都跑到;
/// 窗口构造时已按本机平台装过一次外框,这里再装一次同时验证了「可以重复调用」。
/// macOS 与 Linux 上系统 / 合成器实际画成什么样,无头模式看不到,由实机验收。
/// </remarks>
[TestClass]
[TestCategory("WindowChrome")]
public sealed class WindowChromeTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WindowChromeTests).Assembly);

    [TestMethod]
    public void Dialog_Windows_IsTransparentWindowWithoutDecorations() =>
        AssertCardWindow(WindowChromeKind.Dialog, ChromePlatform.Windows, WindowDecorations.None, extend: false, transparent: true);

    [TestMethod]
    public void Dialog_MacOS_IsOpaqueBorderOnlyWindowWithoutTrafficLights() =>
        AssertCardWindow(WindowChromeKind.Dialog, ChromePlatform.MacOS, WindowDecorations.BorderOnly, extend: true, transparent: false);

    [TestMethod]
    public void Dialog_Wayland_IsTransparentBorderOnlyWindowWithDrawnDecorations() =>
        AssertCardWindow(WindowChromeKind.Dialog, ChromePlatform.LinuxWayland, WindowDecorations.BorderOnly, extend: false, transparent: true);

    [TestMethod]
    public void Dialog_X11_IsOpaqueWindowWithoutDecorations() =>
        AssertCardWindow(WindowChromeKind.Dialog, ChromePlatform.LinuxX11, WindowDecorations.None, extend: false, transparent: false);

    [TestMethod]
    public void Tool_MacOS_ShowsTrafficLights() =>
        AssertCardWindow(WindowChromeKind.Tool, ChromePlatform.MacOS, WindowDecorations.Full, extend: true, transparent: false);

    [TestMethod]
    public void Tool_OtherPlatforms_MatchDialogs()
    {
        AssertCardWindow(WindowChromeKind.Tool, ChromePlatform.Windows, WindowDecorations.None, extend: false, transparent: true);
        AssertCardWindow(WindowChromeKind.Tool, ChromePlatform.LinuxWayland, WindowDecorations.BorderOnly, extend: false, transparent: true);
        AssertCardWindow(WindowChromeKind.Tool, ChromePlatform.LinuxX11, WindowDecorations.None, extend: false, transparent: false);
    }

    [TestMethod]
    public void Main_OnlyMacOSSwitchesToSystemFrame()
    {
        OnUi(() =>
        {
            var window = new Window();
            WindowChrome.Apply(window, WindowChromeKind.Main, ChromePlatform.MacOS);
            Assert.AreEqual(WindowDecorations.Full, window.WindowDecorations);
            Assert.IsTrue(window.ExtendClientAreaToDecorationsHint);
            Assert.IsTrue(window.Classes.Contains("chrome-traffic-lights"));
            Assert.IsFalse(window.Classes.Contains("window-card-flat"), "主窗口没有卡片,不挂直角类。");
            AssertSinglePlatformClass(window, "chrome-macos");

            foreach (ChromePlatform platform in new[] { ChromePlatform.Windows, ChromePlatform.LinuxWayland, ChromePlatform.LinuxX11 })
            {
                WindowChrome.Apply(window, WindowChromeKind.Main, platform);
                Assert.AreEqual(WindowDecorations.None, window.WindowDecorations, platform.ToString());
                Assert.IsFalse(window.ExtendClientAreaToDecorationsHint, platform.ToString());
                Assert.IsNull(window.WindowDecorationsTheme, platform.ToString());
                Assert.IsFalse(window.Classes.Contains("chrome-traffic-lights"), platform.ToString());
                Assert.AreEqual(0, WindowChrome.OuterFrameSize(window), platform.ToString());
                Assert.AreEqual(0, WindowChrome.SizeReductionOf(window), platform.ToString());
            }
        });
    }

    [TestMethod]
    public void WaylandDecorationsTheme_MatchesTheCardGeometry()
    {
        OnUi(() =>
        {
            Assert.IsTrue(Application.Current!.TryFindResource(WindowChrome.WaylandDecorationsThemeKey, out object? resource));
            var theme = resource as ControlTheme;
            Assert.IsNotNull(theme);
            Assert.AreEqual(typeof(WindowDrawnDecorations), theme.TargetType);
            // 阴影宽度与 Windows 的卡片边距一致,描边与卡片描边一致:窗口尺寸的换算按这两个常量算,
            // 主题里改了数、代码没跟着改,卡片就不再与 Windows 一样大。
            Assert.AreEqual(new Thickness(WindowChrome.CardMargin), SetterValue(theme, WindowDrawnDecorations.DefaultShadowThicknessProperty));
            Assert.AreEqual(new Thickness(WindowChrome.CardFrame), SetterValue(theme, WindowDrawnDecorations.DefaultFrameThicknessProperty));
        });
    }

    [TestMethod]
    public void SettingsView_SizeFollowsPlatform_AndReapplyRestoresIt()
    {
        OnUi(() =>
        {
            var window = new SettingsView();
            WindowChrome.Apply(window, WindowChromeKind.Dialog, ChromePlatform.Windows);
            AssertSize(window, 948, 768, 760, 480);

            // macOS / X11:没有 16px 边距,窗口就是卡片。
            WindowChrome.Apply(window, WindowChromeKind.Dialog, ChromePlatform.MacOS);
            AssertSize(window, 916, 736, 728, 448);
            Assert.AreEqual(32, WindowChrome.SizeReductionOf(window));
            WindowChrome.Apply(window, WindowChromeKind.Dialog, ChromePlatform.LinuxX11);
            AssertSize(window, 916, 736, 728, 448);

            // Wayland:Width/Height 只算内容,描边画在外面,再各少 2。窗口表面(内容 + 两边 17)仍是 948×768。
            WindowChrome.Apply(window, WindowChromeKind.Dialog, ChromePlatform.LinuxWayland);
            AssertSize(window, 914, 734, 726, 446);
            Assert.AreEqual(34, WindowChrome.OuterFrameSize(window));

            WindowChrome.Apply(window, WindowChromeKind.Dialog, ChromePlatform.Windows);
            AssertSize(window, 948, 768, 760, 480);
            Assert.AreEqual(0, WindowChrome.OuterFrameSize(window));
        });
    }

    [TestMethod]
    public void MessageDialog_KeepsSizeToContentHeight()
    {
        OnUi(() =>
        {
            var dialog = new MessageDialog();
            WindowChrome.Apply(dialog, WindowChromeKind.Dialog, ChromePlatform.Windows);
            Assert.AreEqual(452, dialog.Width);
            Assert.IsTrue(double.IsNaN(dialog.Height));

            WindowChrome.Apply(dialog, WindowChromeKind.Dialog, ChromePlatform.MacOS);
            Assert.AreEqual(420, dialog.Width);
            Assert.IsTrue(double.IsNaN(dialog.Height), "SizeToContent 的那一边由内容决定,不能被改成定值。");
            Assert.AreEqual(SizeToContent.Height, dialog.SizeToContent);
        });
    }

    [TestMethod]
    public void Cards_FollowPlatformClass()
    {
        OnUi(() =>
        {
            var settings = new SettingsView();
            var dialog = new MessageDialog();
            try
            {
                settings.Show();
                dialog.Show();
                Border settingsCard = settings.FindControl<Border>("RootBorder")!;
                Border navStrip = settings.FindControl<Border>("NavStrip")!;
                var dialogCard = (Border)dialog.Content!;
                Assert.IsTrue(settingsCard.Classes.Contains("window-card"));
                Assert.IsTrue(dialogCard.Classes.Contains("window-card"));

                foreach (ChromePlatform platform in Enum.GetValues<ChromePlatform>())
                {
                    WindowChrome.Apply(settings, WindowChromeKind.Dialog, platform);
                    WindowChrome.Apply(dialog, WindowChromeKind.Dialog, platform);
                    Dispatcher.UIThread.RunJobs();
                    AssertCard(settingsCard, platform);
                    AssertCard(dialogCard, platform);
                    // 导航条贴着卡片左侧两个角:卡片圆角时取内半径 7,直角时跟着直角。
                    CornerRadius expectedNav = platform is ChromePlatform.MacOS or ChromePlatform.LinuxX11
                        ? new CornerRadius(0)
                        : new CornerRadius(7, 0, 0, 7);
                    Assert.AreEqual(expectedNav, navStrip.CornerRadius, platform.ToString());
                }
            }
            finally
            {
                settings.Close();
                dialog.Close();
            }
        });
    }

    [TestMethod]
    public void ToolWindow_MaximizeFlattensCard_AndResizeGripsFollowPlatform()
    {
        OnUi(() =>
        {
            var window = new TraceRouteWindow();
            try
            {
                window.Show();
                Border card = window.FindControl<Border>("RootCard")!;
                Border title = window.FindControl<Border>("TitleBarStrip")!;
                Border status = window.FindControl<Border>("StatusStrip")!;
                Panel grips = window.FindControl<Panel>("ResizeGrips")!;
                Border north = grips.Children.OfType<Border>().Single(grip => Equals(grip.Tag, "North"));

                // Windows 普通态:与改造前 XAML 里写死的值一致,抓取区铺满 16px 的投影留白。
                WindowChrome.Apply(window, WindowChromeKind.Tool, ChromePlatform.Windows);
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(new Thickness(16), card.Margin);
                Assert.AreEqual(new CornerRadius(7, 7, 0, 0), title.CornerRadius);
                Assert.AreEqual(new CornerRadius(0, 0, 7, 7), status.CornerRadius);
                Assert.IsTrue(grips.IsVisible);
                Assert.AreEqual(16, north.Height);

                // 最大化:卡片铺满成直角,贴角的标题栏 / 状态栏跟着直角,抓取区让位。
                window.WindowState = WindowState.Maximized;
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(window.Classes.Contains("window-card-flat"));
                Assert.AreEqual(new Thickness(0), card.Margin);
                Assert.AreEqual(new CornerRadius(0), card.CornerRadius);
                Assert.AreEqual(0, card.BoxShadow.Count);
                Assert.AreEqual(new CornerRadius(0), title.CornerRadius);
                Assert.AreEqual(new CornerRadius(0), status.CornerRadius);
                Assert.IsFalse(grips.IsVisible);

                window.WindowState = WindowState.Normal;
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(window.Classes.Contains("window-card-flat"));
                Assert.AreEqual(new Thickness(16), card.Margin);
                Assert.AreEqual(new CornerRadius(7, 7, 0, 0), title.CornerRadius);
                Assert.IsTrue(grips.IsVisible);

                // X11:直角卡片,抓取区收成贴边的 5px(压在内容上,只取够用的最小值)。
                WindowChrome.Apply(window, WindowChromeKind.Tool, ChromePlatform.LinuxX11);
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(new CornerRadius(0), title.CornerRadius);
                Assert.IsTrue(grips.IsVisible);
                Assert.AreEqual(5, north.Height);

                // macOS 的系统外框、Wayland 的装饰层自带边缘缩放:自绘抓取区让位。
                WindowChrome.Apply(window, WindowChromeKind.Tool, ChromePlatform.MacOS);
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(grips.IsVisible);
                WindowChrome.Apply(window, WindowChromeKind.Tool, ChromePlatform.LinuxWayland);
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(grips.IsVisible);
                Assert.AreEqual(new CornerRadius(7), card.CornerRadius);
                Assert.AreEqual(new CornerRadius(7, 7, 0, 0), title.CornerRadius);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void TrafficLights_HideWindowButtonsAndMakeRoom_ExceptInFullScreen()
    {
        OnUi(() =>
        {
            var window = new TraceRouteWindow();
            try
            {
                window.Show();
                Button[] windowButtons = [.. window.GetLogicalDescendants().OfType<Button>().Where(button => button.Classes.Contains("window-caption"))];
                Panel spacer = window.GetLogicalDescendants().OfType<Panel>().Single(panel => panel.Classes.Contains("traffic-light-spacer"));
                Assert.HasCount(3, windowButtons, "最小化 / 最大化 / 关闭都要挂 window-caption。");

                WindowChrome.Apply(window, WindowChromeKind.Tool, ChromePlatform.Windows);
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(windowButtons.All(button => button.IsVisible));
                Assert.IsFalse(spacer.IsVisible);

                WindowChrome.Apply(window, WindowChromeKind.Tool, ChromePlatform.MacOS);
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(windowButtons.All(button => !button.IsVisible), "macOS 用系统红绿灯。");
                Assert.IsTrue(spacer.IsVisible, "左侧要给红绿灯让位。");

                // 全屏时红绿灯随系统标题栏收起,让位跟着撤掉。
                window.WindowState = WindowState.FullScreen;
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(spacer.IsVisible);
                window.WindowState = WindowState.Normal;
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(spacer.IsVisible);

                // 对话框在 macOS 上是 BorderOnly,没有红绿灯,自己的关闭按钮照常显示。
                WindowChrome.Apply(window, WindowChromeKind.Dialog, ChromePlatform.MacOS);
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(windowButtons.All(button => button.IsVisible));
                Assert.IsFalse(spacer.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void TitleBar_MacOSHidesCaptionButtonsAndMakesRoomForTrafficLights()
    {
        OnUi(() =>
        {
            var titleBar = new TitleBarView();
            var window = new Window { Width = 1200, Height = 100, Content = titleBar };
            try
            {
                WindowChrome.Apply(window, WindowChromeKind.Main, ChromePlatform.MacOS);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Button[] windowButtons = [.. titleBar.GetLogicalDescendants().OfType<Button>().Where(button => button.Classes.Contains("window-caption"))];
                Panel spacer = titleBar.GetLogicalDescendants().OfType<Panel>().Single(panel => panel.Classes.Contains("traffic-light-spacer"));
                Assert.HasCount(3, windowButtons);
                Assert.IsTrue(windowButtons.All(button => !button.IsVisible), "macOS 用系统红绿灯,自绘的三个窗口按钮要隐去。");
                Assert.IsTrue(spacer.IsVisible, "左侧要给红绿灯让位。");

                WindowChrome.Apply(window, WindowChromeKind.Main, ChromePlatform.Windows);
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(windowButtons.All(button => button.IsVisible));
                Assert.IsFalse(spacer.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void TitleBarHeight_FollowsTheSystemTitleBar_OnlyWhereTrafficLightsShow()
    {
        // 红绿灯按系统标题栏垂直居中、位置改不了:标题栏跟系统标题栏同高,两者才同一条中线。
        Assert.AreEqual(28d, WindowChrome.TitleBarHeightFor(trafficLights: true, systemTitleBarHeight: 28, current: double.NaN));
        Assert.AreEqual(32d, WindowChrome.TitleBarHeightFor(trafficLights: true, systemTitleBarHeight: 32, current: 28), "不写死 28,系统给多高就多高。");
        // 全屏时系统标题栏收起、高度报 0:保持原高度,进出全屏时内容不上下跳;还没设过就用样式。
        Assert.AreEqual(32d, WindowChrome.TitleBarHeightFor(trafficLights: true, systemTitleBarHeight: 0, current: 32));
        Assert.IsNull(WindowChrome.TitleBarHeightFor(trafficLights: true, systemTitleBarHeight: 0, current: double.NaN));
        // 没有红绿灯(Windows / Linux、以及 macOS 上的对话框):不设本地值,用样式给的统一高度。
        Assert.IsNull(WindowChrome.TitleBarHeightFor(trafficLights: false, systemTitleBarHeight: 28, current: 28));
    }

    [TestMethod]
    public void MainWindow_ResizeGripsYieldToSystemFrame()
    {
        OnUi(() =>
        {
            var window = new MainWindow { DataContext = new MainWindowViewModel(Substitute.For<IConnectionWorkflowService>()) };
            Panel grips = window.FindControl<Panel>("ResizeGrips")!;
            bool systemFrame = WindowChrome.PlatformOf(window) == ChromePlatform.MacOS;
            Assert.AreEqual(!systemFrame, grips.IsVisible, "构造时按本机平台决定抓取区。");

            WindowChrome.Apply(window, WindowChromeKind.Main, ChromePlatform.MacOS);
            Assert.IsFalse(grips.IsVisible, "macOS 的边缘缩放由系统外框提供。");

            // 主窗口在 Wayland 上是 None,没有装饰层,仍用自绘的抓取区。
            WindowChrome.Apply(window, WindowChromeKind.Main, ChromePlatform.LinuxWayland);
            Assert.IsTrue(grips.IsVisible);

            WindowChrome.Apply(window, WindowChromeKind.Main, ChromePlatform.Windows);
            Assert.IsTrue(grips.IsVisible);
            window.WindowState = WindowState.Maximized;
            Assert.IsFalse(grips.IsVisible, "最大化时抓取区让位。");
            window.WindowState = WindowState.Normal;
            Assert.IsTrue(grips.IsVisible);
        });
    }

    [TestMethod]
    public void Detect_MatchesTheRunningPlatform()
    {
        OnUi(() =>
        {
            ChromePlatform detected = WindowChrome.Detect(new Window());
            if (OperatingSystem.IsWindows())
            {
                Assert.AreEqual(ChromePlatform.Windows, detected);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Assert.AreEqual(ChromePlatform.MacOS, detected);
            }
            else
            {
                // 无头模式既没有 X11 句柄也不是 Wayland 后端:只要没被当成别的操作系统即可。
                Assert.IsTrue(detected is ChromePlatform.LinuxX11 or ChromePlatform.LinuxWayland);
            }
        });
    }

    private static void AssertCardWindow(
        WindowChromeKind kind,
        ChromePlatform platform,
        WindowDecorations decorations,
        bool extend,
        bool transparent)
    {
        OnUi(() =>
        {
            // 先按另一个平台装一遍,确认重复调用会把上一次的设置全部换掉(尤其是不透明底色的资源绑定)。
            var window = new Window();
            WindowChrome.Apply(window, kind, transparent ? ChromePlatform.MacOS : ChromePlatform.Windows);
            WindowChrome.Apply(window, kind, platform);

            string label = $"{kind}/{platform}";
            Assert.AreEqual(decorations, window.WindowDecorations, label);
            Assert.AreEqual(extend, window.ExtendClientAreaToDecorationsHint, label);
            WindowTransparencyLevel level = transparent ? WindowTransparencyLevel.Transparent : WindowTransparencyLevel.None;
            Assert.AreSequenceEqual([level], [.. window.TransparencyLevelHint], message: label);
            if (transparent)
            {
                Assert.AreSame(Brushes.Transparent, window.Background, label);
            }
            else
            {
                Assert.IsTrue(window.TryFindResource("VelaBgSurface", window.ActualThemeVariant, out object? surface), label);
                Assert.AreSame(surface, window.Background, $"{label}:不透明窗口的底色要走令牌");
            }
            if (platform == ChromePlatform.LinuxWayland)
            {
                Assert.IsTrue(window.TryFindResource(WindowChrome.WaylandDecorationsThemeKey, out object? theme), label);
                Assert.AreSame(theme, window.WindowDecorationsTheme, label);
            }
            else
            {
                Assert.IsNull(window.WindowDecorationsTheme, label);
            }
            AssertSinglePlatformClass(window, platform switch
            {
                ChromePlatform.MacOS => "chrome-macos",
                ChromePlatform.LinuxWayland => "chrome-wayland",
                ChromePlatform.LinuxX11 => "chrome-x11",
                _ => "chrome-windows"
            });
            Assert.AreEqual(platform == ChromePlatform.MacOS && kind == WindowChromeKind.Tool,
                window.Classes.Contains("chrome-traffic-lights"), $"{label}:只有 macOS 的非模态窗口显示红绿灯");
            Assert.AreEqual(platform is ChromePlatform.MacOS or ChromePlatform.LinuxX11,
                window.Classes.Contains("window-card-flat"), $"{label}:macOS / X11 的卡片恒是直角");
            Assert.AreEqual(platform, WindowChrome.PlatformOf(window), label);
        });
    }

    private static void AssertCard(Border card, ChromePlatform platform)
    {
        string label = $"{card.Parent?.GetType().Name}/{platform}";
        switch (platform)
        {
            case ChromePlatform.Windows:
                // 与改造前 XAML 里写死的值一致。
                Assert.AreEqual(new Thickness(16), card.Margin, label);
                Assert.AreEqual(new CornerRadius(8), card.CornerRadius, label);
                Assert.AreEqual(new Thickness(1), card.BorderThickness, label);
                Assert.IsTrue(card.TryFindResource("VelaShadowWindow", card.ActualThemeVariant, out object? shadow), label);
                Assert.AreEqual((BoxShadows)shadow!, card.BoxShadow, label);
                break;
            case ChromePlatform.LinuxWayland:
                // 描边与阴影在装饰层里;卡片只剩描边内侧的圆角,并按圆角裁剪内容。
                Assert.AreEqual(new Thickness(0), card.Margin, label);
                Assert.AreEqual(new CornerRadius(7), card.CornerRadius, label);
                Assert.AreEqual(new Thickness(0), card.BorderThickness, label);
                Assert.AreEqual(0, card.BoxShadow.Count, label);
                Assert.IsTrue(card.ClipToBounds, label);
                break;
            default:
                Assert.AreEqual(new Thickness(0), card.Margin, label);
                Assert.AreEqual(new CornerRadius(0), card.CornerRadius, label);
                Assert.AreEqual(new Thickness(0), card.BorderThickness, label);
                Assert.AreEqual(0, card.BoxShadow.Count, label);
                break;
        }
    }

    private static void AssertSinglePlatformClass(Window window, string expected)
    {
        string[] platformClasses = [.. window.Classes.Where(name => name.StartsWith("chrome-", StringComparison.Ordinal)
            && name != "chrome-traffic-lights")];
        Assert.AreSequenceEqual([expected], platformClasses);
    }

    private static void AssertSize(Window window, double width, double height, double minWidth, double minHeight)
    {
        Assert.AreEqual(width, window.Width);
        Assert.AreEqual(height, window.Height);
        Assert.AreEqual(minWidth, window.MinWidth);
        Assert.AreEqual(minHeight, window.MinHeight);
    }

    private static object? SetterValue(ControlTheme theme, AvaloniaProperty property) =>
        theme.Setters.OfType<Setter>().Single(setter => setter.Property == property).Value;

    private static void OnUi(Action action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
