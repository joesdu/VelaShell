using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Chat.Memory;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>记忆:「配置工具」的「记忆」页,以及桌面一轮里模型保存记忆、下一轮带上记忆清单。</summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void ToolsWindow_MemoryPage_AddRejectsSecretsEditsDeletesClearsAndToggles()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var settings = new AiSettings();
            var store = new AiSettingsStore(context);
            var picker = new ToolPickerView(context, settings, new Loc("en"), () => store.SaveAsync(settings), _ => { });
            Window host = Host(picker);
            try
            {
                MemoryStore memories = MemoryStore.For(context.DataDirectory);
                picker.ShowPage(ToolPickerView.Page.Memory);
                await PumpAsync(2);
                Assert.AreEqual("0", NavCount(picker, ToolPickerView.Page.Memory));
                Assert.Contains(t => t.Name == "MemoryEmpty", picker.GetVisualDescendants().OfType<TextBlock>());
                ConfirmOverlay confirm = picker.GetVisualDescendants().OfType<ConfirmOverlay>().Single();
                Button Named(string name) => picker.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);
                static void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                TextBox Box(string name) => picker.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == name);

                // 新增:机密被拒,原因写在输入框下面;正常的存下来,标明是用户加的
                Press(Named("MemoryAdd"));
                await PumpAsync(2);
                Box("MemoryTitleBox").Text = "DB";
                Box("MemoryContentBox").Text = "password: hunter2hunter2";
                Press(Named("MemorySave"));
                await PumpAsync(2);
                TextBlock error = picker.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MemoryEditError");
                Assert.IsTrue(error.IsVisible);
                StringAssert.Contains(error.Text, "Secrets");
                Assert.IsEmpty(memories.List());
                Box("MemoryContentBox").Text = "Production database is PostgreSQL 16.";
                Press(Named("MemorySave"));
                await PumpAsync(2);
                MemoryEntry mine = memories.List().Single();
                Assert.AreEqual("user", mine.Origin);
                Assert.AreEqual("1", NavCount(picker, ToolPickerView.Page.Memory));
                StringAssert.Contains(picker.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MemoryMeta").Text, "added by you");

                // 模型在对话里又存了一条:窗口开着,列表当场刷新
                memories.Add("Shell", "The user's shell is zsh.", "model");
                await PumpAsync(3);
                Assert.HasCount(2, Sources(picker).Where(s => ((string?)s.Tag)?.StartsWith("memory:m", StringComparison.Ordinal) == true));
                Assert.Contains(t => t.Text?.Contains("saved by the agent", StringComparison.Ordinal) == true, picker.GetVisualDescendants().OfType<TextBlock>());

                // 编辑
                Press(Source(picker, "memory:" + mine.Id).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MemoryEdit"));
                await PumpAsync(2);
                Assert.AreEqual("DB", Box("MemoryTitleBox").Text);
                Box("MemoryContentBox").Text = "Production database is PostgreSQL 17.";
                Press(Named("MemorySave"));
                await PumpAsync(2);
                Assert.AreEqual("Production database is PostgreSQL 17.", memories.Find(mine.Id)!.Content);

                // 删除要确认:取消不删,确认才删
                Press(Source(picker, "memory:" + mine.Id).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MemoryDelete"));
                await PumpAsync(2);
                Assert.IsTrue(confirm.IsVisible);
                Press(confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmCancelButton"));
                await PumpAsync(2);
                Assert.IsNotNull(memories.Find(mine.Id));
                Press(Source(picker, "memory:" + mine.Id).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MemoryDelete"));
                await PumpAsync(2);
                Press(confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmAcceptButton"));
                await PumpAsync(3);
                Assert.IsNull(memories.Find(mine.Id));

                // 全部清除(确认)
                Press(Named("MemoryClearAll"));
                await PumpAsync(2);
                Press(confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmAcceptButton"));
                await PumpAsync(3);
                Assert.IsEmpty(memories.List());

                // 总开关:关掉随设置保存
                SwitchOf(Source(picker, "memory:enabled")).IsChecked = false;
                await PumpAsync(3);
                Assert.IsFalse(settings.MemoryEnabled);
                Assert.IsFalse((await store.LoadAsync()).MemoryEnabled);
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>
    /// 真实一轮:Agent 模式下模型调 save_memory 存进记忆,下一轮的系统提示词带上记忆清单;
    /// 对话模式只读(有清单、没有工具);关掉记忆时两样都没有。
    /// </summary>
    [TestMethod]
    [DataRow(ChatMode.Agent, true)]
    [DataRow(ChatMode.Chat, true)]
    [DataRow(ChatMode.Agent, false)]
    public void Memory_ModelSavesInAgentMode_AndTheNextTurnSeesIt(ChatMode mode, bool enabled)
    {
        OnUi(async () =>
        {
            using var chat = new KeyAuthStub((_, body) => (200,
                mode != ChatMode.Agent || !enabled || body.Contains("\"role\":\"tool\"", StringComparison.Ordinal) || body.Contains("second turn", StringComparison.Ordinal)
                    ? LifecycleAnswer
                    : ImageToolCall(MemoryToolSource.SaveToolName, JsonSerializer.Serialize(new { title = "Prefers tables", content = "Show comparisons as tables." }))));
            using var context = new TestPluginContext();
            MemoryStore memories = MemoryStore.For(context.DataDirectory);
            if (mode == ChatMode.Chat) memories.Add("Prefers tables", "Show comparisons as tables.", "user");
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("current", chat.BaseUrl, "chat-model");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = mode,
                Approval = ApprovalMode.Bypass,
                SuggestFollowUps = false,
                MemoryEnabled = enabled
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("compare nginx and caddy");
                Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > 0 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.AreEqual(mode == ChatMode.Agent && enabled, chat.Requests[0].Body.Contains("\"save_memory\"", StringComparison.Ordinal),
                    "只有 Agent 模式且记忆开着才给保存工具");
                if (mode == ChatMode.Agent && enabled)
                {
                    Assert.AreEqual("Prefers tables", memories.List().Single().Title, "模型存进了记忆");
                    Assert.AreEqual("model", memories.List().Single().Origin);
                }

                int before = chat.Requests.Count;
                panel.SendExternal("second turn");
                Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > before && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                string next = chat.Requests[^1].Body;
                Assert.AreEqual(enabled, next.Contains("Prefers tables: Show comparisons as tables.", StringComparison.Ordinal),
                    "下一轮的系统提示词带上记忆清单(关掉记忆时不带)");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
