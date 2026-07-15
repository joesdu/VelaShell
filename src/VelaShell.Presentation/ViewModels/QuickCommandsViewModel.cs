using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Json;
using ReactiveUI;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Presentation.ViewModels;

/// <summary>快捷命令目录视图模型:管理内置与自定义命令的加载、筛选及增删改。</summary>
public class QuickCommandsViewModel : ReactiveObject
{
    private const string Collection = "quick_commands";
    private const string DocumentId = "commands";

    private readonly IAppDataStore _dataStore;
    private readonly string? _legacyDataPath;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private bool _loaded;

    /// <summary>创建快速命令视图模型,并加载内置命令目录。</summary>
    /// <param name="dataStore">用于持久化自定义命令的应用数据存储。</param>
    /// <param name="legacyDataPath">旧版 quick-commands.json 的路径,用于一次性迁移导入;为空时使用默认位置。</param>
    public QuickCommandsViewModel(IAppDataStore dataStore, string? legacyDataPath = null)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _legacyDataPath =
            legacyDataPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".velashell",
                "quick-commands.json"
            );
        AllCommands = [];
        FilteredCommands = [];
        Categories = [];
        AddCommandCommand = ReactiveCommand.Create(AddCommand);
        DeleteCommandCommand = ReactiveCommand.CreateFromTask<QuickCommandViewModel>(
            DeleteCommandAsync
        );
        SaveNewCommandCommand = ReactiveCommand.CreateFromTask(SaveNewCommandAsync);
        CancelAddCommand = ReactiveCommand.Create(CancelAdd);
        BeginEditCommand = ReactiveCommand.Create<QuickCommandViewModel>(BeginEdit);
        SaveEditCommand = ReactiveCommand.CreateFromTask(SaveEditAsync);
        CancelEditCommand = ReactiveCommand.Create(CancelEdit);
        this.WhenAnyValue(vm => vm.SearchQuery).Subscribe(_ => ApplyFilter());
        LoadBuiltInCommands();
    }

    /// <summary>全部命令(内置 + 自定义)的集合。</summary>
    public ObservableCollection<QuickCommandViewModel> AllCommands { get; }

    /// <summary>经搜索条件筛选后、用于界面展示的命令集合。</summary>
    public ObservableCollection<QuickCommandViewModel> FilteredCommands { get; }

    /// <summary>去重排序后的命令分类列表。</summary>
    public ObservableCollection<string> Categories { get; }

    /// <summary>搜索关键字,更改时会自动重新筛选命令列表。</summary>
    public string SearchQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否处于新增命令的输入状态。</summary>
    public bool IsAddingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>新增或编辑命令时输入的命令名称。</summary>
    public string NewName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑命令时输入的分类。</summary>
    public string NewCategory
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑命令时输入的命令文本。</summary>
    public string NewCommandText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑命令时输入的描述。</summary>
    public string NewDescription
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>当前正在编辑的命令;为空表示未处于编辑状态。</summary>
    public QuickCommandViewModel? EditingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>进入新增命令输入状态的命令。</summary>
    public ReactiveCommand<Unit, Unit> AddCommandCommand { get; }

    /// <summary>删除指定自定义命令的命令。</summary>
    public ReactiveCommand<QuickCommandViewModel, Unit> DeleteCommandCommand { get; }

    /// <summary>保存新增命令的命令。</summary>
    public ReactiveCommand<Unit, Unit> SaveNewCommandCommand { get; }

    /// <summary>取消新增命令的命令。</summary>
    public ReactiveCommand<Unit, Unit> CancelAddCommand { get; }

    /// <summary>开始编辑指定自定义命令的命令。</summary>
    public ReactiveCommand<QuickCommandViewModel, Unit> BeginEditCommand { get; }

    /// <summary>保存对命令所做编辑的命令。</summary>
    public ReactiveCommand<Unit, Unit> SaveEditCommand { get; }

    /// <summary>取消当前编辑的命令。</summary>
    public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }

    private void LoadBuiltInCommands()
    {
        // 内置目录移到 Core(QuickCommandCatalog),与命令补全建议共用一份数据。
        foreach (QuickCommand cmd in QuickCommandCatalog.BuiltIns)
        {
            AllCommands.Add(new(cmd));
        }
        RefreshCategories();
        ApplyFilter();
    }

    /// <summary>从数据存储(或旧版文件迁移)异步加载自定义命令并刷新列表。</summary>
    /// <returns>表示异步加载操作的任务。</returns>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }
        await _loadGate.WaitAsync();
        try
        {
            if (_loaded)
            {
                return;
            }
            QuickCommandData? data =
                await _dataStore.GetAsync<QuickCommandData>(Collection, DocumentId)
                ?? await TryImportLegacyAsync();
            if (data?.Commands is not null)
            {
                foreach (QuickCommand cmd in data.Commands)
                {
                    cmd.IsBuiltIn = false;
                    AllCommands.Add(new(cmd));
                }
            }
            _loaded = true;
            RefreshCategories();
            ApplyFilter();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>兼容旧调用名;加载过程本身幂等,重复调用不会重复追加自定义片段。</summary>
    public Task LoadCustomCommandsAsync() => LoadAsync();

    /// <summary>首次运行时从旧版 quick-commands.json 一次性导入到 SonnetDB。</summary>
    private async Task<QuickCommandData?> TryImportLegacyAsync()
    {
        if (string.IsNullOrEmpty(_legacyDataPath) || !File.Exists(_legacyDataPath))
        {
            return null;
        }
        try
        {
            string json = await File.ReadAllTextAsync(_legacyDataPath);
            QuickCommandData? data = JsonSerializer.Deserialize<QuickCommandData>(
                json,
                _jsonOptions
            );
            if (data is not null)
            {
                await _dataStore.UpsertAsync(Collection, DocumentId, data);
            }
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private async Task SaveCustomCommandsAsync()
    {
        var customCommands = AllCommands.Where(c => !c.IsBuiltIn).Select(c => c.ToModel()).ToList();
        var data = new QuickCommandData { Commands = customCommands };
        await _dataStore.UpsertAsync(Collection, DocumentId, data);
    }

    private void AddCommand()
    {
        IsAddingCommand = true;
        NewName = string.Empty;
        NewCategory = "Custom";
        NewCommandText = string.Empty;
        NewDescription = string.Empty;
    }

    private async Task SaveNewCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewCommandText))
        {
            return;
        }
        var model = new QuickCommand
        {
            Name = NewName.Trim(),
            Category = string.IsNullOrWhiteSpace(NewCategory) ? "Custom" : NewCategory.Trim(),
            CommandText = NewCommandText.Trim(),
            Description = NewDescription.Trim(),
            IsBuiltIn = false,
        };
        AllCommands.Add(new(model));
        IsAddingCommand = false;
        RefreshCategories();
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private async Task DeleteCommandAsync(QuickCommandViewModel command)
    {
        if (command.IsBuiltIn)
        {
            return;
        }
        AllCommands.Remove(command);
        FilteredCommands.Remove(command);
        RefreshCategories();
        await SaveCustomCommandsAsync();
    }

    private void BeginEdit(QuickCommandViewModel command)
    {
        if (command.IsBuiltIn)
        {
            return;
        }
        EditingCommand = command;
        NewName = command.Name;
        NewCategory = command.Category;
        NewCommandText = command.CommandText;
        NewDescription = command.Description;
    }

    private async Task SaveEditAsync()
    {
        if (EditingCommand == null || EditingCommand.IsBuiltIn)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewCommandText))
        {
            return;
        }
        EditingCommand.Name = NewName.Trim();
        EditingCommand.Category = string.IsNullOrWhiteSpace(NewCategory)
            ? "Custom"
            : NewCategory.Trim();
        EditingCommand.CommandText = NewCommandText.Trim();
        EditingCommand.Description = NewDescription.Trim();
        EditingCommand = null;
        RefreshCategories();
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private void CancelEdit() => EditingCommand = null;

    private void CancelAdd() => IsAddingCommand = false;

    private void ApplyFilter()
    {
        FilteredCommands.Clear();
        string query = SearchQuery.Trim();
        foreach (QuickCommandViewModel cmd in AllCommands)
        {
            if (
                string.IsNullOrEmpty(query)
                || cmd.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || cmd.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || cmd.CommandText.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            {
                FilteredCommands.Add(cmd);
            }
        }
    }

    private void RefreshCategories()
    {
        Categories.Clear();
        IOrderedEnumerable<string> cats = AllCommands
            .Select(c => c.Category)
            .Distinct()
            .OrderBy(c => c);
        foreach (string cat in cats)
        {
            Categories.Add(cat);
        }
    }
}
