using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>文件里的连接与本机已有连接重复时怎么办。</summary>
public enum SessionImportConflict
{
    /// <summary>跳过,本机那条不动(默认)。</summary>
    Skip,

    /// <summary>用文件里的覆盖本机那条(Id 不变;文件没带机密时保留本机的密码)。</summary>
    Overwrite,

    /// <summary>两条都留:文件里的另存为一条新连接。</summary>
    KeepBoth
}

/// <summary>文件里的一条连接与本机哪条重复、凭什么判的。</summary>
public enum SessionImportMatch
{
    /// <summary>不重复,是一条新连接。</summary>
    None,

    /// <summary>同 Id:就是同一条连接(本机导出的备份、改过的 CSV 导回来)。</summary>
    SameId,

    /// <summary>Id 不同,但协议、主机、端口、用户名都一样:同一台机器上的同一个账号。</summary>
    SameEndpoint
}

/// <summary>导入预览里的一行:文件里的一条连接,和它与本机数据比对的结果。</summary>
public sealed class SessionImportPlanItem
{
    /// <summary>文件里的那一条。</summary>
    public required SessionImportCandidate Candidate { get; init; }

    /// <summary>与之重复的本机连接;不重复时为 null。</summary>
    public SessionProfile? Existing { get; init; }

    /// <summary>重复的判据。</summary>
    public SessionImportMatch Match { get; init; }

    /// <summary>是否与本机已有连接重复。</summary>
    public bool IsDuplicate => Match != SessionImportMatch.None;

    /// <summary>与本机数据比对才看得出来的提示(引用的跳板 / 共享凭据找不到之类)。</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>一份连接文件与本机数据比对后的导入计划。</summary>
public sealed class SessionImportPlan
{
    /// <summary>文件本身。</summary>
    public required SessionImportDocument Document { get; init; }

    /// <summary>逐条比对结果,顺序同 <see cref="SessionImportDocument.Candidates" />。</summary>
    public required IReadOnlyList<SessionImportPlanItem> Items { get; init; }

    /// <summary>比对时本机的全部连接(写入时据此判 Id 冲突、解析引用)。</summary>
    public required IReadOnlyList<SessionProfile> ExistingSessions { get; init; }

    /// <summary>比对时本机的全部分组。</summary>
    public required IReadOnlyList<ServerGroup> ExistingGroups { get; init; }

    /// <summary>比对时本机的全部共享凭据。</summary>
    public required IReadOnlyList<SharedCredential> ExistingCredentials { get; init; }
}

/// <summary>用户在导入对话框里的选择。</summary>
public sealed class SessionImportOptions
{
    /// <summary>重复时怎么办。</summary>
    public SessionImportConflict Conflict { get; init; } = SessionImportConflict.Skip;

    /// <summary>
    /// true = 按文件里的分组放(没有同名分组就新建);false = 全部放进 <see cref="TargetGroupId" />。
    /// </summary>
    public bool UseFileGroups { get; init; } = true;

    /// <summary><see cref="UseFileGroups" /> 为 false 时的目标分组;null = 未分组。</summary>
    public Guid? TargetGroupId { get; init; }

    /// <summary>勾选导入的行(<see cref="SessionImportCandidate.Index" />);null = 全部能导入的行。</summary>
    public IReadOnlyCollection<int>? SelectedIndices { get; init; }
}

/// <summary>一次导入要落盘的全部东西,外加给用户看的统计。由 <see cref="SessionImportPlanner.BuildWrites" /> 算出,调用方照单写库。</summary>
public sealed class SessionImportWriteSet
{
    /// <summary>要保存的分组:新建的,以及成员列表有变的已有分组。</summary>
    public List<ServerGroup> Groups { get; } = [];

    /// <summary>
    /// 要保存的共享凭据:新建的,以及沿用本机那条、但本机缺机密而文件里有(解锁后)的那几条(只补缺的,已有的不动)。
    /// 本机已有同 Id / 同名、机密也齐的不在这里。
    /// </summary>
    public List<SharedCredential> SharedCredentials { get; } = [];

    /// <summary>要保存的连接(新建与覆盖)。</summary>
    public List<SessionProfile> Sessions { get; } = [];

    /// <summary>要写的隧道,键为落盘后的连接 Id。</summary>
    public Dictionary<Guid, List<TunnelConfig>> Tunnels { get; } = [];

    /// <summary>新建的连接数。</summary>
    public int Created { get; internal set; }

    /// <summary>覆盖的连接数。</summary>
    public int Updated { get; internal set; }

    /// <summary>没有写入的行数(重复跳过、没勾选、有错误)。</summary>
    public int Skipped { get; internal set; }

    /// <summary>新建的分组数。</summary>
    public int GroupsCreated { get; internal set; }

    /// <summary>从文件里带进来机密(密码 / 口令)的连接数。</summary>
    public int SecretsImported { get; internal set; }

    /// <summary>写入时做出的妥协(跳板找不到改直连、成环被断开…),已本地化。</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>导入完成后给界面的结果。</summary>
/// <param name="Created">新建的连接数。</param>
/// <param name="Updated">覆盖的连接数。</param>
/// <param name="Skipped">没有写入的行数。</param>
/// <param name="GroupsCreated">新建的分组数。</param>
/// <param name="SecretsImported">带进来机密的连接数。</param>
/// <param name="Warnings">写入时的妥协说明。</param>
public sealed record SessionFileImportOutcome(
    int Created,
    int Updated,
    int Skipped,
    int GroupsCreated,
    int SecretsImported,
    IReadOnlyList<string> Warnings)
{
    /// <summary>实际写入的连接数(新建 + 覆盖)。</summary>
    public int Written => Created + Updated;
}
