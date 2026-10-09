using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Secrets;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 设置页(供应商 › 模型两层)的 headless 装载:左栏行序、选中层切换右侧表单、
/// 新增供应商 / 模型与保存的落盘结果。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SettingsViewUiTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_DraftModelInheritanceUsesOnlyPrimaryCredentialOnAnotherOrigin(bool savedOwnKey)
    {
        OnUi(async () =>
        {
            using var original = new SseStub(ProbeAnswerSse);
            using var draftHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "m", HasOwnApiKey = savedOwnKey };
            var provider = new AiProvider { BaseUrl = original.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            provider.ActiveApiKeyId = await store.AddProviderApiKeyAsync(settings, provider, "supplementary");
            await store.SetApiKeyAsync(model.Id, "saved-own");
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 1;
                await PumpAsync();
                view.GetControl<CheckBox>("OwnKeyCheck").IsChecked = false;
                view.GetControl<TextBox>("BaseUrlBox").Text = draftHost.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer primary", draftHost.Authorizations.Single());
                Assert.IsEmpty(original.Requests);
                Assert.AreEqual(savedOwnKey, model.HasOwnApiKey);
                Assert.IsNull(model.BaseUrlOverride);
                Assert.AreEqual("saved-own", await store.GetApiKeyAsync(model.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void DefaultContextWindow_ShowsLocalizedVerificationHintAndDoesNotMislabelRealOrManualValues(string language)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            var catalog = new ModelsDevCatalog(context);
            var provider = new AiProvider();
            ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-model", 0)]));
            AiModelConfig model = provider.Models.Single();
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc(language);
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                TextBox input = view.GetControl<TextBox>("MaxInputTokensBox");
                TextBlock hint = view.GetControl<TextBlock>("MaxInputTokensHintText");
                Assert.Contains(loc["ContextWindowDefaultHint"], hint.Text!);
                Assert.Contains(loc["ContextWindowDefaultHint"], ToolTip.GetTip(input)?.ToString() ?? "");
                input.Text = "65536";
                await WaitUntilAsync(() => hint.Text?.Contains(loc["ContextWindowDefaultHint"]) == false);
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], hint.Text!);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                model = (await store.LoadAsync()).Providers.Single().Models.Single();
                Assert.AreEqual(false, model.DefaultContextWindow);
                Assert.AreEqual(65536, model.MaxInputTokens);
                input.Text = "128000";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => provider.Models.Single().MaxInputTokens == 128000);
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], hint.Text!);
                Assert.DoesNotContain(loc["ContextWindowUnverifiedHint"], hint.Text!);
                Assert.IsTrue(provider.Models.Single().ContextWindowIsManual);
                ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-model", 262144)]));
                Assert.AreEqual(128000, provider.Models.Single().MaxInputTokens, "手动改回旧自动值仍不得被下次规格覆盖");
                provider.Models.Single().ContextWindowIsManual = false;
                provider.Models.Single().DefaultContextWindow = true;
                await store.SaveAsync(settings); view.RefreshCatalogModels(); await PumpAsync();
                Assert.Contains(loc["ContextWindowDefaultHint"], hint.Text!);
                ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-model", 128000)]));
                await store.SaveAsync(settings); view.RefreshCatalogModels(); await PumpAsync();
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], hint.Text!, "真实接口128000不能标成默认值");
                provider.Models.Single().DefaultContextWindow = null;
                await store.SaveAsync(settings); view.RefreshCatalogModels(); await PumpAsync();
                Assert.Contains(loc["ContextWindowUnverifiedHint"], hint.Text!);
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], hint.Text!);
                loc.Switch(language == "en" ? "zh-Hans" : "en"); view.ApplyLoc();
                Assert.Contains(loc["ContextWindowUnverifiedHint"], ToolTip.GetTip(input)?.ToString() ?? "");
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
    public void DefaultContextWindow_RetypingSameNumberRecordsIntentButNameOnlyKeepsWarning(string language, bool editWindow)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            var catalog = new ModelsDevCatalog(context);
            var provider = new AiProvider();
            ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-same-number", 0)]));
            AiModelConfig model = provider.Models.Single();
            var other = new AiModelConfig { Model = "other", MaxInputTokens = 128000, DefaultContextWindow = true };
            provider.Models.Add(other);
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc(language);
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                TextBox input = view.GetControl<TextBox>("MaxInputTokensBox");
                TextBlock hint = view.GetControl<TextBlock>("MaxInputTokensHintText");
                Assert.Contains(loc["ContextWindowDefaultHint"], hint.Text!);
                view.GetControl<TextBox>("NameBox").Text = "name only";
                if (editWindow)
                {
                    Assert.IsTrue(input.Focus());
                    input.Text = "";
                    input.Text = "128000"; // 同一拍删/重填，不能依赖延后 TextChanged。
                    Assert.DoesNotContain(loc["ContextWindowDefaultHint"], hint.Text!);
                }
                else Assert.Contains(loc["ContextWindowDefaultHint"], hint.Text!);
                view.RefreshCatalogModels();
                await PumpAsync();
                loc.Switch(language == "en" ? "zh-Hans" : "en"); view.ApplyLoc();
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                AiModelConfig saved = (await store.LoadAsync()).Providers.Single().Models.Single(m => m.Id == model.Id);
                Assert.AreEqual(128000, saved.MaxInputTokens);
                Assert.AreEqual(editWindow, saved.ContextWindowIsManual);
                Assert.AreEqual(!editWindow, saved.DefaultContextWindow);
                Assert.AreEqual("name only", saved.Name);
                if (editWindow) Assert.DoesNotContain(loc["ContextWindowDefaultHint"], hint.Text!);
                else Assert.Contains(loc["ContextWindowDefaultHint"], hint.Text!);
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                list.SelectedItem = list.ItemsSource!.Cast<ProviderNavItem>().Single(row => row.Model?.Id == other.Id);
                await PumpAsync();
                Assert.Contains(loc["ContextWindowDefaultHint"], hint.Text!, "上一模型的手动意图不能串到下一页");
                list.SelectedItem = list.ItemsSource!.Cast<ProviderNavItem>().Single(row => row.Model?.Id == model.Id);
                await PumpAsync();
                ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-same-number", 262144)]));
                await store.SaveAsync(settings);
                view.RefreshCatalogModels(); await PumpAsync();
                Assert.AreEqual(editWindow ? "128000" : "262144", input.Text);
                await store.ReloadIntoAsync(settings);
                view.RefreshCatalogModels(); await PumpAsync();
                Assert.AreEqual(editWindow ? 128000 : 262144, settings.FindModel(model.Id)!.MaxInputTokens);
                Assert.AreEqual(editWindow, settings.FindModel(model.Id)!.Config.ContextWindowIsManual);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DefaultContextWindow_SameNumberTypedDuringInitialSecretLoadSurvivesLatestCatalogueMerge()
    {
        OnUi(async () =>
        {
            using var source = new TestPluginContext();
            var provider = new AiProvider();
            File.WriteAllText(Path.Combine(source.DataDirectory, "models-dev.json"), "{}");
            var catalog = new ModelsDevCatalog(source);
            ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-loading-window", 0)]));
            AiModelConfig model = provider.Models.Single();
            model.HasOwnApiKey = true;
            await source.Secrets.SetAsync("apikey:" + model.Id, "own-key");
            var secrets = new DelayedSecrets(source.Secrets, "apikey:" + model.Id);
            using var context = new TestPluginContext { Secrets = secrets };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            try
            {
                await secrets.Started.WaitAsync(TimeSpan.FromSeconds(15));
                TextBox input = view.GetControl<TextBox>("MaxInputTokensBox");
                input.Text = ""; input.Text = "128000";
                view.GetControl<TextBox>("NameBox").Text = "latest name draft";
                ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-loading-window", 262144)]));
                await store.SaveAsync(settings);
                view.RefreshCatalogModels();
                secrets.Release(); await PumpAsync();
                Assert.AreEqual("128000", input.Text);
                Assert.AreEqual("latest name draft", view.GetControl<TextBox>("NameBox").Text);
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], view.GetControl<TextBlock>("MaxInputTokensHintText").Text!);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                AiModelConfig saved = (await store.LoadAsync()).Providers.Single().Models.Single();
                Assert.AreEqual(128000, saved.MaxInputTokens);
                Assert.IsTrue(saved.ContextWindowIsManual);
                Assert.AreEqual(false, saved.DefaultContextWindow);
            }
            finally { secrets.Release(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_ExternalAuthSwitchRefreshesRealPanelsAndKeepsKeyDraftBoundAndMasked(bool subscriptionFirst)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var model = new AiModelConfig { Model = "m" };
            var provider = new AiProvider
            {
                Name = "original", BaseUrl = "https://same.example", Models = [model],
                Auth = subscriptionFirst ? AuthMethod.Subscription : AuthMethod.ApiKey,
                OAuth = new OAuthConfig()
            };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(provider.Id, "original-secret");
            await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "subscription-token" });
            await store.SaveAsync(settings);
            AiSettings live = await store.LoadAsync();
            var loc = new Loc("en");
            var view = new SettingsView(context, store, live, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                TextBox? keyDraft = null;
                view.GetControl<TextBox>("ProviderNameBox").Text = "name draft";
                if (!subscriptionFirst)
                {
                    keyDraft = EditProviderKey(view, provider.Id);
                    keyDraft.Text = "manual-key-draft";
                    Click(KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton"));
                    Assert.Contains("original-secret", ((TextBlock)KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").Content!).Text!);
                }
                var external = new AiSettingsStore(context);
                AiSettings newer = await external.LoadAsync();
                newer.Providers.Single().Auth = subscriptionFirst ? AuthMethod.ApiKey : AuthMethod.Subscription;
                await external.SaveAsync(newer);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["SetupConfigChanged"]);
                Assert.AreEqual(subscriptionFirst, view.GetControl<Control>("ProviderKeyPanel").IsVisible);
                Assert.AreEqual(!subscriptionFirst, view.GetControl<Control>("ProviderAuthPanel").IsVisible);
                Assert.AreEqual("name draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                if (keyDraft is not null)
                {
                    Assert.AreEqual("manual-key-draft", keyDraft.Text);
                    Assert.IsFalse(keyDraft.IsEffectivelyVisible);
                    Assert.DoesNotContain("original-secret", ((TextBlock)KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").Content!).Text!);
                    Click(view.GetControl<Button>("SaveButton")); await PumpAsync();
                    Assert.AreEqual(loc["SetupConfigChanged"], view.GetControl<TextBlock>("StatusText").Text);
                    Assert.AreEqual("original", (await store.LoadAsync()).Providers.Single().Name);
                    Assert.AreEqual("original-secret", await store.GetApiKeyAsync(provider.Id));
                }
                else
                {
                    await WaitUntilAsync(() => HasKeyRow(view, provider.Id));
                    keyDraft = EditProviderKey(view, provider.Id);
                    keyDraft.Text = "manual-key-draft";
                }
                // 同实例回到 API Key 时保留真正草稿，但不恢复完整旧 Key 展示。
                newer = await external.LoadAsync();
                newer.Providers.Single().Auth = AuthMethod.ApiKey;
                await external.SaveAsync(newer);
                await store.ReloadIntoAsync(live);
                view.RefreshCatalogModels(); await PumpAsync();
                Assert.IsTrue(view.GetControl<Control>("ProviderKeyPanel").IsVisible);
                Assert.IsFalse(view.GetControl<Control>("ProviderAuthPanel").IsVisible);
                Assert.AreEqual("manual-key-draft", keyDraft.Text);
                Assert.DoesNotContain("original-secret", ((TextBlock)KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").Content!).Text!);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                Assert.AreEqual("manual-key-draft", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("name draft", (await store.LoadAsync()).Providers.Single().Name);
                // 真正导航必须清掉原实例草稿与旧机密显示。
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 1;
                await PumpAsync();
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                TextBox returnedDraft = KeyControl<TextBox>(view, provider.Id, "ProviderKeyEditBox");
                Assert.IsTrue(string.IsNullOrEmpty(returnedDraft.Text), "导航后不能保留原实例 Key 草稿");
                Assert.AreEqual('●', returnedDraft.PasswordChar);
                Assert.IsFalse(returnedDraft.IsEffectivelyVisible);
                Assert.DoesNotContain("manual-key-draft", ((TextBlock)KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").Content!).Text!);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DefaultContextWindow_SameNumberEditedDuringHeldSaveKeepsLatestIntentAndFields(bool editedBeforeSave)
    {
        OnUi(async () =>
        {
            var storage = new KeySettingsStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            var catalog = new ModelsDevCatalog(context);
            var provider = new AiProvider();
            ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-save-window", 0)]));
            AiModelConfig model = provider.Models.Single();
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                TextBox input = view.GetControl<TextBox>("MaxInputTokensBox");
                TextBox name = view.GetControl<TextBox>("NameBox");
                if (editedBeforeSave) { input.Text = ""; input.Text = "128000"; }
                name.Text = "submitted name";
                storage.HoldWrites = true;
                Click(view.GetControl<Button>("SaveButton"));
                await storage.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                input.Text = ""; input.Text = "128000";
                name.Text = "latest draft during write";
                loc.Switch("zh-Hans"); view.ApplyLoc();
                storage.WriteRelease.TrySetResult();
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                Assert.AreEqual("latest draft during write", name.Text);
                Assert.AreEqual("128000", input.Text);
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], view.GetControl<TextBlock>("MaxInputTokensHintText").Text!);
                AiModelConfig saved = (await store.LoadAsync()).Providers.Single().Models.Single();
                Assert.AreEqual("submitted name", saved.Name);
                Assert.AreEqual(editedBeforeSave, saved.ContextWindowIsManual);
                Assert.AreEqual(!editedBeforeSave, saved.DefaultContextWindow);
                view.GetControl<TextBlock>("StatusText").Text = "";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                saved = (await store.LoadAsync()).Providers.Single().Models.Single();
                Assert.AreEqual("latest draft during write", saved.Name);
                Assert.AreEqual(128000, saved.MaxInputTokens);
                Assert.IsTrue(saved.ContextWindowIsManual);
                Assert.AreEqual(false, saved.DefaultContextWindow);
            }
            finally { storage.WriteRelease.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_SettingsModelPullPublishesOnlyAfterJsonAndRetainsSameNumberDraftAcrossNavigation(bool failWrite)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyProbeServer(_ => 200, holdFirst: true,
                jsonResponse: """{"data":[{"id":"private-atomic-settings-pull","context_length":262144}]}""");
            endpoint.Start();
            using var index = new KeyProbeServer(_ => 200,
                jsonResponse: """{"fixture":{"unused-index-model":{"ctx":4096,"max":1024}}}""");
            index.Start();
            var indexTransport = new CatalogueIndexTransport(index.BaseUrl);
            using var catalogueHttp = new HttpClient(indexTransport);
            var storage = new KeySettingsStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            var catalog = new ModelsDevCatalog(context);
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl };
            ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("private-atomic-settings-pull", 0)]));
            AiModelConfig model = provider.Models.Single();
            provider.AvailableModels = ["original-candidate"];
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(provider.Id, "saved-model-list-key");
            await store.SaveAsync(settings);
            string before = System.Text.Json.JsonSerializer.Serialize(provider);
            var health = new ProviderHealth(); health.Record(model.Id, false);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health, catalogueHttp);
            int notifications = 0;
            view.ModelsChanged += () => notifications++;
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Task pulling = (Task)typeof(SettingsView).GetMethod("PullModelsAsync", flags)!.Invoke(view, null)!;
                await WaitUntilAsync(() => endpoint.Requests.Count == 1);
                Assert.AreEqual("Bearer saved-model-list-key", endpoint.Requests.Single().Auth);
                Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(provider), "网络期间不可先发布目录");
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 1;
                await PumpAsync();
                TextBox input = view.GetControl<TextBox>("MaxInputTokensBox");
                input.Text = ""; input.Text = "128000";
                view.GetControl<TextBox>("NameBox").Text = "model draft during pull";
                storage.HoldWrites = true;
                endpoint.Release();
                await storage.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(provider), "JSON等待中活模型与型号候选仍是旧值");
                Assert.AreEqual(128000, settings.FindModel(model.Id)!.MaxInputTokens);
                Assert.AreEqual("128000", input.Text);
                Assert.IsTrue(health.IsCooling(model.Id), "未提交目录不能提前清健康证据");
                Assert.AreEqual(0, notifications);
                storage.Fail = failWrite;
                storage.WriteRelease.TrySetResult();
                await pulling.WaitAsync(TimeSpan.FromSeconds(15));
                await PumpAsync();
                Assert.AreEqual(new Uri(ModelsDevCatalog.SourceUrl), indexTransport.RequestedUri);
                Assert.IsNull(indexTransport.Authorization);
                Assert.HasCount(1, index.Requests, "force=true 必须真正刷新公共索引,不能以缓存代替刷新");
                Assert.IsNull(index.Requests.Single().Auth, "供应商 Key 不得送到公共规格源");
                Assert.AreEqual(4096, new ModelsDevCatalog(context).ForProvider("fixture").Single().ContextTokens,
                    "通过真实 RefreshAsync(HttpClient) 下载、解析并落盘规格索引");
                AiProvider persisted = (await store.LoadAsync()).Providers.Single();
                if (failWrite)
                {
                    Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(provider));
                    Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(persisted));
                    Assert.IsTrue(health.IsCooling(model.Id));
                    Assert.AreEqual(0, notifications);
                }
                else
                {
                    Assert.AreEqual(262144, provider.Models.Single().MaxInputTokens);
                    Assert.AreEqual(262144, persisted.Models.Single().MaxInputTokens);
                    CollectionAssert.AreEqual(new[] { "private-atomic-settings-pull" }, provider.AvailableModels);
                    Assert.IsFalse(health.IsCooling(model.Id));
                    Assert.AreEqual(1, notifications);
                }
                Assert.AreEqual("128000", input.Text);
                Assert.AreEqual("model draft during pull", view.GetControl<TextBox>("NameBox").Text);
                storage.Fail = false;
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                AiModelConfig saved = (await store.LoadAsync()).Providers.Single().Models.Single();
                Assert.AreEqual(128000, saved.MaxInputTokens);
                Assert.AreEqual(false, saved.DefaultContextWindow);
                Assert.IsTrue(saved.ContextWindowIsManual);
            }
            finally { endpoint.Release(); storage.WriteRelease.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    public void DefaultContextWindow_SelectingKnownSame128000UpdatesDraftAndSavedOrigin()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), """
                {"openai":{"known-model":{"ctx":128000,"max":8192}}}
                """);
            var catalog = new ModelsDevCatalog(context);
            var provider = new AiProvider { CatalogId = "openai", AvailableModels = ["private-model", "known-model"] };
            ModelsDevCatalog.Materialise(provider, catalog.Describe("openai", [("private-model", 0)]));
            AiModelConfig model = provider.Models.Single();
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ComboBox>("ModelPickCombo").SelectedItem = "known-model";
                await WaitUntilAsync(() => view.GetControl<TextBox>("ModelBox").Text == "known-model");
                Assert.AreEqual("128000", view.GetControl<TextBox>("MaxInputTokensBox").Text);
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], view.GetControl<TextBlock>("MaxInputTokensHintText").Text!);
                loc.Switch("zh-Hans"); view.ApplyLoc();
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                AiModelConfig saved = (await store.LoadAsync()).Providers.Single().Models.Single();
                Assert.AreEqual("known-model", saved.Model);
                Assert.AreEqual(128000, saved.MaxInputTokens);
                Assert.AreEqual(false, saved.DefaultContextWindow);
                Assert.DoesNotContain(loc["ContextWindowDefaultHint"], ToolTip.GetTip(view.GetControl<TextBox>("MaxInputTokensBox"))?.ToString() ?? "");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_ColdSecretReadFailureShowsSaveErrorAndRetainsDraftForRetry()
    {
        OnUi(async () =>
        {
            using var endpoint = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var secrets = new KeySettingsSecrets(source.Secrets);
            using var context = new TestPluginContext { Secrets = secrets };
            var provider = new AiProvider { Name = "saved", BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider] };
            await source.Secrets.SetAsync("apikey:" + provider.Id, "original-key");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            secrets.FailReads = true;
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderNameBox").Text = "retained draft";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text?.Contains("Secret read failed") == true);
                Assert.AreEqual("retained draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.AreEqual("saved", (await store.LoadAsync()).Providers[0].Name);
                Assert.IsEmpty(endpoint.Requests);
                secrets.FailReads = false;
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == "Saved.");
                Assert.AreEqual("retained draft", (await store.LoadAsync()).Providers[0].Name);
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer original-key", endpoint.Authorizations.Single());
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("row")]
    [DataRow("test")]
    [DataRow("pull")]
    [DataRow("own")]
    public void Review_IndependentSavedHostConflictRecoversWithoutSendingNewSecretToOldHost(string action)
    {
        OnUi(async () =>
        {
            using var oldHost = new SseStub(ProbeAnswerSse);
            using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "m", HasOwnApiKey = action == "own" };
            var provider = new AiProvider { Name = "original", BaseUrl = oldHost.BaseUrl, Models = [model] };
            var initial = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            await store.AddProviderApiKeyAsync(initial, provider, "old-provider-key");
            if (model.HasOwnApiKey) await store.SetApiKeyAsync(model.Id, "old-model-key");
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = action == "own" ? 1 : 0;
                await ((Task)typeof(SettingsView).GetField("_editorLoad", flags)!.GetValue(view)!).WaitAsync(TimeSpan.FromSeconds(15));
                TextBox name = view.GetControl<TextBox>(action == "own" ? "NameBox" : "ProviderNameBox");
                name.Text = "retain draft";
                var external = new AiSettingsStore(context);
                AiSettings current = await external.LoadAsync();
                AiProvider owner = current.Providers[0];
                string expected = System.Text.Json.JsonSerializer.Serialize(owner);
                AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
                draft.BaseUrl = newHost.BaseUrl;
                await external.SaveProviderApiKeyFormAsync(current, owner, draft, expected, "new-provider-key", owner.Id, "old-provider-key");
                if (model.HasOwnApiKey) await external.SetApiKeyAsync(model.Id, "new-model-key");
                if (action == "row")
                {
                    Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton"));
                    SelectKeyModel(view, provider.Id, model.Id);
                    await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["SetupConfigChanged"]);
                }
                else if (action == "pull")
                    await ((Task)typeof(SettingsView).GetMethod("PullModelsAsync", flags)!.Invoke(view, null)!).WaitAsync(TimeSpan.FromSeconds(15));
                else await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(new Loc("en")["SetupConfigChanged"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.AreEqual("retain draft", name.Text);
                Assert.AreEqual(newHost.BaseUrl, live.Providers[0].BaseUrl);
                Assert.IsEmpty(oldHost.Requests);
                Assert.IsEmpty(newHost.Requests, "冲突恢复不能自动重放探活或目录请求");
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(model.HasOwnApiKey ? "Bearer new-model-key" : "Bearer new-provider-key", newHost.Authorizations.Single());
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false, "storage")]
    [DataRow(true, "storage")]
    [DataRow(false, "secret")]
    [DataRow(true, "secret")]
    [DataRow(false, "conflict")]
    [DataRow(true, "conflict")]
    public void Review_FailedSettingsDeletionKeepsSelectionDraftAllSecretsAndModelChain(bool modelOnly, string failure)
    {
        OnUi(async () =>
        {
            var storage = new KeySettingsStorage(new InMemoryStorage());
            using var source = new TestPluginContext();
            var secrets = new KeySettingsSecrets(source.Secrets);
            using var context = new TestPluginContext { Storage = storage, Secrets = secrets };
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "m", HasOwnApiKey = true };
            var provider = new AiProvider { Name = "original", Models = [model] };
            var live = new AiSettings
            {
                Providers = [provider], ActiveModelId = model.Id,
                FailoverChain = [new FailoverEntry { ModelId = model.Id }]
            };
            await store.AddProviderApiKeyAsync(live, provider, "primary");
            string extra = await store.AddProviderApiKeyAsync(live, provider, "extra");
            await store.SetApiKeyAsync(model.Id, "model-key");
            await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "token", RefreshToken = "refresh", AccountId = "account" });
            string? tokenJson = await context.Secrets.GetAsync("oauth:" + provider.Id);
            int notifications = 0;
            var view = new SettingsView(context, store, live, new Loc("en"), () => notifications++);
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                list.SelectedIndex = modelOnly ? 1 : 0;
                await ((Task)typeof(SettingsView).GetField("_editorLoad", flags)!.GetValue(view)!).WaitAsync(TimeSpan.FromSeconds(15));
                TextBox name = view.GetControl<TextBox>(modelOnly ? "NameBox" : "ProviderNameBox");
                name.Text = "retain name draft";
                Task Delete() => (Task)typeof(SettingsView).GetMethod("DeleteAsync", flags)!.Invoke(view, null)!;
                if (!modelOnly) await Delete();
                if (failure == "conflict")
                {
                    var external = new AiSettingsStore(context);
                    AiSettings newer = await external.LoadAsync();
                    newer.PanelWidthPercent = 63;
                    await external.SaveAsync(newer);
                }
                storage.Fail = failure == "storage";
                secrets.FailNextMutation = failure == "secret";
                await Delete().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreSame(provider, live.Providers.Single());
                Assert.AreSame(model, provider.Models.Single());
                Assert.AreEqual(modelOnly ? 1 : 0, list.SelectedIndex);
                Assert.AreEqual("retain name draft", name.Text);
                Assert.AreEqual(model.Id, live.ActiveModelId);
                Assert.AreEqual(model.Id, live.FailoverChain.Single().ModelId);
                Assert.AreEqual("primary", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("extra", await store.GetApiKeyAsync(extra));
                Assert.AreEqual("model-key", await store.GetApiKeyAsync(model.Id));
                Assert.AreEqual(tokenJson, await context.Secrets.GetAsync("oauth:" + provider.Id));
                AiSettings saved = await new AiSettingsStore(context).LoadAsync();
                Assert.AreEqual(model.Id, saved.ActiveModelId);
                Assert.AreEqual(model.Id, saved.Providers.Single().Models.Single().Id);
                if (failure != "conflict") Assert.AreEqual(0, notifications);
                storage.Fail = false;
                if (!modelOnly) await Delete();
                await Delete().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsNull(live.ActiveModelId);
                Assert.IsEmpty(live.FailoverChain);
                Assert.IsNull(await store.GetApiKeyAsync(model.Id));
                Assert.AreEqual(modelOnly ? "primary" : null, await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual(modelOnly ? "extra" : null, await store.GetApiKeyAsync(extra));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void Review_DeletedIndependentSelectionKeepsDraftButCannotBindItToAnotherProvider()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var first = new AiProvider { Name = "first", BaseUrl = "https://first.example" };
            var second = new AiProvider { Name = "second", BaseUrl = "https://second.example" };
            var initial = new AiSettings { Providers = [first, second] };
            await store.AddProviderApiKeyAsync(initial, first, "first-key");
            await store.AddProviderApiKeyAsync(initial, second, "second-key");
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Task Save() => (Task)typeof(SettingsView).GetMethod("SaveAsync", flags)!.Invoke(view, null)!;
                await ((Task)typeof(SettingsView).GetField("_editorLoad", flags)!.GetValue(view)!).WaitAsync(TimeSpan.FromSeconds(15));
                view.GetControl<TextBox>("ProviderNameBox").Text = "first draft";
                TextBox key = EditProviderKey(view, first.Id);
                key.Text = "must-not-bind-to-second";
                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                other.Providers.RemoveAt(0);
                await otherStore.SaveAsync(other);
                await otherStore.DeleteApiKeyAsync(first.Id);
                await Save().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(new Loc("en")["SetupConfigChanged"], view.GetControl<TextBlock>("StatusText").Text);
                ListBox nav = view.GetControl<ListBox>("ProvidersList");
                Assert.AreEqual(-1, nav.SelectedIndex);
                Assert.HasCount(1, live.Providers);
                Assert.AreEqual("first draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.AreEqual("must-not-bind-to-second", key.Text);
                Assert.IsFalse(view.GetControl<Button>("SaveButton").IsEnabled);
                Assert.AreEqual("second-key", await store.GetApiKeyAsync(second.Id));
                nav.SelectedIndex = 0;
                await ((Task)typeof(SettingsView).GetField("_editorLoad", flags)!.GetValue(view)!).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("second", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.IsTrue(HasKeyRow(view, second.Id));
                await Save().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(new Loc("en")["Saved"], view.GetControl<TextBlock>("StatusText").Text);
                AiProvider saved = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual("second", saved.Name);
                Assert.AreEqual("https://second.example", saved.BaseUrl);
                Assert.AreEqual("second-key", await store.GetApiKeyAsync(second.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void Review_IndependentSnapshotConflictPreservesDraftAndConfirmsAgainstLatestEndpoint(string locale)
    {
        OnUi(async () =>
        {
            using var oldEndpoint = new SseStub(ProbeAnswerSse);
            using var newEndpoint = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var initial = new AiSettings
            {
                Providers = [new AiProvider { Name = "original", BaseUrl = oldEndpoint.BaseUrl, Models = [new AiModelConfig { Model = "m" }] }]
            };
            await store.AddProviderApiKeyAsync(initial, initial.Providers[0], "original-key");
            AiSettings live = await store.LoadAsync();
            AiProvider provider = live.Providers[0];
            var loc = new Loc(locale);
            var view = new SettingsView(context, store, live, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<TextBox>("ProviderNameBox").Text = "name draft";
                TextBox keyDraft = EditProviderKey(view, provider.Id);
                keyDraft.Text = "confirmed-key";
                Assert.IsTrue(keyDraft.Focus());
                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                other.Providers[0].BaseUrl = newEndpoint.BaseUrl;
                other.Providers[0].Models[0].Temperature = 0.6f;
                other.PanelWidthPercent = 67;
                other.WebSearch.MaxFetchChars = 54321;
                string added = await otherStore.AddProviderApiKeyAsync(other, other.Providers[0], "external-extra");
                await otherStore.SetApiKeyAsync(provider.Id, "external-main");
                Button save = view.GetControl<Button>("SaveButton");
                Click(save);
                await WaitUntilAsync(() => save.IsEnabled && view.GetControl<TextBlock>("StatusText").Text == loc["SetupConfigChanged"]);
                Assert.AreEqual("name draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.AreEqual("confirmed-key", keyDraft.Text);
                Assert.IsTrue(keyDraft.IsFocused);
                Assert.AreEqual(newEndpoint.BaseUrl, view.GetControl<TextBox>("ProviderBaseUrlBox").Text, "未编辑地址必须展示外部最新值");
                Assert.IsTrue(HasKeyRow(view, added), "新槽必须同步展示，不能只刷新 CWT 基线");
                Assert.AreEqual("external-main", await store.GetApiKeyAsync(provider.Id), "第一次冲突不重放写 Key");
                Assert.IsEmpty(oldEndpoint.Requests); Assert.IsEmpty(newEndpoint.Requests);
                Click(save);
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("name draft", saved.Providers[0].Name);
                Assert.AreEqual(newEndpoint.BaseUrl, saved.Providers[0].BaseUrl);
                Assert.AreEqual(0.6f, saved.Providers[0].Models[0].Temperature);
                Assert.AreEqual(67, saved.PanelWidthPercent);
                Assert.AreEqual(54321, saved.WebSearch.MaxFetchChars);
                CollectionAssert.AreEqual(new[] { added }, saved.Providers[0].AdditionalApiKeyIds);
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsEmpty(oldEndpoint.Requests);
                Assert.AreEqual("Bearer confirmed-key", newEndpoint.Authorizations.Single());
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_ModelConflictKeepsEditedFieldsAndRefreshesUneditedFieldsBeforeSecondSave(bool ownKey)
    {
        OnUi(async () =>
        {
            using var endpoint = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "m", HasOwnApiKey = ownKey, Temperature = 0.2f };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var initial = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            await store.AddProviderApiKeyAsync(initial, provider, "provider-key");
            if (ownKey) await store.SetApiKeyAsync(model.Id, "original-own");
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<TextBox>("NameBox").Text = "model draft";
                if (ownKey) view.GetControl<TextBox>("ApiKeyBox").Text = "confirmed-own";
                var otherStore = new AiSettingsStore(context);
                AiSettings other = await otherStore.LoadAsync();
                other.Providers[0].Models[0].Temperature = 0.6f;
                other.Providers[0].Models[0].MaxTokens = 5555;
                other.DisabledBuiltinTools = "terminal_write";
                await otherStore.SaveAsync(other);
                if (ownKey) await otherStore.SetApiKeyAsync(model.Id, "external-own");
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["SetupConfigChanged"]);
                Assert.AreEqual("model draft", view.GetControl<TextBox>("NameBox").Text);
                Assert.AreEqual("5555", view.GetControl<TextBox>("MaxTokensBox").Text);
                Assert.AreEqual(0.6f.ToString(), view.GetControl<TextBox>("TemperatureBox").Text);
                if (ownKey)
                {
                    Assert.AreEqual("confirmed-own", view.GetControl<TextBox>("ApiKeyBox").Text);
                    Assert.AreEqual("external-own", await store.GetApiKeyAsync(model.Id));
                }
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("model draft", saved.Providers[0].Models[0].Name);
                Assert.AreEqual(0.6f, saved.Providers[0].Models[0].Temperature);
                Assert.AreEqual("terminal_write", saved.DisabledBuiltinTools);
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(ownKey ? "Bearer confirmed-own" : "Bearer provider-key", endpoint.Authorizations.Single());
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_ConflictReloadKeepsInputTypedWhileWaiting(bool modelPage)
    {
        OnUi(async () =>
        {
            var storage = new KeySettingsStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Name = "original model", Model = "m1", Temperature = 0.2f };
            var provider = new AiProvider { Name = "original provider", BaseUrl = "https://original.example", Models = [model] };
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = modelPage ? model.Id : null });
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                TextBox name = view.GetControl<TextBox>(modelPage ? "NameBox" : "ProviderNameBox");
                name.Text = "earlier draft";
                var external = new AiSettingsStore(context);
                AiSettings newer = await external.LoadAsync();
                newer.Providers[0].BaseUrl = "https://latest.example";
                newer.Providers[0].Models[0].Temperature = 0.6f;
                await external.SaveAsync(newer);
                storage.SettingsReadsUntilHold = 2;
                Task recovering = StartTestAsync(view);
                await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                name.Text = "typed during reload";
                Assert.IsTrue(name.Focus());
                storage.ReadRelease.TrySetResult();
                await recovering.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("typed during reload", name.Text);
                Assert.IsTrue(name.IsFocused);
                Assert.AreEqual(new Loc("en")["SetupConfigChanged"], view.GetControl<TextBlock>("StatusText").Text);
                if (modelPage) Assert.AreEqual(0.6f.ToString(), view.GetControl<TextBox>("TemperatureBox").Text);
                else Assert.AreEqual("https://latest.example", view.GetControl<TextBox>("ProviderBaseUrlBox").Text);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiProvider saved = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual("typed during reload", modelPage ? saved.Models[0].Name : saved.Name);
                Assert.AreEqual("https://latest.example", saved.BaseUrl);
                Assert.AreEqual(0.6f, saved.Models[0].Temperature);
            }
            finally { storage.ReadRelease.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_ModelRecoveryDelayedOwnKeyKeepsLatestDraftOrStopsAfterNavigation(bool navigateAway)
    {
        OnUi(async () =>
        {
            using var source = new TestPluginContext();
            var first = new AiModelConfig { Name = "A", Model = "model-a", Temperature = 0.2f, MaxInputTokens = 128000, DefaultContextWindow = true };
            var second = new AiModelConfig { Name = "B", Model = "model-b", MaxTokens = 7000, MaxInputTokens = 128000, DefaultContextWindow = true };
            var secrets = new DelayedSecrets(source.Secrets, $"apikey:{first.Id}");
            using var context = new TestPluginContext { Secrets = secrets };
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = "https://same.example", Models = [first, second] };
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = first.Id });
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<TextBox>("NameBox").Text = "A earlier draft";
                var external = new AiSettingsStore(context);
                AiSettings newer = await external.LoadAsync();
                newer.Providers[0].Models[0].HasOwnApiKey = true;
                newer.Providers[0].Models[0].Temperature = 0.6f;
                await external.SaveAsync(newer);
                await source.Secrets.SetAsync($"apikey:{first.Id}", "latest-own-key");
                Task recovering = StartTestAsync(view);
                await secrets.Started.WaitAsync(TimeSpan.FromSeconds(15));
                if (navigateAway)
                {
                    ListBox list = view.GetControl<ListBox>("ProvidersList");
                    list.SelectedItem = list.ItemsSource!.Cast<ProviderNavItem>().Single(row => row.Model?.Id == second.Id);
                    await PumpAsync();
                }
                TextBox name = view.GetControl<TextBox>("NameBox");
                name.Text = navigateAway ? "B latest draft" : "A latest draft";
                view.GetControl<TextBox>("MaxTokensBox").Text = "4321";
                TextBox input = view.GetControl<TextBox>("MaxInputTokensBox");
                input.Text = ""; input.Text = "128000";
                if (!navigateAway) view.GetControl<TextBox>("ApiKeyBox").Text = "latest key draft";
                Assert.IsTrue(name.Focus());
                secrets.Release();
                await recovering.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(navigateAway ? "B latest draft" : "A latest draft", name.Text);
                Assert.AreEqual(navigateAway ? "model-b" : "model-a", view.GetControl<TextBox>("ModelBox").Text);
                Assert.AreEqual("4321", view.GetControl<TextBox>("MaxTokensBox").Text);
                Assert.AreEqual("128000", input.Text);
                Assert.AreEqual(navigateAway ? "" : "latest key draft", view.GetControl<TextBox>("ApiKeyBox").Text);
                Assert.AreEqual(!navigateAway, view.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                Assert.IsTrue(name.IsFocused);
                if (navigateAway) Assert.AreEqual("", view.GetControl<TextBlock>("StatusText").Text);
                else Assert.AreEqual(0.6f.ToString(), view.GetControl<TextBox>("TemperatureBox").Text);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiProvider saved = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual(navigateAway ? "A" : "A latest draft", saved.Models[0].Name);
                Assert.AreEqual(navigateAway ? "B latest draft" : "B", saved.Models[1].Name);
                Assert.AreEqual(4321, saved.Models[navigateAway ? 1 : 0].MaxTokens);
                Assert.IsTrue(saved.Models[navigateAway ? 1 : 0].ContextWindowIsManual);
                Assert.AreEqual(false, saved.Models[navigateAway ? 1 : 0].DefaultContextWindow);
                Assert.AreEqual(navigateAway ? "latest-own-key" : "latest key draft", await store.GetApiKeyAsync(first.Id));
            }
            finally { secrets.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void Review_CatalogSavedFieldsBecomeRecoveryBaselineBeforeIndependentModelChange()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "original-model", MaxTokens = 1000, MaxInputTokens = 10000 };
            ModelsDevCatalog.Apply(model, new ModelSpec(model.Model, model.Name, model.MaxInputTokens,
                model.MaxTokens, 0, 0, 0, null), newModel: true);
            var provider = new AiProvider { BaseUrl = "https://same.example", Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<TextBox>("NameBox").Text = "true name draft";
                view.GetControl<TextBox>("MaxTokensBox").Text = "3333";
                ModelsDevCatalog.Materialise(provider, [new ModelSpec("catalog-m1", "catalog", 20000, 2000, 2, 3, 1, true)]);
                await store.SaveAsync(settings);
                view.RefreshCatalogModels(); await PumpAsync();
                Assert.AreEqual("catalog-m1", view.GetControl<TextBox>("ModelBox").Text);
                Assert.AreEqual("20000", view.GetControl<TextBox>("MaxInputTokensBox").Text);
                var external = new AiSettingsStore(context);
                AiSettings newer = await external.LoadAsync();
                AiModelConfig savedModel = newer.Providers[0].Models.Single();
                savedModel.Model = "external-m2";
                savedModel.MaxInputTokens = 60000;
                savedModel.MaxTokens = 6000;
                savedModel.InputPricePerMillion = 6;
                savedModel.OutputPricePerMillion = 7;
                savedModel.CachedInputPricePerMillion = 2;
                await external.SaveAsync(newer);
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("true name draft", view.GetControl<TextBox>("NameBox").Text);
                Assert.AreEqual("3333", view.GetControl<TextBox>("MaxTokensBox").Text);
                Assert.AreEqual("external-m2", view.GetControl<TextBox>("ModelBox").Text);
                Assert.AreEqual("60000", view.GetControl<TextBox>("MaxInputTokensBox").Text);
                Assert.AreEqual("6", view.GetControl<TextBox>("PriceInBox").Text);
                Assert.AreEqual("7", view.GetControl<TextBox>("PriceOutBox").Text);
                Assert.AreEqual("2", view.GetControl<TextBox>("PriceCachedBox").Text);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiModelConfig committed = (await store.LoadAsync()).Providers.Single().Models.Single();
                Assert.AreEqual("true name draft", committed.Name);
                Assert.AreEqual("external-m2", committed.Model);
                Assert.AreEqual(3333, committed.MaxTokens);
                Assert.AreEqual(60000, committed.MaxInputTokens);
                Assert.AreEqual(6, committed.InputPricePerMillion);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Review_ConflictReloadNavigationMergesNewPageBaselineWithoutApplyingOldPageDraft(bool modelPage, bool navigateAfterKeyRead)
    {
        OnUi(async () =>
        {
            using var source = new TestPluginContext();
            var a = new AiModelConfig { Name = "A", Model = "a" };
            var b = new AiModelConfig { Name = "B", Model = "old-b", Temperature = 0.2f };
            var c = new AiModelConfig { Name = "C", Model = "old-c" };
            var delayed = new DelayedSecrets(source.Secrets, $"apikey:{b.Id}");
            var storage = new KeySettingsStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage, Secrets = delayed };
            var store = new AiSettingsStore(context);
            var first = new AiProvider { Name = "owner-A", BaseUrl = "https://a.example", Models = [a], ModelsExpanded = true };
            var second = new AiProvider { Name = "owner-B", BaseUrl = "https://old-b.example", Models = [b], ModelsExpanded = true };
            var third = new AiProvider { Name = "owner-C", BaseUrl = "https://c.example", Models = [c], ModelsExpanded = true };
            await store.SaveAsync(new AiSettings { Providers = [first, second, third], ActiveModelId = a.Id });
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<TextBox>("NameBox").Text = "must never copy A draft";
                var external = new AiSettingsStore(context);
                AiSettings newer = await external.LoadAsync();
                newer.Providers[1].BaseUrl = "https://latest-b.example";
                newer.Providers[1].Models[0].Model = "latest-b";
                newer.Providers[1].Models[0].Temperature = 0.7f;
                newer.Providers[1].Models[0].HasOwnApiKey = modelPage;
                newer.Providers[2].Models[0].Model = "latest-c";
                await external.SaveAsync(newer);
                await source.Secrets.SetAsync($"apikey:{b.Id}", "external-own-b");
                storage.SettingsReadsUntilHold = 2;
                Task recovering = StartTestAsync(view);
                await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                list.SelectedItem = list.ItemsSource!.Cast<ProviderNavItem>().Single(row => row.Provider.Id == second.Id
                    && (modelPage ? row.Model?.Id == b.Id : row.Model is null));
                await PumpAsync();
                TextBox name = view.GetControl<TextBox>(modelPage ? "NameBox" : "ProviderNameBox");
                name.Text = "B own draft while Reload waits";
                storage.ReadRelease.TrySetResult();
                if (modelPage)
                {
                    await delayed.Started.WaitAsync(TimeSpan.FromSeconds(15));
                    name.Text = "B own latest draft while Key waits";
                    view.GetControl<TextBox>("MaxTokensBox").Text = "4321";
                    view.GetControl<TextBox>("ApiKeyBox").Text = "B current own key draft";
                    if (navigateAfterKeyRead)
                    {
                        list.SelectedItem = list.ItemsSource!.Cast<ProviderNavItem>().Single(row => row.Model?.Id == c.Id);
                        await PumpAsync();
                        name = view.GetControl<TextBox>("NameBox");
                        name.Text = "C current draft";
                    }
                    Assert.IsTrue(name.Focus());
                    delayed.Release();
                }
                await recovering.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(navigateAfterKeyRead ? "C current draft"
                    : modelPage ? "B own latest draft while Key waits" : "B own draft while Reload waits", name.Text);
                if (modelPage)
                {
                    Assert.AreEqual(navigateAfterKeyRead ? "latest-c" : "latest-b", view.GetControl<TextBox>("ModelBox").Text);
                    Assert.AreEqual(navigateAfterKeyRead ? "" : "B current own key draft", view.GetControl<TextBox>("ApiKeyBox").Text);
                    if (!navigateAfterKeyRead)
                    {
                        Assert.AreEqual("4321", view.GetControl<TextBox>("MaxTokensBox").Text);
                        Assert.AreEqual(0.7f.ToString(), view.GetControl<TextBox>("TemperatureBox").Text);
                        Assert.AreEqual(true, view.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                    }
                    Assert.IsTrue(name.IsFocused);
                }
                else Assert.AreEqual("https://latest-b.example", view.GetControl<TextBox>("ProviderBaseUrlBox").Text);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("A", saved.Providers[0].Models[0].Name);
                Assert.AreEqual("latest-b", saved.Providers[1].Models[0].Model);
                Assert.AreEqual(0.7f, saved.Providers[1].Models[0].Temperature);
                Assert.AreEqual("https://latest-b.example", saved.Providers[1].BaseUrl);
                Assert.AreEqual(navigateAfterKeyRead ? "C current draft" : "C", saved.Providers[2].Models[0].Name);
                if (modelPage) Assert.AreEqual(navigateAfterKeyRead ? "B" : "B own latest draft while Key waits", saved.Providers[1].Models[0].Name);
                else Assert.AreEqual("B own draft while Reload waits", saved.Providers[1].Name);
            }
            finally { storage.ReadRelease.TrySetResult(); delayed.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void Review_PullModelsWithInlineKeyDraftRequiresSaveWithoutAnyHttp()
    {
        OnUi(async () =>
        {
            using var endpoint = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider] };
            await store.AddProviderApiKeyAsync(settings, provider, "saved-key");
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                TextBox draft = EditProviderKey(view, provider.Id);
                draft.Text = "uncommitted-key";
                Click(view.GetControl<Button>("PullModelsButton"));
                await PumpAsync();
                Assert.AreEqual(new Loc("en")["SetupSaveProviderFirst"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.AreEqual("uncommitted-key", draft.Text);
                Assert.IsTrue(draft.IsEffectivelyVisible);
                Assert.IsEmpty(endpoint.Requests);
                Assert.AreEqual("saved-key", await store.GetApiKeyAsync(provider.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_BalanceToggleAndChatPersistenceCannotOverwriteEachOther(bool balanceFirst)
    {
        OnUi(async () =>
        {
            var storage = new AiSettingsStoreTests.FailingStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            var provider = new AiProvider { BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "extra");
            provider.ActiveApiKeyId = extra;
            await store.SaveAsync(settings);
            var chat = new ChatPanelView(context, store);
            var chatHost = new Window { Content = chat, Width = 900, Height = 700 };
            Window? settingsHost = null;
            chatHost.Show(); await PumpAsync();
            try
            {
                Click(chat.GetControl<Button>("SettingsButton"));
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = new Window { Content = editor, Width = 900, Height = 700 };
                settingsHost.Show(); await PumpAsync();
                editor.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await WaitUntilAsync(() => HasKeyRow(editor, extra));
                CheckBox balance = editor.GetControl<CheckBox>("BalanceApiKeysCheck");
                int completed = storage.WritesCompleted;
                storage.Hold = true;
                if (balanceFirst) balance.IsChecked = true;
                else chat.RememberPanelWidth(61);
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (balanceFirst) chat.RememberPanelWidth(61);
                else balance.IsChecked = true;
                storage.Release.TrySetResult();
                await WaitUntilAsync(() => storage.WritesCompleted == completed + 2 && balance.IsEnabled);

                AiSettings persisted = await new AiSettingsStore(context).LoadAsync();
                Assert.IsTrue(persisted.Providers[0].BalanceApiKeys, "聊天旧快照不能抹掉设置页的均摊选择");
                Assert.AreEqual(61, persisted.PanelWidthPercent, "设置页旧快照不能抹掉聊天面板宽度");
                Assert.AreEqual(extra, persisted.Providers[0].ActiveApiKeyId);
                CollectionAssert.AreEqual(new[] { extra }, persisted.Providers[0].AdditionalApiKeyIds);
                Assert.AreEqual("primary", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("extra", await store.GetApiKeyAsync(extra));
            }
            finally { storage.Release.TrySetResult(); settingsHost?.Close(); chat.Detach(); chatHost.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void Review_EmptyKeyDraftRowShowsUnconfiguredStatusWithoutRequestFailure(string language)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider] };
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "extra");
            var loc = new Loc(language);
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                foreach (string id in new[] { provider.Id, extra })
                {
                    TextBox draft = EditProviderKey(view, id);
                    draft.Text = "unsaved-replacement";
                    await store.RemoveProviderApiKeyAsync(settings, provider, id);
                    view.RefreshCatalogModels(); await PumpAsync();
                    TextBlock status = KeyControl<TextBlock>(view, id, "ProviderKeyStatus");
                    Assert.AreEqual(loc["StatusNeedsKey"], status.Text, "尚未配置 Key 不代表已中止请求");
                    Assert.AreEqual(loc["StatusNeedsKey"], ToolTip.GetTip(status)?.ToString());
                    Assert.AreEqual(loc["StatusNeedsKey"], Avalonia.Automation.AutomationProperties.GetName(status));
                    Assert.IsFalse(KeyControl<Button>(view, id, "ProviderKeyProbeButton").IsEnabled);
                    Assert.AreEqual("unsaved-replacement", draft.Text);
                    Assert.AreNotEqual(loc["ErrorProviderChanged"], status.Text);
                    Assert.AreNotEqual(loc["SetupConfigChanged"], status.Text);
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void Review_ProviderKeySaveKeepsCommittedEndpointWhileSecretIsPending()
    {
        OnUi(async () =>
        {
            using var original = new SseStub(ProbeAnswerSse);
            using var changed = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var provider = new AiProvider { BaseUrl = original.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider] };
            await source.Secrets.SetAsync($"apikey:{provider.Id}", "old-key");
            var delayed = new DelayedSecrets(source.Secrets, $"apikey:{provider.Id}", delayMutation: true);
            using var context = new TestPluginContext { Secrets = delayed };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            int notifications = 0;
            var view = new SettingsView(context, store, settings, new Loc("en"), () => notifications++);
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                EditProviderKey(view, provider.Id).Text = "new-key";
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = changed.BaseUrl;
                Click(view.GetControl<Button>("SaveButton"));
                await delayed.Started.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(original.BaseUrl, provider.BaseUrl, "等待机密时活模型不能已指向新主机");
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsInstanceOfType<AiSettingsStore.ApiKeySlotChangedException>(error);
                Assert.IsEmpty(changed.Requests, "不能向未提交的地址泄露旧 Key");
                delayed.Release();
                await WaitUntilAsync(() => notifications == 1);
                Assert.AreEqual(changed.BaseUrl, provider.BaseUrl);
                (error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(error);
                Assert.AreEqual("Bearer new-key", changed.Authorizations.Single());
            }
            finally { delayed.Release(); window.Close(); }
        });
    }

    private static HeadlessUnitTestSession _session = null!;
    private const string ProbeAnswerSse = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsViewUiTests).Assembly);

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    // Click 处理器丢弃 TestAsync 的任务；首轮必须拿到任务才能证明迟到结果已走完 UI 收尾。
    private static Task StartTestAsync(SettingsView view) =>
        (Task)(typeof(SettingsView).GetMethod("TestAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView.TestAsync is gone")).Invoke(view, null)!;
    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static Border KeyRow(SettingsView view, string keyId) => view.GetLogicalDescendants()
        .OfType<Border>().Single(row => row.Name == "ProviderKeyRow" && (string?)row.Tag == keyId);

    private static bool HasKeyRow(SettingsView view, string keyId) => view.GetLogicalDescendants()
        .OfType<Border>().Any(row => row.Name == "ProviderKeyRow" && (string?)row.Tag == keyId);

    private static T KeyControl<T>(SettingsView view, string keyId, string name) where T : Control
        => KeyRow(view, keyId).GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static TextBox EditProviderKey(SettingsView view, string keyId)
    {
        Click(KeyControl<Button>(view, keyId, "ProviderKeyEditButton"));
        return KeyControl<TextBox>(view, keyId, "ProviderKeyEditBox");
    }

    private sealed class DelayedSecrets(ISecretsApi inner, string delayedName, bool delayMutation = false) : ISecretsApi
    {
        private int _reads;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();

        public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            string? value = await inner.GetAsync(name, cancellationToken);
            if (!delayMutation && name == delayedName && Interlocked.Increment(ref _reads) == 1)
            {
                _started.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return value;
        }

        public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            await WaitForMutationAsync(name, cancellationToken);
            await inner.SetAsync(name, value, cancellationToken);
        }

        public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            await WaitForMutationAsync(name, cancellationToken);
            return await inner.DeleteAsync(name, cancellationToken);
        }

        private async Task WaitForMutationAsync(string name, CancellationToken cancellationToken)
        {
            if (!delayMutation || name != delayedName) return;
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task PumpAsync(int rounds = 20)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(Window Window, SettingsView View, AiSettings Settings, AiSettingsStore Store)> ShowAsync(
        TestPluginContext context, AiSettings settings, Action? onProvidersChanged = null)
    {
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        var view = new SettingsView(context, store, settings, new Loc("en"), onProvidersChanged ?? (() => { }));
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        await PumpAsync();
        return (window, view, settings, store);
    }

    private static AiSettings TwoProviders()
    {
        var routin = new AiProvider
        {
            Name = "Routin",
            BaseUrl = "https://routin.example/v1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models =
            [
                new AiModelConfig { Model = "gpt-5" },
                new AiModelConfig { Name = "Claude", Model = "claude-opus-5", Protocol = ChatProtocol.AnthropicMessages, HasOwnApiKey = true }
            ]
        };
        var ollama = new AiProvider { Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Models = [new AiModelConfig { Model = "llama3.1" }] };
        return new AiSettings { Providers = [routin, ollama], ActiveModelId = routin.Models[1].Id };
    }

    [TestMethod]
    public void List_ShowsProvidersWithIndentedModels_AndSelectsActiveModel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.HasCount(5, rows, "2 个供应商 + 3 个模型");
                Assert.IsTrue(rows[0].IsProvider);
                Assert.AreEqual("Routin", rows[0].Text);
                Assert.IsFalse(rows[1].IsProvider);
                Assert.AreEqual("gpt-5", rows[1].Text, "没填名称的模型显示模型 id");
                Assert.AreEqual("Claude", rows[2].Text);
                Assert.IsGreaterThan(rows[0].Indent.Left, rows[1].Indent.Left, "模型行缩进");
                Assert.IsTrue(rows[3].IsProvider);
                Assert.AreEqual("Ollama", rows[3].Text);
                // 起手选中的是当前活跃模型,右侧是模型表单
                Assert.AreEqual(2, list.SelectedIndex);
                Assert.IsTrue(view.GetControl<StackPanel>("ModelEditor").IsVisible);
                Assert.IsFalse(view.GetControl<StackPanel>("ProviderEditor").IsVisible);
                Assert.AreEqual("claude-opus-5", view.GetControl<TextBox>("ModelBox").Text);
                // 协议下拉:0 = 继承,Anthropic 覆盖 = 枚举值 + 1
                Assert.AreEqual((int)ChatProtocol.AnthropicMessages + 1, view.GetControl<ComboBox>("ProtocolCombo").SelectedIndex);
                Assert.IsTrue(view.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                Assert.IsTrue(view.GetControl<StackPanel>("OwnKeyPanel").IsVisible);
                Assert.IsTrue(view.GetControl<StackPanel>("PromptCachePanel").IsVisible, "解出的协议是 Anthropic,提示词缓存要露出来");

                // 切到供应商行:表单换成供应商那套
                list.SelectedIndex = 0;
                await PumpAsync();
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);
                Assert.IsFalse(view.GetControl<StackPanel>("ModelEditor").IsVisible);
                Assert.AreEqual("Routin", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.AreEqual("https://routin.example/v1", view.GetControl<TextBox>("ProviderBaseUrlBox").Text);
                Assert.AreEqual((int)ChatProtocol.OpenAiChatCompletions, view.GetControl<ComboBox>("ProviderProtocolCombo").SelectedIndex);

                // 切到继承协议的模型:下拉在第 0 项,且缓存开关随供应商默认协议(OpenAI)隐藏
                list.SelectedIndex = 1;
                await PumpAsync();
                Assert.AreEqual(0, view.GetControl<ComboBox>("ProtocolCombo").SelectedIndex);
                Assert.IsFalse(view.GetControl<StackPanel>("OwnKeyPanel").IsVisible);
                Assert.IsFalse(view.GetControl<StackPanel>("PromptCachePanel").IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 折叠 ----

    /// <summary>一家挂 <paramref name="count" /> 个模型;端点能报上几百个,左栏得受得住。</summary>
    private static AiSettings ManyModels(int count, bool selectAModel)
    {
        var big = new AiProvider { Name = "Relay", BaseUrl = "https://relay.example/v1" };
        for (int i = 0; i < count; i++)
        {
            big.Models.Add(new AiModelConfig { Model = $"model-{i:00}" });
        }
        var small = new AiProvider { Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Models = [new AiModelConfig { Model = "llama3.1" }] };
        return new AiSettings
        {
            Providers = [big, small],
            ActiveModelId = selectAModel ? big.Models[^1].Id : small.Models[0].Id
        };
    }

    /// <summary>某一行上那枚折叠箭头(模板里带手型光标的那个 Border)。</summary>
    private static Border Chevron(ListBox list, ProviderNavItem row)
        => list.GetVisualDescendants().OfType<Border>()
               .First(b => ReferenceEquals(b.DataContext, row) && b.Cursor is not null);

    /// <summary>某一行上的名字(那一行还有一个显示模型个数的 TextBlock,按文字认)。</summary>
    private static TextBlock RowName(ListBox list, ProviderNavItem row)
        => list.GetVisualDescendants().OfType<TextBlock>()
               .First(t => ReferenceEquals(t.DataContext, row) && t.Text == row.Text);

    /// <summary>在某个控件上按一下左键(事件照常往上冒泡)。</summary>
    private static void Press(Control control)
        => control.RaiseEvent(new PointerPressedEventArgs(
            control, new Pointer(0, PointerType.Mouse, true), control, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));

    private static void PressKey(ListBox list, Key key)
        => list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    [TestMethod]
    public void Collapse_TakesTheModelRowsOutOfTheListAndIsRemembered()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                Assert.HasCount(5, (List<ProviderNavItem>)list.ItemsSource!);

                // 供应商行上的 ← 折起这一家
                list.SelectedIndex = 0;
                await PumpAsync();
                PressKey(list, Key.Left);
                await PumpAsync();

                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.HasCount(3, rows, "Routin 的两个模型收起来了,它自己那一行留着");
                Assert.AreEqual("Routin", rows[0].Text);
                Assert.IsFalse(rows[0].Expanded);
                Assert.AreEqual("2", rows[0].CountText, "折起来也得看得出这一家有几个模型");
                Assert.AreEqual("Ollama", rows[1].Text);
                Assert.AreEqual(0, list.SelectedIndex, "折的是自己这一家,选中项不动");
                Assert.IsFalse(settings.Providers[0].ModelsExpanded!.Value);

                // 落盘了:重开设置页还是折着的
                AiSettings reloaded = await store.LoadAsync();
                Assert.IsFalse(reloaded.Providers[0].ModelsExpanded!.Value);

                // → 再展开
                PressKey(list, Key.Right);
                await PumpAsync();
                Assert.HasCount(5, (List<ProviderNavItem>)list.ItemsSource!);
                Assert.IsTrue(settings.Providers[0].ModelsExpanded!.Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void Collapse_WithOneOfItsModelsSelected_MovesTheSelectionUpToTheProvider()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.AreEqual(2, list.SelectedIndex, "起手选中的是活跃模型 Claude");

                // 点那家供应商行上的箭头 —— 选中的模型正在里面
                Press(Chevron(list, rows[0]));
                await PumpAsync();

                rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.HasCount(3, rows);
                Assert.AreEqual(0, list.SelectedIndex, "选中项上移到供应商行,而不是留在一个看不见的模型上");
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);
                Assert.IsFalse(settings.Providers[0].ModelsExpanded!.Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ClickingTheProviderName_FoldsItJustLikeTheChevron()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;

                // 点的是名字那几个字,不是那枚 10px 的三角
                Press(RowName(list, rows[0]));
                await PumpAsync();

                rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.HasCount(3, rows, "点名字也折起来");
                Assert.IsFalse(settings.Providers[0].ModelsExpanded!.Value);
                Assert.AreEqual(0, list.SelectedIndex, "顺带选中这一行");
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);

                // 再点一下展开回来
                Press(RowName(list, rows[0]));
                await PumpAsync();
                Assert.HasCount(5, (List<ProviderNavItem>)list.ItemsSource!);
                Assert.IsTrue(settings.Providers[0].ModelsExpanded!.Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ClickingAModelName_JustSelectsIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;

                int before = list.SelectedIndex;

                // 模型行不在折叠热区之列:这一下该被原样放过去,交给 ListBox 自己选
                // (真正的选中要靠命中测试,合成事件驱不动 —— 这里守的是"我们没插手")
                Press(RowName(list, rows[1]));
                await PumpAsync();

                Assert.HasCount(5, (List<ProviderNavItem>)list.ItemsSource!, "列表一行不少");
                Assert.IsNull(settings.Providers[0].ModelsExpanded, "模型行点不出折叠状态来");
                Assert.AreEqual(before, list.SelectedIndex, "更没有被收到供应商行上去");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ALongModelListStartsCollapsed_AShortOneDoesNot()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, _, _) = await ShowAsync(context, ManyModels(40, selectAModel: false));
            try
            {
                var rows = (List<ProviderNavItem>)view.GetControl<ListBox>("ProvidersList").ItemsSource!;
                Assert.HasCount(3, rows, "40 个的那家默认折着,1 个的那家照常展开");
                Assert.AreEqual("Relay", rows[0].Text);
                Assert.IsFalse(rows[0].Expanded);
                Assert.AreEqual("40", rows[0].CountText);
                Assert.IsTrue(rows[1].Expanded);
                Assert.AreEqual("llama3.1", rows[2].Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AutoCollapse_YieldsWhenTheSelectedModelIsInsideThatProvider()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // 活跃模型在那一大家里:折起来的话,右边的表单会停在一个左栏看不见的模型上
            (Window window, SettingsView view, _, _) = await ShowAsync(context, ManyModels(40, selectAModel: true));
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.HasCount(43, rows, "那一大家整个展开:1 + 40,后面还跟着 Ollama 那两行");
                Assert.IsTrue(rows[0].Expanded);
                Assert.AreEqual(40, list.SelectedIndex, "选中的还是那个活跃模型");
                Assert.IsTrue(view.GetControl<StackPanel>("ModelEditor").IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void LeftOnAModelRow_GoesBackToItsProviderWithoutCollapsing()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                Assert.AreEqual(2, list.SelectedIndex);

                PressKey(list, Key.Left);
                await PumpAsync();

                Assert.HasCount(5, (List<ProviderNavItem>)list.ItemsSource!, "一次按键只做一件事:回到父行,不顺手折起来");
                Assert.AreEqual(0, list.SelectedIndex);
                Assert.IsNull(settings.Providers[0].ModelsExpanded, "没折过也没展过,状态该留在「自动」");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AddProviderAndModel_ThenSave_PersistsTwoLayerShape()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, AiSettingsStore store) = await ShowAsync(context, new AiSettings());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                Assert.IsFalse(view.GetControl<Button>("AddModelButton").IsEnabled, "没选中供应商时不能加模型");

                // 「新增供应商」现在开的是「连接供应商」那一页,供应商由它加完再回调进来
                string? requested = "unset";
                view.ProviderCatalogRequested += id => requested = id;
                view.GetControl<Button>("AddButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.IsNull(requested, "点「新增供应商」是开目录页,不带具体条目");

                AiProvider openai = ProviderCatalog.Find("openai")!.CreateProvider();
                settings.Providers.Add(openai);
                settings.ActiveModelId ??= openai.Models[0].Id;
                await store.SaveAsync(settings); // 真实目录在发出 ProviderChanged 回调前已经完成落库。
                view.ReloadFromCatalog(openai.Id);
                await PumpAsync();
                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("OpenAI", settings.Providers[0].Name);
                Assert.HasCount(1, settings.Providers[0].Models);
                Assert.AreEqual(settings.Providers[0].Models[0].Id, settings.ActiveModelId, "头一个模型自动成为活跃模型");
                Assert.AreEqual(0, list.SelectedIndex);
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);

                // 地址先保存，添加空主槽的 Key 走即时落库入口。
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://routin.example/v1";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                view.GetControl<TextBox>("ProviderNewApiKeyBox").Text = "sk-shared";
                Click(view.GetControl<Button>("ProviderAddApiKeyButton"));
                await WaitUntilAsync(() => HasKeyRow(view, openai.Id));
                Assert.AreEqual("https://routin.example/v1", settings.Providers[0].BaseUrl);
                Assert.AreEqual("sk-shared", await store.GetApiKeyAsync(settings.Providers[0].Id));

                // 加第二个模型:挂在选中供应商下,并选中它
                view.GetControl<Button>("AddModelButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.HasCount(2, settings.Providers[0].Models);
                Assert.AreEqual(2, list.SelectedIndex);
                Assert.IsTrue(view.GetControl<StackPanel>("ModelEditor").IsVisible);
                view.GetControl<TextBox>("ModelBox").Text = "claude-opus-5";
                view.GetControl<ComboBox>("ProtocolCombo").SelectedIndex = (int)ChatProtocol.AnthropicMessages + 1;
                view.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();

                AiModelConfig added = settings.Providers[0].Models[1];
                Assert.AreEqual("claude-opus-5", added.Model);
                Assert.AreEqual(ChatProtocol.AnthropicMessages, added.Protocol);
                Assert.IsFalse(added.HasOwnApiKey);
                // 解析:协议来自模型覆盖,地址与 Key 归属继承供应商
                ResolvedModel resolved = settings.FindModel(added.Id)!;
                Assert.AreEqual("https://routin.example/v1", resolved.BaseUrl);
                Assert.AreEqual(settings.Providers[0].Id, resolved.ApiKeyOwnerId);
                Assert.AreEqual("sk-shared", await store.GetApiKeyAsync(resolved.ApiKeyOwnerId));

                // 落盘的是两层结构
                AiSettings reloaded = await store.LoadAsync();
                Assert.HasCount(1, reloaded.Providers);
                Assert.HasCount(2, reloaded.Providers[0].Models);
                Assert.AreEqual(ChatProtocol.AnthropicMessages, reloaded.Providers[0].Models[1].Protocol);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void DeleteProvider_NeedsSecondClick_AndRemovesModelsWithSecrets()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiSettings seed = TwoProviders();
            string routinId = seed.Providers[0].Id;
            string claudeId = seed.Providers[0].Models[1].Id;
            await context.Secrets.SetAsync($"apikey:{routinId}", "sk-routin");
            await context.Secrets.SetAsync($"apikey:{claudeId}", "sk-claude");
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, seed);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                list.SelectedIndex = 0; // Routin
                await PumpAsync();
                Button delete = view.GetControl<Button>("DeleteButton");

                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.HasCount(2, settings.Providers, "第一击只是提示,不删");
                Assert.Contains("2", view.GetControl<TextBlock>("StatusText").Text ?? "", "提示里带着将被连带删除的模型数");

                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("Ollama", settings.Providers[0].Name);
                Assert.AreEqual(settings.Providers[0].Models[0].Id, settings.ActiveModelId, "活跃模型随供应商没了,落到剩下的第一个");
                Assert.IsNull(await context.Secrets.GetAsync($"apikey:{routinId}"));
                Assert.IsNull(await context.Secrets.GetAsync($"apikey:{claudeId}"), "模型的独立 Key 也要清");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 同行新草稿探测只替换行显示,不能抹掉旧探测对仍落库配置的健康证据。
    /// </summary>
    [TestMethod]
    public void LateTestRun_DraftSuccessKeepsNewDisplay_ButOldSavedFailureRecordsHealth()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var held = new SseStub("", jsonContent: "", hold: true); // 空回复 = 探活失败,且被挂住
            using var quick = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                Name = "Routin",
                BaseUrl = held.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                Assert.AreEqual(1, list.SelectedIndex, "前提:起手选中活跃模型那一行");
                Button test = view.GetControl<Button>("TestButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");

                Task oldRun = StartTestAsync(view);
                await held.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(1, held.Requests, "前提:第一轮探活已打到 held 端点并挂住");

                // 切走再切回:LoadEditorAsync 把按钮重新启用 —— 旧一轮还在跑也拦不住再点一次
                list.SelectedIndex = 0;
                await PumpAsync();
                list.SelectedIndex = 1;
                await PumpAsync();
                Assert.IsTrue(test.IsEnabled, "前提:切行把「测试」重新放了出来");

                view.GetControl<TextBox>("BaseUrlBox").Text = quick.BaseUrl; // 第二轮改打能通的地址
                test.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await quick.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(1, quick.Requests, "前提:第二轮打到了 quick 端点");
                await WaitUntilAsync(() => test.IsEnabled);
                Assert.AreEqual(loc.F("TestOk", "OK"), status.Text, "前提:第二轮回写的是成功的那句");
                string passed = status.Text;

                held.Release(); // 旧一轮按已保存的地址失败,只记健康,不覆盖新草稿的 UI 结论
                await oldRun.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.IsTrue(health.IsCooling(model.Id), "草稿成功不能取代已保存端点的失败健康证据");
                Assert.AreEqual(passed, status.Text, "旧一轮的行显示过期,不能翻回失败");
            }
            finally
            {
                held.Release(); // 没断言到这儿也要把在途的探活放掉,别带着挂起请求进 Dispose
                window.Close();
                await PumpAsync(10);
            }
        });
    }

    /// <summary>
    /// 供应商行与首模型行探测同一份落库配置:新模型行先成功,旧供应商行后失败。
    /// 两行各自保留点,共享健康只接纳这个模型最后起跑的有效证据。
    /// </summary>
    [TestMethod]
    public void ProviderAndFirstModelRows_NewerProbePreventsOldFailureFromRecoolingSharedHealth()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var held = new SseStub(ProbeAnswerSse, holdFirstStream: true, firstStreamContent: "");
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                Name = "Routin",
                BaseUrl = held.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            health.Record(model.Id, false); // 测通前先冷却,证明新模型行确实写入成功
            Assert.IsTrue(health.IsCooling(model.Id));
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SetApiKeyAsync(provider.Id, "sk-stable"); // 旧请求与落库相同,有资格回写健康
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                list.SelectedIndex = 0;
                await WaitUntilAsync(() => HasKeyRow(view, provider.Id));

                Task oldRun = StartTestAsync(view);
                string oldBody = await held.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Contains("m1", oldBody, "供应商行确实探测首模型 id");
                Assert.HasCount(1, held.Requests, "供应商行实际探测首模型的已保存地址");

                list.SelectedIndex = 1;
                await PumpAsync();
                Task newRun = StartTestAsync(view);
                await WaitUntilAsync(() => held.Requests.Count == 2);
                Assert.Contains("m1", held.Requests[1], "首模型行也测了同一个已保存的模型 id");
                await newRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(oldRun.IsCompleted, "旧失败仍由 held 挂住,必须晚于模型行的成功");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                Assert.AreEqual(loc.F("TestOk", "OK"), status.Text);
                Assert.IsFalse(health.IsCooling(model.Id), "新模型行测通已落库的端点,应解除预先冷却");

                held.Release();
                await oldRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "同一模型的新探测已起跑,旧供应商行失败不能再冷却它");
                Assert.AreEqual(loc["DotFailed"], rows[0].DotTip, "旧供应商行仍要显示自己的失败点");
                Assert.AreEqual(loc["DotPassed"], rows[1].DotTip, "首模型行仍要显示自己的成功点");
                Assert.AreEqual(loc.F("TestOk", "OK"), status.Text, "未选中的旧行不能覆盖模型行状态");
            }
            finally
            {
                held.Release();
                window.Close();
                await PumpAsync(10);
            }
        });
    }

    [TestMethod]
    public void LateManualFailure_CannotRecoolModelAfterChatAnswered()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m1","choices":[{"index":0,"delta":{"content":"聊天答复"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "OK", holdFirstStream: true, firstStreamContent: "");
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                BaseUrl = endpoint.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [model]
            };
            var settings = new AiSettings
            {
                Providers = [provider], ActiveModelId = model.Id, SuggestFollowUps = false
            };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var panel = new ChatPanelView(context, store);
            var chatWindow = new Window { Width = 800, Height = 600, Content = panel };
            chatWindow.Show();
            await WaitUntilAsync(() => panel.GetControl<ComboBox>("ProviderCombo").SelectedIndex >= 0);
            var health = (ProviderHealth)(typeof(ChatPanelView)
                .GetField("_health", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("ChatPanelView health record is gone"))
                .GetValue(panel)!;
            health.Record(model.Id, false);
            Assert.IsTrue(health.IsCooling(model.Id), "前提:聊天成功前已经冷却");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var settingsWindow = new Window { Width = 900, Height = 700, Content = view };
            settingsWindow.Show();
            await PumpAsync();
            try
            {
                Task manual = StartTestAsync(view);
                string first = await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsTrue(first.Contains("\"stream\":true", StringComparison.Ordinal), "先挂住设置页的真实流式手测请求");
                Assert.IsFalse(manual.IsCompleted);

                panel.SendExternal("请回复");
                await WaitUntilAsync(() => endpoint.Requests.Count >= 2 && !health.IsCooling(model.Id));
                Assert.Contains("\"stream\":true", endpoint.Requests[1], "聊天真实流式请求已答通并解除冷却");
                Assert.IsFalse(manual.IsCompleted, "旧手测须在聊天成功后才释放");

                endpoint.Release(); // 手测此时才收到空回复,行点可以红,健康不能重新冷却
                await manual.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "迟到的手测失败不能覆盖较新的聊天成功");
                Assert.Contains(loc["ProbeEmptyReply"], view.GetControl<TextBlock>("StatusText").Text ?? "",
                    "本行失败显示仍应如实反映自己的探活结果");
            }
            finally
            {
                endpoint.Release();
                panel.Detach();
                settingsWindow.Close();
                chatWindow.Close();
                await PumpAsync(10);
            }
        });
    }

    [TestMethod]
    public void CatalogModelsRefresh_DuringManualProbe_UpdatesCurrentNavigationRow()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub(ProbeAnswerSse, hold: true);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            (Window window, SettingsView view, _, _) = await ShowAsync(context, settings);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var oldRows = (List<ProviderNavItem>)list.ItemsSource!;
                TextBox modelBox = view.GetControl<TextBox>("ModelBox");
                TextBox nameBox = view.GetControl<TextBox>("NameBox");
                modelBox.Text = "draft-model";
                nameBox.Text = "unfinished name";
                Task run = StartTestAsync(view);
                Assert.Contains("draft-model", await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15)),
                    "探活必须实际发送未保存的型号");

                provider.AvailableModels = ["new-catalog-model"];
                view.RefreshCatalogModels(); // 目录拉取完成的同一入口
                var currentRows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.AreNotSame(oldRows[1], currentRows[1], "目录确实重建了导航行");
                Assert.AreEqual(1, list.SelectedIndex, "被动刷新不能抢走正在编辑的模型");
                Assert.AreEqual("draft-model", modelBox.Text);
                Assert.AreEqual("unfinished name", nameBox.Text);

                endpoint.Release();
                await run.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(new Loc("en")["DotPassed"], currentRows[1].DotTip,
                    "在途探活必须点亮重建后的当前行,而非已经脱离列表的旧行");
                Assert.IsTrue(view.GetControl<Border>("TestBadge").IsVisible);
                Assert.AreEqual("draft-model", modelBox.Text, "晚到的探活也不重填草稿");
            }
            finally
            {
                endpoint.Release();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void CatalogModelsRefresh_UpdatesModelChoicesWithoutReplacingDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var provider = settings.Providers[0];
                TextBox modelBox = view.GetControl<TextBox>("ModelBox");
                TextBox priceBox = view.GetControl<TextBox>("PriceInBox");
                ComboBox picker = view.GetControl<ComboBox>("ModelPickCombo");
                StackPanel panel = view.GetControl<StackPanel>("ModelPickPanel");
                modelBox.Text = "draft-model";
                priceBox.Text = "12.34";
                provider.AvailableModels = ["fresh-model"];
                provider.Name = "renamed model owner";
                view.RefreshCatalogModels();
                Assert.IsTrue(panel.IsVisible, "目录拉到型号后下拉应出现");
                Assert.HasCount(1, picker.Items);
                Assert.AreEqual("fresh-model", picker.Items[0]);
                Assert.IsNull(picker.SelectedItem, "未保存的自由输入不应被强制替换为首个候选");
                Assert.AreEqual("draft-model", modelBox.Text);
                Assert.AreEqual("12.34", priceBox.Text);
                string? breadcrumb = view.GetControl<TextBlock>("BreadcrumbText").Text;
                Assert.Contains(provider.Name, breadcrumb!);
                Assert.Contains(settings.Providers[0].Models[1].DisplayName, breadcrumb!);
                Assert.IsFalse(view.GetControl<Control>("PullModelsPanel").IsVisible, "模型行不能显示整家拉取入口");

                provider.AvailableModels = ["draft-model", "another-model"];
                view.RefreshCatalogModels();
                Assert.AreEqual("draft-model", picker.SelectedItem, "草稿后来进入目录时下拉应跟上它");
                Assert.AreEqual("12.34", priceBox.Text, "程序性选中不能自动覆盖用户填写的价格");
                provider.AvailableModels = [];
                view.RefreshCatalogModels();
                Assert.IsFalse(panel.IsVisible, "目录无候选时应收起空下拉");
                Assert.AreEqual("draft-model", modelBox.Text);

                list.SelectedIndex = 0;
                await PumpAsync();
                TextBox providerName = view.GetControl<TextBox>("ProviderNameBox");
                providerName.Text = "unsaved provider";
                provider.AvailableModels = ["one-more-model"];
                view.RefreshCatalogModels();
                Assert.AreEqual(0, list.SelectedIndex);
                Assert.AreEqual("unsaved provider", providerName.Text, "供应商编辑页也不应被动重填");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CatalogModelsRefresh_MaterialisedModel_MergesUntouchedFieldsBeforeUiSave(int draftMode)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // 若程序性选中误走用户选择处理器,这些真实缓存规格会覆盖草稿。
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), """
                {"openai":{"draft-model":{"ctx":99999,"max":9999,"pin":99,"pout":98,"pcache":97}}}
                """);
            var model = new AiModelConfig
            {
                Model = "old-model", MaxInputTokens = 10000, MaxTokens = 1000,
                InputPricePerMillion = 1, OutputPricePerMillion = 2, CachedInputPricePerMillion = 0.5
            };
            ModelsDevCatalog.Apply(model, new ModelSpec(model.Model, model.Name, model.MaxInputTokens, model.MaxTokens,
                model.InputPricePerMillion, model.OutputPricePerMillion, model.CachedInputPricePerMillion, null), newModel: true);
            var provider = new AiProvider { CatalogId = "openai", Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                if (draftMode > 0)
                {
                    view.GetControl<TextBox>("ModelBox").Text = "draft-model";
                    view.GetControl<TextBox>("MaxInputTokensBox").Text = "33333";
                    view.GetControl<TextBox>("PriceInBox").Text = "12.34";
                }
                if (draftMode == 1)
                {
                    view.GetControl<TextBox>("MaxTokensBox").Text = "3333";
                    view.GetControl<TextBox>("PriceOutBox").Text = "23.45";
                    view.GetControl<TextBox>("PriceCachedBox").Text = "3.45";
                }
                for (int round = 1; round <= 2; round++)
                {
                    ModelsDevCatalog.Materialise(provider,
                        [new ModelSpec($"new-model-{round}", "new", 20000 * round, 2000 * round,
                            2 * round, 3 * round, round, true)]);
                    provider.AvailableModels = [model.Model, "draft-model"];
                    await store.SaveAsync(settings); // 与目录消费者一样,先落盘再通知设置页。
                    view.RefreshCatalogModels();
                    await PumpAsync();
                    Assert.AreEqual(draftMode > 0 ? "draft-model" : $"new-model-{round}",
                        view.GetControl<TextBox>("ModelBox").Text);
                    Assert.AreEqual(draftMode > 0 ? "33333" : (20000 * round).ToString(),
                        view.GetControl<TextBox>("MaxInputTokensBox").Text);
                    Assert.AreEqual(draftMode == 1 ? "3333" : (2000 * round).ToString(),
                        view.GetControl<TextBox>("MaxTokensBox").Text);
                    Assert.AreEqual(draftMode > 0 ? "12.34" : (2 * round).ToString(),
                        view.GetControl<TextBox>("PriceInBox").Text);
                    Assert.AreEqual(draftMode == 1 ? "23.45" : (3 * round).ToString(),
                        view.GetControl<TextBox>("PriceOutBox").Text);
                    Assert.AreEqual(draftMode == 1 ? "3.45" : round.ToString(),
                        view.GetControl<TextBox>("PriceCachedBox").Text);
                    Assert.AreEqual(draftMode > 0 ? "draft-model" : model.Model,
                        view.GetControl<ComboBox>("ModelPickCombo").SelectedItem);
                }
                view.GetControl<TextBox>("NameBox").Text = "renamed only";
                view.GetControl<Button>("SaveButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiModelConfig saved = (await store.LoadAsync()).Providers[0].Models.Single(m => m.Id == model.Id);
                Assert.AreEqual("renamed only", saved.Name);
                Assert.AreEqual(draftMode > 0 ? "draft-model" : "new-model-2", saved.Model);
                Assert.AreEqual(draftMode > 0 ? 33333 : 40000, saved.MaxInputTokens);
                Assert.AreEqual(draftMode == 1 ? 3333 : 4000, saved.MaxTokens);
                Assert.AreEqual(draftMode > 0 ? 12.34 : 4, saved.InputPricePerMillion);
                Assert.AreEqual(draftMode == 1 ? 23.45 : 6, saved.OutputPricePerMillion);
                Assert.AreEqual(draftMode == 1 ? 3.45 : 2, saved.CachedInputPricePerMillion);
                Assert.AreEqual(true, saved.SupportsReasoning, "Materialise 更新的能力没有对应表单,保存不能回退它");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogModelsRefresh_PendingEditorLoad_MergesOrDiscardsForNewSelection(bool navigateAway)
    {
        OnUi(async () =>
        {
            using var secretSource = new TestPluginContext();
            var first = new AiModelConfig { Model = "old-model", HasOwnApiKey = true };
            var second = new AiModelConfig { Model = "second-model", MaxInputTokens = 70000, MaxTokens = 7000 };
            ModelsDevCatalog.Apply(first, new ModelSpec(first.Model, first.Name, first.MaxInputTokens,
                first.MaxTokens, 0, 0, 0, null), newModel: true);
            var provider = new AiProvider { Models = [first, second] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = first.Id };
            await secretSource.Secrets.SetAsync($"apikey:{first.Id}", "first-key");
            var secrets = new DelayedSecrets(secretSource.Secrets, $"apikey:{first.Id}");
            using var context = new TestPluginContext { Secrets = secrets };
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                await secrets.Started.WaitAsync(TimeSpan.FromSeconds(15));
                for (int round = 1; round <= 2; round++)
                {
                    ModelsDevCatalog.Materialise(provider,
                        [new ModelSpec($"new-model-{round}", "new", 20000 * round, 2000 * round, 2, 3, 1, true)]);
                    provider.AvailableModels = [first.Model, second.Model];
                    await store.SaveAsync(settings);
                    view.RefreshCatalogModels();
                }
                if (navigateAway)
                {
                    ListBox list = view.GetControl<ListBox>("ProvidersList");
                    list.SelectedItem = ((List<ProviderNavItem>)list.ItemsSource!).Single(row => row.Model == second);
                }
                view.GetControl<TextBox>("NameBox").Text = "pending draft";
                secrets.Release();
                await PumpAsync();
                AiModelConfig selected = navigateAway ? second : first;
                Assert.AreEqual(selected.Model, view.GetControl<TextBox>("ModelBox").Text);
                Assert.AreEqual(selected.MaxInputTokens.ToString(), view.GetControl<TextBox>("MaxInputTokensBox").Text);
                Assert.AreEqual(selected.MaxTokens.ToString(), view.GetControl<TextBox>("MaxTokensBox").Text);
                view.GetControl<Button>("SaveButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiModelConfig saved = (await store.LoadAsync()).Providers[0].Models.Single(m => m.Id == selected.Id);
                Assert.AreEqual("pending draft", saved.Name);
                Assert.AreEqual(navigateAway ? "second-model" : "new-model-2", saved.Model);
                Assert.AreEqual(navigateAway ? 70000 : 40000, saved.MaxInputTokens);
                Assert.AreEqual(navigateAway ? 7000 : 4000, saved.MaxTokens);
                if (navigateAway)
                {
                    ListBox list = view.GetControl<ListBox>("ProvidersList");
                    list.SelectedItem = ((List<ProviderNavItem>)list.ItemsSource!).Single(row => row.Model == first);
                    await PumpAsync();
                    ModelsDevCatalog.Materialise(provider,
                        [new ModelSpec("new-model-3", "new", 60000, 6000, 3, 4, 2, true)]);
                    provider.AvailableModels = [first.Model, second.Model];
                    await store.SaveAsync(settings);
                    view.RefreshCatalogModels();
                    await PumpAsync();
                    Assert.AreEqual("new-model-3", view.GetControl<TextBox>("ModelBox").Text,
                        "切回时必须重置基线,不能把另一模型的值当作草稿");
                    Assert.AreEqual("60000", view.GetControl<TextBox>("MaxInputTokensBox").Text);
                }
            }
            finally { secrets.Release(); window.Close(); }
        });
    }

    /// <summary>
    /// 切走设置行再测,两轮会并发:这一轮(m1)还挂在 held 上,选中行已经换成 m2 并测通。
    /// m1 迟到的<b>失败</b>必须照记到 m1 自己头上(冷却),但状态行与按钮归最后跑完的那一轮 ——
    /// 过期判定若只认全局起跑号,m1 的记录会被整体丢掉,它的失败就石沉大海(review⑥#8)。
    /// </summary>
    [TestMethod]
    public void LateFailureOfADeselectedRow_StillRecordsItsOwnHealth()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var held = new SseStub("", jsonContent: "", hold: true); // m1 的端点:挂住,放行后回空 = 失败
            using var quick = new SseStub(ProbeAnswerSse);
            var m1 = new AiModelConfig { Model = "m1" };
            var m2 = new AiModelConfig { Model = "m2", BaseUrlOverride = quick.BaseUrl };
            var provider = new AiProvider
            {
                Name = "Routin",
                BaseUrl = held.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [m1, m2]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = m1.Id };
            var health = new ProviderHealth();
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            // 落库 Key 让这轮失败确实具备共享健康回写资格,而不只是界面变红。
            // 随后的 m2 回归仍需按模型区分各自的证据。
            await store.SetApiKeyAsync(provider.Id, "sk-round6");
            var loc = new Loc("en");
            health.Record(m2.Id, false); // 测通前先冷却:未记录成功就不可能凭初始默认值蒙混过关
            Assert.IsTrue(health.IsCooling(m2.Id), "前提:m2 已冷却");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.AreEqual(1, list.SelectedIndex, "前提:起手选中活跃模型 m1 那一行");
                Button test = view.GetControl<Button>("TestButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");

                Task oldRun = StartTestAsync(view);
                await held.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(1, held.Requests, "前提:m1 那一轮探活挂住了");

                list.SelectedIndex = 2; // 切到 m2 行
                await PumpAsync();
                Assert.IsTrue(test.IsEnabled, "前提:切行把「测试」重新放了出来");
                Assert.AreEqual(quick.BaseUrl, view.GetControl<TextBox>("BaseUrlBox").Text,
                    "前提:m2 带地址覆盖,表单该显示它自己的端点");

                test.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await quick.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(1, quick.Requests, "前提:m2 那一轮打到了 quick 端点");
                await WaitUntilAsync(() => test.IsEnabled);
                Assert.AreEqual(loc.F("TestOk", "OK"), status.Text, "前提:m2 测通,状态行是成功的那句");
                string passed = status.Text;

                held.Release(); // m1 现在才拿到空回复 —— 它的失败必须记到 m1 头上,界面一个字不许动
                await oldRun.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.IsTrue(health.IsCooling(m1.Id),
                    "迟到的失败归 m1 自己:换行测试不许把它吞掉(review⑥#8)");
                Assert.IsFalse(health.IsCooling(m2.Id), "m2 测通了,不许被牵连冷却");
                Assert.AreEqual(loc["DotFailed"], rows[1].DotTip, "m1 的迟到失败仍点亮它自己的红点");
                Assert.AreEqual(loc["DotPassed"], rows[2].DotTip, "m2 的成功点不受另一模型影响");
                Assert.AreEqual(passed, status.Text, "状态行归最后跑完的那一轮,迟到的旧一轮不许改写");
                Assert.IsTrue(test.IsEnabled, "按钮归最后跑完的那一轮,不许被旧一轮锁死");
            }
            finally
            {
                held.Release(); // 没断言到这儿也要把在途的探活放掉,别带着挂起请求进 Dispose
                window.Close();
                await PumpAsync(10);
            }
        });
    }

    [TestMethod]
    public void SubscriptionRefresh_SameTokenNewEndpoint_DoesNotCoolTheNewCredential()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var oldEndpoint = new SseStub("", jsonContent: "", hold: true);
            using var newEndpoint = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                Name = "subscription", BaseUrl = oldEndpoint.BaseUrl, Auth = AuthMethod.Subscription,
                OAuth = new OAuthConfig { Flow = OAuthFlow.GitHubCopilotDevice, ExchangeUrl = "https://auth.example/exchange" },
                Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SaveTokensAsync(provider.Id, new OAuthTokens
            {
                AccessToken = "same-token", RefreshToken = "long-lived", BaseUrl = oldEndpoint.BaseUrl,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                Task oldRun = StartTestAsync(view);
                await oldEndpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer same-token", oldEndpoint.Authorizations[0]);

                // 旧请求正挂在原端点。下一次解析自动刷新:字符串不变,服务端却下发新端点。
                OAuthStub exchange = new OAuthStub().Json(System.Text.Json.JsonSerializer.Serialize(new
                {
                    token = "same-token",
                    expires_at = 4102444800L,
                    endpoints = new { api = newEndpoint.BaseUrl }
                }));
                using var http = new HttpClient(exchange);
                store.TokenClient = new VelaShell.Plugin.Ai.Auth.OAuthClient(http);
                await store.SaveTokensAsync(provider.Id, new OAuthTokens
                {
                    AccessToken = "same-token", RefreshToken = "long-lived", BaseUrl = oldEndpoint.BaseUrl,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20)
                });
                ProviderCredential refreshed = await store.ResolveCredentialAsync(new ResolvedModel(provider, model));
                Assert.AreEqual(newEndpoint.BaseUrl, refreshed.BaseUrl);
                Assert.AreEqual("same-token", refreshed.Value, "真正发生的是同文本令牌刷新");
                Assert.HasCount(1, exchange.Requests, "令牌确实从 OAuth 交换端点刷新");

                oldEndpoint.Release();
                await oldRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "旧地址上的失败不能冷却同字符串的新地址");
                health.Record(model.Id, false); // 新地址探活成功必须解除这次冷却,否则只断言初始默认值会假绿
                Task newRun = StartTestAsync(view);
                await newEndpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer same-token", newEndpoint.Authorizations[0]);
                await newRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "新地址的成功应该写入健康记录");
            }
            finally
            {
                oldEndpoint.Release();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void SubscriptionSameToken_NewAccountHeader_DoesNotCoolTheNewAccount()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub(ProbeAnswerSse, holdFirstStream: true, firstStreamContent: "");
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                Name = "subscription", BaseUrl = endpoint.BaseUrl, Auth = AuthMethod.Subscription,
                OAuth = new OAuthConfig { ExtraHeaders = "chatgpt-account-id: {account_id}" }, Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "same-token", AccountId = "old-account" });
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                Task oldRun = StartTestAsync(view);
                await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("old-account", endpoint.AccountIds[0], "旧请求真正发了旧账号头");
                await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "same-token", AccountId = "new-account" });
                endpoint.Release();
                await oldRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "旧账号的失败不能冷却同 token 的新账号");

                health.Record(model.Id, false);
                Task newRun = StartTestAsync(view);
                await WaitUntilAsync(() => endpoint.Requests.Count == 2);
                await newRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("new-account", endpoint.AccountIds[1], "第二轮真实请求必须改用新账号头");
                Assert.IsFalse(health.IsCooling(model.Id), "新账号成功应清掉已有冷却");
            }
            finally
            {
                endpoint.Release();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void SubscriptionSameToken_ChangedBearerShape_DoesNotCoolTheKeyCredential()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub("", jsonContent: "", hold: true);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                BaseUrl = endpoint.BaseUrl, Auth = AuthMethod.Subscription,
                OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken }, Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "same-token" });
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                Task run = StartTestAsync(view);
                await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer same-token", endpoint.Authorizations[0]);
                provider.OAuth!.Credential = OAuthCredential.ApiKey;
                await store.SaveAsync(settings);
                ProviderCredential now = await store.ResolveCredentialAsync(new ResolvedModel(provider, model));
                Assert.AreEqual("same-token", now.Value);
                Assert.IsFalse(now.IsBearerToken, "落库凭据现在是普通 Key,不再是 Bearer 令牌");

                endpoint.Release();
                await run.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "相同文本不能将 Bearer 请求的失败记给新 Key");
            }
            finally
            {
                endpoint.Release();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void UnsavedProviderKey_StillOverridesTheProbeWithoutChangingSavedHealth()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            health.Record(model.Id, false);
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SetApiKeyAsync(provider.Id, "saved-key");
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await WaitUntilAsync(() => HasKeyRow(view, provider.Id));
                EditProviderKey(view, provider.Id).Text = "draft-key";
                Task run = StartTestAsync(view);
                await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                await run.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer draft-key", endpoint.Authorizations[0], "实际请求必须使用未保存表单 Key");
                Assert.AreEqual("saved-key", await store.GetApiKeyAsync(provider.Id));
                Assert.IsTrue(health.IsCooling(model.Id), "草稿 Key 测通不得清除落库配置的冷却");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 故障转移链引用的是模型 id:删模型(首击即删)与删供应商(两击确认、连带模型)都会让
    /// 链上条目变成死引用 —— 不剪掉,链会把流量递给一个已经不存在的模型,切换永远失败(review⑥#9)。
    /// </summary>
    [TestMethod]
    public void DeletingModelsOrProviders_PrunesTheFailoverChain()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiSettings seed = TwoProviders();
            string gpt5Id = seed.Providers[0].Models[0].Id;
            string claudeId = seed.Providers[0].Models[1].Id;
            string llamaId = seed.Providers[1].Models[0].Id;
            seed.FailoverChain =
            [
                new FailoverEntry { ModelId = gpt5Id },
                new FailoverEntry { ModelId = claudeId },
                new FailoverEntry { ModelId = llamaId }
            ];
            (Window window, SettingsView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, seed);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                Assert.AreEqual(2, list.SelectedIndex, "前提:起手选中活跃模型 Claude 那一行");
                Button delete = view.GetControl<Button>("DeleteButton");

                // 模型行首击即删:删掉 Claude,链上它那条成了死引用
                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(40);
                Assert.HasCount(2, settings.FailoverChain, "删模型把死条目剪掉(review⑥#9)");
                Assert.AreEqual(gpt5Id, settings.FailoverChain[0].ModelId, "活着的条目按原序保留");
                Assert.AreEqual(llamaId, settings.FailoverChain[1].ModelId, "活着的条目按原序保留");
                AiSettings reloaded = await store.LoadAsync();
                Assert.HasCount(2, reloaded.FailoverChain, "剪掉的结果要落盘");
                Assert.AreEqual(gpt5Id, reloaded.FailoverChain[0].ModelId);
                Assert.AreEqual(llamaId, reloaded.FailoverChain[1].ModelId);

                // 供应商行两击才删:第一击只是提示,Routin 连带 gpt-5 一起没,链上再剪一条
                list.SelectedIndex = 0; // Routin 行
                await PumpAsync();
                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.HasCount(2, settings.FailoverChain, "第一击只是提示,不删也不剪");
                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(40);
                Assert.HasCount(1, settings.FailoverChain,
                    "供应商连模型一起删,死条目跟着剪(review⑥#9)");
                Assert.AreEqual(llamaId, settings.FailoverChain[0].ModelId, "只剩还活着的 llama");
                reloaded = await store.LoadAsync();
                Assert.HasCount(1, reloaded.FailoverChain, "剪掉的结果要落盘");
                Assert.AreEqual(llamaId, reloaded.FailoverChain[0].ModelId);
            }
            finally
            {
                window.Close();
            }
        });
    }
    [TestMethod]
    public void ManageSignIn_FromEarlierSubscription_PassesSelectedInstanceRatherThanCatalog()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("openrouter")!.CreateProvider();
            AiProvider last = ProviderCatalog.Find("openrouter")!.CreateProvider();
            AiSettings settings = new() { Providers = [first, last], ActiveModelId = first.Models[0].Id };
            (Window window, SettingsView view, _, _) = await ShowAsync(context, settings);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                string? requested = "unset";
                view.ProviderCatalogRequested += id => requested = id;
                list.SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<Button>("ManageSignInButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(first.Id, requested);
                list.SelectedIndex = 2;
                await PumpAsync();
                view.GetControl<Button>("ManageSignInButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(last.Id, requested);
                view.GetControl<Button>("AddButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.IsNull(requested, "新建入口仍无指定实例");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChatSettings_CatalogSaveRefreshesContextAndOffersRetryWithoutReplacingDraft(bool dirty)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // 目录规格不参与本例:端点拒绝后应保留原模型,仍显示手动重试入口。
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            using var endpoint = new ErrorStub();
            AiProvider provider = ProviderCatalog.Custom.CreateProvider();
            provider.Name = "original account";
            provider.BaseUrl = "";
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(provider.Id, "original-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false
            });
            var chat = new ChatPanelView(context, store);
            var chatHost = new Window { Width = 900, Height = 700, Content = chat };
            Window? settingsHost = null;
            Window? catalogHost = null;
            chatHost.Show();
            try
            {
                await PumpAsync();
                chat.GetControl<Button>("SettingsButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = new Window { Width = 900, Height = 700, Content = editor };
                settingsHost.Show();
                await PumpAsync();
                editor.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                TextBox name = editor.GetControl<TextBox>("ProviderNameBox");
                TextBox address = editor.GetControl<TextBox>("ProviderBaseUrlBox");
                TextBox? key = dirty ? EditProviderKey(editor, provider.Id) : null;
                Assert.AreEqual(provider.Name, editor.GetControl<TextBlock>("BreadcrumbText").Text);
                Assert.IsFalse(editor.GetControl<Control>("PullModelsPanel").IsVisible);
                if (dirty)
                {
                    name.Text = "unfinished account";
                    address.Text = "https://unfinished.example/v1";
                    key!.Text = "unfinished-key";
                }
                editor.GetControl<Button>("AddButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var catalog = (ProviderSetupView)context.FakeUi.LastPanel.CreateContent();
                catalogHost = new Window { Width = 900, Height = 700, Content = catalog };
                catalogHost.Show();
                catalog.FocusEntry(provider.CatalogId, provider.Id);
                await PumpAsync();
                T Find<T>(string controlName) where T : Control => catalog.GetLogicalDescendants()
                    .OfType<T>().Single(control => control.Name == controlName);
                string savedName = "renamed account";
                string savedKey = "catalog-key";
                Find<TextBox>("SetupNameBox").Text = savedName;
                Find<TextBox>("SetupKeyBox").Text = savedKey;
                Find<ToggleButton>("SetupAdvancedToggle").IsChecked = true;
                Find<TextBox>("SetupBaseUrlBox").Text = endpoint.BaseUrl;
                var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                catalog.ProviderChanged += _ => saved.TrySetResult();
                Task save = ClickAndWaitAsync(Find<Button>("SetupPrimaryButton"));
                await saved.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await save;
                AiProvider persisted = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual(savedName, persisted.Name);
                Assert.AreEqual(endpoint.BaseUrl, persisted.BaseUrl);
                Assert.IsTrue(endpoint.Requests > 0, "目录保存后确实尝试过自动拉取");
                Assert.IsEmpty(persisted.AvailableModels, "拒绝请求不能伪造成功的型号清单");
                Assert.AreEqual(savedName, editor.GetControl<TextBlock>("BreadcrumbText").Text);
                Assert.IsTrue(editor.GetControl<Control>("PullModelsPanel").IsVisible);
                Assert.AreEqual(dirty ? "unfinished account" : savedName, name.Text);
                Assert.AreEqual(dirty ? "https://unfinished.example/v1" : endpoint.BaseUrl, address.Text);
                if (dirty) Assert.AreEqual("unfinished-key", key!.Text);
                else
                {
                    Button reveal = KeyControl<Button>(editor, provider.Id, "ProviderKeyRevealButton");
                    Click(reveal);
                    Assert.Contains(savedKey, ((TextBlock)reveal.Content!).Text ?? "");
                }
                Assert.IsTrue(editor.GetControl<Button>("PullModelsButton").IsEnabled,
                    "自动拉取失败后手动重试入口必须可用");
            }
            finally
            {
                catalogHost?.Close();
                settingsHost?.Close();
                chat.Detach();
                chatHost.Close();
            }

            static async Task ClickAndWaitAsync(Button button)
            {
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
                {
                    if (args.Property == Button.IsEnabledProperty && button.IsEnabled)
                        completed.TrySetResult();
                }
                button.PropertyChanged += OnChanged;
                try
                {
                    button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally { button.PropertyChanged -= OnChanged; }
            }
        });
    }

    [TestMethod]
    public void CatalogSignIn_RefreshesSubscriptionStatusWithoutReplacingDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("openrouter")!.CreateProvider();
            AiSettings settings = new() { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                var loc = new Loc("en");
                TextBlock status = view.GetControl<TextBlock>("ProviderAuthStatusText");
                await WaitUntilAsync(() => status.Text == loc["SubscriptionNotSignedIn"]);
                TextBox name = view.GetControl<TextBox>("ProviderNameBox");
                TextBox address = view.GetControl<TextBox>("ProviderBaseUrlBox");
                name.Text = "unsaved account name";
                address.Text = "https://draft.example/v1";
                var obtainedAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
                await store.SaveTokensAsync(provider.Id, new OAuthTokens
                {
                    AccessToken = "new-account-token",
                    ObtainedAt = obtainedAt
                });
                view.ReloadFromCatalog(provider.Id);
                string signedIn = loc.F("SubscriptionSignedIn", obtainedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                await WaitUntilAsync(() => status.Text == signedIn);
                Assert.AreEqual("unsaved account name", name.Text);
                Assert.AreEqual("https://draft.example/v1", address.Text);

                await store.ClearTokensAsync(provider.Id);
                view.ReloadFromCatalog(provider.Id);
                await WaitUntilAsync(() => status.Text == loc["SubscriptionNotSignedIn"]);
                Assert.AreEqual("unsaved account name", name.Text);
                Assert.AreEqual("https://draft.example/v1", address.Text);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void CatalogAddsFirstModel_ClearsNoModelsWithoutReplacingProviderDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.Models.Clear();
            AiSettings settings = new() { Providers = [provider] };
            (Window window, SettingsView view, _, _) = await ShowAsync(context, settings);
            try
            {
                TextBox name = view.GetControl<TextBox>("ProviderNameBox");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                Assert.AreEqual(new Loc("en")["NoModels"], status.Text);
                name.Text = "unsaved provider draft";
                provider.Models.Add(new AiModelConfig { Model = "first-model" });
                view.ReloadFromCatalog(provider.Id);
                Assert.AreEqual("", status.Text);
                Assert.AreEqual("unsaved provider draft", name.Text);
                Assert.AreEqual(provider.Id, ((ProviderNavItem)view.GetControl<ListBox>("ProvidersList").SelectedItem!).Provider.Id);
                status.Text = new Loc("en")["NoModels"];
                view.RefreshCatalogModels();
                Assert.AreEqual("", status.Text);
                Assert.AreEqual("unsaved provider draft", name.Text);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ModelProbe_UsesDraftReasoningAndOnlyCoolsMatchingSavedReasoning()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub("", holdFirstStream: true, firstStreamContent: "");
            var model = new AiModelConfig { Model = "m1", Reasoning = ReasoningLevel.High };
            var provider = new AiProvider
            {
                BaseUrl = endpoint.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var health = new ProviderHealth();
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, health);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            await PumpAsync();
            try
            {
                view.GetControl<ComboBox>("ReasoningCombo").SelectedIndex = (int)ReasoningLevel.Low;
                Task draftProbe = StartTestAsync(view);
                string firstRequest = await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Contains("\"reasoning_effort\":\"low\"", firstRequest,
                    "手动探活发送的是当前未保存档位,不是已保存的 High");
                Assert.IsFalse(draftProbe.IsCompleted);
                endpoint.Release();
                await draftProbe.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "草稿 Low 的失败不能冷却仍为 High 的已保存模型");

                view.GetControl<Button>("SaveButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => model.Reasoning == ReasoningLevel.Low
                    && view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                Task savedProbe = StartTestAsync(view);
                await WaitUntilAsync(() => endpoint.Requests.Count == 2);
                await savedProbe.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Contains("\"reasoning_effort\":\"low\"", endpoint.Requests[1]);
                Assert.IsTrue(health.IsCooling(model.Id), "请求档位与落库一致后失败才写入冷却");
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Save_NavigatingDuringSecretMutation_PersistsAndNotifiesWithoutReplacingNewDraft(bool providerRow, bool keepKey)
    {
        OnUi(async () =>
        {
            using var secretSource = new TestPluginContext();
            var model = new AiModelConfig { Name = "original model", Model = "m1", HasOwnApiKey = !keepKey };
            var first = new AiProvider { Name = "A", BaseUrl = "https://a.example/v1", Models = [model] };
            var second = new AiProvider { Name = "B", BaseUrl = "https://b.example/v1" };
            var settings = new AiSettings { Providers = [first, second], ActiveModelId = model.Id };
            string ownerId = providerRow ? first.Id : model.Id;
            if (providerRow || !keepKey)
                await secretSource.Secrets.SetAsync($"apikey:{ownerId}", "old-key");
            await secretSource.Secrets.SetAsync($"apikey:{second.Id}", "key-b");
            var secrets = new DelayedSecrets(secretSource.Secrets, $"apikey:{ownerId}", delayMutation: true);
            using var context = new TestPluginContext { Secrets = secrets };
            int notifications = 0;
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings, () =>
            {
                notifications++;
                notified.TrySetResult();
            });
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                list.SelectedItem = rows.Single(row => providerRow ? row.Provider == first && row.IsProvider : row.Model == model);
                await PumpAsync();
                view.GetControl<TextBox>(providerRow ? "ProviderNameBox" : "NameBox").Text = "saved A draft";
                if (!providerRow) view.GetControl<CheckBox>("OwnKeyCheck").IsChecked = keepKey;
                if (providerRow) EditProviderKey(view, first.Id).Text = "new-key";
                else view.GetControl<TextBox>("ApiKeyBox").Text = keepKey ? "new-key" : "";
                view.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await secrets.Started.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(0, notifications, "机密尚未完成,不能提前通知消费者");

                list.SelectedItem = rows.Single(row => row.Provider == second && row.IsProvider);
                await WaitUntilAsync(() => HasKeyRow(view, second.Id));
                view.GetControl<TextBox>("ProviderNameBox").Text = "unsaved B draft";
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://draft-b.example/v1";
                TextBox secondDraft = EditProviderKey(view, second.Id);
                secondDraft.Text = "draft-key-b";
                string? status = view.GetControl<TextBlock>("StatusText").Text;
                secrets.Release();
                await notified.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await PumpAsync();

                AiSettings persisted = await store.LoadAsync();
                AiProvider savedFirst = persisted.Providers.Single(provider => provider.Id == first.Id);
                AiModelConfig savedModel = savedFirst.Models.Single(saved => saved.Id == model.Id);
                Assert.AreEqual("saved A draft", providerRow ? savedFirst.Name : savedModel.Name);
                if (!providerRow)
                {
                    Assert.AreEqual(keepKey, model.HasOwnApiKey);
                    Assert.AreEqual(keepKey, savedModel.HasOwnApiKey, "独立 Key 开关必须与机密写入/删除一起落盘");
                }
                Assert.AreEqual(keepKey ? "new-key" : null, await context.Secrets.GetAsync($"apikey:{ownerId}"));
                Assert.AreEqual(keepKey ? "new-key" : null, await store.GetApiKeyAsync(ownerId));
                Assert.AreEqual(1, notifications, "真实设置变更回调必须恰好执行一次");
                Assert.AreSame(second, ((ProviderNavItem)list.SelectedItem!).Provider);
                Assert.AreEqual("unsaved B draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.AreEqual("https://draft-b.example/v1", view.GetControl<TextBox>("ProviderBaseUrlBox").Text);
                Assert.AreEqual("draft-key-b", secondDraft.Text);
                Assert.AreEqual(status, view.GetControl<TextBlock>("StatusText").Text, "旧保存不得回写新行状态");
                Assert.AreEqual("B", persisted.Providers.Single(provider => provider.Id == second.Id).Name);
                Assert.AreEqual("key-b", await context.Secrets.GetAsync($"apikey:{second.Id}"));
            }
            finally { secrets.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void Save_WaitingForInitialKey_NavigationBackDoesNotSaveNewDraft()
    {
        OnUi(async () =>
        {
            using var secretSource = new TestPluginContext();
            var first = new AiProvider { Name = "A", BaseUrl = "https://a.example/v1" };
            var second = new AiProvider { Name = "B", BaseUrl = "https://b.example/v1" };
            var settings = new AiSettings { Providers = [first, second] };
            await secretSource.Secrets.SetAsync($"apikey:{first.Id}", "key-a");
            var secrets = new DelayedSecrets(secretSource.Secrets, $"apikey:{first.Id}");
            using var context = new TestPluginContext { Secrets = secrets };
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                await secrets.Started.WaitAsync(TimeSpan.FromSeconds(15));
                Task saving = (Task)typeof(SettingsView).GetMethod("SaveAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null)!;
                Assert.IsFalse(saving.IsCompleted, "保存确实在等首次机密读取");
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                list.SelectedItem = rows.Single(row => row.Provider == second && row.IsProvider);
                list.SelectedItem = rows.Single(row => row.Provider == first && row.IsProvider);
                Assert.AreEqual("A", view.GetControl<TextBox>("ProviderNameBox").Text);
                view.GetControl<TextBox>("ProviderNameBox").Text = "revisited A draft";
                secrets.Release();
                await saving.WaitAsync(TimeSpan.FromSeconds(15));
                await WaitUntilAsync(() => HasKeyRow(view, first.Id));
                Assert.AreEqual("A", first.Name, "旧保存不能提交重访后的草稿");
                Assert.AreEqual("A", (await store.LoadAsync()).Providers[0].Name);
                Assert.AreEqual("revisited A draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.AreEqual(first.Id, ((ProviderNavItem)list.SelectedItem!).Provider.Id);
                Assert.AreEqual("key-a", await store.GetApiKeyAsync(first.Id));
            }
            finally { secrets.Release(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogRefresh_DeleteConfirmation_OnlySurvivesSameSelection(bool sameSelection)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiSettings settings = TwoProviders();
            AiProvider first = settings.Providers[0];
            AiProvider second = settings.Providers[1];
            string modelId = first.Models[1].Id;
            await context.Secrets.SetAsync($"apikey:{first.Id}", "provider-secret");
            await context.Secrets.SetAsync($"apikey:{modelId}", "model-secret");
            (Window window, SettingsView view, _, _) = await ShowAsync(context, settings);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                list.SelectedIndex = 0;
                await PumpAsync();
                Button delete = view.GetControl<Button>("DeleteButton");
                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                if (!sameSelection) view.ReloadFromCatalog(second.Id);
                view.ReloadFromCatalog(first.Id);
                await PumpAsync();
                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                if (sameSelection)
                {
                    Assert.HasCount(1, settings.Providers, "同选择刷新不撤销第一击确认");
                    Assert.IsNull(await context.Secrets.GetAsync($"apikey:{first.Id}"));
                    Assert.IsNull(await context.Secrets.GetAsync($"apikey:{modelId}"));
                }
                else
                {
                    Assert.HasCount(2, settings.Providers, "目录导航 A-B-A 后仍需重新两击确认");
                    Assert.AreSame(first, settings.Providers[0]);
                    Assert.AreEqual("provider-secret", await context.Secrets.GetAsync($"apikey:{first.Id}"));
                    Assert.AreEqual("model-secret", await context.Secrets.GetAsync($"apikey:{modelId}"));
                    Assert.AreEqual(new Loc("en").F("DeleteProviderConfirm", first.Models.Count),
                        view.GetControl<TextBlock>("StatusText").Text);
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogRefresh_RemovedSelectionKeepsDraftUntilExplicitNavigation(bool modelsRefresh)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var first = new AiProvider { Name = "A", BaseUrl = "https://a.example/v1" };
            var second = new AiProvider { Name = "B", BaseUrl = "https://b.example/v1" };
            var settings = new AiSettings { Providers = [first, second] };
            await context.Secrets.SetAsync($"apikey:{first.Id}", "key-a");
            await context.Secrets.SetAsync($"apikey:{second.Id}", "key-b");
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://draft-a.example/v1";
                settings.Providers.Remove(first);
                await store.SaveAsync(settings);
                if (modelsRefresh) view.RefreshCatalogModels();
                else view.ReloadFromCatalog(first.Id);
                if (modelsRefresh)
                {
                    Assert.AreEqual(-1, view.GetControl<ListBox>("ProvidersList").SelectedIndex);
                    Assert.AreEqual("https://draft-a.example/v1", view.GetControl<TextBox>("ProviderBaseUrlBox").Text);
                    Assert.IsFalse(view.GetControl<Button>("SaveButton").IsEnabled);
                    view.RefreshCatalogModels();
                    Assert.AreEqual(-1, view.GetControl<ListBox>("ProvidersList").SelectedIndex,
                        "第二次被动刷新仍不能自动丢弃草稿并转选");
                    view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                }
                await WaitUntilAsync(() => HasKeyRow(view, second.Id));
                Assert.AreEqual("B", view.GetControl<TextBox>("ProviderNameBox").Text);
                view.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual("https://b.example/v1", saved.Providers[0].BaseUrl);
                Assert.AreEqual("key-b", await store.GetApiKeyAsync(second.Id), "A 的地址和机密不能写进 B");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogRefresh_ExternalConnectionChanges_MergesOnlyUntouchedFields(bool hasConnectionDraft)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var provider = new AiProvider { Name = "original", BaseUrl = "https://old.example/v1" };
            var settings = new AiSettings { Providers = [provider] };
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "old-key");
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                view.GetControl<TextBox>("ProviderNameBox").Text = "renamed draft";
                if (hasConnectionDraft)
                {
                    view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://draft.example/v1";
                    EditProviderKey(view, provider.Id).Text = "draft-key";
                    view.GetControl<ComboBox>("ProviderProtocolCombo").SelectedIndex = (int)ChatProtocol.OpenAiResponses;
                }
                provider.BaseUrl = "https://new.example/v1";
                provider.DefaultProtocol = ChatProtocol.AnthropicMessages;
                await store.SetApiKeyAsync(provider.Id, "new-key");
                await store.SaveAsync(settings);
                view.ReloadFromCatalog(provider.Id);
                view.RefreshCatalogModels();
                if (hasConnectionDraft)
                {
                    Click(view.GetControl<Button>("SaveButton"));
                    await WaitUntilAsync(() => (view.GetControl<TextBlock>("StatusText").Text ?? "").Contains(new Loc("en")["SetupConfigChanged"], StringComparison.Ordinal));
                    Assert.AreEqual("draft-key", KeyControl<TextBox>(view, provider.Id, "ProviderKeyEditBox").Text,
                        "外部修改后拒绝覆盖并保留新 Key 草稿");
                    Click(KeyControl<Button>(view, provider.Id, "ProviderKeyEditCancelButton"));
                    EditProviderKey(view, provider.Id).Text = "draft-key";
                }
                view.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiProvider saved = (await store.LoadAsync()).Providers[0];
                Assert.AreEqual("renamed draft", saved.Name);
                Assert.AreEqual(hasConnectionDraft ? "https://draft.example/v1" : "https://new.example/v1", saved.BaseUrl);
                Assert.AreEqual(hasConnectionDraft ? ChatProtocol.OpenAiResponses : ChatProtocol.AnthropicMessages, saved.DefaultProtocol);
                Assert.AreEqual(hasConnectionDraft ? "draft-key" : "new-key", await store.GetApiKeyAsync(provider.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("TemperatureBox", "0.75")]
    [DataRow("TopPBox", "0.9")]
    [DataRow("StopBox", "new-stop")]
    public void ModelProbe_SamplingSnapshot_OnlyRecordsMatchingSavedParameters(string changedBox, string newValue)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub("", holdFirstStream: true, firstStreamContent: "");
            var model = new AiModelConfig { Model = "m1", Temperature = 0.25f, TopP = 0.5f, StopSequences = "stop-one\nstop-two" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var health = new ProviderHealth();
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            await PumpAsync();
            try
            {
                Task oldProbe = StartTestAsync(view);
                string request = await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                using (System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(request))
                {
                    Assert.AreEqual(0.25f, body.RootElement.GetProperty("temperature").GetSingle());
                    Assert.AreEqual(0.5f, body.RootElement.GetProperty("top_p").GetSingle());
                    CollectionAssert.AreEqual(new[] { "stop-one", "stop-two" },
                        body.RootElement.GetProperty("stop").EnumerateArray().Select(s => s.GetString()!).ToArray());
                }
                view.GetControl<TextBox>(changedBox).Text = newValue;
                view.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                endpoint.Release();
                await oldProbe.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsFalse(health.IsCooling(model.Id), "旧采样参数的迟到失败不能冷却新配置");
                Task currentProbe = StartTestAsync(view);
                await currentProbe.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsTrue(health.IsCooling(model.Id), "当前已保存采样参数的失败必须记入健康");
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }
    private sealed class CatalogueIndexTransport(string baseUrl) : DelegatingHandler(new HttpClientHandler())
    {
        public Uri? RequestedUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Method != HttpMethod.Get || request.RequestUri != new Uri(ModelsDevCatalog.SourceUrl)
                || request.Headers.Authorization is not null)
                throw new InvalidOperationException("Only the unauthenticated public model index may use this transport.");
            request.RequestUri = new Uri(baseUrl + "/api.json");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class KeyProbeServer(Func<string?, int> status, bool holdFirst = false, string? jsonResponse = null) : IDisposable
    {
        private readonly System.Net.HttpListener _listener = StartListener();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(string? Auth, string Body)> _requests = [];
        private readonly Lock _gate = new();
        public string BaseUrl => _listener.Prefixes.Single().TrimEnd('/');
        public IReadOnlyList<(string? Auth, string Body)> Requests { get { lock (_gate) return [.. _requests]; } }

        private static System.Net.HttpListener StartListener()
        {
            for (int attempt = 0; ; attempt++)
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                probe.Start();
                int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                var listener = new System.Net.HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try { listener.Start(); return listener; }
                catch (System.Net.HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183) { listener.Close(); }
            }
        }

        public void Start() => _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    System.Net.HttpListenerContext request = await _listener.GetContextAsync();
                    string body;
                    using (var reader = new StreamReader(request.Request.InputStream)) body = await reader.ReadToEndAsync();
                    string? auth = request.Request.Headers["Authorization"];
                    int count;
                    lock (_gate) { _requests.Add((auth, body)); count = _requests.Count; }
                    int code = status(auth);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (holdFirst && count == 1) await _release.Task;
                            request.Response.StatusCode = code;
                            request.Response.ContentType = code == 200 && jsonResponse is null ? "text/event-stream" : "application/json";
                            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(code == 200 ? jsonResponse ?? ProbeAnswerSse
                                : "{\"error\":{\"message\":\"invalid api key\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}");
                            request.Response.ContentLength64 = bytes.Length;
                            await request.Response.OutputStream.WriteAsync(bytes);
                            request.Response.Close();
                        }
                        catch (Exception) { }
                    });
                }
            }
            catch (Exception) { }
        });
        public void Release() => _release.TrySetResult();
        public void Dispose() { Release(); _listener.Close(); }
    }

    private sealed class KeySettingsStorage(VelaShell.PluginSdk.Storage.IPluginStorage inner) : VelaShell.PluginSdk.Storage.IPluginStorage
    {
        public bool Fail { get; set; }
        public int SettingsReadsUntilHold { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldWrites { get; set; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (key == "settings" && HoldWrites)
            {
                WriteStarted.TrySetResult();
                await WriteRelease.Task.WaitAsync(cancellationToken);
            }
            if (Fail) throw new IOException("Storage failed");
            await inner.SetAsync(key, value, cancellationToken);
        }
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);
        public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default) => inner.GetKeysAsync(cancellationToken);
    }
    private sealed class KeySettingsSecrets(ISecretsApi inner) : ISecretsApi
    {
        public bool FailNextMutation { get; set; }
        public bool FailReads { get; set; }
        private bool TakeFailure() { bool fail = FailNextMutation; FailNextMutation = false; return fail; }
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
            => FailReads ? Task.FromException<string?>(new IOException("Secret read failed")) : inner.GetAsync(name, cancellationToken);
        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
            => TakeFailure() ? Task.FromException(new IOException("Secret failed")) : inner.SetAsync(name, value, cancellationToken);
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
            => TakeFailure() ? Task.FromException<bool>(new IOException("Secret failed")) : inner.DeleteAsync(name, cancellationToken);
    }

    private static object KeyProbeState(SettingsView view, string keyId) =>
        ((System.Collections.IEnumerable)typeof(SettingsView).GetField("_keyRows",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!)
        .Cast<object>().Single(row => (string?)row.GetType().GetProperty("Id")!.GetValue(row) == keyId);

    private static bool IsKeyProbeRunning(object state) => state.GetType().GetProperty("ProbeCts")!.GetValue(state) is not null;


    private static void SelectKeyModel(SettingsView view, string keyId, string modelId)
    {
        ComboBox picker = KeyControl<ComboBox>(view, keyId, "ProviderKeyModelPicker");
        picker.SelectedItem = picker.ItemsSource!.Cast<object>().Single(choice =>
            (string?)choice.GetType().GetProperty("Id")!.GetValue(choice) == modelId);
    }

    [TestMethod]
    public void ProviderApiKey_ManualProvider_AddEditCancelRemoveAndReopen()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { Name = "manual", BaseUrl = "https://manual.example/v1", Models = [model] };
            var settings = new AiSettings { Providers = [provider] };
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-primary-1234");
            int notifications = 0;
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings, () => notifications++);
            try
            {
                Assert.IsNull(provider.CatalogId);
                Assert.IsFalse(view.GetControl<CheckBox>("BalanceApiKeysCheck").IsChecked);
                Assert.HasCount(1, view.GetControl<StackPanel>("ProviderApiKeyRows").Children);
                foreach (string key in new[] { "sk-second-5678", "sk-third-9012" })
                {
                    view.GetControl<TextBox>("ProviderNewApiKeyBox").Text = key;
                    Click(view.GetControl<Button>("ProviderAddApiKeyButton"));
                    await WaitUntilAsync(() => view.GetControl<TextBox>("ProviderNewApiKeyBox").Text == "");
                }
                Assert.HasCount(2, provider.AdditionalApiKeyIds);
                string second = provider.AdditionalApiKeyIds[0], third = provider.AdditionalApiKeyIds[1];
                Assert.HasCount(3, view.GetControl<StackPanel>("ProviderApiKeyRows").Children);
                foreach ((string id, string key) in new[] { (provider.Id, "sk-primary-1234"), (second, "sk-second-5678"), (third, "sk-third-9012") })
                {
                    Button reveal = KeyControl<Button>(view, id, "ProviderKeyRevealButton");
                    Assert.DoesNotContain(key, ((TextBlock)reveal.Content!).Text ?? "");
                    Assert.DoesNotContain(key, Avalonia.Automation.AutomationProperties.GetName(reveal));
                    Assert.DoesNotContain(key, ToolTip.GetTip(reveal)?.ToString() ?? "");
                }
                TextBox draft = EditProviderKey(view, second);
                Assert.AreEqual("", draft.Text, "铅笔绝不复制原机密");
                draft.Text = "sk-discarded";
                Click(KeyControl<Button>(view, second, "ProviderKeyEditCancelButton"));
                Assert.AreEqual("sk-second-5678", await store.GetApiKeyAsync(second));
                EditProviderKey(view, second).Text = "sk-edited-second";
                Assert.AreEqual("sk-second-5678", await store.GetApiKeyAsync(second), "铅笔暂存不落库");
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => !KeyControl<TextBox>(view, second, "ProviderKeyEditBox").IsEffectivelyVisible);
                Assert.AreEqual("sk-edited-second", await store.GetApiKeyAsync(second));
                EditProviderKey(view, provider.Id).Text = "";
                Click(view.GetControl<Button>("SaveButton"));
                await PumpAsync();
                Assert.AreEqual("sk-primary-1234", await store.GetApiKeyAsync(provider.Id), "空编辑是保持原值，不是删除");
                EditProviderKey(view, provider.Id).Text = "sk-edited-primary";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => !KeyControl<TextBox>(view, provider.Id, "ProviderKeyEditBox").IsEffectivelyVisible);
                Assert.AreEqual("sk-edited-primary", await store.GetApiKeyAsync(provider.Id));
                provider.ActiveApiKeyId = second;
                await store.SaveAsync(settings);
                Click(KeyControl<Button>(view, second, "ProviderKeyRemoveButton"));
                Assert.AreEqual("sk-edited-second", await store.GetApiKeyAsync(second), "第一击不能删机密");
                Click(KeyControl<Button>(view, second, "ProviderKeyRemoveButton"));
                await WaitUntilAsync(() => !HasKeyRow(view, second));
                Assert.IsNull(provider.ActiveApiKeyId);
                Assert.IsNull(await store.GetApiKeyAsync(second));
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyRemoveButton"));
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyRemoveButton"));
                await WaitUntilAsync(() => !HasKeyRow(view, provider.Id));
                Assert.AreEqual(provider.Id, settings.Providers.Single().Id);
                Assert.AreEqual(model.Id, provider.Models.Single().Id);
                Assert.AreEqual("sk-third-9012", (await store.ResolveCredentialAsync(settings.FindModel(model.Id)!)).Value);
                Click(KeyControl<Button>(view, third, "ProviderKeyRemoveButton"));
                Click(KeyControl<Button>(view, third, "ProviderKeyRemoveButton"));
                await WaitUntilAsync(() => !HasKeyRow(view, third));
                view.GetControl<TextBox>("ProviderNewApiKeyBox").Text = "tiny";
                Click(view.GetControl<Button>("ProviderAddApiKeyButton"));
                await WaitUntilAsync(() => HasKeyRow(view, provider.Id));
                Assert.IsEmpty(provider.AdditionalApiKeyIds, "全空后添加回旧主槽");
                Assert.DoesNotContain("tiny", ((TextBlock)KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").Content!).Text ?? "");
                Assert.IsFalse((await store.LoadAsync()).Providers.Single().BalanceApiKeys);
                Assert.AreEqual(9, notifications, "两次添加、三次保存、三次移除、一次重新添加，每次只通知一次");
                AiSettings reopened = await new AiSettingsStore(context).LoadAsync();
                Assert.HasCount(1, reopened.Providers);
                Assert.AreEqual("tiny", await new AiSettingsStore(context).GetApiKeyAsync(provider.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void ProviderApiKey_RevealOnlyOneRow_LanguageAndNarrowKeyboardAccessibility(string language)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m1" }] };
            var other = new AiProvider { Name = "other" };
            var settings = new AiSettings { Providers = [provider, other] };
            await store.AddProviderApiKeyAsync(settings, provider, "sk-visible-primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "sk-hidden-additional");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 420, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                Button reveal = KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton");
                Assert.IsTrue(reveal.Focus());
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await PumpAsync();
                Assert.Contains("sk-visible-primary", ((TextBlock)reveal.Content!).Text ?? "");
                Assert.DoesNotContain("sk-hidden-additional", ((TextBlock)KeyControl<Button>(view, extra, "ProviderKeyRevealButton").Content!).Text ?? "");
                Button pencil = KeyControl<Button>(view, provider.Id, "ProviderKeyEditButton");
                Assert.IsTrue(pencil.Focus());
                loc.Switch(language); view.ApplyLoc();
                Assert.IsTrue(pencil.IsFocused, "换语言不重建行，不抢按钮焦点");
                Assert.Contains(loc["SetupKeyEdit"], Avalonia.Automation.AutomationProperties.GetName(pencil));
                foreach (string action in new[] { "ProviderKeyProbeButton", "ProviderKeyEditButton", "ProviderKeyRemoveButton" })
                {
                    Button button = KeyControl<Button>(view, extra, action);
                    Assert.IsTrue(button.Focusable);
                    Assert.IsTrue(button.Focus());
                    Assert.IsGreaterThan(0d, button.Bounds.Width);
                    Avalonia.Point? origin = button.TranslatePoint(new Avalonia.Point(), KeyRow(view, extra));
                    Assert.IsNotNull(origin);
                    Assert.IsTrue(origin.Value.X >= 0 && origin.Value.X + button.Bounds.Width <= KeyRow(view, extra).Bounds.Width + 0.1,
                        "420 DIP 下操作按钮不得裁切或挤入遮罩摘要");
                    Assert.AreEqual(ToolTip.GetTip(button)?.ToString(), Avalonia.Automation.AutomationProperties.GetName(button));
                }
                Click(pencil);
                Assert.IsTrue(string.IsNullOrEmpty(KeyControl<TextBox>(view, provider.Id, "ProviderKeyEditBox").Text), "编辑框不能预填旧机密");
                Assert.DoesNotContain("sk-visible-primary", ((TextBlock)reveal.Content!).Text ?? "", "编辑时重新遮罩");
                KeyControl<TextBox>(view, provider.Id, "ProviderKeyEditBox").Text = "discard-on-navigation";
                view.ReloadFromCatalog(other.Id); await PumpAsync();
                view.ReloadFromCatalog(provider.Id); await PumpAsync();
                Assert.DoesNotContain("sk-visible-primary", ((TextBlock)KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").Content!).Text ?? "");
                Assert.IsTrue(string.IsNullOrEmpty(KeyControl<TextBox>(view, provider.Id, "ProviderKeyEditBox").Text), "离开供应商后草稿必须丢弃");
                Assert.AreEqual("sk-visible-primary", await store.GetApiKeyAsync(provider.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("deleted")]
    [DataRow("own")]
    [DataRow("draft")]
    public void ProviderApiKey_RememberedModelNeverSubstitutesFirstModelOrTestsUnsavedDraft(string change)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(_ => 200); endpoint.Start();
            var first = new AiModelConfig { Model = "first" };
            var chosen = new AiModelConfig { Model = "chosen" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [first, chosen] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show();
            try
            {
                await WaitUntilAsync(() => HasKeyRow(view, b));
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton"));
                SelectKeyModel(view, provider.Id, chosen.Id);
                await WaitUntilAsync(() => endpoint.Requests.Count == 1 && !IsKeyProbeRunning(KeyProbeState(view, provider.Id)));
                switch (change)
                {
                    case "deleted": provider.Models.Remove(chosen); view.RefreshCatalogModels(); break;
                    case "own": chosen.HasOwnApiKey = true; view.RefreshCatalogModels(); break;
                    case "draft": EditProviderKey(view, b).Text = "unsaved-key"; break;
                }
                Click(KeyControl<Button>(view, b, "ProviderKeyProbeButton"));
                ComboBox picker = KeyControl<ComboBox>(view, b, "ProviderKeyModelPicker");
                if (change == "draft")
                {
                    Assert.AreEqual(loc["SetupSaveProviderFirst"], view.GetControl<TextBlock>("StatusText").Text);
                    Assert.IsFalse(picker.IsVisible);
                }
                else
                {
                    Assert.IsTrue(picker.IsVisible);
                    Assert.AreEqual(-1, picker.SelectedIndex);
                }
                loc.Switch("zh-Hans"); view.ApplyLoc(); await PumpAsync();
                Assert.HasCount(1, endpoint.Requests, "失效模型不能改测首模型，记忆模型也不能绕过未保存草稿校验");
                Assert.AreEqual("Bearer key-a", endpoint.Requests.Single().Auth);
                Assert.Contains("\"model\":\"chosen\"", endpoint.Requests.Single().Body);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_AllKeysProbeInvalidEmptyEditShowsReasonAndResumesAfterCancel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(_ => 200); endpoint.Start();
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m1" }] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show();
            try
            {
                await WaitUntilAsync(() => HasKeyRow(view, b));
                Button button = view.GetControl<Button>("ProviderProbeAllKeysButton");
                Click(button);
                ComboBox picker = view.GetControl<ComboBox>("ProviderAllKeysModelPicker");
                TextBox edit = EditProviderKey(view, b);
                await store.RemoveProviderApiKeyAsync(settings, provider, b);
                view.RefreshCatalogModels();
                await WaitUntilAsync(() => KeyControl<TextBlock>(view, b, "ProviderKeyStatus").Text == loc["StatusNeedsKey"]);
                Assert.IsFalse(button.IsEnabled);
                Assert.IsFalse(picker.IsEnabled);
                Assert.IsTrue(edit.IsEffectivelyVisible, "外部删除不能丢弃仍在编辑的草稿行");
                Assert.IsTrue(string.IsNullOrEmpty(edit.Text));
                picker.SelectedIndex = 0; // 已展开的选择器晚到选择仍须被入口复验挡住。
                foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
                {
                    loc.Switch(language); view.ApplyLoc();
                    Assert.IsTrue(view.GetControl<TextBlock>("ProviderAllKeysHintText").IsEffectivelyVisible);
                    Assert.AreEqual(loc["SetupKeysInvalidDraft"], view.GetControl<TextBlock>("ProviderAllKeysHintText").Text);
                    Assert.AreEqual(loc["SetupKeysInvalidDraft"], Avalonia.Automation.AutomationProperties.GetName(button));
                }
                Assert.IsEmpty(endpoint.Requests);
                Click(KeyControl<Button>(view, b, "ProviderKeyEditCancelButton"));
                view.RefreshCatalogModels();
                await WaitUntilAsync(() => !HasKeyRow(view, b) && button.IsEnabled);
                Assert.IsFalse(view.GetControl<TextBlock>("ProviderAllKeysHintText").IsVisible);
                Click(button); picker.SelectedIndex = 0;
                await WaitUntilAsync(() => endpoint.Requests.Count == 1 && !IsKeyProbeRunning(KeyProbeState(view, provider.Id)));
                Assert.AreEqual("Bearer key-a", endpoint.Requests.Single().Auth);
                Assert.Contains("\"model\":\"m1\"", endpoint.Requests.Single().Body);
                Assert.Contains(loc.F("SetupKeyPassed", "m1", "").TrimEnd(), KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text!);
                Assert.IsNull(await store.GetApiKeyAsync(b));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_AllKeysProbeSelectsOnceAndContinuesAfterOneKeyFails()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(auth => auth == "Bearer key-b" ? 401 : 200);
            endpoint.Start();
            var model = new AiModelConfig { Model = "batch-model" };
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var cross = new AiModelConfig { Model = "cross", BaseUrlOverride = "https://other.example/v1" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model, own, cross] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            string c = await store.AddProviderApiKeyAsync(settings, provider, "key-c");
            var health = new ProviderHealth(); var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 420, Height = 800 };
            window.Show(); view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
            try
            {
                await WaitUntilAsync(() => HasKeyRow(view, c));
                Button button = view.GetControl<Button>("ProviderProbeAllKeysButton");
                Click(button);
                ComboBox picker = view.GetControl<ComboBox>("ProviderAllKeysModelPicker");
                Assert.IsTrue(picker.IsVisible);
                Assert.AreEqual(-1, picker.SelectedIndex);
                Assert.IsEmpty(endpoint.Requests);
                foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
                {
                    loc.Switch(language); view.ApplyLoc();
                    Assert.AreEqual(loc["SetupKeysProbeAll"], button.Content);
                    Assert.AreEqual(-1, picker.SelectedIndex);
                }
                Assert.IsEmpty(endpoint.Requests, "选模型前及切换语言均不得发请求");
                object choice = picker.ItemsSource!.Cast<object>().Single();
                Assert.AreEqual(model.Id, choice.GetType().GetProperty("Id")!.GetValue(choice));
                picker.SelectedItem = choice;
                await WaitUntilAsync(() => button.Content?.ToString() == loc["SetupKeysProbeAll"]);
                CollectionAssert.AreEqual(new[] { "Bearer key-a", "Bearer key-b", "Bearer key-c" }, endpoint.Requests.Select(r => r.Auth).ToArray());
                Assert.IsTrue(endpoint.Requests.All(r => r.Body.Contains("\"model\":\"batch-model\"", StringComparison.Ordinal)
                    && r.Body.Contains("Reply with exactly:", StringComparison.Ordinal)));
                Assert.Contains(loc.F("SetupKeyPassed", "batch-model", "").TrimEnd(), KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text!);
                Assert.Contains(loc.F("SetupKeyFailed", "batch-model", "").TrimEnd(), KeyControl<TextBlock>(view, b, "ProviderKeyStatus").Text!);
                Assert.Contains(loc.F("SetupKeyPassed", "batch-model", "").TrimEnd(), KeyControl<TextBlock>(view, c, "ProviderKeyStatus").Text!);
                Assert.IsTrue(health.IsKeyCooling(b));
                Assert.IsFalse(health.IsKeyCooling(provider.Id)); Assert.IsFalse(health.IsKeyCooling(c));
                Assert.IsFalse(health.IsCooling(model.Id));
                Assert.IsNull(provider.ActiveApiKeyId); Assert.IsFalse(provider.BalanceApiKeys);
                Assert.AreEqual(model.Id, settings.ActiveModelId);
                model.HasOwnApiKey = true; view.RefreshCatalogModels();
                await WaitUntilAsync(() => !button.IsEnabled);
                Assert.AreEqual(loc["SetupKeysNoModel"], Avalonia.Automation.AutomationProperties.GetName(button));
                Assert.HasCount(3, endpoint.Requests, "没有共同模型时不得借用模型独立 Key 或跨域发送补充 Key");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("navigate")]
    [DataRow("edit")]
    [DataRow("model")]
    [DataRow("health")]
    public void ProviderApiKey_AllKeysProbeCancellationStopsQueuedKeysAndDiscardsLateFailure(string change)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(_ => 401, holdFirst: true); endpoint.Start();
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var other = new AiProvider { Name = "other" };
            var settings = new AiSettings { Providers = [provider, other] };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var health = new ProviderHealth(); var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show();
            try
            {
                await WaitUntilAsync(() => HasKeyRow(view, b));
                Button button = view.GetControl<Button>("ProviderProbeAllKeysButton");
                Click(button);
                ComboBox picker = view.GetControl<ComboBox>("ProviderAllKeysModelPicker");
                picker.SelectedIndex = 0;
                await WaitUntilAsync(() => endpoint.Requests.Count == 1);
                Assert.AreEqual(loc["Cancel"], button.Content);
                switch (change)
                {
                    case "cancel": Click(button); break;
                    case "navigate": view.ReloadFromCatalog(other.Id); break;
                    case "edit": EditProviderKey(view, b).Text = "draft-key"; break;
                    case "model": model.Model = "new-model"; view.RefreshCatalogModels(); break;
                    case "health": health.Clear(); break;
                }
                endpoint.Release();
                await WaitUntilAsync(() => button.Content?.ToString() == loc["SetupKeysProbeAll"]);
                Assert.HasCount(1, endpoint.Requests, "取消或配置失效后不继续检测排队 Key");
                Assert.IsFalse(health.IsKeyCooling(provider.Id)); Assert.IsFalse(health.IsKeyCooling(b));
                Assert.IsFalse(health.IsCooling(model.Id));
                if (HasKeyRow(view, provider.Id))
                    Assert.DoesNotContain("Last check failed", KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text!);
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("key")]
    [DataRow("url")]
    [DataRow("protocol")]
    public void ProviderApiKey_AllKeysProbeRejectsUnsavedDraftEvenAfterPickerWasOpened(string draft)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(_ => 200); endpoint.Start();
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m1" }] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, new ProviderHealth());
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show();
            try
            {
                await WaitUntilAsync(() => HasKeyRow(view, b));
                Button button = view.GetControl<Button>("ProviderProbeAllKeysButton");
                Click(button);
                ComboBox picker = view.GetControl<ComboBox>("ProviderAllKeysModelPicker");
                switch (draft)
                {
                    case "key": EditProviderKey(view, b).Text = "draft-key"; break;
                    case "url": view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://draft.example/v1"; break;
                    case "protocol": view.GetControl<ComboBox>("ProviderProtocolCombo").SelectedIndex = (int)ChatProtocol.AnthropicMessages; break;
                }
                picker.SelectedIndex = 0;
                Assert.AreEqual(loc["SetupSaveProviderFirst"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(endpoint.Requests);
                Click(button);
                Assert.AreEqual(loc["SetupSaveProviderFirst"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(endpoint.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_ModelSelectionSendsFixedSlotAndOnlyAuthFailureCoolsThatKey()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(auth => auth == "Bearer key-b" ? 401 : 200);
            endpoint.Start();
            var m1 = new AiModelConfig { Model = "m1" };
            var m2 = new AiModelConfig { Model = "m2" };
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [m1, m2, own] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = m1.Id };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var health = new ProviderHealth();
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0; await PumpAsync();
            try
            {
                Click(KeyControl<Button>(view, b, "ProviderKeyProbeButton"));
                ComboBox picker = KeyControl<ComboBox>(view, b, "ProviderKeyModelPicker");
                Assert.IsTrue(picker.IsVisible);
                Assert.AreEqual(-1, picker.SelectedIndex);
                Assert.HasCount(2, picker.ItemsSource!.Cast<object>().ToList());
                Assert.IsEmpty(endpoint.Requests, "展开或换语言不会擅自选首模型产生付费请求");
                loc.Switch("zh-CN"); view.ApplyLoc();
                Assert.IsEmpty(endpoint.Requests);
                SelectKeyModel(view, b, m2.Id);
                await WaitUntilAsync(() => (KeyControl<TextBlock>(view, b, "ProviderKeyStatus").Text ?? "").Contains("m2", StringComparison.Ordinal)
                    && health.IsKeyCooling(b));
                Assert.HasCount(1, endpoint.Requests);
                Assert.AreEqual("Bearer key-b", endpoint.Requests[0].Auth);
                Assert.Contains("\"model\":\"m2\"", endpoint.Requests[0].Body);
                Assert.Contains("Reply with exactly:", endpoint.Requests[0].Body);
                Assert.Contains("上次检测失败", KeyControl<TextBlock>(view, b, "ProviderKeyStatus").Text ?? "");
                foreach (string language in new[] { "en", "zh-CN", "zh-TW", "ja", "ko" })
                {
                    loc.Switch(language); view.ApplyLoc();
                    string status = KeyControl<TextBlock>(view, b, "ProviderKeyStatus").Text ?? "";
                    string clock = System.Text.RegularExpressions.Regex.Match(status, @"\d{2}:\d{2}$").Value;
                    Assert.AreEqual(loc.F("SetupKeyFailed", "m2", clock), status);
                    Assert.AreEqual(1, endpoint.Requests.Count, "换语言只重新格式化证据，不重测");
                }
                loc.Switch("zh-CN"); view.ApplyLoc();
                Assert.IsFalse(health.IsCooling(m2.Id));
                Assert.IsFalse(health.IsKeyCooling(provider.Id));
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton"));
                Assert.AreEqual(m2.Id, KeyControl<ComboBox>(view, provider.Id, "ProviderKeyModelPicker").SelectedItem!.GetType()
                    .GetProperty("Id")!.GetValue(KeyControl<ComboBox>(view, provider.Id, "ProviderKeyModelPicker").SelectedItem));
                await WaitUntilAsync(() => (KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text ?? "").Contains("上次检测通过", StringComparison.Ordinal));
                Assert.HasCount(2, endpoint.Requests);
                Assert.AreEqual("Bearer key-a", endpoint.Requests[1].Auth);
                Assert.Contains("\"model\":\"m2\"", endpoint.Requests[1].Body);
                Assert.IsNull(provider.ActiveApiKeyId);
                Assert.AreEqual(m1.Id, settings.ActiveModelId);
                Assert.IsFalse(provider.BalanceApiKeys);
                Assert.IsTrue(health.IsKeyCooling(b));
                m2.Model = "new-m2"; view.RefreshCatalogModels(); await PumpAsync();
                Assert.AreEqual(loc["SetupKeyUntested"], KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text);
                m2.HasOwnApiKey = true; m1.HasOwnApiKey = true; view.RefreshCatalogModels(); await PumpAsync();
                Assert.IsFalse(KeyControl<Button>(view, b, "ProviderKeyProbeButton").IsEnabled);
                Assert.Contains(loc["SetupKeyNoModel"], Avalonia.Automation.AutomationProperties.GetName(KeyControl<Button>(view, b, "ProviderKeyProbeButton")));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(404)]
    [DataRow(500)]
    public void ProviderApiKey_NonAuthenticationProbeFailureDoesNotCoolKeysOrModel(int status)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(_ => status); endpoint.Start();
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context); await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var health = new ProviderHealth(); var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                Click(KeyControl<Button>(view, b, "ProviderKeyProbeButton")); SelectKeyModel(view, b, model.Id);
                await WaitUntilAsync(() => (KeyControl<TextBlock>(view, b, "ProviderKeyStatus").Text ?? "").StartsWith(loc.F("SetupKeyFailed", "m1", "").Split("m1")[0], StringComparison.Ordinal));
                Assert.IsFalse(health.IsCooling(model.Id)); Assert.IsFalse(health.IsKeyCooling(b));
                Assert.IsFalse(health.IsKeyCooling(provider.Id));
                Assert.IsTrue(endpoint.Requests.All(request => request.Auth == "Bearer key-b"));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("replace")]
    [DataRow("remove")]
    [DataRow("model")]
    [DataRow("url")]
    [DataRow("protocol")]
    [DataRow("navigate")]
    [DataRow("close")]
    [DataRow("cancel")]
    [DataRow("health")]
    public void ProviderApiKey_LateProbeDoesNotWriteChangedSlotOrModel(string change)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(_ => 401, holdFirst: true); endpoint.Start();
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var other = new AiProvider { Name = "other" };
            var settings = new AiSettings { Providers = [provider, other] };
            var store = new AiSettingsStore(context); await store.AddProviderApiKeyAsync(settings, provider, "key-a");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
            var health = new ProviderHealth(); var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                Click(KeyControl<Button>(view, b, "ProviderKeyProbeButton")); SelectKeyModel(view, b, model.Id);
                await WaitUntilAsync(() => endpoint.Requests.Count == 1);
                object inFlight = KeyProbeState(view, b);
                switch (change)
                {
                    case "replace": await store.ReplaceProviderApiKeyAsync(settings, provider, b, "new-key-b"); view.ReloadFromCatalog(provider.Id); break;
                    case "remove": await store.RemoveProviderApiKeyAsync(settings, provider, b); view.ReloadFromCatalog(provider.Id); break;
                    case "model": provider.Models.Remove(model); view.RefreshCatalogModels(); break;
                    case "url": provider.BaseUrl = "https://changed.example/v1"; view.RefreshCatalogModels(); break;
                    case "protocol": provider.DefaultProtocol = ChatProtocol.AnthropicMessages; view.RefreshCatalogModels(); break;
                    case "navigate": view.ReloadFromCatalog(other.Id); break;
                    case "close": window.Close(); break;
                    case "cancel": Click(KeyControl<Button>(view, b, "ProviderKeyProbeButton")); break;
                    case "health": health.Clear(); break;
                }
                endpoint.Release();
                await WaitUntilAsync(() => !IsKeyProbeRunning(inFlight));
                await PumpAsync();
                Assert.IsFalse(health.IsKeyCooling(b), "旧 401 不得冷却新槽或取消请求");
                Assert.IsFalse(health.IsCooling(model.Id));
                if (HasKeyRow(view, b)) Assert.DoesNotContain("Last check failed", KeyControl<TextBlock>(view, b, "ProviderKeyStatus").Text ?? "");
                Assert.IsNull(provider.ActiveApiKeyId);
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_UnsavedDraftBlocksRowProbe_ExplicitPageTestAndSaveUseNewEndpointAndKey()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var oldEndpoint = new KeyProbeServer(_ => 200); oldEndpoint.Start();
            using var newEndpoint = new KeyProbeServer(_ => 200); newEndpoint.Start();
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { BaseUrl = oldEndpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context); await store.AddProviderApiKeyAsync(settings, provider, "primary-old");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "additional-old");
            provider.ActiveApiKeyId = b; await store.SaveAsync(settings);
            var health = new ProviderHealth(); health.RecordKey(b, false);
            var loc = new Loc("en"); int notifications = 0;
            var view = new SettingsView(context, store, settings, loc, () => notifications++, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                EditProviderKey(view, b).Text = "additional-new";
                Click(KeyControl<Button>(view, b, "ProviderKeyProbeButton"));
                Assert.AreEqual(loc["SetupSaveProviderFirst"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsFalse(KeyControl<ComboBox>(view, b, "ProviderKeyModelPicker").IsVisible);
                Assert.IsEmpty(oldEndpoint.Requests); Assert.IsEmpty(newEndpoint.Requests);
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newEndpoint.BaseUrl;
                Click(view.GetControl<Button>("ProviderAddApiKeyButton"));
                Assert.AreEqual(loc["SetupSaveProviderFirst"], view.GetControl<TextBlock>("StatusText").Text);
                Click(view.GetControl<Button>("PullModelsButton"));
                Assert.AreEqual(loc["SetupSaveProviderFirst"], view.GetControl<TextBlock>("StatusText").Text);
                Task probe = StartTestAsync(view); await probe.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(1, newEndpoint.Requests); Assert.IsEmpty(oldEndpoint.Requests);
                Assert.AreEqual("Bearer additional-new", newEndpoint.Requests[0].Auth);
                Assert.IsTrue(health.IsKeyCooling(b), "未落库的显式测试不得改共享健康");
                Assert.AreEqual("additional-old", await store.GetApiKeyAsync(b));
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                Assert.AreEqual(1, notifications); Assert.AreEqual(newEndpoint.BaseUrl, provider.BaseUrl);
                Assert.AreEqual("additional-new", await new AiSettingsStore(context).GetApiKeyAsync(b));
                Click(KeyControl<Button>(view, b, "ProviderKeyProbeButton")); SelectKeyModel(view, b, model.Id);
                await WaitUntilAsync(() => newEndpoint.Requests.Count == 2 && !health.IsKeyCooling(b));
                Assert.AreEqual("Bearer additional-new", newEndpoint.Requests[1].Auth);
                Assert.IsEmpty(oldEndpoint.Requests, "新 Key 不得发回旧 host，旧 Key 不得发向新 host");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = oldEndpoint.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer primary-old", oldEndpoint.Requests.Single().Auth,
                    "没有新 Key 的跨 origin 草稿只能显式使用主槽，不能泄露 active 补充槽");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_FirstModelOwnKeyIsNotOverriddenByProviderDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext(); using var endpoint = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "own", HasOwnApiKey = true, BaseUrlOverride = endpoint.BaseUrl };
            var provider = new AiProvider { BaseUrl = "https://provider.example", Models = [model] };
            var settings = new AiSettings { Providers = [provider] }; var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "provider-saved"); await store.SetApiKeyAsync(model.Id, "own-saved");
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                EditProviderKey(view, provider.Id).Text = "provider-draft";
                Assert.IsFalse(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton").IsEnabled);
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer own-saved", endpoint.Authorizations.Single());
                Assert.AreEqual("provider-saved", await store.GetApiKeyAsync(provider.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProviderApiKey_PersistenceFailureRetainsDraftAndRollsBackFieldsBalanceAndRemoval(bool failSecret)
    {
        OnUi(async () =>
        {
            var storage = new KeySettingsStorage(new InMemoryStorage());
            using var secretSource = new TestPluginContext();
            var secrets = new KeySettingsSecrets(secretSource.Secrets);
            using var context = new TestPluginContext { Storage = storage, Secrets = secrets };
            var provider = new AiProvider { Name = "original", BaseUrl = "https://old.example", Models = [new AiModelConfig { Model = "m1" }] };
            var settings = new AiSettings { Providers = [provider] }; var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "additional");
            provider.ActiveApiKeyId = b; await store.SaveAsync(settings);
            var health = new ProviderHealth(); health.RecordKey(b, false); int notifications = 0;
            var view = new SettingsView(context, store, settings, new Loc("en"), () => notifications++, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                storage.Fail = !failSecret;
                secrets.FailNextMutation = failSecret;
                EditProviderKey(view, b).Text = "changed";
                view.GetControl<TextBox>("ProviderNameBox").Text = "new name";
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://new.example";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => (view.GetControl<TextBlock>("StatusText").Text ?? "").Contains(failSecret ? "Secret failed" : "Storage failed", StringComparison.Ordinal));
                Assert.AreEqual("original", provider.Name); Assert.AreEqual("https://old.example", provider.BaseUrl);
                Assert.AreEqual("additional", await new AiSettingsStore(context).GetApiKeyAsync(b));
                Assert.AreEqual("changed", KeyControl<TextBox>(view, b, "ProviderKeyEditBox").Text);
                Assert.AreEqual(0, notifications);
                storage.Fail = true;
                view.GetControl<CheckBox>("BalanceApiKeysCheck").IsChecked = true;
                await WaitUntilAsync(() => view.GetControl<CheckBox>("BalanceApiKeysCheck").IsChecked == false);
                Assert.IsFalse(provider.BalanceApiKeys); Assert.IsTrue(health.IsKeyCooling(b));
                Click(KeyControl<Button>(view, b, "ProviderKeyEditCancelButton"));
                view.GetControl<TextBox>("ProviderNameBox").Text = provider.Name;
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = provider.BaseUrl;
                storage.Fail = !failSecret;
                secrets.FailNextMutation = failSecret;
                Click(KeyControl<Button>(view, b, "ProviderKeyRemoveButton")); Click(KeyControl<Button>(view, b, "ProviderKeyRemoveButton"));
                await PumpAsync();
                Assert.IsTrue(HasKeyRow(view, b)); Assert.AreEqual(b, provider.ActiveApiKeyId);
                Assert.AreEqual("additional", await store.GetApiKeyAsync(b));
                Assert.AreEqual(0, notifications);
                storage.Fail = false;
                view.GetControl<CheckBox>("BalanceApiKeysCheck").IsChecked = true; await PumpAsync();
                Assert.IsTrue((await store.LoadAsync()).Providers.Single().BalanceApiKeys);
                Assert.AreEqual(0, notifications, "均摊开关不走清健康回调");
                Assert.IsTrue(health.IsKeyCooling(b));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_SameValueKeyStillSavesNameAndEndpointTogether()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var provider = new AiProvider { Name = "before", BaseUrl = "https://before.example", Models = [new AiModelConfig { Model = "m1" }] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context); await store.AddProviderApiKeyAsync(settings, provider, "same-key");
            int notifications = 0;
            var view = new SettingsView(context, store, settings, new Loc("en"), () => notifications++);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                EditProviderKey(view, provider.Id).Text = "same-key";
                view.GetControl<TextBox>("ProviderNameBox").Text = "after";
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://after.example/v1";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                AiProvider saved = (await new AiSettingsStore(context).LoadAsync()).Providers.Single();
                Assert.AreEqual("after", saved.Name); Assert.AreEqual("https://after.example/v1", saved.BaseUrl);
                Assert.AreEqual("same-key", await new AiSettingsStore(context).GetApiKeyAsync(provider.Id));
                Assert.AreEqual(1, notifications);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_LateSaveOfOneSlotCannotEraseAnotherSlotDraft()
    {
        OnUi(async () =>
        {
            using var source = new TestPluginContext();
            var provider = new AiProvider { BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m1" }] };
            var settings = new AiSettings { Providers = [provider] };
            var sourceStore = new AiSettingsStore(source);
            await sourceStore.AddProviderApiKeyAsync(settings, provider, "old-primary");
            string b = await sourceStore.AddProviderApiKeyAsync(settings, provider, "old-additional");
            var delayed = new DelayedSecrets(source.Secrets, $"apikey:{provider.Id}", delayMutation: true);
            using var context = new TestPluginContext { Secrets = delayed };
            var store = new AiSettingsStore(context); await store.SaveAsync(settings);
            int notifications = 0;
            var view = new SettingsView(context, store, settings, new Loc("en"), () => notifications++);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                EditProviderKey(view, provider.Id).Text = "new-primary";
                Click(view.GetControl<Button>("SaveButton"));
                await delayed.Started.WaitAsync(TimeSpan.FromSeconds(15));
                TextBox next = EditProviderKey(view, b); next.Text = "next-additional-draft";
                delayed.Release();
                await WaitUntilAsync(() => notifications == 1);
                await PumpAsync();
                Assert.AreEqual("new-primary", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("old-additional", await store.GetApiKeyAsync(b));
                Assert.AreEqual("next-additional-draft", next.Text);
                Assert.IsTrue(next.IsEffectivelyVisible);
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => notifications == 2);
                Assert.AreEqual("next-additional-draft", await store.GetApiKeyAsync(b));
            }
            finally { delayed.Release(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void ProviderApiKey_BlankAndDuplicateKeyUseLocalizedValidationWithoutLosingDraft(string language)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext(); var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = "https://same.example" };
            var settings = new AiSettings { Providers = [provider] };
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "additional");
            var loc = new Loc(language); int notifications = 0;
            var view = new SettingsView(context, store, settings, loc, () => notifications++);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                foreach (string bad in new[] { "   ", "primary", "additional" })
                {
                    view.GetControl<TextBox>("ProviderNewApiKeyBox").Text = bad;
                    Click(view.GetControl<Button>("ProviderAddApiKeyButton")); await PumpAsync();
                    Assert.AreEqual(loc["SetupUniqueApiKey"], view.GetControl<TextBlock>("StatusText").Text);
                    Assert.AreEqual(bad, view.GetControl<TextBox>("ProviderNewApiKeyBox").Text);
                    Assert.HasCount(1, provider.AdditionalApiKeyIds);
                }
                TextBox edit = EditProviderKey(view, b); edit.Text = "primary";
                Click(view.GetControl<Button>("SaveButton")); await PumpAsync();
                Assert.AreEqual(loc["SetupUniqueApiKey"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.AreEqual("primary", edit.Text); Assert.AreEqual("additional", await store.GetApiKeyAsync(b));
                Assert.AreEqual(0, notifications);
                await store.RemoveProviderApiKeyAsync(settings, provider, b);
                view.RefreshCatalogModels(); await PumpAsync();
                Assert.AreEqual("primary", edit.Text, "外部删槽后仍保留当前待保存文本");
                Click(view.GetControl<Button>("SaveButton")); await PumpAsync();
                Assert.Contains(loc["SetupConfigChanged"], view.GetControl<TextBlock>("StatusText").Text ?? "");
                Assert.AreEqual(0, notifications);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_ModelPickerFiltersOriginsAndOwnKeysAndUsesIdsForDuplicateNames()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var same = new SseStub(ProbeAnswerSse); using var other = new SseStub(ProbeAnswerSse);
            var a = new AiModelConfig { Model = "m1", Name = "duplicate" };
            var b = new AiModelConfig { Model = "m2", Name = "duplicate" };
            var cross = new AiModelConfig { Model = "m3", BaseUrlOverride = other.BaseUrl };
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { BaseUrl = same.BaseUrl, Models = [a, b, cross, own, new AiModelConfig()] };
            var settings = new AiSettings { Providers = [provider] }; var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "additional");
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton"));
                SelectKeyModel(view, provider.Id, cross.Id);
                await WaitUntilAsync(() => other.Requests.Count == 1 && !IsKeyProbeRunning(KeyProbeState(view, provider.Id)));
                Assert.AreEqual("Bearer primary", other.Authorizations.Single(), "主槽保留已保存模型覆盖地址的旧行为");
                Click(KeyControl<Button>(view, extra, "ProviderKeyProbeButton"));
                var choices = KeyControl<ComboBox>(view, extra, "ProviderKeyModelPicker").ItemsSource!.Cast<object>().ToList();
                Assert.HasCount(2, choices);
                Assert.AreNotEqual(choices[0].ToString(), choices[1].ToString(), "同名模型带短 ID 但绑定配置 ID");
                Assert.IsEmpty(same.Requests); Assert.HasCount(1, other.Requests);
                Assert.AreEqual(-1, KeyControl<ComboBox>(view, extra, "ProviderKeyModelPicker").SelectedIndex,
                    "上次主槽检测的跨域模型不适用于补充 Key，必须重新选择而不是自动改测首模型");
                SelectKeyModel(view, extra, b.Id);
                await WaitUntilAsync(() => same.Requests.Count == 1 && !IsKeyProbeRunning(KeyProbeState(view, extra)));
                Assert.Contains("\"model\":\"m2\"", same.Requests.Single());
                Assert.AreEqual("Bearer additional", same.Authorizations.Single());
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton"));
                Assert.HasCount(3, KeyControl<ComboBox>(view, provider.Id, "ProviderKeyModelPicker").ItemsSource!.Cast<object>().ToList());
                await WaitUntilAsync(() => same.Requests.Count == 2 && !IsKeyProbeRunning(KeyProbeState(view, provider.Id)));
                Assert.Contains("\"model\":\"m2\"", same.Requests[1]);
                Assert.AreEqual("Bearer primary", same.Authorizations[1], "同名模型必须按 ID 复用上次选择");
                Assert.HasCount(1, other.Requests);
                Assert.IsNull(provider.ActiveApiKeyId);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProviderApiKey_ColdTimerReadStaysSingleFlightWithoutBlockingNavigation(bool navigateAway)
    {
        OnUi(async () =>
        {
            using var source = new TestPluginContext();
            string extra = Guid.NewGuid().ToString("N");
            var delayed = new DelayedSecrets(source.Secrets, $"apikey:{extra}");
            using var context = new TestPluginContext { Secrets = delayed };
            var store = new AiSettingsStore(context);
            var first = new AiProvider { Name = "A", BaseUrl = "https://a.example", Models = [new AiModelConfig { Model = "a" }] };
            var second = new AiProvider { Name = "B", BaseUrl = "https://b.example" };
            var settings = new AiSettings { Providers = [first, second] };
            await source.Secrets.SetAsync($"apikey:{first.Id}", "primary-key");
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            try
            {
                await WaitUntilAsync(() => HasKeyRow(view, first.Id));
                TextBox edit = EditProviderKey(view, first.Id);
                edit.Text = "keep key draft";
                Assert.IsTrue(edit.Focus());
                await source.Secrets.SetAsync($"apikey:{extra}", "cold-extra-key");
                first.AdditionalApiKeyIds.Add(extra);
                await store.SaveAsync(settings);
                await delayed.Started.WaitAsync(TimeSpan.FromSeconds(15));
                if (navigateAway)
                {
                    ListBox list = view.GetControl<ListBox>("ProvidersList");
                    list.SelectedItem = list.ItemsSource!.Cast<ProviderNavItem>().Single(row => row.Provider == second);
                    await PumpAsync();
                    view.GetControl<TextBox>("ProviderNameBox").Text = "B current draft";
                    Assert.IsTrue(view.GetControl<TextBox>("ProviderNameBox").Focus());
                }
                await Task.Delay(2200); // 至少两个真实 timer tick 穿过尚未完成的首次冷读。
                await PumpAsync();
                Assert.IsFalse(HasKeyRow(view, extra), "前一轮未完成时不能发起读缓存的第二轮并提前发布");
                if (!navigateAway)
                {
                    Assert.AreEqual("keep key draft", edit.Text);
                    Assert.IsTrue(edit.IsFocused);
                }
                else
                {
                    Assert.AreEqual("B current draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                    Assert.IsTrue(view.GetControl<TextBox>("ProviderNameBox").IsFocused);
                }
                delayed.Release();
                if (!navigateAway)
                {
                    await WaitUntilAsync(() => HasKeyRow(view, extra));
                    Assert.AreEqual("keep key draft", edit.Text);
                    Assert.IsTrue(edit.IsFocused);
                }
                else
                {
                    await PumpAsync();
                    Assert.IsFalse(HasKeyRow(view, first.Id));
                    Assert.IsFalse(HasKeyRow(view, extra));
                    Assert.AreEqual("B current draft", view.GetControl<TextBox>("ProviderNameBox").Text);
                }
            }
            finally { delayed.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_CoolingTimerExpiresWithoutRebuildingAndStopsOnDetach()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext(); var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m1" }] };
            var settings = new AiSettings { Providers = [provider] }; await store.AddProviderApiKeyAsync(settings, provider, "primary");
            var health = new ProviderHealth(); var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                Button focused = KeyControl<Button>(view, provider.Id, "ProviderKeyEditButton"); focused.Focus();
                health.RecordKey(provider.Id, false);
                await WaitUntilAsync(() => KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text == loc["DotCooling"]);
                Assert.IsTrue(focused.IsFocused);
                var failed = (Dictionary<(bool IsKey, string Id), long>)typeof(ProviderHealth).GetField("_failedAt",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(health)!;
                failed[(true, provider.Id)] = Environment.TickCount64 - (long)ProviderHealth.Cooldown.TotalMilliseconds - 1;
                await WaitUntilAsync(() => KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text == loc["SetupKeyUntested"]);
                Assert.AreSame(focused, KeyControl<Button>(view, provider.Id, "ProviderKeyEditButton"));
                var timer = (DispatcherTimer)typeof(SettingsView).GetField("_keyLightTimer",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
                Assert.IsTrue(timer.IsEnabled); window.Close(); Assert.IsFalse(timer.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_DeletingProviderClearsAllOwnedSecretsAndKeepsOtherProviders()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext(); var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { Name = "remove", Models = [model] };
            var other = new AiProvider { Name = "keep" };
            var settings = new AiSettings { Providers = [provider, other] };
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "additional");
            await store.SetApiKeyAsync(model.Id, "own-secret"); await store.SetApiKeyAsync(other.Id, "keep-secret");
            await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "old-oauth-secret" });
            provider.AdditionalApiKeyIds.Add(other.Id); // 损坏 JSON 槽绝不能被当成自己的机密删掉。
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                Click(view.GetControl<Button>("DeleteButton"));
                Assert.HasCount(2, settings.Providers);
                Click(view.GetControl<Button>("DeleteButton")); await PumpAsync();
                Assert.AreSame(other, settings.Providers.Single());
                foreach (string id in new[] { provider.Id, extra, model.Id }) Assert.IsNull(await store.GetApiKeyAsync(id));
                Assert.IsNull(await store.GetTokensAsync(provider.Id));
                Assert.AreEqual("keep-secret", await store.GetApiKeyAsync(other.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_EmptySseReplyHasReadableLocalizedFailureWithoutCooling()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext(); using var endpoint = new SseStub("");
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider] }; var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "empty-reply-key");
            var health = new ProviderHealth(); var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton")); SelectKeyModel(view, provider.Id, model.Id);
                await WaitUntilAsync(() => endpoint.Requests.Count == 1 && !IsKeyProbeRunning(KeyProbeState(view, provider.Id)));
                foreach (string language in new[] { "en", "zh-CN", "zh-TW", "ja", "ko" })
                {
                    loc.Switch(language); view.ApplyLoc();
                    TextBlock status = KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus");
                    Assert.Contains(loc["ProbeEmptyReply"], ToolTip.GetTip(status)?.ToString() ?? "");
                    Assert.Contains("m1", Avalonia.Automation.AutomationProperties.GetName(status));
                    Assert.DoesNotContain("empty-reply-key", ToolTip.GetTip(status)?.ToString() ?? "");
                }
                Assert.IsFalse(health.IsCooling(model.Id)); Assert.IsFalse(health.IsKeyCooling(provider.Id));
                Assert.IsNull(provider.ActiveApiKeyId);
            }
            finally { window.Close(); }
        });
    }

}
