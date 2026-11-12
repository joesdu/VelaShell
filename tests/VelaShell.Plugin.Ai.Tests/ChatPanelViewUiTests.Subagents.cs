using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [GeneratedRegex(@"sub-[a-f0-9]{8}")]
    private static partial Regex SubagentIdRegex();

    [TestMethod]
    public void SubagentCards_FitNarrowPanelAndKeepErrorsAndReadingPosition()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                window.Width = 360;
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                for (int i = 0; i < 30; i++)
                    messages.Children.Add(new TextBlock { Text = $"line {i}", MinHeight = 40 });
                var bubble = new ChatPanelView.AssistantBubble(panel);
                messages.Children.Add(bubble.Root);
                MethodInfo update = typeof(ChatPanelView).GetMethod("UpdateSubagentCards",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                string error = "Permission denied: " + new string('x', 900);
                var snapshot = new SubagentSnapshot("sub-test", "review", "running", "", "",
                    [new("read_local_file", 3), new("list_local_directory", 2)], [], TimeSpan.FromSeconds(5),
                    TaskPrompt: string.Join(" ", Enumerable.Repeat("Inspect workspace files and summarize implementation risks.", 5)));
                update.Invoke(panel, [bubble, new[] { snapshot }]);
                await PumpAsync();
                Expander card = bubble.Root.GetVisualDescendants().OfType<Expander>()
                    .Single(e => e.Classes.Contains("subagentRun"));
                Assert.IsGreaterThan(0, card.Bounds.Width);
                Assert.IsLessThanOrEqualTo(bubble.Root.Bounds.Width, card.Bounds.Width);
                Assert.IsLessThanOrEqualTo(card.Bounds.Width, ((Control)card.Header!).Bounds.Width);
                Assert.Contains(t => t.Text?.Contains("5 tool calls") == true, card.GetVisualDescendants().OfType<TextBlock>());
                ScrollViewer scroll = Find<ScrollViewer>(panel, "ChatScroll");
                scroll.Offset = new Vector(0, 0);
                await PumpAsync();
                update.Invoke(panel, [bubble, new[] { snapshot with { Status = "failed", Errors = [error] } }]);
                await PumpAsync();
                Assert.AreEqual(0, scroll.Offset.Y, "后台终态更新不能把阅读位置拉回底部。");
                Assert.IsFalse(card.IsExpanded);
                Assert.Contains(t => t.Text?.Contains("Failed") == true && t.Text.Contains("Errors: 1"), card.GetVisualDescendants().OfType<TextBlock>());
                ToggleButton header = card.GetVisualDescendants().OfType<ToggleButton>()
                    .Single(t => t.Name == "PART_Header");
                header.IsChecked = true;
                await PumpAsync();
                Assert.IsTrue(card.IsExpanded, "自定义头部仍须切换原生 Expander 状态。");
                Assert.Contains(t => t.Text == error, card.GetVisualDescendants().OfType<SelectableTextBlock>());
                Assert.Contains(t => t.Text == snapshot.TaskPrompt, card.GetVisualDescendants().OfType<SelectableTextBlock>());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SubagentRefresh_PreservesStreamingMarkdownAcrossStatusUpdates(bool arrivesDuringReply)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                MethodInfo update = typeof(ChatPanelView).GetMethod("UpdateSubagentCards",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var snapshot = new SubagentSnapshot("sub-test", "analysis", "running", "", "", [], [], TimeSpan.Zero);
                if (!arrivesDuringReply)
                    update.Invoke(panel, [bubble, new[] { snapshot }]);
                string[] chunks =
                [
                    "| 路径 | 占用 |\n| --- | --- |\n| `/",
                    "var` | **14",
                    "G** |\n\n```text\nFailed to execute\nstatus=203/",
                    "EXEC\n```\n\n**端口",
                    "监听**\n"
                ];
                foreach (string chunk in chunks)
                {
                    bubble.AppendText(chunk);
                    update.Invoke(panel, [bubble, new[] { snapshot with { Elapsed = TimeSpan.FromSeconds(2) } }]);
                }
                update.Invoke(panel, [bubble, new[] { snapshot with { Status = "completed" } }]);
                await PumpAsync(2);
                MarkdownRenderer[] renderers = [.. bubble.Root.GetVisualDescendants().OfType<MarkdownRenderer>()];
                Assert.HasCount(1, renderers, "状态刷新不能把一条 Markdown 回复拆成多个渲染器。");
                MarkdownRenderer renderer = renderers[0];
                Assert.AreEqual(string.Concat(chunks), renderer.MarkdownBuilder!.ToString());
                Assert.AreEqual(string.Concat(chunks), bubble.ReplyText);
                Assert.IsTrue(await WaitForAsync(() =>
                    renderer.RenderedTextProjection?.SourceVersion == renderer.MarkdownBuilder.Version));
                Assert.HasCount(1, renderer.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("Table")).ToArray());
                Assert.HasCount(1, renderer.GetVisualDescendants().OfType<CodeBlock>().ToArray());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SubagentModelSelection_ChatDelegatesAndShowsSelectedModel(bool allowWrites)
    {
        OnUi(async () =>
        {
            // 子任务走流式请求(卡片上有实时正文,中途停下也留得住已写的部分)
            await using var childEndpoint = new SseStub(LifecycleAnswer.Replace("回答", "child analysis complete"));
            AiProvider child = StubProvider("child", childEndpoint.BaseUrl, "child-model");
            using var parentEndpoint = new KeyAuthStub((_, body) =>
            {
                using var document = JsonDocument.Parse(body);
                string messages = document.RootElement.GetProperty("messages").GetRawText();
                if (messages.Contains("\"subagent_wait\"", StringComparison.Ordinal)) return (200, LifecycleAnswer);
                Match id = SubagentIdRegex().Match(messages);
                return (200, id.Success
                    ? ImageToolCall("subagent_wait", JsonSerializer.Serialize(new { id = id.Value, timeoutSeconds = 5 }))
                    : ImageToolCall("delegate_subagent", JsonSerializer.Serialize(new
                    {
                        prompt = "Inspect the available read-only tools.",
                        role = "review",
                        modelId = child.Models[0].Id,
                        allowWrites
                    })));
            });
            using var context = new TestPluginContext();
            AiProvider parent = StubProvider("parent", parentEndpoint.BaseUrl, "parent-model");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [parent, child],
                ActiveModelId = parent.Models[0].Id,
                Mode = ChatMode.Agent,
                SuggestFollowUps = false
            });
            await store.SetApiKeyAsync(parent.Id, "parent-key");
            await store.SetApiKeyAsync(child.Id, "child-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("Delegate the review to child-model and wait for its result.");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1));
                Assert.HasCount(1, childEndpoint.Requests);
                Assert.AreEqual("Bearer child-key", childEndpoint.Authorizations.Single());
                using var request = JsonDocument.Parse(childEndpoint.Requests.Single());
                Assert.AreEqual("child-model", request.RootElement.GetProperty("model").GetString());
                Assert.IsTrue(request.RootElement.GetProperty("stream").GetBoolean());
                JsonElement[] childMessages = [.. request.RootElement.GetProperty("messages").EnumerateArray()];
                Assert.AreEqual("system", childMessages[0].GetProperty("role").GetString(), "子任务要带系统提示词");
                Assert.Contains("Your role: review.", childMessages[0].GetRawText());
                Assert.Contains(allowWrites ? "You may modify files" : "read-only", childMessages[0].GetRawText());
                Assert.Contains("Inspect the available read-only tools.", childMessages[1].GetRawText());
                string[] tools = [.. request.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];
                Assert.Contains("read_remote_file", tools);
                Assert.AreEqual(allowWrites, tools.Contains("write_remote_file"));
                Assert.AreEqual(allowWrites, tools.Contains("patch_remote_file"));
                Assert.DoesNotContain("write_local_file", tools);
                Assert.DoesNotContain("delegate_subagent", tools);
                Assert.DoesNotContain("run_command", tools);
                Expander childCard = messages.GetVisualDescendants().OfType<Expander>()
                    .Single(e => e.Classes.Contains("subagentRun"));
                Assert.IsFalse(childCard.IsExpanded);
                childCard.IsExpanded = true;
                await PumpAsync(2);
                Assert.Contains(r => r.MarkdownBuilder?.ToString() == "child analysis complete", childCard.GetVisualDescendants().OfType<MarkdownRenderer>());
                Assert.Contains(t => t.Text?.Contains("Completed") == true, messages.GetVisualDescendants().OfType<TextBlock>());
                Assert.Contains(t => t.Text?.Contains("child-model") == true, messages.GetVisualDescendants().OfType<TextBlock>());
                // 取结果的那次调用不另出工具卡(报告已在子任务卡片里);委派那次照出
                string[] toolTitles = [.. messages.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? "").Where(text => text.Contains("subagent", StringComparison.Ordinal))];
                Assert.Contains(text => text.Contains("delegate_subagent", StringComparison.Ordinal), toolTitles);
                Assert.DoesNotContain(text => text.Contains("subagent_wait", StringComparison.Ordinal), toolTitles);
                Assert.DoesNotContain(b => b.Name == "SubagentCancel" && b.IsVisible, messages.GetVisualDescendants().OfType<Button>(), "收尾之后卡片上不留取消按钮");
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
            AiProvider parent = StubProvider("parent", parentEndpoint.BaseUrl, "parent-model");
            AiProvider child = StubProvider("child", childEndpoint.BaseUrl, "child-model");
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
                (ProviderCredential credential, string? keyId) = await store.ResolveCredentialWithKeyIdAsync(parentModel);
                await using var orchestrator = new SubagentOrchestrator(async (request, token) =>
                {
                    (ResolvedModel selectedModel, ProviderCredential selectedCredential) = await panel.ResolveSubagentModelAsync(
                        request, parentModel, credential, keyId, token);
                    using IChatClient client = store.CreateClient(selectedModel, selectedCredential);
                    ChatResponse response = await client.GetResponseAsync(request.Prompt, cancellationToken: token);
                    return SubagentExecutionResult.FromText(response.Text);
                }, CancellationToken.None)
                { DefaultModelId = parentModel.Id };
                await using var toolbox = new AgentToolbox(context) { Subagents = orchestrator };
                AIFunction tool = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>()
                    .Single(t => t.Name == "delegate_subagent");
                var arguments = new AIFunctionArguments { ["prompt"] = "inspect configuration" };
                if (selectOther) arguments["modelId"] = child.Models[0].Id;
                await tool.InvokeAsync(arguments);
                string result = await orchestrator.WaitAsync(orchestrator.Snapshots().Single().SubagentId, 5, CancellationToken.None);
                using var json = JsonDocument.Parse(result);
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
                using var body = JsonDocument.Parse(target.Requests.Single());
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
            AiProvider provider = StubProvider("test", endpoint.BaseUrl, "m");
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
                (ProviderCredential credential, string? keyId) = await store.ResolveCredentialWithKeyIdAsync(parent);
                if (scenario == "changed-parent") await store.SetApiKeyAsync(provider.Id, "changed-key");
                await using var orchestrator = new SubagentOrchestrator(async (request, token) =>
                {
                    await panel.ResolveSubagentModelAsync(request, parent, credential, keyId, token);
                    return SubagentExecutionResult.FromText("unexpected");
                }, CancellationToken.None)
                { DefaultModelId = parent.Id };
                orchestrator.Start("inspect", null, scenario == "image" ? image.Id : scenario == "missing" ? "missing" : null);
                string result = await orchestrator.WaitAsync(orchestrator.Snapshots().Single().SubagentId, 5, CancellationToken.None);
                using var json = JsonDocument.Parse(result);
                Assert.AreEqual("failed", json.RootElement.GetProperty("status").GetString());
                Assert.IsGreaterThan(0, json.RootElement.GetProperty("errors").GetArrayLength());
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
            AiProvider provider = StubProvider("test", endpoint.BaseUrl, "m");
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

    /// <summary>
    /// 父模型委派完就停下、一次也不轮询:这一轮要等子任务跑完、把报告当通知送回去再让模型收尾,
    /// 而不是就此结束、把还在跑的子任务取消掉。
    /// </summary>
    [TestMethod]
    public void SubagentResults_AreDeliveredToAParentThatNeverPolls()
    {
        OnUi(async () =>
        {
            await using var childEndpoint = new SseStub(LifecycleAnswer.Replace("回答", "child report: disk is fine"));
            AiProvider child = StubProvider("child", childEndpoint.BaseUrl, "child-model");
            using var parentEndpoint = new KeyAuthStub((_, body) =>
            {
                using var document = JsonDocument.Parse(body);
                string messages = document.RootElement.GetProperty("messages").GetRawText();
                if (messages.Contains("untrusted-subagent-results", StringComparison.Ordinal))
                    return (200, LifecycleAnswer.Replace("回答", "Summary written."));
                // 回执里有了任务 id 就算委派过了;之后模型只说一句就停,不调任何等待工具
                return (200, SubagentIdRegex().IsMatch(messages)
                    ? LifecycleAnswer.Replace("回答", "Delegated.")
                    : ImageToolCall("delegate_subagent", JsonSerializer.Serialize(new
                    {
                        prompt = "Check the disk.",
                        modelId = child.Models[0].Id
                    })));
            });
            using var context = new TestPluginContext();
            AiProvider parent = StubProvider("parent", parentEndpoint.BaseUrl, "parent-model");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [parent, child],
                ActiveModelId = parent.Models[0].Id,
                Mode = ChatMode.Agent,
                SuggestFollowUps = false
            });
            await store.SetApiKeyAsync(parent.Id, "parent-key");
            await store.SetApiKeyAsync(child.Id, "child-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("Have a subagent check the disk.");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1));
                string[] chat = [.. parentEndpoint.Requests.Select(r => r.Body)
                    .Where(body => body.Contains("Have a subagent check the disk.", StringComparison.Ordinal))];
                Assert.HasCount(3, chat, "委派 → 模型停下 → 收到子任务通知后收尾");
                Assert.Contains("child report: disk is fine", chat[2]);
                Assert.Contains("\\\"status\\\":\\\"completed\\\"", chat[2]);
                Assert.HasCount(1, childEndpoint.Requests);
                Assert.Contains(t => t.Text?.Contains("Completed") == true, messages.GetVisualDescendants().OfType<TextBlock>(), "子任务应跑完,而不是随父轮结束被取消");
                Assert.Contains(r => r.MarkdownBuilder?.ToString().Contains("Summary written.") == true, messages.GetVisualDescendants().OfType<MarkdownRenderer>());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void SubagentCards_ShowActivityAndCancelOnlyWhileRunning()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                List<string> cancelled = [];
                bubble.CancelSubagent = cancelled.Add;
                var snapshot = new SubagentSnapshot("sub-test", "review", "running", "", "", [new("read_local_file", 1)], [],
                    TimeSpan.FromSeconds(3), TaskPrompt: "Review the config", Activity: "read_local_file · src/app.cs",
                    InputTokens: 12_400, OutputTokens: 380);
                bubble.UpdateSubagents([snapshot]);
                await PumpAsync(2);
                Expander card = bubble.Root.GetVisualDescendants().OfType<Expander>().Single(e => e.Classes.Contains("subagentRun"));
                TextBlock[] Texts() => [.. card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible)];
                Assert.Contains(t => t.Text == "read_local_file · src/app.cs", Texts(), "运行中要看得到此刻在读什么");
                Assert.Contains(t => t.Text?.Contains("tok ↑") == true && t.Text.Contains("380 tok ↓"), Texts());
                Button cancel = card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SubagentCancel");
                Assert.IsTrue(cancel.IsVisible);

                bubble.UpdateSubagents([snapshot with { Activity = SubagentOrchestrator.WaitingForApproval }]);
                await PumpAsync();
                Assert.Contains(t => t.Text == "Waiting for your approval", Texts());

                cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.AreSequenceEqual(["sub-test"], [.. cancelled]);
                Assert.IsFalse(card.IsExpanded, "点取消不该顺带展开卡片");

                bubble.UpdateSubagents([snapshot with { Status = "cancelled", Activity = "", Output = "half done" }]);
                await PumpAsync();
                Assert.IsFalse(cancel.IsVisible, "终态之后没有可取消的东西");
                Assert.DoesNotContain(t => t.Text == "Waiting for your approval", Texts());
                card.IsExpanded = true;
                await PumpAsync(2);
                Assert.Contains(t => t.Text?.StartsWith("Stopped early", StringComparison.Ordinal) == true, Texts(),
                    "提前停下时要说明下面只是已写出的部分");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void SubagentCards_SurviveHistoryAndHideDuplicateResultCards()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var live = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(live.Root);
                live.CancelSubagent = _ => { };
                live.AddToolCall("call-1", "delegate_subagent", "{\"prompt\":\"inspect\"}");
                live.UpdateSubagents(
                [
                    new SubagentSnapshot("sub-done", "disk", "completed", "", "**/var** is at 91%", [new("read_remote_file", 2)], [],
                        TimeSpan.FromSeconds(9), "model-a", "Check the disk"),
                    new SubagentSnapshot("sub-live", "logs", "running", "", "", [], [], TimeSpan.FromSeconds(4),
                        TaskPrompt: "Check the logs", Activity: "read_terminal")
                ]);
                live.AddToolCall("call-2", "subagent_wait", "{\"id\":\"sub-done\"}");
                live.AddToolCall("call-3", "subagent_status", "{\"id\":\"sub-live\"}");

                Chat.ChatTurnMeta meta = live.Snapshot("model-a", TimeSpan.FromSeconds(12));

                Assert.AreSequenceEqual(["delegate_subagent"], [.. meta.Tools!.Select(t => t.Name)],
                    "取结果的调用不另出工具卡,也就不进历史");
                Assert.AreSequenceEqual(["completed", "cancelled"], [.. meta.Subagents!.Select(s => s.Status)],
                    "入库时还没跑完的按已取消存:翻回历史时它们早就不在跑了");

                var restored = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(restored.Root);
                restored.Restore(meta);
                await PumpAsync(2);
                Expander[] cards = [.. restored.Root.GetVisualDescendants().OfType<Expander>().Where(e => e.Classes.Contains("subagentRun"))];
                Assert.HasCount(2, cards);
                Assert.DoesNotContain(b => b.Name == "SubagentCancel" && b.IsVisible, restored.Root.GetVisualDescendants().OfType<Button>());
                cards[0].IsExpanded = true;
                await PumpAsync(2);
                Assert.Contains(r => r.MarkdownBuilder?.ToString() == "**/var** is at 91%", cards[0].GetVisualDescendants().OfType<MarkdownRenderer>());
                Assert.Contains(t => t.Text == "Check the logs", restored.Root.GetVisualDescendants().OfType<TextBlock>());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ApprovalCard_NamesTheSubagentThatAskedForIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                MethodInfo add = typeof(ChatPanelView).GetMethod("AddApprovalCard", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var fromSubagent = new TaskCompletionSource<bool>();
                var fromParent = new TaskCompletionSource<bool>();
                add.Invoke(panel, [new ApprovalRequest("write_local_file", "notes.txt", Origin: "sub-1a2b3c4d · editor"), fromSubagent]);
                add.Invoke(panel, [new ApprovalRequest("write_local_file", "other.txt"), fromParent]);
                await PumpAsync(2);
                TextBlock[] origins = [.. panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ApprovalOrigin")];
                Assert.HasCount(1, origins, "主 Agent 自己发起的审批不标来源");
                Assert.AreEqual("Requested by subagent sub-1a2b3c4d · editor", origins[0].Text);
                fromSubagent.TrySetResult(false);
                fromParent.TrySetResult(false);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void SubagentCards_UpdateInPlaceAndPreserveExpandedResult()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string output = "**Disk analysis**\n\n" + new string('a', 1400) + "\n\ncomplete";
            await using var orchestrator = new SubagentOrchestrator(async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return new SubagentExecutionResult(output, [new("read_remote_file", 2)], []);
            }, CancellationToken.None)
            { DefaultModelId = "analysis-model" };
            try
            {
                Type type = typeof(ChatPanelView).GetNestedType("AssistantBubble", BindingFlags.NonPublic)!;
                object bubble = Activator.CreateInstance(type, panel)!;
                var root = (Border)type.GetProperty("Root")!.GetValue(bubble)!;
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(root);
                MethodInfo update = typeof(ChatPanelView).GetMethod("UpdateSubagentCards",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                update.Invoke(panel, [bubble, orchestrator.Snapshots()]);
                Assert.DoesNotContain(b => b.Classes.Contains("subagentGroup"), root.GetVisualDescendants().OfType<Border>());

                orchestrator.Start("inspect disk", "disk");
                Assert.IsTrue(await WaitForAsync(() => orchestrator.Snapshots().Single().Status == "running"));
                update.Invoke(panel, [bubble, orchestrator.Snapshots()]);
                await PumpAsync(2);
                Expander card = root.GetVisualDescendants().OfType<Expander>()
                    .Single(e => e.Classes.Contains("subagentRun"));
                Assert.IsFalse(card.IsExpanded);
                Assert.Contains(t => t.Text == "inspect disk", root.GetVisualDescendants().OfType<TextBlock>());
                Assert.Contains(t => t.Text?.Contains("Running") == true, root.GetVisualDescendants().OfType<TextBlock>());
                card.IsExpanded = true;
                await PumpAsync(2);
                Assert.Contains(t => t.Text?.Contains("analysis-model") == true, root.GetVisualDescendants().OfType<TextBlock>());

                release.SetResult();
                await orchestrator.WaitAsync(orchestrator.Snapshots().Single().SubagentId, 5, CancellationToken.None);
                update.Invoke(panel, [bubble, orchestrator.Snapshots()]);
                await PumpAsync(2);
                Assert.HasCount(1, root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("subagentGroup")).ToList());
                Assert.AreSame(card, root.GetVisualDescendants().OfType<Expander>().Single(e => e.Classes.Contains("subagentRun")));
                Assert.IsTrue(card.IsExpanded);
                Assert.Contains(t => t.Text?.Contains("Completed") == true, root.GetVisualDescendants().OfType<TextBlock>());
                Assert.Contains(t => t.Text?.Contains("read_remote_file") == true, root.GetVisualDescendants().OfType<TextBlock>());
                Assert.Contains(r => r.MarkdownBuilder?.ToString() == output, root.GetVisualDescendants().OfType<MarkdownRenderer>(),
                    "展开后应保留完整结果，不能截断为 1200 字符。");
            }
            finally { release.TrySetResult(); panel.Detach(); window.Close(); }
        });
    }
}
