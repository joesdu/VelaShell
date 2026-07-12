using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 会话录制回放中心(设计 NceE6)。终端回放区挂载一个只读的
/// <see cref="VelaTerminalControl" />,由 VM 的 Feed/Reset 回调驱动。
/// </summary>
public partial class RecordingPlayerView : Window
{
    /// <summary>RIS(ESC c)完全重置:选择新录制/拖动时间轴时清屏重放。</summary>
    private static readonly byte[] RisResetSequence = [0x1B, (byte)'c'];

    private readonly VelaTerminalControl _terminal;
    private RecordingPlayerViewModel? _viewModel;

    public RecordingPlayerView()
    {
        InitializeComponent();

        // 只读回放终端:不接输入,不参与焦点。
        _terminal = new()
        {
            Focusable = false,
            IsHitTestVisible = false
        };
        TerminalHost.Child = _terminal;
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.FeedSink = null;
                _viewModel.ResetSink = null;
            }
            _viewModel = DataContext as RecordingPlayerViewModel;
            if (_viewModel is not null)
            {
                // VM 的播放定时器在 UI 线程(DispatcherTimer),直接喂给控件即可。
                _viewModel.FeedSink = data => _terminal.Feed(data);
                _viewModel.ResetSink = () => _terminal.Feed(RisResetSequence);
            }
        };
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Header_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>右下角缩放手柄:无边框窗口经此拖拽调整大小。</summary>
    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>倍速按钮:按 VM 定义的档位循环(1x…16x)。</summary>
    private void CycleSpeed_Click(object? sender, RoutedEventArgs e) => _viewModel?.CycleSpeed();

    /// <summary>导出选中录制为 asciicast v2(.cast)文件。</summary>
    private async void ExportRecording_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { HasSelection: true } vm)
        {
            return;
        }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("Recorder_Export"),
            SuggestedFileName = $"velashell-recording-{DateTime.Now:yyyyMMdd-HHmmss}.cast",
            DefaultExtension = "cast"
        });
        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, vm.BuildAsciicast());
        }
    }
}
