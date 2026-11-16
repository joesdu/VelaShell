using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// models.dev 连不上(被墙、没外网、超时)时,拉取模型照样以上游清单为准落成模型,不被它挡住,
/// 也不先干等它;界面据 <see cref="ModelPullResult.CatalogueUnavailable" /> 补一句说明。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ModelPullCatalogueTests
{
    /// <summary>永远不回的处理器:模拟被墙的 models.dev。</summary>
    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static HttpListener StartListener(out string baseUrl)
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
                baseUrl = $"http://127.0.0.1:{port}";
                return listener;
            }
            catch (HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183)
            {
                listener.Close();
            }
        }
    }

    /// <summary>上游端点:/v1/models 报两个模型。</summary>
    private static async Task ServeModelsAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(token); }
            catch (Exception) { return; }
            byte[] body = Encoding.UTF8.GetBytes("""{"object":"list","data":[{"id":"relay-model-a","object":"model"},{"id":"relay-model-b","object":"model"}]}""");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, token);
            context.Response.Close();
        }
    }

    [TestMethod]
    public async Task UnreachableModelsDev_DoesNotBlockTheUpstreamList()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var catalogue = new ModelsDevCatalog(context);
        using HttpListener listener = StartListener(out string baseUrl);
        using var stop = new CancellationTokenSource();
        Task server = ServeModelsAsync(listener, stop.Token);
        var provider = new AiProvider
        {
            Name = "relay",
            BaseUrl = baseUrl + "/v1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [new AiModelConfig { Model = "" }]
        };
        TimeSpan previous = ModelPull.CatalogueTimeout;
        ModelPull.CatalogueTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            using var blocked = new HttpClient(new Hanging());
            var clock = Stopwatch.StartNew();

            ModelPullResult result = await ModelPull.RunAsync(provider, null, catalogue, store, apiKeyOverride: "k",
                force: true, catalogueHttp: blocked);

            Assert.AreEqual(ModelSource.Endpoint, result.Source, "上游清单照常拿到");
            Assert.AreEqual(2, result.Listed);
            Assert.IsTrue(result.CatalogueUnavailable, "界面据此说明窗口与单价用的是缓存或默认值");
            CollectionAssert.IsSubsetOf(new[] { "relay-model-a", "relay-model-b" }, provider.Models.Select(m => m.Model).ToArray());
            Assert.IsLessThan(10_000, clock.ElapsedMilliseconds, "不干等 models.dev");
        }
        finally
        {
            ModelPull.CatalogueTimeout = previous;
            stop.Cancel();
            listener.Stop();
            try { await server; } catch (Exception) { }
        }
    }

    /// <summary>HttpClient.Timeout 到期抛的是 TaskCanceledException:以前被当成「取消」原样抛出,现在当作刷新失败。</summary>
    [TestMethod]
    public async Task RefreshTimeout_IsAFailureNotACancellation()
    {
        using var context = new TestPluginContext();
        var catalogue = new ModelsDevCatalog(context);
        using var slow = new HttpClient(new Hanging()) { Timeout = TimeSpan.FromMilliseconds(200) };

        bool updated = await catalogue.RefreshAsync(slow, force: true);

        Assert.IsFalse(updated);
    }

    /// <summary>发版时打进插件的快照(gzip 后的 api.json 子集,与 scripts/Update-ModelsDevSnapshot.ps1 产出同形)。</summary>
    private static Func<(Func<Stream> Open, DateTimeOffset At)?> Bundle(DateTimeOffset at, string json = """
        {"openai":{"models":{"gpt-5":{"id":"gpt-5","name":"GPT-5","reasoning":true,"limit":{"context":400000,"output":128000},"cost":{"input":1.25,"output":10}}}}}
        """)
    {
        byte[] gz;
        using (var buffer = new MemoryStream())
        {
            using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(Encoding.UTF8.GetBytes(json));
            gz = buffer.ToArray();
        }
        return () => (() => new MemoryStream(gz), at);
    }

    /// <summary>没有外网的新装用户:没有缓存,用内置快照;快照还新鲜时不必联网刷新。</summary>
    [TestMethod]
    public void BundledSnapshot_IsUsedWhenThereIsNoCache()
    {
        using var context = new TestPluginContext();
        var catalogue = new ModelsDevCatalog(context) { BundledSource = Bundle(DateTimeOffset.UtcNow.AddDays(-1)) };

        ModelSpec spec = catalogue.ForProvider("openai").Single();

        Assert.AreEqual("gpt-5", spec.Id);
        Assert.AreEqual(400000, spec.ContextTokens);
        Assert.IsFalse(catalogue.IsStale, "内置快照还在新鲜期内,拉取时不必先去连 models.dev");
    }

    /// <summary>
    /// 缓存与快照合并,同一型号以较新的那份为准:内置快照只留近一年的型号,刚升级时它比旧缓存新,
    /// 整份换过去的话能联网的用户也会丢掉老型号的规格。合并后老型号从缓存来,新型号与新价从快照来。
    /// </summary>
    [TestMethod]
    public async Task CacheAndSnapshot_AreMerged_NewerWinsPerModel()
    {
        using var context = new TestPluginContext();
        string cacheFile = Path.Combine(context.DataDirectory, "models-dev.json");
        // 缓存:一个老型号,外加 gpt-5 的旧规格
        await File.WriteAllTextAsync(cacheFile,
            """{"openai":{"gpt-4o":{"name":"GPT-4o","ctx":128000,"max":16384},"gpt-5":{"name":"GPT-5","ctx":272000,"max":128000}}}""");
        File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddDays(-3));

        var newerSnapshot = new ModelsDevCatalog(context) { BundledSource = Bundle(DateTimeOffset.UtcNow.AddDays(-1)) };
        var olderSnapshot = new ModelsDevCatalog(context) { BundledSource = Bundle(DateTimeOffset.UtcNow.AddDays(-10)) };

        IReadOnlyList<ModelSpec> fromNewer = newerSnapshot.ForProvider("openai");
        CollectionAssert.AreEqual(new[] { "gpt-4o", "gpt-5" }, fromNewer.Select(s => s.Id).ToArray(), "老型号从缓存来,不因快照裁剪而丢");
        Assert.AreEqual(400000, fromNewer.Single(s => s.Id == "gpt-5").ContextTokens, "快照更新:同一型号以快照为准");
        Assert.AreEqual(272000, olderSnapshot.ForProvider("openai").Single(s => s.Id == "gpt-5").ContextTokens, "缓存更新:同一型号以缓存为准");
    }

    /// <summary>快照过期(装的是很久以前的版本):仍拿它兜底,但标为该刷新;快照损坏时退回缓存。</summary>
    [TestMethod]
    public void OldOrBrokenSnapshot_FallsBackSensibly()
    {
        using var context = new TestPluginContext();
        var old = new ModelsDevCatalog(context) { BundledSource = Bundle(DateTimeOffset.UtcNow.AddDays(-30)) };
        Assert.IsTrue(old.IsStale, "过期了就该在拉取时尝试刷新");
        Assert.AreEqual("gpt-5", old.ForProvider("openai").Single().Id, "刷新之前(或刷新失败时)仍有快照可用");

        var broken = new ModelsDevCatalog(context)
        {
            BundledSource = () => (() => new MemoryStream([1, 2, 3, 4]), DateTimeOffset.UtcNow)
        };
        Assert.IsEmpty(broken.ForProvider("openai"), "快照解不开:退回缓存(这里没有缓存),不抛异常");
    }

    /// <summary>调用方自己取消时照旧抛出:那是用户关了窗口,不是 models.dev 的问题。</summary>
    [TestMethod]
    public async Task CallerCancellation_StillPropagates()
    {
        using var context = new TestPluginContext();
        var catalogue = new ModelsDevCatalog(context);
        using var slow = new HttpClient(new Hanging());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => catalogue.RefreshAsync(slow, force: true, cancel.Token));
    }
}
