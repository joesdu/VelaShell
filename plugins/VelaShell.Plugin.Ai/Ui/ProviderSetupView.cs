using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>配置自定义 API 端点、模型与密钥。</summary>
public sealed class ProviderSetupView : UserControl
{
    private const double Gutter = 10;

    [Flags]
    private enum Missing
    {
        None = 0,
        ApiKey = 1,
        BaseUrl = 2
    }

    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly ProviderHealth? _health;

    /// <summary>模型规格库(models.dev):按需拉新,缓存在插件私有数据目录。</summary>
    private readonly ModelsDevCatalog _models;

    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly TextBox _search = new() { Name = "SetupSearch" };
    private readonly TextBlock _noMatch;
    private readonly Button _back = new() { Name = "SetupBack", Classes = { "host" } };
    private readonly ScrollViewer _pageScroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    internal event Action? BackRequested;
    internal event Action? Created;

    /// <summary>每行的把手:展开往 <c>Slot</c> 里塞内容,状态变了改 <c>Dot</c>/<c>Pill</c>/行尾按钮。</summary>
    private readonly List<Row> _cards = [];

    private sealed record Row(
        ProviderCatalogEntry Entry, Border Card, Ellipse Dot, TextBlock Pill, StackPanel Slot);

    /// <summary>当前展开的是哪一条(目录 id);null = 全收起。一次只开一个。</summary>
    private string? _openId;
    // 展开后绑定实际显示的实例;设置页可指定旧账号,目录初次展开默认最近一份。
    private string? _focusedProviderId;
    private bool _creatingProvider;
    // 窗口从「新增供应商」入口打开:落库只让当前那一行转入编辑,换行或收起重开仍是新建。
    private bool _newProviderEntry;

    private bool _addingAnother;
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
        _newProviderEntry = _creatingProvider = true;
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

        _search.PlaceholderText = _loc["SetupFindProvider"];
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.PlaceholderText);
        _rows.Children.Add(ProviderProxyLinks.CreateNotice(_loc));
        _noMatch = Hint(_loc["ToolsNoMatch"]);
        _noMatch.Name = "SetupNoMatch";
        _noMatch.IsVisible = false;
        _rows.Children.Add(_noMatch);
        _search.TextChanged += (_, _) => FilterCatalog();

        // 返回按钮不随长目录或配置表单滚动；确认遮罩覆盖整个页面。
        var catalog = new DockPanel();
        _search.Margin = new Thickness(0, 0, Gutter, 10);
        DockPanel.SetDock(_search, Dock.Top);
        _back.Content = _loc["SetupBackSettings"];
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _back.Margin = new Thickness(0, 0, 0, 12);
        _back.Click += (_, _) => _ = BackAsync();
        DockPanel.SetDock(_back, Dock.Top);
        catalog.Children.Add(_back);
        catalog.Children.Add(_search);
        _pageScroller.Content = new Border { Padding = new Thickness(0, 0, Gutter, 0), Child = _rows };
        catalog.Children.Add(_pageScroller);
        Content = new Panel
        {
            Children =
            {
                new Border
                {
                    Padding = new Thickness(20, 16, 20 - Gutter, 16),
                    Child = catalog
                },
                _confirm
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

    /// <summary>关闭表单时取消尚未提交的密钥草稿。</summary>
    public void CancelPendingLogin()
    {
        _keyDraftCancellation?.Cancel();
        _keyDraftCancellation?.Dispose();
        _keyDraftCancellation = null;
    }

    /// <summary>复用目录窗口时回到新建入口,不继续编辑上一次选中的实例。</summary>
    public void StartNewProvider()
    {
        if (_addingAnother || _addingKey || _newProviderDraft is not null)
        {
            _newProviderEntry = true;
            return;
        }
        ResetToNewProvider();
    }

    private void ResetToNewProvider()
    {
        Collapse();
        FocusNavigation();
        _focusedProviderId = null;
        _newProviderEntry = _creatingProvider = true;
    }

    /// <summary>
    /// 展开目录里的某一条并滚到眼前;传 null / 认不出的 id 就什么都不做。
    /// </summary>
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
            string? focused = _settings.Providers.Any(p => p.Id == providerId && p.Auth == AuthMethod.ApiKey && p.CatalogId == catalogId)
                ? providerId : null;
            if (providerId is not null && focused is null) return;
            if (_addingAnother || _addingKey || _newProviderDraft is not null)
            {
                _newProviderEntry = false;
                return;
            }
            // 要管的这一份已经摆着:不重建,改到一半的字段留着
            if (_openId == catalogId && row.Slot.IsVisible && focused is not null && focused == _focusedProviderId)
            {
                row.Card.BringIntoView();
                return;
            }
            _newProviderEntry = _creatingProvider = false;
            _focusedProviderId = focused;
            Expand(row);
            row.Card.BringIntoView();
            return;
        }
    }

    // ---- 行 ----

    private void FilterCatalog()
    {
        string query = _search.Text?.Trim() ?? "";
        bool picking = _openId is null;
        _search.IsVisible = picking;
        _back.Content = _loc[!picking && _newProviderEntry ? "SetupBackCatalog" : "SetupBackSettings"];
        bool anyMatch = false;
        foreach (Row row in _cards)
        {
            bool match = row.Entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Entry.Models.Contains(query, StringComparison.OrdinalIgnoreCase);
            anyMatch |= match;
            row.Card.IsVisible = picking ? match : _openId == row.Entry.Id;
        }
        _noMatch.IsVisible = picking && !anyMatch;
    }

    internal void FocusNavigation() => (_openId is null ? (Control)_search : _back).Focus();

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && !_confirm.IsOpen)
        {
            e.Handled = true;
            _ = BackAsync();
            return;
        }
        base.OnKeyDown(e);
    }

    private async Task BackAsync()
    {
        if (_confirm.IsOpen) return;
        if (_openId is not null)
        {
            // A write may already have committed. Do not offer to discard it during publication.
            if (!_primary.IsEnabled && !_addingKey) return;
            if (_addingKey ? !string.IsNullOrEmpty(_keyBox.Text)
                    : _keyEdited || CaptureFormInputs() != _formInputBaseline)
            {
                if (!await _confirm.AskAsync(_loc["SetupLeaveTitle"], _loc["SetupLeaveBody"],
                        _loc["SetupLeaveAction"], _loc["Cancel"]))
                {
                    return;
                }
            }
            Collapse();
            _focusedProviderId = null;
            if (_newProviderEntry || BackRequested is null)
            {
                FocusNavigation();
                return;
            }
        }
        BackRequested?.Invoke();
    }

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
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { name } };
        var models = new TextBlock
        {
            Text = entry.Models,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        models[!ForegroundProperty] = new DynamicResourceExtension("VelaTextTertiary");
        models[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");

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
        pillText[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
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

        // 点行只负责展开，添加操作在展开区里完成。
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
        header.Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(header, entry.Name);
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) Activate(row);
        };
        header.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            Activate(row);
        };
        _cards.Add(row);
        return card;
    }

    private void Activate(Row row)
    {
        if (_openId == row.Entry.Id)
        {
            _ = BackAsync();
            return;
        }
        _focusedProviderId = null;
        // 落库只把那一行转入编辑;换到别的行(或收起重开)按窗口入口重新决定是否新建
        _creatingProvider = _newProviderEntry;
        Expand(row, startAnother: _creatingProvider);
    }

    /// <summary>已配好 Key 的供应商 id。</summary>
    private readonly HashSet<string> _keyed = [with(StringComparer.Ordinal)];

    private void Collapse()
    {
        CancelPendingLogin();
        _addingAnother = false;
        _addingKey = false;
        _addingKeyProviderId = null;
        _newProviderDraft = null;
        foreach (Row row in _cards)
        {
            row.Slot.Children.Clear();
            row.Slot.IsVisible = false;
        }
        _openId = null;
        FilterCatalog();
    }

    private void Expand(Row row, bool startAnother = false)
    {
        // Collapse 先跑:收旧的行,顺带把上一次的起草态清干净 —— 所以这一轮要不要起草,
        // 由调用方在这之后重新定,别在 Collapse 之前设(设了也会被它擦掉)
        Collapse();
        // 只有真有实例可克隆才算「另一个」;该类型的第一份按首次添加走,不套克隆的签发地址核对
        _addingAnother = startAnother && Existing(row.Entry) is not null;
        _openId = row.Entry.Id;
        FilterCatalog();
        _pageScroller.ScrollToHome();
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
    private sealed record FormInputs(string? Name, string? BaseUrl, string? Model, int Protocol);
    private AiProvider? _newProviderDraft;
    private string? _primaryKeyId;
    private Task<string?> _primaryKeyBaseline = Task.FromResult<string?>(null);

    /// <summary>换源清 Key 的确认框;换了表单就按取消收起(<see cref="BuildDetail" />)。</summary>
    private readonly ConfirmOverlay _confirm = new() { Name = "SetupConfirm", ScrimCornerRadius = new CornerRadius(0, 0, 7, 7) };
    private ComboBox _protocolCombo = new();
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

    /// <summary>协议下拉的标签,顺序 = <see cref="ChatProtocol" /> 枚举。</summary>
    private static readonly string[] ProtocolLabels =
        ["OpenAI Chat Completions", "OpenAI Responses", "Anthropic Messages", "OpenAI Images"];

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
        // 新实例只以首模型作为表单底稿;完整模型清单属于原实例,新凭据必须重新拉取。
        bool adding = existing is not null && _addingAnother;
        AiProvider draft = existing is not null && adding
            ? CloneForAnother(existing)
            : existing ?? entry.CreateProvider();
        _providerFormBaseline = JsonSerializer.Serialize(draft);
        _newProviderDraft = existing is null || adding ? draft : null;
        _primaryKeyId = existing is not null && !adding ? existing.Id : null;
        _primaryKeyBaseline = Task.FromResult<string?>(null);
        _confirm.Dismiss();
        AiModelConfig model = draft.Models.Count > 0 ? draft.Models[0] : new AiModelConfig();
        Missing missing = MissingOf(draft);

        var panel = new StackPanel();
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
                && !_keyPrefilling)
            {
                _keyEdited = true;
            }
        };
        _modelPickerRow = null;
        _modelPicker = null;
        _protocolCombo = new ComboBox
        {
            Name = "SetupProtocolCombo",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ProtocolLabels,
            // 显示首模型的**生效**协议(没有模型级覆盖才是默认值):显示的必须等于实际在用的 ——
            SelectedIndex = (int)(model.Protocol ?? draft.DefaultProtocol)
        };
        _formInputBaseline = CaptureFormInputs();
        BuildApiKeyDetail(panel, entry, missing);
        panel.Children.Add(BuildActions(row, existing, missing, adding));

        if (existing is not null && !adding)
        {
            _primaryKeyBaseline = LoadKeyAsync(existing, _keyBox);
        }
        return panel;
    }

    /// <summary>填 Key 的那一路:只问一把 Key(地址不知道时才多问一句),其余全收进「高级」。</summary>
    private void BuildApiKeyDetail(StackPanel panel, ProviderCatalogEntry entry, Missing missing)
    {
        if (missing.HasFlag(Missing.BaseUrl))
        {
            panel.Children.Add(Label(_loc["BaseUrl"]));
            panel.Children.Add(_baseUrlBox);
        }
        panel.Children.Add(Label(_loc["ApiKey"]));
        panel.Children.Add(KeyRow(_keyBox));
        panel.Children.Add(Hint(_loc["ApiKeyHint"]));
        panel.Children.Add(Advanced(entry));
    }

    /// <summary>
    /// 「高级」:名称 / 模型 id / 基地址 / 协议。默认收起。
    /// </summary>
    /// <remarks>
    /// 支持模型拉取的端点不需要手填模型 id；其他端点可在此直接指定。
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
        if ((_newProviderDraft ?? Existing(entry)) is { } source)
        {
            UpdateModelPicker(source);
            TextBox addressBox = _baseUrlBox;
            addressBox.TextChanged += (_, _) =>
            {
                if (ReferenceEquals(addressBox, _baseUrlBox) && (_newProviderDraft ?? Existing(entry)) is { } current)
                    UpdateModelPicker(current);
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
        body.Children.Add(Label(_loc["DefaultProtocol"]));
        body.Children.Add(_protocolCombo);

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
        _primary = new Button { Name = "SetupPrimaryButton", Height = 26, Padding = new Thickness(14, 0) };
        _primary[!ThemeProperty] = new DynamicResourceExtension("VelaAccentPillButtonTheme");
        _primary.Content = PrimaryLabel(existing, missing, adding);
        _primary.Click += (_, _) => _ = PrimaryAsync(row);

        _another = new Button { Name = "SetupAddAnotherButton", Height = 26, Padding = new Thickness(12, 0) };
        _another[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        _another.Content = _loc["SetupAddApiKey"];
        _another.IsVisible = existing is not null && !adding;
        _another.Click += (_, _) => StartAnother(row);

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
        _secondary.Content = _loc[adding ? "Cancel" : "SetupRemove"];
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
        return new StackPanel { Children = { _conflictValues, line } };
    }

    private string PrimaryLabel(AiProvider? existing, Missing missing, bool adding)
    {
        return adding || existing is null || missing.HasFlag(Missing.ApiKey) ? _loc["SetupAdd"] : _loc["Save"];
    }

    // ---- 缺什么 ----

    /// <summary>这一条离"能用"还差哪几样。</summary>
    private Missing MissingOf(AiProvider provider)
    {
        Missing missing = Missing.None;
        if (IsPlaceholder(provider.BaseUrl))
        {
            missing |= Missing.BaseUrl;
        }
        if (!_keyed.Contains(provider.Id))
        {
            missing |= Missing.ApiKey;
        }
        return missing;
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
        if (!primary.IsEnabled || ReferenceEquals(primary, _removingPrimary))
        {
            return;
        }
        ProviderCatalogEntry entry = row.Entry;
        if (_newProviderDraft is null && _focusedProviderId is not null && Existing(entry) is null)
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
            await AddOrSaveKeyAsync(entry).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_primary, primary))
            {
                progress.Text = _loc["Cancelled"];
            }
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            if (ReferenceEquals(_primary, primary))
                await ReloadConflictedFormAsync(entry, primary).ConfigureAwait(true);
        }
        catch (ArgumentException)
        {
            if (ReferenceEquals(_primary, primary)) progress.Text = _loc["SetupUniqueApiKey"];
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Provider setup for '{entry.Name}' failed: {ex.Message}");
            if (ReferenceEquals(_primary, primary))
            {
                progress.Text = _loc.F("SetupFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
            }
        }
        finally
        {
            if (ReferenceEquals(_primary, primary) && !ReferenceEquals(primary, _removingPrimary))
            {
                _primary.IsEnabled = true;
                _secondary.IsEnabled = true;
                AiProvider? existing = Existing(entry);
                bool adding = _addingAnother || _addingKey || _newProviderDraft is not null;
                _primary.Content = PrimaryLabel(existing,
                    existing is null ? Missing.None : MissingOf(existing), adding);
                _secondary.Content = _loc[adding ? "Cancel" : "SetupRemove"];
                _secondary.IsVisible = existing is not null || adding;
                _another.IsVisible = existing is not null && !adding;
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
                + $"{_loc["Protocol"]}: {provider.Models.FirstOrDefault()?.Protocol ?? provider.DefaultProtocol}"));
            _conflictValues.IsVisible = !string.IsNullOrEmpty(_conflictValues.Text);
            _progress.Text = _focusedProviderId is not null && current is null
                ? _loc["SetupSourceRemoved"] : _loc["SetupConfigChanged"];
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Reloading provider setup failed: {ex.Message}");
            if (ReferenceEquals(_primary, primary))
                _progress.Text = _loc.F("SetupFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
        }
    }
    private FormInputs CaptureFormInputs() => new(_nameBox.Text, _baseUrlBox.Text, _modelBox.Text,
        _protocolCombo.SelectedIndex);

    private static FormInputs ProviderFormInputs(AiProvider provider)
    {
        AiModelConfig? model = provider.Models.Count == 0 ? null : provider.Models[0];
        return new(provider.Name, Placeholder(provider.BaseUrl), model?.Model ?? "",
            (int)(model?.Protocol ?? provider.DefaultProtocol));
    }

    private void FollowUntouchedFields(FormInputs opened, FormInputs latest)
    {
        if (_nameBox.Text == opened.Name) _nameBox.Text = latest.Name;
        if (_baseUrlBox.Text == opened.BaseUrl) _baseUrlBox.Text = latest.BaseUrl;
        if (_modelBox.Text == opened.Model) _modelBox.Text = latest.Model;
        if (_protocolCombo.SelectedIndex == opened.Protocol) _protocolCombo.SelectedIndex = latest.Protocol;
    }

    private void StartAnother(Row row)
    {
        if (_addingKey || _addingAnother || _newProviderDraft is not null || !_another.IsEnabled || ReferenceEquals(_primary, _removingPrimary))
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
        Collapse();
        _addingKey = true;
        _addingKeyProviderId = source.Id;
        _keyDraftCancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        _openId = row.Entry.Id;
        FilterCatalog();
        row.Slot.Children.Add(BuildDetail(row));
        row.Slot.IsVisible = true;
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
    /// 与保存后自动拉取的区别是 <c>force</c> —— 规格缓存还在有效期内也重下。
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
                progress.Text = _loc.F("SetupFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
            }
        }
        finally
        {
            pull.IsEnabled = true;
        }
    }

    private async Task SecondaryAsync(Row row)
    {
        Button secondary = _secondary;
        if (!secondary.IsEnabled) return;
        if (_addingAnother || _addingKey || _newProviderDraft is not null)
        {
            if (_creatingProvider)
            {
                ResetToNewProvider();
                return;
            }
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
        _removingPrimary = primary;
        primary.IsEnabled = another.IsEnabled = false;
        try
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
                progress.Text = _loc.F("SetupFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
        }
        finally
        {
            secondary.IsEnabled = true;
            if (ReferenceEquals(_removingPrimary, primary)) _removingPrimary = null;
            if (ReferenceEquals(_secondary, secondary))
            {
                primary.IsEnabled = another.IsEnabled = true;
            }
        }
    }

    /// <summary>新供应商沿用接入配置与首模型底稿,不复制模型清单或机密。</summary>
    private AiProvider CloneForAnother(AiProvider source)
    {
        return new AiProvider
        {
            Name = NextName(source.Name),
            BaseUrl = source.BaseUrl,
            DefaultProtocol = source.DefaultProtocol,
            Auth = source.Auth,
            CatalogId = source.CatalogId,
            StoreResponses = source.StoreResponses,
            CustomResponsesStore = source.CustomResponsesStore,
            AllowSystemMessages = source.AllowSystemMessages,
            UnsupportedParameters = source.UnsupportedParameters,
            Models = source.Models.Count > 0 ? [CloneModel(source.Models[0])] : []
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
        if (_newProviderDraft is null && _focusedProviderId is not null && existing is null)
        {
            _progress.Text = _loc["SetupSourceRemoved"];
            return;
        }
        TextBox keyBox = _keyBox;
        bool OwnsForm() => _openId == entry.Id && ReferenceEquals(keyBox, _keyBox)
            && !_addingKey && !ReferenceEquals(_primary, _removingPrimary);
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
        IReadOnlyList<string> cleared = [];
        if (!OwnsForm() || existing is not null && !_settings.Providers.Contains(existing)) return;
        if (created)
        {
            await _store.CreateProviderAsync(_settings, draft, apiKey: key,
                cancellationToken: _context.Shutdown).ConfigureAwait(true);
            committedKey = string.IsNullOrWhiteSpace(key) ? null : key;
            if (OwnsForm())
            {
                _creatingProvider = false;
                _addingAnother = false;
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
            // 换源时旧主槽一律清掉,只有空白的那种不算一把 Key、不在清掉的名单里,也照清 —— 基线要看换没换源,不能只看名单
            bool movedOrigin = AiSettingsStore.MovesOrigin(provider.BaseUrl, draft.BaseUrl);
            // 地址换到别的源会清掉已存 Key:先弹确认框列出要清的那几把,点了确认才清。框开着期间要清的有了变动,
            // 存储层核对确认凭据再拒一次,换一句话再问 —— 框里没列的那把不会被一起清掉
            IReadOnlyCollection<string>? confirmed = null;
            while (true)
            {
                try
                {
                    cleared = await _store.SaveProviderApiKeyFormAsync(_settings, provider, draft, expectedProviderJson,
                        replacement, keyId: keyId!, expectedKey: expectedKey,
                        confirmedKeyReset: confirmed, cancellationToken: _context.Shutdown);
                    break;
                }
                catch (AiSettingsStore.ApiKeyResetRequiredException ex)
                {
                    if (!OwnsForm()) return;
                    TextBlock resetProgress = _progress;
                    bool accepted = await KeyResetPrompt.AskAsync(_confirm, provider, ex, confirmed is not null, _loc)
                        .ConfigureAwait(true);
                    if (!OwnsForm()) return;
                    if (!accepted)
                    {
                        resetProgress.Text = _loc["SetupResetCancelled"];
                        return;
                    }
                    confirmed = ex.Confirmation;
                }
            }
            committedKey = replacement ?? (movedOrigin ? null : expectedKey);
            // 框里预填的是旧地址的 Key,已经清掉了,别让它还摆在那儿
            if (committedKey is null && ReferenceEquals(keyBox, _keyBox) && keyBox.Text == key)
            {
                _keyPrefilling = true;
                try { keyBox.Text = ""; }
                finally { _keyPrefilling = false; }
                key = "";
            }
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
        List<string> clearedModels = KeyResetPrompt.ClearedModels(provider, cleared, _loc);
        // 清掉的是供应商自己的 Key 槽而又没填新的;只清了模型自带的 Key 不算(照常拉模型,见下)
        bool keysGone = cleared.Count > clearedModels.Count && committedKey is null;
        if (ReferenceEquals(keyBox, _keyBox))
        {
            _progress.Text = created ? _loc["SetupAdded"]
                : _loc.WithClearedModelKeys(keysGone ? _loc["SetupSavedKeysCleared"]
                    : cleared.Count > clearedModels.Count ? _loc["SetupSavedOtherKeysCleared"]
                    : _loc["Saved"], clearedModels);
        }

        await RefreshStatusAsync().ConfigureAwait(true);
        ProviderChanged?.Invoke(provider.Id);
        if (created && OwnsForm() && !_confirm.IsOpen && CaptureFormInputs() == submittedInputs && !_keyEdited) Created?.Invoke();
        // 「再添加一个」跟着落库就露出 —— 按钮收尾(PrimaryAsync 的 finally)要等下面这次
        // 拉取模型的网络请求回来才跑,慢网下能等到 30 秒,期间想加第二份却没钮可按
        // (review⑨)。此刻必然是"已落库、不在起草态",与收尾那句的条件一致。
        if (ReferenceEquals(keyBox, _keyBox) && !ReferenceEquals(_primary, _removingPrimary))
        {
            _another.IsVisible = true;
            _secondary.IsEnabled = true;
        }
        // Key 刚清空、新地址还没有 Key:拉模型只会换来一次鉴权失败,把「请填入新 Key」的提示盖掉
        if (keysGone) return;
        // 拉取进度会盖掉保存后的提示:点了名要重填 Key 的模型接回结果后面。只接回这张表单自己的进度行 ——
        // 保存与刷新状态期间打开了别的卡片,_progress 已是那张卡片的,不往人家那儿接
        TextBlock? progress = ReferenceEquals(keyBox, _keyBox) ? _progress : null;
        string? before = progress?.Text;
        await PullModelsAsync(entry, provider).ConfigureAwait(true);
        if (clearedModels.Count > 0 && progress is not null && ReferenceEquals(_progress, progress) && progress.Text != before)
            progress.Text = _loc.WithClearedModelKeys(progress.Text ?? "", clearedModels);
    }

    /// <summary>
    /// 接上之后把这一家的模型清单配好:id、上下文窗口、三档单价一次填齐。
    /// </summary>
    /// <remarks>
    /// 清单先问端点自己的 <c>/models</c>(只有它知道这个地址实际供应什么),再拿 id 去
    /// <see cref="ModelsDevCatalog" />(models.dev)配窗口与单价;未匹配时保留接口 context_length。
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
        bool ShowsCandidates() => !_addingKey && !_addingAnother && _openId == entry.Id
            && ReferenceEquals(Existing(entry), provider)
            && string.Equals(requestedBaseUrl, _baseUrlBox.Text?.Trim(), StringComparison.Ordinal);
        bool committingCatalogue = false;
        try
        {
            if (OwnsForm())
            {
                progress.Text = _loc["ModelsPulling"];
            }
            IReadOnlySet<string> referenced = await ModelPull.ReferencedModelIdsAsync(_settings, _context, _context.Shutdown)
                                                             .ConfigureAwait(true);
            ModelPullResult result = await ModelPull
                                           .RunAsync(provider, entry.ModelsDevId, _models, _store, force: force,
                                               materialiseInto: catalogue, referencedModelIds: referenced)
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
            string? openedModel = baseline?.Models.FirstOrDefault()?.Model;
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
            {
                _providerFormBaseline = JsonSerializer.Serialize(baseline);
                FollowPulledModel(openedModel, baseline.Models.FirstOrDefault()?.Model);
            }

            ModelsChanged?.Invoke();
            // 只更新正在编辑的这一实例的候选,不覆写未保存字段或新实例草稿。
            if (ShowsCandidates()) UpdateModelPicker(provider);
            if (OwnsForm() && !_addingAnother)
                progress.Text = _loc.F("ModelsPulled", added) + (result.CatalogueUnavailable ? " " + _loc["ModelsSpecsUnavailable"] : "");
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
    /// <summary>
    /// 拉取把首模型的型号改了(出厂的空型号对齐成清单里的头一个、下架的自动模型被清掉换了首位):
    /// 用户没碰过的型号框跟上,输入基线一并改成新值。
    /// </summary>
    /// <remarks>
    /// 只改提交基线不改框的话,框里还是旧值(自定义接入是空串)、输入基线也还是旧值 —— 表单看着没改过,
    /// 再点一次保存,<see cref="ApplyForm" /> 把框里的旧值写回首模型,刚填进去的型号就没了(#582 复审⑤)。
    /// 框被用户改过就不动它:那是草稿,保存时照用户写的落库(与 <see cref="FollowUntouchedFields" /> 同一条规矩)。
    /// </remarks>
    private void FollowPulledModel(string? opened, string? latest)
    {
        if (latest is null || opened == latest || _formInputBaseline is not { } inputs
            || inputs.Model != opened || _modelBox.Text != opened)
        {
            return;
        }
        _modelBox.Text = latest;
        _formInputBaseline = inputs with { Model = latest };
    }

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
        // 端点已移除的自动模型只在整条配置仍等于请求前快照时删除,保留网络期间的编辑。
        foreach (AiModelConfig original in before.Models)
        {
            if (catalogue.Models.Any(model => model.Id == original.Id)) continue;
            AiModelConfig? current = target.Models.Find(model => model.Id == original.Id);
            string originalJson = JsonSerializer.Serialize(original);
            if (current is null || JsonSerializer.Serialize(current) != originalJson) continue;
            target.Models.Remove(current);
            changedModels?.Add(current.Id);
            AiModelConfig? opened = baseline?.Models.Find(model => model.Id == original.Id);
            if (opened is not null && JsonSerializer.Serialize(opened) == originalJson) baseline!.Models.Remove(opened);
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
                    {
                        baseline.Models.Add(JsonSerializer.Deserialize<AiModelConfig>(json)!);
                    }
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
                {
                    opened.DefaultContextWindow = pulled.DefaultContextWindow;
                }
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
                || current.SupportsReasoning != reasoning)
            {
                changedModels?.Add(current.Id);
            }
        }
    }

    /// <summary>把表单里的值搬进供应商对象。没摆出来的控件带的就是目录出厂值,照读即可。</summary>
    private void ApplyForm(AiProvider provider, ProviderCatalogEntry entry)
    {
        provider.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? entry.Name : _nameBox.Text.Trim();
        provider.BaseUrl = _baseUrlBox.Text?.Trim() ?? "";
        if (_protocolCombo.SelectedIndex >= 0)
        {
            var selected = (ChatProtocol)_protocolCombo.SelectedIndex;
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
        _keyed.Clear();
        foreach (AiProvider provider in _settings.Providers)
        {
            if (provider.Auth != AuthMethod.ApiKey || !_cards.Any(row => row.Entry.Id == provider.CatalogId)) continue;
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
            return (_loc["StatusNotAdded"], "VelaTextMuted");
        }
        return mine.Any(p => _keyed.Contains(p.Id))
            ? (_loc["StatusReady"], "VelaShellGreen")
            : (_loc["StatusNeedsKey"], "VelaWarning");
    }

    /// <summary>设置里同一家的<b>全部</b>实例(目录 id 相同即同一家,允许加多份)。</summary>
    private List<AiProvider> Matching(ProviderCatalogEntry entry)
        => [.. _settings.Providers.Where(p => p.Auth == AuthMethod.ApiKey && p.CatalogId == entry.Id)];

    /// <summary>查找表单绑定的实例;仅尚未绑定的首次目录展开默认最新,失效绑定不回退。</summary>
    private AiProvider? Existing(ProviderCatalogEntry entry)
        => _focusedProviderId is { } id
            ? _settings.Providers.Find(p => p.Id == id && p.Auth == AuthMethod.ApiKey && p.CatalogId == entry.Id)
            : _settings.Providers.FindLast(p => p.Auth == AuthMethod.ApiKey && p.CatalogId == entry.Id);

    // ---- 小零件 ----

    private static TextBlock Label(string text) => new() { Classes = { "label" }, Text = text };

    private static TextBlock Hint(string text) => new() { Classes = { "hint" }, Text = text };

    private static TextBox Mono(TextBox box)
    {
        box[!FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        return box;
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
