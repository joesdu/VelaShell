using System.Reactive;
using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

/// <summary>底部多终端实时输入栏的显示状态与共享目标选择。</summary>
public sealed class BroadcastInputViewModel : ReactiveObject
{
    /// <summary>创建广播栏状态并复用指定目标选择器。</summary>
    public BroadcastInputViewModel(TerminalTargetSelectorViewModel targetSelector)
    {
        TargetSelector = targetSelector ?? throw new ArgumentNullException(nameof(targetSelector));
        ToggleCommand = ReactiveCommand.Create(Toggle);
        CloseCommand = ReactiveCommand.Create(Close);
    }

    /// <summary>广播输入使用的终端目标选择器。</summary>
    public TerminalTargetSelectorViewModel TargetSelector { get; }

    /// <summary>广播栏当前是否显示。</summary>
    public bool IsVisible
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>切换广播栏显示状态。</summary>
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    /// <summary>关闭广播栏。</summary>
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>true 表示聚焦广播捕获区,false 表示聚焦活动终端。</summary>
    public event EventHandler<bool>? FocusRequested;

    private void Toggle()
    {
        IsVisible = !IsVisible;
        FocusRequested?.Invoke(this, IsVisible);
    }

    private void Close()
    {
        if (!IsVisible)
        {
            return;
        }
        IsVisible = false;
        FocusRequested?.Invoke(this, false);
    }
}
