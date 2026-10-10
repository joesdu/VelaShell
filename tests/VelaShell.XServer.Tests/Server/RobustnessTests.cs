using System.Buffers.Binary;
using System.Diagnostics;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>连接层的健壮性:主动断开真的断开、连接建立有时限、超大请求不会拖垮服务端。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RobustnessTests
{
    [TestMethod]
    public async Task 连接建立报文迟迟不发完_到了时限就断开()
    {
        await using X11Server server = new() { SetupTimeout = TimeSpan.FromMilliseconds(300) };
        (Stream serverSide, Stream clientSide) = DuplexPair.Create();
        Task serving = server.ServeAsync(serverSide, isLocal: true);
        await clientSide.WriteAsync(new byte[] { (byte)'l', 0, 11, 0 });   // 12 字节的头只发了 4 字节,然后不动了
        await clientSide.FlushAsync();
        // 这条连接的服务任务结束(TCP / Unix 套接字随之由接受循环关掉);没有时限的话它一直挂着。
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        // 按时发完的照常连上。
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Assert.IsTrue((await c.RequestAsync(43, 0)).IsReply, "GetInputFocus");
    }

    [TestMethod]
    public async Task 正在握手的连接有上限_超了的当场关掉_授权字段过长直接拒绝()
    {
        await using X11Server server = new();
        List<(Stream Client, Task Serving)> hanging = [];
        for (int i = 0; i < X11Server.MaxPendingSetups; i++)
        {
            (Stream serverSide, Stream clientSide) = DuplexPair.Create();
            hanging.Add((clientSide, server.ServeAsync(serverSide, isLocal: true)));   // 连上就不说话
        }
        (Stream extraServer, Stream extraClient) = DuplexPair.Create();
        await server.ServeAsync(extraServer, isLocal: true).WaitAsync(TimeSpan.FromSeconds(5));   // 第 33 条当场结束
        Assert.IsTrue(hanging.All(h => !h.Serving.IsCompleted), "前面的还在等握手");
        foreach ((Stream client, Task serving) in hanging)
        {
            await client.DisposeAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await extraClient.DisposeAsync();

        // 名额放回来了;授权数据 300 字节(MIT-MAGIC-COOKIE-1 只要 16):回连接失败,不按它给的长度分配。
        await using XTestClient tooLong = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: new byte[300]);
        Assert.AreEqual(0, tooLong.SetupReply[0], "Failed");
        await using XTestClient ok = await XTestClient.ConnectAsync(server);
        Assert.IsTrue((await ok.RequestAsync(43, 0)).IsReply);
    }

    [TestMethod]
    public async Task 零值配置不在TCP上听_配了cookie或显式打开才听()
    {
        // 原先 ListenTcp 默认 true、又不要 cookie:new X11Server() + StartAsync() 就是本机任何用户都能经环回 TCP 连进来记键盘、注入输入。
        int display = 0;
        for (int n = Random.Shared.Next(1000, 9000); display == 0; n++)
        {
            try
            {
                using System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 6000 + n);
                probe.Start();
                display = n;
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }
        List<string> lines = [];
        await using (X11Server bare = new(new X11ServerOptions { DisplayNumber = display, UnixSocketPath = "" }))
        {
            await bare.StartAsync();
            Assert.AreEqual(0, bare.Port, "没配 cookie:默认不听 TCP");
        }
        await using (X11Server withCookie = new(new X11ServerOptions { DisplayNumber = display, UnixSocketPath = "", AuthorizationCookie = new byte[16] }))
        {
            await withCookie.StartAsync();
            Assert.AreEqual(6000 + display, withCookie.Port, "配了 cookie:默认就听");
        }
        await using X11Server optIn = new(new X11ServerOptions { DisplayNumber = display, UnixSocketPath = "", ListenTcp = true, Log = lines.Add });
        await optIn.StartAsync();
        Assert.AreEqual(6000 + display, optIn.Port, "显式打开照样听");
        Assert.Contains(l => l.Contains("without a cookie", StringComparison.Ordinal), lines, "不配 cookie 听 TCP 时记一行提醒");
    }

    [TestMethod]
    public async Task Windows上TCP监听独占端口_别的进程用SO_REUSEADDR抢不走()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("SO_EXCLUSIVEADDRUSE 是 Windows 的语义;别的系统上 SO_REUSEADDR 本来就绑不上正在听的端口");
            return;
        }
        int display = 0;
        for (int n = Random.Shared.Next(1000, 9000); display == 0; n++)
        {
            try
            {
                using System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 6000 + n);
                probe.Start();
                display = n;
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }
        await using X11Server server = new(new X11ServerOptions { DisplayNumber = display, ListenTcp = true, UnixSocketPath = "" });
        await server.StartAsync();

        // 没设独占时,同一个用户的别的进程(比如低完整性的)用 SO_REUSEADDR 绑更具体的地址照样绑得上:听 0.0.0.0 的话,
        // 它绑 127.0.0.1 就截走了本机的连接(实测)。这里只听环回(不在测试里开对外的端口),所以核对的是监听套接字的选项。
        Assert.IsTrue(server.TcpListenerIsExclusive, "SO_EXCLUSIVEADDRUSE");
        using System.Net.Sockets.Socket thief = new(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        thief.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        Assert.ThrowsExactly<System.Net.Sockets.SocketException>(() => thief.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.Port)));
    }

    [TestMethod]
    public async Task KillClient之后被杀的客户端连接立即结束()
    {
        await using X11Server server = new();
        await using XTestClient killer = await XTestClient.ConnectAsync(server);
        await using XTestClient victim = await XTestClient.ConnectAsync(server);
        uint window = victim.NewId();
        await victim.SendAsync(1, 0, b => b.U32(window).U32(victim.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await victim.SyncAsync();

        await killer.SendAsync(113, 0, b => b.U32(window));   // KillClient
        await killer.SyncAsync();

        // 被杀的一方什么也不发 —— 以前服务端的读端会一直挂在它的连接上;现在 ServeAsync 立即结束(TCP / Unix 套接字随之关闭)。
        await victim.ServerTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task 超过输出队列上限的单条GetImage回复照样发出_超过回复上限的先回BadAlloc()
    {
        // 三块 4K 横排时 xwd -root 的回复约 100 MB:原先超过 64 MB 的输出队列上限,整条连接被断。这里用 8192 × 2160(约 71 MB)。
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 8192, ScreenHeight = 2160 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage image = await c.RequestAsync(73, 2, b => b.U32(c.RootWindow).I16(0).I16(0).U16(8192).U16(2160).U32(0xFFFFFFFF));
        Assert.IsTrue(image.IsReply, "GetImage(ZPixmap)的回复发出来了");
        Assert.AreEqual(8192u * 2160, image.U32(4), "长度(4 字节为单位)");
        await c.SyncAsync();   // 连接还在

        // 估出来超过 MaxImageReplyBytes 的:先回 BadAlloc,不去取像素、编码。
        await using X11Server huge = new(new X11ServerOptions { ScreenWidth = 20000, ScreenHeight = 20000 });
        await using XTestClient h = await XTestClient.ConnectAsync(huge);
        XMessage refused = await h.RequestAsync(73, 2, b => b.U32(h.RootWindow).I16(0).I16(0).U16(20000).U16(20000).U32(0xFFFFFFFF));
        Assert.IsTrue(refused.IsError);
        Assert.AreEqual(11, refused.Detail, "BadAlloc");
        await h.SyncAsync();
    }

    /// <summary>
    /// 单条大回复(<c>GetImage</c> 71 MB,超过 <c>XClient.MaxQueuedOutputBytes</c>)**写出去的途中**,
    /// 后面到达的消息照常排队:记帐要是等整批写完才减,这条回复飞行期间到达的每一条都会被当成
    /// 「客户端不读了」而把连接判死 —— 客户端读完大回复之后紧接着发的那条请求就永远收不到回复
    /// (run 37590196560 的 macOS 作业红的就是它)。
    /// </summary>
    /// <remarks>
    /// 客户端故意不把这条回复读完:写出端堵在管道上(对端不读),「大回复还在写」从一瞬间撑成稳态,
    /// 不必赌调度时机。
    /// 判据是连接还在不在,所以也不必把那 71 MB 读完。
    /// </remarks>
    [TestMethod]
    public async Task 大回复还在写出去的途中小请求照常排队_不被当成积压判死()
    {
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 8192, ScreenHeight = 2160 });
        (Stream serverSide, Stream clientSide) = DuplexPair.Create();
        Task serving = server.ServeAsync(serverSide, isLocal: true);

        // 建立报文(小端、协议 11.0、没有授权数据)与建立回复:之后客户端就不再读了。
        byte[] hello = [(byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        await clientSide.WriteAsync(hello);
        await clientSide.FlushAsync();
        byte[] head = new byte[8];
        await clientSide.ReadExactlyAsync(head);
        await clientSide.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) * 4]);

        // GetImage(ZPixmap,整屏,约 71 MB)。
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), X11Server.RootWindowId);   // drawable;x / y 都是 0
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 8192);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), 2160);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 0xFFFFFFFF);   // plane-mask
        await clientSide.WriteAsync(Request(73, 2, payload));
        await clientSide.FlushAsync();

        // 读 4 KB:证明写出端已经在写这条大回复 —— 也就是已经把它从队列里取走了。
        await clientSide.ReadExactlyAsync(new byte[4096]);

        // 紧接着发一条 GetInputFocus。它到达时大回复还在写:回它的时候不能把连接判死。
        await clientSide.WriteAsync(Request(43, 0, []));
        await clientSide.FlushAsync();
        await Task.Delay(300);   // 等服务端读完这条请求、执行、回出去
        IReadOnlyList<XClientInfo> clients = await server.GetClientsAsync();
        Assert.AreEqual(1, clients.Count, "大回复还在写的时候,紧跟着的请求没有被当成积压");

        await clientSide.DisposeAsync();
        await serverSide.DisposeAsync();
        try
        {
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 写出端正堵在被丢掉的管道上:收尾时的异常不关心。
        }
    }

    /// <summary>一条请求:主 / 次操作码 + 以 4 字节计的长度 + 正文(这里的两条正文都已是 4 的倍数)。</summary>
    private static byte[] Request(byte opcode, byte data, byte[] body)
    {
        byte[] request = new byte[4 + body.Length];
        request[0] = opcode;
        request[1] = data;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)((body.Length + 4) / 4));
        body.CopyTo(request, 4);
        return request;
    }

    [TestMethod]
    public async Task 超大尺寸的PutImage与CopyArea回错误而不是耗尽内存()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(16).U16(16));
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0));

        // PutImage 声称 65535×65535,只带 4 字节数据。
        XMessage put = await c.RequestAsync(72, 2, b => b.U32(pixmap).U32(gc).U16(65535).U16(65535).I16(0).I16(0).U8(0).U8(24).U16(0).U32(0));
        Assert.IsTrue(put.IsError, "数据不够:BadLength");

        // CopyArea 65535×65535:只拷与源、目标相交的部分,不分配请求尺寸的缓冲。
        await c.SendAsync(62, 0, b => b.U32(pixmap).U32(pixmap).U32(gc).I16(0).I16(0).I16(1).I16(1).U16(65535).U16(65535));
        await c.SyncAsync();   // 服务端还活着、还在回应
    }

    private const byte BadAlloc = 11;

    private static (byte, byte, Action<XTestClient.Body>?) CreateWindow(uint id, uint parent) =>
        (1, 0, b => b.U32(id).U32(parent).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));

    [TestMethod]
    public async Task 窗口嵌套到上限之后再建或挪进去回BadAlloc_整条链照常映射与销毁()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        List<uint> chain = [];
        for (int level = 1; level <= X11Server.MaxWindowDepth; level++)
        {
            uint id = c.NewId();
            chain.Add(id);
        }
        await c.SendManyAsync(chain.Select((id, i) => CreateWindow(id, i == 0 ? c.RootWindow : chain[i - 1])));
        // 从外往里逐个映射、再从里往外逐个取消映射:每次只该重画那个窗口(或父窗口)的子树。原先从顶层走起,
        // 每个窗口的可见区域都要沿祖先算一遍 —— 映射一个 d 层深的窗口是 O(d²),整条链 O(n³),256 层要好几秒。
        var elapsed = Stopwatch.StartNew();
        await c.SendManyAsync(chain.Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (8, 0, b => b.U32(id))));   // MapWindow
        await c.SyncAsync();
        await c.SendManyAsync(Enumerable.Reverse(chain).Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (10, 0, b => b.U32(id))));   // UnmapWindow
        await c.SendManyAsync(chain.Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (8, 0, b => b.U32(id))));
        await c.SyncAsync();
        Assert.IsLessThan(3000, elapsed.ElapsedMilliseconds, $"整条链映射、取消映射、再映射用了 {elapsed.ElapsedMilliseconds} ms");

        (byte op, byte data, Action<XTestClient.Body>? body) = CreateWindow(c.NewId(), chain[^1]);
        XMessage tooDeep = await c.RequestAsync(op, data, body);
        Assert.IsTrue(tooDeep.IsError);
        Assert.AreEqual(BadAlloc, tooDeep.Detail);

        // 一棵两层的小树挪到倒数第二层下面:整棵落下去就是第 257 层。
        uint small = c.NewId(), leaf = c.NewId();
        await c.SendManyAsync([CreateWindow(small, c.RootWindow), CreateWindow(leaf, small)]);
        XMessage reparent = await c.RequestAsync(7, 0, b => b.U32(small).U32(chain[^2]).I16(0).I16(0));
        Assert.IsTrue(reparent.IsError);
        Assert.AreEqual(BadAlloc, reparent.Detail);

        await c.SendAsync(4, 0, b => b.U32(chain[0]));   // DestroyWindow:整条链从最深处开始销毁
        XMessage gone = await c.RequestAsync(14, 0, b => b.U32(chain[^1]));   // GetGeometry
        Assert.IsTrue(gone.IsError, "最深的那个窗口随链首一起销毁了");
    }

    [TestMethod]
    public async Task 一个客户端的窗口数到上限之后回BadAlloc_销毁一个就能再建()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = c.NewId();
        List<uint> children = [.. Enumerable.Range(0, X11Server.MaxWindowsPerClient - 1).Select(_ => c.NewId())];
        await c.SendManyAsync([CreateWindow(top, c.RootWindow), .. children.Select(id => CreateWindow(id, top))]);
        await c.SyncAsync();

        (byte op, byte data, Action<XTestClient.Body>? body) = CreateWindow(c.NewId(), top);
        XMessage full = await c.RequestAsync(op, data, body);
        Assert.IsTrue(full.IsError);
        Assert.AreEqual(BadAlloc, full.Detail);

        await c.SendAsync(4, 0, b => b.U32(children[0]));
        uint again = c.NewId();
        (op, data, body) = CreateWindow(again, top);
        await c.SendAsync(op, data, body);
        XMessage geometry = await c.RequestAsync(14, 0, b => b.U32(again));
        Assert.IsTrue(geometry.IsReply, "腾出一个名额之后照常建窗口");
    }

    [TestMethod]
    public async Task 客户端连满之后新连接收到建立失败_走了一个就能再连_资源ID顶上三位恒为0()
    {
        await using X11Server server = new();
        List<XTestClient> clients = [];
        try
        {
            for (int i = 0; i < X11Server.MaxClients; i++)
            {
                XTestClient c = await XTestClient.ConnectAsync(server);
                Assert.AreEqual(1, c.SetupReply[0], $"第 {i + 1} 个连接应当成功");
                Assert.AreEqual(0u, c.ResourceBase & 0xE0000000, "资源 ID 的顶上三位恒为 0(协议第 8 节)");
                clients.Add(c);
            }

            await using (XTestClient refused = await XTestClient.ConnectAsync(server))
            {
                Assert.AreEqual(0, refused.SetupReply[0], "编号用完:连接建立失败,而不是卡住");
            }

            await clients[0].DisposeAsync();
            await clients[0].ServerTask.WaitAsync(TimeSpan.FromSeconds(3));
            clients.RemoveAt(0);
            XTestClient again = await XTestClient.ConnectAsync(server);
            clients.Add(again);
            Assert.AreEqual(1, again.SetupReply[0], "走了一个就空出一个编号");
        }
        finally
        {
            foreach (XTestClient c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task 断开的客户端编号刚分给新客户端时_连接收尾的第二次断开不会摘掉新客户端()
    {
        // KillClient(这里经宿主的 DisconnectClient)当场断开一次,连接收尾时再排一次;编号在中间分给了新客户端的话,
        // 原先按编号摘,第二次就把新客户端摘掉了 —— 它照样收发,编号却又能分出去,两个客户端的资源 ID 范围撞在一起。
        await using X11Server server = new();
        List<XTestClient> clients = [];
        try
        {
            for (int i = 0; i < X11Server.MaxClients; i++)
            {
                clients.Add(await XTestClient.ConnectAsync(server));   // 编号 1–255 用满,下一个从 1 往后找
            }
            XTestClient victim = clients[^1];
            int victimId = (int)(victim.ResourceBase >> 21);
            Assert.AreEqual(X11Server.MaxClients, victimId);

            using SemaphoreSlim gate = new(0);
            Task<bool> blocker = server.InvokeAsync(() => gate.Wait(TimeSpan.FromSeconds(10)));   // 先占住执行线程
            server.DisconnectClient(victimId);                                                    // 排在后面:断开 victim
            Task<XTestClient> newcomer = XTestClient.ConnectAsync(server);                         // 再后面:新客户端登记
            await Task.Delay(300);   // 让新客户端的建立报文读进来、登记排进队列(victim 的收尾要等它被断开之后才排)
            gate.Release();
            Assert.IsTrue(await blocker);
            XTestClient c = await newcomer.WaitAsync(TimeSpan.FromSeconds(5));
            clients.Add(c);
            Assert.AreEqual(1, c.SetupReply[0]);
            Assert.AreEqual(victim.ResourceBase, c.ResourceBase, "新客户端拿到的正是刚空出来的编号");
            await victim.ServerTask.WaitAsync(TimeSpan.FromSeconds(3));   // victim 的连接收完了工(第二次断开已经执行)
            await c.SyncAsync();

            IReadOnlyList<XClientInfo> listed = await server.GetClientsAsync();
            Assert.Contains(info => info.Id == victimId && !info.Retained, listed, "新客户端还在册");
            await using XTestClient extra = await XTestClient.ConnectAsync(server);
            Assert.AreEqual(0, extra.SetupReply[0], "编号仍是满的:不会再分出一个同样的资源 ID 范围");
        }
        finally
        {
            foreach (XTestClient c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task 等执行线程登记时调用方取消了_登记成的客户端随即断开不占编号()
    {
        await using X11Server server = new();
        using SemaphoreSlim gate = new(0);
        Task<bool> blocker = server.InvokeAsync(() => gate.Wait(TimeSpan.FromSeconds(10)));   // 占住执行线程:登记排在后面

        using CancellationTokenSource cancel = new();
        (Stream serverSide, Stream clientSide) = DuplexPair.Create();
        Task serving = server.ServeAsync(serverSide, isLocal: true, cancel.Token);
        await clientSide.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await clientSide.FlushAsync();
        await Task.Delay(300);   // 建立报文读进来了,登记排进了队列
        await cancel.CancelAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));   // ServeAsync 随取消结束

        gate.Release();
        Assert.IsTrue(await blocker);
        IReadOnlyList<XClientInfo> clients = [];
        for (int i = 0; i < 50 && (clients = await server.GetClientsAsync()).Count != 0; i++)
        {
            await Task.Delay(20);   // 断开是登记之后再排的一项
        }
        Assert.IsEmpty(clients, "原先登记照样执行、没人收拾,永久占着一个编号");
        await clientSide.DisposeAsync();
    }

    [TestMethod]
    public async Task 协议错误日志在放掉像素锁之后才交给宿主_刷屏时每秒只记五十条()
    {
        using RecordingHost host = new();
        XTopLevelWindow? handle = null;
        int logged = 0, loggedUnderLock = 0;
        System.Collections.Concurrent.ConcurrentQueue<string> lines = new();
        await using X11Server server = new(new X11ServerOptions
        {
            Log = line =>
            {
                lines.Enqueue(line);
                if (!line.Contains("BadWindow", StringComparison.Ordinal) || handle is not { } window)
                {
                    return;
                }
                Interlocked.Increment(ref logged);
                // 别的线程去读像素:日志要是在持锁时调的,这里就拿不到锁(宿主的 UI 线程就是这么被拖住的)。
                // 抓到一次就够了,之后不再试(否则每条都要等满 1 秒)。
                if (Volatile.Read(ref loggedUnderLock) == 0 && !Task.Run(() => window.CopyPixels(new uint[16 * 16])).Wait(TimeSpan.FromSeconds(1)))
                {
                    Interlocked.Increment(ref loggedUnderLock);
                }
            },
        }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(top).U32(c.RootWindow).I16(0).I16(0).U16(16).U16(16).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        handle = host.Mapped[top];

        // 500 条 MapWindow(不存在的窗口):每条一个 BadWindow。
        await c.SendManyAsync(Enumerable.Range(0, 500).Select<int, (byte, byte, Action<XTestClient.Body>?)>(_ => (8, 0, b => b.U32(0x7FFFFF))));
        await c.SyncAsync();
        Assert.AreEqual(0, loggedUnderLock, "没有一条日志是持着像素锁交出去的");
        Assert.IsLessThanOrEqualTo(100, logged, $"刷屏的错误每秒最多记 50 条,实际 {logged}");

        await Task.Delay(1100);
        await c.RequestAsync(8, 0, b => b.U32(0x7FFFFF));
        Assert.Contains(l => l.Contains("more log lines were not written", StringComparison.Ordinal), lines, "补一行没记的有几条");
    }

    [TestMethod]
    public async Task 宿主的日志委托抛异常不会拖垮执行循环_收工照常()
    {
        // 宿主回调抛异常 → 记「host callback failed」→ 宿主的日志也抛:原先这一下在放锁之后直接调、没人接,
        // 执行循环随之退出,之后所有客户端都卡住,DisposeAsync 也在等执行循环时重抛。
        int logCalls = 0;
        X11Server server = new(new X11ServerOptions
        {
            Log = _ =>
            {
                Interlocked.Increment(ref logCalls);
                throw new InvalidOperationException("the host's log is broken");
            },
        }, new BellThrowingHost());
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendAsync(104, 0);   // Bell:宿主的 BellRequested 抛异常
        await c.SyncAsync();
        await Task.Delay(50);
        await c.SendAsync(104, 0);
        await c.SyncAsync();
        Assert.IsGreaterThan(0, Volatile.Read(ref logCalls), "宿主的日志确实被调到了(并抛了异常)");
        Assert.IsTrue((await c.RequestAsync(43, 0)).IsReply, "执行循环还在跑");
        await server.DisposeAsync();   // 不抛
    }

    /// <summary>BellRequested 抛异常的宿主,其余什么也不做。</summary>
    private sealed class BellThrowingHost : IX11ServerHost
    {
        public void TopLevelMapped(XTopLevelWindow window)
        {
        }

        public void TopLevelUnmapped(XTopLevelWindow window)
        {
        }

        public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes)
        {
        }

        public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
        {
        }

        public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
        {
        }

        public void BellRequested(int volume) => throw new InvalidOperationException("the host's bell is broken");

        public void ClipboardChanged(string text)
        {
        }
    }

    [TestMethod]
    public async Task 客户端给的字符串进日志前去掉控制字符并截断_刷屏的日志按字节限额()
    {
        System.Collections.Concurrent.ConcurrentQueue<string> lines = new();
        await using X11Server server = new(new X11ServerOptions { Log = lines.Enqueue });
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        // OpenFont 的名字是客户端给的(最长 64 KB、什么字节都能带):换行能伪造日志行,ESC 能往看日志的终端里注入控制序列。
        string name = "no-such-font\n2026-10-06 [XServer] forged line\u001b[2J" + new string('x', 4000);
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(name);
        await c.SendManyAsync(Enumerable.Range(0, 200).Select<int, (byte, byte, Action<XTestClient.Body>?)>(_ =>
            (45, 0, b => b.U32(c.NewId()).U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad())));
        await c.SyncAsync();

        string[] fontLines = [.. lines.Where(l => l.Contains("OpenFont", StringComparison.Ordinal))];
        Assert.IsNotEmpty(fontLines);
        foreach (string line in fontLines)
        {
            Assert.IsFalse(line.Any(ch => ch < 0x20), "日志行里没有换行、ESC 之类的控制字符");
            Assert.Contains(@"\x0A", line, "控制字符写成转义");
            Assert.IsLessThan(700, line.Length, "客户端给的长字符串截断了");
        }
        // 原先每秒 50 条 × 4 KB 照记:一秒 200 KB。现在先给 32 KB 的余量,之后每秒补 256 字节。
        long logged = lines.Sum(l => (long)l.Length);
        Assert.IsLessThan(48 * 1024L, logged, $"刷屏的日志按字节限额,实际 {logged} 个字符");
    }

    [TestMethod]
    public async Task 像素图超过像素上限回BadAlloc_顶层缓冲只保留上限之内的一块()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        // 32767² 的像素图:一块就是 4 GB。
        XMessage pixmap = await c.RequestAsync(53, 24, b => b.U32(c.NewId()).U32(c.RootWindow).U16(32767).U16(32767));
        Assert.IsTrue(pixmap.IsError);
        Assert.AreEqual(BadAlloc, pixmap.Detail);

        // 32767² 的顶层窗口照样建、照样映射(协议允许),缓冲只保留上限之内的一块。
        uint top = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(top).U32(c.RootWindow).I16(0).I16(0).U16(32767).U16(32767).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        (int w, int h) = (0, 0);
        Assert.IsTrue(host.Mapped[top].ReadPixels((_, width, height) => (w, h) = (width, height)));
        Assert.AreEqual(32767, w);
        Assert.IsLessThanOrEqualTo(XServer.Drawing.PixelBuffer.MaxPixels, (long)w * h, $"缓冲 {w}×{h}");

        // 把一个小顶层配置成 32767²:同样削到上限之内。
        uint small = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(small).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(small));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(small));
        await c.SendAsync(12, 0, b => b.U32(small).U16(0x0C).U16(0).U32(32767).U32(32767));   // ConfigureWindow 宽、高
        await c.SyncAsync();
        Assert.IsTrue(host.Mapped[small].ReadPixels((_, width, height) => (w, h) = (width, height)));
        Assert.IsLessThanOrEqualTo(XServer.Drawing.PixelBuffer.MaxPixels, (long)w * h, $"缓冲 {w}×{h}");
    }

    [TestMethod]
    public async Task InternAtom的名字合计超上限回BadAlloc_已有的原子照常取得到()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        int max = (int)(X11Server.MaxAtomNameBytes / 65535) + 2;
        XMessage? refused = null;
        for (int i = 0; i < max && refused is null; i++)
        {
            byte[] name = new byte[65535];
            Array.Fill(name, (byte)'a');
            BitConverter.GetBytes(i).CopyTo(name, 0);
            XMessage reply = await c.RequestAsync(16, 0, b => b.U16(65535).U16(0).Bytes(name));
            refused = reply.IsError ? reply : null;
        }
        Assert.IsNotNull(refused, "原子名合计超过上限之后应当拒绝");
        Assert.AreEqual(BadAlloc, refused.Detail);

        XMessage wmName = await c.RequestAsync(16, 0, b => b.U16(7).U16(0).Bytes("WM_NAME"u8.ToArray()));
        Assert.AreEqual(39u, wmName.U32(8), "已经有的原子照常返回(预定义的 WM_NAME)");
    }

    [TestMethod]
    public async Task ChangeProperty追加到超过属性上限回BadAlloc_原来的值不变()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte bigRequests = (await c.RequestAsync(98, 0, b => b.U16(12).U16(0).Bytes("BIG-REQUESTS"u8.ToArray()))).Bytes[9];
        await c.RequestAsync(bigRequests, 0);
        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));

        byte[] chunk = new byte[15 * 1024 * 1024];
        await c.SendAsync(18, 0, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)chunk.Length).Bytes(chunk), bigRequest: true);
        await c.SendAsync(18, 2, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)chunk.Length).Bytes(chunk), bigRequest: true);
        ushort third = await c.SendAsync(18, 2, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)chunk.Length).Bytes(chunk), bigRequest: true);
        XMessage error = await c.NextAsync(m => m.IsError && m.Sequence == third);
        Assert.AreEqual(BadAlloc, error.Detail, "45 MB 超过了 32 MB 的上限");

        XMessage property = await c.RequestAsync(20, 0, b => b.U32(window).U32(39).U32(0).U32(0).U32(0));   // GetProperty,长度 0:只看 bytes-after
        Assert.AreEqual((uint)(2 * chunk.Length), property.U32(12), "前两次追加照常生效");
    }

    [TestMethod]
    public async Task CloseDownMode为Retain时断开后资源留着_KillClient与AllTemporary销毁它们()
    {
        await using X11Server server = new();
        await using XTestClient observer = await XTestClient.ConnectAsync(server);

        async Task<uint> LeaveBehindAsync(byte mode)
        {
            XTestClient c = await XTestClient.ConnectAsync(server);
            uint pixmap = c.NewId();
            await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(8).U16(8));
            await c.SendAsync(112, mode, _ => { });   // SetCloseDownMode
            await c.SyncAsync();
            await c.DisconnectAsync(server);
            return pixmap;
        }
        async Task<bool> ExistsAsync(uint id) => (await observer.RequestAsync(14, 0, b => b.U32(id))).IsReply;   // GetGeometry

        uint permanent = await LeaveBehindAsync(1), temporary = await LeaveBehindAsync(2), destroyed = await LeaveBehindAsync(0);
        Assert.IsTrue(await ExistsAsync(permanent), "RetainPermanent:断开后资源还在");
        Assert.IsTrue(await ExistsAsync(temporary), "RetainTemporary:断开后资源还在");
        Assert.IsFalse(await ExistsAsync(destroyed), "Destroy:照常销毁");

        await observer.SendAsync(113, 0, b => b.U32(0));   // KillClient(AllTemporary)
        Assert.IsFalse(await ExistsAsync(temporary), "AllTemporary 销毁 RetainTemporary 留下的");
        Assert.IsTrue(await ExistsAsync(permanent), "RetainPermanent 的不受影响");

        await observer.SendAsync(113, 0, b => b.U32(permanent));   // KillClient(那个资源):销毁它的客户端留下的全部资源
        Assert.IsFalse(await ExistsAsync(permanent));
    }

    [TestMethod]
    public async Task Retain模式断开后扩展里挂在资源上的状态照常工作_KillClient时才清()
    {
        // 原先断开时就调 ClientClosed:DAMAGE 把还在资源表里的损伤对象摘了(从此不再累积),KillClient 时又调一次。
        await using X11Server server = new();
        await using XTestClient observer = await XTestClient.ConnectAsync(server);
        async Task<byte> MajorAsync(string name)
        {
            byte[] bytes = System.Text.Encoding.Latin1.GetBytes(name);
            return (await observer.RequestAsync(98, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad())).Bytes[9];
        }
        byte damage = await MajorAsync("DAMAGE"), xfixes = await MajorAsync("XFIXES");

        XTestClient retainer = await XTestClient.ConnectAsync(server);
        uint pixmap = retainer.NewId(), damageId = retainer.NewId();
        await retainer.SendAsync(53, 24, b => b.U32(pixmap).U32(retainer.RootWindow).U16(8).U16(8));   // CreatePixmap
        await retainer.SendAsync(damage, 1, b => b.U32(damageId).U32(pixmap).U8(0).U8(0).U8(0).U8(0));  // DamageCreate(RawRectangles)
        await retainer.SendAsync(112, 1);   // SetCloseDownMode(RetainPermanent)
        await retainer.SyncAsync();
        await retainer.DisconnectAsync(server);

        // 别的客户端往留下来的像素图上画:损伤照常累积在留下来的损伤对象上。
        uint gc = observer.NewId(), region = observer.NewId();
        await observer.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0));   // CreateGC
        await observer.SendAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(2).I16(2).U16(3).U16(3));   // PolyFillRectangle
        await observer.SendAsync(xfixes, 5, b => b.U32(region));   // CreateRegion(空)
        await observer.SendAsync(damage, 3, b => b.U32(damageId).U32(0).U32(region));   // DamageSubtract(repair = None, parts = region)
        XMessage fetched = await observer.RequestAsync(xfixes, 19, b => b.U32(region));   // FetchRegion
        Assert.IsTrue(fetched.IsReply);
        Assert.AreEqual((2, 2, 3, 3), (fetched.I16(8), fetched.I16(10), fetched.U16(12), fetched.U16(14)), "留下来的损伤对象还在累积");

        // KillClient:资源连同损伤对象一并销毁。
        await observer.SendAsync(113, 0, b => b.U32(pixmap));
        XMessage gone = await observer.RequestAsync(damage, 3, b => b.U32(damageId).U32(0).U32(0));
        Assert.IsTrue(gone.IsError, "损伤对象随 KillClient 销毁了");
    }

    /// <summary>
    /// 测试工具 <see cref="XTestClient.DisconnectAsync" /> 的保证:客户端那条队里还排着一批请求时断开,连接收尾排在它们后面,
    /// 宿主那条队的查询先跑 —— 只等 ServeAsync 返回就去看,看到的是收尾之前的状态(慢的 CI 机器上偶尔红,这里让它必然出现);
    /// DisconnectAsync 等到收尾做完才返回。
    /// </summary>
    [TestMethod]
    public async Task 断开时客户端的队里还排着请求_DisconnectAsync等到收尾做完才返回()
    {
        await using X11Server server = new();
        await using XTestClient observer = await XTestClient.ConnectAsync(server);
        XTestClient c = await XTestClient.ConnectAsync(server);
        uint pixmap = c.NewId(), gc = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(2000).U16(2000));   // CreatePixmap
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4).U32(0xFF0000));             // CreateGC
        await c.SyncAsync();
        for (int i = 0; i < 300; i++)
        {
            await c.SendAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(0).I16(0).U16(2000).U16(2000));   // PolyFillRectangle:排在连接收尾前面
        }

        await c.DisconnectAsync(server);

        Assert.HasCount(1, await server.GetClientsAsync(), "收尾做完了:只剩 observer");
        Assert.IsTrue((await observer.RequestAsync(14, 0, b => b.U32(pixmap))).IsError, "它的像素图销毁了");   // GetGeometry
    }

    [TestMethod]
    public async Task SetCloseDownMode的取值超出0到2回BadValue()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendAsync(112, 3);   // SetCloseDownMode(3):没有这个模式
        XMessage error = await c.NextAsync(m => m.IsError);
        Assert.AreEqual(2, error.Detail, "BadValue");
        Assert.AreEqual(3u, error.U32(4), "出错的值");
        Assert.AreEqual(112, error.Bytes[10], "主操作码");
    }

    [TestMethod]
    public async Task 保留资源的客户端有上限_超了的照Destroy处理_XRes列得出保留的()
    {
        // 原先不设限:循环「连上 → RetainPermanent → 断开」254 次就占满了编号,之后谁都连不上,也就发不了 KillClient。
        await using X11Server server = new();
        await using XTestClient observer = await XTestClient.ConnectAsync(server);
        List<(uint Base, uint Pixmap)> left = [];
        for (int i = 0; i <= X11Server.MaxRetainedClients; i++)
        {
            XTestClient c = await XTestClient.ConnectAsync(server);
            uint pixmap = c.NewId();
            await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(8).U16(8));
            await c.SendAsync(112, 1);   // SetCloseDownMode(RetainPermanent)
            await c.SyncAsync();
            await c.DisconnectAsync(server);
            left.Add((c.ResourceBase, pixmap));
        }
        await observer.SyncAsync();
        async Task<bool> ExistsAsync(uint id) => (await observer.RequestAsync(14, 0, b => b.U32(id))).IsReply;   // GetGeometry

        Assert.IsTrue(await ExistsAsync(left[X11Server.MaxRetainedClients - 1].Pixmap), "上限之内的照常留着");
        Assert.IsFalse(await ExistsAsync(left[X11Server.MaxRetainedClients].Pixmap), "超了上限的照 Destroy 处理");
        IReadOnlyList<XClientInfo> clients = await server.GetClientsAsync();
        Assert.AreEqual(X11Server.MaxRetainedClients, clients.Count(c => c.Retained));

        // X-Resource 看得见保留的客户端(原先只列连着的),也能按它的 XID 查资源。
        byte[] name = "X-Resource"u8.ToArray();
        byte xres = (await observer.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad())).Bytes[9];
        XMessage listed = await observer.RequestAsync(xres, 1);   // QueryClients
        Assert.AreEqual((uint)(X11Server.MaxRetainedClients + 1), listed.U32(8), "保留的 16 个加上 observer 自己");
        XMessage resources = await observer.RequestAsync(xres, 2, b => b.U32(left[0].Base));   // QueryClientResources
        Assert.IsTrue(resources.IsReply, "保留的客户端按 XID 查得到");
        Assert.AreEqual(1u, resources.U32(8), "一类资源:PIXMAP");
    }

    [TestMethod]
    public async Task 读端卡在背压上时对端断了_写不出去就断开这个客户端()
    {
        if (!System.Net.Sockets.Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();
        await using XTestClient grabber = await XTestClient.ConnectAsync(server);
        XTestClient victim = await XTestClient.ConnectUnixAsync(path);
        await victim.SendAsync(2, 0, b => b.U32(victim.RootWindow).U32(0x800).U32(0x400000));   // 根窗口上选 PropertyChange
        await victim.SyncAsync();
        int victimId = (int)(victim.ResourceBase >> 21);

        await grabber.SendAsync(36, 0);   // GrabServer:victim 之后的请求全部暂存
        await grabber.SyncAsync();
        // 未执行的请求塞到上限(1024 条):读端卡在背压上,不再读套接字。
        await victim.SendManyAsync(Enumerable.Range(0, 1100).Select<int, (byte, byte, Action<XTestClient.Body>?)>(_ => (43, 0, null)));
        await Task.Delay(200);
        await victim.DisposeAsync();   // 对端走了;读端察觉不到

        // 有东西要发给 victim 时写不出去:断开它。原先写出端默默结束,连接一直挂着。
        byte[] value = "x"u8.ToArray();
        IReadOnlyList<XClientInfo> clients = [];
        for (int i = 0; i < 40 && (clients = await server.GetClientsAsync()).Any(c => c.Id == victimId); i++)
        {
            await grabber.SendAsync(18, 0, b => b.U32(grabber.RootWindow).U32(1).U32(31).U8(8).U8(0).U8(0).U8(0).U32(1).Bytes(value).Pad());
            await Task.Delay(50);
        }
        Assert.DoesNotContain(c => c.Id == victimId, clients, "写不出去的客户端断开了");
    }

    [TestMethod]
    public async Task GrabServer期间别人的请求暂存_Ungrab后按原顺序执行()
    {
        await using X11Server server = new();
        await using XTestClient grabber = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        await grabber.SendAsync(36, 0);   // GrabServer
        await grabber.SyncAsync();

        // 别人的请求:先写一个属性,再读回来。抓着的时候一条都不执行。
        byte[] value = "ordered"u8.ToArray();
        await other.SendAsync(18, 0, b => b.U32(other.RootWindow).U32(1).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)value.Length).Bytes(value).Pad());
        ushort read = await other.SendAsync(20, 0, b => b.U32(other.RootWindow).U32(1).U32(0).U32(0).U32(100));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => other.NextAsync(m => m.IsReply && m.Sequence == read, timeoutMs: 200));

        await grabber.SendAsync(37, 0);   // UngrabServer:暂存的请求放回执行循环,按原顺序执行
        XMessage reply = await other.NextAsync(m => m.IsReply && m.Sequence == read);
        Assert.AreEqual((uint)value.Length, reply.U32(16), "读到的是先写进去的那个值");
    }

    [TestMethod]
    public async Task GrabServer的持有者挂住时日志点名它_宿主断开它之后别人的请求照常执行()
    {
        // 持有者挂住(远端进程被 SIGSTOP、SSH 断网而连接没断)时所有会话的 X 程序都冻着:原先日志里一点痕迹也没有。
        System.Collections.Concurrent.ConcurrentQueue<string> lines = new();
        await using X11Server server = new(new X11ServerOptions { Log = lines.Enqueue }) { ServerGrabWarningDelay = TimeSpan.FromMilliseconds(100) };
        await using XTestClient grabber = await XTestClient.ConnectAsync(server, label: "user@stuck:22");
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        await grabber.SendAsync(36, 0);   // GrabServer,之后什么也不做
        await grabber.SyncAsync();
        ushort waiting = await other.SendAsync(43, 0);   // 被暂存
        for (int i = 0; i < 50 && !lines.Any(l => l.Contains("server grab", StringComparison.Ordinal)); i++)
        {
            await Task.Delay(20);
        }
        Assert.Contains(l => l.Contains("server grab", StringComparison.Ordinal) && l.Contains("user@stuck:22", StringComparison.Ordinal), lines,
            "点名抓着服务端的客户端");

        // 逃生:宿主按编号断开它(经 SSH 来的连接也一样),别人暂存的请求放回来执行。
        server.DisconnectClient((int)(grabber.ResourceBase >> 21));
        Assert.IsTrue((await other.NextAsync(m => m.IsReply && m.Sequence == waiting)).IsReply);
    }

    [TestMethod]
    public async Task GrabServer抓得太久时告诉宿主是谁_客户端清单标出持有者_解除之后标记消失()
    {
        // 原先只记一行日志:宿主不知道该断开谁,只能停掉整个服务端(F3)。
        using RecordingHost host = new();
        await using X11Server server = new(host: host) { ServerGrabWarningDelay = TimeSpan.FromMilliseconds(100) };
        await using XTestClient grabber = await XTestClient.ConnectAsync(server, label: "user@stuck:22");
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        int grabberId = (int)(grabber.ResourceBase >> 21);
        await grabber.SendAsync(36, 0);   // GrabServer
        await grabber.SyncAsync();
        Assert.IsTrue((await server.GetClientsAsync()).Single(c => c.Id == grabberId).HoldsServerGrab, "清单标出抓着服务端的那个");
        Assert.IsFalse((await server.GetClientsAsync()).Single(c => c.Id != grabberId).HoldsServerGrab);

        ushort waiting = await other.SendAsync(43, 0);   // 被暂存:有人在等,才算「抓得太久」
        await host.WaitForAsync(() => !host.ServerGrabStalls.IsEmpty);
        XServerGrabStall stall = host.ServerGrabStalls.First();
        Assert.AreEqual((grabberId, "user@stuck:22", 1), (stall.ClientId, stall.ClientLabel, stall.WaitingRequests));
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100), stall.Held);

        server.BreakGrabs();
        Assert.IsTrue((await other.NextAsync(m => m.IsReply && m.Sequence == waiting)).IsReply);
        Assert.IsFalse((await server.GetClientsAsync()).Any(c => c.HoldsServerGrab), "解除之后没有持有者");
    }

    [TestMethod]
    public async Task GrabServer抓着但没人在等时不告诉宿主()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host) { ServerGrabWarningDelay = TimeSpan.FromMilliseconds(50) };
        await using XTestClient grabber = await XTestClient.ConnectAsync(server);
        await grabber.SendAsync(36, 0);
        await grabber.SyncAsync();
        await Task.Delay(300);
        Assert.IsEmpty(host.ServerGrabStalls, "抓着不妨碍谁:不打扰用户");
    }

    [TestMethod]
    public async Task 抓着服务端的客户端断开_抓取随之解除()
    {
        await using X11Server server = new();
        XTestClient grabber = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        await grabber.SendAsync(36, 0);
        await grabber.SyncAsync();
        ushort sequence = await other.SendAsync(43, 0);   // GetInputFocus:被暂存
        await grabber.DisposeAsync();
        XMessage reply = await other.NextAsync(m => m.IsReply && m.Sequence == sequence);
        Assert.IsTrue(reply.IsReply);
    }
}
