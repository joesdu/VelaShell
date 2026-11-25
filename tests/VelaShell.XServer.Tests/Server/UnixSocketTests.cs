using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>Unix 套接字监听:本机客户端经 DISPLAY=:N 连进来。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed partial class UnixSocketTests
{
    [TestMethod]
    public void 占用探测有时限_backlog满了的套接字不会把启动卡死()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        UnixDomainSocketEndPoint endpoint = new(path);
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        listener.Listen(0);
        List<Socket> fillers = [];
        try
        {
            for (int i = 0; i < 4; i++)   // 连上不 accept,把 backlog 塞满(Linux 上之后的阻塞 connect 会一直等)
            {
                Socket filler = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                fillers.Add(filler);
                try
                {
                    filler.Connect(endpoint);
                }
                catch (SocketException)
                {
                }
            }
            var watch = Stopwatch.StartNew();
            bool live = X11Server.IsUnixSocketLive(path);
            Assert.IsLessThan(2000L, watch.ElapsedMilliseconds, "原先同步 connect、不设时限,Linux 上挂死在这里");
            if (OperatingSystem.IsLinux())
            {
                Assert.IsTrue(live, "有人在听、只是 backlog 满了:算占着,不去删它的套接字文件");
            }
        }
        finally
        {
            fillers.ForEach(f => f.Dispose());
            listener.Dispose();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 经Unix套接字完成连接建立()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();
        Assert.AreEqual(0, server.Port, "没开 TCP");

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await using NetworkStream stream = new(socket);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "Success");
        Assert.AreEqual(11, head[2], "协议主版本");

        await server.DisposeAsync();
        Assert.IsFalse(File.Exists(path), "收工时删掉套接字文件");
    }

    [TestMethod]
    public async Task 套接字文件只有属主能读写_配了cookie时同一个用户不带也能连()
    {
        if (!Socket.OSSupportsUnixDomainSockets || OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows 上的 AF_UNIX 没有 Unix 文件权限");
            return;
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path, AuthorizationCookie = new byte[16] });
        await server.StartAsync();
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path), "0600:别的用户连不进来");

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await using NetworkStream stream = new(socket);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "同一个用户经套接字文件连进来,不必带 cookie");

        await using XTestClient wrong = await XTestClient.ConnectUnixAsync(path, "MIT-MAGIC-COOKIE-1", [1]);
        Assert.AreEqual(0, wrong.SetupReply[0], "同一个用户显式提供错误的 cookie 时拒绝连接");
    }

    [TestMethod]
    public async Task 套接字文件后面有别的服务端在听时不删不抢_这个显示号不能用_TCP监听一并撤回()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        // 桌面自己的 Xorg 通常不开 TCP:只看 TCP 端口会以为 :0 空着,把它的套接字文件删了 —— 整个桌面的新程序都连不上。
        // 原先只是不开这条传输、照样用 TCP 开起来,Display 还是 :N —— 用 :N 的本机程序连到的是那一个。
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        using Socket other = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        other.Bind(new UnixDomainSocketEndPoint(path));
        other.Listen(8);   // 服务端探测时连进来的那一条不会被 accept;backlog 给小了,macOS 上后面的连接会被拒
        try
        {
            int display = FreeDisplayNumber();
            await using (X11Server server = new(new X11ServerOptions { DisplayNumber = display, ListenTcp = true, UnixSocketPath = path }))
            {
                SocketException taken = await Assert.ThrowsExactlyAsync<SocketException>(() => server.StartAsync());
                Assert.AreEqual(SocketError.AddressAlreadyInUse, taken.SocketErrorCode);
                Assert.AreEqual(0, server.Port, "TCP 已经开起来的监听撤回了");
                Assert.IsNull(server.Display);
            }
            using (TcpListener reuse = new(System.Net.IPAddress.Loopback, 6000 + display))
            {
                reuse.Start();   // 端口放出来了
            }
            Assert.IsTrue(File.Exists(path), "别人的套接字文件还在");
            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
            using Socket accepted = await other.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(accepted, "连过去的还是原来那个服务端");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "抽象命名空间只有 Linux 有")]
    public async Task 抽象名被别人bind了_这个显示号不能用_套接字文件也不建()
    {
        // 攻击者先 bind 抽象名、不 listen:原先我们的抽象名开不了只记一行,套接字文件与 TCP 照样开、Display 照样是 :N;
        // 它随后再 listen,Xlib / XCB 对 :N 先试抽象名,cookie 就交到了它手上。
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        using Socket squatter = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        squatter.Bind(new UnixDomainSocketEndPoint("\0" + path));

        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        SocketException taken = await Assert.ThrowsExactlyAsync<SocketException>(() => server.StartAsync());
        Assert.AreEqual(SocketError.AddressAlreadyInUse, taken.SocketErrorCode);
        Assert.IsFalse(File.Exists(path), "套接字文件也没建");
        Assert.IsNull(server.Display);
    }

    [TestMethod]
    public async Task 放套接字文件的目录是符号链接或属于别的用户时不开套接字文件()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("目录属主只在 Linux 与 macOS 上核对");
            return;
        }
        // 目录的属主能把里面的条目改名、删掉(粘滞位拦不住属主):别人的目录里,它随时能把我们的 0600 套接字换成自己的。
        string real = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}");
        string link = real + "-link";
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);
        try
        {
            Assert.IsNull(X11Server.SocketDirectoryProblem(real), "自己的目录可信");
            Assert.IsNotNull(X11Server.SocketDirectoryProblem(link), "符号链接不可信:指向哪里由它的属主说了算");
            string path = Path.Combine(link, "X0");
            await using (X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path }))
            {
                // Linux 上抽象名照样开着;macOS 没有抽象名、TCP 又没开 —— 一种传输也没开起来,StartAsync 抛 IOException
                IOException? none = null;
                try
                {
                    await server.StartAsync();
                }
                catch (IOException ex)
                {
                    none = ex;
                }
                Assert.AreEqual(!OperatingSystem.IsLinux(), none is not null, "只有没有抽象名的平台一种传输也开不起来");
                Assert.IsFalse(File.Exists(path), "不在不可信的目录里开套接字文件");
            }

            if (GetEffectiveUid() == 0)   // 改属主要 root(CI 的容器里是)
            {
                Assert.AreEqual(0, Chown(real, 12345, 12345));
                Assert.IsNotNull(X11Server.SocketDirectoryProblem(real), "属于别的用户");
                File.SetUnixFileMode(real, (UnixFileMode)0x3FF);   // 1777 也一样:粘滞位拦不住目录的属主
                Assert.IsNotNull(X11Server.SocketDirectoryProblem(real), "属于别的用户,带粘滞位也不行");
            }
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(real, recursive: true);
        }
    }

    [TestMethod]
    public void 取得到对端uid时以uid为准_取不到才看套接字文件的权限()
    {
        // 9p / drvfs(WSL 挂进来的 Windows 盘)上 chmod 0600 不报错却不生效:「只有属主连得上」不成立,别的用户也连得进来。
        Assert.IsFalse(X11Server.IsLocalUser(ownerOnly: true, peerUid: 4242, self: 1000), "原先取「或」:别的用户被当成本用户、不要 cookie");
        Assert.IsTrue(X11Server.IsLocalUser(ownerOnly: false, peerUid: 1000, self: 1000));
        Assert.IsTrue(X11Server.IsLocalUser(ownerOnly: true, peerUid: null, self: 1000), "取不到 uid(Windows):看套接字文件的权限");
        Assert.IsFalse(X11Server.IsLocalUser(ownerOnly: false, peerUid: null, self: null));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "IPC 命名空间只有 Linux 有")]
    public void MITSHM只给同一个IPC命名空间里的对端_核对不了算不同()
    {
        // 把 /tmp/.X11-unix 挂进容器后,容器里同一个 uid 的进程给的 shmid 按服务端的命名空间解释 —— 读写的是宿主这边的段。
        Assert.IsTrue(X11Server.SameIpcNamespace(Environment.ProcessId));
        Assert.IsFalse(X11Server.SameIpcNamespace(0), "SO_PEERCRED 的 pid 为 0:对端在别的 pid 命名空间里,看不见");
        Assert.IsFalse(X11Server.SameIpcNamespace(int.MaxValue), "进程不在了(读不了 /proc/<pid>/ns/ipc)");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "按 RLIMIT_NOFILE 把 fd 用完只在 Linux 上做")]
    public async Task Fd用完时接受循环不退出_放出来之后照常接新连接()
    {
        // 原先接受循环遇到任何 SocketException 就永久退出:fd 用完(EMFILE)是暂时的,本机 X 程序却从此再也连不进来。
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();

        Assert.AreEqual(0, GetRLimit(RLimitNoFile, out RLimit original));
        List<FileStream> filler = [];
        Socket queued = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            int open = Directory.GetFileSystemEntries("/proc/self/fd").Length;
            Assert.AreEqual(0, SetRLimit(RLimitNoFile, new RLimit { Current = (ulong)open + 32, Maximum = original.Maximum }));
            while (true)
            {
                try
                {
                    filler.Add(File.OpenRead("/dev/null"));
                }
                catch (IOException)
                {
                    break;   // EMFILE:一个 fd 都不剩了(连接的套接字事先建好了)
                }
            }
            await queued.ConnectAsync(new UnixDomainSocketEndPoint(path));   // 排进 backlog;服务端 accept 时拿不到 fd
            await Task.Delay(300);
        }
        finally
        {
            Assert.AreEqual(0, SetRLimit(RLimitNoFile, original));
            filler.ForEach(f => f.Dispose());
        }

        // fd 放出来之后,排着的那条照常被接进来、完成连接建立。
        await using NetworkStream stream = new(queued, ownsSocket: true);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "Success");
    }

    private const int RLimitNoFile = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [LibraryImport("libc", EntryPoint = "getrlimit")]
    private static partial int GetRLimit(int resource, out RLimit limit);

    [LibraryImport("libc", EntryPoint = "setrlimit")]
    private static partial int SetRLimit(int resource, in RLimit limit);

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows, IgnoreMessage = "/tmp/.X{N}-lock 是类 Unix 上的约定")]
    public async Task 持有显示号锁文件_别的服务端持着时这个号不能用_残留的锁清掉重建()
    {
        // Xvfb、xvfb-run -a 挑显示号时只看 /tmp/.X{N}-lock:原先不建,它们会挑中我们的号;我们也不看,会挑中它们的号。
        int display = FreeDisplayNumber();
        string lockPath = $"/tmp/.X{display}-lock";
        try
        {
            await using (X11Server first = new(new X11ServerOptions { DisplayNumber = display, ListenTcp = false }))
            {
                await first.StartAsync();
                Assert.AreEqual($"{Environment.ProcessId,10}\n", File.ReadAllText(lockPath), "内容是 PID,十位右对齐、换行结尾");

                await using X11Server second = new(new X11ServerOptions { DisplayNumber = display, ListenTcp = false });
                SocketException taken = await Assert.ThrowsExactlyAsync<SocketException>(() => second.StartAsync());
                Assert.AreEqual(SocketError.AddressAlreadyInUse, taken.SocketErrorCode, "锁在活着的进程手里:这个号不能用");
            }
            Assert.IsFalse(File.Exists(lockPath), "收工时删掉");

            // 持有者已经不在了:残留,删掉重建。
            using Process gone = Process.Start(new ProcessStartInfo("true"))!;
            await gone.WaitForExitAsync();
            File.WriteAllText(lockPath, $"{gone.Id,10}\n");
            await using X11Server third = new(new X11ServerOptions { DisplayNumber = display, ListenTcp = false });
            await third.StartAsync();
            Assert.AreEqual($"{Environment.ProcessId,10}\n", File.ReadAllText(lockPath));
        }
        finally
        {
            File.Delete(lockPath);
        }
    }

    [TestMethod]
    public async Task 启动失败之后可以再试_路径太长构造时就拒绝_一种传输也没开起来时抛异常()
    {
        // 原先 _started 已经置 1:失败之后再调报「已经在监听了」,这个实例再也开不起来。
        int display = FreeDisplayNumber();
        await using (X11Server server = new(new X11ServerOptions { DisplayNumber = display, ListenTcp = true, UnixSocketPath = "" }))
        {
            using (TcpListener squatter = new(System.Net.IPAddress.Loopback, 6000 + display))
            {
                squatter.Start();
                await Assert.ThrowsExactlyAsync<SocketException>(() => server.StartAsync());
            }
            await server.StartAsync();   // 占着的走了:再来一次
            Assert.AreEqual(6000 + display, server.Port);
        }

        // 路径太长:原先到 StartAsync 才抛 ArgumentOutOfRangeException(宿主只接几类异常,状态卡在「启动中」)。
        Assert.ThrowsExactly<ArgumentException>(() => new X11Server(new X11ServerOptions { UnixSocketPath = "/tmp/" + new string('x', 200) }));

        // 配置了要监听、一种传输也没开起来(放套接字的目录建不了):原先照常返回、Display 为 null。
        string file = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}");
        File.WriteAllText(file, "");
        try
        {
            await using X11Server nowhere = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = Path.Combine(file, "X0") });
            if (OperatingSystem.IsLinux())
            {
                await nowhere.StartAsync();   // Linux 上抽象命名空间里的同名套接字照样开得起来
                Assert.IsNotNull(nowhere.Display);
            }
            else if (Socket.OSSupportsUnixDomainSockets)
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => nowhere.StartAsync());
                Assert.IsNull(nowhere.Display);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>6000 + N 此刻没人占着的显示号(TCP 监听要用)。</summary>
    private static int FreeDisplayNumber()
    {
        for (int n = Random.Shared.Next(1000, 9000); ; n++)
        {
            try
            {
                using TcpListener probe = new(System.Net.IPAddress.Loopback, 6000 + n);
                probe.Start();
                return n;
            }
            catch (SocketException)
            {
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();

    [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Chown(string path, uint owner, uint group);
}
