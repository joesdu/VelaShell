using Tmds.Ssh;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.DependencyInjection;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 连接凭据装配(<see cref="InfrastructureServiceCollectionExtensions.AddCredential" />):必须用用户
/// 选择的凭据【替换】Tmds.Ssh 的默认凭据列表,而非追加。默认列表含 SshAgentCredentials,Windows 上
/// 每次连接都会因 SSH_AUTH_SOCK 非命名管道抛 ArgumentException 刷屏(且 VelaShell 无 agent 选项)。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public class SshCredentialSetupTests
{
    [TestMethod]
    public void FreshSettings_DefaultCredentials_IncludeSshAgent()
    {
        // 缺陷前提:Tmds 默认凭据非空且含 SSH Agent —— 这正是必须"替换而非追加"的原因。
        // 若某天 Tmds 改了默认(此断言失败),说明噪声根源已消失,可回来简化本处逻辑。
        var s = new SshClientSettings("user@host");
        Assert.IsGreaterThan(0, s.Credentials.Count);
        Assert.Contains(c => c is SshAgentCredentials, s.Credentials,
            "前提假设:Tmds.Ssh 默认凭据含 SshAgentCredentials");
    }

    [TestMethod]
    public void AddCredential_Password_ReplacesDefaultsWithOnlyPassword()
    {
        var s = new SshClientSettings("user@host");
        InfrastructureServiceCollectionExtensions.AddCredential(s, new ConnectionInfo
        {
            Host = "host",
            Username = "user",
            AuthMethod = AuthMethod.Password,
            Password = "pw",
        });

        Assert.HasCount(1, s.Credentials);
        Assert.IsInstanceOfType<PasswordCredential>(s.Credentials[0]);
        Assert.DoesNotContain(c => c is SshAgentCredentials, s.Credentials,
            "不得保留 Tmds 默认的 SSH Agent 凭据——它在 Windows 上会因 SSH_AUTH_SOCK 非命名管道每次连接抛 ArgumentException");
    }

    [TestMethod]
    public void AddCredential_PrivateKey_ReplacesDefaultsWithOnlyPrivateKey()
    {
        var s = new SshClientSettings("user@host");
        InfrastructureServiceCollectionExtensions.AddCredential(s, new ConnectionInfo
        {
            Host = "host",
            Username = "user",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/home/user/.ssh/id_ed25519",
        });

        Assert.HasCount(1, s.Credentials);
        Assert.IsInstanceOfType<PrivateKeyCredential>(s.Credentials[0]);
        Assert.DoesNotContain(c => c is SshAgentCredentials, s.Credentials);
    }
}
