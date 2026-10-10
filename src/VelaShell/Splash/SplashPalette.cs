using SkiaSharp;
using VelaShell.Core.Models;

namespace VelaShell.Splash;

/// <summary>
/// 启动画面用到的那几个主题令牌,已解析成 Skia 颜色。
/// </summary>
/// <remarks>
/// 直接取自 <see cref="UiThemeCatalog" /> 的种子色 —— 与界面上的 <c>Vela*</c> 令牌同源
/// (DESIGN.md §2.0),所以换哪套主题启动画面都跟着换,这里不出现任何颜色字面量。
/// 强调色覆盖与设置页同一个口径:有覆盖用覆盖,没有用主题自己的。
/// </remarks>
internal sealed record SplashPalette(
    bool IsDark,
    SKColor Page,
    SKColor Terminal,
    SKColor Surface,
    SKColor BorderPrimary,
    SKColor BorderSecondary,
    SKColor TextPrimary,
    SKColor TextSecondary,
    SKColor TextTertiary,
    SKColor TextMuted,
    SKColor Accent,
    SKColor Success,
    SKColor Info)
{
    /// <summary>按主题 Id 与强调色覆盖解析配色;「跟随系统」按 <paramref name="systemPrefersDark" /> 落到默认暗/亮主题。</summary>
    /// <param name="themeId">主题 Id(含 <c>system</c>,认不出来按「跟随系统」处理)。</param>
    /// <param name="accentOverride">强调色覆盖(<c>#RRGGBB</c>);空或解析不了时用主题自己的。</param>
    /// <param name="systemPrefersDark">系统当前是否偏好暗色。</param>
    /// <returns>启动画面配色。</returns>
    public static SplashPalette Resolve(string? themeId, string? accentOverride, bool systemPrefersDark) =>
        From(UiThemeCatalog.Resolve(themeId, systemPrefersDark), accentOverride);

    /// <summary>由一套主题与强调色覆盖构造配色。</summary>
    /// <param name="theme">主题。</param>
    /// <param name="accentOverride">强调色覆盖;空或解析不了时用主题自己的。</param>
    /// <returns>启动画面配色。</returns>
    public static SplashPalette From(UiTheme theme, string? accentOverride)
    {
        ArgumentNullException.ThrowIfNull(theme);
        UiThemePalette p = theme.Palette;
        SKColor accent = !string.IsNullOrWhiteSpace(accentOverride) && SKColor.TryParse(accentOverride.Trim(), out SKColor custom)
            ? custom.WithAlpha(255)
            : Parse(p.Accent);
        return new(
            theme.IsDark,
            Parse(p.BgPage),
            Parse(p.BgTerminal),
            Parse(p.BgSurface),
            Parse(p.BorderPrimary),
            Parse(p.BorderSecondary),
            Parse(p.TextPrimary),
            Parse(p.TextSecondary),
            Parse(p.TextTertiary),
            Parse(p.TextMuted),
            accent,
            Parse(p.Success),
            Parse(p.Info));
    }

    private static SKColor Parse(string hex) => SKColor.TryParse(hex, out SKColor color) ? color : SKColors.Gray;
}
