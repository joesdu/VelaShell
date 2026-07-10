namespace VelaShell.Core.Ssh;

/// <summary>端口转发类型(对应 SSH 的 direct-tcpip / forwarded-tcpip / SOCKS 动态转发)。</summary>
public enum PortForwardKind
{
    /// <summary>本地转发:监听在本机 Bound 端,流量送往远端 Target。</summary>
    Local,

    /// <summary>远程转发:监听在服务器 Bound 端,流量送回本机侧 Target。</summary>
    Remote,

    /// <summary>动态转发(SOCKS 代理):仅使用 Bound 端,无固定 Target。</summary>
    Dynamic
}

/// <summary>
/// 库中立的端口转发请求:Bound = 监听端(Local 在本机、Remote 在服务器),
/// Target = 目的端;Dynamic 转发不需要 Target。
/// </summary>
public sealed record PortForwardRequest(
    PortForwardKind Kind,
    string BoundHost,
    uint BoundPort,
    string? TargetHost = null,
    uint? TargetPort = null);

/// <summary>
/// 一条运行中的端口转发的句柄,由 <see cref="ISshClientWrapper.StartPortForward" /> 返回。
/// 创建即启动;<see cref="Stop" />/Dispose 停止监听并从客户端摘除(幂等,底层已随
/// 连接失效时静默成功)。转发通道内的错误(如目标拒绝连接)不会停掉监听端口,
/// 经 <see cref="ChannelError" /> 上报供界面展示。
/// </summary>
public interface IPortForwardHandle : IDisposable
{
    bool IsStarted { get; }

    /// <summary>转发通道错误(每个经过的连接失败时触发,监听端口本身仍在)。</summary>
    event Action<Exception>? ChannelError;

    void Stop();
}
