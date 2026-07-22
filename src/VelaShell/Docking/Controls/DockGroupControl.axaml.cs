using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
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
    private int _contentMotionGeneration;
    private readonly Transitions _contentTransitions;
    private readonly Transitions _indicatorTransitions;
    private bool _indicatorPlaced;
    private (double X, double Y, double W, double H) _indicatorGeometry = (-1, -1, -1, -1);

    /// <summary>初始化控件,订阅标签滚动变化并注册全组激活的指针处理器。</summary>
    public DockGroupControl()
    {
        InitializeComponent();
        _contentTransitions = ContentHost.Transitions
            ?? throw new InvalidOperationException("ContentHost transitions are not configured.");
        _indicatorTransitions = ActiveTabIndicator.Transitions
            ?? throw new InvalidOperationException("ActiveTabIndicator transitions are not configured.");
        TabScroll.ScrollChanged += (_, _) => UpdateScrollButtons();
        // 标签宽度随标题变化、容器随集合重建 —— 指示器几何跟着布局走最省心;
        // UpdateActiveTabIndicator 对相同几何短路,不会造成布局风暴。
        TabsHost.LayoutUpdated += (_, _) => UpdateActiveTabIndicator();
        // 点击本组任意位置(含终端内容区)即把本组的选中文档设为全局激活 —— 对应原
        // Dock 的 FocusedDockable 语义;缺了它,分屏后点另一个窗格输入,SFTP 面板与
        // 状态栏不会跟随切换。Tunnel + handledEventsToo:终端控件会吞掉
        // 指针事件,冒泡阶段收不到。点标签时本处理器先按组当前选中激活一次,随后
        // DockTabItem 再精确激活被点的标签,结果一致。
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Workspace is { } workspace && Group is { ActiveDocument: { } active } && !ReferenceEquals(workspace.ActiveDocument, active))
        {
            workspace.ActivateDocument(active);
        }
    }

    /// <summary>当前 DataContext 承载的标签组;非 <see cref="DockGroup" /> 时为 null。</summary>
    public DockGroup? Group => DataContext as DockGroup;

    /// <summary>所属停靠工作区,由 <see cref="Initialize" /> 注入。</summary>
    public DockWorkspace? Workspace { get; private set; }

    /// <summary>所属工作区控件(拖拽控制器经由它取得)。</summary>
    public DockWorkspaceControl? WorkspaceControl { get; private set; }

    internal ScrollViewer TabScrollViewer => TabScroll;

    internal ItemsControl TabsItemsControl => TabsHost;

    internal Panel TabStripPanel => TabStripArea;

    /// <summary>注入工作区、工作区控件与视图解析器,订阅标签组变更并刷新内容区。</summary>
    public void Initialize(DockWorkspace workspace, DockWorkspaceControl workspaceControl, Func<DockDocument, Control?> viewResolver)
    {
        Workspace = workspace;
        WorkspaceControl = workspaceControl;
        _viewResolver = viewResolver;
        if (Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
            group.Documents.CollectionChanged += OnDocumentsChanged;
            ApplyTabsPosition(group.TabsPosition);
        }
        UpdateContent();
    }

    private void OnDocumentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateContent();

    /// <summary>从可视树分离时退订标签组事件,并释放对缓存视图的引用以避免双父级。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _contentMotionGeneration++;
        RestoreContentMotion();
        if (Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
            group.Documents.CollectionChanged -= OnDocumentsChanged;
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
                UpdateActiveTabIndicator();
                break;
            case nameof(DockGroup.TabsPosition):
                ApplyTabsPosition(Group?.TabsPosition ?? DockTabsPosition.Top);
                break;
        }
    }

    /// <summary>
    /// 把滑动强调线对齐到激活标签:水平模式为贴顶的 2px 横线,垂直模式为贴内侧边的
    /// 2px 竖线(标签在左 → 线贴条带右缘,反之贴左缘)。首次落位不做动画(指示器
    /// 不该从别的组的旧位置飘进来),此后位置/宽度经 180ms 过渡滑动 —— 这比每个标签
    /// 自己的顶线瞬时跳变自然得多。
    /// </summary>
    private void UpdateActiveTabIndicator()
    {
        DockDocument? active = Group?.ActiveDocument;
        Control? container = active is null ? null : TabsHost.ContainerFromItem(active);
        if (container is null || container.Bounds.Width <= 0 || container.Bounds.Height <= 0)
        {
            ActiveTabIndicator.IsVisible = false;
            _indicatorPlaced = false;
            _indicatorGeometry = (-1, -1, -1, -1);
            return;
        }
        Point origin = container.TranslatePoint(default, TabsOverlay) ?? default;
        bool vertical = (Group?.TabsPosition ?? DockTabsPosition.Top) != DockTabsPosition.Top;
        (double X, double Y, double W, double H) geometry = vertical
            ? (Group?.TabsPosition == DockTabsPosition.Left ? Math.Max(0, TabsOverlay.Bounds.Width - 2) : 0,
               Math.Round(origin.Y), 2, Math.Round(container.Bounds.Height))
            : (Math.Round(origin.X), 0, Math.Round(container.Bounds.Width), 2);
        if (geometry == _indicatorGeometry && ActiveTabIndicator.IsVisible)
        {
            return; // 布局回调高频触发;几何没变就绝不重写属性,避免过渡被反复重启。
        }
        _indicatorGeometry = geometry;

        bool animate = _indicatorPlaced;
        if (!animate)
        {
            ActiveTabIndicator.Transitions = null;
        }
        ActiveTabIndicator.Width = geometry.W;
        ActiveTabIndicator.Height = geometry.H;
        ActiveTabIndicator.RenderTransform = TransformOperations.Parse(
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"translate({geometry.X}px, {geometry.Y}px)"));
        ActiveTabIndicator.IsVisible = true;
        if (!animate)
        {
            _indicatorPlaced = true;
            Dispatcher.UIThread.Post(() => ActiveTabIndicator.Transitions ??= _indicatorTransitions,
                DispatcherPriority.Render);
        }
    }

    private void UpdateContent()
    {
        DockDocument? active = Group?.ActiveDocument;
        Control? view = active is null ? null : _viewResolver?.Invoke(active);
        int motionGeneration = ++_contentMotionGeneration;
        if (view is null || !ContentHost.IsEffectivelyVisible)
        {
            RestoreContentMotion();
            ContentHost.Target = view;
            EmptyHint.IsVisible = Group is { Documents.Count: 0 };
            return;
        }

        ContentHost.Transitions = null;
        ContentHost.Classes.Add("settling");
        ContentHost.Opacity = 0;
        ContentHost.RenderTransform = TransformOperations.Parse("translateY(2px)");
        ContentHost.Target = view;
        EmptyHint.IsVisible = Group is { Documents.Count: 0 };
        ContentHost.ClearValue(OpacityProperty);
        ContentHost.ClearValue(RenderTransformProperty);
        Dispatcher.UIThread.Post(() =>
        {
            if (motionGeneration == _contentMotionGeneration && ReferenceEquals(ContentHost.Target, view))
            {
                ContentHost.Transitions = _contentTransitions;
                ContentHost.Classes.Remove("settling");
            }
        }, DispatcherPriority.Render);
    }

    private void RestoreContentMotion()
    {
        ContentHost.Transitions = null;
        ContentHost.Classes.Remove("settling");
        ContentHost.ClearValue(OpacityProperty);
        ContentHost.ClearValue(RenderTransformProperty);
        ContentHost.Transitions = _contentTransitions;
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
            DockTabsPosition.Left => Dock.Left,
            DockTabsPosition.Right => Dock.Right,
            _ => Dock.Top
        });
        DockPanel.SetDock(StripSeparator, position switch
        {
            DockTabsPosition.Left => Dock.Left,
            DockTabsPosition.Right => Dock.Right,
            _ => Dock.Top
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
