using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 差异文档里显示的内容:哪个文件、在哪儿、改动前后。
/// </summary>
/// <param name="Path">给人看的路径。</param>
/// <param name="Location">位置的显示文本(本地工作区名 / 远端会话名);null 不显示。</param>
/// <param name="OldPath">重命名前的路径;没有为 null。</param>
/// <param name="Diff">差异;null 表示拿不到文本(二进制、超出上限或没有捕获到内容)。</param>
internal sealed record FileDiffDocument(string Path, string? Location, string? OldPath, ToolFileDiff? Diff)
{
    /// <summary>位置原值(<c>local:…</c> / <c>remote:…</c>),读写文件用;不知道在哪儿为 null。</summary>
    public string? RawLocation { get; init; }

    internal ChangedFileEntry? ReviewEntry { get; init; }

    /// <summary>改动前的全文(新建为 null)。只有 <see cref="HasFullText" /> 时有意义。</summary>
    public string? Before { get; init; }

    /// <summary>改动后的全文(删除、二进制或超限为 null)。只有 <see cref="HasFullText" /> 时有意义。</summary>
    public string? After { get; init; }

    /// <summary><see cref="Before" /> / <see cref="After" /> 是不是整份文件(而不是补丁片段)。</summary>
    public bool HasFullText { get; init; }

    /// <summary>改动种类。</summary>
    public FileChangeKind Kind { get; init; } = FileChangeKind.Modified;

    /// <summary>右侧能不能编辑并保存:有整份新内容、知道在哪儿、不是删除。</summary>
    public bool CanEdit => HasFullText && RawLocation is not null && After is not null && Kind != FileChangeKind.Deleted;

    /// <summary>只有补丁片段、但知道在哪儿:可以「载入完整文件」。</summary>
    public bool CanLoadFull => !HasFullText && RawLocation is not null && Kind != FileChangeKind.Deleted;
}

/// <summary>差异文档读写文件、通报保存结果的那一头(由聊天面板实现)。</summary>
internal interface IFileDiffHost
{
    /// <summary>读当前全文。</summary>
    Task<FileTextReadResult> ReadAsync(string location, string path);

    /// <summary>写回全文;成功返回 null,失败返回原因(Loc 键或异常消息)。</summary>
    Task<string?> WriteAsync(string location, string path, string text, bool withBom);

    /// <summary>保存成功:更新「已修改文件」里这个文件的 After。</summary>
    void Saved(FileDiffDocument document, string text);

    Task<string?> ReviewAsync(ChangedFileEntry entry, int revision, bool keep);
}

/// <summary>
/// 停靠在主窗口标签区的差异文档(仿 VS Code 的对比编辑器)。
/// </summary>
/// <remarks>
/// <para>
/// <b>并排</b>(默认):两个 AvaloniaEdit 编辑器,左旧右新,都带行号与 TextMate 语法高亮(<see cref="TextMateColorizer" />),
/// 增删行底色由 <see cref="DiffLineBackgroundRenderer" /> 画。右侧是改动后的<b>真实全文</b>,能编辑:
/// 停手约 300ms 重算差异,刷新底色、<c>+N −M</c> 与上一处 / 下一处;两侧滚动同步:未改动的行对齐,
/// 改动块按两侧行数比例摊开,横向照搬(不补空行)。
/// </para>
/// <para>
/// <b>内联</b>:只读的统一差异(<see cref="DiffView" />),反映右侧当前内容;想改就切回并排。
/// </para>
/// <para>
/// <b>保存</b>(工具条按钮或编辑器里的 Ctrl+S):先读磁盘上的当前内容,与本标签的基线(打开时 / 上次保存后的内容)比;
/// 不一样就提示「文件已被其他程序修改」,让人选覆盖或重新载入。写入沿用原文件的 BOM 状态。
/// 用户自己的操作,不走 Agent 审批。
/// </para>
/// </remarks>
internal sealed class FileDiffDocumentView : DockPanel
{
    /// <summary>编辑停手多久后重算差异。</summary>
    internal static readonly TimeSpan RecomputeDelay = TimeSpan.FromMilliseconds(300);

    private readonly Loc _loc;
    private readonly IFileDiffHost? _host;
    private readonly TextBlock _path;
    private readonly TextBlock _location;
    private readonly TextBlock _added;
    private readonly TextBlock _removed;
    private readonly TextBlock _dirtyDot;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Button _revert;
    private readonly Button _loadFull;
    private readonly ToggleButton _sideBySide;
    private readonly ToggleButton _inline;
    private readonly ToggleButton _ignoreWhitespace;
    private readonly Border _conflict;
    private readonly Border _host2 = new();
    private readonly DispatcherTimer _recompute;
    private long _diffRevision;
    private bool _computing;
    // 滚动同步的锚点:两侧未改动行的行顶(以行为单位、从 0 起),下标一一对应;两个锚点之间夹着的改动按比例摊开
    private double[] _oldAnchors = [0];
    private double[] _newAnchors = [0];
    private bool _syncing;
    private TextMateColorizer? _leftColorizer;
    private DiffLineBackgroundRenderer? _leftTint, _rightTint;
    private (string Text, bool HasBom)? _pendingOverwrite;
    private string? _reloadText;

    /// <summary>左侧(旧)编辑器;只在并排模式下有。</summary>
    internal TextEditor? LeftEditor { get; private set; }

    /// <summary>右侧(新)编辑器;只在并排模式下有。</summary>
    internal TextEditor? RightEditor { get; private set; }

    /// <summary>内联模式下的统一差异控件(测试用)。</summary>
    internal DiffView? InlineView { get; private set; }

    /// <summary>当前是否为并排模式。</summary>
    internal bool IsSideBySide => _sideBySide.IsChecked == true;

    /// <summary>当前显示的内容(保存后 After 换成新内容)。</summary>
    internal FileDiffDocument Document { get; private set; }

    /// <summary>右侧当前内容的基线:打开时的 After,或上次保存 / 重新载入后的内容。</summary>
    internal string Baseline { get; private set; } = "";

    /// <summary>右侧有没有未保存的修改。</summary>
    internal bool IsDirty => Document.CanEdit && (RightEditor?.Text ?? _editedRight) is { } draft && !SameText(draft, Baseline);

    /// <summary>当前左右两侧之间的差异(并排模式按编辑器内容重算)。</summary>
    internal LineDiffResult? CurrentDiff { get; private set; }

    /// <summary>各处改动的起点:并排模式是右侧(新)的行号,内联模式是行容器里的下标。</summary>
    internal IReadOnlyList<int> ChangeStarts { get; private set; } = [];

    /// <summary>工具条状态行的文本(测试用)。</summary>
    internal string StatusText => _status.Text ?? "";

    /// <summary>冲突提示条是否开着。</summary>
    internal bool IsConflictShown => _conflict.IsVisible;

    internal FileDiffDocumentView(FileDiffDocument document, Loc loc, IFileDiffHost? host = null)
    {
        _loc = loc;
        _host = host;
        Document = document;
        Name = "FileDiffDocument";
        // 作为独立文档挂在主窗口里,不在 ChatPanelView 的资源作用域下:AiIcon.*、芯片开关主题、编辑器模板都得自己带上
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/AiTheme.axaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        });
        this[!BackgroundProperty] = new DynamicResourceExtension("VelaBgTerminal");
        _recompute = new DispatcherTimer { Interval = RecomputeDelay };
        _recompute.Tick += async (_, _) => await RecomputeEditsAsync();

        var toolbar = new Grid
        {
            ColumnDefinitions = [with("Auto,Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto")],
            Margin = new Thickness(12, 8, 12, 4)
        };
        toolbar.Children.Add(new Decorator
        {
            Child = DiffView.Icon("AiIcon.file-diff", "VelaTextTertiary", 14),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        _path = DiffView.Text("", "VelaTextPrimary");
        _path.Name = "DiffDocPath";
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _path[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
        Grid.SetColumn(_path, 1);
        toolbar.Children.Add(_path);
        // 未保存修改的圆点:贴在路径后面,与 VS Code 标签上的那颗点同一个意思
        _dirtyDot = DiffView.Text("●", "VelaAccent");
        _dirtyDot.Name = "DiffDocDirty";
        _dirtyDot.Margin = new Thickness(6, 0, 0, 0);
        _dirtyDot.IsVisible = false;
        ToolTip.SetTip(_dirtyDot, _loc["DiffUnsaved"]);
        Grid.SetColumn(_dirtyDot, 2);
        toolbar.Children.Add(_dirtyDot);
        _location = DiffView.Text("", "VelaTextTertiary");
        _location.Name = "DiffDocLocation";
        _location.TextTrimming = TextTrimming.CharacterEllipsis;
        _location.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_location, 3);
        toolbar.Children.Add(_location);
        _added = DiffView.Text("", "VelaStatusConnected");
        _added.Name = "DiffDocAdded";
        _added.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_added, 4);
        toolbar.Children.Add(_added);
        _removed = DiffView.Text("", "VelaError");
        _removed.Name = "DiffDocRemoved";
        _removed.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(_removed, 5);
        toolbar.Children.Add(_removed);

        _loadFull = TextButton("DiffLoadFull", _loc["DiffLoadFull"], () => _ = LoadFullFileAsync());
        Grid.SetColumn(_loadFull, 6);
        toolbar.Children.Add(_loadFull);
        _revert = TextButton("DiffRevert", _loc["DiffRevert"], () => _ = RevertOrDiscardAsync());
        Grid.SetColumn(_revert, 7);
        toolbar.Children.Add(_revert);
        _save = TextButton("DiffSave", _loc["DiffSave"], () => _ = SaveAsync());
        ToolTip.SetTip(_save, _loc["DiffSaveTip"]);
        _save.Margin = new Thickness(4, 0, 12, 0);
        Grid.SetColumn(_save, 8);
        toolbar.Children.Add(_save);

        Button previous = NavButton("DiffPreviousChange", "AiIcon.chevron-up", "DiffPrevious", () => Navigate(-1));
        Grid.SetColumn(previous, 9);
        toolbar.Children.Add(previous);
        Button next = NavButton("DiffNextChange", "AiIcon.chevron-down", "DiffNext", () => Navigate(1));
        next.Margin = new Thickness(4, 0, 12, 0);
        Grid.SetColumn(next, 10);
        toolbar.Children.Add(next);

        _sideBySide = ModeToggle("DiffSideBySideToggle", _loc["DiffSideBySide"]);
        _sideBySide.IsChecked = true;
        Grid.SetColumn(_sideBySide, 11);
        toolbar.Children.Add(_sideBySide);
        _inline = ModeToggle("DiffInlineToggle", _loc["DiffInline"]);
        _inline.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(_inline, 12);
        toolbar.Children.Add(_inline);
        _sideBySide.Click += (_, _) => SetMode(sideBySide: true);
        _inline.Click += (_, _) => SetMode(sideBySide: false);
        _ignoreWhitespace = ModeToggle("DiffIgnoreWhitespace", "␠");
        _ignoreWhitespace.Margin = new Thickness(4, 0, 0, 0);
        ToolTip.SetTip(_ignoreWhitespace, _loc["DiffIgnoreWhitespace"]);
        AutomationProperties.SetName(_ignoreWhitespace, _loc["DiffIgnoreWhitespace"]);
        Grid.SetColumn(_ignoreWhitespace, 13);
        toolbar.Children.Add(_ignoreWhitespace);
        _ignoreWhitespace.Click += (_, _) =>
        {
            if (IsSideBySide) Recompute();
            else Rebuild();
        };

        _status = DiffView.Text("", "VelaTextTertiary");
        _status.Name = "DiffDocStatus";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(12, 0, 12, 6);
        _status.IsVisible = false;

        _conflict = BuildConflictBar();

        var divider = new Border { Height = 1 };
        divider[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
        var top = new StackPanel();
        top.Children.Add(toolbar);
        top.Children.Add(_status);
        top.Children.Add(_conflict);
        top.Children.Add(divider);
        SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(_host2);

        ActualThemeVariantChanged += (_, _) => ApplyColorizers();
        Load(document, keepEdits: false);
    }

    // ---------- 内容 ----------

    /// <summary>
    /// 文件又被改了(Agent 再次改动):没有未保存的修改就整份刷新;有的话只换左侧,
    /// 右侧的修改留着 —— 保存时基线对不上,会走冲突提示。
    /// </summary>
    internal void Update(FileDiffDocument document)
    {
        if (IsDirty && document.HasFullText)
        {
            Document = document with { After = Document.After };
            if (LeftEditor is { } left)
            {
                left.Text = document.Before ?? "";
                Recompute();
            }
            else Rebuild();
            ShowStatus(_loc["DiffChangedWhileEditing"]);
            return;
        }
        Load(document, keepEdits: false);
    }

    private void Load(FileDiffDocument document, bool keepEdits)
    {
        Document = document;
        _pendingOverwrite = null;
        _reloadText = null;
        _conflict.IsVisible = false;
        if (!keepEdits)
        {
            Baseline = document.After ?? "";
            _editedRight = null;
        }
        _path.Text = document.OldPath is { Length: > 0 } old ? $"{old} → {document.Path}" : document.Path;
        ToolTip.SetTip(_path, _path.Text);
        _location.Text = document.Location ?? "";
        _location.IsVisible = document.Location is { Length: > 0 };
        _loadFull.IsVisible = document.CanLoadFull && _host is not null;
        Rebuild();
    }

    /// <summary>左 / 右两侧的文本:整份文件就是 Before / After;只有补丁片段时把各处片段用一行 ⋯ 隔开拼起来。</summary>
    private (string Left, string Right) Texts()
    {
        if (Document.HasFullText) return (Document.Before ?? "", Document.After ?? "");
        if (Document.Diff is not { } diff) return ("", "");
        var left = new List<string>();
        var right = new List<string>();
        foreach (LineDiffResult section in diff.Sections)
        {
            if (left.Count > 0)
            {
                left.Add("⋯");
                right.Add("⋯");
            }
            left.AddRange(section.Lines.Where(l => l.Kind != DiffLineKind.Added).Select(l => l.Text));
            right.AddRange(section.Lines.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text));
        }
        return (string.Join('\n', left), string.Join('\n', right));
    }

    private void SetMode(bool sideBySide)
    {
        // 切到内联前把右侧的修改记下来,内联显示的就是当前内容;切回并排时再放回编辑器
        _sideBySide.IsChecked = sideBySide;
        _inline.IsChecked = !sideBySide;
        if (sideBySide && RightEditor is not null || !sideBySide && InlineView is not null) return;
        if (!sideBySide && RightEditor is not null) _editedRight = RightEditor.Text;
        Rebuild();
    }

    /// <summary>并排模式下右侧被改过、切去内联时暂存的内容。</summary>
    private string? _editedRight;

    private void Rebuild()
    {
        DetachEditors();
        InlineView = null;
        _inline.IsChecked = !IsSideBySide;
        if (Document.Diff is null && !Document.HasFullText)
        {
            TextBlock note = DiffView.Text(_loc["DiffUnavailable"], "VelaTextTertiary");
            note.Margin = new Thickness(16);
            note.TextWrapping = TextWrapping.Wrap;
            _host2.Child = note;
            ChangeStarts = [];
            CurrentDiff = null;
            UpdateChrome();
            return;
        }
        (string left, string right) = Texts();
        if (_editedRight is not null && Document.CanEdit) right = _editedRight;
        if (IsSideBySide)
        {
            _editedRight = null;
            _host2.Child = BuildEditors(left, right);
            Recompute();
            if (!Document.CanEdit && Document.HasFullText && Document.Kind != FileChangeKind.Deleted)
            {
                ShowStatus(_loc["DiffNotEditable"]);
            }
        }
        else
        {
            CurrentDiff = LineDiff.Compute(left, right, _ignoreWhitespace.IsChecked == true);
            var diff = new ToolFileDiff(Document.Path, [CurrentDiff], Document.HasFullText);
            InlineView = new DiffView(diff, _loc, DiffViewMode.Window, languagePath: Document.Path) { Margin = new Thickness(12, 8, 12, 12) };
            // 头部的 +N −M 工具条上已经有了,不重复
            InlineView.Children[0].IsVisible = false;
            _host2.Child = InlineView;
            DiffView inline = InlineView;
            ChangeStarts = FindRowStarts(inline.Rows);
            inline.RowsChanged += () => ChangeStarts = FindRowStarts(inline.Rows);
            if (Document.CanEdit) ShowStatus(_loc["DiffInlineReadOnly"]);
            UpdateChrome();
        }
    }

    // ---------- 并排编辑器 ----------

    private Grid BuildEditors(string left, string right)
    {
        TextEditor leftEditor = LeftEditor = Editor("DiffLeftEditor", left, readOnly: true);
        TextEditor rightEditor = RightEditor = Editor("DiffRightEditor", right, readOnly: !Document.CanEdit);
        if (_busy) rightEditor.IsReadOnly = true;
        _leftTint = new DiffLineBackgroundRenderer(() => Brush("VelaError"));
        _rightTint = new DiffLineBackgroundRenderer(() => Brush("VelaStatusConnected"));
        leftEditor.TextArea.TextView.BackgroundRenderers.Add(_leftTint);
        rightEditor.TextArea.TextView.BackgroundRenderers.Add(_rightTint);
        ApplyColorizers();

        rightEditor.Document.TextChanged += (_, _) =>
        {
            _diffRevision++;
            UpdateChrome();
            _recompute.Stop();
            _recompute.Start();
        };
        // Ctrl+S 只在编辑器有焦点时生效(隧道阶段在编辑器自己的按键处理之前截住)。宿主的出厂键位没有占 Ctrl+S。
        rightEditor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        leftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll(leftEditor, rightEditor, _oldAnchors, _newAnchors);
        rightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll(rightEditor, leftEditor, _newAnchors, _oldAnchors);

        // 中间一条可拖动的分割条(拖动调宽、双击回到对半)。比例记在视图上,切内联再切回来、刷新内容都不丢。
        var leftColumn = new ColumnDefinition(LeftShare, GridUnitType.Star) { MinWidth = MinPaneWidth };
        var rightColumn = new ColumnDefinition(1 - LeftShare, GridUnitType.Star) { MinWidth = MinPaneWidth };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(leftColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(rightColumn);
        grid.Children.Add(leftEditor);

        // 看得见的是 1px 的线,抓得住的是整条 6px 宽的分割条;悬停 / 拖动时线换成强调色
        var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        line[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        var splitter = new GridSplitter
        {
            Name = "DiffSplitter",
            ResizeDirection = GridResizeDirection.Columns,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast)
        };
        ToolTip.SetTip(splitter, _loc["DiffSplitterTip"]);
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        void Highlight(bool on) => line[!Border.BackgroundProperty] =
            new DynamicResourceExtension(on ? "VelaAccent" : "VelaBorderPrimary");
        splitter.PointerEntered += (_, _) => Highlight(true);
        splitter.PointerExited += (_, _) => Highlight(false);
        // 拖完时 ActualWidth 还没重新布局,读 GridSplitter 写回的列宽(两列同为 star 或同为像素)
        splitter.DragCompleted += (_, _) =>
        {
            GridLength l = leftColumn.Width, r = rightColumn.Width;
            double lv = l.IsAbsolute || l.IsStar ? l.Value : leftColumn.ActualWidth;
            double rv = r.IsAbsolute || r.IsStar ? r.Value : rightColumn.ActualWidth;
            if (l.GridUnitType != r.GridUnitType)
            {
                lv = leftColumn.ActualWidth;
                rv = rightColumn.ActualWidth;
            }
            if (lv + rv > 0) LeftShare = Math.Clamp(lv / (lv + rv), 0.05, 0.95);
        };
        splitter.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            LeftShare = 0.5;
            leftColumn.Width = new GridLength(0.5, GridUnitType.Star);
            rightColumn.Width = new GridLength(0.5, GridUnitType.Star);
        };

        Grid.SetColumn(rightEditor, 2);
        grid.Children.Add(rightEditor);
        return grid;
    }

    private const double SplitterWidth = 6;
    private const double MinPaneWidth = 120;

    /// <summary>测试用:当前左栏比例。</summary>
    internal double LeftShare { get; private set; } = 0.5;

    private TextEditor Editor(string name, string text, bool readOnly)
    {
        var editor = new TextEditor
        {
            Name = name,
            Document = new TextDocument(text),
            IsReadOnly = readOnly,
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 2)
        };
        editor[!TemplatedControl.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        editor[!TemplatedControl.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
        editor[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        editor[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension("VelaBgInput");
        editor[!TextEditor.LineNumbersForegroundProperty] = new DynamicResourceExtension("VelaTextMuted");
        if (!readOnly)
        {
            if (System.IO.Path.GetExtension(Document.Path).ToLowerInvariant() is ".cs" or ".csx")
                editor.TextArea.IndentationStrategy = new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(editor.Options);
            editor.Options.HighlightCurrentLine = true;
            editor.TextArea.TextView[!AvaloniaEdit.Rendering.TextView.CurrentLineBackgroundProperty] = new DynamicResourceExtension("VelaBgHover");
        }
        AutomationProperties.SetName(editor, name);
        return editor;
    }

    /// <summary>按当前亮暗换一套 TextMate 配色挂到两个编辑器上。</summary>
    private void ApplyColorizers()
    {
        if (LeftEditor is null || RightEditor is null) return;
        TextMateSharp.Grammars.ThemeName theme = TextMateHighlighter.ThemeFor(ActualThemeVariant ?? Application.Current?.ActualThemeVariant);
        _leftColorizer = Swap(LeftEditor, _leftColorizer, theme);
        RightColorizer = Swap(RightEditor, RightColorizer, theme);

        TextMateColorizer? Swap(TextEditor editor, TextMateColorizer? old, TextMateSharp.Grammars.ThemeName name)
        {
            if (old is not null)
            {
                old.Detach();
                editor.TextArea.TextView.LineTransformers.Remove(old);
            }
            if (TextMateHighlighter.ForPath(Document.Path, name) is not { } tokenizer) return null;
            var colorizer = new TextMateColorizer(tokenizer, editor.Document);
            editor.TextArea.TextView.LineTransformers.Add(colorizer);
            editor.TextArea.TextView.Redraw();
            return colorizer;
        }
    }

    /// <summary>右侧(新)编辑器上挂着的着色器(测试用)。</summary>
    internal TextMateColorizer? RightColorizer { get; private set; }

    private void DetachEditors()
    {
        _diffRevision++;
        _recompute.Stop();
        _leftColorizer?.Detach();
        RightColorizer?.Detach();
        _leftColorizer = RightColorizer = null;
        LeftEditor = RightEditor = null;
        _leftTint = _rightTint = null;
    }

    private async Task RecomputeEditsAsync()
    {
        if (_computing) return;
        _recompute.Stop();
        if (LeftEditor is null || RightEditor is null) return;
        var before = LeftEditor.Document.CreateSnapshot();
        var after = RightEditor.Document.CreateSnapshot();
        bool ignoreWhitespace = _ignoreWhitespace.IsChecked == true;
        long revision = _diffRevision;
        _computing = true;
        try
        {
            var diff = await Task.Run(() => LineDiff.Compute(before.Text, after.Text, ignoreWhitespace));
            if (revision == _diffRevision) ApplyDiff(diff);
        }
        finally
        {
            _computing = false;
        }
    }

    internal void Recompute()
    {
        if (LeftEditor is null || RightEditor is null) return;
        _diffRevision++;
        _recompute.Stop();
        ApplyDiff(LineDiff.Compute(LeftEditor.Text, RightEditor.Text, _ignoreWhitespace.IsChecked == true));
    }

    private void ApplyDiff(LineDiffResult diff)
    {
        if (LeftEditor is null || RightEditor is null) return;
        CurrentDiff = diff;
        int oldCount = Math.Max(1, LeftEditor.Document.LineCount);
        int newCount = Math.Max(1, RightEditor.Document.LineCount);
        var oldAnchors = new List<double> { 0 };
        var newAnchors = new List<double> { 0 };
        var removed = new HashSet<int>();
        var added = new HashSet<int>();
        var starts = new List<int>();
        int lastNew = 1;
        bool inChange = false, seenContext = false;
        foreach (DiffLine line in diff.Lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    lastNew = line.NewNumber;
                    seenContext = true;
                    AddAnchor(line.OldNumber - 1, line.NewNumber - 1);
                    inChange = false;
                    break;
                case DiffLineKind.Removed:
                    removed.Add(line.OldNumber);
                    if (!inChange) starts.Add(Math.Min(seenContext ? lastNew + 1 : 1, newCount));
                    inChange = true;
                    break;
                default:
                    added.Add(line.NewNumber);
                    if (!inChange) starts.Add(line.NewNumber);
                    inChange = true;
                    break;
            }
        }
        AddAnchor(oldCount, newCount);
        _oldAnchors = [.. oldAnchors];
        _newAnchors = [.. newAnchors];
        ChangeStarts = starts;
        _leftTint?.Lines = removed;
        _rightTint?.Lines = added;
        var (oldSpans, newSpans) = diff.ChangedSpans();
        _leftTint?.ChangedSpans = oldSpans;
        _rightTint?.ChangedSpans = newSpans;
        LeftEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        RightEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        UpdateChrome();

        // 锚点两侧都必须严格递增:开头就是一段纯新增 / 纯删除时,第一行未改动内容的行顶在一侧还是 0,
        // 跳过它,让那段改动和这一行一起按比例摊开,不至于一侧原地不动、另一侧一下跳过去
        void AddAnchor(double oldTop, double newTop)
        {
            if (oldTop <= oldAnchors[^1] || newTop <= newAnchors[^1]) return;
            oldAnchors.Add(oldTop);
            newAnchors.Add(newTop);
        }
    }

    /// <summary>
    /// 一侧滚动后把另一侧带过去(仿 VS Code / Visual Studio 的对比视图):纵向按锚点映射 ——
    /// 未改动的行两边对齐,夹在中间的改动按两侧行数比例摊开,滚过一段新增时另一侧慢慢走、不跳;横向照搬。
    /// </summary>
    /// <remarks>
    /// 不用 <c>TextEditor.ScrollToVerticalOffset</c>:AvaloniaEdit 12.0.0 里它和 <c>ScrollToHorizontalOffset</c>
    /// 都是空方法(只调了 <c>ApplyTemplate</c>),调了什么也不动。直接设编辑器模板里 ScrollViewer 的 Offset ——
    /// 它同步推到 TextView,另一侧的 ScrollOffsetChanged 在 <see cref="_syncing" /> 护罩里就地回来,不会来回拉扯。
    /// </remarks>
    private void SyncScroll(TextEditor source, TextEditor target, double[] from, double[] to)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            Vector offset = source.TextArea.TextView.ScrollOffset;
            double lines = MapAcross(ToLines(source, offset.Y), from, to);
            SetScrollOffset(target, new Vector(offset.X, Math.Max(0, ToPixels(target, lines))));
        }
        catch (Exception)
        {
            // 布局还没好(刚建出来、不在可视树上)时取不到行位置:这次不同步
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 按锚点把一侧的位置(以行为单位,从 0 起,可带小数)换算到另一侧:落在两个锚点之间就线性插值,
    /// 越过最后一个锚点就一比一往后顺延。
    /// </summary>
    internal static double MapAcross(double position, double[] from, double[] to)
    {
        int count = Math.Min(from.Length, to.Length);
        if (count == 0) return position;
        if (position <= from[0]) return to[0] + (position - from[0]);
        if (position >= from[count - 1]) return to[count - 1] + (position - from[count - 1]);
        int index = Array.BinarySearch(from, 0, count, position);
        if (index >= 0) return to[index];
        int upper = ~index, lower = upper - 1;
        double share = (position - from[lower]) / (from[upper] - from[lower]);
        return to[lower] + share * (to[upper] - to[lower]);
    }

    /// <summary>纵向像素偏移 → 行(从 0 起,小数部分是这一行被卷上去的比例)。</summary>
    private static double ToLines(TextEditor editor, double y)
    {
        AvaloniaEdit.Rendering.TextView view = editor.TextArea.TextView;
        double height = view.DefaultLineHeight;
        if (height <= 0 || view.GetDocumentLineByVisualTop(Math.Max(0, y)) is not { } line) return 0;
        double within = (y - view.GetVisualTopByDocumentLine(line.LineNumber)) / height;
        return line.LineNumber - 1 + Math.Clamp(within, 0, 1);
    }

    /// <summary>行(从 0 起,可带小数)→ 纵向像素偏移。</summary>
    private static double ToPixels(TextEditor editor, double lines)
    {
        AvaloniaEdit.Rendering.TextView view = editor.TextArea.TextView;
        int index = (int)Math.Clamp(Math.Floor(lines), 0, Math.Max(0, editor.Document.LineCount - 1));
        return view.GetVisualTopByDocumentLine(index + 1) + (lines - index) * view.DefaultLineHeight;
    }

    /// <summary>设编辑器的滚动位置(越界由 ScrollViewer 自己收住)。</summary>
    private static void SetScrollOffset(TextEditor editor, Vector offset)
    {
        if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll) scroll.Offset = offset;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta))
        {
            e.Handled = true;
            _ = SaveAsync();
        }
    }

    // ---------- 工具条状态 ----------

    private void UpdateChrome()
    {
        int added = CurrentDiff?.Added ?? Document.Diff?.Added ?? 0;
        int removed = CurrentDiff?.Removed ?? Document.Diff?.Removed ?? 0;
        _added.Text = "+" + added;
        _removed.Text = "−" + removed;
        _added.IsVisible = _removed.IsVisible = CurrentDiff is not null || Document.Diff is not null;
        bool dirty = IsDirty;
        _dirtyDot.IsVisible = dirty;
        bool review = !dirty && Document.ReviewEntry is { } entry && entry.Owner.Entries.Contains(entry);
        _save.IsVisible = _revert.IsVisible = (Document.CanEdit || review) && _host is not null;
        _save.IsEnabled = _revert.IsEnabled = !_busy && (dirty || review);
        _save.Content = _loc[review ? "DiffKeep" : "DiffSave"];
        _revert.Content = _loc[review ? "DiffDiscard" : "DiffRevert"];
        AutomationProperties.SetName(_save, (string)_save.Content);
        AutomationProperties.SetName(_revert, (string)_revert.Content);
        ToolTip.SetTip(_save, review ? _save.Content : _loc["DiffSaveTip"]);
    }

    private bool _busy;
    internal bool IsBusy => _busy;

    internal void SetBusy(bool busy)
    {
        _busy = busy;
        if (RightEditor is { } right) right.IsReadOnly = busy || !Document.CanEdit;
        UpdateChrome();
    }

    internal void FinishReview(ChangedFileEntry entry, bool keep)
    {
        if (Document.ReviewEntry != entry) return;
        if (keep) Document = Document with { ReviewEntry = null };
        else
        {
            var restored = new FileDiffDocument(entry.OldPath ?? entry.Path, Document.Location, null, null)
            { Before = entry.Before, After = entry.Before, HasFullText = entry.Before is not null };
            Load(restored, keepEdits: false);
        }
        ShowStatus(_loc[keep ? "DiffKept" : "DiffDiscarded"]);
        UpdateChrome();
    }

    private async Task ReviewAsync(bool keep)
    {
        if (_host is null || Document.ReviewEntry is not { } entry) return;
        string? error = await _host.ReviewAsync(entry, entry.Revision, keep);
        if (error is not null) ShowStatus(ErrorText(error));
    }

    internal async Task RevertOrDiscardAsync()
    {
        if (_busy) return;
        if (IsDirty) Revert();
        else await ReviewAsync(keep: false);
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
    }

    private string ErrorText(string error) => error switch
    {
        FileTextStore.ErrorSessionGone or FileTextStore.ErrorPathRejected or FileTextStore.ErrorNotText or "DiffReviewConflict" or "DiffReviewUnavailable" or "DiffReviewDraft" => _loc[error],
        _ => _loc.F("DiffSaveFailed", error)
    };

    // ---------- 保存 / 还原 / 载入完整文件 ----------

    /// <summary>
    /// 保存右侧内容:先读磁盘当前内容,与基线不一致就弹冲突提示(覆盖 / 重新载入 / 取消),一致才写。
    /// </summary>
    internal async Task SaveAsync()
    {
        if (_busy || _host is null) return;
        if (!IsDirty)
        {
            await ReviewAsync(keep: true);
            return;
        }
        SetBusy(true);
        try { await SaveDraftAsync(); }
        finally { SetBusy(false); }
    }

    private async Task SaveDraftAsync()
    {
        if (_host is null || !Document.CanEdit || Document.RawLocation is not { } location) return;
        string text = RightEditor?.Text ?? _editedRight ?? Baseline;
        FileTextReadResult current = await _host.ReadAsync(location, Document.Path);
        if (current.Error is { } readError)
        {
            ShowStatus(ErrorText(readError));
            return;
        }
        if (current.Text is null || !SameText(current.Text, Baseline))
        {
            _pendingOverwrite = (text, current.HasBom);
            _reloadText = current.Text;
            _conflict.IsVisible = true;
            ShowStatus("");
            return;
        }
        await WriteAsync(text, current.HasBom);
    }

    private async Task WriteAsync(string text, bool hasBom)
    {
        if (_host is null || Document.RawLocation is not { } location) return;
        // 换行照原文件:编辑器里粘贴进来的、或在 Windows 上新敲的行可能是另一种换行,
        // 不统一的话一个 LF 的远端配置文件保存后会混进 CRLF
        text = FileTextStore.MatchLineEndings(text, Baseline);
        string? error = await _host.WriteAsync(location, Document.Path, text, hasBom);
        if (error is not null)
        {
            ShowStatus(ErrorText(error));
            return;
        }
        Baseline = text;
        Document = Document with { After = text, Diff = ToolDiff.FromTexts(Document.Path, Document.Before, text, Document.Kind) };
        _host.Saved(Document, text);
        _conflict.IsVisible = false;
        _pendingOverwrite = null;
        ShowStatus(_loc["DiffSaved"]);
        UpdateChrome();
    }

    /// <summary>冲突提示里选「覆盖」:不管磁盘上现在是什么,写入右侧内容。</summary>
    internal async Task OverwriteAsync()
    {
        if (_pendingOverwrite is not { } pending) return;
        await WriteAsync(pending.Text, pending.HasBom);
    }

    /// <summary>冲突提示里选「重新载入」:右侧换成磁盘上的当前内容,作为新的基线(放弃本标签里的修改)。</summary>
    internal void ReloadFromDisk()
    {
        string text = _reloadText ?? "";
        Baseline = text;
        Document = Document with { After = text };
        _editedRight = text;
        if (RightEditor is { } editor) editor.Text = text;
        else Rebuild();
        _conflict.IsVisible = false;
        _pendingOverwrite = null;
        ShowStatus(_loc["DiffReloaded"]);
        Recompute();
    }

    /// <summary>还原:右侧回到基线。</summary>
    internal void Revert()
    {
        if (!Document.CanEdit) return;
        if (RightEditor is null)
        {
            _editedRight = Baseline;
            Rebuild();
        }
        else RightEditor.Text = Baseline;
        _conflict.IsVisible = false;
        _pendingOverwrite = null;
        ShowStatus("");
        Recompute();
    }

    /// <summary>
    /// 只有补丁片段时「载入完整文件」:读当前全文作为右侧与基线;左侧用补丁片段从当前全文反推改动前的内容,
    /// 反推不出来(片段找不到、不止一处、纯删除)就显示当前内容,并提示一句。
    /// </summary>
    internal async Task LoadFullFileAsync()
    {
        if (_host is null || Document.RawLocation is not { } location) return;
        FileTextReadResult current = await _host.ReadAsync(location, Document.Path);
        if (current.Error is { } || current.Text is null)
        {
            ShowStatus(current.Error is { } e ? ErrorText(e) : _loc["DiffSaveMissing"]);
            return;
        }
        string? before = Document.Diff is { Edits.Count: > 0 } diff ? ToolDiff.ReverseApply(current.Text, diff.Edits) : null;
        Load(Document with
        {
            HasFullText = true,
            Before = before ?? current.Text,
            After = current.Text,
            Diff = ToolDiff.FromTexts(Document.Path, before ?? current.Text, current.Text, FileChangeKind.Modified)
        }, keepEdits: false);
        ShowStatus(before is null ? _loc["DiffLoadedNoBefore"] : "");
    }

    private Border BuildConflictBar()
    {
        TextBlock message = DiffView.Text(_loc["DiffConflict"], "VelaWarning");
        message.TextWrapping = TextWrapping.Wrap;
        Button overwrite = TextButton("DiffConflictOverwrite", _loc["DiffOverwrite"], () => _ = OverwriteAsync());
        Button reload = TextButton("DiffConflictReload", _loc["DiffReload"], ReloadFromDisk);
        Button cancel = TextButton("DiffConflictCancel", _loc["Cancel"], () =>
        {
            _conflict.IsVisible = false;
            _pendingOverwrite = null;
        });
        var grid = new Grid { ColumnDefinitions = [with("*,Auto,Auto,Auto")] };
        grid.Children.Add(message);
        Grid.SetColumn(overwrite, 1);
        grid.Children.Add(overwrite);
        reload.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(reload, 2);
        grid.Children.Add(reload);
        cancel.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(cancel, 3);
        grid.Children.Add(cancel);
        var bar = new Border
        {
            Name = "DiffConflictBar",
            Child = grid,
            Padding = new Thickness(10, 6),
            Margin = new Thickness(12, 0, 12, 6),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            IsVisible = false
        };
        bar[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaWarning");
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        return bar;
    }

    // ---------- 导航 ----------

    /// <summary>内联模式:每一段连续改动的第一行(在行容器里的下标)。</summary>
    private static List<int> FindRowStarts(StackPanel rows)
    {
        var starts = new List<int>();
        bool inChange = false;
        for (int i = 0; i < rows.Children.Count; i++)
        {
            bool change = rows.Children[i].Classes.Contains("diffAdded") || rows.Children[i].Classes.Contains("diffRemoved");
            if (change && !inChange) starts.Add(i);
            inChange = change;
        }
        return starts;
    }

    /// <summary>跳到下一处(<paramref name="direction" /> = 1)或上一处(-1)改动;到头了就停在那儿。</summary>
    internal void Navigate(int direction)
    {
        if (ChangeStarts.Count == 0) return;
        if (RightEditor is { } right)
        {
            AvaloniaEdit.Rendering.TextView view = right.TextArea.TextView;
            int top = view.GetDocumentLineByVisualTop(right.VerticalOffset)?.LineNumber ?? 1;
            // 跳过去时上面留两行上下文,所以「当前」按留白之后的那一行算
            int anchor = top + (right.VerticalOffset > 0 ? 2 : 0);
            int? target = direction > 0
                ? ChangeStarts.Where(l => l > anchor).Cast<int?>().FirstOrDefault()
                : ChangeStarts.Where(l => l < anchor).Cast<int?>().LastOrDefault();
            if (target is { } line)
            {
                double y = view.GetVisualTopByDocumentLine(Math.Max(1, line - 2));
                SetScrollOffset(right, new Vector(right.HorizontalOffset, line <= 3 ? 0 : y));
                right.TextArea.Caret.Line = line;
            }
            return;
        }
        if (InlineView is null) return;
        StackPanel rows = InlineView.Rows;
        ScrollViewer scroll = InlineView.Scroll;
        double current = scroll.Offset.Y;
        double? to = null;
        IEnumerable<int> order = direction > 0 ? ChangeStarts : ChangeStarts.Reverse();
        foreach (int index in order)
        {
            double y = rows.Children[Math.Max(0, index - 2)].Bounds.Y;
            if (direction > 0 ? y > current + 1 : y < current - 1)
            {
                to = y;
                break;
            }
        }
        if (to is { } t) scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, t));
    }

    // ---------- 小部件 ----------

    private IBrush? Brush(string key)
        => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : null;

    private static bool SameText(string? a, string? b)
        => string.Equals(a?.ReplaceLineEndings("\n"), b?.ReplaceLineEndings("\n"), StringComparison.Ordinal);

    private Button NavButton(string name, string icon, string tipKey, Action click)
    {
        var button = new Button
        {
            Name = name,
            Content = DiffView.Icon(icon, "VelaTextSecondary", 12),
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        ToolTip.SetTip(button, _loc[tipKey]);
        AutomationProperties.SetName(button, _loc[tipKey]);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button TextButton(string name, string text, Action click)
    {
        var button = new Button
        {
            Name = name,
            Content = text,
            Height = 24,
            Padding = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        button[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    private static ToggleButton ModeToggle(string name, string text)
    {
        var toggle = new ToggleButton { Name = name, Content = text, VerticalAlignment = VerticalAlignment.Center };
        toggle[!ThemeProperty] = new DynamicResourceExtension("AiChipToggleTheme");
        return toggle;
    }
}
