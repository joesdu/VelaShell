using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

public partial class GeneralSettingsPage : UserControl
{
    public GeneralSettingsPage()
    {
        InitializeComponent();
    }

    private async void ExportSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("SetGeneral_ExportDialogTitle"),
            SuggestedFileName = "velashell-settings.json",
            DefaultExtension = "json"
        });
        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, viewModel.BuildExportJson());
        }
    }

    private async void ImportSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("SetGeneral_ImportDialogTitle"),
            AllowMultiple = false
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path && File.Exists(path))
        {
            viewModel.TryApplyImportedJson(await File.ReadAllTextAsync(path));
        }
    }

    /// <summary>清除历史是破坏性操作:先确认再执行(设置审计 §12 破坏性操作需确认)。</summary>
    private async void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        bool confirmed = await Views.MessageDialog.ConfirmAsync(owner, Strings.Get("SetGeneral_ClearHistory"),
                             Strings.Get("SetGeneral_ClearHistoryConfirm"),
                             danger: true);
        if (confirmed)
        {
            viewModel.ClearHistoryCommand.Execute().Subscribe();
        }
    }
}
