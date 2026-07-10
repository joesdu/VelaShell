using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.ViewModels;

namespace VelaShell.Views;

public partial class ResourceMonitorView : UserControl
{
    private DispatcherTimer? _pollTimer;

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
