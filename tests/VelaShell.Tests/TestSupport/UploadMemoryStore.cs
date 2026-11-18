using System.Collections.Concurrent;
using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Services;

namespace VelaShell.Tests.TestSupport;

/// <summary>每次读写都经 JSON 往返,用于验证上传记忆真正可跨服务实例恢复。</summary>
internal sealed class UploadMemoryStore : IAppDataStore
{
    private readonly ConcurrentDictionary<string, string> _rows = new();
    public Func<Task>? BeforeWrite { get; set; }

    public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
        Task.FromResult(_rows.TryGetValue(collection + "/" + id, out string? json) ? JsonSerializer.Deserialize<T>(json) : null);

    public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
        Task.FromResult(_rows.Where(pair => pair.Key.StartsWith(collection + "/", StringComparison.Ordinal))
            .Select(pair => JsonSerializer.Deserialize<T>(pair.Value)!).ToList());

    public async Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
    {
        if (BeforeWrite is { } before)
        {
            await before();
        }
        _rows[collection + "/" + id] = JsonSerializer.Serialize(value);
    }

    public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        _rows.TryRemove(collection + "/" + id, out _);
        return Task.CompletedTask;
    }
}

/// <summary>按旧测试的简写建立初始映射与全局最近状态,不经过 UI 操作。</summary>
internal static class UploadStateSeeds
{
    public static async Task SaveAsync(this UploadPickerStateStore store, UploadPickerContext? context,
        string localPath, string[] checkedPaths, bool mapping)
    {
        Guid? id = mapping ? (await store.AddMappingAsync(context!, localPath, checkedPaths)).Id : null;
        await store.SaveSelectionAsync(context, id, localPath, checkedPaths, mapping);
    }
}
