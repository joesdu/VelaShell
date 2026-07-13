using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>传输设置分页视图,配置下载目录、外部编辑器等文件传输选项。</summary>
public partial class TransferSettingsPage : UserControl
{
    /// <summary>初始化 <see cref="TransferSettingsPage"/> 并加载 XAML 组件。</summary>
    public TransferSettingsPage()
    {
        InitializeComponent();
    }

    private async void BrowseDownloadDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = Strings.Get("SelectDownloadFolder"),
            AllowMultiple = false
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
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        var options = new FilePickerOpenOptions
        {
            Title = Strings.Get("SetTransfer_SelectEditorTitle"),
            AllowMultiple = false
        };
        if (OperatingSystem.IsWindows())
        {
            options.FileTypeFilter =
            [
                new(Strings.Get("SetTransfer_ExecutableFilter")) { Patterns = ["*.exe", "*.bat", "*.cmd"] },
                FilePickerFileTypes.All
            ];
        }
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            EditorPathBox.Text = path;
            viewModel.RaisePropertyChangedForTransfer();
        }
    }
}
