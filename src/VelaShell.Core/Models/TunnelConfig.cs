namespace VelaShell.Core.Models;

public sealed class TunnelConfig
{
    public required TunnelType Type { get; init; }

    public required string Name { get; init; }

    public required string LocalHost { get; init; }

    public required uint LocalPort { get; init; }

    /// <summary>转发目标主机;动态转发(SOCKS)无固定目标,允许留空。</summary>
    public string RemoteHost { get; init; } = string.Empty;

    /// <summary>转发目标端口;动态转发(SOCKS)无固定目标,允许为 0。</summary>
    public uint RemotePort { get; init; }
}
