// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

/// <summary>agent 协议的报文编号。</summary>
internal static class SshAgentMessage
{
    public const byte Failure = 5;
    public const byte Success = 6;
    public const byte RequestIdentities = 11;
    public const byte IdentitiesAnswer = 12;
    public const byte SignRequest = 13;
    public const byte SignResponse = 14;
}

/// <summary>签名请求的标志位（OpenSSH PROTOCOL.agent）。</summary>
[Flags]
internal enum SshAgentSignFlags : uint
{
    None = 0,

    /// <summary>用 <c>rsa-sha2-256</c> 而不是 SHA-1 的 <c>ssh-rsa</c>。</summary>
    RsaSha2_256 = 0x02,

    /// <summary>用 <c>rsa-sha2-512</c>。</summary>
    RsaSha2_512 = 0x04,
}

/// <summary>agent 里的一把密钥。</summary>
/// <param name="PublicKey">公钥。</param>
/// <param name="Comment">agent 给的注释，通常是私钥文件路径。</param>
public sealed record SshAgentIdentity(SshPublicKey PublicKey, string Comment);

/// <summary>连不上 agent，或者 agent 拒绝了。</summary>
public sealed class SshAgentException : SshException
{
    /// <summary>创建一个 agent 异常。</summary>
    public SshAgentException(string message, Exception? innerException = null)
        : base(SshFailureReason.Unsupported, SshPhase.Authenticating, message, innerException)
    {
    }
}

/// <summary>本机 ssh-agent 的客户端。</summary>
/// <remarks>
/// <para>
/// <b>私钥从不进入本进程。</b>我们只是把「请用这把公钥签这段数据」递过去。
/// 这也是加密私钥的推荐出路：把密钥加进 agent，本库通过 agent 用它。
/// </para>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §7.2〕<b>我们只做客户端，不做 agent 服务端。</b>
/// 本机 agent 由操作系统或别的软件提供（OpenSSH agent / Pageant / 1Password…）。
/// </para>
/// </remarks>
public sealed class SshAgentClient : IAsyncDisposable
{
    /// <summary>单个 agent 报文的长度上限。</summary>
    private const int MaxMessageLength = 256 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    private SshAgentClient(Stream stream, string endpoint)
    {
        _stream = stream;
        Endpoint = endpoint;
    }

    /// <summary>连到的是哪个端点（套接字路径或命名管道名）。</summary>
    public string Endpoint { get; }

    /// <summary>在一条现成的流上说 agent 协议。</summary>
    /// <param name="stream">双工流。本对象释放时会一并释放它。</param>
    /// <param name="label">给人看的端点名，进日志与异常。</param>
    /// <remarks>
    /// 不只是为了测试：agent 也可能在一条隧道的另一头，
    /// 或者由别的软件以自定义方式提供（1Password、YubiKey 代理…）。
    /// 那些情形下调用方自己把流准备好，交给我们说协议。
    /// </remarks>
    public static SshAgentClient FromStream(Stream stream, string label = "(自定义流)")
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SshAgentClient(stream, label);
    }

    /// <summary>本机 agent 的默认端点。</summary>
    /// <remarks>
    /// Windows 上是 OpenSSH 的命名管道；其它平台看 <c>SSH_AUTH_SOCK</c>。
    /// </remarks>
    public static string? DefaultEndpoint =>
        OperatingSystem.IsWindows()
            ? @"\\.\pipe\openssh-ssh-agent"
            : Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");

    /// <summary>连本机 agent。</summary>
    /// <param name="endpoint">端点；<see langword="null"/> 表示用 <see cref="DefaultEndpoint"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshAgentException">连不上。</exception>
    public static async ValueTask<SshAgentClient> ConnectAsync(
        string? endpoint = null, CancellationToken cancellationToken = default)
    {
        string? actual = endpoint ?? DefaultEndpoint;

        if (string.IsNullOrEmpty(actual))
        {
            throw new SshAgentException(
                OperatingSystem.IsWindows()
                    ? "找不到 ssh-agent。Windows 上它是一个服务，用 " +
                      "`Get-Service ssh-agent` 看状态，`Start-Service ssh-agent` 起它。"
                    : "环境变量 SSH_AUTH_SOCK 没有设 —— 本机没有在跑 ssh-agent，" +
                      "或者当前会话没继承到它。");
        }

        try
        {
            if (OperatingSystem.IsWindows() && actual.StartsWith(@"\\.\pipe\", StringComparison.Ordinal))
            {
                string pipeName = actual[@"\\.\pipe\".Length..];
                NamedPipeClientStream pipe = new(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return new SshAgentClient(pipe, actual);
            }

            // Unix 套接字。
            //
            // ⚠️ Windows 上 SSH_AUTH_SOCK 常常指向 msys / WSL 的套接字，
            //    那是另一套东西，.NET 连不上去 —— 所以上面的命名管道分支在前。
            Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(actual), cancellationToken)
                .ConfigureAwait(false);

            return new SshAgentClient(new NetworkStream(socket, ownsSocket: true), actual);
        }
        catch (Exception ex) when (ex is not SshAgentException and not OperationCanceledException)
        {
            throw new SshAgentException($"连不上 ssh-agent（{actual}）：{ex.Message}", ex);
        }
    }

    /// <summary>列出 agent 里的密钥。</summary>
    public async ValueTask<IReadOnlyList<SshAgentIdentity>> ListIdentitiesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] response = await ExchangeAsync(
            new byte[] { SshAgentMessage.RequestIdentities }, cancellationToken).ConfigureAwait(false);

        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        byte type = reader.ReadByte();

        if (type != SshAgentMessage.IdentitiesAnswer)
        {
            throw new SshAgentException($"agent 回了 {type} 而不是身份列表。");
        }

        uint count = reader.ReadUInt32();
        List<SshAgentIdentity> identities = [];

        for (uint i = 0; i < count && i < 1024; i++)
        {
            byte[] blob = reader.ReadStringAsArray(MaxMessageLength);
            string comment = reader.ReadUtf8String(MaxMessageLength);

            try
            {
                identities.Add(new SshAgentIdentity(SshPublicKey.Parse(blob), comment));
            }
            catch (SshWireFormatException)
            {
                // agent 里可能有我们不认识类型的密钥（证书、FIDO、厂商私有）。
                // **跳过它就好** —— 为其中一把报错等于让整个 agent 用不了。
            }
            catch (NotSupportedException)
            {
                // 同上。
            }
        }

        return identities;
    }

    /// <summary>让 agent 用某把密钥签一段数据。</summary>
    /// <param name="publicKeyBlob">要用哪把钥（公钥 blob）。</param>
    /// <param name="data">被签名的数据。</param>
    /// <param name="algorithm">签名算法名；RSA 时决定用 SHA-256 还是 SHA-512。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>SSH 格式的签名 blob。</returns>
    public async ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> publicKeyBlob,
        ReadOnlyMemory<byte> data,
        string algorithm,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SshAgentSignFlags flags = algorithm switch
        {
            SshAlgorithmNames.RsaSha512 => SshAgentSignFlags.RsaSha2_512,
            SshAlgorithmNames.RsaSha256 => SshAgentSignFlags.RsaSha2_256,

            // 其它类型（ed25519、ecdsa）没有可选项，标志位留空。
            _ => SshAgentSignFlags.None,
        };

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        writer.WriteByte(SshAgentMessage.SignRequest);
        writer.WriteString(publicKeyBlob.Span);
        writer.WriteString(data.Span);
        writer.WriteUInt32((uint)flags);

        byte[] response = await ExchangeAsync(request.WrittenMemory, cancellationToken).ConfigureAwait(false);

        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        byte type = reader.ReadByte();

        if (type == SshAgentMessage.Failure)
        {
            // agent 拒签的原因它不会告诉我们 —— 但最常见的两种值得点出来。
            throw new SshAgentException(
                "ssh-agent 拒绝签名。常见原因：这把密钥已经不在 agent 里了，" +
                "或者 agent 配了确认（ssh-add -c）而使用者没有批准。");
        }

        if (type != SshAgentMessage.SignResponse)
        {
            throw new SshAgentException($"agent 回了 {type} 而不是签名。");
        }

        return reader.ReadStringAsArray(MaxMessageLength);
    }

    /// <summary>把 agent 里的一把密钥包成签名器。</summary>
    public ISshSigner CreateSigner(SshAgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new AgentSigner(this, identity);
    }

    /// <summary>把 agent 里的密钥全部包成公钥凭据。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/04 §2.2〕库<b>不会自动</b>去连 agent ——
    /// 这个方法要由使用者显式调用，结果也要由使用者显式加进凭据列表。
    /// </remarks>
    public async ValueTask<IReadOnlyList<SshCredential>> GetCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SshAgentIdentity> identities =
            await ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. identities.Select(id =>
                new PublicKeyCredential(CreateSigner(id), $"agent: {id.Comment}")),
        ];
    }

    // ------------------------------------------------------------ 收发

    private async ValueTask<byte[]> ExchangeAsync(
        ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        // agent 协议是严格的一问一答，没有 id —— 所以并发调用必须串起来。
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)request.Length);

            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            await _stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (length is 0 or > MaxMessageLength)
            {
                throw new SshAgentException($"agent 报文长度不合理：{length}。");
            }

            byte[] response = new byte[length];
            await _stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (EndOfStreamException ex)
        {
            throw new SshAgentException("ssh-agent 在应答之前就断开了。", ex);
        }
        catch (IOException ex)
        {
            throw new SshAgentException($"与 ssh-agent 通信失败：{ex.Message}", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>一把在 agent 里的密钥。</summary>
    /// <remarks>
    /// <see cref="ISshSigner.IsLocalAndCheap"/> 为 <see langword="false"/>：
    /// agent 可能配了「每次签名都要确认」（<c>ssh-add -c</c>），
    /// 那会弹窗。为一把服务端根本不认的密钥去打扰使用者是不可接受的，
    /// 所以公钥认证会先探测再签。
    /// </remarks>
    private sealed class AgentSigner(SshAgentClient client, SshAgentIdentity identity) : ISshSigner
    {
        public SshPublicKey PublicKey => identity.PublicKey;

        public IReadOnlyList<string> SignatureAlgorithms => identity.PublicKey.SignatureAlgorithms;

        public bool IsLocalAndCheap => false;

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
            client.SignAsync(identity.PublicKey.Blob, data, algorithm, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        _lock.Dispose();
    }
}
