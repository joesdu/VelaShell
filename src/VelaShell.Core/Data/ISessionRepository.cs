using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>
/// 会话与分组的持久化仓储接口,提供服务器分组与会话配置的读取、保存与删除操作。
/// </summary>
public interface ISessionRepository
{
    /// <summary>
    /// 异步获取全部服务器分组。
    /// </summary>
    Task<List<ServerGroup>> GetAllGroupsAsync();

    /// <summary>
    /// 异步获取全部会话配置。
    /// </summary>
    Task<List<SessionProfile>> GetAllSessionsAsync();

    /// <summary>
    /// 异步按标识获取单个会话配置,未找到时返回 <c>null</c>。
    /// </summary>
    Task<SessionProfile?> GetSessionAsync(Guid id);

    /// <summary>
    /// 异步保存会话配置(新增或更新)。
    /// </summary>
    Task SaveSessionAsync(SessionProfile session);

    /// <summary>
    /// 异步按标识删除会话配置。
    /// </summary>
    Task DeleteSessionAsync(Guid id);

    /// <summary>
    /// 异步保存服务器分组(新增或更新)。
    /// </summary>
    Task SaveGroupAsync(ServerGroup group);

    /// <summary>
    /// 异步按标识删除服务器分组。
    /// </summary>
    Task DeleteGroupAsync(Guid id);
}
