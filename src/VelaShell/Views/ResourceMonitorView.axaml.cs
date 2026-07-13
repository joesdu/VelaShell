using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>资源监控视图:面板可见时每秒轮询刷新 CPU/内存等资源指标。</summary>
public partial class ResourceMonitorView : UserControl
{
    private DispatcherTimer? _pollTimer;

    /// <summary>初始化资源监控视图并挂接可见性驱动的轮询逻辑。</summary>
    public ResourceMonitorView()
    {
        InitializeComponent();

        // Poll only while the panel is actually visible (it lives in the tab-hover tooltip):
        // one immediate fetch on open, then per-second refresh (§11), stopped on close.
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Refresh();
        _pollTimer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Refresh());
        _pollTimer.Start();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void Refresh()
    {
        if (DataContext is ResourceMonitorViewModel vm)
        {
            _ = vm.RefreshAsync();
        }
    }
}
