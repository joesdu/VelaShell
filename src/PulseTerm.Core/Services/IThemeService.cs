namespace PulseTerm.Core.Services;

public interface IThemeService
{
    string CurrentTheme { get; }

    void SetTheme(string themeName);

    event Action<string>? ThemeChanged;

    /// <summary>The user's accent-color override as a hex string (e.g. "#00D4AA"), or null to use
    /// the theme's default accent.</summary>
    string? AccentColor { get; }

    /// <summary>Sets (or clears, when null/empty) the accent-color override; applied live, no restart.</summary>
    void SetAccent(string? hexColor);

    /// <summary>Raised when the accent override changes; argument is the hex color or null for default.</summary>
    event Action<string?>? AccentChanged;
}
