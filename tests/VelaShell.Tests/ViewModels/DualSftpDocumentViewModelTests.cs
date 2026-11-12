using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 「远程 + 远程」双栏文件标签:两栏之间的流式中转、连接的归属与收尾、目录比较。
/// </summary>
[TestClass]
[TestCategory("FileBrowser")]
public class DualSftpDocumentViewModelTests
{
    private static readonly Guid Left = Guid.NewGuid();
    private static readonly Guid Right = Guid.NewGuid();

    private static RemoteFileInfo Entry(string path, long size = 10, bool directory = false, DateTime? modified = null) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        FullPath = path,
        Size = size,
        Permissions = directory ? "drwxr-xr-x" : "-rw-r--r--",
        IsDirectory = directory,
        LastModified = modified ?? new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        Owner = "u",
        Group = "g",
    };

    /// <summary>两栏共用一个服务(与真实的路由服务一样按会话分派),彼此互为另一栏。</summary>
    private static (ISftpService Sftp, FileBrowserViewModel LeftPane, FileBrowserViewModel RightPane) PanePair(TransferOptions? options = null)
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        sftp.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            if (call.ArgAt<long>(6) > 0)
            {
                input.Seek(call.ArgAt<long>(6), SeekOrigin.Begin);
            }
            await input.CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
        });
        options ??= new() { ConflictPolicy = "overwrite", ResumeEnabled = false };
        var left = new FileBrowserViewModel(sftp, Left) { ServerDisplayName = "alpha", TransferOptions = options };
        var right = new FileBrowserViewModel(sftp, Right) { ServerDisplayName = "beta", TransferOptions = options };
        left.DualPeer = right;
        right.DualPeer = left;
        return (sftp, left, right);
    }

    private static void Readable(ISftpService sftp, Guid session, RemoteFileInfo file)
    {
        sftp.GetFileInfoAsync(session, file.FullPath, Arg.Any<CancellationToken>()).Returns(file);
        sftp.OpenReadAsync(session, file.FullPath, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[file.Size])));
    }

    [TestMethod]
    public async Task ReceiveFromPeer_StreamsFilesAndRecreatesFolders_OnThisSession()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) = PanePair();
        RemoteFileInfo file = Entry("/src/a.txt", 5);
        RemoteFileInfo folder = Entry("/src/dir", directory: true);
        RemoteFileInfo nested = Entry("/src/dir/b.txt", 7);
        sftp.ListDirectoryAsync(Left, "/src/dir", Arg.Any<CancellationToken>()).Returns([nested]);
        Readable(sftp, Left, file);
        Readable(sftp, Left, nested);

        await right.ReceiveFromPeerAsync([new(file), new(folder)]);

        await sftp.Received(1).EnsureDirectoryAsync(Right, "/dir", Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadStreamAsync(Right, Arg.Any<Stream>(), "/a.txt", 5, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadStreamAsync(Right, Arg.Any<Stream>(), "/dir/b.txt", 7, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
        await sftp.Received(1).OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>());
        await sftp.Received(1).OpenReadAsync(Left, nested.FullPath, Arg.Any<CancellationToken>());
        await sftp.DidNotReceiveWithAnyArgs().DownloadStreamAsync(default, null!, null!);
        // SFTP 中转不经本地文件:任何一次上传/下载本地文件的调用都说明字节落了盘。
        await sftp.DidNotReceiveWithAnyArgs().DownloadFileAsync(default, null!, null!);
        await sftp.DidNotReceiveWithAnyArgs().UploadFileAsync(default, null!, null!);
        Assert.IsNull(right.ErrorMessage);
    }

    [TestMethod]
    public async Task ReceiveFromPeer_WithoutAPeer_DoesNothing()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var lonely = new FileBrowserViewModel(sftp, Right);

        await lonely.ReceiveFromPeerAsync([new(Entry("/src/a.txt"))]);

        Assert.IsEmpty(sftp.ReceivedCalls());
    }

    [TestMethod]
    public async Task ReceiveFromPeer_SkipPolicy_LeavesAnExistingTargetAlone()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) = PanePair(new() { ConflictPolicy = "skip", ResumeEnabled = false });
        sftp.ListDirectoryAsync(Right, "/", Arg.Any<CancellationToken>()).Returns([Entry("/a.txt")]);
        RemoteFileInfo file = Entry("/src/a.txt");
        Readable(sftp, Left, file);

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    public async Task ARelayThatFailsBeforeWriting_NeverDeletesTheExistingTarget()
    {
        // 源在规划之后被删掉:目标还没被碰过。若照「半截文件」清理,删掉的会是用户原有的同名文件。
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "overwrite", ResumeEnabled = false, AutoCleanTempFiles = true });
        RemoteFileInfo file = Entry("/src/a.txt");
        sftp.GetFileInfoAsync(Left, file.FullPath, Arg.Any<CancellationToken>()).ThrowsAsync(new FileNotFoundException("gone"));

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    [TestMethod]
    public async Task ARelayThatFailsWhileWriting_KeepsThePartialTarget_WhenResumeIsOn()
    {
        // 与上传同一口径:开着断点续传,写到一半的目标就是下次续传的素材,不能清。
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "overwrite", ResumeEnabled = true, AutoCleanTempFiles = true });
        RemoteFileInfo file = Entry("/src/a.txt");
        Readable(sftp, Left, file);
        FailWhileWriting(sftp, "/a.txt");

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    [TestMethod]
    public async Task ARelayThatFailsWhileWriting_CleansUpThePartialTarget_WhenResumeIsOff()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "overwrite", ResumeEnabled = false, AutoCleanTempFiles = true });
        RemoteFileInfo file = Entry("/src/a.txt");
        Readable(sftp, Left, file);
        FailWhileWriting(sftp, "/a.txt");

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.Received(1).DeleteAsync(Right, "/a.txt", Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    public async Task ReceiveFromPeer_ShortSourceFailsInsteadOfCompleting_AndCleansPartialTarget(int bytes)
    {
        // 可 seek 源仍走原上传路径:不能用新增的顺序源预读行为取代这条半截目标清理回归。
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "overwrite", ResumeEnabled = false, AutoCleanTempFiles = true, TransferMaxRetries = 0 });
        RemoteFileInfo file = Entry("/src/a.txt", 10);
        Readable(sftp, Left, file);
        sftp.OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[bytes])));
        right.TransferSink = new FileTransferViewModel();

        await right.ReceiveFromPeerAsync([new(file)]);

        Assert.HasCount(1, right.TransferSink.Transfers);
        Assert.AreEqual(TransferStatus.Failed, right.TransferSink.Transfers[0].Status);
        await sftp.Received(1).DeleteAsync(Right, "/a.txt", Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(4, false)]
    [DataRow(10, true)]
    public async Task ReceiveFromPeer_SmallSequentialSourceFailure_NeverUploadsOrDeletesExistingTarget(int bytes, bool failCompletion)
    {
        (ISftpService sftp, _, FileBrowserViewModel right) = PanePair(new()
        {
            ConflictPolicy = "overwrite",
            ResumeEnabled = false,
            AutoCleanTempFiles = true,
            TransferMaxRetries = 0,
        });
        RemoteFileInfo file = Entry("/src/a.txt", 10);
        sftp.ListDirectoryAsync(Right, "/", Arg.Any<CancellationToken>()).Returns([Entry("/a.txt", 23)]);
        sftp.GetFileInfoAsync(Left, file.FullPath, Arg.Any<CancellationToken>()).Returns(file);
        var source = new SequentialSource(new byte[bytes], failCompletion);
        sftp.OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream>(source));
        right.TransferSink = new FileTransferViewModel();

        await right.ReceiveFromPeerAsync([new(file)]);

        Assert.HasCount(1, right.TransferSink.Transfers);
        Assert.AreEqual(TransferStatus.Failed, right.TransferSink.Transfers[0].Status);
        Assert.IsTrue(source.Disposed, "小顺序源失败也必须释放,包括源 completion 失败的情况。");
        await sftp.Received(1).OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>());
        await sftp.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
        await sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    /// <summary>与 FTP 数据流一样只准顺序读;释放时可模拟最终回复失败,不能用可 seek 的 MemoryStream 替代。</summary>
    private sealed class SequentialSource(byte[] data, bool failCompletion) : Stream
    {
        private readonly MemoryStream _input = new(data, writable: false);
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.LongLength;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (Disposed) return;
            Disposed = true;
            if (disposing) _input.Dispose();
            base.Dispose(disposing);
            if (disposing && failCompletion) throw new IOException("FTP completion refused.");
        }
    }

    [TestMethod]
    public async Task ReceiveFromPeer_RetryDiagnosticsCaptureAnEarlySourceFailureEvenWhenItRecovers()
    {
        string logDirectory = Path.Combine(Path.GetTempPath(), "vela-relay-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            (ISftpService sftp, _, FileBrowserViewModel right) = PanePair(new()
            {
                ConflictPolicy = "overwrite",
                ResumeEnabled = false,
                TransferMaxRetries = 1,
                TransferLogging = true,
                LogDirectory = logDirectory,
            });
            RemoteFileInfo file = Entry("/src/a.txt", 10);
            Readable(sftp, Left, file);
            sftp.OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>()).Returns(
                _ => Task.FromResult<Stream>(new ThrowOnReadStream()),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[10])));
            right.RetryDelay = (_, _) => Task.CompletedTask;
            right.TransferSink = new FileTransferViewModel();

            await right.ReceiveFromPeerAsync([new(file)]);

            Assert.AreEqual(TransferStatus.Completed, right.TransferSink.Transfers.Single().Status);
            await sftp.Received(2).OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>());
            string log = File.ReadAllText(Directory.GetFiles(logDirectory, "transfer-*.log").Single());
            string diagnostic = log.Split('\n').Single(line => line.Contains("ATTEMPT_FAILED", StringComparison.Ordinal));
            // 源读不出来是链路问题:记一条诊断,并如实标出它会自动重试。
            Assert.Contains("stage=transfer", diagnostic);
            Assert.Contains("retry=True", diagnostic);
            Assert.Contains("kind=Transient", diagnostic);
            Assert.IsFalse(diagnostic.Contains(file.FullPath, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
        }
    }

    /// <summary>一读就断的源(FTP 数据连接被对端关掉的形态)。</summary>
    private sealed class ThrowOnReadStream : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    /// <summary>
    /// 源与元数据长度对不上不算「临时」:重试还是同一份数据。以前它归成临时,
    /// 一份这种文件要连撞满设置里的次数才报错 —— 传输日志里成片的 source-length。
    /// </summary>
    [TestMethod]
    public async Task ReceiveFromPeer_LengthMismatch_IsReportedOnceAndNeverRetried()
    {
        string logDirectory = Path.Combine(Path.GetTempPath(), "vela-relay-length-" + Guid.NewGuid().ToString("N"));
        try
        {
            (ISftpService sftp, _, FileBrowserViewModel right) = PanePair(new()
            {
                ConflictPolicy = "overwrite",
                ResumeEnabled = false,
                TransferMaxRetries = 3,
                TransferLogging = true,
                LogDirectory = logDirectory,
            });
            RemoteFileInfo file = Entry("/src/a.txt", 10);
            Readable(sftp, Left, file);
            sftp.OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>()).Returns(
                _ => Task.FromResult<Stream>(new MemoryStream([])));
            right.RetryDelay = (_, _) => throw new AssertFailedException("长度不符不该进入重试等待。");
            right.TransferSink = new FileTransferViewModel();

            await right.ReceiveFromPeerAsync([new(file)]);

            Assert.AreEqual(TransferStatus.Failed, right.TransferSink.Transfers.Single().Status);
            await sftp.Received(1).OpenReadAsync(Left, file.FullPath, Arg.Any<CancellationToken>());
            string log = File.ReadAllText(Directory.GetFiles(logDirectory, "transfer-*.log").Single());
            Assert.ContainsSingle(line => line.Contains("ATTEMPT_FAILED", StringComparison.Ordinal),
log.Split('\n'), "只尝试一次,不再自动重试。");
            Assert.Contains("kind=Permanent", log);
        }
        finally
        {
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReceiveFromPeer_WithAPartialTarget_ResumesFromIt()
    {
        // 上一次中转在目标上留下了 4 字节,源有 10 字节:从 4 接着传(真正的起点由目标端再核实),
        // 而不是按同名冲突去问覆盖还是跳过。
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "ask", ResumeEnabled = true });
        sftp.ListDirectoryAsync(Right, "/", Arg.Any<CancellationToken>()).Returns([Entry("/a.txt", 4)]);
        sftp.GetFileInfoAsync(Right, "/a.txt", Arg.Any<CancellationToken>()).Returns(Entry("/a.txt", 4));
        RemoteFileInfo file = Entry("/src/a.txt", 10);
        Readable(sftp, Left, file);
        right.ConfirmRemoteOverwrite = _ => throw new AssertFailedException("续传不该弹同名冲突框。");

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.Received(1).UploadStreamAsync(Right, Arg.Any<Stream>(), "/a.txt", 10, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), 4, Arg.Any<uint?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ReceiveFromPeer_WithAFullSizeTarget_IsAConflictNotAResume()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "skip", ResumeEnabled = true });
        sftp.ListDirectoryAsync(Right, "/", Arg.Any<CancellationToken>()).Returns([Entry("/a.txt", 10)]);
        sftp.GetFileInfoAsync(Right, "/a.txt", Arg.Any<CancellationToken>()).Returns(Entry("/a.txt", 10));
        RemoteFileInfo file = Entry("/src/a.txt", 10);
        Readable(sftp, Left, file);

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    [DataRow(ConnectionType.FTP, ConnectionType.SFTP)]
    [DataRow(ConnectionType.SSH, ConnectionType.FTP)]
    [DataRow(ConnectionType.SFTP, ConnectionType.Plugin)]
    public async Task OnlyFtpPanes_UseStreamedRelaySources(ConnectionType leftType, ConnectionType rightType)
    {
        await using var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", leftType), Left, null, (_, _) => Task.CompletedTask),
            new(Profile("beta", rightType), Right, null, (_, _) => Task.CompletedTask),
            Substitute.For<ISftpService>(), new TransferOptions());
        await vm.InitialLoadTask;

        // FTP / FTPS 的数据连接是顺序的:作为中转源时走流管道,而不是整份缓存到本地。
        Assert.AreEqual(leftType == ConnectionType.FTP, vm.LeftFiles.UseStreamedRelay);
        Assert.AreEqual(rightType == ConnectionType.FTP, vm.RightFiles.UseStreamedRelay);
        // 旧的整份缓存路径默认不再启用,但实现留着(见 DualSftpDocumentViewModel.IsStagedRelayEnabled)。
        Assert.IsFalse(vm.LeftFiles.UseStagedRelaySource);
        Assert.IsFalse(vm.RightFiles.UseStagedRelaySource);
    }

    [TestMethod]
    public async Task APaneThatCannotReceive_DisablesTheCopyTowardIt_AndRefusesTransfers()
    {
        // 右栏是一个没实现流式上传的插件协议:往它那边的按钮置灰,代码调用也被拒并说明原因;反方向照常。
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (_, _) => Task.CompletedTask),
            new(Profile("bucket", ConnectionType.Plugin), Right, null, (_, _) => Task.CompletedTask) { AcceptsStreamedUploads = false },
            sftp,
            new TransferOptions());

        Assert.IsFalse(await vm.CopyLeftToRightCommand.CanExecute.FirstAsync());
        Assert.IsTrue(await vm.CopyRightToLeftCommand.CanExecute.FirstAsync());
        await vm.RightFiles.ReceiveFromPeerAsync([new(Entry("/src/a.txt"))]);
        Assert.IsNotNull(vm.RightFiles.ErrorMessage);
        await sftp.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
        await vm.CloseAsync();
    }

    [TestMethod]
    public async Task APluginPane_GetsItsProtocolsRightClickActions()
    {
        (string Action, string Path)? invoked = null;
        var protocol = new PluginSdk.Protocols.ProtocolDescriptor
        {
            Id = "acme.s3",
            DisplayName = "S3",
            Actions = [new("share", "Copy share link", PluginSdk.Protocols.ProtocolActionScope.File)],
        };
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (_, _) => Task.CompletedTask),
            new(Profile("bucket", ConnectionType.Plugin), Right, null, (_, _) => Task.CompletedTask)
            {
                Protocol = protocol,
                InvokeProtocolAction = (action, path) =>
                {
                    invoked = (action, path);
                    return Task.CompletedTask;
                },
            },
            Substitute.For<ISftpService>(),
            new TransferOptions());

        Assert.IsNotNull(vm.RightFiles.InvokeProtocolAction, "插件栏要带上协议的右键动作。");
        Assert.IsNull(vm.LeftFiles.InvokeProtocolAction);
        await vm.RightFiles.InvokeProtocolAction("share", "/bucket/a.txt");
        Assert.AreEqual(("share", "/bucket/a.txt"), invoked);
        await vm.CloseAsync();
    }

    private static void FailWhileWriting(ISftpService sftp, string target) =>
        sftp.UploadStreamAsync(Right, Arg.Any<Stream>(), target, Arg.Any<long>(), Arg.Any<DateTime?>(),
                Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("connection reset"));

    [TestMethod]
    public async Task Close_DisconnectsBothSessions_EvenWhenTheFirstDisconnectThrows()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var disconnected = new List<Guid>();
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (id, _) =>
            {
                disconnected.Add(id);
                throw new IOException("already gone");
            }),
            new(Profile("beta", ConnectionType.FTP), Right, null, (id, _) =>
            {
                disconnected.Add(id);
                return Task.CompletedTask;
            }),
            sftp,
            new TransferOptions());

        await Assert.ThrowsExactlyAsync<IOException>(() => vm.CloseAsync());

        Assert.AreSequenceEqual([Left, Right], disconnected, SequenceOrder.InAnyOrder, "一条断开失败不能让另一条留着没人关。");
        Assert.AreSequenceEqual([Left, Right], [.. vm.SessionIds]);
        Assert.AreEqual("alpha ⇄ beta", vm.Title);
    }

    [TestMethod]
    public void TheSameProfileOnBothSides_IsRejected()
    {
        SessionProfile profile = Profile("alpha", ConnectionType.SSH);

        Assert.ThrowsExactly<ArgumentException>(() => new DualSftpDocumentViewModel(
            new(profile, Left, null, (_, _) => Task.CompletedTask),
            new(profile, Right, null, (_, _) => Task.CompletedTask),
            Substitute.For<ISftpService>(),
            new TransferOptions()));
    }

    [TestMethod]
    public void Status_IsTheWorseOfTheTwo()
    {
        Assert.AreEqual(SessionStatus.Error, DualSftpDocumentViewModel.Worse(SessionStatus.Connected, SessionStatus.Error));
        Assert.AreEqual(SessionStatus.Disconnected, DualSftpDocumentViewModel.Worse(SessionStatus.Disconnected, SessionStatus.Connecting));
        Assert.AreEqual(SessionStatus.Connected, DualSftpDocumentViewModel.Worse(SessionStatus.Connected, SessionStatus.Connected));
    }

    [TestMethod]
    public async Task CompareDirectories_SelectsWhatDiffersOnEachSide()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        DateTime t = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        sftp.GetWorkingDirectoryAsync(Left, Arg.Any<CancellationToken>()).Returns("/l");
        sftp.GetWorkingDirectoryAsync(Right, Arg.Any<CancellationToken>()).Returns("/r");
        sftp.ListDirectoryAsync(Left, "/l", Arg.Any<CancellationToken>()).Returns(
        [
            Entry("/l/same.txt", 3, modified: t),
            Entry("/l/only-left.txt", 3, modified: t),
            Entry("/l/newer.txt", 3, modified: t.AddHours(1)),
        ]);
        sftp.ListDirectoryAsync(Right, "/r", Arg.Any<CancellationToken>()).Returns(
        [
            Entry("/r/same.txt", 3, modified: t),
            Entry("/r/newer.txt", 3, modified: t),
            Entry("/r/only-right.txt", 3, modified: t),
        ]);
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (_, _) => Task.CompletedTask),
            new(Profile("beta", ConnectionType.SFTP), Right, null, (_, _) => Task.CompletedTask),
            sftp,
            new TransferOptions());
        await vm.InitialLoadTask;

        await vm.CompareDirectoriesCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(["only-left.txt", "newer.txt"], [.. vm.LeftFiles.SelectedFiles.Select(f => f.Name)], SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["only-right.txt"], [.. vm.RightFiles.SelectedFiles.Select(f => f.Name)], SequenceOrder.InAnyOrder);
        Assert.IsNotNull(vm.CompareSummary);
        await vm.CloseAsync();
    }

    private static SessionProfile Profile(string name, ConnectionType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Username = "u",
        ConnectionType = type,
    };
}
