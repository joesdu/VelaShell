using System.Net;
using System.Net.Sockets;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// 「不用代理执行 DNS 查找」(<see cref="Core.Net.ProxyRoute.ProxyDns" /> 关)时的本机解析:
/// 先在本机把目标解析成 IP,再请代理去连这个 IP。SSH 的代理拨号与 FTP 的代理客户端共用。
/// </summary>
public static class LocalDnsResolver
{
    /// <summary>目标已是 IP 字面量则原样返回;否则解析并优先取 IPv4。</summary>
    public static async Task<string> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        IPAddress? pick = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        return pick?.ToString()
            ?? throw new IOException(Strings.Format("Msg_ProxyConnectFailed", $"DNS lookup for '{host}' returned no addresses"));
    }
}
