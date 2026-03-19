using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using PulseTerm.Controls.DependencyInjection;
using PulseTerm.Infrastructure.DependencyInjection;
using PulseTerm.App.ViewModels;
using PulseTerm.App.Views;
using PulseTerm.Presentation.DependencyInjection;
using PulseTerm.Core.Services;

namespace PulseTerm.App;

public partial class App : Application
{
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
            .AddSingleton<MainWindowViewModel>()
            .BuildServiceProvider();

        _themeService = _serviceProvider.GetRequiredService<IThemeService>();

        _themeService.ThemeChanged += OnThemeChanged;
        ApplyThemeVariant(_themeService.CurrentTheme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
}
