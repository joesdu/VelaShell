using System.Globalization;
using VelaShell.Splash;

namespace VelaShell.Tests.Splash;

/// <summary>
/// 原生启动画面窗口的冒烟测试:真建一个分层窗口、跑起消息循环、请求关闭,确认它淡出后自己退出。
/// </summary>
/// <remarks>
/// 守的是互操作那一层(结构体布局、窗口过程回调、DIB 与 Skia 表面的对接):这些写错了编译照样过,
/// 运行时要么建不出窗口,要么在非托管回调里把进程带走。测试期间桌面上会闪过一下启动画面。
/// </remarks>
[TestClass]
public sealed class Win32SplashWindowTests
{
    [TestMethod]
    public void Window_ShowsRendersAndClosesOnRequest()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            Assert.Inconclusive("启动画面窗口只在 Windows 10 1607+ 上有。");
        }
        Exception? failure = null;
        Win32SplashWindow? created = null;
        int frames = 0;
        using ManualResetEventSlim ready = new();
        Thread thread = new(() =>
        {
            try
            {
                SplashNative.SetThreadDpiAwarenessContext(SplashNative.DpiAwarenessPerMonitorV2);
                SplashSimulation simulation = new();
                using SplashRenderer renderer = SplashRenderer.Create(
                    "terminal", SplashPalette.Resolve("dark", null, true), SplashStrings.Load(),
                    SplashResources.Create(CultureInfo.GetCultureInfo("en-US")));
                using Win32SplashWindow window = new(renderer, () =>
                {
                    Interlocked.Increment(ref frames);
                    return simulation.At(600);
                });
                created = window;
                ready.Set();
                window.Run();
            }
            catch (Exception ex)
            {
                failure = ex;
                ready.Set();
            }
        }) { IsBackground = true };
        thread.Start();

        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(10)), "窗口没建起来。");
        Assert.IsNull(failure, failure?.ToString());
        Thread.Sleep(200);
        created!.RequestClose();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "请求关闭后消息循环没有退出。");
        Assert.IsNull(failure, failure?.ToString());
        Assert.IsGreaterThan(3, Volatile.Read(ref frames), "定时器没有驱动重绘。");
    }
}
