using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Services.Syntax;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 插件日志窗口(插件管理页每行的「查看日志」):只读显示这个插件自己那份日志的尾部,能刷新、能打开所在文件夹。
/// </summary>
public partial class PluginLogWindow : Window
{
    private readonly IThemeService? _themeService;

    /// <summary>初始化窗口,按平台装外框(与插件管理窗口同规格)。打开时读一次尾部。</summary>
    public PluginLogWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrips);
        _themeService = (Application.Current as App)?.Services?.GetService<IThemeService>();
        _themeService?.EffectiveThemeChanged += OnEffectiveThemeChanged;
        Opened += (_, _) => FireAndForget.Run(RefreshAsync);
    }

    private PluginLogViewModel? ViewModel => DataContext as PluginLogViewModel;

    /// <summary>重读尾部,并把视图滚到最后一行 —— 最近发生的事在最下面。</summary>
    internal async Task RefreshAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        await vm.RefreshAsync();
        LogText.Text = vm.Text;
        // 等文本排版完再着色和挪光标,否则高亮与滚动范围还是旧的。
        Dispatcher.UIThread.Post(() =>
        {
            ApplySyntaxHighlighting();
            LogText.CaretOffset = LogText.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private void ApplySyntaxHighlighting()
    {
        try
        {
            var definition = SyntaxHighlightingService.Resolve(
                ".log", FirstLineOf(LogText.Text), ActiveUiTheme());
            LogText.SyntaxHighlighting = null;
            LogText.SyntaxHighlighting = definition;
        }
        catch (Exception)
        {
            // 高亮失败时退化为纯文本,不能影响日志查看。
            LogText.SyntaxHighlighting = null;
        }
    }

    private UiTheme ActiveUiTheme() =>
        UiThemeCatalog.Resolve(_themeService?.CurrentTheme, ActualThemeVariant != ThemeVariant.Light);

    private void OnEffectiveThemeChanged() =>
        Dispatcher.UIThread.Post(ApplySyntaxHighlighting);

    private static string? FirstLineOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        int end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(RefreshAsync);

    private void OpenFolder_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel?.Directory is not { } directory)
        {
            return;
        }
        // 插件还没写过日志时目录可能还不存在:建出来再打开,免得系统文件管理器报「找不到」。
        Directory.CreateDirectory(directory);
        await Launcher.LaunchDirectoryInfoAsync(new(directory));
    });

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        this.BeginWindowMoveDrag(e);
    }

    /// <summary>缩放抓取区。只认左键:系统 sizing 模态循环只在左键弹起时退出(#116)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _themeService?.EffectiveThemeChanged -= OnEffectiveThemeChanged;
        base.OnClosed(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
