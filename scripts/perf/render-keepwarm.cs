#:project ../../src/VelaShell/VelaShell.csproj
#:property TreatWarningsAsErrors=false
#:property AllowUnsafeBlocks=true
#:property SignAssembly=false
#:property ValidateExecutableReferencesMatchSelfContained=false

// 主窗口渲染停顿的测量(只在 Windows 上有意义;feature-plan.md「整个应用的渲染停顿」,plan.md §201 四 / §219):
// 改一次字,量到这一批被渲染线程处理完为止,对照挂与不挂候选修法「保温」(见文末 KeepWarm)。
//
//   dotnet run -c Release -p:SignAssembly=false -p:ValidateExecutableReferencesMatchSelfContained=false scripts/perf/render-keepwarm.cs -- [选项]
//
// 一定用 Release。两个 -p 的理由同 scripts/xserver/perf/perf.cs:仓库里没有签名密钥;主程序 Release 自包含,被非自包含的脚本引用要关掉那道检查。
// 引用主程序工程只为拿到同一版 Avalonia 与同一套渲染设置。
//
// ⚠️ 别用帧回调(RequestAnimationFrame)的延迟当指标:窗口闲着时 Avalonia 在 UI 线程上当场补发回调(实测恒为 0 毫秒),
// 而停顿停住的是渲染线程(等系统合成器确认上一次提交)。要等 Compositor.RequestCommitAsync() —— 这一批真被处理完。
//
// 模式:
//   --mode steady      每 33 毫秒改一次文字(scripts/xserver/perf 的 --render-probe 那种节奏;零星出帧最密的情形),
//                      保温开着时每一拍都当作一次输入
//   --mode typing      按打字节奏:按键 → 隔 --echo 毫秒回显,回显连出 --burst 帧(文字、光标……)之后闲下来(默认)
// 选项:
//   --warm on|off      是否挂保温(默认 on)
//   --seconds N        steady 跑多久(默认 180)
//   --keys N           typing 模拟多少次按键(默认 600)
//   --gap A-B          typing 两次按键之间隔多少毫秒,均匀随机(默认 80-600)
//   --echo MS          typing 按键到回显隔多少毫秒(默认 10,模拟一次往返)
//   --burst N          typing 每次回显连出几帧(默认 3)
//   --seed N           随机数种子(默认 1),两次对照用同一个
//
// 渲染设置与主程序一致(Program.ResolveRenderingMode 的 GPU 档:AngleEgl,不行退 Software;合成模式用 Avalonia 默认候选)。
// 改字到这一批处理完超过 50 毫秒的打一行「[停顿]」,最后报分布。数字只用来比较前后改动。

using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

ProbeOptions options = ProbeOptions.Parse(args);
if (OperatingSystem.IsWindows()) Native.TimeBeginPeriod(1);
Console.WriteLine(options.Steady
    ? $"模式:steady  保温:{(options.Warm ? "开" : "关")}  时长:{options.Seconds} s"
    : $"模式:typing  保温:{(options.Warm ? "开" : "关")}  按键:{options.Keys}  间隔:{options.GapMin}-{options.GapMax} ms  回显:{options.EchoMs} ms × {options.Burst} 帧");
return AppBuilder.Configure(() => new ProbeApp(options)).UsePlatformDetect()
    .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] })
    .StartWithClassicDesktopLifetime(args);

sealed record ProbeOptions(bool Steady, bool Warm, int Seconds, int Keys, int GapMin, int GapMax, int EchoMs, int Burst, int Seed)
{
    public static ProbeOptions Parse(string[] args)
    {
        var o = new ProbeOptions(false, true, 180, 600, 80, 600, 10, 3, 1);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode": o = o with { Steady = args[++i] == "steady" }; break;
                case "--warm": o = o with { Warm = args[++i] != "off" }; break;
                case "--seconds": o = o with { Seconds = int.Parse(args[++i]) }; break;
                case "--keys": o = o with { Keys = int.Parse(args[++i]) }; break;
                case "--gap":
                    string[] gap = args[++i].Split('-');
                    o = o with { GapMin = int.Parse(gap[0]), GapMax = int.Parse(gap[1]) };
                    break;
                case "--echo": o = o with { EchoMs = int.Parse(args[++i]) }; break;
                case "--burst": o = o with { Burst = int.Parse(args[++i]) }; break;
                case "--seed": o = o with { Seed = int.Parse(args[++i]) }; break;
            }
        }
        return o;
    }
}

sealed class ProbeApp(ProbeOptions options) : Application
{
    private readonly List<double> _samples = [];
    private readonly Stopwatch _clock = new();

    // 没有主题,窗口就没有模板:文字不上屏、也没有浮层,量到的是「什么都不画」(scripts/xserver/perf/perf.cs 同样要加)
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var text = new TextBlock { Text = "measuring…", Margin = new Thickness(12) };
            var window = new Window { Title = "VelaShell render keep-warm probe", Width = 420, Height = 80, Content = text, Topmost = true };
            KeepWarm? warm = options.Warm ? new KeepWarm(window) : null;
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                window.Activate();
                await Task.Delay(2000);   // 让启动时的那一串帧过去
                // 文字没挂进可视树、或保温控件没挂上浮层,量出来的数没有意义:报错退出,别给一份「0 次停顿」
                if (TopLevel.GetTopLevel(text) is null || warm is { Attached: false })
                {
                    Console.Error.WriteLine("探针没挂上:文字不在可视树里或保温控件没进浮层,结果作废。");
                    desktop.Shutdown(2);
                    return;
                }
                _clock.Start();
                if (options.Steady) await RunSteadyAsync(window, text, warm);
                else await RunTypingAsync(window, text, warm);
                Report();
                desktop.Shutdown();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 改一次字,量到这一批被渲染线程处理完为止。帧回调量不出这种停顿:窗口闲着时 Avalonia 在 UI 线程上当场补发回调,
    /// 停住的是渲染线程(它在等系统合成器确认上一次提交),所以要等「这一批处理完」。
    /// </summary>
    private async Task FrameAsync(Window window, TextBlock text, string label)
    {
        text.Text = label;
        long requested = Stopwatch.GetTimestamp();
        await Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(window)!.Compositor.RequestCommitAsync();
        double ms = Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
        _samples.Add(ms);
        if (ms > 50)
        {
            Console.WriteLine($"    [停顿] {label} 处理完用了 {ms:F0} ms,发生在 {_clock.Elapsed.TotalSeconds:F1} s");
        }
    }

    private async Task RunSteadyAsync(Window window, TextBlock text, KeepWarm? warm)
    {
        int n = 0;
        while (_clock.Elapsed < TimeSpan.FromSeconds(options.Seconds))
        {
            await Task.Delay(33);
            warm?.Warm();
            await FrameAsync(window, text, $"tick {++n}");
        }
    }

    private async Task RunTypingAsync(Window window, TextBlock text, KeepWarm? warm)
    {
        var random = new Random(options.Seed);
        for (int key = 1; key <= options.Keys; key++)
        {
            await Task.Delay(random.Next(options.GapMin, options.GapMax + 1));
            warm?.Warm();   // 窗口收到了按键(候选修法里输入处理就是这一句)
            await Task.Delay(options.EchoMs);
            for (int f = 1; f <= options.Burst; f++)
            {
                await FrameAsync(window, text, $"key {key}.{f}");
            }
        }
    }

    private void Report()
    {
        // --keys 0、--seconds 0 一帧都不量:没有分布可报,别在 _samples[^1] 上抛异常
        if (_samples.Count == 0)
        {
            Console.WriteLine("一帧都没量到(--keys 0 或 --seconds 0),没有分布可报。");
            return;
        }
        _samples.Sort();
        double Pct(double p) => _samples[Math.Min(_samples.Count - 1, (int)(p * _samples.Count))];
        Console.WriteLine($"改字到处理完:p50 {Pct(.5):F1} ms  p90 {Pct(.9):F1} ms  p99 {Pct(.99):F1} ms  最长 {_samples[^1]:F0} ms  共 {_samples.Count} 帧");
        Console.WriteLine($"超过 50 ms:{_samples.Count(ms => ms > 50)} 次  超过 100 ms:{_samples.Count(ms => ms > 100)} 次  超过 300 ms:{_samples.Count(ms => ms > 300)} 次");
    }
}

static partial class Native
{
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint period);
}

/// <summary>
/// 候选修法「保温」:输入之后两秒之内,每一帧都让浮层里一个 1×1 的透明控件重画一次,渲染循环不转空拍
/// (X 窗口用的是同一个办法,见 XNativeWindow.XSurface.KeepWarm)。2026-10-19 在开发机上停顿复现不出来,
/// 没有挂进主程序;哪台机器复现了,先用本脚本对照开关两种情形,量出收益再决定挂不挂(plan.md §219)。
/// </summary>
sealed class KeepWarm
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);
    private readonly TopLevel _topLevel;
    private readonly Pulse _pulse = new();
    private long _warmUntil;
    private bool _frameRequested;

    public KeepWarm(Window window)
    {
        _topLevel = window;
        window.Opened += (_, _) =>
        {
            // 取不到浮层(窗口没有主题模板)就挂不上;不再静默略过,由 Attached 交给探针报错
            if (OverlayLayer.GetOverlayLayer(window) is not { } layer) return;
            layer.Children.Add(_pulse);
            Attached = true;
        };
    }

    /// <summary>1×1 的重画控件是否已经挂进浮层。</summary>
    public bool Attached { get; private set; }

    public void Warm()
    {
        _warmUntil = Stopwatch.GetTimestamp() + (long)(Duration.TotalSeconds * Stopwatch.Frequency);
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested) return;
        _frameRequested = true;
        _topLevel.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            if (Stopwatch.GetTimestamp() >= _warmUntil) return;
            _pulse.InvalidateVisual();
            RequestFrame();
        });
    }

    private sealed class Pulse : Control
    {
        public Pulse()
        {
            Width = 1;
            Height = 1;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => context.FillRectangle(Brushes.Transparent, new Rect(0, 0, 1, 1));
    }
}
