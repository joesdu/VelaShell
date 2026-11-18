using Avalonia.Controls;

using AvaloniaEdit;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>桌面的系统提示词:自定义提示词追加在内置说明之后,不再整段替换(ai-plan §12.2 的 1b)。</summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SystemPrompt_CustomPromptIsAppendedAfterTheBuiltInRules(bool modelLevel)
    {
        OnUi(async () =>
        {
            using var chat = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("current", chat.BaseUrl, "chat-model");
            if (modelLevel) provider.Models[0].SystemPrompt = "MODEL-LEVEL STYLE";
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent,
                Approval = ApprovalMode.Bypass,
                SuggestFollowUps = false,
                SystemPrompt = "GLOBAL STYLE"
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("hello");
                Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > 0 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                string body = chat.Requests[0].Body;

                int builtIn = body.IndexOf("You are the AI assistant embedded in VelaShell", StringComparison.Ordinal);
                int agentRules = body.IndexOf("You can call tools to inspect", StringComparison.Ordinal);
                string expected = modelLevel ? "MODEL-LEVEL STYLE" : "GLOBAL STYLE";
                int custom = body.IndexOf("Operator instructions: " + expected, StringComparison.Ordinal);
                Assert.IsGreaterThanOrEqualTo(0, builtIn, "内置说明照常发出");
                Assert.IsGreaterThan(builtIn, agentRules, "Agent 模式说明不再被自定义提示词吞掉");
                Assert.IsGreaterThan(agentRules, custom, "自定义提示词排在内置说明之后");
                Assert.AreEqual(!modelLevel, body.Contains("GLOBAL STYLE", StringComparison.Ordinal), "模型级的优先,全局的不叠加");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 桌面:工作区里的技能进技能目录(排在自定义提示词之前)、摆出 load_skill;模型调 load_skill 拿到正文(ai-plan §12.3)。
    /// </summary>
    [TestMethod]
    public void Skills_WorkspaceSkillIsIndexedAndLoadable()
    {
        OnUi(async () =>
        {
            string workspace = Path.Combine(Path.GetTempPath(), "panel-skills-" + Guid.NewGuid().ToString("N"));
            string skillDir = Path.Combine(workspace, ".velashell", "skills", "nginx-check");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: nginx-check\ndescription: Check nginx config safely.\n---\nAlways run nginx -t before reloading.");
            try
            {
                using var chat = new KeyAuthStub((_, body) => (200, body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
                    ? LifecycleAnswer
                    : ImageToolCall("load_skill", "{\"name\":\"nginx-check\"}")));
                using var context = new TestPluginContext();
                var store = new AiSettingsStore(context);
                AiProvider provider = StubProvider("current", chat.BaseUrl, "chat-model");
                var local = new LocalWorkspace { Name = "repo", RootPath = workspace };
                await store.SaveAsync(new AiSettings
                {
                    Providers = [provider],
                    ActiveModelId = provider.Models[0].Id,
                    Mode = ChatMode.Agent,
                    Approval = ApprovalMode.Bypass,
                    SuggestFollowUps = false,
                    SystemPrompt = "GLOBAL STYLE",
                    LocalWorkspaces = [local],
                    LocalDefaultWorkspaceId = local.Id
                });
                (Window window, ChatPanelView panel) = await ShowAsync(context);
                try
                {
                    panel.SendExternal("reload nginx");
                    Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > 1 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                    string first = chat.Requests[0].Body;
                    int index = first.IndexOf("- nginx-check (workspace): Check nginx config safely.", StringComparison.Ordinal);
                    Assert.IsGreaterThan(0, index, "技能目录进了系统提示词");
                    Assert.IsGreaterThan(index, first.IndexOf("Operator instructions: GLOBAL STYLE", StringComparison.Ordinal), "自定义提示词仍排最后");
                    Assert.Contains("\"load_skill\"", first);
                    Assert.Contains("Always run nginx -t before reloading.", chat.Requests[1].Body, "load_skill 把正文交回模型");

                    // /技能名 显式调用:送给模型的那份附上技能正文;气泡里仍是用户敲的那一行
                    int before = chat.Requests.Count;
                    Find<TextEditor>(panel, "InputBox").Text = "/nginx-check reload prod"; // 用户敲的才认斜杠命令(SendExternal 不认)
                    Click(panel, "SendButton");
                    Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > before && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                    string invoked = chat.Requests[before].Body;
                    Assert.Contains("explicitly invoked the skill", invoked);
                    Assert.Contains("<skill name=", invoked);
                    Assert.Contains("User request: reload prod", invoked);
                    // 历史库里(气泡与载入旧会话都读它)留的是原话,展开的指令只在送给模型的那份里
                    var history = new ChatHistoryStore(context);
                    await history.InitAsync();
                    IReadOnlyList<ChatEntry> saved = await history.LoadAsync(panel.ActiveConversation.ConversationId);
                    List<string> userLines = [.. saved.Where(e => e.Role == "user").Select(e => e.Text)];
                    Assert.Contains("/nginx-check reload prod", userLines, "历史里存原话");
                    Assert.DoesNotContain(t => t.Contains("explicitly invoked", StringComparison.Ordinal), userLines, "展开的指令不进历史");
                }
                finally { panel.Detach(); window.Close(); }
            }
            finally
            {
                try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
            }
        });
    }
}
