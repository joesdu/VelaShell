using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Localization;
using VelaShell.Services;
using VelaShell.Views;
using VelaShell.Controls.DependencyInjection;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.DependencyInjection;
using VelaShell.Presentation.DependencyInjection;
using VelaShell.ViewModels;

namespace VelaShell;

public class App : Application
{
    private ServiceProvider? _serviceProvider;
    private AppSettings? _startupSettings;
    private IThemeService? _themeService;
    private TrayIconService? _trayIconService;

    public IServiceProvider? Services => _serviceProvider;

    /// <summary>托盘图标当前是否挂载(主窗口据此决定“关闭时最小化到托盘”是否可用)。</summary>
    public bool TrayIconActive => _trayIconService?.IsActive == true;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _serviceProvider = new ServiceCollection()
                           .AddVelaShellPresentation()
                           .AddVelaShellControls()
                           .AddVelaShellInfrastructure()
                           .AddSingleton<IThemeService>(_ => new ThemeService("system"))
                           .AddSingleton<ISettingsPreviewService, SettingsPreviewService>()
                           .AddSingleton<IHostKeyPrompt, HostKeyPromptDialogService>()
                           .AddSingleton<ILocalizationService, LocalizationService>()
                           .AddSingleton<IKeyboardShortcutService, KeyboardShortcutService>()
                           .AddSingleton<SettingsViewModel>()
                           .AddSingleton<MainWindowViewModel>()
                           .BuildServiceProvider();
        _themeService = _serviceProvider.GetRequiredService<IThemeService>();

        // Live-rebinding localized strings ({loc:Localize}) follow the DI service (#4).
        LocalizedStrings.Instance.Attach(_serviceProvider.GetRequiredService<ILocalizationService>());
        _themeService.ThemeChanged += OnThemeChanged;
        _themeService.AccentChanged += ApplyAccent;
        ApplyThemeVariant(_themeService.CurrentTheme);
        ApplyAccent(_themeService.AccentColor);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyPersistedPreferences();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel viewModel = _serviceProvider?.GetRequiredService<MainWindowViewModel>() ?? new MainWindowViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            desktop.MainWindow = mainWindow;

            // 启动时窗口状态(设置 → 外观):记住上次 / 最大化 / 默认大小。
            ApplyStartupWindowState(mainWindow, _startupSettings);

            // 开机自启动与设置保持同步(用户可能在外部改过注册表)。
            StartupRegistration.Apply(_startupSettings?.General.LaunchAtStartup == true);

            // 过期会话/传输日志清理(设置 → 常规/文件传输 → 日志保留天数),后台执行。
            SessionLogService.CleanupExpired(_startupSettings?.General.LogRetentionDays ?? 30);
            TransferLogService.CleanupExpired(_startupSettings?.Transfer.LogDirectory,
                _startupSettings?.Transfer.TransferLogRetentionDays ?? 30);

            // 托盘图标(关闭时最小化到托盘);设置保存后热更新挂载状态。
            _trayIconService = new(this);
            _trayIconService.ShowRequested += () =>
            {
                mainWindow.Show();
                if (mainWindow.WindowState == WindowState.Minimized)
                {
                    mainWindow.WindowState = WindowState.Normal;
                }
                mainWindow.Activate();
            };
            _trayIconService.ExitRequested += mainWindow.ForceClose;
            _trayIconService.SetEnabled(_startupSettings?.General.MinimizeToTray == true);
            if (_serviceProvider?.GetService<ISettingsService>() is { } settingsService)
            {
                settingsService.SettingsSaved += settings =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        StartupRegistration.Apply(settings.General.LaunchAtStartup);
                        _trayIconService?.SetEnabled(settings.General.MinimizeToTray);
                    });
            }

            // 退出时释放容器,确保 SonnetDB 引擎正常关闭(WAL/段刷盘);
            // 并清理「默认编辑器打开」遗留的 remote-edit 临时文件。
            desktop.Exit += (_, _) =>
            {
                _trayIconService?.Dispose();
                ExternalEditSessionManager.CleanupAll();
                DisposeServicesOnExit();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Releases the DI container on shutdown. Teardown disconnects any live SSH/SFTP sessions —
    /// each <c>Disconnect()</c> is a blocking network round-trip — and flushes the SonnetDB engine.
    /// The previous code ran this synchronously via <c>Dispose()</c> on the UI thread, so a slow or
    /// unresponsive connection left the process alive well after the window closed. We now use async
    /// disposal (also the correct path for the IAsyncDisposable services) bounded by a short timeout,
    /// so the app exits promptly; the OS reclaims any still-closing sockets on process teardown.
    /// </summary>
    private void DisposeServicesOnExit()
    {
        ServiceProvider? provider = _serviceProvider;
        _serviceProvider = null;
        if (provider is null)
        {
            return;
        }
        try
        {
            provider.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown: never block or fault the exit path.
        }
    }

    /// <summary>
    /// Applies persisted language / theme / accent before the first window shows,
    /// so the app starts in the user's chosen look without a visible re-theme flash.
    /// </summary>
    private void ApplyPersistedPreferences()
    {
        if (_serviceProvider is null)
        {
            return;
        }
        try
        {
            AppSettings settings = _serviceProvider.GetRequiredService<ISettingsService>()
                                                   .GetSettingsAsync().GetAwaiter().GetResult();
            _startupSettings = settings;
            _serviceProvider.GetRequiredService<ILocalizationService>()
                            .SetLanguage(settings.Language);
            if (!string.IsNullOrWhiteSpace(settings.Theme))
            {
                _themeService?.SetTheme(settings.Theme);
            }
            if (!string.IsNullOrWhiteSpace(settings.AccentColor))
            {
                _themeService?.SetAccent(settings.AccentColor);
            }
        }
        catch
        {
            // Corrupt settings must never block startup; defaults apply.
        }
    }

    /// <summary>启动时窗口状态(设置 → 外观 → 启动时窗口状态),在窗口显示前应用以免闪动。</summary>
    private static void ApplyStartupWindowState(MainWindow window, AppSettings? settings)
    {
        if (settings is null)
        {
            return;
        }
        switch (settings.Appearance.StartupWindowState)
        {
            case "maximized":
                window.WindowState = WindowState.Maximized;
                break;
            case "default":
                break;
            default: // remember
                AppearanceOptions a = settings.Appearance;
                if (a is { LastWindowWidth: >= 800, LastWindowHeight: >= 500 })
                {
                    window.Width = a.LastWindowWidth;
                    window.Height = a.LastWindowHeight;
                }
                if (a.LastWindowMaximized)
                {
                    window.WindowState = WindowState.Maximized;
                }
                break;
        }
    }

    private void OnThemeChanged(string themeName)
    {
        ApplyThemeVariant(themeName);
    }

    private void ApplyThemeVariant(string themeName)
    {
        RequestedThemeVariant = themeName.ToLowerInvariant() switch
        {
            "light"  => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _        => ThemeVariant.Dark
        };
    }

    /// <summary>
    /// Applies the accent-color override live by shadowing the themed accent brushes at the
    /// application level; every <c>DynamicResource VelaAccent</c> updates without a restart (#3).
    /// A null/empty value removes the override and restores the theme's default accent.
    /// </summary>
    private void ApplyAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            Resources.Remove("VelaAccent");
            Resources.Remove("VelaAccentDim");
            Resources.Remove("VelaAccentForeground");
            return;
        }
        if (!Color.TryParse(hex, out Color color))
        {
            return;
        }
        Resources["VelaAccent"] = new SolidColorBrush(color);
        // Dim variant: same hue at ~19% opacity, matching the design's #RRGGBB30 tokens.
        Resources["VelaAccentDim"] = new SolidColorBrush(new Color(0x30, color.R, color.G, color.B));

        // 自定义强调色的配对前景按亮度自动选:亮底深字、深底浅字,
        // 避免用户挑深色 accent 后按钮文字(令牌随主题固定)对比不足。
        double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        Resources["VelaAccentForeground"] = new SolidColorBrush(luminance > 0.55 ? Color.Parse("#0A0E14") : Color.Parse("#FFFBEB"));
    }
}
