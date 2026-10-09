using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 故障转移候选怎么算:链里还有活条目就按自定义链走,空链(或全是死条目)才自动发现;
/// 失败者自己不出现在名单里。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class FailoverChainTests
{
    private static AiProvider Provider(string name, string? catalogId, params (string Id, string Model)[] models)
        => new()
        {
            Name = name,
            CatalogId = catalogId,
            BaseUrl = "http://127.0.0.1:1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [.. models.Select(m => new AiModelConfig { Id = m.Id, Model = m.Model })]
        };

    private static ResolvedModel Resolved(AiSettings settings, string id)
        => settings.FindModel(id) ?? throw new InvalidOperationException($"fixture: {id}");

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public void Review_AutomaticCandidatesRequireAnIndependentConnection(bool ownKey, bool otherEndpoint, bool otherProtocol)
    {
        AiProvider provider = Provider("one", "openai", ("a", "m"), ("b", "m"));
        AiModelConfig alternative = provider.Models[1];
        alternative.HasOwnApiKey = ownKey;
        if (otherEndpoint) alternative.BaseUrlOverride = "http://127.0.0.1:2";
        if (otherProtocol) alternative.Protocol = ChatProtocol.OpenAiResponses;
        var settings = new AiSettings { Providers = [provider] };
        List<ResolvedModel> candidates = settings.FailoverCandidates(Resolved(settings, "a"));
        if (ownKey || otherEndpoint || otherProtocol)
            CollectionAssert.AreEqual(new[] { "b" }, candidates.Select(model => model.Id).ToArray());
        else Assert.IsEmpty(candidates, "同端点、同协议、同凭据的重复模型不是备用接入");
        settings.FailoverChain = [new FailoverEntry { ModelId = "b" }];
        Assert.AreEqual("b", settings.FailoverCandidates(Resolved(settings, "a")).Single().Id,
            "显式排链仍尊重用户选择");
    }

    [TestMethod]
    [DataRow("https://API.example.com:443/v1/", false)]
    [DataRow("https://api.example.com/v1", false)]
    [DataRow("https://api.example.com/V1", true)]
    [DataRow("https://api.example.com/v2", true)]
    public void Review_AutomaticCandidatesNormalizeEndpointIdentity(string endpoint, bool independent)
    {
        AiProvider provider = Provider("one", "openai", ("a", "m"), ("b", "m"));
        provider.BaseUrl = "https://api.example.com/v1";
        provider.Models[1].BaseUrlOverride = endpoint;
        var settings = new AiSettings { Providers = [provider] };
        CollectionAssert.AreEqual(independent ? new[] { "b" } : Array.Empty<string>(),
            settings.FailoverCandidates(Resolved(settings, "a")).Select(model => model.Id).ToArray());
    }

    [TestMethod]
    [DataRow("https://api.example.com", "https://api.example.com/v1/")]
    [DataRow("https://api.example.com/V1", "https://api.example.com/")]
    public void Review_AnthropicAutomaticCandidatesUseActualClientBaseUrl(string endpoint, string alternate)
    {
        AiProvider provider = Provider("one", "anthropic", ("a", "m"), ("b", "m"));
        provider.DefaultProtocol = ChatProtocol.AnthropicMessages;
        provider.BaseUrl = endpoint;
        provider.Models[1].BaseUrlOverride = alternate;
        var settings = new AiSettings { Providers = [provider] };
        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "a")));
        provider.Models[1].BaseUrlOverride = "https://api.example.com/other";
        CollectionAssert.AreEqual(new[] { "b" }, settings.FailoverCandidates(Resolved(settings, "a")).Select(model => model.Id).ToArray());
    }

    [TestMethod]
    public void EmptyChainDiscoversSameCatalogAndModel_IgnoringCase()
    {
        AiProvider first = Provider("openai-1", "openai", ("a", "gpt-4o"));
        AiProvider second = Provider("openai-2", "openai", ("b", "GPT-4O"));
        AiProvider other = Provider("anthropic", "anthropic", ("c", "gpt-4o"));
        var settings = new AiSettings { Providers = [first, second, other] };

        List<ResolvedModel> candidates = settings.FailoverCandidates(Resolved(settings, "a"));

        Assert.HasCount(1, candidates, "同目录 + 同模型(忽略大小写)才算;同名模型在别家目录下不算");
        Assert.AreEqual("b", candidates[0].Id);
        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "c")),
            "没有目录 id 的自配接入与有目录 id 的互不相干");
    }

    [TestMethod]
    public void EmptyChainNeverDiscoversBetweenManualProviders()
    {
        // 两个无目录 id 的自定义接入可能恰好同模型名,却根本不是"同一家多加了一份" ——
        // 没人把它们排进链,就不该在主站失败后被自动发过去;要跨请显式排链。
        AiProvider first = Provider("relay-1", null, ("a", "gpt-4o"));
        AiProvider second = Provider("relay-2", null, ("b", "gpt-4o"));
        var settings = new AiSettings { Providers = [first, second] };

        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "a")),
            "CatalogId 为空的自定义接入不参与自动发现");
        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "b")),
            "反方向同样:失败者是自定义接入时也不自动找同名模型");
    }

    [TestMethod]
    public void EmptyChainNeverCrossesCustomCatalogInstances_ButDiscoversRealCatalog()
    {
        foreach (string catalog in new[] { "custom-openai", "custom-anthropic", "custom-oauth" })
        {
            AiProvider first = Provider("relay-a", catalog, ("a", "gpt-4o"));
            first.BaseUrl = "https://relay-a.example/v1";
            AiProvider second = Provider("relay-b", catalog, ("b", "gpt-4o"));
            second.BaseUrl = "https://relay-b.example/v1";
            AiProvider official = Provider("openai-a", "openai", ("c", "gpt-4o"));
            AiProvider officialBackup = Provider("openai-b", "openai", ("d", "GPT-4O"));
            var settings = new AiSettings { Providers = [first, second, official, officialBackup] };

            Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "a")),
                $"{catalog} 的不同中转站同模型不能自动互切");
            Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "b")),
                $"{catalog} 的不同中转站反向也不能自动互切");
            List<ResolvedModel> officialCandidates = settings.FailoverCandidates(Resolved(settings, "c"));
            Assert.HasCount(1, officialCandidates, "真实目录的同名模型仍可自动互切");
            Assert.AreEqual("d", officialCandidates[0].Id);

            settings.FailoverChain = [new FailoverEntry { ModelId = "b" }];
            List<ResolvedModel> explicitCandidates = settings.FailoverCandidates(Resolved(settings, "a"));
            Assert.HasCount(1, explicitCandidates, "显式排链仍允许跨不同自定义中转站");
            Assert.AreEqual("b", explicitCandidates[0].Id);
        }
    }

    [TestMethod]
    public void AzureResourcesRequireExplicitChain_EvenWhenCatalogAndModelMatch()
    {
        AiProvider first = Provider("azure-a", "azure-openai", ("a", "gpt-4o"));
        first.BaseUrl = "https://resource-a.openai.azure.com";
        AiProvider second = Provider("azure-b", "AZURE-OPENAI", ("b", "GPT-4O"));
        second.BaseUrl = "https://resource-b.openai.azure.com";
        var settings = new AiSettings { Providers = [first, second] };

        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "a")),
            "空链不能把 Azure 的其它资源当作同目录备用站");
        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "b")),
            "大小写不同的目录 id 和反向故障转移也不能绕过限制");

        settings.FailoverChain = [new FailoverEntry { ModelId = "deleted" }];
        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "a")),
            "只有失效条目的链回退自动发现时也不能跨 Azure 资源");

        settings.FailoverChain = [new FailoverEntry { ModelId = "b" }];
        List<ResolvedModel> candidates = settings.FailoverCandidates(Resolved(settings, "a"));
        Assert.HasCount(1, candidates, "显式链仍可切换 Azure 资源");
        Assert.AreEqual("b", candidates[0].Id);
    }

    [TestMethod]
    public void ChainOfOnlyDeletedEntriesDiscoversLikeAnEmptyChain()
    {
        // 删模型时全局设置窗口没开着,链里留下的全是死 id —— 这等同没人排过链:
        // 不放行自动发现的话,故障转移会在一条永远凑不齐的链上报"没有下一站",
        // 同目录同型号的健康接入明明排得上号(review②)。
        AiProvider first = Provider("openai-1", "openai", ("a", "gpt-4o"));
        AiProvider second = Provider("openai-2", "openai", ("b", "gpt-4o"));
        var settings = new AiSettings
        {
            Providers = [first, second],
            FailoverChain = [new FailoverEntry { ModelId = "deleted" }]
        };

        List<ResolvedModel> candidates = settings.FailoverCandidates(Resolved(settings, "b"));

        Assert.HasCount(1, candidates, "链里全是已删模型的死条目 ≈ 空链,交给自动发现");
        Assert.AreEqual("a", candidates[0].Id);

        // 对照:链里还剩活条目时仍按链走 —— 被排除后为空就是就地停下,
        // 不许悄悄改成自动发现(那是用户明确排过的链)。
        settings.FailoverChain =
        [
            new FailoverEntry { ModelId = "b" },
            new FailoverEntry { ModelId = "deleted" }
        ];
        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "b")),
            "链里有活条目(哪怕只剩失败者自己):链内走完就停,不回退自动发现");
    }

    [TestMethod]
    public void EmptyChainExcludesTheFailedOne()
    {
        AiProvider first = Provider("one", "openai", ("a", "gpt-4o"));
        AiProvider second = Provider("two", "openai", ("b", "gpt-4o"));
        var settings = new AiSettings { Providers = [first, second] };

        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "a"))
                .Where(m => m.Id == "a"),
            "失败者自己不进候选,否则切换等于原地打转");
    }

    [TestMethod]
    public void CustomChainFollowsListOrderAndSkipsStaleDuplicatesAndSelf()
    {
        AiProvider provider = Provider("p1", null, ("a", "m1"), ("b", "m2"));
        AiProvider other = Provider("p2", null, ("c", "m3"));
        var settings = new AiSettings
        {
            Providers = [provider, other],
            FailoverChain =
            [
                new FailoverEntry { ModelId = "c" },
                new FailoverEntry { ModelId = "gone" }, // 已删的模型 —— 静默跳过
                new FailoverEntry { ModelId = "c" },    // 重复 —— 只算一次
                new FailoverEntry { ModelId = "a" },    // 失败者自己 —— 剔掉
                new FailoverEntry { ModelId = "b" }
            ]
        };

        List<ResolvedModel> candidates = settings.FailoverCandidates(Resolved(settings, "a"));

        Assert.HasCount(2, candidates, "重复、已删、失败者自己各被去掉一个");
        Assert.AreEqual("c", candidates[0].Id, "链序 = 优先序");
        Assert.AreEqual("b", candidates[1].Id);
    }

    [TestMethod]
    public async Task ChainSurvivesSaveAndLoad()
    {
        using var context = new TestPluginContext();
        var provider = Provider("p", null, ("a", "m"));
        var settings = new AiSettings
        {
            Providers = [provider],
            FailoverChain = [new FailoverEntry { ModelId = "a" }]
        };
        AiSettingsStore store = new(context);

        await store.SaveAsync(settings);
        AiSettings reloaded = await store.LoadAsync();

        Assert.HasCount(1, reloaded.FailoverChain, "链要落盘,重启后还在(它表达的是用户的排布,不是此刻的状态)");
        Assert.AreEqual("a", reloaded.FailoverChain[0].ModelId);
    }

    [TestMethod]
    public void CustomChainWinsOverCatalogDiscovery()
    {
        // 链里明确点了别家的模型:哪怕目录/模型 id 都不同,也按用户点的来
        AiProvider first = Provider("p1", "openai", ("a", "gpt-4o"));
        AiProvider second = Provider("p2", "openai", ("b", "gpt-4o"));
        AiProvider third = Provider("p3", null, ("c", "claude"));
        var settings = new AiSettings
        {
            Providers = [first, second, third],
            FailoverChain = [new FailoverEntry { ModelId = "c" }]
        };

        List<ResolvedModel> candidates = settings.FailoverCandidates(Resolved(settings, "a"));

        Assert.HasCount(1, candidates, "自定义链非空就不做自动发现");
        Assert.AreEqual("c", candidates[0].Id);
    }

    [TestMethod]
    public void ChainNeverRevisitsStationsTriedEarlierInThisTurn()
    {
        // A→B→C:B 失败时 A 的 60 秒冷却恰好过期 —— 候选也不能排回 A:
        // 跳数被原地烧掉,健康的 C 永远轮不上(冷却的短窗认不出"本回合试过",
        // 得由调用方把试挂过的站整轮带进来)。
        AiProvider provider = Provider("p", null, ("a", "m1"), ("b", "m2"), ("c", "m3"));
        var settings = new AiSettings
        {
            Providers = [provider],
            FailoverChain =
            [
                new FailoverEntry { ModelId = "a" },
                new FailoverEntry { ModelId = "b" },
                new FailoverEntry { ModelId = "c" }
            ]
        };

        List<ResolvedModel> candidates =
            settings.FailoverCandidates(Resolved(settings, "b"), triedBefore: ["a"]);

        Assert.HasCount(1, candidates, "失败者自己与本轮已试挂的 A 都剔掉,只剩还没试过的 C");
        Assert.AreEqual("c", candidates[0].Id);
    }

    [TestMethod]
    public void AutoDiscovery_AlsoSkipsStationsTriedEarlierInThisTurn()
    {
        AiProvider first = Provider("openai-1", "openai", ("a", "gpt-4o"));
        AiProvider second = Provider("openai-2", "openai", ("b", "gpt-4o"));
        var settings = new AiSettings { Providers = [first, second] };

        Assert.IsEmpty(settings.FailoverCandidates(Resolved(settings, "b"), triedBefore: ["a"]),
            "自动发现同样整轮排除:同目录的 A 在本回合已经试挂过");
    }
}
