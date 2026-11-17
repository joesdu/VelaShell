using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>抽屉里正在看的东西。</summary>
/// <param name="Kind">agent(成员)/ task(任务)/ pair(两人之间的对话)。</param>
/// <param name="A">成员名、任务 id,或对话的一端。</param>
/// <param name="B">对话的另一端。</param>
/// <param name="Focus">对话里高亮的那条消息。</param>
internal sealed record DrawerView(string Kind, string A, string? B = null, TeamMessageInfo? Focus = null);

/// <summary>
/// 详情抽屉:从右侧滑出,盖在页面上。三种内容 —— 成员、任务、两人之间的对话 —— 之间可以互相点过去,
/// 走过的路记成一摞,左上角一层层返回;Esc 或点遮罩关掉。
/// </summary>
internal sealed partial class TeamConsoleView
{
    /// <summary>抽屉滑进 / 滑出要多久。</summary>
    private static readonly TimeSpan DrawerSlide = TimeSpan.FromMilliseconds(300);

    // 遮罩与抽屉一直在可视树上,关着时只是「透明、不接鼠标」和「停在屏幕右侧外面」:
    // 开合只改这两个属性的目标值,过渡永远从此刻的位置出发(见 OpenView 的说明)
    private readonly Border _scrim = new() { Name = "TeamDrawerScrim", Opacity = 0, IsHitTestVisible = false };
    private readonly Border _drawer = new() { Name = "TeamDrawer", Classes = { "tcDrawer" }, Width = 640, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DispatcherTimer _drawerSettle = new() { Interval = DrawerSlide + TimeSpan.FromMilliseconds(60) };
    private readonly TextBlock _drawerTitle = TeamUi.Line("", "VelaTextPrimary", 13, FontWeight.SemiBold);
    private readonly Button _drawerBack = new() { Name = "TeamDrawerBack", Classes = { "tcIconButton" } };
    private readonly StackPanel _drawerBody = new();
    private readonly ScrollViewer _drawerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly List<DrawerView> _drawerStack = [];
    private DrawerView? _drawerView;
    private string _drawerKey = "";

    // 任务视图里随快照原地更新的那几样(其余部分按签名整块重建)
    private TextBlock? _taskTiming;
    private ObservableStringBuilder? _reportText;
    private Control? _reportBody, _reportEmpty;
    private string _reportShown = "";

    /// <summary>抽屉是否开着。</summary>
    internal bool IsDrawerOpen { get; private set; }

    /// <summary>抽屉里正在看的东西;关着为 null。</summary>
    internal DrawerView? CurrentDrawerView => IsDrawerOpen ? _drawerView : null;

    /// <summary>抽屉标题(测试用)。</summary>
    internal string DrawerTitle => _drawerTitle.Text ?? "";

    private Panel BuildDrawer()
    {
        _scrim.Bind(Border.BackgroundProperty, "VelaScrim");
        _scrim.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            CloseDrawer();
        };

        _drawerBack.Content = TeamUi.Icon("AiIcon.arrow-left", "VelaTextSecondary", 14);
        ToolTip.SetTip(_drawerBack, _loc["TeamDrawerBack"]);
        _drawerBack.Click += (_, _) => DrawerBack();
        var close = new Button { Name = "TeamDrawerClose", Classes = { "tcIconButton" }, Content = TeamUi.Icon("AiIcon.x", "VelaTextSecondary", 14) };
        ToolTip.SetTip(close, _loc["TeamDrawerClose"]);
        close.Click += (_, _) => CloseDrawer();

        var head = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], ColumnSpacing = 10 };
        head.Children.Add(_drawerBack);
        head.Children.Add(_drawerTitle.At(1));
        head.Children.Add(close.At(2));
        Border headBar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12), Child = head }
            .Bind(Border.BorderBrushProperty, "VelaBorderPrimary");

        _drawerScroll.Content = new Border { Padding = new Thickness(16), Child = _drawerBody };
        var layout = new Grid { RowDefinitions = [with("Auto,*")] };
        layout.Children.Add(headBar);
        layout.Children.Add(_drawerScroll.At(0, 1));
        _drawer.Child = layout;
        // 先摆到屏幕外,再挂过渡:顺序反了的话,页面一打开抽屉就会从原位「滑出去」一次
        _drawer.RenderTransform = DrawerOffscreen();
        KeyboardNavigation.SetTabNavigation(_drawer, KeyboardNavigationMode.None);
        _drawer.Transitions =
        [
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = DrawerSlide, Easing = new SplineEasing(0.2, 0.8, 0.2, 1) }
        ];
        _scrim.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) }];
        // 滑出去之后再清内容:滑的途中清掉,看到的是一块空板子在动
        _drawerSettle.Tick += (_, _) =>
        {
            _drawerSettle.Stop();
            if (!IsDrawerOpen) _drawerBody.Children.Clear();
        };

        var overlay = new Panel();
        overlay.Children.Add(_scrim);
        overlay.Children.Add(_drawer);
        return overlay;
    }

    /// <summary>打开抽屉看某样东西;已经开着就把当前这样压进返回栈。</summary>
    internal void OpenView(DrawerView view, bool push = true)
    {
        if (!IsDrawerOpen)
        {
            _drawerStack.Clear();
        }
        else if (push && _drawerView is not null && _drawerView != view)
        {
            _drawerStack.Add(_drawerView);
        }
        _drawerView = view;
        bool opening = !IsDrawerOpen;
        IsDrawerOpen = true;
        _drawerSettle.Stop();
        // 先把内容建好再滑:滑进来的是一块已经排好的板子,不是边滑边长出来
        RenderDrawer(resetScroll: true);
        if (!opening) return;
        // 只动两个目标值:抽屉从屏幕外滑到位,遮罩淡入。
        // 以前的做法是每次打开先把抽屉「瞬移」到起点(偏右、透明)、下一拍再放回终点 —— 可这两步都挂着过渡,
        // 第一步自己也在动:抽屉先在原位闪出来、往回淡出,再被拉回来,看上去就是闪一下。
        // 现在关着时它本来就停在屏幕外,开合都是从当前位置出发的一段过渡,滑到一半再点也只是折返。
        _drawer.RenderTransform = TransformOperations.Parse("translateX(0px)");
        _scrim.Opacity = 1;
        _scrim.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(_drawer, KeyboardNavigationMode.Continue);
        Focus(); // Esc 关抽屉要先拿到键盘焦点
    }

    /// <summary>关掉抽屉:滑回屏幕右侧外面,遮罩淡出。</summary>
    internal void CloseDrawer()
    {
        if (!IsDrawerOpen) return;
        IsDrawerOpen = false;
        _drawer.RenderTransform = DrawerOffscreen();
        _scrim.Opacity = 0;
        _scrim.IsHitTestVisible = false;
        // 关着的抽屉还在可视树上,别让 Tab 键走进去
        KeyboardNavigation.SetTabNavigation(_drawer, KeyboardNavigationMode.None);
        _drawerStack.Clear();
        _drawerKey = "";
        _drawerSettle.Stop();
        _drawerSettle.Start();
    }

    /// <summary>抽屉关着时停在哪儿:整块移到页面右缘之外,再多出一截把投影也带出去。</summary>
    private TransformOperations DrawerOffscreen()
        => TransformOperations.Parse(FormattableString.Invariant($"translateX({_drawer.Width + 40:0}px)"));

    /// <summary>页面宽度变了:抽屉跟着变宽 / 变窄;关着的话,停靠的位置也要跟着挪。</summary>
    private void ResizeDrawer(double pageWidth)
    {
        _drawer.Width = Math.Min(640, pageWidth);
        if (!IsDrawerOpen) _drawer.RenderTransform = DrawerOffscreen();
    }

    /// <summary>回到上一样东西;没有上一样时什么也不做。</summary>
    internal void DrawerBack()
    {
        if (_drawerStack.Count == 0) return;
        _drawerView = _drawerStack[^1];
        _drawerStack.RemoveAt(_drawerStack.Count - 1);
        RenderDrawer(resetScroll: true);
    }

    /// <summary>快照变了:抽屉里的内容只在它自己那部分数据变了时才重建,免得打断正在读的人。</summary>
    private void RefreshDrawer()
    {
        if (!IsDrawerOpen || _drawerView is null) return;
        if (DrawerKeyOf(_drawerView) != _drawerKey)
        {
            RenderDrawer(resetScroll: false);
            return;
        }
        if (_drawerView.Kind == "task" && FindTask(_drawerView.A) is { } task) UpdateTaskLive(task);
    }

    private void RenderDrawer(bool resetScroll)
    {
        if (_drawerView is not { } view) return;
        double keep = _drawerScroll.Offset.Y;
        _drawerBack.IsVisible = _drawerStack.Count > 0;
        _drawerKey = DrawerKeyOf(view);
        _drawerBody.Children.Clear();
        _taskTiming = null;
        _reportText = null;
        _reportBody = _reportEmpty = null;
        _reportShown = "";
        Control? focus = null;
        switch (view.Kind)
        {
            case "agent":
                RenderAgent(view.A);
                break;
            case "task":
                RenderTask(view.A);
                break;
            default:
                focus = RenderPair(view.A, view.B ?? "", view.Focus);
                break;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (resetScroll && focus is not null) focus.BringIntoView();
            else _drawerScroll.Offset = new Vector(0, resetScroll ? 0 : keep);
        });
    }

    /// <summary>当前这样东西依赖的数据的指纹;没变就不重建。</summary>
    private string DrawerKeyOf(DrawerView view)
    {
        switch (view.Kind)
        {
            case "agent":
                {
                    TeamMemberInfo? member = Team.Members.FirstOrDefault(m => m.Name == view.A);
                    TeamMemberUsage? usage = member is null ? null : UsageOf(member);
                    return string.Join("|", "agent", view.A, StateOf(view.A), member is null ? "" : ActivityOf(member),
                        usage?.Input, usage?.Output, usage?.Cached, usage?.Context,
                        Team.Messages.Count(m => TeamConsoleModel.Involves(m, view.A)),
                        string.Join(",", Team.Tasks.Where(t => t.Assignee == view.A).Select(t => t.Id + t.Status)),
                        _session.Telemetry.Member(view.A).Count);
                }
            case "task":
                {
                    if (FindTask(view.A) is not { } task) return "task|" + view.A;
                    SubagentSnapshot? run = TeamConsoleModel.RunOf(task, Runs);
                    return string.Join("|", "task", task.Id, task.Status, task.Attempts, task.Verdict, task.Note,
                        Team.Messages.Count(m => string.Equals(m.TaskId, task.Id, StringComparison.OrdinalIgnoreCase)),
                        run?.SubagentId, run?.Status, run?.InputTokens, run?.OutputTokens, run?.Errors.Count,
                        string.Join(",", run?.ToolsUsed.Select(t => t.Name + t.Count) ?? []),
                        _session.Cancel is not null, _session.Retry is not null, task.Retries, task.Assignee, StateOf(task.Assignee));
                }
            default:
                {
                    TeamMessageInfo[] between = [.. TeamConsoleModel.Between(Team, view.A, view.B ?? "")];
                    return string.Join("|", "pair", view.A, view.B, between.Length, view.Focus?.At.Ticks, view.Focus?.Text,
                        string.Join(",", between.Select(m => FindTask(m.TaskId)?.Report.Length ?? 0)));
                }
        }
    }

    // ---------- 积木 ----------

    private static StackPanel Section(string title, Control body)
    {
        TextBlock heading = TeamUi.Text(title, "VelaTextSecondary", 12, FontWeight.SemiBold);
        heading.Margin = new Thickness(0, 0, 0, 8);
        var section = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        section.Children.Add(heading);
        section.Children.Add(body);
        return section;
    }

    private static Border Cell(string key, Control value)
    {
        var stack = new StackPanel();
        stack.Children.Add(TeamUi.Line(key, "VelaTextSecondary", 11));
        stack.Children.Add(value);
        return new Border { Classes = { "tcCell" }, Child = stack };
    }

    private static Border Cell(string key, string value)
        => Cell(key, TeamUi.Line(value, "VelaTextPrimary", 15, FontWeight.SemiBold, mono: true));

    private TextBlock None() => TeamUi.Text(_loc["TeamNone"], "VelaTextTertiary", 12);

    /// <summary>条目:一个任务。点开看任务。</summary>
    private Border TaskItem(TeamTaskInfo task)
    {
        var grid = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], ColumnSpacing = 8 };
        grid.Children.Add(TeamUi.Text(task.Id, "VelaTextSecondary", 12, mono: true));
        grid.Children.Add(TeamUi.Line(task.Title, "VelaTextPrimary", 12).At(1));
        grid.Children.Add(TaskPill(task).At(2));
        return Clickable(new Border { Classes = { "tcItem" }, Child = grid, Tag = task.Id },
            () => OpenView(new DrawerView("task", task.Id)));
    }

    /// <summary>条目:一条消息。点开看它所在的对话,并高亮这一条。</summary>
    private Border MessageItem(TeamMessageInfo message)
    {
        var grid = new Grid { ColumnDefinitions = [with("Auto,Auto,Auto,Auto,Auto,*")], ColumnSpacing = 8 };
        grid.Children.Add(TeamUi.Text(message.At.ToLocalTime().ToString("HH:mm"), "VelaTextSecondary", 12, mono: true));
        grid.Children.Add(Avatar(message.From, 20).At(1));
        grid.Children.Add(TeamUi.Text("→", "VelaTextTertiary", 12).At(2));
        grid.Children.Add(Avatar(message.To, 20).At(3));
        grid.Children.Add(KindTag(message.Kind).At(4));
        grid.Children.Add(TeamUi.Line(ChatFormatting.OneLine(message.Text, 200), "VelaTextPrimary", 12).At(5));
        return Clickable(new Border { Classes = { "tcItem" }, Child = grid, Tag = message },
            () => OpenView(new DrawerView("pair", message.From, message.To, message)));
    }

    /// <summary>条目:一个人。点开看成员。</summary>
    private Border PartyItem(string id, string detail, Control? trailing, double avatar = 26)
    {
        Party party = PartyOf(id);
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        text.Children.Add(TeamUi.Text(party.Name, "VelaTextPrimary", 12, FontWeight.SemiBold));
        if (detail.Length > 0) text.Children.Add(TeamUi.Line(detail, "VelaTextSecondary", 12));
        var grid = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], ColumnSpacing = 8 };
        grid.Children.Add(Avatar(id, avatar));
        grid.Children.Add(text.At(1));
        if (trailing is not null) grid.Children.Add(trailing.At(2));
        return Clickable(new Border { Classes = { "tcItem" }, Child = grid, Tag = id },
            () => OpenView(new DrawerView("agent", id)));
    }

    // ---------- 成员 ----------

    private void RenderAgent(string id)
    {
        Party party = PartyOf(id);
        TeamMemberInfo? member = party.Member;
        _drawerTitle.Text = party.Role.Length > 0 ? $"{party.Name} · {party.Role}" : party.Name;

        TextBlock name = TeamUi.Text(party.Name, "VelaTextPrimary", 18, FontWeight.SemiBold);
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(name);
        nameRow.Children.Add(TeamUi.Text(party.Role, "VelaTextSecondary", 13));
        var badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        Panel pill = StatePill(id);
        pill.Margin = new Thickness(0, 0, 6, 0);
        badges.Children.Add(pill);
        TeamMemberUsage? usage = member is null ? null : UsageOf(member);
        string modelLine = ModelLine(member is null ? Team.LeadModelId : usage!.ModelId);
        if (modelLine.Length > 0) badges.Children.Add(TeamUi.Chip(modelLine));
        string about = member is null
            ? _loc["TeamLeadAbout"]
            : string.Join(" · ", ((string?[])
            [
                member.AllowWrites ? _loc["SubagentWritable"] : _loc["TeamMemberReadOnly"],
                member.AllowCommands ? _loc["SubagentCommands"] : null
            ]).OfType<string>());
        TextBlock aboutText = TeamUi.Text(about, "VelaTextSecondary", 12);
        aboutText.TextWrapping = TextWrapping.Wrap;
        aboutText.Margin = new Thickness(0, 6, 0, 0);
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(nameRow);
        who.Children.Add(badges);
        who.Children.Add(aboutText);
        var profile = new Grid { ColumnDefinitions = [with("Auto,*")], ColumnSpacing = 14 };
        profile.Children.Add(Avatar(id, 72));
        profile.Children.Add(who.At(1));
        _drawerBody.Children.Add(profile);

        if (member is not null && usage is not null)
        {
            double? share = ContextShare(usage);
            var context = new StackPanel();
            context.Children.Add(TeamUi.Line(share is { } s ? Percent(s) : "—", "VelaTextPrimary", 15, FontWeight.SemiBold, mono: true));
            var meter = new Meter(5);
            meter.Set(share ?? 0, share > 0.85 ? "VelaError" : "VelaAccent");
            meter.Root.Margin = new Thickness(0, 6, 0, 0);
            context.Children.Add(meter.Root);

            var cells = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 10, -4, 0) };
            cells.Children.Add(Cell(_loc["TeamCellInput"], ChatFormatting.Compact(usage.Input)));
            cells.Children.Add(Cell(_loc["TeamCellOutput"], ChatFormatting.Compact(usage.Output)));
            cells.Children.Add(Cell(_loc["TeamCellCacheHit"], usage.Input > 0 ? Percent(usage.Cached / (double)usage.Input) : "—"));
            cells.Children.Add(Cell(_loc["TeamStatCost"], CostOf(member) is { } cost ? ChatPanelView.FormatCost(cost) : "—"));
            cells.Children.Add(Cell(_loc["TeamCellSentReceived"],
                $"{Team.Messages.Count(m => m.From == id)} / {Team.Messages.Count(m => m.From != id && TeamConsoleModel.Involves(m, id))}"));
            cells.Children.Add(Cell(_loc["TeamCellContext"], context));
            _drawerBody.Children.Add(cells);

            string activity = ActivityOf(member);
            TextBlock doing = TeamUi.Text(activity.Length > 0 ? activity : "—", "VelaTextSecondary", 13);
            doing.TextWrapping = TextWrapping.Wrap;
            _drawerBody.Children.Add(Section(_loc["TeamSecActivity"], doing));

            IReadOnlyList<double> series = _session.Telemetry.Member(id);
            Control trend = series.Count >= 2
                ? new TeamSparkline { Height = 48, Values = series }.Bind(TeamSparkline.StrokeProperty, party.ColorKey)
                : TeamUi.Text(_loc[Team.Phase == "running" ? "TeamRateWarming" : "TeamRateNone"], "VelaTextTertiary", 12);
            _drawerBody.Children.Add(Section(_loc["TeamSecRate"], trend));

            var tools = new WrapPanel();
            foreach (SubagentToolUsage tool in usage.Tools)
            {
                Border chip = TeamUi.Chip($"{tool.Name} ×{tool.Count}");
                chip.Margin = new Thickness(0, 0, 6, 6);
                tools.Children.Add(chip);
            }
            _drawerBody.Children.Add(Section(_loc["TeamSecTools"], usage.Tools.Count > 0 ? tools : None()));
        }

        TeamTaskInfo[] owned = [.. Team.Tasks.Where(t => t.Assignee == id)];
        if (member is not null)
        {
            var list = new StackPanel();
            foreach (TeamTaskInfo task in owned) list.Children.Add(TaskItem(task));
            _drawerBody.Children.Add(Section(_loc["TeamSecOwned"], owned.Length > 0 ? list : None()));
        }

        // 沟通对象:和谁说过话、各几条;没说过话但有关系(连线)的也列上
        var partners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (TeamMessageInfo message in Team.Messages.Where(m => m.From == id || m.To == id))
        {
            string other = message.From == id ? message.To : message.From;
            partners[other] = partners.GetValueOrDefault(other) + 1;
        }
        IReadOnlyList<TeamEdge> edges = TeamConsoleModel.Edges(Team);
        var partnerList = new StackPanel();
        foreach ((string other, int count) in partners.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            Party them = PartyOf(other);
            var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            text.Children.Add(TeamUi.Text(them.Name, "VelaTextPrimary", 12, FontWeight.SemiBold));
            if (them.Role.Length > 0) text.Children.Add(TeamUi.Text(them.Role, "VelaTextSecondary", 12));
            if (TeamConsoleModel.FindEdge(edges, id, other) is { } found)
            {
                text.Children.Add(TeamUi.Text("· " + EdgeLabel(found.Edge), EdgeLook(found.Edge.Kind).Color, 12));
            }
            var grid = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], ColumnSpacing = 8 };
            grid.Children.Add(Avatar(other, 26));
            grid.Children.Add(text.At(1));
            grid.Children.Add(TeamUi.Text(_loc.F("TeamMessageCount", count), "VelaTextSecondary", 12, mono: true).At(2));
            partnerList.Children.Add(Clickable(new Border { Classes = { "tcItem" }, Child = grid, Tag = other },
                () => OpenView(new DrawerView("pair", id, other))));
        }
        _drawerBody.Children.Add(Section(_loc["TeamSecPartners"], partners.Count > 0 ? partnerList : None()));

        TeamMessageInfo[] recent = [.. Team.Messages.Where(m => TeamConsoleModel.Involves(m, id)).TakeLast(8).Reverse()];
        var recentList = new StackPanel();
        foreach (TeamMessageInfo message in recent) recentList.Children.Add(MessageItem(message));
        _drawerBody.Children.Add(Section(_loc["TeamSecRecent"], recent.Length > 0 ? recentList : None()));
    }

    // ---------- 任务 ----------

    private void RenderTask(string id)
    {
        if (FindTask(id) is not { } task)
        {
            _drawerTitle.Text = "#" + id;
            _drawerBody.Children.Add(None());
            return;
        }
        _drawerTitle.Text = $"{task.Id} · {task.Title}";
        SubagentSnapshot? run = TeamConsoleModel.RunOf(task, Runs);
        Party owner = PartyOf(task.Assignee);

        var top = new WrapPanel();
        Panel pill = TaskPill(task);
        pill.Margin = new Thickness(0, 0, 8, 0);
        top.Children.Add(pill);
        _taskTiming = TeamUi.Text("", "VelaTextSecondary", 11, mono: true);
        top.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(7, 0),
            Child = _taskTiming,
            VerticalAlignment = VerticalAlignment.Center
        }.Bind(Border.BorderBrushProperty, "VelaBorderSecondary"));
        _drawerBody.Children.Add(top);

        // 进度:做完满格,失败满格错误色;在跑的没有可信的完成度(步数上限不是进度),走不定进度动画
        var progressHead = new Grid { ColumnDefinitions = [with("*,Auto")], Margin = new Thickness(0, 14, 0, 4) };
        progressHead.Children.Add(TeamUi.Text(_loc["TeamTaskProgress"], "VelaTextPrimary", 13, FontWeight.SemiBold));
        progressHead.Children.Add(TeamUi.Text(task.Status == "completed" ? "100%" : task.Status is "running" or "failed" ? _loc[TaskLook(task).Key] : "0%",
            "VelaTextPrimary", 12, mono: true).At(1));
        ProgressBar bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            MinHeight = 8,
            Value = task.Status is "completed" or "failed" ? 100 : 0,
            IsIndeterminate = task.Status == "running"
        }.Bind(BackgroundProperty, "VelaBgHover").Bind(ForegroundProperty, task.Status == "failed" ? "VelaError" : owner.ColorKey);
        _drawerBody.Children.Add(progressHead);
        _drawerBody.Children.Add(bar);

        int related = Team.Messages.Count(m => string.Equals(m.TaskId, task.Id, StringComparison.OrdinalIgnoreCase));
        long tokens = TeamConsoleModel.RunsOf(task, Runs).Sum(r => r.InputTokens + r.OutputTokens);
        var cells = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 10, -4, 0) };
        cells.Children.Add(Cell(_loc["TeamCellMessages"], related.ToString()));
        cells.Children.Add(Cell(_loc["TeamCellTokens"], ChatFormatting.Compact(tokens)));
        cells.Children.Add(Cell(_loc["TeamCellDeps"], task.DependsOn.Count > 0 ? string.Join(", ", task.DependsOn) : "—"));
        _drawerBody.Children.Add(cells);

        // 等待 / 受阻 / 返工的原因,以及审查结论
        var notes = new List<string>();
        if (TeamNotes.Localize(task.Note, _loc, Refer) is { Length: > 0 } note) notes.Add(note);
        if (task.Verifies is { } verifiedId)
        {
            notes.Add(_loc.F("TeamReviews", Refer(verifiedId))
                      + (task.Verdict is { } verdict ? " · " + _loc[verdict == "PASS" ? "TeamVerdictPass" : "TeamVerdictFail"] : ""));
        }
        // 返工(审查不过)与重试(出错后重跑)分开写:Attempts 是总共跑了几次
        int reworks = task.Attempts - 1 - task.Retries;
        if (reworks > 0) notes.Add(_loc.F("TeamRework", reworks));
        if (task.Retries > 0) notes.Add(_loc.F("TeamRetries", task.Retries));
        if (notes.Count > 0)
        {
            TextBlock line = TeamUi.Text(string.Join(" · ", notes), "VelaTextSecondary", 12);
            line.TextWrapping = TextWrapping.Wrap;
            line.Margin = new Thickness(0, 10, 0, 0);
            _drawerBody.Children.Add(line);
        }
        if (RetryBar(task) is { } retryBar) _drawerBody.Children.Add(retryBar);

        string ownerModel = ModelName(run?.ModelId ?? owner.Member?.ModelId);
        _drawerBody.Children.Add(Section(_loc["TeamSecOwner"],
            PartyItem(task.Assignee, owner.Role + (ownerModel.Length > 0 ? " · " + ownerModel : ""), StatePill(task.Assignee), 30)));
        if (ReviewerOf(task) is { } review && review.Assignee != task.Assignee)
        {
            _drawerBody.Children.Add(Section(_loc["TeamSecReviewer"],
                PartyItem(review.Assignee, PartyOf(review.Assignee).Role, StatePill(review.Assignee))));
        }
        TeamTaskInfo[] upstream = [.. task.DependsOn.Select(FindTask).OfType<TeamTaskInfo>()];
        if (upstream.Length > 0)
        {
            var list = new StackPanel();
            foreach (TeamTaskInfo dependency in upstream) list.Children.Add(TaskItem(dependency));
            _drawerBody.Children.Add(Section(_loc["TeamSecUpstream"], list));
        }

        // 任务报告:在跑时是到目前为止的正文(流式追加),落定后是完整报告
        var report = new StackPanel { Spacing = 8 };
        if (run is { IsTerminal: false } && _session.Cancel is { } cancel)
        {
            Button stop = new Button
            {
                Name = "TeamTaskCancel",
                Content = _loc["Cancel"],
                Height = 24,
                Padding = new Thickness(10, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            }.Bind(ThemeProperty, "VelaOutlineButtonTheme");
            ToolTip.SetTip(stop, _loc["SubagentCancel"]);
            string runId = run.SubagentId;
            stop.Click += (_, _) =>
            {
                stop.IsEnabled = false;
                cancel(runId);
            };
            report.Children.Add(stop);
        }
        if (run is { ToolsUsed.Count: > 0 })
        {
            var tools = new WrapPanel();
            foreach (SubagentToolUsage tool in run.ToolsUsed)
            {
                Border chip = TeamUi.Chip($"{tool.Name} ×{tool.Count}");
                chip.Margin = new Thickness(0, 0, 6, 4);
                tools.Children.Add(chip);
            }
            report.Children.Add(tools);
        }
        if (run is { Errors.Count: > 0 })
        {
            SelectableTextBlock errors = new SelectableTextBlock { Text = string.Join(Environment.NewLine, run.Errors), TextWrapping = TextWrapping.Wrap }
                .Bind(TextBlock.ForegroundProperty, "VelaError").Bind(TextBlock.FontSizeProperty, "VelaFontSize11")
                .Bind(TextBlock.FontFamilyProperty, "VelaUiMonoFont");
            report.Children.Add(new Border { Classes = { "tcCode" }, Child = errors });
        }
        if (run is { Status: "failed" or "cancelled" or "timed_out", Output.Length: > 0 })
        {
            report.Children.Add(TeamUi.Text(_loc["SubagentPartial"], "VelaTextTertiary", 11));
        }
        _reportText = new ObservableStringBuilder();
        _reportShown = "";
        _reportBody = Markdown(_reportText);
        _reportBody.IsVisible = false;
        report.Children.Add(_reportBody);
        _reportEmpty = TeamUi.Text(_loc["TeamReportEmpty"], "VelaTextTertiary", 12);
        report.Children.Add(_reportEmpty);
        _drawerBody.Children.Add(Section(_loc["TeamSecReport"], report));
        UpdateTaskLive(task);

        TeamMessageInfo[] messages = [.. Team.Messages.Where(m => string.Equals(m.TaskId, task.Id, StringComparison.OrdinalIgnoreCase)).Reverse()];
        var feed = new StackPanel();
        foreach (TeamMessageInfo message in messages) feed.Children.Add(MessageItem(message));
        _drawerBody.Children.Add(Section(_loc["TeamSecMessages"], messages.Length > 0 ? feed : None()));
    }

    /// <summary>
    /// 没做成的任务(失败 / 超时 / 被取消 / 受阻)下面的那一条:「重试」让原来的成员再跑一遍,
    /// 「改派给」后面的成员药丸把它交给别人。只在这一轮还在跑时有(引擎随这一轮收尾);做成了、在跑、没开始的任务没有。
    /// </summary>
    private Border? RetryBar(TeamTaskInfo task)
    {
        if (_session.Retry is not { } retry || task.Status is not ("failed" or "cancelled" or "blocked")) return null;
        TextBlock result = TeamUi.Text("", "VelaWarning", 12);
        result.TextWrapping = TextWrapping.Wrap;
        result.IsVisible = false;

        void Run(string? assignee, Control source)
        {
            source.IsEnabled = false;
            // 成了的话任务马上回到待办 / 运行中,快照一变这一条就跟着抽屉重建没了;没成(比如这一轮刚好收尾)把原因写出来
            string answer = retry(task.Id, assignee);
            if (answer.StartsWith("Retrying", StringComparison.Ordinal)) return;
            source.IsEnabled = true;
            result.Text = answer;
            result.IsVisible = answer.Length > 0;
        }

        Button again = new Button
        {
            Name = "TeamTaskRetry",
            Content = _loc["TeamRetry"],
            Height = 24,
            Padding = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center
        }.Bind(ThemeProperty, "VelaOutlineButtonTheme");
        ToolTip.SetTip(again, _loc.F("TeamRetryTip", task.Assignee));
        again.Click += (_, _) => Run(null, again);

        var actions = new WrapPanel();
        again.Margin = new Thickness(0, 0, 10, 4);
        actions.Children.Add(again);
        TeamMemberInfo[] others = [.. Team.Members.Where(m => m.Name != task.Assignee)];
        if (others.Length > 0)
        {
            TextBlock label = TeamUi.Text(_loc["TeamReassignTo"], "VelaTextSecondary", 12);
            label.Margin = new Thickness(0, 0, 8, 4);
            actions.Children.Add(label);
            foreach (TeamMemberInfo member in others)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                row.Children.Add(Avatar(member.Name, 20));
                row.Children.Add(TeamUi.Text(member.Name, "VelaTextPrimary", 12));
                var chip = new Border { Classes = { "tcFilter" }, Child = row, Tag = member.Name, Margin = new Thickness(0, 0, 6, 4) };
                ToolTip.SetTip(chip, _loc.F("TeamReassignTip", member.Name, member.Role));
                actions.Children.Add(Clickable(chip, () => Run(member.Name, chip)));
            }
        }

        TextBlock hint = TeamUi.Text(_loc[task.Status == "blocked" ? "TeamRetryHintBlocked" : "TeamRetryHint"], "VelaTextSecondary", 12);
        hint.TextWrapping = TextWrapping.Wrap;
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(hint);
        stack.Children.Add(actions);
        stack.Children.Add(result);
        return new Border { Name = "TeamTaskRetryBar", Classes = { "tcCell" }, Margin = new Thickness(0, 12, 0, 0), Child = stack };
    }

    /// <summary>任务视图里每一拍都在变的两样:耗时那一行,以及流式长出来的报告正文。</summary>

    private void UpdateTaskLive(TeamTaskInfo task)
    {
        _taskTiming?.Text = task.Elapsed > TimeSpan.Zero || task.Status == "running"
            ? _loc.F("TeamBarTip", ChatFormatting.Duration(task.StartOffset ?? TimeSpan.Zero), ChatFormatting.Duration(task.Elapsed))
            : _loc["TeamTaskPending"];
        if (_reportText is null) return;
        string text = TeamConsoleModel.RunOf(task, Runs)?.Output is { Length: > 0 } output ? output : task.Report;
        if (text == _reportShown) return;
        // 正文只会往后长:接着上一段追加,Markdown 才不会每次刷新都从头重排;对不上(返工后换了一份)就重画整个抽屉
        if (_reportShown.Length == 0 || text.StartsWith(_reportShown, StringComparison.Ordinal))
        {
            _reportText.Append(text[_reportShown.Length..]);
            _reportShown = text;
            _reportBody?.IsVisible = true;
            _reportEmpty?.IsVisible = false;
        }
        else
        {
            _drawerKey = "";
        }
    }

    // ---------- 两人之间的对话 ----------

    /// <summary>画对话;返回高亮的那个气泡(要滚到它那儿)。</summary>
    private Control? RenderPair(string a, string b, TeamMessageInfo? focus)
    {
        Party left = PartyOf(a), right = PartyOf(b);
        _drawerTitle.Text = _loc.F("TeamPairTitle", left.Name, right.Name);
        TeamMessageInfo[] messages = [.. TeamConsoleModel.Between(Team, a, b)];
        (TeamEdge Edge, bool Reversed)? edge = TeamConsoleModel.FindEdge(TeamConsoleModel.Edges(Team), a, b);

        var relation = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (edge is { } found)
        {
            (string key, string color) = EdgeLook(found.Edge.Kind);
            Panel tag = TeamUi.Tag(EdgeLabel(found.Edge), color);
            tag.HorizontalAlignment = HorizontalAlignment.Center;
            relation.Children.Add(tag);
            TextBlock kind = TeamUi.Text(_loc[key], "VelaTextTertiary", 11);
            kind.HorizontalAlignment = HorizontalAlignment.Center;
            relation.Children.Add(kind);
        }
        TextBlock count = TeamUi.Text(_loc.F("TeamMessageCount", messages.Length), "VelaTextSecondary", 11, mono: true);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        relation.Children.Add(count);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 14) };
        head.Children.Add(PairEnd(left));
        head.Children.Add(relation);
        head.Children.Add(PairEnd(right));
        _drawerBody.Children.Add(head);

        TeamMessageInfo? focused = focus is null ? null : messages.FirstOrDefault(m => ReferenceEquals(m, focus) || m == focus);
        if (focused is not null) _drawerBody.Children.Add(MetaBox(focused));

        Control? focusBubble = null;
        var thread = new StackPanel { Spacing = 14 };
        foreach (TeamMessageInfo message in messages)
        {
            Control bubble = Bubble(message, mine: message.From == a, focused: ReferenceEquals(message, focused));
            if (ReferenceEquals(message, focused)) focusBubble = bubble;
            thread.Children.Add(bubble);
        }
        if (messages.Length == 0) thread.Children.Add(None());
        _drawerBody.Children.Add(thread);
        return focusBubble;

        Control PairEnd(Party party)
        {
            var stack = new StackPanel { Spacing = 2 };
            Viewbox avatar = TeamAvatar.Create(party.Face, 52);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(avatar);
            TextBlock name = TeamUi.Text(party.Name, "VelaTextPrimary", 12, FontWeight.SemiBold);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(name);
            string model = ModelName(party.Member is { } member ? UsageOf(member).ModelId : party.Id == TeamConsoleModel.Lead ? Team.LeadModelId : null);
            if (model.Length > 0)
            {
                TextBlock sub = TeamUi.Line(model, "VelaTextSecondary", 12);
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                sub.MaxWidth = 160;
                stack.Children.Add(sub);
            }
            // 「全体」「引擎」不是某个人,点了没有可看的
            return party.Member is not null || party.Id == TeamConsoleModel.Lead
                ? Clickable(stack, () => OpenView(new DrawerView("agent", party.Id)))
                : stack;
        }
    }

    /// <summary>这条消息说的那个任务的那次运行(汇报 / 审查 / 交付才有);没有为 null。</summary>
    private (TeamTaskInfo Task, SubagentSnapshot? Run)? LinkedRun(TeamMessageInfo message)
        => message.Kind is "report" or "review" or "handoff" && FindTask(message.TaskId) is { } task
            ? (task, TeamConsoleModel.RunOf(task, Runs))
            : null;

    /// <summary>高亮消息的元数据:时间、任务、模型、耗时、输入、输出、缓存、成本(后几项取自它对应的那次运行)。</summary>
    private Border MetaBox(TeamMessageInfo message)
    {
        var title = new WrapPanel();
        Panel tag = KindTag(message.Kind);
        tag.Margin = new Thickness(0, 0, 8, 0);
        title.Children.Add(tag);
        TextBlock text = TeamUi.Text(ChatFormatting.OneLine(message.Text, 160), "VelaTextPrimary", 13, FontWeight.SemiBold);
        text.TextWrapping = TextWrapping.Wrap;
        title.Children.Add(text);

        SubagentSnapshot? run = LinkedRun(message)?.Run;
        Party from = PartyOf(message.From);
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 8, 0, 0) };
        grid.Children.Add(Field(_loc["TeamMetaTime"], TeamUi.Text(message.At.ToLocalTime().ToString("HH:mm:ss"), "VelaTextPrimary", 12, mono: true)));
        Control taskField = TeamUi.Text("—", "VelaTextPrimary", 12, mono: true);
        if (message.TaskId is { Length: > 0 } taskId)
        {
            TextBlock link = TeamUi.Text(taskId, "VelaAccent", 12, mono: true);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            taskField = Clickable(link, () => OpenView(new DrawerView("task", taskId)));
        }
        grid.Children.Add(Field(_loc["TeamMetaTask"], taskField));
        string model = ModelName(run?.ModelId ?? from.Member?.ModelId ?? (from.Id == TeamConsoleModel.Lead ? Team.LeadModelId : null));
        grid.Children.Add(Field(_loc["Model"], Value(model.Length > 0 ? model : "—")));
        grid.Children.Add(Field(_loc["TeamMetaElapsed"], Value(run is null ? "—" : ChatFormatting.Duration(run.Elapsed))));
        grid.Children.Add(Field(_loc["TeamTokenInput"], Value(run is null ? "—" : ChatFormatting.Compact(run.InputTokens))));
        grid.Children.Add(Field(_loc["TeamTokenOutput"], Value(run is null ? "—" : ChatFormatting.Compact(run.OutputTokens))));
        grid.Children.Add(Field(_loc["TeamTokenCached"], Value(run is null ? "—" : ChatFormatting.Compact(run.CachedInputTokens))));
        grid.Children.Add(Field(_loc["TeamStatCost"], Value(run is not null && CostOf(run, from.Member) is { } cost ? ChatPanelView.FormatCost(cost) : "—")));

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(grid);
        return new Border { Name = "TeamMessageMeta", Classes = { "tcMeta" }, Child = stack };

        static TextBlock Value(string value) => TeamUi.Line(value, "VelaTextPrimary", 12, mono: true);

        static StackPanel Field(string key, Control value)
        {
            var field = new StackPanel { Margin = new Thickness(0, 4, 8, 0) };
            field.Children.Add(TeamUi.Line(key, "VelaTextSecondary", 11));
            field.Children.Add(value);
            return field;
        }
    }

    /// <summary>
    /// 一条消息的气泡:头像 + 正文框(谁、类型、时刻、任务 → 消息本身 → 它带来的那份报告 → 用到的工具 → 用量)。
    /// 左边那位(抽屉标题里写在前面的)的气泡靠左,对方的靠右并带一点对方的身份色。
    /// </summary>
    private Grid Bubble(TeamMessageInfo message, bool mine, bool focused)
    {
        Party from = PartyOf(message.From);
        var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void Meta(Control item)
        {
            item.Margin = new Thickness(0, 0, 6, 0);
            meta.Children.Add(item);
        }
        Meta(TeamUi.Text(from.Name, "VelaTextPrimary", 12, FontWeight.SemiBold));
        Meta(KindTag(message.Kind));
        Meta(TeamUi.Text(message.At.ToLocalTime().ToString("HH:mm:ss"), "VelaTextTertiary", 12, mono: true));
        if (message.TaskId is { Length: > 0 } taskId) Meta(TeamUi.Chip(taskId, radius: 5));

        SelectableTextBlock text = new SelectableTextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold }
            .Bind(TextBlock.FontSizeProperty, "VelaFontSize13").Bind(TextBlock.ForegroundProperty, "VelaTextPrimary");
        var body = new StackPanel();
        body.Children.Add(meta);
        body.Children.Add(text);

        if (LinkedRun(message) is { } linked)
        {
            if (linked.Task.Report.Length > 0)
            {
                body.Children.Add(new ScrollViewer
                {
                    MaxHeight = 320,
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = Markdown(linked.Task.Report)
                });
            }
            if (linked.Run is { } run)
            {
                if (run.ToolsUsed.Count > 0)
                {
                    var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    Viewbox wrench = TeamUi.Icon("AiIcon.wrench", "VelaTextTertiary", 12);
                    wrench.Margin = new Thickness(0, 0, 6, 0);
                    tools.Children.Add(wrench);
                    foreach (SubagentToolUsage tool in run.ToolsUsed)
                    {
                        Border chip = TeamUi.Chip($"{tool.Name} ×{tool.Count}", radius: 5);
                        chip.Margin = new Thickness(0, 0, 6, 4);
                        tools.Children.Add(chip);
                    }
                    body.Children.Add(tools);
                }
                if (run.InputTokens + run.OutputTokens > 0)
                {
                    string foot = $"in {ChatFormatting.Compact(run.InputTokens)} · out {ChatFormatting.Compact(run.OutputTokens)}"
                                  + (run.CachedInputTokens > 0 ? $" · cache {ChatFormatting.Compact(run.CachedInputTokens)}" : "")
                                  + $" · {ChatFormatting.Duration(run.Elapsed)}"
                                  + (CostOf(run, from.Member) is { } cost ? " · " + ChatPanelView.FormatCost(cost) : "");
                    body.Children.Add(new Border
                    {
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Margin = new Thickness(0, 6, 0, 0),
                        Padding = new Thickness(0, 6, 0, 0),
                        Child = TeamUi.Text(foot, "VelaTextTertiary", 11, mono: true)
                    }.Bind(Border.BorderBrushProperty, "VelaBorderPrimary"));
                }
            }
        }

        var frame = new Border { Classes = { "tcBubble" }, ClipToBounds = true, Tag = message };
        frame.Classes.Set("focus", focused);
        var layers = new Panel();
        // 对方的气泡叠一层很淡的身份色,左右两边一眼分得开
        if (!mine) layers.Children.Add(new Border { Opacity = 0.07, IsHitTestVisible = false }.Bind(Border.BackgroundProperty, from.ColorKey));
        layers.Children.Add(new Border { Padding = new Thickness(12, 10), Child = body });
        frame.Child = layers;
        // 点气泡 = 把它设成高亮的那条(顶上的元数据框换成它的)
        frame.PointerPressed += (_, e) =>
        {
            if (_drawerView is not { Kind: "pair" } view) return;
            e.Handled = true;
            _drawerView = view with { Focus = message };
            RenderDrawer(resetScroll: false);
        };

        Viewbox avatar = TeamAvatar.Create(from.Face, 34);
        avatar.VerticalAlignment = VerticalAlignment.Top;
        var row = new Grid { ColumnSpacing = 10 };
        if (mine)
        {
            row.ColumnDefinitions = [with("Auto,*")];
            row.Margin = new Thickness(0, 0, 34, 0);
            row.Children.Add(avatar);
            row.Children.Add(frame.At(1));
            frame.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            row.ColumnDefinitions = [with("*,Auto")];
            row.Margin = new Thickness(34, 0, 0, 0);
            row.Children.Add(frame);
            row.Children.Add(avatar.At(1));
            frame.HorizontalAlignment = HorizontalAlignment.Right;
        }
        return row;
    }
}
