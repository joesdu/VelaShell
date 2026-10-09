using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 插件窗口的初始尺寸:按设计值给,但不超过所在屏幕的工作区。
/// </summary>
/// <remarks>
/// 宿主照 <c>PanelOptions.WindowWidth/Height</c> 原样开窗,不替插件收边 ——
/// 设计值放大之后,1366×768 或高缩放比的屏幕上窗口底边(连同底部按钮)会落到任务栏后面。
/// 拿不到屏幕信息(不在 UI 线程、无头测试、隔离进程里没有主窗口)时按设计值原样返回。
/// </remarks>
internal static class WindowFit
{
    /// <summary>四周留给任务栏与投影的余量(逻辑像素)。</summary>
    private const double ScreenMargin = 48;

    /// <summary>把设计尺寸夹到屏幕工作区以内。</summary>
    /// <param name="width">设计宽。</param>
    /// <param name="height">设计高。</param>
    /// <param name="anchor">用它所在的屏幕;null 时用主窗口所在的屏幕。</param>
    public static (double Width, double Height) Fit(double width, double height, Visual? anchor = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return (width, height);
        }
        try
        {
            TopLevel? top = anchor is null ? null : TopLevel.GetTopLevel(anchor);
            top ??= (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            Screen? screen = top?.Screens is { } screens ? screens.ScreenFromTopLevel(top) ?? screens.Primary : null;
            if (screen is not { Scaling: > 0 })
            {
                return (width, height);
            }
            double maxWidth = screen.WorkingArea.Width / screen.Scaling - ScreenMargin;
            double maxHeight = screen.WorkingArea.Height / screen.Scaling - ScreenMargin;
            return (Math.Min(width, Math.Max(maxWidth, 320)), Math.Min(height, Math.Max(maxHeight, 240)));
        }
        catch
        {
            // 屏幕信息在某些平台/远程桌面下不一定可用:拿不到就不收边。
            return (width, height);
        }
    }
}
