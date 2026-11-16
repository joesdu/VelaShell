using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    private static List<Border> ToolItems(Control root)
        => [.. root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("toolItem"))];

    private static void Press(Control target)
        => target.RaiseEvent(new PointerPressedEventArgs(target, new Pointer(0, PointerType.Mouse, true),
            target, default, 0, new PointerPointProperties(), KeyModifiers.None));

    /// <summary>
    /// 相邻的工具调用并成一组:运行中露出最近两张,模型开始写字后收成一行,组头写清次数与需留意的数量;
    /// 整轮收尾把过程收进「工作过程」,结论正文留在外面。
    /// </summary>
    [TestMethod]
    public void ToolCalls_GroupWhileRunning_SealOnText_FoldIntoWorkLog()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                var bubble = new ChatPanelView.AssistantBubble(panel);
                messages.Children.Add(bubble.Root);
                for (int i = 1; i <= 4; i++)
                    bubble.AddToolCall($"c{i}", i == 4 ? "read_file" : "run_command", $"{{\"command\":\"echo {i}\"}}");
                for (int i = 1; i <= 3; i++) bubble.CompleteToolCall($"c{i}", $"out {i}");
                bubble.CompleteToolCall("c4", "Tool failed; execution paused. Its side effects must not be retried automatically.");
                await PumpAsync();

                Border group = bubble.Root.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toolGroup"));
                Assert.IsTrue(group.Classes.Contains("grouped"));
                List<Border> cards = ToolItems(group);
                Assert.HasCount(4, cards);
                CollectionAssert.AreEqual(new[] { false, false, true, true }, cards.Select(c => c.IsVisible).ToArray(),
                    "运行中只露出最近两张");

                bubble.AppendText("Disk usage is fine.");
                await PumpAsync();
                Assert.IsTrue(cards.All(c => !c.IsVisible), "模型开始写字后,前面这一串收成一行");
                string header = string.Join(" ", group.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text));
                StringAssert.Contains(header, "Ran 4 actions");
                StringAssert.Contains(header, "run_command ×3");
                StringAssert.Contains(header, "1 need attention");

                Grid groupHeader = group.GetVisualDescendants().OfType<Grid>().First(g => g.IsVisible);
                Press(groupHeader);
                await PumpAsync();
                Assert.IsTrue(cards.All(c => c.IsVisible), "点组头展开全部");

                bubble.FinishStreaming("model", DateTimeOffset.Now, TimeSpan.FromSeconds(42));
                await PumpAsync();
                Border fold = bubble.Root.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("processFold"));
                Assert.IsTrue(fold.GetVisualDescendants().Contains(group), "工具组收进「工作过程」");
                Assert.Contains(t => t.Text?.StartsWith("Work log · 4 actions", StringComparison.Ordinal) == true,
                    fold.GetVisualDescendants().OfType<TextBlock>());
                MarkdownRenderer conclusion = bubble.Root.GetVisualDescendants().OfType<MarkdownRenderer>().Single();
                Assert.IsFalse(fold.GetVisualDescendants().Contains(conclusion), "结论正文留在外面");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>被停下的一轮不收进「工作过程」:停在哪一步得让人一眼看到。单张卡不出组头。</summary>
    [TestMethod]
    public void CancelledTurn_IsNotFolded_AndSingleCallHasNoGroupHeader()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                var single = new ChatPanelView.AssistantBubble(panel);
                messages.Children.Add(single.Root);
                single.AddToolCall("s1", "list_sessions", "{}");
                await PumpAsync();
                Border lone = single.Root.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toolGroup"));
                Assert.IsFalse(lone.Classes.Contains("grouped"));
                Assert.IsTrue(ToolItems(lone).Single().IsVisible);

                var cancelled = new ChatPanelView.AssistantBubble(panel);
                messages.Children.Add(cancelled.Root);
                cancelled.AddToolCall("x1", "run_command", "{\"command\":\"a\"}");
                cancelled.AddToolCall("x2", "run_command", "{\"command\":\"b\"}");
                cancelled.AppendText("partial");
                cancelled.FinishStreaming(cancelled: true);
                await PumpAsync();
                Assert.IsFalse(cancelled.Root.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("processFold")));
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 团队卡的任务行:负责人简写圈在标题左侧(各行对齐),右侧依次是输入 token 与耗时、都右对齐;
    /// 团队消息的「谁 → 谁」用同样的简写圈;跑完的团队显示「已完成」。
    /// </summary>
    [TestMethod]
    public void TeamCard_AlignsAvatarsLeftAndTokensTimeRight()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                window.Width = 460;
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                var bubble = new ChatPanelView.AssistantBubble(panel);
                messages.Children.Add(bubble.Root);
                TeamMemberInfo[] members = [new("system", "researcher", null, false, 0), new("network", "researcher", null, false, 1)];
                TeamTaskInfo[] tasks =
                [
                    new("t1", "审查 SSH 与权限", "system", "completed", [], null, 1, null, "sub-a", "ok", "", TimeSpan.FromSeconds(34)),
                    new("t2", "审查 FRP 与防火墙", "network", "completed", [], null, 1, null, "sub-b", "ok", "", TimeSpan.FromSeconds(40))
                ];
                var team = new TeamBoardSnapshot("team-1", "安全审计", "completed", members, tasks,
                    [new(DateTimeOffset.Now, "network", "lead", "report", "#t2 done")], TimeSpan.FromSeconds(80));
                SubagentSnapshot[] runs =
                [
                    new("sub-a", "system", "completed", "", "ok", [], [], TimeSpan.FromSeconds(34), InputTokens: 18_200, OutputTokens: 900, TeamTaskId: "t1"),
                    new("sub-b", "network", "completed", "", "ok", [], [], TimeSpan.FromSeconds(40), InputTokens: 9_400, OutputTokens: 700, TeamTaskId: "t2")
                ];
                bubble.UpdateTeam(team, runs);
                await PumpAsync();

                Border board = bubble.Root.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("teamBoard"));
                Assert.Contains(t => t.Text == "Done", board.GetVisualDescendants().OfType<TextBlock>());
                Border[] rows = [.. board.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("teamTask"))];
                Assert.HasCount(2, rows);
                double? titleLeft = null;
                foreach (Border row in rows)
                {
                    TextBlock title = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.StartsWith("审查", StringComparison.Ordinal) == true);
                    Assert.DoesNotContain("#", title.Text!, "标题只写任务本身,不带任务 id");
                    TextBlock tokens = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.EndsWith(" tok", StringComparison.Ordinal) == true);
                    TextBlock elapsed = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.EndsWith('s') == true && t.Text.Any(char.IsDigit) && t != tokens && t != title);
                    double left = title.TranslatePoint(default, board)!.Value.X;
                    titleLeft ??= left;
                    Assert.AreEqual(titleLeft.Value, left, 0.5, "各行标题左缘对齐");
                    Assert.IsLessThan(left, row.GetVisualDescendants().OfType<Border>().First(b => b.CornerRadius.TopLeft > 0 && b.Width == 16).TranslatePoint(default, board)!.Value.X + 1,
                        "简写圈在标题左侧");
                    Assert.IsLessThan(elapsed.TranslatePoint(default, board)!.Value.X, tokens.TranslatePoint(default, board)!.Value.X, "token 在耗时左侧");
                    Assert.AreEqual(TextAlignment.Right, elapsed.TextAlignment);
                }
                Assert.Contains(t => t.Text == "18k tok", board.GetVisualDescendants().OfType<TextBlock>());

                // 团队消息展开再收起:卡片高度回到展开之前,不留一大块空白
                double before = board.Bounds.Height;
                TextBlock messagesTitle = board.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Team messages", StringComparison.Ordinal) == true);
                Control messagesHeader = (Control)messagesTitle.Parent!;
                Press(messagesHeader);
                await PumpAsync();
                Assert.IsGreaterThan(before, board.Bounds.Height, "展开后变高");
                Press(messagesHeader);
                await PumpAsync();
                Assert.AreEqual(before, board.Bounds.Height, 0.5, "收起后回到原高度");
                Assert.DoesNotContain(t => t.Text == "network", board.GetVisualDescendants().OfType<TextBlock>(), "成员名不再写全称,只用简写圈");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>展开工具卡:参数按「键: 值」逐行显示,不再是 <c>\u</c> 开头的转义码;头部摘要取主参数。</summary>
    [TestMethod]
    public void ToolCardDetails_AreHumanReadable()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                var bubble = new ChatPanelView.AssistantBubble(panel);
                messages.Children.Add(bubble.Root);
                bubble.AddToolCall("c1", "run_command",
                    "{\"command\":\"python3 - \\u003C\\u003C\\u0027PY\\u0027\\nprint(\\u0027\\u4E2D\\u6587\\u0027)\\nPY\",\"waitSeconds\":2}");
                bubble.CompleteToolCall("c1", "{\"state\":\"Succeeded\",\"output\":\"\\u4E2D\\u6587\"}");
                await PumpAsync();

                Border card = ToolItems(bubble.Root).Single();
                Assert.Contains(t => t.Text == "python3 - <<'PY' print('中文') PY", card.GetVisualDescendants().OfType<TextBlock>());
                Press(card.GetVisualDescendants().OfType<Grid>().First());
                await PumpAsync();
                string details = card.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text!.ReplaceLineEndings("\n");
                StringAssert.Contains(details, "command:\n  python3 - <<'PY'\n  print('中文')\n  PY");
                StringAssert.Contains(details, "output: 中文");
                Assert.DoesNotContain("\\u", details);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
