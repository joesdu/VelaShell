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
        Id = Id,
        SavedOrder = SavedOrder,
        ProfileId = ProfileId,
        RemotePath = RemotePath,
        LocalPath = LocalPath,
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
        LocalPath = LocalPath,
        CheckedPaths = [.. CheckedPaths ?? []],
        Mappings = [.. (Mappings ?? []).Select(item => item.Clone())],
        LastUploadedMappings = [with(LastUploadedMappings ?? [])],
        SaveOrder = SaveOrder,
    };
}

/// <summary>串行读写上传记忆。每次修改从最新内存状态合并,慢写入不能覆盖较新的选择。</summary>
internal sealed class UploadPickerStateStore(IAppDataStore? store)
{
    private readonly Lock _sync = new();
    private Task _pending = Task.CompletedTask;
    private UploadPickerState? _state;

    // 读取成功(或没有存储)之前不写盘:一次读失败后把临时的空状态写回去,会清掉磁盘上已有的映射。
    private bool _loaded;

    public Task<UploadPickerState> LoadAsync() => Enqueue(async () => (await ReadCoreAsync()).Clone());

    /// <summary>
    /// 保存全局最近目录与勾选。映射只记录它自己目录下的勾选:当前目录就是映射的本地目录时才更新勾选;
    /// 映射的本地 / 远端目录只由添加映射决定,在选择器里浏览别的目录不会改写绑定。
    /// </summary>
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
                && state.Mappings.FirstOrDefault(item => item.Id == mappingId && item.ProfileId == profileId) is { } mapping
                && SameLocalPath(mapping.LocalPath, localPath))
            {
                // 更新绝不插入:删除之后到达的旧窗口/旧保存不能复活映射。
                mapping.CheckedPaths = paths;
                mapping.SavedOrder = ++state.SaveOrder;
            }
            return (state.Clone(), await PersistAsync(state));
        });
    }

    private static bool SameLocalPath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(RemoteDirectoryBookmark.NormalizeLocalPath(left), RemoteDirectoryBookmark.NormalizeLocalPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    RemotePath = remote,
                    LocalPath = local,
                    CheckedPaths = paths,
                    SavedOrder = ++state.SaveOrder,
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
        if (!_loaded)
        {
            return false;
        }
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
        if (_loaded)
        {
            return _state!;
        }
        UploadPickerState? loaded;
        try
        {
            loaded = store is null ? null
                : await store.GetAsync<UploadPickerState>("uploadPickerState", "state").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 读不出来时仍允许打开选择器:先用内存里的临时状态,不写盘(见 PersistAsync),下次操作再重读。
            return _state ??= Normalize(new());
        }
        // 读成功后以磁盘内容为准;读失败期间的临时改动没写过盘,直接丢弃,不与真实数据交错合并。
        _state = Normalize(loaded ?? new());
        _loaded = true;
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
        return _state;
    }

    private static UploadPickerState Normalize(UploadPickerState state)
    {
        state.Mappings ??= [];
        state.LastUploadedMappings ??= [];
        state.CheckedPaths ??= [];
        state.LocalPath ??= string.Empty;
        state.SaveOrder = Math.Max(state.SaveOrder, state.Mappings.Select(item => item.SavedOrder).DefaultIfEmpty().Max());
        return state;
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
