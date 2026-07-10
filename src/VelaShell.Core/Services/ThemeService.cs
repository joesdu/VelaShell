namespace VelaShell.Core.Services;

public class ThemeService(string initialTheme = "dark", string? initialAccent = null) : IThemeService
{
    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase) { "dark", "light", "system" };

    public string CurrentTheme { get; private set; } = ValidThemes.Contains(initialTheme) ? initialTheme.ToLowerInvariant() : "dark";

    public event Action<string>? ThemeChanged;

    public string? AccentColor { get; private set; } = NormalizeHex(initialAccent);

    public event Action<string?>? AccentChanged;

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
