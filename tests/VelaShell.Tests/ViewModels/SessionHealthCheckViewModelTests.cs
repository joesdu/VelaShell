using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 会话健康巡检:用户点开才跑、一次一批、并发有上限、可停、每台有期限;认证那一步不弹框(缺凭据记「需要凭据」,
/// 要人回答的记「需要确认」);结果表列出每台的结论与卡在哪一步。
/// </summary>
[TestClass]
[TestCategory("HealthCheck")]
public sealed class SessionHealthCheckViewModelTests
{
    // ---- 归类 ----

    [TestMethod]
    public void Classify_AllStepsPassed_IsHealthy()
    {
        SessionHealthOutcome outcome = SessionHealthClassifier.Classify(Report(DiagnosticStepStatus.Success), 42);

        Assert.AreEqual(SessionHealthStatus.Healthy, outcome.Status);
        Assert.IsNull(outcome.Step);
        Assert.AreEqual(42, outcome.ElapsedMs);
    }

    [TestMethod]
    public void Classify_NoSavedCredentials_NeedsCredentials_NotAFailure()
    {
        SessionHealthOutcome outcome = SessionHealthClassifier.Classify(
            Report(DiagnosticStepStatus.Warning, authDetail: Strings.Get("DiagSvc_NoCredsSkip")), 10);

        Assert.AreEqual(SessionHealthStatus.NeedsCredentials, outcome.Status);
        Assert.AreEqual("认证", outcome.Step);
    }

    [TestMethod]
    public void Classify_LoginNotVerified_IsReachable()
    {
        SessionHealthOutcome outcome = SessionHealthClassifier.Classify(
            Report(DiagnosticStepStatus.Skipped, authDetail: Strings.Get("DiagSvc_LoginNotVerified")), 10);

        Assert.AreEqual(SessionHealthStatus.Reachable, outcome.Status);
    }

    [TestMethod]
    public void Classify_TcpFailure_IsFailedAtThatStep_WithTheIssueAndTheReason()
    {
        DiagnosticReport report = new()
        {
            Steps =
            [
                new(0, "DNS", DiagnosticStepStatus.Success),
                new(1, "TCP", DiagnosticStepStatus.Failed, "Connection refused"),
                new(2, "SSH", DiagnosticStepStatus.Skipped),
                new(3, "认证", DiagnosticStepStatus.Skipped),
            ],
            IssueTitle = "TCP 建链失败",
        };

        SessionHealthOutcome outcome = SessionHealthClassifier.Classify(report, 10);

        Assert.AreEqual(SessionHealthStatus.Failed, outcome.Status);
        Assert.AreEqual("TCP", outcome.Step);
        Assert.Contains("TCP 建链失败", outcome.Detail);
        Assert.Contains("Connection refused", outcome.Detail);
    }

    /// <summary>主机指纹待确认、动态码被谢绝、Tailscale 要复核:不是机器坏了,是巡检不替人回答。</summary>
    [TestMethod]
    public void Classify_ThingsOnlyAPersonCanAnswer_NeedAttention()
    {
        Exception[] failures =
        [
            new VelaSshHostKeyRejectedException("指纹待确认"),
            new VelaSshAuthenticationCancelledException(Strings.Get("SshErr_KbdAuthUnattended")),
            new VelaSshConnectionException("连不上", new VelaSshTailscaleAuthenticationException("要复核")),
        ];
        foreach (Exception failure in failures)
        {
            DiagnosticReport report = Report(DiagnosticStepStatus.Failed, failure: failure, issue: "认证失败");

            Assert.AreEqual(SessionHealthStatus.NeedsAttention, SessionHealthClassifier.Classify(report, 1).Status, failure.Message);
        }
        Assert.AreEqual(SessionHealthStatus.Failed,
            SessionHealthClassifier.Classify(Report(DiagnosticStepStatus.Failed, failure: new VelaSshAuthenticationException("密码不对"), issue: "认证失败"), 1).Status,
            "密码不对就是连不上,不是待确认。");
    }

    /// <summary>
    /// 私钥带口令、配置里没存口令:读私钥那一步就失败(手动连接时会问口令),巡检记「需要凭据」而不是红色的「连不上」。
    /// 存着的口令不对是存错了,照旧算连不上。
    /// </summary>
    [TestMethod]
    public void Classify_KeyNeedsAPassphraseThatIsNotSaved_NeedsCredentials_NotAFailure()
    {
        var missing = new VelaSshAuthenticationException("这把私钥要口令",
            new SshPrivateKeyException(SshFailureReason.KeyPassphraseRequired, "key is encrypted"));
        SessionHealthOutcome outcome = SessionHealthClassifier.Classify(
            Report(DiagnosticStepStatus.Failed, failure: missing, issue: "认证失败"), 10);

        Assert.AreEqual(SessionHealthStatus.NeedsCredentials, outcome.Status);
        Assert.AreEqual("认证", outcome.Step);
        Assert.AreEqual("这把私钥要口令", outcome.Detail);

        var wrong = new VelaSshAuthenticationException("口令不对",
            new SshPrivateKeyException(SshFailureReason.KeyPassphraseIncorrect, "bad passphrase"));
        Assert.AreEqual(SessionHealthStatus.Failed,
            SessionHealthClassifier.Classify(Report(DiagnosticStepStatus.Failed, failure: wrong, issue: "认证失败"), 1).Status);
    }

    /// <summary>
    /// 巡检以「不许弹框」打开系统密钥库的钥:密钥库非得问人(PIN、插卡、确认)时报 NTE_SILENT_CONTEXT ——
    /// 打开时报的在失败链上,签名时才报的在认证尝试记录里。两种都记「需要凭据」,不是机器连不上。
    /// </summary>
    [TestMethod]
    public void Classify_KeyStoreKeyThatWouldPromptForAPin_NeedsCredentials()
    {
        var silent = new System.Security.Cryptography.CryptographicException("UI required") { HResult = unchecked((int)0x80090022) };
        var atOpen = new VelaSshAuthenticationException("打不开密钥库里的钥",
            new SshPrivateKeyException(SshFailureReason.KeyFileUnreadable, "cannot open", silent));
        var atSign = new VelaSshAuthenticationException("认证失败",
            new SshAuthenticationException(SshFailureReason.AuthenticationMethodExhausted, "all credentials failed",
                [
                    new SshAuthAttempt("publickey", "vela-key", SshAuthOutcome.SkippedNoMaterial, ["publickey"])
                    {
                        Details = [new SshAuthDetail(SshAuthDetailKind.CredentialFailed) { Error = silent }],
                    },
                ],
                ["publickey"]));

        foreach (Exception failure in new Exception[] { atOpen, atSign })
        {
            Assert.AreEqual(SessionHealthStatus.NeedsCredentials,
                SessionHealthClassifier.Classify(Report(DiagnosticStepStatus.Failed, failure: failure, issue: "认证失败"), 1).Status,
                failure.Message);
        }
        var otherCngError = new VelaSshAuthenticationException("打不开密钥库里的钥",
            new SshPrivateKeyException(SshFailureReason.KeyFileUnreadable, "not found",
                new System.Security.Cryptography.CryptographicException("Keyset does not exist") { HResult = unchecked((int)0x80090016) }));
        Assert.AreEqual(SessionHealthStatus.Failed,
            SessionHealthClassifier.Classify(Report(DiagnosticStepStatus.Failed, failure: otherCngError, issue: "认证失败"), 1).Status,
            "钥不在(别的 CNG 错误)照旧是连不上。");
    }

    [TestMethod]
    public void Classify_BrokenJumpChain_IsFailedAtTheConfiguration()
    {
        DiagnosticReport report = new()
        {
            Steps = [.. Enumerable.Range(0, 4).Select(i => new DiagnosticStepUpdate(i, $"步骤 {i}", DiagnosticStepStatus.Skipped))],
            IssueTitle = "跳板链配置错误",
            IssueDescription = "跳板主机已被删除",
        };

        SessionHealthOutcome outcome = SessionHealthClassifier.Classify(report, 1);

        Assert.AreEqual(SessionHealthStatus.Failed, outcome.Status);
        Assert.AreEqual(Strings.Get("Health_StepConfiguration"), outcome.Step);
        Assert.Contains("跳板主机已被删除", outcome.Detail);
    }

    // ---- 一批的跑法 ----

    [TestMethod]
    public async Task Run_ChecksEveryRow_AtMostSixAtOnce_InTheGivenOrder()
    {
        IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
        int inFlight = 0;
        int peak = 0;
        var release = new TaskCompletionSource();
        diagnostics.DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                   .Returns(async _ =>
                   {
                       int now = Interlocked.Increment(ref inFlight);
                       InterlockedMax(ref peak, now);
                       await release.Task;
                       Interlocked.Decrement(ref inFlight);
                       return Report(DiagnosticStepStatus.Success);
                   });
        var vm = new SessionHealthCheckViewModel(diagnostics);
        List<SessionProfile> profiles = [.. Enumerable.Range(1, 10).Select(i => Ssh($"web-{i:00}"))];

        Assert.IsTrue(vm.TryStart(profiles));
        await WaitUntilAsync(() => Volatile.Read(ref inFlight) == SessionHealthCheckViewModel.Concurrency);
        Assert.IsTrue(vm.IsRunning);
        Assert.IsFalse(vm.TryStart([Ssh("other")]), "上一轮没查完时不接新的一批。");
        Assert.AreEqual(Strings.Format("Health_SummaryRunning", 0, 10, Strings.Format("Health_Summary", 10, 0, 0, 0)), vm.Summary,
            "查着时汇总前面带进度。");
        release.SetResult();
        await WaitUntilAsync(() => !vm.IsRunning);

        Assert.AreEqual(SessionHealthCheckViewModel.Concurrency, Volatile.Read(ref peak), "最多六台同时查。");
        Assert.AreSequenceEqual([.. profiles.Select(p => p.Name)], [.. vm.Rows.Select(r => r.Name)], "按给的顺序列。");
        Assert.IsTrue(vm.Rows.All(r => r.Status == SessionHealthStatus.Healthy));
        Assert.AreEqual(Strings.Format("Health_Summary", 10, 10, 0, 0), vm.Summary);
    }

    [TestMethod]
    public async Task NonSshProfiles_AreListed_ButNeverProbed()
    {
        IConnectionDiagnosticsService diagnostics = Passing();
        var vm = new SessionHealthCheckViewModel(diagnostics);
        SessionProfile ftp = Ssh("files");
        ftp.ConnectionType = ConnectionType.FTP;

        vm.TryStart([Ssh("web"), ftp]);
        await WaitUntilAsync(() => !vm.IsRunning);

        Assert.AreEqual(SessionHealthStatus.Unsupported, vm.Rows[1].Status);
        Assert.AreEqual(Strings.Get("Health_UnsupportedDetail"), vm.Rows[1].Detail);
        await diagnostics.DidNotReceive().DiagnoseUnattendedAsync(ftp, Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
        await diagnostics.Received(1).DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Stop_CancelsWhatIsStillRunningOrWaiting()
    {
        IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
        int started = 0;
        diagnostics.DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                   .Returns(async call =>
                   {
                       Interlocked.Increment(ref started);
                       await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                       return Report(DiagnosticStepStatus.Success);
                   });
        var vm = new SessionHealthCheckViewModel(diagnostics);

        vm.TryStart([.. Enumerable.Range(1, 8).Select(i => Ssh($"h{i}"))]);
        await WaitUntilAsync(() => Volatile.Read(ref started) == SessionHealthCheckViewModel.Concurrency);
        await vm.StopCommand.Execute().FirstAsync();
        await WaitUntilAsync(() => !vm.IsRunning);

        Assert.IsTrue(vm.Rows.All(r => r.Status == SessionHealthStatus.Cancelled), string.Join(", ", vm.Rows.Select(r => r.Status)));
        Assert.AreEqual(SessionHealthCheckViewModel.Concurrency, Volatile.Read(ref started), "排着队的那两台没开始查。");
        Assert.AreEqual(Strings.Get("Health_NotChecked"), vm.Rows[^1].Detail);
    }

    [TestMethod]
    public async Task ASlowHost_TimesOut_WithoutHoldingUpTheOthers()
    {
        IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
        diagnostics.DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                   .Returns(async call =>
                   {
                       if (call.Arg<SessionProfile>().Name == "slow")
                       {
                           await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                       }
                       return Report(DiagnosticStepStatus.Success);
                   });
        var vm = new SessionHealthCheckViewModel(diagnostics, perHostTimeout: TimeSpan.FromMilliseconds(200));

        vm.TryStart([Ssh("slow"), Ssh("fast")]);
        await WaitUntilAsync(() => !vm.IsRunning);

        Assert.AreEqual(SessionHealthStatus.TimedOut, vm.Rows[0].Status);
        Assert.AreEqual(SessionHealthStatus.Healthy, vm.Rows[1].Status);
        Assert.IsTrue(vm.Rows[0].IsProblem);
    }

    /// <summary>关掉「验证登录」:下一轮只查到 SSH 握手(交给诊断的是 false);开关在这次启动里记住。</summary>
    [TestMethod]
    public async Task VerifyLogin_Off_IsPassedToTheDiagnosis()
    {
        IConnectionDiagnosticsService diagnostics = Passing();
        var vm = new SessionHealthCheckViewModel(diagnostics);
        try
        {
            await vm.ToggleVerifyLoginCommand.Execute().FirstAsync();
            Assert.IsFalse(vm.VerifyLogin);
            Assert.IsFalse(new SessionHealthCheckViewModel(diagnostics).VerifyLogin, "关掉窗口再开沿用。");

            vm.TryStart([Ssh("web")]);
            await WaitUntilAsync(() => !vm.IsRunning);

            await diagnostics.Received(1).DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), false, Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            vm.VerifyLogin = true;
        }
    }

    [TestMethod]
    public async Task ProblemsOnly_KeepsTheRowsThatNeedAttention_AndTheReportListsEveryRow()
    {
        IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
        diagnostics.DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                   .Returns(call => call.Arg<SessionProfile>().Name switch
                   {
                       "ok" => Report(DiagnosticStepStatus.Success),
                       "nocreds" => Report(DiagnosticStepStatus.Warning),
                       _ => Report(DiagnosticStepStatus.Failed, issue: "认证失败"),
                   });
        var vm = new SessionHealthCheckViewModel(diagnostics);
        vm.TryStart([Ssh("ok"), Ssh("nocreds"), Ssh("broken")]);
        await WaitUntilAsync(() => !vm.IsRunning);

        await vm.ToggleProblemsOnlyCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(["nocreds", "broken"], [.. vm.VisibleRows.Select(r => r.Name)]);
        Assert.AreEqual(Strings.Format("Health_Summary", 3, 1, 1, 1), vm.Summary);
        string report = vm.BuildReportText();
        Assert.Contains("ok\t", report);
        Assert.Contains("broken\t", report);
        Assert.Contains(Strings.Get("Health_StatusFailed"), report);
        Assert.HasCount(4, report.TrimEnd().Split('\n'), "表头 + 每台一行(过滤只管显示)。");
    }

    /// <summary>编辑过配置之后重查那一台:先从库里取最新的那份,只查它一台。</summary>
    [TestMethod]
    public async Task Recheck_ReloadsTheProfileFromTheStore_AndChecksOnlyThatRow()
    {
        IConnectionDiagnosticsService diagnostics = Passing();
        SessionProfile web = Ssh("web");
        SessionProfile db = Ssh("db");
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        SessionProfile edited = web.Clone();
        edited.Name = "web (改过)";
        repository.GetSessionAsync(web.Id).Returns(edited);
        repository.GetSessionAsync(db.Id).Returns(db);
        var vm = new SessionHealthCheckViewModel(diagnostics, repository);
        vm.TryStart([web, db]);
        await WaitUntilAsync(() => !vm.IsRunning);
        diagnostics.ClearReceivedCalls();

        await vm.RecheckAsync(web.Id);

        Assert.AreEqual("web (改过)", vm.Rows[0].Name);
        await diagnostics.Received(1).DiagnoseUnattendedAsync(edited, Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
        await diagnostics.DidNotReceive().DiagnoseUnattendedAsync(db, Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 「重新巡检」每一轮先从库里取最新的配置(行还是那几行):在这一行的连接诊断里一键放开了算法、在别处改了配置,
    /// 下一轮查的就是改过的那份 —— 曾经一直拿开始巡检时交进来的老对象去查,同一处又失败一次。库里删掉了的从表上拿掉。
    /// </summary>
    [TestMethod]
    public async Task Rerun_ReloadsEveryProfileFromTheStore_AndDropsDeletedOnes()
    {
        IConnectionDiagnosticsService diagnostics = Passing();
        SessionProfile web = Ssh("web");
        SessionProfile gone = Ssh("gone");
        SessionProfile stored = web.Clone();
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetSessionAsync(web.Id).Returns(_ => stored.Clone());
        repository.GetSessionAsync(gone.Id).Returns(gone);
        var vm = new SessionHealthCheckViewModel(diagnostics, repository);
        vm.TryStart([web, gone]);
        await WaitUntilAsync(() => !vm.IsRunning);
        SessionHealthRowViewModel row = vm.Rows[0];

        stored.Ssh = new SshSessionOptions { KexAlgorithms = "+diffie-hellman-group14-sha1" };
        repository.GetSessionAsync(gone.Id).Returns(Task.FromResult<SessionProfile?>(null));
        diagnostics.ClearReceivedCalls();

        await vm.RerunCommand.Execute().FirstAsync();

        Assert.AreSame(row, vm.Rows.Single(), "行还是那一行;删掉了的那条拿掉。");
        Assert.AreEqual("+diffie-hellman-group14-sha1", row.Profile.Ssh?.KexAlgorithms);
        await diagnostics.Received(1).DiagnoseUnattendedAsync(
            Arg.Is<SessionProfile>(p => p.Id == web.Id && p.Ssh != null && p.Ssh.KexAlgorithms == "+diffie-hellman-group14-sha1"),
            Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
        await diagnostics.DidNotReceive().DiagnoseUnattendedAsync(
            Arg.Is<SessionProfile>(p => p.Id == gone.Id), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(Strings.Format("Health_Summary", 1, 1, 0, 0), vm.Summary);
    }

    [TestMethod]
    public async Task RowActions_RaiseDiagnoseAndEditForThatProfile()
    {
        var vm = new SessionHealthCheckViewModel(Passing());
        SessionProfile web = Ssh("web");
        vm.TryStart([web]);
        await WaitUntilAsync(() => !vm.IsRunning);
        SessionProfile? diagnosed = null;
        SessionProfile? edited = null;
        vm.DiagnoseRequested += p => diagnosed = p;
        vm.EditRequested += p => edited = p;

        await vm.DiagnoseCommand.Execute(vm.Rows[0]).FirstAsync();
        await vm.EditCommand.Execute(vm.Rows[0]).FirstAsync();

        Assert.AreSame(web, diagnosed);
        Assert.AreSame(web, edited);
    }

    // ---- 入口 ----

    [TestMethod]
    public async Task TreeMenus_RaiseTheHealthCheckRequest_ForTheGroupAndTheSelection()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        var group = new ServerGroup { Id = Guid.NewGuid(), Name = "web", SortOrder = 0 };
        List<SessionProfile> sessions =
        [
            new() { Id = Guid.NewGuid(), Name = "web-1", Host = "a", GroupId = group.Id },
            new() { Id = Guid.NewGuid(), Name = "web-2", Host = "b", GroupId = group.Id },
            new() { Id = Guid.NewGuid(), Name = "db", Host = "c" }
        ];
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(sessions));
        var tree = new SessionTreeViewModel(repository);
        await tree.LoadCommand.Execute().FirstAsync();
        List<IReadOnlyList<SessionProfile>> requests = [];
        tree.HealthCheckRequested += requests.Add;

        tree.SelectedNode = tree.Nodes.Single(static n => n.IsGroup);
        await tree.HealthCheckGroupCommand.Execute().FirstAsync();
        tree.SelectedNode = tree.Rows.Single(static r => r.Name == "web-2");
        tree.ToggleMultiSelection(tree.Rows.Single(static r => r.Name == "db"));
        await tree.HealthCheckSelectedCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(["web-1", "web-2"], [.. requests[0].Select(static p => p.Name)], "整组");
        Assert.AreSequenceEqual(["web-2", "db"], [.. requests[1].Select(static p => p.Name)], "多选,按树上的顺序");
        Assert.AreEqual(Strings.Format("Tree_HealthCheckSelected", 2), tree.HealthCheckSelectedText);
    }

    [TestMethod]
    public void CommandPalette_HealthChecksEverySavedConnection()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { Ssh("b-host"), Ssh("a-host") }));
        var vm = new MainWindowViewModel(sessionRepository: repository);
        IReadOnlyList<SessionProfile>? requested = null;
        vm.HealthCheckRequested += profiles => requested = profiles;

        Assert.IsTrue(vm.Commands.Execute("tools.healthCheck"));
        SpinWait.SpinUntil(() => requested is not null, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(requested);
        Assert.AreSequenceEqual(["a-host", "b-host"], [.. requested.Select(p => p.Name)]);
    }

    // ---- 辅助 ----

    private static SessionProfile Ssh(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };

    private static IConnectionDiagnosticsService Passing()
    {
        IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
        diagnostics.DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                   .Returns(_ => Report(DiagnosticStepStatus.Success));
        return diagnostics;
    }

    private static DiagnosticReport Report(DiagnosticStepStatus auth, string? authDetail = null, Exception? failure = null, string? issue = null) => new()
    {
        Steps =
        [
            new(0, "DNS", DiagnosticStepStatus.Success),
            new(1, "TCP", DiagnosticStepStatus.Success),
            new(2, "SSH", DiagnosticStepStatus.Success),
            new(3, "认证", auth, authDetail ?? failure?.Message),
        ],
        IssueTitle = issue,
        Failure = failure,
    };

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(condition(), "等了 5 秒条件仍不成立。");
    }
}
