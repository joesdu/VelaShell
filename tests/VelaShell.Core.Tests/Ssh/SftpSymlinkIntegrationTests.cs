using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 符号链接在<b>真实 OpenSSH 服务端</b>上的行为。单测里 <see cref="ISftpClientWrapper" /> 是替身,
/// 测不到的恰恰是服务端那几处特有的东西:
/// <list type="bullet">
///   <item>OpenSSH 把 SSH_FXP_SYMLINK 的两个参数实现反了(bugzilla #861)—— 顺序错了建出来的链接指向链接自己;</item>
///   <item>readdir 带回的是 lstat 属性还是 stat 属性,决定了列表里还认不认得出链接;</item>
///   <item>SSH_FXP_REMOVE 对链接删的是链接本身还是目标。</item>
/// </list>
/// 放在 Core.Tests 是因为要用 <c>VelaSshClientWrapper.InnerConnection</c>（internal，只对本工程开放）。
/// </summary>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
public class SftpSymlinkIntegrationTests
{
    // 写 IPv4 字面量而不是 localhost:localhost 先解析到 ::1,而 Docker Desktop 的端口转发只在 IPv4 上应答,
    // 于是探测与登录都连到 ::1 上失败,用例被记成 Inconclusive —— 容器明明是 healthy 的。
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    public async Task Symlinks_ListCreateResolveAndDelete_AgainstRealOpenSsh()
    {
        RequireDockerAndSsh();

        string root = $"/tmp/vela-links-{Guid.NewGuid():N}";
        VelaSshClientWrapper ssh = await ConnectAsync();
        try
        {
            await ssh.RunCommandAsync(
                $"mkdir -p {root}/real && echo hi > {root}/real/a.txt && ln -s real {root}/dirlink && ln -s missing {root}/broken");
            SshConnection inner = ssh.InnerConnection ?? throw new InvalidOperationException("SSH not connected.");
            await using var sftp = new VelaSftpClientWrapper(
                async ct => await SftpFileSystem.ConnectAsync(inner, cancellationToken: ct));
            await sftp.ConnectAsync(CancellationToken.None);

            // 列表:认得出链接,且指向目录的链接仍判为目录(能进去);断链不是目录。
            List<SftpEntry> listed = [.. await sftp.ListDirectoryAsync(root, CancellationToken.None)];
            SftpEntry dirLink = listed.Single(e => e.Name == "dirlink");
            SftpEntry broken = listed.Single(e => e.Name == "broken");
            Assert.IsTrue(dirLink.IsSymbolicLink);
            Assert.IsTrue(dirLink.IsDirectory);
            Assert.AreEqual("real", dirLink.LinkTarget);
            Assert.IsTrue(broken.IsSymbolicLink);
            Assert.IsFalse(broken.IsDirectory);
            Assert.IsFalse(listed.Single(e => e.Name == "real").IsSymbolicLink);

            // 单条 stat:断链也要返回条目(否则删不掉)。
            SftpEntry? brokenEntry = await sftp.GetEntryAsync($"{root}/broken");
            Assert.IsNotNull(brokenEntry);
            Assert.IsTrue(brokenEntry.IsSymbolicLink);

            // 创建:参数顺序对了,readlink 读回来的才是目标而不是链接自己。
            await sftp.CreateSymbolicLinkAsync($"{root}/created", "real/a.txt");
            string readBack = await ssh.RunCommandAsync($"readlink {root}/created");
            Assert.AreEqual("real/a.txt", readBack.Trim());

            // 删除链接:目标目录与其中的文件原样留着。
            await sftp.DeleteFileAsync($"{root}/dirlink");
            string after = await ssh.RunCommandAsync($"test -e {root}/dirlink || echo gone; cat {root}/real/a.txt");
            Assert.Contains("gone", after);
            Assert.Contains("hi", after);
        }
        finally
        {
            try
            {
                await ssh.RunCommandAsync($"rm -rf {root}");
            }
            catch
            {
                // 清理尽力而为;容器本来就是一次性的。
            }
            await ssh.DisposeAsync();
        }
    }

    /// <summary>跳过记 Inconclusive 而不是"通过":全绿的报告里不能混着一行断言都没跑的用例。</summary>
    private static void RequireDockerAndSsh()
    {
        if (!IsDockerAvailable())
        {
            Assert.Inconclusive("Docker 不可用。运行 'docker compose -f docker-compose.test.yml up -d' 以启用。");
        }
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
    private static async Task<VelaSshClientWrapper> ConnectAsync()
    {
        VelaSshClientWrapper client = new(
            ct => new SshConnectionOptions(TestUser, TestHost, TestPort)
            {
                Credentials = [new PasswordCredential(TestPassword)],
                // 测试容器的主机键每次重建都变：无条件信任，不写 known_hosts。
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }.ConnectAsync(ct),
            TimeSpan.FromSeconds(10));

        await client.ConnectAsync(CancellationToken.None);
        return client;
    }
}
