using System.Collections.ObjectModel;
using ReactiveUI;

namespace VelaShell.ViewModels;

/// <summary>
/// A single entry in the command palette — either a session to connect to or an action to run.
/// </summary>
public sealed class CommandPaletteItem(
    string category,
    string title,
    Action invoke,
    string? hint = null,
    string? tag = null,
    bool isSession = false)
    : ReactiveObject
{
    /// <summary>Grouping bucket shown as a section header (e.g. "会话", "命令").</summary>
    public string Category { get; } = category;

    public string Title { get; } = title;

    /// <summary>Trailing hint text: a keyboard shortcut, or "Enter 连接" for sessions.</summary>
    public string? Hint { get; } = hint;

    /// <summary>Optional coloured badge (e.g. an environment tag).</summary>
    public string? Tag { get; } = tag;

    public bool IsSession { get; } = isSession;

    public Action Invoke { get; } = invoke;

    /// <summary>True when this item is the current keyboard selection (drives the highlight).</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>A category section of palette results.</summary>
public sealed class CommandPaletteGroup(string category)
{
    public string Category { get; } = category;

    public ObservableCollection<CommandPaletteItem> Items { get; } = [];
}
