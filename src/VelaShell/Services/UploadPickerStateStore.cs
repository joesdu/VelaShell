using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>一次上传选择的目标快照;临时连接没有可持久化的配置标识。</summary>
internal sealed record UploadPickerContext(Guid? ProfileId, string RemotePath, string ServerName);

/// <summary>本机上传目录记忆,与随连接导出的目录收藏分开保存。</summary>
internal sealed class UploadDirectoryMapping
{
    public Guid ProfileId { get; set; }
    public string RemotePath { get; set; } = "/";
    public string LocalPath { get; set; } = string.Empty;
    public string[] CheckedPaths { get; set; } = [];

    public UploadDirectoryMapping Clone() => new()
    {
        ProfileId = ProfileId, RemotePath = RemotePath, LocalPath = LocalPath,
        CheckedPaths = [.. CheckedPaths ?? []],
    };
}

/// <summary>最近一次本地选择及各连接、各远端目录的独立记忆。</summary>
internal sealed class UploadPickerState
{
    public string LocalPath { get; set; } = string.Empty;
    public string[] CheckedPaths { get; set; } = [];
    public List<UploadDirectoryMapping> Mappings { get; set; } = [];

    public UploadPickerState Clone() => new()
    {
        LocalPath = LocalPath, CheckedPaths = [.. CheckedPaths ?? []],
        Mappings = [.. (Mappings ?? []).Select(item => item.Clone())],
    };
}

/// <summary>串行读写上传记忆。每次修改从最新内存状态合并,慢写入不能覆盖较新的选择。</summary>
internal sealed class UploadPickerStateStore(IAppDataStore? store)
{
    private readonly object _sync = new();
    private Task _pending = Task.CompletedTask;
    private UploadPickerState? _state;

    public Task<UploadPickerState> LoadAsync() => Enqueue(async () => (await ReadCoreAsync()).Clone());

    public Task<(UploadPickerState State, bool Saved)> SaveAsync(
        UploadPickerContext? context, string localPath, string[] checkedPaths, bool updateMapping)
    {
        string[] paths = [.. checkedPaths];
        return Enqueue(async () =>
        {
            UploadPickerState state = await ReadCoreAsync();
            state.LocalPath = localPath;
            state.CheckedPaths = paths;
            if (updateMapping && context?.ProfileId is { } profileId)
            {
                string remotePath = RemoteDirectoryBookmark.NormalizeRemotePath(context.RemotePath);
                state.Mappings.RemoveAll(item => item.ProfileId == profileId && item.RemotePath == remotePath);
                state.Mappings.Add(new()
                {
                    ProfileId = profileId, RemotePath = remotePath,
                    LocalPath = localPath, CheckedPaths = paths,
                });
            }
            bool saved = true;
            if (store is not null)
            {
                try
                {
                    await store.UpsertAsync("uploadPickerState", "state", state.Clone()).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // 内存记忆仍有效,由窗口告知本次未能持久化。
                    saved = false;
                }
            }
            return (state.Clone(), saved);
        });
    }

    private async Task<UploadPickerState> ReadCoreAsync()
    {
        if (_state is null)
        {
            try
            {
                _state = store is null ? null
                    : await store.GetAsync<UploadPickerState>("uploadPickerState", "state").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 使用习惯读不出来时仍允许打开选择器。
            }
            _state ??= new();
            _state.Mappings ??= [];
        }
        return _state;
    }

    private Task<T> Enqueue<T>(Func<Task<T>> operation)
    {
        lock (_sync)
        {
            Task<T> next = RunAfterAsync(_pending, operation);
            _pending = next;
            return next;
        }
    }

    private static async Task<T> RunAfterAsync<T>(Task previous, Func<Task<T>> operation)
    {
        await previous.ConfigureAwait(false);
        return await operation().ConfigureAwait(false);
    }
}
