using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Behaviors;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class ConnectionProfileViewModelTests
{
    // 密码 ASCII 过滤已从 ViewModel 下沉到 SecurePasswordBox 输入行为。
    [TestMethod]
    [DataRow("pä中文ss123", "pss123")]
    [DataRow("secret!", "secret!")]
    [DataRow("密码", "")]
    public void FilterAscii_StripsNonAsciiCharacters(string input, string expected) => Assert.AreEqual(expected, SecurePasswordBox.FilterAscii(input));

    /// <summary>
    /// 「认证后执行命令」是每条配置各一份的:打开一条已存的配置要回显它自己的那条,
    /// 保存要原样带回去。回显丢字段的表现是"改个端口顺手把命令清空了",而且不报错。
    /// </summary>
    [TestMethod]
    public async Task PostAuthCommand_RoundTripsThroughTheEditDialog()
    {
        var existing = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            PostAuthCommand = "sudo su -",
            PostAuthCommandDelaySeconds = 3,
        };

        var vm = new ConnectionProfileViewModel(existing);

        Assert.IsTrue(vm.SupportsPostAuthCommand, "SSH 才有 shell 通道,这一栏也只对它出现。");
        Assert.AreEqual("sudo su -", vm.PostAuthCommand);
        Assert.AreEqual(3, vm.PostAuthCommandDelaySeconds);
        Assert.IsTrue(vm.IsTerminalSectionModified, "填过的命令不能藏得看不出来:「终端」页签上要有圆点,否则用户会当成配置丢了。");

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved);
        Assert.AreEqual("sudo su -", saved.PostAuthCommand);
        Assert.AreEqual(3, saved.PostAuthCommandDelaySeconds);
    }

    /// <summary>
    /// IPv6 主机(#611):配置里存不带方括号的裸地址;主机栏写成 <c>[v6]:port</c> 的,端口取方括号后面那个。
    /// 方括号带进配置的话,经跳板时会原样发给服务端,主机密钥也与裸地址记成两台。
    /// </summary>
    [TestMethod]
    [DataRow("2001:db8:0:1::10:20", 22, "2001:db8:0:1::10:20", 22)]
    [DataRow(" [2001:db8::1] ", 22, "2001:db8::1", 22)]
    [DataRow("[2001:db8::1]:2222", 22, "2001:db8::1", 2222)]
    [DataRow("[fe80::1%en0]:2200", 2222, "fe80::1%en0", 2200)]
    [DataRow("example.com", 2222, "example.com", 2222)]
    public async Task Ipv6Host_IsSavedWithoutBrackets(string host, int port, string savedHost, int savedPort)
    {
        var vm = new ConnectionProfileViewModel { Host = host, Port = port, Username = "tzraeq" };

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual((savedHost, savedPort), (saved.Host, saved.Port));
        Assert.AreEqual($"tzraeq@{savedHost}", saved.Name, "名称留空时的兜底也用去掉方括号的地址");
    }

    /// <summary>只有空格的命令等于没配:存下去会让 <c>SendSilentCommand</c> 每次连接多敲一个回车。</summary>
    [TestMethod]
    public async Task PostAuthCommand_WhenBlank_IsSavedAsNull()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", PostAuthCommand = "   " };

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.IsNull(saved.PostAuthCommand);
    }

    /// <summary>
    /// 备注(#549):打开已存的配置要回显,保存要原样带回 —— 包括行内的换行;
    /// 首尾的空行空格去掉。不分协议,哪种连接都有这一栏。
    /// </summary>
    [TestMethod]
    [DataRow(ConnectionType.SSH)]
    [DataRow(ConnectionType.SFTP)]
    [DataRow(ConnectionType.FTP)]
    public async Task Notes_RoundTripThroughTheEditDialog(ConnectionType type)
    {
        var existing = new SessionProfile
        {
            ConnectionType = type,
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            Notes = "跳板机,只开 22\n值班:运维二组",
        };

        var vm = new ConnectionProfileViewModel(existing);
        Assert.AreEqual(existing.Notes, vm.Notes);

        vm.Notes = "\n  跳板机,只开 22\n\n改配置先报备  \n";
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual("跳板机,只开 22\n\n改配置先报备", saved.Notes);
    }

    /// <summary>
    /// 对话框里没有、但属于这条配置的状态,编辑保存时必须原样带回:置顶(资源管理器右键)、
    /// 最近连接时间(「最近连接」按它排)、自动开启的隧道、SFTP 文档里存的目录收藏。
    /// </summary>
    /// <remarks>
    /// 保存是按界面上的字段新建一条配置再整条覆盖落盘的;漏带一项,表现就是"改个端口,
    /// 置顶没了""改个名字,这条连接从最近连接里掉到最后",而且不报任何错。
    /// </remarks>
    [TestMethod]
    public async Task EditingAProfile_KeepsStateTheDialogDoesNotShow()
    {
        DateTime lastConnected = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        List<Guid> tunnels = [Guid.NewGuid(), Guid.NewGuid()];
        var bookmark = new RemoteDirectoryBookmark { Name = "logs", LocalPath = "/tmp/logs", RemotePath = "/var/log" };
        var existing = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            IsPinned = true,
            LastConnectedAt = lastConnected,
            AutoStartTunnelIds = tunnels,
            DirectoryBookmarks = [bookmark],
        };

        var vm = new ConnectionProfileViewModel(existing) { Port = 2222 };
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual(2222, saved.Port);
        Assert.IsTrue(saved.IsPinned, "编辑一条置顶的连接,保存后不该被取消置顶");
        Assert.AreEqual(lastConnected, saved.LastConnectedAt);
        Assert.IsNotNull(saved.AutoStartTunnelIds);
        Assert.AreSequenceEqual(tunnels, saved.AutoStartTunnelIds);
        Assert.AreNotSame(tunnels, saved.AutoStartTunnelIds, "存下去的是副本,不与传进来的配置共享列表");
        RemoteDirectoryBookmark kept = saved.DirectoryBookmarks.Single();
        Assert.AreEqual((bookmark.Id, "logs", "/tmp/logs", "/var/log"), (kept.Id, kept.Name, kept.LocalPath, kept.RemotePath),
            "编辑一条连接、哪怕什么都没改,保存后目录收藏也不能被清空");
        Assert.AreNotSame(bookmark, kept, "收藏条目也是副本");
    }

    /// <summary>新建的连接没有这些状态:不置顶、没连过。</summary>
    [TestMethod]
    public async Task NewProfile_IsNotPinnedAndHasNeverConnected()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u" };

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.IsFalse(saved.IsPinned);
        Assert.IsNull(saved.LastConnectedAt);
        Assert.IsNull(saved.AutoStartTunnelIds);
    }

    /// <summary>只有空白的备注等于没写:存成 null,资源管理器里也就不弹一块空的悬停提示。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \r\n \n\t")]
    public async Task Notes_WhenBlank_AreSavedAsNull(string? notes)
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", Notes = notes };

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.IsNull(saved.Notes);
    }

    /// <summary>
    /// 防空闲是一条会话自己的事:打开已存的配置要回显,保存要原样带回去,清成 0 要存回 null。
    /// </summary>
    /// <remarks>
    /// 会按空闲踢人的只是特定那几台机器(堡垒机、跳板机),所以这一项没有全局开关可跟随 ——
    /// 0 就是关。存回 null 而不是 0,是为了不给每条从没碰过这一项的配置留下假痕迹。
    /// </remarks>
    [TestMethod]
    public async Task AntiIdle_RoundTripsThroughTheEditDialog()
    {
        var existing = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            Terminal = new() { AntiIdleSeconds = 90 },
        };

        var vm = new ConnectionProfileViewModel(existing);
        Assert.AreEqual(90, vm.AntiIdleSeconds);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved);
        Assert.AreEqual(90, saved.Terminal?.AntiIdleSeconds);

        vm.AntiIdleSeconds = 0;
        SessionProfile? off = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(off);
        Assert.IsNull(off.Terminal?.AntiIdleSeconds, "关掉之后不该在配置里留下一段「设过,设的是关」。");
    }

    /// <summary>
    /// SSH 可选能力(压缩 / agent 转发 / X11 转发)打开编辑对话框要回显,保存要原样带回;
    /// 全部关掉要存回 null,不给配置留一段全 false 的对象。
    /// </summary>
    [TestMethod]
    public async Task SshFeatures_RoundTripThroughTheEditDialog()
    {
        var existing = new SessionProfile
        {
            Name = "far-away",
            Host = "10.0.0.2",
            Username = "ops",
            Ssh = new()
            {
                Compression = true,
                AgentForwarding = true,
                X11Forwarding = true,
                X11Display = "localhost:1.0",
                X11Trusted = false,
            },
        };

        var vm = new ConnectionProfileViewModel(existing);
        Assert.IsTrue(vm.SshCompression);
        Assert.IsTrue(vm.SshAgentForwarding);
        Assert.IsTrue(vm.SshX11Forwarding);
        Assert.AreEqual("localhost:1.0", vm.SshX11Display);
        Assert.IsFalse(vm.SshX11Trusted);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved?.Ssh);
        Assert.IsTrue(saved.Ssh.Compression);
        Assert.IsTrue(saved.Ssh.AgentForwarding);
        Assert.IsTrue(saved.Ssh.X11Forwarding);
        Assert.AreEqual("localhost:1.0", saved.Ssh.X11Display);
        Assert.IsFalse(saved.Ssh.X11Trusted);

        vm.SshCompression = false;
        vm.SshAgentForwarding = false;
        vm.SshX11Forwarding = false;
        SessionProfile? off = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(off);
        Assert.IsNull(off.Ssh, "三项都关了就不该留下一个全 false 的对象。");
    }

    /// <summary>
    /// SFTP 配置没有交互式 shell:两个转发存下来永远不生效,切回 SSH 时却会突然起作用 ——
    /// 所以只留压缩。
    /// </summary>
    [TestMethod]
    public async Task SshFeatures_SftpKeepsCompressionButDropsForwarding()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "files.example.com",
            Username = "root",
            Password = SecureStringConvert.FromPlaintext("secret"),
            SshCompression = true,
            SshAgentForwarding = true,
            SshX11Forwarding = true,
        };
        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(profile?.Ssh);
        Assert.IsTrue(profile.Ssh.Compression);
        Assert.IsFalse(profile.Ssh.AgentForwarding);
        Assert.IsFalse(profile.Ssh.X11Forwarding);
        Assert.IsNull(profile.Ssh.AgentForwardKeys, "SFTP 没有 shell,限定的密钥也不该留下");
        Assert.IsFalse(profile.Ssh.AgentForwardConfirm);
    }

    /// <summary>
    /// 按键时序混淆:SSH 配置打开回显、保存带回,只开它一项也要存下来(不能当成全空存回 null);
    /// SFTP 没有交互式 shell,存下来永远不生效、切回 SSH 时却会突然起作用,所以不存,页签圆点也不亮。
    /// </summary>
    [TestMethod]
    public async Task ObscureKeystrokeTiming_RoundTripsForSsh_AndIsNotStoredForSftp()
    {
        var existing = new SessionProfile
        {
            Name = "typing",
            Host = "10.0.0.4",
            Username = "ops",
            Password = "secret",
            Ssh = new() { ObscureKeystrokeTiming = true },
        };

        var vm = new ConnectionProfileViewModel(existing);
        Assert.IsTrue(vm.SshObscureKeystrokeTiming);
        Assert.IsTrue(vm.IsSshOptionsSectionModified);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved?.Ssh, "只开了这一项也是「有」,不能存回 null。");
        Assert.IsTrue(saved.Ssh.ObscureKeystrokeTiming);

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
        Assert.IsFalse(vm.IsSshOptionsSectionModified, "SFTP 不存这一项,圆点不该还亮着。");
        SessionProfile? sftp = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(sftp);
        Assert.IsNull(sftp.Ssh, "SFTP 配置没有交互式 shell,按键时序混淆不存。");
    }

    /// <summary>
    /// 按连接指定 agent 端点:回显、存回(去掉首尾空白;只填了它也不算空;SFTP 也存 —— SFTP 也能用 agent 认证),
    /// 清空就是系统默认(存 null);写法不对只提示、不拦保存;「只转发指定密钥」的候选从那个 agent 里列。
    /// </summary>
    [TestMethod]
    public async Task AgentEndpoint_RoundTrips_AndKeyChoicesComeFromThatAgent()
    {
        const string pipe = @"\\.\pipe\vela-pinned-agent";
        ISshKeyService keys = Substitute.For<ISshKeyService>();
        keys.ListAgentKeysAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        keys.ListAgentKeysAsync(pipe, Arg.Any<CancellationToken>())
            .Returns([new SshKeyInfo("pinned-agent-key", "ED25519", "", "", KeyLine(4))]);
        keys.ListKeysAsync(Arg.Any<CancellationToken>()).Returns([]);
        var existing = new SessionProfile
        {
            Name = "deploy",
            Host = "10.0.0.6",
            Username = "ops",
            AuthMethod = AuthMethod.Agent,
            Ssh = new() { AgentEndpoint = pipe },
        };

        var vm = new ConnectionProfileViewModel(existing, keyService: keys);
        Assert.AreEqual(pipe, vm.SshAgentEndpoint);
        Assert.IsTrue(vm.IsSshOptionsSectionModified, "填了端点,「SSH 选项」页签上要有圆点");

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreEqual(pipe, saved?.Ssh?.AgentEndpoint, "只填了端点也是「有」,不能存回 null");

        vm.SshAgentForwarding = true;
        vm.SshAgentForwardRestrict = true;
        await vm.LoadAgentForwardKeysAsync();
        Assert.AreEqual("pinned-agent-key", vm.AgentForwardKeyChoices.Single().Label, "转发的是这个 agent,候选也得从它里面列");

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
        vm.SshAgentEndpoint = "  ~/agents/deploy.sock  ";
        SessionProfile? sftp = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreEqual("~/agents/deploy.sock", sftp?.Ssh?.AgentEndpoint, "SFTP 也用 agent 认证:端点照存,~ 留到连接时展开");

        vm.SshAgentEndpoint = "agents/relative.sock";
        Assert.IsNotNull(vm.SshAgentEndpointError, "相对路径落在哪全看当前目录,要提示");
        SessionProfile? relative = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreEqual("agents/relative.sock", relative?.Ssh?.AgentEndpoint, "只提示、不拦保存:配置可能是别的平台上写的");

        vm.SshAgentEndpoint = "   ";
        Assert.IsNull(vm.SshAgentEndpointError);
        SessionProfile? cleared = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNull(cleared?.Ssh, "清空 = 系统默认,一项都没开时存 null");
    }

    // ---- 系统密钥库 ----

    private static SystemKeyInfo StoreKey(string name, string provider = SystemKeyReference.SoftwareProvider, string? subject = null) =>
        new(new SystemKeyReference { Provider = provider, KeyName = name, PublicKey = $"ecdsa-sha2-nistp256 AAAA{name} {name}" },
            "ecdsa-sha2-nistp256", 256, "SHA256:" + name, subject);

    private static ISystemKeyStore KeyStore(bool supported, params SystemKeyInfo[] keys)
    {
        ISystemKeyStore store = Substitute.For<ISystemKeyStore>();
        store.IsSupported.Returns(supported);
        store.ListKeysAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SystemKeyInfo>>(keys));
        return store;
    }

    /// <summary>
    /// 系统密钥库:选到这一段就列密钥库里的钥,选一把、存下的只是它的引用(不存密码、不挂共享凭据);
    /// 没选钥不许保存;「复制公钥」复制那一把的 OpenSSH 公钥行。
    /// </summary>
    [TestMethod]
    public async Task KeyStoreAuth_ListsTheKeys_AndSavesOnlyTheReference()
    {
        ISystemKeyStore store = KeyStore(true, StoreKey("soft-key"), StoreKey("tpm-key", SystemKeyReference.PlatformProvider, "Joe TPM"));
        var vm = new ConnectionProfileViewModel(systemKeyStore: store)
        {
            Host = "10.0.0.7",
            Username = "ops",
            Password = SecureStringConvert.FromPlaintext("not-for-key-store"),
        };
        string? copied = null;
        vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        Assert.IsTrue(vm.ShowKeyStoreAuthOption);

        vm.SelectAuthMethodCommand.Execute(AuthMethod.KeyStore).Subscribe();
        await vm.LoadSystemKeysAsync();
        Assert.IsTrue(vm.ShowKeyStoreFields);
        Assert.HasCount(2, vm.SystemKeyChoices);
        Assert.AreEqual("Joe TPM", vm.SystemKeyChoices[1].Title, "证书存储里有证书用这把钥时,拿证书主题当名字");

        SessionProfile? refused = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNull(refused, "没选钥不许保存");
        Assert.AreEqual(Strings.Get("Svc_KeyStoreKeyRequired"), vm.ErrorMessage);

        vm.SelectedSystemKey = vm.SystemKeyChoices[1];
        await vm.CopySystemKeyPublicKeyCommand.Execute().FirstAsync();
        Assert.AreEqual("ecdsa-sha2-nistp256 AAAAtpm-key tpm-key", copied);
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved);
        Assert.AreEqual(AuthMethod.KeyStore, saved.AuthMethod);
        Assert.AreEqual(SystemKeyReference.PlatformProvider, saved.KeyStoreKey?.Provider);
        Assert.AreEqual("tpm-key", saved.KeyStoreKey?.KeyName);
        Assert.IsNull(saved.Password, "系统密钥库不用密码:表单里留着的那个不存");
        Assert.IsNull(saved.CredentialSource);

        vm.SelectAuthMethodCommand.Execute(AuthMethod.Password).Subscribe();
        SessionProfile? switched = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNull(switched?.KeyStoreKey, "换了认证方式,引用不存 —— 不留看不见、切回来却生效的旧值");
    }

    /// <summary>存过的那一把这台电脑上找不到(别的电脑上建的、卡没插):照样列着、选中、标明找不到,打开再保存不丢。</summary>
    [TestMethod]
    public async Task KeyStoreAuth_KeepsASavedKeyThatIsNotHere()
    {
        SystemKeyReference saved = new() { Provider = SystemKeyReference.SmartCardProvider, KeyName = "card-key", PublicKey = "ssh-rsa AAAA card" };
        var existing = new SessionProfile
        {
            Name = "card",
            Host = "10.0.0.8",
            Username = "ops",
            AuthMethod = AuthMethod.KeyStore,
            KeyStoreKey = saved,
        };

        var vm = new ConnectionProfileViewModel(existing, systemKeyStore: KeyStore(true, StoreKey("other")));
        await vm.LoadSystemKeysAsync();

        Assert.HasCount(2, vm.SystemKeyChoices);
        Assert.IsTrue(vm.SelectedSystemKey?.IsMissing);
        Assert.AreSame(vm.SystemKeyChoices[0], vm.SelectedSystemKey);
        SessionProfile? again = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreEqual(saved, again?.KeyStoreKey);
    }

    /// <summary>
    /// 系统密钥库可以选配一张证书(与证书认证共用同一个字段与校验):留空不算错(就是按公钥认证),
    /// 填了就要文件在;存下的是钥的引用加证书路径。
    /// </summary>
    [TestMethod]
    public async Task KeyStoreAuth_CertificateIsOptional_AndSavedWithTheReference()
    {
        var vm = new ConnectionProfileViewModel(systemKeyStore: KeyStore(true, StoreKey("tpm-key", SystemKeyReference.PlatformProvider)))
        {
            Host = "10.0.0.10",
            Username = "ops",
        };
        vm.SelectAuthMethodCommand.Execute(AuthMethod.KeyStore).Subscribe();
        await vm.LoadSystemKeysAsync();
        vm.SelectedSystemKey = vm.SystemKeyChoices[0];

        Assert.IsNull(vm.CertificatePathError, "系统密钥库的证书是可选的:留空就是按公钥认证");
        vm.CertificatePath = Path.Combine(Path.GetTempPath(), $"vela-no-such-{Guid.NewGuid():N}-cert.pub");
        Assert.AreEqual(Strings.Get("Profile_ErrCertMissing"), vm.CertificatePathError, "填了就要文件在");

        string certificate = Path.Combine(Path.GetTempPath(), $"vela-keystore-{Guid.NewGuid():N}-cert.pub");
        await File.WriteAllTextAsync(certificate, "ecdsa-sha2-nistp256-cert-v01@openssh.com AAAA test\n");
        try
        {
            vm.CertificatePath = certificate;
            Assert.IsNull(vm.CertificatePathError);

            SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
            Assert.IsNotNull(saved);
            Assert.AreEqual(AuthMethod.KeyStore, saved.AuthMethod);
            Assert.AreEqual("tpm-key", saved.KeyStoreKey?.KeyName);
            Assert.AreEqual(certificate, saved.CertificatePath);
            Assert.IsNull(saved.PrivateKeyPath, "钥在密钥库里,不配私钥文件");
        }
        finally
        {
            File.Delete(certificate);
        }

        // 证书认证那一路照旧:证书是必填的。
        vm.CertificatePath = null;
        vm.SelectAuthMethodCommand.Execute(AuthMethod.Certificate).Subscribe();
        Assert.AreEqual(Strings.Get("Profile_ErrCertRequired"), vm.CertificatePathError);
    }

    /// <summary>
    /// 没有系统密钥库的平台上不显示这一段 —— 除非这条配置本来就是它(从 Windows 同步过来),
    /// 那就照样选中它、说明这台电脑上用不了,而不是让它看起来什么都没选。
    /// </summary>
    [TestMethod]
    public void KeyStoreOption_IsHiddenWithoutAKeyStore_UnlessTheProfileUsesIt()
    {
        ISystemKeyStore none = KeyStore(false);
        Assert.IsFalse(new ConnectionProfileViewModel(systemKeyStore: none).ShowKeyStoreAuthOption);

        var synced = new ConnectionProfileViewModel(
            new SessionProfile
            {
                Name = "from-windows",
                Host = "10.0.0.9",
                Username = "ops",
                AuthMethod = AuthMethod.KeyStore,
                KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.SoftwareProvider, KeyName = "k" },
            },
            systemKeyStore: none);
        Assert.IsTrue(synced.ShowKeyStoreAuthOption);
        Assert.IsTrue(synced.IsKeyStoreAuth);
        Assert.IsTrue(synced.KeyStoreUnsupportedHere);
    }

    // 三把测试用公钥(随手拼的 ed25519 blob,只用来算指纹与去重,不参与任何签名)。
    private static string KeyLine(byte seed, string? comment = null)
    {
        byte[] blob = [0, 0, 0, 11, .. "ssh-ed25519"u8, 0, 0, 0, 32, .. Enumerable.Repeat(seed, 32)];
        return $"ssh-ed25519 {Convert.ToBase64String(blob)}" + (comment is null ? "" : " " + comment);
    }

    /// <summary>限定的密钥与逐次确认:打开回显、原样保存;没动过清单时存过的钥一把都不能丢。</summary>
    [TestMethod]
    public async Task AgentForwardRestriction_RoundTripsThroughTheEditDialog()
    {
        var existing = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.3",
            Username = "ops",
            Ssh = new() { AgentForwarding = true, AgentForwardKeys = [KeyLine(1, "next-hop")], AgentForwardConfirm = true },
        };

        var vm = new ConnectionProfileViewModel(existing);
        Assert.IsTrue(vm.SshAgentForwardRestrict);
        Assert.IsTrue(vm.SshAgentForwardConfirm);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved?.Ssh);
        Assert.AreSequenceEqual([KeyLine(1, "next-hop")], [.. saved.Ssh.AgentForwardKeys!]);
        Assert.IsTrue(saved.Ssh.AgentForwardConfirm);

        // 关掉转发:两项附属设置一并不存,免得下次打开转发时悄悄生效
        vm.SshAgentForwarding = false;
        SessionProfile? off = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNull(off?.Ssh);
    }

    /// <summary>勾了「只转发选中的密钥」却一把都没选:不许保存 —— 那是一条永远不转发、用户却以为开着的配置。</summary>
    [TestMethod]
    public async Task AgentForwardRestriction_WithNothingSelected_CannotBeSaved()
    {
        ISshKeyService keys = Substitute.For<ISshKeyService>();
        keys.ListAgentKeysAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new SshKeyInfo("from-agent", "ED25519", "", "", KeyLine(2))]);
        keys.ListKeysAsync(Arg.Any<CancellationToken>()).Returns([]);
        var vm = new ConnectionProfileViewModel(keyService: keys)
        {
            Host = "10.0.0.4",
            Username = "ops",
            Password = SecureStringConvert.FromPlaintext("secret"),
            SshAgentForwarding = true,
            SshAgentForwardRestrict = true
        };
        await vm.LoadAgentForwardKeysAsync();

        SessionProfile? refused = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNull(refused);
        Assert.AreEqual(Strings.Get("Profile_SshAgentForwardKeysEmpty"), vm.ErrorMessage);

        vm.AgentForwardKeyChoices.Single().IsSelected = true;
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreSequenceEqual([KeyLine(2)], [.. saved!.Ssh!.AgentForwardKeys!]);
    }

    /// <summary>
    /// 候选清单:存过的 + agent 里的 + ~/.ssh 下的,按指纹去重。
    /// 存过的钥即使已不在别处也要列出并保持勾选,否则打开再保存就悄悄少了一把。
    /// </summary>
    [TestMethod]
    public async Task AgentForwardKeyChoices_MergeSavedAgentAndFileKeys()
    {
        ISshKeyService keys = Substitute.For<ISshKeyService>();
        keys.ListAgentKeysAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new SshKeyInfo("C:/keys/work", "ED25519", "", "", KeyLine(1, "C:/keys/work")),
            new SshKeyInfo("C:/keys/home", "ED25519", "", "", KeyLine(2, "C:/keys/home")),
        ]);
        keys.ListKeysAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SshKeyInfo("id_home", "ED25519", "", "C:/Users/u/.ssh/id_home", KeyLine(2, "home")),   // 与 agent 里那把同一把
            new SshKeyInfo("id_file", "ED25519", "", "C:/Users/u/.ssh/id_file", KeyLine(3)),
            new SshKeyInfo("no_pub", "RSA", "", "C:/Users/u/.ssh/no_pub", null),
        ]);
        var existing = new SessionProfile
        {
            Name = "b",
            Host = "h",
            Username = "u",
            Ssh = new() { AgentForwarding = true, AgentForwardKeys = [KeyLine(1), KeyLine(9, "gone")] },
        };

        var vm = new ConnectionProfileViewModel(existing, keyService: keys);
        await vm.LoadAgentForwardKeysAsync();

        List<AgentForwardKeyChoice> choices = [.. vm.AgentForwardKeyChoices];
        Assert.HasCount(4, choices, "1、9(存过)、2(agent 与 ~/.ssh 各一份,合并)、3");
        Assert.IsTrue(choices.Single(c => c.Label == "C:/keys/work").IsSelected, "存过、又在 agent 里找到:换成带名字的那行,仍勾选");
        Assert.IsTrue(choices.Single(c => c.Label == "gone").IsSelected, "存过、已不在别处:照样列出并勾选");
        Assert.IsFalse(choices.Single(c => c.Label == "C:/keys/home").IsSelected);
        Assert.IsFalse(choices.Single(c => c.Label == "id_file").IsSelected);
        Assert.IsFalse(vm.HasNoAgentForwardKeyChoices);
    }

    /// <summary>SSH Agent 保持枚举值 3,选中后密码与私钥字段都不再出现。</summary>
    [TestMethod]
    public async Task AgentAuth_KeepsItsEnumIndex_AndNeedsNoCredentialFields()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", AuthMethodIndex = 3 };

        Assert.AreEqual(AuthMethod.Agent, vm.AuthMethod);
        Assert.IsTrue(vm.IsAgentAuth);
        Assert.IsFalse(vm.ShowPasswordField);
        Assert.IsFalse(vm.ShowsPrivateKeyFields);
        Assert.IsNull(vm.PrivateKeyPathError);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreEqual(AuthMethod.Agent, saved?.AuthMethod);
    }

    [TestMethod]
    public void AntiIdle_IsClampedToTheSupportedRange()
    {
        var vm = new ConnectionProfileViewModel { AntiIdleSeconds = 99_999 };
        Assert.AreEqual(TerminalOverrides.MaxAntiIdleSeconds, vm.AntiIdleSeconds);

        vm.AntiIdleSeconds = -5;
        Assert.AreEqual(0, vm.AntiIdleSeconds, "负数没有意义,归零(= 关闭)。");
    }

    [TestMethod]
    public void PostAuthCommandDelay_IsClampedToTheSupportedRange()
    {
        var vm = new ConnectionProfileViewModel { PostAuthCommandDelaySeconds = 9999 };
        Assert.AreEqual(SessionProfile.MaxPostAuthCommandDelaySeconds, vm.PostAuthCommandDelaySeconds);

        vm.PostAuthCommandDelaySeconds = -1;
        Assert.AreEqual(0, vm.PostAuthCommandDelaySeconds, "0 是合法值:不等,握手完立刻发。");
    }

    /// <summary>
    /// 换到没有终端的协议(SFTP / FTP / 对象存储)时这一栏收起,存下去的也必须是 null。
    /// 留着的话它就是一条永远不执行的命令,而且切回 SSH 时会诈尸执行一次。
    /// </summary>
    [TestMethod]
    public async Task PostAuthCommand_OnProtocolsWithoutAShell_IsHiddenAndNotSaved()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", PostAuthCommand = "tmux attach" };

        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).FirstAsync();

        Assert.IsFalse(vm.SupportsPostAuthCommand);
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved);
        Assert.IsNull(saved.PostAuthCommand);
    }

    /// <summary>
    /// FTP / FTPS 的「默认打开路径」:保存时经 <c>FtpSettings</c> 的 setter 归一化,
    /// 重新打开配置要回显,且「高级」页签上要点出圆点(否则用户会当成配置丢了)。
    /// </summary>
    [TestMethod]
    public async Task FtpInitialRemotePath_IsNormalizedOnSave_AndRestoredOnEdit()
    {
        var vm = new ConnectionProfileViewModel { Host = "ftp.example.com", Username = "deploy" };
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).FirstAsync();
        vm.FtpInitialRemotePath = @"  \var\www\html\  ";

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual("/var/www/html", saved.Ftp?.InitialRemotePath);

        var reopened = new ConnectionProfileViewModel(saved);
        Assert.AreEqual("/var/www/html", reopened.FtpInitialRemotePath);
        Assert.IsTrue(reopened.ShowAdvancedSection);
        Assert.IsTrue(reopened.IsAdvancedSectionModified, "填过的路径不能藏得看不出来。");
    }

    /// <summary>
    /// FTP 的「服务器时区」:默认「与本机相同」(存 null,旧行为);选了偏移就按分钟存、重开回显、「高级」页签点出圆点;
    /// 手改出来的、不在下拉表里的偏移重开时也要留着,不能被悄悄改掉。
    /// </summary>
    [TestMethod]
    public async Task FtpServerTimeZone_DefaultsToLocal_IsSavedAsMinutes_AndRestoredOnEdit()
    {
        var vm = new ConnectionProfileViewModel { Host = "ftp.example.com", Username = "deploy" };
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).FirstAsync();
        Assert.IsNull(vm.SelectedFtpTimeZone.Minutes);
        Assert.IsFalse(vm.IsAdvancedSectionModified);

        vm.SelectedFtpTimeZone = vm.FtpTimeZoneOptions.Single(o => o.Minutes == 345);
        Assert.AreEqual("UTC+05:45", vm.SelectedFtpTimeZone.Label);
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual(345, saved.Ftp?.ServerUtcOffsetMinutes);
        var reopened = new ConnectionProfileViewModel(saved);
        Assert.AreEqual(345, reopened.SelectedFtpTimeZone.Minutes);
        Assert.IsTrue(reopened.IsAdvancedSectionModified, "配过的时区不能藏得看不出来。");

        reopened.SelectedFtpTimeZone = reopened.FtpTimeZoneOptions[0];
        SessionProfile? cleared = await reopened.SaveCommand.Execute().FirstAsync();
        Assert.IsNull(cleared?.Ftp?.ServerUtcOffsetMinutes, "「与本机相同」存 null,即旧行为");

        saved.Ftp!.ServerUtcOffsetMinutes = 7 * 60 + 15;
        var odd = new ConnectionProfileViewModel(saved);
        Assert.AreEqual(435, odd.SelectedFtpTimeZone.Minutes);
        Assert.AreEqual("UTC+07:15", odd.SelectedFtpTimeZone.Label);
        Assert.AreEqual(435, (await odd.SaveCommand.Execute().FirstAsync())?.Ftp?.ServerUtcOffsetMinutes);
    }

    /// <summary>这块设置只属于 FTP;换成别的协议时整个 <c>Ftp</c> 块不落盘,路径自然一起走。</summary>
    [TestMethod]
    public async Task FtpInitialRemotePath_IsNotSavedOnNonFtpProtocols()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u" };
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).FirstAsync();
        vm.FtpInitialRemotePath = "/pub";
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.IsNull(saved.Ftp);
    }

    [TestMethod]
    public void PluginFields_MarkedAdvanced_LiveOnTheAdvancedPage()
    {
        // S3 这类协议一口气声明十来个字段,全铺在「常规」页会把表单拉得很长。
        // 标了 IsAdvanced 的调优项放到「高级」页,页签上报出字段数。
        var vm = new ConnectionProfileViewModel();
        vm.PluginFields.Add(new(new() { Key = "region", Label = "区域" }, null));
        vm.PluginFields.Add(new(new() { Key = "partSize", Label = "分片大小", IsAdvanced = true }, null));
        vm.PluginFields.Add(new(new() { Key = "concurrency", Label = "并发分片数", IsAdvanced = true }, null));

        Assert.IsTrue(vm.IsGeneralSection);
        Assert.IsTrue(vm.PluginFields[0].IsRowVisible, "连得上连不上取决于它的字段必须在「常规」页上。");
        Assert.IsFalse(vm.PluginFields[1].IsRowVisible);
        Assert.IsFalse(vm.PluginFields[2].IsRowVisible);
        Assert.IsTrue(vm.ShowAdvancedSection);
        Assert.IsTrue(vm.HasAdvancedBadge);
        Assert.AreEqual("2", vm.AdvancedBadge);

        vm.SelectSectionCommand.Execute(ConnectionProfileSection.Advanced).Subscribe();
        Assert.IsTrue(vm.IsAdvancedSection);
        Assert.IsFalse(vm.PluginFields[0].IsRowVisible, "常用字段在「常规」页,不在「高级」页上重复出现。");
        Assert.IsTrue(vm.PluginFields[1].IsRowVisible);
        Assert.IsTrue(vm.PluginFields[2].IsRowVisible);
        // 页签上的数字不随切页变:它说的是「这一页有几项」,不是「还有几项没看见」。
        Assert.AreEqual("2", vm.AdvancedBadge);

        vm.SelectSectionCommand.Execute(ConnectionProfileSection.General).Subscribe();
        Assert.HasCount(2, vm.PluginFields.Where(field => !field.IsRowVisible));
    }

    /// <summary>
    /// 最后一个高级字段没了(切去别的协议、换了一个没有调优项的插件),停在「高级」页就是一片空白
    /// 而且页签上没有一个是选中的 —— 必须落回「常规」。
    /// </summary>
    [TestMethod]
    public void AdvancedPage_FallsBackToGeneral_WhenItsLastFieldGoes()
    {
        var vm = new ConnectionProfileViewModel();
        vm.PluginFields.Add(new(new() { Key = "partSize", Label = "分片大小", IsAdvanced = true }, null));
        vm.SelectedSection = ConnectionProfileSection.Advanced;
        Assert.IsTrue(vm.IsAdvancedSection);

        vm.PluginFields.RemoveAt(0);

        Assert.IsFalse(vm.ShowAdvancedSection);
        Assert.IsTrue(vm.IsGeneralSection);
    }

    /// <summary>没有协议级凭据的终端协议(Telnet)替身:只用来把描述符送进视图模型。</summary>
    private sealed class NoCredentialTerminal : PluginSdk.Protocols.IProtocolTerminal
    {
        public Task<PluginSdk.Protocols.IProtocolTerminalSession> ConnectAsync(
            PluginSdk.Protocols.ProtocolConnectRequest request,
            PluginSdk.Protocols.ProtocolTerminalOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task PluginProtocol_WithoutCredentials_HidesUsernameAndPassword_AndStillSaves()
    {
        // Telnet 的登录发生在带内(对端打印 login:)。摆着两个填了也发不出去的框会误导用户,
        // 而"用户名不能为空"这条更会把保存/连接按钮永久灰死 —— 无凭据协议必须两条都免掉。
        var registry = new PluginProtocolRegistry();
        using IDisposable handle = registry.Register("test.telnet", new()
        {
            Id = "test.telnet",
            DisplayName = "Telnet",
            DefaultPort = 23,
            Features = PluginSdk.Protocols.ProtocolFeatures.NoCredentials
        }, new NoCredentialTerminal());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute("test.telnet").FirstAsync();

        Assert.IsFalse(vm.ShowCredentialFields, "无凭据协议不该显示用户名一行。");
        Assert.IsFalse(vm.ShowPasswordField, "同上,口令与「记住密码」也一并收起。");
        Assert.IsTrue(vm.AllowsAnonymous, "NoCredentials 蕴含匿名,否则按钮永远灰着。");
        Assert.AreEqual(23, vm.Port, "端口应跟随协议默认值。");

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile, "用户名为空也必须存得下去。");
        Assert.AreEqual(ConnectionType.Plugin, profile.ConnectionType);
        Assert.AreEqual("test.telnet", profile.PluginProtocolId);
    }

    /// <summary>工作台替身:只用来把带变体的描述符送进视图模型,从不真的打开。</summary>
    private sealed class VariantWorkspace : PluginSdk.Workspaces.IWorkspaceProvider
    {
        public Task<PluginSdk.Workspaces.IWorkspaceDocument> OpenAsync(
            PluginSdk.Workspaces.WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// 数据库式的工作台:一个页签,「类型」那一栏选 MySQL(3306)/ PostgreSQL(5432)/ SQLite 三档。
    /// SQLite 是磁盘上的一个文件:没有凭据、没有端点,「主机」那一栏改标成「数据库文件」。
    /// </summary>
    private static PluginProtocolRegistry RegisterVariantWorkspace(
        string id, out IDisposable handle)
    {
        var registry = new PluginProtocolRegistry();
        handle = registry.RegisterWorkspace(id, new()
        {
            Id = id,
            DisplayName = "数据库",
            DefaultPort = 3306,
            // 基础描述符是"要凭据"的那一档 —— 漏套变体时 SQLite 也会冒出两栏凭据。
            Features = PluginSdk.Workspaces.WorkspaceFeatures.CertificateTrust,
            Fields =
            [
                new()
                {
                    Key = "dialect",
                    Label = "数据库类型",
                    Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL"), new("sqlite", "SQLite")]
                }
            ],
            VariantKey = "dialect",
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 },
                new()
                {
                    Value = "sqlite",
                    DefaultPort = 1,
                    HostLabel = "数据库文件",
                    Features = PluginSdk.Workspaces.WorkspaceFeatures.NoCredentials
                               | PluginSdk.Workspaces.WorkspaceFeatures.NoEndpoint
                }
            ]
        }, new VariantWorkspace());
        return registry;
    }

    /// <summary>
    /// <b>连接框本身跟着"类型"那一栏变形</b> —— 端口、"主机"那一栏的含义、要不要凭据。
    /// <para>
    /// 一个插件用一个页签承载一族相近的连接类型时,这几样**是随方言变的**:
    /// 用户选了 PostgreSQL,端口就该是 5432 而不是 MySQL 的 3306。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_Variant_ReshapesTheConnectionBox()
    {
        PluginProtocolRegistry registry = RegisterVariantWorkspace("test.db", out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();

            Assert.AreEqual(3306, vm.Port, "默认方言那一档的端口。");
            Assert.IsTrue(vm.ShowCredentialFields, "MySQL 是要凭据的。");

            PluginProtocolFieldViewModel dialect = vm.PluginFields.Single(f => f.Key == "dialect");

            dialect.Text = "postgresql";
            Assert.AreEqual(5432, vm.Port, "换方言之后端口要跟着走 —— 否则选了 PG 却连去 MySQL 的端口。");

            dialect.Text = "sqlite";
            Assert.AreEqual("数据库文件", vm.HostLabel, "SQLite 的\"主机\"那一栏装的是文件路径。");
            Assert.IsFalse(vm.ShowCredentialFields, "SQLite 就是个文件,填了用户名也没地方会用到。");
            Assert.IsTrue(vm.AllowsAnonymous, "NoCredentials 蕴含匿名,否则按钮永远灰着。");

            // 切回去要能**完全**恢复:变体每次都从原始描述重新套,而不是层层叠加。
            dialect.Text = "mysql";
            Assert.AreEqual(3306, vm.Port);
            Assert.IsTrue(vm.ShowCredentialFields);
            Assert.IsFalse(vm.AllowsAnonymous, "MySQL 要凭据:匿名不能粘在上一档上。");
            Assert.AreNotEqual("数据库文件", vm.HostLabel, "上一个变体的标签粘住了 —— 变体是覆盖,不是累加。");
        }
    }

    /// <summary>
    /// <b>用户自己填过的端口不会被变体盖掉。</b>与切换页签用的是同一条判定 ——
    /// 用户把端口改成 13306(容器映射)再换方言,自己填的端口不能没了。
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_Variant_DoesNotOverwriteAHandTypedPort()
    {
        PluginProtocolRegistry registry = RegisterVariantWorkspace("test.db", out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();

            vm.Port = 13306;
            vm.PluginFields.Single(f => f.Key == "dialect").Text = "postgresql";

            Assert.AreEqual(13306, vm.Port, "用户手填的端口被变体盖掉了。");
        }
    }

    /// <summary>
    /// <b>重复点当前插件页签,不能把已经跟着变体走的端口拉回页签默认值。</b>
    /// <para>
    /// <c>IsProtocolDefaultPort</c> 把**变体端口**也算作"用户没手填过",而端口跟随发生在
    /// "重复点当前页签"那条早退之前。直接写页签默认值的话,选了 PostgreSQL(5432)之后
    /// 再点一次同一个页签,端口会被改写成 3306 再从早退处返回 —— 下拉仍写着 PostgreSQL。
    /// 目标端口先按变体算好再写,所以端口<b>一次都不该动</b>(不是先改成 3306 再改回来)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_ReselectingTheSameTab_KeepsTheVariantPort()
    {
        PluginProtocolRegistry registry = RegisterVariantWorkspace("test.db", out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();
            vm.PluginFields.Single(f => f.Key == "dialect").Text = "postgresql";
            Assert.AreEqual(5432, vm.Port, "前置条件:变体端口已经跟上来了。");

            List<int> ports = [];
            ((System.ComponentModel.INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionProfileViewModel.Port))
                {
                    ports.Add(vm.Port);
                }
            };

            // 再点一次**同一个**页签。
            await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();

            Assert.AreEqual(5432, vm.Port, "重复点当前页签把变体端口拉回了页签默认值。");
            Assert.IsEmpty(ports, $"端口不该被动过,实际依次变成了:{string.Join(" → ", ports)}");
            Assert.AreEqual(
                "postgresql",
                vm.PluginFields.Single(f => f.Key == "dialect").Text,
                "方言下拉不该被动过 —— 它与端口必须一直是一致的。");
        }
    }

    /// <summary>
    /// <b>从插件变体切回内建协议时,变体端口要跟着还原。</b>
    /// <para>
    /// 判定用的 <c>IsProtocolDefaultPort</c> 要靠 <c>_pluginBaseForm</c> 才认得出变体端口,
    /// 而切回内建协议那一支会先把它置空 —— 等到方法末尾再判,5432 已经取不到变体信息、
    /// 会被当成"用户手填的"留下来,新建的 SSH 配置停在 5432 上。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_SwitchingBackToSsh_RestoresTheDefaultPort()
    {
        PluginProtocolRegistry registry = RegisterVariantWorkspace("test.db", out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();
            vm.PluginFields.Single(f => f.Key == "dialect").Text = "postgresql";
            Assert.AreEqual(5432, vm.Port, "前置条件:变体端口已经跟上来了。");

            await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();

            Assert.AreEqual(22, vm.Port, "切回 SSH 之后端口停在了 5432 上。");
        }
    }

    /// <summary>
    /// <b>从带变体的页签换到另一个插件页签,目标端口是新页签的,不是上一个页签的变体端口。</b>
    /// <para>
    /// 端口跟随在清掉上一个协议的表单之前算目标;那一刻 <c>_pluginBaseForm</c> 还是上一个页签的,
    /// 拿它的变体表算,端口会停在 PostgreSQL 的 5432 上,而新页签是 Redis。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_SwitchingToAnotherPluginTab_TakesThatTabsPort()
    {
        PluginProtocolRegistry registry = RegisterVariantWorkspace("test.db", out IDisposable handle);
        using (handle)
        using (registry.RegisterWorkspace("test.cache", new()
        {
            Id = "test.cache",
            DisplayName = "缓存",
            DefaultPort = 6379
        }, new VariantWorkspace()))
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();
            vm.PluginFields.Single(f => f.Key == "dialect").Text = "postgresql";
            Assert.AreEqual(5432, vm.Port, "前置条件:变体端口已经跟上来了。");

            await vm.SelectPluginProtocolCommand.Execute("test.cache").FirstAsync();

            Assert.AreEqual(6379, vm.Port, "换了页签,端口却还停在上一个页签的变体端口上。");
        }
    }

    /// <summary>
    /// <b>没有端点的那一档,端口那一栏要收起来 —— 而且切回去还得出现。</b>
    /// <para>
    /// SQLite 是磁盘上的一个文件,端口那一栏填什么都不会被拼进连接串,留着只会让用户以为它有意义。
    /// 顺带守住两条边界:主机那一栏<b>不能</b>跟着收(文件路径正是填在它里面),
    /// 以及收起端口之后保存按钮不能因此灰掉 —— 端口的**取值**照旧在 1–65535 内。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_VariantWithoutEndpoint_HidesThePortBox()
    {
        PluginProtocolRegistry registry = RegisterVariantWorkspace("test.db", out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = @"D:\data\app.db" };
            await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();

            Assert.IsTrue(vm.ShowPortField, "MySQL 是有端点的,端口那一栏必须在。");

            PluginProtocolFieldViewModel dialect = vm.PluginFields.Single(f => f.Key == "dialect");

            // ShowPortField 是纯计算属性:不补发通知的话,值变了而界面上那一栏纹丝不动。
            // 所以这里连通知一起验。
            List<string?> changed = [];
            ((System.ComponentModel.INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            dialect.Text = "sqlite";
            Assert.IsFalse(vm.ShowPortField, "SQLite 没有端点,端口那一栏还留着上一个方言的残值。");
            Assert.Contains(nameof(vm.ShowPortField), changed,
                "换变体时没给 ShowPortField 补发通知,绑在它上面的那一栏不会跟着刷新。");
            Assert.AreEqual("数据库文件", vm.HostLabel, "主机那一栏不能跟着收 —— 文件路径就填在它里面。");
            Assert.IsTrue(await vm.SaveCommand.CanExecute.FirstAsync(),
                "收起一栏不该顺手把保存按钮堵死:端口的取值仍在合法区间内。");

            // 切回去要完全恢复:变体的能力位是整体替换,不该有"收起来就再也不出现"。
            dialect.Text = "mysql";
            Assert.IsTrue(vm.ShowPortField, "换回有端点的方言,端口那一栏没回来。");
        }
    }

    /// <summary>没有网络端点的终端协议(串口)替身,兼动态候选项来源。</summary>
    private sealed class FakeSerialTerminal(params string[] ports)
        : PluginSdk.Protocols.IProtocolTerminal, PluginSdk.Protocols.IProtocolChoiceSource
    {
        /// <summary>被问过几次候选项(用来验证刷新真的重取,而不是拿缓存糊弄)。</summary>
        public int Queries { get; private set; }

        /// <summary>下一次要给出的端口(模拟热插拔)。</summary>
        public string[] Ports { get; set; } = ports;

        public Task<PluginSdk.Protocols.IProtocolTerminalSession> ConnectAsync(
            PluginSdk.Protocols.ProtocolConnectRequest request,
            PluginSdk.Protocols.ProtocolTerminalOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginSdk.Protocols.ProtocolSettingChoice>> GetChoicesAsync(
            string fieldKey, CancellationToken cancellationToken = default)
        {
            Queries++;
            return Task.FromResult<IReadOnlyList<PluginSdk.Protocols.ProtocolSettingChoice>>(
                fieldKey == PluginSdk.Protocols.ProtocolDescriptor.HostFieldKey
                    ? [.. Ports.Select(port => new PluginSdk.Protocols.ProtocolSettingChoice(port, $"Fake ({port})"))]
                    : []);
        }
    }

    private static PluginProtocolRegistry RegisterSerial(
        FakeSerialTerminal terminal, out IDisposable handle)
    {
        var registry = new PluginProtocolRegistry();
        handle = registry.Register("test.serial", new()
        {
            Id = "test.serial",
            DisplayName = "串口",
            DefaultPort = 22,
            HostLabel = "串口设备",
            HostKind = PluginSdk.Protocols.ProtocolSettingKind.DynamicChoice,
            HostAllowsCustomValue = true,
            Features = PluginSdk.Protocols.ProtocolFeatures.NoEndpoint
                       | PluginSdk.Protocols.ProtocolFeatures.NoCredentials
        }, terminal);
        return registry;
    }

    [TestMethod]
    public async Task PluginProtocol_WithoutAnEndpoint_HidesThePortColumnButKeepsTheButtonsUsable()
    {
        // 串口的目标不是 host:port:端口那一栏填什么都不会被用上,摆着只会让用户以为它有意义,
        // 而且还留着上一个协议的残值。但它的**取值**必须照旧参与按钮判定 ——
        // 收起一栏不该顺手把保存/连接按钮堵死。
        var terminal = new FakeSerialTerminal("COM3");
        PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };

            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            Assert.IsFalse(vm.ShowPortField);
            Assert.IsTrue(vm.Port is >= 1 and <= 65535, "端口仍是个合法值,只是不显示。");
            SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
            Assert.IsNotNull(profile, "没有端口栏,也必须存得下去。");
            Assert.AreEqual("COM3", profile.Host, "设备名走的正是「主机」那一栏。");
        }
    }

    [TestMethod]
    public async Task PluginProtocol_WithADynamicHostColumn_FetchesTheChoicesWhenTheFormOpens()
    {
        var terminal = new FakeSerialTerminal("COM3", "COM7");
        PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };

            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            Assert.IsTrue(vm.HostIsEditableChoice, "可手输:枚举不到的设备也必须填得进去。");
            Assert.IsTrue(vm.HostIsDynamicChoice, "动态:USB 转串口是热插拔的,得给刷新按钮。");
            Assert.IsFalse(vm.HostIsText);
            Assert.AreSequenceEqual(["COM3", "COM7"], [.. vm.HostChoices.Select(choice => choice.Value)]);
            Assert.AreEqual("串口设备", vm.HostLabel);
        }
    }

    [TestMethod]
    public async Task RefreshHostChoices_PicksUpADeviceThatWasPluggedInAfterTheDialogOpened()
    {
        // 这个按钮存在的全部理由:用户很可能是先打开连接对话框、才想起去插线。
        var terminal = new FakeSerialTerminal("COM3");
        PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };
            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();
            terminal.Ports = ["COM3", "COM9"];

            await vm.RefreshHostChoicesCommand.Execute().FirstAsync();

            Assert.AreSequenceEqual(["COM3", "COM9"], [.. vm.HostChoices.Select(choice => choice.Value)]);
            Assert.AreEqual(2, terminal.Queries, "刷新必须是真的重取,而不是拿缓存糊弄。");
        }
    }

    [TestMethod]
    public async Task DynamicHostColumn_KeepsAStoredDeviceThatIsNotPluggedInRightNow()
    {
        // 一条存着 COM7 的配置在适配器没插的时候打开,值被悄悄改写成 COM3 再保存下去,
        // 是一次**静默的数据损坏** —— 用户下次插上线才发现配置指到别处去了。
        var terminal = new FakeSerialTerminal("COM3");
        PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM7" };

            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            Assert.AreEqual("COM7", vm.Host);
            Assert.IsNull(vm.SelectedHostChoice, "对不上候选项就是没有选中项,当前值由文本框自己显示。");
            SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
            Assert.AreEqual("COM7", profile!.Host);
        }
    }

    [TestMethod]
    public async Task SwitchingBackToSsh_RestoresThePlainHostTextBoxAndThePortColumn()
    {
        var terminal = new FakeSerialTerminal("COM3");
        PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };
            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();

            Assert.IsTrue(vm.ShowPortField);
            Assert.IsTrue(vm.HostIsText, "切回 SSH 后主机那格必须是普通文本框,不能还挂着串口下拉。");
            Assert.IsEmpty(vm.HostChoices);
        }
    }

    [TestMethod]
    public void EditableChoiceField_DoesNotRewriteAValueThatIsNotInTheList()
    {
        // 波特率:表里给九个常用值,但 250000(Marlin 固件)得填得进去 ——
        // 把表当白名单等于告诉这些用户"本工具不支持你的设备"。
        var field = new PluginProtocolFieldViewModel(new()
        {
            Key = "baudRate",
            Label = "波特率",
            Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
            AllowsCustomValue = true,
            DefaultValue = "115200",
            Choices = [new("9600", "9600"), new("115200", "115200")]
        }, "250000");

        Assert.AreEqual("250000", field.Text);
        Assert.IsTrue(field.IsEditableChoice);
        Assert.IsFalse(field.IsChoice);
        Assert.IsNull(field.SelectedChoice);
    }

    [TestMethod]
    public void ReadOnlyChoiceField_StillNormalisesAValueThatIsNotInTheList()
    {
        // 封闭枚举的老行为不变:值对不上就归一到第一项,免得下拉空着没法解释。
        var field = new PluginProtocolFieldViewModel(new()
        {
            Key = "parity",
            Label = "校验位",
            Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
            Choices = [new("none", "无"), new("even", "偶校验")]
        }, "whatever");

        Assert.AreEqual("none", field.Text);
    }

    [TestMethod]
    public void ChoiceItem_StringifiesToTheStoredValue() =>
        // 可编辑下拉在用户选中一项时,会拿 item.ToString() 去填那个文本框 ——
        // 而那就是接下来落盘的值。返回展示文案的话,存进配置的会是
        // "USB-SERIAL CH340 (COM3)" 这种打不开的东西。
        Assert.AreEqual("COM3", new PluginChoiceItem("COM3", "USB-SERIAL CH340 (COM3)").ToString());

    /// <summary>
    /// 声明了显示条件的字段只在条件成立时出现,并且**改一下就跟着变** ——
    /// 这正是它要解决的问题:Redis 的「主节点名」只有哨兵模式有意义,
    /// 原先它在独立形态下照样显示,靠字段下面一行小字"仅哨兵模式"解释。
    /// </summary>
    [TestMethod]
    public void PluginFields_VisibleWhen_FollowsTheFieldItDependsOn()
    {
        var vm = new ConnectionProfileViewModel();
        vm.PluginFields.Add(new(new()
        {
            Key = "mode",
            Label = "部署形态",
            Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
            DefaultValue = "standalone",
            Choices = [new("standalone", "独立"), new("sentinel", "哨兵"), new("cluster", "集群")]
        }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "masterName",
            Label = "主节点名",
            VisibleWhen = new("mode", "sentinel")
        }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "database",
            Label = "默认数据库",
            VisibleWhen = new("mode", ["standalone", "sentinel"])
        }, null));

        PluginProtocolFieldViewModel master = vm.PluginFields[1];
        PluginProtocolFieldViewModel database = vm.PluginFields[2];

        Assert.IsFalse(master.IsRowVisible, "独立形态下不该出现哨兵专用的主节点名。");
        Assert.IsTrue(database.IsRowVisible, "独立形态有多数据库。");

        vm.PluginFields[0].Text = "sentinel";
        Assert.IsTrue(master.IsRowVisible, "切到哨兵后主节点名要出现。");
        Assert.IsTrue(database.IsRowVisible);

        vm.PluginFields[0].Text = "cluster";
        Assert.IsFalse(master.IsRowVisible);
        Assert.IsFalse(database.IsRowVisible, "集群只有 db0,数据库那格不该出现。");
    }

    /// <summary>
    /// 条件不成立时字段只是**看不见**,值照常保留并落盘 —— 与 IsHidden 同一套存取语义。
    /// 反过来做的话,用户在哨兵下填了主节点名、切去独立瞄一眼再切回来,
    /// 会发现自己填的东西被界面顺手清掉了。
    /// </summary>
    [TestMethod]
    public async Task PluginFields_HiddenByCondition_KeepTheirValues()
    {
        var vm = new ConnectionProfileViewModel { Host = "127.0.0.1", Username = "default" };
        await vm.SelectPluginProtocolCommand.Execute("velashell.s3").FirstAsync();
        vm.PluginFields.Add(new(new()
        {
            Key = "mode",
            Label = "部署形态",
            DefaultValue = "sentinel"
        }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "masterName",
            Label = "主节点名",
            VisibleWhen = new("mode", "sentinel")
        }, "mymaster"));

        Assert.IsTrue(vm.PluginFields[^1].IsRowVisible);
        vm.PluginFields[^2].Text = "standalone";
        Assert.IsFalse(vm.PluginFields[^1].IsRowVisible, "条件不再成立,字段应从表单上消失。");

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.IsNotNull(profile.PluginSettings);
        Assert.AreEqual("mymaster", profile.PluginSettings["masterName"],
            "看不见 ≠ 被清掉:值必须原样带回。");
    }

    /// <summary>切到「高级」页也不该把当前不适用的字段翻出来 —— 两个条件是与关系。</summary>
    [TestMethod]
    public void PluginFields_VisibleWhen_BeatsTheAdvancedPage()
    {
        var vm = new ConnectionProfileViewModel();
        vm.PluginFields.Add(new(new() { Key = "mode", Label = "形态", DefaultValue = "standalone" }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "sentinelTuning",
            Label = "哨兵调优",
            IsAdvanced = true,
            VisibleWhen = new("mode", "sentinel")
        }, null));
        vm.PluginFields.Add(new(new() { Key = "timeout", Label = "超时", IsAdvanced = true }, null));

        vm.SelectSectionCommand.Execute(ConnectionProfileSection.Advanced).Subscribe();
        Assert.IsTrue(vm.IsAdvancedSection);
        Assert.IsFalse(vm.PluginFields[1].IsRowVisible, "不适用的字段,切到「高级」页也不该出现。");
        // 页签上的数字只数眼下适用的:点进去看不到的那一项不该算进去。
        Assert.AreEqual("1", vm.AdvancedBadge);

        vm.PluginFields[0].Text = "sentinel";
        Assert.IsTrue(vm.PluginFields[1].IsRowVisible);
        Assert.AreEqual("2", vm.AdvancedBadge);
    }

    [TestMethod]
    public async Task PluginFields_CollapsedAdvancedValues_StillGetSaved()
    {
        // 分页只是显示层面的事:落盘必须把高级字段一并带上,
        // 否则用户填过一次分片大小、切回「常规」页再保存,值就静默丢了。
        var vm = new ConnectionProfileViewModel
        {
            Host = "s3.example.com",
            Username = "AKIA",
        };
        await vm.SelectPluginProtocolCommand.Execute("velashell.s3").FirstAsync();
        vm.PluginFields.Add(new(new() { Key = "partSize", Label = "分片大小", IsAdvanced = true }, "16777216"));
        Assert.IsFalse(vm.PluginFields[0].IsRowVisible, "在「高级」页上,「常规」页看不到它。");
        Assert.IsTrue(vm.IsAdvancedSectionModified, "填过的高级字段要在页签上点出来。");

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.AreEqual(ConnectionType.Plugin, profile.ConnectionType);
        Assert.IsNotNull(profile.PluginSettings);
        Assert.AreEqual("16777216", profile.PluginSettings["partSize"]);
    }

    [TestMethod]
    public async Task SaveCommand_MaterializesSecurePasswordIntoProfile()
    {
        // 无 workflow 时 SaveCommand 直接返回 BuildProfile 的结果,便于校验 SecureString → 明文交接。
        var vm = new ConnectionProfileViewModel
        {
            Name = "prod",
            Host = "h",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = SecureStringConvert.FromPlaintext("s3cret")
        };
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.AreEqual("s3cret", profile.Password);
    }

    [TestMethod]
    public async Task NewProfile_DefaultsToSsh_AndCanSaveSftp()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            Password = SecureStringConvert.FromPlaintext("secret"),
        };

        Assert.AreEqual(ConnectionType.SSH, vm.ConnectionType);
        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(profile);
        Assert.AreEqual(ConnectionType.SFTP, profile.ConnectionType);
        Assert.IsTrue(vm.IsSftpSelected);
        Assert.IsFalse(vm.IsSshSelected);
    }

    [TestMethod]
    public void EditProfile_ReopensSelectedProtocol()
    {
        var existing = new SessionProfile
        {
            ConnectionType = ConnectionType.SFTP,
            Host = "files.example.com",
            Username = "root",
        };

        var vm = new ConnectionProfileViewModel(existing);

        Assert.AreEqual(ConnectionType.SFTP, vm.ConnectionType);
        Assert.IsTrue(vm.IsSftpSelected);
        Assert.IsFalse(vm.IsSshSelected);
    }

    [TestMethod]
    public async Task SaveCommand_UsesWorkflowServiceAndReturnsSavedProfile()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        var expected = new SessionProfile
        {
            Name = "prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
        workflow.SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(expected);
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        SessionProfile? result = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreSame(expected, result);
        await workflow.Received(1).SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task TestConnectionCommand_StoresSuccessState()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(true));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.LastTestSucceeded);
        Assert.IsNull(vm.ErrorMessage);
    }

    [TestMethod]
    public async Task CopyErrorCommand_CopiesFailureText_AndAcknowledgesWithCopiedState()
    {
        // 连接失败的原文(认证链、主机密钥指纹、栈顶异常)常常一长串,
        // 用户要拿去搜索或贴给同事 —— 照着屏幕抄不现实,复制按钮就是为它准备的。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(false, "Permission denied (publickey,password)."));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        string? copied = null;
        vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.HasError, "失败之后必须有可复制的错误信息,按钮才出得来。");
        Assert.IsFalse(vm.ErrorCopied);

        await vm.CopyErrorCommand.Execute().FirstAsync();
        Assert.AreEqual("Permission denied (publickey,password).", copied);
        // 没有这句回执就分不清"复制成功了"和"按钮没反应",用户只会再点两下。
        Assert.IsTrue(vm.ErrorCopied);
    }

    [TestMethod]
    public async Task CopyErrorCommand_IsUnavailable_WhenThereIsNoError()
    {
        // 成功那一条没有可复制的内容:命令必须自己灰掉,而不是复制一个空串。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(true));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);

        Assert.IsFalse(await vm.CopyErrorCommand.CanExecute.FirstAsync());
        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.HasError);
        Assert.IsFalse(await vm.CopyErrorCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task NewError_ClearsPreviousCopiedAcknowledgement()
    {
        // 「已复制」是对某一段具体文本说的:换了一条错误还顶着旧回执,
        // 用户会以为新的这条也已经在剪贴板里了。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(false, "第一条"), new ConnectionTestResult(false, "第二条"));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        vm.CopyToClipboard = _ => Task.CompletedTask;

        await vm.TestConnectionCommand.Execute().FirstAsync();
        await vm.CopyErrorCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.ErrorCopied);

        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.AreEqual("第二条", vm.ErrorMessage);
        Assert.IsFalse(vm.ErrorCopied);
    }

    [TestMethod]
    public async Task SaveCommand_CreatesNewGroup_WhenGroupTextIsUnknown()
    {
        // 分组框输入了不存在的分组名:保存时应新建分组落库,并把配置归入该组。
        ISessionRepository? repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new ConnectionProfileViewModel(sessionRepository: repository)
        {
            Host = "h",
            Port = 22,
            Username = "root"
        };
        await vm.LoadGroupsAsync();
        vm.GroupText = "生产环境";
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        await repository.Received(1).SaveGroupAsync(Arg.Is<ServerGroup>(g => g.Name == "生产环境"));
        Assert.IsNotNull(profile.GroupId);
        Assert.Contains(option => option.Name == "生产环境" && option.Id == profile.GroupId, vm.Groups);
    }

    [TestMethod]
    public async Task SaveCommand_NewGroup_GoesAfterTheHighestSortOrder_NotTheGroupCount()
    {
        // 分组删过、拖动重排过(#571)之后序号不连续:按「当前个数」取号会和已有分组撞上。
        ISessionRepository? repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>
        {
            new() { Name = "A", SortOrder = 0 },
            new() { Name = "B", SortOrder = 7 }
        }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new ConnectionProfileViewModel(sessionRepository: repository)
        {
            Host = "h",
            Port = 22,
            Username = "root"
        };
        await vm.LoadGroupsAsync();
        vm.GroupText = "C";

        await vm.SaveCommand.Execute().FirstAsync();

        await repository.Received(1).SaveGroupAsync(Arg.Is<ServerGroup>(g => g.Name == "C" && g.SortOrder == 8));
    }

    [TestMethod]
    public async Task SaveCommand_ReusesExistingGroup_WhenGroupTextMatches()
    {
        var existing = new ServerGroup { Name = "生产环境" };
        ISessionRepository? repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { existing }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new ConnectionProfileViewModel(sessionRepository: repository)
        {
            Host = "h",
            Port = 22,
            Username = "root"
        };
        await vm.LoadGroupsAsync();
        vm.GroupText = "生产环境";
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.AreEqual(existing.Id, profile.GroupId);
        await repository.DidNotReceive().SaveGroupAsync(Arg.Any<ServerGroup>());
    }

    [TestMethod]
    public async Task SaveCommand_EmptyOrUngroupedText_SavesAsUngrouped()
    {
        ISessionRepository? repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new ConnectionProfileViewModel(sessionRepository: repository)
        {
            Host = "h",
            Port = 22,
            Username = "root"
        };
        await vm.LoadGroupsAsync();
        vm.GroupText = "  ";
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.IsNull(profile.GroupId);
        await repository.DidNotReceive().SaveGroupAsync(Arg.Any<ServerGroup>());
    }

    private static ConnectionProfileViewModel CreateValidViewModel(IConnectionWorkflowService workflow)
    {
        return new(connectionWorkflowService: workflow)
        {
            Name = "prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = SecureStringConvert.FromPlaintext("secret")
        };
    }
}
