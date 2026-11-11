using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void SubagentModelSelection_ChatDelegatesAndShowsSelectedModel()
    {
        OnUi(async () =>
        {
            await using var childEndpoint = new SseStub(LifecycleAnswer, jsonContent: "child analysis complete");
            var child = StubProvider("child", childEndpoint.BaseUrl, "child-model");
            using var parentEndpoint = new KeyAuthStub((_, body) =>
            {
                using JsonDocument document = JsonDocument.Parse(body);
                string messages = document.RootElement.GetProperty("messages").GetRawText();
                if (messages.Contains("\"subagent_wait\"", StringComparison.Ordinal)) return (200, LifecycleAnswer);
                var id = System.Text.RegularExpressions.Regex.Match(messages, @"sub-[a-f0-9]{8}");
                return (200, id.Success
                    ? ImageToolCall("subagent_wait", JsonSerializer.Serialize(new { id = id.Value, timeoutSeconds = 5 }))
                    : ImageToolCall("delegate_subagent", JsonSerializer.Serialize(new
                    {
                        prompt = "Inspect the available read-only tools.", role = "review", modelId = child.Models[0].Id
                    })));
            });
            using var context = new TestPluginContext();
            var parent = StubProvider("parent", parentEndpoint.BaseUrl, "parent-model");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [parent, child], ActiveModelId = parent.Models[0].Id,
                Mode = ChatMode.Agent, SuggestFollowUps = false
            });
            await store.SetApiKeyAsync(parent.Id, "parent-key");
            await store.SetApiKeyAsync(child.Id, "child-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("Delegate the review to child-model and wait for its result.");
                var messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1));
                Assert.HasCount(1, childEndpoint.Requests);
                Assert.AreEqual("Bearer child-key", childEndpoint.Authorizations.Single());
                using JsonDocument request = JsonDocument.Parse(childEndpoint.Requests.Single());
                Assert.AreEqual("child-model", request.RootElement.GetProperty("model").GetString());
                string[] tools = request.RootElement.GetProperty("tools").EnumerateArray()
                    .Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToArray();
                Assert.Contains("read_remote_file", tools);
                Assert.DoesNotContain("write_local_file", tools);
                Assert.DoesNotContain("delegate_subagent", tools);
                Assert.DoesNotContain("run_command", tools);
                Assert.IsTrue(messages.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Any(t => t.Text == "child analysis complete"));
                Assert.IsTrue(messages.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text?.Contains("completed") == true));
                Assert.IsTrue(messages.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text?.Contains("child-model") == true));
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SubagentModelSelection_RoutesWithItsOwnCredential(bool selectOther)
    {
        OnUi(async () =>
        {
            await using var parentEndpoint = new SseStub(LifecycleAnswer, jsonContent: "parent answer");
            await using var childEndpoint = new SseStub(LifecycleAnswer, jsonContent: "child answer");
            using var context = new TestPluginContext();
            var parent = StubProvider("parent", parentEndpoint.BaseUrl, "parent-model");
            var child = StubProvider("child", childEndpoint.BaseUrl, "child-model");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [parent, child],
                ActiveModelId = parent.Models[0].Id,
                SuggestFollowUps = false
            });
            await store.SetApiKeyAsync(parent.Id, "parent-key");
            await store.SetApiKeyAsync(child.Id, "child-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                AiSettings settings = await store.LoadAsync();
                ResolvedModel parentModel = settings.FindModel(parent.Models[0].Id)!;
                var parentCredential = await store.ResolveCredentialWithKeyIdAsync(parentModel);
                await using var orchestrator = new SubagentOrchestrator(async (request, token) =>
                {
                    var selected = await panel.ResolveSubagentModelAsync(
                        request, parentModel, parentCredential.Credential, parentCredential.KeyId, token);
                    using IChatClient client = store.CreateClient(selected.Model, selected.Credential);
                    ChatResponse response = await client.GetResponseAsync(request.Prompt, cancellationToken: token);
                    return SubagentExecutionResult.FromText(response.Text);
                }, CancellationToken.None) { DefaultModelId = parentModel.Id };
                await using var toolbox = new AgentToolbox(context) { Subagents = orchestrator };
                AIFunction tool = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>()
                    .Single(t => t.Name == "delegate_subagent");
                var arguments = new AIFunctionArguments { ["prompt"] = "inspect configuration" };
                if (selectOther) arguments["modelId"] = child.Models[0].Id;
                await tool.InvokeAsync(arguments);
                string result = await orchestrator.WaitAsync(orchestrator.Snapshots().Single().SubagentId, 5, CancellationToken.None);
                using JsonDocument json = JsonDocument.Parse(result);
                Assert.AreEqual("completed", json.RootElement.GetProperty("status").GetString(), result);
                Assert.AreEqual(selectOther ? child.Models[0].Id : parent.Models[0].Id,
                    json.RootElement.GetProperty("modelId").GetString());
                Assert.AreEqual(selectOther ? "child answer" : "parent answer",
                    json.RootElement.GetProperty("output").GetString());
                SseStub target = selectOther ? childEndpoint : parentEndpoint;
                SseStub unused = selectOther ? parentEndpoint : childEndpoint;
                Assert.HasCount(1, target.Requests);
                Assert.IsEmpty(unused.Requests);
                Assert.AreEqual(selectOther ? "Bearer child-key" : "Bearer parent-key", target.Authorizations.Single());
                using JsonDocument body = JsonDocument.Parse(target.Requests.Single());
                Assert.AreEqual(selectOther ? "child-model" : "parent-model", body.RootElement.GetProperty("model").GetString());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("image")]
    [DataRow("changed-parent")]
    public void SubagentModelSelection_InvalidSelectionFailsWithoutFallback(string scenario)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var provider = StubProvider("test", endpoint.BaseUrl, "m");
            var image = new AiModelConfig { Model = "image", Protocol = ChatProtocol.OpenAiImages };
            provider.Models.Add(image);
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            await store.SetApiKeyAsync(provider.Id, "original-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                AiSettings settings = await store.LoadAsync();
                ResolvedModel parent = settings.FindModel(provider.Models[0].Id)!;
                var credential = await store.ResolveCredentialWithKeyIdAsync(parent);
                if (scenario == "changed-parent") await store.SetApiKeyAsync(provider.Id, "changed-key");
                await using var orchestrator = new SubagentOrchestrator(async (request, token) =>
                {
                    await panel.ResolveSubagentModelAsync(request, parent, credential.Credential, credential.KeyId, token);
                    return SubagentExecutionResult.FromText("unexpected");
                }, CancellationToken.None) { DefaultModelId = parent.Id };
                orchestrator.Start("inspect", null, scenario == "image" ? image.Id : scenario == "missing" ? "missing" : null);
                string result = await orchestrator.WaitAsync(orchestrator.Snapshots().Single().SubagentId, 5, CancellationToken.None);
                using JsonDocument json = JsonDocument.Parse(result);
                Assert.AreEqual("failed", json.RootElement.GetProperty("status").GetString());
                Assert.IsTrue(json.RootElement.GetProperty("errors").GetArrayLength() > 0);
                Assert.IsEmpty(endpoint.Requests);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

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
            }, CancellationToken.None) { DefaultModelId = "analysis-model" };
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
                Assert.IsTrue(root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("analysis-model") == true));

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
