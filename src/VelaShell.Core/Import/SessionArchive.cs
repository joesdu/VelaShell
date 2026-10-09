using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>
/// 连接导出文件(VelaShell JSON,#571)的根对象:分组、连接、被引用的共享凭据与隧道,外加一段可选的加密机密。
/// </summary>
/// <remarks>
/// <para>
/// 连接直接用 <see cref="SessionProfile" /> 本身序列化,而不是另抄一份 DTO:另抄一份就多一处"加字段要记得同步"的地方,
/// 漏了的表现是"导出再导入之后某个设置没了"。代价是新字段会自动进文件 —— 所以
/// <c>SessionArchiveFieldTests</c> 用反射逐个属性检查归类(导出 / 机密 / 本机状态),新加一个没归类的字段就红。
/// </para>
/// <para>
/// 机密(密码、私钥口令、插件机密)<b>永远不以明文出现在文件里</b>:要么不导出,要么整体用导出口令加密成
/// <see cref="Secrets" />。其余部分保持明文,便于对照着看、手改主机名之类的字段后再导回去。
/// </para>
/// </remarks>
public sealed class SessionArchive
{
    /// <summary><see cref="Format" /> 的固定值,导入时据此认出这是 VelaShell 的连接文件。</summary>
    public const string FormatId = "velashell-sessions";

    /// <summary>当前写出的文件版本。导入时比它新的版本一律拒收,不猜着读。</summary>
    public const int CurrentVersion = 1;

    /// <summary>文件格式标识,恒为 <see cref="FormatId" />。</summary>
    public string Format { get; set; } = FormatId;

    /// <summary>文件版本号。</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>导出时间(UTC)。</summary>
    public DateTime ExportedAtUtc { get; set; }

    /// <summary>写出这个文件的程序与版本(如 <c>VelaShell 0.9.0</c>),只用于排查。</summary>
    public string? Application { get; set; }

    /// <summary>导出的连接所属的分组。</summary>
    public List<SessionArchiveGroup> Groups { get; set; } = [];

    /// <summary>导出的连接;密码、私钥口令与插件机密一律已剥离。</summary>
    public List<SessionProfile> Sessions { get; set; } = [];

    /// <summary>被导出连接引用的共享凭据(#550);密码与私钥口令已剥离。没有引用时为 null。</summary>
    public List<SharedCredential>? SharedCredentials { get; set; }

    /// <summary>端口转发隧道:键为所属连接在本文件里的 Id。没有隧道时为 null。</summary>
    public Dictionary<Guid, List<TunnelConfig>>? Tunnels { get; set; }

    /// <summary>用导出口令加密的机密;导出时没选「包含敏感信息」则为 null。</summary>
    public SessionArchiveSecrets? Secrets { get; set; }
}

/// <summary>导出文件里的一个分组(只带显示所需的字段,成员关系以连接上的 <c>groupId</c> 为准)。</summary>
public sealed class SessionArchiveGroup
{
    /// <summary>分组在导出方那台机器上的 Id;导入时同 Id 的分组视为同一个。</summary>
    public Guid Id { get; set; }

    /// <summary>分组名;导入时没有同 Id 的分组就按名称(不区分大小写)合并。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组图标(可选)。</summary>
    public string? Icon { get; set; }

    /// <summary>分组在导出方的排序序号;新建的分组按它的先后接在已有分组之后。</summary>
    public int SortOrder { get; set; }
}

/// <summary>导出文件里加密的那一段机密。</summary>
public sealed class SessionArchiveSecrets
{
    /// <summary>当前使用的算法描述:PBKDF2-SHA256(200k 迭代)派生 AES-256-GCM 密钥,与云同步的端到端加密同一套。</summary>
    public const string CurrentAlgorithm = "pbkdf2-sha256-200000+aes-256-gcm";

    /// <summary>算法描述;导入时不认识的算法一律当作打不开。</summary>
    public string Algorithm { get; set; } = CurrentAlgorithm;

    /// <summary>密文:Base64(salt16 | nonce12 | tag16 | cipher),解开后是 <see cref="SessionArchiveSecretPayload" /> 的 JSON。</summary>
    public string Data { get; set; } = string.Empty;
}

/// <summary>机密段解密后的内容:按连接 / 共享凭据的 Id 存放各自的机密。</summary>
public sealed class SessionArchiveSecretPayload
{
    /// <summary>连接的机密,键为连接在文件里的 Id。</summary>
    public Dictionary<Guid, SessionSecretValues>? Sessions { get; set; }

    /// <summary>共享凭据的机密,键为凭据在文件里的 Id。</summary>
    public Dictionary<Guid, SessionSecretValues>? SharedCredentials { get; set; }
}

/// <summary>一条连接或共享凭据上的全部机密。</summary>
public sealed class SessionSecretValues
{
    /// <summary>登录密码。</summary>
    public string? Password { get; set; }

    /// <summary>私钥口令。</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>插件协议的机密字段(仅连接有)。</summary>
    public Dictionary<string, string>? PluginSecrets { get; set; }

    /// <summary>是否一项机密都没有(空的不写进文件)。</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(Password)
        && string.IsNullOrEmpty(PrivateKeyPassphrase)
        && PluginSecrets is not { Count: > 0 };
}
