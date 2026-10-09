using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Behaviors;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>隧道(端口转发)面板视图,展示与管理隧道相关的 UI。</summary>
public partial class TunnelPanelView : UserControl
{
    private readonly Func<string, Task<bool>> _confirmDeleteHandler;
    private TunnelPanelViewModel? _viewModel;

    /// <summary>初始化 <see cref="TunnelPanelView"/> 并加载 XAML 组件,接线标题栏拖拽。</summary>
    public TunnelPanelView()
    {
        InitializeComponent();
        _confirmDeleteHandler = ConfirmDeleteAsync;
        DataContextChanged += OnDataContextChanged;

        // 拖拽 + 越界夹紧 + 松手落盘,与文件传输提示、消息中心共用一份实现。
        if (this.FindControl<Border>("DragHandle") is { } handle)
        {
            PanelDragHandler.Attach(this, handle);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is { } previous && ReferenceEquals(previous.ConfirmDelete, _confirmDeleteHandler))
        {
            previous.ConfirmDelete = null;
        }

        _viewModel = DataContext as TunnelPanelViewModel;
        if (_viewModel is { } vm)
        {
            vm.ConfirmDelete = _confirmDeleteHandler;
        }
    }

    private void HelpButton_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        await new TunnelHelpDialog().ShowDialog(owner);
    });

    private async Task<bool> ConfirmDeleteAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        return await MessageDialog.ConfirmAsync(
            owner,
            Strings.ConfirmDeleteTitle,
            message,
            Strings.Delete,
            kind: MessageDialogKind.Warning,
            danger: true
        );
    }
}
