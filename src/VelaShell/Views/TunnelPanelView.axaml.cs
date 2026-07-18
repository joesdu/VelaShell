using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VelaShell.Views;

/// <summary>隧道(端口转发)面板视图,展示与管理隧道相关的 UI。</summary>
public partial class TunnelPanelView : UserControl
{
    /// <summary>初始化 <see cref="TunnelPanelView"/> 并加载 XAML 组件。</summary>
    public TunnelPanelView()
    {
        InitializeComponent();
    }

    private async void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        await new TunnelHelpDialog().ShowDialog(owner);
    }
}
