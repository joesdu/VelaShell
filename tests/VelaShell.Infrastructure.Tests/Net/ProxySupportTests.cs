using System.Net;
using System.Net.Sockets;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Net;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Transport;

namespace VelaShell.Infrastructure.Tests.Net;

/// <summary>
/// 统一代理层:经 SSH 代理拨号器走一遍 HTTP CONNECT 与 SOCKS5 握手,对着 RFC 字节序列断言(期望值为地面真值,
/// 不用被测代码自证),SSH 拨号器与解析器覆盖直连/环回豁免/配置校验/系统代理折算。
/// </summary>
[TestClass]
[TestCategory("Proxy")]
public class ProxySupportTests
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));

    /// <summary>经 SSH 的代理拨号器(宿主真正在跑的那条路径)按 <paramref name="route" /> 连到目标。</summary>
    private static async Task<Stream> DialThroughAsync(ProxyRoute route, string host, int port, CancellationToken cancellationToken)
    {
        IProxyResolver resolver = Substitute.For<IProxyResolver>();
        resolver.Resolve(host, port).Returns(route);
        return await new ProxyTransportDialer(resolver).DialAsync(SshDialTarget.Direct(host, port), cancellationToken);
    }

    // ———— SOCKS5 完整握手(假代理服务器) ————

    /// <summary>无认证 + 远端 DNS:方法协商只报 0x00,请求发域名(ATYP=03),隧道立通(横幅可读)。</summary>
    [TestMethod]
    public async Task Socks5_NoAuth_RemoteDns_TunnelDelivensBanner()
    {
        using CancellationTokenSource cts = Deadline();
        var greeting = new TaskCompletionSource<byte[]>();
        var request = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async s =>
        {
            greeting.SetResult(await ReadAsync(s, 3, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            request.SetResult(await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
            await s.WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-Fake\r\n"), cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port);
        await using Stream tunnel = await DialThroughAsync(route, "target.example", 2222, cts.Token);

        byte[] greetingBytes = await greeting.Task;
        Assert.AreSequenceEqual(new byte[] { 0x05, 0x01, 0x00 }, greetingBytes);
        byte[] expectedRequest =
        [
            0x05, 0x01, 0x00, 0x03, 0x0E,
            .. Encoding.ASCII.GetBytes("target.example"),
            0x08, 0xAE, // 2222
        ];
        byte[] requestBytes = await request.Task;
        Assert.AreSequenceEqual(expectedRequest, requestBytes);
        Assert.AreEqual("SSH-2.0-Fake\r\n", Encoding.ASCII.GetString(await ReadAsync(tunnel, 14, cts.Token)));
    }

    /// <summary>带凭据:方法列表含 0x02,子协商按 RFC 1929 发 [01 ulen user plen pass]。</summary>
    [TestMethod]
    public async Task Socks5_WithCredentials_SendsRfc1929Subnegotiation()
    {
        using CancellationTokenSource cts = Deadline();
        var greeting = new TaskCompletionSource<byte[]>();
        var auth = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async s =>
        {
            greeting.SetResult(await ReadAsync(s, 4, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x02 }, cts.Token);
            auth.SetResult(await ReadAsync(s, 3 + 2 + 6, cts.Token));
            await s.WriteAsync(new byte[] { 0x01, 0x00 }, cts.Token);
            await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port, "us", "secret");
        await using Stream tunnel = await DialThroughAsync(route, "target.example", 22, cts.Token);

        byte[] greetingBytes = await greeting.Task;
        Assert.AreSequenceEqual(new byte[] { 0x05, 0x02, 0x00, 0x02 }, greetingBytes);
        byte[] expectedAuth =
        [
            0x01, 0x02, (byte)'u', (byte)'s',
            0x06, .. Encoding.ASCII.GetBytes("secret"),
        ];
        byte[] authBytes = await auth.Task;
        Assert.AreSequenceEqual(expectedAuth, authBytes);
    }

    /// <summary>认证被拒(RFC 1929 status != 0)必须抛错,不得带着未认证的链路继续。</summary>
    [TestMethod]
    public async Task Socks5_CredentialsRejected_Throws()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 4, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x02 }, cts.Token);
            await ReadAsync(s, 3 + 1 + 5, cts.Token);
            await s.WriteAsync(new byte[] { 0x01, 0x01 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port, "u", "wrong");
        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(() =>
            DialThroughAsync(route, "target.example", 22, cts.Token));

        // 配了凭据却被拒:原因码留着 ProxyAuthRequired,文案说的是「凭据不对」而不是「没配凭据」。
        Assert.AreEqual(SshFailureReason.ProxyAuthRequired, error.Reason);
        Assert.StartsWith(Strings.Get("Msg_ProxyAuthFailed"), error.Message);
    }

    /// <summary>关闭「使用代理执行 DNS 查找」:本地解析后发 IP(localhost → ATYP=01 127.0.0.1)。</summary>
    [TestMethod]
    public async Task Socks5_LocalDns_SendsResolvedAddressInsteadOfHostname()
    {
        using CancellationTokenSource cts = Deadline();
        var request = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 3, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            request.SetResult(await ReadAsync(s, 4 + 4 + 2, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port, ProxyDns: false);
        await using Stream tunnel = await DialThroughAsync(route, "localhost", 2222, cts.Token);

        byte[] requestBytes = await request.Task;
        Assert.AreSequenceEqual(
            new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, 0x08, 0xAE },
            requestBytes);
    }

    /// <summary>代理拒绝连接(REP != 0)必须抛错。</summary>
    [TestMethod]
    public async Task Socks5_ConnectRefusedByProxy_Throws()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 3, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port);
        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(() =>
            DialThroughAsync(route, "target.example", 22, cts.Token));
        Assert.AreEqual(SshFailureReason.ProxyRefused, error.Reason);
        Assert.Contains($"via socks5 127.0.0.1:{server.Port} → target.example:22", error.Message);
    }

    // ———— HTTP CONNECT ————

    /// <summary>
    /// 带认证的 CONNECT 请求头正确;应答头与紧随其后的隧道首包在同一次发送里到达时,
    /// 握手读取不得越界吞掉隧道数据(SSH 横幅必须能从返回的流里完整读出)。
    /// </summary>
    [TestMethod]
    public async Task HttpConnect_SendsAuthHeader_AndDoesNotOverreadTunnelBytes()
    {
        using CancellationTokenSource cts = Deadline();
        var request = new TaskCompletionSource<string>();
        await using var server = new FakeServer(async s =>
        {
            request.SetResult(await ReadHttpHeadAsync(s, cts.Token));
            byte[] burst = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 Connection established\r\nProxy-Agent: Fake\r\n\r\nSSH-2.0-Fake\r\n");
            await s.WriteAsync(burst, cts.Token); // 应答与横幅一次写出,专门制造越界读的机会
        });

        var route = new ProxyRoute(ProxyKind.Http, "127.0.0.1", server.Port, "user", "pa:ss");
        await using Stream tunnel = await DialThroughAsync(route, "target.example", 22, cts.Token);

        string head = await request.Task;
        Assert.StartsWith("CONNECT target.example:22 HTTP/1.1\r\n", head);
        Assert.Contains("Host: target.example:22\r\n", head);
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pa:ss"));
        Assert.Contains($"Proxy-Authorization: Basic {basic}\r\n", head);
        Assert.AreEqual("SSH-2.0-Fake\r\n", Encoding.ASCII.GetString(await ReadAsync(tunnel, 14, cts.Token)));
    }

    /// <summary>非 2xx 应答(如 502)必须抛错。</summary>
    [TestMethod]
    public async Task HttpConnect_NonSuccessStatus_Throws()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadHttpHeadAsync(s, cts.Token);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n"), cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Http, "127.0.0.1", server.Port);
        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(() =>
            DialThroughAsync(route, "target.example", 22, cts.Token));
        Assert.AreEqual(SshFailureReason.ProxyRefused, error.Reason);
        Assert.Contains("switch Proxy to socks5", error.Message, "HTTP 代理拒绝 22 端口时要指路");
    }

    // ———— SSH 的代理拨号器 ————

    /// <summary>
    /// SSH 拨号器经 SOCKS5 打通目标，拿回来的流双向原样传输。
    /// </summary>
    /// <remarks>
    /// 这条用例原先测的是 <c>LoopbackProxyRelay</c>：为了让一个只认 host:port 的 SSH 库
    /// 走代理，宿主在 127.0.0.1 上开一个一次性监听，把库骗过去，再在背后打通代理隧道。
    /// 换到 VelaShell.Ssh 之后那整条路没有了 —— 代理隧道就是一条 Stream，
    /// 由 <see cref="ProxyTransportDialer" /> 直接交给库，**本机不再开监听端口**。
    /// 所以这里改成对着拨号器测，覆盖的是真正在跑的那条路径。
    /// </remarks>
    [TestMethod]
    public async Task SshDialer_ForwardsBothDirectionsThroughProxy()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 3, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
            await s.WriteAsync(Encoding.ASCII.GetBytes("hello"), cts.Token);
            byte[] echo = await ReadAsync(s, 4, cts.Token);
            await s.WriteAsync(echo, cts.Token);
        });

        IProxyResolver resolver = Substitute.For<IProxyResolver>();
        resolver.Resolve("target.example", 2222)
                .Returns(new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port));

        ProxyTransportDialer dialer = new(resolver);
        await using Stream stream = await dialer.DialAsync(
            SshDialTarget.Direct("target.example", 2222), cts.Token);

        Assert.AreEqual("hello", Encoding.ASCII.GetString(await ReadAsync(stream, 5, cts.Token)));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("ping"), cts.Token);
        Assert.AreEqual("ping", Encoding.ASCII.GetString(await ReadAsync(stream, 4, cts.Token)));

        // 走了代理，拨号器要如实报出自己这一跳的种类 —— 日志里靠它分辨直连与代理。
        Assert.AreEqual(SshDialKind.Socks5, dialer.Kind);
    }

    // ———— 解析器 ————

    /// <summary>none 是"我就是要强制直连",与"没配过"不同 —— 所以这里必须显式写出来。</summary>
    [TestMethod]
    public void Resolver_NoneType_ReturnsDirect()
    {
        // 别用 new ProxyOptions() 代替:默认值已经是 system,那样这条测的就不是 none 了
        ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "none" });
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 22).Kind);
    }

    /// <summary>
    /// 没配过代理时 HTTP 通道跟随系统代理,而不是强制直连。
    /// </summary>
    /// <remarks>
    /// 解析结果会被装成进程级 <c>HttpClient.DefaultProxy</c>,顶掉 .NET 原本的系统代理 ——
    /// 默认直连的话,"装了 VelaShell 反而把系统代理关掉了",浏览器出得去、本程序出不去。
    /// </remarks>
    [TestMethod]
    public void Resolver_FreshOptions_FollowTheSystemProxy()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://sysproxy.example:8080");
            ProxyResolver resolver = CreateResolver(new ProxyOptions());

            // schemeHint 由 HTTP 通道给出(VelaWebProxy 传 destination.Scheme)。
            ProxyRoute route = resolver.Resolve("example.com", 443, "https");

            Assert.AreEqual(ProxyKind.Http, route.Kind);
            Assert.AreEqual("sysproxy.example", route.Host);
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    [TestMethod]
    public void Resolver_ExplicitHttp_CarriesEndpointCredentialsAndDnsFlag()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions
        {
            Type = "http",
            Host = " proxy.example ",
            Port = 3128,
            Username = "u",
            Password = "p",
            ProxyDns = false,
        });
        ProxyRoute route = resolver.Resolve("example.com", 22);
        Assert.AreEqual(new ProxyRoute(ProxyKind.Http, "proxy.example", 3128, "u", "p", false), route);
    }

    /// <summary>环回目标永不走代理(代理自身的中继与本机实验环境依赖这一点)。</summary>
    [TestMethod]
    public void Resolver_LoopbackTarget_BypassesEvenWithExplicitProxy()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080 });
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("127.0.0.1", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("localhost", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("::1", 22).Kind);
    }

    /// <summary>用户显式开代理但配置不完整:必须抛错,绝不静默直连泄漏流量。</summary>
    [TestMethod]
    public void Resolver_EnabledButIncomplete_Throws()
    {
        ProxyResolver noHost = CreateResolver(new ProxyOptions { Type = "http", Host = "", Port = 8080 });
        Assert.ThrowsExactly<ProxyMisconfiguredException>(() => noHost.Resolve("example.com", 22));
        ProxyResolver badPort = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 0 });
        Assert.ThrowsExactly<ProxyMisconfiguredException>(() => badPort.Resolve("example.com", 22));
    }

    /// <summary>system 档按系统代理折算;命中 bypass 列表时直连。</summary>
    [TestMethod]
    public void Resolver_SystemType_FollowsCapturedSystemProxy()
    {
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://sysproxy.example:8080")
            {
                // WebProxy 的 bypass 正则匹配的是完整 URI(如 https://bypassed.example:443/),不能锚定纯主机名。
                BypassList = [@"bypassed\.example"],
            };
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system", ProxyDns = false });

            ProxyRoute route = resolver.Resolve("example.com", 443, "https");
            Assert.AreEqual(ProxyKind.Http, route.Kind);
            Assert.AreEqual("sysproxy.example", route.Host);
            Assert.AreEqual(8080, route.Port);
            Assert.IsFalse(route.ProxyDns);

            Assert.AreEqual(ProxyKind.None, resolver.Resolve("bypassed.example", 443, "https").Kind);
        }
        finally
        {
            ProxyResolver.SystemProxySource = null;
        }
    }

    /// <summary>HttpClient 适配:socks5/http 产出对应 scheme 的代理 URI,直连目标报告绕过。</summary>
    [TestMethod]
    public void VelaWebProxy_MapsRouteToProxyUri()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080 });
        var webProxy = new VelaWebProxy(resolver);
        Assert.AreEqual(new Uri("socks5://proxy.example:1080"), webProxy.GetProxy(new Uri("https://api.github.com/")));
        Assert.IsFalse(webProxy.IsBypassed(new Uri("https://api.github.com/")));
        Assert.IsTrue(webProxy.IsBypassed(new Uri("http://127.0.0.1:8384/")));
    }

    /// <summary>#464:系统代理 URI 自带 userinfo 时,凭据必须带进隧道握手(以前直接丢空必吃 407)。</summary>
    [TestMethod]
    public void Resolver_SystemType_PreservesCredentialsFromProxyUri()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://u:p@sysproxy.example:8080");
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system" });

            // SSH(裸 TCP)与 HTTPS 都走同一个系统代理,凭据都要带上。
            ProxyRoute ssh = resolver.Resolve("example.com", 22);
            Assert.AreEqual(ProxyKind.Http, ssh.Kind);
            Assert.AreEqual("sysproxy.example", ssh.Host);
            Assert.AreEqual("u", ssh.Username);
            Assert.AreEqual("p", ssh.Password);

            ProxyRoute https = resolver.Resolve("example.com", 443, "https");
            Assert.AreEqual(ProxyKind.Http, https.Kind);
            Assert.AreEqual("u", https.Username);
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>#464:socks5h 与 socks 系 scheme 一律按 SOCKS5 处理,不再误判成 HTTP CONNECT。</summary>
    [TestMethod]
    public void Resolver_SystemType_MapsSocksVariantsToSocks5()
    {
        Assert.AreEqual(ProxyKind.Socks5, ProxyResolver.MapProxyScheme("socks5h"));
        Assert.AreEqual(ProxyKind.Socks5, ProxyResolver.MapProxyScheme("SOCKS5"));
        Assert.AreEqual(ProxyKind.Http, ProxyResolver.MapProxyScheme("http"));
    }

    /// <summary>
    /// 规约:system 是**解析器**。系统配的是 HTTP,就作用于全部出站 —— SSH / SFTP / FTP 控制与数据
    /// 都走它,而不是"只代理 HTTP 更新"。
    /// </summary>
    [TestMethod]
    public void Resolver_SystemType_HttpSystemProxy_CoversBareTcp()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://sysproxy.example:8080");
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system" });

            Assert.AreEqual(ProxyKind.Http, resolver.Resolve("example.com", 22).Kind, "SSH 跟随系统代理");
            Assert.AreEqual(ProxyKind.Http, resolver.Resolve("example.com", 21).Kind, "FTP 控制连接跟随系统代理");
            Assert.AreEqual(ProxyKind.Http, resolver.Resolve("example.com", 2222).Kind, "自定义 SSH 端口同样跟随");
            Assert.AreEqual(ProxyKind.Http, resolver.Resolve("example.com", 443, "https").Kind, "HTTP 通道也跟随");
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>
    /// 规约:系统配的是 SOCKS 就按 SOCKS5 走,绝不硬套 HTTP CONNECT。
    /// </summary>
    [TestMethod]
    public void Resolver_SystemType_SocksSystemProxy_ResolvesToSocks5()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("socks5://sysproxy.example:1080");
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system" });

            ProxyRoute route = resolver.Resolve("example.com", 22);

            Assert.AreEqual(ProxyKind.Socks5, route.Kind);
            Assert.AreEqual("sysproxy.example", route.Host);
            Assert.AreEqual(1080, route.Port);
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>
    /// 规约:系统代理默认**远程解析** DNS(把主机名交给代理)。只有 none 才本机解析。
    /// </summary>
    [TestMethod]
    public void Resolver_SystemType_DefaultsToRemoteDns()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://sysproxy.example:8080");
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system" });

            Assert.IsTrue(resolver.Resolve("example.com", 22).ProxyDns, "默认把主机名交给代理解析");
            Assert.IsTrue(resolver.Resolve("example.com", 443, "https").ProxyDns);
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>
    /// 规约:PAC / 系统代理探询失败时明确回退直连,不静默乱连、也不把失败当成"代理已生效"。
    /// </summary>
    [TestMethod]
    public void Resolver_SystemType_ProbeFailure_FallsBackToDirect()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new ThrowingProxy();
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system" });

            Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 22).Kind);
            Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 443, "https").Kind);
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>
    /// 规约:none 必须**真正直连** —— 即便系统代理可用,none 也不回看它(更不会读环境变量)。
    /// "选了无代理仍绕出去"是这类客户端最常见的故障。
    /// </summary>
    [TestMethod]
    public void Resolver_NoneType_IgnoresSystemProxyEntirely()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://sysproxy.example:8080");
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "none" });

            Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 22).Kind);
            Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 443, "https").Kind);

            // HttpClient 侧同样直连:GetProxy 返回 null = 不走代理。
            var webProxy = new VelaWebProxy(resolver);
            Assert.IsNull(webProxy.GetProxy(new Uri("https://api.github.com/")));
            Assert.IsTrue(webProxy.IsBypassed(new Uri("https://api.github.com/")));
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>抛错的假系统代理:模拟 PAC 求值失败 / 系统代理配置损坏。</summary>
    private sealed class ThrowingProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public bool IsBypassed(Uri host) => throw new InvalidOperationException("PAC evaluation failed");
        public Uri? GetProxy(Uri destination) => throw new InvalidOperationException("PAC evaluation failed");
    }

    /// <summary>裸 TCP 也能被显式 http / socks5 代理:那条路不看系统代理。</summary>
    [TestMethod]
    public void Resolver_ExplicitProxy_StillAppliesToBareTcp()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080 });
        Assert.AreEqual(ProxyKind.Socks5, resolver.Resolve("example.com", 22).Kind);
    }

    /// <summary>
    /// #464:保存代理设置后,同一个解析器实例的**下一次**解析就必须用新值 —— 不必重启应用。
    /// 设置服务的只读快照在保存时被整体替换,解析器每次都读它,这里把这个契约钉住。
    /// </summary>
    [TestMethod]
    public void Resolver_ProxyTypeChange_TakesEffectWithoutRestart()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.CurrentSnapshot.Returns(new AppSettings
        {
            Proxy = new ProxyOptions { Type = "none" },
        });
        var resolver = new ProxyResolver(settings);

        Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 22).Kind);

        // 用户切到 SOCKS5 代理并保存。
        settings.CurrentSnapshot.Returns(new AppSettings
        {
            Proxy = new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080 },
        });
        ProxyRoute viaProxy = resolver.Resolve("example.com", 22);
        Assert.AreEqual(ProxyKind.Socks5, viaProxy.Kind);
        Assert.AreEqual("proxy.example", viaProxy.Host);
        Assert.AreEqual(1080, viaProxy.Port);

        // 再切回无代理:立刻直连,不需要重启。
        settings.CurrentSnapshot.Returns(new AppSettings
        {
            Proxy = new ProxyOptions { Type = "none" },
        });
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 22).Kind);
    }

    /// <summary>
    /// system 档对 HTTP 通道跟随系统代理(VelaWebProxy 会把目标 scheme 透传下去)——
    /// AI 插件取模型列表、更新检查这类网页请求在需要代理的网络里照常工作。
    /// </summary>
    [TestMethod]
    public void VelaWebProxy_SystemType_ProxiesHttpChannels()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://sysproxy.example:8080");
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system" });
            var webProxy = new VelaWebProxy(resolver);

            Assert.AreEqual(new Uri("http://sysproxy.example:8080"), webProxy.GetProxy(new Uri("https://api.github.com/")));
            Assert.IsFalse(webProxy.IsBypassed(new Uri("https://api.github.com/")));
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>只对 https 绕过的假系统代理:复现按 scheme 分流的 PAC 行为。</summary>
    private sealed class SchemeSplitProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public bool IsBypassed(Uri host) =>
            string.Equals(host.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        public Uri? GetProxy(Uri destination) => new("http://sysproxy.example:8080");
    }

    /// <summary>HTTP 通道按真实协议探针:https 目标不再被 http 规则误判走代理。</summary>
    [TestMethod]
    public void Resolver_SchemeHint_FollowsCallerScheme()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new SchemeSplitProxy();
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system" });

            // https 目标:https 探针说绕过 → 直连。
            Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 443, "https").Kind);
            // 同一目标按 http 问:http 探针命中 → 走代理。
            Assert.AreEqual(ProxyKind.Http, resolver.Resolve("example.com", 443, "http").Kind);
            // 裸 TCP(SSH / FTP,无 hint):按 http 探针问 → 同样走代理(system 覆盖全部出站)。
            Assert.AreEqual(ProxyKind.Http, resolver.Resolve("example.com", 22).Kind);
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>环回豁免:127/8 整段、尾点 FQDN、IPv4 映射 IPv6、带方括号的 Uri.Host 一律不走代理。</summary>
    [TestMethod]
    public void Resolver_LoopbackVariants_BypassEvenWithExplicitProxy()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080 });
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("127.0.0.2", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("localhost.", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("127.0.0.1.", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("  localhost  ", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("::ffff:127.0.0.1", 22).Kind);
        // VelaWebProxy 传的是 Uri.Host,IPv6 时自带方括号。
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("[::1]", 22).Kind);
        // 非环回不受影响。
        Assert.AreEqual(ProxyKind.Socks5, resolver.Resolve("192.0.2.1", 22).Kind);
    }

    /// <summary>
    /// <c>FormatHost</c> 不能给已经带方括号的主机再套一层:<c>[[::1]]</c> 会让
    /// <see cref="Uri" /> 构造直接抛,而 <c>VelaWebProxy</c> 传进来的正是 <c>Uri.Host</c>。
    /// </summary>
    [TestMethod]
    public void FormatHost_DoesNotDoubleBracketIpv6()
    {
        Assert.AreEqual("[::1]", ProxyResolver.FormatHost("::1"), "裸 IPv6 要补方括号");
        Assert.AreEqual("[::1]", ProxyResolver.FormatHost("[::1]"), "已带方括号的不能再套一层");
        Assert.AreEqual("example.com", ProxyResolver.FormatHost("example.com"));
        Assert.AreEqual("127.0.0.1", ProxyResolver.FormatHost("127.0.0.1"));
        // 拼出来的必须真能构造成 Uri —— 这才是双重方括号的实际后果。
        foreach (string host in (string[])["::1", "[::1]", "2001:db8::1", "[2001:db8::1]", "example.com"])
        {
            _ = new Uri($"http://{ProxyResolver.FormatHost(host)}:8080/");
        }
    }

    /// <summary>
    /// IPv6 代理端点经 <c>VelaWebProxy</c> 出去时不能炸:<c>route.Host</c> 来自 <c>Uri.Host</c>,
    /// 已经是 <c>[::1]</c> 的形状。
    /// </summary>
    [TestMethod]
    public void VelaWebProxy_Ipv6ProxyEndpoint_ProducesAUsableUri()
    {
        IWebProxy? saved = ProxyResolver.SystemProxySource;
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://[2001:db8::1]:8080");
            var webProxy = new VelaWebProxy(CreateResolver(new ProxyOptions { Type = "system" }));

            Assert.AreEqual(new Uri("http://[2001:db8::1]:8080"), webProxy.GetProxy(new Uri("https://api.github.com/")));
        }
        finally
        {
            ProxyResolver.SystemProxySource = saved;
        }
    }

    /// <summary>
    /// 专用类型仍是 <see cref="InvalidOperationException" /> 的子类。
    /// </summary>
    /// <remarks>
    /// SSH 侧 <c>PrepareProxyRelay</c> 的 <c>catch (InvalidOperationException)</c> 靠这条继承关系
    /// 才照常命中;换成独立异常基类会让"代理配错"在 SSH 通道上变成未处理异常。
    /// </remarks>
    [TestMethod]
    public void ProxyMisconfigured_StaysAnInvalidOperationException()
    {
        ProxyResolver noHost = CreateResolver(new ProxyOptions { Type = "http", Port = 8080 });
        Assert.IsInstanceOfType<InvalidOperationException>(
            Assert.ThrowsExactly<ProxyMisconfiguredException>(() => noHost.Resolve("example.com", 22)));
    }

    /// <summary>SOCKS5 凭据超 255 字节(RFC 1929 长度字段上限)在 Resolve 即报错,不等握手失败。</summary>
    [TestMethod]
    public void Resolver_Socks5OversizedCredentials_ThrowsEarly()
    {
        string longUser = new('u', 300);
        ProxyResolver socks = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080, Username = longUser });
        // 与"没填全 host/port"同属代理配置不成立,类型必须一致 —— 否则 FTP 侧那条
        // "别翻译这条错误"的分支会漏掉它。
        Assert.ThrowsExactly<ProxyMisconfiguredException>(() => socks.Resolve("example.com", 22));
        // HTTP Basic 无此限制,不应误伤。
        ProxyResolver http = CreateResolver(new ProxyOptions { Type = "http", Host = "proxy.example", Port = 8080, Username = longUser });
        Assert.AreEqual(ProxyKind.Http, http.Resolve("example.com", 22).Kind);
    }

    // ———— 基建 ————

    private static ProxyResolver CreateResolver(ProxyOptions options)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { Proxy = options });
        return new ProxyResolver(settings);
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (n == 0)
            {
                throw new IOException($"connection closed after {read}/{count} bytes");
            }
            read += n;
        }
        return buffer;
    }

    private static async Task<string> ReadHttpHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new List<byte>();
        byte[] one = new byte[1];
        while (head.Count < 4
               || !(head[^4] == (byte)'\r' && head[^3] == (byte)'\n' && head[^2] == (byte)'\r' && head[^1] == (byte)'\n'))
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (n == 0)
            {
                throw new IOException("connection closed before end of HTTP headers");
            }
            head.Add(one[0]);
        }
        return Encoding.ASCII.GetString([.. head]);
    }

    /// <summary>环回假代理服务器:接受一条连接并执行脚本;脚本异常在 Dispose 时抛回测试。</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _run;

        public int Port { get; }

        public FakeServer(Func<NetworkStream, Task> script)
        {
            _listener.Start(1);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _run = RunAsync(script);
        }

        private async Task RunAsync(Func<NetworkStream, Task> script)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();
            await script(stream);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException)
            {
                // 客户端因断言失败提前断开时的正常收尾噪声。
            }
            catch (TimeoutException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
