using System.Collections.ObjectModel;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

/// <summary>
/// A single entry in the command palette — either a session to connect to or an action to run.
/// </summary>
public sealed class CommandPaletteItem : ReactiveObject
{
    private bool _isSelected;

    public CommandPaletteItem(
        string category,
        string title,
        Action invoke,
        string? hint = null,
        string? tag = null,
        bool isSession = false)
    {
        Category = category;
        Title = title;
        Invoke = invoke;
        Hint = hint;
        Tag = tag;
        IsSession = isSession;
    }

    /// <summary>Grouping bucket shown as a section header (e.g. "会话", "命令").</summary>
    public string Category { get; }

    public string Title { get; }

    /// <summary>Trailing hint text: a keyboard shortcut, or "Enter 连接" for sessions.</summary>
    public string? Hint { get; }

    /// <summary>Optional coloured badge (e.g. an environment tag).</summary>
    public string? Tag { get; }

    public bool IsSession { get; }

    public Action Invoke { get; }

    /// <summary>True when this item is the current keyboard selection (drives the highlight).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>A category section of palette results.</summary>
public sealed class CommandPaletteGroup
{
    public CommandPaletteGroup(string category)
    {
        Category = category;
        Items = new ObservableCollection<CommandPaletteItem>();
    }

    public string Category { get; }

    public ObservableCollection<CommandPaletteItem> Items { get; }
}
