using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;
using KeyModifiers = VelaShell.Services.KeyModifiers;

namespace VelaShell.Views;

/// <summary>
/// 单个终端标签页的视图:承载终端控件,并叠加行内搜索、命令补全弹层、
/// 滚动条同步与快捷键回退等交互逻辑。
/// </summary>
public partial class TerminalTabView : UserControl
{
    private readonly IKeyboardShortcutService _shortcutService;

    // 共享密码列表随提示符出现、随上下文变化消失:一次弹出对应一次填入,关掉即作废,
    // 用户想再填得重新触发 —— 免得一个陈旧的弹层在光标早已挪走后仍然可点。
    private SharedPasswordFillRequest? _passwordRequest;
    private string? _suppressedPasswordPrompt;
    private INotifyPropertyChanged? _passwordHost;

    /// <summary>
    /// 取共享凭据仓储。生产走 <see cref="App" /> 的 DI 容器;headless 测试挂的是另一个
    /// <see cref="Application" />(不带容器),故留一个可替换的取数口,与
    /// <c>EnglishInputLocale.InputLocaleSwitcherFactory</c> 同一套做法。
    /// </summary>
    internal static Func<ISharedCredentialRepository?> RepositoryFactory { get; set; } =
        static () =>
            Application.Current is App { Services: { } services }
                ? services.GetService<ISharedCredentialRepository>()
                : null;

    private void OnPasswordHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActiveTerminalTab))
        {
            ClosePasswordPopup();
        }
    }

    /// <summary>收起密码列表;请求随之作废,已入队的字节不受影响(桥已持有)。</summary>
    private void ClosePasswordPopup(bool suppress = false)
    {
        if (suppress && _termControl is { } control)
        {
            // 记住这一行提示,免得它还在屏幕上时反复重开(用户已决定手输或忽略)。
            _suppressedPasswordPrompt = control.GetLogicalLineEndingAt(control.CursorRow);
        }
        // 先作废请求再收弹层:收起会同步触发 Closed,那边见请求已空就不再重入。
        _passwordRequest?.Dispose();
        _passwordRequest = null;
        if (PasswordPopup.IsOpen)
        {
            PasswordPopup.IsOpen = false;
        }
        PasswordList.ItemsSource = null;
        PasswordStatus.IsVisible = false;
    }

    /// <summary>
    /// 请求被键入或提示行变化作废:收口,但不记成「用户已拒绝」(不 suppress)。
    /// </summary>
    /// <remarks>
    /// 空口令上的 Backspace、Enter 不改变跟踪的输入,InputChanged 不响 —— 原先弹层就这么留着,
    /// 点哪一项都只回「已过期」,而按 Esc 关掉又会把这一行记成已拒绝,自动入口再也不出来。
    /// </remarks>
    private void OnPasswordRequestExpired(SharedPasswordFillRequest request)
    {
        if (ReferenceEquals(request, _passwordRequest))
        {
            ClosePasswordPopup();
        }
    }

    /// <summary>
    /// 输出批次结束后复核光标行:是明确的密码提示且上下文没变,就把共享密码列表锚在
    /// 光标行下沿弹出。识别是启发式的(见 <see cref="InteractivePromptDetector.IsPasswordFillPrompt" />),
    /// 误判的代价只是多出一个可忽略的列表,手动入口不受影响。
    /// </summary>
    private void OnPasswordOutputUpdated()
    {
        if (_termControl is not { } control
            || DataContext is not TerminalTabViewModel { IsConnected: true, Bridge: not null } tab
            || TopLevel.GetTopLevel(this) is not Window { IsActive: true, DataContext: MainWindowViewModel main }
            || !ReferenceEquals(main.ActiveTerminalTab, tab) || !control.IsFocused
            || control.ScrollOffset != 0 || control.IsAlternateScreenActive || !IsEffectivelyVisible)
        {
            ClosePasswordPopup();
            return;
        }
        // 廉价预筛:每个输出批次都走这里,cat 一个不换行的大文件时整屏就是一条逻辑行。两种认法都要求
        // 去掉尾随空白后以冒号收尾(见 IsPasswordFillPrompt),而逻辑行的结尾就是光标行 —— 光标行有字
        // 却不以冒号收尾,就不必拼整段逻辑行、也不必跑正则。光标行全空白时(提示的尾随空格恰好折到
        // 下一行)结尾在上面几行,照旧拼。
        string cursorLine = control.GetBufferLine(control.CursorRow);
        ReadOnlySpan<char> tail = cursorLine.AsSpan().TrimEnd();
        if (tail.Length > 0 && tail[^1] is not (':' or '：'))
        {
            ClosePasswordPopup();
            _suppressedPasswordPrompt = null;
            return;
        }
        // 整段逻辑行:窄窗格里带 URL 的 git 提示会被自动换行折开,光标行只剩尾巴。光标行本身完整时照认 ——
        // 上一行的折行标志可能是 "\r ESC[2K" 原地重写前留下的陈旧值,接起来反倒认不出。
        string line = control.GetLogicalLineEndingAt(control.CursorRow);
        if (!(InteractivePromptDetector.IsPasswordFillPrompt(line)
              || InteractivePromptDetector.IsPasswordFillPrompt(cursorLine))
            || line == _suppressedPasswordPrompt)
        {
            ClosePasswordPopup();
            if (line != _suppressedPasswordPrompt)
            {
                _suppressedPasswordPrompt = null;
            }
            return;
        }
        if (_passwordRequest is { PromptChanged: true })
        {
            // 提示行换了一条(输错后重新提示):旧请求已经作废,按新提示重开,别留一个点了只回「已过期」的列表。
            ClosePasswordPopup();
        }
        if (!PasswordPopup.IsOpen)
        {
            OpenPasswordPopup(control);
        }
    }

    /// <summary>按提示符弹出列表:先只读元数据,读取与发送都留到用户点选之后。</summary>
    private void OpenPasswordPopup(VelaTerminalControl control)
    {
        if (DataContext is not TerminalTabViewModel { IsConnected: true, Bridge: not null } tab
            || RepositoryFactory() is not { } repository)
        {
            return;
        }
        DismissSuggestions(suppress: false);
        // 已经开着一个(命令面板里再点一次手动入口):先作废旧请求,否则它对控件的订阅就此泄漏。
        _passwordRequest?.Dispose();
        PasswordList.ItemsSource = null;
        PasswordList.SelectedItem = null;
        PasswordStatus.IsVisible = false;
        SharedPasswordFillRequest request = new(
            tab,
            () => IsAttachedToVisualTree() && PasswordPopup.IsOpen && ReferenceEquals(DataContext, tab));
        request.Expired += () => OnPasswordRequestExpired(request);
        _passwordRequest = request;
        // 告警照实说这一次按不按回车:命令面板的手动入口开在认不出的行上时只填不按(见 SubmitsWithEnter)。
        PasswordWarning.Text = Strings.Get(request.SubmitsWithEnter ? "PasswordFill_Warning" : "PasswordFill_WarningNoEnter");
        PasswordPopup.PlacementTarget ??= TerminalHost;
        // 锚在光标行本身:下面放得下就贴着它的下沿往下展开,放不下(最大化后提示符在末行)由定位器
        // 翻到它的上沿之上 —— 两种情况都不挡住提示行。上下各留一道缝,缝做在锚点矩形上而不是
        // VerticalOffset 上:偏移量翻转时不跟着反向,翻上去就成了往提示行里压。
        PasswordPopup.PlacementRect = control.GetCursorRect().Inflate(new Thickness(0, CursorPopupGap));
        PasswordPopup.IsOpen = true;
        FireAndForget.Run(() => LoadPasswordItemsAsync(request, repository));
    }

    // 贴着光标行弹出的两个弹层(共享密码、命令补全)与光标行之间的缝,上下各一道。
    // 缝做在锚点矩形上,不用 VerticalOffset:定位器把弹层翻到上方时偏移量不跟着反向,翻上去就压进光标行。
    private const double CursorPopupGap = 2;

    private async Task LoadPasswordItemsAsync(SharedPasswordFillRequest request, ISharedCredentialRepository repository)
    {
        IReadOnlyList<SharedCredentialMetadata> items;
        string? status = null;
        try
        {
            items = [.. (await repository.ListMetadataAsync())
                .Where(static c => c.AuthMethod == AuthMethod.Password && c.HasPassword)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不出来不等于没有:照实说读取失败,别让用户去设置里找一份其实存在的凭据。
            System.Diagnostics.Trace.WriteLine($"[PasswordFill] Listing shared credentials failed: {ex}");
            items = [];
            status = "PasswordFill_ReadFailed";
        }
        if (!ReferenceEquals(request, _passwordRequest))
        {
            return; // 加载期间弹层已收起或换了一次:别把列表塞回一个已经失效的请求。
        }
        PasswordList.ItemsSource = items;
        SetPasswordStatus(status ?? (items.Count == 0 ? "PasswordFill_Empty" : null));
    }

    private void SetPasswordStatus(string? key)
    {
        PasswordStatus.Text = key is null ? string.Empty : Strings.Get(key);
        PasswordStatus.IsVisible = key is not null;
    }

    /// <summary>
    /// 点选一条即读取、填入并提交:正文后面紧跟一次回车,同一次入队(见 <see cref="SharedPasswordFillRequest.FillAsync" />)。
    /// 原先只发正文、要用户再按一次 Enter(#628 第 1 项),选完顺手敲的字符就会混进口令里。
    /// 只在认得出的密码提示上按回车;手动入口开在别的行上时只填不按(<see cref="SharedPasswordFillRequest.SubmitsWithEnter" />)。
    /// 挂 SelectionChanged 而不是 Tapped:列表项不可聚焦,选中哪一行本来就是这次点击的唯一
    /// 语义,而 Tapped 的路由事件在 headless 测试里造不出来 —— 这条路径就永远测不到。
    /// 键盘 ↑↓ 也借选中态当高亮,那时只移高亮、不填入(<see cref="_highlightingPassword" />)。
    /// </summary>
    private void OnPasswordSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_highlightingPassword && PasswordList.SelectedItem is SharedCredentialMetadata selected)
        {
            FillPassword(selected);
        }
    }

    private void FillPassword(SharedCredentialMetadata selected)
    {
        if (_passwordRequest is not { } request || RepositoryFactory() is not { } repository)
        {
            return;
        }
        // 先清选中再发起读取:清空引发的这一次回调落到 null 守卫上,不会重入。
        PasswordList.SelectedItem = null;
        Task fill = FillSelectedPasswordAsync(request, repository, selected.Id);
        PasswordFillForTest = fill;
        FireAndForget.Run(() => fill);
    }

    // ↑↓ 正在挪键盘高亮:这次选中变化只是高亮,不是点选。
    private bool _highlightingPassword;

    /// <summary>
    /// 密码列表键位(#638):↑↓ 移高亮,Enter 填入高亮项;返回 true 表示按键已被列表消费。
    /// </summary>
    /// <remarks>
    /// 列表不可聚焦,键位照补全弹层的办法在隧道阶段截下。不预先高亮任何一项:提示符前直接按 Enter
    /// 是用户自己的提交(哪怕是空口令),不能变成「把第一个密码发出去」。
    /// </remarks>
    private bool HandlePasswordKey(KeyEventArgs e)
    {
        if (!PasswordPopup.IsOpen)
        {
            return false;
        }
        if (e.Key == Key.Escape)
        {
            ClosePasswordPopup(suppress: true);
            e.Handled = true;
            return true;
        }
        if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None)
        {
            return false;
        }
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                // 密码提示前的方向键只会把 ESC 序列塞进口令,列表开着就归列表;读取在途时原地不动。
                if (PasswordList.IsEnabled)
                {
                    MovePasswordHighlight(e.Key == Key.Down ? +1 : -1);
                }
                e.Handled = true;
                return true;
            case Key.Enter when !PasswordList.IsEnabled:
                // 读取在途(列表禁用):这一下多半是连按的「提交」。放过去,回车就抢在密码前面
                // 到达远端 —— 交上去的是空口令,而这次键入还会让在途的填入作废。
                e.Handled = true;
                return true;
            case Key.Enter when PasswordList.SelectedItem is SharedCredentialMetadata selected:
                FillPassword(selected);
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }

    private void MovePasswordHighlight(int delta)
    {
        int count = PasswordList.ItemCount;
        if (count == 0)
        {
            return;
        }
        int current = PasswordList.SelectedIndex;
        // 还没有高亮时 ↓ 落到第一项、↑ 落到最后一项;之后首尾循环。
        int next = current < 0
            ? (delta > 0 ? 0 : count - 1)
            : ((current + delta) % count + count) % count;
        SetPasswordHighlight(next);
        PasswordList.ScrollIntoView(next);
    }

    private void SetPasswordHighlight(int index)
    {
        _highlightingPassword = true;
        try
        {
            PasswordList.SelectedIndex = index;
        }
        finally
        {
            _highlightingPassword = false;
        }
    }

    /// <summary>
    /// 鼠标按下先撤掉键盘高亮:否则点中的恰是高亮那一项时选中态不变、不触发 SelectionChanged,
    /// 这一下就白点了。隧道阶段抢在列表项处理按下之前,且不置 Handled。
    /// </summary>
    private void OnPasswordListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PasswordList.SelectedIndex >= 0)
        {
            SetPasswordHighlight(-1);
        }
    }

    private async Task FillSelectedPasswordAsync(
        SharedPasswordFillRequest request,
        ISharedCredentialRepository repository,
        Guid id)
    {
        PasswordList.IsEnabled = false;
        PasswordFillResult result;
        try
        {
            result = await request.FillAsync(repository, id);
        }
        finally
        {
            PasswordList.IsEnabled = true;
        }
        if (result != PasswordFillResult.Queued)
        {
            // 失败一律留着弹层、把原因写在列表上方:这会儿收口,状态文字会连同弹层一起消失,
            // 用户只看到"点了一下、没了"。尤其过期那条,什么都没发出去,更不能让用户以为
            // 已经填上。上下文再变一次(输出/滚动/失焦)弹层自然收口,用户也可按 Esc。
            SetPasswordStatus(result switch
            {
                PasswordFillResult.Expired => "PasswordFill_Expired",
                PasswordFillResult.InvalidPassword => "PasswordFill_Invalid",
                PasswordFillResult.Unavailable => "PasswordFill_Unavailable",
                _ => "PasswordFill_ReadFailed",
            });
            return;
        }
        // 成功即收口:密码已在途,弹层留着只会让人再点一次。入队不等于远端送达,这是已知限制。
        ClosePasswordPopup(suppress: true);
        if (_termControl is { } control && IsAttachedToVisualTree())
        {
            control.Focus();
        }
    }

    /// <summary>没有识别到提示符时(命令面板入口)手动召出,锚在当前光标行。</summary>
    /// <remarks>
    /// <para>
    /// 先把焦点交回终端:面板收起后没人还焦点,而列表开着时 <see cref="FocusTerminal" /> 刻意不抢 ——
    /// 焦点不在终端上,↑↓/Enter/Esc 到不了隧道键位,键入也进不了终端,下一批输出还会因
    /// 「终端没焦点」把列表收掉。
    /// </para>
    /// <para>
    /// 这个入口开在光标此刻所在的任何一行上,所以光标行认不出是密码提示时只填不按回车,
    /// 列表顶部的告警也照实换成「不按回车」那一句(<see cref="SharedPasswordFillRequest.SubmitsWithEnter" />)。
    /// </para>
    /// </remarks>
    internal void OpenSharedPasswordPicker()
    {
        if (_termControl is { } control)
        {
            control.Focus();
            OpenPasswordPopup(control);
        }
    }

    /// <summary>
    /// 最近一次点选发起的读取与填入(测试用)。迟到的读取结果要经 UI 调度器排队才落到状态行,
    /// 让几次路都不保证轮到它 —— 测试等这个任务,而不是数 <c>Task.Yield</c>。
    /// </summary>
    internal Task PasswordFillForTest { get; private set; } = Task.CompletedTask;

    /// <summary>密码列表当前是否展开(测试用;弹层是独立顶层,不能从可视树里找)。</summary>
    internal bool PasswordPopupOpenForTest => PasswordPopup.IsOpen;

    /// <summary>密码列表当前展示的条目(测试用)。</summary>
    internal IReadOnlyList<SharedCredentialMetadata> PasswordItemsForTest =>
        PasswordList.ItemsSource as IReadOnlyList<SharedCredentialMetadata> ?? [];

    /// <summary>弹层锚定的矩形(测试用;弹层是独立顶层,视觉树里查不到)。</summary>
    internal Rect PasswordPlacementRectForTest => PasswordPopup.PlacementRect ?? default;

    /// <summary>命令补全弹层是否开着(测试用)。</summary>
    internal bool SuggestPopupOpenForTest => SuggestPopup.IsOpen;

    /// <summary>键盘高亮的是第几项;-1 = 没有高亮(测试用)。</summary>
    internal int PasswordHighlightForTest => PasswordList.SelectedIndex;

    /// <summary>弹层状态行的当前文字;未显示时为空串(测试用)。</summary>
    internal string PasswordStatusForTest => PasswordStatus.IsVisible ? PasswordStatus.Text ?? string.Empty : string.Empty;

    /// <summary>列表顶部那句风险告警的当前文字(测试用)。</summary>
    internal string PasswordWarningForTest => PasswordWarning.Text ?? string.Empty;

    /// <summary>收口密码列表(测试用;等价于用户按 Esc 或切走窗口)。</summary>
    internal void ClosePasswordPopupForTest() => ClosePasswordPopup(suppress: true);

    /// <summary>在补全面板里选中并接受一条候选(测试用;等价于面板开着时选中它按 Enter)。</summary>
    internal void AcceptSuggestionForTest(CommandSuggestion suggestion)
    {
        _suggestions = [suggestion];
        SuggestList.ItemsSource = _suggestions;
        SuggestList.SelectedIndex = 0;
        AcceptSuggestion();
    }

    /// <summary>最近一次「先问变量、再写进命令行」的接受流程(测试用;等它而不是数 <c>Task.Yield</c>)。</summary>
    internal Task QuickCommandAcceptForTest { get; private set; } = Task.CompletedTask;

    /// <summary>点选一条列表项(测试用;等价于点中那一行,走同一条 SelectionChanged 路径)。</summary>
    internal void PickPasswordItem(SharedCredentialMetadata item) =>
        PasswordList.SelectedItem = item;

    // ---- 终端内搜索(spec §5.3) ------------------------------------

    private IReadOnlyList<BufferSearchHit> _searchHits = [];

    private int _searchIndex = -1;
    private bool _syncingScrollBar;
    private VelaTerminalControl? _termControl;

    /// <summary>无参构造:使用默认的 <see cref="KeyboardShortcutService"/>(供 XAML/设计器实例化)。</summary>
    public TerminalTabView()
        : this(new KeyboardShortcutService()) { }

    /// <summary>使用指定的快捷键服务构造视图并完成事件接线。</summary>
    public TerminalTabView(IKeyboardShortcutService shortcutService)
    {
        _shortcutService =
            shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
        InitializeComponent();
        Focusable = true;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += (_, _) =>
        {
            ClosePasswordPopup();
            HookTerminalControl();
            HookSuggestions();
            // 失败覆盖层上的「复制」:剪贴板要经 TopLevel 取,视图模型层拿不到,由视图注入。
            if (DataContext is TerminalTabViewModel tab)
            {
                tab.CopyToClipboard = CopyToClipboardAsync;
            }
        };
        ScrollBarView?.Scroll += OnScrollBarScroll;

        // 采用 Tunnel 路由,使已断开的标签能在终端控件把按键交给 PTY 之前截获 Enter / Ctrl+R
        // 用于重连(以及 Ctrl+F 打开搜索)。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // 平台侧关闭弹层(如系统截图覆盖层/激活切换导致宿主隐藏它)时,IsOpen 与
        // 幽灵/列表状态会与本类脱钩;订阅 Closed 统一对账,防止"看不见却仍拦键"的
        // 僵尸弹层
        SuggestPopup.Closed += (_, _) => OnSuggestPopupClosedByPlatform();
        // 密码列表同理:平台侧收起时请求跟着作废、退订,不能让一个看不见的请求还挂在控件上。
        // 本类自己收口时请求已先置空,这里不会重入。
        PasswordPopup.Closed += (_, _) =>
        {
            if (_passwordRequest is not null)
            {
                ClosePasswordPopup();
            }
        };
        SearchBox.TextChanged += (_, _) => ScheduleSearch();
        SearchNext.Click += (_, _) => MoveHit(+1);
        SearchPrev.Click += (_, _) => MoveHit(-1);
        SearchClose.Click += (_, _) => CloseSearch();
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        SuggestList.Tapped += (_, _) => AcceptSuggestion();
        // 列表项不可聚焦(点密码不得抢走终端焦点),选中哪一行就是这次点击的全部语义。
        PasswordList.SelectionChanged += OnPasswordSelectionChanged;
        PasswordList.AddHandler(PointerPressedEvent, OnPasswordListPointerPressed, RoutingStrategies.Tunnel);

        // 点终端正文即收起补全弹层(#315)。弹层刻意不开 light-dismiss(那会吞掉
        // 关闭它的那一次点击),而点击一个**已经聚焦**的终端不产生 LostFocus,
        // 于是原先没有任何路径能收口它 —— 面板会一直悬在旧光标处。
        // Tunnel 抢在终端控件消费指针之前拿到,且不置 Handled:选区/光标行为不变。
        TerminalHost.AddHandler(
            PointerPressedEvent,
            OnTerminalHostPointerPressed,
            RoutingStrategies.Tunnel
        );
    }

    /// <summary>点击终端正文:收起补全弹层与幽灵,按键不消费(终端照常处理选区)。</summary>
    private void OnTerminalHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SuggestPopup.IsOpen)
        {
            DismissSuggestions(suppress: false);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (HandlePasswordKey(e))
        {
            return;
        }
        // 自愈守卫:弹层开着但焦点既不在终端控件也不在本视图(回退层)上,说明焦点
        // 被外因(系统截图覆盖层抢焦点、原生弹层被平台隐藏等)拖进了弹层或别处——
        // 正常交互期间焦点始终在终端/视图上。此时任何按键都先收口弹层并把焦点还给
        // 终端,保证输入永远可自行恢复,而不是只能关标签页重连。
        if (SuggestPopup.IsOpen && !IsFocused && _termControl is { IsFocused: false })
        {
            SuggestDiag.Log("self-heal", $"view={GetHashCode():X} key={e.Key} focus escaped -> dismiss+refocus");
            DismissSuggestions(suppress: false);
            FocusTerminal();
        }

        // 补全弹层的键位优先(↑↓/Tab/Esc),否则方向键会被终端吃掉发成 ESC 序列。
        if (HandleSuggestionKey(e))
        {
            return;
        }

        // 终端内搜索栏(spec §5.3),出厂 Ctrl+F。键位走键位表:解绑之后 ^F 原样交给远端
        // (vim / less 的下翻页、readline 的前进一个字符)。
        if ((ShortcutKeymapService.Resolve()?.Current ?? ShortcutKeymap.Default).Matches("search.terminal", e))
        {
            OpenSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchBar.IsVisible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }
        if (DataContext is not TerminalTabViewModel vm)
        {
            return;
        }

        // Esc 关闭一个已经断开的标签页。条件挂在覆盖层可见性上而非单看连接状态:
        // 连接正常时 Esc 必须原样透传给远端(vim/less 等重度依赖),只有覆盖层挡在
        // 终端前面、按键本就到不了 PTY 时才改写它的语义。
        if (e is { Key: Key.Escape, KeyModifiers: Avalonia.Input.KeyModifiers.None }
            && vm.ShowDisconnectedOverlay)
        {
            vm.RequestClose();
            e.Handled = true;
            return;
        }
        if (vm.ConnectionStatus != SessionStatus.Disconnected)
        {
            return;
        }
        bool reconnect =
            e
            is { Key: Key.Enter, KeyModifiers: Avalonia.Input.KeyModifiers.None }
                or { Key: Key.R, KeyModifiers: Avalonia.Input.KeyModifiers.Control };
        if (!reconnect)
        {
            return;
        }
        vm.RequestReconnect();
        e.Handled = true;
    }

    // ---- 命令补全弹层(plan.md #16) ----------------------------------------

    /// <summary>补全数据接线的目标 VM(视图被 Dock 回收复用,随 DataContext 重挂)。</summary>
    private TerminalTabViewModel? _suggestVm;

    private IReadOnlyList<CommandSuggestion> _suggestions = [];
    private string? _suppressedInput;
    private int _suggestSeq;

    // 建议查询防抖:每个可打印字节都触发 InputChanged,连打时不该每键都对
    // 500 条历史全量过滤 + 排序 + 分配一轮(幽灵文本的本地即时消耗不受影响,手感不变)。
    private IDisposable? _suggestDebounce;
    private const int SuggestDebounceMs = 90;

    /// <summary>幽灵文本对应的完整候选命令(接受时据此设置抑制值);null 即无幽灵。</summary>
    private string? _ghostFull;

    /// <summary>幽灵文本对应的那条候选(接受带变量的快捷命令时要问变量);与 <see cref="_ghostFull" /> 同生共灭。</summary>
    private CommandSuggestion? _ghostSuggestion;

    private void HookSuggestions()
    {
        _suggestVm?.InputTracker.InputChanged -= OnTrackedInputChanged;
        _suggestVm?.Disconnected -= OnSuggestVmDisconnected;
        _suggestVm = DataContext as TerminalTabViewModel;
        _suggestVm?.InputTracker.InputChanged += OnTrackedInputChanged;
        _suggestVm?.Disconnected += OnSuggestVmDisconnected;
        SuggestDiag.Log(
            "hook",
            $"view={GetHashCode():X} vm={_suggestVm?.GetHashCode():X} provider={(_suggestVm?.SuggestionProvider is null ? "null" : "ok")}"
        );
        DismissSuggestions(suppress: false);
    }

    /// <summary>断连横幅会移动光标,残留的幽灵会被画到新光标处——断连即收口。</summary>
    private void OnSuggestVmDisconnected(object? sender, EventArgs e)
    {
        ClosePasswordPopup();
        DismissSuggestions(suppress: false);
    }

    /// <summary>统一收起智能提示:弹层与幽灵文本同生共灭,防止各清一半的残留。</summary>
    private void DismissSuggestions(bool suppress)
    {
        CloseSuggestPopup(suppress);
        ClearGhost();
    }

    private void ClearGhost()
    {
        _ghostFull = null;
        _ghostSuggestion = null;
        _termControl?.ClearGhostSuggestion();
    }

    private void OnTrackedInputChanged()
    {
        ClosePasswordPopup(suppress: true);
        string input = EffectiveInput();
        // 每键路径:先查开关再拼实参(急切求值防线,见 SuggestDiag.IsEnabled)。
        if (SuggestDiag.IsEnabled)
        {
            SuggestDiag.Log(
                "changed",
                $"""
                view={GetHashCode():X} attached={IsAttachedToVisualTree()} known="{_suggestVm?.InputTracker.CurrentInput
                    ?? "<unknown>"}" effective="{input}" suppressed="{_suppressedInput}"
                """
            );
        }
        // 程序注入(快捷命令下发)不是用户键入:收起并记为已抑制,待用户继续
        // 编辑(输入内容变化)后再恢复正常补全。
        if (_suggestVm?.IsProgrammaticInput == true)
        {
            CloseSuggestPopup(suppress: true);
            ClearGhost();
            return;
        }
        // 备用屏(vim/htop 等全屏程序)内不做命令补全。
        if (_termControl?.IsAlternateScreenActive == true)
        {
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        if (string.IsNullOrEmpty(input) || input == _suppressedInput || IsInteractivePromptLine())
        {
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        _suppressedInput = null;

        // 键入/退格仍与当前候选同前缀时,保持候选不动:控件在每帧按真实光标列自行切出
        // 剩余部分(见 CurrentGhostRemainder),内容与位置同源于回显后的光标,恒不失步——
        // 故这里不再逐键推送剩余文本(那会让缩短/增长的幽灵在回显前画到旧光标处而抖动/卡顿)。
        // InputChanged 在"发往 PTY"时同步触发、早于回显,因此一旦键入偏离候选(下面 else),
        // 会在该帧回显重绘之前就清除,不会闪出错位的幽灵。未知态(按过方向键)下 EffectiveInput
        // 降级为试探段,同样按"紧贴光标之前"安全地追加式提示。
        if (
            _ghostFull is { } full
            && full.Length > input.Length
            && full.StartsWith(input, StringComparison.Ordinal)
            && !HasTextRightOfCursor()
        )
        {
            // 保持:控件持有完整候选与锚列,重绘即现切,无需在此推送或重绘。
        }
        else
        {
            ClearGhost();
        }

        // 面板未开时结果落到幽灵文本;面板已开(Alt+Enter 召出)则持续跟随键入刷新列表。
        // 查询防抖:停键 90ms 后才真正打提供者;输入与面板状态在触发时重新取,
        // 避免用调度时刻的陈旧快照查询。
        _suggestDebounce?.Dispose();
        _suggestDebounce = DispatcherTimer.RunOnce(() =>
        {
            _suggestDebounce = null;
            string current = EffectiveInput();
            if (string.IsNullOrEmpty(current) || current == _suppressedInput)
            {
                return;
            }
            bool panelOpen = SuggestPopup.IsOpen;
            _ = UpdateSuggestionsAsync(current, panelOpen ? 20 : 8, openPanel: panelOpen);
        }, TimeSpan.FromMilliseconds(SuggestDebounceMs));
    }

    /// <summary>
    /// 光标右侧同一行是否还有非空白内容(行中编辑、zsh 右提示符等):有则不显示
    /// 幽灵文本——叠画会盖住既有内容,且此时"补全在行尾"的语义不成立。
    /// 宽字符行的列→索引映射有偏差,保守放行(测不准时宁可显示)。
    /// </summary>
    private bool HasTextRightOfCursor()
    {
        if (_termControl is null)
        {
            return false;
        }
        try
        {
            string line = _termControl.GetBufferLine(_termControl.CursorRow);
            int col = _termControl.CursorCol;
            return line.Length > col && !string.IsNullOrWhiteSpace(line[col..]);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 交互提示行不弹命令补全(sudo 密码提示、apt 的 "[Y/n]" 确认行下按键
    /// 仍弹智能提示)。把光标行末尾已回显的输入剥掉后,按三类特征判定"此刻不是在编写
    /// shell 命令,而是在回答程序的提问":
    ///   ① 密码/口令/验证码 —— 冒号(全/半角)结尾 + 密码类关键词;
    ///   ② 是否类确认      —— 结尾括号选项 [Y/n]/(yes/no)/[Y/I/N/O/D/Z]/[是/否],
    ///                        或整行以问号结尾(cp/rm 的 "overwrite 'x'?"/"remove 'x'?");
    ///   ③ 编号选择菜单    —— 冒号结尾 + "选择/编号/选项/number/selection/choice" 等词
    ///                        (update-alternatives、tzdata、npm init 的序号选单)。
    /// 这些场景把整条历史命令塞进程序输入里有害无益,一律不弹(含 Alt+Enter 主动召出)。
    /// </summary>
    private bool IsInteractivePromptLine()
    {
        if (_termControl is null)
        {
            return false;
        }
        try
        {
            return InteractivePromptDetector.IsInteractivePrompt(
                _termControl.GetBufferLine(_termControl.CursorRow),
                EffectiveInput()
            );
        }
        catch
        {
            return false; // 读缓冲失败(极端竞态):宁可照常弹出,不影响正常补全。
        }
    }

    /// <summary>
    /// 建议所依据的"当前输入":确定态用整行;未知态(按过方向键/F 键后)降级为
    /// 自最后一个控制键以来键入的字符段——建议不因一次按键失灵,补全只追加/等量回删,
    /// 永远与屏幕一致。
    /// </summary>
    private string EffectiveInput() =>
        _suggestVm?.InputTracker.CurrentInput
        ?? _suggestVm?.InputTracker.TentativeRun
        ?? string.Empty;

    private bool IsAttachedToVisualTree() => VisualRoot is not null;

    private async Task UpdateSuggestionsAsync(string prefix, int max, bool openPanel)
    {
        if (_suggestVm?.SuggestionProvider is not { } provider)
        {
            return;
        }
        int seq = ++_suggestSeq;
        try
        {
            IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync(
                prefix,
                max
            );

            // 建议查询可能异步恢复;UI 变更强制回到 UI 线程(fire-and-forget 场景下
            // 线程亲和性异常会被静默吞掉,表现为"弹层时灵时不灵")。
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ApplySuggestions(seq, items, openPanel));
                return;
            }
            ApplySuggestions(seq, items, openPanel);
        }
        catch
        {
            // 建议是锦上添花,任何失败都只安静收起,不得污染后续键入的状态。
            CloseSuggestPopup(suppress: false);
            ClearGhost();
        }
    }

    private void ApplySuggestions(int seq, IReadOnlyList<CommandSuggestion> items, bool openPanel)
    {
        // 键入速度快于查询时只应用最后一次结果;期间行内容变了(seq 落后)直接丢弃。
        if (seq != _suggestSeq)
        {
            SuggestDiag.Log("apply", $"seq {seq} stale (now {_suggestSeq})");
            return;
        }
        if (items.Count == 0)
        {
            SuggestDiag.Log("apply", "0 items -> close");
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        // 结果落地时输入已被抑制(接受补全/程序注入赛过了在途查询):丢弃,不重开。
        string input = EffectiveInput();
        if (input == _suppressedInput)
        {
            SuggestDiag.Log("apply", "input suppressed -> drop");
            return;
        }
        if (!openPanel)
        {
            ApplyGhost(input, items);
            return;
        }
        // 查询在途期间窗口失活(切去截图/别的应用):不得把弹层开到别的程序上面,
        // 也不得在自己被系统隐藏时开出"看不见却拦键"的僵尸弹层——直接丢弃。
        if (_hostWindow is { IsActive: false })
        {
            SuggestDiag.Log("apply", "host window inactive -> drop, no open");
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        ClearGhost();
        _suggestions = items;
        SuggestList.ItemsSource = items;
        SuggestList.SelectedIndex = 0;
        SuggestPopup.PlacementTarget ??= TerminalHost;
        if (_termControl is not null)
        {
            // 锚定在输入起点列(而非光标列):面板不随键入逐列漂移,与主流终端一致。
            // 与光标行的缝照密码弹层的做法留在锚点矩形上(见 CursorPopupGap),底下放不下翻到上方时也不压进光标行。
            SuggestPopup.PlacementRect = _termControl.GetCursorRect(input.Length).Inflate(new Thickness(0, CursorPopupGap));
        }
        SuggestPopup.IsOpen = true;
        SuggestDiag.Log(
            "apply",
            $"opened with {items.Count} items, isOpen={SuggestPopup.IsOpen}, target={(SuggestPopup.PlacementTarget is null ? "null" : "ok")}"
        );
    }

    /// <summary>
    /// 幽灵文本模式:取首个与当前输入严格同前缀(区分大小写——剩余部分要原样写入
    /// shell)且更长的候选,把剩余部分叠画在光标后;无合格候选则清除。
    /// </summary>
    private void ApplyGhost(string input, IReadOnlyList<CommandSuggestion> items)
    {
        if (_termControl is null || input.Length == 0 || HasTextRightOfCursor())
        {
            ClearGhost();
            return;
        }
        foreach (CommandSuggestion item in items)
        {
            if (
                item.Text.Length > input.Length
                && item.Text.StartsWith(input, StringComparison.Ordinal)
            )
            {
                _ghostFull = item.Text;
                _ghostSuggestion = item;
                // 只交完整候选;剩余部分由控件每帧按屏上"光标左侧已回显文本"现算(见
                // CurrentGhostRemainder),不再逐键回推,故键入/退格/空格均恒贴光标、不受回显延迟影响。
                _termControl.SetGhostSuggestion(item.Text);
                SuggestDiag.Log("ghost", $"\"{item.Text}\" remainder {item.Text.Length - input.Length} chars");
                return;
            }
        }
        ClearGhost();
    }

    /// <summary>
    /// 弹层被平台侧关闭后的对账:清掉本类残留的列表与幽灵,避免 IsOpen 已 false
    /// 而键位分支仍按"面板开着"决策;若焦点被弹层带走,交还终端。
    /// </summary>
    private void OnSuggestPopupClosedByPlatform()
    {
        SuggestDiag.Log(
            "popup-closed",
            $"view={GetHashCode():X} termFocused={_termControl?.IsFocused} attached={IsAttachedToVisualTree()}"
        );
        _suggestions = [];
        ClearGhost();
        if (_termControl is { IsFocused: false } && IsAttachedToVisualTree())
        {
            FocusTerminal();
        }
    }

    private void CloseSuggestPopup(bool suppress)
    {
        if (suppress)
        {
            _suppressedInput = EffectiveInput();
        }
        if (SuggestPopup.IsOpen)
        {
            // 只记真实的开→关转换,不记每键空转(诊断关闭时本行零开销)。
            SuggestDiag.Log("close", $"view={GetHashCode():X} suppress={suppress}");
            SuggestPopup.IsOpen = false;
        }
        _suggestions = [];
    }

    /// <summary>补全弹层键位;返回 true 表示按键已被弹层消费。</summary>
    private bool HandleSuggestionKey(KeyEventArgs e)
    {
        // Alt+Enter:主动弹出补全面板(空输入 = 快捷命令全量 + 最近历史)。
        // 交互提示行(密码/是否确认/编号选单)与备用屏连主动召出也不给:把整条
        // 历史命令插进程序输入里有害无益。
        if (e is { Key: Key.Enter, KeyModifiers: Avalonia.Input.KeyModifiers.Alt })
        {
            if (IsInteractivePromptLine() || _termControl?.IsAlternateScreenActive == true)
            {
                e.Handled = true;
                return true;
            }
            _suppressedInput = null;
            _ = UpdateSuggestionsAsync(EffectiveInput(), 20, openPanel: true);
            e.Handled = true;
            return true;
        }
        if (!SuggestPopup.IsOpen)
        {
            // → / End 接受幽灵文本(fish/zsh-autosuggestions 语义)。幽灵只在确定态
            // 展示,此时光标必在行尾——行尾的 → 在 shell 里本就是空操作,不会误伤移动。
            if (
                e.KeyModifiers == Avalonia.Input.KeyModifiers.None
                && e.Key is Key.Right or Key.End
                && _ghostFull is not null
                && _termControl?.CurrentGhostRemainder() is { Length: > 0 } remainder
            )
            {
                // 幽灵是一条带 {{变量}} 的快捷命令:与面板里选中它同一条路,先问变量再写。
                if (_ghostSuggestion is { HasVariables: true } ghostCommand && _ghostSuggestion.Text == _ghostFull)
                {
                    ClearGhost();
                    if (TryAcceptWithVariables(ghostCommand))
                    {
                        e.Handled = true;
                        return true;
                    }
                }
                // 不设抑制:接受后 InputChanged 里发出的查询若命中更长的候选
                // (如 "git checkout" → "git checkout -b"),继续以幽灵形式接力提示
                // (fish 语义);幽灵只显示严格更长的候选,不会原样重现。
                ClearGhost();
                _termControl.WriteInput(_termControl.SessionEncoding.GetBytes(remainder));
                e.Handled = true;
                return true;
            }
            return false;
        }
        switch (e.Key)
        {
            case Key.Down:
                MoveSuggestion(+1);
                e.Handled = true;
                return true;
            case Key.Up:
                MoveSuggestion(-1);
                e.Handled = true;
                return true;
            case Key.Escape:
                CloseSuggestPopup(suppress: true);
                e.Handled = true;
                return true;
            case Key.C when e.KeyModifiers == Avalonia.Input.KeyModifiers.Control:
                // Ctrl+C = 取消当前行,面板一并收口(#315)。不置 Handled、返回 false:
                // 按键必须照常下发(中断前台进程;开了「有选区时 Ctrl+C 复制」则是复制)。
                // 走 InputChanged 那条路收口在这里不够 —— 复制语义下根本没有字节发往 PTY。
                DismissSuggestions(suppress: false);
                return false;
            case Key.Enter when e.KeyModifiers == Avalonia.Input.KeyModifiers.None:
                // 弹层存在时 Enter 始终输入当前选中项(VS 语义,无需先按方向键;
                // 不想要建议按 Esc 退出)。唯一例外:选中项与已输入完全相同
                // (如手动敲完了整条命令)→ 直通 shell 执行,免去二次回车。
                if (
                    SuggestList.SelectedItem is CommandSuggestion selected
                    && selected.Text != EffectiveInput()
                )
                {
                    AcceptSuggestion();
                    e.Handled = true;
                    return true;
                }
                CloseSuggestPopup(suppress: false);
                return false;
            default:
                // 其余按键(含 Tab)直通终端:Tab 保留给 shell 的原生补全,
                // 行内容随之不可知,弹层会经 InputChanged 自动收起。
                return false;
        }
    }

    private void MoveSuggestion(int delta)
    {
        if (_suggestions.Count == 0)
        {
            return;
        }
        int next =
            ((SuggestList.SelectedIndex + delta) % _suggestions.Count + _suggestions.Count)
            % _suggestions.Count;
        SuggestList.SelectedIndex = next;
        SuggestList.ScrollIntoView(next);
    }

    private void AcceptSuggestion()
    {
        if (_termControl is null || SuggestList.SelectedItem is not CommandSuggestion suggestion)
        {
            return;
        }
        CloseSuggestPopup(suppress: false);
        ClearGhost();
        if (TryAcceptWithVariables(suggestion))
        {
            return;
        }
        WriteCompletion(suggestion.Text);
        FocusTerminal();
    }

    /// <summary>
    /// 把选中的候选续写进命令行:与已键入的同前缀就只写剩下的部分,不同前缀就先回删再写整条。
    /// </summary>
    /// <remarks>
    /// 经用户输入通道写入(shell 负责回显),跟踪器同路径感知,行状态保持一致。
    /// 不设抑制:面板已关,WriteInput 触发的查询结果只会落到幽灵文本,且幽灵
    /// 只显示严格更长的候选——接受后若存在更长的延续(参数、子选项)就接力提示。
    /// </remarks>
    /// <param name="text">要落到命令行上的完整文本。</param>
    private void WriteCompletion(string text)
    {
        if (_termControl is null)
        {
            return;
        }
        string current = EffectiveInput();
        string payload = text.StartsWith(current, StringComparison.Ordinal)
            ? text[current.Length..]
            // 候选与已输入不同前缀:回删已键入的字符再写入完整候选(shell 每次 DEL 删一个字符)。
            : new string('\x7f', current.Length) + text;
        if (payload.Length > 0)
        {
            _termControl.WriteInput(_termControl.SessionEncoding.GetBytes(payload));
        }
    }

    /// <summary>
    /// 候选是一条带 <c>{{变量}}</c> 的快捷命令时,先弹与快捷命令区同一个询问框(预填上一次的值),
    /// 填完再把替换好的命令续写进命令行;返回 false 表示不需要问(照常按原文插入)。
    /// </summary>
    /// <remarks>
    /// 原先插入的是含占位的原文(§132 记过这条):补全按前缀续写,而用户键入的前缀几乎总在占位之前,
    /// 替换之后照样对得上;对不上时 <see cref="WriteCompletion" /> 回删整段再写,与别的候选一样。
    /// 弹框期间标签可能断开、视图可能被停靠层换了数据上下文:回来时都要再核一遍,不对就一个字节都不写。
    /// 取消同样一个字节都不写。
    /// </remarks>
    private bool TryAcceptWithVariables(CommandSuggestion suggestion)
    {
        if (!suggestion.HasVariables
            || _suggestVm is not { QuickCommandTextResolver: { } resolve } vm
            || suggestion.Command is not { } command)
        {
            return false;
        }
        // 询问框是另一个窗口:用户在那儿敲的字不会进终端,输入跟踪器不受影响。
        // 记下此刻的输入,回来时据此判断命令行在弹框期间有没有变过(远端自己清了行、重连…)。
        string inputBefore = EffectiveInput();
        QuickCommandAcceptForTest = AcceptWithVariablesAsync();
        return true;

        async Task AcceptWithVariablesAsync()
        {
            string? text = null;
            try
            {
                text = await resolve(command);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 询问框本身出了错(窗口正在关之类):当作取消,别把含占位的原文写进去。
                System.Diagnostics.Trace.WriteLine($"[VelaShell] Quick command variables prompt failed: {ex.Message}");
            }
            if (text is not null
                && ReferenceEquals(vm, _suggestVm)
                && vm.IsConnected
                && EffectiveInput() == inputBefore)
            {
                WriteCompletion(text);
            }
            if (ReferenceEquals(vm, _suggestVm) && IsAttachedToVisualTree())
            {
                FocusTerminal();
            }
        }
    }

    internal void OpenSearch()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        RunSearch();
    }

    private void CloseSearch()
    {
        // DispatcherTimer 被调度器强引用,不停表会让关掉的搜索栏继续按时唤醒本视图。
        _searchDebounce?.Stop();
        SearchBar.IsVisible = false;
        _searchHits = [];
        _searchIndex = -1;
        _termControl?.ClearSearchHighlights();
        FocusTerminal();
    }

    /// <summary>
    /// 搜索防抖间隔。<see cref="RunSearch" /> 要在 UI 线程上扫过整个缓冲区(回滚 + 屏幕,
    /// 默认上限 1 万行);逐键直跑会让长会话下搜索框自己打字都发卡。取 150ms:低于连续
    /// 击键的间隔,又短到松手即出结果。
    /// </summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(150);

    private DispatcherTimer? _searchDebounce;

    /// <summary>把一次搜索排进防抖窗口;窗口内的后续击键只是把它往后推。</summary>
    private void ScheduleSearch()
    {
        if (_searchDebounce is null)
        {
            _searchDebounce = new(SearchDebounce, DispatcherPriority.Input, (_, _) => RunSearch());
        }
        else
        {
            _searchDebounce.Stop(); // 重启计时 = 推迟到最后一次击键之后。
        }
        _searchDebounce.Start();
    }

    /// <summary>
    /// 立即跑掉待处理的防抖搜索。Enter(下一处)/Esc 之类需要"此刻的结果"的动作先调它,
    /// 否则会拿着上一次击键前的命中集做导航。
    /// </summary>
    private void FlushPendingSearch()
    {
        if (_searchDebounce is { IsEnabled: true })
        {
            _searchDebounce.Stop();
            RunSearch();
        }
    }

    private void RunSearch()
    {
        _searchDebounce?.Stop(); // 直接调用(打开搜索栏)时也要吃掉待处理的那一次。
        if (_termControl is null || !SearchBar.IsVisible)
        {
            return;
        }
        string query = SearchBox.Text ?? string.Empty;
        _searchHits = _termControl.SearchBuffer(query);
        _searchIndex = _searchHits.Count > 0 ? 0 : -1;
        ShowCurrentHit();
    }

    private void MoveHit(int delta)
    {
        FlushPendingSearch(); // 导航必须基于当前输入的命中集,不能用防抖窗口前的旧结果。
        if (_searchHits.Count == 0)
        {
            return;
        }
        _searchIndex =
            (((_searchIndex + delta) % _searchHits.Count) + _searchHits.Count) % _searchHits.Count;
        ShowCurrentHit();
    }

    private void ShowCurrentHit()
    {
        SearchCount.Text =
            _searchHits.Count == 0
                ? string.IsNullOrEmpty(SearchBox.Text)
                    ? ""
                    : Strings.Get("Term_NoMatches")
                : $"{_searchIndex + 1}/{_searchHits.Count}";

        // 所有命中项均保持高亮;当前项以 accent 着色(§5.3)。
        _termControl?.SetSearchHighlights(_searchHits, _searchIndex);
        if (_searchIndex >= 0 && _termControl is not null)
        {
            _termControl.ShowHit(_searchHits[_searchIndex]);
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveHit(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    /// <summary>宿主窗口(挂接失活事件用;弹层是独立顶层,不随主窗口失活自动收起)。</summary>
    private Window? _hostWindow;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HookTerminalControl();
        // Dock 回收复用视图时 DataContext 可能经历 null→原值的往返,这里重挂一次
        // 补全接线,保证切换标签后建议仍然工作(幂等:先退订再订阅)。
        HookSuggestions();

        // 切到其他应用时收起补全弹层——Popup 是独立顶层窗口,不主动关就会
        // 悬浮在别的程序上面。
        _hostWindow = TopLevel.GetTopLevel(this) as Window;
        _passwordHost?.PropertyChanged -= OnPasswordHostChanged;
        _passwordHost = _hostWindow?.DataContext as MainWindowViewModel;
        _passwordHost?.PropertyChanged += OnPasswordHostChanged;
        _hostWindow?.Deactivated += OnHostWindowDeactivated;
        // 重新激活时兜底恢复:系统截图覆盖层(Win+Shift+S)偶尔不触发 Deactivated,
        // 弹层就会保持打开、把焦点困在自己的顶层窗口里,导致终端"输入死亡、内容被
        // 灰色残影盖住",只能关标签重开(时序竞态,非必现)。
        // 截完图切回本窗口时必然重新激活——这一刻不管失活有没有触发过,只要弹层还开
        // 着就统一收口并把焦点还给终端。此路径不依赖 Deactivated,也不依赖按键能到达
        // 本视图(焦点困在弹层时按键根本到不了),是该竞态的唯一可靠恢复点。
        _hostWindow?.Activated += OnHostWindowActivated;
        FocusTerminal();
    }

    private void OnHostWindowDeactivated(object? sender, EventArgs e)
    {
        ClosePasswordPopup();
        SuggestDiag.Log("deactivated", $"view={GetHashCode():X} popupOpen={SuggestPopup.IsOpen}");
        DismissSuggestions(suppress: false);
    }

    /// <summary>
    /// 窗口重新激活时的双重兜底(时序竞态,非必现):
    ///
    /// ① 强制终端重绘 —— 根因:系统截图覆盖层(Win+Shift+S)+ 微信粘贴会让窗口某个
    ///    矩形区域在 DWM 层被置脏并要求重绘,而空闲终端(无输出、失焦后连光标闪烁的
    ///    周期重绘也停了)没有任何 visual 被标脏,那块区域就不重绘,露出下层 surface
    ///    底色,表现为"内容变空白/一块灰"。用户从覆盖窗口点回本窗口时窗口必然重新
    ///    激活——此刻强制终端跑一遍 Render(呈现一整帧新画面到 DWM)即可消除残影。
    ///    点击终端本会经 OnGotFocus 重绘,但失活前后焦点未变(终端一直"持有"焦点),
    ///    OnGotFocus 不重新触发,故必须在激活时补这一刀。立即 + 延迟各一次:后者兜住
    ///    "激活事件早于 DWM 完成表面恢复"的时序。
    ///
    /// ② 收口僵尸弹层 —— 弹层仍开着说明它熬过了切走(失活未触发或未收口),一并收口
    ///    并把焦点还给终端,让可能被困的输入自动复活,无需关标签重连。
    /// </summary>
    private void OnHostWindowActivated(object? sender, EventArgs e)
    {
        SuggestDiag.Log(
            "activated",
            $"view={GetHashCode():X} popupOpen={SuggestPopup.IsOpen} termFocused={_termControl?.IsFocused} -> force repaint"
        );

        // ① 立即重绘终端 + 下一拍强制整棵可视树重绘(DWM 表面恢复可能晚于激活事件;
        //    整树重绘确保合成器一定提交一整帧新画面到 DWM,兜住单控件 InvalidateVisual
        //    被"内容未变"优化掉、或空白区跨出终端边界的情形)。激活不频繁,代价可忽略。
        _termControl?.InvalidateTerminal();
        DispatcherTimer.RunOnce(ForceFullRepaint, TimeSpan.FromMilliseconds(120));

        // ② 顺带收口可能残留的僵尸弹层(内容消失常与输入死亡耦合出现)。
        if (SuggestPopup.IsOpen)
        {
            DismissSuggestions(suppress: false);
            FocusTerminal();
        }
    }

    /// <summary>
    /// 强制宿主窗口整棵可视树重绘:遍历所有可视后代逐个 <see cref="Visual.InvalidateVisual" />,
    /// 逼合成器提交一整帧新画面到 DWM。用于截图覆盖层导致某区域"内容消失/残留灰块"后的恢复。
    /// </summary>
    private void ForceFullRepaint()
    {
        if (_hostWindow is not { } window)
        {
            _termControl?.InvalidateTerminal();
            return;
        }
        window.InvalidateVisual();
        foreach (Visual visual in window.GetVisualDescendants())
        {
            visual.InvalidateVisual();
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ClosePasswordPopup();
        _suggestDebounce?.Dispose();
        _suggestDebounce = null;
        DismissSuggestions(suppress: false);
        // 脱离可视树后不该再响应跟踪器(重新 attach 时 HookSuggestions 会幂等重订)。
        _suggestVm?.InputTracker.InputChanged -= OnTrackedInputChanged;
        _suggestVm?.Disconnected -= OnSuggestVmDisconnected;
        _hostWindow?.Deactivated -= OnHostWindowDeactivated;
        _hostWindow?.Activated -= OnHostWindowActivated;
        _hostWindow = null;
        _passwordHost?.PropertyChanged -= OnPasswordHostChanged;
        _passwordHost = null;
        if (_termControl is not null)
        {
            _termControl.OutputUpdated -= OnPasswordOutputUpdated;
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
            _termControl.LostFocus -= OnTerminalLostFocus;
            _termControl.LayoutPaddingChanged -= OnTerminalPaddingChanged;
        }
        _termControl = null;
    }

    /// <summary>
    /// 终端失焦(点击 SFTP 面板/侧边栏等)时收起弹层与幽灵;建议列表项不可聚焦,
    /// 因此点击建议项本身不会触发这里。
    /// </summary>
    private void OnTerminalLostFocus(object? sender, RoutedEventArgs e)
    {
        ClosePasswordPopup();
        DismissSuggestions(suppress: false);
    }

    // 终端控件是跨分屏重新挂载的单一共享实例,因此每个视图都把滚动条(重新)绑定到它
    // 当前承载的那个控件上。
    /// <summary>把文本写进系统剪贴板;拿不到剪贴板(无 TopLevel)时静默跳过。</summary>
    private async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void HookTerminalControl()
    {
        var ctrl =
            (DataContext as TerminalTabViewModel)?.TerminalEmulator.Control as VelaTerminalControl;
        if (ReferenceEquals(ctrl, _termControl))
        {
            SyncScrollBar();
            return;
        }
        if (_termControl is not null)
        {
            _termControl.OutputUpdated -= OnPasswordOutputUpdated;
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
            _termControl.LostFocus -= OnTerminalLostFocus;
            _termControl.LayoutPaddingChanged -= OnTerminalPaddingChanged;
            // 幽灵是画在控件上的覆盖层:换出的旧控件若在别的面板仍可见,
            // 不清会把幽灵永久残留在旧光标处。
            _termControl.ClearGhostSuggestion();
        }
        _ghostFull = null;
        _ghostSuggestion = null;
        _termControl = ctrl;
        if (_termControl is not null)
        {
            _termControl.OutputUpdated += OnPasswordOutputUpdated;
            _termControl.ScrollChanged += OnTerminalScrollChanged;
            _termControl.LostFocus += OnTerminalLostFocus;
            _termControl.LayoutPaddingChanged += OnTerminalPaddingChanged;
            SyncScrollBarGeometry();
        }
        SyncScrollBar();
    }

    /// <summary>
    /// 让覆盖式滚动条正好铺满终端正文右侧那条留白带(RightPadding 已从列数里扣掉),
    /// 于是它不再压住文字(#117);用户内边距(设置 → 终端 → 内边距)则以外边距让开,
    /// 使滚动条始终紧贴正文而非窗口边缘(#227)。几何全部取自控件,单一事实来源。
    /// </summary>
    private void SyncScrollBarGeometry()
    {
        if (ScrollBarView is null || _termControl is null)
        {
            return;
        }
        double pad = _termControl.ContentPadding;
        ScrollBarView.Width = _termControl.RightPadding;
        ScrollBarView.Margin = new(0, pad, pad, pad);
    }

    private void OnTerminalPaddingChanged() => SyncScrollBarGeometry();

    private void OnTerminalScrollChanged()
    {
        ClosePasswordPopup();
        SyncScrollBar();
    }

    private void SyncScrollBar()
    {
        if (ScrollBarView is null || _termControl is null)
        {
            return;
        }
        _syncingScrollBar = true;
        try
        {
            int max = _termControl.MaxScrollOffset;
            ScrollBarView.Maximum = max;
            ScrollBarView.ViewportSize = Math.Max(1, _termControl.Rows);
            // 跟随实时输出时(偏移 0)滑块位于底部,完全回滚到历史时位于顶部。
            ScrollBarView.Value = max - _termControl.ScrollOffset;
            ScrollBarView.IsEnabled = max > 0;
            // 滚动条覆盖在终端右边缘;没有回滚内容时无可滚动项,因此整体隐藏,
            // 而非显示禁用的滑块。
            ScrollBarView.IsVisible = max > 0;
        }
        finally
        {
            _syncingScrollBar = false;
        }
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        if (_syncingScrollBar || _termControl is null || ScrollBarView is null)
        {
            return;
        }
        int max = _termControl.MaxScrollOffset;
        _termControl.ScrollOffset = max - (int)Math.Round(ScrollBarView.Value);
    }

    /// <summary>指针按下时将焦点交还终端,确保后续键入直接进入 PTY。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        FocusTerminal();
        base.OnPointerPressed(e);
    }

    /// <summary>
    /// 标签视图持有焦点时的快捷键处理:复制/粘贴/发送中断与终端控件行为保持一致;
    /// 全局动作(新建/关闭/切换标签、打开设置)经快捷键服务解析后转发到主窗口命令。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        KeyModifiers modifiers = MapModifiers(e.KeyModifiers);
        KeyCode key = MapKey(e.Key);
        if (key == KeyCode.None)
        {
            base.OnKeyDown(e);
            return;
        }
        ShortcutAction action = _shortcutService.Resolve(modifiers, key, ShortcutContext.Terminal);
        switch (action)
        {
            // 本层是焦点落在标签视图(而非终端控件)时的回退:正常情况下终端控件自己的
            // OnKeyDown 已处理这些键。回退层必须与控件行为一致地跟随设置,否则同一个
            // 快捷键会因焦点位置不同而表现不同(选中即复制/Ctrl+C 不受控)。
            case ShortcutAction.Copy:
                // 复用控件的复制:只复制选中内容,并尊重「复制时去除尾部空格」等设置。
                if (_termControl is not null)
                {
                    _ = _termControl.CopyAsync();
                }
                else
                {
                    _ = CopySelectionAsync();
                }
                e.Handled = true;
                return;
            case ShortcutAction.Paste:
                if (_termControl is not null)
                {
                    _ = _termControl.PasteAsync();
                }
                else
                {
                    _ = PasteFromClipboardAsync();
                }
                e.Handled = true;
                return;
            case ShortcutAction.SendInterrupt:
                // 与 VelaTerminalControl.OnKeyDown 同规则:「选中时 Ctrl+C 复制」开启且有
                // 选区 → 复制;否则发送中断信号 ^C。
                if (_termControl?.TryCopyOnCtrlC() != true)
                {
                    SendBytesToTerminal([0x03]);
                }
                e.Handled = true;
                return;
            case ShortcutAction.NewTab:
            case ShortcutAction.CloseTab:
            case ShortcutAction.NextTab:
            case ShortcutAction.PreviousTab:
            case ShortcutAction.OpenSettings:
                if (ExecuteGlobalShortcut(action))
                {
                    e.Handled = true;
                    return;
                }
                break;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// 把快捷键服务解析出的全局动作接到主窗口的既有命令上,与 MainWindow.KeyBindings
    /// 同源。经服务解析而非写死手势,macOS 上才是 Cmd+T/W/, 而非 Ctrl。找不到宿主 VM
    /// 时返回 false,让事件继续冒泡由 Window.KeyBindings 兜底,避免双重执行。
    /// </summary>
    private bool ExecuteGlobalShortcut(ShortcutAction action)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel vm)
        {
            return false;
        }
        switch (action)
        {
            case ShortcutAction.NewTab:
                return vm.Commands.Execute("session.new");
            case ShortcutAction.CloseTab:
                vm.CloseActiveTabCommand.Execute().Subscribe();
                return true;
            case ShortcutAction.NextTab:
                vm.NextTabCommand.Execute().Subscribe();
                return true;
            case ShortcutAction.PreviousTab:
                vm.PreviousTabCommand.Execute().Subscribe();
                return true;
            case ShortcutAction.OpenSettings:
                vm.OpenSettingsCommand.Execute().Subscribe();
                return true;
            default:
                return false;
        }
    }

    /// <summary>将文本输入按会话字符集编码后转发到终端。</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            SendTextToTerminal(e.Text);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    /// <summary>
    /// 按会话字符集编码后送进终端的输入通道(键入与粘贴共用)。
    /// </summary>
    /// <remarks>
    /// 编码只能问终端要:它是解码对端输出的那一套,两边必须同源 —— 各写一个 UTF-8
    /// 就是本次要修的那个 bug 的由来。
    /// </remarks>
    private void SendTextToTerminal(string text)
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            vm.TerminalEmulator.WriteInput(vm.TerminalEmulator.SessionEncoding.GetBytes(text));
        }
    }

    /// <summary>送一段已经成型的控制字节(^C 这类);它们不是文本,不经字符集。</summary>
    private void SendBytesToTerminal(byte[] data)
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            vm.TerminalEmulator.WriteInput(data);
        }
    }

    private async Task CopySelectionAsync()
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard == null)
        {
            return;
        }
        string selectedText = GetSelectedText();
        if (!string.IsNullOrEmpty(selectedText))
        {
            await clipboard.SetTextAsync(selectedText);
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard == null)
        {
            return;
        }
        string? text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            SendTextToTerminal(text);
        }
    }

    private IClipboard? GetClipboard() => TopLevel.GetTopLevel(this)?.Clipboard;

    private string GetSelectedText()
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            ITerminalEmulator emulator = vm.TerminalEmulator;
            var sb = new StringBuilder();
            for (int row = 0; row < emulator.Rows; row++)
            {
                string line = emulator.GetBufferLine(row);
                if (!string.IsNullOrEmpty(line))
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }
                    sb.Append(line);
                }
            }
            return sb.ToString();
        }
        return string.Empty;
    }

    /// <summary>把键盘焦点交给终端模拟器控件。</summary>
    public void FocusTerminal()
    {
        if (DataContext is not TerminalTabViewModel vm)
        {
            return;
        }
        Dispatcher.UIThread.Post(
            () =>
            {
                // 密码列表开着时不抢焦点:那是用户为点选而保持的鼠标交互,焦点一旦回到
                // 终端,弹层会立刻被"终端失焦"的镜像守卫收掉,点选就永远落不上。
                if (PasswordPopup.IsOpen || !ReferenceEquals(DataContext, vm)
                    || !IsAttachedToVisualTree() || !IsEffectivelyVisible
                    || TopLevel.GetTopLevel(this) is not Window { IsActive: true } owner
                    || (owner.DataContext is MainWindowViewModel main && !ReferenceEquals(main.ActiveTerminalTab, vm))
                    || owner.OwnedWindows.Any(window => window.IsVisible))
                {
                    return;
                }
                Focus();
                vm.TerminalEmulator.Control.Focus();
            },
            DispatcherPriority.Input
        );
    }

    private static KeyModifiers MapModifiers(Avalonia.Input.KeyModifiers avaloniaModifiers)
    {
        KeyModifiers result = KeyModifiers.None;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            result |= KeyModifiers.Ctrl;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            result |= KeyModifiers.Shift;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            result |= KeyModifiers.Alt;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            result |= KeyModifiers.Meta;
        }
        return result;
    }

    private static KeyCode MapKey(Key avaloniaKey)
    {
        return avaloniaKey switch
        {
            Key.C => KeyCode.C,
            Key.V => KeyCode.V,
            Key.T => KeyCode.T,
            Key.W => KeyCode.W,
            Key.Tab => KeyCode.Tab,
            Key.OemComma => KeyCode.Comma,
            _ => KeyCode.None,
        };
    }
}
