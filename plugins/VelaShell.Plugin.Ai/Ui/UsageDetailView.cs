using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

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
internal sealed record UsageFigures(
    bool Streaming, long ContextTokens, int Window, string WindowWarning,
    long LastInput, long LastCacheRead, long LastCacheWrite,
    long TotalInput, long TotalOutput, long TotalReasoning, long TotalCacheRead, long TotalCacheWrite,
    int Dropped, double? Cost, int MaxOutput, bool ReportsCacheWrites);

/// <summary>
/// 输入框右下角用量的悬停面板:上下文窗口、提示缓存、本会话累计、花费,各一段,配占比条。
/// </summary>
/// <remarks>
/// <para>
/// 以前悬停里是七八行同一字号的纯文本,「上一轮 / 累计」「命中 / 写入」挤在一起分不出主次。
/// 现在每段一个小标题 + 右侧的关键数字,上下文与缓存各一根条:上下文条按阈值变色(与状态行那根同一规则),
/// 缓存条把上一轮的输入拆成「读取 / 写入 / 未命中」三截 —— 缓存值不值得开,看这一根就够了。
/// </para>
/// <para>
/// 弹层不在面板的样式作用域里,所以字号、颜色都直接绑令牌,不靠 <c>dim</c> 之类的类名。
/// <see cref="ToString" /> 返回纯文本明细(读屏与测试用),内容与以前的文字提示一致。
/// </para>
/// </remarks>
internal sealed class UsageDetailView : StackPanel
{
    private const double BarHeight = 6;
    private readonly Loc _loc;

    // 上下文
    private readonly TextBlock _contextValue = Value();
    private readonly Grid _contextBar = new() { Height = BarHeight };
    private readonly TextBlock _contextCaption = Caption();
    private readonly TextBlock _windowWarning = Caption("VelaWarning");

    // 缓存
    private readonly StackPanel _cacheSection = new() { Spacing = 4 };
    private readonly TextBlock _cacheValue = Value();
    private readonly Grid _cacheBar = new() { Height = BarHeight };
    private readonly WrapPanel _cacheLegend = new();
    private readonly TextBlock _cacheCaption = Caption();

    // 本会话
    private readonly Grid _session = new() { ColumnDefinitions = [with("*,*,*")], ColumnSpacing = 8 };
    private readonly Grid _costRow = new() { ColumnDefinitions = [with("*,Auto")] };
    private readonly TextBlock _costValue = Value();
    private readonly TextBlock _footer = Caption();

    private string _plain = "";

    public UsageDetailView(Loc loc)
    {
        _loc = loc;
        Spacing = 10;
        Width = 280;

        var context = new StackPanel { Spacing = 4 };
        context.Children.Add(Heading("UsageSectionContext", _contextValue));
        context.Children.Add(Track(_contextBar));
        context.Children.Add(_contextCaption);
        context.Children.Add(_windowWarning);
        Children.Add(context);

        _cacheSection.Children.Add(Heading("UsageSectionCache", _cacheValue));
        _cacheSection.Children.Add(Track(_cacheBar));
        _cacheSection.Children.Add(_cacheLegend);
        _cacheSection.Children.Add(_cacheCaption);
        Children.Add(_cacheSection);

        var session = new StackPanel { Spacing = 4 };
        session.Children.Add(Heading("UsageSectionSession", null));
        session.Children.Add(_session);
        Children.Add(session);

        var costLabel = Label(_loc["UsageCostLabel"]);
        _costRow.Children.Add(costLabel);
        Grid.SetColumn(_costValue, 1);
        _costRow.Children.Add(_costValue);
        Children.Add(_costRow);

        Children.Add(_footer);
    }

    /// <summary>纯文本明细(读屏、测试与复制用)。</summary>
    public override string ToString() => _plain;

    public void Update(UsageFigures f, string plainText)
    {
        _plain = plainText;
        string estimate = f.Streaming ? "~" : "";

        // ── 上下文窗口 ──
        if (f.Window > 0)
        {
            double ratio = Math.Clamp(f.ContextTokens / (double)f.Window, 0, 1);
            int percent = (int)Math.Round(ratio * 100);
            _contextValue.Text = $"{estimate}{f.ContextTokens:N0} / {f.Window:N0}";
            SetSegments(_contextBar, [(ratio, ContextTone(percent))]);
            _contextCaption.Text = _loc.F(f.Streaming ? "UsageContextCaptionStreaming" : "UsageContextCaption",
                estimate + percent, ChatFormatting.Compact(Math.Max(0, f.Window - f.ContextTokens)));
            ((Control)_contextBar.Parent!).IsVisible = true;
        }
        else
        {
            _contextValue.Text = f.ContextTokens > 0 ? $"{estimate}{f.ContextTokens:N0}" : "—";
            ((Control)_contextBar.Parent!).IsVisible = false; // 不知道窗口就画不出占比,不留空槽
            _contextCaption.Text = _loc["UsageContextUnknownWindow"];
        }
        if (f.Dropped > 0) _contextCaption.Text += "\n" + _loc.F("UsageTrimmedLine", f.Dropped);
        _windowWarning.Text = f.WindowWarning;
        _windowWarning.IsVisible = f.WindowWarning.Length > 0;

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
            SetSegments(_cacheBar,
            [
                (read / (double)f.LastInput, "VelaStatusConnected"),
                (written / (double)f.LastInput, "VelaWarning")
            ]);
            _cacheLegend.Children.Clear();
            _cacheLegend.Children.Add(LegendItem("VelaStatusConnected", _loc["UsageCacheRead"], read));
            _cacheLegend.Children.Add(LegendItem("VelaWarning", _loc["UsageCacheWrite"], written));
            _cacheLegend.Children.Add(LegendItem("VelaBorderPrimary", _loc["UsageCacheMiss"], missed));
            string caption = anyCache
                ? _loc.F("UsageCacheTotalsLine", $"{f.TotalCacheRead:N0}", $"{f.TotalCacheWrite:N0}")
                : _loc["UsageCacheNone"];
            // 写入一直是 0 而协议本来就不一定报:说清楚是「没报」,不是「没写」
            if (f.TotalCacheWrite == 0 && !f.ReportsCacheWrites) caption += "\n" + _loc["UsageCacheWriteUnreported"];
            _cacheCaption.Text = caption;
        }

        // ── 本会话累计 ──
        _session.Children.Clear();
        AddStat(0, _loc["UsageInput"], f.TotalInput);
        AddStat(1, _loc["UsageOutput"], f.TotalOutput);
        if (f.TotalReasoning > 0) AddStat(2, _loc["UsageReasoning"], f.TotalReasoning);

        _costRow.IsVisible = f.Cost is not null;
        _costValue.Text = f.Cost is { } cost ? ChatPanelView.FormatCost(cost) : "";

        _footer.Text = (f.MaxOutput > 0
                           ? _loc.F("UsageLimitsLine", $"{f.MaxOutput:N0}", f.Window > 0 ? $"{f.Window:N0}" : "—")
                           : "")
                       + (f.Cost is not null ? (f.MaxOutput > 0 ? "\n" : "") + _loc["UsageCostNote"] : "")
                       + (f.Streaming ? "\n" + _loc["UsageStreamingEstimate"] : "");
        _footer.IsVisible = _footer.Text.Length > 0;
    }

    private void AddStat(int column, string label, long value)
    {
        var cell = new StackPanel { Spacing = 0 };
        cell.Children.Add(Label(label));
        TextBlock number = Value();
        number.Text = ChatFormatting.Compact(value);
        ToolTip.SetTip(number, $"{value:N0}");
        cell.Children.Add(number);
        Grid.SetColumn(cell, column);
        _session.Children.Add(cell);
    }

    private static string ContextTone(int percent) => percent switch
    {
        >= 95 => "VelaError",
        >= 80 => "VelaWarning",
        _ => "VelaAccent"
    };

    /// <summary>把一根条按比例分成几截上色,剩下的露出底槽。</summary>
    private static void SetSegments(Grid bar, IReadOnlyList<(double Ratio, string Tone)> segments)
    {
        bar.Children.Clear();
        bar.ColumnDefinitions.Clear();
        double used = 0;
        foreach ((double ratio, string tone) in segments)
        {
            double r = Math.Clamp(ratio, 0, 1 - used);
            if (r <= 0) continue;
            // 极小的一截也给 2px,免得「有」看上去像「没有」
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(r, GridUnitType.Star)) { MinWidth = 2 });
            var fill = new Border();
            fill[!Border.BackgroundProperty] = new DynamicResourceExtension(tone);
            Grid.SetColumn(fill, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(fill);
            used += r;
        }
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0, 1 - used), GridUnitType.Star)));
    }

    private static Border Track(Grid bar)
    {
        var track = new Border { CornerRadius = new CornerRadius(BarHeight / 2), ClipToBounds = true, Child = bar };
        track[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBorderSecondary");
        return track;
    }

    private Grid Heading(string key, TextBlock? value)
    {
        var grid = new Grid { ColumnDefinitions = [with("*,Auto")] };
        var title = new TextBlock { Text = _loc[key], FontWeight = FontWeight.SemiBold };
        title[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        grid.Children.Add(title);
        if (value is not null)
        {
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static TextBlock Value()
    {
        var text = new TextBlock { FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        return text;
    }

    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text };
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextTertiary");
        return label;
    }

    private static TextBlock Caption(string tone = "VelaTextTertiary")
    {
        var caption = new TextBlock { TextWrapping = TextWrapping.Wrap };
        caption[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        caption[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(tone);
        return caption;
    }

    private static StackPanel LegendItem(string tone, string label, long value)
    {
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        dot[!Border.BackgroundProperty] = new DynamicResourceExtension(tone);
        var text = new TextBlock { Text = $"{label} {ChatFormatting.Compact(value)}", VerticalAlignment = VerticalAlignment.Center };
        text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        ToolTip.SetTip(text, $"{value:N0}");
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 10, 0) };
        item.Children.Add(dot);
        item.Children.Add(text);
        return item;
    }
}
