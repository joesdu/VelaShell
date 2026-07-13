using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Json;
using ReactiveUI;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

/// <summary>快速命令面板的视图模型:管理内置与自定义命令的加载、筛选、增删改及执行。</summary>
public class QuickCommandsViewModel : ReactiveObject
{
    private const string Collection = "quick_commands";
    private const string DocumentId = "commands";

    private readonly IAppDataStore _dataStore;
    private readonly Action<string>? _executeCallback;
    private readonly string? _legacyDataPath;
    private readonly JsonSerializerOptions jsonOption = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>创建快速命令视图模型,并加载内置命令目录。</summary>
    /// <param name="dataStore">用于持久化自定义命令的应用数据存储。</param>
    /// <param name="executeCallback">执行命令时用于将命令文本发送到终端的回调。</param>
    /// <param name="legacyDataPath">旧版 quick-commands.json 的路径,用于一次性迁移导入;为空时使用默认位置。</param>
    public QuickCommandsViewModel(
        IAppDataStore dataStore,
        Action<string>? executeCallback = null,
        string? legacyDataPath = null)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _executeCallback = executeCallback;
        _legacyDataPath = legacyDataPath ??
                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                              ".velashell", "quick-commands.json");
        AllCommands = [];
        FilteredCommands = [];
        Categories = [];
        ExecuteCommandCommand = ReactiveCommand.Create<QuickCommandViewModel>(ExecuteCommand);
        AddCommandCommand = ReactiveCommand.Create(AddCommand);
        DeleteCommandCommand = ReactiveCommand.Create<QuickCommandViewModel>(DeleteCommand);
        SaveNewCommandCommand = ReactiveCommand.Create(SaveNewCommand);
        CancelAddCommand = ReactiveCommand.Create(CancelAdd);
        BeginEditCommand = ReactiveCommand.Create<QuickCommandViewModel>(BeginEdit);
        SaveEditCommand = ReactiveCommand.Create(SaveEdit);
        CancelEditCommand = ReactiveCommand.Create(CancelEdit);
        this.WhenAnyValue(vm => vm.SearchQuery)
            .Subscribe(_ => ApplyFilter());
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

    /// <summary>执行选中命令、将其文本发送到终端的命令。</summary>
    public ReactiveCommand<QuickCommandViewModel, Unit> ExecuteCommandCommand { get; }

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
    public async Task LoadCustomCommandsAsync()
    {
        QuickCommandData? data = await _dataStore.GetAsync<QuickCommandData>(Collection, DocumentId) ?? await TryImportLegacyAsync();
        if (data?.Commands != null)
        {
            foreach (QuickCommand cmd in data.Commands)
            {
                cmd.IsBuiltIn = false;
                AllCommands.Add(new(cmd));
            }
        }
        RefreshCategories();
        ApplyFilter();
    }

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
            QuickCommandData? data = JsonSerializer.Deserialize<QuickCommandData>(json, jsonOption);
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
        var customCommands = AllCommands
                             .Where(c => !c.IsBuiltIn)
                             .Select(c => c.ToModel())
                             .ToList();
        var data = new QuickCommandData { Commands = customCommands };
        await _dataStore.UpsertAsync(Collection, DocumentId, data);
    }

    private void ExecuteCommand(QuickCommandViewModel command) => _executeCallback?.Invoke(command.CommandText);

    private void AddCommand()
    {
        IsAddingCommand = true;
        NewName = string.Empty;
        NewCategory = "Custom";
        NewCommandText = string.Empty;
        NewDescription = string.Empty;
    }

    private void SaveNewCommand()
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
            IsBuiltIn = false
        };
        AllCommands.Add(new(model));
        IsAddingCommand = false;
        RefreshCategories();
        ApplyFilter();
        SaveCustomCommandsAsync().GetAwaiter().GetResult();
    }

    private void DeleteCommand(QuickCommandViewModel command)
    {
        if (command.IsBuiltIn)
        {
            return;
        }
        AllCommands.Remove(command);
        FilteredCommands.Remove(command);
        RefreshCategories();
        SaveCustomCommandsAsync().GetAwaiter().GetResult();
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

    private void SaveEdit()
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
        EditingCommand.Category = string.IsNullOrWhiteSpace(NewCategory) ? "Custom" : NewCategory.Trim();
        EditingCommand.CommandText = NewCommandText.Trim();
        EditingCommand.Description = NewDescription.Trim();
        EditingCommand = null;
        RefreshCategories();
        ApplyFilter();
        SaveCustomCommandsAsync().GetAwaiter().GetResult();
    }

    private void CancelEdit() => EditingCommand = null;

    private void CancelAdd() => IsAddingCommand = false;

    private void ApplyFilter()
    {
        FilteredCommands.Clear();
        string query = SearchQuery.Trim();
        foreach (QuickCommandViewModel cmd in AllCommands)
        {
            if (string.IsNullOrEmpty(query) ||
                cmd.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                cmd.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                cmd.CommandText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCommands.Add(cmd);
            }
        }
    }

    private void RefreshCategories()
    {
        Categories.Clear();
        IOrderedEnumerable<string> cats = AllCommands.Select(c => c.Category).Distinct().OrderBy(c => c);
        foreach (string cat in cats)
        {
            Categories.Add(cat);
        }
    }
}
