using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.ReactiveUI.Controls;
using VelaShell.App.ViewModels;
using VelaShell.App.Views;

namespace VelaShell.App.Docking;

/// <summary>
/// A Dock document that hosts a single SSH terminal tab. Wrapping the terminal (rather than
/// making <see cref="TerminalTabViewModel" /> itself a dockable) keeps the presentation/tab model
/// independent of the docking framework, so the existing tab collection and tests are unaffected.
/// Implements <see cref="IDataTemplate" /> because Dock 12's Fluent theme presents document
/// content via <c>ContentTemplate="{Binding}"</c> — i.e. it expects the document itself to be its
/// own template (as Dock.Model.Avalonia's Document is). Without this every realization logged a
/// "Could not convert TerminalDocument to IDataTemplate" binding error before falling back to a
/// DataTemplate lookup.
/// </summary>
public sealed class TerminalDocument : Document, IDataTemplate
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

    public Control Build(object? param) => new TerminalTabView { DataContext = Terminal };

    public bool Match(object? data) => data is TerminalDocument;
}
