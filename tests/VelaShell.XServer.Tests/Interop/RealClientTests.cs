using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Interop;

/// <summary>
/// 真实的 Xlib / XCB 客户端(Docker 里的 xdpyinfo、xterm、xeyes……)连进来。
/// </summary>
/// <remarks>
/// <para>
/// 需要 Docker 与镜像 <c>velashell-xclients</c>(<c>docker build -t velashell-xclients scripts/xserver/interop</c>),
/// 并设环境变量 <c>VELASHELL_XSERVER_INTEROP=1</c>。条件不满足时早退并在 TestContext 里写 <c>[SKIP]</c> ——
/// MSTest 会把早退记为通过,**看 [SKIP] 行才知道跑没跑**(与宿主的 DockerIntegration 同一约定)。
/// </para>
/// <para>
/// 验收标准是「没有协议错误 + 画出了东西」:每条发给客户端的错误都经 <see cref="XServerOptions.Log" /> 收集,
/// 断言为空;顶层窗口里必须有不同于背景的像素。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Interop")]
public sealed class RealClientTests
{
    private const string Image = "velashell-xclients";
    private const int DisplayNumber = 47;

    public TestContext TestContext { get; set; } = null!;

    private bool ShouldSkip()
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_XSERVER_INTEROP") != "1")
        {
            TestContext.WriteLine("[SKIP] 没有设 VELASHELL_XSERVER_INTEROP=1");
            return true;
        }
        return false;
    }

    private static async Task<(int ExitCode, string Output)> RunClientAsync(byte[] cookie, string command, int timeoutSeconds = 60)
    {
        string script = $"xauth -q add host.docker.internal:{DisplayNumber} MIT-MAGIC-COOKIE-1 {Convert.ToHexStringLower(cookie)} 2>/dev/null; "
                        + $"export DISPLAY=host.docker.internal:{DisplayNumber}; {command}";
        ProcessStartInfo start = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in (string[])["run", "--rm", "--add-host=host.docker.internal:host-gateway", Image, "sh", "-c", script])
        {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static (X11Server Server, ConcurrentQueue<string> Errors, byte[] Cookie) StartServer(IXServerHost? host = null)
    {
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        ConcurrentQueue<string> errors = new();
        X11Server server = new(new XServerOptions
        {
            DisplayNumber = DisplayNumber,
            ListenAddress = IPAddress.Any,
            AuthorizationCookie = cookie,
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            Log = line =>
            {
                if (line.Contains(": Bad", StringComparison.Ordinal))
                {
                    errors.Enqueue(line);
                }
            },
        }, host);
        return (server, errors, cookie);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task xdpyinfo连上并看到VelaShell这块屏幕()
    {
        if (ShouldSkip())
        {
            return;
        }
        (X11Server server, ConcurrentQueue<string> errors, byte[] cookie) = StartServer();
        await using (server)
        {
            await server.StartAsync();
            (int exit, string output) = await RunClientAsync(cookie, "xdpyinfo");
            TestContext.WriteLine(output);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "vendor string:    VelaShell");
            StringAssert.Contains(output, "TrueColor");
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }

    [TestMethod]
    [DataRow("xterm -geometry 40x6 -e sh -c 'echo hello; sleep 3'")]
    [DataRow("xeyes")]
    [DataRow("xclock -update 1")]
    [DataRow("xlogo")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task 图形程序映射窗口画出内容且没有协议错误(string program)
    {
        if (ShouldSkip())
        {
            return;
        }
        using RecordingHost host = new();
        (X11Server server, ConcurrentQueue<string> errors, byte[] cookie) = StartServer(host);
        await using (server)
        {
            await server.StartAsync();
            Task<(int ExitCode, string Output)> client = RunClientAsync(cookie, $"timeout 5 {program}; true");
            await host.WaitForAsync(() => !host.Mapped.IsEmpty, 60_000);
            await Task.Delay(1500);   // 等第一轮 Expose 画完
            XTopLevelWindow window = host.Mapped.Values.First();
            (uint[] pixels, _, _) = RecordingHost.Snapshot(window);
            int distinct = pixels.Distinct().Count();
            TestContext.WriteLine($"{program}: {window.Width}x{window.Height} '{window.Title}',{distinct} 种颜色");
            (_, string output) = await client;
            TestContext.WriteLine(output);
            Assert.IsGreaterThan(1, distinct, "窗口里应当画出了不止背景一种颜色");
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }
}
