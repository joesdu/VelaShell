using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>设置存储:配置往返、机密隔离与三种协议的客户端构造。</summary>
[TestClass]
public sealed class AiSettingsStoreTests
{
    private const string GuardReplySse = "data: {\"id\":\"r\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]\n\n";

    [TestMethod]
    [DataRow("success")]
    [DataRow("storage")]
    [DataRow("cancel")]
    [DataRow("live-conflict")]
    [DataRow("provider-reference")]
    [DataRow("model-reference")]
    public async Task CatalogueCommit_HeldJsonPublishesOnlySavedModelsAndRejectsPendingConsumers(string outcome)
    {
        using var oldHost = new SseStub(GuardReplySse);
        using var newHost = new SseStub(GuardReplySse);
        var storage = new FailingStorage(new InMemoryStorage());
        using var secretSource = new TestPluginContext();
        var secrets = new MutateThenFailSecrets(secretSource.Secrets);
        using var context = new TestPluginContext { Storage = storage, Secrets = secrets };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "old-model" };
        ModelsDevCatalog.Apply(model, new ModelSpec(model.Model, "", 128000, 1000, 1, 2, .5, false), newModel: true);
        var provider = new AiProvider { BaseUrl = oldHost.BaseUrl, AvailableModels = ["old-choice"], ModelsExpanded = false, Models = [model] };
        var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
        await store.SaveAsync(settings);
        await context.Secrets.SetAsync("apikey:" + provider.Id, "old-key");
        int secretWrites = secrets.Mutations;
        string originalJson = System.Text.Json.JsonSerializer.Serialize(settings);
        string expected = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        draft.BaseUrl = newHost.BaseUrl;
        draft.AvailableModels = ["new-model", "added-model"];
        draft.ModelsExpanded = null;
        draft.Models[0].Model = "new-model";
        ModelsDevCatalog.Apply(draft.Models[0], new ModelSpec("new-model", "", 65536, 4000, 3, 4, 1, true), newModel: true);
        draft.Models.Add(new AiModelConfig { Model = "added-model", DefaultContextWindow = true });
        string draftJson = System.Text.Json.JsonSerializer.Serialize(draft);
        ResolvedModel retained = settings.FindModel(model.Id)!;
        long epoch = store.ProviderConfigurationVersion(provider);
        using var cancellation = new CancellationTokenSource();
        storage.Hold = true;
        storage.FailHeldWrite = outcome == "storage";
        Task pending = store.SaveProviderCatalogueAsync(settings, provider, draft, expected, cancellation.Token);
        try
        {
            await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(originalJson, System.Text.Json.JsonSerializer.Serialize(settings), "目录候选、GUID、窗口与来源仍未发布");
            Assert.AreEqual(originalJson, System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")));
            Assert.AreEqual(epoch, store.ProviderConfigurationVersion(provider));
            (Exception? error, _) = await HealthProbe.ProbeAsync(store, retained);
            Assert.IsInstanceOfType<AiSettingsStore.ApiKeySlotChangedException>(error);
            await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveProviderCredentialAsync(provider));
            Assert.IsEmpty(oldHost.Requests);
            Assert.IsEmpty(newHost.Requests, "未提交站点不得接收旧配置机密");
            if (outcome == "cancel") cancellation.Cancel();
            if (outcome == "live-conflict") settings.PanelWidthPercent = 62;
            if (outcome == "provider-reference") settings.Providers[0] = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
            if (outcome == "model-reference") provider.Models[0] = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(System.Text.Json.JsonSerializer.Serialize(model))!;
        }
        finally { storage.Release.TrySetResult(); }
        if (outcome == "storage") await Assert.ThrowsAsync<IOException>(() => pending);
        else if (outcome == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        else if (outcome != "success") await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => pending);
        else await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(secretWrites, secrets.Mutations, "目录事务不应写入或删除 Key/OAuth");
        Assert.AreEqual(draftJson, System.Text.Json.JsonSerializer.Serialize(draft), "失败或成功均不反写提交草稿");
        if (outcome == "success")
        {
            Assert.AreSame(provider, settings.Providers.Single());
            Assert.AreSame(model, provider.Models[0], "自动更新沿用原模型 GUID 与活对象");
            Assert.AreEqual(draftJson, System.Text.Json.JsonSerializer.Serialize(provider));
            Assert.AreEqual(epoch + 1, store.ProviderConfigurationVersion(provider));
            (Exception? staleError, _) = await HealthProbe.ProbeAsync(store, retained);
            Assert.IsInstanceOfType<AiSettingsStore.ApiKeySlotChangedException>(staleError,
                "旧 ResolvedModel 保留提交前的出境地址，不能因活对象同 ID 更新而重新授权");
            ResolvedModel fresh = settings.FindModel(model.Id)!;
            Assert.AreSame(provider, fresh.Provider);
            Assert.AreSame(model, fresh.Config);
            Assert.AreEqual(newHost.BaseUrl, fresh.BaseUrl);
            Assert.IsEmpty(newHost.Requests, "旧解出快照被拒时不能已经发出 HTTP");
            (Exception? error, _) = await HealthProbe.ProbeAsync(store, fresh);
            Assert.IsNull(error, error?.ToString());
            Assert.AreEqual("Bearer old-key", newHost.Authorizations.Single());
            Assert.Contains("\"model\":\"new-model\"", newHost.Requests.Single());
            Assert.IsEmpty(oldHost.Requests);
        }
        else
        {
            Assert.AreEqual(expected, System.Text.Json.JsonSerializer.Serialize(provider));
            Assert.AreEqual(originalJson, System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")), "失败与晚到冲突都恢复原磁盘基线");
            Assert.AreEqual(epoch, store.ProviderConfigurationVersion(provider));
            if (outcome == "live-conflict") Assert.AreEqual(62, settings.PanelWidthPercent, "不得覆盖等待期间的活修改");
            storage.FailHeldWrite = false;
            AiProvider current = settings.Providers.Single();
            await store.SaveProviderCatalogueAsync(settings, current, draft, System.Text.Json.JsonSerializer.Serialize(current));
            Assert.AreEqual("new-model", current.Models[0].Model, "失败基线仍可明确重试，不产生幽灵型号");
        }
    }

    [TestMethod]
    public async Task CatalogueCommit_IndependentStaleBaselineCannotAbsorbOrPublishFailedCatalogue()
    {
        using var oldHost = new SseStub(GuardReplySse);
        using var newHost = new SseStub(GuardReplySse);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "old-model" }] };
        await store.SaveAsync(new AiSettings { Providers = [provider] });
        await store.SetApiKeyAsync(provider.Id, "old-key");
        AiSettings stale = await store.LoadAsync();
        AiSettings current = await new AiSettingsStore(context).LoadAsync();
        current.Providers[0].Name = "externally saved";
        current.Providers[0].BaseUrl = newHost.BaseUrl;
        await store.SaveAsync(current);
        string expected = System.Text.Json.JsonSerializer.Serialize(stale.Providers[0]);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        draft.AvailableModels = ["uncommitted-model"];
        draft.Models.Add(new AiModelConfig { Model = "uncommitted-model", DefaultContextWindow = true });
        string staleJson = System.Text.Json.JsonSerializer.Serialize(stale);
        string savedJson = System.Text.Json.JsonSerializer.Serialize(current);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
            store.SaveProviderCatalogueAsync(stale, stale.Providers[0], draft, expected));
        Assert.AreEqual(staleJson, System.Text.Json.JsonSerializer.Serialize(stale));
        Assert.AreEqual(savedJson, System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")));
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.SaveAsync(stale));
        (Exception? error, _) = await HealthProbe.ProbeAsync(store, stale.FindModel(provider.Models[0].Id)!);
        Assert.IsInstanceOfType<AiSettingsStore.ApiKeySlotChangedException>(error);
        Assert.IsEmpty(oldHost.Requests);
        Assert.IsEmpty(newHost.Requests);
    }

    [TestMethod]
    public async Task CatalogueCommit_MetadataDoesNotAdvanceRequestEpochButRequestAbaDoes()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "m", MaxInputTokens = 128000 }] };
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        string initial = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider metadata = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(initial)!;
        metadata.AvailableModels = ["m", "candidate"];
        metadata.ModelsExpanded = false;
        metadata.Models[0].DefaultContextWindow = true;
        metadata.Models[0].InputPricePerMillion = 3;
        await store.SaveProviderCatalogueAsync(settings, provider, metadata, initial);
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
        string baseline = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider changed = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(baseline)!;
        changed.Models[0].MaxInputTokens = 65536;
        await store.SaveProviderCatalogueAsync(settings, provider, changed, baseline);
        Assert.AreEqual(1L, store.ProviderConfigurationVersion(provider));
        await store.SaveProviderCatalogueAsync(settings, provider, metadata, System.Text.Json.JsonSerializer.Serialize(provider));
        Assert.AreEqual(2L, store.ProviderConfigurationVersion(provider), "A→B→A 仍拒绝等待前的旧历史请求");
        await store.SaveAsync(settings);
        Assert.AreEqual(2L, store.ProviderConfigurationVersion(provider), "普通重写不能重复推进目录 epoch");
    }

    [TestMethod]
    [DataRow("edit")]
    [DataRow("model-reference")]
    [DataRow("provider-reference")]
    public async Task ModelForm_HeldSecretReadCannotOverwriteLaterModelOrReplacement(string change)
    {
        using var source = new TestPluginContext();
        var secrets = new DeleteFailingSecrets(source.Secrets);
        using var context = new TestPluginContext { Secrets = secrets };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", HasOwnApiKey = true, MaxInputTokens = 128000 };
        var provider = new AiProvider { Models = [model] };
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        await context.Secrets.SetAsync("apikey:" + model.Id, "old-key");
        string expected = System.Text.Json.JsonSerializer.Serialize(model);
        AiModelConfig draft = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(expected)!;
        draft.Name = "unsaved draft";
        draft.MaxInputTokens = 65536;
        string draftJson = System.Text.Json.JsonSerializer.Serialize(draft);
        secrets.HoldReadName = "apikey:" + model.Id;
        Task pending = store.SaveModelFormAsync(settings, provider, model, draft, expected, "new-key", "old-key");
        try
        {
            await secrets.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "edit") { model.MaxInputTokens = 32000; model.ContextWindowIsManual = true; }
            if (change == "model-reference") provider.Models[0] = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(expected)!;
            if (change == "provider-reference") settings.Providers[0] = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(System.Text.Json.JsonSerializer.Serialize(provider))!;
        }
        finally { secrets.ReadRelease.TrySetResult(); }
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => pending);
        Assert.AreEqual(change == "edit" ? 32000 : 128000, model.MaxInputTokens);
        Assert.AreEqual(change == "edit", model.ContextWindowIsManual);
        Assert.AreEqual(draftJson, System.Text.Json.JsonSerializer.Serialize(draft));
        Assert.AreEqual("old-key", await context.Secrets.GetAsync("apikey:" + model.Id));
        Assert.AreEqual(128000, (await store.LoadAsync()).Providers[0].Models[0].MaxInputTokens);
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
    }

    [TestMethod]
    [DataRow("provider-form")]
    [DataRow("model-form")]
    [DataRow("create-key")]
    [DataRow("create-oauth")]
    [DataRow("relogin")]
    [DataRow("set")]
    [DataRow("delete")]
    [DataRow("delete-token")]
    [DataRow("add")]
    [DataRow("replace")]
    [DataRow("remove")]
    [DataRow("tokens")]
    [DataRow("clear-tokens")]
    [DataRow("delete-provider")]
    [DataRow("delete-provider-token")]
    [DataRow("delete-model")]
    [DataRow("delete-model-token")]
    public async Task SecretMutationThenThrow_RestoresRawEntryCacheAndEpochBeforeRealConsumers(string operation)
    {
        using var oldHost = new SseStub(GuardReplySse);
        using var newHost = new SseStub(GuardReplySse);
        using var source = new TestPluginContext();
        var secrets = new MutateThenFailSecrets(source.Secrets);
        using var context = new TestPluginContext { Secrets = secrets };
        var store = new AiSettingsStore(context);
        bool subscription = operation is "create-oauth" or "relogin" or "tokens" or "clear-tokens";
        var model = new AiModelConfig { Model = "old-model", HasOwnApiKey = operation is "model-form" or "delete-model" or "delete-model-token" };
        string slot = Guid.NewGuid().ToString("N");
        var provider = new AiProvider { BaseUrl = oldHost.BaseUrl, Models = [model], AdditionalApiKeyIds = [slot],
            Auth = subscription ? AuthMethod.Subscription : AuthMethod.ApiKey,
            OAuth = subscription ? new OAuthConfig { Credential = OAuthCredential.AccessToken } : null };
        var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
        await store.SaveAsync(settings);
        await context.Secrets.SetAsync("apikey:" + provider.Id, "primary-old");
        await context.Secrets.SetAsync("apikey:" + slot, "extra-old");
        await context.Secrets.SetAsync("apikey:" + model.Id, "model-old");
        var oldTokens = new OAuthTokens { AccessToken = "token-old", AccountId = "old-account", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        string oldTokenJson = System.Text.Json.JsonSerializer.Serialize(oldTokens);
        await context.Secrets.SetAsync("oauth:" + provider.Id, oldTokenJson);
        string oldModelTokenJson = System.Text.Json.JsonSerializer.Serialize(new OAuthTokens { AccessToken = "model-token-old" });
        await context.Secrets.SetAsync("oauth:" + model.Id, oldModelTokenJson);
        string settingsJson = System.Text.Json.JsonSerializer.Serialize(settings);
        string expected = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        draft.BaseUrl = newHost.BaseUrl;
        var created = new AiProvider { BaseUrl = newHost.BaseUrl, Models = [new AiModelConfig { Model = "new-model" }],
            Auth = subscription ? AuthMethod.Subscription : AuthMethod.ApiKey, OAuth = subscription ? new OAuthConfig() : null };
        var newTokens = new OAuthTokens { AccessToken = "token-new", AccountId = "new-account" };
        string modelExpected = System.Text.Json.JsonSerializer.Serialize(model);
        AiModelConfig modelDraft = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(modelExpected)!;
        modelDraft.BaseUrlOverride = newHost.BaseUrl;
        secrets.FailNextMutation = true;
        secrets.SkipMutationsBeforeFailure = operation is "delete-token" or "delete-provider-token" or "delete-model-token" ? 1 : 0;
        Task Mutation() => operation switch
        {
            "provider-form" => store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "key-new", provider.Id, "primary-old"),
            "model-form" => store.SaveModelFormAsync(settings, provider, model, modelDraft, modelExpected, "key-new", "model-old"),
            "create-key" => store.CreateProviderAsync(settings, created, "key-new"),
            "create-oauth" => store.CreateProviderAsync(settings, created, tokens: newTokens),
            "relogin" => store.SaveProviderOAuthFormAsync(settings, provider, draft, expected, newTokens),
            "set" => store.SetApiKeyAsync(provider.Id, "key-new"),
            "delete" or "delete-token" => store.DeleteApiKeyAsync(provider.Id),
            "add" => store.AddProviderApiKeyAsync(settings, provider, "key-new"),
            "replace" => store.ReplaceProviderApiKeyAsync(settings, provider, slot, "key-new"),
            "remove" => store.RemoveProviderApiKeyAsync(settings, provider, slot),
            "tokens" => store.SaveTokensAsync(provider.Id, newTokens),
            "clear-tokens" => store.ClearTokensAsync(provider.Id),
            "delete-provider" or "delete-provider-token" => store.DeleteProviderAsync(settings, provider),
            "delete-model" or "delete-model-token" => store.DeleteModelAsync(settings, provider, model),
            _ => throw new InvalidOperationException(operation)
        };
        await Assert.ThrowsAsync<IOException>(Mutation);
        Assert.IsFalse(secrets.FailNextMutation, "用例必须经过真实机密变更后才抛错");
        Assert.AreEqual(settingsJson, System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.AreEqual("primary-old", await context.Secrets.GetAsync("apikey:" + provider.Id));
        Assert.AreEqual("extra-old", await context.Secrets.GetAsync("apikey:" + slot));
        Assert.AreEqual("model-old", await context.Secrets.GetAsync("apikey:" + model.Id));
        Assert.AreEqual(oldTokenJson, await context.Secrets.GetAsync("oauth:" + provider.Id));
        Assert.AreEqual(oldModelTokenJson, await context.Secrets.GetAsync("oauth:" + model.Id));
        Assert.IsNull(await context.Secrets.GetAsync("apikey:" + created.Id));
        Assert.IsNull(await context.Secrets.GetAsync("oauth:" + created.Id));
        foreach (string name in secrets.MutationNames)
            if (name != "apikey:" + provider.Id && name != "apikey:" + model.Id
                && name != "apikey:" + slot && name != "oauth:" + provider.Id && name != "oauth:" + model.Id)
                Assert.IsNull(await context.Secrets.GetAsync(name), "新增槽或账号失败不能留下未提交机密");
        Assert.AreEqual("primary-old", await new AiSettingsStore(context).GetApiKeyAsync(provider.Id));
        Assert.AreEqual("token-old", (await new AiSettingsStore(context).GetTokensAsync(provider.Id))?.AccessToken);
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(0L, store.TokenLoginVersion(provider.Id));
        (Exception? error, _) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), settings.FindModel(model.Id)!);
        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual("Bearer " + (subscription ? "token-old" : model.HasOwnApiKey ? "model-old" : "primary-old"), oldHost.Authorizations.Single());
        Assert.IsEmpty(newHost.Requests, "写后抛错的未提交凭据不能发往新站点，也不能配对旧地址出境");
        await store.SaveAsync(settings);
        Assert.AreEqual(settingsJson, System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")));
    }

    internal sealed class MutateThenFailSecrets(VelaShell.PluginSdk.Secrets.ISecretsApi inner) : VelaShell.PluginSdk.Secrets.ISecretsApi
    {
        public bool FailNextMutation { get; set; }
        public int SkipMutationsBeforeFailure { get; set; }
        public HashSet<string> MutationNames { get; } = new(StringComparer.Ordinal);
        public int Mutations { get; private set; }
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) => inner.GetAsync(name, cancellationToken);
        public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            await inner.SetAsync(name, value, cancellationToken);
            AfterMutation(name);
        }
        public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            bool removed = await inner.DeleteAsync(name, cancellationToken);
            AfterMutation(name);
            return removed;
        }
        private void AfterMutation(string name)
        {
            ++Mutations;
            MutationNames.Add(name);
            if (!FailNextMutation) return;
            if (SkipMutationsBeforeFailure-- > 0) return;
            FailNextMutation = false;
            throw new IOException("Secret entry changed before backing-file write failed");
        }
    }

    [TestMethod]
    public async Task Review_ExplicitPrimaryProbeAllowsEmptyPrimaryWithoutFallbackOrRelaxingSavedScope()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = "https://confirmed.example", Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        await store.AddProviderApiKeyAsync(settings, provider, "must-not-fallback");
        await store.RemoveProviderApiKeyAsync(settings, provider, provider.Id);
        ResolvedModel saved = settings.FindModel(provider.Models[0].Id)!;
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialWithKeyIdAsync(saved, keyId: provider.Id));
        Assert.IsNull((await store.ResolvePrimaryCredentialForProbeAsync(saved)).Value, "显式跨源无鉴权测试不能改取补充槽");
        var external = new AiSettingsStore(context);
        AiSettings changed = await external.LoadAsync();
        changed.Providers[0].BaseUrl = "https://changed.example";
        await external.SaveAsync(changed);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolvePrimaryCredentialForProbeAsync(saved));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_IndependentSavedHostCannotReceiveNewProviderOrModelCredential(bool ownKey)
    {
        using var oldHost = new SseStub(GuardReplySse);
        using var newHost = new SseStub(GuardReplySse);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiSettings initial = await store.LoadAsync();
        var provider = new AiProvider { BaseUrl = oldHost.BaseUrl, Models = [new AiModelConfig { Model = "m", HasOwnApiKey = ownKey }] };
        await store.CreateProviderAsync(initial, provider, "old-provider-key");
        if (ownKey) await store.SetApiKeyAsync(provider.Models[0].Id, "old-model-key");
        var staleStore = new AiSettingsStore(context);
        AiSettings stale = await staleStore.LoadAsync();
        ResolvedModel old = stale.FindModel(provider.Models[0].Id)!;
        var newStore = new AiSettingsStore(context);
        AiSettings current = await newStore.LoadAsync();
        AiProvider owner = current.Providers[0];
        if (ownKey)
        {
            AiModelConfig model = owner.Models[0];
            string expected = System.Text.Json.JsonSerializer.Serialize(model);
            AiModelConfig draft = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(expected)!;
            draft.BaseUrlOverride = newHost.BaseUrl;
            await newStore.SaveModelFormAsync(current, owner, model, draft, expected, "new-model-key", "old-model-key");
        }
        else
        {
            string expected = System.Text.Json.JsonSerializer.Serialize(owner);
            AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
            draft.BaseUrl = newHost.BaseUrl;
            await newStore.SaveProviderApiKeyFormAsync(current, owner, draft, expected, "new-provider-key", owner.Id, "old-provider-key");
        }
        (Exception? error, _) = await HealthProbe.ProbeAsync(staleStore, old);
        Assert.IsInstanceOfType<AiSettingsStore.ApiKeySlotChangedException>(error);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => staleStore.ResolveProviderCredentialAsync(old.Provider));
        Assert.IsEmpty(oldHost.Requests);
        Assert.IsEmpty(newHost.Requests);
        (error, _) = await HealthProbe.ProbeAsync(newStore, current.FindModel(old.Id)!);
        Assert.IsNull(error);
        Assert.AreEqual(ownKey ? "Bearer new-model-key" : "Bearer new-provider-key", newHost.Authorizations.Single());
        Assert.IsEmpty(oldHost.Requests, "新机密不能发往旧独立快照的主机");
    }

    [TestMethod]
    public async Task Review_StandaloneHealthProbeStillWorksButSavedClonesAndUncommittedEndpointsDoNot()
    {
        using var host = new SseStub(GuardReplySse);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m" };
        var provider = new AiProvider { BaseUrl = host.BaseUrl, Models = [model] };
        await store.SetApiKeyAsync(provider.Id, "standalone-key");
        (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, model));
        Assert.IsNull(error);
        await store.SaveAsync(new AiSettings { Providers = [provider] });
        AiModelConfig clone = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(System.Text.Json.JsonSerializer.Serialize(model))!;
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialAsync(new ResolvedModel(provider, clone)));
        ResolvedModel retained = new(provider, model);
        provider.BaseUrl = "https://uncommitted.example";
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialAsync(retained));
        Assert.HasCount(1, host.Requests);
    }

    [TestMethod]
    [DataRow(false, false, "storage")]
    [DataRow(false, true, "storage")]
    [DataRow(true, true, "storage")]
    [DataRow(false, false, "cancel")]
    [DataRow(false, true, "cancel")]
    [DataRow(true, true, "cancel")]
    [DataRow(false, false, "success")]
    [DataRow(false, true, "success")]
    [DataRow(true, true, "success")]
    public async Task Review_ColdDeletionRejectsReadersAndLateRawReadsCannotPoisonRestoredCredentials(
        bool modelOnly, bool ownKey, string outcome)
    {
        using var host = new SseStub(GuardReplySse);
        var storage = new FailingStorage(new InMemoryStorage());
        using var source = new TestPluginContext();
        var secrets = new DeleteFailingSecrets(source.Secrets);
        using var context = new TestPluginContext { Storage = storage, Secrets = secrets };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", HasOwnApiKey = ownKey };
        var provider = new AiProvider { BaseUrl = host.BaseUrl, Models = [model] };
        var settings = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
        await store.SaveAsync(settings);
        string ownerId = ownKey ? model.Id : provider.Id;
        string originalKey = ownKey ? "model-key" : "provider-key";
        await context.Secrets.SetAsync("apikey:" + ownerId, originalKey);
        await context.Secrets.SetAsync("oauth:" + ownerId,
            System.Text.Json.JsonSerializer.Serialize(new OAuthTokens { AccessToken = "original-token", AccountId = "original-account" }));
        ResolvedModel retained = settings.FindModel(model.Id)!;
        long epoch = store.ProviderConfigurationVersion(provider), login = store.TokenLoginVersion(ownerId);
        using var cancellation = new CancellationTokenSource();
        storage.Hold = true;
        storage.FailHeldWrite = outcome == "storage";
        Task deletion = modelOnly ? store.DeleteModelAsync(settings, provider, model, cancellation.Token)
            : store.DeleteProviderAsync(settings, provider, cancellation.Token);
        try
        {
            await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsNull(await context.Secrets.GetAsync("apikey:" + ownerId), "JSON 尚未提交时机密已删除，缓存仍冷");
            secrets.HoldReadName = "apikey:" + ownerId;
            Task<string?> lateRawRead = new AiSettingsStore(context).GetApiKeyAsync(ownerId);
            await secrets.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsNull(await store.GetTokensAsync(ownerId), "原始令牌读取也不能缓存删除窗口中的空值");
            await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
                store.ResolveCredentialAsync(retained).WaitAsync(TimeSpan.FromSeconds(15)));
            await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
                store.ResolveProviderCredentialAsync(provider).WaitAsync(TimeSpan.FromSeconds(15)));
            await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
                store.ValidateProviderCredentialScopeAsync(provider).WaitAsync(TimeSpan.FromSeconds(15)));
            if (outcome == "cancel") cancellation.Cancel();
            storage.Release.TrySetResult();
            if (outcome == "storage") await Assert.ThrowsAsync<IOException>(() => deletion);
            else if (outcome == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => deletion);
            else await deletion.WaitAsync(TimeSpan.FromSeconds(15));
            secrets.ReadRelease.TrySetResult();
            Assert.IsNull(await lateRawRead.WaitAsync(TimeSpan.FromSeconds(15)), "原始读取返回其删除窗口快照，不得污染晚缓存");
            Assert.AreEqual(epoch + (outcome == "success" ? 1 : 0), store.ProviderConfigurationVersion(provider));
            Assert.AreEqual(login + (outcome == "success" ? 1 : 0), store.TokenLoginVersion(ownerId));
            if (outcome == "success")
            {
                await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialAsync(retained));
                Assert.IsNull(await store.GetApiKeyAsync(ownerId));
                Assert.IsNull(await store.GetTokensAsync(ownerId));
                Assert.IsEmpty(host.Requests);
            }
            else
            {
                Assert.AreEqual(originalKey, await store.GetApiKeyAsync(ownerId));
                Assert.AreEqual("original-account", (await store.GetTokensAsync(ownerId))!.AccountId);
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, retained);
                Assert.IsNull(error);
                Assert.AreEqual("Bearer " + originalKey, host.Authorizations.Single(), "失败恢复后必须真正携带原 Key 发出探活");
                await store.ValidateProviderCredentialScopeAsync(provider);
            }
        }
        finally
        {
            storage.Release.TrySetResult();
            secrets.ReadRelease.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public async Task Review_DeletionStartedDuringEitherConfirmedScopeReadIsRejected(bool modelOnly, int readNumber)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", HasOwnApiKey = modelOnly };
        var provider = new AiProvider { Models = [model] };
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        await store.SetApiKeyAsync(modelOnly ? model.Id : provider.Id, "original-key");
        storage.HoldReadNumber = readNumber;
        Task<ProviderCredential> resolving = store.ResolveCredentialAsync(settings.FindModel(model.Id)!);
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        storage.Hold = true;
        Task deletion = modelOnly ? store.DeleteModelAsync(settings, provider, model) : store.DeleteProviderAsync(settings, provider);
        try
        {
            await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            storage.ReadRelease.TrySetResult();
            await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => resolving.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            storage.ReadRelease.TrySetResult();
            storage.Release.TrySetResult();
        }
        await deletion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    [DataRow(false, "storage")]
    [DataRow(true, "storage")]
    [DataRow(false, "conflict")]
    [DataRow(true, "conflict")]
    [DataRow(false, "secret")]
    [DataRow(true, "secret")]
    public async Task Review_FailedAtomicDeletionRetainsEverySecretAndLiveIdentity(bool modelOnly, string failure)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var source = new TestPluginContext();
        var secrets = new DeleteFailingSecrets(source.Secrets);
        using var context = new TestPluginContext { Storage = storage, Secrets = secrets };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", HasOwnApiKey = true };
        var provider = new AiProvider { Models = [model] };
        var remaining = new AiProvider { Models = [new AiModelConfig { Model = "remaining" }] };
        var live = new AiSettings
        {
            Providers = [provider, remaining], ActiveModelId = model.Id, ActiveProviderId = provider.Id,
            FailoverChain = [new FailoverEntry { ModelId = model.Id }, new FailoverEntry { ModelId = remaining.Models[0].Id }]
        };
        await store.AddProviderApiKeyAsync(live, provider, "primary");
        string extra = await store.AddProviderApiKeyAsync(live, provider, "supplementary");
        await store.SetApiKeyAsync(model.Id, "model-key");
        foreach (string id in new[] { provider.Id, extra, model.Id })
            await store.SaveTokensAsync(id, new OAuthTokens { AccessToken = "token-" + id, RefreshToken = "refresh-" + id, AccountId = "account-" + id });
        string liveJson = System.Text.Json.JsonSerializer.Serialize(live);
        var originals = new Dictionary<string, string?>();
        foreach (string id in new[] { provider.Id, extra, model.Id })
            foreach (string prefix in new[] { "apikey:", "oauth:" }) originals[prefix + id] = await context.Secrets.GetAsync(prefix + id);
        long epoch = store.ProviderConfigurationVersion(provider), login = store.TokenLoginVersion(provider.Id);
        if (failure == "conflict")
        {
            var external = new AiSettingsStore(context);
            AiSettings newer = await external.LoadAsync();
            newer.PanelWidthPercent = 61;
            await external.SaveAsync(newer);
        }
        storage.Fail = failure == "storage";
        secrets.FailDeleteNumber = failure == "secret" ? modelOnly ? 2 : 3 : 0;
        Task Delete() => modelOnly ? store.DeleteModelAsync(live, provider, model) : store.DeleteProviderAsync(live, provider);
        if (failure == "conflict") await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(Delete);
        else await Assert.ThrowsAsync<IOException>(Delete);
        Assert.AreEqual(liveJson, System.Text.Json.JsonSerializer.Serialize(live));
        Assert.AreSame(provider, live.Providers[0]);
        Assert.AreSame(model, provider.Models[0]);
        Assert.AreEqual(epoch, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(login, store.TokenLoginVersion(provider.Id));
        foreach (var original in originals) Assert.AreEqual(original.Value, await context.Secrets.GetAsync(original.Key), original.Key);
        Assert.AreEqual("primary", await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual("supplementary", await store.GetApiKeyAsync(extra));
        Assert.AreEqual("model-key", await store.GetApiKeyAsync(model.Id));
        AiSettings saved = await new AiSettingsStore(context).LoadAsync();
        Assert.AreEqual(model.Id, saved.ActiveModelId);
        Assert.HasCount(2, saved.FailoverChain);
        storage.Fail = false;
        await store.ReloadIntoAsync(live);
        await Delete().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(remaining.Models[0].Id, live.ActiveModelId);
        Assert.HasCount(1, live.FailoverChain);
        Assert.IsNull(await context.Secrets.GetAsync("apikey:" + model.Id));
        Assert.IsNull(await context.Secrets.GetAsync("oauth:" + model.Id));
        Assert.AreEqual(modelOnly ? "primary" : null, await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual(modelOnly ? "supplementary" : null, await store.GetApiKeyAsync(extra));
    }

    private sealed class DeleteFailingSecrets(VelaShell.PluginSdk.Secrets.ISecretsApi inner) : VelaShell.PluginSdk.Secrets.ISecretsApi
    {
        public int FailDeleteNumber { get; set; }
        public int CancelDeleteNumber { get; set; }
        public bool FailSet { get; set; }
        public bool CancelSet { get; set; }
        public string? HoldReadName { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            string? snapshot = await inner.GetAsync(name, cancellationToken);
            if (HoldReadName == name)
            {
                HoldReadName = null;
                ReadStarted.TrySetResult();
                await ReadRelease.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }
        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
            => FailSet ? Task.FromException(new IOException("Secret set failed"))
                : CancelSet ? Task.FromException(new OperationCanceledException()) : inner.SetAsync(name, value, cancellationToken);
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
            => FailDeleteNumber > 0 && --FailDeleteNumber == 0 ? Task.FromException<bool>(new IOException("Secret delete failed"))
                : CancelDeleteNumber > 0 && --CancelDeleteNumber == 0 ? Task.FromException<bool>(new OperationCanceledException())
                : inner.DeleteAsync(name, cancellationToken);
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public async Task Review_DirectSecretMutationWaitsForAtomicDeletionAndRollback(bool modelOnly, bool fail, bool directDelete)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", HasOwnApiKey = true };
        var provider = new AiProvider { Models = [model] };
        var live = new AiSettings { Providers = [provider], ActiveModelId = model.Id };
        await store.AddProviderApiKeyAsync(live, provider, "provider-key");
        await store.SetApiKeyAsync(model.Id, "model-key");
        storage.Hold = true;
        storage.FailHeldWrite = fail;
        Task deletion = modelOnly ? store.DeleteModelAsync(live, provider, model) : store.DeleteProviderAsync(live, provider);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        string owner = modelOnly ? model.Id : provider.Id;
        var external = new AiSettingsStore(context);
        Task direct = directDelete ? external.DeleteApiKeyAsync(owner) : external.SetApiKeyAsync(owner, "external-key");
        try
        {
            Assert.IsFalse(direct.IsCompleted);
            Assert.HasCount(1, live.Providers);
            Assert.HasCount(1, provider.Models);
            Assert.AreEqual(model.Id, live.ActiveModelId);
        }
        finally { storage.Release.TrySetResult(); }
        if (fail) await Assert.ThrowsAsync<IOException>(() => deletion);
        else await deletion;
        await direct.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(directDelete ? null : "external-key", await store.GetApiKeyAsync(owner));
        Assert.AreEqual(directDelete ? null : "external-key", await context.Secrets.GetAsync("apikey:" + owner));
        Assert.AreEqual(fail ? model.Id : null, live.ActiveModelId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_FormEpochOnlyAdvancesAfterCommittedRequestChanges(bool providerForm)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m" };
        var provider = new AiProvider { BaseUrl = "https://original.example", Models = [model] };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        Task Commit(bool request)
        {
            if (providerForm)
            {
                string expected = System.Text.Json.JsonSerializer.Serialize(provider);
                AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
                draft.Name = "new name";
                draft.Models[0].InputPricePerMillion = 7;
                if (request) draft.BaseUrl = provider.BaseUrl == "https://original.example" ? "https://changed.example" : "https://original.example";
                return store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, null, provider.Id, "primary");
            }
            string modelExpected = System.Text.Json.JsonSerializer.Serialize(model);
            AiModelConfig modelDraft = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(modelExpected)!;
            modelDraft.Name = "new name";
            modelDraft.InputPricePerMillion = 7;
            if (request) modelDraft.Model = model.Model == "m" ? "changed" : "m";
            return store.SaveModelFormAsync(settings, provider, model, modelDraft, modelExpected, null, null);
        }
        await Commit(false);
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
        storage.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Commit(true));
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
        storage.Fail = false;
        await Commit(true);
        Assert.AreEqual(1L, store.ProviderConfigurationVersion(provider));
        await Commit(true);
        Assert.AreEqual(2L, store.ProviderConfigurationVersion(provider), "A→B→A不能让MCP等待前的旧请求重新有效");
        await store.SaveAsync(settings);
        Assert.AreEqual(2L, store.ProviderConfigurationVersion(provider), "表单成功后的普通保存不得重复推进 epoch");
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task Review_PureKeyAbaAdvancesEveryRegisteredOwnerOnlyOnSuccess(bool ownKey, bool form, bool cancel)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var source = new TestPluginContext();
        var secrets = new DeleteFailingSecrets(source.Secrets);
        using var context = new TestPluginContext { Storage = storage, Secrets = secrets };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", HasOwnApiKey = ownKey };
        var provider = new AiProvider { Models = [model] };
        var unrelated = new AiProvider { Models = [new AiModelConfig { Model = "other" }] };
        var settings = new AiSettings { Providers = [provider, unrelated] };
        await store.SaveAsync(settings);
        string ownerId = ownKey ? model.Id : provider.Id;
        await store.SetApiKeyAsync(ownerId, "key-a");
        var otherStore = new AiSettingsStore(context);
        AiSettings independent = await otherStore.LoadAsync();
        AiProvider registered = independent.Providers[0];
        long epoch = store.ProviderConfigurationVersion(provider), otherEpoch = otherStore.ProviderConfigurationVersion(registered);
        string originalJson = System.Text.Json.JsonSerializer.Serialize(provider);
        async Task Commit(string key, bool requestChange = false)
        {
            string? expectedKey = await store.GetApiKeyAsync(ownerId);
            if (!form) { await store.SetApiKeyAsync(ownerId, key); return; }
            if (ownKey)
            {
                string expected = System.Text.Json.JsonSerializer.Serialize(model);
                AiModelConfig draft = System.Text.Json.JsonSerializer.Deserialize<AiModelConfig>(expected)!;
                if (requestChange) draft.MaxTokens = 4000;
                await store.SaveModelFormAsync(settings, provider, model, draft, expected, key, expectedKey);
            }
            else
            {
                string expected = System.Text.Json.JsonSerializer.Serialize(provider);
                AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
                if (requestChange) draft.Models[0].MaxTokens = 4000;
                await store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, key, ownerId, expectedKey);
            }
        }
        storage.Fail = form && !cancel;
        storage.Cancel = form && cancel;
        secrets.FailSet = !form && !cancel;
        secrets.CancelSet = !form && cancel;
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => Commit("key-b"));
        else await Assert.ThrowsAsync<IOException>(() => Commit("key-b"));
        Assert.AreEqual(epoch, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(otherEpoch, otherStore.ProviderConfigurationVersion(registered));
        Assert.AreEqual("key-a", await context.Secrets.GetAsync("apikey:" + ownerId));
        storage.Fail = storage.Cancel = secrets.FailSet = secrets.CancelSet = false;
        await Commit("key-b");
        Assert.AreEqual(epoch + 1, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(otherEpoch + 1, otherStore.ProviderConfigurationVersion(registered));
        await Commit("key-b");
        Assert.AreEqual(epoch + 1, store.ProviderConfigurationVersion(provider), "相同 bytes 重写不是新凭据");
        await Commit("key-a");
        Assert.AreEqual(epoch + 2, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(otherEpoch + 2, otherStore.ProviderConfigurationVersion(registered));
        Assert.AreEqual(originalJson, System.Text.Json.JsonSerializer.Serialize(provider), "纯 Key ABA 的请求 JSON 完全相同");
        Assert.AreEqual("key-a", (await store.ResolveCredentialAsync(settings.FindModel(model.Id)!)).Value);
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(unrelated));
        if (form)
        {
            await Commit("key-c", true);
            Assert.AreEqual(epoch + 3, store.ProviderConfigurationVersion(provider), "JSON 与 Key 同笔变化只推进一次");
            Assert.AreEqual(otherEpoch + 3, otherStore.ProviderConfigurationVersion(registered));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_ReplacePrimaryAndSupplementaryKeyAbaPublishesBothRegisteredScopes(bool supplementary)
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, supplementary ? "primary" : "key-a");
        string ownerId = supplementary ? await store.AddProviderApiKeyAsync(settings, provider, "key-a") : provider.Id;
        AiProvider retained = (await new AiSettingsStore(context).LoadAsync()).Providers[0];
        long epoch = store.ProviderConfigurationVersion(provider), otherEpoch = store.ProviderConfigurationVersion(retained);
        await store.ReplaceProviderApiKeyAsync(settings, provider, ownerId, "key-b");
        await store.ReplaceProviderApiKeyAsync(settings, provider, ownerId, "key-a");
        Assert.AreEqual(epoch + 2, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(otherEpoch + 2, store.ProviderConfigurationVersion(retained));
        await store.SaveAsync(settings);
        Assert.AreEqual(epoch + 2, store.ProviderConfigurationVersion(provider));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Review_RemoveAndDirectDeleteKeyAbaCannotReauthorizeOldEpoch(bool directDelete, bool ownKey)
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "m", HasOwnApiKey = ownKey }] };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, ownKey ? "provider-key" : "key-a");
        string ownerId = ownKey ? provider.Models[0].Id : provider.Id;
        if (ownKey) await store.SetApiKeyAsync(ownerId, "key-a");
        AiProvider retained = (await new AiSettingsStore(context).LoadAsync()).Providers[0];
        long epoch = store.ProviderConfigurationVersion(provider), otherEpoch = store.ProviderConfigurationVersion(retained);
        if (directDelete) await store.DeleteApiKeyAsync(ownerId);
        else await store.RemoveProviderApiKeyAsync(settings, provider, ownerId);
        Assert.AreEqual(epoch + 1, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(otherEpoch + 1, store.ProviderConfigurationVersion(retained));
        await store.SetApiKeyAsync(ownerId, "key-a");
        Assert.AreEqual(epoch + 2, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(otherEpoch + 2, store.ProviderConfigurationVersion(retained));
        Assert.AreEqual("key-a", (await store.ResolveCredentialAsync(settings.FindModel(provider.Models[0].Id)!)).Value);
    }

    [TestMethod]
    public async Task Review_CommittedStickySlotAndBalanceKeepFixedCredentialSecurityEpochValid()
    {
        using var host = new SseStub(GuardReplySse);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = host.BaseUrl, Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "key-a");
        string selectedId = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
        ResolvedModel target = settings.FindModel(provider.Models[0].Id)!;
        var selected = await store.ResolveCredentialWithKeyIdAsync(target, keyId: selectedId);
        long epoch = store.ProviderConfigurationVersion(provider), selection = provider.ActiveApiKeySelectionEpoch;
        provider.ActiveApiKeyId = selectedId;
        provider.BalanceApiKeys = true;
        await store.SaveAsync(settings);
        Assert.AreEqual(epoch, store.ProviderConfigurationVersion(provider), "成功粘槽不能使该轮已固定凭据的正文和建议失效");
        Assert.AreNotEqual(selection, provider.ActiveApiKeySelectionEpoch, "选槽并发仍由独立代际裁决");
        var checkedCredential = await store.ResolveCredentialWithKeyIdAsync(target, keyId: selectedId);
        Assert.AreEqual(selected.Credential.Value, checkedCredential.Credential.Value);
        using IChatClient client = store.CreateClient(target, checkedCredential.Credential);
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "fixed-slot-follow-up")])) { }
        Assert.AreEqual("Bearer key-b", host.Authorizations.Single());
        Assert.Contains("fixed-slot-follow-up", host.Requests.Single());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Review_FailedOrCancelledTokenClearKeepsOriginalAccountAndPendingBodyUsable(bool directDelete, bool cancel)
    {
        using var host = new SseStub(GuardReplySse);
        using var source = new TestPluginContext();
        var secrets = new DeleteFailingSecrets(source.Secrets);
        using var context = new TestPluginContext { Secrets = secrets };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider
        {
            BaseUrl = host.BaseUrl, Auth = AuthMethod.Subscription,
            OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken, ExtraHeaders = "chatgpt-account-id: {account_id}" },
            Models = [new AiModelConfig { Model = "m" }]
        };
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        await store.SetApiKeyAsync(provider.Id, "retained-unused-key");
        var original = new OAuthTokens { AccessToken = "original-token", AccountId = "original-account" };
        await store.SaveTokensAsync(provider.Id, original);
        ResolvedModel target = settings.FindModel(provider.Models[0].Id)!;
        ProviderCredential credential = await store.ResolveCredentialAsync(target, refreshTokens: false);
        long login = store.TokenLoginVersion(provider.Id), epoch = store.ProviderConfigurationVersion(provider);
        if (cancel) secrets.CancelDeleteNumber = directDelete ? 2 : 1;
        else secrets.FailDeleteNumber = directDelete ? 2 : 1;
        Task Clear() => directDelete ? store.DeleteApiKeyAsync(provider.Id) : store.ClearTokensAsync(provider.Id);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(Clear);
        else await Assert.ThrowsAsync<IOException>(Clear);
        Assert.AreEqual(login, store.TokenLoginVersion(provider.Id));
        Assert.AreEqual(epoch, store.ProviderConfigurationVersion(provider));
        Assert.AreSame(original, await store.GetTokensAsync(provider.Id), "失败不得清除原缓存账号");
        Assert.AreEqual("retained-unused-key", await context.Secrets.GetAsync("apikey:" + provider.Id));
        Assert.AreEqual(credential.Value, (await store.ResolveCredentialAsync(target, refreshTokens: false)).Value);
        using IChatClient client = store.CreateClient(target, credential);
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "original-account-pending-body")])) { }
        Assert.AreEqual("Bearer original-token", host.Authorizations.Single());
        Assert.AreEqual("original-account", host.AccountIds.Single());
        Assert.Contains("original-account-pending-body", host.Requests.Single());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_PlainSavePublishesActualRequestEpochIncludingMaterialisedLimits(bool materialise)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", MaxTokens = 1000, MaxInputTokens = 2000 };
        ModelsDevCatalog.Apply(model, new ModelSpec(model.Model, model.Name, model.MaxInputTokens,
            model.MaxTokens, 0, 0, 0, null), newModel: true);
        var provider = new AiProvider { Models = [model] };
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        provider.Name = "显示名";
        model.Name = "模型显示名";
        model.InputPricePerMillion = 3;
        model.OutputPricePerMillion = 4;
        model.CachedInputPricePerMillion = 1;
        settings.ActiveModelId = model.Id;
        settings.PanelWidthPercent = 61;
        await store.SaveAsync(settings);
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider), "名字、价格、活跃模型和面板设置不是请求配置");
        if (materialise)
            ModelsDevCatalog.Materialise(provider, [new ModelSpec("m", "模型显示名", 8000, 4000, 3, 4, 1, false)]);
        else
        {
            model.MaxTokens = 4000;
            model.MaxInputTokens = 8000;
        }
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider), "读取版本不能把未提交草稿发布为新 epoch");
        storage.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(settings));
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
        storage.Fail = false;
        await store.SaveAsync(settings);
        Assert.AreEqual(1L, store.ProviderConfigurationVersion(provider));
        AiModelConfig saved = (await store.LoadAsync()).Providers[0].Models[0];
        Assert.AreEqual(4000, saved.MaxTokens);
        Assert.AreEqual(8000, saved.MaxInputTokens);
        await store.SaveAsync(settings);
        Assert.AreEqual(1L, store.ProviderConfigurationVersion(provider), "相同请求配置重写不应再次推进");
        model.MaxTokens = 1000;
        model.MaxInputTokens = 2000;
        await store.SaveAsync(settings);
        Assert.AreEqual(2L, store.ProviderConfigurationVersion(provider), "A→B→A 的普通保存也必须使旧回合失效");
    }

    [TestMethod]
    public async Task Review_PlainSaveEpochTracksWrittenSnapshotRatherThanPostAwaitLiveValues()
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var model = new AiModelConfig { Model = "m", MaxTokens = 1000 };
        var provider = new AiProvider { Models = [model] };
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        model.MaxTokens = 4000;
        storage.Hold = true;
        Task saving = store.SaveAsync(settings);
        try
        {
            await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            model.MaxTokens = 1000;
            Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
        }
        finally { storage.Release.TrySetResult(); }
        await saving.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(1L, store.ProviderConfigurationVersion(provider));
        Assert.AreEqual(4000, (await new AiSettingsStore(context).LoadAsync()).Providers[0].Models[0].MaxTokens);
        await store.SaveAsync(settings);
        Assert.AreEqual(2L, store.ProviderConfigurationVersion(provider), "已写 B 再写活配置 A 必须产生两次实际请求变化");
        settings.Providers.Remove(provider);
        await store.SaveAsync(settings);
        Assert.AreEqual(3L, store.ProviderConfigurationVersion(provider), "普通保存移除供应商也要推进被保留对象的 epoch");
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialAsync(new ResolvedModel(provider, model)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_CreationRejectsConcurrentLiveEditsWithoutPublishingProviderOrCredentials(bool subscription)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        AiSettings live = await store.LoadAsync();
        var draft = new AiProvider
        {
            BaseUrl = "https://custom.example", Models = [new AiModelConfig { Model = "m" }],
            Auth = subscription ? AuthMethod.Subscription : AuthMethod.ApiKey,
            OAuth = subscription ? new OAuthConfig { TokenUrl = "https://issuer.example/token" } : null
        };
        storage.Hold = true;
        Task pending = store.CreateProviderAsync(live, draft, subscription ? null : "new-key",
            subscription ? new OAuthTokens { AccessToken = "new-token" } : null);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.IsEmpty(live.Providers);
            Assert.IsNull(live.ActiveModelId);
            live.PanelWidthPercent = 63;
        }
        finally { storage.Release.TrySetResult(); }
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => pending);
        Assert.IsEmpty(live.Providers);
        Assert.IsNull(await store.GetApiKeyAsync(draft.Id));
        Assert.IsNull(await store.GetTokensAsync(draft.Id));
        AiSettings saved = await store.LoadAsync();
        Assert.IsEmpty(saved.Providers);
        Assert.AreEqual(63, saved.PanelWidthPercent);
        await store.CreateProviderAsync(live, draft, subscription ? null : "new-key",
            subscription ? new OAuthTokens { AccessToken = "new-token" } : null);
        Assert.HasCount(1, (await store.LoadAsync()).Providers);
    }

    [TestMethod]
    public async Task Review_OAuthFormFailureKeepsOldEndpointAccountAndLoginVersionUntilExplicitRetry()
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider
        {
            Name = "old account", BaseUrl = "https://old.example", Auth = AuthMethod.Subscription,
            OAuth = new OAuthConfig { TokenUrl = "https://issuer.example/token" },
            Models = [new AiModelConfig { Model = "m" }]
        };
        AiSettings live = await store.LoadAsync();
        await store.CreateProviderAsync(live, provider, tokens: new OAuthTokens { AccessToken = "old-token", AccountId = "old-account" });
        long version = store.TokenLoginVersion(provider.Id);
        string expected = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        draft.Name = "new account";
        draft.BaseUrl = "https://new.example";
        var tokens = new OAuthTokens { AccessToken = "new-token", AccountId = "new-account" };
        storage.Hold = true;
        storage.FailHeldWrite = true;
        Task pending = store.SaveProviderOAuthFormAsync(live, provider, draft, expected, tokens);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.AreEqual("https://old.example", provider.BaseUrl);
            Assert.AreEqual("old-token", (await store.GetTokensAsync(provider.Id))!.AccessToken);
            Assert.AreEqual(version, store.TokenLoginVersion(provider.Id));
        }
        finally { storage.Release.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.AreEqual("old account", provider.Name);
        Assert.AreEqual("old-account", (await store.GetTokensAsync(provider.Id))!.AccountId);
        Assert.AreEqual(version, store.TokenLoginVersion(provider.Id));
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(provider));
        storage.FailHeldWrite = false;
        await store.SaveProviderOAuthFormAsync(live, provider, draft, expected, tokens).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual("https://new.example", provider.BaseUrl);
        Assert.AreEqual("new-account", (await store.GetTokenSnapshotAsync(provider.Id, default)).Tokens!.AccountId);
        Assert.AreEqual(version + 1, store.TokenLoginVersion(provider.Id));
        Assert.AreEqual(1L, store.ProviderConfigurationVersion(provider), "订阅登录提交不能丢失已有epoch护栏");
        Assert.AreEqual("https://new.example", (await store.LoadAsync()).Providers[0].BaseUrl);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Review_DirectSecretWritesWaitForProviderCommitAndItsRollback(bool failCommit, bool delete)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = "https://original.example" };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "original-key");
        string expected = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        draft.BaseUrl = "https://draft.example";
        storage.Hold = true;
        storage.FailHeldWrite = failCommit;
        Task commit = store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "form-key", provider.Id, "original-key");
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var external = new AiSettingsStore(context);
        Task direct = delete ? external.DeleteApiKeyAsync(provider.Id) : external.SetApiKeyAsync(provider.Id, "external-key");
        try { Assert.IsFalse(direct.IsCompleted, "直接写 Key 必须排在表单事务及回滚之后"); }
        finally { storage.Release.TrySetResult(); }
        if (failCommit) await Assert.ThrowsAsync<IOException>(() => commit);
        else await commit.WaitAsync(TimeSpan.FromSeconds(15));
        await direct.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(delete ? null : "external-key", await context.Secrets.GetAsync($"apikey:{provider.Id}"));
        Assert.AreEqual(delete ? null : "external-key", await store.GetApiKeyAsync(provider.Id), "缓存必须与最终机密匹配");
        Assert.AreEqual(failCommit ? "https://original.example" : "https://draft.example", (await store.LoadAsync()).Providers[0].BaseUrl);
    }

    [TestMethod]
    public async Task Review_ReloadIntoRefreshesWholeSnapshotKeepsLiveObjectsAndRequestEpochs()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Name = "original", BaseUrl = "https://original.example", Models = [new AiModelConfig { Model = "m" }] };
        var initial = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(initial, provider, "primary");
        AiSettings live = await store.LoadAsync();
        AiProvider kept = live.Providers[0];
        AiModelConfig keptModel = kept.Models[0];
        var otherStore = new AiSettingsStore(context);
        AiSettings other = await otherStore.LoadAsync();
        other.SystemPrompt = "最新全局值";
        other.WebSearch.MaxFetchChars = 98765;
        other.PanelWidthPercent = 66;
        await otherStore.SaveAsync(other);
        await store.ReloadIntoAsync(live);
        Assert.AreEqual(0L, store.ProviderConfigurationVersion(kept), "仅全局修改不使供应商请求失效");
        Assert.AreEqual("最新全局值", live.SystemPrompt);
        Assert.AreEqual(98765, live.WebSearch.MaxFetchChars);
        other.Providers[0].BaseUrl = "https://new.example";
        other.Providers[0].Models[0].Temperature = 0.7f;
        string slot = await otherStore.AddProviderApiKeyAsync(other, other.Providers[0], "new-extra");
        await store.ReloadIntoAsync(live);
        Assert.AreSame(kept, live.Providers[0]);
        Assert.AreSame(keptModel, kept.Models[0]);
        Assert.AreEqual("https://new.example", kept.BaseUrl);
        Assert.AreEqual(0.7f, keptModel.Temperature);
        Assert.AreEqual("new-extra", await store.GetApiKeyAsync(slot));
        Assert.AreEqual(1L, store.ProviderConfigurationVersion(kept));
        other.Providers[0].BaseUrl = "https://original.example";
        await otherStore.SaveAsync(other);
        await store.ReloadIntoAsync(live);
        Assert.AreEqual(2L, store.ProviderConfigurationVersion(kept), "A→B→A 不能复活旧回合");
        live.SystemPrompt = "确认后再保存";
        await store.SaveAsync(live);
        AiSettings saved = await otherStore.LoadAsync();
        Assert.AreEqual("确认后再保存", saved.SystemPrompt);
        Assert.AreEqual(66, saved.PanelWidthPercent);
        Assert.AreEqual(98765, saved.WebSearch.MaxFetchChars);
        CollectionAssert.AreEqual(new[] { slot }, saved.Providers[0].AdditionalApiKeyIds);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_ProviderCreationFailureRollsBackSecretsAndPublishesNoGhost(bool subscription)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        AiSettings live = await store.LoadAsync();
        var draft = new AiProvider
        {
            BaseUrl = "https://custom.example", Models = [new AiModelConfig { Model = "m" }],
            Auth = subscription ? AuthMethod.Subscription : AuthMethod.ApiKey,
            OAuth = subscription ? new OAuthConfig { TokenUrl = "https://issuer.example/token" } : null
        };
        string secretName = (subscription ? "oauth:" : "apikey:") + draft.Id;
        storage.Fail = true;
        Task Create() => store.CreateProviderAsync(live, draft, subscription ? null : "creation-key",
            subscription ? new OAuthTokens { AccessToken = "creation-token" } : null);
        await Assert.ThrowsAsync<IOException>(Create);
        Assert.IsEmpty(live.Providers);
        Assert.IsNull(live.ActiveModelId);
        Assert.IsNull(await context.Secrets.GetAsync(secretName));
        Assert.IsNull(await store.GetApiKeyAsync(draft.Id));
        Assert.IsNull(await store.GetTokensAsync(draft.Id));
        Assert.AreEqual(0L, store.TokenLoginVersion(draft.Id));
        storage.Fail = false;
        await Create().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreSame(draft, live.Providers.Single());
        Assert.AreEqual(draft.Models[0].Id, live.ActiveModelId);
        AiSettings saved = await store.LoadAsync();
        Assert.AreEqual(draft.Id, saved.Providers.Single().Id);
        string json = (await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")).GetRawText();
        Assert.DoesNotContain("creation-key", json);
        Assert.DoesNotContain("creation-token", json);
        Assert.AreEqual(subscription ? 1L : 0L, store.TokenLoginVersion(draft.Id));
    }

    [TestMethod]
    public async Task Review_StaleProviderCreationDoesNotWriteAnyCredentialAndReloadAllowsExplicitRetry()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiSettings live = await store.LoadAsync();
        var otherStore = new AiSettingsStore(context);
        AiSettings other = await otherStore.LoadAsync();
        other.DisabledBuiltinTools = "terminal_write";
        await otherStore.SaveAsync(other);
        var draft = new AiProvider { Models = [new AiModelConfig { Model = "m" }] };
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.CreateProviderAsync(live, draft, "new-key"));
        Assert.IsEmpty(live.Providers);
        Assert.IsNull(await store.GetApiKeyAsync(draft.Id));
        await store.ReloadIntoAsync(live);
        await store.CreateProviderAsync(live, draft, "new-key");
        Assert.AreEqual("terminal_write", (await store.LoadAsync()).DisabledBuiltinTools);
        Assert.AreEqual("new-key", await store.GetApiKeyAsync(draft.Id));
    }

    [TestMethod]
    public async Task Review_GlobalDraftCannotChangeProvidersKeysOrNoneditableWebSettings()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        settings.WebSearch.MaxFetchChars = 4321;
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        string extra = await store.AddProviderApiKeyAsync(settings, provider, "extra");
        var draft = new AiSettings
        {
            SystemPrompt = "全局草稿",
            Providers = [new AiProvider { Id = provider.Id, BaseUrl = "https://must-not-publish.example" }],
            FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }, new FailoverEntry { ModelId = "missing" }]
        };
        draft.WebSearch.AllowedPrivateHosts = "allowed.example";
        draft.WebSearch.MaxFetchChars = 9999;
        await store.SaveGlobalSettingsAsync(settings, draft);
        Assert.AreSame(provider, settings.Providers[0]);
        Assert.AreEqual(4321, settings.WebSearch.MaxFetchChars);
        CollectionAssert.AreEqual(new[] { extra }, provider.AdditionalApiKeyIds);
        Assert.AreEqual(provider.Models[0].Id, settings.FailoverChain.Single().ModelId);
        draft.WebSearch.AllowedPrivateHosts = "";
        draft.FailoverChain.Clear();
        Assert.AreEqual("allowed.example", settings.WebSearch.AllowedPrivateHosts);
        Assert.HasCount(1, settings.FailoverChain);
        await store.SaveAsync(settings);
        AiSettings saved = await store.LoadAsync();
        Assert.AreEqual(provider.BaseUrl, saved.Providers[0].BaseUrl);
        CollectionAssert.AreEqual(new[] { extra }, saved.Providers[0].AdditionalApiKeyIds);
        Assert.AreEqual(4321, saved.WebSearch.MaxFetchChars);
        Assert.AreEqual("primary", await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual("extra", await store.GetApiKeyAsync(extra));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Review_DetachedProviderCommitRollbackAndRetryKeepBaselineUsable(int operation)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = "https://original.example" };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        string existing = await store.AddProviderApiKeyAsync(settings, provider, "extra");
        Task Commit()
        {
            string expected = System.Text.Json.JsonSerializer.Serialize(provider);
            AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
            draft.BaseUrl = "https://draft.example";
            return operation switch
            {
                0 => store.AddProviderApiKeyAsync(settings, provider, "another"),
                1 => store.RemoveProviderApiKeyAsync(settings, provider, existing),
                _ => store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "updated", provider.Id, "primary")
            };
        }
        storage.Hold = true;
        Task pending = Commit();
        await storage.Started.Task;
        settings.SystemPrompt = "并发草稿不能丢失";
        storage.Release.TrySetResult();
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => pending);
        CollectionAssert.AreEqual(new[] { existing }, provider.AdditionalApiKeyIds);
        Assert.AreEqual("primary", await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual("extra", await store.GetApiKeyAsync(existing));
        await Commit();
        settings.PanelWidthPercent = 64;
        await store.SaveAsync(settings);
        AiSettings saved = await store.LoadAsync();
        Assert.AreEqual("并发草稿不能丢失", saved.SystemPrompt);
        Assert.AreEqual(64, saved.PanelWidthPercent);
        Assert.AreEqual(operation == 2 ? "https://draft.example" : "https://original.example", saved.Providers[0].BaseUrl);
        Assert.AreEqual(operation == 2 ? "updated" : "primary", await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual(operation == 1 ? null : "extra", await store.GetApiKeyAsync(existing));
        Assert.AreEqual(operation == 0 ? 2 : operation == 1 ? 0 : 1, saved.Providers[0].AdditionalApiKeyIds.Count);
    }

    [TestMethod]
    public async Task Review_ProviderFormEmptyCredentialBaselineCannotOverwriteNewlyAddedKey()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider();
        var settings = new AiSettings { Providers = [provider] };
        await store.SaveAsync(settings);
        string expected = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        await store.SetApiKeyAsync(provider.Id, "newer-key");
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
            store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "stale-key", null, null));
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
            store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "stale-key", null, "newer-key"));
        Assert.AreEqual("newer-key", await store.GetApiKeyAsync(provider.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_IndependentLoadsCannotEraseOrReviveKeySlotsAndCanRetry(bool remove)
    {
        using var context = new TestPluginContext();
        var storeA = new AiSettingsStore(context);
        var storeB = new AiSettingsStore(context);
        var initial = new AiSettings { Providers = [new AiProvider()] };
        await storeA.AddProviderApiKeyAsync(initial, initial.Providers[0], "primary");
        string? slot = remove ? await storeA.AddProviderApiKeyAsync(initial, initial.Providers[0], "extra") : null;
        AiSettings current = await storeA.LoadAsync();
        AiSettings stale = await storeB.LoadAsync();
        current.PanelWidthPercent = 63;
        if (remove) await storeA.RemoveProviderApiKeyAsync(current, current.Providers[0], slot!);
        else slot = await storeA.AddProviderApiKeyAsync(current, current.Providers[0], "extra");
        stale.SystemPrompt = "保留未提交草稿";
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => storeB.SaveAsync(stale));
        Assert.AreEqual("保留未提交草稿", stale.SystemPrompt);
        Assert.AreEqual(remove ? 1 : 0, stale.Providers[0].AdditionalApiKeyIds.Count);
        string promptDraft = stale.SystemPrompt!;
        AiProvider kept = stale.Providers[0];
        await storeB.ReloadIntoAsync(stale);
        AiSettings saved = stale;
        Assert.AreSame(kept, saved.Providers[0]);
        Assert.AreEqual(63, saved.PanelWidthPercent);
        Assert.AreEqual(remove ? 0 : 1, saved.Providers[0].AdditionalApiKeyIds.Count);
        Assert.AreEqual(remove ? null : "extra", await storeB.GetApiKeyAsync(slot!));
        saved.SystemPrompt = promptDraft;
        await storeB.SaveAsync(saved);
        saved.SystemPrompt = "第二次正常保存";
        await storeB.SaveAsync(saved);
        await storeB.AddProviderApiKeyAsync(saved, saved.Providers[0], "retry-key");
        Assert.AreEqual("第二次正常保存", (await storeA.LoadAsync()).SystemPrompt);
    }

    [TestMethod]
    public async Task Review_IndependentGlobalSaveCannotBeOverwrittenByKeyTransaction()
    {
        using var context = new TestPluginContext();
        var storeA = new AiSettingsStore(context);
        var storeB = new AiSettingsStore(context);
        var initial = new AiSettings { Providers = [new AiProvider()] };
        await storeA.AddProviderApiKeyAsync(initial, initial.Providers[0], "primary");
        AiSettings current = await storeA.LoadAsync();
        AiSettings stale = await storeB.LoadAsync();
        current.SystemPrompt = "另一个页面的新全局设置";
        await storeA.SaveAsync(current);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
            storeB.AddProviderApiKeyAsync(stale, stale.Providers[0], "must-not-commit"));
        Assert.IsEmpty(stale.Providers[0].AdditionalApiKeyIds);
        Assert.AreEqual("另一个页面的新全局设置", (await storeB.LoadAsync()).SystemPrompt);
    }

    [TestMethod]
    public async Task Review_SettingsBaselineAllowsInitialSaveProviderAndModelEdits()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiSettings settings = await store.LoadAsync();
        settings.SystemPrompt = "首次保存";
        await store.SaveAsync(settings);
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "first" }] };
        settings.Providers.Add(provider);
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        provider.Models.Add(new AiModelConfig { Model = "second" });
        await store.SaveAsync(settings);
        provider.Models.RemoveAt(0);
        await store.SaveAsync(settings);
        string expected = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        draft.Name = "保存后的供应商";
        await store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "updated", provider.Id, "primary");
        settings.SystemPrompt = "事务后仍可保存";
        await store.SaveAsync(settings);
        AiSettings saved = await store.LoadAsync();
        Assert.AreEqual("事务后仍可保存", saved.SystemPrompt);
        Assert.AreEqual("保存后的供应商", saved.Providers[0].Name);
        Assert.AreEqual("second", saved.Providers[0].Models.Single().Model);
        Assert.AreEqual("updated", await store.GetApiKeyAsync(provider.Id));
    }

    [TestMethod]
    public async Task Review_DetachedGlobalCommitFailureRollbackAndRetryKeepBaselineUsable()
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        AiSettings settings = await store.LoadAsync();
        await store.SaveAsync(settings);
        storage.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.SaveGlobalSettingsAsync(settings, new AiSettings { SystemPrompt = "draft" }));
        Assert.IsNull(settings.SystemPrompt);
        storage.Fail = false;
        await store.SaveGlobalSettingsAsync(settings, new AiSettings { SystemPrompt = "draft" });
        Assert.AreEqual("draft", settings.SystemPrompt);
        storage.Hold = true;
        Task pending = store.SaveGlobalSettingsAsync(settings, new AiSettings { SystemPrompt = "late draft" });
        await storage.Started.Task;
        settings.PanelWidthPercent = 62;
        storage.Release.TrySetResult();
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => pending);
        Assert.AreEqual("draft", settings.SystemPrompt);
        await store.SaveGlobalSettingsAsync(settings, new AiSettings { SystemPrompt = "retry" });
        settings.PanelWidthPercent = 64;
        await store.SaveAsync(settings);
        AiSettings saved = await store.LoadAsync();
        Assert.AreEqual("retry", saved.SystemPrompt);
        Assert.AreEqual(64, saved.PanelWidthPercent);
    }

    [TestMethod]
    public async Task Review_SettingsSaveCancellationAndFailureCannotReleaseOrStrandSharedGate()
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var settings = new AiSettings { Providers = [new AiProvider()] };
        storage.Hold = true;
        Task first = store.SaveAsync(settings);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(settings, cancelled.Token));
        settings.PanelWidthPercent = 61;
        Task queued = new AiSettingsStore(context).SaveAsync(settings);
        try { Assert.IsFalse(queued.IsCompleted, "取消等待者不能释放仍属于前一笔提交的闸"); }
        finally { storage.Release.TrySetResult(); }
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(15));

        storage.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(settings));
        storage.Fail = false;
        storage.Cancel = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(settings));
        storage.Cancel = false;
        settings.Providers[0].BalanceApiKeys = true;
        await store.SaveAsync(settings).WaitAsync(TimeSpan.FromSeconds(15));
        AiSettings persisted = await store.LoadAsync();
        Assert.AreEqual(61, persisted.PanelWidthPercent);
        Assert.IsTrue(persisted.Providers[0].BalanceApiKeys);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Review_ConcurrentBalanceAndChatSavesPreserveLatestFields(bool balanceFirst, bool secondStore)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id };
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        string extra = await store.AddProviderApiKeyAsync(settings, provider, "extra");
        provider.ActiveApiKeyId = extra;
        await store.SaveAsync(settings);
        var chatStore = secondStore ? new AiSettingsStore(context) : store;
        storage.Hold = true;
        if (balanceFirst) provider.BalanceApiKeys = true;
        else settings.PanelWidthPercent = 61;
        Task first = store.SaveAsync(settings);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (balanceFirst) settings.PanelWidthPercent = 61;
        else provider.BalanceApiKeys = true;
        Task second = chatStore.SaveAsync(settings);
        storage.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        AiSettings persisted = await new AiSettingsStore(context).LoadAsync();
        Assert.IsTrue(persisted.Providers[0].BalanceApiKeys, "迟到的聊天快照不能覆盖均摊开关");
        Assert.AreEqual(61, persisted.PanelWidthPercent, "迟到的均摊快照不能覆盖聊天活字段");
        Assert.AreEqual(settings.ActiveModelId, persisted.ActiveModelId);
        Assert.AreEqual(extra, persisted.Providers[0].ActiveApiKeyId);
        CollectionAssert.AreEqual(new[] { extra }, persisted.Providers[0].AdditionalApiKeyIds);
        Assert.AreEqual("primary", await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual("extra", await store.GetApiKeyAsync(extra));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_ConcurrentSavePreservesSuccessfulProviderTransaction(bool providerForm)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = "https://original.example", Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        storage.Hold = true;
        Task older = store.SaveAsync(settings);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        string baseline = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(baseline)!;
        draft.BaseUrl = "https://updated.example";
        Task mutation = providerForm ? store.SaveProviderApiKeyFormAsync(settings, provider, draft, baseline, "updated-key", provider.Id, "primary")
            : store.AddProviderApiKeyAsync(settings, provider, "extra");
        storage.Release.TrySetResult();
        await Task.WhenAll(older, mutation).WaitAsync(TimeSpan.FromSeconds(15));

        AiProvider persisted = (await new AiSettingsStore(context).LoadAsync()).Providers[0];
        Assert.AreEqual(provider.BaseUrl, persisted.BaseUrl, "较旧的普通保存不能覆盖成功提交的地址");
        CollectionAssert.AreEqual(provider.AdditionalApiKeyIds, persisted.AdditionalApiKeyIds,
            "较旧的普通保存不能清掉已成功提交的补充槽");
        if (providerForm)
        {
            Assert.AreEqual("https://updated.example", persisted.BaseUrl);
            Assert.AreEqual("updated-key", await new AiSettingsStore(context).GetApiKeyAsync(provider.Id));
        }
        else
        {
            Assert.HasCount(1, persisted.AdditionalApiKeyIds);
            Assert.AreEqual("extra", await new AiSettingsStore(context).GetApiKeyAsync(persisted.AdditionalApiKeyIds[0]));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Review_FailedSlotMutationCannotLeaveTopologyPersistedByConcurrentSave(bool remove)
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider();
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        string existing = await store.AddProviderApiKeyAsync(settings, provider, "extra");
        storage.Hold = true;
        storage.FailHeldWrite = true;
        Task pending = remove ? store.RemoveProviderApiKeyAsync(settings, provider, existing)
            : store.AddProviderApiKeyAsync(settings, provider, "another");
        await storage.Started.Task;
        settings.SystemPrompt = "concurrent global save";
        Task concurrent = store.SaveAsync(settings);
        storage.Release.TrySetResult();
        await concurrent;
        await Assert.ThrowsAsync<IOException>(() => pending);
        AiSettings persisted = await store.LoadAsync();
        CollectionAssert.AreEqual(new[] { existing }, persisted.Providers[0].AdditionalApiKeyIds);
        Assert.AreEqual("extra", await store.GetApiKeyAsync(existing));
        Assert.AreEqual("concurrent global save", persisted.SystemPrompt);
    }

    [TestMethod]
    public async Task Review_ProviderFormCommitCannotOverwriteConcurrentGlobalSave()
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = "https://original.example", Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "original");
        string baseline = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(baseline)!;
        draft.BaseUrl = "https://draft.example";
        storage.Hold = true;
        Task pending = store.SaveProviderApiKeyFormAsync(settings, provider, draft, baseline, "draft-key", provider.Id, "original");
        await storage.Started.Task;
        settings.SystemPrompt = "concurrent global save";
        Task concurrent = store.SaveAsync(settings);
        storage.Release.TrySetResult();
        await concurrent;
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => pending);
        AiSettings persisted = await store.LoadAsync();
        Assert.AreEqual("concurrent global save", persisted.SystemPrompt);
        Assert.AreEqual("https://original.example", persisted.Providers[0].BaseUrl);
        Assert.AreEqual("original", await store.GetApiKeyAsync(provider.Id));
    }

    [TestMethod]
    public async Task ProviderApiKey_StalePanelCannotOverwriteNewlySavedSlotTopology()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var initial = new AiSettings { Providers = [new AiProvider { BaseUrl = "https://example.test/v1" }] };
        await store.AddProviderApiKeyAsync(initial, initial.Providers[0], "primary");
        AiSettings firstPanel = await store.LoadAsync();
        AiSettings secondPanel = await store.LoadAsync();
        string added = await store.AddProviderApiKeyAsync(firstPanel, firstPanel.Providers[0], "first-panel-key");
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
            store.AddProviderApiKeyAsync(secondPanel, secondPanel.Providers[0], "stale-panel-key"));
        AiSettings saved = await store.LoadAsync();
        CollectionAssert.AreEqual(new[] { added }, saved.Providers[0].AdditionalApiKeyIds);
        Assert.AreEqual("first-panel-key", await store.GetApiKeyAsync(added));
        Assert.IsEmpty(secondPanel.Providers[0].AdditionalApiKeyIds);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProviderApiKey_AmbiguousSupplementaryOwnershipCannotSendAnotherProvidersSecret(bool freshStore)
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var a = new AiProvider { BaseUrl = "https://a.example/v1" };
        var b = new AiProvider { BaseUrl = "https://b.example/v1", Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [a, b] };
        await store.AddProviderApiKeyAsync(settings, a, "primary-a");
        await store.AddProviderApiKeyAsync(settings, b, "primary-b");
        string shared = await store.AddProviderApiKeyAsync(settings, a, "must-not-leak");
        b.AdditionalApiKeyIds.Add(shared);
        b.ActiveApiKeyId = shared;
        await store.SaveAsync(settings);
        AiSettings reopened = await store.LoadAsync();
        var target = new ResolvedModel(reopened.Providers[1], reopened.Providers[1].Models[0]);
        if (freshStore) store = new AiSettingsStore(context);
        Assert.AreEqual("primary-b", (await store.ResolveCredentialAsync(target)).Value);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
            store.ResolveCredentialWithKeyIdAsync(target, keyId: shared));
    }

    [TestMethod]
    public async Task ProviderApiKey_StaleEditor_CannotOverwriteChangedCredential()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { BaseUrl = "https://example.test/v1" };
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "old");
        await store.ReplaceProviderApiKeyAsync(settings, provider, provider.Id, "newer-window");
        string expected = System.Text.Json.JsonSerializer.Serialize(provider);
        AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() =>
            store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "stale-draft",
                keyId: provider.Id, expectedKey: "old"));
        Assert.AreEqual("newer-window", await new AiSettingsStore(context).GetApiKeyAsync(provider.Id));
    }

    [TestMethod]
    public async Task ProviderApiKey_PersistenceFailure_RestoresSecretsOrderAndActive()
    {
        var storage = new FailingStorage(new InMemoryStorage());
        using var context = new TestPluginContext { Storage = storage };
        var store = new AiSettingsStore(context);
        var provider = new AiProvider();
        var settings = new AiSettings { Providers = [provider] };
        await store.AddProviderApiKeyAsync(settings, provider, "a");
        string b = await store.AddProviderApiKeyAsync(settings, provider, "b");
        string c = await store.AddProviderApiKeyAsync(settings, provider, "c");
        provider.ActiveApiKeyId = b;
        await store.SaveAsync(settings);
        storage.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.AddProviderApiKeyAsync(settings, provider, "d"));
        await Assert.ThrowsAsync<IOException>(() => store.ReplaceProviderApiKeyAsync(settings, provider, b, "new"));
        await Assert.ThrowsAsync<IOException>(() => store.RemoveProviderApiKeyAsync(settings, provider, b));
        await Assert.ThrowsAsync<IOException>(() => store.RemoveProviderApiKeyAsync(settings, provider, provider.Id));
        CollectionAssert.AreEqual(new[] { b, c }, provider.AdditionalApiKeyIds);
        Assert.AreEqual(b, provider.ActiveApiKeyId);
        Assert.AreEqual("a", await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual("b", await new AiSettingsStore(context).GetApiKeyAsync(b));
        storage.Fail = false;
        storage.Cancel = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReplaceProviderApiKeyAsync(settings, provider, b, "cancelled"));
        Assert.AreEqual("b", await store.GetApiKeyAsync(b));
        Assert.AreEqual(b, (await store.LoadAsync()).Providers[0].ActiveApiKeyId);
    }

    internal sealed class FailingStorage(IPluginStorage inner) : IPluginStorage
    {
        private int _writesCompleted;
        public int WritesCompleted => Volatile.Read(ref _writesCompleted);
        public bool Fail { get; set; }
        public bool Cancel { get; set; }
        public bool Hold { get; set; }
        public bool FailHeldWrite { get; set; }
        public int HoldReadNumber { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            T? snapshot = await inner.GetAsync<T>(key, cancellationToken);
            if (HoldReadNumber > 0 && --HoldReadNumber == 0)
            {
                ReadStarted.TrySetResult();
                await ReadRelease.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }
        public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Storage failed");
            if (Cancel) throw new OperationCanceledException();
            System.Text.Json.JsonElement snapshot = System.Text.Json.JsonSerializer.SerializeToElement(value);
            if (Hold)
            {
                Hold = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (FailHeldWrite) throw new IOException("Held storage failed");
            }
            await inner.SetAsync(key, snapshot, cancellationToken);
            Interlocked.Increment(ref _writesCompleted);
        }
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(key, cancellationToken);
        public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default)
            => inner.GetKeysAsync(cancellationToken);
    }

    [TestMethod]
    public async Task ProviderApiKey_ResolveSlots_RespectsOriginAndOwnership()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var config = new AiModelConfig { Model = "m" };
        var provider = new AiProvider { BaseUrl = "https://api.example/v1", Models = [config] };
        var other = new AiProvider();
        var settings = new AiSettings { Providers = [provider, other] };
        await store.AddProviderApiKeyAsync(settings, provider, "primary");
        string extra = await store.AddProviderApiKeyAsync(settings, provider, "additional");
        provider.ActiveApiKeyId = extra;
        await store.SaveAsync(settings);
        var model = new ResolvedModel(provider, config);
        var selected = await store.ResolveCredentialWithKeyIdAsync(model);
        Assert.AreEqual(extra, selected.KeyId);
        Assert.AreEqual("additional", selected.Credential.Value);
        await store.SetApiKeyAsync(other.Id, "must-not-leak");
        provider.AdditionalApiKeyIds.AddRange([extra, other.Id, "invalid"]);
        provider.ActiveApiKeyId = other.Id;
        await store.SaveAsync(settings);
        Assert.AreEqual("primary", (await store.ResolveCredentialAsync(model)).Value);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialWithKeyIdAsync(model, keyId: other.Id));
        config.BaseUrlOverride = "https://second.example/v1";
        model = new ResolvedModel(provider, config);
        provider.ActiveApiKeyId = extra;
        await store.SaveAsync(settings);
        selected = await store.ResolveCredentialWithKeyIdAsync(model);
        Assert.AreEqual("primary", selected.Credential.Value);
        Assert.IsNull(selected.KeyId);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialWithKeyIdAsync(model, keyId: extra));
        config.BaseUrlOverride = "https://API.example:443/other";
        await store.SaveAsync(settings);
        model = new ResolvedModel(provider, config);
        Assert.AreEqual("additional", (await store.ResolveCredentialAsync(model)).Value);
        config.HasOwnApiKey = true;
        model = new ResolvedModel(provider, config);
        await store.SetApiKeyAsync(config.Id, "model-only");
        await store.SaveAsync(settings);
        selected = await store.ResolveCredentialWithKeyIdAsync(model);
        Assert.AreEqual("model-only", selected.Credential.Value);
        Assert.IsNull(selected.KeyId);
        config.HasOwnApiKey = false;
        model = new ResolvedModel(provider, config);
        await store.SaveAsync(settings); // 将前面故意构造的重复/无效槽视为已加载 JSON,再验证删除。
        await store.RemoveProviderApiKeyAsync(settings, provider, extra);
        await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialWithKeyIdAsync(model, keyId: extra));
        await store.SetApiKeyAsync(provider.Id, "   ");
        Assert.IsNull((await store.ResolveCredentialWithKeyIdAsync(model)).KeyId);
    }

    [TestMethod]
    public async Task ProviderApiKey_ManageSlots_PreservesModelAndSeparatesSecrets()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        string modelId = provider.Models[0].Id;
        Assert.AreEqual(provider.Id, await store.AddProviderApiKeyAsync(settings, provider, " key-a "));
        string b = await store.AddProviderApiKeyAsync(settings, provider, "key-b");
        string c = await store.AddProviderApiKeyAsync(settings, provider, "key-c");
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddProviderApiKeyAsync(settings, provider, "key-b"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceProviderApiKeyAsync(settings, provider, b, " key-a "));
        await store.ReplaceProviderApiKeyAsync(settings, provider, b, "key-b-new");
        provider.ActiveApiKeyId = b;
        await store.SaveAsync(settings);
        var reopened = new AiSettingsStore(context);
        AiSettings loaded = await reopened.LoadAsync();
        Assert.HasCount(1, loaded.Providers);
        Assert.AreEqual(modelId, loaded.Providers[0].Models[0].Id);
        Assert.IsFalse(loaded.Providers[0].BalanceApiKeys);
        Assert.AreEqual(" key-a ", await reopened.GetApiKeyAsync(provider.Id));
        Assert.AreEqual("key-b-new", await reopened.GetApiKeyAsync(b));
        System.Text.Json.JsonElement json = await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings");
        Assert.DoesNotContain("key-b-new", json.GetRawText());
        await store.RemoveProviderApiKeyAsync(settings, provider, b);
        Assert.IsNull(provider.ActiveApiKeyId);
        CollectionAssert.AreEqual(new[] { c }, provider.AdditionalApiKeyIds);
        Assert.IsNull(await store.GetApiKeyAsync(b));
        await store.RemoveProviderApiKeyAsync(settings, provider, provider.Id);
        Assert.IsNull(await store.GetApiKeyAsync(provider.Id));
        Assert.AreEqual(modelId, provider.Models[0].Id);
        await store.RemoveProviderApiKeyAsync(settings, provider, c);
        Assert.AreEqual(provider.Id, await store.AddProviderApiKeyAsync(settings, provider, "replacement"));
    }

    [TestMethod]
    public async Task Load_WithoutSavedSettings_ReturnsDefaults()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);

        AiSettings settings = await store.LoadAsync();

        Assert.IsEmpty(settings.Providers);
        Assert.IsFalse(settings.AgentMode);
        Assert.IsFalse(settings.AutoApproveCommands);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsProviders()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var settings = new AiSettings
        {
            Providers =
            [
                new AiProvider
                {
                    Name = "Anthropic",
                    DefaultProtocol = ChatProtocol.AnthropicMessages,
                    BaseUrl = "https://api.anthropic.com",
                    Models =
                    [
                        new AiModelConfig { Model = "claude-opus-5", MaxTokens = 4096 },
                        new AiModelConfig { Model = "gpt-5", Protocol = ChatProtocol.OpenAiResponses, HasOwnApiKey = true }
                    ]
                }
            ],
            AgentMode = true
        };
        settings.ActiveModelId = settings.Providers[0].Models[1].Id;

        await store.SaveAsync(settings);
        AiSettings loaded = await store.LoadAsync();

        Assert.HasCount(1, loaded.Providers);
        Assert.AreEqual("Anthropic", loaded.Providers[0].Name);
        Assert.AreEqual(ChatProtocol.AnthropicMessages, loaded.Providers[0].DefaultProtocol);
        Assert.HasCount(2, loaded.Providers[0].Models);
        Assert.AreEqual(4096, loaded.Providers[0].Models[0].MaxTokens);
        Assert.IsNull(loaded.Providers[0].Models[0].Protocol, "没覆盖的协议要保持 null(继承)");
        Assert.AreEqual(ChatProtocol.OpenAiResponses, loaded.Providers[0].Models[1].Protocol);
        Assert.IsTrue(loaded.Providers[0].Models[1].HasOwnApiKey);
        Assert.AreEqual(settings.ActiveModelId, loaded.ActiveModelId);
        Assert.IsTrue(loaded.AgentMode);
    }

    // ---- 继承解析 ----

    [TestMethod]
    public void ResolvedModel_InheritsProtocolUrlAndKeyOwner_UnlessOverridden()
    {
        var provider = new AiProvider { Name = "Routin", BaseUrl = "https://routin.example/v1", DefaultProtocol = ChatProtocol.OpenAiChatCompletions };
        var inherit = new AiModelConfig { Model = "gpt-5" };
        var custom = new AiModelConfig
        {
            Model = "claude",
            Protocol = ChatProtocol.AnthropicMessages,
            HasOwnApiKey = true,
            BaseUrlOverride = "https://routin.example"
        };

        var a = new ResolvedModel(provider, inherit);
        var b = new ResolvedModel(provider, custom);

        Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, a.Protocol);
        Assert.AreEqual("https://routin.example/v1", a.BaseUrl);
        Assert.AreEqual(provider.Id, a.ApiKeyOwnerId, "没勾独立 Key 就用供应商那把");
        Assert.AreEqual("gpt-5", a.Name, "没填名称时显示模型 id");
        Assert.AreEqual(ChatProtocol.AnthropicMessages, b.Protocol);
        Assert.AreEqual("https://routin.example", b.BaseUrl);
        Assert.AreEqual(custom.Id, b.ApiKeyOwnerId);
    }

    // ---- 旧版扁平接入 → 供应商/模型两层 ----

    /// <summary>旧格式落盘时长这样:Providers 里每条自带 BaseUrl / Model / Protocol,ActiveProviderId 指向其中一条。</summary>
    private static async Task SaveLegacyAsync(TestPluginContext context, string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        await context.Storage.SetAsync("settings", doc.RootElement.Clone());
    }

    [TestMethod]
    public async Task Load_LegacyFlatProviders_GroupsByBaseUrl_AndKeepsActiveSelection()
    {
        using var context = new TestPluginContext();
        await context.Secrets.SetAsync("apikey:m1", "sk-routin");
        await context.Secrets.SetAsync("apikey:m2", "sk-routin");
        await context.Secrets.SetAsync("apikey:m3", "sk-other");
        await context.Secrets.SetAsync("apikey:m4", "sk-anthropic");
        await SaveLegacyAsync(context, """
            {
              "Providers": [
                { "Id": "m1", "Name": "GPT", "Protocol": "OpenAiResponses", "BaseUrl": "https://routin.example/v1", "Model": "gpt-5", "MaxTokens": 4096, "MaxInputTokens": 400000 },
                { "Id": "m2", "Name": "Grok", "Protocol": "OpenAiChatCompletions", "BaseUrl": "https://routin.example/v1/", "Model": "grok-4", "Reasoning": "High" },
                { "Id": "m3", "Name": "Claude", "Protocol": "AnthropicMessages", "BaseUrl": "https://routin.example", "Model": "claude-opus-5" },
                { "Id": "m4", "Name": "Claude", "Protocol": "AnthropicMessages", "BaseUrl": "https://api.anthropic.com", "Model": "claude-opus-5" }
              ],
              "ActiveProviderId": "m2",
              "AgentMode": true,
              "SystemPrompt": "keep me"
            }
            """);
        var store = new AiSettingsStore(context);

        AiSettings loaded = await store.LoadAsync();

        // 结构:同一主机的三条并成一家(忽略尾斜杠与 /v1),官方 Anthropic 单独一家
        Assert.HasCount(2, loaded.Providers);
        AiProvider routin = loaded.Providers[0];
        Assert.AreEqual("routin.example", routin.Name, "名字各不相同时用主机名");
        Assert.AreEqual("https://routin.example/v1", routin.BaseUrl);
        Assert.HasCount(3, routin.Models);
        Assert.AreEqual("m1", routin.Models[0].Id, "旧接入 id 原样成为模型 id");
        Assert.AreEqual("GPT", routin.Models[0].Name);
        Assert.AreEqual(4096, routin.Models[0].MaxTokens);
        Assert.AreEqual(400000, routin.Models[0].MaxInputTokens);
        Assert.AreEqual(ReasoningLevel.High, routin.Models[1].Reasoning);
        // 协议:两条 OpenAI 系各一票,取先出现的 Responses 为默认;其它两条各自覆盖
        Assert.AreEqual(ChatProtocol.OpenAiResponses, routin.DefaultProtocol);
        Assert.IsNull(routin.Models[0].Protocol);
        Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, routin.Models[1].Protocol);
        Assert.AreEqual(ChatProtocol.AnthropicMessages, routin.Models[2].Protocol);
        // 地址:请求打到哪儿一个字节都不变 —— 与供应商地址不完全一致的记覆盖
        Assert.IsNull(routin.Models[0].BaseUrlOverride);
        Assert.AreEqual("https://routin.example/v1/", routin.Models[1].BaseUrlOverride);
        Assert.AreEqual("https://routin.example", routin.Models[2].BaseUrlOverride);
        // Key:头一把提到供应商;同 Key 的改继承(自己那份删掉),不同的标独立
        Assert.AreEqual("sk-routin", await context.Secrets.GetAsync($"apikey:{routin.Id}"));
        Assert.IsFalse(routin.Models[0].HasOwnApiKey);
        Assert.IsFalse(routin.Models[1].HasOwnApiKey);
        Assert.IsNull(await context.Secrets.GetAsync("apikey:m2"), "改继承的模型不该留一份孤儿机密");
        Assert.IsTrue(routin.Models[2].HasOwnApiKey);
        Assert.AreEqual("sk-other", await context.Secrets.GetAsync("apikey:m3"));
        // 单条一组:名字直接用它的,模型名留空(显示模型 id)
        AiProvider anthropic = loaded.Providers[1];
        Assert.AreEqual("Claude", anthropic.Name);
        Assert.AreEqual("", anthropic.Models[0].Name);
        Assert.AreEqual("claude-opus-5", anthropic.Models[0].DisplayName);
        Assert.AreEqual("sk-anthropic", await context.Secrets.GetAsync($"apikey:{anthropic.Id}"));
        // 其余设置与当前选中不丢
        Assert.AreEqual("m2", loaded.ActiveModelId);
        Assert.IsNull(loaded.ActiveProviderId);
        Assert.AreEqual(ChatMode.Agent, loaded.Mode);
        Assert.AreEqual("keep me", loaded.SystemPrompt);
        // 解析出来的模型仍能拿到正确的 Key
        ResolvedModel resolved = loaded.FindModel("m2")!;
        Assert.AreEqual("sk-routin", await store.GetApiKeyAsync(resolved.ApiKeyOwnerId));

        // 已回写为新格式:再读一次不再走迁移,结果一致
        AiSettings again = await new AiSettingsStore(context).LoadAsync();
        Assert.HasCount(2, again.Providers);
        Assert.AreEqual(routin.Id, again.Providers[0].Id);
        Assert.AreEqual("m2", again.ActiveModelId);
        loaded.SystemPrompt = "迁移后普通保存";
        await store.SaveAsync(loaded).WaitAsync(TimeSpan.FromSeconds(15));
        string added = await store.AddProviderApiKeyAsync(loaded, routin, "migration-extra").WaitAsync(TimeSpan.FromSeconds(15));
        await store.SaveAsync(loaded).WaitAsync(TimeSpan.FromSeconds(15));
        AiSettings afterEdit = await store.LoadAsync();
        Assert.AreEqual("迁移后普通保存", afterEdit.SystemPrompt);
        CollectionAssert.AreEqual(new[] { added }, afterEdit.Providers[0].AdditionalApiKeyIds);
        Assert.AreEqual("migration-extra", await store.GetApiKeyAsync(added));
    }

    [TestMethod]
    public async Task Load_LegacyWithoutAnyProviders_IsNotTreatedAsLegacy()
    {
        using var context = new TestPluginContext();
        await SaveLegacyAsync(context, """{ "Providers": [], "Mode": "Plan" }""");

        AiSettings loaded = await new AiSettingsStore(context).LoadAsync();

        Assert.IsEmpty(loaded.Providers);
        Assert.AreEqual(ChatMode.Plan, loaded.Mode);
    }

    [TestMethod]
    public async Task ApiKey_SetGetDelete_UsesSecretStore()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);

        await store.SetApiKeyAsync("p1", "sk-secret");
        Assert.AreEqual("sk-secret", await store.GetApiKeyAsync("p1"));

        // 空值 = 清除
        await store.SetApiKeyAsync("p1", "");
        Assert.IsNull(await store.GetApiKeyAsync("p1"));
    }

    // ---- 思考档位翻译(两家协议认的东西不一样,见 ApplyReasoning) ----

    private static ResolvedModel Provider(ChatProtocol protocol, ReasoningLevel reasoning, int maxTokens = 8192)
        => new(
            new AiProvider { Name = "t", DefaultProtocol = protocol },
            new AiModelConfig { Model = "the-real-model", MaxTokens = maxTokens, Reasoning = reasoning });

    [TestMethod]
    public void ApplyReasoning_Default_LeavesTheRequestAlone()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.Default));

        Assert.IsNull(options.Reasoning, "跟随接入默认 = 请求里根本不带这个参数");
        Assert.IsNull(options.RawRepresentationFactory);
    }

    [TestMethod]
    public void ApplyReasoning_OpenAi_UsesTheStandardKnobOnly()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.OpenAiResponses, ReasoningLevel.High));

        Assert.AreEqual(ReasoningEffort.High, options.Reasoning?.Effort);
        Assert.AreEqual(ReasoningOutput.Full, options.Reasoning?.Output);
        Assert.IsNull(options.RawRepresentationFactory, "OpenAI 适配器认 ChatOptions.Reasoning,不必动请求体");
    }

    /// <summary>
    /// Anthropic 适配器不认 <c>ChatOptions.Reasoning</c>,thinking 只能经 raw 请求体下发。
    /// 同时守住实测出来的坑:raw 里的 <c>MaxTokens</c>/<c>Model</c> 会盖过适配器的值,
    /// 所以必须填真值 —— 一旦回退成占位值,线上请求就会带着错误的模型与输出上限发出去。
    /// </summary>
    [TestMethod]
    public void ApplyReasoning_Anthropic_PutsThinkingInTheRequestBody_WithRealModelAndLimit()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.Medium));

        var raw = options.RawRepresentationFactory!(new StubChatClient()) as Anthropic.Models.Messages.MessageCreateParams;
        Assert.IsNotNull(raw);
        // Model 是 ApiEnum 包装,ToString 给的是 JSON 形式("the-real-model")
        Assert.Contains("the-real-model", raw.Model.ToString());
        Assert.AreEqual(8192, raw.MaxTokens);
        var thinking = raw.Thinking?.Value as Anthropic.Models.Messages.ThinkingConfigEnabled;
        Assert.IsNotNull(thinking, "中档应当开启 thinking");
        Assert.AreEqual(4096, thinking.BudgetTokens);
        Assert.IsLessThan(raw.MaxTokens, thinking.BudgetTokens, "协议要求 max_tokens > budget_tokens");
    }

    [TestMethod]
    public void ApplyReasoning_Anthropic_Off_SendsDisabledThinking()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.Off));

        var raw = (Anthropic.Models.Messages.MessageCreateParams)options.RawRepresentationFactory!(new StubChatClient())!;
        Assert.IsInstanceOfType<Anthropic.Models.Messages.ThinkingConfigDisabled>(raw.Thinking?.Value);
    }

    /// <summary>
    /// 输出上限被设得放不下思考时,抬高这一次请求的上限,而不是悄悄不思考
    /// (预算有协议下限 1024,还得给正文留余量)。
    /// </summary>
    [TestMethod]
    public void ApplyReasoning_Anthropic_TinyOutputLimit_StillLeavesRoomForTheAnswer()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.High, maxTokens: 900));

        var raw = (Anthropic.Models.Messages.MessageCreateParams)options.RawRepresentationFactory!(new StubChatClient())!;
        var thinking = (Anthropic.Models.Messages.ThinkingConfigEnabled)raw.Thinking!.Value!;
        Assert.AreEqual(1024, thinking.BudgetTokens, "不得低于协议下限");
        Assert.AreEqual(2048, raw.MaxTokens, "上限抬到刚好放得下思考 + 正文");
    }

    /// <summary>raw 工厂只用到"当前客户端"这个参数的存在性,给个空壳即可。</summary>
    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task CreateClient_EachProtocol_BuildsChatClient()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        foreach (ChatProtocol protocol in Enum.GetValues<ChatProtocol>())
        {
            var provider = new ResolvedModel(
                new AiProvider { Name = "t", DefaultProtocol = protocol, BaseUrl = "https://example.com/v1" },
                new AiModelConfig { Model = "test-model" });

            IChatClient client = await store.CreateClientAsync(provider, "sk-test");

            Assert.IsNotNull(client, protocol.ToString());
        }
    }
}
