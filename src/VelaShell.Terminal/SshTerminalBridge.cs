using Avalonia.Threading;
using VelaShell.Core.Ssh;

namespace VelaShell.Terminal;

/// <summary>
/// SSH ShellStream 与终端模拟器之间的桥接:后台读线程批量拉取主机输出、合并后在 UI 线程一次性喂入,
/// 并把用户输入写回 PTY。同时负责回显抑制与远端关闭通知。
/// </summary>
public class SshTerminalBridge : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly List<byte[]> _pending = [];

    // Output-batching pump: the read thread enqueues raw chunks and requests a single
    // coalesced flush on the UI thread, instead of marshaling + feeding once per read.
    // Under bursty output (apt/yum, cat, progress bars) this collapses hundreds of
    // cross-thread hops + full redraws into one Feed per frame.
    private readonly Lock _pendingLock = new();
    private readonly IShellStreamWrapper _shellStream;
    private readonly ITerminalEmulator _terminal;
    private volatile bool _disposed;

    // 连接初始化命令的回显抑制器(静默执行);仅在 UI 线程读写(Arm 与 FlushPending 同线程)。
    private EchoSuppressor? _echoSuppressor;
    private int _flushScheduled;
    private Task? _readTask;
    private int _started;

    /// <summary>绑定终端模拟器与 Shell 流,并订阅终端的用户输入事件。</summary>
    public SshTerminalBridge(ITerminalEmulator terminal, IShellStreamWrapper shellStream)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _cts = new();
        _terminal.UserInput += OnUserInput;
    }

    /// <summary>停止读循环、退订输入事件并释放 Shell 流与取消源(可安全重复调用)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _terminal.UserInput -= OnUserInput;
        ZModemRouter?.SessionEnded -= OnZModemSessionEnded;
        _cts.Cancel();

        // Dispose the stream BEFORE waiting on the read task: SSH.NET's ShellStream.Read blocks
        // in Monitor.Wait (its ReadAsync is the base Stream wrapper, so the token can't interrupt
        // it) and Dispose pulses that wait, making the pending read return EOF immediately and
        // without an exception. The previous order (wait, then dispose) parked the read task for
        // the full 2s timeout on every tab close.
        try
        {
            _shellStream.Dispose();
        }
        catch
        {
            // Best-effort: the channel may already be torn down by the session disconnect.
        }
        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Swallow faults from read task during dispose
        }
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>读写或喂入终端过程中发生异常时触发。</summary>
    public event Action<Exception>? Error;

    /// <summary>
    /// Raw host output chunks, fired on the read thread — used by session logging
    /// (设置 → 常规 → 会话日志). Subscribers must be fast and never throw.
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// Raised when the remote side closes the channel (e.g. the shell ran <c>exit</c> or the
    /// server rebooted): the read loop ended on its own rather than via <see cref="Dispose" />.
    /// Lets the session transition to a disconnected state that can be reconnected in place.
    /// Not raised during intentional teardown. Fired on the read thread — marshal as needed.
    /// </summary>
    public event Action? Closed;

    /// <summary>
    /// 可选的 ZMODEM 路由器。非 null 时,读循环会先经它路由每一段输出字节
    /// (检测并接管 ZMODEM 会话),其余字节才嗂入终端。由宿主在启动前装配。
    /// 赋值时自动订阅其会话结束事件,以便在会话收尾后把终端复位到干净状态。
    /// </summary>
    public ZModem.ZModemTerminalRouter? ZModemRouter
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }
            field?.SessionEnded -= OnZModemSessionEnded;
            field = value;
            field?.SessionEnded += OnZModemSessionEnded;
        }
    }

    // 退出备用屏幕缓冲区的控制序列(DECRST 1049)。ZMODEM 传输对 VT 终端本应完全透明,
    // 任何会话都不该把终端切到备用屏;每次会话收尾补发一次以自愈,防止杂散协议字节把主屏内容
    // 挡在空白的备用屏后面(表现为"整屏内容消失、只能重开会话")。
    private static readonly byte[] AltScreenExit = "\x1b[?1049l"u8.ToArray();

    /// <summary>
    /// ZMODEM 会话结束(成功 / 失败 / 取消)后的终端复位:在 UI 线程补发一次 DECRST 1049。
    /// 若终端确实被杂散字节卡在备用屏,这会切回主屏、恢复可见内容;若本就在主屏(正常情况),
    /// 模拟器会短路返回,是无副作用的空操作。事件在后台线程触发,故必须编组到 UI 线程再喂入。
    /// </summary>
    private void OnZModemSessionEnded(Core.ZModem.Model.ZModemSession session)
    {
        _ = session;
        if (_disposed)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                _terminal.Feed(AltScreenExit);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        });
    }

    /// <summary>
    /// 在输出流上剥除即将注入的命令回显(见 <see cref="EchoSuppressor" />)。
    /// 回显最多出现两次(内核规范模式 + readline 预输入重绘),窗口过后自动失效。
    /// </summary>
    public void SuppressEchoOnce(byte[] needle) => _echoSuppressor = new(needle, 2, TimeSpan.FromSeconds(10));

    /// <summary>
    /// 程序化注入:直写 PTY,不经终端控件的输入事件。连接初始化命令(提示符补行脚本、
    /// 启动命令)必须走这里——若走 WriteInput,注入里的 ESC 字节会把命令补全的行跟踪器
    /// (plan.md #16)打进未知态,SSH 标签的智能建议从连接起就全灭(实测取证)。
    /// </summary>
    public void SendRaw(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
        {
            return;
        }
        _ = WriteUserInputAsync(data);
    }

    /// <summary>启动后台读循环;仅允许调用一次,重复调用会抛出异常。</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("Bridge already started");
        }

        // Only start reading. Do NOT prime the shell with a newline — the server already sends
        // its banner and prompt on connect, so an extra '\n' produces a duplicate prompt line.
        // The token is snapshotted here because Dispose disposes _cts after its 2s grace — a
        // still-draining loop must not touch the CTS property afterwards (token reads stay valid).
        CancellationToken token = _cts.Token;
        _readTask = Task.Run(() => ReadLoopAsync(token));
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        // A larger read buffer means fewer awaits and larger natural batches.
        byte[] buffer = new byte[16384];
        bool remoteClosed = false;
        try
        {
            while (!token.IsCancellationRequested && _shellStream.CanRead)
            {
                int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    // EOF: the remote closed the channel (exit / reboot / dropped connection).
                    remoteClosed = true;
                    break;
                }
                byte[] data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                try
                {
                    DataReceived?.Invoke(data);
                }
                catch
                {
                    // 日志订阅者异常不允许打断读循环。
                }

                // Do NOT await a per-read UI hop. Queue the chunk and coalesce; the read
                // thread keeps pace with the network while the UI drains at frame rate.
                // ZMODEM 路由优先:会话期间返回空终端字节(全部转交引擎),
                // 命中时仅把引导前的字节嗂终端;未启用时原样嗂入。
                ZModem.ZModemTerminalRouter? router = ZModemRouter;
                if (router is null)
                {
                    EnqueueForFeed(data);
                }
                else
                {
                    ZModem.ZModemRouteResult route = router.ProcessIncoming(data);
                    if (route.TerminalBytes.Length > 0)
                    {
                        EnqueueForFeed(route.TerminalBytes);
                    }
                }
            }

            // Loop also exits when the stream reports it can no longer be read.
            if (!token.IsCancellationRequested)
            {
                remoteClosed = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — not an error
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed during shutdown — not an error
        }
        catch (Exception ex)
        {
            remoteClosed = true;
            Error?.Invoke(ex);
        }

        // Signal a remote-initiated close, but not our own Dispose()-driven teardown.
        if (remoteClosed && !_disposed)
        {
            Closed?.Invoke();
        }
    }

    private void EnqueueForFeed(byte[] data)
    {
        lock (_pendingLock)
        {
            _pending.Add(data);
        }

        // Schedule at most one pending UI flush; further chunks piggyback on it.
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(FlushPending);
        }
    }

    private void FlushPending()
    {
        // Reset first so chunks arriving during the drain schedule a fresh flush.
        Interlocked.Exchange(ref _flushScheduled, 0);
        byte[] combined;
        lock (_pendingLock)
        {
            int count = _pending.Count;
            if (count == 0)
            {
                return;
            }
            if (count == 1)
            {
                combined = _pending[0];
            }
            else
            {
                int total = 0;
                for (int i = 0; i < count; i++)
                {
                    total += _pending[i].Length;
                }
                combined = new byte[total];
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    byte[] chunk = _pending[i];
                    Array.Copy(chunk, 0, combined, offset, chunk.Length);
                    offset += chunk.Length;
                }
            }
            _pending.Clear();
        }
        if (_disposed)
        {
            return;
        }
        if (_echoSuppressor is { } suppressor)
        {
            combined = suppressor.Process(combined);
            if (suppressor.Expired)
            {
                _echoSuppressor = null;
            }
            if (combined.Length == 0)
            {
                return;
            }
        }
        try
        {
            // One Feed per flush => one Updated => one repaint, regardless of chunk count.
            _terminal.Feed(combined);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private void OnUserInput(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
        {
            return;
        }

        // Fire-and-forget with error handling — this is an event handler so async void is acceptable
        _ = WriteUserInputAsync(data);
    }

    private async Task WriteUserInputAsync(byte[] data)
    {
        try
        {
            await _shellStream.WriteAsync(data, 0, data.Length, CancellationToken.None).ConfigureAwait(false);
            _shellStream.Flush();
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed — expected during teardown
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }
}
