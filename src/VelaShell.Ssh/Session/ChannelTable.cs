// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.1  CHANNEL_OPEN / CONFIRMATION / FAILURE
//   RFC 4254 §5.3  通道号要等双向 CLOSE 之后才回收
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§2、§8

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

/// <summary>一条连接的通道表：占着号的通道（含正在打开的）、通道号的分配与回收、接收窗口的总预算、入站通道的处理器，以及两个方向的开通道。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshConnection"/> 拆出来的协作者。表里的一切都在<b>连接的状态锁</b>里读写（构造时交进来的就是连接的那一把，
/// 不是另起的锁）：开通道时「连接判死了没有」与「登记进表」要在同一把锁里，判死时 <see cref="CloseAll"/> 才看得到每一条。
/// </para>
/// <para>
/// 锁顺序不变：连接的状态锁在前、通道的状态锁在后；这里持着连接锁时不碰任何通道的锁，也不入队。
/// 会话窗口总预算（<see cref="_windowBudgetUsed"/>）归这把锁，通道自己计的那一笔归通道的锁（见 <c>SshChannel.TryChargeBudget</c>）。
/// </para>
/// </remarks>
internal sealed class ChannelTable
{
    /// <summary>
    /// 一个 <c>CHANNEL_EXTENDED_DATA</c> 报文里，数据之外占 <c>packet_length</c> 的最大字节数：
    /// <c>padding_length</c>(1) + 编号(1) + 通道号(4) + 类型码(4) + 长度(4) + 最大填充(255)。
    /// </summary>
    private const int ChannelPacketOverhead = 1 + 1 + 4 + 4 + 4 + 255;

    private readonly SshConnection _connection;
    private readonly Transport.SshPacketTransport _transport;
    private readonly Lock _stateLock;
    private readonly SshConnectionLimits _limits;

    private readonly Dictionary<uint, SshChannel> _channels = [];
    private readonly Dictionary<uint, TaskCompletionSource<SshChannel>> _pendingOpens = [];

    /// <summary>已回收、但还不到复用时间的通道号，带回收那一刻（<see cref="SshConnection.Time"/> 的时间戳）。</summary>
    private readonly Queue<(uint Id, long RecycledAt)> _recycledIds = new();

    /// <summary>按通道类型登记的入站通道处理器（服务端发起的通道）。</summary>
    /// <remarks>
    /// 同一类型可以有<b>多个</b>处理器 —— 两个远程转发（<c>-R 8080</c> 与 <c>-R 9090</c>）
    /// 都要接 <c>forwarded-tcpip</c>。后登记的先问，第一个同意的接走。
    /// </remarks>
    private readonly Dictionary<string, List<IIncomingChannelHandler>> _incomingHandlers = [];

    private uint _nextChannelId;
    private long _windowBudgetUsed;

    /// <param name="connection">所属连接（也是交给每条通道的 <see cref="ISshChannelHost"/>）。</param>
    /// <param name="transport">连接的传输（宣告的单包上限要装得进它的报文上限）。</param>
    /// <param name="stateLock">连接的状态锁。</param>
    /// <param name="limits">通道限额。</param>
    internal ChannelTable(SshConnection connection, Transport.SshPacketTransport transport, Lock stateLock, SshConnectionLimits limits)
    {
        _connection = connection;
        _transport = transport;
        _stateLock = stateLock;
        _limits = limits;
    }

    /// <summary>当前占着通道号的通道数（见 <see cref="SshConnection.ChannelCount"/>）。</summary>
    public int Count
    {
        get
        {
            lock (_stateLock)
            {
                return _channels.Count;
            }
        }
    }

    /// <summary>此刻开着的通道的快照，按通道号排（见 <see cref="SshConnection.Channels"/>）。</summary>
    public IReadOnlyList<SshChannelSnapshot> Snapshot()
    {
        SshChannel[] channels;
        lock (_stateLock)
        {
            channels = [.. _channels.Values];
        }

        // 通道自己的锁在连接的锁之外取（锁顺序：连接在前、通道在后，这里干脆不嵌套）。
        return [.. channels
            .OrderBy(channel => channel.LocalId)
            .Select(channel => new SshChannelSnapshot(
                channel.LocalId, channel.ChannelType, channel.State, channel.OpenedAt, channel.BytesSent, channel.BytesReceived))];
    }

    public SshChannel? Lookup(uint localId)
    {
        lock (_stateLock)
        {
            return _channels.GetValueOrDefault(localId);
        }
    }

    /// <summary>打开一条通道（见 <see cref="SshConnection.OpenChannelAsync"/>）。</summary>
    /// <exception cref="SshChannelException">对端拒绝，或本端限额已满。</exception>
    public async ValueTask<SshChannel> OpenAsync(
        string channelType,
        ReadOnlyMemory<byte> typeSpecificPayload,
        SshChannelOptions? options,
        CancellationToken cancellationToken)
    {
        SshChannelOptions effective = options ?? SshChannelOptions.Default;
        int window = effective.WindowPolicy.InitialBytes;

        // 我们宣告的单包上限必须装得进传输层允许的报文 —— 否则对端照着宣告发来的
        // 报文会被传输层当成超长报文而判为协议错误，错误还指不到这里。
        int maxPayload = _transport.MaxPacketLength - ChannelPacketOverhead;
        if (effective.ReceiveMaxPacketBytes > maxPayload)
        {
            throw new ArgumentException(
                $"通道的 ReceiveMaxPacketBytes（{effective.ReceiveMaxPacketBytes}）超过了传输层允许的上限 " +
                $"{maxPayload}（报文上限 {_transport.MaxPacketLength} 减去报文头与填充）。",
                nameof(options));
        }

        SshChannel channel;
        TaskCompletionSource<SshChannel> completion;

        lock (_stateLock)
        {
            _connection.ThrowIfFaulted();

            // 限额撞满**不断开会话** —— 通道是独立的失败域。原因码是 LimitExceeded（本端的上限），
            // 不是 ChannelOpenFailed：后者说的是对端拒绝，而且曾经连 OpenFailureReason 都没有。
            //
            // 只数 `_channels`：正在打开的那条**也在里面**（见下面的登记），
            // 两个字典加起来会把它数两遍，等于把上限砍了一半。
            if (_channels.Count >= _limits.MaxChannels)
            {
                throw new SshChannelException(
                    SshFailureReason.LimitExceeded,
                    $"本端的并发通道数已达上限 {_limits.MaxChannels}。")
                {
                    FailureKind = SshChannelFailureKind.ChannelLimitReached,
                    Limit = _limits.MaxChannels,
                };
            }

            if (_windowBudgetUsed + window > _limits.SessionWindowBudgetBytes)
            {
                throw new SshChannelException(
                    SshFailureReason.LimitExceeded,
                    $"会话的接收窗口总预算已用尽（{_limits.SessionWindowBudgetBytes / (1024 * 1024)} MiB）。" +
                    "把窗口策略调小，或者少开几条并发通道。")
                {
                    FailureKind = SshChannelFailureKind.WindowBudgetExhausted,
                    Limit = _limits.SessionWindowBudgetBytes,
                };
            }

            uint localId = AllocateChannelId();
            _windowBudgetUsed += window;

            channel = new SshChannel(_connection, localId, channelType, effective);
            completion = new TaskCompletionSource<SshChannel>(TaskCreationOptions.RunContinuationsAsynchronously);

            // ⚠️ **通道要在发 CHANNEL_OPEN 之前就登记进 `_channels`。**
            //
            // 登记晚了会丢应答，而且是随机丢：收包循环和这里是两个线程，
            // 内存传输上服务端可以在 `SendAsync` 的 await 还没恢复时就把
            // OPEN_CONFIRMATION 送到。那一刻 `OnOpenConfirmation`
            // 会把 `_pendingOpens` 里的项**取走**，再去 `_channels` 里找通道 ——
            // 找不到，于是 `return`，应答就被丢在地上，TCS 永远不完成。
            //
            // 表现是「OpenChannelAsync 挂到用例超时」，而且每次挂的用例都不一样，
            // 因为它只取决于那两个线程谁先跑。满跑约十轮复现一次。
            // 两条 dump 把它钉死：客户端收到的报文号最后一个正是 91
            // （OPEN_CONFIRMATION），而 `_pendingOpens` 已经空了。
            _pendingOpens[localId] = completion;
            _channels[localId] = channel;
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpen);
        writer.WriteUtf8String(channelType);
        writer.WriteUInt32(channel.LocalId);
        writer.WriteUInt32((uint)window);
        writer.WriteUInt32((uint)effective.ReceiveMaxPacketBytes);
        writer.WriteRaw(typeSpecificPayload.Span);

        bool enqueued = false;
        try
        {
            // 取消只在入队之前生效（见 EnqueueAsync）：这一步正常返回，CHANNEL_OPEN 就一定会上线。
            await _connection.SendAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            enqueued = true;

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (enqueued)
        {
            // CHANNEL_OPEN 已经发出去了，调用方不等了 —— 但对端迟早会应答。
            //
            // ⚠️ **不能把它从账本里摘掉。**摘了的话，迟到的 OPEN_CONFIRMATION 找不到主、被丢在地上：
            //    服务端那条通道永远不关，每取消一次就占掉它一个 MaxSessions 名额（OpenSSH 默认 10），
            //    之后再开通道全都失败。所以让应答照常走完：确认下来的通道立刻关掉；
            //    对端拒绝、连接断开或释放时，通道已经由那几条路径收尾了。
            //    「确认刚到、取消紧跟着到」的竞态也落在这里 —— 那时 completion 已经带着结果。
            //
            //    拒绝与断线这两种结局也要接住：没人等了，它们的异常留在任务上就是一个没人观察的任务异常，
            //    宿主据此写崩溃日志。曾经这里只接「确认下来」那一种（OnlyOnRanToCompletion）。
            _ = completion.Task.ContinueWith(
                static opened =>
                {
                    if (opened.IsCompletedSuccessfully)
                    {
                        _ = opened.Result.DisposeAsync().AsTask();
                    }
                    else
                    {
                        _ = opened.Exception;   // 看一眼，标成已观察
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception)
        {
            // 没发出去（入队前取消、连接已经没了），或者对端拒绝了。
            lock (_stateLock)
            {
                _pendingOpens.Remove(channel.LocalId);

                // 对端拒绝时 OnOpenFailed 已经把通道摘掉、预算退回 —— 这里再退一次，
                // 预算就越拒越多，会话窗口总上限形同虚设。
                if (_channels.Remove(channel.LocalId))
                {
                    _windowBudgetUsed -= window;
                }
            }
            throw;
        }
    }

    /// <summary>收到 <c>CHANNEL_OPEN_CONFIRMATION</c>：等着的那条通道开成了。</summary>
    public void OnOpenConfirmation(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
        uint recipient = reader.ReadUInt32();
        uint senderChannel = reader.ReadUInt32();
        uint initialWindow = reader.ReadUInt32();
        uint maxPacket = reader.ReadUInt32();

        TaskCompletionSource<SshChannel>? completion;
        SshChannel? channel;
        lock (_stateLock)
        {
            _pendingOpens.Remove(recipient, out completion);
            _channels.TryGetValue(recipient, out channel);
        }

        if (completion is null || channel is null)
        {
            return;
        }

        channel.OnOpenConfirmed(senderChannel, initialWindow, maxPacket);
        completion.TrySetResult(channel);
    }

    /// <summary>收到 <c>CHANNEL_OPEN_FAILURE</c>：等着的那条通道被对端拒绝了。</summary>
    public void OnOpenFailure(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpenFailure);
        uint recipient = reader.ReadUInt32();
        uint reasonCode = reader.ReadUInt32();
        string description = reader.ReadUtf8String(SshConnection.MaxFieldBytes);

        TaskCompletionSource<SshChannel>? completion;
        SshChannel? channel;
        string channelType = SshProtocolNames.ChannelSession;
        lock (_stateLock)
        {
            // 〔velashell-docs/zh/ssh/spec/05 §八〕只认还在等确认的那种：对一条已经开着的通道发 OPEN_FAILURE 是对端违规，忽略。
            // 曾经照样把通道收尾、**当场还号**（没走双向 CLOSE）—— 对端还以为那条通道开着，30 秒后号被复用就会串话。
            if (!_pendingOpens.Remove(recipient, out completion))
            {
                return;
            }
            if (_channels.TryGetValue(recipient, out channel))
            {
                channelType = channel.ChannelType;
            }
        }

        channel?.OnOpenFailed();
        completion.TrySetException(
            SshChannelException.FromOpenFailure(channelType, reasonCode, description));
    }

    /// <summary>把还在等确认的那一项摘出来（对端在确认之前就发了 <c>CLOSE</c>）；没有在等的时为 <see langword="null"/>。</summary>
    public TaskCompletionSource<SshChannel>? TakePendingOpen(uint recipient)
    {
        TaskCompletionSource<SshChannel>? pendingOpen;
        lock (_stateLock)
        {
            _pendingOpens.Remove(recipient, out pendingOpen);
        }
        return pendingOpen;
    }

    /// <summary>把所有还在等开通道的人摘出来（连接判死或释放时由连接结算）。</summary>
    public List<TaskCompletionSource<SshChannel>> TakePendingOpens()
    {
        lock (_stateLock)
        {
            List<TaskCompletionSource<SshChannel>> pending = [.. _pendingOpens.Values];
            _pendingOpens.Clear();
            return pending;
        }
    }

    /// <summary>会话没了：每条通道跟着收尾，还没读完的读端拿到 <paramref name="reason"/>。</summary>
    public void CloseAll(Exception reason)
    {
        SshChannel[] channels;
        lock (_stateLock)
        {
            channels = [.. _channels.Values];
        }

        foreach (SshChannel channel in channels)
        {
            channel.OnSessionClosed(reason);
        }
    }

    /// <summary>自适应扩窗之前先向会话的窗口总预算申请（见 <see cref="ISshChannelHost.TryReserveWindowBudget"/>）。</summary>
    public bool TryReserveWindowBudget(int bytes)
    {
        lock (_stateLock)
        {
            if (_windowBudgetUsed + bytes > _limits.SessionWindowBudgetBytes)
            {
                return false;
            }
            _windowBudgetUsed += bytes;
            return true;
        }
    }

    /// <summary>缩窗时把多出来的预算还回去（见 <see cref="ISshChannelHost.ReleaseWindowBudget"/>）。</summary>
    public void ReleaseWindowBudget(int bytes)
    {
        lock (_stateLock)
        {
            _windowBudgetUsed -= bytes;
        }
    }

    /// <summary>通道彻底关了：摘掉、退预算、回收号（见 <see cref="ISshChannelHost.OnChannelClosed"/>）。</summary>
    public void OnChannelClosed(uint localId, int windowBytes)
    {
        lock (_stateLock)
        {
            if (!_channels.Remove(localId))
            {
                return;
            }
            _windowBudgetUsed -= windowBytes;
            RecycleChannelId(localId);
        }
    }

    /// <summary>给某一类型<b>再挂</b>一个处理器，不影响已有的（见 <see cref="SshConnection.AddIncomingChannelHandler"/>）。</summary>
    public void AddIncomingChannelHandler(string channelType, IIncomingChannelHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateLock)
        {
            if (!_incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list))
            {
                list = [];
                _incomingHandlers[channelType] = list;
            }
            list.Add(handler);
        }
    }

    /// <summary>摘掉<b>这一个</b>处理器；别人登记的同类型处理器不受影响。</summary>
    public void RemoveIncomingChannelHandler(string channelType, IIncomingChannelHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateLock)
        {
            if (_incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list)
                && list.Remove(handler)
                && list.Count == 0)
            {
                _incomingHandlers.Remove(channelType);
            }
        }
    }

    /// <summary>同时在「问处理器要不要接」的对端开通道请求上限，超出的直接以资源不足拒绝。</summary>
    internal const int MaxPeerOpensDeciding = 64;

    /// <summary>正在后台问处理器的对端开通道请求数。</summary>
    private int _peerOpensDeciding;

    /// <summary>收到对端的 <c>CHANNEL_OPEN</c>。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>问处理器这一步不在接收循环上等。</b>处理器是使用者写的，它的 <c>GetOptionsAsync</c>
    /// 可能要弹窗问人、查配置、连别的东西 —— 曾经就地 await，它一慢，整条连接上所有通道的收包都停住。
    /// 现在解析与查处理器在这里做完，问与接受交给后台：接收循环立刻回去收包。
    /// </para>
    /// <para>
    /// 对端在我们确认之前不能往这条通道上发任何东西，所以后台完成的确认与接收循环之间没有顺序问题；
    /// 两条开通道请求的确认谁先谁后也不要紧 —— 各自带着对端的通道号。
    /// 同时在问的请求有上限（<see cref="MaxPeerOpensDeciding"/>）：没有的话，对端刷一堆开通道请求、
    /// 处理器又慢，就是一堆挂着的任务。
    /// </para>
    /// </remarks>
    public ValueTask OnPeerChannelOpenAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpen);
        string channelType = reader.ReadUtf8String(SshConnection.MaxFieldBytes);
        uint senderChannel = reader.ReadUInt32();
        uint initialWindow = reader.ReadUInt32();
        uint maxPacket = reader.ReadUInt32();

        // ⚠️ **必须复制。**载荷背后是传输的接收缓冲，下一次读包就会被覆盖；
        //    而处理器是在另一个任务上、在接收循环读了别的报文之后才用它。
        ReadOnlyMemory<byte> typeSpecific = payload[(int)reader.Consumed..].ToArray();

        IIncomingChannelHandler[] candidates;
        lock (_stateLock)
        {
            candidates = _incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list)
                ? [.. Enumerable.Reverse(list)]
                : [];
        }

        if (candidates.Length == 0)
        {
            // 没人处理这种类型。**必须明确拒绝**，不能沉默 —— 沉默会让对端一直等着。
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.UnknownChannelType,
                $"本端没有登记 {channelType} 的处理器。");
            return default;
        }

        if (Interlocked.Increment(ref _peerOpensDeciding) > MaxPeerOpensDeciding)
        {
            Interlocked.Decrement(ref _peerOpensDeciding);
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.ResourceShortage,
                $"同时在决定的开通道请求已达上限 {MaxPeerOpensDeciding}。");
            return default;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await AcceptPeerChannelAsync(
                            channelType, senderChannel, initialWindow, maxPacket, typeSpecific, candidates,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 会话在收工（取消），或者会话已经坏了（Post 不再发）。单条开通道请求没有别的补救。
                }
                finally
                {
                    Interlocked.Decrement(ref _peerOpensDeciding);
                }
            },
            CancellationToken.None);
        return default;
    }

    /// <summary>处理器拒绝时回给对端的原因码（RFC 4254 §5.1）。</summary>
    /// <remarks>
    /// 〔FW-D2〕连不上要连的东西（远程转发的本机目标、agent 转发的本机 agent）回 2（connect failed），
    /// 并发名额满了回 4（resource shortage），其余回 1（administratively prohibited）。曾经一律回 1：
    /// 远端分不出是「不让连」还是「连不上」，服务端日志里也没有 connect failed。
    /// </remarks>
    private static SshChannelOpenFailureReason OpenFailureReasonFor(Exception refusal) => (refusal as SshException)?.Reason switch
    {
        SshFailureReason.TcpRefused or SshFailureReason.TcpTimeout or SshFailureReason.TcpUnreachable
            or SshFailureReason.DnsFailure or SshFailureReason.AgentUnavailable or SshFailureReason.AgentNotRunning
            => SshChannelOpenFailureReason.ConnectFailed,
        SshFailureReason.LimitExceeded => SshChannelOpenFailureReason.ResourceShortage,
        _ => SshChannelOpenFailureReason.AdministrativelyProhibited,
    };

    /// <summary>问处理器要不要接，接的话建通道、回确认、交给处理器（后台执行，见 <see cref="OnPeerChannelOpenAsync"/>）。</summary>
    private async Task AcceptPeerChannelAsync(
        string channelType,
        uint senderChannel,
        uint initialWindow,
        uint maxPacket,
        ReadOnlyMemory<byte> typeSpecific,
        IIncomingChannelHandler[] candidates,
        CancellationToken cancellationToken)
    {
        // 后登记的先问；第一个同意的接走。「不归我」用异常表达（见接口说明）。
        IIncomingChannelHandler? handler = null;
        SshChannelOptions options = SshChannelOptions.Default;
        string refusal = "";
        SshChannelOpenFailureReason refusalCode = SshChannelOpenFailureReason.AdministrativelyProhibited;
        foreach (IIncomingChannelHandler candidate in candidates)
        {
            try
            {
                options = await candidate.GetOptionsAsync(channelType, typeSpecific, cancellationToken)
                    .ConfigureAwait(false);
                handler = candidate;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 处理器拒绝（例如远程转发的路由表里找不到这个绑定地址）。
                refusal = ex.Message;
                refusalCode = OpenFailureReasonFor(ex);
            }
        }

        if (handler is null)
        {
            PostOpenFailure(senderChannel, refusalCode, refusal);
            return;
        }

        int window = options.WindowPolicy.InitialBytes;
        SshChannel? channel = null;

        lock (_stateLock)
        {
            if (!_connection.IsFaulted
                && _channels.Count < _limits.MaxChannels
                && _windowBudgetUsed + window <= _limits.SessionWindowBudgetBytes)
            {
                uint localId = AllocateChannelId();
                _windowBudgetUsed += window;
                channel = new SshChannel(_connection, localId, channelType, options);
                _channels[localId] = channel;
            }
        }

        if (channel is null)
        {
            // ⚠️ 处理器在 GetOptionsAsync 里可能已经占了资源（并发槽位）。
            //    这条通道不会走到 HandleAsync 了，得告诉它还回去 —— 否则每拒一次漏一个，
            //    漏满之后这一类通道就再也开不出来了。
            NotifyOpenAborted(handler, channelType, typeSpecific);
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.ResourceShortage,
                "本端的通道数或窗口预算已用尽。");
            return;
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
        writer.WriteUInt32(senderChannel);
        writer.WriteUInt32(channel.LocalId);
        writer.WriteUInt32((uint)window);
        writer.WriteUInt32((uint)options.ReceiveMaxPacketBytes);

        // ① 对端的号、窗口与状态在确认发出之前就设好：对端一收到确认就可能 EOF + CLOSE，
        //    接收循环处理那个 CLOSE 时，回给对端的 CLOSE 要带着它的真实通道号（见 OnOpenAccepted）。
        // ② 确认先入队，泵后启动 —— 泵发出的数据在队列里排在确认之后，
        //    对端一定先认得这条通道，再收到它的数据。
        channel.OnOpenAccepted(senderChannel, initialWindow, maxPacket);
        _connection.Post(buffer.WrittenMemory);
        _connection.AfterIncomingOpenConfirmationQueued?.Invoke(channel);
        channel.StartPumps();

        // 交给处理器时**不等它** —— 它多半要去连一个本地目标，
        // 而接收循环不能停在任何一条通道上。
        SshChannel accepted = channel;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await handler.HandleAsync(accepted, typeSpecific, _connection.LifetimeToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 单条通道的失败不影响会话 —— 通道是独立的失败域。
                }
                finally
                {
                    await accepted.DisposeAsync().ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    private static void NotifyOpenAborted(
        IIncomingChannelHandler handler, string channelType, ReadOnlyMemory<byte> typeSpecific)
    {
        try
        {
            handler.OnOpenAborted(channelType, typeSpecific);
        }
        catch (Exception)
        {
            // 处理器自己的善后出错，不该变成会话的问题。
        }
    }

    private void PostOpenFailure(uint senderChannel, SshChannelOpenFailureReason reason, string description)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpenFailure);
        writer.WriteUInt32(senderChannel);
        writer.WriteUInt32((uint)reason);

        // 描述里常常带着对端给的通道类型名（最长 64 KiB）—— 原样回显的话，
        // 每一个畸形的 CHANNEL_OPEN 都换来一个同样大的应答，对端拿它来撑爆我们的发送队列。
        writer.WriteUtf8String(description.Length <= MaxOpenFailureDescription
            ? description
            : description[..MaxOpenFailureDescription] + "…");
        writer.WriteUtf8String("");
        _connection.Post(buffer.WrittenMemory);
    }

    /// <summary>回 <c>CHANNEL_OPEN_FAILURE</c> 时描述的最大字符数。</summary>
    private const int MaxOpenFailureDescription = 256;

    /// <summary>分配一个通道号，优先用「已经放够时间」的回收号。</summary>
    /// <remarks>调用时必须持有 <see cref="_stateLock"/>。</remarks>
    private uint AllocateChannelId()
    {
        // 回收号要放够 ChannelIdReuseDelay 才能再用 —— 对端可能还在路上
        // 发这个号的数据，复用得太早会让那些数据投递到新通道上（串话）。
        if (_recycledIds.TryPeek(out (uint Id, long RecycledAt) head)
            && _connection.Time.GetElapsedTime(head.RecycledAt) >= _limits.ChannelIdReuseDelay)
        {
            _recycledIds.Dequeue();
            return head.Id;
        }

        return _nextChannelId++;
    }

    /// <remarks>调用时必须持有 <see cref="_stateLock"/>。</remarks>
    private void RecycleChannelId(uint localId) =>
        _recycledIds.Enqueue((localId, _connection.Time.GetTimestamp()));
}
