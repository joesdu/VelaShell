using VelaShell.Core.Models;

namespace VelaShell.Core.Sync;

/// <summary>
/// 云同步的本机配置与状态(SonnetDB app_config/sync 文档)。永不进入同步载荷:
/// 令牌与口令经 ISecretProtector 机器绑定加密,换机不可解。
/// </summary>
public class SyncSettings
{
    public bool Enabled { get; set; }

    /// <summary>承载同步数据的 secret Gist Id;空 = 首次推送时自动创建。</summary>
    public string GistId { get; set; } = "";

    /// <summary>本设备名(写入载荷元数据,便于辨认版本来自哪台设备)。</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>自动同步:启动时拉取 + 设置保存后防抖推送。</summary>
    public bool AutoSync { get; set; } = true;

    // 同步范围(用户确认:只同步配置类数据 —— 应用设置、会话连接、代码片段)
    public bool SyncAppSettings { get; set; } = true;

    public bool SyncProfiles { get; set; } = true;

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
    public int SchemaVersion { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }

    public string DeviceName { get; set; } = "";

    /// <summary>应用设置(推送前剔除设备本地字段;拉取时同样保留本机值)。</summary>
    public AppSettings? Settings { get; set; }

    public List<ServerGroup>? Groups { get; set; }

    /// <summary>连接配置;未启用端到端口令时密码与私钥口令被剥离。</summary>
    public List<SessionProfile>? Profiles { get; set; }

    /// <summary>端口转发隧道配置(随连接配置同步):键 = 所属连接的 profileId。</summary>
    public Dictionary<Guid, List<TunnelConfig>>? Tunnels { get; set; }

    public QuickCommandData? Snippets { get; set; }
}

/// <summary>
/// Gist 文件中的信封:明文载荷或端到端加密后的密文二选一。
/// 加密格式:Base64(salt16 | nonce12 | tag16 | cipher),PBKDF2-SHA256(200k) + AES-256-GCM。
/// </summary>
public class SyncEnvelope
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }

    public string DeviceName { get; set; } = "";

    public bool Encrypted { get; set; }

    public string? CipherText { get; set; }

    public SyncPayload? Payload { get; set; }
}

/// <summary>
/// Gist 修订历史条目(版本管理直接复用 Gist 原生 revision)。
/// <paramref name="DeviceName" /> 来自该版本载荷信封的元数据(需逐版本读取,可能为 null)。
/// </summary>
public sealed record GistRevision(string Version, DateTime CommittedAtUtc, int Additions, int Deletions, string? DeviceName = null);

public enum SyncAction
{
    None,
    UpToDate,
    Pushed,
    Pulled,
    Failed
}

public sealed record SyncResult(SyncAction Action, bool Success, string Message)
{
    public static SyncResult Fail(string message) => new(SyncAction.Failed, false, message);
}
