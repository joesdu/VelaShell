namespace VelaShell.Core.Ssh;

/// <summary>远端向本机 agent 发起的一次请求(已经过策略闸门)。</summary>
/// <param name="MessageType">agent 协议消息号(见 <see cref="SshAgentProtocol" />)。</param>
/// <param name="Allowed">是否被放行;<see langword="false" /> 表示就地回了 FAILURE,本机 agent 未被惊动。</param>
/// <param name="Fingerprint">签名请求所用公钥的指纹;非签名请求为 <see langword="null" />。</param>
public sealed record AgentForwardRequestEvent(byte MessageType, bool Allowed, string? Fingerprint);

/// <summary>
/// 一条已启动的 SSH agent 转发。释放即停止转发并尽力清掉远端的套接字。
/// <para>
/// 与端口转发的句柄(<see cref="IPortForwardHandle" />)同构:都是「启动即返回、生命周期由句柄负责」,
/// 都带计数。计数在这里尤其要紧 —— agent 转发是安全敏感能力,「这条会话上远端到底动了几次你的钥匙」
/// 必须能当场答得上来。
/// </para>
/// </summary>
public interface IAgentForwardHandle : IAsyncDisposable
{
    /// <summary>远端上供对端 <c>ssh</c> 连接的套接字绝对路径,即要写进 <c>SSH_AUTH_SOCK</c> 的值。</summary>
    string RemoteSocketPath { get; }

    /// <summary>转发是否仍在运行。</summary>
    bool IsRunning { get; }

    /// <summary>远端累计发起的 agent 连接数。</summary>
    int TotalConnections { get; }

    /// <summary>累计放行的签名请求数。</summary>
    int SignRequests { get; }

    /// <summary>被策略闸门就地拒绝的请求数(改钥匙、清空、上锁一类)。</summary>
    int DeniedRequests { get; }

    /// <summary>
    /// 每处理一条 agent 请求触发一次。在 I/O 线程上调用,订阅方应快速返回
    /// (审计写库要自己切到后台,别把转发的搬运循环堵住)。
    /// </summary>
    event Action<AgentForwardRequestEvent>? RequestHandled;
}
