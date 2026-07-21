namespace VelaShell.Core.Services;

/// <summary>
/// <see cref="IThemeService"/> 的默认实现,跟踪当前活动主题("dark"、"light" 或 "system")
/// 与可选的强调色,任一方更新时抛出变更事件。
/// </summary>
public class ThemeService(string initialTheme = "dark", string? initialAccent = null) : IThemeService
{
    private static readonly HashSet<string> ValidThemes = [with(StringComparer.OrdinalIgnoreCase), "dark", "light", "system"];

    /// <summary>
    /// 当前活动的主题名称("dark"、"light" 或 "system")。
    /// </summary>
    public string CurrentTheme { get; private set; } = ValidThemes.Contains(initialTheme) ? initialTheme.ToLowerInvariant() : "dark";

    /// <summary>
    /// 活动主题变更时触发,携带新的主题名称。
    /// </summary>
    public event Action<string>? ThemeChanged;

    /// <summary>
    /// 当前强调色,为规范化后的十六进制字符串;未设置时为 <c>null</c>。
    /// </summary>
    public string? AccentColor { get; private set; } = NormalizeHex(initialAccent);

    /// <summary>
    /// 强调色变更时触发,携带新的规范化十六进制值(或 <c>null</c>)。
    /// </summary>
    public event Action<string?>? AccentChanged;

    /// <summary>
    /// 根据给定十六进制字符串设置强调色(经过校验与规范化),
    /// 仅当值确实变化时抛出 <see cref="AccentChanged"/>。
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
    /// 将活动主题切换到给定名称,变更时抛出 <see cref="ThemeChanged"/>。
    /// 对于无法识别的主题抛出 <see cref="ArgumentException"/>。
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
    /// 校验 #RGB / #RRGGBB / #RRGGBBAA 颜色,返回规范化后的值;为空时返回 null。
    /// 值格式非法时抛出。
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
