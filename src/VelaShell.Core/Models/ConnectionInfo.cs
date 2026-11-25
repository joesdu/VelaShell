namespace VelaShell.Core.Models;

/// <summary>
/// SSH 连接信息与凭据
/// </summary>
public class ConnectionInfo
{
    /// <summary>
    /// 获取或设置主机名或 IP 地址
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// 获取或设置 SSH 端口(默认:22)
    /// </summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// 获取或设置用户名
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// 获取或设置认证方式
    /// </summary>
    public required AuthMethod AuthMethod { get; init; }

    /// <summary>
    /// 获取或设置密码(用于密码认证)
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// 获取或设置私钥文件路径(用于私钥认证)
    /// </summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// 获取或设置私钥口令(可选)
    /// </summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>
    /// 获取或设置 OpenSSH 用户证书文件路径:证书认证时与 <see cref="PrivateKeyPath" /> 成对使用;
    /// <see cref="AuthMethod.KeyStore" /> 认证时可选,配的是 <see cref="KeyStoreKey" /> 那把钥的证书
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// 系统密钥库里那把钥的引用(用于 <see cref="AuthMethod.KeyStore" /> 认证)
    /// </summary>
    public SystemKeyReference? KeyStoreKey { get; init; }

    /// <summary>
    /// 跳板主机(ProxyJump):先连它,再经它连到本机。递归嵌套即多段跳;
    /// 由工作流按 <c>SessionProfile.JumpHostProfileId</c> 链解析(带环检测)。null = 直连。
    /// 具体建链方式由 Infrastructure 决定(当前为 VelaShell.Ssh 的嵌套连接 + direct-tcpip 隧道)。
    /// </summary>
    public ConnectionInfo? JumpHost { get; init; }

    /// <summary>
    /// 本次连接的保活心跳间隔(秒,0 = 关闭);null = 跟随全局设置。
    /// </summary>
    /// <remarks>
    /// 来自 <c>SessionProfile.Terminal.KeepAliveSeconds</c>(F-06)。放在这里而不是让
    /// Infrastructure 再去查一次配置:建链时才知道这一跳是哪条配置,而跳板链上每一跳
    /// 都可以有自己的设置 —— 客户端工厂那一层手里只有一个 <see cref="ConnectionInfo" />。
    /// </remarks>
    public int? KeepAliveSeconds { get; init; }

    /// <summary>
    /// 这一跳的 SSH 可选能力(压缩、agent 转发、X11 转发);null = 全关。
    /// </summary>
    /// <remarks>
    /// 压缩只按终点 SSH 配置协商;同一传输上的通道共享协商结果。两个转发只对<b>终点</b>那一跳的交互式
    /// shell 生效 —— 跳板只负责开隧道,不开 shell,也就没有请求转发的通道。
    /// </remarks>
    public SshSessionOptions? Ssh { get; init; }

    /// <summary>
    /// 这个会话的字符集名(会话覆盖优先于全局,见 <c>SessionTerminalSettings.Encoding</c>);SFTP 按它解读远端文件名。
    /// null = UTF-8。
    /// </summary>
    /// <remarks>
    /// <para>
    /// SFTP v3 没规定文件名编码,SSH 库默认按 UTF-8、解不开的字节无损往返 —— GBK 服务器上的中文名打得开、删得掉,
    /// 只是显示成替换字符。连 GBK 服务器的人终端编码早就配成了 GBK,文件名多半也是,于是复用这一个值。
    /// </para>
    /// <para>
    /// 放在这里而不是让 Infrastructure 再查一次配置,理由同 <see cref="KeepAliveSeconds" />:开 SFTP 通道的那一层手里只有会话。
    /// 只在终点那一跳上有值(跳板只开隧道、不开 SFTP)。值是 UTF-8(或认不出的名字)时 Infrastructure <b>不</b>把它交给库,
    /// 让库走默认的无损往返 —— 库眼下把显式的 UTF-8 也认作默认,但宿主不该把正确性押在这个实现细节上。
    /// </para>
    /// </remarks>
    public string? FileNameEncoding { get; init; }

    /// <summary>
    /// 这次连接是断线后的<b>自动重连</b>(没有人在等它):主机指纹要人裁决时直接拒绝,不弹窗。
    /// </summary>
    /// <remarks>
    /// 自动重连在后台发生 —— 睡眠唤醒后所有标签一起重连、隧道按退避一直重试,用户多半不在屏幕前。
    /// 这时弹出「指纹变了 / 没见过,信不信任」,要么叠出一摞没人点的模态框,要么让重连一直挂着;
    /// 而一个没人看着就被点掉的框,本身就违背了让人核对指纹的初衷。被拒之后由宿主停掉自动重连,等用户手动重连再问。
    /// 只管主机指纹:登录凭据、动态码这类提示照旧(它们各有自己的取消处理)。跳板链上每一跳都按它办 —— 一条链共用一个策略。
    /// </remarks>
    public bool IsAutomaticReconnect { get; init; }

    /// <summary>
    /// 连接诊断的协议轨迹:非 null 时这条连接(连同跳板链上每一跳)挂上 SSH 库的报文旁路,把每个报文的元信息记进去。
    /// <see langword="null" />(平常的连接)不挂旁路。
    /// </summary>
    /// <remarks>
    /// 只看最外层这一份:跳板那几跳由装配那一层用同一份去记,每条记录标着是哪一跳。
    /// 记的只有方向、类型、长度、序号、通道号,不要载荷(见 <see cref="Ssh.SshPacketTrace" />)。
    /// </remarks>
    public Ssh.SshPacketTrace? PacketTrace { get; init; }

    /// <summary>
    /// 这次连接是<b>没人看着的探测</b>(会话健康巡检):要人回答的一律不问 —— 主机指纹、动态码、Tailscale 复核,
    /// 照拒绝 / 失败报回去,由结果窗口告诉用户哪几台要他亲自连一次。
    /// </summary>
    /// <remarks>
    /// 比 <see cref="IsAutomaticReconnect" /> 更严:那边只管主机指纹,动态码框照旧(断线重连多半是用户自己的会话,值得问);
    /// 巡检一次几十台,每台弹一个框就是一摞没人点的模态框。主机指纹被拦时的说法也不同 —— 没有「停止自动重连」这回事,
    /// 第一次见到的主机也不记安全告警(用户点名要查的机器,没见过不算异常;指纹<b>变了</b>照记)。
    /// 设置里关掉了「首次连接确认指纹」时照旧按 TOFU 记下,与手动连接同一个口径。
    /// </remarks>
    public bool IsUnattended { get; init; }
}
