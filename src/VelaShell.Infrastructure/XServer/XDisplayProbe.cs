using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VelaShell.Core.XServer;

namespace VelaShell.Infrastructure.XServer;

/// <summary>本机某个显示号上有没有 X 服务端在听。</summary>
/// <remarks>
/// 两处都要看:TCP <c>6000+N</c>(Windows 上的 X 服务端都走它),以及类 Unix 上的 <c>/tmp/.X11-unix/XN</c>
/// —— 桌面自己的 Xorg / XWayland 通常关着 TCP,只看端口会以为 :0 空着。
/// </remarks>
internal static partial class XDisplayProbe
{
    /// <summary>
    /// Windows 上在环回 <c>6000+N</c> 监听的进程是不是在当前用户会话里(按 <c>GetExtendedTcpTable</c> 给的属主进程号、
    /// <c>ProcessIdToSessionId</c> 比会话号);没人在听、查不到属主或查不到会话时为 false。
    /// </summary>
    /// <remarks>
    /// Windows 的 TCP 端口全机共享:终端服务器(多个用户同时登录)上 <c>localhost:0</c> 后面可能是别的用户开着的 VcXsrv(常带 <c>-ac</c>),
    /// 把 X11 转发交给它,远端程序的窗口、键盘、剪贴板就都到了别人的桌面上。查不清楚的一律当作不是自己的。
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static bool IsTcpListenerInThisSession(int display)
    {
        int port = XServerCommandLine.TcpPort(display);
        if (port is <= 0 or > ushort.MaxValue)
        {
            return false;
        }
        uint? owner = ListenerProcess(port, AddressFamilyInet, rowSize: 24, portOffset: 8, pidOffset: 20)
                      ?? ListenerProcess(port, AddressFamilyInet6, rowSize: 56, portOffset: 20, pidOffset: 52);
        return owner is { } pid
               && ProcessIdToSessionId(pid, out uint session) && ProcessIdToSessionId((uint)Environment.ProcessId, out uint own)
               && session == own;
    }

    private const uint AddressFamilyInet = 2, AddressFamilyInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    /// <summary>在 <paramref name="port" /> 上监听的进程号(MIB_TCP(6)TABLE_OWNER_PID 的一行:端口是网络字节序的低 16 位)。</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe uint? ListenerProcess(int port, uint family, int rowSize, int portOffset, int pidOffset)
    {
        uint size = 0;
        _ = GetExtendedTcpTable(null, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            byte[] buffer = new byte[size];
            fixed (byte* table = buffer)
            {
                uint result = GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result == 122)   // ERROR_INSUFFICIENT_BUFFER:表在两次调用之间变大了
                {
                    continue;
                }
                if (result != 0)
                {
                    return null;
                }
            }
            int count = (int)Math.Min(BitConverter.ToUInt32(buffer, 0), (uint)((buffer.Length - 4) / rowSize));
            for (int i = 0; i < count; i++)
            {
                int row = 4 + (i * rowSize);
                uint raw = BitConverter.ToUInt32(buffer, row + portOffset);
                if ((((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF)) == port)
                {
                    return BitConverter.ToUInt32(buffer, row + pidOffset);
                }
            }
            return null;
        }
        return null;
    }

    [LibraryImport("iphlpapi.dll")]
    [SupportedOSPlatform("windows")]
    private static unsafe partial uint GetExtendedTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, int tableClass, uint reserved);

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    /// <summary>探测一个端口有没有人听的上限。环回上连不上是立刻被拒,这个数只防意外。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>TCP 与 Unix 套接字任何一处有人在听,或者(类 Unix 上)显示号锁由一个活着的进程持着,即为占用。</summary>
    public static async Task<bool> IsInUseAsync(int display, CancellationToken cancellationToken) =>
        IsDisplayLocked(display)
        || await IsTcpListeningAsync(display, cancellationToken).ConfigureAwait(false)
        || await IsUnixSocketLiveAsync(display, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 类 Unix 上 <c>/tmp/.X{N}-lock</c>(Xserver(1) 的约定:里面是持有者的进程号)在、持有者还活着或者读不出是谁:这个号有人用。
    /// 只开 TCP、只开抽象名的服务端、<c>xvfb-run</c> 占着的号,套接字探测都看不出来 —— 原先自动选号挑中它,开的时候才撞上、再换号,至多换 4 次。
    /// 持有者已经不在的(崩溃留下的)不算,服务端开的时候会收回。
    /// </summary>
    internal static bool IsDisplayLocked(int display, string lockDirectory = "/tmp")
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }
        string path = Path.Combine(lockDirectory, $".X{display}-lock");
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            if (!int.TryParse(File.ReadAllText(path).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid)
                || pid <= 0)
            {
                return true;   // 读不出是谁持着:当作有人用
            }
            using System.Diagnostics.Process holder = System.Diagnostics.Process.GetProcessById(pid);
            return !holder.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // 没有这个进程:崩溃留下的锁
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>环回上 <c>6000+N</c> 有没有人在听。</summary>
    public static async Task<bool> IsTcpListeningAsync(int display, CancellationToken cancellationToken)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProbeTimeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, XServerCommandLine.TcpPort(display)), limit.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>类 Unix 上 <c>/tmp/.X11-unix/XN</c> 后面有没有进程在听(Linux 另看抽象命名空间里的同名套接字)。</summary>
    private static async Task<bool> IsUnixSocketLiveAsync(int display, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || !Socket.OSSupportsUnixDomainSockets)
        {
            return false;
        }
        string path = $"/tmp/.X11-unix/X{display}";
        return (OperatingSystem.IsLinux() && IsAbstractNameBound("\0" + path))
               || (File.Exists(path) && await CanConnectAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Linux 抽象命名空间里的这个名字有没有人 bind(不论 listen 没有):自己 bind 一下试试,成了立刻放掉(抽象名不落文件)。
    /// 原先连过去看:有人先 bind 不 listen 时连接被拒、判为空闲,内置服务端开起来之后它再 listen,
    /// 用 <c>:N</c> 连的本机程序先试抽象名,就把 cookie 交给了它。
    /// </summary>
    private static bool IsAbstractNameBound(string name)
    {
        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Bind(new UnixDomainSocketEndPoint(name));
            return false;
        }
        catch (SocketException e)
        {
            // 别的错误(这个环境不支持抽象名之类):服务端那边同样开不了它,只跳过、不算占用。
            return e.SocketErrorCode == SocketError.AddressAlreadyInUse;
        }
    }

    /// <summary>
    /// 连得上就是有人在听。限时:Linux 上对 backlog 已满的 AF_UNIX 流套接字做阻塞 connect 会一直等 —— 原先同步 Connect、不设时限,
    /// 本机任何用户在 <c>/tmp/.X11-unix/X1</c>(那个目录人人可写)上 listen(0) 并自己先连一条不 accept,显示号探测就挂死在那里,X Server 再也启动不了。
    /// 到时限还没连上按「有人占着」算(确实有人在听,只是不收)。
    /// </summary>
    private static async Task<bool> CanConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProbeTimeout);
        try
        {
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain)
        {
            return true;   // 有人在听、backlog 满了(Linux 上非阻塞 connect 回 EAGAIN):占着
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }
}
