namespace VelaShell.Core.XServer;

/// <summary>本机 X 服务端的运行状态。</summary>
public enum XServerState
{
    /// <summary>没在运行。</summary>
    Stopped,

    /// <summary>进程已拉起,正在等它开始监听。</summary>
    Starting,

    /// <summary>在运行,可以接受 X 客户端。</summary>
    Running
}

/// <summary>一次启动的结果。</summary>
/// <param name="Success">是否已在运行(包括调用前就已在运行)。</param>
/// <param name="Error">失败原因(已本地化,可直接给人看);成功时为 <see langword="null" />。</param>
public sealed record XServerStartResult(bool Success, string? Error = null)
{
    /// <summary>成功。</summary>
    public static XServerStartResult Ok { get; } = new(true);

    /// <summary>失败,附原因。</summary>
    public static XServerStartResult Fail(string error) => new(false, error);
}

/// <summary>为 SSH X11 转发解析本机显示的结果。</summary>
/// <param name="Display">
/// 应该转发到的显示(形如 <c>localhost:0.0</c>);<see langword="null" /> = 本服务不接管,
/// 按老规矩取 <c>DISPLAY</c> 与默认值。
/// </param>
/// <param name="Error">自动启动失败的原因(已本地化);没有尝试启动或启动成功时为 <see langword="null" />。</param>
public sealed record XServerDisplayResolution(string? Display, string? Error = null)
{
    /// <summary>不接管。</summary>
    public static XServerDisplayResolution None { get; } = new(Display: null);
}

/// <summary>
/// 由 VelaShell 管理的本机 X 服务端(标题栏的 X Server 按钮、设置 → X Server)。
/// </summary>
/// <remarks>
/// <para>
/// <b>不是 X 服务端的实现</b>,是一个已安装的 X 服务端(Windows 上是 VcXsrv)的<b>进程管理者</b>:
/// 找到可执行文件、挑一个空闲的显示号、按设置拼命令行拉起来、等它开始监听,退出时把它关掉。
/// 只管自己拉起的那一个进程 —— 用户在外面另开的 X 服务端不碰。
/// </para>
/// <para>
/// <see cref="StateChanged" /> 可能在任意线程上触发,界面侧自己切回 UI 线程。
/// </para>
/// </remarks>
public interface ILocalXServer
{
    /// <summary>当前平台是否支持(目前只有 Windows;Linux 桌面自带 X / XWayland,macOS 用 XQuartz)。</summary>
    bool IsSupported { get; }

    /// <summary>当前状态。</summary>
    XServerState State { get; }

    /// <summary>运行中时的显示号;没在运行时为 <see langword="null" />。</summary>
    int? DisplayNumber { get; }

    /// <summary>运行中时给 X 客户端用的显示地址(<c>localhost:N.0</c>);没在运行时为 <see langword="null" />。</summary>
    string? Display { get; }

    /// <summary>状态变化(含进程自己退出)。</summary>
    event EventHandler? StateChanged;

    /// <summary>找 X 服务端的可执行文件:先认配置的路径,再查常见安装位置与 PATH。</summary>
    /// <param name="configuredPath">设置里填的路径;留空走自动查找。</param>
    /// <returns>找到的完整路径;找不到为 <see langword="null" />。</returns>
    string? FindExecutable(string? configuredPath);

    /// <summary>按当前设置启动;已在运行时直接返回成功。</summary>
    Task<XServerStartResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停掉由本服务拉起的进程;没在运行时什么都不做。</summary>
    Task StopAsync();

    /// <summary>
    /// SSH 会话要开 X11 转发、而配置里没写显示地址时调用:在运行就给出它的显示;
    /// 没在运行且设置允许自动启动时先启动。
    /// </summary>
    /// <remarks>
    /// 本机 6000 端口上已经有别的 X 服务端(X410、用户手开的 VcXsrv)时不自动启动,
    /// 返回 <see cref="XServerDisplayResolution.None" /> —— 用户已经有一个在用的显示,
    /// 再开一个只会让窗口出现在意料之外的地方。找不到可执行文件时同样静默不接管:
    /// 没装 VcXsrv 的人不该在每次连接时收到一条提示。
    /// </remarks>
    Task<XServerDisplayResolution> ResolveForwardingDisplayAsync(CancellationToken cancellationToken = default);
}
