using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
            Title = "导出配置",
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
            Title = "导入配置",
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
        bool confirmed = await Views.MessageDialog.ConfirmAsync(owner, "清除历史记录",
                             "将清除命令历史和最近连接记录,此操作不可撤销。确定继续吗?",
                             danger: true);
        if (confirmed)
        {
            viewModel.ClearHistoryCommand.Execute().Subscribe();
        }
    }
}
