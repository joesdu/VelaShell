using VelaShell.Core.Models;

namespace VelaShell.Core.Sync;

/// <summary>
/// 云同步的本机配置与状态(SonnetDB app_config/sync 文档)。永不进入同步载荷:
/// 令牌与口令经 ISecretProtector 机器绑定加密,换机不可解。
/// </summary>
public class SyncSettings
{
    /// <summary>是否启用云同步功能。</summary>
    public bool Enabled { get; set; }

    /// <summary>承载同步数据的 secret Gist Id;空 = 首次推送时自动创建。</summary>
    public string GistId { get; set; } = "";

    /// <summary>本设备名(写入载荷元数据,便于辨认版本来自哪台设备)。</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>自动同步:启动时拉取 + 设置保存后防抖推送。</summary>
    public bool AutoSync { get; set; } = true;

    // 同步范围(用户确认:只同步配置类数据 —— 应用设置、会话连接、代码片段)
    /// <summary>同步范围:是否同步应用设置。</summary>
    public bool SyncAppSettings { get; set; } = true;

    /// <summary>同步范围:是否同步会话连接配置(分组与 Profile)。</summary>
    public bool SyncProfiles { get; set; } = true;

    /// <summary>同步范围:是否同步快捷命令/代码片段。</summary>
    public bool SyncSnippets { get; set; } = true;

    /// <summary>GitHub PAT(gist 权限),ISecretProtector 加密存储。</summary>
    public string? ProtectedToken { get; set; }

    /// <summary>端到端加密口令(可选),ISecretProtector 加密存储;设置后载荷整体 AES-GCM 加密。</summary>
    public string? ProtectedPassphrase { get; set; }

    // ———— 同步状态(智能方向判定的依据) ————

    /// <summary>上次成功同步(推送或拉取)的时间。</summary>
    public DateTime? LastSyncAtUtc { get; set; }

    /// <summary>本地自上次同步后的最近一次改动时间;null = 无未同步改动。</summary>
    public DateTime? LastLocalChangeAtUtc { get; set; }

    /// <summary>上次同步时远端的 Gist revision;用于检测远端是否有新版本。</summary>
    public string? LastRemoteVersion { get; set; }
}

/// <summary>同步载荷:各端共享的用户数据快照(存放于 Gist 单文件)。</summary>
public class SyncPayload
{
    /// <summary>载荷架构版本号,用于跨版本兼容判定。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>本快照的生成时间(UTC)。</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>生成本快照的设备名。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>应用设置(推送前剔除设备本地字段;拉取时同样保留本机值)。</summary>
    public AppSettings? Settings { get; set; }

    /// <summary>会话连接分组列表。</summary>
    public List<ServerGroup>? Groups { get; set; }

    /// <summary>连接配置;未启用端到端口令时密码与私钥口令被剥离。</summary>
    public List<SessionProfile>? Profiles { get; set; }

    /// <summary>端口转发隧道配置(随连接配置同步):键 = 所属连接的 profileId。</summary>
    public Dictionary<Guid, List<TunnelConfig>>? Tunnels { get; set; }

    /// <summary>快捷命令/代码片段数据。</summary>
    public QuickCommandData? Snippets { get; set; }
}

/// <summary>
/// Gist 文件中的信封:明文载荷或端到端加密后的密文二选一。
/// 加密格式:Base64(salt16 | nonce12 | tag16 | cipher),PBKDF2-SHA256(200k) + AES-256-GCM。
/// </summary>
public class SyncEnvelope
{
    /// <summary>信封架构版本号,用于跨版本兼容判定。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>载荷生成时间(UTC)。</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>生成本载荷的设备名。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>载荷是否经端到端加密;true 时使用 <see cref="CipherText" />,否则使用 <see cref="Payload" />。</summary>
    public bool Encrypted { get; set; }

    /// <summary>加密后的密文(Base64:salt16 | nonce12 | tag16 | cipher);仅在 <see cref="Encrypted" /> 为 true 时存在。</summary>
    public string? CipherText { get; set; }

    /// <summary>明文载荷;仅在未加密时存在。</summary>
    public SyncPayload? Payload { get; set; }
}

/// <summary>
/// Gist 修订历史条目(版本管理直接复用 Gist 原生 revision)。
/// <paramref name="DeviceName" /> 来自该版本载荷信封的元数据(需逐版本读取,可能为 null)。
/// </summary>
/// <param name="Version">Gist 版本(revision)标识。</param>
/// <param name="CommittedAtUtc">该版本的提交时间(UTC)。</param>
/// <param name="Additions">相对上一版本的新增行数。</param>
/// <param name="Deletions">相对上一版本的删除行数。</param>
/// <param name="DeviceName">生成该版本的设备名;需逐版本读取载荷信封,可能为 null。</param>
public sealed record GistRevision(string Version, DateTime CommittedAtUtc, int Additions, int Deletions, string? DeviceName = null);

/// <summary>一次同步操作的结果动作类型。</summary>
public enum SyncAction
{
    /// <summary>未执行任何操作。</summary>
    None,

    /// <summary>本地与远端一致,无需同步。</summary>
    UpToDate,

    /// <summary>已将本地数据推送到远端。</summary>
    Pushed,

    /// <summary>已从远端拉取数据到本地。</summary>
    Pulled,

    /// <summary>同步失败。</summary>
    Failed
}

/// <summary>一次同步操作的结果。</summary>
/// <param name="Action">本次同步执行的动作类型。</param>
/// <param name="Success">操作是否成功。</param>
/// <param name="Message">面向用户的结果描述信息。</param>
public sealed record SyncResult(SyncAction Action, bool Success, string Message)
{
    /// <summary>构造一个表示失败的同步结果。</summary>
    /// <param name="message">失败原因描述。</param>
    /// <returns>Action 为 <see cref="SyncAction.Failed" />、Success 为 false 的结果。</returns>
    public static SyncResult Fail(string message) => new(SyncAction.Failed, false, message);
}
