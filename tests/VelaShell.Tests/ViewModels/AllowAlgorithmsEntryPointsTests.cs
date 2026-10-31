using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.Services;
using VelaShell.Ssh.Diagnostics;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 「放开这几个算法」推广到终端标签之外的三个失败入口:独立 SFTP 文档的失败卡片、连接诊断窗口、连接配置页的「测试」。
/// 与终端标签同一个确认框、同一套写法(只写诊断点名的那几个,跳板那一跳改跳板自己的配置);确认之后各按各的语义
/// 重连 / 重新诊断 / 重新测试。
/// </summary>
[TestClass]
[TestCategory("AllowAlgorithms")]
public sealed class AllowAlgorithmsEntryPointsTests
{
    private const string Group14Sha1 = "diffie-hellman-group14-sha1";

    // ---- 独立 SFTP 文档 ----

    [TestMethod]
    public async Task SftpDocument_NegotiationFailure_OffersTheRemedy_AndConfirmingSavesThenRetries()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        SessionProfile profile = SftpProfile();
        ISessionRepository repository = RepositoryWith(profile);
        workflow.SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<SessionProfile>());
        // 每次握手那一刻配置里的 KEX 清单(重试拿的是同一个对象,事后看参数分不出两次各是什么样)。
        var kexAtConnect = new List<string?>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    kexAtConnect.Add(call.Arg<SessionProfile>().Ssh?.KexAlgorithms);
                    return Task.FromException<SshSession>(kexAtConnect.Count == 1
                        ? Failure()
                        : new VelaSshConnectionException("连接被重置"));
                });
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), FakeTerminal.Emulator, sessionRepository: repository);

        await vm.OpenSftpDocumentForProfileAsync(profile);

        ConnectingDocument card = vm.Layout.AllDocuments().OfType<ConnectingDocument>().Single();
        Assert.IsTrue(card.HasError);
        Assert.IsTrue(card.HasAlgorithmRemedy, "SFTP 配置败在协商上:失败卡片也要给按钮。");
        Assert.AreSequenceEqual([Group14Sha1], [.. card.AlgorithmRemedy!.Algorithms]);
        Assert.Contains(Group14Sha1, card.AlgorithmRemedyTip);

        // 用户在确认框上取消:什么都不改、不重试。
        string? body = null;
        vm.AllowAlgorithmsConfirmer = (_, text) =>
        {
            body = text;
            return Task.FromResult(false);
        };
        card.AllowAlgorithms();
        await WaitUntilAsync(() => body is not null);
        Assert.Contains(Group14Sha1, body);
        await workflow.DidNotReceive().SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        Assert.HasCount(1, kexAtConnect, "取消了就不重试。");

        // 确认:库里那份写上 +算法(不开总开关),重试拿的是改过的那份。
        vm.AllowAlgorithmsConfirmer = (_, _) => Task.FromResult(true);
        card.AllowAlgorithms();
        await WaitUntilAsync(() => kexAtConnect.Count == 2);

        await workflow.Received(1).SaveProfileAsync(
            Arg.Is<SessionProfile>(p => p.Id == profile.Id && p.Password == null
                                        && p.Ssh!.KexAlgorithms == "+" + Group14Sha1 && !p.Ssh.LegacyAlgorithms),
            Arg.Any<CancellationToken>());
        Assert.AreSequenceEqual([null, "+" + Group14Sha1], kexAtConnect, "重试拿的是改过的那份。");
        await WaitUntilAsync(() => card.HasError);
        Assert.IsFalse(card.HasAlgorithmRemedy, "这一次败在别处:按钮跟着收掉。");
    }

    [TestMethod]
    public async Task SftpDocument_OtherFailures_OfferNothing()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SshSession>(new VelaSshConnectionException("连接被重置")));
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), FakeTerminal.Emulator);

        await vm.OpenSftpDocumentForProfileAsync(SftpProfile());

        ConnectingDocument card = vm.Layout.AllDocuments().OfType<ConnectingDocument>().Single();
        Assert.IsTrue(card.HasError);
        Assert.IsFalse(card.HasAlgorithmRemedy);
    }

    // ---- 连接诊断窗口 ----

    [TestMethod]
    public async Task Diagnostics_NegotiationFailure_ConfirmingSavesAndDiagnosesAgainWithTheChangedCopy()
    {
        SessionProfile profile = SshProfile();
        ISessionRepository repository = RepositoryWith(profile);
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<SessionProfile>());
        IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
        var diagnosed = new List<SessionProfile>();
        diagnostics.DiagnoseAsync(Arg.Do<SessionProfile>(diagnosed.Add), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                   .Returns(NegotiationReport(), PassedReport());
        var vm = new ConnectionDiagnosticsViewModel(profile, diagnostics, repository, workflow);

        await vm.RunCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.HasAlgorithmRemedy, "认证那一步败在协商上:问题卡片上要给按钮。");
        Assert.Contains(Group14Sha1, vm.AlgorithmRemedyTip);

        string? action = null;
        vm.AllowAlgorithmsConfirmer = (_, _, confirmText) =>
        {
            action = confirmText;
            return Task.FromResult(true);
        };
        await vm.AllowAlgorithmsCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get("SshAlgoFix_ConfirmActionRediagnose"), action, "确认键说的是「放开并重新诊断」。");
        await workflow.Received(1).SaveProfileAsync(
            Arg.Is<SessionProfile>(p => p.Id == profile.Id && p.Ssh!.KexAlgorithms == "+" + Group14Sha1), Arg.Any<CancellationToken>());
        Assert.HasCount(2, diagnosed);
        Assert.AreEqual("+" + Group14Sha1, diagnosed[1].Ssh!.KexAlgorithms, "重新诊断用的是改过的那份。");
        Assert.IsNull(profile.Ssh, "调用方那一份(可能是资源管理器缓存的对象)不改。");
        Assert.IsFalse(vm.HasAlgorithmRemedy, "这一轮通过了:按钮收掉。");
        Assert.IsTrue(vm.ChangedStoredProfiles, "库里的配置改了:窗口关掉之后宿主要刷新资源管理器。");
    }

    [TestMethod]
    public async Task Diagnostics_JumpHopRemedy_WithTheJumpProfileGone_SaysSoAndDoesNotRerun()
    {
        SessionProfile profile = SshProfile();
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
        diagnostics.DiagnoseAsync(Arg.Any<SessionProfile>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                   .Returns(NegotiationReport(hopDepth: 1));
        var vm = new ConnectionDiagnosticsViewModel(profile, diagnostics, RepositoryWith(profile), workflow)
        {
            AllowAlgorithmsConfirmer = (_, _, _) => throw new AssertFailedException("找不到要改的配置就不该问。"),
        };

        await vm.RunCommand.Execute().FirstAsync();
        Assert.AreEqual(1, vm.AlgorithmRemedy!.HopDepth);
        await vm.AllowAlgorithmsCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.HasAlgorithmRemedyError);
        Assert.Contains("jumper@bastion:22", vm.AlgorithmRemedyError);
        await workflow.DidNotReceive().SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        await diagnostics.Received(1).DiagnoseAsync(Arg.Any<SessionProfile>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
    }

    // ---- 连接配置页的「测试」 ----

    [TestMethod]
    public async Task ProfileEditor_NegotiationFailure_WritesTheFormOnlyThenTestsAgain()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        var tested = new List<SessionProfile>();
        workflow.TestConnectionAsync(Arg.Do<SessionProfile>(tested.Add), Arg.Any<CancellationToken>())
                .Returns(Failed(new VelaSshConnectionException("算法谈不成", Negotiation())), new ConnectionTestResult(true));
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        ConnectionProfileViewModel vm = Editor(workflow, repository);

        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.HasAlgorithmRemedy, "「测试」败在协商上:反馈条上要给按钮。");

        string? body = null;
        string? action = null;
        vm.AllowAlgorithmsConfirmer = (_, text, confirmText) =>
        {
            body = text;
            action = confirmText;
            return Task.FromResult(true);
        };
        await vm.AllowAlgorithmsCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get("SshAlgoFix_ConfirmActionRetest"), action);
        Assert.Contains(Strings.Get("SshAlgoFix_FormOnlyNote"), body, "改的是表单:确认框要说清按保存才存下。");
        Assert.IsTrue(vm.SshCustomAlgorithms, "自定义清单展开:用户看得见改了什么。");
        Assert.AreEqual("+" + Group14Sha1, vm.SshKexAlgorithms);
        Assert.IsFalse(vm.SshLegacyAlgorithms, "⚠️ 不能顺手把整组老算法都打开。");
        await workflow.DidNotReceive().SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        await repository.DidNotReceive().SaveSessionAsync(Arg.Any<SessionProfile>());
        Assert.HasCount(2, tested);
        Assert.AreEqual("+" + Group14Sha1, tested[1].Ssh!.KexAlgorithms, "重新测试用的是改过的表单。");
        Assert.IsTrue(vm.LastTestSucceeded);
        Assert.IsFalse(vm.HasAlgorithmRemedy);
    }

    [TestMethod]
    public async Task ProfileEditor_JumpHopRemedy_SavesTheJumpHostsProfile_AndLeavesTheFormAlone()
    {
        SessionProfile jump = new() { Id = Guid.NewGuid(), Name = "Bastion", Host = "bastion", Username = "jumper", AuthMethod = AuthMethod.Password };
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetSessionAsync(jump.Id).Returns(_ => jump.Clone());
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { jump }));
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<SessionProfile>());
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(Failed(Failure(hopDepth: 1)), new ConnectionTestResult(true));
        ConnectionProfileViewModel vm = Editor(workflow, repository);
        await vm.LoadGroupsAsync();
        vm.SelectedJumpHost = vm.JumpHostOptions.First(o => o.Id == jump.Id);

        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.AreEqual(1, vm.AlgorithmRemedy!.HopDepth);
        string? body = null;
        vm.AllowAlgorithmsConfirmer = (_, text, _) =>
        {
            body = text;
            return Task.FromResult(true);
        };
        await vm.AllowAlgorithmsCommand.Execute().FirstAsync();

        Assert.Contains("Bastion", body, "确认框说的是要改哪一条配置。");
        Assert.IsFalse(body!.Contains(Strings.Get("SshAlgoFix_FormOnlyNote"), StringComparison.Ordinal), "跳板那一跳当场存下,不是只改表单。");
        await workflow.Received(1).SaveProfileAsync(
            Arg.Is<SessionProfile>(p => p.Id == jump.Id && p.Ssh!.KexAlgorithms == "+" + Group14Sha1), Arg.Any<CancellationToken>());
        Assert.IsFalse(vm.SshCustomAlgorithms, "终点那一跳没谈不成,表单不动。");
        Assert.IsTrue(vm.LastTestSucceeded);
    }

    [TestMethod]
    public async Task ProfileEditor_ANewError_DropsTheRemedy()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(Failed(new VelaSshConnectionException("算法谈不成", Negotiation())), Failed(new VelaSshConnectionException("连接被重置")));
        ConnectionProfileViewModel vm = Editor(workflow, Substitute.For<ISessionRepository>());

        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.HasAlgorithmRemedy);
        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.HasAlgorithmRemedy, "按钮是对上一次失败说的:换了失败原因就收掉。");
        Assert.IsFalse(await vm.AllowAlgorithmsCommand.CanExecute.FirstAsync());
    }

    // ---- 各入口共用的那一段(SshAlgorithmRemedyFlow) ----

    /// <summary>
    /// 确认框开着时别处存了这条配置(云同步拉下来的、巡检窗口里「编辑配置」存的):确认之后在最新的那份上只补这几个算法,
    /// 别处的改动不被框前读的那份冲掉。
    /// </summary>
    [TestMethod]
    public async Task Flow_SavesTheRemedyOnTopOfTheProfileAsItIsAfterTheConfirm()
    {
        SessionProfile profile = SshProfile();
        SessionProfile stored = profile.Clone();
        stored.Password = null;
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetSessionAsync(profile.Id).Returns(_ => stored.Clone());
        var saved = new List<SessionProfile>();
        repository.SaveSessionAsync(Arg.Do<SessionProfile>(saved.Add)).Returns(Task.CompletedTask);
        var flow = new SshAlgorithmRemedyFlow(repository, workflow: null);

        SshAlgorithmRemedyApplied? applied = await flow.ConfirmAndSaveAsync(
            profile,
            new SshAlgorithmRemedy(SshAlgorithmKind.KeyExchange, [Group14Sha1]),
            SshAlgorithmRemedyFollowUp.Reconnect,
            confirm: (_, _, _) =>
            {
                stored.Name = "OldBox (同步过来的新名字)";
                stored.Ssh = new SshSessionOptions { Compression = true, Macs = "+hmac-sha1" };
                return Task.FromResult(true);
            },
            reportError: message => throw new AssertFailedException(message));

        SessionProfile written = saved.Single();
        Assert.AreEqual("OldBox (同步过来的新名字)", written.Name);
        Assert.IsTrue(written.Ssh!.Compression, "框开着时别处的改动留着");
        Assert.AreEqual("+hmac-sha1", written.Ssh.Macs);
        Assert.AreEqual("+" + Group14Sha1, written.Ssh.KexAlgorithms, "只补诊断点名的那几个");
        Assert.AreSame(written, applied!.Stored, "交回去的是写进库的那份");
    }

    /// <summary>确认框开着时这条配置被删了:什么都不写(不凭框前那份再建回来),说一句。</summary>
    [TestMethod]
    public async Task Flow_ProfileDeletedWhileConfirming_ChangesNothingAndSaysSo()
    {
        SessionProfile profile = SshProfile();
        SessionProfile? stored = profile.Clone();
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetSessionAsync(profile.Id).Returns(_ => stored?.Clone());
        var flow = new SshAlgorithmRemedyFlow(repository, workflow: null);
        string? error = null;

        SshAlgorithmRemedyApplied? applied = await flow.ConfirmAndSaveAsync(
            profile,
            new SshAlgorithmRemedy(SshAlgorithmKind.KeyExchange, [Group14Sha1]),
            SshAlgorithmRemedyFollowUp.Reconnect,
            confirm: (_, _, _) =>
            {
                stored = null;
                return Task.FromResult(true);
            },
            reportError: message => error = message);

        Assert.IsNull(applied);
        Assert.AreEqual(Strings.Format("SshAlgoFix_ProfileMissing", "OldBox"), error);
        await repository.DidNotReceive().SaveSessionAsync(Arg.Any<SessionProfile>());
    }

    // ---- 辅助 ----

    private static SshNegotiationException Negotiation() => new(
        SshNegotiationCategory.KeyExchange, [Group14Sha1], ["curve25519-sha256"], "SSH-2.0-OldBox");

    private static ConnectionTestResult Failed(Exception error) => new(false, error.Message, error);

    /// <summary>一次协商失败;<paramref name="hopDepth" /> 大于 0 时是跳板那一跳谈不成的。</summary>
    private static VelaSshConnectionException Failure(int hopDepth = 0)
    {
        SshNegotiationException negotiation = Negotiation();
        if (hopDepth > 0)
        {
            // 装配器在跳板那一跳的建连上打的标记(见 SshAlgorithmRemedy.TagHop;internal,测试工程够不着),这里照样打上。
            typeof(SshAlgorithmRemedy).GetMethod("TagHop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                                      .Invoke(null, [negotiation, hopDepth, "jumper@bastion:22"]);
        }
        return new("算法谈不成", negotiation);
    }

    private static DiagnosticReport NegotiationReport(int hopDepth = 0)
    {
        Exception failure = Failure(hopDepth);
        return new()
        {
            Steps = Steps(DiagnosticStepStatus.Failed),
            IssueTitle = "连接失败",
            IssueDescription = failure.Message,
            Failure = failure,
        };
    }

    private static DiagnosticReport PassedReport() => new() { Steps = Steps(DiagnosticStepStatus.Success) };

    private static DiagnosticStepUpdate[] Steps(DiagnosticStepStatus last) =>
    [
        new(0, "DNS", DiagnosticStepStatus.Success),
        new(1, "TCP", DiagnosticStepStatus.Success),
        new(2, "SSH", DiagnosticStepStatus.Success),
        new(3, "Auth", last),
    ];

    private static SessionProfile SshProfile() => new()
    {
        Id = Guid.NewGuid(),
        Name = "OldBox",
        Host = "oldbox.example.com",
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };

    private static SessionProfile SftpProfile()
    {
        SessionProfile profile = SshProfile();
        profile.ConnectionType = ConnectionType.SFTP;
        return profile;
    }

    /// <summary>库里存着这条配置(没记口令的那一份)。</summary>
    private static ISessionRepository RepositoryWith(SessionProfile profile)
    {
        SessionProfile stored = profile.Clone();
        stored.Password = null;
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetSessionAsync(profile.Id).Returns(_ => stored.Clone());
        return repository;
    }

    private static ConnectionProfileViewModel Editor(IConnectionWorkflowService workflow, ISessionRepository repository) =>
        new(connectionWorkflowService: workflow, sessionRepository: repository)
        {
            Name = "OldBox",
            Host = "oldbox.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = SecureStringConvert.FromPlaintext("secret"),
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(condition(), "等了 5 秒条件仍不成立。");
    }
}
