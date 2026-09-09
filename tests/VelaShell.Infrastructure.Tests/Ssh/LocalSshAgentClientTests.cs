using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 本机 agent 端点的解析:配置 → <c>SSH_AUTH_SOCK</c> → 平台默认。
/// </summary>
/// <remarks>
/// 这段逻辑看着琐碎,却是 Windows 上 agent 能不能用的全部分水岭:
/// 装了 Git for Windows / msys2 的机器上 <c>SSH_AUTH_SOCK</c> 多半指向一个 Win32 程序
/// 打不开的伪套接字,而真正的 agent 在 <c>\\.\pipe\openssh-ssh-agent</c>。
/// 「环境变量优先、但用户能一口咬定」这条顺序正是为此存在的,值得钉住。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class LocalSshAgentClientTests
{
    private string? _savedAuthSock;

    [TestInitialize]
    public void SaveEnvironment() => _savedAuthSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");

    [TestCleanup]
    public void RestoreEnvironment() => Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", _savedAuthSock);

    [TestMethod]
    public void UserConfiguredEndpointWinsOverTheEnvironment()
    {
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "/tmp/from-env.sock");
        var client = new LocalSshAgentClient(() => @"\\.\pipe\my-agent");

        (string endpoint, SshAgentEndpointSource source) = client.ResolveEndpoint();

        Assert.AreEqual(@"\\.\pipe\my-agent", endpoint);
        Assert.AreEqual(SshAgentEndpointSource.UserConfigured, source);
    }

    /// <summary>只填了空白等于没填 —— 否则清空输入框会把 agent 整个关掉,而界面上看不出原因。</summary>
    [TestMethod]
    public void BlankConfiguredEndpointFallsBackToTheEnvironment()
    {
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "/tmp/from-env.sock");
        var client = new LocalSshAgentClient(() => "   ");

        (string endpoint, SshAgentEndpointSource source) = client.ResolveEndpoint();

        Assert.AreEqual("/tmp/from-env.sock", endpoint);
        Assert.AreEqual(SshAgentEndpointSource.Environment, source);
    }

    /// <summary>
    /// 两个来源都没有时:Windows 有平台默认(微软 OpenSSH 服务固定用那条管道),
    /// 类 Unix 没有 —— agent 的套接字名带随机后缀,除了环境变量无处可查。
    /// </summary>
    [TestMethod]
    public void WithoutAnySourceOnlyWindowsHasADefault()
    {
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
        var client = new LocalSshAgentClient(() => null);

        (string endpoint, SshAgentEndpointSource source) = client.ResolveEndpoint();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.AreEqual(LocalSshAgentClient.WindowsOpenSshPipe, endpoint);
            Assert.AreEqual(SshAgentEndpointSource.PlatformDefault, source);
        }
        else
        {
            Assert.AreEqual(string.Empty, endpoint);
            Assert.AreEqual(SshAgentEndpointSource.None, source);
        }
    }

    [TestMethod]
    public void NamedPipeEndpointsAreRecognisedAndStripped()
    {
        Assert.AreEqual("openssh-ssh-agent", LocalSshAgentClient.TryGetPipeName(LocalSshAgentClient.WindowsOpenSshPipe));
        Assert.AreEqual("pageant", LocalSshAgentClient.TryGetPipeName(@"\\.\PIPE\pageant"), "管道前缀不区分大小写");
    }

    /// <summary>
    /// 套接字路径不能被误判成管道 —— 判错的后果是拿一个路径当管道名去连,
    /// 报的错("找不到管道")与真正的原因("那是个伪套接字")毫不相干。
    /// </summary>
    [TestMethod]
    public void SocketPathsAreNotMistakenForPipes()
    {
        Assert.IsNull(LocalSshAgentClient.TryGetPipeName("/tmp/ssh-XXXX/agent.1234"));
        Assert.IsNull(LocalSshAgentClient.TryGetPipeName(@"C:\Users\me\AppData\Local\Temp\ssh-agent.sock"));
        Assert.IsNull(LocalSshAgentClient.TryGetPipeName(@"\\server\pipe\remote"), "远程管道不在支持范围");
    }

    /// <summary>探测不该抛:「本机没有 agent」是常态而非故障,原因放在结果里。</summary>
    [TestMethod]
    public async Task ProbeWithNoEndpointReportsUnavailableInsteadOfThrowing()
    {
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
        // 一个几乎不可能存在的端点:走到"连不上"这条分支,而不是"没有端点"那条。
        var client = new LocalSshAgentClient(() => @"\\.\pipe\vela-agent-does-not-exist-9f3a2b");

        SshAgentProbe probe = await client.ProbeAsync(TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(probe.IsAvailable);
        Assert.IsNotNull(probe.Error);
        Assert.IsEmpty(probe.Identities);
    }

    /// <summary>
    /// 对着一个**真的命名管道**跑一遍完整往返:连上 → 发 REQUEST_IDENTITIES → 收 IDENTITIES_ANSWER
    /// → 解出身份。
    /// </summary>
    /// <remarks>
    /// 上面那些用例只验端点字符串怎么挑,一个字节都没真的走过管道。而 Windows 上出问题的
    /// 恰恰是这一段:管道名要不要带前缀、异步标志给没给、读能不能读满一条报文 ——
    /// 这些在字符串断言里全看不见。真起一个 <see cref="NamedPipeServerStream" /> 假扮 agent,
    /// 把这条路径整个走通,不需要机器上真有 ssh-agent 服务(本机那个服务多半是禁用的)。
    /// </remarks>
    [TestMethod]
    public async Task ProbeAgainstARealNamedPipe_CompletesTheFullRoundTrip()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("命名管道只在 Windows 上;类 Unix 那半边走 AF_UNIX。");
            return;
        }

        string pipeName = $"vela-test-agent-{Guid.NewGuid():N}";
        byte[] blob = BuildKeyBlob("ssh-ed25519", [4, 2]);
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        CancellationToken token = TestContext.CancellationTokenSource.Token;

        Task agent = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(token);
            byte[]? request = await SshAgentRelay.ReadMessageAsync(server, token);
            Assert.IsNotNull(request);
            Assert.AreEqual(SshAgentProtocol.RequestIdentities, request[0]);
            await server.WriteAsync(SshAgentProtocol.Frame(BuildIdentitiesAnswer(blob, "ci@fake-agent")), token);
            await server.FlushAsync(token);
        }, token);

        var client = new LocalSshAgentClient(() => $@"\\.\pipe\{pipeName}");
        SshAgentProbe probe = await client.ProbeAsync(token);
        await agent;

        Assert.IsTrue(probe.IsAvailable, probe.Error);
        Assert.AreEqual(SshAgentEndpointSource.UserConfigured, probe.Source);
        Assert.HasCount(1, probe.Identities);
        Assert.AreEqual("ssh-ed25519", probe.Identities[0].KeyType);
        Assert.AreEqual(SshAgentProtocol.Fingerprint(blob), probe.Identities[0].Fingerprint);
        Assert.AreEqual("ci@fake-agent", probe.Identities[0].Comment);
    }

    /// <summary>测试上下文,MSTest 按约定注入。</summary>
    public TestContext TestContext { get; set; } = null!;

    // ---- SSH 线格式的报文构造(uint32 大端长度 + 字节)----

    private static byte[] BuildKeyBlob(string keyType, byte[] body)
    {
        var blob = new List<byte>();
        AppendString(blob, Encoding.ASCII.GetBytes(keyType));
        AppendString(blob, body);
        return [.. blob];
    }

    private static byte[] BuildIdentitiesAnswer(byte[] blob, string comment)
    {
        var payload = new List<byte> { SshAgentProtocol.IdentitiesAnswer };
        payload.AddRange(SshAgentRelay.Uint32(1));
        AppendString(payload, blob);
        AppendString(payload, Encoding.UTF8.GetBytes(comment));
        return [.. payload];
    }

    private static void AppendString(List<byte> target, byte[] value)
    {
        target.AddRange(SshAgentRelay.Uint32((uint)value.Length));
        target.AddRange(value);
    }
}
