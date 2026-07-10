using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI;
using Dock.Model.ReactiveUI.Controls;

namespace VelaShell.App.Docking;

/// <summary>
/// Builds and drives the terminal workspace layout: a single <see cref="DocumentDock" /> whose
/// documents are draggable and splittable inside the main window (Dock.Avalonia provides the
/// drag/split behavior; floating windows are disabled by product decision). Terminals are
/// added and removed at runtime.
/// </summary>
public sealed class TerminalDockFactory : Factory
{
    private IRootDock? _rootDock;

    private IDocumentDock? DocumentDock { get; set; }

    /// <summary>Raised when a terminal document is closed by the user (via the dock UI).</summary>
    public event Action<TerminalDocument>? DocumentClosed;

    public override IRootDock CreateLayout()
    {
        var documentDock = new DocumentDock
        {
            Id = "Terminals",
            Title = "Terminals",
            IsCollapsable = false,
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>()
        };
        IRootDock root = CreateRootDock();
        root.Id = "Root";
        root.Title = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(documentDock);
        root.ActiveDockable = documentDock;
        root.DefaultDockable = documentDock;
        DocumentDock = documentDock;
        _rootDock = root;
        return root;
    }

    /// <summary>Adds a terminal document to the workspace and focuses it.</summary>
    public void AddTerminal(TerminalDocument document)
    {
        if (DocumentDock is null)
        {
            return;
        }
        AddDockable(DocumentDock, document);
        SetActiveDockable(document);
        if (_rootDock is not null)
        {
            SetFocusedDockable(_rootDock, document);
        }
    }

    /// <summary>
    /// Removes a terminal document from the workspace without raising <see cref="DocumentClosed" />
    /// (unlike a user-initiated close). Used to retract a "connecting" tab when the handshake fails.
    /// </summary>
    public void RemoveTerminal(TerminalDocument document)
    {
        if (DocumentDock is null)
        {
            return;
        }
        RemoveDockable(document, false);
    }

    public override void OnDockableClosed(IDockable? dockable)
    {
        base.OnDockableClosed(dockable);
        if (dockable is TerminalDocument document)
        {
            DocumentClosed?.Invoke(document);
        }
    }
}
