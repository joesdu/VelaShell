// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.4  CHANNEL_REQUEST / SUCCESS / FAILURE —— 应答没有 id,按发送顺序对齐
//   RFC 4254 §6.9  signal(want_reply 为假)
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.1

using System.Buffers;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>一条通道上本端发出的通道请求，以及等应答的那本 FIFO 账本。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshChannel"/> 拆出来的协作者。要应答的请求<b>登记与入队是同一个动作</b>：登记放在交给会话的放行判定里，
/// 由会话在入队锁里与入队同一时刻执行（<see cref="ISshChannelHost.SendIfAsync"/>）—— 不拆成「先查后发」，也不变成异步。
/// </para>
/// <para>
/// 关账由通道在收尾时做（<c>SshChannel.FinishClose</c>）；之后到的迟到应答照单吸收（见 <see cref="OnReply"/>）。
/// </para>
/// </remarks>
internal sealed class ChannelRequestLedger(SshChannel channel)
{
    /// <summary>
    /// 等待应答的通道请求。
    /// </summary>
    /// <remarks>
    /// <b>通道请求的应答没有 id，靠 FIFO 顺序对齐</b>（velashell-docs/zh/ssh/spec/05 §5.1）——
    /// 所以这里是队列而不是字典。用字典就需要一个 id，而协议根本没给。
    /// </remarks>
    private readonly Session.FifoRequestLedger<bool> _pending = new();

    /// <summary>发一个通道请求（见 <see cref="SshChannel.SendRequestAsync"/>）。</summary>
    public async ValueTask<bool> SendAsync(
        string requestType, ReadOnlyMemory<byte> payload, bool wantReply, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelRequest);
        writer.WriteUInt32(channel.RemoteId);
        writer.WriteUtf8String(requestType);
        writer.WriteBoolean(wantReply);
        writer.WriteRaw(payload.Span);

        // ⚠️ 关了之后不许再发（RFC 4254 §5.3）。对端双向 CLOSE 走完之后就可以复用它的通道号，
        //    一个迟到的 window-change / signal 会落到**另一个会话**上；要应答的请求还会把
        //    那条新通道的应答队列搅乱。
        if (!wantReply)
        {
            bool sent = false;
            await channel.Host.SendIfAsync(buffer.WrittenMemory, () => sent = channel.MayStillSend(), channel.IsInteractive, cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }

        // 登记与入队是**同一个动作**：应答靠 FIFO 对齐。
        // 先发后登记，一个快到的应答会发现账本是空的；先登记后发（中间隔着背压等待），
        // 并发的两个请求可能登记顺序与上线顺序相反，而那会把应答安到对方头上。
        Task<bool>? reply = null;
        await channel.Host.SendIfAsync(
            buffer.WrittenMemory,
            () =>
            {
                // 账本已经关了（通道在本端收尾了）也不发：发出去的话对端的应答回来时没人认领。
                if (!channel.MayStillSend())
                {
                    return false;
                }
                reply = _pending.TryRegister();
                return reply is not null;
            },
            channel.IsInteractive, cancellationToken).ConfigureAwait(false);

        return reply is not null && await reply.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>给远端进程发信号（见 <see cref="SshChannel.SendSignalAsync"/>）。</summary>
    public async ValueTask SendSignalAsync(string signalName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalName);

        if (signalName.StartsWith("SIG", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"信号名不带 SIG 前缀：用 \"{signalName[3..]}\" 而不是 \"{signalName}\"（RFC 4254 §6.9）。",
                nameof(signalName));
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(signalName);

        // RFC 4254 §6.9 明确要求 want_reply 为假。
        await channel.SendRequestAsync(
            SshProtocolNames.RequestSignal, buffer.WrittenMemory, wantReply: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>收到通道请求的应答。</summary>
    /// <returns><see langword="false"/> 说明 FIFO 失步了：账本还开着，却没有人在等。</returns>
    /// <remarks>
    /// <para>
    /// 账本已经关了（本端收尾过这条通道）时，迟到的应答照单吸收，不算失步。
    /// 典型的时序：exec 已经发出，调用方等应答时取消了 → 释放通道，CLOSE 发出、账本关掉、
    /// 通道号还扣着等对端的 CLOSE → 对端按顺序先回 exec 的应答，再回 CLOSE。
    /// 那个应答是合法的在途报文（RFC 4254 §5.3；velashell-docs/zh/ssh/spec/05 §1 规则 4），
    /// 在途的请求在关账时已经以「没成」结算过了。
    /// </para>
    /// <para>
    /// 曾经它被判成失步，整条连接以 PROTOCOL_ERROR 断开 —— 同一连接上的终端、SFTP、隧道一起陪葬，
    /// 而起因只是一个带超时的探测命令。
    /// </para>
    /// </remarks>
    public bool OnReply(bool success) =>
        _pending.TryComplete(success) || _pending.IsClosed;

    /// <summary>关账：还在等应答的请求不会再有应答了，一律以「没成」结算。</summary>
    /// <remarks>让它们返回 false 而不是永远挂着 —— 挂死没有堆栈也没有日志，只有一个再也不返回的 await。</remarks>
    public void Close() => _pending.Close(false);
}
