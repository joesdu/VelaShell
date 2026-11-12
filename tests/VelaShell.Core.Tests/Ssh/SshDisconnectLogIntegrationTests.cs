using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 连接经宿主装配路径建好之后,结束时诊断日志里有一行说清原因。
/// </summary>
/// <remarks>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh</c>(端口 2222)。对端掉线用「在服务端杀掉这条会话的 sshd 进程」造:
/// exec 通道里那条命令的父进程就是它,杀掉之后 TCP 直接断,与网络闪断时客户端看到的一样。
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 SSH 操作经宿主装配路径,取消令牌由测试上下文给出。")]
[TestClass]
[TestCategory("DockerIntegration")]
[DoNotParallelize]
public class SshDisconnectLogIntegrationTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2222;
    private const string Target = "testuser@127.0.0.1:2222";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(60_000)]
    public async Task PeerDrop_AndLocalClose_EachLeaveOneLineWithTheReason()
    {
        RequireContainer();
        using var log = new CapturingListener();
        Trace.Listeners.Add(log);
        try
        {
            VelaSshClientWrapper dropped = await ConnectAsync();
            try
            {
                // 杀掉的是服务端这条会话自己的进程:命令本身不会有回音,连接随之断掉
                await dropped.RunCommandAsync("kill -9 $PPID", TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(15), TestContext.CancellationToken);
            }
            catch (Exception)
            {
                // 预期:命令所在的连接被对端断开
            }
            string peerLine = await log.WaitForAsync(line => line.StartsWith($"[SSH] {Target} closed (connected since ", StringComparison.Ordinal)
                && !line.EndsWith("closed locally", StringComparison.Ordinal));
            Assert.Contains("(Open)", peerLine, "掉线发生在会话建立之后");
            await dropped.DisposeAsync();

            int before = log.Lines.Count;
            VelaSshClientWrapper closed = await ConnectAsync();
            Assert.AreEqual("testuser", (await closed.RunCommandAsync("whoami", TestContext.CancellationToken)).Trim());
            await closed.DisposeAsync();
            string localLine = await log.WaitForAsync(line => line.EndsWith("closed locally", StringComparison.Ordinal), skip: before);
            Assert.StartsWith($"[SSH] {Target} closed (connected since ", localLine);
            TestContext.WriteLine(peerLine);
            TestContext.WriteLine(localLine);
        }
        finally
        {
            Trace.Listeners.Remove(log);
        }
    }

    private async Task<VelaSshClientWrapper> ConnectAsync()
    {
        ConnectionInfo info = new()
        {
            Host = TestHost,
            Port = TestPort,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass",
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        try
        {
            await wrapper.ConnectAsync(TestContext.CancellationToken);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }

    private static void RequireContainer()
    {
        try
        {
            using TcpClient tcp = new();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达。运行 'docker compose -f docker-compose.test.yml up -d' 以启用。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }

    private sealed class CapturingListener : TraceListener
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (message is not null) Lines.Enqueue(message);
        }

        public async Task<string> WaitForAsync(Func<string, bool> match, int skip = 0)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (Lines.Skip(skip).FirstOrDefault(match) is { } line) return line;
                await Task.Delay(50);
            }
            Assert.Fail("没等到连接结束的日志行。已记下:\n" + string.Join('\n', Lines.Skip(skip)));
            return "";
        }
    }
}
