using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextMateSharp.Grammars;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// AvaloniaEdit 的 TextMate 着色:逐行分词,按行缓存「这一行结束时的状态」与着色段;
/// 文档一改,从改动所在那一行起作废缓存,之后的行在下次绘制时接着上一行的状态重算。
/// </summary>
/// <remarks>
/// 只从头顺序往下算:要画第 n 行,前 n-1 行的状态得先有 —— 跳着算,多行注释与字符串的状态就断了。
/// 超过 <see cref="TextMateHighlighter.MaxLines" /> 行的文档整份不着色;单行过长、分词超时的那一行不着色,状态原样往下传。
/// </remarks>
internal sealed class TextMateColorizer : DocumentColorizingTransformer
{
    private readonly TextMateLineTokenizer _tokenizer;
    private readonly TextDocument _document;
    private readonly List<IStateStack?> _endStates = [];
    private readonly List<IReadOnlyList<HighlightSpan>> _spans = [];

    internal TextMateColorizer(TextMateLineTokenizer tokenizer, TextDocument document)
    {
        _tokenizer = tokenizer;
        _document = document;
        document.Changed += OnDocumentChanged;
    }

    /// <summary>已经算过的行数(测试用)。</summary>
    internal int CachedLines => _spans.Count;

    /// <summary>取某行(从 1 起)的着色段,必要时从缓存末尾顺序算到这一行。</summary>
    internal IReadOnlyList<HighlightSpan> SpansOf(int lineNumber)
    {
        if (lineNumber < 1 || lineNumber > _document.LineCount || _document.LineCount > TextMateHighlighter.MaxLines) return [];
        while (_spans.Count < lineNumber)
        {
            int index = _spans.Count;
            DocumentLine line = _document.GetLineByNumber(index + 1);
            IStateStack? previous = index == 0 ? null : _endStates[index - 1];
            IStateStack? next = _tokenizer.TokenizeLine(_document.GetText(line.Offset, line.Length), previous, out IReadOnlyList<HighlightSpan> spans);
            _endStates.Add(next);
            _spans.Add(spans);
        }
        return _spans[lineNumber - 1];
    }

    /// <summary>解除对文档的订阅(换配色、换文档时)。</summary>
    internal void Detach() => _document.Changed -= OnDocumentChanged;

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (HighlightSpan span in SpansOf(line.LineNumber))
        {
            int start = line.Offset + span.Start;
            int end = Math.Min(start + span.Length, line.EndOffset);
            if (end <= start) continue;
            IBrush brush = span.Brush;
            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        int offset = Math.Clamp(e.Offset, 0, _document.TextLength);
        int index = _document.GetLineByOffset(offset).LineNumber - 1;
        if (index < _spans.Count)
        {
            _spans.RemoveRange(index, _spans.Count - index);
            _endStates.RemoveRange(index, _endStates.Count - index);
        }
    }
}

/// <summary>
/// 增删行的整行底色:令牌色(<c>VelaStatusConnected</c> / <c>VelaError</c>)压低不透明度画在文字下面,与 <see cref="DiffView" /> 同一配方。
/// </summary>
internal sealed class DiffLineBackgroundRenderer(Func<IBrush?> brush) : IBackgroundRenderer
{
    private const double TintOpacity = 0.16;

    /// <summary>要垫底色的行号(从 1 起)。</summary>
    internal HashSet<int> Lines { get; set; } = [];

    internal Dictionary<int, DiffTextSpan> ChangedSpans { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Lines.Count == 0 || brush() is not { } fill || !textView.VisualLinesValid) return;
        foreach (VisualLine line in textView.VisualLines)
        {
            DocumentLine documentLine = line.FirstDocumentLine;
            if (!Lines.Contains(documentLine.LineNumber)) continue;
            double top = line.VisualTop - textView.VerticalOffset;
            using (drawingContext.PushOpacity(TintOpacity))
                drawingContext.FillRectangle(fill, new Rect(0, top, Math.Max(textView.Bounds.Width, 1), line.Height));
            if (!ChangedSpans.TryGetValue(documentLine.LineNumber, out DiffTextSpan span) || span.Start + span.Length > documentLine.Length) continue;
            var segment = new TextSegment { StartOffset = documentLine.Offset + span.Start, Length = span.Length };
            using (drawingContext.PushOpacity(0.3))
            {
                foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    drawingContext.FillRectangle(fill, rect);
            }
        }
    }
}
