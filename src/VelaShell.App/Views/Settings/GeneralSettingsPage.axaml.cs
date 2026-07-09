using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.App.ViewModels;

namespace VelaShell.App.Views.Settings;

public partial class GeneralSettingsPage : UserControl
{
    public GeneralSettingsPage()
    {
        InitializeComponent();
    }

    private async void ExportSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出配置",
            SuggestedFileName = "velashell-settings.json",
            DefaultExtension = "json",
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, viewModel.BuildExportJson());
        }
    }

    private async void ImportSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入配置",
            AllowMultiple = false,
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path && File.Exists(path))
        {
            viewModel.TryApplyImportedJson(await File.ReadAllTextAsync(path));
        }
    }
}
