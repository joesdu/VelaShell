// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(resource-id-base / mask)、
//   附录 B「Syntactic Conventions」(回复:32 字节起、长度以 4 字节计;事件:恰好 32 字节;
//   错误:32 字节,含序号、出错的值、次 / 主操作码)

using System.Text;
using System.Threading.Channels;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;

namespace VelaShell.XServer.Server;

/// <summary>一个已连上的客户端。</summary>
/// <remarks>
/// 除 <see cref="Output" /> 之外的状态只在执行线程上读写。发给客户端的字节进 <see cref="Output" />,
/// 由连接的写出任务写到套接字 —— 执行线程从不在套接字上阻塞。
/// </remarks>
internal sealed class XClient : IDisposable
{
    /// <summary>每个客户端可用的资源 ID 位(21 位,约 200 万个)。</summary>
    public const uint ResourceMask = 0x001FFFFF;

    /// <summary>排队等写出的字节上限:客户端不读了(卡死、被挂起),超过就断开它,免得服务端内存无限增长。</summary>
    public const long MaxQueuedOutputBytes = 64L * 1024 * 1024;

    /// <summary>已读进来、还没执行的请求上限:读端到了上限就等执行线程消化,给发得太快的客户端施加背压。</summary>
    public const int MaxPendingRequests = 1024;

    /// <summary>
    /// 已读进来、还没执行的请求合计的字节上限。只数条数的话,1024 条 × 16 MB 的大请求就是 16 GB ——
    /// SYNC 的 Await、别人的 GrabServer 挂住的请求一直占着。一条请求本身比上限大(BIG-REQUESTS 最大 16 MB)时,
    /// 只要前面的都执行完了照样放行,不会卡死。只按客户端各自算,不设全局上限:全局上限会让 GrabServer 的持有者
    /// 连 UngrabServer 都发不进来(字节被别人挂住的请求占满了),就是死锁。
    /// </summary>
    public const long MaxPendingRequestBytes = 32L * 1024 * 1024;

    private readonly CancellationTokenSource _abort = new();
    private long _queuedBytes;
    private long _pendingRequestBytes;
    private TaskCompletionSource? _requestBytesWaiter;

    public XClient(int index, bool bigEndian)
    {
        Index = index;
        ResourceBase = (uint)index << 21;
        BigEndian = bigEndian;
    }

    public int Index { get; }

    /// <summary>宿主给这条连接起的名字(<see cref="X11Server.ServeAuthenticatedAsync(System.IO.Stream, string?, System.Threading.CancellationToken)" />);没给为 null。</summary>
    public string? Label { get; init; }

    public uint ResourceBase { get; }

    public bool BigEndian { get; }

    /// <summary>最近处理完的请求序号(低 16 位)。事件与错误都带它。</summary>
    public ushort Sequence { get; set; }

    public bool BigRequestsEnabled { get; set; }

    public bool Closed { get; set; }

    /// <summary>SetCloseDownMode:0 Destroy(默认),1 RetainPermanent,2 RetainTemporary。</summary>
    public byte CloseDownMode { get; set; }

    /// <summary>save-set(ChangeSaveSet):窗口 ID → XFIXES 的 target(挂到根窗口)与 map(补映射)。断开时见 X11Server.ProcessSaveSet。</summary>
    public Dictionary<uint, (bool ToRoot, bool Map)> SaveSet { get; } = [];

    /// <summary>这个客户端眼下拥有的窗口数(见 <c>X11Server.MaxWindowsPerClient</c>)。</summary>
    public int WindowCount { get; set; }

    /// <summary>记在这个客户端名下的内存(字节,见 <c>X11Server.ChargeMemory</c>);断开之后还没释放的(保留的资源、写在别人窗口上的属性)照样算。</summary>
    public long MemoryInUse { get; set; }

    // 最近几条请求的主、次操作码(主 << 16 | 次),环形覆盖;出错时一并打印,便于看出错前客户端在干什么。
    // 每条请求都记,所以记成整数 —— 拼字符串只在真要打印时做。
    private readonly uint[] _recentRequests = new uint[8];
    private int _requestCount;

    /// <summary>记下刚开始执行的一条请求。</summary>
    public void NoteRequest(byte major, ushort minor) => _recentRequests[_requestCount++ & 7] = ((uint)major << 16) | minor;

    /// <summary>最近几条请求(从早到晚),「主.次」以空格隔开。</summary>
    public string RecentRequests()
    {
        int count = Math.Min(_requestCount, _recentRequests.Length);
        StringBuilder text = new();
        for (int i = _requestCount - count; i < _requestCount; i++)
        {
            uint entry = _recentRequests[i & 7];
            text.Append(text.Length == 0 ? "" : " ").Append(entry >> 16).Append('.').Append(entry & 0xFFFF);
        }
        return text.ToString();
    }

    public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>读端每读进一条请求取一个名额,执行完还回来(见 <see cref="MaxPendingRequests" />)。</summary>
    public SemaphoreSlim PendingRequests { get; } = new(MaxPendingRequests);

    /// <summary>这个连接被服务端主动断开(KillClient、关窗、输出积压)时触发;连接的读写任务以它收工。</summary>
    public CancellationToken Aborted => _abort.Token;

    /// <summary>
    /// 立即断开:标记关闭、结束写出、取消连接上挂着的读写 —— 只 <c>TryComplete</c> 写出端的话,
    /// 读端还阻塞在套接字上,空闲的客户端永远不知道自己被断开了。
    /// </summary>
    public void Abort()
    {
        Closed = true;
        Output.Writer.TryComplete();
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 连接已经收完工。
        }
    }

    /// <summary>写出端取走了这么多字节(还没写完):从这一刻起它不再算「排队」。</summary>
    public void NoteWritten(long count) => Interlocked.Add(ref _queuedBytes, -count);

    /// <summary>
    /// 读端要读进一条 <paramref name="bytes" /> 字节的请求:未执行的请求合计超出 <see cref="MaxPendingRequestBytes" /> 时
    /// 先等执行线程消化(<see cref="ReleaseRequestBytes" />)。只由这个连接的读端调(单线程)。
    /// </summary>
    public async ValueTask ReserveRequestBytesAsync(int bytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Fits())
            {
                Interlocked.Add(ref _pendingRequestBytes, bytes);
                return;
            }
            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _requestBytesWaiter, waiter);
            if (Fits())   // 登记之后再看一眼:登记之前刚好放掉的那一次不会丢
            {
                Volatile.Write(ref _requestBytesWaiter, null);
                continue;
            }
            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        bool Fits()
        {
            long pending = Volatile.Read(ref _pendingRequestBytes);
            return pending == 0 || pending + bytes <= MaxPendingRequestBytes;
        }
    }

    /// <summary>一条请求执行了(或连接收工时丢掉了):把它占的字节还回去。执行线程上调。</summary>
    public void ReleaseRequestBytes(int bytes)
    {
        Interlocked.Add(ref _pendingRequestBytes, -bytes);
        Interlocked.Exchange(ref _requestBytesWaiter, null)?.TrySetResult();
    }

    /// <summary>连接结束时释放(断开的信号源与请求名额)。之后再 <see cref="Abort" /> 是空操作。</summary>
    /// <remarks>
    /// <see cref="PendingRequests" /> 故意不释放:连接收工后,已排进执行线程的请求还会执行、还会还名额;
    /// SemaphoreSlim 不用等待句柄时没有非托管资源,不释放没有代价。
    /// </remarks>
    public void Dispose() => _abort.Dispose();

    /// <summary>这个 ID 是不是在本客户端的资源范围内。</summary>
    public bool OwnsId(uint id) => (id & ~ResourceMask) == ResourceBase;

    public XWriter Writer(int capacity = 32) => new(BigEndian, capacity);

    /// <summary>
    /// 排一条消息等写出。还排着的(没交给套接字)已经到了 <see cref="MaxQueuedOutputBytes" />(客户端不读了)就断开它。
    /// 只看「之前排着的」:单条消息本身可以比上限大(三块 4K 横排时 <c>xwd -root</c> 的 GetImage 回复约 100 MB)——
    /// 原先按「加上这条之后」判,这样的回复整条连接被断,断开之前还白算了一遍。超出的部分最多一条消息,
    /// 而大回复本身另有上限(<c>X11Server.MaxImageReplyBytes</c>)。已经交给套接字的那一批不算:写出端一取走
    /// 就减(见 <c>X11Server.PumpOutputAsync</c>),否则那条大回复写出去要好一会儿,这期间到达的每条消息都会被
    /// 误判成积压,连接被白白判死。
    /// </summary>
    public void Send(byte[] bytes)
    {
        if (Closed)
        {
            return;
        }
        if (Interlocked.Add(ref _queuedBytes, bytes.Length) - bytes.Length >= MaxQueuedOutputBytes)
        {
            Abort();   // 客户端不读了:与其让内存涨到进程崩溃,不如断开它(X.Org 同样会断开写不出去的客户端)
            return;
        }
        Output.Writer.TryWrite(bytes);
    }

    // 回复、事件、错误先在一个写入器里拼好,再按实际长度拷出一份交给写出端。拼的那个按线程复用(几乎都在执行线程上),
    // 每条消息只分配交出去的那一份;拼到一半又要拼另一条(拼回复时发事件)的,借不到就另起一个。
    [ThreadStatic]
    private static XWriter? _scratch;

    /// <summary>复用的写入器最多留着这么大的缓冲:偶尔一条几 MB 的回复(GetImage)拼完就放掉,不一直占着。</summary>
    private const int MaxScratchCapacity = 64 * 1024;

    private XWriter Borrow()
    {
        XWriter? w = _scratch;
        _scratch = null;
        return w is null ? new XWriter(BigEndian, 64) : w.Reset(BigEndian);
    }

    /// <summary>拷出前 <paramref name="length" /> 字节,把写入器还回去。</summary>
    private static byte[] Finish(XWriter w, int length)
    {
        byte[] bytes = w.CopyPrefix(length);
        if (w.Capacity <= MaxScratchCapacity)
        {
            _scratch = w;
        }
        return bytes;
    }

    /// <summary>发一条回复:头(1、data、序号、长度)+ 由 <paramref name="body" /> 写的内容,补齐到至少 32 字节。</summary>
    public void Reply(byte data, Action<XWriter> body) => Reply(data, body, static (w, write) => write(w));

    /// <summary>
    /// 同 <see cref="Reply(byte, Action{XWriter})" />,内容所需的值经 <paramref name="state" /> 传进去:
    /// <paramref name="body" /> 写成不捕获变量的静态 lambda,就不必每条回复分配一个闭包(频繁的请求用)。
    /// </summary>
    public void Reply<TState>(byte data, TState state, Action<XWriter, TState> body)
    {
        XWriter w = Borrow();
        w.U8(1).U8(data).U16(Sequence).U32(0);
        body(w, state);
        if (w.Length < 32)
        {
            w.Zero(32 - w.Length);
        }
        w.Pad4();
        w.PatchU32(4, (uint)((w.Length - 32) / 4));
        Send(Finish(w, w.Length));
    }

    /// <summary>发一个事件:恰好 32 字节,由 <paramref name="body" /> 写序号之后的 28 字节。</summary>
    public void Event(byte code, byte detail, Action<XWriter> body, bool sent = false) =>
        Event(code, detail, body, static (w, write) => write(w), sent);

    /// <summary>同 <see cref="Event(byte, byte, Action{XWriter}, bool)" />,值经 <paramref name="state" /> 传进去(见带状态的 Reply)。</summary>
    public void Event<TState>(byte code, byte detail, TState state, Action<XWriter, TState> body, bool sent = false)
    {
        XWriter w = Borrow();
        w.U8((byte)(code | (sent ? XEventCode.SentFlag : 0))).U8(detail).U16(Sequence);
        body(w, state);
        if (w.Length < 32)
        {
            w.Zero(32 - w.Length);
        }
        Send(Finish(w, 32));
    }

    /// <summary>
    /// 发一个 GenericEvent(Generic Event Extension):32 字节头 —— 35、扩展主操作码、序号、额外长度、evtype ——
    /// 之后可以跟任意多的 4 字节单位。<paramref name="body" /> 从第 10 字节(evtype 之后)写起。
    /// </summary>
    public void GenericEvent(byte extension, ushort evtype, Action<XWriter> body)
    {
        XWriter w = Borrow();
        w.U8(XEventCode.GenericEvent).U8(extension).U16(Sequence).U32(0).U16(evtype);
        body(w);
        if (w.Length < 32)
        {
            w.Zero(32 - w.Length);
        }
        w.Pad4();
        w.PatchU32(4, (uint)((w.Length - 32) / 4));
        Send(Finish(w, w.Length));
    }

    /// <summary>经 Unix 套接字连进来、与服务端在同一个 IPC 命名空间里的(Linux,见 X11Server.SameIpcNamespace):MIT-SHM 只对这样的客户端可见。</summary>
    public bool SameHost { get; init; }

    /// <summary>经 <see cref="X11Server.ServeAuthenticatedAsync(System.IO.Stream, string?, System.Threading.CancellationToken)" /> 进来的(SSH 转发):<see cref="X11ServerOptions.RestrictForwardedClients" /> 管它。</summary>
    public bool Forwarded { get; init; }

    /// <summary>连接对端的 uid(Linux 上经 SO_PEERCRED、macOS / FreeBSD 上经 getpeereid 取得);取不到时为 null。MIT-SHM 按它核对段的访问权限。</summary>
    public uint? PeerUid { get; init; }

    /// <summary>
    /// 非受信客户端(SECURITY 扩展「SecurityClientUntrusted」):用 SecurityGenerateAuthorization 签出的非受信 cookie 连进来的,
    /// 或者宿主经 <see cref="X11Server.ServeAuthenticatedAsync(System.IO.Stream, string?, XClientTrust, System.Threading.CancellationToken)" />
    /// 指明非受信的(<c>ssh -X</c> 那一档)。它的请求按 SECURITY 规范第三章受限,见 <c>X11Server.Security.cs</c>。
    /// </summary>
    public bool Untrusted { get; init; }

    /// <summary>连进来时用的 SECURITY 授权(SecurityGenerateAuthorization 签的);用别的方式连进来的为 null。</summary>
    public SecurityAuthorization? Authorization { get; init; }

    /// <summary>
    /// 选了 PointerMotionHint 时已经发过提示的那个事件窗口,和发的时候的提示轮次(<c>X11Server</c> 在
    /// 按键 / 按钮变化、指针换窗口时推进轮次;QueryPointer / GetMotionEvents 清掉这一项)。
    /// </summary>
    public (object? Window, uint Epoch) MotionHint { get; set; }

    /// <summary>发一条错误。</summary>
    public void Error(XErrorCode code, uint badValue, ushort minorOpcode, byte majorOpcode)
    {
        XWriter w = Borrow();
        w.U8(0).U8((byte)code).U16(Sequence).U32(badValue).U16(minorOpcode).U8(majorOpcode).Zero(21);
        Send(Finish(w, 32));
    }

    public override string ToString() => Label is null ? $"client#{Index}" : $"client#{Index} ({Label})";
}
