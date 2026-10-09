using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>连接文件的格式。</summary>
public enum SessionFileFormat
{
    /// <summary>VelaShell 自己的 JSON 导出文件:完整保真,可带加密的机密。</summary>
    Json,

    /// <summary>CSV 表格:给 Excel 批量编辑用,只覆盖常用字段,不带机密(导入时可以有密码列)。</summary>
    Csv
}

/// <summary>
/// CSV 里出现了哪些列。覆盖已有连接时只改文件里有的列 —— 没有的列不动,
/// 否则「导出 CSV → Excel 改个用户名 → 导回去」会把终端设置、插件设置这些 CSV 表达不了的东西全冲掉。
/// </summary>
[Flags]
public enum SessionCsvColumns
{
    /// <summary>一列都没有。</summary>
    None = 0,

    /// <summary><c>id</c>:连接 Id,导回时据此精确对上原来那条。</summary>
    Id = 1 << 0,

    /// <summary><c>name</c>:显示名称。</summary>
    Name = 1 << 1,

    /// <summary><c>group</c>:分组名。</summary>
    Group = 1 << 2,

    /// <summary><c>protocol</c>:协议。</summary>
    Protocol = 1 << 3,

    /// <summary><c>host</c>:主机(必有)。</summary>
    Host = 1 << 4,

    /// <summary><c>port</c>:端口。</summary>
    Port = 1 << 5,

    /// <summary><c>username</c>:用户名。</summary>
    Username = 1 << 6,

    /// <summary><c>auth</c>:认证方式。</summary>
    Auth = 1 << 7,

    /// <summary><c>password</c>:密码(导出时恒为空,导入时可填)。</summary>
    Password = 1 << 8,

    /// <summary><c>private_key</c>:私钥文件路径。</summary>
    PrivateKey = 1 << 9,

    /// <summary><c>certificate</c>:证书文件路径。</summary>
    Certificate = 1 << 10,

    /// <summary><c>credential</c>:共享凭据的名称。</summary>
    Credential = 1 << 11,

    /// <summary><c>jump_host</c>:跳板机(另一条连接的名称)。</summary>
    JumpHost = 1 << 12,

    /// <summary><c>tags</c>:标签,分号分隔。</summary>
    Tags = 1 << 13,

    /// <summary><c>notes</c>:备注。</summary>
    Notes = 1 << 14,

    /// <summary>JSON 导入:整条连接都由文件给出。</summary>
    All = Id | Name | Group | Protocol | Host | Port | Username | Auth | Password | PrivateKey | Certificate
          | Credential | JumpHost | Tags | Notes
}

/// <summary>文件里的一条连接(解析后、尚未与本机数据比对)。</summary>
public sealed class SessionImportCandidate
{
    /// <summary>在文件里的先后序号(从 0 起),界面与导入都按它排。</summary>
    public required int Index { get; init; }

    /// <summary>CSV 的行号(从 1 起,含表头那一行);JSON 为 null。</summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// 文件描述的连接。JSON:<see cref="SessionProfile.Id" /> 是文件里的 Id;CSV:有 <c>id</c> 列且合法时是那个 Id,否则是一个新 Id。
    /// </summary>
    public required SessionProfile Profile { get; init; }

    /// <summary>文件是否给出了这条连接的 Id(没给的不参与按 Id 查重,也不会去占用文件里的 Id)。</summary>
    public bool HasFileId { get; init; }

    /// <summary>JSON:连接在文件里所属分组的 Id;CSV 为 null。</summary>
    public Guid? FileGroupId { get; init; }

    /// <summary>
    /// 分组名:JSON 是文件分组的名称;CSV 是 <c>group</c> 列的值。null = 没有这一列 / 不属于任何分组。
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>CSV:<c>jump_host</c> 列的原值(连接名称或 Id);JSON 用 <see cref="SessionProfile.JumpHostProfileId" />。</summary>
    public string? JumpHostReference { get; init; }

    /// <summary>CSV:<c>credential</c> 列的原值(共享凭据的名称或 Id);JSON 用 <see cref="SessionProfile.CredentialSource" />。</summary>
    public string? CredentialReference { get; init; }

    /// <summary>这条连接由文件给出了哪些字段(JSON 恒为 <see cref="SessionCsvColumns.All" />)。</summary>
    public SessionCsvColumns Columns { get; init; } = SessionCsvColumns.All;

    /// <summary>致命问题(主机为空、端口非法…):有任何一条,这一行就不能导入。</summary>
    public List<string> Errors { get; } = [];

    /// <summary>提示性问题(Id 无效被忽略…):照常导入,只是告诉用户。</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>这一行能否导入(没有致命问题)。</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>文件是否给了这条连接任何机密(CSV 的密码列,或 JSON 解锁后的机密)。</summary>
    public bool HasSecret =>
        !string.IsNullOrEmpty(Profile.Password)
        || !string.IsNullOrEmpty(Profile.PrivateKeyPassphrase)
        || Profile.PluginSecrets is { Count: > 0 };
}

/// <summary>解析好的一份连接文件。</summary>
public sealed class SessionImportDocument
{
    /// <summary>文件名(只用于显示)。</summary>
    public required string FileName { get; init; }

    /// <summary>文件格式。</summary>
    public required SessionFileFormat Format { get; init; }

    /// <summary>文件里的全部连接,按文件先后排列(含有错误、不能导入的行)。</summary>
    public required IReadOnlyList<SessionImportCandidate> Candidates { get; init; }

    /// <summary>JSON:文件里的分组。</summary>
    public IReadOnlyList<SessionArchiveGroup> Groups { get; init; } = [];

    /// <summary>JSON:文件里的共享凭据(解锁后带上机密)。</summary>
    public IReadOnlyList<SharedCredential> SharedCredentials { get; init; } = [];

    /// <summary>JSON:隧道,键为连接在文件里的 Id。</summary>
    public IReadOnlyDictionary<Guid, List<TunnelConfig>> Tunnels { get; init; } = new Dictionary<Guid, List<TunnelConfig>>();

    /// <summary>JSON:加密的机密段;没有则为 null。</summary>
    public SessionArchiveSecrets? EncryptedSecrets { get; init; }

    /// <summary>文件是否带着加密的机密(要口令才能解开)。</summary>
    public bool HasEncryptedSecrets => EncryptedSecrets is not null;

    /// <summary>加密的机密是否已经用口令解开并填回了各条连接与凭据。</summary>
    public bool SecretsUnlocked { get; internal set; }

    /// <summary>JSON:文件的导出时间。</summary>
    public DateTime? ExportedAtUtc { get; init; }

    /// <summary>整份文件层面的提示(忽略了不认识的列之类),不针对某一行。</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>连接文件读不了(不是 VelaShell 的文件、版本太新、缺必需的列…)。<see cref="Exception.Message" /> 已本地化,可直接给用户看。</summary>
public sealed class SessionFileFormatException : Exception
{
    /// <summary>以一条已本地化的说明构造。</summary>
    /// <param name="message">给用户看的说明。</param>
    public SessionFileFormatException(string message) : base(message)
    {
    }

    /// <summary>以一条已本地化的说明与内部异常构造。</summary>
    /// <param name="message">给用户看的说明。</param>
    /// <param name="innerException">导致读不了的异常。</param>
    public SessionFileFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>无参构造(序列化与分析器要求)。</summary>
    public SessionFileFormatException()
    {
    }
}
