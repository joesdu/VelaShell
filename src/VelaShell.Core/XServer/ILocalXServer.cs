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
/// <param name="Connector">
/// 内置 X 服务端给的本机连接器:调一次得到一条直接接进服务端的双工流,SSH 的 x11 通道不必再去连本机端口。
/// 第一个参数是这条连接的来历(比如 <c>user@host:22</c>),服务端记进日志与客户端清单,说得出是哪个会话的程序。
/// <see langword="null" /> = 按 <paramref name="Display" /> 走套接字(VcXsrv 等外部 X 服务端)。
/// </param>
public sealed record XServerDisplayResolution(
    string? Display, string? Error = null, Func<string?, CancellationToken, ValueTask<Stream>>? Connector = null)
{
    /// <summary>不接管。</summary>
    public static XServerDisplayResolution None { get; } = new(Display: null);
}

/// <summary>标题栏 X Server 浮层里的一行:一个连着的 X 程序(<see cref="ILocalXServer.GetClientsAsync" />)。</summary>
/// <param name="Key">断开它用的键(<see cref="ILocalXServer.DisconnectClient" />);只在这一次运行之内有效,停了再开的服务端不认。</param>
/// <param name="Id">服务端给它的编号(X 协议里资源 ID 的高位),给人对照日志用。</param>
/// <param name="Name">程序名:它第一个窗口的 <c>WM_CLASS</c>;没有窗口、或窗口没写 <c>WM_CLASS</c> 时为空串。</param>
/// <param name="Title">它第一个窗口的标题;没有为空串。</param>
/// <param name="Source">从哪来:经 SSH 会话转发来的是连接的来历(<c>user@host:22</c>);本机直接连进来的为 <see langword="null" />。</param>
/// <param name="Windows">映射着的顶层窗口数。</param>
/// <param name="MemoryBytes">记在它账上的内存,字节(像素图、窗口缓冲、字形……)。</param>
/// <param name="Retained">已经以 Retain 模式断开,只剩资源还留着。</param>
/// <param name="HoldsServerGrab">正抓着整个 X 服务端(GrabServer):别的程序都在等它。</param>
public sealed record XServerClient(
    string Key, int Id, string Name, string Title, string? Source, int Windows, long MemoryBytes, bool Retained, bool HoldsServerGrab);

/// <summary>一个 X 程序抓着整个服务端太久,别的 X 程序都在等它(<see cref="ILocalXServer.ServerGrabStalled" />)。</summary>
/// <param name="ClientKey">断开它用的键(<see cref="ILocalXServer.DisconnectClient" />)。</param>
/// <param name="Id">服务端给它的编号(见 <see cref="XServerClient.Id" />)。</param>
/// <param name="Name">程序名(见 <see cref="XServerClient.Name" />,退到窗口标题;都没有为空串)。</param>
/// <param name="Source">从哪来(见 <see cref="XServerClient.Source" />)。</param>
/// <param name="Held">已经抓了多久。</param>
public sealed record XServerGrabStallNotice(string ClientKey, int Id, string Name, string? Source, TimeSpan Held);

/// <summary>
/// 由 VelaShell 管理的本机 X 服务端(标题栏的 X Server 按钮、设置 → X Server)。
/// </summary>
/// <remarks>
/// <para>
/// 两种实现,由设置里的引擎决定:内置的 X 服务端(进程内,<c>VelaShell.XServer</c>),或 Windows 上用户装好的
/// VcXsrv 的<b>进程管理者</b>(找到可执行文件、挑一个空闲的显示号、按设置拼命令行拉起来、等它开始监听,
/// 退出时把它关掉)。都只管自己启动的那一个 —— 用户在外面另开的 X 服务端不碰。
/// </para>
/// <para>
/// <see cref="StateChanged" /> 可能在任意线程上触发,界面侧自己切回 UI 线程。
/// </para>
/// </remarks>
public interface ILocalXServer
{
    /// <summary>当前平台是否支持(内置引擎各平台都支持;VcXsrv 只在 Windows 上)。</summary>
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
    /// 本机已经有别的 X 服务端在用(Windows 上当前用户会话里的进程在 6000 端口上听 —— X410、用户手开的 VcXsrv;别的会话的不算,
    /// 终端服务器上那是别的用户的;其它平台上设了
    /// <c>DISPLAY</c>)时不自动启动,返回 <see cref="XServerDisplayResolution.None" /> —— 用户已经有一个在用的显示,
    /// 再开一个只会让窗口出现在意料之外的地方。VcXsrv 引擎找不到可执行文件时同样静默不接管:
    /// 没装 VcXsrv 的人不该在每次连接时收到一条提示。内置引擎在运行时一并给出
    /// <see cref="XServerDisplayResolution.Connector" />。
    /// </remarks>
    Task<XServerDisplayResolution> ResolveForwardingDisplayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 此刻连着的 X 程序(客户端连接)有几个 —— 停掉之前告诉用户「会断开 N 个程序」。只有内置引擎数得出来;
    /// 外部 X 服务端(VcXsrv)、没在运行时为 0。
    /// </summary>
    Task<int> CountConnectedClientsAsync() => Task.FromResult(0);

    /// <summary>
    /// 列得出连着的 X 程序、能逐个断开(<see cref="GetClientsAsync" /> / <see cref="DisconnectClient" /> / <see cref="BreakGrabs" /> 有效)。
    /// 只有内置引擎能;外部 X 服务端(VcXsrv)为 <see langword="false" />,标题栏按钮照旧一点开、一点关。
    /// </summary>
    bool CanManageClients => false;

    /// <summary>
    /// 连着的 X 程序,连同以 Retain 模式断开、资源还留着的(标题栏 X Server 浮层:「谁连着、来自哪个会话、占多少内存」)。
    /// 没在运行、或 <see cref="CanManageClients" /> 为假时为空。
    /// </summary>
    Task<IReadOnlyList<XServerClient>> GetClientsAsync() => Task.FromResult<IReadOnlyList<XServerClient>>([]);

    /// <summary>
    /// 断开这个 X 程序(KillClient 语义:连接断开,它的窗口全部关闭;只剩资源的,资源一并销毁)。键过期(服务端已经停了又开)时什么也不做。
    /// </summary>
    /// <param name="key"><see cref="XServerClient.Key" />。</param>
    void DisconnectClient(string key)
    {
    }

    /// <summary>
    /// 卡住时的恢复手段:解除所有 X 程序的鼠标 / 键盘抓取与冻结、放开 GrabServer。远端菜单开着时 SSH 断网、远端程序挂住时,
    /// 所有 X 窗口点不动、打不了字 —— 用这个,不必停掉整个 X Server。
    /// </summary>
    void BreakGrabs()
    {
    }

    /// <summary>一个 X 程序抓着整个服务端太久,别的 X 程序都在等它(可能在任意线程上触发)。默认实现从不触发。</summary>
    event EventHandler<XServerGrabStallNotice>? ServerGrabStalled
    {
        add
        {
        }
        remove
        {
        }
    }
}
