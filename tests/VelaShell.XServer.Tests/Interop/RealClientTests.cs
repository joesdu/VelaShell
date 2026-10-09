using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
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
/// 验收标准是「没有协议错误 + 画出了东西」:每条发给客户端的错误都经 <see cref="X11ServerOptions.Log" /> 收集,
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

    private static (X11Server Server, ConcurrentQueue<string> Errors, byte[] Cookie) StartServer(IX11ServerHost? host = null)
    {
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        ConcurrentQueue<string> errors = new();
        X11Server server = new(new X11ServerOptions
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
            Assert.Contains("vendor string:    VelaShell", output);
            Assert.Contains("TrueColor", output);
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task glxinfo的直接与间接两条路径()
    {
        if (ShouldSkip())
        {
            return;
        }
        (X11Server server, ConcurrentQueue<string> errors, byte[] cookie) = StartServer();
        await using (server)
        {
            await server.StartAsync();
            (int exit, string output) = await RunClientAsync(cookie, "glxinfo -B && echo ==== && LIBGL_ALWAYS_INDIRECT=1 glxinfo -B");
            TestContext.WriteLine(output);
            Assert.AreEqual(0, exit, output);
            Assert.Contains("direct rendering: Yes", output);
            Assert.Contains("OpenGL renderer string: VelaShell.XServer software rasterizer", output);
            Assert.Contains("OpenGL version string: 1.1", output);
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }

    [TestMethod]
    [DataRow("xterm -geometry 40x6 -e sh -c 'echo hello; sleep 3'")]
    [DataRow("xeyes")]
    [DataRow("xclock -update 1")]
    [DataRow("xlogo")]
    [DataRow("xclock -render -update 1")]   // RENDER:抗锯齿的表盘与指针
    [DataRow("xeyes -render")]              // RENDER + SHAPE
    [DataRow("xterm -fa Monospace -fs 11 -geometry 40x6 -e sh -c 'echo hello; sleep 3'")]   // Xft 字形走 RENDER
    [DataRow("glxgears")]                                   // GLX 直接渲染:Mesa 在客户端软件渲染,经 PutImage 送像素
    [DataRow("env LIBGL_ALWAYS_INDIRECT=1 glxgears")]       // GLX 间接渲染:服务端的软件 GL
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
            TestContext.WriteLine($"{program}: {window.Snapshot.Width}x{window.Snapshot.Height} '{window.Snapshot.Title}',{distinct} 种颜色");
            (_, string output) = await client;
            TestContext.WriteLine(output);
            Assert.IsGreaterThan(1, distinct, "窗口里应当画出了不止背景一种颜色");
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }

    /// <summary>一个 Swing 程序:报能否最大化、边距,请求最大化之后报尺寸。源文件直接 <c>java P.java</c> 跑。</summary>
    private const string SwingProbe = """
        import javax.swing.*;
        import java.awt.*;
        public class P {
            public static void main(String[] a) throws Exception {
                System.out.println("max-supported=" + Toolkit.getDefaultToolkit().isFrameStateSupported(Frame.MAXIMIZED_BOTH));
                JFrame[] f = new JFrame[1];
                SwingUtilities.invokeAndWait(() -> { f[0] = new JFrame("probe"); f[0].setSize(300, 200); f[0].setVisible(true); });
                Thread.sleep(3000);
                SwingUtilities.invokeAndWait(() -> { System.out.println("insets-top=" + f[0].getInsets().top); f[0].setExtendedState(Frame.MAXIMIZED_BOTH); });
                Thread.sleep(4000);
                SwingUtilities.invokeAndWait(() -> System.out.println("size=" + f[0].getWidth() + "x" + f[0].getHeight()));
                System.exit(0);
            }
        }
        """;

    /// <summary>
    /// Java 的窗口管理器判定(xs_plan CP-9 / WN-S3):服务端占着 WM_S0 与根窗口的 SubstructureRedirect,名字是 Java 认得的「不套外框」的 LG3D ——
    /// 能最大化、边距为 0(不假定有标题栏)、最大化之后按新尺寸重排。名字不认得时 Java 当成会套外框的窗口管理器,一直等 ReparentNotify、
    /// 不理 ConfigureNotify;什么都不占时判成没有窗口管理器,最大化不可用。
    /// </summary>
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Swing认出不套外框的窗口管理器_最大化之后按新尺寸重排()
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
            Task<(int ExitCode, string Output)> client = RunClientAsync(cookie,
                $"command -v java >/dev/null || {{ echo NO-JAVA; exit 0; }}; cat > /tmp/P.java <<'EOF'\n{SwingProbe}\nEOF\njava /tmp/P.java", 180);
            // 宿主照办最大化(与 Avalonia 宿主一样:设状态、改尺寸)。
            XStateChangeRequest? maximize = null;
            while (maximize is null && !client.IsCompleted)
            {
                maximize = host.Requests.OfType<XStateChangeRequest>().FirstOrDefault(r => (r.Add & XWindowStates.Maximized) != 0);
                await Task.Delay(100);
            }
            if (maximize is not null)
            {
                server.SetTopLevelStates(maximize.Window, XWindowStates.Maximized);
                server.ResizeTopLevel(maximize.Window, 1920, 1080);
            }
            (_, string output) = await client;
            TestContext.WriteLine(output);
            if (output.Contains("NO-JAVA", StringComparison.Ordinal))
            {
                TestContext.WriteLine("[SKIP] 镜像里没有 Java:按 scripts/xserver/interop/Dockerfile 重建 velashell-xclients");
                return;
            }
            Assert.Contains("max-supported=true", output, "认出有窗口管理器");
            Assert.Contains("insets-top=0", output, "不假定有标题栏");
            Assert.Contains("size=1920x1080", output, "最大化之后按新尺寸重排");
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task 单窗口模式下twm接管顶层_xterm套上外框_拼进屏幕()
    {
        if (ShouldSkip())
        {
            return;
        }
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        ConcurrentQueue<string> errors = new();
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions
        {
            DisplayNumber = DisplayNumber,
            ListenAddress = IPAddress.Any,
            AuthorizationCookie = cookie,
            ScreenWidth = 800,
            ScreenHeight = 600,
            Rootful = true,
            Log = line =>
            {
                if (line.Contains(": Bad", StringComparison.Ordinal))
                {
                    errors.Enqueue(line);
                }
            },
        }, host);
        await server.StartAsync();
        Task<(int ExitCode, string Output)> client = RunClientAsync(cookie,
            "command -v twm >/dev/null || { echo NO-TWM; exit 0; }; "
            + "twm 2>/tmp/twm.err & sleep 2; xterm -geometry 40x10+30+40 -e sleep 20 & "
            + "w=$(xdotool search --sync --class xterm | head -1); sleep 2; "
            + "echo root=$(xwininfo -root | awk '/Window id:/{print $4}'); "
            + "echo parent=$(xwininfo -id $w -tree | awk '/Parent window id:/{print $4}'); "
            + "sleep 3; cat /tmp/twm.err; echo done");
        // 客户端还连着时看屏幕:xterm 的白底拼进了屏幕(容器一退出,窗口就都没了)。
        int white = 0;
        while (!client.IsCompleted)
        {
            int count = 0;
            server.Screen!.ReadPixels((pixels, _, _) =>
            {
                foreach (uint p in pixels)
                {
                    count += (p & 0xFFFFFF) == 0xFFFFFF ? 1 : 0;
                }
            });
            white = Math.Max(white, count);
            await Task.Delay(200);
        }
        (int exit, string output) = await client;
        TestContext.WriteLine(output);
        if (output.Contains("NO-TWM", StringComparison.Ordinal))
        {
            TestContext.WriteLine("[SKIP] 镜像里没有 twm:按 scripts/xserver/interop/Dockerfile 重建 velashell-xclients");
            return;
        }
        Assert.AreEqual(0, exit, output);
        Assert.DoesNotContain("another window manager", output, "服务端不占窗口管理器的位置");
        string root = output.Split('\n').Single(l => l.StartsWith("root=", StringComparison.Ordinal))[5..].Trim();
        string parent = output.Split('\n').Single(l => l.StartsWith("parent=", StringComparison.Ordinal))[7..].Trim();
        Assert.AreNotEqual(root, parent, "xterm 被 twm 套进了外框");
        Assert.IsTrue(host.Mapped.IsEmpty, "顶层不单独交给宿主");
        Assert.IsGreaterThan(10_000, white, "xterm 的白底拼进了屏幕");
        Assert.IsEmpty(errors, string.Join('\n', errors));
    }
}
