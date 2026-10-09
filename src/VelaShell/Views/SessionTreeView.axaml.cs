using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Presentation.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>会话树视图:以分组树形式展示会话,支持展开折叠、双击连接、右键菜单与拖动分组。</summary>
public partial class SessionTreeView : UserControl
{
    /// <summary>
    /// 会话拖放载荷前缀(与 SFTP 那套 VFTP/VFTPL 同形)。只在本树内自产自销,
    /// 前缀的作用是别把外部拖进来的文本/文件当成会话节点。
    /// </summary>
    private const string SessionDragPrefix = "VSESS|";

    /// <summary>分组拖放载荷前缀(#571 拖动分组行重排),与会话的分开:两者落点规则完全不同。</summary>
    private const string GroupDragPrefix = "VGRP|";

    /// <summary>按下到移动超过该像素才算拖拽,避免误触打断双击连接(与文件面板一致)。</summary>
    private const double DragThreshold = 5;

    private SessionTreeViewModel? _viewModel;

    // 拖拽手势状态:按下时记住行与按下事件(DoDragDropAsync 需要原始的 PointerPressedEventArgs)。
    private SessionTreeNodeViewModel? _dragNode;
    private PointerPressedEventArgs? _dragPointerArgs;
    private Point _dragOrigin;
    private bool _isDragging;

    /// <summary>被拖会话的显示名,拖起时快照下来给幽灵标签用(拖放过程中不再依赖手势状态)。</summary>
    private string _dragLabel = string.Empty;

    /// <summary>光标最后一次报出的位置(叠层坐标系),用来在幽灵标签量出真实尺寸后重新夹一次。</summary>
    private Point _dragGhostAnchor;

    /// <summary>初始化会话树视图并加载 XAML 组件。</summary>
    public SessionTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // 拖动分组:接收落点在整棵树上(空白处 = 移出分组),发起在会话行上
        // (Session_PointerPressed 记录起点,移动超过阈值才真正开始拖)。
        DragDrop.SetAllowDrop(SessionTreeRoot, true);
        SessionTreeRoot.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        SessionTreeRoot.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        SessionTreeRoot.AddHandler(DragDrop.DragLeaveEvent, OnTreeDragLeave);
        SessionTreeRoot.AddHandler(PointerMovedEvent, OnTreePointerMoved);
        SessionTreeRoot.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Bubble, true);

        // 排布定下真实尺寸之后再夹一次(见 ClampGhostIntoOverlay)。
        DragGhost.SizeChanged += (_, _) => ClampGhostIntoOverlay();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as SessionTreeViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTreeViewModel.SelectedNode))
        {
            Dispatcher.UIThread.Post(BringSelectedSessionIntoView, DispatcherPriority.Loaded);
        }
    }

    private void BringSelectedSessionIntoView()
    {
        if (_viewModel?.SelectedNode is not { } selected)
        {
            return;
        }
        // 交给列表自己滚:行是虚拟化出来的,选中项在视口外时压根没有对应的控件可以
        // BringIntoView —— 而"选中了却没滚过去"恰恰只在列表长的时候发生。
        SessionTreeRoot.ScrollIntoView(selected);
    }

    /// <summary>单击分组行即切换展开/折叠(设计 FrJPu:chevron 随之翻转)。</summary>
    private void Group_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: true } node })
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    /// <summary>
    /// 右键分组行时先选中它:分组菜单里的命令同样作用于 SelectedNode。
    /// 左键记下拖动分组(#571)的起点 —— 是否真拖由移动阈值决定,没拖动就照旧由 Group_Tapped 展开 / 折叠。
    /// </summary>
    private void Group_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (
            sender is not Control { DataContext: SessionTreeNodeViewModel { IsGroup: true } node }
            || DataContext is not SessionTreeViewModel viewModel
        )
        {
            return;
        }
        PointerPointProperties properties = e.GetCurrentPoint(null).Properties;
        if (properties.IsRightButtonPressed)
        {
            viewModel.SelectedNode = node;
            return;
        }
        if (properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None)
        {
            _dragNode = node;
            _dragPointerArgs = e;
            _dragOrigin = e.GetPosition(this);
            _isDragging = false;
        }
    }

    /// <summary>双击会话行直接连接(分组行仅展开/折叠)。</summary>
    private void Session_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (
            sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node }
            && DataContext is SessionTreeViewModel viewModel
        )
        {
            viewModel.SelectedNode = node;
            viewModel.RequestConnect(node.Id);
        }
    }

    /// <summary>
    /// 右键弹菜单前先选中所指行:菜单里的命令都作用于 SelectedNode,不选中会
    /// 对着上一次选择的会话执行。左键则记下拖拽起点(是否真拖由移动阈值决定)。
    /// </summary>
    private void Session_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (
            sender is not Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node }
            || DataContext is not SessionTreeViewModel viewModel
        )
        {
            return;
        }
        PointerPointProperties properties = e.GetCurrentPoint(null).Properties;
        if (properties.IsRightButtonPressed)
        {
            // 右键多选里的一行:保持多选,弹多选专用菜单;右键多选之外的行:选中它(多选随之结束),弹原菜单。
            viewModel.SelectedNode = node;
            if (sender is Control row)
            {
                UseMenuFor(row, node, viewModel);
            }
            return;
        }
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            // Shift + 单击:从上一次单击的那一行选到这一行;再按着 Ctrl 就并进已有的多选。
            // 与 Ctrl 一样自己处理掉、不记拖拽起点。
            viewModel.ExtendSelectionTo(node, additive: IsToggleModifier(e.KeyModifiers));
            e.Handled = true;
            return;
        }
        if (IsToggleModifier(e.KeyModifiers))
        {
            // Ctrl + 单击:加入 / 移出多选。自己处理掉,不让列表再按它的单选规则改一遍选中项,
            // 也不记拖拽起点 —— Ctrl 点下去是在选东西,不是要拖。
            viewModel.ToggleMultiSelection(node);
            e.Handled = true;
            return;
        }
        // 普通单击 = 重新开始选:多选就此结束、这一行成为 Shift 的起点(选中项交给列表照常处理)。
        viewModel.StartSelectionAt(node);
        _dragNode = node;
        _dragPointerArgs = e;
        _dragOrigin = e.GetPosition(this);
        _isDragging = false;
    }

    /// <summary>
    /// 多选的修饰键:Windows / Linux 是 Ctrl,macOS 上习惯的是 ⌘(Meta)—— 两个都认。
    /// </summary>
    private static bool IsToggleModifier(KeyModifiers modifiers) =>
        (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    /// <summary>各会话行自己的那份右键菜单(第一次换成多选菜单时记下,换回来时用)。</summary>
    private readonly ConditionalWeakTable<Control, ContextMenu> _rowMenus = [];

    /// <summary>
    /// 右键按下时,给这一行挂上该弹的菜单:它在多选里 → 多选专用菜单;否则 → 行上原本那份。
    /// </summary>
    /// <remarks>
    /// 挂在按下而不是 ContextRequested 上:菜单是在松开右键时才弹的,按下时换好,弹出来的就是对的那份;
    /// 而同一个控件上两个 ContextRequested 处理器谁先谁后,取决于注册顺序,靠不住。
    /// 行上的原菜单一行没改,多选菜单是另一个实例 —— 两套菜单互不影响。
    /// </remarks>
    private void UseMenuFor(Control row, SessionTreeNodeViewModel node, SessionTreeViewModel viewModel)
    {
        if (!_rowMenus.TryGetValue(row, out ContextMenu? own))
        {
            if (row.ContextMenu is not { } original)
            {
                return;
            }
            own = original;
            _rowMenus.Add(row, own);
        }
        if (node.IsMultiMarked && viewModel.HasMultiSelection && Resources["MultiSelectionMenu"] is ContextMenu multi)
        {
            multi.DataContext = viewModel;
            row.ContextMenu = multi;
        }
        else
        {
            row.ContextMenu = own;
        }
    }

    // ── 拖动分组 ────────────────────────────────────────────────

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragNode is null || _dragPointerArgs is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetDragGesture();
            return;
        }
        Point current = e.GetPosition(this);
        if (
            Math.Abs(current.X - _dragOrigin.X) < DragThreshold
            && Math.Abs(current.Y - _dragOrigin.Y) < DragThreshold
        )
        {
            return;
        }
        _isDragging = true;
        _ = _dragNode.IsGroup
            ? StartGroupDragAsync(_dragNode, _dragPointerArgs)
            : StartSessionDragAsync(_dragNode, _dragPointerArgs);
    }

    /// <summary>拖动分组行重排(#571):载荷是分组 Id,落点由 <see cref="OnGroupDragOver" /> 解析。</summary>
    private async Task StartGroupDragAsync(SessionTreeNodeViewModel group, PointerPressedEventArgs pointerArgs)
    {
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(GroupDragPrefix + group.Id);
        data.Add(item);
        _dragLabel = group.Name;
        try
        {
            await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDragFeedback();
            ResetDragGesture();
        }
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ResetDragGesture();

    private async Task StartSessionDragAsync(
        SessionTreeNodeViewModel node,
        PointerPressedEventArgs pointerArgs
    )
    {
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(SessionDragPrefix + node.Id);
        data.Add(item);
        _dragLabel = node.Name;
        try
        {
            await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Move);
        }
        finally
        {
            // 拖拽被取消(Esc/落在窗口外)时同样要收拾干净,否则高亮与幽灵标签会留在屏幕上。
            ClearDragFeedback();
            ResetDragGesture();
        }
    }

    private void ResetDragGesture()
    {
        _isDragging = false;
        _dragNode = null;
        _dragPointerArgs = null;
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (TryGetDraggedId(e, GroupDragPrefix) is { } groupId && DataContext is SessionTreeViewModel groupTree)
        {
            OnGroupDragOver(e, groupId, groupTree);
            return;
        }
        if (
            TryGetDraggedSessionId(e) is not { } sessionId
            || DataContext is not SessionTreeViewModel viewModel
        )
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        Guid target = viewModel.ResolveDropTargetGroupId(FindNodeAt(e.Source));
        // 落回原分组等于什么都没发生:直接给"不可放置"光标,免得用户以为拖成功了。
        bool sameGroup = viewModel.FindGroupIdOfSession(sessionId) == target;
        e.DragEffects = sameGroup ? DragDropEffects.None : DragDropEffects.Move;
        ShowDragFeedback(_dragLabel, target, sameGroup, e.GetPosition(DragOverlay));
        e.Handled = true;
    }

    /// <summary>
    /// 更新拖拽过程中的三处落点提示:目标分组行点亮、落到未分组时把整棵树框起来、
    /// 跟随光标的幽灵标签写明“&lt;会话&gt; → &lt;目标&gt;”。
    /// </summary>
    /// <remarks>
    /// 拖放事件在无头测试里造不出来,因此这里是 internal:测试直接调它,验的是真实视觉状态
    /// (可见性、标签文案、被夹在叠层内的坐标),而不是"我相信 DragOver 会做对"。
    /// </remarks>
    /// <param name="draggedName">被拖会话的显示名。</param>
    /// <param name="targetGroupId">落点分组;<see cref="Guid.Empty" /> 表示未分组(树根)。</param>
    /// <param name="sameGroup">落点与当前所在分组相同(等于没动),此时只显示会话名、不给落点承诺。</param>
    /// <param name="position">光标在 <c>DragOverlay</c> 坐标系里的位置。</param>
    internal void ShowDragFeedback(
        string draggedName,
        Guid targetGroupId,
        bool sameGroup,
        Point position
    )
    {
        if (DataContext is not SessionTreeViewModel viewModel)
        {
            return;
        }
        HighlightDropTarget(viewModel, sameGroup ? Guid.Empty : targetGroupId);
        RootDropZone.IsVisible = !sameGroup && targetGroupId == Guid.Empty;
        DragGhostText.Text = sameGroup
            ? draggedName
            : $"{draggedName} → {viewModel.DescribeDropTarget(targetGroupId)}";
        DragGhost.IsVisible = true;

        // 先量一次再摆位:标签宽度随文案变,不夹住的话拖到右下角时会被裁掉半截。
        DragGhost.Measure(Size.Infinity);
        _dragGhostAnchor = position;
        ClampGhostIntoOverlay();
    }

    /// <summary>
    /// 把幽灵标签夹回叠层里。
    /// </summary>
    /// <remarks>
    /// <b>夹的依据必须是排布之后的真实尺寸,不能是 <c>DesiredSize</c>。</b>标签的高度取决于
    /// 字体度量,而字体是逐平台解析的:同一段文案在 macOS 上排出来比手工量到的
    /// <c>DesiredSize</c> 高 3px,拖到底边时标签就有一截露在叠层外面。
    /// <para>
    /// 所以摆位分两步:<see cref="ShowDragFeedback" /> 先按手工量到的尺寸摆一次(拖动时
    /// 不能等下一帧,否则标签会滞后于光标),排布定下真实 <c>Bounds</c> 后
    /// <c>SizeChanged</c> 再夹一次。尺寸没变时第二次是原地不动的。
    /// </para>
    /// </remarks>
    private void ClampGhostIntoOverlay()
    {
        // 排布还没跑过时 Bounds 是空的,退回手工量到的尺寸。
        Size ghost = DragGhost.Bounds.Size is { Width: > 0, Height: > 0 } arranged
            ? arranged
            : DragGhost.DesiredSize;
        double maxLeft = Math.Max(0, DragOverlay.Bounds.Width - ghost.Width);
        double maxTop = Math.Max(0, DragOverlay.Bounds.Height - ghost.Height);
        Canvas.SetLeft(DragGhost, Math.Clamp(_dragGhostAnchor.X + 12, 0, maxLeft));
        Canvas.SetTop(DragGhost, Math.Clamp(_dragGhostAnchor.Y + 16, 0, maxTop));
    }

    /// <summary>
    /// 拖着分组经过:按鼠标下面那一行算出空位,画插入线、更新幽灵标签;落回原处给「不可放置」光标。
    /// </summary>
    private void OnGroupDragOver(DragEventArgs e, Guid groupId, SessionTreeViewModel viewModel)
    {
        int slot = ResolveGroupSlot(e, viewModel);
        bool moves = viewModel.GroupDropIndex(groupId, slot) is not null;
        e.DragEffects = moves ? DragDropEffects.Move : DragDropEffects.None;
        ShowGroupDragFeedback(_dragLabel, slot, moves, e.GetPosition(DragOverlay));
        e.Handled = true;
    }

    /// <summary>鼠标下面那一行,以及鼠标在不在它的上半截,换算成空位(规则在视图模型里)。</summary>
    private static int ResolveGroupSlot(DragEventArgs e, SessionTreeViewModel viewModel)
    {
        ListBoxItem? container = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        bool upperHalf = container is not null && e.GetPosition(container).Y < container.Bounds.Height / 2;
        return viewModel.ResolveGroupDropSlot(FindNodeAt(e.Source), upperHalf);
    }

    /// <summary>
    /// 拖动分组时的落点提示:分组之间画一条插入线,幽灵标签写明「&lt;分组&gt; → 移到「X」之前」。
    /// </summary>
    /// <remarks>与 <see cref="ShowDragFeedback" /> 同理设成 internal:拖放事件在无头测试里造不出来,测试直接调它看真实的视觉状态。</remarks>
    /// <param name="draggedName">被拖分组的名字。</param>
    /// <param name="slot">空位(0 = 最前)。</param>
    /// <param name="moves">松手会不会真的挪动(落回原处时不画线、不给落点承诺)。</param>
    /// <param name="position">光标在 <c>DragOverlay</c> 坐标系里的位置。</param>
    internal void ShowGroupDragFeedback(string draggedName, int slot, bool moves, Point position)
    {
        if (DataContext is not SessionTreeViewModel viewModel)
        {
            return;
        }
        HighlightDropTarget(viewModel, Guid.Empty);
        RootDropZone.IsVisible = false;
        double? lineY = moves ? GroupSlotLineY(viewModel, slot) : null;
        GroupInsertLine.IsVisible = lineY is not null;
        if (lineY is { } y)
        {
            GroupInsertLine.Width = Math.Max(0, DragOverlay.Bounds.Width - 8);
            Canvas.SetLeft(GroupInsertLine, 4);
            Canvas.SetTop(GroupInsertLine, Math.Max(0, y - 1));
        }
        DragGhostText.Text = moves ? $"{draggedName} → {viewModel.DescribeGroupDropSlot(slot)}" : draggedName;
        DragGhost.IsVisible = true;
        DragGhost.Measure(Size.Infinity);
        _dragGhostAnchor = position;
        ClampGhostIntoOverlay();
    }

    /// <summary>
    /// 空位在叠层里的纵坐标:插在某个分组前面 = 那一组标题行的上沿;排到最后 = 最后一组(连同展开的会话)的下沿。
    /// 那一行此刻没被虚拟化出来(滚到视口外了)就不画线 —— 幽灵标签照样写明落点。
    /// </summary>
    private double? GroupSlotLineY(SessionTreeViewModel viewModel, int slot)
    {
        List<SessionTreeNodeViewModel> groups = [.. viewModel.Nodes.Where(static node => node.IsGroup)];
        if (groups.Count == 0)
        {
            return null;
        }
        if (slot < groups.Count)
        {
            return EdgeOf(groups[slot], bottom: false);
        }
        SessionTreeNodeViewModel last = groups[^1];
        int end = viewModel.Rows.IndexOf(last);
        if (end < 0)
        {
            return null;
        }
        while (end + 1 < viewModel.Rows.Count && last.Children.Contains(viewModel.Rows[end + 1]))
        {
            end++;
        }
        return end + 1 < viewModel.Rows.Count
            ? EdgeOf(viewModel.Rows[end + 1], bottom: false)
            : EdgeOf(viewModel.Rows[end], bottom: true);
    }

    private double? EdgeOf(SessionTreeNodeViewModel row, bool bottom)
    {
        if (SessionTreeRoot.ContainerFromItem(row) is not Control container
            || container.TranslatePoint(new Point(0, bottom ? container.Bounds.Height : 0), DragOverlay) is not { } point)
        {
            return null;
        }
        return point.Y;
    }

    private void OnTreeDrop(object? sender, DragEventArgs e) => FireAndForget.Run(async () =>
    {
        ClearDragFeedback();
        if (TryGetDraggedId(e, GroupDragPrefix) is { } groupId && DataContext is SessionTreeViewModel groupTree)
        {
            e.Handled = true;
            if (groupTree.GroupDropIndex(groupId, ResolveGroupSlot(e, groupTree)) is { } index)
            {
                await groupTree.MoveGroupAsync(groupId, index);
            }
            return;
        }
        if (
            TryGetDraggedSessionId(e) is not { } sessionId
            || DataContext is not SessionTreeViewModel viewModel
        )
        {
            return;
        }
        e.Handled = true;
        await viewModel.MoveSessionToGroupAsync(
            sessionId,
            viewModel.ResolveDropTargetGroupId(FindNodeAt(e.Source))
        );
    });

    /// <summary>
    /// 只有指针真的离开整棵树时才熄灭高亮。
    /// DragLeave 是路由事件:在树内部跨元素时(行与行之间、行 Border 与里面的文本之间)
    /// 每次都会冒泡上来,若见一次清一次,就会与紧随其后的 DragOver 交替点亮/熄灭 ——
    /// 表现为拖着会话在目标分组上移动时持续闪烁。
    /// </summary>
    private void OnTreeDragLeave(object? sender, DragEventArgs e)
    {
        if (!new Rect(SessionTreeRoot.Bounds.Size).Contains(e.GetPosition(SessionTreeRoot)))
        {
            ClearDragFeedback();
        }
    }

    /// <summary>点亮目标分组行;<see cref="Guid.Empty" />(未分组/树根)不对应任何行,等同于全部熄灭。</summary>
    private static void HighlightDropTarget(SessionTreeViewModel viewModel, Guid groupId)
    {
        foreach (SessionTreeNodeViewModel node in viewModel.Nodes)
        {
            node.IsDropTarget = node.IsGroup && node.Id == groupId;
        }
    }

    /// <summary>熄灭全部落点提示(行高亮 + 根落点框 + 幽灵标签)。</summary>
    internal void ClearDragFeedback()
    {
        if (DataContext is SessionTreeViewModel viewModel)
        {
            HighlightDropTarget(viewModel, Guid.Empty);
        }
        RootDropZone.IsVisible = false;
        GroupInsertLine.IsVisible = false;
        DragGhost.IsVisible = false;
    }

    /// <summary>从拖放载荷里取出会话 Id;不是本树发出的会话拖拽则返回 null。</summary>
    private static Guid? TryGetDraggedSessionId(DragEventArgs e) => TryGetDraggedId(e, SessionDragPrefix);

    /// <summary>按前缀从拖放载荷里取出 Id(会话 / 分组);前缀不对或不是本树发出的拖拽则返回 null。</summary>
    private static Guid? TryGetDraggedId(DragEventArgs e, string prefix)
    {
        string? text = e.DataTransfer.TryGetText();
        if (text is null || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        return Guid.TryParse(text[prefix.Length..], out Guid id) ? id : null;
    }

    /// <summary>
    /// 找出鼠标下的树节点:从命中的可视元素向上找第一个数据上下文是节点的控件。
    /// 树的空白处找不到节点,返回 null —— 落点解析(ResolveDropTargetGroupId)把它当作"未分组"。
    /// </summary>
    private static SessionTreeNodeViewModel? FindNodeAt(object? source) =>
        (source as Visual)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<SessionTreeNodeViewModel>()
            .FirstOrDefault();
}
