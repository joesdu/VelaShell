using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Diagnostics;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Splash;

/// <summary>
/// 启动画面:进程一起来就在独立线程上显示,主窗口画完第一帧时淡出。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 Avalonia 窗口</b>:冷启动打点里,Avalonia 平台初始化加 XAML 加载要 3 秒多
/// (见 ~/.velashell/logs 的 [Startup] timeline),Avalonia 窗口最早也只能在那之后出现 —— 而这几秒
/// 正是用户以为「打不开」的那段空白。所以在 <c>Main</c> 里就起一个原生窗口(<see cref="Win32SplashWindow" />),
/// 画法交给 Skia(Avalonia 本来就要装载它,早装载不白花钱)。
/// </para>
/// <para>
/// <b>只在 Windows 上</b>:macOS 有 Dock 图标弹跳、Linux 桌面有启动通知,系统本身就给了「正在打开」的反馈;
/// Windows 只有一个几秒就消失的忙碌光标。
/// </para>
/// <para>
/// <b>永不挡启动</b>:读镜像、建窗口、画图任何一步出错都只记日志,画面不出来而已。
/// 第一帧迟迟不来时有一根保险丝(<see cref="Fuse" />)兜底关掉;启动中途失败由 <c>Program.Main</c>
/// 在弹错误框之前同步关掉,免得错误框压在启动画面底下。
/// </para>
/// </remarks>
internal static class SplashScreen
{
    /// <summary>关掉启动画面的环境变量(<c>VELASHELL_NO_SPLASH=1</c>):自动化测试、录屏与排障用。</summary>
    public const string DisableEnvironmentVariable = "VELASHELL_NO_SPLASH";

    /// <summary>第一帧一直不来时,画面最多留多久。实测最慢的冷启动首帧约 8 秒。</summary>
    private static readonly TimeSpan Fuse = TimeSpan.FromSeconds(60);

    private static readonly Lock Gate = new();
    private static Thread? _thread;
    private static Win32SplashWindow? _window;
    private static bool _closeRequested;

    /// <summary>
    /// 在后台线程上显示启动画面(样式、主题、语言取自镜像文件)。重复调用只有第一次生效;
    /// 非 Windows、设置为「不显示」或被环境变量关掉时什么都不做。
    /// </summary>
    public static void Start()
    {
        // SetThreadDpiAwarenessContext 要 Windows 10 1607。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)
            || Environment.GetEnvironmentVariable(DisableEnvironmentVariable) == "1")
        {
            return;
        }
        lock (Gate)
        {
            if (_thread is not null)
            {
                return;
            }
            // 刻意留在默认的 MTA:窗口不收输入,用不到 STA 的那一套(拖放、剪贴板、输入法);
            // 留在 MTA,主线程上的 COM 调用就不会被封送到这条线程上等它泵消息。
            _thread = new Thread(Run) { IsBackground = true, Name = "VelaShell splash" };
            _thread.Start();
        }
        _ = FirstFrameSignal.WaitAsync(Fuse).ContinueWith(_ => Close(), TaskScheduler.Default);
    }

    /// <summary>关掉启动画面(淡出)。可在任意线程调用,没显示时什么都不做。</summary>
    /// <param name="wait">是否等画面线程退出(最多 1 秒)。弹错误框之前要等,否则错误框会被画面压住。</param>
    public static void Close(bool wait = false)
    {
        Thread? thread;
        lock (Gate)
        {
            _closeRequested = true;
            _window?.RequestClose();
            thread = _thread;
        }
        if (wait && thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private static void Run()
    {
        SplashProgressTracker tracker = new();
        Action<string, TimeSpan> onMark = tracker.Record;
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
            {
                return;
            }
            StartupAppearance appearance = StartupAppearance.Read(new VelaShellStoragePaths().StartupAppearanceFile);
            if (appearance.SplashStyle == SplashStyles.None)
            {
                return;
            }

            // 先订阅再补齐:订阅之前已经打过的点(至少有 Main)从快照里补,之后的点由事件送来。
            StartupTrace.MarkRecorded += onMark;
            foreach ((string name, TimeSpan at) in StartupTrace.Marks)
            {
                tracker.Record(name, at);
            }

            // 本线程用设置里的界面语言取词;主线程的文化此时还是系统的,不受影响。
            CultureInfo culture = ResolveCulture(appearance.Language);
            Thread.CurrentThread.CurrentUICulture = culture;
            SplashNative.SetThreadDpiAwarenessContext(SplashNative.DpiAwarenessPerMonitorV2);

            SplashPalette palette = SplashPalette.Resolve(appearance.Theme, appearance.Accent, SystemPrefersDark());
            using SplashRenderer renderer = SplashRenderer.Create(appearance.SplashStyle, palette, SplashStrings.Load(), SplashResources.Create(culture));
            double shownAt = StartupTrace.Elapsed.TotalMilliseconds;
            using Win32SplashWindow window = new(renderer, () => tracker.Snapshot(StartupTrace.Elapsed.TotalMilliseconds, shownAt));
            lock (Gate)
            {
                if (_closeRequested)
                {
                    return;
                }
                _window = window;
            }
            StartupTrace.Mark("SplashShown");
            window.Run();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Splash] Splash screen failed: {ex}");
        }
        finally
        {
            StartupTrace.MarkRecorded -= onMark;
            lock (Gate)
            {
                _window = null;
            }
        }
    }

    /// <summary>设置里的界面语言;空或认不出来时按系统界面语言挑(与 <see cref="LocalizationService" /> 同一口径)。</summary>
    internal static CultureInfo ResolveCulture(string? language)
    {
        try
        {
            return new CultureInfo(string.IsNullOrWhiteSpace(language)
                ? LocalizationService.ResolveSystemLanguage(CultureInfo.CurrentUICulture)
                : language.Trim());
        }
        catch (CultureNotFoundException)
        {
            return new CultureInfo(LocalizationService.ResolveSystemLanguage(CultureInfo.CurrentUICulture));
        }
    }

    /// <summary>Windows 的「应用模式」是否为暗色(主题选「跟随系统」时用)。读不到按亮色 —— 那是 Windows 的出厂值。</summary>
    private static bool SystemPrefersDark()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                                     "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
