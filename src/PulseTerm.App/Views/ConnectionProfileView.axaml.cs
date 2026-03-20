using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using PulseTerm.App.ViewModels;
using System.Reactive.Linq;

namespace PulseTerm.App.Views;

public partial class ConnectionProfileView : Window
{
    public ConnectionProfileView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }

        viewModel.SaveCommand.Subscribe(profile => Close(profile));
        viewModel.CancelCommand.Subscribe(profile => Close(profile));
    }
}
