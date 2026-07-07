using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Views;

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
        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
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
            _ = vm.RefreshAsync();
    }
}
