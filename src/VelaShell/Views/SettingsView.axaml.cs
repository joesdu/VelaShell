using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using VelaShell.Views.Settings;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>设置窗口视图,承载各设置分页并处理保存、重置与关闭等交互。</summary>
public partial class SettingsView : Window
{
    private SettingsViewModel? _viewModel;

    /// <summary>页面宿主:按分区按需创建页面并缓存(见 <see cref="SettingsPageHost" />)。</summary>
    private readonly SettingsPageHost _pages;

    /// <summary>初始化 <see cref="SettingsView"/>,加载组件并绑定视图模型的关闭请求。</summary>
    public SettingsView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        _pages = new(PageHost);
        DataContextChanged += (_, _) =>
        {
            _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel?.CloseRequested -= OnCloseRequested;
            _viewModel = DataContext as SettingsViewModel;
            _viewModel?.CloseRequested += OnCloseRequested;
            _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
            // 装上视图模型的那一刻就把当前页建出来;窗口打开时看到的就是它。
            _pages.Show(_viewModel?.SelectedSectionKey ?? SettingsSectionKey.General);
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSectionKey) && _viewModel is { } vm)
        {
            _pages.Show(vm.SelectedSectionKey);
        }
    }

    /// <summary>已创建的设置页数量(懒加载回归用例读它)。</summary>
    internal int CreatedPageCountForTest => _pages.CreatedPageCount;

    /// <summary>
    /// 打开时把窗口收进当前屏幕的工作区。
    /// </summary>
    /// <remarks>
    /// 这里以前钉死 948×768 且 <c>CanResize=False</c>。1366×768 的笔记本工作区高度只有
    /// 约 728(减去任务栏),150% 缩放下更小 —— 窗口比屏幕高,底部那条 52px 的
    /// 「保存 / 取消 / 恢复默认」就落到屏幕外,而不可缩放意味着<b>没有任何办法</b>够到它:
    /// 键盘 Tab 过去焦点也在可视区外。
    /// <para>
    /// 内容区本来就是 ScrollViewer、底部操作条是独立的一行,所以缩小窗口只会让可滚动区变短,
    /// 操作条始终贴在底边。这里只负责初始尺寸不越界,并把窗口摆回工作区中央。
    /// </para>
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitIntoWorkArea();
    }

    private void FitIntoWorkArea()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return;
        }
        // WorkingArea 是物理像素,而窗口尺寸按 DIP 计 —— 高 DPI 下不换算就会算出
        // 一个"看起来放得下"的假结论。
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        // Wayland 的阴影与描边画在 Width/Height 之外,要一起算进去;其它平台窗口尺寸就是全部。
        double frame = WindowChrome.OuterFrameSize(this);
        double availableWidth = (screen.WorkingArea.Width / scaling) - frame;
        double availableHeight = (screen.WorkingArea.Height / scaling) - frame;

        // 留一点余量,免得正好顶到工作区边缘(某些桌面环境的自动隐藏面板会占掉几像素)。
        const double margin = 16;
        double width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, availableWidth - margin));
        double height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, availableHeight - margin));
        if (Math.Abs(width - Width) < 0.5 && Math.Abs(height - Height) < 0.5)
        {
            return; // 放得下,保持 CenterOwner 定好的位置。
        }
        Width = width;
        Height = height;
        // 尺寸变了,CenterOwner 算出来的位置就不再居中(还可能把标题栏顶出屏幕外)。
        Position = new PixelPoint(
            screen.WorkingArea.X + (int)Math.Max(0, (screen.WorkingArea.Width - (width * scaling)) / 2),
            screen.WorkingArea.Y + (int)Math.Max(0, (screen.WorkingArea.Height - (height * scaling)) / 2));
    }

    // CloseRequested 由保存/取消命令(按钮点击)触发,仍在输入事件栈内:推迟关闭,
    // 避免后续路由打到已销毁的窗口刷 "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void OnCloseRequested(object? sender, EventArgs e) => this.PostClose();

    /// <summary>Esc 以取消语义关闭设置窗口,未保存预览由 <see cref="OnClosed" /> 回滚。</summary>
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
            this.BeginWindowMoveDrag(e);
        }
    }

    /// <summary>恢复默认是破坏性操作:先确认再执行,防止误点丢失全部设置(设置审计 C-11)。</summary>
    private void ResetToDefaults_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    ) => FireAndForget.Run(async () =>
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
    });
}
