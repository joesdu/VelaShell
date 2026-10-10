namespace VelaShell.Core.Models;

/// <summary>
/// 启动画面样式(<see cref="AppearanceOptions.SplashStyle" /> 的取值)。
/// </summary>
/// <remarks>
/// 五套样式共用同一份进度来源(启动打点)与同一套主题令牌,只是画法不同;
/// 设计稿与取舍见 DESIGN.md 的「Splash Screen」一节。
/// </remarks>
public static class SplashStyles
{
    /// <summary>经典:大 Logo + 产品名,底部一排滑动的小圆点。出厂默认。</summary>
    public const string Classic = "classic";

    /// <summary>终端开机:迷你终端里逐行打出真实的启动阶段与耗时。</summary>
    public const string Terminal = "terminal";

    /// <summary>船帆座:启动每推进一段就点亮一颗主星、连上一段星线。</summary>
    public const string Constellation = "constellation";

    /// <summary>提示符:Logo 里的 <c>&gt;_</c> 把产品名敲出来,下方五格进度。</summary>
    public const string Prompt = "prompt";

    /// <summary>看板娘:Q 版形象 + 气泡里的当前阶段,底部猫爪印进度。</summary>
    public const string Mascot = "mascot";

    /// <summary>不显示启动画面。</summary>
    public const string None = "none";

    /// <summary>全部取值,按设置页下拉的顺序(出厂默认在前,「不显示」置末)。</summary>
    public static IReadOnlyList<string> All { get; } = [Classic, Terminal, Constellation, Prompt, Mascot, None];

    /// <summary>
    /// 把一个可能来自磁盘的值规整成合法取值:认不出来(空、手改、更新版本写入的新样式)一律回到 <see cref="Classic" />。
    /// </summary>
    /// <param name="value">待规整的值。</param>
    /// <returns>合法的样式取值。</returns>
    public static string Normalize(string? value)
    {
        string trimmed = value?.Trim() ?? "";
        foreach (string style in All)
        {
            if (string.Equals(style, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return style;
            }
        }
        return Classic;
    }
}
