using Avalonia.Controls;
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
}
