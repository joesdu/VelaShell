using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>连接历史(时序数据):记录每次连接并支撑侧边栏“最近连接”列表。</summary>
public interface IRecentConnectionService
{
    /// <summary>记录一次连接(成功或失败均记录,查询时按需过滤)。</summary>
    Task RecordAsync(RecentConnectionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按时间倒序返回最近成功连接,同一目标(配置 Id 或 user@host:port)只保留最新一条。
    /// </summary>
    Task<List<RecentConnectionEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>清空连接历史。</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
