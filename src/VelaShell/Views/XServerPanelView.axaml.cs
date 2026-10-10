using Avalonia.Controls;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>标题栏 X Server 按钮的浮层:连着的 X 程序清单、断开、解除卡住、停止(规范 §4A.2)。</summary>
public partial class XServerPanelView : UserControl
{
    private readonly Func<string, Task<bool>> _confirmDisconnect;
    private XServerToggleViewModel? _viewModel;

    /// <summary>初始化 <see cref="XServerPanelView" /> 并加载 XAML 组件。</summary>
    public XServerPanelView()
    {
        InitializeComponent();
        _confirmDisconnect = ConfirmDisconnectAsync;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>断开前的确认框只有视图拿得到窗口:接上视图模型的 <see cref="XServerToggleViewModel.ConfirmDisconnectAsync" />。</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is { } previous && ReferenceEquals(previous.ConfirmDisconnectAsync, _confirmDisconnect))
        {
            previous.ConfirmDisconnectAsync = null;
        }
        _viewModel = DataContext as XServerToggleViewModel;
        _viewModel?.ConfirmDisconnectAsync = _confirmDisconnect;
    }

    private async Task<bool> ConfirmDisconnectAsync(string name) =>
        TopLevel.GetTopLevel(this) is Window owner
        && await MessageDialog.ConfirmAsync(owner, Strings.Get("XServer_DisconnectConfirmTitle"),
            Strings.Format("XServer_DisconnectConfirmMessage", name), Strings.Get("XServer_Disconnect"), Strings.Cancel,
            MessageDialogKind.Warning, danger: true);
}
