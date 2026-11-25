using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// keyboard-interactive(2FA / OTP)在<b>真实 OpenSSH + PAM</b> 上的端到端行为。
/// </summary>
/// <remarks>
/// <para>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-2fa</c>(端口 2224,见 <c>tests/fixtures/ssh-2fa/Dockerfile</c>):
/// sshd 只开 keyboard-interactive,PAM 先问 <c>Password:</c> 再问 <c>Verification code:</c>(Google Authenticator 的 TOTP)。
/// 连接走宿主真正的装配路径(<see cref="SshConnectionAssembler" /> + <see cref="VelaSshClientWrapper" />),
/// 界面换成按脚本应答的替身 —— 要验的是「口令自动答、只有动态码才问人」在真实 PAM 的提问顺序下成立。
/// </para>
/// <para>
/// 自己写的服务端替身与实现一样错时测不出来:PAM 每一轮只问一条、两轮都是不回显,
/// 恰恰是库自带「密码兼答」会把密码填进验证码那一轮的形状。
/// </para>
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 SSH 操作经宿主装配路径,取消令牌由测试上下文给出。")]
[TestClass]
[TestCategory("DockerIntegration")]
[DoNotParallelize]
public class KeyboardInteractiveIntegrationTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2224;
    private const string Password = "velapass";

    /// <summary>靶机里 TOTP 的种子:base32 <c>JBSWY3DPEHPK3PXP</c> = "Hello!" + DE AD BE EF。</summary>
    private static readonly byte[] TotpSecret = [0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0xDE, 0xAD, 0xBE, 0xEF];

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 口令自动代答、只有动态码问人 —— 而且<b>一次尝试就过</b>。
    /// </summary>
    /// <remarks>
    /// 账号 vela-strict 是 <c>MaxAuthTries 1</c>:库自带的「密码兼答」若还开着,会先把口令填进验证码那一轮、
    /// 白白失败一次(真实服务器上那是一次 OTP 失败计数),sshd 当场断开,这条用例就红。
    /// 宽松的服务器上它照样能「最后连上」,看不出来。
    /// </remarks>
    [TestMethod]
    [Timeout(60_000)]
    public async Task PasswordThenCode_OnlyTheCodeIsAskedFor_AndTheFirstAttemptSucceeds()
    {
        RequireContainer();
        var prompt = new ScriptedPrompt(_ => [Totp()]);

        await using VelaSshClientWrapper ssh = await ConnectAsync(PasswordInfo("vela-strict"), prompt);

        Assert.AreEqual("vela-strict", (await ssh.RunCommandAsync("whoami")).Trim());
        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        Assert.AreEqual($"vela-strict@{TestHost}:{TestPort}", asked.Target);
        KeyboardInteractiveField field = asked.Fields.Single();
        Assert.Contains("Verification code", field.Prompt, "口令那一轮应该由已有的密码代答,只有动态码才问人");
        Assert.IsFalse(field.Echo);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task WrongCode_IsAnAuthenticationFailure()
    {
        RequireContainer();
        var prompt = new ScriptedPrompt(_ => ["not-a-code"]);

        await Assert.ThrowsExactlyAsync<VelaSshAuthenticationException>(
            () => ConnectAsync(PasswordInfo("vela-otp"), prompt));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CancellingTheCodePrompt_IsReportedAsCancelled_NotAsAFailure()
    {
        RequireContainer();
        var prompt = new ScriptedPrompt(_ => null);

        // 不是认证失败(那会让宿主再弹一遍凭据框),也不是超时(调用方没取消的取消会被改判成超时)。
        await Assert.ThrowsExactlyAsync<VelaSshAuthenticationCancelledException>(
            () => ConnectAsync(PasswordInfo("vela-otp"), prompt));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task KeyThenCode_TheSecondFactorIsAskedAfterTheKey()
    {
        RequireContainer();
        var prompt = new ScriptedPrompt(_ => [Totp()]);
        ConnectionInfo info = new()
        {
            Host = TestHost,
            Port = TestPort,
            Username = "vela-keyotp",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = FixtureKeyPath(),
        };

        await using VelaSshClientWrapper ssh = await ConnectAsync(info, prompt);

        Assert.AreEqual("vela-keyotp", (await ssh.RunCommandAsync("whoami")).Trim());
        Assert.Contains("Verification code", prompt.Requests.Single().Fields.Single().Prompt);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task WithoutAPrompt_PasswordAloneCannotPassTheSecondFactor()
    {
        RequireContainer();
        ConnectionInfo info = PasswordInfo("vela-otp");
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: FixtureHostKeys.Create(), settings: null, prompt: null, alerts: null, proxyResolver: null);
        await using VelaSshClientWrapper ssh = new(assembled.Connect, assembled.ConnectTimeout);

        // 没有界面可问(headless)时退回库的「密码兼答」:它把密码也填进验证码那一轮,服务端拒掉 ——
        // 这正是需要动态码框的原因。
        await Assert.ThrowsExactlyAsync<VelaSshAuthenticationException>(
            () => ssh.ConnectAsync(TestContext.CancellationToken));
    }

    private static ConnectionInfo PasswordInfo(string user) => new()
    {
        Host = TestHost,
        Port = TestPort,
        Username = user,
        AuthMethod = AuthMethod.Password,
        Password = Password,
    };

    private async Task<VelaSshClientWrapper> ConnectAsync(ConnectionInfo info, IKeyboardInteractivePrompt prompt)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: FixtureHostKeys.Create(), settings: null, prompt: null, alerts: null, proxyResolver: null, keyboardPrompt: prompt);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout);
        try
        {
            await wrapper.ConnectAsync(TestContext.CancellationToken);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }

    /// <summary>RFC 6238 的 TOTP(HMAC-SHA1、30 秒、6 位),与 Google Authenticator 的默认一致。</summary>
    private static string Totp()
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        byte[] hash = HMACSHA1.HashData(TotpSecret, counter);
        int offset = hash[^1] & 0x0F;
        int code = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000;
        return code.ToString("D6");
    }

    private static string FixtureKeyPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "ssh-2fa", "id_ed25519");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new AssertFailedException("找不到 tests/fixtures/ssh-2fa/id_ed25519。");
    }

    private static void RequireContainer()
    {
        try
        {
            using TcpClient tcp = new();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"[SKIP] ssh-2fa 靶机 {TestHost}:{TestPort} 不可达。" +
                                    "运行 'docker compose -f docker-compose.test.yml up -d --build ssh-2fa'。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"[SKIP] ssh-2fa 靶机 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }

    /// <summary>按脚本应答的界面替身,记下每一轮被问了什么。</summary>
    private sealed class ScriptedPrompt(Func<KeyboardInteractiveRequest, IReadOnlyList<string>?> answer) : IKeyboardInteractivePrompt
    {
        public List<KeyboardInteractiveRequest> Requests { get; } = [];

        public Task<IReadOnlyList<string>?> AskAsync(KeyboardInteractiveRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}
