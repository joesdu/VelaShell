using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;

namespace VelaShell.ViewModels;

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

    /// <summary>
    /// Creates the palette view model and wires up its keyboard/mouse commands.
    /// </summary>
    /// <param name="itemsProvider">Supplies the current palette items on demand; when null an empty list is used.</param>
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

    /// <summary>The filtered items arranged into category groups for display.</summary>
    public ObservableCollection<CommandPaletteGroup> Groups { get; }

    /// <summary>The current search text; changing it re-filters the item list.</summary>
    public string Query
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>Whether the palette overlay is currently visible.</summary>
    public bool IsOpen
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The currently highlighted item; keeps the item's own selection flag in sync.</summary>
    public CommandPaletteItem? SelectedItem
    {
        get;
        private set
        {
            field?.IsSelected = false;
            this.RaiseAndSetIfChanged(ref field, value);
            field?.IsSelected = true;
            this.RaisePropertyChanged(nameof(HasResults));
        }
    }

    /// <summary>The number of items currently matching the query.</summary>
    public int ResultCount => _flat.Count;

    /// <summary>Whether any item currently matches the query.</summary>
    public bool HasResults => _flat.Count > 0;

    /// <summary>Moves the selection to the next matching item.</summary>
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }

    /// <summary>Moves the selection to the previous matching item.</summary>
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }

    /// <summary>Runs the currently selected item and closes the palette.</summary>
    public ReactiveCommand<Unit, Unit> ExecuteSelectedCommand { get; }

    /// <summary>Selects and immediately runs the supplied item (mouse activation).</summary>
    public ReactiveCommand<CommandPaletteItem, Unit> ActivateCommand { get; }

    /// <summary>Closes the palette overlay without running anything.</summary>
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>Reloads items from the provider, clears the query and shows the palette.</summary>
    public void Open()
    {
        _all = _itemsProvider();
        Query = string.Empty;
        Rebuild();
        IsOpen = true;
    }

    /// <summary>Hides the palette overlay.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Advances the selection to the next matching item, wrapping around.</summary>
    public void MoveDown() => Move(1);

    /// <summary>Advances the selection to the previous matching item, wrapping around.</summary>
    public void MoveUp() => Move(-1);

    private void Move(int delta)
    {
        if (_flat.Count == 0)
        {
            return;
        }
        int index = SelectedItem is null ? -1 : _flat.IndexOf(SelectedItem);
        index = (index + delta + _flat.Count) % _flat.Count;
        SelectedItem = _flat[index];
    }

    /// <summary>Closes the palette and invokes the currently selected item, if any.</summary>
    public void ExecuteSelected()
    {
        CommandPaletteItem? item = SelectedItem;
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
        _flat.Clear();
        string query = Query.Trim();

        // 每键入一字符全量重建;分组在挂入 Groups 之前先装配完 Items——组一旦在
        // 可见集合里,逐项 Add 会给面板每条结果发一次 CollectionChanged+布局,
        // 会话/命令上千条时按键卡顿。离线装配后每组只挂一次。
        var byCategory = new Dictionary<string, CommandPaletteGroup>();
        var ordered = new List<CommandPaletteGroup>();
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
                ordered.Add(group);
            }
            group.Items.Add(item);
            _flat.Add(item);
        }
        Groups.Clear();
        foreach (CommandPaletteGroup group in ordered)
        {
            Groups.Add(group);
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
