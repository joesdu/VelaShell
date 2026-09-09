namespace VelaShell.Core.Ssh;

/// <summary>本机 SSH agent 的探测结果。</summary>
/// <param name="IsAvailable">是否连得上。</param>
/// <param name="Endpoint">实际使用的端点(命名管道名或套接字路径);探不到时为空串。</param>
/// <param name="Source">端点是怎么来的(用户配置 / <c>SSH_AUTH_SOCK</c> / 平台默认)。</param>
/// <param name="Identities">agent 当前持有的密钥;连不上时为空表。</param>
/// <param name="Error">连不上的原因(给用户看的一句话);连得上时为 <see langword="null" />。</param>
public sealed record SshAgentProbe(
    bool IsAvailable,
    string Endpoint,
    SshAgentEndpointSource Source,
    IReadOnlyList<SshAgentIdentity> Identities,
    string? Error);

/// <summary>本机 agent 端点的来源,决定了探测失败时该怎么向用户解释。</summary>
public enum SshAgentEndpointSource
{
    /// <summary>没有可用端点。</summary>
    None,

    /// <summary>设置 → 密钥管理里用户显式填的端点。</summary>
    UserConfigured,

    /// <summary>环境变量 <c>SSH_AUTH_SOCK</c>。</summary>
    Environment,

    /// <summary>平台默认(Windows 上是 OpenSSH 的命名管道)。</summary>
    PlatformDefault
}

/// <summary>
/// 本机 SSH agent 客户端:把「连上本机 agent」这件事从平台差异里抽出来。
/// <para>
/// Windows 与类 Unix 上 agent 的 IPC 形态完全不同(命名管道 vs unix 域套接字),
/// 而 <c>SSH_AUTH_SOCK</c> 在 Windows 上还常常指向 msys / WSL / Git Bash 的**伪套接字** ——
/// 那不是 AF_UNIX,谁去连都连不上。把这些差异挡在这一层之后,agent 转发的数据面
/// 只需要面对一条普通的 <see cref="Stream" />。
/// </para>
/// </summary>
public interface ISshAgentClient
{
    /// <summary>
    /// 探测本机 agent:连一次、列一次身份、立刻断开。
    /// <para>不抛异常 —— 「没有 agent」是常态而非故障,原因放在 <see cref="SshAgentProbe.Error" /> 里。</para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结果。</returns>
    Task<SshAgentProbe> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 开一条到本机 agent 的双工流。调用方负责释放。
    /// <para>agent 协议是「一问一答」且**无多路复用**,因此每条转发连接都要单独开一条。</para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌(只作用于建立阶段)。</param>
    /// <returns>与本机 agent 之间的双工字节流。</returns>
    /// <exception cref="InvalidOperationException">本机没有可用的 agent 端点。</exception>
    Task<Stream> ConnectAsync(CancellationToken cancellationToken = default);
}
