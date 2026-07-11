using System.Text.Json;
using SonnetDB.Documents;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合 <c>app_config</c> 的设置服务:
/// settings 与 state 各为一个固定 Id 的 JSON 文档。首次运行导入既有 settings.json / state.json。
/// </summary>
public sealed class SonnetDbSettingsService(SonnetDbEngine engine, IReadOnlyList<string>? legacyDirectories = null) : ISettingsService
{
    private const string SettingsDocId = "settings";
    private const string StateDocId = "state";

    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly IReadOnlyList<string> _legacyDirectories = legacyDirectories ?? [];

    /// <summary>
    /// settings 文档的 JSON 缓存:设置是读热点(每次连接/每个传输文件都读),
    /// 而引擎全局锁串行所有集合操作。缓存序列化文本、按次反序列化,调用方仍各拿独立
    /// 实例(可安全修改后再保存),读路径却不再进锁/碰盘。保存时同步刷新。
    /// </summary>
    private volatile string? _settingsJsonCache;

    public event Action<AppSettings>? SettingsSaved;

    public async Task<AppSettings> GetSettingsAsync()
    {
        if (_settingsJsonCache is { } cached && SonnetDbJson.Deserialize<AppSettings>(cached) is { } fromCache)
        {
            fromCache.Normalize();
            return fromCache;
        }
        AppSettings settings = await GetOrImportAsync<AppSettings>(SettingsDocId, "settings.json").ConfigureAwait(false);
        settings.Normalize();
        _settingsJsonCache = SonnetDbJson.Serialize(settings);
        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string json = SonnetDbJson.Serialize(settings);
        await UpsertJsonAsync(SettingsDocId, json).ConfigureAwait(false);
        _settingsJsonCache = json;
        SettingsSaved?.Invoke(settings);
    }

    public async Task<AppState> GetStateAsync() => await GetOrImportAsync<AppState>(StateDocId, "state.json").ConfigureAwait(false);

    public async Task SaveStateAsync(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        await UpsertAsync(StateDocId, state).ConfigureAwait(false);
    }

    private async Task<T> GetOrImportAsync<T>(string docId, string legacyFileName) where T : class, new()
    {
        T? existing = await _engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection, store =>
        {
            DocumentRow? row = store.Get(docId);
            return row is null ? null : SonnetDbJson.Deserialize<T>(row.Json);
        }).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }
        T? imported = await TryImportLegacyAsync<T>(legacyFileName).ConfigureAwait(false);
        if (imported is null)
        {
            return new();
        }
        await UpsertAsync(docId, imported).ConfigureAwait(false);
        return imported;
    }

    private Task UpsertAsync<T>(string docId, T value) where T : class => UpsertJsonAsync(docId, SonnetDbJson.Serialize(value));

    private async Task UpsertJsonAsync(string docId, string json)
    {
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, store =>
        {
            store.Upsert(docId, json);
            return null;
        }).ConfigureAwait(false);
    }

    private async Task<T?> TryImportLegacyAsync<T>(string fileName) where T : class
    {
        foreach (string directory in _legacyDirectories)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                T? value = SonnetDbJson.Deserialize<T>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
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
