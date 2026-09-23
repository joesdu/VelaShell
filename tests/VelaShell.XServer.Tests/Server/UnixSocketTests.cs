using System.Net.Sockets;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Tests.Server;

/// <summary>Unix 套接字监听:本机客户端经 DISPLAY=:N 连进来。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class UnixSocketTests
{
    [TestMethod]
    public async Task 经Unix套接字完成连接建立()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new XServerOptions { ListenTcp = false, UnixSocketPath = path });
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
}
