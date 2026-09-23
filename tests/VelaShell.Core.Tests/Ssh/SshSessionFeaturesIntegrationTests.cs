using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 压缩、ssh-agent(认证与转发)、X11 转发在<b>真实 OpenSSH 服务端</b>上的端到端行为。
/// </summary>
/// <remarks>
/// <para>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-shells</c>(端口 2223,账号 vela-bash,
/// 见 <c>tests/fixtures/ssh-shells/Dockerfile</c>:开了 <c>X11Forwarding</c> 并装了 xauth)。
/// 连接走宿主真正的装配路径(<see cref="SshConnectionAssembler" /> + <see cref="VelaSshClientWrapper" />),
/// 不是直接调库 —— 要验的正是「配置里的开关有没有一路接到线上」。
/// </para>
/// <para>
/// 两个转发都验到<b>另一头真的被连上</b>为止,而不只是看 <c>DISPLAY</c> / <c>SSH_AUTH_SOCK</c>
/// 有没有被设上:本机起一个假的 X 服务器 / 假的 agent,远端发起连接,断言它们收到了东西。
/// </para>
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
[TestCategory("DockerIntegration")]
[DoNotParallelize]
public class SshSessionFeaturesIntegrationTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2223;
    private const string TestUser = "vela-bash";
    private const string TestPassword = "velapass";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>开了压缩:协商结果真的是 <c>zlib@openssh.com</c>,而且会话照常可用。</summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Compression_IsNegotiatedAndTheSessionWorks()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync(new SshSessionOptions { Compression = true });

        Assert.AreEqual("zlib@openssh.com", ssh.InnerConnection!.Algorithms!.Value.CompressionClientToServer);
        Assert.AreEqual("zlib@openssh.com", ssh.InnerConnection.Algorithms.Value.CompressionServerToClient);

        // 压缩只在认证之后才开始(delayed zlib):这条命令的往返是真正走压缩的那一段。
        string echoed = await ssh.RunCommandAsync("head -c 20000 /dev/zero | tr '\\0' a | wc -c");
        Assert.AreEqual("20000", echoed.Trim());
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Compression_Off_NegotiatesNone()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync(null);

        Assert.AreEqual("none", ssh.InnerConnection!.Algorithms!.Value.CompressionClientToServer);
    }

    /// <summary>
    /// X11 转发一路通到本机:远端往 <c>$DISPLAY</c> 发一个 X11 建立报文,本机的假 X 服务器收到它,
    /// 而且 cookie 已经被换掉 —— 服务端那边拿到的永远是假 cookie。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task X11_RemoteClientReachesTheLocalDisplay()
    {
        RequireContainer();
        (TcpListener listener, int display) = StartFakeXServer();
        try
        {
            Task<byte[]> received = ReceiveSetupAsync(listener);
            await using VelaSshClientWrapper ssh = await ConnectAsync(new SshSessionOptions
            {
                X11Forwarding = true,
                X11Display = $"127.0.0.1:{display}.0",
                X11Trusted = true,
            });
            await using IShellStreamWrapper shell = await OpenShellAsync(ssh);
            AssertNoWarnings(shell);
            Assert.Contains(n => n.Text.Contains($"127.0.0.1:{display}.0", StringComparison.Ordinal), shell.Notices,
                "开成了要告诉用户转发到了哪个显示。");

            // 用 bash 的 /dev/tcp 手搓一个 X 客户端:建立报文 + 远端 .Xauthority 里那个(假)cookie。
            // 小端 'l';协议 11.0;鉴权名 18 字节(补齐到 20)、数据 16 字节。
            const string client =
                """
                ( d=${DISPLAY#*:}; d=${d%%.*}; c=$(xauth list | grep ":$d " | awk '{print $3}' | tail -1); exec 3<>/dev/tcp/127.0.0.1/$((6000+d)); printf 'l\0\13\0\0\0\22\0\20\0\0\0MIT-MAGIC-COOKIE-1\0\0' >&3; printf "$(printf %s "$c" | sed 's/../\\x&/g')" >&3; sleep 1 ); echo X11-$((1+1))DONE
                """;
            string output = await RunInShellAsync(shell, client, "X11-2DONE");

            byte[] setup = await received.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsGreaterThanOrEqualTo(48, setup.Length, $"建立报文不完整。远端输出:\n{output}");
            Assert.AreEqual((byte)'l', setup[0]);
            Assert.AreEqual("MIT-MAGIC-COOKIE-1", Encoding.ASCII.GetString(setup, 12, 18));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 服务端拒绝 X11 时 shell 照样打开,终端里留一条带原因的黄字提示 —— 这里用一个
    /// 认不出来的显示地址触发本机这一侧的失败。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task X11_BadDisplay_StillOpensTheShellWithAWarning()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync(new SshSessionOptions
        {
            X11Forwarding = true,
            X11Display = "definitely-not-a-display",
        });
        await using IShellStreamWrapper shell = await OpenShellAsync(ssh);

        Assert.Contains(n => n.IsWarning, shell.Notices);
        string output = await RunInShellAsync(shell, "echo \"D=[$DISPLAY]\"; echo SHELL-$((1+1))OK", "SHELL-2OK");
        Assert.Contains("D=[]", output, "没请求 X11 时远端不该有 DISPLAY。");
    }

    /// <summary>
    /// 服务端拒绝 X11(<c>vela-dash</c> 在靶机上单独关了 <c>X11Forwarding</c>)时:X11 是尽力而为的,
    /// shell 一次就开成,同时请求的 agent 转发不受连累,终端里只留一条 X11 的黄字提示。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce()
    {
        RequireContainer();
        RequireWindowsPipes();
        await using var agent = FakeAgent.Start(signer: null);
        using EnvironmentScope scope = new("SSH_AUTH_SOCK", agent.Endpoint);

        await using VelaSshClientWrapper ssh = await ConnectAsync(
            new SshSessionOptions { X11Forwarding = true, X11Display = "127.0.0.1:0.0", AgentForwarding = true },
            user: "vela-dash");
        await using IShellStreamWrapper shell = await OpenShellAsync(ssh);

        string notices = string.Join(" | ", shell.Notices.Select(n => (n.IsWarning ? "!" : "") + n.Text));
        Assert.AreEqual(1, shell.Notices.Count(n => n.IsWarning), notices);
        Assert.Contains(n => !n.IsWarning && n.Text == Strings.Get("Ssh_AgentForwardOn"), shell.Notices, notices);

        string output = await RunInShellAsync(
            shell, "echo \"D=[$DISPLAY]\"; ssh-add -l; echo DASH-$((1+1))DONE", "DASH-2DONE");
        Assert.Contains("D=[]", output, $"X11 没开成,远端不该有 DISPLAY。远端输出:\n{output}");
        Assert.IsGreaterThan(0, agent.Requests, "agent 转发被 X11 的失败连累了。");
    }

    /// <summary>
    /// agent 转发一路通到本机:远端 <c>ssh-add -l</c> 问到的是本机那个(假)agent。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task AgentForwarding_RemoteSshAddTalksToTheLocalAgent()
    {
        RequireContainer();
        RequireWindowsPipes();
        await using var agent = FakeAgent.Start(signer: null);
        using EnvironmentScope scope = new("SSH_AUTH_SOCK", agent.Endpoint);

        await using VelaSshClientWrapper ssh = await ConnectAsync(new SshSessionOptions { AgentForwarding = true });
        await using IShellStreamWrapper shell = await OpenShellAsync(ssh);
        AssertNoWarnings(shell);

        string output = await RunInShellAsync(shell, "ssh-add -l; echo AGENT-$((1+1))DONE", "AGENT-2DONE");

        Assert.Contains("no identities", output, $"远端输出:\n{output}");
        Assert.IsGreaterThan(0, agent.Requests, "本机 agent 一次都没被问到 —— 转发没通。");
    }

    /// <summary>
    /// SSH Agent 认证:私钥只在(假)agent 里,配置里一个字的凭据都没有,也能登上去。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task AgentAuthentication_LogsInWithAKeyThatOnlyTheAgentHolds()
    {
        RequireContainer();
        RequireWindowsPipes();

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ISshSigner signer = SshPrivateKeyFile.Parse(ecdsa.ExportPkcs8PrivateKeyPem());
        string publicLine =
            $"{signer.PublicKey.KeyType} {Convert.ToBase64String(signer.PublicKey.Blob.Span)} vela-agent-test";

        // 先用口令把公钥放进 authorized_keys。
        await using (VelaSshClientWrapper setup = await ConnectAsync(null))
        {
            await setup.RunCommandAsync(
                $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '{publicLine}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
        }

        try
        {
            await using var agent = FakeAgent.Start(signer);
            using EnvironmentScope scope = new("SSH_AUTH_SOCK", agent.Endpoint);

            SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
                new ConnectionInfo
                {
                    Host = TestHost,
                    Port = TestPort,
                    Username = TestUser,
                    AuthMethod = AuthMethod.Agent,
                },
                hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
            await using VelaSshClientWrapper ssh = new(assembled.Connect, assembled.ConnectTimeout, assembled.DialerLifetime);
            await ssh.ConnectAsync(TestContext.CancellationToken);

            Assert.AreEqual(TestUser, (await ssh.RunCommandAsync("whoami")).Trim());
            Assert.IsGreaterThan(0, agent.Signatures, "认证没有经过 agent 签名。");
        }
        finally
        {
            await using VelaSshClientWrapper cleanup = await ConnectAsync(null);
            await cleanup.RunCommandAsync("sed -i '/vela-agent-test/d' ~/.ssh/authorized_keys");
        }
    }

    // ------------------------------------------------------------ 连接与 shell

    private async Task<VelaSshClientWrapper> ConnectAsync(SshSessionOptions? features, string user = TestUser)
    {
        ConnectionInfo info = new()
        {
            Host = TestHost,
            Port = TestPort,
            Username = user,
            AuthMethod = AuthMethod.Password,
            Password = TestPassword,
            Ssh = features,
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(
            assembled.Connect, assembled.ConnectTimeout, assembled.DialerLifetime, info.Ssh);
        await wrapper.ConnectAsync(TestContext.CancellationToken);
        return wrapper;
    }

    private Task<IShellStreamWrapper> OpenShellAsync(VelaSshClientWrapper ssh) =>
        ssh.CreateShellStreamAsync("xterm-256color", 120, 40, 0, 0, 4096,
            cancellationToken: TestContext.CancellationToken);

    private static void AssertNoWarnings(IShellStreamWrapper shell) =>
        Assert.DoesNotContain(n => n.IsWarning, shell.Notices,
            "不该有转发失败的提示:" + string.Join(" | ", shell.Notices.Select(n => n.Text)));

    /// <summary>往交互式 shell 里敲一行,读到 <paramref name="marker" /> 为止,返回期间的全部输出。</summary>
    private static async Task<string> RunInShellAsync(IShellStreamWrapper shell, string command, string marker)
    {
        byte[] line = Encoding.UTF8.GetBytes(command + "\n");
        await shell.WriteAsync(line, 0, line.Length, CancellationToken.None);

        StringBuilder output = new();
        byte[] buffer = new byte[8192];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        // marker 由命令现算出来(如 echo X-$((1+1))DONE → X-2DONE):回显里只有算式,不会误判;
        // 也不怕 readline 在折行处往回显里插控制序列,把字面值截成两半。
        while (!output.ToString().Contains(marker, StringComparison.Ordinal))
        {
            int read = await shell.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
            if (read == 0)
            {
                break;
            }
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return output.ToString();
    }

    // ------------------------------------------------------------ 假 X 服务器

    /// <summary>在 127.0.0.1:6000+N 上监听,N 取一个没被占用的号。</summary>
    private static (TcpListener Listener, int Display) StartFakeXServer()
    {
        for (int display = 42; display < 90; display++)
        {
            TcpListener listener = new(IPAddress.Loopback, 6000 + display);
            try
            {
                listener.Start();
                return (listener, display);
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }
        Assert.Inconclusive("本机 6042–6089 端口都被占用,起不了假 X 服务器。");
        throw new UnreachableException();
    }

    /// <summary>收下第一条连接上的建立报文(12 字节头 + 鉴权名与数据)。</summary>
    private static async Task<byte[]> ReceiveSetupAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[48];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    // ------------------------------------------------------------ 假 agent

    /// <summary>
    /// 一个只懂两条请求的 ssh-agent:列身份、签名。挂在命名管道上,测试用 <c>SSH_AUTH_SOCK</c> 指过来。
    /// </summary>
    private sealed class FakeAgent : IAsyncDisposable
    {
        private readonly string _pipeName = "vela-test-agent-" + Guid.NewGuid().ToString("N");
        private readonly ISshSigner? _signer;
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private int _requests;
        private int _signatures;

        private FakeAgent(ISshSigner? signer) => _signer = signer;

        public string Endpoint => @"\\.\pipe\" + _pipeName;

        public int Requests => Volatile.Read(ref _requests);

        public int Signatures => Volatile.Read(ref _signatures);

        public static FakeAgent Start(ISshSigner? signer)
        {
            FakeAgent agent = new(signer);
            agent._loop = agent.AcceptLoopAsync();
            return agent;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = new(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    return;
                }
                _ = ServeAsync(pipe);
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            await using (pipe)
            {
                try
                {
                    byte[] header = new byte[4];
                    while (true)
                    {
                        await pipe.ReadExactlyAsync(header, _stop.Token);
                        byte[] request = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                        await pipe.ReadExactlyAsync(request, _stop.Token);
                        Interlocked.Increment(ref _requests);

                        byte[] response = await AnswerAsync(request);
                        byte[] framed = new byte[4 + response.Length];
                        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)response.Length);
                        response.CopyTo(framed, 4);
                        await pipe.WriteAsync(framed, _stop.Token);
                        await pipe.FlushAsync(_stop.Token);
                    }
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
                {
                    // 对端收工。
                }
            }
        }

        private async Task<byte[]> AnswerAsync(byte[] request)
        {
            switch (request[0])
            {
                case 11: // SSH_AGENTC_REQUEST_IDENTITIES
                    {
                        using MemoryStream answer = new();
                        answer.WriteByte(12); // SSH_AGENT_IDENTITIES_ANSWER
                        if (_signer is null)
                        {
                            WriteUInt32(answer, 0);
                        }
                        else
                        {
                            WriteUInt32(answer, 1);
                            WriteString(answer, _signer.PublicKey.Blob.Span);
                            WriteString(answer, "vela-agent-test"u8);
                        }
                        return answer.ToArray();
                    }

                case 13 when _signer is not null: // SSH_AGENTC_SIGN_REQUEST
                    {
                        int offset = 1;
                        ReadString(request, ref offset); // key blob
                        byte[] data = ReadString(request, ref offset);
                        byte[] signature = await _signer.SignAsync(data, _signer.SignatureAlgorithms[0]);
                        Interlocked.Increment(ref _signatures);

                        using MemoryStream answer = new();
                        answer.WriteByte(14); // SSH_AGENT_SIGN_RESPONSE
                        WriteString(answer, signature);
                        return answer.ToArray();
                    }

                default:
                    return [5]; // SSH_AGENT_FAILURE
            }
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
        {
            WriteUInt32(stream, (uint)value.Length);
            stream.Write(value);
        }

        private static byte[] ReadString(byte[] buffer, ref int offset)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
            byte[] value = buffer.AsSpan(offset + 4, length).ToArray();
            offset += 4 + length;
            return value;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Dispose();
        }
    }

    /// <summary>临时改一个环境变量,离开作用域时还原。</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    // ------------------------------------------------------------ 环境

    /// <summary>跳过记 Inconclusive 而不是"通过":全绿的报告里不能混着一行断言都没跑的用例。</summary>
    private static void RequireContainer()
    {
        try
        {
            using TcpClient tcp = new();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"[SKIP] ssh-shells 靶机 {TestHost}:{TestPort} 不可达。" +
                                    "运行 'docker compose -f docker-compose.test.yml up -d --build ssh-shells'。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"[SKIP] ssh-shells 靶机 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }

    /// <summary>假 agent 挂在命名管道上;宿主只在 Windows 上把 SSH_AUTH_SOCK 当管道用。</summary>
    private static void RequireWindowsPipes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("[SKIP] 假 agent 用的是 Windows 命名管道。");
        }
    }
}
