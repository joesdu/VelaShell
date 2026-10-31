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

    /// <summary>命令退 <paramref name="exitCode" />;退 0 时那个目录随之不在了(之后再 stat 拿到 null),除非 <paramref name="removes" /> 为 false。</summary>
    private void StubExit(int exitCode, bool removes = true) =>
        _ssh.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (exitCode == 0 && removes)
                {
                    _sftpClient.GetEntryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((SftpEntry?)null);
                }
                return new RemoteCommandResult(string.Empty, string.Empty, exitCode);
            });

    /// <summary>会话的字符集(终端与 SFTP 文件名同一个口径)。</summary>
    private void SessionCharset(string charset)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        _connectionService.GetSession(_sessionId).Returns(new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new() { Host = "h", Port = 22, Username = "u", AuthMethod = AuthMethod.Password, FileNameEncoding = charset },
            Status = SessionStatus.Connected
        });
    }

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

    /// <summary>
    /// GBK / Big5 / Shift_JIS 会话:SFTP 按会话字符集解名字,<c>/data/报表</c> 在服务器上是 GBK 字节,而 exec 命令行是 UTF-8 ——
    /// <c>rm -rf</c> 拿到的是另一串字节(不存在照样退 0,或者正好删掉一个 UTF-8 字节的同名目录)。中文路径不走命令,走 SFTP 递归
    /// (复查 file-panes F2)。
    /// </summary>
    [TestMethod]
    [DataRow("GBK", "/data/报表")]
    [DataRow("Big5", "/data/功能")]
    [DataRow("shift_jis", "/data/表")]
    public async Task LegacyCharsetSession_NonAsciiPath_IsNotPassedToTheCommand(string charset, string dir)
    {
        SessionCharset(charset);
        StubTree(dir);
        StubExit(0);

        await CreateService().DeleteAsync(_sessionId, dir);

        await _ssh.DidNotReceive().RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteFileAsync($"{dir}/a.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(dir, Arg.Any<CancellationToken>());
    }

    /// <summary>纯 ASCII 的路径在这几种字符集里与 UTF-8 是同一串字节:照走快路径。</summary>
    [TestMethod]
    public async Task LegacyCharsetSession_AsciiPath_StillUsesTheCommand()
    {
        SessionCharset("GBK");
        StubTree();
        StubExit(0);

        await CreateService().DeleteAsync(_sessionId, Dir);

        await _ssh.Received(1).RunCommandDetailedAsync($"rm -rf -- '{Dir}'", Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>UTF-8 会话里 SFTP 把解不开的字节记成 U+DC80–DCFF,编进 UTF-8 命令行成了 EF BF BD:同样不走命令。</summary>
    [TestMethod]
    public async Task Utf8Session_PathWithRawByteEscapes_IsNotPassedToTheCommand()
    {
        string dir = "/data/" + (char)0xDCD6 + (char)0xDCD0;
        StubTree(dir);
        StubExit(0);

        await CreateService().DeleteAsync(_sessionId, dir);

        await _ssh.DidNotReceive().RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(dir, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 命令退 0 而目录还在(<c>rm -f</c> 删一个不存在的路径同样退 0):不报成功,接着走 SFTP 递归把它删掉。
    /// </summary>
    [TestMethod]
    public async Task ZeroExit_ButTheDirectoryIsStillThere_FallsBackToTheSftpWalk()
    {
        StubTree();
        StubExit(0, removes: false);

        await CreateService().DeleteAsync(_sessionId, Dir);

        await _ssh.Received(1).RunCommandDetailedAsync($"rm -rf -- '{Dir}'", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(Dir, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 算摘要同理:GBK 会话里的中文路径不进 <c>sha256sum</c> 的命令行(进去了要么找不到、要么算的是 UTF-8 字节的同名文件),
    /// 记「没算出来」;同一批里的 ASCII 路径照算。
    /// </summary>
    [TestMethod]
    public async Task Sha256_LegacyCharsetSession_SkipsPathsWhoseBytesDiffer()
    {
        SessionCharset("GBK");
        const string Ascii = "/data/a.txt";
        const string Chinese = "/data/报表.txt";
        string? sent = null;
        _ssh.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<string>(0);
                return new RemoteCommandResult(new string('a', 64) + "  " + Ascii + "\n", string.Empty, 0);
            });

        IReadOnlyDictionary<string, string?> hashes = await CreateService().ComputeSha256Async(_sessionId, [Ascii, Chinese]);

        Assert.AreEqual(new string('a', 64), hashes[Ascii]);
        Assert.IsNull(hashes[Chinese]);
        Assert.IsNotNull(sent);
        Assert.DoesNotContain("报表", sent);

        sent = null;
        IReadOnlyDictionary<string, string?> none = await CreateService().ComputeSha256Async(_sessionId, [Chinese]);
        Assert.IsNull(none[Chinese]);
        Assert.IsNull(sent, "一条都传不过去时连命令都不发");
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
