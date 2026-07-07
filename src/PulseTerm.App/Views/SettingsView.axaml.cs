using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Views;

public partial class SettingsView : Window
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.CloseRequested -= OnCloseRequested;
            }

            _viewModel = DataContext as SettingsViewModel;
            if (_viewModel is not null)
            {
                _viewModel.CloseRequested += OnCloseRequested;
            }
        };
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
