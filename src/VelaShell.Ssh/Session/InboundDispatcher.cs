// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §11   DISCONNECT / IGNORE / UNIMPLEMENTED / DEBUG
//   RFC 4254 §4    GLOBAL_REQUEST 的应答
//   RFC 4254 §5    通道的开、关、数据、请求
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§6、§8;velashell-docs/zh/ssh/spec/08-failures.md §六

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>连接的接收循环与收包分发：读一个报文、交给对的那一方，接着读。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshConnection"/> 拆出来的协作者。<b>接收循环绝不因为某一条通道而停下</b>：要回的报文一律投递
/// （<c>SshConnection.Post</c> / <c>SshConnection.PostIf</c>），发送泵按投递顺序把它们送出去，这里接着读。
/// </para>
/// <para>
/// <see cref="DispatchAsync"/> 会被<b>重入</b>：重协商期间密钥交换跑在接收循环上，它读到的会话层报文
/// 经 <c>SshConnection.RekeyKexTransport.ReadPacketAsync</c> 就地回到这里派发（闸门只管发送方向）。
/// 所以这里的每一条路径都只许投递、不许等发送 —— 在 KEX 中等发送，就是接收循环等它自己。
/// </para>
/// </remarks>
internal sealed class InboundDispatcher(
    SshConnection connection,
    SshPacketTransport transport,
    ChannelTable channels,
    GlobalRequestLedger globalRequests)
{
    /// <summary>接收循环：读到对端关闭、出错、或者连接收工为止；收尾时每条通道跟着收尾。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SshInboundPacket packet = await transport.ReadPacketAsync(cancellationToken).ConfigureAwait(false);

                // 任何入站报文都证明链路活着 —— 不只是保活应答。
                connection.NoteInbound();

                if (packet.IsEndOfStream)
                {
                    connection.Fault(new SshConnectionClosedException(
                        SshFailureReason.ClosedByPeer, SshPhase.Open, "对端关闭了连接。"));
                    return;
                }

                await DispatchAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 我们自己要收工（释放，或者 Fault 已经把会话判死）。
        }
        catch (Exception ex)
        {
            // 〔velashell-docs/zh/ssh/spec/08 §六〕对端发来的东西解不开、违反了协议、重协商时验不过主机密钥：
            // 先尽力告诉它为什么断（RFC 4253 §11.1），再判死。曾经一声不吭就断，服务端日志里只有「Connection closed」。
            // 套接字断了、对端在报文中途走了的，发了也没人收（DisconnectReasonFor 给 null）。
            if (!connection.IsFaulted && ConnectionFaults.DisconnectReasonFor(ex) is { } reason)
            {
                await connection.TrySendDisconnectAsync(reason, ConnectionFaults.DisconnectDescription(reason, ex), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            connection.Fault(ex);
        }
        finally
        {
            // _lifetime 只有 Fault 与释放会取消，而 Fault 在取消之前就记下了原因 ——
            // 没有故障就只能是本端释放的。
            channels.CloseAll(connection.CurrentFault ?? SshConnection.DisposedReason());
        }
    }

    /// <remarks>
    /// ⚠️ <b>这里的每一条路径都不许等发送。</b>要回的报文一律投递（<c>SshConnection.Post</c>）：
    /// 发送泵按投递顺序把它们送出去，而接收循环接着读。
    /// 在这里等发送，TCP 发送缓冲一满，接收循环就停了 —— 而对端正等着我们读，
    /// 才肯去读我们发的东西。
    /// </remarks>
    public async ValueTask DispatchAsync(SshInboundPacket packet, CancellationToken cancellationToken)
    {
        switch (packet.MessageNumber)
        {
            // 对端要重协商。必须应答 —— 不应答的表现是「会话忽然断掉」。
            case SshMessageNumber.KexInit:
                await connection.OnPeerKexInitAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelData:
                await OnChannelDataAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelExtendedData:
                await OnChannelExtendedDataAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelWindowAdjust:
                await OnChannelWindowAdjustAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelOpenConfirmation:
                channels.OnOpenConfirmation(packet.Payload);
                return;

            case SshMessageNumber.ChannelOpenFailure:
                channels.OnOpenFailure(packet.Payload);
                return;

            case SshMessageNumber.ChannelEof:
                channels.Lookup(ReadRecipient(packet.Payload))?.OnEof();
                return;

            case SshMessageNumber.ChannelClose:
                OnChannelClose(packet.Payload);
                return;

            case SshMessageNumber.ChannelRequest:
                OnChannelRequest(packet.Payload);
                return;

            case SshMessageNumber.ChannelSuccess:
            case SshMessageNumber.ChannelFailure:
                await OnChannelRequestReplyAsync(
                    packet.Payload, packet.MessageNumber == SshMessageNumber.ChannelSuccess, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case SshMessageNumber.GlobalRequest:
                globalRequests.OnPeerRequest(packet.Payload);
                return;

            case SshMessageNumber.RequestSuccess:
            case SshMessageNumber.RequestFailure:
                await globalRequests.OnReplyAsync(
                    packet.MessageNumber == SshMessageNumber.RequestSuccess,
                    packet.Payload.Length > 1 ? packet.Payload[1..].ToArray() : ReadOnlyMemory<byte>.Empty,
                    cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelOpen:
                await channels.OnPeerChannelOpenAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            // 这几种在任何阶段都可能出现，而且都不需要回应：
            //   · IGNORE / DEBUG —— RFC 4253 §11.2、§11.3；
            //   · UNIMPLEMENTED —— 对 UNIMPLEMENTED 再回 UNIMPLEMENTED 只会让两边互相回声；
            //   · EXT_INFO —— RFC 8308 §2.4 允许服务端在认证成功后再发一次。
            case SshMessageNumber.Ignore:
            case SshMessageNumber.Unimplemented:
                return;

            case SshMessageNumber.Debug:
                OnDebugMessage(packet.Payload);
                return;

            // RFC 8308 §2.4 允许服务端在认证成功后再发一次（OpenSSH 在这一次里宣告 ping@openssh.com 之类）。
            case SshMessageNumber.ExtInfo:
                connection.OnExtensionInfo(packet.Payload);
                return;

            // 〔velashell-docs/zh/ssh/spec/05 §6.5〕对端的 PING 原样回 PONG；我们的 PING 的 PONG 交给等它的那一次测量。
            case SshMessageNumber.Ping:
                connection.OnPing(packet.Payload);
                return;

            case SshMessageNumber.Pong:
                connection.OnPong(packet.Payload);
                return;

            case SshMessageNumber.Disconnect:
                connection.Fault(ReadDisconnect(packet.Payload));
                return;

            default:
                // 未知报文编号：回 UNIMPLEMENTED，不断开（RFC 4253 §11.4）。
                PostUnimplemented();
                return;
        }
    }

    /// <summary>连上之后的调试消息：在线程池上交给回调，不挡接收循环；回调抛的丢掉，格式不对的照 RFC 忽略。</summary>
    private void OnDebugMessage(ReadOnlyMemory<byte> payload)
    {
        if (connection.DebugMessageHandler is not { } handler)
        {
            return;
        }

        SshDebugMessage message;
        try
        {
            message = SshDebugMessage.Decode(payload);
        }
        catch (SshWireFormatException)
        {
            return;
        }

        if (message.AlwaysDisplay && PeerText.Sanitize(message.Message) is { Length: > 0 } text)
        {
            CancellationToken token = connection.Disconnected;
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(text, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 接收循环之外的通知：回调抛的不影响连接。
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>把对端的 <c>DISCONNECT</c> 解成带原因码与原话的异常。</summary>
    /// <remarks>
    /// 对端给的描述常常是唯一有用的信息（<c>Too many authentication failures</c>），
    /// 拼进一句「服务端断开了」就等于把它扔了（原则 3）。
    /// </remarks>
    private static SshConnectionClosedException ReadDisconnect(ReadOnlyMemory<byte> payload)
    {
        SshDisconnectReason? reason = null;
        string? description = null;
        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadMessageNumber(SshMessageNumber.Disconnect);
            reason = (SshDisconnectReason)reader.ReadUInt32();
            description = reader.ReadUtf8String(SshConnection.MaxFieldBytes);
        }
        catch (Exception)
        {
            // 残缺的 DISCONNECT 仍然是 DISCONNECT —— 能读出多少算多少。
        }

        string message = "服务端发来了 DISCONNECT" +
            (reason is { } code ? $"（{code}，{(uint)code}）" : "") +
            (string.IsNullOrEmpty(description) ? "。" : $"：{PeerText.Sanitize(description)}");

        return new SshConnectionClosedException(SshFailureReason.Disconnected, SshPhase.Open, message)
        {
            DisconnectReason = reason,
            PeerDescription = description,
        };
    }

    private async ValueTask OnChannelDataAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelData);
        uint recipient = reader.ReadUInt32();
        ReadOnlySequence<byte> data = reader.ReadString(int.MaxValue);

        SshChannel? channel = channels.Lookup(recipient);
        if (channel is null)
        {
            return;   // 刚回收的通道的在途数据 —— 忽略，不断开
        }

        if (await ExceedsMaxPacketAsync(channel, data.Length, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!channel.OnData(data))
        {
            await connection.FaultProtocolAsync(
                $"通道 {recipient} 的对端发来了超出我们宣告窗口的数据 —— 这是明确的协议违规。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask OnChannelExtendedDataAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelExtendedData);
        uint recipient = reader.ReadUInt32();
        uint dataTypeCode = reader.ReadUInt32();
        ReadOnlySequence<byte> data = reader.ReadString(int.MaxValue);

        SshChannel? channel = channels.Lookup(recipient);
        if (channel is null)
        {
            return;
        }

        if (await ExceedsMaxPacketAsync(channel, data.Length, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!channel.OnExtendedData(dataTypeCode, data))
        {
            await connection.FaultProtocolAsync(
                $"通道 {recipient} 的对端发来了超出我们宣告窗口的扩展数据。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/05 §八〕单个数据段超出我们宣告的 max packet 是协议违规：判死并返回 <see langword="true"/>。
    /// </summary>
    /// <remarks>曾经不查 —— 有窗口与传输层的上限兜着，没有内存风险，但那是一个照单全收的违规。</remarks>
    private async ValueTask<bool> ExceedsMaxPacketAsync(SshChannel channel, long length, CancellationToken cancellationToken)
    {
        if (length <= channel.ReceiveMaxPacketBytes)
        {
            return false;
        }

        await connection.FaultProtocolAsync(
            $"通道 {channel.LocalId} 的对端发来了 {length} 字节的数据段，超出我们宣告的 max packet（{channel.ReceiveMaxPacketBytes} 字节）。",
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask OnChannelWindowAdjustAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelWindowAdjust);
        uint recipient = reader.ReadUInt32();
        uint bytes = reader.ReadUInt32();

        SshChannel? channel = channels.Lookup(recipient);
        if (channel is not null && !channel.OnWindowAdjust(bytes))
        {
            await connection.FaultProtocolAsync(
                $"通道 {recipient} 的窗口回补让计数溢出了 uint32 —— 对端在拿它撑爆我们的账。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnChannelClose(ReadOnlyMemory<byte> payload)
    {
        uint recipient = ReadRecipient(payload);
        SshChannel? channel = channels.Lookup(recipient);
        if (channel is null)
        {
            return;
        }

        // 还在等确认的通道收到 CLOSE（对端违规）：我们还不知道对端的通道号，回不了 CLOSE；按「没开成」收尾，
        // 让等着开通道的人拿到结局。曾经照常回 CLOSE（带着还是 0 的对端号 —— 关掉的是对端的 0 号通道）、
        // 而等确认的那个 completion 没人结算：不带令牌开通道的调用方就永远挂着。
        TaskCompletionSource<SshChannel>? pendingOpen = channels.TakePendingOpen(recipient);
        if (pendingOpen is not null)
        {
            channel.OnOpenFailed();
            pendingOpen.TrySetException(new SshChannelException(
                SshFailureReason.ChannelOpenFailed, $"对端还没确认通道 {recipient} 就把它关掉了。")
            {
                FailureKind = SshChannelFailureKind.ClosedBeforeConfirmation,
            });
            return;
        }

        // CLOSE **必须双向**：收到对端的就得回一个（除非我们已经发过）。
        // 「要不要回」与入队在同一把锁里判定 —— 泵正要发的数据要么排在这个 CLOSE 前面，
        // 要么看到「已发」而不再发（见 ISshChannelHost.SendIfAsync）。
        byte[] reply = new byte[5];
        reply[0] = (byte)SshMessageNumber.ChannelClose;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1), channel.RemoteId);
        connection.PostIf(reply, channel.OnClose, channel.IsInteractive);

        channel.OnCloseCompleted();
    }

    private void OnChannelRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelRequest);
        uint recipient = reader.ReadUInt32();
        string requestType = reader.ReadUtf8String(SshConnection.MaxFieldBytes);
        bool wantReply = reader.ReadBoolean();

        // 只把**类型相关的那一段**交给通道 —— 报文头(编号、通道号、类型、want_reply)
        // 的长度是变的(类型是个变长 string)，让下游自己去跳过它，
        // 迟早会有人按固定长度跳，然后把 exit-status 读成别的数。
        ReadOnlyMemory<byte> typeSpecific = payload[(int)reader.Consumed..];

        SshChannel? channel = channels.Lookup(recipient);
        if (channel is null)
        {
            // 不存在的通道：不回。通道号要等双向 CLOSE 走完才回收，所以这里只剩对端违规一种可能；
            // 而回应答需要**对端的**通道号，我们手上只有自己的 —— 曾经就拿它去填，
            // FAILURE 落在对端一条不相干的通道上（velashell-docs/zh/ssh/spec/05 §8：忽略）。
            return;
        }

        bool handled = channel.OnPeerRequest(requestType, typeSpecific);

        if (!wantReply)
        {
            return;
        }

        // 认得就回 SUCCESS，不认得回 FAILURE —— 但**必须回**。
        // 沉默会让对端的 FIFO 队列永远错位。
        // 例外是我们已经发过 CLOSE：之后这条通道上不许再有任何报文（RFC 4254 §5.3），
        // 对端收到 CLOSE 也就不再等这个应答了。
        byte[] reply = new byte[5];
        reply[0] = (byte)(handled ? SshMessageNumber.ChannelSuccess : SshMessageNumber.ChannelFailure);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1), channel.RemoteId);
        connection.PostIf(reply, () => !channel.CloseSent, channel.IsInteractive);
    }

    private async ValueTask OnChannelRequestReplyAsync(
        ReadOnlyMemory<byte> payload, bool success, CancellationToken cancellationToken)
    {
        uint recipient = ReadRecipient(payload);
        SshChannel? channel = channels.Lookup(recipient);

        if (channel is not null && !channel.OnRequestReply(success))
        {
            // 队列空时收到应答 = FIFO 失步。再往下走，后面每一个应答
            // 都会被安到错误的请求上，而那是**静默的**错误答案。
            await connection.FaultProtocolAsync(
                $"通道 {recipient} 收到了没有对应请求的应答 —— 请求/应答的 FIFO 已经失步。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>回一个 <c>UNIMPLEMENTED</c>。</summary>
    /// <remarks>
    /// 里面要带<b>被拒那个报文的序号</b>（RFC 4253 §11.4），不是 0 ——
    /// 对端靠它知道是哪一个报文没被认出来。刚读完的那个报文用的是
    /// 「当前接收序号 − 1」（序号在取出一帧之后才推进）。
    /// </remarks>
    private void PostUnimplemented()
    {
        byte[] packet = new byte[5];
        packet[0] = (byte)SshMessageNumber.Unimplemented;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(1), unchecked(transport.ReceiveSequenceNumber - 1));
        connection.Post(packet);
    }

    private static uint ReadRecipient(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();
        return reader.ReadUInt32();
    }
}
