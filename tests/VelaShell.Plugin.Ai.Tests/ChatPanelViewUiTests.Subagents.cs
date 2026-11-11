using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    [DataRow(ChatMode.Agent, false, true)]
    [DataRow(ChatMode.Agent, true, false)]
    [DataRow(ChatMode.Chat, false, false)]
    [DataRow(ChatMode.Plan, false, false)]
    public void SubagentGuidance_TracksAvailableToolsWithCustomPrompt(ChatMode mode, bool disabled, bool expected)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var provider = StubProvider("test", endpoint.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = mode,
                SystemPrompt = "Custom instructions retained.",
                DisabledBuiltinTools = disabled ? "delegate_subagent" : "",
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("Analyze with multiple agents.");
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                string body = endpoint.Requests[^1];
                Assert.Contains("Custom instructions retained.", body);
                Assert.AreEqual(expected, body.Contains("Subagents are AI workers", StringComparison.Ordinal));
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void SubagentCards_ShowRunningThenReplaceWithFinalResult()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var orchestrator = new SubagentOrchestrator(async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return new SubagentExecutionResult("disk analysis complete", [new("read_remote_file", 2)], []);
            }, CancellationToken.None);
            try
            {
                Type type = typeof(ChatPanelView).GetNestedType("AssistantBubble", BindingFlags.NonPublic)!;
                object bubble = Activator.CreateInstance(type, panel)!;
                var root = (Border)type.GetProperty("Root")!.GetValue(bubble)!;
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(root);
                MethodInfo update = typeof(ChatPanelView).GetMethod("UpdateSubagentCards",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                update.Invoke(panel, [bubble, orchestrator.Snapshots()]);
                Assert.IsFalse(root.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("noticeCard")));

                orchestrator.Start("inspect disk", "disk");
                Assert.IsTrue(await WaitForAsync(() => orchestrator.Snapshots().Single().Status == "running"));
                update.Invoke(panel, [bubble, orchestrator.Snapshots()]);
                await PumpAsync(2);
                Assert.IsTrue(root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("running") == true));

                release.SetResult();
                await orchestrator.WaitAsync(orchestrator.Snapshots().Single().SubagentId, 5, CancellationToken.None);
                update.Invoke(panel, [bubble, orchestrator.Snapshots()]);
                await PumpAsync(2);
                Assert.HasCount(1, root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("noticeCard")).ToList());
                Assert.IsTrue(root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("completed") == true));
                Assert.IsTrue(root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("read_remote_file") == true));
                Assert.IsTrue(root.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Text == "disk analysis complete"));
            }
            finally { release.TrySetResult(); panel.Detach(); window.Close(); }
        });
    }
}
