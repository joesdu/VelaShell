using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Views;

public partial class FileBrowserView : UserControl
{
    private const double MinNameWidth = 180;
    private const double MinSizeWidth = 70;
    private const double MinPermissionsWidth = 80;
    private const double MinModifiedWidth = 110;
    private string? _activeSplitter;
    private double _dragStartX;
    private double _startNameWidth;
    private double _startSizeWidth;
    private double _startPermissionsWidth;

    private static readonly FontFamily TerminalFont = new("JetBrains Mono, Cascadia Mono, Consolas, monospace");
    private static readonly Typeface TerminalTypeface = new(TerminalFont);

    public FileBrowserView()
    {
        InitializeComponent();

        // The VM cannot touch Avalonia storage APIs; the view supplies the OS pickers.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not FileBrowserViewModel vm)
                return;

            vm.PickFilesForUpload = PickFilesAsync;
            vm.PickFolderForUpload = PickFolderAsync;
            vm.PickSavePathForDownload = PickSavePathAsync;
            vm.PromptForText = PromptForTextAsync;
            vm.CopyToClipboard = CopyToClipboardAsync;
            vm.ShowFileProperties = ShowFilePropertiesAsync;
            vm.ConfirmDelete = ConfirmAsync;
        };

        // Accept dropping local files/folders onto the list to upload into CurrentPath.
        if (this.FindControl<ListBox>("FileList") is { } fileList)
        {
            DragDrop.SetAllowDrop(fileList, true);
            fileList.AddHandler(DragDrop.DragOverEvent, OnFileListDragOver);
            fileList.AddHandler(DragDrop.DropEvent, OnFileListDrop);
        }

        AddHandler(PointerMovedEvent, OnColumnSplitterPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnColumnSplitterReleased, RoutingStrategies.Tunnel);
    }

    private void OnColumnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm || sender is not Control { Tag: string tag })
            return;

        // Use ClickCount here so auto-fit still works even when pointer handling/capture is active.
        if (e.ClickCount >= 2)
        {
            AutoFitColumnBySplitterTag(vm, tag);
            e.Handled = true;
            return;
        }

        _activeSplitter = tag;
        _dragStartX = e.GetPosition(this).X;
        _startNameWidth = vm.NameColumnWidth.Value;
        _startSizeWidth = vm.SizeColumnWidth.Value;
        _startPermissionsWidth = vm.PermissionsColumnWidth.Value;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnColumnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeSplitter is null || DataContext is not FileBrowserViewModel vm)
            return;

        var delta = e.GetPosition(this).X - _dragStartX;

        switch (_activeSplitter)
        {
            case "NameSize":
                vm.NameColumnWidth = new GridLength(ClampColumnWidth(_startNameWidth + delta, MinNameWidth, GetMaxNameWidth(vm)));
                break;

            case "SizePermissions":
                vm.SizeColumnWidth = new GridLength(ClampColumnWidth(_startSizeWidth + delta, MinSizeWidth, GetMaxSizeWidth(vm)));
                break;

            case "PermissionsModified":
                vm.PermissionsColumnWidth = new GridLength(ClampColumnWidth(_startPermissionsWidth + delta, MinPermissionsWidth, GetMaxPermissionsWidth(vm)));
                break;
        }

        e.Handled = true;
    }

    private void OnColumnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeSplitter is null)
            return;

        _activeSplitter = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void AutoFitColumnBySplitterTag(FileBrowserViewModel vm, string tag)
    {
        switch (tag)
        {
            case "NameSize":
                {
                    var targetName = EstimateAutoWidthForName(vm);
                    vm.NameColumnWidth = new GridLength(ClampColumnWidth(targetName, MinNameWidth, GetMaxNameWidth(vm)));
                    break;
                }
            case "SizePermissions":
                {
                    var targetSize = EstimateAutoWidthForSize(vm);
                    vm.SizeColumnWidth = new GridLength(ClampColumnWidth(targetSize, MinSizeWidth, GetMaxSizeWidth(vm)));
                    break;
                }
            case "PermissionsModified":
                {
                    var targetPerm = EstimateAutoWidthForPermissions(vm);
                    vm.PermissionsColumnWidth = new GridLength(ClampColumnWidth(targetPerm, MinPermissionsWidth, GetMaxPermissionsWidth(vm)));
                    break;
                }
        }
    }

    private static double EstimateAutoWidthForName(FileBrowserViewModel vm)
    {
        var header = MeasureTextWidth("文件名", 10);
        var rows = vm.Files.Any() ? vm.Files.Max(f => MeasureTextWidth(f.Name ?? string.Empty, 11)) : 0;

        // Name column also has a leading icon area (14px icon + 6px gap) and breathing room.
        var estimated = Math.Max(header, rows) + 24 + 10;
        return Math.Clamp(estimated, MinNameWidth, 760);
    }

    private static double EstimateAutoWidthForSize(FileBrowserViewModel vm)
    {
        var header = MeasureTextWidth("大小", 10);
        var rows = vm.Files.Any() ? vm.Files.Max(f => MeasureTextWidth(f.FormattedSize ?? string.Empty, 11)) : 0;
        var estimated = Math.Max(header, rows) + 8;
        return Math.Clamp(estimated, MinSizeWidth, 260);
    }

    private static double EstimateAutoWidthForPermissions(FileBrowserViewModel vm)
    {
        var header = MeasureTextWidth("权限", 10);
        var rows = vm.Files.Any() ? vm.Files.Max(f => MeasureTextWidth(f.Permissions ?? string.Empty, 11)) : 0;
        var estimated = Math.Max(header, rows) + 8;
        return Math.Clamp(estimated, MinPermissionsWidth, 300);
    }

    private double GetMaxNameWidth(FileBrowserViewModel vm) =>
        Math.Max(MinNameWidth, Bounds.Width - vm.SizeColumnWidth.Value - vm.PermissionsColumnWidth.Value - MinModifiedWidth - 28);

    private double GetMaxSizeWidth(FileBrowserViewModel vm) =>
        Math.Max(MinSizeWidth, Bounds.Width - vm.NameColumnWidth.Value - vm.PermissionsColumnWidth.Value - MinModifiedWidth - 28);

    private double GetMaxPermissionsWidth(FileBrowserViewModel vm) =>
        Math.Max(MinPermissionsWidth, Bounds.Width - vm.NameColumnWidth.Value - vm.SizeColumnWidth.Value - MinModifiedWidth - 28);

    private static double MeasureTextWidth(string text, double fontSize)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TerminalTypeface,
            fontSize,
            Brushes.White);

        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
    }

    private static double ClampColumnWidth(double value, double min, double max)
    {
        if (max < min)
            return min;

        return Math.Clamp(value, min, max);
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

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return null;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要上传的文件夹",
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnFileListDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var paths = ExtractLocalPaths(e);
        e.DragEffects = paths.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileListDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
            return;

        var paths = ExtractLocalPaths(e);
        if (paths.Count == 0)
            return;

        await vm.UploadLocalPathsAsync(paths);
        e.Handled = true;
    }

    private static IReadOnlyList<string> ExtractLocalPaths(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items is null || items.Length == 0)
            return Array.Empty<string>();

        return items
            .Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Modal yes/no confirmation for destructive actions (delete). Returns true to proceed.</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return false;

        var confirmButton = new Button { Content = "删除", MinWidth = 72 };
        var cancelButton = new Button { Content = "取消", IsCancel = true, IsDefault = true, MinWidth = 72 };

        bool result = false;
        var dialog = new Window
        {
            Title = "确认删除",
            CanResize = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, MaxWidth = 360, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, confirmButton },
                    },
                },
            },
        };

        confirmButton.Click += (_, _) => { result = true; dialog.Close(); };
        cancelButton.Click += (_, _) => { result = false; dialog.Close(); };

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
