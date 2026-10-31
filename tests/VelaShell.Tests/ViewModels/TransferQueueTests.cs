using Avalonia.Headless;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 传输队列持久化在面板上的样子:断线停下的批次挂成「已暂停」、「继续」从半截接着传、
/// 配置删了只能放弃、重启之后只还原不外连、用户取消就不再记着。
/// </summary>
[TestClass]
[TestCategory("FileTransfer")]
public sealed class TransferQueueTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TransferQueueTests).Assembly);

    /// <summary>
    /// 一批下载传到第二个文件时连接没了:那一个与后面的都不再去撞,整批挂成一行「已暂停」,
    /// 剩下的两个记在队列里;断线停下的文件行由这一行代表,不留红行。
    /// </summary>
    [TestMethod]
    public Task LosingTheConnectionMidBatch_PausesTheRestInsteadOfFailingThem() => OnUi(async () =>
    {
        using var rig = new Rig();
        rig.Fails("/srv/b.bin", new RemoteSessionUnavailableException("Session gone"));

        await rig.DownloadAsync("a", "b", "c");

        Assert.AreSequenceEqual(["/srv/a.bin", "/srv/b.bin"], [.. rig.Downloaded(rig.First)], "c 不再开始");
        PausedTransferBatchViewModel paused = rig.Sink.PausedBatches.Single();
        Assert.AreSequenceEqual(["/srv/b.bin", "/srv/c.bin"], [.. paused.Batch.Items.Select(i => i.RemotePath)]);
        Assert.AreEqual(PausedBatchReason.Interrupted, paused.Reason);
        Assert.AreEqual(Strings.Format("Transfer_PausedFiles", 2), paused.Title);
        Assert.IsTrue(paused.CanResume);
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status, "只剩 a 那一行;b 由暂停的批次代表");
        Assert.IsTrue(rig.Sink.IsPanelVisible);
    });

    /// <summary>临时失败把重试次数用完,同样整批停下(链路多半是坏了),而不是一个接一个地失败。</summary>
    [TestMethod]
    public Task ExhaustingRetriesOnATransientFailure_AlsoPausesTheBatch() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 1);
        rig.Fails("/srv/b.bin", new VelaSshConnectionException("连接被重置"));

        await rig.DownloadAsync("a", "b", "c");

        Assert.AreEqual(2, rig.Downloaded(rig.First).Count(p => p == "/srv/b.bin"), "首次 + 重试 1 次");
        Assert.AreEqual(2, rig.Sink.PausedBatches.Single().RemainingCount);
    });

    /// <summary>永久失败是这个文件自己的问题:标红、不进队列,其余照传。</summary>
    [TestMethod]
    public Task APermanentFailure_FailsThatFileAndTheBatchCarriesOn() => OnUi(async () =>
    {
        using var rig = new Rig();
        rig.Fails("/srv/b.bin", new VelaSftpPermissionDeniedException("Permission denied"));

        await rig.DownloadAsync("a", "b", "c");

        Assert.AreSequenceEqual(["/srv/a.bin", "/srv/b.bin", "/srv/c.bin"], [.. rig.Downloaded(rig.First)]);
        Assert.IsEmpty(rig.Sink.PausedBatches);
        Assert.ContainsSingle(t => t.Status == TransferStatus.Failed, rig.Sink.Transfers);
    });

    /// <summary>不知道来自哪条连接的面板(占位面板之类)不记队列:断线照旧是失败,恢复时无处可连。</summary>
    [TestMethod]
    public Task APanelWithoutAConnectionProfile_KeepsTheOldFailureBehaviour() => OnUi(async () =>
    {
        using var rig = new Rig(withOrigin: false);
        rig.Fails("/srv/b.bin", new RemoteSessionUnavailableException("Session gone"));

        await rig.DownloadAsync("a", "b", "c");

        Assert.IsEmpty(rig.Sink.PausedBatches);
        Assert.HasCount(3, rig.Downloaded(rig.First), "没有队列可停,剩下的照旧逐个去试");
        Assert.Contains(t => t.Status == TransferStatus.Failed, rig.Sink.Transfers);
    });

    /// <summary>
    /// 「继续」:换到一条新的连接上,半截从断点接着传,没开始的整份传;同名冲突不再问一遍(规划时问过了);
    /// 交接之后暂停的那一行撤掉,队列里也不再有它。
    /// </summary>
    [TestMethod]
    public Task ResumingContinuesTheRemainingFilesOnANewConnection_FromTheirPartials() => OnUi(async () =>
    {
        using var rig = new Rig();
        rig.Fails("/srv/b.bin", new RemoteSessionUnavailableException("Session gone"), writtenBeforeFailure: 10);
        await rig.DownloadAsync("a", "b", "c");
        PausedTransferBatchViewModel paused = rig.Sink.PausedBatches.Single();

        // c 在本地已经有一份同样大小的(比如上次传完了还没来得及记账):不是半截,
        // 按规划时定下的覆盖整份重传,不再弹同名冲突框。
        await File.WriteAllBytesAsync(Path.Combine(rig.Directory, "c.bin"), new byte[100]);
        // 重连之后是一个新会话、一个新面板(终端标签重连就是这样)。
        var second = Guid.NewGuid();
        rig.Remote(second, "/srv/b.bin", 100);
        rig.Remote(second, "/srv/c.bin", 100);
        FileBrowserViewModel reconnected = rig.Panel(second);
        // 「跳过」策略下照常规路径走,c 会被跳过;接着传不再过冲突策略,c 照规划时定下的覆盖。
        reconnected.TransferOptions.ConflictPolicy = "skip";
        reconnected.ConfirmOverwrite = _ => throw new AssertFailedException("接着传不该再弹同名冲突框。");
        var done = new TaskCompletionSource<TransferResumeOutcome>();
        rig.Sink.ResumeHandler = async batch =>
        {
            TransferResumeOutcome outcome = await reconnected.ResumeQueuedTransfersAsync(batch)
                ? new(TransferResumeResult.Started)
                : new(TransferResumeResult.Failed, reconnected.ErrorMessage);
            done.SetResult(outcome);
            return outcome;
        };

        rig.Sink.ResumePausedBatchCommand.Execute(paused.BatchId).Subscribe();
        Assert.AreEqual(TransferResumeResult.Started, (await done.Task.WaitAsync(TimeSpan.FromSeconds(10))).Result);

        Assert.AreSequenceEqual(
            [("/srv/b.bin", 10L), ("/srv/c.bin", 0L)], [.. rig.DownloadCalls(second)], "b 从本地那 10 字节接着下,c 整份下(不按冲突策略跳过)");
        Assert.IsEmpty(rig.Sink.PausedBatches);
        Assert.IsNull(rig.Sink.Journal.Snapshot(paused.BatchId));
    });

    /// <summary>
    /// 暂停到「继续」之间源变了(FTP 这类后端同样核实续传起点,对不上抛 VelaSftpResumeMismatchException):
    /// 不往那半截后面追加,而是按规划时定下的覆盖整份重传 —— 「询问」策略下也不再弹框(冲突在规划时问过了)。
    /// </summary>
    [TestMethod]
    public Task ResumingAfterThePartialStoppedMatching_RetransfersTheWholeFileInsteadOfAppending() => OnUi(async () =>
    {
        using var rig = new Rig();
        rig.Fails("/srv/b.bin", new RemoteSessionUnavailableException("Session gone"), writtenBeforeFailure: 10);
        await rig.DownloadAsync("a", "b");
        PausedTransferBatchViewModel paused = rig.Sink.PausedBatches.Single();

        var second = Guid.NewGuid();
        rig.Remote(second, "/srv/b.bin", 100);
        rig.Sftp.DownloadFileAsync(second, "/srv/b.bin", Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Is<long>(offset => offset > 0), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new VelaSftpResumeMismatchException("changed while paused")));
        FileBrowserViewModel reconnected = rig.Panel(second);
        reconnected.TransferOptions.ConflictPolicy = "ask";
        reconnected.ConfirmOverwrite = _ => throw new AssertFailedException("接着传不该再弹同名冲突框。");
        var done = new TaskCompletionSource<TransferResumeOutcome>();
        rig.Sink.ResumeHandler = async batch =>
        {
            TransferResumeOutcome outcome = await reconnected.ResumeQueuedTransfersAsync(batch)
                ? new(TransferResumeResult.Started)
                : new(TransferResumeResult.Failed, reconnected.ErrorMessage);
            done.SetResult(outcome);
            return outcome;
        };

        rig.Sink.ResumePausedBatchCommand.Execute(paused.BatchId).Subscribe();
        Assert.AreEqual(TransferResumeResult.Started, (await done.Task.WaitAsync(TimeSpan.FromSeconds(10))).Result);

        Assert.AreSequenceEqual([("/srv/b.bin", 10L), ("/srv/b.bin", 0L)], [.. rig.DownloadCalls(second)], "对不上就整份重下,不往半截后面追加");
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Last(t => t.FileName == "b.bin").Status);
        Assert.IsEmpty(rig.Sink.PausedBatches);
    });

    /// <summary>配置删了:不去猜它原来连的是哪台,如实说明,只能放弃;放弃之后队列里也没了。</summary>
    [TestMethod]
    public Task WhenTheProfileIsGone_TheBatchCanOnlyBeDiscarded() => OnUi(async () =>
    {
        using var rig = new Rig();
        rig.Fails("/srv/b.bin", new RemoteSessionUnavailableException("Session gone"));
        await rig.DownloadAsync("a", "b");
        PausedTransferBatchViewModel paused = rig.Sink.PausedBatches.Single();
        var answered = new TaskCompletionSource();
        rig.Sink.ResumeHandler = _ =>
        {
            answered.SetResult();
            return Task.FromResult(new TransferResumeOutcome(TransferResumeResult.ProfileMissing));
        };

        rig.Sink.ResumePausedBatchCommand.Execute(paused.BatchId).Subscribe();
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Yield();

        Assert.IsFalse(paused.CanResume);
        Assert.Contains(Strings.Get("Transfer_PausedProfileMissing"), paused.Detail);

        rig.Sink.DiscardPausedBatchCommand.Execute(paused.BatchId).Subscribe();
        Assert.IsEmpty(rig.Sink.PausedBatches);
        Assert.IsNull(rig.Sink.Journal.Snapshot(paused.BatchId));
    });

    /// <summary>没连上(用户取消了登录框之类):原样留着,可以再点。</summary>
    [TestMethod]
    public Task WhenTheConnectionCannotBeMade_TheBatchStaysPausedAndCanBeRetried() => OnUi(async () =>
    {
        using var rig = new Rig();
        rig.Fails("/srv/b.bin", new RemoteSessionUnavailableException("Session gone"));
        await rig.DownloadAsync("a", "b");
        PausedTransferBatchViewModel paused = rig.Sink.PausedBatches.Single();
        var answered = new TaskCompletionSource();
        rig.Sink.ResumeHandler = _ =>
        {
            answered.TrySetResult();
            return Task.FromResult(new TransferResumeOutcome(TransferResumeResult.NotConnected));
        };

        rig.Sink.ResumePausedBatchCommand.Execute(paused.BatchId).Subscribe();
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Yield();

        Assert.IsTrue(paused.CanResume);
        Assert.IsFalse(paused.IsResuming);
        Assert.AreSame(paused, rig.Sink.PausedBatches.Single());
    });

    /// <summary>用户取消 = 不要了:不挂暂停行,队列里也删掉。</summary>
    [TestMethod]
    public Task CancellingABatch_ForgetsIt() => OnUi(async () =>
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        using var rig = new Rig(store: store);
        var started = new TaskCompletionSource();
        rig.Sftp.DownloadFileAsync(rig.First, "/srv/a.bin", Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(5));
            });

        Task download = rig.DownloadAsync("a", "b");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Sink.CancelAllCommand.Execute().Subscribe();
        await download.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Sink.Journal.FlushAsync();

        Assert.IsEmpty(rig.Sink.PausedBatches);
        // 批次开始时那一份可能已经落过盘(攒够一秒就落),取消之后的最后一笔必须是删掉它。
        var lastWrite = new Dictionary<string, string>();
        foreach (NSubstitute.Core.ICall call in store.ReceivedCalls())
        {
            object?[] args = call.GetArguments();
            if (args.Length > 1 && Equals(args[0], TransferQueueJournal.Collection) && args[1] is string id)
            {
                lastWrite[id] = call.GetMethodInfo().Name;
            }
        }
        Assert.IsNotEmpty(lastWrite);
        Assert.IsTrue(lastWrite.Values.All(m => m == nameof(IAppDataStore.DeleteAsync)), string.Join(", ", lastWrite));
    });

    /// <summary>
    /// 浮窗那一行的 ×:只停这一项,这一批其余照传;停下的这一项从队列里摘掉 ——
    /// 之后这一批断线停下,「已暂停」那一行里只剩真正没传的,点「继续」也不会再传它。
    /// </summary>
    [TestMethod]
    public Task CancellingOneRow_StopsOnlyThatFile_AndDropsItFromTheQueue() => OnUi(async () =>
    {
        using var rig = new Rig();
        Task<CancellationToken> bStarted = rig.Hangs("/srv/b.bin");
        rig.Fails("/srv/c.bin", new RemoteSessionUnavailableException("Session gone"));

        Task download = rig.DownloadAsync("a", "b", "c", "d");
        CancellationToken bToken = await bStarted.WaitAsync(TimeSpan.FromSeconds(10));
        TransferItemViewModel b = rig.Row("b.bin");
        Assert.IsTrue(b.CanCancel, "在传的那一行有 ×");
        rig.Sink.CancelTransferCommand.Execute(b.Id).Subscribe();
        await download.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(bToken.IsCancellationRequested, "停的是 b 自己那一次传输,不只是行上改个字");
        Assert.AreEqual(TransferStatus.Cancelled, b.Status);
        Assert.IsFalse(b.CanCancel);
        Assert.AreSequenceEqual(["/srv/a.bin", "/srv/b.bin", "/srv/c.bin"], [.. rig.Downloaded(rig.First)], "b 停下之后这一批接着传 c(c 断线,整批才停)");
        Assert.AreEqual(TransferStatus.Completed, rig.Row("a.bin").Status);
        PausedTransferBatchViewModel paused = rig.Sink.PausedBatches.Single();
        Assert.AreSequenceEqual(["/srv/c.bin", "/srv/d.bin"], [.. paused.Batch.Items.Select(i => i.RemotePath)], "取消了的 b 不再记在队列里,「继续」不会再传它");
    });

    /// <summary>
    /// 并发传输时同样只停那一项:另一条在传的不受牵连,停下的那个名额接着传下一个;
    /// 这一批照常记账 —— 之后断线停下时,队列里剩的是真正没传完的那些。
    /// </summary>
    [TestMethod]
    public Task CancellingOneRow_WhileOthersRunConcurrently_LeavesThemRunning() => OnUi(async () =>
    {
        using var rig = new Rig(concurrency: 2);
        Task<CancellationToken> aStarted = rig.Hangs("/srv/a.bin");
        rig.Fails("/srv/c.bin", new RemoteSessionUnavailableException("Session gone"));
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Sftp.DownloadFileAsync(rig.First, "/srv/b.bin", Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                bStarted.TrySetResult(call.ArgAt<CancellationToken>(5));
                await releaseB.Task;
            });

        Task download = rig.DownloadAsync("a", "b", "c");
        CancellationToken aToken = await aStarted.WaitAsync(TimeSpan.FromSeconds(10));
        CancellationToken bToken = await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Sink.CancelTransferCommand.Execute(rig.Row("a.bin").Id).Subscribe();
        Assert.IsTrue(aToken.IsCancellationRequested);
        Assert.IsFalse(bToken.IsCancellationRequested, "同一批里另一条在传的不受牵连");
        releaseB.SetResult();
        await download.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(TransferStatus.Cancelled, rig.Row("a.bin").Status);
        Assert.AreEqual(TransferStatus.Completed, rig.Row("b.bin").Status);
        CollectionAssert.Contains(rig.Downloaded(rig.First), "/srv/c.bin", "a 停下之后它那个名额接着传 c");
        // c 断线,整批停下:队列里只剩 c —— a 取消了、b 传完了,都已摘掉。
        // 要是 a 的取消漏成整批取消,这一批就被当成「不要了」整个从队列里删掉,这一行根本不会出现。
        PausedTransferBatchViewModel paused = rig.Sink.PausedBatches.Single();
        Assert.AreSequenceEqual(["/srv/c.bin"], [.. paused.Batch.Items.Select(i => i.RemotePath)]);
        Assert.IsFalse(rig.Sink.IsBatchActive, "停一项不该让批次挂在那里收不了尾");
    });

    /// <summary>
    /// 重启之后:上次没传完的批次还原成「已暂停」,但<b>不外连</b> —— 一个字节都不传,连也不连,等用户点「继续」。
    /// </summary>
    [TestMethod]
    public Task AfterARestart_UnfinishedBatchesAreRestoredAsPaused_WithoutConnecting() => OnUi(async () =>
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var batchId = Guid.NewGuid();
        store.GetAllAsync<TransferQueueChunk>(TransferQueueJournal.Collection, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TransferQueueChunk>
            {
                new()
                {
                    BatchId = batchId,
                    ProfileId = Guid.NewGuid(),
                    ServerName = "prod-01",
                    CreatedUtc = DateTime.UtcNow.AddHours(-3),
                    Items = [new() { Index = 4, Direction = "upload", LocalPath = "C:/backup/db.dump", RemotePath = "/srv/db.dump" }],
                },
            }));
        int resumeCalls = 0;

        var sink = new FileTransferViewModel(store)
        {
            ResumeHandler = _ =>
            {
                resumeCalls++;
                return Task.FromResult(new TransferResumeOutcome(TransferResumeResult.Started));
            },
        };
        for (int i = 0; i < 100 && sink.PausedBatches.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        PausedTransferBatchViewModel restored = sink.PausedBatches.Single();
        Assert.AreEqual(batchId, restored.BatchId);
        Assert.AreEqual(PausedBatchReason.Restored, restored.Reason);
        Assert.AreEqual("db.dump", restored.Title);
        Assert.Contains("prod-01", restored.Detail);
        Assert.Contains(Strings.Get("Transfer_PausedRestored"), restored.Detail);
        Assert.IsTrue(sink.IsPanelVisible, "要用户处理的东西,启动时亮出来");
        Assert.AreEqual(0, resumeCalls, "启动时不替用户去连");
    });

    /// <summary>目录同步的批次不记队列(计划是在预览里逐条确认的,重启之后该重新比较)。</summary>
    [TestMethod]
    public Task DirectorySyncBatches_AreNotQueued() => OnUi(async () =>
    {
        using var rig = new Rig();
        rig.Fails("/srv/b.bin", new RemoteSessionUnavailableException("Session gone"));
        FileBrowserViewModel panel = rig.Panel(rig.First);

        await panel.RunSyncTransfersAsync(
            [
                new(TransferType.Download, Path.Combine(rig.Directory, "a.bin"), "/srv/a.bin"),
                new(TransferType.Download, Path.Combine(rig.Directory, "b.bin"), "/srv/b.bin"),
            ],
            settled: null,
            CancellationToken.None);

        Assert.IsEmpty(rig.Sink.PausedBatches);
        Assert.Contains(t => t.Status == TransferStatus.Failed, rig.Sink.Transfers);
    });

    /// <summary>
    /// 目录同步里点了一行的 ×:同步其余照做、不算整个同步被取消;这一项记「已取消」——
    /// 不拿 ErrorMessage 顶替(那是别的文件留下的,或者没有,就成了「传输失败」)。
    /// </summary>
    [TestMethod]
    public Task CancellingOneSyncRow_RecordsItAsCancelled_AndTheSyncCarriesOn() => OnUi(async () =>
    {
        using var rig = new Rig();
        Task<CancellationToken> aStarted = rig.Hangs("/srv/a.bin");
        var runner = new DirectorySyncRunner(rig.Sftp, rig.First, rig.Panel(rig.First));

        Task<DirectorySyncResult> sync = runner.ExecuteAsync(rig.Directory, "/srv", [Download("a.bin"), Download("b.bin")], null, CancellationToken.None);
        await aStarted.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Sink.CancelTransferCommand.Execute(rig.Row("a.bin").Id).Subscribe();
        DirectorySyncResult result = await sync.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(result.Cancelled, "停一项不是取消整个同步");
        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(1, result.Failed);
        Assert.AreSequenceEqual([$"a.bin: {Strings.Get("Sync_Cancelled")}"], [.. result.Errors]);
        CollectionAssert.Contains(rig.Downloaded(rig.First), "/srv/b.bin");

        // 远端时间精度记成未知:替身不真的写文件,别让「对齐修改时间」去碰一个不存在的本地文件。
        static SyncAction Download(string name) => new(SyncActionKind.Download, name, false, null,
            new SyncItem(name, "/srv/" + name, false, 100, DateTime.UtcNow, SyncTimePrecision.Unknown));
    });

    private static Task OnUi(Func<Task> body) => _session.RunOnUiAsync(body);

    private sealed class Rig : IDisposable
    {
        private readonly bool _withOrigin;
        private readonly int _retries;
        private readonly int _concurrency;
        private FileBrowserViewModel? _first;

        public Rig(int retries = 0, bool withOrigin = true, IAppDataStore? store = null, int concurrency = 1)
        {
            _retries = retries;
            _withOrigin = withOrigin;
            _concurrency = concurrency;
            Sink = new FileTransferViewModel(store);
            System.IO.Directory.CreateDirectory(Directory);
        }

        public Guid ProfileId { get; } = Guid.NewGuid();

        public Guid First { get; } = Guid.NewGuid();

        public ISftpService Sftp { get; } = Substitute.For<ISftpService>();

        public FileTransferViewModel Sink { get; }

        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vela-queue-" + Guid.NewGuid().ToString("N"));

        public FileBrowserViewModel Panel(Guid session) => new(Sftp, session)
        {
            TransferSink = Sink,
            QueueOrigin = _withOrigin ? new(ProfileId, "prod-01") : null,
            TransferOptions = new()
            {
                ConflictPolicy = "overwrite",
                ResumeEnabled = true,
                AutoCleanTempFiles = false,
                TransferLogging = false,
                NotifyOnComplete = false,
                MaxConcurrentTransfers = _concurrency,
                TransferMaxRetries = _retries,
            },
            RetryDelay = (_, _) => Task.CompletedTask,
        };

        public void Remote(Guid session, string path, long size) =>
            Sftp.GetFileInfoAsync(session, path, Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                Size = size,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                Owner = "u",
                Group = "g",
            });

        /// <summary>
        /// 这个远端文件在第一个会话上的下载一直传着、直到它的取消令牌被触发;返回的任务在下载开始时完成。
        /// 也记下那个令牌,好断言取消的是不是这一项自己的那个。
        /// </summary>
        public Task<CancellationToken> Hangs(string remotePath)
        {
            var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            Sftp.DownloadFileAsync(First, remotePath, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                    Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    CancellationToken token = call.ArgAt<CancellationToken>(5);
                    started.TrySetResult(token);
                    await Task.Delay(Timeout.Infinite, token);
                });
            return started.Task;
        }

        /// <summary>浮窗上某个文件的那一行。</summary>
        public TransferItemViewModel Row(string fileName) => Sink.Transfers.Single(t => t.FileName == fileName);

        /// <summary>这个远端文件在第一个会话上下载失败;失败之前先在本地写下 <paramref name="writtenBeforeFailure" /> 字节。</summary>
        public void Fails(string remotePath, Exception failure, long writtenBeforeFailure = 0) =>
            Sftp.DownloadFileAsync(First, remotePath, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                    Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (writtenBeforeFailure > 0)
                    {
                        File.WriteAllBytes(call.ArgAt<string>(2), new byte[writtenBeforeFailure]);
                    }
                    return Task.FromException(failure);
                });

        public Task DownloadAsync(params string[] names)
        {
            _first ??= Panel(First);
            return _first.DownloadRemoteEntriesAsync(
                [
                    .. names.Select(n => new RemoteFileInfoViewModel(new RemoteFileInfo
                    {
                        Name = n + ".bin",
                        FullPath = $"/srv/{n}.bin",
                        Size = 100,
                        Permissions = "-rw-r--r--",
                        IsDirectory = false,
                        LastModified = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                        Owner = "u",
                        Group = "g",
                    })),
                ],
                Directory);
        }

        public List<string> Downloaded(Guid session) => [.. DownloadCalls(session).Select(c => c.Remote)];

        public List<(string Remote, long Offset)> DownloadCalls(Guid session) =>
        [
            .. Sftp.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ISftpService.DownloadFileAsync) && (Guid)c.GetArguments()[0]! == session)
                .Select(c => ((string)c.GetArguments()[1]!, (long)c.GetArguments()[4]!)),
        ];

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
