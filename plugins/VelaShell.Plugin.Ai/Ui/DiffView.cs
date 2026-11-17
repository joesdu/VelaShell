using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// git 风格的统一差异视图:头部 <c>+N −M</c>,正文逐行带旧/新行号与 <c>+</c>/<c>-</c> 前缀,
/// 增删行垫一层淡色底,改动附近只留 3 行上下文,其余压成一行 <c>⋯</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>增删行的底色不是新令牌。</b>底色层就是 <c>VelaStatusConnected</c> / <c>VelaError</c> 本身,
/// 压低不透明度垫在文字下面。软件有十二套命名主题,令牌由种子色派生后整份换掉;
/// 在插件里另写两套亮暗色值,换到 Nord、Sakura 之类的主题就对不上了 —— 压透明度的做法跟着任何主题走。
/// </para>
/// <para>
/// 用法:<c>new DiffView(diff, loc, DiffViewMode.Inline)</c> 嵌进工具卡片展开区(只横向滚动,纵向交给外层);
/// <c>DiffViewMode.Window</c> 用在独立的差异窗口里(两个方向都自己滚)。
/// 超过 <see cref="MaxRenderedLines" /> 行的部分不再逐行建控件,只留一行「还有 N 行」。
/// </para>
/// </remarks>
internal sealed class DiffView : DockPanel
{
    /// <summary>最多逐行建多少行(整份新建的大文件也不至于把界面拖住)。</summary>
    internal const int MaxRenderedLines = 1500;

    /// <summary>增删行底色层的不透明度。</summary>
    private const double TintOpacity = 0.16;

    /// <summary>行号列每位数字的宽度估值(等宽 11 号字)。</summary>
    private const double DigitWidth = 7;

    private readonly Loc _loc;
    private readonly double _numberWidth;
    private readonly bool _numbers;
    private readonly ToolFileDiff _diff;
    private readonly int _context;
    private readonly string? _languagePath;
    private ThemeVariant? _builtFor;
    private int _rendered;

    /// <summary>每段的旧 / 新两侧逐行着色(按段内行号 - 1 取);拿不到为 null。</summary>
    private (IReadOnlyList<HighlightSpan>[]? Old, IReadOnlyList<HighlightSpan>[]? New)[] _highlights = [];

    /// <summary>正文的行容器(测试按它数行)。</summary>
    internal StackPanel Rows { get; } = new() { Name = "DiffRows" };

    /// <summary>正文的滚动容器(差异文档跳转「上一处 / 下一处」时滚它)。</summary>
    internal ScrollViewer Scroll { get; }

    /// <summary>当前是否有语法着色(测试用)。</summary>
    internal bool IsHighlighted => _highlights.Any(h => h.Old is not null || h.New is not null);

    /// <param name="diff">要画的差异。</param>
    /// <param name="loc">文案。</param>
    /// <param name="mode">嵌在卡片里还是独立窗口。</param>
    /// <param name="title">头部左侧的标题(文件名);null 不显示。</param>
    /// <param name="context">改动附近保留的上下文行数。</param>
    /// <param name="action">贴在头部最右侧的操作(工具卡片里的「在标签页中打开」);null 不放。</param>
    /// <param name="languagePath">按它的扩展名挑语法高亮的语言;null 时用 <see cref="ToolFileDiff.Path" />。</param>
    internal DiffView(ToolFileDiff diff, Loc loc, DiffViewMode mode, string? title = null, int context = 3, Control? action = null,
        string? languagePath = null)
    {
        _loc = loc;
        _diff = diff;
        _context = context;
        _languagePath = languagePath ?? diff.Path;
        Name = "DiffView";
        _numbers = diff.HasLineNumbers;
        int maxNumber = 0;
        foreach (LineDiffResult section in diff.Sections)
        {
            foreach (DiffLine line in section.Lines)
            {
                maxNumber = Math.Max(maxNumber, Math.Max(line.OldNumber, line.NewNumber));
            }
        }
        _numberWidth = _numbers ? Math.Max(2, maxNumber.ToString(CultureInfo.InvariantCulture).Length) * DigitWidth + 8 : 0;

        Grid header = BuildHeader(diff, title, action);
        SetDock(header, Dock.Top);
        Children.Add(header);

        ScrollViewer scroll = Scroll = new ScrollViewer
        {
            Name = "DiffScroll",
            Content = Rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = mode == DiffViewMode.Window ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = scroll
        };
        frame[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");
        frame[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgInput");
        Children.Add(frame);

        BuildRows(Application.Current?.ActualThemeVariant);
        // 亮暗切换时换一套 TextMate 配色重画(行级底色本来就是令牌,自己会跟着变)
        ActualThemeVariantChanged += (_, _) =>
        {
            if (_builtFor != ActualThemeVariant) BuildRows(ActualThemeVariant);
        };
    }

    /// <summary>按配色重建正文各行。着色对每段的<b>完整</b>旧 / 新文本分词,再按行号取用 —— 不对折叠后的片段分词。</summary>
    private void BuildRows(ThemeVariant? variant)
    {
        _builtFor = variant;
        _rendered = 0;
        Rows.Children.Clear();
        TextMateSharp.Grammars.ThemeName theme = TextMateHighlighter.ThemeFor(variant);
        _highlights = [.. _diff.Sections.Select(section =>
        {
            string[] oldLines = [.. section.Lines.Where(l => l.Kind != DiffLineKind.Added).Select(l => l.Text)];
            string[] newLines = [.. section.Lines.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text)];
            return (TextMateHighlighter.HighlightLines(oldLines, _languagePath, theme),
                    TextMateHighlighter.HighlightLines(newLines, _languagePath, theme));
        })];

        var sections = new List<(int Index, IReadOnlyList<DiffLine> Lines)>();
        for (int i = 0; i < _diff.Sections.Count; i++)
        {
            IReadOnlyList<DiffLine> collapsed = _diff.Sections[i].Collapse(_context);
            if (collapsed.Count > 0) sections.Add((i, collapsed));
        }
        int total = sections.Sum(l => l.Lines.Count);
        for (int s = 0; s < sections.Count && _rendered < total; s++)
        {
            if (s > 0)
            {
                // 补丁的几处替换之间:不知道隔了多少行,只画一行 ⋯
                Rows.Children.Add(GapRow("⋯"));
            }
            foreach (DiffLine line in sections[s].Lines)
            {
                if (_rendered >= MaxRenderedLines)
                {
                    Rows.Children.Add(GapRow(_loc.F("DiffTruncated", total - _rendered)));
                    _rendered = total;
                    break;
                }
                Rows.Children.Add(line.Kind == DiffLineKind.Gap
                    ? GapRow("⋯ " + _loc.F("DiffHiddenLines", line.HiddenCount))
                    : LineRow(line, SpansFor(sections[s].Index, line)));
                _rendered++;
            }
        }
        if (sections.Count == 0)
        {
            Rows.Children.Add(GapRow(_loc["DiffNoChanges"]));
        }
    }

    private IReadOnlyList<HighlightSpan>? SpansFor(int section, DiffLine line)
    {
        if (section >= _highlights.Length) return null;
        (IReadOnlyList<HighlightSpan>[]? old, IReadOnlyList<HighlightSpan>[]? @new) = _highlights[section];
        return line.Kind == DiffLineKind.Removed ? At(old, line.OldNumber) : At(@new, line.NewNumber);

        static IReadOnlyList<HighlightSpan>? At(IReadOnlyList<HighlightSpan>[]? lines, int number)
            => lines is not null && number > 0 && number <= lines.Length ? lines[number - 1] : null;
    }

    /// <summary>
    /// 把一行文本按着色段拆成若干 Run(制表符展开成 4 个空格);没有着色的部分沿用 TextBlock 自己的前景色。
    /// </summary>
    internal static void ApplySpans(TextBlock block, string text, IReadOnlyList<HighlightSpan>? spans)
    {
        if (spans is not { Count: > 0 } || text.Length == 0)
        {
            block.Text = text.Length == 0 ? " " : Expand(text);
            return;
        }
        block.Text = null;
        var inlines = new InlineCollection();
        int at = 0;
        foreach (HighlightSpan span in spans)
        {
            if (span.Start < at || span.Start + span.Length > text.Length) continue;
            if (span.Start > at) inlines.Add(new Run(Expand(text[at..span.Start])));
            inlines.Add(new Run(Expand(text.Substring(span.Start, span.Length))) { Foreground = span.Brush });
            at = span.Start + span.Length;
        }
        if (at < text.Length) inlines.Add(new Run(Expand(text[at..])));
        block.Inlines = inlines;

        static string Expand(string s) => s.Replace("\t", "    ", StringComparison.Ordinal);
    }

    private static Grid BuildHeader(ToolFileDiff diff, string? title, Control? action)
    {
        var header = new Grid
        {
            ColumnDefinitions = [with("Auto,*,Auto,Auto,Auto")],
            Margin = new Thickness(0, 0, 0, 4)
        };
        if (title is { Length: > 0 })
        {
            var icon = new Decorator
            {
                Child = Icon("AiIcon.file-diff", "VelaTextTertiary", 12),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(icon);
            TextBlock name = Text(title, "VelaTextPrimary");
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 1);
            header.Children.Add(name);
        }
        TextBlock added = Text("+" + diff.Added, "VelaStatusConnected");
        added.Name = "DiffAdded";
        added.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(added, 2);
        header.Children.Add(added);
        TextBlock removed = Text("−" + diff.Removed, "VelaError");
        removed.Name = "DiffRemoved";
        removed.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(removed, 3);
        header.Children.Add(removed);
        if (action is not null)
        {
            action.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(action, 4);
            header.Children.Add(action);
        }
        return header;
    }

    private Panel LineRow(DiffLine line, IReadOnlyList<HighlightSpan>? spans)
    {
        var grid = new Grid { ColumnDefinitions = [with("Auto,Auto,Auto,*")] };
        string tone = line.Kind switch
        {
            DiffLineKind.Added => "VelaStatusConnected",
            DiffLineKind.Removed => "VelaError",
            _ => "VelaTextMuted"
        };
        if (_numbers)
        {
            grid.Children.Add(Number(line.OldNumber, 0));
            grid.Children.Add(Number(line.NewNumber, 1));
        }
        TextBlock prefix = Mono(line.Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Removed => "-",
            _ => " "
        }, tone);
        prefix.Width = 16;
        prefix.TextAlignment = TextAlignment.Center;
        Grid.SetColumn(prefix, 2);
        grid.Children.Add(prefix);
        TextBlock text = Mono("", line.Kind == DiffLineKind.Context ? "VelaTextSecondary" : "VelaTextPrimary");
        ApplySpans(text, line.Text, spans);
        text.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(text, 3);
        grid.Children.Add(text);

        var row = new Panel { Classes = { DiffClass(line.Kind) } };
        if (line.Kind is DiffLineKind.Added or DiffLineKind.Removed)
        {
            var tint = new Border { Opacity = TintOpacity, IsHitTestVisible = false };
            tint[!Border.BackgroundProperty] = new DynamicResourceExtension(tone);
            row.Children.Add(tint);
        }
        row.Children.Add(grid);
        return row;
    }

    private TextBlock Number(int number, int column)
    {
        TextBlock block = Mono(number > 0 ? number.ToString(CultureInfo.InvariantCulture) : "", "VelaTextMuted");
        block.Width = _numberWidth;
        block.TextAlignment = TextAlignment.Right;
        block.Padding = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(block, column);
        return block;
    }

    private Panel GapRow(string text)
    {
        TextBlock block = Mono(text, "VelaTextTertiary");
        block.Margin = new Thickness(_numbers ? _numberWidth * 2 + 16 : 16, 1, 8, 1);
        var row = new Panel { Classes = { "diffGap" } };
        var tint = new Border { IsHitTestVisible = false };
        tint[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgHover");
        row.Children.Add(tint);
        row.Children.Add(block);
        return row;
    }

    private static string DiffClass(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => "diffAdded",
        DiffLineKind.Removed => "diffRemoved",
        _ => "diffContext"
    };

    internal static TextBlock Mono(string text, string brushKey)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap };
        block[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        block[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(brushKey);
        return block;
    }

    internal static TextBlock Text(string text, string brushKey)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        block[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(brushKey);
        return block;
    }

    /// <summary>描边图标(与 <c>ChatPanelView.MakeIcon</c> 同一做法:几何与描边都绑资源)。</summary>
    internal static Viewbox Icon(string geometryKey, string brushKey, double size)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        path[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(geometryKey);
        path[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension(brushKey);
        return new Viewbox { Width = size, Height = size, Child = path };
    }
}

/// <summary><see cref="DiffView" /> 的摆放方式。</summary>
internal enum DiffViewMode
{
    /// <summary>嵌在工具卡片展开区:只横向滚动,纵向交给外层。</summary>
    Inline,

    /// <summary>独立的差异窗口:两个方向都自己滚。</summary>
    Window
}
