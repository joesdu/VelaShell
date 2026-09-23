using VelaShell.Core.Models;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 代理设置变更的「热生效」判定:改完代理必须当场作用于下一次连接,不必重启应用(#464)。
/// </summary>
/// <remarks>
/// 新连接本来就每次重读设置(代理解析器读的是保存时被整体替换的快照),
/// 这里钉住的是另外三件事:
/// <list type="number">
/// <item>只有代理**真的**变了才去动用户已打开的标签 —— 保存一次字体不该顺手重连。</item>
/// <item>应用刚起来、还没有上一份设置时不算"变更",免得首次保存就触发重连。</item>
/// <item>真的变了之后,**挑得中**那些连接失败的标签 —— 第一版就栽在这一步。</item>
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

    // ———— 挑出「该被救」的标签 ————

    /// <summary>
    /// 拿**真实的** <see cref="TerminalTabViewModel" /> 钉住筛选:连接失败的 SSH 标签要被选中。
    /// </summary>
    /// <remarks>
    /// 这条用例是冲着第一版那个缺陷来的 —— 当时按 <c>SessionStatus.Error</c> 筛,
    /// 而失败的标签停在 <c>Disconnected</c> + <c>ConnectionError</c>,一个都选不中。
    /// 用替身或用枚举复述一遍条件都挡不住那种错,只有让真标签走一遍真实的失败路径才行。
    /// </remarks>
    [TestMethod]
    public void AFailedSshTab_IsPickedUp()
    {
        TerminalTabViewModel tab = SshTab();
        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.MarkConnectionFailed("Connection timed out (via http 127.0.0.1:7897 → example.com:22)");

        Assert.AreSequenceEqual(
            (TerminalTabViewModel[])[tab], MainWindowViewModel.FailedSshTabsAfterProxyChange([tab]), SequenceOrder.InAnyOrder);
    }

    /// <summary>连着的、干净断开的、本地终端、非 SSH 协议,一律不碰。</summary>
    [TestMethod]
    public void EverythingElse_IsLeftAlone()
    {
        TerminalTabViewModel connected = SshTab();
        connected.ConnectionStatus = SessionStatus.Connected;

        TerminalTabViewModel cleanlyDropped = SshTab();
        cleanlyDropped.ConnectionStatus = SessionStatus.Connected;
        cleanlyDropped.MarkDisconnected();

        TerminalTabViewModel local = new(FakeTerminal.Emulator()) { LocalShell = new("pwsh", "PowerShell", "pwsh.exe") };
        local.MarkConnectionFailed("boom");

        TerminalTabViewModel plugin = SshTab();
        plugin.Profile!.ConnectionType = ConnectionType.Plugin;
        plugin.MarkConnectionFailed("boom");

        Assert.IsEmpty(MainWindowViewModel.FailedSshTabsAfterProxyChange(
            [connected, cleanlyDropped, local, plugin]));
    }

    /// <summary>重连成功之后就不该再被当成"失败标签"反复救。</summary>
    [TestMethod]
    public void OnceReconnected_ItDropsOutOfTheSelection()
    {
        TerminalTabViewModel tab = SshTab();
        tab.MarkConnectionFailed("Connection timed out");
        Assert.IsNotEmpty(MainWindowViewModel.FailedSshTabsAfterProxyChange([tab]));

        tab.ConnectionStatus = SessionStatus.Connected;

        Assert.IsEmpty(MainWindowViewModel.FailedSshTabsAfterProxyChange([tab]));
    }

    private static TerminalTabViewModel SshTab() =>
        new(FakeTerminal.Emulator())
        {
            Profile = new() { Name = "s", Host = "example.com", Port = 22, ConnectionType = ConnectionType.SSH },
        };

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
