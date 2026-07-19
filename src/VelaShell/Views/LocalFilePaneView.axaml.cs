using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Reactive.Linq;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

public partial class LocalFilePaneView : UserControl
{
    public LocalFilePaneView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LocalFilePaneViewModel vm)
            {
                vm.ConfirmDelete = ConfirmAsync;
            }
        };
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
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed
            || sender is not Border { DataContext: LocalFileEntry entry }
            || entry.IsParentEntry
            || DataContext is not LocalFilePaneViewModel vm
            || FileList is not { } listBox)
        {
            return;
        }
        if (listBox.SelectedItems is not { } selection)
        {
            return;
        }
        if (!selection.Contains(entry))
        {
            selection.Clear();
            selection.Add(entry);
        }
    }
}
