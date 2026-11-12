using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class RemoteRelayCommitTests
{
    private static readonly Guid SourceSession = Guid.NewGuid();
    private static readonly Guid TargetSession = Guid.NewGuid();
    private const string SourcePath = "/source/file";
    private const string TargetPath = "/destination/file";

    [TestMethod]
    public async Task UnsupportedPreflight_ReturnsFalseWithoutOpeningSource()
    {
        ISftpService source = Substitute.For<ISftpService>();
        ISftpService target = Substitute.For<ISftpService>();
        target.CheckUploadCommitAsync(TargetSession, TargetPath, true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException());

        Assert.IsFalse(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath));
        await source.DidNotReceiveWithAnyArgs().GetFileInfoAsync(default, null!);
        await source.DidNotReceiveWithAnyArgs().OpenReadAsync(default, null!);
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    public async Task SameSession_ReturnsFalseBeforePreflightOrSourceOpen()
    {
        ISftpService source = Substitute.For<ISftpService>();
        ISftpService target = Substitute.For<ISftpService>();

        Assert.IsFalse(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, SourceSession, TargetPath));
        await source.DidNotReceiveWithAnyArgs().OpenReadAsync(default, null!);
        await target.DidNotReceiveWithAnyArgs().CheckUploadCommitAsync(default, null!, false);
    }

    [TestMethod]
    public async Task Success_ConsumesAndClosesBeforeCommit_ReportsCompletionAfterCommit()
    {
        var stream = new TrackingStream(new byte[1234]);
        (ISftpService source, ISftpService target) = Services(stream, 1234);
        target.GetUploadCreateModeAsync(TargetSession, TargetPath, Arg.Any<CancellationToken>()).Returns((uint?)384);
        var progress = new RecordingProgress();
        string? uploadedPath = null;
        bool consumed = false;
        bool committed = false;
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            uploadedPath = call.ArgAt<string>(2);
            Assert.AreEqual((uint?)384, call.ArgAt<uint?>(7));
            using var sink = new MemoryStream();
            await call.ArgAt<Stream>(1).CopyToAsync(sink);
            Assert.AreEqual(1234L, sink.Length);
            consumed = true;
            call.ArgAt<IProgress<TransferProgress>?>(5)!.Report(FinalProgress(1234));
            Assert.DoesNotContain(p => p.Percentage >= 100, progress.Items);
        });
        target.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(call =>
        {
            Assert.IsTrue(consumed);
            Assert.IsTrue(stream.Closed);
            Assert.DoesNotContain(p => p.Percentage >= 100, progress.Items);
            Assert.AreEqual(uploadedPath, call.ArgAt<string>(1));
            Assert.AreEqual(TargetPath, call.ArgAt<string>(2));
            Assert.IsTrue(call.ArgAt<bool>(3));
            committed = true;
            return Task.CompletedTask;
        });

        Assert.IsTrue(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath, progress));
        Assert.IsTrue(committed);
        Assert.IsNotNull(uploadedPath);
        Assert.StartsWith("/destination/.vela-relay-", uploadedPath);
        Assert.AreNotEqual(TargetPath, uploadedPath);
        Assert.HasCount(1, progress.Items.Where(p => p.Percentage >= 100).ToList());
        await target.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    [TestMethod]
    public async Task LargeSequentialSource_LateCloseFailure_DeletesOnlyTemporaryFile()
    {
        const int size = 4 * 1024 * 1024 + 1;
        var original = new IOException("source final reply");
        var stream = new TrackingStream(new byte[size], sequential: true, closeError: original, chunk: 1);
        (ISftpService source, ISftpService target) = Services(stream, size);
        string? uploaded = null;
        long received = 0;
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            uploaded = call.ArgAt<string>(2);
            using var sink = new MemoryStream();
            try
            {
                // 一次只给 4 KiB:像 FTP 数据流,读循环会一块一块灌。
                await call.ArgAt<Stream>(1).CopyToAsync(sink);
            }
            finally
            {
                // 无论上传是否先收工,这里都要拿到"源被取走了多少":失败时 CopyToAsync 会先抛。
                received = sink.Length;
            }
            Assert.IsFalse(stream.Closed);
        });

        Exception actual = await Assert.ThrowsExactlyAsync<IOException>(() =>
            RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.AreSame(original, actual);
        Assert.AreEqual(size, stream.Position, "源端必须完整读取后才遇到最终回复失败。");
        Assert.IsGreaterThan(0L, received);
        Assert.IsLessThanOrEqualTo(size, received,
            "管道完成失败时可丢弃尚未交给目标的缓冲;不要求目标收到全部字节。");
        Assert.IsTrue(stream.Closed);
        Assert.IsNotNull(uploaded);
        Assert.AreNotEqual(TargetPath, uploaded);
        await target.Received(1).DeleteAsync(TargetSession, uploaded, Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        await target.DidNotReceive().DeleteAsync(TargetSession, TargetPath, Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().CommitUploadAsync(default, null!, null!, false);
    }

    [TestMethod]
    public async Task UploadFailure_CleanupFailureDoesNotMaskOriginal()
    {
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[4]), 4);
        var original = new IOException("upload failed");
        string? uploaded = null;
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(call =>
        {
            uploaded = call.ArgAt<string>(2);
            return Task.FromException(original);
        });
        target.DeleteAsync(default, null!).ReturnsForAnyArgs(Task.FromException(new IOException("cleanup failed")));

        Exception actual = await Assert.ThrowsExactlyAsync<IOException>(() =>
            RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath));
        Assert.AreSame(original, actual);
        Assert.IsNotNull(uploaded);
        Assert.AreNotEqual(TargetPath, uploaded);
        await target.Received(1).DeleteAsync(TargetSession, uploaded, Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().CommitUploadAsync(default, null!, null!, false);
    }

    [TestMethod]
    public async Task CancelledUpload_CleanupUsesIndependentLiveToken()
    {
        using var cts = new CancellationTokenSource();
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[4]), 4);
        string? uploaded = null;
        CancellationToken cleanupToken = default;
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(call =>
        {
            uploaded = call.ArgAt<string>(2);
            cts.Cancel();
            return Task.FromCanceled(cts.Token);
        });
        target.DeleteAsync(default, null!).ReturnsForAnyArgs(call =>
        {
            cleanupToken = call.ArgAt<CancellationToken>(3);
            Assert.IsTrue(cleanupToken.CanBeCanceled);
            Assert.IsFalse(cleanupToken.IsCancellationRequested);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath, cancellationToken: cts.Token));
        Assert.IsNotNull(uploaded);
        Assert.AreNotEqual(cts.Token, cleanupToken);
        await target.Received(1).DeleteAsync(TargetSession, uploaded, Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().CommitUploadAsync(default, null!, null!, false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CommitFailure_IsAmbiguousPermanentAndNeverCleansUp(bool timeout)
    {
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[4]), 4);
        Exception original = timeout ? new TimeoutException("commit timeout") : new IOException("commit io");
        target.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(Task.FromException(original));
        var progress = new RecordingProgress();

        RemoteRelayCommitException actual = await Assert.ThrowsExactlyAsync<RemoteRelayCommitException>(() =>
            RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath, progress));
        Assert.AreSame(original, actual.InnerException);
        Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(actual));
        Assert.DoesNotContain(p => p.Percentage >= 100, progress.Items);
        await target.ReceivedWithAnyArgs(1).CommitUploadAsync(default, null!, null!, false);
        await target.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    [TestMethod]
    public async Task SerializedTarget_ForwardsCommitPreflightAndCreationMode()
    {
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[4]), 4);
        target.GetUploadCreateModeAsync(TargetSession, TargetPath, Arg.Any<CancellationToken>()).Returns((uint?)384);
        await using var serialized = new SerializedSftpService(target, TargetSession);

        Assert.IsTrue(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath,
            serialized, TargetSession, TargetPath));

        await target.Received(1).CheckUploadCommitAsync(TargetSession, TargetPath, true, Arg.Any<CancellationToken>());
        await target.Received(1).GetUploadCreateModeAsync(TargetSession, TargetPath, Arg.Any<CancellationToken>());
        await target.Received(1).UploadStreamAsync(TargetSession, Arg.Any<Stream>(), Arg.Any<string>(), 4,
            Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>?>(), 0, 384, Arg.Any<CancellationToken>());
        await target.Received(1).CommitUploadAsync(TargetSession, Arg.Any<string>(), TargetPath, true, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CancellationAfterCommitStarts_DoesNotUndoConfirmedSuccess()
    {
        using var cts = new CancellationTokenSource();
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[4]), 4);
        target.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(call =>
        {
            cts.Cancel();
            Assert.IsFalse(call.ArgAt<CancellationToken>(4).IsCancellationRequested);
            return Task.CompletedTask;
        });
        var progress = new RecordingProgress();

        Assert.IsTrue(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath,
            target, TargetSession, TargetPath, progress, cancellationToken: cts.Token));
        Assert.HasCount(1, progress.Items.Where(p => p.Percentage == 100).ToList());
        await target.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(0, 17)]
    [DataRow(-1, 17)]
    public async Task UnknownOrEmptySequentialSource_RequiresCompleteConsumption(long expected, int size)
    {
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[size], sequential: true), expected);
        Assert.IsTrue(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath,
            target, TargetSession, TargetPath));
        await target.ReceivedWithAnyArgs(1).CommitUploadAsync(default, null!, null!, false);
    }

    [TestMethod]
    public async Task UnknownSequentialSource_EarlyReturningUploaderCannotCommit()
    {
        // 长度未知(SIZE 没问到)时,上传必须先真正读完源、并等源自己的收尾成功,才谈得上提交。
        // 上传端一个字节都不取就返回,那条链(以及 FTP 的最终回复)根本没跑过 —— 不能提交。
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[17], sequential: true), 0);
        // 上传端不取字节就返回,而源自己以失败收尾 —— 上传的"成功"是假的。
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(_ => Task.CompletedTask);
        Exception failure = await Assert.ThrowsExactlyAsync<IOException>(() => RemoteRelay.TryCopyFileWithRemoteCommitAsync(
            source, SourceSession, SourcePath, target, TargetSession, TargetPath));
        Assert.IsFalse(failure is OperationCanceledException, "那是一次失败的传输,不是取消。");
        await target.DidNotReceiveWithAnyArgs().CommitUploadAsync(default, null!, null!, false);
    }

    [TestMethod]
    public async Task SymbolicLinkTarget_FallsBackBeforeOpeningSource()
    {
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[4]), 4);
        target.ExistsAsync(TargetSession, TargetPath, Arg.Any<CancellationToken>()).Returns(true);
        target.GetFileInfoAsync(TargetSession, TargetPath, Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo
        {
            Name = "file",
            FullPath = TargetPath,
            Size = 4,
            IsDirectory = false,
            IsSymbolicLink = true,
            Permissions = "lrwxrwxrwx",
            LastModified = default,
            Owner = "u",
            Group = "g",
        });
        Assert.IsFalse(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath));
        await source.DidNotReceiveWithAnyArgs().OpenReadAsync(default, null!);
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    public async Task DefiniteCommitRefusal_PreservesErrorAndCleansTemporary()
    {
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[4]), 4);
        var refusal = new Core.Ssh.VelaSftpPermissionDeniedException("rename denied");
        target.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(Task.FromException(refusal));
        Exception actual = await Assert.ThrowsExactlyAsync<Core.Ssh.VelaSftpPermissionDeniedException>(() =>
            RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath));
        Assert.AreSame(refusal, actual);
        await target.Received(1).DeleteAsync(TargetSession, Arg.Is<string>(p => p.StartsWith("/destination/.vela-relay-")),
            Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        await target.DidNotReceive().DeleteAsync(TargetSession, TargetPath, Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(17)]
    public async Task UnknownLength_CompletesWithActualBytesAfterCommit(int length)
    {
        (ISftpService source, ISftpService target) = Services(new TrackingStream(new byte[length], sequential: true), 0);
        var progress = new RecordingProgress();
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null);
            call.ArgAt<IProgress<TransferProgress>>(5).Report(new TransferProgress
            {
                FileName = "file",
                BytesTransferred = length,
                TotalBytes = 0,
                Percentage = 0,
                SpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero,
            });
        });
        target.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(_ =>
        {
            Assert.DoesNotContain(p => p.Percentage == 100, progress.Items);
            return Task.CompletedTask;
        });
        Assert.IsTrue(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, SourcePath, target, TargetSession, TargetPath, progress));
        TransferProgress final = progress.Items.Single(p => p.Percentage == 100);
        Assert.AreEqual(length, final.TotalBytes);
        Assert.AreEqual(length, final.BytesTransferred);
        Assert.AreEqual("file", final.FileName);
    }

    [TestMethod]
    public async Task SerializedCommit_BypassesMetadataQueueButCloseWaitsForCommit()
    {
        ISftpService inner = Substitute.For<ISftpService>();
        var metadata = new TaskCompletionSource<List<RemoteFileInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.ListDirectoryAsync(default, null!).ReturnsForAnyArgs(metadata.Task);
        inner.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(commit.Task);
        await using var service = new SerializedSftpService(inner, TargetSession);
        Task listing = service.ListDirectoryAsync(TargetSession, "/");
        Task committing = service.CommitUploadAsync(TargetSession, "/temp", TargetPath, true);
        Assert.Contains(call => call.GetMethodInfo().Name == nameof(ISftpService.CommitUploadAsync), inner.ReceivedCalls());
        Task closing = service.CloseAsync();
        try
        {
            metadata.SetResult([]);
            await listing;
            Assert.IsFalse(closing.IsCompleted);
        }
        finally
        {
            commit.TrySetResult();
        }
        await committing;
        await closing;
    }

    private static (ISftpService Source, ISftpService Target) Services(Stream stream, long size)
    {
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, SourcePath, Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo
        {
            Name = "file",
            FullPath = SourcePath,
            Size = size,
            Permissions = "-rw-r--r--",
            Owner = "u",
            Group = "g",
            IsDirectory = false,
            LastModified = default,
        });
        source.OpenReadAsync(SourceSession, SourcePath, Arg.Any<CancellationToken>()).Returns(Task.FromResult(stream));
        ISftpService target = Substitute.For<ISftpService>();
        target.CheckUploadCommitAsync(TargetSession, TargetPath, true, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        target.GetUploadCreateModeAsync(TargetSession, TargetPath, Arg.Any<CancellationToken>()).Returns((uint?)null);
        target.CommitUploadAsync(default, null!, null!, false).ReturnsForAnyArgs(Task.CompletedTask);
        target.DeleteAsync(default, null!).ReturnsForAnyArgs(Task.CompletedTask);
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null);
            call.ArgAt<IProgress<TransferProgress>?>(5)?.Report(FinalProgress(size));
        });
        return (source, target);
    }

    private static TransferProgress FinalProgress(long size) => new()
    {
        FileName = "file",
        BytesTransferred = size,
        TotalBytes = size,
        Percentage = 100,
        SpeedBytesPerSecond = 0,
        EstimatedTimeRemaining = TimeSpan.Zero,
    };

    private sealed class RecordingProgress : IProgress<TransferProgress>
    {
        public List<TransferProgress> Items { get; } = [];
        public void Report(TransferProgress value) => Items.Add(value);
    }

    private sealed class TrackingStream(byte[] data, bool sequential = false, Exception? closeError = null, int chunk = int.MaxValue) : MemoryStream(data)
    {
        public bool Closed { get; private set; }
        public override bool CanSeek => !sequential && base.CanSeek;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span[..Math.Min(buffer.Length, chunk)]));
        }

        public override ValueTask DisposeAsync()
        {
            Closed = true;
            return closeError is null ? base.DisposeAsync() : ValueTask.FromException(closeError);
        }
    }
}
