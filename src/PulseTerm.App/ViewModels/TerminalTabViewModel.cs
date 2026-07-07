using System;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using PulseTerm.Core.Models;
using PulseTerm.Core.Resources;
using PulseTerm.Core.Ssh;
using PulseTerm.Presentation.ViewModels;
using PulseTerm.Terminal;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class TerminalTabViewModel : TabViewModel, IDisposable
{
    private TimeSpan? _latency;
    private bool _isConnected;
    private int _reconnectAttempts;
    private bool _disposed;
    private bool _started;

    /// <summary>
    /// Creates a tab that owns the terminal emulator but has no live transport yet. Used to show
    /// the tab immediately in a "connecting" state; call <see cref="AttachTransport"/> once the
    /// shell stream is available (#17), and again to reconnect in place (#19).
    /// </summary>
    public TerminalTabViewModel(ITerminalEmulator terminalEmulator)
    {
        TerminalEmulator = terminalEmulator ?? throw new ArgumentNullException(nameof(terminalEmulator));

        Title = Strings.NewTab;
        ConnectionStatus = SessionStatus.Disconnected;

        // Keep the remote PTY size in sync with the local terminal grid. This is tied to the
        // emulator, not the transport, so it survives reconnects.
        TerminalEmulator.PtySizeChanged += OnPtySizeChanged;

        SearchCommand = ReactiveCommand.Create(() => { });
        CopyCommand = ReactiveCommand.Create(() => { });
        SplitCommand = ReactiveCommand.Create(() => { });
        ToggleBroadcastCommand = ReactiveCommand.Create(() => { });
        OpenTunnelCommand = ReactiveCommand.Create(() => { });
        OpenQuickCommandsCommand = ReactiveCommand.Create(() => { });
    }

    /// <summary>Creates a tab and attaches a live transport immediately (the established-connection case).</summary>
    public TerminalTabViewModel(ITerminalEmulator terminalEmulator, IShellStreamWrapper shellStream)
        : this(terminalEmulator)
    {
        AttachTransport(shellStream ?? throw new ArgumentNullException(nameof(shellStream)));
    }

    public Guid SessionId { get; set; }

    /// <summary>Resource panel data for this tab (hover >400ms on the tab shows it, §11).</summary>
    public ResourceMonitorViewModel? ResourceMonitor { get; set; }

    /// <summary>The profile this tab was connected with, used to reconnect in place (#19).</summary>
    public SessionProfile? Profile { get; set; }

    /// <summary>Raised when the session drops (remote closed the channel) so the UI can show the
    /// disconnected overlay and offer reconnect (#19).</summary>
    public event EventHandler? Disconnected;

    /// <summary>Raised when the user asks to reconnect a disconnected tab (Enter / Ctrl+R).</summary>
    public event EventHandler? ReconnectRequested;

    /// <summary>Requests a reconnect, but only from the disconnected state (no-op otherwise).</summary>
    public void RequestReconnect()
    {
        if (ConnectionStatus == SessionStatus.Disconnected)
            ReconnectRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Status-bar connection summary for this tab, e.g. "SSH • root@host:22".</summary>
    public string ConnectionSummary { get; init; } = string.Empty;

    /// <summary>The terminal emulation type advertised for this session.</summary>
    public string TerminalTypeName { get; init; } = "xterm-256color";

    /// <summary>The character encoding used for this session.</summary>
    public string EncodingName { get; init; } = "UTF-8";

    public ITerminalEmulator TerminalEmulator { get; }

    public IShellStreamWrapper? ShellStream { get; private set; }

    public SshTerminalBridge? Bridge { get; private set; }

    public new SessionStatus ConnectionStatus
    {
        get => base.ConnectionStatus;
        set
        {
            base.ConnectionStatus = value;
            IsConnected = value == SessionStatus.Connected;
        }
    }

    public TimeSpan? Latency
    {
        get => _latency;
        set => this.RaiseAndSetIfChanged(ref _latency, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public int ReconnectAttempts
    {
        get => _reconnectAttempts;
        private set => this.RaiseAndSetIfChanged(ref _reconnectAttempts, value);
    }

    public int MaxReconnectAttempts => 3;

    public bool CanReconnect => ReconnectAttempts < MaxReconnectAttempts;

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SplitCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBroadcastCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTunnelCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQuickCommandsCommand { get; }

    public void Start()
    {
        if (_started || Bridge is null)
        {
            return;
        }

        Bridge.Start();
        _started = true;
    }

    /// <summary>
    /// Attaches a live shell stream and prepares I/O pumping (call <see cref="Start"/> after).
    /// Any previous transport is torn down in the background first, so this doubles as the
    /// reconnect entry point that reuses the same tab and scrollback buffer (#19).
    /// </summary>
    public void AttachTransport(IShellStreamWrapper shellStream)
    {
        ArgumentNullException.ThrowIfNull(shellStream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        DetachTransport();

        ShellStream = shellStream;
        var bridge = new SshTerminalBridge(TerminalEmulator, shellStream);
        bridge.Closed += OnBridgeClosed;
        Bridge = bridge;
        _started = false;
    }

    /// <summary>Tears down the current transport off the UI thread, keeping the tab and buffer intact.</summary>
    public void DetachTransport()
    {
        var bridge = Bridge;
        if (bridge is null)
            return;

        bridge.Closed -= OnBridgeClosed;
        Bridge = null;
        ShellStream = null;
        _started = false;

        // Bridge.Dispose also disposes the shell stream; run it off the caller's thread.
        Task.Run(bridge.Dispose);
    }

    private void OnBridgeClosed()
    {
        // Fired on the read thread; marshal the reactive status change to the UI thread.
        if (Dispatcher.UIThread.CheckAccess())
            MarkDisconnected();
        else
            Dispatcher.UIThread.Post(MarkDisconnected);
    }

    /// <summary>Transitions the tab to the disconnected state and notifies listeners (idempotent).</summary>
    public void MarkDisconnected()
    {
        if (_disposed || ConnectionStatus == SessionStatus.Disconnected)
            return;

        ConnectionStatus = SessionStatus.Disconnected;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void OnPtySizeChanged(int columns, int rows)
    {
        var stream = ShellStream;
        if (_disposed || stream is null || !stream.CanWrite)
            return;

        // Off-load the channel request so a resize never stalls the UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                stream.Resize(columns, rows);
            }
            catch
            {
                // A resize on a torn-down or unsupported channel is non-fatal.
            }
        });
    }

    public void IncrementReconnectAttempt()
    {
        ReconnectAttempts++;
    }

    public void ResetReconnectAttempts()
    {
        ReconnectAttempts = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        TerminalEmulator.PtySizeChanged -= OnPtySizeChanged;

        // Instant, UI-safe teardown so the tab closes immediately: this only unhooks the
        // emulator's Updated handler, no network I/O.
        TerminalEmulator.Dispose();

        // Network teardown (cancel the read loop, close the SSH channel) can block for up to a
        // couple of seconds, so run it off the caller's (UI) thread — the tab is already gone.
        // Fixes the "closing a tab freezes the UI" problem (#18). Bridge.Dispose is idempotent.
        var bridge = Bridge;
        if (bridge is not null)
        {
            bridge.Closed -= OnBridgeClosed;
            Bridge = null;
            Task.Run(bridge.Dispose);
        }
    }
}
