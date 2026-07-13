namespace VelaShell.Core.Services;

/// <summary>主题服务:管理当前主题(明/暗等)与强调色覆盖,并在变化时通知订阅方。</summary>
public interface IThemeService
{
    /// <summary>当前生效的主题名称。</summary>
    string CurrentTheme { get; }

    /// <summary>
    /// The user's accent-color override as a hex string (e.g. "#00D4AA"), or null to use
    /// the theme's default accent.
    /// </summary>
    string? AccentColor { get; }

    /// <summary>切换到指定名称的主题;立即应用,无需重启。</summary>
    void SetTheme(string themeName);

    /// <summary>主题变更时触发,参数为新的主题名称。</summary>
    event Action<string>? ThemeChanged;

    /// <summary>Sets (or clears, when null/empty) the accent-color override; applied live, no restart.</summary>
    void SetAccent(string? hexColor);

    /// <summary>Raised when the accent override changes; argument is the hex color or null for default.</summary>
    event Action<string?>? AccentChanged;
}
