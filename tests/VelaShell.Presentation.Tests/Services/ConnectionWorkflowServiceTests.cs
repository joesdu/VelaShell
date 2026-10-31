using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

[TestClass]
public sealed class ConnectionWorkflowServiceTests
{
    private readonly ISessionRepository _sessionRepository = Substitute.For<ISessionRepository>();
    private readonly ISshConnectionService _sshConnectionService = Substitute.For<ISshConnectionService>();

    [TestMethod]
    public async Task SaveProfileAsync_PersistsProfile()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        SessionProfile result = await service.SaveProfileAsync(profile);
        Assert.AreSame(profile, result);
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    [TestMethod]
    public async Task ConnectProfileAsync_SavesLastConnectedAt()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        _sessionRepository.GetSessionAsync(profile.Id).Returns(profile);
        var session = new SshSession
        {
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(session);
        SshSession result = await service.ConnectProfileAsync(profile);
        Assert.AreSame(session, result);
        Assert.IsNotNull(profile.LastConnectedAt);
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    /// <summary>
    /// 不在库里的是现搭的临时配置(外部拉起、「最近连接」重建的):连上之后不回写 —— 不进会话树,也就不随云同步上传;
    /// 连接历史(最近连接,不含密码)与审计照记。
    /// </summary>
    [TestMethod]
    public async Task ConnectProfileAsync_TemporaryProfileNotInRepository_IsNotSaved_ButStillRecorded()
    {
        IRecentConnectionService recent = Substitute.For<IRecentConnectionService>();
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        var service = new ConnectionWorkflowService(_sessionRepository, _sshConnectionService, recent, audit);
        SessionProfile temporary = CreateProfile("tester@localhost:22");
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(new SshSession
                             {
                                 ConnectionInfo = new() { Host = temporary.Host, Port = 22, Username = temporary.Username, AuthMethod = AuthMethod.Password },
                                 Status = SessionStatus.Connected
                             });

        await service.ConnectProfileAsync(temporary);
        await service.ReconnectProfileAutomaticallyAsync(temporary);

        await _sessionRepository.DidNotReceiveWithAnyArgs().SaveSessionAsync(default!);
        await recent.Received(2).RecordAsync(Arg.Is<RecentConnectionEntry>(e =>
            e.ProfileId == temporary.Id && e.Host == "localhost" && e.Username == "tester" && e.Success));
        await audit.Received(2).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == "connect" && e.ProfileId == temporary.Id));
    }

    /// <summary>
    /// 连上之后只改库里那份的最近连接时间,不把调用方手里的副本整份写回(§298 的欠账):终端标签的副本开着的这段时间里,
    /// 别处(云同步、编辑框、巡检窗口的「放开这几个算法」)刚改的名字、算法、认证后命令不会被它盖掉,现敲却没让记住的密码也落不了库。
    /// </summary>
    [TestMethod]
    public async Task ConnectProfileAsync_WritesOnlyTheConnectionTime_OntoTheStoredCopy()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile stored = CreateProfile("renamed-elsewhere");
        stored.Password = null;
        stored.RememberPassword = false;
        stored.Ssh = new SshSessionOptions { KexAlgorithms = "+diffie-hellman-group14-sha1" };
        stored.PostAuthCommand = "tmux attach";
        SessionProfile copy = stored.Clone();
        copy.Name = "old-name";
        copy.Ssh = null;
        copy.PostAuthCommand = "echo old";
        copy.Password = "typed-now";
        _sessionRepository.GetSessionAsync(stored.Id).Returns(_ => stored.Clone());
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(call => new SshSession { ConnectionInfo = (ConnectionInfo)call[0], Status = SessionStatus.Connected });

        await service.ConnectProfileAsync(copy);

        Assert.IsNotNull(copy.LastConnectedAt, "调用方那份照样记上这次的时间");
        await _sessionRepository.Received(1).SaveSessionAsync(Arg.Is<SessionProfile>(p =>
            p.Id == stored.Id && p.Name == "renamed-elsewhere" && p.Ssh!.KexAlgorithms == "+diffie-hellman-group14-sha1"
            && p.PostAuthCommand == "tmux attach" && p.Password == null && p.LastConnectedAt == copy.LastConnectedAt));
    }

    /// <summary>登录框交回的那份:只把登录那几项写进库里那份,别的字段以库里为准;勾了记住密码才落密码。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SaveLoginChangesAsync_WritesTheLoginFieldsOntoTheStoredCopy(bool remember)
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile stored = CreateProfile("renamed-elsewhere");
        stored.Username = string.Empty;
        stored.Password = null;
        stored.PostAuthCommand = "tmux attach";
        SessionProfile prompted = stored.Clone();
        prompted.Name = "old-name";
        prompted.PostAuthCommand = null;
        prompted.Username = "deploy";
        prompted.Password = "typed-now";
        prompted.RememberPassword = remember;
        _sessionRepository.GetSessionAsync(stored.Id).Returns(_ => stored.Clone());

        await service.SaveLoginChangesAsync(prompted);

        await _sessionRepository.Received(1).SaveSessionAsync(Arg.Is<SessionProfile>(p =>
            p.Id == stored.Id && p.Name == "renamed-elsewhere" && p.PostAuthCommand == "tmux attach"
            && p.Username == "deploy" && p.RememberPassword == remember && p.Password == (remember ? "typed-now" : null)));
        Assert.AreEqual("typed-now", prompted.Password, "交进来的那份不动:标签还要用它");
    }

    /// <summary>不在库里的临时配置(§252):登录框交回的那份同样不落库。</summary>
    [TestMethod]
    public async Task SaveLoginChangesAsync_TemporaryProfile_IsNotSaved()
    {
        ConnectionWorkflowService service = CreateService();

        await service.SaveLoginChangesAsync(CreateProfile("tester@localhost:22"));

        await _sessionRepository.DidNotReceiveWithAnyArgs().SaveSessionAsync(default!);
    }

    [TestMethod]
    public async Task TestConnectionAsync_WhenConnectFails_ReturnsFailureResult()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns<Task<SshSession>>(_ => throw new InvalidOperationException("boom"));
        ConnectionTestResult result = await service.TestConnectionAsync(profile);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("boom", result.ErrorMessage);
        // 原始异常一并交出:宿主要从异常链里认出「算法谈不成」才给得出一键放开。
        Assert.IsInstanceOfType<InvalidOperationException>(result.Error);
    }

    /// <summary>
    /// 自动重连按「没人在场」连:跳板链上每一跳都标上(主机指纹要人裁决时直接拒绝、不弹窗);
    /// 普通连接一跳也不标 —— 标错了的话,首次连接的指纹确认框就再也弹不出来。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConnectionInfo_IsMarkedAsAutomaticReconnect_OnlyForAutomaticReconnect(bool automatic)
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile jump = CreateProfile("bastion");
        SessionProfile target = CreateProfile("web-01");
        target.JumpHostProfileId = jump.Id;
        _sessionRepository.GetSessionAsync(jump.Id).Returns(jump);
        ConnectionInfo? connected = null;
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(call =>
                             {
                                 connected = (ConnectionInfo)call[0];
                                 return new SshSession { ConnectionInfo = connected, Status = SessionStatus.Connected };
                             });

        _ = automatic
            ? await service.ReconnectProfileAutomaticallyAsync(target)
            : await service.ConnectProfileAsync(target);

        Assert.IsNotNull(connected);
        Assert.AreEqual(automatic, connected.IsAutomaticReconnect);
        Assert.AreEqual(automatic, connected.JumpHost?.IsAutomaticReconnect);
    }

    /// <summary>
    /// 巡检的连接测试按「没人看着」连:跳板链上每一跳都标上(要人回答的一律不问),连完随即断开;
    /// 普通的「测试」一跳也不标。
    /// </summary>
    [TestMethod]
    public async Task UnattendedTest_MarksEveryHop_AndDisconnects_ButTheOrdinaryTestDoesNot()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile jump = CreateProfile("bastion");
        SessionProfile target = CreateProfile("web-01");
        target.JumpHostProfileId = jump.Id;
        _sessionRepository.GetSessionAsync(jump.Id).Returns(jump);
        var connected = new List<ConnectionInfo>();
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(call =>
                             {
                                 connected.Add((ConnectionInfo)call[0]);
                                 return new SshSession { ConnectionInfo = connected[^1], Status = SessionStatus.Connected };
                             });

        ConnectionTestResult unattended = await service.TestConnectionUnattendedAsync(target);
        ConnectionTestResult ordinary = await service.TestConnectionAsync(target);

        Assert.IsTrue(unattended.Success);
        Assert.IsTrue(ordinary.Success);
        Assert.IsTrue(connected[0].IsUnattended);
        Assert.IsTrue(connected[0].JumpHost!.IsUnattended);
        Assert.IsFalse(connected[0].IsAutomaticReconnect, "巡检不是自动重连:那条会停掉自动重连、说法也不同。");
        Assert.IsFalse(connected[1].IsUnattended);
        Assert.IsFalse(connected[1].JumpHost!.IsUnattended);
        await _sshConnectionService.Received(2).DisconnectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ———— 已存的密码在这台设备上解不开(第二轮第 20 条) ————

    /// <summary>载入时没解开、仍是密文原样的那一份(见 <see cref="StoredSecret" />)。</summary>
    private const string Undecryptable = "enc1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>
    /// 存着的密码解不开:不拿密文去登录(白白一次失败认证,还可能撞上 fail2ban),报一个说得清的凭据错误 ——
    /// 界面上的连接见到它退回登录框,隧道、测试连接这些不弹框的路给出这句原因。
    /// </summary>
    [TestMethod]
    public async Task UnreadableStoredPassword_IsNeverSentToTheServer()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        profile.Password = Undecryptable;

        CredentialProviderException error = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => service.ConnectProfileAsync(profile));
        ConnectionTestResult test = await service.TestConnectionUnattendedAsync(profile);

        Assert.AreEqual(CredentialFailure.UnreadableSecret, error.Kind);
        Assert.AreEqual(Strings.Get("Cred_Failure_StoredSecretUnreadable"), error.Message);
        Assert.IsFalse(test.Success);
        Assert.AreEqual(Strings.Get("Cred_Failure_StoredSecretUnreadable"), test.ErrorMessage);
        await _sshConnectionService.DidNotReceiveWithAnyArgs().ConnectAsync(default!, default);
    }

    /// <summary>私钥口令同理;跳板那一跳解不开时点名是哪一跳(跳板向来不弹框,问目标机的凭据也顶不了跳板)。</summary>
    [TestMethod]
    public async Task UnreadablePassphrase_OrJumpHostPassword_FailsBeforeConnecting()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile keyed = CreateProfile("keyed");
        keyed.AuthMethod = AuthMethod.PrivateKey;
        keyed.PrivateKeyPath = "/keys/id_ed25519";
        keyed.PrivateKeyPassphrase = Undecryptable;
        SessionProfile jump = CreateProfile("bastion");
        jump.Password = Undecryptable;
        SessionProfile target = CreateProfile("web-01");
        target.JumpHostProfileId = jump.Id;
        _sessionRepository.GetSessionAsync(jump.Id).Returns(jump);

        await Assert.ThrowsExactlyAsync<CredentialProviderException>(() => service.ConnectProfileAsync(keyed));
        InvalidOperationException viaJump = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ConnectProfileAsync(target));

        Assert.AreEqual(Strings.Format("Svc_JumpCredentialFailed", "bastion", Strings.Get("Cred_Failure_StoredSecretUnreadable")),
            viaJump.Message);
        await _sshConnectionService.DidNotReceiveWithAnyArgs().ConnectAsync(default!, default);
    }

    /// <summary>巡检被停下 / 单台到点:取消照原样抛出,不当成「这台连不上」。</summary>
    [TestMethod]
    public async Task UnattendedTest_CallerCancellation_Propagates()
    {
        ConnectionWorkflowService service = CreateService();
        using var cts = new CancellationTokenSource();
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns<Task<SshSession>>(_ =>
                             {
                                 cts.Cancel();
                                 throw new OperationCanceledException(cts.Token);
                             });

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.TestConnectionUnattendedAsync(CreateProfile(), cts.Token));
    }

    /// <summary>插件配置不测:插件的探针会开真会话、可能弹插件自己的框。</summary>
    [TestMethod]
    public async Task UnattendedTest_PluginProfile_IsNotProbed()
    {
        ConnectionWorkflowService service = CreateService();
        int probes = 0;
        service.PluginProbe = (_, _) =>
        {
            probes++;
            return Task.CompletedTask;
        };

        ConnectionTestResult result = await service.TestConnectionUnattendedAsync(CreatePluginProfile());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, probes);
        await _sshConnectionService.DidNotReceive().ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 会话的字符集(会话覆盖优先于全局)带进连接信息,SFTP 按它解读文件名;跳板那一跳不带(它不开 SFTP)。
    /// </summary>
    [TestMethod]
    [DataRow("GBK", "UTF-8", "GBK")]
    [DataRow(null, "Big5", "Big5")]
    [DataRow(null, "UTF-8", "UTF-8")]
    public async Task ConnectionInfo_CarriesTheSessionEncoding_OnTheTargetHopOnly(
        string? profileEncoding, string globalEncoding, string expected)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { TerminalEncoding = globalEncoding });
        var service = new ConnectionWorkflowService(_sessionRepository, _sshConnectionService, settingsService: settings);
        SessionProfile jump = CreateProfile("bastion");
        jump.Terminal = new TerminalOverrides { Encoding = "Shift_JIS" };
        SessionProfile target = CreateProfile("legacy-nas");
        target.Terminal = new TerminalOverrides { Encoding = profileEncoding };
        target.JumpHostProfileId = jump.Id;
        _sessionRepository.GetSessionAsync(jump.Id).Returns(jump);
        ConnectionInfo? connected = null;
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(call =>
                             {
                                 connected = (ConnectionInfo)call[0];
                                 return new SshSession { ConnectionInfo = connected, Status = SessionStatus.Connected };
                             });

        await service.ConnectProfileAsync(target);

        Assert.IsNotNull(connected);
        Assert.AreEqual(expected, connected.FileNameEncoding);
        Assert.IsNull(connected.JumpHost?.FileNameEncoding, "跳板只开隧道,不该带文件名编码");
    }

    /// <summary>
    /// 诊断给的协议轨迹挂在最外层的连接信息上(装配那一层拿它记链上每一跳);跳板那一份不重复挂。
    /// 平常的「测试连接」与连接都不挂 —— 报文旁路只在诊断时开。
    /// </summary>
    [TestMethod]
    public async Task PacketTrace_IsCarriedOnlyByTheDiagnosticTest()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile jump = CreateProfile("bastion");
        SessionProfile target = CreateProfile("web-01");
        target.JumpHostProfileId = jump.Id;
        _sessionRepository.GetSessionAsync(jump.Id).Returns(jump);
        List<ConnectionInfo> connected = [];
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(call =>
                             {
                                 connected.Add((ConnectionInfo)call[0]);
                                 return new SshSession { ConnectionInfo = connected[^1], Status = SessionStatus.Connected };
                             });
        SshPacketTrace trace = new();

        Assert.IsTrue((await service.TestConnectionAsync(target, trace)).Success);
        Assert.IsTrue((await service.TestConnectionAsync(target)).Success);
        await service.ConnectProfileAsync(target);

        Assert.AreSame(trace, connected[0].PacketTrace);
        Assert.IsNull(connected[0].JumpHost?.PacketTrace, "跳板那一跳由装配那一层用同一份记,不必再挂");
        Assert.IsNull(connected[1].PacketTrace, "平常的测试连接不挂旁路");
        Assert.IsNull(connected[2].PacketTrace, "平常的连接不挂旁路");
    }

    [TestMethod]
    public async Task GetSavedProfilesAsync_ReturnsSortedProfiles()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile first = CreateProfile("b", DateTime.UtcNow.AddMinutes(-10));
        SessionProfile second = CreateProfile("a", DateTime.UtcNow);
        _sessionRepository.GetAllSessionsAsync().Returns([first, second]);
        IReadOnlyList<SessionProfile> result = await service.GetSavedProfilesAsync();
        Assert.AreSame(second, result[0]);
        Assert.AreSame(first, result[1]);
    }

    /// <summary>
    /// 插件连接类型的「测试」必须走探针,**绝不能**落到 SSH 那条路上。
    /// <para>
    /// 曾经就是落下去的:一条 Redis 配置按「测试」,宿主拿 SSH 去连 6379,
    /// TCP 连上、版本交换卡死,最后报"连接超时" —— 用户按这个提示去查防火墙,
    /// 而真实原因(口令/库号/TLS)一个都没被测到。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TestConnectionAsync_PluginProfile_UsesProbeInsteadOfSsh()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreatePluginProfile();
        SessionProfile? probed = null;
        service.PluginProbe = (candidate, _) =>
        {
            probed = candidate;
            return Task.CompletedTask;
        };

        ConnectionTestResult result = await service.TestConnectionAsync(profile);

        Assert.IsTrue(result.Success);
        Assert.AreSame(profile, probed);
        await _sshConnectionService.DidNotReceive()
                                  .ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task TestConnectionAsync_PluginProbeThrows_ReportsProbeMessage()
    {
        ConnectionWorkflowService service = CreateService();
        service.PluginProbe = (_, _) => throw new InvalidOperationException("wrong password");

        ConnectionTestResult result = await service.TestConnectionAsync(CreatePluginProfile());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("wrong password", result.ErrorMessage);
    }

    /// <summary>没接探针时要明确说"测不了",而不是拿 SSH 去撞插件端口撞出一个假原因。</summary>
    [TestMethod]
    public async Task TestConnectionAsync_PluginProfileWithoutProbe_DoesNotFallBackToSsh()
    {
        ConnectionWorkflowService service = CreateService();

        ConnectionTestResult result = await service.TestConnectionAsync(CreatePluginProfile());

        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
        await _sshConnectionService.DidNotReceive()
                                  .ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>());
    }

    private ConnectionWorkflowService CreateService() => new(_sessionRepository, _sshConnectionService);

    private static SessionProfile CreatePluginProfile() => new()
    {
        Name = "local-redis",
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = "redis",
        Host = "127.0.0.1",
        Port = 6379,
        AuthMethod = AuthMethod.Password
    };

    private static SessionProfile CreateProfile(string name = "server", DateTime? lastConnectedAt = null)
    {
        return new()
        {
            Name = name,
            Host = "localhost",
            Port = 22,
            Username = "tester",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
            LastConnectedAt = lastConnectedAt
        };
    }
}
