using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.Platform;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>主机密钥确认窗口,提示用户信任或拒绝服务器的主机密钥。</summary>
public partial class HostKeyPromptView : Window
{
    private HostKeyPromptViewModel? _viewModel;

    /// <summary>初始化主机密钥提示窗口,并订阅视图模型的结果以随决策关闭窗口。</summary>
    public HostKeyPromptView()
    {
        InitializeComponent();
        // macOS 无边框 + SizeToContent 弹窗底部按钮点不动的命中区域修复(见该类型注释)。
        MacBorderlessWindowFix.Apply(this);

        // VM 的信任/拒绝命令只落 Result;窗口无系统标题栏,由这里负责随 Result 关闭。
        DataContextChanged += (_, _) =>
        {
            _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = DataContext as HostKeyPromptViewModel;
            _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostKeyPromptViewModel.Result) && _viewModel?.Result is { } decision)
        {
            Close(decision);
        }
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
