namespace VelaShell.Core.Services;

/// <summary>
/// Default <see cref="IThemeService"/> implementation that tracks the active theme
/// ("dark", "light", or "system") and optional accent color, raising change events when either updates.
/// </summary>
public class ThemeService(string initialTheme = "dark", string? initialAccent = null) : IThemeService
{
    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase) { "dark", "light", "system" };

    /// <summary>
    /// The currently active theme name ("dark", "light", or "system").
    /// </summary>
    public string CurrentTheme { get; private set; } = ValidThemes.Contains(initialTheme) ? initialTheme.ToLowerInvariant() : "dark";

    /// <summary>
    /// Raised when the active theme changes, carrying the new theme name.
    /// </summary>
    public event Action<string>? ThemeChanged;

    /// <summary>
    /// The current accent color as a normalized hex string, or <c>null</c> when none is set.
    /// </summary>
    public string? AccentColor { get; private set; } = NormalizeHex(initialAccent);

    /// <summary>
    /// Raised when the accent color changes, carrying the new normalized hex value (or <c>null</c>).
    /// </summary>
    public event Action<string?>? AccentChanged;

    /// <summary>
    /// Sets the accent color from the given hex string (validated and normalized),
    /// raising <see cref="AccentChanged"/> only when the value actually changes.
    /// </summary>
    public void SetAccent(string? hexColor)
    {
        string? normalized = NormalizeHex(hexColor);
        if (AccentColor == normalized)
        {
            return;
        }
        AccentColor = normalized;
        AccentChanged?.Invoke(AccentColor);
    }

    /// <summary>
    /// Switches the active theme to the given name, raising <see cref="ThemeChanged"/>
    /// when it changes. Throws <see cref="ArgumentException"/> for an unrecognized theme.
    /// </summary>
    public void SetTheme(string themeName)
    {
        string normalized = themeName.ToLowerInvariant();
        if (!ValidThemes.Contains(normalized))
        {
            throw new ArgumentException($@"Invalid theme: '{themeName}'. Valid themes: dark, light, system.", nameof(themeName));
        }
        if (CurrentTheme == normalized)
        {
            return;
        }
        CurrentTheme = normalized;
        ThemeChanged?.Invoke(CurrentTheme);
    }

    /// <summary>
    /// Validates a #RGB / #RRGGBB / #RRGGBBAA color, returning it normalized, or null when
    /// empty. Throws on a malformed value.
    /// </summary>
    private static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        string value = hex.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }
        int digits = value.Length - 1;
        if (digits is 3 or 6 or 8 && value[1..].All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }
        throw new ArgumentException($@"Invalid accent color: '{hex}'. Expected #RGB, #RRGGBB, or #RRGGBBAA.", nameof(hex));
    }
}
