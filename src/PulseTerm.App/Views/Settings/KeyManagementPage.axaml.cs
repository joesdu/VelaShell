using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Ssh;

namespace PulseTerm.App.Views.Settings;

public partial class KeyManagementPage : UserControl
{
    public KeyManagementPage()
    {
        InitializeComponent();
    }

    private async void ImportKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 SSH 私钥",
            AllowMultiple = false,
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await viewModel.SshKeys.ImportAsync(path);
        }
    }

    private async void CopyPublicKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SshKeyInfo key }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(key.PublicKeyLine ?? key.Fingerprint);
        }
    }
}
