using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VelaShell.Core.Models;
using SyncInputChannel = VelaShell.Services.SyncInputChannel;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>同步输入频道的独立管理窗口。窗口只负责交互，成员索引和批量操作由视图模型处理。</summary>
public partial class ChannelManagerWindow : Window
{
    /// <summary>初始化频道管理工具窗口。</summary>
    public ChannelManagerWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool);
    }

    private ChannelManagerViewModel? ViewModel => DataContext as ChannelManagerViewModel;

    private static ChannelManagerRow? Row(object? sender) =>
        (sender as Control)?.DataContext as ChannelManagerRow;

    private void Selection_Click(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row)
        {
            ViewModel?.ToggleSelection(row);
        }
    }

    private void Expand_Click(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row)
        {
            ViewModel?.ToggleExpanded(row);
        }
    }

    private void Channel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string letter } || Row(sender) is not { } row
            || !Enum.TryParse(letter, ignoreCase: true, out SyncInputChannel channel))
        {
            return;
        }
        ViewModel?.Move(channel, row);
    }

    private void Filter_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && sender is Button { Tag: string tag } && int.TryParse(tag, out int filter))
        {
            viewModel.FilterIndex = filter;
        }
    }

    private void Target_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: > 0 } combo || ViewModel is not { } viewModel)
        {
            return;
        }
        viewModel.Move((SyncInputChannel)(combo.SelectedIndex - 1));
        combo.SelectedIndex = 0;
    }

    private void Pause_Click(object? sender, RoutedEventArgs e) => ViewModel?.SetPaused(true);

    private void Resume_Click(object? sender, RoutedEventArgs e) => ViewModel?.SetPaused(false);

    private void Leave_Click(object? sender, RoutedEventArgs e) => ViewModel?.Leave();

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Header_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source
            && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }
        ToggleMaximize();
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.IsVisible = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (SearchBox.IsVisible)
            {
                SearchBox.Text = string.Empty;
                SearchBox.IsVisible = false;
            }
            else this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
