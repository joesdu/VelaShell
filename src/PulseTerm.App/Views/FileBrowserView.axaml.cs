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
using PulseTerm.Core.Resources;

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
            vm.PickFolderForDownload = PickDownloadFolderAsync;
            vm.PromptForText = PromptForTextAsync;
            vm.CopyToClipboard = CopyToClipboardAsync;
            vm.ShowFileProperties = ShowFilePropertiesAsync;
            vm.PromptForPermissions = PromptForPermissionsAsync;
            vm.ConfirmDelete = ConfirmAsync;
            vm.OpenLocalFile = OpenLocalFileAsync;
        };

        // Accept dropping local files/folders onto the list to upload into CurrentPath.
        if (this.FindControl<ListBox>("FileList") is { } fileList)
        {
            DragDrop.SetAllowDrop(fileList, true);
            fileList.AddHandler(DragDrop.DragOverEvent, OnFileListDragOver);
            fileList.AddHandler(DragDrop.DropEvent, OnFileListDrop);

            // Drag-out (§6): dragging rows off the list downloads them and hands the temp
            // files to the OS drag, so dropping into Explorer etc. copies them locally.
            fileList.AddHandler(PointerPressedEvent, OnFileListPointerPressed,
                RoutingStrategies.Bubble, handledEventsToo: true);
            fileList.AddHandler(PointerMovedEvent, OnFileListPointerMoved,
                RoutingStrategies.Bubble, handledEventsToo: true);
            fileList.AddHandler(PointerReleasedEvent, OnFileListPointerReleased,
                RoutingStrategies.Bubble, handledEventsToo: true);
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
        var rows = vm.Files.Any() ? vm.Files.Max(f => MeasureTextWidth(f.DisplayName ?? string.Empty, 11)) : 0;

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

    /// <summary>Double-clicking a row descends into a directory, or downloads a file to a temp
    /// folder and opens it with the OS default program (§6).</summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
            return;

        var row = (e.Source as Control)?.DataContext as RemoteFileInfoViewModel;
        if (row is null)
            return;

        vm.ActivateCommand.Execute(row).Subscribe(_ => { }, _ => { });
    }

    /// <summary>Opens a downloaded local file with the platform default handler.</summary>
    private async Task OpenLocalFileAsync(string localPath)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        await top.Launcher.LaunchFileInfoAsync(new System.IO.FileInfo(localPath));
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return Array.Empty<string>();

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SelectFilesToUpload,
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
            Title = Strings.SelectFolderToUpload,
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Picks the local destination folder for folder/batch downloads.</summary>
    private async Task<string?> PickDownloadFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return null;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.SelectDownloadFolder,
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    // --- Drag-out state: armed on row press, fired once the pointer travels past a threshold. ---
    private PointerPressedEventArgs? _dragOutPressArgs;
    private RemoteFileInfoViewModel? _dragOutPressRow;
    private Point _dragOutPressPoint;
    private bool _dragOutInProgress;

    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragOutPressArgs = null;
        _dragOutPressRow = null;

        if (_dragOutInProgress || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var row = (e.Source as Control)?.DataContext as RemoteFileInfoViewModel;
        if (row is null || row.IsParentEntry)
            return;

        _dragOutPressArgs = e;
        _dragOutPressRow = row;
        _dragOutPressPoint = e.GetPosition(this);
    }

    private void OnFileListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragOutPressArgs is null || _dragOutInProgress || DataContext is not FileBrowserViewModel vm)
            return;

        var delta = e.GetPosition(this) - _dragOutPressPoint;
        if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8)
            return;

        // Dragging a row that is part of the selection drags the whole selection.
        var pressed = _dragOutPressRow!;
        var targets = vm.SelectedFiles.Contains(pressed)
            ? vm.SelectedFiles.ToList()
            : new List<RemoteFileInfoViewModel> { pressed };

        var pressArgs = _dragOutPressArgs;
        _dragOutPressArgs = null;
        _dragOutPressRow = null;
        _dragOutInProgress = true;
        _ = StartDragOutAsync(vm, pressArgs!, targets);
    }

    private void OnFileListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragOutPressArgs = null;
        _dragOutPressRow = null;
    }

    /// <summary>Downloads the dragged rows to a temp folder, then starts the OS drag with the
    /// local paths. If the user releases the pointer before the download finishes, the drag
    /// simply never starts (the files stay in temp).</summary>
    private async Task StartDragOutAsync(FileBrowserViewModel vm, PointerPressedEventArgs pressArgs,
        IReadOnlyList<RemoteFileInfoViewModel> targets)
    {
        try
        {
            var localPaths = await vm.PrepareDragOutAsync(targets);
            if (localPaths.Count == 0)
                return;

            var top = TopLevel.GetTopLevel(this);
            if (top is null)
                return;

            using var dataTransfer = new DataTransfer();
            foreach (var path in localPaths)
            {
                IStorageItem? item = System.IO.Directory.Exists(path)
                    ? await top.StorageProvider.TryGetFolderFromPathAsync(path)
                    : await top.StorageProvider.TryGetFileFromPathAsync(path);

                if (item is not null)
                    dataTransfer.Add(DataTransferItem.CreateFile(item));
            }

            await DragDrop.DoDragDropAsync(pressArgs, dataTransfer, DragDropEffects.Copy);
        }
        finally
        {
            _dragOutInProgress = false;
        }
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
            Title = Strings.SaveToLocal,
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
        var okButton = new Button { Content = Strings.OK, IsDefault = true, MinWidth = 72 };
        var cancelButton = new Button { Content = Strings.Cancel, IsCancel = true, MinWidth = 72 };

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

        var confirmButton = new Button { Content = Strings.Delete, MinWidth = 72 };
        var cancelButton = new Button { Content = Strings.Cancel, IsCancel = true, IsDefault = true, MinWidth = 72 };

        bool result = false;
        var dialog = new Window
        {
            Title = Strings.ConfirmDeleteTitle,
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

        AddRow(Strings.Name, file.Name);
        AddRow(Strings.FilePath, file.FullPath);
        AddRow(Strings.FileType, file.IsDirectory ? Strings.Folder : Strings.File);
        AddRow(Strings.Size, file.FormattedSize);
        AddRow(Strings.Permissions, file.Permissions);
        AddRow(Strings.Modified, file.FormattedModifiedTime);

        var okButton = new Button
        {
            Content = Strings.OK,
            IsDefault = true,
            IsCancel = true,
            MinWidth = 72,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var dialog = new Window
        {
            Title = Strings.Properties,
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

    /// <summary>chmod editor (§6 context menu): a 3×3 rwx checkbox grid with a live octal readout.
    /// Returns the chosen mode as three octal digits in decimal (e.g. 755), or null if cancelled.</summary>
    private async Task<short?> PromptForPermissionsAsync(RemoteFileInfoViewModel file)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        // "drwxr-xr-x" → 9 rwx flags; anything malformed falls back to all-off.
        var flags = new bool[9];
        var perms = file.Permissions;
        if (perms.Length == 10)
        {
            for (var i = 0; i < 9; i++)
                flags[i] = perms[i + 1] != '-';
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };

        void Place(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            control.Margin = new Thickness(8, 4);
            grid.Children.Add(control);
        }

        string[] columnHeaders = { Strings.PermissionRead, Strings.PermissionWrite, Strings.PermissionExecute };
        string[] rowHeaders = { Strings.PermissionOwner, Strings.PermissionGroup, Strings.PermissionOthers };

        for (var c = 0; c < 3; c++)
            Place(new TextBlock { Text = columnHeaders[c], FontWeight = Avalonia.Media.FontWeight.Medium }, 0, c + 1);

        var octalText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var boxes = new CheckBox[9];

        short CurrentMode()
        {
            short mode = 0;
            for (var g = 0; g < 3; g++)
            {
                var digit = (boxes[g * 3].IsChecked == true ? 4 : 0)
                          + (boxes[g * 3 + 1].IsChecked == true ? 2 : 0)
                          + (boxes[g * 3 + 2].IsChecked == true ? 1 : 0);
                mode = (short)(mode * 10 + digit);
            }

            return mode;
        }

        void RefreshOctal() => octalText.Text = $"chmod {CurrentMode():000}  {file.Name}";

        for (var r = 0; r < 3; r++)
        {
            Place(new TextBlock { Text = rowHeaders[r], VerticalAlignment = VerticalAlignment.Center }, r + 1, 0);
            for (var c = 0; c < 3; c++)
            {
                var box = new CheckBox { IsChecked = flags[r * 3 + c] };
                box.IsCheckedChanged += (_, _) => RefreshOctal();
                boxes[r * 3 + c] = box;
                Place(box, r + 1, c + 1);
            }
        }

        RefreshOctal();

        var okButton = new Button { Content = Strings.OK, IsDefault = true, MinWidth = 72 };
        var cancelButton = new Button { Content = Strings.Cancel, IsCancel = true, MinWidth = 72 };

        short? result = null;
        var dialog = new Window
        {
            Title = Strings.ChangePermissions,
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
                    grid,
                    octalText,
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

        okButton.Click += (_, _) => { result = CurrentMode(); dialog.Close(); };
        cancelButton.Click += (_, _) => { result = null; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return result;
    }
}
