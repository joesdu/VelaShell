using Avalonia;
using Avalonia.Media;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 会话标签页上的协议图标:一眼分清这条标签是终端、文件面板,还是插件的工作台。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ConnectionAccent" /> 的分工:那个回答「是**哪一台**机器」(按 id 或用户
/// 指定的颜色),这个回答「是**哪一种**连接」。两者都挂在标签上,但答的不是同一个问题 ——
/// 所以颜色不该被协议占用,图标也不该按 id 随机。
/// </para>
/// <para>
/// <b>这张表只认宿主内建的三种协议。</b>插件协议(S3 / Redis / 串口 / Telnet…)一律落到
/// <see cref="PluginKey" /> 这个通用图标 —— 宿主不认识具体是哪个插件,也**不应该**认识。
/// 让 Redis 显示 Redis 的标、串口显示接口的标,正确的路子是插件在自己的描述符里交出图标,
/// 而不是在宿主里维护一张「插件 id → 图标」的表(那种表第三方插件永远进不去)。
/// 该字段尚未进 SDK,进度见 <c>feature-plan.md</c>。
/// </para>
/// </remarks>
public static class ConnectionIcon
{
    /// <summary>SSH 会话:带框的终端字形。</summary>
    public const string SshKey = "Icon.square-terminal";

    /// <summary>SFTP / FTP:标签内容是双栏文件浏览器,不是终端。</summary>
    public const string FileKey = "Icon.hard-drive";

    /// <summary>插件协议的通用图标(插件自报图标之前的兜底)。</summary>
    public const string PluginKey = "Icon.plug";

    /// <summary>
    /// 返回该配置对应的图标资源键;本地终端(无配置)返回 <c>null</c>,表示不画图标。
    /// </summary>
    /// <param name="profile">连接配置;<c>null</c> 表示本地终端一类没有配置的标签。</param>
    /// <returns>图标资源键,或 <c>null</c>。</returns>
    public static string? ResourceKeyFor(SessionProfile? profile) =>
        profile?.ConnectionType switch
        {
            ConnectionType.SSH => SshKey,
            ConnectionType.SFTP or ConnectionType.FTP => FileKey,
            ConnectionType.Plugin => PluginKey,
            _ => null
        };

    /// <summary>返回该配置对应的图标几何;取不到资源(无头测试、设计器)时返回 <c>null</c>。</summary>
    /// <param name="profile">连接配置。</param>
    /// <returns>图标几何,或 <c>null</c>(此时标签不画图标)。</returns>
    public static Geometry? ForProfile(SessionProfile? profile) =>
        ResourceKeyFor(profile) is { } key ? Resolve(key) : null;

    private static Geometry? Resolve(string key) =>
        Application.Current is { } app
        && app.Resources.TryGetResource(key, app.ActualThemeVariant, out object? value)
            ? value as Geometry
            : null;
}
