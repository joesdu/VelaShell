using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Json;
using ReactiveUI;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

public class QuickCommandsViewModel : ReactiveObject
{
    private const string Collection = "quick_commands";
    private const string DocumentId = "commands";

    private readonly IAppDataStore _dataStore;
    private readonly Action<string>? _executeCallback;
    private readonly string? _legacyDataPath;

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

    public ObservableCollection<QuickCommandViewModel> AllCommands { get; }

    public ObservableCollection<QuickCommandViewModel> FilteredCommands { get; }

    public ObservableCollection<string> Categories { get; }

    public string SearchQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public bool IsAddingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string NewName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string NewCategory
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string NewCommandText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string NewDescription
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public QuickCommandViewModel? EditingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<QuickCommandViewModel, Unit> ExecuteCommandCommand { get; }

    public ReactiveCommand<Unit, Unit> AddCommandCommand { get; }

    public ReactiveCommand<QuickCommandViewModel, Unit> DeleteCommandCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveNewCommandCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelAddCommand { get; }

    public ReactiveCommand<QuickCommandViewModel, Unit> BeginEditCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveEditCommand { get; }

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
            QuickCommandData? data = JsonSerializer.Deserialize<QuickCommandData>(json,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                });
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
