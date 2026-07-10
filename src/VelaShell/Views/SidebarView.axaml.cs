using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    public event EventHandler? OpenConnectionProfileRequested;

    /// <summary>Raised by the footer gear button to open the settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user double-clicks a recent connection to reconnect to it.</summary>
    public event EventHandler<RecentConnectionEntry>? RecentConnectRequested;

    private void OpenConnectionProfile_Click(object? sender, RoutedEventArgs e)
    {
        OpenConnectionProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RecentConnection_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RecentConnectionItemViewModel item })
        {
            RecentConnectRequested?.Invoke(this, item.Entry);
        }
    }
}
