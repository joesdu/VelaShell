using Avalonia.Controls;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.Plugin.Ai.Auth;
using System.Reflection;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    [DataRow("owned-key")]
    [DataRow("account")]
    [DataRow("display")]
    [DataRow("issuer")]
    [DataRow("exchange")]
    [DataRow("flow")]
    [DataRow("display-refresh")]
    [DataRow("parallel-refresh")]
    public void Review_FirstStationCredentialChangesDuringMcpCannotReceiveHistory(string change)
    {
        OnUi(async () =>
        {
            using var mcp = new SseStub("", hold: true);
            using var target = new SseStub(LifecycleAnswer);
            using var refreshHttp = new HttpClient(new OAuthStub().Json("{\"access_token\":\"new-token\",\"expires_in\":3600}"));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("account", target.BaseUrl, "m");
            bool subscription = change != "owned-key";
            if (subscription)
            {
                provider.Auth = AuthMethod.Subscription;
                provider.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
                if (change == "parallel-refresh")
                {
                    provider.OAuth.TokenUrl = "https://auth.example/token";
                    store.TokenClient = new OAuthClient(refreshHttp);
                }
                await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "old-token",
                    AccountId = change is "display" or "parallel-refresh" ? null : "same-account",
                    Account = change == "display" ? "same display name" : change == "display-refresh" ? "old display" : null,
                    RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            }
            else
            {
                provider.Models[0].HasOwnApiKey = true;
                await store.SetApiKeyAsync(provider.Models[0].Id, "old-key");
            }
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id, Mode = ChatMode.Agent,
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
                if (change == "parallel-refresh")
                {
                    (await store.GetTokensAsync(provider.Id))!.ExpiresAt = DateTimeOffset.UtcNow;
                    await store.ResolveProviderCredentialAsync(PanelSettings(panel).Providers[0]);
                }
                else if (subscription)
                {
                    if (change == "issuer") PanelSettings(panel).Providers[0].OAuth!.ClientId = "different-client";
                    if (change == "exchange") PanelSettings(panel).Providers[0].OAuth!.ExchangeUrl = "https://different.example/token";
                    if (change == "flow") PanelSettings(panel).Providers[0].OAuth!.Flow = OAuthFlow.GitHubCopilotDevice;
                    await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "new-token",
                        AccountId = change == "display" ? null : change == "account" ? "different-account" : "same-account",
                        Account = change == "display" ? "same display name" : change == "display-refresh" ? "new display" : null });
                }
                else await store.SetApiKeyAsync(provider.Models[0].Id, "new-key");
                mcp.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                if (change is "display-refresh" or "parallel-refresh")
                {
                    Assert.HasCount(1, target.Requests, "稳定账号 ID 不因显示名变化而失去合法续期");
                    Assert.AreEqual("Bearer new-token", target.Authorizations[0]);
                }
                else Assert.IsEmpty(target.Requests, "MCP 等待期间换凭据不能将旧轮次历史送给新身份");
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
            using var mcp = new SseStub("", hold: true);
            using var target = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("original", target.BaseUrl, "m");
            await store.SetApiKeyAsync(provider.Id, "key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id, Mode = ChatMode.Agent,
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
            using var other = new SseStub(LifecycleAnswer);
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
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
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
            using var mcp = new SseStub("", hold: true);
            using var target = new SseStub(LifecycleAnswer);
            using var otherEndpoint = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("original", target.BaseUrl, "m");
            await store.SetApiKeyAsync(provider.Id, "fixed-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id, Mode = ChatMode.Agent,
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
                if (requestAba) Assert.IsEmpty(target.Requests, "地址切回也不能恢复旧请求的发送权限");
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
    [DataRow("token")]
    [DataRow("issuer")]
    [DataRow("account")]
    public void Review_BackupOAuthAccountChangedDuringProbeCannotReceiveHistory(string change)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var target = new SseStub(LifecycleAnswer, hold: true);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider first = StubProvider("first", broken.BaseUrl, "m");
            AiProvider second = StubProvider("second", target.BaseUrl, "m");
            second.Auth = AuthMethod.Subscription;
            second.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
            await store.SaveTokensAsync(second.Id, new OAuthTokens { AccessToken = "old-token", AccountId = "old-account" });
            await store.SaveAsync(new AiSettings { Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("private-history");
                Assert.IsTrue(await WaitForAsync(() => target.Requests.Count == 1));
                if (change == "issuer") PanelSettings(panel).Providers[1].OAuth!.ClientId = "different-client";
                if (change != "issuer") await store.SaveTokensAsync(second.Id, new OAuthTokens
                {
                    AccessToken = change == "token" ? "new-token" : "old-token", AccountId = "new-account"
                });
                target.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                Assert.HasCount(1, target.Requests, "新账号不许获得第二次探活或旧轮次正式历史");
                Assert.AreEqual("Bearer old-token", target.Authorizations[0]);
                Assert.IsTrue(target.Requests.All(body => !body.Contains("private-history", StringComparison.Ordinal)),
                    "任何账号/issuer 变化后的请求都不能含旧轮次历史,不只检查 old-token");
                Assert.AreEqual(first.Models[0].Id, PanelSettings(panel).ActiveModelId);
                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId,
                    "身份失效的备用不能粘住为 active");
            }
            finally { target.Release(); panel.Detach(); window.Close(); }
        });
    }
    [TestMethod]
    [DataRow("renew")]
    [DataRow("account")]
    [DataRow("issuer")]
    [DataRow("key")]
    public void Review_ToolContinuationUsesRenewedStableAccountWithoutReplayingEffects(string change)
    {
        OnUi(async () =>
        {
            using var toolGate = new ManualResetEventSlim();
            var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var target = new HeldStreamingReply(toolBeforeFailure: true, successfulContinuation: true);
            using var backup = new SseStub(LifecycleAnswer);
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
            if (change == "key") await store.SetApiKeyAsync(provider.Id, "old-key");
            else
            {
                provider.Auth = AuthMethod.Subscription;
                provider.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
                await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "old-token", AccountId = "stable-account" });
            }
            await store.SaveAsync(new AiSettings { Providers = [provider, spare], ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent, Approval = ApprovalMode.Bypass, SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = spare.Models[0].Id }] });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-tool-history");
                Assert.IsTrue(await WaitForAsync(() => toolStarted.Task.IsCompleted));
                if (change == "key") await store.SetApiKeyAsync(provider.Id, "new-key");
                else
                {
                    if (change == "issuer") PanelSettings(panel).Providers[0].OAuth!.ClientId = "new-issuer";
                    await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "new-token",
                        AccountId = change == "account" ? "other-account" : "stable-account" });
                }
                toolGate.Set();
                target.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(1, context.FakeRemoteExec.Executed, "工具副作用只执行一次");
                Assert.IsEmpty(backup.Requests, "工具已执行后禁止故障重放,连备份探活都不做");
                Assert.AreEqual(change == "renew" ? 2 : 1, target.FormalRequests,
                    "同稳定账号续期须更新工具续流客户端;换身份或Key则禁止旧历史出境");
                if (change == "renew")
                {
                    Assert.Contains("executed-once", await target.ContinuationRequest);
                    CollectionAssert.AreEqual(new[] { "Bearer old-token", "Bearer new-token" }, target.Authorizations.ToArray());
                    Assert.AreEqual("备用站回答", AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Single().MarkdownBuilder.ToString());
                }
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
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
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
            await store.SaveAsync(new AiSettings { Providers = [first, second], ActiveModelId = first.Models[0].Id,
                SuggestFollowUps = false, FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }] });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-history");
                Assert.IsTrue(await WaitForAsync(() => candidate.Held.Task.IsCompleted));
                await store.SetApiKeyAsync(second.Id, "changed-key");
                if (cancel) Click(panel, "StopButton");
                candidate.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(1, candidate.Requests);
                Assert.DoesNotContain("private-history", candidate.Requests[0].Body);
                List<Border> errors = [.. Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants().OfType<Border>()
                    .Where(card => card.Classes.Contains("errorCard"))];
                if (cancel) Assert.IsEmpty(errors, "取消不应变成配置错误卡");
                else Assert.Contains("original-request-failure", errors.Single().GetVisualDescendants()
                    .OfType<SelectableTextBlock>().Single(block => block.Classes.Contains("mono")).Text ?? "",
                    "备用失效不能覆盖首站401/429的原始诊断");
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
            using var first = new SseStub(LifecycleAnswer);
            using var next = new SseStub(LifecycleAnswer);
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
                var persisted = (int)typeof(ChatPanelView).GetProperty("PersistedCount", flags)!.GetValue(panel)!;
                Assert.AreEqual(1, persisted, "插话只入库一次");
                object conversation = typeof(ChatPanelView).GetProperty("Cur", flags)!.GetValue(panel)!;
                typeof(ChatPanelView).GetMethod("OnSteeringDelivered", flags)!.Invoke(panel, [conversation, old]);
                await PumpAsync();
                Assert.HasCount(1, history, "旧通道迟到Post不能误提交新通道名单");
                var commitMethod = typeof(ChatPanelView).GetMethod("CommitSteeringAsync", flags)!;
                await (Task)commitMethod.Invoke(panel, [conversation])!;
                await (Task)commitMethod.Invoke(panel, [conversation])!;
                Assert.HasCount(2, history, "新通道自己的送达只提交一次");
                Assert.AreEqual(2, (int)typeof(ChatPanelView).GetProperty("PersistedCount", flags)!.GetValue(panel)!);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("key")]
    [DataRow("account")]
    [DataRow("issuer")]
    [DataRow("renew")]
    public void Review_CompactionRevalidatesFixedKeyAtActualSummaryBoundary(string change)
    {
        OnUi(async () =>
        {
            using var target = new SseStub(LifecycleAnswer, jsonContent: "摘要");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("fixed", target.BaseUrl, "m", maxInputTokens: 40);
            provider.Models[0].MaxTokens = 1;
            if (change == "key") await store.SetApiKeyAsync(provider.Id, "old-key");
            else
            {
                provider.Auth = AuthMethod.Subscription;
                provider.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
                await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "old-token", AccountId = "stable-account" });
            }
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
                if (change == "key") store.SetApiKeyAsync(provider.Id, "new-key").GetAwaiter().GetResult();
                else
                {
                    if (change == "issuer") PanelSettings(panel).Providers[0].OAuth!.ClientId = "new-issuer";
                    store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "new-token",
                        AccountId = change == "account" ? "other-account" : "stable-account" }).GetAwaiter().GetResult();
                }
            }
            status.PropertyChanged += BeforeSummary;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    panel.SendExternal("private-history-" + i);
                    int completed = i + 1;
                    Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == completed
                        && !Find<Button>(panel, "StopButton").IsVisible));
                }
                armed = true;
                panel.SendExternal("第五轮压缩必须在真正出境处再验");
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 5
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.IsTrue(changed, "已在装配复验后、实际摘要出境前同步换凭据");
                if (change == "renew")
                {
                    Assert.HasCount(6, target.Requests, "同账号合法续期可做一次摘要和一次正式回答");
                    Assert.AreEqual("Bearer new-token", target.Authorizations[4], "摘要本身必须使用续期后的客户端");
                    Assert.AreEqual("Bearer new-token", target.Authorizations[5]);
                }
                else Assert.HasCount(4, target.Requests, "固定Key或身份失效后裸摘要和正文都不得出境");
            }
            finally { status.PropertyChanged -= BeforeSummary; panel.Detach(); window.Close(); }
        });
    }

}
