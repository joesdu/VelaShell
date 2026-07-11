using System.Collections.Concurrent;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// “仅本次信任”的指纹缓存:进程内存,不落盘,应用重启即失效。
/// 同一次运行内,被临时信任的 host:port+指纹 对终端重连和 SFTP 通道同样放行,
/// 避免一次会话里反复弹确认框。
/// </summary>
public static class HostTrustOnceCache
{
    private static readonly ConcurrentDictionary<string, string> Trusted = new();

    public static void Remember(string host, int port, string fingerprint) =>
        Trusted[Key(host, port)] = fingerprint;

    public static bool IsTrusted(string host, int port, string fingerprint) =>
        Trusted.TryGetValue(Key(host, port), out string? cached) && cached == fingerprint;

    private static string Key(string host, int port) => $"{host}:{port}";
}
