using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;
using PulseTerm.Core.Ssh;
using Renci.SshNet.Sftp;
using Renci.SshNet.Common;
using ConnectionInfo = PulseTerm.Core.Models.ConnectionInfo;

namespace PulseTerm.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class SftpServiceTests
{
    private readonly ISshConnectionService _connectionService;
    private readonly ISftpService _sftpService;
    private readonly Guid _sessionId;
    private readonly ISftpClientWrapper _sftpClient;

    public SftpServiceTests()
    {
        _connectionService = Substitute.For<ISshConnectionService>();
        _sftpClient = Substitute.For<ISftpClientWrapper>();
        _sessionId = Guid.NewGuid();

        var session = new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new ConnectionInfo
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

        var result = await _sftpService.GetWorkingDirectoryAsync(_sessionId);

        Assert.AreEqual("/home/testuser", result);
    }

    [TestMethod]
    public async Task CloseSessionAsync_DisconnectsAndDisposesClient()
    {
        // Open a channel so it gets cached, then close it.
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new List<ISftpFile>()));
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
    public async Task SetPermissionsAsync_CallsChangePermissionsOnClient()
    {
        await _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", 755);

        _sftpClient.Received(1).ChangePermissions("/home/user/run.sh", 755);
    }

    [TestMethod]
    [DataRow((short)-1)]
    [DataRow((short)778)]   // last digit > 7
    [DataRow((short)787)]   // middle digit > 7
    [DataRow((short)800)]   // leading digit > 7
    public async Task SetPermissionsAsync_RejectsNonOctalModes(short mode)
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", mode));

        _sftpClient.DidNotReceive().ChangePermissions(Arg.Any<string>(), Arg.Any<short>());
    }

    [TestMethod]
    public async Task CreateFileAsync_UploadsAnEmptyStream()
    {
        await _sftpService.CreateFileAsync(_sessionId, "/home/user/empty.txt");

        await _sftpClient.Received(1).UploadAsync(
            Arg.Any<Stream>(),
            "/home/user/empty.txt",
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ListDirectoryAsync_ReturnsRemoteFileInfoArray()
    {
        // Arrange
        var mockFiles = new List<ISftpFile>
        {
            CreateMockSftpFile("file1.txt", "/home/user/file1.txt", 1024, false, "rw-r--r--"),
            CreateMockSftpFile("dir1", "/home/user/dir1", 0, true, "rwxr-xr-x")
        };

        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(mockFiles));

        // Act
        var result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        Assert.AreEqual(2, result.Count());
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
        var localPath = Path.GetTempFileName();
        var testData = new byte[1024];
        new Random().NextBytes(testData);
        await File.WriteAllBytesAsync(localPath, testData);

        var remotePath = "/home/user/uploaded.bin";

        _sftpClient.UploadAsync(
            Arg.Any<Stream>(),
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
        await _sftpClient.Received(1).UploadAsync(
            Arg.Any<Stream>(),
            remotePath,
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());

        Assert.IsTrue(progressReports.Any(), "because progress should be reported during upload");

        File.Delete(localPath);
    }

    [TestMethod]
    public async Task DownloadFileAsync_VerifiesBytesRead()
    {
        // Arrange
        var localPath = Path.Combine(Path.GetTempPath(), $"download_{Guid.NewGuid()}.bin");
        var remotePath = "/home/user/remote.bin";

        var mockFile = CreateMockSftpFile("remote.bin", remotePath, 2048, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        _sftpClient.DownloadAsync(
            remotePath,
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
                var stream = callInfo.Arg<Stream>();
                var testData = new byte[2048];
                new Random().NextBytes(testData);
                await stream.WriteAsync(testData);
            });

        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert
        await _sftpClient.Received(1).DownloadAsync(
            remotePath,
            Arg.Any<Stream>(),
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());

        Assert.IsTrue(File.Exists(localPath));
        Assert.IsTrue(progressReports.Any());
        Assert.AreEqual(2048L, progressReports.Last().BytesTransferred);

        if (File.Exists(localPath))
            File.Delete(localPath);
    }

    [TestMethod]
    public async Task DeleteAsync_DeletesFile()
    {
        // Arrange
        var remotePath = "/home/user/todelete.txt";
        var mockFile = CreateMockSftpFile("todelete.txt", remotePath, 1024, false, "rw-r--r--");

        _sftpClient.Exists(remotePath).Returns(true);
        _sftpClient.ListDirectory("/home/user")
            .Returns(new[] { mockFile });

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
        var remotePath = "/home/user/newdir";

        // Act
        await _sftpService.CreateDirectoryAsync(_sessionId, remotePath);

        // Assert
        _sftpClient.Received(1).CreateDirectory(remotePath);
    }

    [TestMethod]
    public async Task DeleteAsync_WithDirectory_DeletesDirectory()
    {
        // Arrange
        var remotePath = "/home/user/mydir";
        var mockDir = CreateMockSftpFile("mydir", remotePath, 0, true, "rwxr-xr-x");

        _sftpClient.Exists(remotePath).Returns(true);
        _sftpClient.ListDirectory("/home/user")
            .Returns(new[] { mockDir });

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
        var mockDir = CreateMockSftpFile("proj", dir, 0, true, "rwxr-xr-x");
        var childFile = CreateMockSftpFile("a.txt", "/home/user/proj/a.txt", 10, false, "rw-r--r--");
        var childSub = CreateMockSftpFile("sub", "/home/user/proj/sub", 0, true, "rwxr-xr-x");
        var grandchild = CreateMockSftpFile("b.txt", "/home/user/proj/sub/b.txt", 20, false, "rw-r--r--");

        _sftpClient.Exists(dir).Returns(true);
        _sftpClient.ListDirectory("/home/user").Returns(new[] { mockDir });   // parent listing → proj is a directory
        _sftpClient.ListDirectory(dir).Returns(new[] { childFile, childSub });
        _sftpClient.ListDirectory("/home/user/proj/sub").Returns(new[] { grandchild });

        var reports = new List<SftpDeleteProgress>();
        await _sftpService.DeleteAsync(_sessionId, dir,
            new SynchronousProgress<SftpDeleteProgress>(reports.Add));

        // Files removed, and each directory removed only after its contents.
        _sftpClient.Received(1).DeleteFile("/home/user/proj/a.txt");
        _sftpClient.Received(1).DeleteFile("/home/user/proj/sub/b.txt");
        _sftpClient.Received(1).DeleteDirectory("/home/user/proj/sub");
        _sftpClient.Received(1).DeleteDirectory(dir);

        Assert.AreEqual(5, reports.Count);                 // initial 0/total + 2 files + 2 directories
        Assert.AreEqual(0, reports[0].DeletedCount);
        Assert.AreEqual(4, reports[0].TotalCount);
        Assert.AreEqual(4, reports[^1].DeletedCount);
        Assert.AreEqual(4, reports[^1].TotalCount);
    }

    [TestMethod]
    public async Task DeleteAsync_WhenPathNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        var remotePath = "/home/user/nonexistent.txt";
        _sftpClient.Exists(remotePath).Returns(false);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => _sftpService.DeleteAsync(_sessionId, remotePath));
    }

    [TestMethod]
    public async Task ListDirectoryAsync_OwnerAndGroup_AreNotBooleanStrings()
    {
        // Arrange
        var mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 1024, false, "rw-r--r--");
        mockFile.UserId.Returns(1000);
        mockFile.GroupId.Returns(1000);

        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        // Act
        var result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

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
        var mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 0, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Act
        await ((IAsyncDisposable)_sftpService).DisposeAsync();

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
        await Assert.ThrowsExactlyAsync<SftpPermissionDeniedException>(
            () => _sftpService.ListDirectoryAsync(_sessionId, "/root/restricted"));
    }

    [TestMethod]
    public async Task UploadFileAsync_WithProgressCallback_FiresCorrectPercentages()
    {
        // Arrange
        var localPath = Path.GetTempFileName();
        var testData = new byte[10000];
        await File.WriteAllBytesAsync(localPath, testData);

        var remotePath = "/home/user/upload.bin";
        var progressReports = new List<TransferProgress>();

        _sftpClient.UploadAsync(
            Arg.Any<Stream>(),
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
        Assert.IsTrue(progressReports.Count() >= 4);
        Assert.IsTrue(progressReports.Any(p => p.Percentage >= 25 && p.Percentage < 35));
        Assert.IsTrue(progressReports.Any(p => p.Percentage >= 50 && p.Percentage < 60));
        Assert.IsTrue(progressReports.Any(p => p.Percentage >= 75 && p.Percentage < 85));
        Assert.AreEqual(100, progressReports.Last().Percentage);

        // Cleanup
        File.Delete(localPath);
    }

    [TestMethod]
    public async Task GetFileInfoAsync_ReturnsFileInformation()
    {
        // Arrange
        var remotePath = "/home/user/info.txt";
        var mockFile = CreateMockSftpFile("info.txt", remotePath, 4096, false, "rw-r--r--");

        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        // Act
        var result = await _sftpService.GetFileInfoAsync(_sessionId, remotePath);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("info.txt", result.Name);
        Assert.AreEqual(remotePath, result.FullPath);
        Assert.AreEqual(4096L, result.Size);
        Assert.IsFalse(result.IsDirectory);
    }

    private class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private ISftpFile CreateMockSftpFile(string name, string fullName, long length, bool isDirectory, string permissions)
    {
        var file = Substitute.For<ISftpFile>();
        file.Name.Returns(name);
        file.FullName.Returns(fullName);
        file.Length.Returns(length);
        file.IsDirectory.Returns(isDirectory);
        file.LastWriteTime.Returns(DateTime.UtcNow);

        file.OwnerCanRead.Returns(permissions.Length > 0 && permissions[0] == 'r');
        file.OwnerCanWrite.Returns(permissions.Length > 1 && permissions[1] == 'w');
        file.OwnerCanExecute.Returns(permissions.Length > 2 && permissions[2] == 'x');

        file.GroupCanRead.Returns(permissions.Length > 3 && permissions[3] == 'r');
        file.GroupCanWrite.Returns(permissions.Length > 4 && permissions[4] == 'w');
        file.GroupCanExecute.Returns(permissions.Length > 5 && permissions[5] == 'x');

        file.OthersCanRead.Returns(permissions.Length > 6 && permissions[6] == 'r');
        file.OthersCanWrite.Returns(permissions.Length > 7 && permissions[7] == 'w');
        file.OthersCanExecute.Returns(permissions.Length > 8 && permissions[8] == 'x');

        return file;
    }
}
