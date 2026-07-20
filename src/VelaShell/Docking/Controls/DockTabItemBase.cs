using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>Shared activation, selection, drag, close, split, and tab-position behavior.</summary>
public abstract class DockTabItemBase : UserControl
{
    private DockGroupControl? _owner;

    /// <summary>The document bound to this tab via data context.</summary>
    protected DockDocument? Document => DataContext as DockDocument;
    /// <summary>The workspace owning this tab's group.</summary>
    protected DockWorkspace? Workspace => _owner?.Workspace;
    /// <summary>The dock group containing this tab.</summary>
    protected DockGroup? Group => _owner?.Group;

    /// <summary>Locates the owning group control and subscribes to active document changes.</summary>
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

    /// <summary>Unsubscribes from group property changes and releases the owner reference.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
        }
        _owner = null;
    }

    /// <summary>Activates the document on left-click and initiates drag on press.</summary>
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

    /// <summary>Closes the current tab's document.</summary>
    protected void CloseTab_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseDocument(Document!);
    /// <summary>Closes all documents in the group except the current one.</summary>
    protected void CloseOthers_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseOtherDocuments(Document!);
    /// <summary>Closes all documents in the group.</summary>
    protected void CloseAll_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseAllDocuments(Document!);
    /// <summary>Closes all documents to the left of the current one.</summary>
    protected void CloseLeft_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseLeftDocuments(Document!);
    /// <summary>Closes all documents to the right of the current one.</summary>
    protected void CloseRight_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseRightDocuments(Document!);
    /// <summary>Splits the document horizontally into a new group.</summary>
    protected void SplitHorizontal_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Horizontal);
    /// <summary>Splits the document vertically into a new group.</summary>
    protected void SplitVertical_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Vertical);
    /// <summary>Moves tabs to the top of the group.</summary>
    protected void TabsTop_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Top);
    /// <summary>Moves tabs to the left of the group.</summary>
    protected void TabsLeft_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Left);
    /// <summary>Moves tabs to the right of the group.</summary>
    protected void TabsRight_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Right);

    private void SetTabsPosition(DockTabsPosition position)
    {
        if (Group is { } group)
        {
            group.TabsPosition = position;
        }
    }
}
