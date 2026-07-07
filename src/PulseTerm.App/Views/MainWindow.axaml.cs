using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.Services;

namespace PulseTerm.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (this.FindControl<SidebarView>("SidebarHost") is { } sidebar)
        {
            sidebar.OpenConnectionProfileRequested += OnOpenConnectionProfileRequested;
            sidebar.ConnectRequested += OnSidebarConnectRequested;
        }

        Opened += OnWindowOpened;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.InitializeAsync();
    }

    // The window uses the native OS title bar per design spec §2 — no custom chrome.

    private async void OnSidebarConnectRequested(object? sender, SessionProfile profile)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var tab = await vm.TryConnectProfileAsync(profile);
        if (tab is null && vm.LastConnectionError is { Length: > 0 } error)
            await ShowConnectionErrorAsync(error);
    }

    private async void OnOpenConnectionProfileRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }

        if (App.Current is not App app || app.Services is null)
        {
            return;
        }

        var connectionProfileViewModel = new ConnectionProfileViewModel(
            connectionWorkflowService: app.Services.GetService<IConnectionWorkflowService>());

        var dialog = new ConnectionProfileView
        {
            DataContext = connectionProfileViewModel
        };

        var profile = await dialog.ShowDialog<SessionProfile?>(this);
        if (profile is null)
        {
            return;
        }

        // TryConnectProfileAsync never throws — a failed auth/connection is reported, not crashed.
        var tab = await mainWindowViewModel.TryConnectProfileAsync(profile);
        if (tab is null && mainWindowViewModel.LastConnectionError is { Length: > 0 } error)
        {
            await ShowConnectionErrorAsync(error);
        }
    }

    private Task ShowConnectionErrorAsync(string message)
    {
        var okButton = new Button
        {
            Content = "确定",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 6),
        };

        var dialog = new Window
        {
            Title = "连接失败",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.FindResource("PulseBgSurface") as IBrush ?? Brushes.Transparent,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 20,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = this.FindResource("PulseTextPrimary") as IBrush ?? Brushes.White,
                    },
                    okButton,
                },
            },
        };

        okButton.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(this);
    }
}
