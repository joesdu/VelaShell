using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Transport;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 「自动加载密钥到 Agent」(<see cref="SshAgentKeyLoader" />):什么时候加、加过的不再加、
/// agent 出任何问题都只是「没加上」,不抛到连接那边去。
/// </summary>
/// <remarks>
/// 报文层面对不对(私钥字段顺序、约束编码)由 SSH 库的 <c>AgentAddIdentityTests</c> 盯着,
/// 这里的假 agent 只数请求、不解析私钥。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class SshAgentKeyLoaderTests
{
    /// <summary>只懂「列身份」与「加钥」两条请求的假 agent。</summary>
    private sealed class FakeAgent
    {
        private readonly List<byte[]> _blobs = [];

        public int AddRequests { get; private set; }

        public bool RejectAdditions { get; init; }

        public void Hold(InMemorySshSigner key) => _blobs.Add(key.PublicKey.Blob.ToArray());

        public ValueTask<SshAgentClient> ConnectAsync(CancellationToken cancellationToken)
        {
            (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
            // 服务端跟着连接走,不跟着调用方的令牌走:客户端释放时流会读到结尾。
            _ = Task.Run(() => ServeAsync(theirs, CancellationToken.None), CancellationToken.None);
            return ValueTask.FromResult(SshAgentClient.FromStream(ours, "(假 agent)"));
        }

        private async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header, cancellationToken);
                    byte[] request = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                    await stream.ReadExactlyAsync(request, cancellationToken);

                    byte[] response = request[0] switch
                    {
                        11 => Identities(),
                        17 => Add(),
                        _ => [5],
                    };

                    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)response.Length);
                    await stream.WriteAsync(header, cancellationToken);
                    await stream.WriteAsync(response, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
            {
                // 客户端走了。
            }
        }

        private byte[] Add()
        {
            AddRequests++;
            return RejectAdditions ? [5] : [6];
        }

        private byte[] Identities()
        {
            ArrayBufferWriter<byte> buffer = new();
            Write(buffer, [12]);
            WriteUInt32(buffer, (uint)_blobs.Count);
            foreach (byte[] blob in _blobs)
            {
                WriteUInt32(buffer, (uint)blob.Length);
                Write(buffer, blob);
                byte[] comment = Encoding.UTF8.GetBytes("held");
                WriteUInt32(buffer, (uint)comment.Length);
                Write(buffer, comment);
            }
            return buffer.WrittenSpan.ToArray();
        }

        private static void WriteUInt32(ArrayBufferWriter<byte> buffer, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.GetSpan(4), value);
            buffer.Advance(4);
        }

        private static void Write(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> bytes) => buffer.Write(bytes);
    }

    [TestMethod]
    public async Task AddsTheKeyWhenTheAgentDoesNotHoldIt()
    {
        FakeAgent agent = new();
        using var key = InMemorySshSigner.GenerateEd25519();

        SshAgentKeyLoader.Outcome outcome = await SshAgentKeyLoader.AddAsync(key, "~/.ssh/id_ed25519", agent.ConnectAsync);

        Assert.AreEqual(SshAgentKeyLoader.Outcome.Added, outcome);
        Assert.AreEqual(1, agent.AddRequests);
    }

    /// <summary>每连一次就往 agent 里写一次私钥没有必要:先列一遍,有了就不加。</summary>
    [TestMethod]
    public async Task SkipsAKeyTheAgentAlreadyHolds()
    {
        FakeAgent agent = new();
        using var key = InMemorySshSigner.GenerateEd25519();
        using var other = InMemorySshSigner.GenerateEd25519();
        agent.Hold(other);
        agent.Hold(key);

        SshAgentKeyLoader.Outcome outcome = await SshAgentKeyLoader.AddAsync(key, "k", agent.ConnectAsync);

        Assert.AreEqual(SshAgentKeyLoader.Outcome.AlreadyPresent, outcome);
        Assert.AreEqual(0, agent.AddRequests);
    }

    [TestMethod]
    public async Task AnAgentThatRefusesIsReportedNotThrown()
    {
        FakeAgent agent = new() { RejectAdditions = true };
        using var key = InMemorySshSigner.GenerateEd25519();

        SshAgentKeyLoader.Outcome outcome = await SshAgentKeyLoader.AddAsync(key, "k", agent.ConnectAsync);

        Assert.AreEqual(SshAgentKeyLoader.Outcome.Failed, outcome);
        Assert.AreEqual(1, agent.AddRequests);
    }

    /// <summary>agent 没在跑是最常见的情形(Windows 上服务默认不启动),不能因此抛出。</summary>
    [TestMethod]
    public async Task AnUnreachableAgentIsReportedNotThrown()
    {
        using var key = InMemorySshSigner.GenerateEd25519();

        SshAgentKeyLoader.Outcome notRunning = await SshAgentKeyLoader.AddAsync(
            key, "k", _ => throw new SshAgentException(SshFailureReason.AgentUnavailable, "连不上"));
        SshAgentKeyLoader.Outcome timedOut = await SshAgentKeyLoader.AddAsync(
            key, "k", _ => throw new OperationCanceledException());

        Assert.AreEqual(SshAgentKeyLoader.Outcome.Failed, notRunning);
        Assert.AreEqual(SshAgentKeyLoader.Outcome.Failed, timedOut);
    }

    [TestMethod]
    public void OnlyPrivateKeyAuthenticationOffersAKey()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        ConnectionInfo privateKey = new()
        {
            Host = "h",
            Username = "u",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "C:/keys/id_ed25519",
        };

        Assert.IsTrue(SshAgentKeyLoader.TryGetKeyToAdd(
            privateKey, [new PublicKeyCredential(key, "C:/keys/id_ed25519")], out InMemorySshSigner offered, out string comment));
        Assert.AreSame(key, offered);
        Assert.AreEqual("C:/keys/id_ed25519", comment, "注释写私钥文件路径,与 ssh-add 一致");

        // 证书:签名器包着证书,库暂不支持「证书 + 私钥」的加钥格式。
        ConnectionInfo certificate = new() { Host = "h", Username = "u", AuthMethod = AuthMethod.Certificate };
        Assert.IsFalse(SshAgentKeyLoader.TryGetKeyToAdd(
            certificate, [new PublicKeyCredential(key)], out _, out _));

        // 密码:没有钥。
        ConnectionInfo password = new() { Host = "h", Username = "u", AuthMethod = AuthMethod.Password };
        Assert.IsFalse(SshAgentKeyLoader.TryGetKeyToAdd(password, [new PasswordCredential("pw")], out _, out _));
    }
}
