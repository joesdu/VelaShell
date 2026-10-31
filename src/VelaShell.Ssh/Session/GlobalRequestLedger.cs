// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §4    GLOBAL_REQUEST / REQUEST_SUCCESS / REQUEST_FAILURE —— 应答没有 id,按发送顺序对齐
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §6

using System.Buffers;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

/// <summary>一条连接上的全局请求：本端发出的（含保活探测）与它们的 FIFO 应答账本，以及对端发来的全局请求。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshConnection"/> 拆出来的协作者。<b>登记与入队是同一个动作</b>：登记放在交给连接的回调里，
/// 由连接在入队锁里与入队同一时刻执行（<c>SshConnection.SendAsync(packet, onEnqueued, …)</c> /
/// <c>SshConnection.PostRegistered</c>）—— 不拆成「先登记后发」，也不变成异步。
/// </para>
/// <para>
/// 对端的全局请求与应答在接收循环上处理（<see cref="OnPeerRequest"/>、<see cref="OnReplyAsync"/>）：只投递，不等发送。
/// </para>
/// </remarks>
internal sealed class GlobalRequestLedger(SshConnection connection)
{
    /// <summary>全局请求的应答账本。<b>同样没有 id，靠 FIFO 对齐。</b></summary>
    private readonly FifoRequestLedger<SshGlobalRequestReply> _pending = new();

    /// <summary>发一个全局请求，把应答（含载荷）带回来（见 <see cref="SshConnection.SendGlobalRequestAsync(string, ReadOnlyMemory{byte}, bool, CancellationToken)"/>）。</summary>
    /// <returns><paramref name="wantReply"/> 为假时立刻返回一个成功的空应答。</returns>
    public async ValueTask<SshGlobalRequestReply> SendAsync(
        string requestType,
        ReadOnlyMemory<byte> payload,
        bool wantReply,
        CancellationToken cancellationToken)
    {
        if (!wantReply)
        {
            await connection.SendAsync(Encode(requestType, payload, wantReply: false), cancellationToken)
                .ConfigureAwait(false);
            return new SshGlobalRequestReply(true, ReadOnlyMemory<byte>.Empty);
        }

        return await SendAsync(requestType, payload, onReply: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>发一个要应答的全局请求；应答一到，先在接收循环上同步调用 <paramref name="onReply"/>。</summary>
    public async ValueTask<SshGlobalRequestReply> SendAsync(
        string requestType,
        ReadOnlyMemory<byte> payload,
        Action<SshGlobalRequestReply>? onReply,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> packet = Encode(requestType, payload, wantReply: true);

        // 登记与入队是同一个动作：应答靠 FIFO 对齐（见 SshConnection.SendAsync 的重载说明）。
        Task<SshGlobalRequestReply>? reply = null;
        await connection.SendAsync(packet, () => reply = _pending.Register(onReply), cancellationToken)
            .ConfigureAwait(false);
        return await reply!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> Encode(
        string requestType, ReadOnlyMemory<byte> payload, bool wantReply)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.GlobalRequest);
        writer.WriteUtf8String(requestType);
        writer.WriteBoolean(wantReply);
        writer.WriteRaw(payload.Span);
        return buffer.WrittenMemory;
    }

    /// <summary>发一次保活探测，在 <paramref name="timeout"/> 之内等应答。</summary>
    /// <returns>期限内有应答（成功或失败都算）为 <see langword="true"/>。</returns>
    /// <remarks>
    /// ⚠️ <b>期限从入队那一刻算起，罩住「发出去」与「等应答」两段。</b>
    /// 死链的典型样子是对端不再读、发送泵卡在一次写上、出站积压顶满背压：
    /// 这时探测若在背压上等、或等自己刷上线之后才开始计时，它就跟着一起卡住，
    /// <c>missed</c> 永远不加一 —— 恰恰在最需要判死的时候判不了。
    /// </remarks>
    public async ValueTask<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> packet = Encode(
            SshProtocolNames.KeepAliveOpenSsh, default, wantReply: true);

        // 登记与入队是同一个动作：应答靠 FIFO 对齐（见 SshConnection.SendAsync 的重载说明）。
        long startedAt = connection.Time.GetTimestamp();
        Task<SshGlobalRequestReply>? reply = null;
        if (!connection.PostRegistered(packet, () => reply = _pending.Register()))
        {
            return false;
        }

        try
        {
            await reply!.WaitAsync(timeout, connection.Time, cancellationToken).ConfigureAwait(false);
            if (connection.IsFaulted)
            {
                return false;
            }
            connection.RecordRoundTrip(connection.Time.GetElapsedTime(startedAt));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>对端发来的全局请求：认得的（主机密钥宣告）交给连接，要应答的一律回 <c>REQUEST_FAILURE</c>。</summary>
    /// <remarks>在接收循环上调用；只投递，不等发送。</remarks>
    public void OnPeerRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.GlobalRequest);
        string requestType = reader.ReadUtf8String(SshConnection.MaxFieldBytes);
        bool wantReply = reader.ReadBoolean();

        if (requestType == SshProtocolNames.RequestHostKeys)
        {
            connection.OnHostKeysAnnounced(ref reader);
        }

        if (!wantReply)
        {
            return;
        }

        // 〔重要 velashell-docs/zh/ssh/spec/05 §6.4〕对未知全局请求回 REQUEST_FAILURE 是**必须**的，
        // 不能沉默。沉默会让对端的 FIFO 队列永远错位 ——
        // 它下一个请求的应答会被认成这一个的。
        byte[] failure = [(byte)SshMessageNumber.RequestFailure];
        connection.Post(failure);
    }

    /// <summary>收到 <c>REQUEST_SUCCESS</c> / <c>REQUEST_FAILURE</c>：交给最早的那个请求；没人在等就是 FIFO 失步，判死。</summary>
    public async ValueTask OnReplyAsync(
        bool success, ReadOnlyMemory<byte> replyPayload, CancellationToken cancellationToken)
    {
        if (!_pending.TryComplete(new SshGlobalRequestReply(success, replyPayload)))
        {
            await connection.FaultProtocolAsync(
                "收到了没有对应全局请求的应答 —— FIFO 已经失步。", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>关账：在途的与之后登记的请求一律以「没成」收尾（连接判死或释放时）。</summary>
    public void Close() => _pending.Close(new SshGlobalRequestReply(false, ReadOnlyMemory<byte>.Empty));
}
