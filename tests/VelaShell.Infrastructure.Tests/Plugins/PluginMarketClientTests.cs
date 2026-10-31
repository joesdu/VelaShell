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

        Assert.HasCount(2, latest);
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

    /// <summary>
    /// 半成品的位置上预先放着一个链接(别的本机用户能写这个目录时就能这么干):不顺着它写,
    /// 它指向的文件一字不动,包落在自己新建的文件里;上次没下完的半成品同样先删再独占新建。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DownloadAsync_DoesNotWriteThroughAPlantedLink(bool symbolic)
    {
        byte[] payload = Encoding.ASCII.GetBytes("pretend-this-is-a-vpx");
        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        HttpPluginMarketClient client = new("https://market.test", new(TicketThen(payload, digest)));
        string destination = Path.Combine(_dir, "updates", "acme.foo-1.3.0.vpx");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string victim = Path.Combine(_dir, "victim.txt");
        await File.WriteAllTextAsync(victim, "do-not-touch");
        if (symbolic)
        {
            try
            {
                File.CreateSymbolicLink(destination + ".partial", victim);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows 上建符号链接要开发者模式或管理员;这个场景本来就出在 Linux 的共用 /tmp 上,CI 的 Linux / macOS 作业会跑到。
                Assert.Inconclusive($"[SKIP] cannot create a symbolic link here: {ex.Message}");
            }
        }
        else
        {
            await CreateHardLinkAsync(destination + ".partial", victim);
        }

        await client.DownloadAsync("acme.foo", "1.3.0", destination);

        Assert.AreEqual("do-not-touch", await File.ReadAllTextAsync(victim), "不能顺着链接把包写进别处");
        Assert.IsNull(new FileInfo(destination).LinkTarget, "落地的是自己新建的文件,不是那个链接");
        Assert.AreEqual("pretend-this-is-a-vpx", await File.ReadAllTextAsync(destination));
    }

    private static async Task CreateHardLinkAsync(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "ln")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in OperatingSystem.IsWindows() ? ["/c", "mklink", "/H", link, target] : new[] { target, link })
        {
            start.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        Assert.AreEqual(0, process.ExitCode, $"硬链接没建出来:{await error}");
    }

    /// <summary>Unix 上新建的下载目录只给自己(0700)、包只给自己读写(0600)。</summary>
    [TestMethod]
    public async Task DownloadAsync_OnUnix_CreatesAPrivateDirectoryAndFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("[SKIP] Unix file modes only");
            return;
        }
        byte[] payload = Encoding.ASCII.GetBytes("pretend-this-is-a-vpx");
        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        HttpPluginMarketClient client = new("https://market.test", new(TicketThen(payload, digest)));
        string directory = Path.Combine(_dir, "fresh-updates");
        string destination = Path.Combine(directory, "acme.foo-1.3.0.vpx");

        await client.DownloadAsync("acme.foo", "1.3.0", destination);

        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
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

    /// <summary>
    /// 插件更新一律要摘要、一律走 https(维护者 2026-10-25 决定,#652 复审⑤):商店没给 <c>fileSha256</c>
    /// (或给的不是 64 位十六进制)就不下,票据里的下载地址是 http 也不下 —— 以前前者照装不核、后者照下。
    /// 两种都在发出下载请求之前拒绝,磁盘上什么都不留。
    /// </summary>
    [TestMethod]
    [DataRow("https://cdn.test/acme.foo-1.3.0.vpx", null, DisplayName = "没给摘要")]
    [DataRow("https://cdn.test/acme.foo-1.3.0.vpx", "", DisplayName = "摘要是空串")]
    [DataRow("https://cdn.test/acme.foo-1.3.0.vpx", "bb", DisplayName = "摘要不是 SHA-256")]
    [DataRow("http://cdn.test/acme.foo-1.3.0.vpx", "valid", DisplayName = "下载地址是明文 http")]
    public async Task DownloadAsync_WithoutADigestOrOverPlainHttp_RefusesBeforeDownloading(string url, string? digest)
    {
        byte[] payload = Encoding.ASCII.GetBytes("pretend-this-is-a-vpx");
        string declared = digest == "valid" ? Convert.ToHexStringLower(SHA256.HashData(payload)) : digest ?? "";
        string sha = digest is null ? "" : $"\"fileSha256\": \"{declared}\",";
        StubHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
            ? Json($$"""{ "url": "{{url}}", {{sha}} "packageSize": {{payload.Length}} }""")
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        HttpPluginMarketClient client = new("https://market.test", new(handler));
        string destination = Path.Combine(_dir, "acme.foo-1.3.0.vpx");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.DownloadAsync("acme.foo", "1.3.0", destination));

        Assert.HasCount(1, handler.Requested, "只取了票据,包一个字节都没下");
        Assert.IsFalse(File.Exists(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"));
    }

    /// <summary>商店地址本身是 http 时连票据都不取(与查更新同一条规矩)。</summary>
    [TestMethod]
    public async Task DownloadAsync_FromAPlainHttpMarketplace_SendsNothingAtAll()
    {
        StubHandler handler = new(_ => Json("{}"));
        HttpPluginMarketClient client = new("http://market.test", new(handler));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.DownloadAsync("acme.foo", "1.3.0", Path.Combine(_dir, "x.vpx")));

        Assert.IsEmpty(handler.Requested);
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
