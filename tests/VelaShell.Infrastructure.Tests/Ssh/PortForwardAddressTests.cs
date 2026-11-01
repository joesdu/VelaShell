using System.Net;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 端口转发的地址翻译。
/// </summary>
/// <remarks>
/// <para>
/// 搬运、计数、半关闭、错误上报这些**数据面**的行为已经全部在库里了
/// (<c>PortForwarder</c> / <c>RemotePortForwarder</c> 自带计量),对应的用例也在库的仓库里 ——
/// 宿主这边原先那 259 行 <c>MeteredPortForwardTests</c> 跟着 <c>MeteredPortForwardHandle</c>
/// 一起删掉了。
/// </para>
/// <para>
/// 剩下这一件事仍然归宿主:把用户在界面上填的那串地址,翻成绑定地址或出站目标。
/// 它有过一次真事故 —— <c>0.0.0.0</c> 作为**目标**是没有意义的
/// (那只是「监听所有接口」的写法),照字面连过去会连到一个不存在的地方。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class PortForwardAddressTests
{
    [TestMethod]
    [DataRow("0.0.0.0")]
    [DataRow("*")]
    public void BindAddress_AllInterfaces(string host) =>
        Assert.AreEqual(IPAddress.Any, LibraryPortForwardHandle.ParseBindAddress(host));

    [TestMethod]
    public void BindAddress_AllInterfacesV6() =>
        Assert.AreEqual(IPAddress.IPv6Any, LibraryPortForwardHandle.ParseBindAddress("::"));

    [TestMethod]
    public void BindAddress_Loopback() =>
        Assert.AreEqual(IPAddress.Loopback, LibraryPortForwardHandle.ParseBindAddress("127.0.0.1"));

    /// <summary>localhost 交给库的默认:同时听 127.0.0.1 与 ::1(Q5)。</summary>
    [TestMethod]
    public void BindAddress_Localhost_MeansBothLoopbacks() =>
        Assert.IsNull(LibraryPortForwardHandle.ParseBindAddress("localhost"));

    [TestMethod]
    public void BindAddress_Explicit() =>
        Assert.AreEqual(IPAddress.Parse("192.168.1.10"), LibraryPortForwardHandle.ParseBindAddress("192.168.1.10"));

    /// <summary>
    /// 远程转发的**本机目标**:<c>0.0.0.0</c> 按用户的本意落到环回。
    /// </summary>
    /// <remarks>
    /// 用户在「远程转发」里把目标填成 0.0.0.0,想表达的是「本机」。
    /// 照字面去连 0.0.0.0 的结果因平台而异,没有一个是他想要的。
    /// </remarks>
    /// <summary>隧道上设的限速原样交给库的两种转发参数(库按转发器、按方向限);不设就是不限。</summary>
    [TestMethod]
    public void Options_CarryTheRateLimit()
    {
        PortForwardRequest limited = new(PortForwardKind.Local, "127.0.0.1", 5432, "db", 5432) { MaxBytesPerSecond = 65536 };
        PortForwardRequest unlimited = new(PortForwardKind.Remote, "localhost", 8080, "127.0.0.1", 80);

        Assert.AreEqual(65536L, LibraryPortForwardHandle.LocalOptions(limited, "127.0.0.1").MaxBytesPerSecond);
        Assert.AreEqual(5432, LibraryPortForwardHandle.LocalOptions(limited, "127.0.0.1").BindPort);
        Assert.AreEqual(65536L, LibraryPortForwardHandle.RemoteOptions(limited, "localhost").MaxBytesPerSecond);
        Assert.IsNull(LibraryPortForwardHandle.LocalOptions(unlimited, "localhost").MaxBytesPerSecond);
        RemotePortForwardOptions remote = LibraryPortForwardHandle.RemoteOptions(unlimited, "localhost");
        Assert.IsNull(remote.MaxBytesPerSecond);
        Assert.AreEqual("localhost", remote.BindAddress, "远程转发的绑定地址原样交给服务端。");
        Assert.AreEqual(8080, remote.BindPort);
    }

    /// <summary>本机在套接字文件上监听时,参数里只有那个路径:地址与端口那栏不解析、不交给库(可能是用户没再管的旧值)。</summary>
    [TestMethod]
    public void LocalOptions_WithASocketPath_ListenOnTheSocketOnly()
    {
        PortForwardRequest request = new(PortForwardKind.Local, "not-an-address", 2375, "127.0.0.1", 2375)
        {
            BoundSocketPath = "/run/user/1000/docker.sock",
            MaxBytesPerSecond = 1024
        };

        LocalPortForwardOptions options = LibraryPortForwardHandle.LocalOptions(request, request.BoundHost);

        Assert.AreEqual("/run/user/1000/docker.sock", options.ListenSocketPath);
        Assert.IsNull(options.BindAddress);
        Assert.AreEqual(0, options.BindPort);
        Assert.AreEqual(1024L, options.MaxBytesPerSecond);
        Assert.IsFalse(options.AllowSocketReplacement, "已有的文件不替用户删。");
    }

    /// <summary>
    /// 远程转发里服务器那头是套接字时,路径交给库的 <see cref="RemotePortForwardOptions.ListenSocketPath" />(库据此发 streamlocal-forward、不看地址与端口);
    /// 服务器那头是端口时不设,本机那头是什么都不影响它。
    /// </summary>
    [TestMethod]
    public void RemoteOptions_CarryTheServerSocketPath()
    {
        PortForwardRequest serverSocket = new(PortForwardKind.Remote, string.Empty, 0, "127.0.0.1", 5432)
        {
            BoundSocketPath = "/run/user/1000/pg.sock"
        };
        PortForwardRequest serverPort = new(PortForwardKind.Remote, "localhost", 2375)
        {
            TargetSocketPath = "/var/run/docker.sock"
        };

        Assert.AreEqual("/run/user/1000/pg.sock", LibraryPortForwardHandle.RemoteOptions(serverSocket, serverSocket.BoundHost).ListenSocketPath);
        RemotePortForwardOptions port = LibraryPortForwardHandle.RemoteOptions(serverPort, serverPort.BoundHost);
        Assert.IsNull(port.ListenSocketPath);
        Assert.AreEqual("localhost", port.BindAddress);
        Assert.AreEqual(2375, port.BindPort);
    }

    /// <summary>
    /// 放行名单交给库:单独一条 any 是全放;别的逐条按 主机:端口 比(通配、端口 *);空名单是全拒,不悄悄变成全放。
    /// </summary>
    [TestMethod]
    public void PermitPolicy_MapsTheAllowList()
    {
        RemoteOpenPolicy any = LibraryPortForwardHandle.PermitPolicy(["any"]);
        Assert.IsTrue(any.Permits("intranet.example", 9999));

        RemoteOpenPolicy listed = LibraryPortForwardHandle.PermitPolicy(["localhost:8080", "*.corp.example:*"]);
        Assert.IsTrue(listed.Permits("localhost", 8080));
        Assert.IsTrue(listed.Permits("pkg.corp.example", 443));
        Assert.IsFalse(listed.Permits("localhost", 8081));
        Assert.IsFalse(listed.Permits("evil.example", 443));

        Assert.IsFalse(LibraryPortForwardHandle.PermitPolicy([]).Permits("localhost", 8080));
        Assert.IsFalse(LibraryPortForwardHandle.PermitPolicy(null).Permits("localhost", 8080));
    }

    /// <summary>
    /// 连接列表里的来源:IPv6 带方括号、服务端报的主机名原样;Unix 套接字上连进来的客户端没有名字,给 null(界面写「来源未知」)。
    /// </summary>
    [TestMethod]
    public void ConnectionSource_IsWrittenAsHostPort()
    {
        Assert.AreEqual("127.0.0.1:53122", LibraryPortForwardHandle.DescribeEndPoint(new IPEndPoint(IPAddress.Loopback, 53122)));
        Assert.AreEqual("[::1]:53122", LibraryPortForwardHandle.DescribeEndPoint(new IPEndPoint(IPAddress.IPv6Loopback, 53122)));
        Assert.AreEqual("gateway.local:4022", LibraryPortForwardHandle.DescribeEndPoint(new DnsEndPoint("gateway.local", 4022)));
        Assert.IsNull(LibraryPortForwardHandle.DescribeEndPoint(null));
    }

    [TestMethod]
    [DataRow("0.0.0.0", "127.0.0.1")]
    [DataRow("*", "127.0.0.1")]
    [DataRow("::", "::1")]
    [DataRow("127.0.0.1", "127.0.0.1")]
    [DataRow("192.168.1.10", "192.168.1.10")]
    public void OutboundHost_ResolvesWildcardToLoopback(string configured, string expected) =>
        Assert.AreEqual(expected, LibraryPortForwardHandle.ResolveOutboundHost(configured));
}
