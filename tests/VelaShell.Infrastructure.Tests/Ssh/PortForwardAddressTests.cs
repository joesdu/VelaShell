using System.Net;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 端口转发的地址翻译。
/// </summary>
/// <remarks>
/// <para>
/// 搬运、计数、半关闭、错误上报这些**数据面**的行为已经全部在库里了
/// (<c>PortForwarder</c> / <c>RemoteForwarder</c> 自带计量),对应的用例也在库的仓库里 ——
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
    [DataRow("localhost")]
    [DataRow("127.0.0.1")]
    public void BindAddress_Loopback(string host) =>
        Assert.AreEqual(IPAddress.Loopback, LibraryPortForwardHandle.ParseBindAddress(host));

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
    [TestMethod]
    [DataRow("0.0.0.0", "127.0.0.1")]
    [DataRow("*", "127.0.0.1")]
    [DataRow("::", "::1")]
    [DataRow("127.0.0.1", "127.0.0.1")]
    [DataRow("192.168.1.10", "192.168.1.10")]
    public void OutboundHost_ResolvesWildcardToLoopback(string configured, string expected) =>
        Assert.AreEqual(expected, LibraryPortForwardHandle.ResolveOutboundHost(configured));
}
