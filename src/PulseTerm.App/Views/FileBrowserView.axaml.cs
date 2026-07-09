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
using Microsoft.Extensions.DependencyInjection;
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
            vm.ConfirmDelete = ConfirmAsync;
            vm.OpenLocalFile = OpenLocalFileAsync;
            vm.OpenInBuiltInEditor = OpenInBuiltInEditorAsync;
            vm.PromptConfigureEditor = PromptConfigureEditorAsync;
            vm.ConfirmOverwrite = ConfirmOverwriteAsync;
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

    /// <summary>Picks the local destination folder for folder/batch downloads, starting at the
    /// configured 本地下载目录 (设置 → 文件传输).</summary>
    private async Task<string?> PickDownloadFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return null;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.SelectDownloadFolder,
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveDefaultDownloadFolderAsync(top),
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>把设置中的下载目录("~" 展开)换成存储提供器的文件夹句柄;失败返回 null
    /// (选择器落在系统默认位置)。</summary>
    private async Task<IStorageFolder?> ResolveDefaultDownloadFolderAsync(TopLevel top)
    {
        if (DataContext is not FileBrowserViewModel vm)
            return null;

        var configured = vm.TransferOptions.LocalDownloadDirectory?.Trim();
        if (string.IsNullOrEmpty(configured))
            return null;

        if (configured.StartsWith("~"))
        {
            configured = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                configured.TrimStart('~', '/', '\\'));
        }

        try
        {
            return System.IO.Directory.Exists(configured)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(configured)
                : null;
        }
        catch
        {
            return null;
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
            SuggestedStartLocation = await ResolveDefaultDownloadFolderAsync(top),
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>下载遇到本地同名文件且冲突策略为“询问”:覆盖 or 跳过该文件。</summary>
    private async Task<bool> ConfirmOverwriteAsync(string localPath)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return true;

        return await MessageDialog.ConfirmAsync(owner, "文件已存在",
            $"本地已有同名文件:\n{localPath}\n\n覆盖它吗?(取消则跳过该文件)",
            kind: MessageDialogKind.Warning);
    }

    /// <summary>Modal single-line text prompt used by new folder / new file / rename / move.
    /// Returns the entered text, or null if the user cancelled.</summary>
    private async Task<string?> PromptForTextAsync(string title, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        return await MessageDialog.PromptAsync(owner, title, initialValue);
    }

    /// <summary>Modal yes/no confirmation for destructive actions (delete). Returns true to proceed.</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return false;

        return await MessageDialog.ConfirmAsync(owner, Strings.ConfirmDeleteTitle, message,
            confirmText: Strings.Delete, kind: MessageDialogKind.Warning, danger: true);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>未配置默认编辑器:弹窗说明配置位置,确认则直接打开设置窗口(文件传输页)。</summary>
    private async Task PromptConfigureEditorAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var openSettings = await MessageDialog.ConfirmAsync(owner, "未配置默认编辑器",
            "「使用默认编辑器打开」需要先指定编辑器程序:\n" +
            "Windows 填 exe 完整路径或命令名(如 notepad);Linux 填命令名(如 gedit);" +
            "macOS 填应用名(如 Visual Studio Code)。\n\n是否现在打开设置进行配置?",
            confirmText: "打开设置", kind: MessageDialogKind.Info);

        if (!openSettings)
            return;

        // 直达 设置 → 文件传输 页(索引 5,对应 SettingsView 的页序)。
        if (App.Current is App app && app.Services?.GetService<SettingsViewModel>() is { } settingsViewModel)
            settingsViewModel.SelectedSectionIndex = 5;

        if (owner.DataContext is MainWindowViewModel mainViewModel)
            mainViewModel.OpenSettingsCommand.Execute().Subscribe();
    }

    /// <summary>「打开」:非模态弹出内置 AvaloniaEdit 编辑器,保存时经回调上传回服务器。</summary>
    private Task OpenInBuiltInEditorAsync(RemoteFileInfoViewModel file, string localPath, Func<Task> uploadAsync)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return Task.CompletedTask;

        var editor = new RemoteFileEditorView(file.Name, file.FullPath, localPath, uploadAsync);
        editor.Show(owner);
        return Task.CompletedTask;
    }

    /// <summary>右键弹菜单前把所指行并入选区(资源管理器惯例):已在多选内则保持
    /// 多选不变,否则改为仅选中该行 —— 否则"下载/删除"(作用于选中集合)会对着
    /// 旧选区操作。</summary>
    private void FileRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
            return;

        if (sender is not Border { DataContext: RemoteFileInfoViewModel file }
            || this.FindControl<ListBox>("FileList") is not { } listBox)
            return;

        if (file.IsParentEntry)
            return;

        if (listBox.SelectedItems is { } selection && !selection.Contains(file))
        {
            selection.Clear();
            selection.Add(file);
        }
    }

    /// <summary>属性弹窗(参考 WinSCP):基本信息 + rwx 权限矩阵 + 八进制输入合并在一个界面。
    /// 文本着色一律走 MessageDialog 的 BodyHost 样式类(dim/mono/mono-accent)—— 代码里
    /// FindResource 取不到主题字典的画刷(会拿到 null 把文字画没,用户反馈的"文字看不见")。
    /// 确定且权限有变化时返回新 mode(三位八进制按十进制书写,如 755),否则返回 null。</summary>
    private async Task<short?> ShowFilePropertiesAsync(RemoteFileInfoViewModel file)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        var content = new StackPanel { Spacing = 14, MinWidth = 360 };

        // ── 头部:类型图标 + 名称 ─────────────────────────────────────────────
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var typeIcon = new PulseTerm.Controls.Controls.LucideIcon
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Data = this.FindResource(file.IsDirectory ? "Icon.folder" : "Icon.file") as Geometry,
        };
        typeIcon.Classes.Add(file.IsDirectory ? "folder" : "file");
        header.Children.Add(typeIcon);
        header.Children.Add(new TextBlock
        {
            Text = file.Name,
            FontSize = 13,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(header);

        // ── 基本信息 ────────────────────────────────────────────────────────
        var rows = new StackPanel { Spacing = 8 };
        void AddRow(string label, string value, bool mono = true)
        {
            if (string.IsNullOrEmpty(value))
                return;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*") };
            var labelText = new TextBlock { Text = label };
            labelText.Classes.Add("dim");
            grid.Children.Add(labelText);

            var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            if (mono)
                valueText.Classes.Add("mono");
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
            rows.Children.Add(grid);
        }

        AddRow(Strings.FileType, file.IsDirectory ? Strings.Folder : Strings.File, mono: false);
        AddRow(Strings.FilePath, file.FullPath);
        AddRow(Strings.Size, file.FormattedSize);
        AddRow(Strings.Modified, file.FormattedModifiedTime);
        AddRow(Strings.PermissionOwner, file.Owner);
        AddRow(Strings.PermissionGroup, file.Group);
        content.Children.Add(rows);

        // ── 权限矩阵("drwxr-xr-x" → 9 个 rwx 标志;异常串回退为全不勾) ────────
        var flags = new bool[9];
        var perms = file.Permissions;
        if (perms.Length == 10)
        {
            for (var i = 0; i < 9; i++)
                flags[i] = perms[i + 1] != '-';
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("96,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };

        void Place(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            if (column > 0)
                control.Margin = new Thickness(0, 4, 24, 4);
            else
                control.Margin = new Thickness(0, 4);
            grid.Children.Add(control);
        }

        string[] columnHeaders = { Strings.PermissionRead, Strings.PermissionWrite, Strings.PermissionExecute };
        string[] rowHeaders = { Strings.PermissionOwner, Strings.PermissionGroup, Strings.PermissionOthers };

        var permTitle = new TextBlock { Text = Strings.Permissions };
        permTitle.Classes.Add("dim");
        Place(permTitle, 0, 0);

        for (var c = 0; c < 3; c++)
        {
            var head = new TextBlock
            {
                Text = columnHeaders[c],
                FontWeight = Avalonia.Media.FontWeight.Medium,
            };
            Place(head, 0, c + 1);
        }

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

        // 八进制输入与勾选矩阵双向同步(参考图中的"八进制"输入行)。
        var octalBox = new TextBox { Width = 90, MaxLength = 3 };
        var syncing = false;

        void SyncOctalFromBoxes()
        {
            if (syncing)
                return;
            syncing = true;
            octalBox.Text = $"{CurrentMode():000}";
            syncing = false;
        }

        void SyncBoxesFromOctal()
        {
            if (syncing || octalBox.Text is not { Length: 3 } text)
                return;

            syncing = true;
            for (var g = 0; g < 3; g++)
            {
                if (text[g] is < '0' or > '7')
                    continue;
                var digit = text[g] - '0';
                boxes[g * 3].IsChecked = (digit & 4) != 0;
                boxes[g * 3 + 1].IsChecked = (digit & 2) != 0;
                boxes[g * 3 + 2].IsChecked = (digit & 1) != 0;
            }

            syncing = false;
        }

        for (var r = 0; r < 3; r++)
        {
            var rowLabel = new TextBlock
            {
                Text = rowHeaders[r],
                VerticalAlignment = VerticalAlignment.Center,
            };
            rowLabel.Classes.Add("dim");
            Place(rowLabel, r + 1, 0);
            for (var c = 0; c < 3; c++)
            {
                var box = new CheckBox { IsChecked = flags[r * 3 + c], MinWidth = 0, Padding = new Thickness(0) };
                box.IsCheckedChanged += (_, _) => SyncOctalFromBoxes();
                boxes[r * 3 + c] = box;
                Place(box, r + 1, c + 1);
            }
        }

        var initialMode = CurrentMode();
        SyncOctalFromBoxes();
        octalBox.TextChanged += (_, _) => SyncBoxesFromOctal();

        content.Children.Add(grid);

        var octalRow = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*") };
        var octalLabel = new TextBlock { Text = "八进制", VerticalAlignment = VerticalAlignment.Center };
        octalLabel.Classes.Add("dim");
        octalRow.Children.Add(octalLabel);
        var octalHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        octalHost.Children.Add(octalBox);
        var chmodEcho = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        chmodEcho.Classes.Add("mono-accent");
        void RefreshEcho() => chmodEcho.Text = $"chmod {CurrentMode():000}";
        RefreshEcho();
        foreach (var box in boxes)
            box.IsCheckedChanged += (_, _) => RefreshEcho();
        octalBox.TextChanged += (_, _) => RefreshEcho();
        octalHost.Children.Add(chmodEcho);
        Grid.SetColumn(octalHost, 1);
        octalRow.Children.Add(octalHost);
        content.Children.Add(octalRow);

        var confirmed = await MessageDialog.ShowCustomAsync(owner, Strings.Properties, content);
        if (!confirmed)
            return null;

        var newMode = CurrentMode();
        return newMode == initialMode ? null : newMode;
    }
}
