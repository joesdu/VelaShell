using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Views;

/// <summary>侧边栏视图:承载资源管理器树、最近连接列表与底部设置入口,并向宿主窗口冒泡打开连接/设置/重连请求。</summary>
public partial class SidebarView : UserControl
{
    /// <summary>创建侧边栏视图并加载其可视组件。</summary>
    public SidebarView()
    {
        InitializeComponent();
    }

    /// <summary>用户请求打开“新建连接”配置弹窗时触发(顶部新建按钮)。</summary>
    public event EventHandler? OpenConnectionProfileRequested;

    /// <summary>Raised by the footer gear button to open the settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user double-clicks a recent connection to reconnect to it.</summary>
    public event EventHandler<RecentConnectionEntry>? RecentConnectRequested;

    private void OpenConnectionProfile_Click(object? sender, RoutedEventArgs e)
    {
        OpenConnectionProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RecentConnection_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RecentConnectionItemViewModel item })
        {
            RecentConnectRequested?.Invoke(this, item.Entry);
        }
    }
}
