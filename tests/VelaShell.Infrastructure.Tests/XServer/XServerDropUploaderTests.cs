using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 本机文件拖进经 SSH 转发来的 X 程序(F16):按 X 程序的连接名(<c>user@host:22</c>)找到连着的会话,
/// 经它的 SFTP 传到远端一个只有自己看得到的临时目录,交回远端路径。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XServerDropUploaderTests
{
    private string _local = "";

    [TestInitialize]
    public void Setup() => _local = Directory.CreateTempSubdirectory("vx-drop-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_local, recursive: true);

    private static SshSession Session(string user, string host, int port, SessionStatus status) => new()
    {
        ConnectionInfo = new ConnectionInfo { Host = host, Port = port, Username = user, AuthMethod = AuthMethod.Password },
        Status = status,
    };

    [TestMethod]
    public async Task Upload_FindsTheSessionByLabel_MakesAPrivateDirectory_AndReturnsRemotePathsInOrder()
    {
        SshSession other = Session("joe", "other", 22, SessionStatus.Connected);
        SshSession box = Session("joe", "box", 2222, SessionStatus.Connected);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns([other, box]);
        ISftpService sftp = Substitute.For<ISftpService>();
        SftpXServerDropUploader uploader = new(connections, sftp);

        string a = Path.Combine(_local, "a.txt"), nested = Path.Combine(_local, "dir"), again = Path.Combine(nested, "a.txt");
        File.WriteAllText(a, "a");
        Directory.CreateDirectory(Path.Combine(nested, "sub"));
        File.WriteAllText(again, "b");
        File.WriteAllText(Path.Combine(nested, "sub", "c.txt"), "c");

        Assert.IsTrue(uploader.CanUpload("joe@box:2222"));
        Assert.IsFalse(uploader.CanUpload("joe@box:22"), "端口不同就不是同一个会话");
        IReadOnlyList<string>? remote = await uploader.UploadAsync("joe@box:2222", [a, nested, again, Path.Combine(_local, "gone.txt")]);

        Assert.IsNotNull(remote);
        Assert.HasCount(3, remote, "已经不存在的跳过");
        string directory = remote[0][..remote[0].LastIndexOf('/')];
        StringAssert.StartsWith(directory, "/tmp/velashell-drop-");
        Assert.AreSequenceEqual([$"{directory}/a.txt", $"{directory}/dir", $"{directory}/a (2).txt"], remote.ToArray(), "同名的加序号");
        await sftp.Received(1).CreateDirectoryAsync(box.SessionId, directory, Arg.Any<CancellationToken>());
        await sftp.Received(1).SetPermissionsAsync(box.SessionId, directory, 700, Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadFileAsync(box.SessionId, a, $"{directory}/a.txt", null, 0, Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadFileAsync(box.SessionId, again, $"{directory}/dir/a.txt", null, 0, Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadFileAsync(box.SessionId, Path.Combine(nested, "sub", "c.txt"), $"{directory}/dir/sub/c.txt", null, 0, Arg.Any<CancellationToken>());
        await sftp.DidNotReceive().CreateDirectoryAsync(other.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Upload_WithoutAConnectedSession_ReturnsNull()
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns([Session("joe", "box", 22, SessionStatus.Disconnected)]);
        ISftpService sftp = Substitute.For<ISftpService>();
        SftpXServerDropUploader uploader = new(connections, sftp);

        Assert.IsFalse(uploader.CanUpload("joe@box:22"), "会话断开了");
        Assert.IsNull(await uploader.UploadAsync("joe@box:22", [Path.Combine(_local, "x")]));
        await sftp.DidNotReceive().CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
