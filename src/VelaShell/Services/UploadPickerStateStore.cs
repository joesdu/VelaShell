using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>一次上传选择的目标快照;临时连接没有可持久化的配置标识。</summary>
internal sealed record UploadPickerContext(Guid? ProfileId, string RemotePath, string ServerName);

/// <summary>本机上传目录记忆,与随连接导出的目录收藏分开保存。</summary>
internal sealed class UploadDirectoryMapping
{
    public Guid Id { get; set; }
    public long SavedOrder { get; set; }
    public Guid ProfileId { get; set; }
    public string RemotePath { get; set; } = "/";
    public string LocalPath { get; set; } = string.Empty;
    public string[] CheckedPaths { get; set; } = [];

    public UploadDirectoryMapping Clone() => new()
    {
        Id = Id, SavedOrder = SavedOrder, ProfileId = ProfileId, RemotePath = RemotePath, LocalPath = LocalPath,
        CheckedPaths = [.. CheckedPaths ?? []],
    };
}

/// <summary>最近一次本地选择及各连接、各远端目录的独立记忆。</summary>
internal sealed class UploadPickerState
{
    public string LocalPath { get; set; } = string.Empty;
    public string[] CheckedPaths { get; set; } = [];
    public List<UploadDirectoryMapping> Mappings { get; set; } = [];
    public Dictionary<Guid, Guid> LastUploadedMappings { get; set; } = [];
    public long SaveOrder { get; set; }

    public UploadPickerState Clone() => new()
    {
        LocalPath = LocalPath, CheckedPaths = [.. CheckedPaths ?? []],
        Mappings = [.. (Mappings ?? []).Select(item => item.Clone())],
        LastUploadedMappings = new(LastUploadedMappings ?? []), SaveOrder = SaveOrder,
    };
}

/// <summary>串行读写上传记忆。每次修改从最新内存状态合并,慢写入不能覆盖较新的选择。</summary>
internal sealed class UploadPickerStateStore(IAppDataStore? store)
{
    private readonly object _sync = new();
    private Task _pending = Task.CompletedTask;
    private UploadPickerState? _state;

    public Task<UploadPickerState> LoadAsync() => Enqueue(async () => (await ReadCoreAsync()).Clone());

    public Task<(UploadPickerState State, bool Saved)> SaveSelectionAsync(
        UploadPickerContext? context, Guid? mappingId, string localPath, string[] checkedPaths, bool updateMapping)
    {
        string[] paths = [.. checkedPaths];
        return Enqueue(async () =>
        {
            UploadPickerState state = await ReadCoreAsync();
            state.LocalPath = localPath;
            state.CheckedPaths = paths;
            if (updateMapping && context?.ProfileId is { } profileId
                && state.Mappings.FirstOrDefault(item => item.Id == mappingId && item.ProfileId == profileId) is { } mapping)
            {
                // 更新绝不插入:删除之后到达的旧窗口/旧保存不能复活映射。
                mapping.LocalPath = localPath;
                mapping.CheckedPaths = paths;
                mapping.SavedOrder = ++state.SaveOrder;
            }
            return (state.Clone(), await PersistAsync(state));
        });
    }

    public Task<(UploadPickerState State, bool Saved, Guid Id)> AddMappingAsync(
        UploadPickerContext context, string localPath, string[]? checkedPaths = null)
    {
        string[] paths = [.. checkedPaths ?? []];
        return Enqueue(async () =>
        {
            UploadPickerState state = await ReadCoreAsync();
            Guid profileId = context.ProfileId ?? throw new ArgumentException("A saved connection is required.", nameof(context));
            string remote = RemoteDirectoryBookmark.NormalizeRemotePath(context.RemotePath);
            string local = RemoteDirectoryBookmark.NormalizeLocalPath(localPath);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            UploadDirectoryMapping? mapping = state.Mappings.FirstOrDefault(item => item.ProfileId == profileId
                && item.RemotePath == remote && string.Equals(item.LocalPath, local, comparison));
            if (mapping is null)
            {
                mapping = new()
                {
                    Id = Guid.NewGuid(), ProfileId = profileId, RemotePath = remote, LocalPath = local,
                    CheckedPaths = paths, SavedOrder = ++state.SaveOrder,
                };
                state.Mappings.Add(mapping);
            }
            return (state.Clone(), await PersistAsync(state), mapping.Id);
        });
    }

    public Task<(UploadPickerState State, bool Saved)> DeleteMappingAsync(Guid profileId, Guid id) => Enqueue(async () =>
    {
        UploadPickerState state = await ReadCoreAsync();
        state.Mappings.RemoveAll(item => item.ProfileId == profileId && item.Id == id);
        if (state.LastUploadedMappings.GetValueOrDefault(profileId) == id)
        {
            state.LastUploadedMappings.Remove(profileId);
        }
        return (state.Clone(), await PersistAsync(state));
    });

    public Task<(UploadPickerState State, bool Saved)> MarkUploadedAsync(Guid profileId, Guid id) => Enqueue(async () =>
    {
        UploadPickerState state = await ReadCoreAsync();
        if (state.Mappings.Any(item => item.ProfileId == profileId && item.Id == id))
        {
            state.LastUploadedMappings[profileId] = id;
        }
        return (state.Clone(), await PersistAsync(state));
    });

    private async Task<bool> PersistAsync(UploadPickerState state)
    {
        try
        {
            if (store is not null)
            {
                await store.UpsertAsync("uploadPickerState", "state", state.Clone()).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
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
            _state.LastUploadedMappings ??= [];
            _state.SaveOrder = Math.Max(_state.SaveOrder, _state.Mappings.Select(item => item.SavedOrder).DefaultIfEmpty().Max());
            bool migrated = false;
            foreach (UploadDirectoryMapping mapping in _state.Mappings)
            {
                if (mapping.Id == Guid.Empty)
                {
                    mapping.Id = Guid.NewGuid();
                    mapping.SavedOrder = ++_state.SaveOrder;
                    migrated = true;
                }
            }
            if (migrated)
            {
                await PersistAsync(_state).ConfigureAwait(false);
            }
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
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 失败仍交给原调用方;后续合法操作不继承前一次参数/存储错误。
        }
        return await operation().ConfigureAwait(false);
    }
}
