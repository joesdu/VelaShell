// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL        auth-agent-req@openssh.com / auth-agent@openssh.com
//   OpenSSH PROTOCOL.agent  agent 协议
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §七

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Forwarding;

/// <summary>远端请求用某把密钥签名时，交给使用者定夺。</summary>
/// <param name="Key">远端想用哪把钥。</param>
/// <param name="Comment">这把钥在 agent 里的注释（通常是私钥文件路径）。</param>
public readonly record struct AgentSignatureRequest(SshPublicKey Key, string Comment);

/// <summary>agent 转发的安全策略。</summary>
/// <remarks>
/// <para>
/// ⚠️ <b>agent 转发是一把上膛的枪。</b>
/// 转发期间，远端主机上的 root 可以用你的私钥<b>签任何东西</b> ——
/// 包括拿你的身份去登录别的机器。私钥本身不会离开本机，
/// 但「用私钥做事的能力」离开了。
/// </para>
/// <para>
/// 所以这里的默认值全部朝着「更少暴露」那一侧。
/// </para>
/// </remarks>
public sealed record AgentForwardPolicy
{
    /// <summary>
    /// 只转发这些公钥；<b>空列表表示把整个 agent 暴露出去</b>。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §7.2〕<b>必须支持「只转发指定的密钥」。</b>
    /// 一台跳板机没有理由能用到你所有的密钥 —— 它只需要下一跳那一把。
    /// </remarks>
    public IReadOnlyList<SshPublicKey> AllowedKeys { get; init; } = [];

    /// <summary>
    /// 每次远端请求签名时问一次使用者。
    /// </summary>
    /// <remarks>
    /// 返回 <see langword="false"/> 就拒签。对跳板场景这是唯一能让人安心的做法 ——
    /// 否则你根本不知道那台机器拿你的身份做了什么、做了几次。
    /// </remarks>
    public Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>>? ConfirmEachSignature { get; init; }

    /// <summary>同时允许几条 agent 通道。</summary>
    public int MaxConcurrentChannels { get; init; } = 8;

    /// <summary>默认策略：不限密钥、不逐次确认。</summary>
    /// <remarks>
    /// 它**只在使用者已经显式打开 agent 转发之后**才生效 ——
    /// 转发本身默认是关的。
    /// </remarks>
    public static AgentForwardPolicy Default { get; } = new();
}

/// <summary>把远端的 agent 请求桥到本机 ssh-agent。</summary>
/// <remarks>
/// <para>
/// 机制是三步：
/// </para>
/// <list type="number">
///   <item>在 <b>session 通道</b>上发 <c>auth-agent-req@openssh.com</c>。</item>
///   <item>服务端随后可以发起 <c>auth-agent@openssh.com</c> 通道。</item>
///   <item>我们把那条通道上的字节流桥到本机 agent。</item>
/// </list>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §7.2〕<b>我们只做转发，不做 agent 服务端。</b>
/// 本机 agent 由操作系统或别的软件提供。
/// </para>
/// </remarks>
public sealed class AgentForwarder : IIncomingChannelHandler, IAsyncDisposable
{
    private const int MaxAgentMessageLength = 256 * 1024;

    /// <summary>agent 通道的接收窗口：一整条最长报文（4 字节长度 + 内容）。</summary>
    internal const int AgentChannelWindowBytes = 4 + MaxAgentMessageLength;

    private readonly Session.SshConnection _connection;
    private readonly Func<CancellationToken, ValueTask<SshAgentClient>> _connectAgent;
    private readonly AgentForwardPolicy _policy;
    private readonly SemaphoreSlim _slots;
    private bool _disposed;

    /// <summary>转发器自己的生命周期：释放时取消，已经打开的 agent 通道随之断开。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private AgentForwarder(
        Session.SshConnection connection,
        Func<CancellationToken, ValueTask<SshAgentClient>> connectAgent,
        AgentForwardPolicy policy)
    {
        _connection = connection;
        _connectAgent = connectAgent;
        _policy = policy;
        _slots = new SemaphoreSlim(policy.MaxConcurrentChannels, policy.MaxConcurrentChannels);
    }

    /// <summary>远端请求过几次签名。</summary>
    public int SignatureRequests { get; private set; }

    /// <summary>有几次因为策略被拒。</summary>
    public int SignaturesDenied { get; private set; }

    /// <summary>远端列过几次身份。</summary>
    public int IdentityListings { get; private set; }

    /// <summary>被隐藏掉的密钥数（不在 <c>AllowedKeys</c> 里的）。</summary>
    public int KeysHidden { get; private set; }

    /// <summary>在一条 session 通道上请求 agent 转发。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="channel">要在哪条 session 通道上请求。</param>
    /// <param name="policy">安全策略。</param>
    /// <param name="agentEndpoint">本机 agent 端点；<see langword="null"/> 用默认。</param>
    /// <param name="connectAgent">
    /// 自己去连 agent。给了它就忽略 <paramref name="agentEndpoint"/> ——
    /// agent 可能在一条隧道的另一头，或者由别的软件以自定义方式提供。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshForwardException">服务端拒绝了转发请求。</exception>
    /// <remarks>
    /// <b>这个方法必须由使用者显式调用</b> —— agent 转发默认是关的，
    /// 而且没有「全局打开」的开关：它是逐连接、逐通道的决定。
    /// </remarks>
    public static async ValueTask<AgentForwarder> RequestAsync(
        Session.SshConnection connection,
        SshChannel channel,
        AgentForwardPolicy? policy = null,
        string? agentEndpoint = null,
        Func<CancellationToken, ValueTask<SshAgentClient>>? connectAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(channel);

        AgentForwarder forwarder = new(
            connection,
            connectAgent ?? (ct => SshAgentClient.ConnectAsync(agentEndpoint, ct)),
            policy ?? AgentForwardPolicy.Default);

        // 先登记处理器再发请求：服务端可能在应答之后立刻就发起通道，
        // 那时处理器必须已经在位，否则第一条会被拒。
        //
        // 用 Add 而不是 Set：同一条连接上几个会话都开了 agent 转发时，
        // 各挂各的，释放一个不会把别人的摘掉。
        connection.AddIncomingChannelHandler(SshAlgorithmNames.ChannelAuthAgent, forwarder);

        try
        {
            bool accepted = await channel.SendRequestAsync(
                SshAlgorithmNames.RequestAuthAgent, default, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshForwardException(
                    "服务端拒绝了 agent 转发请求。常见原因是 sshd_config 里 AllowAgentForwarding no。");
            }

            return forwarder;
        }
        catch (Exception)
        {
            connection.RemoveIncomingChannelHandler(SshAlgorithmNames.ChannelAuthAgent, forwarder);
            throw;
        }
    }

    // ------------------------------------------------------------ 入站通道

    /// <inheritdoc />
    public ValueTask<SshChannelOptions> GetOptionsAsync(
        string channelType, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_slots.Wait(0, CancellationToken.None))
        {
            throw new SshForwardException(
                $"同时打开的 agent 通道已达上限 {_policy.MaxConcurrentChannels}。");
        }

        // ⚠️ 窗口至少要装得下**一整条**最长的 agent 报文（长度前缀 + 256 KiB）。
        //    报文收齐之前读的一方一个字节都不消费（见 ReadAgentMessageAsync），而窗口只随消费回补：
        //    窗口比报文小，就是对端等窗口、我们等报文，谁也动不了。曾经给的是 32 KiB，
        //    签一段稍长的数据（ssh-keygen -Y sign、证书）就卡死在那里。
        //    窗口只是额度，不是预先分配的内存；平常的报文只有几百字节。
        return ValueTask.FromResult(SshChannelOptions.Default with
        {
            WindowPolicy = SshWindowPolicy.Fixed(AgentChannelWindowBytes),
            StderrPolicy = SshStderrPolicy.Discard,
        });
    }

    /// <inheritdoc />
    /// <remarks>还回 <see cref="GetOptionsAsync"/> 占的并发槽位。</remarks>
    public void OnOpenAborted(string channelType, ReadOnlyMemory<byte> typeSpecificPayload) => _slots.Release();

    /// <inheritdoc />
    public async Task HandleAsync(
        SshChannel channel, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        try
        {
            // ⚠️ 连上转发器自己的生命周期，不只是连接的。传进来的令牌属于整条连接 ——
            //    只看它的话，释放转发器（比如关掉开了 agent 转发的那个 shell）之后，
            //    已经打开的 agent 通道照样逐条转发签名，远端主机上的 root 可以一直拿着它用，
            //    直到整条连接断开（而连接上可能还开着 SFTP 或别的会话）。
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

            await using SshAgentClient agent =
                await _connectAgent(linked.Token).ConfigureAwait(false);

            await BridgeAsync(channel, agent, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
        catch (SshException)
        {
            // 连不上本机 agent，或者远端发了畸形请求。
            // **单条通道的失败不影响会话** —— 关掉它就好。
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>把远端的 agent 请求逐条转给本机 agent。</summary>
    /// <remarks>
    /// <b>不是字节级的直通。</b>我们要把每一条请求解出来看一眼 ——
    /// 只有这样才能实现「只转发指定的密钥」与「逐次签名确认」。
    /// 直通管子省事，但那两条安全约束就无从谈起。
    /// </remarks>
    private async Task BridgeAsync(
        SshChannel channel, SshAgentClient agent, CancellationToken cancellationToken)
    {
        PipeReader input = channel.StandardOutput;
        PipeWriter output = channel.StandardInput;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? request = await ReadAgentMessageAsync(input, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;   // 远端关了
            }

            // ⚠️ 再看一眼源头。释放是把 _lifetime 放到线程池上取消的：它自己的 IsCancellationRequested
            //    当场变真，链接出来的 cancellationToken 却要等回调跑完才变 —— 恰好夹在中间到的一条请求
            //    会被读出来，释放都返回了还替远端签一次名。
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            byte[] response = await HandleAgentRequestAsync(request, agent, cancellationToken)
                .ConfigureAwait(false);

            byte[] framed = new byte[4 + response.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)response.Length);
            response.CopyTo(framed.AsSpan(4));

            await output.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]> HandleAgentRequestAsync(
        byte[] request, SshAgentClient agent, CancellationToken cancellationToken)
    {
        if (request.Length == 0)
        {
            return [SshAgentWire.Failure];
        }

        return request[0] switch
        {
            SshAgentWire.RequestIdentities =>
                await HandleListAsync(agent, cancellationToken).ConfigureAwait(false),
            SshAgentWire.SignRequest =>
                await HandleSignAsync(request, agent, cancellationToken).ConfigureAwait(false),

            // 增删密钥、锁定 agent 之类的请求**一律不转发**。
            // 远端没有任何理由改动我们本机 agent 的状态。
            _ => [SshAgentWire.Failure],
        };
    }

    private async ValueTask<byte[]> HandleListAsync(SshAgentClient agent, CancellationToken cancellationToken)
    {
        IdentityListings++;

        IReadOnlyList<SshAgentIdentity> all =
            await agent.ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

        List<SshAgentIdentity> visible = [];
        foreach (SshAgentIdentity identity in all)
        {
            if (IsAllowed(identity.PublicKey))
            {
                visible.Add(identity);
            }
            else
            {
                KeysHidden++;
            }
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteByte(SshAgentWire.IdentitiesAnswer);
        writer.WriteUInt32((uint)visible.Count);

        foreach (SshAgentIdentity identity in visible)
        {
            writer.WriteString(identity.PublicKey.Blob.Span);
            writer.WriteUtf8String(identity.Comment);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private async ValueTask<byte[]> HandleSignAsync(
        byte[] request, SshAgentClient agent, CancellationToken cancellationToken)
    {
        SignatureRequests++;

        SshDataReader reader = new(new ReadOnlySequence<byte>(request));
        reader.ReadByte();

        byte[] keyBlob;
        byte[] data;
        uint flags;

        try
        {
            keyBlob = reader.ReadStringAsArray(MaxAgentMessageLength);
            data = reader.ReadStringAsArray(MaxAgentMessageLength);
            flags = reader.ReadUInt32();
        }
        catch (SshWireFormatException)
        {
            return [SshAgentWire.Failure];
        }

        SshPublicKey key;
        try
        {
            key = SshPublicKey.Parse(keyBlob);
        }
        catch (Exception)
        {
            return [SshAgentWire.Failure];
        }

        if (!IsAllowed(key))
        {
            SignaturesDenied++;
            return [SshAgentWire.Failure];
        }

        if (_policy.ConfirmEachSignature is { } confirm)
        {
            IReadOnlyList<SshAgentIdentity> identities =
                await agent.ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

            // 注释是给使用者看的（通常是私钥文件路径）——
            // 弹窗里只说「有人要用某把 ed25519 签名」没什么用。
            string comment = "";
            foreach (SshAgentIdentity candidate in identities)
            {
                if (candidate.PublicKey.Blob.Span.SequenceEqual(keyBlob))
                {
                    comment = candidate.Comment;
                    break;
                }
            }

            bool approved = await confirm(new AgentSignatureRequest(key, comment), cancellationToken)
                .ConfigureAwait(false);

            if (!approved)
            {
                SignaturesDenied++;
                return [SshAgentWire.Failure];
            }
        }

        // 标志位决定 RSA 用哪种摘要。把它翻回算法名交给 agent 客户端。
        string algorithm = ChooseAlgorithm(key, flags);

        try
        {
            byte[] signature = await agent
                .SignAsync(keyBlob, data, algorithm, cancellationToken).ConfigureAwait(false);

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteByte(SshAgentWire.SignResponse);
            writer.WriteString(signature);
            return buffer.WrittenSpan.ToArray();
        }
        catch (SshAgentException)
        {
            return [SshAgentWire.Failure];
        }
    }

    /// <remarks>
    /// 按 <see cref="SshPublicKey.PlainKeyType"/> 判断：RSA 证书的类型串是 <c>ssh-rsa-cert-v01@openssh.com</c>，
    /// 按 <c>KeyType</c> 比的话远端要的 SHA-2 标志位被忽略，证书就被签成 SHA-1。
    /// 返回的是普通签名算法名 —— 签名 blob 里写的本来就是它（OpenSSH PROTOCOL.certkeys）。
    /// </remarks>
    private static string ChooseAlgorithm(SshPublicKey key, uint flags)
    {
        if (key.PlainKeyType != SshAlgorithmNames.SshRsa)
        {
            return key.PlainKeyType;
        }

        return (flags & SshAgentWire.FlagRsaSha512) != 0
            ? SshAlgorithmNames.RsaSha512
            : (flags & SshAgentWire.FlagRsaSha256) != 0
                ? SshAlgorithmNames.RsaSha256
                : SshAlgorithmNames.SshRsa;
    }

    private bool IsAllowed(SshPublicKey key)
    {
        if (_policy.AllowedKeys.Count == 0)
        {
            return true;
        }

        // 按证书里那把钥比：证书用的是同一把私钥，放行了钥就等于放行了它的证书（反过来也一样）。
        foreach (SshPublicKey allowed in _policy.AllowedKeys)
        {
            if (allowed.PlainKey.Blob.Span.SequenceEqual(key.PlainKey.Blob.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>读一条 agent 报文（4 字节长度 + 内容）。</summary>
    private static async ValueTask<byte[]?> ReadAgentMessageAsync(
        PipeReader input, CancellationToken cancellationToken)
    {
        // 缓冲在循环外面：循环里 stackalloc 会把栈一点点吃光。
        byte[] lengthBytes = new byte[4];

        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.Length >= 4)
            {
                buffer.Slice(0, 4).CopyTo(lengthBytes);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);

                if (length is 0 or > MaxAgentMessageLength)
                {
                    input.AdvanceTo(buffer.End);
                    return null;   // 畸形 —— 关掉这条通道
                }

                if (buffer.Length >= 4 + length)
                {
                    byte[] message = buffer.Slice(4, length).ToArray();
                    input.AdvanceTo(buffer.GetPosition(4 + length));
                    return message;
                }
            }

            if (read.IsCompleted)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                return null;
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;

        _connection.RemoveIncomingChannelHandler(SshAlgorithmNames.ChannelAuthAgent, this);

        // 已经打开的 agent 通道一起断：它们的转发循环挂在 _lifetime 上，退出后连接会关掉通道。
        // 回调放到线程池上（与会话收尾同一条规矩）。令牌源不释放 —— 还在收尾的循环要读它。
        Session.Lifecycle.CancelInBackground(_lifetime);

        // 不 Dispose 信号量：还在跑的 agent 通道收尾时要 Release 它。
        // 它没有用到等待句柄，不释放也不漏任何非托管资源。
        return ValueTask.CompletedTask;
    }
}

/// <summary>agent 协议的线上常量。</summary>
internal static class SshAgentWire
{
    public const byte Failure = 5;
    public const byte RequestIdentities = 11;
    public const byte IdentitiesAnswer = 12;
    public const byte SignRequest = 13;
    public const byte SignResponse = 14;

    public const uint FlagRsaSha256 = 0x02;
    public const uint FlagRsaSha512 = 0x04;
}
