using System.Collections.Specialized;
using System.ComponentModel;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>左侧的一条映射快照;当前目标标记与列表焦点无关。</summary>
internal sealed record UploadMappingItem(UploadDirectoryMapping Mapping, bool IsCurrent)
{
    public string RemotePath => Mapping.RemotePath;
    public string LocalPath => Mapping.LocalPath;
}

/// <summary>上传专用勾选集。文件列表的高亮与键盘定位仍由本地面板负责。</summary>
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

    public UploadPickerSelection(LocalFilePaneViewModel browser, UploadPickerStateStore store, UploadPickerContext? context)
    {
        _browser = browser;
        _store = store;
        _context = context is null ? null : context with
        {
            RemotePath = RemoteDirectoryBookmark.NormalizeRemotePath(context.RemotePath),
        };
        _directory = browser.CurrentPath;
        browser.PropertyChanged += OnBrowserPropertyChanged;
        browser.Entries.CollectionChanged += OnEntriesChanged;
    }

    public event EventHandler? Changed;
    public IReadOnlyList<UploadMappingItem> Mappings { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    public bool IsRestoring => _restoring;
    public IReadOnlyList<LocalFileEntry> CheckedEntries =>
        [.. _browser.Entries.Where(entry => !entry.IsParentEntry && _checked.Contains(entry.FullPath))];
    public bool HasEntries => _browser.Entries.Any(entry => !entry.IsParentEntry);
    public bool? AllChecked => CheckedEntries.Count == 0 ? false
        : _browser.Entries.Where(entry => !entry.IsParentEntry).All(IsChecked) ? true : null;

    public bool IsChecked(LocalFileEntry entry) => !entry.IsParentEntry && _checked.Contains(entry.FullPath);

    public void SetChecked(LocalFileEntry entry, bool value)
    {
        if (_disposed || _restoring || entry.IsParentEntry || !_browser.Entries.Contains(entry))
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
        if (_disposed || _restoring)
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
        _restoring = true;
        RaiseChanged();
        try
        {
            UploadPickerState state = await _store.LoadAsync();
            if (_disposed)
            {
                return;
            }
            SetMappings(state);
            UploadDirectoryMapping? mapping = state.Mappings.FirstOrDefault(item =>
                item.ProfileId == _context?.ProfileId && item.RemotePath == _context?.RemotePath);
            bool restored = mapping is not null && await NavigateAsync(mapping.LocalPath);
            if (mapping is not null && !restored)
            {
                _protectMapping = true;
                ErrorMessage = Strings.Format("Sftp_UploadMappingUnavailable", mapping.LocalPath);
            }
            string[] checkedPaths = restored ? mapping!.CheckedPaths : state.CheckedPaths;
            if (!restored)
            {
                restored = await NavigateAsync(state.LocalPath);
            }
            if (!restored)
            {
                await _browser.LoadInitialAsync();
                checkedPaths = [];
            }
            RestoreChecks(checkedPaths);
            _directory = _browser.CurrentPath;
        }
        finally
        {
            _restoring = false;
            if (!_disposed)
            {
                Save();
                RaiseChanged();
            }
        }
    }

    public async Task ApplyMappingAsync(UploadMappingItem item)
    {
        long version = ++_restoreVersion;
        _restoring = true;
        RaiseChanged();
        try
        {
            if (await NavigateAsync(item.LocalPath) && version == _restoreVersion && !_disposed)
            {
                RestoreChecks(item.Mapping.CheckedPaths);
                _directory = _browser.CurrentPath;
                _protectMapping = false;
                ErrorMessage = null;
            }
            else if (version == _restoreVersion)
            {
                ErrorMessage = Strings.Format("Sftp_UploadMappingUnavailable", item.LocalPath);
            }
        }
        finally
        {
            if (version == _restoreVersion)
            {
                _restoring = false;
                if (!_disposed)
                {
                    Save();
                    RaiseChanged();
                }
            }
        }
    }

    public Task FlushAsync() => _pendingSave;

    public void ReportError(string message)
    {
        ErrorMessage = message;
        RaiseChanged();
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
        // 导航失败保留的是上一个成功目录;用户仍可修改并保存它的勾选,不必先刷新来清掉错误栏。
        if (!_disposed && _browser.CurrentPath.Length > 0 && (_browser.ErrorMessage is null || userChangedChecks))
        {
            _pendingSave = SaveCoreAsync(++_saveVersion, NormalizeDirectory(_browser.CurrentPath), [.. _checked]);
        }
    }

    private async Task SaveCoreAsync(long version, string directory, string[] paths)
    {
        (UploadPickerState state, bool saved) = await _store.SaveAsync(_context, directory, paths, !_protectMapping);
        if (!_disposed && version == _saveVersion)
        {
            SetMappings(state);
            if (!saved)
            {
                ErrorMessage = Strings.Get("Sftp_UploadMemorySaveFailed");
            }
            RaiseChanged();
        }
    }

    private void SetMappings(UploadPickerState state) => Mappings = [.. state.Mappings
        .Where(item => item.ProfileId == _context?.ProfileId)
        .Select(item => new UploadMappingItem(item.Clone(), item.RemotePath == _context?.RemotePath))
        .OrderByDescending(item => item.IsCurrent).ThenBy(item => item.RemotePath, StringComparer.Ordinal)];

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
        _browser.PropertyChanged -= OnBrowserPropertyChanged;
        _browser.Entries.CollectionChanged -= OnEntriesChanged;
    }
}
