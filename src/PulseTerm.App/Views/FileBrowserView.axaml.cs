using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Views;

public partial class FileBrowserView : UserControl
{
    public FileBrowserView()
    {
        InitializeComponent();

        // The VM cannot touch Avalonia storage APIs; the view supplies the OS pickers.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not FileBrowserViewModel vm)
                return;

            vm.PickFilesForUpload = PickFilesAsync;
            vm.PickSavePathForDownload = PickSavePathAsync;
        };
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return Array.Empty<string>();

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = true,
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
    }

    private async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存到本地",
            SuggestedFileName = suggestedName,
        });

        return file?.TryGetLocalPath();
    }
}