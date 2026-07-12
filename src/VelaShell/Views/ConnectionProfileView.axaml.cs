using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

public partial class ConnectionProfileView : Window
{
    public ConnectionProfileView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        viewModel.SaveCommand.Subscribe(Close);
        viewModel.ConnectCommand.Subscribe(Close);
        viewModel.CancelCommand.Subscribe(Close);
        await viewModel.LoadGroupsAsync();
    }

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async void BrowseKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
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
