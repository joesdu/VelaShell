using System.Collections.Specialized;
using System.ComponentModel;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>左侧映射快照;选中标记按映射 Id 区分同一远端的多条记录。</summary>
internal sealed record UploadMappingItem(UploadDirectoryMapping Mapping, bool IsCurrent)
{
    public string RemotePath => Mapping.RemotePath;
    public string LocalPath => Mapping.LocalPath;
}

/// <summary>上传专用勾选与映射状态,独立于文件列表的高亮和定位。</summary>
internal sealed class UploadPickerSelection : IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly LocalFilePaneViewModel _browser;
    private readonly UploadPickerStateStore _store;
    private readonly UploadPickerContext? _context;
    private readonly HashSet<string> _checked = new(PathComparer);
    private string _directory;
    private bool _restoring;
    private bool _protectMapping;
    private bool _disposed;
    private long _restoreVersion;
    private long _saveVersion;
    private Task _pendingSave = Task.CompletedTask;
    private Task _pendingOperation = Task.CompletedTask;

    public UploadPickerSelection(LocalFilePaneViewModel browser, UploadPickerStateStore store, UploadPickerContext? context)
    {
        _browser = browser;
        _store = store;
        _context = context is null ? null : context with
        {
            RemotePath = RemoteDirectoryBookmark.NormalizeRemotePath(context.RemotePath),
        };
        TargetRemotePath = _context?.RemotePath ?? string.Empty;
        _directory = browser.CurrentPath;
        browser.PropertyChanged += OnBrowserPropertyChanged;
        browser.Entries.CollectionChanged += OnEntriesChanged;
    }

    public event EventHandler? Changed;
    public IReadOnlyList<UploadMappingItem> Mappings { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    public string TargetRemotePath { get; private set; }
    public string ServerName => _context?.ServerName ?? string.Empty;
    public Guid? ActiveMappingId { get; private set; }
    public bool CanManageMappings => _context?.ProfileId is not null;
    public bool HasActiveMapping => !CanManageMappings || ActiveMappingId is not null;
    public bool IsRestoring => _restoring;
    public IReadOnlyList<LocalFileEntry> CheckedEntries =>
        [.. _browser.Entries.Where(entry => !entry.IsParentEntry && _checked.Contains(entry.FullPath))];
    public bool HasEntries => _browser.Entries.Any(entry => !entry.IsParentEntry);
    public bool? AllChecked => CheckedEntries.Count == 0 ? false
        : _browser.Entries.Where(entry => !entry.IsParentEntry).All(IsChecked) ? true : null;

    public bool IsChecked(LocalFileEntry entry) => !entry.IsParentEntry && _checked.Contains(entry.FullPath);

    public void SetChecked(LocalFileEntry entry, bool value)
    {
        if (_disposed || _restoring || !HasActiveMapping || entry.IsParentEntry || !_browser.Entries.Contains(entry))
        {
            return;
        }
        bool changed = value ? _checked.Add(entry.FullPath) : _checked.Remove(entry.FullPath);
        if (changed)
        {
            _protectMapping = false;
            ErrorMessage = null;
            Save(userChangedChecks: true);
            RaiseChanged();
        }
    }

    public void SetAll(bool value)
    {
        if (_disposed || _restoring || !HasActiveMapping)
        {
            return;
        }
        _checked.Clear();
        if (value)
        {
            _checked.UnionWith(_browser.Entries.Where(entry => !entry.IsParentEntry).Select(entry => entry.FullPath));
        }
        _protectMapping = false;
        ErrorMessage = null;
        Save(userChangedChecks: true);
        RaiseChanged();
    }

    public async Task InitializeAsync()
    {
        long version = ++_restoreVersion;
        _restoring = true;
        RaiseChanged();
        try
        {
            UploadPickerState state = await _store.LoadAsync();
            if (!IsCurrent(version))
            {
                return;
            }
            SetMappings(state);
            UploadDirectoryMapping? exact = state.Mappings.Where(item => item.ProfileId == _context?.ProfileId
                && item.RemotePath == _context?.RemotePath).OrderByDescending(item => item.SavedOrder).FirstOrDefault();
            UploadDirectoryMapping? mapping = exact;
            if (exact is not null && _context?.ProfileId is { } profile
                && state.LastUploadedMappings.TryGetValue(profile, out Guid last))
            {
                mapping = state.Mappings.FirstOrDefault(item => item.ProfileId == profile && item.Id == last) ?? exact;
            }
            if (mapping is not null)
            {
                ActiveMappingId = mapping.Id;
                TargetRemotePath = mapping.RemotePath;
            }
            bool restored = mapping is not null && await NavigateAsync(mapping.LocalPath);
            if (!IsCurrent(version))
            {
                return;
            }
            if (mapping is not null && !restored)
            {
                _protectMapping = true;
                ErrorMessage = Strings.Format("Sftp_UploadMappingUnavailable", mapping.LocalPath);
            }
            string[] paths = restored ? mapping!.CheckedPaths : CanManageMappings ? [] : state.CheckedPaths;
            if (!restored)
            {
                restored = await NavigateAsync(state.LocalPath);
            }
            if (!restored && IsCurrent(version))
            {
                await _browser.LoadInitialAsync();
                paths = [];
            }
            if (!IsCurrent(version))
            {
                return;
            }
            _directory = _browser.CurrentPath;
            if (mapping is null && CanManageMappings && _browser.CurrentPath.Length > 0 && _browser.ErrorMessage is null)
            {
                var added = await _store.AddMappingAsync(_context!, _browser.CurrentPath);
                if (!IsCurrent(version))
                {
                    return;
                }
                ActiveMappingId = added.Id;
                AcceptState(added.State, added.Saved);
                paths = [];
            }
            RestoreChecks(paths);
        }
        finally
        {
            FinishRestore(version);
        }
    }

    public Task ApplyMappingAsync(UploadMappingItem item)
    {
        Task operation = ApplyMappingCoreAsync(item);
        _pendingOperation = operation;
        return operation;
    }

    private async Task ApplyMappingCoreAsync(UploadMappingItem item)
    {
        long version = ++_restoreVersion;
        ++_saveVersion;
        _restoring = true;
        RaiseChanged();
        try
        {
            // 重新读取,避免点击时携带的 UI 快照比刚完成的勾选保存更旧。
            UploadPickerState state = await _store.LoadAsync();
            UploadDirectoryMapping? mapping = state.Mappings.FirstOrDefault(candidate =>
                candidate.Id == item.Mapping.Id && candidate.ProfileId == _context?.ProfileId);
            if (!IsCurrent(version) || mapping is null)
            {
                return;
            }
            if (await NavigateAsync(mapping.LocalPath) && IsCurrent(version))
            {
                ActiveMappingId = mapping.Id;
                TargetRemotePath = mapping.RemotePath;
                RestoreChecks(mapping.CheckedPaths);
                _directory = _browser.CurrentPath;
                _protectMapping = false;
                ErrorMessage = null;
                SetMappings(state);
            }
            else if (IsCurrent(version))
            {
                ErrorMessage = Strings.Format("Sftp_UploadMappingUnavailable", mapping.LocalPath);
            }
        }
        finally
        {
            FinishRestore(version);
        }
    }

    public Task AddMappingAsync(string remotePath, string localPath)
    {
        Task operation = AddMappingCoreAsync(remotePath, localPath);
        _pendingOperation = operation;
        return operation;
    }

    private async Task AddMappingCoreAsync(string remotePath, string localPath)
    {
        if (_disposed || !CanManageMappings || _restoring)
        {
            return;
        }
        var added = await _store.AddMappingAsync(_context! with { RemotePath = remotePath }, localPath);
        if (!_disposed)
        {
            AcceptState(added.State, added.Saved);
            await ApplyMappingAsync(Mappings.Single(item => item.Mapping.Id == added.Id));
        }
    }

    public Task DeleteMappingAsync(UploadMappingItem item)
    {
        Task operation = DeleteMappingCoreAsync(item);
        _pendingOperation = operation;
        return operation;
    }

    private async Task DeleteMappingCoreAsync(UploadMappingItem item)
    {
        if (_disposed || _context?.ProfileId is not { } profile)
        {
            return;
        }
        bool navigationPending = _restoring;
        long version = ++_restoreVersion;
        ++_saveVersion;
        _restoring = true;
        if (ActiveMappingId == item.Mapping.Id)
        {
            ActiveMappingId = null;
            _checked.Clear();
            _protectMapping = false;
        }
        RaiseChanged();
        try
        {
            if (navigationPending && _directory.Length > 0)
            {
                // 同时使本地面板的旧请求失效,仅增加映射版本还不足以阻止它改写文件列表。
                await _browser.NavigateToAsync(_directory);
            }
            var deleted = await _store.DeleteMappingAsync(profile, item.Mapping.Id);
            if (IsCurrent(version))
            {
                ErrorMessage = null;
                AcceptState(deleted.State, deleted.Saved);
            }
        }
        finally
        {
            if (IsCurrent(version))
            {
                _restoring = false;
                RaiseChanged();
            }
        }
    }

    public UploadSelectionResult? Submit()
    {
        if (_disposed || _restoring || !HasActiveMapping || _browser.IsDirectoryLoading || CheckedEntries.Count == 0)
        {
            return null;
        }
        var result = new UploadSelectionResult([.. CheckedEntries.Select(entry => entry.FullPath)], TargetRemotePath, ActiveMappingId);
        Save(userChangedChecks: true);
        if (_context?.ProfileId is { } profile && ActiveMappingId is { } id)
        {
            _pendingSave = MarkUploadedAsync(++_saveVersion, profile, id);
        }
        return result;
    }

    private async Task MarkUploadedAsync(long version, Guid profile, Guid id)
    {
        var saved = await _store.MarkUploadedAsync(profile, id);
        if (!_disposed && version == _saveVersion)
        {
            AcceptState(saved.State, saved.Saved);
        }
    }

    public async Task FlushAsync()
    {
        // 导航结束时还会排入一次保存,先等操作结束再取最新写入。
        try { await _pendingOperation; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 操作入口负责显示错误;关闭时仍要排空其他已经排队的写入。
        }
        await _pendingSave;
    }
    public void ReportError(string message) { ErrorMessage = message; RaiseChanged(); }
    private bool IsCurrent(long version) => !_disposed && version == _restoreVersion;

    private void FinishRestore(long version)
    {
        if (IsCurrent(version))
        {
            _restoring = false;
            Save();
            RaiseChanged();
        }
    }

    private async Task<bool> NavigateAsync(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            string canonical = RemoteDirectoryBookmark.NormalizeLocalPath(path);
            await _browser.NavigateToAsync(canonical);
            return !_disposed && _browser.ErrorMessage is null && PathComparer.Equals(_browser.CurrentPath, canonical);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private void RestoreChecks(IEnumerable<string> paths)
    {
        _checked.Clear();
        _checked.UnionWith(paths);
        ReconcileChecks();
    }

    private void OnBrowserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_restoring && e.PropertyName == nameof(LocalFilePaneViewModel.CurrentPath)
            && !PathComparer.Equals(NormalizeDirectory(_directory), NormalizeDirectory(_browser.CurrentPath)))
        {
            _directory = _browser.CurrentPath;
            _checked.Clear();
            _protectMapping = false;
            ErrorMessage = null;
        }
        RaiseChanged();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_restoring)
        {
            ReconcileChecks();
            Save();
        }
        RaiseChanged();
    }

    private void ReconcileChecks() => _checked.IntersectWith(
        _browser.Entries.Where(entry => !entry.IsParentEntry).Select(entry => entry.FullPath));
    private static string NormalizeDirectory(string path) => path.Length == 0 ? path
        : RemoteDirectoryBookmark.NormalizeLocalPath(path);

    private void Save(bool userChangedChecks = false)
    {
        if (!_disposed && _browser.CurrentPath.Length > 0 && (_browser.ErrorMessage is null || userChangedChecks))
        {
            _pendingSave = SaveCoreAsync(++_saveVersion, ActiveMappingId, NormalizeDirectory(_browser.CurrentPath), [.. _checked]);
        }
    }

    private async Task SaveCoreAsync(long version, Guid? id, string directory, string[] paths)
    {
        var saved = await _store.SaveSelectionAsync(_context, id, directory, paths, !_protectMapping);
        if (!_disposed && version == _saveVersion)
        {
            AcceptState(saved.State, saved.Saved);
        }
    }

    private void AcceptState(UploadPickerState state, bool saved)
    {
        SetMappings(state);
        if (!saved)
        {
            ErrorMessage = Strings.Get("Sftp_UploadMemorySaveFailed");
        }
        RaiseChanged();
    }

    private void SetMappings(UploadPickerState state) => Mappings = [.. state.Mappings
        .Where(item => item.ProfileId == _context?.ProfileId)
        .Select(item => new UploadMappingItem(item.Clone(), item.Id == ActiveMappingId))
        .OrderByDescending(item => item.IsCurrent).ThenBy(item => item.RemotePath, StringComparer.Ordinal)
        .ThenBy(item => item.LocalPath, PathComparer)];

    private void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ++_restoreVersion;
        _browser.PropertyChanged -= OnBrowserPropertyChanged;
        _browser.Entries.CollectionChanged -= OnEntriesChanged;
    }
}
