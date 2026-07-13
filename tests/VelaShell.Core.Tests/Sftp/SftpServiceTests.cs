using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class SftpServiceTests
{
    private readonly ISshConnectionService _connectionService;
    private readonly Guid _sessionId;
    private readonly ISftpClientWrapper _sftpClient;
    private readonly ISftpService _sftpService;

    public SftpServiceTests()
    {
        _connectionService = Substitute.For<ISshConnectionService>();
        _sftpClient = Substitute.For<ISftpClientWrapper>();
        _sessionId = Guid.NewGuid();
        var session = new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new()
            {
                Host = "test.example.com",
                Port = 22,
                Username = "testuser",
                AuthMethod = AuthMethod.Password,
                Password = "testpass"
            },
            Status = SessionStatus.Connected
        };
        _connectionService.GetSession(_sessionId).Returns(session);
        _sftpClient.IsConnected.Returns(true);
        _sftpService = new SftpService(_connectionService, _ => _sftpClient);
    }

    [TestMethod]
    public async Task GetWorkingDirectoryAsync_ReturnsClientWorkingDirectory()
    {
        _sftpClient.WorkingDirectory.Returns("/home/testuser");
        string result = await _sftpService.GetWorkingDirectoryAsync(_sessionId);
        Assert.AreEqual("/home/testuser", result);
    }

    [TestMethod]
    public async Task CloseSessionAsync_DisconnectsAndDisposesClient()
    {
        // Open a channel so it gets cached, then close it.
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(new List<SftpEntry>()));
        await _sftpService.ListDirectoryAsync(_sessionId, "/");
        await _sftpService.CloseSessionAsync(_sessionId);
        _sftpClient.Received(1).Disconnect();
        _sftpClient.Received(1).Dispose();
    }

    [TestMethod]
    public async Task CloseSessionAsync_UnknownSession_IsNoOp()
    {
        await _sftpService.CloseSessionAsync(Guid.NewGuid());
        _sftpClient.DidNotReceive().Disconnect();
        _sftpClient.DidNotReceive().Dispose();
    }

    [TestMethod]
    public async Task RenameAsync_CallsRenameFileOnClient()
    {
        await _sftpService.RenameAsync(_sessionId, "/home/user/old.txt", "/home/user/new.txt");
        _sftpClient.Received(1).RenameFile("/home/user/old.txt", "/home/user/new.txt");
    }

    [TestMethod]
    public async Task RenameAsync_WhenPlainRenameRejected_FallsBackToPosixRename()
    {
        // Some servers answer the plain SSH_FXP_RENAME with SSH_FX_BAD_MESSAGE.
        _sftpClient
            .When(c => c.RenameFile("/home/user/dir", "/tmp/dir"))
            .Do(_ => throw new SftpOperationException("bad message"));
        await _sftpService.RenameAsync(_sessionId, "/home/user/dir", "/tmp/dir");
        _sftpClient.Received(1).PosixRenameFile("/home/user/dir", "/tmp/dir");
    }

    [TestMethod]
    public async Task RenameAsync_WhenBothRenamesFail_SurfacesOriginalError()
    {
        _sftpClient
            .When(c => c.RenameFile("/home/user/dir", "/tmp/dir"))
            .Do(_ => throw new SftpOperationException("bad message"));
        _sftpClient
            .When(c => c.PosixRenameFile("/home/user/dir", "/tmp/dir"))
            .Do(_ => throw new NotSupportedException("posix-rename not supported"));
        SftpOperationException ex = await Assert.ThrowsExactlyAsync<SftpOperationException>(() => _sftpService.RenameAsync(_sessionId, "/home/user/dir", "/tmp/dir"));
        Assert.AreEqual("bad message", ex.Message);
    }

    [TestMethod]
    public async Task UploadFileAsync_WhenCancelledMidTransfer_SurfacesCleanOperationCanceled()
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-up-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "payload");
        using var cts = new CancellationTokenSource();
        try
        {
            // Simulate the user cancelling mid-transfer: our token registration disposes the source
            // stream, so the underlying transfer fails with an IOException-style error. The service
            // must normalise that to a clean OperationCanceledException (never re-throw from the
            // progress callback, which SSH.NET runs on a detached thread and would crash the app).
            _sftpClient.UploadAsync(Arg.Any<Stream>(), "/home/user/up.txt",
                           Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           cts.Cancel();
                           throw new IOException("stream closed by cancellation");
                       });
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => _sftpService.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt", null, cts.Token));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task SetPermissionsAsync_CallsChangePermissionsOnClient()
    {
        await _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", 755);
        _sftpClient.Received(1).ChangePermissions("/home/user/run.sh", 755);
    }

    [TestMethod]
    [DataRow((short)-1)]
    [DataRow((short)778)] // last digit > 7
    [DataRow((short)787)] // middle digit > 7
    [DataRow((short)800)] // leading digit > 7
    public async Task SetPermissionsAsync_RejectsNonOctalModes(short mode)
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", mode));
        _sftpClient.DidNotReceive().ChangePermissions(Arg.Any<string>(), Arg.Any<short>());
    }

    [TestMethod]
    public async Task CreateFileAsync_UploadsAnEmptyStream()
    {
        await _sftpService.CreateFileAsync(_sessionId, "/home/user/empty.txt");
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(),
            "/home/user/empty.txt",
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ListDirectoryAsync_ReturnsRemoteFileInfoArray()
    {
        // Arrange
        var mockFiles = new List<SftpEntry>
        {
            CreateMockSftpFile("file1.txt", "/home/user/file1.txt", 1024, false, "rw-r--r--"),
            CreateMockSftpFile("dir1", "/home/user/dir1", 0, true, "rwxr-xr-x")
        };
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(mockFiles));

        // Act
        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        Assert.HasCount(2, result);
        Assert.AreEqual("file1.txt", result[0].Name);
        Assert.AreEqual("/home/user/file1.txt", result[0].FullPath);
        Assert.AreEqual(1024L, result[0].Size);
        Assert.IsFalse(result[0].IsDirectory);
        StringAssert.Contains(result[0].Permissions, "rw-r--r--");
        Assert.AreEqual("dir1", result[1].Name);
        Assert.IsTrue(result[1].IsDirectory);
        StringAssert.Contains(result[1].Permissions, "rwxr-xr-x");
    }

    [TestMethod]
    public async Task UploadFileAsync_VerifiesBytesWritten()
    {
        // Arrange
        string localPath = Path.GetTempFileName();
        byte[] testData = new byte[1024];
        new Random().NextBytes(testData);
        await File.WriteAllBytesAsync(localPath, testData);
        string remotePath = "/home/user/uploaded.bin";
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               callback(512);
                               callback(1024);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);
        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(),
            remotePath,
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
        Assert.IsNotEmpty(progressReports, "because progress should be reported during upload");
        File.Delete(localPath);
    }

    [TestMethod]
    public async Task DownloadFileAsync_VerifiesBytesRead()
    {
        // Arrange
        string localPath = Path.Combine(Path.GetTempPath(), $"download_{Guid.NewGuid()}.bin");
        string remotePath = "/home/user/remote.bin";
        SftpEntry mockFile = CreateMockSftpFile("remote.bin", remotePath, 2048, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));
        _sftpClient.DownloadAsync(remotePath,
                       Arg.Any<Stream>(),
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               callback(512);
                               callback(2048);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(async callInfo =>
                   {
                       Stream? stream = callInfo.Arg<Stream>();
                       byte[] testData = new byte[2048];
                       new Random().NextBytes(testData);
                       await stream.WriteAsync(testData);
                   });
        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert
        await _sftpClient.Received(1).DownloadAsync(remotePath,
            Arg.Any<Stream>(),
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
        Assert.IsTrue(File.Exists(localPath));
        Assert.IsNotEmpty(progressReports);
        Assert.AreEqual(2048L, progressReports.Last().BytesTransferred);
        if (File.Exists(localPath))
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_DeletesFile()
    {
        // Arrange
        string remotePath = "/home/user/todelete.txt";
        SftpEntry mockFile = CreateMockSftpFile("todelete.txt", remotePath, 1024, false, "rw-r--r--");
        _sftpClient.Exists(remotePath).Returns(true);
        _sftpClient.ListDirectory("/home/user")
                   .Returns([mockFile]);

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        _sftpClient.Received(1).DeleteFile(remotePath);
        _sftpClient.DidNotReceive().DeleteDirectory(Arg.Any<string>());
    }

    [TestMethod]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/newdir";

        // Act
        await _sftpService.CreateDirectoryAsync(_sessionId, remotePath);

        // Assert
        _sftpClient.Received(1).CreateDirectory(remotePath);
    }

    [TestMethod]
    public async Task DeleteAsync_WithDirectory_DeletesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/mydir";
        SftpEntry mockDir = CreateMockSftpFile("mydir", remotePath, 0, true, "rwxr-xr-x");
        _sftpClient.Exists(remotePath).Returns(true);
        _sftpClient.ListDirectory("/home/user")
                   .Returns([mockDir]);

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        _sftpClient.Received(1).DeleteDirectory(remotePath);
        _sftpClient.DidNotReceive().DeleteFile(Arg.Any<string>());
    }

    [TestMethod]
    public async Task DeleteAsync_RecursivelyDeletesDirectoryContents_AndReportsProgress()
    {
        const string dir = "/home/user/proj";
        SftpEntry mockDir = CreateMockSftpFile("proj", dir, 0, true, "rwxr-xr-x");
        SftpEntry childFile = CreateMockSftpFile("a.txt", "/home/user/proj/a.txt", 10, false, "rw-r--r--");
        SftpEntry childSub = CreateMockSftpFile("sub", "/home/user/proj/sub", 0, true, "rwxr-xr-x");
        SftpEntry grandchild = CreateMockSftpFile("b.txt", "/home/user/proj/sub/b.txt", 20, false, "rw-r--r--");
        _sftpClient.Exists(dir).Returns(true);
        _sftpClient.ListDirectory("/home/user").Returns([mockDir]); // parent listing → proj is a directory
        _sftpClient.ListDirectory(dir).Returns([childFile, childSub]);
        _sftpClient.ListDirectory("/home/user/proj/sub").Returns([grandchild]);
        var reports = new List<SftpDeleteProgress>();
        await _sftpService.DeleteAsync(_sessionId, dir,
            new SynchronousProgress<SftpDeleteProgress>(reports.Add));

        // Files removed, and each directory removed only after its contents.
        _sftpClient.Received(1).DeleteFile("/home/user/proj/a.txt");
        _sftpClient.Received(1).DeleteFile("/home/user/proj/sub/b.txt");
        _sftpClient.Received(1).DeleteDirectory("/home/user/proj/sub");
        _sftpClient.Received(1).DeleteDirectory(dir);
        Assert.HasCount(5, reports); // initial 0/total + 2 files + 2 directories
        Assert.AreEqual(0, reports[0].DeletedCount);
        Assert.AreEqual(4, reports[0].TotalCount);
        Assert.AreEqual(4, reports[^1].DeletedCount);
        Assert.AreEqual(4, reports[^1].TotalCount);
    }

    [TestMethod]
    public async Task DeleteAsync_WhenPathNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        string remotePath = "/home/user/nonexistent.txt";
        _sftpClient.Exists(remotePath).Returns(false);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _sftpService.DeleteAsync(_sessionId, remotePath));
    }

    [TestMethod]
    public async Task ListDirectoryAsync_OwnerAndGroup_AreNotBooleanStrings()
    {
        // Arrange
        SftpEntry mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 1024, false, "rw-r--r--")
            with
        { UserId = 1000, GroupId = 1000 };
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));

        // Act
        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        Assert.AreEqual("1000", result[0].Owner);
        Assert.AreEqual("1000", result[0].Group);
        Assert.AreNotEqual("True", result[0].Owner);
        Assert.AreNotEqual("False", result[0].Owner);
        Assert.AreNotEqual("True", result[0].Group);
        Assert.AreNotEqual("False", result[0].Group);
    }

    [TestMethod]
    public async Task DisposeAsync_DisconnectsAndDisposesAllClients()
    {
        // Arrange — trigger client caching by calling any method
        SftpEntry mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 0, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));
        await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Act
        await _sftpService.DisposeAsync();

        // Assert
        _sftpClient.Received(1).Disconnect();
        _sftpClient.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ListDirectoryAsync_WhenPermissionDenied_ThrowsException()
    {
        // Arrange
        _sftpClient.ListDirectoryAsync("/root/restricted", Arg.Any<CancellationToken>())
                   .Throws(new SftpPermissionDeniedException("Permission denied"));

        // Act & Assert
        await Assert.ThrowsExactlyAsync<SftpPermissionDeniedException>(() => _sftpService.ListDirectoryAsync(_sessionId, "/root/restricted"));
    }

    [TestMethod]
    public async Task UploadFileAsync_WithProgressCallback_FiresCorrectPercentages()
    {
        // Arrange
        string localPath = Path.GetTempFileName();
        byte[] testData = new byte[10000];
        await File.WriteAllBytesAsync(localPath, testData);
        string remotePath = "/home/user/upload.bin";
        var progressReports = new List<TransferProgress>();
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               // Simulate progress at 25%, 50%, 75%, 100%
                               callback(2500);
                               callback(5000);
                               callback(7500);
                               callback(10000);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);

        // Act
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert
        Assert.IsGreaterThanOrEqualTo(4, progressReports.Count());
        Assert.Contains(p => p.Percentage is >= 25 and < 35, progressReports);
        Assert.Contains(p => p.Percentage is >= 50 and < 60, progressReports);
        Assert.Contains(p => p.Percentage is >= 75 and < 85, progressReports);
        Assert.AreEqual(100, progressReports.Last().Percentage);

        // Cleanup
        File.Delete(localPath);
    }

    [TestMethod]
    public async Task GetFileInfoAsync_ReturnsFileInformation()
    {
        // Arrange
        string remotePath = "/home/user/info.txt";
        SftpEntry mockFile = CreateMockSftpFile("info.txt", remotePath, 4096, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));

        // Act
        RemoteFileInfo result = await _sftpService.GetFileInfoAsync(_sessionId, remotePath);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("info.txt", result.Name);
        Assert.AreEqual(remotePath, result.FullPath);
        Assert.AreEqual(4096L, result.Size);
        Assert.IsFalse(result.IsDirectory);
    }

    private static SftpEntry CreateMockSftpFile(string name, string fullName, long length, bool isDirectory, string permissions)
    {
        // SftpEntry 是 Core 的中立不可变记录(不再是 SSH 库的接口),直接构造即可。
        return new()
        {
            Name = name,
            FullName = fullName,
            Length = length,
            IsDirectory = isDirectory,
            LastWriteTime = DateTime.UtcNow,
            OwnerCanRead = permissions.Length > 0 && permissions[0] == 'r',
            OwnerCanWrite = permissions.Length > 1 && permissions[1] == 'w',
            OwnerCanExecute = permissions.Length > 2 && permissions[2] == 'x',
            GroupCanRead = permissions.Length > 3 && permissions[3] == 'r',
            GroupCanWrite = permissions.Length > 4 && permissions[4] == 'w',
            GroupCanExecute = permissions.Length > 5 && permissions[5] == 'x',
            OthersCanRead = permissions.Length > 6 && permissions[6] == 'r',
            OthersCanWrite = permissions.Length > 7 && permissions[7] == 'w',
            OthersCanExecute = permissions.Length > 8 && permissions[8] == 'x'
        };
    }

    private class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }
}
