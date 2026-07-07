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
            .AddSingleton<PulseTerm.Core.Ssh.IHostKeyService>(sp =>
                new PulseTerm.Infrastructure.Ssh.HostKeyService(sp.GetRequiredService<JsonDataStore>()))
            .AddSingleton<Services.IKeyboardShortcutService, Services.KeyboardShortcutService>()
            .AddSingleton<JsonDataStore>()
            .AddSingleton<ISettingsService, SettingsService>()
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
        }

        base.OnFrameworkInitializationCompleted();
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
