using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Storage;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 故障转移的两头:发送时真的切、全局设置里的有序列表真的编辑得了。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void KeyFailover_BalancingFailureDoesNotAdvanceCursorForProbeOrOverwritePreviousActive()
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-a"
                ? (401, "{\"error\":{\"message\":\"auth\"}}") : (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            provider.BalanceApiKeys = true;
            provider.ActiveApiKeyId = second;
            await store.SaveAsync(settings);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("首发坏 Key,然后探活健康 Key");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                CollectionAssert.AreEqual(new[] { "Bearer key-a", "Bearer key-b", "Bearer key-b" }, endpoint.Requests.Select(r => r.Auth).ToArray());
                Assert.AreEqual(second, (await store.LoadAsync()).Providers[0].ActiveApiKeyId);
                PanelHealth(panel).RecordKey(provider.Id, true);
                panel.SendExternal("探活 B 不能吞掉下一轮应使用的 B");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 2));
                Assert.AreEqual("Bearer key-b", endpoint.Requests[^1].Auth);
                Assert.HasCount(4, endpoint.Requests, "下一轮只发一次正式请求,探活没有推进首发游标");
                PanelHealth(panel).RecordKey(provider.Id, false);
                panel.SendExternal("轮转绕过当前冷却的 A");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 3));
                Assert.HasCount(5, endpoint.Requests);
                Assert.AreEqual("Bearer key-b", endpoint.Requests[^1].Auth);
                Assert.AreEqual(second, (await store.LoadAsync()).Providers[0].ActiveApiKeyId);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void KeyFailover_BalanceChangeDuringProbeAppliesOnlyToNextTurn(bool initiallyBalanced)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-a"
                ? (401, "{\"error\":{\"message\":\"auth\"}}") : (200, LifecycleAnswer), "Bearer key-b");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            provider.BalanceApiKeys = initiallyBalanced;
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("开跑时固定开关");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Held.Task.IsCompleted));
                AiSettings live = PanelSettings(panel);
                live.Providers[0].BalanceApiKeys = !initiallyBalanced;
                await store.SaveAsync(live);
                endpoint.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.AreEqual(initiallyBalanced ? null : second, (await store.LoadAsync()).Providers[0].ActiveApiKeyId,
                    "本轮是否粘住取决于开跑快照,不是探活返回时的新开关");
            }
            finally { endpoint.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(500)]
    [DataRow(200)]
    public void KeyFailover_NonKeyProbeFailureSkipsRemainingSlotsWithoutCoolingThem(int probeStatus)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-a"
                ? (401, "{\"error\":{\"message\":\"auth\"}}") : auth == "Bearer key-b"
                    ? (probeStatus, probeStatus == 200 ? "data: [DONE]\n\n" : "{\"error\":{\"message\":\"upstream\"}}")
                    : (200, LifecycleAnswer));
            using var backupEndpoint = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            AiProvider backup = StubProvider("backup", backupEndpoint.BaseUrl, "m");
            var settings = new AiSettings
            {
                Providers = [provider, backup], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = backup.Models[0].Id }]
            };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            string c = await store.AddProviderApiKeyAsync(settings, provider, "key-c");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("B 探活的模型故障不能误诊整个 Key 池");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                CollectionAssert.AreEqual(new[] { "Bearer key-a", "Bearer key-b" }, endpoint.Requests.Select(r => r.Auth).ToArray());
                Assert.HasCount(2, backupEndpoint.Requests);
                Assert.IsTrue(PanelHealth(panel).IsCooling(provider.Models[0].Id));
                Assert.IsTrue(PanelHealth(panel).IsKeyCooling(provider.Id));
                Assert.IsFalse(PanelHealth(panel).IsKeyCooling(b));
                Assert.IsFalse(PanelHealth(panel).IsKeyCooling(c));
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void KeyFailover_VisibleTextOrThinkingNeverReplaysAcrossKeys(bool thinking)
    {
        OnUi(async () =>
        {
            string delta = thinking ? "\"reasoning_content\":\"已经思考\"" : "\"content\":\"已经回答\"";
            string stream = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{" + delta
                + "},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n";
            using var endpoint = new SseStub(stream, abortAfterFirstChunk: true);
            using var backupEndpoint = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            AiProvider backup = StubProvider("backup", backupEndpoint.BaseUrl, "m");
            var settings = new AiSettings
            {
                Providers = [provider, backup], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = backup.Models[0].Id }]
            };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("已开口不能重放");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                await endpoint.FirstChunkFlushedAsync.WaitAsync(TimeSpan.FromSeconds(5));
                if (thinking)
                {
                    Assert.IsTrue(await WaitForAsync(() => ThinkingHeader(messages) is not null));
                    ClickHeader(messages);
                    Assert.IsTrue(await WaitForAsync(() => ThinkingBody(messages)?.IsVisible == true
                        && ThinkingText(messages).Contains("已经思考", StringComparison.Ordinal)),
                        "真实 UI 已展开并显示首段思考后才允许断流");
                }
                else
                {
                    Assert.IsTrue(await WaitForAsync(() => AnswerRenderers(messages).Any(renderer =>
                        renderer.MarkdownBuilder.ToString().Contains("已经回答", StringComparison.Ordinal))),
                        "必须由真实 UI 消费到正文,不能把服务端 Flush 当成 SDK 已解析");
                }
                Assert.IsTrue(Find<Button>(panel, "StopButton").IsVisible, "首事件之后流仍挂在显式闸门上");
                endpoint.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                Assert.HasCount(1, endpoint.Requests);
                Assert.AreEqual("Bearer key-a", endpoint.Authorizations[0]);
                Assert.IsEmpty(backupEndpoint.Requests);
            }
            finally { endpoint.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(429)]
    public void KeyFailover_AuthFailureUsesAnotherSlotBeforeChangingModelAndSticks(int status)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-a"
                ? (status, "{\"error\":{\"message\":\"invalid key\"}}") : (200, ProbeAccountingAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string secondId = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("同模型先换 Key");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                string[] expected = status == 429
                    ? ["Bearer key-a", "Bearer key-a", "Bearer key-b", "Bearer key-b"]
                    : ["Bearer key-a", "Bearer key-b", "Bearer key-b"];
                CollectionAssert.AreEqual(expected, endpoint.Requests.Select(r => r.Auth).ToArray());
                Assert.Contains("Reply with exactly:", endpoint.Requests[^2].Body, "下一槽固定凭据先探活");
                Assert.DoesNotContain("Reply with exactly:", endpoint.Requests[^1].Body, "探活后才正式发送同一条用户消息");
                AiSettings reloaded = await store.LoadAsync();
                Assert.HasCount(1, reloaded.Providers);
                Assert.AreEqual(provider.Models[0].Id, reloaded.ActiveModelId);
                Assert.AreEqual(secondId, reloaded.Providers[0].ActiveApiKeyId);
                ProviderHealth health = PanelHealth(panel);
                Assert.IsTrue(health.IsKeyCooling(provider.Id));
                Assert.IsFalse(health.IsKeyCooling(secondId));
                Assert.IsFalse(health.IsCooling(provider.Models[0].Id), "一把 Key 挂了不能连坐模型");
                Assert.Contains("Conversation total: in 22 / out 10", ToolTip.GetTip(Find<TextBlock>(panel, "UsageText")) as string ?? "",
                    "探活与正式回答的实际用量各入账一次");
                panel.SendExternal("下一轮粘住健康 Key");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 2));
                Assert.AreEqual(expected.Length + 1, endpoint.Requests.Count);
                Assert.AreEqual("Bearer key-b", endpoint.Requests[^1].Auth);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void KeyFailover_StickySlotCommitKeepsSuccessfulTurnsSuggestionsAuthorized()
    {
        OnUi(async () =>
        {
            const string suggestion = "{\"id\":\"suggest\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Next question?\"},\"finish_reason\":\"stop\"}]}";
            using var endpoint = new KeyAuthStub((auth, body) => auth == "Bearer key-a"
                ? (401, "{\"error\":{\"message\":\"invalid key\"}}")
                : (200, body.Contains("Suggest ", StringComparison.Ordinal) ? suggestion : ProbeAccountingAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = true };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("Answer then offer a next question");
                Assert.IsTrue(await WaitForAsync(() => Find<WrapPanel>(panel, "SuggestionBar").GetVisualDescendants()
                    .OfType<TextBlock>().Any(block => block.Text == "Next question?")));
                CollectionAssert.AreEqual(new[] { "Bearer key-a", "Bearer key-b", "Bearer key-b", "Bearer key-b" },
                    endpoint.Requests.Select(request => request.Auth).ToArray());
                Assert.Contains("Suggest ", endpoint.Requests[^1].Body);
                Assert.AreEqual(second, (await store.LoadAsync()).Providers[0].ActiveApiKeyId);
                Assert.Contains("Conversation total: in 22 / out 10", ToolTip.GetTip(Find<TextBlock>(panel, "UsageText")) as string ?? "");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void KeyFailover_OnlyAfterPoolExhaustionProbesBackupAndItsOtherSlots()
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-d" ? (200, LifecycleAnswer)
                : (401, "{\"error\":{\"message\":\"unauthorized\"}}"));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider first = StubProvider("first", endpoint.BaseUrl, "m1");
            AiProvider backup = StubProvider("backup", endpoint.BaseUrl, "m2");
            var settings = new AiSettings
            {
                Providers = [first, backup], ActiveModelId = first.Models[0].Id, SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = backup.Models[0].Id }]
            };
            await store.SetApiKeyAsync(first.Id, "key-a");
            await store.SetApiKeyAsync(backup.Id, "key-c");
            await store.SaveAsync(settings);
            string b = await store.AddProviderApiKeyAsync(settings, first, "key-b");
            string d = await store.AddProviderApiKeyAsync(settings, backup, "key-d");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("全池失败才换模型");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                CollectionAssert.AreEqual(new[] { "Bearer key-a", "Bearer key-b", "Bearer key-c", "Bearer key-d", "Bearer key-d" },
                    endpoint.Requests.Select(r => r.Auth).ToArray());
                ProviderHealth health = PanelHealth(panel);
                Assert.IsTrue(health.IsKeyCooling(first.Id));
                Assert.IsTrue(health.IsKeyCooling(b));
                Assert.IsTrue(health.IsCooling(first.Models[0].Id));
                Assert.IsTrue(health.IsKeyCooling(backup.Id));
                Assert.IsFalse(health.IsCooling(backup.Models[0].Id));
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual(backup.Models[0].Id, saved.ActiveModelId);
                Assert.AreEqual(d, saved.Providers[1].ActiveApiKeyId);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(404)]
    [DataRow(500)]
    [DataRow(200)]
    public void KeyFailover_NonKeyFailureNeverVisitsOtherSlots(int status)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (status, status == 200
                ? "data: [DONE]\n\n" : "{\"error\":{\"message\":\"model error\"}}"));
            using var backupEndpoint = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider first = StubProvider("pool", endpoint.BaseUrl, "m");
            AiProvider backup = StubProvider("backup", backupEndpoint.BaseUrl, "m");
            var settings = new AiSettings
            {
                Providers = [first, backup], ActiveModelId = first.Models[0].Id, SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = backup.Models[0].Id }]
            };
            await store.SetApiKeyAsync(first.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, first, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("错误分类不能因 Key 池扩大请求");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                Assert.IsTrue(endpoint.Requests.All(r => r.Auth == "Bearer key-a"));
                Assert.IsFalse(PanelHealth(panel).IsKeyCooling(first.Id));
                Assert.IsFalse(PanelHealth(panel).IsKeyCooling(second));
                if (status is 400 or 404) Assert.IsEmpty(backupEndpoint.Requests);
                else Assert.HasCount(2, backupEndpoint.Requests, "非 Key 故障沿原模型链,不逐 Key 多撞一遍");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void KeyFailover_BalancingRotatesOnlyFormalFirstSendsAndPreservesStickySlot()
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            provider.ActiveApiKeyId = second;
            provider.BalanceApiKeys = true;
            await store.SaveAsync(settings);
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                // 用户逐 Key 手测不推进聊天均摊游标。
                var manual = await store.ResolveCredentialWithKeyIdAsync(settings.FindModel(provider.Models[0].Id)!, keyId: second);
                Assert.IsNull((await HealthProbe.ProbeAsync(store, settings.FindModel(provider.Models[0].Id)!, manual.Credential)).Error);
                for (int turn = 1; turn <= 3; turn++)
                {
                    panel.SendExternal("均摊第" + turn + "轮");
                    int count = turn;
                    Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                        && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == count));
                }
                var formal = endpoint.Requests.Where(r => !r.Body.Contains("Reply with exactly:", StringComparison.Ordinal)).ToArray();
                CollectionAssert.AreEqual(new[] { "Bearer key-a", "Bearer key-b", "Bearer key-a" }, formal.Select(r => r.Auth).ToArray());
                Assert.AreEqual(4, endpoint.Requests.Count, "手测一次 + 三轮正式流,无额外付费探活");
                Assert.AreEqual(second, (await store.LoadAsync()).Providers[0].ActiveApiKeyId);
                AiSettings live = PanelSettings(panel);
                live.Providers[0].BalanceApiKeys = false;
                await store.SaveAsync(live); // 不清冷却,下一轮才读取新开关。
                panel.SendExternal("关闭均摊后回到原 active");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 4));
                Assert.AreEqual("Bearer key-b", endpoint.Requests[^1].Auth);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("replace")]
    [DataRow("remove")]
    [DataRow("cancel")]
    public void KeyFailover_ChangedOrCancelledProbedSlotNeverGetsFormalHistoryOrSticks(string change)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-a"
                ? (401, "{\"error\":{\"message\":\"auth\"}}") : (200, ProbeAccountingAnswer), "Bearer key-b");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("private-history-must-not-reach-changed-slot");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Held.Task.IsCompleted));
                Assert.Contains("Reply with exactly:", endpoint.Requests[^1].Body);
                AiSettings live = PanelSettings(panel);
                if (change == "replace") await store.ReplaceProviderApiKeyAsync(live, live.Providers[0], second, "key-new");
                else if (change == "remove")
                {
                    await store.RemoveProviderApiKeyAsync(live, live.Providers[0], second);
                    await store.AddProviderApiKeyAsync(live, live.Providers[0], "key-b");
                }
                else Click(panel, "StopButton");
                endpoint.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, endpoint.Requests, "已试的槽改值/删除,或用户取消后绝不隐式改用另一槽发送历史");
                Assert.IsNull((await store.LoadAsync()).Providers[0].ActiveApiKeyId);
                Assert.IsFalse(PanelHealth(panel).IsCooling(provider.Models[0].Id), "配置失效/取消不是模型失败证据");
                Assert.IsFalse(PanelHealth(panel).IsKeyCooling(second));
            }
            finally { endpoint.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void KeyFailover_ModelOwnedOrCrossOriginKeysNeverEnterProviderPool(bool owned)
    {
        OnUi(async () =>
        {
            using var home = new SseStub(LifecycleAnswer);
            using var endpoint = new KeyAuthStub((_, _) => (401, "{\"error\":{\"message\":\"auth\"}}"));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", owned ? endpoint.BaseUrl : home.BaseUrl, "m");
            provider.Models[0].BaseUrlOverride = endpoint.BaseUrl;
            provider.Models[0].HasOwnApiKey = owned;
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            if (owned) await store.SetApiKeyAsync(provider.Models[0].Id, "key-own");
            await store.SaveAsync(settings);
            await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("独立/跨源凭据不能借用其它 Key");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                Assert.HasCount(1, endpoint.Requests);
                Assert.AreEqual(owned ? "Bearer key-own" : "Bearer key-a", endpoint.Requests[0].Auth);
                Assert.IsEmpty(home.Requests);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 第一家 401 → 按链切到第二家,切换前先探活,通了就用它答完,
    /// 并且<b>粘住</b> —— ActiveModelId 改写并落盘,下次打开默认就是新那家。
    /// </summary>
    [TestMethod]
    public void FailoverChain_SwitchesOnAuthFailureAndSticksToTheNewProvider()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var healthy = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"来自第二家。"},"finish_reason":"stop"}]}

                data: [DONE]


                """,
                // 探活与正式回答都消费上面的 SSE。
                jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", healthy.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("随便说点什么");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0),
                    "第一家鉴权挂了,要能按链切到第二家把话说完");

                if (!await WaitForAsync(
                        () => Find<ComboBox>(panel, "ProviderCombo").SelectedIndex == 1, 60))
                {
                    AiSettings mid = await new AiSettingsStore(context).LoadAsync();
                    Assert.Fail($"诊断: combo={Find<ComboBox>(panel, "ProviderCombo").SelectedIndex}, " +
                        $"active={mid.ActiveModelId}, first={first.Models[0].Id}, second={second.Models[0].Id}, " +
                        $"brokenReq={broken.Requests}, healthyReq={healthy.Requests.Count}, " +
                        $"status={Find<TextBlock>(panel, "StatusText").Text}, footers={Footers(messages).Count}");
                }

                AiSettings reloaded = await new AiSettingsStore(context).LoadAsync();
                Assert.AreEqual(second.Models[0].Id, reloaded.ActiveModelId,
                    "粘住:成功那家落盘,下次默认走它");
                Assert.IsTrue(healthy.Requests.Count >= 2,
                    $"切换前探活一次 + 正式一轮都该打到第二家(实际 {healthy.Requests.Count})");
                Assert.IsTrue(broken.Requests >= 1, "第一家至少被试过一次");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void CandidateProbeUsage_AccumulatesOnceWithoutBecomingContextUsage()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var healthy = new SseStub(ProbeAccountingAnswer);
            var storage = new HeldSettingsStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider first = StubProvider("broken", broken.BaseUrl, "m");
            AiProvider second = StubProvider("backup", healthy.BaseUrl, "m", maxInputTokens: 100);
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            storage.TargetModelId = second.Models[0].Id;
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("探活也是真实计费");
                Assert.IsTrue(await WaitForAsync(() => storage.Started.Task.IsCompleted),
                    "候选探活已结束，正式流尚被粘住保存的闸门阻挡");
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                Assert.HasCount(1, healthy.Requests, "当前只有候选探活，没有正式流");
                Assert.AreEqual("↑11 ↓5", usage.Text, "探活只能累计，不能充当正文的上一轮上下文");
                Assert.Contains("Conversation total: in 11 / out 5", ToolTip.GetTip(usage) as string ?? "");
                storage.Release.TrySetResult();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, healthy.Requests, "一次探活与一次正式流");
                Assert.Contains("11/100", usage.Text ?? "", "占窗只取正式回复的输入");
                Assert.Contains("Conversation total: in 22 / out 10", ToolTip.GetTip(usage) as string ?? "",
                    "探活和正文各入账一次");
                Assert.AreEqual("回答", AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Single().MarkdownBuilder.ToString());
            }
            finally { storage.Release.TrySetResult(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedCandidateProbe_WithReceivedUsage_AccountsBeforeSwitching(bool disconnect)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            const string usageOnly = """
                data: {"id":"probe","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}

                data: [DONE]

                """;
            using var empty = disconnect ? null : new SseStub(usageOnly);
            // 探活只在结束时回调用量,没有首帧观察接口。完整首 HTTP chunk 后用 FIN 截断,
            // TCP 保证数据先于 EOF 到达,不会像 Flush 后 RST 那样丢掉尚未解析的用量。
            using var truncated = disconnect ? new TruncatedSseStub(usageOnly) : null;
            using var healthy = new SseStub(ProbeAccountingAnswer);
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("broken", broken.BaseUrl, "m");
            AiProvider second = StubProvider("empty", empty?.BaseUrl ?? truncated!.BaseUrl, "m");
            AiProvider third = StubProvider("backup", healthy.BaseUrl, "m", maxInputTokens: 100);
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second, third], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }, new FailoverEntry { ModelId = third.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("失败探活的用量不能丢");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.AreEqual(1, empty?.Requests.Count ?? truncated!.Requests, "无正文候选只探活,不发送正式流");
                Assert.HasCount(2, healthy.Requests, "下一站一次探活、一次正式回复");
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                Assert.Contains("11/100", usage.Text ?? "");
                Assert.Contains("Conversation total: in 29 / out 13", ToolTip.GetTip(usage) as string ?? "",
                    "失败候选 7/3 + 下一站探活 11/5 + 正式流 11/5，均不得重复或丢失");
                Assert.AreEqual("回答", AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Single().MarkdownBuilder.ToString());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CandidateProbe_LateUsageStaysWithOriginConversation(bool reset)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var healthy = new SseStub(ProbeAccountingAnswer, holdFirstStream: true);
            using var context = new TestPluginContext();
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            AiProvider first = StubProvider("broken", broken.BaseUrl, "m");
            AiProvider second = StubProvider("backup", healthy.BaseUrl, "m", maxInputTokens: 100);
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("探活挂起后切会话");
                StackPanel original = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => healthy.Requests.Count == 1));
                ComboBox sessions = Find<ComboBox>(panel, "SessionCombo");
                if (reset)
                {
                    Click(panel, "NewChatButton");
                    Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible),
                        "先让旧探活的取消收尾，避免把新消息排进旧轮次");
                    Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 1;
                    panel.SendExternal("新会话自己的用量");
                    Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                        && Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                }
                else sessions.SelectedIndex = 1;
                healthy.Release();
                Assert.IsTrue(await WaitForAsync(() => reset
                    ? !Find<Button>(panel, "StopButton").IsVisible
                    : Footers(original).Count == 1));
                await PumpAsync();
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                if (reset)
                {
                    Assert.Contains("11/100", usage.Text ?? "");
                    Assert.Contains("Conversation total: in 11 / out 5", ToolTip.GetTip(usage) as string ?? "",
                        "迟到的探活不能污染已重置会话的新一轮真实用量");
                }
                else
                {
                    Assert.AreEqual("", usage.Text, "迟到的候选探活不能写入另一前台会话");
                    Assert.DoesNotContain("Conversation total:", ToolTip.GetTip(usage) as string ?? "");
                }
                if (!reset)
                {
                    sessions.SelectedIndex = 0;
                    Assert.Contains("11/100", usage.Text ?? "");
                    Assert.Contains("Conversation total: in 22 / out 10", ToolTip.GetTip(usage) as string ?? "",
                        "切回发起会话，必须看到探活和正式流各一次的真实累计");
                }
                else Assert.HasCount(2, healthy.Requests, "重置取消旧探活，备用站只有旧探活与新会话的正式流");
            }
            finally { healthy.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void FixedCredentialProbe_ReportsUsageOnCallerUiContext()
    {
        OnUi(async () =>
        {
            using var endpoint = new SseStub(ProbeAccountingAnswer);
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("probe", endpoint.BaseUrl, "m");
            var model = new ResolvedModel(provider, provider.Models[0]);
            SynchronizationContext? callerContext = SynchronizationContext.Current;
            int callerThread = Environment.CurrentManagedThreadId;
            int calls = 0;
            (Exception? error, _) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), model,
                ProviderCredential.Key(null), usageCallback: usage =>
                {
                    calls++;
                    Assert.IsTrue(Avalonia.Threading.Dispatcher.UIThread.CheckAccess(), "不能从 SSE 线程池直接调用 UI 消费者");
                    Assert.AreEqual(callerThread, Environment.CurrentManagedThreadId);
                    Assert.AreSame(callerContext, SynchronizationContext.Current);
                    Assert.AreEqual(11L, usage.InputTokenCount);
                    Assert.AreEqual(5L, usage.OutputTokenCount);
                });
            Assert.IsNull(error, error?.ToString());
            Assert.AreEqual(1, calls);
        });
    }

    private const string ProbeAccountingAnswer = """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"回答"},"finish_reason":"stop"}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":11,"completion_tokens":5,"total_tokens":16}}

        data: [DONE]

        """;

    /// <summary>备用站已经收到 m1 流式请求后改成 m2,收尾和历史都仍要标实际发送的 m1。</summary>
    [TestMethod]
    public void FailoverReply_ModelEditedAfterStreamRequest_RecordsTheSentModel()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var target = new HeldStreamingReply();
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "a");
            AiProvider second = StubProvider("beta", target.BaseUrl, "m1");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("核对实际请求型号");
                Assert.IsTrue(await WaitForAsync(() => target.StreamRequest.IsCompleted),
                    "备用站的探活已返回,真正的 SSE 请求已到达且正挂住");
                Assert.Contains("\"model\":\"m1\"", await target.StreamRequest);

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(settings);
                await PumpAsync(10);
                FindIn<ListBox>(settings, "ProvidersList").SelectedIndex = 3;
                await PumpAsync(10);
                FindIn<TextBox>(settings, "ModelBox").Text = "m2";
                RaiseClick(FindIn<Button>(settings, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(settings, "StatusText").Text == "Saved."));
                Assert.AreEqual("m2", (await store.LoadAsync()).Providers[1].Models[0].Model);

                target.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0), "挂起的 SSE 放行后回复已收尾");
                string footer = string.Join("|", Footers(messages)[^1].GetVisualDescendants()
                    .OfType<TextBlock>().Select(block => block.Text ?? ""));
                Assert.Contains("m1", footer, "页脚应记录真正发出的型号");
                Assert.DoesNotContain("m2", footer, "设置页的后改型号不是本轮使用的型号");

                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                ChatSessionSummary session = (await history.ListSessionsAsync()).Single(s => s.Title == "核对实际请求型号");
                ChatEntry answer = (await history.LoadAsync(session.Id)).Single(entry => entry.Role == "assistant");
                Assert.AreEqual("备用站回答", answer.Text);
                Assert.AreEqual("m1", answer.Meta?.Model, "回放的历史元数据应记录真正发出的型号");
            }
            finally
            {
                target.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void FailedCandidateProbe_CoolsTheModelForTheNextTurn()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var unprobeable = new ErrorStub();
            using var answering = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"已接管"},"finish_reason":"stop"}]}

                data: [DONE]

                """, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", unprobeable.BaseUrl, "m");
            AiProvider c = StubProvider("c", answering.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [a, b, c], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id },
                                 new FailoverEntry { ModelId = c.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("第一次");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1));
                Assert.AreEqual(1, unprobeable.Requests, "首次跳过坏候选前确实探测过它");
                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 0;
                panel.SendExternal("第二次");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2));
                Assert.AreEqual(1, unprobeable.Requests, "候选探活失败必须进入冷却,不能下一轮又探一次");
                Assert.HasCount(4, answering.Requests, "每轮 C 只收到一次探活与一次正式请求");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ManualSelectionBeforeFailure_EvenWhenReturnedToOriginal_DoesNotGetStuckToBackup()
    {
        OnUi(async () =>
        {
            using var broken = new SseStub("", hold: true);
            using var answering = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"备用回答"},"finish_reason":"stop"}]}

                data: [DONE]

                """, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", answering.BaseUrl, "m");
            AiProvider c = StubProvider("c", "http://127.0.0.1:1/v1", "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b, c], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox selector = Find<ComboBox>(panel, "ProviderCombo");
                panel.SendExternal("会话刚开始");
                Assert.IsTrue(await WaitForAsync(() => broken.Requests.Count == 1), "首站正式请求尚未返回错误");
                selector.SelectedIndex = 2;
                selector.SelectedIndex = 0;
                broken.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1
                    && answering.Requests.Count >= 2), "失败后仍由备用模型完成回答");
                Assert.AreEqual(a.Models[0].Id, (await store.LoadAsync()).ActiveModelId,
                    "A→C→A 发生在错误之前,不能被旧轮次粘住成 B");
                Assert.AreEqual(0, selector.SelectedIndex);
                Assert.Contains("\"stream\":true", answering.Requests[^1]);
            }
            finally
            {
                broken.Release();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ConcurrentFailover_AutomaticAbaSelection_IsNotRolledBackByOlderCancellation()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var heldB = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

                data: [DONE]


                """, holdAfterFirstChunk: true, firstStreamContent: "OK");
            using var healthyB = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var healthyC = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var context = new TestPluginContext();
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.3", username: "root");
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", heldB.BaseUrl, "m");
            AiProvider c = StubProvider("c", healthyC.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b, c], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }, new FailoverEntry { ModelId = c.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? settingsHost = null;
            try
            {
                ComboBox sessions = Find<ComboBox>(panel, "SessionCombo");
                ComboBox models = Find<ComboBox>(panel, "ProviderCombo");
                panel.SendExternal("对话0等待B");
                Assert.IsTrue(await WaitForAsync(() => heldB.Requests.Count == 2 && models.SelectedIndex == 1));
                Assert.Contains("\"stream\":true", heldB.Requests[0]);
                Assert.Contains("\"stream\":true", heldB.Requests[1]);
                Assert.IsTrue(Find<Button>(panel, "StopButton").IsVisible, "B 已探通，正式流在无正文 role 帧后挂起");

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = Host(editor);
                await PumpAsync(10);
                async Task SaveAddressAsync(int row, string address)
                {
                    FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = row;
                    await PumpAsync(10);
                    FindIn<TextBox>(editor, "ProviderBaseUrlBox").Text = address;
                    FindIn<TextBlock>(editor, "StatusText").Text = "";
                    RaiseClick(FindIn<Button>(editor, "SaveButton"));
                    Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                }

                await SaveAddressAsync(2, broken.BaseUrl); // B 变坏；真实保存通知清掉旧健康缓存
                sessions.SelectedIndex = 1;
                panel.SendExternal("对话1由C回答");
                Assert.IsTrue(await WaitForAsync(() => models.SelectedIndex == 2 && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, healthyC.Requests, "对话1确实探活C并由C正式回答");
                Assert.AreEqual("回答", AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Single().MarkdownBuilder.ToString());

                await SaveAddressAsync(2, healthyB.BaseUrl);
                await SaveAddressAsync(4, broken.BaseUrl); // C 变坏；B 重新健康且清掉失败冷却
                sessions.SelectedIndex = 2;
                panel.SendExternal("对话2由B回答");
                Assert.IsTrue(await WaitForAsync(() => models.SelectedIndex == 1 && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, healthyB.Requests, "对话2确实探活B并由B正式回答");
                Assert.AreEqual("回答", AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Single().MarkdownBuilder.ToString());

                sessions.SelectedIndex = 0;
                Assert.IsTrue(Find<Button>(panel, "StopButton").IsVisible, "旧对话0仍在原B端点挂起");
                Click(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId,
                    "较新的自动B→C→B拥有选择，旧A→B取消不得回滚成A");
                Assert.AreEqual(1, models.SelectedIndex);
            }
            finally { heldB.Release(); settingsHost?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void BackgroundStickySwitch_RecomputesVisibleConversationUsageWithNewWindow()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var answering = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"后台回答"},"finish_reason":"stop"}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":80,"completion_tokens":2,"total_tokens":82}}

                data: [DONE]

                """, holdFirstStream: true, firstStreamContent: "OK", skipFirstStreams: 2);
            var storage = new HeldSettingsStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            AiProvider a = StubProvider("a", broken.BaseUrl, "m", maxInputTokens: 100);
            AiProvider b = StubProvider("b", answering.BaseUrl, "m", maxInputTokens: 200);
            AiProvider c = StubProvider("c", answering.BaseUrl, "m", maxInputTokens: 300);
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b, c], ActiveModelId = b.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                // 前台先由 B 计费,再手动切到 A:相同统计先按 A 的窗口展示。
                ComboBox sessions = Find<ComboBox>(panel, "SessionCombo");
                ComboBox models = Find<ComboBox>(panel, "ProviderCombo");
                panel.SendExternal("前台用量");
                StackPanel visible = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(visible).Count == 1));
                panel.SendExternal("前台第二轮");
                Assert.IsTrue(await WaitForAsync(() => Footers(visible).Count == 2));
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                models.SelectedIndex = 0;
                Assert.Contains("80/100", usage.Text ?? "", "前台先按 A 的窗口计算");
                sessions.SelectedIndex = 1;
                storage.TargetModelId = b.Models[0].Id;
                panel.SendExternal("后台切站");
                StackPanel background = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => answering.Requests.Count == 3), "备用探活挂在真实 HTTP 端");
                sessions.SelectedIndex = 0;
                answering.Release();
                Assert.IsTrue(await WaitForAsync(() => storage.Started.Task.IsCompleted), "粘住保存正被存储闸门挂住");
                Assert.AreEqual(1, models.SelectedIndex, "保存未完成时下拉也须已切到 B");
                Assert.Contains("80/200", usage.Text ?? "", "保存未完成时前台须已按 B 的窗口重算");
                Assert.Contains("Conversation total: in 160 / out 4", ToolTip.GetTip(usage) as string ?? "",
                    "后台自己的统计不可写进前台");
                Assert.HasCount(2, Footers(visible), "后台的回答不可写入前台气泡");

                int brokenBefore = broken.Requests;
                int healthyBefore = answering.Requests.Count;
                panel.SendExternal("保存挂住时前台直接走 B");
                Assert.IsTrue(await WaitForAsync(() => Footers(visible).Count == 3));
                Assert.AreEqual(brokenBefore, broken.Requests, "前台第一请求不得再撞旧站 A");
                Assert.HasCount(healthyBefore + 1, answering.Requests, "前台只向 B 发正式请求,无额外故障转移探活");
                Assert.Contains("\"stream\":true", answering.Requests[^1]);

                models.SelectedIndex = 2;
                storage.Release.TrySetResult();
                Assert.IsTrue(await WaitForAsync(() => Footers(background).Count == 1), "保存放行后后台独立答完");
                Assert.AreEqual(2, models.SelectedIndex, "旧保存回调不得重放 B 覆盖用户的新选择 C");
                Assert.AreEqual(c.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.Contains("80/300", usage.Text ?? "");
                Assert.Contains("Conversation total: in 240 / out 6", ToolTip.GetTip(usage) as string ?? "",
                    "后台一轮的统计不可盖掉前台三轮的累计");
                Assert.HasCount(3, Footers(visible));
                models.SelectedIndex = 1;
                sessions.SelectedIndex = 1;
                Assert.Contains("80/200", usage.Text ?? "", "切回后台应显示它自己的用量和新窗口");
                Assert.Contains("Conversation total: in 80 / out 2", ToolTip.GetTip(usage) as string ?? "",
                    "后台独立记账,切回它才显示自己的累计");
            }
            finally
            {
                answering.Release();
                storage.Release.TrySetResult();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void BackgroundFailoverRollback_SynchronizesVisibleSelectionBeforeSaveAndPreservesNewSelection()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var heldBackup = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

                data: [DONE]

                """, holdAfterFirstChunk: true, firstStreamContent: "OK");
            using var answering = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"前台回答"},"finish_reason":"stop"}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":80,"completion_tokens":2,"total_tokens":82}}

                data: [DONE]

                """);
            var storage = new HeldSettingsStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            AiProvider a = StubProvider("a", broken.BaseUrl, "m", maxInputTokens: 100);
            AiProvider b = StubProvider("b", heldBackup.BaseUrl, "m", maxInputTokens: 200);
            AiProvider c = StubProvider("c", answering.BaseUrl, "m", maxInputTokens: 300);
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b, c], ActiveModelId = c.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox sessions = Find<ComboBox>(panel, "SessionCombo");
                ComboBox models = Find<ComboBox>(panel, "ProviderCombo");
                panel.SendExternal("前台用量");
                StackPanel visible = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(visible).Count == 1));
                models.SelectedIndex = 0;
                sessions.SelectedIndex = 1;
                panel.SendExternal("备用站等待中取消");
                StackPanel background = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => heldBackup.Requests.Count == 2 && models.SelectedIndex == 1));
                sessions.SelectedIndex = 0;
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                Assert.Contains("80/200", usage.Text ?? "");

                storage.TargetModelId = a.Models[0].Id;
                sessions.SelectedIndex = 1;
                Click(panel, "StopButton");
                sessions.SelectedIndex = 0;
                Assert.IsTrue(await WaitForAsync(() => storage.Started.Task.IsCompleted), "取消后的回滚保存已挂住");
                Assert.AreEqual(0, models.SelectedIndex, "回滚保存尚未完成也须已显示原站 A");
                Assert.Contains("80/100", usage.Text ?? "", "回滚须按前台原用量及 A 的窗口重算");

                models.SelectedIndex = 2;
                storage.Release.TrySetResult();
                Assert.IsTrue(await WaitForAsync(() => Footers(background).Count == 1));
                Assert.AreEqual(2, models.SelectedIndex, "迟到回滚保存不得重放原站覆盖新选择 C");
                Assert.AreEqual(c.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.Contains("80/300", usage.Text ?? "");
                Assert.Contains("Conversation total: in 80 / out 2", ToolTip.GetTip(usage) as string ?? "");
            }
            finally
            {
                storage.Release.TrySetResult();
                heldBackup.Release();
                panel.Detach();
                window.Close();
            }
        });
    }

    private sealed class HeldSettingsStorage(IPluginStorage inner) : IPluginStorage
    {
        public string? TargetModelId { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
            => inner.GetAsync<T>(key, cancellationToken);

        public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            if (key == "settings" && TargetModelId is not null
                && System.Text.Json.JsonSerializer.SerializeToElement(value).GetProperty(nameof(AiSettings.ActiveModelId)).GetString() == TargetModelId
                && !Started.Task.IsCompleted)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await inner.SetAsync(key, value, cancellationToken);
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default)
            => inner.GetKeysAsync(cancellationToken);
    }


    /// <summary>同模型的旧探活迟到失败,不得盖过另一会话稍后发出且已答通的正式请求。</summary>
    [TestMethod]
    public void ConcurrentConversations_LateProbeFailureDoesNotCoolAnsweredModel()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var target = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"回答成功"},"finish_reason":"stop"}]}

                data: [DONE]


                """, holdFirstStream: true, firstStreamContent: "");
            using var context = new TestPluginContext();
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", target.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox sessions = Find<ComboBox>(panel, "SessionCombo");
                ComboBox models = Find<ComboBox>(panel, "ProviderCombo");
                panel.SendExternal("首会话:故障转移探活");
                Assert.IsTrue(await WaitForAsync(() => target.Requests.Count == 1), "旧探活已在真实 HTTP 端挂起");
                Assert.Contains("\"stream\":true", target.Requests[0]);

                sessions.SelectedIndex = 1;
                StackPanel secondMessages = Find<StackPanel>(panel, "MessagesPanel");
                models.SelectedIndex = 1;
                panel.SendExternal("第二会话:模型正常回答");
                Assert.IsTrue(await WaitForAsync(() => Footers(secondMessages).Count > 0), "较新的正式请求已经答通");
                Assert.HasCount(2, target.Requests);
                Assert.Contains("\"stream\":true", target.Requests[1]);

                target.Release(); // 旧探活现在才返回空回复;不能给已答通的模型降入冷却
                sessions.SelectedIndex = 0;
                StackPanel firstMessages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(firstMessages).Count > 0));
                sessions.SelectedIndex = 1;
                models.SelectedIndex = 0;
                panel.SendExternal("再撞坏站时应仍能探测正常模型");
                Assert.IsTrue(await WaitForAsync(() => Footers(secondMessages).Count > 1),
                    "旧探活失败不应让备用模型被跳过");
                Assert.HasCount(4, target.Requests, "新一轮仍须先探活,再由可用模型正式回答");
                Assert.Contains("\"stream\":true", target.Requests[2]);
                Assert.Contains("\"stream\":true", target.Requests[3]);
            }
            finally
            {
                target.Release();
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 换站装配的两件事:① 系统提示词按<b>当前这一站</b>取 —— 界面选择还(或已被用户改回)
    /// 指着 A 时,B 收到的必须是 B 自己的提示词,不是 A 的(跨接入泄露);
    /// ② 探活那 15 秒里用户动过的选择,粘住不许盖 —— 这一轮照切去能用的下一站,但不落盘。
    /// 选择"改到 C"发生在探活返回之前,靠 <c>SseStub(hold)</c> 挂住探测响应来卡窗口,
    /// 不靠 sleep 赌时序。
    /// </summary>
    [TestMethod]
    public void FailoverChain_UsesCurrentStationsPromptAndNeverOverridesSelectionChangedDuringProbe()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var healthy = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"来自第二家。"},"finish_reason":"stop"}]}

                data: [DONE]


                """,
                jsonContent: "OK",
                hold: true);
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", healthy.BaseUrl, "m");
            second.Models[0].SystemPrompt = "PROMPT_B";
            AiProvider third = StubProvider("gamma", "http://127.0.0.1:1/v1", "m");
            third.Models[0].SystemPrompt = "PROMPT_C";
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second, third],
                ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("随便说点什么");
                // 等切换前探活真的挂在第二家端点上(hold:不 Release 就不回)——
                // 这之后、探活返回之前做什么都赶得上,窗口是确定的
                Assert.IsTrue(await WaitForAsync(() => healthy.Requests.Count >= 1),
                    "切换前要先探活第二家");

                // 探活还没回来,用户把选择改到第三家:这是一次更新的手动选择
                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 2;

                healthy.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0),
                    "选择改过也照常切到探通的第二家把话说完");

                Assert.HasCount(2, healthy.Requests, "备用站一次探活、一次正式回答");
                string turnBody = healthy.Requests[1];
                Assert.Contains("PROMPT_B", turnBody, "换站装配应带第二家自己的专用提示词");
                Assert.DoesNotContain("PROMPT_C", turnBody,
                    "界面选第三家时不许把它的提示词发给第二家");

                AiSettings reloaded = await new AiSettingsStore(context).LoadAsync();
                Assert.AreEqual(third.Models[0].Id, reloaded.ActiveModelId,
                    "探活期间被动过的手选是更新的选择:这一轮切站但不粘住,不许拿落盘盖回去");
                Assert.AreEqual(2, Find<ComboBox>(panel, "ProviderCombo").SelectedIndex,
                    "界面也停在用户选的那家");
            }
            finally
            {
                healthy.Release(); // 兜底放行,免得失败路径把在途请求挂到测试收尾之后
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>探活挂起时保存新 Key 或新地址:旧探测不准替新凭据作证。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void KeyFailover_ProbeChangesDiscardProofWithoutSendingHistoryToNewSlotOrEndpoint(bool changeEndpoint)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var oldTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"经新密钥重探后回答"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "OK", hold: true);
            using var newTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"新站回答"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", oldTarget.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(second.Id, "old-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("本轮消息");
                Assert.IsTrue(await WaitForAsync(() => oldTarget.Requests.Count == 1), "第一次探测挂在旧目标上");
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(settings);
                await PumpAsync(10);
                // 行序为 alpha、alpha/m、beta、beta/m。
                FindIn<ListBox>(settings, "ProvidersList").SelectedIndex = 2;
                await PumpAsync(10);
                await StageProviderApiKeyAsync(settings, "new-key");
                if (changeEndpoint)
                {
                    FindIn<TextBox>(settings, "ProviderBaseUrlBox").Text = newTarget.BaseUrl;
                }
                RaiseClick(FindIn<Button>(settings, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(settings, "StatusText").Text == "Saved."),
                    "新 Key 与目标保存完成");
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual(changeEndpoint ? newTarget.BaseUrl : oldTarget.BaseUrl, saved.Providers[1].BaseUrl);
                Assert.AreEqual("new-key", await new AiSettingsStore(context).GetApiKeyAsync(second.Id));

                oldTarget.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0 && !Find<Button>(panel, "StopButton").IsVisible),
                    "已选槽改值后旧轮次安全结算,不能隐式重新选择并重放历史");
                Assert.AreEqual("Bearer old-key", oldTarget.Authorizations[0], "旧探活仅带原先的 Key");
                Assert.HasCount(1, oldTarget.Requests, "旧端点只收到固定旧 Key 的探活,不再发摘要或正式历史");
                Assert.IsEmpty(newTarget.Requests, "保存新 URL/Key 不授权旧轮次向新目标发消息");
                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally
            {
                oldTarget.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void FailoverProbe_ReasoningCapabilityDropsDuringProbe_DiscardsCandidateUntilNextTurn()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var target = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"正常回答"},"finish_reason":"stop"}]}

                data: [DONE]

                """, holdFirstStream: true, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("first", broken.BaseUrl, "m");
            AiProvider second = StubProvider("second", target.BaseUrl, "m");
            second.Models[0].SupportsReasoning = true;
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                Find<ComboBox>(panel, "ReasoningCombo").SelectedIndex = (int)ReasoningLevel.High;
                panel.SendExternal("确认备用站能力");
                Assert.IsTrue(await WaitForAsync(() => target.Requests.Count == 1), "旧档探活已经挂住");

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                ListBox list = FindIn<ListBox>(editor, "ProvidersList");
                list.SelectedIndex = 3;
                await PumpAsync(10);
                ((ProviderNavItem)list.SelectedItem!).Model!.SupportsReasoning = false;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                Assert.IsFalse((await store.LoadAsync()).Providers[1].Models[0].SupportsReasoning);
                target.Release();

                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1
                    && !Find<Button>(panel, "StopButton").IsVisible), "能力保存后旧回合应安全结束,不得重探新配置");
                Assert.HasCount(1, target.Requests, "旧回合只允许已经发出的 High 探活");
                Assert.Contains("reasoning_effort", target.Requests[0], "原先探活须按开跑时的 High");
                Assert.IsTrue(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);

                Find<ComboBox>(panel, "ReasoningCombo").SelectedIndex = (int)ReasoningLevel.High;
                panel.SendExternal("新回合使用已保存的无思考能力配置");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(3, target.Requests, "新用户回合才能按新能力探活并正式回答");
                Assert.DoesNotContain("reasoning_effort", target.Requests[1]);
                Assert.DoesNotContain("reasoning_effort", target.Requests[2]);
                Assert.Contains("\"stream\":true", target.Requests[1]);
                Assert.Contains("\"stream\":true", target.Requests[2]);
                Assert.AreEqual("正常回答", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                Assert.AreEqual(second.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally
            {
                target.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void FailoverProbe_ReasoningCapabilityGainsDuringProbe_DiscardsCandidateUntilNextTurn()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var target = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"新能力已启用"},"finish_reason":"stop"}]}

                data: [DONE]

                """, holdFirstStream: true, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("first", broken.BaseUrl, "m");
            AiProvider second = StubProvider("second", target.BaseUrl, "m");
            second.Models[0].SupportsReasoning = false;
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                Find<ComboBox>(panel, "ReasoningCombo").SelectedIndex = (int)ReasoningLevel.High;
                panel.SendExternal("检验能力变化");
                Assert.IsTrue(await WaitForAsync(() => target.Requests.Count == 1));
                Assert.DoesNotContain("reasoning_effort", target.Requests[0], "初始不支持思考,旧探活不得带 High");
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                ListBox list = FindIn<ListBox>(editor, "ProvidersList");
                list.SelectedIndex = 3;
                await PumpAsync(10);
                ((ProviderNavItem)list.SelectedItem!).Model!.SupportsReasoning = true;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                Assert.IsTrue((await store.LoadAsync()).Providers[1].Models[0].SupportsReasoning);
                target.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1
                    && !Find<Button>(panel, "StopButton").IsVisible), "能力保存后旧回合不获准重探或发送");
                Assert.HasCount(1, target.Requests, "旧回合只保留原先无 High 的探活");
                Assert.IsTrue(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);

                Find<ComboBox>(panel, "ReasoningCombo").SelectedIndex = (int)ReasoningLevel.High;
                panel.SendExternal("新回合使用已保存的思考能力");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(3, target.Requests, "新用户回合才发送新能力的 High 探活与正式流");
                Assert.Contains("reasoning_effort", target.Requests[1]);
                Assert.Contains("reasoning_effort", target.Requests[2]);
                Assert.Contains("\"stream\":true", target.Requests[1]);
                Assert.Contains("\"stream\":true", target.Requests[2]);
                Assert.AreEqual("新能力已启用", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                Assert.AreEqual(second.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally
            {
                target.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void FailoverProbe_ModelEditedDuringProbe_DiscardsCandidateUntilNextTurn()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var target = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"new-model","choices":[{"index":0,"delta":{"content":"新型号回答"},"finish_reason":"stop"}]}

                data: [DONE]


                """, holdFirstStream: true, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", target.BaseUrl, "old-model");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("请回答");
                Assert.IsTrue(await WaitForAsync(() => target.Requests.Count == 1), "旧型号探活已送出并挂起");
                Assert.Contains("\"model\":\"old-model\"", target.Requests[0]);

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(settings);
                await PumpAsync(10);
                FindIn<ListBox>(settings, "ProvidersList").SelectedIndex = 3; // alpha、alpha/m、beta、beta/model
                await PumpAsync(10);
                FindIn<TextBox>(settings, "ModelBox").Text = "new-model";
                RaiseClick(FindIn<Button>(settings, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(settings, "StatusText").Text == "Saved."));
                Assert.AreEqual("new-model", (await store.LoadAsync()).Providers[1].Models[0].Model);

                target.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1
                    && !Find<Button>(panel, "StopButton").IsVisible), "型号保存后旧回合应拒绝新配置并安全结束");
                Assert.HasCount(1, target.Requests, "旧回合只允许已发出的旧型号探活,不得发新型号探活或历史");
                Assert.IsTrue(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);

                panel.SendExternal("新用户回合请用已保存的新型号回答");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(3, target.Requests, "新回合先探活新型号,再发正式请求");
                Assert.Contains("\"model\":\"new-model\"", target.Requests[1]);
                Assert.Contains("\"model\":\"new-model\"", target.Requests[2]);
                Assert.Contains("\"stream\":true", target.Requests[1]);
                Assert.Contains("\"stream\":true", target.Requests[2]);
                Assert.AreEqual(second.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.AreEqual("新型号回答", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
            }
            finally
            {
                target.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void FailoverProbe_TwoTargetChangesNeverSendToUnverifiedThirdSite()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var oldTarget = new SseStub(LifecycleAnswer, hold: true);
            using var secondTarget = new SseStub(LifecycleAnswer, hold: true);
            using var thirdTarget = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", oldTarget.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("不允许发给未经验证的第三站");
                Assert.IsTrue(await WaitForAsync(() => oldTarget.Requests.Count == 1));
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(settings);
                await PumpAsync(10);
                FindIn<ListBox>(settings, "ProvidersList").SelectedIndex = 2;
                await PumpAsync(10);
                TextBox address = FindIn<TextBox>(settings, "ProviderBaseUrlBox");
                TextBlock status = FindIn<TextBlock>(settings, "StatusText");
                address.Text = secondTarget.BaseUrl;
                RaiseClick(FindIn<Button>(settings, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => status.Text == "Saved."));
                address.Text = thirdTarget.BaseUrl;
                RaiseClick(FindIn<Button>(settings, "SaveButton"));
                await PumpAsync(10);
                Assert.AreEqual(thirdTarget.BaseUrl,
                    (await new AiSettingsStore(context).LoadAsync()).Providers[1].BaseUrl,
                    "第二次保存已落盘,才让旧配置的探活返回");
                oldTarget.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1
                    && !Find<Button>(panel, "StopButton").IsVisible), "连续编辑后旧回合应安全结束,不能再等新站探活");
                Assert.HasCount(1, oldTarget.Requests, "旧站仅收到保存前已发出的探活");
                Assert.IsEmpty(secondTarget.Requests, "第一次地址保存也不得授权旧回合重探第二站");
                Assert.IsEmpty(thirdTarget.Requests, "第二次地址保存不得授权旧回合向第三站发探活或历史");
                Assert.IsTrue(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                Assert.AreEqual(first.Models[0].Id, (await new AiSettingsStore(context).LoadAsync()).ActiveModelId);

                panel.SendExternal("新用户回合使用最终保存的第三站");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(1, oldTarget.Requests);
                Assert.IsEmpty(secondTarget.Requests, "后续新回合也不应使用已经被覆盖的中间地址");
                Assert.HasCount(2, thirdTarget.Requests, "新回合才可在最终地址探活并正式回答");
                Assert.Contains("Reply with exactly:", thirdTarget.Requests[0]);
                Assert.Contains("新用户回合使用最终保存的第三站", thirdTarget.Requests[1]);
                Assert.AreEqual("回答", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                Assert.AreEqual(second.Models[0].Id, (await new AiSettingsStore(context).LoadAsync()).ActiveModelId);
            }
            finally
            {
                oldTarget.Release();
                secondTarget.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailoverProbe_OAuthTokenRefresh_ReprobesSameEndpointButRejectsUntrustedMove(bool moveEndpoint)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var oldTarget = new SseStub(LifecycleAnswer, hold: true);
            using var newTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"订阅端点回答"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", oldTarget.BaseUrl, "m");
            second.Auth = AuthMethod.Subscription;
            second.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(second.Id, new OAuthTokens { AccessToken = "old-token", BaseUrl = oldTarget.BaseUrl, AccountId = "same-account" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store); // 同一登录管理器更新令牌,真实 UI 的订阅登录路径
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("本轮消息");
                Assert.IsTrue(await WaitForAsync(() => oldTarget.Requests.Count == 1), "旧登录端点探活已经发出");
                await store.SaveTokensAsync(second.Id,
                    new OAuthTokens
                    {
                        AccessToken = "new-token", BaseUrl = moveEndpoint ? newTarget.BaseUrl : oldTarget.BaseUrl,
                        AccountId = "same-account"
                    });
                oldTarget.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1
                    && !Find<Button>(panel, "StopButton").IsVisible), "令牌更新后旧回合必须完成或安全失败,不能等未经授权的新端点");
                Assert.AreEqual("Bearer old-token", oldTarget.Authorizations[0]);
                Assert.IsEmpty(newTarget.Requests, "相同账号身份也不能授权旧回合向新来源发送探活、摘要或历史");
                if (moveEndpoint)
                {
                    Assert.HasCount(1, oldTarget.Requests, "地址移动后旧端点仅保留原令牌的已发探活");
                    Assert.IsTrue(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                    Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);

                    panel.SendExternal("新用户回合使用更新后的订阅端点");
                    Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2
                        && !Find<Button>(panel, "StopButton").IsVisible));
                    Assert.HasCount(1, oldTarget.Requests, "新回合不能把新令牌发回旧端点");
                    Assert.HasCount(2, newTarget.Requests, "新用户回合才在新的令牌端点探活并正式回答");
                    Assert.AreSequenceEqual(["Bearer new-token", "Bearer new-token"], newTarget.Authorizations);
                    Assert.Contains("Reply with exactly:", newTarget.Requests[0]);
                    Assert.Contains("新用户回合使用更新后的订阅端点", newTarget.Requests[1]);
                    Assert.AreEqual("订阅端点回答", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                }
                else
                {
                    Assert.HasCount(3, oldTarget.Requests, "同端点续期仍应以新令牌重探,然后正式回答");
                    Assert.AreSequenceEqual(["Bearer old-token", "Bearer new-token", "Bearer new-token"], oldTarget.Authorizations);
                    Assert.Contains("Reply with exactly:", oldTarget.Requests[1]);
                    Assert.Contains("本轮消息", oldTarget.Requests[2]);
                    Assert.AreEqual("回答", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                }
                Assert.AreEqual(second.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally
            {
                oldTarget.Release();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow("unrelated")]
    [DataRow("refresh")]
    [DataRow("moved")]
    [DataRow("model")]
    [DataRow("config")]
    public void FirstStation_SummaryWindowKeepsCredentialsAndEndpointPaired(string change)
    {
        OnUi(async () =>
        {
            using var stub = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"已回答"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "摘要", holdFirstNonStream: true);
            using var newTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"来自新端点"},"finish_reason":"stop"}]}

                data: [DONE]


                """);
            using var context = new TestPluginContext();
            var oauth = new OAuthStub().Json("""{"access_token":"new-token","expires_in":3600}""");
            using var http = new HttpClient(oauth);
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            AiProvider first = StubProvider("alpha", stub.BaseUrl, change == "model" ? "m1" : "m", maxInputTokens: 40);
            first.Models[0].MaxTokens = 1;
            first.Auth = AuthMethod.Subscription;
            first.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken,
                TokenUrl = "https://auth.example/token", ClientId = "test"
            };
            AiProvider unrelated = StubProvider("beta", "http://127.0.0.1:1/v1", "m");
            await store.SaveTokensAsync(first.Id, new OAuthTokens
            {
                AccessToken = "old-token", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), AccountId = "same-account"
            });
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, unrelated], ActiveModelId = first.Models[0].Id, SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            Window? editorHost = null;
            window.Show();
            try
            {
                await PumpAsync();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                for (int turn = 1; turn <= 4; turn++)
                {
                    panel.SendExternal($"第 {turn} 条问题");
                    int completed = turn;
                    Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= completed),
                        "压缩前的历史对话要真实完成");
                }
                panel.SendExternal("该压缩的第五条问题");
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Any(body => !body.Contains("\"stream\":true", StringComparison.Ordinal))),
                    "首站已经用旧凭据发出摘要请求,服务端保持挂起");
                int summaryIndex = stub.Requests.Count - 1;
                Assert.AreEqual("Bearer old-token", stub.Authorizations[summaryIndex]);
                if (change == "model")
                {
                    Assert.Contains("\"model\":\"m1\"", stub.Requests[summaryIndex],
                        "压缩请求已实际用本轮最初的型号发出");
                }

                if (change == "refresh")
                {
                    // 摘要等待期间令牌进入续期窗:下一次 ResolveCredentialAsync 必须真走 OAuth 刷新。
                    await store.SaveTokensAsync(first.Id, new OAuthTokens
                    {
                        AccessToken = "old-token", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20), AccountId = "same-account"
                    });
                }
                else if (change == "moved")
                {
                    await store.SaveTokensAsync(first.Id, new OAuthTokens
                    {
                        AccessToken = "new-token", RefreshToken = "refresh", BaseUrl = newTarget.BaseUrl,
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), AccountId = "same-account"
                    });
                }
                else
                {
                    Click(panel, "SettingsButton");
                    await PumpAsync(5);
                    var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                    editorHost = Host(editor);
                    await PumpAsync(10);
                    FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = change == "config" ? 0 : change == "model" ? 1 : 2;
                    await PumpAsync(10);
                    if (change == "config")
                    {
                        FindIn<TextBox>(editor, "ProviderBaseUrlBox").Text = newTarget.BaseUrl;
                    }
                    else if (change == "model")
                    {
                        FindIn<TextBox>(editor, "ModelBox").Text = "m2";
                    }
                    else
                    {
                        FindIn<TextBox>(editor, "ProviderNameBox").Text = "renamed-beta";
                    }
                    RaiseClick(FindIn<Button>(editor, "SaveButton"));
                    Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                    Assert.AreEqual(change == "config" ? newTarget.BaseUrl : change == "model" ? "m2" : "renamed-beta",
                        change == "config" ? (await store.LoadAsync()).Providers[0].BaseUrl
                        : change == "model" ? (await store.LoadAsync()).Providers[0].Models[0].Model
                        : (await store.LoadAsync()).Providers[1].Name);
                }

                stub.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= 5
                    && !Find<Button>(panel, "StopButton").IsVisible),
                    "摘要后应结束本轮:同端点续期继续发送,配置变更或令牌移到不可信来源则安全拒绝");
                Assert.HasCount(summaryIndex + 1 + (change is "moved" or "config" or "model" ? 0 : 1), stub.Requests,
                    "目标变更后不能把新凭据发到旧站");
                if (change is "moved" or "config" or "model")
                {
                    Assert.IsEmpty(newTarget.Requests, "保存请求变更或同账号令牌移到不可信来源都不授权旧回合发送");
                    Assert.IsTrue(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                }
                else
                {
                    Assert.Contains("\"stream\":true", stub.Requests[summaryIndex + 1]);
                    Assert.AreEqual(change == "unrelated" ? "Bearer old-token" : "Bearer new-token",
                        stub.Authorizations[summaryIndex + 1], "同端点续期的正式客户端与令牌必须配对固定");
                }
                if (change == "moved")
                {
                    panel.SendExternal("新用户回合使用订阅的新端点");
                    Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= 6
                        && !Find<Button>(panel, "StopButton").IsVisible));
                    Assert.HasCount(summaryIndex + 1, stub.Requests, "新的用户回合不能带新令牌回到旧端点");
                    Assert.IsTrue(newTarget.Requests.Any(body => body.Contains("\"stream\":true", StringComparison.Ordinal)),
                        "新用户回合才可向新端点发送正式消息");
                    Assert.IsTrue(newTarget.Authorizations.All(value => value == "Bearer new-token"));
                    Assert.AreEqual("来自新端点", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                }
                Assert.HasCount(change == "refresh" ? 1 : 0, oauth.Requests,
                    "只有令牌进入续期窗才真正调用 OAuth 刷新端点");
            }
            finally
            {
                stub.Release();
                editorHost?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailoverSummary_OAuthRenewsDuringCompaction_ReprobesSameEndpointButRejectsUntrustedMove(bool moveEndpoint)
    {
        OnUi(async () =>
        {
            using var summaryGate = new ManualResetEventSlim();
            using var broken = new ErrorStub();
            using var oldTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"旧站答复"},"finish_reason":"stop"}]}

                data: [DONE]

                """, nonStreamingReply: body =>
            {
                if (body.Contains("You are compacting", StringComparison.Ordinal))
                {
                    summaryGate.Wait(); // 真实 HTTP 摘要已抵达，令牌在响应挂起时更新
                    return "摘要";
                }
                return "OK";
            });
            using var renewedTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"续期后答复"},"finish_reason":"stop"}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":13,"completion_tokens":4,"total_tokens":17}}

                data: [DONE]

                """, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("broken", broken.BaseUrl, "m");
            AiProvider second = StubProvider("subscription", oldTarget.BaseUrl, "m", maxInputTokens: 40);
            second.Models[0].MaxTokens = 1;
            second.Auth = AuthMethod.Subscription;
            second.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(second.Id, new OAuthTokens { AccessToken = "old", BaseUrl = oldTarget.BaseUrl, AccountId = "same-account" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = second.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                for (int turn = 1; turn <= 4; turn++)
                {
                    panel.SendExternal($"历史第{turn}条问题");
                    int finished = turn;
                    Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= finished));
                }
                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 0;
                panel.SendExternal("第五条触发故障转移和压缩");
                Assert.IsTrue(await WaitForAsync(() => oldTarget.Requests.Any(body => body.Contains("You are compacting", StringComparison.Ordinal))),
                    "备用站探活之后旧凭据的真实摘要请求已挂住");
                await store.SaveTokensAsync(second.Id,
                    new OAuthTokens
                    {
                        AccessToken = "renewed", BaseUrl = moveEndpoint ? renewedTarget.BaseUrl : oldTarget.BaseUrl,
                        AccountId = "same-account"
                    });
                summaryGate.Set();

                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= 5
                    && !Find<Button>(panel, "StopButton").IsVisible), "摘要后同端点续期应答完,不可信来源变更应安全拒绝");
                Assert.AreEqual("Bearer old", oldTarget.Authorizations[5]);
                Assert.DoesNotContain("\"stream\":true", oldTarget.Requests[5]);
                Assert.IsEmpty(renewedTarget.Requests, "摘要期间的同账号令牌地址移动不能授权旧回合向新来源探活或发送历史");
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                Assert.Contains("Conversation total: in 1 / out 1", ToolTip.GetTip(usage) as string ?? "",
                    "已完成的摘要 1/1 仍须入账;未发出的新站请求及无用量的旧流不可估造");
                if (moveEndpoint)
                {
                    Assert.HasCount(6, oldTarget.Requests, "旧站只有四次历史流、一次探活和一次摘要,不再接收第五轮正式流");
                    Assert.IsTrue(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                    Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);

                    panel.SendExternal("新回合");
                    Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= 6
                        && !Find<Button>(panel, "StopButton").IsVisible));
                    Assert.HasCount(6, oldTarget.Requests, "新回合也不能把续期令牌带回旧站");
                    Assert.AreEqual(2, renewedTarget.Requests.Count(body => body.Contains("\"stream\":true", StringComparison.Ordinal)),
                        "新用户回合在新端点先探活、后真实回答");
                    Assert.IsTrue(renewedTarget.Authorizations.All(value => value == "Bearer renewed"));
                    Assert.Contains("Reply with exactly:", renewedTarget.Requests[0]);
                    Assert.Contains("新回合", renewedTarget.Requests[^1]);
                    Assert.AreEqual("续期后答复", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                    Assert.Contains("13/40", usage.Text ?? "", "新回合重探用量不替代正式输入占窗");
                    int summaries = renewedTarget.Requests.Count(body => !body.Contains("\"stream\":true", StringComparison.Ordinal));
                    Assert.Contains($"Conversation total: in {27 + summaries} / out {9 + summaries}", ToolTip.GetTip(usage) as string ?? "",
                        "旧轮摘要与新回合探活、正式流及实际发生的新摘要各入账一次");
                }
                else
                {
                    Assert.HasCount(8, oldTarget.Requests, "同端点续期保留旧探活和摘要,以新令牌重探后正式回答");
                    Assert.AreEqual("Bearer renewed", oldTarget.Authorizations[6]);
                    Assert.AreEqual(oldTarget.Authorizations[6], oldTarget.Authorizations[7]);
                    Assert.Contains("Reply with exactly:", oldTarget.Requests[6]);
                    Assert.Contains("\"stream\":true", oldTarget.Requests[7]);
                    Assert.AreEqual("旧站答复", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                }
                Assert.AreEqual(second.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally
            {
                summaryGate.Set();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(true, false, true, "en")]
    [DataRow(true, true, true, "en")]
    [DataRow(false, false, true, "en")]
    [DataRow(false, true, true, "en")]
    [DataRow(true, false, false, "en")]
    [DataRow(true, true, false, "zh-Hans")]
    [DataRow(false, false, false, "zh-Hant")]
    [DataRow(false, true, false, "ja")]
    [DataRow(true, true, false, "ko")]
    public void FailoverBackup_SettingsChangedDuringPreparation_SkipsUnverifiedStationOrShowsLocalizedError(
        bool mcpWait, bool moveEndpoint, bool useBackup, string locale)
    {
        OnUi(async () =>
        {
            using var broken = new SseStub("data: [DONE]\n\n", holdFirstStream: true, firstStreamContent: "");
            using var oldTarget = new SseStub(LifecycleAnswer, jsonContent: "摘要", holdFirstNonStream: true);
            using var newTarget = new SseStub(LifecycleAnswer);
            using var healthy = new SseStub(LifecycleAnswer, firstStreamContent: "OK");
            using var mcp = new SseStub("", hold: true);
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", oldTarget.BaseUrl, "m", maxInputTokens: 40);
            AiProvider c = StubProvider("c", healthy.BaseUrl, "m");
            b.Models[0].MaxTokens = 1;
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(b.Id, "old-key");
            await store.SetApiKeyAsync(c.Id, "c-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = useBackup ? [a, b, c] : [a, b], ActiveModelId = b.Models[0].Id,
                FailoverChain = useBackup
                    ? [new FailoverEntry { ModelId = b.Models[0].Id }, new FailoverEntry { ModelId = c.Models[0].Id }]
                    : [new FailoverEntry { ModelId = b.Models[0].Id }],
                McpServers = [],
                SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            await PumpAsync();
            Window? host = null, toolsHost = null, mcpHost = null;
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                for (int turn = 1; turn <= 4; turn++)
                {
                    panel.SendExternal($"历史第{turn}条问题");
                    int finished = turn;
                    Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= finished
                        && !Find<Button>(panel, "StopButton").IsVisible));
                }
                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 0;
                if (mcpWait) Find<ComboBox>(panel, "ModeCombo").SelectedIndex = (int)ChatMode.Agent;
                panel.SendExternal("private-fifth-turn");
                await broken.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                if (mcpWait)
                {
                    Click(panel, "ToolsButton");
                    await PumpAsync(5);
                    var picker = (ToolPickerView)context.FakeUi.LastPanel.CreateContent();
                    toolsHost = Host(picker);
                    RaiseClick(FindIn<Button>(picker, "McpRailAddButton"));
                    await PumpAsync(5);
                    var servers = (McpServersView)context.FakeUi.LastPanel.CreateContent();
                    mcpHost = Host(servers);
                    await PumpAsync(5);
                    FindIn<ComboBox>(servers, "McpTransportCombo").SelectedIndex = (int)McpTransportType.Http;
                    FindIn<TextBox>(servers, "McpUrlBox").Text = mcp.BaseUrl + "/mcp";
                    FindIn<CheckBox>(servers, "McpEnabledCheck").IsChecked = true;
                    RaiseClick(FindIn<Button>(servers, "McpSaveButton"));
                }
                broken.Release();
                Assert.IsTrue(await WaitForAsync(() => mcpWait ? mcp.RequestBodyAsync.IsCompleted
                    : oldTarget.Requests.Any(body => body.Contains("You are compacting", StringComparison.Ordinal))),
                    "B 已通过真实探活,装配正在等 MCP 或摘要响应");
                int beforeChange = mcpWait ? 5 : 6;
                Assert.HasCount(beforeChange, oldTarget.Requests, "B 只有四次历史流、一次探活及可选摘要,尚无第五轮正式流");
                Assert.Contains("Reply with exactly:", oldTarget.Requests[4]);
                Assert.AreEqual("Bearer old-key", oldTarget.Authorizations[^1]);

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 2;
                await StageProviderApiKeyAsync(editor, "new-key");
                if (moveEndpoint) FindIn<TextBox>(editor, "ProviderBaseUrlBox").Text = newTarget.BaseUrl;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                Assert.AreEqual("new-key", await store.GetApiKeyAsync(b.Id));
                Assert.AreEqual(moveEndpoint ? newTarget.BaseUrl : oldTarget.BaseUrl, (await store.LoadAsync()).Providers[1].BaseUrl);
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch(locale);
                mcp.Release();
                oldTarget.Release();

                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= 5
                    && !Find<Button>(panel, "StopButton").IsVisible, 1200));
                Assert.HasCount(beforeChange, oldTarget.Requests, "B 旧客户端不再收到摘要或正式正文,也不得原地重探新 Key");
                Assert.IsTrue(oldTarget.Authorizations.All(value => value == "Bearer old-key"));
                Assert.IsEmpty(newTarget.Requests, "保存后未经探活的新地址和 Key 绝不接收本轮历史或正式正文");
                if (useBackup)
                {
                    Assert.HasCount(2, healthy.Requests, "B 装配失败不终结整链;C 先探活,再真实回答");
                    Assert.Contains("Reply with exactly:", healthy.Requests[0]);
                    Assert.DoesNotContain("private-fifth-turn", healthy.Requests[0]);
                    Assert.Contains("private-fifth-turn", healthy.Requests[1]);
                    Assert.AreSequenceEqual(["Bearer c-key", "Bearer c-key"], healthy.Authorizations);
                    Assert.AreEqual("回答", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                    Assert.IsFalse(messages.GetVisualDescendants().OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
                    Assert.AreEqual(c.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                    var history = new ChatHistoryStore(context);
                    await history.InitAsync();
                    ChatEntry lastAnswer = (await history.LoadAsync((await history.ListSessionsAsync()).Single().Id))
                        .Last(entry => entry.Role == "assistant");
                    Assert.AreEqual("回答", lastAnswer.Text, "C 的真实回答也必须入库,不能只停在探活或空成功气泡");
                }
                else
                {
                    Assert.IsEmpty(healthy.Requests);
                    Border card = messages.GetVisualDescendants().OfType<Border>().Single(item => item.Classes.Contains("errorCard"));
                    string detail = card.GetVisualDescendants().OfType<SelectableTextBlock>()
                        .Single(block => block.Classes.Contains("mono")).Text ?? "";
                    Assert.AreEqual(loc["ErrorProviderChanged"], detail);
                    Assert.DoesNotContain("Provider configuration changed", detail);
                    Assert.DoesNotContain("old-key", detail);
                    Assert.DoesNotContain("new-key", detail);
                    Assert.AreEqual(a.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
            }
            finally { broken.Release(); mcp.Release(); oldTarget.Release(); mcpHost?.Close(); toolsHost?.Close(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void McpHandshake_ProviderChangesBeforeCompaction_RevalidatesOrRefuses(bool moveEndpoint)
    {
        OnUi(async () =>
        {
            using var mcp = new SseStub("", hold: true);
            using var oldTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"历史答复"},"finish_reason":"stop"}]}

                data: [DONE]

                """, jsonContent: "摘要");
            using var newTarget = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("account", oldTarget.BaseUrl, "m", maxInputTokens: 40);
            provider.Models[0].MaxTokens = 1;
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(provider.Id, "old-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                McpServers = [new McpServerConfig { Name = "held", Transport = McpTransportType.Http, Url = mcp.BaseUrl + "/mcp" }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                for (int turn = 1; turn <= 4; turn++)
                {
                    panel.SendExternal($"之前第{turn}问");
                    int finished = turn;
                    Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= finished));
                }
                Find<ComboBox>(panel, "ModeCombo").SelectedIndex = (int)ChatMode.Agent;
                await PumpAsync(10);
                panel.SendExternal("该压缩的第五问");
                Assert.IsTrue(await WaitForAsync(() => mcp.RequestBodyAsync.IsCompleted), "第五轮确实在等 MCP 网络握手");

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 0;
                await StageProviderApiKeyAsync(editor, "new-key");
                if (moveEndpoint)
                {
                    FindIn<TextBox>(editor, "ProviderBaseUrlBox").Text = newTarget.BaseUrl;
                }
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                mcp.Release();

                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count >= 5, 1200),
                    "MCP 返回后轮次应安全结算");
                Assert.HasCount(4, oldTarget.Requests, "固定主槽改值后旧客户端不能在 MCP 返回后偷发摘要或历史");
                Assert.IsTrue(oldTarget.Authorizations.All(auth => auth == "Bearer old-key"));
                Assert.IsEmpty(newTarget.Requests, "新 URL/Key 未经本轮验证,不能接收旧轮次用户历史");
                Border errorCard = messages.GetVisualDescendants().OfType<Border>().Single(card => card.Classes.Contains("errorCard"));
                Assert.AreEqual(new Loc("en")["ErrorProviderChanged"], errorCard.GetVisualDescendants()
                    .OfType<SelectableTextBlock>().Single(block => block.Classes.Contains("mono")).Text);
            }
            finally
            {
                mcp.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ConfigChangedDuringAnswer_SkipsSuggestionsToOldAddressWithNewKey()
    {
        OnUi(async () =>
        {
            using var oldTarget = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"已经回答"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "OK", hold: true);
            using var newTarget = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("alpha", oldTarget.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(provider.Id, "old-key");
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id,
                SuggestFollowUps = true });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("本轮消息");
                Assert.IsTrue(await WaitForAsync(() => oldTarget.Requests.Count == 1));
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(settings);
                await PumpAsync(10);
                FindIn<ListBox>(settings, "ProvidersList").SelectedIndex = 0;
                await StageProviderApiKeyAsync(settings, "new-key");
                FindIn<TextBox>(settings, "ProviderBaseUrlBox").Text = newTarget.BaseUrl;
                RaiseClick(FindIn<Button>(settings, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(settings, "StatusText").Text == "Saved."));
                var persisted = new AiSettingsStore(context);
                Assert.AreEqual(newTarget.BaseUrl, (await persisted.LoadAsync()).Providers[0].BaseUrl);
                Assert.AreEqual("new-key", await persisted.GetApiKeyAsync(provider.Id));

                oldTarget.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count > 0));
                await PumpAsync(15);
                Assert.HasCount(1, oldTarget.Requests, "答完后不能重新取新 Key 向旧地址附带发送请求");
                Assert.AreEqual("Bearer old-key", oldTarget.Authorizations[0]);
                Assert.IsEmpty(newTarget.Requests, "旧轮次不应擅自向新地址补发建议");
            }
            finally
            {
                oldTarget.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 全局设置里的故障转移列表:添加(自动排掉已在链上的)、上移、移出、保存落盘。
    /// </summary>
    [TestMethod]
    public void FailoverChainList_AddMoveRemoveThenSave()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var provider = new AiProvider
            {
                Name = "one",
                BaseUrl = "http://127.0.0.1:1/v1",
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [new AiModelConfig { Model = "m1" }, new AiModelConfig { Model = "m2" }]
            };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var view = new GlobalSettingsView(context, store, settings, new Loc("en"),
                draft => store.SaveGlobalSettingsAsync(settings, draft), _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                StackPanel rows = FindIn<StackPanel>(view, "FailoverRowsHost");
                Assert.HasCount(0, rows.Children, "空链起步:切换走自动发现");
                Button probeAll = FindIn<Button>(view, "FailoverProbeAllButton");
                Assert.IsFalse(probeAll.IsEnabled, "空链没有探测目标,按钮不应可点击却无反馈");

                // 加两次:第一次拿的是 m1,第二次下拉只剩 m2
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                Assert.HasCount(2, rows.Children, "加了两次就有两行");
                Assert.IsTrue(probeAll.IsEnabled, "链里有模型时才能检测全部");
                Assert.AreEqual("m1", RowLabels(view)[0]);
                Assert.AreEqual("m2", RowLabels(view)[1]);
                Assert.IsEmpty((List<string>)FindIn<ComboBox>(view, "FailoverAddCombo").ItemsSource!,
                    "两份都进链了,没有可再加的");
                Assert.IsTrue(FindIn<TextBlock>(view, "FailoverAddHintText").IsVisible,
                    "一个都不剩时要把话说清楚,而不是留一个空下拉");

                // 聚焦第二行上移:重建后应把焦点留给移动后的同一模型。
                Button moving = NamedButtons(view, "FailoverUpButton")[1];
                Assert.IsTrue(moving.Focus());
                RaiseClick(moving);
                List<string> moved = RowLabels(view);
                Assert.AreEqual("m2", moved[0], "上移后排到第一");
                Assert.AreEqual("m1", moved[1]);
                Assert.IsFalse(NamedButtons(view, "FailoverUpButton")[0].IsEnabled, "第一行没有可再上的");
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton")[0].IsFocused,
                    "移到第一行时上移按钮禁用,焦点落在同一模型的移除按钮");

                // 移出第一行 → 只剩 m1,焦点交给幸存行。
                RaiseClick(NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.HasCount(1, rows.Children, "移出一行");
                Assert.AreEqual("m1", RowLabels(view)[0]);
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton")[0].IsFocused,
                    "移除行后键盘焦点要落在相邻幸存行");

                // 保存:编辑态写回设置
                RaiseClick(FindIn<Button>(view, "SaveButton"));
                await PumpAsync(10);
                Assert.HasCount(1, settings.FailoverChain, "保存把列表写回设置");
                Assert.AreEqual(provider.Models[0].Id, settings.FailoverChain[0].ModelId);
                Assert.AreEqual("Saved.", FindIn<TextBlock>(view, "StatusText").Text, "保存成功有回执");

                RaiseClick(NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.IsFalse(probeAll.IsEnabled, "移除最后一站后不能留下无效的检测按钮");
                Assert.IsTrue(FindIn<Button>(view, "SaveButton").IsFocused,
                    "最后一行移除后焦点要退到仍可操作的保存按钮");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ProbeAllStatus_SwitchingLocaleRerendersProgressAndSummary()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var stub = new SseStub(LifecycleAnswer, hold: true);
            AiProvider provider = StubProvider("only", stub.BaseUrl, "m1");
            var settings = new AiSettings
            {
                Providers = [provider], FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, loc,
                _ => Task.CompletedTask, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                TextBlock status = FindIn<TextBlock>(view, "FailoverStatusText");
                RaiseClick(FindIn<Button>(view, "FailoverProbeAllButton"));
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count > 0));
                Assert.AreEqual("Testing m1…", status.Text);

                loc.Switch("zh-Hans");
                view.ApplyLoc();
                Assert.AreEqual("正在检测 m1…", status.Text, "探活仍在跑,换语种不能丢模型名和进度");
                Assert.IsFalse(FindIn<Button>(view, "FailoverProbeAllButton").IsEnabled,
                    "语言刷新重建行时不应重新放出仍在途的批量检测");
                Button removeDuringProbe = NamedButtons(view, "FailoverRemoveButton")[0];

                stub.Release();
                Assert.IsTrue(await WaitForAsync(() => status.Text == "检测完成:1 通过,0 失败。"),
                    "探活完成保留成功/失败计数,并以当前语种显示");
                Assert.AreSame(removeDuringProbe, NamedButtons(view, "FailoverRemoveButton")[0],
                    "单项检测结束不应拆掉行内按钮,否则键盘焦点会丢失");
                Assert.IsTrue(FindIn<Button>(view, "FailoverProbeAllButton").IsEnabled,
                    "探活真正结束且链非空后才重新允许检测");
                loc.Switch("en");
                view.ApplyLoc();
                Assert.AreEqual("Done: 1 passed, 0 failed.", status.Text,
                    "完成后的状态也必须随语言切换,不能冻结原语言字符串");
            }
            finally
            {
                stub.Release();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 换语言会重建「可添加」下拉(行上的按钮提示是本语言的)——
    /// 重建不能把用户刚挑好的那条拨回第一项,否则点「添加」加进去的是另一条模型。
    /// </summary>
    [TestMethod]
    public void FailoverAddCombo_KeepsThePickedModelWhenRebuilt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var provider = new AiProvider
            {
                Name = "one",
                BaseUrl = "http://127.0.0.1:1/v1",
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models =
                [
                    new AiModelConfig { Name = "m1", Model = "a" },
                    new AiModelConfig { Name = "m2", Model = "b" },
                    new AiModelConfig { Name = "m3", Model = "c" }
                ]
            };
            var settings = new AiSettings { Providers = [provider] };
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, new Loc("en"),
                _ => Task.CompletedTask, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                ComboBox addCombo = FindIn<ComboBox>(view, "FailoverAddCombo");
                Assert.HasCount(3, (List<string>)addCombo.ItemsSource!, "都还没进链,三条都能加");
                addCombo.SelectedIndex = 2;
                Assert.AreEqual("m3", addCombo.SelectedItem, "前提:挑中的是第三条");

                view.ApplyLoc(); // 换语言:行提示重建,可添加下拉跟着重建

                Assert.AreEqual(2, addCombo.SelectedIndex,
                    "重建不能把挑中的那条拨回第一项 —— 点「添加」会加错模型");
                Assert.AreEqual("m3", addCombo.SelectedItem, "挑中的还是同一条");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// ActiveModelId 空着、或指着已删的模型(旧数据、并发改动都可能留下这种悬空 id):
    /// 下拉显示并实际在用的是第一项,但这个有效选择必须<b>写回设置</b> ——
    /// 否则故障转移的粘住判定(实际站 == 记录站)永远为假:第一站故障切走后粘不住,
    /// 下一轮又从故障站起,备用站白干一轮。
    /// </summary>
    [TestMethod]
    public void MissingActiveModelId_WritesTheFirstStationBackAndPersistsIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("alpha", "http://127.0.0.1:1/v1", "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = "deleted-model-id", // 模型设置里删掉它之后留下的悬空 id
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Assert.AreEqual(0, Find<ComboBox>(panel, "ProviderCombo").SelectedIndex,
                    "悬空 id:显示并实际在用的都是第一项");

                await PumpAsync(20); // 写回是 fire-and-forget 的落盘,给它几拍
                AiSettings reloaded = await new AiSettingsStore(context).LoadAsync();
                Assert.AreEqual(provider.Models[0].Id, reloaded.ActiveModelId,
                    "有效选择要写回并落盘 —— 粘住与回滚都靠它对表");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 状态灯要跟得上<b>窗外</b>发生的事:行建好之后,聊天里新进的失败(没人动这个窗口)
    /// 也得点亮 —— 只在重建行时读一次冷却状态,窗口开着就永远停在旧灯上。
    /// </summary>
    [TestMethod]
    public void FailoverLight_LightsUpForNewFailuresWithoutRebuildingRows()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var provider = new AiProvider
            {
                Name = "one",
                BaseUrl = "http://127.0.0.1:1/v1",
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [new AiModelConfig { Name = "m1", Model = "a" }]
            };
            var settings = new AiSettings
            {
                Providers = [provider],
                FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            var health = new ProviderHealth();
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, new Loc("en"),
                _ => Task.CompletedTask, _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                var dot = FindIn<Avalonia.Controls.Shapes.Path>(view, "FailoverRowDot");
                Assert.HasCount(1, FindIn<StackPanel>(view, "FailoverRowsHost").Children, "前提:链上有一行");
                Assert.IsFalse(dot.IsVisible, "没测过也没冷却:灯是灭的");

                health.Record(provider.Models[0].Id, ok: false); // 窗外(聊天里)新发生的失败

                Assert.IsTrue(await WaitForAsync(() => dot.IsVisible),
                    "心跳要把新失败点亮 —— 行一次都没重建过");
                Assert.AreEqual(new Loc("en")["DotCooling"], ToolTip.GetTip(dot),
                    "没有主动探测结果时,冷却灯应说明是最近请求失败,不能冒充探活失败");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 改配置(<c>RefreshFromProviders</c>)会掐掉在途的「检测全部」—— 但进度行还写着
    /// 「正在检测〈旧模型〉」:那描述的是一轮已经停下的任务,窗口能一直显示着它(review⑥)。
    /// </summary>
    [TestMethod]
    public void RefreshFromProviders_ClearsTheStaleProbingProgressLine()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var stub = new SseStub(LifecycleAnswer, hold: true); // 挂住:让进度行停在"正在检测"
            var provider = new AiProvider
            {
                Name = "one",
                BaseUrl = stub.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [new AiModelConfig { Name = "m1", Model = "a" }]
            };
            var settings = new AiSettings
            {
                Providers = [provider],
                FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, new Loc("en"),
                _ => Task.CompletedTask, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                TextBlock status = FindIn<TextBlock>(view, "FailoverStatusText");
                RaiseClick(FindIn<Button>(view, "FailoverProbeAllButton"));
                await PumpAsync(5);
                Assert.Contains("m1", status.Text, "前提:进度行显示着「正在检测 m1…」");

                view.RefreshFromProviders(); // 改配置:掐掉在途探测
                await PumpAsync(10);

                Assert.AreEqual("", status.Text,
                    "被掐掉的那轮进度不再存在 —— 留着「正在检测」就是在显示一个已停止的任务(review⑥)");
            }
            finally
            {
                stub.Release();
                window.Close();
                await PumpAsync(5);
            }
        });
    }

    [TestMethod]
    public void RemovingOnlyChainModel_CancelsProbeAndNeverRestoresOldProgressOrSummary()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var stub = new SseStub(LifecycleAnswer, holdFirstStream: true,
                firstStreamContent: "old result");
            AiProvider provider = StubProvider("only", stub.BaseUrl, "m1");
            var settings = new AiSettings
            {
                Providers = [provider], FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, new Loc("en"),
                _ => Task.CompletedTask, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                TextBlock status = FindIn<TextBlock>(view, "FailoverStatusText");
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count == 1), "唯一模型的旧探活已在 HTTP 端挂起");
                Assert.AreEqual("Testing m1…", status.Text);

                RaiseClick(NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.AreEqual("", status.Text, "移除后立刻清除旧进度");
                Assert.IsFalse(probe.IsEnabled, "空链不允许继续探活");
                stub.Release();
                await PumpAsync(10);
                Assert.AreEqual("", status.Text, "旧响应到达后不得重写旧结果或汇总");

                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled), "旧任务取消后新链可重新探活");
                Assert.AreEqual("", status.Text, "重新添加同一模型不继承旧汇总");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => status.Text == "Done: 1 passed, 0 failed."),
                    "重新添加后可独立完成新一轮探活");
                Assert.HasCount(2, stub.Requests, "新汇总只能来自第二次探活");
            }
            finally
            {
                stub.Release();
                window.Close();
            }
        });
    }


    /// <summary>
    /// 开跑时选的思考档位是<b>快照</b>:第一家 401、按链换到第二家,这一轮仍按开跑时的档位走 ——
    /// 但只对「可调档」的站生效。目标站 <c>SupportsReasoning=false</c> 时档位得被摘掉,
    /// 否则一个它根本不认的 <c>reasoning_effort</c> 就跟着换站发了出去(review⑥#5)。
    /// </summary>
    [TestMethod]
    public void FailoverHop_DoesNotCarryTheTierToAStationThatCannotAdjustIt()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var healthy = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"来自第二家。"},"finish_reason":"stop"}]}

                data: [DONE]


                """,
                // 探活与正式回答都消费上面的 SSE。
                jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", healthy.BaseUrl, "m");
            second.Models[0].SupportsReasoning = false; // 第二家能聊天,不能带思考档
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox combo = Find<ComboBox>(panel, "ReasoningCombo");
                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);
                Assert.Contains("overridden", combo.Classes, "前提:开跑前临时档位是亮着的");

                panel.SendExternal("随便说点什么");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(
                        () => Footers(messages).Count > 0 && healthy.Requests.Count >= 2),
                    $"第一家 401,按链切到第二家答完(footers={Footers(messages).Count}, req={healthy.Requests.Count})");

                Assert.DoesNotContain("reasoning_effort", healthy.Requests[1],
                    "第二家不支持思考:换站不许把临时档位的 reasoning_effort 捎过去(review⑥#5)");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 粘住切站会换掉当前模型 —— 开跑时选的思考档位是<b>对上一家</b>的临时选择,
    /// 换站后必须一并清掉:否则 chip 还亮着「改过档」,下一轮把上一家的档位发给了这一家(review⑥#6)。
    /// 第一轮(开跑时的快照)是正向对照:它该带档,证明断言盯的不是个空串。
    /// </summary>
    [TestMethod]
    public void StickyProviderSwitch_ClearsTheTemporaryReasoningTier()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var healthy = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"来自第二家。"},"finish_reason":"stop"}]}

                data: [DONE]


                """,
                jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", healthy.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox combo = Find<ComboBox>(panel, "ReasoningCombo");
                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);
                Assert.Contains("overridden", combo.Classes, "前提:开跑前临时档位是亮着的");

                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("随便说点什么");
                Assert.IsTrue(await WaitForAsync(
                        () => Footers(messages).Count > 0
                              && Find<ComboBox>(panel, "ProviderCombo").SelectedIndex == 1),
                    "前提:第一家 401,按链切到第二家并粘住");
                Assert.DoesNotContain("overridden", combo.Classes,
                    "换站清掉开跑时的临时档位 —— chip 还亮着就是在骗人(review⑥#6)");
                Assert.Contains("reasoning_effort", healthy.Requests[1],
                    "正向对照:第一轮(开跑时的快照)该带档 —— 不带就说明断言盯的是个空串");

                panel.SendExternal("再说一句");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 1),
                    "第二轮也要答完");
                Assert.DoesNotContain("reasoning_effort", healthy.Requests[2],
                    "档位已清:第二轮按第二家自己的配置发,不许再捎带旧站的档(review⑥#6)");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void CandidateProbe_ShowsLocalizedProgressAndCanBeStoppedWithoutSticking(string locale)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var candidate = new SseStub(LifecycleAnswer, hold: true);
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("first", broken.BaseUrl, "m");
            AiProvider second = StubProvider("candidate", candidate.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch(locale);
                panel.SendExternal("请回答");
                Assert.IsTrue(await WaitForAsync(() => candidate.Requests.Count == 1));
                Assert.AreEqual(loc.F("FailoverProbing", second.Models[0].DisplayName), Find<TextBlock>(panel, "StatusText").Text);
                Assert.IsTrue(Find<Button>(panel, "StopButton").IsVisible);
                Assert.IsTrue(Find<Button>(panel, "StopButton").IsEnabled);
                Assert.Contains("Reply with exactly:", candidate.Requests[0], "候选挂起的是探活而非正式回答");
                Click(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                candidate.Release();
                await PumpAsync(10);
                Assert.HasCount(1, candidate.Requests, "停止后迟到的探活不得启动正式请求");
                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.AreEqual(0, Find<ComboBox>(panel, "ProviderCombo").SelectedIndex);
                Assert.IsEmpty(AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")));
            }
            finally { candidate.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en", false)]
    [DataRow("zh-Hans", false)]
    [DataRow("zh-Hant", false)]
    [DataRow("ja", false)]
    [DataRow("ko", false)]
    [DataRow("en", true)]
    public void BuiltinOAuthAtForeignHost_NeverLeaksAndUsesBackupOrLocalizedError(string locale, bool useBackup)
    {
        OnUi(async () =>
        {
            using var foreign = new SseStub(LifecycleAnswer);
            using var backup = new SseStub(LifecycleAnswer, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("openrouter")!.CreateProvider();
            first.BaseUrl = foreign.BaseUrl;
            AiProvider second = StubProvider("backup", backup.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(first.Id, new OAuthTokens { AccessToken = "sk-or-secret" });
            await store.SetApiKeyAsync(second.Id, "backup-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = useBackup ? [first, second] : [first], ActiveModelId = first.Models[0].Id,
                FailoverChain = useBackup ? [new FailoverEntry { ModelId = second.Models[0].Id }] : [],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch(locale);
                panel.SendExternal("请回答");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.IsEmpty(foreign.Requests, "内置 OAuth Key 不得发往异站，连探活也不许发");
                Assert.IsEmpty(foreign.Authorizations);
                if (useBackup)
                {
                    Assert.HasCount(2, backup.Requests, "备用站只探活一次并正式回答一次");
                    Assert.AreSequenceEqual(["Bearer backup-key", "Bearer backup-key"], backup.Authorizations);
                    Assert.Contains("Reply with exactly:", backup.Requests[0]);
                    Assert.DoesNotContain("Reply with exactly:", backup.Requests[1]);
                    Assert.AreEqual("回答", AnswerRenderers(messages).Single().MarkdownBuilder.ToString());
                    Assert.AreEqual(second.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                    Assert.AreEqual(1, Find<ComboBox>(panel, "ProviderCombo").SelectedIndex);
                }
                else
                {
                    Assert.IsEmpty(backup.Requests);
                    Border card = messages.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("errorCard"));
                    string detail = card.GetVisualDescendants().OfType<SelectableTextBlock>()
                        .Single(t => t.Classes.Contains("mono")).Text ?? "";
                    Assert.AreEqual(loc["SetupBuiltinOAuthHostMismatch"], detail);
                    Assert.DoesNotContain("sk-or-secret", detail);
                    Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void WhitespaceOnlyFirstStation_RetriesOnceThenShowsOnlyBackupAnswerAndSticks()
    {
        OnUi(async () =>
        {
            using var empty = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":" \t\n "},"finish_reason":"stop"}]}

                data: [DONE]


                """);
            using var backup = new SseStub(LifecycleAnswer, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("empty", empty.BaseUrl, "m");
            AiProvider second = StubProvider("backup", backup.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("请回答");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, empty.Requests, "首站正式空白回复只允许原地重试一次");
                Assert.HasCount(2, backup.Requests, "备用一次探活一次正式回复");
                Assert.AreEqual(second.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.AreEqual(1, Find<ComboBox>(panel, "ProviderCombo").SelectedIndex);
                Assert.AreEqual("回答", AnswerRenderers(messages).Single().MarkdownBuilder.ToString(),
                    "失败首站的空白不能残留在备用正文之前");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void FailedUsageThenUnreportedAnswer_KeepsTotalsButClearsUnknownContext()
    {
        OnUi(async () =>
        {
            using var warm = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"历史回答"},"finish_reason":"stop"}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":50,"completion_tokens":10,"total_tokens":60}}

                data: [DONE]


                """);
            using var failed = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}

                data: [DONE]


                """);
            using var backup = new SseStub(LifecycleAnswer, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider history = StubProvider("history", warm.BaseUrl, "m", maxInputTokens: 100);
            AiProvider original = StubProvider("failed", failed.BaseUrl, "m", maxInputTokens: 100);
            AiProvider next = StubProvider("backup", backup.BaseUrl, "m", maxInputTokens: 100);
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [history, original, next], ActiveModelId = history.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = next.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("先建立真实历史上下文");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1 && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.Contains("50/100", Find<TextBlock>(panel, "UsageText").Text ?? "");
                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 1;
                panel.SendExternal("空流失败后备用不报告用量");
                Assert.IsTrue(await WaitForAsync(() => backup.Requests.Count == 2 && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, failed.Requests, "失败元数据流有限重试一次");
                Assert.AreEqual("回答", AnswerRenderers(messages).Last().MarkdownBuilder.ToString());
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                Assert.AreEqual("↑64 ↓16", usage.Text, "累计保留50/10与两次7/3，未知正文占窗不能借用历史或失败量");
                Assert.Contains("Conversation total: in 64 / out 16", ToolTip.GetTip(usage) as string ?? "");
                Assert.DoesNotContain("Last turn context:", ToolTip.GetTip(usage) as string ?? "");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ProbePassesButMetadataOnlyAnswer_SwitchesPastEmptyStationAndSticksToResponder()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var empty = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}

                data: [DONE]


                """, firstStreamContent: "OK");
            using var answering = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"第三站回答"},"finish_reason":"stop"}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":11,"completion_tokens":5,"total_tokens":16}}

                data: [DONE]

                """, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", empty.BaseUrl, "m");
            AiProvider third = StubProvider("gamma", answering.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second, third], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id },
                                 new FailoverEntry { ModelId = third.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("请回答");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0 && answering.Requests.Count >= 2),
                    "B 探活通过但正式流只有元数据,应由 C 接管回答");
                Assert.HasCount(3, empty.Requests, "B 一次探活、一次空回复、有限的原地重试一次");
                Assert.HasCount(2, answering.Requests, "C 探活一次、正式回答一次");
                Assert.Contains("\"stream\":true", empty.Requests[1]);
                Assert.Contains("\"stream\":true", empty.Requests[2]);
                Assert.Contains("\"stream\":true", answering.Requests[1]);
                Assert.AreEqual(third.Models[0].Id, (await store.LoadAsync()).ActiveModelId,
                    "空回复 B 不能粘住,真正答复的 C 才能粘住");
                Assert.IsNotEmpty(AnswerRenderers(messages), "C 的真实正文要显示,而不是空的成功气泡");
                string usageTip = ToolTip.GetTip(Find<TextBlock>(panel, "UsageText")) as string ?? "";
                Assert.Contains("Conversation total: in 36 / out 16", usageTip,
                    "B 的两次空流各计一次，加上 C 的探活与真实回复；usage 不可被重试清空或并进回答正文");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void MetadataOnlyAnswerAtLastStation_ShowsLocalizedEmptyReplyInsteadOfNetworkError()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var empty = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":"stop"}]}

                data: [DONE]


                """, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", empty.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("请回答");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0));
                Assert.HasCount(3, empty.Requests, "探活一次后空正式回复最多重试一次");
                Border card = messages.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Classes.Contains("errorCard"));
                string detail = card.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Single(t => t.Classes.Contains("mono")).Text ?? "";
                Assert.AreEqual(new Loc("en")["ProbeEmptyReply"], detail,
                    "服务端 HTTP 已通但没有开口,应显示既有的本地化空回复提示");
                Assert.DoesNotContain(new Loc("en")["ErrorUnreachable"], detail,
                    "空回复不能说成网络/代理未连通");
                Assert.AreEqual(first.Models[0].Id, (await store.LoadAsync()).ActiveModelId,
                    "没有一站真正答复时,不能粘住空回复的 B");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void RoleOnlyUpdate_DoesNotBlockReplayButVisibleContentAndToolsDo()
    {
        List<ChatResponseUpdate> updates = [new(ChatRole.Assistant, [])];
        Assert.IsFalse(ChatPanelView.HasMeaningfulOutput(updates),
            "已经解析的首帧只有 role,没有可见内容或工具副作用,断流可安全重试");
        updates.Add(new ChatResponseUpdate(ChatRole.Assistant,
            [new TextContent(" \t\r\n "), new TextReasoningContent(" \t\r\n ")]));
        Assert.IsFalse(ChatPanelView.HasMeaningfulOutput(updates), "纯空白正文和思考仍可重放");
        updates.Add(new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("回答")]));
        Assert.IsTrue(ChatPanelView.HasMeaningfulOutput(updates), "看见正文后不许重放");
        updates.Clear();
        updates.Add(new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "run_command", null)]));
        Assert.IsTrue(ChatPanelView.HasMeaningfulOutput(updates), "工具调用即使无文字也不能重放");
        updates.Clear();
        updates.Add(new ChatResponseUpdate(ChatRole.Tool, [new FunctionResultContent("call-1", "")]));
        Assert.IsTrue(ChatPanelView.HasMeaningfulOutput(updates), "空工具结果也不可重放");
        updates.Clear();
        updates.Add(new ChatResponseUpdate(ChatRole.Assistant, [new ErrorContent("failure")]));
        Assert.IsTrue(ChatPanelView.HasMeaningfulOutput(updates), "错误内容不可重放");
    }
    [TestMethod]
    [DataRow(" \t\r\n ", false)]
    [DataRow("真实思考", true)]
    public async Task RawReasoningReplayBoundary_UsesRealOpenAiStreamingUpdates(string reasoning, bool meaningful)
    {
        string delta = System.Text.Json.JsonSerializer.Serialize(new { role = "assistant", reasoning });
        using var stub = new SseStub(
            $"data: {{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{{\"index\":0,\"delta\":{delta},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n");
        var openAi = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("k"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(stub.BaseUrl) });
        using IChatClient client = openAi.GetChatClient("m").AsIChatClient();
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("hi"))
        {
            updates.Add(update);
        }
        Assert.IsTrue(updates.Any(update => ReasoningPeek.IsBlank(update)
            && ReasoningPeek.TryRead(update.RawRepresentation, out string recovered) && recovered == reasoning),
            "前提:真实 SDK raw update 的 reasoning 确实被读取，不用伪造 RawRepresentation");
        Assert.AreEqual(meaningful, ChatPanelView.HasMeaningfulOutput(updates),
            "raw 思考纯空白可重放，真实思考禁止重放");
    }


    /// <summary>真 SSE 断流后备用站接管;首帧 role 的分类由上面的纯判定测试确定性证明。</summary>
    [TestMethod]
    public void RoleOnlyFrameThenBrokenStream_TransfersToHealthyStation()
    {
        OnUi(async () =>
        {
            using var broken = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

                """, chunkDelay: TimeSpan.FromMilliseconds(80), abortAfterFirstChunk: true);
            using var healthy = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"备用站接管成功"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", healthy.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("请回答");
                await broken.FirstChunkFlushedAsync.WaitAsync(TimeSpan.FromSeconds(10));
                broken.Release(); // 只有role元数据,不用把发送完成伪称可见正文已消费。
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0
                    && healthy.Requests.Count >= 2), "真实断流后应重试并由备用站探活接管");
                Assert.IsTrue(broken.Requests.Count >= 2, "真实流式请求断了,至少原地重试一次");
                Assert.Contains("\"stream\":true", broken.Requests[0], "故障发生在流式正文,不是连接之前");
                Assert.AreEqual(second.Models[0].Id, (await new AiSettingsStore(context).LoadAsync()).ActiveModelId,
                    "备用站接管且被粘住");
                Assert.IsNotEmpty(AnswerRenderers(messages), "备用站的回答应真正显示在聊天界面");
            }
            finally
            {
                broken.Release();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConfigChangedAfterVisibleOutput_BrokenOrCancelledStreamNeverReplays(bool cancel)
    {
        OnUi(async () =>
        {
            using var original = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"partial-response"},"finish_reason":null}]}

                """, chunkDelay: TimeSpan.FromMilliseconds(1), abortAfterFirstChunk: true, holdAfterFirstChunk: true);
            using var moved = new SseStub(LifecycleAnswer);
            using var backup = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            AiProvider a = StubProvider("a", original.BaseUrl, "m");
            AiProvider b = StubProvider("b", backup.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(a.Id, "old-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("protected-visible-output");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => AnswerRenderers(messages).Any(renderer =>
                    renderer.MarkdownBuilder.ToString() == "partial-response")), "首帧真实正文已在界面消费,流仍挂起");
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 0;
                await StageProviderApiKeyAsync(editor, "new-key");
                FindIn<TextBox>(editor, "ProviderBaseUrlBox").Text = moved.BaseUrl;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                if (cancel) Click(panel, "StopButton");
                original.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(1, original.Requests, "开口后断流或取消都不可原地重试");
                Assert.AreEqual("Bearer old-key", original.Authorizations.Single());
                Assert.IsEmpty(moved.Requests, "新地址和 Key 不得接收旧轮次的重放");
                Assert.IsEmpty(backup.Requests, "已有正文或用户取消都不触发备用站探活");
                Assert.AreEqual("partial-response", AnswerRenderers(messages).Single().MarkdownBuilder.ToString());
                Assert.AreEqual(a.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally { original.Release(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void KeyFailover_ConfigChangedAfterToolExecutionDoesNotReplaySideEffects()
    {
        OnUi(async () =>
        {
            using var original = new HeldStreamingReply(toolBeforeFailure: true);
            using var moved = new SseStub(LifecycleAnswer);
            using var backup = new SseStub(LifecycleAnswer);
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            context.FakeSessions.AddConnected();
            context.FakeRemoteExec.Handler = (_, _) => "executed-once";
            AiProvider a = StubProvider("a", original.BaseUrl, "m");
            AiProvider b = StubProvider("b", backup.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(a.Id, "old-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b], ActiveModelId = a.Models[0].Id, Mode = ChatMode.Agent, Approval = ApprovalMode.Bypass,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
            });
            AiSettings pool = await store.LoadAsync();
            await store.AddProviderApiKeyAsync(pool, pool.Providers[0], "spare-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("execute-protected-command");
                Assert.IsTrue(await WaitForAsync(() => original.ContinuationRequest.IsCompleted));
                Assert.HasCount(1, context.FakeRemoteExec.Executed, "真实 SSE 工具调用已由函数循环执行一次");
                Assert.Contains("executed-once", await original.ContinuationRequest,
                    "后续真实请求已消费工具结果,不是只测构造出的 FunctionCallContent");
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 0;
                await StageProviderApiKeyAsync(editor, "new-key");
                FindIn<TextBox>(editor, "ProviderBaseUrlBox").Text = moved.BaseUrl;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                original.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(1, context.FakeRemoteExec.Executed, "配置变化和可切换鉴权失败不得重放已执行工具");
                Assert.AreEqual(2, original.FormalRequests, "工具流与工具结果续流各一次,不能换另一把 Key 重放");
                Assert.IsEmpty(moved.Requests, "已产生工具副作用的旧轮次不得发送给新配置");
                Assert.IsEmpty(backup.Requests, "有工具副作用就连备用站探活也不得开始");
                Assert.AreEqual(a.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.IsTrue(Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants()
                    .OfType<Border>().Any(card => card.Classes.Contains("errorCard")));
            }
            finally { original.Release(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    /// <summary>探活即时返回;正式 SSE 请求收到后等待测试显式放行。</summary>
    private sealed class HeldStreamingReply : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly TaskCompletionSource<string> _stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _continuation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _followUpRequests;
        private int _formalRequests;
        private readonly Lock _authGate = new();
        private readonly List<string?> _authorizations = [];
        public IReadOnlyList<string?> Authorizations { get { lock (_authGate) return [.. _authorizations]; } }

        public string BaseUrl { get; }
        public Task<string> StreamRequest => _stream.Task;
        public Task<string> ContinuationRequest => _continuation.Task;
        public int FollowUpRequests => Volatile.Read(ref _followUpRequests);
        public int FormalRequests => Volatile.Read(ref _formalRequests);

        public HeldStreamingReply(bool toolBeforeFailure = false, bool successfulContinuation = false)
        {
            for (int attempt = 0; ; attempt++)
            {
                var portProbe = new TcpListener(IPAddress.Loopback, 0);
                portProbe.Start();
                int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
                portProbe.Stop();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    BaseUrl = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183)
                {
                    listener.Close();
                }
                catch
                {
                    listener.Close();
                    throw;
                }
            }

            byte[] probe = Encoding.UTF8.GetBytes("""
                {"id":"1","object":"chat.completion","created":1,"model":"m1","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}
                """);
            byte[] streamedProbe = Encoding.UTF8.GetBytes("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m1","choices":[{"index":0,"delta":{"content":"OK"},"finish_reason":"stop"}]}

                data: [DONE]


                """.ReplaceLineEndings("\n"));
            byte[] reply = Encoding.UTF8.GetBytes("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m1","choices":[{"index":0,"delta":{"content":"备用站回答"},"finish_reason":"stop"}]}

                data: [DONE]


                """.ReplaceLineEndings("\n"));
            byte[] toolReply = Encoding.UTF8.GetBytes("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"run_command","arguments":"{\"command\":\"touch /tmp/config-change-protection\"}"}}]},"finish_reason":null}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

                data: [DONE]


                """.ReplaceLineEndings("\n"));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        HttpListenerContext request = await _listener.GetContextAsync();
                        using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                        string body = await reader.ReadToEndAsync();
                        bool streaming = body.Contains("\"stream\":true", StringComparison.Ordinal);
                        bool probing = streaming && body.Contains("Reply with exactly:", StringComparison.Ordinal);
                        if (!streaming)
                        {
                            Interlocked.Increment(ref _followUpRequests);
                        }
                        if (streaming && !probing)
                        {
                            _stream.TrySetResult(body);
                            int formal = Interlocked.Increment(ref _formalRequests);
                            lock (_authGate) _authorizations.Add(request.Request.Headers["Authorization"]);
                            if (toolBeforeFailure && formal == 1)
                            {
                                request.Response.ContentType = "text/event-stream";
                                request.Response.ContentLength64 = toolReply.Length;
                                await request.Response.OutputStream.WriteAsync(toolReply);
                                request.Response.Close();
                                continue;
                            }
                            if (toolBeforeFailure) _continuation.TrySetResult(body);
                            await _released.Task;
                            if (toolBeforeFailure && !successfulContinuation)
                            {
                                request.Response.StatusCode = 401;
                                byte[] error = Encoding.UTF8.GetBytes("""{"error":{"message":"invalid api key"}}""");
                                request.Response.ContentType = "application/json";
                                request.Response.ContentLength64 = error.Length;
                                await request.Response.OutputStream.WriteAsync(error);
                                request.Response.Close();
                                continue;
                            }
                        }
                        byte[] content = probing ? streamedProbe : streaming ? reply : probe;
                        request.Response.ContentType = streaming ? "text/event-stream" : "application/json";
                        request.Response.ContentLength64 = content.Length;
                        await request.Response.OutputStream.WriteAsync(content);
                        request.Response.Close();
                    }
                }
                catch (Exception)
                {
                    // 测试结束时监听器被 Dispose,不再发送响应。
                }
            });
        }

        public void Release() => _released.TrySetResult();

        public void Dispose()
        {
            _stream.TrySetCanceled();
            Release();
            _listener.Close();
        }
    }

    private static AiSettings PanelSettings(ChatPanelView panel)
        => (AiSettings)typeof(ChatPanelView).GetField("_settings",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;

    private static ProviderHealth PanelHealth(ChatPanelView panel)
        => (ProviderHealth)typeof(ChatPanelView).GetField("_health",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;

    /// <summary>按真实 Authorization 应答,可只挂起指定槽的探活,不阻塞其它会话。</summary>
    private sealed class KeyAuthStub : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Lock _gate = new();
        private readonly List<(string? Auth, string Body)> _requests = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string BaseUrl { get; }
        public IReadOnlyList<(string? Auth, string Body)> Requests { get { lock (_gate) return [.. _requests]; } }

        public KeyAuthStub(Func<string?, string, (int Status, string Body)> response, string? holdProbeKey = null, bool holdFirstRequest = false)
        {
            for (int attempt = 0; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    BaseUrl = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183) { listener.Close(); }
                catch { listener.Close(); throw; }
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    bool held = false;
                    while (true)
                    {
                        HttpListenerContext request = await _listener.GetContextAsync();
                        using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                        string body = await reader.ReadToEndAsync();
                        string? auth = request.Request.Headers["Authorization"];
                        lock (_gate) _requests.Add((auth, body));
                        bool pause = !held && auth == holdProbeKey && (holdFirstRequest || body.Contains("Reply with exactly:", StringComparison.Ordinal));
                        if (pause) { held = true; Held.TrySetResult(); }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                if (pause) await _release.Task;
                                (int status, string content) = response(auth, body);
                                byte[] bytes = Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n"));
                                request.Response.StatusCode = status;
                                request.Response.ContentType = status == 200 ? "text/event-stream" : "application/json";
                                request.Response.ContentLength64 = bytes.Length;
                                await request.Response.OutputStream.WriteAsync(bytes);
                                request.Response.Close();
                            }
                            catch (Exception) { request.Response.Close(); }
                        });
                    }
                }
                catch (Exception) { /* 测试关闭监听器后不再接收请求。 */ }
            });
        }

        public void Release() => _release.TrySetResult();
        public void Dispose() { Release(); _listener.Close(); }
    }

    private static async Task StageProviderApiKeyAsync(SettingsView editor, string key)
    {
        Assert.IsTrue(await WaitForAsync(() => NamedButtons(editor, "ProviderKeyEditButton").Count > 0),
            "先载入已保存的主槽行,不把旧 Secret 回填到编辑草稿");
        RaiseClick(NamedButtons(editor, "ProviderKeyEditButton")[0]);
        Assert.IsTrue(await WaitForAsync(() => editor.GetVisualDescendants().OfType<TextBox>()
            .Any(box => box.Name == "ProviderKeyEditBox" && box.IsVisible)));
        TextBox draft = FindIn<TextBox>(editor, "ProviderKeyEditBox");
        Assert.IsTrue(string.IsNullOrEmpty(draft.Text), "铅笔只展开空草稿");
        draft.Text = key;
    }

    private static void RaiseClick(Control control)
        => control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>按 Name 在任意控件树里找一个(面板那个同名助手只吃 ChatPanelView)。</summary>
    private static T FindIn<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
           ?? throw new InvalidOperationException($"没有叫 {name} 的 {typeof(T).Name}");

    private static List<Button> NamedButtons(Control root, string name)
        => [.. root.GetVisualDescendants().OfType<Button>().Where(b => b.Name == name)];

    private static List<string> RowLabels(Control root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Name == "FailoverRowLabel")
            .Select(t => t.Text ?? "")];
}
