using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 从经 SSH 转发来的 X 程序往本机拖出文件(F16 的另一半):按 X 程序的连接名找到连着的会话,经它的 SFTP 把文件与目录取回本机的一个新临时目录,
/// 交回本机路径;远端的名字在本机不合法时换掉;一天以前留下的临时目录在下一次取之前清掉。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XServerDragDownloaderTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup() => _root = Directory.CreateTempSubdirectory("vx-drag-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private static SshSession Session(string user, string host, int port, SessionStatus status) => new()
    {
        ConnectionInfo = new ConnectionInfo { Host = host, Port = port, Username = user, AuthMethod = AuthMethod.Password },
        Status = status,
    };

    private static RemoteFileInfo Info(string name, string path, bool directory, bool link = false) => new()
    {
        Name = name,
        FullPath = path,
        Size = 1,
        Permissions = directory ? "drwxr-xr-x" : "-rw-r--r--",
        IsDirectory = directory,
        IsSymbolicLink = link,
        LastModified = DateTime.UtcNow,
        Owner = "joe",
        Group = "joe",
    };

    [TestMethod]
    public async Task Download_FindsTheSessionByLabel_FetchesFilesAndDirectories_IntoANewTempDirectory()
    {
        SshSession box = Session("joe", "box", 2222, SessionStatus.Connected);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns([Session("joe", "other", 22, SessionStatus.Connected), box]);
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.ExistsAsync(box.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<string>(1) != "/home/joe/gone");
        sftp.GetFileInfoAsync(box.SessionId, "/home/joe/a.txt", Arg.Any<CancellationToken>()).Returns(Info("a.txt", "/home/joe/a.txt", false));
        sftp.GetFileInfoAsync(box.SessionId, "/srv/a.txt", Arg.Any<CancellationToken>()).Returns(Info("a.txt", "/srv/a.txt", false));
        sftp.GetFileInfoAsync(box.SessionId, "/home/joe/dir", Arg.Any<CancellationToken>()).Returns(Info("dir", "/home/joe/dir", true));
        sftp.ListDirectoryAsync(box.SessionId, "/home/joe/dir", Arg.Any<CancellationToken>()).Returns(
        [
            Info(".", "/home/joe/dir/.", true),
            Info("b.txt", "/home/joe/dir/b.txt", false),
            Info("sub", "/home/joe/dir/sub", true),
            Info("loop", "/home/joe/dir/loop", true, link: true),
        ]);
        sftp.ListDirectoryAsync(box.SessionId, "/home/joe/dir/sub", Arg.Any<CancellationToken>()).Returns([Info("c.txt", "/home/joe/dir/sub/c.txt", false)]);
        SftpXServerDragDownloader downloader = new(connections, sftp, _root);

        Assert.IsTrue(downloader.CanDownload("joe@box:2222"));
        Assert.IsFalse(downloader.CanDownload("joe@box:22"), "端口不同就不是同一个会话");
        IReadOnlyList<string>? local = await downloader.DownloadAsync("joe@box:2222", ["/home/joe/a.txt", "/home/joe/gone", "/home/joe/dir", "/srv/a.txt"]);

        Assert.IsNotNull(local);
        Assert.HasCount(3, local, "远端已经不存在的跳过");
        string directory = Path.GetDirectoryName(local[0])!;
        Assert.AreEqual(_root, Path.GetDirectoryName(directory), "每次一个新的子目录");
        Assert.AreSequenceEqual([Path.Combine(directory, "a.txt"), Path.Combine(directory, "dir"), Path.Combine(directory, "a (2).txt")], local.ToArray(), "同名的加序号");
        Assert.IsTrue(Directory.Exists(Path.Combine(directory, "dir", "sub")), "目录照样建出来");
        await sftp.Received(1).DownloadFileAsync(box.SessionId, "/home/joe/a.txt", Path.Combine(directory, "a.txt"), null, 0, Arg.Any<CancellationToken>());
        await sftp.Received(1).DownloadFileAsync(box.SessionId, "/srv/a.txt", Path.Combine(directory, "a (2).txt"), null, 0, Arg.Any<CancellationToken>());
        await sftp.Received(1).DownloadFileAsync(box.SessionId, "/home/joe/dir/b.txt", Path.Combine(directory, "dir", "b.txt"), null, 0, Arg.Any<CancellationToken>());
        await sftp.Received(1).DownloadFileAsync(box.SessionId, "/home/joe/dir/sub/c.txt", Path.Combine(directory, "dir", "sub", "c.txt"), null, 0, Arg.Any<CancellationToken>());
        await sftp.DidNotReceive().ListDirectoryAsync(box.SessionId, "/home/joe/dir/loop", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Download_WithoutAConnectedSession_ReturnsNull_AndOldTempDirectoriesArePruned()
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns([Session("joe", "box", 22, SessionStatus.Disconnected)]);
        ISftpService sftp = Substitute.For<ISftpService>();
        SftpXServerDragDownloader offline = new(connections, sftp, _root);
        Assert.IsFalse(offline.CanDownload("joe@box:22"), "会话断开了");
        Assert.IsNull(await offline.DownloadAsync("joe@box:22", ["/x"]));

        string old = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - SftpXServerDragDownloader.Retention - TimeSpan.FromHours(1));
        string fresh = Directory.CreateDirectory(Path.Combine(_root, "fresh")).FullName;
        SshSession box = Session("joe", "box", 22, SessionStatus.Connected);
        connections.Sessions.Returns([box]);
        await new SftpXServerDragDownloader(connections, sftp, _root).DownloadAsync("joe@box:22", []);
        Assert.IsFalse(Directory.Exists(old), "一天以前的清掉");
        Assert.IsTrue(Directory.Exists(fresh), "新的留着(放下的程序可能还在拷)");
    }

    [TestMethod]
    public void SafeName_ReplacesCharactersTheLocalFileSystemRejects()
    {
        Assert.AreEqual("report.txt", SftpXServerDragDownloader.SafeName("report.txt"));
        Assert.AreEqual("dragged", SftpXServerDragDownloader.SafeName("..."), "只剩点与空格的换成默认名");
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("a_b_c_.txt", SftpXServerDragDownloader.SafeName("a:b*c?.txt"));
            Assert.AreEqual("_CON.txt", SftpXServerDragDownloader.SafeName("CON.txt"), "保留名前面加下划线");
            Assert.AreEqual("trailing", SftpXServerDragDownloader.SafeName("trailing. "), "结尾的点与空格去掉");
        }
        else
        {
            Assert.AreEqual("a:b*c?.txt", SftpXServerDragDownloader.SafeName("a:b*c?.txt"), "类 Unix 上这些字符合法");
        }
    }
}
