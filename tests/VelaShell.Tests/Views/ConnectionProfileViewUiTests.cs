using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Behaviors;
using VelaShell.Controls.Controls;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
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

    [TestMethod]
    public void TailscaleSelector_UsesFullWidthBelowUsername_AndHidesAuthenticationMaterial()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "node.example.com",
                Username = "ops",
                Password = SecureStringConvert.FromPlaintext("stale-password"),
                PrivateKeyPath = "/stale-key"
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Button tailscale = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.CommandParameter, AuthMethod.Tailscale));
                TextBox username = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Text == "ops");
                Assert.IsTrue(tailscale.IsEffectivelyVisible);
                Assert.IsTrue(tailscale.IsTabStop);
                var segments = (Control)tailscale.GetVisualParent()!;
                Assert.IsGreaterThanOrEqualTo(username.Bounds.Width, segments.Bounds.Width, "认证分段应使用用户名下方的全宽行");
                Assert.IsGreaterThanOrEqualTo(username.TranslatePoint(default, window)!.Value.Y + username.Bounds.Height, tailscale.TranslatePoint(default, window)!.Value.Y);

                tailscale.Command!.Execute(tailscale.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(vm.IsTailscaleAuth);
                Assert.IsTrue(username.IsEffectivelyVisible);
                Assert.IsFalse(window.GetVisualDescendants().OfType<TextBox>().Single(SecurePasswordBox.GetEnabled).IsEffectivelyVisible);
                Assert.IsFalse(window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Text == "/stale-key").IsEffectivelyVisible);
                Assert.IsTrue(window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == Strings.Get("Profile_TailscaleAuthHint")).IsEffectivelyVisible);

                vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(tailscale.IsEffectivelyVisible);
                Assert.IsTrue(vm.IsTailscaleAuth);

                vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(tailscale.IsEffectivelyVisible);
                Assert.IsTrue(vm.ShowInlinePassword, "FTP 仍使用既有密码认证布局");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AuthenticationDialog_TailscaleSegment_HidesPasswordAndSharedUpdate()
    {
        _session.Dispatch(() =>
        {
            var vm = new AuthenticationDialogViewModel("node", 22, "ops",
                sharedCredential: new("shared", 2, SharedCredentialPromptReason.Incomplete));
            var window = new AuthenticationDialogView { DataContext = vm };
            window.Show();
            vm.NextCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Button tailscale = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, vm.SelectTailscaleCommand));
                Assert.IsTrue(tailscale.IsEffectivelyVisible);
                Assert.IsTrue(tailscale.IsTabStop);
                tailscale.Command!.Execute(tailscale.CommandParameter);
                Dispatcher.UIThread.RunJobs();

                Assert.IsTrue(vm.IsTailscaleMethod);
                Assert.IsFalse(window.GetVisualDescendants().OfType<TextBox>().Single(SecurePasswordBox.GetEnabled).IsEffectivelyVisible);
                Assert.DoesNotContain(static box => box.IsEffectivelyVisible, window.GetVisualDescendants().OfType<CheckBox>());
                Assert.IsTrue(window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == Strings.Get("Profile_TailscaleAuthHint")).IsEffectivelyVisible);
                Assert.IsTrue(window.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, vm.LoginCommand)).IsEffectivelyEnabled);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ============================== 选类型那一屏(卡片 → 下一步 → 表单) ==============================

    private const string DbId = "test.db";
    private const string DialectKey = "dialect";
    private const string DialectLabel = "Database type";

    private sealed class NoopWorkspace : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// 数据库式的工作台:「类型」选 MySQL(3306)/ PostgreSQL(5432)/ SQLite(文件,无端点无凭据)。
    /// 描述符**不带图标**:卡片要画协议栏同款的通用插头(视图层兜底)。
    /// </summary>
    private static PluginProtocolRegistry DbRegistry(out IDisposable handle)
    {
        var registry = new PluginProtocolRegistry();
        handle = registry.RegisterWorkspace(DbId, new WorkspaceDescriptor
        {
            Id = DbId,
            DisplayName = "Database",
            DefaultPort = 3306,
            Fields =
            [
                new()
                {
                    Key = DialectKey,
                    Label = DialectLabel,
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL"), new("sqlite", "SQLite")]
                },
                new() { Key = "database", Label = "Database name", Kind = ProtocolSettingKind.Text }
            ],
            VariantKey = DialectKey,
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 },
                new()
                {
                    Value = "sqlite",
                    DefaultPort = 1,
                    HostLabel = "Database file",
                    Features = WorkspaceFeatures.NoCredentials | WorkspaceFeatures.NoEndpoint
                }
            ]
        }, new NoopWorkspace());
        return registry;
    }

    private static List<Button> TypeTiles(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("type-tile"))];

    private static string? TileName(Button tile) =>
        tile.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("type-tile-name")).Text;

    private static bool[] TileSelection(Window window) =>
        [.. TypeTiles(window).Select(tile => tile.Classes.Contains("selected"))];

    /// <summary>页脚右侧此刻看得见的按钮,按从左到右的顺序。</summary>
    private static string?[] VisibleFooterButtons(Window window) =>
        [.. window.FindControl<Button>("ConnectButton")!.GetVisualParent<StackPanel>()!.Children
            .OfType<Button>()
            .Where(button => button.IsVisible)
            .Select(button => button.Name)];

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>在控件正中按下松开左键(与用户的手势一样走命中测试);<paramref name="twice" /> 为双击。</summary>
    private static void Click(Window window, Control target, bool twice = false)
    {
        Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new AssertFailedException($"{target} is not in the window.");
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        if (twice)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }
        Layout(window);
    }

    /// <summary>
    /// 设了 <c>VELASHELL_VISUAL_QA_DIR</c> 才把这一帧存成 PNG 给人看(与 DockMotionUiTests 等同一个开关);
    /// 没设时什么也不做,断言不依赖它。
    /// </summary>
    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
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

    /// <summary>
    /// <b>新建连接点进数据库页签:右栏换成一屏卡片,一个候选项一张;页脚只剩 [取消][下一步]</b>(附录 A 第一行)。
    /// <para>
    /// 页签条与表单收起,连接目标预览也收起(还没有端点);默认那一档(MySQL)高亮;
    /// 描述符没给图标时卡片画通用插头;「下一步」是对话框级默认按钮,与「取消」同高。
    /// 单击另一张只换高亮,不离开卡片。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task TypePicker_RendersOneCardPerChoice_AndFooterShowsCancelNext() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            try
            {
                await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
                Layout(window);
                Assert.IsTrue(vm.IsPickingType, "前提:新建连接点进带类型的页签,先停在卡片上。");
                Assert.IsTrue(window.FindControl<ScrollViewer>("TypePickerPanel")!.IsEffectivelyVisible, "卡片面板应出现。");
                Assert.IsFalse(window.FindControl<ScrollViewer>("FormScroll")!.IsEffectivelyVisible, "卡片那一屏不显示表单。");
                Assert.IsFalse(window.FindControl<Button>("GeneralTab")!.IsEffectivelyVisible, "卡片那一屏不显示分页页签条。");
                Assert.IsFalse(window.FindControl<StackPanel>("TypeHeader")!.IsEffectivelyVisible, "还在卡片上,表头不出现。");

                List<Button> tiles = TypeTiles(window);
                Assert.AreSequenceEqual(["MySQL", "PostgreSQL", "SQLite"], tiles.Select(TileName).ToArray(), "一个候选项一张卡片,顺序同候选项。");
                Assert.IsTrue(tiles.All(tile => tile.IsEffectivelyVisible && tile.IsTabStop), "卡片都要看得见、Tab 得到。");
                Assert.IsTrue(tiles.All(tile => tile.Bounds is { Width: 112, Height: 88 }), "磁贴 112×88(DESIGN.md §5.2)。");
                Assert.AreSequenceEqual([true, false, false], TileSelection(window), "默认那一档预选、只有它高亮。");
                Assert.IsTrue(tiles.All(tile => tile.Transitions is null || tile.Transitions.Count == 0),
                    "磁贴不挂画刷过渡(#577)。");
                Geometry? plug = ConnectionIcon.GeometryFor(ConnectionIcon.PluginKey);
                Assert.IsNotNull(plug);
                Assert.IsTrue(tiles.All(tile => ReferenceEquals(tile.GetVisualDescendants().OfType<LucideIcon>().Single().Data, plug)),
                    "描述符没给图标:卡片画协议栏同款的通用插头,而不是空着。");

                Assert.AreSequenceEqual(["PickTypeCancelButton", "NextButton"], VisibleFooterButtons(window), "卡片那一屏页脚只有 [取消][下一步]。");
                Assert.IsFalse(window.FindControl<Grid>("EndpointPreviewRow")!.IsEffectivelyVisible, "还没有端点,预览收起。");
                Button next = window.FindControl<Button>("NextButton")!;
                Button cancel = window.FindControl<Button>("PickTypeCancelButton")!;
                Assert.IsTrue(next.IsDefault, "对话框级 Enter = 下一步。");
                Assert.IsTrue(next.IsEffectivelyEnabled, "预选了一档,「下一步」立即可用。");
                Assert.AreEqual(next.Bounds.Height, cancel.Bounds.Height, "同一排按钮同高(DESIGN.md §5.1)。");
                SaveOptionalFrame(window, "type-picker-cards.png");

                Click(window, tiles[1]);
                Assert.IsTrue(vm.IsPickingType, "单击只选不走。");
                Assert.AreSequenceEqual([false, true, false], TileSelection(window), "高亮跟到点的那张。");
                Assert.AreEqual("postgresql", vm.SelectedTypeValue);
            }
            finally
            {
                window.Close();
            }
        }
    });

    /// <summary>
    /// <b>点「下一步」进表单:页签条与表单回来,页脚换成 [上一步][测试][保存][连接],表头写着选定的类型</b>(附录 A 第二行)。
    /// <para>
    /// 新建时表头不带「更换」(回卡片走「上一步」)。点「上一步」回到卡片、高亮停在刚选的那张;
    /// 双击另一张 = 选中并下一步,直接回到表单(代码后置挂的 DoubleTapped)。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task TypePicker_AfterNext_FooterShowsBackTestSaveConnect_AndTabsReturn() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            try
            {
                await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
                Layout(window);
                Click(window, TypeTiles(window)[1]);
                Click(window, window.FindControl<Button>("NextButton")!);
                Assert.IsFalse(vm.IsPickingType, "点了「下一步」应进表单。");
                Assert.IsFalse(window.FindControl<ScrollViewer>("TypePickerPanel")!.IsEffectivelyVisible, "卡片面板收起。");
                Assert.IsTrue(window.FindControl<ScrollViewer>("FormScroll")!.IsEffectivelyVisible, "表单回来。");
                Assert.IsTrue(window.FindControl<Button>("GeneralTab")!.IsEffectivelyVisible, "分页页签条回来。");
                Assert.IsTrue(window.FindControl<StackPanel>("TypeHeader")!.IsEffectivelyVisible, "表单顶部出「类型」那一行。");
                Assert.AreEqual("PostgreSQL", window.FindControl<TextBlock>("TypeHeaderValue")!.Text);
                Assert.IsFalse(window.FindControl<Button>("ChangeTypeLink")!.IsEffectivelyVisible, "新建时没有「更换」。");
                Assert.DoesNotContain(text => text.Text == DialectLabel && text.IsEffectivelyVisible, window.GetVisualDescendants().OfType<TextBlock>(),
                    "表单里那一行类型下拉不再出现,由表头替代(D11)。");
                Assert.AreEqual(5432, vm.Port, "端口跟到 PostgreSQL 的默认值。");

                Assert.AreSequenceEqual(["BackButton", "TestButton", "SaveButton", "ConnectButton"], VisibleFooterButtons(window), "新建的表单:[上一步][测试][保存][连接]。");
                Assert.IsTrue(window.FindControl<Grid>("EndpointPreviewRow")!.IsEffectivelyVisible, "表单阶段照旧显示连接目标预览。");
                Button back = window.FindControl<Button>("BackButton")!;
                Assert.IsTrue(back.IsEffectivelyEnabled);
                Assert.AreEqual(window.FindControl<Button>("ConnectButton")!.Bounds.Height, back.Bounds.Height, "同一排按钮同高。");
                SaveOptionalFrame(window, "type-picker-form-new.png");

                Click(window, back);
                Assert.IsTrue(vm.IsPickingType, "「上一步」回到卡片。");
                Assert.IsTrue(window.FindControl<ScrollViewer>("TypePickerPanel")!.IsEffectivelyVisible);
                Assert.AreSequenceEqual([false, true, false], TileSelection(window), "回来时高亮停在当前类型上。");

                Click(window, TypeTiles(window)[0], twice: true);
                Assert.IsFalse(vm.IsPickingType, "双击卡片 = 选中并下一步。");
                Assert.AreEqual("MySQL", window.FindControl<TextBlock>("TypeHeaderValue")!.Text);
                Assert.AreEqual(3306, vm.Port, "端口还是默认值,跟到 MySQL。");
            }
            finally
            {
                window.Close();
            }
        }
    });

    /// <summary>
    /// <b>没有端点的类型收起「跳板主机」那一行,有端点的照常显示</b>:选 SQLite 进表单,绑着跳板列表的那个下拉
    /// 连同它的标签都看不见;回卡片换 MySQL,整行回来。
    /// <para>
    /// 断言落在真正绑定 <see cref="ConnectionProfileViewModel.JumpHostOptions" /> 的下拉与它所在的那一行上,
    /// 不只看视图模型属性 —— AXAML 那一行漏了 IsVisible 绑定时,视图模型用例照样绿,这条要红。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task JumpHostRow_CollapsesForTheTypeWithoutEndpoint_AndReturnsForMySql() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            try
            {
                await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
                Layout(window);
                Click(window, TypeTiles(window)[2]);
                Click(window, window.FindControl<Button>("NextButton")!);
                Assert.IsFalse(vm.IsPickingType, "前提:点了「下一步」应进表单。");
                Assert.AreEqual("SQLite", window.FindControl<TextBlock>("TypeHeaderValue")!.Text, "前提:选的是 SQLite。");
                Assert.IsTrue(window.GetVisualDescendants().OfType<TextBox>()
                        .Any(box => box.Text == "10.0.0.9" && box.IsEffectivelyVisible),
                    "前提:「常规」页在显示,主机那一栏看得见 —— 否则跳板行看不见可能只是整页没显示。");

                ComboBox jumpHost = JumpHostBox(window, vm);
                StackPanel row = jumpHost.GetVisualParent<StackPanel>()!;
                Assert.IsFalse(row.IsVisible, "SQLite 没有端点,「跳板主机」那一行该收起。");
                Assert.IsFalse(jumpHost.IsEffectivelyVisible, "SQLite 下跳板下拉还看得见。");
                Assert.IsFalse(JumpHostLabelVisible(row), "SQLite 下「跳板主机」标签还看得见。");

                Click(window, window.FindControl<Button>("BackButton")!);
                Click(window, TypeTiles(window)[0], twice: true);
                Assert.AreEqual("MySQL", window.FindControl<TextBlock>("TypeHeaderValue")!.Text, "前提:换成了 MySQL。");

                jumpHost = JumpHostBox(window, vm);
                row = jumpHost.GetVisualParent<StackPanel>()!;
                Assert.IsTrue(row.IsVisible, "MySQL 有端点,「跳板主机」那一行要回来。");
                Assert.IsTrue(jumpHost.IsEffectivelyVisible, "MySQL 下跳板下拉看不见。");
                Assert.IsTrue(JumpHostLabelVisible(row), "MySQL 下「跳板主机」标签看不见。");
            }
            finally
            {
                window.Close();
            }
        }
    });

    /// <summary>绑着跳板列表的那个下拉(连接对话框里只有它一个)。</summary>
    private static ComboBox JumpHostBox(Window window, ConnectionProfileViewModel vm) =>
        window.GetVisualDescendants().OfType<ComboBox>().Single(box => ReferenceEquals(box.ItemsSource, vm.JumpHostOptions));

    /// <summary>「跳板主机」那一行里的标签此刻看不看得见。</summary>
    private static bool JumpHostLabelVisible(StackPanel row) =>
        row.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text == Strings.Get("Profile_JumpHost") && text.IsEffectivelyVisible);

    /// <summary>
    /// <b>编辑已存的连接:直接是表单,表头「类型 PostgreSQL · 更换」,页脚没有「上一步」</b>(附录 A 第三行)。
    /// <para>点「更换」回到卡片,高亮在当前类型上;编辑态的卡片那一屏同样是 [取消][下一步]。</para>
    /// </summary>
    [TestMethod]
    public Task TypePicker_EditMode_ShowsChangeLink_NoBack() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(new SessionProfile
            {
                Name = "orders",
                ConnectionType = ConnectionType.Plugin,
                PluginProtocolId = DbId,
                Host = "db.internal",
                Port = 5432,
                Username = "app",
                PluginSettings = new(StringComparer.Ordinal) { [DialectKey] = "postgresql", ["database"] = "orders" }
            }, protocolRegistry: registry);
            await vm.PendingPluginLoad;
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            try
            {
                Layout(window);
                Assert.IsTrue(vm.IsEditing); Assert.IsFalse(vm.IsPickingType, "编辑已有配置不先出卡片。");

                Assert.IsTrue(window.FindControl<ScrollViewer>("FormScroll")!.IsEffectivelyVisible);
                Assert.IsTrue(window.FindControl<Button>("GeneralTab")!.IsEffectivelyVisible);
                Assert.IsFalse(window.FindControl<ScrollViewer>("TypePickerPanel")!.IsEffectivelyVisible);
                Assert.IsTrue(window.FindControl<StackPanel>("TypeHeader")!.IsEffectivelyVisible);
                Assert.AreEqual("PostgreSQL", window.FindControl<TextBlock>("TypeHeaderValue")!.Text);
                Button change = window.FindControl<Button>("ChangeTypeLink")!;
                Assert.IsTrue(change.IsEffectivelyVisible, "编辑态改类型走表头的「更换」。");
                Assert.DoesNotContain(text => text.Text == DialectLabel && text.IsEffectivelyVisible, window.GetVisualDescendants().OfType<TextBlock>(),
                    "编辑态同样不显示那一行类型下拉(D11)。");
                Assert.AreSequenceEqual(["TestButton", "SaveButton", "ConnectButton"], VisibleFooterButtons(window), "编辑态的表单没有「上一步」。");
                SaveOptionalFrame(window, "type-picker-form-edit.png");

                Click(window, change);
                Assert.IsTrue(vm.IsPickingType, "「更换」回到卡片。");
                Assert.IsTrue(window.FindControl<ScrollViewer>("TypePickerPanel")!.IsEffectivelyVisible);
                Assert.AreSequenceEqual([false, true, false], TileSelection(window), "高亮在当前类型(PostgreSQL)上。");
                Assert.AreSequenceEqual(["PickTypeCancelButton", "NextButton"], VisibleFooterButtons(window));
            }
            finally
            {
                window.Close();
            }
        }
    });

    /// <summary>
    /// <b>编辑一条 PostgreSQL@5432 的连接,在协议栏点 SSH、再点回数据库:端口框里还是 5432</b>。
    /// <para>
    /// 编辑态点回来直接是表单(没有卡片,也就没有「下一步」那一下可以纠正端口)。原先端口框停在页签默认的 3306 上,
    /// 表头却写着 PostgreSQL —— 点保存就落盘一条连不上的配置。断言落在界面上那个端口框,不只看视图模型。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task TypePicker_EditMode_SshTabThenBack_KeepsThePortInThePortBox() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(new SessionProfile
            {
                Name = "orders",
                ConnectionType = ConnectionType.Plugin,
                PluginProtocolId = DbId,
                Host = "db.internal",
                Port = 5432,
                Username = "app",
                PluginSettings = new(StringComparer.Ordinal) { [DialectKey] = "postgresql" }
            }, protocolRegistry: registry);
            await vm.PendingPluginLoad;
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            try
            {
                Layout(window);
                Assert.AreEqual(5432m, PortBox(window).Value, "前提:端口框里是已存的 5432。");

                Click(window, window.FindControl<Button>("SshTab")!);
                Assert.IsTrue(vm.IsSshSelected, "前提:点到了 SSH。");
                Assert.AreEqual(22m, PortBox(window).Value, "前提:切到 SSH 端口跟到 22。");

                Click(window, window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Classes.Contains("plugin-proto") && Equals(button.Tag, DbId)));
                Assert.IsFalse(vm.IsPluginLoading, "前提:表单装载完了。");
                Assert.IsFalse(vm.IsPickingType, "编辑态点回来直接是表单。");
                Assert.AreEqual("PostgreSQL", window.FindControl<TextBlock>("TypeHeaderValue")!.Text);
                Assert.AreEqual(5432m, PortBox(window).Value, "点一下 SSH 再点回来,端口框成了页签默认值,表头却还是 PostgreSQL。");
                Assert.AreEqual(5432, vm.Port);
            }
            finally
            {
                window.Close();
            }
        }
    });

    /// <summary>「常规」页上绑着端口的那个数字框(1–65535;其余几个数字框在别的分页上)。</summary>
    private static NumericUpDown PortBox(Window window) =>
        window.GetVisualDescendants().OfType<NumericUpDown>()
            .Single(box => box.IsEffectivelyVisible && box is { Minimum: 1, Maximum: 65535 });

    /// <summary>打开新建对话框、点进数据库页签,停在卡片那一屏;把键盘焦点放到第 <paramref name="index" /> 张卡片上。</summary>
    private static async Task<(ConnectionProfileViewModel Vm, ConnectionProfileView Window)> OpenCardsWithFocusOnAsync(
        PluginProtocolRegistry registry, int index)
    {
        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        var window = new ConnectionProfileView { DataContext = vm };
        window.Show();
        await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
        Layout(window);
        Assert.IsTrue(vm.IsPickingType, "前提:停在卡片上。");
        Button card = TypeTiles(window)[index];
        Assert.IsTrue(card.Focus(NavigationMethod.Tab), "前提:卡片拿得到键盘焦点。");
        Layout(window);
        Assert.IsTrue(card.IsFocused);
        return (vm, window);
    }

    /// <summary>
    /// <b>焦点在卡片上按 Enter = 选中这张并进入下一步</b>(与双击同义)。
    /// <para>
    /// 进卡片那一屏时焦点就落在卡片上;卡片是 Button,不拦的话它把 Enter 当一次点击(只选中)吞掉,
    /// 对话框级的「下一步」(IsDefault)收不到,键盘用户按 Enter 永远停在卡片上。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task TypePicker_EnterOnAFocusedCard_SelectsItAndGoesToTheForm() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            (ConnectionProfileViewModel vm, ConnectionProfileView window) = await OpenCardsWithFocusOnAsync(registry, 1);
            try
            {
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Layout(window);

                Assert.IsFalse(vm.IsPickingType, "卡片上按 Enter 应选中并进入下一步。");
                Assert.AreEqual("PostgreSQL", vm.CurrentTypeLabel, "类型是按 Enter 的那张卡。");
                Assert.AreEqual("PostgreSQL", window.FindControl<TextBlock>("TypeHeaderValue")!.Text);
                Assert.AreEqual(5432, vm.Port, "端口跟到 PostgreSQL 的默认值。");
                Assert.IsTrue(window.FindControl<ScrollViewer>("FormScroll")!.IsEffectivelyVisible, "表单回来。");
            }
            finally
            {
                window.Close();
            }
        }
    });

    /// <summary><b>焦点在卡片上按 Space 只选中</b>:高亮换过去,仍停在卡片那一屏,类型与端口都没被碰。</summary>
    [TestMethod]
    public Task TypePicker_SpaceOnAFocusedCard_OnlySelectsIt() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            (ConnectionProfileViewModel vm, ConnectionProfileView window) = await OpenCardsWithFocusOnAsync(registry, 1);
            try
            {
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Layout(window);

                Assert.IsTrue(vm.IsPickingType, "Space 只选中,不离开卡片。");
                Assert.AreEqual("postgresql", vm.SelectedTypeValue, "选中的是按 Space 的那张卡。");
                Assert.AreSequenceEqual([false, true, false], TileSelection(window), "高亮跟到那张卡。");
                Assert.AreEqual("MySQL", vm.CurrentTypeLabel, "还没点下一步,类型不变。");
                Assert.AreEqual(3306, vm.Port, "还没点下一步,端口不动。");
            }
            finally
            {
                window.Close();
            }
        }
    });

    // ============================== 插件自定义页(按页键分页) ==============================

    /// <summary>
    /// 模拟 MySQL 的合成表单:类型(出卡片)、「数据库」页放默认库与 schema、「SSL」页放 SSL 档位、
    /// 「高级」放连接超时。经 <c>PluginFormOverride</c> 喂进去,界面用例不依赖插件描述符的写法。
    /// </summary>
    private static PluginConnectionForm PagedMySqlForm() =>
        new(PluginConnectionKind.Workspace,
            HostLabel: null,
            HostPlaceholder: null,
            UsernameLabel: null,
            PasswordLabel: null,
            Fields:
            [
                new()
                {
                    Key = DialectKey,
                    Label = DialectLabel,
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL"), new("sqlite", "SQLite")]
                },
                new() { Key = "database", Label = "默认数据库", Placeholder = "连接后先进入的库(可留空)" },
                new() { Key = "schema", Label = "Schema", Placeholder = "可留空" },
                new()
                {
                    Key = "sslMode",
                    Label = "SSL 模式",
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "preferred",
                    Choices = [new("disabled", "禁用"), new("preferred", "优先"), new("required", "必须"), new("verify-ca", "校验 CA")]
                },
                new() { Key = "timeout", Label = "连接超时(秒)", Kind = ProtocolSettingKind.Integer, DefaultValue = "15", IsAdvanced = true }
            ],
            AllowsAnonymous: false,
            SupportsSshTunnel: false,
            ShowsCredentials: true,
            VariantKey: DialectKey,
            Variants:
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 },
                new() { Value = "sqlite", DefaultPort = 1, Features = WorkspaceFeatures.NoCredentials | WorkspaceFeatures.NoEndpoint }
            ],
            Pages: [new("database", "数据库"), new("ssl", "SSL")],
            FieldPages: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database"] = "database",
                ["schema"] = "database",
                ["sslMode"] = "ssl"
            });

    /// <summary>页签条上此刻看得见的页签,从左到右。</summary>
    private static List<Button> VisibleSectionTabs(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("section-tab") && button.IsEffectivelyVisible)
            .OrderBy(button => button.TranslatePoint(default, window)?.X ?? double.NaN)];

    private static bool LabelShowing(Window window, string label) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label && text.IsEffectivelyVisible);

    /// <summary>
    /// <b>插件声明的自定义页签画在「常规」与「高级」之间,点进去只露出那一页的字段</b>。
    /// <para>
    /// 卡片那一屏整条页签条收起,自定义页签跟着收;「下一步」之后是 [常规][数据库][SSL][高级],
    /// 与内建页签同一个主题、同样的间距;点「数据库」:页签高亮、下划线滑过去、只露出默认库与 schema。
    /// 设了 <c>VELASHELL_VISUAL_QA_DIR</c> 时把停在「数据库」页的这一帧存成 plugin-pages-database.png。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task PluginPageTabs_RenderBetweenGeneralAndAdvanced() => _session.RunOnUiAsync(async () =>
    {
        PluginProtocolRegistry registry = DbRegistry(out IDisposable handle);
        using (handle)
        {
            PluginConnectionForm form = PagedMySqlForm();
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry)
            {
                Host = "10.0.0.9",
                Username = "root",
                PluginFormOverride = _ => form
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            try
            {
                await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
                Layout(window);
                Assert.IsTrue(vm.IsPickingType, "前提:新建连接先停在卡片上。");
                ItemsControl pageTabs = window.FindControl<ItemsControl>("PluginPageTabs")!;
                Assert.IsFalse(pageTabs.IsEffectivelyVisible, "卡片那一屏整条页签条收起,自定义页签跟着收。");

                Click(window, window.FindControl<Button>("NextButton")!);
                Assert.IsFalse(vm.IsPickingType, "前提:「下一步」之后进表单。");

                List<Button> tabs = VisibleSectionTabs(window);
                Button general = window.FindControl<Button>("GeneralTab")!;
                Button advanced = window.FindControl<Button>("AdvancedTab")!;
                Assert.AreSequenceEqual(
                    [null, "database", "ssl", null], tabs.Select(tab => tab.Tag).ToArray(), "页签从左到右:常规、数据库、SSL、高级。");
                Assert.AreSame(general, tabs[0]);
                Assert.AreSame(advanced, tabs[3]);
                Assert.AreSequenceEqual(["数据库", "SSL"], tabs.Skip(1).Take(2).Select(tab => tab.GetVisualDescendants().OfType<TextBlock>().First().Text).ToArray());
                Assert.IsTrue(tabs.All(tab => tab.Theme == general.Theme && tab.Bounds.Height == general.Bounds.Height),
                    "自定义页签与「常规」同一个主题、同高。");
                double Left(Button tab) => tab.TranslatePoint(default, window)?.X ?? double.NaN;
                for (int i = 1; i < tabs.Count; i++)
                {
                    double gap = Left(tabs[i]) - (Left(tabs[i - 1]) + tabs[i - 1].Bounds.Width);
                    Assert.AreEqual(2, gap, 0.01, $"页签 {i - 1}→{i} 的间距与内建页签一致(2)。");
                }
                Assert.IsTrue(general.Classes.Contains("selected"));
                Assert.IsFalse(tabs[1].Classes.Contains("selected"));

                Click(window, tabs[1]);
                Assert.IsTrue(vm.IsPluginPageSection, "点「数据库」页签进那一页。");
                Assert.AreEqual("database", vm.SelectedPluginPageKey);
                Assert.IsTrue(tabs[1].Classes.Contains("selected"), "「数据库」页签高亮。");
                Assert.IsFalse(tabs[2].Classes.Contains("selected"));
                Assert.IsFalse(general.Classes.Contains("selected"));
                AssertIndicatorAligned(window.FindControl<Border>("SectionTabIndicator")!, tabs[1]);
                Assert.IsTrue(window.FindControl<StackPanel>("PluginPageSection")!.IsEffectivelyVisible);
                Assert.IsTrue(LabelShowing(window, "默认数据库"));
                Assert.IsTrue(LabelShowing(window, "Schema"));
                Assert.IsFalse(LabelShowing(window, "SSL 模式"), "别的自定义页的字段不露出来。");
                Assert.IsFalse(LabelShowing(window, "连接超时(秒)"), "「高级」的字段不露出来。");
                Assert.DoesNotContain(box => box.Text == "10.0.0.9" && box.IsEffectivelyVisible, window.GetVisualDescendants().OfType<TextBox>(),
                    "「常规」页的主机那一栏不在这一页上。");
                SaveOptionalFrame(window, "plugin-pages-database.png");

                Click(window, tabs[2]);
                Assert.AreEqual("ssl", vm.SelectedPluginPageKey);
                AssertIndicatorAligned(window.FindControl<Border>("SectionTabIndicator")!, tabs[2]);
                Assert.IsTrue(LabelShowing(window, "SSL 模式"));
                Assert.IsFalse(LabelShowing(window, "默认数据库"));
            }
            finally
            {
                window.Close();
            }
        }
    });

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
    /// 协议栏项的悬停直接落值,不在 Button 本体上挂画刷过渡(#577)。
    /// 常态背景是 Transparent(#00FFFFFF,全透明的白),Avalonia 按非预乘的 ARGB 逐通道插值,
    /// 过渡到不透明的悬停色途中是半透明的白,暗色主题下指针每进出一次就闪一下亮灰。
    /// 更一般的守门见 Design/BrushTransitionFromTransparentTests。
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
