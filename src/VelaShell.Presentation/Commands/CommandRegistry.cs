namespace VelaShell.Presentation.Commands;

/// <summary>
/// One executable app command. The registry is the single source shared by the menu bar,
/// the command palette and keyboard shortcuts (design spec §4A.1), so every entry point
/// shows the same name, hint and behavior.
/// </summary>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string Category,
    Action Execute,
    Func<bool>? CanExecute = null,
    string? Shortcut = null,
    string? Icon = null)
{
    public bool IsEnabled => CanExecute?.Invoke() ?? true;
}

public interface ICommandRegistry
{
    IReadOnlyList<CommandDescriptor> All { get; }

    /// <summary>Registers (or replaces, by id) a command.</summary>
    void Register(CommandDescriptor command);

    CommandDescriptor? Find(string id);

    /// <summary>Executes the command when it exists and is enabled; returns whether it ran.</summary>
    bool Execute(string id);
}

public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public void Register(CommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.ContainsKey(command.Id))
        {
            _order.Add(command.Id);
        }
        _commands[command.Id] = command;
    }

    public CommandDescriptor? Find(string id) => _commands.GetValueOrDefault(id);

    public IReadOnlyList<CommandDescriptor> All => [.. _order.Select(id => _commands[id])];

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
