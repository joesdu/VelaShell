// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 测试服务端的**连接协议侧**：通道的开关、数据、窗口、请求。
// 接在 TestAuthServer 之后，在同一条传输上跑 RFC 4254 的服务端一半。
//
// ⚠️ **只为测试存在，绝不发布。**
//    它按剧本回放输出，没有真的执行任何东西。

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>服务端收到 <c>exec</c> / <c>shell</c> 之后照着回放的剧本。</summary>
public sealed record TestChannelScript
{
    /// <summary>发到 stdout 的内容。</summary>
    public byte[] StandardOutput { get; init; } = [];

    /// <summary>发到 stderr 的内容（<c>CHANNEL_EXTENDED_DATA</c>，类型码 1）。</summary>
    public byte[] StandardError { get; init; } = [];

    /// <summary>
    /// 用一个我们不认识的扩展数据类型码再发一份。
    /// </summary>
    /// <remarks>客户端应当丢弃它、照常计窗口、且不报错。</remarks>
    public byte[] UnknownExtendedData { get; init; } = [];

    /// <summary>退出码。<see langword="null"/> 表示不发 <c>exit-status</c>。</summary>
    public int? ExitCode { get; init; }

    /// <summary>退出信号名（不带 SIG 前缀）。设了就发 <c>exit-signal</c> 而不是 <c>exit-status</c>。</summary>
    public string? ExitSignal { get; init; }

    /// <summary>把客户端发来的 stdin 原样回送到 stdout。</summary>
    public bool EchoStandardInput { get; init; }

    /// <summary>等收到客户端的 <c>CHANNEL_EOF</c> 之后才开始回放。</summary>
    public bool WaitForClientEof { get; init; }

    /// <summary>拒绝 <c>exec</c> / <c>shell</c> / <c>subsystem</c>。</summary>
    public bool RejectCommand { get; init; }

    /// <summary>拒绝 <c>pty-req</c>。</summary>
    public bool RejectPty { get; init; }

    /// <summary>拒绝 <c>CHANNEL_OPEN</c>，回这个原因码。</summary>
    public SshChannelOpenFailureReason? RejectOpenWith { get; init; }

    /// <summary>服务端宣告的初始窗口。</summary>
    public int InitialWindow { get; init; } = 64 * 1024;

    /// <summary>服务端宣告的最大报文长度。</summary>
    public int MaxPacket { get; init; } = 32 * 1024;

    /// <summary>回放完之后发 <c>CHANNEL_EOF</c> 与 <c>CHANNEL_CLOSE</c>。</summary>
    public bool CloseAfterScript { get; init; } = true;

    /// <summary>
    /// <c>subsystem</c> 被接受之后，把通道的字节流交给它。
    /// </summary>
    /// <remarks>
    /// SFTP 就挂在这里：子系统拿到的是一条纯字节流，
    /// 与 SSH 的分帧无关 —— 它有自己的分帧。
    /// </remarks>
    public Func<PipeReader, PipeWriter, CancellationToken, Task>? SubsystemHandler { get; init; }

    /// <summary>
    /// 收到 <c>direct-tcpip</c> 时怎么处理。
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> 表示拒绝（回 <see cref="RejectTunnelWith"/>）。
    /// 参数是目标 <c>host:port</c> 与这条隧道的字节流。
    /// </remarks>
    public Func<string, PipeReader, PipeWriter, CancellationToken, Task>? TunnelHandler { get; init; }

    /// <summary>没有 <see cref="TunnelHandler"/> 时用这个原因码拒绝隧道。</summary>
    public SshChannelOpenFailureReason RejectTunnelWith { get; init; } =
        SshChannelOpenFailureReason.AdministrativelyProhibited;

    /// <summary>接受 <c>x11-req</c> 通道请求。</summary>
    public bool GrantX11Forward { get; init; }

    /// <summary>接受 <c>streamlocal-forward@openssh.com</c> 全局请求。</summary>
    public bool GrantStreamLocalForward { get; init; }

    /// <summary>接受 <c>tcpip-forward</c> 全局请求，并回这个端口（<c>0</c> = 拒绝）。</summary>
    public int GrantRemoteForwardPort { get; init; }

    /// <summary>拒绝 <c>auth-agent-req@openssh.com</c>。</summary>
    public bool RejectAgentForward { get; init; }
}

/// <summary>测试服务端收到的一条 <c>x11-req</c>。</summary>
/// <param name="SingleConnection">只允许一条 X11 连接。</param>
/// <param name="AuthProtocol">授权协议名。</param>
/// <param name="AuthCookieHex">cookie 的十六进制文本 —— <b>应当是假的那个</b>。</param>
/// <param name="ScreenNumber">屏幕号。</param>
public sealed record TestX11Request(
    bool SingleConnection, string AuthProtocol, string AuthCookieHex, int ScreenNumber);

/// <summary>服务端在通道上观察到的事实。</summary>
public sealed class TestChannelObservation
{
    /// <summary>收到的 <c>exec</c> 命令行。</summary>
    public List<string> Commands { get; } = [];

    /// <summary>收到的通道请求类型，按顺序。</summary>
    public List<string> Requests { get; } = [];

    /// <summary>收到的环境变量。</summary>
    public Dictionary<string, string> Environment { get; } = [];

    /// <summary>收到的 <c>pty-req</c>：终端类型与尺寸。</summary>
    public List<(string Term, TerminalSize Size, byte[] Modes)> PtyRequests { get; } = [];

    /// <summary>收到的 <c>window-change</c>。</summary>
    public List<TerminalSize> WindowChanges { get; } = [];

    /// <summary>收到的信号名。</summary>
    public List<string> Signals { get; } = [];

    /// <summary>收到的 <c>subsystem</c> 名。</summary>
    public List<string> Subsystems { get; } = [];

    /// <summary>收到的 stdin 全部内容。</summary>
    public List<byte> StandardInput { get; } = [];

    /// <summary>是否收到过客户端的 <c>CHANNEL_EOF</c>。</summary>
    public bool ReceivedEof { get; set; }

    /// <summary>是否收到过客户端的 <c>CHANNEL_CLOSE</c>。</summary>
    public bool ReceivedClose { get; set; }

    /// <summary>客户端宣告的初始窗口与最大报文长度。</summary>
    public (uint Window, uint MaxPacket) ClientAnnounced { get; set; }

    private int _windowAdjustCount;

    /// <summary>收到的 <c>WINDOW_ADJUST</c> 次数。</summary>
    /// <remarks>
    /// 加它的是服务端循环所在的线程，读它的是测试线程，所以两边都要显式同步 ——
    /// 自适应窗口那组用例正是**轮询这个数直到它不再变**来判断回补已经全部到齐。
    /// 少了同步，那个循环有可能一直读到同一个陈旧值。
    /// </remarks>
    public int WindowAdjustCount => Volatile.Read(ref _windowAdjustCount);

    /// <summary>记一次收到的 <c>WINDOW_ADJUST</c>。</summary>
    public void NoteWindowAdjust() => Interlocked.Increment(ref _windowAdjustCount);

    /// <summary>收到的 <c>WINDOW_ADJUST</c> 总字节数。</summary>
    public long WindowAdjustBytes { get; set; }

    /// <summary>服务端一侧完成过几次重协商（不分发起方）。</summary>
    public int RekeysHandled { get; set; }

    /// <summary>子系统输出泵挂掉的原因（如果有）。</summary>
    /// <remarks>
    /// 泵一死 EOF / CLOSE 就发不出去，客户端会挂到超时。断言之前先看这里。
    /// </remarks>
    public Exception? SubsystemFault { get; set; }

    /// <summary>服务端收包循环挂掉的原因（如果有）。</summary>
    /// <remarks>
    /// 循环一停就不再应答，客户端会挂到超时。断言之前先看这里。
    /// </remarks>
    public Exception? ServerFault { get; set; }

    /// <summary>
    /// 客户端已经开始收尾（用例在拆场）。之后服务端写不出去是预期的，不算服务端故障。
    /// </summary>
    /// <remarks>
    /// 典型的一幕：客户端关通道、紧接着释放连接；服务端刚收到 CLOSE、正要回一个 ——
    /// 那一次写撞上已经关掉的读取端。那是拆场的时序，不是被测行为。
    /// </remarks>
    public bool ClientGone
    {
        get => Volatile.Read(ref _clientGone);
        set => Volatile.Write(ref _clientGone, value);
    }

    private bool _clientGone;

    /// <summary>回放剧本时抛出的异常（如果有）。</summary>
    /// <remarks>
    /// 剧本挂了就发不出 EOF / CLOSE，客户端会一直等到超时。
    /// 断言之前先看这里，能把「30 秒超时」变成一句看得懂的原因。
    /// </remarks>
    public Exception? ScriptFault { get; set; }

    /// <summary>收到的 <c>x11-req</c>。</summary>
    /// <remarks>
    /// 用例靠它验证**发出去的是假 cookie**，而不是本机真实的那个。
    /// </remarks>
    public List<TestX11Request> X11Requests { get; } = [];

    /// <summary>收到的全局请求类型。</summary>
    public List<string> GlobalRequests { get; } = [];

    /// <summary>客户端请求在哪些套接字路径上开远程转发。</summary>
    public List<string> StreamLocalForwardBinds { get; } = [];

    /// <summary>被拒绝的 <c>CHANNEL_OPEN</c> 次数。</summary>
    public int RejectedOpens { get; set; }

    /// <summary>客户端请求隧道到哪些目标（<c>host:port</c> 或套接字路径）。</summary>
    public List<string> TunnelTargets { get; } = [];

    /// <summary>客户端请求的远程转发绑定。</summary>
    public List<(string BindAddress, int Port)> RemoteForwardBinds { get; } = [];

    /// <summary>客户端请求过几次 agent 转发。</summary>
    public int AgentForwardRequests { get; set; }
}

/// <summary>测试服务端的连接协议侧。</summary>
public sealed class TestChannelServer : IDisposable
{
    private const int MaxField = 256 * 1024;

    private readonly SshPacketTransport _transport;
    private readonly TestChannelScript _script;

    /// <summary>本端（服务端）给通道的编号 → 客户端的编号。</summary>
    private readonly Dictionary<uint, uint> _peerIds = [];

    /// <summary>本端能往客户端发多少字节（客户端的接收窗口）。</summary>
    /// <remarks>
    /// ⚠️ **一切读写都要走 <see cref="_sendWindowLock"/>。**
    ///
    /// 它被两条并发的路径改：收包循环处理 <c>WINDOW_ADJUST</c> 时加，
    /// 发送泵扣额度时减。没有锁的时候这是一个教科书式的丢失更新 ——
    /// 「读 current → 写 current + bytes」和「读 available → 写 available - take」
    /// 交错，其中一次写会被另一次覆盖掉。
    ///
    /// 覆盖掉**减**的那次，窗口就凭空变大，服务端于是发出**超过客户端宣告窗口**
    /// 的数据；客户端（正确地）判定这是协议违规并断开，
    /// 表现为「偶尔有一次只收到一半数据就 EOF 了」。
    /// 实测约五轮满跑挂一次 —— 这个桩的 bug，不是库的 bug。
    /// </remarks>
    private readonly Dictionary<uint, long> _sendWindow = [];

    private readonly Lock _sendWindowLock = new();

    private readonly Dictionary<uint, TaskCompletionSource> _eofReceived = [];

    /// <summary>已经发出去的服务端 KEXINIT；非 null 表示一次重协商正在进行。</summary>
    private byte[]? _pendingRekeyKexInit;

    /// <summary>重协商完成时要交代的那个任务。</summary>
    private TaskCompletionSource<TestSshServerHandshake>? _rekeyRequest;

    /// <summary>发起重协商要靠它 —— 密钥交换的服务端实现在那边。</summary>
    private TestSshServer? _server;
    private readonly AsyncGateBox _windowGate = new();

    private uint _nextChannelId = 1000;

    /// <summary>服务端发起、还在等客户端答复的通道。</summary>
    private readonly Dictionary<uint, TaskCompletionSource<bool>> _pendingServerOpens = [];

    /// <summary>每条通道的入站字节流（客户端 → 处理器）。</summary>
    private readonly Dictionary<uint, Pipe> _channelInput = [];

    /// <summary>每条通道的出站字节流（处理器 → 客户端）。</summary>
    private readonly Dictionary<uint, Pipe> _channelOutput = [];


    /// <summary>在一条已完成认证的传输上建立连接协议服务端。</summary>
    public TestChannelServer(SshPacketTransport transport, TestChannelScript? script = null)
    {
        _transport = transport;
        _script = script ?? new TestChannelScript();
    }

    /// <summary>服务端这一侧观察到的事实。</summary>
    public TestChannelObservation Observation { get; } = new();

    /// <summary>跑服务端的接收循环，直到对端关闭连接。</summary>
    /// <summary>收包并按剧本应答，直到对端走掉或被取消。</summary>
    /// <remarks>
    /// ⚠️ **这里绝不能让异常把循环静静地带走。**
    ///
    /// 循环一停，服务端就再也不应答了；而客户端那边看到的不是错误，
    /// 是一个**永远不返回的 await** —— 最后以「30 秒全局超时」收场，
    /// 日志里连是哪一步出的问题都看不出来。这种失败查起来极其昂贵，
    /// 我在这个仓库里为它烧掉过整整一轮排查。
    ///
    /// 所以：记下原因，并**主动把传输关掉**，让客户端立刻读到「对端关闭了连接」。
    /// 超时变成一条指名道姓的失败。
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SshInboundPacket packet;
                try
                {
                    packet = await _transport.ReadPacketAsync(cancellationToken);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (packet.IsEndOfStream || packet.MessageNumber == SshMessageNumber.Disconnect)
                {
                    return;
                }

                await HandleAsync(packet.MessageNumber, packet.Payload.ToArray(), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 测试收尾。
        }
        catch (Exception ex) when (Observation.ClientGone && ex is IOException or ObjectDisposedException)
        {
            // 拆场时序：客户端已经走了，服务端的最后一次回写落空。见 ClientGone 的说明。
        }
        catch (Exception ex)
        {
            Observation.ServerFault ??= ex;

            // 让客户端立刻看到「对端没了」，而不是挂到超时。
            try
            {
                await _transport.DisposeAsync();
            }
            catch (Exception)
            {
                // 已经坏掉了，没有别的补救。
            }

            throw;
        }
    }

    /// <summary>把服务端交给这个循环，之后才能发起重协商。</summary>
    public void AttachServer(TestSshServer server) => _server = server;

    /// <summary>请求发起一次重协商，返回的任务在重协商完成时结束。</summary>
    /// <remarks>
    /// 这模拟的是「OpenSSH 到了 RekeyLimit 自己发 KEXINIT」——
    /// 客户端接不住的表现就是会话忽然断掉。
    /// </remarks>
    public async Task<TestSshServerHandshake> RequestRekeyAsync(
        CancellationToken cancellationToken = default)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("先调 AttachServer 再请求重协商。");
        }

        TaskCompletionSource<TestSshServerHandshake> request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _rekeyRequest, request);

        // ⚠️ **在这里就把 KEXINIT 发出去**，不要只设个标志等收包循环去发。
        // 收包循环此刻停在「等下一个报文」上，而客户端在等我们的 KEXINIT ——
        // 只设标志的话两边一起停住（我第一版就是这么写的，五条用例全挂在 25 秒超时上）。
        //
        // 发出去之后客户端会回它自己的 KEXINIT，那个报文把循环唤醒，
        // 循环再走 OnClientKexInitAsync 把剩下的做完。
        _pendingRekeyKexInit = await _server.BeginRekeyAsync(SendForKexAsync, cancellationToken);

        return await request.Task;
    }

    /// <summary>收到客户端的 KEXINIT —— 把重协商收尾。</summary>
    private async Task OnClientKexInitAsync(byte[] payload, CancellationToken cancellationToken)
    {
        byte[]? ours = _pendingRekeyKexInit;
        TaskCompletionSource<TestSshServerHandshake>? request =
            Interlocked.Exchange(ref _rekeyRequest, null);

        _pendingRekeyKexInit = null;

        try
        {
            // 两种发起方向在这里分开：
            //   · ours 非 null —— 我们先发过 KEXINIT（服务端发起），收尾就好；
            //   · ours 为 null —— 客户端发起的，我们还没发过，要发。
            TestSshServerHandshake again = ours is not null
                ? await _server!.CompleteRekeyAsync(
                    ours, payload, SendForKexAsync, ReadForKexAsync, cancellationToken)
                : await _server!.RespondToRekeyAsync(
                    payload, SendForKexAsync, ReadForKexAsync, cancellationToken);

            Observation.RekeysHandled++;
            request?.TrySetResult(again);
        }
        catch (Exception ex)
        {
            request?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>密钥交换期间的发送器 —— 走发送锁，与剧本泵互不打断。</summary>
    private async ValueTask SendForKexAsync(
        ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        await SendAsync(packet, cancellationToken);

    /// <summary>密钥交换期间的读取器：非传输层报文就地处理掉。</summary>
    /// <remarks>
    /// 重协商期间客户端仍然可以发通道数据（闸门只管它自己的发送方向），
    /// 所以这里遇到会话层报文要照常处理，不能报错也不能丢。
    /// </remarks>
    private async ValueTask<SshInboundPacket> ReadForKexAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            SshInboundPacket packet = await _transport.ReadPacketAsync(cancellationToken);

            if (packet.IsEndOfStream || (byte)packet.MessageNumber is >= 1 and <= 49)
            {
                return packet;
            }

            await HandleAsync(packet.MessageNumber, packet.Payload.ToArray(), cancellationToken);
        }
    }

    private async Task HandleAsync(SshMessageNumber number, byte[] payload, CancellationToken cancellationToken)
    {
        switch (number)
        {
            case SshMessageNumber.ChannelOpen:
                await OnChannelOpenAsync(payload, cancellationToken);
                return;

            case SshMessageNumber.ChannelRequest:
                await OnChannelRequestAsync(payload, cancellationToken);
                return;

            case SshMessageNumber.ChannelData:
                await OnChannelDataAsync(payload, cancellationToken);
                return;

            case SshMessageNumber.KexInit:
                await OnClientKexInitAsync(payload, cancellationToken);
                return;

            case SshMessageNumber.ChannelWindowAdjust:
                OnWindowAdjust(payload);
                return;

            case SshMessageNumber.ChannelEof:
                OnChannelEof(payload);
                return;

            case SshMessageNumber.ChannelClose:
                await OnChannelCloseAsync(payload, cancellationToken);
                return;

            case SshMessageNumber.GlobalRequest:
                await OnGlobalRequestAsync(payload, cancellationToken);
                return;

            case SshMessageNumber.ChannelOpenConfirmation:
                OnServerOpenAnswered(payload, accepted: true);
                return;

            case SshMessageNumber.ChannelOpenFailure:
                OnServerOpenAnswered(payload, accepted: false);
                return;

            default:
                return;
        }
    }

    // ------------------------------------------------------------ 通道开关

    private async Task OnChannelOpenAsync(byte[] payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpen);
        string channelType = reader.ReadUtf8String(MaxField);
        uint clientChannel = reader.ReadUInt32();
        uint clientWindow = reader.ReadUInt32();
        uint clientMaxPacket = reader.ReadUInt32();

        Observation.ClientAnnounced = (clientWindow, clientMaxPacket);

        bool isTunnel = channelType is SshAlgorithmNames.ChannelDirectTcpIp
            or SshAlgorithmNames.ChannelDirectStreamLocal;

        // 目标要在**任何 await 之前**读出来 —— SshDataReader 是 ref struct，
        // 跨不过 await 边界。
        string tunnelTarget = isTunnel ? ReadTunnelTarget(channelType, ref reader) : "";

        if (isTunnel && _script.TunnelHandler is null && _script.RejectOpenWith is null)
        {
            Observation.RejectedOpens++;

            ArrayBufferWriter<byte> denial = new();
            SshDataWriter denialWriter = new(denial);
            denialWriter.WriteMessageNumber(SshMessageNumber.ChannelOpenFailure);
            denialWriter.WriteUInt32(clientChannel);
            denialWriter.WriteUInt32((uint)_script.RejectTunnelWith);
            denialWriter.WriteUtf8String("测试服务端没有配隧道处理器。");
            denialWriter.WriteUtf8String("");
            await SendAsync(denial.WrittenMemory, cancellationToken);
            return;
        }

        if (_script.RejectOpenWith is { } reason)
        {
            Observation.RejectedOpens++;

            ArrayBufferWriter<byte> failure = new();
            SshDataWriter failureWriter = new(failure);
            failureWriter.WriteMessageNumber(SshMessageNumber.ChannelOpenFailure);
            failureWriter.WriteUInt32(clientChannel);
            failureWriter.WriteUInt32((uint)reason);
            failureWriter.WriteUtf8String("测试服务端按剧本拒绝。");
            failureWriter.WriteUtf8String("");
            await SendAsync(failure.WrittenMemory, cancellationToken);
            return;
        }

        uint serverChannel = _nextChannelId++;
        _peerIds[serverChannel] = clientChannel;
        lock (_sendWindowLock)
        {
            _sendWindow[serverChannel] = clientWindow;
        }
        _eofReceived[serverChannel] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
        writer.WriteUInt32(clientChannel);
        writer.WriteUInt32(serverChannel);
        writer.WriteUInt32((uint)_script.InitialWindow);
        writer.WriteUInt32((uint)_script.MaxPacket);
        await SendAsync(buffer.WrittenMemory, cancellationToken);

        if (isTunnel && _script.TunnelHandler is not null)
        {
            Observation.TunnelTargets.Add(tunnelTarget);

            StartHandler(
                serverChannel,
                (handlerInput, handlerOutput, token) =>
                    _script.TunnelHandler(tunnelTarget, handlerInput, handlerOutput, token),
                cancellationToken);
        }
    }

    /// <summary>读出隧道的目标。</summary>
    /// <remarks>
    /// <b>字段不全时返回空串，不抛异常。</b>有些用例会故意开一条不带类型字段的
    /// <c>direct-tcpip</c> 来测拒绝路径；桩要是在这里炸掉，服务端循环就死了，
    /// 客户端只会等到超时 —— 那会把「服务端拒绝了」伪装成「连接挂死」，
    /// 而后者排查起来完全是另一回事。
    /// </remarks>
    private static string ReadTunnelTarget(string channelType, scoped ref SshDataReader reader)
    {
        try
        {
            if (reader.IsEmpty)
            {
                return "";
            }

            if (channelType == SshAlgorithmNames.ChannelDirectStreamLocal)
            {
                return reader.ReadUtf8String(MaxField);
            }

            string host = reader.ReadUtf8String(MaxField);
            uint port = reader.ReadUInt32();
            return $"{host}:{port}";
        }
        catch (SshWireFormatException)
        {
            return "";
        }
    }

    private async Task OnChannelCloseAsync(byte[] payload, CancellationToken cancellationToken)
    {
        uint serverChannel = ReadRecipient(payload);
        Observation.ReceivedClose = true;

        if (_peerIds.TryGetValue(serverChannel, out uint clientChannel))
        {
            await SendSimpleAsync(SshMessageNumber.ChannelClose, clientChannel, cancellationToken);
            _peerIds.Remove(serverChannel);
        }
    }

    private void OnChannelEof(byte[] payload)
    {
        uint serverChannel = ReadRecipient(payload);
        Observation.ReceivedEof = true;
        if (_eofReceived.TryGetValue(serverChannel, out TaskCompletionSource? completion))
        {
            completion.TrySetResult();
        }

        // 客户端不再发了 —— 处理器那一侧也该读到结尾，否则它会一直等。
        if (_channelInput.TryGetValue(serverChannel, out Pipe? handlerInput))
        {
            handlerInput.Writer.Complete();
        }
    }

    // ------------------------------------------------------------ 请求

    private async Task OnChannelRequestAsync(byte[] payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelRequest);
        uint serverChannel = reader.ReadUInt32();
        string requestType = reader.ReadUtf8String(MaxField);
        bool wantReply = reader.ReadBoolean();

        Observation.Requests.Add(requestType);

        bool success = true;
        bool startScript = false;
        bool startSubsystem = false;

        switch (requestType)
        {
            case SshAlgorithmNames.RequestExec:
                Observation.Commands.Add(reader.ReadUtf8String(MaxField));
                success = !_script.RejectCommand;
                startScript = success;
                break;

            case SshAlgorithmNames.RequestShell:
                success = !_script.RejectCommand;
                startScript = success;
                break;

            case SshAlgorithmNames.RequestX11:
                Observation.X11Requests.Add(new TestX11Request(
                    SingleConnection: reader.ReadBoolean(),
                    AuthProtocol: reader.ReadUtf8String(MaxField),
                    AuthCookieHex: reader.ReadUtf8String(MaxField),
                    ScreenNumber: (int)reader.ReadUInt32()));
                success = _script.GrantX11Forward;
                break;

            case SshAlgorithmNames.RequestSubsystem:
                Observation.Subsystems.Add(reader.ReadUtf8String(MaxField));
                success = !_script.RejectCommand;
                startSubsystem = success && _script.SubsystemHandler is not null;
                startScript = success && !startSubsystem;
                break;

            case SshAlgorithmNames.RequestPty:
                ReadPtyRequest(ref reader);
                success = !_script.RejectPty;
                break;

            case SshAlgorithmNames.RequestWindowChange:
                Observation.WindowChanges.Add(ReadTerminalSize(ref reader));
                return;   // want_reply 必为假，不回

            case SshAlgorithmNames.RequestEnvironment:
                Observation.Environment[reader.ReadUtf8String(MaxField)] = reader.ReadUtf8String(MaxField);
                return;   // want_reply 必为假，不回

            case SshAlgorithmNames.RequestSignal:
                Observation.Signals.Add(reader.ReadUtf8String(MaxField));
                return;   // want_reply 必为假，不回

            case SshAlgorithmNames.RequestAuthAgent:
                Observation.AgentForwardRequests++;
                success = !_script.RejectAgentForward;
                break;

            default:
                success = false;
                break;
        }

        if (wantReply)
        {
            uint clientChannel = _peerIds.GetValueOrDefault(serverChannel);
            await SendSimpleAsync(
                success ? SshMessageNumber.ChannelSuccess : SshMessageNumber.ChannelFailure,
                clientChannel, cancellationToken);
        }

        if (startSubsystem)
        {
            StartSubsystem(serverChannel, cancellationToken);
        }

        if (startScript)
        {
            // 回放在后台跑：它可能要等窗口回补，而那要靠接收循环继续收包。
            _ = Task.Run(() => PlayScriptAsync(serverChannel, cancellationToken), cancellationToken);
        }
    }

    private void ReadPtyRequest(scoped ref SshDataReader reader)
    {
        string term = reader.ReadUtf8String(MaxField);
        TerminalSize size = ReadTerminalSize(ref reader);
        byte[] modes = reader.ReadStringAsArray(MaxField);
        Observation.PtyRequests.Add((term, size, modes));
    }

    private static TerminalSize ReadTerminalSize(scoped ref SshDataReader reader) =>
        new((int)reader.ReadUInt32(), (int)reader.ReadUInt32(),
            (int)reader.ReadUInt32(), (int)reader.ReadUInt32());

    private async Task OnGlobalRequestAsync(byte[] payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.GlobalRequest);
        string requestType = reader.ReadUtf8String(MaxField);
        Observation.GlobalRequests.Add(requestType);
        bool wantReply = reader.ReadBoolean();

        if (requestType == "tcpip-forward" && _script.GrantRemoteForwardPort > 0)
        {
            string bindAddress = reader.ReadUtf8String(MaxField);
            uint requestedPort = reader.ReadUInt32();
            Observation.RemoteForwardBinds.Add((bindAddress, (int)requestedPort));

            // 请求端口 0 时，**实际端口在 REQUEST_SUCCESS 的载荷里**。
            ArrayBufferWriter<byte> success = new();
            SshDataWriter successWriter = new(success);
            successWriter.WriteMessageNumber(SshMessageNumber.RequestSuccess);
            if (requestedPort == 0)
            {
                successWriter.WriteUInt32((uint)_script.GrantRemoteForwardPort);
            }
            await SendAsync(success.WrittenMemory, cancellationToken);
            return;
        }

        if (requestType == SshAlgorithmNames.RequestStreamLocalForward
            && _script.GrantStreamLocalForward)
        {
            string socketPath = reader.ReadUtf8String(MaxField);
            Observation.StreamLocalForwardBinds.Add(socketPath);

            // streamlocal 的 REQUEST_SUCCESS **没有载荷** —— 套接字路径是
            // 请求方给的，服务端不需要回一个「实际路径」（端口 0 那套不适用）。
            if (wantReply)
            {
                byte[] success = [(byte)SshMessageNumber.RequestSuccess];
                await SendAsync(success, cancellationToken);
            }
            return;
        }

        if (requestType is "cancel-tcpip-forward"
            or SshAlgorithmNames.RequestCancelStreamLocalForward)
        {
            if (wantReply)
            {
                byte[] success = [(byte)SshMessageNumber.RequestSuccess];
                await SendAsync(success, cancellationToken);
            }
            return;
        }

        if (wantReply)
        {
            // 不认识的全局请求回 FAILURE —— **必须回**，沉默会让 FIFO 永远错位。
            byte[] failure = [(byte)SshMessageNumber.RequestFailure];
            await SendAsync(failure, cancellationToken);
        }
    }

    private void StartSubsystem(uint serverChannel, CancellationToken cancellationToken) =>
        StartHandler(serverChannel, _script.SubsystemHandler!, cancellationToken);

    /// <summary>给一条通道挂上一个「拿字节流干活」的处理器。</summary>
    private void StartHandler(
        uint serverChannel,
        Func<PipeReader, PipeWriter, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        Pipe input = new(new PipeOptions(useSynchronizationContext: false));
        Pipe output = new(new PipeOptions(useSynchronizationContext: false));
        _channelInput[serverChannel] = input;
        _channelOutput[serverChannel] = output;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await handler(input.Reader, output.Writer, cancellationToken);
                }
                finally
                {
                    await output.Writer.CompleteAsync();
                }
            },
            cancellationToken);

        // 把处理器写出来的字节搬到 CHANNEL_DATA 上。
        _ = Task.Run(
            () => PumpHandlerOutputAsync(serverChannel, cancellationToken), cancellationToken);
    }

    private async Task PumpHandlerOutputAsync(uint serverChannel, CancellationToken cancellationToken)
    {
        PipeReader reader = _channelOutput[serverChannel].Reader;

        try
        {
            while (true)
            {
                ReadResult read = await reader.ReadAsync(cancellationToken);
                if (!read.Buffer.IsEmpty)
                {
                    await SendPlainDataAsync(serverChannel, read.Buffer.ToArray(), cancellationToken);
                }
                reader.AdvanceTo(read.Buffer.End);

                if (read.IsCompleted)
                {
                    break;
                }
            }

            // 处理器写完了 —— 发 EOF 与 CLOSE，让客户端那一侧能正常收场。
            if (_peerIds.TryGetValue(serverChannel, out uint clientChannel))
            {
                await SendSimpleAsync(SshMessageNumber.ChannelEof, clientChannel, cancellationToken);
                await SendSimpleAsync(SshMessageNumber.ChannelClose, clientChannel, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 测试收尾。
        }
        catch (Exception ex) when (Observation.ClientGone && ex is IOException or ObjectDisposedException)
        {
            // 拆场时序：客户端已经走了，最后一次回写落空。见 ClientGone 的说明。
        }
        catch (Exception ex)
        {
            // ⚠️ **这里以前是静默吞掉的，那是个坑。**
            //
            // 子系统的输出泵一死，EOF 与 CLOSE 就发不出去，客户端于是一直等，
            // 最后以「30 秒全局超时」收场 —— 而那条超时指不到这里。
            // 与 RunAsync / PlayScriptAsync 里那两处同一个毛病，这是第三处。
            Observation.SubsystemFault ??= ex;

            // 让客户端立刻收场，而不是挂到超时。
            if (_peerIds.TryGetValue(serverChannel, out uint peer))
            {
                try
                {
                    await SendSimpleAsync(SshMessageNumber.ChannelEof, peer, CancellationToken.None);
                    await SendSimpleAsync(SshMessageNumber.ChannelClose, peer, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 连收尾都发不出去 —— 传输已经没了。
                }
            }
        }
    }

    // ------------------------------------------------------------ 数据

    private async Task OnChannelDataAsync(byte[] payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelData);
        uint serverChannel = reader.ReadUInt32();
        byte[] data = reader.ReadStringAsArray(MaxField);

        Observation.StandardInput.AddRange(data);

        if (_channelInput.TryGetValue(serverChannel, out Pipe? handlerInput))
        {
            await handlerInput.Writer.WriteAsync(data, cancellationToken);
        }

        if (_script.EchoStandardInput)
        {
            await SendDataAsync(serverChannel, data, extended: false, cancellationToken);
        }

        // 服务端也要回补窗口，不然客户端发大量 stdin 时会停住。
        if (_peerIds.TryGetValue(serverChannel, out uint clientChannel))
        {
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteMessageNumber(SshMessageNumber.ChannelWindowAdjust);
            writer.WriteUInt32(clientChannel);
            writer.WriteUInt32((uint)data.Length);
            await SendAsync(buffer.WrittenMemory, cancellationToken);
        }
    }

    /// <summary>客户端对「服务端发起的通道」给了答复。</summary>
    private void OnServerOpenAnswered(byte[] payload, bool accepted)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();
        uint serverChannel = reader.ReadUInt32();

        if (!_pendingServerOpens.Remove(serverChannel, out TaskCompletionSource<bool>? completion))
        {
            return;
        }

        if (accepted)
        {
            uint clientChannel = reader.ReadUInt32();
            uint clientWindow = reader.ReadUInt32();
            uint clientMaxPacket = reader.ReadUInt32();

            _peerIds[serverChannel] = clientChannel;
            lock (_sendWindowLock)
            {
                _sendWindow[serverChannel] = clientWindow;
            }
            Observation.ClientAnnounced = (clientWindow, clientMaxPacket);
        }

        completion.TrySetResult(accepted);
    }

    private void OnWindowAdjust(byte[] payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelWindowAdjust);
        uint serverChannel = reader.ReadUInt32();
        uint bytes = reader.ReadUInt32();

        Observation.NoteWindowAdjust();
        Observation.WindowAdjustBytes += bytes;

        bool known;
        lock (_sendWindowLock)
        {
            known = _sendWindow.TryGetValue(serverChannel, out long current);
            if (known)
            {
                _sendWindow[serverChannel] = current + bytes;
            }
        }

        if (known)
        {
            _windowGate.Signal();
        }
    }

    // ------------------------------------------------------------ 回放

    private async Task PlayScriptAsync(uint serverChannel, CancellationToken cancellationToken)
    {
        try
        {
            if (_script.WaitForClientEof && _eofReceived.TryGetValue(serverChannel, out TaskCompletionSource? eof))
            {
                await eof.Task.WaitAsync(cancellationToken);
            }

            if (_script.StandardOutput.Length > 0)
            {
                await SendDataAsync(serverChannel, _script.StandardOutput, extended: false, cancellationToken);
            }

            if (_script.StandardError.Length > 0)
            {
                await SendDataAsync(serverChannel, _script.StandardError, extended: true, cancellationToken);
            }

            if (_script.UnknownExtendedData.Length > 0)
            {
                await SendExtendedAsync(
                    serverChannel, _script.UnknownExtendedData, dataTypeCode: 7, cancellationToken);
            }

            if (_script.ExitSignal is { } signal)
            {
                ArrayBufferWriter<byte> buffer = new();
                SshDataWriter writer = new(buffer);
                writer.WriteUtf8String(signal);
                writer.WriteBoolean(true);            // core dumped
                writer.WriteUtf8String("测试服务端按剧本发出的退出信号。");
                writer.WriteUtf8String("");
                await SendChannelRequestAsync(serverChannel, "exit-signal", buffer.WrittenMemory, cancellationToken);
            }
            else if (_script.ExitCode is { } code)
            {
                ArrayBufferWriter<byte> buffer = new();
                SshDataWriter writer = new(buffer);
                writer.WriteUInt32((uint)code);
                await SendChannelRequestAsync(serverChannel, "exit-status", buffer.WrittenMemory, cancellationToken);
            }

            if (_script.CloseAfterScript && _peerIds.TryGetValue(serverChannel, out uint clientChannel))
            {
                await SendSimpleAsync(SshMessageNumber.ChannelEof, clientChannel, cancellationToken);
                await SendSimpleAsync(SshMessageNumber.ChannelClose, clientChannel, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 测试收尾。
        }
        catch (Exception ex) when (Observation.ClientGone && ex is IOException or ObjectDisposedException)
        {
            // 拆场时序：客户端已经走了，最后一次回写落空。见 ClientGone 的说明。
        }
        catch (Exception ex)
        {
            // ⚠️ **剧本挂了必须说出来。**
            //
            // 原先这里只接 OperationCanceledException，别的异常会让这个 Task
            // 静静地 faulted 掉 —— 于是 EOF 与 CLOSE 永远发不出去，
            // 客户端那边就一直等，最后以「30 秒超时」收场。
            // 那是最难查的一种失败：**没有消息，没有栈，只有一个不返回的 await。**
            Observation.ScriptFault ??= ex;

            // 把通道收掉，让客户端立刻拿到 EOF 而不是挂满 30 秒。
            // 这样测试会以一句能看懂的断言失败，而不是超时。
            if (_peerIds.TryGetValue(serverChannel, out uint peer))
            {
                try
                {
                    await SendSimpleAsync(SshMessageNumber.ChannelEof, peer, CancellationToken.None);
                    await SendSimpleAsync(SshMessageNumber.ChannelClose, peer, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 连收尾都发不出去 —— 传输已经没了，没有别的补救。
                }
            }
        }
    }

    private Task SendDataAsync(
        uint serverChannel, byte[] data, bool extended, CancellationToken cancellationToken) =>
        extended
            ? SendExtendedAsync(serverChannel, data, dataTypeCode: 1, cancellationToken)
            : SendPlainDataAsync(serverChannel, data, cancellationToken);

    private async Task SendPlainDataAsync(uint serverChannel, byte[] data, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = await TakeSendWindowAsync(
                serverChannel, Math.Min(data.Length - offset, MaxSendChunk()), cancellationToken);

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteMessageNumber(SshMessageNumber.ChannelData);
            writer.WriteUInt32(_peerIds[serverChannel]);
            writer.WriteString(data.AsSpan(offset, chunk));
            await SendAsync(buffer.WrittenMemory, cancellationToken);

            offset += chunk;
        }
    }

    private async Task SendExtendedAsync(
        uint serverChannel, byte[] data, uint dataTypeCode, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = await TakeSendWindowAsync(
                serverChannel, Math.Min(data.Length - offset, MaxSendChunk()), cancellationToken);

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteMessageNumber(SshMessageNumber.ChannelExtendedData);
            writer.WriteUInt32(_peerIds[serverChannel]);
            writer.WriteUInt32(dataTypeCode);
            writer.WriteString(data.AsSpan(offset, chunk));
            await SendAsync(buffer.WrittenMemory, cancellationToken);

            offset += chunk;
        }
    }

    private int MaxSendChunk() => (int)Math.Min(Observation.ClientAnnounced.MaxPacket, int.MaxValue);

    /// <summary>等到客户端的接收窗口能放下东西，扣掉并返回这次能发多少。</summary>
    private async Task<int> TakeSendWindowAsync(uint serverChannel, int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            // 先取票、再查条件（AsyncGate 的纪律）：反过来会丢唤醒。
            Task ticket = _windowGate.NextChange();

            lock (_sendWindowLock)
            {
                long available = _sendWindow.GetValueOrDefault(serverChannel);
                if (available > 0)
                {
                    // **查与扣必须在同一个临界区里。** 分开写就等于没锁：
                    // 两条路径都会基于同一个 available 算出各自的新值。
                    int take = (int)Math.Min(wanted, available);
                    _sendWindow[serverChannel] = available - take;
                    return take;
                }
            }

            await ticket.WaitAsync(cancellationToken);
        }
    }

    private async Task SendChannelRequestAsync(
        uint serverChannel, string requestType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelRequest);
        writer.WriteUInt32(_peerIds[serverChannel]);
        writer.WriteUtf8String(requestType);
        writer.WriteBoolean(false);   // exit-status / exit-signal 的 want_reply 必为假
        writer.WriteRaw(payload.Span);
        await SendAsync(buffer.WrittenMemory, cancellationToken);
    }

    /// <summary>服务端主动向客户端发起一条通道，成功后给出一条读写它的流。</summary>
    /// <remarks>
    /// <c>forwarded-tcpip</c> 与 <c>auth-agent@openssh.com</c> 都是这个形状：
    /// <b>方向反过来</b> —— 服务端是发起方。
    /// </remarks>
    public async Task<Stream?> OpenChannelToClientAsync(
        string channelType, ReadOnlyMemory<byte> typeSpecific, CancellationToken cancellationToken)
    {
        uint serverChannel = _nextChannelId++;
        TaskCompletionSource<bool> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingServerOpens[serverChannel] = opened;

        Pipe toHandler = new(new PipeOptions(useSynchronizationContext: false));
        Pipe fromHandler = new(new PipeOptions(useSynchronizationContext: false));
        _channelInput[serverChannel] = toHandler;
        _channelOutput[serverChannel] = fromHandler;

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpen);
        writer.WriteUtf8String(channelType);
        writer.WriteUInt32(serverChannel);
        writer.WriteUInt32((uint)_script.InitialWindow);
        writer.WriteUInt32((uint)_script.MaxPacket);
        writer.WriteRaw(typeSpecific.Span);
        await SendAsync(buffer.WrittenMemory, cancellationToken);

        if (!await opened.Task.WaitAsync(cancellationToken))
        {
            return null;   // 客户端拒绝了
        }

        _ = Task.Run(() => PumpHandlerOutputAsync(serverChannel, cancellationToken), cancellationToken);
        return new PipeDuplexStream(toHandler.Reader, fromHandler.Writer);
    }

    /// <summary>把一对管道当成一条双工流用。</summary>
    private sealed class PipeDuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                ReadResult read = await reader.ReadAsync(cancellationToken);

                if (!read.Buffer.IsEmpty)
                {
                    int take = (int)Math.Min(read.Buffer.Length, buffer.Length);
                    read.Buffer.Slice(0, take).CopyTo(buffer.Span);
                    reader.AdvanceTo(read.Buffer.GetPosition(take));
                    return take;
                }

                reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);

                if (read.IsCompleted)
                {
                    return 0;
                }
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await writer.WriteAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken) =>
            await writer.FlushAsync(cancellationToken);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Complete();
                writer.Complete();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------ 出站

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            _transport.WritePacket(packet.Span);
            await _transport.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Task SendSimpleAsync(SshMessageNumber number, uint channel, CancellationToken cancellationToken)
    {
        byte[] packet = new byte[5];
        packet[0] = (byte)number;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), channel);
        return SendAsync(packet, cancellationToken);
    }

    private static uint ReadRecipient(byte[] payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();
        return reader.ReadUInt32();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (Pipe pipe in _channelInput.Values)
        {
            pipe.Writer.Complete();
        }
        foreach (Pipe pipe in _channelOutput.Values)
        {
            pipe.Writer.Complete();
        }
        _sendLock.Dispose();
    }

    /// <summary>与库内 AsyncGate 同样的「先取票、再查条件、后等票」模式。</summary>
    private sealed class AsyncGateBox
    {
        private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextChange() => Volatile.Read(ref _current).Task;

        public void Signal()
        {
            TaskCompletionSource previous = Interlocked.Exchange(
                ref _current, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            previous.TrySetResult();
        }
    }
}
