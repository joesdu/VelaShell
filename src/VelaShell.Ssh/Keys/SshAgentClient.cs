// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3)

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

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
            throw new SshAgentException(SshFailureReason.AgentUnavailable,
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

                try
                {
                    await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    EnsureTrustedPipeServer(pipe, actual);
                    return new SshAgentClient(pipe, actual);
                }
                catch (Exception)
                {
                    // 连不上、超时（宿主只等三秒）或属主不对：句柄当场关掉，不留给终结器。
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
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
            throw new SshAgentException(SshFailureReason.AgentUnavailable, $"连不上 ssh-agent（{actual}）：{ex.Message}", ex);
        }
    }

    /// <summary>确认命名管道的另一头是可信的 agent，而不是抢先占了这个管道名的别的用户。</summary>
    /// <remarks>
    /// <para>
    /// Windows 上 OpenSSH agent 的管道名是固定的（<c>openssh-ssh-agent</c>）。服务没在跑的时候，
    /// 本机任何一个用户都能先把这个名字建出来 —— 之后我们发过去的就是签名请求，
    /// 开了「自动加载密钥到 Agent」的话，还有**明文私钥**。
    /// </para>
    /// <para>
    /// 判据是管道对象的属主：抢先建管道的人只能把属主设成自己（或自己所在、可以当属主的组）。
    /// 可信的只有三种 —— 当前用户（1Password、Pageant、KeePassXC 之类以当前用户身份跑的 agent）、
    /// SYSTEM 与 Administrators（OpenSSH 的 agent 服务；提权的进程建的管道属主也是 Administrators）。
    /// </para>
    /// <para>
    /// 不靠降低模拟级别（<c>TokenImpersonationLevel.Identification</c>）来防：OpenSSH 的 agent 服务
    /// 要以连进来的用户身份保存密钥，降级会把正常的 agent 一起弄坏。
    /// </para>
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureTrustedPipeServer(NamedPipeClientStream pipe, string endpoint)
    {
        System.Security.Principal.SecurityIdentifier? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier))
                as System.Security.Principal.SecurityIdentifier;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or System.Security.Principal.IdentityNotMappedException)
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable,
                $"读不到 ssh-agent 命名管道（{endpoint}）的属主，不能确认另一头是可信的 agent：{ex.Message}", ex);
        }

        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!IsTrustedPipeOwner(owner, current.User))
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable,
                $"ssh-agent 命名管道（{endpoint}）的属主是 {owner?.Value ?? "（空）"}，" +
                "既不是当前用户也不是系统 —— 可能是别的用户抢先占了这个管道名。已拒绝连接。");
        }
    }

    /// <summary>管道属主可信吗（见 <see cref="EnsureTrustedPipeServer"/>）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsTrustedPipeOwner(
        System.Security.Principal.SecurityIdentifier? owner, System.Security.Principal.SecurityIdentifier? currentUser) =>
        owner is not null
        && (owner == currentUser
            || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid)
            || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid));

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
            throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 回了 {type} 而不是身份列表。");
        }

        uint count = reader.ReadUInt32();
        List<SshAgentIdentity> identities = [];

        for (uint i = 0; i < count && i < 1024; i++)
        {
            byte[] blob = reader.ReadStringAsArray(MaxMessageLength);
            string comment = reader.ReadUtf8String(MaxMessageLength);

            try
            {
                identities.Add(new SshAgentIdentity(SshPublicKey.Decode(blob), comment));
            }
            catch (SshPublicKeyException)
            {
                // agent 里可能有我们不认识类型的密钥（FIDO、DSA、厂商私有，或者本库不支持的证书类型）。
                // **跳过它就好** —— 为其中一把报错等于让整个 agent 用不了。
                //
                // ⚠️ 要接的是 SshPublicKeyException：Parse 把「不支持的类型」与
                //    「blob 格式非法」都包成它抛出来，不会让 SshWireFormatException 漏到这里。
                //    曾经只接了后者，结果 agent 里只要有一张证书，整个列表就抛异常 ——
                //    agent 认证、agent 转发、自动加钥一起用不了。
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

        // 证书的算法名带 -cert-v01 后缀，标志位按去掉后缀的名字定 —— 否则 RSA 证书会被签成 SHA-1。
        SshAgentSignFlags flags = SshPublicKey.StripCertificateSuffix(algorithm) switch
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
            throw new SshAgentException(SshFailureReason.AgentRefused,
                "ssh-agent 拒绝签名。常见原因：这把密钥已经不在 agent 里了，" +
                "或者 agent 配了确认（ssh-add -c）而使用者没有批准。");
        }

        if (type != SshAgentMessage.SignResponse)
        {
            throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 回了 {type} 而不是签名。");
        }

        return reader.ReadStringAsArray(MaxMessageLength);
    }

    /// <summary>把一把进程内私钥加进 agent（<c>ssh-add</c>）。</summary>
    /// <param name="key">要加的私钥。只接受进程内私钥 —— 背后是 agent / 硬件的签名器手里根本没有私钥。</param>
    /// <param name="comment">注释，<c>ssh-add -l</c> 显示的那一列，通常写私钥文件路径。</param>
    /// <param name="constraints">约束；<see langword="null"/> 或全空表示不带约束。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshAgentException">agent 拒绝了，或者通信失败。</exception>
    /// <remarks>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §7.3〕<b>库从不自动调用它。</b>
    /// 往使用者的 agent 里放东西是使用者的决定。
    /// </para>
    /// <para>
    /// 不查重：同一把钥加两次由 agent 处理（OpenSSH 会更新注释与约束）。
    /// 加进去的钥活多久由 agent 决定 —— Windows 的 OpenSSH agent 会把它存进注册表，重启后仍在。
    /// </para>
    /// </remarks>
    public async ValueTask AddIdentityAsync(
        InMemorySshSigner key,
        string comment,
        SshAgentKeyConstraints? constraints = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(comment);

        bool constrained = constraints is { IsEmpty: false };

        // 报文里是明文私钥。按上限一次性预留，免得扩容在堆上留下没清零的旧副本；
        // 用完整块清零（spec/07 §7.3 决策 3）。
        byte[] buffer = new byte[MaxMessageLength];
        try
        {
            FixedBufferWriter output = new(buffer);
            SshDataWriter writer = new(output);

            // 没有约束就发 17：有的 agent 认 17 却不认 25（决策 2）。
            writer.WriteByte(constrained ? SshAgentMessage.AddIdentityConstrained : SshAgentMessage.AddIdentity);
            key.WriteAgentPrivateKey(output);
            writer.WriteUtf8String(comment);

            if (constrained)
            {
                if (constraints!.Lifetime is { } lifetime)
                {
                    writer.WriteByte(SshAgentMessage.ConstrainLifetime);
                    writer.WriteUInt32((uint)Math.Clamp(Math.Ceiling(lifetime.TotalSeconds), 1, uint.MaxValue));
                }
                if (constraints.ConfirmEachUse)
                {
                    writer.WriteByte(SshAgentMessage.ConstrainConfirm);
                }
            }

            byte[] response = await ExchangeAsync(
                buffer.AsMemory(0, output.WrittenCount), cancellationToken).ConfigureAwait(false);

            if (response[0] == SshAgentMessage.Success)
            {
                return;
            }

            if (response[0] == SshAgentMessage.Failure)
            {
                // agent 不说原因 —— 点出最常见的三种。
                throw new SshAgentException(SshFailureReason.AgentRefused,
                    "ssh-agent 拒绝加入这把密钥。常见原因：agent 不支持约束（有效期 / 逐次确认）、" +
                    "agent 已被锁定（ssh-add -x），或者 agent 不支持这种密钥类型。");
            }

            throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 回了 {response[0]} 而不是成功 / 失败。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
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
                throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 报文长度不合理：{length}。");
            }

            byte[] response = new byte[length];
            await _stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (EndOfStreamException ex)
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable, "ssh-agent 在应答之前就断开了。", ex);
        }
        catch (IOException ex)
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable, $"与 ssh-agent 通信失败：{ex.Message}", ex);
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

    /// <summary>写进一块固定缓冲、写满即抛的写入器。</summary>
    /// <remarks>
    /// 加钥报文装的是明文私钥：<see cref="ArrayBufferWriter{T}"/> 扩容时会把旧数组
    /// 原样丢给 GC，那里面的私钥没人清零。这里宁可报错也不扩容。
    /// </remarks>
    private sealed class FixedBufferWriter(byte[] buffer) : IBufferWriter<byte>
    {
        public int WrittenCount { get; private set; }

        public void Advance(int count) => WrittenCount += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => buffer.AsMemory(Reserve(sizeHint));

        public Span<byte> GetSpan(int sizeHint = 0) => buffer.AsSpan(Reserve(sizeHint));

        private int Reserve(int sizeHint)
        {
            if (buffer.Length - WrittenCount < Math.Max(sizeHint, 1))
            {
                throw new SshAgentException(SshFailureReason.LimitExceeded, "要加入 agent 的密钥太大，超出了 agent 报文的长度上限。");
            }
            return WrittenCount;
        }
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
