namespace VelaShell.Core.Models;

/// <summary>审计日志条目,写入 SonnetDB 时序 measurement <c>audit_log</c>。</summary>
public class AuditEntry
{
    /// <summary>事件发生时间(UTC),作为时序数据的时间戳。</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 类别,如 connection / profile / settings / security。
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>动作,如 connect / disconnect / save / delete / trust-host。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>相关会话配置 Id(可空)。</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>人类可读的详情。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// 改动从哪来(<see cref="AuditSources" />):配置类记录(连接配置、设置、共享凭据)才有,
    /// 写入那一刻取 <see cref="AuditSourceScope.Current" />;连接与安全两类、以及加这一栏之前的旧记录为 null。
    /// </summary>
    public string? Source { get; set; }
}

/// <summary>审计日志的类别(<see cref="AuditEntry.Category" />)。</summary>
public static class AuditCategories
{
    /// <summary>连接成败。</summary>
    public const string Connection = "connection";

    /// <summary>安全事件:主机指纹的裁决、外部拉起。</summary>
    public const string Security = "security";

    /// <summary>连接配置的新建、修改、删除。</summary>
    public const string Profile = "profile";

    /// <summary>设置变更(只记哪几类变了,不记值)。</summary>
    public const string Settings = "settings";

    /// <summary>共享凭据的新建、修改、删除。</summary>
    public const string Credential = "credential";
}

/// <summary>配置类审计记录的动作(<see cref="AuditEntry.Action" />)。连接与安全两类的动作仍按原样写在各自的调用处。</summary>
public static class AuditActions
{
    /// <summary>新建了一条连接配置。</summary>
    public const string ProfileCreated = "profile-created";

    /// <summary>改了一条连接配置。</summary>
    public const string ProfileUpdated = "profile-updated";

    /// <summary>删了一条连接配置。</summary>
    public const string ProfileDeleted = "profile-deleted";

    /// <summary>删了一个分组,组里还剩的配置被移回未分组(一次删除一条汇总,详情带分组名与条数)。</summary>
    public const string ProfilesUngrouped = "profiles-ungrouped";

    /// <summary>设置变了(详情是变了的分类)。</summary>
    public const string SettingsChanged = "settings-changed";

    /// <summary>新建了一条共享凭据。</summary>
    public const string CredentialCreated = "credential-created";

    /// <summary>改了一条共享凭据(详情带着引用它的连接数)。</summary>
    public const string CredentialUpdated = "credential-updated";

    /// <summary>删了一条共享凭据。</summary>
    public const string CredentialDeleted = "credential-deleted";

    /// <summary>开启了主密码(详情只记有没有恢复码,不记任何口令材料)。</summary>
    public const string MasterPasswordEnabled = "master-password-enabled";

    /// <summary>关闭了主密码。</summary>
    public const string MasterPasswordDisabled = "master-password-disabled";

    /// <summary>修改了主密码(含用恢复码解开后设新主密码)。</summary>
    public const string MasterPasswordChanged = "master-password-changed";

    /// <summary>忘记主密码、另起新密钥(详情是改名保留的文件名)。</summary>
    public const string MasterPasswordReset = "master-password-reset";
}
