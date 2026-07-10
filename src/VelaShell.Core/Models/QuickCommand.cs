namespace VelaShell.Core.Models;

public class QuickCommand
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string CommandText { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsBuiltIn { get; set; }
}
