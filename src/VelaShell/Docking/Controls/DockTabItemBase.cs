using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>Shared activation, selection, drag, close, split, and tab-position behavior.</summary>
public abstract class DockTabItemBase : UserControl
{
    private DockGroupControl? _owner;

    protected DockDocument? Document => DataContext as DockDocument;
    protected DockWorkspace? Workspace => _owner?.Workspace;
    protected DockGroup? Group => _owner?.Group;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.FindAncestorOfType<DockGroupControl>();
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
        }
        UpdateSelected();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
        }
        _owner = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Document is not { } document || Workspace is not { } workspace)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            workspace.ActivateDocument(document);
            _owner?.WorkspaceControl?.DragController.OnTabPressed(this, e);
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            workspace.ActivateDocument(document);
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DockGroup.ActiveDocument))
        {
            UpdateSelected();
        }
    }

    private void UpdateSelected() =>
        PseudoClasses.Set(":selected", Document is not null && ReferenceEquals(Group?.ActiveDocument, Document));

    protected void CloseTab_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseDocument(Document!);
    protected void CloseOthers_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseOtherDocuments(Document!);
    protected void CloseAll_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseAllDocuments(Document!);
    protected void CloseLeft_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseLeftDocuments(Document!);
    protected void CloseRight_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseRightDocuments(Document!);
    protected void SplitHorizontal_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Horizontal);
    protected void SplitVertical_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Vertical);
    protected void TabsTop_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Top);
    protected void TabsLeft_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Left);
    protected void TabsRight_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Right);

    private void SetTabsPosition(DockTabsPosition position)
    {
        if (Group is { } group)
        {
            group.TabsPosition = position;
        }
    }
}
