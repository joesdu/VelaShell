namespace VelaShell.Core.Models;

/// <summary>端口转发隧道的配置:类型、名称及本地/远端监听与目标地址。</summary>
public sealed class TunnelConfig
{
    /// <summary>转发类型:本地、远程或动态(SOCKS)。</summary>
    public required TunnelType Type { get; init; }

    /// <summary>隧道显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>本地监听主机(通常为 127.0.0.1 或 0.0.0.0)。</summary>
    public required string LocalHost { get; init; }

    /// <summary>本地监听端口。</summary>
    public required uint LocalPort { get; init; }

    /// <summary>转发目标主机;动态转发(SOCKS)无固定目标,允许留空。</summary>
    public string RemoteHost { get; init; } = string.Empty;

    /// <summary>转发目标端口;动态转发(SOCKS)无固定目标,允许为 0。</summary>
    public uint RemotePort { get; init; }
}
