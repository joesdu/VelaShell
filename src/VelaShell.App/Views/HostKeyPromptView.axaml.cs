using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.App.ViewModels;

namespace VelaShell.App.Views;

public partial class HostKeyPromptView : Window
{
    private HostKeyPromptViewModel? _viewModel;

    public HostKeyPromptView()
    {
        InitializeComponent();

        // VM 的信任/拒绝命令只落 Result;窗口无系统标题栏,由这里负责随 Result 关闭。
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as HostKeyPromptViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostKeyPromptViewModel.Result) && _viewModel?.Result is { } result)
        {
            Close(result);
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
