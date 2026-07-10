using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VelaShell.App.ViewModels;

namespace VelaShell.App.Views;

public partial class SettingsView : Window
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.CloseRequested -= OnCloseRequested;
            }

            _viewModel = DataContext as SettingsViewModel;
            if (_viewModel is not null)
            {
                _viewModel.CloseRequested += OnCloseRequested;
            }
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
}
