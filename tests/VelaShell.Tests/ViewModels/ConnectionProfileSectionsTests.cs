using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接对话框的分页、页签上的「有非默认值」圆点、页脚的连接目标预览。
/// </summary>
[TestClass]
public sealed class ConnectionProfileSectionsTests
{
    /// <summary>哪几页出现由协议决定:终端与转发只挂在交互式 shell 上,SSH 选项 SSH / SFTP 都有。</summary>
    [TestMethod]
    [DataRow(ConnectionType.SSH, true, true, true, false)]
    [DataRow(ConnectionType.SFTP, false, true, false, false)]
    [DataRow(ConnectionType.FTP, false, false, false, true)]
    public void SectionsFollowTheProtocol(ConnectionType type, bool terminal, bool sshOptions, bool forwarding, bool advanced)
    {
        var vm = new ConnectionProfileViewModel();
        vm.SelectConnectionTypeCommand.Execute(type).Subscribe();

        Assert.AreEqual(terminal, vm.ShowTerminalSection);
        Assert.AreEqual(sshOptions, vm.ShowSshOptionsSection);
        Assert.AreEqual(forwarding, vm.ShowForwardingSection);
        Assert.AreEqual(advanced, vm.ShowAdvancedSection);
    }

    /// <summary>
    /// 从 SSH 的「转发」页切到 FTP:那一页没了,右边不能停在一片空白上,也不能没有一个页签是选中的。
    /// </summary>
    [TestMethod]
    public void SwitchingToAProtocolWithoutTheCurrentPage_FallsBackToGeneral()
    {
        var vm = new ConnectionProfileViewModel();
        vm.SelectSectionCommand.Execute(ConnectionProfileSection.Forwarding).Subscribe();
        Assert.IsTrue(vm.IsForwardingSection);

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();

        Assert.IsTrue(vm.IsGeneralSection);
        Assert.IsFalse(vm.IsForwardingSection);
    }

    /// <summary>
    /// 切到插件协议走的是另一条路(不清插件字段,激活还没完成时表单是空的),同样要落回「常规」——
    /// 否则 S3 的对话框右边停着一页 SSH 转发。
    /// </summary>
    [TestMethod]
    public void SwitchingToAPluginProtocol_LeavesSshOnlyPages()
    {
        var vm = new ConnectionProfileViewModel();
        vm.SelectSectionCommand.Execute(ConnectionProfileSection.Forwarding).Subscribe();

        vm.SelectPluginProtocolCommand.Execute("velashell.s3").Subscribe();

        Assert.IsFalse(vm.ShowForwardingSection);
        Assert.IsFalse(vm.ShowTerminalSection);
        Assert.IsFalse(vm.ShowSshOptionsSection);
        Assert.IsTrue(vm.IsGeneralSection);
    }

    /// <summary>页还在的话,换协议不该把人从当前页踢走(SSH 与 SFTP 都有「SSH 选项」)。</summary>
    [TestMethod]
    public void SwitchingProtocols_KeepsThePageWhenItStillExists()
    {
        var vm = new ConnectionProfileViewModel();
        vm.SelectSectionCommand.Execute(ConnectionProfileSection.SshOptions).Subscribe();

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();

        Assert.IsTrue(vm.IsSshOptionsSection);
    }

    /// <summary>当前协议没有的那一页选不进去(SFTP 没有终端)。</summary>
    [TestMethod]
    public void SelectingAPageTheProtocolDoesNotHave_StaysOnGeneral()
    {
        var vm = new ConnectionProfileViewModel();
        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();

        vm.SelectedSection = ConnectionProfileSection.Terminal;

        Assert.IsTrue(vm.IsGeneralSection);
    }

    /// <summary>
    /// 编辑一条配过东西的配置:每一页的圆点都要点亮。原先的做法是把折叠区自动展开,
    /// 分页之后填过的东西在别的页签后面,不点出来就会被当成丢了。
    /// </summary>
    [TestMethod]
    public void ModifiedDots_LightUpForSavedNonDefaults()
    {
        var existing = new SessionProfile
        {
            Host = "10.0.0.1",
            Username = "ops",
            Terminal = new TerminalOverrides { Encoding = "GBK" },
            Ssh = new SshSessionOptions { ObscureKeystrokeTiming = true, X11Forwarding = true },
        };

        var vm = new ConnectionProfileViewModel(existing);

        Assert.IsTrue(vm.IsTerminalSectionModified);
        Assert.IsTrue(vm.IsSshOptionsSectionModified);
        Assert.IsTrue(vm.IsForwardingSectionModified);
    }

    /// <summary>新建连接全是默认值:一个圆点都不该有,否则圆点就不再说明任何事。</summary>
    [TestMethod]
    public void ModifiedDots_StayOffForANewProfile()
    {
        var vm = new ConnectionProfileViewModel();

        Assert.IsFalse(vm.IsTerminalSectionModified);
        Assert.IsFalse(vm.IsSshOptionsSectionModified);
        Assert.IsFalse(vm.IsForwardingSectionModified);
        Assert.IsFalse(vm.IsAdvancedSectionModified);
    }

    /// <summary>圆点随输入实时变:改了立刻亮,改回默认立刻灭,并且发通知让界面跟上。</summary>
    [TestMethod]
    public void ModifiedDots_FollowEdits()
    {
        var vm = new ConnectionProfileViewModel();
        List<string> raised = [];
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.AntiIdleSeconds = 60;
        Assert.IsTrue(vm.IsTerminalSectionModified);
        Assert.Contains(nameof(ConnectionProfileViewModel.IsTerminalSectionModified), raised);

        vm.AntiIdleSeconds = 0;
        Assert.IsFalse(vm.IsTerminalSectionModified);

        vm.SshAgentForwarding = true;
        Assert.IsTrue(vm.IsForwardingSectionModified);
        Assert.Contains(nameof(ConnectionProfileViewModel.IsForwardingSectionModified), raised);

        vm.SshLegacyAlgorithms = true;
        Assert.IsTrue(vm.IsSshOptionsSectionModified);
        Assert.Contains(nameof(ConnectionProfileViewModel.IsSshOptionsSectionModified), raised);
    }

    [TestMethod]
    public void EndpointPreview_IsEmptyUntilThereIsAHost() =>
        Assert.AreEqual(string.Empty, new ConnectionProfileViewModel { Username = "root" }.EndpointPreview);

    [TestMethod]
    public void EndpointPreview_ShowsUserHostAndPortForSsh() =>
        Assert.AreEqual("root@192.168.1.100:22",
            new ConnectionProfileViewModel { Host = " 192.168.1.100 ", Username = "root", Port = 22 }.EndpointPreview);

    [TestMethod]
    public void EndpointPreview_LeavesOutAnEmptyUser() =>
        Assert.AreEqual("example.com:2222", new ConnectionProfileViewModel { Host = "example.com", Port = 2222 }.EndpointPreview);

    /// <summary>IPv6 字面量带端口要加方括号,否则 <c>::1:22</c> 读不出哪段是端口。</summary>
    [TestMethod]
    public void EndpointPreview_BracketsIpv6Literals() =>
        Assert.AreEqual("root@[fe80::1]:22",
            new ConnectionProfileViewModel { Host = "fe80::1", Username = "root", Port = 22 }.EndpointPreview);

    /// <summary>
    /// 主机栏里已经带着方括号的(从别处抄来的 <c>[v6]:port</c>)不再套一层;方括号后面的端口就是要连的端口,
    /// 预览照实显示 —— 曾经显示成 <c>root@[2001:db8::1]:2222:22</c>(#611)。
    /// </summary>
    [TestMethod]
    [DataRow("[2001:db8::1]", "root@[2001:db8::1]:22")]
    [DataRow(" [2001:db8::1]:2222 ", "root@[2001:db8::1]:2222")]
    public void EndpointPreview_HonoursABracketedHost(string host, string expected) =>
        Assert.AreEqual(expected, new ConnectionProfileViewModel { Host = host, Username = "root", Port = 22 }.EndpointPreview);

    [TestMethod]
    public void EndpointPreview_CarriesTheSchemeForFileProtocols()
    {
        var vm = new ConnectionProfileViewModel { Host = "files.example.com", Username = "deploy" };

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
        Assert.AreEqual("sftp://deploy@files.example.com:22", vm.EndpointPreview);

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();
        Assert.AreEqual("ftp://deploy@files.example.com:21", vm.EndpointPreview);

        // 隐式 FTPS 连端口一起跟着切到 990。
        vm.FtpEncryption = FtpEncryptionMode.Implicit;
        Assert.AreEqual("ftps://deploy@files.example.com:990", vm.EndpointPreview);

        // 匿名登录不带用户名 —— 填着的用户名这时根本不会发出去。
        vm.FtpAnonymous = true;
        Assert.AreEqual("ftps://files.example.com:990", vm.EndpointPreview);
    }

    /// <summary>预览是随输入实时更新的:每个组成部分变了都要发通知。</summary>
    [TestMethod]
    public void EndpointPreview_RaisesWhenAnyPartChanges()
    {
        var vm = new ConnectionProfileViewModel();
        int raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionProfileViewModel.EndpointPreview))
            {
                raised++;
            }
        };

        vm.Host = "h";
        vm.Username = "u";
        vm.Port = 2200;

        Assert.AreEqual(3, raised);
    }

    [TestMethod]
    public void AuthMethodSegments_SwitchTheAuthMethod()
    {
        var vm = new ConnectionProfileViewModel();

        vm.SelectAuthMethodCommand.Execute(AuthMethod.Certificate).Subscribe();
        Assert.IsTrue(vm.IsCertAuth);
        Assert.IsTrue(vm.ShowsPrivateKeyFields);

        vm.SelectAuthMethodCommand.Execute(AuthMethod.Agent).Subscribe();
        Assert.IsTrue(vm.IsAgentAuth);
        Assert.IsFalse(vm.ShowPasswordField);
    }

    /// <summary>「获取更多协议…」只在主窗口注入了打开插件管理器的回调时出现,点下去走的就是那个回调。</summary>
    [TestMethod]
    public void OpenPluginManager_IsOfferedOnlyWhenTheHostProvidesIt()
    {
        var vm = new ConnectionProfileViewModel();
        Assert.IsFalse(vm.CanOpenPluginManager);

        int opened = 0;
        vm.OpenPluginManager = () => opened++;
        Assert.IsTrue(vm.CanOpenPluginManager);

        vm.OpenPluginManagerCommand.Execute().Subscribe();
        Assert.AreEqual(1, opened);
    }
}
