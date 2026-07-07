using System.Text.Json;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;

namespace PulseTerm.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合 <c>app_config</c> 的设置服务:
/// settings 与 state 各为一个固定 Id 的 JSON 文档。首次运行导入既有 settings.json / state.json。
/// </summary>
public sealed class SonnetDbSettingsService : ISettingsService
{
    private const string SettingsDocId = "settings";
    private const string StateDocId = "state";

    private readonly SonnetDbEngine _engine;
    private readonly IReadOnlyList<string> _legacyDirectories;

    public event Action<AppSettings>? SettingsSaved;

    public SonnetDbSettingsService(SonnetDbEngine engine, IReadOnlyList<string>? legacyDirectories = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _legacyDirectories = legacyDirectories ?? [];
    }

    public async Task<AppSettings> GetSettingsAsync()
        => await GetOrImportAsync<AppSettings>(SettingsDocId, "settings.json").ConfigureAwait(false);

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await UpsertAsync(SettingsDocId, settings).ConfigureAwait(false);
        SettingsSaved?.Invoke(settings);
    }

    public async Task<AppState> GetStateAsync()
        => await GetOrImportAsync<AppState>(StateDocId, "state.json").ConfigureAwait(false);

    public async Task SaveStateAsync(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        await UpsertAsync(StateDocId, state).ConfigureAwait(false);
    }

    private async Task<T> GetOrImportAsync<T>(string docId, string legacyFileName) where T : class, new()
    {
        var existing = await _engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection, store =>
        {
            var row = store.Get(docId);
            return row is null ? null : SonnetDbJson.Deserialize<T>(row.Json);
        }).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var imported = await TryImportLegacyAsync<T>(legacyFileName).ConfigureAwait(false);
        if (imported is not null)
        {
            await UpsertAsync(docId, imported).ConfigureAwait(false);
            return imported;
        }

        return new T();
    }

    private async Task UpsertAsync<T>(string docId, T value) where T : class
    {
        var json = SonnetDbJson.Serialize(value);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, store =>
        {
            store.Upsert(docId, json);
            return null;
        }).ConfigureAwait(false);
    }

    private async Task<T?> TryImportLegacyAsync<T>(string fileName) where T : class
    {
        foreach (var directory in _legacyDirectories)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var value = SonnetDbJson.Deserialize<T>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 旧文件损坏时跳过。
            }
        }

        return null;
    }
}
