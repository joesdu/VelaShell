using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 按连接指定 ssh-agent 端点(<see cref="SshSessionOptions.AgentEndpoint" />):写法的检查与展开,
/// 以及「SSH Agent」认证、agent 转发真的连的是那一个,而不是 <c>SSH_AUTH_SOCK</c> 指的系统默认。
/// </summary>
/// <remarks>
/// 认证走宿主真实的装配路径(<see cref="SshConnectionAssembler" /> → <see cref="VelaSshClientWrapper" />),对端是环回上的测试服务端,
/// agent 是 Unix 域套接字上的假 agent(Windows 10 1803 起也有 AF_UNIX,各平台同一份用例)。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class SshAgentEndpointTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------ 写法

    [TestMethod]
    public void 没填就是系统默认_存的是去掉空白与引号的原样()
    {
        Assert.IsTrue(SshAgentEndpoints.IsDefault(null));
        Assert.IsTrue(SshAgentEndpoints.IsDefault("   "));
        Assert.IsNull(SshAgentEndpoints.Normalize("  "));
        Assert.IsNull(SshAgentEndpoints.Resolve(null), "系统默认交 null,由库按 SSH_AUTH_SOCK / Windows 的 agent 服务去找");
        Assert.AreEqual("~/agent.sock", SshAgentEndpoints.Normalize("  \"~/agent.sock\" "));
    }

    [TestMethod]
    public void 连接时才展开主目录_管道前缀统一成库认的写法()
    {
        Assert.AreEqual(Path.Combine("/home/u", "agents/a.sock"), SshAgentEndpoints.Resolve("~/agents/a.sock", "/home/u"));
        Assert.AreEqual("/home/u", SshAgentEndpoints.Resolve("~", "/home/u"));
        Assert.AreEqual(@"\\.\pipe\openssh-ssh-agent", SshAgentEndpoints.Resolve(@"\\.\PIPE\openssh-ssh-agent"),
            "库按区分大小写的前缀认管道,大写的前缀不统一就会被当成套接字路径");
        Assert.AreEqual("/run/user/1000/agent.sock", SshAgentEndpoints.Resolve("/run/user/1000/agent.sock", "/home/u"));
    }

    [TestMethod]
    [DataRow(@"\\.\pipe\openssh-ssh-agent", true, SshAgentEndpointProblem.None)]
    [DataRow(@"\\.\pipe\openssh-ssh-agent", false, SshAgentEndpointProblem.PipeNotSupported)]
    [DataRow(@"\\.\pipe\", true, SshAgentEndpointProblem.PipeNameMissing)]
    [DataRow(@"\\.\pipe\a\b", true, SshAgentEndpointProblem.PipeNameMissing)]
    [DataRow(@"C:\Users\u\agent.sock", true, SshAgentEndpointProblem.None)]
    [DataRow(@"~/agent.sock", true, SshAgentEndpointProblem.None)]
    [DataRow(@"~/agent.sock", false, SshAgentEndpointProblem.None)]
    [DataRow("/run/user/1000/agent.sock", false, SshAgentEndpointProblem.None)]
    [DataRow("/run/user/1000/agent.sock", true, SshAgentEndpointProblem.NotFullPath)]
    [DataRow(@"C:\Users\u\agent.sock", false, SshAgentEndpointProblem.NotFullPath)]
    [DataRow("agents/relative.sock", false, SshAgentEndpointProblem.NotFullPath)]
    [DataRow(@"\\server\share\agent.sock", true, SshAgentEndpointProblem.NotFullPath)]
    public void 检查写法(string value, bool windows, SshAgentEndpointProblem expected)
    {
        string home = windows ? @"C:\Users\u" : "/home/u";
        Assert.AreEqual(expected, SshAgentEndpoints.Check(value, windows, macOS: false, home));
    }

    [TestMethod]
    public void 套接字路径不能超过sun_path()
    {
        string linux = "/tmp/" + new string('a', 102);   // 107 字节:正好
        Assert.AreEqual(SshAgentEndpointProblem.None, SshAgentEndpoints.Check(linux, windows: false, macOS: false, "/home/u"));
        Assert.AreEqual(SshAgentEndpointProblem.TooLong, SshAgentEndpoints.Check(linux + "a", windows: false, macOS: false, "/home/u"));
        Assert.AreEqual(SshAgentEndpointProblem.TooLong, SshAgentEndpoints.Check(linux, windows: false, macOS: true, "/home/u"), "macOS 只有 104 字节");
    }

    // ------------------------------------------------------------ 认证与转发连的是哪个 agent

    /// <summary>
    /// 「SSH Agent」认证连的是这条连接指定的 agent:<c>SSH_AUTH_SOCK</c> 指着一个不存在的套接字,照样登得上,签名出自指定的那个。
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Agent认证用连接指定的端点_不碰SSH_AUTH_SOCK()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using InMemorySshSigner signer = SshPrivateKeyFile.Parse(ecdsa.ExportPkcs8PrivateKeyPem());
        await using var agent = SocketAgent.Start(signer);
        using EnvironmentScope _ = new("SSH_AUTH_SOCK", Path.Combine(Path.GetTempPath(), $"vs-no-agent-{Guid.NewGuid():N}"[..40] + ".sock"));
        await using LoopbackSshHost host = new()
        {
            AuthPolicy = new TestAuthPolicy
            {
                RequiredMethods = ["publickey"],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            },
        };

        ConnectionInfo info = AgentInfo(host.Port, agent.Path);
        await using VelaSshClientWrapper ssh = await ConnectAsync(info);

        Assert.IsGreaterThan(0, agent.Signatures, "签名要出自连接指定的那个 agent");
    }

    /// <summary>指定的 agent 连不上:提示点名那个端点,不再叫人去起 Windows 的 OpenSSH agent 服务(那多半不是它)。</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task 指定的agent连不上时提示点名那个端点()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"vs-gone-{Guid.NewGuid():N}"[..36] + ".sock");
        await using LoopbackSshHost host = new();

        VelaSshAuthenticationException ex = await Assert.ThrowsAsync<VelaSshAuthenticationException>(
            async () => await ConnectAsync(AgentInfo(host.Port, missing)));

        Assert.Contains(missing, ex.Message);
        Assert.DoesNotContain(Strings.Get("SshErr_AgentNotRunning"), ex.Message, "那条提示讲的是 Windows 的 agent 服务,对指定的端点是误导");
        Assert.AreEqual(0, host.Accepted, "agent 连不上就不去连服务器");
    }

    /// <summary>指定的 agent 里一把钥都没有:提示也点名它。</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task 指定的agent里没有钥时提示点名那个端点()
    {
        await using var agent = SocketAgent.Start(signer: null);
        await using LoopbackSshHost host = new();

        VelaSshAuthenticationException ex = await Assert.ThrowsAsync<VelaSshAuthenticationException>(
            async () => await ConnectAsync(AgentInfo(host.Port, agent.Path)));

        Assert.AreEqual(Strings.Format("SshErr_AgentEndpointNoKeys", agent.Path), ex.Message);
    }

    /// <summary>agent 转发转发的也是这一个(与 OpenSSH 一致:<c>IdentityAgent</c> 换掉的就是被转发的那个);开 shell 那行灰字里写出来。</summary>
    [TestMethod]
    public void Agent转发用连接指定的端点()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SshSessionOptions features = new() { AgentForwarding = true, AgentEndpoint = "~/agents/deploy.sock" };

        AgentForwardOptions? pinned = SshForwardingOptions.Agent(features, []);
        AgentForwardOptions? fallback = SshForwardingOptions.Agent(new SshSessionOptions { AgentForwarding = true }, []);

        Assert.AreEqual(Path.Combine(home, "agents/deploy.sock"), pinned?.AgentEndpoint, "~ 在连接那一刻展开");
        Assert.IsNull(fallback?.AgentEndpoint, "没指定交 null:库按系统默认");
        Assert.Contains("~/agents/deploy.sock", SshForwardingOptions.DescribeAgent(features, pinned!));
        Assert.DoesNotContain("agent:", SshForwardingOptions.DescribeAgent(null, fallback!).Replace("ssh-agent", ""));
    }

    // ------------------------------------------------------------ 辅助

    private static ConnectionInfo AgentInfo(int port, string endpoint) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "joe",
        AuthMethod = AuthMethod.Agent,
        Ssh = new SshSessionOptions { AgentEndpoint = endpoint },
    };

    private static async Task<VelaSshClientWrapper> ConnectAsync(ConnectionInfo info)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        using CancellationTokenSource cts = new(Deadline);
        try
        {
            await wrapper.ConnectAsync(cts.Token);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }

    /// <summary>Unix 域套接字上的假 agent:只懂列身份(11)与签名(13)。</summary>
    private sealed class SocketAgent : IAsyncDisposable
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource _stop = new();
        private readonly ISshSigner? _signer;
        private Task _loop = Task.CompletedTask;
        private int _signatures;

        private SocketAgent(ISshSigner? signer, string path)
        {
            _signer = signer;
            Path = path;
        }

        /// <summary>套接字路径(短一些:sun_path 只有一百来个字节)。</summary>
        public string Path { get; }

        public int Signatures => Volatile.Read(ref _signatures);

        public static SocketAgent Start(ISshSigner? signer)
        {
            SocketAgent agent = new(signer, System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vsa-{Guid.NewGuid():N}"[..20] + ".sock"));
            agent._listener.Bind(new UnixDomainSocketEndPoint(agent.Path));
            agent._listener.Listen(8);
            agent._loop = agent.AcceptLoopAsync();
            return agent;
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    Socket client = await _listener.AcceptAsync(_stop.Token);
                    _ = ServeAsync(new NetworkStream(client, ownsSocket: true));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // 拆场。
            }
        }

        private async Task ServeAsync(NetworkStream stream)
        {
            await using (stream)
            {
                try
                {
                    byte[] header = new byte[4];
                    while (true)
                    {
                        await stream.ReadExactlyAsync(header, _stop.Token);
                        byte[] request = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                        await stream.ReadExactlyAsync(request, _stop.Token);
                        byte[] response = await AnswerAsync(request);
                        byte[] framed = new byte[4 + response.Length];
                        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)response.Length);
                        response.CopyTo(framed, 4);
                        await stream.WriteAsync(framed, _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
                {
                    // 客户端走了。
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
                        answer.WriteByte(12);
                        WriteUInt32(answer, _signer is null ? 0u : 1u);
                        if (_signer is not null)
                        {
                            WriteString(answer, _signer.PublicKey.Blob.Span);
                            WriteString(answer, "vela-endpoint-test"u8);
                        }
                        return answer.ToArray();
                    }
                case 13 when _signer is not null: // SSH_AGENTC_SIGN_REQUEST
                    {
                        int offset = 1;
                        ReadString(request, ref offset);
                        byte[] data = ReadString(request, ref offset);
                        byte[] signature = await _signer.SignAsync(data, _signer.SignatureAlgorithms[0]);
                        Interlocked.Increment(ref _signatures);
                        using MemoryStream answer = new();
                        answer.WriteByte(14);
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
            _listener.Dispose();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>临时改一个环境变量,离开作用域时还原(本程序集不并行跑用例)。</summary>
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
}
