using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI;
using Dock.Model.ReactiveUI.Controls;

namespace PulseTerm.App.Docking;

/// <summary>
/// Builds and drives the terminal workspace layout: a single <see cref="DocumentDock"/> whose
/// documents are draggable, splittable and can be torn off into floating windows (Dock.Avalonia
/// provides the drag/float/split behavior). Terminals are added and removed at runtime.
/// </summary>
public sealed class TerminalDockFactory : Factory
{
    private IDocumentDock? _documentDock;
    private IRootDock? _rootDock;

    public TerminalDockFactory()
    {
        // Without a host-window locator, "Float" removes the document from the layout but
        // never creates a window to show it in — the tab just vanished (用户反馈). HostWindow
        // is Dock.Avalonia's standard floating chrome.
        DefaultHostWindowLocator = () => new HostWindow();
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };
    }

    public IDocumentDock? DocumentDock => _documentDock;

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
            VisibleDockables = CreateList<IDockable>(),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(documentDock);
        root.ActiveDockable = documentDock;
        root.DefaultDockable = documentDock;

        _documentDock = documentDock;
        _rootDock = root;
        return root;
    }

    /// <summary>Adds a terminal document to the workspace and focuses it.</summary>
    public void AddTerminal(TerminalDocument document)
    {
        if (_documentDock is null)
            return;

        AddDockable(_documentDock, document);
        SetActiveDockable(document);
        if (_rootDock is not null)
            SetFocusedDockable(_rootDock, document);
    }

    /// <summary>
    /// Removes a terminal document from the workspace without raising <see cref="DocumentClosed"/>
    /// (unlike a user-initiated close). Used to retract a "connecting" tab when the handshake fails.
    /// </summary>
    public void RemoveTerminal(TerminalDocument document)
    {
        if (_documentDock is null)
            return;

        RemoveDockable(document, collapse: false);
    }

    public override void OnDockableClosed(IDockable? dockable)
    {
        base.OnDockableClosed(dockable);
        if (dockable is TerminalDocument document)
            DocumentClosed?.Invoke(document);
    }
}
