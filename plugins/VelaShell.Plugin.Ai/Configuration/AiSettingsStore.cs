using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Images;
using OpenAI.Responses;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 设置读写:配置走 Storage(JSON),API Key 走 Secrets(加密)。
/// 同时充当 <see cref="IChatClient" /> 工厂:三种线协议分别由
/// OpenAI 官方 SDK(Chat Completions / Responses)与 Anthropic 官方 SDK 承载,
/// 统一到 Microsoft.Extensions.AI 抽象。
/// </summary>
public sealed class AiSettingsStore(IPluginContext context)
{
    private const string SettingsKey = "settings";
    private AiSettings? _knownSettings;
    private static readonly Dictionary<string, Uri> BuiltInOAuthOrigins = ProviderCatalog.All
        .Where(entry => entry.IsSubscription && !entry.NeedsOAuthSetup)
        .ToDictionary(entry => entry.Id, entry => new Uri(entry.CreateProvider().BaseUrl), StringComparer.OrdinalIgnoreCase);

    /// <summary>内置订阅凭据不能发送到未经该登录授权的地址;可换用其他接入,但不能原地重试。</summary>
    public sealed class BuiltinOAuthHostMismatchException() : InvalidOperationException(
        "Built-in OAuth credentials cannot be sent to another host. Use a custom provider with its own credential.");

    /// <summary>设置快照已过期或选定 Key 槽已改变；保留草稿，重新加载后才能提交，不得换凭据重放。</summary>
    public sealed class ApiKeySlotChangedException() : InvalidOperationException("The selected API Key slot changed.");

    /// <summary>表单把 Base URL 换到了另一个源,保存会清掉这家供应商已存的 Key(订阅登录的还要退出登录);须经用户确认后重新提交。</summary>
    /// <remarks>
    /// 确认的是提示时列出的那几把(以及那一份登录令牌):调用方把 <see cref="Confirmation" /> 原样交回。提示之后别处又存进一把、
    /// 同一个槽换了值、或换了一次登录,都不在确认之内,提交时会再拒绝一次,不会被一并清掉。
    /// </remarks>
    public sealed class ApiKeyResetRequiredException(IReadOnlyList<string> keyOwners, IReadOnlyList<string> confirmation,
        bool signsOut = false)
        : InvalidOperationException("The Base URL points to another origin; saved credentials must be cleared first.")
    {
        /// <summary>确认之后还要退出这家的订阅登录:旧地址签出的令牌不发往新地址,之后要在新地址重新登录。</summary>
        public bool SignsOut { get; } = signsOut;

        /// <summary>确认后会被清掉的已存 Key 数。</summary>
        public int KeyCount => KeyOwners.Count;

        /// <summary>会被清掉的已存 Key 的归属者(供应商的 Key 槽 id 或模型 id)。</summary>
        public IReadOnlyList<string> KeyOwners { get; } = keyOwners;

        /// <summary>确认凭据:要清的每一把(归属者 + 值的摘要,不含明文)。</summary>
        public IReadOnlyList<string> Confirmation { get; } = confirmation;
    }

    /// <summary>
    /// 已解出的 API Key。取一次要走"读库 + DPAPI 解包",而每发一条消息(以及每次要后续提问)
    /// 都会建一次客户端 —— 没必要每次都解。写入/删除时同步失效。
    /// </summary>
    private static readonly ConditionalWeakTable<IPluginContext, ProviderKeyStorage> ProviderKeyStores = [];
    private readonly ProviderKeyStorage _providerKeys = ProviderKeyStores.GetValue(context, static _ => new());
    private ConcurrentDictionary<string, string?> KeyCache => _providerKeys.Cache;

    private sealed class ProviderKeyStorage
    {
        public readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly ConcurrentDictionary<string, byte> Updating = new(StringComparer.Ordinal);
        public readonly ConditionalWeakTable<AiSettings, SettingsBaseline> SettingsBaselines = [];
        public readonly ConditionalWeakTable<AiProvider, SettingsScope> ProviderSettings = [];
        public readonly ConcurrentDictionary<string, OAuthTokens?> Tokens = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, long> LoginVersions = new(StringComparer.Ordinal);
        public readonly SemaphoreSlim RefreshGate = new(1, 1);
        public readonly SemaphoreSlim TokenWriteGate = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, ProviderEpoch> ProviderEpochs = new(StringComparer.Ordinal);

    private sealed class SettingsBaseline
    {
        public string? Json;
    }

    private sealed class ProviderEpoch
    {
        public long Value;
        public string? RequestJson;
        public bool Deleted;
    }
    private sealed class SettingsScope(AiSettings settings) { public AiSettings Settings = settings; }

    private void RegisterSettingsScope(AiSettings settings)
    {
        foreach (AiProvider provider in settings.Providers)
        {
            if (_providerKeys.ProviderSettings.TryGetValue(provider, out SettingsScope? scope)) scope.Settings = settings;
            else _providerKeys.ProviderSettings.Add(provider, new(settings));
        }
    }

    internal static long ProviderConfigurationVersion(AiProvider provider)
        => Volatile.Read(ref ProviderEpochs.GetOrAdd(provider.Id, static _ => new()).Value);

    private static string ProviderRequestJson(AiProvider provider) => JsonSerializer.Serialize(new
    {
        provider.BaseUrl,
        provider.DefaultProtocol,
        provider.Auth,
        provider.OAuth,
        provider.CatalogId,
        provider.AdditionalApiKeyIds,
        provider.StoreResponses,
        provider.CustomResponsesStore,
        provider.AllowSystemMessages,
        provider.UnsupportedParameters,
        Models = provider.Models.Select(model => new
        {
            model.Id,
            model.Model,
            model.Protocol,
            model.HasOwnApiKey,
            model.BaseUrlOverride,
            model.MaxTokens,
            model.MaxInputTokens,
            model.PromptCaching,
            model.Temperature,
            model.TopP,
            model.StopSequences,
            model.SystemPrompt,
            model.Reasoning,
            model.SupportsReasoning
        })
    });

    private void PublishProviderRequests(AiSettings settings, AiSettings snapshot, AiProvider? newLogin = null,
        string? changedKeyOwnerId = null)
    {
        // 弱引用登记也保留已从当前列表移除的活对象；仅比较成功发布的请求快照。
        HashSet<string> advancedProviderIds = [];
        foreach (KeyValuePair<AiProvider, SettingsScope> entry in _providerKeys.ProviderSettings)
        {
            AiProvider provider = entry.Key;
            ProviderEpoch epoch = ProviderEpochs.GetOrAdd(provider.Id, static _ => new());
            bool keyChanged = changedKeyOwnerId is not null && epoch.RequestJson is not null && !epoch.Deleted
                && ProviderOwnsApiKey(provider, changedKeyOwnerId);
            if (!ReferenceEquals(entry.Value.Settings, settings))
            {
                if (keyChanged && advancedProviderIds.Add(provider.Id)) Interlocked.Increment(ref epoch.Value);
                continue;
            }
            AiProvider? saved = snapshot.Providers.Find(candidate => candidate.Id == provider.Id);
            string? requestJson = saved is null ? null : ProviderRequestJson(saved);
            if ((epoch.RequestJson is not null || epoch.Deleted) && epoch.RequestJson != requestJson
                || ReferenceEquals(provider, newLogin) || keyChanged)
            {
                if (advancedProviderIds.Add(provider.Id)) Interlocked.Increment(ref epoch.Value);
            }

            epoch.RequestJson = requestJson;
            epoch.Deleted = saved is null;
        }
    }

    private static bool ProviderOwnsApiKey(AiProvider provider, string ownerId)
        => provider.Auth == AuthMethod.ApiKey && (provider.Id == ownerId || provider.AdditionalApiKeyIds.Contains(ownerId))
            || provider.Models.Any(model => model.HasOwnApiKey && model.Id == ownerId);

    private void PublishApiKeyChange(string ownerId)
    {
        HashSet<string> advancedProviderIds = [];
        foreach (KeyValuePair<AiProvider, SettingsScope> entry in _providerKeys.ProviderSettings)
        {
            if (ProviderOwnsApiKey(entry.Key, ownerId)
                && ProviderEpochs.TryGetValue(entry.Key.Id, out ProviderEpoch? epoch)
                && epoch.RequestJson is not null && !epoch.Deleted)
            {
                if (advancedProviderIds.Add(entry.Key.Id)) Interlocked.Increment(ref epoch.Value);
            }
        }
    }

    private List<string> MarkApiKeyUpdating(string ownerId)
    {
        List<string> ids = [ownerId];
        _providerKeys.Updating[ownerId] = 0;
        foreach (KeyValuePair<AiProvider, SettingsScope> entry in _providerKeys.ProviderSettings)
        {
            if (!ProviderOwnsApiKey(entry.Key, ownerId) || entry.Key.Id == ownerId) continue;
            ids.Add(entry.Key.Id);
            _providerKeys.Updating[entry.Key.Id] = 0;
        }
        return ids;
    }

    /// <summary>丢弃活配置中的旧快照并完整重载；保持共享设置及同 ID 的供应商/模型引用，供保留在 UI 中的草稿显式重试。</summary>
    public async Task ReloadIntoAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AiSettings fresh = await LoadSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
            await _providerKeys.TokenWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (AiProvider old in settings.Providers)
                {
                    AiProvider? saved = fresh.Providers.Find(provider => provider.Id == old.Id);
                    if (saved is not null && ProviderRequestJson(old) == ProviderRequestJson(saved)) continue;
                    _providerKeys.LoginVersions.AddOrUpdate(old.Id, 1, static (_, version) => version + 1);
                }
                PublishProviderRequests(settings, fresh);
                var providers = fresh.Providers.Select(saved =>
                {
                    AiProvider? live = settings.Providers.Find(provider => provider.Id == saved.Id);
                    if (live is null) return saved;
                    CopyProvider(live, saved);
                    return live;
                }).ToList();
                settings.Providers = providers;
                settings.ActiveModelId = fresh.ActiveModelId;
                settings.ActiveProviderId = fresh.ActiveProviderId;
                settings.Mode = fresh.Mode;
                settings.Approval = fresh.Approval;
                settings.DisabledBuiltinTools = fresh.DisabledBuiltinTools;
                settings.AgentMode = fresh.AgentMode;
                settings.AutoApproveCommands = fresh.AutoApproveCommands;
                settings.SystemPrompt = fresh.SystemPrompt;
                settings.SuggestFollowUps = fresh.SuggestFollowUps;
                settings.ImageModelId = fresh.ImageModelId;
                settings.ImageGenerationEnabled = fresh.ImageGenerationEnabled;
                settings.CompactContext = fresh.CompactContext;
                settings.PanelWidthPercent = fresh.PanelWidthPercent;
                settings.McpServers = fresh.McpServers;
                settings.WebSearch = fresh.WebSearch;
                settings.FailoverChain = fresh.FailoverChain;
                settings.AutoFailoverAcrossAccounts = fresh.AutoFailoverAcrossAccounts;
                _providerKeys.SettingsBaselines.GetValue(settings, static _ => new()).Json
                    = _providerKeys.SettingsBaselines.GetValue(fresh, static _ => new()).Json;
                _knownSettings = settings;
                RegisterSettingsScope(settings);
            }
            finally { _providerKeys.TokenWriteGate.Release(); }
        }
        finally { ProviderKeyGate.Release(); }
    }

    private static void CopyProvider(AiProvider target, AiProvider source)
    {
        target.Name = source.Name;
        target.BaseUrl = source.BaseUrl;
        target.DefaultProtocol = source.DefaultProtocol;
        target.Auth = source.Auth;
        target.AdditionalApiKeyIds = source.AdditionalApiKeyIds;
        target.ActiveApiKeyId = source.ActiveApiKeyId;
        target.BalanceApiKeys = source.BalanceApiKeys;
        target.CatalogId = source.CatalogId;
        target.OAuth = source.OAuth;
        target.AvailableModels = source.AvailableModels;
        target.ModelsExpanded = source.ModelsExpanded;
        target.StoreResponses = source.StoreResponses;
        target.CustomResponsesStore = source.CustomResponsesStore;
        target.AllowSystemMessages = source.AllowSystemMessages;
        target.UnsupportedParameters = source.UnsupportedParameters;
        target.Models = [.. source.Models.Select(saved =>
        {
            AiModelConfig? live = target.Models.Find(model => model.Id == saved.Id);
            if (live is null) return saved;
            CopyModel(live, saved);
            return live;
        })];
    }

    private static void CopyModel(AiModelConfig target, AiModelConfig source)
    {
        target.Name = source.Name;
        target.Model = source.Model;
        target.Protocol = source.Protocol;
        target.HasOwnApiKey = source.HasOwnApiKey;
        target.BaseUrlOverride = source.BaseUrlOverride;
        target.MaxTokens = source.MaxTokens;
        target.MaxInputTokens = source.MaxInputTokens;
        target.LastFetchedSpec = source.LastFetchedSpec;
        target.DefaultContextWindow = source.DefaultContextWindow;
        target.ContextWindowIsManual = source.ContextWindowIsManual;
        target.PromptCaching = source.PromptCaching;
        target.Temperature = source.Temperature;
        target.TopP = source.TopP;
        target.StopSequences = source.StopSequences;
        target.SystemPrompt = source.SystemPrompt;
        target.InputPricePerMillion = source.InputPricePerMillion;
        target.OutputPricePerMillion = source.OutputPricePerMillion;
        target.CachedInputPricePerMillion = source.CachedInputPricePerMillion;
        target.Reasoning = source.Reasoning;
        target.SupportsReasoning = source.SupportsReasoning;
    }
    /// <summary>
    /// 读取设置(不存在时返回带默认值的新实例)。旧版扁平接入列表会在这里折成两层并<b>立即回写</b>
    /// (含机密整理),之后再读就是新格式了。
    /// </summary>
    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadSettingsCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { ProviderKeyGate.Release(); }
    }

    private async Task<AiSettings> LoadSettingsCoreAsync(CancellationToken cancellationToken)
    {
        JsonElement raw = await context.Storage.GetAsync<JsonElement>(SettingsKey, cancellationToken).ConfigureAwait(false);
        AiSettings settings = raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new AiSettings() : raw.Deserialize<AiSettings>() ?? new AiSettings();
        _providerKeys.SettingsBaselines.GetValue(settings, static _ => new()).Json = SavedSettingsJson(raw);
        if (raw.ValueKind == JsonValueKind.Object && LegacySettingsMigration.IsLegacyShape(raw))
        {
            List<LegacyProviderConfig> legacy = raw.GetProperty(nameof(AiSettings.Providers))
                .Deserialize<List<LegacyProviderConfig>>() ?? [];
            List<(AiProvider Provider, List<LegacyProviderConfig> Members)> groups = LegacySettingsMigration.Group(legacy);
            await LegacySettingsMigration.MigrateSecretsAsync(groups, context.Secrets, cancellationToken).ConfigureAwait(false);
            KeyCache.Clear();
            settings.Providers = groups.ConvertAll(g => g.Provider);
            settings.Migrate();
            await SaveSettingsCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            context.Log.Info($"AI settings: migrated {legacy.Count} legacy provider entries into {settings.Providers.Count} provider(s).");
        }
        RegisterSettingsScope(settings);
        PublishProviderRequests(settings, settings);
        _knownSettings = settings;
        return settings;
    }

    /// <summary>在共享写闸内提交新供应商及机密；落盘成功前不发布供应商或活跃模型。</summary>
    public Task CreateProviderAsync(AiSettings settings, AiProvider draft, string? apiKey = null,
        OAuthTokens? tokens = null, CancellationToken cancellationToken = default)
        => CommitProviderConfigurationAsync(settings, null, draft, null, apiKey, tokens, cancellationToken);

    /// <summary>原子提交订阅供应商的登录配置与新账号令牌，晚到的旧账号刷新不能覆盖新登录。</summary>
    /// <remarks>
    /// 地址换到了另一个源:新令牌本来就是新地址签的,但模型自带、跟着供应商地址走的 Key 属于旧地址,
    /// 要经 <paramref name="confirmedKeyReset" /> 确认后一并清掉(见 <see cref="SaveProviderFormAsync" />)。
    /// 目录页对配齐了的订阅行不摆地址框,重新登录走不到这一步;这里是保底:没确认就拒绝,不悄悄留着旧 Key。
    /// </remarks>
    /// <returns>清掉的已存 Key 的归属者。</returns>
    public async Task<IReadOnlyList<string>> SaveProviderOAuthFormAsync(AiSettings settings, AiProvider provider, AiProvider draft,
        string expectedProviderJson, OAuthTokens tokens, IReadOnlyCollection<string>? confirmedKeyReset = null,
        CancellationToken cancellationToken = default)
        => (await CommitProviderConfigurationAsync(settings, provider, draft, expectedProviderJson, null, tokens,
            cancellationToken, confirmedKeyReset).ConfigureAwait(false)).Cleared;

    /// <summary>提交供应商表单(名称、地址、协议);设置页给订阅登录的供应商用。</summary>
    /// <remarks>
    /// Base URL 换到另一个源时,旧地址签出的登录令牌不能跟着发过去:退出登录(删令牌、推进登录代次,
    /// 在途的刷新与请求复验时都认得出来),模型自带、跟着供应商地址走的 Key 一并清掉。
    /// 有东西要清就默认拒绝、抛 <see cref="ApiKeyResetRequiredException" />(<see cref="ApiKeyResetRequiredException.SignsOut" />
    /// 说明会不会退出登录),调用方让用户确认后把确认凭据经 <paramref name="confirmedKeyReset" /> 交回。
    /// </remarks>
    /// <returns>清掉的已存 Key 的归属者,以及这次保存有没有退出登录。</returns>
    internal async Task<(IReadOnlyList<string> Cleared, bool SignedOut)> SaveProviderFormAsync(AiSettings settings,
        AiProvider provider, AiProvider draft, string expectedProviderJson,
        IReadOnlyCollection<string>? confirmedKeyReset = null, CancellationToken cancellationToken = default)
        => await CommitProviderConfigurationAsync(settings, provider, draft, expectedProviderJson, null, null,
            cancellationToken, confirmedKeyReset).ConfigureAwait(false);

    /// <summary>仅保存分离的目录合并稿；JSON 落盘前不发布型号、规格、来源或请求代次，也不改写机密。</summary>
    internal async Task SaveProviderCatalogueAsync(AiSettings settings, AiProvider provider, AiProvider draft,
        string expectedProviderJson, CancellationToken cancellationToken = default)
    {
        AiProvider frozen = JsonSerializer.Deserialize<AiProvider>(JsonSerializer.Serialize(draft))!;
        AiModelConfig[] models = [.. provider.Models];
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool settingsWritten = false;
        string? savedJson = null;
        try
        {
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!settings.Providers.Contains(provider) || provider.Id != frozen.Id
                || !provider.Models.SequenceEqual(models) || JsonSerializer.Serialize(provider) != expectedProviderJson)
            {
                throw new ApiKeySlotChangedException();
            }

            savedJson = _providerKeys.SettingsBaselines.GetValue(settings, static _ => new()).Json;
            AiProvider[] owners = [.. settings.Providers];
            string liveJson = JsonSerializer.Serialize(settings);
            AiSettings detached = JsonSerializer.Deserialize<AiSettings>(liveJson)!;
            detached.Providers[settings.Providers.IndexOf(provider)] = frozen;
            detached.FailoverChain.RemoveAll(entry => detached.FindModel(entry.ModelId) is null);
            if (detached.FindModel(detached.ActiveModelId) is null)
                detached.ActiveModelId = frozen.Models.FirstOrDefault()?.Id ?? detached.ResolveModels().FirstOrDefault()?.Id;
            _providerKeys.Updating[provider.Id] = 0;
            await WriteSettingsSnapshotAsync(settings, JsonSerializer.Serialize(detached), cancellationToken).ConfigureAwait(false);
            settingsWritten = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!settings.Providers.SequenceEqual(owners) || !provider.Models.SequenceEqual(models)
                || JsonSerializer.Serialize(settings) != liveJson)
            {
                throw new ApiKeySlotChangedException();
            }

            CopyProvider(provider, frozen);
            settings.ActiveModelId = detached.ActiveModelId;
            settings.FailoverChain = detached.FailoverChain;
            PublishProviderRequests(settings, detached);
        }
        catch
        {
            // 冲突不把等待期间的未保存活字段顺便落盘，也不发布失败目录的请求代次。
            if (settingsWritten)
            {
                if (savedJson is null)
                {
                    await context.Storage.RemoveAsync(SettingsKey, CancellationToken.None).ConfigureAwait(false);
                    _providerKeys.SettingsBaselines.GetValue(settings, static _ => new()).Json = null;
                }
                else
                {
                    await WriteSettingsSnapshotAsync(settings, savedJson, CancellationToken.None).ConfigureAwait(false);
                }
            }
            throw;
        }
        finally
        {
            _providerKeys.Updating.TryRemove(provider.Id, out _);
            ProviderKeyGate.Release();
        }
    }

    private async Task<(IReadOnlyList<string> Cleared, bool SignedOut)> CommitProviderConfigurationAsync(AiSettings settings,
        AiProvider? provider, AiProvider draft, string? expectedProviderJson, string? apiKey, OAuthTokens? tokens,
        CancellationToken cancellationToken, IReadOnlyCollection<string>? confirmedKeyReset = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(draft);
        AiProvider frozen = JsonSerializer.Deserialize<AiProvider>(JsonSerializer.Serialize(draft))!;
        OAuthTokens? frozenTokens = tokens is null ? null : JsonSerializer.Deserialize<OAuthTokens>(JsonSerializer.Serialize(tokens));
        if ((apiKey is not null && frozen.Auth != AuthMethod.ApiKey)
            || (frozenTokens is not null && (frozen.Auth != AuthMethod.Subscription || frozen.OAuth is null)))
        {
            throw new ArgumentException("Credentials do not match the provider authentication.");
        }

        if (frozenTokens is not null)
        {
            EnsureBuiltinOAuthCredentialDestination(new ResolvedModel(frozen, new AiModelConfig()),
                new ProviderCredential(frozenTokens.AccessToken, frozen.OAuth!.Credential != OAuthCredential.ApiKey,
                    null, frozenTokens.BaseUrl));
        }

        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool tokenGateHeld = false, keyWritten = false, tokenWritten = false, settingsWritten = false, resetWritten = false;
        string? oldKey = null, oldTokenJson = null;
        OAuthTokens? oldTokens = null;
        // 换源时动过的机密:归属者 → 原值,失败时逐个恢复
        Dictionary<string, string?> resetKeys = [with(StringComparer.Ordinal)];
        List<(string Id, string Key)> cleared = [];
        bool signOut = false;
        try
        {
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            if (provider is not null && (!settings.Providers.Contains(provider) || provider.Id != frozen.Id
                || JsonSerializer.Serialize(provider) != expectedProviderJson))
            {
                throw new ApiKeySlotChangedException();
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (AiProvider owner in settings.Providers.Where(owner => !ReferenceEquals(owner, provider)))
            {
                ids.Add(owner.Id);
                ids.UnionWith(owner.Models.Select(model => model.Id));
                ids.UnionWith(owner.AdditionalApiKeyIds);
            }
            if (!ids.Add(frozen.Id) || frozen.Models.Any(model => !ids.Add(model.Id))
                || frozen.AdditionalApiKeyIds.Any(id => !Guid.TryParseExact(id, "N", out _) || !ids.Add(id)))
            {
                throw new ApiKeySlotChangedException();
            }

            string liveJson = JsonSerializer.Serialize(settings);
            AiSettings detached = JsonSerializer.Deserialize<AiSettings>(liveJson)!;
            if (provider is null)
            {
                detached.Providers.Add(frozen);
                detached.ActiveModelId ??= frozen.Models.FirstOrDefault()?.Id;
            }
            else
            {
                detached.Providers[settings.Providers.IndexOf(provider)] = frozen;
            }

            // 地址换到别的源:跟着供应商地址走的已存 Key(主槽、补充槽、没填覆盖地址的模型自带 Key)属于旧地址;
            // 订阅登录又没换来新令牌的(设置页只改了表单),旧地址签出的令牌也不能跟去 —— 退出登录。
            // 先读、先核确认,一样都还没动;确认过的才清,失败逐个恢复
            bool originChanged = provider is not null && MovesOrigin(provider.BaseUrl, frozen.BaseUrl);
            signOut = originChanged && provider!.Auth == AuthMethod.Subscription && frozenTokens is null;
            List<string> resetOwners = originChanged ? [.. KeysBoundToProviderOrigin(provider!)] : [];
            foreach (string id in resetOwners)
            {
                string? saved = await context.Secrets.GetAsync(SecretName(id), cancellationToken).ConfigureAwait(false);
                resetKeys[id] = saved;
                if (!string.IsNullOrWhiteSpace(saved)) cleared.Add((id, saved));
            }
            if (frozenTokens is not null || signOut)
            {
                // 全部嵌套提交遵循 KeyGate → TokenWriteGate；刷新只使用 TokenWriteGate。
                await _providerKeys.TokenWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                tokenGateHeld = true;
                oldTokenJson = await context.Secrets.GetAsync(TokenSecretName(frozen.Id), cancellationToken).ConfigureAwait(false);
                oldTokens = await GetTokensAsync(frozen.Id, cancellationToken).ConfigureAwait(false);
            }
            signOut &= !string.IsNullOrEmpty(oldTokenJson); // 本来就没登录:没有令牌可退
            RequireKeyResetConfirmed(cleared, confirmedKeyReset, signOut ? LoginResetToken(frozen.Id, oldTokenJson!) : null);

            _providerKeys.Updating[frozen.Id] = 0;
            foreach (string id in resetOwners) _providerKeys.Updating[id] = 0;
            if (frozenTokens is not null)
            {
                tokenWritten = true;
                await context.Secrets.SetAsync(TokenSecretName(frozen.Id), JsonSerializer.Serialize(frozenTokens), cancellationToken).ConfigureAwait(false);
            }
            else if (signOut)
            {
                // 缓存跟着机密一起清:提交期间别处(凭据解析先看 Updating 闸)不会拿到旧令牌;失败在 catch 里恢复
                tokenWritten = true;
                await context.Secrets.DeleteAsync(TokenSecretName(frozen.Id), cancellationToken).ConfigureAwait(false);
                TokenCache[frozen.Id] = null;
            }
            if (resetOwners.Count > 0)
            {
                resetWritten = true;
                foreach (string id in resetOwners) await SetApiKeyCoreAsync(id, null, cancellationToken).ConfigureAwait(false);
            }
            if (apiKey is not null)
            {
                oldKey = await context.Secrets.GetAsync(SecretName(frozen.Id), cancellationToken).ConfigureAwait(false);
                keyWritten = true;
                await SetApiKeyCoreAsync(frozen.Id, apiKey, cancellationToken).ConfigureAwait(false);
            }
            await WriteSettingsSnapshotAsync(settings, JsonSerializer.Serialize(detached), cancellationToken).ConfigureAwait(false);
            settingsWritten = true;
            if (JsonSerializer.Serialize(settings) != liveJson) throw new ApiKeySlotChangedException();
            CopyProvider(provider ?? draft, frozen);
            if (provider is null) settings.Providers.Add(draft);
            RegisterSettingsScope(settings);
            settings.ActiveModelId = detached.ActiveModelId;
            if (frozenTokens is not null)
            {
                TokenCache[frozen.Id] = frozenTokens;
                _providerKeys.LoginVersions.AddOrUpdate(frozen.Id, 1, static (_, version) => version + 1);
            }
            else if (signOut)
            {
                // 推进登录代次:在途的令牌刷新回来对不上号就不写回,复验旧令牌的请求也会被拒(与退出登录同一条路)
                _providerKeys.LoginVersions.AddOrUpdate(frozen.Id, 1, static (_, version) => version + 1);
            }
            PublishProviderRequests(settings, detached, frozenTokens is not null || signOut ? provider : null,
                keyWritten && oldKey != (string.IsNullOrEmpty(apiKey) ? null : apiKey) ? frozen.Id : null);
            // 清掉的模型 Key:别处设置里的同一供应商副本也一并作废
            foreach ((string id, _) in cleared) PublishApiKeyChange(id);
            return ([.. cleared.Select(item => item.Id)], signOut);
        }
        catch
        {
            if (resetWritten)
            {
                foreach ((string id, string? saved) in resetKeys) await RestoreApiKeyCoreAsync(id, saved).ConfigureAwait(false);
            }
            if (keyWritten) await RestoreApiKeyCoreAsync(frozen.Id, oldKey).ConfigureAwait(false);
            if (tokenWritten)
            {
                TokenCache[frozen.Id] = oldTokens;
                if (oldTokenJson is null) await context.Secrets.DeleteAsync(TokenSecretName(frozen.Id), CancellationToken.None).ConfigureAwait(false);
                else await context.Secrets.SetAsync(TokenSecretName(frozen.Id), oldTokenJson, CancellationToken.None).ConfigureAwait(false);
            }
            if (settingsWritten) await SaveSettingsCoreAsync(settings, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (tokenGateHeld) _providerKeys.TokenWriteGate.Release();
            _providerKeys.Updating.TryRemove(frozen.Id, out _);
            foreach (string id in resetKeys.Keys) _providerKeys.Updating.TryRemove(id, out _);
            ProviderKeyGate.Release();
        }
    }

    /// <summary>提交模型表单与这个模型自带的 Key(<paramref name="apiKey" /> 为空即删掉)。</summary>
    /// <remarks>
    /// 覆盖基地址把这个模型实际去的地址(没填就是供应商的地址)换到另一个源时,已存的自带 Key 是给旧地址的,
    /// 与供应商那一层换源(<see cref="SaveProviderApiKeyFormAsync" />)同一个口径:提交里原样带着那把旧 Key
    /// (表单预填的没动过)就默认拒绝、抛 <see cref="ApiKeyResetRequiredException" />,调用方让用户确认后把确认凭据经
    /// <paramref name="confirmedKeyReset" /> 交回,这次提交连同地址把那把 Key 清掉。同一次提交里换了一把新 Key,
    /// 那把是给新地址的,照存不问;只改路径(同源)不动 Key。
    /// </remarks>
    /// <returns>清掉的已存 Key 的归属者(这个模型的 id;没清就是空)。</returns>
    internal async Task<IReadOnlyList<string>> SaveModelFormAsync(AiSettings settings, AiProvider provider, AiModelConfig model,
        AiModelConfig draft, string expectedModelJson, string? apiKey, string? expectedKey,
        IReadOnlyCollection<string>? confirmedKeyReset = null, CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool keyWritten = false, settingsWritten = false;
        string? oldKey = null;
        try
        {
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!settings.Providers.Contains(provider) || !provider.Models.Contains(model)
                || model.Id != draft.Id || JsonSerializer.Serialize(model) != expectedModelJson)
            {
                throw new ApiKeySlotChangedException();
            }

            oldKey = await context.Secrets.GetAsync(SecretName(model.Id), cancellationToken).ConfigureAwait(false);
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!settings.Providers.Contains(provider) || !provider.Models.Contains(model)
                || model.Id != draft.Id || JsonSerializer.Serialize(model) != expectedModelJson)
            {
                throw new ApiKeySlotChangedException();
            }

            draft = JsonSerializer.Deserialize<AiModelConfig>(JsonSerializer.Serialize(draft))!;
            if (oldKey != expectedKey) throw new ApiKeySlotChangedException();
            // 这个模型实际去的地址换了源,而已存的那把自带 Key 原样留着:它会被发往新地址。先问,确认过才清
            // (按归属者与值核对:框开着期间别处换了这把 Key,就对不上、再问一次)
            List<(string Id, string Key)> cleared = [];
            if (!string.IsNullOrWhiteSpace(oldKey) && apiKey == oldKey
                && MovesOrigin(new ResolvedModel(provider, model).BaseUrl, new ResolvedModel(provider, draft).BaseUrl))
            {
                cleared.Add((model.Id, oldKey));
            }
            RequireKeyResetConfirmed(cleared, confirmedKeyReset);
            if (cleared.Count > 0) apiKey = null;
            if (draft.MaxInputTokens != model.MaxInputTokens)
            {
                draft.DefaultContextWindow = false;
                draft.ContextWindowIsManual = true;
            }
            string liveJson = JsonSerializer.Serialize(settings);
            AiSettings detached = JsonSerializer.Deserialize<AiSettings>(liveJson)!;
            detached.Providers[settings.Providers.IndexOf(provider)].Models[provider.Models.IndexOf(model)] = draft;
            // 旧活模型与缓存凭据在存储等待期间保持配对；JSON 成功后一起发布。
            _providerKeys.Updating[provider.Id] = 0;
            _providerKeys.Updating[model.Id] = 0;
            keyWritten = true;
            if (string.IsNullOrEmpty(apiKey)) await context.Secrets.DeleteAsync(SecretName(model.Id), cancellationToken).ConfigureAwait(false);
            else await context.Secrets.SetAsync(SecretName(model.Id), apiKey, cancellationToken).ConfigureAwait(false);
            await WriteSettingsSnapshotAsync(settings, JsonSerializer.Serialize(detached), cancellationToken).ConfigureAwait(false);
            settingsWritten = true;
            if (JsonSerializer.Serialize(settings) != liveJson) throw new ApiKeySlotChangedException();
            if (!settings.Providers.Contains(provider) || !provider.Models.Contains(model))
                throw new ApiKeySlotChangedException();
            CopyModel(model, draft);
            KeyCache[model.Id] = string.IsNullOrEmpty(apiKey) ? null : apiKey;
            PublishProviderRequests(settings, detached, changedKeyOwnerId:
                oldKey != (string.IsNullOrEmpty(apiKey) ? null : apiKey) ? model.Id : null);
            return [.. cleared.Select(item => item.Id)];
        }
        catch
        {
            if (keyWritten) await RestoreApiKeyCoreAsync(model.Id, oldKey).ConfigureAwait(false);
            if (settingsWritten) await SaveSettingsCoreAsync(settings, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _providerKeys.Updating.TryRemove(model.Id, out _);
            _providerKeys.Updating.TryRemove(provider.Id, out _);
            ProviderKeyGate.Release();
        }
    }

    /// <summary>与 Key 事务共用写闸；仅允许 Load/前次成功写入的基线仍匹配落盘快照时提交。</summary>
    public async Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken);
        try { await SaveSettingsCoreAsync(settings, cancellationToken); }
        finally { ProviderKeyGate.Release(); }
    }

    // 仅供已持有共享写闸的普通提交与 Key 事务恢复调用，不能再次进入 SaveAsync。
    private async Task SaveSettingsCoreAsync(AiSettings settings, CancellationToken cancellationToken,
        string? changedKeyOwnerId = null)
    {
        await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(settings);
        AiSettings snapshot = JsonSerializer.Deserialize<AiSettings>(json)!;
        await WriteSettingsSnapshotAsync(settings, json, cancellationToken).ConfigureAwait(false);
        PublishProviderRequests(settings, snapshot, changedKeyOwnerId: changedKeyOwnerId);
    }

    private static string? SavedSettingsJson(JsonElement raw)
        => raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : JsonSerializer.Serialize(raw);

    private async Task ValidateSettingsBaselineAsync(AiSettings settings, CancellationToken cancellationToken)
    {
        JsonElement saved = await context.Storage.GetAsync<JsonElement>(SettingsKey, cancellationToken).ConfigureAwait(false);
        string? baseline = _providerKeys.SettingsBaselines.GetValue(settings, static _ => new()).Json;
        if (SavedSettingsJson(saved) != baseline) throw new ApiKeySlotChangedException();
    }

    private async Task WriteSettingsSnapshotAsync(AiSettings settings, string json, CancellationToken cancellationToken)
    {
        // 写入不可变快照，基线必须对应实际落盘值，而不是 await 后可能已经变化的活对象。
        // 基线也要取 JsonElement 重新序列化后的文本:比对时读回来的就是这一种。DateTimeOffset 在对象序列化里
        // 原样写成 "+08:00",经 JsonElement 再写一遍会按默认编码器转成 "\u002B08:00" —— 直接拿 json 当基线,
        // 东八区下 MCP 刷过工具清单之后,保存一次就再也对不上,之后的保存与发送全被当成配置已变而拒绝。
        JsonElement snapshot = JsonSerializer.Deserialize<JsonElement>(json);
        await context.Storage.SetAsync(SettingsKey, snapshot, cancellationToken).ConfigureAwait(false);
        _providerKeys.SettingsBaselines.GetValue(settings, static _ => new()).Json = SavedSettingsJson(snapshot);
        _knownSettings = settings;
        RegisterSettingsScope(settings);
    }

    /// <summary>仅提交全局页的五组字段，不接纳草稿中的供应商、Key 拓扑或 OAuth 修改。</summary>
    internal async Task SaveGlobalSettingsAsync(AiSettings settings, AiSettings draft,
        CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            string liveJson = JsonSerializer.Serialize(settings);
            AiSettings detached = JsonSerializer.Deserialize<AiSettings>(liveJson)!;
            ApplyGlobalSettings(detached, draft);
            await WriteSettingsSnapshotAsync(settings, JsonSerializer.Serialize(detached), cancellationToken).ConfigureAwait(false);
            if (JsonSerializer.Serialize(settings) != liveJson)
            {
                await SaveSettingsCoreAsync(settings, CancellationToken.None).ConfigureAwait(false);
                throw new ApiKeySlotChangedException();
            }
            settings.SystemPrompt = detached.SystemPrompt;
            settings.CompactContext = detached.CompactContext;
            settings.SuggestFollowUps = detached.SuggestFollowUps;
            settings.ImageModelId = detached.ImageModelId;
            settings.ImageGenerationEnabled = detached.ImageGenerationEnabled;
            CopyEditableWebSearch(settings.WebSearch, detached.WebSearch);
            settings.FailoverChain = detached.FailoverChain;
            settings.AutoFailoverAcrossAccounts = detached.AutoFailoverAcrossAccounts;
            PublishProviderRequests(settings, detached);
        }
        finally { ProviderKeyGate.Release(); }
    }

    private static void ApplyGlobalSettings(AiSettings target, AiSettings draft)
    {
        target.SystemPrompt = draft.SystemPrompt;
        target.CompactContext = draft.CompactContext;
        target.SuggestFollowUps = draft.SuggestFollowUps;
        target.ImageModelId = draft.ImageModelId;
        target.ImageGenerationEnabled = draft.ImageGenerationEnabled;
        CopyEditableWebSearch(target.WebSearch, draft.WebSearch);
        target.FailoverChain = [.. draft.FailoverChain.Where(entry => target.FindModel(entry.ModelId) is not null).Select(entry => new FailoverEntry { ModelId = entry.ModelId })];
        target.AutoFailoverAcrossAccounts = draft.AutoFailoverAcrossAccounts;
    }

    private static void CopyEditableWebSearch(WebSearchOptions target, WebSearchOptions source)
    {
        target.Enabled = source.Enabled;
        target.SearxngBaseUrl = source.SearxngBaseUrl;
        target.MaxResults = source.MaxResults;
        target.PreferProviderNative = source.PreferProviderNative;
        target.AllowPrivateNetwork = source.AllowPrivateNetwork;
        target.AllowedPrivateHosts = source.AllowedPrivateHosts;
    }

    /// <summary>读取某 Key 归属者(供应商 id,或带独立 Key 的模型 id)的 API Key(未配置返回 null)。命中缓存则不碰机密存储。按模型解析继承链请传 <see cref="ResolvedModel.ApiKeyOwnerId" />。</summary>
    public async Task<string?> GetApiKeyAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        bool updating = _providerKeys.Updating.ContainsKey(ownerId);
        if (KeyCache.TryGetValue(ownerId, out string? cached))
        {
            return cached;
        }
        string? key = await context.Secrets.GetAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
        return updating || _providerKeys.Updating.ContainsKey(ownerId) ? key : KeyCache.GetOrAdd(ownerId, key);
    }

    /// <summary>写入(或清除)某归属者的 API Key。</summary>
    public async Task SetApiKeyAsync(string ownerId, string? apiKey, CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<string>? updating = null;
        try
        {
            string? oldKey = await context.Secrets.GetAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
            updating = MarkApiKeyUpdating(ownerId);
            try
            {
                await SetApiKeyCoreAsync(ownerId, apiKey, cancellationToken).ConfigureAwait(false);
                if (oldKey != (string.IsNullOrEmpty(apiKey) ? null : apiKey)) PublishApiKeyChange(ownerId);
            }
            catch
            {
                await RestoreApiKeyCoreAsync(ownerId, oldKey).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (updating is not null) foreach (string id in updating) _providerKeys.Updating.TryRemove(id, out _);
            ProviderKeyGate.Release();
        }
    }

    // KeyGate 已持有；事务提交及回滚不能调用公开入口再次拿闸。
    private async Task SetApiKeyCoreAsync(string ownerId, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            await context.Secrets.DeleteAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
            KeyCache[ownerId] = null;
        }
        else
        {
            await context.Secrets.SetAsync(SecretName(ownerId), apiKey, cancellationToken).ConfigureAwait(false);
            KeyCache[ownerId] = apiKey;
        }
    }

    private Task RestoreApiKeyCoreAsync(string ownerId, string? apiKey)
    {
        KeyCache[ownerId] = string.IsNullOrEmpty(apiKey) ? null : apiKey;
        return SetApiKeyCoreAsync(ownerId, apiKey, CancellationToken.None);
    }

    /// <summary>在共享写闸内删除供应商及全部专属机密；落盘成功前不发布列表、活跃模型或链。</summary>
    public Task DeleteProviderAsync(AiSettings settings, AiProvider provider, CancellationToken cancellationToken = default)
        => DeleteConfigurationAsync(settings, provider, null, cancellationToken);

    /// <summary>原子删除模型及其独立机密；任何失败都保留原配置及凭据。</summary>
    public Task DeleteModelAsync(AiSettings settings, AiProvider provider, AiModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return DeleteConfigurationAsync(settings, provider, model, cancellationToken);
    }

    private async Task DeleteConfigurationAsync(AiSettings settings, AiProvider provider, AiModelConfig? model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(provider);
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool tokenGateHeld = false, settingsWritten = false;
        int touched = 0;
        var secrets = new List<(string Name, string? Value)>();
        var caches = new List<(string Id, string? Key, OAuthTokens? Tokens)>();
        string[] ownerIds = [];
        try
        {
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!settings.Providers.Contains(provider) || model is not null && !provider.Models.Contains(model))
                throw new ApiKeySlotChangedException();
            _knownSettings = settings;
            string liveJson = JsonSerializer.Serialize(settings);
            AiSettings detached = JsonSerializer.Deserialize<AiSettings>(liveJson)!;
            AiProvider staged = detached.Providers[settings.Providers.IndexOf(provider)];
            ownerIds = model is not null ? [model.Id]
                : [.. ProviderApiKeyIds(provider).Concat(provider.Models.Select(config => config.Id)).Distinct(StringComparer.Ordinal)];
            if (model is null) detached.Providers.Remove(staged);
            else staged.Models.RemoveAt(provider.Models.IndexOf(model));
            detached.FailoverChain.RemoveAll(entry => detached.FindModel(entry.ModelId) is null);
            if (detached.FindModel(detached.ActiveModelId) is null)
                detached.ActiveModelId = detached.ResolveModels().FirstOrDefault()?.Id;
            if (detached.ActiveProviderId == provider.Id && model is null || detached.ActiveProviderId == model?.Id)
                detached.ActiveProviderId = null;
            _providerKeys.Updating[provider.Id] = 0;
            foreach (string ownerId in ownerIds) _providerKeys.Updating[ownerId] = 0;
            await _providerKeys.TokenWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            tokenGateHeld = true;
            foreach (string ownerId in ownerIds)
            {
                string keyName = SecretName(ownerId), tokenName = TokenSecretName(ownerId);
                string? key = await context.Secrets.GetAsync(keyName, cancellationToken).ConfigureAwait(false);
                string? tokenJson = await context.Secrets.GetAsync(tokenName, cancellationToken).ConfigureAwait(false);
                secrets.Add((keyName, key));
                secrets.Add((tokenName, tokenJson));
                caches.Add((ownerId, KeyCache.TryGetValue(ownerId, out string? cachedKey) ? cachedKey : key,
                    TokenCache.TryGetValue(ownerId, out OAuthTokens? cachedTokens) ? cachedTokens : ParseTokens(ownerId, tokenJson)));
            }
            foreach ((string Name, string? Value) secret in secrets)
            {
                ++touched;
                await context.Secrets.DeleteAsync(secret.Name, cancellationToken).ConfigureAwait(false);
            }
            await WriteSettingsSnapshotAsync(settings, JsonSerializer.Serialize(detached), cancellationToken).ConfigureAwait(false);
            settingsWritten = true;
            if (JsonSerializer.Serialize(settings) != liveJson) throw new ApiKeySlotChangedException();
            PublishProviderRequests(settings, detached);
            if (model is null) settings.Providers.Remove(provider);
            else provider.Models.Remove(model);
            settings.ActiveModelId = detached.ActiveModelId;
            settings.ActiveProviderId = detached.ActiveProviderId;
            settings.FailoverChain = detached.FailoverChain;
            foreach (string ownerId in ownerIds)
            {
                KeyCache[ownerId] = null;
                TokenCache[ownerId] = null;
                _providerKeys.LoginVersions.AddOrUpdate(ownerId, 1, static (_, version) => version + 1);
            }
        }
        catch
        {
            foreach ((string id, string? key, OAuthTokens? tokens) in caches)
            {
                KeyCache[id] = key;
                TokenCache[id] = tokens;
            }
            for (int index = 0; index < touched; ++index)
            {
                (string Name, string? Value) secret = secrets[index];
                if (secret.Value is null) await context.Secrets.DeleteAsync(secret.Name, CancellationToken.None).ConfigureAwait(false);
                else await context.Secrets.SetAsync(secret.Name, secret.Value, CancellationToken.None).ConfigureAwait(false);
            }
            if (settingsWritten) await SaveSettingsCoreAsync(settings, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _providerKeys.Updating.TryRemove(provider.Id, out _);
            foreach (string ownerId in ownerIds) _providerKeys.Updating.TryRemove(ownerId, out _);
            if (tokenGateHeld) _providerKeys.TokenWriteGate.Release();
            ProviderKeyGate.Release();
        }
    }

    /// <summary>删除供应商 / 模型时连带清除其机密(API Key 与订阅令牌一并清)。</summary>
    public async Task DeleteApiKeyAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<string>? updating = null;
        bool tokenGateHeld = false, keyTouched = false, tokenTouched = false;
        string? oldKey = null, oldTokenJson = null;
        OAuthTokens? oldTokens = null;
        try
        {
            // 唯一嵌套顺序是 KeyGate → TokenWriteGate；令牌刷新不拿 KeyGate。
            await _providerKeys.TokenWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            tokenGateHeld = true;
            oldKey = await context.Secrets.GetAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
            oldTokenJson = await context.Secrets.GetAsync(TokenSecretName(ownerId), cancellationToken).ConfigureAwait(false);
            oldTokens = await GetTokensAsync(ownerId, cancellationToken).ConfigureAwait(false);
            updating = MarkApiKeyUpdating(ownerId);
            keyTouched = true;
            await SetApiKeyCoreAsync(ownerId, null, cancellationToken).ConfigureAwait(false);
            tokenTouched = true;
            await context.Secrets.DeleteAsync(TokenSecretName(ownerId), cancellationToken).ConfigureAwait(false);
            TokenCache[ownerId] = null;
            _providerKeys.LoginVersions.AddOrUpdate(ownerId, 1, static (_, version) => version + 1);
            if (oldKey is not null) PublishApiKeyChange(ownerId);
        }
        catch
        {
            if (keyTouched) await RestoreApiKeyCoreAsync(ownerId, oldKey).ConfigureAwait(false);
            if (tokenTouched)
            {
                TokenCache[ownerId] = oldTokens;
                if (oldTokenJson is null) await context.Secrets.DeleteAsync(TokenSecretName(ownerId), CancellationToken.None).ConfigureAwait(false);
                else await context.Secrets.SetAsync(TokenSecretName(ownerId), oldTokenJson, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (updating is not null) foreach (string id in updating) _providerKeys.Updating.TryRemove(id, out _);
            if (tokenGateHeld) _providerKeys.TokenWriteGate.Release();
            ProviderKeyGate.Release();
        }
    }

    private SemaphoreSlim ProviderKeyGate => _providerKeys.Gate;

    internal IEnumerable<string> ProviderApiKeyIds(AiProvider provider)
    {
        yield return provider.Id;
        var seen = new HashSet<string>(StringComparer.Ordinal) { provider.Id };
        foreach (string id in provider.AdditionalApiKeyIds)
        {
            if (!Guid.TryParseExact(id, "N", out _) || !seen.Add(id)
                || provider.Models.Any(model => model.Id == id)
                || !(_knownSettings?.Providers.Any(owner => owner.Id == provider.Id
                    && owner.AdditionalApiKeyIds.Contains(id, StringComparer.Ordinal)) ?? false)
                || (_knownSettings?.Providers.Any(owner => owner.Id == id || owner.Models.Any(model => model.Id == id)
                    || (owner.Id != provider.Id && owner.AdditionalApiKeyIds.Contains(id, StringComparer.Ordinal))) ?? false))
            {
                continue;
            }

            yield return id;
        }
    }

    private void ValidateProvider(AiSettings settings, AiProvider provider)
    {
        if (!settings.Providers.Contains(provider) || provider.Auth != AuthMethod.ApiKey)
            throw new ApiKeySlotChangedException();
        _knownSettings = settings;
    }

    private async Task ValidateProviderSlotTopologyAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        JsonElement saved = await context.Storage.GetAsync<JsonElement>(SettingsKey, cancellationToken).ConfigureAwait(false);
        if (saved.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        if (!saved.TryGetProperty(nameof(AiSettings.Providers), out JsonElement providers) || providers.ValueKind != JsonValueKind.Array)
            throw new ApiKeySlotChangedException();
        foreach (JsonElement owner in providers.EnumerateArray())
        {
            if (!owner.TryGetProperty(nameof(AiProvider.Id), out JsonElement id) || id.GetString() != provider.Id) continue;
            if (!owner.TryGetProperty(nameof(AiProvider.AdditionalApiKeyIds), out JsonElement slots))
            {
                if (provider.AdditionalApiKeyIds.Count == 0) return; // 旧配置尚无补充槽字段。
                throw new ApiKeySlotChangedException();
            }
            if (slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() != provider.AdditionalApiKeyIds.Count)
                throw new ApiKeySlotChangedException();
            int index = 0;
            foreach (JsonElement slot in slots.EnumerateArray())
            {
                if (slot.ValueKind != JsonValueKind.String || slot.GetString() != provider.AdditionalApiKeyIds[index++])
                    throw new ApiKeySlotChangedException();
            }

            return;
        }
        // 基线已确认当前落盘版本；新加入活设置的供应商尚无补充槽时可以首次提交。
        if (provider.AdditionalApiKeyIds.Count != 0) throw new ApiKeySlotChangedException();
    }

    /// <summary>
    /// 提交供应商表单(名称、地址、协议)与可选的一把替换 Key。
    /// </summary>
    /// <remarks>
    /// Base URL 换到另一个源(协议、主机或端口不同)时,已存的 Key 属于旧地址,不能跟着发过去:
    /// 主槽、补充槽与继承这个地址的模型自带 Key 一并清掉,补充槽与当前槽复位;同一次提交里新填的 Key
    /// 是给新地址的,落进主槽。会清掉已存 Key 时默认拒绝并抛 <see cref="ApiKeyResetRequiredException" />,
    /// 调用方让用户确认后把异常里的 <see cref="ApiKeyResetRequiredException.Confirmation" /> 经
    /// <paramref name="confirmedKeyReset" /> 交回;要清的超出确认过的那几把(提示之后别处又存了一把、或同一个槽换了值)
    /// 就再拒绝一次。只改路径(同源)不动 Key。
    /// </remarks>
    /// <returns>清掉的已存 Key 的归属者(不含被这次替换覆盖的那一把)。</returns>
    internal async Task<IReadOnlyList<string>> SaveProviderApiKeyFormAsync(AiSettings settings, AiProvider provider,
        AiProvider draft, string expectedProviderJson, string? replacementKey, string? keyId, string? expectedKey,
        IReadOnlyCollection<string>? confirmedKeyReset = null, CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken);
        string? oldKey = null;
        string ownerId = keyId ?? provider.Id;
        bool secretWritten = false, settingsWritten = false;
        // 换源时动过的机密:归属者 → 原值,失败时逐个恢复
        Dictionary<string, string?> resetKeys = [with(StringComparer.Ordinal)];
        List<(string Id, string Key)> cleared = [];
        try
        {
            ValidateProvider(settings, provider);
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            await ValidateProviderSlotTopologyAsync(provider, cancellationToken);
            if (JsonSerializer.Serialize(provider) != expectedProviderJson) throw new ApiKeySlotChangedException();
            if (!ProviderApiKeyIds(provider).Contains(ownerId, StringComparer.Ordinal)) throw new ApiKeySlotChangedException();
            bool originChanged = MovesOrigin(provider.BaseUrl, draft.BaseUrl);
            if (replacementKey is not null)
            {
                // 换源时其余 Key 都要清掉,新 Key 与它们重复也无妨(地址搬家、Key 不变是常见情形);空白照样不收
                if (!originChanged) await ValidateUniqueKeyAsync(provider, replacementKey, ownerId, cancellationToken);
                else if (string.IsNullOrWhiteSpace(replacementKey))
                    throw new ArgumentException("API Key must be nonempty and unique.", nameof(replacementKey));
            }
            oldKey = await context.Secrets.GetAsync(SecretName(ownerId), cancellationToken);
            if ((keyId is null && oldKey is not null) || oldKey != expectedKey) throw new ApiKeySlotChangedException();
            List<string> resetOwners = originChanged ? [.. KeysBoundToProviderOrigin(provider)] : [];
            foreach (string id in resetOwners)
            {
                string? saved = id == ownerId ? oldKey : await context.Secrets.GetAsync(SecretName(id), cancellationToken);
                resetKeys[id] = saved;
                // 被这次替换覆盖的那一把是用户自己换掉的,不算「清掉」;只有空白的那种算不上 Key,照清不问
                if (!string.IsNullOrWhiteSpace(saved) && (replacementKey is null || id != ownerId)) cleared.Add((id, saved));
            }
            RequireKeyResetConfirmed(cleared, confirmedKeyReset);

            AiProvider staged = draft;
            _providerKeys.Updating[provider.Id] = 0;
            _providerKeys.Updating[ownerId] = 0;
            foreach (string id in resetOwners) _providerKeys.Updating[id] = 0;
            if (originChanged)
            {
                staged = JsonSerializer.Deserialize<AiProvider>(JsonSerializer.Serialize(draft))!;
                staged.AdditionalApiKeyIds = [];
                staged.ActiveApiKeyId = null;
                secretWritten = true;
                // 缓存跟着机密一起改:提交期间别处读到的是「没有」(主槽是这次新填的那把),不会把旧 Key 拿在手里、
                // 等提交成功后带去新地址;失败回滚时 catch 里逐个恢复机密与缓存
                foreach (string id in resetOwners)
                    await SetApiKeyCoreAsync(id, id == provider.Id ? replacementKey : null, cancellationToken);
            }
            else if (replacementKey is not null && replacementKey != oldKey)
            {
                secretWritten = true;
                await context.Secrets.SetAsync(SecretName(ownerId), replacementKey, cancellationToken);
            }
            ValidateProvider(settings, provider);
            if (JsonSerializer.Serialize(provider) != expectedProviderJson) throw new ApiKeySlotChangedException();
            string expectedSettingsJson = JsonSerializer.Serialize(settings);
            AiSettings detached = JsonSerializer.Deserialize<AiSettings>(expectedSettingsJson)!;
            detached.Providers[settings.Providers.IndexOf(provider)] = staged;
            await WriteSettingsSnapshotAsync(settings, JsonSerializer.Serialize(detached), cancellationToken).ConfigureAwait(false);
            settingsWritten = true;
            ValidateProvider(settings, provider);
            if (JsonSerializer.Serialize(settings) != expectedSettingsJson) throw new ApiKeySlotChangedException();
            // 无 await 的发布段:机密/JSON等待期间活配置保持原地址与凭据。
            CopyProvider(provider, staged);
            if (originChanged)
            {
                // 清掉的槽与模型 Key 都归这家供应商:按主槽 id 发布,别处设置里的同一供应商副本也一并作废
                PublishProviderRequests(settings, detached, changedKeyOwnerId: provider.Id);
                if (replacementKey is not null) KeyCache[provider.Id] = replacementKey;
            }
            else
            {
                PublishProviderRequests(settings, detached, changedKeyOwnerId: secretWritten ? ownerId : null);
                if (replacementKey is not null) KeyCache[ownerId] = replacementKey;
            }
            return [.. cleared.Select(item => item.Id)];
        }
        catch
        {
            if (resetKeys.Count > 0 && secretWritten)
            {
                foreach ((string id, string? saved) in resetKeys) await RestoreApiKeyCoreAsync(id, saved);
            }
            else if (secretWritten)
            {
                await RestoreApiKeyCoreAsync(ownerId, oldKey);
            }

            if (settingsWritten) await SaveSettingsCoreAsync(settings, CancellationToken.None);
            throw;
        }
        finally
        {
            _providerKeys.Updating.TryRemove(provider.Id, out _);
            _providerKeys.Updating.TryRemove(ownerId, out _);
            foreach (string id in resetKeys.Keys) _providerKeys.Updating.TryRemove(id, out _);
            ProviderKeyGate.Release();
        }
    }

    /// <summary>Base URL 从 <paramref name="from" /> 改成 <paramref name="to" /> 是否换到了另一个源;只改路径、大小写或写出默认端口不算。</summary>
    internal static bool MovesOrigin(string? from, string? to)
        => !string.Equals(from, to, StringComparison.Ordinal) && !SameOrigin(from, to);

    /// <summary>
    /// 跟着供应商 Base URL 走的 Key:主槽、补充槽,以及没填覆盖地址、自带 Key 的模型。
    /// 填了覆盖地址的模型去的是自己的地址,供应商换地址与它们无关。
    /// </summary>
    private IEnumerable<string> KeysBoundToProviderOrigin(AiProvider provider)
    {
        foreach (string id in ProviderApiKeyIds(provider)) yield return id;
        foreach (AiModelConfig model in provider.Models)
        {
            if (model.HasOwnApiKey && string.IsNullOrWhiteSpace(model.BaseUrlOverride)) yield return model.Id;
        }
    }

    /// <summary>确认凭据里的一项:归属者 + 值的摘要(不留明文)。同一个槽换了值,摘要就对不上。</summary>
    internal static string KeyResetToken(string ownerId, string key)
        => ownerId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>确认凭据里的登录令牌那一项:换了一次登录(令牌组变了),摘要就对不上,要重新问。</summary>
    internal static string LoginResetToken(string providerId, string tokenJson) => KeyResetToken(TokenSecretName(providerId), tokenJson);

    // 确认的是提示时列出的那几把(归属者与值都算)与那一份登录:别处又存进一把、同一个槽换了值、换了一次登录,
    // 都要重新问,不能连它一起清;要清的变少了(提示后在主槽填了新 Key)仍在确认之内
    private static void RequireKeyResetConfirmed(List<(string Id, string Key)> cleared, IReadOnlyCollection<string>? confirmed,
        string? loginToken = null)
    {
        if (cleared.Count == 0 && loginToken is null) return;
        string[] tokens = [.. cleared.Select(item => KeyResetToken(item.Id, item.Key)), .. loginToken is null ? [] : new[] { loginToken }];
        if (confirmed is null || !tokens.All(token => confirmed.Contains(token, StringComparer.Ordinal)))
            throw new ApiKeyResetRequiredException([.. cleared.Select(item => item.Id)], tokens, signsOut: loginToken is not null);
    }

    private async Task ValidateUniqueKeyAsync(AiProvider provider, string key, string? exceptId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("API Key must be nonempty and unique.", nameof(key));
        foreach (string id in ProviderApiKeyIds(provider))
        {
            if (id != exceptId && await GetApiKeyAsync(id, cancellationToken).ConfigureAwait(false) == key)
                throw new ArgumentException("API Key must be nonempty and unique.", nameof(key));
        }
    }

    private async Task CommitProviderKeySlotsAsync(AiSettings settings, AiProvider provider,
        List<string> slots, string? active, string? changedKeyOwnerId, CancellationToken cancellationToken)
    {
        string baseline = JsonSerializer.Serialize(settings);
        AiSettings detached = JsonSerializer.Deserialize<AiSettings>(baseline)!;
        AiProvider staged = detached.Providers[settings.Providers.IndexOf(provider)];
        staged.AdditionalApiKeyIds = slots;
        staged.ActiveApiKeyId = active;
        await WriteSettingsSnapshotAsync(settings, JsonSerializer.Serialize(detached), cancellationToken).ConfigureAwait(false);
        if (JsonSerializer.Serialize(settings) != baseline)
        {
            await SaveSettingsCoreAsync(settings, CancellationToken.None).ConfigureAwait(false);
            throw new ApiKeySlotChangedException();
        }
        provider.AdditionalApiKeyIds.Clear();
        provider.AdditionalApiKeyIds.AddRange(slots);
        provider.ActiveApiKeyId = active;
        PublishProviderRequests(settings, detached, changedKeyOwnerId: changedKeyOwnerId);
    }

    /// <summary>为供应商添加不重复的加密 API Key;失败恢复机密及配置。</summary>
    public async Task<string> AddProviderApiKeyAsync(AiSettings settings, AiProvider provider, string key,
        CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateProvider(settings, provider);
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            await ValidateProviderSlotTopologyAsync(provider, cancellationToken).ConfigureAwait(false);
            await ValidateUniqueKeyAsync(provider, key, null, cancellationToken).ConfigureAwait(false);
            bool hasKey = false;
            foreach (string id in ProviderApiKeyIds(provider))
                hasKey |= !string.IsNullOrWhiteSpace(await GetApiKeyAsync(id, cancellationToken).ConfigureAwait(false));
            string keyId = hasKey ? Guid.NewGuid().ToString("N") : provider.Id;
            string? oldKey = await context.Secrets.GetAsync(SecretName(keyId), cancellationToken).ConfigureAwait(false);
            _providerKeys.Updating[provider.Id] = 0;
            try
            {
                await SetApiKeyCoreAsync(keyId, key, cancellationToken).ConfigureAwait(false);
                ValidateProvider(settings, provider);
                List<string> slots = [.. provider.AdditionalApiKeyIds];
                if (hasKey) slots.Add(keyId);
                await CommitProviderKeySlotsAsync(settings, provider, slots, provider.ActiveApiKeyId,
                    oldKey != key ? keyId : null, cancellationToken).ConfigureAwait(false);
                return keyId;
            }
            catch
            {
                await RestoreApiKeyCoreAsync(keyId, oldKey).ConfigureAwait(false);
                throw;
            }
        }
        finally { _providerKeys.Updating.TryRemove(provider.Id, out _); ProviderKeyGate.Release(); }
    }

    /// <summary>替换供应商的固定 Key 槽,保持 ID、顺序与当前槽不变。</summary>
    public async Task ReplaceProviderApiKeyAsync(AiSettings settings, AiProvider provider, string keyId, string key,
        CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateProvider(settings, provider);
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            await ValidateProviderSlotTopologyAsync(provider, cancellationToken).ConfigureAwait(false);
            if (!ProviderApiKeyIds(provider).Contains(keyId, StringComparer.Ordinal)) throw new ApiKeySlotChangedException();
            await ValidateUniqueKeyAsync(provider, key, keyId, cancellationToken).ConfigureAwait(false);
            string? oldKey = await context.Secrets.GetAsync(SecretName(keyId), cancellationToken).ConfigureAwait(false);
            if (oldKey == key) return;
            _providerKeys.Updating[provider.Id] = 0;
            _providerKeys.Updating[keyId] = 0;
            try
            {
                await SetApiKeyCoreAsync(keyId, key, cancellationToken).ConfigureAwait(false);
                ValidateProvider(settings, provider);
                await SaveSettingsCoreAsync(settings, cancellationToken, keyId).ConfigureAwait(false);
            }
            catch
            {
                await RestoreApiKeyCoreAsync(keyId, oldKey).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _providerKeys.Updating.TryRemove(keyId, out _);
            _providerKeys.Updating.TryRemove(provider.Id, out _);
            ProviderKeyGate.Release();
        }
    }

    /// <summary>移除固定 Key 槽;主槽只清机密,失败恢复原值与位置。</summary>
    public async Task RemoveProviderApiKeyAsync(AiSettings settings, AiProvider provider, string keyId,
        CancellationToken cancellationToken = default)
    {
        await ProviderKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateProvider(settings, provider);
            await ValidateSettingsBaselineAsync(settings, cancellationToken).ConfigureAwait(false);
            await ValidateProviderSlotTopologyAsync(provider, cancellationToken).ConfigureAwait(false);
            if (!ProviderApiKeyIds(provider).Contains(keyId, StringComparer.Ordinal)) throw new ApiKeySlotChangedException();
            string? oldKey = await context.Secrets.GetAsync(SecretName(keyId), cancellationToken).ConfigureAwait(false);
            _providerKeys.Updating[provider.Id] = 0;
            try
            {
                await SetApiKeyCoreAsync(keyId, null, cancellationToken).ConfigureAwait(false);
                ValidateProvider(settings, provider);
                List<string> slots = [.. provider.AdditionalApiKeyIds];
                if (keyId != provider.Id) slots.RemoveAll(id => id == keyId);
                string? active = provider.ActiveApiKeyId == keyId ? null : provider.ActiveApiKeyId;
                await CommitProviderKeySlotsAsync(settings, provider, slots, active,
                    oldKey is not null ? keyId : null, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RestoreApiKeyCoreAsync(keyId, oldKey).ConfigureAwait(false);
                throw;
            }
        }
        finally { _providerKeys.Updating.TryRemove(provider.Id, out _); ProviderKeyGate.Release(); }
    }

    // ---- 订阅登录的令牌 ----

    /// <summary>
    /// 已解出的登录令牌。与 <see cref="KeyCache" /> 同一个理由:每发一条消息都要取一次,
    /// 而取一次是"读库 + DPAPI 解包 + 反序列化"。
    /// </summary>
    private ConcurrentDictionary<string, OAuthTokens?> TokenCache => _providerKeys.Tokens;

    /// <summary>刷新令牌时的单飞闸:一轮对话会并发建好几个客户端,没有它就会同时刷好几次。</summary>
    private SemaphoreSlim RefreshGate => _providerKeys.RefreshGate;

    internal long TokenLoginVersion(string providerId) => _providerKeys.LoginVersions.GetValueOrDefault(providerId);

    /// <summary>
    /// 刷令牌用的 HTTP 客户端。<b>静态共享</b>:每次刷新新建一个会攒下一堆处于 TIME_WAIT 的连接,
    /// 而这条路上不需要任何自定义 handler(令牌端点不是 SSE,也没有中转站要清洗)。
    /// </summary>
    private static readonly HttpClient TokenHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>刷令牌走谁。留成可替换的,测试里换成打桩的 handler(生产从不改它)。</summary>
    internal OAuthClient TokenClient { get; set; } = new(TokenHttp);

    /// <summary>读取某供应商的订阅登录令牌;没登录过返回 null。</summary>
    public async Task<OAuthTokens?> GetTokensAsync(string providerId, CancellationToken cancellationToken = default)
    {
        bool updating = _providerKeys.Updating.ContainsKey(providerId);
        if (TokenCache.TryGetValue(providerId, out OAuthTokens? cached))
        {
            return cached;
        }
        string? json = await context.Secrets.GetAsync(TokenSecretName(providerId), cancellationToken).ConfigureAwait(false);
        OAuthTokens? tokens = ParseTokens(providerId, json);
        return updating || _providerKeys.Updating.ContainsKey(providerId) ? tokens : TokenCache.GetOrAdd(providerId, tokens);
    }

    private OAuthTokens? ParseTokens(string providerId, string? json)
    {
        OAuthTokens? tokens = null;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                tokens = JsonSerializer.Deserialize<OAuthTokens>(json);
            }
            catch (JsonException ex)
            {
                // 存坏了就当没登录 —— 让用户重登一次,好过每条消息都炸一次
                context.Log.Warn($"Stored sign-in for provider {providerId} could not be read: {ex.Message}");
            }
        }
        return tokens;
    }

    internal async Task<(OAuthTokens? Tokens, long Version)> GetTokenSnapshotAsync(string providerId, CancellationToken token)
    {
        await _providerKeys.TokenWriteGate.WaitAsync(token).ConfigureAwait(false);
        try { return (await GetTokensAsync(providerId, token).ConfigureAwait(false), TokenLoginVersion(providerId)); }
        finally { _providerKeys.TokenWriteGate.Release(); }
    }

    /// <summary>写入登录令牌(整组 JSON 加密落盘)。</summary>
    public Task SaveTokensAsync(string providerId, OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return SaveTokensCoreAsync(providerId, tokens, true, cancellationToken);
    }

    private async Task SaveTokensCoreAsync(string providerId, OAuthTokens? tokens, bool newLogin,
        CancellationToken cancellationToken, long? expectedLoginVersion = null)
    {
        await _providerKeys.TokenWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool tokenWritten = false;
        string? oldJson = null;
        OAuthTokens? oldTokens = null;
        try
        {
            if (expectedLoginVersion is { } expected && TokenLoginVersion(providerId) != expected)
                throw new ApiKeySlotChangedException();
            oldJson = await context.Secrets.GetAsync(TokenSecretName(providerId), cancellationToken).ConfigureAwait(false);
            oldTokens = TokenCache.TryGetValue(providerId, out OAuthTokens? cached) ? cached : ParseTokens(providerId, oldJson);
            // 即使宿主先改内存再写盘，冷读也只能取得仍与活配置配对的旧令牌。
            TokenCache[providerId] = oldTokens;
            tokenWritten = true;
            if (tokens is null) await context.Secrets.DeleteAsync(TokenSecretName(providerId), cancellationToken).ConfigureAwait(false);
            else await context.Secrets.SetAsync(TokenSecretName(providerId), JsonSerializer.Serialize(tokens), cancellationToken).ConfigureAwait(false);
            TokenCache[providerId] = tokens;
            if (newLogin) _providerKeys.LoginVersions.AddOrUpdate(providerId, 1, static (_, version) => version + 1);
        }
        catch
        {
            if (tokenWritten)
            {
                TokenCache[providerId] = oldTokens;
                if (oldJson is null) await context.Secrets.DeleteAsync(TokenSecretName(providerId), CancellationToken.None).ConfigureAwait(false);
                else await context.Secrets.SetAsync(TokenSecretName(providerId), oldJson, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally { _providerKeys.TokenWriteGate.Release(); }
    }

    /// <summary>退出登录:清掉令牌；失败保持原账号、缓存与登录代次。</summary>
    public Task ClearTokensAsync(string providerId, CancellationToken cancellationToken = default)
        => SaveTokensCoreAsync(providerId, null, true, cancellationToken);

    /// <summary>
    /// 解出这个模型发请求时该用的凭据:模型自带 Key > 供应商订阅登录 > 供应商 API Key。
    /// </summary>
    /// <remarks>
    /// 订阅登录且换回来的是短期 access token 时,这里会<b>顺手把快过期的刷掉</b> ——
    /// 客户端是每发一条消息现建的,在建之前刷新,就等于每条消息都拿着一把新鲜的令牌上路,
    /// 不必再往请求管道里塞一层"401 就重试"。
    /// </remarks>
    /// <param name="model">已解出继承链的模型。</param>
    /// <param name="refreshTokens">发送准备时刷新;仅核对已解析凭据时设为 false,避免核对本身旋转令牌。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<ProviderCredential> ResolveCredentialAsync(ResolvedModel model,
        bool refreshTokens = true, CancellationToken cancellationToken = default)
        => (await ResolveCredentialWithKeyIdAsync(model, refreshTokens, cancellationToken: cancellationToken).ConfigureAwait(false)).Credential;

    internal async Task<(ProviderCredential Credential, string? KeyId)> ResolveCredentialWithKeyIdAsync(
        ResolvedModel model, bool refreshTokens = true, string? keyId = null, CancellationToken cancellationToken = default)
        => await ResolveSavedCredentialAsync(model, false, cancellationToken, refreshTokens, keyId).ConfigureAwait(false);
    internal Task<(ProviderCredential Credential, string? KeyId)> ResolveProviderCredentialWithKeyIdAsync(
        AiProvider provider, string? keyId = null, CancellationToken cancellationToken = default)
        => ResolveSavedCredentialAsync(new ResolvedModel(provider, new AiModelConfig()), true, cancellationToken, keyId: keyId);

    internal async Task ValidateProviderCredentialScopeAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        if (_providerKeys.Updating.ContainsKey(provider.Id)) throw new ApiKeySlotChangedException();
        long version = ProviderConfigurationVersion(provider);
        await ValidateCredentialScopeAsync(new ResolvedModel(provider, new AiModelConfig()), true, cancellationToken).ConfigureAwait(false);
        if (_providerKeys.Updating.ContainsKey(provider.Id) || ProviderConfigurationVersion(provider) != version)
            throw new ApiKeySlotChangedException();
    }

    private async Task ValidateCredentialScopeAsync(ResolvedModel model, bool providerOnly, CancellationToken token)
    {
        if (_providerKeys.Updating.ContainsKey(model.Provider.Id)
            || ProviderEpochs.TryGetValue(model.Provider.Id, out ProviderEpoch? epoch) && epoch.Deleted)
        {
            throw new ApiKeySlotChangedException();
        }

        JsonElement raw = await context.Storage.GetAsync<JsonElement>(SettingsKey, token).ConfigureAwait(false);
        if (_providerKeys.Updating.ContainsKey(model.Provider.Id)
            || ProviderEpochs.TryGetValue(model.Provider.Id, out epoch) && epoch.Deleted)
        {
            throw new ApiKeySlotChangedException();
        }

        string? savedJson = SavedSettingsJson(raw);
        if (savedJson is null && !_providerKeys.ProviderSettings.TryGetValue(model.Provider, out _)) return;
        if (!_providerKeys.ProviderSettings.TryGetValue(model.Provider, out SettingsScope? scope)
            || !scope.Settings.Providers.Contains(model.Provider)
            || _providerKeys.SettingsBaselines.GetValue(scope.Settings, static _ => new()).Json != savedJson)
        {
            throw new ApiKeySlotChangedException();
        }

        AiProvider? saved = null;
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(nameof(AiSettings.Providers), out JsonElement owners)
            && owners.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement owner in owners.EnumerateArray())
            {
                if (!owner.TryGetProperty(nameof(AiProvider.Id), out JsonElement id) || id.GetString() != model.Provider.Id) continue;
                saved = owner.Deserialize<AiProvider>();
                break;
            }
        }
        if (saved is null || ProviderRequestJson(saved) != ProviderRequestJson(model.Provider)
            || !providerOnly && !model.Provider.Models.Contains(model.Config))
        {
            throw new ApiKeySlotChangedException();
        }

        string currentUrl = string.IsNullOrWhiteSpace(model.Config.BaseUrlOverride) ? model.Provider.BaseUrl : model.Config.BaseUrlOverride;
        if (currentUrl != model.BaseUrl || (model.Config.Protocol ?? model.Provider.DefaultProtocol) != model.Protocol
            || (model.Config.HasOwnApiKey ? model.Config.Id : model.Provider.Id) != model.ApiKeyOwnerId)
        {
            throw new ApiKeySlotChangedException();
        }

        _knownSettings = scope.Settings;
    }

    private async Task<(ProviderCredential Credential, string? KeyId)> ResolveSavedCredentialAsync(
        ResolvedModel model, bool providerOnly, CancellationToken cancellationToken, bool refreshTokens = true, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_providerKeys.Updating.ContainsKey(model.Provider.Id)) throw new ApiKeySlotChangedException();
        long version = ProviderConfigurationVersion(model.Provider);
        await ValidateCredentialScopeAsync(model, providerOnly, cancellationToken).ConfigureAwait(false);
        (ProviderCredential Credential, string? KeyId) credential = await ResolveCredentialCoreAsync(model, refreshTokens, keyId, cancellationToken).ConfigureAwait(false);
        if (_providerKeys.Updating.ContainsKey(model.Provider.Id) || ProviderConfigurationVersion(model.Provider) != version)
            throw new ApiKeySlotChangedException();
        await ValidateCredentialScopeAsync(model, providerOnly, cancellationToken).ConfigureAwait(false);
        if (_providerKeys.Updating.ContainsKey(model.Provider.Id) || ProviderConfigurationVersion(model.Provider) != version)
            throw new ApiKeySlotChangedException();
        return credential;
    }

    private async Task<(ProviderCredential Credential, string? KeyId)> ResolveCredentialCoreAsync(
        ResolvedModel model, bool refreshTokens, string? keyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        AiProvider provider = model.Provider;
        if (model.Config.HasOwnApiKey)
            return (ProviderCredential.Key(await GetApiKeyAsync(model.ApiKeyOwnerId, cancellationToken).ConfigureAwait(false)), null);
        if (provider.Auth != AuthMethod.Subscription)
        {
            if (_providerKeys.Updating.ContainsKey(provider.Id)) throw new ApiKeySlotChangedException();
            // 模型的覆盖地址与供应商不同源时,供应商的 Key(主槽也一样)都不跟去:那是给供应商自己那个地址的。
            // 覆盖地址要 Key 就勾「自带 Key」填上;没勾就是不带 Key 的请求(设置页对这类模型给迁移提示)。
            // 以前跨源时借主槽 —— 供应商换源之后,为新地址填的主 Key 会被发往这个旧覆盖地址(§217 留下的口径)
            bool sameOrigin = SameOrigin(model.BaseUrl, provider.BaseUrl);
            bool crossOriginOverride = InheritsNoKeyAcrossOrigins(provider, model.Config);
            if (!string.IsNullOrEmpty(keyId))
            {
                if (crossOriginOverride || !ProviderApiKeyIds(provider).Contains(keyId, StringComparer.Ordinal)
                    || (!sameOrigin && keyId != provider.Id))
                {
                    throw new ApiKeySlotChangedException();
                }

                string? selected = await GetApiKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(selected)) throw new ApiKeySlotChangedException();
                return (ProviderCredential.Key(selected), sameOrigin ? keyId : null);
            }
            if (crossOriginOverride) return (ProviderCredential.Key(null), null);
            // 供应商自己的地址解析不出 origin(空着或写错)时照旧只用主槽,不进 Key 池
            if (!sameOrigin)
                return (ProviderCredential.Key(await GetApiKeyAsync(provider.Id, cancellationToken).ConfigureAwait(false)), null);
            IEnumerable<string> ids = ProviderApiKeyIds(provider);
            if (provider.ActiveApiKeyId is { } active && ids.Contains(active, StringComparer.Ordinal))
                ids = new[] { active }.Concat(ids.Where(id => id != active));
            foreach (string id in ids)
            {
                string? key = await GetApiKeyAsync(id, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(key)) return (ProviderCredential.Key(key), id);
            }
            return (ProviderCredential.Key(null), null);
        }
        (OAuthTokens? tokens, long loginVersion) = await GetTokenSnapshotAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (tokens is null)
        {
            return (ProviderCredential.Key(null), null); // 尚未登录,保持旧空凭据行为。
        }
        // 登录换回来的是一把长期 API Key(OpenRouter 那类):与手填的 Key 走同一条路
        if (provider.OAuth?.Credential == OAuthCredential.ApiKey)
        {
            var key = ProviderCredential.Key(tokens.AccessToken);
            EnsureBuiltinOAuthCredentialDestination(model, key);
            return (key, null);
        }
        if (refreshTokens && tokens.NeedsRefresh && !string.IsNullOrEmpty(tokens.RefreshToken) && provider.OAuth is { } oauth)
        {
            tokens = await RefreshAsync(provider, oauth, tokens, loginVersion, cancellationToken).ConfigureAwait(false);
        }
        ProviderCredential bearer = new(tokens.AccessToken, true,
            ExtraHeadersPolicy.Parse(provider.OAuth?.ExtraHeaders, tokens.AccountId), tokens.BaseUrl);
        EnsureBuiltinOAuthCredentialDestination(model, bearer);
        return (bearer, null);
    }

    /// <summary>
    /// 填 Key 的供应商下,覆盖地址与供应商不同源、又没勾「自带 Key」的模型:请求不带任何 Key
    /// (见 <see cref="ResolveCredentialCoreAsync" />)。以前这类模型借主槽,设置页据此给迁移提示。
    /// </summary>
    internal static bool InheritsNoKeyAcrossOrigins(AiProvider provider, AiModelConfig model)
        => provider.Auth == AuthMethod.ApiKey && !model.HasOwnApiKey
            && !string.IsNullOrWhiteSpace(model.BaseUrlOverride)
            && !string.Equals(model.BaseUrlOverride, provider.BaseUrl, StringComparison.Ordinal)
            && !SameOrigin(model.BaseUrlOverride, provider.BaseUrl);

    internal static bool SameOrigin(string? a, string? b)
        => Uri.TryCreate(a, UriKind.Absolute, out Uri? left)
            && Uri.TryCreate(b, UriKind.Absolute, out Uri? right)
            && left.Scheme is "http" or "https" && right.Scheme is "http" or "https"
            && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;

    /// <summary>
    /// 取<b>供应商这一层</b>的凭据(拉模型列表用,那一步还没有选定模型)。
    /// </summary>
    /// <remarks>
    /// 拿一个空白模型去解继承链,而不是拿 <c>Models[0]</c>:后者可能勾着"用自己的 Key",
    /// 那把 Key 是给那个模型的,拿它去问整家的模型列表就问错了对象 ——
    /// 空白模型什么都不覆盖,解出来的正好是供应商自己的地址与 Key。
    /// </remarks>
    /// <param name="provider">供应商。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<ProviderCredential> ResolveProviderCredentialAsync(AiProvider provider,
        CancellationToken cancellationToken = default)
        => (await ResolveSavedCredentialAsync(new ResolvedModel(provider, new AiModelConfig()), true, cancellationToken)
            .ConfigureAwait(false)).Credential;

    /// <summary>换新令牌并落盘;HTTP 400/401/403 授权拒绝沿用旧令牌,网络及其他 HTTP 故障交给调用方。</summary>
    private async Task<OAuthTokens> RefreshAsync(AiProvider provider, OAuthConfig oauth, OAuthTokens tokens,
        long loginVersion, CancellationToken cancellationToken)
    {
        OAuthConfig fixedOAuth = oauth.Clone();
        string oauthSnapshot = JsonSerializer.Serialize(fixedOAuth);
        await RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 等闸期间别人可能已经刷过了
            (OAuthTokens? Tokens, long Version) snapshot = await GetTokenSnapshotAsync(provider.Id, cancellationToken).ConfigureAwait(false);
            OAuthTokens? latest = snapshot.Tokens;
            if (snapshot.Version != loginVersion || JsonSerializer.Serialize(provider.OAuth) != oauthSnapshot)
                throw new ApiKeySlotChangedException();
            if (latest is not null && !latest.NeedsRefresh)
            {
                return latest;
            }
            OAuthTokens fresh = await TokenClient.RefreshAsync(fixedOAuth, latest ?? tokens, cancellationToken)
                                                 .ConfigureAwait(false);
            if (TokenLoginVersion(provider.Id) != loginVersion || JsonSerializer.Serialize(provider.OAuth) != oauthSnapshot)
                throw new ApiKeySlotChangedException();
            await SaveTokensCoreAsync(provider.Id, fresh, false, cancellationToken, loginVersion).ConfigureAwait(false);
            context.Log.Info($"Refreshed the sign-in for '{provider.Name}'.");
            return fresh;
        }
        catch (OAuthException ex) // OAuthClient已把400/401/403授权响应解析为OAuth错误。
        {
            context.Log.Warn($"Refreshing the sign-in for '{provider.Name}' failed: {ex.Message}");
            if ((await GetTokenSnapshotAsync(provider.Id, cancellationToken).ConfigureAwait(false)).Version != loginVersion
                || JsonSerializer.Serialize(provider.OAuth) != oauthSnapshot)
            {
                throw new ApiKeySlotChangedException();
            }

            return tokens;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            // 传输处理器直接抛出带授权状态的HTTP异常时，保持相同回退语义。
            context.Log.Warn($"Refreshing the sign-in for '{provider.Name}' failed: {ex.Message}");
            if ((await GetTokenSnapshotAsync(provider.Id, cancellationToken).ConfigureAwait(false)).Version != loginVersion
                || JsonSerializer.Serialize(provider.OAuth) != oauthSnapshot)
            {
                throw new ApiKeySlotChangedException();
            }

            return tokens;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            and not HttpRequestException and not IOException and not TimeoutException and not ApiKeySlotChangedException)
        {
            context.Log.Warn($"Refreshing the sign-in for '{provider.Name}' failed: {ex.Message}");
            if ((await GetTokenSnapshotAsync(provider.Id, cancellationToken).ConfigureAwait(false)).Version != loginVersion
                || JsonSerializer.Serialize(provider.OAuth) != oauthSnapshot)
            {
                throw new ApiKeySlotChangedException();
            }

            return tokens;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    /// <summary>
    /// 为模型构造 <see cref="IChatClient" />(每次调用按继承链取最新凭据:API Key 或订阅令牌)。
    /// 返回的是"裸"客户端;Agent 模式的函数调用循环由调用方经
    /// <c>AsBuilder().UseFunctionInvocation()</c> 叠加。
    /// </summary>
    /// <param name="provider">已解出继承链的模型。</param>
    /// <param name="apiKeyOverride">设置页"测试"用:拿表单里还没保存的那把 Key 试一下。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<IChatClient> CreateClientAsync(ResolvedModel provider, string? apiKeyOverride = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ProviderCredential credential = apiKeyOverride is null
            ? await ResolveCredentialAsync(provider, cancellationToken: cancellationToken).ConfigureAwait(false)
            : ProviderCredential.Key(apiKeyOverride);
        return CreateClient(provider, credential);
    }

    /// <summary>Generate or edit one image using the selected image model and its current credentials.</summary>
    public async Task<DataContent> GenerateImageAsync(ResolvedModel model, string prompt,
        IReadOnlyList<DataContent>? inputs, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsImageModel(model)) throw new ArgumentException("The selected model is not an image model.", nameof(model));
        ProviderCredential credential = await ResolveCredentialAsync(model, cancellationToken: token).ConfigureAwait(false);
        EnsureBuiltinOAuthCredentialDestination(model, credential);
        ImageClient client = CreateOpenAiClient(model, credential, callerCountsRetries: false, noRetries: true).GetImageClient(model.Model);
        return await new ImageGenerationService(client, model.Model, model.Model.StartsWith("dall-e-", StringComparison.OrdinalIgnoreCase))
            .GenerateAsync(prompt, inputs, token).ConfigureAwait(false);
    }

    /// <summary>使用已经解析并固定的凭据建客户端,避免候选地址与后读到的新 Key 错配。</summary>
    /// <param name="provider">已解出继承链的模型。</param>
    /// <param name="credential">已固定的凭据。</param>
    /// <param name="callerCountsRetries">调用方自己按 Key 槽计数重试时关闭多槽供应商的 SDK 隐式重试。</param>
    internal IChatClient CreateClient(ResolvedModel provider, ProviderCredential credential, bool callerCountsRetries = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        EnsureBuiltinOAuthCredentialDestination(provider, credential);
        // OpenAI 系协议无论 Key 还是 access token 都走 Authorization: Bearer,一条路即可;
        // Anthropic 分岔(Key 是 x-api-key,令牌是 Bearer —— SDK 13.x 一律发 Bearer,Key 由插件改回),见下。
        string? secret = credential.Value;
        if (IsImageModel(provider))
        {
            return new OpenAiImageChatClient(CreateOpenAiClient(provider, credential, callerCountsRetries, noRetries: true)
                .GetImageClient(provider.Model), provider.Model.StartsWith("dall-e-", StringComparison.OrdinalIgnoreCase));
        }
        switch (provider.Protocol)
        {
            case ChatProtocol.OpenAiChatCompletions:
                return CreateOpenAiClient(provider, credential, callerCountsRetries).GetChatClient(provider.Model).AsIChatClient();

            case ChatProtocol.OpenAiResponses:
                // 工具循环每步整段重发,不拿 previous_response_id 续接 —— 中转站背后的上游多半没存上一步
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
                return new StatelessResponsesChatClient(
                    CreateOpenAiClient(provider, credential, callerCountsRetries).GetResponsesClient().AsIChatClient(provider.Model));
#pragma warning restore OPENAI001

            case ChatProtocol.AnthropicMessages:
                {
                    // 少数供应商按账户下发端点(Copilot 的企业账户),那时以登录带回来的为准
                    string baseUrl = ClientBaseUrl(EndpointOf(provider, credential), provider.Protocol);
                    // 中转站常在 Anthropic 流末尾补一行 OpenAI 习惯的 data: DONE,
                    // 而 SDK 对每个 data: 行无条件反序列化 —— 整轮回复会在最后一刻炸掉(见 SseRepairHandler)
                    List<DelegatingHandler> handlers = [new SseRepairHandler(ReportSseDrop)];
                    // SDK 13.x 把 ApiKey 也发成 Authorization: Bearer(12.x 是 x-api-key),只认 x-api-key 的
                    // 中转站因此 401:手填的 Key 改回 x-api-key。必须排在附加头之前 —— 用户明确配置的头最后落定,
                    // 不被它盖掉(见 AnthropicApiKeyHandler)。anthropic-version 照 SDK 的发(13.x 是 2026-10-21)。
                    if (!string.IsNullOrWhiteSpace(secret) && !credential.IsBearerToken)
                    {
                        handlers.Add(new AnthropicApiKeyHandler(secret));
                    }
                    if (credential.Headers is { Count: > 0 } extra)
                    {
                        handlers.Add(new ExtraHeadersHandler(extra));
                    }
                    // 属性为 init-only,按凭据形态分别构造:登录换来的短期令牌要走 AuthToken。
                    // 13.x 起 ApiKey 与 AuthToken 发出去都是 Authorization: Bearer,区别只在上面那个改写处理器挂不挂。
                    // 没有凭据时,只有官方 api.anthropic.com 交给 SDK 去读本机的 ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN
                    // (那两个变量本来就是给官方 API 的,只配环境变量直连官方是原设计);中转站、自行部署的网关、
                    // 换源后还没填新 Key 的地址一律把两者显式置空 —— SDK 里每个属性只关掉自己那个变量,
                    // 只置一个,另一个照样被读出来发过去(13.2.1 实测)。
                    bool noSdkRetries = callerCountsRetries && HasProviderKeyPool(provider);
                    AnthropicClient anthropic = !string.IsNullOrWhiteSpace(secret)
                        ? credential.IsBearerToken
                            ? new AnthropicClient { BaseUrl = baseUrl, AuthToken = secret, Handlers = handlers, MaxRetries = noSdkRetries ? 0 : null }
                            : new AnthropicClient { BaseUrl = baseUrl, ApiKey = secret, Handlers = handlers, MaxRetries = noSdkRetries ? 0 : null }
                        : IsOfficialAnthropicEndpoint(baseUrl)
                            ? new AnthropicClient { BaseUrl = baseUrl, Handlers = handlers, MaxRetries = noSdkRetries ? 0 : null }
                            : new AnthropicClient
                            {
                                BaseUrl = baseUrl,
                                ApiKey = null,
                                AuthToken = null,
                                Handlers = handlers,
                                MaxRetries = noSdkRetries ? 0 : null
                            };
                    return anthropic.AsIChatClient(provider.Model, provider.MaxTokens);
                }

            default:
                throw new InvalidOperationException($"Unknown protocol: {provider.Protocol}");
        }
    }

    /// <summary>已经报告过的收尾哨兵(见 <see cref="ReportSseDrop" />)。</summary>
    private readonly HashSet<string> _reportedSentinels = [];

    /// <summary>
    /// SSE 清洗丢了一行时怎么记。
    /// </summary>
    /// <remarks>
    /// 哨兵(<c>[DONE]</c> 之类)每轮对话都会来一次,报成每轮一条 Warning 就是纯噪音;
    /// 但完全不报又会丢掉"清洗生效了"这个凭据 —— 折中成<b>每种哨兵只报头一次</b>,而且降到 Info。
    /// 认不出来的载荷照旧每次都警告:那可能是中转站塞进来的错误信息,漏一条就变成无声的截断。
    /// </remarks>
    private void ReportSseDrop(string payload, bool sentinel)
    {
        if (!sentinel)
        {
            context.Log.Warn($"SSE repair: dropped an unparsable data line — {payload}");
            return;
        }
        // 清洗跑在后台的搬运任务上,这个集合会被多个流并发碰到
        lock (_reportedSentinels)
        {
            if (!_reportedSentinels.Add(payload))
            {
                return;
            }
        }
        context.Log.Info(
            $"SSE repair: this endpoint ends its stream with a non-Anthropic sentinel ({payload}); dropping it from here on.");
    }

    /// <summary>Anthropic 的思考预算下限(协议要求 <c>budget_tokens ≥ 1024</c>)。</summary>
    private const int AnthropicMinThinkingBudget = 1024;

    /// <summary>
    /// 把模型的思考档位翻译进请求选项。两条路并存,因为两家的适配器认的东西不一样:
    /// <list type="bullet">
    /// <item><c>ChatOptions.Reasoning</c> —— OpenAI 系适配器认(映射成 reasoning effort / summary)。</item>
    /// <item>
    /// <c>RawRepresentationFactory</c> 返回 <see cref="MessageCreateParams" /> —— Anthropic 适配器
    /// 不认前者(12.40.0 的 <c>AsIChatClient</c> 里没有任何 reasoning 映射),只能把
    /// <c>thinking</c> 直接塞进请求体。
    /// </item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <b>实测约束(2026-08-15,Anthropic 12.40.0,本地假端点抓包核对过流式与非流式两条路)</b>:
    /// <c>MessageCreateParams</c> 的 <c>required</c> 成员(<c>MaxTokens</c> / <c>Model</c> / <c>Messages</c>)
    /// 在 raw 对象里必然有值,而适配器<b>只覆盖 Messages</b> —— MaxTokens 与 Model 以 raw 里的为准,
    /// <c>ChatOptions.MaxOutputTokens</c> 和 <c>AsIChatClient(model, maxTokens)</c> 都被无视。
    /// 所以这里必须把真实的模型与输出上限一并填进去,否则请求会带着占位值发出去。
    /// 另:开思考时 Anthropic 要求 <c>max_tokens > budget_tokens</c>。
    /// temperature 不用操心:SDK 13.x 的 <c>MessageCreateParams</c> 已经没有这个字段,
    /// Anthropic 协议上 <see cref="ApplyEndpointQuirks" /> 也会把它从选项里抹掉。
    /// </remarks>
    public static void ApplyReasoning(ChatOptions options, ResolvedModel provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Reasoning == ReasoningLevel.Default)
        {
            return;
        }

        options.Reasoning = provider.Reasoning == ReasoningLevel.Off
            ? new ReasoningOptions { Effort = ReasoningEffort.None, Output = ReasoningOutput.None }
            : new ReasoningOptions
            {
                Effort = provider.Reasoning switch
                {
                    ReasoningLevel.Low => ReasoningEffort.Low,
                    ReasoningLevel.High => ReasoningEffort.High,
                    ReasoningLevel.ExtraHigh or ReasoningLevel.Max => ReasoningEffort.ExtraHigh,
                    _ => ReasoningEffort.Medium
                },
                // 要的就是把思考过程显示出来,能给全文就别只给摘要
                Output = ReasoningOutput.Full
            };

        // 通用抽象最高只有 ExtraHigh;max 必须交给 OpenAI 的原生请求,避免降成 xhigh。
        if (provider.Reasoning == ReasoningLevel.Max && provider.Protocol is
            ChatProtocol.OpenAiChatCompletions or ChatProtocol.OpenAiResponses)
        {
            options.Reasoning = null;
#pragma warning disable OPENAI001
            options.RawRepresentationFactory = provider.Protocol == ChatProtocol.OpenAiResponses
                ? _ => new OpenAI.Responses.CreateResponseOptions
                {
                    ReasoningOptions = new OpenAI.Responses.ResponseReasoningOptions
                    {
                        ReasoningEffortLevel = OpenAI.Responses.ResponseReasoningEffortLevel.Max,
                        ReasoningSummaryVerbosity = OpenAI.Responses.ResponseReasoningSummaryVerbosity.Detailed
                    }
                }
                : _ => new OpenAI.Chat.ChatCompletionOptions
                {
                    ReasoningEffortLevel = new OpenAI.Chat.ChatReasoningEffortLevel("max")
                };
#pragma warning restore OPENAI001
            return;
        }

        if (provider.Protocol != ChatProtocol.AnthropicMessages)
        {
            return;
        }

        (int budget, int maxTokens) = AnthropicThinkingBudget(provider);
        ThinkingConfigParam thinking = provider.Reasoning == ReasoningLevel.Off
            ? new ThinkingConfigDisabled()
            : new ThinkingConfigEnabled(budget);
        options.RawRepresentationFactory = _ => new MessageCreateParams
        {
            // Messages 会被适配器覆盖成真正的对话;MaxTokens/Model 不会,必须给真值(见 remarks)
            Messages = [],
            MaxTokens = maxTokens,
            Model = provider.Model,
            Thinking = thinking
        };
    }

    /// <summary>
    /// 把这一家端点的"脾气"应用到请求上:不收的参数摘掉、该关的开关关掉。
    /// </summary>
    /// <remarks>
    /// 订阅型的私有后端常常只是标准协议的<b>受限子集</b>,多发一个字段就整轮 400,
    /// 而且一次只告诉你一个。所以这些差异全部收在<b>目录数据</b>里
    /// (<see cref="AiProvider.UnsupportedParameters" /> / <see cref="AiProvider.StoreResponses" />),
    /// 由这里统一施加 —— 再发现一条只要加一行数据,不必改代码。
    /// <para>
    /// 唯一一条<b>按协议</b>而不是按目录数据的规则:Anthropic 协议上一律不发 temperature / top_p / top_k。
    /// Anthropic SDK 13.x 把 <c>anthropic-version</c> 换成了 <c>2026-10-21</c>,这一版的接口收到这三个字段
    /// 一律 400;SDK 也随之从 <c>MessageCreateParams</c> 删掉了它们,<c>IChatClient</c> 适配器不再映射。
    /// 这里显式抹掉,不靠适配器"恰好不映射" —— 设置页对 Anthropic 模型禁用这两格,说的就是这件事。
    /// 设置里存着的值不动,换回 OpenAI 系协议照常生效。
    /// </para>
    /// </remarks>
    /// <param name="options">这一轮的请求选项;<b>就地修改</b>。</param>
    /// <param name="provider">已解出继承链的模型。</param>
    public static void ApplyEndpointQuirks(ChatOptions options, ResolvedModel provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Protocol == ChatProtocol.AnthropicMessages)
        {
            options.Temperature = null;
            options.TopP = null;
            options.TopK = null;
        }
        // 按目录现读,不用供应商身上那份快照 —— 否则每加一条新规则,
        // 已经连上的用户都得重新登录一次才拿得到(见 EndpointQuirks)
        var quirks = EndpointQuirks.Of(provider.Provider);
        ApplyResponseStore(options, provider, quirks);
        foreach (string raw in quirks.UnsupportedParameters
                                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // 认不出来的名字直接跳过:目录里写错一个字,不该让整轮对话崩掉
            switch (raw.Trim().ToLowerInvariant())
            {
                case "max_output_tokens" or "max_tokens":
                    options.MaxOutputTokens = null;
                    break;
                case "temperature":
                    options.Temperature = null;
                    break;
                case "top_p":
                    options.TopP = null;
                    break;
                case "stop" or "stop_sequences":
                    options.StopSequences = null;
                    break;
                case "frequency_penalty":
                    options.FrequencyPenalty = null;
                    break;
                case "presence_penalty":
                    options.PresencePenalty = null;
                    break;
                case "seed":
                    options.Seed = null;
                    break;
            }
        }
    }

    /// <summary>
    /// 明确要求"别存这一轮"时,往 Responses 请求里塞 <c>store: false</c>。
    /// </summary>
    /// <remarks>
    /// 只对 <see cref="ChatProtocol.OpenAiResponses" /> 有意义(<c>store</c> 是那套协议的字段)。
    /// 走的是 <c>RawRepresentationFactory</c>,与 Anthropic 那条思考预算的路子同一个道理:
    /// <c>ChatOptions</c> 上没有对应的抽象属性,只能把原生请求对象递下去。
    /// </remarks>
    private static void ApplyResponseStore(ChatOptions options, ResolvedModel provider, EndpointQuirks quirks)
    {
        if (provider.Protocol != ChatProtocol.OpenAiResponses || quirks.StoreResponses)
        {
            return;
        }
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
        Func<IChatClient, object?>? previousFactory = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            CreateResponseOptions raw = previousFactory?.Invoke(client) as CreateResponseOptions
                ?? new CreateResponseOptions();
            raw.StoredOutputEnabled = false;
            return raw;
        };
#pragma warning restore OPENAI001
    }

    /// <summary>
    /// 算 Anthropic 的思考预算与配套的输出上限:预算不得低于协议下限,也必须小于 max_tokens
    /// (给正文留够 <see cref="AnthropicMinThinkingBudget" /> 的余量);
    /// 用户把输出上限设得过小时,把上限抬到刚好放得下,而不是悄悄不思考。
    /// </summary>
    private static (int Budget, int MaxTokens) AnthropicThinkingBudget(ResolvedModel provider)
    {
        int desired = provider.Reasoning switch
        {
            ReasoningLevel.Low => 2048,
            ReasoningLevel.High => 16384,
            ReasoningLevel.ExtraHigh => 32768,
            ReasoningLevel.Max => 65536,
            _ => 4096
        };
        int budget = Math.Clamp(desired, AnthropicMinThinkingBudget,
            Math.Max(AnthropicMinThinkingBudget, provider.MaxTokens - AnthropicMinThinkingBudget));
        return (budget, Math.Max(provider.MaxTokens, budget + AnthropicMinThinkingBudget));
    }

    /// <summary>
    /// 这次请求该打到哪儿:登录带回来的端点优先,没有才用供应商配置里的。
    /// </summary>
    /// <remarks>
    /// 少数供应商按账户下发地址(Copilot 的企业账户与个人账户不是同一个),而那个地址
    /// <b>只有登录之后才知道</b> —— 没有这一层,就只能让用户手填一个他根本无从知道的值。
    /// </remarks>
    private static string EndpointOf(ResolvedModel provider, ProviderCredential credential)
        => string.IsNullOrWhiteSpace(credential.BaseUrl) ? provider.BaseUrl : credential.BaseUrl;

    private static void EnsureBuiltinOAuthCredentialDestination(ResolvedModel model, ProviderCredential credential)
    {
        if (model.Provider.Auth != AuthMethod.Subscription || model.Config.HasOwnApiKey
            || string.IsNullOrEmpty(credential.Value)
            || model.Provider.CatalogId is not { } catalog
            || !BuiltInOAuthOrigins.TryGetValue(catalog, out Uri? trusted)
            || (string.Equals(catalog, "github-copilot", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(credential.BaseUrl)))
        {
            return;
        }
        if (!Uri.TryCreate(EndpointOf(model, credential), UriKind.Absolute, out Uri? target)
            || !string.Equals(target.Scheme, trusted.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.IdnHost, trusted.IdnHost, StringComparison.OrdinalIgnoreCase)
            || target.Port != trusted.Port)
        {
            throw new BuiltinOAuthHostMismatchException();
        }
    }

    /// <summary>官方 Anthropic API 的主机:没有凭据时只有它可以用本机的环境变量(见 <see cref="CreateClient" />)。</summary>
    private const string OfficialAnthropicHost = "api.anthropic.com";

    /// <summary>
    /// 是不是官方 Anthropic API(<c>https://api.anthropic.com</c>,默认端口)。
    /// 只认 https 与默认端口:明文 http 或别的端口都不是那个端点,环境变量里的 Key 不往那儿送。
    /// </summary>
    internal static bool IsOfficialAnthropicEndpoint(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.IdnHost, OfficialAnthropicHost, StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443;

    internal static string ClientBaseUrl(string endpoint, ChatProtocol protocol)
    {
        string baseUrl = endpoint.TrimEnd('/');
        // Anthropic SDK 追加 /v1；候选判重必须与客户端使用同一基地址。
        return protocol == ChatProtocol.AnthropicMessages && baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl[..^3].TrimEnd('/') : baseUrl;
    }

    private static readonly ClientRetryPolicy NoSdkRetries = new(0);

    private bool HasProviderKeyPool(ResolvedModel model)
        => model.Provider.Auth == AuthMethod.ApiKey && !model.Config.HasOwnApiKey
            && SameOrigin(model.BaseUrl, model.Provider.BaseUrl)
            && (ProviderApiKeyIds(model.Provider).Skip(1).Any()
                || model.Provider.AdditionalApiKeyIds.Any(static id => Guid.TryParseExact(id, "N", out _)));

    internal static bool IsImageModel(ResolvedModel model)
        => model.Protocol == ChatProtocol.OpenAiImages
            || (model.Protocol is ChatProtocol.OpenAiChatCompletions or ChatProtocol.OpenAiResponses
                && (model.Model.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase)
                    || model.Model.StartsWith("dall-e-", StringComparison.OrdinalIgnoreCase)));

    private OpenAIClient CreateOpenAiClient(ResolvedModel provider, ProviderCredential credential, bool callerCountsRetries, bool noRetries = false)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(ClientBaseUrl(EndpointOf(provider, credential), provider.Protocol)) };
        // 多槽的重试由聊天回路统一计数;SDK 隐式重试会把一次 429 重试乘成多次 HTTP。
        if (noRetries || (callerCountsRetries && HasProviderKeyPool(provider))) options.RetryPolicy = NoSdkRetries;
        if (credential.Headers is { Count: > 0 } headers)
        {
            options.AddPolicy(new ExtraHeadersPolicy(headers), PipelinePosition.PerCall);
        }
        return new OpenAIClient(
            // OpenAI SDK 要求凭据非空;Ollama 等本地服务无鉴权,给占位值即可。
            // access token 与 API Key 在这条路上是一回事 —— SDK 都发成 Authorization: Bearer。
            new ApiKeyCredential(string.IsNullOrWhiteSpace(credential.Value) ? "not-needed" : credential.Value),
            options);
    }

    private static string SecretName(string ownerId) => LegacySettingsMigration.SecretName(ownerId);

    /// <summary>订阅登录的令牌组存在哪个机密键下。与 API Key 分开,退出登录时不误伤手填的 Key。</summary>
    private static string TokenSecretName(string providerId) => $"oauth:{providerId}";
}
