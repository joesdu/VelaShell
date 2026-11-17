using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;

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

    // 引擎状态说明的几种固定句式(TeamEngine.Pump / OnEntryFinished 写的)
    [System.Text.RegularExpressions.GeneratedRegex(@"^waiting for (\S+) to finish #(\S+)$")]
    private static partial System.Text.RegularExpressions.Regex TeamNoteBusy();

    [System.Text.RegularExpressions.GeneratedRegex(@"^re-review after #(\S+) rework$")]
    private static partial System.Text.RegularExpressions.Regex TeamNoteReReview();

    [System.Text.RegularExpressions.GeneratedRegex(@"^#(\S+) (failed|cancelled|blocked)$")]
    private static partial System.Text.RegularExpressions.Regex TeamNoteBlocked();

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
        // ✎ = 可改文件,>_ = 可执行命令(两者都要主控显式开,执行时照旧审批)
        ToolTip.SetTip(avatar, $"{member.Name} · {member.Role}" + (member.AllowWrites ? " · ✎" : "") + (member.AllowCommands ? " · >_" : ""));
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
        private readonly StackPanel _messages = new() { Spacing = 3 };

        // 开合的是整个滚动区而不是里面的列表:只藏列表时 ScrollViewer 留着展开时的高度,收起后剩一大块空白
        private readonly ScrollViewer _messagesScroll = new()
        {
            MaxHeight = 220,
            IsVisible = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
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
                _messagesScroll.IsVisible = !_messagesScroll.IsVisible;
                _messagesChevron.RenderTransform = _messagesScroll.IsVisible ? new RotateTransform(90) : null;
                if (_messagesScroll.IsVisible) _messagesScroll.ScrollToEnd(); // 展开时停在最新一条
            };
            stack.Children.Add(messagesHeader);
            _messagesScroll.Content = _messages;
            stack.Children.Add(_messagesScroll);

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
            // 全队合计:token 与费用(每次运行各按自己的模型单价算,返工的那几次也算 —— 都是真花出去的)
            SubagentSnapshot[] teamRuns = [.. runs.Where(r => r.TeamTaskId is not null)];
            long teamTokens = teamRuns.Sum(r => r.InputTokens + r.OutputTokens);
            double? teamCost = null;
            foreach (SubagentSnapshot r in teamRuns)
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

            var byId = runs.ToDictionary(r => r.SubagentId, StringComparer.Ordinal);
            // 第二行引用别的任务(等待谁、审查谁)时写对方的标题而不是 id,过长截短
            var titles = team.Tasks.ToDictionary(t => t.Id, t => t.Title, StringComparer.OrdinalIgnoreCase);
            string Refer(string id) => titles.TryGetValue(id, out string? title)
                ? "「" + (title.Length <= 14 ? title : title[..14] + "…") + "」"
                : "#" + id;
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
                row.Update(task, member, run, Cancel is not null, Refer);
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

    /// <summary>任务卡头像的边长:标题与「角色 · 模型」两行的高度。</summary>
    private const double TaskAvatar = 28;

    /// <summary>
    /// 任务表的一张卡:左边负责人头像,右上角状态;正文四行 ——
    /// 标题 / 角色(身份色)+ 模型 · 思考强度 / 此刻在做什么 + 「上下文占用 · token ≈费用 · 耗时」/ 身份色进度条。
    /// 点开是该成员这次运行的卡片。
    /// </summary>
    private sealed class TeamTaskRow
    {
        private readonly ChatPanelView _owner;
        private readonly Decorator _status = new() { Margin = new Thickness(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        private readonly Decorator _assignee = new() { Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        // 角色用成员身份色,模型与思考强度用次要色:一眼分清「谁」和「用什么跑」
        private readonly TextBlock _role = new() { FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _model = new() { Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _metrics = new() { Classes = { "dim" }, TextAlignment = TextAlignment.Right, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, Margin = new Thickness(0, 3, 0, 0) };
        private readonly Border _expanded = new() { IsVisible = false, Margin = new Thickness(TaskAvatar + 8, 4, 0, 4) };
        private readonly SubagentRunCard _card;
        private string _statusKey = "";
        private string _assigneeKey = "";
        private string _barTone = "";
        private string _report = "";
        private bool _hasRun;

        public Border Root { get; }

        public TeamTaskRow(ChatPanelView owner, Action<string> cancel)
        {
            _owner = owner;
            _card = new SubagentRunCard(owner, cancel);
            _bar[!BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
            _detail[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");

            var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            identity.Children.Add(_role);
            identity.Children.Add(_model);

            var meta = new Grid { ColumnDefinitions = [with("*,Auto")] };
            meta.Children.Add(_detail);
            Grid.SetColumn(_metrics, 1);
            meta.Children.Add(_metrics);

            var body = new StackPanel { Spacing = 1 };
            body.Children.Add(_title);
            body.Children.Add(identity);
            body.Children.Add(meta);
            body.Children.Add(_bar);

            // 头像 | 正文 | 状态
            var line = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            line.Children.Add(_assignee);
            Grid.SetColumn(body, 1);
            line.Children.Add(body);
            Grid.SetColumn(_status, 2);
            line.Children.Add(_status);
            line.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _expanded.IsVisible = !_expanded.IsVisible;
            };
            var stack = new StackPanel();
            stack.Children.Add(line);
            stack.Children.Add(_expanded);
            Root = new Border { Classes = { "teamTask" }, Child = stack };
        }

        public void Update(TeamTaskInfo task, TeamMemberInfo? member, SubagentSnapshot? run, bool cancellable,
            Func<string, string> refer)
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
                _status.Child = MakeIcon(icon, tone, 13);
            }
            ToolTip.SetTip(_status, StatusLabel(task));
            // 只写任务本身:任务 id 常被模型起成角色名,与左边的头像重复;id 留在悬停提示里
            _title.Text = task.Title;
            ToolTip.SetTip(_title, $"#{task.Id} · {task.Title}");

            TeamMemberInfo who = member ?? new TeamMemberInfo(task.Assignee, "", null, false, 0);
            string assigneeKey = who.Name + who.Color;
            if (assigneeKey != _assigneeKey)
            {
                _assigneeKey = assigneeKey;
                _assignee.Child = MemberAvatar(who, TaskAvatar);
                _role[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(MemberColor(who.Color));
            }

            // 第二行:角色 + 实际跑的模型(运行过就以运行记录为准 —— 选定的模型不可用时会换替补)· 思考强度
            _role.Text = who.Role.Length > 0 ? who.Role : who.Name;
            ToolTip.SetTip(_role, who.Name + (who.AllowWrites ? " · ✎" : "") + (who.AllowCommands ? " · >_" : ""));
            string? modelId = run?.ModelId ?? who.ModelId;
            ResolvedModel? model = modelId is null ? null : _owner._settings.FindModel(modelId);
            string reasoning = model is null ? "" : _owner.ReasoningLabel(_owner.MemberReasoning(model));
            _model.Text = (model?.Name ?? modelId ?? "") + (reasoning.Length > 0 ? " · " + reasoning : "");
            ToolTip.SetTip(_model, model is null ? null : $"{model.ProviderName} / {model.Name} ({model.Model})");

            // 第三行左:此刻在做什么(运行中)> 等待 / 受阻 / 返工原因 > 审查与依赖关系;都没有就写状态
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
            else if (LocalizeNote(task.Note, refer) is { Length: > 0 } note)
            {
                parts.Add(note);
            }
            if (verifier)
            {
                parts.Add(_owner._loc.F("TeamReviews", refer(task.Verifies!))
                          + (task.Verdict is { } verdict ? " · " + _owner._loc[verdict == "PASS" ? "TeamVerdictPass" : "TeamVerdictFail"] : ""));
            }
            else if (task.DependsOn.Count > 0 && task.Status == "pending")
            {
                parts.Add(_owner._loc.F("TeamDependsOn", string.Join("、", task.DependsOn.Select(refer))));
            }
            if (task.Attempts > 1) parts.Add(_owner._loc.F("TeamRework", task.Attempts - 1));
            if (parts.Count == 0) parts.Add(StatusLabel(task));
            _detail.Text = string.Join(" · ", parts);
            ToolTip.SetTip(_detail, _detail.Text);

            // 第三行右:上下文占用 · token ≈费用 · 耗时
            var metrics = new List<string>();
            var tip = new System.Text.StringBuilder();
            int window = model?.MaxInputTokens ?? 0;
            if (run is { ContextTokens: > 0 } sampled && window > 0)
            {
                int percent = (int)Math.Min(100, Math.Round(sampled.ContextTokens * 100.0 / window));
                metrics.Add($"{_owner._loc["CtxShort"]} {percent}%");
                tip.AppendLine(_owner._loc.F("TeamContextTip", ChatFormatting.Compact(sampled.ContextTokens), ChatFormatting.Compact(window), percent));
            }
            if (run is { } used && used.InputTokens + used.OutputTokens > 0)
            {
                string tokens = ChatFormatting.Compact(used.InputTokens + used.OutputTokens);
                tip.AppendLine(_owner._loc.F("TeamTokens", task.Assignee, ChatFormatting.Compact(used.InputTokens), ChatFormatting.Compact(used.OutputTokens)));
                if (used.CachedInputTokens > 0)
                    tip.AppendLine(_owner._loc.F("TeamCachedTip", ChatFormatting.Compact(used.CachedInputTokens)));
                if (model is not null && EstimateCost(model, used.InputTokens, used.CachedInputTokens, used.OutputTokens) is { } cost)
                {
                    tokens += " " + FormatCost(cost);
                    tip.AppendLine(_owner._loc.F("TeamCostTip", cost.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), model.Name));
                }
                metrics.Add(tokens);
            }
            if (task.Elapsed > TimeSpan.Zero) metrics.Add(ChatFormatting.Duration(run?.Elapsed ?? task.Elapsed));
            _metrics.Text = string.Join(" · ", metrics);
            ToolTip.SetTip(_metrics, tip.Length > 0 ? tip.ToString().TrimEnd() : null);

            // 第四行:进度条。完成 = 满格(身份色;审查不过 = 警告色),失败 = 满格错误色,
            // 进行中没有可信的完成度(步数上限不是进度),走不定进度动画;等待 / 受阻 / 取消 = 空槽
            (double value, bool busy, string barTone) = task.Status switch
            {
                "completed" when verifier && task.Verdict == "FAIL" => (100d, false, "VelaWarning"),
                "completed" => (100d, false, MemberColor(who.Color)),
                "failed" => (100d, false, "VelaError"),
                "running" => (0d, true, MemberColor(who.Color)),
                _ => (0d, false, MemberColor(who.Color))
            };
            _bar.IsIndeterminate = busy;
            _bar.Value = value;
            if (barTone != _barTone)
            {
                _barTone = barTone;
                _bar[!ForegroundProperty] = new DynamicResourceExtension(barTone);
            }

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

        /// <summary>
        /// 引擎的状态说明是给模型看的英文固定句式(见 TeamEngine),这里换成本地化文案、任务 id 换成标题;
        /// 「等待依赖」「返工 n/m」与第二行其它部分重复,不再写;认不出的(多半是工具报的错误原文)照原样。
        /// </summary>
        private string LocalizeNote(string note, Func<string, string> refer)
        {
            if (note.Length == 0 || note.StartsWith("waiting for #", StringComparison.Ordinal)
                || note.StartsWith("rework ", StringComparison.Ordinal))
            {
                return "";
            }
            if (TeamNoteBusy().Match(note) is { Success: true } busy)
                return _owner._loc.F("TeamWaitMember", busy.Groups[1].Value, refer(busy.Groups[2].Value));
            if (TeamNoteReReview().Match(note) is { Success: true } again)
                return _owner._loc.F("TeamReReview", refer(again.Groups[1].Value));
            if (TeamNoteBlocked().Match(note) is { Success: true } blocked)
                return _owner._loc.F("TeamBlockedBy", refer(blocked.Groups[1].Value));
            return note == "team cancelled" ? _owner._loc["Cancelled"] : note;
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
