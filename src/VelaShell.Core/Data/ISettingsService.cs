using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

public interface ISettingsService
{
    /// <summary>
    /// Raised after settings are persisted, so live consumers (open terminal tabs,
    /// theme, …) can re-apply them without a restart (#3/#21).
    /// </summary>
    event Action<AppSettings>? SettingsSaved;

    Task<AppSettings> GetSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
    Task<AppState> GetStateAsync();
    Task SaveStateAsync(AppState state);
}
