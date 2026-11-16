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
