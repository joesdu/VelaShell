using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.App.ViewModels;

namespace VelaShell.App.Views.Settings;

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
            // 直接写控件,由 TwoWay 绑定回写 POCO —— Transfer 引用不变时仅靠
            // RaisePropertyChanged 刷新,绑定可能因引用相同跳过重读(输入框不回显)。
            DownloadDirBox.Text = path;
            viewModel.RaisePropertyChangedForTransfer();
        }
    }

    private async void BrowseEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var options = new FilePickerOpenOptions
        {
            Title = "选择默认编辑器程序",
            AllowMultiple = false,
        };
        if (System.OperatingSystem.IsWindows())
        {
            options.FileTypeFilter = new[]
            {
                new FilePickerFileType("可执行程序") { Patterns = new[] { "*.exe", "*.bat", "*.cmd" } },
                FilePickerFileTypes.All,
            };
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            EditorPathBox.Text = path;
            viewModel.RaisePropertyChangedForTransfer();
        }
    }
}
