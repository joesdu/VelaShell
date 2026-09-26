using System.Net;
using System.Net.Sockets;
using VelaShell.Core.XServer;

namespace VelaShell.Infrastructure.XServer;

/// <summary>本机某个显示号上有没有 X 服务端在听。</summary>
/// <remarks>
/// 两处都要看:TCP <c>6000+N</c>(Windows 上的 X 服务端都走它),以及类 Unix 上的 <c>/tmp/.X11-unix/XN</c>
/// —— 桌面自己的 Xorg / XWayland 通常关着 TCP,只看端口会以为 :0 空着。
/// </remarks>
internal static class XDisplayProbe
{
    /// <summary>探测一个端口有没有人听的上限。环回上连不上是立刻被拒,这个数只防意外。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>TCP 与 Unix 套接字任何一处有人在听即为占用。</summary>
    public static async Task<bool> IsInUseAsync(int display, CancellationToken cancellationToken) =>
        await IsTcpListeningAsync(display, cancellationToken).ConfigureAwait(false) || IsUnixSocketLive(display);

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
    private static bool IsUnixSocketLive(int display)
    {
        if (OperatingSystem.IsWindows() || !Socket.OSSupportsUnixDomainSockets)
        {
            return false;
        }
        string path = $"/tmp/.X11-unix/X{display}";
        return (OperatingSystem.IsLinux() && CanConnect("\0" + path)) || (File.Exists(path) && CanConnect(path));
    }

    private static bool CanConnect(string endpoint)
    {
        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(endpoint));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
