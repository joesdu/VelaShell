using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Behaviors;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("ConnectionProfileUi")]
public sealed class ConnectionProfileViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ConnectionProfileViewUiTests).Assembly);

    [TestMethod]
    public void ProtocolTabs_ExposeFocusableSftpAndFtp_AndKeepLegacyProtocolsDisabled()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "files.example.com",
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var protocolButtons = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("proto-item"))
                .ToList();
            // 协议栏里 SSH / SFTP / FTP 三个内建项;S3、Telnet、串口现在都由插件贡献,
            // 没装插件(单测宿主就是这种)时不出现,「插件」那一组的标题也跟着不出现。
            Assert.HasCount(3, protocolButtons);
            Assert.IsTrue(protocolButtons.All(button => button.IsTabStop));
            AssertProtocolTabHoverIsStable(protocolButtons);
            Assert.IsFalse(vm.HasPluginProtocols);

            // 禁用的占位项一个都不该剩下:最后一个(串口)已由 velashell.serial 插件接管,
            // 宿主至此不再认识任何一种具体协议。
            var legacyProtocols = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("proto-item"))
                .ToList();
            Assert.IsEmpty(legacyProtocols);

            TextBox passwordBox = window.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(SecurePasswordBox.GetEnabled);
            Assert.IsTrue(EnglishInputLocale.GetEnabled(passwordBox));
            Assert.IsFalse(InputMethod.GetIsInputMethodEnabled(passwordBox));

            vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(protocolButtons.Single(button => button.Classes.Contains("selected")).IsEffectivelyVisible);
            Assert.IsTrue(vm.IsSftpSelected);

            // 切到 FTP:仍然只有一个页签处于选中态,且端口跟着切到 21(原本是 SSH 的 22)。
            vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(vm.IsFtpSelected);
            Assert.HasCount(1, protocolButtons.Where(button => button.Classes.Contains("selected")));
            Assert.AreEqual(21, vm.Port);
            Assert.IsFalse(vm.RequiresSshAuth);

            // 没有插件协议时,页签集合里不该凭空多出什么。
            Assert.IsEmpty(vm.PluginProtocols);
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void CopyErrorButton_AppearsOnlyWithAnError_AndSwitchesToCopiedState()
    {
        _session.Dispatch(() =>
        {
            IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
            workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                    .Returns(new ConnectionTestResult(false, "Permission denied (publickey,password)."));
            var vm = new ConnectionProfileViewModel(connectionWorkflowService: workflow)
            {
                Host = "prod.example.com",
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret")
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Button copyButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Name == "CopyErrorButton");
            // 没出错时反馈条整条不占位置,复制按钮自然也不该露出来。
            Assert.IsFalse(copyButton.IsEffectivelyVisible);

            vm.TestConnectionCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(copyButton.IsEffectivelyVisible, "测试失败后错误信息旁必须出现复制按钮。");

            // 视图在 Opened 里把剪贴板回调注入了 VM;headless 下换成探针,
            // 断言点击真的走到剪贴板这一步(而不是命令灰着、按了没反应)。
            string? copied = null;
            vm.CopyToClipboard = text =>
            {
                copied = text;
                return Task.CompletedTask;
            };
            copyButton.Command?.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("Permission denied (publickey,password).", copied);
            Assert.IsTrue(vm.ErrorCopied);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SectionTabIndicator_SlidesToSelectedSection()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "files.example.com",
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border indicator = window.FindControl<Border>("SectionTabIndicator")
                ?? throw new AssertFailedException("SectionTabIndicator not found.");
            Button generalTab = window.FindControl<Button>("GeneralTab")!;
            Button forwardingTab = window.FindControl<Button>("ForwardingTab")!;

            foreach (Button tab in window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("section-tab")))
            {
                Assert.IsTrue(tab.Transitions is null || tab.Transitions.Count == 0,
                    "分页 tab 不应在 Button 本体上启动悬停过渡");
            }

            // 初始:下划线对齐「常规」。
            Assert.IsTrue(indicator.IsVisible);
            AssertIndicatorAligned(indicator, generalTab);

            // 切到「转发」:下划线经 180ms 过渡滑过去(断言读基值,与动画时间解耦)。
            vm.SelectSectionCommand.Execute(ConnectionProfileSection.Forwarding).Subscribe();
            Dispatcher.UIThread.RunJobs();
            AssertIndicatorAligned(indicator, forwardingTab);

            // 换成 FTP:「转发」页没了,落回「常规」,下划线也得跟回来 ——
            // 否则它悬在一个已经收起的页签原来的位置上。
            vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsFalse(forwardingTab.IsVisible);
            AssertIndicatorAligned(indicator, generalTab);

            // FTP 有「高级」页(默认打开路径)。
            Button advancedTab = window.FindControl<Button>("AdvancedTab")!;
            Assert.IsTrue(advancedTab.IsVisible);
            vm.SelectSectionCommand.Execute(ConnectionProfileSection.Advanced).Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertIndicatorAligned(indicator, advancedTab);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 每一页只露出自己的内容:切到「终端」时,「常规」页的主机输入框不该还在,
    /// 反过来也一样 —— 两页叠着画出来,就是一张两倍长、字段重复的表单。
    /// </summary>
    [TestMethod]
    public void OnlyTheSelectedSectionIsShown()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel { Host = "10.0.0.1", Username = "root", PostAuthCommand = "tmux attach" };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                TextBox Showing(string text) => window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Text == text);

                Assert.IsTrue(Showing("10.0.0.1").IsEffectivelyVisible);
                Assert.IsFalse(Showing("tmux attach").IsEffectivelyVisible);

                vm.SelectSectionCommand.Execute(ConnectionProfileSection.Terminal).Subscribe();
                Dispatcher.UIThread.RunJobs();

                Assert.IsFalse(Showing("10.0.0.1").IsEffectivelyVisible);
                Assert.IsTrue(Showing("tmux attach").IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void Form_ScrollsAndKeepsFooterReachable_WhenWindowHeightIsCapped()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "s3.amazonaws.com",
                Username = "AKIA",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            // 构造时就按屏幕工作区钳过高度:等到 Opened 再钳,用户会先看见一个高过屏幕的窗口。
            Assert.IsTrue(double.IsFinite(window.MaxHeight), "窗口高度上限应在显示前就设好。");
            // 屏幕再高也不越过设计上限(与设置窗口 768 对齐)——「能放下」不等于「该放这么高」。
            Assert.IsLessThanOrEqualTo(768d, window.MaxHeight, "大屏上应按设计上限钳住,而不是任其长到屏幕高度。");
            window.Show();
            // 模拟一块矮屏(Opened 里会按真实屏幕重新钳一次,所以只能在显示之后压):
            // 插件协议字段一多,原来的 StackPanel 会一路长到屏幕外,底部的保存/连接按钮
            // 点不到 —— 现在超出的部分必须由表单区自己滚。
            window.MaxHeight = 320;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.IsLessThanOrEqualTo(320.5, window.Bounds.Height, "窗口高度不得越过上限。");

            ScrollViewer form = window.FindControl<ScrollViewer>("FormScroll")!;
            Assert.IsGreaterThan(form.Viewport.Height, form.Extent.Height, "表单放不下时必须可滚动。");

            // 页脚是定高行,不参与滚动:连接按钮永远落在窗口里。
            Button connect = window.FindControl<Button>("ConnectButton")!;
            Point origin = connect.TranslatePoint(default, window) ?? default;
            Assert.IsLessThanOrEqualTo(window.Bounds.Height + 0.5, origin.Y + connect.Bounds.Height,
                "「连接」按钮必须留在窗口可视区域内。");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 新建连接一打开,表单右侧不能顶着一条已经展开的粗滚动条。
    /// </summary>
    /// <remarks>
    /// 这里原先写死了 <c>ScrollViewer.AllowAutoHide="False"</c>,本意是"表单被高度上限截断时
    /// 别让人以为字段没了";但这个开关在 Avalonia 里的含义是【常驻展开】
    /// (<c>ScrollBar.UpdateIsExpandedState</c>:不许自动隐藏就是 <c>IsExpanded=true</c>),
    /// 于是 Redis 这类字段多的协议一打开,滚动条就是滑道 + 两端箭头的完全展开态,
    /// 和全应用其它地方对不上。未激活态本就是一根可见的 2px 细条(见 ScrollBarStyleTests),
    /// 截断照样看得见,affordance 并不靠常驻展开来给。
    /// </remarks>
    [TestMethod]
    public void FormScrollBar_StartsCollapsed_EvenWhenTheFormIsTruncated()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel { Host = "127.0.0.1" };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            // 同 Form_Scrolls… 那条:压一块矮屏,逼出真正需要滚动的表单。
            window.MaxHeight = 320;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            ScrollViewer form = window.FindControl<ScrollViewer>("FormScroll")!;
            Assert.IsGreaterThan(form.Viewport.Height, form.Extent.Height, "前提:表单确实放不下。");
            Assert.IsTrue(form.AllowAutoHide, "表单不该按住 AllowAutoHide —— 那等于让滚动条常驻展开。");

            ScrollBar vertical = form.GetVisualDescendants().OfType<ScrollBar>()
                .Single(bar => ReferenceEquals(bar.TemplatedParent, form) &&
                               bar.Orientation == Orientation.Vertical);
            Assert.IsFalse(vertical.IsExpanded, "鼠标还没碰上去就展开了:一进来看到的就是粗滚动条。");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 编辑一条存了口令的连接:对话框一打开,表单停在最上面,不被口令框拽下去。
    /// </summary>
    /// <remarks>
    /// 文本框被程序赋值时光标跟着挪,<c>TextPresenter</c> 随后发一次 BringIntoView ——
    /// 不管它有没有焦点。口令框落在首屏以下时(矮屏,或插件字段多的协议),
    /// 外层的表单区就被它滚下去一百来像素,连接名一栏打开就看不见。
    /// </remarks>
    [TestMethod]
    public void OpeningWithASavedPassword_LeavesTheFormAtTheTop()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "db.example.com",
                Username = "ops",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            window.MaxHeight = 320;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            ScrollViewer form = window.FindControl<ScrollViewer>("FormScroll")!;
            TextBox password = window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => SecurePasswordBox.GetEnabled(box) && box.IsEffectivelyVisible);
            Point top = password.TranslatePoint(default, form) ?? default;
            Assert.IsGreaterThan(form.Viewport.Height, top.Y + form.Offset.Y, "前提:口令框在首屏以下。");
            Assert.AreEqual(0d, form.Offset.Y, "没获得焦点的文本框不该把表单滚走。");

            // 用户自己把焦点放进去(Tab 或点击)时照常滚到它。
            password.Focus();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsGreaterThan(0d, form.Offset.Y, "获得焦点的文本框仍然要被带进视野。");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 备注框(#549)是多行的:回车在框里换行,不会把对话框提交掉;敲进去的内容回到视图模型。
    /// </summary>
    [TestMethod]
    public void NotesBox_EnterStartsANewLine_AndDoesNotSubmitTheDialog()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel { Host = "10.0.0.1", Username = "ops", Notes = "跳板机" };
            bool submitted = false;
            vm.SaveCommand.Subscribe(_ => submitted = true);
            vm.ConnectCommand.Subscribe(_ => submitted = true);
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                TextBox notes = window.FindControl<TextBox>("NotesBox")!;
                Assert.IsTrue(notes.IsEffectivelyVisible, "备注在「常规」页,对话框一打开就看得见。");
                Assert.AreEqual("跳板机", notes.Text);

                notes.Focus();
                notes.CaretIndex = notes.Text!.Length;
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyTextInput("值班:运维二组");
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual("跳板机" + notes.NewLine + "值班:运维二组", vm.Notes);
                Assert.IsFalse(submitted, "回车只是换行,不该保存或连接。");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 可编辑下拉在用户选中一项之后,文本框里必须是**落盘值**。
    /// <para>
    /// 串口的端口下拉整个设计都压在这一条上:列表里显示的是
    /// "USB-SERIAL CH340 (COM3)",而绑到 <c>Host</c> 上、接下来要拿去打开端口的必须是
    /// <c>COM3</c>。Avalonia 的可编辑 ComboBox 填文本框时用的是 <c>item.ToString()</c> ——
    /// 所以宿主侧包了一层 <see cref="PluginChoiceItem" /> 把 <c>ToString</c> 定成落盘值。
    /// 这条用例钉的就是"那个假设成立"。直接把 SDK 的 record 丢进去,存下来的会是
    /// <c>ProtocolSettingChoice { Value = COM3, … }</c>。
    /// </para>
    /// </summary>
    [TestMethod]
    public void EditableComboBox_PutsTheStoredValueInTheTextBox_NotTheDisplayLabel()
    {
        _session.Dispatch(() =>
        {
            var combo = new ComboBox
            {
                IsEditable = true,
                ItemsSource = new[]
                {
                    new PluginChoiceItem("COM3", "USB-SERIAL CH340 (COM3)"),
                    new PluginChoiceItem("COM7", "Prolific USB-to-Serial (COM7)")
                }
            };
            var window = new Window { Content = combo, Width = 320, Height = 120 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            combo.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual("COM7", combo.Text);

            // 反向:填一个列表里没有的设备(适配器没插、容器里映射进来的口)照旧留得住。
            combo.Text = "/dev/ttyS10";
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("/dev/ttyS10", combo.Text);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>标题栏与回放中心同一个样子(同导入会话)。</summary>
    [TestMethod]
    public void TitleBar_FollowsTheDialogTitleBarSpec()
    {
        _session.Dispatch(() =>
        {
            var window = new ConnectionProfileView { DataContext = new ConnectionProfileViewModel() };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                DialogTitleBarAssert.FollowsSpec(window);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>悬停关闭键的红底被卡片的圆角裁掉,不伸出窗口外。</summary>
    [TestMethod]
    public void CloseHover_StaysInsideTheRoundedCorner()
    {
        _session.Dispatch(() =>
        {
            var window = new ConnectionProfileView { DataContext = new ConnectionProfileViewModel() };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                DialogTitleBarAssert.CloseHoverStaysInsideTheRoundedCorner(window);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 勾上 X11 转发后,「受信任」勾选框与本机 X 显示的输入框在同一条中线上。
    /// </summary>
    /// <remarks>
    /// 原先勾选框靠「贴底 + 底边距 8」去凑输入框的中线,凑出来高了一截;框高一变(字号、主题)还会再歪。
    /// </remarks>
    [TestMethod]
    public void X11Trusted_SharesTheDisplayBoxCentreLine()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel { SshX11Forwarding = true, SelectedSection = ConnectionProfileSection.Forwarding };
            var window = new ConnectionProfileView { DataContext = vm, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                CheckBox trusted = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "X11TrustedCheck");
                Grid row = trusted.GetVisualAncestors().OfType<Grid>().First();
                TextBox display = row.Children.OfType<TextBox>().Single();
                Assert.IsTrue(trusted.IsEffectivelyVisible);

                double CentreY(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height / 2), row)!.Value.Y;
                Assert.AreEqual(CentreY(display), CentreY(trusted), 0.5, "受信任勾选框应与输入框同一条中线");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void AssertIndicatorAligned(Border indicator, Button tab)
    {
        // 读基值(过渡目标)而非属性现值:现值在 180ms 滑动期间是动画中间值。
        Visual panel = indicator.GetVisualParent()!;
        Point origin = tab.TranslatePoint(default, panel) ?? default;
        double actualX = indicator.GetBaseValue(Visual.RenderTransformProperty).GetValueOrDefault()?.Value.M31 ?? -1;
        double actualWidth = indicator.GetBaseValue(Layoutable.WidthProperty).GetValueOrDefault(double.NaN);
        Assert.AreEqual(Math.Round(origin.X), actualX, 0.6, "下划线应与选中协议标签左缘对齐。");
        Assert.AreEqual(Math.Round(tab.Bounds.Width), actualWidth, 0.6, "下划线宽度应等于选中协议标签宽度。");
    }

    /// <summary>
    /// Tab 悬停只改变稳定的画刷值，不在 Button 本体上启动过渡。
    /// Windows 不同缩放比例下，按钮前景又被图标绑定时，过渡会让悬停命中区域出现闪烁。
    /// </summary>
    private static void AssertProtocolTabHoverIsStable(IReadOnlyList<Button> protocolButtons)
    {
        foreach (Button button in protocolButtons)
        {
            Assert.IsTrue(button.Transitions is null || button.Transitions.Count == 0,
                "协议 tab 不应在 Button 本体上启动悬停过渡");
        }
    }
}
