using System.Reflection;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class StagedRelaySourceTests
{
    private static readonly Guid SourceSession = Guid.NewGuid();
    private static readonly Guid TargetSession = Guid.NewGuid();
    private static readonly DateTime Mtime = new(2026, 10, 28, 1, 2, 3, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(7, 7)]
    [DataRow(4 * 1024 * 1024, 4 * 1024 * 1024)]
    [DataRow(4 * 1024 * 1024 + 1, 4 * 1024 * 1024 + 1)]
    [DataRow(0, 17)]
    [DataRow(-1, 17)]
    [DataRow(0, 0)]
    public async Task PrepareThenUpload_UsesCompletedDownloadAndActualLength(int stat, int payloadLength)
    {
        byte[] bytes = Payload(payloadLength);
        ISftpService source = Source(stat, bytes);
        bool downloaded = false;
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            Stream sink = call.ArgAt<Stream>(2);
            Assert.AreEqual(0L, sink.Length, "Allocation must not prepopulate the cache with zero bytes.");
            await Task.Yield();
            await sink.WriteAsync(bytes.AsMemory(), call.ArgAt<CancellationToken>(4));
            downloaded = true;
        });
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(source, SourceSession, "/source");
        var progress = new RecordingProgress();
        ISftpService target = Target(async input =>
        {
            Assert.IsTrue(downloaded);
            Assert.IsTrue(input.CanSeek);
            Assert.IsFalse(input.CanWrite);
            Assert.AreEqual(0L, input.Position);
            Assert.AreEqual(payloadLength, input.Length);
            Assert.ThrowsExactly<NotSupportedException>(() => input.WriteByte(3));
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy);
            Assert.AreSequenceEqual(bytes, copy.ToArray());
        });
        bool writing = false;
        await staged.UploadAsync(target, TargetSession, "/target", progress, () =>
        {
            Assert.IsTrue(downloaded);
            writing = true;
        });
        Assert.IsTrue(writing);
        Assert.ContainsSingle(p => p.Percentage == 100, progress.Items);
        await target.Received(1).UploadStreamAsync(TargetSession, Arg.Any<Stream>(), "/target", payloadLength, Mtime,
            Arg.Any<IProgress<TransferProgress>?>(), 0, null, Arg.Any<CancellationToken>());
        await source.DidNotReceiveWithAnyArgs().OpenReadAsync(default, null!);
        await source.DidNotReceiveWithAnyArgs().CloseSessionAsync(default);
        await source.DidNotReceive().DisposeAsync();
    }

    [TestMethod]
    [DataRow(10, 0)]
    [DataRow(10, 4)]
    [DataRow(10, 11)]
    [DataRow(4 * 1024 * 1024 + 1, 4)]
    [DataRow(4 * 1024 * 1024 + 1, 4 * 1024 * 1024 + 2)]
    public async Task Prepare_LengthMismatchNeverBecomesReady(int expectedBytes, int payloadLength)
    {
        ISftpService source = Source(expectedBytes, []);
        FileStream? file = null;
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            Stream sink = call.ArgAt<Stream>(2);
            file = AssertUserTempFile(sink);
            await sink.WriteAsync(Payload(payloadLength));
        });
        RemoteTransferLengthException error = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() =>
            StagedRelaySource.PrepareAsync(source, SourceSession, "/source"));
        Assert.AreEqual(expectedBytes, error.ExpectedBytes);
        Assert.AreEqual(payloadLength, error.ActualBytes);
        Assert.IsNotNull(file);
        Assert.IsFalse(File.Exists(file.Name));
    }

    [TestMethod]
    public async Task Prepare_SourceFinalErrorIsPreservedAndNeverTouchesTarget()
    {
        ISftpService source = Source(3, [1, 2, 3]);
        var original = new IOException("source final reply");
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 });
            throw original;
        });
        ISftpService target = Target(_ => Task.CompletedTask);
        bool writing = false;
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(source, SourceSession, "/source");
            await staged.UploadAsync(target, TargetSession, "/target", writing: () => writing = true);
        });
        Assert.AreSame(original, error);
        Assert.IsFalse(writing);
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    [DataRow(10)]
    [DataRow(0)]
    public async Task Upload_FailureKeepsCacheAndSecondAttemptResetsWithoutDownloading(int stat)
    {
        ISftpService source = Source(stat, Payload(10));
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(source, SourceSession, "/source");
        var original = new IOException("target failed");
        ISftpService first = Target(async input =>
        {
            await input.ReadExactlyAsync(new byte[3]);
            throw original;
        });
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => staged.UploadAsync(first, TargetSession, "/target"));
        Assert.AreSame(original, error);
        ISftpService second = Target(async input =>
        {
            Assert.AreEqual(0L, input.Position);
            await input.CopyToAsync(Stream.Null);
        });
        await staged.UploadAsync(second, TargetSession, "/target", resumeOffset: 3);
        await source.ReceivedWithAnyArgs(1).DownloadStreamAsync(default, null!, null!);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Upload_EarlyReturnAndSeekOnlyCannotReportSuccess(int mode)
    {
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(Source(0, Payload(10)), SourceSession, "/source");
        ISftpService target = Target(async input =>
        {
            if (mode == 1) await input.ReadExactlyAsync(new byte[1]);
            if (mode == 3)
            {
                input.Seek(9, SeekOrigin.Begin);
                await input.ReadExactlyAsync(new byte[1]);
            }
            if (mode == 2)
            {
                input.Seek(10, SeekOrigin.Begin);
                Assert.AreEqual(0, await input.ReadAsync(new byte[1]));
            }
        });
        var progress = new RecordingProgress();
        await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() => staged.UploadAsync(target, TargetSession, "/target", progress));
        Assert.DoesNotContain(p => p.Percentage == 100, progress.Items);
        Assert.Contains(p => p.Percentage == 99 && p.BytesTransferred == 10, progress.Items);
    }

    [TestMethod]
    public async Task Upload_SeekForTailVerificationDoesNotCountRepeatedReadsOrFinalSeek()
    {
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(Source(10, Payload(10)), SourceSession, "/source");
        ISftpService target = Target(async input =>
        {
            input.Seek(2, SeekOrigin.Begin);
            await input.ReadExactlyAsync(new byte[4]);
            input.Seek(3, SeekOrigin.Begin);
            await input.CopyToAsync(Stream.Null);
            input.Position = 0;
        });
        await staged.UploadAsync(target, TargetSession, "/target", resumeOffset: 6);
    }

    [TestMethod]
    public async Task Upload_CancellationWithholdsCompletionAndCacheCanBeRetried()
    {
        using var cts = new CancellationTokenSource();
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(Source(10, Payload(10)), SourceSession, "/source");
        var progress = new RecordingProgress();
        ISftpService target = Target(async input =>
        {
            await input.CopyToAsync(Stream.Null);
            cts.Cancel();
        });
        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            staged.UploadAsync(target, TargetSession, "/target", progress, cancellationToken: cts.Token));
        Assert.AreEqual(cts.Token, error.CancellationToken);
        Assert.DoesNotContain(p => p.Percentage == 100, progress.Items);
        await staged.UploadAsync(Target(input => input.CopyToAsync(Stream.Null)), TargetSession, "/target");
    }

    [TestMethod]
    [DataRow(10, 10)]
    [DataRow(4 * 1024 * 1024, 4 * 1024 * 1024)]
    [DataRow(4 * 1024 * 1024 + 1, 4 * 1024 * 1024 + 1)]
    [DataRow(0, 17)]
    [DataRow(-1, 17)]
    [DataRow(0, 0)]
    public async Task AllSizes_UseExclusiveUserTempFileUntilDisposed(int stat, int payloadLength)
    {
        ISftpService source = Source(stat, []);
        FileStream? file = null;
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            Stream sink = call.ArgAt<Stream>(2);
            file = AssertUserTempFile(sink);
            Assert.AreEqual(0L, file.Length);
            await sink.WriteAsync(Payload(payloadLength));
        });
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(source, SourceSession, "/source");
        Assert.IsNotNull(file);
        Assert.IsTrue(File.Exists(file.Name), "Prepare must retain the completed snapshot for upload/retry.");
        Assert.AreEqual(payloadLength, file.Length);
        Assert.ThrowsExactly<IOException>(() => { using FileStream other = File.OpenRead(file.Name); });
        await staged.UploadAsync(Target(input => input.CopyToAsync(Stream.Null)), TargetSession, "/target");
        Assert.IsTrue(File.Exists(file.Name));
        await staged.DisposeAsync();
        await staged.DisposeAsync();
        Assert.IsFalse(file.CanRead);
        Assert.IsFalse(File.Exists(file.Name));
        Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(file.Name)));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => staged.UploadAsync(Target(_ => Task.CompletedTask), TargetSession, "/target"));
    }

    [TestMethod]
    [DataRow(10, false)]
    [DataRow(10, true)]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(4 * 1024 * 1024 + 1, false)]
    [DataRow(4 * 1024 * 1024 + 1, true)]
    public async Task Temp_IsDeletedOnSourceFailureOrCancellation(int stat, bool cancel)
    {
        using var cts = new CancellationTokenSource();
        ISftpService source = Source(stat, []);
        var original = new IOException("source final reply");
        FileStream? file = null;
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            Stream sink = call.ArgAt<Stream>(2);
            file = AssertUserTempFile(sink);
            await sink.WriteAsync(Payload(stat > 0 ? stat : 3));
            if (cancel)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            throw original;
        });
        if (cancel)
        {
            OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                StagedRelaySource.PrepareAsync(source, SourceSession, "/source", cts.Token));
            Assert.AreEqual(cts.Token, error.CancellationToken);
        }
        else
        {
            IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => StagedRelaySource.PrepareAsync(source, SourceSession, "/source"));
            Assert.AreSame(original, error);
        }
        Assert.IsNotNull(file);
        Assert.IsFalse(file.CanRead);
        Assert.IsFalse(File.Exists(file.Name));
        Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(file.Name)));
    }

    private static FileStream AssertUserTempFile(Stream sink)
    {
        Assert.IsInstanceOfType<FileStream>(Storage(sink));
        var file = (FileStream)Storage(sink);
        Assert.AreEqual(Path.Combine(UserPathResolver.Home, ".velashell", "temp"), Path.GetDirectoryName(file.Name));
        Assert.IsTrue(File.Exists(file.Name));
        Assert.ThrowsExactly<IOException>(() => { using FileStream other = File.OpenRead(file.Name); });
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file.Name));
        return file;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Prepare_SynchronousWriteEntryPointsAndUploadReads(bool span)
    {
        ISftpService source = Source(3, []);
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(call =>
        {
            Stream sink = call.ArgAt<Stream>(2);
            byte[] bytes = [1, 2, 3];
            if (span) sink.Write(bytes.AsSpan());
            else sink.Write(bytes, 0, bytes.Length);
            sink.Flush();
            return Task.CompletedTask;
        });
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(source, SourceSession, "/source");
        await staged.UploadAsync(Target(input =>
        {
            byte[] bytes = new byte[3];
            Assert.AreEqual(3, span ? input.Read(bytes.AsSpan()) : input.Read(bytes, 0, 3));
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, bytes);
            return Task.CompletedTask;
        }), TargetSession, "/target");
    }

    [TestMethod]
    public async Task Prepare_CancellationAfterDownloadDeletesFileBeforeReturning()
    {
        using var cts = new CancellationTokenSource();
        ISftpService source = Source(3, []);
        FileStream? file = null;
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            Stream sink = call.ArgAt<Stream>(2);
            file = AssertUserTempFile(sink);
            await sink.WriteAsync(new byte[] { 1, 2, 3 });
            cts.Cancel(); // backend returned normally despite cancellation
        });
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => StagedRelaySource.PrepareAsync(source, SourceSession, "/source", cts.Token));
        Assert.IsNotNull(file);
        Assert.IsFalse(file.CanRead);
        Assert.IsFalse(File.Exists(file.Name));
    }

    [TestMethod]
    public async Task Prepare_CancelledBeforeStatDoesNoWork()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ISftpService source = Source(1, [1]);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => StagedRelaySource.PrepareAsync(source, SourceSession, "/source", cts.Token));
        Assert.IsEmpty(source.ReceivedCalls());
    }

    [TestMethod]
    public async Task PreparedCache_SourceCannotMutateRetainedDestination()
    {
        ISftpService source = Source(1, [1]);
        Stream? destination = null;
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
        {
            destination = call.ArgAt<Stream>(2);
            Assert.IsFalse(destination.CanSeek);
            Assert.ThrowsExactly<NotSupportedException>(() => destination.SetLength(1));
            await destination.WriteAsync(new byte[] { 7 });
        });
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(source, SourceSession, "/source");
        Assert.IsNotNull(destination);
        Assert.IsFalse(destination.CanWrite);
        Assert.ThrowsExactly<NotSupportedException>(() => destination.WriteByte(8));
        await destination.DisposeAsync(); // neither backend owns the cache
        await staged.UploadAsync(Target(input => input.CopyToAsync(Stream.Null)), TargetSession, "/target");
    }

    [TestMethod]
    [DataRow("write")]
    [DataRow("flush")]
    [DataRow("read")]
    [DataRow("seek")]
    public async Task LocalIoFailure_IsTypedPermanentAndDoesNotMaskSourceOrTarget(string operation)
    {
        var storage = new FaultStorage();
        ISftpService source = Source(3, [1, 2, 3]);
        if (operation is "write" or "flush")
        {
            storage.Fail = operation;
            RelayCacheException error = await Assert.ThrowsExactlyAsync<RelayCacheException>(() => PrepareWithStorage(source, () => storage));
            Assert.AreSame(storage.Error, error.InnerException);
            Assert.AreEqual(operation, error.Operation);
            Assert.IsTrue(storage.Disposed);
        }
        else
        {
            await using StagedRelaySource staged = await PrepareWithStorage(source, () => storage);
            storage.Fail = operation;
            RelayCacheException error = await Assert.ThrowsExactlyAsync<RelayCacheException>(() => staged.UploadAsync(
                Target(input => input.CopyToAsync(Stream.Null)), TargetSession, "/target"));
            Assert.AreSame(storage.Error, error.InnerException);
            Assert.AreEqual(operation, error.Operation);
        }
    }

    [TestMethod]
    public async Task LocalCreateFailure_IsWrappedBeforeDownload()
    {
        ISftpService source = Source(0, []);
        var original = new UnauthorizedAccessException("private path");
        RelayCacheException error = await Assert.ThrowsExactlyAsync<RelayCacheException>(() => PrepareWithStorage(source, () => throw original));
        Assert.AreSame(original, error.InnerException);
        Assert.AreEqual("create", error.Operation);
        Assert.DoesNotContain("private path", error.Message);
        await source.DidNotReceiveWithAnyArgs().DownloadStreamAsync(default, null!, null!);
    }

    [TestMethod]
    public async Task CleanupFailure_NeverReplacesPrimarySourceErrorOrCancellation()
    {
        var storage = new FaultStorage { FailDispose = true };
        ISftpService source = Source(3, [1, 2, 3]);
        var original = new OperationCanceledException();
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(Task.FromException(original));
        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PrepareWithStorage(source, () => storage));
        Assert.AreSame(original, error);
        Assert.IsTrue(storage.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Upload_ThroughSftpService_ReusesTailVerificationAndDurableHint(bool mismatch)
    {
        byte[] payload = Payload(100);
        ISftpService source = Source(100, payload);
        await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(source, SourceSession, "/source");
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        ISftpClientWrapper client = Substitute.For<ISftpClientWrapper>();
        client.IsConnected.Returns(true);
        connections.GetSession(TargetSession).Returns(new SshSession
        {
            SessionId = TargetSession,
            ConnectionInfo = new() { Host = "h", Port = 22, Username = "u", AuthMethod = AuthMethod.Password, Password = "p" },
            Status = SessionStatus.Connected,
        });
        await using var target = new SftpService(connections, _ => client);
        client.GetFileSizeAsync("/target", Arg.Any<CancellationToken>()).Returns(60L);
        client.ResumeSafetyMargin.Returns(10L);
        client.OpenAsync("/target", FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(mismatch ? new byte[60] : payload[..60])));
        client.UploadAsync(Arg.Any<Stream>(), "/target", Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new VelaSftpTransferInterruptedException("interrupted", 40, new IOException())));
        // With no settings service SftpService uses the mtime-bearing overload.
        client.UploadAsync(Arg.Any<Stream>(), "/target", Arg.Any<RemoteUploadOptions>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.ArgAt<RemoteUploadOptions>(2).ResumeOffset == 0)
                    return Task.FromException(new VelaSftpTransferInterruptedException("interrupted", 40, new IOException()));
                return ConsumeResumeAsync(call.ArgAt<Stream>(0), call.ArgAt<RemoteUploadOptions>(2).ResumeOffset);
            });
        client.UploadAsync(Arg.Any<Stream>(), "/target", Arg.Any<long>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(call => ConsumeResumeAsync(call.ArgAt<Stream>(0), call.ArgAt<long>(2)));
        async Task ConsumeResumeAsync(Stream input, long offset)
        {
            Assert.AreEqual(40L, offset, "The service's durable hint, not size-minus-margin (50), wins.");
            input.Position = offset;
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy);
            Assert.AreSequenceEqual(payload[40..], copy.ToArray());
        }
        await Assert.ThrowsExactlyAsync<VelaSftpTransferInterruptedException>(() => staged.UploadAsync(target, TargetSession, "/target"));
        if (mismatch)
            await Assert.ThrowsExactlyAsync<VelaSftpResumeMismatchException>(() => staged.UploadAsync(target, TargetSession, "/target", resumeOffset: 60));
        else
            await staged.UploadAsync(target, TargetSession, "/target", resumeOffset: 60);
        await source.ReceivedWithAnyArgs(1).DownloadStreamAsync(default, null!, null!);
    }

    private static Task<StagedRelaySource> PrepareWithStorage(ISftpService source, Func<Stream> factory) =>
        (Task<StagedRelaySource>)typeof(StagedRelaySource).GetMethod("PrepareCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [source, SourceSession, "/source", (Func<long, Stream>)(_ => factory()), CancellationToken.None])!;

    private static Stream Storage(Stream sink) => (Stream)sink.GetType()
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(Stream)).GetValue(sink)!;

    private sealed class FaultStorage : MemoryStream
    {
        public string? Fail { get; set; }
        public bool FailDispose { get; init; }
        public bool Disposed { get; private set; }
        public IOException Error { get; } = new("local device");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Fail == "write" ? ValueTask.FromException(Error) : base.WriteAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Fail == "flush" ? Task.FromException(Error) : base.FlushAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Fail == "read" ? ValueTask.FromException<int>(Error) : base.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => Fail == "seek" ? throw Error : base.Seek(offset, origin);
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            Dispose();
            return FailDispose ? ValueTask.FromException(Error) : ValueTask.CompletedTask;
        }
    }

    private static byte[] Payload(int size) => [.. Enumerable.Range(0, size).Select(i => (byte)(i * 31))];

    private static ISftpService Source(long stat, byte[] payload)
    {
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/source", Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo
        {
            Name = "source",
            FullPath = "/source",
            Size = stat,
            LastModified = Mtime,
            Permissions = "-rw-------",
            Owner = "u",
            Group = "g",
            IsDirectory = false,
        });
        source.DownloadStreamAsync(default, null!, null!).ReturnsForAnyArgs(async call =>
            await call.ArgAt<Stream>(2).WriteAsync(payload.AsMemory(), call.ArgAt<CancellationToken>(4)));
        return source;
    }

    private static ISftpService Target(Func<Stream, Task> consume)
    {
        ISftpService target = Substitute.For<ISftpService>();
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            await consume(call.ArgAt<Stream>(1));
            call.ArgAt<IProgress<TransferProgress>?>(5)?.Report(new TransferProgress
            {
                FileName = "target",
                TotalBytes = call.ArgAt<long>(3),
                BytesTransferred = call.ArgAt<long>(3),
                Percentage = 100,
                SpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero,
            });
        });
        return target;
    }

    private sealed class RecordingProgress : IProgress<TransferProgress>
    {
        public List<TransferProgress> Items { get; } = [];
        public void Report(TransferProgress value) => Items.Add(value);
    }
}
