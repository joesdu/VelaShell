using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>设置窗口视图,承载各设置分页并处理保存、重置与关闭等交互。</summary>
public partial class SettingsView : Window
{
    private SettingsViewModel? _viewModel;

    /// <summary>初始化 <see cref="SettingsView"/>,加载组件并绑定视图模型的关闭请求。</summary>
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

    /// <summary>Esc 以取消语义关闭设置窗口,未保存预览由 <see cref="OnClosed" /> 回滚。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

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
    private async void ResetToDefaults_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(
            this,
            Strings.Get("Settings_ResetConfirmTitle"),
            Strings.Get("Settings_ResetConfirmMessage"),
            danger: true
        );
        if (confirmed)
        {
            _viewModel.ResetCommand.Execute().Subscribe();
        }
    }
}
