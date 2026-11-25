using System.Net;
using System.Net.Sockets;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 没人看着的探测(会话健康巡检,<see cref="ConnectionInfo.IsUnattended" />)走宿主真正的装配路径:
/// 要人回答的一律不问 —— 主机指纹待确认直接拒、动态码一问就谢绝(口令那一轮照样代答,密码不会填进验证码那一轮)。
/// </summary>
[TestClass]
[TestCategory("SshConnection")]
public sealed class UnattendedProbeTests
{
    private const string Password = "hunter2";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>第一次见到的主机、开着「首次连接确认指纹」:巡检不弹确认框,直接拒;也不记安全告警(只是还没亲自连过)。</summary>
    [TestMethod]
    public async Task UnknownHostKey_IsRejectedWithoutAsking_AndRaisesNoAlert()
    {
        await using ProbeServer server = new(new TestAuthPolicy { AcceptPassword = Password });
        (IHostKeyService store, ISettingsService settings) = TrustStore(HostKeyVerification.Unknown);
        IHostKeyPrompt prompt = Substitute.For<IHostKeyPrompt>();
        ISecurityAlertService alerts = Substitute.For<ISecurityAlertService>();

        Exception failure = await Assert.ThrowsAsync<Exception>(
            () => ConnectAsync(Info(server.Port, unattended: true), store, settings, prompt, alerts));

        Assert.Contains(e => e is VelaSshHostKeyRejectedException, Chain(failure), failure.ToString());
        // 说的是巡检没替你接受,不是「停止自动重连」(取两句文案在第一个占位符之前的那一段比)。
        Assert.Contains(Strings.Get("Msg_HostKeyUnattendedBlocked").Split('{')[0], failure.ToString());
        Assert.IsFalse(failure.ToString().Contains(Strings.Get("Msg_HostKeyAutoReconnectBlocked").Split('{')[0], StringComparison.Ordinal));
        await prompt.DidNotReceiveWithAnyArgs().DecideAsync(default!, default, default!, default!, default, default, default, default);
        await alerts.DidNotReceiveWithAnyArgs().RaiseAsync(default!, default!, default);
    }

    /// <summary>指纹<b>变了</b>的照样拒、照样记安全告警 —— 那是要人看到的事。</summary>
    [TestMethod]
    public async Task ChangedHostKey_IsRejectedWithoutAsking_AndStillRaisesTheAlert()
    {
        await using ProbeServer server = new(new TestAuthPolicy { AcceptPassword = Password });
        (IHostKeyService store, ISettingsService settings) = TrustStore(HostKeyVerification.Changed);
        IHostKeyPrompt prompt = Substitute.For<IHostKeyPrompt>();
        ISecurityAlertService alerts = Substitute.For<ISecurityAlertService>();

        await Assert.ThrowsAsync<Exception>(() => ConnectAsync(Info(server.Port, unattended: true), store, settings, prompt, alerts));

        await prompt.DidNotReceiveWithAnyArgs().DecideAsync(default!, default, default!, default!, default, default, default, default);
        await alerts.Received(1).RaiseAsync("hostkey-rejected", Arg.Any<string>(), Arg.Any<object?>());
    }

    /// <summary>对照:手动连接时同一台主机照常弹确认框(巡检的「不问」没漏到别的连接上)。</summary>
    [TestMethod]
    public async Task UnknownHostKey_StillAsks_WhenSomeoneIsThere()
    {
        await using ProbeServer server = new(new TestAuthPolicy { AcceptPassword = Password });
        (IHostKeyService store, ISettingsService settings) = TrustStore(HostKeyVerification.Unknown);
        IHostKeyPrompt prompt = Substitute.For<IHostKeyPrompt>();
        prompt.DecideAsync(default!, default, default!, default!, default, default, default, default)
              .ReturnsForAnyArgs(HostKeyDecision.TrustOnce);

        await using VelaSshClientWrapper connected =
            await ConnectAsync(Info(server.Port, unattended: false), store, settings, prompt, alerts: null);

        await prompt.ReceivedWithAnyArgs(1).DecideAsync(default!, default, default!, default!, default, default, default, default);
    }

    /// <summary>
    /// 「口令 + 动态码」的 PAM:口令那一轮照样用已存的密码代答,动态码那一轮谢绝、不问人,报成「要你亲自连一次」;
    /// 密码不会被填进验证码那一轮(那会白白记一次动态码失败)。
    /// </summary>
    [TestMethod]
    public async Task PasswordThenCode_AnswersThePasswordRound_AndDeclinesTheCodeWithoutAsking()
    {
        await using ProbeServer server = new(new TestAuthPolicy
        {
            OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
            RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
            KeyboardRounds =
            [
                new() { Prompts = [("Password: ", false)], ExpectedAnswers = [Password] },
                new() { Prompts = [("Verification code: ", false)], ExpectedAnswers = ["123456"] },
            ],
        });
        IKeyboardInteractivePrompt keyboard = Substitute.For<IKeyboardInteractivePrompt>();

        Exception failure = await Assert.ThrowsAsync<Exception>(
            () => ConnectAsync(Info(server.Port, unattended: true), FixtureHostKeys.Create(), keyboardPrompt: keyboard));

        Assert.Contains(e => e is VelaSshAuthenticationCancelledException, Chain(failure), failure.ToString());
        Assert.Contains(Strings.Get("SshErr_KbdAuthUnattended"), failure.Message);
        await keyboard.DidNotReceiveWithAnyArgs().AskAsync(default!, default);
        await server.WaitForSessionAsync();
        Assert.HasCount(1, server.KeyboardAnswers, "只答了口令那一轮。");
        Assert.AreSequenceEqual([Password], [.. server.KeyboardAnswers[0]]);
    }

    // ---- 辅助 ----

    private static ConnectionInfo Info(int port, bool unattended) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "joe",
        AuthMethod = AuthMethod.Password,
        Password = Password,
        IsUnattended = unattended,
    };

    private static (IHostKeyService Store, ISettingsService Settings) TrustStore(HostKeyVerification verification)
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.VerifyHostKeyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(verification);
        store.FindKnownHostKeysAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<KnownHost>>([]));
        AppSettings snapshot = new();
        snapshot.Security.ConfirmFirstFingerprint = true;
        snapshot.Security.BlockOnFingerprintChange = false;
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.CurrentSnapshot.Returns(snapshot);
        settings.GetSettingsAsync().Returns(snapshot);
        return (store, settings);
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static async Task<VelaSshClientWrapper> ConnectAsync(
        ConnectionInfo info,
        IHostKeyService hostKey,
        ISettingsService? settings = null,
        IHostKeyPrompt? prompt = null,
        ISecurityAlertService? alerts = null,
        IKeyboardInteractivePrompt? keyboardPrompt = null)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey, settings, prompt, alerts, proxyResolver: null, keyboardPrompt);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh) { Route = assembled.Route };
        using CancellationTokenSource cts = new(Deadline);
        try
        {
            await wrapper.ConnectAsync(cts.Token);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }

    /// <summary>只接一条连接的环回服务端:握手、按给定策略认证,记下 keyboard-interactive 收到的答案。</summary>
    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Task _session;

        public ProbeServer(TestAuthPolicy policy)
        {
            _listener.Start();
            _session = ServeAsync(policy);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<IReadOnlyList<string>> KeyboardAnswers { get; } = [];

        public async Task WaitForSessionAsync() => await _session.WaitAsync(Deadline);

        private async Task ServeAsync(TestAuthPolicy policy)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using TestSshServer server = new(client.GetStream());
                TestSshServerHandshake handshake = await server.HandshakeAsync(_cts.Token);
                TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, policy) { SshServer = server };
                try
                {
                    await auth.RunAsync(_cts.Token);
                }
                finally
                {
                    KeyboardAnswers.AddRange(auth.Observation.KeyboardAnswers);
                }
            }
            catch (Exception)
            {
                // 客户端走了、用例拆场 —— 服务端这一侧的收尾不是被测对象。
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _session;
            }
            catch (Exception)
            {
                // 同上。
            }
            _cts.Dispose();
        }
    }
}
