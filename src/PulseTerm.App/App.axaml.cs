using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using PulseTerm.Controls.DependencyInjection;
using PulseTerm.Infrastructure.DependencyInjection;
using PulseTerm.App.ViewModels;
using PulseTerm.App.Views;
using PulseTerm.Presentation.DependencyInjection;
using PulseTerm.Core.Data;
using PulseTerm.Core.Services;

namespace PulseTerm.App;

public partial class App : Application
{
    public IServiceProvider? Services => _serviceProvider;

    private ServiceProvider? _serviceProvider;
    private IThemeService? _themeService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        _serviceProvider = new ServiceCollection()
            .AddPulseTermPresentation()
            .AddPulseTermControls()
            .AddPulseTermInfrastructure()
            .AddSingleton<IThemeService>(_ => new ThemeService("system"))
            .AddSingleton<PulseTerm.Core.Localization.ILocalizationService, PulseTerm.Core.Localization.LocalizationService>()
            .AddSingleton<Services.IKeyboardShortcutService, Services.KeyboardShortcutService>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<MainWindowViewModel>()
            .BuildServiceProvider();

        _themeService = _serviceProvider.GetRequiredService<IThemeService>();

        // Live-rebinding localized strings ({loc:Localize}) follow the DI service (#4).
        Localization.LocalizedStrings.Instance.Attach(
            _serviceProvider.GetRequiredService<PulseTerm.Core.Localization.ILocalizationService>());

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
            var viewModel = _serviceProvider?.GetRequiredService<MainWindowViewModel>()
                ?? new MainWindowViewModel();

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            // 退出时释放容器,确保 SonnetDB 引擎正常关闭(WAL/段刷盘);
            // 并清理「默认编辑器打开」遗留的 remote-edit 临时文件。
            desktop.Exit += (_, _) =>
            {
                PulseTerm.App.Services.ExternalEditSessionManager.CleanupAll();
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
        var provider = _serviceProvider;
        _serviceProvider = null;
        if (provider is null)
            return;

        try
        {
            provider.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown: never block or fault the exit path.
        }
    }

    /// <summary>Applies persisted language / theme / accent before the first window shows,
    /// so the app starts in the user's chosen look without a visible re-theme flash.</summary>
    private void ApplyPersistedPreferences()
    {
        if (_serviceProvider is null)
            return;

        try
        {
            var settings = _serviceProvider.GetRequiredService<ISettingsService>()
                .GetSettingsAsync().GetAwaiter().GetResult();

            _serviceProvider.GetRequiredService<PulseTerm.Core.Localization.ILocalizationService>()
                .SetLanguage(settings.Language);
            if (!string.IsNullOrWhiteSpace(settings.Theme))
                _themeService?.SetTheme(settings.Theme);
            if (!string.IsNullOrWhiteSpace(settings.AccentColor))
                _themeService?.SetAccent(settings.AccentColor);
        }
        catch
        {
            // Corrupt settings must never block startup; defaults apply.
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
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
    }

    /// <summary>
    /// Applies the accent-color override live by shadowing the themed accent brushes at the
    /// application level; every <c>DynamicResource PulseAccent</c> updates without a restart (#3).
    /// A null/empty value removes the override and restores the theme's default accent.
    /// </summary>
    private void ApplyAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            Resources.Remove("PulseAccent");
            Resources.Remove("PulseAccentDim");
            return;
        }

        if (!Color.TryParse(hex, out var color))
            return;

        Resources["PulseAccent"] = new SolidColorBrush(color);
        // Dim variant: same hue at ~19% opacity, matching the design's #RRGGBB30 tokens.
        Resources["PulseAccentDim"] = new SolidColorBrush(new Color(0x30, color.R, color.G, color.B));
    }
}
