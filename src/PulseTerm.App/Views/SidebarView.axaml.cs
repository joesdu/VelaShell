using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PulseTerm.App.Views;

public partial class SidebarView : UserControl
{
    public event EventHandler? OpenConnectionProfileRequested;

    public SidebarView()
    {
        InitializeComponent();
    }

    private void OpenConnectionProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenConnectionProfileRequested?.Invoke(this, EventArgs.Empty);
    }
}
