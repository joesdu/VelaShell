using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using TextMateSharp.Grammars;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>团队控制台向聊天面板要的那几样东西:文案、模型配置、链接怎么开。</summary>
internal interface ITeamConsoleHost
{
    /// <summary>当前语言的文案表(与聊天面板共用一份,切语言时一起变)。</summary>
    Loc Loc { get; }

    /// <summary>按模型配置 id 找模型(显示名、单价、上下文窗口);找不到为 null。</summary>
    ResolvedModel? FindModel(string? modelId);

    /// <summary>成员跑这个模型时实际用的思考强度的短标签;不可调或「自动」为空串。</summary>
    string ReasoningLabel(ResolvedModel model);

    /// <summary>报告里的链接被点了。</summary>
    void OpenLink(LinkClickedEventArgs e);
}

/// <summary>
/// 团队控制台:停靠在主窗口标签区的一整页,把一个 Agent 团队摊开来看。
/// </summary>
/// <remarks>
/// <para>
/// 自上而下:头部(目标、阶段、已运行多久)→ 六块统计 → 团队拓扑与数据流(图下是最新一条通信)| 右栏「分工 / 通信」→
/// 任务进度与归属(时间轴)| Token 消耗;两行的左右两栏同宽、上下对齐。点成员、任务、连线或某条消息,右侧滑出抽屉看详情,
/// 抽屉里可以一层层点进去,左上角返回。
/// </para>
/// <para>
/// 数据全部来自 <see cref="TeamConsoleSession" />(聊天面板里那张入口卡看的是同一份):团队在跑时随快照刷新,
/// 这一轮收尾后停在最后一版;从历史里翻出来的团队是一张静态的终态。
/// </para>
/// <para>
/// 页面是独立文档,不在聊天面板的资源作用域下:图标、Markdown 皮肤与本页的版式都得自己带上
/// (与 <see cref="FileDiffDocumentView" /> 同理)。配色全部绑 <c>Vela*</c> 令牌,跟着宿主主题走。
/// </para>
/// </remarks>
internal sealed partial class TeamConsoleView : UserControl
{
    /// <summary>页面内容区不窄于这个宽度时,拓扑 / 成员、任务 / 用量各自并排两栏;再窄就上下叠放。</summary>
    internal const double WideBreakpoint = 1150;

    /// <summary>页面内容的最大宽度(超宽屏上居中,不无限拉长)。</summary>
    private const double MaxContentWidth = 1640;

    /// <summary>按模型分布时各模型的颜色(按出现顺序轮换)。</summary>
    private static readonly string[] ModelPalette =
        ["VelaAccent", "VelaInfo", "VelaShellGreen", "VelaShellYellow", "VelaShellMagenta"];

    private readonly TeamConsoleSession _session;
    private readonly ITeamConsoleHost _host;
    private readonly Loc _loc;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _page = new() { Spacing = 16, MaxWidth = MaxContentWidth };
    private readonly Grid _topRow = new(), _bottomRow = new();
    private readonly Decorator _liveHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _subtitle;
    private readonly Border _graphPanel = null!;
    private readonly Border _rosterPanel = null!;
    private readonly Border _ganttPanel = null!;
    private readonly Border _tokensPanel = null!;
    private string _liveKey = "";
    private bool _dirty = true;
    private bool? _wide;
    private int _seenMessages = -1;
    private ThemeName _codeTheme = ThemeName.DarkPlus;

    private TeamBoardSnapshot Team => _session.Team;

    private IReadOnlyList<SubagentSnapshot> Runs => _session.Runs;

    /// <summary>这一页看的是哪个团队。</summary>
    internal string TeamId => _session.Team.TeamId;

    internal TeamConsoleView(TeamConsoleSession session, ITeamConsoleHost host)
    {
        _session = session;
        _host = host;
        _loc = host.Loc;
        Name = "TeamConsole";
        Focusable = true;
        FocusAdorner = null; // 页面自己拿焦点只是为了接 Esc,不该画一圈焦点框
        ClipToBounds = true; // 关着的抽屉停在右缘之外,靠这一条裁掉

        // 图标、LiveMarkdown 的令牌与样式、Markdown 皮肤和本页的版式都在这一份编译好的样式表里(为什么不能在这里按地址并入,见该文件开头)
        Styles.Add(new TeamConsoleStyles());
        Styles.Add(MarkdownSkin.LocalImagesOnly());
        Resources["AiCopyTip"] = _loc["Copy"];
        Resources["AiWrapTip"] = _loc["ToggleWrap"];
        this[!BackgroundProperty] = new DynamicResourceExtension("VelaBgPage");
        this[!FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");

        _subtitle = TeamUi.Line("", "VelaTextSecondary", 12);
        Control header = BuildHeader();

        BuildStats();
        _graphPanel = BuildGraphPanel();
        _rosterPanel = BuildRailPanel();
        _ganttPanel = BuildGanttPanel();
        _tokensPanel = BuildTokensPanel();
        _page.Children.Add(_stats);
        _page.Children.Add(_topRow);
        _page.Children.Add(_bottomRow);

        _scroll = new ScrollViewer
        {
            Name = "TeamConsoleScroll",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(20, 16, 20, 40), Child = _page }
        };
        var root = new Grid { RowDefinitions = [with("Auto,*")] };
        root.Children.Add(header);
        root.Children.Add(_scroll.At(0, 1));
        Control drawer = BuildDrawer();
        Grid.SetRowSpan(drawer, 2);
        root.Children.Add(drawer);
        Content = root;

        ApplyLayout(wide: true);
        SizeChanged += (_, e) => OnWidthChanged(e.NewSize.Width);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsDrawerOpen)
            {
                e.Handled = true;
                CloseDrawer();
            }
        };
        _refreshTimer.Tick += (_, _) => RefreshIfDirty();
        ActualThemeVariantChanged += (_, _) => SyncSkin();
        Refresh();
    }

    // ---------- 生命周期 ----------

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _session.Changed += OnSessionChanged;
        SyncSkin();
        _dirty = true;
        RefreshIfDirty();
        _refreshTimer.Start();
        UpdateGraphClock();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 标签页被关掉或切走时不再跟着快照重画,动画的钟也停掉;切回来时补一次
        _session.Changed -= OnSessionChanged;
        _refreshTimer.Stop();
        _drawerSettle.Stop();
        StopGraphClock();
    }

    private void OnSessionChanged()
    {
        _dirty = true;
        // 新消息立刻画:连线上的光点要跟消息同步出现,不等下一拍
        if (Team.Messages.Count != _seenMessages) RefreshIfDirty();
    }

    private void RefreshIfDirty()
    {
        if (!_dirty) return;
        Refresh();
    }

    private void SyncSkin()
    {
        _codeTheme = MarkdownSkin.Sync(this);
        MarkdownSkin.ApplyCodeTheme(this, _codeTheme);
    }

    /// <summary>按当前快照把整页刷新一遍(各分区只改变了的部分)。</summary>
    internal void Refresh()
    {
        _dirty = false;
        _seenMessages = Team.Messages.Count;
        RefreshHeader();
        RefreshStats();
        RefreshGraph();
        RefreshRoster();
        RefreshGantt();
        RefreshTokens();
        RefreshFeed(force: false);
        RefreshDrawer();
    }

    // ---------- 头部 ----------

    private Border BuildHeader()
    {
        // 方形标:强调色底 + 团队图标(与聊天里团队入口、标签页图标是同一个)
        Border logo = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Child = new Decorator { Child = TeamUi.Icon("AiIcon.users", "VelaBgPage", 18), HorizontalAlignment = HorizontalAlignment.Center }
        }.Bind(Border.BackgroundProperty, "VelaAccent");

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(TeamUi.Text(_loc["TeamConsoleTitle"], "VelaTextPrimary", 15, FontWeight.SemiBold));
        titleRow.Children.Add(_liveHost);
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(titleRow);
        titles.Children.Add(_subtitle);

        var grid = new Grid { ColumnDefinitions = [with("Auto,*")], ColumnSpacing = 12 };
        grid.Children.Add(logo);
        grid.Children.Add(titles.At(1));
        return new Border
        {
            Name = "TeamConsoleHeader",
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 12),
            Child = grid
        }.Bind(Border.BackgroundProperty, "VelaBgPage").Bind(Border.BorderBrushProperty, "VelaBorderPrimary");
    }

    private void RefreshHeader()
    {
        (string key, string color) = Team.Phase switch
        {
            "completed" => ("TeamPhaseCompleted", "VelaStatusConnected"),
            "failed" => ("TeamPhaseFailed", "VelaWarning"),
            "cancelled" => ("Cancelled", "VelaWarning"),
            _ => ("TeamConsoleLive", "VelaStatusConnected")
        };
        if (key != _liveKey)
        {
            _liveKey = key;
            _liveHost.Child = TeamUi.Pill(_loc[key], color);
        }
        _subtitle.Text = _loc.F("TeamConsoleSubtitle", ChatFormatting.OneLine(Team.Goal, 200), ChatFormatting.Duration(Team.Elapsed));
        ToolTip.SetTip(_subtitle, Team.Goal);
    }

    // ---------- 版式 ----------

    /// <summary>当前是否为宽屏的并排两栏(测试用)。</summary>
    internal bool IsWide => _wide == true;

    private void OnWidthChanged(double width)
    {
        if (width <= 0) return;
        ApplyLayout(width >= WideBreakpoint);
        _stats.Columns = width >= 1000 ? 6 : width >= 560 ? 3 : 2;
        ResizeDrawer(width);
    }

    private void ApplyLayout(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        // 上下两行用同一个比例,右栏的两张卡左右边缘对齐
        Arrange(_topRow, _graphPanel, _rosterPanel, wide, ColumnRatio);
        Arrange(_bottomRow, _ganttPanel, _tokensPanel, wide, ColumnRatio);
        FitRail(wide);
    }

    /// <summary>宽屏并排时左栏与右栏的宽度比(上下两行共用,右栏对齐)。</summary>
    private const double ColumnRatio = 1.6;

    /// <summary>两块面板:宽屏左右并排(左边占 <paramref name="ratio" /> 份、右边 1 份),窄屏上下叠放。</summary>
    private static void Arrange(Grid grid, Control first, Control second, bool wide, double ratio)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(ratio, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
            grid.ColumnSpacing = 16;
            grid.RowSpacing = 0;
            grid.Children.Add(first.At(0));
            grid.Children.Add(second.At(1));
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.ColumnSpacing = 0;
            grid.RowSpacing = 16;
            grid.Children.Add(first.At(0, 0));
            grid.Children.Add(second.At(0, 1));
        }
    }

    /// <summary>一块面板:头行(标题 + 灰色小字说明 + 右端的附件)与正文。</summary>
    private static Border Panel(string title, string? hint, Control? trailing, Control body, string name)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(TeamUi.Text(title, "VelaTextPrimary", 13, FontWeight.SemiBold));
        if (hint is { Length: > 0 }) heading.Children.Add(TeamUi.Line(hint, "VelaTextSecondary", 12));
        var head = new Grid { ColumnDefinitions = [with("*,Auto")], ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(heading);
        if (trailing is not null) head.Children.Add(trailing.At(1));
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        return new Border { Name = name, Classes = { "tcPanel" }, Child = stack };
    }

    // ---------- 人、状态、类型的外观 ----------

    /// <summary>消息或关系的一端:成员、主控,或者「全体」「引擎」。</summary>
    private sealed record Party(string Id, string Name, string Role, TeamFace Face, string ColorKey, TeamMemberInfo? Member);

    private Party PartyOf(string id)
    {
        if (Team.Members.FirstOrDefault(m => m.Name == id) is { } member)
        {
            string color = ChatPanelView.MemberColor(member.Color);
            return new Party(id, member.Name, member.Role, TeamFace.Member(color, member.Color), color, member);
        }
        return id switch
        {
            TeamConsoleModel.Lead => new Party(id, _loc["TeamLead"], _loc["TeamLeadRole"], TeamFace.Lead, "VelaTextSecondary", null),
            "all" => new Party(id, _loc["TeamAll"], "", TeamFace.Neutral, "VelaTextTertiary", null),
            "engine" => new Party(id, _loc["TeamEngine"], "", TeamFace.Neutral, "VelaTextTertiary", null),
            _ => new Party(id, id, "", TeamFace.Neutral, "VelaTextTertiary", null)
        };
    }

    private Viewbox Avatar(string partyId, double size) => TeamAvatar.Create(PartyOf(partyId).Face, size);

    private static (string Key, string Color) StateLook(TeamMemberState state) => state switch
    {
        TeamMemberState.Working => ("TeamStateWorking", "VelaStatusConnected"),
        TeamMemberState.Thinking => ("TeamStateThinking", "VelaInfo"),
        TeamMemberState.Waiting => ("TeamStateWaiting", "VelaWarning"),
        TeamMemberState.Blocked => ("TeamStateBlocked", "VelaError"),
        TeamMemberState.Online => ("TeamStateOnline", "VelaAccent"),
        _ => ("TeamStateIdle", "VelaTextTertiary")
    };

    private TeamMemberState StateOf(string partyId)
        => Team.Members.FirstOrDefault(m => m.Name == partyId) is { } member
            ? TeamConsoleModel.StateOf(member, Team, Runs)
            : TeamConsoleModel.LeadState(Team);

    private Panel StatePill(string partyId)
    {
        (string key, string color) = StateLook(StateOf(partyId));
        return TeamUi.Pill(_loc[key], color);
    }

    /// <summary>任务状态的文案与颜色。审查任务跑完但结论是不通过,按警告显示。</summary>
    private static (string Key, string Color) TaskLook(TeamTaskInfo task) => task.Status switch
    {
        "completed" when task.Verifies is not null && task.Verdict == "FAIL" => ("TeamVerdictFail", "VelaWarning"),
        "completed" => ("SubagentCompleted", "VelaStatusConnected"),
        "running" => ("SubagentRunning", "VelaInfo"),
        "failed" => ("SubagentFailed", "VelaError"),
        "blocked" => ("TeamBlocked", "VelaWarning"),
        "cancelled" => ("Cancelled", "VelaWarning"),
        _ => ("TeamTaskPending", "VelaTextTertiary")
    };

    private Panel TaskPill(TeamTaskInfo task)
    {
        (string key, string color) = TaskLook(task);
        return TeamUi.Pill(_loc[key], color);
    }

    /// <summary>消息类型的文案与颜色。</summary>
    private static (string Key, string Color) KindLook(string kind) => kind switch
    {
        "assign" => ("TeamMsgAssign", "VelaAccent"),
        "handoff" => ("TeamMsgHandoff", "VelaInfo"),
        "peer" => ("TeamMsgPeer", "VelaShellMagenta"),
        "report" => ("TeamMsgReport", "VelaStatusConnected"),
        "review" => ("TeamMsgReview", "VelaWarning"),
        _ => ("TeamMsgSystem", "VelaTextTertiary")
    };

    private Panel KindTag(string kind)
    {
        (string key, string color) = KindLook(kind);
        return TeamUi.Tag(_loc[key], color);
    }

    /// <summary>关系种类的图例文案与颜色。</summary>
    private static (string Key, string Color) EdgeLook(string kind) => kind switch
    {
        "handoff" => ("TeamEdgeHandoff", "VelaInfo"),
        "collab" => ("TeamEdgeCollab", "VelaShellMagenta"),
        "review" => ("TeamEdgeReview", "VelaWarning"),
        _ => ("TeamEdgeAssign", "VelaAccent")
    };

    /// <summary>连线上的短标签:交付写交的是哪个任务,其余写关系本身。</summary>
    private string EdgeLabel(TeamEdge edge) => edge.Kind switch
    {
        "handoff" => edge.Subject is { Length: > 0 } subject ? ChatFormatting.OneLine(subject, 8) : _loc["TeamMsgHandoff"],
        "collab" => _loc["TeamMsgPeer"],
        "review" => _loc["TeamMsgReview"],
        _ => _loc["TeamMsgAssign"]
    };

    // ---------- 用量与模型 ----------

    private TeamMemberUsage UsageOf(TeamMemberInfo member) => TeamConsoleModel.UsageOf(member, Team, Runs);

    /// <summary>成员名下的预估花费:每次运行各按自己实际用的模型的单价算;一个单价都没填为 null。</summary>
    private double? CostOf(TeamMemberInfo member)
    {
        double? total = null;
        foreach (TeamTaskInfo task in Team.Tasks.Where(t => t.Assignee == member.Name))
        {
            foreach (SubagentSnapshot run in TeamConsoleModel.RunsOf(task, Runs))
            {
                if (CostOf(run, member) is { } cost) total = (total ?? 0) + cost;
            }
        }
        return total;
    }

    private double? CostOf(SubagentSnapshot run, TeamMemberInfo? member)
        => _host.FindModel(run.ModelId ?? member?.ModelId) is { } model
            ? ChatPanelView.EstimateCost(model, run.InputTokens, run.CachedInputTokens, run.OutputTokens)
            : null;

    /// <summary>模型的显示名;没配置(或配置已删)就写 id。</summary>
    private string ModelName(string? modelId)
        => modelId is null ? "" : _host.FindModel(modelId)?.Name ?? modelId;

    /// <summary>「模型 · 思考强度」。</summary>
    private string ModelLine(string? modelId)
    {
        if (modelId is null) return "";
        if (_host.FindModel(modelId) is not { } model) return modelId;
        string reasoning = _host.ReasoningLabel(model);
        return model.Name + (reasoning.Length > 0 ? " · " + reasoning : "");
    }

    /// <summary>上下文占用(0–1);不知道窗口多大或还没有采样为 null。</summary>
    private double? ContextShare(TeamMemberUsage usage)
    {
        int window = _host.FindModel(usage.ModelId)?.MaxInputTokens ?? 0;
        return usage.Context > 0 && window > 0 ? Math.Min(1, usage.Context / (double)window) : null;
    }

    private static string Percent(double fraction) => $"{Math.Round(fraction * 100):0}%";

    // ---------- 任务说明 ----------

    /// <summary>引用别的任务时写它的标题(过长截短),认不出的写 #id。</summary>
    private string Refer(string taskId)
        => Team.Tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase)) is { } task
            ? "「" + ChatFormatting.OneLine(task.Title, 14) + "」"
            : "#" + taskId;

    /// <summary>成员此刻在做什么:在跑的任务的实时动作 &gt; 在等什么 &gt; 最近一条与它有关的消息。</summary>
    private string ActivityOf(TeamMemberInfo member)
    {
        TeamTaskInfo[] own = [.. Team.Tasks.Where(t => t.Assignee == member.Name)];
        if (own.FirstOrDefault(t => t.Status == "running") is { } running)
        {
            string doing = TeamConsoleModel.RunOf(running, Runs)?.Activity switch
            {
                SubagentOrchestrator.WaitingForApproval => _loc["SubagentWaitingApproval"],
                SubagentOrchestrator.WaitingForFileLock => _loc["SubagentWaitingLock"],
                { Length: > 0 } activity => activity,
                _ => _loc["TeamStateThinking"]
            };
            return $"{running.Title} · {doing}";
        }
        if (Team.Phase == "running" && own.FirstOrDefault(t => t.Status == "pending") is { } waiting)
        {
            return TeamNotes.Localize(waiting.Note, _loc, Refer) is { Length: > 0 } note
                ? note
                : waiting.DependsOn.Count > 0
                    ? _loc.F("TeamDependsOn", string.Join("、", waiting.DependsOn.Select(Refer)))
                    : _loc["TeamTaskPending"];
        }
        return Team.Messages.LastOrDefault(m => m.From == member.Name || m.To == member.Name) is { } last
            ? (last.From == member.Name ? $"→ {PartyOf(last.To).Name}:" : $"{PartyOf(last.From).Name} →:") + last.Text
            : "";
    }

    /// <summary>审查这个任务的那个任务(有的话)。</summary>
    private TeamTaskInfo? ReviewerOf(TeamTaskInfo task)
        => Team.Tasks.FirstOrDefault(t => string.Equals(t.Verifies, task.Id, StringComparison.OrdinalIgnoreCase));

    private TeamTaskInfo? FindTask(string? id)
        => id is null ? null : Team.Tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---------- 小工具 ----------

    /// <summary>整块可点:按下就算(与聊天面板里的卡片同一个手感)。</summary>
    private static T Clickable<T>(T control, Action action) where T : Control
    {
        control.Cursor = new Cursor(StandardCursorType.Hand);
        control.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return control;
    }

    /// <summary>一段渲染好的 Markdown(成员的报告)。</summary>
    private MarkdownRenderer Markdown(ObservableStringBuilder text)
    {
        var renderer = new MarkdownRenderer { MarkdownBuilder = text, CodeBlockColorTheme = _codeTheme };
        renderer.LinkClick += (_, e) => _host.OpenLink(e);
        return renderer;
    }

    private MarkdownRenderer Markdown(string text)
    {
        var builder = new ObservableStringBuilder();
        builder.Append(text);
        return Markdown(builder);
    }
}

/// <summary>
/// 引擎给任务写的状态说明是给模型看的英文固定句式(见 <c>TeamEngine.Pump</c> / <c>OnEntryFinished</c>),
/// 界面上换成本地化文案、任务 id 换成标题。
/// </summary>
internal static partial class TeamNotes
{
    [GeneratedRegex(@"^waiting for (\S+) to finish #(\S+)$")]
    private static partial Regex Busy();

    [GeneratedRegex(@"^re-review after #(\S+) rework$")]
    private static partial Regex ReReview();

    [GeneratedRegex(@"^#(\S+) (failed|cancelled|blocked)$")]
    private static partial Regex Blocked();

    /// <summary>
    /// 「等待依赖」「返工 n/m」在界面上另有地方写,这里不重复(返回空串);认不出的(多半是工具报的错误原文)照原样。
    /// </summary>
    public static string Localize(string note, Loc loc, Func<string, string> refer)
    {
        if (note.Length == 0 || note.StartsWith("waiting for #", StringComparison.Ordinal)
            || note.StartsWith("rework ", StringComparison.Ordinal))
        {
            return "";
        }
        if (Busy().Match(note) is { Success: true } busy)
            return loc.F("TeamWaitMember", busy.Groups[1].Value, refer(busy.Groups[2].Value));
        if (ReReview().Match(note) is { Success: true } again)
            return loc.F("TeamReReview", refer(again.Groups[1].Value));
        if (Blocked().Match(note) is { Success: true } blocked)
            return loc.F("TeamBlockedBy", refer(blocked.Groups[1].Value));
        return note == "team cancelled" ? loc["Cancelled"] : note;
    }
}
