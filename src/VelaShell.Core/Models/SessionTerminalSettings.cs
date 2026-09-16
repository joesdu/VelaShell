namespace VelaShell.Core.Models;

/// <summary>
/// 把「全局终端设置」与「本条配置的覆盖项」合成一份生效值。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖的规则只有一条:<see cref="TerminalOverrides" /> 里那一项不为空就用它,否则用全局。
/// 简单到几乎不值得单开一个类型 —— 但调用点有五六处(建标签、握手、重连、插件终端、
/// 设置热更新),各写一遍 <c>profile.Terminal?.X ?? settings.Y</c> 的结果必然是某一处漏了,
/// 表现为"覆盖在新建标签时生效、重连之后又变回全局",而那种 bug 没人会往这里想。
/// </para>
/// <para>
/// 一律接受 <c>profile</c> 为 null:本地终端、插件借用的终端视图都没有会话配置,
/// 那时整套覆盖自然不适用。
/// </para>
/// </remarks>
public static class SessionTerminalSettings
{
    /// <summary>生效的 <c>TERM</c> 名。</summary>
    /// <param name="profile">会话配置;null 表示没有配置(本地终端等)。</param>
    /// <param name="settings">全局设置。</param>
    /// <returns>会话覆盖值,或全局值。</returns>
    public static string TerminalType(SessionProfile? profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Pick(profile?.Terminal?.TerminalType, settings.TerminalType);
    }

    /// <summary>
    /// 生效的会话字符集名:解码远端输出与编码用户键入共用这一套。
    /// </summary>
    /// <remarks>
    /// 两个方向必须同一套 —— 远端的行编辑(readline / zle)按 <c>LANG</c> 的字符集数「字符」,
    /// 送进去的字节若不是那套编码,退格会删半个字、光标移动错位,而不只是显示乱码。
    /// </remarks>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <param name="settings">全局设置。</param>
    /// <returns>会话覆盖值,或全局值;两者都空时为 <c>UTF-8</c>。</returns>
    public static string Encoding(SessionProfile? profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string resolved = Pick(profile?.Terminal?.Encoding, settings.TerminalEncoding);
        // 全局那一项本身也可能是空(旧配置、手改坏的文件),兜到 UTF-8 而不是让编码解析抛。
        return string.IsNullOrWhiteSpace(resolved) ? "UTF-8" : resolved;
    }

    /// <summary>生效的保活心跳间隔(秒)。</summary>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <param name="settings">全局设置。</param>
    /// <returns>会话覆盖值,或全局值。</returns>
    public static int KeepAliveSeconds(SessionProfile? profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return profile?.Terminal?.KeepAliveSeconds ?? settings.General.KeepAliveSeconds;
    }

    /// <summary>
    /// 生效的防空闲注入间隔(秒);0 = 关闭。
    /// </summary>
    /// <remarks>
    /// 与其它几项不同,这一项<b>只按会话走</b>,没有全局值可回落 —— 理由见
    /// <see cref="TerminalOverrides.AntiIdleSeconds" />:注入的字节是打进对端 tty 的,
    /// 该不该冒这个风险,答案在"这台机器会不会踢人",而不在一个全局开关上。
    /// </remarks>
    /// <param name="profile">会话配置;null 表示没有配置(本地终端等)。</param>
    /// <returns>会话设定值,或 0(关闭)。</returns>
    public static int AntiIdleSeconds(SessionProfile? profile) =>
        Math.Max(0, profile?.Terminal?.AntiIdleSeconds ?? 0);

    /// <summary>
    /// 用户指定的标签强调色(<c>#RRGGBB</c>);null = 按配置 id 自动配色。
    /// </summary>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <returns>十六进制色值,或 null。</returns>
    public static string? TabColor(SessionProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.Terminal?.TabColor) ? null : profile.Terminal.TabColor;

    /// <summary>登录后自动切换到的目录;null 或空 = 不切换。</summary>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <returns>目录路径,或 null。</returns>
    public static string? StartupDirectory(SessionProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.Terminal?.StartupDirectory)
            ? null
            : profile.Terminal.StartupDirectory.Trim();

    private static string Pick(string? overrideValue, string globalValue) =>
        string.IsNullOrWhiteSpace(overrideValue) ? globalValue : overrideValue.Trim();
}
