using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

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
        viewModel.LoginCommand.Subscribe(Close);
        viewModel.CancelCommand.Subscribe(Close);
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
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    }
}
