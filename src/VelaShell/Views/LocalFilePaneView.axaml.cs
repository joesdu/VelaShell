using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Reactive.Linq;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>Local file pane view with breadcrumbs, context menu, and cross-pane drag-drop.</summary>
public partial class LocalFilePaneView : UserControl
{
    private const string RemoteDragMarker = "VFTP|";
    private const string LocalDragMarker = "VFTPL|";

    // Drag state
    private bool _isDragging;
    private Point _dragOrigin;
    private LocalFileEntry? _dragRow;
    private PointerPressedEventArgs? _dragPointerArgs;
    private const double DragThreshold = 5;

    /// <summary>Initializes the local file pane view and wires VM delegates.</summary>
    public LocalFilePaneView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LocalFilePaneViewModel vm)
            {
                vm.ConfirmDelete = ConfirmAsync;
                vm.PromptForText = PromptForTextAsync;
                vm.OpenLocalFile = OpenLocalFileAsync;
            }
        };

        // Drag-drop: receive remote file drops + initiate local file drags.
        if (this.FindControl<ListBox>("FileList") is { } fileList)
        {
            DragDrop.SetAllowDrop(fileList, true);
            fileList.AddHandler(DragDrop.DragOverEvent, OnLocalDropDragOver);
            fileList.AddHandler(DragDrop.DropEvent, OnLocalDrop);
            fileList.AddHandler(PointerMovedEvent, OnLocalDragPointerMoved);
            fileList.AddHandler(PointerReleasedEvent, OnLocalDragPointerReleased);
        }
    }

    private void OnRootSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LocalFilePaneViewModel viewModel
            && sender is ComboBox comboBox
            && comboBox.SelectedItem is LocalRootEntry root
            && !ReferenceEquals(root, viewModel.SelectedRoot))
        {
            if (!root.IsAccessible)
            {
                comboBox.SelectedItem = viewModel.SelectedRoot;
                return;
            }
            viewModel.SwitchRootCommand.Execute(root).Subscribe();
        }
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }
        return await MessageDialog.ConfirmAsync(
            owner,
            Strings.ConfirmDeleteTitle,
            message,
            Strings.Delete,
            kind: MessageDialogKind.Warning,
            danger: true);
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LocalFilePaneViewModel vm && sender is ListBox list && list.SelectedItem is LocalFileEntry entry)
        {
            vm.ActivateCommand.Execute(entry).Subscribe();
        }
    }

    private void OnFileRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            // Left button on a non-parent row: prepare for drag to remote pane.
            if (sender is Border { DataContext: LocalFileEntry entry } && !entry.IsParentEntry)
            {
                _dragOrigin = e.GetPosition(this.FindControl<ListBox>("FileList"));
                _dragRow = entry;
                _dragPointerArgs = e;
                _isDragging = false;
            }
            return;
        }
        // Right button: select row for context menu.
        if (sender is not Border { DataContext: LocalFileEntry entry2 }
            || entry2.IsParentEntry
            || DataContext is not LocalFilePaneViewModel vm
            || FileList is not { } listBox)
        {
            return;
        }
        if (listBox.SelectedItems is not { } selection)
        {
            return;
        }
        if (!selection.Contains(entry2))
        {
            selection.Clear();
            selection.Add(entry2);
        }
    }

    private async Task<string?> PromptForTextAsync(string title, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }
        return await MessageDialog.PromptAsync(owner, title, initialValue);
    }

    private async Task OpenLocalFileAsync(string localPath)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        await top.Launcher.LaunchFileInfoAsync(new(localPath));
    }

    // ── Drag initiation (local → remote) ────────────────────────────────

    private void OnLocalDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragRow is null || _dragPointerArgs is null)
        {
            return;
        }
        Point current = e.GetPosition(this.FindControl<ListBox>("FileList"));
        if (Math.Abs(current.X - _dragOrigin.X) < DragThreshold && Math.Abs(current.Y - _dragOrigin.Y) < DragThreshold)
        {
            return;
        }

        _isDragging = true;
        _ = StartLocalDragAsync(_dragRow, _dragPointerArgs);
    }

    private void OnLocalDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        _dragRow = null;
        _dragPointerArgs = null;
    }

    private async Task StartLocalDragAsync(LocalFileEntry source, PointerPressedEventArgs pointerArgs)
    {
        var paths = new List<string>();
        if (DataContext is LocalFilePaneViewModel vm)
        {
            foreach (LocalFileEntry item in vm.SelectedEntries)
            {
                if (!item.IsParentEntry) paths.Add(item.FullPath);
            }
        }
        if (paths.Count == 0) paths.Add(source.FullPath);

        var data = new DataTransfer();
        var dragItem = new DataTransferItem();
        dragItem.SetText(LocalDragMarker + string.Join("\n", paths));
        data.Add(dragItem);

        await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Copy);

        _isDragging = false;
        _dragRow = null;
        _dragPointerArgs = null;
    }

    // ── Drop handling (remote → local) ──────────────────────────────────

    private void OnLocalDropDragOver(object? sender, DragEventArgs e)
    {
        bool isRemoteDrag = !string.IsNullOrEmpty(e.DataTransfer.TryGetText())
            && e.DataTransfer.TryGetText()!.StartsWith(RemoteDragMarker);
        bool hasFiles = e.DataTransfer.TryGetFiles()?.Length > 0;
        e.DragEffects = (isRemoteDrag || hasFiles) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnLocalDrop(object? sender, DragEventArgs e)
    {
        // Check for cross-pane remote file drag first (VFTP marker).
        string? text = e.DataTransfer.TryGetText();
        if (!string.IsNullOrEmpty(text) && text.StartsWith(RemoteDragMarker))
        {
            string[] remotePaths = text[RemoteDragMarker.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (remotePaths.Length == 0) return;

        SftpDocumentViewModel? sftpVm = (this.Parent as Control)?.DataContext as SftpDocumentViewModel
            ?? (this.Parent?.Parent as Control)?.DataContext as SftpDocumentViewModel;
        if (sftpVm is null) return;

        // Select matching remote entries for download.
        sftpVm.RemoteFiles.SelectedFiles.Clear();
        foreach (string path in remotePaths)
        {
            RemoteFileInfoViewModel? entry = sftpVm.RemoteFiles.Files
                .FirstOrDefault(f => f.FullPath == path);
            if (entry is not null && !entry.IsParentEntry)
                sftpVm.RemoteFiles.SelectedFiles.Add(entry);
        }

        if (sftpVm.RemoteFiles.SelectedFiles.Count > 0)
        {
            try
            {
                await sftpVm.DownloadSelectedAsync();
                await sftpVm.LocalFiles.RefreshAsync();
            }
            catch (FileNotFoundException)
            {
                // File may have been deleted between listing and download; refresh remote listing.
                sftpVm.RemoteFiles.RefreshCommand.Execute().Subscribe();
            }
        }
        e.Handled = true;
        return;
        }

        // Also support OS file drops onto local pane (just refresh).
        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        if (files is { Length: > 0 })
        {
            if (DataContext is LocalFilePaneViewModel vm)
            {
                await vm.RefreshAsync();
            }
            e.Handled = true;
        }
    }
}
