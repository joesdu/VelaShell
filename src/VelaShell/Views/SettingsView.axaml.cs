using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.ViewModels;

namespace VelaShell.Views;

public partial class SettingsView : Window
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            _viewModel?.CloseRequested -= OnCloseRequested;
            _viewModel = DataContext as SettingsViewModel;
            _viewModel?.CloseRequested += OnCloseRequested;
        };
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>窗口以任意方式关闭(取消/Esc/系统关闭)都要回滚未保存的外观预览。</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewModel?.NotifyClosed();
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>恢复默认是破坏性操作:先确认再执行,防止误点丢失全部设置(设置审计 C-11)。</summary>
    private async void ResetToDefaults_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(this, "恢复默认设置",
                             "将把所有页面的设置恢复为出厂值(点击「保存设置」后才会写入磁盘)。确定继续吗?",
                             danger: true);
        if (confirmed)
        {
            _viewModel.ResetCommand.Execute().Subscribe();
        }
    }
}
