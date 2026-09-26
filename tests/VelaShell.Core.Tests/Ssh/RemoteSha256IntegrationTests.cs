using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 远端 SHA-256 在<b>真实 OpenSSH 服务端</b>上的行为。单测里 exec 通道是替身,验不到的恰恰是:
/// 用户的登录 shell 怎么解析这条命令、单引号转义对不对、带反斜杠与换行的名字在 GNU sha256sum 输出里怎么转义。
/// </summary>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
public class RemoteSha256IntegrationTests
{
    // 写 IPv4 字面量:localhost 先解析到 ::1,而 Docker Desktop 的端口转发只在 IPv4 上应答。
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    public async Task Sha256_OfAwkwardFileNames_MatchesTheLocalDigest()
    {
        RequireDockerAndSsh();

        string root = $"/tmp/vela-sha-{Guid.NewGuid():N}";
        (string Name, string Content)[] files =
        [
            ("plain.txt", "plain"),
            ("with space.txt", "space"),
            ("it's quoted.txt", "quote"),
            ("back\\slash.txt", "backslash"),
            ("$HOME;echo pwned", "dollar"),
            ("new\nline.txt", "newline"),
        ];
        VelaSshClientWrapper ssh = await ConnectAsync();
        try
        {
            var setup = new StringBuilder($"mkdir -p {RemoteSha256.Quote(root)}");
            foreach ((string name, string content) in files)
            {
                setup.Append($" && printf %s {RemoteSha256.Quote(content)} > {RemoteSha256.Quote(root + "/" + name)}");
            }
            await ssh.RunCommandAsync(setup.ToString());
            string[] paths = [.. files.Select(f => root + "/" + f.Name), root + "/missing.txt"];

            RemoteCommandResult result = await ssh.RunCommandDetailedAsync(RemoteSha256.BuildCommand(paths));
            IReadOnlyDictionary<string, string?> digests =
                RemoteSha256.Parse(result.StandardOutput, result.StandardError, result.ExitCode, paths);

            foreach ((string name, string content) in files)
            {
                Assert.AreEqual(
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                    digests[root + "/" + name],
                    $"文件名 {name}");
            }
            Assert.IsNull(digests[root + "/missing.txt"], "不存在的文件只让它自己回退");
            Assert.AreEqual(1, result.ExitCode, "有文件失败时 sha256sum 退出 1,但其余照常输出");
        }
        finally
        {
            try
            {
                await ssh.RunCommandAsync($"rm -rf {RemoteSha256.Quote(root)}");
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
            ct => SshConnection.ConnectAsync(new SshConnectionOptions(TestUser, TestHost, TestPort)
            {
                Credentials = [new PasswordCredential(TestPassword)],
                // 测试容器的主机键每次重建都变：无条件信任，不写 known_hosts。
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }, ct),
            TimeSpan.FromSeconds(10));

        await client.ConnectAsync(CancellationToken.None);
        return client;
    }
}
