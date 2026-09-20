using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 目录删除的快路径(#474):有 exec 通道时一条 <c>rm -rf</c> 顶替整棵树的 SFTP 递归,
/// 其余情况一律照旧走 SFTP —— 这两半都得钉住,漏哪一半都会在真机上变成"删不掉"。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public class SftpRecursiveDeleteCommandTests
{
    private const string Dir = "/home/user/proj";

    private readonly ISshConnectionService _connectionService = Substitute.For<ISshConnectionService>();
    private readonly ISftpClientWrapper _sftpClient = Substitute.For<ISftpClientWrapper>();
    private readonly ISshClientWrapper _ssh = Substitute.For<ISshClientWrapper>();
    private readonly Guid _sessionId = Guid.NewGuid();

    public SftpRecursiveDeleteCommandTests()
    {
        _sftpClient.IsConnected.Returns(true);
        _connectionService.GetSession(_sessionId).Returns(new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new() { Host = "h", Port = 22, Username = "u", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected
        });
    }

    /// <summary>把一棵 proj/{a.txt, sub/b.txt} 的树摆到 SFTP 替身上。</summary>
    private void StubTree(string dir = Dir)
    {
        _sftpClient.GetEntryAsync(dir, Arg.Any<CancellationToken>()).Returns(Entry(dir, isDirectory: true));
        _sftpClient.ListDirectoryAsync(dir, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [Entry($"{dir}/a.txt", false), Entry($"{dir}/sub", true)]));
        _sftpClient.ListDirectoryAsync($"{dir}/sub", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([Entry($"{dir}/sub/b.txt", false)]));
    }

    private static SftpEntry Entry(string fullName, bool isDirectory) =>
        new()
        {
            Name = fullName[(fullName.LastIndexOf('/') + 1)..],
            FullName = fullName,
            IsDirectory = isDirectory,
            LastWriteTime = DateTime.UtcNow
        };

    private SftpService CreateService(bool useCommand = true, bool withExecChannel = true)
    {
        // null 要显式写:NSubstitute 的递归替身会给接口返回值凭空造一个替身,
        // 不写就测不到"没有 exec 通道"这件事。
        _connectionService.GetClient(_sessionId).Returns(withExecChannel ? _ssh : null);
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync()
                .Returns(new AppSettings { Transfer = { UseRecursiveDeleteCommand = useCommand } });
        return new(_connectionService, _ => _sftpClient, settings);
    }

    private void StubExit(int exitCode) =>
        _ssh.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteCommandResult(string.Empty, string.Empty, exitCode));

    [TestMethod]
    public async Task DirectoryOnSshSession_IsDeletedByOneCommand_NotByWalkingTheTree()
    {
        StubTree();
        StubExit(0);

        await CreateService().DeleteAsync(_sessionId, Dir);

        await _ssh.Received(1).RunCommandDetailedAsync($"rm -rf -- '{Dir}'", Arg.Any<CancellationToken>());

        // 关键的一半:快路径成立时,那棵树连列都不该列一遍 —— 列举本身就是它要省掉的开销。
        await _sftpClient.DidNotReceive().ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 快路径没有逐条进度,只报一个不确定态的起点(TotalCount = 0),界面据此显示转圈。
    /// </summary>
    [TestMethod]
    public async Task FastPath_ReportsIndeterminateProgress()
    {
        StubTree();
        StubExit(0);
        var reports = new List<SftpDeleteProgress>();

        await CreateService().DeleteAsync(_sessionId, Dir, new SynchronousProgress<SftpDeleteProgress>(reports.Add));

        Assert.HasCount(1, reports);
        Assert.AreEqual(0, reports[0].TotalCount);
    }

    /// <summary>
    /// 命令退非零码(权限不足、只删了一半)即判失败:剩下的交给 SFTP 递归,
    /// 删不动时抛出的才是那条路径上的真实错误。
    /// </summary>
    [TestMethod]
    public async Task NonZeroExit_FallsBackToTheSftpWalk()
    {
        StubTree();
        StubExit(1);

        await CreateService().DeleteAsync(_sessionId, Dir);

        await _sftpClient.Received(1).DeleteFileAsync($"{Dir}/a.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(Dir, Arg.Any<CancellationToken>());
    }

    /// <summary>通道抛异常(非 Unix 主机、通道中途断开)同样回退,而不是把删除整个弄失败。</summary>
    [TestMethod]
    public async Task CommandThrows_FallsBackToTheSftpWalk()
    {
        StubTree();
        _ssh.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<RemoteCommandResult>(_ => throw new VelaSshClientException("no exec channel"));

        await CreateService().DeleteAsync(_sessionId, Dir);

        await _sftpClient.Received(1).DeleteDirectoryAsync(Dir, Arg.Any<CancellationToken>());
    }

    /// <summary>独立 SFTP 配置 / FTP / 插件协议:没有 SSH 客户端,连试都不该试。</summary>
    [TestMethod]
    public async Task WithoutExecChannel_UsesTheSftpWalk()
    {
        StubTree();

        await CreateService(withExecChannel: false).DeleteAsync(_sessionId, Dir);

        await _ssh.DidNotReceive().RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(Dir, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SettingOff_UsesTheSftpWalk()
    {
        StubTree();
        StubExit(0);

        await CreateService(useCommand: false).DeleteAsync(_sessionId, Dir);

        await _ssh.DidNotReceive().RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(Dir, Arg.Any<CancellationToken>());
    }

    /// <summary>单个文件已经是一次往返,走命令没有收益,也就不该多开一条通道。</summary>
    [TestMethod]
    public async Task SingleFile_NeverUsesTheCommand()
    {
        const string file = "/home/user/a.txt";
        _sftpClient.GetEntryAsync(file, Arg.Any<CancellationToken>()).Returns(Entry(file, isDirectory: false));
        StubExit(0);

        await CreateService().DeleteAsync(_sessionId, file);

        await _ssh.DidNotReceive().RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteFileAsync(file, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 路径里的单引号、空格与 <c>$</c> 必须在 shell 那一层失去特殊含义 ——
    /// 拼错一个引号,<c>rm -rf</c> 删的就不是用户点的那个目录了。
    /// </summary>
    [TestMethod]
    public async Task PathWithQuotesAndSpaces_IsQuotedForTheShell()
    {
        const string nasty = "/home/user/it's a $HOME; rm";
        StubTree(nasty);
        StubExit(0);

        await CreateService().DeleteAsync(_sessionId, nasty);

        await _ssh.Received(1).RunCommandDetailedAsync(
            @"rm -rf -- '/home/user/it'\''s a $HOME; rm'", Arg.Any<CancellationToken>());
    }

    /// <summary>根目录不交给 <c>rm -rf</c>:那一刀下去没有回头路。</summary>
    [TestMethod]
    public async Task RootPath_NeverUsesTheCommand()
    {
        StubTree("/");
        StubExit(0);

        await CreateService().DeleteAsync(_sessionId, "/");

        await _ssh.DidNotReceive().RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>进度回调要在调用线程上同步跑完,断言才拿得到完整的序列。</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
