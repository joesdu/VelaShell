using VelaShell.Core.Models;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 代理设置变更的「热生效」判定:改完代理必须当场作用于下一次连接,不必重启应用(#464)。
/// </summary>
/// <remarks>
/// 新连接本来就每次重读设置(代理解析器读的是保存时被整体替换的快照),
/// 这里钉住的是另外两件事:
/// <list type="number">
/// <item>只有代理**真的**变了才去动用户已打开的标签 —— 保存一次字体不该顺手重连。</item>
/// <item>应用刚起来、还没有上一份设置时不算"变更",免得首次保存就触发重连。</item>
/// </list>
/// </remarks>
[TestClass]
[TestCategory("Proxy")]
public sealed class ProxySettingsHotApplyTests
{
    [TestMethod]
    public void IdenticalProxyOptions_AreNotAChange()
    {
        Assert.IsFalse(MainWindowViewModel.ProxyChanged(
            Configured(host: "127.0.0.1", port: 1080, user: "u", pass: "p", dns: false),
            Configured(host: "127.0.0.1", port: 1080, user: "u", pass: "p", dns: false)));
    }

    [TestMethod]
    public void FreshDefaults_AreNotAChangeEither() => Assert.IsFalse(MainWindowViewModel.ProxyChanged(new ProxyOptions(), new ProxyOptions()));

    [TestMethod]
    public void NoPreviousSettings_IsNotAChange() =>
        // 启动后第一次保存:没有可比对的上一份设置,不该因此重连任何标签。
        Assert.IsFalse(MainWindowViewModel.ProxyChanged(null, new ProxyOptions()));

    [TestMethod]
    public void SwitchingNoneToSystem_IsAChange()
    {
        // #464 的原始场景:从系统代理切到无代理(或反向),必须触发一次热生效。
        Assert.IsTrue(MainWindowViewModel.ProxyChanged(
            new ProxyOptions { Type = "system" },
            new ProxyOptions { Type = "none" }));
        Assert.IsTrue(MainWindowViewModel.ProxyChanged(
            new ProxyOptions { Type = "none" },
            new ProxyOptions { Type = "system" }));
    }

    [TestMethod]
    public void EndpointCredentialAndDnsEdits_AreChanges()
    {
        ProxyOptions baseline = Configured(host: "proxy.example", port: 8080, user: "u", pass: "p", dns: true);

        Assert.IsTrue(MainWindowViewModel.ProxyChanged(baseline, Configured(host: "other.example", port: 8080, user: "u", pass: "p", dns: true)), "改主机要生效");
        Assert.IsTrue(MainWindowViewModel.ProxyChanged(baseline, Configured(host: "proxy.example", port: 9090, user: "u", pass: "p", dns: true)), "改端口要生效");
        Assert.IsTrue(MainWindowViewModel.ProxyChanged(baseline, Configured(host: "proxy.example", port: 8080, user: "u2", pass: "p", dns: true)), "改用户名要生效");
        Assert.IsTrue(MainWindowViewModel.ProxyChanged(baseline, Configured(host: "proxy.example", port: 8080, user: "u", pass: "p2", dns: true)), "改密码要生效");
        Assert.IsTrue(MainWindowViewModel.ProxyChanged(baseline, Configured(host: "proxy.example", port: 8080, user: "u", pass: "p", dns: false)), "改 DNS 开关要生效");
    }

    private static ProxyOptions Configured(string host, int port, string user, string pass, bool dns) => new()
    {
        Type = "socks5",
        Host = host,
        Port = port,
        Username = user,
        Password = pass,
        ProxyDns = dns,
    };
}
