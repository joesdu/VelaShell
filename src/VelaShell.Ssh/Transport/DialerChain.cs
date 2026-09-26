// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1;velashell-docs/zh/ssh/design/architecture.md §5.1

using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Transport;

/// <summary>内置拨号器的入口：直连、SOCKS5、HTTP 代理、跳板、代理命令。</summary>
/// <remarks>
/// <para>
/// 这里是拿到内置拨号器的<b>唯一</b>入口，返回的都是 <see cref="ISshTransportDialer"/> ——
/// 具体实现是 <c>internal</c> 的，要自己的拨号方式就实现那个接口。
/// </para>
/// <para>
/// <b>代理的嵌套靠 <c>via</c></b>：每个代理都有一个「怎么到达代理本身」的内层拨号器，
/// 默认直连 TCP。<c>DialerChain.Socks5("proxy", 1080, via: DialerChain.HttpConnect("corp", 3128))</c>
/// 读作「经 HTTP 代理 corp 到达 SOCKS5 代理 proxy，再由它连目标」。
/// </para>
/// <para>
/// 跳板的嵌套写在跳板自己的连接参数里（<see cref="SshConnectionOptions.Dialer"/>）——
/// 跳板本来就是一条有自己拨号方式的完整连接。
/// </para>
/// </remarks>
public static class DialerChain
{
    /// <summary>直连 TCP。</summary>
    public static ISshTransportDialer Tcp => TcpTransportDialer.Shared;

    /// <summary>经 SOCKS5 代理（RFC 1928）。目标主机名交给代理去解析。</summary>
    /// <param name="host">代理主机。</param>
    /// <param name="port">代理端口。</param>
    /// <param name="credentials">代理凭据（RFC 1929）；<see langword="null"/> 表示不认证。</param>
    /// <param name="via">怎么到达代理本身；<see langword="null"/> 表示直连。</param>
    public static ISshTransportDialer Socks5(
        string host, int port, SshProxyCredentials? credentials = null, ISshTransportDialer? via = null) =>
        new Socks5Dialer(new SshEndPoint(host, port)) { Credentials = credentials, Inner = via ?? Tcp };

    /// <summary>经 HTTP 代理的 <c>CONNECT</c> 方法（RFC 9110 §9.3.6）。目标主机名交给代理去解析。</summary>
    /// <param name="host">代理主机。</param>
    /// <param name="port">代理端口。</param>
    /// <param name="credentials">代理凭据（Basic）；<see langword="null"/> 表示不认证。</param>
    /// <param name="via">怎么到达代理本身；<see langword="null"/> 表示直连。</param>
    public static ISshTransportDialer HttpConnect(
        string host, int port, SshProxyCredentials? credentials = null, ISshTransportDialer? via = null) =>
        new HttpConnectDialer(new SshEndPoint(host, port)) { Credentials = credentials, Inner = via ?? Tcp };

    /// <summary>经跳板主机（<c>ProxyJump</c> / <c>ssh -J</c>）。</summary>
    /// <param name="jumpHost">跳板的连接参数：它自己的凭据、主机密钥策略，以及它自己的拨号器。</param>
    public static ISshTransportDialer Jump(SshConnectionOptions jumpHost) => new SshJumpDialer(jumpHost);

    /// <summary>经跳板主机，跳板连接由调用方自己建。</summary>
    /// <param name="jumpHost">跳板的地址（进诊断信息与逐跳记录）。</param>
    /// <param name="connect">
    /// 建立到跳板的连接。<b>每次拨号调一次</b> —— 断线重连时跳板也要重连；
    /// 返回的连接归拨出来的流所有，流释放时一并断开。
    /// </param>
    /// <remarks>
    /// 给「每一跳都要现准备凭据」的调用方用（先连 ssh-agent、弹口令框之类）。
    /// 能直接给出连接参数的，用 <see cref="Jump(SshConnectionOptions)"/> ——
    /// 那样外层连接的计时器会传进跳板的建连里。
    /// </remarks>
    public static ISshTransportDialer Jump(
        SshEndPoint jumpHost, Func<CancellationToken, ValueTask<SshConnection>> connect) =>
        new SshJumpDialer(jumpHost, connect);

    /// <summary>
    /// 依次经过若干跳板：<paramref name="jumpHosts"/>[0] 最近，最后一个直接连目标
    /// （对应 <c>ProxyJump a,b,c</c>）。
    /// </summary>
    /// <remarks>
    /// 第一个跳板用它自己的 <see cref="SshConnectionOptions.Dialer"/>；
    /// 其后每个跳板的拨号器被换成「经前一个跳板」。
    /// </remarks>
    public static ISshTransportDialer Jumps(params IReadOnlyList<SshConnectionOptions> jumpHosts)
    {
        ArgumentNullException.ThrowIfNull(jumpHosts);
        if (jumpHosts.Count == 0)
        {
            throw new ArgumentException("至少要有一个跳板。", nameof(jumpHosts));
        }

        SshJumpDialer dialer = new(jumpHosts[0]);
        for (int i = 1; i < jumpHosts.Count; i++)
        {
            dialer = new SshJumpDialer(jumpHosts[i] with { Dialer = dialer });
        }
        return dialer;
    }

    /// <summary>经一个外部程序（<c>ProxyCommand</c>）。</summary>
    /// <param name="commandTemplate">命令行模板，支持 <c>%h %p %r %n %%</c>。</param>
    /// <param name="userName"><c>%r</c> 替换成什么（目标的登录用户名）。</param>
    /// <param name="originalHost"><c>%n</c> 替换成什么（用户原本输入的主机名）；缺省用目标主机。</param>
    public static ISshTransportDialer Command(
        string commandTemplate, string? userName = null, string? originalHost = null) =>
        new ProxyCommandDialer(commandTemplate) { UserName = userName, OriginalHost = originalHost };
}
