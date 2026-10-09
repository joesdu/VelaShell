using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 全局设置窗口:系统提示词、上下文压缩、后续提问、网络检索、故障转移 ——
/// 不归任何一个供应商/模型管的那些。
/// 保存时提交分离的全局草稿,由回调在持久化成功后发布到面板共享设置。
/// </summary>
public partial class GlobalSettingsView : UserControl
{
    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<AiSettings, Task> _persist;
    private readonly Action<bool> _onSettingsReloaded;
    private readonly ProviderHealth? _health;

    /// <summary>故障转移列表的编辑态:构造时拷一份,「保存」才写回设置(与上面那些字段同一口径)。</summary>
    private readonly List<FailoverEntry> _chain = [];

    /// <summary>还能加进列表的模型(已在这条链上的排不出去)。</summary>
    private readonly List<ResolvedModel> _addable = [];

    /// <summary>与 <c>_chain</c> 同序的状态灯与可读文本;刷新不重建行、不改变按钮焦点。</summary>
    private readonly List<(Avalonia.Controls.Shapes.Path Dot, TextBlock Status)> _rowIndicators = [];

    /// <summary>
    /// 状态灯的刷新心跳:冷却状态在<b>窗外</b>也会变(聊天里新进的失败要点亮,
    /// 冷却到期要自己熄),只在重建行时读一次会永远停在旧灯上(review⑩)。
    /// </summary>
    private DispatcherTimer? _lightTimer;

    /// <summary>手动探活的结果(键 = 模型 id):行尾那盏灯的依据,不落盘。</summary>
    private sealed record ProbeResult(bool Ok, DateTime At, string? KeyId, string? ActiveKeyId,
        ProviderCredential? Credential, int Version);
    private readonly Dictionary<string, ProbeResult> _probeResults = new(StringComparer.Ordinal);
    private bool _attached;
    private bool _checkingProbeResults;

    /// <summary>批量探测的取消源:窗口拆掉就作废,别让隐身的请求还在后台跑并改共享健康记录。</summary>
    private CancellationTokenSource? _probeCts;
    private string? _probingModelName;
    private (int Passed, int Failed)? _probeTotals;
    private Dictionary<Control, object?> _loadedForm = [];
    private string[] _loadedChain = [];
    private string[] _displayedChain = [];
    private (string Id, AiProvider Provider, long Version)[] _confirmedProviderRequests = [];

    private void ConfirmProviderRequests() => _confirmedProviderRequests = _settings.Providers
        .Select(provider => (provider.Id, provider, _store.ProviderConfigurationVersion(provider))).ToArray();

    private Dictionary<Control, object?> CaptureForm() => new Control[]
    {
        SystemPromptBox, CompactContextCheck, SuggestFollowUpsCheck, WebEnabledCheck,
        WebSearxUrlBox, WebMaxResultsBox, WebNativeCheck, WebPrivateCheck, WebAllowedHostsBox
    }.ToDictionary(control => control, ReadFormValue);

    private static object? ReadFormValue(Control control) => control switch
    {
        TextBox text => text.Text,
        CheckBox check => check.IsChecked,
        _ => null
    };

    private void ShowSavedForm()
    {
        SystemPromptBox.Text = _settings.SystemPrompt ?? "";
        CompactContextCheck.IsChecked = _settings.CompactContext;
        SuggestFollowUpsCheck.IsChecked = _settings.SuggestFollowUps;
        WebSearchOptions web = _settings.WebSearch;
        WebEnabledCheck.IsChecked = web.Enabled;
        WebSearxUrlBox.Text = web.SearxngBaseUrl;
        WebMaxResultsBox.Text = web.MaxResults.ToString();
        WebNativeCheck.IsChecked = web.PreferProviderNative;
        WebPrivateCheck.IsChecked = web.AllowPrivateNetwork;
        WebAllowedHostsBox.Text = web.AllowedPrivateHosts;
        UpdateWebVisibility();
    }

    private async Task RecoverSettingsConflictAsync()
    {
        try { await RecoverSettingsConflictCoreAsync(); }
        catch (Exception ex)
        {
            _context.Log.Error("Reload AI global settings failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private async Task RecoverSettingsConflictCoreAsync()
    {
        await _store.ReloadIntoAsync(_settings);
        bool requestChanged = _confirmedProviderRequests.Length != _settings.Providers.Count
            || _confirmedProviderRequests.Any(previous =>
                !_settings.Providers.Any(provider => provider.Id == previous.Id
                    && ReferenceEquals(provider, previous.Provider)
                    && _store.ProviderConfigurationVersion(provider) == previous.Version));
        // Reload 等待期间仍允许编辑；重填之前才读取当前草稿。
        Dictionary<Control, object?> dirty = CaptureForm()
            .Where(field => !_loadedForm.TryGetValue(field.Key, out object? loaded) || !Equals(loaded, field.Value))
            .ToDictionary(field => field.Key, field => field.Value);
        bool chainEdited = !_chain.Select(entry => entry.ModelId).SequenceEqual(_loadedChain);
        ShowSavedForm();
        _loadedForm = CaptureForm();
        foreach ((Control control, object? value) in dirty)
        {
            if (control is TextBox text) text.Text = (string?)value;
            else if (control is CheckBox check) check.IsChecked = (bool?)value;
        }
        if (!chainEdited)
        {
            _chain.Clear();
            _chain.AddRange(_settings.FailoverChain.Select(entry => new FailoverEntry { ModelId = entry.ModelId }));
        }
        _loadedChain = _settings.FailoverChain.Select(entry => entry.ModelId).ToArray();
        RefreshFromProviders(requestChanged);
        _onSettingsReloaded(requestChanged);
        StatusText.Text = $"{_loc["Error"]}: {_loc["SetupConfigChanged"]}";
    }

    /// <summary>由聊天面板构造(UI 线程)。</summary>
    public GlobalSettingsView(IPluginContext context, AiSettingsStore store, AiSettings settings, Loc loc,
        Func<AiSettings, Task> persist, Action<bool> onSettingsReloaded, ProviderHealth? health = null)
    {
        _context = context;
        _store = store;
        _settings = settings;
        _loc = loc;
        _persist = persist;
        _onSettingsReloaded = onSettingsReloaded;
        _health = health;
        ConfirmProviderRequests();
        InitializeComponent();
        foreach (FailoverEntry entry in settings.FailoverChain)
        {
            _chain.Add(new FailoverEntry { ModelId = entry.ModelId });
        }
        ApplyLoc();

        ShowSavedForm();
        _loadedForm = CaptureForm();
        _loadedChain = _chain.Select(entry => entry.ModelId).ToArray();

        WebEnabledCheck.IsCheckedChanged += (_, _) => UpdateWebVisibility();
        FailoverAddButton.Click += (_, _) => AddFailover();
        FailoverProbeAllButton.Click += (_, _) => _ = ProbeAllAsync();
        SaveButton.Click += (_, _) => _ = SaveAsync();
    }

    /// <summary>语言切换时由面板调用。</summary>
    public void ApplyLoc()
    {
        SectionGlobalTitle.Text = _loc["SecGlobal"];
        SystemPromptLabel.Text = _loc["SystemPrompt"];
        CompactContextCheck.Content = _loc["CompactContext"];
        CompactContextHintText.Text = _loc["CompactContextHint"];
        SuggestFollowUpsCheck.Content = _loc["SuggestFollowUps"];
        SuggestFollowUpsHintText.Text = _loc["SuggestFollowUpsHint"];

        SectionWebTitle.Text = _loc["SecWebSearch"];
        WebEnabledCheck.Content = _loc["WebEnabled"];
        WebEnabledHintText.Text = _loc["WebEnabledHint"];
        WebSearxUrlLabel.Text = _loc["WebSearxUrl"];
        WebSearxHintText.Text = _loc["WebSearxHint"];
        WebMaxResultsLabel.Text = _loc["WebMaxResults"];
        WebNativeCheck.Content = _loc["WebNative"];
        WebNativeHintText.Text = _loc["WebNativeHint"];
        WebPrivateCheck.Content = _loc["WebPrivate"];
        WebPrivateHintText.Text = _loc["WebPrivateHint"];
        WebAllowedHostsLabel.Text = _loc["WebAllowedHosts"];
        WebAllowedHostsHintText.Text = _loc["WebAllowedHostsHint"];

        SectionFailoverTitle.Text = _loc["SecFailover"];
        FailoverHintText.Text = _loc["FailoverHint"];
        FailoverAddText.Text = _loc["SetupAdd"];
        Avalonia.Automation.AutomationProperties.SetName(FailoverAddCombo, _loc["FailoverAddModel"]);
        FailoverProbeAllText.Text = _loc["FailoverProbeAll"];
        RebuildFailover(); // 行上的按钮提示是本语言的,语言换了要跟着重建
        RefreshProbeStatusText();

        SaveText.Text = _loc["Save"];
    }

    private void RefreshProbeStatusText()
    {
        FailoverStatusText.Text = _probingModelName is { } name
            ? _loc.F("FailoverProbing", name)
            : _probeTotals is { } totals
                ? _loc.F("FailoverProbeDone", totals.Passed, totals.Failed)
                : "";
    }

    private void InvalidateProbeProgress()
    {
        _probeCts?.Cancel();
        _probingModelName = null;
        _probeTotals = null;
        RefreshProbeStatusText();
    }

    private void InvalidateProbeResults(string? modelId = null)
    {
        if (modelId is null) _probeResults.Clear();
        else _probeResults.Remove(modelId);
        InvalidateProbeProgress();
    }

    /// <summary>关掉总闸就把整块细节收起来:那些字段一条都用不上,留着只是噪音。</summary>
    private void UpdateWebVisibility() => WebDetailPanel.IsVisible = WebEnabledCheck.IsChecked == true;

    // ---------- 故障转移 ----------

    /// <summary>
    /// 供应商/模型在外面那两个窗口里改了、或设置页保存了时,由面板调用:
    /// 「可添加」下拉与行上的名字跟上(新模型排得出去、删掉的模型加不进来),
    /// <see cref="_chain" /> 的排序原样保留 —— 那是用户正在排的链。
    /// </summary>
    /// <remarks>
    /// 请求参数真正变化时，两件旧证据在这里作废:探测灯描述的是<b>改之前</b>那份配置，
    /// 在途的「全部检测」也必须掐掉，避免迟到结果把新配置标红。
    /// 只重载全局字段或显示名称时仍刷新列表，但保留有效证据。
    /// 共享健康表的冷却由面板在 <c>OnProvidersChanged</c> 里清(那条路不依赖本窗口开没开)。
    /// 链里已被删掉的模型也一并剔除:行都没了,留着只是个点不动的死 id。
    /// </remarks>
    public void RefreshFromProviders(bool requestChanged = true)
    {
        if (requestChanged)
        {
            InvalidateProbeResults();
            ConfirmProviderRequests();
        }
        _chain.RemoveAll(e => _settings.FindModel(e.ModelId) is null);
        // 自动剔除死 ID 不是用户编辑；只同步这些删除，不吸收其它窗口的新链。
        _loadedChain = _loadedChain.Where(id => _settings.FindModel(id) is not null).ToArray();
        RebuildFailover();
    }

    /// <summary>重建故障转移的整块:行列表 + 可添加下拉。增删换位后调用。</summary>
    private void RebuildFailover()
    {
        if (!_chain.Select(entry => entry.ModelId).SequenceEqual(_displayedChain))
        {
            // 请求配置未变也不能让旧链的批次继续探已移出的模型；同链显示刷新不打断。
            InvalidateProbeProgress();
            foreach (string id in _probeResults.Keys.Where(id => _chain.All(entry => entry.ModelId != id)).ToArray())
                _probeResults.Remove(id);
            _displayedChain = _chain.Select(entry => entry.ModelId).ToArray();
        }
        RebuildFailoverRows();
        bool prefix = _settings.Providers.Count > 1;
        // 重建前记住选中的那条:换语言、外部增删都会走到这里,直接拨回 0 的话,
        // 用户刚在下拉里挑好的那条,点「添加」时会变成列表里的另一条(review)
        string? keepId = FailoverAddCombo.SelectedIndex is int picked && picked >= 0 && picked < _addable.Count
            ? _addable[picked].Id
            : null;
        _addable.Clear();
        foreach (ResolvedModel model in _settings.ResolveModels())
        {
            if (_chain.All(e => e.ModelId != model.Id))
            {
                _addable.Add(model);
            }
        }
        FailoverAddCombo.ItemsSource = _addable
            .Select(m => prefix && !string.IsNullOrWhiteSpace(m.ProviderName) ? $"{m.ProviderName} · {m.Name}" : m.Name)
            .ToList();
        int restore = keepId is null ? -1 : _addable.FindIndex(m => m.Id == keepId);
        FailoverAddCombo.SelectedIndex = restore >= 0 ? restore : _addable.Count > 0 ? 0 : -1;
        FailoverAddButton.IsEnabled = _addable.Count > 0;
        FailoverProbeAllButton.IsEnabled = HasProbeTargets() && _probeCts is null;
        // 一个都不剩时把话说清楚,而不是留一个空下拉让人猜
        FailoverAddHintText.IsVisible = _addable.Count == 0;
        FailoverAddHintText.Text = _loc["FailoverEmpty"];
    }

    private bool HasProbeTargets()
    {
        foreach (FailoverEntry entry in _chain)
        {
            if (_settings.FindModel(entry.ModelId) is not null)
            {
                return true;
            }
        }
        return false;
    }

    private void RebuildFailoverRows()
    {
        string? focusedModelId = null;
        string? focusedAction = null;
        int focusedIndex = -1;
        for (int i = 0; i < FailoverRowsHost.Children.Count; i++)
        {
            if (FailoverRowsHost.Children[i] is not Border { Child: Grid previous }) continue;
            foreach (Control child in previous.Children)
            {
                if (child is not Button { IsFocused: true } button) continue;
                focusedModelId = (string?)((Border)FailoverRowsHost.Children[i]).Tag;
                focusedAction = button.Name;
                focusedIndex = i;
                break;
            }
            if (focusedIndex >= 0) break;
        }

        FailoverRowsHost.Children.Clear();
        _rowIndicators.Clear();
        for (int i = 0; i < _chain.Count; i++)
        {
            FailoverRowsHost.Children.Add(BuildFailoverRow(i));
        }
        if (focusedIndex < 0) return;
        int target = _chain.FindIndex(e => e.ModelId == focusedModelId);
        if (target < 0) target = Math.Min(focusedIndex, _chain.Count - 1);
        if (target < 0 || FailoverRowsHost.Children[target] is not Border { Child: Grid current })
        {
            SaveButton.Focus();
            return;
        }
        Button? fallback = null;
        foreach (Control child in current.Children)
        {
            if (child is not Button button) continue;
            if (button.Name == "FailoverRemoveButton") fallback = button;
            if (button.Name != focusedAction || !button.IsEnabled) continue;
            button.Focus();
            return;
        }
        fallback?.Focus();
    }

    /// <summary>
    /// 一行:序号 + 名字 + 状态灯 + 上移/下移/移出。
    /// 整列重建而不是就地改:序号、禁用态、按钮闭包里的下标都得跟着变,逐个改更容易漏。
    /// </summary>
    private Border BuildFailoverRow(int index)
    {
        FailoverEntry entry = _chain[index];
        ResolvedModel? model = _settings.FindModel(entry.ModelId);
        bool prefix = _settings.Providers.Count > 1;
        string name = model is null
            ? entry.ModelId
            : prefix && !string.IsNullOrWhiteSpace(model.ProviderName)
                ? $"{model.ProviderName} · {model.Name}"
                : model.Name;

        var number = new TextBlock
        {
            Classes = { "dim" },
            Text = (index + 1).ToString(),
            Width = 18,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var label = new TextBlock
        {
            Name = "FailoverRowLabel",
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        if (model is null)
        {
            // 模型已经删了:灰着显示原始 id,保存时这条会被 FailoverCandidates 自然淘汰
            label.Classes.Add("dim");
        }

        var status = new TextBlock
        {
            Name = "FailoverRowStatus",
            Classes = { "dim" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = false
        };
        var description = new StackPanel { Children = { label, status } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto") };
        grid.Children.Add(number);
        Grid.SetColumn(description, 1);
        grid.Children.Add(description);

        var dot = BuildProbeDot(model, status);
        _rowIndicators.Add((dot, status));
        Grid.SetColumn(dot, 2);
        grid.Children.Add(dot);
        Button up = RowIconButton("FailoverUpButton", "AiIcon.chevron-up", _loc["FailoverUp"],
            (_, _) => MoveFailover(index, -1));
        up.IsEnabled = index > 0;
        Grid.SetColumn(up, 3);
        grid.Children.Add(up);

        Button down = RowIconButton("FailoverDownButton", "AiIcon.chevron-down", _loc["FailoverDown"],
            (_, _) => MoveFailover(index, 1));
        down.IsEnabled = index < _chain.Count - 1;
        Grid.SetColumn(down, 4);
        grid.Children.Add(down);

        Button remove = RowIconButton("FailoverRemoveButton", "AiIcon.trash-2", _loc["FailoverRemove"],
            (_, _) =>
            {
                InvalidateProbeProgress();
                _chain.RemoveAt(index);
                RebuildFailover();
            });
        Grid.SetColumn(remove, 5);
        grid.Children.Add(remove);

        return new Border { Name = "FailoverRow", Tag = entry.ModelId, Padding = new Thickness(4, 2), Child = grid };
    }

    /// <summary>行尾的状态灯:手动测过就看测的结果;没测过但正冷却着也点个红点。</summary>
    private Avalonia.Controls.Shapes.Path BuildProbeDot(ResolvedModel? model, TextBlock status)
    {
        var dot = new Avalonia.Controls.Shapes.Path
        {
            Name = "FailoverRowDot",
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0), // 描边越出Path边界时也不能被相邻按钮遮挡。
            IsVisible = false
        };
        dot[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("AiIcon.circle-check");
        RefreshProbeDot((dot, status), model);
        return dot;
    }

    /// <summary>
    /// 按<b>此刻</b>的状态重画一盏灯:探测结果优先,没测过但正冷却就点红。
    /// 与建灯拆开是为了让心跳(见 <see cref="_lightTimer" />)能逐盏刷新 ——
    /// 整列重建会把按钮闭包一起换掉,一秒一次地打断用户点行内按钮。
    /// </summary>
    private void RefreshProbeDot((Avalonia.Controls.Shapes.Path Dot, TextBlock Status) indicator, ResolvedModel? model)
    {
        var (dot, status) = indicator;
        bool? passed = null;
        string? tip = null;
        if (model is not null)
        {
            if (_probeResults.TryGetValue(model.Id, out ProbeResult? stale)
                && (stale.Version != (_health?.Version ?? 0)
                    || stale.ActiveKeyId != model.Provider.ActiveApiKeyId))
                InvalidateProbeResults(model.Id);
            if (_probeResults.TryGetValue(model.Id, out ProbeResult? result))
            {
                passed = result.Ok;
                tip = $"{_loc[result.Ok ? "DotPassed" : "DotFailed"]} · {result.At:HH:mm}";
            }
            else if (_health is { } health && health.IsCooling(model.Id))
            {
                passed = false;
                tip = _loc["DotCooling"];
            }
        }
        status.Text = tip;
        status.IsVisible = passed.HasValue;
        if (passed is { } ok)
        {
            dot.IsVisible = true;
            dot[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(
                ok ? "AiIcon.circle-check" : "AiIcon.circle-x");
            dot[!Avalonia.Controls.Shapes.Path.StrokeProperty] = new DynamicResourceExtension(
                ok ? "VelaStatusConnected" : "VelaError");
            ToolTip.SetTip(dot, tip);
        }
        else
        {
            dot.IsVisible = false;
            ToolTip.SetTip(dot, null);
        }
    }

    /// <summary>行尾的小图标按钮:图标使用插件资源,操作名称与提示使用同一份本地化文案。</summary>
    private static Button RowIconButton(string name, string iconKey, string tip, EventHandler<RoutedEventArgs> onClick)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 11,
            Height = 11,
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        icon[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(iconKey);
        icon[!Avalonia.Controls.Shapes.Path.StrokeProperty] = new DynamicResourceExtension("VelaTextSecondary");
        var button = new Button
        {
            Name = name,
            Classes = { "host" },
            Content = icon,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += onClick;
        return button;
    }

    private void AddFailover()
    {
        int selected = FailoverAddCombo.SelectedIndex;
        if (selected < 0 || selected >= _addable.Count)
        {
            return;
        }
        InvalidateProbeProgress();
        _chain.Add(new FailoverEntry { ModelId = _addable[selected].Id });
        RebuildFailover();
    }

    /// <summary>上移/下移一位:整列重建(见 <see cref="BuildFailoverRow" />)。</summary>
    private void MoveFailover(int index, int delta)
    {
        int target = index + delta;
        if (target < 0 || target >= _chain.Count)
        {
            return;
        }
        InvalidateProbeProgress();
        FailoverEntry entry = _chain[index];
        _chain.RemoveAt(index);
        _chain.Insert(target, entry);
        RebuildFailover();
    }

    /// <summary>
    /// 逐行探活(极小真请求),结果点进灯里并顺手喂给共享的健康记录。
    /// 窗口关掉就整体作废(<see cref="_probeCts" />):循环是逐条真请求,
    /// 不掐的话关窗后还会隐身地再跑十几秒,重开窗口更会叠出两轮。
    /// </summary>
    private async Task ProbeAllAsync()
    {
        if (!HasProbeTargets())
        {
            return;
        }
        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        CancellationToken token = cts.Token;
        FailoverProbeAllButton.IsEnabled = false;
        int passed = 0, failed = 0;
        _probeTotals = null;
        try
        {
            foreach (FailoverEntry entry in _chain.ToList())
            {
                token.ThrowIfCancellationRequested();
                ResolvedModel? model = _settings.FindModel(entry.ModelId);
                if (model is null)
                {
                    continue;
                }
                _probeResults.Remove(model.Id);
                int probingRow = _chain.FindIndex(e => e.ModelId == model.Id);
                if (probingRow >= 0) RefreshProbeDot(_rowIndicators[probingRow], model);
                _probingModelName = model.Name;
                RefreshProbeStatusText();
                int healthEvidence = _health?.Version ?? 0;
                long observationId = _health?.BeginObservation() ?? 0;
                string modelName = model.Model;
                string endpoint = model.BaseUrl;
                ChatProtocol protocol = model.Protocol;
                string ownerId = model.ApiKeyOwnerId;
                string? activeKeyId = model.Provider.ActiveApiKeyId;
                (ProviderCredential Credential, string? KeyId)? selected = null;
                Exception? error;
                try
                {
                    selected = await _store.ResolveCredentialWithKeyIdAsync(model, token);
                    (error, _) = await HealthProbe.ProbeAsync(_store, model, selected.Value.Credential, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = ex; }
                // 取消可能正好落在"探活返回"与"记账"之间(配置刚变过,见 RefreshFromProviders):
                // 这条旧结果描述的是改之前的配置,一个字都不能写 —— 标红和冷却都算
                token.ThrowIfCancellationRequested();
                ResolvedModel? fresh = _settings.FindModel(model.Id);
                if (fresh is null || fresh.Model != modelName || fresh.BaseUrl != endpoint
                    || fresh.Protocol != protocol || fresh.ApiKeyOwnerId != ownerId
                    || fresh.Provider.ActiveApiKeyId != activeKeyId || (_health?.Version ?? 0) != healthEvidence)
                    continue;
                if (selected is { } sent)
                {
                    try
                    {
                        var current = await _store.ResolveCredentialWithKeyIdAsync(fresh, token, refreshTokens: false);
                        token.ThrowIfCancellationRequested();
                        if (current.KeyId != sent.KeyId
                            || !string.Equals(current.Credential.Value, sent.Credential.Value, StringComparison.Ordinal)
                            || current.Credential.IsBearerToken != sent.Credential.IsBearerToken
                            || current.Credential.BaseUrl != sent.Credential.BaseUrl
                            || !(current.Credential.Headers ?? []).SequenceEqual(sent.Credential.Headers ?? [])
                            || _settings.FindModel(model.Id) is not { } verified || verified.Model != modelName
                            || verified.BaseUrl != endpoint || verified.Protocol != protocol || verified.ApiKeyOwnerId != ownerId
                            || verified.Provider.ActiveApiKeyId != activeKeyId || (_health?.Version ?? 0) != healthEvidence)
                            continue; // active 槽或 Key 已变:旧行灯和旧模型健康都丢弃。
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
                }
                bool ok = error is null;
                _probeResults[model.Id] = new ProbeResult(ok, DateTime.Now, selected?.KeyId,
                    activeKeyId, selected?.Credential, healthEvidence);
                if (selected is { } observed)
                {
                    if (observed.KeyId is { } keyId)
                    {
                        if (ok || TransientFailure.IsApiKeyFailure(error))
                            _health?.RecordKey(keyId, ok, healthEvidence, observationId);
                        if (ok || !TransientFailure.IsApiKeyFailure(error))
                            _health?.Record(model.Id, ok, healthEvidence, observationId);
                    }
                    else _health?.Record(model.Id, ok, healthEvidence, observationId);
                }
                if (ok)
                {
                    passed++;
                }
                else
                {
                    failed++;
                }
                int rowIndex = _chain.FindIndex(e => e.ModelId == model.Id);
                if (rowIndex >= 0)
                {
                    RefreshProbeDot(_rowIndicators[rowIndex], model); // 只刷新状态,不拆正在操作的按钮
                }
            }
            _probingModelName = null;
            _probeTotals = (passed, failed);
            RefreshProbeStatusText();
        }
        catch (OperationCanceledException)
        {
            // 窗口关了:结果不再有人看,这轮没测完也不留话(状态行随窗消失)。
            // 用户主动取消照 HealthProbe 的约定原样到这儿,同样不算"端点死了"。
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts))
            {
                _probeCts = null;
                FailoverProbeAllButton.IsEnabled = HasProbeTargets();
            }
            cts.Dispose();
        }
    }

    /// <summary>挂上视觉树:点起状态灯心跳(拆窗时停,见 <see cref="OnDetachedFromVisualTree" />)。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _lightTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lightTimer.Tick -= OnLightTick;
        _lightTimer.Tick += OnLightTick;
        _lightTimer.Start();
    }

    /// <summary>心跳:逐盏重画状态灯 —— 聊天中新进的失败要亮,冷却到期要自己熄(整列不重建)。</summary>
    private void OnLightTick(object? sender, EventArgs e)
    {
        _ = RevalidateProbeResultsAsync();
        int count = Math.Min(_rowIndicators.Count, _chain.Count);
        for (int i = 0; i < count; i++)
        {
            RefreshProbeDot(_rowIndicators[i], _settings.FindModel(_chain[i].ModelId));
        }
    }

    private async Task RevalidateProbeResultsAsync()
    {
        if (_checkingProbeResults || !_attached) return;
        _checkingProbeResults = true;
        try
        {
            foreach ((string id, ProbeResult result) in _probeResults.ToArray())
            {
                ResolvedModel? model = _settings.FindModel(id);
                bool matches = model is not null && result.Version == (_health?.Version ?? 0)
                    && result.ActiveKeyId == model.Provider.ActiveApiKeyId;
                if (matches && result.Credential is { } sent)
                {
                    try
                    {
                        var current = await _store.ResolveCredentialWithKeyIdAsync(model!, refreshTokens: false);
                        matches = current.KeyId == result.KeyId && current.Credential.Value == sent.Value
                            && current.Credential.IsBearerToken == sent.IsBearerToken
                            && current.Credential.BaseUrl == sent.BaseUrl
                            && (current.Credential.Headers ?? []).SequenceEqual(sent.Headers ?? [])
                            && _settings.FindModel(id) is { } verified
                            && verified.Provider.ActiveApiKeyId == result.ActiveKeyId
                            && result.Version == (_health?.Version ?? 0);
                    }
                    catch (Exception) { matches = false; }
                }
                if (!_attached) return;
                if (!matches && _probeResults.TryGetValue(id, out ProbeResult? latest) && ReferenceEquals(latest, result))
                {
                    InvalidateProbeResults(id);
                    int index = _chain.FindIndex(entry => entry.ModelId == id);
                    if (index >= 0) RefreshProbeDot(_rowIndicators[index], _settings.FindModel(id));
                }
            }
        }
        finally { _checkingProbeResults = false; }
    }

    /// <summary>窗口拆掉时把在途的批量探测掐掉(在途的那次请求由链接令牌一并取消),心跳也停。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_lightTimer is not null)
        {
            _lightTimer.Stop();
            _lightTimer.Tick -= OnLightTick;
        }
        _probingModelName = null;
        _probeTotals = null;
        RefreshProbeStatusText();
        _probeCts?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task SaveAsync()
    {
        if (!SaveButton.IsEnabled) return;
        SaveButton.IsEnabled = false;
        StatusText.Text = "";
        try
        {
            var draft = new AiSettings
            {
                SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text,
                CompactContext = CompactContextCheck.IsChecked == true,
                SuggestFollowUps = SuggestFollowUpsCheck.IsChecked == true,
                WebSearch = new WebSearchOptions
                {
                    Enabled = WebEnabledCheck.IsChecked == true,
                    SearxngBaseUrl = WebSearxUrlBox.Text?.Trim() ?? "",
                    PreferProviderNative = WebNativeCheck.IsChecked == true,
                    AllowPrivateNetwork = WebPrivateCheck.IsChecked == true,
                    AllowedPrivateHosts = WebAllowedHostsBox.Text ?? "",
                    // 非数字保留已有值,而不是把用户已有的值清成 0。
                    MaxResults = int.TryParse(WebMaxResultsBox.Text, out int count) ? count : _settings.WebSearch.MaxResults,
                    MaxFetchChars = _settings.WebSearch.MaxFetchChars
                },
                FailoverChain = [.. _chain.Select(entry => new FailoverEntry { ModelId = entry.ModelId })]
            };
            draft.WebSearch.Clamp();
            WebMaxResultsBox.Text = draft.WebSearch.MaxResults.ToString();
            // 只把本次提交的 UI 快照记为基线;等待落盘期间仍允许用户继续编辑下一份草稿。
            Dictionary<Control, object?> submittedForm = CaptureForm();
            string[] submittedChain = draft.FailoverChain.Select(entry => entry.ModelId).ToArray();
            // 回调以分离快照落盘且仅发布这些全局字段;失败时共享设置和本页编辑态都保持原样。
            await _persist(draft);
            _loadedForm = submittedForm;
            _loadedChain = submittedChain;
            StatusText.Text = submittedForm.All(field => Equals(field.Value, ReadFormValue(field.Key)))
                && _chain.Select(entry => entry.ModelId).SequenceEqual(submittedChain) ? _loc["Saved"] : "";
        }
        catch (AiSettingsStore.ApiKeySlotChangedException)
        {
            await RecoverSettingsConflictAsync();
        }
        catch (Exception ex)
        {
            _context.Log.Error("Save AI global settings failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
        finally { SaveButton.IsEnabled = true; }
    }
}
