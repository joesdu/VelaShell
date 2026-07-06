namespace PulseTerm.Core.Services;

public class ThemeService : IThemeService
{
    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase) { "dark", "light", "system" };

    private string _currentTheme;
    private string? _accentColor;

    public ThemeService(string initialTheme = "dark", string? initialAccent = null)
    {
        _currentTheme = ValidThemes.Contains(initialTheme) ? initialTheme.ToLowerInvariant() : "dark";
        _accentColor = NormalizeHex(initialAccent);
    }

    public string CurrentTheme => _currentTheme;

    public event Action<string>? ThemeChanged;

    public string? AccentColor => _accentColor;

    public event Action<string?>? AccentChanged;

    public void SetAccent(string? hexColor)
    {
        var normalized = NormalizeHex(hexColor);
        if (_accentColor == normalized)
            return;

        _accentColor = normalized;
        AccentChanged?.Invoke(_accentColor);
    }

    /// <summary>Validates a #RGB / #RRGGBB / #RRGGBBAA color, returning it normalized, or null when
    /// empty. Throws on a malformed value.</summary>
    private static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        var value = hex.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;

        var digits = value.Length - 1;
        if ((digits is 3 or 6 or 8) && value[1..].All(Uri.IsHexDigit))
            return value.ToUpperInvariant();

        throw new ArgumentException($"Invalid accent color: '{hex}'. Expected #RGB, #RRGGBB, or #RRGGBBAA.", nameof(hex));
    }

    public void SetTheme(string themeName)
    {
        var normalized = themeName.ToLowerInvariant();

        if (!ValidThemes.Contains(normalized))
            throw new ArgumentException($"Invalid theme: '{themeName}'. Valid themes: dark, light, system.", nameof(themeName));

        if (_currentTheme == normalized)
            return;

        _currentTheme = normalized;
        ThemeChanged?.Invoke(_currentTheme);
    }
}
