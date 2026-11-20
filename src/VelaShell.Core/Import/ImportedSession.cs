using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>从外部工具(Xshell、WinSCP 等)解析出的一条可导入会话,携带导入预览所需的元数据与状态。</summary>
public sealed class ImportedSession
{
    /// <summary>会话显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>来源分组下的目录名，从父到子。每一段都是字面名称；没有目录元数据时直接放在来源分组。</summary>
    public IReadOnlyList<string> GroupPath { get; init; } = [];

    /// <summary>目标主机地址(主机名或 IP)。</summary>
    public required string Host { get; init; }

    /// <summary>连接端口。</summary>
    public int Port { get; init; } = 22;

    /// <summary>登录用户名;来源未填写时为空串。</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>映射后的连接协议类型(支持 SSH / SFTP / FTP / S3)。</summary>
    public ConnectionType ConnectionType { get; init; } = ConnectionType.SSH;

    /// <summary>原文件中的协议字段原值(如 SSH、SFTP、SCP、FTP、S3),用于预览展示与不支持判定。</summary>
    public string Protocol { get; init; } = "SSH";

    /// <summary>该协议是否可被 VelaShell 导入(SSH / SFTP / FTP / S3 支持;WebDAV 等仍不支持)。</summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>来源是否包含非空的加密密码字段。</summary>
    public bool HasEncryptedPassword { get; init; }

    /// <summary>成功解密并通过校验的明文密码;未启用密码、解密失败或校验不过时为 <c>null</c>。</summary>
    public string? Password { get; init; }

    /// <summary>是否成功还原出密码明文(即 <see cref="Password" /> 非空)。</summary>
    public bool PasswordRecovered => Password is { Length: > 0 };

    /// <summary>
    /// 私钥文件路径(来自 OpenSSH config 的 <c>IdentityFile</c> 等);非空即以私钥认证方式导入。
    /// </summary>
    /// <remarks>
    /// 路径不校验是否存在:密钥可能还没从另一台机器拷过来,把路径原样带进配置里
    /// 比悄悄丢掉更有用 —— 用户在连接对话框里一眼就能看见并修正。
    /// </remarks>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// OpenSSH 用户证书路径(来自 <c>CertificateFile</c>,或 <c>IdentityFile</c> 旁边存在的 <c>-cert.pub</c>);
    /// 非空即以证书认证导入,私钥仍是 <see cref="PrivateKeyPath" />。同样不校验文件是否存在。
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// 来源明确给出的认证方式(证书、SSH Agent);<see langword="null" /> 时由写入器按
    /// 「有私钥就私钥认证,否则密码」推断 —— 那是各来源原来的口径。
    /// </summary>
    public AuthMethod? AuthMethod { get; init; }

    /// <summary>SSH 协议层的选项(压缩、agent 转发、算法清单);来源没给任何一项时为 <see langword="null" />。</summary>
    public SshSessionOptions? Ssh { get; init; }

    /// <summary>随会话一起导入的端口转发(落成这条连接的隧道,停止状态);没有时为 <see langword="null" />。</summary>
    public IReadOnlyList<TunnelConfig>? Tunnels { get; init; }

    /// <summary>
    /// 导入时的说明(界面语言的一句话一条):转发落成了几条隧道,以及来源里写了、却没能照原样导入的设置。
    /// 预览行里列给用户看 —— 映射不了的如实说出来,而不是悄悄丢掉、让用户连上之后才发现少了一条转发。
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// 跳板主机的**别名**(来自 <c>ProxyJump</c>);由写入器在同一批导入的会话里按
    /// <see cref="Name" /> 解析成 <c>SessionProfile.JumpHostProfileId</c>。
    /// </summary>
    /// <remarks>
    /// 存别名而不是直接存 id:扫描阶段跳板那条会话还没落盘,根本没有 id;
    /// 而两条会话是否一起被勾选,要到用户点「导入」那一刻才知道。
    /// </remarks>
    public string? JumpHostAlias { get; init; }

    /// <summary>VelaShell 中是否已存在同主机/端口/用户名的会话(用于提示重复)。</summary>
    public bool AlreadyExists { get; init; }

    /// <summary>来源标识(文件路径或注册表键),用于诊断。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// FTP / FTPS 的协议专属设置(加密方式等);仅当 <see cref="ConnectionType" /> 为
    /// <see cref="ConnectionType.FTP" /> 时非空。
    /// </summary>
    public FtpSettings? FtpSettings { get; init; }

    /// <summary>
    /// 插件协议 id;仅当 <see cref="ConnectionType" /> 为
    /// <see cref="ConnectionType.Plugin" /> 时非空。
    /// <para>
    /// 导入器天生就认识外部工具的格式,因此由它把「WinSCP 的 S3 会话」映射到
    /// <c>velashell.s3</c> 这个协议 id;插件没装也照常导入 —— 配置留着,
    /// 装上插件即可用,这比拒绝导入更符合用户预期。
    /// </para>
    /// </summary>
    public string? PluginProtocolId { get; init; }

    /// <summary>插件协议的非机密设置(端点、区域等);仅插件协议非空。</summary>
    public Dictionary<string, string>? PluginSettings { get; init; }
}
