using VelaShell.Core.Models;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 连接凭据装配(<see cref="SshConnectionAssembler.BuildCredentialsAsync" />):
/// **只给用户显式选择的那一条,不做任何隐式回退**。
/// </summary>
/// <remarks>
/// <para>
/// 这条约束的来历是一个具体缺陷:上一版底层库的默认凭据列表**非空**,含 SSH Agent
/// 与 <c>~/.ssh</c> 下的默认私钥。只 Add 不替换的话,每次连接都会先试一遍 agent ——
/// Windows 上 <c>SSH_AUTH_SOCK</c> 常指向 msys/WSL 的 Unix 套接字路径,
/// 于是每次连接稳定刷一条首发异常;更糟的是它可能拿一把用户根本没打算用的钥去认证。
/// </para>
/// <para>
/// 换到 VelaShell.Ssh 之后**库本身也不回退**(不自动读 <c>~/.ssh/id_*</c>、不自动连 ssh-agent),
/// 所以这条用例从「盯住我们有没有替换掉默认值」变成「盯住我们没有自己加回来」——
/// 它防的是同一件事。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class SshCredentialSetupTests
{
    private static ConnectionInfo Password() => new()
    {
        Host = "host",
        Username = "user",
        AuthMethod = AuthMethod.Password,
        Password = "pw",
    };

    [TestMethod]
    public async Task Password_YieldsOnlyOnePasswordCredential()
    {
        IReadOnlyList<SshCredential> credentials =
            await SshConnectionAssembler.BuildCredentialsAsync(Password(), TestContext.CancellationToken);

        Assert.HasCount(1, credentials);
        Assert.IsInstanceOfType<PasswordCredential>(credentials[0]);
    }

    /// <summary>
    /// 密码那一路要**同时应答 <c>keyboard-interactive</c>**。
    /// </summary>
    /// <remarks>
    /// 很多服务端(关了 <c>PasswordAuthentication</c> 却开着 PAM 的)只接受后者,
    /// 而用户填的就是同一个密码。不应答的表现是「密码明明是对的却登不上」。
    /// </remarks>
    [TestMethod]
    public async Task Password_AlsoAnswersKeyboardInteractive()
    {
        IReadOnlyList<SshCredential> credentials =
            await SshConnectionAssembler.BuildCredentialsAsync(Password(), TestContext.CancellationToken);

        PasswordCredential credential = (PasswordCredential)credentials[0];
        Assert.IsTrue(credential.AlsoAnswerKeyboardInteractive);
    }

    /// <summary>
    /// 私钥路径读不出来时要抛,而不是悄悄退化成「没有可用凭据」。
    /// </summary>
    /// <remarks>
    /// 悄悄退化的表现是服务端一句 <c>Permission denied (publickey)</c> ——
    /// 用户会去反复检查服务端的 <c>authorized_keys</c>,而问题在本地。
    /// </remarks>
    [TestMethod]
    public async Task PrivateKey_MissingFile_Throws()
    {
        ConnectionInfo info = new()
        {
            Host = "host",
            Username = "user",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = Path.Combine(Path.GetTempPath(), "velashell-does-not-exist-" + Guid.NewGuid()),
        };

        await Assert.ThrowsAsync<Exception>(async () =>
            await SshConnectionAssembler.BuildCredentialsAsync(info, TestContext.CancellationToken));
    }

    /// <summary>不认识的认证方式要当场抛,不能落到一条空凭据列表上。</summary>
    [TestMethod]
    public async Task UnknownMethod_Throws()
    {
        ConnectionInfo info = new()
        {
            Host = "host",
            Username = "user",
            AuthMethod = (AuthMethod)999,
        };

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await SshConnectionAssembler.BuildCredentialsAsync(info, TestContext.CancellationToken));
    }

    /// <summary>MSTest 注入的测试上下文。</summary>
    public TestContext TestContext { get; set; } = null!;
}
