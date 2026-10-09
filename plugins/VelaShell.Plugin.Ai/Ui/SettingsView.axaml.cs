using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>左栏一行:供应商行(<see cref="Model" /> 为 null)或其下的模型行。</summary>
/// <remarks>
/// 是<b>类</b>不是 record:行尾那颗连通性状态点要在"测试"跑完的那一刻就地变色。
/// record 是不可变的,只能靠重建整个列表来刷新 —— 而重建会重置选中项、
/// 进而触发 <c>LoadEditorAsync</c> 把用户表单里还没保存的改动冲掉。
/// </remarks>
public sealed class ProviderNavItem(
    AiProvider provider, AiModelConfig? model, string text, Thickness indent, FontWeight weight,
    int modelCount = 0, bool expanded = false, string toggleTip = "")
    : System.ComponentModel.INotifyPropertyChanged
{

    /// <summary>这一行所属的供应商(模型行也指向它的父供应商)。</summary>
    public AiProvider Provider { get; } = provider;

    /// <summary>模型行指向的模型;供应商行为 null。</summary>
    public AiModelConfig? Model { get; } = model;

    /// <summary>行上显示的名字。</summary>
    public string Text { get; } = text;

    /// <summary>左缩进:模型行比供应商行进一档。</summary>
    public Thickness Indent { get; } = indent;

    /// <summary>字重:供应商行加粗。</summary>
    public FontWeight Weight { get; } = weight;

    /// <summary>
    /// 层级图标:供应商 = 云,模型 = 方块。几何在 <c>RefreshNavVisuals</c> 里解析 ——
    /// 视图挂进可视树之后还要再解析一次,所以跟 <see cref="Dot" /> 一样走通知。
    /// </summary>
    public Geometry? Icon
    {
        get;
        set => Set(ref field, value, nameof(Icon));
    }

    /// <summary>
    /// 图标描边色:比同一行的文字再暗一档,选中时和文字一起转强调色。
    /// 文字的三档在样式里(<c>DialogStyles.axaml</c> 的 nav 选择器),图标这一档只能逐项算,
    /// 所以跟 <see cref="Dot" /> 一样走通知 —— 换选中项时就地改色,不重建列表。
    /// </summary>
    public IBrush? Tint
    {
        get;
        set => Set(ref field, value, nameof(Tint));
    }

    /// <summary>是不是供应商行。</summary>
    public bool IsProvider => Model is null;

    /// <summary>这一家下面挂了几个模型(模型行为 0)。</summary>
    public int ModelCount { get; } = modelCount;

    /// <summary>这一家的模型列表此刻展着没有(模型行无意义)。</summary>
    public bool Expanded { get; } = expanded;

    /// <summary>有折叠箭头的行:挂着模型的供应商行。</summary>
    public bool CanCollapse => IsProvider && ModelCount > 0;

    /// <summary>
    /// 折起来时也得看得出这一家有多少个模型 —— 数字就贴在名字后面。
    /// </summary>
    /// <remarks>展开时也照显:一家有 300 个还是 3 个,是决定"要不要折起来"的依据本身。</remarks>
    public string CountText => CanCollapse ? ModelCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>折叠箭头的悬停说明(展开态与折叠态是两句话)。</summary>
    public string ToggleTip { get; } = toggleTip;

    /// <summary>状态点的颜色:灰 = 本次窗口内没测过,绿 = 通过,红 = 失败。</summary>
    public IBrush? Dot
    {
        get;
        set => Set(ref field, value, nameof(Dot));
    }

    /// <summary>状态点的悬停说明。</summary>
    public string DotTip
    {
        get;
        set => Set(ref field, value, nameof(DotTip));
    } = "";

    /// <summary>图标/状态点变化时通知绑定(<see cref="Icon" /> / <see cref="Dot" /> / <see cref="DotTip" /> / <see cref="Tint" />)。</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 设置页:左栏是"供应商 › 模型"两层树,右侧表单随选中的层切换 ——
/// 供应商管地址 / 默认协议 / 共用 API Key,模型管模型 id 与其余一切(可覆盖协议、Key、地址)。
/// 直接编辑面板共享的 <see cref="AiSettings" /> 实例,保存后经回调通知聊天面板刷新。
/// </summary>
public partial class SettingsView : UserControl
{
    private static readonly string[] ProtocolLabels =
        ["OpenAI Chat Completions", "OpenAI Responses", "Anthropic Messages"];

    /// <summary>思考档位下拉的文案键,顺序与 <see cref="ReasoningLevel" /> 一一对应。</summary>
    private static readonly string[] ReasoningKeys =
        ["ReasoningDefault", "ReasoningOff", "ReasoningLow", "ReasoningMedium", "ReasoningHigh"];

    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Action _onProvidersChanged;

    /// <summary>共享的被动健康记录;「测试」的结果顺手记进去,给故障转移当候选过滤用。</summary>
    private readonly ProviderHealth? _health;

    /// <summary>模型规格库(models.dev):挑模型时顺手把窗口与单价一起填好。</summary>
    private readonly ModelsDevCatalog _models;
    // 只用于公共规格索引；调用方管理所提供客户端的生命周期，端点 /models 保持独立真实请求。
    private readonly HttpClient? _catalogueHttp;
    private List<ProviderNavItem> _nav = [];
    private bool _loadingEditor;
    private bool _refreshingNav;
    private int _authStatusRun;
    private int _editorRun;
    private Task _editorLoad = Task.CompletedTask;
    private string _loadedProviderName = "";
    private string _loadedProviderUrl = "";
    private int _loadedProviderProtocol;
    private string _loadedKey = "";
    private Dictionary<Control, object?> _loadedModelForm = [];
    private ModelSpec? _windowSpecDraft;
    private bool _settingWindowText;
    private bool _windowEdited;
    private int _windowDraftRun;
    private int _windowEditRevision;

    private Dictionary<Control, object?> CaptureModelForm() => new Control[]
    {
        NameBox, ModelBox, ProtocolCombo, OwnKeyCheck, ApiKeyBox, BaseUrlBox, MaxTokensBox,
        MaxInputTokensBox, ReasoningCombo, PromptCacheCheck, TemperatureBox, TopPBox, StopBox,
        PriceInBox, PriceOutBox, PriceCachedBox, ProviderPromptBox
    }.ToDictionary(control => control, control => control switch
    {
        TextBox text => (object?)text.Text,
        ComboBox combo => combo.SelectedIndex,
        CheckBox check => check.IsChecked,
        _ => null
    });

    private async Task RecoverSettingsConflictAsync(AiProvider provider, AiModelConfig? model, int run)
    {
        try { await RecoverSettingsConflictCoreAsync(provider, model, run); }
        catch (Exception ex)
        {
            _context.Log.Error("Reload AI settings failed.", ex);
            if (run == _editorRun && ReferenceEquals(SelectedProvider, provider) && ReferenceEquals(SelectedModel, model))
                StatusText.Text = _loc.F("TestFail", KeyErrorText(ex));
        }
    }

    private async Task RecoverSettingsConflictCoreAsync(AiProvider provider, AiModelConfig? model, int run)
    {
        bool Current() => run == _editorRun && ReferenceEquals(SelectedProvider, provider)
            && ReferenceEquals(SelectedModel, model);
        if (!Current()) return;
        string beforeReload = JsonSerializer.Serialize(_settings);
        await _store.ReloadIntoAsync(_settings);
        if (JsonSerializer.Serialize(_settings) != beforeReload) _onProvidersChanged();
        if (!Current())
        {
            // 导航期间的新页可能已从旧共享配置装载；只用它自己的基线合并最新保存值。
            RefreshCatalogModels();
            await _editorLoad;
            return;
        }
        if (_settings.Providers.Contains(provider) && (model is null || provider.Models.Contains(model)))
        {
            _refreshingNav = true;
            try { ReloadList(model?.Id ?? provider.Id); }
            finally { _refreshingNav = false; }
            if (model is null)
            {
                RefreshEditorContext(provider, null);
                await MergeSavedProviderAsync(provider, run, Task.CompletedTask);
                if (!Current()) return;
                if (_keyDraft is { } keyDraft && KeyRowCurrent(keyDraft, run))
                {
                    keyDraft.OriginalKey = keyDraft.Key;
                    keyDraft.OriginalUrl = provider.BaseUrl;
                    keyDraft.OriginalProtocol = provider.DefaultProtocol;
                }
            }
            else
            {
                await MergeSavedModelAsync(provider, model, run, _editorLoad);
                if (!Current())
                {
                    RefreshCatalogModels();
                    await _editorLoad;
                    return;
                }
            }
        }
        else
        {
            // 原编辑对象已删除：显示当前树，但不把保留的草稿绑定到另一家。
            RefreshCatalogModels();
        }
        if (!Current() && SelectedItem is not null) return;
        StatusText.Text = _loc["SetupConfigChanged"];
    }

    /// <summary>
    /// 本次窗口内"测试"跑出来的结果(键 = 模型 id,供应商行用供应商 id)。
    /// <b>不落盘</b>:它说的是"刚才那一次连通",隔天再打开时那句话已经不成立了 ——
    /// 与其显示一个可能过期的绿点,不如老实退回灰点。
    /// </summary>
    private readonly Dictionary<string, (bool Ok, DateTime At)> _testResults = [with(StringComparer.Ordinal)];

    /// <summary>
    /// 「测试」的全局代次:每点一次 +1。只守按钮复位;行的显示由 <see cref="_testRuns" /> 守。
    /// 共享健康由 <see cref="ProviderHealth" /> 按模型和起跑序号裁决。
    /// </summary>
    private int _testRun;

    /// <summary>每行最新起跑号:供应商行与模型行各自保留状态、状态点和徽章。</summary>
    private readonly Dictionary<string, int> _testRuns = [with(StringComparer.Ordinal)];

    /// <summary>左栏图标的三档描边色(见 <see cref="ApplyTints" />),装载后解析一次。</summary>
    private IBrush? _providerTint;
    private IBrush? _modelTint;
    private IBrush? _selectedTint;

    /// <summary>两击确认删供应商:记着第一击是冲着谁的,换了选择就作废。</summary>
    private string? _pendingDeleteProviderId;
    private readonly List<ProviderKeyRowState> _keyRows = [];
    private ProviderKeyRowState? _keyDraft;
    private ProviderKeyRowState? _savingKeyRow;
    private string? _pendingRemoveKeyId;
    private string? _lastKeyProbeModelId;
    private CancellationTokenSource _keyEditorCts = new();
    private CancellationTokenSource? _allKeyProbeCts;
    private DispatcherTimer? _keyLightTimer;
    private bool _keyMutation;
    private bool _refreshingKeyRowsOnTick;
    private bool _settingBalance;
    private int _keyRowsRun;

    /// <summary>
    /// 新增供应商传 null;管理登录传选中实例 id(同目录的不同账号不可混用)。
    /// 窗口的开合与置前由聊天面板统一管理。
    /// </summary>
    public event Action<string?>? ProviderCatalogRequested;

    /// <summary>模型清单刷新已落库；只刷新派生视图，不作为普通配置保存清空全局健康。</summary>
    public event Action? ModelsChanged;

    /// <summary>由聊天面板构造(UI 线程)。</summary>
    public SettingsView(IPluginContext context, AiSettingsStore store, AiSettings settings, Loc loc, Action onProvidersChanged,
        ProviderHealth? health = null, HttpClient? catalogueHttp = null)
    {
        _context = context;
        _store = store;
        _settings = settings;
        _loc = loc;
        _onProvidersChanged = onProvidersChanged;
        _health = health;
        _models = new ModelsDevCatalog(context);
        _catalogueHttp = catalogueHttp;
        InitializeComponent();
        ApplyLoc();

        ProviderProtocolCombo.ItemsSource = ProtocolLabels;

        ProvidersList.SelectionChanged += (_, _) =>
        {
            if (_refreshingNav)
            {
                return;
            }
            _pendingDeleteProviderId = null;
            ApplyTints();
            _ = LoadEditorAsync();
        };
        // 树形控件的键盘约定:→ 展开、← 折起(模型行上的 ← 是"回到我这一家")。
        // 光有那枚小箭头的话,一路用键盘走下来的人在这儿就没辙了。
        // 走<b>隧道</b>而不是 KeyDown 事件:ListBox 自己的类处理器把左右键当成条目导航吃掉了,
        // 冒泡阶段再挂已经晚了(那时 Handled 已经是 true)。
        ProvidersList.AddHandler(KeyDownEvent, OnNavKeyDown, RoutingStrategies.Tunnel);
        // 从清单里挑一个:不光填模型 id,连<b>上下文窗口与三档单价</b>一起填好 ——
        // 那几项才是这一页最难填、填错了又不报错的东西(窗口错则占比错,单价错则花费估算错)。
        // _loadingEditor 期间不理会:那是重填表单时的程序性赋值,不是用户在挑。
        ModelPickCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingEditor || ModelPickCombo.SelectedItem is not string id)
            {
                return;
            }
            ModelBox.Text = id;
            if (SelectedProvider is not { } owner || _models.ForProvider(ProviderCatalog.Find(owner.CatalogId)?.ModelsDevId)
                    .FirstOrDefault(s => s.Id == id) is not { } spec)
            {
                return;
            }
            if (spec.ContextTokens > 0)
            {
                _windowSpecDraft = spec;
                ++_windowEditRevision;
                SetWindowText(spec.ContextTokens.ToString());
                UpdateContextWindowHint();
            }
            if (spec.OutputTokens > 0)
            {
                MaxTokensBox.Text = spec.OutputTokens.ToString();
            }
            PriceInBox.Text = Money(spec.InputPrice);
            PriceOutBox.Text = Money(spec.OutputPrice);
            PriceCachedBox.Text = Money(spec.CachedInputPrice);
            StatusText.Text = _loc["ModelSpecFilled"];
        };
        ProviderProtocolCombo.SelectionChanged += (_, _) => UpdateProtocolOnlyFields();
        ProtocolCombo.SelectionChanged += (_, _) => UpdateProtocolOnlyFields();
        OwnKeyCheck.IsCheckedChanged += (_, _) => OwnKeyPanel.IsVisible = OwnKeyCheck.IsChecked == true;
        AddButton.Click += (_, _) => ProviderCatalogRequested?.Invoke(null);
        // 同目录多账号按左栏选中的实例定位,不是随目录 id 找最近一份。
        ManageSignInButton.Click += (_, _) => ProviderCatalogRequested?.Invoke(SelectedProvider?.Id);
        PullModelsButton.Click += (_, _) => _ = PullModelsAsync();
        AddModelButton.Click += OnAddModelClick;
        SaveButton.Click += OnSaveClick;
        DeleteButton.Click += OnDeleteClick;
        TestButton.Click += OnTestClick;
        RevealKeyToggle.IsCheckedChanged += (_, _) =>
            ApiKeyBox.PasswordChar = RevealKeyToggle.IsChecked == true ? '\0' : '●';
        ProviderAddApiKeyButton.Click += (_, _) => _ = AddProviderKeyAsync();
        ProviderProbeAllKeysButton.Click += (_, _) => OpenAllKeysModelPicker();
        ProviderAllKeysModelPicker.SelectionChanged += OnAllKeysModelSelected;
        BalanceApiKeysCheck.IsCheckedChanged += (_, _) => _ = SaveBalanceAsync();
        // TextChanged 延后派发，不能判断赋值当刻是否来自装载/合并。
        MaxInputTokensBox.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            if (!_settingWindowText && _windowDraftRun == _editorRun && SelectedModel is not null)
            {
                _windowEdited = true;
                ++_windowEditRevision;
                _windowSpecDraft = null;
            }
            UpdateContextWindowHint();
        };
        ModelBox.TextChanged += (_, _) => UpdateContextWindowHint();
        SizeChanged += (_, _) => SettingsLayout.ColumnDefinitions[0].Width = new GridLength(Bounds.Width < 650 ? 120 : 230);

        // 起手选中当前活跃的模型;没有就选第一行
        ReloadList(selectId: _settings.ActiveModelId ?? _settings.Providers.FirstOrDefault()?.Id);
    }

    /// <summary>语言切换时由面板调用。</summary>
    public void ApplyLoc()
    {
        ProvidersHeader.Text = _loc["Providers"];
        SectionProviderTitle.Text = _loc["SecProvider"];
        SectionEndpointTitle.Text = _loc["SecEndpoint"];
        SectionCapacityTitle.Text = _loc["SecCapacity"];
        SectionSamplingTitle.Text = _loc["SecSampling"];
        AddText.Text = _loc["AddProvider"];
        AddModelText.Text = _loc["AddModel"];
        ProviderNameLabel.Text = _loc["Name"];
        ProviderBaseUrlLabel.Text = _loc["BaseUrl"];
        ProviderProtocolLabel.Text = _loc["DefaultProtocol"];
        ProviderProtocolHintText.Text = _loc["DefaultProtocolHint"];
        ProviderApiKeyLabel.Text = _loc["ApiKey"];
        ProviderApiKeyHintText.Text = _loc["SetupKeySaveHint"];
        ProviderAddApiKeyButton.Content = _loc["SetupAddApiKey"];
        ProviderNewApiKeyBox.PlaceholderText = _loc["SetupAddApiKey"];
        Avalonia.Automation.AutomationProperties.SetName(ProviderNewApiKeyBox, _loc["SetupAddApiKey"]);
        Avalonia.Automation.AutomationProperties.SetName(ProviderAddApiKeyButton, _loc["SetupAddApiKey"]);
        BalanceApiKeysCheck.Content = _loc["SetupBalanceApiKeys"];
        BalanceApiKeysHintText.Text = _loc["SetupBalanceApiKeysHint"];
        foreach (ProviderKeyRowState row in _keyRows) ApplyKeyRowLoc(row);
        RefreshAllKeysProbeControls();
        ProviderKeyBadgeText.Text = _loc["KeyEncrypted"];
        ModelKeyBadgeText.Text = _loc["KeyEncrypted"];
        ProviderAuthLabel.Text = _loc["SubscriptionAuth"];
        ProviderAuthHintText.Text = _loc["SubscriptionHint"];
        ManageSignInText.Text = _loc["ManageSignIn"];
        PullModelsText.Text = _loc["ModelsPull"];
        PullModelsHintText.Text = _loc["ModelsPullHint"];
        NameLabel.Text = _loc["DisplayName"];
        ProtocolLabel.Text = _loc["Protocol"];
        BaseUrlLabel.Text = _loc["BaseUrlOverride"];
        BaseUrlHintText.Text = _loc["BaseUrlOverrideHint"];
        ModelLabel.Text = _loc["ModelId"];
        ModelPickLabel.Text = _loc["ModelPick"];
        OwnKeyCheck.Content = _loc["OwnApiKey"];
        OwnKeyHintText.Text = _loc["OwnApiKeyHint"];
        MaxTokensLabel.Text = _loc["MaxTokens"];
        MaxInputTokensLabel.Text = _loc["MaxInputTokens"];
        UpdateContextWindowHint();
        ReasoningLabel.Text = _loc["Reasoning"];
        ReasoningHintText.Text = _loc["ReasoningHint"];
        PromptCacheCheck.Content = _loc["PromptCache"];
        PromptCacheHintText.Text = _loc["PromptCacheHint"];
        TemperatureLabel.Text = _loc["Temperature"];
        TopPLabel.Text = _loc["TopP"];
        StopLabel.Text = _loc["StopSequences"];
        SamplingHintText.Text = _loc["SamplingHint"];
        PriceInLabel.Text = _loc["PriceIn"];
        PriceOutLabel.Text = _loc["PriceOut"];
        PriceCachedLabel.Text = _loc["PriceCached"];
        PriceHintText.Text = _loc["PriceHint"];
        ProviderPromptLabel.Text = _loc["ProviderPrompt"];
        // 语言切换时下拉项也要跟着换,选中项按索引留住
        int reasoning = ReasoningCombo.SelectedIndex;
        ReasoningCombo.ItemsSource = ReasoningKeys.Select(key => _loc[key]).ToList();
        ReasoningCombo.SelectedIndex = reasoning;
        int protocol = ProtocolCombo.SelectedIndex;
        ProtocolCombo.ItemsSource = ProtocolChoices(SelectedProvider);
        ProtocolCombo.SelectedIndex = protocol;
        ApiKeyHintText.Text = _loc["ApiKeyHint"];
        SaveText.Text = _loc["Save"];
        TestText.Text = _loc["Test"];
        DeleteText.Text = _loc["Delete"];
    }

    // ---- 选中项 ----

    private ProviderNavItem? SelectedItem
        => ProvidersList.SelectedIndex >= 0 && ProvidersList.SelectedIndex < _nav.Count
            ? _nav[ProvidersList.SelectedIndex]
            : null;

    /// <summary>选中行所属的供应商(选中的是供应商行就是它自己)。</summary>
    private AiProvider? SelectedProvider => SelectedItem?.Provider;

    /// <summary>选中的模型行;选中的是供应商行则为 null。</summary>
    private AiModelConfig? SelectedModel => SelectedItem?.Model;

    /// <summary>模型协议下拉:第 0 项"继承供应商(xxx)",其后按 <see cref="ChatProtocol" /> 枚举顺序。</summary>
    private List<string> ProtocolChoices(AiProvider? provider)
    {
        string inherited = provider is null ? "" : ProtocolLabels[(int)provider.DefaultProtocol];
        return [_loc.F("InheritProtocol", inherited), .. ProtocolLabels];
    }

    /// <summary>状态点/徽章的记忆键:模型行按模型 id,供应商行按供应商 id。</summary>
    private static string NavKey(ProviderNavItem item) => item.Model?.Id ?? item.Provider.Id;

    /// <summary>把某一行的状态点刷成它当前该有的颜色(没测过 = 灰)。</summary>
    private void ApplyDot(ProviderNavItem item)
    {
        bool? result = _testResults.TryGetValue(NavKey(item), out (bool Ok, DateTime At) r) ? r.Ok : null;
        (string brushKey, string tipKey) = result switch
        {
            true => ("VelaStatusConnected", "DotPassed"),
            false => ("VelaError", "DotFailed"),
            _ => ("VelaTextMuted", "DotUntested")
        };
        item.Dot = this.TryFindResource(brushKey, ActualThemeVariant, out object? brush) ? brush as IBrush : null;
        item.DotTip = _loc[tipKey];
    }

    /// <summary>
    /// 挂进可视树之后把左栏的图标与状态点重解析一次。
    /// <b>构造期解析不到宿主令牌</b>:那时 <c>TryFindResource</c> 只走得到本控件自己的
    /// <c>Resources</c>,往上没有父级、也够不着 <c>Application.Resources</c>,
    /// 于是 Vela* 全落空 —— 图标描边与状态点都是 null 画刷,整列层级标记和连通性点直接不显示。
    /// </summary>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        _providerTint = null;
        _modelTint = null;
        _selectedTint = null;
        RefreshNavVisuals();
        UpdateTestBadge();
    }

    /// <summary>左栏每一行的层级图标、连通性点与图标描边色,一起重算。</summary>
    private void RefreshNavVisuals()
    {
        Geometry? cloud = Token<Geometry>("AiIcon.cloud");
        Geometry? box = Token<Geometry>("AiIcon.box");
        _providerTint ??= Token<IBrush>("VelaTextSecondary");
        _modelTint ??= Token<IBrush>("VelaTextMuted");
        _selectedTint ??= Token<IBrush>("VelaAccent");
        foreach (ProviderNavItem item in _nav)
        {
            item.Icon = item.IsProvider ? cloud : box;
            ApplyDot(item);
        }
        ApplyTints();
    }

    /// <summary>本视图里解析一个宿主令牌(缺席时回落 null,属性保持默认外观)。</summary>
    private T? Token<T>(string key) where T : class
        => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value as T : null;

    /// <summary>
    /// 左栏图标的描边色:选中行跟着文字一起转强调色,其余比同一行的文字再暗一档
    /// (供应商行文字 Primary / 图标 Secondary,模型行文字 Secondary / 图标 Muted)。
    /// 图标是这一行的层级标记,压过文字就成了噪点。
    /// </summary>
    private void ApplyTints()
    {
        ProviderNavItem? selected = SelectedItem;
        foreach (ProviderNavItem item in _nav)
        {
            item.Tint = ReferenceEquals(item, selected)
                ? _selectedTint
                : item.IsProvider ? _providerTint : _modelTint;
        }
    }

    /// <summary>表单顶上那枚测试结果徽章。没测过就整枚隐掉,不假装知道。</summary>
    private void UpdateTestBadge()
    {
        if (SelectedItem is not { } item || !_testResults.TryGetValue(NavKey(item), out (bool Ok, DateTime At) r))
        {
            TestBadge.IsVisible = false;
            return;
        }
        TestBadge.IsVisible = true;
        // 带上时刻:"通过"是有保质期的一句话,不写清是几点测的,隔半小时看还以为是刚测过
        TestBadgeText.Text = $"{_loc[r.Ok ? "DotPassed" : "DotFailed"]} · {r.At:HH:mm}";
        IBrush? tone = Token<IBrush>(r.Ok ? "VelaStatusConnected" : "VelaError");
        TestBadgeText.Foreground = tone;
        TestBadgeIcon.Data = Token<Geometry>(r.Ok ? "AiIcon.circle-check" : "AiIcon.circle-x");
        TestBadgeIcon.Stroke = tone;
        // 同色淡底、不描边(设计图 E):描一圈边会让这枚小标签看起来像个可点的按钮
        TestBadge.Background = tone is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, 0.14)
            : null;
    }

    /// <summary>
    /// 模型多到什么程度就默认折起来。
    /// </summary>
    /// <remarks>
    /// 一屏左栏大约摆得下二十来行。定 12 是让"一家 + 它的模型"仍能和别家一起看见 ——
    /// 超过这个数,列表就从"一览"变成了"要滚动的东西",那正是该折起来的时候。
    /// </remarks>
    private const int AutoCollapseFrom = 12;

    /// <summary>
    /// 这一家自己的展开状态:用户表过态就听他的,没表过态才按数量自动判断
    /// (见 <see cref="AiProvider.ModelsExpanded" />)。
    /// </summary>
    private static bool IsExpanded(AiProvider provider)
        => provider.ModelsExpanded ?? provider.Models.Count <= AutoCollapseFrom;

    /// <summary>
    /// 列表里这一家实际展不展开。
    /// </summary>
    /// <remarks>
    /// 选中项落在这一家的某个模型上时<b>强制展开</b> —— 否则选中行不在列表里,
    /// 右边的表单会停在一个左栏看不见的模型上,而用户没有任何办法看出自己在编辑谁。
    /// </remarks>
    private static bool IsVisiblyExpanded(AiProvider provider, string? selectId)
        => IsExpanded(provider) || provider.Models.Exists(m => m.Id == selectId);

    private void ReloadList(string? selectId, bool selectFallback = true)
    {
        ProviderNavItem? before = SelectedItem;
        _nav = [];
        int selectIndex = -1;
        foreach (AiProvider provider in _settings.Providers)
        {
            if (provider.Id == selectId)
            {
                selectIndex = _nav.Count;
            }
            bool expanded = IsVisiblyExpanded(provider, selectId);
            _nav.Add(new ProviderNavItem(provider, null,
                string.IsNullOrWhiteSpace(provider.Name) ? _loc["Unnamed"] : provider.Name,
                new Thickness(0), FontWeight.Medium,
                provider.Models.Count, expanded, _loc[expanded ? "NavCollapse" : "NavExpand"]));
            if (!expanded)
            {
                continue; // 折起来的这一家,模型行整批不进列表
            }
            foreach (AiModelConfig model in provider.Models)
            {
                if (model.Id == selectId)
                {
                    selectIndex = _nav.Count;
                }
                _nav.Add(new ProviderNavItem(provider, model,
                    string.IsNullOrWhiteSpace(model.DisplayName) ? _loc["Unnamed"] : model.DisplayName,
                    new Thickness(14, 0, 0, 0), FontWeight.Normal));
            }
        }
        ProvidersList.ItemsSource = _nav;
        ProvidersList.SelectedIndex = selectIndex >= 0 ? selectIndex : selectFallback ? Math.Min(0, _nav.Count - 1) : -1;
        if (!ReferenceEquals(before?.Provider, SelectedProvider) || !ReferenceEquals(before?.Model, SelectedModel))
            _pendingDeleteProviderId = null;
        // 图标/状态点排在选中项定下来之后:选中那一行的图标要转强调色,得先知道是哪一行。
        // (SelectedIndex 没变时 SelectionChanged 不会再触发,所以这里必须自己补一次。)
        RefreshNavVisuals();
        if (ProvidersList.SelectedIndex < 0 && !_refreshingNav)
        {
            _ = LoadEditorAsync();
        }
    }

    // ---- 折叠 ----

    /// <summary>
    /// 点供应商行 —— 名字、图标、那枚箭头,<b>整行都算</b>:折/展它的模型列表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 文件树里点文件夹名就该折叠,只让一枚 10px 的三角管这件事是把功能藏起来。
    /// 模型行与没挂模型的供应商行不在此列:它们照常只是被选中。
    /// </para>
    /// <para>
    /// <b>吃掉这次按下</b>(<c>e.Handled</c>)并由 <see cref="Toggle" /> 自己把选中项落到这一行上,
    /// 而不是放给 <see cref="ListBox" /> 去选:折叠会整个重建列表,被点中的那个容器当场失效,
    /// 让它在一个已经拆掉的容器上算选中项,选出来的是哪一行没人说得准。
    /// </para>
    /// </remarks>
    private void OnNavRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Handled && sender is Control { DataContext: ProviderNavItem { CanCollapse: true } item })
        {
            e.Handled = true;
            Toggle(item.Provider);
        }
    }

    /// <summary>→ 展开、← 折起;模型行上的 ← 收回到它那一家的行上。</summary>
    private void OnNavKeyDown(object? sender, KeyEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }
        // 模型行上的 ← 只是"回到我这一家",不顺手把它折起来 ——
        // 一次按键做两件事,想再展开时会发现自己已经不在原来那一行了
        if (item.Model is not null && e.Key == Key.Left)
        {
            e.Handled = true;
            ReloadList(item.Provider.Id);
            return;
        }
        if (!item.CanCollapse || e.Key is not (Key.Left or Key.Right))
        {
            return;
        }
        bool expand = e.Key == Key.Right;
        if (expand == IsVisiblyExpanded(item.Provider, NavKey(item)))
        {
            return; // 已经是这个状态了,别白重建一次列表(那会闪一下)
        }
        e.Handled = true;
        Toggle(item.Provider);
    }

    /// <summary>
    /// 翻转这一家的展开状态并落盘,选中项落到<b>这一家的供应商行</b>上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 选中项一律收到供应商行上,而不是留在原处:折起来时,原先选中的模型会连同整批模型一起
    /// 从列表里消失,而右边的表单还停在它上面 —— 用户没有任何办法看出自己在编辑谁。
    /// 展开时收上来则是点这一行的应有之义(用户点的就是它)。
    /// </para>
    /// <para>
    /// 算当前状态时要把"选中项强制展开"一起算进去(<see cref="IsVisiblyExpanded" />),
    /// 否则一家被自动折起、却因为选中了它的模型而展着时,第一下点击会把它从"折"翻成"展" ——
    /// 而用户眼睛看到的是展开的,点下去却没反应。
    /// </para>
    /// </remarks>
    private void Toggle(AiProvider provider)
    {
        provider.ModelsExpanded = !IsVisiblyExpanded(provider, SelectedItem is { } cur ? NavKey(cur) : null);
        _ = PersistAsync(notify: false);
        ReloadList(provider.Id);
    }

    private Task LoadEditorAsync() => _editorLoad = LoadEditorCoreAsync(++_editorRun);
    private void ResetKeyEditor()
    {
        _lastKeyProbeModelId = null;
        CancelAllKeysProbe();
        ProviderAllKeysModelPicker.IsVisible = false;
        _keyEditorCts.Cancel();
        _keyEditorCts.Dispose();
        _keyEditorCts = new CancellationTokenSource();
        foreach (ProviderKeyRowState row in _keyRows) CancelKeyProbe(row);
        _keyRows.Clear();
        ProviderApiKeyRows.Children.Clear();
        _keyDraft = null;
        _pendingRemoveKeyId = null;
        ProviderNewApiKeyBox.Text = "";
        ++_keyRowsRun;
    }

    private async Task LoadEditorCoreAsync(int run)
    {
        ResetKeyEditor();
        _loadingEditor = true;
        try
        {
            AiProvider? provider = SelectedProvider;
            AiModelConfig? model = SelectedModel;
            bool hasSelection = provider is not null;
            ProviderEditor.IsVisible = hasSelection && model is null;
            ModelEditor.IsVisible = model is not null;
            SaveButton.IsEnabled = hasSelection;
            TestButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
            AddModelButton.IsEnabled = hasSelection;
            StatusText.Text = "";
            RefreshEditorContext(provider, model);
            UpdateTestBadge();

            // 供应商表单
            ProviderNameBox.Text = provider?.Name ?? "";
            ProviderBaseUrlBox.Text = provider?.BaseUrl ?? "";
            ProviderProtocolCombo.SelectedIndex = provider is null ? -1 : (int)provider.DefaultProtocol;
            _settingBalance = true;
            try { BalanceApiKeysCheck.IsChecked = provider?.BalanceApiKeys ?? false; }
            finally { _settingBalance = false; }
            _loadedProviderName = ProviderNameBox.Text;
            _loadedProviderUrl = ProviderBaseUrlBox.Text;
            _loadedProviderProtocol = ProviderProtocolCombo.SelectedIndex;
            _loadedKey = "";

            // 模型表单
            ShowSavedModelForm(provider, model);

            bool subscription = RefreshAuthPanels(provider);

            if (provider is not null && model is null)
            {
                if (subscription)
                {
                    await RefreshSubscriptionStatusAsync(provider);
                }
                else
                {
                    await RefreshProviderKeyRowsAsync(provider, run);
                }
                if (run != _editorRun || !ReferenceEquals(SelectedProvider, provider) || SelectedModel is not null
                    || !_settings.Providers.Contains(provider)) return;
                if (provider.Models.Count == 0)
                {
                    StatusText.Text = _loc["NoModels"];
                }
            }
            else if (model is { HasOwnApiKey: true })
            {
                string? key = await _store.GetApiKeyAsync(model.Id);
                if (run == _editorRun && ReferenceEquals(SelectedProvider, provider) && ReferenceEquals(SelectedModel, model)
                    && provider is not null && _settings.Providers.Contains(provider) && provider.Models.Contains(model))
                {
                    if (string.IsNullOrEmpty(ApiKeyBox.Text)) ApiKeyBox.Text = key ?? "";
                    _loadedKey = key ?? "";
                    _loadedModelForm[ApiKeyBox] = _loadedKey;
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log.Error("Load provider editor failed.", ex);
        }
        finally
        {
            if (run == _editorRun)
            {
                _loadingEditor = false;
            }
        }
    }

    private void ShowSavedModelForm(AiProvider? provider, AiModelConfig? model)
    {
        _windowSpecDraft = null;
        _windowEdited = false;
        _windowDraftRun = _editorRun;
        NameBox.Text = model?.Name ?? "";
        ModelBox.Text = model?.Model ?? "";
        List<string> available = provider?.AvailableModels ?? [];
        ModelPickPanel.IsVisible = model is not null && available.Count > 0;
        ModelPickCombo.ItemsSource = available;
        ModelPickCombo.SelectedItem = available.Find(
            id => string.Equals(id, model?.Model, StringComparison.OrdinalIgnoreCase));
        ProtocolCombo.ItemsSource = ProtocolChoices(provider);
        ProtocolCombo.SelectedIndex = model is null ? -1 : model.Protocol is { } p ? (int)p + 1 : 0;
        OwnKeyCheck.IsChecked = model?.HasOwnApiKey ?? false;
        OwnKeyPanel.IsVisible = model?.HasOwnApiKey ?? false;
        BaseUrlBox.Text = model?.BaseUrlOverride ?? "";
        MaxTokensBox.Text = model?.MaxTokens.ToString() ?? "";
        SetWindowText(model?.MaxInputTokens.ToString() ?? "");
        ReasoningCombo.SelectedIndex = model is null ? -1 : (int)model.Reasoning;
        PromptCacheCheck.IsChecked = model?.PromptCaching ?? true;
        TemperatureBox.Text = model?.Temperature?.ToString() ?? "";
        TopPBox.Text = model?.TopP?.ToString() ?? "";
        StopBox.Text = model?.StopSequences ?? "";
        PriceInBox.Text = Money(model?.InputPricePerMillion);
        PriceOutBox.Text = Money(model?.OutputPricePerMillion);
        PriceCachedBox.Text = Money(model?.CachedInputPricePerMillion);
        ProviderPromptBox.Text = model?.SystemPrompt ?? "";
        UpdateProtocolOnlyFields();
        ApiKeyBox.Text = "";
        _loadedModelForm = CaptureModelForm();
        UpdateContextWindowHint();
    }

    private void SetWindowText(string? text)
    {
        _settingWindowText = true;
        try { MaxInputTokensBox.Text = text; }
        finally { _settingWindowText = false; }
    }

    private void UpdateContextWindowHint()
    {
        bool parsed = int.TryParse(MaxInputTokensBox.Text, out int window);
        bool selectedSpec = parsed && _windowSpecDraft is { } spec && spec.Id == ModelBox.Text && spec.ContextTokens == window;
        string warning = !_windowEdited && !selectedSpec && parsed && SelectedModel is { } model && window == model.MaxInputTokens
            ? _loc.ContextWindowWarning(window, model.DefaultContextWindow) : "";
        string hint = _loc["MaxInputTokensHint"];
        if (warning.Length > 0) hint += "\n" + warning;
        MaxInputTokensHintText.Text = hint;
        ToolTip.SetTip(MaxInputTokensBox, hint);
    }

    private void RefreshEditorContext(AiProvider? provider, AiModelConfig? model)
    {
        // 面包屑属于已保存的选择上下文,不是右侧尚未保存的草稿。
        string providerName = provider is null
            ? ""
            : string.IsNullOrWhiteSpace(provider.Name) ? _loc["Unnamed"] : provider.Name;
        string modelName = model is null
            ? ""
            : string.IsNullOrWhiteSpace(model.DisplayName) ? _loc["Unnamed"] : model.DisplayName;
        BreadcrumbText.Text = model is null ? providerName : $"{providerName}  ›  {modelName}";
        // 拉取是整家的事;端点或目录任意一条路可用就显示入口。
        PullModelsPanel.IsVisible = provider is not null && model is null
                                    && (!string.IsNullOrWhiteSpace(provider.BaseUrl)
                                        || ProviderCatalog.Find(provider.CatalogId)?.ModelsDevId.Length > 0);
    }

    private bool RefreshAuthPanels(AiProvider? provider)
    {
        bool subscription = provider is { Auth: AuthMethod.Subscription };
        ProviderKeyPanel.IsVisible = !subscription;
        ProviderAuthPanel.IsVisible = subscription;
        ProviderAuthStatusText.Text = "";
        ++_authStatusRun;
        if (subscription)
        {
            foreach (ProviderKeyRowState row in _keyRows)
            {
                CancelKeyProbe(row);
                row.Revealed = false;
                row.Picker.IsVisible = false;
                ApplyKeyRowLoc(row);
            }
        }
        return subscription;
    }

    private async Task RefreshSubscriptionStatusAsync(AiProvider provider)
    {
        int run = ++_authStatusRun;
        try
        {
            OAuthTokens? tokens = await _store.GetTokensAsync(provider.Id);
            if (run == _authStatusRun && ReferenceEquals(SelectedProvider, provider) && SelectedModel is null
                && provider.Auth == AuthMethod.Subscription && _settings.Providers.Contains(provider))
            {
                ProviderAuthStatusText.Text = tokens is null
                    ? _loc["SubscriptionNotSignedIn"]
                    : _loc.F("SubscriptionSignedIn", tokens.ObtainedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }
        }
        catch (Exception ex)
        {
            _context.Log.Error("Refresh subscription status failed.", ex);
        }
    }

    /// <summary>空串表示"不发这个参数",所以解析失败与留空都返回 null。</summary>
    private static float? ParseOptional(string? text)
        => float.TryParse(text?.Trim(), out float value) ? value : null;

    /// <summary>单价:解析不出来就当没填(0 = 不估算成本)。</summary>
    private static double ParsePrice(string? text)
        => double.TryParse(text?.Trim(), out double value) && value > 0 ? value : 0;

    /// <summary>0 显示成空串 —— 免得每个新模型的三个单价框里都摆着个 0 招人误会。</summary>
    private static string Money(double? value) => value is > 0 ? value.Value.ToString("0.####") : "";

    /// <summary>表单里此刻解出的协议(模型覆盖 → 供应商默认)。</summary>
    private ChatProtocol? FormProtocol()
    {
        if (ProtocolCombo.SelectedIndex > 0)
        {
            return (ChatProtocol)(ProtocolCombo.SelectedIndex - 1);
        }
        return SelectedProvider is { } provider
            ? ProviderProtocolCombo.SelectedIndex >= 0 && SelectedModel is null
                ? (ChatProtocol)ProviderProtocolCombo.SelectedIndex
                : provider.DefaultProtocol
            : null;
    }

    /// <summary>只对某一种协议成立的选项跟着解出的协议显隐(现在只有 Anthropic 的提示词缓存)。</summary>
    private void UpdateProtocolOnlyFields()
        => PromptCachePanel.IsVisible = FormProtocol() == ChatProtocol.AnthropicMessages;

    /// <summary>
    /// 「拉取模型」:问端点它实际供应哪些模型,配上规格,落成真正可选的模型。
    /// </summary>
    /// <remarks>
    /// 与「连接供应商」那一页上的同名按钮走同一条路(<see cref="ModelPull.RunAsync" />)。
    /// 在这儿也放一个,是因为用户十有八九是在这一页发现"怎么只有一个模型"的 ——
    /// 让他为此再跑回目录页未免绕。
    /// 有连接或行内 Key 草稿时要求先保存；拉取仅能使用已经确认的地址与凭据。
    /// </remarks>
    private async Task PullModelsAsync()
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }
        // 订阅登录的凭据不在表单里,靠 Id 去机密存储取(与「测试」那边同一个判断)
        if (ProviderConnectionDirty(provider) || _keyDraft is not null)
        {
            StatusText.Text = _loc["SetupSaveProviderFirst"];
            return;
        }
        int run = _editorRun;
        string beforeJson = JsonSerializer.Serialize(provider);
        AiProvider before = JsonSerializer.Deserialize<AiProvider>(beforeJson)!;
        AiProvider catalogue = JsonSerializer.Deserialize<AiProvider>(beforeJson)!;
        bool committingCatalogue = false;
        try
        {
            PullModelsButton.IsEnabled = false;
            StatusText.Text = _loc["ModelsPulling"];
            ModelPullResult result = await ModelPull.RunAsync(
                provider, ProviderCatalog.Find(provider.CatalogId)?.ModelsDevId, _models, _store,
                force: true, materialiseInto: catalogue, catalogueHttp: _catalogueHttp);
            if (result.Source == ModelSource.None)
            {
                if (run == _editorRun && ReferenceEquals(SelectedProvider, provider)) StatusText.Text = _loc["ModelsNone"];
                return;
            }
            if (!_settings.Providers.Contains(provider) || provider.BaseUrl != before.BaseUrl
                || provider.DefaultProtocol != before.DefaultProtocol || provider.Auth != before.Auth
                || provider.CatalogId != before.CatalogId) return;
            string expected = JsonSerializer.Serialize(provider);
            AiProvider draft = JsonSerializer.Deserialize<AiProvider>(expected)!;
            List<string>? changedModels = _health is null ? null : [];
            ProviderSetupView.MergeCatalogue(draft, before, catalogue, null, changedModels);
            committingCatalogue = true;
            await _store.SaveProviderCatalogueAsync(_settings, provider, draft, expected, _context.Shutdown);
            if (changedModels is not null)
                foreach (string id in changedModels) _health!.Invalidate(id);
            RefreshCatalogModels();
            ModelsChanged?.Invoke();
            if (run == _editorRun && ReferenceEquals(SelectedProvider, provider))
                StatusText.Text = _loc.F("ModelsPulled", provider.Models.Count);
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            if (!committingCatalogue) await RecoverSettingsConflictAsync(provider, null, run);
            else if (run == _editorRun && ReferenceEquals(SelectedProvider, provider)) StatusText.Text = _loc["SetupConfigChanged"];
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Pulling models for '{provider.Name}' failed: {ex.Message}");
            if (run == _editorRun && ReferenceEquals(SelectedProvider, provider))
                StatusText.Text = $"{_loc["Error"]}: {Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"])}";
        }
        finally
        {
            PullModelsButton.IsEnabled = true;
        }
    }

    // ---- 新增 ----

    /// <summary>
    /// 「连接供应商」那边加完 / 登完之后回来叫这个:重建左栏并选中它。
    /// </summary>
    /// <remarks>那边已经落过盘了,这里只刷界面 —— 再存一次会把它刚写的东西按内存里的旧值盖回去。</remarks>
    /// <param name="providerId">要选中的供应商 id。</param>
    public void ReloadFromCatalog(string providerId)
    {
        ClearStaleNoModels();
        ProviderNavItem? before = SelectedItem;
        _refreshingNav = true;
        try { ReloadList(providerId); }
        finally { _refreshingNav = false; }
        RefreshAfterCatalog(before);
    }

    private void RefreshAfterCatalog(ProviderNavItem? before)
    {
        if (before is null || !ReferenceEquals(before.Provider, SelectedProvider)
            || !ReferenceEquals(before.Model, SelectedModel))
        {
            _ = LoadEditorAsync();
            return;
        }
        RefreshEditorContext(SelectedProvider, SelectedModel);
        if (SelectedModel is null)
            _editorLoad = MergeSavedProviderAsync(before.Provider, _editorRun, _editorLoad);
        else
            _editorLoad = MergeSavedModelAsync(before.Provider, before.Model!, _editorRun, _editorLoad);
    }

    private async Task MergeSavedProviderAsync(AiProvider provider, int run, Task pending)
    {
        await pending;
        if (run != _editorRun || !ReferenceEquals(SelectedProvider, provider) || SelectedModel is not null
            || !_settings.Providers.Contains(provider)) return;
        if (ProviderNameBox.Text == _loadedProviderName) ProviderNameBox.Text = provider.Name;
        if (ProviderBaseUrlBox.Text == _loadedProviderUrl) ProviderBaseUrlBox.Text = provider.BaseUrl;
        if (ProviderProtocolCombo.SelectedIndex == _loadedProviderProtocol)
            ProviderProtocolCombo.SelectedIndex = (int)provider.DefaultProtocol;
        _loadedProviderName = provider.Name;
        _loadedProviderUrl = provider.BaseUrl;
        _loadedProviderProtocol = (int)provider.DefaultProtocol;
        UpdateProtocolOnlyFields();
        if (RefreshAuthPanels(provider))
        {
            await RefreshSubscriptionStatusAsync(provider);
            return;
        }
        _settingBalance = true;
        try { BalanceApiKeysCheck.IsChecked = provider.BalanceApiKeys; }
        finally { _settingBalance = false; }
        await RefreshProviderKeyRowsAsync(provider, run);
    }
    private sealed class ProviderKeyRowState(AiProvider provider, string id, string key)
    {
        public AiProvider Provider { get; } = provider;
        public string Id { get; } = id;
        public string Key { get; set; } = key;
        public int Ordinal { get; set; }
        public bool Revealed { get; set; }
        public Border Host { get; set; } = null!;
        public Button Reveal { get; set; } = null!;
        public TextBlock Label { get; set; } = null!;
        public TextBlock Status { get; set; } = null!;
        public Avalonia.Controls.Shapes.Path Dot { get; set; } = null!;
        public Button Probe { get; set; } = null!;
        public Button Edit { get; set; } = null!;
        public Button Remove { get; set; } = null!;
        public StackPanel EditPanel { get; set; } = null!;
        public TextBox EditBox { get; set; } = null!;
        public Button Cancel { get; set; } = null!;
        public ComboBox Picker { get; set; } = null!;
        public CancellationTokenSource? ProbeCts { get; set; }
        public int ProbeRun { get; set; }
        public string? CheckingModelId { get; set; }
        public KeyRequestSnapshot? CheckingRequest { get; set; }
        public int CheckingVersion { get; set; }
        public string OriginalKey { get; set; } = "";
        public string OriginalUrl { get; set; } = "";
        public ChatProtocol OriginalProtocol { get; set; }
        public KeyProbeResult? Result { get; set; }
    }

    private sealed record KeyModelChoice(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record KeyProbeResult(string ModelId, KeyRequestSnapshot Request, bool Ok,
        DateTime At, Exception? Error, int Version);

    private readonly record struct KeyRequestSnapshot(string ProviderId, string ProviderUrl, string? CatalogId,
        string ModelId, ChatProtocol Protocol, string Url, string Model, int MaxTokens, int MaxInputTokens, ReasoningLevel Reasoning,
        float? Temperature, float? TopP, string Stop, bool PromptCaching, string? SystemPrompt,
        bool StoreResponses, bool AllowSystemMessages, string Unsupported, string KeyOwner, AuthMethod Auth)
    {
        public static KeyRequestSnapshot Capture(ResolvedModel model) => new(model.Provider.Id, model.Provider.BaseUrl,
            model.Provider.CatalogId, model.Id,
            model.Protocol, model.BaseUrl, model.Model, model.MaxTokens, model.MaxInputTokens, model.Reasoning,
            model.Temperature, model.TopP, model.StopSequences, model.PromptCaching, model.SystemPrompt,
            model.Provider.StoreResponses, model.Provider.AllowSystemMessages,
            model.Provider.UnsupportedParameters, model.ApiKeyOwnerId, model.Provider.Auth);
    }

    private static ResolvedModel FreezeProbeModel(ResolvedModel model) => new(new AiProvider
    {
        Id = model.Provider.Id, Name = model.Provider.Name, BaseUrl = model.Provider.BaseUrl,
        DefaultProtocol = model.Protocol, CatalogId = model.Provider.CatalogId,
        Auth = model.Provider.Auth, OAuth = model.Provider.OAuth,
        AdditionalApiKeyIds = [.. model.Provider.AdditionalApiKeyIds], ActiveApiKeyId = model.Provider.ActiveApiKeyId,
        StoreResponses = model.Provider.StoreResponses, AllowSystemMessages = model.Provider.AllowSystemMessages,
        UnsupportedParameters = model.Provider.UnsupportedParameters
    }, new AiModelConfig
    {
        Id = model.Id, Name = model.Name, Model = model.Model, Protocol = model.Protocol,
        BaseUrlOverride = model.BaseUrl,
        HasOwnApiKey = model.Config.HasOwnApiKey, MaxTokens = model.MaxTokens,
        MaxInputTokens = model.MaxInputTokens, Reasoning = model.Reasoning, PromptCaching = model.PromptCaching,
        Temperature = model.Temperature, TopP = model.TopP, StopSequences = model.StopSequences,
        SystemPrompt = model.SystemPrompt
    });

    private string? PendingProviderKey => _keyDraft is { } row && !string.IsNullOrEmpty(row.EditBox.Text)
        ? row.EditBox.Text : null;

    private bool ProviderConnectionDirty(AiProvider provider)
        => (ProviderBaseUrlBox.Text?.Trim() ?? "") != provider.BaseUrl
            || ProviderProtocolCombo.SelectedIndex >= 0
                && ProviderProtocolCombo.SelectedIndex != (int)provider.DefaultProtocol;

    private bool ProviderDraftDirty(AiProvider provider)
        => ProviderConnectionDirty(provider) || PendingProviderKey is not null;

    private bool KeyRowCurrent(ProviderKeyRowState row, int run)
        => run == _editorRun && SelectedModel is null && ReferenceEquals(SelectedProvider, row.Provider)
            && _settings.Providers.Contains(row.Provider) && _keyRows.Contains(row)
            && _store.ProviderApiKeyIds(row.Provider).Contains(row.Id, StringComparer.Ordinal);

    private async Task RefreshProviderKeyRowsAsync(AiProvider provider, int run)
    {
        int refresh = ++_keyRowsRun;
        CancellationToken token = _keyEditorCts.Token;
        var keys = new List<(string Id, string Key)>();
        foreach (string id in _store.ProviderApiKeyIds(provider).ToArray())
        {
            string? key = await _store.GetApiKeyAsync(id, token);
            if (!string.IsNullOrWhiteSpace(key)) keys.Add((id, key));
        }
        if (token.IsCancellationRequested || refresh != _keyRowsRun || run != _editorRun
            || !ReferenceEquals(SelectedProvider, provider) || SelectedModel is not null
            || !_settings.Providers.Contains(provider) || provider.Auth != AuthMethod.ApiKey) return;
        foreach (ProviderKeyRowState stale in _keyRows.Where(row => keys.All(key => key.Id != row.Id)).ToArray())
        {
            CancelKeyProbe(stale);
            if (_pendingRemoveKeyId == stale.Id) _pendingRemoveKeyId = null;
            if (ReferenceEquals(_keyDraft, stale))
            {
                stale.Key = "";
                stale.Revealed = false;
                stale.Result = null;
                ApplyKeyRowLoc(stale);
                continue; // 保留空编辑稿；保存时明确报槽已变，不能静默保存其它字段。
            }
            _keyRows.Remove(stale);
            ProviderApiKeyRows.Children.Remove(stale.Host);
        }
        for (int index = 0; index < keys.Count; index++)
        {
            (string id, string key) = keys[index];
            ProviderKeyRowState? row = _keyRows.Find(existing => existing.Id == id);
            if (row is null)
            {
                row = new ProviderKeyRowState(provider, id, key);
                BuildProviderKeyRow(row);
                _keyRows.Insert(index, row);
                ProviderApiKeyRows.Children.Insert(index, row.Host);
            }
            else if (row.Key != key)
            {
                CancelKeyProbe(row);
                if (_pendingRemoveKeyId == row.Id) _pendingRemoveKeyId = null;
                row.Result = null;
                row.Revealed = false;
                row.Key = key;
            }
            int previousIndex = _keyRows.IndexOf(row);
            if (previousIndex != index)
            {
                Control? focused = row.Host.GetLogicalDescendants().OfType<Control>().FirstOrDefault(control => control.IsFocused);
                _keyRows.RemoveAt(previousIndex);
                _keyRows.Insert(index, row);
                ProviderApiKeyRows.Children.Remove(row.Host);
                ProviderApiKeyRows.Children.Insert(index, row.Host);
                focused?.Focus();
            }
            row.Ordinal = index + 1;
        }
        foreach (ProviderKeyRowState row in _keyRows) ApplyKeyRowLoc(row);
        RefreshAllKeysProbeControls();
    }

    private void BuildProviderKeyRow(ProviderKeyRowState row)
    {
        row.Label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        row.Label[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        row.Reveal = new Button
        {
            Name = "ProviderKeyRevealButton", Tag = row.Id, Content = row.Label,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, MinWidth = 0, Padding = new Thickness(0)
        };
        row.Reveal[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        row.Reveal.Click += (_, _) => { row.Revealed = !row.Revealed; ApplyKeyRowLoc(row); };
        row.Status = new TextBlock
        {
            Name = "ProviderKeyStatus", TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "dim" }
        };
        row.Dot = new Avalonia.Controls.Shapes.Path
        {
            Name = "ProviderKeyStatusIcon", Width = 13, Height = 13, Stretch = Stretch.Uniform,
            StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0)
        };
        row.Probe = ProviderRowIconButton("ProviderKeyProbeButton", "AiIcon.heart-pulse",
            (_, _) => OpenKeyModelPicker(row));
        row.Edit = ProviderRowIconButton("ProviderKeyEditButton", "AiIcon.pencil", (_, _) => BeginKeyEdit(row));
        row.Remove = ProviderRowIconButton("ProviderKeyRemoveButton", "AiIcon.minus", (_, _) => _ = RemoveProviderKeyAsync(row));
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        line.Children.Add(row.Reveal);
        Grid.SetColumn(row.Dot, 1); line.Children.Add(row.Dot);
        Grid.SetColumn(row.Probe, 2); line.Children.Add(row.Probe);
        Grid.SetColumn(row.Edit, 3); line.Children.Add(row.Edit);
        Grid.SetColumn(row.Remove, 4); line.Children.Add(row.Remove);
        row.EditBox = new TextBox { Name = "ProviderKeyEditBox", Tag = row.Id, PasswordChar = '●' };
        row.EditBox[!TextBox.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        row.Cancel = new Button { Name = "ProviderKeyEditCancelButton", Tag = row.Id, MinHeight = 26 };
        row.Cancel[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        row.Cancel.Click += (_, _) => CancelKeyEdit(row);
        var editLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto") };
        editLine.Children.Add(row.EditBox);
        Grid.SetColumn(row.Cancel, 2); editLine.Children.Add(row.Cancel);
        row.EditPanel = new StackPanel { IsVisible = false, Children = { editLine } };
        row.Picker = new ComboBox
        {
            Name = "ProviderKeyModelPicker", Tag = row.Id, IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, SelectedIndex = -1
        };
        row.Host = new Border
        {
            Name = "ProviderKeyRow", Tag = row.Id, Padding = new Thickness(4, 4),
            Child = new StackPanel { Spacing = 4, Children = { line, row.Status, row.EditPanel, row.Picker } }
        };
    }

    private static Button ProviderRowIconButton(string name, string iconKey, EventHandler<RoutedEventArgs> action)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 24, Height = 24, Stretch = Stretch.None, StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round
        };
        icon[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(iconKey);
        icon[!Avalonia.Controls.Shapes.Path.StrokeProperty] = new DynamicResourceExtension("VelaTextSecondary");
        var button = new Button
        {
            Name = name, Classes = { "host" }, Content = new Viewbox { Width = 11, Height = 11, Child = icon }, Margin = new Thickness(2, 0),
            Width = 26, Height = 26, Padding = new Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        button.Click += action;
        return button;
    }

    private static void SetKeyActionName(Control control, string text)
    {
        ToolTip.SetTip(control, text);
        Avalonia.Automation.AutomationProperties.SetName(control, text);
    }

    private static List<KeyModelChoice> KeyModelChoices(ProviderKeyRowState row)
    {
        var models = row.Provider.Models.Where(model => KeyModelEligible(row, model)).ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (AiModelConfig model in models) counts[model.DisplayName] = counts.GetValueOrDefault(model.DisplayName) + 1;
        return models.Select(model => new KeyModelChoice(model.Id,
            counts[model.DisplayName] > 1
                ? $"{model.DisplayName} · {model.Id[..Math.Min(8, model.Id.Length)]}"
                : model.DisplayName)).ToList();
    }
    private static bool KeyModelEligible(ProviderKeyRowState row, AiModelConfig model)
        => !model.HasOwnApiKey && !string.IsNullOrWhiteSpace(model.Model)
            && (row.Id == row.Provider.Id || AiSettingsStore.SameOrigin(
                string.IsNullOrWhiteSpace(model.BaseUrlOverride) ? row.Provider.BaseUrl : model.BaseUrlOverride,
                row.Provider.BaseUrl));

    private static bool HasKeyModels(ProviderKeyRowState row)
    {
        foreach (AiModelConfig model in row.Provider.Models)
            if (KeyModelEligible(row, model)) return true;
        return false;
    }


    private string? CurrentProviderKeyId(AiProvider provider)
    {
        // 行已从机密库读取，与解析器相同地忽略空槽和失效 active；不为每秒刷新再次解密。
        return _keyRows.Find(row => row.Id == provider.ActiveApiKeyId && !string.IsNullOrWhiteSpace(row.Key))?.Id
            ?? _keyRows.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Key))?.Id;
    }

    private void ApplyKeyRowLoc(ProviderKeyRowState row)
    {
        string ordinal = _loc.F("SetupApiKeyOrdinal", row.Ordinal);
        string summary = row.Revealed ? row.Key : row.Key.Length > 4 ? "●●●●" + row.Key[^4..] : "●●●●";
        string active = !row.Provider.BalanceApiKeys && CurrentProviderKeyId(row.Provider) == row.Id
            ? $" · {_loc["SetupApiKeyActive"]}" : "";
        row.Label.Text = $"{ordinal} · {summary}{active}";
        row.Label.TextWrapping = row.Revealed ? TextWrapping.Wrap : TextWrapping.NoWrap;
        row.Label.TextTrimming = row.Revealed ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        SetKeyActionName(row.Reveal, $"{ordinal} · {_loc[row.Revealed ? "SetupKeyHide" : "SetupKeyReveal"]}");
        SetKeyActionName(row.Edit, $"{ordinal} · {_loc["SetupKeyEdit"]}");
        SetKeyActionName(row.Remove, $"{ordinal} · {_loc["SetupKeyRemove"]}");
        SetKeyActionName(row.EditBox, $"{ordinal} · {_loc["SetupKeyEdit"]}");
        row.EditBox.PlaceholderText = _loc["SetupKeyEdit"];
        row.Cancel.Content = _loc["Cancel"];
        SetKeyActionName(row.Cancel, _loc["Cancel"]);
        SetKeyActionName(row.Picker, $"{ordinal} · {_loc["SetupKeySelectModel"]}");
        row.Picker.PlaceholderText = _loc["SetupKeySelectModel"];
        bool hasModels = HasKeyModels(row);
        row.Probe.IsEnabled = hasModels && !_keyMutation && _allKeyProbeCts is null && !string.IsNullOrWhiteSpace(row.Key);
        SetKeyActionName(row.Probe, $"{ordinal} · {_loc[hasModels ? "SetupKeyProbe" : "SetupKeyNoModel"]}");
        RefreshKeyRowStatus(row);
    }

    private void RefreshKeyRowStatus(ProviderKeyRowState row)
    {
        if (row.CheckingModelId is { } checkingId && (_settings.FindModel(checkingId) is not { } checkingModel
            || row.CheckingVersion != (_health?.Version ?? 0)
            || !KeyModelEligible(row, checkingModel.Config)
            || row.CheckingRequest != KeyRequestSnapshot.Capture(checkingModel))) CancelKeyProbe(row);
        if (row.Result is { } result)
        {
            ResolvedModel? live = _settings.FindModel(result.ModelId);
            if (result.Version != (_health?.Version ?? 0) || live is null
                || !ReferenceEquals(live.Provider, row.Provider) || live.Config.HasOwnApiKey
                || !KeyModelEligible(row, live.Config)
                || result.Request != KeyRequestSnapshot.Capture(live)) row.Result = null;
        }
        string text, brush = "VelaTextMuted", icon = "AiIcon.key-untested";
        string? detail = null;
        if (string.IsNullOrWhiteSpace(row.Key)) text = _loc["StatusNeedsKey"];
        else if (row.CheckingModelId is { } checking)
        {
            text = _loc.F("SetupKeyChecking", _settings.FindModel(checking)?.Name ?? checking);
            icon = "AiIcon.heart-pulse";
        }
        else if (row.Result is { } last)
        {
            text = _loc.F(last.Ok ? "SetupKeyPassed" : "SetupKeyFailed",
                _settings.FindModel(last.ModelId)?.Name ?? last.Request.Model, last.At.ToString("HH:mm"));
            brush = last.Ok ? "VelaStatusConnected" : "VelaError";
            icon = last.Ok ? "AiIcon.circle-check" : "AiIcon.circle-x";
            detail = last.Error is null ? null : KeyErrorText(last.Error);
        }
        else if (!HasKeyModels(row)) text = _loc["SetupKeyNoModel"];
        else if (_health?.IsKeyCooling(row.Id) == true)
        {
            text = _loc["DotCooling"]; brush = "VelaError"; icon = "AiIcon.circle-x";
        }
        else text = _loc["SetupKeyUntested"];
        row.Status.Text = text;
        row.Status[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(brush);
        row.Dot[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(icon);
        row.Dot[!Avalonia.Controls.Shapes.Path.StrokeProperty] = new DynamicResourceExtension(brush);
        SetKeyActionName(row.Status, detail is null ? text : $"{text}\n{detail}");
        ToolTip.SetTip(row.Dot, detail is null ? text : $"{text}\n{detail}");
    }

    private string KeyErrorText(Exception error)
    {
        string detail = error switch
        {
            HealthProbe.EmptyReplyException => _loc["ProbeEmptyReply"],
            AiSettingsStore.ApiKeySlotChangedException => _loc["SetupConfigChanged"],
            _ => Chat.ApiErrorText.Describe(error, _loc["ErrorUnreachable"])
        };
        foreach (ProviderKeyRowState row in _keyRows)
            if (!string.IsNullOrWhiteSpace(row.Key)) detail = detail.Replace(row.Key, "●●●●", StringComparison.Ordinal);
        Redact(PendingProviderKey);
        Redact(ProviderNewApiKeyBox.Text);
        Redact(_keyDraft?.OriginalKey);
        void Redact(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key)) detail = detail.Replace(key, "●●●●", StringComparison.Ordinal);
        }
        return detail;
    }

    private void BeginKeyEdit(ProviderKeyRowState row)
    {
        if (ReferenceEquals(_savingKeyRow, row) || !KeyRowCurrent(row, _editorRun)) return;
        CancelAllKeysProbe();
        if (_keyDraft is { } old) CancelKeyEdit(old);
        CancelKeyProbe(row);
        foreach (ProviderKeyRowState other in _keyRows) { other.Revealed = false; ApplyKeyRowLoc(other); }
        row.Result = null;
        row.Picker.IsVisible = false;
        row.OriginalKey = row.Key;
        row.OriginalUrl = row.Provider.BaseUrl;
        row.OriginalProtocol = row.Provider.DefaultProtocol;
        row.EditBox.Text = "";
        row.EditPanel.IsVisible = true;
        _keyDraft = row;
        _pendingRemoveKeyId = null;
        ApplyKeyRowLoc(row);
        row.EditBox.Focus();
    }

    private void CancelKeyEdit(ProviderKeyRowState row)
    {
        row.EditBox.Text = "";
        row.EditPanel.IsVisible = false;
        if (ReferenceEquals(_keyDraft, row)) _keyDraft = null;
    }

    private static void CancelKeyProbe(ProviderKeyRowState row)
    {
        row.ProbeCts?.Cancel();
        row.CheckingModelId = null;
        row.CheckingRequest = null;
        ++row.ProbeRun;
    }

    private void SetKeyMutation(bool busy)
    {
        if (busy) CancelAllKeysProbe();
        _keyMutation = busy;
        ProviderAddApiKeyButton.IsEnabled = !busy;
        BalanceApiKeysCheck.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && SelectedItem is not null;
        foreach (ProviderKeyRowState row in _keyRows)
        {
            row.Remove.IsEnabled = !busy;
            row.Edit.IsEnabled = row.EditBox.IsEnabled = row.Cancel.IsEnabled = !busy || !ReferenceEquals(_savingKeyRow, row);
            ApplyKeyRowLoc(row);
        }
        RefreshAllKeysProbeControls();
    }

    private async Task AddProviderKeyAsync()
    {
        AiProvider? provider = SelectedProvider;
        int run = _editorRun;
        if (_keyMutation || provider is null || SelectedModel is not null || provider.Auth != AuthMethod.ApiKey) return;
        if (ProviderDraftDirty(provider)) { StatusText.Text = _loc["SetupSaveProviderFirst"]; return; }
        string key = ProviderNewApiKeyBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(key)) { StatusText.Text = _loc["SetupUniqueApiKey"]; return; }
        CancellationToken token = _keyEditorCts.Token;
        SetKeyMutation(true);
        try
        {
            await _store.AddProviderApiKeyAsync(_settings, provider, key, token);
            _onProvidersChanged();
            if (run != _editorRun || token.IsCancellationRequested) return;
            if (ProviderNewApiKeyBox.Text == key) ProviderNewApiKeyBox.Text = "";
            await RefreshProviderKeyRowsAsync(provider, run);
            StatusText.Text = _loc["Saved"];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentException) { if (run == _editorRun) StatusText.Text = _loc["SetupUniqueApiKey"]; }
        catch (AiSettingsStore.ApiKeySlotChangedException) { await RecoverSettingsConflictAsync(provider, null, run); }
        catch (Exception ex) { if (run == _editorRun) StatusText.Text = _loc.F("TestFail", KeyErrorText(ex)); }
        finally { SetKeyMutation(false); }
    }

    private async Task RemoveProviderKeyAsync(ProviderKeyRowState row)
    {
        int run = _editorRun;
        if (_keyMutation || !KeyRowCurrent(row, run)) return;
        if (ProviderDraftDirty(row.Provider)) { StatusText.Text = _loc["SetupSaveProviderFirst"]; return; }
        if (_pendingRemoveKeyId != row.Id)
        {
            _pendingRemoveKeyId = row.Id;
            StatusText.Text = _loc.F("SetupRemoveApiKeyConfirm", row.Ordinal);
            return;
        }
        CancellationToken token = _keyEditorCts.Token;
        SetKeyMutation(true);
        CancelKeyProbe(row);
        foreach (ProviderKeyRowState other in _keyRows) { other.Revealed = false; ApplyKeyRowLoc(other); }
        try
        {
            await _store.RemoveProviderApiKeyAsync(_settings, row.Provider, row.Id, token);
            row.Result = null;
            _onProvidersChanged();
            if (run != _editorRun || token.IsCancellationRequested) return;
            _pendingRemoveKeyId = null;
            await RefreshProviderKeyRowsAsync(row.Provider, run);
            StatusText.Text = _loc["Saved"];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AiSettingsStore.ApiKeySlotChangedException) { await RecoverSettingsConflictAsync(row.Provider, null, run); }
        catch (Exception ex) { if (run == _editorRun) StatusText.Text = _loc.F("TestFail", KeyErrorText(ex)); }
        finally { SetKeyMutation(false); }
    }

    private async Task SaveBalanceAsync()
    {
        if (_settingBalance || _loadingEditor || _keyMutation || SelectedModel is not null
            || SelectedProvider is not { Auth: AuthMethod.ApiKey } provider || !_settings.Providers.Contains(provider)) return;
        bool previous = provider.BalanceApiKeys, value = BalanceApiKeysCheck.IsChecked == true;
        if (previous == value) return;
        int run = _editorRun;
        provider.BalanceApiKeys = value;
        SetKeyMutation(true);
        try { await _store.SaveAsync(_settings); }
        catch (Exception ex)
        {
            if (provider.BalanceApiKeys == value) provider.BalanceApiKeys = previous;
            if (run == _editorRun)
            {
                _settingBalance = true;
                try { BalanceApiKeysCheck.IsChecked = provider.BalanceApiKeys; }
                finally { _settingBalance = false; }
                if (ex is AiSettingsStore.ApiKeySlotChangedException) await RecoverSettingsConflictAsync(provider, null, run);
                else StatusText.Text = _loc.F("TestFail", KeyErrorText(ex));
            }
        }
        finally { SetKeyMutation(false); }
    }

    private void CancelAllKeysProbe() => _allKeyProbeCts?.Cancel();

    private bool HasInvalidKeyRows()
    {
        foreach (ProviderKeyRowState row in _keyRows)
            if (string.IsNullOrWhiteSpace(row.Key) || !KeyRowCurrent(row, _editorRun)) return true;
        return false;
    }

    private bool AllKeysCanUse(AiModelConfig model)
    {
        if (_keyRows.Count == 0) return false;
        foreach (ProviderKeyRowState row in _keyRows)
            if (!KeyModelEligible(row, model)) return false;
        return true;
    }

    private bool HasAllKeysModel()
    {
        if (_keyRows.Count == 0) return false;
        foreach (AiModelConfig model in _keyRows[0].Provider.Models)
            if (AllKeysCanUse(model)) return true;
        return false;
    }

    private List<KeyModelChoice> AllKeysModelChoices()
        => _keyRows.Count == 0 ? [] : KeyModelChoices(_keyRows[0])
            .Where(choice => _keyRows[0].Provider.Models.Find(model => model.Id == choice.Id) is { } model
                && AllKeysCanUse(model)).ToList();

    private void RefreshAllKeysProbeControls()
    {
        bool busy = _allKeyProbeCts is not null;
        bool eligible = HasAllKeysModel();
        bool invalid = HasInvalidKeyRows();
        ProviderAllKeysHintText.Text = invalid ? _loc["SetupKeysInvalidDraft"] : "";
        ProviderAllKeysHintText.IsVisible = invalid;
        ProviderProbeAllKeysButton.Content = _loc[busy ? "Cancel" : "SetupKeysProbeAll"];
        ProviderProbeAllKeysButton.IsEnabled = busy || !_keyMutation && eligible && !invalid;
        SetKeyActionName(ProviderProbeAllKeysButton, _loc[busy ? "Cancel" : invalid ? "SetupKeysInvalidDraft"
            : eligible ? "SetupKeysProbeAll" : "SetupKeysNoModel"]);
        ProviderAllKeysModelPicker.IsEnabled = !busy && !_keyMutation && !invalid;
        ProviderAllKeysModelPicker.PlaceholderText = _loc["SetupKeysSelectModel"];
        SetKeyActionName(ProviderAllKeysModelPicker, _loc["SetupKeysSelectModel"]);
    }

    private void OpenAllKeysModelPicker()
    {
        if (_allKeyProbeCts is not null) { CancelAllKeysProbe(); return; }
        if (_keyMutation || SelectedModel is not null || SelectedProvider is not { Auth: AuthMethod.ApiKey } provider) return;
        if (HasInvalidKeyRows()) { RefreshAllKeysProbeControls(); return; }
        if (ProviderDraftDirty(provider)) { StatusText.Text = _loc["SetupSaveProviderFirst"]; return; }
        List<KeyModelChoice> choices = AllKeysModelChoices();
        ProviderAllKeysModelPicker.SelectionChanged -= OnAllKeysModelSelected;
        ProviderAllKeysModelPicker.ItemsSource = choices;
        ProviderAllKeysModelPicker.SelectedIndex = -1;
        ProviderAllKeysModelPicker.SelectionChanged += OnAllKeysModelSelected;
        ProviderAllKeysModelPicker.IsVisible = choices.Count > 0;
        if (ProviderAllKeysModelPicker.IsVisible) ProviderAllKeysModelPicker.Focus();
    }

    private void OnAllKeysModelSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ProviderAllKeysModelPicker.SelectedItem is KeyModelChoice choice) _ = ProbeAllProviderKeysAsync(choice.Id);
    }

    private async Task ProbeAllProviderKeysAsync(string modelId)
    {
        if (_allKeyProbeCts is not null || _keyMutation || SelectedModel is not null
            || SelectedProvider is not { Auth: AuthMethod.ApiKey } provider) return;
        if (HasInvalidKeyRows()) { RefreshAllKeysProbeControls(); return; }
        if (ProviderDraftDirty(provider)) { StatusText.Text = _loc["SetupSaveProviderFirst"]; return; }
        if (_settings.FindModel(modelId) is not { } model || !ReferenceEquals(model.Provider, provider)
            || _keyRows.Count == 0 || _keyRows.Any(row => !KeyModelEligible(row, model.Config))) return;
        int editorRun = _editorRun, version = _health?.Version ?? 0;
        KeyRequestSnapshot snapshot = KeyRequestSnapshot.Capture(model);
        var rows = _keyRows.Select(row => (Row: row, Key: row.Key)).ToArray();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_keyEditorCts.Token);
        _allKeyProbeCts = cts;
        foreach (var (row, _) in rows)
        {
            CancelKeyProbe(row);
            row.Picker.IsVisible = false;
            ApplyKeyRowLoc(row);
        }
        RefreshAllKeysProbeControls();
        try
        {
            // ponytail: 顺序探测避免同时突发请求；确有等待压力时再增加有界并发。
            foreach (var (row, _) in rows)
            {
                if (cts.IsCancellationRequested || _keyMutation || ProviderDraftDirty(provider)
                    || version != (_health?.Version ?? 0) || _keyRows.Count != rows.Length
                    || rows.Any(entry => !KeyRowCurrent(entry.Row, editorRun) || entry.Row.Key != entry.Key)
                    || _settings.FindModel(modelId) is not { } live || snapshot != KeyRequestSnapshot.Capture(live)) break;
                await ProbeProviderKeyAsync(row, modelId, cts.Token);
            }
        }
        finally
        {
            if (ReferenceEquals(_allKeyProbeCts, cts)) _allKeyProbeCts = null;
            RefreshAllKeysProbeControls();
            foreach (ProviderKeyRowState row in _keyRows) ApplyKeyRowLoc(row);
        }
    }

    private void OpenKeyModelPicker(ProviderKeyRowState row)
    {
        if (!KeyRowCurrent(row, _editorRun) || _keyMutation) return;
        if (_allKeyProbeCts is not null) return;
        if (ProviderConnectionDirty(row.Provider) || PendingProviderKey is not null)
        {
            CancelKeyProbe(row);
            row.Picker.IsVisible = false;
            StatusText.Text = _loc["SetupSaveProviderFirst"];
            return;
        }
        bool wasChecking = row.ProbeCts is not null;
        CancelKeyProbe(row);
        List<KeyModelChoice> choices = KeyModelChoices(row);
        row.Picker.SelectionChanged -= OnKeyModelSelected;
        row.Picker.ItemsSource = choices;
        row.Picker.SelectedItem = choices.Find(choice => choice.Id == _lastKeyProbeModelId);
        row.Picker.SelectionChanged += OnKeyModelSelected;
        row.Picker.IsVisible = choices.Count > 0;
        ApplyKeyRowLoc(row);
        if (choices.Count > 0) row.Picker.Focus();
        // 用户点击检测才复用上次型号；装载、重绘或换语言不触发请求。再次点击在途行只取消。
        if (!wasChecking && row.Picker.SelectedItem is KeyModelChoice previous)
            _ = ProbeProviderKeyAsync(row, previous.Id);
    }

    private void OnKeyModelSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string keyId, SelectedItem: KeyModelChoice choice }) return;
        if (_keyRows.Find(row => row.Id == keyId) is { } row)
        {
            _lastKeyProbeModelId = choice.Id;
            _ = ProbeProviderKeyAsync(row, choice.Id);
        }
    }

    private async Task ProbeProviderKeyAsync(ProviderKeyRowState row, string modelId, CancellationToken batchToken = default)
    {
        int editorRun = _editorRun;
        if (!KeyRowCurrent(row, editorRun)) return;
        if (ProviderConnectionDirty(row.Provider) || PendingProviderKey is not null)
        {
            CancelKeyProbe(row);
            row.Picker.IsVisible = false;
            StatusText.Text = _loc["SetupSaveProviderFirst"];
            return;
        }
        if (_settings.FindModel(modelId) is not { } live || !ReferenceEquals(live.Provider, row.Provider)
            || !KeyModelEligible(row, live.Config)) return;
        CancelKeyProbe(row);
        int run = row.ProbeRun;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_keyEditorCts.Token, batchToken);
        row.ProbeCts = cts;
        CancellationToken token = cts.Token;
        row.CheckingModelId = modelId;
        ResolvedModel candidate = FreezeProbeModel(live);
        KeyRequestSnapshot snapshot = KeyRequestSnapshot.Capture(live);
        int version = _health?.Version ?? 0;
        row.CheckingRequest = snapshot;
        row.CheckingVersion = version;
        long observation = _health?.BeginObservation() ?? 0;
        RefreshKeyRowStatus(row);
        try
        {
            var (credential, _) = await _store.ResolveCredentialWithKeyIdAsync(live, token, keyId: row.Id);
            if (credential.Value != row.Key) throw new AiSettingsStore.ApiKeySlotChangedException();
            if (string.IsNullOrWhiteSpace(credential.Value)
                || !await StillSavedKeyProbeAsync(row, modelId, snapshot, credential, editorRun, run, version, token)) return;
            (Exception? error, _) = await HealthProbe.ProbeAsync(_store, candidate, credential, token);
            token.ThrowIfCancellationRequested();
            if (!await StillSavedKeyProbeAsync(row, modelId, snapshot, credential, editorRun, run, version, token)) return;
            if (error is AiSettingsStore.ApiKeySlotChangedException) throw error;
            row.Result = new KeyProbeResult(modelId, snapshot, error is null, DateTime.Now, error, version);
            if (error is null)
            {
                _health?.RecordKey(row.Id, true, version, observation);
                _health?.Record(modelId, true, version, observation);
            }
            else if (TransientFailure.IsApiKeyFailure(error))
                _health?.RecordKey(row.Id, false, version, observation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AiSettingsStore.ApiKeySlotChangedException) { await RecoverSettingsConflictAsync(row.Provider, null, editorRun); }
        catch (Exception ex)
        {
            if (run == row.ProbeRun && KeyRowCurrent(row, editorRun))
                StatusText.Text = _loc.F("TestFail", KeyErrorText(ex));
        }
        finally
        {
            if (ReferenceEquals(row.ProbeCts, cts)) row.ProbeCts = null;
            if (run == row.ProbeRun)
            {
                row.CheckingModelId = null;
                RefreshKeyRowStatus(row);
            }
            cts.Dispose();
        }
    }

    private async Task<bool> StillSavedKeyProbeAsync(ProviderKeyRowState row, string modelId,
        KeyRequestSnapshot snapshot, ProviderCredential credential, int editorRun, int probeRun,
        int version, CancellationToken token)
    {
        if (token.IsCancellationRequested || probeRun != row.ProbeRun || !KeyRowCurrent(row, editorRun)
            || version != (_health?.Version ?? 0) || _settings.FindModel(modelId) is not { } saved
            || !ReferenceEquals(saved.Provider, row.Provider) || saved.Config.HasOwnApiKey
            || snapshot != KeyRequestSnapshot.Capture(saved)
            || !KeyModelEligible(row, saved.Config)) return false;
        var (now, _) = await _store.ResolveCredentialWithKeyIdAsync(saved, token, refreshTokens: false, keyId: row.Id);
        return !token.IsCancellationRequested && probeRun == row.ProbeRun && KeyRowCurrent(row, editorRun)
            && version == (_health?.Version ?? 0) && snapshot == KeyRequestSnapshot.Capture(saved)
            && credential == now;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_keyEditorCts.IsCancellationRequested)
        {
            _keyEditorCts.Dispose();
            _keyEditorCts = new CancellationTokenSource();
        }
        _keyLightTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _keyLightTimer.Tick -= OnKeyLightTick;
        _keyLightTimer.Tick += OnKeyLightTick;
        _keyLightTimer.Start();
        foreach (ProviderKeyRowState row in _keyRows) ApplyKeyRowLoc(row);
    }

    private void OnKeyLightTick(object? sender, EventArgs e)
    {
        foreach (ProviderKeyRowState row in _keyRows) ApplyKeyRowLoc(row);
        RefreshAllKeysProbeControls();
        if (!_refreshingKeyRowsOnTick && !_loadingEditor && SelectedModel is null
            && SelectedProvider is { Auth: AuthMethod.ApiKey } provider)
            _ = RefreshKeyRowsOnTickAsync(provider, _editorRun);
    }
    private async Task RefreshKeyRowsOnTickAsync(AiProvider provider, int run)
    {
        _refreshingKeyRowsOnTick = true;
        try { await RefreshProviderKeyRowsAsync(provider, run); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _context.Log.Warn($"Refreshing provider Key rows failed: {ex.GetType().Name}"); }
        finally { _refreshingKeyRowsOnTick = false; }
    }


    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyLightTimer?.Stop();
        if (_keyLightTimer is not null) _keyLightTimer.Tick -= OnKeyLightTick;
        CancelAllKeysProbe();
        ProviderAllKeysModelPicker.IsVisible = false;
        _keyEditorCts.Cancel();
        foreach (ProviderKeyRowState row in _keyRows)
        {
            CancelKeyProbe(row);
            row.Revealed = false;
            CancelKeyEdit(row);
            row.Picker.IsVisible = false;
            ApplyKeyRowLoc(row);
        }
        base.OnDetachedFromVisualTree(e);
    }


    private async Task MergeSavedModelAsync(AiProvider provider, AiModelConfig model, int run, Task pending)
    {
        bool Current() => run == _editorRun && ReferenceEquals(SelectedProvider, provider)
            && ReferenceEquals(SelectedModel, model) && _settings.Providers.Contains(provider)
            && provider.Models.Contains(model);
        await pending;
        if (!Current()) return;
        string key = model.HasOwnApiKey ? await _store.GetApiKeyAsync(model.Id) ?? "" : "";
        if (!Current()) return;
        // 两次等待都结束后再读取本页最新输入，未编辑字段从当前保存模型重填。
        Dictionary<Control, object?> dirty = CaptureModelForm()
            .Where(field => field.Key == MaxInputTokensBox && _windowEdited
                || !_loadedModelForm.TryGetValue(field.Key, out object? loaded) || !Equals(loaded, field.Value))
            .ToDictionary(field => field.Key, field => field.Value);
        ModelSpec? windowSpec = _windowSpecDraft;
        bool windowEdited = _windowEdited;
        Control? focused = this.GetLogicalDescendants().OfType<Control>().FirstOrDefault(control => control.IsFocused);
        bool wasLoading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            RefreshEditorContext(provider, model);
            ShowSavedModelForm(provider, model);
            ApiKeyBox.Text = _loadedKey = key;
            _loadedModelForm[ApiKeyBox] = key;
            foreach ((Control control, object? value) in dirty)
            {
                switch (control)
                {
                    case TextBox text when ReferenceEquals(text, MaxInputTokensBox): SetWindowText((string?)value); break;
                    case TextBox text: text.Text = (string?)value; break;
                    case ComboBox combo: combo.SelectedIndex = (int)value!; break;
                    case CheckBox check: check.IsChecked = (bool?)value; break;
                }
            }
            // 下拉只反映合并后的当前草稿，程序性选中不能再回填规格。
            ModelPickCombo.SelectedItem = provider.AvailableModels.Find(
                id => string.Equals(id, ModelBox.Text, StringComparison.OrdinalIgnoreCase));
            if (windowSpec is not null && windowSpec.Id == ModelBox.Text
                && int.TryParse(MaxInputTokensBox.Text, out int window) && windowSpec.ContextTokens == window)
                _windowSpecDraft = windowSpec;
            _windowEdited = windowEdited;
            UpdateContextWindowHint();
        }
        finally { _loadingEditor = wasLoading; }
        focused?.Focus();
    }

    private void ClearStaleNoModels()
    {
        if (SelectedProvider is { Models.Count: > 0 } && StatusText.Text == _loc["NoModels"])
            StatusText.Text = "";
    }

    /// <summary>目录页异步拉取模型后的被动刷新:保持选中项和未保存的表单。</summary>
    public void RefreshCatalogModels()
    {
        ClearStaleNoModels();
        ProviderNavItem? before = SelectedItem;
        string? selectedId = SelectedItem is { } item ? NavKey(item) : null;
        _refreshingNav = true;
        try
        {
            ReloadList(selectedId, selectFallback: false);
        }
        finally
        {
            _refreshingNav = false;
        }
        if (SelectedItem is null)
        {
            // 外部删除时保留原表单与独立 Key 草稿，不能自动将它们换绑到第一行。
            if (before is not null) ++_editorRun;
            _loadingEditor = false;
            SaveButton.IsEnabled = TestButton.IsEnabled = DeleteButton.IsEnabled = AddModelButton.IsEnabled = false;
            if (before is not null) StatusText.Text = _loc["SetupConfigChanged"];
            return;
        }
        RefreshAfterCatalog(before);
    }

    private void OnAddModelClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }
        var model = new AiModelConfig();
        provider.Models.Add(model);
        _settings.ActiveModelId ??= model.Id;
        ReloadList(model.Id);
        _ = PersistAsync(notify: true);
    }

    // ---- 保存 ----

    private void OnSaveClick(object? sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        ProviderNavItem? before = SelectedItem;
        int run = _editorRun;
        try
        {
            await _editorLoad;
            if (run != _editorRun) return;
            if (before is not null && before.Model is null)
                await MergeSavedProviderAsync(before.Provider, run, Task.CompletedTask);
            else if (before?.Model is { } selectedModel)
                await MergeSavedModelAsync(before.Provider, selectedModel, run, Task.CompletedTask);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Merge saved AI settings before saving failed.", ex);
            if (run == _editorRun)
            {
                _editorLoad = Task.CompletedTask;
                StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
            }
            return;
        }
        if (run != _editorRun || _loadingEditor || SelectedItem is not { } item
            || before is null || !ReferenceEquals(before.Provider, item.Provider) || !ReferenceEquals(before.Model, item.Model))
        {
            return;
        }
        if (item.Model is null)
        {
            await SaveProviderFormAsync(item.Provider, run);
            return;
        }
        AiModelConfig liveModel = item.Model;
        string expectedModel = JsonSerializer.Serialize(liveModel);
        AiModelConfig model = JsonSerializer.Deserialize<AiModelConfig>(expectedModel)!;
        {
            model.Name = NameBox.Text?.Trim() ?? "";
            model.Model = ModelBox.Text?.Trim() ?? "";
            model.Protocol = ProtocolCombo.SelectedIndex > 0 ? (ChatProtocol)(ProtocolCombo.SelectedIndex - 1) : null;
            model.HasOwnApiKey = OwnKeyCheck.IsChecked == true;
            model.BaseUrlOverride = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim();
            if (int.TryParse(MaxTokensBox.Text?.Trim(), out int maxTokens) && maxTokens > 0)
            {
                model.MaxTokens = maxTokens;
            }
            // 输入上限允许填 0 —— 那表示"窗口未知",用量只显示累计不显示占比
            if (int.TryParse(MaxInputTokensBox.Text?.Trim(), out int maxInputTokens) && maxInputTokens >= 0)
            {
                model.MaxInputTokens = maxInputTokens;
            }
            model.Reasoning = ReasoningCombo.SelectedIndex >= 0
                ? (ReasoningLevel)ReasoningCombo.SelectedIndex
                : model.Reasoning;
            model.PromptCaching = PromptCacheCheck.IsChecked == true;
            model.Temperature = ParseOptional(TemperatureBox.Text);
            model.TopP = ParseOptional(TopPBox.Text);
            model.StopSequences = StopBox.Text ?? "";
            model.InputPricePerMillion = ParsePrice(PriceInBox.Text);
            model.OutputPricePerMillion = ParsePrice(PriceOutBox.Text);
            model.CachedInputPricePerMillion = ParsePrice(PriceCachedBox.Text);
            model.SystemPrompt = string.IsNullOrWhiteSpace(ProviderPromptBox.Text) ? null : ProviderPromptBox.Text;
        }
        if (int.TryParse(MaxInputTokensBox.Text?.Trim(), out int editedWindow) && editedWindow >= 0
            && (_windowEdited || _windowSpecDraft is { } selected
                && selected.Id == model.Model && selected.ContextTokens == model.MaxInputTokens))
        {
            model.DefaultContextWindow = false;
            model.ContextWindowIsManual = true;
        }
        Dictionary<Control, object?> submittedForm = CaptureModelForm();
        int submittedWindowRevision = _windowEditRevision;
        string? submittedKey = model.HasOwnApiKey ? ApiKeyBox.Text : null;
        try
        {
            string? expectedKey = liveModel.HasOwnApiKey ? string.IsNullOrEmpty(_loadedKey) ? null : _loadedKey
                : await _store.GetApiKeyAsync(liveModel.Id);
            if (run != _editorRun || !ReferenceEquals(SelectedProvider, item.Provider)
                || !ReferenceEquals(SelectedModel, liveModel)) return;
            await _store.SaveModelFormAsync(_settings, item.Provider, liveModel, model, expectedModel,
                submittedKey, expectedKey);
            _onProvidersChanged();
            if (run != _editorRun) return;
            _loadedModelForm = submittedForm;
            if (_windowEditRevision == submittedWindowRevision)
            {
                _windowEdited = false;
                _windowSpecDraft = null;
            }
            _refreshingNav = true;
            try { ReloadList(liveModel.Id); }
            finally { _refreshingNav = false; }
            await MergeSavedModelAsync(item.Provider, liveModel, run, Task.CompletedTask);
            if (run != _editorRun) return;
            StatusText.Text = _loc["Saved"];
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            await RecoverSettingsConflictAsync(item.Provider, liveModel, run);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Save AI settings failed.", ex);
            if (run == _editorRun) StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }
    private async Task SaveProviderFormAsync(AiProvider provider, int run)
    {
        if (_keyMutation || !_settings.Providers.Contains(provider)) return;
        ProviderKeyRowState? draft = _keyDraft;
        Control? draftFocus = draft?.EditBox.IsFocused == true ? draft.EditBox : null;
        string? replacement = PendingProviderKey;
        if (replacement is not null && (draft is null || !KeyRowCurrent(draft, run)
            || provider.Auth != AuthMethod.ApiKey))
        {
            StatusText.Text = _loc["SetupConfigChanged"];
            return;
        }
        string name = ProviderNameBox.Text?.Trim() ?? "";
        string url = ProviderBaseUrlBox.Text?.Trim() ?? "";
        ChatProtocol protocol = ProviderProtocolCombo.SelectedIndex >= 0
            ? (ChatProtocol)ProviderProtocolCombo.SelectedIndex : provider.DefaultProtocol;
        _savingKeyRow = draft;
        SetKeyMutation(true);
        try
        {
            if (replacement is not null)
            {
                string? currentKey = await _store.GetApiKeyAsync(draft!.Id);
                if (run != _editorRun || !ReferenceEquals(_keyDraft, draft)
                    || !KeyRowCurrent(draft, run)) return;
                if (draft.OriginalUrl != provider.BaseUrl || draft.OriginalProtocol != provider.DefaultProtocol
                    || draft.OriginalKey != (currentKey ?? "")) throw new AiSettingsStore.ApiKeySlotChangedException();
            }
            if (run != _editorRun || !ReferenceEquals(SelectedProvider, provider)
                || SelectedModel is not null || !_settings.Providers.Contains(provider)) return;
            if (provider.Auth == AuthMethod.ApiKey)
            {
                string expected = JsonSerializer.Serialize(provider);
                AiProvider detached = JsonSerializer.Deserialize<AiProvider>(expected)!;
                detached.Name = name;
                detached.BaseUrl = url;
                detached.DefaultProtocol = protocol;
                string keyId = replacement is null ? provider.Id : draft!.Id;
                string? expectedKey = replacement is null ? await _store.GetApiKeyAsync(keyId)
                    : string.IsNullOrEmpty(draft!.OriginalKey) ? null : draft.OriginalKey;
                if (run != _editorRun || !ReferenceEquals(SelectedProvider, provider)) return;
                await _store.SaveProviderApiKeyFormAsync(_settings, provider, detached, expected, replacement,
                    keyId: keyId, expectedKey: expectedKey);
            }
            else
            {
                string expected = JsonSerializer.Serialize(provider);
                AiProvider detached = JsonSerializer.Deserialize<AiProvider>(expected)!;
                detached.Name = name;
                detached.BaseUrl = url;
                detached.DefaultProtocol = protocol;
                await _store.SaveProviderFormAsync(_settings, provider, detached, expected);
            }
            if (draft is not null)
            {
                CancelKeyProbe(draft);
                draft.Result = null;
                draft.Revealed = false;
            }
            _onProvidersChanged();
            if (run != _editorRun || !ReferenceEquals(SelectedProvider, provider)) return;
            _loadedProviderName = provider.Name;
            _loadedProviderUrl = provider.BaseUrl;
            _loadedProviderProtocol = (int)provider.DefaultProtocol;
            if (ReferenceEquals(_keyDraft, draft) && draft is not null) CancelKeyEdit(draft);
            _refreshingNav = true;
            try { ReloadList(provider.Id); }
            finally { _refreshingNav = false; }
            RefreshEditorContext(provider, null);
            await RefreshProviderKeyRowsAsync(provider, run);
            if (run == _editorRun && (_keyDraft is null || ReferenceEquals(_keyDraft, draft))) StatusText.Text = _loc["Saved"];
        }
        catch (ArgumentException) { if (run == _editorRun) StatusText.Text = _loc["SetupUniqueApiKey"]; }
        catch (AiSettingsStore.ApiKeySlotChangedException) { await RecoverSettingsConflictAsync(provider, null, run); }
        catch (Exception ex) { if (run == _editorRun) StatusText.Text = _loc.F("TestFail", KeyErrorText(ex)); }
        finally
        {
            _savingKeyRow = null;
            SetKeyMutation(false);
            if (draftFocus is not null && run == _editorRun && ReferenceEquals(SelectedProvider, provider)
                && ReferenceEquals(_keyDraft, draft)) draftFocus.Focus();
        }
    }


    // ---- 删除 ----

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => _ = DeleteAsync();

    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }
        AiProvider provider = item.Provider;
        int index = ProvidersList.SelectedIndex;
        int run = _editorRun;
        try
        {
            if (item.Model is { } model)
            {
                await _store.DeleteModelAsync(_settings, provider, model);
            }
            else
            {
                // 删供应商连带删它下面所有模型 —— 两击确认,第一击只是提示
                if (_pendingDeleteProviderId != provider.Id)
                {
                    _pendingDeleteProviderId = provider.Id;
                    StatusText.Text = _loc.F("DeleteProviderConfirm", provider.Models.Count);
                    return;
                }
                _pendingDeleteProviderId = null;
                await _store.DeleteProviderAsync(_settings, provider);
            }
            _onProvidersChanged();
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            await RecoverSettingsConflictAsync(provider, item.Model, run);
            return;
        }
        catch (Exception ex)
        {
            _context.Log.Error("Delete provider failed.", ex);
            if (run == _editorRun) StatusText.Text = _loc.F("TestFail", KeyErrorText(ex));
            return;
        }
        if (run != _editorRun) return;
        // 删完落在上一行(模型删了落回供应商;供应商删了落到前一个供应商末尾)
        string? next = index > 0 && index - 1 < _nav.Count ? (_nav[index - 1].Model?.Id ?? _nav[index - 1].Provider.Id) : null;
        ReloadList(next);
    }

    // ---- 测试 ----

    private void OnTestClick(object? sender, RoutedEventArgs e) => _ = TestAsync();

    /// <summary>
    /// 用表单当前值(可能未保存)测试。选中模型就测它;选中供应商就拿它下面第一个模型探活 ——
    /// 供应商本身没法单独"连一下",总得有个模型 id 才发得出请求。
    /// </summary>
    private async Task TestAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }
        AiProvider provider = item.Provider;
        int editorRun = _editorRun;
        ResolvedModel candidate;
        string? apiKeyOverride;
        string? requestedKeyId = null;
        string? overrideKeyId = null;
        // 探活结束时再校验本次请求的完整凭据与当下落库配置是否一致。
        // 等待期间可能换账号、刷新令牌或保存新的地址/Key。
        string compareId;
        if (item.Model is { } model)
        {
            var draft = new AiModelConfig
            {
                Id = model.Id,
                Name = NameBox.Text ?? "",
                Model = ModelBox.Text?.Trim() ?? "",
                Protocol = ProtocolCombo.SelectedIndex > 0 ? (ChatProtocol)(ProtocolCombo.SelectedIndex - 1) : null,
                HasOwnApiKey = OwnKeyCheck.IsChecked == true,
                BaseUrlOverride = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim(),
                MaxTokens = int.TryParse(MaxTokensBox.Text?.Trim(), out int maxTokens) && maxTokens > 0 ? maxTokens : model.MaxTokens,
                Reasoning = ReasoningCombo.SelectedIndex >= 0 ? (ReasoningLevel)ReasoningCombo.SelectedIndex : model.Reasoning,
                Temperature = ParseOptional(TemperatureBox.Text),
                TopP = ParseOptional(TopPBox.Text),
                StopSequences = StopBox.Text ?? ""
            };
            candidate = new ResolvedModel(provider, draft);
            // 独立 Key 用表单里的;继承的走供应商已保存的那把
            apiKeyOverride = draft.HasOwnApiKey ? ApiKeyBox.Text : null;
            // "表单 == 落库"的校验挪到探活跑完之后(见 SameAsSavedAsync)——
            // 这儿只记下要拿哪一份落库来比(按 id,比的时候重新查)。
            compareId = model.Id;
        }
        else
        {
            if (provider.Models.Count == 0)
            {
                StatusText.Text = _loc["NoModels"];
                return;
            }
            var draft = new AiProvider
            {
                Id = provider.Id,
                Name = ProviderNameBox.Text ?? "",
                BaseUrl = ProviderBaseUrlBox.Text?.Trim() ?? "",
                DefaultProtocol = ProviderProtocolCombo.SelectedIndex >= 0
                    ? (ChatProtocol)ProviderProtocolCombo.SelectedIndex
                    : provider.DefaultProtocol,
                // 目录 id 与端点限制要一并带上:探活与正式请求同样先过 ApplyEndpointQuirks,
                // 少了它们,内置 Codex 后端会直接拒掉这份探活请求;而结果又会记进共享健康表,
                // 等于把一个实际能聊的接入白冷却 60 秒(review⑤)
                CatalogId = provider.CatalogId,
                StoreResponses = provider.StoreResponses,
                AllowSystemMessages = provider.AllowSystemMessages,
                UnsupportedParameters = provider.UnsupportedParameters,
                // 订阅登录那家的凭据不在表单里,靠 Id 去机密存储取(见 ResolveCredentialAsync)
                Auth = provider.Auth,
                OAuth = provider.OAuth,
                AdditionalApiKeyIds = [.. provider.AdditionalApiKeyIds], ActiveApiKeyId = provider.ActiveApiKeyId
            };
            AiModelConfig first = provider.Models[0];
            candidate = new ResolvedModel(draft, first);
            apiKeyOverride = first.HasOwnApiKey || provider.Auth == AuthMethod.Subscription
                ? null
                : PendingProviderKey;
            if (!first.HasOwnApiKey && provider.Auth == AuthMethod.ApiKey)
            {
                overrideKeyId = apiKeyOverride is not null ? _keyDraft?.Id : null;
                if (apiKeyOverride is null && !AiSettingsStore.SameOrigin(draft.BaseUrl, provider.BaseUrl))
                    requestedKeyId = provider.Id;
            }
            compareId = first.Id; // 同上:拿哪份落库来比,探活跑完再定(见 SameAsSavedAsync)
        }
        if (apiKeyOverride is null && !candidate.Config.HasOwnApiKey && provider.Auth == AuthMethod.ApiKey
            && !AiSettingsStore.SameOrigin(candidate.BaseUrl, provider.BaseUrl)) requestedKeyId = provider.Id;
        candidate = FreezeProbeModel(candidate);
        ResolvedModel? savedCredentialModel = _settings.FindModel(compareId);
        KeyRequestSnapshot? savedRequest = savedCredentialModel is null ? null : KeyRequestSnapshot.Capture(savedCredentialModel);
        long savedProviderVersion = _store.ProviderConfigurationVersion(provider);
        int myRun = Interlocked.Increment(ref _testRun);
        string rowKey = NavKey(item);
        // 显示按行裁决;供应商行与首模型行虽有不同的点,共享健康却写同一个模型 id。
        _testRuns[rowKey] = myRun;
        TestButton.IsEnabled = false;
        StatusText.Text = _loc["Testing"];
        // 发请求前固定代际和起跑序号:等待令牌刷新或探活时的较新聊天成功不能被迟到失败盖掉。
        int healthEvidence = _health?.Version ?? 0;
        long observationId = _health?.BeginObservation() ?? 0;
        // 请求和健康裁决必须共用同一份凭据:解析订阅令牌可能触发刷新,
        // 再让探活自行解析一次会把 A 的快照与实际发出的 B 错配。
        var sent = (protocol: candidate.Protocol, baseUrl: candidate.BaseUrl,
            model: candidate.Model, maxTokens: candidate.MaxTokens, reasoning: candidate.Reasoning,
            temperature: candidate.Temperature, topP: candidate.TopP, stop: candidate.StopSequences,
            keyOwner: candidate.ApiKeyOwnerId);
        ProviderCredential sentCredential = default;
        string? sentKeyId = null;
        Exception? credentialError = null;
        try
        {
            await _store.ValidateProviderCredentialScopeAsync(provider);
            ResolvedModel saved = savedCredentialModel ?? throw new AiSettingsStore.ApiKeySlotChangedException();
            if (apiKeyOverride is null)
            {
                if (requestedKeyId == provider.Id && provider.Auth == AuthMethod.ApiKey)
                {
                    sentCredential = await _store.ResolvePrimaryCredentialForProbeAsync(saved);
                    sentKeyId = !string.IsNullOrWhiteSpace(sentCredential.Value)
                        && AiSettingsStore.SameOrigin(candidate.BaseUrl, provider.BaseUrl) ? provider.Id : null;
                }
                else
                    (sentCredential, sentKeyId) = candidate.Config.HasOwnApiKey == saved.Config.HasOwnApiKey
                        ? await _store.ResolveCredentialWithKeyIdAsync(saved)
                        : await _store.ResolveProviderCredentialWithKeyIdAsync(provider);
            }
            else
            {
                sentCredential = ProviderCredential.Key(apiKeyOverride);
                sentKeyId = overrideKeyId;
            }
            if (_store.ProviderConfigurationVersion(provider) != savedProviderVersion
                || savedRequest != KeyRequestSnapshot.Capture(new ResolvedModel(provider, saved.Config)))
                throw new AiSettingsStore.ApiKeySlotChangedException();
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            if (myRun == Volatile.Read(ref _testRun)) TestButton.IsEnabled = SelectedItem is not null;
            await RecoverSettingsConflictAsync(provider, item.Model, editorRun);
            return;
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Reading the credential for '{compareId}' failed: {ex.Message}");
            credentialError = ex;
        }
        try
        {
            (Exception? error, string text) = credentialError is null
                ? await HealthProbe.ProbeAsync(_store, candidate, sentCredential)
                : (credentialError, "");
            bool ok = error is null;
            // UI 按行裁决,与共享健康写回互不干扰:同行旧探测过期仍可能是落库配置的
            // 有效证据(例如新探测使用未保存的地址),不能因此跳过后面的健康校验。
            if (_testRuns.GetValueOrDefault(rowKey, -1) == myRun)
            {
                bool rowSelected = SelectedItem is { } selected && NavKey(selected) == rowKey;
                if (error is null)
                {
                    if (rowSelected)
                    {
                        StatusText.Text = _loc.F("TestOk", text.Length > 80 ? text[..80] + "…" : text);
                    }
                    _testResults[rowKey] = (true, DateTime.Now);
                }
                else
                {
                    string detail = error is HealthProbe.EmptyReplyException
                        ? _loc["ProbeEmptyReply"]
                        : Chat.ApiErrorText.Describe(error, _loc["ErrorUnreachable"]);
                    if (rowSelected)
                    {
                        StatusText.Text = _loc.F("TestFail", detail);
                    }
                    _testResults[rowKey] = (false, DateTime.Now);
                }
            }
            // 健康冷却表是**全局共享**的:表单还是草稿(与落库那份不一致)时,测出来的结果
            // 不能记到落库配置头上 —— 一个没保存的笔误会把好配置降进 60s 冷却、
            // 反过来一次没保存的侥幸成功会把真坏的配置从冷却里捞出来。草稿的结果就地显示,
            // 要喂给故障转移,先把它保存下来再测一次。
            // 一致性**此刻**才校验:探活最多 15 秒,这期间用户可能把新地址 / 新 Key 保存了
            // 下来 —— 开始时算好的一致结论到这儿已经过期,拿它去写会把旧请求的
            // 结果记到新配置头上(review④)。
            bool sameAsSaved = credentialError is null && await SameAsSavedAsync();
            // 草稿不占健康序号;仅与落库完全相同的探测参与该模型的先后裁决。
            if (sameAsSaved)
            {
                if (sentKeyId is not null && TransientFailure.IsApiKeyFailure(error))
                    _health?.RecordKey(sentKeyId, false, healthEvidence, observationId);
                else _health?.Record(candidate.Id, ok, healthEvidence, observationId);
            }
        }
        finally
        {
            // 被动目录刷新可能在探测在途时重建导航;状态按 id 写进当前的行,
            // 不重建列表或重填表单,以免冲掉未保存的草稿。
            if (_testRuns.GetValueOrDefault(rowKey, -1) == myRun)
            {
                foreach (ProviderNavItem current in _nav)
                {
                    if (string.Equals(NavKey(current), rowKey, StringComparison.Ordinal))
                    {
                        ApplyDot(current);
                    }
                }
                UpdateTestBadge();
            }
            // 按钮按**全局**代次认:最后起跑的那一轮才有权复位(此刻按下的是它,
            // 还没跑完就不许再放出第三次点击)。被同行更新一轮取代的旧一轮全局也最新不了,
            // 连带不碰按钮 —— 收尾顺序由最后起跑那轮说了算。
            if (myRun == Volatile.Read(ref _testRun))
            {
                TestButton.IsEnabled = SelectedItem is not null;
            }
        }

        // 机密解析失败时没有可验证的请求凭据:依然显示失败,但不回写共享健康。
        async Task<bool> KeyMatchesAsync(string typed, string ownerId)
        {
            try
            {
                string? stored = await _store.GetApiKeyAsync(ownerId);
                // "" 与 null 都是"没有这把钥匙":无密钥接入的表单值是空串、机密库里是 null,
                // 判不相等会让一次成功的手动测试解除不了这个模型已有的冷却(review⑥)
                return string.Equals(typed, stored ?? "", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"Reading the stored API key for '{ownerId}' failed: {ex.Message}");
                return false;
            }
        }

        // 探活跑完才校验"这次发出的那套 == 落库":等待期间用户可能已经把新配置保存了
        // 下来,开始时算好的一致结论到这儿已经过期。落库那份按 id **重新查** —— 拿到的
        // 才是此刻真存着的(保存会就地改这些对象);等待期间模型被删了就保守判不一致。
        // 左边比的是探活**开始前**抓的 sent 值快照,不是 candidate 的活属性(见方法开头,review⑧)。
        async Task<bool> SameAsSavedAsync()
        {
            AiProvider? live = _settings.Providers.Find(p => p.Id == provider.Id);
            AiModelConfig? liveModel = live?.Models.Find(m => m.Id == compareId);
            if (live is null || liveModel is null)
            {
                return false;
            }
            ResolvedModel saved = new(live, liveModel);
            // "等于落库"= 真正会发出去的那套(协议/地址/模型/上限/Key 归属)与已保存的一致;
            // 只改了显示名也算一致 —— 名字不上线。Key 归属先比(独立 ↔ 继承换了位置,
            // Key 项就没有可比性),再比凭据本身。
            if (sent.protocol != saved.Protocol
                || sent.baseUrl != saved.BaseUrl
                || sent.model != saved.Model
                || sent.maxTokens != saved.MaxTokens
                || sent.reasoning != saved.Reasoning
                || sent.temperature != saved.Temperature
                || sent.topP != saved.TopP
                || sent.stop != saved.StopSequences
                || sent.keyOwner != saved.ApiKeyOwnerId)
            {
                return false;
            }
            if (apiKeyOverride is not null)
            {
                // 表单里填了 Key:那把 Key 此刻还在库里吗
                return (sentKeyId is null || _store.ProviderApiKeyIds(live).Contains(sentKeyId, StringComparer.Ordinal))
                    && await KeyMatchesAsync(apiKeyOverride, sentKeyId ?? sent.keyOwner);
            }
            // 值、鉴权形态、令牌指定的实际地址及解析后的账户头必须全相同。
            // 令牌文本即使不变,刷新后的端点或账号头也可能指向另一份接入。
            try
            {
                var (now, nowKeyId) = await _store.ResolveCredentialWithKeyIdAsync(saved, refreshTokens: false);
                if (nowKeyId != sentKeyId) return false;
                return string.Equals(sentCredential.Value, now.Value, StringComparison.Ordinal)
                    && sentCredential.IsBearerToken == now.IsBearerToken
                    && string.Equals(sentCredential.BaseUrl, now.BaseUrl, StringComparison.Ordinal)
                    && (sentCredential.Headers ?? []).SequenceEqual(now.Headers ?? []);
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"Re-reading the credential for '{compareId}' failed: {ex.Message}");
                return false;
            }
        }
    }

    private async Task PersistAsync(bool notify)
    {
        await _store.SaveAsync(_settings);
        if (notify)
        {
            _onProvidersChanged();
        }
    }
}
