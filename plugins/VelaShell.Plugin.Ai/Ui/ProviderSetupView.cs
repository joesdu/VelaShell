using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text.Json;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「连接供应商」:一页列出内置目录,<b>点一下就登进去</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>设计的第一条:能自动的绝不问用户。</b>参数齐全的那些家,点行(或点行尾的「登录」)
/// 立刻开浏览器,授权完成后自动落库、自动建好模型 —— 全程零输入。
/// 只有"程序确实不知道"的东西才会露出输入框,而且<b>只露缺的那几个</b>:
/// 自行部署的服务不知道地址,那就问地址;还没注册 OAuth 应用,那就问客户端 id;
/// 走 API Key 的,那就只问一把 Key。名称、模型 id、协议这些目录里都有,
/// 收进「高级」里,想改的人找得到,不想改的人看不见。
/// </para>
/// <para>
/// <b>登录不落任何明文</b>:PKCE 的 verifier 只活在这一次调用的栈上,换回来的令牌整组
/// 序列化后经宿主机密存储加密落盘(键 <c>oauth:&lt;供应商 id&gt;</c>),界面上从头到尾不显示它。
/// </para>
/// <para>
/// 整页代码里搭、不写 AXAML:行是按目录动态生成的,而且每行展开出来的东西各不相同
/// (缺什么显什么),写成模板反倒要为这套规则再造一层状态。
/// </para>
/// <para>
/// <b>同一家允许加多份</b>(换个 Key、换种协议、登另一个账号都算):已有至少一份时展开区里
/// 多一枚「再添加一个」,按下表单就地转入新建态 —— 底稿拷最近一份、机密不跟过来,
/// 主按钮变回「添加」。作用在单个实例上的改与移除,这一页只对<b>最近一份</b>生效,
/// 更早那些去设置页左栏管(那边本来就是逐实例的表单)。
/// </para>
/// </remarks>
public sealed class ProviderSetupView : UserControl
{
    private const double Gutter = 10;

    /// <summary>这一条还缺什么才登得上 / 用得起来。</summary>
    [Flags]
    private enum Missing
    {
        /// <summary>什么都不缺 —— 点一下直接登录。</summary>
        None = 0,

        /// <summary>没有 API Key。</summary>
        ApiKey = 1,

        /// <summary>没有基地址(自行部署 / 按资源分配地址的云服务)。</summary>
        BaseUrl = 2,

        /// <summary>OAuth 客户端 id 还空着(VelaShell 尚未在这家注册应用)。</summary>
        ClientId = 4,

        /// <summary>OAuth 端点还空着(完全自定义的那一条)。</summary>
        Endpoints = 8
    }

    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly ProviderHealth? _health;

    /// <summary>模型规格库(models.dev):按需拉新,缓存在插件私有数据目录。</summary>
    private readonly ModelsDevCatalog _models;

    private readonly StackPanel _rows = new() { Spacing = 6 };

    /// <summary>每行的把手:展开往 <c>Slot</c> 里塞内容,状态变了改 <c>Dot</c>/<c>Pill</c>/行尾按钮。</summary>
    private readonly List<Row> _cards = [];

    private sealed record Row(
        ProviderCatalogEntry Entry, Border Card, Ellipse Dot, TextBlock Pill, StackPanel Slot);

    /// <summary>当前展开的是哪一条(目录 id);null = 全收起。一次只开一个。</summary>
    private string? _openId;
    // 展开后绑定实际显示的实例;设置页可指定旧账号,目录初次展开默认最近一份。
    private string? _focusedProviderId;


    /// <summary>正在跑的那次登录 —— 「取消」按的就是它,窗口关掉也要一并取消。</summary>
    private CancellationTokenSource? _login;

    /// <summary>
    /// OAuth「添加另一个账号」的独立实例草稿;收起或落库后清除。
    /// </summary>
    private bool _addingAnother;
    private string? _cloneSourceId;
    private bool _addingKey;
    private string? _addingKeyProviderId;
    private CancellationTokenSource? _keyDraftCancellation;


    /// <summary>供应商增删改完了(参数是它的 id);设置页据此重建左栏并选中。</summary>
    public event Action<string>? ProviderChanged;
    /// <summary>模型拉取已写回原实例;只通知刷新列表,不改变设置页当前选择。</summary>
    public event Action? ModelsChanged;

    /// <param name="context">插件上下文(日志 + 剪贴板)。</param>
    /// <param name="store">设置存储(建客户端、存机密都靠它)。</param>
    /// <param name="settings">面板共享的设置实例;直接改它。</param>
    /// <param name="loc">多语言文案。</param>
    /// <param name="focusCatalogId">打开时直接展开的目录条目。</param>
    /// <param name="focusProviderId">从设置页跳转时指定的实例 id;null 选最近一份。</param>
    /// <param name="health">共享健康记录；目录实际改动模型请求配置时只失效该模型。</param>
    public ProviderSetupView(IPluginContext context, AiSettingsStore store, AiSettings settings, Loc loc,
        string? focusCatalogId = null, string? focusProviderId = null, ProviderHealth? health = null)
    {
        _context = context;
        _store = store;
        _settings = settings;
        _loc = loc;
        _health = health;
        _models = new ModelsDevCatalog(context);
        DetachedFromVisualTree += (_, _) => CancelPendingLogin();

        Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/DialogStyles.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/AiTheme.axaml")
        });

        // 顶部说明与底部脚注都拿掉了:每一行自己已经说清了状态和下一步,
        // 两大段常驻文案只是把列表往下挤(用户验收时点名要删)。
        // 右侧留白拆成 10(根)+ 10(滚动区内):覆盖式滚动条贴着滚动区右缘画,
        // 全放根上它就压在卡片边上了(与「配置工具」同一处理)。
        Content = new Border
        {
            Padding = new Thickness(20, 16, 20 - Gutter, 16),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new Border { Padding = new Thickness(0, 0, Gutter, 0), Child = _rows }
            }
        };

        foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
        {
            _rows.Children.Add(BuildCard(entry));
        }
        _ = InitialiseAsync(focusCatalogId, focusProviderId);
    }

    private async Task InitialiseAsync(string? focusCatalogId, string? focusProviderId)
    {
        await RefreshStatusAsync().ConfigureAwait(true);
        FocusEntry(focusCatalogId, focusProviderId);
    }

    /// <summary>窗口关掉时把还在跑的登录掐掉,别留一个环回端口和一轮轮询在后台空转。</summary>
    public void CancelPendingLogin()
    {
        _login?.Cancel();
        _keyDraftCancellation?.Cancel();
        _keyDraftCancellation?.Dispose();
        _keyDraftCancellation = null;
        _login = null;
    }

    /// <summary>
    /// 展开目录里的某一条并滚到眼前;传 null / 认不出的 id 就什么都不做。
    /// </summary>
    /// <remarks>
    /// 从设置页点「管理登录」过来时用。<b>这条路不自动发起登录</b> ——
    /// 用户是来"看看/改改"的,窗口一开就把浏览器弹出去太粗暴。
    /// </remarks>
    /// <param name="catalogId">目录 id。</param>
    /// <param name="providerId">要操作的那份实例;null 取最近一份。</param>
    public void FocusEntry(string? catalogId, string? providerId = null)
    {
        if (catalogId is null)
        {
            return;
        }
        foreach (Row row in _cards)
        {
            if (row.Entry.Id != catalogId)
            {
                continue;
            }
            _focusedProviderId = _settings.Providers.Any(p => p.Id == providerId && p.CatalogId == catalogId)
                ? providerId : null;
            Expand(row);
            row.Card.BringIntoView();
            return;
        }
    }

    // ---- 行 ----

    private Border BuildCard(ProviderCatalogEntry entry)
    {
        var monogram = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.Monogram,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        monogram[!BackgroundProperty] = new DynamicResourceExtension("VelaBgActive");
        ((TextBlock)monogram.Child)[!ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        ((TextBlock)monogram.Child)[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");

        var name = new TextBlock { Text = entry.Name, FontWeight = FontWeight.Medium };
        name[!ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        name[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize13");
        // 走非公开接口的那几条挂个小标:哪天"AI 突然不能用了",用户得知道该往哪儿想
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { name } };
        if (entry.Experimental)
        {
            nameRow.Children.Add(Badge(_loc["SetupExperimental"]));
        }
        var models = new TextBlock
        {
            Text = entry.Models,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        models[!ForegroundProperty] = new DynamicResourceExtension("VelaTextTertiary");
        models[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");

        var dot = new Ellipse
        {
            Name = $"SetupDot.{entry.Id}",
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        dot[!Shape.FillProperty] = new DynamicResourceExtension("VelaTextMuted");
        var pillText = new TextBlock
        {
            Name = $"SetupPill.{entry.Id}",
            Text = _loc["StatusNotAdded"],
            VerticalAlignment = VerticalAlignment.Center
        };
        pillText[!ForegroundProperty] = new DynamicResourceExtension("VelaTextTertiary");
        pillText[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { dot, pillText }
            }
        };
        pill[!BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");

        // 行上<b>不放</b>动作按钮:点行只负责展开,登录/添加一律在展开区里点。
        // 一来"点一下就弹浏览器"太突然 —— 用户还没看清这一家要什么就被推去授权;
        // 二来按钮坐在行的命中区里,按下会冒泡、抬起才是 Click,天然是个双触发的坑。
        var header = new Grid
        {
            ColumnDefinitions = [with("Auto,*,Auto")],
            // 整行都要能点,而不是只有那几个字:Grid 没有背景就不吃命中测试
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var titles = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        titles.Children.Add(nameRow);
        titles.Children.Add(models);
        Grid.SetColumn(monogram, 0);
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(pill, 2);
        header.Children.Add(monogram);
        header.Children.Add(titles);
        header.Children.Add(pill);

        var slot = new StackPanel { IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
        var card = new Border
        {
            Name = $"SetupRow.{entry.Id}",
            Classes = { "card" },
            Padding = new Thickness(12, 10),
            Child = new StackPanel { Children = { header, slot } }
        };
        var row = new Row(entry, card, dot, pillText, slot);
        header.PointerPressed += (_, _) => Activate(row);
        _cards.Add(row);
        return card;
    }

    /// <summary>
    /// 点了某一行。<b>能直接登的就直接登</b>,不能的才展开那几个缺的框。
    /// </summary>
    /// <summary>点某一行:只管展开 / 收起,<b>绝不自动发起登录</b>。</summary>
    private void Activate(Row row)
    {
        // 登录正跑着就别再碰这一行:重进会把自己刚起的那次掐掉(Expand → Collapse → CancelPendingLogin),
        // 而用户那边浏览器已经开着了 —— 掐完再开一次,回调就落到一个没人等的端口上。
        if (_openId == row.Entry.Id && _login is not null)
        {
            return;
        }
        if (_openId == row.Entry.Id)
        {
            Collapse();
            _focusedProviderId = null;
            return;
        }
        _focusedProviderId = null;
        Expand(row);
    }

    private bool IsConnected(ProviderCatalogEntry entry)
        => Existing(entry) is { } provider && _connected.Contains(provider.Id);

    /// <summary>已登录的供应商 id(<see cref="RefreshStatusAsync" /> 刷新;读机密是异步的,不能在点击路径上现读)。</summary>
    private readonly HashSet<string> _connected = [with(StringComparer.Ordinal)];

    /// <summary>已配好 Key 的供应商 id。</summary>
    private readonly HashSet<string> _keyed = [with(StringComparer.Ordinal)];

    private void Collapse()
    {
        CancelPendingLogin();
        _addingAnother = false;
        _addingKey = false;
        _addingKeyProviderId = null;
        _cloneSourceId = null;
        _newProviderDraft = null;
        foreach (Row row in _cards)
        {
            row.Slot.Children.Clear();
            row.Slot.IsVisible = false;
        }
        _openId = null;
    }

    private void Expand(Row row, bool startAnother = false)
    {
        // Collapse 先跑:收旧的行,顺带把上一次的起草态清干净 —— 所以这一轮要不要起草,
        // 由调用方在这之后重新定,别在 Collapse 之前设(设了也会被它擦掉)
        Collapse();
        _addingAnother = startAnother;
        _openId = row.Entry.Id;
        row.Slot.Children.Add(BuildDetail(row));
        row.Slot.IsVisible = true;
    }

    // ---- 展开区(每次展开重建,控件引用放在视图字段上)----

    private TextBox _nameBox = new();
    private TextBox _modelBox = new();
    private TextBox _baseUrlBox = new();
    private TextBox _keyBox = new();
    private bool _keyEdited;
    private bool _keyPrefilling;
    private string? _providerFormBaseline;
    // 输入基线独立于目录吸收后的提交基线,避免未编辑控件被误判为用户草稿。
    private FormInputs? _formInputBaseline;
    private sealed record FormInputs(string? Name, string? BaseUrl, string? Model, int Protocol, int Flow,
        string? ClientId, string? AuthorizationUrl, string? TokenUrl, string? DeviceCodeUrl, string? Scopes);
    private AiProvider? _newProviderDraft;
    private string? _primaryKeyId;
    private Task<string?> _primaryKeyBaseline = Task.FromResult<string?>(null);
    // 克隆的授权参数只属于完整的签发地址;同一主机的不同路径也可能是不同租户。
    private string? _oauthConfiguredBaseUrl;
    private string? _oauthSourceBaseUrl;
    private (string? Api, string? Auth, string? Device, string? Token, string? Client)? _scopesForOrigin;
    private ComboBox _protocolCombo = new();
    private ComboBox _flowCombo = new();
    private TextBox _authUrlBox = new();
    private TextBox _tokenUrlBox = new();
    private TextBox _deviceUrlBox = new();
    private TextBox _clientIdBox = new();
    private TextBox _scopesBox = new();
    private TextBlock _progress = new();
    private TextBlock _conflictValues = new();
    private Button _primary = new();
    private Button? _removingPrimary;
    private Button _another = new();
    private Button _pull = new();
    private StackPanel? _modelPickerRow;
    private Button _secondary = new();
    private ComboBox? _modelPicker;
    private bool _modelPickerUpdating;
    private StackPanel _deviceCodePanel = new();
    private TextBlock _deviceCodeText = new();

    /// <summary>协议下拉的标签,顺序 = <see cref="ChatProtocol" /> 枚举。</summary>
    private static readonly string[] ProtocolLabels =
        ["OpenAI Chat Completions", "OpenAI Responses", "Anthropic Messages"];

    private StackPanel BuildDetail(Row row)
    {
        ProviderCatalogEntry entry = row.Entry;
        AiProvider? existing = Existing(entry);
        // 目录初次展开选最新一份,之后所有动作只认实际显示的实例,不随共享设置漂移。
        if (existing is not null) _focusedProviderId = existing.Id;
        if (_addingKey && existing is not null)
        {
            _keyBox = Mono(new TextBox { Name = "SetupKeyBox", PasswordChar = '●' });
            var keyPanel = new StackPanel();
            keyPanel.Children.Add(Label(_loc["ApiKey"]));
            keyPanel.Children.Add(_keyBox);
            _modelPickerRow = null;
            _modelPicker = null;
            keyPanel.Children.Add(BuildActions(row, existing, Missing.None, adding: true));
            return keyPanel;
        }
        // 「再添加一个」按下后:表单起草的是一个还没落库的新实例 —— 底稿拷最近一份
        // (配置照搬、实例与模型全换新 id),机密不跟过来(它们按 id 存,新 id 天然取不到)。
        bool adding = existing is not null && _addingAnother;
        AiProvider draft = existing is not null && adding
            ? CloneForAnother(existing)
            : existing ?? entry.CreateProvider();
        _cloneSourceId = adding ? existing?.Id : null;
        _providerFormBaseline = JsonSerializer.Serialize(draft);
        _newProviderDraft = existing is null || adding ? draft : null;
        _primaryKeyId = existing is not null && !adding ? existing.Id : null;
        _primaryKeyBaseline = Task.FromResult<string?>(null);
        AiModelConfig model = draft.Models.Count > 0 ? draft.Models[0] : new AiModelConfig();
        Missing missing = MissingOf(entry, draft);

        var panel = new StackPanel();
        if (entry.Experimental)
        {
            panel.Children.Add(Hint(_loc["SetupExperimentalHint"]));
        }
        // 每个控件都先造出来:后面按"缺不缺"决定摆不摆,ApplyForm 一律照读,
        // 没摆出来的就是目录里那个值,不会被清空。
        _nameBox = new TextBox { Name = "SetupNameBox", Text = draft.Name };
        _modelBox = Mono(new TextBox { Name = "SetupModelBox", Text = model.Model });
        _baseUrlBox = Mono(new TextBox { Name = "SetupBaseUrlBox", Text = Placeholder(draft.BaseUrl) });
        _keyBox = Mono(new TextBox { Name = "SetupKeyBox", PasswordChar = '●' });
        _keyEdited = false;
        TextBox keyInput = _keyBox;
        keyInput.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && ReferenceEquals(keyInput, _keyBox)
                && !_keyPrefilling) _keyEdited = true;
        };
        _modelPickerRow = null;
        _modelPicker = null;
        _oauthConfiguredBaseUrl = adding && entry.NeedsOAuthSetup ? draft.BaseUrl : null;
        _oauthSourceBaseUrl = _oauthConfiguredBaseUrl;
        _protocolCombo = new ComboBox
        {
            Name = "SetupProtocolCombo",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ProtocolLabels,
            // 显示首模型的**生效**协议(没有模型级覆盖才是默认值):显示的必须等于实际在用的 ——
            SelectedIndex = (int)(model.Protocol ?? draft.DefaultProtocol)
        };
        BuildOAuthControls(draft);
        _formInputBaseline = CaptureFormInputs();
        _scopesForOrigin = adding && entry.NeedsOAuthSetup
            ? (_baseUrlBox.Text, _authUrlBox.Text, _deviceUrlBox.Text, _tokenUrlBox.Text, _clientIdBox.Text)
            : null;
        if (_scopesForOrigin is not null)
        {
            TextBox scopesBox = _scopesBox;
            scopesBox.PropertyChanged += (_, change) =>
            {
                if (change.Property == TextBox.TextProperty && ReferenceEquals(scopesBox, _scopesBox)
                    && _addingAnother && !string.IsNullOrEmpty(scopesBox.Text))
                {
                    // 与旧值逐字相同也可能是用户为新 issuer 明确重填的权限。
                    _scopesForOrigin = CurrentScopesOrigin();
                }
            };
        }
        if (adding && entry.NeedsOAuthSetup)
        {
            TextBox scopesBox = _scopesBox;
            foreach (TextBox endpointBox in new[] { _authUrlBox, _deviceUrlBox, _tokenUrlBox, _clientIdBox })
            {
                endpointBox.TextChanged += (_, _) =>
                {
                    if (ReferenceEquals(scopesBox, _scopesBox) && _addingAnother)
                    {
                        ClearScopesOnIssuerChange();
                    }
                };
            }
        }
        if (adding && entry.NeedsOAuthSetup)
        {
            // 每次 API 地址切换都必须重新确认授权信息,防止已为第二台主机配置的令牌
            // 又被原样带到第三台。同主机的路径也可能区分 OAuth 租户,不能推定安全。
            TextBox addressBox = _baseUrlBox;
            addressBox.TextChanged += (_, _) =>
            {
                if (!ReferenceEquals(addressBox, _baseUrlBox) || !_addingAnother)
                {
                    return; // 上一次展开区晚到的事件不能改写当前草稿
                }
                string? address = addressBox.Text;
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
                    || string.IsNullOrEmpty(uri.Host) || SameOAuthIssuer(_oauthConfiguredBaseUrl, address))
                {
                    return;
                }
                _oauthConfiguredBaseUrl = address;
                _clientIdBox.Text = "";
                _authUrlBox.Text = "";
                _tokenUrlBox.Text = "";
                _deviceUrlBox.Text = "";
                _scopesBox.Text = "";
                ClearScopesOnIssuerChange();
            };
        }

        if (entry.IsSubscription)
        {
            BuildSubscriptionDetail(panel, entry, missing, adding);
        }
        else
        {
            BuildApiKeyDetail(panel, entry, missing);
        }
        panel.Children.Add(BuildActions(row, existing, missing, adding));

        if (existing is not null && !entry.IsSubscription)
        {
            _primaryKeyBaseline = LoadKeyAsync(existing, _keyBox);
        }
        return panel;
    }

    /// <summary>订阅项:缺什么摆什么。什么都不缺时这里<b>一个输入框都没有</b>,只有一行进度。</summary>
    /// <param name="panel">展开区的容器。</param>
    /// <param name="entry">这一行是目录里的哪一条。</param>
    /// <param name="missing">这一条还缺什么才登得上。</param>
    /// <param name="adding">正在「再添加一个」—— 已连接的状态提示要让位给一次全新的登录。</param>
    private void BuildSubscriptionDetail(StackPanel panel, ProviderCatalogEntry entry, Missing missing, bool adding)
    {
        if (!adding && IsConnected(entry))
        {
            panel.Children.Add(Hint(_loc["SetupConnectedHint"]));
            return;
        }
        if (missing == Missing.None)
        {
            panel.Children.Add(Hint(_loc["SetupSignInHint"]));
            if (!adding)
            {
                return;
            }
            // 起草第二份:字段一个都不缺,但「高级」不能省 —— 新实例得能改地址与协议,
            // 否则已配齐的 Azure / 自定义 OAuth 只能原样再登一份,连换个地址都做不到。
            // 落到函数末尾的 Advanced(entry);缺字段的那些分支因为是 None 一个都不摆。
        }
        // 起草第二份的自定义 OAuth:端点与客户端 id 两块**无条件摆出来**(missing 是 None
        // 也摆)—— 换台主机多半要换一套注册,授权 / 令牌端点不可改的话,旧主机签出的令牌
        // 会被当成新主机的凭据发过去(review⑥#3)。内置项不适用:端点由目录定,
        // ApplyForm 对非 NeedsOAuthSetup 根本不落库,摆出来就是骗人的。
        bool addingCustom = adding && entry.NeedsOAuthSetup;
        if (missing.HasFlag(Missing.ClientId) || addingCustom)
        {
            if (missing.HasFlag(Missing.ClientId))
            {
                // 客户端 id 空着 = VelaShell 还没在这家注册应用。别只摆个空框让人猜它哪儿来的:
                // 把申请入口一并给出来,愿意自己注册的人当场就能填。clone 起草时框里已有值,
                // 这句提示就成了假话,只在真缺时才摆。
                panel.Children.Add(Hint(_loc["SetupClientIdPending"]));
                if (entry.RegistrationUrl.Length > 0)
                {
                    panel.Children.Add(LinkRow(_loc["SetupOpenRegistration"], entry.RegistrationUrl));
                }
            }
            panel.Children.Add(Label(_loc["OAuthClientId"]));
            panel.Children.Add(_clientIdBox);
        }
        if (missing.HasFlag(Missing.BaseUrl))
        {
            panel.Children.Add(Label(_loc["BaseUrl"]));
            panel.Children.Add(_baseUrlBox);
        }
        if (missing.HasFlag(Missing.Endpoints) || addingCustom)
        {
            BuildEndpointFields(panel);
        }
        panel.Children.Add(Advanced(entry));
    }

    /// <summary>填 Key 的那一路:只问一把 Key(地址不知道时才多问一句),其余全收进「高级」。</summary>
    private void BuildApiKeyDetail(StackPanel panel, ProviderCatalogEntry entry, Missing missing)
    {
        if (missing.HasFlag(Missing.BaseUrl))
        {
            panel.Children.Add(Label(_loc["BaseUrl"]));
            panel.Children.Add(_baseUrlBox);
        }
        if (NeedsKey(entry))
        {
            panel.Children.Add(Label(_loc["ApiKey"]));
            panel.Children.Add(KeyRow(_keyBox));
            panel.Children.Add(Hint(_loc["ApiKeyHint"]));
        }
        else
        {
            panel.Children.Add(Hint(_loc["SetupNoKeyNeeded"]));
        }
        panel.Children.Add(Advanced(entry));
    }

    private void BuildEndpointFields(StackPanel panel)
    {
        // 客户端 id 那一格由 BuildSubscriptionDetail 按 Missing.ClientId 摆,这里不能再摆一次 ——
        // 同一个控件挂进两个父容器,Avalonia 直接抛
        panel.Children.Add(Label(_loc["OAuthFlow"]));
        panel.Children.Add(_flowCombo);
        TextBlock authLabel = Label(_loc["OAuthAuthorizeUrl"]);
        panel.Children.Add(authLabel);
        panel.Children.Add(_authUrlBox);
        TextBlock deviceLabel = Label(_loc["OAuthDeviceUrl"]);
        panel.Children.Add(deviceLabel);
        panel.Children.Add(_deviceUrlBox);
        panel.Children.Add(Label(_loc["OAuthTokenUrl"]));
        panel.Children.Add(_tokenUrlBox);
        panel.Children.Add(Label(_loc["OAuthScopes"]));
        panel.Children.Add(_scopesBox);

        // 授权码那一行与设备码那一行互斥,连标签一起收
        void SyncFlow()
        {
            bool device = _flowCombo.SelectedIndex == 1;
            authLabel.IsVisible = !device;
            _authUrlBox.IsVisible = !device;
            deviceLabel.IsVisible = device;
            _deviceUrlBox.IsVisible = device;
        }
        _flowCombo.SelectionChanged += (_, _) => SyncFlow();
        SyncFlow();
    }

    private void BuildOAuthControls(AiProvider draft)
    {
        OAuthConfig oauth = draft.OAuth ?? new OAuthConfig();
        _flowCombo = new ComboBox
        {
            Name = "SetupFlowCombo",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { _loc["OAuthFlowPkce"], _loc["OAuthFlowDevice"] },
            // OpenRouter 那套 PKCE 变体在界面上仍归"授权码"一档:用户看到的行为一样,
            // 差别全在协议细节里,摆出第三个选项只会让人不知道该选哪个
            SelectedIndex = oauth.Flow == OAuthFlow.DeviceCode ? 1 : 0
        };
        _authUrlBox = Mono(new TextBox { Name = "SetupAuthUrlBox", Text = oauth.AuthorizationUrl });
        _tokenUrlBox = Mono(new TextBox { Name = "SetupTokenUrlBox", Text = oauth.TokenUrl });
        _deviceUrlBox = Mono(new TextBox { Name = "SetupDeviceUrlBox", Text = oauth.DeviceCodeUrl });
        _clientIdBox = Mono(new TextBox { Name = "SetupClientIdBox", Text = oauth.ClientId });
        _scopesBox = Mono(new TextBox { Name = "SetupScopesBox", Text = oauth.Scopes });
    }

    /// <summary>协议下拉什么时候归用户选:填 Key 的一律可选;订阅类只有那两条要填地址的自定义项。</summary>
    private static bool ProtocolSelectable(ProviderCatalogEntry entry)
        => entry.NeedsBaseUrl || !entry.IsSubscription;

    /// <summary>
    /// 「高级」:名称 / 模型 id / 基地址 / 协议。默认收起。
    /// </summary>
    /// <remarks>
    /// 这些目录里都有出厂值,九成用户一辈子不用改;摆在正面就是"填一堆东西"的由来。
    /// 但走中转站的人确实要改地址和模型 id,所以留一个折叠入口,而不是干脆删掉。
    /// </remarks>
    private StackPanel Advanced(ProviderCatalogEntry entry)
    {
        var body = new StackPanel { IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(Label(_loc["Name"]));
        body.Children.Add(_nameBox);
        // 拉到过列表就摆个下拉:接完一家之后想换模型,该是"从真实存在的里面挑",
        // 而不是回去翻文档把 id 一个字一个字敲对
        _modelPickerRow = new StackPanel { IsVisible = false };
        body.Children.Add(_modelPickerRow);
        if (Existing(entry) is { } source)
        {
            UpdateModelPicker(source);
            TextBox addressBox = _baseUrlBox;
            addressBox.TextChanged += (_, _) =>
            {
                if (ReferenceEquals(addressBox, _baseUrlBox)) UpdateModelPicker(source);
            };
        }
        body.Children.Add(Label(_loc["Model"]));
        body.Children.Add(_modelBox);
        body.Children.Add(Hint(_loc["SetupModelHint"]));
        // 缺地址时它已经摆在正面了,别在这儿再来一个(同一个控件也挂不进两处)
        if (_baseUrlBox.Parent is null)
        {
            body.Children.Add(Label(_loc["BaseUrl"]));
            body.Children.Add(_baseUrlBox);
        }
        // 协议下拉:缺地址的自定义项照旧摆;填 Key 的一律可选 ——
        // 目录里 openai 出厂是 Responses,想走 Chat Completions 的人得有个地方改它
        if (ProtocolSelectable(entry))
        {
            body.Children.Add(Label(_loc["DefaultProtocol"]));
            body.Children.Add(_protocolCombo);
        }

        var toggle = new ToggleButton
        {
            Name = "SetupAdvancedToggle",
            Content = _loc["SetupAdvanced"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            Height = 22,
            Padding = new Thickness(8, 0)
        };
        toggle[!ThemeProperty] = new DynamicResourceExtension("AiChipToggleTheme");
        toggle.IsCheckedChanged += (_, _) => body.IsVisible = toggle.IsChecked == true;
        return new StackPanel { Children = { toggle, body } };
    }

    private void UpdateModelPicker(AiProvider source)
    {
        if (_modelPickerRow is not { } row) return;
        row.IsVisible = source.AvailableModels.Count > 0
                        && string.Equals(source.BaseUrl, _baseUrlBox.Text?.Trim(), StringComparison.Ordinal);
        if (source.AvailableModels.Count == 0) return;
        if (_modelPicker is null)
        {
            row.Children.Add(Label(_loc["ModelPick"]));
            _modelPicker = new ComboBox { Name = "SetupModelPicker", HorizontalAlignment = HorizontalAlignment.Stretch };
            ComboBox picker = _modelPicker;
            picker.SelectionChanged += (_, _) =>
            {
                if (!_modelPickerUpdating && ReferenceEquals(picker, _modelPicker) && picker.SelectedItem is string id)
                {
                    _modelBox.Text = id;
                }
            };
            row.Children.Add(picker);
        }
        _modelPickerUpdating = true;
        try
        {
            _modelPicker.ItemsSource = source.AvailableModels;
            _modelPicker.SelectedItem = source.AvailableModels.FirstOrDefault(m =>
                string.Equals(m, _modelBox.Text, StringComparison.OrdinalIgnoreCase));
        }
        finally { _modelPickerUpdating = false; }
    }

    /// <summary>底下那一行:主按钮 + 「再添加一个」+ 次按钮(退出登录 / 移除 / 取消)+ 进度,设备码时上面还有一枚用户码。</summary>
    private StackPanel BuildActions(Row row, AiProvider? existing, Missing missing, bool adding)
    {
        ProviderCatalogEntry entry = row.Entry;
        _progress = new TextBlock
        {
            Name = "SetupProgressText",
            Classes = { "dim" },
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        _deviceCodeText = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _deviceCodeText[!ForegroundProperty] = new DynamicResourceExtension("VelaAccent");
        _deviceCodeText[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize13");
        _deviceCodeText[!FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        var copy = new Button { Content = _loc["Copy"], Height = 24, Padding = new Thickness(10, 0) };
        copy[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        copy.Click += (_, _) => _ = _context.Clipboard.SetTextAsync(_deviceCodeText.Text ?? "");
        _deviceCodePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            IsVisible = false,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _deviceCodeText, copy }
        };

        _primary = new Button { Name = "SetupPrimaryButton", Height = 26, Padding = new Thickness(14, 0) };
        _primary[!ThemeProperty] = new DynamicResourceExtension("VelaAccentPillButtonTheme");
        _primary.Content = PrimaryLabel(entry, existing, missing, adding);
        _primary.Click += (_, _) => _ = PrimaryAsync(row);

        // API Key 只新增槽;订阅仍新增独立账号。两种草稿期间都隐藏入口。
        _another = new Button { Name = "SetupAddAnotherButton", Height = 26, Padding = new Thickness(12, 0) };
        _another[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        _another.Content = _loc[entry.IsSubscription ? "SetupAddAnotherAccount" : "SetupAddApiKey"];
        _another.IsVisible = existing is not null && !adding && (entry.IsSubscription || NeedsKey(entry));
        _another.Click += (_, _) => StartAnother(row);

        // 「拉取模型」:接上之后自动拉过一次,但那一次可能没网、可能缓存是旧的,
        // 各家也会不断出新型号 —— 没有这个按钮,用户就只能靠"退出登录再登一次"来重来一遍。
        _pull = new Button { Name = "SetupPullButton", Height = 26, Padding = new Thickness(12, 0) };
        _pull[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        _pull.Content = _loc["ModelsPull"];
        // 已经加进来,而且两条路至少通一条:端点自己的 /models(填了地址就能问),
        // 或 models.dev 收录了这一家。新建态里没有"已经加进来"的东西可拉,藏起来。
        _pull.IsVisible = existing is not null && !adding
                          && (entry.ModelsDevId.Length > 0 || !string.IsNullOrWhiteSpace(existing.BaseUrl));
        _pull.Click += (_, _) => _ = PullNowAsync(row);

        _secondary = new Button { Name = "SetupSecondaryButton", Height = 26, Padding = new Thickness(12, 0) };
        _secondary[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        // 新建态的次按钮是「取消」:放弃这次起草,回到编辑最近一份(登录期间它被借去掐登录,同名同义)
        _secondary.Content = adding
            ? _loc["Cancel"]
            : entry.IsSubscription ? _loc["SetupSignOut"] : _loc["SetupRemove"];
        _secondary.IsVisible = existing is not null;
        _secondary.Click += (_, _) => _ = SecondaryAsync(row);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _primary, _another, _pull, _secondary }
        };
        var line = new Grid { ColumnDefinitions = [with("*,Auto")], Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(buttons, 1);
        line.Children.Add(_progress);
        line.Children.Add(buttons);
        _conflictValues = Hint("");
        _conflictValues.Name = "SetupConflictValues";
        _conflictValues.IsVisible = false;
        return new StackPanel { Children = { _conflictValues, _deviceCodePanel, line } };
    }

    private string PrimaryLabel(ProviderCatalogEntry entry, AiProvider? existing, Missing missing, bool adding)
    {
        if (entry.IsSubscription)
        {
            // 新建态是去登<b>另一个账号</b>:哪怕最近一份连着,这里也得说「登录」而不是「重新登录」
            return !adding && IsConnected(entry) ? _loc["SetupReconnect"] : _loc["SetupSignIn"];
        }
        return adding || existing is null || missing.HasFlag(Missing.ApiKey) ? _loc["SetupAdd"] : _loc["Save"];
    }

    // ---- 缺什么 ----

    /// <summary>这一条离"能用"还差哪几样。</summary>
    private Missing MissingOf(ProviderCatalogEntry entry, AiProvider provider)
    {
        Missing missing = Missing.None;
        if (IsPlaceholder(provider.BaseUrl))
        {
            missing |= Missing.BaseUrl;
        }
        if (entry.IsSubscription)
        {
            OAuthConfig oauth = provider.OAuth ?? new OAuthConfig();
            // OpenRouter 那一路本来就没有 client_id,不算缺
            if (oauth.Flow != OAuthFlow.OpenRouterPkce && string.IsNullOrWhiteSpace(oauth.ClientId))
            {
                missing |= Missing.ClientId;
            }
            if (string.IsNullOrWhiteSpace(oauth.TokenUrl)
                || string.IsNullOrWhiteSpace(oauth.Flow == OAuthFlow.DeviceCode
                    ? oauth.DeviceCodeUrl
                    : oauth.AuthorizationUrl))
            {
                missing |= Missing.Endpoints;
            }
            return missing;
        }
        if (NeedsKey(entry) && !_keyed.Contains(provider.Id))
        {
            missing |= Missing.ApiKey;
        }
        return missing;
    }

    /// <summary>本地自部署(Ollama 之类)不需要鉴权,别对着它一直挂个"还没填 Key"。</summary>
    private static bool NeedsKey(ProviderCatalogEntry entry)
    {
        string url = entry.CreateProvider().BaseUrl;
        return !url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
               && !url.Contains("127.0.0.1", StringComparison.Ordinal);
    }

    /// <summary>地址还是空的、或者还带着 <c>&lt;resource&gt;</c> 这种占位符。</summary>
    private static bool IsPlaceholder(string? url)
        => string.IsNullOrWhiteSpace(url) || url.Contains('<', StringComparison.Ordinal);

    /// <summary>占位符不该出现在输入框里当默认值 —— 用户十有八九会直接把它连尖括号一起提交。</summary>
    private static string Placeholder(string url) => url.Contains('<', StringComparison.Ordinal) ? "" : url;

    // ---- 动作 ----

    private async Task PrimaryAsync(Row row)
    {
        // 一次落库后还会异步拉模型;这期间同一目录可以打开下一份表单。
        Button primary = _primary;
        TextBlock progress = _progress;
        if (!primary.IsEnabled || _login is not null || ReferenceEquals(primary, _removingPrimary))
        {
            return;
        }
        ProviderCatalogEntry entry = row.Entry;
        if (_focusedProviderId is not null && Existing(entry) is null)
        {
            progress.Text = _loc["SetupSourceRemoved"];
            return;
        }
        try
        {
            primary.IsEnabled = false;
            if (_addingKey)
            {
                await AddKeyDraftAsync(row).ConfigureAwait(true);
                return;
            }
            if (entry.IsSubscription)
            {
                await SignInAsync(row).ConfigureAwait(true);
            }
            else
            {
                await AddOrSaveKeyAsync(entry).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_primary, primary))
            {
                progress.Text = _loc["LoginCancelled"];
            }
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            if (ReferenceEquals(_primary, primary))
                await ReloadConflictedFormAsync(entry, primary).ConfigureAwait(true);
        }
        catch (ArgumentException) when (!entry.IsSubscription)
        {
            if (ReferenceEquals(_primary, primary)) progress.Text = _loc["SetupUniqueApiKey"];
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Provider setup for '{entry.Name}' failed: {ex.Message}");
            if (ReferenceEquals(_primary, primary))
            {
                progress.Text = _loc.F("LoginFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
            }
        }
        finally
        {
            if (ReferenceEquals(_primary, primary) && !ReferenceEquals(primary, _removingPrimary))
            {
                _primary.IsEnabled = true;
                _secondary.IsEnabled = true;
                _deviceCodePanel.IsVisible = false;
                // 这一轮之后这家的处境可能变了(刚加进来 / 刚登上 / 起草到一半失败了),按钮文字跟着改。
                AiProvider? existing = Existing(entry);
                bool adding = _addingAnother || _addingKey || _newProviderDraft is not null;
                _primary.Content = PrimaryLabel(entry, existing,
                    existing is null ? Missing.None : MissingOf(entry, existing), adding);
                _secondary.Content = adding
                    ? _loc["Cancel"]
                    : entry.IsSubscription ? _loc["SetupSignOut"] : _loc["SetupRemove"];
                _secondary.IsVisible = existing is not null || adding;
                _another.IsVisible = existing is not null && !adding && (entry.IsSubscription || NeedsKey(entry));
                _pull.IsVisible = existing is not null && !adding
                                  && (entry.ModelsDevId.Length > 0 || !string.IsNullOrWhiteSpace(existing.BaseUrl));
            }
        }
    }
    private async Task ReloadConflictedFormAsync(ProviderCatalogEntry entry, Button primary)
    {
        try
        {
            FormInputs? opened = _formInputBaseline;
            await _store.ReloadIntoAsync(_settings, _context.Shutdown).ConfigureAwait(true);
            if (!ReferenceEquals(_primary, primary)) return;
            AiProvider? current = Existing(entry);
            if (_newProviderDraft is null && !_addingKey && current is not null)
            {
                FormInputs latestInputs = ProviderFormInputs(current);
                if (opened is not null) FollowUntouchedFields(opened, latestInputs);
                _providerFormBaseline = JsonSerializer.Serialize(current);
                _formInputBaseline = latestInputs;
                _primaryKeyId = current.Id;
                _primaryKeyBaseline = _store.GetApiKeyAsync(current.Id, _context.Shutdown);
                string? latestKey = await _primaryKeyBaseline.ConfigureAwait(true);
                if (!ReferenceEquals(_primary, primary)) return;
                if (!_keyEdited)
                {
                    _keyPrefilling = true;
                    try { _keyBox.Text = latestKey ?? ""; }
                    finally { _keyPrefilling = false; }
                }
            }
            IEnumerable<AiProvider> latest = _focusedProviderId is null ? Matching(entry)
                : current is null ? [] : [current];
            _conflictValues.Text = string.Join("\n", latest.Select(provider =>
                $"{_loc["Name"]}: {provider.Name}\n{_loc["BaseUrl"]}: {provider.BaseUrl}\n"
                + $"{_loc["Model"]}: {provider.Models.FirstOrDefault()?.Model}\n"
                + $"{_loc["Protocol"]}: {provider.Models.FirstOrDefault()?.Protocol ?? provider.DefaultProtocol}"
                + (provider.OAuth is { } oauth
                    ? $"\n{_loc["OAuthClientId"]}: {oauth.ClientId}\n{_loc["OAuthTokenUrl"]}: {oauth.TokenUrl}"
                      + $"\n{_loc["OAuthAuthorizeUrl"]}: {oauth.AuthorizationUrl}\n{_loc["OAuthDeviceUrl"]}: {oauth.DeviceCodeUrl}"
                      + $"\n{_loc["OAuthScopes"]}: {oauth.Scopes}\n{_loc["OAuthFlow"]}: "
                      + _loc[oauth.Flow == OAuthFlow.DeviceCode ? "OAuthFlowDevice" : "OAuthFlowPkce"] : "")));
            _conflictValues.IsVisible = !string.IsNullOrEmpty(_conflictValues.Text);
            _progress.Text = _focusedProviderId is not null && current is null
                ? _loc["SetupSourceRemoved"] : _loc["SetupConfigChanged"];
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Reloading provider setup failed: {ex.Message}");
            if (ReferenceEquals(_primary, primary))
                _progress.Text = _loc.F("LoginFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
        }
    }
    private FormInputs CaptureFormInputs() => new(_nameBox.Text, _baseUrlBox.Text, _modelBox.Text,
        _protocolCombo.SelectedIndex, _flowCombo.SelectedIndex, _clientIdBox.Text,
        _authUrlBox.Text, _tokenUrlBox.Text, _deviceUrlBox.Text, _scopesBox.Text);

    private static FormInputs ProviderFormInputs(AiProvider provider)
    {
        AiModelConfig? model = provider.Models.Count == 0 ? null : provider.Models[0];
        OAuthConfig? oauth = provider.OAuth;
        return new(provider.Name, Placeholder(provider.BaseUrl), model?.Model ?? "",
            (int)(model?.Protocol ?? provider.DefaultProtocol), oauth?.Flow == OAuthFlow.DeviceCode ? 1 : 0,
            oauth?.ClientId ?? "", oauth?.AuthorizationUrl ?? "", oauth?.TokenUrl ?? "",
            oauth?.DeviceCodeUrl ?? "", oauth?.Scopes ?? "");
    }

    private void FollowUntouchedFields(FormInputs opened, FormInputs latest)
    {
        if (_nameBox.Text == opened.Name) _nameBox.Text = latest.Name;
        if (_baseUrlBox.Text == opened.BaseUrl) _baseUrlBox.Text = latest.BaseUrl;
        if (_modelBox.Text == opened.Model) _modelBox.Text = latest.Model;
        if (_protocolCombo.SelectedIndex == opened.Protocol) _protocolCombo.SelectedIndex = latest.Protocol;
        if (_flowCombo.SelectedIndex == opened.Flow) _flowCombo.SelectedIndex = latest.Flow;
        if (_clientIdBox.Text == opened.ClientId) _clientIdBox.Text = latest.ClientId;
        if (_authUrlBox.Text == opened.AuthorizationUrl) _authUrlBox.Text = latest.AuthorizationUrl;
        if (_tokenUrlBox.Text == opened.TokenUrl) _tokenUrlBox.Text = latest.TokenUrl;
        if (_deviceUrlBox.Text == opened.DeviceCodeUrl) _deviceUrlBox.Text = latest.DeviceCodeUrl;
        if (_scopesBox.Text == opened.Scopes) _scopesBox.Text = latest.Scopes;
    }



    /// <summary>API Key 开启空的槽草稿;OAuth 开启独立账号草稿。</summary>
    private void StartAnother(Row row)
    {
        // 登录正跑着就别碰这一行:重建会把刚起的那次掐掉(与 Activate 同一条理由)
        if (_addingKey || _addingAnother || _newProviderDraft is not null || _login is not null || !_another.IsEnabled || ReferenceEquals(_primary, _removingPrimary))
        {
            return;
        }
        // 来源已被其它窗口移除时保留旧表单;收起再展开才可选择幸存实例或首次添加。
        if (Existing(row.Entry) is not { } source)
        {
            _progress.Text = _loc["SetupSourceRemoved"];
            return;
        }
        if (CaptureFormInputs() != _formInputBaseline || _keyEdited)
        {
            _progress.Text = _loc["SetupSaveProviderFirst"];
            return;
        }
        if (!row.Entry.IsSubscription)
        {
            Collapse();
            _addingKey = true;
            _addingKeyProviderId = source.Id;
            _keyDraftCancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
            _openId = row.Entry.Id;
            row.Slot.Children.Add(BuildDetail(row));
            row.Slot.IsVisible = true;
            return;
        }
        Expand(row, startAnother: true);
    }

    private async Task AddKeyDraftAsync(Row row)
    {
        TextBox input = _keyBox;
        Button primary = _primary;
        CancellationTokenSource? cancellation = _keyDraftCancellation;
        AiProvider? provider = Existing(row.Entry);
        bool OwnsDraft() => _addingKey && _openId == row.Entry.Id
            && ReferenceEquals(input, _keyBox) && provider is not null
            && provider.Id == _addingKeyProviderId && _settings.Providers.Contains(provider)
            && ReferenceEquals(Existing(row.Entry), provider) && cancellation?.IsCancellationRequested == false;
        if (!OwnsDraft()) return;
        string submittedKey = input.Text ?? "";
        try
        {
            await _store.AddProviderApiKeyAsync(_settings, provider!, submittedKey, cancellation!.Token)
                .ConfigureAwait(true);
            if (!OwnsDraft()) return;
            ProviderChanged?.Invoke(provider!.Id);
            if (!OwnsDraft()) return;
            // 等待期间的新输入仍是同一固定实例的下一把 Key 草稿,不能重建丢掉。
            if (input.Text == submittedKey) Expand(row);
            _progress.Text = _loc["SetupAdded"];
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (ArgumentException)
        {
            if (OwnsDraft()) _progress.Text = _loc["SetupUniqueApiKey"];
        }
        finally
        {
            if (!ReferenceEquals(primary, _removingPrimary)) primary.IsEnabled = true;
            if (ReferenceEquals(_keyDraftCancellation, cancellation) && !_addingKey)
                _keyDraftCancellation = null;
            if (!_addingKey || !ReferenceEquals(_keyDraftCancellation, cancellation)) cancellation?.Dispose();
        }
    }

    /// <summary>
    /// 「拉取模型」按下去:重新问一次清单,把这一家的模型重新落地。
    /// </summary>
    /// <remarks>
    /// 与登录后那次自动拉的区别是 <c>force</c> —— 规格缓存还在有效期内也重下。
    /// 用户是明确点了"拉取"才走到这儿的,这时还拿七天前的缓存糊弄他就没意义了。
    /// </remarks>
    private async Task PullNowAsync(Row row)
    {
        if (Existing(row.Entry) is not { } provider)
        {
            return;
        }
        Button pull = _pull;
        TextBlock progress = _progress;
        if (!pull.IsEnabled)
        {
            return;
        }
        try
        {
            pull.IsEnabled = false;
            await PullModelsAsync(row.Entry, provider, force: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.Log.Warn($"Pulling models for '{provider.Name}' failed: {ex.Message}");
            if (ReferenceEquals(_progress, progress) && ReferenceEquals(Existing(row.Entry), provider))
            {
                progress.Text = _loc.F("LoginFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
            }
        }
        finally
        {
            pull.IsEnabled = true;
        }
    }

    /// <summary>次按钮:登录中是「取消」,新建态是「取消」(放弃起草),平时是「退出登录」/「移除」。</summary>
    private async Task SecondaryAsync(Row row)
    {
        Button secondary = _secondary;
        if (!secondary.IsEnabled) return;
        if (_login is not null)
        {
            CancelPendingLogin();
            return;
        }
        if (_addingAnother || _addingKey || _newProviderDraft is not null)
        {
            // 放弃这次「再添加一个」,表单回到编辑最近一份
            _addingAnother = false;
            if (Existing(row.Entry) is null) _focusedProviderId = null;
            Expand(row);
            return;
        }
        if (Existing(row.Entry) is not { } provider)
        {
            if (_focusedProviderId is not null) _progress.Text = _loc["SetupSourceRemoved"];
            return;
        }
        secondary.IsEnabled = false;
        TextBlock progress = _progress;
        Button primary = _primary;
        Button another = _another;
        if (!row.Entry.IsSubscription)
        {
            _removingPrimary = primary;
            primary.IsEnabled = another.IsEnabled = false;
        }
        try
        {
            if (row.Entry.IsSubscription)
            {
                await _store.ClearTokensAsync(provider.Id).ConfigureAwait(true);
                if (ReferenceEquals(_secondary, secondary)) progress.Text = _loc["LoginSignedOut"];
            }
            else
            {
                // 「移除」连着它下面的模型和机密一起走,与设置页删供应商同一套语义
                await _store.DeleteProviderAsync(_settings, provider, _context.Shutdown).ConfigureAwait(true);
                // 删除后重建幸存者的表单;不能让旧表单指向另一份实例。
                if (ReferenceEquals(_secondary, secondary))
                {
                    _focusedProviderId = null;
                    if (Existing(row.Entry) is not null) Expand(row);
                    else Collapse();
                    _progress.Text = _loc["SetupRemoved"];
                }
            }
            await RefreshStatusAsync().ConfigureAwait(true);
            ProviderChanged?.Invoke(provider.Id);
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            if (ReferenceEquals(_secondary, secondary))
                await ReloadConflictedFormAsync(row.Entry, primary).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Removing provider '{provider.Name}' failed: {ex.Message}");
            if (ReferenceEquals(_secondary, secondary))
                progress.Text = _loc.F("LoginFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
        }
        finally
        {
            secondary.IsEnabled = true;
            if (!row.Entry.IsSubscription)
            {
                if (ReferenceEquals(_removingPrimary, primary)) _removingPrimary = null;
                if (ReferenceEquals(_secondary, secondary))
                {
                    primary.IsEnabled = another.IsEnabled = true;
                }
            }
        }
    }

    /// <summary>OAuth 第二账号沿用接入配置,但使用独立供应商和模型 ID,不复制机密。</summary>
    private AiProvider CloneForAnother(AiProvider source)
    {
        return new AiProvider
        {
            Name = NextName(source.Name),
            BaseUrl = source.BaseUrl,
            DefaultProtocol = source.DefaultProtocol,
            Auth = source.Auth,
            CatalogId = source.CatalogId,
            OAuth = source.OAuth?.Clone(),
            AvailableModels = [.. source.AvailableModels],
            ModelsExpanded = source.ModelsExpanded,
            StoreResponses = source.StoreResponses,
            AllowSystemMessages = source.AllowSystemMessages,
            UnsupportedParameters = source.UnsupportedParameters,
            Models = [.. source.Models.Select(m => CloneModel(m))]
        };

        // 保留协议与请求参数;清除旧账号的模型专属端点和机密继承标记。
        static AiModelConfig CloneModel(AiModelConfig m) => new()
        {
            // Id 走属性默认值(新 Guid)—— ActiveModelId 与机密键都指着它,绝不能与原件共用
            Name = m.Name,
            Model = m.Model,
            Protocol = m.Protocol,
            // 新账号不得把新凭据发送到旧模型专属主机。
            BaseUrlOverride = null,
            MaxTokens = m.MaxTokens,
            MaxInputTokens = m.MaxInputTokens,
            LastFetchedSpec = m.LastFetchedSpec,
            DefaultContextWindow = m.DefaultContextWindow,
            ContextWindowIsManual = m.ContextWindowIsManual,
            PromptCaching = m.PromptCaching,
            Temperature = m.Temperature,
            TopP = m.TopP,
            StopSequences = m.StopSequences,
            SystemPrompt = m.SystemPrompt,
            InputPricePerMillion = m.InputPricePerMillion,
            OutputPricePerMillion = m.OutputPricePerMillion,
            CachedInputPricePerMillion = m.CachedInputPricePerMillion,
            Reasoning = m.Reasoning,
            SupportsReasoning = m.SupportsReasoning
        };
    }

    /// <summary>两份同名地摆在设置页左栏里分不出谁是谁 —— 撞名就顺手加个序号(「OpenAI 2」)。</summary>
    private string NextName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName) || _settings.Providers.All(p => p.Name != baseName))
        {
            return baseName;
        }
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (_settings.Providers.All(p => p.Name != candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>首次创建供应商或保存当前供应商的主 Key。</summary>
    private async Task AddOrSaveKeyAsync(ProviderCatalogEntry entry)
    {
        AiProvider? existing = _newProviderDraft is null ? Existing(entry) : null;
        if (_focusedProviderId is not null && existing is null)
        {
            _progress.Text = _loc["SetupSourceRemoved"];
            return;
        }
        TextBox keyBox = _keyBox;
        bool OwnsForm() => _openId == entry.Id && ReferenceEquals(keyBox, _keyBox)
            && !_addingKey && !_addingAnother && !ReferenceEquals(_primary, _removingPrimary);
        if (!OwnsForm()) return;
        bool created = existing is null;
        string expectedProviderJson = _providerFormBaseline!;
        AiProvider draft = JsonSerializer.Deserialize<AiProvider>(expectedProviderJson)!;
        AiProvider provider = existing ?? draft;
        FormInputs submittedInputs = CaptureFormInputs();
        ApplyForm(draft, entry);
        string? keyId = _primaryKeyId;
        Task<string?> keyBaseline = _primaryKeyBaseline;
        if (IsPlaceholder(draft.BaseUrl))
        {
            _progress.Text = _loc["SetupNeedsBaseUrl"];
            return;
        }
        // 框里是空的且库里已经有一把,那是"没改 Key,只改了别的",别把它清掉
        string? key = keyBox.Text;
        bool keyWasEdited = _keyEdited;
        string? committedKey;
        if (!OwnsForm() || existing is not null && !_settings.Providers.Contains(existing)) return;
        if (created)
        {
            await _store.CreateProviderAsync(_settings, draft, apiKey: key,
                cancellationToken: _context.Shutdown).ConfigureAwait(true);
            committedKey = string.IsNullOrWhiteSpace(key) ? null : key;
            if (OwnsForm())
            {
                _newProviderDraft = null;
                _focusedProviderId = provider.Id;
                _secondary.Content = _loc["SetupRemove"];
                _secondary.IsEnabled = false;
            }
        }
        else
        {
            string? replacement = !string.IsNullOrWhiteSpace(key) && keyWasEdited ? key : null;
            string? expectedKey = await keyBaseline.ConfigureAwait(true);
            if (!OwnsForm()) return;
            await _store.SaveProviderApiKeyFormAsync(_settings, provider, draft, expectedProviderJson, replacement,
                keyId: keyId!, expectedKey: expectedKey, cancellationToken: _context.Shutdown);
            committedKey = replacement ?? expectedKey;
        }
        if (ReferenceEquals(keyBox, _keyBox))
        {
            _providerFormBaseline = JsonSerializer.Serialize(provider);
            _formInputBaseline = submittedInputs;
            _primaryKeyId = provider.Id;
            _primaryKeyBaseline = Task.FromResult(committedKey);
        }
        if (ReferenceEquals(keyBox, _keyBox)) _keyEdited = keyBox.Text != key;
        if (ReferenceEquals(keyBox, _keyBox)) _conflictValues.IsVisible = false;
        if (ReferenceEquals(keyBox, _keyBox)) _progress.Text = created ? _loc["SetupAdded"] : _loc["Saved"];
        await RefreshStatusAsync().ConfigureAwait(true);
        ProviderChanged?.Invoke(provider.Id);
        // 「再添加一个」跟着落库就露出 —— 按钮收尾(PrimaryAsync 的 finally)要等下面这次
        // 拉取模型的网络请求回来才跑,慢网下能等到 30 秒,期间想加第二份却没钮可按
        // (review⑨)。此刻必然是"已落库、不在起草态",与收尾那句的条件一致。
        if (ReferenceEquals(keyBox, _keyBox) && !ReferenceEquals(_primary, _removingPrimary))
        {
            _another.IsVisible = true;
            _secondary.IsEnabled = true;
        }
        await PullModelsAsync(entry, provider).ConfigureAwait(true);
    }


    // OAuth 身份不能只看 origin:同 host 的 /tenant-a 与 /tenant-b 也会签不同凭据。
    private static bool SameOAuthIssuer(string? a, string? b)
        => string.Equals(a, b, StringComparison.Ordinal);

    private (string? Api, string? Auth, string? Device, string? Token, string? Client) CurrentScopesOrigin()
        => (_baseUrlBox.Text, _authUrlBox.Text, _deviceUrlBox.Text, _tokenUrlBox.Text, _clientIdBox.Text);

    private void ClearScopesOnIssuerChange()
    {
        if (_scopesForOrigin is not { } previous) return;
        (string? Api, string? Auth, string? Device, string? Token, string? Client) current = CurrentScopesOrigin();
        if (SameOAuthIssuer(previous.Api, current.Api)
            && SameOAuthIssuer(previous.Auth, current.Auth)
            && SameOAuthIssuer(previous.Device, current.Device)
            && SameOAuthIssuer(previous.Token, current.Token)
            && SameOAuthIssuer(previous.Client, current.Client)) return;
        _scopesForOrigin = current;
        _scopesBox.Text = "";
    }

    /// <summary>
    /// 订阅登录那一路。<b>先登成功再把供应商写进设置</b> —— 登录失败(或用户中途放弃)时,
    /// 列表里不该多出一个连不上的空壳。已经加过的那家默认原地重登、不产生副本;
    /// 「再添加一个」按下后则落到一个新实例上(独立 <c>oauth:&lt;id&gt;</c>,即多账号)。
    /// </summary>
    private async Task SignInAsync(Row row)
    {
        ProviderCatalogEntry entry = row.Entry;
        if (!entry.NeedsOAuthSetup && !AiSettingsStore.SameOrigin(entry.CreateProvider().BaseUrl, _baseUrlBox.Text))
        {
            // 内置 OAuth 凭据只属于官方 API 主机;中转站需另建自定义接入并提供自己的凭据。
            _progress.Text = _loc["SetupBuiltinOAuthHostMismatch"];
            return;
        }
        AiProvider? existing = _newProviderDraft is not null && !_addingAnother ? null : Existing(entry);
        if (_focusedProviderId is not null && existing is null)
        {
            _progress.Text = _loc["SetupSourceRemoved"];
            return;
        }
        bool created = _newProviderDraft is not null;
        string expectedProviderJson = _providerFormBaseline!;
        AiProvider provider = JsonSerializer.Deserialize<AiProvider>(expectedProviderJson)!;
        AiProvider sourceProvider = JsonSerializer.Deserialize<AiProvider>(expectedProviderJson)!;
        if (created && _addingAnother && entry.NeedsOAuthSetup)
        {
            ClearScopesOnIssuerChange(); // TextChanged 可能尚未派发,提交前也必须核对实际目标
        }
        FormInputs submittedInputs = CaptureFormInputs();
        ApplyForm(provider, entry);
        if (IsPlaceholder(provider.BaseUrl))
        {
            _progress.Text = _loc["SetupNeedsBaseUrl"];
            return;
        }
        // TextChanged 可能排在点击事件之后:提交时以当前输入框的地址为准再查一遍。
        // 若新签发地址尚未清空旧授权参数,这里同步清掉并拒绝本次登录,绝不发旧 issuer 的 token。
        if (created && _addingAnother && entry.NeedsOAuthSetup
            && !SameOAuthIssuer(_oauthConfiguredBaseUrl, provider.BaseUrl))
        {
            _oauthConfiguredBaseUrl = provider.BaseUrl;
            _clientIdBox.Text = _authUrlBox.Text = _tokenUrlBox.Text = _deviceUrlBox.Text = _scopesBox.Text = "";
            ClearScopesOnIssuerChange();
            _progress.Text = _loc["SetupNeedsOAuth"];
            return;
        }
        OAuthConfig oauth = provider.OAuth!;
        if (entry.NeedsOAuthSetup && sourceProvider.OAuth is { } sourceOAuth
            && (!SameOAuthIssuer(created && _addingAnother ? _oauthSourceBaseUrl : sourceProvider.BaseUrl, provider.BaseUrl)
                || !SameOAuthIssuer(sourceOAuth.TokenUrl, oauth.TokenUrl)
                || !SameOAuthIssuer(sourceOAuth.DeviceCodeUrl, oauth.DeviceCodeUrl)
                || !SameOAuthIssuer(sourceOAuth.AuthorizationUrl, oauth.AuthorizationUrl)
                || !string.Equals(sourceOAuth.ClientId, oauth.ClientId, StringComparison.Ordinal)))
        {
            // 可见输入由 ApplyForm 覆盖;换 API 或授权端点的完整地址时,
            // 旧 issuer 的隐藏凭据和参数不得进入任何授权请求。
            oauth.ClientSecret = "";
            oauth.ExtraHeaders = "";
            oauth.ExchangeHeaders = "";
            oauth.ExchangeUrl = "";
            oauth.ExtraAuthorizeParams = "";
            oauth.AccountIdClaim = "";
        }
        if (!provider.CanSignIn)
        {
            _progress.Text = _loc["SetupNeedsOAuth"];
            return;
        }

        _secondary.Content = _loc["Cancel"];
        _secondary.IsVisible = true;
        // 登录期间「再添加一个」藏起来:点击虽已被 StartAnother 挡住,但摆着一颗按了没反应的钮
        // 只会让用户怀疑是不是卡了
        _another.IsVisible = false;
        _deviceCodePanel.IsVisible = false;
        _progress.Text = _loc["LoginStarting"];
        // 取消源在这儿开、在这儿收:CancelPendingLogin 只置 null 不 Dispose,
        // 否则窗口关掉之后再有人碰它就是一个 ObjectDisposedException。
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        _login = cancellation;
        TextBlock loginProgress = _progress;
        TextBlock deviceCode = _deviceCodeText;
        StackPanel devicePanel = _deviceCodePanel;
        Button loginSecondary = _secondary;
        Button loginAnother = _another;

        var prompts = new LoginPrompts(
            _loc["LoginPageTitle"], _loc["LoginPageBody"],
            _loc["LoginWaiting"], _loc["LoginExchanging"], _loc["LoginUserCode"]);
        var progress = new Progress<LoginProgress>(step => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_login, cancellation) || !ReferenceEquals(_progress, loginProgress))
            {
                return;
            }
            loginProgress.Text = step.Message;
            if (step.Device is { } device)
            {
                deviceCode.Text = device.UserCode;
                devicePanel.IsVisible = true;
            }
        }));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var login = new ProviderLogin(new OAuthClient(http), LaunchAsync);
        try
        {
            OAuthTokens tokens = await login.SignInAsync(provider.OAuth!, prompts, progress, cancellation.Token)
                .ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_secondary, loginSecondary)) return;
            if (existing is not null && (!ReferenceEquals(Existing(entry), existing)
                || !_settings.Providers.Contains(existing)))
            {
                loginProgress.Text = _loc["SetupSourceRemoved"];
                return;
            }
            if (created)
            {
                await _store.CreateProviderAsync(_settings, provider, tokens: tokens,
                    cancellationToken: cancellation.Token).ConfigureAwait(true);
                if (ReferenceEquals(_secondary, loginSecondary))
                {
                    _focusedProviderId = provider.Id;
                    _addingAnother = false;
                    _newProviderDraft = null;
                    loginSecondary.Content = _loc["SetupSignOut"];
                    loginSecondary.IsEnabled = false;
                }
            }
            else
            {
                await _store.SaveProviderOAuthFormAsync(_settings, existing!, provider, expectedProviderJson,
                    tokens, cancellation.Token).ConfigureAwait(true);
                provider = existing!;
            }
            if (ReferenceEquals(_secondary, loginSecondary))
            {
                _providerFormBaseline = JsonSerializer.Serialize(provider);
                _formInputBaseline = submittedInputs;
                _conflictValues.IsVisible = false;
            }
        }
        finally
        {
            // 取消覆盖登录和原子提交;成功后立即释放,不妨碍模型拉取期间开启下一份草稿。
            if (ReferenceEquals(_login, cancellation)) _login = null;
        }
        if (ReferenceEquals(_progress, loginProgress)) loginProgress.Text = _loc["LoginDone"];
        await RefreshStatusAsync().ConfigureAwait(true);
        ProviderChanged?.Invoke(provider.Id);
        // 同 AddOrSaveKeyAsync:按钮收尾排在拉取模型之后,落库了就先露出「再添加一个」,
        // 别让一次慢网拉取把"加下一份"的入口扣住 30 秒(同样的收尾条件,review⑨)。
        if (ReferenceEquals(_secondary, loginSecondary))
        {
            loginAnother.IsVisible = true;
            loginSecondary.IsEnabled = true;
        }
        await PullModelsAsync(entry, provider).ConfigureAwait(true);
    }

    /// <summary>
    /// 接上之后把这一家的模型清单配好:id、上下文窗口、三档单价一次填齐。
    /// </summary>
    /// <remarks>
    /// 清单先问端点自己的 <c>/models</c>(只有它知道这个地址实际供应什么),再拿 id 去
    /// <see cref="ModelsDevCatalog" />(models.dev)配窗口与单价;未匹配时保留接口 context_length。
    /// 订阅型私有后端可能没有清单接口。两条路的取舍全在 <see cref="ModelPull" /> 里。
    /// <para>
    /// 放在登录/添加<b>成功之后</b>单独跑,拉不到也只是少个便利:
    /// 这一步失败不该让"已经连上了"这个结果打折扣,更不该把异常冒到登录的错误处理里去。
    /// </para>
    /// </remarks>
    private async Task PullModelsAsync(ProviderCatalogEntry entry, AiProvider provider, bool force = false)
    {
        if (string.IsNullOrEmpty(entry.ModelsDevId) && string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return; // 两条路都不通:目录没收录,地址也还空着,保持出厂示例即可
        }
        TextBlock progress = _progress;
        string requestedBaseUrl = provider.BaseUrl;
        string providerJson = JsonSerializer.Serialize(provider);
        AiProvider before = JsonSerializer.Deserialize<AiProvider>(providerJson)!;
        AiProvider catalogue = JsonSerializer.Deserialize<AiProvider>(providerJson)!;
        bool OwnsForm() => ReferenceEquals(_progress, progress) && ReferenceEquals(Existing(entry), provider);
        bool ShowsCandidates() => !_addingKey && _openId == entry.Id && ReferenceEquals(Existing(entry), provider)
            && string.Equals(requestedBaseUrl, _baseUrlBox.Text?.Trim(), StringComparison.Ordinal)
            && (!_addingAnother || _cloneSourceId == provider.Id);
        bool committingCatalogue = false;
        try
        {
            if (OwnsForm())
            {
                progress.Text = _loc["ModelsPulling"];
            }
            ModelPullResult result = await ModelPull
                                           .RunAsync(provider, entry.ModelsDevId, _models, _store, force: force,
                                               materialiseInto: catalogue)
                                           .ConfigureAwait(true);
            if (result.Source == ModelSource.None)
            {
                if (ShowsCandidates()) UpdateModelPicker(provider);
                if (OwnsForm()) progress.Text = _loc["ModelsNone"];
                return;
            }
            if (!_settings.Providers.Contains(provider) || provider.BaseUrl != requestedBaseUrl) return;
            Button baselinePrimary = _primary;
            string? baselineJson = !_addingAnother && !_addingKey && _openId == entry.Id
                && ReferenceEquals(Existing(entry), provider) ? _providerFormBaseline : null;
            AiProvider? baseline = baselineJson is null ? null : JsonSerializer.Deserialize<AiProvider>(baselineJson);
            List<string>? changedModels = _health is null ? null : [];
            string expectedProviderJson = JsonSerializer.Serialize(provider);
            AiProvider draft = JsonSerializer.Deserialize<AiProvider>(expectedProviderJson)!;
            MergeCatalogue(draft, before, catalogue, baseline, changedModels);
            int added = draft.Models.Count;
            committingCatalogue = true;
            await _store.SaveProviderCatalogueAsync(_settings, provider, draft, expectedProviderJson,
                _context.Shutdown).ConfigureAwait(true);
            if (changedModels is not null)
                foreach (string id in changedModels) _health!.Invalidate(id);
            if (baseline is not null && ReferenceEquals(_primary, baselinePrimary)
                && _providerFormBaseline == baselineJson)
                _providerFormBaseline = JsonSerializer.Serialize(baseline);
            ModelsChanged?.Invoke();
            // 重新展开或起草同址克隆时,只更新型号候选,不覆写未保存字段。
            if (ShowsCandidates()) UpdateModelPicker(provider);
            if (OwnsForm() && !_addingAnother) progress.Text = _loc.F("ModelsPulled", added);
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            if (OwnsForm())
            {
                if (committingCatalogue) progress.Text = _loc["SetupConfigChanged"];
                else await ReloadConflictedFormAsync(entry, _primary).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.Log.Warn($"Loading the model catalogue for '{provider.Name}' failed: {ex.Message}");
            if (OwnsForm())
            {
                progress.Text = _loc["ModelsNone"];
            }
        }
    }
    // 目录只接纳自己实际改过的规格字段;网络期间的外部编辑不能混进表单基线。
    internal static void MergeCatalogue(AiProvider target, AiProvider before, AiProvider catalogue,
        AiProvider? baseline, List<string>? changedModels)
    {
        if (!target.AvailableModels.SequenceEqual(catalogue.AvailableModels)
            && target.AvailableModels.SequenceEqual(before.AvailableModels))
        {
            target.AvailableModels = [.. catalogue.AvailableModels];
            if (baseline is not null && baseline.AvailableModels.SequenceEqual(before.AvailableModels))
                baseline.AvailableModels = [.. catalogue.AvailableModels];
        }
        if (target.ModelsExpanded != catalogue.ModelsExpanded && target.ModelsExpanded == before.ModelsExpanded)
        {
            target.ModelsExpanded = catalogue.ModelsExpanded;
            if (baseline is not null && baseline.ModelsExpanded == before.ModelsExpanded)
                baseline.ModelsExpanded = catalogue.ModelsExpanded;
        }
        foreach (AiModelConfig pulled in catalogue.Models)
        {
            AiModelConfig? original = before.Models.Find(model => model.Id == pulled.Id);
            AiModelConfig? current = target.Models.Find(model => model.Id == pulled.Id);
            AiModelConfig? opened = baseline?.Models.Find(model => model.Id == pulled.Id);
            if (original is null)
            {
                if (current is null && !target.Models.Any(model => string.Equals(model.Model, pulled.Model, StringComparison.OrdinalIgnoreCase)))
                {
                    string json = JsonSerializer.Serialize(pulled);
                    target.Models.Add(JsonSerializer.Deserialize<AiModelConfig>(json)!);
                    if (baseline is not null && opened is null
                        && !baseline.Models.Any(model => string.Equals(model.Model, pulled.Model, StringComparison.OrdinalIgnoreCase)))
                        baseline.Models.Add(JsonSerializer.Deserialize<AiModelConfig>(json)!);
                }
                continue;
            }
            if (current is null || current.Model != original.Model) continue;
            string model = current.Model;
            int? openedInput = opened?.MaxInputTokens;
            int input = current.MaxInputTokens, output = current.MaxTokens;
            bool? reasoning = current.SupportsReasoning;
            double priceIn = current.InputPricePerMillion, priceOut = current.OutputPricePerMillion,
                priceCached = current.CachedInputPricePerMillion;
            if (pulled.Model != original.Model)
            {
                current.Model = pulled.Model;
                if (opened is not null && opened.Model == original.Model) opened.Model = pulled.Model;
            }
            if (!current.ContextWindowIsManual && pulled.MaxInputTokens != original.MaxInputTokens
                && current.MaxInputTokens == original.MaxInputTokens)
            {
                current.MaxInputTokens = pulled.MaxInputTokens;
                if (opened is not null && opened.MaxInputTokens == original.MaxInputTokens) opened.MaxInputTokens = pulled.MaxInputTokens;
            }
            if (pulled.MaxTokens != original.MaxTokens && current.MaxTokens == original.MaxTokens)
            {
                current.MaxTokens = pulled.MaxTokens;
                if (opened is not null && opened.MaxTokens == original.MaxTokens) opened.MaxTokens = pulled.MaxTokens;
            }
            if (pulled.InputPricePerMillion != original.InputPricePerMillion && current.InputPricePerMillion == original.InputPricePerMillion)
            {
                current.InputPricePerMillion = pulled.InputPricePerMillion;
                if (opened is not null && opened.InputPricePerMillion == original.InputPricePerMillion) opened.InputPricePerMillion = pulled.InputPricePerMillion;
            }
            if (pulled.OutputPricePerMillion != original.OutputPricePerMillion && current.OutputPricePerMillion == original.OutputPricePerMillion)
            {
                current.OutputPricePerMillion = pulled.OutputPricePerMillion;
                if (opened is not null && opened.OutputPricePerMillion == original.OutputPricePerMillion) opened.OutputPricePerMillion = pulled.OutputPricePerMillion;
            }
            if (pulled.CachedInputPricePerMillion != original.CachedInputPricePerMillion && current.CachedInputPricePerMillion == original.CachedInputPricePerMillion)
            {
                current.CachedInputPricePerMillion = pulled.CachedInputPricePerMillion;
                if (opened is not null && opened.CachedInputPricePerMillion == original.CachedInputPricePerMillion)
                    opened.CachedInputPricePerMillion = pulled.CachedInputPricePerMillion;
            }
            if (pulled.SupportsReasoning != original.SupportsReasoning && current.SupportsReasoning == original.SupportsReasoning)
            {
                current.SupportsReasoning = pulled.SupportsReasoning;
                if (opened is not null && opened.SupportsReasoning == original.SupportsReasoning)
                    opened.SupportsReasoning = pulled.SupportsReasoning;
            }
            if (!current.ContextWindowIsManual && input == original.MaxInputTokens
                && current.DefaultContextWindow == original.DefaultContextWindow)
            {
                current.DefaultContextWindow = pulled.DefaultContextWindow;
                if (opened is not null && openedInput == original.MaxInputTokens
                    && opened.DefaultContextWindow == original.DefaultContextWindow)
                    opened.DefaultContextWindow = pulled.DefaultContextWindow;
            }
            if (original.LastFetchedSpec is { } previous && pulled.LastFetchedSpec is { } fetched
                && current.LastFetchedSpec == previous)
            {
                current.LastFetchedSpec = fetched with
                {
                    ContextTokens = input == original.MaxInputTokens ? fetched.ContextTokens : previous.ContextTokens,
                    OutputTokens = output == original.MaxTokens ? fetched.OutputTokens : previous.OutputTokens,
                    InputPrice = priceIn == original.InputPricePerMillion ? fetched.InputPrice : previous.InputPrice,
                    OutputPrice = priceOut == original.OutputPricePerMillion ? fetched.OutputPrice : previous.OutputPrice,
                    CachedInputPrice = priceCached == original.CachedInputPricePerMillion ? fetched.CachedInputPrice : previous.CachedInputPrice,
                    Reasoning = reasoning == original.SupportsReasoning ? fetched.Reasoning : previous.Reasoning
                };
                if (opened?.LastFetchedSpec == previous) opened.LastFetchedSpec = current.LastFetchedSpec;
            }
            if (current.Model != model || current.MaxInputTokens != input || current.MaxTokens != output
                || current.SupportsReasoning != reasoning) changedModels?.Add(current.Id);
        }
    }


    /// <summary>把表单里的值搬进供应商对象。没摆出来的控件带的就是目录出厂值,照读即可。</summary>
    private void ApplyForm(AiProvider provider, ProviderCatalogEntry entry)
    {
        provider.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? entry.Name : _nameBox.Text.Trim();
        if (_addingAnother && !string.Equals(provider.BaseUrl, _baseUrlBox.Text?.Trim() ?? "", StringComparison.Ordinal))
        {
            provider.AvailableModels.Clear(); // 克隆的新地址不沿用源服务器的模型清单
            if (_modelPickerRow is not null)
            {
                _modelPickerRow.IsVisible = false;
            }
        }
        provider.BaseUrl = _baseUrlBox.Text?.Trim() ?? "";
        if (ProtocolSelectable(entry) && _protocolCombo.SelectedIndex >= 0)
        {
            ChatProtocol selected = (ChatProtocol)_protocolCombo.SelectedIndex;
            // 下拉显示的是首模型的**生效**协议(见 BuildDetail 处的初始化):与生效值一致
            // 就什么都没改;不一致才是用户动了它 —— 此时清掉首模型的覆盖、把新值落成默认,
            // 让实际请求跟随刚选定的显示值。拿"等于旧默认值"当条件会漏掉"覆盖与默认不同"
            // 的情形:显示默认、实际走覆盖,用户选了显示的那个,等于没动默认值,
            // 覆盖就地残留,保存后实际仍走覆盖(review②)。
            ChatProtocol effective = provider.Models.Count > 0 && provider.Models[0].Protocol is { } own
                ? own
                : provider.DefaultProtocol;
            if (selected != effective)
            {
                if (provider.Models.Count > 0)
                {
                    provider.Models[0].Protocol = null;
                }
                provider.DefaultProtocol = selected;
            }
        }
        if (provider.Models.Count == 0)
        {
            provider.Models.Add(new AiModelConfig());
        }
        provider.Models[0].Model = _modelBox.Text?.Trim() ?? "";
        if (!entry.IsSubscription)
        {
            return;
        }
        OAuthConfig oauth = provider.OAuth ??= new OAuthConfig();
        oauth.ClientId = _clientIdBox.Text?.Trim() ?? "";
        if (!entry.NeedsOAuthSetup)
        {
            return; // 内置项的端点是目录给的,不让界面覆盖掉
        }
        oauth.Flow = _flowCombo.SelectedIndex == 1 ? OAuthFlow.DeviceCode : OAuthFlow.AuthorizationCodePkce;
        oauth.AuthorizationUrl = _authUrlBox.Text?.Trim() ?? "";
        oauth.TokenUrl = _tokenUrlBox.Text?.Trim() ?? "";
        oauth.DeviceCodeUrl = _deviceUrlBox.Text?.Trim() ?? "";
        oauth.Scopes = _scopesBox.Text?.Trim() ?? "";
    }

    private async Task LaunchAsync(Uri uri, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
            return;
        }
        // 拿不到 Launcher(理论上只在没挂进窗口时)—— 把地址显示出来让用户自己开,别静默失败。
        // 但<b>日志里只留主机名</b>:完整授权地址带着 state 与 code_challenge,
        // 那是一次登录握手的一半,不该躺在日志文件里(见汇报 §10.2)。
        _context.Log.Warn($"No launcher available; asking the user to open the sign-in page at {uri.Host} manually.");
        _progress.Text = uri.ToString();
    }

    private async Task<string?> LoadKeyAsync(AiProvider source, TextBox target)
    {
        try
        {
            string? key = await _store.GetApiKeyAsync(source.Id).ConfigureAwait(true);
            // 旧异步读回来的值不能写进另一张表单,也不能覆盖用户刚刚手填的 Key。
            if (!ReferenceEquals(target, _keyBox) || _keyEdited || !string.IsNullOrEmpty(target.Text))
            {
                return key;
            }
            _keyPrefilling = true;
            try { target.Text = key ?? ""; }
            finally { _keyPrefilling = false; }
            return key;
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Reading the stored key failed: {ex.Message}");
            throw;
        }
    }

    // ---- 状态灯 ----

    /// <summary>
    /// 刷新每一行右侧那枚状态与行尾按钮。要读机密所以是异步的,一次把整页读完 ——
    /// 每行各发一次会在打开窗口时把机密存储敲十几下。
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        _connected.Clear();
        _keyed.Clear();
        foreach (AiProvider provider in _settings.Providers)
        {
            if (await _store.GetTokensAsync(provider.Id).ConfigureAwait(true) is not null)
            {
                _connected.Add(provider.Id);
            }
            foreach (string keyId in _store.ProviderApiKeyIds(provider))
            {
                if (string.IsNullOrWhiteSpace(await _store.GetApiKeyAsync(keyId).ConfigureAwait(true))) continue;
                _keyed.Add(provider.Id);
                break;
            }
        }
        foreach (Row row in _cards)
        {
            (string label, string brush) = StatusOf(row.Entry);
            row.Pill.Text = label;
            row.Dot[!Shape.FillProperty] = new DynamicResourceExtension(brush);
        }
    }

    private (string Label, string Brush) StatusOf(ProviderCatalogEntry entry)
    {
        // 同一家可能有多份,行上那枚状态灯按<b>整家</b>算:任何一份可用就是可用 ——
        // 只盯最近一份的话,新加的那份没配好会把早就配好的那份也说成不可用
        List<AiProvider> mine = Matching(entry);
        if (mine.Count == 0)
        {
            return entry.IsSubscription
                ? (_loc["StatusNotConnected"], "VelaTextMuted")
                : (_loc["StatusNotAdded"], "VelaTextMuted");
        }
        if (entry.IsSubscription)
        {
            return mine.Any(p => _connected.Contains(p.Id))
                ? (_loc["StatusConnected"], "VelaShellGreen")
                : (_loc["StatusNotConnected"], "VelaWarning");
        }
        return !NeedsKey(entry) || mine.Any(p => _keyed.Contains(p.Id))
            ? (_loc["StatusReady"], "VelaShellGreen")
            : (_loc["StatusNeedsKey"], "VelaWarning");
    }

    /// <summary>设置里同一家的<b>全部</b>实例(目录 id 相同即同一家,允许加多份)。</summary>
    private List<AiProvider> Matching(ProviderCatalogEntry entry)
        => [.. _settings.Providers.Where(p => p.CatalogId == entry.Id)];

    /// <summary>查找表单绑定的实例;仅尚未绑定的首次目录展开默认最新,失效绑定不回退。</summary>
    private AiProvider? Existing(ProviderCatalogEntry entry)
        => _focusedProviderId is { } id
            ? _settings.Providers.Find(p => p.Id == id && p.CatalogId == entry.Id)
            : _settings.Providers.FindLast(p => p.CatalogId == entry.Id);

    // ---- 小零件 ----

    private static TextBlock Label(string text) => new() { Classes = { "label" }, Text = text };

    /// <summary>一枚警示色的小标(目前只有"实验性"用它)。</summary>
    private static Border Badge(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label[!ForegroundProperty] = new DynamicResourceExtension("VelaWarning");
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");
        var badge = new Border
        {
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label
        };
        badge[!BorderBrushProperty] = new DynamicResourceExtension("VelaWarning");
        return badge;
    }

    private static TextBlock Hint(string text) => new() { Classes = { "hint" }, Text = text };

    private static TextBox Mono(TextBox box)
    {
        box[!FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        return box;
    }

    /// <summary>一枚"去这儿注册"的按钮,点了开浏览器。</summary>
    private Button LinkRow(string text, string url)
    {
        var button = new Button
        {
            Content = text,
            Height = 24,
            Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        button.Click += (_, _) =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                _ = LaunchAsync(uri, CancellationToken.None);
            }
        };
        return button;
    }

    /// <summary>Key 输入框 + 右边一枚"看一眼"的眼睛,与设置页同一形状。</summary>
    private static Grid KeyRow(TextBox box)
    {
        var reveal = new ToggleButton { Width = 30, Height = 30, Padding = new Thickness(0) };
        reveal[!ThemeProperty] = new DynamicResourceExtension("AiChipToggleTheme");
        var eye = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        eye[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("Icon.eye");
        eye[!Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextSecondary");
        reveal.Content = new Viewbox { Width = 13, Height = 13, Child = eye };
        reveal.IsCheckedChanged += (_, _) => box.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        var grid = new Grid { ColumnDefinitions = [with("*,6,Auto")] };
        Grid.SetColumn(reveal, 2);
        grid.Children.Add(box);
        grid.Children.Add(reveal);
        return grid;
    }
}
