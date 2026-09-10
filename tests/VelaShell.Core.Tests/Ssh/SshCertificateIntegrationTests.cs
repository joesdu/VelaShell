using System.Net.Sockets;
using Tmds.Ssh;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.DependencyInjection;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// OpenSSH 用户证书认证的端到端验证:打通 <c>AddCredential</c> → Tmds.Ssh → 真实 sshd。
/// </summary>
/// <remarks>
/// <para>
/// 靶机被刻意配成<b>只认证书</b>:<c>TrustedUserCAKeys</c> 指向我们的 CA,而
/// <c>AuthorizedKeysFile none</c> + <c>PasswordAuthentication no</c> +
/// <c>GSSAPIAuthentication no</c> 把其余每一条路都堵死,预置的 authorized_keys 也删掉了。
/// 这一点是本用例成立的前提:只要服务端还留着任何一条回退路径,"连上了"就不能证明
/// 证书生效 —— 走的可能是普通公钥认证,而你以为是证书。
/// </para>
/// <para>
/// <see cref="ConnectsWithoutCertificate_IsRejected" /> 是配套的阴性对照:同一把私钥、
/// 不带证书必须被拒。两条一起看才说明问题;只有阳性那条时,一台配置松掉的靶机会让它一直绿着。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("DockerIntegration")]
public sealed class SshCertificateIntegrationTests
{
    private const string TestHost = "127.0.0.1";
    private const int CertPort = 2223;
    private const string TestUser = "testuser";

    /// <summary>靶机资产(CA、用户私钥、证书)所在目录;由 VELASHELL_CERT_LAB 指定。</summary>
    private static string? LabDir => Environment.GetEnvironmentVariable("VELASHELL_CERT_LAB");

    private static string KeyPath => Path.Combine(LabDir!, "id_ed25519");
    private static string CertPath => Path.Combine(LabDir!, "id_ed25519-cert.pub");

    /// <summary>
    /// 靶机不在就把用例记成<b>未执行</b>,而不是让它安静地记成通过。
    /// </summary>
    private static void RequireCertServer()
    {
        if (string.IsNullOrWhiteSpace(LabDir) || !File.Exists(KeyPath) || !File.Exists(CertPath))
        {
            Assert.Inconclusive(
                "证书靶机资产缺失。把 VELASHELL_CERT_LAB 指向含 id_ed25519 / id_ed25519-cert.pub 的目录。");
        }
        try
        {
            using var probe = new TcpClient();
            IAsyncResult pending = probe.BeginConnect(TestHost, CertPort, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
            {
                Assert.Inconclusive($"证书靶机未监听 {TestHost}:{CertPort}。");
            }
            probe.EndConnect(pending);
        }
        catch (SocketException)
        {
            Assert.Inconclusive($"证书靶机未监听 {TestHost}:{CertPort}。");
        }
    }

    /// <summary>按连接信息装配 settings —— 凭据那一段走的正是生产代码。</summary>
    private static SshClientSettings BuildSettings(VelaConnectionInfo ci)
    {
        var settings = new SshClientSettings($"{ci.Username}@{ci.Host}")
        {
            Port = ci.Port,
            AutoConnect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // 本用例要验的是【用户】认证,主机认证不是被测对象:靶机的主机密钥每次
            // 重建镜像都会变,在这里较真只会把用例变成 known_hosts 的维护负担。
            HostAuthentication = (_, _) => ValueTask.FromResult(true)
        };
        InfrastructureServiceCollectionExtensions.AddCredential(settings, ci);
        return settings;
    }

    private static VelaConnectionInfo CertificateConnection() =>
        new()
        {
            Host = TestHost,
            Port = CertPort,
            Username = TestUser,
            AuthMethod = AuthMethod.Certificate,
            PrivateKeyPath = KeyPath,
            CertificatePath = CertPath
        };

    [TestMethod]
    public async Task ConnectsWithCertificate_AndRunsACommand()
    {
        RequireCertServer();

        using var client = new SshClient(BuildSettings(CertificateConnection()));
        await client.ConnectAsync(TestCancellation);

        // 连上还不够:跑一条命令才能证明这是一条可用的会话,而不只是握手成功。
        // 顺带确认服务端认下来的身份就是证书 principals 里那个 testuser。
        using RemoteProcess process = await client.ExecuteAsync("id -un", cancellationToken: TestCancellation);
        (string stdout, string stderr) = await process.ReadToEndAsStringAsync(TestCancellation);

        // stderr 带进失败消息:命令没输出时,原因通常就写在那里。
        Assert.AreEqual(TestUser, stdout.Trim(), $"stderr: {stderr}");
    }

    [TestMethod]
    public async Task ConnectsWithoutCertificate_IsRejected()
    {
        RequireCertServer();

        // 同一把私钥,只是不走证书那一路。靶机没有 authorized_keys,必须拒绝 ——
        // 这条一旦变绿,说明靶机配置松了,阳性那条的结论也就不作数了。
        VelaConnectionInfo plainKey = new()
        {
            Host = TestHost,
            Port = CertPort,
            Username = TestUser,
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = KeyPath
        };

        using var client = new SshClient(BuildSettings(plainKey));
        // 用基类而不是某个具体子类:被测的是"必须被拒",不是 Tmds 内部选了哪一种异常 ——
        // 钉死子类只会让上游换个类型就红一片,而那与本用例想守的东西无关。
        await Assert.ThrowsAsync<SshConnectionException>(
            async () => await client.ConnectAsync(TestCancellation));
    }

    private static CancellationToken TestCancellation => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
