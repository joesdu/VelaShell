using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>连接配置编辑窗口,用于新建或编辑连接档案并支持保存后立即连接。</summary>
public partial class ConnectionProfileView : Window
{
    private bool _sectionIndicatorPlaced;
    private Avalonia.Animation.Transitions? _sectionIndicatorTransitions;
    private (double X, double W) _sectionIndicatorGeometry = (-1, -1);

    /// <summary>屏幕工作区四周留出的余量(DIP):窗口不贴边,也给投影留出位置。</summary>
    private const double ScreenEdgeMargin = 48;

    /// <summary>
    /// 对话框自身的高度上限(DIP,含卡片外的 16 投影余量)。窗口按设计定高 696,
    /// 这个上限只在屏幕更矮时起作用;取 768 与设置窗口(948×768)对齐,
    /// 应用里的大弹窗保持同一个高度上限。
    /// </summary>
    private const double PreferredMaxHeight = 768;

    /// <summary>初始化连接配置窗口,并在打开时绑定命令与加载分组数据。</summary>
    public ConnectionProfileView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        // 必须在 Show 之前钳一次:等到 Opened 再钳,窗口已经按未钳制的高度量好并定过位了,
        // 用户会先看到一个高过屏幕的窗口闪一下,而且它的 Y 可能已经是负数。
        ApplyScreenBounds(preferCurrentScreen: false);
        Opened += OnOpened;
        // 贴着屏幕下沿开的对话框被钳高之后可能落在工作区外面;每次尺寸变化都把窗口拉回工作区内。
        SizeChanged += (_, _) => ClampToWorkingArea();
        // 滑动下划线跟随布局(字体加载、DPI 变化、换协议后页签增减都会改按钮位置);几何未变时短路。
        LayoutUpdated += (_, _) => UpdateSectionTabIndicator();
        // 挂在表单内容上而不是 ScrollViewer 上:请求冒泡时先到 ScrollContentPresenter,挂在外面就拦晚了。
        if (FormScroll.Content is Control formContent)
        {
            formContent.AddHandler(RequestBringIntoViewEvent, OnFormRequestBringIntoView);
        }
        // 双击类型卡片 = 选中并下一步。挂在卡片列表上(卡片是模板生成的,逐个挂要跟着集合增删)。
        // 连已处理的也收:卡片内容里哪个控件先把这次双击标成已处理,也不该吞掉「下一步」。
        TypeCardsList.AddHandler(DoubleTappedEvent, OnTypeCardDoubleTapped, handledEventsToo: true);
        // 卡片上按 Enter = 选中并下一步(与双击同义),Space 仍只选中。走隧道:卡片是 Button,
        // 冒泡到它自己时它会把 Enter 当一次点击(只选中)并标成已处理,对话框级的「下一步」(IsDefault)就收不到了。
        TypeCardsList.AddHandler(KeyDownEvent, OnTypeCardKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>双击卡片:选中它并直接进表单(等于单击 + 「下一步」)。</summary>
    private void OnTypeCardDoubleTapped(object? sender, TappedEventArgs e) => ConfirmTypeCard(e);

    /// <summary>卡片上按 Enter:同双击。其余按键(Space 选中、Tab 换卡)照常交给卡片自己。</summary>
    private void OnTypeCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConfirmTypeCard(e);
        }
    }

    /// <summary>事件来自某张卡片时,选中它并直接进表单(ConfirmTypeCard);不是来自卡片的不碰。</summary>
    private void ConfirmTypeCard(RoutedEventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true)?.DataContext
                is not PluginTypeCardViewModel card)
        {
            return;
        }
        viewModel.ConfirmTypeCardCommand.Execute(card.Value).Subscribe();
        e.Handled = true;
    }

    /// <summary>
    /// 进到卡片那一屏时把焦点放到选中的那张上:键盘用户接着就能 Tab / Space 选别的,Enter 进下一步。
    /// 不走 Tab 式聚焦 —— 那会挂上 :focus-visible 的描边,鼠标点进来的人看到的是一圈卡住的高亮。
    /// </summary>
    private void FocusSelectedTypeCard()
    {
        if (DataContext is not ConnectionProfileViewModel { IsPickingType: true })
        {
            return;
        }
        TypeCardsList.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.DataContext is PluginTypeCardViewModel { IsSelected: true })
            ?.Focus();
    }

    /// <summary>
    /// 只让有焦点的文本框滚动表单。文本框被程序赋值(编辑已存的连接时回填口令、插件字段回填)时光标跟着挪,
    /// <c>TextPresenter</c> 随后发一次 BringIntoView —— 不管有没有焦点;口令框落在首屏以下时,
    /// 表单一打开就被它拽下去,连接名那一栏看不见。Tab / 点击进去的那一个照常带进视野。
    /// </summary>
    private static void OnFormRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject?.FindAncestorOfType<TextBox>(includeSelf: true) is { IsKeyboardFocusWithin: false })
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 把窗口的最大尺寸钉在屏幕工作区与 <see cref="PreferredMaxHeight" /> 之内。
    /// 矮屏上窗口放不下设计高度时,超出的部分交给表单区的 ScrollViewer,
    /// 页签、反馈条与底部的保存/连接按钮始终留在窗口里。
    /// </summary>
    /// <param name="preferCurrentScreen">
    /// 是否优先取窗口当前所在的屏幕(显示之后才有意义);否则取主屏。
    /// </param>
    private void ApplyScreenBounds(bool preferCurrentScreen)
    {
        if (ResolveScreen(preferCurrentScreen) is not { Scaling: > 0 } screen)
        {
            return;
        }
        // 上限按 Windows 口径写(含卡片外 16px 的投影余量):其它平台没有那圈余量,减掉才与卡片可见高度一致;
        // Wayland 的阴影与描边画在窗口尺寸之外,工作区里要一并让出来。
        double frame = WindowChrome.OuterFrameSize(this);
        double screenLimit = Math.Max(240, (screen.WorkingArea.Height / screen.Scaling) - ScreenEdgeMargin - frame);
        // 小屏按屏幕钳,大屏按设计上限钳:两者取小。
        MaxHeight = Math.Min(PreferredMaxHeight - WindowChrome.SizeReductionOf(this), screenLimit);
        MaxWidth = Math.Max(320, (screen.WorkingArea.Width / screen.Scaling) - ScreenEdgeMargin - frame);
    }

    /// <summary>把窗口位置夹回屏幕工作区(尺寸变化后仍留在原处会露到屏幕外)。</summary>
    private void ClampToWorkingArea()
    {
        if (ResolveScreen(preferCurrentScreen: true) is not { Scaling: > 0 } screen)
        {
            return;
        }
        PixelRect area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        PixelPoint current = Position;
        int x = Math.Clamp(current.X, area.X, Math.Max(area.X, area.Right - size.Width));
        int y = Math.Clamp(current.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        if (x != current.X || y != current.Y)
        {
            Position = new(x, y);
        }
    }

    private Screen? ResolveScreen(bool preferCurrentScreen)
    {
        try
        {
            return (preferCurrentScreen ? Screens.ScreenFromWindow(this) : null) ?? Screens.Primary;
        }
        catch
        {
            // 屏幕信息不是每个后端/时机都拿得到(无头测试、远程桌面):拿不到就不钳。
            return null;
        }
    }

    /// <summary>
    /// 把滑动下划线对齐到当前分页的页签:首次落位不动画,此后位置与宽度经
    /// 180ms 过渡滑动。
    /// </summary>
    private void UpdateSectionTabIndicator()
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        Button? target = viewModel.SelectedSection switch
        {
            ConnectionProfileSection.Terminal => TerminalTab,
            ConnectionProfileSection.SshOptions => SshOptionsTab,
            ConnectionProfileSection.Forwarding => ForwardingTab,
            ConnectionProfileSection.Advanced => AdvancedTab,
            ConnectionProfileSection.PluginPage => PluginPageTab(viewModel.SelectedPluginPageKey),
            _ => GeneralTab
        };
        if (target is null || !target.IsVisible || target.Bounds.Width <= 0)
        {
            return;
        }
        Point origin = target.TranslatePoint(default, SectionTabsPanel) ?? default;
        (double X, double W) geometry = (Math.Round(origin.X), Math.Round(target.Bounds.Width));
        if (geometry == _sectionIndicatorGeometry && SectionTabIndicator.IsVisible)
        {
            return;
        }
        _sectionIndicatorGeometry = geometry;
        bool animate = _sectionIndicatorPlaced;
        if (!animate)
        {
            _sectionIndicatorTransitions ??= SectionTabIndicator.Transitions;
            SectionTabIndicator.Transitions = null;
        }
        SectionTabIndicator.Width = geometry.W;
        SectionTabIndicator.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"translateX({geometry.X}px)"));
        SectionTabIndicator.IsVisible = true;
        if (!animate)
        {
            _sectionIndicatorPlaced = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => SectionTabIndicator.Transitions ??= _sectionIndicatorTransitions,
                Avalonia.Threading.DispatcherPriority.Render);
        }
    }

    /// <summary>自定义页签里页键为 <paramref name="key" /> 的那一个(模板生成的,按 Tag 找);还没建出来时为 null。</summary>
    private Button? PluginPageTab(string? key) =>
        key is null
            ? null
            : PluginPageTabs.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => button.Tag is string tag && string.Equals(tag, key, StringComparison.Ordinal));

    private void OnOpened(object? sender, EventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        // 对话框是 CenterOwner:显示之后才知道落在哪块屏上,多屏下重新钳一次。
        ApplyScreenBounds(preferCurrentScreen: true);
        ClampToWorkingArea();
        // 剪贴板挂在 TopLevel 上,视图模型层够不着,由视图注入。赋值幂等:
        // 窗口重开也只是覆盖同一个委托,不会像事件订阅那样越接越多。
        viewModel.CopyToClipboard = CopyToClipboardAsync;
        // 「放开这几个算法并重新测试」的确认框:与终端标签那颗按钮同一个框(同一个标题、同一种点名、警示样式),
        // 以本对话框为属主。
        viewModel.AllowAlgorithmsConfirmer = (title, body, action) =>
            MessageDialog.ConfirmAsync(this, title, body, action, kind: MessageDialogKind.Warning);
        // 切页只改按钮前景色、不触发布局,滑动下划线必须由 VM 属性变化驱动。
        // 换协议也要盯:页签随之增减,选中那一页的按钮位置跟着挪(或者被收起、落回「常规」)。
        viewModel.PropertyChanged += (_, args) =>
        {
            // 自定义页之间切换时 SelectedSection 不变(都是 PluginPage),只有页键变。
            if (args.PropertyName is nameof(ConnectionProfileViewModel.SelectedSection)
                or nameof(ConnectionProfileViewModel.ConnectionType)
                or nameof(ConnectionProfileViewModel.SelectedPluginPageKey))
            {
                UpdateSectionTabIndicator();
            }
            // 卡片要等这一轮布局把它们建出来才聚焦得到,推迟到布局之后。
            if (args.PropertyName is nameof(ConnectionProfileViewModel.IsPickingType) && viewModel.IsPickingType)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(FocusSelectedTypeCard, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
        UpdateSectionTabIndicator();
        // 保存/连接/取消命令由按钮点击触发,回调仍在输入事件栈内:推迟关闭,避免后续路由
        // 打到已销毁的窗口刷 "PlatformImpl is null" 警告。
        viewModel.SaveCommand.Subscribe(this.PostClose);
        viewModel.ConnectCommand.Subscribe(this.PostClose);
        viewModel.CancelCommand.Subscribe(this.PostClose);
        // 「凭据来源」旁的「新建…」(#550):编辑框是本对话框的子窗口,视图模型够不着窗口,由视图注入。
        viewModel.CreateSharedCredential = CreateSharedCredentialAsync;
        await viewModel.LoadGroupsAsync();
        await viewModel.LoadSharedCredentialsAsync();
    });

    /// <summary>
    /// 打开共享凭据编辑框(预填表单里现有的凭据),保存后交回新建的那一条;取消为 null。
    /// </summary>
    /// <remarks>
    /// 不显示「使用这条凭据的连接」清单:要挂的就是正在编辑的这一条,由本对话框自己保存时带上引用。
    /// </remarks>
    private async Task<SharedCredential?> CreateSharedCredentialAsync(SharedCredential draft)
    {
        if ((Application.Current as App)?.Services is not { } services
            || services.GetService<ISharedCredentialRepository>() is not { } repository)
        {
            return null;
        }
        List<SessionProfile> profiles = services.GetService<ISessionRepository>() is { } sessions
            ? await sessions.GetAllSessionsAsync()
            : [];
        var dialog = new SharedCredentialEditorView
        {
            DataContext = new SharedCredentialEditorViewModel(null, profiles, showUsers: false, draft: draft)
        };
        if (await dialog.ShowDialog<SharedCredentialEditResult?>(this) is not { } result)
        {
            return null;
        }
        await repository.SaveAsync(result.Credential);
        return result.Credential;
    }

    /// <summary>窗口关闭时退订注册表事件,免得单例注册表上挂满已关闭对话框的视图模型。</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as ConnectionProfileViewModel)?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Esc 等价于点击取消:经 CancelCommand 走与取消按钮完全相同的关闭路径(不保存改动)。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is ConnectionProfileViewModel viewModel)
            {
                viewModel.CancelCommand.Execute().Subscribe();
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    /// <summary>主机栏失焦:写成「主机:端口」的,把端口拆进端口栏。</summary>
    private void HostBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionProfileViewModel viewModel)
        {
            viewModel.SplitPortFromHost();
        }
    }

    /// <summary>把文本写进系统剪贴板;拿不到剪贴板(无 TopLevel)时静默跳过。</summary>
    private async Task CopyToClipboardAsync(string text)
    {
        if (GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void BrowseKeyFile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    });

    /// <summary>
    /// 选择 OpenSSH 用户证书文件。选完之后若私钥还空着,按 OpenSSH 的
    /// <c>&lt;key&gt;-cert.pub</c> 命名约定顺手把私钥补上。
    /// </summary>
    /// <remarks>
    /// 证书与私钥是 ssh-keygen 成对产出的,文件名只差一个后缀。不自动补的话,用户要在
    /// 两个文件选择器里把同一个目录翻两遍,还容易挑到隔壁那把不匹配的私钥 ——
    /// 而那种错配到连接时只会得到一句笼统的 publickey 被拒。
    /// 推不出来(自定义命名)或推出的文件不存在就什么都不做,交回用户自己选。
    /// </remarks>
    private void BrowseCertificateFile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectCertFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }
        viewModel.CertificatePath = path;
        // 只有证书认证才按 <key>-cert.pub 的约定补私钥;系统密钥库那一路的钥在密钥库里,补一个私钥路径只会留下一个用不上的字段。
        if (viewModel.IsCertAuth
            && string.IsNullOrWhiteSpace(viewModel.PrivateKeyPath) && OpenSshCertificatePaths.InferPrivateKeyPath(path) is { } keyPath)
        {
            viewModel.PrivateKeyPath = keyPath;
        }
    });
}
