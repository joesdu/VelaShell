using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 标签条上的单个标签:点击激活、右键菜单(关闭系列/拆分/标签位置)、
/// 拖拽起点(重排/跨组/分屏,由 <see cref="DockDragController" /> 接管)。
/// 选中态通过 :selected 伪类呈现,随所属组的 ActiveDocument 联动。
/// </summary>
public partial class DockTabItem : UserControl
{
    private DockGroupControl? _owner;

    /// <summary>初始化标签项并加载其 XAML 内容。</summary>
    public DockTabItem()
    {
        InitializeComponent();
    }

    private DockDocument? Document => DataContext as DockDocument;

    private DockWorkspace? Workspace => _owner?.Workspace;

    internal DockGroup? Group => _owner?.Group;

    /// <summary>挂载到可视树时定位所属分组并订阅其活动文档变化。</summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.FindAncestorOfType<DockGroupControl>();
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
        }
        UpdateSelected();
    }

    /// <summary>从可视树分离时退订分组事件并清理引用。</summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
        }
        _owner = null;
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

    /// <summary>处理指针按下:激活文档并启动标签拖拽。</summary>
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
            // 右键先激活再弹菜单(与原 Dock 行为一致:菜单作用于被点的标签)。
            workspace.ActivateDocument(document);
        }
    }

    // ---- 右键菜单(与原 DockContextMenu 同构,命令直连 DockWorkspace) ----

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Workspace?.CloseDocument(doc);
        }
    }

    private void CloseOthers_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Workspace?.CloseOtherDocuments(doc);
        }
    }

    private void CloseAll_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Workspace?.CloseAllDocuments(doc);
        }
    }

    private void CloseLeft_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Workspace?.CloseLeftDocuments(doc);
        }
    }

    private void CloseRight_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Workspace?.CloseRightDocuments(doc);
        }
    }

    private void SplitHorizontal_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Workspace?.SplitDocument(doc, DockOrientation.Horizontal);
        }
    }

    private void SplitVertical_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } doc)
        {
            Workspace?.SplitDocument(doc, DockOrientation.Vertical);
        }
    }

    private void TabsTop_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Top);

    private void TabsLeft_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Left);

    private void TabsRight_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Right);

    private void SetTabsPosition(DockTabsPosition position)
    {
        if (Group is { } group)
        {
            group.TabsPosition = position;
        }
    }
}
