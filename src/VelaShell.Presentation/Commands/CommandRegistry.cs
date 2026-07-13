namespace VelaShell.Presentation.Commands;

/// <summary>
/// One executable app command. The registry is the single source shared by the menu bar,
/// the command palette and keyboard shortcuts (design spec §4A.1), so every entry point
/// shows the same name, hint and behavior.
/// </summary>
/// <param name="Id">Stable unique identifier used to register, find and invoke the command.</param>
/// <param name="Title">Human-readable, localized display name shown to the user.</param>
/// <param name="Category">Grouping label (e.g. for the command palette or menu section).</param>
/// <param name="Execute">Action run when the command is invoked.</param>
/// <param name="CanExecute">Optional predicate gating availability; <see langword="null" /> means always enabled.</param>
/// <param name="Shortcut">Optional keyboard shortcut hint/binding.</param>
/// <param name="Icon">Optional icon identifier shown alongside the command.</param>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string Category,
    Action Execute,
    Func<bool>? CanExecute = null,
    string? Shortcut = null,
    string? Icon = null)
{
    /// <summary>Whether the command is currently enabled (per <see cref="CanExecute" />).</summary>
    public bool IsEnabled => CanExecute?.Invoke() ?? true;
}

/// <summary>
/// Shared registry of app commands backing the menu bar, command palette and shortcuts.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>All registered commands, in registration order.</summary>
    IReadOnlyList<CommandDescriptor> All { get; }

    /// <summary>Registers (or replaces, by id) a command.</summary>
    void Register(CommandDescriptor command);

    /// <summary>Finds a command by id, or <see langword="null" /> if none is registered.</summary>
    CommandDescriptor? Find(string id);

    /// <summary>Executes the command when it exists and is enabled; returns whether it ran.</summary>
    bool Execute(string id);
}

/// <summary>Default in-memory <see cref="ICommandRegistry" /> preserving registration order.</summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    /// <summary>Registers (or replaces, by id) a command, keeping first-seen order.</summary>
    public void Register(CommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.ContainsKey(command.Id))
        {
            _order.Add(command.Id);
        }
        _commands[command.Id] = command;
    }

    /// <summary>Finds a command by id, or <see langword="null" /> if none is registered.</summary>
    public CommandDescriptor? Find(string id) => _commands.GetValueOrDefault(id);

    /// <summary>All registered commands, in registration order.</summary>
    public IReadOnlyList<CommandDescriptor> All => [.. _order.Select(id => _commands[id])];

    /// <summary>Executes the command when it exists and is enabled; returns whether it ran.</summary>
    public bool Execute(string id)
    {
        CommandDescriptor? command = Find(id);
        if (command is null || !command.IsEnabled)
        {
            return false;
        }
        command.Execute();
        return true;
    }
}
