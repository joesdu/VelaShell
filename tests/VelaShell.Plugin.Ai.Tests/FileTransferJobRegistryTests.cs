using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class FileTransferJobRegistryTests
{
    private static RemoteTransferSnapshot Running(string session, string source, string destination, string operation)
        => new()
        {
            TransferId = "transfer",
            OperationId = operation,
            SessionId = session,
            LocalPath = source,
            RemotePath = destination,
            State = RemoteTransferState.Running,
            Sequence = 1,
            Generation = 1,
            TotalBytes = 64L * 1024 * 1024,
            TransferredBytes = 1024,
            SpeedBytesPerSecond = 256,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(20)
        };

    private static AIFunction Tool(AgentToolbox toolbox, CommandJobRunScope run, string name)
        => toolbox.CreateTools(ChatMode.Agent, runScope: run).OfType<AIFunction>().Single(t => t.Name == name);

    private static async Task<string> Call(AgentToolbox toolbox, CommandJobRunScope run, string name,
        Dictionary<string, object?> args, CancellationToken token = default)
        => (await Tool(toolbox, run, name).InvokeAsync([with(args)], token))?.ToString() ?? "";

    private static string Source(long size)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-transfer-" + Guid.NewGuid().ToString("N"));
        using FileStream stream = File.Create(path);
        stream.SetLength(size);
        return path;
    }

    /// <summary>
    /// 仿宿主的下载启动:Windows 安全落盘按 OPEN_EXISTING 逐段钉住父目录链,不替调用方建目录 ——
    /// 父目录不存在时任务在宿主审批之前就失败。
    /// </summary>
    private static RemoteTransferSnapshot StartDownloadLikeHost(string session, string remote, string local, string operation)
    {
        RemoteTransferSnapshot started = Running(session, local, remote, operation) with
        { TransferId = "download-transfer", Direction = RemoteTransferDirection.Download };
        return Directory.Exists(Path.GetDirectoryName(local)) ? started : started with
        {
            State = RemoteTransferState.Failed,
            RemoteStopConfirmed = true,
            ErrorCode = "download-failed",
            ErrorMessage = "Cannot lock the destination directory chain."
        };
    }

    /// <summary>支持后台下载的宿主替身:远端预置一个文件,启动走 <see cref="StartDownloadLikeHost" />。</summary>
    private static string DownloadableHost(TestPluginContext context, string remotePath = "/var/log/app.log")
    {
        string session = context.FakeSessions.AddConnected().SessionId;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsDownloadOperations = true;
        context.FakeRemoteFs.AddFile(session, remotePath, new byte[64]);
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) =>
            Task.FromResult(StartDownloadLikeHost(id, remote, local, options.OperationId));
        return session;
    }

    [TestMethod]
    public async Task LargeUpload_ReturnsReceipt_AndRequestCancellationOrDisposeDoesNotCancelHost()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, token) => finish.Task.WaitAsync(token);
        var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(64L * 1024 * 1024);
        try
        {
            string receipt = await Call(toolbox, run, "upload_local_file", new()
            { ["localPath"] = local, ["remotePath"] = "/tmp/large", ["sessionId"] = session.SessionId });
            using var json = JsonDocument.Parse(receipt);
            Assert.AreEqual("transfer", json.RootElement.GetProperty("transfer_id").GetString());
            Assert.IsTrue(json.RootElement.GetProperty("stillRunning").GetBoolean());
            Assert.HasCount(1, context.FakeRemoteFs.UploadsStarted);
            string status = await Call(toolbox, run, "transfer_status", new() { ["transferId"] = "transfer" });
            Assert.Contains("speedBytesPerSecond", status);
            string timedWait = await Call(toolbox, run, "transfer_wait", new() { ["transferId"] = "transfer", ["waitSeconds"] = 0 });
            Assert.Contains("\"stillRunning\":true", timedWait);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
            using var cancel = new CancellationTokenSource();
            Task waiting = Call(toolbox, run, "transfer_wait", new() { ["transferId"] = "transfer", ["waitSeconds"] = 30 }, cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
            await run.DisposeAsync();
            await toolbox.DisposeAsync();
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
            finish.SetResult(snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 });
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task AcceptedStart_CallCancellationAndExplicitOwnerCleanup_CancelsLateReceipt()
    {
        using var context = new TestPluginContext();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken hostRequest = default;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, ct) =>
        {
            hostRequest = ct;
            accepted.TrySetResult();
            return receipt.Task.WaitAsync(ct);
        };
        context.FakeRemoteFs.CancelTransferHandler = (id, generation, _) =>
        {
            Assert.AreEqual("transfer", id);
            Assert.AreEqual(7L, generation);
            cancelled.TrySetResult();
            return Task.FromResult(Running("session", "source", "destination", "operation") with
            { State = RemoteTransferState.Cancelled, Generation = generation, Sequence = 2, RemoteStopConfirmed = true });
        };
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        using IDisposable subscription = scope.Subscribe(update =>
        { if (update.Snapshot?.State == RemoteTransferState.Cancelled) observed.TrySetResult(); });
        using var request = new CancellationTokenSource();
        Task<RemoteTransferSnapshot> start = scope.StartAsync("session", "source", "destination", "operation", false, request.Token);
        await accepted.Task;
        Assert.IsTrue(scope.HasRunningTransfers, "未取得回执的已批准启动也必须参与闲置保护");
        await request.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        Assert.IsFalse(hostRequest.IsCancellationRequested, "调用令牌只能停止等待回执，不能取消宿主请求观察");
        await scope.CancelAllAsync();
        await scope.DisposeAsync();
        receipt.SetResult(Running("session", "source", "destination", "operation") with { Generation = 7 });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.StartAsync("session", "source", "destination", "operation", false, default);
        Assert.IsFalse(scope.HasRunningTransfers);
        Assert.HasCount(1, context.FakeRemoteFs.UploadsStarted);
    }

    [TestMethod]
    public async Task Completion_IsDeliveredOnce_AndStatusConsumesTerminal()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Task<IReadOnlyList<RemoteTransferSnapshot>> completed = run.Transfers!.WaitForCompletionAsync();
            Assert.IsFalse(completed.IsCompleted);
            snapshot = snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 };
            finish.SetResult(snapshot);
            Assert.HasCount(1, await completed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsEmpty(await run.Transfers.WaitForCompletionAsync());
            Assert.IsFalse(run.Transfers.HasActiveJobs);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task TwoRuns_CannotReadEachOthersTransfer_AndLargeDownloadStartsInBackground()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsDownloadOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(
            Running(id, source, target, options.OperationId) with { State = RemoteTransferState.Succeeded });
        var downloadCompletion = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) =>
            Task.FromResult(StartDownloadLikeHost(id, remote, local, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => downloadCompletion.Task.WaitAsync(ct);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope first = toolbox.BeginRun("owner", "first");
        await using CommandJobRunScope second = toolbox.BeginRun("owner", "second");
        string local = Source(1);
        try
        {
            await Call(toolbox, first, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            await Assert.ThrowsAsync<FileNotFoundException>(() => second.Transfers!.ReadAsync("transfer"));
            Assert.IsEmpty(context.FakeRemoteFs.TransfersRead);
            context.FakeRemoteFs.AddFile(session.SessionId, "/tmp/large", new byte[33 * 1024 * 1024]);
            string download = await Call(toolbox, first, "download_remote_file", new()
            {
                ["remotePath"] = "/tmp/large",
                ["sessionId"] = session.SessionId
            });
            using var downloadJson = JsonDocument.Parse(download);
            Assert.AreEqual("Download", downloadJson.RootElement.GetProperty("direction").GetString());
            Assert.AreEqual("download-transfer", downloadJson.RootElement.GetProperty("transfer_id").GetString());
            Assert.IsTrue(downloadJson.RootElement.GetProperty("stillRunning").GetBoolean());
            Assert.HasCount(1, context.FakeRemoteFs.DownloadsStarted);
            Assert.IsFalse(context.FakeRemoteFs.DownloadsStarted[0].Options.Overwrite);
            await Assert.ThrowsAsync<FileNotFoundException>(() => second.Transfers!.ReadAsync("download-transfer"));
            Assert.IsEmpty(context.FakeRemoteFs.TransfersRead);
            downloadCompletion.SetResult(Running(session.SessionId, "", "/tmp/large", "download") with
            {
                TransferId = "download-transfer",
                Direction = RemoteTransferDirection.Download,
                State = RemoteTransferState.Succeeded,
                Sequence = 2
            });
            RemoteTransferSnapshot completed = (await first.Transfers!.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
            Assert.AreEqual(RemoteTransferDirection.Download, completed.Direction);
            Assert.IsTrue(completed.IsSuccess);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task DeniedUpload_DoesNotStart_AndUnknownNeverResumes()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(
            Running(id, source, target, options.OperationId) with { State = RemoteTransferState.Unknown });
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(Running(session.SessionId, "", "", "")
            with
        { State = RemoteTransferState.Unknown });
        await using var toolbox = new AgentToolbox(context) { ApprovalHandler = _ => Task.FromResult(false) };
        string local = Source(1);
        try
        {
            await using CommandJobRunScope denied = toolbox.BeginRun("owner", "denied");
            string result = await Call(toolbox, denied, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("DENIED", result);
            Assert.IsEmpty(context.FakeRemoteFs.UploadsStarted);
            toolbox.Approval = ApprovalMode.Bypass;
            await using CommandJobRunScope unknown = toolbox.BeginRun("owner", "unknown");
            await Call(toolbox, unknown, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.IsTrue(unknown.HasUnknownOutcome);
            string resumed = await Call(toolbox, unknown, "transfer_resume", new()
            {
                ["transferId"] = "transfer",
                ["expectedGeneration"] = 1L,
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("not confirmed", resumed);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersResumed);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task WaitFailure_FallsBackToGetRunning_ThenKeepsWatchingUntilCompletion()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        int waits = 0;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => Interlocked.Increment(ref waits) == 1
            ? Task.FromException<RemoteTransferSnapshot>(new IOException("temporary")) : finish.Task.WaitAsync(ct);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Task<IReadOnlyList<RemoteTransferSnapshot>> completed = run.Transfers!.WaitForCompletionAsync();
            finish.SetResult(snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 });
            Assert.AreEqual(RemoteTransferState.Succeeded,
                (await completed.WaitAsync(TimeSpan.FromSeconds(5)))[0].State);
            Assert.IsGreaterThanOrEqualTo(2, waits);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task UiPeek_DoesNotConsumeTerminal_AndFullBytesAreNotSuccess()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(
            snapshot = Running(id, source, target, options.OperationId) with { TransferredBytes = 64L * 1024 * 1024 });
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            string receipt = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("\"stillRunning\":true", receipt);
            snapshot = snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 };
            RemoteTransferSnapshot peek = await run.Transfers!.PeekAsync("transfer");
            Assert.IsTrue(peek.IsSuccess);
            Assert.IsTrue(run.Transfers.HasActiveJobs, "界面读取不能消费模型完成通知");
            Assert.HasCount(1, await run.Transfers.WaitForCompletionAsync());
            Assert.IsEmpty(await run.Transfers.WaitForCompletionAsync());
            finish.TrySetResult(snapshot);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task MonitorFailure_DoesNotForgeHostSequence_AndStillAllowsExplicitCleanup()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        RemoteTransferSnapshot? snapshot = null;
        bool recover = false;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, _) => Task.FromException<RemoteTransferSnapshot>(new IOException("offline"));
        context.FakeRemoteFs.GetTransferHandler = (_, _) => recover ? Task.FromResult(snapshot!)
            : Task.FromException<RemoteTransferSnapshot>(new IOException("offline"));
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot! with
        { State = RemoteTransferState.Cancelled, Sequence = 2, RemoteStopConfirmed = true });
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        List<FileTransferJobUpdate> updates = [];
        using IDisposable subscription = run.Transfers!.Subscribe(updates.Add);
        string local = Source(1);
        try
        {
            await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            RemoteTransferSnapshot unknown = (await run.Transfers!.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
            Assert.AreEqual(RemoteTransferState.Unknown, unknown.State);
            Assert.AreEqual(1L, unknown.Sequence, "本地观察失败不能递增宿主权威Sequence");
            Assert.IsTrue(run.Transfers.HasRunningTransfers);
            Assert.IsTrue(run.HasUnknownOutcome);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, "网络异常不能伪装成撤权取消");
            Assert.IsTrue(updates.Last().MonitoringUnavailable);
            Assert.AreEqual(RemoteTransferState.Running, updates.Last().Snapshot!.State);
            recover = true;
            Assert.AreEqual(1L, (await run.Transfers.PeekAsync("transfer")).Sequence);
            Assert.IsFalse(updates.Last().MonitoringUnavailable, "同一宿主Sequence恢复观察必须通知界面");
            await run.Transfers.CancelAllAsync();
            Assert.HasCount(1, context.FakeRemoteFs.TransfersCancelled);
        }
        finally { File.Delete(local); }
    }

    /// <summary>
    /// 观察连着失败几次只报"进度不可确认",不就此收手:宿主恢复后自己接着盯到终态。
    /// </summary>
    /// <remarks>
    /// 从前第三次失败后观察者就此退出,而且再也不重启:卡片冻在"不可确认"、IM 等不到结局、
    /// send_file 被这次下载永远挡着,订阅与清理登记也都放不掉 —— 除非恰好有人显式查一次状态。
    /// 不确定标记也只该活到拿回新鲜快照为止,不能一直把整轮钉在"结果未知"上。
    /// </remarks>
    [TestMethod]
    public async Task MonitorFailure_KeepsWatchingWithBackoff_AndClearsTheUncertaintyOnRecovery()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        bool offline = true;
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) =>
            Task.FromResult(snapshot = StartDownloadLikeHost(id, remote, local, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, _) => Volatile.Read(ref offline)
            ? Task.FromException<RemoteTransferSnapshot>(new IOException("offline"))
            : Task.FromResult(snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2, RemoteStopConfirmed = true });
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Volatile.Read(ref offline)
            ? Task.FromException<RemoteTransferSnapshot>(new IOException("offline")) : Task.FromResult(snapshot!);
        var registry = FileTransferJobRegistry.For(context);
        FileTransferRunScope scope = registry.BeginRun("owner", "run");
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = scope.Subscribe(update =>
        {
            if (update.MonitoringUnavailable) unavailable.TrySetResult();
            else if (update.Snapshot?.IsSuccess == true) succeeded.TrySetResult();
        });
        string target = Path.Combine(context.DataDirectory, "app.log");
        Directory.CreateDirectory(context.DataDirectory);
        await scope.StartDownloadAsync(session, "/var/log/app.log", target, "operation", false, default);

        await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(scope.HasUnknownOutcome, "观察不可确认时这一项算结果未知");
        Assert.IsTrue(registry.HasUncommittedDownload(target));
        Volatile.Write(ref offline, false);

        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(scope.HasRunningTransfers);
        Assert.IsFalse(scope.HasUnknownOutcome, "拿回新鲜快照之后,观察失败带来的不确定要撤掉");
        Assert.IsFalse(registry.HasUncommittedDownload(target), "下载已成功提交,send_file 不该再被它挡着");
        Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, "观察失败不能伪装成取消");
        Assert.AreEqual(TimeSpan.FromSeconds(1), FileTransferJobRegistry.RetryDelay(3));
        Assert.AreEqual(TimeSpan.FromSeconds(30), FileTransferJobRegistry.RetryDelay(40), "退避封顶 30 秒");
    }

    /// <summary>
    /// 任务登记之后、发布那一步抛了(授权回调读会话失败之类):回执照旧报不确定,
    /// 但这一项仍得有人盯到终态 —— 从前观察者压根没起,它就永远"还在跑"。
    /// </summary>
    [TestMethod]
    public async Task PublishFailureAfterRegistration_StillStartsTheWatcher()
    {
        using var context = new TestPluginContext();
        RemoteTransferSnapshot snapshot = Running("session", "source", "destination", "operation");
        var watched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, _) =>
        {
            watched.TrySetResult();
            return Task.FromResult(snapshot with { State = RemoteTransferState.Succeeded, Sequence = 2, RemoteStopConfirmed = true });
        };
        int checks = 0;
        // 第一次是启动前的授权;第二次落在登记之后的发布里
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run", (_, _) =>
            Interlocked.Increment(ref checks) == 2
                ? Task.FromException<bool>(new IOException("session lookup failed"))
                : Task.FromResult(true));
        using IDisposable subscription = scope.Subscribe(update =>
        {
            if (update.Snapshot?.IsSuccess == true) succeeded.TrySetResult();
        });

        await Assert.ThrowsAsync<IOException>(() => scope.StartAsync("session", "source", "destination", "operation", false, default));

        await watched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(scope.HasRunningTransfers);
    }

    [TestMethod]
    public async Task UnconfirmedHostUnknown_RemainsExplicitlyCancellable()
    {
        using var context = new TestPluginContext();
        RemoteTransferSnapshot snapshot = Running("session", "source", "destination", "start") with
        { State = RemoteTransferState.Unknown, RemoteStopConfirmed = false };
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot with
        { State = RemoteTransferState.Cancelled, Sequence = 2, RemoteStopConfirmed = true });
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        await scope.StartAsync("session", "source", "destination", "start", false, default);
        Assert.IsTrue(scope.HasRunningTransfers);
        await scope.CancelAllAsync();
        Assert.HasCount(1, context.FakeRemoteFs.TransfersCancelled);
        Assert.IsFalse(scope.HasRunningTransfers);
    }

    [TestMethod]
    public async Task RevokedScope_ProducesOnlyWithheldNotification_AndNoPathLeak()
    {
        using var context = new TestPluginContext();
        bool allowed = true;
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot snapshot = Running("session", "private-source", "private-destination", "operation");
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot);
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run", (_, _) => Task.FromResult(allowed));
        List<FileTransferJobUpdate> updates = [];
        using IDisposable subscription = scope.Subscribe(updates.Add);
        await scope.StartAsync("session", "private-source", "private-destination", "operation", false, default);
        allowed = false;
        finish.SetResult(snapshot with { State = RemoteTransferState.Succeeded, Sequence = 2 });
        RemoteTransferSnapshot withheld = (await scope.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
        Assert.AreEqual(RemoteTransferState.Unknown, withheld.State);
        Assert.AreEqual("", withheld.LocalPath);
        Assert.AreEqual("", withheld.RemotePath);
        Assert.AreEqual("", withheld.SessionId);
        Assert.IsTrue(updates.Last().Withheld);
        Assert.IsNull(updates.Last().Snapshot);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.PeekAsync("transfer"));
    }

    [TestMethod]
    public async Task HostAuthorizationRevoked_WaitExceptionWithholdsMetadataAndStopsModelSafely()
    {
        using var context = new TestPluginContext();
        var revoked = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot snapshot = Running("private-session", "private-source", "private-destination", "operation");
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => revoked.Task.WaitAsync(ct);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot with
        {
            SessionId = "",
            LocalPath = "",
            RemotePath = "",
            State = RemoteTransferState.Unknown,
            Sequence = 2,
            ErrorCode = "permission-revoked"
        });
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = scope.Subscribe(update =>
        {
            if (!update.Withheld) return;
            Assert.IsNull(update.Snapshot);
            Assert.AreEqual("", update.LocalPath);
            Assert.AreEqual("", update.RemotePath);
            hidden.TrySetResult();
        });
        await scope.StartAsync("private-session", "private-source", "private-destination", "operation", false, default);
        revoked.SetException(new PluginPermissionDeniedException("Upload authorization was revoked."));
        RemoteTransferSnapshot withheld = (await scope.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
        await hidden.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(scope.HasUnknownOutcome);
        Assert.AreEqual(RemoteTransferState.Unknown, withheld.State);
        Assert.AreEqual("", withheld.SessionId);
        Assert.AreEqual("", withheld.LocalPath);
        Assert.AreEqual("", withheld.RemotePath);
        Assert.IsEmpty(context.FakeRemoteFs.TransfersRead, "明确撤权不能当网络错误回读泄漏旧快照");
    }

    [TestMethod]
    public async Task Resume_PreservesHostGenerationAndCurrentTarget_AndCancelCarriesGeneration()
    {
        using var context = new TestPluginContext();
        RemoteTransferSnapshot snapshot = Running("old-session", "source", "destination", "start") with
        {
            State = RemoteTransferState.Failed,
            RemoteStopConfirmed = true,
            Resumable = true,
            DestinationCommitConfirmedAbsent = true
        };
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.ResumeTransferHandler = (_, options, _) => Task.FromResult(snapshot = snapshot with
        { SessionId = options.TargetSessionId, Generation = 2, Sequence = 2, State = RemoteTransferState.Succeeded });
        context.FakeRemoteFs.CancelTransferHandler = (_, generation, _) => generation == 2 ? Task.FromResult(snapshot)
            : Task.FromException<RemoteTransferSnapshot>(new InvalidOperationException("stale generation"));
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        await scope.StartAsync("old-session", "source", "destination", "start", false, default);
        RemoteTransferSnapshot resumed = await scope.ResumeAsync("transfer", new RemoteTransferResumeOptions
        { OperationId = "resume", ExpectedGeneration = 1, TargetSessionId = "current-session" }, default);
        Assert.AreEqual(2L, resumed.Generation);
        Assert.AreEqual("current-session", context.FakeRemoteFs.TransfersResumed[0].Options.TargetSessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CancelAsync("transfer", 1));
        await scope.CancelAsync("transfer", 2);
        Assert.AreEqual(2L, context.FakeRemoteFs.TransfersCancelled.Last().ExpectedGeneration);
    }

    [TestMethod]
    public async Task LostStartReceipt_BlocksAutomaticSideEffects_AndUsesStableCallIdentity()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) =>
            Task.FromException<RemoteTransferSnapshot>(new IOException("receipt lost"));
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            AgentToolbox.InvocationIdentity.Value = "stable-call";
            string first = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            string second = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("Unknown", first);
            Assert.IsTrue(run.HasUnknownOutcome);
            Assert.HasCount(2, context.FakeRemoteFs.UploadsStarted, "回执网络丢失只用相同OperationId重取一次；重复调用复用在途/失败账本");
            Assert.AreEqual(context.FakeRemoteFs.UploadsStarted[0].Options.OperationId,
                context.FakeRemoteFs.UploadsStarted[1].Options.OperationId);
            Assert.AreEqual(first, second);
        }
        finally { AgentToolbox.InvocationIdentity.Value = null; File.Delete(local); }
    }

    [TestMethod]
    [DataRow(ApprovalMode.Bypass, false)]
    [DataRow(ApprovalMode.Ask, true)]
    [DataRow(ApprovalMode.ReadOnlyAuto, true)]
    public async Task McpUpload_ReturnsQuickly_AndActiveTransferSurvivesCallTimeout(ApprovalMode approval, bool autoApprove)
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot = snapshot! with
        { State = RemoteTransferState.Cancelled, Sequence = 2, RemoteStopConfirmed = true });
        await using var host = new McpToolHost(context, new McpServerSettings
        { Mode = ChatMode.Agent, Approval = approval, AutoApproveUploads = autoApprove });
        string local = Source(64L * 1024 * 1024);
        try
        {
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            string result = await host.CallAsync("upload_local_file", new Dictionary<string, object?>
            { ["localPath"] = local, ["remotePath"] = "/tmp/file", ["sessionId"] = session.SessionId }, request.Token);
            Assert.Contains("transfer_id", result);
            await request.CancelAsync();
            Assert.IsTrue(host.HasActiveTransfers);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
            Assert.AreEqual(RemoteTransferApprovalContext.ExternalMcp, context.FakeRemoteFs.UploadsStarted.Single().Options.ApprovalContext);
            if (autoApprove)
            {
                context.FakeRemoteFs.GetTransferHandler = (_, _) =>
                    throw new UnauthorizedAccessException("Read authorization revoked.");
                await host.CallAsync("transfer_cancel", new Dictionary<string, object?>
                { ["transferId"] = "transfer", ["expectedGeneration"] = snapshot!.Generation }, default);
                Assert.HasCount(1, context.FakeRemoteFs.TransfersCancelled);
            }
            finish.TrySetResult(autoApprove ? snapshot! : snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 });
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task McpTransferApproval_IsIndependentPerDirectionAndDoesNotAuthorizeOtherEntrypoints(bool uploads, bool downloads)
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        context.FakeRemoteFs.StartUploadHandler = (id, local, remote, options, _) =>
            Task.FromResult(Running(id, local, remote, options.OperationId) with
            { TransferId = options.OperationId, State = RemoteTransferState.Succeeded, RemoteStopConfirmed = true });
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) =>
            Task.FromResult(StartDownloadLikeHost(id, remote, local, options.OperationId) with
            { TransferId = options.OperationId, State = RemoteTransferState.Succeeded, RemoteStopConfirmed = true });
        var settings = new McpServerSettings
        { Mode = ChatMode.Agent, Approval = ApprovalMode.Ask, AutoApproveUploads = uploads, AutoApproveDownloads = downloads };
        await using var host = new McpToolHost(context, settings);
        string source = Source(3);
        try
        {
            var uploadArgs = new Dictionary<string, object?> { ["sessionId"] = session, ["localPath"] = source, ["remotePath"] = "/tmp/target" };
            var downloadArgs = new Dictionary<string, object?> { ["sessionId"] = session, ["remotePath"] = "/var/log/app.log" };
            await host.CallAsync("upload_local_file", uploadArgs, default);
            await host.CallAsync("download_remote_file", downloadArgs, default);
            Assert.HasCount(uploads ? 1 : 0, context.FakeRemoteFs.UploadsStarted);
            Assert.HasCount(downloads ? 1 : 0, context.FakeRemoteFs.DownloadsStarted);
            if (downloads)
                Assert.AreEqual(RemoteTransferApprovalContext.ExternalMcp, context.FakeRemoteFs.DownloadsStarted.Single().Options.ApprovalContext);

            await using var desktop = new AgentToolbox(context) { Approval = ApprovalMode.Ask };
            await using CommandJobRunScope run = desktop.BeginRun("desktop", "turn", denialEndsRun: false);
            await Call(desktop, run, "upload_local_file", uploadArgs);
            await Call(desktop, run, "download_remote_file", downloadArgs);
            Assert.HasCount(uploads ? 1 : 0, context.FakeRemoteFs.UploadsStarted);
            Assert.HasCount(downloads ? 1 : 0, context.FakeRemoteFs.DownloadsStarted);
            desktop.Approval = ApprovalMode.Bypass;
            string receipt = await Call(desktop, run, "upload_local_file", uploadArgs);
            Assert.Contains("\"transfer_id\"", receipt);
            Assert.AreEqual(RemoteTransferApprovalContext.Default, context.FakeRemoteFs.UploadsStarted.Last().Options.ApprovalContext);

            await using var plan = new McpToolHost(context, new McpServerSettings
            { Mode = ChatMode.Plan, AutoApproveUploads = true, AutoApproveDownloads = true });
            Assert.IsFalse(plan.Tools.Any(t => t.Name is "upload_local_file" or "download_remote_file"));
            await using var disabled = new McpToolHost(context, new McpServerSettings
            { Mode = ChatMode.Agent, AutoApproveUploads = true, DisabledTools = "upload_local_file" });
            Assert.IsFalse(disabled.Tools.Any(t => t.Name == "upload_local_file"));
        }
        finally { File.Delete(source); }
    }

    /// <summary>
    /// 不给本机路径时落到插件私有的 downloads/。宿主只钉住已存在的父目录链,不替插件建目录 ——
    /// 这一步漏了,每一次默认下载都在宿主审批之前失败,IM 的 send_file 也就无文件可发。
    /// </summary>
    [TestMethod]
    public async Task DefaultDownload_CreatesThePrivateDownloadsFolder()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string folder = Path.Combine(context.DataDirectory, "downloads");
        Assert.IsFalse(Directory.Exists(folder));
        string receipt = await Call(toolbox, run, "download_remote_file", new()
        {
            ["remotePath"] = "/var/log/app.log",
            ["sessionId"] = session
        });
        Assert.IsTrue(Directory.Exists(folder), receipt);
        using var json = JsonDocument.Parse(receipt);
        Assert.AreEqual("Running", json.RootElement.GetProperty("state").GetString(), receipt);
        Assert.AreEqual(Path.Combine(folder, "app.log"), json.RootElement.GetProperty("snapshot").GetProperty("LocalPath").GetString());
        finish.TrySetResult(StartDownloadLikeHost(session, "/var/log/app.log", Path.Combine(folder, "app.log"), "done") with
        { State = RemoteTransferState.Succeeded, Sequence = 2, RemoteStopConfirmed = true });
    }

    /// <summary>
    /// 宿主没有独立下载能力时当场说清 —— 先弹审批卡、用户点了允许才说做不了,等于白问一次。
    /// </summary>
    [TestMethod]
    public async Task UnsupportedDownload_FailsBeforeAskingForApproval()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        context.FakeRemoteFs.SupportsDownloadOperations = false;
        List<ApprovalRequest> asked = [];
        await using var toolbox = new AgentToolbox(context)
        {
            ApprovalHandler = request =>
            {
                asked.Add(request);
                return Task.FromResult(true);
            }
        };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string result = await Call(toolbox, run, "download_remote_file", new()
        {
            ["remotePath"] = "/var/log/app.log",
            ["sessionId"] = session
        });
        Assert.Contains("does not support independent download tasks", result);
        Assert.IsEmpty(asked, "宿主做不了的下载不该先弹审批");
        Assert.IsEmpty(context.FakeRemoteFs.DownloadsStarted);
    }

    /// <summary>
    /// 下载回执丢失(IO/超时)是不确定的启动,不是失败:回成"失败"会招来一次重复下载。
    /// 同一调用身份重放只复用原账本,不另起任务。
    /// </summary>
    [TestMethod]
    public async Task LostDownloadReceipt_IsUnknownNotFailed_AndIsNotReplayed()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        context.FakeRemoteFs.StartDownloadHandler = (_, _, _, _, _) =>
            Task.FromException<RemoteTransferSnapshot>(new IOException("receipt lost"));
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        try
        {
            AgentToolbox.InvocationIdentity.Value = "stable-call";
            Dictionary<string, object?> args = new() { ["remotePath"] = "/var/log/app.log", ["sessionId"] = session };
            string first = await Call(toolbox, run, "download_remote_file", args);
            string second = await Call(toolbox, run, "download_remote_file", args);
            Assert.DoesNotContain("Failed", first);
            using var json = JsonDocument.Parse(first);
            Assert.AreEqual("Unknown", json.RootElement.GetProperty("state").GetString());
            Assert.IsTrue(run.HasUnknownOutcome);
            Assert.HasCount(2, context.FakeRemoteFs.DownloadsStarted, "回执丢失只用相同 OperationId 重取一次;重复调用复用原账本");
            Assert.AreEqual(first, second);
        }
        finally { AgentToolbox.InvocationIdentity.Value = null; }
    }

    /// <summary>宿主拒绝授权是本轮的停止边界,不是一次普通的下载失败;也不回带宿主的原话。</summary>
    [TestMethod]
    public async Task DeniedDownloadAuthorization_EndsTheRun()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        context.FakeRemoteFs.StartDownloadHandler = (_, _, _, _, _) => Task.FromException<RemoteTransferSnapshot>(
            new PluginPermissionDeniedException("Download permission was not granted."));
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string result = await Call(toolbox, run, "download_remote_file", new()
        {
            ["remotePath"] = "/var/log/app.log",
            ["sessionId"] = session
        });
        Assert.Contains("authorization was denied", result);
        Assert.DoesNotContain("Download permission was not granted", result);
        Assert.IsTrue(run.HasDeniedExecution);
        Assert.IsFalse(run.HasUnknownOutcome);
        Assert.HasCount(1, context.FakeRemoteFs.DownloadsStarted);
    }

    /// <summary>
    /// 等下载回执时用户按了停止:取消照常上抛,而不是回一句"下载失败" ——
    /// 已受理的启动仍在后台登记,告诉模型"失败"只会招来重复下载。
    /// </summary>
    [TestMethod]
    public async Task StopWhileWaitingForTheDownloadReceipt_IsNotReportedAsAFailure()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.StartDownloadHandler = (_, _, _, _, _) =>
        {
            accepted.TrySetResult();
            return receipt.Task;
        };
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        using var stop = new CancellationTokenSource();
        Task<string> download = Call(toolbox, run, "download_remote_file", new()
        {
            ["remotePath"] = "/var/log/app.log",
            ["sessionId"] = session
        }, stop.Token);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => download);
        receipt.SetResult(StartDownloadLikeHost(session, "/var/log/app.log", "late", "late") with
        { State = RemoteTransferState.Succeeded, Sequence = 2, RemoteStopConfirmed = true });
    }

    /// <summary>
    /// 默认落点的文件名来自服务器,不可信:盘符相对名(<c>C:x</c> 会让 Path.Combine 整个丢掉下载目录)、
    /// 带冒号的备用数据流、设备名、结尾的点(Windows 会悄悄削掉)一律拒绝,一个字节都不下。
    /// </summary>
    [TestMethod]
    public async Task DefaultDownload_RefusesRemoteNamesThatAreNotPlainLocalFileNames()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        foreach (string name in new[] { "C:x", "report.txt:hidden", "NUL", "nul.tar.gz", "COM1.txt", "lpt9", "CONOUT$", "COM¹", "notes." })
        {
            context.FakeRemoteFs.AddFile(session, "/srv/" + name, new byte[4]);
            string result = await Call(toolbox, run, "download_remote_file", new()
            {
                ["remotePath"] = "/srv/" + name,
                ["sessionId"] = session
            });
            Assert.Contains("Refused", result, name);
        }
        Assert.IsEmpty(context.FakeRemoteFs.DownloadsStarted);
    }

    /// <summary>
    /// 给了 local_path 就与上传同一把尺:必须是完整的绝对路径,并在审批之前规范化。
    /// </summary>
    /// <remarks>
    /// Windows 上 <c>C:foo</c>(C 盘的当前目录)与 <c>\tmp\x</c>(当前盘的根)都算 rooted,
    /// 宿主会按自己进程的当前目录补全 —— 落点就不是审批卡上写的那个。带 <c>..</c> 的全路径先折平,
    /// 审批卡与宿主拿到的是同一个路径。
    /// </remarks>
    [TestMethod]
    public async Task ExplicitDownloadTarget_MustBeFullyQualified_AndIsNormalizedBeforeApproval()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        var never = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => never.Task.WaitAsync(ct);
        List<ApprovalRequest> asked = [];
        await using var toolbox = new AgentToolbox(context)
        {
            ApprovalHandler = request =>
            {
                asked.Add(request);
                return Task.FromResult(true);
            }
        };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        foreach (string partial in new[] { "C:app.log", @"\tmp\app.log", "relative/app.log" })
        {
            string refused = await Call(toolbox, run, "download_remote_file", new()
            {
                ["remotePath"] = "/var/log/app.log",
                ["localPath"] = partial,
                ["sessionId"] = session
            });
            Assert.Contains("absolute path", refused, partial);
        }
        Assert.IsEmpty(asked, "补不全的路径不该先弹审批");
        Assert.IsEmpty(context.FakeRemoteFs.DownloadsStarted);

        string folder = Path.Combine(context.DataDirectory, "exports");
        Directory.CreateDirectory(folder);
        string dotted = Path.Combine(folder, "..", "exports", "app.log");
        string expected = Path.GetFullPath(dotted);
        await Call(toolbox, run, "download_remote_file", new()
        {
            ["remotePath"] = "/var/log/app.log",
            ["localPath"] = dotted,
            ["sessionId"] = session
        });
        Assert.Contains("→ " + expected + " ", asked.Single().Detail);
        Assert.AreEqual(expected, context.FakeRemoteFs.DownloadsStarted.Single().LocalPath);
    }

    /// <summary>
    /// 插件自己在交给宿主之前的拒绝 —— 操作身份冲突、每插件 1024 份回执用尽 —— 是确定的"没开始":
    /// 回「启动前被拒」,不报回执丢失,也不把整轮停成结果未知。
    /// </summary>
    [TestMethod]
    public async Task PluginSideRefusals_AreRejectedBeforeExecution_NotUnknown()
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(
            Running(id, source, target, options.OperationId) with
            { TransferId = options.OperationId, State = RemoteTransferState.Succeeded, RemoteStopConfirmed = true });
        var registry = FileTransferJobRegistry.For(context);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            // 这次调用要用的操作身份已经被另一轮占了
            AgentToolbox.InvocationIdentity.Value = "stable-call";
            string taken = FileTransferJobRegistry.StableOperationId("owner", "run", "upload:stable-call");
            await registry.BeginRun("other", "run").StartAsync(session, local, "/tmp/other", taken, false, default);
            string conflict = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session
            });
            Assert.Contains("rejected before execution", conflict);
            Assert.IsFalse(run.HasUnknownOutcome, conflict);
            Assert.HasCount(1, context.FakeRemoteFs.UploadsStarted);

            FileTransferRunScope filler = registry.BeginRun("filler", "run");
            for (int i = 1; i < 1024; i++)
                await filler.StartAsync(session, local, "/tmp/filler", "filler-" + i, false, default);
            AgentToolbox.InvocationIdentity.Value = "download-call";
            string exhausted = await Call(toolbox, run, "download_remote_file", new()
            {
                ["remotePath"] = "/var/log/app.log",
                ["sessionId"] = session
            });
            Assert.Contains("rejected before execution", exhausted);
            Assert.Contains("budget", exhausted);
            Assert.IsFalse(run.HasUnknownOutcome, exhausted);
            Assert.IsEmpty(context.FakeRemoteFs.DownloadsStarted);
        }
        finally { AgentToolbox.InvocationIdentity.Value = null; File.Delete(local); }
    }

    /// <summary>
    /// 宿主登记之前的明确拒绝(SDK 2.1.3 的 <see cref="PluginTransferRejectedException" />:源被占用、找不到、额度满)
    /// 同样是确定的"没开始":不按回执丢失用同一个 OperationId 重取(它不是 IOException),不把整轮停成结果未知,
    /// 也不让这个本地路径从此被当成「可能还在写」;给模型的说明按原因写,叫它别紧接着重试。
    /// </summary>
    [TestMethod]
    [DataRow(RemoteTransferRejectReason.SourceInUse, "locked by another program")]
    [DataRow(RemoteTransferRejectReason.SourceNotFound, "does not exist")]
    [DataRow(RemoteTransferRejectReason.QuotaExceeded, "too many transfers")]
    [DataRow(RemoteTransferRejectReason.Unknown, "no specific reason")]
    public async Task HostSideRefusalsBeforeRegistering_AreRejectedNotUnknown(RemoteTransferRejectReason reason, string hint)
    {
        using var context = new TestPluginContext();
        string session = DownloadableHost(context);
        int uploads = 0, downloads = 0;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref uploads);
            return Task.FromException<RemoteTransferSnapshot>(new PluginTransferRejectedException(reason, "refused by host"));
        };
        context.FakeRemoteFs.StartDownloadHandler = (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref downloads);
            return Task.FromException<RemoteTransferSnapshot>(new PluginTransferRejectedException(reason, "refused by host"));
        };
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        string target = Path.Combine(context.DataDirectory, "rejected.log");
        try
        {
            string upload = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session
            });
            string download = await Call(toolbox, run, "download_remote_file", new()
            {
                ["remotePath"] = "/var/log/app.log",
                ["localPath"] = target,
                ["sessionId"] = session
            });

            foreach (string result in new[] { upload, download })
            {
                Assert.Contains("rejected by the host before it started", result);
                Assert.Contains(hint, result);
                Assert.DoesNotContain("Unknown", result);
            }
            Assert.AreEqual(1, uploads, "启动前被拒不是回执丢失,不能拿同一个 OperationId 再发一次");
            Assert.AreEqual(1, downloads);
            Assert.IsFalse(run.HasUnknownOutcome, upload + download);
            Assert.IsFalse(FileTransferJobRegistry.For(context).HasUncommittedDownload(target), "被拒的下载什么都没写,不挡后续 send_file");
        }
        finally { File.Delete(local); }
    }

    /// <summary>
    /// transfer_resume 续不上时只回一句文字。它看原任务时不能顺手把终态消费掉 ——
    /// 否则协调器以为模型已经知道结局,再也不发完成通知,模型只知道"不可恢复"而不知道结果。
    /// </summary>
    [TestMethod]
    public async Task ResumeThatCannotProceed_LeavesTheTerminalStateForTheCompletionNotification()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var never = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot! with
        { State = RemoteTransferState.Failed, RemoteStopConfirmed = true, Sequence = 2 });
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => never.Task.WaitAsync(ct);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            string resumed = await Call(toolbox, run, "transfer_resume", new()
            {
                ["transferId"] = "transfer",
                ["expectedGeneration"] = 1L,
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("not confirmed", resumed);
            Assert.IsTrue(run.Transfers!.HasActiveJobs, "续不上时终态还没交给模型");
            RemoteTransferSnapshot notified = (await run.Transfers.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5))).Single();
            Assert.AreEqual(RemoteTransferState.Failed, notified.State);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersResumed);
        }
        finally { File.Delete(local); }
    }

    /// <summary>transfer_wait / transfer_cancel 管的是本轮任意方向的传输,说明与回话里不能只写"上传"。</summary>
    [TestMethod]
    public async Task TransferWaitAndCancel_SpeakOfTransfersNotJustUploads()
    {
        using var context = new TestPluginContext();
        await using var toolbox = new AgentToolbox(context) { ApprovalHandler = _ => Task.FromResult(false) };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        foreach (string name in new[] { "transfer_wait", "transfer_cancel" })
        {
            AIFunction tool = Tool(toolbox, run, name);
            Assert.Contains("download", tool.Description, name);
            Assert.DoesNotContain("the upload", tool.Description + tool.JsonSchema, name);
        }
        string denied = await Call(toolbox, run, "transfer_cancel", new()
        {
            ["transferId"] = "download-transfer",
            ["expectedGeneration"] = 1L
        });
        Assert.Contains("DENIED cancelling this transfer", denied);
    }

    /// <summary>
    /// 仿宿主的逐任务确认:每次上传先停在 Approval 阶段(确认框排在闸后),测试点「批准」「拒绝」;
    /// 取消请求让还在确认阶段的任务以 Cancelled 收尾(确认框随之撤下),已批准的则只记一次取消请求。
    /// 构造时要求 withdraws 的同时宣告条件撤回:判定与撤下和「批准」在同一把锁里,先到者赢(与宿主同一口径)。
    /// </summary>
    private sealed class ApprovalQueueHost
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, RemoteTransferSnapshot> _snapshots = [];
        private readonly Dictionary<string, TaskCompletionSource> _changed = [];
        private readonly HashSet<string> _approved = [];
        private readonly HashSet<string> _approvedThenCancelled = [];
        private int _next;

        public ApprovalQueueHost(TestPluginContext context, bool withdraws = false)
        {
            context.FakeRemoteFs.SupportsTransferOperations = true;
            context.FakeRemoteFs.SupportsTransferWithdraw = withdraws;
            if (withdraws)
            {
                context.FakeRemoteFs.WithdrawTransferHandler = (id, _, _) =>
                {
                    lock (_gate)
                    {
                        RemoteTransferSnapshot current = _snapshots[id];
                        RemoteTransferWithdrawOutcome outcome;
                        if (_approved.Contains(id))
                        {
                            outcome = RemoteTransferWithdrawOutcome.PastApproval;
                        }
                        else if (current.CancellationRequested)
                        {
                            outcome = RemoteTransferWithdrawOutcome.Withdrawn;
                        }
                        else if (current.IsTerminal)
                        {
                            outcome = RemoteTransferWithdrawOutcome.AlreadyEnded;
                        }
                        else
                        {
                            Set(id, current with { State = RemoteTransferState.Cancelled, CancellationRequested = true, RemoteStopConfirmed = true });
                            outcome = RemoteTransferWithdrawOutcome.Withdrawn;
                        }
                        return Task.FromResult(new RemoteTransferWithdrawResult { Outcome = outcome, Snapshot = _snapshots[id] });
                    }
                };
            }
            context.FakeRemoteFs.StartUploadHandler = (session, local, remote, options, _) =>
            {
                lock (_gate)
                {
                    string id = $"t{++_next}";
                    _snapshots[id] = new RemoteTransferSnapshot
                    {
                        TransferId = id,
                        OperationId = options.OperationId,
                        SessionId = session,
                        LocalPath = local,
                        RemotePath = remote,
                        State = RemoteTransferState.Starting,
                        Phase = RemoteTransferPhase.Approval,
                        Sequence = 1,
                        Generation = 1,
                        TotalBytes = 1
                    };
                    _changed[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return Task.FromResult(_snapshots[id]);
                }
            };
            context.FakeRemoteFs.GetTransferHandler = (id, _) => { lock (_gate) return Task.FromResult(_snapshots[id]); };
            context.FakeRemoteFs.WaitTransferHandler = async (id, after, _, ct) =>
            {
                Task changed;
                lock (_gate)
                {
                    if (_snapshots[id].Sequence > after) return _snapshots[id];
                    changed = _changed[id].Task;
                }
                await changed.WaitAsync(ct);
                lock (_gate) return _snapshots[id];
            };
            context.FakeRemoteFs.CancelTransferHandler = (id, _, _) =>
            {
                lock (_gate)
                {
                    RemoteTransferSnapshot current = _snapshots[id];
                    if (current.IsTerminal) return Task.FromResult(current);
                    if (_approved.Contains(id)) _approvedThenCancelled.Add(id);
                    Set(id, current.Phase == RemoteTransferPhase.Approval
                        ? current with { State = RemoteTransferState.Cancelled, CancellationRequested = true, RemoteStopConfirmed = true }
                        : current with { State = RemoteTransferState.CancelRequested, CancellationRequested = true });
                    return Task.FromResult(_snapshots[id]);
                }
            };
        }

        /// <summary>用户点「批准」;已经撤下或收尾的(撤回先到)这一下作废,返回 false。</summary>
        public bool Approve(string id)
        {
            lock (_gate)
            {
                RemoteTransferSnapshot current = _snapshots[id];
                if (current.IsTerminal || current.CancellationRequested) return false;
                _approved.Add(id);
                Set(id, current with { State = RemoteTransferState.Running, Phase = RemoteTransferPhase.Transfer });
                return true;
            }
        }

        /// <summary>宿主这边已经批准,却还没唤醒插件的观察者 —— 插件手里的快照仍停在 Approval。</summary>
        public void ApproveUnobserved(string id)
        {
            lock (_gate)
            {
                _approved.Add(id);
                _snapshots[id] = _snapshots[id] with
                {
                    State = RemoteTransferState.Running,
                    Phase = RemoteTransferPhase.Transfer,
                    Sequence = _snapshots[id].Sequence + 1
                };
            }
        }

        /// <summary>批准生效之后又收到了取消 —— 「批准了又被撤」。</summary>
        public bool ApprovedThenCancelled(string id) { lock (_gate) return _approvedThenCancelled.Contains(id); }

        public void Deny(string id)
        {
            lock (_gate)
            {
                Set(id, _snapshots[id] with
                {
                    State = RemoteTransferState.Failed,
                    RemoteStopConfirmed = true,
                    ErrorCode = "permission-denied",
                    ErrorMessage = "A trusted host upload approval was not granted."
                });
            }
        }

        public RemoteTransferState State(string id) { lock (_gate) return _snapshots[id].State; }

        // 调用方持有 _gate
        private void Set(string id, RemoteTransferSnapshot next)
        {
            _snapshots[id] = next with { Sequence = _snapshots[id].Sequence + 1 };
            TaskCompletionSource changed = _changed[id];
            _changed[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    private static async Task UploadAsync(AgentToolbox toolbox, CommandJobRunScope run, string session, string local, string remote)
    {
        string receipt = await Call(toolbox, run, "upload_local_file", new()
        { ["localPath"] = local, ["remotePath"] = remote, ["sessionId"] = session });
        Assert.Contains("transfer_id", receipt, receipt);
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (int i = 0; i < 1_000 && !done(); i++) await Task.Delay(10);
        Assert.IsTrue(done(), "等待的条件没能在超时内成立。");
    }

    /// <summary>
    /// AI 上传③:一轮里发起了几次上传,宿主的确认框排着队一个个弹。用户点了停止,
    /// 还在等确认的那些要一并撤掉(确认框不再弹),已经批准的照常传完 —— 停止只停模型。
    /// 宿主支持条件撤回时逐项走 <see cref="IRemoteFsApi.WithdrawPendingTransferAsync" />,一次无条件取消都不发;
    /// 不支持时照旧「先取快照、仍在确认阶段才按代次取消」,也绝不去调条件撤回。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "宿主不支持条件撤回")]
    [DataRow(true, DisplayName = "宿主支持条件撤回")]
    public async Task Stop_WithdrawsTheRunsUploadsStillAwaitingHostApproval(bool hostWithdraws)
    {
        using var context = new TestPluginContext();
        string session = context.FakeSessions.AddConnected().SessionId;
        var host = new ApprovalQueueHost(context, hostWithdraws);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        using var stop = new CancellationTokenSource();
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run", cancellationToken: stop.Token);
        string local = Source(1);
        try
        {
            await UploadAsync(toolbox, run, session, local, "/tmp/a");
            await UploadAsync(toolbox, run, session, local, "/tmp/b");
            await UploadAsync(toolbox, run, session, local, "/tmp/c");
            host.Approve("t1");
            await WaitUntilAsync(() => host.State("t1") == RemoteTransferState.Running);

            await stop.CancelAsync();

            await WaitUntilAsync(() => host.State("t2") == RemoteTransferState.Cancelled && host.State("t3") == RemoteTransferState.Cancelled);
            Assert.AreEqual(RemoteTransferState.Running, host.State("t1"), "已批准的那一个照常传");
            Assert.IsFalse(host.ApprovedThenCancelled("t1"), "批准了又被撤");
            Assert.IsFalse(run.HasUnknownOutcome, "撤掉的是还没开始的任务,结果是确定的");
            if (hostWithdraws)
            {
                Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, "支持条件撤回的宿主上不发无条件取消");
                CollectionAssert.IsSubsetOf(new[] { "t2", "t3" }, context.FakeRemoteFs.TransfersWithdrawn.Select(w => w.TransferId).ToList());
            }
            else
            {
                await WaitUntilAsync(() => context.FakeRemoteFs.TransfersCancelled.Count == 2);
                Assert.IsEmpty(context.FakeRemoteFs.TransfersWithdrawn, "不支持的宿主上不调条件撤回");
            }
        }
        finally { File.Delete(local); }
    }

    /// <summary>
    /// 停止那一刻插件手里的快照落后一拍(宿主已经批准、观察者还没收到 Transfer 阶段):那一个照样不撤。
    /// 老宿主上靠撤之前先向宿主取最新的;支持条件撤回的宿主上由宿主判定,答 PastApproval、什么都不动。
    /// 以前按旧快照撤,用户刚批准的上传随停止一起被撤掉(负载下必现)。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "宿主不支持条件撤回")]
    [DataRow(true, DisplayName = "宿主支持条件撤回")]
    public async Task Stop_KeepsAnUploadTheHostApprovedBeforeThePluginSawIt(bool hostWithdraws)
    {
        using var context = new TestPluginContext();
        string session = context.FakeSessions.AddConnected().SessionId;
        var host = new ApprovalQueueHost(context, hostWithdraws);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        using var stop = new CancellationTokenSource();
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run", cancellationToken: stop.Token);
        string local = Source(1);
        try
        {
            await UploadAsync(toolbox, run, session, local, "/tmp/a");
            await UploadAsync(toolbox, run, session, local, "/tmp/b");
            host.ApproveUnobserved("t1");

            await stop.CancelAsync();

            await WaitUntilAsync(() => host.State("t2") == RemoteTransferState.Cancelled);
            Assert.AreEqual(RemoteTransferState.Running, host.State("t1"), "宿主已经批准的那一个不撤");
            Assert.IsFalse(host.ApprovedThenCancelled("t1"));
            if (hostWithdraws)
            {
                // 插件手里的 t1 仍是 Approval,照样去问宿主;宿主答 PastApproval,插件只发布不撤。
                await WaitUntilAsync(() => context.FakeRemoteFs.TransfersWithdrawn.Count == 2);
                Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
                Assert.IsEmpty(context.FakeRemoteFs.TransfersRead.Where(id => id == "t1"), "条件撤回不必先取快照");
            }
            else
            {
                Assert.AreSequenceEqual(["t2"], [.. context.FakeRemoteFs.TransfersCancelled.Select(c => c.TransferId)]);
            }
        }
        finally { File.Delete(local); }
    }

    /// <summary>
    /// 宿主恰在停止的那一拍批准(§263 合入后发现的那条竞态在负载下的样子):批准与停止同时出发、反复撞。
    /// 支持条件撤回的宿主上,每一轮只许两种结局 —— 批准先到就照常传(插件绝不去撤它),撤回先到就以 Cancelled 收尾、批准作废;
    /// 绝不出现「批准了又被撤」,插件也一次无条件取消都不发。
    /// </summary>
    [TestMethod]
    public async Task Stop_RacingTheHostsApproval_NeverWithdrawsWhatTheUserJustApproved()
    {
        const int rounds = 30;
        int approvedFirst = 0, withdrawnFirst = 0;
        for (int i = 0; i < rounds; i++)
        {
            using var context = new TestPluginContext();
            string session = context.FakeSessions.AddConnected().SessionId;
            var host = new ApprovalQueueHost(context, withdraws: true);
            await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
            using var stop = new CancellationTokenSource();
            await using CommandJobRunScope run = toolbox.BeginRun("owner", "run", cancellationToken: stop.Token);
            string local = Source(1);
            try
            {
                await UploadAsync(toolbox, run, session, local, "/tmp/a");
                await UploadAsync(toolbox, run, session, local, "/tmp/b");
                using var barrier = new Barrier(2);
                // 停止那条路要先经线程池跑停止回调、再发撤回,比点一下「批准」慢:批准逐轮往后错开 0 – 0.5 ms,两种先后都撞得到。
                TimeSpan lag = TimeSpan.FromMilliseconds(0.5 * i / rounds);
                Task<bool> approve = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    for (var clock = System.Diagnostics.Stopwatch.StartNew(); clock.Elapsed < lag;) Thread.SpinWait(20);
                    return host.Approve("t1");
                });
                Task stopping = Task.Run(async () => { barrier.SignalAndWait(); await stop.CancelAsync(); });
                await Task.WhenAll(approve, stopping);

                await WaitUntilAsync(() => host.State("t2") == RemoteTransferState.Cancelled);
                await WaitUntilAsync(() => host.State("t1") is RemoteTransferState.Running or RemoteTransferState.Cancelled);
                if (await approve)
                {
                    approvedFirst++;
                    Assert.AreEqual(RemoteTransferState.Running, host.State("t1"), $"第 {i} 轮:批准先到,却没照常传");
                }
                else
                {
                    withdrawnFirst++;
                    Assert.AreEqual(RemoteTransferState.Cancelled, host.State("t1"), $"第 {i} 轮:撤回先到,批准却生效了");
                }
                Assert.IsFalse(host.ApprovedThenCancelled("t1"), $"第 {i} 轮:批准了又被撤");
                Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, $"第 {i} 轮:发了无条件取消");
            }
            finally { File.Delete(local); }
        }
        Console.WriteLine($"[stop-race] approved first {approvedFirst}, withdrawn first {withdrawnFirst}");
    }

    /// <summary>
    /// 支持条件撤回的宿主给了本 SDK 不认识的结论(更新的宿主)、快照还停在确认阶段:不当成撤下了,
    /// 也不据此改发无条件取消 —— 只发布快照,由观察者照常盯到终态。
    /// </summary>
    [TestMethod]
    public async Task Stop_WithAnUnknownWithdrawalOutcome_NeitherAssumesWithdrawnNorFallsBackToCancel()
    {
        using var context = new TestPluginContext();
        string session = context.FakeSessions.AddConnected().SessionId;
        var host = new ApprovalQueueHost(context, withdraws: true);
        context.FakeRemoteFs.WithdrawTransferHandler = async (id, _, ct) => new RemoteTransferWithdrawResult
        {
            Outcome = (RemoteTransferWithdrawOutcome)42,
            Snapshot = await context.FakeRemoteFs.GetTransferHandler!(id, ct)
        };
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        using var stop = new CancellationTokenSource();
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run", cancellationToken: stop.Token);
        string local = Source(1);
        try
        {
            await UploadAsync(toolbox, run, session, local, "/tmp/a");
            await stop.CancelAsync();
            await WaitUntilAsync(() => context.FakeRemoteFs.TransfersWithdrawn.Count >= 1);
            await Task.Delay(200);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, "不认识的结论不改发无条件取消");
            Assert.AreEqual(RemoteTransferState.Starting, host.State("t1"), "宿主那边什么都没动");
        }
        finally { File.Delete(local); }
    }

    /// <summary>拒绝同样是本轮的停止边界:插件审批卡上拒了别的工具,或在宿主确认框里拒了其中一个上传。</summary>
    [TestMethod]
    [DataRow("plugin-card", false)]
    [DataRow("host-dialog", false)]
    [DataRow("plugin-card", true)]
    [DataRow("host-dialog", true)]
    public async Task Denial_WithdrawsTheRunsOtherUploadsStillAwaitingHostApproval(string where, bool hostWithdraws)
    {
        using var context = new TestPluginContext();
        string session = context.FakeSessions.AddConnected().SessionId;
        context.FakeRemoteExec.Handler = (_, _) => "out";
        var host = new ApprovalQueueHost(context, hostWithdraws);
        await using var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session,
            Approval = ApprovalMode.Ask,
            ApprovalHandler = request => Task.FromResult(request.Kind == "upload_local_file")
        };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            await UploadAsync(toolbox, run, session, local, "/tmp/a");
            await UploadAsync(toolbox, run, session, local, "/tmp/b");
            if (where == "plugin-card")
            {
                Assert.Contains("DENIED", await Call(toolbox, run, "run_command", new() { ["command"] = "rm -rf /tmp/x" }));
                await WaitUntilAsync(() => host.State("t1") == RemoteTransferState.Cancelled && host.State("t2") == RemoteTransferState.Cancelled);
            }
            else
            {
                host.Deny("t1");
                await WaitUntilAsync(() => host.State("t2") == RemoteTransferState.Cancelled);
                Assert.AreEqual(RemoteTransferState.Failed, host.State("t1"));
            }
            Assert.IsTrue(run.HasDeniedExecution);
            if (hostWithdraws) Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, "支持条件撤回的宿主上不发无条件取消");
            else Assert.IsEmpty(context.FakeRemoteFs.TransfersWithdrawn, "不支持的宿主上不调条件撤回");
        }
        finally { File.Delete(local); }
    }

    /// <summary>
    /// 正常收尾(模型答完、上传还排着等确认)不撤:那是用户随后要去点的。
    /// 对外 MCP 的轮次跨整条连接,宿主拒了其中一个也不连带撤掉同一连接里别的上传。
    /// </summary>
    [TestMethod]
    [DataRow(true, false, DisplayName = "桌面 / IM 的轮次正常收尾")]
    [DataRow(false, false, DisplayName = "对外 MCP 的轮次里宿主拒了一个")]
    [DataRow(true, true, DisplayName = "桌面 / IM 的轮次正常收尾(宿主支持条件撤回)")]
    [DataRow(false, true, DisplayName = "对外 MCP 的轮次里宿主拒了一个(宿主支持条件撤回)")]
    public async Task NormalFinishOrMcpDenial_LeavesPendingApprovalsAlone(bool desktop, bool hostWithdraws)
    {
        using var context = new TestPluginContext();
        string session = context.FakeSessions.AddConnected().SessionId;
        var host = new ApprovalQueueHost(context, hostWithdraws);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        using var stop = new CancellationTokenSource();
        CommandJobRunScope run = toolbox.BeginRun("owner", "run", denialEndsRun: desktop, cancellationToken: stop.Token);
        string local = Source(1);
        try
        {
            await UploadAsync(toolbox, run, session, local, "/tmp/a");
            await UploadAsync(toolbox, run, session, local, "/tmp/b");
            if (desktop)
            {
                await run.DisposeAsync();
                await stop.CancelAsync(); // 收尾之后入口令牌再被取消(换会话、关面板)与这一轮无关
            }
            else
            {
                host.Deny("t1");
                await WaitUntilAsync(() => run.Transfers!.HasDeniedExecution);
            }
            await Task.Delay(200);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersWithdrawn, "正常收尾不撤,条件撤回也不调");
            Assert.AreEqual(RemoteTransferState.Starting, host.State("t2"), "还等着用户去点");
        }
        finally
        {
            await run.DisposeAsync();
            File.Delete(local);
        }
    }

    /// <summary>
    /// 停止之后才拿到回执、还在确认阶段的任务(启动请求在路上)同样一到就撤;支持条件撤回的宿主上走条件撤回、不发无条件取消。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "宿主不支持条件撤回")]
    [DataRow(true, DisplayName = "宿主支持条件撤回")]
    public async Task UploadReceiptArrivingAfterStop_IsWithdrawnToo(bool hostWithdraws)
    {
        using var context = new TestPluginContext();
        var receipt = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot pending = Running("session", "source", "/remote", "operation") with
        { State = RemoteTransferState.Starting, Phase = RemoteTransferPhase.Approval, TransferredBytes = 0 };
        RemoteTransferSnapshot cancelled = pending with
        { State = RemoteTransferState.Cancelled, CancellationRequested = true, RemoteStopConfirmed = true, Sequence = 2 };
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsTransferWithdraw = hostWithdraws;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, ct) => { accepted.TrySetResult(); return receipt.Task.WaitAsync(ct); };
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(pending);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => pending, ct);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(cancelled);
        context.FakeRemoteFs.WithdrawTransferHandler = (_, _, _) => Task.FromResult(new RemoteTransferWithdrawResult
        { Outcome = RemoteTransferWithdrawOutcome.Withdrawn, Snapshot = cancelled });
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run", denialEndsRun: true);
        Task<RemoteTransferSnapshot> start = scope.StartAsync("session", "source", "/remote", "operation", false, default);
        await accepted.Task;

        await scope.CancelUnapprovedAsync();
        Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, "回执还没到,没有可撤的");
        Assert.IsEmpty(context.FakeRemoteFs.TransfersWithdrawn);
        receipt.SetResult(pending);
        await start;

        if (hostWithdraws)
        {
            await WaitUntilAsync(() => context.FakeRemoteFs.TransfersWithdrawn.Count == 1);
            Assert.AreEqual(("transfer", 1L), context.FakeRemoteFs.TransfersWithdrawn[0]);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
        }
        else
        {
            await WaitUntilAsync(() => context.FakeRemoteFs.TransfersCancelled.Count == 1);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersWithdrawn);
        }
    }
}
