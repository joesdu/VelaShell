using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>
/// 基于 JSON 文件持久化会话配置与分组的仓储实现,读写通过信号量串行化以保证并发安全。
/// </summary>
public class SessionRepository : ISessionRepository
{
    private readonly string _dataPath;
    private readonly JsonDataStore _dataStore;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>
    /// 创建仓储实例。未指定 <paramref name="dataPath" /> 时默认存放到用户目录下的 .velashell/sessions.json。
    /// </summary>
    /// <param name="dataStore">底层的 JSON 读写存储。</param>
    /// <param name="dataPath">可选的数据文件路径;为空时使用默认路径。</param>
    public SessionRepository(JsonDataStore dataStore, string? dataPath = null)
    {
        _dataStore = dataStore;
        if (string.IsNullOrEmpty(dataPath))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _dataPath = Path.Combine(userProfile, ".velashell", "sessions.json");
        }
        else
        {
            _dataPath = dataPath;
        }
    }

    /// <summary>获取全部服务器分组。</summary>
    /// <returns>已保存的分组列表。</returns>
    public async Task<List<ServerGroup>> GetAllGroupsAsync()
    {
        SessionData data = await LoadDataAsync().ConfigureAwait(false);
        return data.Groups;
    }

    /// <summary>获取全部会话配置。</summary>
    /// <returns>已保存的会话列表。</returns>
    public async Task<List<SessionProfile>> GetAllSessionsAsync()
    {
        SessionData data = await LoadDataAsync().ConfigureAwait(false);
        return data.Sessions;
    }

    /// <summary>按标识获取单个会话配置。</summary>
    /// <param name="id">会话唯一标识。</param>
    /// <returns>匹配的会话;不存在时返回 null。</returns>
    public async Task<SessionProfile?> GetSessionAsync(Guid id)
    {
        SessionData data = await LoadDataAsync().ConfigureAwait(false);
        return data.Sessions.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>保存会话配置:已存在则覆盖,否则新增,并持久化到磁盘。</summary>
    /// <param name="session">待保存的会话。</param>
    public async Task SaveSessionAsync(SessionProfile session)
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SessionData data = await LoadDataAsync().ConfigureAwait(false);
            int existingIndex = data.Sessions.FindIndex(s => s.Id == session.Id);
            if (existingIndex >= 0)
            {
                data.Sessions[existingIndex] = session;
            }
            else
            {
                data.Sessions.Add(session);
            }
            await _dataStore.SaveAsync(_dataPath, data).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>删除指定会话,并从所有分组的会话引用中一并移除。</summary>
    /// <param name="id">待删除会话的唯一标识。</param>
    public async Task DeleteSessionAsync(Guid id)
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SessionData data = await LoadDataAsync().ConfigureAwait(false);
            data.Sessions.RemoveAll(s => s.Id == id);
            foreach (ServerGroup group in data.Groups)
            {
                group.Sessions.Remove(id);
            }
            await _dataStore.SaveAsync(_dataPath, data).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>保存服务器分组:已存在则覆盖,否则新增,并持久化到磁盘。</summary>
    /// <param name="group">待保存的分组。</param>
    public async Task SaveGroupAsync(ServerGroup group)
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SessionData data = await LoadDataAsync().ConfigureAwait(false);
            int existingIndex = data.Groups.FindIndex(g => g.Id == group.Id);
            if (existingIndex >= 0)
            {
                data.Groups[existingIndex] = group;
            }
            else
            {
                data.Groups.Add(group);
            }
            await _dataStore.SaveAsync(_dataPath, data).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>删除指定分组,并将原属该分组的会话的分组归属清空。</summary>
    /// <param name="id">待删除分组的唯一标识。</param>
    public async Task DeleteGroupAsync(Guid id)
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SessionData data = await LoadDataAsync().ConfigureAwait(false);
            data.Groups.RemoveAll(g => g.Id == id);
            foreach (SessionProfile session in data.Sessions.Where(s => s.GroupId == id))
            {
                session.GroupId = null;
            }
            await _dataStore.SaveAsync(_dataPath, data).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<SessionData> LoadDataAsync() => await _dataStore.LoadAsync<SessionData>(_dataPath).ConfigureAwait(false) ?? new SessionData();
}

internal class SessionData
{
    public List<ServerGroup> Groups { get; set; } = [];

    public List<SessionProfile> Sessions { get; set; } = [];
}
