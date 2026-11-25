using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>支持一次性读取资源管理器所需分组与会话数据的仓储扩展。</summary>
public interface ISessionTreeDataRepository
{
    /// <summary>在一次存储访问中获取全部分组与会话配置。</summary>
    Task<(List<ServerGroup> Groups, List<SessionProfile> Sessions)> GetAllTreeDataAsync();

    /// <summary>优先使用批量读取,兼容只提供分组与会话查询的仓储。</summary>
    static async Task<(List<ServerGroup> Groups, List<SessionProfile> Sessions)> ReadAsync(ISessionRepository repository)
    {
        if (repository is ISessionTreeDataRepository batch)
        {
            return await batch.GetAllTreeDataAsync().ConfigureAwait(false);
        }
        Task<List<ServerGroup>> groups = repository.GetAllGroupsAsync();
        Task<List<SessionProfile>> sessions = repository.GetAllSessionsAsync();
        await Task.WhenAll(groups, sessions).ConfigureAwait(false);
        return (await groups.ConfigureAwait(false), await sessions.ConfigureAwait(false));
    }
}
