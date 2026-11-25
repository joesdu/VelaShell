using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>环回 TCP 上的真实版本交换、KEX、认证和宿主会话生命周期；不替换协议或客户端。</summary>
[TestClass]
[TestCategory("SshConnection")]
public sealed class TailscaleSshAuthenticationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string LegalNotice = "未经授权的访问将被记录。";

    [TestMethod]
    public async Task Accept只发送None且不读取历史秘密()
    {
        await using Harness harness = new();
        await using Round round = harness.Start(Accept());
        SshSession session = await harness.ConnectAsync();

        await AssertConnectedAsync(harness, round, session);
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        Assert.AreEqual("stale-password", harness.Profile.Password);
        Assert.AreEqual("missing-stale-key", harness.Profile.PrivateKeyPath);
        Assert.AreEqual("stale-passphrase", harness.Profile.PrivateKeyPassphrase);
        Assert.AreEqual("missing-stale-certificate", harness.Profile.CertificatePath);
        await harness.DisconnectAsync(session);
        await round.Finished.WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task Check等待窗口批准时仍能读取Success且结束通知清理窗口()
    {
        string uri = VerificationUri();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        TaskCompletionSource windowLifetime = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = new(show: n =>
        {
            if (n.VerificationUri is null)
            {
                windowLifetime.TrySetResult();
            }
            // 显示/更新只等 UI 操作完成，不把整个窗口生命周期当成回调返回值。
            return Task.CompletedTask;
        });
        await using Round round = harness.Start(Check(uri, approve));
        Task<SshSession> connecting = harness.ConnectAsync();
        TailscaleSshCheckNotification shown = await harness.NextAsync();
        AssertShown(harness, shown, uri);
        Assert.IsFalse(connecting.IsCompleted);
        Assert.IsFalse(windowLifetime.Task.IsCompleted);

        approve.SetResult(true);
        SshSession session = await connecting.WaitAsync(Deadline);
        await AssertConnectedAsync(harness, round, session);
        await harness.ExpectEndAsync(shown);
        Assert.IsTrue(windowLifetime.Task.IsCompleted, "SUCCESS 必须发出 End，而不是等窗口关闭才读取认证结果。");
        shown.Cancel!();
        Assert.IsTrue(harness.Service.GetClient(session.SessionId)!.IsConnected, "成功后的旧取消按钮不得拆掉会话。");
        await harness.DisconnectAsync(session);
    }

    [TestMethod]
    [DataRow("button", "SshErr_TailscaleCheckCancelled")]
    [DataRow("caller", "")]
    [DataRow("caller-and-button", "")]
    [DataRow("timeout", "SshErr_TailscaleCheckTimeout")]
    [DataRow("reject", "SshErr_TailscaleRejected")]
    [DataRow("peer", "SshErr_TailscaleCheckFailed")]
    public async Task Check取消超时拒绝和断开均结束本次认证(string outcome, string resource)
    {
        string uri = VerificationUri();
        string token = uri[(uri.LastIndexOf('/') + 1)..];
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new(authenticationTimeout: outcome == "timeout" ? TimeSpan.FromSeconds(2) : null);
        await using Round round = harness.Start(Check(uri, approve));
        using CancellationTokenSource caller = new();
        var elapsed = Stopwatch.StartNew();
        Task<SshSession> connecting = harness.ConnectAsync(caller.Token);
        TailscaleSshCheckNotification shown = await harness.NextAsync();
        AssertShown(harness, shown, uri);
        Assert.IsFalse(connecting.IsCompleted);

        switch (outcome)
        {
            case "button": shown.Cancel!(); break;
            case "caller": caller.Cancel(); break;
            case "caller-and-button": caller.Cancel(); shown.Cancel!(); break;
            case "reject": approve.SetResult(false); break;
            case "peer": round.ClosePeer(); break;
        }

        Exception failure = await Assert.ThrowsAsync<Exception>(async () => await connecting.WaitAsync(Deadline));
        if (outcome is "caller" or "caller-and-button")
        {
            Assert.IsInstanceOfType<OperationCanceledException>(failure);
        }
        else if (outcome == "button")
        {
            Assert.IsInstanceOfType<VelaSshAuthenticationCancelledException>(failure);
            Assert.Contains(Strings.Get(resource), failure.Message);
            Assert.IsNull(failure.InnerException, "用户取消不能保留携带服务端原文的原始异常链。");
        }
        else
        {
            Assert.IsInstanceOfType<VelaSshTailscaleAuthenticationException>(failure);
            Assert.Contains(Strings.Get(resource), failure.Message);
            Assert.IsNull(failure.InnerException, "不能保留携带服务端原文的原始异常链。");
            Assert.IsTrue(((VelaSshTailscaleAuthenticationException)failure).RequiresUserAction, "出现过验证链接的失败要人重新验证。");
        }
        if (outcome == "timeout")
        {
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1), elapsed.Elapsed);
            Assert.IsLessThan(Deadline, elapsed.Elapsed);
        }
        await harness.ExpectEndAsync(shown);
        shown.Cancel!();
        shown.Cancel!();
        Assert.IsEmpty(harness.Service.Sessions);
        AssertNoSecret(token, failure.ToString(), harness.Log.Text, shown.ToString(), harness.Notices());
        Assert.IsFalse(round.Succeeded.Task.IsCompletedSuccessfully && round.Succeeded.Task.Result);
    }

    [TestMethod]
    public async Task None直接被拒时不要求密码且不建立验证窗口()
    {
        await using Harness harness = new();
        await using Round round = harness.Start(new TestAuthPolicy
        {
            RequiredMethods = [SshProtocolNames.AuthPassword],
            OfferedMethods = [SshProtocolNames.AuthPassword, SshProtocolNames.AuthPublicKey],
            Banners = [LegalNotice]
        });

        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await harness.ConnectAsync());

        Assert.Contains(Strings.Get("SshErr_TailscaleRejected"), failure.Message);
        Assert.IsNull(failure.InnerException);
        Assert.IsTrue(failure.RequiresUserAction, "服务端明确拒绝,再试多少次结果都一样。");
        Assert.AreSequenceEqual([SshProtocolNames.AuthNone], (await round.Ready.Task.WaitAsync(Deadline)).Observation.RequestedMethods);
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        Assert.IsEmpty(harness.Service.Sessions);
        Assert.IsFalse(await round.Succeeded.Task.WaitAsync(Deadline));
        Assert.Contains(LegalNotice, harness.Notices());
    }

    [TestMethod]
    public async Task 没有验证界面时安全失败且不发送密码()
    {
        string uri = VerificationUri();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new(hasUi: false);
        await using Round round = harness.Start(Check(uri, approve));

        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await harness.ConnectAsync().WaitAsync(Deadline));

        Assert.Contains(Strings.Get("SshErr_TailscaleCheckUiUnavailable"), failure.Message);
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        Assert.IsEmpty(harness.Service.Sessions);
        Assert.AreSequenceEqual([SshProtocolNames.AuthNone], (await round.Ready.Task.WaitAsync(Deadline)).Observation.RequestedMethods);
        AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], failure.ToString(), harness.Log.Text, harness.Notices());
    }

    [TestMethod]
    public async Task 自动重连Accept仍成功且不弹窗()
    {
        await using Harness harness = new(automatic: true);
        await using Round round = harness.Start(Accept());
        SshSession session = await harness.ConnectAsync();
        await AssertConnectedAsync(harness, round, session);
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        await harness.DisconnectAsync(session);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 普通密码目标的Tailscale跳板拒绝或自动Check不会启动目标认证(bool automatic)
    {
        using TcpListener target = new(IPAddress.Loopback, 0);
        target.Start();
        int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new(profile: port => new ConnectionInfo
        {
            Host = "127.0.0.1",
            Port = targetPort,
            Username = "target-user",
            AuthMethod = AuthMethod.Password,
            Password = "target-password",
            IsAutomaticReconnect = automatic,
            KeepAliveSeconds = 0,
            JumpHost = ProfileFor(port)
        });
        await using Round round = harness.Start(Check(VerificationUri(), approve));
        Task<SshSession> connecting = harness.ConnectAsync();
        TailscaleSshCheckNotification? shown = null;
        if (!automatic)
        {
            shown = await harness.NextAsync();
            Assert.AreEqual($"tailscale-user@127.0.0.1:{harness.Port}", shown.Target);
            approve.SetResult(false);
        }
        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await connecting.WaitAsync(Deadline));
        Assert.Contains(Strings.Get(automatic ? "SshErr_TailscaleCheckRequired" : "SshErr_TailscaleRejected"), failure.Message);
        if (shown is not null)
        {
            await harness.ExpectEndAsync(shown);
        }
        Assert.IsFalse(target.Pending(), "跳板认证失败后，不能拨号或尝试目标密码。");
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        Assert.IsEmpty(harness.Service.Sessions);
        Assert.AreSequenceEqual([SshProtocolNames.AuthNone], (await round.Ready.Task.WaitAsync(Deadline)).Observation.RequestedMethods);
    }

    [TestMethod]
    public async Task Tailscale目标的普通密码跳板拒绝保留实际认证跳与普通异常()
    {
        using TcpListener target = new(IPAddress.Loopback, 0);
        target.Start();
        int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        await using Harness harness = new(profile: port => new ConnectionInfo
        {
            Host = "127.0.0.1",
            Port = targetPort,
            Username = "tailscale-target",
            AuthMethod = AuthMethod.Tailscale,
            KeepAliveSeconds = 0,
            JumpHost = new ConnectionInfo
            {
                Host = "127.0.0.1",
                Port = port,
                Username = "password-jump",
                AuthMethod = AuthMethod.Password,
                Password = "wrong-password",
                KeepAliveSeconds = 0
            }
        });
        await using Round round = harness.Start(new TestAuthPolicy
        {
            ExpectedUserName = "password-jump",
            RequiredMethods = [SshProtocolNames.AuthPassword],
            OfferedMethods = [SshProtocolNames.AuthPassword],
            AcceptPassword = "accepted-password",
            Banners = [LegalNotice]
        });

        VelaSshAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshAuthenticationException>(
            async () => await harness.ConnectAsync());

        Assert.Contains(Strings.Format("SshErr_AuthExhausted", SshProtocolNames.AuthPassword), failure.Message);
        Assert.Contains($"password-jump@127.0.0.1:{harness.Port}", failure.Message);
        Assert.DoesNotContain("tailscale-target", failure.Message);
        Assert.DoesNotContain($"127.0.0.1:{targetPort}", failure.Message);
        Assert.IsFalse(target.Pending(), "普通跳板认证失败后不能拨号 Tailscale 目标。");
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        Assert.IsEmpty(harness.Service.Sessions);
        Assert.IsFalse(await round.Succeeded.Task.WaitAsync(Deadline));
        await round.Finished.WaitAsync(Deadline);
        TestAuthObservation observation = (await round.Ready.Task.WaitAsync(Deadline)).Observation;
        Assert.AreSequenceEqual([SshProtocolNames.AuthNone, SshProtocolNames.AuthPassword], observation.RequestedMethods);
        Assert.AreSequenceEqual(["password-jump"], observation.UserNames);
        Assert.Contains(LegalNotice, harness.Notices());
    }

    [TestMethod]
    [DataRow(true, "login.tailscale.com")]
    [DataRow(false, "login.tailscale.com")]
    [DataRow(true, "login\u3002tailscale.com")]
    [DataRow(false, "login\u3002tailscale.com")]
    [DataRow(true, "ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ")]
    [DataRow(false, "ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ")]
    public async Task 验证链接只进入专属通知而法律声明保留(bool accepted, string host)
    {
        string uri = VerificationUri().Replace("login.tailscale.com", host, StringComparison.Ordinal);
        string token = uri[(uri.LastIndexOf('/') + 1)..];
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new();
        await using Round round = harness.Start(Check($"{LegalNotice}\nTo authenticate, visit {uri}", approve));
        Task<SshSession> connecting = harness.ConnectAsync();
        TailscaleSshCheckNotification shown = await harness.NextAsync();
        AssertShown(harness, shown, uri);
        approve.SetResult(accepted);
        string exceptionText = "";
        if (accepted)
        {
            SshSession session = await connecting.WaitAsync(Deadline);
            await AssertConnectedAsync(harness, round, session);
            await harness.DisconnectAsync(session);
        }
        else
        {
            VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
                async () => await connecting.WaitAsync(Deadline));
            Assert.IsNull(failure.InnerException);
            exceptionText = failure.ToString();
        }
        await harness.ExpectEndAsync(shown);
        string notices = harness.Notices();
        Assert.Contains(LegalNotice, notices);
        AssertNoSecret(token, notices, harness.Log.Text, exceptionText, shown.ToString());
    }

    [TestMethod]
    [DataRow("login.tailscale.com")]
    [DataRow("login\u3002tailscale.com")]
    [DataRow("ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ")]
    public async Task 前导行和Debug中的链接被脱敏但不会触发Check(string host)
    {
        string uri = VerificationUri().Replace("login.tailscale.com", host, StringComparison.Ordinal);
        await using Harness harness = new();
        await using Round round = harness.Start(Accept() with
        {
            DebugMessages = [(true, $"Debug: {LegalNotice} {uri}"), (false, uri)]
        }, new TestSshServerOptions { PreAuthBanner = [$"Pre-auth: {LegalNotice} {uri}"] });
        SshSession session = await harness.ConnectAsync();
        await AssertConnectedAsync(harness, round, session);
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        string notices = harness.Notices();
        Assert.Contains($"Pre-auth: {LegalNotice}", notices);
        Assert.Contains($"Debug: {LegalNotice}", notices);
        AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], notices, harness.Log.Text);
        await harness.DisconnectAsync(session);
    }

    [TestMethod]
    public async Task 同服务配置和监听器逐轮AcceptCheckAccept不缓存认证策略()
    {
        await using Harness harness = new();
        ConnectionInfo original = harness.Profile;
        Guid? previousAttempt = null;
        foreach (bool challenge in new[] { false, true, false })
        {
            string uri = VerificationUri();
            TaskCompletionSource<bool> approve = NewSignal<bool>();
            await using Round round = harness.Start(challenge ? Check(uri, approve) : Accept());
            Task<SshSession> connecting = harness.ConnectAsync();
            TailscaleSshCheckNotification? shown = null;
            if (challenge)
            {
                shown = await harness.NextAsync();
                AssertShown(harness, shown, uri);
                Assert.IsFalse(connecting.IsCompleted);
                previousAttempt = shown.AttemptId;
                approve.SetResult(true);
            }
            SshSession session = await connecting.WaitAsync(Deadline);
            Assert.AreSame(original, session.ConnectionInfo);
            await AssertConnectedAsync(harness, round, session);
            if (shown is not null)
            {
                await harness.ExpectEndAsync(shown);
            }
            Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
            AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], harness.Notices(), harness.Log.Text, shown?.ToString() ?? "");
            await harness.DisconnectAsync(session);
            await round.Finished.WaitAsync(Deadline);
        }
        Assert.IsNotNull(previousAttempt);
    }

    [TestMethod]
    public async Task Check无挑战再次挑战每轮重新取得链接与Attempt()
    {
        await using Harness harness = new();
        string uri = VerificationUri();
        Guid? previousAttempt = null;
        Action? staleCancel = null;
        foreach (bool challenge in new[] { true, false, true })
        {
            TaskCompletionSource<bool> approve = NewSignal<bool>();
            await using Round round = harness.Start(challenge ? Check(uri, approve) : Accept());
            Task<SshSession> connecting = harness.ConnectAsync();
            TailscaleSshCheckNotification? shown = null;
            if (challenge)
            {
                shown = await harness.NextAsync();
                AssertShown(harness, shown, uri);
                Assert.AreNotEqual(previousAttempt, shown.AttemptId);
                previousAttempt = shown.AttemptId;
                staleCancel?.Invoke();
                Assert.IsFalse(shown.CancellationToken.IsCancellationRequested);
                approve.SetResult(true);
            }
            SshSession session = await connecting.WaitAsync(Deadline);
            await AssertConnectedAsync(harness, round, session);
            if (shown is not null)
            {
                await harness.ExpectEndAsync(shown);
                staleCancel = shown.Cancel;
            }
            Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
            AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], harness.Notices(), harness.Log.Text, shown?.ToString() ?? "");
            await harness.DisconnectAsync(session);
            staleCancel?.Invoke();
            await round.Finished.WaitAsync(Deadline);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 同Attempt重复U1不重复提示而U2更新现有挑战(bool accepted)
    {
        string firstUri = VerificationUri();
        string secondUri = VerificationUri();
        TaskCompletionSource<bool> sendUpdates = NewSignal<bool>();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new();
        TestAuthServer? currentAuth = null;
        await using Round round = harness.Start(Accept() with
        {
            BannersBeforeSuccess = [$"{LegalNotice}\n{firstUri}"],
            BeforeSuccess = async ct =>
            {
                await sendUpdates.Task.WaitAsync(ct);
                await currentAuth!.SendBannerAsync(firstUri, ct);
                await currentAuth.SendBannerAsync(secondUri, ct);
                return await approve.Task.WaitAsync(ct);
            }
        });
        Task<SshSession> connecting = harness.ConnectAsync();
        TailscaleSshCheckNotification first = await harness.NextAsync();
        AssertShown(harness, first, firstUri);
        currentAuth = await round.Ready.Task.WaitAsync(Deadline);
        sendUpdates.SetResult(true);
        TailscaleSshCheckNotification updated = await harness.NextAsync();
        AssertShown(harness, updated, secondUri);
        Assert.AreEqual(first.AttemptId, updated.AttemptId);
        Assert.AreEqual(first.Target, updated.Target);
        Assert.IsFalse(connecting.IsCompleted);
        approve.SetResult(accepted);
        string exceptionText = "";
        if (accepted)
        {
            SshSession session = await connecting.WaitAsync(Deadline);
            await AssertConnectedAsync(harness, round, session);
            await harness.ExpectEndAsync(updated);
            first.Cancel!();
            updated.Cancel!();
            Assert.IsTrue(harness.Service.GetClient(session.SessionId)!.IsConnected);
            await harness.DisconnectAsync(session);
        }
        else
        {
            VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
                async () => await connecting.WaitAsync(Deadline));
            Assert.IsNull(failure.InnerException);
            exceptionText = failure.ToString();
            await harness.ExpectEndAsync(updated);
            Assert.IsEmpty(harness.Service.Sessions);
        }
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _), "重复 U1 不应留下额外的显示通知。");
        string notices = harness.Notices();
        Assert.Contains(LegalNotice, notices);
        AssertNoSecret(firstUri[(firstUri.LastIndexOf('/') + 1)..], notices, harness.Log.Text, exceptionText, first.ToString(), updated.ToString());
        AssertNoSecret(secondUri[(secondUri.LastIndexOf('/') + 1)..], notices, harness.Log.Text, exceptionText, first.ToString(), updated.ToString());
    }

    [TestMethod]
    [DataRow("button")]
    [DataRow("reject")]
    [DataRow("timeout")]
    public async Task 前轮取消拒绝或超时后同配置无横幅直接成功(string outcome)
    {
        await using Harness harness = new(authenticationTimeout: TimeSpan.FromSeconds(2));
        string uri = VerificationUri();
        TailscaleSshCheckNotification old;
        TaskCompletionSource<bool> firstApproval = NewSignal<bool>();
        await using (Round first = harness.Start(Check(uri, firstApproval)))
        {
            Task<SshSession> failed = harness.ConnectAsync();
            old = await harness.NextAsync();
            if (outcome == "reject")
            {
                firstApproval.SetResult(false);
            }
            else if (outcome == "button")
            {
                old.Cancel!();
            }
            Exception failure = await Assert.ThrowsAsync<Exception>(async () => await failed.WaitAsync(Deadline));
            if (outcome == "button")
            {
                Assert.IsInstanceOfType<VelaSshAuthenticationCancelledException>(failure);
            }
            else
            {
                Assert.IsInstanceOfType<VelaSshTailscaleAuthenticationException>(failure);
                Assert.Contains(Strings.Get(outcome == "timeout" ? "SshErr_TailscaleCheckTimeout" : "SshErr_TailscaleRejected"), failure.Message);
            }
            Assert.IsNull(failure.InnerException);
            await harness.ExpectEndAsync(old);
            Assert.IsEmpty(harness.Service.Sessions);
            AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], failure.ToString(), harness.Log.Text, harness.Notices(), old.ToString());
        }
        await using Round second = harness.Start(Accept());
        Task<SshSession> connecting = harness.ConnectAsync();
        old.Cancel!();
        SshSession session = await connecting.WaitAsync(Deadline);
        Assert.AreSame(harness.Profile, session.ConnectionInfo);
        await AssertConnectedAsync(harness, second, session);
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        old.Cancel!();
        Assert.IsTrue(harness.Service.GetClient(session.SessionId)!.IsConnected);
        await harness.DisconnectAsync(session);
    }

    [TestMethod]
    public async Task 自动FreshCheck失败后同服务配置端口的手动尝试无挑战成功()
    {
        await using Harness harness = new(automatic: true);
        string uri = VerificationUri();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using (Round first = harness.Start(Check(uri, approve)))
        {
            VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
                async () => await harness.ConnectAsync());
            Assert.Contains(Strings.Get("SshErr_TailscaleCheckRequired"), failure.Message);
            Assert.IsNull(failure.InnerException);
            Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
            Assert.IsEmpty(harness.Service.Sessions);
            AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], failure.ToString(), harness.Log.Text, harness.Notices());
        }
        // ConnectionInfo 的尝试标记是 init-only；配置字段不变，仅换成本轮的手动标记快照。
        ConnectionInfo manualAttempt = ProfileFor(harness.Port);
        Assert.AreEqual(harness.Profile.Host, manualAttempt.Host);
        Assert.AreEqual(harness.Profile.Port, manualAttempt.Port);
        Assert.AreEqual(harness.Profile.Username, manualAttempt.Username);
        Assert.AreEqual(harness.Profile.AuthMethod, manualAttempt.AuthMethod);
        Assert.AreEqual(harness.Profile.Password, manualAttempt.Password);
        Assert.AreEqual(harness.Profile.PrivateKeyPath, manualAttempt.PrivateKeyPath);
        Assert.AreEqual(harness.Profile.PrivateKeyPassphrase, manualAttempt.PrivateKeyPassphrase);
        Assert.AreEqual(harness.Profile.CertificatePath, manualAttempt.CertificatePath);
        Assert.AreEqual(harness.Profile.JumpHost, manualAttempt.JumpHost);
        Assert.AreEqual(harness.Profile.Ssh, manualAttempt.Ssh);
        Assert.AreEqual(harness.Profile.KeepAliveSeconds, manualAttempt.KeepAliveSeconds);
        Assert.IsTrue(harness.Profile.IsAutomaticReconnect);
        Assert.IsFalse(manualAttempt.IsAutomaticReconnect);
        await using Round second = harness.Start(Accept());
        SshSession session = await harness.Service.ConnectAsync(manualAttempt).WaitAsync(Deadline);
        await AssertConnectedAsync(harness, second, session);
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        await harness.DisconnectAsync(session);
    }

    [TestMethod]
    public async Task 同配置并发连接的取消和旧按钮不会影响另一认证()
    {
        await using Harness harness = new();
        TaskCompletionSource<bool> firstApproval = NewSignal<bool>();
        TaskCompletionSource<bool> secondApproval = NewSignal<bool>();
        string firstUri = VerificationUri();
        string secondUri = VerificationUri();
        await using Round first = harness.Start(Check(firstUri, firstApproval));
        Task<SshSession> firstConnecting = harness.ConnectAsync();
        TailscaleSshCheckNotification firstShown = await harness.NextAsync();
        await using Round second = harness.Start(Check(secondUri, secondApproval));
        Task<SshSession> secondConnecting = harness.ConnectAsync();
        TailscaleSshCheckNotification secondShown = await harness.NextAsync();
        Assert.AreEqual(firstShown.Target, secondShown.Target);
        Assert.AreNotEqual(firstShown.AttemptId, secondShown.AttemptId);
        firstShown.Cancel!();
        VelaSshAuthenticationCancelledException failure = await Assert.ThrowsExactlyAsync<VelaSshAuthenticationCancelledException>(
            async () => await firstConnecting.WaitAsync(Deadline));
        await harness.ExpectEndAsync(firstShown);
        firstShown.Cancel!();
        Assert.IsFalse(secondShown.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(secondConnecting.IsCompleted);
        secondApproval.SetResult(true);
        SshSession session = await secondConnecting.WaitAsync(Deadline);
        await AssertConnectedAsync(harness, second, session);
        await harness.ExpectEndAsync(secondShown);
        Assert.AreSame(harness.Profile, session.ConnectionInfo);
        Assert.HasCount(1, harness.Service.Sessions);
        firstShown.Cancel!();
        secondShown.Cancel!();
        Assert.IsTrue(harness.Service.GetClient(session.SessionId)!.IsConnected);
        string notices = harness.Notices();
        AssertNoSecret(firstUri[(firstUri.LastIndexOf('/') + 1)..], notices, harness.Log.Text, failure.ToString(), firstShown.ToString(), secondShown.ToString());
        AssertNoSecret(secondUri[(secondUri.LastIndexOf('/') + 1)..], notices, harness.Log.Text, failure.ToString(), firstShown.ToString(), secondShown.ToString());
        await harness.DisconnectAsync(session);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 显示回调同步或异步失败不能泄漏原始异常(bool asynchronous)
    {
        string uri = VerificationUri();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new(show: notification =>
        {
            if (notification.VerificationUri is null)
            {
                return Task.CompletedTask;
            }
            IOException raw = new(uri, new InvalidOperationException(uri));
            if (asynchronous)
            {
                return Task.FromException(raw);
            }
            throw raw;
        });
        await using Round round = harness.Start(Check($"{LegalNotice}\n{uri}", approve));
        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await harness.ConnectAsync().WaitAsync(Deadline));
        Assert.Contains(Strings.Get("SshErr_TailscaleCheckUiUnavailable"), failure.Message);
        Assert.IsNull(failure.InnerException);
        TailscaleSshCheckNotification shown = await harness.NextAsync();
        Assert.AreEqual(uri, shown.VerificationUri?.OriginalString);
        await harness.ExpectEndAsync(shown);
        string notices = harness.Notices();
        Assert.Contains(LegalNotice, notices);
        AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], failure.ToString(), harness.Log.Text, notices, shown.ToString());
        Assert.IsEmpty(harness.Service.Sessions);
    }

    [TestMethod]
    public async Task 尚未收到合法挑战时两秒认证超时不冒充Check超时()
    {
        string token = Guid.NewGuid().ToString("N");
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        TaskCompletionSource<bool> entered = NewSignal<bool>();
        await using Harness harness = new(authenticationTimeout: TimeSpan.FromSeconds(2));
        await using Round round = harness.Start(Accept() with
        {
            BannersBeforeSuccess = [$"{LegalNotice}\nhttp://login.tailscale.com/a/{token}"],
            BeforeSuccess = ct =>
            {
                entered.TrySetResult(true);
                return approve.Task.WaitAsync(ct);
            }
        });
        Task<SshSession> connecting = harness.ConnectAsync();
        await entered.Task.WaitAsync(Deadline);
        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await connecting.WaitAsync(Deadline));
        Assert.Contains(Strings.Get("SshErr_TailscaleAuthTimeout"), failure.Message);
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(failure.RequiresUserAction, "没等到链接的超时与别的认证方式一样照常退避重试。");
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        Assert.IsEmpty(harness.Service.Sessions);
        string notices = harness.Notices();
        Assert.Contains(LegalNotice, notices);
        AssertNoSecret(token, failure.ToString(), harness.Log.Text, notices);
    }

    [TestMethod]
    public async Task 收到验证链接后放宽期限_超过守卫期仍能等到批准()
    {
        string uri = VerificationUri();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new(authenticationTimeout: TimeSpan.FromSeconds(1), checkTimeout: TimeSpan.FromSeconds(30));
        await using Round round = harness.Start(Check(uri, approve));
        DateTimeOffset started = DateTimeOffset.Now;
        Task<SshSession> connecting = harness.Service.ConnectAsync(harness.Profile);
        TailscaleSshCheckNotification shown = await harness.NextAsync();
        AssertShown(harness, shown, uri);
        Assert.IsNotNull(shown.ExpiresAt, "验证窗要知道最长等到几点。");
        Assert.IsGreaterThanOrEqualTo(started + TimeSpan.FromSeconds(29), shown.ExpiresAt.Value);
        Assert.IsLessThanOrEqualTo(DateTimeOffset.Now + TimeSpan.FromSeconds(31), shown.ExpiresAt.Value);

        // 守卫期(1 秒)早过了:曾经的认证期限在开始时一次定死,收到链接也不放宽,SSO + 多因素来不及。
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.IsFalse(connecting.IsCompleted, "收到合法链接之后不能再按守卫期判超时。");
        approve.SetResult(true);
        SshSession session = await connecting.WaitAsync(Deadline);
        await AssertConnectedAsync(harness, round, session);
        await harness.ExpectEndAsync(shown);
        await harness.DisconnectAsync(session);
    }

    [TestMethod]
    public async Task 没有验证链接时认证途中断线不暂停自动重连()
    {
        TaskCompletionSource<bool> entered = NewSignal<bool>();
        await using Harness harness = new();
        await using Round round = harness.Start(Accept() with
        {
            BannersBeforeSuccess = [LegalNotice],
            BeforeSuccess = async ct =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return false;
            }
        });
        Task<SshSession> connecting = harness.ConnectAsync();
        await entered.Task.WaitAsync(Deadline);
        round.ClosePeer();

        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await connecting.WaitAsync(Deadline));
        Assert.Contains(Strings.Get("SshErr_TailscaleCheckFailed"), failure.Message);
        Assert.IsFalse(failure.RequiresUserAction, "连接被重置不是授权结论,要照常退避重试,而不是永久暂停。");
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _));
        Assert.IsEmpty(harness.Service.Sessions);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 横幅说明后断开按拒绝报_并带上去掉链接与控制字符的服务端说明(bool afterCheck)
    {
        string uri = VerificationUri();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        const string Denial = "tailnet policy does not permit you to SSH as user \"root\"";
        string banner = $"{Denial}\nsee https://evil.example/{Guid.NewGuid():N} \u001b[31mnow\u001b[0m";
        await using Harness harness = new();
        TestAuthPolicy policy = afterCheck
            ? Check($"{LegalNotice}\n{uri}", approve) with { RejectWithBannerAndDisconnect = banner }
            : new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPassword],
                OfferedMethods = [SshProtocolNames.AuthPassword],
                RejectWithBannerAndDisconnect = banner
            };
        await using Round round = harness.Start(policy);
        Task<SshSession> connecting = harness.ConnectAsync();
        TailscaleSshCheckNotification? shown = null;
        if (afterCheck)
        {
            shown = await harness.NextAsync();
            AssertShown(harness, shown, uri);
            approve.SetResult(false);
        }

        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await connecting.WaitAsync(Deadline));
        Assert.Contains(Strings.Get("SshErr_TailscaleRejected"), failure.Message, "真实的拒绝是横幅 + DISCONNECT,不能报成笼统的验证失败。");
        Assert.Contains(Strings.Format("SshErr_TailscaleServerMessage", "").TrimEnd(), failure.Message);
        Assert.Contains(Denial, failure.Message);
        Assert.DoesNotContain("https://", failure.Message, "服务端说明里的链接不能借应用的失败提示出现。");
        Assert.DoesNotContain("\u001b", failure.Message);
        Assert.DoesNotContain(LegalNotice, failure.Message, "验证链接之前的横幅不是拒绝的理由。");
        Assert.IsTrue(failure.RequiresUserAction);
        Assert.IsNull(failure.InnerException);
        if (shown is not null)
        {
            await harness.ExpectEndAsync(shown);
            AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], failure.ToString(), harness.Log.Text);
        }
        Assert.IsEmpty(harness.Service.Sessions);
    }

    [TestMethod]
    public async Task 密码配置遇到Tailscale验证链接立即失败并提示改用Tailscale认证()
    {
        string uri = VerificationUri();
        TaskCompletionSource<bool> approve = NewSignal<bool>();
        await using Harness harness = new(profile: port => new ConnectionInfo
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "ops",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
            KeepAliveSeconds = 0
        });
        // 库开头那条 none 探测被 check 模式的服务端挂起等批准;链接在横幅里。
        await using Round round = harness.Start(Check($"To authenticate, visit: {uri}", approve));
        var elapsed = Stopwatch.StartNew();

        VelaSshTailscaleAuthenticationException failure = await Assert.ThrowsExactlyAsync<VelaSshTailscaleAuthenticationException>(
            async () => await harness.ConnectAsync());

        Assert.IsLessThan(Deadline, elapsed.Elapsed, "不能干等到认证期限。");
        Assert.Contains(Strings.Get("SshErr_TailscaleAuthMethodRequired"), failure.Message);
        Assert.Contains($"ops@127.0.0.1:{harness.Port}", failure.Message);
        Assert.IsTrue(failure.RequiresUserAction, "要改认证方式才连得上,后台重试没有意义。");
        Assert.IsFalse(harness.Notifications.Reader.TryRead(out _), "密码配置不弹 Tailscale 验证窗。");
        Assert.AreSequenceEqual([SshProtocolNames.AuthNone], (await round.Ready.Task.WaitAsync(Deadline)).Observation.RequestedMethods,
            "还没轮到密码就该停下,密码不发出去。");
        Assert.IsEmpty(harness.Service.Sessions);
        AssertNoSecret(uri[(uri.LastIndexOf('/') + 1)..], failure.ToString(), harness.Log.Text, harness.Notices());
    }

    [TestMethod]
    public async Task 守卫期从主机密钥裁决结束时起算_放宽只一次_结束后不再碰取消源()
    {
        TaskCompletionSource<SshHostKeyVerdict> decision = NewSignal<SshHostKeyVerdict>();
        using CancellationTokenSource attempt = new();
        TailscaleAuthDeadline deadline = new(new SlowHostKeyPolicy(decision.Task), attempt,
            guard: TimeSpan.FromMilliseconds(200), checkWait: TimeSpan.FromSeconds(30));

        using var hostKey = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        ValueTask<SshHostKeyVerdict> evaluating = deadline.EvaluateAsync(new SshHostKeyContext
        {
            Host = "node",
            Port = 22,
            Key = SshPublicKey.Decode(hostKey.PublicKeyBlob),
            NegotiatedAlgorithm = SshAlgorithmNames.SshEd25519
        });
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.IsFalse(attempt.IsCancellationRequested, "用户看指纹的时间不算进认证期限。");
        Assert.IsFalse(deadline.IsArmed);

        decision.SetResult(SshHostKeyVerdict.Accept);
        await evaluating;
        Assert.IsTrue(deadline.IsArmed);
        await Task.Delay(Timeout.Infinite, attempt.Token).ContinueWith(_ => { }).WaitAsync(Deadline);

        using CancellationTokenSource second = new();
        TailscaleAuthDeadline check = new(new SlowHostKeyPolicy(Task.FromResult(SshHostKeyVerdict.Accept)), second,
            guard: TimeSpan.FromMilliseconds(200), checkWait: TimeSpan.FromSeconds(30));
        DateTimeOffset? expiresAt = check.ExtendForCheck();
        Assert.IsNotNull(expiresAt);
        Assert.AreEqual(expiresAt, check.ExtendForCheck(), "同一跳的新链接沿用同一个截止时刻。");
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.IsFalse(second.IsCancellationRequested);
        check.Finish();
        Assert.IsNull(check.ExtendForCheck());
    }

    private sealed class SlowHostKeyPolicy(Task<SshHostKeyVerdict> verdict) : IHostKeyPolicy
    {
        public async ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            await verdict.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string VerificationUri() => $"https://login.tailscale.com/a/{Guid.NewGuid():N}";
    private static TestAuthPolicy Accept() => new()
    {
        RequiredMethods = [SshProtocolNames.AuthNone],
        OfferedMethods = []
    };
    private static TestAuthPolicy Check(string banner, TaskCompletionSource<bool> approve) => Accept() with
    {
        BannersBeforeSuccess = [banner],
        BeforeSuccess = ct => approve.Task.WaitAsync(ct)
    };
    private static ConnectionInfo ProfileFor(int port, bool automatic = false) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "tailscale-user",
        AuthMethod = AuthMethod.Tailscale,
        Password = "stale-password",
        PrivateKeyPath = "missing-stale-key",
        PrivateKeyPassphrase = "stale-passphrase",
        CertificatePath = "missing-stale-certificate",
        IsAutomaticReconnect = automatic,
        KeepAliveSeconds = 0
    };
    private static void AssertShown(Harness harness, TailscaleSshCheckNotification notification, string uri)
    {
        Assert.AreNotEqual(Guid.Empty, notification.AttemptId);
        Assert.AreEqual($"tailscale-user@127.0.0.1:{harness.Port}", notification.Target);
        Assert.AreEqual(uri, notification.VerificationUri?.OriginalString);
        Assert.IsNotNull(notification.Cancel);
        Assert.IsTrue(notification.CancellationToken.CanBeCanceled);
        Assert.IsFalse(notification.CancellationToken.IsCancellationRequested);
    }
    private static async Task AssertConnectedAsync(Harness harness, Round round, SshSession session)
    {
        Assert.IsTrue(await round.Succeeded.Task.WaitAsync(Deadline), "成功必须来自 RunAsync 结果，不是已通过方法列表。");
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        Assert.IsTrue(harness.Service.GetClient(session.SessionId)!.IsConnected);
        Assert.AreSequenceEqual([SshProtocolNames.AuthNone], (await round.Ready.Task.WaitAsync(Deadline)).Observation.RequestedMethods);
        Assert.IsFalse(round.Finished.IsCompleted, "SUCCESS 后服务端必须保留传输直到客户端断开。");
    }
    private static void AssertNoSecret(string secret, params string[] texts)
    {
        foreach (string text in texts)
        {
            Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal), "授权 token 不得进入横幅、日志或异常链。");
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<SshServerBanners> _banners = new();
        private readonly List<Round> _rounds = [];
        public Channel<TailscaleSshCheckNotification> Notifications { get; } = Channel.CreateUnbounded<TailscaleSshCheckNotification>();
        public CapturedLog Log { get; } = new();
        public ConnectionInfo Profile { get; }
        public SshConnectionService Service { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        // checkTimeout:收到链接之后的等待上限;不给时与 authenticationTimeout 相同(沿用旧用例的「两秒就超时」)。
        public Harness(bool automatic = false, bool hasUi = true, TimeSpan? authenticationTimeout = null,
            Func<TailscaleSshCheckNotification, Task>? show = null, Func<int, ConnectionInfo>? profile = null,
            TimeSpan? checkTimeout = null)
        {
            _listener.Start();
            Profile = profile?.Invoke(Port) ?? ProfileFor(Port, automatic);
            Service = new SshConnectionService(info =>
            {
                SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
                    info, hostKey: FixtureHostKeys.Create(), settings: null, prompt: null, alerts: null, proxyResolver: null,
                    tailscaleCheckPrompt: hasUi ? NotifyAsync : null, authenticationTimeout: authenticationTimeout,
                    tailscaleCheckTimeout: checkTimeout ?? authenticationTimeout);
                _banners.Enqueue(assembled.Banners);
                return new VelaSshClientWrapper(assembled.Connect, assembled.ConnectTimeout, info.Ssh, banners: assembled.Banners);
            }, Log);

            Task NotifyAsync(TailscaleSshCheckNotification notification)
            {
                Notifications.Writer.TryWrite(notification);
                return show?.Invoke(notification) ?? Task.CompletedTask;
            }
        }
        public Round Start(TestAuthPolicy policy, TestSshServerOptions? options = null)
        {
            Round round = new(_listener, policy, options);
            _rounds.Add(round);
            return round;
        }
        public Task<SshSession> ConnectAsync(CancellationToken cancellationToken = default) =>
            Service.ConnectAsync(Profile, cancellationToken).WaitAsync(Deadline);
        public async Task DisconnectAsync(SshSession session)
        {
            await Service.DisconnectAsync(session.SessionId);
            Assert.AreEqual(SessionStatus.Disconnected, session.Status);
            Assert.IsNull(Service.GetClient(session.SessionId));
        }
        public async Task<TailscaleSshCheckNotification> NextAsync() => await Notifications.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        public async Task ExpectEndAsync(TailscaleSshCheckNotification shown)
        {
            TailscaleSshCheckNotification ended = await NextAsync();
            Assert.AreEqual(shown.AttemptId, ended.AttemptId);
            Assert.AreEqual(shown.Target, ended.Target);
            Assert.IsNull(ended.VerificationUri);
            Assert.IsNull(ended.Cancel);
            Assert.AreEqual(CancellationToken.None, ended.CancellationToken);
            Assert.IsTrue(shown.CancellationToken.IsCancellationRequested);
        }
        public string Notices() => string.Join('\n', _banners.SelectMany(b => b.TakeNotices()).Select(n => n.Text));
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.DisposeAsync();
            }
            finally
            {
                foreach (Round round in _rounds)
                {
                    await round.DisposeAsync();
                }
                _listener.Stop();
            }
        }
    }

    // 每轮服务端有独立生命周期，不随调用方令牌提前消失而伪造客户端取消成功。
    private sealed class Round : IAsyncDisposable
    {
        private TcpClient? _peer;
        private bool _disposed;
        public CancellationTokenSource Lifetime { get; } = new();
        public TaskCompletionSource<TestAuthServer> Ready { get; } = NewSignal<TestAuthServer>();
        public TaskCompletionSource<bool> Succeeded { get; } = NewSignal<bool>();
        public Task Finished { get; }

        public Round(TcpListener listener, TestAuthPolicy policy, TestSshServerOptions? options) => Finished = RunAsync(listener, policy, options);
        public void ClosePeer() => _peer!.Dispose();
        private async Task RunAsync(TcpListener listener, TestAuthPolicy policy, TestSshServerOptions? options)
        {
            try
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync(Lifetime.Token);
                _peer = peer;
                NetworkStream stream = peer.GetStream();
                if (options is { PreAuthBanner.Count: > 0 })
                {
                    // fixture 的 WriteLineAsync 仅用于 ASCII 标识串；前导文本须保留 UTF-8，不能先把 IDN 写成问号。
                    foreach (string line in options.PreAuthBanner)
                    {
                        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\r\n"), Lifetime.Token);
                    }
                    await stream.FlushAsync(Lifetime.Token);
                    options = options with { PreAuthBanner = [] };
                }
                await using TestSshServer server = new(stream, options);
                TestSshServerHandshake handshake = await server.HandshakeAsync(Lifetime.Token);
                TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, policy) { SshServer = server };
                Ready.TrySetResult(auth);
                bool authenticated = await auth.RunAsync(Lifetime.Token);
                Succeeded.TrySetResult(authenticated);
                if (!authenticated)
                {
                    return;
                }
                while (true)
                {
                    SshInboundPacket packet = await server.Transport.ReadPacketAsync(Lifetime.Token);
                    if (packet.IsEndOfStream || packet.MessageNumber == SshMessageNumber.Disconnect)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
            {
                Succeeded.TrySetResult(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or SshException or ObjectDisposedException)
            {
                Ready.TrySetException(ex);
                Succeeded.TrySetResult(false);
            }
            catch (Exception ex)
            {
                Ready.TrySetException(ex);
                Succeeded.TrySetException(ex);
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                await Lifetime.CancelAsync();
                _peer?.Dispose();
            }
            finally
            {
                await Finished.WaitAsync(Deadline);
                Lifetime.Dispose();
            }
        }
    }

    private sealed class CapturedLog : ILogger<SshConnectionService>
    {
        private readonly ConcurrentQueue<string> _entries = new();
        public string Text => string.Join('\n', _entries);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue(formatter(state, exception));
            if (exception is not null)
            {
                _entries.Enqueue(exception.ToString());
            }
        }
    }
}
