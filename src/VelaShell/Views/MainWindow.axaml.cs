using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Views;

public partial class MainWindow : Window
{
    private IDisposable? _fileBrowserVisibilitySub;
    private bool _forceClose;

    /// <summary>自绘缩放抓取区:普通状态按下即进入原生缩放;最大化时整层隐藏(见 OnPropertyChanged)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 最大化/全屏时缩放抓取区必须让位(否则挡住屏幕边缘 5px 的标题栏与状态栏点击)。
        if (change.Property == WindowStateProperty && this.FindControl<Panel>("ResizeGrips") is { } grips)
        {
            grips.IsVisible = WindowState == WindowState.Normal;
        }
    }

    /// <summary>
    /// File panel height restored when the panel reopens (§6 drag to grow). Default 360
    /// = sidebar recent-connections block (320) + footer (40), so the file panel's top divider
    /// lands on the same horizontal line as the sidebar's tree/recent splitter (用户反馈).
    /// </summary>
    private double _lastFileRowHeight = 360;

    private AppSettings? _settings;

    private ISettingsService? _settingsService;
    private bool _sidebarOnRight;

    // NOTE: the window's DataContext must be assigned AFTER construction (object initializer in
    // App): child views' compiled bindings (x:DataType = their own VM) briefly see the inherited
    // MainWindowViewModel during InitializeComponent if it is set earlier, spraying
    // InvalidCastException binding errors before each view's local DataContext binding takes over.
    // The cost is a handful of benign "Value is null" messages from DockControl's theme bindings
    // ($self.Layout.* with FallbackValue) while Layout is still null — the lesser noise.
    public MainWindow()
    {
        InitializeComponent();
        if (this.FindControl<SidebarView>("SidebarHost") is { } sidebar)
        {
            sidebar.OpenConnectionProfileRequested += OnOpenConnectionProfileRequested;
            sidebar.RecentConnectRequested += OnSidebarRecentConnectRequested;
            sidebar.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
        }
        DataContextChanged += (_, _) => HookFileBrowserVisibility();
        Opened += OnWindowOpened;
        if (OperatingSystem.IsWindows())
        {
            Opened += (_, _) => SetupSnapLayouts();
        }
    }

    // ---- 原生窗口效果(WindowChrome 手法) --------------------------------------
    // WindowDecorations="None" 的自绘窗体默认失去 DWM 框架语义。这里补回
    // WS_CAPTION|WS_THICKFRAME|WS_MIN/MAXIMIZEBOX(样式回调,防 Avalonia 重算时
    // 清掉),再用 WM_NCCALCSIZE 让客户区占满窗口(无可视系统标题/边框)——
    // 由此找回:DWM 阴影、Win11 圆角、最小化/最大化动画、悬停最大化按钮的
    // 贴靠布局面板(还需 WM_NCHITTEST 对按钮报 HTMAXBUTTON)。
    // WM_NCHITTEST 其余区域强制 HTCLIENT,防止系统按 WS_CAPTION 在顶部划出
    // 非客户带吞掉自绘标题栏的输入(Avalonia 12 extend 模式踩过的坑)。

    private const int HTMAXBUTTON = 9;

    private TitleBarView? TitleBar => this.FindControl<TitleBarView>("TitleBarHost");

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtrW(IntPtr hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial long SetWindowLongPtrW(IntPtr hWnd, int index, long value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINRECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public WINRECT Monitor;
        public WINRECT Work;
        public uint Flags;
    }

    private const long StyleWsCaption = 0x00C00000, StyleWsThickFrame = 0x00040000,
                       StyleWsMinimizeBox = 0x00020000, StyleWsMaximizeBox = 0x00010000;

    private void SetupSnapLayouts()
    {
        // 样式回调:Avalonia 每次重算窗口样式后追加 DWM 框架位。
        Win32Properties.AddWindowStylesCallback(this, (style, exStyle) =>
            ((uint)(style | StyleWsCaption | StyleWsThickFrame | StyleWsMinimizeBox | StyleWsMaximizeBox), exStyle));
        Win32Properties.AddWndProcHookCallback(this, SnapLayoutsWndProc);

        // 立即应用一次并触发 FRAMECHANGED,当前会话即刻生效。
        if (TryGetPlatformHandle() is { } handle)
        {
            const int GWL_STYLE = -16;
            const uint SWP_FRAMECHANGED = 0x0020, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;
            long style = GetWindowLongPtrW(handle.Handle, GWL_STYLE);
            SetWindowLongPtrW(handle.Handle, GWL_STYLE,
                style | StyleWsCaption | StyleWsThickFrame | StyleWsMinimizeBox | StyleWsMaximizeBox);
            SetWindowPos(handle.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
        }
    }

    private IntPtr SnapLayoutsWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCMOUSELEAVE = 0x02A2,
                   WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2;
        const int HTCLIENT = 1;
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                // 客户区占满窗口(去掉可视的系统标题/边框,保留 DWM 框架语义)。
                // 最大化时窗口矩形按惯例大出边框宽度,须裁回工作区,否则四周越屏。
                if (IsZoomed(hWnd))
                {
                    IntPtr monitor = MonitorFromWindow(hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                    var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                    if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
                    {
                        Marshal.StructureToPtr(info.Work, lParam, false);
                    }
                }
                handled = true;
                return IntPtr.Zero;
            case WM_NCHITTEST:
                if (IsPointOverMaximizeButton(lParam))
                {
                    TitleBar?.SetMaximizeNcHover(true);
                    handled = true;
                    return HTMAXBUTTON;
                }
                TitleBar?.SetMaximizeNcHover(false);
                // 其余全部按客户区处理:拖动/双击由自绘标题栏负责,缩放由自绘抓取区负责;
                // 不拦截会让 DefWindowProc 按 WS_CAPTION 在顶部划非客户带吞掉输入。
                handled = true;
                return HTCLIENT;
            case WM_NCMOUSELEAVE:
                TitleBar?.SetMaximizeNcHover(false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                handled = true; // 吞掉按下,防 DefWindowProc 的历史行为
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                handled = true;
                TitleBar?.SetMaximizeNcHover(false);
                TitleBar?.ToggleMaximize();
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private bool IsPointOverMaximizeButton(IntPtr lParam)
    {
        if (TitleBar?.MaximizeButtonControl is not { IsVisible: true } button || !button.IsAttachedToVisualTree())
        {
            return false;
        }
        // lParam:屏幕物理坐标(低 16 位 x / 高 16 位 y,有符号)。
        long packed = lParam.ToInt64();
        int screenX = unchecked((short)(packed & 0xFFFF));
        int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
        PixelPoint topLeft = button.PointToScreen(new Point(0, 0));
        var rect = new PixelRect(topLeft,
            new PixelSize((int)(button.Bounds.Width * RenderScaling), (int)(button.Bounds.Height * RenderScaling)));
        return rect.Contains(new PixelPoint(screenX, screenY));
    }

    /// <summary>
    /// Collapses/expands the file-panel rows as FileBrowser.IsVisible flips. WhenAnyValue
    /// tracks through the FileBrowser property itself, so per-tab rebinds re-subscribe automatically.
    /// </summary>
    private void HookFileBrowserVisibility()
    {
        _fileBrowserVisibilitySub?.Dispose();
        _fileBrowserVisibilitySub = null;
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        _fileBrowserVisibilitySub = vm
                                    .WhenAnyValue(x => x.FileBrowser.IsVisible)
                                    .Subscribe(visible => Dispatcher.UIThread.Post(() => SetFileRowsVisible(visible)));
    }

    private void SetFileRowsVisible(bool visible)
    {
        if (this.FindControl<Grid>("MainAreaGrid") is not { RowDefinitions.Count: >= 3 } grid)
        {
            return;
        }
        RowDefinition splitterRow = grid.RowDefinitions[1];
        RowDefinition fileRow = grid.RowDefinitions[2];
        if (visible)
        {
            splitterRow.Height = new(5);
            fileRow.MinHeight = 120;
            fileRow.Height = new(Math.Max(_lastFileRowHeight, 120));
        }
        else
        {
            // Remember the user's dragged height so reopening restores it.
            if (fileRow.Height is { IsAbsolute: true, Value: > 0 })
            {
                _lastFileRowHeight = fileRow.Height.Value;
            }
            fileRow.MinHeight = 0;
            fileRow.Height = new(0);
            splitterRow.Height = new(0);
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.TerminalSearchRequested += OnTerminalSearchRequested;
            vm.NewConnectionRequested += (_, _) => _ = OpenProfileDialogAsync(null);
            vm.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
            vm.InteractiveAuthenticator = PromptCredentialsAsync;
            vm.MultilinePasteConfirmer = ConfirmMultilinePasteAsync;
            vm.ExportBufferRequested += (_, _) => _ = ExportTerminalBufferAsync(vm);
            // 工具菜单“连接诊断”:对当前标签的配置打开诊断中心(设计 RGXg1)。
            vm.DiagnosticsRequested += profile => Dispatcher.UIThread.Post(() => _ = OpenDiagnosticsDialogAsync(profile));

            // 资源管理器树:右键连接/双击连接 + 右键编辑。
            if (vm.Sidebar.SessionTree is { } tree)
            {
                tree.ConnectRequested += profile => Dispatcher.UIThread.Post(() => _ = vm.TryConnectProfileAsync(profile));
                tree.EditRequested += profile => Dispatcher.UIThread.Post(() => _ = OpenProfileDialogAsync(profile));

                // 打开 SFTP:先连接(已连接则新开标签),随后展开文件浏览面板。
                tree.OpenSftpRequested += profile => Dispatcher.UIThread.Post(async () =>
                {
                    // 连接失败已在标签页内以覆盖层提示(设计 yxjmg),这里不再弹全局框;
                    // 仅在连上后展开 SFTP 面板。
                    TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
                    if (tab is { ConnectionStatus: SessionStatus.Connected } && !vm.FileBrowser.IsVisible)
                    {
                        vm.ToggleFileBrowser();
                    }
                });

                // 端口转发:打开隧道管理面板并预选该服务器(全局非模态,见 fuXS7);
                // 无需先建立终端会话,面板会在创建隧道时后台自动连接。
                tree.PortForwardRequested += profile => Dispatcher.UIThread.Post(() => vm.OpenTunnelPanel(profile));

                // 连接诊断:对选中的配置打开诊断中心(设计 RGXg1)。
                tree.DiagnoseRequested += profile => Dispatcher.UIThread.Post(() => _ = OpenDiagnosticsDialogAsync(profile));

                // 断开连接:断开该配置所有已连接的终端标签(保留缓冲以便重连)。
                // 必须按 Profile.Id 匹配——tab.SessionId 是 SSH 连接会话 ID,与配置 ID
                // 不是一回事,之前用它比较永远匹配不上,菜单点了没反应(用户反馈 #2)。
                tree.DisconnectRequested += profile => Dispatcher.UIThread.Post(() =>
                {
                    foreach (TerminalTabViewModel tab in vm.TabBar.Tabs.OfType<TerminalTabViewModel>().Where(t =>
                                 t.Profile?.Id == profile.Id && t.ConnectionStatus == SessionStatus.Connected).ToList())
                    {
                        tab.DisconnectCommand.Execute().Subscribe();
                    }
                });
            }
            await vm.InitializeAsync();
        }

        // 外观/行为设置:启动时应用一次,设置保存后热更新。
        if (Application.Current is App { Services: { } services } && services.GetService<ISettingsService>() is { } settingsService)
        {
            _settingsService = settingsService;
            settingsService.SettingsSaved += OnSettingsSavedForWindow;
            Closed += (_, _) => settingsService.SettingsSaved -= OnSettingsSavedForWindow;

            // 外观即时预览(未持久化):同样应用窗口外观,但不覆盖 _settings(已保存状态)。
            if (services.GetService<ISettingsPreviewService>() is { } previewService)
            {
                previewService.PreviewRequested += OnSettingsPreviewedForWindow;
                Closed += (_, _) => previewService.PreviewRequested -= OnSettingsPreviewedForWindow;
            }
            try
            {
                _settings = await settingsService.GetSettingsAsync();
                ApplyWindowAppearance(_settings);
            }
            catch
            {
                // 设置读取失败不影响窗口本身。
            }
            await RestoreSessionsAsync(_settings);
        }
    }

    /// <summary>
    /// 恢复会话(设置 → 常规 → 启动):重连上次退出时在线的连接。缺凭据的
    /// 配置会走既有的登录验证弹窗;单个失败不影响其余会话。
    /// </summary>
    private async Task RestoreSessionsAsync(AppSettings? settings)
    {
        if (settings?.General.RestoreSessionsOnStartup != true || settings.General.LastOpenProfileIds.Count == 0 || DataContext is not MainWindowViewModel vm || Application.Current is not App { Services: { } services } || services.GetService<ISessionRepository>() is not { } repository)
        {
            return;
        }
        foreach (Guid profileId in settings.General.LastOpenProfileIds.Distinct().ToList())
        {
            try
            {
                SessionProfile? profile = await repository.GetSessionAsync(profileId);
                if (profile is not null)
                {
                    await vm.TryConnectProfileAsync(profile);
                }
            }
            catch
            {
                // 配置已删除或连接失败:跳过,继续恢复其余会话。
            }
        }
    }

    private void OnSettingsSavedForWindow(AppSettings settings) =>
        Dispatcher.UIThread.Post(() =>
        {
            _settings = settings;
            ApplyWindowAppearance(settings);
        });

    private void OnSettingsPreviewedForWindow(AppSettings settings) => Dispatcher.UIThread.Post(() => ApplyWindowAppearance(settings));

    /// <summary>应用设置 → 外观:窗口透明度、侧边栏位置、界面字体/字号。
    /// (菜单栏显隐设置已随文字菜单一并移除:自绘标题栏承载窗口控制按钮,必须常显。)</summary>
    private void ApplyWindowAppearance(AppSettings settings)
    {
        AppearanceOptions a = settings.Appearance;
        Opacity = Math.Clamp(a.WindowOpacityPercent, 10, 100) / 100.0;
        ApplySidebarPosition(a.SidebarPosition == "right");
        if (Application.Current is not { } app)
        {
            return;
        }
        // 界面字体:覆盖 VelaUiFont 令牌(空或默认 Inter 时还原主题字体);
        // App 级 :is(Window) 样式让所有窗口继承,未显式指定 FontFamily 的文本统一换字体。
        string uiFont = a.UiFont.Trim();
        if (string.IsNullOrEmpty(uiFont) || string.Equals(uiFont, "Inter", StringComparison.OrdinalIgnoreCase))
        {
            app.Resources.Remove("VelaUiFont");
        }
        else
        {
            app.Resources["VelaUiFont"] = new FontFamily($"{uiFont}, Segoe UI, Microsoft YaHei, sans-serif");
        }

        // 界面字号:覆盖 VelaUiFontSize 令牌(同上,全窗口继承);同时覆盖 Fluent 的
        // ControlContentThemeFontSize,让内置控件(按钮/输入框/下拉等)一起缩放。
        double uiFontSize = Math.Clamp(a.UiFontSize, 9, 24);
        app.Resources["VelaUiFontSize"] = uiFontSize;
        app.Resources["ControlContentThemeFontSize"] = uiFontSize;
    }

    /// <summary>侧边栏位置(设置 → 外观):交换侧边栏与主区所在列,分隔条留在中间。</summary>
    private void ApplySidebarPosition(bool right)
    {
        if (right == _sidebarOnRight)
        {
            return;
        }
        if (this.FindControl<SidebarView>("SidebarHost") is not { } sidebar || this.FindControl<Grid>("MainAreaGrid") is not { } main || sidebar.Parent is not Grid contentGrid || contentGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }
        _sidebarOnRight = right;
        ColumnDefinitions cols = contentGrid.ColumnDefinitions;
        int sidebarCol = right ? 2 : 0;
        int mainCol = right ? 0 : 2;

        // 保留用户拖出来的侧边栏宽度。
        GridLength sidebarWidth = cols[right ? 0 : 2].Width;
        if (!sidebarWidth.IsAbsolute)
        {
            sidebarWidth = new(260);
        }
        cols[sidebarCol].Width = sidebarWidth;
        cols[sidebarCol].MinWidth = 180;
        cols[sidebarCol].MaxWidth = 520;
        cols[mainCol].Width = new(1, GridUnitType.Star);
        cols[mainCol].MinWidth = 400;
        cols[mainCol].MaxWidth = double.PositiveInfinity;
        Grid.SetColumn(sidebar, sidebarCol);
        Grid.SetColumn(main, mainCol);
    }

    /// <summary>托盘“退出”/关闭确认后的真正退出:跳过托盘拦截与确认弹窗。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>
    /// 关闭链路(设置 → 常规):最小化到托盘 → 关闭前确认 → 记住窗口状态。
    /// 系统关机/应用程序退出(CloseReason ≠ 用户点关闭)不拦截。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        AppSettings? settings = _settings;
        bool userInitiated = e.CloseReason == WindowCloseReason.WindowClosing;
        if (!_forceClose && userInitiated && settings is not null)
        {
            if (settings.General.MinimizeToTray && Application.Current is App app && HasActiveTrayIcon(app))
            {
                e.Cancel = true;
                Hide();
                base.OnClosing(e);
                return;
            }
            if (settings.General.ConfirmBeforeClose && HasConnectedSessions())
            {
                e.Cancel = true;
                _ = ConfirmCloseAsync();
                base.OnClosing(e);
                return;
            }
        }
        PersistWindowBounds(settings);
        base.OnClosing(e);
    }

    private static bool HasActiveTrayIcon(App app) => app.TrayIconActive;

    private bool HasConnectedSessions() => DataContext is MainWindowViewModel vm && vm.TabBar.Tabs.OfType<TerminalTabViewModel>().Any(t => t.IsConnected);

    private async Task ConfirmCloseAsync()
    {
        bool confirmed = await MessageDialog.ConfirmAsync(this, Strings.Get("Main_CloseConfirmTitle"),
                             Strings.Get("Main_CloseConfirmBody"));
        if (confirmed)
        {
            ForceClose();
        }
    }

    /// <summary>
    /// 退出时的状态记忆:窗口尺寸/最大化(启动时窗口状态 = 记住上次)与
    /// 已连接会话的配置 id(恢复会话)。同步等待,本地写入很快。
    /// </summary>
    private void PersistWindowBounds(AppSettings? settings)
    {
        if (_settingsService is null || settings is null)
        {
            return;
        }
        bool rememberWindow = settings.Appearance.StartupWindowState == "remember";
        bool rememberSessions = settings.General.RestoreSessionsOnStartup;
        if (!rememberWindow && !rememberSessions)
        {
            return;
        }
        try
        {
            if (rememberWindow)
            {
                settings.Appearance.LastWindowMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    settings.Appearance.LastWindowWidth = Width;
                    settings.Appearance.LastWindowHeight = Height;
                }
            }
            if (rememberSessions && DataContext is MainWindowViewModel vm)
            {
                settings.General.LastOpenProfileIds = [.. vm.TabBar.Tabs.OfType<TerminalTabViewModel>().Where(t => t is { IsConnected: true, Profile: { } p } && p.Id != Guid.Empty).Select(t => t.Profile!.Id).Distinct()];
            }
            _settingsService.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            // 记忆退出状态失败不阻塞退出。
        }
    }

    /// <summary>Menu/palette 终端内查找 → opens the visible terminal view's search bar.</summary>
    private void OnTerminalSearchRequested(object? sender, EventArgs e)
    {
        foreach (TerminalTabView view in this.GetVisualDescendants().OfType<TerminalTabView>())
        {
            if (view.IsEffectivelyVisible)
            {
                view.OpenSearch();
                return;
            }
        }
    }

    // The window uses the native OS title bar per design spec §2 — no custom chrome.

    private void OnSidebarRecentConnectRequested(object? sender, RecentConnectionEntry entry)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // 连接失败已在标签页内以覆盖层提示(设计 yxjmg),不再弹全局框。
        _ = vm.TryConnectRecentAsync(entry);
    }

    private void OnOpenConnectionProfileRequested(object? sender, EventArgs e) => _ = OpenProfileDialogAsync(null);

    /// <summary>打开“新建连接”弹窗;传入 existing 时为编辑既有配置。</summary>
    private async Task OpenProfileDialogAsync(SessionProfile? existing)
    {
        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        if (Application.Current is not App app || app.Services is null)
        {
            return;
        }

        // 新建连接的默认端口与默认密钥(设置 → 常规 / 密钥管理)。
        int defaultPort = _settings?.DefaultPort ?? 22;
        string? defaultKeyPath = null;
        if (_settings?.Keys.DefaultKeyName is { Length: > 0 } keyName)
        {
            string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", keyName);
            if (File.Exists(candidate))
            {
                defaultKeyPath = candidate;
            }
        }
        var connectionProfileViewModel = new ConnectionProfileViewModel(existing,
            app.Services.GetService<IConnectionWorkflowService>(),
            app.Services.GetService<ISessionRepository>(),
            defaultPort,
            defaultKeyPath);
        var dialog = new ConnectionProfileView
        {
            DataContext = connectionProfileViewModel
        };
        SessionProfile? profile = await dialog.ShowDialog<SessionProfile?>(this);
        if (profile is null)
        {
            return;
        }

        // 保存/连接均已持久化配置 —— 资源管理器树同步刷新。
        await mainWindowViewModel.RefreshSessionTreeAsync();

        // 仅“连接”按钮触发实际连接;“保存”只落库。
        if (!connectionProfileViewModel.ConnectAfterClose)
        {
            return;
        }

        // TryConnectProfileAsync never throws — 连接失败已在标签页内以覆盖层提示(设计 yxjmg),
        // 不再弹全局框。
        await mainWindowViewModel.TryConnectProfileAsync(profile);
    }

    /// <summary>打开连接诊断中心(设计 RGXg1):打开即自动执行一轮四步检测。</summary>
    private async Task OpenDiagnosticsDialogAsync(SessionProfile profile)
    {
        if (Application.Current is not App app || app.Services?.GetService<IConnectionDiagnosticsService>() is not { } diagnosticsService)
        {
            return;
        }
        var dialog = new ConnectionDiagnosticsView
        {
            DataContext = new ConnectionDiagnosticsViewModel(profile, diagnosticsService)
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>打开设置窗口(设计 §14):DI 单例 VM,打开时重新加载持久化设置。</summary>
    private async Task OpenSettingsAsync()
    {
        if (Application.Current is not App app || app.Services?.GetService<SettingsViewModel>() is not { } settingsViewModel)
        {
            return;
        }
        await settingsViewModel.LoadCommand.Execute();
        var dialog = new SettingsView { DataContext = settingsViewModel };
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// 登录验证流程(设计:身份验证 第1步/第2步):补全用户名与认证凭据。
    /// 已信任主机显示其指纹;首次连接提示握手时记录(TOFU)。取消返回 null。
    /// </summary>
    private async Task<SessionProfile?> PromptCredentialsAsync(SessionProfile profile)
    {
        string? knownFingerprint = null;
        if (Application.Current is App app && app.Services?.GetService<IHostKeyService>() is { } hostKeys)
        {
            try
            {
                List<KnownHost> hosts = await hostKeys.GetKnownHostsAsync();
                knownFingerprint = hosts
                                   .FirstOrDefault(h => h.Host == profile.Host && h.Port == profile.Port)
                                   ?.Fingerprint;
            }
            catch
            {
                // 指纹仅用于展示,读取失败不阻塞验证流程。
            }
        }
        var viewModel = new AuthenticationDialogViewModel(profile.Host,
            profile.Port,
            profile.Username,
            knownFingerprint,
            profile.AuthMethod);
        var dialog = new AuthenticationDialogView { DataContext = viewModel };
        AuthenticationResult? result = await dialog.ShowDialog<AuthenticationResult?>(this);
        if (result is null)
        {
            return null;
        }
        profile.Username = result.Username;
        profile.AuthMethod = result.AuthMethod;
        if (result.AuthMethod == AuthMethod.Password)
        {
            // 交接点:SecureString → 管线所需的明文,随即释放 SecureString。
            using (result.Password)
            {
                profile.Password = SecureStringConvert.ToPlaintext(result.Password);
            }
            profile.RememberPassword = result.RememberPassword;
        }
        else
        {
            profile.PrivateKeyPath = result.PrivateKeyPath;
            profile.PrivateKeyPassphrase = result.PrivateKeyPassphrase;
        }
        return profile;
    }

    /// <summary>导出终端输出(§12.4):有选区导出选区,否则导出整个缓冲区(scrollback+屏幕)。</summary>
    private async Task ExportTerminalBufferAsync(MainWindowViewModel vm)
    {
        (string Text, string SuggestedFileName)? export = vm.GetActiveTerminalExport();
        if (export is null)
        {
            return;
        }
        (string text, string suggestedName) = export.Value;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("Main_ExportTerminalTitle"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new(Strings.Get("Main_FileTypeText")) { Patterns = ["*.txt"] },
                new(Strings.Get("Main_FileTypeLog")) { Patterns = ["*.log"] }
            ]
        });
        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, text);
            vm.StatusBar.Status = Strings.Format("Main_TerminalExported", path);
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowMessageAsync(this, Strings.Get("Main_ExportFailed"), ex.Message, MessageDialogKind.Error);
        }
    }

    /// <summary>
    /// 多行粘贴确认(设置 → 终端 → 粘贴时确认多行内容):预览前几行,防止把
    /// 整段脚本误粘进 shell 直接执行。
    /// </summary>
    private Task<bool> ConfirmMultilinePasteAsync(string text)
    {
        string[] lines = text.Split('\n');
        IEnumerable<string> previewLines = lines.Take(5).Select(l => l.TrimEnd('\r'));
        string preview = string.Join('\n', previewLines);
        if (lines.Length > 5)
        {
            preview += "\n…";
        }
        return MessageDialog.ConfirmAsync(this, Strings.Get("Main_PasteMultilineTitle"),
            Strings.Format("Main_PasteMultilineBody", lines.Length, preview));
    }
}
