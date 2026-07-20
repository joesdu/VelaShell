namespace VelaShell.Core.Services;

/// <summary>主题服务:管理当前主题(明/暗等)与强调色覆盖,并在变化时通知订阅方。</summary>
public interface IThemeService
{
    /// <summary>当前生效的主题名称。</summary>
    string CurrentTheme { get; }

    /// <summary>
    /// 用户自定义的强调色覆盖,为十六进制字符串(如 "#00D4AA");为 null 时使用主题的默认强调色。
    /// </summary>
    string? AccentColor { get; }

    /// <summary>切换到指定名称的主题;立即应用,无需重启。</summary>
    void SetTheme(string themeName);

    /// <summary>主题变更时触发,参数为新的主题名称。</summary>
    event Action<string>? ThemeChanged;

    /// <summary>设置(或在为 null/空时清除)强调色覆盖;实时生效,无需重启。</summary>
    void SetAccent(string? hexColor);

    /// <summary>强调色覆盖变更时触发;参数为十六进制颜色,或为 null 表示默认。</summary>
    event Action<string?>? AccentChanged;
}
