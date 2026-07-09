using System;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.App.ViewModels;

namespace VelaShell.App.Views;

public partial class AuthenticationDialogView : Window
{
    public AuthenticationDialogView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not AuthenticationDialogViewModel viewModel)
        {
            return;
        }

        viewModel.LoginCommand.Subscribe(result => Close(result));
        viewModel.CancelCommand.Subscribe(result => Close(result));
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async void BrowseKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AuthenticationDialogViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择私钥文件",
            AllowMultiple = false,
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    }
}
