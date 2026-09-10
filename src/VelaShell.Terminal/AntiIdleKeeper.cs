namespace VelaShell.Terminal;

/// <summary>
/// 防空闲断开:会话静默满一个间隔时,往输入流里送一个不可见字节,把服务端的空闲计时归零。
/// </summary>
/// <remarks>
/// <para>
/// 它与 SSH 的保活心跳(<c>KeepAliveInterval</c>)解决的不是同一件事。心跳走的是协议层,
/// 对端的 sshd 看得见、shell 看不见 —— 而把人踢掉的往往正是 shell 那一层:bash 的
/// <c>TMOUT</c>、堡垒机的会话超时、<c>screen</c> 之外的登录会话空闲策略,统统按
/// "有没有人往 tty 里输入过"计时。所以防空闲只能真的写字节,写进 PTY 的输入流。
/// </para>
/// <para>
/// 送的是 <c>NUL</c>(0x00),不是空格。空格会被 shell 当成用户输入留在命令行上,
/// 也会被全屏程序(vim / less / htop)当成按键吃掉 —— 用户离开一小时回来,
/// 命令行前面多了三十个空格,或者 less 已经翻到了文件末尾。NUL 在行规范里被丢弃,
/// 却照样刷新 tty 的读活动,正是这里要的:有输入,但什么也没发生。
/// </para>
/// <para>
/// <b>只在真的空闲时才发。</b>用户刚敲过键的间隔里一个字节都不该多发:那既是浪费,
/// 也让"注入"这件事出现在本不需要它的时刻。因此每次记账都记在最后一次出站写上,
/// 定时器醒来发现还不够久,就把闹钟改到刚好满一个间隔的那一刻再睡回去。
/// </para>
/// <para>
/// 线程:<see cref="NoteActivity" /> 由 UI 线程调用(击键与注入都在那儿入队),
/// 回调在线程池上跑,两边只经由一个原子的时间戳与一把锁相见。
/// </para>
/// </remarks>
internal sealed class AntiIdleKeeper : IDisposable
{
    /// <summary>注入的载荷:单个 NUL。写循环只读不改,故可长期共用同一个数组。</summary>
    internal static readonly byte[] Payload = [0x00];

    /// <summary>
    /// 该发但发不出去(ZMODEM 会话进行中、流暂时不可写)时的重试间隔。
    /// </summary>
    /// <remarks>
    /// 不按整个间隔重排:传输结束后不该再干等一整轮 —— 用户配 60 秒是因为服务器 90 秒踢人,
    /// 一次传输把窗口吃掉半轮,下一发就可能迟到。每秒一次的空转只是一个 bool 判断。
    /// </remarks>
    private static readonly TimeSpan RetryWhenBlocked = TimeSpan.FromSeconds(1);

    private readonly Action<byte[]> _send;
    private readonly Func<bool> _canSend;
    private readonly Func<long> _nowMs;
    private readonly Lock _gate = new();
    private readonly Timer _timer;

    private TimeSpan _interval;
    private long _lastActivityMs;
    private bool _disposed;

    /// <summary>构造一个防空闲注入器;构造后处于关闭状态,设了 <see cref="Interval" /> 才开始计时。</summary>
    /// <param name="send">把载荷送进出站队列的动作(与击键同一条路)。</param>
    /// <param name="canSend">此刻能不能注入:传输会话进行中、流不可写时必须为 false。</param>
    /// <param name="nowMs">单调毫秒时钟;仅测试替换。</param>
    public AntiIdleKeeper(Action<byte[]> send, Func<bool> canSend, Func<long>? nowMs = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _canSend = canSend ?? throw new ArgumentNullException(nameof(canSend));
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
        _lastActivityMs = _nowMs();
        _timer = new(static state => ((AntiIdleKeeper)state!).OnTick(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>注入间隔;<see cref="TimeSpan.Zero" />(或更小)= 关闭。赋值即重新计时。</summary>
    public TimeSpan Interval
    {
        get
        {
            lock (_gate)
            {
                return _interval;
            }
        }
        set
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _interval = value > TimeSpan.Zero ? value : TimeSpan.Zero;
                // 从此刻起算:刚打开开关的会话不该立刻挨一发,那只会让用户以为终端自己抽了一下。
                Volatile.Write(ref _lastActivityMs, _nowMs());
                Arm(_interval);
            }
        }
    }

    /// <summary>记一次出站活动:用户敲了键、程序注入了命令,或我们自己刚发过一发。</summary>
    public void NoteActivity() => Volatile.Write(ref _lastActivityMs, _nowMs());

    /// <summary>停表(可安全重复调用)。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _interval = TimeSpan.Zero;
        }
        _timer.Dispose();
    }

    /// <summary>测试探针:同步跑一次定时器回调,免得测试去睡真实的秒。</summary>
    internal void TickForTest() => OnTick();

    /// <summary>
    /// 到点了:够久没动静就注入一发,否则把闹钟改到"刚好满一个间隔"的那一刻。
    /// </summary>
    private void OnTick()
    {
        TimeSpan interval;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            interval = _interval;
        }
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        long period = (long)interval.TotalMilliseconds;
        long idle = _nowMs() - Volatile.Read(ref _lastActivityMs);
        TimeSpan next;
        if (idle < period)
        {
            // 这一轮里用户敲过键 —— 服务端的计时已经被他自己刷新了,我们不必插话。
            next = TimeSpan.FromMilliseconds(period - idle);
        }
        else if (_canSend())
        {
            _send(Payload);
            NoteActivity(); // 自己发的也算活动:下一发同样等满一个间隔。
            next = interval;
        }
        else
        {
            // ZMODEM 传输中或流正不可写。传输本身就是流量,服务端此刻不会认为会话空闲;
            // 等它结束再补,而不是把字节插进协议帧里(那会让整笔传输 CRC 错乃至失败)。
            next = RetryWhenBlocked;
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                Arm(next);
            }
        }
    }

    /// <summary>重排闹钟(单次触发,每轮由 <see cref="OnTick" /> 自己续上)。调用方须持有 <see cref="_gate" />。</summary>
    private void Arm(TimeSpan due)
    {
        try
        {
            if (due <= TimeSpan.Zero)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 撞上了:计时器已经没了,本就不该再响。
        }
    }
}
