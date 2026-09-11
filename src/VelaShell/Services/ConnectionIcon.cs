using Avalonia;
using Avalonia.Media;
using VelaShell.Core.Models;
using VelaShell.PluginSdk;

namespace VelaShell.Services;

/// <summary>
/// 标签页上要画的那个图标:几何、它的原生视框,以及描边还是填充。
/// </summary>
/// <remarks>
/// 三样必须一起走。只传几何的话,一个视框 1024 的品牌 logo 会被按 24 缩放 ——
/// 放大四十多倍,屏幕上什么也看不见;而把实心图形拿去描边,得到的是它的轮廓线。
/// </remarks>
public sealed class TabIcon
{
    /// <summary>要绘制的路径几何。</summary>
    public required Geometry Geometry { get; init; }

    /// <summary>几何的原生视框边长,默认 24(lucide)。</summary>
    public double ViewBoxSize { get; init; } = 24d;

    /// <summary>非 <c>null</c> 即填充渲染;<c>null</c> 表示描边(宿主图标集的语言)。</summary>
    public IBrush? Fill { get; init; }
}

/// <summary>
/// 标签页上的图标该画什么:宿主内建的三种协议各有字形,插件的由插件自己交出来。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ConnectionAccent" /> 的分工:那个回答「是**哪一台**机器」(按 id 或用户
/// 指定的颜色),这个回答「是**哪一种**连接」。两者都挂在标签上,但答的不是同一个问题 ——
/// 所以颜色不该被协议占用,图标也不该按 id 随机。
/// </para>
/// <para>
/// <b>宿主这张表只认自己内建的三种协议。</b>插件(S3 / Redis / 串口 / AI 面板…)的图标
/// 由插件在 <see cref="PluginIcon" /> 里交出来,宿主**不维护**「插件 id → 图标」的对照表 ——
/// 那种表第三方插件永远进不去。插件没给就退回通用插头。
/// </para>
/// </remarks>
public static class ConnectionIcon
{
    /// <summary>SSH 会话:带框的终端字形。</summary>
    public const string SshKey = "Icon.square-terminal";

    /// <summary>SFTP / FTP:标签内容是双栏文件浏览器,不是终端。</summary>
    public const string FileKey = "Icon.hard-drive";

    /// <summary>插件没有自报图标时的通用兜底。</summary>
    public const string PluginKey = "Icon.plug";

    /// <summary>
    /// 返回该配置对应的**内建**图标资源键。插件协议给的是通用插头 ——
    /// 它们的具体字形不归这张表管,见 <see cref="ForSession" />。
    /// </summary>
    /// <param name="profile">连接配置;<c>null</c> 表示本地终端一类没有配置的标签。</param>
    /// <returns>图标资源键,或 <c>null</c>(本地终端:不画图标)。</returns>
    public static string? ResourceKeyFor(SessionProfile? profile) =>
        profile?.ConnectionType switch
        {
            ConnectionType.SSH => SshKey,
            ConnectionType.SFTP or ConnectionType.FTP => FileKey,
            ConnectionType.Plugin => PluginKey,
            _ => null
        };

    /// <summary>
    /// 会话标签页(终端 / SFTP / 插件工作台)的图标:**插件自报的优先**,
    /// 没有才按连接类型取内建字形。
    /// </summary>
    /// <param name="profile">连接配置。</param>
    /// <param name="pluginIcon">插件描述符里交出来的图标;宿主内建协议传 <c>null</c>。</param>
    /// <returns>要画的图标,或 <c>null</c>(此时标签不画图标)。</returns>
    public static TabIcon? ForSession(SessionProfile? profile, PluginIcon? pluginIcon = null) =>
        FromPlugin(pluginIcon, () => ConnectionAccent.BrushForProfile(profile))
        ?? FromKey(ResourceKeyFor(profile));

    /// <summary>
    /// 插件面板标签页的图标:插件自报的优先,没有就是通用插头。
    /// </summary>
    /// <param name="pluginIcon">插件在 <c>PanelOptions.Icon</c> 里交出来的图标。</param>
    /// <returns>要画的图标,或 <c>null</c>(连通用插头都取不到时,如无头测试)。</returns>
    public static TabIcon? ForPanel(PluginIcon? pluginIcon) =>
        FromPlugin(pluginIcon, static () => ThemeBrushes.Resolve("VelaAccent", Colors.SteelBlue))
        ?? FromKey(PluginKey);

    /// <summary>
    /// 把插件交出来的图标翻译成可绘制的几何。**解析失败当作没给** ——
    /// 一段畸形路径是插件的问题,不该把宿主的标签条顶掉。
    /// </summary>
    /// <param name="icon">插件自报的图标。</param>
    /// <param name="tint">实心图标的填充色;只在真要填充时才求值。</param>
    private static TabIcon? FromPlugin(PluginIcon? icon, Func<IBrush> tint)
    {
        if (icon is null || string.IsNullOrWhiteSpace(icon.PathData))
        {
            return null;
        }
        Geometry geometry;
        try
        {
            geometry = Geometry.Parse(icon.PathData);
        }
        catch (Exception)
        {
            // 这里**刻意**捕获所有异常,与仓库里逐类型列举的写法不同:解析的是插件给的
            // 任意字符串,而 Avalonia 的 PathMarkupParser 抛什么取决于错在哪一位 ——
            // 光是试出来的就有 InvalidDataException(命令字认不出)与 FormatException(数字坏了)。
            // 枚举类型在这里是在猜,而猜漏一个的代价是**整条标签条连带主窗口一起炸**。
            // 纯解析,没有 IO 也没有取消令牌,吞掉不会盖住别的问题。
            return null;
        }
        return new()
        {
            Geometry = geometry,
            // 非正数 / 非有限值按 24 处理:一个写错的视框不该让图标整个消失。
            ViewBoxSize = icon.ViewBoxSize is > 0 and < double.PositiveInfinity ? icon.ViewBoxSize : 24d,
            Fill = icon.IsFilled ? tint() : null
        };
    }

    private static TabIcon? FromKey(string? key) =>
        key is not null && Resolve(key) is { } geometry ? new() { Geometry = geometry } : null;

    private static Geometry? Resolve(string key) =>
        Application.Current is { } app
        && app.Resources.TryGetResource(key, app.ActualThemeVariant, out object? value)
            ? value as Geometry
            : null;
}
