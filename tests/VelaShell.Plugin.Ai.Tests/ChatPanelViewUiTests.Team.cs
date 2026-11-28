using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 团队模式的界面:会话里只留一张入口卡,点开是主窗口标签区里的团队控制台(<see cref="TeamConsoleView" />)。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    /// <summary>
    /// 三人团队:system 调研(指定了带单价的模型)→ network 修改(沿用主控的模型)→ checker 审查 network 的结果。
    /// 用量:t1 18 200 输入(其中 10 000 命中缓存)+ 900 输出,按 5 / 0.5 / 25 的单价 ≈ 0.07;t2 9 400 + 700,没有单价。
    /// </summary>
    private static (TeamBoardSnapshot Team, SubagentSnapshot[] Runs) SampleTeam(string modelId, string phase = "completed")
    {
        bool running = phase == "running";
        DateTimeOffset at = DateTimeOffset.Now.AddMinutes(-2);
        TeamMemberInfo[] members =
        [
            new("system", "researcher", modelId, false, 0),
            new("network", "engineer", null, true, 1, AllowCommands: true),
            new("checker", "reviewer", null, false, 2)
        ];
        TeamTaskInfo[] tasks =
        [
            new("t1", "审查 SSH 与权限", "system", "completed", [], null, 1, null, "sub-a", "## 结论\n- `PermitRootLogin` 已关闭\n\n```bash\nsshd -T\n```", "",
                TimeSpan.FromSeconds(34), TimeSpan.Zero),
            new("t2", "修正 FRP 与防火墙", "network", running ? "running" : "completed", ["t1"], null, 1, null, "sub-b", running ? "" : "ok", "",
                TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(35)),
            new("t3", "复核防火墙改动", "checker", running ? "pending" : "completed", ["t2"], "t2", running ? 0 : 1, running ? null : "PASS",
                running ? null : "sub-c", running ? "" : "VERDICT: PASS", running ? "waiting for #t2" : "",
                running ? TimeSpan.Zero : TimeSpan.FromSeconds(5), running ? null : TimeSpan.FromSeconds(75))
        ];
        List<TeamMessageInfo> messages =
        [
            new(at, "lead", "all", "system", "Team formed: 3 members, 3 tasks."),
            new(at.AddSeconds(1), "lead", "system", "assign", "#t1 审查 SSH 与权限", "t1"),
            new(at.AddSeconds(35), "system", "lead", "report", "#t1 done: sshd is fine", "t1"),
            new(at.AddSeconds(35), "lead", "network", "assign", "#t2 修正 FRP 与防火墙", "t2"),
            new(at.AddSeconds(35), "system", "network", "handoff", "#t1 审查 SSH 与权限 → #t2", "t1")
        ];
        if (!running)
        {
            messages.Add(new(at.AddSeconds(75), "network", "lead", "report", "#t2 done", "t2"));
            messages.Add(new(at.AddSeconds(80), "checker", "network", "review", "#t2 passed review.", "t3"));
        }
        var team = new TeamBoardSnapshot("team-1", "安全审计", phase, members, tasks, messages, TimeSpan.FromSeconds(80), modelId);
        SubagentSnapshot[] runs =
        [
            new("sub-a", "system · researcher", "completed", "", "## 结论\n- `PermitRootLogin` 已关闭\n\n```bash\nsshd -T\n```", [new("read_remote_file", 3)], [],
                TimeSpan.FromSeconds(34), ModelId: modelId, InputTokens: 18_200, OutputTokens: 900, TeamTaskId: "t1",
                CachedInputTokens: 10_000, ContextTokens: 8_000),
            new("sub-b", "network · engineer", running ? "running" : "completed", "", running ? "正在改 frpc.toml" : "ok", [], [],
                TimeSpan.FromSeconds(40), Activity: running ? "patch_remote_file /etc/frp/frpc.toml" : "",
                InputTokens: 9_400, OutputTokens: 700, AllowWrites: true, TeamTaskId: "t2", AllowCommands: true),
            // 普通委派(不属于团队)不进团队的账
            new("sub-x", "analysis", "completed", "", "side quest", [], [], TimeSpan.FromSeconds(3), InputTokens: 50_000, OutputTokens: 50_000)
        ];
        return (team, runs);
    }

    /// <summary>配一个带单价的模型(团队入口与控制台按它估费用),返回它的配置 id。</summary>
    private static async Task<string> SavePricedModelAsync(TestPluginContext context)
    {
        File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
        AiProvider provider = StubProvider("stub", "http://127.0.0.1:9", "opus", maxInputTokens: 200_000);
        AiModelConfig opus = provider.Models[0];
        opus.Name = "Opus 5.5";
        opus.SupportsReasoning = true;
        opus.Reasoning = ReasoningLevel.Low;
        opus.InputPricePerMillion = 5;
        opus.CachedInputPricePerMillion = 0.5;
        opus.OutputPricePerMillion = 25;
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        { Providers = [provider], ActiveModelId = opus.Id, SuggestFollowUps = false });
        return opus.Id;
    }

    private static ChatPanelView.AssistantBubble AddBubble(ChatPanelView panel)
    {
        var bubble = new ChatPanelView.AssistantBubble(panel);
        Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
        return bubble;
    }

    private static Border TeamEntry(ChatPanelView.AssistantBubble bubble)
        => bubble.Root.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TeamEntry");

    private static T Named<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static string[] TextsOf(Control root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

    /// <summary>把团队控制台挂进一个够宽的窗口(标签页在真实宿主里就是这么大一块)。</summary>
    private static async Task<Window> HostConsoleAsync(TeamConsoleView view, double width = 1400)
    {
        var window = new Window { Width = width, Height = 900, Content = view };
        window.Show();
        await PumpAsync(5);
        return window;
    }

    /// <summary>
    /// 会话里的团队只剩一张两行的入口卡:目标 + 阶段,成员头像叠 + 进度(完成数、耗时、全队 token 与费用)。
    /// 任务表、进度条、团队消息都不在会话里了;卡片出现本身不开任何标签页。
    /// </summary>
    [TestMethod]
    public void TeamEntry_IsACompactCardInTheConversation()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            string modelId = await SavePricedModelAsync(context);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                window.Width = 460;
                ChatPanelView.AssistantBubble bubble = AddBubble(panel);
                int panelsBefore = context.FakeUi.Panels.Count;
                (TeamBoardSnapshot team, SubagentSnapshot[] runs) = SampleTeam(modelId);
                bubble.UpdateTeam(team, runs);
                await PumpAsync();

                Border entry = TeamEntry(bubble);
                Assert.IsTrue(entry.Classes.Contains("toolCard"), "入口沿用工具卡的那套卡片样式");
                string[] texts = TextsOf(entry);
                Assert.Contains("安全审计", texts);
                Assert.Contains("Done", texts);
                Assert.Contains(t => t.StartsWith("3/3 tasks", StringComparison.Ordinal) && t.EndsWith("29k tok ≈0.07", StringComparison.Ordinal), texts,
                    "进度行写完成数、全队 token 与合计费用(普通委派的用量不算进来)");
                Assert.HasCount(3, entry.GetVisualDescendants().OfType<Border>().Where(b => b.CornerRadius.TopLeft == 9 && b.Width == 18).ToArray(),
                    "三名成员三个头像");
                Assert.AreEqual("Open the team console in a tab", ToolTip.GetTip(entry));
                Assert.IsTrue(entry.Focusable, "键盘也够得着");

                Assert.IsEmpty(bubble.Root.GetVisualDescendants().OfType<ProgressBar>(), "任务表不在会话里了");
                Assert.IsEmpty(bubble.Root.GetVisualDescendants().OfType<Expander>());
                Assert.DoesNotContain(t => t.Contains("审查 SSH", StringComparison.Ordinal), TextsOf(bubble.Root), "任务标题只在控制台里");
                Assert.IsLessThan(80d, entry.Bounds.Height, "入口只有两行");
                Assert.HasCount(panelsBefore, context.FakeUi.Panels, "不点不开");

                Assert.IsTrue(bubble.HasTeam);
                Assert.AreSame(team, bubble.TeamRun, "入库的还是完整的看板");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 点入口:团队控制台开成主窗口标签组里的一个文档标签,标题是「团队 · 目标」(过长截短)、带团队图标;
    /// 已经开着就切过去,不再开第二个;Enter 也能开;标签关了再点就重开。
    /// </summary>
    [TestMethod]
    public void TeamEntry_OpensTheConsoleAsOneDocumentTab()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            string modelId = await SavePricedModelAsync(context);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ChatPanelView.AssistantBubble bubble = AddBubble(panel);
                (TeamBoardSnapshot team, SubagentSnapshot[] runs) = SampleTeam(modelId);
                bubble.UpdateTeam(team, runs);
                await PumpAsync();
                int before = context.FakeUi.Panels.Count;
                Border entry = TeamEntry(bubble);

                Press(entry);
                await PumpAsync(5);
                Assert.HasCount(before + 1, context.FakeUi.Panels);
                FakePanel tab = context.FakeUi.LastPanel;
                Assert.AreEqual(PanelDisplayMode.Document, tab.Options.DisplayMode);
                Assert.AreEqual(PanelPlacement.Tabs, tab.Options.Placement, "并进主窗口的标签组,不是 AI 面板那一栏");
                Assert.AreEqual("Team · 安全审计", tab.Options.Title);
                Assert.IsNotNull(tab.Options.Icon);
                var view = (TeamConsoleView)tab.CreateContent();
                Assert.AreEqual("team-1", view.TeamId);
                Assert.AreSame(view, panel.OpenTeamConsoleView("team-1"));

                Press(entry);
                await PumpAsync(5);
                Assert.HasCount(before + 1, context.FakeUi.Panels, "已经开着:切过去,不再开第二个");
                Assert.AreEqual(1, tab.ActivateCount);

                entry.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = entry });
                await PumpAsync(5);
                Assert.AreEqual(2, tab.ActivateCount, "Enter 与点击等价");

                await tab.CloseAsync();
                await PumpAsync(3);
                Assert.IsNull(panel.OpenTeamConsoleView("team-1"));
                Press(entry);
                await PumpAsync(5);
                Assert.HasCount(before + 2, context.FakeUi.Panels, "标签关了:再点重开一个");

                string longTitle = panel.TeamTabTitle(team with { Goal = "把线上三台网关的 nginx 配置逐台核对\n并修正 upstream 端口后平滑重载" });
                Assert.AreEqual("Team · " + "把线上三台网关的 nginx 配置逐台核对 并修正 upstream 端口后平滑重载"[..ChatPanelView.TeamTabGoalChars] + "…", longTitle,
                    "目标压成一行、截到标签放得下的长度");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>老宿主不认文档标签、或者开的时候出了错:退回独立窗口,不把面板带崩。</summary>
    [TestMethod]
    public void TeamEntry_FallsBackToAWindowWhenTheTabCannotOpen()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            string modelId = await SavePricedModelAsync(context);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ChatPanelView.AssistantBubble bubble = AddBubble(panel);
                (TeamBoardSnapshot team, SubagentSnapshot[] runs) = SampleTeam(modelId);
                bubble.UpdateTeam(team, runs);
                await PumpAsync();
                panel.ShowDocumentOverride = (_, _) => throw new NotSupportedException("no document tabs");

                Press(TeamEntry(bubble));
                await PumpAsync(5);

                FakePanel fallback = context.FakeUi.LastPanel;
                Assert.AreEqual(PanelDisplayMode.Window, fallback.Options.DisplayMode);
                Assert.AreEqual("Team · 安全审计", fallback.Options.Title);
                Assert.IsInstanceOfType<TeamConsoleView>(fallback.CreateContent());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 控制台一页:六块统计、拓扑(主控 + 成员,指派 / 交付 / 审查三种关系)、成员卡、任务时间轴、Token 消耗、通信记录。
    /// 宽屏并排两栏,窄了上下叠放;通信记录能按成员、类型、关键字筛。
    /// </summary>
    [TestMethod]
    public void TeamConsole_ShowsTheWholeTeamOnOnePage()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            string modelId = await SavePricedModelAsync(context);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                ChatPanelView.AssistantBubble bubble = AddBubble(panel);
                (TeamBoardSnapshot team, SubagentSnapshot[] runs) = SampleTeam(modelId);
                bubble.UpdateTeam(team, runs);
                await panel.OpenTeamConsoleAsync(bubble.TeamSession!);
                TeamConsoleView view = panel.OpenTeamConsoleView("team-1")!;
                host = await HostConsoleAsync(view);

                // 头部:阶段与目标
                string[] header = TextsOf(Named<Border>(view, "TeamConsoleHeader"));
                Assert.Contains("Agent Team", header);
                Assert.Contains("Done", header);
                Assert.Contains(t => t.StartsWith("Goal: 安全审计", StringComparison.Ordinal), header);

                // 统计
                Assert.Contains("100%", TextsOf(Named<Border>(view, "TeamStatProgress")));
                string[] tasks = TextsOf(Named<Border>(view, "TeamStatTasks"));
                Assert.Contains("3", tasks);
                Assert.Contains(" / 3", tasks);
                Assert.Contains(" / 3", TextsOf(Named<Border>(view, "TeamStatActive")), "活跃成员写「几个 / 共几个」");
                Assert.Contains("29k", TextsOf(Named<Border>(view, "TeamStatTokens")), "普通委派的用量不算进团队");
                Assert.Contains("≈0.07", TextsOf(Named<Border>(view, "TeamStatCost")));
                Assert.Contains(team.Messages.Count.ToString(), TextsOf(Named<Border>(view, "TeamStatMessages")));

                // 拓扑:主控 + 三名成员;主控指派每个人,调研交付给修改,审查指向被审查的人
                Assert.AreSequenceEqual(["lead", "system", "network", "checker"], [.. view.GraphNodes], SequenceOrder.InAnyOrder);
                Assert.AreEqual(3, view.GraphEdges.Count(e => e.Kind == "assign"));
                Assert.AreEqual(("system", "network"), view.GraphEdges.Where(e => e.Kind == "handoff").Select(e => (e.From, e.To)).Single());
                Assert.AreEqual(("checker", "network"), view.GraphEdges.Where(e => e.Kind == "review").Select(e => (e.From, e.To)).Single());
                Assert.AreEqual(0, view.ActiveFlights, "打开时不把历史消息重放成光点");

                // 成员卡:名字、角色、状态、实际模型、用量
                Border roster = Named<Border>(view, "TeamRosterPanel");
                Border[] cards = [.. roster.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tcCard"))];
                Assert.HasCount(3, cards);
                string[] first = TextsOf(cards[0]);
                Assert.Contains("system", first);
                Assert.Contains("researcher", first);
                Assert.Contains("Idle", first, "跑完的成员是空闲");
                Assert.Contains("Opus 5.5", first);
                Assert.Contains("19k", first);

                // 任务时间轴:一行一个任务,标题、状态,做完的条写 100%
                Border gantt = Named<Border>(view, "TeamGanttPanel");
                Border[] rows = [.. gantt.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tcRow"))];
                Assert.HasCount(3, rows);
                Assert.Contains("审查 SSH 与权限", TextsOf(rows[0]));
                Assert.Contains("Completed", TextsOf(rows[0]));
                Assert.Contains("100%", TextsOf(rows[0]));
                Assert.Contains("60s", TextsOf(gantt), "时间轴的刻度");

                // Token 消耗:按成员排,用得多的在前
                Border tokens = Named<Border>(view, "TeamTokensPanel");
                Border[] tokenRows = [.. tokens.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tcRow"))];
                Assert.AreEqual("system", tokenRows[0].Tag);
                Assert.Contains(t => t.StartsWith("Opus 5.5 ×", StringComparison.Ordinal), TextsOf(tokens), "按模型分布");
                Assert.Contains("Only recorded while the team is running", TextsOf(tokens), "历史里翻出来的团队没有速率序列,不编造");

                // 通信记录:最新的在上;按成员 / 关键字筛
                // 右栏默认是「分工」页;通信记录在同一张卡的「通信」页
                Assert.AreEqual("roster", view.RailPage);
                Press(Named<Border>(view, "TeamRailFeedTab"));
                await PumpAsync(3);
                Assert.AreEqual("feed", view.RailPage);
                Border feed = Named<Border>(view, "TeamFeedPanel");
                // 通信页开窗时是隐藏的:页里 ScrollViewer 的内容在 headless 下要等下一次布局才进可视树,行是否存在按逻辑树数
                Border[] FeedRows() => [.. feed.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("tcFeedRow"))];
                Assert.HasCount(team.Messages.Count, FeedRows());
                Assert.AreSame(team.Messages[^1], FeedRows()[0].Tag);
                view.SetFeedParty("checker");
                await PumpAsync(3);
                // 审查那一条 + 开头发给「全体」的那一条
                Assert.HasCount(2, FeedRows());
                view.SetFeedParty("all");
                Named<TextBox>(view, "TeamFeedSearch").Text = "frp";
                await PumpAsync(3);
                Assert.HasCount(1, FeedRows(), "只有「#t2 修正 FRP 与防火墙」那条分派提到 FRP(搜索不分大小写)");
                Named<TextBox>(view, "TeamFeedSearch").Text = "";
                Named<ComboBox>(view, "TeamFeedKind").SelectedIndex = 2; // 交付
                await PumpAsync(3);
                Assert.HasCount(1, FeedRows());

                // 「通信」与「分工」两页只显示一页;拓扑下面一行是最新的一条通信
                Assert.IsTrue(feed.IsVisible);
                Assert.IsFalse(Named<Border>(view, "TeamRosterPanel").IsVisible);
                Assert.AreSame(team.Messages[^1], view.TickerMessage);
                Press(Named<Border>(view, "TeamRailRosterTab"));
                await PumpAsync(3);
                Assert.AreEqual("roster", view.RailPage);
                Assert.IsFalse(feed.IsVisible);
                Assert.IsTrue(Named<Border>(view, "TeamRosterPanel").IsVisible);

                // 右栏与下面的 Token 卡左右对齐;并排时右栏与拓扑等高
                Border rail = Named<Border>(view, "TeamRailPanel"), graphPanel = Named<Border>(view, "TeamGraphPanel");
                Assert.AreEqual(tokens.Bounds.X, rail.Bounds.X, 0.5, "右栏与 Token 卡左缘对齐");
                Assert.AreEqual(tokens.Bounds.Width, rail.Bounds.Width, 0.5, "右栏与 Token 卡同宽");
                Assert.AreEqual(graphPanel.Bounds.Height, rail.Bounds.Height, 0.5, "右栏与拓扑等高");

                // 版式:宽屏并排,窄了叠放
                Assert.IsTrue(view.IsWide);
                host.Width = 900;
                await PumpAsync(5);
                Assert.IsFalse(view.IsWide);
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 抽屉:点成员看成员(用量、任务、沟通对象),点任务看任务(负责人、前置、报告),点消息看两人的对话并高亮那一条;
    /// 一层层点进去能一层层返回,Esc 关掉。
    /// </summary>
    [TestMethod]
    public void TeamConsole_DrawerWalksBetweenMembersTasksAndConversations()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            string modelId = await SavePricedModelAsync(context);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                ChatPanelView.AssistantBubble bubble = AddBubble(panel);
                (TeamBoardSnapshot team, SubagentSnapshot[] runs) = SampleTeam(modelId);
                bubble.UpdateTeam(team, runs);
                await panel.OpenTeamConsoleAsync(bubble.TeamSession!);
                TeamConsoleView view = panel.OpenTeamConsoleView("team-1")!;
                host = await HostConsoleAsync(view);
                Border drawer = Named<Border>(view, "TeamDrawer");
                Border scrim = Named<Border>(view, "TeamDrawerScrim");
                Assert.IsFalse(view.IsDrawerOpen);
                Assert.IsFalse(scrim.IsHitTestVisible, "关着时遮罩不挡页面上的点击");
                Assert.IsTrue(drawer.IsVisible, "抽屉一直在树上,关着时只是停在屏幕右侧外面(开合才是一段从当前位置出发的滑动)");

                // 成员
                Border card = Named<Border>(view, "TeamRosterPanel").GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("tcCard") && Equals(b.Tag, "system"));
                Press(card);
                await PumpAsync(5);
                Assert.IsTrue(view.IsDrawerOpen);
                Assert.IsTrue(scrim.IsHitTestVisible);
                Assert.AreEqual("system · researcher", view.DrawerTitle);
                string[] agent = TextsOf(drawer);
                Assert.Contains("18k", agent, "输入 token");
                Assert.Contains("55%", agent, "缓存命中率 = 10 000 / 18 200");
                Assert.Contains("4%", agent, "上下文占用 = 8 000 / 200 000");
                Assert.Contains("≈0.07", agent);
                Assert.Contains("read_remote_file ×3", agent, "用过的工具");
                Assert.IsFalse(Named<Button>(view, "TeamDrawerBack").IsVisible, "第一层没有可返回的");

                // 成员 → 它负责的任务
                Press(drawer.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tcItem") && Equals(b.Tag, "t1")));
                await PumpAsync(5);
                Assert.AreEqual("task", view.CurrentDrawerView!.Kind);
                Assert.AreEqual("t1 · 审查 SSH 与权限", view.DrawerTitle);
                Assert.IsNotEmpty(drawer.GetVisualDescendants().OfType<MarkdownRenderer>(), "任务报告按 Markdown 渲染");
                // 页面是独立文档,图标、LiveMarkdown 的令牌与模板都得它自己带着:报告里的代码块要真的套上模板
                // (少了 LiveMarkdown 的样式,CodeBlock 没有模板,画出来是一片空白)
                Assert.IsTrue(view.TryFindResource("AiIcon.arrow-left", view.ActualThemeVariant, out _), "插件自己的图标");
                Assert.IsTrue(view.TryFindResource("FontSize2Xl", view.ActualThemeVariant, out _), "LiveMarkdown 的默认令牌");
                await HeadlessPump.UntilAsync(() => drawer.GetVisualDescendants().OfType<CodeBlock>().Any());
                CodeBlock code = drawer.GetVisualDescendants().OfType<CodeBlock>().First();
                Assert.IsNotNull(code.Template, "LiveMarkdown 的控件模板套上了");
                Assert.IsNotEmpty(code.GetVisualDescendants().OfType<Border>(), "模板真的展开成了可视树");
                Assert.IsEmpty(drawer.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "TeamTaskCancel"), "跑完的任务没有取消");
                Assert.IsTrue(Named<Button>(view, "TeamDrawerBack").IsVisible);

                // 任务 → 相关通信里的一条消息 → 两人之间的对话,高亮这一条
                Border message = drawer.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("tcItem") && b.Tag is TeamMessageInfo { Kind: "report" });
                Press(message);
                await PumpAsync(5);
                Assert.AreEqual("pair", view.CurrentDrawerView!.Kind);
                Assert.AreEqual("Conversation: system ⇄ lead", view.DrawerTitle);
                Border meta = Named<Border>(view, "TeamMessageMeta");
                Assert.Contains("Opus 5.5", TextsOf(meta), "元数据取自这条汇报对应的那次运行");
                Border[] bubbles = [.. drawer.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tcBubble"))];
                Assert.HasCount(2, bubbles, "分派一条、汇报一条");
                Assert.ContainsSingle(b => b.Classes.Contains("focus"), bubbles);

                // 一层层返回
                view.DrawerBack();
                await PumpAsync(3);
                Assert.AreEqual("task", view.CurrentDrawerView!.Kind);
                Named<Button>(view, "TeamDrawerBack").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(3);
                Assert.AreEqual("agent", view.CurrentDrawerView!.Kind);

                // Esc 关掉
                view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = view });
                await PumpAsync(3);
                Assert.IsFalse(view.IsDrawerOpen);
                Assert.IsFalse(scrim.IsHitTestVisible);
                Assert.IsNull(view.CurrentDrawerView);

                // 点遮罩也能关
                view.OpenView(new DrawerView("agent", "system"));
                await PumpAsync(3);
                Press(scrim);
                await PumpAsync(3);
                Assert.IsFalse(view.IsDrawerOpen);

                // 通信记录里点一条:直接到对话
                view.ShowRail("feed"); // 通信记录在右栏的「通信」页
                await PumpAsync(3);
                Border row = Named<Border>(view, "TeamFeedPanel").GetLogicalDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("tcFeedRow") && b.Tag is TeamMessageInfo { Kind: "review" });
                Press(row);
                await PumpAsync(5);
                Assert.AreEqual("Conversation: checker ⇄ network", view.DrawerTitle);
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 团队在跑:控制台跟着快照走 —— 成员状态、新到的消息(连线上飞光点、通信记录多一行)、
    /// 任务抽屉里的流式报告与取消按钮;这一轮收尾(取消入口收回)后按钮消失。
    /// </summary>
    [TestMethod]
    public void TeamConsole_FollowsTheRunningTeam()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            string modelId = await SavePricedModelAsync(context);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                ChatPanelView.AssistantBubble bubble = AddBubble(panel);
                List<string> cancelled = [];
                bubble.CancelSubagent = cancelled.Add;
                (TeamBoardSnapshot team, SubagentSnapshot[] runs) = SampleTeam(modelId, "running");
                bubble.UpdateTeam(team, runs);
                await panel.OpenTeamConsoleAsync(bubble.TeamSession!);
                TeamConsoleView view = panel.OpenTeamConsoleView("team-1")!;
                host = await HostConsoleAsync(view);

                Assert.Contains("Live", TextsOf(Named<Border>(view, "TeamConsoleHeader")));
                Border roster = Named<Border>(view, "TeamRosterPanel");
                Border Card(string name) => roster.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tcCard") && Equals(b.Tag, name));
                Assert.Contains("Working", TextsOf(Card("network")), "在调工具 = 执行中");
                Assert.Contains(t => t.EndsWith("patch_remote_file /etc/frp/frpc.toml", StringComparison.Ordinal), TextsOf(Card("network")), "此刻在做什么");
                Assert.Contains("Waiting", TextsOf(Card("checker")), "等依赖 = 等待中");
                Assert.Contains("Not started", TextsOf(Named<Border>(view, "TeamGanttPanel")));

                // 任务抽屉:在跑的任务有取消按钮,报告是到目前为止写出来的部分
                view.OpenView(new DrawerView("task", "t2"));
                await PumpAsync(5);
                Border drawer = Named<Border>(view, "TeamDrawer");
                Button cancel = Named<Button>(view, "TeamTaskCancel");
                Assert.IsNotEmpty(drawer.GetVisualDescendants().OfType<MarkdownRenderer>());
                cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.AreSequenceEqual(["sub-b"], cancelled, "取消的是这个任务当前的那次运行");
                view.CloseDrawer();

                // 新到一条成员之间的消息:通信记录多一行,对应的连线上飞起光点
                view.ShowRail("feed"); // 通信记录在右栏的「通信」页
                await PumpAsync(3);
                Border feed = Named<Border>(view, "TeamFeedPanel");
                int rowsBefore = feed.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("tcFeedRow"));
                var asked = new TeamMessageInfo(DateTimeOffset.Now, "network", "system", "peer", "which port does frps listen on?");
                bubble.UpdateTeam(team with { Messages = [.. team.Messages, asked] }, runs);
                await PumpAsync(3);
                Border[] rowsAfter = [.. feed.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("tcFeedRow"))];
                Assert.HasCount(rowsBefore + 1, rowsAfter);
                Assert.AreSame(asked, rowsAfter[0].Tag, "最新的在最上面");
                Assert.AreEqual(1, view.ActiveFlights, "沿 system ⇄ network 那条线飞一串光点");

                // 这一轮收尾:团队落定、取消入口收回
                bubble.CancelSubagent = null;
                (TeamBoardSnapshot done, SubagentSnapshot[] finalRuns) = SampleTeam(modelId);
                bubble.UpdateTeam(done, finalRuns);
                view.OpenView(new DrawerView("task", "t2"));
                await PumpAsync(5);
                Assert.Contains("Done", TextsOf(Named<Border>(view, "TeamConsoleHeader")));
                Assert.IsEmpty(drawer.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "TeamTaskCancel"));
                Assert.Contains("Idle", TextsOf(Card("network")));
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 没做成的任务(失败 / 受阻)在任务抽屉里有「重试」与「改派给」:点了就把任务 id(和接手的成员)交给引擎;
    /// 做成了的任务没有;这一轮收尾(重试入口收回)后也没有;引擎说没重试成时把原因写出来。
    /// </summary>
    [TestMethod]
    public void TeamConsole_FailedTaskCanBeRetriedOrHandedOver()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            string modelId = await SavePricedModelAsync(context);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                ChatPanelView.AssistantBubble bubble = AddBubble(panel);
                List<(string Task, string? Assignee)> retried = [];
                string answer = "Retrying #t2.";
                bubble.RetryTeamTask = (task, assignee) =>
                {
                    retried.Add((task, assignee));
                    return answer;
                };
                (TeamBoardSnapshot team, SubagentSnapshot[] runs) = SampleTeam(modelId);
                // t2 断流两次后失败,t3 因此受阻
                TeamBoardSnapshot broken = team with
                {
                    Phase = "failed",
                    Tasks =
                    [
                        team.Tasks[0],
                        team.Tasks[1] with { Status = "failed", Attempts = 3, Retries = 2, Note = "Upstream response stream was interrupted" },
                        team.Tasks[2] with { Status = "blocked", Attempts = 0, Verdict = null, SubagentId = null, Note = "#t2 failed" }
                    ]
                };
                bubble.UpdateTeam(broken, runs);
                await panel.OpenTeamConsoleAsync(bubble.TeamSession!);
                TeamConsoleView view = panel.OpenTeamConsoleView("team-1")!;
                host = await HostConsoleAsync(view);
                Border drawer = Named<Border>(view, "TeamDrawer");

                view.OpenView(new DrawerView("task", "t2"));
                await PumpAsync(5);
                string[] texts = TextsOf(drawer);
                Assert.Contains(t => t.Contains("Upstream response stream was interrupted", StringComparison.Ordinal), texts, "为什么失败");
                Assert.Contains(t => t.Contains("retried ×2", StringComparison.Ordinal), texts, "已经自动重试过几次");
                Border bar = Named<Border>(view, "TeamTaskRetryBar");
                Border[] others = [.. bar.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tcFilter"))];
                Assert.AreSequenceEqual((string[])["system", "checker"], [.. others.Select(b => (string)b.Tag!)], "可以改派给另外两名成员");

                Named<Button>(view, "TeamTaskRetry").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(("t2", null), retried.Single(), "重试:还是原来的成员");
                Press(others[1]);
                Assert.AreEqual(("t2", "checker"), retried[1], "改派:交给点中的成员");

                // 引擎没重试成(比如这一轮刚好收尾):把原因写出来,按钮恢复可点
                answer = "Could not retry #t2: Subagent service is shutting down.";
                view.OpenView(new DrawerView("task", "t2"), push: false);
                await PumpAsync(3);
                Button again = Named<Button>(view, "TeamTaskRetry");
                again.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Could not retry #t2: Subagent service is shutting down.", TextsOf(Named<Border>(view, "TeamTaskRetryBar")));
                Assert.IsTrue(again.IsEnabled);

                // 受阻的任务也能点(引擎会去重跑挡住它的上游);做成了的没有这一条
                view.OpenView(new DrawerView("task", "t3"));
                await PumpAsync(5);
                Assert.Contains(t => t.StartsWith("This task is blocked", StringComparison.Ordinal), TextsOf(Named<Border>(view, "TeamTaskRetryBar")));
                view.OpenView(new DrawerView("task", "t1"));
                await PumpAsync(5);
                Assert.IsEmpty(drawer.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "TeamTaskRetryBar"));

                // 这一轮收尾:引擎不在了,入口收回
                bubble.RetryTeamTask = null;
                bubble.UpdateTeam(broken, runs);
                view.OpenView(new DrawerView("task", "t2"));
                await PumpAsync(5);
                Assert.IsEmpty(drawer.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "TeamTaskRetryBar"));
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }
}
