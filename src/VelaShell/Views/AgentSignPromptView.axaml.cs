using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>agent 转发的逐次确认窗口:远端要用本机 agent 里的钥签名时,问用户一次。</summary>
public partial class AgentSignPromptView : Window
{
    private AgentSignPromptViewModel? _viewModel;

    /// <summary>初始化窗口,并订阅视图模型的裁决以随之关闭。</summary>
    public AgentSignPromptView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);

        // VM 的三个命令只落 Result;窗口无系统标题栏,由这里负责随 Result 关闭。
        DataContextChanged += (_, _) =>
        {
            _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = DataContext as AgentSignPromptViewModel;
            _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        };

        // 焦点先落在「拒绝」上:窗口弹出时用户可能正在终端里打字。
        Opened += (_, _) => this.FindControl<Button>("DenyButton")?.Focus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentSignPromptViewModel.Result) && _viewModel?.Result is { } decision)
        {
            // 命令由按钮点击触发,仍在输入事件栈内:推迟关闭(同 HostKeyPromptView)。
            this.PostClose(decision);
        }
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }
}
