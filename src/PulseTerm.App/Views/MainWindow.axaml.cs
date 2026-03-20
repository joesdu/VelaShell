using System;
using Avalonia.Controls;
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
        }
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

        await mainWindowViewModel.ConnectProfileAsync(profile);
    }
}
