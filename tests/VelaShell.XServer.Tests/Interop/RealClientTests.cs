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

    /// <summary>
    /// 剪贴板的图片(xs_plan F15):xclip 以 image/png 复制,服务端按 TARGETS 取回交给宿主;宿主给的图片 xclip 按 TARGETS 看得到、取回来逐字节相同。
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task xclip复制的PNG交给宿主_宿主给的PNG用xclip取得回来()
    {
        if (ShouldSkip())
        {
            return;
        }
        using RecordingHost host = new();
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        await using X11Server server = new(new X11ServerOptions
        {
            DisplayNumber = DisplayNumber,
            ListenAddress = IPAddress.Any,
            AuthorizationCookie = cookie,
            ClipboardFollowsFocus = false,   // 这里没有 X 窗口有焦点:只看传输
        }, host);
        await server.StartAsync();
        byte[] copied = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, .. RandomNumberGenerator.GetBytes(3000)];
        (int exit, string output) = await RunClientAsync(cookie,
            $"echo {Convert.ToBase64String(copied)} | base64 -d > /tmp/a.png && xclip -selection clipboard -t image/png -i /tmp/a.png && sleep 2");
        Assert.AreEqual(0, exit, output);
        await host.WaitForAsync(() => host.ClipboardContent is { Png.IsEmpty: false });
        CollectionAssert.AreEqual(copied, host.ClipboardContent!.Png.ToArray());

        byte[] given = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, .. RandomNumberGenerator.GetBytes(400_000)];   // 大于 INCR 的分块
        server.SetClipboard(new XClipboardContent { Text = "图注", Png = given });
        (exit, output) = await RunClientAsync(cookie,
            "xclip -selection clipboard -t TARGETS -o; echo '----8<----'; xclip -selection clipboard -t image/png -o | base64 -w0");
        TestContext.WriteLine(output[..Math.Min(output.Length, 600)]);
        Assert.AreEqual(0, exit, output);
        string[] parts = output.Split("----8<----");
        Assert.Contains("image/png", parts[0]);
        Assert.Contains("UTF8_STRING", parts[0]);
        CollectionAssert.AreEqual(given, Convert.FromBase64String(parts[1].Trim()));
    }

    /// <summary>
    /// 本机输入法上屏(xs_plan F5):真实的 Xlib 客户端(xev)收到借来的键码时按新的键值解释 —— 一段字里新借的几个键码只有一次通知
    /// (核心 MappingNotify 与只报键值那一段的 XKB MapNotify);CapsLock 开着时借来的「é」仍是 eacute(借来的键是 ALPHABETIC 类型,
    /// Lock 被这个键消耗;原先推成 ONE_LEVEL,Xlib 把它转成 Eacute);超过空键码数的字挪用键码之后也照样对。没有协议错误。
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task 输入法上屏的字_xev按借来的键值解释_CapsLock开着也不转大写()
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
            Task<(int ExitCode, string Output)> client = RunClientAsync(cookie, "timeout 12 xev -event keyboard -geometry 200x100; true");
            while (host.Mapped.IsEmpty && !client.IsCompleted)
            {
                await Task.Delay(100);
            }
            Assert.IsFalse(host.Mapped.IsEmpty, "xev 的窗口映射了:" + (client.IsCompleted ? (await client).Output : ""));
            await Task.Delay(1000);   // xev 映射之后还要选好事件、取键位表
            server.FocusTopLevel(host.Mapped.Values.First());
            server.InjectText("中文é");
            await Task.Delay(1500);
            server.InjectKey(XKeycodes.CapsLock, pressed: true);
            server.InjectKey(XKeycodes.CapsLock, pressed: false);
            server.InjectText("éa");
            (_, string output) = await client;
            TestContext.WriteLine(output);

            Assert.Contains("keysym 0x1004e2d, U4E2D", output, "中:借来的键码按 Unicode 键值解释");
            Assert.Contains("keysym 0x1006587, U6587", output, "文");
            int caps = output.IndexOf("Caps_Lock", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, caps, "CapsLock 按下了");
            string afterCaps = output[caps..];
            Assert.Contains("keysym 0xe9, eacute", afterCaps, "CapsLock 开着时借来的 é 仍是小写");
            Assert.DoesNotContain("Eacute", output, "没有被转成大写");
            Assert.Contains("keysym 0x61, a", afterCaps, "CapsLock 开着时键位表里的 a 也借键码,打出来是 a");
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }

    /// <summary>平滑滚动(xs_plan F6):libXi 解得出指针设备的两个滚动轴与 ScrollClass(xinput list --long 列出「Scroll info」)。</summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task xinput看得到指针的两个滚动轴()
    {
        if (ShouldSkip())
        {
            return;
        }
        (X11Server server, ConcurrentQueue<string> errors, byte[] cookie) = StartServer();
        await using (server)
        {
            await server.StartAsync();
            (int exit, string output) = await RunClientAsync(cookie, "xinput list --long");
            TestContext.WriteLine(output);
            Assert.AreEqual(0, exit, output);
            Assert.Contains("Rel Vert Scroll", output);
            Assert.Contains("Scroll info for Valuator 2", output);
            Assert.Contains("Scroll info for Valuator 3", output);
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
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

    /// <summary>
    /// 多重采样 FBConfig 与 GLX_EXT_libglvnd(xs_plan F22):<c>glxgears -samples 4</c> 直接、间接两条路径都拿得到多重采样配置
    /// (原先报「couldn't get an RGB, Double-buffered, Multisample visual」退出);服务端的 GLX 扩展里列出 GLX_EXT_libglvnd。
    /// 跑满 4 秒被 timeout 结束(退出码 124)就是一直在正常画。
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task glxgears要多重采样也拿得到配置_服务端列出libglvnd扩展()
    {
        if (ShouldSkip())
        {
            return;
        }
        (X11Server server, ConcurrentQueue<string> errors, byte[] cookie) = StartServer();
        await using (server)
        {
            await server.StartAsync();
            (_, string output) = await RunClientAsync(cookie,
                "glxinfo | grep -A3 'server glx extensions'; "
                + "timeout 4 glxgears -samples 4 > /tmp/d.txt 2>&1; echo direct-exit=$?; head -3 /tmp/d.txt; "
                + "LIBGL_ALWAYS_INDIRECT=1 timeout 4 glxgears -samples 4 > /tmp/i.txt 2>&1; echo indirect-exit=$?; head -3 /tmp/i.txt");
            TestContext.WriteLine(output);
            Assert.Contains("direct-exit=124", output);
            Assert.Contains("indirect-exit=124", output);
            Assert.Contains("GLX_EXT_libglvnd", output);
            Assert.IsEmpty(errors, string.Join('\n', errors));
        }
    }

    /// <summary>
    /// 执行线程在客户端之间轮流:间接 GL 的 glxgears 排满渲染请求时,同时连进来的 xdpyinfo 几秒内就拿到回复
    /// (原先先来先做,它要等 glxgears 那一千多条请求全做完 —— 实测 25 秒)。
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task 间接GL客户端排满请求时_别的客户端照样很快得到回复()
    {
        if (ShouldSkip())
        {
            return;
        }
        (X11Server server, ConcurrentQueue<string> errors, byte[] cookie) = StartServer();
        await using (server)
        {
            await server.StartAsync();
            (_, string output) = await RunClientAsync(cookie,
                "(LIBGL_ALWAYS_INDIRECT=1 timeout 10 glxgears > /dev/null 2>&1 &); sleep 3; "
                + "start=$(date +%s); timeout 30 xdpyinfo > /dev/null; echo xdpyinfo-exit=$? waited=$(( $(date +%s) - start ))");
            TestContext.WriteLine(output);
            Assert.Contains("xdpyinfo-exit=0", output);
            int waited = int.Parse(output[(output.IndexOf("waited=", StringComparison.Ordinal) + 7)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsLessThanOrEqualTo(5, waited, "xdpyinfo 不用等 glxgears 的请求全做完");
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

    /// <summary>
    /// 非受信(<c>ssh -X</c> 的做法):真的 xauth 先用受信 cookie 连上、经 SECURITY 签一个非受信 cookie 写回 .Xauthority,
    /// 之后的程序带着它连进来 —— 照常画窗口,看不到 XTEST,截不了根窗口。
    /// </summary>
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task 非受信_xauth签出的受限cookie连进来_程序照常画_看不到XTEST_截不了根窗口()
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
                "xauth generate $DISPLAY . untrusted timeout 120 && echo GENERATED; "
                + "xdpyinfo -queryExtensions | sed -n 's/^ *\\(XTEST\\|RENDER\\|SECURITY\\|XInputExtension\\)\\b.*/EXT \\1/p'; "
                + "xwd -root -silent >/dev/null 2>&1 && echo ROOT-CAPTURED || echo ROOT-DENIED; "
                + "timeout 6 xeyes; true", 120);
            await host.WaitForAsync(() => !host.Mapped.IsEmpty, 90_000);
            await Task.Delay(1500);
            XTopLevelWindow window = host.Mapped.Values.First();
            (uint[] pixels, _, _) = RecordingHost.Snapshot(window);
            IReadOnlyList<XClientInfo> clients = await server.GetClientsAsync();
            (_, string output) = await client;
            TestContext.WriteLine(output);
            TestContext.WriteLine(string.Join('\n', errors));
            Assert.Contains("GENERATED", output, "xauth 经 SECURITY 签出了 cookie");
            Assert.Contains("EXT RENDER", output);
            Assert.Contains("EXT XInputExtension", output);
            Assert.DoesNotContain("EXT XTEST", output, "非受信客户端看不到 XTEST");
            Assert.DoesNotContain("EXT SECURITY", output);
            Assert.Contains("ROOT-DENIED", output, "xwd -root 截不了屏");
            Assert.IsGreaterThan(1, pixels.Distinct().Count(), "xeyes 照常画出来");
            Assert.IsTrue(clients.Any(c => c.Trust == XClientTrust.Untrusted && c.TopLevels.Count > 0), "画窗口的是非受信客户端");
        }
    }

    /// <summary>非受信 cookie 下常见的程序照常画出来,也没有协议错误(SECURITY 的限制没有误伤正常的用法)。</summary>
    [TestMethod]
    [DataRow("xterm -geometry 40x6 -e sh -c 'echo hello; sleep 3'")]
    [DataRow("xterm -fa Monospace -fs 11 -geometry 40x6 -e sh -c 'echo hello; sleep 3'")]
    [DataRow("xclock -render -update 1")]
    [DataRow("xlogo")]
    [DataRow("env LIBGL_ALWAYS_INDIRECT=1 glxgears")]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task 非受信cookie下常见程序照常画出来且没有协议错误(string program)
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
                $"xauth generate $DISPLAY . untrusted timeout 120 || exit 1; timeout 5 {program}; true", 120);
            await host.WaitForAsync(() => !host.Mapped.IsEmpty, 90_000);
            await Task.Delay(1500);
            XTopLevelWindow window = host.Mapped.Values.First();
            (uint[] pixels, _, _) = RecordingHost.Snapshot(window);
            bool untrusted = (await server.GetClientsAsync()).Any(c => c.Trust == XClientTrust.Untrusted && c.TopLevels.Count > 0);
            (_, string output) = await client;
            TestContext.WriteLine(output);
            Assert.IsTrue(untrusted, "画窗口的是非受信客户端");
            Assert.IsGreaterThan(1, pixels.Distinct().Count(), "窗口里画出了东西");
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
