using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 失败覆盖层上的「放开这几个算法并重连」:协商失败且对端有本版实现了只是没放开的算法时出现;
/// 确认之后只把诊断点名的那几个写进那一跳配置的自定义清单(不开「允许老算法」总开关),落盘、就地重连。
/// </summary>
[TestClass]
[TestCategory("AllowAlgorithms")]
public sealed class AllowAlgorithmsFlowTests
{
    private const string Group14Sha1 = "diffie-hellman-group14-sha1";

    [TestMethod]
    public async Task NegotiationFailure_OffersTheRemedy_AndConfirmingWritesOnlyTheNamedAlgorithmsThenReconnects()
    {
        Harness h = await Harness.ConnectedAsync();
        h.FailNextConnectWith(new VelaSshConnectionException("算法谈不成", Negotiation()));
        await h.Vm.ReconnectTabAsync(h.Tab);

        Assert.IsTrue(h.Tab.HasAlgorithmRemedy, "协商失败、对端有可放开的算法:覆盖层要给按钮。");
        Assert.AreSequenceEqual([Group14Sha1], [.. h.Tab.AlgorithmRemedy!.Algorithms]);
        Assert.IsTrue(await h.Tab.AllowAlgorithmsCommand.CanExecute.FirstAsync());

        // 用户在确认框上取消:配置一个字都不改,也不重连。
        string? body = null;
        h.Vm.AllowAlgorithmsConfirmer = (_, text) =>
        {
            body = text;
            return Task.FromResult(false);
        };
        await h.Vm.AllowAlgorithmsAndReconnectAsync(h.Tab);
        Assert.Contains(Group14Sha1, body, "确认框要点名写进去的是哪几个。");
        Assert.Contains("Prod", body);
        Assert.IsNull(h.Tab.Profile!.Ssh);
        await h.Workflow.DidNotReceive().SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(SessionStatus.Disconnected, h.Tab.ConnectionStatus);

        // 确认:写进库里那份配置(不是标签手里带着手输口令的副本)与标签手里那份,然后重连。
        h.Vm.AllowAlgorithmsConfirmer = (_, _) => Task.FromResult(true);
        h.SucceedNextConnect();
        await h.Vm.AllowAlgorithmsAndReconnectAsync(h.Tab);

        Assert.AreEqual(SessionStatus.Connected, h.Tab.ConnectionStatus);
        Assert.IsFalse(h.Tab.HasAlgorithmRemedy, "连上之后按钮收掉。");
        Assert.AreEqual("+" + Group14Sha1, h.Tab.Profile.Ssh!.KexAlgorithms);
        Assert.IsFalse(h.Tab.Profile.Ssh.LegacyAlgorithms, "⚠️ 不能顺手把整组老算法都打开。");
        await h.Workflow.Received(1).SaveProfileAsync(
            Arg.Is<SessionProfile>(p => p.Id == h.Profile.Id && p.Password == null
                                        && p.Ssh!.KexAlgorithms == "+" + Group14Sha1 && !p.Ssh.LegacyAlgorithms
                                        && p.Ssh.HostKeyAlgorithms == null && p.Ssh.Macs == null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>按钮经标签的命令触发:与覆盖层上点的是同一条路。</summary>
    [TestMethod]
    public async Task TheOverlayCommand_RunsTheSameFlow()
    {
        Harness h = await Harness.ConnectedAsync();
        h.FailNextConnectWith(new VelaSshConnectionException("算法谈不成", Negotiation()));
        await h.Vm.ReconnectTabAsync(h.Tab);
        h.Vm.AllowAlgorithmsConfirmer = (_, _) => Task.FromResult(true);
        h.SucceedNextConnect();

        h.Tab.AllowAlgorithmsCommand.Execute().Subscribe();
        for (int i = 0; i < 200 && h.Tab.ConnectionStatus != SessionStatus.Connected; i++)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual(SessionStatus.Connected, h.Tab.ConnectionStatus);
        Assert.AreEqual("+" + Group14Sha1, h.Tab.Profile!.Ssh!.KexAlgorithms);
    }

    /// <summary>不是协商失败(或对端一个能放开的都没有):不给按钮,点了也什么都不做。</summary>
    [TestMethod]
    public async Task OtherFailures_OfferNothing()
    {
        Harness h = await Harness.ConnectedAsync();
        h.FailNextConnectWith(new VelaSshConnectionException("连接被重置"));
        await h.Vm.ReconnectTabAsync(h.Tab);

        Assert.IsFalse(h.Tab.HasAlgorithmRemedy);
        Assert.IsFalse(await h.Tab.AllowAlgorithmsCommand.CanExecute.FirstAsync());
        h.Vm.AllowAlgorithmsConfirmer = (_, _) => throw new AssertFailedException("没有补救就不该问。");
        await h.Vm.AllowAlgorithmsAndReconnectAsync(h.Tab);
    }

    /// <summary>谈不成的是跳板:改的是跳板自己的配置(顺着「跳板主机」数到那一跳),终点的配置与标签手里那份都不动。</summary>
    [TestMethod]
    public async Task JumpHopRemedy_ChangesTheJumpHostsProfileOnly()
    {
        SessionProfile jump = new() { Id = Guid.NewGuid(), Name = "Bastion", Host = "bastion", Username = "jumper", AuthMethod = AuthMethod.Password };
        Harness h = await Harness.ConnectedAsync(jumpHost: jump);
        h.Tab.MarkConnectionFailed("跳板上谈不成",
            new SshAlgorithmRemedy(SshAlgorithmKind.KeyExchange, [Group14Sha1], HopDepth: 1, HopTarget: "jumper@bastion:22"));
        string? body = null;
        h.Vm.AllowAlgorithmsConfirmer = (_, text) =>
        {
            body = text;
            return Task.FromResult(true);
        };
        h.SucceedNextConnect();

        await h.Vm.AllowAlgorithmsAndReconnectAsync(h.Tab);

        Assert.Contains("Bastion", body, "确认框说的是要改哪一条配置。");
        await h.Workflow.Received(1).SaveProfileAsync(
            Arg.Is<SessionProfile>(p => p.Id == jump.Id && p.Ssh!.KexAlgorithms == "+" + Group14Sha1), Arg.Any<CancellationToken>());
        await h.Workflow.DidNotReceive().SaveProfileAsync(Arg.Is<SessionProfile>(p => p.Id == h.Profile.Id), Arg.Any<CancellationToken>());
        Assert.IsNull(h.Tab.Profile!.Ssh, "终点那一跳没谈不成,它的配置不动。");
        Assert.AreEqual(SessionStatus.Connected, h.Tab.ConnectionStatus);
    }

    /// <summary>跳板的配置已经找不到了:不猜着去改别的,不重连。</summary>
    [TestMethod]
    public async Task JumpHopRemedy_WithTheJumpProfileGone_ChangesNothing()
    {
        Harness h = await Harness.ConnectedAsync();
        h.Tab.MarkConnectionFailed("跳板上谈不成",
            new SshAlgorithmRemedy(SshAlgorithmKind.KeyExchange, [Group14Sha1], HopDepth: 1, HopTarget: "jumper@bastion:22"));
        h.Vm.AllowAlgorithmsConfirmer = (_, _) => throw new AssertFailedException("找不到要改的配置就不该问。");

        await h.Vm.AllowAlgorithmsAndReconnectAsync(h.Tab);

        await h.Workflow.DidNotReceive().SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(SessionStatus.Disconnected, h.Tab.ConnectionStatus);
    }

    private static SshNegotiationException Negotiation() => new(
        SshNegotiationCategory.KeyExchange, [Group14Sha1], ["curve25519-sha256"], "SSH-2.0-OldBox");

    private sealed class Harness
    {
        private readonly SshSession _session;

        private Harness(MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow, SessionProfile profile, SshSession session)
        {
            Vm = vm;
            Tab = tab;
            Workflow = workflow;
            Profile = profile;
            _session = session;
        }

        public MainWindowViewModel Vm { get; }

        public TerminalTabViewModel Tab { get; }

        public IConnectionWorkflowService Workflow { get; }

        public SessionProfile Profile { get; }

        public static async Task<Harness> ConnectedAsync(SessionProfile? jumpHost = null)
        {
            IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
            ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
            ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
            IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
            shell.CanRead.Returns(true);
            shell.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new TaskCompletionSource<int>().Task);
            ITerminalEmulator terminal = FakeTerminal.Emulator();
            SessionProfile profile = new()
            {
                Id = Guid.NewGuid(),
                Name = "Prod",
                Host = "prod.example.com",
                Username = "root",
                AuthMethod = AuthMethod.Password,
                // 标签手里这份带着这次手输的口令;库里那份没记口令(见下面的 stored)。
                Password = "typed-now",
                JumpHostProfileId = jumpHost?.Id,
            };
            SessionProfile stored = profile.Clone();
            stored.Password = null;
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetSessionAsync(profile.Id).Returns(_ => stored.Clone());
            if (jumpHost is not null)
            {
                repository.GetSessionAsync(jumpHost.Id).Returns(_ => jumpHost.Clone());
            }
            workflow.SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        // 替身的保存也要真的「落库」:重连先从库里重读这条配置(§298 的欠账),读回来的得是改过的那份。
                        SessionProfile saved = call.Arg<SessionProfile>();
                        if (saved.Id == profile.Id)
                        {
                            stored = saved.Clone();
                        }
                        return saved;
                    });
            SshSession session = new()
            {
                SessionId = Guid.NewGuid(),
                ConnectionInfo = new() { Host = profile.Host, Username = profile.Username, AuthMethod = profile.AuthMethod },
                Status = SessionStatus.Connected,
            };
            workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(session);
            ssh.GetClient(session.SessionId).Returns(client);
            client.CreateShellStreamAsync(Arg.Any<string>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shell);
            var vm = new MainWindowViewModel(workflow, ssh, () => terminal, sessionRepository: repository);
            TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
            Assert.IsNotNull(tab);
            Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
            tab.MarkDisconnected();
            return new(vm, tab, workflow, profile, session);
        }

        public void FailNextConnectWith(Exception error) =>
            Workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<SshSession>(error));

        public void SucceedNextConnect() =>
            Workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(_session);
    }
}
