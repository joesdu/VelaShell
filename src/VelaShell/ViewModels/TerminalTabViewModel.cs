using System.Reactive;
using System.Text;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Services;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;

namespace VelaShell.ViewModels;

public class TerminalTabViewModel : TabViewModel, IDisposable
{
    private readonly Lock _ptyResizeGate = new();
    private string? _connectionError;
    private bool _disposed;
    private (int Columns, int Rows)? _pendingPtySize;
    private bool _ptyResizeSending;
    private bool _started;

    /// <summary>
    /// Creates a tab that owns the terminal emulator but has no live transport yet. Used to show
    /// the tab immediately in a "connecting" state; call <see cref="AttachTransport" /> once the
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

        // Toolbar quick actions (用户反馈 #5): tear the transport down but keep the tab,
        // or ask the owner to reconnect in place (#19 flow).
        DisconnectCommand = ReactiveCommand.Create(() =>
            {
                UserRequestedDisconnect = true;
                DetachTransport();
                MarkDisconnected();
            },
            this.WhenAnyValue(x => x.IsConnected));
        ReconnectCommand = ReactiveCommand.Create(RequestReconnect,
            this.WhenAnyValue(x => x.IsConnected, connected => !connected));

        // 标签页内失败/断开覆盖层(设计 yxjmg)的“关闭标签页”按钮。
        CloseTabCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
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

    /// <summary>
    /// 该连接的稳定标识色(标签页色条与 SFTP 面板同色联动,防多标签误操作)。
    /// 本地终端/无配置标签返回透明。Profile 在标签创建时就已赋值,绑定一次性读取即可。
    /// </summary>
    public Avalonia.Media.IBrush ConnectionAccentBrush =>
        Profile is { } profile ? ConnectionAccent.BrushFor(profile.Id) : Avalonia.Media.Brushes.Transparent;

    /// <summary>
    /// 本地终端标签(§12 P1-1)对应的 shell;null = SSH 会话。重开(Enter/Ctrl+R)
    /// 用它重新拉起本地进程。
    /// </summary>
    public LocalShellInfo? LocalShell { get; set; }

    /// <summary>
    /// true = 本次断开由用户主动触发(断开按钮),自动重连(设置 → 常规)不介入;
    /// 重新挂载传输时复位。
    /// </summary>
    public bool UserRequestedDisconnect { get; private set; }

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

            // 连接成功即清除上次失败信息(隐去覆盖层);其余状态变化都要刷新覆盖层可见性。
            if (value == SessionStatus.Connected)
            {
                ConnectionError = null;
            }
            this.RaisePropertyChanged(nameof(ShowDisconnectedOverlay));
            this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
            this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
        }
    }

    /// <summary>
    /// 最近一次连接失败的原因;非空时覆盖层显示为“连接失败”,否则为掉线的
    /// “连接已断开”(设计 yxjmg / dcOverlay)。
    /// </summary>
    public string? ConnectionError
    {
        get => _connectionError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _connectionError, value);
            this.RaisePropertyChanged(nameof(HasConnectionError));
            this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
            this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
        }
    }

    public bool HasConnectionError => !string.IsNullOrEmpty(_connectionError);

    /// <summary>
    /// 标签页内失败/断开覆盖层(设计 yxjmg)的可见性:未连接(断开/错误)且是一个
    /// 真实会话标签(SSH 或本地)时显示。连接中/已连接不显示。
    /// </summary>
    public bool ShowDisconnectedOverlay => !_disposed && ConnectionStatus is SessionStatus.Disconnected or SessionStatus.Error && (Profile is not null || LocalShell is not null);

    public string DisconnectOverlayTitle => HasConnectionError ? "连接失败" : "连接已断开";

    public string DisconnectOverlayDetail => HasConnectionError
                                                 ? _connectionError!
                                                 : $"// 与 {OverlayHostLabel} 的 SSH 连接已丢失";

    private string OverlayHostLabel => Profile is { } p ? $"{p.Host}:{p.Port}" : Title;

    public TimeSpan? Latency
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsConnected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int ReconnectAttempts
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int MaxReconnectAttempts => 3;

    public bool CanReconnect => ReconnectAttempts < MaxReconnectAttempts;

    /// <summary>Disconnects the live transport, keeping the tab (and its buffer) for reconnect.</summary>
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    /// <summary>Requests an in-place reconnect of a disconnected tab (same as Enter / Ctrl+R).</summary>
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }

    /// <summary>Closes the tab from the in-tab failure/disconnect overlay (设计 yxjmg).</summary>
    public ReactiveCommand<Unit, Unit> CloseTabCommand { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        TerminalEmulator.PtySizeChanged -= OnPtySizeChanged;

        // Instant, UI-safe teardown so the tab closes immediately: this only unhooks the
        // emulator's Updated handler, no network I/O.
        TerminalEmulator.Dispose();

        // Network teardown (cancel the read loop, close the SSH channel) can block for up to a
        // couple of seconds, so run it off the caller's (UI) thread — the tab is already gone.
        // Fixes the "closing a tab freezes the UI" problem (#18). Bridge.Dispose is idempotent.
        SshTerminalBridge? bridge = Bridge;
        if (bridge is not null)
        {
            bridge.Closed -= OnBridgeClosed;
            Bridge = null;
            Task.Run(bridge.Dispose);
        }
    }

    /// <summary>
    /// Raised when the session drops (remote closed the channel) so the UI can show the
    /// disconnected overlay and offer reconnect (#19).
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>Raised when the user asks to reconnect a disconnected tab (Enter / Ctrl+R).</summary>
    public event EventHandler? ReconnectRequested;

    /// <summary>标签页内失败/断开覆盖层(设计 yxjmg)的“关闭标签页”触发,由宿主移除该标签。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Requests a reconnect, but only from the disconnected state (no-op otherwise).</summary>
    public void RequestReconnect()
    {
        if (ConnectionStatus == SessionStatus.Disconnected)
        {
            ReconnectRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 把初始化命令注入远端 shell 并静默执行:发送前在桥上装回显抑制器,
    /// 把 PTY 回显的这一行从输出流剥掉(用户要求不在界面显示)。前导空格让
    /// HISTCONTROL=ignoreboth 不记历史;抑制针 needle 不含该空格(空格太常见,
    /// 不适合做流匹配锚点),残留的空格与光标位置由命令本身的补行脚本消化。
    /// </summary>
    public void SendSilentCommand(string command)
    {
        string payload = command.Trim();
        if (Bridge is null || payload.Length == 0)
        {
            return;
        }
        Bridge.SuppressEchoOnce(Encoding.UTF8.GetBytes(payload + "\r\n"));
        TerminalEmulator.WriteInput(Encoding.UTF8.GetBytes(" " + payload + "\n"));
    }

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
    /// Attaches a live shell stream and prepares I/O pumping (call <see cref="Start" /> after).
    /// Any previous transport is torn down in the background first, so this doubles as the
    /// reconnect entry point that reuses the same tab and scrollback buffer (#19).
    /// </summary>
    public void AttachTransport(IShellStreamWrapper shellStream)
    {
        ArgumentNullException.ThrowIfNull(shellStream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        DetachTransport();
        UserRequestedDisconnect = false;
        ShellStream = shellStream;
        var bridge = new SshTerminalBridge(TerminalEmulator, shellStream);
        bridge.Closed += OnBridgeClosed;
        Bridge = bridge;
        _started = false;

        // The channel was opened at a fixed default grid (120×32). By now the control has usually
        // already been laid out to the real viewport, but the PtySizeChanged that carried that
        // size fired while ShellStream was still null and was dropped. Push the emulator's current
        // grid to the new stream so the remote PTY winsize matches what's visible — otherwise
        // full-screen apps (htop/nano) read the stale 32-row size and draw their footer mid-screen,
        // leaving the lower part of the terminal blank.
        SyncPtySize();
    }

    /// <summary>
    /// Re-sends the emulator's current grid size to the live shell stream, so the remote
    /// PTY winsize matches the actual viewport rather than the fixed size the channel opened with.
    /// </summary>
    private void SyncPtySize()
    {
        if (TerminalEmulator is { Columns: > 0, Rows: > 0 })
        {
            OnPtySizeChanged(TerminalEmulator.Columns, TerminalEmulator.Rows);
        }
    }

    /// <summary>Tears down the current transport off the UI thread, keeping the tab and buffer intact.</summary>
    public void DetachTransport()
    {
        SshTerminalBridge? bridge = Bridge;
        if (bridge is null)
        {
            return;
        }
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
        {
            MarkDisconnected();
        }
        else
        {
            Dispatcher.UIThread.Post(() => MarkDisconnected());
        }
    }

    /// <summary>
    /// 初始连接失败:保留标签并在标签页内显示失败覆盖层(设计 yxjmg),不销毁标签、
    /// 不弹全局框。置为断开态以启用“重新连接”;不触发 <see cref="Disconnected" /> 事件——
    /// 初始连接失败不应自动重连(尤其认证失败),由用户手动决定重连或关闭。
    /// </summary>
    public void MarkConnectionFailed(string message)
    {
        if (_disposed)
        {
            return;
        }

        // 先记录原因(覆盖层显示“连接失败”),再切断开态刷新覆盖层可见性。
        ConnectionError = message;
        ConnectionStatus = SessionStatus.Disconnected;
    }

    /// <summary>
    /// Transitions the tab to the disconnected state and notifies listeners (idempotent).
    /// <paramref name="reason" /> 非空时(如重连失败)覆盖层显示为“连接失败 + 具体原因”,
    /// 否则为普通掉线的“连接已断开”。
    /// </summary>
    public void MarkDisconnected(string? reason = null)
    {
        if (_disposed || ConnectionStatus == SessionStatus.Disconnected)
        {
            return;
        }
        if (!string.IsNullOrEmpty(reason))
        {
            ConnectionError = reason;
        }
        ConnectionStatus = SessionStatus.Disconnected;
        FeedDisconnectNotice();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Prints a red "connection closed" banner plus the reconnect hint into the
    /// terminal, so the user knows Enter / the Reconnect button will bring the session back
    /// (用户反馈 #1). Runs for both manual disconnects and remote closes.
    /// </summary>
    private void FeedDisconnectNotice()
    {
        string notice =
            "\r\n\u001b[0m\u001b[31m● " +
            Strings.TerminalDisconnectedNotice +
            "\u001b[0m\r\n" +
            "\u001b[90m" +
            Strings.TerminalReconnectHint +
            "\u001b[0m\r\n";
        try
        {
            TerminalEmulator.Feed(Encoding.UTF8.GetBytes(notice));
        }
        catch
        {
            // Purely cosmetic; never let the hint break the disconnect flow.
        }
    }

    /// <summary>
    /// Forwards grid changes to the SSH channel off the UI thread, strictly in order
    /// and collapsing bursts to the latest size. The previous fire-and-forget Task.Run per
    /// event could deliver sizes out of order during drag storms, leaving the remote shell
    /// with a stale grid — its subsequent prompt redraw then corrupted the buffer.
    /// </summary>
    private void OnPtySizeChanged(int columns, int rows)
    {
        if (_disposed || ShellStream is null || !ShellStream.CanWrite)
        {
            return;
        }
        lock (_ptyResizeGate)
        {
            _pendingPtySize = (columns, rows);
            if (_ptyResizeSending)
            {
                return;
            }
            _ptyResizeSending = true;
        }
        _ = Task.Run(DrainPtyResizeQueue);
    }

    private void DrainPtyResizeQueue()
    {
        while (true)
        {
            (int Columns, int Rows) size;
            lock (_ptyResizeGate)
            {
                if (_pendingPtySize is null)
                {
                    _ptyResizeSending = false;
                    return;
                }
                size = _pendingPtySize.Value;
                _pendingPtySize = null;
            }
            IShellStreamWrapper? stream = ShellStream;
            if (_disposed || stream is null || !stream.CanWrite)
            {
                lock (_ptyResizeGate)
                {
                    _pendingPtySize = null;
                    _ptyResizeSending = false;
                }
                return;
            }
            try
            {
                stream.Resize(size.Columns, size.Rows);
            }
            catch
            {
                // A resize on a torn-down or unsupported channel is non-fatal.
            }
        }
    }

    public void IncrementReconnectAttempt()
    {
        ReconnectAttempts++;
    }

    public void ResetReconnectAttempts()
    {
        ReconnectAttempts = 0;
    }
}
