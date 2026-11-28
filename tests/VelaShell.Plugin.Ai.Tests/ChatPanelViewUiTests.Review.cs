using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void Review_ModelKeyChangedDuringMcpCannotReceiveHistory()
    {
        OnUi(async () =>
        {
            await using var mcp = new SseStub("", hold: true);
            await using var target = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("account", target.BaseUrl, "m");
            provider.Models[0].HasOwnApiKey = true;
            await store.SetApiKeyAsync(provider.Models[0].Id, "old-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent,
                McpServers = [new McpServerConfig { Name = "held", Transport = McpTransportType.Http, Url = mcp.BaseUrl + "/mcp" }],
                SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("private-history");
                Assert.IsTrue(await WaitForAsync(() => mcp.RequestBodyAsync.IsCompleted));
                await store.SetApiKeyAsync(provider.Models[0].Id, "new-key");
                mcp.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                Assert.IsEmpty(target.Requests);
            }
            finally { mcp.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_ModelFormChangeDuringMcpRejectsOldParametersButNotDisplayName(bool requestChange)
    {
        OnUi(async () =>
        {
            await using var mcp = new SseStub("", hold: true);
            await using var target = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("original", target.BaseUrl, "m");
            await store.SetApiKeyAsync(provider.Id, "key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent,
                McpServers = [new McpServerConfig { Name = "held", Transport = McpTransportType.Http, Url = mcp.BaseUrl + "/mcp" }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-parameter-history");
                Assert.IsTrue(await WaitForAsync(() => mcp.RequestBodyAsync.IsCompleted));
                AiSettings live = PanelSettings(panel);
                AiModelConfig model = live.Providers[0].Models[0];
                string expected = System.Text.Json.JsonSerializer.Serialize(model);
                AiModelConfig draft = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(expected)!;
                if (requestChange) draft.Temperature = 0.7f; else draft.Name = "renamed";
                await store.SaveModelFormAsync(live, live.Providers[0], model, draft, expected, null, null);
                mcp.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                if (requestChange) Assert.IsEmpty(target.Requests, "旧装配参数不因凭据未变而获得发送权限");
                else Assert.HasCount(1, target.Requests, "显示名称变更不改变模型请求配置");
            }
            finally { mcp.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Review_KeyFailoverDoesNotReauthorizeProviderAbaDuringFirstRequest()
    {
        OnUi(async () =>
        {
            using var target = new KeyAuthStub((auth, _) => auth == "Bearer a" ? (401, "{\"error\":{\"message\":\"auth\"}}") : (200, LifecycleAnswer), "Bearer a", holdFirstRequest: true);
            await using var other = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", target.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.AddProviderApiKeyAsync(settings, provider, "a");
            await store.AddProviderApiKeyAsync(settings, provider, "b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-before-provider-aba");
                Assert.IsTrue(await WaitForAsync(() => target.Held.Task.IsCompleted));
                AiSettings live = PanelSettings(panel);
                var externalStore = new AiSettingsStore(context);
                AiSettings external = await externalStore.LoadAsync();
                external.Providers[0].BaseUrl = other.BaseUrl;
                await externalStore.SaveAsync(external); await store.ReloadIntoAsync(live);
                external = await externalStore.LoadAsync(); external.Providers[0].BaseUrl = target.BaseUrl;
                await externalStore.SaveAsync(external); await store.ReloadIntoAsync(live);
                target.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(1, target.Requests, "首请求已经发送,但不能给B槽重新授权旧历史,连探活都跳过");
                Assert.IsEmpty(other.Requests);
                Assert.IsNull(live.Providers[0].ActiveApiKeyId);
            }
            finally { target.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_ProviderReloadRejectsRequestConfigurationAbaButAllowsNameOnlyChange(bool requestAba)
    {
        OnUi(async () =>
        {
            await using var mcp = new SseStub("", hold: true);
            await using var target = new SseStub(LifecycleAnswer);
            await using var otherEndpoint = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("original", target.BaseUrl, "m");
            await store.SetApiKeyAsync(provider.Id, "fixed-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent,
                McpServers = [new McpServerConfig { Name = "held", Transport = McpTransportType.Http, Url = mcp.BaseUrl + "/mcp" }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-reload-history");
                Assert.IsTrue(await WaitForAsync(() => mcp.RequestBodyAsync.IsCompleted));
                AiSettings live = PanelSettings(panel);
                AiProvider originalInstance = live.Providers[0];
                var externalStore = new AiSettingsStore(context);
                AiSettings external = await externalStore.LoadAsync();
                if (requestAba) external.Providers[0].BaseUrl = otherEndpoint.BaseUrl;
                else external.Providers[0].Name = "renamed";
                await externalStore.SaveAsync(external);
                await store.ReloadIntoAsync(live);
                if (requestAba)
                {
                    external = await externalStore.LoadAsync();
                    external.Providers[0].BaseUrl = target.BaseUrl;
                    await externalStore.SaveAsync(external);
                    await store.ReloadIntoAsync(live);
                }
                Assert.AreSame(originalInstance, live.Providers[0]);
                mcp.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                Assert.IsEmpty(otherEndpoint.Requests, "新的目的地不接收旧回合历史");
                if (requestAba)
                {
                    Assert.IsEmpty(target.Requests, "地址切回也不能恢复旧请求的发送权限");
                }
                else
                {
                    Assert.HasCount(1, target.Requests);
                    Assert.Contains("private-reload-history", target.Requests[0], "纯名称更改不终止合法工具回合");
                }
            }
            finally { mcp.Release(); panel.Detach(); window.Close(); }
        });
    }
    [TestMethod]
    public void Review_KeyRotationStopsToolContinuationWithoutReplayingEffects()
    {
        OnUi(async () =>
        {
            using var toolGate = new ManualResetEventSlim();
            var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var target = new HeldStreamingReply(toolBeforeFailure: true, successfulContinuation: true);
            await using var backup = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            context.FakeSessions.AddConnected();
            context.FakeRemoteExec.Handler = (_, _) =>
            {
                toolStarted.TrySetResult();
                toolGate.Wait();
                return "executed-once";
            };
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("account", target.BaseUrl, "m");
            AiProvider spare = StubProvider("spare", backup.BaseUrl, "m");
            await store.SetApiKeyAsync(provider.Id, "old-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider, spare],
                ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent,
                Approval = ApprovalMode.Bypass,
                SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = spare.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-tool-history");
                Assert.IsTrue(await WaitForAsync(() => toolStarted.Task.IsCompleted));
                await store.SetApiKeyAsync(provider.Id, "new-key");
                toolGate.Set();
                target.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(1, context.FakeRemoteExec.Executed, "工具副作用只执行一次");
                Assert.IsEmpty(backup.Requests, "工具已执行后禁止故障重放,连备份探活都不做");
                Assert.AreEqual(1, target.FormalRequests);
                Assert.AreEqual(provider.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally { toolGate.Set(); target.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Review_KeySelectionChangedAwayAndBackCannotBeOverwrittenByOldTurn()
    {
        OnUi(async () =>
        {
            using var target = new KeyAuthStub((auth, _) => auth == "Bearer key-a"
                ? (401, "{\"error\":{\"message\":\"auth\"}}") : (200, LifecycleAnswer), "Bearer key-b");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", target.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("旧回合不能盖掉更新的主槽选择");
                Assert.IsTrue(await WaitForAsync(() => target.Held.Task.IsCompleted));
                AiProvider live = PanelSettings(panel).Providers[0];
                live.ActiveApiKeyId = second;
                await store.SaveAsync(PanelSettings(panel));
                live.ActiveApiKeyId = null;
                await store.SaveAsync(PanelSettings(panel));
                target.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(3, target.Requests, "固定槽仍可完成原回合");
                Assert.IsNull(live.ActiveApiKeyId, "ABA手选仍拥有选择权");
                Assert.IsNull((await store.LoadAsync()).Providers[0].ActiveApiKeyId);
            }
            finally { target.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(401, false)]
    [DataRow(429, false)]
    [DataRow(401, true)]
    public void Review_ExhaustedChangedCandidatesPreserveOriginalFailureOrCancellation(int status, bool cancel)
    {
        OnUi(async () =>
        {
            using var broken = new KeyAuthStub((_, _) => (status, "{\"error\":{\"message\":\"original-request-failure\"}}"));
            using var candidate = new KeyAuthStub((_, _) => (200, LifecycleAnswer), "Bearer candidate-key");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider first = StubProvider("first", broken.BaseUrl, "m");
            AiProvider second = StubProvider("second", candidate.BaseUrl, "m");
            await store.SetApiKeyAsync(first.Id, "first-key");
            await store.SetApiKeyAsync(second.Id, "candidate-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-history");
                Assert.IsTrue(await WaitForAsync(() => candidate.Held.Task.IsCompleted));
                await store.SetApiKeyAsync(second.Id, "changed-key");
                if (cancel) Click(panel, "SendButton");
                candidate.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(1, candidate.Requests);
                Assert.DoesNotContain("private-history", candidate.Requests[0].Body);
                List<Border> errors = [.. Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants().OfType<Border>()
                    .Where(card => card.Classes.Contains("errorCard"))];
                if (cancel)
                {
                    Assert.IsEmpty(errors, "取消不应变成配置错误卡");
                }
                else
                {
                    Assert.Contains("original-request-failure", errors.Single().GetVisualDescendants()
                    .OfType<SelectableTextBlock>().Single(block => block.Classes.Contains("mono")).Text ?? "",
                    "备用失效不能覆盖首站401/429的原始诊断");
                }

                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally { candidate.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Review_SteeringReplacementCommitsUndispatchedDeliveryExactlyOnce()
    {
        OnUi(async () =>
        {
            await using var first = new SseStub(LifecycleAnswer);
            await using var next = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("first", first.BaseUrl, "m");
            AiProvider backup = StubProvider("next", next.BaseUrl, "m");
            await store.SaveAsync(new AiSettings { Providers = [provider, backup], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var history = (List<ChatMessage>)typeof(ChatPanelView).GetProperty("History", flags)!.GetValue(panel)!;
                var queue = (SteeringQueue)typeof(ChatPanelView).GetProperty("SteeringQueue", flags)!.GetValue(panel)!;
                MethodInfo begin = typeof(ChatPanelView).GetMethod("BeginSteering", flags)!;
                using var old = new SteeringChatClient(store.CreateClient(PanelSettings(panel).FindModel(provider.Models[0].Id)!, ProviderCredential.Key(null)), queue);
                begin.Invoke(panel, [old]);
                queue.Enqueue(new SteeringMessage("UNDISPATCHED_STEERING", "UNDISPATCHED_STEERING", new ChatMessage(ChatRole.User, "UNDISPATCHED_STEERING")));
                await foreach (ChatResponseUpdate _ in old.GetStreamingResponseAsync([new ChatMessage(ChatRole.System, "system"), new ChatMessage(ChatRole.User, "original")])) { }
                Assert.HasCount(1, old.Delivered);
                Assert.IsEmpty(history, "模拟送达Post尚未执行,插话仅在旧通道里");
                using var replacement = new SteeringChatClient(store.CreateClient(PanelSettings(panel).FindModel(backup.Models[0].Id)!, ProviderCredential.Key(null)), queue);
                if (begin.Invoke(panel, [replacement]) is Task commit) await commit;
                Assert.HasCount(1, history, "换站必须先提交旧通道送达名单");
                if (begin.Invoke(panel, [replacement]) is Task repeated) await repeated;
                Assert.HasCount(1, history, "重复换流不能重复提交历史");
                queue.Enqueue(new SteeringMessage("NEXT_CHANNEL_STEERING", "NEXT_CHANNEL_STEERING", new ChatMessage(ChatRole.User, "NEXT_CHANNEL_STEERING")));
                await foreach (ChatResponseUpdate _ in replacement.GetStreamingResponseAsync(ContextBuilder.Build("system", history, 1000, 1).Messages)) { }
                Assert.Contains("UNDISPATCHED_STEERING", next.Requests.Single(), "备用正式请求应携带已送达插话");
                int persisted = (int)typeof(ChatPanelView).GetProperty("PersistedCount", flags)!.GetValue(panel)!;
                Assert.AreEqual(1, persisted, "插话只入库一次");
                object conversation = typeof(ChatPanelView).GetProperty("Cur", flags)!.GetValue(panel)!;
                typeof(ChatPanelView).GetMethod("OnSteeringDelivered", flags)!.Invoke(panel, [conversation, old]);
                await PumpAsync();
                Assert.HasCount(1, history, "旧通道迟到Post不能误提交新通道名单");
                MethodInfo commitMethod = typeof(ChatPanelView).GetMethod("CommitSteeringAsync", flags)!;
                await (Task)commitMethod.Invoke(panel, [conversation])!;
                await (Task)commitMethod.Invoke(panel, [conversation])!;
                Assert.HasCount(2, history, "新通道自己的送达只提交一次");
                Assert.AreEqual(2, (int)typeof(ChatPanelView).GetProperty("PersistedCount", flags)!.GetValue(panel)!);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Review_CompactionRevalidatesFixedKeyAtActualSummaryBoundary()
    {
        OnUi(async () =>
        {
            await using var target = new SseStub(LifecycleAnswer, jsonContent: "摘要");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("fixed", target.BaseUrl, "m", maxInputTokens: 40);
            provider.Models[0].MaxTokens = 1;
            await store.SetApiKeyAsync(provider.Id, "old-key");
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            await PumpAsync();
            bool changed = false;
            bool armed = false;
            TextBlock status = Find<TextBlock>(panel, "StatusText");
            void BeforeSummary(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
            {
                if (!armed || changed || e.Property != TextBlock.TextProperty || status.Text != new Loc("en")["Compacting"]) return;
                // 状态更新发生在真正摘要Task.Run之前;公开UI事件同步换凭据,不靠内部读取次数或延时抢跑。
                changed = true;
                store.SetApiKeyAsync(provider.Id, "new-key").GetAwaiter().GetResult();
            }
            status.PropertyChanged += BeforeSummary;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    panel.SendExternal("private-history-" + i);
                    int completed = i + 1;
                    Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == completed
                        && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                }
                armed = true;
                panel.SendExternal("第五轮压缩必须在真正出境处再验");
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 5
                    && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.IsTrue(changed, "已在装配复验后、实际摘要出境前同步换凭据");
                Assert.HasCount(4, target.Requests, "固定 Key 失效后摘要和正文都不得出境");
            }
            finally { status.PropertyChanged -= BeforeSummary; panel.Detach(); window.Close(); }
        });
    }
}
