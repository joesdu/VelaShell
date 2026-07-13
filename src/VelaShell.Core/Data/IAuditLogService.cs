using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>审计日志(时序数据)。为设置中的“安全审计”页与合规追踪提供存取。</summary>
public interface IAuditLogService
{
    /// <summary>写入一条审计日志条目。</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>按时间倒序查询;<paramref name="category" /> 为空时返回全部类别。</summary>
    Task<List<AuditEntry>> QueryAsync(int limit, string? category = null, CancellationToken cancellationToken = default);
}
