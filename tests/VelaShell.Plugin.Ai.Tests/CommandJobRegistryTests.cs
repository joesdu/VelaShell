using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class CommandJobRegistryTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledExec : IRemoteExecApi
    {
        public Func<string, IProgress<ExecOutput>, CancellationToken, Task<ExecStreamResult>> Handler { get; set; }
            = (_, _, _) => Task.FromResult(new ExecStreamResult(0, 0));
        public Func<CancellationToken, Task<ExecResult>> RunHandler { get; set; }
            = _ => Task.FromResult(new ExecResult("stdout") { Error = "stderr", ExitCode = 7 });
        public int Calls;
        public int SudoCalls;
        public Func<IProgress<SudoExecEvent>, CancellationToken, Task<SudoExecResult>> SudoHandler { get; set; }
            = (_, _) => Task.FromResult(new SudoExecResult(SudoExecOutcome.Exited, 0));
        public Task<SudoExecResult> StreamSudoAsync(string sessionId, string command, SudoExecOptions? options,
            IProgress<SudoExecEvent> progress, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SudoCalls);
            return SudoHandler(progress, cancellationToken);
        }
        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
            CancellationToken cancellationToken = default) => RunHandler(cancellationToken);
        public Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
            IProgress<ExecOutput> output, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Handler(command, output, cancellationToken);
        }
    }

    [TestMethod]
    public async Task FinalResult_RetainsStderrAndNonzeroExit()
    {
        var exec = new ControlledExec
        {
            Handler = (_, output, _) =>
        {
            output.Report(new(ExecStream.StandardOutput, "normal"));
            output.Report(new(ExecStream.StandardError, "bad"));
            return Task.FromResult(new ExecStreamResult(23, 2));
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string id = registry.Start(scope, "session", "cmd");
        CommandJobSnapshot result = await registry.WaitAsync(scope, id, TimeSpan.Zero);
        Assert.AreEqual(CommandJobState.Failed, result.State);
        Assert.AreEqual(23, result.ExitCode);
        Assert.AreEqual("stderr", result.Output[1].Stream);
        Assert.AreEqual("bad", result.Output[1].Text);
        Assert.IsTrue(result.RemoteStopConfirmed);
    }

    [TestMethod]
    public async Task ShortWait_DoesNotCancel_AndCompletionNotificationIsOnce()
    {
        TaskCompletionSource finish = Signal();
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
        {
            await finish.Task.WaitAsync(token);
            return new(0, 0);
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string id = registry.Start(scope, "session", "long foreground");
        CommandJobSnapshot running = await registry.WaitAsync(scope, id, TimeSpan.Zero);
        Assert.AreEqual(CommandJobState.Running, running.State);
        Assert.IsFalse(running.CancellationRequested);
        registry.MarkBackground(scope, id);
        Assert.IsTrue(scope.HasActiveJobs);
        Task<IReadOnlyList<CommandJobSnapshot>> pending = scope.WaitForCompletionAsync();
        Assert.IsFalse(pending.IsCompleted);
        finish.SetResult();
        Assert.AreEqual(CommandJobState.Succeeded, (await pending.WaitAsync(TimeSpan.FromSeconds(5)))[0].State);
        Assert.IsFalse(scope.HasActiveJobs);
        Assert.IsEmpty(await scope.WaitForCompletionAsync());
    }

    /// <summary>模型已经用 task_wait / task_status 拿到终态,就不该再收一条完成通知、多请求一次模型。</summary>
    [TestMethod]
    public async Task TerminalStateReadByTheModel_IsNotNotifiedAgain()
    {
        TaskCompletionSource finish = Signal();
        var exec = new ControlledExec
        {
            Handler = async (command, _, token) =>
            {
                if (command == "waited") await finish.Task.WaitAsync(token);
                return new(0, 0);
            }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string waited = registry.Start(scope, "s", "waited");
        string read = registry.Start(scope, "s", "read");
        registry.MarkBackground(scope, waited);
        registry.MarkBackground(scope, read);
        Assert.IsFalse((await registry.ReadAsync(scope, waited)).IsTerminal);
        Assert.IsTrue((await registry.ReadAsync(scope, read)).IsTerminal);
        Assert.IsTrue(scope.HasActiveJobs, "非终态的查询不算交付");
        finish.SetResult();
        Assert.IsTrue((await registry.WaitAsync(scope, waited, TimeSpan.FromSeconds(5))).IsTerminal);
        Assert.IsFalse(scope.HasActiveJobs);
        Assert.IsEmpty(await scope.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Notifications_ReturnFirstCompletedWithoutWaitingForOthers()
    {
        TaskCompletionSource first = Signal();
        TaskCompletionSource second = Signal();
        var exec = new ControlledExec
        {
            Handler = async (command, _, token) =>
        {
            await (command == "first" ? first : second).Task.WaitAsync(token);
            return new(0, 0);
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string a = registry.Start(scope, "s", "first");
        string b = registry.Start(scope, "s", "second");
        registry.MarkBackground(scope, a);
        registry.MarkBackground(scope, b);
        Task<IReadOnlyList<CommandJobSnapshot>> pending = scope.WaitForCompletionAsync();
        first.SetResult();
        IReadOnlyList<CommandJobSnapshot> completed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, completed);
        Assert.AreEqual(a, completed[0].TaskId);
        Assert.IsTrue(scope.HasActiveJobs);
        second.SetResult();
        Assert.AreEqual(b, (await scope.WaitForCompletionAsync())[0].TaskId);
    }

    [TestMethod]
    public async Task Output_IsBoundedAndCursorsExposeExpiredHistory()
    {
        var exec = new ControlledExec
        {
            Handler = (_, output, _) =>
        {
            for (int i = 0; i < 20; i++)
            {
                output.Report(new(ExecStream.StandardOutput, new string('x', 10000)));
            }
            output.Report(new(ExecStream.StandardError, "progress\r100%"));
            return Task.FromResult(new ExecStreamResult(0, 21));
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string id = registry.Start(scope, "s", "cmd");
        CommandJobSnapshot result = await registry.ReadAsync(scope, id);
        Assert.IsTrue(result.OutputTruncated);
        Assert.IsGreaterThan(0, result.EarliestCursor);
        Assert.IsLessThanOrEqualTo(32768, result.Output.Sum(x => x.Text.Length));
        Assert.IsTrue(result.Output.All(x => x.Text.Length <= 4096));
        Assert.AreEqual(61L, result.NextCursor, "每条一万字拆成 4096+4096+1808 三条,不丢后半段");
        Assert.AreEqual("progress\r100%", result.Output[^1].Text);
        Assert.IsEmpty((await registry.ReadAsync(scope, id, result.NextCursor)).Output);
    }

    /// <summary>
    /// 宿主按最长 8192 字节上报提权输出,普通流超长行切片后再补 23 字的续片标记(共 4119 字)。
    /// 单条只留前 4096 字时,前者丢掉后半段,后者丢掉标记,两者还都被误报成截断。
    /// </summary>
    [TestMethod]
    public async Task OversizeEntries_AreSplitInOrder_NotTruncated()
    {
        string chunk = string.Concat(Enumerable.Range(0, 8192).Select(i => (char)('a' + i % 26)));
        string line = new string('x', 4096) + " [VelaShell: continued]";
        var exec = new ControlledExec
        {
            Handler = (_, output, _) =>
            {
                output.Report(new(ExecStream.StandardOutput, line));
                return Task.FromResult(new ExecStreamResult(0, 1));
            },
            SudoHandler = (output, _) =>
            {
                output.Report(new(SudoExecEventKind.State, SudoExecState.Running));
                output.Report(new(SudoExecEventKind.Output, Text: chunk));
                return Task.FromResult(new SudoExecResult(SudoExecOutcome.Exited, 0, 1));
            }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        foreach ((string id, string expected) in new[] { (registry.StartSudo(scope, "s", "cmd"), chunk), (registry.Start(scope, "s", "cmd"), line) })
        {
            CommandJobSnapshot result = await registry.WaitAsync(scope, id, TimeSpan.FromSeconds(5));
            Assert.AreEqual(expected, string.Concat(result.Output.Select(o => o.Text)));
            Assert.IsTrue(result.Output.All(o => o.Text.Length <= 4096));
            Assert.IsFalse(result.OutputTruncated);
        }
    }

    [TestMethod]
    public async Task OwnersAndRuns_CannotReadOrCancelEachOthersTaskIds()
    {
        await using var registry = new CommandJobRegistry(new ControlledExec());
        await using CommandJobRunScope a = registry.BeginRun("owner", "a");
        await using CommandJobRunScope b = registry.BeginRun("other", "b");
        await using CommandJobRunScope c = registry.BeginRun("owner", "c");
        string id = registry.Start(a, "s", "cmd");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.ReadAsync(b, id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.ReadAsync(c, id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.CancelAsync(b, id));
    }

    [TestMethod]
    public async Task FourSlots_AreSharedAcrossRegistries_QueuedCancelledNeverStarts()
    {
        TaskCompletionSource fourStarted = Signal();
        int active = 0;
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref active) == 4) fourStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, 0);
        }
        };
        object contextKey = new();
        await using var first = new CommandJobRegistry(exec, contextKey);
        await using var second = new CommandJobRegistry(exec, contextKey);
        await using CommandJobRunScope a = first.BeginRun("owner", "a");
        await using CommandJobRunScope b = second.BeginRun("owner", "b");
        for (int i = 0; i < 4; i++) first.Start(a, "s", "cmd");
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string queued = second.Start(b, "s", "never starts");
        Assert.AreEqual(CommandJobState.Starting, (await second.ReadAsync(b, queued)).State);
        await second.CancelAsync(b, queued);
        CommandJobSnapshot cancelled = await second.WaitAsync(b, queued, TimeSpan.FromSeconds(5));
        Assert.AreEqual(CommandJobState.Cancelled, cancelled.State);
        Assert.IsFalse(b.HasUnknownOutcome, "已证实未启动的取消不能冒充远端结果未知");
        Assert.IsTrue(cancelled.RemoteStopConfirmed);
        Assert.AreEqual(4, exec.Calls);
        await a.CancelAsync();
        Assert.AreEqual(4, exec.Calls);
    }

    [TestMethod]
    public async Task RevocationWhileQueued_PreventsExecutionAndWithholdsOutput()
    {
        bool allowed = true;
        TaskCompletionSource fourStarted = Signal();
        int started = 0;
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref started) == 4) fourStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, 0);
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope blockers = registry.BeginRun("blockers", "run");
        await using CommandJobRunScope denied = registry.BeginRun("denied", "run", authorize: (_, _) => Task.FromResult(allowed));
        for (int i = 0; i < 4; i++) registry.Start(blockers, "s", "blocker");
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string id = registry.Start(denied, "s", "queued");
        registry.MarkBackground(denied, id);
        allowed = false;
        await blockers.CancelAsync();
        IReadOnlyList<CommandJobSnapshot> withheld = await denied.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, withheld, "撤权的任务也要交出一条通知,否则续接会把空表当成全部完成");
        Assert.AreEqual(CommandJobState.Unknown, withheld[0].State);
        Assert.IsEmpty(withheld[0].Output);
        Assert.AreEqual("", withheld[0].SessionId);
        Assert.IsFalse(denied.HasActiveJobs);
        Assert.AreEqual(4, exec.Calls);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.ReadAsync(denied, id));
        await registry.CancelAsync(denied, id);
    }

    [TestMethod]
    public async Task Disconnect_IsUnknown_AndDisposeAwaitsCancellationCleanup()
    {
        var exec = new ControlledExec { Handler = (_, _, _) => throw new IOException("connection lost") };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        CommandJobSnapshot unknown = await registry.ReadAsync(scope, registry.Start(scope, "s", "cmd"));
        Assert.AreEqual(CommandJobState.Unknown, unknown.State);
        Assert.IsTrue(scope.HasUnknownOutcome, "短等内即时Unknown也必须阻止本轮自动推进,不依赖Background标记");
        Assert.IsNull(unknown.ExitCode);
        Assert.IsFalse(unknown.RemoteStopConfirmed);
        TaskCompletionSource cleanup = Signal();
        exec.Handler = async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleanup.SetResult(); }
            return new(0, 0);
        };
        registry.Start(scope, "s", "long");
        await scope.DisposeAsync();
        Assert.IsTrue(cleanup.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task ExecutionTimeout_IsDistinctFromWaitExpiry()
    {
        var exec = new ControlledExec { Handler = (_, _, _) => throw new TimeoutException() };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        CommandJobSnapshot result = await registry.ReadAsync(scope, registry.Start(scope, "s", "cmd", TimeSpan.FromSeconds(1)));
        Assert.AreEqual(CommandJobState.TimedOut, result.State);
        Assert.IsTrue(scope.HasUnknownOutcome, "执行超时但未确认远端停止时禁止模型自动重放");
        Assert.IsNull(result.ExitCode);
        Assert.IsFalse(result.RemoteStopConfirmed);
    }

    [TestMethod]
    public async Task Monitoring_DoesNotCauseAutomaticModelWake()
    {
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, 0);
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string id = registry.Start(scope, "s", "tail -f", monitoring: true);
        registry.MarkBackground(scope, id);
        Assert.IsFalse(scope.HasActiveJobs);
        Assert.IsTrue(scope.HasMonitoringJobs);
        Assert.IsEmpty(await scope.WaitForCompletionAsync());
        await scope.CancelAsync();
        CommandJobSnapshot result = await registry.ReadAsync(scope, id);
        Assert.IsTrue(result.CancellationRequested);
        Assert.IsFalse(result.RemoteStopConfirmed);
        Assert.AreEqual(CommandJobState.Unknown, result.State);
        Assert.IsFalse(scope.HasMonitoringJobs);
    }

    [TestMethod]
    public async Task MonitoringSelfExit_CompletesLocalWaitWithoutModelNotification()
    {
        TaskCompletionSource finish = Signal();
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
        {
            await finish.Task.WaitAsync(token);
            return new(0, 0);
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string id = registry.Start(scope, "s", "monitor", monitoring: true);
        registry.MarkBackground(scope, id);
        Task waiting = scope.WaitForMonitoringEndAsync();
        Assert.IsFalse(waiting.IsCompleted);
        finish.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(scope.HasActiveJobs);
        Assert.IsFalse(scope.HasMonitoringJobs);
    }

    /// <summary>
    /// 点名取消 tail -f 是结束监控的正常做法;提权命令还停在审批/认证里就取消,用户命令根本没启动。
    /// 两者以前都记成 Unknown,整轮随之暂停。已经启动的普通命令被取消仍是 Unknown。
    /// </summary>
    [TestMethod]
    public async Task DeliberateMonitoringStop_AndSudoCancelledBeforeStart_AreNotUnknown()
    {
        TaskCompletionSource awaitingUser = Signal();
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(0, 0);
            },
            SudoHandler = async (output, token) =>
            {
                output.Report(new(SudoExecEventKind.State, SudoExecState.AwaitingAuthentication));
                awaitingUser.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new SudoExecResult(SudoExecOutcome.Exited, 0);
            }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string monitor = registry.Start(scope, "s", "tail -f /var/log/syslog", monitoring: true);
        string sudo = registry.StartSudo(scope, "s", "apt upgrade");
        await awaitingUser.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (string id in new[] { monitor, sudo })
        {
            await registry.CancelAsync(scope, id);
            CommandJobSnapshot result = await registry.WaitAsync(scope, id, TimeSpan.FromSeconds(5));
            Assert.AreEqual(CommandJobState.Cancelled, result.State);
            Assert.IsFalse(result.RemoteStopConfirmed, "通道已经开过,不能声称远端确认停止");
        }
        Assert.IsFalse(scope.HasUnknownOutcome);
        string running = registry.Start(scope, "s", "apt upgrade");
        await registry.CancelAsync(scope, running);
        Assert.AreEqual(CommandJobState.Unknown, (await registry.WaitAsync(scope, running, TimeSpan.FromSeconds(5))).State);
        Assert.IsTrue(scope.HasUnknownOutcome);
    }

    /// <summary>
    /// 已收尾且终态已交付的任务在额度满时回收,长寿的轮次(MCP 一条连接一个)不会跑满 128 条就停摆;
    /// 承载 Unknown 停止边界的任务不回收。
    /// </summary>
    [TestMethod]
    public async Task DeliveredJobs_AreReclaimedAtTheCap_ButUnknownOutcomesStay()
    {
        var exec = new ControlledExec
        {
            Handler = (command, _, _) => command == "lost"
                ? Task.FromException<ExecStreamResult>(new IOException("connection lost"))
                : Task.FromResult(new ExecStreamResult(0, 0))
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string lost = registry.Start(scope, "s", "lost");
        Assert.AreEqual(CommandJobState.Unknown, (await registry.WaitAsync(scope, lost, TimeSpan.FromSeconds(5))).State);
        for (int i = 0; i < 300; i++)
        {
            string id = registry.Start(scope, "s", "ok");
            Assert.AreEqual(CommandJobState.Succeeded, (await registry.WaitAsync(scope, id, TimeSpan.FromSeconds(5))).State);
        }
        Assert.IsTrue(scope.HasUnknownOutcome);
        Assert.AreEqual(CommandJobState.Unknown, (await registry.ReadAsync(scope, lost)).State);
        await using CommandJobRunScope undelivered = registry.BeginRun("owner", "undelivered");
        for (int i = 0; i < 128; i++) registry.Start(undelivered, "s", "ok");
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Start(undelivered, "s", "ok"),
            "没交付过终态的任务不回收,模型还可能来查");
    }

    [TestMethod]
    public async Task RegistryLifetime_CancelsFallbackAndExplicitRuns()
    {
        using var lifetime = new CancellationTokenSource();
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, 0);
        }
        };
        await using var registry = new CommandJobRegistry(exec, lifetimeToken: lifetime.Token);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        string id = registry.Start(scope, "s", "cmd");
        lifetime.Cancel();
        CommandJobSnapshot result = await registry.WaitAsync(scope, id, TimeSpan.FromSeconds(5));
        Assert.AreEqual(CommandJobState.Unknown, result.State);
        Assert.IsTrue(result.CancellationRequested);
        Assert.IsFalse(result.RemoteStopConfirmed);
    }

    [TestMethod]
    public async Task Toolbox_ProbePreservesFullResult_AndCancellationPropagates()
    {
        var exec = new ControlledExec();
        using var context = new TestPluginContext { RemoteExec = exec };
        string session = context.FakeSessions.AddConnected().SessionId;
        await using var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session };
        AIFunction function = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == "system_overview");
        string result = (await function.InvokeAsync([], CancellationToken.None))!.ToString()!;
        using var json = JsonDocument.Parse(result);
        Assert.AreEqual("stdout", json.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual("stderr", json.RootElement.GetProperty("stderr").GetString());
        Assert.AreEqual(7, json.RootElement.GetProperty("exitCode").GetInt32());
        using var cancel = new CancellationTokenSource();
        TaskCompletionSource started = Signal();
        exec.RunHandler = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new("");
        };
        Task<object?> pending = function.InvokeAsync([], cancel.Token).AsTask();
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [TestMethod]
    public async Task Toolbox_ApprovalRevocationPreventsStart_AndScopeIsCapturedPerToolList()
    {
        var exec = new ControlledExec();
        using var context = new TestPluginContext { RemoteExec = exec };
        string session = context.FakeSessions.AddConnected().SessionId;
        bool allowed = true;
        var sessionScope = new DelegateScope(() => allowed);
        TaskCompletionSource approve = Signal();
        TaskCompletionSource requested = Signal();
        await using var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session,
            Scope = sessionScope,
            ApprovalHandler = async _ => { requested.SetResult(); await approve.Task; return true; }
        };
        await using CommandJobRunScope scope = toolbox.BeginRun("owner", "run");
        AIFunction function = toolbox.CreateTools(ChatMode.Agent, runScope: scope).OfType<AIFunction>().Single(f => f.Name == "run_command");
        Task<object?> pending = function.InvokeAsync(new AIFunctionArguments { ["command"] = "deploy" }, CancellationToken.None).AsTask();
        await requested.Task;
        allowed = false;
        approve.SetResult();
        await pending;
        Assert.AreEqual(0, exec.Calls);
    }

    [TestMethod]
    public async Task Toolbox_BatchOfSixteenQueuesAndRunStopCancelsUnstartedItems()
    {
        var exec = new ControlledExec
        {
            Handler = async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, 0);
        }
        };
        using var context = new TestPluginContext { RemoteExec = exec };
        string[] sessions = [.. Enumerable.Range(0, 16).Select(i => context.FakeSessions.AddConnected($"host-{i}").SessionId)];
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope scope = toolbox.BeginRun("owner", "run");
        AIFunction function = toolbox.CreateTools(ChatMode.Agent, runScope: scope).OfType<AIFunction>().Single(f => f.Name == "run_on_sessions");
        string result = (await function.InvokeAsync(new AIFunctionArguments
        {
            ["sessionIds"] = sessions,
            ["command"] = "foreground",
            ["waitSeconds"] = 0
        }, CancellationToken.None))!.ToString()!;
        Assert.AreEqual(4, exec.Calls);
        Assert.AreEqual(16, result.Split("taskId").Length - 1);
        await scope.CancelAsync();
        Assert.AreEqual(4, exec.Calls);
    }

    [TestMethod]
    public async Task Toolbox_TaskToolsCaptureOwnerAndCancelDoesNotLeakRevokedMetadata()
    {
        var exec = new ControlledExec
        {
            Handler = async (_, output, token) =>
        {
            output.Report(new(ExecStream.StandardError, "private error"));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, 1);
        }
        };
        bool allowed = true;
        using var context = new TestPluginContext { RemoteExec = exec };
        string session = context.FakeSessions.AddConnected().SessionId;
        await using var toolbox = new AgentToolbox(context)
        {
            Approval = ApprovalMode.Bypass,
            SessionIdProvider = () => session,
            Scope = new DelegateScope(() => allowed)
        };
        await using CommandJobRunScope a = toolbox.BeginRun("chat-a", "run");
        await using CommandJobRunScope b = toolbox.BeginRun("chat-b", "run");
        AIFunction[] toolsA = [.. toolbox.CreateTools(ChatMode.Agent, runScope: a).OfType<AIFunction>()];
        AIFunction[] toolsB = [.. toolbox.CreateTools(ChatMode.Agent, runScope: b).OfType<AIFunction>()];
        string running = (await toolsA.Single(f => f.Name == "run_command").InvokeAsync(new AIFunctionArguments
        {
            ["command"] = "foreground",
            ["waitSeconds"] = 0
        }, CancellationToken.None))!.ToString()!;
        using var json = JsonDocument.Parse(running);
        string id = json.RootElement.GetProperty("taskId").GetString()!;
        var args = new AIFunctionArguments { ["taskId"] = id };
        string foreign = (await toolsB.Single(f => f.Name == "task_status").InvokeAsync(args, CancellationToken.None))!.ToString()!;
        Assert.DoesNotContain("private error", foreign);
        Assert.Contains("No command task", foreign);
        allowed = false;
        string revoked = (await toolsA.Single(f => f.Name == "task_status").InvokeAsync(args, CancellationToken.None))!.ToString()!;
        Assert.DoesNotContain("private error", revoked);
        string cancel = (await toolsA.Single(f => f.Name == "task_cancel").InvokeAsync(args, CancellationToken.None))!.ToString()!;
        Assert.DoesNotContain(session, cancel);
        Assert.DoesNotContain("private error", cancel);
        Assert.DoesNotContain("exitCode", cancel);
        Assert.Contains("cancellationRequested", cancel);
    }

    [TestMethod]
    public async Task Toolbox_RunStopCancelsApprovalWaitWithoutStartingCommand()
    {
        var exec = new ControlledExec();
        using var context = new TestPluginContext { RemoteExec = exec };
        string session = context.FakeSessions.AddConnected().SessionId;
        TaskCompletionSource asked = Signal();
        TaskCompletionSource unresolvedApproval = Signal();
        await using var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session,
            ApprovalHandler = async _ => { asked.SetResult(); await unresolvedApproval.Task; return true; }
        };
        await using CommandJobRunScope scope = toolbox.BeginRun("owner", "run");
        AIFunction function = toolbox.CreateTools(ChatMode.Agent, runScope: scope).OfType<AIFunction>().Single(f => f.Name == "run_command");
        Task<object?> pending = function.InvokeAsync(new AIFunctionArguments { ["command"] = "deploy" }, CancellationToken.None).AsTask();
        await asked.Task;
        await scope.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, exec.Calls);
        unresolvedApproval.SetResult();
    }

    [TestMethod]
    public async Task Toolbox_ApprovalDenialBlocksLaterToolsInTheSameRun()
    {
        var exec = new ControlledExec();
        using var context = new TestPluginContext { RemoteExec = exec };
        string session = context.FakeSessions.AddConnected().SessionId;
        int approvals = 0;
        await using var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session,
            ApprovalHandler = _ => { approvals++; return Task.FromResult(false); }
        };
        await using CommandJobRunScope scope = toolbox.BeginRun("owner", "run");
        AIFunction command = toolbox.CreateTools(ChatMode.Agent, runScope: scope).OfType<AIFunction>().Single(f => f.Name == "run_command");
        await command.InvokeAsync(new AIFunctionArguments { ["command"] = "deploy" }, CancellationToken.None);
        Assert.IsTrue(scope.HasDeniedExecution);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await command.InvokeAsync(new AIFunctionArguments { ["command"] = "another change" }, CancellationToken.None));
        Assert.AreEqual(1, approvals);
        Assert.AreEqual(0, exec.Calls);
    }

    [TestMethod]
    [DataRow(ApprovalMode.Ask, false, false)]
    [DataRow(ApprovalMode.ReadOnlyAuto, false, false)]
    [DataRow(ApprovalMode.Bypass, false, true)]
    [DataRow(ApprovalMode.Ask, true, true)]
    [DataRow(ApprovalMode.ReadOnlyAuto, true, true)]
    public async Task Elevated_ApprovalIsExplicitNoRepeatKey_ExceptPluginBypass(
        ApprovalMode mode, bool handlerAvailable, bool executed)
    {
        var exec = new ControlledExec();
        using var context = new TestPluginContext { RemoteExec = exec };
        string session = context.FakeSessions.AddConnected().SessionId;
        ApprovalRequest? request = null;
        await using var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session,
            Approval = mode,
            ApprovalHandler = handlerAvailable ? value => { request = value; return Task.FromResult(true); } : null
        };
        AIFunction command = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == "run_command");
        await command.InvokeAsync(new AIFunctionArguments
        {
            ["command"] = "cat /etc/hosts",
            ["elevated"] = true
        }, CancellationToken.None);
        Assert.AreEqual(executed ? 1 : 0, exec.SudoCalls);
        Assert.AreEqual(0, exec.Calls, "提权绝不退化普通exec");
        if (handlerAvailable)
        {
            Assert.AreEqual("run_command_elevated", request!.Value.Kind);
            Assert.IsNull(request.Value.RepeatKey);
            Assert.Contains("ENTIRE command runs as root", request.Value.Detail);
            Assert.Contains(session, request.Value.Detail);
            Assert.Contains("cat /etc/hosts", request.Value.Detail);
        }
    }

    /// <summary>
    /// 对外 MCP 那条路没有审批卡:提权交给宿主的 root 确认框(<see cref="AgentToolbox.ElevationConfirmedByHost" />),
    /// 插件这一层不再替用户拒掉。放开的<b>只有</b>提权 —— 同一个工具箱里不带 elevated 的命令照旧按挡位问、照旧被拒。
    /// </summary>
    [TestMethod]
    [DataRow(ApprovalMode.Ask)]
    [DataRow(ApprovalMode.ReadOnlyAuto)]
    public async Task Elevated_ConfirmedByHost_SkipsOnlyThePluginApproval(ApprovalMode mode)
    {
        var exec = new ControlledExec();
        using var context = new TestPluginContext { RemoteExec = exec };
        string session = context.FakeSessions.AddConnected().SessionId;
        var asked = new List<string>();
        await using var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session,
            Approval = mode,
            ElevationConfirmedByHost = true,
            ApprovalHandler = value => { asked.Add(value.Kind); return Task.FromResult(false); }
        };
        AIFunction command = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == "run_command");

        string elevated = (await command.InvokeAsync(new AIFunctionArguments
        {
            ["command"] = "systemctl restart nginx",
            ["elevated"] = true
        }, CancellationToken.None))?.ToString() ?? "";
        string plain = (await command.InvokeAsync(new AIFunctionArguments
        {
            ["command"] = "systemctl restart nginx"
        }, CancellationToken.None))?.ToString() ?? "";

        Assert.DoesNotContain("DENIED", elevated);
        Assert.AreEqual(1, exec.SudoCalls, "提权直接交给宿主的确认框");
        Assert.Contains("DENIED", plain);
        Assert.AreEqual(0, exec.Calls, "普通写命令照旧要插件审批");
        Assert.AreSequenceEqual(["run_command"], asked, "插件只为普通命令问了一次,没替提权弹卡");
    }

    [TestMethod]
    [DataRow(SudoExecOutcome.Exited, 0, CommandJobState.Succeeded, false)]
    [DataRow(SudoExecOutcome.Exited, 13, CommandJobState.Failed, false)]
    [DataRow(SudoExecOutcome.Exited, null, CommandJobState.Unknown, true)]
    [DataRow(SudoExecOutcome.Unknown, null, CommandJobState.Unknown, true)]
    [DataRow(SudoExecOutcome.AuthenticationCancelled, null, CommandJobState.Failed, false)]
    [DataRow(SudoExecOutcome.AuthenticationFailed, null, CommandJobState.Failed, false)]
    [DataRow(SudoExecOutcome.NotSupported, null, CommandJobState.Failed, false)]
    public async Task Sudo_OutcomeMappingNeverInventsSuccess(SudoExecOutcome outcome, int? exit,
        CommandJobState expected, bool uncertain)
    {
        var exec = new ControlledExec
        {
            SudoHandler = (output, _) =>
        {
            output.Report(new(SudoExecEventKind.State, SudoExecState.AwaitingAuthentication));
            output.Report(new(SudoExecEventKind.Output, Text: "partial\nnext\rprogress"));
            return Task.FromResult(new SudoExecResult(outcome, exit, 1));
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        CommandJobSnapshot result = await registry.ReadAsync(scope, registry.StartSudo(scope, "s", "actual"));
        Assert.AreEqual(expected, result.State);
        Assert.AreEqual(uncertain, scope.HasUnknownOutcome);
        Assert.AreEqual(outcome == SudoExecOutcome.AuthenticationCancelled, scope.HasDeniedExecution);
        Assert.HasCount(1, result.Output, "认证状态事件不得冒充输出");
        Assert.AreEqual("combined", result.Output[0].Stream);
        Assert.AreEqual("partial\nnext\rprogress", result.Output[0].Text);
        Assert.AreEqual(0, exec.Calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sudo_TimeoutGuardOnlyAfterCommandStarted(bool commandStarted)
    {
        var exec = new ControlledExec
        {
            SudoHandler = (output, _) =>
        {
            output.Report(new(SudoExecEventKind.State, commandStarted ? SudoExecState.Running : SudoExecState.AwaitingAuthentication));
            throw new TimeoutException();
        }
        };
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        CommandJobSnapshot result = await registry.ReadAsync(scope, registry.StartSudo(scope, "s", "actual"));
        Assert.AreEqual(CommandJobState.TimedOut, result.State);
        Assert.AreEqual(commandStarted, scope.HasUnknownOutcome);
        Assert.AreEqual(0, exec.Calls);
    }

    private sealed class DelegateScope(Func<bool> allowed) : ISessionScope
    {
        public string Describe() => "test scope";
        public Task<bool> AllowsLiveAsync(PluginSdk.Sessions.SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(allowed());
        public Task<bool> AllowsSavedAsync(PluginSdk.Sessions.SavedSessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(allowed());
    }
}
