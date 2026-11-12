using Avalonia.Headless;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 传输失败自动重试(设置 → 文件传输 → 失败自动重试,<c>TransferMaxRetries</c>):
/// 临时失败按次数重试、带退避;永久失败直接失败;重试时续传与否跟着「恢复中断的传输」走。
/// </summary>
/// <remarks>
/// 跑在 headless UI 线程上,理由见 <see cref="GlobalTransferConcurrencyTests" />:传输面板的记账要求续体回到 UI 线程。
/// </remarks>
[TestClass]
[TestCategory("FileTransfer")]
public sealed class TransferRetryTests
{
    private const string RemoteFile = "/data/a.bin";
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TransferRetryTests).Assembly);

    [TestMethod]
    public Task ATransientFailure_IsRetriedWithBackoff_UntilItGoesThrough() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3);
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException(new VelaSshConnectionException("连接被重置")),
                _ => Task.FromException(new VelaSshOperationTimeoutException("超时")),
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.HasCount(3, rig.DownloadCalls(), "失败两次、第三次成功");
        Assert.AreSequenceEqual([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], rig.Delays, "翻倍退避");
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status);
        Assert.IsNull(rig.Browser.ErrorMessage);
    });

    [TestMethod]
    public Task RetriesStopAtTheConfiguredLimit_AndTheErrorSaysHowManyWereTried() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 2);
        var failure = new VelaSshConnectionException("连接被重置");
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        await rig.DownloadAsync();

        Assert.HasCount(3, rig.DownloadCalls(), "首次 + 重试 2 次");
        Assert.AreEqual(TransferStatus.Failed, rig.Sink.Transfers.Single().Status);
        Assert.AreEqual(Strings.Format("Transfer_FailedAfterRetries", 2, failure.Message), rig.Browser.ErrorMessage);
    });

    [TestMethod]
    public Task ZeroRetries_FailsOnTheFirstError() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 0);
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSshConnectionException("连接被重置"));

        await rig.DownloadAsync();

        Assert.HasCount(1, rig.DownloadCalls());
        Assert.IsEmpty(rig.Delays);
        Assert.AreEqual("连接被重置", rig.Browser.ErrorMessage, "没重试过就不提重试");
    });

    [TestMethod]
    [DataRow("permission", DisplayName = "权限拒绝")]
    [DataRow("missing", DisplayName = "远端不存在")]
    [DataRow("refused", DisplayName = "服务端拒写(磁盘满)")]
    [DataRow("local", DisplayName = "本地无权限")]
    public Task APermanentFailure_IsNotRetried(string kind) => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3);
        Exception failure = kind switch
        {
            "permission" => new VelaSftpPermissionDeniedException("Permission denied"),
            "missing" => new VelaSftpPathNotFoundException("No such file"),
            "refused" => new VelaSftpTransferInterruptedException("No space left on device", 4096) { RefusedByServer = true },
            _ => new UnauthorizedAccessException("Access denied"),
        };
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        await rig.DownloadAsync();

        Assert.HasCount(1, rig.DownloadCalls(), "永久性失败再试也一样");
        Assert.IsEmpty(rig.Delays);
        Assert.AreEqual(TransferStatus.Failed, rig.Sink.Transfers.Single().Status);
    });

    /// <summary>续传开着:重试从这个文件自己已经写下的地方接着传,而不是整份重来。</summary>
    [TestMethod]
    public Task WithResumeOn_ARetryContinuesFromWhatThisFileAlreadyWrote() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3, resume: true);
        rig.Sftp.GetFileInfoAsync(rig.SessionId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(Remote(100));
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                call => Rig.WritePartialThenFail(call, 40),
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreSequenceEqual([0, 40], rig.DownloadCalls().Select(c => c.Offset).ToArray());
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status);
    });

    /// <summary>续传关着:重试整份重来。</summary>
    [TestMethod]
    public Task WithResumeOff_ARetryStartsOver() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3, resume: false);
        rig.Sftp.GetFileInfoAsync(rig.SessionId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(Remote(100));
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                call => Rig.WritePartialThenFail(call, 40),
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreSequenceEqual([0, 0], rig.DownloadCalls().Select(c => c.Offset).ToArray());
    });

    /// <summary>
    /// 上一次连一个字节都没传出去:目标上的东西不是它写的,不能凭长度去续(目录同步的目标比源短是常态)。
    /// </summary>
    [TestMethod]
    public Task AnAttemptThatMovedNoBytes_IsRetriedFromTheSameStart() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3, resume: true);
        rig.Sftp.GetFileInfoAsync(rig.SessionId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(Remote(100));
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                call =>
                {
                    // 别人(不是这次传输)在本地放了一份 40 字节的同名文件,这一次没传出任何字节就断了。
                    File.WriteAllBytes(call.ArgAt<string>(2), new byte[40]);
                    return Task.FromException(new VelaSshConnectionException("连接被重置"));
                },
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreSequenceEqual([0, 0], rig.DownloadCalls().Select(c => c.Offset).ToArray());
    });

    /// <summary>等待重试期间,行上写明是第几次重试(而不是像又回去排队了)。</summary>
    [TestMethod]
    public Task WhileWaitingToRetry_TheRowSaysWhichRetryItIs() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3);
        string? shown = null;
        rig.Browser.RetryDelay = (_, _) =>
        {
            shown ??= rig.Sink.Transfers.Single().ProgressText;
            return Task.CompletedTask;
        };
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new TimeoutException()), _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreEqual(Strings.Format("Transfer_RetryWaiting", 1, 3), shown);
    });

    /// <summary>「全部取消」照样能把一个正等着重试的传输停下,而且不再重试。</summary>
    [TestMethod]
    public Task CancellingWhileWaitingToRetry_StopsTheTransfer() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 5);
        var waiting = new TaskCompletionSource();
        rig.Browser.RetryDelay = async (_, ct) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSshConnectionException("连接被重置"));

        Task download = rig.DownloadAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Sink.CancelAllCommand.Execute().Subscribe();
        await download.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.HasCount(1, rig.DownloadCalls());
        Assert.AreEqual(TransferStatus.Cancelled, rig.Sink.Transfers.Single().Status);
    });

    /// <summary>上传与中转走同一条重试路径(这里验上传)。</summary>
    [TestMethod]
    public Task UploadsAreRetriedToo() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 2);
        string local = Path.Combine(rig.Directory, "up.bin");
        await File.WriteAllBytesAsync(local, new byte[64]);
        rig.Sftp.ListDirectoryAsync(rig.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        rig.Sftp.UploadFileAsync(rig.SessionId, local, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new VelaSshClientException("通道被对端关闭")), _ => Task.CompletedTask);

        await rig.Browser.UploadLocalPathsAsync([local]);

        await rig.Sftp.Received(2).UploadFileAsync(rig.SessionId, local, Arg.Any<string>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status);
    });

    [TestMethod]
    public Task AttemptDiagnostics_RecordsRecoveredFailuresWithoutLeakingMessagesOrPaths() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 2);
        rig.Browser.TransferOptions.TransferLogging = true;
        rig.Browser.TransferOptions.LogDirectory = rig.Directory;
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new RemoteTransferLengthException("/secret/token=private-path", 1024, 0)),
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        string log = File.ReadAllText(Directory.GetFiles(rig.Directory, "transfer-*.log").Single());
        string diagnostic = log.Split('\n').Single(line => line.Contains("ATTEMPT_FAILED", StringComparison.Ordinal));
        Assert.Contains("attempt=1", diagnostic);
        // 长度不符是永久失败:诊断照样留一条,但如实标成不会再自动重试。
        Assert.Contains("retry=False", diagnostic);
        Assert.Contains("kind=Permanent", diagnostic);
        Assert.Contains(nameof(RemoteTransferLengthException), diagnostic);
        Assert.Contains("expected=1024,actual=0", diagnostic);
        Assert.IsFalse(diagnostic.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.Contains("private-path", StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.Contains(RemoteFile, StringComparison.Ordinal));
        Assert.AreEqual(TransferStatus.Failed, rig.Sink.Transfers.Single().Status,
            "长度不符不再重试,这一项直接按失败落定。");
    });

    [TestMethod]
    public Task AttemptDiagnostics_RespectsLoggingDisabled() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 1);
        rig.Browser.TransferOptions.LogDirectory = rig.Directory;
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("password=do-not-log"));

        await rig.DownloadAsync();

        Assert.IsEmpty(Directory.GetFiles(rig.Directory, "transfer-*.log"));
    });

    [TestMethod]
    public void AttemptDiagnostics_RecordsNestedTypesAndDurableBytesWithoutServerText()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-attempt-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var failure = new VelaSftpTransferInterruptedException("token=private-server-response", 4096,
                new IOException("/private/location", new TimeoutException("password=secret")));
            new VelaSftpUploadFailureDetails(2048, new VelaSftpPermissionDeniedException("/private/cleanup token=secret"))
                .AttachTo(failure);
            TransferLogService.AppendFailure(directory, Guid.NewGuid(), TransferType.Relay, 2, 1024, true, false, failure);

            string log = File.ReadAllText(Directory.GetFiles(directory, "transfer-*.log").Single());
            Assert.Contains("durable=4096,refused=False", log);
            Assert.Contains(nameof(TimeoutException), log);
            Assert.Contains("targetDurable=2048", log);
            Assert.Contains("cleanup=VelaSftpPermissionDeniedException", log);
            Assert.Contains("retry=False", log);
            Assert.IsFalse(log.Contains("private", StringComparison.Ordinal));
            Assert.IsFalse(log.Contains("secret", StringComparison.Ordinal));
            Assert.IsFalse(log.Contains("token=", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AttemptDiagnostics_CacheFailureIsPermanentAndLocal_WithoutLeakingMessages()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-cache-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timeout = new TimeoutException("password=private-cache-secret");
            var io = new IOException("/private/cache/location token=private-cache-token", timeout);
            var failure = new RelayCacheException("write", io);
            Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure),
                "本地缓存错误不能被内层可重试的超时误判为链路故障。");

            TransferLogService.AppendFailure(directory, Guid.NewGuid(), TransferType.Relay, 1, 0, false, false, failure);

            string log = File.ReadAllText(Directory.GetFiles(directory, "transfer-*.log").Single());
            Assert.Contains("ATTEMPT_FAILED", log);
            Assert.Contains("kind=Permanent", log);
            Assert.Contains("stage=local-cache", log);
            Assert.Contains("retry=False", log);
            Assert.Contains(nameof(RelayCacheException), log);
            Assert.Contains(nameof(IOException), log);
            Assert.Contains(nameof(TimeoutException), log);
            foreach (string message in new[] { failure.Message, io.Message, timeout.Message })
            {
                Assert.IsFalse(log.Contains(message, StringComparison.Ordinal), "诊断不能输出任何一层异常消息。");
            }
            Assert.IsFalse(log.Contains("/private/cache/location", StringComparison.Ordinal));
            Assert.IsFalse(log.Contains("private-cache-secret", StringComparison.Ordinal));
            Assert.IsFalse(log.Contains("private-cache-token", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AttemptDiagnostics_RecordsAggregateBranchesButNeverMessages()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-attempt-aggregate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var error = new AggregateException("token=secret",
                new IOException("/private/source"), new VelaSftpPermissionDeniedException("password=secret"));
            TransferLogService.AppendFailure(directory, Guid.NewGuid(), TransferType.Relay, 1, 0, false, false, error);

            string log = File.ReadAllText(Directory.GetFiles(directory, "transfer-*.log").Single());
            Assert.Contains(nameof(IOException), log);
            Assert.Contains(nameof(VelaSftpPermissionDeniedException), log);
            Assert.Contains("kind=Permanent", log);
            Assert.IsFalse(log.Contains("secret", StringComparison.Ordinal));
            Assert.IsFalse(log.Contains("private", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(8, false)]
    [DataRow(9, false)]
    [DataRow(10, false)]
    [DataRow(9, true)]
    public void AttemptDiagnostics_MarksTruncatedExceptionChains(int count, bool aggregate)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-attempt-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            Exception error = new IOException("private");
            if (aggregate)
            {
                error = new AggregateException(Enumerable.Range(1, count - 1).Select(_ => new IOException("private")));
            }
            else
            {
                for (int i = 1; i < count; i++) error = new IOException("private", error);
            }
            TransferLogService.AppendFailure(directory, Guid.NewGuid(), TransferType.Relay, 1, 0, false, false, error);

            string log = File.ReadAllText(Directory.GetFiles(directory, "transfer-*.log").Single());
            Assert.AreEqual(count > 8, log.Contains("truncated=True", StringComparison.Ordinal));
            Assert.IsFalse(log.Contains("private", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task StagedRelay_TargetRetryReusesReadySource_EvenIfOriginalChanges(bool resume) => OnUi(async () =>
    {
        var rig = new RelayRig(resume: resume);
        var inputs = new List<Stream>();
        var offsets = new List<long>();
        rig.Sftp.Configure().UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            inputs.Add(input);
            offsets.Add(call.ArgAt<long>(6));
            Assert.IsTrue(input.CanSeek);
            Assert.IsFalse(rig.Item.IsPreparingSource);
            Assert.AreEqual(10L, call.ArgAt<long>(3));
            if (inputs.Count == 1)
            {
                Assert.AreEqual(0L, input.Position);
                input.Seek(4, SeekOrigin.Begin);
                call.ArgAt<IProgress<TransferProgress>?>(5)?.Report(RelayRig.Progress(4));
                rig.Sftp.ExistsAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>()).Returns(true);
                rig.Sftp.GetFileInfoAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>()).Returns(Remote(4));
                // 已缓存后原源消失:自动续传不能重新 stat 它,更不能重新下载。
                rig.Sftp.GetFileInfoAsync(rig.SourceId, RemoteFile, Arg.Any<CancellationToken>())
                    .ThrowsAsync(new FileNotFoundException("source moved"));
                throw new VelaSftpTransferInterruptedException("target interrupted", 4);
            }
            Assert.AreEqual(0L, input.Position, "每次上传必须重置缓存,尾部核验仍交给目标服务。");
            if (resume)
            {
                input.Seek(2, SeekOrigin.Begin);
                Assert.AreEqual(2, input.ReadByte(), "续传尾部必须可重新读取缓存。");
            }
            input.Seek(call.ArgAt<long>(6), SeekOrigin.Begin);
            await input.CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
        });

        await rig.RunAsync();

        Assert.AreEqual(TransferStatus.Completed, rig.Item.Status);
        Assert.AreSequenceEqual(resume ? [0, 4] : [0L, 0L], offsets.ToArray());
        Assert.AreEqual(1, rig.Downloads);
        Assert.AreEqual(1, rig.Delays);
        Assert.IsTrue(inputs.All(input => !input.CanRead), "运行结束必须释放上传所用缓存。");
        Assert.IsNull(rig.Item.CancelItem);
        await rig.Sftp.Received(1).GetFileInfoAsync(rig.SourceId, RemoteFile, Arg.Any<CancellationToken>());
        await rig.Sftp.DidNotReceiveWithAnyArgs().OpenReadAsync(default, null!);
    });

    [TestMethod]
    public Task StagedRelay_PreparationDoesNotAuthorizeResumeOfUntouchedTarget() => OnUi(async () =>
    {
        var rig = new RelayRig(resume: true);
        var offsets = new List<long>();
        rig.Sftp.Configure().UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            offsets.Add(call.ArgAt<long>(6));
            if (offsets.Count == 1)
            {
                // 下载源已经完成,但上传还没有确认任何字节。后来出现的目标不是本次写出的。
                rig.Sftp.ExistsAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>()).Returns(true);
                rig.Sftp.GetFileInfoAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>()).Returns(Remote(4));
                throw new TimeoutException("target not ready");
            }
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
        });

        await rig.RunAsync();

        Assert.AreEqual(TransferStatus.Completed, rig.Item.Status);
        Assert.AreEqual(1, rig.Downloads);
        Assert.AreSequenceEqual([0, 0], offsets.ToArray());
        await rig.Sftp.DidNotReceive().GetFileInfoAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>());
    });

    [TestMethod]
    public Task StagedRelay_PrepareFailureRedownloads_WithoutCountingSourceProgress() => OnUi(async () =>
    {
        var rig = new RelayRig();
        var destinations = new List<Stream>();
        rig.Sftp.Configure().DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            Stream destination = call.ArgAt<Stream>(2);
            destinations.Add(destination);
            Assert.IsTrue(rig.Item.IsPreparingSource);
            Assert.AreEqual(Strings.Get("Transfer_PreparingSource"), rig.Item.ProgressText);
            Assert.AreEqual(0L, rig.Item.TransferredBytes);
            Assert.AreEqual(0, rig.Uploads);
            await destination.WriteAsync(RelayRig.Data, call.ArgAt<CancellationToken>(4));
            call.ArgAt<IProgress<TransferProgress>?>(3)?.Report(RelayRig.Progress(10));
            if (destinations.Count == 1) throw new TimeoutException("source completion failed");
        });

        await rig.RunAsync();

        Assert.AreEqual(2, rig.Downloads);
        Assert.AreEqual(1, rig.Uploads);
        Assert.AreEqual(TransferStatus.Completed, rig.Item.Status);
        Assert.AreNotSame(destinations[0], destinations[1]);
        Assert.IsTrue(destinations.All(stream => !stream.CanWrite));
        Assert.IsFalse(rig.Item.IsPreparingSource);
        Assert.IsNull(rig.Item.CancelItem);
        await rig.Sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task StagedRelay_SourceFailureNeverTouchesExistingTarget(bool resume) => OnUi(async () =>
    {
        var rig = new RelayRig(resume: resume);
        var errors = new List<string?>();
        bool refreshedAfterFailure = false;
        Stream? prepareDestination = null;
        rig.Target.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(FileBrowserViewModel.ErrorMessage))
                errors.Add(rig.Target.ErrorMessage);
        };
        rig.Sftp.ListDirectoryAsync(rig.TargetId, "/", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (rig.Sink.Transfers.SingleOrDefault()?.Status == TransferStatus.Failed)
                refreshedAfterFailure = true;
            return Task.FromResult(new List<RemoteFileInfo> { Remote(4) });
        });
        rig.Sftp.GetFileInfoAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>()).Returns(Remote(4));
        rig.Sftp.Configure().DownloadStreamAsync(default, null!, null!)
            .ReturnsForAnyArgs(call =>
            {
                prepareDestination = call.ArgAt<Stream>(2);
                return Task.FromException(new UnauthorizedAccessException("source denied"));
            });

        await rig.RunAsync();

        Assert.AreEqual(TransferStatus.Failed, rig.Item.Status);
        // RunTransferAsync 确实报告源错误;ReceiveFromPeerAsync 末尾的 RefreshAsync →
        // NavigateCoreAsync 会清掉面板错误,但不能清掉已经落定的传输失败状态。
        Assert.AreSequenceEqual(["source denied", null], [.. errors]);
        Assert.IsTrue(refreshedAfterFailure, "确认清错来自传输落定后的目录刷新,而非未执行下载。");
        Assert.IsNull(rig.Target.ErrorMessage);
        Assert.IsNotNull(prepareDestination, "源下载 mock 必须实际执行。");
        Assert.IsFalse(prepareDestination.CanWrite, "失败的源准备缓存必须释放。");
        await rig.Sftp.Received(1).DownloadStreamAsync(rig.SourceId, RemoteFile, Arg.Any<Stream>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(1, rig.Downloads);
        Assert.AreEqual(0, rig.Uploads);
        Assert.AreEqual(0, rig.Delays);
        Assert.IsNull(rig.Item.CancelItem);
        Assert.IsFalse(rig.Item.IsPreparingSource);
        await rig.Sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task StagedRelay_CancellationCleansCacheAndItem(bool duringUpload) => OnUi(async () =>
    {
        var rig = new RelayRig();
        Stream? cache = null;
        var started = new TaskCompletionSource();
        if (duringUpload)
        {
            rig.Sftp.Configure().UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
            {
                cache = call.ArgAt<Stream>(1);
                started.SetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(8));
            });
        }
        else
        {
            rig.Sftp.Configure().DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
            {
                cache = call.ArgAt<Stream>(2);
                await cache.WriteAsync(RelayRig.Data, call.ArgAt<CancellationToken>(4));
                started.SetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(4));
            });
        }
        Task run = rig.RunAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(rig.Item.CancelItem);
        rig.Item.CancelItem();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(TransferStatus.Cancelled, rig.Item.Status);
        Assert.IsNotNull(cache);
        Assert.IsFalse(duringUpload ? cache.CanRead : cache.CanWrite);
        Assert.IsNull(rig.Item.CancelItem);
        Assert.IsFalse(rig.Item.IsPreparingSource);
        Assert.AreEqual(0, rig.Delays);
        await rig.Sftp.Received(duringUpload ? 1 : 0).DeleteAsync(rig.TargetId, "/a.bin",
            Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
    });

    [TestMethod]
    public Task StagedRelay_ManualRetryPreparesANewSource() => OnUi(async () =>
    {
        var rig = new RelayRig();
        rig.Sftp.Configure().UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(
            _ => Task.FromException(new UnauthorizedAccessException("target denied")),
            call => call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8)));
        await rig.RunAsync();
        TransferItemViewModel failed = rig.Item;
        Assert.AreEqual(TransferStatus.Failed, failed.Status);
        Assert.IsNotNull(failed.RetryAsync);

        await failed.RetryAsync();

        Assert.AreEqual(2, rig.Downloads);
        Assert.AreEqual(2, rig.Uploads);
        Assert.HasCount(2, rig.Sink.Transfers);
        Assert.ContainsSingle(item => item.Status == TransferStatus.Completed, rig.Sink.Transfers);
    });

    [TestMethod]
    public Task StagedRelay_RenamedRetryPreparesANewSource() => OnUi(async () =>
    {
        var rig = new RelayRig(resume: true);
        rig.Target.TransferOptions.ConflictPolicy = "rename";
        rig.Sftp.ListDirectoryAsync(rig.TargetId, "/", Arg.Any<CancellationToken>()).Returns([Remote(4)]);
        rig.Sftp.GetFileInfoAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>()).Returns(Remote(4));
        var paths = new List<string>();
        Stream? firstInput = null;
        rig.Sftp.Configure().UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            paths.Add(call.ArgAt<string>(2));
            if (paths.Count == 1)
            {
                firstInput = call.ArgAt<Stream>(1);
                Assert.AreEqual(4L, call.ArgAt<long>(6));
                throw new VelaSftpResumeMismatchException("tail differs");
            }
            Assert.IsNotNull(firstInput);
            Assert.IsFalse(firstInput.CanRead, "改名的新运行开始前必须释放旧缓存。");
            Assert.AreEqual(0L, call.ArgAt<long>(6));
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
        });

        await rig.RunAsync();

        Assert.AreEqual(2, rig.Downloads);
        Assert.HasCount(2, paths);
        Assert.AreNotEqual(paths[0], paths[1]);
        Assert.ContainsSingle(item => item.Status == TransferStatus.Completed, rig.Sink.Transfers);
        Assert.ContainsSingle(item => item.Status == TransferStatus.Cancelled, rig.Sink.Transfers);
        Assert.IsTrue(rig.Sink.Transfers.All(item => item.CancelItem is null));
        await rig.Sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    });

    [TestMethod]
    public Task StagedRelay_CacheFailureIsPermanent_EvenWithTransientInnerError() => OnUi(async () =>
    {
        var rig = new RelayRig();
        var failure = new RelayCacheException("write", new IOException("cache failed", new TimeoutException()));
        rig.Sftp.Configure().DownloadStreamAsync(default, null!, null!)
            .ReturnsForAnyArgs(_ => Task.FromException(failure));

        await rig.RunAsync();

        Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure));
        Assert.AreEqual(TransferStatus.Failed, rig.Item.Status);
        Assert.AreEqual(1, rig.Downloads);
        Assert.AreEqual(0, rig.Uploads);
        Assert.AreEqual(0, rig.Delays);
        Assert.IsNull(rig.Item.CancelItem);
        await rig.Sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    });

    [TestMethod]
    public Task RemoteCommitRelay_RetryRestartsSourceAndNeverResumesOrDeletesFinalTarget() => OnUi(async () =>
    {
        var rig = new RelayRig(resume: true);
        rig.Sftp.Configure().CheckUploadCommitAsync(default, null!, false).ReturnsForAnyArgs(Task.CompletedTask);
        rig.Sftp.OpenReadAsync(rig.SourceId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(RelayRig.Data)));
        var paths = new List<string>();
        rig.Sftp.Configure().UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            paths.Add(call.ArgAt<string>(2));
            Assert.AreEqual(0L, call.ArgAt<long>(6));
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
            call.ArgAt<IProgress<TransferProgress>?>(5)?.Report(RelayRig.Progress(10));
            if (paths.Count == 1) throw new TimeoutException("upload interrupted");
        });

        await rig.RunAsync();

        Assert.AreEqual(TransferStatus.Completed, rig.Item.Status);
        Assert.AreEqual(0, rig.Downloads);
        Assert.AreEqual(1, rig.Delays);
        Assert.HasCount(2, paths);
        Assert.AreNotEqual(paths[0], paths[1]);
        Assert.IsTrue(paths.All(path => path.StartsWith("/.vela-relay-", StringComparison.Ordinal)));
        await rig.Sftp.Received(2).OpenReadAsync(rig.SourceId, RemoteFile, Arg.Any<CancellationToken>());
        await rig.Sftp.Received(1).DeleteAsync(rig.TargetId, paths[0], Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        await rig.Sftp.DidNotReceive().DeleteAsync(rig.TargetId, "/a.bin", Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        await rig.Sftp.Received(1).CommitUploadAsync(rig.TargetId, paths[1], "/a.bin", true, Arg.Any<CancellationToken>());
        await rig.Sftp.DidNotReceive().GetFileInfoAsync(rig.TargetId, "/a.bin", Arg.Any<CancellationToken>());
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task RemoteCommitRelay_UnconfirmedCommitNeverRetriesOrCleansFinalTarget(bool cancel) => OnUi(async () =>
    {
        var rig = new RelayRig();
        rig.Sftp.Configure().CheckUploadCommitAsync(default, null!, false).ReturnsForAnyArgs(Task.CompletedTask);
        rig.Sftp.OpenReadAsync(rig.SourceId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(RelayRig.Data)));
        rig.Sftp.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(_ =>
        {
            if (cancel) rig.Item.CancelItem!();
            return Task.FromException(new TimeoutException("commit reply missing"));
        });

        await rig.RunAsync();

        Assert.AreEqual(TransferStatus.Failed, rig.Item.Status);
        Assert.AreEqual(0, rig.Delays);
        Assert.AreEqual(0, rig.Downloads);
        Assert.AreEqual(1, rig.Uploads);
        await rig.Sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    });

    private sealed class RelayRig
    {
        public static readonly byte[] Data = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        public Guid SourceId { get; } = Guid.NewGuid();
        public Guid TargetId { get; } = Guid.NewGuid();
        public ISftpService Sftp { get; } = Substitute.For<ISftpService>();
        public FileTransferViewModel Sink { get; } = new();
        public FileBrowserViewModel Target { get; }
        public TransferItemViewModel Item => Sink.Transfers.Single();
        public int Delays { get; private set; }
        public int Downloads => Sftp.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ISftpService.DownloadStreamAsync));
        public int Uploads => Sftp.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ISftpService.UploadStreamAsync));

        public RelayRig(bool resume = false, bool streamed = false, bool staged = true)
        {
            Sftp.ListDirectoryAsync(default, null!).ReturnsForAnyArgs(_ => Task.FromResult(new List<RemoteFileInfo>()));
            Sftp.CheckUploadCommitAsync(default, null!, false).ReturnsForAnyArgs(
                _ => Task.FromException(new NotSupportedException("No atomic commit")));
            Sftp.GetFileInfoAsync(SourceId, RemoteFile, Arg.Any<CancellationToken>()).Returns(Remote(10));
            Sftp.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(call =>
                call.ArgAt<Stream>(2).WriteAsync(Data, call.ArgAt<CancellationToken>(4)).AsTask());
            Sftp.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(call =>
                call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8)));
            var source = new FileBrowserViewModel(Sftp, SourceId)
            {
                // 旧路径(整份缓存)与流管道各自显式选一条,免得用例靠默认值决定它验的是哪条。
                UseStagedRelaySource = staged,
                UseStreamedRelay = streamed,
            };
            Target = new FileBrowserViewModel(Sftp, TargetId)
            {
                DualPeer = source,
                TransferSink = Sink,
                TransferOptions = new()
                {
                    ConflictPolicy = "overwrite",
                    ResumeEnabled = resume,
                    AutoCleanTempFiles = true,
                    TransferMaxRetries = 2,
                    TransferLogging = false,
                    NotifyOnComplete = false,
                },
                RetryDelay = (_, _) => { Delays++; return Task.CompletedTask; },
            };
            source.DualPeer = Target;
        }

        public Task RunAsync() => Target.ReceiveFromPeerAsync([new(Remote(10))]);
        public static TransferProgress Progress(long bytes) => new()
        {
            FileName = "a.bin",
            BytesTransferred = bytes,
            TotalBytes = 10,
            Percentage = (int)(bytes * 10),
            SpeedBytesPerSecond = 0,
            EstimatedTimeRemaining = TimeSpan.Zero,
        };
    }

    private static Task OnUi(Func<Task> body) => _session.RunOnUiAsync(body);

    private static RemoteFileInfo Remote(long size) => new()
    {
        Name = "a.bin",
        FullPath = RemoteFile,
        Size = size,
        Permissions = "-rw-r--r--",
        IsDirectory = false,
        LastModified = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Owner = "u",
        Group = "g",
    };

    /// <summary>一个面板 + 一个传输浮窗 + 一个本地下载目录;重试等待换成立即返回并记下时长。</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(int retries, bool resume = true)
        {
            Directory = Path.Combine(Path.GetTempPath(), "vela-retry-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Browser = new FileBrowserViewModel(Sftp, SessionId)
            {
                TransferSink = Sink,
                TransferOptions = new()
                {
                    ConflictPolicy = "overwrite",
                    ResumeEnabled = resume,
                    AutoCleanTempFiles = false,
                    TransferLogging = false,
                    NotifyOnComplete = false,
                    TransferMaxRetries = retries,
                },
                RetryDelay = (delay, _) =>
                {
                    Delays.Add(delay);
                    return Task.CompletedTask;
                },
            };
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public ISftpService Sftp { get; } = Substitute.For<ISftpService>();

        public FileTransferViewModel Sink { get; } = new(null);

        public FileBrowserViewModel Browser { get; }

        public string Directory { get; }

        public List<TimeSpan> Delays { get; } = [];

        public Task DownloadAsync() => Browser.DownloadRemoteEntriesAsync(
            [new RemoteFileInfoViewModel(Remote(100))],
            Directory);

        /// <summary>按次序列出每一次下载调用的起点。</summary>
        public List<(string Local, long Offset)> DownloadCalls() =>
        [
            .. Sftp.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ISftpService.DownloadFileAsync))
                .Select(c => ((string)c.GetArguments()[2]!, (long)c.GetArguments()[4]!)),
        ];

        /// <summary>模拟一次写了 <paramref name="bytes" /> 字节就断线的下载。</summary>
        public static Task WritePartialThenFail(NSubstitute.Core.CallInfo call, long bytes)
        {
            File.WriteAllBytes(call.ArgAt<string>(2), new byte[bytes]);
            call.ArgAt<IProgress<TransferProgress>?>(3)?.Report(new()
            {
                FileName = "a.bin",
                BytesTransferred = bytes,
                TotalBytes = 100,
                Percentage = (int)bytes,
                SpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero,
            });
            return Task.FromException(new VelaSshConnectionException("连接被重置"));
        }

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
