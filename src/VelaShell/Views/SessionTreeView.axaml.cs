using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Views;

/// <summary>会话树视图:以分组树形式展示会话,支持展开折叠、双击连接与右键菜单。</summary>
public partial class SessionTreeView : UserControl
{
    /// <summary>初始化会话树视图并加载 XAML 组件。</summary>
    public SessionTreeView()
    {
        InitializeComponent();
    }

    /// <summary>单击分组行即切换展开/折叠(设计 FrJPu:chevron 随之翻转)。</summary>
    private void Group_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: true } node })
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    /// <summary>双击会话行直接连接(分组行仅展开/折叠)。</summary>
    private void Session_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node } && DataContext is SessionTreeViewModel viewModel)
        {
            viewModel.SelectedNode = node;
            viewModel.RequestConnect(node.Id);
        }
    }

    /// <summary>
    /// 右键弹菜单前先选中所指行:菜单里的命令都作用于 SelectedNode,不选中会
    /// 对着上一次选择的会话执行。
    /// </summary>
    private void Session_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            return;
        }
        if (sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node } && DataContext is SessionTreeViewModel viewModel)
        {
            viewModel.SelectedNode = node;
        }
    }
}
