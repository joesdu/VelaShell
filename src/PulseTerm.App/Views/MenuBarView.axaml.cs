using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PulseTerm.App.Views;

public partial class MenuBarView : UserControl
{
    public MenuBarView()
    {
        InitializeComponent();
    }

    /// <summary>Closes any open dropdown (flyouts don't auto-close on inner button clicks).
    /// The entry's own Command still executes via its binding.</summary>
    private void Entry_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
            button.Flyout?.Hide();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        Entry_Click(sender, e);
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
