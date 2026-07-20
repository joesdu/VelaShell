using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>
/// 应用设置与运行时状态的持久化服务:读写用户设置(<see cref="AppSettings" />)
/// 与应用状态(<see cref="AppState" />),并在保存后广播以支持热更新。
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 设置持久化后触发,使得在线消费者(已打开的终端标签、
    /// 主题等)无需重启即可重新应用这些设置 (#3/#21)。
    /// </summary>
    event Action<AppSettings>? SettingsSaved;

    /// <summary>读取当前持久化的应用设置;不存在时返回默认值。</summary>
    Task<AppSettings> GetSettingsAsync();

    /// <summary>持久化应用设置,并触发 <see cref="SettingsSaved" /> 以通知在线消费者。</summary>
    Task SaveSettingsAsync(AppSettings settings);

    /// <summary>读取当前持久化的应用运行时状态(如窗口/会话布局)。</summary>
    Task<AppState> GetStateAsync();

    /// <summary>持久化应用运行时状态。</summary>
    Task SaveStateAsync(AppState state);
}
