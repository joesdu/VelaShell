using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace VelaShell.Views;

/// <summary>状态栏视图,展示当前连接状态与相关运行时信息。</summary>
public partial class StatusBarView : UserControl
{
    /// <summary>宿主提供的远程文件面板切换命令。</summary>
    public static readonly StyledProperty<ICommand?> FileBrowserCommandProperty =
        AvaloniaProperty.Register<StatusBarView, ICommand?>(nameof(FileBrowserCommand));

    /// <summary>当前是否展示远程文件入口。</summary>
    public static readonly StyledProperty<bool> ShowFileBrowserButtonProperty =
        AvaloniaProperty.Register<StatusBarView, bool>(nameof(ShowFileBrowserButton));

    /// <summary>远程文件面板当前是否打开。</summary>
    public static readonly StyledProperty<bool> IsFileBrowserVisibleProperty =
        AvaloniaProperty.Register<StatusBarView, bool>(nameof(IsFileBrowserVisible));

    /// <summary>初始化 <see cref="StatusBarView"/> 并加载 XAML 组件。</summary>
    public StatusBarView()
    {
        InitializeComponent();
    }

    /// <summary>切换远程文件面板的命令。</summary>
    public ICommand? FileBrowserCommand
    {
        get => GetValue(FileBrowserCommandProperty);
        set => SetValue(FileBrowserCommandProperty, value);
    }

    /// <summary>是否显示远程文件按钮。</summary>
    public bool ShowFileBrowserButton
    {
        get => GetValue(ShowFileBrowserButtonProperty);
        set => SetValue(ShowFileBrowserButtonProperty, value);
    }

    /// <summary>远程文件面板是否打开,用于切换按钮强调色。</summary>
    public bool IsFileBrowserVisible
    {
        get => GetValue(IsFileBrowserVisibleProperty);
        set => SetValue(IsFileBrowserVisibleProperty, value);
    }
}
