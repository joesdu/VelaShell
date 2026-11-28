using Avalonia.Controls;
using Avalonia.Input;
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
                Assert.AreSequenceEqual([false, false, true, true], [.. cards.Select(c => c.IsVisible)], "运行中只露出最近两张");

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
                Assert.Contains(group, fold.GetVisualDescendants(), "工具组收进「工作过程」");
                Assert.Contains(t => t.Text?.StartsWith("Work log · 4 actions", StringComparison.Ordinal) == true,
                    fold.GetVisualDescendants().OfType<TextBlock>());
                MarkdownRenderer conclusion = bubble.Root.GetVisualDescendants().OfType<MarkdownRenderer>().Single();
                Assert.DoesNotContain(conclusion, fold.GetVisualDescendants(), "结论正文留在外面");
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
                Assert.DoesNotContain(b => b.Classes.Contains("processFold"), cancelled.Root.GetVisualDescendants().OfType<Border>());
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
