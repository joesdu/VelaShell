using System.Reactive;
using System.Text;
using Avalonia.Input;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.Terminal.Input;
using VelaShell.Terminal.Rendering;

namespace VelaShell.ViewModels;

/// <summary>
/// 单个终端标签页的视图模型:持有终端模拟器与(可选的)SSH/本地传输,负责连接状态、
/// 断开/重连、PTY 尺寸同步与命令补全的行跟踪。
/// </summary>
public class TerminalTabViewModel : TabViewModel, IDisposable
{
    private readonly Lock _ptyResizeGate = new();
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
        TerminalEmulator =
            terminalEmulator ?? throw new ArgumentNullException(nameof(terminalEmulator));
        Title = Strings.NewTab;
        ConnectionStatus = SessionStatus.Disconnected;

        // Keep the remote PTY size in sync with the local terminal grid. This is tied to the
        // emulator, not the transport, so it survives reconnects.
        TerminalEmulator.PtySizeChanged += OnPtySizeChanged;

        // 命令补全(plan.md #16):旁路跟踪用户键入的命令行;Enter 提交时做回显校验
        // (密码输入无回显,不入历史)后向宿主上报。必须订阅 TypedInput 而非 UserInput:
        // 后者还承载终端的协议自动应答(ESC 开头),会把跟踪器永久打进未知态。
        TerminalEmulator.TypedInput += OnUserInputForTracker;
        InputTracker.CommandSubmitted += OnTrackedCommandSubmitted;
        InputTracker.UnknownLineSubmitted += OnUnknownLineSubmitted;

        // Toolbar quick actions (用户反馈 #5): tear the transport down but keep the tab,
        // or ask the owner to reconnect in place (#19 flow).
        DisconnectCommand = ReactiveCommand.Create(
            () =>
            {
                UserRequestedDisconnect = true;
                DetachTransport();
                MarkDisconnected();
            },
            this.WhenAnyValue(x => x.IsConnected)
        );
        ReconnectCommand = ReactiveCommand.Create(
            RequestReconnect,
            this.WhenAnyValue(x => x.IsConnected, connected => !connected)
        );

        // 标签页内失败/断开覆盖层(设计 yxjmg)的“关闭标签页”按钮。
        CloseTabCommand = ReactiveCommand.Create(() =>
            CloseRequested?.Invoke(this, EventArgs.Empty)
        );
    }

    /// <summary>Creates a tab and attaches a live transport immediately (the established-connection case).</summary>
    public TerminalTabViewModel(ITerminalEmulator terminalEmulator, IShellStreamWrapper shellStream)
        : this(terminalEmulator)
    {
        AttachTransport(shellStream ?? throw new ArgumentNullException(nameof(shellStream)));
    }

    /// <summary>该标签所属会话的唯一标识,用于与宿主的会话管理关联。</summary>
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
        Profile is { } profile
            ? ConnectionAccent.BrushFor(profile.Id)
            : Avalonia.Media.Brushes.Transparent;

    /// <summary>本标签正在键入的命令行跟踪器(命令补全弹层的数据入口,见视图侧)。</summary>
    public TerminalInputTracker InputTracker { get; } = new();

    /// <summary>补全建议提供器(宿主 MainWindowViewModel 注入;null = 补全不可用)。</summary>
    public CommandSuggestionProvider? SuggestionProvider { get; set; }

    /// <summary>用户在本标签提交了一条通过回显校验的命令(宿主记入全局命令历史)。</summary>
    public event Action<string>? CommandLineSubmitted;

    private void OnUserInputForTracker(byte[] data)
    {
        InputTracker.Process(data);
        SuggestDiag.Log(
            "typed",
            $"""
            bytes=[{Convert.ToHexString(data)}] input="{InputTracker.CurrentInput ?? "<unknown>"}"
            """
        );
    }

    private void OnTrackedCommandSubmitted(string command)
    {
        // 回显校验:所键入的文本应已被 shell 回显到屏上;密码提示符不回显 → 不记录,
        // 防止口令进历史。注意桥接层的输出是按帧合并 Feed 的——Enter 瞬间最后几个
        // 字符的回显可能还在队列里,同步校验失败时延迟 200ms 后在整个可视区做二次
        // 校验(此时命令行可能已随输出上移,故扫全屏而非只看光标行)。
        if (TerminalEmulator is not VelaTerminalControl control)
        {
            return;
        }
        if (CursorLineContains(control, command))
        {
            CommandLineSubmitted?.Invoke(command);
            return;
        }
        DispatcherTimer.RunOnce(
            () =>
            {
                if (!_disposed && ScreenContains(control, command))
                {
                    CommandLineSubmitted?.Invoke(command);
                }
            },
            TimeSpan.FromMilliseconds(200)
        );
    }

    private void OnUnknownLineSubmitted()
    {
        // 行内容本地不可知(用户按过方向键/Tab 等):改从屏幕的光标行提取命令——
        // 提示符之后的文本就是 shell 将执行的整行。读取发生在 Enter 的瞬间,
        // 先于命令输出刷屏;密码提示行没有提示符结尾标记,天然不会被提取。
        if (TerminalEmulator is not VelaTerminalControl control)
        {
            return;
        }
        try
        {
            string? command = ExtractCommandAfterPrompt(control.GetBufferLine(control.CursorRow));
            if (!string.IsNullOrWhiteSpace(command))
            {
                CommandLineSubmitted?.Invoke(command);
            }
        }
        catch
        {
            // 读缓冲失败时宁可漏记不误记。
        }
    }

    /// <summary>
    /// 从提示符行提取命令:取最早出现的提示符结尾标记("$ "/"# "/"❯ "/"% ")之后的文本。
    /// 识别不了的提示符样式(无标记)返回 null——宁可漏记不误记。
    /// </summary>
    internal static string? ExtractCommandAfterPrompt(string line)
    {
        int best = -1;
        foreach (string marker in (ReadOnlySpan<string>)["$ ", "# ", "❯ ", "% "])
        {
            int index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }
        if (best < 0)
        {
            return null;
        }
        string command = line[(best + 2)..].Trim();
        return command.Length == 0 ? null : command;
    }

    private static bool CursorLineContains(VelaTerminalControl control, string command)
    {
        try
        {
            return control
                .GetBufferLine(control.CursorRow)
                .Contains(command, StringComparison.Ordinal);
        }
        catch
        {
            // 读缓冲失败(极端竞态)时宁可漏记不误记。
            return false;
        }
    }

    private static bool ScreenContains(VelaTerminalControl control, string command)
    {
        try
        {
            for (int row = 0; row < control.Rows; row++)
            {
                if (control.GetBufferLine(row).Contains(command, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 同上:宁可漏记不误记。
        }
        return false;
    }

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

    /// <summary>本标签持有的终端模拟器,跨重连保持不变(拥有滚动缓冲区)。</summary>
    public ITerminalEmulator TerminalEmulator { get; }

    /// <summary>当前挂载的 shell 数据流;未连接或断开后为 null。</summary>
    public IShellStreamWrapper? ShellStream { get; private set; }

    /// <summary>连接 shell 流与终端模拟器的桥接器;未连接时为 null。</summary>
    public SshTerminalBridge? Bridge { get; private set; }

    /// <summary>
    /// 本标签的连接状态。赋值时同步 <see cref="IsConnected" />、在连接成功时清除错误,
    /// 并刷新失败/断开覆盖层的可见性与文案。
    /// </summary>
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
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasConnectionError));
            this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
            this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
        }
    }

    /// <summary>是否存在连接错误(决定覆盖层显示“连接失败”还是“连接已断开”)。</summary>
    public bool HasConnectionError => !string.IsNullOrEmpty(ConnectionError);

    /// <summary>
    /// 标签页内失败/断开覆盖层(设计 yxjmg)的可见性:未连接(断开/错误)且是一个
    /// 真实会话标签(SSH 或本地)时显示。连接中/已连接不显示。
    /// </summary>
    public bool ShowDisconnectedOverlay =>
        !_disposed
        && ConnectionStatus is SessionStatus.Disconnected or SessionStatus.Error
        && (Profile is not null || LocalShell is not null);

    /// <summary>失败/断开覆盖层的标题:有错误时为“连接失败”,否则为“连接已断开”。</summary>
    public string DisconnectOverlayTitle =>
        HasConnectionError
            ? Strings.Get("Msg_ConnectionFailedTitle")
            : Strings.Get("Msg_ConnectionClosedTitle");

    /// <summary>失败/断开覆盖层的详情:有错误时显示具体原因,否则显示掉线主机提示。</summary>
    public string DisconnectOverlayDetail =>
        HasConnectionError
            ? ConnectionError!
            : Strings.Format("Msg_SshConnectionLostDetail", OverlayHostLabel);

    private string OverlayHostLabel => Profile is { } p ? $"{p.Host}:{p.Port}" : Title;

    /// <summary>最近一次测得的连接延迟;未知时为 null。</summary>
    public TimeSpan? Latency
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否处于已连接状态(随 <see cref="ConnectionStatus" /> 联动)。</summary>
    public bool IsConnected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>已尝试的自动重连次数,连接成功或重置时归零。</summary>
    public int ReconnectAttempts
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 最大自动重连次数,唯一来源是设置 → 常规 → 自动重连(General.MaxRetries),
    /// 由宿主在断开处理时同步下发;默认值与设置模型一致,仅作未注入时的兜底。
    /// </summary>
    public int MaxReconnectAttempts
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 3;

    /// <summary>是否仍允许自动重连(重连次数未达上限)。</summary>
    public bool CanReconnect => ReconnectAttempts < MaxReconnectAttempts;

    /// <summary>Disconnects the live transport, keeping the tab (and its buffer) for reconnect.</summary>
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    /// <summary>Requests an in-place reconnect of a disconnected tab (same as Enter / Ctrl+R).</summary>
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }

    /// <summary>Closes the tab from the in-tab failure/disconnect overlay (设计 yxjmg).</summary>
    public ReactiveCommand<Unit, Unit> CloseTabCommand { get; }

    /// <summary>
    /// 释放标签资源:解绑事件、即时销毁终端模拟器(UI 安全),并把耗时的网络拆除
    /// (取消读循环、关闭 SSH 通道)放到后台线程,避免关闭标签时卡住 UI(#18)。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        TerminalEmulator.PtySizeChanged -= OnPtySizeChanged;
        TerminalEmulator.TypedInput -= OnUserInputForTracker;
        InputTracker.CommandSubmitted -= OnTrackedCommandSubmitted;
        InputTracker.UnknownLineSubmitted -= OnUnknownLineSubmitted;

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
        GC.SuppressFinalize(this);
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
    /// 通过正常用户输入通道执行一条快捷命令。命令正文原样发送,仅移除已有的末尾
    /// 换行并补一个回车,使其与用户键入后按 Enter 的行为一致。
    /// </summary>
    /// <returns>命令已发送时为 true;终端未连接或命令为空时为 false。</returns>
    public bool TryExecuteCommand(string command)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        string payload = command.TrimEnd('\r', '\n') + "\r";
        TerminalEmulator.WriteInput(Encoding.UTF8.GetBytes(payload));
        return true;
    }

    /// <summary>向已连接终端发送广播文本。</summary>
    public bool TryWriteTextInput(string text)
    {
        if (!IsConnected || string.IsNullOrEmpty(text))
        {
            return false;
        }
        TerminalEmulator.WriteTextInput(text);
        return true;
    }

    /// <summary>按本终端模式编码并发送广播按键。</summary>
    public bool TryWriteKeyInput(Key key, Avalonia.Input.KeyModifiers modifiers) =>
        IsConnected && TerminalEmulator.WriteKeyInput(key, modifiers);

    /// <summary>按本终端 bracketed-paste 状态发送广播粘贴。</summary>
    public bool TryWritePasteInput(string text)
    {
        if (!IsConnected || string.IsNullOrEmpty(text))
        {
            return false;
        }
        TerminalEmulator.WritePasteInput(text);
        return true;
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

        // 直写 PTY(SendRaw)而非 WriteInput:注入不是用户键入,不得进入命令补全的
        // 行跟踪——补行脚本里的 ESC 字节曾把跟踪器打进未知态,SSH 标签建议全灭。
        Bridge.SendRaw(Encoding.UTF8.GetBytes(" " + payload + "\n"));
    }

    /// <summary>启动桥接的 I/O 泵送(幂等;无桥或已启动时为空操作)。</summary>
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
            $"\r\n\u001b[0m\u001b[31m● {Strings.TerminalDisconnectedNotice}\u001b[0m\r\n\u001b[90m{Strings.TerminalReconnectHint}\u001b[0m\r\n";
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

    /// <summary>自动重连尝试计数加一。</summary>
    public void IncrementReconnectAttempt()
    {
        ReconnectAttempts++;
    }

    /// <summary>把自动重连尝试计数归零(连接成功后调用)。</summary>
    public void ResetReconnectAttempts()
    {
        ReconnectAttempts = 0;
    }
}
