using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VelaShell.Core.Data;

public class JsonDataStore(ILogger<JsonDataStore>? logger = null)
{
    private readonly SemaphoreSlim _dictionaryLock = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _fileLocks = new();

    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<T?> LoadAsync<T>(string filePath, CancellationToken cancellationToken = default) where T : class, new()
    {
        SemaphoreSlim fileLock = await GetFileLockAsync(filePath).ConfigureAwait(false);
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                return new();
            }
            await using var stream = new FileStream(filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Corrupt JSON detected in {FilePath}, resetting to defaults", filePath);
            return new();
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task SaveAsync<T>(string filePath, T data, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        SemaphoreSlim fileLock = await GetFileLockAsync(filePath).ConfigureAwait(false);
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(filePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);
                    await JsonSerializer.SerializeAsync(stream, data, _options, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay((int)Math.Pow(2, attempt) * 100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task<SemaphoreSlim> GetFileLockAsync(string filePath)
    {
        await _dictionaryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_fileLocks.TryGetValue(filePath, out SemaphoreSlim? fileLock))
            {
                return fileLock;
            }
            fileLock = new(1, 1);
            _fileLocks[filePath] = fileLock;
            return fileLock;
        }
        finally
        {
            _dictionaryLock.Release();
        }
    }
}
