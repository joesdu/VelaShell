using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;

namespace VelaShell.App.ViewModels;

/// <summary>
/// The command palette (Ctrl+P / Ctrl+K): a fuzzy-searchable, keyboard-driven overlay that
/// lists sessions and actions grouped by category. The item source is provided by the host so
/// the palette stays decoupled and unit-testable.
/// </summary>
public sealed class CommandPaletteViewModel : ReactiveObject
{
    private readonly List<CommandPaletteItem> _flat = [];
    private readonly Func<IReadOnlyList<CommandPaletteItem>> _itemsProvider;
    private IReadOnlyList<CommandPaletteItem> _all = [];

    private string _query = string.Empty;
    private CommandPaletteItem? _selectedItem;

    public CommandPaletteViewModel(Func<IReadOnlyList<CommandPaletteItem>>? itemsProvider = null)
    {
        _itemsProvider = itemsProvider ?? (() => []);
        Groups = [];
        MoveDownCommand = ReactiveCommand.Create(MoveDown);
        MoveUpCommand = ReactiveCommand.Create(MoveUp);
        ExecuteSelectedCommand = ReactiveCommand.Create(ExecuteSelected);
        ActivateCommand = ReactiveCommand.Create<CommandPaletteItem>(Activate);
        CloseCommand = ReactiveCommand.Create(Close);
        this.WhenAnyValue(x => x.Query).Subscribe(_ => Rebuild());
    }

    public ObservableCollection<CommandPaletteGroup> Groups { get; }

    public string Query
    {
        get => _query;
        set => this.RaiseAndSetIfChanged(ref _query, value);
    }

    public bool IsOpen
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public CommandPaletteItem? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            _selectedItem?.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedItem, value);
            _selectedItem?.IsSelected = true;
            this.RaisePropertyChanged(nameof(HasResults));
        }
    }

    public int ResultCount => _flat.Count;

    public bool HasResults => _flat.Count > 0;

    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }

    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }

    public ReactiveCommand<Unit, Unit> ExecuteSelectedCommand { get; }

    public ReactiveCommand<CommandPaletteItem, Unit> ActivateCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>Reloads items from the provider, clears the query and shows the palette.</summary>
    public void Open()
    {
        _all = _itemsProvider();
        Query = string.Empty;
        Rebuild();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void MoveDown() => Move(1);

    public void MoveUp() => Move(-1);

    private void Move(int delta)
    {
        if (_flat.Count == 0)
        {
            return;
        }
        int index = _selectedItem is null ? -1 : _flat.IndexOf(_selectedItem);
        index = (index + delta + _flat.Count) % _flat.Count;
        SelectedItem = _flat[index];
    }

    public void ExecuteSelected()
    {
        CommandPaletteItem? item = _selectedItem;
        if (item is null)
        {
            return;
        }
        Close();
        item.Invoke();
    }

    /// <summary>Selects and immediately runs an item (used for mouse clicks).</summary>
    public void Activate(CommandPaletteItem item)
    {
        SelectedItem = item;
        ExecuteSelected();
    }

    private void Rebuild()
    {
        Groups.Clear();
        _flat.Clear();
        string query = _query.Trim();
        var byCategory = new Dictionary<string, CommandPaletteGroup>();
        foreach (CommandPaletteItem item in _all)
        {
            if (!Matches(item, query))
            {
                continue;
            }
            if (!byCategory.TryGetValue(item.Category, out CommandPaletteGroup? group))
            {
                group = new(item.Category);
                byCategory[item.Category] = group;
                Groups.Add(group);
            }
            group.Items.Add(item);
            _flat.Add(item);
        }
        SelectedItem = _flat.Count > 0 ? _flat[0] : null;
        this.RaisePropertyChanged(nameof(ResultCount));
        this.RaisePropertyChanged(nameof(HasResults));
    }

    private static bool Matches(CommandPaletteItem item, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }
        return item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || (item.Hint?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || Fuzzy(item.Title, query);
    }

    /// <summary>Subsequence fuzzy match: every query char appears in order within the title.</summary>
    private static bool Fuzzy(string title, string query)
    {
        int q = 0;
        foreach (char c in title)
        {
            if (q < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[q]))
            {
                q++;
            }
        }
        return q == query.Length;
    }
}
