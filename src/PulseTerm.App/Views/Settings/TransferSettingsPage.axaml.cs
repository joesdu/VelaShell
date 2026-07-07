using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Views.Settings;

public partial class TransferSettingsPage : UserControl
{
    public TransferSettingsPage()
    {
        InitializeComponent();
    }

    private async void BrowseDownloadDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择下载目录",
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.Transfer.LocalDownloadDirectory = path;
            // POCO 无变更通知,强制刷新绑定。
            viewModel.RaisePropertyChangedForTransfer();
        }
    }
}
