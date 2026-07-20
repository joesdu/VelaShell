using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

/// <summary>Domain ViewModel for the local side of an SFTP dual-pane document.</summary>
public sealed class LocalFilePaneViewModel : ReactiveObject
{
    private readonly TransferOptions _transferOptions;
    private readonly ILocalFileSystem _fileSystem;
    private readonly ILocalRootProvider _rootProvider;
    private readonly BatchObservableCollection<LocalFileEntry> _entries = [];
    private readonly ObservableCollection<LocalRootEntry> _roots = [];
    private readonly CancellationTokenSource _lifetime = new();
    private string _currentPath = string.Empty;
    private long _navigationVersion;
    private bool _isDirectoryLoading;
    private string? _errorMessage;
    private LocalRootEntry? _selectedRoot;
    private bool _isSwitchingRoot;

    /// <summary>Creates a local pane using the configured transfer download directory.</summary>
    public LocalFilePaneViewModel(TransferOptions transferOptions)
        : this(transferOptions, null) { }

    internal LocalFilePaneViewModel(
        TransferOptions transferOptions,
        ILocalFileSystem? fileSystem = null,
        ILocalRootProvider? rootProvider = null
    )
    {
        _transferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions));
        _fileSystem = fileSystem ?? new PhysicalLocalFileSystem();
        _rootProvider = rootProvider ?? new PhysicalLocalRootProvider();
        Entries = _entries;
        Roots = _roots;
        SelectedEntries = [];
        NavigateToCommand = ReactiveCommand.CreateFromTask<string>(path => NavigateToAsync(path, _lifetime.Token));
        ActivateCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => ActivateAsync(entry, _lifetime.Token));
        GoUpCommand = ReactiveCommand.CreateFromTask(() => GoUpAsync(_lifetime.Token));
        RefreshCommand = ReactiveCommand.CreateFromTask(() => RefreshAsync(_lifetime.Token));
        SwitchRootCommand = ReactiveCommand.CreateFromTask<LocalRootEntry>(root => SwitchRootAsync(root, _lifetime.Token));
        RefreshRootsCommand = ReactiveCommand.CreateFromTask(() => RefreshRootsAsync(_lifetime.Token));
        DeleteItemCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => DeleteItemAsync(entry, _lifetime.Token));
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(() => DeleteSelectedAsync(_lifetime.Token));
        UploadSelectedCommand = ReactiveCommand.CreateFromTask(
            () => UploadSelectedAsync?.Invoke() ?? Task.CompletedTask);
        RenameCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => RenameAsync(entry, _lifetime.Token));
        NewFolderCommand = ReactiveCommand.CreateFromTask(() => NewFolderAsync(_lifetime.Token));
        OpenItemCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => OpenItemAsync(entry, _lifetime.Token));
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
    }

    /// <summary>Visible local rows, including a synthetic parent row outside the filesystem root.</summary>
    public ObservableCollection<LocalFileEntry> Entries { get; }

    /// <summary>Selectable physical roots for the local pane.</summary>
    public ObservableCollection<LocalRootEntry> Roots { get; }

    /// <summary>The root containing the current path, selected by longest prefix.</summary>
    public LocalRootEntry? SelectedRoot
    {
        get => _selectedRoot;
        private set => this.RaiseAndSetIfChanged(ref _selectedRoot, value);
    }

    /// <summary>Selected rows used by the host for multi-item operations.</summary>
    public ObservableCollection<LocalFileEntry> SelectedEntries { get; }

    /// <summary>Navigates to a canonical local directory.</summary>
    public ReactiveCommand<string, Unit> NavigateToCommand { get; }

    /// <summary>Enters an activated directory row.</summary>
    public ReactiveCommand<LocalFileEntry, Unit> ActivateCommand { get; }

    /// <summary>Navigates to the parent directory.</summary>
    public ReactiveCommand<Unit, Unit> GoUpCommand { get; }

    /// <summary>Refreshes the current directory listing.</summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Switches the current directory to a selected root.</summary>
    public ReactiveCommand<LocalRootEntry, Unit> SwitchRootCommand { get; }

    /// <summary>Reloads the list of available local roots.</summary>
    public ReactiveCommand<Unit, Unit> RefreshRootsCommand { get; }

    /// <summary>Confirms and permanently deletes one local entry.</summary>
    public ReactiveCommand<LocalFileEntry, Unit> DeleteItemCommand { get; }

    /// <summary>Confirms and permanently deletes the selected local entries.</summary>
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

    /// <summary>Host-provided upload delegate for the standalone SFTP document.</summary>
    public ReactiveCommand<Unit, Unit> UploadSelectedCommand { get; }

    /// <summary>Renames a selected local entry in-place.</summary>
    public ReactiveCommand<LocalFileEntry, Unit> RenameCommand { get; }

    /// <summary>Creates a new folder in the current directory.</summary>
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }

    /// <summary>Opens a local file with the default program or navigates into a directory.</summary>
    public ReactiveCommand<LocalFileEntry, Unit> OpenItemCommand { get; }

    /// <summary>Host-provided upload delegate invoked by UploadSelectedCommand.</summary>
    public Func<Task>? UploadSelectedAsync { get; set; }

    /// <summary>Sorts visible rows by name, size, or modified time.</summary>
    public ReactiveCommand<string, Unit> SortCommand { get; }

    /// <summary>Injected destructive-action confirmation callback.</summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>Set by the view: prompts for a line of text (title, initial value) → entered text or null.</summary>
    public Func<string, string, Task<string?>>? PromptForText { get; set; }

    /// <summary>Set by the view: opens a local file with the OS default program.</summary>
    public Func<string, Task>? OpenLocalFile { get; set; }

    /// <summary>Cancels local work before the document drains and closes its SFTP channel.</summary>
    public void Detach() => _lifetime.Cancel();

    /// <summary>The canonical directory currently listed.</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (_currentPath == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _currentPath, value);
            this.RaisePropertyChanged(nameof(Breadcrumbs));
            this.RaisePropertyChanged(nameof(RootBreadcrumbLabel));
            this.RaisePropertyChanged(nameof(RootBreadcrumbPath));
        }
    }

    /// <summary>Clickable breadcrumb segments for the current path, deepest last. Root is handled by RootBreadcrumbLabel.</summary>
    public IReadOnlyList<LocalBreadcrumbSegment> Breadcrumbs
    {
        get
        {
            string root = RootBreadcrumbLabel;
            if (string.IsNullOrEmpty(CurrentPath) || CurrentPath == root)
            {
                return [];
            }

            // Guard: RootBreadcrumbLabel may resolve to a place-holder root when CurrentPath is still
            // empty, so the slice below would fail with startIndex > length.
            if (root.Length >= CurrentPath.Length || !CurrentPath.StartsWith(root, PathComparison))
            {
                return [];
            }

            char sep = Path.DirectorySeparatorChar;
            ReadOnlySpan<char> relative = CurrentPath.AsSpan(root.Length).TrimStart(sep);
            if (relative.Length == 0)
            {
                return [];
            }

            string[] parts = relative.ToString().Split(sep, StringSplitOptions.RemoveEmptyEntries);
            return [.. parts.Select((part, i) =>
            {
                string path = root.TrimEnd(sep) + sep + string.Join(sep, parts.Take(i + 1));
                return new LocalBreadcrumbSegment(part, path);
            })];
        }
    }

    /// <summary>Label for the root breadcrumb button (e.g. "/" on Linux, "C:\" on Windows).</summary>
    public string RootBreadcrumbLabel
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath))
            {
                return OperatingSystem.IsWindows() ? "C:\\" : "/";
            }
            string? root = Path.GetPathRoot(CurrentPath);
            if (string.IsNullOrEmpty(root))
            {
                return "/";
            }
            // Ensure Windows roots end with a separator (e.g. "C:\" not "C:")
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
    }

    /// <summary>Path for the root breadcrumb button navigation.</summary>
    public string RootBreadcrumbPath => RootBreadcrumbLabel;

    /// <summary>OS path separator character for breadcrumb display.</summary>
    public string PathSeparator => Path.DirectorySeparatorChar.ToString();

    /// <summary>Whether a directory listing is in progress.</summary>
    public bool IsDirectoryLoading
    {
        get => _isDirectoryLoading;
        private set => this.RaiseAndSetIfChanged(ref _isDirectoryLoading, value);
    }

    /// <summary>The most recent listing or deletion error.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>The active sort column.</summary>
    public string SortColumn
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "name";

    /// <summary>Whether the active sort is descending.</summary>
    public bool SortDescending
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Loads the configured directory, falling back to the user home directory.</summary>
    public async Task LoadInitialAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRootsAsync(cancellationToken);
        string configured;
        try
        {
            configured = ExpandConfiguredPath(_transferOptions.LocalDownloadDirectory);
        }
        catch (ArgumentException)
        {
            configured = string.Empty;
        }
        catch (NotSupportedException)
        {
            configured = string.Empty;
        }
        string home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (string candidate in new[] { configured, home }.Distinct(StringComparer.Ordinal))
        {
            if (!DirectoryIsAccessible(candidate))
            {
                continue;
            }

            await NavigateToAsync(candidate, cancellationToken);
            if (ErrorMessage is null)
            {
                return;
            }
        }

        CurrentPath = home;
        SyncSelectedRoot(home);
    }

    /// <summary>Reloads the local roots list, preserving the current path selection.</summary>
    public async Task RefreshRootsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LocalRootEntry> roots = await _rootProvider.EnumerateAsync(cancellationToken);
        _roots.Clear();
        foreach (LocalRootEntry root in roots)
        {
            _roots.Add(root);
        }
        SyncSelectedRoot(CurrentPath);
    }

    /// <summary>Switches to the path of the selected root if it is accessible.</summary>
    public async Task SwitchRootAsync(LocalRootEntry? root, CancellationToken cancellationToken = default)
    {
        if (_isSwitchingRoot || root is null || !root.IsAccessible)
        {
            return;
        }

        _isSwitchingRoot = true;
        try
        {
            await NavigateToAsync(root.FullPath, cancellationToken);
        }
        finally
        {
            _isSwitchingRoot = false;
        }
    }

    /// <summary>Lists a directory and retains the previous rows when listing fails.</summary>
    public async Task NavigateToAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonical = Path.GetFullPath(path);
        long version = Interlocked.Increment(ref _navigationVersion);
        try
        {
            ErrorMessage = null;
            IsDirectoryLoading = true;
            List<LocalFileEntry> listed = await ListDirectoryAsync(canonical, cancellationToken);
            if (version != Volatile.Read(ref _navigationVersion))
            {
                return;
            }

            bool changed = !string.Equals(CurrentPath, canonical, PathComparison);
            HashSet<string> selected = SelectedEntries
                .Where(entry => !entry.IsParentEntry)
                .Select(entry => entry.FullPath)
                .ToHashSet(PathComparisonComparer);
            CurrentPath = canonical;
            if (changed)
            {
                SelectedEntries.Clear();
            }
            RebuildEntries(listed);
            SyncSelectedRoot(canonical);
            if (!changed)
            {
                RestoreSelection(selected);
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _navigationVersion))
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _navigationVersion))
            {
                IsDirectoryLoading = false;
            }
        }
    }

    /// <summary>Refreshes the current directory or performs initial loading.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(CurrentPath)
            ? LoadInitialAsync(cancellationToken)
            : NavigateToAsync(CurrentPath, cancellationToken);

    /// <summary>Navigates to the parent directory, staying at the filesystem root.</summary>
    public Task GoUpAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(CurrentPath)
            ? LoadInitialAsync(cancellationToken)
            : NavigateToAsync(ParentOf(CurrentPath), cancellationToken);

    /// <summary>Enters a directory entry; files and the parent row are inert.</summary>
    public Task ActivateAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default) =>
        entry is { IsDirectory: true } ? NavigateToAsync(entry.FullPath, cancellationToken) : Task.CompletedTask;

    /// <summary>Confirms and permanently deletes one entry without following links.</summary>
    public async Task DeleteItemAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        if (entry is null || entry.IsParentEntry || ConfirmDelete is null)
        {
            return;
        }

        if (!await ConfirmDelete($"Delete '{entry.Name}' permanently?"))
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            await DeletePathAsync(ToFileSystemEntry(entry), cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Confirms and permanently deletes selected entries without following links.</summary>
    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        LocalFileEntry[] targets = SelectedEntries.Where(entry => !entry.IsParentEntry).ToArray();
        if (targets.Length == 0 || ConfirmDelete is null || !await ConfirmDelete($"Delete {targets.Length} item(s) permanently?"))
        {
            return;
        }

        foreach (LocalFileEntry target in targets)
        {
            try
            {
                await DeletePathAsync(ToFileSystemEntry(target), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = null;
                break;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                break;
            }
        }

        await RefreshAsync(cancellationToken);
    }

    private async Task<List<LocalFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<LocalFileSystemEntry> entries = await _fileSystem.EnumerateAsync(
            path,
            cancellationToken
        );
        return [
            .. entries.Select(entry => new LocalFileEntry(
                entry.Name,
                entry.FullPath,
                entry.IsDirectory,
                entry.SizeBytes,
                entry.LastModified,
                entry.IsReparsePoint
            )),
        ];
    }

    private async Task DeletePathAsync(
        LocalFileSystemEntry entry,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.IsReparsePoint)
        {
            await DeleteSingleEntryAsync(entry, cancellationToken);
            return;
        }

        if (!entry.IsDirectory)
        {
            await _fileSystem.DeleteFileAsync(entry.FullPath, cancellationToken);
            return;
        }

        IReadOnlyList<LocalFileSystemEntry> children = await _fileSystem.EnumerateAsync(
            entry.FullPath,
            cancellationToken
        );
        foreach (LocalFileSystemEntry child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeletePathAsync(child, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _fileSystem.DeleteDirectoryAsync(entry.FullPath, cancellationToken);
    }

    private async Task DeleteSingleEntryAsync(
        LocalFileSystemEntry entry,
        CancellationToken cancellationToken
    )
    {
        if (entry.IsDirectory)
        {
            await _fileSystem.DeleteDirectoryAsync(entry.FullPath, cancellationToken);
        }
        else
        {
            await _fileSystem.DeleteFileAsync(entry.FullPath, cancellationToken);
        }
    }

    private static LocalFileSystemEntry ToFileSystemEntry(LocalFileEntry entry) =>
        new(
            entry.Name,
            entry.FullPath,
            entry.IsDirectory,
            entry.SizeBytes,
            entry.LastModified,
            entry.IsReparsePoint
        );

    private void ToggleSort(string? column)
    {
        if (string.IsNullOrWhiteSpace(column) || column is not ("name" or "size" or "modified"))
        {
            return;
        }
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }
        RebuildEntries(_entries.Where(entry => !entry.IsParentEntry));
    }

    private void RebuildEntries(IEnumerable<LocalFileEntry> entries)
    {
        IEnumerable<LocalFileEntry> sorted = entries.Where(entry => !entry.IsParentEntry);
        IOrderedEnumerable<LocalFileEntry> directories = sorted.OrderByDescending(entry => entry.IsDirectory);
        sorted = SortColumn switch
        {
            "size" => SortDescending ? directories.ThenByDescending(entry => entry.SizeBytes) : directories.ThenBy(entry => entry.SizeBytes),
            "modified" => SortDescending ? directories.ThenByDescending(entry => entry.LastModified) : directories.ThenBy(entry => entry.LastModified),
            _ => SortDescending ? directories.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase) : directories.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };
        List<LocalFileEntry> rebuilt = string.Equals(
            CurrentPath,
            Path.GetPathRoot(CurrentPath),
            PathComparison
        )
            ? [.. sorted]
            : [LocalFileEntry.CreateParent(ParentOf(CurrentPath)), .. sorted];
        _entries.ReplaceAll(rebuilt);
    }

    private void SyncSelectedRoot(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        LocalRootEntry? root = Roots
            .Where(candidate => candidate.IsAccessible && IsPathWithin(candidate.FullPath, path))
            .OrderByDescending(candidate => candidate.FullPath.Length)
            .FirstOrDefault();
        if (!ReferenceEquals(SelectedRoot, root))
        {
            SelectedRoot = root;
        }
    }

    private static bool IsPathWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string canonicalPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(canonicalRoot, canonicalPath, PathComparison)
            || canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison)
            || canonicalPath.StartsWith(canonicalRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private void RestoreSelection(HashSet<string> selectedPaths)
    {
        SelectedEntries.Clear();
        foreach (LocalFileEntry entry in Entries.Where(entry => selectedPaths.Contains(entry.FullPath)))
        {
            SelectedEntries.Add(entry);
        }
    }

    private static string ExpandConfiguredPath(string configured)
    {
        string value = configured?.Trim() ?? string.Empty;
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value[1..].TrimStart('/', '\\')
            );
        }
        return string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value);
    }

    private static bool DirectoryIsAccessible(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return false;
            }

            using IEnumerator<FileSystemInfo> entries = new DirectoryInfo(path)
                .EnumerateFileSystemInfos()
                .GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string ParentOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        ?? Path.GetPathRoot(path)
        ?? path;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparisonComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task RenameAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        entry ??= SelectedEntries.FirstOrDefault(static e => !e.IsParentEntry);
        if (PromptForText is null || entry is null || entry.IsParentEntry)
        {
            return;
        }
        string? newName = await PromptForText("Rename", entry.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == entry.Name)
        {
            return;
        }
        string target = Path.Combine(ParentOf(entry.FullPath), newName.Trim());
        try
        {
            ErrorMessage = null;
            if (entry.IsDirectory)
            {
                Directory.Move(entry.FullPath, target);
            }
            else
            {
                File.Move(entry.FullPath, target);
            }
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task NewFolderAsync(CancellationToken cancellationToken = default)
    {
        if (PromptForText is null)
        {
            return;
        }
        string? name = await PromptForText("New Folder", "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        string target = Path.Combine(CurrentPath, name.Trim());
        try
        {
            ErrorMessage = null;
            Directory.CreateDirectory(target);
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task OpenItemAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        entry ??= SelectedEntries.FirstOrDefault(static e => !e.IsParentEntry);
        if (entry is null || entry.IsParentEntry)
        {
            return;
        }
        if (entry.IsDirectory)
        {
            await NavigateToAsync(entry.FullPath, cancellationToken);
        }
        else if (OpenLocalFile is not null)
        {
            await OpenLocalFile(entry.FullPath);
        }
    }
}

/// <summary>One clickable segment of the local path breadcrumb.</summary>
public sealed record LocalBreadcrumbSegment(string Name, string Path);
