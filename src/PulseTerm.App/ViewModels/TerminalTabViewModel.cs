using System;
using System.Reactive;
using System.Threading.Tasks;
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

    public TerminalTabViewModel(ITerminalEmulator terminalEmulator, IShellStreamWrapper shellStream)
    {
        TerminalEmulator = terminalEmulator ?? throw new ArgumentNullException(nameof(terminalEmulator));
        ShellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));

        Title = Strings.NewTab;
        ConnectionStatus = SessionStatus.Disconnected;

        Bridge = new SshTerminalBridge(terminalEmulator, shellStream);

        // Keep the remote PTY size in sync with the local terminal grid.
        TerminalEmulator.PtySizeChanged += OnPtySizeChanged;

        SearchCommand = ReactiveCommand.Create(() => { });
        CopyCommand = ReactiveCommand.Create(() => { });
        SplitCommand = ReactiveCommand.Create(() => { });
        ToggleBroadcastCommand = ReactiveCommand.Create(() => { });
        OpenTunnelCommand = ReactiveCommand.Create(() => { });
        OpenQuickCommandsCommand = ReactiveCommand.Create(() => { });
    }

    public Guid SessionId { get; init; }

    public ITerminalEmulator TerminalEmulator { get; }

    public IShellStreamWrapper ShellStream { get; }

    public SshTerminalBridge Bridge { get; }

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
        if (_started)
        {
            return;
        }

        Bridge.Start();
        _started = true;
    }

    private void OnPtySizeChanged(int columns, int rows)
    {
        if (_disposed || !ShellStream.CanWrite)
            return;

        // Off-load the channel request so a resize never stalls the UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                ShellStream.Resize(columns, rows);
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
        Bridge.Dispose();
        TerminalEmulator.Dispose();
    }
}
