using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>输入框上方的「你应该知道」条:显示、折叠、逐条与全部关掉、新建对话清空,以及真实一轮里模型调 note_for_user。</summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void NotesBar_ShowsNotes_CollapsesDismisses_AndClearsOnNewChat()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Border bar = Find<Border>(panel, "NotesBar");
                Assert.IsFalse(bar.IsVisible, "没有条目时整条隐藏");

                // 提示条已经显示了内容,回复里不另摆一张 note_for_user 的工具卡(直播与载入历史都走 AddToolCall)
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                bubble.AddToolCall("c1", ConversationNotes.ToolName, """{"kind":"info","title":"x"}""");
                bubble.AddToolCall("c2", "list_sessions", "{}");
                Assert.AreSequenceEqual(["list_sessions"], [.. bubble.ToolCardsForTest().Select(c => c.ToolName)]);

                // 工具调用在轮次的异步流里,可能不在 UI 线程:走投递
                await Task.Run(() =>
                {
                    panel.ActiveNotes.Add("warning", "Port 22 is open to the internet", "sshd listens on 0.0.0.0");
                    panel.ActiveNotes.Add("action", "Restart nginx", null);
                });
                await PumpAsync(5);

                Assert.IsTrue(bar.IsVisible);
                Assert.AreEqual("You should know (2)", TextOf(bar, "NotesTitle"));
                StackPanel list = bar.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "NotesList");
                Assert.IsTrue(list.IsEffectivelyVisible, "默认展开");
                Assert.Contains("sshd listens on 0.0.0.0", bar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

                Press(bar.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "NotesHeader"));
                await PumpAsync(3);
                Assert.IsFalse(bar.GetVisualDescendants().OfType<ScrollViewer>().Single().IsVisible, "点标题行收起");
                Assert.AreEqual("You should know (2)", TextOf(bar, "NotesTitle"), "收起时标题仍显示条数");
                Press(bar.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "NotesHeader"));
                await PumpAsync(3);

                RaiseClick(bar.GetVisualDescendants().OfType<Button>().First(b => b.Name == "NoteDismiss"));
                await PumpAsync(3);
                Assert.AreEqual("You should know (1)", TextOf(bar, "NotesTitle"));

                RaiseClick(bar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NotesDismissAll"));
                await PumpAsync(3);
                Assert.IsFalse(bar.IsVisible);

                panel.ActiveNotes.Add("info", "Backups run at 03:00", null);
                await PumpAsync(3);
                Assert.IsTrue(bar.IsVisible);
                Click(panel, "NewChatButton");
                await PumpAsync(3);
                Assert.IsFalse(bar.IsVisible, "新建对话清空");
                Assert.IsEmpty(panel.ActiveNotes.Visible);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>真实一轮:模型调 note_for_user,条目出现在提示条上;对话模式、或在「配置工具」里停用时不给这个工具。</summary>
    [TestMethod]
    [DataRow(ChatMode.Agent, false)]
    [DataRow(ChatMode.Plan, false)]
    [DataRow(ChatMode.Chat, false)]
    [DataRow(ChatMode.Agent, true)]
    public void NoteForUser_FromTheModel_PinsToTheBar(ChatMode mode, bool disabled)
    {
        OnUi(async () =>
        {
            bool offered = mode != ChatMode.Chat && !disabled;
            using var chat = new KeyAuthStub((_, body) => (200,
                !offered || body.Contains("\"role\":\"tool\"", StringComparison.Ordinal) ? LifecycleAnswer
                    : ImageToolCall(ConversationNotes.ToolName, JsonSerializer.Serialize(new
                    {
                        kind = "warning",
                        title = "证书 3 天后过期",
                        detail = "web-1 上的 /etc/ssl/site.pem"
                    }))));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("current", chat.BaseUrl, "chat-model");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = mode,
                Approval = ApprovalMode.Bypass,
                SuggestFollowUps = false,
                DisabledBuiltinTools = disabled ? ConversationNotes.ToolName : ""
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("检查一下证书");
                Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > 0 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                await PumpAsync(5);

                Assert.AreEqual(offered, chat.Requests[0].Body.Contains(ConversationNotes.ToolName, StringComparison.Ordinal));
                Border bar = Find<Border>(panel, "NotesBar");
                Assert.AreEqual(offered, bar.IsVisible);
                if (offered)
                {
                    ConversationNote note = panel.ActiveNotes.Visible.Single();
                    Assert.AreEqual("warning", note.Kind);
                    Assert.AreEqual("证书 3 天后过期", note.Title);
                    Assert.AreEqual("web-1 上的 /etc/ssl/site.pem", note.Detail);
                }
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
