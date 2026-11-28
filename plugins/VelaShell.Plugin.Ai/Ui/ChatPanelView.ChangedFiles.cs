using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 输入框上方的「已修改文件」条(仿 VS Code Copilot),以及点开文件后停靠在主窗口标签区的差异文档。
/// </summary>
/// <remarks>
/// <para>
/// 数据来自工具箱的 <c>FileChanged</c> 回调:write / patch / delete / rename 成功后带着前后全文报上来。
/// 回调发生在本轮的异步流里(可能不在 UI 线程),所以先按 <see cref="Cur" /> 记下这一轮属于哪份对话,
/// 再投递回 UI 线程并入那份对话自己的 <see cref="Conversation.ChangedFiles" />;后台跑着的对话改了文件,
/// 不会出现在正看着的这份对话的列表里。新建 / 载入对话时清空;不写进历史。
/// </para>
/// <para>
/// 差异文档按(位置, 路径)去重:已经开着就 <c>ActivateAsync</c> 切过去,文件之后又被改了就原地刷新内容。
/// 老宿主不支持文档标签、或打开时抛了异常,退回成独立窗口(与模型配置等窗口同一套)。
/// </para>
/// </remarks>
public partial class ChatPanelView : IFileDiffHost
{
    /// <summary>差异文档标签上的图标(lucide file-diff)。</summary>
    private static readonly PluginIcon DiffTabIcon = PluginIcon.Stroked(
        "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M9 10h6 M12 7v6 M9 17h6");

    /// <summary>已经开着的差异文档,键见 <see cref="ChangedFileList.KeyOf" />。</summary>
    private readonly Dictionary<string, (IPluginPanel Panel, FileDiffDocumentView View)> _diffDocuments = [with(StringComparer.Ordinal)];

    /// <summary>退回成独立窗口时开出来的那些(面板关闭时一并带走)。</summary>
    private readonly List<IPluginPanel> _diffFallbackWindows = [];

    private bool _changedFilesExpanded;

    /// <summary>
    /// 测试替身:替换「把差异文档开成停靠标签」这一步(例如模拟老宿主抛异常)。正式运行为 null,走 <c>Ui.ShowPanelAsync</c>。
    /// </summary>
    internal Func<PanelOptions, Func<object>, Task<IPluginPanel>>? ShowDocumentOverride { get; set; }

    /// <summary>当前显示的这份对话的已修改文件(测试用)。</summary>
    internal ChangedFileList ActiveChangedFiles => _active.ChangedFiles;

    /// <summary>当前显示的这份对话(测试用)。</summary>
    internal Conversation ActiveConversation => _active;

    /// <summary>
    /// 工具箱报来一条文件改动(可能不在 UI 线程):记下所属对话,投递回 UI 线程合并。
    /// </summary>
    internal void OnToolFileChanged(FileChange change)
    {
        Conversation conv = Cur;
        string conversationId = conv.ConversationId;
        Dispatcher.UIThread.Post(() => ApplyFileChange(conv, conversationId, change));
    }

    /// <summary>在 UI 线程上把改动并进那份对话的列表,挂到工具卡片上,刷新已打开的差异文档。</summary>
    internal void ApplyFileChange(Conversation conv, string conversationId, FileChange change)
    {
        // 这期间那份对话被清掉或换成了别的会话:改动属于已经离开的那段,不再记
        if (!string.Equals(conv.ConversationId, conversationId, StringComparison.Ordinal)) return;
        ChangedFileEntry? entry = conv.ChangedFiles.Add(change);
        conv.ActiveBubble?.AttachFileChange(change);
        if (entry is not null)
        {
            RefreshOpenDocument(ChangedFileList.KeyOf(entry.Location, entry.Path), entry);
            if (change.OldPath is { Length: > 0 } oldPath)
                RefreshOpenDocument(ChangedFileList.KeyOf(entry.Location, oldPath), entry);
        }
        if (ReferenceEquals(conv, _active)) RefreshChangedFiles();
    }

    private void RefreshOpenDocument(string key, ChangedFileEntry entry)
    {
        if (_diffDocuments.TryGetValue(key, out (IPluginPanel Panel, FileDiffDocumentView View) open) && open.Panel.IsOpen)
        {
            open.View.Update(DocumentOf(entry));
        }
    }

    /// <summary>清空当前这份对话的列表(「清除列表」与新建 / 载入对话)。</summary>
    internal void ClearChangedFiles()
    {
        Cur.ChangedFiles.Clear();
        if (IsForeground) RefreshChangedFiles();
    }

    /// <summary>位置的显示文本:本地工作区名 / 远端会话名;认不出来就原样。</summary>
    internal string DescribeLocation(string location)
    {
        if (location.StartsWith("local:", StringComparison.Ordinal))
        {
            string id = location["local:".Length..];
            string name = _settings.LocalWorkspaces.FirstOrDefault(w => w.Id == id)?.Name is { Length: > 0 } n ? n : id;
            return _loc.F("DiffLocationLocal", name);
        }
        if (location.StartsWith("remote:", StringComparison.Ordinal))
        {
            string id = location["remote:".Length..];
            SessionInfo? session = _sessions.Find(s => s.SessionId == id);
            return _loc.F("DiffLocationRemote", session is null ? id : $"{session.Username}@{session.Host}");
        }
        return location;
    }

    private static string FileNameOf(string path)
    {
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string DirectoryOf(string path)
    {
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash > 0 ? normalized[..slash] : slash == 0 ? "/" : "";
    }

    // ---------- 差异文档 ----------

    /// <summary>差异文档能读写的单个文件上限(本地与远端都按它)。</summary>
    private const int DiffEditMaxBytes = 4 * 1024 * 1024;

    /// <summary>最近打开 / 切过去的那个差异标签:再开新的之前先把它切到前面,新标签才落在同一个标签组里。</summary>
    private string? _lastDiffKey;

    /// <summary>
    /// 读写差异文档对应的文件。工作区每次现取:设置里改过、删掉了都跟得上。保存是用户自己的操作,
    /// 所以认全部已配置的工作区(而不只是输入框此刻选中的那个),路径仍由 LocalPathScope 校验必须落在工作区内。
    /// </summary>
    private FileTextStore FileTexts => field ??= new FileTextStore(_context, () => _settings.LocalWorkspaces, DiffEditMaxBytes);

    Task<FileTextReadResult> IFileDiffHost.ReadAsync(string location, string path) => FileTexts.ReadAsync(location, path);

    Task<string?> IFileDiffHost.WriteAsync(string location, string path, string text, bool withBom)
        => FileTexts.WriteAsync(location, path, text, withBom);

    /// <summary>保存草稿只更新发起审阅的记录，其他对话仍用各自的快照核对外部修改。</summary>
    void IFileDiffHost.Saved(FileDiffDocument document, string text)
    {
        if (document.ReviewEntry is not { } entry || !entry.Owner.Entries.Contains(entry)) return;
        entry.After = text;
        entry.Recompute();
        if (entry.Owner == _active.ChangedFiles) RefreshChangedFiles();
    }

    /// <summary>按「已修改文件」里的条目建文档:前后全文都在,能编辑。</summary>
    internal FileDiffDocument DocumentOf(ChangedFileEntry entry) => DocumentOf(entry.Location, entry.Path, entry.OldPath,
        entry.Kind, entry.Before, entry.After, entry.Diff) with
    { ReviewEntry = entry };

    /// <summary>按一条文件改动(或合并后的条目)建文档。</summary>
    internal FileDiffDocument DocumentOf(string location, string path, string? oldPath, FileChangeKind kind,
        string? before, string? after, ToolFileDiff? diff)
    {
        bool full = kind switch
        {
            FileChangeKind.Created => after is not null,
            FileChangeKind.Deleted => before is not null,
            _ => before is not null && after is not null
        };
        return new FileDiffDocument(path, DescribeLocation(location), oldPath, diff)
        {
            RawLocation = location,
            Before = before,
            After = after,
            HasFullText = full,
            Kind = kind
        };
    }

    /// <summary>
    /// 把差异开成停靠文档(已开着就切过去并刷新)。
    /// </summary>
    internal void OpenDiffDocument(string key, FileDiffDocument document) => _ = OpenDiffDocumentAsync(key, document);

    /// <inheritdoc cref="OpenDiffDocument" />
    internal async Task OpenDiffDocumentAsync(string key, FileDiffDocument document)
    {
        if (_diffDocuments.TryGetValue(key, out (IPluginPanel Panel, FileDiffDocumentView View) existing))
        {
            if (existing.Panel.IsOpen)
            {
                existing.View.Update(document);
                _lastDiffKey = key;
                try
                {
                    await existing.Panel.ActivateAsync();
                }
                catch (Exception ex)
                {
                    _context.Log.Warn($"Activating the diff tab failed: {ex.Message}");
                }
                return;
            }
            _diffDocuments.Remove(key);
        }
        string title = _loc.F("DiffTabTitle", FileNameOf(document.Path));
        var view = new FileDiffDocumentView(document, _loc, this);
        var options = new PanelOptions
        {
            Title = title,
            Icon = DiffTabIcon,
            DisplayMode = PanelDisplayMode.Document,
            Placement = PanelPlacement.Tabs
        };
        try
        {
            // 宿主把 Tabs 放进「当前活动标签所在的组」:已经有对比标签开着时先把最近那个切到前面,
            // 新标签就跟它进同一组,而不是落到别处(比如用户刚点过的终端那一组)
            if (_lastDiffKey is { } last && _diffDocuments.TryGetValue(last, out (IPluginPanel Panel, FileDiffDocumentView View) sibling)
                && sibling.Panel.IsOpen)
            {
                await sibling.Panel.ActivateAsync();
            }
            IPluginPanel panel = ShowDocumentOverride is { } show
                ? await show(options, () => view)
                : await _context.Ui.ShowPanelAsync(options, () => view);
            _diffDocuments[key] = (panel, view);
            _lastDiffKey = key;
            // Closed 可能在线程池上回调,清账回 UI 线程
            panel.Closed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_diffDocuments.TryGetValue(key, out (IPluginPanel Panel, FileDiffDocumentView View) open)
                    && ReferenceEquals(open.Panel, panel))
                {
                    _diffDocuments.Remove(key);
                    if (_lastDiffKey == key) _lastDiffKey = _diffDocuments.Keys.LastOrDefault();
                }
            });
        }
        catch (Exception ex)
        {
            // 老宿主不认文档标签、或者开的时候出了错:退回独立窗口,别让一次点击把面板带崩
            _context.Log.Warn($"Opening the diff tab failed, falling back to a window: {ex.Message}");
            IPluginPanel? fallback = null;
            (double width, double height) = FallbackWindowSize();
            await OpenAsync(title, width, height, [], () => view,
                panel =>
                {
                    fallback = panel;
                    _diffFallbackWindows.Add(panel);
                    _fallbackDiffViews.Add(view);
                },
                () =>
                {
                    _fallbackDiffViews.Remove(view);
                    if (fallback is not null) _diffFallbackWindows.Remove(fallback);
                });
        }
    }

    /// <summary>退回窗口时的尺寸占屏幕工作区的比例:对比要并排两栏代码,窗口得够大。</summary>
    internal const double FallbackWindowFraction = 0.9;

    /// <summary>
    /// 退回窗口的尺寸:屏幕工作区(<see cref="WindowFit.Fit" /> 已扣掉任务栏余量)的 <see cref="FallbackWindowFraction" />;
    /// 拿不到屏幕信息(无头测试等)时给 1400×900。之后 <c>OpenAsync</c> 再过一遍 <c>WindowFit.Fit</c>,不会再被缩小。
    /// </summary>
    internal (double Width, double Height) FallbackWindowSize()
    {
        const double probe = 100_000;
        (double width, double height) = WindowFit.Fit(probe, probe, this);
        if (width >= probe || height >= probe) return (1400, 900);
        return (Math.Max(720, Math.Floor(width * FallbackWindowFraction)), Math.Max(480, Math.Floor(height * FallbackWindowFraction)));
    }

    /// <summary>面板关闭时带走退回成窗口的那些差异(停靠文档是主窗口的标签,留给用户自己关)。</summary>
    private void CloseDiffWindows()
    {
        foreach (IPluginPanel panel in _diffFallbackWindows.ToArray())
        {
            _ = panel.CloseAsync();
        }
        _diffFallbackWindows.Clear();
    }

    // ---------- 已修改文件条 ----------

    /// <summary>按当前这份对话重画「已修改文件」条;一个文件都没有时整条隐藏。</summary>
    internal void RefreshChangedFiles()
    {
        ChangedFileList list = _active.ChangedFiles;
        if (_confirmDiscardList != list || list.Entries.Count == 0)
        {
            _confirmDiscardList = null;
            _confirmDiscardEntries = [];
        }
        ChangedFilesBar.IsVisible = list.Entries.Count > 0;
        if (list.Entries.Count == 0)
        {
            ChangedFilesBar.Child = null;
            return;
        }

        var header = new Grid
        {
            Name = "ChangedFilesHeader",
            ColumnDefinitions = [with("Auto,Auto,Auto,Auto,Auto,*,Auto")],
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        AutomationProperties.SetName(header, _loc["ChangedFilesToggle"]);
        var chevron = new Decorator
        {
            Child = MakeIcon(_changedFilesExpanded ? "AiIcon.chevron-down" : "Icon.chevron-right", "VelaTextMuted", 10),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        header.Children.Add(chevron);
        var icon = new Decorator
        {
            Child = MakeIcon("AiIcon.file-diff", "VelaTextTertiary", 12),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        header.Children.Add(icon);
        var title = new TextBlock
        {
            Name = "ChangedFilesTitle",
            Classes = { "changedFilesTitle" },
            Text = list.Entries.Count == 1 ? _loc["ChangedFilesOne"] : _loc.F("ChangedFilesMany", list.Entries.Count)
        };
        Grid.SetColumn(title, 2);
        header.Children.Add(title);
        TextBlock added = DiffView.Text("+" + list.TotalAdded, "VelaStatusConnected");
        added.Name = "ChangedFilesAdded";
        added.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(added, 3);
        header.Children.Add(added);
        TextBlock removed = DiffView.Text("−" + list.TotalRemoved, "VelaError");
        removed.Name = "ChangedFilesRemoved";
        removed.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(removed, 4);
        header.Children.Add(removed);

        header.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _changedFilesExpanded = !_changedFilesExpanded;
            RefreshChangedFiles();
        };

        var rows = new StackPanel { Name = "ChangedFilesList", Spacing = 1 };
        foreach (ChangedFileEntry entry in list.Entries)
        {
            rows.Children.Add(ChangedFileRow(entry));
            if (entry.ReviewError is { } error)
            {
                TextBlock message = DiffView.Text(error, "VelaWarning");
                message.TextWrapping = TextWrapping.Wrap;
                rows.Children.Add(message);
            }
        }
        var scroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 168,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = _changedFilesExpanded,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var stack = new StackPanel();
        var titleRow = new Grid { ColumnDefinitions = [with("*,Auto")] };
        titleRow.Children.Add(header);
        StackPanel actions = BatchActions(list);
        Grid.SetColumn(actions, 1);
        titleRow.Children.Add(actions);
        stack.Children.Add(titleRow);
        stack.Children.Add(scroll);
        ChangedFilesBar.Child = stack;
    }

    private Border ChangedFileRow(ChangedFileEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = [with("Auto,Auto,*,Auto,Auto,Auto")] };
        grid.Children.Add(new Decorator
        {
            Child = MakeIcon(entry.Kind == FileChangeKind.Deleted ? "AiIcon.trash-2" : "AiIcon.file", "VelaTextTertiary", 12),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var name = new TextBlock { Classes = { "changedFileName" }, Text = FileNameOf(entry.Path) };
        if (entry.Kind == FileChangeKind.Deleted) name.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        string where = DirectoryOf(entry.Path);
        var dir = new TextBlock
        {
            Classes = { "changedFileDir" },
            Text = where.Length > 0 ? where : DescribeLocation(entry.Location),
            Margin = new Thickness(8, 0, 8, 0)
        };
        Grid.SetColumn(dir, 2);
        grid.Children.Add(dir);
        if (entry.Diff is not null)
        {
            TextBlock added = DiffView.Text("+" + entry.Added, "VelaStatusConnected");
            Grid.SetColumn(added, 3);
            grid.Children.Add(added);
            TextBlock removed = DiffView.Text("−" + entry.Removed, "VelaError");
            removed.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(removed, 4);
            grid.Children.Add(removed);
        }
        TextBlock badge = DiffView.Text(entry.Badge, entry.Kind switch
        {
            FileChangeKind.Created => "VelaStatusConnected",
            FileChangeKind.Deleted => "VelaError",
            FileChangeKind.Renamed => "VelaInfo",
            _ => "VelaWarning"
        });
        badge.Name = "ChangedFileBadge";
        badge.Width = 14;
        badge.TextAlignment = TextAlignment.Center;
        badge.FontWeight = FontWeight.SemiBold;
        badge.Margin = new Thickness(8, 0, 0, 0);
        string kindText = entry.Kind switch
        {
            FileChangeKind.Created => _loc["ChangedFileKindCreated"],
            FileChangeKind.Deleted => _loc["ChangedFileKindDeleted"],
            FileChangeKind.Renamed => _loc.F("ChangedFileKindRenamed", entry.OldPath ?? ""),
            _ => _loc["ChangedFileKindModified"]
        };
        ToolTip.SetTip(badge, kindText);
        Grid.SetColumn(badge, 5);
        grid.Children.Add(badge);

        var row = new Border
        {
            Classes = { "changedFileRow" },
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = entry
        };
        ToolTip.SetTip(row, $"{entry.Path}\n{DescribeLocation(entry.Location)} · {kindText}");
        AutomationProperties.SetName(row, $"{entry.Path} {kindText}");
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenDiffDocument(ChangedFileList.KeyOf(entry.Location, entry.Path), DocumentOf(entry));
        };
        return row;
    }
}
