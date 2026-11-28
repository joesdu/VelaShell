using System.Text.Json;
using VelaShell.PluginSdk.Secrets;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 旧版(2026-08-16 之前)的扁平接入配置:一条 = 一个模型,地址 / 协议 / Key 都长在它身上。
/// 只在迁移时反序列化用,新代码不要碰。
/// </summary>
public sealed class LegacyProviderConfig
{
    /// <summary>旧接入标识；迁移后仍用作模型 ID，以保留当前选择。</summary>
    public string Id { get; set; } = "";
    /// <summary>用户为旧接入设置的显示名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>该模型原来使用的请求协议，可能与合并后的供应商默认协议不同。</summary>
    public ChatProtocol Protocol { get; set; } = ChatProtocol.OpenAiChatCompletions;
    /// <summary>原请求基地址；迁移时用于归并供应商，并保留模型自己的地址差异。</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>请求中发送给供应商的模型名，不是本地配置 ID。</summary>
    public string Model { get; set; } = "";
    /// <summary>旧配置允许一次回复生成的最大 token 数。</summary>
    public int MaxTokens { get; set; } = 8192;
    /// <summary>旧配置声明的模型输入 token 上限。</summary>
    public int MaxInputTokens { get; set; } = 128000;
    /// <summary>旧接入是否启用提示词缓存。</summary>
    public bool PromptCaching { get; set; } = true;
    /// <summary>迁移到模型配置的采样温度；为空表示未指定。</summary>
    public float? Temperature { get; set; }
    /// <summary>旧配置的核采样概率阈值；为空表示未指定。</summary>
    public float? TopP { get; set; }
    /// <summary>旧格式中配置的生成停止序列文本。</summary>
    public string StopSequences { get; set; } = "";
    /// <summary>该接入附带的可选系统提示词。</summary>
    public string? SystemPrompt { get; set; }
    /// <summary>每百万普通输入 token 的配置单价。</summary>
    public double InputPricePerMillion { get; set; }
    /// <summary>每百万输出 token 的配置单价。</summary>
    public double OutputPricePerMillion { get; set; }
    /// <summary>每百万命中缓存的输入 token 的配置单价。</summary>
    public double CachedInputPricePerMillion { get; set; }
    /// <summary>旧接入为该模型选择的推理强度。</summary>
    public ReasoningLevel Reasoning { get; set; } = ReasoningLevel.Default;
}

/// <summary>
/// 把旧版扁平接入列表折成"供应商 → 模型"两层。
/// </summary>
/// <remarks>
/// 规则(全部为了让老用户升级后<b>行为不变</b>):
/// <list type="bullet">
/// <item>按基地址分组(忽略大小写、尾斜杠与尾部 <c>/v1</c>);同组合成一个供应商。
/// 组内地址若与供应商地址不完全一致,该模型记 <see cref="AiModelConfig.BaseUrlOverride" />,请求打到哪儿一个字节都不变。</item>
/// <item>旧接入 id 原样成为模型 id —— 因而 <c>ActiveProviderId</c> 直接就是新的 <c>ActiveModelId</c>,聊天面板不用重选。</item>
/// <item>供应商默认协议 = 组内最多的那种;不同的模型单独覆盖。</item>
/// <item>Key:组内第一把非空 Key 提到供应商名下(新 id,复制一份);其它模型的 Key 与之相同就删掉自己那份改继承,
/// 不同(或供应商没有而它有)则标 <see cref="AiModelConfig.HasOwnApiKey" />,机密原地不动。</item>
/// <item>供应商名:组内名字全一样就用它;否则用地址的主机名。</item>
/// </list>
/// </remarks>
public static class LegacySettingsMigration
{
    /// <summary>JSON 形状探测:<c>Providers</c> 数组里的元素长着 <c>Model</c>/<c>BaseUrl</c> 而没有 <c>Models</c>,就是旧格式。</summary>
    public static bool IsLegacyShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(nameof(AiSettings.Providers), out JsonElement providers)
            || providers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (JsonElement item in providers.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (item.TryGetProperty(nameof(AiProvider.Models), out _))
            {
                return false;
            }
            if (item.TryGetProperty(nameof(LegacyProviderConfig.Model), out _)
                || item.TryGetProperty(nameof(LegacyProviderConfig.Protocol), out _))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 分组折叠(纯结构部分,不碰机密)。返回每个供应商对应的旧条目,供 <see cref="MigrateSecretsAsync" /> 处理 Key。
    /// </summary>
    public static List<(AiProvider Provider, List<LegacyProviderConfig> Members)> Group(IReadOnlyList<LegacyProviderConfig> legacy)
    {
        var groups = new List<(AiProvider Provider, List<LegacyProviderConfig> Members)>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LegacyProviderConfig item in legacy)
        {
            string key = GroupKey(item.BaseUrl);
            if (!byKey.TryGetValue(key, out int index))
            {
                index = groups.Count;
                byKey[key] = index;
                groups.Add((new AiProvider { BaseUrl = item.BaseUrl.Trim() }, []));
            }
            groups[index].Members.Add(item);
        }

        foreach ((AiProvider provider, List<LegacyProviderConfig> members) in groups)
        {
            provider.DefaultProtocol = members
                .GroupBy(m => m.Protocol)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => members.FindIndex(m => m.Protocol == g.Key))
                .First().Key;
            provider.Name = members.All(m => string.Equals(m.Name, members[0].Name, StringComparison.Ordinal))
                ? members[0].Name
                : HostOf(provider.BaseUrl);
            foreach (LegacyProviderConfig m in members)
            {
                bool sameUrl = string.Equals(m.BaseUrl.Trim(), provider.BaseUrl, StringComparison.Ordinal);
                provider.Models.Add(new AiModelConfig
                {
                    Id = string.IsNullOrEmpty(m.Id) ? Guid.NewGuid().ToString("N") : m.Id,
                    // 名字跟供应商重了就留空,界面上显示模型 id,免得列表里“Claude › Claude”
                    Name = string.Equals(m.Name, provider.Name, StringComparison.Ordinal) ? "" : m.Name,
                    Model = m.Model,
                    Protocol = m.Protocol == provider.DefaultProtocol ? null : m.Protocol,
                    BaseUrlOverride = sameUrl ? null : m.BaseUrl.Trim(),
                    MaxTokens = m.MaxTokens,
                    MaxInputTokens = m.MaxInputTokens,
                    PromptCaching = m.PromptCaching,
                    Temperature = m.Temperature,
                    TopP = m.TopP,
                    StopSequences = m.StopSequences,
                    SystemPrompt = m.SystemPrompt,
                    InputPricePerMillion = m.InputPricePerMillion,
                    OutputPricePerMillion = m.OutputPricePerMillion,
                    CachedInputPricePerMillion = m.CachedInputPricePerMillion,
                    Reasoning = m.Reasoning
                });
            }
        }
        return groups;
    }

    /// <summary>
    /// 按 <see cref="Group" /> 的结果整理机密:把组内第一把 Key 提到供应商名下,能继承的模型删掉自己那份。
    /// </summary>
    public static async Task MigrateSecretsAsync(
        List<(AiProvider Provider, List<LegacyProviderConfig> Members)> groups,
        ISecretsApi secrets,
        CancellationToken cancellationToken = default)
    {
        foreach ((AiProvider provider, List<LegacyProviderConfig> members) in groups)
        {
            var keys = new List<(AiModelConfig Model, string? Key)>(members.Count);
            for (int i = 0; i < members.Count; i++)
            {
                string? key = await secrets.GetAsync(SecretName(provider.Models[i].Id), cancellationToken).ConfigureAwait(false);
                keys.Add((provider.Models[i], string.IsNullOrEmpty(key) ? null : key));
            }
            string? providerKey = keys.Find(k => k.Key is not null).Key;
            if (providerKey is not null)
            {
                await secrets.SetAsync(SecretName(provider.Id), providerKey, cancellationToken).ConfigureAwait(false);
            }
            foreach ((AiModelConfig model, string? key) in keys)
            {
                if (key is null || string.Equals(key, providerKey, StringComparison.Ordinal))
                {
                    // 没 Key 的直接继承(同一地址,继承供应商那把也是对的);同 Key 的删掉自己那份改继承
                    model.HasOwnApiKey = false;
                    if (key is not null)
                    {
                        await secrets.DeleteAsync(SecretName(model.Id), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    model.HasOwnApiKey = true;
                }
            }
        }
    }

    /// <summary>机密键(与 <see cref="AiSettingsStore" /> 保持一致)。</summary>
    internal static string SecretName(string ownerId) => $"apikey:{ownerId}";

    private static string GroupKey(string baseUrl)
    {
        string key = baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
        if (key.EndsWith("/v1", StringComparison.Ordinal))
        {
            key = key[..^3].TrimEnd('/');
        }
        return key;
    }

    private static string HostOf(string baseUrl)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : baseUrl;
}
