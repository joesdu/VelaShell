using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 团队卡:一张卡看清「谁在做什么、做到哪、彼此说了什么」。
/// </summary>
/// <remarks>
/// <para>
/// 版式参照 cc-haha 的团队工作台与 MiniMax Mavis 的任务视图,按 400–500px 的侧栏压缩:
/// 头部(目标 + 阶段芯片)→ 成员头像叠 + 进度 → 任务表(状态 · 标题 · 负责人 · 耗时,第二行写依赖 / 审查 / 此刻动作)
/// → 团队消息(按分派 / 汇报 / 协作 / 系统分色,默认收起)。点任务行展开该成员这次运行的实时卡片。
/// </para>
/// <para>
/// 颜色分工:状态用语义色(进行中 = 强调色、完成 = 已连接绿、受阻 / 返工 = 警告、失败 = 错误),
/// 成员身份用终端 ANSI 那组令牌(VelaShellCyan / Blue / Magenta / Yellow / Green),两者不混用。
/// </para>
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>成员身份色:按成员序号轮换。都是宿主转发过来的主题令牌,跟着明暗主题变。</summary>
    private static readonly string[] MemberColors =
        ["VelaShellCyan", "VelaShellBlue", "VelaShellMagenta", "VelaShellYellow", "VelaShellGreen"];

    private static string MemberColor(int index) => MemberColors[index % MemberColors.Length];

    /// <summary>任务行与消息里的小简写圈。字号令牌不得小于 11(FontTokens 可访问性门禁),16px 去掉边框后刚好放得下 11 号字。</summary>
    private const double SmallAvatar = 16;

    /// <summary>成员头像:身份色描边的小圆,里面是名字首字母。</summary>
    private static Border MemberAvatar(TeamMemberInfo member, double size = 18)
    {
        string initial = member.Name.Length > 0 ? char.ToUpperInvariant(member.Name[0]).ToString() : "?";
        var text = new TextBlock
        {
            Text = initial,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(MemberColor(member.Color));
        var avatar = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(1.5),
            Child = text
        };
        avatar[!Border.BorderBrushProperty] = new DynamicResourceExtension(MemberColor(member.Color));
        avatar[!Border.BackgroundProperty] = new DynamicResourceExtension(MemberColor(member.Color) + "Dim");
        ToolTip.SetTip(avatar, $"{member.Name} · {member.Role}" + (member.AllowWrites ? " · ✎" : ""));
        return avatar;
    }

    /// <summary>主控 / 全体 / 引擎的简写圈:中性色,不占成员的身份色(强调色在深色主题下与 VelaShellBlue 同色,不能用)。</summary>
    private static Border NeutralAvatar(string text, string tip, double size)
    {
        var label = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        var avatar = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(1.5),
            Child = label
        };
        avatar[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaTextTertiary");
        avatar[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgHover");
        ToolTip.SetTip(avatar, tip);
        return avatar;
    }

    private sealed class TeamBoardView
    {
        private readonly ChatPanelView _owner;
        private readonly TextBlock _goal = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        private readonly TextBlock _phaseText = new();
        private readonly Border _phaseChip;
        private readonly StackPanel _avatars = new() { Orientation = Orientation.Horizontal, Spacing = -4 };
        private readonly TextBlock _progress = new() { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _tasks = new() { Spacing = 2 };
        private readonly StackPanel _messages = new() { Spacing = 3, IsVisible = false };
        private readonly TextBlock _messagesTitle = new() { Classes = { "dim" } };
        private readonly Avalonia.Controls.Shapes.Path _messagesChevron;
        private readonly Dictionary<string, TeamTaskRow> _rows = [with(StringComparer.Ordinal)];
        private int _renderedMessages = -1;
        private string _membersKey = "";

        public Border Root { get; }

        /// <summary>最近一次的看板(入库用)。</summary>
        public TeamBoardSnapshot? Latest { get; private set; }

        /// <summary>取消一个成员的这次运行;历史回放为 null。</summary>
        public Action<string>? Cancel { get; set; }

        public TeamBoardView(ChatPanelView owner)
        {
            _owner = owner;
            var stack = new StackPanel { Spacing = 6 };

            var head = new Grid { ColumnDefinitions = [with("Auto,*,Auto")] };
            head.Children.Add(new Decorator
            {
                Child = MakeIcon("Icon.users", "VelaAccent", 13),
                Margin = new Thickness(0, 1, 6, 0),
                VerticalAlignment = VerticalAlignment.Top
            });
            Grid.SetColumn(_goal, 1);
            head.Children.Add(_goal);
            _phaseChip = new Border { Classes = { "phaseChip" }, Child = _phaseText, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(_phaseChip, 2);
            head.Children.Add(_phaseChip);
            stack.Children.Add(head);

            var roster = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            roster.Children.Add(_avatars);
            roster.Children.Add(_progress);
            stack.Children.Add(roster);

            stack.Children.Add(new Border { Classes = { "teamDivider" } });
            stack.Children.Add(_tasks);

            var messagesHeader = new Grid
            {
                ColumnDefinitions = [with("Auto,Auto,*")],
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(0, 2, 0, 0)
            };
            _messagesChevron = new Avalonia.Controls.Shapes.Path { Width = 24, Height = 24, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
            _messagesChevron[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("Icon.chevron-right");
            _messagesChevron[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextMuted");
            messagesHeader.Children.Add(new Viewbox { Width = 10, Height = 10, Child = _messagesChevron, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var messageIcon = new Decorator { Child = MakeIcon("AiIcon.messages-square", "VelaTextMuted", 11), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(messageIcon, 1);
            messagesHeader.Children.Add(messageIcon);
            Grid.SetColumn(_messagesTitle, 2);
            messagesHeader.Children.Add(_messagesTitle);
            messagesHeader.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _messages.IsVisible = !_messages.IsVisible;
                _messagesChevron.RenderTransform = _messages.IsVisible ? new RotateTransform(90) : null;
            };
            stack.Children.Add(messagesHeader);
            stack.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = _messages
            });

            Root = new Border { Classes = { "toolCard", "teamBoard" }, Child = stack };
        }

        public void Update(TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
        {
            Latest = team;
            _goal.Text = team.Goal;
            ToolTip.SetTip(_goal, team.Goal);
            (string phaseKey, bool warn) = team.Phase switch
            {
                "completed" => ("TeamPhaseCompleted", false),
                "failed" => ("TeamPhaseFailed", true),
                "cancelled" => ("Cancelled", true),
                _ => ("TeamPhaseRunning", false)
            };
            _phaseText.Text = _owner._loc[phaseKey];
            _phaseChip.Classes.Set("waiting", warn);

            string membersKey = string.Join("|", team.Members.Select(m => m.Name));
            if (membersKey != _membersKey)
            {
                _membersKey = membersKey;
                _avatars.Children.Clear();
                foreach (TeamMemberInfo member in team.Members) _avatars.Children.Add(MemberAvatar(member));
            }
            int done = team.Tasks.Count(t => t.Status == "completed");
            int running = team.Tasks.Count(t => t.Status == "running");
            _progress.Text = _owner._loc.F("TeamProgress", done, team.Tasks.Count, running)
                             + " · " + ChatFormatting.Duration(team.Elapsed);

            var byId = runs.ToDictionary(r => r.SubagentId, StringComparer.Ordinal);
            foreach (TeamTaskInfo task in team.Tasks)
            {
                if (!_rows.TryGetValue(task.Id, out TeamTaskRow? row))
                {
                    row = new TeamTaskRow(_owner, id => Cancel?.Invoke(id));
                    _rows[task.Id] = row;
                    _tasks.Children.Add(row.Root);
                }
                TeamMemberInfo? member = team.Members.FirstOrDefault(m => m.Name == task.Assignee);
                SubagentSnapshot? run = task.SubagentId is { } id && byId.TryGetValue(id, out SubagentSnapshot? found) ? found : null;
                row.Update(task, member, run, Cancel is not null);
            }

            _messagesTitle.Text = _owner._loc.F("TeamMessages", team.Messages.Count);
            if (_renderedMessages != team.Messages.Count)
            {
                _renderedMessages = team.Messages.Count;
                _messages.Children.Clear();
                foreach (TeamMessageInfo message in team.Messages) _messages.Children.Add(MessageRow(message, team));
            }
        }

        /// <summary>一条团队消息:分类标签 + 「谁 → 谁」+ 正文。</summary>
        private Grid MessageRow(TeamMessageInfo message, TeamBoardSnapshot team)
        {
            (string key, string tone) = message.Kind switch
            {
                "assign" => ("TeamMsgAssign", "VelaAccent"),
                "report" => ("TeamMsgReport", "VelaStatusConnected"),
                "peer" => ("TeamMsgPeer", "VelaShellMagenta"),
                _ => ("TeamMsgSystem", "VelaTextMuted")
            };
            var tag = new TextBlock { Text = _owner._loc[key], FontWeight = FontWeight.SemiBold };
            tag[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            tag[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(tone);
            var tagBox = new Border { Classes = { "teamTag" }, Child = tag, VerticalAlignment = VerticalAlignment.Top };
            tagBox[!Border.BorderBrushProperty] = new DynamicResourceExtension(tone);

            // 「谁 → 谁」用与任务表同一套简写圈:成员是身份色首字母,主控 / 全体 / 引擎是中性色的短字
            var route = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            route.Children.Add(Party(message.From));
            route.Children.Add(new TextBlock { Classes = { "dim" }, Text = "→", VerticalAlignment = VerticalAlignment.Center });
            route.Children.Add(Party(message.To));
            var body = new SelectableTextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap };
            body[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(route);
            text.Children.Add(body);

            var row = new Grid { ColumnDefinitions = [with("Auto,*")], ColumnSpacing = 6 };
            row.Children.Add(tagBox);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            ToolTip.SetTip(row, message.At.ToLocalTime().ToString("HH:mm:ss"));
            return row;

            Border Party(string name)
            {
                if (team.Members.FirstOrDefault(m => m.Name == name) is { } member) return MemberAvatar(member, SmallAvatar);
                (string shortKey, string fullKey) = name switch
                {
                    "lead" => ("TeamLeadShort", "TeamLead"),
                    "all" => ("TeamAllShort", "TeamAll"),
                    "engine" => ("TeamEngineShort", "TeamEngine"),
                    _ => ("", "")
                };
                return NeutralAvatar(shortKey.Length > 0 ? _owner._loc[shortKey] : name, fullKey.Length > 0 ? _owner._loc[fullKey] : name, SmallAvatar);
            }
        }
    }

    /// <summary>任务表的一行:状态 · #id 标题 · 负责人 · 耗时;第二行写依赖 / 审查 / 此刻动作;点开是该成员这次运行的卡片。</summary>
    private sealed class TeamTaskRow
    {
        private readonly ChatPanelView _owner;
        private readonly Decorator _status = new() { Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        // 负责人只放一枚带身份色的简写圈,固定在左侧一列:各行的圈与标题都上下对齐
        private readonly Decorator _assignee = new() { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        // 右侧两列定宽右对齐:这个成员这次运行收到的输入 token、耗时(时间贴最右)
        private readonly TextBlock _tokens = new() { Classes = { "dim" }, MinWidth = 48, TextAlignment = TextAlignment.Right, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _elapsed = new() { Classes = { "dim" }, MinWidth = 42, TextAlignment = TextAlignment.Right, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _detail = new() { Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private readonly Border _expanded = new() { IsVisible = false, Margin = new Thickness(18, 4, 0, 4) };
        private readonly SubagentRunCard _card;
        private string _statusKey = "";
        private string _assigneeKey = "";
        private string _report = "";
        private bool _hasRun;

        public Border Root { get; }

        public TeamTaskRow(ChatPanelView owner, Action<string> cancel)
        {
            _owner = owner;
            _card = new SubagentRunCard(owner, cancel);
            // 状态 | 负责人简写 | #id 标题 | 输入 token | 耗时
            var line = new Grid { ColumnDefinitions = [with("Auto,Auto,*,Auto,Auto")] };
            line.Children.Add(_status);
            Grid.SetColumn(_assignee, 1);
            line.Children.Add(_assignee);
            Grid.SetColumn(_title, 2);
            line.Children.Add(_title);
            Grid.SetColumn(_tokens, 3);
            line.Children.Add(_tokens);
            Grid.SetColumn(_elapsed, 4);
            line.Children.Add(_elapsed);
            _detail.Margin = new Thickness(11 + 6 + SmallAvatar + 6, 0, 0, 0); // 与标题左缘对齐(状态图标 + 简写圈)
            var header = new StackPanel { Spacing = 1, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            header.Children.Add(line);
            header.Children.Add(_detail);
            header.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _expanded.IsVisible = !_expanded.IsVisible;
            };
            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_expanded);
            Root = new Border { Classes = { "teamTask" }, Child = stack };
        }

        public void Update(TeamTaskInfo task, TeamMemberInfo? member, SubagentSnapshot? run, bool cancellable)
        {
            bool verifier = task.Verifies is not null;
            (string icon, string tone) = task.Status switch
            {
                "completed" when verifier && task.Verdict == "FAIL" => ("Icon.circle-alert", "VelaWarning"),
                "completed" => (verifier ? "AiIcon.shield-check" : "Icon.circle-check", "VelaStatusConnected"),
                "running" => (verifier ? "AiIcon.shield-alert" : "Icon.ellipsis", "VelaAccent"),
                "failed" => ("AiIcon.circle-x", "VelaError"),
                "blocked" or "cancelled" => ("AiIcon.minus", "VelaWarning"),
                _ => (verifier ? "AiIcon.shield-alert" : "AiIcon.square", "VelaTextMuted")
            };
            string statusKey = icon + tone;
            if (statusKey != _statusKey)
            {
                _statusKey = statusKey;
                _status.Child = MakeIcon(icon, tone, 11);
            }
            ToolTip.SetTip(_status, StatusLabel(task));
            _title.Text = $"#{task.Id} {task.Title}";
            ToolTip.SetTip(_title, task.Title);

            string assigneeKey = member is null ? task.Assignee : member.Name + member.Color;
            if (assigneeKey != _assigneeKey)
            {
                _assigneeKey = assigneeKey;
                _assignee.Child = MemberAvatar(member ?? new TeamMemberInfo(task.Assignee, "", null, false, 0), SmallAvatar);
            }
            _elapsed.Text = task.Elapsed > TimeSpan.Zero ? ChatFormatting.Duration(run?.Elapsed ?? task.Elapsed) : "";
            _tokens.Text = run is { InputTokens: > 0 } used ? $"{ChatFormatting.Compact(used.InputTokens)} tok" : "";
            ToolTip.SetTip(_tokens, run is { } r && r.InputTokens + r.OutputTokens > 0
                ? _owner._loc.F("TeamTokens", task.Assignee, ChatFormatting.Compact(r.InputTokens), ChatFormatting.Compact(r.OutputTokens))
                : null);

            // 第二行:此刻在做什么(运行中)> 等待 / 受阻 / 返工原因 > 审查与依赖关系
            var parts = new List<string>();
            if (task.Status == "running" && run is { Activity.Length: > 0 } live)
            {
                parts.Add(live.Activity switch
                {
                    SubagentOrchestrator.WaitingForApproval => _owner._loc["SubagentWaitingApproval"],
                    SubagentOrchestrator.WaitingForFileLock => _owner._loc["SubagentWaitingLock"],
                    var activity => activity
                });
            }
            else if (task.Note.Length > 0)
            {
                parts.Add(task.Note);
            }
            if (verifier)
            {
                parts.Add(_owner._loc.F("TeamReviews", task.Verifies!)
                          + (task.Verdict is { } verdict ? " · " + _owner._loc[verdict == "PASS" ? "TeamVerdictPass" : "TeamVerdictFail"] : ""));
            }
            else if (task.DependsOn.Count > 0 && task.Status == "pending")
            {
                parts.Add(_owner._loc.F("TeamDependsOn", string.Join(", ", task.DependsOn.Select(d => "#" + d))));
            }
            if (task.Attempts > 1) parts.Add(_owner._loc.F("TeamRework", task.Attempts - 1));
            _detail.Text = string.Join(" · ", parts);
            _detail.IsVisible = _detail.Text.Length > 0;
            ToolTip.SetTip(_detail, _detail.Text);

            // 展开区:有运行记录就放实时卡片;只剩报告(旧数据)就放报告
            if (run is not null)
            {
                if (!_hasRun)
                {
                    _hasRun = true;
                    _expanded.Child = _card.Root;
                }
                _card.Update(run, cancellable);
            }
            else if (task.Report.Length > 0 && task.Report != _report)
            {
                _report = task.Report;
                var segment = new MarkdownSegment(_owner);
                segment.Append(task.Report);
                _expanded.Child = segment.Host;
            }
        }

        private string StatusLabel(TeamTaskInfo task) => task.Status switch
        {
            "completed" => _owner._loc["SubagentCompleted"],
            "running" => _owner._loc["SubagentRunning"],
            "failed" => _owner._loc["SubagentFailed"],
            "blocked" => _owner._loc["TeamBlocked"],
            "cancelled" => _owner._loc["Cancelled"],
            _ => _owner._loc["SubagentQueued"]
        };
    }

    internal sealed partial class AssistantBubble
    {
        private TeamBoardView? _teamView;

        /// <summary>这一轮的团队看板从哪取(编排器还在时有值);收尾后为 null,卡片停在最后一版。</summary>
        public Func<TeamBoardSnapshot?>? TeamSource { get; set; }

        /// <summary>这一轮组过团队。</summary>
        public bool HasTeam => _teamView is not null;

        /// <summary>团队看板的入库快照;没组过团队时为 null。</summary>
        public TeamBoardSnapshot? TeamRun => _teamView?.Latest;

        /// <summary>刷新团队卡;第一次有团队时插进回复(与子任务组同一个位置规则)。</summary>
        public void UpdateTeam(TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
        {
            if (_teamView is null)
            {
                _teamView = new TeamBoardView(_owner) { Cancel = CancelSubagent };
                int index = _currentSegment is null
                    ? _stack.Children.Count
                    : _stack.Children.IndexOf(_currentSegment.Host);
                _stack.Children.Insert(index, _teamView.Root);
            }
            _teamView.Cancel = CancelSubagent;
            _teamView.Update(team, runs);
        }
    }
}
