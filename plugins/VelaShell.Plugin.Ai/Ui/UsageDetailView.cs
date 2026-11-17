using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>用量面板要画的数字(一次快照)。</summary>
/// <param name="Streaming">回复还在生成:上下文是估算值。</param>
/// <param name="ContextTokens">上一轮请求(或在途估算)的输入大小。</param>
/// <param name="Window">上下文窗口;0 = 不知道。</param>
/// <param name="WindowWarning">窗口大小没核实时的提示;没有为空串。</param>
/// <param name="LastInput">上一轮请求的输入(含缓存读写)。</param>
/// <param name="LastCacheRead">其中命中缓存读取的部分。</param>
/// <param name="LastCacheWrite">其中写入缓存的部分。</param>
/// <param name="TotalInput">会话累计输入。</param>
/// <param name="TotalOutput">会话累计输出。</param>
/// <param name="TotalReasoning">会话累计思考 token。</param>
/// <param name="TotalCacheRead">会话累计缓存读取。</param>
/// <param name="TotalCacheWrite">会话累计缓存写入。</param>
/// <param name="Dropped">为放进窗口移出上下文的消息数。</param>
/// <param name="Cost">预计花费;没填单价为 null。</param>
/// <param name="MaxOutput">最大输出 token;不知道模型为 0。</param>
/// <param name="ReportsCacheWrites">这条接入的协议会不会报缓存写入(Anthropic 一定报;OpenAI 系只有部分中转站报)。</param>
/// <param name="Breakdown">最近一次请求的上下文拆分;还没有为 null。</param>
/// <param name="CompactAt">自动压缩线(token);0 = 不会自动压缩。</param>
/// <param name="CompactionEnabled">「压缩上下文」开关。关着时超窗只能丢最早的消息。</param>
/// <param name="Model">模型名(明细标题用)。</param>
internal sealed record UsageFigures(
    bool Streaming, long ContextTokens, int Window, string WindowWarning,
    long LastInput, long LastCacheRead, long LastCacheWrite,
    long TotalInput, long TotalOutput, long TotalReasoning, long TotalCacheRead, long TotalCacheWrite,
    int Dropped, double? Cost, int MaxOutput, bool ReportsCacheWrites,
    ContextBreakdown? Breakdown = null, int CompactAt = 0, bool CompactionEnabled = true, string Model = "");

/// <summary>
/// 输入框右下角用量的悬停面板:总量 + 按来源分段的条(带自动压缩刻度)+ 各类一行,
/// 下面是提示缓存与本会话累计。点击状态行打开的 <see cref="ContextDetailView" /> 给出逐项明细。
/// </summary>
/// <remarks>
/// <para>
/// 以前这里只说「占了多少」,不说「被谁占了」:系统提示词与工具定义开场就吃掉十几到二十几 k,
/// 悬停里看不出来;条的变色阈值(80% / 95%)也和真正触发压缩的那条线(窗口减余量的 75%)对不上。
/// 现在条按来源分段,压缩线画成刻度,离它多远在说明里直接写出来。
/// </para>
/// <para>
/// 弹层不在面板的样式作用域里,所以字号、颜色都直接绑令牌,不靠 <c>dim</c> 之类的类名。
/// <see cref="ToString" /> 返回纯文本明细(读屏与测试用)。
/// </para>
/// </remarks>
internal sealed class UsageDetailView : StackPanel
{
    private const double BarHeight = 8;
    private readonly Loc _loc;

    // 上下文
    private readonly Panel _badgeHost = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _usedValue = UsageVisuals.Text(size: "VelaFontSize16", bold: true);
    private readonly TextBlock _windowValue = UsageVisuals.Text(tone: "VelaTextTertiary");
    private readonly Panel _barHost = new();
    private readonly TextBlock _contextCaption = UsageVisuals.Text(tone: "VelaTextTertiary", size: "VelaFontSize11", wrap: true);
    private readonly TextBlock _windowWarning = UsageVisuals.Text(tone: "VelaWarning", size: "VelaFontSize11", wrap: true);
    private readonly Grid _slices = new() { ColumnDefinitions = [with("Auto,*,64,Auto")], ColumnSpacing = 8, RowSpacing = 4 };

    // 缓存
    private readonly StackPanel _cacheSection = new() { Spacing = 4 };
    private readonly TextBlock _cacheValue = UsageVisuals.Text(bold: true);
    private readonly Panel _cacheBarHost = new();
    private readonly WrapPanel _cacheLegend = new();
    private readonly TextBlock _cacheCaption = UsageVisuals.Text(tone: "VelaTextTertiary", size: "VelaFontSize11", wrap: true);

    // 本会话
    private readonly Grid _session = new() { ColumnDefinitions = [with("*,*,*,*")], ColumnSpacing = 8 };
    private readonly TextBlock _footer = UsageVisuals.Text(tone: "VelaTextMuted", size: "VelaFontSize11", wrap: true);

    private string _plain = "";

    public UsageDetailView(Loc loc)
    {
        _loc = loc;
        Spacing = 8;
        Width = 300;

        var header = new Grid { ColumnDefinitions = [with("*,Auto")] };
        header.Children.Add(UsageVisuals.Text(_loc["UsageSectionContext"], bold: true));
        Grid.SetColumn(_badgeHost, 1);
        header.Children.Add(_badgeHost);
        Children.Add(header);

        _windowValue.Margin = new Thickness(6, 0, 0, 1);
        _windowValue.VerticalAlignment = VerticalAlignment.Bottom;
        _usedValue.VerticalAlignment = VerticalAlignment.Bottom;
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _usedValue, _windowValue } });
        Children.Add(_barHost);
        Children.Add(_contextCaption);
        Children.Add(_windowWarning);
        Children.Add(_slices);

        Children.Add(UsageVisuals.Rule());

        var cacheHeading = new Grid { ColumnDefinitions = [with("*,Auto")] };
        cacheHeading.Children.Add(UsageVisuals.Text(_loc["UsageSectionCache"], "VelaTextSecondary"));
        Grid.SetColumn(_cacheValue, 1);
        cacheHeading.Children.Add(_cacheValue);
        _cacheSection.Children.Add(cacheHeading);
        _cacheSection.Children.Add(_cacheBarHost);
        _cacheSection.Children.Add(_cacheLegend);
        _cacheSection.Children.Add(_cacheCaption);
        Children.Add(_cacheSection);

        Children.Add(_session);
        Children.Add(_footer);
    }

    /// <summary>纯文本明细(读屏、测试与复制用)。</summary>
    public override string ToString() => _plain;

    public void Update(UsageFigures f, string plainText)
    {
        _plain = plainText;
        var u = new ContextUsage(f);
        string e = u.Estimate;

        // ── 上下文:总量、分段条、离压缩线多远 ──
        _badgeHost.Children.Clear();
        if (u.Window > 0) _badgeHost.Children.Add(UsageVisuals.Badge($"{e}{u.Percent}%", u.NearCompaction));
        _usedValue.Text = u.Used > 0 ? e + ChatFormatting.Compact(u.Used) : "—";
        _windowValue.Text = u.Window > 0 ? "/ " + ChatFormatting.Compact(u.Window) : "";
        ToolTip.SetTip(_usedValue, u.Window > 0 ? $"{u.Used:N0} / {u.Window:N0}" : $"{u.Used:N0}");

        _barHost.Children.Clear();
        _barHost.IsVisible = u.Window > 0 || u.Slices.Count > 0;
        if (_barHost.IsVisible)
            _barHost.Children.Add(UsageVisuals.Bar(u.BarSegments(), BarHeight,
                u.CompactAt > 0 ? u.CompactAt / (double)u.Window : null));

        _contextCaption.Text = ContextCaption(u, _loc);
        _contextCaption[!TextBlock.ForegroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(u.NearCompaction ? "VelaWarning" : "VelaTextTertiary");
        _windowWarning.Text = f.WindowWarning;
        _windowWarning.IsVisible = f.WindowWarning.Length > 0;

        // ── 各类一行:色块、名字、相对最大一类的小条、数 ──
        _slices.Children.Clear();
        _slices.RowDefinitions.Clear();
        if (u.Slices.Count > 0)
        {
            long max = u.Slices.Max(s => s.Tokens);
            foreach ((ContextSlice slice, long tokens) in u.Slices)
            {
                int row = _slices.RowDefinitions.Count;
                _slices.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                string tone = UsageVisuals.ToneOf(slice);
                Place(UsageVisuals.Swatch(tone), row, 0);
                Place(UsageVisuals.Text(_loc[UsageVisuals.NameKey(slice)], "VelaTextSecondary", "VelaFontSize11"), row, 1);
                Control mini = UsageVisuals.Bar([new(tokens / (double)Math.Max(1, max), tone)], 4);
                mini.VerticalAlignment = VerticalAlignment.Center;
                Place(mini, row, 2);
                TextBlock value = UsageVisuals.Number(e + ChatFormatting.Compact(tokens));
                ToolTip.SetTip(value, $"{tokens:N0}");
                Place(value, row, 3);
            }
        }
        else if (u.Used > 0)
        {
            _slices.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock pending = UsageVisuals.Text(_loc["ContextNoBreakdown"], "VelaTextTertiary", "VelaFontSize11", wrap: true);
            Grid.SetColumnSpan(pending, 4);
            _slices.Children.Add(pending);
        }
        _slices.IsVisible = _slices.Children.Count > 0;

        // ── 提示缓存:上一轮的输入拆三截 ──
        bool anyCache = f.TotalCacheRead > 0 || f.TotalCacheWrite > 0;
        _cacheSection.IsVisible = !f.Streaming && f.LastInput > 0;
        if (_cacheSection.IsVisible)
        {
            long read = Math.Clamp(f.LastCacheRead, 0, f.LastInput);
            long written = Math.Clamp(f.LastCacheWrite, 0, f.LastInput - read);
            long missed = f.LastInput - read - written;
            int hit = (int)Math.Round(read * 100.0 / f.LastInput);
            _cacheValue.Text = _loc.F("UsageCacheHitValue", hit);
            _cacheBarHost.Children.Clear();
            _cacheBarHost.Children.Add(UsageVisuals.Bar(
            [
                new(read / (double)f.LastInput, "VelaStatusConnected"),
                new(written / (double)f.LastInput, "VelaWarning")
            ], 4));
            _cacheLegend.Children.Clear();
            _cacheLegend.Children.Add(LegendItem("VelaStatusConnected", _loc["UsageCacheRead"], read));
            _cacheLegend.Children.Add(LegendItem("VelaWarning", _loc["UsageCacheWrite"], written));
            _cacheLegend.Children.Add(LegendItem(UsageVisuals.FreeTone, _loc["UsageCacheMiss"], missed));
            string caption = anyCache
                ? _loc.F("UsageCacheTotalsLine", $"{f.TotalCacheRead:N0}", $"{f.TotalCacheWrite:N0}")
                : _loc["UsageCacheNone"];
            // 写入一直是 0 而协议本来就不一定报:说清楚是「没报」,不是「没写」
            if (f.TotalCacheWrite == 0 && !f.ReportsCacheWrites) caption += "\n" + _loc["UsageCacheWriteUnreported"];
            _cacheCaption.Text = caption;
        }

        // ── 本会话累计 ──
        _session.Children.Clear();
        AddStat(_session, 0, _loc["UsageInput"], e + ChatFormatting.Compact(f.TotalInput), $"{f.TotalInput:N0}");
        AddStat(_session, 1, _loc["UsageOutput"], e + ChatFormatting.Compact(f.TotalOutput), $"{f.TotalOutput:N0}");
        if (f.TotalReasoning > 0)
            AddStat(_session, 2, _loc["UsageReasoning"], ChatFormatting.Compact(f.TotalReasoning), $"{f.TotalReasoning:N0}");
        if (f.Cost is { } cost)
            AddStat(_session, 3, _loc["UsageCostLabel"], ChatPanelView.FormatCost(cost), _loc["UsageCostNote"]);

        _footer.Text = (f.Streaming ? _loc["UsageStreamingEstimate"] + "\n" : "")
                       + (u.Slices.Count > 0 ? _loc["ContextEstimateNote"] + " · " : "")
                       + _loc["UsageClickForDetail"];
    }

    /// <summary>条下面那句:离压缩线多远 / 已移出几条 / 窗口未知。两个视图共用。</summary>
    internal static string ContextCaption(ContextUsage u, Loc loc)
    {
        string caption;
        if (u.Window <= 0)
            caption = loc["UsageContextUnknownWindow"];
        else if (u.CompactAt > 0 && u.NearCompaction)
            caption = loc.F("ContextNearCompaction", u.Estimate + ChatFormatting.Compact(u.Free));
        else if (u.CompactAt > 0)
            caption = loc.F("ContextToCompaction", u.Estimate + ChatFormatting.Compact(u.Free), ChatFormatting.Compact(u.CompactAt));
        else
            caption = loc.F(u.Figures.Streaming ? "UsageContextCaptionStreaming" : "UsageContextCaption",
                u.Estimate + u.Percent, ChatFormatting.Compact(Math.Max(0, u.Window - u.Used)));
        if (u.Figures.Dropped > 0) caption += "\n" + loc.F("UsageTrimmedLine", u.Figures.Dropped);
        return caption;
    }

    /// <summary>「输入 / 输出 / 思考 / 花费」一格:小标签 + 数。两个视图共用。</summary>
    internal static void AddStat(Grid grid, int column, string label, string value, string tip)
    {
        var cell = new StackPanel { Spacing = 0 };
        cell.Children.Add(UsageVisuals.Text(label, "VelaTextTertiary", "VelaFontSize11"));
        TextBlock number = UsageVisuals.Text(value, bold: true);
        ToolTip.SetTip(number, tip);
        cell.Children.Add(number);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private void Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        _slices.Children.Add(control);
    }

    private static StackPanel LegendItem(string tone, string label, long value)
    {
        TextBlock text = UsageVisuals.Text($"{label} {ChatFormatting.Compact(value)}", "VelaTextSecondary", "VelaFontSize11");
        ToolTip.SetTip(text, $"{value:N0}");
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 10, 0) };
        item.Children.Add(UsageVisuals.Swatch(tone));
        item.Children.Add(text);
        return item;
    }
}
