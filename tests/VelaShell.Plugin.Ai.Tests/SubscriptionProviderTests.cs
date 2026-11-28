using System.Reflection;
using System.Text.Json;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 供应商目录、订阅令牌的存取,以及"发请求时到底拿哪一把凭据"。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SubscriptionProviderTests
{

    // Seed and verify legacy bytes through the backing store, never through this guard.
    internal sealed class GuardedSecrets(PluginSdk.Secrets.ISecretsApi inner) : PluginSdk.Secrets.ISecretsApi
    {
        public List<string> OAuthAccesses { get; } = [];
        private void Observe(string operation, string name)
        {
            if (name.StartsWith("oauth:", StringComparison.Ordinal)) OAuthAccesses.Add($"{operation} {name}");
        }
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            Observe("get", name);
            return inner.GetAsync(name, cancellationToken);
        }
        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            Observe("set", name);
            return inner.SetAsync(name, value, cancellationToken);
        }
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            Observe("delete", name);
            return inner.DeleteAsync(name, cancellationToken);
        }
    }

    [TestMethod]
    public void Catalog_OnlyOffersTheTwoCustomApiKeyProviders()
    {
        CollectionAssert.AreEquivalent(new[] { "custom-openai", "custom-anthropic" }, ProviderCatalog.All.Select(e => e.Id).ToArray());
        Assert.IsNull(ProviderCatalog.Find("custom-oauth"));
        Assert.IsNull(ProviderCatalog.Find("openai-codex"));
        Assert.IsNull(ProviderCatalog.Find("github-copilot"));
        foreach (var entry in ProviderCatalog.All)
        {
            var draft = entry.CreateProvider();
            Assert.AreEqual(AuthMethod.ApiKey, draft.Auth);
            Assert.IsNull(draft.OAuth);
        }
    }

    [TestMethod]
    public async Task LegacyTokenEntryPointsCannotReturnOrPersistAnAccount()
    {
        using var backing = new TestPluginContext();
        var secrets = new GuardedSecrets(backing.Secrets);
        using var context = new TestPluginContext { Secrets = secrets };
        var store = new AiSettingsStore(context);
        var (provider, model) = Subscription(OAuthCredential.AccessToken);
        var tokens = new OAuthTokens { AccessToken = "legacy-token" };
        string slot = $"oauth:{provider.Id}";
        await backing.Secrets.SetAsync(slot, JsonSerializer.Serialize(tokens));
        var settings = new AiSettings { Providers = [] };
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveTokensAsync(provider.Id, tokens));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.CreateProviderAsync(settings, provider));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.CreateProviderAsync(settings, ProviderCatalog.Custom.CreateProvider(), tokens: tokens));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveProviderOAuthFormAsync(settings, provider, provider, JsonSerializer.Serialize(provider), tokens));
        Assert.IsNull(await store.GetTokensAsync(provider.Id));
        var snapshot = await store.GetTokenSnapshotAsync(provider.Id, default);
        Assert.IsNull(snapshot.Tokens);
        Assert.AreEqual(0L, snapshot.Version);
        await store.ClearTokensAsync(provider.Id);
        Assert.IsNull((await store.ResolveCredentialAsync(model)).Value);
        Assert.IsEmpty(settings.Providers);
        Assert.IsEmpty(secrets.OAuthAccesses);
        Assert.AreEqual(JsonSerializer.Serialize(tokens), await backing.Secrets.GetAsync(slot));
    }

    [TestMethod]
    public async Task KeyFormAndDeletionNeverSnapshotOrRestoreLegacyTokens()
    {
        using var backing = new TestPluginContext();
        var secrets = new GuardedSecrets(backing.Secrets);
        using var context = new TestPluginContext { Secrets = secrets };
        var store = new AiSettingsStore(context);
        AiSettings settings = await store.LoadAsync();
        AiProvider provider = ProviderCatalog.Custom.CreateProvider();
        provider.BaseUrl = "https://first.example/v1";
        string tokenSlot = $"oauth:{provider.Id}";
        await backing.Secrets.SetAsync(tokenSlot, "legacy-bytes");
        await store.CreateProviderAsync(settings, provider, "first-key");
        string expected = JsonSerializer.Serialize(provider);
        AiProvider draft = JsonSerializer.Deserialize<AiProvider>(expected)!;
        draft.BaseUrl = "https://second.example/v1";
        await store.SaveProviderApiKeyFormAsync(settings, provider, draft, expected, "new-key", provider.Id, "first-key");
        Assert.AreEqual("new-key", await store.GetApiKeyAsync(provider.Id));
        await store.DeleteProviderAsync(settings, provider);
        Assert.IsNull(await store.GetApiKeyAsync(provider.Id));
        Assert.IsEmpty(secrets.OAuthAccesses);
        Assert.AreEqual("legacy-bytes", await backing.Secrets.GetAsync(tokenSlot));
    }

    [TestMethod]
    public void Catalog_EntriesAreWellFormed()
    {
        Assert.IsNotEmpty(ProviderCatalog.All);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
        {
            Assert.IsTrue(ids.Add(entry.Id), $"目录 id 重了:{entry.Id}");
            Assert.IsNotEmpty(entry.Name, entry.Id);
            Assert.IsNotEmpty(entry.Models, $"{entry.Id} 少了示例模型这一行副标题");
            Assert.IsNotEmpty(entry.Monogram, entry.Id);
            Assert.IsLessThanOrEqualTo(2, entry.Monogram.Length, $"{entry.Id} 的字母牌只放得下两个字");

            AiProvider provider = entry.CreateProvider();
            Assert.AreEqual(entry.Id, provider.CatalogId, "造出来的供应商要认得自己是从哪一条来的");
            Assert.AreEqual(entry.Auth, provider.Auth);
            Assert.HasCount(1, provider.Models, $"{entry.Id} 该带一个起手模型");
            if (!entry.NeedsBaseUrl)
            {
                Assert.IsNotEmpty(provider.BaseUrl, $"{entry.Id} 不用自填地址,那就得自带一个");
                Assert.IsNotEmpty(provider.Models[0].Model, $"{entry.Id} 该带一个示例模型 id");
            }
        }
    }

    [TestMethod]
    public void Loc_EveryKeyCoversAllFiveLanguages()
    {
        // 少一列不会编译报错,只会在切到那门语言时抛 IndexOutOfRange —— 上个棘轮
        FieldInfo field = typeof(Loc).GetField("Table", BindingFlags.NonPublic | BindingFlags.Static)!;
        var table = (Dictionary<string, string[]>)field.GetValue(null)!;
        foreach ((string key, string[] values) in table)
        {
            Assert.HasCount(5, values, $"文案 {key} 少了几门语言");
            for (int i = 0; i < values.Length; i++)
            {
                Assert.IsNotEmpty(values[i], $"文案 {key} 的第 {i} 门语言是空的");
            }
        }
    }

    // ---- 凭据解析 ----

    private static (AiProvider Provider, ResolvedModel Model) Subscription(OAuthCredential credential)
    {
        var provider = new AiProvider
        {
            Name = "Sub",
            BaseUrl = "https://relay.example/v1",
            Auth = AuthMethod.Subscription,
            OAuth = new OAuthConfig
            {
                TokenUrl = "https://auth.example/token",
                AuthorizationUrl = "https://auth.example/authorize",
                Credential = credential
            },
            Models = [new AiModelConfig { Model = "m" }]
        };
        return (provider, new ResolvedModel(provider, provider.Models[0]));
    }

    [TestMethod]
    public async Task Credential_ApiKeyProvider_IsNotABearerToken()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Name = "Plain", Models = [new AiModelConfig { Model = "m" }] };
        await store.SetApiKeyAsync(provider.Id, "sk-plain");

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.AreEqual("sk-plain", credential.Value);
        Assert.IsFalse(credential.IsBearerToken, "手填的 Key 要按各家老规矩发(Anthropic 的 x-api-key)");
    }

    [TestMethod]
    public void Provider_LoadedFromOldSettings_StaysOnTheApiKeyPath()
    {
        // 升级上来的配置里没有 Auth / CatalogId / OAuth 这三个字段
        const string legacy = """
            {"Providers":[{"Id":"p1","Name":"OpenAI","BaseUrl":"https://api.openai.com/v1",
            "DefaultProtocol":"OpenAiResponses","Models":[{"Id":"m1","Model":"gpt-5"}]}],"ActiveModelId":"m1"}
            """;

        AiSettings settings = JsonSerializer.Deserialize<AiSettings>(legacy)!;

        Assert.AreEqual(AuthMethod.ApiKey, settings.Providers[0].Auth);
        Assert.IsNull(settings.Providers[0].OAuth);
        Assert.IsNull(settings.Providers[0].CatalogId);
        Assert.IsFalse(settings.Providers[0].CanSignIn);
    }

    [TestMethod]
    [DataRow(AuthMethod.Subscription)]
    [DataRow(AuthMethod.ApiKey)]
    public async Task Load_StripsLegacyAuthenticationWithoutReadingTokens(AuthMethod auth)
    {
        using var backing = new TestPluginContext();
        var secrets = new GuardedSecrets(backing.Secrets);
        using var context = new TestPluginContext { Secrets = secrets };
        const string providerId = "legacy-subscription";
        AiSettings settings = new()
        {
            Providers =
            [
                new AiProvider
                {
                    Id = providerId,
                    Name = "Legacy subscription",
                    BaseUrl = "https://relay.example/v1",
                    Auth = auth,
                    Models = [new AiModelConfig { Model = "m" }]
                }
            ]
        };
        var raw = JsonSerializer.SerializeToNode(settings)!;
        raw["Providers"]![0]!["OAuth"] = new System.Text.Json.Nodes.JsonObject { ["TokenUrl"] = "https://old.example/token" };
        await context.Storage.SetAsync("settings", raw.Deserialize<JsonElement>());
        await backing.Secrets.SetAsync($"oauth:{providerId}", "{not-readable-legacy-token}");

        AiSettings loaded = await new AiSettingsStore(context).LoadAsync();

        AiProvider provider = loaded.Providers.Single();
        Assert.AreEqual(AuthMethod.ApiKey, provider.Auth);
        Assert.IsNull(provider.OAuth);
        await new AiSettingsStore(context).LoadAsync();
        bool subscription = auth == AuthMethod.Subscription;
        Assert.AreEqual(subscription ? null : "{not-readable-legacy-token}", await backing.Secrets.GetAsync($"oauth:{providerId}"));
        CollectionAssert.AreEqual(subscription ? new[] { $"delete oauth:{providerId}" } : Array.Empty<string>(), secrets.OAuthAccesses);
        JsonElement persisted = await context.Storage.GetAsync<JsonElement>("settings");
        JsonElement saved = persisted.GetProperty("Providers")[0];
        Assert.AreEqual("ApiKey", saved.GetProperty("Auth").GetString());
        Assert.IsFalse(saved.TryGetProperty("OAuth", out var oauth) && oauth.ValueKind != JsonValueKind.Null);
    }
}
