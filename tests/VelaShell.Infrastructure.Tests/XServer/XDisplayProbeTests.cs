using System.Net;
using System.Net.Sockets;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>本机显示的探测:Windows 上在 6000+N 监听的进程是不是当前用户会话里的(终端服务器上可能是别人的 X 服务端)。</summary>
[TestClass]
[TestCategory("XServer")]
public class XDisplayProbeTests
{
    [TestMethod]
    public void TcpListenerOfThisProcess_IsInThisSession_AndNoListenerIsNot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("按会话核对属主只在 Windows 上做(TCP 端口全机共享)");
            return;
        }
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int display = ((IPEndPoint)listener.LocalEndpoint).Port - XServerCommandLine.TcpPort(0);
        Assert.IsTrue(XDisplayProbe.IsTcpListenerInThisSession(display), "自己开的监听在当前会话里");

        listener.Stop();
        Assert.IsFalse(XDisplayProbe.IsTcpListenerInThisSession(display), "没人在听:不算自己的");
    }

    /// <summary>
    /// 显示号锁(Xserver(1) 的 <c>/tmp/.X{N}-lock</c>):持有者还活着、或者读不出是谁,这个号算占用;持有者已经不在(崩溃留下的)不算,没有锁也不算。
    /// 原先只看套接字有没有人听,只开 TCP、只开抽象名的服务端与 xvfb-run 占着的号,自动选号会挑中。
    /// </summary>
    [TestMethod]
    public void DisplayLock_HeldByALiveProcessOrUnreadable_IsInUse_StaleOrMissingIsNot()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("显示号锁是类 Unix 上的约定");
            return;
        }
        string directory = Directory.CreateTempSubdirectory("vx-lock-").FullName;
        try
        {
            Assert.IsFalse(XDisplayProbe.IsDisplayLocked(7, directory), "没有锁");
            string lockFile = Path.Combine(directory, ".X7-lock");
            File.WriteAllText(lockFile, $"{Environment.ProcessId,10}\n");
            Assert.IsTrue(XDisplayProbe.IsDisplayLocked(7, directory), "持有者(本进程)活着");
            File.WriteAllText(lockFile, $"{int.MaxValue,10}\n");
            Assert.IsFalse(XDisplayProbe.IsDisplayLocked(7, directory), "持有者不在了:崩溃留下的");
            File.WriteAllText(lockFile, "garbage\n");
            Assert.IsTrue(XDisplayProbe.IsDisplayLocked(7, directory), "读不出是谁:当作有人用");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
