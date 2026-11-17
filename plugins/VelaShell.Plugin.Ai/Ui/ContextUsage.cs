using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>上下文按来源分成的几类(用量面板里一类一种颜色)。</summary>
internal enum ContextSlice
{
    System,
    Tools,
    Mcp,
    Summary,
    Messages
}

/// <summary>
/// 用量面板(悬停)与上下文明细(点击)共用的一份算好的数:已用、窗口、压缩线、各类占用。
/// </summary>
/// <remarks>
/// 分类来自 <see cref="ContextBreakdown" />(本地估算),这里按<b>报告的</b>输入总量等比缩放,
/// 所以各类加起来正好等于状态行上那个数。没有拆分(还没发过请求、或刚从历史恢复)时只有总量。
/// </remarks>
internal sealed class ContextUsage
{
    /// <summary>压缩线之前多近算「快到了」:与状态行变色无关,只用于面板的提示。</summary>
    private const double NearRatio = 0.92;

    public ContextUsage(UsageFigures f)
    {
        Figures = f;
        Used = Math.Max(0, f.ContextTokens);
        Window = Math.Max(0, f.Window);
        Reserve = Window > 0 ? Math.Clamp(f.MaxOutput, 0, Window) : 0;
        CompactAt = Window > 0 ? Math.Max(0, f.CompactAt) : 0;
        Breakdown = f.Breakdown;
        // 生成中:总量本身就是估算(请求 + 已生成的输出),拆分不缩放,多出来的那截是正在写的回复,记进消息
        Factor = !f.Streaming && Breakdown is { Total: > 0 } b && Used > 0 ? Used / (double)b.Total : 1;
        Slices = Breakdown is null ? [] : Split(Breakdown);
    }

    public UsageFigures Figures { get; }
    public ContextBreakdown? Breakdown { get; }

    /// <summary>已用(报告值;生成中是估算)。</summary>
    public long Used { get; }

    /// <summary>上下文窗口;0 = 不知道。</summary>
    public long Window { get; }

    /// <summary>给回复留的余量(最大输出)。</summary>
    public long Reserve { get; }

    /// <summary>压缩线;0 = 不会自动压缩(窗口未知或开关关着)。</summary>
    public long CompactAt { get; }

    /// <summary>估算 → 报告值的缩放系数。</summary>
    public double Factor { get; }

    /// <summary>非零的各类占用(已缩放),按固定顺序。</summary>
    public IReadOnlyList<(ContextSlice Slice, long Tokens)> Slices { get; }

    /// <summary>数字前的估算标记(生成中为 <c>~</c>)。</summary>
    public string Estimate => Figures.Streaming ? "~" : "";

    /// <summary>到压缩线(没有压缩线时到「窗口 − 余量」)还能用多少。</summary>
    public long Free => Math.Max(0, (CompactAt > 0 ? CompactAt : Window - Reserve) - Used);

    /// <summary>压缩线之后、余量之前那段:到了这里就会先压缩,实际用不上。</summary>
    public long Buffer => CompactAt > 0 ? Math.Max(0, Window - Reserve - Math.Max(CompactAt, Used)) : 0;

    /// <summary>离压缩线已经很近。</summary>
    public bool NearCompaction => CompactAt > 0 && Used >= CompactAt * NearRatio;

    public int Percent => Window > 0 ? (int)Math.Min(100, Math.Round(Used * 100.0 / Window)) : 0;

    /// <summary>某个估算值缩放到报告口径。</summary>
    public long Scale(int raw) => (long)Math.Round(raw * Factor);

    /// <summary>占窗口的百分比文字(小于 10 留一位小数)。</summary>
    public string PercentOf(long tokens)
    {
        if (Window <= 0) return "";
        double p = tokens * 100.0 / Window;
        return p < 10 ? $"{p:0.0}%" : $"{p:0}%";
    }

    /// <summary>
    /// 整根条(相对窗口)的分段:各类 → 可用(空,露出底槽)→ 压缩缓冲 → 回复余量。
    /// 窗口未知时只画各类,相对已用。
    /// </summary>
    public List<UsageVisuals.Segment> BarSegments()
    {
        var segments = new List<UsageVisuals.Segment>();
        double whole = Window > 0 ? Window : Math.Max(1, Used);
        if (Slices.Count > 0)
        {
            foreach ((ContextSlice slice, long tokens) in Slices)
                segments.Add(new(tokens / whole, UsageVisuals.ToneOf(slice)));
        }
        else if (Used > 0)
        {
            segments.Add(new(Used / whole, UsageVisuals.ToneOf(ContextSlice.Messages)));
        }
        if (Window <= 0) return segments;
        segments.Add(new(Free / whole, null));
        if (Buffer > 0) segments.Add(new(Buffer / whole, UsageVisuals.BufferTone, UsageVisuals.BufferOpacity));
        if (Reserve > 0) segments.Add(new(Reserve / whole, UsageVisuals.ReserveTone, UsageVisuals.ReserveOpacity));
        return segments;
    }

    /// <summary>
    /// 按比例缩放,保证各类加起来等于已用:多出来的(舍入,或生成中正在写的回复)记到「消息」上;
    /// 少了的从最大的一类扣 —— 扣在消息上会把只有几个 token 的消息整行抹掉。
    /// </summary>
    private List<(ContextSlice, long)> Split(ContextBreakdown b)
    {
        (ContextSlice Slice, int Raw)[] raw =
        [
            (ContextSlice.System, b.System),
            (ContextSlice.Tools, b.Tools),
            (ContextSlice.Mcp, b.Mcp),
            (ContextSlice.Summary, b.Summary),
            (ContextSlice.Messages, b.Messages)
        ];
        List<(ContextSlice Slice, long Tokens)> scaled =
            [.. raw.Where(r => r.Raw > 0 || r.Slice == ContextSlice.Messages)
                   .Select(r => (r.Slice, r.Raw > 0 ? Math.Max(1, Scale(r.Raw)) : 0))];
        if (Used > 0)
        {
            long diff = Used - scaled.Sum(s => s.Tokens);
            int target = diff >= 0
                ? scaled.FindIndex(s => s.Slice == ContextSlice.Messages)
                : scaled.IndexOf(scaled.MaxBy(s => s.Tokens));
            scaled[target] = (scaled[target].Slice, Math.Max(0, scaled[target].Tokens + diff));
        }
        return [.. scaled.Where(s => s.Tokens > 0)];
    }
}

/// <summary>两个用量视图共用的小部件:分段条、色块、文字。颜色一律绑主题令牌。</summary>
internal static class UsageVisuals
{
    public const string BufferTone = "VelaWarning";
    public const double BufferOpacity = 0.35;
    public const string ReserveTone = "VelaTextMuted";
    public const double ReserveOpacity = 0.55;
    public const string FreeTone = "VelaBorderSecondary";

    /// <summary>一截分段;<see cref="Tone" /> 为 null 表示空着(露出底槽)。</summary>
    public readonly record struct Segment(double Ratio, string? Tone, double Opacity = 1);

    /// <summary>各类的颜色:状态行与面板同一套,用主题自带的那组 ANSI 色(深浅两套主题都有)。</summary>
    public static string ToneOf(ContextSlice slice) => slice switch
    {
        ContextSlice.System => "VelaShellSubtle",
        ContextSlice.Tools => "VelaShellCyan",
        ContextSlice.Mcp => "VelaShellMagenta",
        ContextSlice.Summary => "VelaShellYellow",
        _ => "VelaAccent"
    };

    public static string NameKey(ContextSlice slice) => "ContextSlice" + slice;

    /// <summary>一根分段条,可选在 <paramref name="mark" />(0–1)处画一道刻度(压缩线)。</summary>
    public static Control Bar(IReadOnlyList<Segment> segments, double height, double? mark = null)
    {
        var bar = new Grid { Height = height };
        double used = 0;
        foreach (Segment segment in segments)
        {
            double r = Math.Clamp(segment.Ratio, 0, 1 - used);
            if (r <= 0) continue;
            // 极小的一截也给 2px,免得「有」看上去像「没有」
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(r, GridUnitType.Star))
            { MinWidth = segment.Tone is null ? 0 : 2 });
            if (segment.Tone is { } tone)
            {
                Border fill = Fill(tone, segment.Opacity);
                Grid.SetColumn(fill, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(fill);
            }
            used += r;
        }
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0, 1 - used), GridUnitType.Star)));

        var track = new Border { CornerRadius = new CornerRadius(height / 2), ClipToBounds = true, Child = bar };
        track[!Border.BackgroundProperty] = new DynamicResourceExtension(FreeTone);
        if (mark is not { } m || m <= 0 || m >= 1) return track;

        // 刻度比条上下各高出 3px,压在条上面
        var layer = new Grid { Height = height + 6 };
        track.VerticalAlignment = VerticalAlignment.Center;
        layer.Children.Add(track);
        var marks = new Grid { ColumnDefinitions = [new ColumnDefinition(new GridLength(m, GridUnitType.Star)), new ColumnDefinition(new GridLength(1 - m, GridUnitType.Star))] };
        var tick = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-1, 0, 0, 0)
        };
        tick[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        Grid.SetColumn(tick, 1);
        marks.Children.Add(tick);
        layer.Children.Add(marks);
        return layer;
    }

    public static Border Fill(string tone, double opacity = 1)
    {
        var fill = new Border { Opacity = opacity };
        fill[!Border.BackgroundProperty] = new DynamicResourceExtension(tone);
        return fill;
    }

    public static Border Swatch(string tone, double opacity = 1, bool hollow = false)
    {
        var swatch = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = opacity
        };
        if (hollow)
        {
            swatch.BorderThickness = new Thickness(1.5);
            swatch[!Border.BorderBrushProperty] = new DynamicResourceExtension(FreeTone);
        }
        else
        {
            swatch[!Border.BackgroundProperty] = new DynamicResourceExtension(tone);
        }
        return swatch;
    }

    /// <summary>一段绑令牌的文字。</summary>
    public static TextBlock Text(string text = "", string tone = "VelaTextPrimary", string size = "VelaFontSize12",
        bool bold = false, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!wrap) block.TextTrimming = TextTrimming.CharacterEllipsis;
        block[!TextBlock.FontSizeProperty] = new DynamicResourceExtension(size);
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(tone);
        return block;
    }

    /// <summary>右对齐的等宽数字。</summary>
    public static TextBlock Number(string text, string tone = "VelaTextPrimary", string size = "VelaFontSize11", bool bold = false)
    {
        TextBlock block = Text(text, tone, size, bold);
        block.HorizontalAlignment = HorizontalAlignment.Right;
        block.TextAlignment = TextAlignment.Right;
        return block;
    }

    /// <summary>一条细分隔线(两侧顶到面板边)。</summary>
    public static Border Rule()
    {
        var rule = new Border { Height = 1 };
        rule[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
        return rule;
    }

    /// <summary>百分比徽标:平时强调色,快到压缩线时警告色。</summary>
    public static Panel Badge(string text, bool warn)
    {
        string tone = warn ? "VelaWarning" : "VelaAccent";
        TextBlock label = Text(text, tone, "VelaFontSize11", bold: true);
        label.Margin = new Thickness(7, 1);
        // 底色用同一个令牌压低不透明度(主题里没有 Warning 的 Dim 版),文字本身保持不透明
        Border ground = Fill(tone, 0.16);
        ground.CornerRadius = new CornerRadius(8);
        return new Panel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ground, label }
        };
    }
}
