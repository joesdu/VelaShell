using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Localization;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>隧道面板与帮助窗口的真实 Avalonia 布局、主题和截图回归测试。</summary>
[TestClass]
[TestCategory("TunnelUI")]
public sealed class TunnelPanelUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TunnelPanelUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void Panel_HelpAndFormActions_FollowTheLayoutContract()
    {
        OnUi(() =>
        {
            ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");
            try
            {
                var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>());
                // 视觉 QA 的样本要带上统计行与「自动」徽标 —— 行内最小的那几档字号正长在这两处,
                // 截图里没有它们,字号回归就看不出来。
                vm.Tunnels.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = new()
                    {
                        Type = TunnelType.LocalForward,
                        Name = string.Empty,
                        LocalHost = "127.0.0.1",
                        LocalPort = 5432,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 5432,
                        AutoReconnect = true
                    },
                    Status = TunnelStatus.Active,
                    SessionId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    BytesTransferred = 1_468_006,
                    TotalConnections = 3,
                    ActiveConnections = 2
                }));

                var view = new TunnelPanelView { DataContext = vm };
                var window = new Window { Width = 380, Height = 760, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Button help = view.FindControl<Button>("HelpButton")!;
                StackPanel actions = view.FindControl<StackPanel>("FormActions")!;
                Assert.IsNotNull(help);
                Assert.AreEqual(HorizontalAlignment.Right, actions.HorizontalAlignment);
                Assert.IsGreaterThan(0, help.Bounds.Width);

                SaveFrame(window, "tunnel-panel-dark.png");
                // 亮色也拍一帧:数量徽标原先是强调色实底配强调色字,换了主题才看得出字有没有被吃掉。
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                SaveFrame(window, "tunnel-panel-light.png");
                window.Close();
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = originalTheme;
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                _localization.SetLanguage(previousUiCulture.Name);
            }
        });
    }

    /// <summary>
    /// 表单末尾几行复选框(转发到本机 / 掉线后自动重连 / 程序启动时自动建立)之间只留表单 StackPanel 的 Spacing(设计 tunNewForm 是 8px,
    /// 2026-10-09 面板整体放大后为 10px)。Fluent 的 CheckBox 模板在框上下各塞了 6px 死高
    /// (模板里写死 Height="32",外部样式压不动),不抵消掉就是多出 12px 的行距 ——
    /// 肉眼看就是「自动重连」那行离上面差着一大截。
    /// </summary>
    [TestMethod]
    public void Panel_FormCheckBoxes_KeepOnlyTheFormSpacing()
    {
        OnUi(() =>
        {
            var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>());
            var view = new TunnelPanelView { DataContext = vm };
            var window = new Window { Width = 380, Height = 760, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            CheckBox[] boxes = [.. view.GetVisualDescendants().OfType<CheckBox>()];
            Assert.HasCount(3, boxes, "表单里应当是「转发到本机」「自动重连」「程序启动时自动建立」三个复选框。");

            // 复选框自身的布局高度,而不是模板撑出来的 32。
            foreach (CheckBox box in boxes)
            {
                Assert.AreEqual(20d, box.DesiredSize.Height, 0.5,
                    "复选框应当只占勾选框本身的高度,模板多出来的 12px 死高要被抵消掉。");
            }

            for (int i = 1; i < boxes.Length; i++)
            {
                double gap = boxes[i].TranslatePoint(new(0, 0), window)!.Value.Y
                             - boxes[i - 1].TranslatePoint(new(0, 0), window)!.Value.Y
                             - boxes[i - 1].DesiredSize.Height;
                Assert.AreEqual(10d, gap, 0.5, $"第 {i} 与第 {i + 1} 行复选框之间应当就是表单 StackPanel 的 Spacing=10。");
            }

            window.Close();
        });
    }

    /// <summary>
    /// 名字很长、三枚徽标(类型 / 自动 / 自启)全亮、又是徽标最宽的英文时,截断的应当是名字:
    /// 最右那枚徽标要完整地停在编辑键左边,不能被行尾的操作键压住或裁掉。
    /// </summary>
    [TestMethod]
    public void Panel_LongNameWithAllBadges_TrimsTheNameNotTheBadges()
    {
        OnUi(() =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            _localization.SetLanguage("en-US");
            try
            {
                var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>());
                vm.Tunnels.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = new()
                    {
                        Type = TunnelType.RemoteForward,
                        Name = "production-postgres-primary-replica-forward",
                        LocalHost = "127.0.0.1",
                        LocalPort = 5432,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 5432,
                        AutoReconnect = true,
                        AutoStart = true
                    },
                    Status = TunnelStatus.Stopped,
                    SessionId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow
                }));
                var view = new TunnelPanelView { DataContext = vm };
                var window = new Window { Width = 420, Height = 760, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                TextBlock startupBadge = view.GetVisualDescendants().OfType<TextBlock>()
                                             .Single(t => t.Text == Strings.Get("Tunnel_AutoStartBadge"));
                Button edit = view.GetVisualDescendants().OfType<Button>()
                                  .Single(b => Equals(ToolTip.GetTip(b), Strings.Get("Tunnel_EditTip")));
                TextBlock name = view.GetVisualDescendants().OfType<TextBlock>()
                                     .Single(t => t.Text == "production-postgres-primary-replica-forward");
                double badgeRight = startupBadge.TranslatePoint(new(startupBadge.Bounds.Width, 0), window)!.Value.X;
                double editLeft = edit.TranslatePoint(new(0, 0), window)!.Value.X;

                Assert.IsTrue(startupBadge.IsEffectivelyVisible);
                Assert.IsLessThanOrEqualTo(editLeft, badgeRight, "最右那枚徽标应当完整地停在编辑键左边。");
                var natural = new TextBlock
                {
                    Text = name.Text,
                    FontFamily = name.FontFamily,
                    FontSize = name.FontSize,
                    FontWeight = name.FontWeight
                };
                natural.Measure(Size.Infinity);
                Assert.IsLessThan(natural.DesiredSize.Width, name.Bounds.Width, "名字应当被截断而不是把徽标挤出去。");
                Assert.IsGreaterThan(40, name.Bounds.Width, "名字至少还要露出一截。");
                SaveFrame(window, "tunnel-panel-long-name-en.png");
                window.Close();
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                _localization.SetLanguage(previousUiCulture.Name);
            }
        });
    }

    /// <summary>
    /// 开着隧道关掉面板、再打开且仍落在同一台服务器时,列表要原样在。宿主关面板只是把它藏起来,
    /// 下拉框的绑定一直活着:重开时刷新服务器列表的 <c>Servers.Clear()</c> 会让下拉框把选中项
    /// 回写成 null,视图模型若照单全收,就把条目换成了空集合,而随后选回同一台服务器又被当成
    /// 「没变」跳过 —— 用户得切到别的服务器再切回来才看得到隧道。
    /// </summary>
    [TestMethod]
    public void Panel_Reopen_KeepsTheSelectedServersTunnels()
    {
        OnUi(() =>
        {
            var serverId = Guid.NewGuid();
            // 每次都返回新取的实例,与 SessionRepository.GetAllSessionsAsync 的行为一致。
            var vm = new TunnelPanelViewModel(
                Substitute.For<ITunnelWorkflowService>(),
                () => Task.FromResult<IReadOnlyList<SessionProfile>>(
                    [new() { Id = serverId, Name = "srv", Host = "10.0.0.1", Username = "root" }]));
            var view = new TunnelPanelView { DataContext = vm };
            var host = new Panel { Children = { view } };
            var window = new Window { Width = 420, Height = 760, Content = host };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.IsTrue(vm.OpenAsync(serverId).IsCompleted);
            Dispatcher.UIThread.RunJobs();
            vm.Tunnels.Add(new(new TunnelInfo
            {
                Id = Guid.NewGuid(),
                Config = new()
                {
                    Type = TunnelType.LocalForward,
                    Name = "pg",
                    LocalHost = "127.0.0.1",
                    LocalPort = 5432,
                    RemoteHost = "127.0.0.1",
                    RemotePort = 5432
                },
                Status = TunnelStatus.Active,
                SessionId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow
            }));

            // 关面板(宿主把它藏起来)再打开,预选的还是这台服务器。
            host.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(vm.OpenAsync(serverId).IsCompleted);
            host.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.AreEqual(serverId, vm.SelectedServer?.Id);
            Assert.HasCount(1, vm.Tunnels, "重开面板后,同一台服务器上已开的隧道应当直接列出来。");
            ComboBox servers = view.GetVisualDescendants().OfType<ComboBox>().First();
            Assert.AreSame(vm.SelectedServer, servers.SelectedItem, "下拉框应当回到这台服务器上。");
            window.Close();
        });
    }

    /// <summary>
    /// 启停途中,那一格的启停键换成一圈转着的环(不是按钮、点不动),编辑与删除也点不动;
    /// 走完换回对应的键。
    /// </summary>
    [TestMethod]
    public void Panel_BusyTunnel_SwapsStartStopForASpinner()
    {
        OnUi(() =>
        {
            var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>());
            var tunnel = new TunnelItemViewModel(new TunnelInfo
            {
                Id = Guid.NewGuid(),
                Config = new()
                {
                    Type = TunnelType.LocalForward,
                    Name = "pg",
                    LocalHost = "127.0.0.1",
                    LocalPort = 5432,
                    RemoteHost = "127.0.0.1",
                    RemotePort = 5432
                },
                Status = TunnelStatus.Stopped,
                SessionId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow
            });
            vm.Tunnels.Add(tunnel);
            var view = new TunnelPanelView { DataContext = vm };
            var window = new Window { Width = 420, Height = 760, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            tunnel.IsStarting = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border spinner = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BusyIndicator");
            Assert.IsTrue(spinner.IsVisible, "启动途中应当显示转圈。");
            Assert.IsEmpty(RowButtons(view, "Tunnel_StartTip", "Tunnel_StopTip").Where(b => b.IsVisible),
                "启动途中启停键都该收起来,不能再点。");
            Assert.IsFalse(RowButtons(view, "Tunnel_DeleteTip").Single().IsEnabled, "启停途中不该能删。");
            Assert.AreEqual(Strings.Get("Tunnel_Starting"), ToolTip.GetTip(spinner));
            SaveFrame(window, "tunnel-panel-busy.png");

            tunnel.IsStarting = false;
            Dispatcher.UIThread.RunJobs();

            Assert.IsFalse(spinner.IsVisible);
            Assert.IsTrue(RowButtons(view, "Tunnel_StartTip").Single().IsVisible, "走完换回「启动」键。");
            Assert.IsTrue(RowButtons(view, "Tunnel_DeleteTip").Single().IsEnabled);
            window.Close();
        });

        static IEnumerable<Button> RowButtons(TunnelPanelView view, params string[] tipKeys) =>
            view.GetVisualDescendants().OfType<Button>()
                .Where(b => ToolTip.GetTip(b) is string tip && tipKeys.Any(key => tip == Strings.Get(key)));
    }

    /// <summary>
    /// 拖标题栏挪面板:松手时位置落盘(下次打开原地出现);按在关闭键上不起拖;
    /// 拖出窗口的部分被夹回来,面板始终整块留在可视区里。
    /// </summary>
    [TestMethod]
    public void Panel_DragTitleBar_MovesPanelAndPersistsPosition()
    {
        OnUi(() =>
        {
            IAppDataStore store = Substitute.For<IAppDataStore>();
            var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>(), dataStore: store);
            // 与 MainWindow 一样:外层铺满,对齐与边距落在视图上(拖拽的参考系就是这个父容器)。
            var view = new TunnelPanelView
            {
                DataContext = vm,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new(0, 8, 48, 0)
            };
            var window = new Window { Width = 1200, Height = 800, Content = new Panel { Children = { view } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border handle = view.FindControl<Border>("DragHandle")!;
            Point grab = handle.TranslatePoint(new(80, handle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(grab, MouseButton.Left);
            window.MouseMove(grab + new Vector(-500, 120));
            window.MouseUp(grab + new Vector(-500, 120), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(-500, vm.PanelOffsetX, 0.5);
            Assert.AreEqual(120, vm.PanelOffsetY, 0.5);
            _ = store.Received(1).UpsertAsync("ui-layout", "tunnel-panel",
                Arg.Is<PanelPosition>(p => p.OffsetX == vm.PanelOffsetX && p.OffsetY == vm.PanelOffsetY),
                Arg.Any<CancellationToken>());

            // 按在关闭键上:那是点按钮,不是拖面板。
            Button close = view.GetVisualDescendants().OfType<Button>()
                               .Single(b => Equals(ToolTip.GetTip(b), Strings.Get("Tunnel_ClosePanel")));
            Point onClose = close.TranslatePoint(new(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            window.MouseDown(onClose, MouseButton.Left);
            window.MouseMove(onClose + new Vector(-100, 100));
            window.MouseUp(onClose + new Vector(-100, 100), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(-500, vm.PanelOffsetX, 0.5, "按在关闭键上不该拖动面板。");

            // 往左上甩出窗口:夹回到贴着窗口左上角,不会跑到看不见的地方。
            grab = handle.TranslatePoint(new(80, handle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(grab, MouseButton.Left);
            window.MouseMove(grab + new Vector(-5000, -5000));
            window.MouseUp(grab + new Vector(-5000, -5000), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Point topLeft = view.TranslatePoint(new(0, 0), window)!.Value;
            Assert.AreEqual(0, topLeft.X, 0.5, "面板左缘应被夹在窗口内。");
            Assert.AreEqual(0, topLeft.Y, 0.5, "面板上缘应被夹在窗口内。");
            window.Close();
        });
    }

    [TestMethod]
    public void Panel_EditButtons_ReflectTunnelStatusAndTooltip()
    {
        OnUi(() =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>());
                vm.Tunnels.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = new()
                    {
                        Type = TunnelType.LocalForward,
                        Name = "active",
                        LocalHost = "127.0.0.1",
                        LocalPort = 5432,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 5432
                    },
                    Status = TunnelStatus.Active,
                    SessionId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow
                }));
                vm.Tunnels.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = new()
                    {
                        Type = TunnelType.LocalForward,
                        Name = "stopped",
                        LocalHost = "127.0.0.1",
                        LocalPort = 5433,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 5433
                    },
                    Status = TunnelStatus.Stopped,
                    SessionId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow
                }));

                var view = new TunnelPanelView { DataContext = vm };
                var window = new Window { Width = 380, Height = 760, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Button[] editButtons = [.. view.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button => ToolTip.GetTip(button) is string tip
                        && (string.Equals(tip, Strings.Get("Tunnel_EditTip"))
                            || string.Equals(tip, Strings.Get("Tunnel_EditDisabledTip"))))];

                Assert.HasCount(2, editButtons);
                Assert.IsFalse(editButtons.Single(button => Equals(ToolTip.GetTip(button), Strings.Get("Tunnel_EditDisabledTip"))).IsEnabled);
                Assert.IsTrue(editButtons.Single(button => Equals(ToolTip.GetTip(button), Strings.Get("Tunnel_EditTip"))).IsEnabled);
                window.Close();
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        });
    }

    [TestMethod]
    public void HelpDialog_UsesOwnedDialogChrome_AndRendersThemesAndLocales()
    {
        OnUi(() =>
        {
            ThemeVariant original = Application.Current!.RequestedThemeVariant;
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                RenderHelpDialog("en-US", ThemeVariant.Dark, "tunnel-help-en-dark.png");
                RenderHelpDialog("zh-CN", ThemeVariant.Dark, "tunnel-help-zh-Hans-dark.png");
                RenderHelpDialog("zh-CN", ThemeVariant.Light, "tunnel-help-zh-Hans-light.png");
                RenderHelpDialog("zh-TW", ThemeVariant.Dark, "tunnel-help-zh-Hant-dark.png");
                RenderHelpDialog("ja-JP", ThemeVariant.Dark, "tunnel-help-ja-dark.png");
                RenderHelpDialog("ko-KR", ThemeVariant.Dark, "tunnel-help-ko-dark.png");
            }
            finally
            {
                Application.Current.RequestedThemeVariant = original;
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                _localization.SetLanguage(previousUiCulture.Name);
            }
        });
    }

    private static void RenderHelpDialog(string cultureName, ThemeVariant theme, string fileName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        _localization.SetLanguage(cultureName);
        Application.Current!.RequestedThemeVariant = theme;
        var dialog = new TunnelHelpDialog();
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();

        // 外框按平台装(WindowChrome;各平台取值由 WindowChromeTests 钉住):macOS 与 Wayland 的对话框
        // 用系统 / 装饰层的 BorderOnly,Windows 与 X11 是自绘卡片、无系统装饰。
        ChromePlatform platform = WindowChrome.PlatformOf(dialog)!.Value;
        WindowDecorations expected = platform is ChromePlatform.MacOS or ChromePlatform.LinuxWayland
            ? WindowDecorations.BorderOnly
            : WindowDecorations.None;
        Assert.AreEqual(expected, dialog.WindowDecorations, platform.ToString());
        Assert.IsFalse(dialog.ShowInTaskbar);
        Assert.IsFalse(dialog.CanResize);
        Assert.AreEqual(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
        Assert.IsGreaterThanOrEqualTo(9, dialog.GetVisualDescendants().OfType<TextBlock>().Count());

        SaveFrame(dialog, fileName);
        dialog.Close();
    }

    private static void SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
