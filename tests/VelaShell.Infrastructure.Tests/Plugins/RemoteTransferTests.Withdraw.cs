using System.Collections.Concurrent;
using System.Security.Cryptography;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.PluginHost;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 条件撤回(<see cref="IRemoteFsApi.WithdrawPendingTransferAsync" />,SDK 2.1.4):只在这一代的批准还没生效时撤下。
/// 判定与撤下在任务锁里一步完成,与「批准生效」(授权闸登记批准 → onApproved 在任务锁里推进阶段)同一个串行点,先到者赢;
/// 上传与下载同一口径。
/// </summary>
public sealed partial class RemoteTransferTests
{
    [TestMethod]
    public void TheHostAdvertisesConditionalWithdrawal()
    {
        using var capability = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, AllowEverything()));
        IRemoteFsApi api = capability;
        Assert.IsTrue(api.SupportsTransferWithdraw);
    }

    /// <summary>
    /// 撤回先到:已经弹出的确认框随之关掉,排在确认闸后面的不再弹;之后才到的「允许」作废。
    /// 两项都以 Cancelled 收尾、RemoteStopConfirmed,一个字节都没读写。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WithdrawBeforeApproval_ClosesTheShownPromptSkipsTheQueuedOneAndTransfersNothing(bool download)
    {
        string directory = NewDirectory("vela-withdraw-");
        string file = CreateFile(8);
        _sftp.GetFileInfoAsync(_session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        var prompts = new HeldPrompts();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompts.Prompt));
        try
        {
            RemoteTransferSnapshot first = await StartHeldAsync(api, download, file, directory, "a");
            RemoteTransferSnapshot second = await StartHeldAsync(api, download, file, directory, "b");
            Shown shown = await prompts.NextAsync();
            (RemoteTransferSnapshot asked, RemoteTransferSnapshot waiting) = shown.Remote == first.RemotePath ? (first, second) : (second, first);

            RemoteTransferWithdrawResult queued = await api.WithdrawPendingTransferAsync(waiting.TransferId, waiting.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, queued.Outcome);
            Assert.IsTrue(queued.IsWithdrawn);
            Assert.IsTrue(queued.Snapshot.CancellationRequested, "撤回登记了取消意图");
            Assert.AreEqual(RemoteTransferPhase.Approval, queued.Snapshot.Phase);
            AssertWithdrawnEnd(await Terminal(api, waiting.TransferId));

            RemoteTransferWithdrawResult open = await api.WithdrawPendingTransferAsync(asked.TransferId, asked.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, open.Outcome);
            Assert.IsTrue(shown.Token.IsCancellationRequested, "已经弹出的确认框随撤回关掉");
            shown.Decision.TrySetResult(PluginPermissionDecision.AllowOnce);   // 撤回之后才到的批准作废
            AssertWithdrawnEnd(await Terminal(api, asked.TransferId));

            await WaitIdle(api);
            Assert.AreEqual(1, prompts.Count, "排在确认闸后面的那一项没有再弹");
            await AssertNothingTransferredAsync();
            if (download) Assert.IsFalse(File.Exists(Path.Combine(directory, "a")) || File.Exists(Path.Combine(directory, "b")));
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn,
                (await api.WithdrawPendingTransferAsync(asked.TransferId, asked.Generation)).Outcome, "收尾之后再问,结论不变");
        }
        finally { prompts.ReleaseAll(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 批准先生效(已批准、在等传输槽):撤回答 PastApproval、什么都不改,任务照常传完;收尾之后再问仍是 PastApproval。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WithdrawAfterApproval_IsPastApprovalAndTheTransferCompletes(bool download)
    {
        string directory = NewDirectory("vela-withdraw-");
        byte[] content = [1, 2, 3];
        string target = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(content.Length));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(content, call.ArgAt<CancellationToken>(4)));
        ConfigureWithdrawDigests(content, 8);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, AllowEverything()));
        (TaskCompletionSource release, Func<int> writes, string file) = await OccupyAllSlots(api);
        try
        {
            RemoteTransferSnapshot start = download
                ? await api.StartDownloadAsync(_session.ToString(), "/source", target, new() { OperationId = "approved" })
                : await api.StartUploadAsync(_session.ToString(), file, "/approved", new() { OperationId = "approved" });
            RemoteTransferSnapshot queued = await WaitQueuedAfterApproval(api, start.TransferId);

            RemoteTransferWithdrawResult result = await api.WithdrawPendingTransferAsync(start.TransferId, queued.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.PastApproval, result.Outcome);
            Assert.IsFalse(result.IsWithdrawn);
            Assert.IsFalse(result.Snapshot.CancellationRequested, "批准已经生效的,撤回什么都不改");
            Assert.AreEqual(queued.Sequence, result.Snapshot.Sequence);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.PastApproval,
                (await api.WithdrawPendingTransferAsync(start.TransferId, queued.Generation)).Outcome, "同一代再问,结论不变");

            release.TrySetResult();
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            Assert.IsFalse(done.CancellationRequested);
            if (download) CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(target));
            else await _sftp.Received(1).CommitUploadAsync(_session, Arg.Any<string>(), "/approved", false, Arg.Any<CancellationToken>());
            Assert.AreEqual(RemoteTransferWithdrawOutcome.PastApproval,
                (await api.WithdrawPendingTransferAsync(start.TransferId, done.Generation)).Outcome, "批准之后已到终态的也是 PastApproval");
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>批准之前就因为别的原因收尾了(这里是确认框里被拒):AlreadyEnded,快照原样,什么都不改。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WithdrawAfterTheTransferEndedBeforeApproval_IsAlreadyEnded(bool download)
    {
        string directory = NewDirectory("vela-withdraw-");
        string file = CreateFile(8);
        _sftp.GetFileInfoAsync(_session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        var prompts = new HeldPrompts();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompts.Prompt));
        try
        {
            RemoteTransferSnapshot start = await StartHeldAsync(api, download, file, directory, "denied");
            (await prompts.NextAsync()).Decision.TrySetResult(PluginPermissionDecision.Deny);
            RemoteTransferSnapshot denied = await Terminal(api, start.TransferId);
            Assert.AreEqual("permission-denied", denied.ErrorCode);

            RemoteTransferWithdrawResult result = await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.AlreadyEnded, result.Outcome);
            Assert.IsFalse(result.IsWithdrawn);
            Assert.AreEqual(denied, result.Snapshot, "已经收尾的,撤回什么都不改");
            await api.CancelTransferAsync(start.TransferId, start.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.AlreadyEnded,
                (await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation)).Outcome, "同一代再问,结论不变");
            await WaitIdle(api);
            await AssertNothingTransferredAsync();
        }
        finally { prompts.ReleaseAll(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 幂等与代次:同一代重复撤回结论不变(收尾前后都一样);代次不符(含 0)抛 <see cref="InvalidOperationException" />、什么都不改;
    /// 批准之前的 <see cref="RemoteFsCapability.CancelTransferAsync" /> 也算撤下(SDK 的判定顺序第二条)。
    /// </summary>
    [TestMethod]
    public async Task WithdrawIsIdempotentPerGenerationAndCountsAnEarlierCancel()
    {
        string file = CreateFile(8);
        var prompts = new HeldPrompts();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompts.Prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/idempotent", new() { OperationId = "idempotent" });
            await prompts.NextAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.WithdrawPendingTransferAsync(start.TransferId, 0));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.WithdrawPendingTransferAsync(start.TransferId, start.Generation + 1));
            RemoteTransferSnapshot untouched = await api.GetTransferAsync(start.TransferId);
            Assert.IsFalse(untouched.CancellationRequested, "代次不符的撤回什么都不改");

            RemoteTransferWithdrawResult once = await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
            RemoteTransferWithdrawResult twice = await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, once.Outcome);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, twice.Outcome);
            Assert.IsGreaterThanOrEqualTo(once.Snapshot.Sequence, twice.Snapshot.Sequence);
            AssertWithdrawnEnd(await Terminal(api, start.TransferId));
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, (await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation)).Outcome);

            RemoteTransferSnapshot cancelled = await api.StartUploadAsync(_session.ToString(), file, "/cancelled", new() { OperationId = "cancelled" });
            await api.CancelTransferAsync(cancelled.TransferId, cancelled.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn,
                (await api.WithdrawPendingTransferAsync(cancelled.TransferId, cancelled.Generation)).Outcome, "批准之前的取消也算撤下");
            AssertWithdrawnEnd(await Terminal(api, cancelled.TransferId));
            await WaitIdle(api);
            await AssertNothingTransferredAsync();
        }
        finally { prompts.ReleaseAll(); await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>无效 id、别的插件的 id 一律 <see cref="FileNotFoundException" />,不泄漏、也不碰那一项。</summary>
    [TestMethod]
    public async Task WithdrawRejectsUnknownIdsAndOtherPluginsTransfers()
    {
        string file = CreateFile(8);
        var prompts = new HeldPrompts();
        var gate = new PluginPermissionGate(null, prompts.Prompt);
        using var mine = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        using var other = new RemoteFsCapability(_sftp, _connections, pluginId: "test.other", uploadPermissions: gate);
        try
        {
            RemoteTransferSnapshot start = await mine.StartUploadAsync(_session.ToString(), file, "/mine", new() { OperationId = "mine" });
            await prompts.NextAsync();
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => other.WithdrawPendingTransferAsync(start.TransferId, start.Generation));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => mine.WithdrawPendingTransferAsync("not-a-transfer", 1));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => mine.WithdrawPendingTransferAsync(null!, 1));
            RemoteTransferSnapshot still = await mine.GetTransferAsync(start.TransferId);
            Assert.IsFalse(still.CancellationRequested, "别的插件撤不动这一项");
            Assert.AreEqual(RemoteTransferPhase.Approval, still.Phase);
            await mine.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
            await Terminal(mine, start.TransferId);
        }
        finally { prompts.ReleaseAll(); await WaitIdle(mine); File.Delete(file); }
    }

    /// <summary>
    /// 撤权之后仍可用于清理:还在等确认就被撤权的,收尾后撤回答 AlreadyEnded;快照按撤权口径脱敏(路径、会话、说明都不给)。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WithdrawAfterRevocationStillCleansUpWithARedactedSnapshot(bool download)
    {
        string directory = NewDirectory("vela-withdraw-");
        string file = CreateFile(8);
        _sftp.GetFileInfoAsync(_session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        var prompts = new HeldPrompts();
        var gate = new PluginPermissionGate(null, prompts.Prompt);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        try
        {
            RemoteTransferSnapshot start = await StartHeldAsync(api, download, file, directory, "revoked");
            Shown shown = await prompts.NextAsync();
            await gate.RevokeAsync("test.transfer");
            await WaitIdle(api);
            Assert.IsTrue(shown.Token.IsCancellationRequested);
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.GetTransferAsync(start.TransferId));

            RemoteTransferWithdrawResult result = await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.AlreadyEnded, result.Outcome);
            AssertRedacted(result.Snapshot);
            Assert.AreEqual("permission-revoked", result.Snapshot.ErrorCode);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.AlreadyEnded, (await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation)).Outcome);
            await AssertNothingTransferredAsync();
        }
        finally { prompts.ReleaseAll(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>已批准、正在传的上传被撤权:撤回答 PastApproval(撤权自己会停它),快照照样脱敏。</summary>
    [TestMethod]
    public async Task WithdrawAfterRevokingAnApprovedUploadIsPastApprovalAndRedacted()
    {
        string file = CreateFile(8);
        TaskCompletionSource started = NewSignal();
        TaskCompletionSource release = NewSignal();
        PluginPermissionGate gate = AllowUploads();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/running", new() { OperationId = "running" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gate.RevokeAsync("test.transfer");
            RemoteTransferWithdrawResult result = await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.PastApproval, result.Outcome);
            AssertRedacted(result.Snapshot);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 断线或撤权已经叫停了这一代、后台流程还没来得及发布终态,撤回恰在这一拍到达:撤回照判定顺序答 Withdrawn,
    /// 那么收尾也得是 Cancelled(回执已经告诉插件「撤下了、以 Cancelled 收尾」),不能再按先叫停的原因记成断线失败或撤权失败。
    /// 卡点:上传在后台流程问会话的那一下(批准之前的最后一步),下载在审批之前 stat 远端源的那一下。
    /// </summary>
    [TestMethod]
    [DataRow(false, "disconnect")]
    [DataRow(true, "disconnect")]
    [DataRow(false, "revoke")]
    [DataRow(true, "revoke")]
    public async Task WithdrawalLandingWhileAnotherStopIsStillUnwinding_EndsCancelled(bool download, string trigger)
    {
        string directory = NewDirectory("vela-withdraw-");
        string file = CreateFile(8);
        var gate = new PluginPermissionGate(null, AllowEverything());
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var withdrawal = new TaskCompletionSource<RemoteTransferWithdrawResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SshSession session = _connections.GetSession(_session)!;
        // 在后台流程里、批准之前:先让断线 / 撤权叫停这一代,再撤回 —— 两步都在后台流程发布终态之前完成。
        void StopThenWithdraw()
        {
            try
            {
                string id = started.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (trigger == "disconnect") _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(session);
                else gate.RevokeAsync("test.transfer").GetAwaiter().GetResult();
                withdrawal.TrySetResult(api.WithdrawPendingTransferAsync(id, 1).GetAwaiter().GetResult());
            }
            catch (Exception ex) { withdrawal.TrySetException(ex); }
        }
        if (download)
        {
            _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(_ => { StopThenWithdraw(); return SourceInfo(3); });
        }
        else
        {
            // 启动时登记前后各问一次会话(Resolve),后台流程审批之前问的是第三次。
            int asked = 0;
            _connections.GetSession(_session).Returns(_ =>
            {
                if (Interlocked.Increment(ref asked) == 3) StopThenWithdraw();
                return session;
            });
        }
        try
        {
            RemoteTransferSnapshot start = download
                ? await api.StartDownloadAsync(_session.ToString(), "/source", Path.Combine(directory, "target"), new() { OperationId = "unwinding" })
                : await api.StartUploadAsync(_session.ToString(), file, "/unwinding", new() { OperationId = "unwinding" });
            started.TrySetResult(start.TransferId);
            RemoteTransferWithdrawResult result = await withdrawal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, result.Outcome, "叫停了还没收尾,照判定顺序是本次撤下");
            await WaitIdle(api);
            // 撤权之后读不了快照,同一代的撤回仍可用于清理(脱敏),结论不变。
            RemoteTransferWithdrawResult again = await api.WithdrawPendingTransferAsync(start.TransferId, 1);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, again.Outcome);
            Assert.AreEqual(RemoteTransferState.Cancelled, again.Snapshot.State, $"{again.Snapshot.ErrorCode} {again.Snapshot.ErrorMessage}");
            Assert.AreEqual("cancelled", again.Snapshot.ErrorCode);
            Assert.IsTrue(again.Snapshot.RemoteStopConfirmed);
            Assert.IsTrue(again.Snapshot.DestinationCommitConfirmedAbsent);
            await AssertNothingTransferredAsync();
        }
        finally { started.TrySetResult(""); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 批准与撤回在同一拍竞争(用户点「允许」的同一刻插件发来撤回),上传与下载各撞若干轮。每一轮只许两种结局之一:
    /// PastApproval 且传输照常完成;或 Withdrawn 且一个字节都没传、以 Cancelled 收尾。
    /// 绝不出现「批准了又被撤」(PastApproval 却被取消)或「撤了还在传」(Withdrawn 却开始写)。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApprovalRacingWithdrawal_EitherCompletesNormallyOrNeverTransfersAByte(bool download)
    {
        const int rounds = 40;
        string directory = NewDirectory("vela-withdraw-race-");
        string file = CreateFile(8);
        byte[] content = [7, 8, 9, 10];
        var writes = new ConcurrentQueue<string>();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>())
            .Returns(call => { writes.Enqueue(call.ArgAt<string>(2)); return Task.CompletedTask; });
        _sftp.GetFileInfoAsync(_session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SourceInfo(content.Length));
        _sftp.DownloadStreamAsync(_session, Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                writes.Enqueue(call.ArgAt<string>(1));
                await call.ArgAt<Stream>(2).WriteAsync(content, call.ArgAt<CancellationToken>(4));
            });
        ConfigureWithdrawDigests(content, 8);
        // 批准就地在点「允许」的那个线程上继续往下走(不经线程池转一手),与撤回真正挤在同一拍里。
        var prompts = new HeldPrompts(inlineDecisions: true);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.raced", uploadPermissions: new(null, prompts.Prompt));
        int pastApproval = 0, withdrawn = 0;
        try
        {
            for (int i = 0; i < rounds; i++)
            {
                string source = $"/src-{i}", destination = $"/race-{i}/target", target = Path.Combine(directory, $"target-{i}");
                RemoteTransferSnapshot start = download
                    ? await api.StartDownloadAsync(_session.ToString(), source, target, new() { OperationId = $"race-{i}" })
                    : await api.StartUploadAsync(_session.ToString(), file, destination, new() { OperationId = $"race-{i}" });
                Shown shown = await prompts.NextAsync();
                using var barrier = new Barrier(2);
                Task approve = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    shown.Decision.TrySetResult(PluginPermissionDecision.AllowOnce);
                });
                Task<RemoteTransferWithdrawResult> withdraw = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    return api.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
                });
                await Task.WhenAll(approve, withdraw);
                RemoteTransferWithdrawResult result = await withdraw;
                RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
                await WaitIdle(api);
                bool wrote = writes.Any(path => download ? path == source : path.StartsWith($"/race-{i}/", StringComparison.Ordinal));
                switch (result.Outcome)
                {
                    case RemoteTransferWithdrawOutcome.PastApproval:
                        pastApproval++;
                        Assert.AreEqual(RemoteTransferState.Succeeded, done.State, $"第 {i} 轮批准先生效,却没有照常传完:{done.ErrorCode} {done.ErrorMessage}");
                        Assert.IsFalse(done.CancellationRequested, $"第 {i} 轮:批准了又被撤");
                        Assert.IsTrue(wrote, $"第 {i} 轮:批准先生效却没开始传");
                        break;
                    case RemoteTransferWithdrawOutcome.Withdrawn:
                        withdrawn++;
                        AssertWithdrawnEnd(done, $"第 {i} 轮");
                        Assert.IsFalse(wrote, $"第 {i} 轮:撤了还在传");
                        if (download) Assert.IsFalse(File.Exists(target), $"第 {i} 轮:撤下的下载落了目标");
                        break;
                    default:
                        Assert.Fail($"第 {i} 轮的结论只该是 PastApproval 或 Withdrawn,实际 {result.Outcome}");
                        break;
                }
                Assert.AreEqual(result.Outcome, (await api.WithdrawPendingTransferAsync(start.TransferId, start.Generation)).Outcome,
                    $"第 {i} 轮:同一代再问,结论变了");
            }
            Console.WriteLine($"[withdraw-race] {(download ? "download" : "upload")}: PastApproval {pastApproval}, Withdrawn {withdrawn}");
            Assert.AreEqual(rounds, pastApproval + withdrawn);
        }
        finally { prompts.ReleaseAll(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 隔离模式:握手宣告 <see cref="HandshakeResponse.SupportsTransferWithdraw" />,PluginHost 的代理经 <c>fs/transfer/withdraw</c>
    /// 把请求交给宿主、原样带回结论与快照(更新的宿主给的不认识的结论也照数值带回);宿主那边的异常类型照常还原;不改发取消。
    /// </summary>
    [TestMethod]
    public async Task IsolatedProxyRoutesWithdrawalAndTheHandshakeAdvertisesIt()
    {
        await using Pair pair = await Pair.Open();
        RemoteTransferSnapshot snapshot = new() { TransferId = "transfer", OperationId = "operation", SessionId = "s", LocalPath = "local", RemotePath = "/remote", Sequence = 4, Generation = 1, State = RemoteTransferState.CancelRequested, Phase = RemoteTransferPhase.Approval, CancellationRequested = true };
        var withdrawn = new RemoteTransferWithdrawResult { Outcome = RemoteTransferWithdrawOutcome.Withdrawn, Snapshot = snapshot };
        var future = new RemoteTransferWithdrawResult { Outcome = (RemoteTransferWithdrawOutcome)42, Snapshot = snapshot with { TransferId = "future" } };
        IRemoteFsApi fake = Substitute.For<IRemoteFsApi>();
        fake.SupportsTransferOperations.Returns(true);
        fake.SupportsTransferWithdraw.Returns(true);
        fake.WithdrawPendingTransferAsync("transfer", 1, Arg.Any<CancellationToken>()).Returns(withdrawn);
        fake.WithdrawPendingTransferAsync("transfer", 2, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RemoteTransferWithdrawResult>(new InvalidOperationException("Transfer generation does not match.")));
        fake.WithdrawPendingTransferAsync("future", 1, Arg.Any<CancellationToken>()).Returns(future);
        using PluginContext context = Context(fake);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        var methods = new ConcurrentQueue<string>();
        pair._host.SetRequestHandler((method, payload, ct) => { methods.Enqueue(method); return router.HandleRequestAsync(method, payload, ct); });
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        Assert.IsTrue(hello!.SupportsTransferWithdraw);
        var proxy = new RpcRemoteFs(pair._plugin, hello.SupportsTransferOperations, hello.SupportsDownloadOperations, hello.SupportsTransferWithdraw);
        Assert.IsTrue(proxy.SupportsTransferWithdraw);

        RemoteTransferWithdrawResult result = await proxy.WithdrawPendingTransferAsync("transfer", 1);
        Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, result.Outcome);
        Assert.AreEqual(snapshot, result.Snapshot);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => proxy.WithdrawPendingTransferAsync("transfer", 2));
        RemoteTransferWithdrawResult unknown = await proxy.WithdrawPendingTransferAsync("future", 1);
        Assert.AreEqual((RemoteTransferWithdrawOutcome)42, unknown.Outcome);
        Assert.IsFalse(unknown.IsWithdrawn, "不认识的结论不算撤下");
        Assert.IsTrue(methods.Contains(PluginRpc.FsTransferWithdraw));
        Assert.IsFalse(methods.Contains(PluginRpc.FsTransferCancel), "条件撤回不改发无条件取消");
        await fake.DidNotReceive().CancelTransferAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 握手没宣告(宿主不支持、或更旧的宿主)时,PluginHost 在插件进程里直接抛 <see cref="NotSupportedException" />:
    /// 一个请求都不发,更不退化成 <c>fs/transfer/cancel</c>。
    /// </summary>
    [TestMethod]
    public async Task HostWithoutWithdrawalIsNeverAskedAndThePluginSideThrowsNotSupported()
    {
        await using Pair pair = await Pair.Open();
        IRemoteFsApi fake = Substitute.For<IRemoteFsApi>();
        fake.SupportsTransferOperations.Returns(true);
        using PluginContext context = Context(fake);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        var methods = new ConcurrentQueue<string>();
        pair._host.SetRequestHandler((method, payload, ct) => { methods.Enqueue(method); return router.HandleRequestAsync(method, payload, ct); });
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        Assert.IsFalse(hello!.SupportsTransferWithdraw);
        var proxy = new RpcRemoteFs(pair._plugin, hello.SupportsTransferOperations, hello.SupportsDownloadOperations, hello.SupportsTransferWithdraw);
        Assert.IsFalse(proxy.SupportsTransferWithdraw);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.WithdrawPendingTransferAsync("transfer", 1));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => new RpcRemoteFs(pair._plugin, true).WithdrawPendingTransferAsync("transfer", 1));
        Assert.AreSequenceEqual([PluginRpc.Handshake], [.. methods], "握手之后一个请求都没发");
        await fake.DidNotReceive().WithdrawPendingTransferAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await fake.DidNotReceive().CancelTransferAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>隔离模式整链:真实的能力实例在路由后面,插件进程的代理撤回一项等确认的上传,确认框关掉、以 Cancelled 收尾。</summary>
    [TestMethod]
    public async Task IsolatedWithdrawalReachesTheRealCapability()
    {
        string file = CreateFile(8);
        var prompts = new HeldPrompts();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompts.Prompt));
        await using Pair pair = await Pair.Open();
        using PluginContext context = Context(api);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        try
        {
            HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
                new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
            Assert.IsTrue(hello!.SupportsTransferWithdraw, "宿主的能力实例宣告支持");
            var proxy = new RpcRemoteFs(pair._plugin, hello.SupportsTransferOperations, hello.SupportsDownloadOperations, hello.SupportsTransferWithdraw);
            RemoteTransferSnapshot start = await proxy.StartUploadAsync(_session.ToString(), file, "/isolated", new() { OperationId = "isolated" });
            Shown shown = await prompts.NextAsync();

            RemoteTransferWithdrawResult result = await proxy.WithdrawPendingTransferAsync(start.TransferId, start.Generation);
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, result.Outcome);
            Assert.IsTrue(result.Snapshot.CancellationRequested);
            Assert.IsTrue(shown.Token.IsCancellationRequested, "确认框随撤回关掉");
            AssertWithdrawnEnd(await Terminal(api, start.TransferId));
            Assert.AreEqual(RemoteTransferWithdrawOutcome.Withdrawn, (await proxy.WithdrawPendingTransferAsync(start.TransferId, start.Generation)).Outcome);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => proxy.WithdrawPendingTransferAsync(start.TransferId, 2));
            await AssertNothingTransferredAsync();
        }
        finally { prompts.ReleaseAll(); await WaitIdle(api); File.Delete(file); }
    }

    private static void AssertWithdrawnEnd(RemoteTransferSnapshot done, string? round = null)
    {
        string prefix = round is null ? "" : round + ":";
        Assert.AreEqual(RemoteTransferState.Cancelled, done.State, $"{prefix}{done.ErrorCode} {done.ErrorMessage}");
        Assert.AreEqual("cancelled", done.ErrorCode, prefix);
        Assert.IsTrue(done.RemoteStopConfirmed, prefix + "撤下的这一代确认没写");
        Assert.IsTrue(done.DestinationCommitConfirmedAbsent, prefix);
        Assert.IsTrue(done.CancellationRequested, prefix);
        Assert.AreEqual(0, done.TransferredBytes, prefix + "一个字节都没传");
        Assert.AreEqual(RemoteTransferPhase.Approval, done.Phase, prefix + "批准从没生效");
    }

    private static void AssertRedacted(RemoteTransferSnapshot snapshot)
    {
        Assert.AreEqual("", snapshot.LocalPath);
        Assert.AreEqual("", snapshot.RemotePath);
        Assert.AreEqual("", snapshot.SessionId);
        Assert.IsNull(snapshot.ErrorMessage);
    }

    private async Task AssertNothingTransferredAsync()
    {
        await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
        await _sftp.DidNotReceive().DownloadStreamAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>());
        await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>上传传 <paramref name="file" /> 到 <c>/name</c>;下载把 <c>/name</c> 下到 <paramref name="directory" /> 里的同名文件。</summary>
    private Task<RemoteTransferSnapshot> StartHeldAsync(RemoteFsCapability api, bool download, string file, string directory, string name)
        => download
            ? api.StartDownloadAsync(_session.ToString(), "/" + name, Path.Combine(directory, name), new() { OperationId = name })
            : api.StartUploadAsync(_session.ToString(), file, "/" + name, new() { OperationId = name });

    /// <summary>远端摘要:下载源(<c>/source</c>、<c>/src-*</c>)按给的内容,其余(上传的临时文件)按全零的上传源。</summary>
    private void ConfigureWithdrawDigests(byte[] download, int uploadLength)
    {
        string downloaded = Convert.ToHexString(SHA256.HashData(download)).ToLowerInvariant();
        string uploaded = Convert.ToHexString(SHA256.HashData(new byte[uploadLength])).ToLowerInvariant();
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string path = call.ArgAt<IReadOnlyList<string>>(1)[0];
            bool source = path == "/source" || path.StartsWith("/src-", StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [path] = source ? downloaded : uploaded });
        });
    }

    private sealed record Shown(string Remote, TaskCompletionSource<PluginPermissionDecision> Decision, CancellationToken Token);

    /// <summary>
    /// 仿宿主确认框:弹出时记下(连同闸交给它的令牌 —— 真实的框随令牌关掉),由用例决定点什么;
    /// 构造时要求 inlineDecisions 的,点下去的那个线程就地接着跑批准(不经线程池转一手)。
    /// </summary>
    private sealed class HeldPrompts
    {
        private readonly ConcurrentQueue<Shown> _shown = new();
        private readonly ConcurrentQueue<Shown> _all = new();
        private readonly bool _inlineDecisions;
        private int _count;

        internal HeldPrompts(bool inlineDecisions = false)
        {
            _inlineDecisions = inlineDecisions;
            Prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(call => Hold(call.ArgAt<string>(3), call.ArgAt<CancellationToken>(6)));
            Prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(call => Hold(call.ArgAt<string>(2), call.ArgAt<CancellationToken>(6)));
        }

        internal IPluginPermissionPrompt Prompt { get; } = Substitute.For<IPluginPermissionPrompt>();

        internal int Count => Volatile.Read(ref _count);

        private Task<PluginPermissionDecision> Hold(string remote, CancellationToken token)
        {
            var decision = new TaskCompletionSource<PluginPermissionDecision>(
                _inlineDecisions ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new Shown(remote, decision, token);
            Interlocked.Increment(ref _count);
            _all.Enqueue(shown);
            _shown.Enqueue(shown);
            return decision.Task;
        }

        internal async Task<Shown> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Shown? shown;
            while (!_shown.TryDequeue(out shown)) await Task.Delay(1, timeout.Token);
            return shown;
        }

        /// <summary>收尾:还挂着的框一律按拒绝放掉,不留永远不完成的等待。</summary>
        internal void ReleaseAll()
        {
            foreach (Shown shown in _all) shown.Decision.TrySetResult(PluginPermissionDecision.Deny);
        }
    }
}
