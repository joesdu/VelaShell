using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 标签组控件:标签条 + 分割线 + 内容区。DataContext 为 <see cref="DockGroup" />,
/// 由 <see cref="DockWorkspaceControl" /> 创建并注入工作区与视图解析器
/// (视图按文档缓存,切换标签复用同一控件实例)。
/// </summary>
public partial class DockGroupControl : UserControl
{
    private Func<DockDocument, Control?>? _viewResolver;

    public DockGroupControl()
    {
        InitializeComponent();
        TabScroll.ScrollChanged += (_, _) => UpdateScrollButtons();
    }

    public DockGroup? Group => DataContext as DockGroup;

    public DockWorkspace? Workspace { get; private set; }

    /// <summary>所属工作区控件(拖拽控制器经由它取得)。</summary>
    public DockWorkspaceControl? WorkspaceControl { get; private set; }

    internal ScrollViewer TabScrollViewer => TabScroll;

    internal ItemsControl TabsItemsControl => TabsHost;

    internal Panel TabStripPanel => TabStripArea;

    public void Initialize(DockWorkspace workspace, DockWorkspaceControl workspaceControl, Func<DockDocument, Control?> viewResolver)
    {
        Workspace = workspace;
        WorkspaceControl = workspaceControl;
        _viewResolver = viewResolver;
        if (Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
            ApplyTabsPosition(group.TabsPosition);
        }
        UpdateContent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
        }
        // 结构重建时本控件被丢弃;释放对缓存视图的引用,避免下一个宿主收养时双父级。
        ContentHost.Target = null;
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DockGroup.ActiveDocument):
                UpdateContent();
                break;
            case nameof(DockGroup.TabsPosition):
                ApplyTabsPosition(Group?.TabsPosition ?? DockTabsPosition.Top);
                break;
        }
    }

    private void UpdateContent()
    {
        DockDocument? active = Group?.ActiveDocument;
        ContentHost.Target = active is null ? null : _viewResolver?.Invoke(active);
    }

    /// <summary>
    /// 标签位置(右键菜单“标签位置”):Top 为默认;Left/Right 时标签条纵排、
    /// 分割线立起、滚动方向改为垂直(溢出三连钮按宽度判定,纵排时自然隐藏)。
    /// </summary>
    private void ApplyTabsPosition(DockTabsPosition position)
    {
        bool vertical = position != DockTabsPosition.Top;
        DockPanel.SetDock(TabStripArea, position switch
        {
            DockTabsPosition.Left => Avalonia.Controls.Dock.Left,
            DockTabsPosition.Right => Avalonia.Controls.Dock.Right,
            _ => Avalonia.Controls.Dock.Top
        });
        DockPanel.SetDock(StripSeparator, position switch
        {
            DockTabsPosition.Left => Avalonia.Controls.Dock.Left,
            DockTabsPosition.Right => Avalonia.Controls.Dock.Right,
            _ => Avalonia.Controls.Dock.Top
        });
        StripSeparator.Height = vertical ? double.NaN : 1;
        StripSeparator.Width = vertical ? 1 : double.NaN;
        TabStripArea.MinHeight = vertical ? 0 : 35;
        TabStripArea.MinWidth = vertical ? 35 : 0;
        TabScroll.HorizontalScrollBarVisibility = vertical
                                                      ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                                                      : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        TabScroll.VerticalScrollBarVisibility = vertical
                                                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden
                                                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        TabsHost.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal
        });
    }

    // ---- 溢出控件(设计 nunbT pZGS4) ----

    private void UpdateScrollButtons()
    {
        ScrollLeftButton.IsEnabled = TabScroll.Offset.X > 0.5;
        ScrollRightButton.IsEnabled = TabScroll.Offset.X + TabScroll.Viewport.Width < TabScroll.Extent.Width - 0.5;
    }

    private void ScrollLeft_Click(object? sender, RoutedEventArgs e) =>
        TabScroll.Offset = TabScroll.Offset.WithX(Math.Max(0, TabScroll.Offset.X - 120));

    private void ScrollRight_Click(object? sender, RoutedEventArgs e) =>
        TabScroll.Offset = TabScroll.Offset.WithX(Math.Min(
            Math.Max(0, TabScroll.Extent.Width - TabScroll.Viewport.Width),
            TabScroll.Offset.X + 120));

    /// <summary>标签列表下拉(设计 nunbT tabListDrop):点击时按当前标签动态生成。</summary>
    private void TabListDrop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Group is not { Documents.Count: > 0 } group || Workspace is not { } workspace)
        {
            return;
        }
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (DockDocument document in group.Documents)
        {
            var item = new MenuItem { Header = document.Title };
            if (ReferenceEquals(group.ActiveDocument, document))
            {
                // 激活项以类名标识,着色交给样式表的 DynamicResource。
                item.Classes.Add("active-tab");
            }
            DockDocument captured = document;
            item.Click += (_, _) => workspace.ActivateDocument(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }
}
