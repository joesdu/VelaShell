using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>应用设置与运行状态的持久化服务,基于 <see cref="JsonDataStore"/> 读写 JSON 文件。</summary>
public class SettingsService : ISettingsService
{
    private readonly JsonDataStore _dataStore;
    private readonly string _settingsPath;
    private readonly string _statePath;

    /// <summary>创建设置服务;未指定 <paramref name="basePath"/> 时默认使用用户目录下的 .velashell 文件夹。</summary>
    public SettingsService(JsonDataStore dataStore, string? basePath = null)
    {
        _dataStore = dataStore;
        if (string.IsNullOrEmpty(basePath))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            basePath = Path.Combine(userProfile, ".velashell");
        }
        _settingsPath = Path.Combine(basePath, "settings.json");
        _statePath = Path.Combine(basePath, "state.json");
    }

    /// <summary>读取应用设置;文件不存在时返回默认值,并在返回前执行归一化。</summary>
    public async Task<AppSettings> GetSettingsAsync()
    {
        AppSettings settings = await _dataStore.LoadAsync<AppSettings>(_settingsPath).ConfigureAwait(false) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }

    /// <summary>设置成功保存后触发,携带最新的设置对象供订阅方刷新。</summary>
    public event Action<AppSettings>? SettingsSaved;

    /// <summary>持久化应用设置,写入完成后触发 <see cref="SettingsSaved"/> 事件。</summary>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await _dataStore.SaveAsync(_settingsPath, settings).ConfigureAwait(false);
        SettingsSaved?.Invoke(settings);
    }

    /// <summary>读取应用运行状态;文件不存在时返回默认状态。</summary>
    public async Task<AppState> GetStateAsync() => await _dataStore.LoadAsync<AppState>(_statePath).ConfigureAwait(false) ?? new AppState();

    /// <summary>持久化应用运行状态到状态文件。</summary>
    public async Task SaveStateAsync(AppState state) => await _dataStore.SaveAsync(_statePath, state).ConfigureAwait(false);
}
