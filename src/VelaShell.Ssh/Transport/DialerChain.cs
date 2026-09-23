// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1;velashell-docs/zh/ssh/design/architecture.md §5.1

using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Transport;

/// <summary>内置拨号器的入口：直连、SOCKS5、HTTP 代理、跳板、代理命令。</summary>
/// <remarks>
/// <para>
/// <b>嵌套靠 <c>Via</c></b>：每个代理类拨号器都有一个「怎么到达代理本身」的内层拨号器，
/// 默认直连 TCP。<c>DialerChain.Socks5("proxy", 1080).Via(DialerChain.HttpConnect("corp", 3128))</c>
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

    /// <summary>经 SOCKS5 代理（RFC 1928）。</summary>
    /// <param name="host">代理主机。</param>
    /// <param name="port">代理端口。</param>
    /// <param name="credentials">代理凭据（RFC 1929）；<see langword="null"/> 表示不认证。</param>
    public static Socks5Dialer Socks5(string host, int port, SshProxyCredentials? credentials = null) =>
        new(new SshEndPoint(host, port)) { Credentials = credentials };

    /// <summary>经 HTTP 代理的 <c>CONNECT</c> 方法（RFC 9110 §9.3.6）。</summary>
    /// <param name="host">代理主机。</param>
    /// <param name="port">代理端口。</param>
    /// <param name="credentials">代理凭据（Basic）；<see langword="null"/> 表示不认证。</param>
    public static HttpConnectDialer HttpConnect(string host, int port, SshProxyCredentials? credentials = null) =>
        new(new SshEndPoint(host, port)) { Credentials = credentials };

    /// <summary>经跳板主机（<c>ProxyJump</c> / <c>ssh -J</c>）。</summary>
    /// <param name="jumpHost">跳板的连接参数。</param>
    public static SshJumpDialer Jump(SshConnectionOptions jumpHost) => new(jumpHost);

    /// <summary>
    /// 依次经过若干跳板：<paramref name="jumpHosts"/>[0] 最近，最后一个直接连目标
    /// （对应 <c>ProxyJump a,b,c</c>）。
    /// </summary>
    /// <remarks>
    /// 第一个跳板用它自己的 <see cref="SshConnectionOptions.Dialer"/>；
    /// 其后每个跳板的拨号器被换成「经前一个跳板」。
    /// </remarks>
    public static SshJumpDialer Jumps(params IReadOnlyList<SshConnectionOptions> jumpHosts)
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
    public static ProxyCommandDialer Command(string commandTemplate) => new(commandTemplate);
}
