using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [DataRow(null)]
    public void DefaultContextWindow_UsageTooltipWarnsForEstimateButNotVerified128000(bool? defaultWindow)
    {
        OnUi(async () =>
        {
            using var endpoint = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"reply"},"finish_reason":null}]}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}

                data: [DONE]

                """);
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            AiProvider provider = StubProvider("window", endpoint.BaseUrl, "m");
            provider.Models[0].MaxInputTokens = 128000;
            provider.Models[0].DefaultContextWindow = defaultWindow;
            await new AiSettingsStore(context).SaveAsync(new AiSettings
                { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox modelPicker = Find<ComboBox>(panel, "ProviderCombo");
                Assert.IsTrue(modelPicker.IsVisible && modelPicker.IsEffectivelyVisible);
                string before = ToolTip.GetTip(modelPicker)?.ToString() ?? "";
                var beforeLoc = new Loc("en");
                if (defaultWindow == true) Assert.Contains(beforeLoc["ContextWindowDefaultHint"], before);
                else if (defaultWindow is null) Assert.Contains(beforeLoc["ContextWindowUnverifiedHint"], before);
                else Assert.DoesNotContain(beforeLoc["ContextWindowDefaultHint"], before);
                Assert.AreEqual(defaultWindow == false ? "" : beforeLoc[defaultWindow == true ? "ContextWindowDefaultHint" : "ContextWindowUnverifiedHint"],
                    Avalonia.Automation.AutomationProperties.GetHelpText(modelPicker));
                panel.SendExternal("show context usage");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible
                    && (ToolTip.GetTip(Find<TextBlock>(panel, "UsageText"))?.ToString() ?? "").Contains("Conversation total: in 5 / out 2")));
                string tip = ToolTip.GetTip(Find<TextBlock>(panel, "UsageText"))?.ToString() ?? "";
                var loc = new Loc("en");
                if (defaultWindow == true) Assert.Contains(loc["ContextWindowDefaultHint"], tip);
                else Assert.DoesNotContain(loc["ContextWindowDefaultHint"], tip);
                if (defaultWindow is null) Assert.Contains(loc["ContextWindowUnverifiedHint"], tip);
                else Assert.DoesNotContain(loc["ContextWindowUnverifiedHint"], tip);
                Assert.Contains("Conversation total: in 5 / out 2", tip);
                Assert.HasCount(1, endpoint.Requests, "来源说明不得增加探活或付费请求");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_GlobalConflictDetectsDirectKeyPublicationBeforeOrAfterWindowCreation(bool beforeWindow)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer new-key"
                ? (200, LifecycleAnswer) : (401, "{\"error\":{\"message\":\"old key\"}}"));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                FailoverChain = [new() { ModelId = provider.Models[0].Id }]
            });
            await store.SetApiKeyAsync(provider.Id, "old-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                ProviderHealth health = PanelHealth(panel);
                health.Record(provider.Models[0].Id, false);
                health.RecordKey(provider.Id, false);
                int version = health.Version;
                AiProvider liveProvider = PanelSettings(panel).Providers[0];
                long requestVersion = store.ProviderConfigurationVersion(liveProvider);
                var otherStore = new AiSettingsStore(context);
                if (beforeWindow) await otherStore.SetApiKeyAsync(provider.Id, "new-key");
                GlobalSettingsView view = await OpenGlobalViewAsync(context, panel);
                host = Host(view);
                if (!beforeWindow) await otherStore.SetApiKeyAsync(provider.Id, "new-key");
                Assert.IsTrue(store.ProviderConfigurationVersion(liveProvider) > requestVersion,
                    "直接写 Key 在重载前已推进活对象代次");
                // 纯显示刷新不得提前认可新 Key 的代次、吞掉父健康表仍持有的旧失败。
                typeof(ChatPanelView).GetMethod("RefreshProviderViews",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [false]);
                AiSettings other = await otherStore.LoadAsync();
                other.SystemPrompt = "external prompt";
                await otherStore.SaveAsync(other);
                FindIn<TextBox>(view, "SystemPromptBox").Text = "explicit draft";
                Button save = FindIn<Button>(view, "SaveButton");
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                var loc = new Loc("en");
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                Assert.AreEqual(version + 1, health.Version);
                Assert.IsFalse(health.IsCooling(provider.Models[0].Id));
                Assert.IsFalse(health.IsKeyCooling(provider.Id));
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled && endpoint.Requests.Count == 1));
                Assert.AreEqual("Bearer new-key", endpoint.Requests.Single().Auth);
                Assert.IsTrue(FindIn<TextBlock>(view, "FailoverRowStatus").Text!.StartsWith(loc["DotPassed"], StringComparison.Ordinal));
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == loc["Saved"]));

                health.Record(provider.Models[0].Id, false);
                health.RecordKey(provider.Id, false);
                other = await otherStore.LoadAsync();
                other.SystemPrompt = "another external prompt only";
                await otherStore.SaveAsync(other);
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                Assert.AreEqual(version + 1, health.Version, "确认处理 Key 变化后，纯全局冲突不再误清健康");
                Assert.IsTrue(health.IsCooling(provider.Models[0].Id));
                Assert.IsTrue(health.IsKeyCooling(provider.Id));
                Assert.HasCount(1, endpoint.Requests, "重载和显示刷新不额外发探活");
            }
            finally { host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Review_GlobalChainReloadCancelsOnlyChangedEffectiveProbeBatch(bool completed, bool changeChain)
    {
        OnUi(async () =>
        {
            using var a = new SseStub(LifecycleAnswer, holdFirstStream: !completed, firstStreamContent: "OK");
            using var b = new SseStub(LifecycleAnswer, firstStreamContent: "OK");
            using var c = new SseStub(LifecycleAnswer, firstStreamContent: "OK");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider first = StubProvider("A", a.BaseUrl, "a");
            AiProvider second = StubProvider("B", b.BaseUrl, "b");
            AiProvider third = StubProvider("C", c.BaseUrl, "c");
            await store.SaveAsync(new AiSettings
            {
                Providers = [first, second, third], ActiveModelId = first.Models[0].Id,
                FailoverChain = [new() { ModelId = first.Models[0].Id }, new() { ModelId = second.Models[0].Id }]
            });
            await store.SetApiKeyAsync(first.Id, "key-a");
            await store.SetApiKeyAsync(second.Id, "key-b");
            await store.SetApiKeyAsync(third.Id, "key-c");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                GlobalSettingsView view = await OpenGlobalViewAsync(context, panel);
                host = Host(view);
                ProviderHealth health = PanelHealth(panel);
                health.Record(first.Models[0].Id, false);
                health.Record(third.Models[0].Id, false);
                int version = health.Version;
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                TextBlock summary = FindIn<TextBlock>(view, "FailoverStatusText");
                var loc = new Loc("en");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => a.Requests.Count == 1 && (!completed
                    || probe.IsEnabled && summary.Text == loc.F("FailoverProbeDone", 2, 0))));
                if (!completed) Assert.IsEmpty(b.Requests, "A挂起时还没有探B");
                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                other.SystemPrompt = "external prompt";
                other.Providers[0].Name = "renamed A";
                if (changeChain) other.FailoverChain = [new() { ModelId = third.Models[0].Id }];
                await otherStore.SaveAsync(other);
                // 本地化和纯显示刷新也不能打断同一有效链的真实探测。
                view.ApplyLoc();
                view.RefreshFromProviders(false);
                FindIn<TextBox>(view, "SystemPromptBox").Text = "explicit draft";
                Button save = FindIn<Button>(view, "SaveButton");
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                Assert.AreEqual(version, health.Version, "链变化不等于请求配置变化，不能清共享健康");
                if (changeChain) Assert.AreEqual("", summary.Text, "新链不能展示旧批次进度或完成汇总");
                else if (completed) Assert.AreEqual(loc.F("FailoverProbeDone", 2, 0), summary.Text);
                a.Release();
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled));
                await PumpAsync(5);
                Assert.HasCount(1, a.Requests);
                Assert.HasCount(completed || !changeChain ? 1 : 0, b.Requests,
                    "挂起A期间链已替换为C，A返回不能再向旧链B发真实付费探活");
                Assert.IsEmpty(c.Requests, "回载新链不是用户请求探测新链");
                Assert.IsTrue(health.IsCooling(third.Models[0].Id), "旧批次不能改写新链C的健康证据");
                if (!completed && changeChain) Assert.IsTrue(health.IsCooling(first.Models[0].Id),
                    "取消后的迟到A成功也不能发布旧Observation");
                Assert.AreEqual(changeChain ? "" : loc.F("FailoverProbeDone", 2, 0), summary.Text);
                var results = (System.Collections.IDictionary)typeof(GlobalSettingsView).GetField("_probeResults",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
                Assert.AreEqual(changeChain ? 0 : 2, results.Count);
            }
            finally { a.Release(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("prompt", false, 2)]
    [DataRow("chain", false, 2)]
    [DataRow("name", false, 2)]
    [DataRow("balance", false, 2)]
    [DataRow("endpoint", true, 2)]
    [DataRow("add", true, 3)]
    [DataRow("remove", true, 1)]
    public void Review_GlobalConflictOnlyRequestChangesInvalidateParentHealth(string change, bool requestChanged, int modelCount)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("original", "http://127.0.0.1:1/v1", "m1");
            provider.Models.Add(new AiModelConfig { Model = "m2" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                SystemPrompt = "original prompt", FailoverChain = [new() { ModelId = provider.Models[0].Id }]
            });
            await store.SetApiKeyAsync(provider.Id, "primary-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            Window? settingsHost = null;
            try
            {
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var modelSettings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = Host(modelSettings);
                await PumpAsync(5);
                context.FakeUi.LastPanel.Options.TitleActions.Single().OnClick();
                await PumpAsync(5);
                var view = (GlobalSettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(view);
                var loc = new Loc("en");
                var health = (ProviderHealth)typeof(ChatPanelView).GetField("_health",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                AiSettings live = PanelSettings(panel);
                AiProvider kept = live.Providers[0];
                health.Record(provider.Models[0].Id, false);
                health.RecordKey(provider.Id, false);
                int healthVersion = health.Version;
                long requestVersion = store.ProviderConfigurationVersion(kept);
                FindIn<TextBox>(view, "SystemPromptBox").Text = "explicit prompt draft";
                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                switch (change)
                {
                    case "prompt": other.SystemPrompt = "external prompt"; break;
                    case "chain": other.FailoverChain = [new() { ModelId = provider.Models[1].Id }]; break;
                    case "name":
                        other.Providers[0].Name = "renamed provider";
                        other.Providers[0].Models[0].Name = "renamed model";
                        break;
                    case "balance": other.Providers[0].BalanceApiKeys = true; break;
                    case "endpoint": other.Providers[0].BaseUrl = "http://127.0.0.1:2/v1"; break;
                    case "add": other.Providers[0].Models.Add(new AiModelConfig { Model = "m3" }); break;
                    case "remove": other.Providers[0].Models.RemoveAt(1); break;
                }
                await otherStore.SaveAsync(other);
                Button save = FindIn<Button>(view, "SaveButton");
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                Assert.AreSame(kept, live.Providers[0], "同 ID 的请求变化不得靠替换引用检测");
                Assert.AreEqual(requestChanged, store.ProviderConfigurationVersion(kept) != requestVersion);
                Assert.AreEqual(requestChanged ? healthVersion + 1 : healthVersion, health.Version);
                Assert.AreEqual(!requestChanged, health.IsCooling(provider.Models[0].Id));
                Assert.AreEqual(!requestChanged, health.IsKeyCooling(provider.Id));
                Assert.HasCount(modelCount, Find<ComboBox>(panel, "ProviderCombo").Items);
                Assert.HasCount(modelCount - 1, FindIn<ComboBox>(view, "FailoverAddCombo").Items);
                if (change == "name")
                {
                    Assert.AreEqual("renamed model", modelSettings.GetControl<TextBox>("NameBox").Text);
                    Assert.Contains("renamed model", Find<ComboBox>(panel, "ProviderCombo").Items[0]?.ToString() ?? "");
                }
                Assert.AreEqual("explicit prompt draft", FindIn<TextBox>(view, "SystemPromptBox").Text);
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == loc["Saved"]));
                Assert.AreEqual(requestChanged ? healthVersion + 1 : healthVersion, health.Version);
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("explicit prompt draft", saved.SystemPrompt);
                CollectionAssert.AreEqual(other.FailoverChain.Select(entry => entry.ModelId).ToArray(),
                    saved.FailoverChain.Select(entry => entry.ModelId).ToArray());
            }
            finally { settingsHost?.Close(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_GlobalAutoPrunedChainFollowsExternalChainButKeepsUserReordering(bool reorder)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", "http://127.0.0.1:1/v1", "deleted");
            provider.Models.Add(new AiModelConfig { Model = "b" });
            provider.Models.Add(new AiModelConfig { Model = "c" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[1].Id,
                FailoverChain = provider.Models.Select(model => new FailoverEntry { ModelId = model.Id }).ToList()
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                GlobalSettingsView view = await OpenGlobalViewAsync(context, panel);
                host = Host(view);
                if (reorder) RaiseClick(NamedButtons(view, "FailoverUpButton")[2]);
                AiSettings live = PanelSettings(panel);
                await store.DeleteModelAsync(live, live.Providers[0], live.Providers[0].Models[0]);
                view.RefreshFromProviders();
                CollectionAssert.AreEqual(reorder ? new[] { "c", "b" } : new[] { "b", "c" }, RowLabels(view));
                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                var added = new AiModelConfig { Model = "external-new" };
                other.Providers[0].Models.Add(added);
                other.FailoverChain = [new() { ModelId = added.Id }];
                await otherStore.SaveAsync(other);
                FindIn<TextBox>(view, "SystemPromptBox").Text = "explicit prompt draft";
                Button save = FindIn<Button>(view, "SaveButton");
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                var loc = new Loc("en");
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                string[] expected = reorder ? new[] { provider.Models[2].Id, provider.Models[1].Id } : new[] { added.Id };
                string[] rows = view.GetVisualDescendants().OfType<Border>()
                    .Where(row => row.Name == "FailoverRow").Select(row => (string)row.Tag!).ToArray();
                CollectionAssert.AreEqual(expected, rows, "自动删除不能伪装用户编辑；真实重排也不能被外部链覆盖");
                Assert.AreEqual(added.Id, live.FailoverChain.Single().ModelId, "冲突恢复不提前发布用户重排");
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == loc["Saved"]));
                AiSettings saved = await store.LoadAsync();
                CollectionAssert.AreEqual(expected, saved.FailoverChain.Select(entry => entry.ModelId).ToArray());
                Assert.AreEqual("explicit prompt draft", saved.SystemPrompt);
            }
            finally { host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void Review_GlobalIndependentSnapshotConflictRefreshesUntouchedFieldsAndPreservesExplicitDraft(string locale)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("original", "http://127.0.0.1:1/v1", "m");
            provider.Models.Add(new AiModelConfig { Model = "removed-model" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                SystemPrompt = "original prompt", CompactContext = true,
                WebSearch = new WebSearchOptions { SearxngBaseUrl = "https://original.example", MaxFetchChars = 12345 }
            });
            await store.SetApiKeyAsync(provider.Id, "original-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            Window? settingsHost = null;
            try
            {
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var modelSettings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = Host(modelSettings);
                await PumpAsync(5);
                modelSettings.GetControl<TextBox>("NameBox").Text = "keep model draft";
                context.FakeUi.LastPanel.Options.TitleActions.Single().OnClick();
                await PumpAsync(5);
                var view = (GlobalSettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(view);
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch(locale); view.ApplyLoc();
                TextBox prompt = FindIn<TextBox>(view, "SystemPromptBox");
                prompt.Text = "explicit prompt draft";
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                Button remove = NamedButtons(view, "FailoverRemoveButton").Single();
                Assert.IsTrue(remove.Focus());
                var health = (ProviderHealth)typeof(ChatPanelView).GetField("_health",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                health.Record(provider.Models[0].Id, false);
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(view, "FailoverRowStatus").Text == loc["DotCooling"]));
                Assert.HasCount(2, Find<ComboBox>(panel, "ProviderCombo").Items);
                AiSettings live = PanelSettings(panel);
                AiProvider kept = live.Providers[0];
                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                other.SystemPrompt = "external prompt";
                other.CompactContext = false;
                other.WebSearch.SearxngBaseUrl = "https://external.example";
                other.WebSearch.MaxFetchChars = 98765;
                other.PanelWidthPercent = 65;
                other.DisabledBuiltinTools = "terminal_write";
                other.Providers[0].Name = "latest provider";
                other.Providers[0].Models.RemoveAt(1);
                string slot = await otherStore.AddProviderApiKeyAsync(other, other.Providers[0], "external-key");
                Button save = FindIn<Button>(view, "SaveButton");
                RaiseClick(save);
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                Assert.AreSame(live, PanelSettings(panel));
                Assert.AreSame(kept, live.Providers[0]);
                Assert.AreEqual("external prompt", live.SystemPrompt);
                Assert.AreEqual("explicit prompt draft", prompt.Text);
                Assert.AreEqual(false, FindIn<CheckBox>(view, "CompactContextCheck").IsChecked);
                Assert.AreEqual("https://external.example", FindIn<TextBox>(view, "WebSearxUrlBox").Text);
                Assert.AreEqual("latest provider", live.Providers[0].Name);
                Button reboundRemove = NamedButtons(view, "FailoverRemoveButton").Single();
                Assert.IsTrue(reboundRemove.IsFocused, "恢复重建链条时保留同模型操作焦点");
                Assert.IsFalse(health.IsCooling(provider.Models[0].Id), "Reload 发布必须清掉旧配置冷却");
                Assert.IsFalse(FindIn<TextBlock>(view, "FailoverRowStatus").IsVisible, "恢复发布后旧红灯必须熄灭");
                Assert.HasCount(1, Find<ComboBox>(panel, "ProviderCombo").Items, "父模型下拉必须删除外部已移除项");
                Assert.AreEqual("keep model draft", modelSettings.GetControl<TextBox>("NameBox").Text);
                Assert.HasCount(2, modelSettings.GetControl<ListBox>("ProvidersList").Items);
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == loc["Saved"]));
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("explicit prompt draft", saved.SystemPrompt);
                Assert.IsFalse(saved.CompactContext);
                Assert.AreEqual("https://external.example", saved.WebSearch.SearxngBaseUrl);
                Assert.AreEqual(98765, saved.WebSearch.MaxFetchChars);
                Assert.AreEqual(65, saved.PanelWidthPercent);
                Assert.AreEqual("terminal_write", saved.DisabledBuiltinTools);
                Assert.AreEqual(provider.Models[0].Id, saved.FailoverChain.Single().ModelId);
                CollectionAssert.AreEqual(new[] { slot }, saved.Providers[0].AdditionalApiKeyIds);
                Assert.AreEqual("external-key", await store.GetApiKeyAsync(slot));
            }
            finally { settingsHost?.Close(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void Review_GlobalSaveThroughParentPublishesOnlyCommittedFields(string locale)
    {
        OnUi(async () =>
        {
            var storage = new GlobalSaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", "http://127.0.0.1:1/v1", "m");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                SystemPrompt = "original prompt", CompactContext = false, SuggestFollowUps = true,
                PanelWidthPercent = 37, DisabledBuiltinTools = "web_fetch",
                WebSearch = new WebSearchOptions { SearxngBaseUrl = "https://original.example", MaxResults = 9, MaxFetchChars = 234567 }
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                GlobalSettingsView view = await OpenGlobalViewAsync(context, panel);
                host = Host(view);
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch(locale);
                view.ApplyLoc();
                AiSettings live = PanelSettings(panel);
                string baseline = JsonSerializer.Serialize(live);
                FindIn<TextBox>(view, "SystemPromptBox").Text = "edited prompt";
                FindIn<CheckBox>(view, "CompactContextCheck").IsChecked = true;
                FindIn<CheckBox>(view, "SuggestFollowUpsCheck").IsChecked = false;
                FindIn<CheckBox>(view, "WebEnabledCheck").IsChecked = false;
                FindIn<TextBox>(view, "WebSearxUrlBox").Text = " https://edited.example ";
                FindIn<TextBox>(view, "WebMaxResultsBox").Text = "99";
                FindIn<CheckBox>(view, "WebNativeCheck").IsChecked = false;
                FindIn<CheckBox>(view, "WebPrivateCheck").IsChecked = true;
                FindIn<TextBox>(view, "WebAllowedHostsBox").Text = "internal.example";
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                Button save = FindIn<Button>(view, "SaveButton");
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                storage.FailWrites = true;
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && !string.IsNullOrEmpty(status.Text)));
                Assert.AreEqual($"{loc["Error"]}: Settings write failed", status.Text,
                    "真实父保存委托必须把存储失败交回全局页,不能显示已保存");
                Assert.AreEqual(baseline, JsonSerializer.Serialize(live), "失败不能发布链条或其它全局字段");
                Assert.AreEqual(baseline, JsonSerializer.Serialize(await store.LoadAsync()));
                Assert.AreEqual("edited prompt", FindIn<TextBox>(view, "SystemPromptBox").Text, "失败保留编辑态供重试");
                Assert.HasCount(1, NamedButtons(view, "FailoverRemoveButton"));

                panel.RememberPanelWidth(58); // 普通自动保存仍吸收异常,不产生未观察 Task 异常。
                await PumpAsync(5);
                Assert.AreEqual(58, live.PanelWidthPercent);
                Assert.AreEqual(37, (await store.LoadAsync()).PanelWidthPercent);
                storage.FailWrites = false;
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == loc["Saved"]));
                AiSettings persisted = await store.LoadAsync();
                Assert.AreEqual(JsonSerializer.Serialize(live), JsonSerializer.Serialize(persisted));
                Assert.AreEqual("edited prompt", persisted.SystemPrompt);
                Assert.IsTrue(persisted.CompactContext);
                Assert.IsFalse(persisted.SuggestFollowUps);
                Assert.IsFalse(persisted.WebSearch.Enabled);
                Assert.AreEqual("https://edited.example", persisted.WebSearch.SearxngBaseUrl);
                Assert.AreEqual(20, persisted.WebSearch.MaxResults);
                Assert.IsFalse(persisted.WebSearch.PreferProviderNative);
                Assert.IsTrue(persisted.WebSearch.AllowPrivateNetwork);
                Assert.AreEqual("internal.example", persisted.WebSearch.AllowedPrivateHosts);
                Assert.AreEqual(234567, persisted.WebSearch.MaxFetchChars, "页外全局字段不能被默认值覆盖");
                Assert.AreEqual(58, persisted.PanelWidthPercent);
                Assert.AreEqual("web_fetch", persisted.DisabledBuiltinTools);
                Assert.AreEqual(provider.Models[0].Id, persisted.FailoverChain.Single().ModelId);
            }
            finally { host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Review_GlobalSaveConcurrentPanelEditCannotBeOverwritten()
    {
        OnUi(async () =>
        {
            var storage = new GlobalSaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", "http://127.0.0.1:1/v1", "m");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id, SystemPrompt = "original prompt"
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                GlobalSettingsView view = await OpenGlobalViewAsync(context, panel);
                host = Host(view);
                FindIn<TextBox>(view, "SystemPromptBox").Text = "edited prompt";
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                storage.HoldNextWrite = true;
                Button save = FindIn<Button>(view, "SaveButton");
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => storage.Started.Task.IsCompleted));
                AiSettings live = PanelSettings(panel);
                Assert.AreEqual("original prompt", live.SystemPrompt, "等待存储时尚未发布草稿");
                Assert.IsEmpty(live.FailoverChain);
                panel.RememberPanelWidth(61);
                live.WebSearch.MaxFetchChars = 345678;
                storage.Release.TrySetResult();
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && !string.IsNullOrEmpty(status.Text)));
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                Assert.IsTrue(status.Text!.StartsWith(loc["Error"] + ":", StringComparison.Ordinal), "并发变化拒绝旧草稿并提示失败");
                AiSettings persisted = await store.LoadAsync();
                Assert.AreEqual("original prompt", live.SystemPrompt);
                Assert.IsEmpty(live.FailoverChain);
                Assert.AreEqual("original prompt", persisted.SystemPrompt);
                Assert.IsEmpty(persisted.FailoverChain);
                Assert.AreEqual(61, persisted.PanelWidthPercent);
                Assert.AreEqual(345678, persisted.WebSearch.MaxFetchChars);
                Assert.AreEqual("edited prompt", FindIn<TextBox>(view, "SystemPromptBox").Text);
                Assert.HasCount(1, NamedButtons(view, "FailoverRemoveButton"));
            }
            finally { storage.Release.TrySetResult(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void Review_GlobalSaveKeepsLaterFormAndChainDraftAcrossNextConflict(string locale)
    {
        OnUi(async () =>
        {
            var storage = new GlobalSaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", "http://127.0.0.1:1/v1", "m1");
            provider.Models.Add(new AiModelConfig { Model = "m2" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                SystemPrompt = "original prompt", PanelWidthPercent = 37,
                WebSearch = new WebSearchOptions { MaxFetchChars = 12345 }
            });
            await store.SetApiKeyAsync(provider.Id, "primary-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                GlobalSettingsView view = await OpenGlobalViewAsync(context, panel);
                host = Host(view);
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch(locale); view.ApplyLoc();
                TextBox prompt = FindIn<TextBox>(view, "SystemPromptBox");
                TextBox url = FindIn<TextBox>(view, "WebSearxUrlBox");
                TextBox max = FindIn<TextBox>(view, "WebMaxResultsBox");
                TextBox hosts = FindIn<TextBox>(view, "WebAllowedHostsBox");
                CheckBox compact = FindIn<CheckBox>(view, "CompactContextCheck");
                CheckBox followUps = FindIn<CheckBox>(view, "SuggestFollowUpsCheck");
                CheckBox web = FindIn<CheckBox>(view, "WebEnabledCheck");
                CheckBox native = FindIn<CheckBox>(view, "WebNativeCheck");
                CheckBox privateNetwork = FindIn<CheckBox>(view, "WebPrivateCheck");
                ComboBox add = FindIn<ComboBox>(view, "FailoverAddCombo");
                Button save = FindIn<Button>(view, "SaveButton");
                TextBlock status = FindIn<TextBlock>(view, "StatusText");

                prompt.Text = "submitted A";
                compact.IsChecked = true;
                followUps.IsChecked = false;
                web.IsChecked = true;
                url.Text = " https://submitted.example ";
                max.Text = "99";
                native.IsChecked = true;
                privateNetwork.IsChecked = true;
                hosts.Text = "submitted.internal";
                add.SelectedIndex = 0;
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                storage.HoldNextWrite = true;
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => storage.Started.Task.IsCompleted));
                Assert.IsFalse(save.IsEnabled);
                Assert.IsTrue(prompt.IsEnabled && compact.IsEnabled && add.IsEnabled,
                    "落盘只禁保存,后续草稿仍可编辑");
                Assert.AreEqual("20", max.Text, "提交快照包含本次夹取后的真实表单值");

                prompt.Text = "later B";
                compact.IsChecked = false;
                followUps.IsChecked = true;
                url.Text = "https://later.example";
                max.Text = "7";
                native.IsChecked = false;
                privateNetwork.IsChecked = false;
                hosts.Text = "later.internal";
                web.IsChecked = false;
                RaiseClick(NamedButtons(view, "FailoverRemoveButton").Single());
                add.SelectedIndex = 1;
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                Button removeB = NamedButtons(view, "FailoverRemoveButton").Single();
                Assert.IsTrue(removeB.Focus());
                storage.Release.TrySetResult();
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled));
                Assert.AreEqual("", status.Text, "A落盘不能声称等待期间新写的B草稿也已保存");
                Assert.AreSame(removeB, NamedButtons(view, "FailoverRemoveButton").Single());
                Assert.IsTrue(removeB.IsFocused, "完成旧提交不能抢走后续草稿的操作焦点");

                void AssertDraftB()
                {
                    Assert.AreEqual("later B", prompt.Text);
                    Assert.AreEqual(false, compact.IsChecked);
                    Assert.AreEqual(true, followUps.IsChecked);
                    Assert.AreEqual(false, web.IsChecked);
                    Assert.AreEqual("https://later.example", url.Text);
                    Assert.AreEqual("7", max.Text);
                    Assert.AreEqual(false, native.IsChecked);
                    Assert.AreEqual(false, privateNetwork.IsChecked);
                    Assert.AreEqual("later.internal", hosts.Text);
                    Assert.AreEqual(provider.Models[1].Id, FindIn<Border>(view, "FailoverRow").Tag);
                    Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton").Single().IsFocused);
                }
                AssertDraftB();
                AiSettings committed = await store.LoadAsync();
                Assert.AreEqual("submitted A", committed.SystemPrompt);
                Assert.IsTrue(committed.CompactContext);
                Assert.IsFalse(committed.SuggestFollowUps);
                Assert.IsTrue(committed.WebSearch.Enabled);
                Assert.AreEqual("https://submitted.example", committed.WebSearch.SearxngBaseUrl);
                Assert.AreEqual(20, committed.WebSearch.MaxResults);
                Assert.IsTrue(committed.WebSearch.PreferProviderNative);
                Assert.IsTrue(committed.WebSearch.AllowPrivateNetwork);
                Assert.AreEqual("submitted.internal", committed.WebSearch.AllowedPrivateHosts);
                Assert.AreEqual(provider.Models[0].Id, committed.FailoverChain.Single().ModelId);
                Assert.AreEqual("submitted A", PanelSettings(panel).SystemPrompt);

                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                other.SystemPrompt = "external C";
                other.WebSearch.SearxngBaseUrl = "https://external.example";
                other.WebSearch.MaxResults = 3;
                other.WebSearch.AllowedPrivateHosts = "external.internal";
                other.WebSearch.MaxFetchChars = 98765;
                other.PanelWidthPercent = 66;
                other.FailoverChain.Clear();
                await otherStore.AddProviderApiKeyAsync(other, other.Providers[0], "external-key");
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled
                    && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                AssertDraftB();
                Assert.AreEqual("external C", PanelSettings(panel).SystemPrompt);
                Assert.AreEqual("external C", (await store.LoadAsync()).SystemPrompt,
                    "第二次保存真实冲突后不得擅自覆盖外部已提交配置");

                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == loc["Saved"]));
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("later B", saved.SystemPrompt);
                Assert.IsFalse(saved.CompactContext);
                Assert.IsTrue(saved.SuggestFollowUps);
                Assert.IsFalse(saved.WebSearch.Enabled);
                Assert.AreEqual("https://later.example", saved.WebSearch.SearxngBaseUrl);
                Assert.AreEqual(7, saved.WebSearch.MaxResults);
                Assert.IsFalse(saved.WebSearch.PreferProviderNative);
                Assert.IsFalse(saved.WebSearch.AllowPrivateNetwork);
                Assert.AreEqual("later.internal", saved.WebSearch.AllowedPrivateHosts);
                Assert.AreEqual(provider.Models[1].Id, saved.FailoverChain.Single().ModelId);
                Assert.AreEqual(98765, saved.WebSearch.MaxFetchChars);
                Assert.AreEqual(66, saved.PanelWidthPercent);
            }
            finally { storage.Release.TrySetResult(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void Review_GlobalConflictRetainsInputAndChainEditedWhileReloadIsWaiting(string locale)
    {
        OnUi(async () =>
        {
            var storage = new GlobalSaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", "http://127.0.0.1:1/v1", "m1");
            provider.Models.Add(new AiModelConfig { Model = "m2" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                SystemPrompt = "original", CompactContext = true,
                FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                GlobalSettingsView view = await OpenGlobalViewAsync(context, panel);
                host = Host(view);
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch(locale); view.ApplyLoc();
                var external = new AiSettingsStore(context);
                AiSettings newer = await external.LoadAsync();
                newer.SystemPrompt = "external";
                newer.CompactContext = false;
                newer.WebSearch.SearxngBaseUrl = "https://external.example";
                await external.SaveAsync(newer);
                TextBox prompt = FindIn<TextBox>(view, "SystemPromptBox");
                prompt.Text = "submitted draft";
                Button save = FindIn<Button>(view, "SaveButton");
                storage.SettingsReadsUntilHold = 2; // 校验快照后，第二次读取才是冲突 Reload。
                RaiseClick(save);
                await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(save.IsEnabled);
                prompt.Text = "typed during reload";
                FindIn<CheckBox>(view, "WebEnabledCheck").IsChecked = true;
                FindIn<TextBox>(view, "WebAllowedHostsBox").Text = "new.internal";
                RaiseClick(NamedButtons(view, "FailoverRemoveButton").Single());
                FindIn<ComboBox>(view, "FailoverAddCombo").SelectedIndex = 1;
                RaiseClick(FindIn<Button>(view, "FailoverAddButton"));
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton").Single().Focus());
                storage.ReadRelease.TrySetResult();
                TextBlock status = FindIn<TextBlock>(view, "StatusText");
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled
                    && status.Text == $"{loc["Error"]}: {loc["SetupConfigChanged"]}"));
                Assert.AreEqual("typed during reload", prompt.Text);
                Assert.AreEqual(true, FindIn<CheckBox>(view, "WebEnabledCheck").IsChecked);
                Assert.AreEqual("new.internal", FindIn<TextBox>(view, "WebAllowedHostsBox").Text);
                Assert.AreEqual(false, FindIn<CheckBox>(view, "CompactContextCheck").IsChecked);
                Assert.AreEqual("https://external.example", FindIn<TextBox>(view, "WebSearxUrlBox").Text);
                Assert.AreEqual(provider.Models[1].Id, FindIn<Border>(view, "FailoverRow").Tag);
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton").Single().IsFocused);
                RaiseClick(save);
                Assert.IsTrue(await WaitForAsync(() => save.IsEnabled && status.Text == loc["Saved"]));
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("typed during reload", saved.SystemPrompt);
                Assert.IsTrue(saved.WebSearch.Enabled);
                Assert.AreEqual("new.internal", saved.WebSearch.AllowedPrivateHosts);
                Assert.IsFalse(saved.CompactContext);
                Assert.AreEqual("https://external.example", saved.WebSearch.SearxngBaseUrl);
                Assert.AreEqual(provider.Models[1].Id, saved.FailoverChain.Single().ModelId);
            }
            finally { storage.ReadRelease.TrySetResult(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_GlobalReloadCallbackMergesEveryUntouchedModelFieldOrRetainsRemovedModelDraft(bool removed)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var first = new AiModelConfig
            {
                Name = "original A", Model = "old-a", HasOwnApiKey = true,
                BaseUrlOverride = "https://old-model.example", Temperature = 0.2f,
                TopP = 0.3f, StopSequences = "old-stop", SystemPrompt = "old-model-prompt"
            };
            var second = new AiModelConfig { Name = "B", Model = "model-b" };
            var provider = new AiProvider { Name = "owner", BaseUrl = "https://provider.example", Models = [first, second], ModelsExpanded = true };
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = first.Id });
            await store.SetApiKeyAsync(first.Id, "original-own-key");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? settingsHost = null;
            Window? globalHost = null;
            try
            {
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var modelSettings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = Host(modelSettings);
                Assert.IsTrue(await WaitForAsync(() => modelSettings.GetControl<TextBox>("ApiKeyBox").Text == "original-own-key"));
                modelSettings.GetControl<TextBox>("NameBox").Text = "keep A name draft";
                modelSettings.GetControl<TextBox>("MaxTokensBox").Text = "3333";
                modelSettings.GetControl<TextBox>("ApiKeyBox").Text = "keep A own-key draft";
                Assert.IsTrue(modelSettings.GetControl<TextBox>("ApiKeyBox").Focus());
                context.FakeUi.LastPanel.Options.TitleActions.Single().OnClick();
                await PumpAsync(5);
                var global = (GlobalSettingsView)context.FakeUi.LastPanel.CreateContent();
                globalHost = Host(global);
                FindIn<TextBox>(global, "SystemPromptBox").Text = "keep global draft";
                var external = new AiSettingsStore(context);
                AiSettings newer = await external.LoadAsync();
                if (removed) newer.Providers[0].Models.RemoveAt(0);
                else
                {
                    AiModelConfig latest = newer.Providers[0].Models[0];
                    latest.Name = "external A name";
                    latest.Model = "latest-a";
                    latest.Protocol = ChatProtocol.AnthropicMessages;
                    latest.BaseUrlOverride = "https://latest-model.example";
                    latest.MaxTokens = 5555;
                    latest.MaxInputTokens = 65000;
                    latest.Reasoning = ReasoningLevel.High;
                    latest.PromptCaching = false;
                    latest.Temperature = 0.7f;
                    latest.TopP = 0.8f;
                    latest.StopSequences = "latest-stop";
                    latest.InputPricePerMillion = 2;
                    latest.OutputPricePerMillion = 3;
                    latest.CachedInputPricePerMillion = 1;
                    latest.SystemPrompt = "latest-model-prompt";
                    newer.Providers[0].AvailableModels = ["latest-a", "model-b"];
                }
                await external.SaveAsync(newer);
                if (!removed) await external.SetApiKeyAsync(first.Id, "external-own-key");
                RaiseClick(FindIn<Button>(global, "SaveButton"));
                TextBlock globalStatus = FindIn<TextBlock>(global, "StatusText");
                Assert.IsTrue(await WaitForAsync(() => FindIn<Button>(global, "SaveButton").IsEnabled
                    && (globalStatus.Text ?? "").Contains(new Loc("en")["SetupConfigChanged"], StringComparison.Ordinal)));
                await PumpAsync(5);
                Assert.AreEqual("keep global draft", FindIn<TextBox>(global, "SystemPromptBox").Text);
                Assert.AreEqual("keep A name draft", modelSettings.GetControl<TextBox>("NameBox").Text);
                Assert.AreEqual("3333", modelSettings.GetControl<TextBox>("MaxTokensBox").Text);
                Assert.AreEqual("keep A own-key draft", modelSettings.GetControl<TextBox>("ApiKeyBox").Text);
                ListBox nav = modelSettings.GetControl<ListBox>("ProvidersList");
                if (removed)
                {
                    Assert.AreEqual(-1, nav.SelectedIndex);
                    foreach (string button in new[] { "SaveButton", "TestButton", "DeleteButton", "AddModelButton" })
                        Assert.IsFalse(modelSettings.GetControl<Button>(button).IsEnabled);
                    Assert.IsTrue(modelSettings.GetControl<StackPanel>("ModelEditor").IsVisible);
                    Assert.AreEqual("old-a", modelSettings.GetControl<TextBox>("ModelBox").Text);
                    RaiseClick(modelSettings.GetControl<Button>("SaveButton"));
                    await PumpAsync(5);
                    Assert.AreEqual("B", (await store.LoadAsync()).Providers[0].Models.Single().Name);
                    nav.SelectedItem = nav.ItemsSource!.Cast<ProviderNavItem>().Single(row => row.Model?.Id == second.Id);
                    await PumpAsync(5);
                    Assert.AreEqual("B", modelSettings.GetControl<TextBox>("NameBox").Text);
                    Assert.AreEqual("", modelSettings.GetControl<TextBox>("ApiKeyBox").Text);
                    modelSettings.GetControl<TextBox>("NameBox").Text = "B explicit navigation draft";
                    RaiseClick(modelSettings.GetControl<Button>("SaveButton"));
                    Assert.IsTrue(await WaitForAsync(() => modelSettings.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]));
                    Assert.AreEqual("B explicit navigation draft", (await store.LoadAsync()).Providers[0].Models.Single().Name);
                    Assert.AreEqual("original-own-key", await store.GetApiKeyAsync(first.Id));
                }
                else
                {
                    Assert.AreEqual("latest-a", modelSettings.GetControl<TextBox>("ModelBox").Text);
                    Assert.AreEqual("latest-a", modelSettings.GetControl<ComboBox>("ModelPickCombo").SelectedItem);
                    Assert.AreEqual((int)ChatProtocol.AnthropicMessages + 1, modelSettings.GetControl<ComboBox>("ProtocolCombo").SelectedIndex);
                    Assert.AreEqual(true, modelSettings.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                    Assert.AreEqual("https://latest-model.example", modelSettings.GetControl<TextBox>("BaseUrlBox").Text);
                    Assert.AreEqual("65000", modelSettings.GetControl<TextBox>("MaxInputTokensBox").Text);
                    Assert.AreEqual((int)ReasoningLevel.High, modelSettings.GetControl<ComboBox>("ReasoningCombo").SelectedIndex);
                    Assert.AreEqual(false, modelSettings.GetControl<CheckBox>("PromptCacheCheck").IsChecked);
                    Assert.AreEqual(0.7f.ToString(), modelSettings.GetControl<TextBox>("TemperatureBox").Text);
                    Assert.AreEqual(0.8f.ToString(), modelSettings.GetControl<TextBox>("TopPBox").Text);
                    Assert.AreEqual("latest-stop", modelSettings.GetControl<TextBox>("StopBox").Text);
                    Assert.AreEqual("2", modelSettings.GetControl<TextBox>("PriceInBox").Text);
                    Assert.AreEqual("3", modelSettings.GetControl<TextBox>("PriceOutBox").Text);
                    Assert.AreEqual("1", modelSettings.GetControl<TextBox>("PriceCachedBox").Text);
                    Assert.AreEqual("latest-model-prompt", modelSettings.GetControl<TextBox>("ProviderPromptBox").Text);
                    RaiseClick(modelSettings.GetControl<Button>("SaveButton"));
                    Assert.IsTrue(await WaitForAsync(() => modelSettings.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]));
                    AiModelConfig committed = (await store.LoadAsync()).Providers[0].Models[0];
                    Assert.AreEqual("keep A name draft", committed.Name);
                    Assert.AreEqual("latest-a", committed.Model);
                    Assert.AreEqual(3333, committed.MaxTokens);
                    Assert.AreEqual("https://latest-model.example", committed.BaseUrlOverride);
                    Assert.AreEqual(0.7f, committed.Temperature);
                    Assert.AreEqual(0.8f, committed.TopP);
                    Assert.AreEqual("latest-stop", committed.StopSequences);
                    Assert.AreEqual("latest-model-prompt", committed.SystemPrompt);
                    Assert.AreEqual("keep A own-key draft", await store.GetApiKeyAsync(first.Id));
                }
            }
            finally { globalHost?.Close(); settingsHost?.Close(); panel.Detach(); window.Close(); }
        });
    }

    private static async Task<GlobalSettingsView> OpenGlobalViewAsync(TestPluginContext context, ChatPanelView panel)
    {
        Click(panel, "SettingsButton");
        await PumpAsync(5);
        context.FakeUi.LastPanel.Options.TitleActions.Single().OnClick();
        await PumpAsync(5);
        return (GlobalSettingsView)context.FakeUi.LastPanel.CreateContent();
    }

    private sealed class GlobalSaveStorage(IPluginStorage inner) : IPluginStorage
    {
        public bool FailWrites { get; set; }
        public bool HoldNextWrite { get; set; }
        public int SettingsReadsUntilHold { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            if (key == "settings" && SettingsReadsUntilHold > 0 && --SettingsReadsUntilHold == 0)
            {
                ReadStarted.TrySetResult();
                await ReadRelease.Task.WaitAsync(cancellationToken);
            }
            return await inner.GetAsync<T>(key, cancellationToken);
        }

        public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            if (key == "settings" && HoldNextWrite)
            {
                HoldNextWrite = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (key == "settings" && FailWrites) throw new IOException("Settings write failed");
            await inner.SetAsync(key, value, cancellationToken);
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default)
            => inner.GetKeysAsync(cancellationToken);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(429)]
    public void Review_GlobalProbeAuthenticationFailureOnlyCoolsTheProbedSlot(int statusCode)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-a"
                ? (statusCode, "{\"error\":{\"message\":\"auth\"}}") : (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }] };
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var health = new ProviderHealth();
            var view = new GlobalSettingsView(context, store, settings, new Loc("en"), _ => Task.CompletedTask, _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => endpoint.Requests.Count > 0 && probe.IsEnabled));
                Assert.IsFalse(health.IsCooling(provider.Models[0].Id), "一把 Key 的鉴权失败不能跳过仍有备用 Key 的整个模型");
                Assert.IsTrue(health.IsKeyCooling(provider.Id));
                Assert.IsFalse(health.IsKeyCooling(b));
                CollectionAssert.AreEqual(new[] { "Bearer key-a" }, endpoint.Requests.Select(request => request.Auth).ToArray());
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_CompletedGlobalProbeIsInvalidatedByActiveSlotOrSecretChange(bool replaceSecret)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }] };
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var health = new ProviderHealth();
            var view = new GlobalSettingsView(context, store, settings, new Loc("en"), _ => Task.CompletedTask, _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Button remove = NamedButtons(view, "FailoverRemoveButton")[0];
                Assert.IsTrue(remove.Focus());
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                RaiseClick(probe);
                TextBlock result = FindIn<TextBlock>(view, "FailoverRowStatus");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Requests.Count == 1 && probe.IsEnabled && result.IsVisible));
                if (replaceSecret) await store.ReplaceProviderApiKeyAsync(settings, provider, provider.Id, "replacement");
                else { provider.ActiveApiKeyId = b; await store.SaveAsync(settings); }
                Assert.AreEqual(0, health.Version, "不靠全局清证据掩盖 active 切换");
                Assert.IsTrue(await WaitForAsync(() => !result.IsVisible), "心跳必须撤掉属于旧 active/旧 Secret 的绿灯");
                Assert.HasCount(1, endpoint.Requests, "仅复验凭据,不能再发付费探活");
                Assert.AreSame(remove, NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.IsTrue(remove.IsFocused);
                foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
                {
                    var loc = (Loc)typeof(GlobalSettingsView).GetField("_loc",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
                    loc.Switch(locale); view.ApplyLoc();
                    Assert.AreEqual("", FindIn<TextBlock>(view, "FailoverStatusText").Text,
                        "旧证据撤掉后，语言切换不能复活旧成功汇总");
                    Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton").Single().IsFocused);
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en", false)]
    [DataRow("zh-Hans", false)]
    [DataRow("zh-Hant", false)]
    [DataRow("ja", false)]
    [DataRow("ko", false)]
    [DataRow("en", true)]
    [DataRow("zh-Hans", true)]
    [DataRow("zh-Hant", true)]
    [DataRow("ja", true)]
    [DataRow("ko", true)]
    public void Review_GlobalProbeLocalizationBeforeHeartbeatClearsInvalidResultAndSummary(string locale, bool changeVersion)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings
            {
                Providers = [provider], FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string backup = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var health = new ProviderHealth();
            var loc = new Loc(locale == "en" ? "zh-Hans" : "en");
            var view = new GlobalSettingsView(context, store, settings, loc,
                draft => store.SaveGlobalSettingsAsync(settings, draft), _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton").Single().Focus());
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                TextBlock summary = FindIn<TextBlock>(view, "FailoverStatusText");
                var results = (System.Collections.IDictionary)typeof(GlobalSettingsView).GetField("_probeResults",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled && endpoint.Requests.Count == 1
                    && summary.Text == loc.F("FailoverProbeDone", 1, 0)));
                Assert.AreEqual("Bearer key-a", endpoint.Requests.Single().Auth);
                Assert.AreEqual(1, results.Count);

                health.Record(provider.Models[0].Id, ok: false);
                view.ApplyLoc();
                Assert.IsTrue(FindIn<TextBlock>(view, "FailoverRowStatus").Text!.StartsWith(loc["DotPassed"], StringComparison.Ordinal),
                    "有效的手动历史仍优先于被动冷却,本修复不能扩大失效策略");
                Assert.AreEqual(loc.F("FailoverProbeDone", 1, 0), summary.Text);

                // 不让出 UI 线程:语言重建先遇到失效证据,不能靠稍后心跳替它清汇总。
                if (changeVersion) health.Clear();
                else provider.ActiveApiKeyId = backup;
                loc.Switch(locale); view.ApplyLoc();
                Assert.AreEqual(0, results.Count, "语言重建立即移除旧 active 或旧健康代际的结果");
                Assert.AreEqual("", summary.Text, "结果和批次汇总必须在同一个失效入口撤掉");
                TextBlock row = FindIn<TextBlock>(view, "FailoverRowStatus");
                if (changeVersion) Assert.IsFalse(row.IsVisible);
                else Assert.AreEqual(loc["DotCooling"], row.Text);
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton").Single().IsFocused);
                await PumpAsync(250);
                Assert.AreEqual(0, results.Count);
                Assert.AreEqual("", summary.Text, "心跳没有剩余结果可复验时也不能留下永久旧汇总");
                Assert.HasCount(1, endpoint.Requests, "语言重建与心跳复验都不能重放真实探活请求");
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton").Single().IsFocused);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void Review_GlobalProbeSecretReadFailureDoesNotInventModelHealthEvidence()
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var source = new TestPluginContext();
            using var context = new TestPluginContext { Secrets = new UnreadableProbeSecrets(source.Secrets) };
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings { Providers = [provider], FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }] };
            await store.SaveAsync(settings);
            var health = new ProviderHealth();
            var view = new GlobalSettingsView(context, store, settings, new Loc("en"), _ => Task.CompletedTask, _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled && FindIn<TextBlock>(view, "FailoverRowStatus").IsVisible));
                Assert.IsFalse(health.IsCooling(provider.Models[0].Id), "解密失败不等于服务端不健康");
                Assert.IsFalse(health.IsKeyCooling(provider.Id));
                Assert.IsEmpty(endpoint.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_GlobalProbeSecretReadFailureIsInvalidatedWhenActiveSlotChanges(bool explicitPrimary)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var source = new TestPluginContext();
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            string backupId = Guid.NewGuid().ToString("N");
            provider.AdditionalApiKeyIds = [backupId];
            provider.ActiveApiKeyId = explicitPrimary ? provider.Id : null;
            await source.Secrets.SetAsync($"apikey:{backupId}", "key-b");
            using var context = new TestPluginContext
            {
                Secrets = new UnreadableProbeSecrets(source.Secrets, $"apikey:{provider.Id}")
            };
            var store = new AiSettingsStore(context);
            var settings = new AiSettings
            {
                Providers = [provider], FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            await store.SaveAsync(settings);
            var health = new ProviderHealth();
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, store, settings, loc, _ => Task.CompletedTask, _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Button remove = NamedButtons(view, "FailoverRemoveButton")[0];
                Assert.IsTrue(remove.Focus());
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                TextBlock status = FindIn<TextBlock>(view, "FailoverRowStatus");
                var results = (System.Collections.IDictionary)typeof(GlobalSettingsView).GetField("_probeResults",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled && status.IsVisible));
                Assert.AreEqual(1, results.Count);
                Assert.IsTrue(status.Text!.StartsWith(loc["DotFailed"], StringComparison.Ordinal));
                Assert.IsFalse(health.IsCooling(provider.Models[0].Id));
                Assert.IsFalse(health.IsKeyCooling(provider.Id));
                Assert.IsEmpty(endpoint.Requests);
                await PumpAsync(250);
                Assert.IsTrue(status.IsVisible, "active未变时保留本次本地读凭据失败,不捏造重探");

                provider.ActiveApiKeyId = backupId;
                await store.SaveAsync(settings);
                Assert.AreEqual("key-b", (await store.ResolveCredentialWithKeyIdAsync(settings.FindModel(provider.Models[0].Id)!)).Credential.Value);
                Assert.AreEqual(0, health.Version, "不能靠清健康代际掩盖active归属失效");
                Assert.IsTrue(await WaitForAsync(() => !status.IsVisible && results.Count == 0),
                    "Credential/KeyId为空的失败结果同样属于旧active,心跳必须撤灯并移除证据");
                Assert.IsEmpty(endpoint.Requests, "复验不能发付费探活");
                Assert.AreSame(remove, NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.IsTrue(remove.IsFocused);
                foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
                {
                    loc.Switch(locale);
                    view.ApplyLoc();
                    Assert.IsFalse(FindIn<TextBlock>(view, "FailoverRowStatus").IsVisible);
                    Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton")[0].IsFocused);
                }
                await PumpAsync(250);
                Assert.AreEqual(0, results.Count, "后续心跳不能恢复旧active的失败证据");
                Assert.IsFalse(FindIn<TextBlock>(view, "FailoverRowStatus").IsVisible);
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled && endpoint.Requests.Count == 1));
                Assert.IsTrue(FindIn<TextBlock>(view, "FailoverRowStatus").Text!.StartsWith(loc["DotPassed"], StringComparison.Ordinal));
                Assert.AreEqual("Bearer key-b", endpoint.Requests.Single().Auth);
            }
            finally { window.Close(); }
        });
    }

    private sealed class UnreadableProbeSecrets(ISecretsApi inner, string? unreadableName = null) : ISecretsApi
    {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
            => unreadableName is null || name == unreadableName
                ? Task.FromException<string?>(new IOException("Secret cannot be read"))
                : inner.GetAsync(name, cancellationToken);
        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
            => inner.SetAsync(name, value, cancellationToken);
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(name, cancellationToken);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void KeyFailover_GlobalProbeDiscardsLateActiveSlotResultEvenWhenKeyBytesMatch(bool sameBytes, bool fails)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => fails
                ? (401, "{\"error\":{\"message\":\"old active failed\"}}") : (200, LifecycleAnswer), "Bearer key-a");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            var settings = new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            await store.SetApiKeyAsync(provider.Id, "key-a");
            await store.SaveAsync(settings);
            string second = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            if (sameBytes) await store.SetApiKeyAsync(second, "key-a"); // 故意构造历史槽数据,ID 才是验证身份。
            var health = new ProviderHealth();
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, store, settings, loc, _ => Task.CompletedTask, _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Button remove = NamedButtons(view, "FailoverRemoveButton")[0];
                Assert.IsTrue(remove.Focus());
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => endpoint.Held.Task.IsCompleted));
                Assert.HasCount(1, endpoint.Requests, "检测全部逐模型仅探当前 active,不轮池");
                provider.ActiveApiKeyId = second;
                await store.SaveAsync(settings);
                Assert.AreEqual(0, health.Version, "只改 active 不靠清健康代际掩盖竞态");
                endpoint.Release();
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled));
                TextBlock status = FindIn<TextBlock>(view, "FailoverRowStatus");
                Assert.IsFalse(status.IsVisible, "旧 active 的迟到成功或401都不得点亮新 active 的行灯");
                Assert.IsTrue(string.IsNullOrEmpty(status.Text));
                Assert.IsFalse(health.IsCooling(provider.Models[0].Id));
                Assert.IsFalse(health.IsKeyCooling(provider.Id));
                Assert.AreSame(remove, NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.IsTrue(remove.IsFocused);
                foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
                {
                    loc.Switch(locale);
                    view.ApplyLoc();
                    Assert.IsFalse(FindIn<TextBlock>(view, "FailoverRowStatus").IsVisible);
                    Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton")[0].IsFocused);
                }
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void FailoverActions_AutomationNamesFollowLocale()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("one", "http://127.0.0.1:1/v1", "m");
            var settings = new AiSettings
            {
                Providers = [provider],
                FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, loc,
                _ => Task.CompletedTask, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                foreach (string locale in new[] { "en", "zh-Hans" })
                {
                    loc.Switch(locale);
                    view.ApplyLoc();
                    foreach (string action in new[] { "FailoverUp", "FailoverDown", "FailoverRemove" })
                    {
                        Button button = NamedButtons(view, action + "Button")[0];
                        Assert.AreEqual(loc[action], new ButtonAutomationPeer(button).GetName(),
                            $"{locale}: 屏幕阅读器必须读出操作名称,而不是图形控件类型");
                    }
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void FailoverAddCombo_SelectedCandidateHasLocalizedPurpose()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("one", "http://127.0.0.1:1/v1", "candidate");
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, new AiSettingsStore(context),
                new AiSettings { Providers = [provider] }, loc, _ => Task.CompletedTask, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                ComboBox combo = FindIn<ComboBox>(view, "FailoverAddCombo");
                foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
                {
                    loc.Switch(locale);
                    view.ApplyLoc();
                    Assert.IsTrue(combo.IsEnabled);
                    Assert.AreEqual("candidate", combo.SelectedItem);
                    Assert.AreEqual(loc["FailoverAddModel"], AutomationProperties.GetName(combo));
                    AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(combo);
                    Assert.IsTrue(peer.IsControlElement());
                    Assert.AreEqual(loc["FailoverAddModel"], peer.GetName(),
                        "候选已选中时,读屏仍应说明这个选择器用于加入故障转移链");
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void FailoverProbe_ExposesFailureAndTimeWithoutLosingRowFocus()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var healthy = new SseStub("", firstStreamContent: "OK");
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("外包", broken.BaseUrl, "global.deepseek-v4.1-flash-sg-with-long-model-name");
            AiProvider second = StubProvider("WinClaw", healthy.BaseUrl, "Qwen3.6-35b-a3b-nvfp4-with-long-model-name");
            var settings = new AiSettings
            {
                Providers = [first, second],
                FailoverChain = [new FailoverEntry { ModelId = first.Models[0].Id },
                                 new FailoverEntry { ModelId = second.Models[0].Id }]
            };
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, loc,
                _ => Task.CompletedTask, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Button remove = NamedButtons(view, "FailoverRemoveButton")[0];
                Assert.IsTrue(remove.Focus());
                DateTime started = DateTime.Now;
                Button probe = FindIn<Button>(view, "FailoverProbeAllButton");
                RaiseClick(probe);
                Assert.IsTrue(await WaitForAsync(() => probe.IsEnabled));
                DateTime finished = DateTime.Now;
                Assert.AreEqual(1, broken.Requests, "失败状态必须来自实际 HTTP 探测");
                Assert.HasCount(1, healthy.Requests);
                Assert.AreSame(remove, NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.IsTrue(remove.IsFocused, "探测完成不能替换行按钮或抢走键盘焦点");

                var statuses = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.Name == "FailoverRowStatus").ToList();
                string failedTime = statuses[0].Text!.Split(" · ")[1];
                Assert.IsTrue(failedTime == started.ToString("HH:mm") || failedTime == finished.ToString("HH:mm"),
                    "可读状态必须包含本次检测的时刻");
                string passedTime = statuses[1].Text!.Split(" · ")[1];
                foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
                {
                    loc.Switch(locale);
                    view.ApplyLoc();
                    statuses = view.GetVisualDescendants().OfType<TextBlock>()
                        .Where(block => block.Name == "FailoverRowStatus").ToList();
                    for (int i = 0; i < statuses.Count; i++)
                    {
                        Assert.IsTrue(statuses[i].IsVisible, "键盘用户无需悬停即可看到状态和时间");
                        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(statuses[i]);
                        Assert.IsTrue(peer.IsControlElement(), "状态必须出现在读屏可读控件上,不能只命名装饰 Path");
                        Assert.AreEqual($"{loc[i == 0 ? "DotFailed" : "DotPassed"]} · {(i == 0 ? failedTime : passedTime)}",
                            peer.GetName());
                    }
                    Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton")[0].IsFocused,
                        "语言切换后焦点仍属于同一模型的行按钮");
                }
                foreach (double width in new[] { 420d, 640d, 900d })
                {
                    window.Width = width;
                    await PumpAsync(5);
                    foreach (Border row in view.GetVisualDescendants().OfType<Border>()
                        .Where(control => control.Name == "FailoverRow"))
                    {
                        var grid = (Grid)row.Child!;
                        var dot = grid.Children.OfType<Avalonia.Controls.Shapes.Path>().Single();
                        Button up = grid.Children.OfType<Button>().Single(button => button.Name == "FailoverUpButton");
                        Assert.IsTrue(dot.Bounds.Right + dot.StrokeThickness / 2 < up.Bounds.Left,
                            $"{width}: 真实检测结果描边必须与行尾按钮分离，不被按钮边界遮挡");
                    }
                }

                first.BaseUrl = healthy.BaseUrl;
                view.RefreshFromProviders();
                Assert.IsTrue(view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.Name == "FailoverRowStatus")
                    .All(block => !block.IsVisible && string.IsNullOrEmpty(block.Text)),
                    "配置改变后不能继续暴露属于旧配置的检测结果和时间");
                Assert.IsTrue(NamedButtons(view, "FailoverRemoveButton")[0].IsFocused);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void FailoverCooling_ExposesNewHealthFailureWithoutRebuildingRows()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("one", "http://127.0.0.1:1/v1", "m");
            var settings = new AiSettings
            {
                Providers = [provider],
                FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            var health = new ProviderHealth();
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, loc,
                _ => Task.CompletedTask, _ => { }, health);
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                TextBlock status = FindIn<TextBlock>(view, "FailoverRowStatus");
                Button remove = NamedButtons(view, "FailoverRemoveButton")[0];
                Assert.IsTrue(remove.Focus());
                health.Record(provider.Models[0].Id, ok: false);
                Assert.IsTrue(await WaitForAsync(() => status.IsVisible));
                AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(status);
                Assert.IsTrue(peer.IsControlElement());
                Assert.AreEqual(loc["DotCooling"], peer.GetName());
                Assert.AreSame(remove, NamedButtons(view, "FailoverRemoveButton")[0]);
                Assert.IsTrue(remove.IsFocused);
                health.Record(provider.Models[0].Id, ok: true);
                Assert.IsTrue(await WaitForAsync(() => !status.IsVisible));
                Assert.IsTrue(string.IsNullOrEmpty(peer.GetName()), "恢复后不继续朗读已经失效的冷却状态");
            }
            finally { window.Close(); }
        });
    }
}
