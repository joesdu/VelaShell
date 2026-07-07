using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PulseTerm.Presentation.ViewModels;

namespace PulseTerm.App.Views;

public partial class SessionTreeView : UserControl
{
    public SessionTreeView()
    {
        InitializeComponent();
    }

    /// <summary>双击会话行直接连接(分组行仅展开/折叠)。</summary>
    private void Session_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node }
            && DataContext is SessionTreeViewModel viewModel)
        {
            viewModel.SelectedNode = node;
            viewModel.RequestConnect(node.Id);
        }
    }
}
