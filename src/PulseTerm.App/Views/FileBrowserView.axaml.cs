using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
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
            vm.PromptForText = PromptForTextAsync;
            vm.CopyToClipboard = CopyToClipboardAsync;
            vm.ShowFileProperties = ShowFilePropertiesAsync;
        };
    }

    /// <summary>Double-clicking a row descends into a directory (files are left to the toolbar
    /// download action, which needs a save target).</summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
            return;

        var row = (e.Source as Control)?.DataContext as RemoteFileInfoViewModel;
        if (row is null || !row.IsDirectory)
            return;

        vm.ActivateCommand.Execute(row).Subscribe(_ => { }, _ => { });
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

    /// <summary>Modal single-line text prompt used by new folder / new file / rename / move.
    /// Returns the entered text, or null if the user cancelled.</summary>
    private async Task<string?> PromptForTextAsync(string title, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        var textBox = new TextBox { Text = initialValue, MinWidth = 340 };
        var okButton = new Button { Content = "确定", IsDefault = true, MinWidth = 72 };
        var cancelButton = new Button { Content = "取消", IsCancel = true, MinWidth = 72 };

        string? result = null;
        var dialog = new Window
        {
            Title = title,
            CanResize = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title },
                    textBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, okButton },
                    },
                },
            },
        };

        okButton.Click += (_, _) => { result = textBox.Text; dialog.Close(); };
        cancelButton.Click += (_, _) => { result = null; dialog.Close(); };
        dialog.Opened += (_, _) => { textBox.SelectAll(); textBox.Focus(); };

        await dialog.ShowDialog(owner);
        return result;
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>Read-only modal listing a remote entry's metadata.</summary>
    private async Task ShowFilePropertiesAsync(RemoteFileInfoViewModel file)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var rows = new StackPanel { Spacing = 6 };
        void AddRow(string label, string value) =>
            rows.Children.Add(new TextBlock { Text = $"{label}：{value}" });

        AddRow("名称", file.Name);
        AddRow("路径", file.FullPath);
        AddRow("类型", file.IsDirectory ? "文件夹" : "文件");
        AddRow("大小", file.FormattedSize);
        AddRow("权限", file.Permissions);
        AddRow("修改时间", file.FormattedModifiedTime);

        var okButton = new Button
        {
            Content = "确定",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 72,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var dialog = new Window
        {
            Title = "属性",
            CanResize = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children = { rows, okButton },
            },
        };

        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }
}