using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 工具卡片的类别图标与差异、输入框上方的「已修改文件」条、停靠在标签区的差异文档。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    private const string PatchArgs = """{"path":"src/app.cs","oldText":"int b = 2;","newText":"int b = 3;\nint c = 4;"}""";

    private static string TextOf(Control root, string name)
        => root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == name).Text ?? "";

    [TestMethod]
    public void ToolCard_ShowsKindIcon_DiffStats_AndInlineDiffFromArguments()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                // 历史回放也走这条:只有持久化的参数,补丁的差异照样算得出来
                bubble.AddToolCall("c1", "patch_local_file", PatchArgs);
                bubble.CompleteToolCall("c1", "Patched src/app.cs.");
                bubble.AddToolCall("c2", "myserver_lookup", """{"query":"x"}""");
                await PumpAsync(5);

                List<Border> cards = ToolItems(bubble.Root);
                Decorator kind = cards[0].GetVisualDescendants().OfType<Decorator>().First(d => d.Name == "ToolKindIcon");
                Assert.AreEqual("AiIcon.file-pen", kind.Tag);
                Assert.AreEqual("AiIcon.plug", cards[1].GetVisualDescendants().OfType<Decorator>().First(d => d.Name == "ToolKindIcon").Tag,
                    "MCP 工具用默认的插头图标");
                Assert.AreEqual("+2", TextOf(cards[0], "ToolDiffAdded"));
                Assert.AreEqual("−1", TextOf(cards[0], "ToolDiffRemoved"));

                Press(cards[0].GetVisualDescendants().OfType<Grid>().First());
                await PumpAsync(5);
                DiffView diff = cards[0].GetVisualDescendants().OfType<DiffView>().Single();
                Assert.AreEqual(1, diff.Rows.Children.Count(r => r.Classes.Contains("diffRemoved")));
                Assert.AreEqual(2, diff.Rows.Children.Count(r => r.Classes.Contains("diffAdded")));
                string details = string.Join("\n", cards[0].GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text));
                Assert.DoesNotContain("oldText", details, "差异已经画出来了,原始的 oldText / newText 不再整段列出");
                StringAssert.Contains(details, "Patched src/app.cs.");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void FileChanged_FillsChangedFilesBar_AttachesToCard_AndClears()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                panel.ActiveConversation.ActiveBubble = bubble;
                bubble.AddToolCall("c1", "patch_local_file", PatchArgs);
                Border bar = Find<Border>(panel, "ChangedFilesBar");
                Assert.IsFalse(bar.IsVisible, "一个文件都没改时整条隐藏");

                // 回调可能来自工具线程:走投递
                await Task.Run(() => panel.OnToolFileChanged(new FileChange("patch_local_file", "local:w", "src/app.cs",
                    FileChangeKind.Modified, "int a = 1;\nint b = 2;\n", "int a = 1;\nint b = 3;\nint c = 4;\n")));
                await PumpAsync(5);
                panel.OnToolFileChanged(new FileChange("delete_local_path", "local:w", "old/readme.md",
                    FileChangeKind.Deleted, "x\ny\n", null));
                await PumpAsync(5);

                Assert.IsTrue(bar.IsVisible);
                Assert.AreEqual("2 files changed", TextOf(bar, "ChangedFilesTitle"));
                Assert.AreEqual("+2", TextOf(bar, "ChangedFilesAdded"));
                Assert.AreEqual("−3", TextOf(bar, "ChangedFilesRemoved"));
                Press(bar.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "ChangedFilesHeader"));
                await PumpAsync(3);
                Assert.AreSequenceEqual(["M", "D"], bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ChangedFileBadge").Select(t => t.Text).ToArray());

                ChatPanelView.ToolCard card = bubble.ToolCardsForTest().Single();
                Assert.IsNotNull(card.FileChange, "改动挂到发起它的那张卡上");
                Assert.IsTrue(card.Diff!.HasLineNumbers, "换成整份前后文的真实差异");

                RaiseClick(bar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChangedFilesKeepAll"));
                await PumpAsync(3);
                Assert.IsFalse(bar.IsVisible);
                Assert.IsEmpty(panel.ActiveChangedFiles.Entries);

                panel.OnToolFileChanged(new FileChange("write_local_file", "local:w", "n.txt", FileChangeKind.Created, null, "a"));
                await PumpAsync(3);
                Assert.IsTrue(bar.IsVisible);
                Click(panel, "NewChatButton");
                await PumpAsync(3);
                Assert.IsFalse(bar.IsVisible, "新建对话清空列表");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ChangedFile_OpensDockedDiffTab_ReusesItAndRefreshesInPlace()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.OnToolFileChanged(new FileChange("patch_remote_file", "remote:s1", "/etc/app.conf",
                    FileChangeKind.Modified, "a\nb\nc\n", "a\nB\nc\n"));
                await PumpAsync(3);
                Border bar = Find<Border>(panel, "ChangedFilesBar");
                Press(bar.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "ChangedFilesHeader"));
                await PumpAsync(3);
                Border row = bar.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("changedFileRow"));
                int before = context.FakeUi.Panels.Count;
                Press(row);
                await PumpAsync(5);

                Assert.HasCount(before + 1, context.FakeUi.Panels);
                FakePanel tab = context.FakeUi.LastPanel;
                Assert.AreEqual(PanelDisplayMode.Document, tab.Options.DisplayMode);
                Assert.AreEqual(PanelPlacement.Tabs, tab.Options.Placement);
                Assert.AreEqual("app.conf (changes)", tab.Options.Title);
                var view = (FileDiffDocumentView)tab.CreateContent();
                Assert.IsTrue(view.IsSideBySide, "默认左右并排");
                Assert.AreEqual("a\nb\nc\n", view.LeftEditor!.Text);
                Assert.AreEqual("a\nB\nc\n", view.RightEditor!.Text, "右侧是改动后的真实全文");
                Assert.IsTrue(view.LeftEditor.IsReadOnly);
                Assert.IsFalse(view.RightEditor.IsReadOnly, "知道位置、有整份新内容:右侧能编辑");
                Assert.AreEqual(1, view.CurrentDiff!.Added);
                Assert.AreEqual(1, view.CurrentDiff.Removed);
                Assert.HasCount(1, view.ChangeStarts);
                Assert.AreEqual("Remote · s1", view.Document.Location);

                // 同一个文件再点:切过去,不重复开
                Press(row);
                await PumpAsync(5);
                Assert.HasCount(before + 1, context.FakeUi.Panels);
                Assert.AreEqual(1, tab.ActivateCount);

                // 文件又被改了:已开着的标签原地刷新
                panel.OnToolFileChanged(new FileChange("patch_remote_file", "remote:s1", "/etc/app.conf",
                    FileChangeKind.Modified, "a\nB\nc\n", "a\nB\nc\nd\n"));
                await PumpAsync(3);
                Assert.AreEqual(2, view.Document.Diff!.Added);
                Assert.AreEqual(1, view.Document.Diff.Removed, "Before 仍取第一次改动之前");

                // 切到内联:复用统一差异控件
                RaiseClick(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "DiffInlineToggle"));
                Assert.IsFalse(view.IsSideBySide);
                Assert.IsNotNull(view.InlineView);
                Assert.AreEqual(1, view.InlineView.Rows.Children.Count(r => r.Classes.Contains("diffRemoved")));

                // 标签关了:再点就重开一个
                await tab.CloseAsync();
                await PumpAsync(3);
                Press(bar.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("changedFileRow")));
                await PumpAsync(5);
                Assert.HasCount(before + 2, context.FakeUi.Panels);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void DiffTab_FailureFallsBackToWindow_AndCardButtonOpensFragment()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                bubble.AddToolCall("c1", "patch_local_file", PatchArgs);
                await PumpAsync(3);
                Border card = ToolItems(bubble.Root).Single();
                Press(card.GetVisualDescendants().OfType<Grid>().First());
                await PumpAsync(3);
                Button open = card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenDiffInTab");

                RaiseClick(open);
                await PumpAsync(5);
                FakePanel tab = context.FakeUi.LastPanel;
                Assert.AreEqual(PanelDisplayMode.Document, tab.Options.DisplayMode);
                Assert.AreEqual("app.cs (changes)", tab.Options.Title);
                var view = (FileDiffDocumentView)tab.CreateContent();
                Assert.IsFalse(view.Document.Diff!.HasLineNumbers, "只有片段时只比对片段");
                await tab.CloseAsync();
                await PumpAsync(3);

                // 老宿主 / 打开失败:退回独立窗口,不崩
                panel.ShowDocumentOverride = (_, _) => throw new NotSupportedException("no document tabs");
                RaiseClick(open);
                await PumpAsync(5);
                FakePanel fallback = context.FakeUi.LastPanel;
                Assert.AreEqual(PanelDisplayMode.Window, fallback.Options.DisplayMode);
                Assert.IsInstanceOfType<FileDiffDocumentView>(fallback.CreateContent());
                Assert.IsGreaterThanOrEqualTo(1000d, fallback.Options.WindowWidth, "退回窗口按屏幕的较大比例开,不再是小窗");
                Assert.IsGreaterThanOrEqualTo(760d, fallback.Options.WindowHeight);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
