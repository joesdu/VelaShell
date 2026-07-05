using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PulseTerm.Core.Models;

namespace PulseTerm.App.Views;

public partial class SidebarView : UserControl
{
    public event EventHandler? OpenConnectionProfileRequested;

    /// <summary>Raised when the user double-clicks a saved/recent connection to reconnect to it.</summary>
    public event EventHandler<SessionProfile>? ConnectRequested;

    public SidebarView()
    {
        InitializeComponent();
    }

    private void OpenConnectionProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenConnectionProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RecentConnection_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionProfile profile })
            ConnectRequested?.Invoke(this, profile);
    }
}
