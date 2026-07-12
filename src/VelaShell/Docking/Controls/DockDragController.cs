using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 标签拖拽状态机(docs/dock-replacement-plan.md §2.5):按下记录 → 位移超阈值进入拖拽 →
/// 悬停标签条 = 插入线(松手重排/跨组并入),悬停内容区 = 五区高亮(中心并入、四边拆分)→
/// 松手执行 / Esc 取消。所有落点都在主窗口内 —— 浮动窗口按产品决策不存在。
/// 采用“松手才执行”的预览式交互(与原 Dock 一致),拖拽过程不动模型,天然可取消。
/// </summary>
internal sealed class DockDragController(DockWorkspaceControl owner)
{
    private const double DragThreshold = 4;
    private const double EdgeRatio = 0.25;

    private DockTabItem? _tab;
    private DockDocument? _document;
    private Point _origin;
    private bool _dragging;
    private (DockGroup Group, DockPosition Position, int Index)? _pending;
    private IPointer? _pointer;
    private TopLevel? _topLevel;

    public void OnTabPressed(DockTabItem tab, PointerPressedEventArgs e)
    {
        if (tab.DataContext is not DockDocument document || owner.Workspace is null)
        {
            return;
        }
        _tab = tab;
        _document = document;
        _origin = e.GetPosition(owner);
        _dragging = false;
        _pending = null;
        _pointer = e.Pointer;
        tab.PointerMoved += OnPointerMoved;
        tab.PointerReleased += OnPointerReleased;
        tab.PointerCaptureLost += OnPointerCaptureLost;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tab is null || _document is null)
        {
            return;
        }
        Point position = e.GetPosition(owner);
        if (!_dragging)
        {
            Point delta = position - _origin;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            {
                return;
            }
            _dragging = true;
            _pointer = e.Pointer;
            _tab.Classes.Add("dragging");
            HookEscape();
        }
        UpdateDrag(position);
    }

    private void UpdateDrag(Point position)
    {
        if (owner.Workspace is not { } workspace || _document is null)
        {
            return;
        }

        // 1) 标签条命中:插入线(同组 = 重排,跨组 = 并入到该位置)。
        foreach (DockGroupControl groupControl in owner.GroupControls)
        {
            if (groupControl.Group is not { } group)
            {
                continue;
            }
            if (BoundsOf(groupControl.TabStripPanel) is { } stripBounds && stripBounds.Contains(position))
            {
                int index = ComputeInsertIndex(groupControl, group, position);
                _pending = (group, DockPosition.Center, index);
                owner.Overlay.ShowInsertion(InsertionLine(groupControl, group, index, stripBounds));
                return;
            }
        }

        // 2) 内容区命中:五区高亮。
        foreach (DockGroupControl groupControl in owner.GroupControls)
        {
            if (groupControl.Group is not { } group || BoundsOf(groupControl) is not { } bounds || !bounds.Contains(position))
            {
                continue;
            }
            DockPosition dockPosition = RegionAt(bounds, position);
            _pending = (group, dockPosition, -1);
            owner.Overlay.ShowRegion(RegionRect(bounds, dockPosition));
            return;
        }

        // 3) 无有效落点。
        _pending = null;
        owner.Overlay.Hide();
    }

    /// <summary>按四边归一化距离取最近者,都不够近(≥25%)则为中心并入。</summary>
    private static DockPosition RegionAt(Rect bounds, Point position)
    {
        double x = (position.X - bounds.X) / bounds.Width;
        double y = (position.Y - bounds.Y) / bounds.Height;
        (DockPosition Position, double Distance)[] edges =
        [
            (DockPosition.Left, x),
            (DockPosition.Right, 1 - x),
            (DockPosition.Top, y),
            (DockPosition.Bottom, 1 - y)
        ];
        (DockPosition nearest, double distance) = edges.MinBy(edge => edge.Distance);
        return distance < EdgeRatio ? nearest : DockPosition.Center;
    }

    private static Rect RegionRect(Rect bounds, DockPosition position) => position switch
    {
        DockPosition.Left => bounds.WithWidth(bounds.Width / 2),
        DockPosition.Right => new Rect(bounds.X + bounds.Width / 2, bounds.Y, bounds.Width / 2, bounds.Height),
        DockPosition.Top => bounds.WithHeight(bounds.Height / 2),
        DockPosition.Bottom => new Rect(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height / 2),
        _ => bounds.Deflate(4)
    };

    private int ComputeInsertIndex(DockGroupControl groupControl, DockGroup group, Point position)
    {
        bool verticalStrip = group.TabsPosition != DockTabsPosition.Top;
        for (int i = 0; i < group.Documents.Count; i++)
        {
            if (groupControl.TabsItemsControl.ContainerFromIndex(i) is not { } container || BoundsOf(container) is not { } rect)
            {
                continue;
            }
            double middle = verticalStrip ? rect.Center.Y : rect.Center.X;
            double pointer = verticalStrip ? position.Y : position.X;
            if (pointer < middle)
            {
                return i;
            }
        }
        return group.Documents.Count;
    }

    /// <summary>插入位置线:落在第 index 个标签的前缘(越界则贴最后一个标签的后缘)。</summary>
    private Rect InsertionLine(DockGroupControl groupControl, DockGroup group, int index, Rect stripBounds)
    {
        bool verticalStrip = group.TabsPosition != DockTabsPosition.Top;
        double edge;
        if (group.Documents.Count == 0)
        {
            edge = verticalStrip ? stripBounds.Y : stripBounds.X;
        }
        else
        {
            int anchor = Math.Min(index, group.Documents.Count - 1);
            Rect rect = groupControl.TabsItemsControl.ContainerFromIndex(anchor) is { } container
                            ? BoundsOf(container) ?? stripBounds
                            : stripBounds;
            edge = verticalStrip
                       ? index >= group.Documents.Count ? rect.Bottom : rect.Y
                       : index >= group.Documents.Count ? rect.Right : rect.X;
        }
        return verticalStrip
                   ? new Rect(stripBounds.X, edge - 1, stripBounds.Width, 2)
                   : new Rect(edge - 1, stripBounds.Y, 2, stripBounds.Height);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _pending is { } pending && _document is { } document && owner.Workspace is { } workspace)
        {
            int index = pending.Index;
            if (pending.Position == DockPosition.Center && index >= 0
                && workspace.FindGroup(document) is { } current && ReferenceEquals(current, pending.Group))
            {
                // 同组:插入位换算为重排目标位(元素先被摘出,后方位置整体前移一格)。
                int from = current.Documents.IndexOf(document);
                if (index > from)
                {
                    index--;
                }
            }
            workspace.DockTo(document, pending.Group, pending.Position, index);
        }
        Cleanup();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Cleanup();

    private void HookEscape()
    {
        _topLevel = TopLevel.GetTopLevel(owner);
        _topLevel?.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        e.Handled = true;
        _pointer?.Capture(null); // 触发 PointerCaptureLost → Cleanup
        Cleanup();
    }

    private void Cleanup()
    {
        if (_tab is { } tab)
        {
            tab.PointerMoved -= OnPointerMoved;
            tab.PointerReleased -= OnPointerReleased;
            tab.PointerCaptureLost -= OnPointerCaptureLost;
            tab.Classes.Remove("dragging");
        }
        _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _topLevel = null;
        _tab = null;
        _document = null;
        _pending = null;
        _pointer = null;
        _dragging = false;
        owner.Overlay.Hide();
    }

    /// <summary>把任一控件的边界换算到工作区坐标系;未挂树时返回 null。</summary>
    private Rect? BoundsOf(Control control)
    {
        if (control.TranslatePoint(default, owner) is not { } origin)
        {
            return null;
        }
        return new Rect(origin, control.Bounds.Size);
    }
}
