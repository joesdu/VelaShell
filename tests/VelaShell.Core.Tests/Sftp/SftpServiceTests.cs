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
        await _sftpClient.Received(1).RenameFileAsync("/home/user/old.txt", "/home/user/new.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RenameAsync_WhenPlainRenameRejected_FallsBackToPosixRename()
    {
        // Some servers answer the plain SSH_FXP_RENAME with SSH_FX_BAD_MESSAGE.
        _sftpClient
            .RenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new SftpOperationException("bad message"));
        await _sftpService.RenameAsync(_sessionId, "/home/user/dir", "/tmp/dir");
        await _sftpClient.Received(1).PosixRenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RenameAsync_WhenBothRenamesFail_SurfacesOriginalError()
    {
        _sftpClient
            .RenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new SftpOperationException("bad message"));
        _sftpClient
            .PosixRenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException("posix-rename not supported"));
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
        await _sftpClient.Received(1).ChangePermissionsAsync("/home/user/run.sh", 755, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow((short)-1)]
    [DataRow((short)778)] // last digit > 7
    [DataRow((short)787)] // middle digit > 7
    [DataRow((short)800)] // leading digit > 7
    public async Task SetPermissionsAsync_RejectsNonOctalModes(short mode)
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", mode));
        await _sftpClient.DidNotReceive().ChangePermissionsAsync(Arg.Any<string>(), Arg.Any<short>(), Arg.Any<CancellationToken>());
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
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(true);
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).DeleteFileAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/newdir";

        // Act
        await _sftpService.CreateDirectoryAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task DeleteAsync_WithDirectory_DeletesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/mydir";
        SftpEntry mockDir = CreateMockSftpFile("mydir", remotePath, 0, true, "rwxr-xr-x");
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(true);
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockDir]));
        _sftpClient.ListDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).DeleteDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task DeleteAsync_RecursivelyDeletesDirectoryContents_AndReportsProgress()
    {
        const string dir = "/home/user/proj";
        SftpEntry mockDir = CreateMockSftpFile("proj", dir, 0, true, "rwxr-xr-x");
        SftpEntry childFile = CreateMockSftpFile("a.txt", "/home/user/proj/a.txt", 10, false, "rw-r--r--");
        SftpEntry childSub = CreateMockSftpFile("sub", "/home/user/proj/sub", 0, true, "rwxr-xr-x");
        SftpEntry grandchild = CreateMockSftpFile("b.txt", "/home/user/proj/sub/b.txt", 20, false, "rw-r--r--");
        _sftpClient.ExistsAsync(dir, Arg.Any<CancellationToken>()).Returns(true);
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockDir])); // parent listing → proj is a directory
        _sftpClient.ListDirectoryAsync(dir, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([childFile, childSub]));
        _sftpClient.ListDirectoryAsync("/home/user/proj/sub", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([grandchild]));
        var reports = new List<SftpDeleteProgress>();
        await _sftpService.DeleteAsync(_sessionId, dir,
            new SynchronousProgress<SftpDeleteProgress>(reports.Add));

        // Files removed, and each directory removed only after its contents.
        await _sftpClient.Received(1).DeleteFileAsync("/home/user/proj/a.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteFileAsync("/home/user/proj/sub/b.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync("/home/user/proj/sub", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(dir, Arg.Any<CancellationToken>());
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
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(false);

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

    // —— 属主/属组的数字 id → 名称翻译(见 RemoteIdentityResolver)————————————

    /// <summary>查表命令的返回样本:passwd 段 + 分隔标记 + group 段。</summary>
    private const string IdentityLookupOutput = """
                                                root:x:0:0:root:/root:/bin/bash
                                                deploy:x:1000:1000:Deploy User:/home/deploy:/bin/bash
                                                ###VELA-GROUPS###
                                                root:x:0:
                                                www-data:x:33:
                                                """;

    [TestMethod]
    public async Task ListDirectoryAsync_TranslatesNumericIdsUsingRemotePasswdDatabase()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("app.log", "/srv/app.log", 10, false, "rw-r--r--") with { UserId = 1000, GroupId = 33 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.AreEqual("deploy", result[0].Owner);
        Assert.AreEqual("www-data", result[0].Group);
    }

    [TestMethod]
    public async Task ListDirectoryAsync_IdWithoutPasswdEntry_FallsBackToNumericId()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("orphan", "/srv/orphan", 10, false, "rw-r--r--") with { UserId = 4242, GroupId = 4242 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.AreEqual("4242", result[0].Owner);
        Assert.AreEqual("4242", result[0].Group);
    }

    /// <summary>仅 SFTP、没有 exec 通道的主机:查表整个失败,不该拖垮列目录。</summary>
    [TestMethod]
    public async Task ListDirectoryAsync_WhenIdentityLookupFails_StillListsWithNumericIds()
    {
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        sshClient.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(new InvalidOperationException("no exec channel"));
        _connectionService.GetClient(_sessionId).Returns(sshClient);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("app.log", "/srv/app.log", 10, false, "rw-r--r--") with { UserId = 1000, GroupId = 33 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.HasCount(1, result);
        Assert.AreEqual("1000", result[0].Owner);
        Assert.AreEqual("33", result[0].Group);
    }

    /// <summary>整表只查一次:切目录不该每次都往返一条 getent。</summary>
    [TestMethod]
    public async Task ListDirectoryAsync_QueriesIdentityDatabaseOncePerSession()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/a");
        await _sftpService.ListDirectoryAsync(_sessionId, "/b");
        await _sftpService.ListDirectoryAsync(_sessionId, "/c");

        await sshClient.Received(1).RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>会话关闭要丢弃查表缓存,重连后才会按新主机重新查。</summary>
    [TestMethod]
    public async Task CloseSessionAsync_DiscardsIdentityCache()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/a");
        await _sftpService.CloseSessionAsync(_sessionId);
        await _sftpService.ListDirectoryAsync(_sessionId, "/a");

        await sshClient.Received(2).RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 分隔标记以 '#' 开头,在命令里必须带引号:裸写时 shell 会把它当注释,连同其后的
    /// group 查询一起吞掉,于是只有 passwd 段跑出来 —— 属主显示名称而属组回退数字。
    /// 其余查表测试都直接喂假输出,测不到命令本身,故在此守住。
    /// </summary>
    [TestMethod]
    public async Task ListDirectoryAsync_IdentityLookupCommand_QuotesSectionSeparator()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        string command = (string)sshClient.ReceivedCalls()
                                          .Single(c => c.GetMethodInfo().Name == nameof(ISshClientWrapper.RunCommandAsync))
                                          .GetArguments()[0]!;
        StringAssert.Contains(command, "echo '###VELA-GROUPS###'");
        Assert.IsFalse(command.Contains("echo ###", StringComparison.Ordinal),
                       "分隔标记未加引号,group 段会被 shell 当注释吞掉。");
    }

    private ISshClientWrapper GivenIdentityLookupReturns(string output)
    {
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        sshClient.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(output));
        _connectionService.GetClient(_sessionId).Returns(sshClient);
        return sshClient;
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
