using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>一行里的一段着色:从 <see cref="Start" /> 起 <see cref="Length" /> 个字符用 <see cref="Brush" />。</summary>
internal readonly record struct HighlightSpan(int Start, int Length, IBrush Brush);

/// <summary>
/// 一种语言在一套配色下的逐行分词器:<see cref="TokenizeLine" /> 带着上一行的状态往下走,
/// 多行注释、多行字符串的状态才接得上。
/// </summary>
internal sealed class TextMateLineTokenizer
{
    private readonly IGrammar _grammar;
    private readonly Theme _theme;
    private readonly int _defaultForeground;
    private readonly Dictionary<int, IBrush?> _brushes = [];

    internal TextMateLineTokenizer(IGrammar grammar, Theme theme)
    {
        _grammar = grammar;
        _theme = theme;
        // 主题的缺省前景(根规则)不着色:正文颜色交给 Vela 令牌,亮暗主题下都读得清
        _defaultForeground = theme.Match([]).Select(r => r.foreground).FirstOrDefault(f => f > 0);
    }

    /// <summary>分一行;超时或出错时返回空着色、状态原样往下传。</summary>
    /// <param name="line">行文本(不含换行符)。</param>
    /// <param name="state">上一行结束时的状态;第一行为 null。</param>
    /// <param name="spans">这一行的着色段。</param>
    /// <returns>这一行结束时的状态,交给下一行。</returns>
    internal IStateStack? TokenizeLine(string line, IStateStack? state, out IReadOnlyList<HighlightSpan> spans)
    {
        if (line.Length > TextMateHighlighter.MaxLineLength)
        {
            spans = [];
            return state;
        }
        try
        {
            ITokenizeLineResult result = _grammar.TokenizeLine(line, state, TextMateHighlighter.LineTimeout);
            var list = new List<HighlightSpan>(result.Tokens.Length);
            foreach (IToken token in result.Tokens)
            {
                int start = Math.Min(token.StartIndex, line.Length);
                int end = Math.Min(token.EndIndex, line.Length);
                if (end <= start) continue;
                if (BrushFor(token.Scopes) is { } brush)
                {
                    list.Add(new HighlightSpan(start, end - start, brush));
                }
            }
            spans = list;
            return result.RuleStack;
        }
        catch (Exception)
        {
            spans = [];
            return state;
        }
    }

    private IBrush? BrushFor(List<string> scopes)
    {
        int foreground = 0;
        foreach (ThemeTrieElementRule rule in _theme.Match(scopes))
        {
            if (rule.foreground > 0)
            {
                foreground = rule.foreground;
                break;
            }
        }
        if (foreground <= 0 || foreground == _defaultForeground) return null;
        if (_brushes.TryGetValue(foreground, out IBrush? cached)) return cached;
        // 颜色来自 TextMate 主题数据(与 LiveMarkdown 代码块同一套 DarkPlus / LightPlus),不是代码里的字面量
        IBrush? brush = Color.TryParse(_theme.GetColor(foreground), out Color color) ? new ImmutableSolidColorBrush(color) : null;
        _brushes[foreground] = brush;
        return brush;
    }
}

/// <summary>
/// TextMate 语法高亮(TextMateSharp,与聊天代码块共用宿主那份程序集与 grammar):
/// 按文件扩展名挑语言,逐行分词并把上一行的状态带下去,按主题规则给每段前景色。
/// </summary>
/// <remarks>
/// <para>
/// 配色跟随亮暗:<see cref="ThemeVariant.Dark" /> 用 DarkPlus,否则 LightPlus —— 与 <c>ChatPanelView.SyncMarkdownSkin</c> 一致。
/// <c>Registry</c> 每套配色一份、grammar 按作用域名缓存,反复打开对比不重新加载。
/// </para>
/// <para>
/// 上限:超过 <see cref="MaxLines" /> 行不高亮;单行超过 <see cref="MaxLineLength" /> 字符那一行不着色(状态原样往下传);
/// 每行分词 <see cref="LineTimeout" /> 超时;整份累计超过 <see cref="TotalBudget" /> 后余下的行不再着色。
/// 认不出的扩展名、原生库或 grammar 加载失败,一律退回纯文本,不抛。
/// </para>
/// </remarks>
internal static class TextMateHighlighter
{
    /// <summary>超过这么多行整份不高亮。</summary>
    internal const int MaxLines = 20000;

    /// <summary>单行超过这么多字符不着色(压缩过的 JS、base64 之类)。</summary>
    internal const int MaxLineLength = 3000;

    /// <summary>每行分词的超时。</summary>
    internal static readonly TimeSpan LineTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>一整份文本的分词时间预算。</summary>
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromMilliseconds(1500);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<ThemeName, (RegistryOptions Options, Registry Registry)> Registries = [];
    private static readonly Dictionary<(ThemeName, string), IGrammar?> Grammars = [];
    private static bool _unavailable;

    /// <summary>当前主题变体对应的 TextMate 配色。</summary>
    internal static ThemeName ThemeFor(ThemeVariant? variant)
        => variant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;

    /// <summary>为某个文件路径建分词器;认不出语言、或 TextMate 不可用时返回 null。</summary>
    /// <param name="path">文件路径(只看扩展名)。</param>
    /// <param name="theme">配色。</param>
    internal static TextMateLineTokenizer? ForPath(string? path, ThemeName theme)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string extension = System.IO.Path.GetExtension(path.Replace('\\', '/'));
        if (extension.Length < 2) return null;
        lock (Gate)
        {
            if (_unavailable) return null;
            try
            {
                (RegistryOptions options, Registry registry) = RegistryFor(theme);
                string? scope = options.GetScopeByExtension(extension);
                if (string.IsNullOrEmpty(scope)) return null;
                if (!Grammars.TryGetValue((theme, scope), out IGrammar? grammar))
                {
                    grammar = registry.LoadGrammar(scope);
                    Grammars[(theme, scope)] = grammar;
                }
                return grammar is null ? null : new TextMateLineTokenizer(grammar, registry.GetTheme());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or BadImageFormatException)
            {
                // 原生正则库(libonigwrap)不在:整个进程都别再试了
                _unavailable = true;
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 给一整份文本的每一行算着色。必须传<b>完整</b>文本(或完整的补丁片段),不能只传折叠后的几行 ——
    /// 多行注释与字符串的状态是从头带下来的。不高亮时返回 null。
    /// </summary>
    /// <param name="lines">全部行。</param>
    /// <param name="path">文件路径(决定语言)。</param>
    /// <param name="theme">配色。</param>
    internal static IReadOnlyList<HighlightSpan>[]? HighlightLines(IReadOnlyList<string> lines, string? path, ThemeName theme)
    {
        if (lines.Count == 0 || lines.Count > MaxLines) return null;
        TextMateLineTokenizer? tokenizer = ForPath(path, theme);
        if (tokenizer is null) return null;
        var result = new IReadOnlyList<HighlightSpan>[lines.Count];
        IStateStack? state = null;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < lines.Count; i++)
        {
            if (clock.Elapsed > TotalBudget)
            {
                for (int j = i; j < lines.Count; j++) result[j] = [];
                break;
            }
            state = tokenizer.TokenizeLine(lines[i], state, out IReadOnlyList<HighlightSpan> spans);
            result[i] = spans;
        }
        return result;
    }

    private static (RegistryOptions, Registry) RegistryFor(ThemeName theme)
    {
        if (!Registries.TryGetValue(theme, out (RegistryOptions Options, Registry Registry) entry))
        {
            var options = new RegistryOptions(theme);
            entry = (options, new Registry(options));
            Registries[theme] = entry;
        }
        return entry;
    }
}
