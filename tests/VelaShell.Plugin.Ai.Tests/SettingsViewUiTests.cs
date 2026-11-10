using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 设置页(供应商 › 模型两层)的 headless 装载:左栏行序、选中层切换右侧表单、
/// 新增供应商 / 模型与保存的落盘结果。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed partial class SettingsViewUiTests
{
    /// <summary>
    /// 自定义接入有一个 store 开关,默认关、勾上当场落库(#616–#636 复审①,维护者 2026-10-25 决定);
    /// 目录里的具名供应商(官方 openai)以目录为准,不摆这个开关。
    /// </summary>
    [TestMethod]
    public void CustomEndpoint_HasAStoreSwitch_OffByDefault_AndTheOfficialEntryDoesNot()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider custom = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            custom.BaseUrl = "https://relay.example/v1";
            custom.DefaultProtocol = ChatProtocol.OpenAiResponses;
            custom.Models[0].Model = "m";
            custom.ModelsExpanded = false;
            AiProvider official = ProviderCatalog.Find("openai")!.CreateProvider();
            official.ModelsExpanded = false;
            (Window window, SettingsView view, AiSettings settings, AiSettingsStore store) = await ShowAsync(context,
                new AiSettings { Providers = [custom, official], ActiveModelId = custom.Models[0].Id });
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                StackPanel panel = view.GetControl<StackPanel>("ProviderStorePanel");
                CheckBox check = view.GetControl<CheckBox>("ProviderStoreCheck");
                Assert.IsTrue(panel.IsVisible, "自定义接入要摆出这个开关");
                Assert.IsFalse(check.IsChecked, "默认关");
                long version = AiSettingsStore.ProviderConfigurationVersion(custom);

                check.IsChecked = true;
                await WaitUntilAsync(() => custom.CustomResponsesStore == true && check.IsEnabled);
                Assert.IsTrue((await new AiSettingsStore(context).LoadAsync()).Providers[0].CustomResponsesStore, "勾上当场落库");
                Assert.IsGreaterThan(version, AiSettingsStore.ProviderConfigurationVersion(custom), "它改的是请求本身:推进请求配置代次");
                Assert.IsFalse(EndpointQuirks.Of(custom) is { StoreResponses: false });

                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 1;
                await PumpAsync();
                Assert.IsFalse(panel.IsVisible, "官方 openai 以目录为准,不摆开关");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Review_DraftModelInheritanceSendsNoProviderKeyToAnotherOrigin(bool savedOwnKey)
    {
        OnUi(async () =>
        {
            await using var original = new SseStub(ProbeAnswerSse);
            await using var draftHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "m", HasOwnApiKey = savedOwnKey };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = original.BaseUrl, Models = [model] };
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
                // 草稿改成继承、覆盖地址在别的服务器:与保存之后一样不借供应商的 Key(主槽也不借)
                Assert.AreEqual("Bearer not-needed", draftHost.Authorizations.Single());
                Assert.IsEmpty(original.Requests);
                Assert.AreEqual(savedOwnKey, model.HasOwnApiKey);
                Assert.IsNull(model.BaseUrlOverride);
                Assert.AreEqual("saved-own", await store.GetApiKeyAsync(model.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void CrossOriginOverride_ExistingModelIsFlaggedAndCanKeepUsingKeyOneInOneClick()
    {
        OnUi(async () =>
        {
            await using var providerHost = new SseStub(ProbeAnswerSse);
            await using var elsewhereHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            // 存量配置:覆盖地址在别的服务器、继承供应商 Key 的模型(以前借第 1 把)
            var elsewhere = new AiModelConfig { Name = "Elsewhere", Model = "m", BaseUrlOverride = elsewhereHost.BaseUrl };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                BaseUrl = providerHost.BaseUrl,
                Models = [new AiModelConfig { Model = "home" }, elsewhere]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = elsewhere.Id };
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                // 供应商页点名
                list.SelectedIndex = 0;
                await PumpAsync();
                TextBlock listed = view.GetControl<TextBlock>("CrossOriginModelsHintText");
                Assert.IsTrue(listed.IsVisible);
                Assert.AreEqual(loc.F("CrossOriginModelsNoKey", "Elsewhere"), listed.Text);

                // 模型页说明请求不带 Key;测试也不带
                list.SelectedIndex = 2;
                await PumpAsync();
                Assert.IsTrue(view.GetControl<StackPanel>("CrossOriginKeyPanel").IsVisible);
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer not-needed", elsewhereHost.Authorizations.Single(), "第 1 把不再跟去");

                // 一键沿用第 1 把:只填进表单,保存才落库
                Click(view.GetControl<Button>("CrossOriginKeyFillButton"));
                await WaitUntilAsync(() => status.Text == loc["CrossOriginPrimaryKeyFilled"]);
                Assert.IsTrue(view.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                Assert.AreEqual("primary", view.GetControl<TextBox>("ApiKeyBox").Text);
                Assert.IsFalse(view.GetControl<StackPanel>("CrossOriginKeyPanel").IsVisible);
                Assert.IsFalse(elsewhere.HasOwnApiKey, "没保存之前什么都没落库");
                Assert.IsNull(await store.GetApiKeyAsync(elsewhere.Id));

                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => status.Text == loc["Saved"]);
                Assert.IsTrue(elsewhere.HasOwnApiKey);
                Assert.AreEqual("primary", await store.GetApiKeyAsync(elsewhere.Id));
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer primary", elsewhereHost.Authorizations[^1], "明确交给它之后才带");
                Assert.IsEmpty(providerHost.Requests);

                list.SelectedIndex = 0;
                await PumpAsync();
                Assert.IsFalse(view.GetControl<TextBlock>("CrossOriginModelsHintText").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void CrossOriginOverride_HintFollowsTheUnsavedDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var model = new AiModelConfig { Model = "m" };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://relay.example/v1", Models = [model] };
            (Window window, SettingsView view, _, _) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = model.Id });
            try
            {
                StackPanel panel = view.GetControl<StackPanel>("CrossOriginKeyPanel");
                TextBox url = view.GetControl<TextBox>("BaseUrlBox");
                Assert.IsFalse(panel.IsVisible);
                url.Text = "https://other.example/v1";
                await PumpAsync();
                Assert.IsTrue(panel.IsVisible, "草稿改到别的服务器就提示");
                url.Text = "https://RELAY.example:443/v2";
                await PumpAsync();
                Assert.IsFalse(panel.IsVisible, "同源只换路径照旧借供应商的 Key");
                url.Text = "https://other.example/v1";
                view.GetControl<CheckBox>("OwnKeyCheck").IsChecked = true;
                await PumpAsync();
                Assert.IsFalse(panel.IsVisible, "勾了自带 Key 就不用提示");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_SaveAsksInADialogThenClearsSavedKeys()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "old-extra");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                TextBox url = view.GetControl<TextBox>("ProviderBaseUrlBox");
                Button save = view.GetControl<Button>("SaveButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                url.Text = newHost.BaseUrl;
                Click(save);
                // 独立的确认框列出要清的把数;框开着时「保存」锁着,再点一下(连点)不算确认
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2), await WaitForConfirmAsync(view));
                Assert.IsFalse(save.IsEnabled);
                Click(save);
                await PumpAsync();
                Assert.IsTrue(ConfirmBox(view).IsOpen);
                Assert.AreEqual(oldHost.BaseUrl, provider.BaseUrl, "没点确认就什么都不改");
                Assert.AreEqual("old-primary", await store.GetApiKeyAsync(provider.Id));

                // 取消:地址与 Key 都不动,草稿留着
                AnswerConfirm(view, accept: false);
                await WaitUntilAsync(() => save.IsEnabled && status.Text == loc["SetupResetCancelled"]);
                Assert.AreEqual(oldHost.BaseUrl, provider.BaseUrl);
                Assert.AreEqual("old-extra", await store.GetApiKeyAsync(extra));
                Assert.AreEqual(newHost.BaseUrl, url.Text);

                // 换个地址再存:重新问,这次确认
                url.Text = "https://third.example/v1";
                Click(save);
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2), await WaitForConfirmAsync(view));
                AnswerConfirm(view, accept: true);
                await WaitUntilAsync(() => save.IsEnabled && status.Text == loc["SetupSavedKeysCleared"]);

                Assert.AreEqual("https://third.example/v1", provider.BaseUrl);
                Assert.IsNull(await store.GetApiKeyAsync(provider.Id));
                Assert.IsNull(await store.GetApiKeyAsync(extra));
                Assert.IsEmpty(provider.AdditionalApiKeyIds);
                Assert.IsFalse(HasKeyRow(view, extra));
                Assert.IsEmpty(oldHost.Requests);
                Assert.IsEmpty(newHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_UnsavedTestSendsOnlyTheKeyTypedForIt()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupTestNeedsNewOriginKey"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(newHost.Requests, "旧地址的 Key 不能拿去测新地址");
                Assert.IsEmpty(oldHost.Requests);

                EditProviderKey(view, provider.Id).Text = "new-primary";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer new-primary", newHost.Authorizations.Single());
                Assert.IsEmpty(oldHost.Requests);
                Assert.AreEqual("old-primary", await store.GetApiKeyAsync(provider.Id), "测试不落库");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_KeylessProviderStillTestsTheUnsavedAddress()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                // Ollama 一类的无鉴权接入从本机换到另一台机器:没有已存的 Key 可带,不该被「请先填新 Key」拦下,照常测
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer not-needed", newHost.Authorizations.Single(), view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// 模型这一层换源(维护者 2026-10-25 第二轮第 24 条):覆盖基地址改到别的服务器,表单里预填的自带 Key 没动过,
    /// 保存先弹确认框(连点「保存」不算确认),取消什么都不改,确认后清掉这一行自带的 Key、地址落库。
    /// </summary>
    [TestMethod]
    public void ModelOverrideToAnotherOrigin_SaveAsksInADialogThenClearsTheOwnKey()
    {
        OnUi(async () =>
        {
            await using var home = new SseStub(ProbeAnswerSse);
            await using var elsewhere = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Name = "Own", Model = "m", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = home.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            await store.SetApiKeyAsync(model.Id, "own-key");
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 1;
                TextBox key = view.GetControl<TextBox>("ApiKeyBox");
                await WaitUntilAsync(() => key.Text == "own-key");
                TextBox url = view.GetControl<TextBox>("BaseUrlBox");
                Button save = view.GetControl<Button>("SaveButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                url.Text = elsewhere.BaseUrl;
                Click(save);
                Assert.AreEqual(loc["ModelBaseUrlKeyResetConfirm"], await WaitForConfirmAsync(view));
                Assert.AreEqual(loc["SetupKeyResetTitle"], ConfirmBox(view).GetLogicalDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "ConfirmTitle").Text);
                Assert.IsFalse(save.IsEnabled, "框开着时「保存」锁着");
                Click(save);
                await PumpAsync();
                Assert.IsTrue(ConfirmBox(view).IsOpen);
                Assert.IsNull(model.BaseUrlOverride, "没点确认就什么都不改");
                Assert.AreEqual("own-key", await store.GetApiKeyAsync(model.Id));

                AnswerConfirm(view, accept: false);
                await WaitUntilAsync(() => save.IsEnabled && status.Text == loc["SetupResetCancelled"]);
                Assert.IsNull(model.BaseUrlOverride);
                Assert.AreEqual("own-key", await store.GetApiKeyAsync(model.Id));
                Assert.AreEqual(elsewhere.BaseUrl, url.Text, "草稿留着");
                Assert.AreEqual("own-key", key.Text);

                Click(save);
                Assert.AreEqual(loc["ModelBaseUrlKeyResetConfirm"], await WaitForConfirmAsync(view));
                AnswerConfirm(view, accept: true);
                await WaitUntilAsync(() => save.IsEnabled && status.Text == loc["SetupSavedKeysCleared"]);
                Assert.AreEqual(elsewhere.BaseUrl, model.BaseUrlOverride);
                Assert.IsTrue(model.HasOwnApiKey, "仍是自带 Key:不改用供应商的 Key");
                Assert.IsNull(await store.GetApiKeyAsync(model.Id));
                Assert.AreEqual("", key.Text, "框里不再留着旧 Key");
                Assert.IsTrue(view.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                Assert.AreEqual("primary", await store.GetApiKeyAsync(provider.Id));
                AiModelConfig saved = (await new AiSettingsStore(context).LoadAsync()).Providers[0].Models[0];
                Assert.AreEqual(elsewhere.BaseUrl, saved.BaseUrlOverride);
                Assert.IsEmpty(home.Requests);
                Assert.IsEmpty(elsewhere.Requests);

                // 之后为新地址填的 Key 照常保存,不再问
                key.Text = "elsewhere-key";
                Click(save);
                await WaitUntilAsync(() => status.Text == loc["Saved"]);
                Assert.IsFalse(ConfirmBox(view).IsOpen);
                Assert.AreEqual("elsewhere-key", await store.GetApiKeyAsync(model.Id));
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// 模型这一层换源还没保存时,「测试」不带框里预填的已存自带 Key:请用户先填新服务器的 Key;
    /// 换成新填的就测它,框清空了就不带 Key 测。同一次保存里换了新 Key 也不必确认。
    /// </summary>
    [TestMethod]
    public void ModelOverrideToAnotherOrigin_UnsavedTestDoesNotSendTheSavedOwnKey()
    {
        OnUi(async () =>
        {
            await using var home = new SseStub(ProbeAnswerSse);
            await using var elsewhere = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Name = "Own", Model = "m", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = home.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            await store.SetApiKeyAsync(model.Id, "own-key");
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 1;
                TextBox key = view.GetControl<TextBox>("ApiKeyBox");
                await WaitUntilAsync(() => key.Text == "own-key");
                TextBlock status = view.GetControl<TextBlock>("StatusText");

                // 地址没换源:照常带着它测
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer own-key", home.Authorizations.Single(), status.Text);

                view.GetControl<TextBox>("BaseUrlBox").Text = elsewhere.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupTestNeedsNewOriginKey"], status.Text);
                Assert.IsEmpty(elsewhere.Requests, "旧地址的 Key 不能拿去测新地址");
                Assert.HasCount(1, home.Requests);

                key.Text = "";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer not-needed", elsewhere.Authorizations.Single(), status.Text);

                key.Text = "elsewhere-key";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer elsewhere-key", elsewhere.Authorizations[1], status.Text);
                Assert.AreEqual("own-key", await store.GetApiKeyAsync(model.Id), "测试不落库");
                Assert.HasCount(1, home.Requests);

                // 新填的 Key 是给新地址的:保存不问,直接落库
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => status.Text == loc["Saved"]);
                Assert.IsFalse(ConfirmBox(view).IsOpen);
                Assert.AreEqual(elsewhere.BaseUrl, model.BaseUrlOverride);
                Assert.AreEqual("elsewhere-key", await store.GetApiKeyAsync(model.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_FirstModelsOwnKeyIsNotTestedAgainstTheUnsavedAddress()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [own] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = own.Id };
            await store.SaveAsync(settings);
            await store.SetApiKeyAsync(own.Id, "old-own");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                // 供应商这一层没有 Key,但不是无鉴权接入:首模型自带的那把属于旧地址,同样不带去测;
                // 也没有继承供应商 Key 的模型可换,「先填新 Key」走不通,直说先保存、再给各模型填新服务器的 Key
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupTestNeedsModelKeys"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(newHost.Requests);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BaseUrlToAnotherOrigin_OwnKeyFirstModelTestsTheTypedKeyThroughAnInheritingModel(bool inheritingModel)
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                BaseUrl = oldHost.BaseUrl,
                Models = inheritingModel ? [own, new AiModelConfig { Model = "inherits" }] : [own]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = own.Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            await store.SetApiKeyAsync(own.Id, "old-own");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                // 没填新 Key:首模型自带的那把属于旧地址,不带去测。有继承供应商 Key 的模型可换,就请用户先填新 Key;
                // 没有的话填了也测不成,直说先保存、再给各模型填新服务器的 Key
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc[inheritingModel ? "SetupTestNeedsNewOriginKey" : "SetupTestNeedsModelKeys"], status.Text);
                Assert.IsEmpty(newHost.Requests);

                // 照提示填了新 Key:首模型用不上供应商的 Key,换一个继承供应商 Key 的模型来测;没有就如实说
                EditProviderKey(view, provider.Id).Text = "new-primary";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                if (inheritingModel)
                {
                    Assert.AreEqual("Bearer new-primary", newHost.Authorizations.Single());
                    Assert.Contains("\"model\":\"inherits\"", newHost.Requests.Single());
                }
                else
                {
                    Assert.AreEqual(loc["SetupKeyNoModel"], status.Text);
                    Assert.IsEmpty(newHost.Requests);
                }
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>已登录的自定义 OAuth 供应商,另挂一个自带 Key、跟着供应商地址走的模型。</summary>
    private static async Task<(AiSettingsStore Store, AiSettings Settings, AiProvider Provider, AiModelConfig Own)> SignedInCustomOAuthAsync(
        TestPluginContext context, string baseUrl)
    {
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
        provider.BaseUrl = baseUrl;
        provider.ModelsExpanded = true;
        provider.Models[0].Model = "m";
        var own = new AiModelConfig { Name = "Own model", Model = "own", HasOwnApiKey = true };
        provider.Models.Add(own);
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "at-old", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await store.SetApiKeyAsync(own.Id, "own-old");
        return (store, settings, provider, own);
    }

    [TestMethod]
    public void SubscriptionBaseUrlToAnotherOrigin_SaveAsksThenSignsOutAndClearsModelKeys()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            (AiSettingsStore store, AiSettings settings, AiProvider provider, AiModelConfig own) =
                await SignedInCustomOAuthAsync(context, oldHost.BaseUrl);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderAuthPanel").IsVisible, "前提:订阅登录的供应商页");
                Button save = view.GetControl<Button>("SaveButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;

                // 先说清:保存会退出登录、哪个模型自带的 Key 一起清;取消就什么都不动
                Click(save);
                string expected = loc["SetupBaseUrlSignOutConfirm"] + "\n" + loc.F("SetupBaseUrlKeyResetConfirmAlso", 1)
                    + "\n" + loc.F("SetupBaseUrlKeyResetModels", "Own model");
                Assert.AreEqual(expected, await WaitForConfirmAsync(view));
                Assert.AreEqual(loc["SetupSignOutResetTitle"],
                    ConfirmBox(view).GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ConfirmTitle").Text);
                AnswerConfirm(view, accept: false);
                await WaitUntilAsync(() => save.IsEnabled && status.Text == loc["SetupResetCancelled"]);
                Assert.AreEqual(oldHost.BaseUrl, provider.BaseUrl);
                Assert.AreEqual("at-old", (await store.GetTokensAsync(provider.Id))?.AccessToken);
                Assert.AreEqual("own-old", await store.GetApiKeyAsync(own.Id));

                Click(save);
                Assert.AreEqual(expected, await WaitForConfirmAsync(view));
                AnswerConfirm(view, accept: true);
                await WaitUntilAsync(() => save.IsEnabled
                    && status.Text == loc["SetupSavedSignedOut"] + "\n" + loc.F("SetupModelKeysCleared", "Own model"));
                Assert.AreEqual(newHost.BaseUrl, provider.BaseUrl);
                Assert.IsNull(await store.GetTokensAsync(provider.Id), "旧地址签出的令牌不留");
                Assert.IsNull(await store.GetApiKeyAsync(own.Id));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("ProviderAuthStatusText").Text == loc["SubscriptionNotSignedIn"]);
                Assert.IsEmpty(oldHost.Requests);
                Assert.IsEmpty(newHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SubscriptionBaseUrlToAnotherOrigin_UnsavedTestNeverCarriesTheSignIn(bool signedIn)
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            (AiSettingsStore store, AiSettings settings, AiProvider provider, AiModelConfig own) =
                await SignedInCustomOAuthAsync(context, oldHost.BaseUrl);
            if (!signedIn) await store.ClearTokensAsync(provider.Id);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                if (signedIn)
                {
                    // 还登着:请用户先保存(会退出登录),在新地址登录后再测
                    Assert.AreEqual(loc["SetupTestNeedsSaveSignIn"], view.GetControl<TextBlock>("StatusText").Text);
                    Assert.IsEmpty(newHost.Requests);
                }
                else
                {
                    // 没登录:照常测,不带任何已存的凭据
                    Assert.AreEqual("Bearer not-needed", newHost.Authorizations.Single(), view.GetControl<TextBlock>("StatusText").Text);
                }
                Assert.IsEmpty(oldHost.Requests);
                Assert.AreEqual(oldHost.BaseUrl, provider.BaseUrl, "测试不落库");
                Assert.AreEqual(signedIn ? "at-old" : null, (await store.GetTokensAsync(provider.Id))?.AccessToken);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void RemovedKeyOne_StillShowsAnEmptyPrimarySlotThatCanBeFilledBackIn()
    {
        OnUi(async () =>
        {
            await using var host = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = host.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "key-one");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "key-two");
            // 存量状态:第 1 把被移除过 —— 主槽只清了机密,槽还在,第 2 把留在补充槽
            await store.RemoveProviderApiKeyAsync(settings, provider, provider.Id);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                Assert.IsTrue(HasKeyRow(view, provider.Id), "空着的主槽照样摆出来");
                Assert.AreEqual(loc.F("SetupApiKeyOrdinal", 1) + " · " + loc["SetupApiKeyEmptySlot"],
                    KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").GetLogicalDescendants().OfType<TextBlock>().Single().Text);
                Assert.AreEqual(loc["StatusNeedsKey"], KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text);
                Assert.IsFalse(KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").IsEnabled);
                Assert.IsFalse(KeyControl<Button>(view, provider.Id, "ProviderKeyRemoveButton").IsEnabled, "没有 Key 可移除");
                Assert.IsFalse(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton").IsEnabled, "没有 Key 可测");
                Assert.IsTrue(KeyControl<Button>(view, provider.Id, "ProviderKeyEditButton").IsEnabled);
                Assert.IsTrue(view.GetControl<Button>("ProviderProbeAllKeysButton").IsEnabled, "空主槽不算坏行,检测全部照常可用(只测有 Key 的)");
                Assert.IsFalse(view.GetControl<TextBlock>("ProviderAllKeysHintText").IsVisible);
                Assert.IsTrue(HasKeyRow(view, extra));

                // 铅笔填回第 1 把,保存:落进主槽,第 2 把照旧
                EditProviderKey(view, provider.Id).Text = "key-one-again";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                Assert.AreEqual("key-one-again", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("key-two", await store.GetApiKeyAsync(extra));
                Assert.AreSequenceEqual([extra], [.. provider.AdditionalApiKeyIds]);
                await WaitUntilAsync(() => KeyControl<Button>(view, provider.Id, "ProviderKeyRemoveButton").IsEnabled);
                Assert.IsTrue(KeyControl<Button>(view, provider.Id, "ProviderKeyRevealButton").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ConfirmOverlay_DefaultsToCancel_AndEscapeReplacementOrDetachAllCancel()
    {
        OnUi(async () =>
        {
            var overlay = new ConfirmOverlay();
            var below = new Button { Name = "Below" };
            var window = new Window { Content = new Grid { Children = { below, overlay } }, Width = 600, Height = 400 };
            window.Show(); await PumpAsync();
            try
            {
                Task<bool> first = overlay.AskAsync("title", "message", "Remove", "Cancel");
                await PumpAsync();
                Assert.IsTrue(overlay.IsVisible && overlay.IsOpen);
                Button cancel = overlay.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ConfirmCancelButton");
                Assert.IsTrue(cancel.IsFocused, "会删东西的确认默认落在「取消」上:顺手一个回车不该把 Key 清掉");
                // 同一时刻只问一件事:再问就把前一个按取消收掉
                Task<bool> second = overlay.AskAsync("title", "again", "Remove", "Cancel");
                Assert.IsFalse(await first);
                overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.IsFalse(await second, "Esc 等于取消");
                Assert.IsFalse(overlay.IsVisible);
                Task<bool> third = overlay.AskAsync("title", "detached", "Remove", "Cancel");
                window.Content = null; // 视图被拆下(关窗、换页)
                Assert.IsFalse(await third);
                Task<bool> fourth = overlay.AskAsync("title", "accepted", "Remove", "Cancel");
                Click(overlay.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ConfirmAcceptButton"));
                Assert.IsTrue(await fourth, "只有点框里的确认按钮才算确认");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("same-settings")]
    [DataRow("same-slot")]
    [DataRow("other-window")]
    public void BaseUrlToAnotherOrigin_KeyStoredAfterThePromptAsksAgainInsteadOfClearingIt(string where)
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                Button save = view.GetControl<Button>("SaveButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                Click(save);
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 1), await WaitForConfirmAsync(view));

                // 确认框列着「清 1 把」的时候又存进一把:目录页走同一份设置(加一把,或给同一个槽换上新服务器的 Key);
                // 另一个窗口写盘后这边冲突重载
                string added = provider.Id;
                if (where == "same-settings")
                {
                    added = await store.AddProviderApiKeyAsync(settings, provider, "added-after-prompt");
                }
                else if (where == "same-slot")
                {
                    await store.ReplaceProviderApiKeyAsync(settings, provider, provider.Id, "added-after-prompt");
                }
                else
                {
                    var external = new AiSettingsStore(context);
                    AiSettings other = await external.LoadAsync();
                    added = await external.AddProviderApiKeyAsync(other, other.Providers[0], "added-after-prompt");
                }
                AnswerConfirm(view, accept: true);
                if (where == "other-window")
                {
                    await WaitUntilAsync(() => save.IsEnabled && status.Text == loc["SetupConfigChanged"]);
                    Assert.AreEqual(newHost.BaseUrl, view.GetControl<TextBox>("ProviderBaseUrlBox").Text, "草稿地址保留");
                    Assert.AreEqual("added-after-prompt", await store.GetApiKeyAsync(added), "冲突时什么都不清");
                    Click(save);
                    Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2), await WaitForConfirmAsync(view));
                }
                else
                {
                    // 确认的只是框里那 1 把:要清的变了,存储层再拒一次,换一句话再问 —— 一字不差的框看着像没点上
                    Assert.AreEqual(loc.F("SetupBaseUrlKeyResetChanged", where == "same-slot" ? 1 : 2), await WaitForConfirmAsync(view));
                }
                Assert.AreEqual("added-after-prompt", await store.GetApiKeyAsync(added), "旧的确认不能把它一起清掉");
                Assert.AreEqual(oldHost.BaseUrl, provider.BaseUrl);

                AnswerConfirm(view, accept: true);
                await WaitUntilAsync(() => save.IsEnabled && status.Text == loc["SetupSavedKeysCleared"]);
                Assert.IsNull(await store.GetApiKeyAsync(added));
                Assert.AreEqual(newHost.BaseUrl, provider.BaseUrl);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_SavedStatusNamesModelsWhoseOwnKeyWasCleared()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var own = new AiModelConfig { Name = "Own model", Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }, own] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            await store.SetApiKeyAsync(own.Id, "old-own");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                Button save = view.GetControl<Button>("SaveButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                Click(save);
                // 确认框里就点名:哪几个模型自带的 Key 会一起清
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2) + "\n" + loc.F("SetupBaseUrlKeyResetModels", "Own model"),
                    await WaitForConfirmAsync(view));
                AnswerConfirm(view, accept: true);
                // 自带 Key 的模型不会改用供应商的新 Key:点名请用户各自重填
                await WaitUntilAsync(() => save.IsEnabled
                    && status.Text == loc["SetupSavedKeysCleared"] + "\n" + loc.F("SetupModelKeysCleared", "Own model"));
                Assert.IsNull(await store.GetApiKeyAsync(own.Id));
                Assert.IsTrue(own.HasOwnApiKey);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_SavedStatusCallsAnUnnamedModelWhatTheListCallsIt()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            // 名称与模型 id 都还空着的模型,同样可以自带 Key
            var own = new AiModelConfig { HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }, own] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            await store.SetApiKeyAsync(own.Id, "old-own");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                Button save = view.GetControl<Button>("SaveButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                Click(save);
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2) + "\n" + loc.F("SetupBaseUrlKeyResetModels", loc["Unnamed"]),
                    await WaitForConfirmAsync(view));
                AnswerConfirm(view, accept: true);
                // 点名时与左栏一样叫「(未命名)」,不在冒号后面留一个空名字
                await WaitUntilAsync(() => save.IsEnabled
                    && status.Text == loc["SetupSavedKeysCleared"] + "\n" + loc.F("SetupModelKeysCleared", loc["Unnamed"]));
                Assert.IsNull(await store.GetApiKeyAsync(own.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_ClearedStatusReplacesThePromptWhileAnotherKeyIsBeingEdited()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            var sourceStore = new AiSettingsStore(source);
            await sourceStore.AddProviderApiKeyAsync(settings, provider, "old-primary");
            string extra = await sourceStore.AddProviderApiKeyAsync(settings, provider, "old-extra");
            var secrets = new DelayedSecrets(source.Secrets, $"apikey:{provider.Id}", delayMutation: true);
            using var context = new TestPluginContext { Secrets = secrets };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                Button save = view.GetControl<Button>("SaveButton");
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                Click(save);
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2), await WaitForConfirmAsync(view));

                // 暂停在删除机密之前;写设置时 Key 已经删除,定时刷新可能已移除第二行。
                AnswerConfirm(view, accept: true);
                await secrets.Started.WaitAsync(TimeSpan.FromSeconds(15));
                await PumpAsync(240); // 跨过一秒的 Key 行刷新周期,确认行仍可编辑。
                EditProviderKey(view, extra);
                secrets.Release();
                await WaitUntilAsync(() => save.IsEnabled && provider.BaseUrl == newHost.BaseUrl);
                // Key 已经清了:保存结果要说出来,不能被别的状态盖住
                Assert.AreEqual(loc["SetupSavedKeysCleared"], status.Text);
                Assert.IsNull(await store.GetApiKeyAsync(provider.Id));
                Assert.IsNull(await store.GetApiKeyAsync(extra));
            }
            finally { secrets.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_EmptyPrimarySlotStillTestsWithoutTheOtherSlots()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "old-extra");
            // 移除第 1 把:主槽只清机密,第 2 把留在补充槽
            await store.RemoveProviderApiKeyAsync(settings, provider, provider.Id);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                // 未保存的跨源探活只带主槽,补充槽不跟去:主槽空着就没有 Key 会发过去,换到无鉴权的新主机照常测
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(1, newHost.Authorizations, view.GetControl<TextBlock>("StatusText").Text);
                Assert.AreEqual("Bearer not-needed", newHost.Authorizations[0]);
                Assert.IsEmpty(oldHost.Requests);
                Assert.AreEqual("old-extra", await store.GetApiKeyAsync(extra));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("ANTHROPIC_API_KEY")]
    [DataRow("ANTHROPIC_AUTH_TOKEN")]
    [DataRow(null)]
    public void BaseUrlToAnotherOrigin_BlankKeyWithAnEmptyPrimaryTestsWithoutTheMachineKey(string? variable)
    {
        OnUi(async () =>
        {
            string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            string? authToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
            if (variable is not null) Environment.SetEnvironmentVariable(variable, "machine-secret");
            await using var oldHost = new SseStub(AnthropicProbeAnswerSse);
            await using var newHost = new SseStub(AnthropicProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                BaseUrl = oldHost.BaseUrl,
                DefaultProtocol = ChatProtocol.AnthropicMessages,
                Models = [new AiModelConfig { Model = "m" }]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "old-extra");
            await store.RemoveProviderApiKeyAsync(settings, provider, provider.Id);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                // 主槽空着,只把第 2 把改成空格:探活不带任何已存的 Key。本机设了环境变量也照常测 ——
                // 没有 Key 时它们只发往官方 api.anthropic.com,这台新主机拿不到
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                EditProviderKey(view, extra).Text = "   ";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                string? status = view.GetControl<TextBlock>("StatusText").Text;
                Assert.HasCount(1, newHost.Requests, status);
                Assert.IsNull(newHost.Authorizations.Single(), "本机的 Key 不能跟去");
                Assert.IsNull(newHost.RequestHeaders.Single()["x-api-key"]);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally
            {
                window.Close();
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
                Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", authToken);
            }
        });
    }

    [TestMethod]
    [DataRow("ANTHROPIC_API_KEY")]
    [DataRow("ANTHROPIC_AUTH_TOKEN")]
    public void BaseUrlToAnotherOrigin_KeylessAnthropicProviderDoesNotSendTheMachineKey(string variable)
    {
        OnUi(async () =>
        {
            // 无鉴权的 Anthropic 接入换到另一台主机:本机环境变量里的 Key 只给官方主机,不会跟去,所以照常测、不带任何 Key
            (string? status, IReadOnlyList<string> oldRequests, IReadOnlyList<string> newRequests, IReadOnlyList<string?> sent) =
                await TestKeylessAnthropicMoveAsync(variable);
            Assert.HasCount(1, newRequests, status);
            Assert.IsNull(sent.Single(), "本机的 Key 不能发往这台主机");
            Assert.IsEmpty(oldRequests);
        });
    }

    [TestMethod]
    [DataRow("ANTHROPIC_API_KEY")]
    [DataRow("ANTHROPIC_AUTH_TOKEN")]
    public void BaseUrlToAnotherOrigin_BlankTypedKeyDoesNotLetTheMachineKeyOut(string variable)
    {
        OnUi(async () =>
        {
            string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            string? authToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable(variable, "machine-secret");
            await using var oldHost = new SseStub(AnthropicProbeAnswerSse);
            await using var newHost = new SseStub(AnthropicProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                BaseUrl = oldHost.BaseUrl,
                DefaultProtocol = ChatProtocol.AnthropicMessages,
                Models = [new AiModelConfig { Model = "m" }]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                // 主槽里改成只有空格:算不上新填的 Key,按没填处理 —— 已存的旧主 Key 还在,照样请用户先填新 Key
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                EditProviderKey(view, provider.Id).Text = "   ";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(new Loc("en")["SetupTestNeedsNewOriginKey"], view.GetControl<TextBlock>("StatusText").Text,
                    string.Join(", ", newHost.Authorizations));
                Assert.IsEmpty(newHost.Requests);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally
            {
                window.Close();
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
                Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", authToken);
            }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_KeylessAnthropicProviderWithoutAMachineKeyStillTests()
    {
        OnUi(async () =>
        {
            (string? status, IReadOnlyList<string> oldRequests, IReadOnlyList<string> newRequests, _) =
                await TestKeylessAnthropicMoveAsync(null);
            Assert.HasCount(1, newRequests, status);
            Assert.AreEqual(new Loc("en").F("TestOk", "OK"), status);
            Assert.IsEmpty(oldRequests);
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_BlankTypedKeyDoesNotStandInForTheFirstModelsOwnKey()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [own, new AiModelConfig { Model = "inherits" }] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = own.Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            await store.SetApiKeyAsync(own.Id, "old-own");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                // 编辑框里只有空格:不是新服务器的 Key,不能借它换一个继承模型、空着凭据去测;按没填处理
                EditProviderKey(view, provider.Id).Text = "   ";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupTestNeedsNewOriginKey"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(newHost.Requests);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_ModelWithoutAModelIdIsNotUsedToTestTheTypedKey()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            // 唯一继承供应商 Key 的模型还没填模型 id(名称也空着)
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [own, new AiModelConfig()] };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = own.Id };
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            await store.SetApiKeyAsync(own.Id, "old-own");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                TextBlock status = view.GetControl<TextBlock>("StatusText");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                // 填了供应商的新 Key:模型 id 为空的那个发不出像样的请求,不拿它来测
                TextBox typed = EditProviderKey(view, provider.Id);
                typed.Text = "new-primary";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupKeyNoModel"], status.Text);
                Assert.IsEmpty(newHost.Requests);

                // 没填新 Key:同样没有能换的模型,直说先保存、再给各模型填新服务器的 Key
                typed.Text = "";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupTestNeedsModelKeys"], status.Text);
                Assert.IsEmpty(newHost.Requests);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_ConfigurationChangedWhileReadingTheKeyIsNotTested()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [own] };
            // 不设活跃模型:打开时选中供应商行,模型编辑器不先读它的 Key
            var settings = new AiSettings { Providers = [provider] };
            // 首模型勾了自带 Key 却还没填;它的 Key 不在缓存里,点「测试」要真等机密存储 —— 把这一下挂住
            var delayed = new DelayedSecrets(source.Secrets, $"apikey:{own.Id}");
            using var context = new TestPluginContext { Secrets = delayed };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "old-primary");
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                Task test = StartTestAsync(view);
                await delayed.Started.WaitAsync(TimeSpan.FromSeconds(15));
                // 读的期间,同一份设置里这个模型改成了继承供应商 Key(别处保存):探活改带主槽那把旧 Key,刚读到的「没有」已不作数
                string expected = System.Text.Json.JsonSerializer.Serialize(own);
                AiModelConfig inherits = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(expected)!;
                inherits.HasOwnApiKey = false;
                await store.SaveModelFormAsync(settings, provider, own, inherits, expected, apiKey: null, expectedKey: null);
                delayed.Release();
                await test.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupTestNeedsNewOriginKey"], view.GetControl<TextBlock>("StatusText").Text,
                    string.Join(", ", newHost.Authorizations));
                Assert.IsEmpty(newHost.Requests, "旧地址的 Key 不能拿去测新地址");
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { delayed.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_SecondClickWhileReadingTheKeyDoesNotTestTwice()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [own] };
            // 不设活跃模型:打开时选中供应商行,模型编辑器不先读它的 Key;首模型勾了自带 Key 却还没填(无鉴权接入照常测)
            var settings = new AiSettings { Providers = [provider] };
            var delayed = new DelayedSecrets(source.Secrets, $"apikey:{own.Id}");
            using var context = new TestPluginContext { Secrets = delayed };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                Task first = StartTestAsync(view);
                await delayed.Started.WaitAsync(TimeSpan.FromSeconds(15));
                // 读已存 Key 的期间按钮还没置灰:连点的第二下不能再起一次探活
                Assert.IsTrue(view.GetControl<Button>("TestButton").IsEnabled);
                Task second = StartTestAsync(view);
                delayed.Release();
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(1, newHost.Requests);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { delayed.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void BaseUrlToAnotherOrigin_ClickAfterEditingTheAddressDuringTheReadTestsTheNewAddress()
    {
        OnUi(async () =>
        {
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var firstHost = new SseStub(ProbeAnswerSse);
            await using var secondHost = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldHost.BaseUrl, Models = [own] };
            var settings = new AiSettings { Providers = [provider] };
            var delayed = new DelayedSecrets(source.Secrets, $"apikey:{own.Id}");
            using var context = new TestPluginContext { Secrets = delayed };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = firstHost.BaseUrl;
                Task first = StartTestAsync(view);
                await delayed.Started.WaitAsync(TimeSpan.FromSeconds(15));
                // 第一击还在读已存 Key,用户改了地址再点:测的应是框里现在的地址,第一击作废,也不能两击都被吞掉
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = secondHost.BaseUrl;
                Task second = StartTestAsync(view);
                delayed.Release();
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsEmpty(firstHost.Requests);
                Assert.HasCount(1, secondHost.Requests, view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(oldHost.Requests);
            }
            finally { delayed.Release(); window.Close(); }
        });
    }

    // 无鉴权的 Anthropic 接入把地址换到另一台主机、还没保存就点「测试」;machineVariable 是本机设了的那个环境变量(null 为都没设)
    private static async Task<(string? Status, IReadOnlyList<string> OldRequests, IReadOnlyList<string> NewRequests,
        IReadOnlyList<string?> NewAuthorizations)> TestKeylessAnthropicMoveAsync(string? machineVariable)
    {
        string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string? authToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
            if (machineVariable is not null) Environment.SetEnvironmentVariable(machineVariable, "machine-secret");
            await using var oldHost = new SseStub(AnthropicProbeAnswerSse);
            await using var newHost = new SseStub(AnthropicProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                BaseUrl = oldHost.BaseUrl,
                DefaultProtocol = ChatProtocol.AnthropicMessages,
                Models = [new AiModelConfig { Model = "m" }]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
            await store.SaveAsync(settings);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 0;
                await PumpAsync();
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = newHost.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                return (view.GetControl<TextBlock>("StatusText").Text, oldHost.Requests, newHost.Requests, newHost.Authorizations);
            }
            finally { window.Close(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", authToken);
        }
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
            var provider = new AiProvider { ModelsExpanded = true };
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
                Assert.IsFalse(model.DefaultContextWindow);
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
            var provider = new AiProvider { ModelsExpanded = true };
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
                else
                {
                    Assert.Contains(loc["ContextWindowDefaultHint"], hint.Text!);
                }

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
            var provider = new AiProvider { ModelsExpanded = true };
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
                Assert.IsFalse(saved.DefaultContextWindow);
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
                ModelsExpanded = true,
                Name = "original",
                BaseUrl = "https://same.example",
                Models = [model],
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
            var provider = new AiProvider { ModelsExpanded = true };
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
                Assert.IsFalse(saved.DefaultContextWindow);
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl };
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
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var pulling = (Task)typeof(SettingsView).GetMethod("PullModelsAsync", flags)!.Invoke(view, null)!;
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
                    Assert.AreSequenceEqual(["private-atomic-settings-pull"], provider.AvailableModels);
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
                Assert.IsFalse(saved.DefaultContextWindow);
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
            var provider = new AiProvider { ModelsExpanded = true, CatalogId = "openai", AvailableModels = ["private-model", "known-model"] };
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
                Assert.IsFalse(saved.DefaultContextWindow);
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
            await using var endpoint = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var secrets = new KeySettingsSecrets(source.Secrets);
            using var context = new TestPluginContext { Secrets = secrets };
            var provider = new AiProvider { ModelsExpanded = true, Name = "saved", BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
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
            await using var oldHost = new SseStub(ProbeAnswerSse);
            await using var newHost = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "m", HasOwnApiKey = action == "own" };
            var provider = new AiProvider { ModelsExpanded = true, Name = "original", BaseUrl = oldHost.BaseUrl, Models = [model] };
            var initial = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            await store.AddProviderApiKeyAsync(initial, provider, "old-provider-key");
            if (model.HasOwnApiKey) await store.SetApiKeyAsync(model.Id, "old-model-key");
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
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
                // 另一个窗口已确认换源:模型继承地址时它自带的旧 Key 也一并清掉,随后再为新主机填
                await external.SaveProviderApiKeyFormAsync(current, owner, draft, expected, "new-provider-key", owner.Id, "old-provider-key",
                    confirmedKeyReset: model.HasOwnApiKey ? [AiSettingsStore.KeyResetToken(model.Id, "old-model-key")] : null);
                if (model.HasOwnApiKey) await external.SetApiKeyAsync(model.Id, "new-model-key");
                if (action == "row")
                {
                    Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton"));
                    SelectKeyModel(view, provider.Id, model.Id);
                    await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["SetupConfigChanged"]);
                }
                else if (action == "pull")
                {
                    await ((Task)typeof(SettingsView).GetMethod("PullModelsAsync", flags)!.Invoke(view, null)!).WaitAsync(TimeSpan.FromSeconds(15));
                }
                else
                {
                    await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                }

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
            var provider = new AiProvider { ModelsExpanded = true, Name = "original", Models = [model] };
            var live = new AiSettings
            {
                Providers = [provider],
                ActiveModelId = model.Id,
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
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
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
            var first = new AiProvider { ModelsExpanded = true, Name = "first", BaseUrl = "https://first.example" };
            var second = new AiProvider { ModelsExpanded = true, Name = "second", BaseUrl = "https://second.example" };
            var initial = new AiSettings { Providers = [first, second] };
            await store.AddProviderApiKeyAsync(initial, first, "first-key");
            await store.AddProviderApiKeyAsync(initial, second, "second-key");
            AiSettings live = await store.LoadAsync();
            var view = new SettingsView(context, store, live, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show(); await PumpAsync();
            try
            {
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Task Save() => (Task)typeof(SettingsView).GetMethod("SaveAsync", flags)!.Invoke(view, [null])!;
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
            await using var oldEndpoint = new SseStub(ProbeAnswerSse);
            await using var newEndpoint = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var initial = new AiSettings
            {
                Providers = [new AiProvider { ModelsExpanded = true, Name = "original", BaseUrl = oldEndpoint.BaseUrl, Models = [new AiModelConfig { Model = "m" }] }]
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
                Assert.AreSequenceEqual([added], saved.Providers[0].AdditionalApiKeyIds);
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
            await using var endpoint = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var model = new AiModelConfig { Model = "m", HasOwnApiKey = ownKey, Temperature = 0.2f };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
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
            var provider = new AiProvider { ModelsExpanded = true, Name = "original provider", BaseUrl = "https://original.example", Models = [model] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example", Models = [first, second] };
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
                Assert.IsFalse(saved.Models[navigateAway ? 1 : 0].DefaultContextWindow);
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example", Models = [model] };
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
            var first = new AiProvider { ModelsExpanded = true, Name = "owner-A", BaseUrl = "https://a.example", Models = [a] };
            var second = new AiProvider { ModelsExpanded = true, Name = "owner-B", BaseUrl = "https://old-b.example", Models = [b] };
            var third = new AiProvider { ModelsExpanded = true, Name = "owner-C", BaseUrl = "https://c.example", Models = [c] };
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
                        Assert.IsTrue(view.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                    }
                    Assert.IsTrue(name.IsFocused);
                }
                else
                {
                    Assert.AreEqual("https://latest-b.example", view.GetControl<TextBox>("ProviderBaseUrlBox").Text);
                }

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
            await using var endpoint = new SseStub(ProbeAnswerSse);
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m" }] };
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
                Assert.AreSequenceEqual([extra], persisted.Providers[0].AdditionalApiKeyIds);
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m" }] };
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
            await using var original = new SseStub(ProbeAnswerSse);
            await using var changed = new SseStub(ProbeAnswerSse);
            using var source = new TestPluginContext();
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = original.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
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
    private const string AnthropicProbeAnswerSse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_probe","type":"message","role":"assistant","model":"m","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

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

    private static ConfirmOverlay ConfirmBox(Control view) => view.GetLogicalDescendants().OfType<ConfirmOverlay>().Single();

    /// <summary>等确认框弹出来,返回框里的说明。</summary>
    private static async Task<string?> WaitForConfirmAsync(Control view)
    {
        ConfirmOverlay box = ConfirmBox(view);
        await WaitUntilAsync(() => box.IsOpen);
        return box.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ConfirmMessage").Text;
    }

    /// <summary>点确认框里的「确认」或「取消」。</summary>
    private static void AnswerConfirm(Control view, bool accept) => Click(ConfirmBox(view).GetLogicalDescendants()
        .OfType<Button>().Single(button => button.Name == (accept ? "ConfirmAcceptButton" : "ConfirmCancelButton")));

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

    private static Task PumpAsync(int rounds = 20) => HeadlessPump.RunAsync(rounds);

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
            ModelsExpanded = true,
            Name = "Routin",
            BaseUrl = "https://routin.example/v1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models =
            [
                new AiModelConfig { Model = "gpt-5" },
                new AiModelConfig { Name = "Claude", Model = "claude-opus-5", Protocol = ChatProtocol.AnthropicMessages, HasOwnApiKey = true }
            ]
        };
        var ollama = new AiProvider { ModelsExpanded = true, Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Models = [new AiModelConfig { Model = "llama3.1" }] };
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

    [TestMethod]
    public void Sampling_AnthropicProtocolDisablesTemperatureAndTopP_ButKeepsTheSavedValues()
    {
        // Anthropic SDK 13.x 的接口版本(2026-10-21)不收 temperature / top_p,插件在这条协议上不发:
        // 设置页得把这件事摆出来,而不是让两格照常可填、填了却静默丢掉
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var claude = new AiModelConfig
            {
                Model = "claude-opus-5",
                Protocol = ChatProtocol.AnthropicMessages,
                Temperature = 0.3f,
                TopP = 0.9f
            };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                Name = "Relay",
                BaseUrl = "https://relay.example/v1",
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [claude, new AiModelConfig { Model = "gpt-5", Temperature = 0.5f }]
            };
            (Window window, SettingsView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = claude.Id });
            try
            {
                var loc = new Loc("en");
                TextBox temperature = view.GetControl<TextBox>("TemperatureBox");
                TextBox topP = view.GetControl<TextBox>("TopPBox");
                TextBox stop = view.GetControl<TextBox>("StopBox");
                TextBlock notice = view.GetControl<TextBlock>("SamplingAnthropicHintText");
                ComboBox protocol = view.GetControl<ComboBox>("ProtocolCombo");

                // 起手选中的就是覆盖成 Anthropic 协议的那个模型
                Assert.AreEqual((int)ChatProtocol.AnthropicMessages + 1, protocol.SelectedIndex);
                Assert.IsFalse(temperature.IsEnabled, "Anthropic 协议不发温度,这一格不该还能填");
                Assert.IsFalse(topP.IsEnabled);
                Assert.IsTrue(stop.IsEnabled, "停止序列在 Anthropic 协议上照发,不能跟着禁用");
                Assert.IsTrue(notice.IsVisible);
                Assert.AreEqual(loc["SamplingAnthropicNotSent"], notice.Text);
                Assert.AreEqual(0.3f.ToString(), temperature.Text, "只禁用、不清空:存着的值照样显示");
                Assert.AreEqual(0.9f.ToString(), topP.Text);
                Assert.DoesNotContain("Anthropic", view.GetControl<TextBlock>("SamplingHintText").Text!,
                    "通用说明不能再暗示这两格对 Anthropic 有效");

                // 改成继承供应商的 OpenAI 协议:两格重新可填,说明收起
                protocol.SelectedIndex = 0;
                await PumpAsync();
                Assert.IsTrue(temperature.IsEnabled);
                Assert.IsTrue(topP.IsEnabled);
                Assert.IsFalse(notice.IsVisible);

                // 切回 Anthropic 再保存:禁用着的两格把存着的值原样写回,不被当成"清空"
                protocol.SelectedIndex = (int)ChatProtocol.AnthropicMessages + 1;
                await PumpAsync();
                Assert.IsFalse(temperature.IsEnabled);
                stop.Text = "END";
                Click(view.GetControl<Button>("SaveButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["Saved"]);
                AiModelConfig saved = (await store.LoadAsync()).Providers.Single().Models[0];
                Assert.AreEqual(0.3f, saved.Temperature);
                Assert.AreEqual(0.9f, saved.TopP);
                Assert.AreEqual("END", saved.StopSequences);
                Assert.AreEqual(ChatProtocol.AnthropicMessages, saved.Protocol);

                // 走 OpenAI 协议的模型:照常可填
                view.GetControl<ListBox>("ProvidersList").SelectedIndex = 2;
                await PumpAsync();
                Assert.AreEqual("gpt-5", view.GetControl<TextBox>("ModelBox").Text);
                Assert.IsTrue(temperature.IsEnabled);
                Assert.IsTrue(topP.IsEnabled);
                Assert.IsFalse(notice.IsVisible);
                Assert.AreEqual(0.5f.ToString(), temperature.Text);
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
                Assert.IsTrue(settings.Providers[0].ModelsExpanded, "模型行不改变原有展开状态");
                Assert.AreEqual(before, list.SelectedIndex, "更没有被收到供应商行上去");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(12, false)]
    [DataRow(40, false)]
    [DataRow(40, true)]
    public void ModelListsStartCollapsed_RegardlessOfCountOrActiveModel(int count, bool selectAModel)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiSettings seed = ManyModels(count, selectAModel);
            string? activeModelId = seed.ActiveModelId;
            (Window window, SettingsView view, AiSettings settings, AiSettingsStore store) = await ShowAsync(context, seed);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.HasCount(2, rows, "长短模型列表都默认折叠");
                Assert.AreEqual("Relay", rows[0].Text);
                Assert.AreEqual(count.ToString(), rows[0].CountText);
                Assert.AreEqual("Ollama", rows[1].Text);
                Assert.AreEqual("1", rows[1].CountText);
                Assert.IsTrue(rows.All(row => row.IsProvider && !row.Expanded));
                Assert.AreEqual(selectAModel ? 0 : 1, list.SelectedIndex, "选中活跃模型所属供应商，不自动展开");
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);
                Assert.IsFalse(view.GetControl<StackPanel>("ModelEditor").IsVisible);
                Assert.AreEqual(activeModelId, settings.ActiveModelId, "导航不切换聊天模型");
                Assert.IsTrue(settings.Providers.All(provider => provider.ModelsExpanded is null));

                // 手动展开后再打开设置，仍保留用户的选择。
                string providerId = rows[list.SelectedIndex].Provider.Id;
                PressKey(list, Key.Right);
                await PumpAsync();
                AiSettings reloaded = await store.LoadAsync();
                var reopened = new SettingsView(context, store, reloaded, new Loc("en"), () => { });
                var reopenedRows = (List<ProviderNavItem>)reopened.GetControl<ListBox>("ProvidersList").ItemsSource!;
                Assert.IsTrue(reopenedRows.Single(row => row.IsProvider && row.Provider.Id == providerId).Expanded);
                Assert.AreEqual(activeModelId, reloaded.ActiveModelId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void SavedCollapse_IsNotOverriddenByTheActiveModel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiSettings seed = ManyModels(40, selectAModel: true);
            seed.Providers[0].ModelsExpanded = false;
            (Window window, SettingsView view, _, _) = await ShowAsync(context, seed);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                Assert.HasCount(2, (List<ProviderNavItem>)list.ItemsSource!);
                Assert.AreEqual(0, list.SelectedIndex);
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);
                Assert.IsFalse(seed.Providers[0].ModelsExpanded!.Value);
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
                Assert.IsTrue(settings.Providers[0].ModelsExpanded, "回到父行不改变原有展开状态");
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
            await using var held = new SseStub("", jsonContent: "", hold: true); // 空回复 = 探活失败,且被挂住
            await using var quick = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
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
            await using var held = new SseStub(ProbeAnswerSse, holdFirstStream: true, firstStreamContent: "",
                streamingReply: n => n == 3 ? "data: [DONE]\n\n" : ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
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
            await using var endpoint = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m1","choices":[{"index":0,"delta":{"content":"聊天答复"},"finish_reason":"stop"}]}

                data: [DONE]


                """, jsonContent: "OK", holdFirstStream: true, firstStreamContent: "",
                streamingReply: n => n == 3 ? "data: [DONE]\n\n" : ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                BaseUrl = endpoint.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                Models = [model]
            };
            var settings = new AiSettings
            {
                Providers = [provider],
                ActiveModelId = model.Id,
                SuggestFollowUps = false
            };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var panel = new ChatPanelView(context, store);
            var chatWindow = new Window { Width = 800, Height = 600, Content = panel };
            chatWindow.Show();
            await WaitUntilAsync(() => panel.GetControl<ListBox>("ProviderList").SelectedIndex >= 0);
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
            await using var endpoint = new SseStub(ProbeAnswerSse, hold: true);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
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
                AiProvider provider = settings.Providers[0];
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
                Model = "old-model",
                MaxInputTokens = 10000,
                MaxTokens = 1000,
                InputPricePerMillion = 1,
                OutputPricePerMillion = 2,
                CachedInputPricePerMillion = 0.5
            };
            ModelsDevCatalog.Apply(model, new ModelSpec(model.Model, model.Name, model.MaxInputTokens, model.MaxTokens,
                model.InputPricePerMillion, model.OutputPricePerMillion, model.CachedInputPricePerMillion, null), newModel: true);
            var provider = new AiProvider { ModelsExpanded = true, CatalogId = "openai", Models = [model] };
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
                Assert.IsTrue(saved.SupportsReasoning, "Materialise 更新的能力没有对应表单,保存不能回退它");
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
            var provider = new AiProvider { ModelsExpanded = true, Models = [first, second] };
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
            await using var held = new SseStub("", jsonContent: "", hold: true); // m1 的端点:挂住,放行后回空 = 失败
            await using var quick = new SseStub(ProbeAnswerSse);
            var m1 = new AiModelConfig { Model = "m1" };
            var m2 = new AiModelConfig { Model = "m2", BaseUrlOverride = quick.BaseUrl };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
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
            await using var oldEndpoint = new SseStub("", jsonContent: "", hold: true);
            await using var newEndpoint = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                Name = "subscription",
                BaseUrl = oldEndpoint.BaseUrl,
                Auth = AuthMethod.Subscription,
                OAuth = new OAuthConfig { Flow = OAuthFlow.GitHubCopilotDevice, ExchangeUrl = "https://auth.example/exchange" },
                Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SaveTokensAsync(provider.Id, new OAuthTokens
            {
                AccessToken = "same-token",
                RefreshToken = "long-lived",
                BaseUrl = oldEndpoint.BaseUrl,
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
                store.TokenClient = new Auth.OAuthClient(http);
                await store.SaveTokensAsync(provider.Id, new OAuthTokens
                {
                    AccessToken = "same-token",
                    RefreshToken = "long-lived",
                    BaseUrl = oldEndpoint.BaseUrl,
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
            // 旧账号首次与补试都空答,这一轮才真有失败;新一轮(第 3 个流)正常作答
            await using var endpoint = new SseStub(ProbeAnswerSse, holdFirstStream: true, firstStreamContent: "",
                streamingReply: n => n == 2 ? "data: [DONE]\n\n" : ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                Name = "subscription",
                BaseUrl = endpoint.BaseUrl,
                Auth = AuthMethod.Subscription,
                OAuth = new OAuthConfig { ExtraHeaders = "chatgpt-account-id: {account_id}" },
                Models = [model]
            };
            var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
            var health = new ProviderHealth();
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "same-token", AccountId = "old-account" });
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
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
                Assert.HasCount(2, endpoint.Requests, "旧探活补试过一次");
                Assert.AreEqual(loc.F("TestFail", loc["ProbeEmptyReply"]), view.GetControl<TextBlock>("StatusText").Text,
                    "前提:旧账号这一轮真的失败了,下面的冷却断言才有东西可拦");
                Assert.IsFalse(health.IsCooling(model.Id), "旧账号的失败不能冷却同 token 的新账号");

                health.Record(model.Id, false);
                Task newRun = StartTestAsync(view);
                await WaitUntilAsync(() => endpoint.Requests.Count == 3);
                await newRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("old-account", endpoint.AccountIds[1], "旧探活补试仍固定原账号");
                Assert.AreEqual("new-account", endpoint.AccountIds[2], "第二轮真实请求必须改用新账号头");
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
            await using var endpoint = new SseStub("", jsonContent: "", hold: true);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
                BaseUrl = endpoint.BaseUrl,
                Auth = AuthMethod.Subscription,
                OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken },
                Models = [model]
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
            await using var endpoint = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
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
                list.SelectedIndex = 1;
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
            await using var endpoint = new ErrorStub();
            AiProvider provider = ProviderCatalog.Custom.CreateProvider();
            provider.Name = "original account";
            provider.BaseUrl = "";
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(provider.Id, "original-key");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                SuggestFollowUps = false
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
                Assert.IsGreaterThan(0, endpoint.Requests, "目录保存后确实尝试过自动拉取");
                Assert.IsEmpty(persisted.AvailableModels, "拒绝请求不能伪造成功的型号清单");
                Assert.AreEqual(savedName, editor.GetControl<TextBlock>("BreadcrumbText").Text);
                Assert.IsTrue(editor.GetControl<Control>("PullModelsPanel").IsVisible);
                Assert.AreEqual(dirty ? "unfinished account" : savedName, name.Text);
                Assert.AreEqual(dirty ? "https://unfinished.example/v1" : endpoint.BaseUrl, address.Text);
                if (dirty)
                {
                    Assert.AreEqual("unfinished-key", key!.Text);
                }
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
                void OnChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
                {
                    if (args.Property == InputElement.IsEnabledProperty && button.IsEnabled)
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
    [DataRow(ReasoningLevel.Low, "low")]
    [DataRow(ReasoningLevel.ExtraHigh, "xhigh")]
    [DataRow(ReasoningLevel.Max, "max")]
    public void ModelProbe_UsesDraftReasoningAndOnlyCoolsMatchingSavedReasoning(ReasoningLevel level, string effort)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub("", holdFirstStream: true, firstStreamContent: "");
            var model = new AiModelConfig { Model = "m1", Reasoning = ReasoningLevel.High };
            var provider = new AiProvider
            {
                ModelsExpanded = true,
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
                view.GetControl<ComboBox>("ReasoningCombo").SelectedIndex = (int)level;
                Task draftProbe = StartTestAsync(view);
                string firstRequest = await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Contains($"\"reasoning_effort\":\"{effort}\"", firstRequest,
                    "手动探活发送的是当前未保存档位,不是已保存的 High");
                Assert.IsFalse(draftProbe.IsCompleted);
                endpoint.Release();
                await draftProbe.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(2, endpoint.Requests, "空回复探活必须完成一次补试");
                Assert.IsFalse(health.IsCooling(model.Id), "草稿档位的失败不能冷却仍为 High 的已保存模型");

                view.GetControl<Button>("SaveButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => model.Reasoning == level
                    && view.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]);
                Assert.AreEqual(level, (await store.LoadAsync()).Providers[0].Models[0].Reasoning);
                Task savedProbe = StartTestAsync(view);
                await savedProbe.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.HasCount(4, endpoint.Requests, "保存后的探活也必须完成一次补试");
                foreach (string request in endpoint.Requests)
                    Assert.Contains($"\"reasoning_effort\":\"{effort}\"", request, "探活及补试都必须固定当前所选档位");
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
            var first = new AiProvider { ModelsExpanded = true, Name = "A", BaseUrl = "https://a.example/v1", Models = [model] };
            var second = new AiProvider { ModelsExpanded = true, Name = "B", BaseUrl = "https://b.example/v1" };
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
            var first = new AiProvider { ModelsExpanded = true, Name = "A", BaseUrl = "https://a.example/v1" };
            var second = new AiProvider { ModelsExpanded = true, Name = "B", BaseUrl = "https://b.example/v1" };
            var settings = new AiSettings { Providers = [first, second] };
            await secretSource.Secrets.SetAsync($"apikey:{first.Id}", "key-a");
            var secrets = new DelayedSecrets(secretSource.Secrets, $"apikey:{first.Id}");
            using var context = new TestPluginContext { Secrets = secrets };
            (Window window, SettingsView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                await secrets.Started.WaitAsync(TimeSpan.FromSeconds(15));
                var saving = (Task)typeof(SettingsView).GetMethod("SaveAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, [null])!;
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
            var first = new AiProvider { ModelsExpanded = true, Name = "A", BaseUrl = "https://a.example/v1" };
            var second = new AiProvider { ModelsExpanded = true, Name = "B", BaseUrl = "https://b.example/v1" };
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
            var provider = new AiProvider { ModelsExpanded = true, Name = "original", BaseUrl = "https://old.example/v1" };
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
            await using var endpoint = new SseStub("", holdFirstStream: true, firstStreamContent: "");
            var model = new AiModelConfig { Model = "m1", Temperature = 0.25f, TopP = 0.5f, StopSequences = "stop-one\nstop-two" };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
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
                using (var body = System.Text.Json.JsonDocument.Parse(request))
                {
                    Assert.AreEqual(0.25f, body.RootElement.GetProperty("temperature").GetSingle());
                    Assert.AreEqual(0.5f, body.RootElement.GetProperty("top_p").GetSingle());
                    Assert.AreSequenceEqual(["stop-one", "stop-two"], [.. body.RootElement.GetProperty("stop").EnumerateArray().Select(s => s.GetString()!)]);
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
            {
                throw new InvalidOperationException("Only the unauthenticated public model index may use this transport.");
            }

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

    private sealed class KeySettingsStorage(PluginSdk.Storage.IPluginStorage inner) : PluginSdk.Storage.IPluginStorage
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
            var provider = new AiProvider { ModelsExpanded = true, Name = "manual", BaseUrl = "https://manual.example/v1", Models = [model] };
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
                // 移除第 1 把只清主槽机密:第 3 把还在,这一行照样摆着(空着,能用铅笔填回去)
                await WaitUntilAsync(() => KeyControl<TextBlock>(view, provider.Id, "ProviderKeyStatus").Text == new Loc("en")["StatusNeedsKey"]);
                Assert.IsNull(await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual(provider.Id, settings.Providers.Single().Id);
                Assert.AreEqual(model.Id, provider.Models.Single().Id);
                Assert.AreEqual("sk-third-9012", (await store.ResolveCredentialAsync(settings.FindModel(model.Id)!)).Value);
                Click(KeyControl<Button>(view, third, "ProviderKeyRemoveButton"));
                Click(KeyControl<Button>(view, third, "ProviderKeyRemoveButton"));
                await WaitUntilAsync(() => !HasKeyRow(view, third) && !HasKeyRow(view, provider.Id));
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m1" }] };
            var other = new AiProvider { ModelsExpanded = true, Name = "other" };
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
                    Point? origin = button.TranslatePoint(new Point(), KeyRow(view, extra));
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [first, chosen] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m1" }] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model, own, cross] };
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
                Assert.AreSequenceEqual(["Bearer key-a", "Bearer key-b", "Bearer key-c"], endpoint.Requests.Select(r => r.Auth).ToArray());
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
            var other = new AiProvider { ModelsExpanded = true, Name = "other" };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [new AiModelConfig { Model = "m1" }] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [m1, m2, own] };
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
                    string clock = TrailingClock().Match(status).Value;
                    Assert.AreEqual(loc.F("SetupKeyFailed", "m2", clock), status);
                    Assert.HasCount(1, endpoint.Requests, "换语言只重新格式化证据，不重测");
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
            var other = new AiProvider { ModelsExpanded = true, Name = "other" };
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
    public void ProviderApiKey_AdditionalSlotDraftNeverGoesToCrossOriginFirstModel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var own = new KeyProbeServer(_ => 200); own.Start();
            using var elsewhere = new KeyProbeServer(_ => 200); elsewhere.Start();
            // 首模型把地址改到了别家:补充槽的 Key 在任何路径上都只发往供应商自己的源
            var routed = new AiModelConfig { Model = "m-routed", BaseUrlOverride = elsewhere.BaseUrl };
            var local = new AiModelConfig { Model = "m-local" };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = own.BaseUrl, Models = [routed, local] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string b = await store.AddProviderApiKeyAsync(settings, provider, "additional-old");
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, new ProviderHealth());
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                EditProviderKey(view, b).Text = "additional-new";
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsEmpty(elsewhere.Requests, "补充槽的草稿 Key 不得送往首模型覆盖的别家地址");
                Assert.AreEqual("Bearer additional-new", own.Requests.Single().Auth, "改用同源的继承模型来测");
                Assert.Contains("\"model\":\"m-local\"", own.Requests.Single().Body);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void AddModel_AfterAnotherWindowSaved_ReloadsAndReportsTheConflict()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "http://127.0.0.1:9", Models = [new AiModelConfig { Model = "m1" }] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                // 另一份设置快照先落了盘:本页基线过期,「添加模型」的保存会被拒
                AiSettings other = await new AiSettingsStore(context).LoadAsync();
                other.SystemPrompt = "saved elsewhere";
                await new AiSettingsStore(context).SaveAsync(other);

                Click(view.GetControl<Button>("AddModelButton"));
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["SetupConfigChanged"]);
                Assert.AreEqual("saved elsewhere", settings.SystemPrompt, "冲突后重载到最新落盘,而不是留一份只活在内存里的改动");
                Assert.HasCount(1, provider.Models, "没落盘的新模型随重载撤掉,界面与磁盘一致");
                Assert.AreEqual("saved elsewhere", (await new AiSettingsStore(context).LoadAsync()).SystemPrompt);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderTest_PassingSavedKeyClearsThatKeysCooldown()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new KeyProbeServer(_ => 200); endpoint.Start();
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider] };
            var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            var health = new ProviderHealth();
            health.RecordKey(provider.Id, false);
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual("Bearer primary", endpoint.Requests.Single().Auth);
                Assert.IsFalse(health.IsKeyCooling(provider.Id), "已保存的 Key 刚测通,不能继续显示冷却、被故障转移绕开");
                Assert.IsFalse(health.IsCooling(model.Id));
            }
            finally { window.Close(); }
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = oldEndpoint.BaseUrl, Models = [model] };
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
                // 换到另一台主机:先在确认框里说会清掉旧主槽那一把,确认才保存;新填的 Key 落进主槽、补充槽复位
                Click(view.GetControl<Button>("SaveButton"));
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 1), await WaitForConfirmAsync(view));
                Assert.AreEqual(0, notifications); Assert.AreEqual(oldEndpoint.BaseUrl, provider.BaseUrl);
                AnswerConfirm(view, accept: true);
                await WaitUntilAsync(() => view.GetControl<TextBlock>("StatusText").Text == loc["SetupSavedOtherKeysCleared"]);
                Assert.AreEqual(1, notifications); Assert.AreEqual(newEndpoint.BaseUrl, provider.BaseUrl);
                Assert.AreEqual("additional-new", await new AiSettingsStore(context).GetApiKeyAsync(provider.Id));
                Assert.IsEmpty(provider.AdditionalApiKeyIds); Assert.IsFalse(HasKeyRow(view, b));
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton")); SelectKeyModel(view, provider.Id, model.Id);
                await WaitUntilAsync(() => newEndpoint.Requests.Count == 2);
                Assert.AreEqual("Bearer additional-new", newEndpoint.Requests[1].Auth);
                Assert.IsEmpty(oldEndpoint.Requests, "新 Key 不得发回旧 host，旧 Key 不得发向新 host");
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = oldEndpoint.BaseUrl;
                await StartTestAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(loc["SetupTestNeedsNewOriginKey"], view.GetControl<TextBlock>("StatusText").Text);
                Assert.IsEmpty(oldEndpoint.Requests, "没有新 Key 的跨 origin 草稿不带任何已存的 Key");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_FirstModelOwnKeyIsNotOverriddenByProviderDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext(); await using var endpoint = new SseStub(ProbeAnswerSse);
            var model = new AiModelConfig { Model = "own", HasOwnApiKey = true, BaseUrlOverride = endpoint.BaseUrl };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://provider.example", Models = [model] };
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
            var provider = new AiProvider { ModelsExpanded = true, Name = "original", BaseUrl = "https://old.example", Models = [new AiModelConfig { Model = "m1" }] };
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
                // 同源改路径:要测的是落盘失败的回滚,不牵涉换源清 Key 的确认
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://old.example/v2";
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
            var provider = new AiProvider { ModelsExpanded = true, Name = "before", BaseUrl = "https://before.example", Models = [new AiModelConfig { Model = "m1" }] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m1" }] };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example" };
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
            await using var same = new SseStub(ProbeAnswerSse); await using var other = new SseStub(ProbeAnswerSse);
            var a = new AiModelConfig { Model = "m1", Name = "duplicate" };
            var b = new AiModelConfig { Model = "m2", Name = "duplicate" };
            var cross = new AiModelConfig { Model = "m3", BaseUrlOverride = other.BaseUrl };
            var own = new AiModelConfig { Model = "own", HasOwnApiKey = true };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = same.BaseUrl, Models = [a, b, cross, own, new AiModelConfig()] };
            var settings = new AiSettings { Providers = [provider] }; var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "primary");
            string extra = await store.AddProviderApiKeyAsync(settings, provider, "additional");
            var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                // 跨源覆盖的模型不借供应商的任何一把 Key,主槽也不例外:哪一行的候选里都没有它;自带 Key、没填型号的也不在
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton"));
                var primaryChoices = KeyControl<ComboBox>(view, provider.Id, "ProviderKeyModelPicker").ItemsSource!.Cast<object>().ToList();
                Assert.HasCount(2, primaryChoices);
                Assert.AreNotEqual(primaryChoices[0].ToString(), primaryChoices[1].ToString(), "同名模型带短 ID 但绑定配置 ID");
                SelectKeyModel(view, provider.Id, b.Id);
                await WaitUntilAsync(() => same.Requests.Count == 1 && !IsKeyProbeRunning(KeyProbeState(view, provider.Id)));
                Assert.Contains("\"model\":\"m2\"", same.Requests.Single());
                Assert.AreEqual("Bearer primary", same.Authorizations.Single());
                // 补充槽:同样两项,按 ID 复用上次选的那个直接测
                Click(KeyControl<Button>(view, extra, "ProviderKeyProbeButton"));
                Assert.HasCount(2, KeyControl<ComboBox>(view, extra, "ProviderKeyModelPicker").ItemsSource!.Cast<object>().ToList());
                await WaitUntilAsync(() => same.Requests.Count == 2 && !IsKeyProbeRunning(KeyProbeState(view, extra)));
                Assert.Contains("\"model\":\"m2\"", same.Requests[1]);
                Assert.AreEqual("Bearer additional", same.Authorizations[1], "同名模型必须按 ID 复用上次选择");
                Assert.IsEmpty(other.Requests, "跨源覆盖地址一次都不能被拿供应商的 Key 去测");
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
            var first = new AiProvider { ModelsExpanded = true, Name = "A", BaseUrl = "https://a.example", Models = [new AiModelConfig { Model = "a" }] };
            var second = new AiProvider { ModelsExpanded = true, Name = "B", BaseUrl = "https://b.example" };
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
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = "https://same.example", Models = [new AiModelConfig { Model = "m1" }] };
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
                // 把唯一那条失败的时刻拨到冷却窗口之外(不依赖记录键的具体形状)
                var failed = (System.Collections.IDictionary)typeof(ProviderHealth).GetField("_failedAt",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(health)!;
                foreach (object entry in failed.Keys.Cast<object>().ToArray())
                    failed[entry] = Environment.TickCount64 - (long)ProviderHealth.Cooldown.TotalMilliseconds - 1;
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
            var provider = new AiProvider { ModelsExpanded = true, Name = "remove", Models = [model] };
            var other = new AiProvider { ModelsExpanded = true, Name = "keep" };
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
            using var context = new TestPluginContext(); await using var endpoint = new SseStub("");
            var model = new AiModelConfig { Model = "m1" };
            var provider = new AiProvider { ModelsExpanded = true, BaseUrl = endpoint.BaseUrl, Models = [model] };
            var settings = new AiSettings { Providers = [provider] }; var store = new AiSettingsStore(context);
            await store.AddProviderApiKeyAsync(settings, provider, "empty-reply-key");
            var health = new ProviderHealth(); var loc = new Loc("en");
            var view = new SettingsView(context, store, settings, loc, () => { }, health);
            var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); await PumpAsync();
            try
            {
                Click(KeyControl<Button>(view, provider.Id, "ProviderKeyProbeButton")); SelectKeyModel(view, provider.Id, model.Id);
                await WaitUntilAsync(() => endpoint.Requests.Count == 2 && !IsKeyProbeRunning(KeyProbeState(view, provider.Id)));
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

    /// <summary>状态行末尾的 <c>HH:mm</c>。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\d{2}:\d{2}$")]
    private static partial System.Text.RegularExpressions.Regex TrailingClock();
}
