using System.Net;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Infrastructure.Plugins.Market;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件商店只读客户端:响应解析、隐私边界(请求里带了什么)、以及包摘要不符时的拒收。
/// 全程经注入的 <see cref="HttpMessageHandler" /> 打桩,不碰网络。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginMarketClientTests : IDisposable
{
    private readonly string _dir;

    /// <summary>建一个本次用例专属的临时目录。</summary>
    public PluginMarketClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"velashell_market_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>按请求给出响应的打桩处理器;顺带把每条请求的地址记下来供断言。</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string TwoPlugins = """
        { "plugins": {
            "acme.foo": { "version": "1.3.0", "apiLevel": 2, "minHostVersion": "0.4.0",
                          "minSdkVersion": "2.0.0", "hostMode": "isolated", "signature": "Trusted",
                          "publisherFingerprint": "SHA256:abc", "payloadSha256": "aa", "fileSha256": "bb",
                          "packageSize": 4096, "publishedAt": "2026-09-01T00:00:00Z" },
            "acme.bar": { "version": "0.9.0", "apiLevel": 1, "minHostVersion": null,
                          "minSdkVersion": null, "signature": "Unsigned",
                          "publisherFingerprint": null, "fileSha256": "cc", "packageSize": 512 } } }
        """;

    [TestMethod]
    public async Task GetLatestAsync_ReadsBackEveryFieldTheUpdateDecisionNeeds()
    {
        StubHandler handler = new(_ => Json(TwoPlugins));
        HttpPluginMarketClient client = new("https://market.test", new(handler));

        IReadOnlyDictionary<string, PluginMarketVersion> latest =
            await client.GetLatestAsync(["acme.foo", "acme.bar"]);

        Assert.AreEqual(2, latest.Count);
        PluginMarketVersion foo = latest["acme.foo"];
        Assert.AreEqual("1.3.0", foo.Version);
        Assert.AreEqual(2, foo.ApiLevel);
        Assert.AreEqual("0.4.0", foo.MinHostVersion, "宿主够不够新,全靠这一项判。");
        Assert.AreEqual("2.0.0", foo.MinSdkVersion);
        Assert.AreEqual("SHA256:abc", foo.PublisherFingerprint, "换没换发布者,靠它与钉住的指纹比。");
        Assert.AreEqual(4096, foo.PackageSize);
        // 未签名的那一条:指纹为空,界面据此走"完整确认流程"而不是直接装。
        Assert.IsNull(latest["acme.bar"].PublisherFingerprint);
    }

    [TestMethod]
    public async Task GetLatestAsync_SendsTheRequestedIdsAndNothingElse()
    {
        StubHandler handler = new(_ => Json("""{ "plugins": {} }"""));
        HttpPluginMarketClient client = new("https://market.test", new(handler));

        await client.GetLatestAsync(["acme.foo", "acme.bar"]);

        // 这条断言守的是 PRIVACY.md 里那句承诺:请求里除了"要查哪几个插件"之外不带别的。
        Assert.ContainsSingle(handler.Requested);
        string requested = handler.Requested[0];
        Assert.AreEqual("https://market.test/api/plugins/latest?ids=acme.foo%2Cacme.bar", requested);
    }

    [TestMethod]
    public async Task GetLatestAsync_WhenTheMarketplaceIsUnreachable_YieldsAnEmptyTable()
    {
        StubHandler handler = new(_ => throw new HttpRequestException("no route to host"));
        HttpPluginMarketClient client = new("https://market.test", new(handler));

        IReadOnlyDictionary<string, PluginMarketVersion> latest = await client.GetLatestAsync(["acme.foo"]);

        Assert.IsEmpty(latest, "查不到就是这次没查到,不该把异常抛到插件管理页上。");
    }

    [TestMethod]
    public async Task GetLatestAsync_OverPlainHttp_SendsNothingAtAll()
    {
        StubHandler handler = new(_ => Json(TwoPlugins));
        HttpPluginMarketClient client = new("http://market.test", new(handler));

        IReadOnlyDictionary<string, PluginMarketVersion> latest = await client.GetLatestAsync(["acme.foo"]);

        Assert.IsEmpty(latest);
        Assert.IsEmpty(handler.Requested,
            "明文取回的版本号与下载地址可被中间人换掉,而那头连着的是要在本机跑起来的代码。");
    }

    [TestMethod]
    public async Task DownloadAsync_WritesThePackageWhenTheDigestMatches()
    {
        byte[] payload = Encoding.ASCII.GetBytes("pretend-this-is-a-vpx");
        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        HttpPluginMarketClient client = new("https://market.test", new(TicketThen(payload, digest)));
        string destination = Path.Combine(_dir, "acme.foo-1.3.0.vpx");

        await client.DownloadAsync("acme.foo", "1.3.0", destination);

        Assert.AreEqual("pretend-this-is-a-vpx", await File.ReadAllTextAsync(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"), "下完不该留半成品。");
    }

    [TestMethod]
    public async Task DownloadAsync_WhenTheDigestDoesNotMatch_RejectsAndLeavesNothingBehind()
    {
        byte[] payload = Encoding.ASCII.GetBytes("tampered");
        HttpPluginMarketClient client = new("https://market.test",
            new(TicketThen(payload, new string('0', 64))));
        string destination = Path.Combine(_dir, "acme.foo-1.3.0.vpx");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.DownloadAsync("acme.foo", "1.3.0", destination));

        // 内容存疑的包一旦留在磁盘上,迟早有人(或某段重试逻辑)把它当成"已经下好了"直接装上去。
        Assert.IsFalse(File.Exists(destination));
    }

    /// <summary>先回下载票据,再回包体。</summary>
    private static StubHandler TicketThen(byte[] payload, string declaredDigest) =>
        new(request => request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
            ? Json($$"""
                { "url": "https://cdn.test/acme.foo-1.3.0.vpx", "payloadSha256": "unused",
                  "fileSha256": "{{declaredDigest}}", "packageSize": {{payload.Length}} }
                """)
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
}
