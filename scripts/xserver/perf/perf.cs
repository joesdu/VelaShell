#:project ../../../src/VelaShell/VelaShell.csproj
#:property TreatWarningsAsErrors=false
#:property AllowUnsafeBlocks=true
#:property SignAssembly=false
#:property ValidateExecutableReferencesMatchSelfContained=false

// VelaShell.XServer 的帧率与跟手程度测量:内置服务端 + 宿主,真实的 X 客户端跑在 Docker 里(镜像 velashell-xperf,同目录 Dockerfile)。
//
//   docker build -t velashell-xperf scripts/xserver/perf
//   dotnet run -c Release -p:SignAssembly=false -p:ValidateExecutableReferencesMatchSelfContained=false scripts/xserver/perf/perf.cs -- [选项] [场景 ...]
//
// 一定用 Release:Debug 的服务端慢得多(整窗动画只有十分之一的帧率),量出来的不是用户看到的。两个 -p 是全局属性 ——
// 仓库里没有签名密钥;主程序 Release 是自包含的,被这个非自包含的脚本引用要关掉那道检查(写在文件头的 #:property 只管脚本自己)。
//
// 选项:
//   --headless            不开 Avalonia 宿主:换成一个每 16.7 毫秒按损伤取一次像素的模拟宿主(只量服务端 + 传输 + 客户端)
//   --ssh                 经 SSH X11 转发(VelaShell.Ssh + 连接器,与宿主同一条路):自己起一个 sshd 容器,客户端在里面跑
//   --compress            SSH 开 zlib@openssh.com 压缩
//   --netem "<参数>"       sshd 容器的网卡上加 tc netem(比如 "delay 10ms"),模拟链路往返
//   --seconds N           帧率类场景每个跑多久(默认 10)
//   --samples N           延迟类场景注入多少次(默认 200)
//   --display N           显示号(默认 30;Docker 直连时容器经 host.docker.internal:7000+N 连进来)
//   --gsk <renderer>      GTK 4 场景的 GSK_RENDERER(默认 cairo,与宿主替 X11 转发会话设的一致)
//   --composition <模式>  Windows 上 Avalonia 的合成模式(WinUIComposition / DirectComposition / LowLatencyDxgiSwapChain / RedirectionSurface),
//                         不给时用默认的候选列表;渲染后端与主程序一致(AngleEgl,不行退 Software)
//   --render-probe        主窗口每 33 毫秒改一次文字并请求一帧回调,超过 50 毫秒的打一行 —— 与 X 无关的渲染循环停顿
//   --no-power-throttling 本进程不吃 Windows 11 对后台进程的节能限速(对照实验用)
//   --gap A-B             延迟类场景两次注入之间隔多少毫秒(默认 20-40;打字节奏用 80-600)
//   --no-warm             关掉宿主的保温(XSurface.KeepWarm),对照实验用
//
// Avalonia 宿主模式下输入照真实的路走:投到 UI 线程上注入,并像 XNativeWindow 的输入处理那样给窗口保温。
//
// 「损伤→进位图」超过 50 毫秒的每一次都打一行「[停顿]」。Avalonia 宿主模式下另报 UI 线程派发延迟(真实输入先经 UI 线程)。
//
// 场景(不给时跑 follow key xterm anim:1280x720 gears:1280x720):
//   follow                指针 → 画面:xprobe 跟着指针画方块。注入指针移动,量到服务端报出那块的损伤为止
//   key                   按键 → 整窗重画:xprobe 每次按键整窗换色
//   xterm                 按键 → 回显:xterm -e cat(终端行规程回显,xterm 画出那个字)
//   anim:WxH              xprobe 整窗动画(每帧 PutImage 整幅 + XSync),客户端报的帧率
//   gears:WxH             glxgears(llvmpipe 直接渲染,每帧整窗 PutImage)报的帧率
//   gears-indirect:WxH    glxgears 走间接 GLX(服务端的软件 GL)
//   gtk4:<demo>           gtk4-demo --run <demo>(比如 frames:跟着 GTK 的帧时钟每帧整窗重画),量损伤成簇数与宿主帧数
//   gtk3:<demo>           gtk3-demo --run <demo>(比如 pixbufs)
//
// 每个场景报:客户端自己报的帧率、服务端交给宿主的损伤批数 / 像素、宿主每秒取了几帧像素、损伤到像素进位图的延迟、
// 宿主取像素的耗时(这段时间执行线程停着)、执行线程忙的比例;延迟类场景另报注入到损伤的延迟分布。
// 宿主的计量来自 VelaShell.XServer.Host(XServerHostMetrics),执行线程的来自 VelaShell.XServer。数字只用来比较前后改动。

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using VelaShell.Services.XServer;
using VelaShell.Views.XServer;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;
using VelaShell.XServer;

PerfOptions options = PerfOptions.Parse(args);
if (OperatingSystem.IsWindows())
{
    Native.TimeBeginPeriod(1);   // 模拟宿主的 16.7 毫秒节拍、注入间隔都靠 Sleep / Delay,默认 15.6 毫秒的粒度太粗
    if (options.NoPowerThrottling)
    {
        Native.DisablePowerThrottling();
    }
}
if (options.Headless)
{
    return await Perf.RunAsync(options, ui: null);
}
PerfApp.Options = options;
Console.WriteLine($"合成模式候选:{(options.Composition is { } chosen ? chosen.ToString() : string.Join(" → ", new Win32PlatformOptions().CompositionMode) + "(默认)")}");
AppBuilder.Configure<PerfApp>().UsePlatformDetect()
    .With(new Win32PlatformOptions
    {
        RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],   // 与主程序(Program.ResolveRenderingMode)一致
        CompositionMode = options.Composition is { } mode ? [mode] : new Win32PlatformOptions().CompositionMode,
    })
    .StartWithClassicDesktopLifetime(args);
return PerfApp.ExitCode;

sealed class PerfApp : Application
{
    public static PerfOptions Options { get; set; } = new();
    public static int ExitCode { get; private set; }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            TextBlock text = new() { Text = "measuring…", Margin = new Thickness(12) };
            Window main = new() { Title = "VelaShell X perf", Width = 360, Height = 60, Content = text };
            desktop.MainWindow = main;
            main.Opened += async (_, _) =>
            {
                if (Options.RenderProbe)
                {
                    StartRenderProbe(main, text);
                }
                ExitCode = await Perf.RunAsync(Options, new AvaloniaXServerHost());
                desktop.Shutdown(ExitCode);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 与 X 无关的渲染探针:每 33 毫秒改一次主窗口的文字(要提交一批给合成器)、再请求一帧回调,量请求到回调隔了多久。
    /// X 窗口的「损伤→进位图」停顿与它同时出现,就是整个渲染循环(合成器)停住了,不是宿主取像素的问题。
    /// </summary>
    private static void StartRenderProbe(Window main, TextBlock text)
    {
        int n = 0;
        DispatcherTimer timer = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Normal, (_, _) =>
        {
            text.Text = $"measuring… {++n}";
            long requested = Stopwatch.GetTimestamp();
            main.RequestAnimationFrame(_ =>
            {
                double ms = Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
                if (ms > 50)
                {
                    Console.WriteLine($"    [停顿] 主窗口帧回调 {ms:F0} ms,发生在启动后 {Stopwatch.GetElapsedTime(Stats.Started).TotalSeconds:F2} s");
                }
            });
        });
        timer.Start();
    }
}

static partial class Native
{
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, int infoClass, ref PowerThrottlingState info, int size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial nint GetCurrentProcess();

    /// <summary>本进程不吃 Windows 11 对后台进程的节能限速(EcoQoS:降频、合并计时器唤醒)。对照实验用。</summary>
    public static void DisablePowerThrottling()
    {
        // ProcessPowerThrottling = 4;EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION 交给我们自己控制,StateMask = 0 即关掉
        PowerThrottlingState state = new() { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        bool ok = SetProcessInformation(GetCurrentProcess(), 4, ref state, Marshal.SizeOf<PowerThrottlingState>());
        Console.WriteLine($"关闭节能限速:{(ok ? "成功" : $"失败 {Marshal.GetLastWin32Error()}")}");
    }
}

sealed class PerfOptions
{
    public bool Headless { get; set; }
    public bool Ssh { get; set; }
    public bool Compress { get; set; }
    public string? Netem { get; set; }
    public int Seconds { get; set; } = 10;
    public int Samples { get; set; } = 200;
    public int Display { get; set; } = 30;
    public string Gsk { get; set; } = "cairo";
    public string Image { get; set; } = "velashell-xperf";
    public bool RenderProbe { get; set; }
    public bool NoPowerThrottling { get; set; }
    public Win32CompositionMode? Composition { get; set; }
    public (int Min, int Max) Gap { get; set; } = (20, 40);
    public bool NoWarm { get; set; }
    public List<string> Scenarios { get; } = [];

    public static PerfOptions Parse(string[] args)
    {
        PerfOptions o = new();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headless": o.Headless = true; break;
                case "--ssh": o.Ssh = true; break;
                case "--compress": o.Compress = true; break;
                case "--netem": o.Netem = args[++i]; break;
                case "--seconds": o.Seconds = int.Parse(args[++i]); break;
                case "--samples": o.Samples = int.Parse(args[++i]); break;
                case "--display": o.Display = int.Parse(args[++i]); break;
                case "--gsk": o.Gsk = args[++i]; break;
                case "--image": o.Image = args[++i]; break;
                case "--render-probe": o.RenderProbe = true; break;
                case "--no-power-throttling": o.NoPowerThrottling = true; break;
                case "--composition": o.Composition = Enum.Parse<Win32CompositionMode>(args[++i]); break;
                case "--gap": o.Gap = args[++i].Split('-') is [string lo, string hi] ? (int.Parse(lo), int.Parse(hi)) : throw new ArgumentException("--gap 写成 最小-最大(毫秒)"); break;
                case "--no-warm": o.NoWarm = true; break;
                default: o.Scenarios.Add(args[i]); break;
            }
        }
        if (o.Scenarios.Count == 0)
        {
            o.Scenarios.AddRange(["follow", "key", "xterm", "anim:1280x720", "gears:1280x720"]);
        }
        return o;
    }
}

/// <summary>一个场景里收集的数:宿主的计量(MeterListener 或模拟宿主直接记)、执行线程的忙碌时间、经 ProbeHost 看到的损伤。</summary>
sealed class Stats
{
    private readonly Lock _gate = new();
    public long HostFrames, BusyFrames, DamageBatches, DamagePixels, Bursts;
    private long _lastDamage;
    public double WorkMs;
    public List<double> DamageLatency { get; } = [];
    public List<double> ReadDuration { get; } = [];
    public List<double> UiDispatch { get; } = [];

    public void Reset()
    {
        lock (_gate)
        {
            HostFrames = BusyFrames = DamageBatches = DamagePixels = Bursts = 0;
            WorkMs = 0;
            DamageLatency.Clear();
            ReadDuration.Clear();
            UiDispatch.Clear();
        }
    }

    public void Frame() { lock (_gate) { HostFrames++; } }
    public void Busy() { lock (_gate) { BusyFrames++; } }
    public static readonly long Started = Stopwatch.GetTimestamp();

    public void Latency(double ms)
    {
        lock (_gate)
        {
            DamageLatency.Add(ms);
        }
        if (ms > 50)
        {
            Console.WriteLine($"    [停顿] 损伤→进位图 {ms:F0} ms,发生在启动后 {Stopwatch.GetElapsedTime(Started).TotalSeconds:F2} s");
        }
    }
    public void Read(double ms) { lock (_gate) { ReadDuration.Add(ms); } }
    public void Ui(double ms) { lock (_gate) { UiDispatch.Add(ms); } }
    public void Work(double ms) { lock (_gate) { WorkMs += ms; } }
    /// <summary>一批损伤;与上一批隔了 3 毫秒以上算客户端新的一帧(按损伤成簇估帧率:工具包画一帧的请求挨着到)。</summary>
    public void Damage(long pixels, long at)
    {
        lock (_gate)
        {
            DamageBatches++;
            DamagePixels += pixels;
            if (at - _lastDamage > Stopwatch.Frequency * 3 / 1000)
            {
                Bursts++;
            }
            _lastDamage = at;
        }
    }

    public string Describe(double seconds)
    {
        lock (_gate)
        {
            StringBuilder b = new();
            b.Append($"  服务端 → 宿主  损伤 {DamageBatches / seconds,6:F1} 批/秒(成簇 {Bursts / seconds,5:F1} 簇/秒)  {DamagePixels / seconds / 1e6,6:F2} M 像素/秒   执行线程忙 {WorkMs / 10 / seconds,5:F1}%\n");
            b.Append($"  宿主          取像素 {HostFrames / seconds,6:F1} 帧/秒  让帧 {BusyFrames}  损伤→进位图 {Percentiles(DamageLatency)}\n");
            b.Append($"                取像素耗时 {Percentiles(ReadDuration)}");
            if (UiDispatch.Count > 0)
            {
                b.Append($"\n  UI 线程      派发延迟(Input 优先级,每 4 毫秒探一次) {Percentiles(UiDispatch)}");
            }
            return b.ToString();
        }
    }

    public static string Percentiles(List<double> values)
    {
        if (values.Count == 0)
        {
            return "(无)";
        }
        double[] sorted = [.. values.Order()];
        double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
        return $"p50 {At(0.5):F1} / p95 {At(0.95):F1} / p99 {At(0.99):F1} / max {sorted[^1]:F1} ms (n={sorted.Length})";
    }
}

/// <summary>
/// 包在真正的宿主外面:损伤打时间戳、计数,等着某一点被画到的 waiter 在这里放行;其余回调原样转过去。
/// </summary>
sealed class ProbeHost(IX11ServerHost inner, Stats stats) : IX11ServerHost
{
    private readonly Lock _gate = new();
    private readonly List<XTopLevelWindow> _mapped = [];
    private (XTopLevelWindow Window, int X, int Y, TaskCompletionSource<long> Done)? _waiter;

    public XTopLevelWindow? Find(Func<XTopLevelSnapshot, bool> match)
    {
        lock (_gate)
        {
            return _mapped.LastOrDefault(w => w.IsAlive && match(w.Snapshot));
        }
    }

    /// <summary>等 <paramref name="window" /> 里包含 (x, y) 的那块损伤;x 为负时任何损伤都算。返回报出损伤的时刻。</summary>
    public Task<long> WaitDamage(XTopLevelWindow window, int x, int y)
    {
        TaskCompletionSource<long> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiter = (window, x, y, done);
        }
        return done.Task;
    }

    public void TopLevelMapped(XTopLevelWindow window)
    {
        lock (_gate)
        {
            _mapped.Add(window);
        }
        inner.TopLevelMapped(window);
    }

    public void TopLevelUnmapped(XTopLevelWindow window)
    {
        lock (_gate)
        {
            _mapped.Remove(window);
        }
        inner.TopLevelUnmapped(window);
    }

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
    {
        long now = Stopwatch.GetTimestamp();
        long pixels = 0;
        foreach (XRect r in damage)
        {
            pixels += (long)r.Width * r.Height;
        }
        stats.Damage(pixels, now);
        lock (_gate)
        {
            if (_waiter is { } w && ReferenceEquals(w.Window, window)
                && (w.X < 0 || damage.Any(r => w.X >= r.X && w.X < r.X + r.Width && w.Y >= r.Y && w.Y < r.Y + r.Height)))
            {
                _waiter = null;
                w.Done.TrySetResult(now);
            }
        }
        inner.TopLevelDamaged(window, damage);
    }

    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes) => inner.TopLevelChanged(window, changes);
    public void TopLevelRedrawn(XTopLevelWindow window) => inner.TopLevelRedrawn(window);
    public void FrameClockWanted(bool wanted) => inner.FrameClockWanted(wanted);
    public void CursorChanged(XTopLevelWindow? window, XCursor cursor) => inner.CursorChanged(window, cursor);
    public void BellRequested(int volume) => inner.BellRequested(volume);
    public void ClipboardChanged(string text) => inner.ClipboardChanged(text);
    public void ClipboardContentChanged(XClipboardContent content) => inner.ClipboardContentChanged(content);
    public void WindowManagerRequested(XWindowManagerRequest request) => inner.WindowManagerRequested(request);
    public void ServerGrabStalled(XServerGrabStall stall) => inner.ServerGrabStalled(stall);
    public void PointerWarped(int rootX, int rootY) => inner.PointerWarped(rootX, rootY);
    public void SystemTrayIconAdded(XTopLevelWindow icon, string title) => inner.SystemTrayIconAdded(icon, title);
    public void SystemTrayIconRemoved(XTopLevelWindow icon) => inner.SystemTrayIconRemoved(icon);
    public void PointerConfinementChanged(XRect? area) => inner.PointerConfinementChanged(area);
    public void ScreenSaverSuspensionChanged(bool suspended) => inner.ScreenSaverSuspensionChanged(suspended);
    public void ScreenSaverReset() => inner.ScreenSaverReset();
    public void InputMethodFocusChanged(XInputMethodFocus? focus) => inner.InputMethodFocusChanged(focus);
    public void OutgoingDragStarted(XOutgoingDrag drag) => inner.OutgoingDragStarted(drag);
    public void OutgoingDragEnded(XOutgoingDrag drag) => inner.OutgoingDragEnded(drag);
}

/// <summary>
/// 不开界面时代替 Avalonia 宿主:与它一样按窗口攒损伤,每 16.7 毫秒(60 Hz)限时 8 毫秒拿像素锁、把损伤矩形拷进自己的缓冲。
/// 只为让服务端那边的锁竞争与真实宿主相近;不画任何东西。
/// </summary>
sealed class SimHost : IX11ServerHost, IDisposable
{
    private readonly Stats _stats;
    private readonly Lock _gate = new();
    private readonly Dictionary<XTopLevelWindow, (List<XRect> Rects, long Since)> _pending = [];
    private readonly Thread _thread;
    private volatile bool _stop;
    private uint[] _copy = [];
    private List<XRect> _reading = [];

    public SimHost(Stats stats)
    {
        _stats = stats;
        _thread = new Thread(Loop) { IsBackground = true, Name = "sim-host-frames" };
        _thread.Start();
    }

    public void Dispose() => _stop = true;

    private void Loop()
    {
        long period = Stopwatch.Frequency / 60, next = Stopwatch.GetTimestamp() + period;
        while (!_stop)
        {
            while (Stopwatch.GetTimestamp() < next)
            {
                Thread.Sleep(1);
            }
            next += period;
            KeyValuePair<XTopLevelWindow, (List<XRect> Rects, long Since)>[] due;
            lock (_gate)
            {
                due = [.. _pending];
                _pending.Clear();
            }
            foreach ((XTopLevelWindow window, (List<XRect> rects, long since)) in due)
            {
                _reading = rects;
                long started = Stopwatch.GetTimestamp();
                XPixelReadResult read = window.TryReadPixels(Copy, TimeSpan.FromMilliseconds(8));
                _stats.Read(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (read == XPixelReadResult.Busy)
                {
                    _stats.Busy();
                    lock (_gate)
                    {
                        _pending.TryAdd(window, (rects, since));   // 下一帧再取
                    }
                    continue;
                }
                _stats.Frame();
                _stats.Latency(Stopwatch.GetElapsedTime(since).TotalMilliseconds);
            }
        }
    }

    private void Copy(ReadOnlySpan<uint> pixels, int width, int height)
    {
        if (_copy.Length < pixels.Length)
        {
            _copy = new uint[pixels.Length];
        }
        foreach (XRect d in _reading)
        {
            XRect r = d.Intersect(new XRect(0, 0, width, height));
            for (int y = r.Y; y < r.Y + r.Height; y++)
            {
                pixels.Slice((y * width) + r.X, r.Width).CopyTo(_copy.AsSpan((y * width) + r.X));
            }
        }
    }

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(window, out var entry))
            {
                entry = ([], Stopwatch.GetTimestamp());
            }
            entry.Rects.AddRange(damage);
            if (entry.Rects.Count > 32)
            {
                XRect bounds = entry.Rects[0];
                foreach (XRect r in entry.Rects)
                {
                    int x1 = Math.Min(bounds.X, r.X), y1 = Math.Min(bounds.Y, r.Y);
                    bounds = new XRect(x1, y1, Math.Max(bounds.X + bounds.Width, r.X + r.Width) - x1, Math.Max(bounds.Y + bounds.Height, r.Y + r.Height) - y1);
                }
                entry.Rects.Clear();
                entry.Rects.Add(bounds);
            }
            _pending[window] = entry;
        }
    }

    public void TopLevelMapped(XTopLevelWindow window) => TopLevelDamaged(window, [new XRect(0, 0, window.Snapshot.Width, window.Snapshot.Height)]);
    public void TopLevelUnmapped(XTopLevelWindow window) { }
    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes) { }
    public void CursorChanged(XTopLevelWindow? window, XCursor cursor) { }
    public void BellRequested(int volume) { }
    public void ClipboardChanged(string text) { }
}

static class Perf
{
    public static async Task<int> RunAsync(PerfOptions options, AvaloniaXServerHost? ui)
    {
        Stats stats = new();
        using MeterListener listener = Listen(stats);
        using SimHost? sim = ui is null ? new SimHost(stats) : null;
        using CancellationTokenSource uiProbe = new();
        if (ui is not null)
        {
            // 真实的指针、键盘先到 UI 线程,再注入服务端:UI 线程忙(取像素、布局、渲染)时输入跟着晚。每 4 毫秒投一个 Input 优先级的空操作,量它等了多久。
            _ = Task.Run(async () =>
            {
                while (!uiProbe.IsCancellationRequested)
                {
                    long posted = Stopwatch.GetTimestamp();
                    await Dispatcher.UIThread.InvokeAsync(() => stats.Ui(Stopwatch.GetElapsedTime(posted).TotalMilliseconds), DispatcherPriority.Input);
                    await Task.Delay(4);
                }
            });
        }
        ProbeHost probe = new((IX11ServerHost?)ui ?? sim!, stats);
        InputPath input = new(ui);
        if (ui is not null && options.NoWarm)
        {
            InputPath.SetKeepWarm(TimeSpan.Zero);
        }
        await using X11Server server = new(new X11ServerOptions { DisplayNumber = options.Display, ListenTcp = false, UnixSocketPath = "", Log = line => Console.WriteLine($"[x] {line}") }, probe);
        if (ui is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ui.AttachAsync(server, CancellationToken.None));
        }

        await using IClientLauncher launcher = options.Ssh
            ? await SshLauncher.StartAsync(options, server)
            : DockerLauncher.Start(options, server);
        Console.WriteLine($"模式:{(ui is null ? "无界面(模拟 60 Hz 宿主)" : "Avalonia 宿主")} · {launcher.Describe}");
        foreach (string scenario in options.Scenarios)
        {
            try
            {
                await RunScenarioAsync(scenario, options, server, probe, stats, launcher, input);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{scenario}] 失败:{ex.GetType().Name}: {ex.Message}");
            }
        }
        await uiProbe.CancelAsync();
        if (ui is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(ui.Detach);
        }
        return 0;
    }

    private static MeterListener Listen(Stats stats)
    {
        MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name is "VelaShell.XServer.Host" || instrument.Name == "velashell.xserver.work.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "velashell.xserver.host.frames": stats.Frame(); break;
                case "velashell.xserver.host.frames.busy": stats.Busy(); break;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "velashell.xserver.host.damage.latency": stats.Latency(value); break;
                case "velashell.xserver.host.read.duration": stats.Read(value); break;
                case "velashell.xserver.work.duration": stats.Work(value); break;
            }
        });
        listener.Start();
        return listener;
    }

    private static async Task RunScenarioAsync(string scenario, PerfOptions options, X11Server server, ProbeHost probe, Stats stats, IClientLauncher launcher,
        InputPath input)
    {
        string[] parts = scenario.Split(':', 2);
        (string kind, string arg) = (parts[0], parts.Length > 1 ? parts[1] : "");
        (int w, int h) = arg.Split('x') is [string a, string b] && int.TryParse(a, out int pw) && int.TryParse(b, out int ph) ? (pw, ph) : (1280, 720);
        int run = options.Seconds;
        int latencyBudget = (int)(options.Samples * (options.Gap.Max + 100) / 1000.0) + 30;   // 每次最多「间隔 + 一次往返」,另留起步的余量
        (string command, Func<XTopLevelSnapshot, bool> match, bool latency) = kind switch
        {
            "follow" => ("xprobe follow", (Func<XTopLevelSnapshot, bool>)(s => s.Title == "xprobe-follow"), true),
            "key" => ("xprobe key", s => s.Title == "xprobe-key", true),
            "xterm" => ("xterm -geometry 80x24 -e cat", s => s.ClassName == "XTerm", true),
            "anim" => ($"xprobe anim {w} {h}", s => s.Title == "xprobe-anim", false),
            "gears" => ($"env vblank_mode=0 glxgears -geometry {w}x{h}", s => s.Title == "glxgears", false),
            "gears-indirect" => ($"env vblank_mode=0 LIBGL_ALWAYS_INDIRECT=1 glxgears -geometry {w}x{h}", s => s.Title == "glxgears", false),
            "gtk4" => ($"env GSK_RENDERER={options.Gsk} gtk4-demo --run={arg}", s => s.ClassName.Contains("gtk4", StringComparison.OrdinalIgnoreCase) || s.Title.Length > 0, false),
            "gtk3" => ($"gtk3-demo --run={arg}", s => s.Title.Length > 0, false),
            _ => throw new ArgumentException($"不认识的场景 {scenario}"),
        };
        int budget = latency ? latencyBudget : run + 15;
        await using IClientProcess client = await launcher.LaunchAsync($"timeout -s KILL {budget} {command}");
        XTopLevelWindow window = await WaitWindowAsync(probe, match, client, TimeSpan.FromSeconds(15));
        server.FocusTopLevel(window);
        input.Send(window, static () => { }, activate: true);   // 相当于窗口被激活:XNativeWindow 在 Activated 里给窗口保温、当场转起来
        await Task.Delay(1500);   // 先让它稳下来(GTK 起步时连着重画几次、glxgears 第一行帧率)

        Console.WriteLine($"[{scenario}]  {command}(测量从启动后 {Stopwatch.GetElapsedTime(Stats.Started).TotalSeconds:F2} s 开始)");
        if (latency)
        {
            stats.Reset();
            Stopwatch clock = Stopwatch.StartNew();
            List<double> samples = await MeasureLatencyAsync(kind, options.Samples, server, probe, window, input, options.Gap);
            Console.WriteLine($"  注入 → 损伤    {Stats.Percentiles(samples)}");
            Console.WriteLine(stats.Describe(clock.Elapsed.TotalSeconds));
        }
        else
        {
            client.ClearLines();
            stats.Reset();
            Stopwatch clock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(run));
            double elapsed = clock.Elapsed.TotalSeconds;
            string reported = ClientFps(client.TakeLines());
            Console.WriteLine($"  客户端报的帧率 {reported}");
            Console.WriteLine(stats.Describe(elapsed));
        }
    }

    private static async Task<XTopLevelWindow> WaitWindowAsync(ProbeHost probe, Func<XTopLevelSnapshot, bool> match, IClientProcess client, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (probe.Find(match) is { } window)
            {
                return window;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"等不到窗口;客户端输出:{string.Join(" | ", client.TakeLines().TakeLast(5))}");
    }

    /// <summary>
    /// 一次一个:注入、等那块损伤、隔 20–40 毫秒(随机,免得与客户端或宿主的节拍锁相)再下一次。
    /// follow 在窗口两侧来回跳(新位置离旧方块足够远,只有客户端画了新方块才会有包含它的损伤);key / xterm 按 a 键。
    /// </summary>
    private static async Task<List<double>> MeasureLatencyAsync(string kind, int samples, X11Server server, ProbeHost probe, XTopLevelWindow window,
        InputPath input, (int Min, int Max) gap)
    {
        List<double> result = [];
        Random random = new(1);
        int timeouts = 0;
        for (int i = 0; i < samples; i++)
        {
            Task<long> drawn;
            long injected;
            if (kind == "follow")
            {
                int x = i % 2 == 0 ? 120 : 520, y = 160 + random.Next(160);
                drawn = probe.WaitDamage(window, x, y);
                injected = Stopwatch.GetTimestamp();
                input.Send(window, () => server.InjectPointerMotion(window, x, y));
            }
            else
            {
                drawn = probe.WaitDamage(window, -1, -1);
                injected = Stopwatch.GetTimestamp();
                input.Send(window, () =>
                {
                    // 用户往哪个窗口打字,哪个窗口就有焦点。测的这几分钟里本机前台换了(用户在别的窗口里干活),
                    // 宿主会把 X 的焦点清掉,注入的键就没了去处 —— 每次先把焦点还给它。
                    server.FocusTopLevel(window);
                    server.InjectKey(XKeycodes.A, pressed: true);
                    server.InjectKey(XKeycodes.A, pressed: false);
                });
            }
            Task done = await Task.WhenAny(drawn, Task.Delay(2000));
            if (done == drawn)
            {
                result.Add(Stopwatch.GetElapsedTime(injected, drawn.Result).TotalMilliseconds);
            }
            else if (++timeouts > 5)
            {
                throw new TimeoutException("连续等不到损伤");
            }
            await Task.Delay(gap.Min + random.Next(gap.Max - gap.Min + 1));
        }
        return result;
    }

    private static string ClientFps(IReadOnlyList<string> lines)
    {
        List<double> fps = [];
        foreach (string line in lines)
        {
            if (Regex.Match(line, @"=\s*([\d.]+) FPS") is { Success: true } gears)
            {
                fps.Add(double.Parse(gears.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (Regex.Match(line, @"^fps ([\d.]+)") is { Success: true } probe)
            {
                fps.Add(double.Parse(probe.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return fps.Count == 0 ? "(客户端没报)" : $"{fps.Average(),6:F1}(各次 {string.Join(" ", fps.Select(f => f.ToString("F0")))})";
    }
}

/// <summary>
/// 注入输入走的路。真实的指针、键盘先到 UI 线程,<c>XNativeWindow</c> 的输入处理注入服务端、并给窗口保温(<c>XSurface.Warm</c>,
/// 避开 Avalonia 在 Windows 上的渲染停顿)。Avalonia 宿主模式下照这条路走:投到 UI 线程上注入,经反射调 Warm(宿主的内部成员);
/// 无界面模式下直接注入。
/// </summary>
sealed class InputPath(AvaloniaXServerHost? ui)
{
    private static readonly FieldInfo? WindowsField = typeof(AvaloniaXServerHost).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? SurfaceField = typeof(XNativeWindow).GetField("_surface", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly Type? SurfaceType = typeof(XNativeWindow).GetNestedType("XSurface", BindingFlags.NonPublic);

    public void Send(XTopLevelWindow window, Action inject, bool activate = false)
    {
        if (ui is null)
        {
            inject();
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            inject();
            if (WindowsField?.GetValue(ui) is System.Collections.IDictionary map && map[window] is { } native && SurfaceField?.GetValue(native) is { } surface)
            {
                SurfaceType?.GetMethod("Warm")?.Invoke(surface, [activate]);   // 输入只把保温往后延;窗口激活当场转起来(与 XNativeWindow 一致)
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>改保温时长(<c>XSurface.KeepWarm</c>);对照实验用。</summary>
    public static void SetKeepWarm(TimeSpan value) =>
        SurfaceType?.GetProperty("KeepWarm", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, value);
}

interface IClientLauncher : IAsyncDisposable
{
    string Describe { get; }
    Task<IClientProcess> LaunchAsync(string command);
}

interface IClientProcess : IAsyncDisposable
{
    IReadOnlyList<string> TakeLines();
    void ClearLines();
}

/// <summary>收客户端 stdout / stderr 的行。</summary>
class LineSink
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    public void Add(string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    public IReadOnlyList<string> TakeLines()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }

    public void ClearLines()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }
}

/// <summary>Docker 直连:容器经 host.docker.internal:7000+N 连到这里,直接交给服务端(当作本机连接)。</summary>
sealed class DockerLauncher : IClientLauncher
{
    private readonly PerfOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private int _next;

    private DockerLauncher(PerfOptions options, X11Server server)
    {
        _options = options;
        _listener = new TcpListener(IPAddress.Any, 7000 + options.Display);
        _listener.Start();
        _ = AcceptAsync(server);
    }

    public static DockerLauncher Start(PerfOptions options, X11Server server) => new(options, server);

    public string Describe => $"Docker 直连 TCP(host.docker.internal:{7000 + _options.Display})";

    private async Task AcceptAsync(X11Server server)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            tcp.NoDelay = true;
            _ = Task.Run(async () =>
            {
                using (tcp)
                {
                    try
                    {
                        await server.ServeAsync(tcp.GetStream(), isLocal: true);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                    {
                    }
                }
            });
        }
    }

    public Task<IClientProcess> LaunchAsync(string command)
    {
        string name = $"xperf-{Environment.ProcessId}-{Interlocked.Increment(ref _next)}";
        string display = $"host.docker.internal:{1000 + _options.Display}";
        ProcessStartInfo start = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in (string[])["run", "--rm", "--name", name, "-e", $"DISPLAY={display}", _options.Image, "sh", "-c", command])
        {
            start.ArgumentList.Add(a);
        }
        Process process = Process.Start(start)!;
        DockerClient client = new(process, name);
        process.OutputDataReceived += (_, e) => client.Add(e.Data);
        process.ErrorDataReceived += (_, e) => client.Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return Task.FromResult<IClientProcess>(client);
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private sealed class DockerClient(Process process, string name) : LineSink, IClientProcess
    {
        public async ValueTask DisposeAsync()
        {
            using Process kill = Process.Start(new ProcessStartInfo("docker", ["rm", "-f", name]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            await kill.WaitForExitAsync();
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}

/// <summary>
/// 经 SSH X11 转发:起一个 sshd 容器(可加 netem),VelaShell.Ssh 连上去跑命令,x11 通道经连接器(内存双工流)直接交给服务端 ——
/// 与宿主里「X Server」按钮 + SSH 会话是同一条路(BuiltInLocalXServer.Serve)。
/// </summary>
sealed class SshLauncher : IClientLauncher
{
    private readonly string _container;
    private readonly SshConnection _connection;
    private readonly X11Server _server;
    private readonly PerfOptions _options;

    private SshLauncher(string container, SshConnection connection, X11Server server, PerfOptions options)
    {
        (_container, _connection, _server, _options) = (container, connection, server, options);
    }

    public string Describe =>
        $"SSH X11 转发(加密 {_connection.Algorithms.EncryptionServerToClient},压缩 {_connection.Algorithms.CompressionServerToClient}"
        + (_options.Netem is { } netem ? $",netem {netem}" : "") + ")";

    public static async Task<SshLauncher> StartAsync(PerfOptions options, X11Server server)
    {
        int port = 2240 + options.Display % 100;
        string container = $"xperf-sshd-{Environment.ProcessId}";
        await DockerAsync("run", "-d", "--rm", "--name", container, "--cap-add", "NET_ADMIN", "-p", $"127.0.0.1:{port}:22", options.Image, "/usr/sbin/sshd", "-D", "-e");
        if (options.Netem is { } netem)
        {
            await DockerAsync(["exec", container, "tc", "qdisc", "add", "dev", "eth0", "root", "netem", .. netem.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
        }
        SshConnectionOptions connect = new($"dp@127.0.0.1:{port}")
        {
            Credentials = [new PasswordCredential("testpass")],
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Algorithms = options.Compress ? SshAlgorithmSet.Default.WithCompression() : SshAlgorithmSet.Default,
        };
        SshConnection? connection = null;
        for (int attempt = 0; connection is null; attempt++)
        {
            try
            {
                connection = await SshConnection.ConnectAsync(connect);
            }
            catch (Exception) when (attempt < 20)
            {
                await Task.Delay(250);   // sshd 还没起来
            }
        }
        return new SshLauncher(container, connection, server, options);
    }

    private static async Task DockerAsync(params string[] args)
    {
        using Process p = Process.Start(new ProcessStartInfo("docker", args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', args)}: {err.Trim()}");
        }
    }

    public async Task<IClientProcess> LaunchAsync(string command)
    {
        X11ForwardOptions x11 = new()
        {
            Display = X11Display.Parse($":{_options.Display}"),
            IsTrusted = true,
            Timeout = Timeout.InfiniteTimeSpan,
            LocalConnector = ct =>
            {
                (InMemoryDuplexStream serverSide, InMemoryDuplexStream clientSide) = InMemoryTransport.CreatePair();
                _ = ServeAsync(serverSide);
                return ValueTask.FromResult<Stream>(clientSide);
            },
        };
        SshCommand ssh = await _connection.ExecuteAsync(command, new SshCommandOptions { X11Forwarding = x11 });
        SshClient client = new(ssh);
        client.Pump(ssh.StandardOutput);
        client.Pump(ssh.StandardError);
        return client;
    }

    private async Task ServeAsync(InMemoryDuplexStream stream)
    {
        try
        {
            await _server.ServeAuthenticatedAsync(stream, "perf@sshd");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
        {
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await DockerAsync("rm", "-f", _container);
    }

    private sealed class SshClient(SshCommand command) : LineSink, IClientProcess
    {
        public void Pump(PipeReader reader) => _ = Task.Run(async () =>
        {
            StringBuilder line = new();
            try
            {
                while (true)
                {
                    ReadResult read = await reader.ReadAsync();
                    foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                    {
                        foreach (char ch in Encoding.UTF8.GetString(segment.Span))
                        {
                            if (ch == '\n')
                            {
                                Add(line.ToString());
                                line.Clear();
                            }
                            else
                            {
                                line.Append(ch);
                            }
                        }
                    }
                    reader.AdvanceTo(read.Buffer.End);
                    if (read.IsCompleted)
                    {
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // 命令结束、连接断了
            }
        });

        public async ValueTask DisposeAsync()
        {
            try
            {
                using CancellationTokenSource wait = new(TimeSpan.FromSeconds(3));
                await command.SendSignalAsync("KILL", wait.Token);
            }
            catch (Exception)
            {
                // 已经结束,或者服务端不认 signal 请求(timeout -s KILL 兜底)
            }
            await command.DisposeAsync();
        }
    }
}
