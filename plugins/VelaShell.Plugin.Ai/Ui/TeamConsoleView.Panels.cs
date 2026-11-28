using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 团队控制台的各块面板:统计、右栏(分工 = 成员状态 / 通信 = 通信记录)、任务进度与归属、Token 消耗。
/// 每块都是「建一次、之后原地改」:团队在跑时每秒都有数字在变,整块重建会让悬停提示与滚动位置跟着丢。
/// </summary>
internal sealed partial class TeamConsoleView
{
    // ═══════════════════════════ 进度条 ═══════════════════════════

    /// <summary>一根细进度条:凹槽里按比例填一段颜色。</summary>
    private sealed class Meter
    {
        private readonly Grid _grid = new();
        private readonly Border _fill = new();
        private string _color = "";

        public Border Root { get; }

        public Meter(double height, double radius = 9)
        {
            _fill.CornerRadius = new CornerRadius(radius);
            _grid.ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Star));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            _grid.Children.Add(_fill);
            Root = new Border { Classes = { "tcTrough" }, Height = height, CornerRadius = new CornerRadius(radius), Child = _grid };
        }

        public void Set(double fraction, string colorKey)
        {
            fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
            _grid.ColumnDefinitions[0].Width = new GridLength(fraction, GridUnitType.Star);
            _grid.ColumnDefinitions[1].Width = new GridLength(1 - fraction, GridUnitType.Star);
            if (colorKey != _color)
            {
                _color = colorKey;
                _fill.Bind(Border.BackgroundProperty, colorKey);
            }
        }
    }

    // ═══════════════════════════ 统计 ═══════════════════════════

    private readonly UniformGrid _stats = new() { Columns = 6, Margin = new Thickness(-6) };
    private StatTile? _statProgress, _statActive, _statTasks, _statTokens, _statCost, _statMessages;

    /// <summary>一块统计:小标题、大数字(可带灰色的「/ 总数」)、底下一行说明或一根进度条。</summary>
    private sealed class StatTile
    {
        private readonly TextBlock _value = TeamUi.Text("", "VelaTextPrimary", 20, FontWeight.SemiBold, mono: true);
        private readonly TextBlock _suffix = TeamUi.Text("", "VelaTextSecondary", 14, mono: true);
        private readonly TextBlock _sub = TeamUi.Line("", "VelaTextTertiary", 11);

        public Border Root { get; }

        public Meter? Bar { get; }

        public StatTile(string title, bool withBar, string name)
        {
            _suffix.VerticalAlignment = VerticalAlignment.Bottom;
            _suffix.Margin = new Thickness(0, 0, 0, 2);
            var number = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            number.Children.Add(_value);
            number.Children.Add(_suffix);
            var stack = new StackPanel();
            stack.Children.Add(TeamUi.Line(title, "VelaTextSecondary", 12));
            stack.Children.Add(number);
            if (withBar)
            {
                Bar = new Meter(5);
                Bar.Root.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(Bar.Root);
            }
            else
            {
                stack.Children.Add(_sub);
            }
            Root = new Border { Name = name, Classes = { "tcStat" }, Child = stack };
        }

        public void Set(string value, string suffix = "", string sub = "", string? tip = null)
        {
            _value.Text = value;
            _suffix.Text = suffix;
            _sub.Text = sub;
            ToolTip.SetTip(Root, tip);
        }
    }

    private void BuildStats()
    {
        _statProgress = new StatTile(_loc["TeamStatProgress"], withBar: true, "TeamStatProgress");
        _statActive = new StatTile(_loc["TeamStatActive"], withBar: false, "TeamStatActive");
        _statTasks = new StatTile(_loc["TeamStatTasks"], withBar: false, "TeamStatTasks");
        _statTokens = new StatTile(_loc["TeamStatTokens"], withBar: false, "TeamStatTokens");
        _statCost = new StatTile(_loc["TeamStatCost"], withBar: false, "TeamStatCost");
        _statMessages = new StatTile(_loc["TeamStatMessages"], withBar: false, "TeamStatMessages");
        foreach (StatTile tile in (StatTile[])[_statProgress, _statActive, _statTasks, _statTokens, _statCost, _statMessages])
        {
            _stats.Children.Add(tile.Root);
        }
        // 点「通信」那块统计,右栏翻到通信记录
        Clickable(_statMessages.Root, () => ShowRail("feed"));
    }

    private void RefreshStats()
    {
        int total = Team.Tasks.Count;
        int done = Team.Tasks.Count(t => t.Status == "completed");
        double progress = total == 0 ? 0 : done / (double)total;
        _statProgress?.Set((progress * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%");
        _statProgress?.Bar?.Set(progress, "VelaAccent");

        TeamMemberState[] states = [.. Team.Members.Select(m => TeamConsoleModel.StateOf(m, Team, Runs))];
        int active = states.Count(s => s is TeamMemberState.Working or TeamMemberState.Thinking);
        int stalled = states.Count(s => s is TeamMemberState.Waiting or TeamMemberState.Blocked);
        _statActive?.Set(active.ToString(), $" / {Team.Members.Count}", _loc.F("TeamStatStalled", stalled));

        _statTasks?.Set(done.ToString(), $" / {total}", _loc["TeamStatTasksSub"]);

        long tokens = 0;
        double? cost = null;
        foreach (TeamMemberInfo member in Team.Members)
        {
            tokens += UsageOf(member).Total;
            if (CostOf(member) is { } own) cost = (cost ?? 0) + own;
        }
        IReadOnlyList<double> series = _session.Telemetry.Team;
        _statTokens?.Set(ChatFormatting.Compact(tokens), "",
            series.Count > 0 ? _loc.F("TeamStatRate", ChatFormatting.Compact((long)series[^1]), TeamTelemetry.Interval.TotalSeconds) : _loc["TeamStatTokensSub"]);
        _statCost?.Set(cost is { } sum ? ChatPanelView.FormatCost(sum) : "—", "", _loc["TeamStatCostSub"],
            cost is { } exact ? _loc.F("TeamTotalCostTip", exact.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)) : null);
        _statMessages?.Set(Team.Messages.Count.ToString(), "", ChatFormatting.Duration(Team.Elapsed));
    }

    // ═══════════════════════════ 成员状态 ═══════════════════════════

    private readonly StackPanel _roster = new() { Spacing = 8 };
    private readonly Dictionary<string, RosterCard> _rosterCards = [with(StringComparer.Ordinal)];

    // ═══════════════════════════ 右栏:分工 / 通信 ═══════════════════════════

    /// <summary>右栏不跟拓扑并排(窄屏叠放)时最多多高,再多就在栏里滚。</summary>
    private const double RailStackedHeight = 520;

    private readonly ElasticHost _railBody = new() { MinElasticHeight = 320 };
    private readonly TextBlock _railTitle = TeamUi.Text("", "VelaTextPrimary", 13, FontWeight.SemiBold);
    private readonly TextBlock _railHint = TeamUi.Line("", "VelaTextSecondary", 12);
    private readonly TextBlock _feedTabCount = TeamUi.Text("", "VelaTextTertiary", 11, mono: true);
    private Border _rosterPage = null!, _feedPage = null!, _rosterTab = null!, _feedTab = null!;
    private string _feedHint = "";

    /// <summary>右栏此刻是哪一页:「roster」分工 /「feed」通信(测试用)。</summary>
    internal string RailPage { get; private set; } = "roster";

    /// <summary>
    /// 右栏:一张卡片,头部右端的分段开关在「分工」(成员状态)与「通信」(通信记录)之间切换。
    /// 并排时卡片与左边的拓扑等高,内容在卡片里滚;叠放时限高 <see cref="RailStackedHeight" />。
    /// </summary>
    private Border BuildRailPanel()
    {
        _rosterPage = new Border
        {
            Name = "TeamRosterPanel",
            Child = new ScrollViewer { Name = "TeamRosterScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _roster }
        };
        _feedPage = BuildFeedPage();
        var pages = new Panel();
        pages.Children.Add(_rosterPage);
        pages.Children.Add(_feedPage);
        _railBody.Child = pages;

        _rosterTab = Tab(TeamUi.Text(_loc["TeamRailRoster"], "VelaTextPrimary", 12), "roster");
        var feedLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        feedLabel.Children.Add(TeamUi.Text(_loc["TeamRailFeed"], "VelaTextPrimary", 12));
        feedLabel.Children.Add(_feedTabCount);
        _feedTab = Tab(feedLabel, "feed");
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        tabs.Children.Add(_rosterTab);
        tabs.Children.Add(_feedTab);
        Border switcher = new() { Name = "TeamRailSwitch", Classes = { "tcSwitch" }, Child = tabs, VerticalAlignment = VerticalAlignment.Center };

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(_railTitle);
        heading.Children.Add(_railHint);
        var head = new Grid { ColumnDefinitions = [with("*,Auto")], ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(heading);
        head.Children.Add(switcher.At(1));

        var root = new Grid { RowDefinitions = [with("Auto,*")] };
        root.Children.Add(head);
        root.Children.Add(_railBody.At(0, 1));
        ShowRail("roster");
        return new Border { Name = "TeamRailPanel", Classes = { "tcPanel" }, Child = root };

        Border Tab(Control label, string page)
            => Clickable(new Border { Name = page == "roster" ? "TeamRailRosterTab" : "TeamRailFeedTab", Classes = { "tcSwitchItem" }, Child = label },
                () => ShowRail(page));
    }

    /// <summary>右栏切到「分工」(roster)或「通信」(feed)。</summary>
    internal void ShowRail(string page)
    {
        RailPage = page == "feed" ? "feed" : "roster";
        bool feed = RailPage == "feed";
        _rosterPage.IsVisible = !feed;
        _feedPage.IsVisible = feed;
        _rosterTab.Classes.Set("on", !feed);
        _feedTab.Classes.Set("on", feed);
        _railTitle.Text = _loc[feed ? "TeamFeed" : "TeamRoster"];
        _railHint.Text = feed ? _feedHint : _loc["TeamRosterHint"];
    }

    /// <summary>并排时右栏跟着左边的拓扑等高;叠放时自己限高。</summary>
    private void FitRail(bool wide)
    {
        _railBody.Elastic = wide;
        _railBody.MaxHeight = wide ? double.PositiveInfinity : RailStackedHeight;
    }

    /// <summary>
    /// 高度由旁边决定的容器:量尺寸时只报一个下限(<see cref="MinElasticHeight" />,内容更矮就报内容的高度),
    /// 排版时给多高就占多高,里头的滚动区按这个高度滚。这样右栏既不会把一行撑高,又能填满拓扑那么高。
    /// </summary>
    private sealed class ElasticHost : Decorator
    {
        private double _arranged = double.NaN;

        public double MinElasticHeight { get; init; } = 300;

        public bool Elastic
        {
            get;
            set
            {
                if (field == value) return;
                field = value;
                _arranged = double.NaN;
                InvalidateMeasure();
            }
        } = true;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!Elastic || Child is not { } child) return base.MeasureOverride(availableSize);
            // 按上一次实际排到的高度量,前后两遍的约束一致,不会来回失效
            double height = double.IsNaN(_arranged) ? MinElasticHeight : _arranged;
            child.Measure(new Size(availableSize.Width, Math.Min(height, availableSize.Height)));
            return new Size(child.DesiredSize.Width, Math.Min(child.DesiredSize.Height, MinElasticHeight));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Elastic && Child is { } child && !finalSize.Height.Equals(_arranged))
            {
                _arranged = finalSize.Height;
                child.Measure(finalSize);
            }
            return base.ArrangeOverride(finalSize);
        }
    }

    /// <summary>
    /// 成员卡:头像 | 名字、角色、状态,模型与手上的任务,此刻在做什么 | 用量、迷你走势、上下文占用。
    /// </summary>
    private sealed class RosterCard
    {
        private readonly TeamConsoleView _owner;
        private readonly Decorator _pill = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 3, 0, 0) };
        private readonly TextBlock _activity = TeamUi.Line("", "VelaTextSecondary", 12);
        private readonly TextBlock _tokens = TeamUi.Text("", "VelaTextPrimary", 13, FontWeight.SemiBold, mono: true);
        private readonly TeamSparkline _spark = new() { Width = 96, Height = 22, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Meter _context = new(3);
        private string _pillKey = "", _chipsKey = "";

        public Border Root { get; }

        public RosterCard(TeamConsoleView owner, TeamMemberInfo member)
        {
            _owner = owner;
            Party party = owner.PartyOf(member.Name);
            var nameRow = new WrapPanel();
            TextBlock name = TeamUi.Text(member.Name, "VelaTextPrimary", 13, FontWeight.SemiBold);
            name.Margin = new Thickness(0, 0, 6, 0);
            TextBlock role = TeamUi.Text(member.Role, "VelaTextSecondary", 12);
            role.Margin = new Thickness(0, 0, 6, 0);
            nameRow.Children.Add(name);
            nameRow.Children.Add(role);
            nameRow.Children.Add(_pill);
            _activity.Margin = new Thickness(0, 2, 0, 0);
            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            middle.Children.Add(nameRow);
            middle.Children.Add(_chips);
            middle.Children.Add(_activity);

            _spark.Bind(TeamSparkline.StrokeProperty, party.ColorKey);
            _context.Root.Width = 96;
            _context.Root.Margin = new Thickness(0, 4, 0, 0);
            _context.Root.HorizontalAlignment = HorizontalAlignment.Right;
            var tokenRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            tokenRow.Children.Add(_tokens);
            tokenRow.Children.Add(TeamUi.Text("tok", "VelaTextTertiary", 12));
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(tokenRow);
            right.Children.Add(_spark);
            right.Children.Add(_context.Root);

            var grid = new Grid { ColumnDefinitions = [with("44,*,Auto")], ColumnSpacing = 10 };
            grid.Children.Add(TeamAvatar.Create(party.Face, 44));
            grid.Children.Add(middle.At(1));
            grid.Children.Add(right.At(2));
            Root = Clickable(new Border { Classes = { "tcCard" }, Child = grid, Tag = member.Name },
                () => owner.OpenView(new DrawerView("agent", member.Name)));
        }

        public void Update(TeamMemberInfo member)
        {
            TeamMemberState state = TeamConsoleModel.StateOf(member, _owner.Team, _owner.Runs);
            (string key, string color) = StateLook(state);
            if (key != _pillKey)
            {
                _pillKey = key;
                _pill.Child = TeamUi.Pill(_owner._loc[key], color);
            }
            TeamMemberUsage usage = _owner.UsageOf(member);
            string model = _owner.ModelName(usage.ModelId);
            string current = string.Join(" ", _owner.Team.Tasks.Where(t => t.Assignee == member.Name && t.Status is "running" or "pending").Select(t => t.Id));
            string permissions = (member.AllowWrites ? "✎" : "") + (member.AllowCommands ? " >_" : "");
            string chipsKey = model + "|" + current + "|" + permissions;
            if (chipsKey != _chipsKey)
            {
                _chipsKey = chipsKey;
                _chips.Children.Clear();
                if (model.Length > 0) AddChip(TeamUi.Chip(model, "VelaAccent", "VelaAccent"), _owner.ModelLine(usage.ModelId));
                if (current.Length > 0) AddChip(TeamUi.Chip(current), null);
                // ✎ = 可改文件,>_ = 可执行命令(两者都要主控显式开,执行时照旧审批)
                if (permissions.Length > 0)
                {
                    AddChip(TeamUi.Chip(permissions.Trim()), string.Join(" · ", ((string?[])
                    [
                        member.AllowWrites ? _owner._loc["SubagentWritable"] : null,
                        member.AllowCommands ? _owner._loc["SubagentCommands"] : null
                    ]).OfType<string>()));
                }
            }
            string activity = _owner.ActivityOf(member);
            _activity.Text = activity.Length > 0 ? activity : "—";
            ToolTip.SetTip(_activity, activity.Length > 0 ? activity : null);
            _tokens.Text = ChatFormatting.Compact(usage.Total);
            _spark.Values = _owner._session.Telemetry.Member(member.Name);
            double? share = _owner.ContextShare(usage);
            _context.Set(share ?? 0, share > 0.85 ? "VelaError" : _owner.PartyOf(member.Name).ColorKey);
            ToolTip.SetTip(_context.Root, share is { } s ? _owner._loc.F("TeamContextUse", Percent(s)) : null);

            void AddChip(Border chip, string? tip)
            {
                chip.Margin = new Thickness(0, 0, 6, 2);
                ToolTip.SetTip(chip, tip);
                _chips.Children.Add(chip);
            }
        }
    }

    private void RefreshRoster()
    {
        foreach (TeamMemberInfo member in Team.Members)
        {
            if (!_rosterCards.TryGetValue(member.Name, out RosterCard? card))
            {
                card = new RosterCard(this, member);
                _rosterCards[member.Name] = card;
                _roster.Children.Add(card.Root);
            }
            card.Update(member);
        }
    }

    // ═══════════════════════════ 任务进度与归属 ═══════════════════════════

    private readonly StackPanel _gantt = new() { Spacing = 2 };
    private readonly Dictionary<string, GanttRow> _ganttRows = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Grid _ganttScale = new();
    private double _ganttSpan;

    /// <summary>时间轴的刻度候选(秒):每一档都能被 6 整除,刻度标出来是整数。</summary>
    private static readonly double[] SpanSteps = [60, 90, 120, 180, 240, 300, 420, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 21600];

    /// <summary>时间轴的总长:比已运行的时间多留一成,取够得着的最小一档。</summary>
    internal static double SpanFor(TimeSpan elapsed)
    {
        double needed = elapsed.TotalSeconds * 1.1;
        foreach (double step in SpanSteps)
        {
            if (step >= needed) return step;
        }
        return Math.Ceiling(needed / 3600) * 3600;
    }

    private static string ScaleLabel(double seconds, double span)
        => span <= 180 ? $"{seconds:0}s" : $"{seconds / 60:0.#}m";

    private Border BuildGanttPanel()
    {
        var head = new Grid { ColumnDefinitions = [with("200,*,84")], ColumnSpacing = 10, Margin = new Thickness(6, 0, 6, 4) };
        head.Children.Add(TeamUi.Text(_loc["TeamGanttColumn"], "VelaTextSecondary", 11));
        head.Children.Add(_ganttScale.At(1));
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(_gantt);
        return Panel(_loc["TeamGantt"], _loc["TeamGanttHint"], TeamUi.Text(_loc["TeamGanttLegend"], "VelaTextSecondary", 12), stack, "TeamGanttPanel");
    }

    /// <summary>
    /// 时间轴上的一行:任务 id、标题、负责人(与审查者)头像 | 任务条(起点 = 开跑时刻,长度 = 已耗时,
    /// 填色 = 负责人的身份色)与「现在」线 | 状态。
    /// </summary>
    private sealed class GanttRow
    {
        private readonly TeamConsoleView _owner;
        private readonly TextBlock _title = TeamUi.Line("", "VelaTextPrimary", 12);
        private readonly StackPanel _owners = new() { Orientation = Orientation.Horizontal };
        private readonly Grid _barSlots = new(), _nowSlots = new();
        private readonly Border _bar = new() { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), ClipToBounds = true, Margin = new Thickness(0, 3) };
        private readonly Border _wash = new(), _fill = new();
        private readonly TextBlock _label = TeamUi.Text("", "VelaTextPrimary", 11, FontWeight.SemiBold, mono: true);
        private readonly Decorator _status = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        private string _ownersKey = "", _statusKey = "", _color = "";

        public Border Root { get; }

        public GanttRow(TeamConsoleView owner, TeamTaskInfo task)
        {
            _owner = owner;
            TextBlock id = TeamUi.Line(task.Id, "VelaTextSecondary", 11, mono: true);
            id.MaxWidth = 56;
            var label = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], ColumnSpacing = 8 };
            label.Children.Add(id);
            label.Children.Add(_title.At(1));
            label.Children.Add(_owners.At(2));

            // 凹槽上的六等分刻度线
            var ticks = new Grid();
            for (int i = 0; i < 6; i++)
            {
                ticks.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                if (i == 5) break;
                ticks.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 1, 0) }
                    .Bind(Border.BorderBrushProperty, "VelaBorderPrimary").At(i));
            }
            _label.HorizontalAlignment = HorizontalAlignment.Right;
            _label.Margin = new Thickness(0, 0, 6, 0);
            var inner = new Panel();
            inner.Children.Add(_wash);
            inner.Children.Add(_fill);
            inner.Children.Add(_label);
            _bar.Child = inner;
            for (int i = 0; i < 3; i++) _barSlots.ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Star));
            _barSlots.ColumnDefinitions[1].MinWidth = 6;
            _barSlots.Children.Add(_bar.At(1));
            // 「现在」线:一根强调色的虚线,比凹槽上下各高出一点
            Line now = new Line { StartPoint = new Point(0, 0), EndPoint = new Point(0, 30), StrokeThickness = 1.5, StrokeDashArray = [2.5, 2], Margin = new Thickness(0, -4) }
                .Bind(Shape.StrokeProperty, "VelaAccent");
            _nowSlots.ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Star));
            _nowSlots.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _nowSlots.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            _nowSlots.Children.Add(now.At(1));
            _nowSlots.IsHitTestVisible = false;
            var track = new Panel { Height = 22 };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = ticks });
            track.Children.Add(_barSlots);
            track.Children.Add(_nowSlots);

            var grid = new Grid { ColumnDefinitions = [with("200,*,84")], ColumnSpacing = 10 };
            grid.Children.Add(label);
            grid.Children.Add(track.At(1));
            grid.Children.Add(_status.At(2));
            Root = Clickable(new Border { Classes = { "tcRow" }, Child = grid, Tag = task.Id },
                () => owner.OpenView(new DrawerView("task", task.Id)));
        }

        public void Update(TeamTaskInfo task, double span)
        {
            _title.Text = task.Title;
            ToolTip.SetTip(_title, $"#{task.Id} · {task.Title}");

            TeamTaskInfo? review = _owner.ReviewerOf(task);
            string ownersKey = task.Assignee + "|" + review?.Assignee;
            if (ownersKey != _ownersKey)
            {
                _ownersKey = ownersKey;
                _owners.Children.Clear();
                _owners.Children.Add(_owner.Avatar(task.Assignee, 22));
                if (review is not null && review.Assignee != task.Assignee)
                {
                    Viewbox reviewer = _owner.Avatar(review.Assignee, 18);
                    reviewer.Margin = new Thickness(-6, 0, 0, 0);
                    _owners.Children.Add(reviewer);
                }
                ToolTip.SetTip(_owners, _owner._loc.F("TeamOwnerTip", task.Assignee)
                    + (review is not null ? " · " + _owner._loc.F("TeamReviewerTip", review.Assignee) : ""));
            }

            string color = task.Status == "failed" ? "VelaError" : _owner.PartyOf(task.Assignee).ColorKey;
            if (color != _color)
            {
                _color = color;
                _bar.Bind(Border.BorderBrushProperty, color);
                _wash.Bind(Border.BackgroundProperty, color);
                _fill.Bind(Border.BackgroundProperty, color);
            }
            // 条的位置:开跑时刻起、长到已耗时;没开跑过的任务没有条。老数据没记开跑时刻,从 0 起画
            bool started = task.Elapsed > TimeSpan.Zero || task.Status == "running";
            double left = Math.Clamp((task.StartOffset ?? TimeSpan.Zero).TotalSeconds / span, 0, 1);
            double width = Math.Clamp(task.Elapsed.TotalSeconds / span, 0, 1 - left);
            _barSlots.ColumnDefinitions[0].Width = new GridLength(left, GridUnitType.Star);
            _barSlots.ColumnDefinitions[1].Width = new GridLength(width, GridUnitType.Star);
            _barSlots.ColumnDefinitions[2].Width = new GridLength(Math.Max(0, 1 - left - width), GridUnitType.Star);
            _bar.IsVisible = started;
            _wash.Opacity = 0.22;
            // 填色:做完的实心,在跑的半透明(没有可信的完成度,不装成百分比),取消 / 受阻的只留淡底
            _fill.Opacity = task.Status switch { "completed" or "failed" => 0.85, "running" => 0.45, _ => 0 };
            _label.Text = task.Status == "completed" ? "100%" : started ? ChatFormatting.Duration(task.Elapsed) : "";
            ToolTip.SetTip(_bar, started ? _owner._loc.F("TeamBarTip",
                ChatFormatting.Duration(task.StartOffset ?? TimeSpan.Zero), ChatFormatting.Duration(task.Elapsed)) : null);

            double now = Math.Clamp(_owner.Team.Elapsed.TotalSeconds / span, 0, 1);
            _nowSlots.ColumnDefinitions[0].Width = new GridLength(now, GridUnitType.Star);
            _nowSlots.ColumnDefinitions[2].Width = new GridLength(1 - now, GridUnitType.Star);

            (string key, string tone) = TaskLook(task);
            if (key + tone != _statusKey)
            {
                _statusKey = key + tone;
                _status.Child = TeamUi.Pill(_owner._loc[key], tone);
            }
        }
    }

    private void RefreshGantt()
    {
        double span = SpanFor(Team.Elapsed);
        if (span != _ganttSpan)
        {
            _ganttSpan = span;
            _ganttScale.Children.Clear();
            _ganttScale.ColumnDefinitions.Clear();
            // 七个刻度标在六等分的边界上:首尾贴边,中间的各居中在分界线上
            for (int i = 0; i < 7; i++)
            {
                _ganttScale.ColumnDefinitions.Add(new ColumnDefinition(i is 0 or 6 ? 0.5 : 1, GridUnitType.Star));
                TextBlock tick = TeamUi.Text(ScaleLabel(span / 6 * i, span), "VelaTextTertiary", 11, mono: true);
                tick.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 6 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                _ganttScale.Children.Add(tick.At(i));
            }
        }
        foreach (TeamTaskInfo task in Team.Tasks)
        {
            if (!_ganttRows.TryGetValue(task.Id, out GanttRow? row))
            {
                row = new GanttRow(this, task);
                _ganttRows[task.Id] = row;
                _gantt.Children.Add(row.Root);
            }
            row.Update(task, span);
        }
    }

    // ═══════════════════════════ Token 消耗 ═══════════════════════════

    private readonly StackPanel _tokenRows = new();
    private readonly Dictionary<string, TokenRow> _tokenRowMap = [with(StringComparer.Ordinal)];
    private readonly Grid _modelBar = new() { Height = 14 };
    private readonly UniformGrid _modelLegend = new() { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _rateNow = TeamUi.Text("", "VelaTextPrimary", 12, mono: true);
    private readonly TeamSparkline _rate = new() { Height = 110, Thickness = 2, Headroom = 1.15 };
    private readonly TextBlock _rateSpan = TeamUi.Text("", "VelaTextTertiary", 11, mono: true);
    private readonly TextBlock _ratePeak = TeamUi.Text("", "VelaTextTertiary", 11, mono: true);
    private readonly TextBlock _rateEmpty = TeamUi.Text("", "VelaTextSecondary", 12);
    private string _tokenOrder = "", _modelKey = "";

    private Border BuildTokensPanel()
    {
        var key = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        key.Children.Add(Swatch("VelaAccent", 1, _loc["TeamTokenInput"]));
        key.Children.Add(Swatch("VelaAccent", 0.35, _loc["TeamTokenCached"]));
        key.Children.Add(Swatch("VelaInfo", 1, _loc["TeamTokenOutput"]));

        var rateHead = new Grid { ColumnDefinitions = [with("*,Auto")], Margin = new Thickness(0, 14, 0, 6) };
        rateHead.Children.Add(TeamUi.Text(_loc.F("TeamTokenRate", TeamTelemetry.Interval.TotalSeconds), "VelaTextSecondary", 12, FontWeight.SemiBold));
        rateHead.Children.Add(_rateNow.At(1));
        _rate.Bind(TeamSparkline.StrokeProperty, "VelaAccent").Bind(TeamSparkline.GuideProperty, "VelaBorderPrimary");
        var rateScale = new Grid { ColumnDefinitions = [with("*,Auto,*")] };
        rateScale.Children.Add(_rateSpan);
        rateScale.Children.Add(_ratePeak.At(1));
        TextBlock nowLabel = TeamUi.Text(_loc["TeamRateNow"], "VelaTextTertiary", 11, mono: true);
        nowLabel.HorizontalAlignment = HorizontalAlignment.Right;
        rateScale.Children.Add(nowLabel.At(2));

        var stack = new StackPanel();
        stack.Children.Add(key);
        stack.Children.Add(_tokenRows);
        TextBlock byModel = TeamUi.Text(_loc["TeamTokenByModel"], "VelaTextSecondary", 12, FontWeight.SemiBold);
        byModel.Margin = new Thickness(0, 14, 0, 6);
        stack.Children.Add(byModel);
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = _modelBar });
        stack.Children.Add(_modelLegend);
        stack.Children.Add(rateHead);
        _rateEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _rateEmpty.TextWrapping = TextWrapping.Wrap;
        var chart = new Panel { MinHeight = 110 };
        chart.Children.Add(_rate);
        chart.Children.Add(_rateEmpty);
        stack.Children.Add(chart);
        stack.Children.Add(rateScale);
        return Panel(_loc["TeamTokensPanel"], null, TeamUi.Text(_loc["TeamStatCostSub"], "VelaTextSecondary", 11), stack, "TeamTokensPanel");

        static StackPanel Swatch(string color, double opacity, string text)
        {
            Border box = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Opacity = opacity, VerticalAlignment = VerticalAlignment.Center }
                .Bind(Border.BackgroundProperty, color);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 12, 0) };
            row.Children.Add(box);
            row.Children.Add(TeamUi.Text(text, "VelaTextSecondary", 11));
            return row;
        }
    }

    /// <summary>一名成员的用量条:新输入 | 缓存命中 | 输出,三段的长度按全队用得最多的那位折算。</summary>
    private sealed class TokenRow
    {
        private readonly TeamConsoleView _owner;
        private readonly Grid _segments = new();
        private readonly TextBlock _total = TeamUi.Text("", "VelaTextPrimary", 12, mono: true);

        public Border Root { get; }

        public TokenRow(TeamConsoleView owner, TeamMemberInfo member)
        {
            _owner = owner;
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(owner.Avatar(member.Name, 18));
            name.Children.Add(TeamUi.Line(member.Name, "VelaTextPrimary", 12));
            for (int i = 0; i < 4; i++) _segments.ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Star));
            _segments.Children.Add(new Border().Bind(Border.BackgroundProperty, "VelaAccent"));
            _segments.Children.Add(new Border { Opacity = 0.35 }.Bind(Border.BackgroundProperty, "VelaAccent").At(1));
            _segments.Children.Add(new Border().Bind(Border.BackgroundProperty, "VelaInfo").At(2));
            _total.HorizontalAlignment = HorizontalAlignment.Right;
            var grid = new Grid { ColumnDefinitions = [with("110,*,64")], ColumnSpacing = 10 };
            grid.Children.Add(name);
            grid.Children.Add(new Border { Classes = { "tcTrough" }, Height = 12, CornerRadius = new CornerRadius(4), Child = _segments }.At(1));
            grid.Children.Add(_total.At(2));
            Root = Clickable(new Border { Classes = { "tcRow" }, Padding = new Thickness(6, 3), Child = grid, Tag = member.Name },
                () => owner.OpenView(new DrawerView("agent", member.Name)));
        }

        public void Update(TeamMemberInfo member, TeamMemberUsage usage, long max)
        {
            double scale = Math.Max(1, max);
            double fresh = (usage.Input - usage.Cached) / scale, cached = usage.Cached / scale, output = usage.Output / scale;
            _segments.ColumnDefinitions[0].Width = new GridLength(fresh, GridUnitType.Star);
            _segments.ColumnDefinitions[1].Width = new GridLength(cached, GridUnitType.Star);
            _segments.ColumnDefinitions[2].Width = new GridLength(output, GridUnitType.Star);
            _segments.ColumnDefinitions[3].Width = new GridLength(Math.Max(0, 1 - fresh - cached - output), GridUnitType.Star);
            _total.Text = ChatFormatting.Compact(usage.Total);
            ToolTip.SetTip(Root, _owner._loc.F("TeamTokens", member.Name, ChatFormatting.Compact(usage.Input), ChatFormatting.Compact(usage.Output))
                + (usage.Cached > 0 ? "\n" + _owner._loc.F("TeamCachedTip", ChatFormatting.Compact(usage.Cached)) : "")
                + (_owner.CostOf(member) is { } cost ? "\n" + ChatPanelView.FormatCost(cost) : ""));
        }
    }

    private void RefreshTokens()
    {
        var usages = Team.Members.Select(m => (Member: m, Usage: UsageOf(m))).ToList();
        long max = usages.Count == 0 ? 0 : usages.Max(u => u.Usage.Total);
        // 用得多的排前面;次序变了才重排,免得每一拍都拆装一遍
        var sorted = usages.OrderByDescending(u => u.Usage.Total).ToList();
        string order = string.Join("|", sorted.Select(u => u.Member.Name));
        foreach ((TeamMemberInfo member, TeamMemberUsage usage) in sorted)
        {
            if (!_tokenRowMap.TryGetValue(member.Name, out TokenRow? row)) _tokenRowMap[member.Name] = row = new TokenRow(this, member);
            row.Update(member, usage, max);
        }
        if (order != _tokenOrder)
        {
            _tokenOrder = order;
            _tokenRows.Children.Clear();
            foreach ((TeamMemberInfo member, _) in sorted) _tokenRows.Children.Add(_tokenRowMap[member.Name].Root);
        }

        // 按模型分布:同一个模型的成员合并成一段
        var byModel = usages.Where(u => u.Usage.ModelId is not null).GroupBy(u => u.Usage.ModelId!, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Tokens: g.Sum(u => u.Usage.Total), Count: g.Count(),
                Cost: g.Select(u => CostOf(u.Member)).Aggregate((double?)null, (sum, c) => c is null ? sum : (sum ?? 0) + c)))
            .ToList();
        long all = byModel.Sum(m => m.Tokens);
        string modelKey = string.Join("|", byModel.Select(m => $"{m.Id}:{m.Tokens}:{m.Count}:{m.Cost}"));
        if (modelKey != _modelKey)
        {
            _modelKey = modelKey;
            _modelBar.Children.Clear();
            _modelBar.ColumnDefinitions.Clear();
            _modelLegend.Children.Clear();
            _modelBar.ColumnSpacing = 2;
            for (int i = 0; i < byModel.Count; i++)
            {
                (string id, long tokens, int count, double? cost) = byModel[i];
                string color = ModelPalette[i % ModelPalette.Length];
                double share = all == 0 ? 1.0 / byModel.Count : tokens / (double)all;
                _modelBar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(share, 0.0001), GridUnitType.Star));
                Border segment = new Border().Bind(Border.BackgroundProperty, color).At(i);
                ToolTip.SetTip(segment, ModelLine(id));
                _modelBar.Children.Add(segment);

                Border swatch = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center }
                    .Bind(Border.BackgroundProperty, color);
                TextBlock share2 = TeamUi.Text(Percent(share) + (cost is { } c ? " · " + ChatPanelView.FormatCost(c) : ""), "VelaTextPrimary", 11, mono: true);
                var legend = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], ColumnSpacing = 6, Margin = new Thickness(0, 2, 12, 2) };
                legend.Children.Add(swatch);
                legend.Children.Add(TeamUi.Line($"{ModelName(id)} ×{count}", "VelaTextSecondary", 11).At(1));
                legend.Children.Add(share2.At(2));
                _modelLegend.Children.Add(legend);
            }
        }

        IReadOnlyList<double> series = _session.Telemetry.Team;
        bool sampled = series.Count >= 2;
        _rate.Values = series;
        _rate.IsVisible = sampled;
        _rateEmpty.IsVisible = !sampled;
        _rateEmpty.Text = _loc[Team.Phase == "running" ? "TeamRateWarming" : "TeamRateNone"];
        _rateNow.Text = sampled ? ChatFormatting.Compact((long)series[^1]) : "";
        _rateSpan.Text = sampled ? "-" + ChatFormatting.Duration(TimeSpan.FromSeconds(series.Count * TeamTelemetry.Interval.TotalSeconds)) : "";
        _ratePeak.Text = sampled ? _loc.F("TeamRatePeak", ChatFormatting.Compact((long)series.Max())) : "";
    }

    // ═══════════════════════════ 通信记录(右栏的「通信」页) ═══════════════════════════

    /// <summary>通信记录最多画多少行(最新的在上)。</summary>
    internal const int MaxFeedRows = 120;

    private static readonly string[] FeedKinds = ["assign", "handoff", "peer", "report", "review", "system"];

    private readonly StackPanel _feed = new();
    private readonly ComboBox _feedPartyBox = new() { Name = "TeamFeedParty", Classes = { "tcSelect" } };
    private string[] _feedParties = [];
    private bool _syncingParty;
    private string _feedKind = "all", _feedQuery = "", _feedKey = "";
    private TeamMessageInfo? _newest;

    /// <summary>通信记录此刻按哪名成员筛(「all」= 不筛;测试用)。</summary>
    internal string FeedParty { get; private set; } = "all";

    /// <summary>「通信」页:一行筛选(成员 / 类型 / 关键字),下面是可滚动的消息列表。</summary>
    private Border BuildFeedPage()
    {
        _feedPartyBox.SelectionChanged += (_, _) =>
        {
            if (_syncingParty || _feedPartyBox.SelectedIndex < 0 || _feedPartyBox.SelectedIndex >= _feedParties.Length) return;
            FeedParty = _feedParties[_feedPartyBox.SelectedIndex];
            RefreshFeed(force: true);
        };
        var kinds = new ComboBox { Name = "TeamFeedKind", Classes = { "tcSelect" } };
        kinds.Items.Add(_loc["TeamFeedAllKinds"]);
        foreach (string kind in FeedKinds) kinds.Items.Add(_loc[KindLook(kind).Key]);
        kinds.SelectedIndex = 0;
        kinds.SelectionChanged += (_, _) =>
        {
            _feedKind = kinds.SelectedIndex <= 0 ? "all" : FeedKinds[kinds.SelectedIndex - 1];
            RefreshFeed(force: true);
        };
        var search = new TextBox { Name = "TeamFeedSearch", PlaceholderText = _loc["TeamFeedSearch"] };
        search.TextChanged += (_, _) =>
        {
            _feedQuery = search.Text?.Trim() ?? "";
            RefreshFeed(force: true);
        };
        var searchGrid = new Grid { ColumnDefinitions = [with("Auto,*")] };
        searchGrid.Children.Add(TeamUi.Icon("AiIcon.search", "VelaTextTertiary", 13));
        searchGrid.Children.Add(search.At(1));
        // 栏窄:两个下拉按内容收窄,搜索框占剩下的宽度
        _feedPartyBox.MinWidth = kinds.MinWidth = 0;
        _feedPartyBox.MaxWidth = kinds.MaxWidth = 140;
        var tools = new Grid { ColumnDefinitions = [with("Auto,Auto,*")], ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(_feedPartyBox);
        tools.Children.Add(kinds.At(1));
        tools.Children.Add(new Border { Classes = { "tcSearch" }, Width = double.NaN, Child = searchGrid }.At(2));

        Border list = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new ScrollViewer { Name = "TeamFeedScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _feed }
        }.Bind(Border.BorderBrushProperty, "VelaBorderPrimary");
        var grid = new Grid { RowDefinitions = [with("Auto,*")] };
        grid.Children.Add(tools);
        grid.Children.Add(list.At(0, 1));
        return new Border { Name = "TeamFeedPanel", Child = grid };
    }

    /// <summary>成员下拉:全部成员、主控、各成员;成员有增减时重填,尽量保住当前的选择。</summary>
    private void EnsurePartyFilter()
    {
        string[] parties = ["all", TeamConsoleModel.Lead, .. Team.Members.Select(m => m.Name)];
        if (parties.SequenceEqual(_feedParties)) return;
        _feedParties = parties;
        _syncingParty = true;
        try
        {
            _feedPartyBox.Items.Clear();
            foreach (string id in parties) _feedPartyBox.Items.Add(id == "all" ? _loc["TeamFeedAllMembers"] : PartyOf(id).Name);
            if (Array.IndexOf(parties, FeedParty) < 0) FeedParty = "all";
            _feedPartyBox.SelectedIndex = Array.IndexOf(parties, FeedParty);
        }
        finally
        {
            _syncingParty = false;
        }
    }

    /// <summary>按成员筛通信记录(「all」= 不筛)。</summary>
    internal void SetFeedParty(string party)
    {
        EnsurePartyFilter();
        FeedParty = Array.IndexOf(_feedParties, party) >= 0 ? party : "all";
        _syncingParty = true;
        try
        {
            _feedPartyBox.SelectedIndex = Array.IndexOf(_feedParties, FeedParty);
        }
        finally
        {
            _syncingParty = false;
        }
        RefreshFeed(force: true);
    }

    private void RefreshFeed(bool force)
    {
        EnsurePartyFilter();
        TeamMessageInfo? last = Team.Messages.Count > 0 ? Team.Messages[^1] : null;
        string key = $"{Team.Messages.Count}|{last?.At.Ticks}|{FeedParty}|{_feedKind}|{_feedQuery}";
        if (!force && key == _feedKey) return;
        // 团队在跑时新到的那一条要闪一下;第一次画和改筛选条件时不闪
        bool arrived = !force && _feedKey.Length > 0 && last is not null && !ReferenceEquals(last, _newest);
        _feedKey = key;
        _newest = last;
        _feedTabCount.Text = Team.Messages.Count.ToString();

        List<TeamMessageInfo> shown =
        [
            .. Team.Messages.Where(m => (FeedParty == "all" || TeamConsoleModel.Involves(m, FeedParty))
                                        && (_feedKind == "all" || m.Kind == _feedKind || (_feedKind == "system" && !FeedKinds.Contains(m.Kind)))
                                        && (_feedQuery.Length == 0 || m.Text.Contains(_feedQuery, StringComparison.OrdinalIgnoreCase)))
        ];
        shown.Reverse();
        _feedHint = _loc.F("TeamFeedCount", shown.Count);
        if (RailPage == "feed") _railHint.Text = _feedHint;
        _feed.Children.Clear();
        foreach (TeamMessageInfo message in shown.Take(MaxFeedRows))
        {
            _feed.Children.Add(FeedRow(message, arrived && ReferenceEquals(message, last)));
        }
        if (shown.Count == 0)
        {
            TextBlock empty = TeamUi.Text(_loc["TeamFeedEmpty"], "VelaTextSecondary", 12);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(20);
            _feed.Children.Add(empty);
        }
    }

    /// <summary>
    /// 通信记录的一行(按右栏的宽度排成两行):谁 → 谁 · 类型 | 任务 · 用量 · 时刻;下面一行是内容。
    /// </summary>
    private Border FeedRow(TeamMessageInfo message, bool fresh)
    {
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, ClipToBounds = true };
        pair.Children.Add(Avatar(message.From, 18));
        pair.Children.Add(TeamUi.Text(PartyOf(message.From).Name, "VelaTextPrimary", 12, FontWeight.SemiBold));
        pair.Children.Add(TeamUi.Text("→", "VelaTextTertiary", 12));
        pair.Children.Add(Avatar(message.To, 18));
        pair.Children.Add(TeamUi.Text(PartyOf(message.To).Name, "VelaTextPrimary", 12, FontWeight.SemiBold));
        Panel kind = KindTag(message.Kind);
        kind.Margin = new Thickness(3, 0, 0, 0);
        pair.Children.Add(kind);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (message.TaskId is { Length: > 0 } taskId) meta.Children.Add(TeamUi.Chip(taskId, radius: 5));
        TeamTaskInfo? task = FindTask(message.TaskId);
        if (message.Kind is "report" or "review" && task is not null && TeamConsoleModel.RunOf(task, Runs) is { } run
            && run.InputTokens + run.OutputTokens > 0)
        {
            meta.Children.Add(TeamUi.Text(ChatFormatting.Compact(run.InputTokens + run.OutputTokens) + " tok", "VelaTextSecondary", 11, mono: true));
        }
        meta.Children.Add(TeamUi.Text(message.At.ToLocalTime().ToString("HH:mm:ss"), "VelaTextTertiary", 11, mono: true));

        var top = new Grid { ColumnDefinitions = [with("*,Auto")], ColumnSpacing = 8 };
        top.Children.Add(pair);
        top.Children.Add(meta.At(1));
        TextBlock text = TeamUi.Line(ChatFormatting.OneLine(message.Text, 300), "VelaTextPrimary", 12);
        text.Margin = new Thickness(0, 4, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(text);

        Border row = Clickable(new Border { Classes = { "tcFeedRow" }, Child = stack, Tag = message },
            () => OpenView(new DrawerView("pair", message.From, message.To, message)));
        if (fresh) Flash(row);
        return row;
    }

    /// <summary>新到的一行闪一下强调色再淡出。</summary>
    private static void Flash(Border row)
    {
        Border glow = new Border { Opacity = 0.22, IsHitTestVisible = false }.Bind(Border.BackgroundProperty, "VelaAccent");
        glow.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(1.6) }];
        var layers = new Panel();
        Control content = row.Child!;
        row.Child = null;
        layers.Children.Add(glow);
        layers.Children.Add(content);
        row.Child = layers;
        // 进了可视树之后再改,过渡才会跑
        glow.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => glow.Opacity = 0);
    }
}
