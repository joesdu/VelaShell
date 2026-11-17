using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 团队模式在聊天面板里的那一小块:一张入口卡,以及把团队控制台开成主窗口标签页的那一步。
/// </summary>
/// <remarks>
/// <para>
/// 团队的全貌(拓扑、成员、任务时间轴、用量、通信记录)不在会话里铺开 —— 侧栏只有四五百像素宽,摆不下,
/// 也会把正文挤到很远。会话里只留一张两行的入口卡:目标 + 阶段,成员头像叠 + 进度;整张卡可点,
/// 点开是停靠在主窗口标签区的 <see cref="TeamConsoleView" />(与差异文档同一套开法:已开着就切过去)。
/// </para>
/// <para>
/// 颜色分工:状态用语义色,成员身份用终端 ANSI 那组令牌(VelaShellCyan / Blue / Magenta / Yellow / Green),两者不混用;
/// 入口卡上的头像与控制台里的成员是同一套身份色。
/// </para>
/// </remarks>
public partial class ChatPanelView : ITeamConsoleHost
{
    /// <summary>成员身份色:按成员序号轮换。都是宿主转发过来的主题令牌,跟着明暗主题变。</summary>
    private static readonly string[] MemberColors =
        ["VelaShellCyan", "VelaShellBlue", "VelaShellMagenta", "VelaShellYellow", "VelaShellGreen"];

    /// <summary>第 <paramref name="index" /> 名成员的身份色令牌(入口卡与团队控制台共用)。</summary>
    internal static string MemberColor(int index) => MemberColors[index % MemberColors.Length];

    /// <summary>团队控制台标签上的图标(lucide users,与入口卡、工具卡片上的团队图标是同一个)。</summary>
    private static readonly PluginIcon TeamTabIcon = PluginIcon.Stroked(
        "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M13 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75");

    /// <summary>标签标题里目标最多留多少个字:标签页很窄,写全了反而看不出是哪个团队。</summary>
    internal const int TeamTabGoalChars = 24;

    /// <summary>已经开着的团队控制台,键是团队 id。</summary>
    private readonly Dictionary<string, (IPluginPanel Panel, TeamConsoleView View)> _teamConsoles = [with(StringComparer.Ordinal)];

    /// <summary>
    /// 成员实际用的思考强度:与主控同一个模型时沿用这一轮在输入框选的强度(子任务就是这么跑的,
    /// 见 ResolveSubagentModelAsync),其它模型按接入里配的。不可调的模型没有强度可言。
    /// </summary>
    private ReasoningLevel MemberReasoning(ResolvedModel model)
    {
        if (!model.ReasoningAdjustable) return ReasoningLevel.Default;
        return model.Id == ActiveProvider?.Id && ActiveProviderForRequest is { } turn ? turn.Reasoning : model.Reasoning;
    }

    /// <summary>思考强度的短标签;「自动」(不指定)不写,免得每张卡都挂一个没信息量的词。</summary>
    private string ReasoningLabel(ReasoningLevel level) => level switch
    {
        ReasoningLevel.Off => _loc["ReasoningOff"],
        ReasoningLevel.Low => _loc["ReasoningLow"],
        ReasoningLevel.Medium => _loc["ReasoningMedium"],
        ReasoningLevel.High => _loc["ReasoningHigh"],
        ReasoningLevel.ExtraHigh => _loc["ReasoningExtraHigh"],
        ReasoningLevel.Max => _loc["ReasoningMax"],
        _ => ""
    };

    // ---------- 团队控制台向面板要的东西 ----------

    Loc ITeamConsoleHost.Loc => _loc;

    ResolvedModel? ITeamConsoleHost.FindModel(string? modelId) => _settings.FindModel(modelId);

    string ITeamConsoleHost.ReasoningLabel(ResolvedModel model) => ReasoningLabel(MemberReasoning(model));

    void ITeamConsoleHost.OpenLink(LinkClickedEventArgs e) => OnMarkdownLinkClicked(e);

    // ---------- 把控制台开成标签页 ----------

    /// <summary>标签标题:「团队 · 目标」,目标压成一行并截短。</summary>
    internal string TeamTabTitle(TeamBoardSnapshot team)
        => _loc.F("TeamTabTitle", ChatFormatting.OneLine(team.Goal.Trim(), TeamTabGoalChars));

    /// <summary>已经开着的团队控制台(测试用)。</summary>
    internal TeamConsoleView? OpenTeamConsoleView(string teamId)
        => _teamConsoles.TryGetValue(teamId, out (IPluginPanel Panel, TeamConsoleView View) open) && open.Panel.IsOpen ? open.View : null;

    /// <summary>把一个团队的控制台开成主窗口标签区里的标签页(已开着就切过去)。</summary>
    internal void OpenTeamConsole(TeamConsoleSession session) => _ = OpenTeamConsoleAsync(session);

    /// <inheritdoc cref="OpenTeamConsole" />
    internal async Task OpenTeamConsoleAsync(TeamConsoleSession session)
    {
        string key = session.Team.TeamId;
        if (_teamConsoles.TryGetValue(key, out (IPluginPanel Panel, TeamConsoleView View) existing))
        {
            if (existing.Panel.IsOpen)
            {
                try
                {
                    await existing.Panel.ActivateAsync();
                }
                catch (Exception ex)
                {
                    _context.Log.Warn($"Activating the team console tab failed: {ex.Message}");
                }
                return;
            }
            _teamConsoles.Remove(key);
        }
        string title = TeamTabTitle(session.Team);
        var view = new TeamConsoleView(session, this);
        var options = new PanelOptions
        {
            Title = title,
            Icon = TeamTabIcon,
            DisplayMode = PanelDisplayMode.Document,
            // 并入主窗口的标签组(终端、差异文档所在的那一片),而不是 AI 面板自己那一栏
            Placement = PanelPlacement.Tabs
        };
        try
        {
            IPluginPanel panel = ShowDocumentOverride is { } show
                ? await show(options, () => view)
                : await _context.Ui.ShowPanelAsync(options, () => view);
            _teamConsoles[key] = (panel, view);
            // Closed 可能在线程池上回调,清账回 UI 线程
            panel.Closed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_teamConsoles.TryGetValue(key, out (IPluginPanel Panel, TeamConsoleView View) open) && ReferenceEquals(open.Panel, panel))
                {
                    _teamConsoles.Remove(key);
                }
            });
        }
        catch (Exception ex)
        {
            // 老宿主不认文档标签、或者开的时候出了错:退回独立窗口(与差异文档同一个兜底),别让一次点击把面板带崩
            _context.Log.Warn($"Opening the team console tab failed, falling back to a window: {ex.Message}");
            IPluginPanel? fallback = null;
            (double width, double height) = FallbackWindowSize();
            await OpenAsync(title, width, height, [], () => new TeamConsoleView(session, this),
                panel =>
                {
                    fallback = panel;
                    _diffFallbackWindows.Add(panel);
                },
                () =>
                {
                    if (fallback is not null) _diffFallbackWindows.Remove(fallback);
                });
        }
    }

    // ---------- 入口卡 ----------

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
        // ✎ = 可改文件,>_ = 可执行命令(两者都要主控显式开,执行时照旧审批)
        ToolTip.SetTip(avatar, $"{member.Name} · {member.Role}" + (member.AllowWrites ? " · ✎" : "") + (member.AllowCommands ? " · >_" : ""));
        return avatar;
    }

    /// <summary>
    /// 会话里的团队入口:第一行 = 团队图标 + 目标 + 阶段芯片 + 「在标签页中打开」的箭头,
    /// 第二行 = 成员头像叠 + 「完成数 / 总数 · 进行中 · 已运行 · token ≈费用」。整张卡可点(也能用键盘:Tab 到它再按 Enter / 空格)。
    /// </summary>
    private sealed class TeamEntryCard
    {
        private readonly ChatPanelView _owner;
        private readonly TextBlock _goal = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private readonly TextBlock _phaseText = new();
        private readonly Border _phaseChip;
        private readonly StackPanel _avatars = new() { Orientation = Orientation.Horizontal, Spacing = -4 };
        private readonly TextBlock _progress = new() { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private string _membersKey = "";

        public Border Root { get; }

        /// <summary>这张卡指向的团队(换了一个团队就换一份)。</summary>
        public TeamConsoleSession Session { get; private set; }

        /// <summary>最近一次的看板(入库用)。</summary>
        public TeamBoardSnapshot Latest => Session.Team;

        /// <summary>取消一个成员的这次运行;历史回放与这一轮收尾之后为 null。</summary>
        public Action<string>? Cancel
        {
            get => Session.Cancel;
            set => Session.Cancel = value;
        }

        public TeamEntryCard(ChatPanelView owner, TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
        {
            _owner = owner;
            Session = new TeamConsoleSession(team, runs);

            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            head.Children.Add(new Decorator
            {
                Child = MakeIcon("AiIcon.users", "VelaAccent", 13),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            _goal.VerticalAlignment = VerticalAlignment.Center;
            // 与工具卡标题同一档字号:入口是一张小卡,不该比正文还抢眼
            _goal[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
            Grid.SetColumn(_goal, 1);
            head.Children.Add(_goal);
            _phaseChip = new Border { Classes = { "phaseChip" }, Child = _phaseText, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(_phaseChip, 2);
            head.Children.Add(_phaseChip);
            // 行尾的箭头:说明「这张卡会把东西开到别处去」,悬停整张卡时跟着提亮(见 Border.teamEntry 的样式)
            var open = new Decorator
            {
                Classes = { "teamEntryOpen" },
                Child = MakeIcon("AiIcon.arrow-up-right", "VelaTextTertiary", 12),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(open, 3);
            head.Children.Add(open);

            var roster = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            roster.Children.Add(_avatars);
            Grid.SetColumn(_progress, 1);
            roster.Children.Add(_progress);

            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(head);
            stack.Children.Add(roster);
            Root = new Border
            {
                Name = "TeamEntry",
                Classes = { "toolCard", "teamEntry" },
                Child = stack,
                Focusable = true,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            string tip = owner._loc["TeamEntryTip"];
            ToolTip.SetTip(Root, tip);
            AutomationProperties.SetName(Root, tip);
            Root.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                owner.OpenTeamConsole(Session);
            };
            Root.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space)) return;
                e.Handled = true;
                owner.OpenTeamConsole(Session);
            };
            Render();
        }

        public void Update(TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
        {
            if (team.TeamId != Session.Team.TeamId)
            {
                // 同一轮里主控又组了一个新团队:入口改指新的那个;旧团队的控制台若还开着,停在它最后一版
                Session = new TeamConsoleSession(team, runs) { Cancel = Session.Cancel };
            }
            else
            {
                Session.Update(team, runs);
            }
            Render();
        }

        private void Render()
        {
            TeamBoardSnapshot team = Session.Team;
            _goal.Text = ChatFormatting.OneLine(team.Goal, 200);
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
            // 全队合计:token 与费用(每次运行各按自己的模型单价算,返工的那几次也算 —— 都是真花出去的)
            long teamTokens = Session.Runs.Sum(r => r.InputTokens + r.OutputTokens);
            double? teamCost = null;
            foreach (SubagentSnapshot r in Session.Runs)
            {
                if (r.ModelId is { } id && _owner._settings.FindModel(id) is { } model
                    && EstimateCost(model, r.InputTokens, r.CachedInputTokens, r.OutputTokens) is { } cost)
                {
                    teamCost = (teamCost ?? 0) + cost;
                }
            }
            _progress.Text = _owner._loc.F("TeamProgress", done, team.Tasks.Count, running)
                             + " · " + ChatFormatting.Duration(team.Elapsed)
                             + (teamTokens > 0 ? $" · {ChatFormatting.Compact(teamTokens)} tok" : "")
                             + (teamCost is { } total ? " " + FormatCost(total) : "");
            ToolTip.SetTip(_progress, teamCost is { } sum
                ? _owner._loc.F("TeamTotalCostTip", sum.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                : null);
        }
    }

    internal sealed partial class AssistantBubble
    {
        private TeamEntryCard? _teamView;

        /// <summary>这一轮的团队看板从哪取(编排器还在时有值);收尾后为 null,入口与控制台停在最后一版。</summary>
        public Func<TeamBoardSnapshot?>? TeamSource { get; set; }

        /// <summary>这一轮组过团队。</summary>
        public bool HasTeam => _teamView is not null;

        /// <summary>团队看板的入库快照;没组过团队时为 null。</summary>
        public TeamBoardSnapshot? TeamRun => _teamView?.Latest;

        /// <summary>这条回复里团队入口背后的那份状态(测试用);没组过团队时为 null。</summary>
        internal TeamConsoleSession? TeamSession => _teamView?.Session;

        /// <summary>
        /// 刷新团队入口(以及开着的团队控制台);第一次有团队时把入口卡插进回复(与子任务组同一个位置规则)。
        /// </summary>
        public void UpdateTeam(TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
        {
            if (_teamView is null)
            {
                _teamView = new TeamEntryCard(_owner, team, runs) { Cancel = CancelSubagent };
                int index = _currentSegment is null
                    ? _stack.Children.Count
                    : _stack.Children.IndexOf(_currentSegment.Host);
                _stack.Children.Insert(index, _teamView.Root);
                return;
            }
            _teamView.Cancel = CancelSubagent;
            _teamView.Update(team, runs);
        }
    }
}
