using Dock.Model.ReactiveUI.Controls;
using VelaShell.App.ViewModels;

namespace VelaShell.App.Docking;

/// <summary>
/// A Dock document that hosts a single SSH terminal tab. Wrapping the terminal (rather than
/// making <see cref="TerminalTabViewModel" /> itself a dockable) keeps the presentation/tab model
/// independent of the docking framework, so the existing tab collection and tests are unaffected.
/// The visual is resolved by a DataTemplate matched to this type.
/// </summary>
public sealed class TerminalDocument : Document
{
    public TerminalDocument(TerminalTabViewModel terminal)
    {
        Terminal = terminal;
        Id = terminal.Id.ToString("N");
        Title = terminal.Title;
        CanClose = true;
        // Floating is disabled by product decision (用户反馈): tabs only rearrange and split
        // inside the main window; tearing off into separate windows added confusion, not value.
        CanFloat = false;
        CanPin = false;
    }

    public TerminalTabViewModel Terminal { get; }
}
