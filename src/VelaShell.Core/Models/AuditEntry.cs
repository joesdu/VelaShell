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
}
