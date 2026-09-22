using System.Buffers;
using System.Threading.Channels;
using Avalonia.Threading;
using VelaShell.Core.Ssh;

namespace VelaShell.Terminal;

/// <summary>
/// SSH ShellStream 与终端模拟器之间的桥接:后台读线程批量拉取主机输出、合并后在 UI 线程一次性喂入,
/// 并把用户输入写回 PTY。同时负责回显抑制与远端关闭通知。
/// </summary>
public class SshTerminalBridge : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    /// <summary>
    /// 一块待喂入终端的输出。数组租自 <see cref="ArrayPool{T}" />,排空后必须归还。
    /// </summary>
    /// <param name="Buffer">承载字节的数组。<b>可能比实际数据长</b>(池租的数组只保证 ≥ 请求长度)。</param>
    /// <param name="Length">有效字节数 —— 一律以它为准,绝不能用 <c>Buffer.Length</c>。</param>
    private readonly record struct PendingChunk(byte[] Buffer, int Length);

    private readonly List<PendingChunk> _pending = [];

    /// <summary>
    /// <see cref="FlushPending" /> 从 <see cref="_pending" /> 摘下来的待处理块。
    /// 提出来是为了让拼接与归还都在锁外做(锁只护摘取这一下),同时保住"归还必发生"的时机 ——
    /// 池数组只有在 Feed 同步消费完之后才能还。仅 UI 线程访问。
    /// </summary>
    private readonly List<PendingChunk> _draining = [];

    // 出站写队列:所有发往 PTY 的字节(击键 + SendRaw 注入)先入队,由唯一的写循环按序
    // 逐段 await 后刷出。绝不能对底层流并发 WriteAsync —— 通道的写不保证并发安全:
    // 两个写并发时会各自读发送窗口、交错切包,字节以乱序抵达远端;
    // 远端 shell 按收到的顺序回显,屏幕上就是"打 docker status 出来字符拆散跳动"。
    // 打字稍快 + 网络延迟让上一个写挂起 await,下一个按键就会插队,竞态必现。
    // (SSH.NET 的 ShellStream 内部有锁掩盖了这一点,迁到 Tmds.Ssh 后才暴露出来;
    //  串行化这件事是宿主该保证的,不该指望换哪个库能替我们兜住。)
    private readonly Channel<OutboundItem> _writeQueue = Channel.CreateUnbounded<OutboundItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly Task _writeTask;

    /// <summary>
    /// 出站队列元素:待发载荷 + 可选的排空信号(仅测试探针使用,写循环处理到该元素
    /// 且其前所有写都已完成后置位)。
    /// </summary>
    private readonly record struct OutboundItem(byte[] Data, TaskCompletionSource? Drained);

    // 输出合批泵:读线程把原始分块入队,并只请求一次 UI 线程的合并刷新,
    // 而非每次读取都编组并喂入一次。在突发输出(apt/yum、cat、进度条)下,这把
    // 数百次跨线程跳转 + 整屏重绘,压缩成每帧一次 Feed。
    private readonly Lock _pendingLock = new();
    private readonly IShellStreamWrapper _shellStream;
    private readonly ITerminalEmulator _terminal;
    private volatile bool _disposed;

    // 连接初始化命令的回显抑制器(静默执行);仅在 UI 线程读写(Arm 与 FlushPending 同线程)。
    // volatile:注入现在推迟到"对端安静"之后,由线程池上的轮询触发(见 RunWhenOutputIdle),
    // 因此这个字段会被 UI 线程(FlushPending)与线程池线程(OpenInjectionWindow)同时碰。
    // EchoSuppressor 自身是加锁的,这里只需保证引用的可见性。
    private volatile EchoSuppressor? _echoSuppressor;

    // 同一个抑制针的第二份实例,专供旁路记录(DataReceived → 会话日志 / 会话录制)。
    // 记录挂在读线程的原始流上,拿不到显示路径抑制后的结果 —— 于是注入的初始化脚本
    // 在终端里看不见,回放时却整行冒出来。两条路径看的是同一份字节流,但在不同线程上消费,
    // 共用一个实例会踩坏 EchoSuppressor 的 _held/_hitsLeft 状态,故各持一份。
    // volatile:由 UI 线程装配(SuppressEchoOnce),由读线程消费。
    private volatile EchoSuppressor? _tapEchoSuppressor;
    private int _flushScheduled;
    private Task? _readTask;
    private int _started;

    // 防空闲注入器。默认关闭(间隔为零),由宿主按会话配置打开 —— 本地终端与没配这一项的
    // 会话上它一个字节都不会发。构造在字段初始化里而不是等宿主来设,是为了让"记一次活动"
    // 这件事在整条出站路径上无条件成立,不必到处判空。
    private readonly AntiIdleKeeper _antiIdle;

    /// <summary>绑定终端模拟器与 Shell 流,并订阅终端的用户输入事件。</summary>
    public SshTerminalBridge(ITerminalEmulator terminal, IShellStreamWrapper shellStream)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _cts = new();
        _antiIdle = new(EnqueueOutbound, CanInjectAntiIdle);
        _terminal.UserInput += OnUserInput;
        _writeTask = Task.Run(WriteLoopAsync);
    }

    /// <summary>停止读循环、退订输入事件并释放 Shell 流与取消源(可安全重复调用)。</summary>
    /// <remarks>
    /// 释放是异步的:它要等读写循环真的退出、等 shell 通道关完。
    /// 原先是同步 <c>Dispose</c> 加两处 <c>Task.Wait(超时)</c> —— 那是在 UI 线程上
    /// 阻塞等网络收尾,超时只是把「卡死」换成「卡两秒」。现在一路 await:
    /// 关标签页这个动作在收尾真的完成之后才算完,过程中也不占着 UI 线程。
    /// 收尾仍带一份预算(见 <see cref="DrainAsync" />),但那是给不守约定的流实现兜底的,
    /// 不是替代等待本身 —— 守约定的流一被释放,两个循环立刻退出,预算一秒都用不上。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _terminal.UserInput -= OnUserInput;

        // 防空闲的闹钟第一个停:它在线程池上跑,晚一步就可能往正在拆的流上再送一发。
        _antiIdle.Dispose();

        // 封口写队列:写循环排空残余(_disposed 已置位,只弃不写)后自行退出。
        _writeQueue.Writer.TryComplete();

        // 读循环可能正等在背压闸上(积压超高水位)。此刻 UI 再也不会来排空了,
        // 不放行它就会一直挂在那里 —— 下面的 DrainAsync 白等满 2 秒预算才返回。
        ReleaseDrainGate();

        // 先释放流、后取消令牌:释放流会以"通道关闭"唤醒挂起的读取,包装层将其吞为 EOF,
        // 读循环无异常退出。若先 Cancel,取消会以 OperationCanceledException 打穿底层库的
        // 整条异步读栈,每次关标签都在调试器里刷一串首次机会异常。令牌保留为兜底:
        // 个别实现的 Dispose 若未能唤醒读取,Cancel 仍能让循环退出。
        //
        // 释放本身也带预算:SSH 通道的释放要往对端发 CHANNEL_CLOSE,半死的链路上(发送缓冲
        // 塞满、对端卡在重协商里)这一发可能挂上几分钟;插件的流更是第三方代码。等不到就
        // 放它在后台自己收尾 —— 关标签与重连不能被一条坏掉的链路拖住。
        Task dispose = DisposeStreamQuietlyAsync(_shellStream);
        try
        {
            await dispose.WaitAsync(StreamDisposeBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 预算用完:释放仍在后台进行,异常已由 DisposeStreamQuietlyAsync 吞掉。
        }
        await _cts.CancelAsync().ConfigureAwait(false);

        // 等两个循环真的退出 —— 但**不无限等**。「释放流会唤醒挂起的读取」是实现方的约定,
        // 自带的 ShellStreamWrapper 与 ConPtyShellStream 都守着;插件提供的终端协议流是第三方
        // 代码,不守时这里就永远等不回来,而拆桥正处在关标签与退出这两条路上。
        // 等法本身是异步的(await,不是 Task.Wait):既不占线程池线程,也不阻塞 UI。
        bool readDrained = await DrainAsync(_readTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        // 流已释放:挂起中的写以 ObjectDisposedException 醒来并被吞掉,循环随即因封口退出。
        bool writeDrained = await DrainAsync(_writeTask, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        if (readDrained && writeDrained)
        {
            _cts.Dispose();
            // 闸最后释放:两个循环都退出了,读循环已经不可能再碰它。
            _drainGate.Dispose();
        }
        // 没排空就**不释放**:循环还活着,这会儿释放只会让它在 _drainGate.WaitAsync 或
        // CTS 属性上炸 ObjectDisposedException。两者都不持有非托管句柄,交给 GC 是安全的。
        GC.SuppressFinalize(this);
    }

    /// <summary>释放 shell 流的预算;超出之后不再等,释放在后台继续。</summary>
    private static readonly TimeSpan StreamDisposeBudget = TimeSpan.FromSeconds(2);

    private static async Task DisposeStreamQuietlyAsync(IShellStreamWrapper stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 尽力而为:通道可能已被会话断开拆除。
        }
    }

    /// <summary>等一个循环退出,最多等 <paramref name="budget" />;返回它是否真的退出了。</summary>
    /// <remarks>
    /// 返回值决定调用方还能不能释放循环仍在使用的同步原语 —— 超时后释放它们,
    /// 换来的只是循环里一串 <see cref="ObjectDisposedException" />。
    /// </remarks>
    private static async ValueTask<bool> DrainAsync(Task? loop, TimeSpan budget)
    {
        if (loop is null)
        {
            return true;
        }
        try
        {
            await loop.WaitAsync(budget).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放期间循环抛出的异常一律吞掉。它究竟退出没有以 IsCompleted 为准,
            // 而不是以异常类型 —— 循环自己抛 TimeoutException 也不该被当成"没退出"。
        }
        return loop.IsCompleted;
    }

    /// <summary>读写或喂入终端过程中发生异常时触发。</summary>
    public event Action<Exception>? Error;

    /// <summary>
    /// 主机输出分块,在读线程上触发 —— 供会话日志(设置 → 常规)与会话录制
    /// (设置 → 安全审计)使用。订阅者必须快速返回且绝不抛异常。
    /// 与显示路径一样剥除了连接初始化命令的回显(见 <see cref="SuppressEchoOnce" />):
    /// 注入的脚本在终端里既然是隐形的,记录与回放里也不该冒出来。
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// 当远端关闭通道时触发(例如 shell 执行了 <c>exit</c> 或
    /// 服务器重启):读循环自行结束,而非经由 <see cref="DisposeAsync" />。
    /// 使会话可转为断开状态并就地重连。
    /// 主动拆除期间不会触发。在读取线程上触发——按需封送。
    /// <para>
    /// 参数是「为什么结束」:<c>exit</c> 与掉线在这里必须分得开,否则宿主只能
    /// 把两者一视同仁地自动连回去,用户就退不掉了(#383)。
    /// </para>
    /// </summary>
    public event Action<ShellCloseReason>? Closed;

    /// <summary>
    /// 在输出流上剥除即将注入的命令回显(见 <see cref="EchoSuppressor" />)。
    /// 回显最多出现两次(内核规范模式 + readline 预输入重绘),窗口过后自动失效。
    /// 显示路径与旁路记录路径(<see cref="DataReceived" />)各装一份实例,理由见字段注释。
    /// </summary>
    /// <param name="needle">注入的整行(含换行),即 PTY 会回显出来的那串字节。</param>
    /// <remarks>
    /// <para>
    /// <b>只给短命令用。</b>整行一旦超出终端宽度,各家 shell 的折行重绘就没法逐字节匹配了
    /// (真机实测三种形态);那种情形走 <see cref="OpenInjectionWindow" />。
    /// 这条路今天只服务两种场景:探不出 shell 种类、或对端是 cmd.exe / PowerShell ——
    /// 那上面 <c>printf</c> 未必存在,哨兵回不来,窗口只能白等到超时。
    /// </para>
    /// <para>
    /// <b>连着发好几条时是「加一根针」,不是「换一个抑制器」。</b>握手那一串
    /// (目录上报脚本 → 初始目录 <c>cd</c> → 认证后命令)是**同一瞬间**连着写进 PTY 的,
    /// 换实例会让前一个在见到任何数据之前就被顶掉 —— 它那行回显于是原样留在屏幕上
    /// (用户看到的那串 <c>test -n "${BASH_VERSION:-}" &amp;&amp; eval …</c> 正是这么来的)。
    /// 加针对<b>注入窗口</b>那个实例同样成立:窗口闭合后它退回剥针,后面那几条照样剥得掉。
    /// </para>
    /// </remarks>
    public void SuppressEchoOnce(byte[] needle)
    {
        // 太短的针不装:构造会抛(短针在流里到处都是,扣住它会剪坏正常输出)。
        // 真会走到这里的是"非 POSIX 对端 + 一条极短的认证后命令"(<c>w</c> 之类)——
        // 那一行的回显会露在屏幕上,但那远好过握手时甩一个异常出来。
        if (needle.Length < EchoSuppressor.MinNeedleBytes)
        {
            return;
        }
        if (_echoSuppressor is { Expired: false } active)
        {
            active.AddNeedle(needle);
        }
        else
        {
            _echoSuppressor = new(needle, 2, TimeSpan.FromSeconds(10));
        }
        if (_tapEchoSuppressor is { Expired: false } tap)
        {
            tap.AddNeedle(needle);
        }
        else
        {
            _tapEchoSuppressor = new(needle, 2, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// 只开一个「注入窗口」:从这一刻起扣住显示路径上的一切,直到 <paramref name="sentinel" /> 出现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内置的目录上报脚本走这条路而不是抑制针:它有八九百字符,超出终端宽度之后各家 shell
    /// 的回显重绘五花八门(ash 插 <c>CR LF</c>、zsh 重复断点字符、fish 整行重排),
    /// 逐字节匹配根本咬不住 —— 真机上三种全撞到了。整段扣住反而简单且必然正确。
    /// 理由详见 <see cref="EchoSuppressor.OpenWindow" />。
    /// </para>
    /// <para>
    /// <b>调用方必须先等对端安静下来</b>(<see cref="RunWhenOutputIdle" />),否则横幅 / MOTD
    /// 会连同注入的副作用一起被扣住。
    /// </para>
    /// <para>
    /// <b>旁路记录(会话日志 / 录制)不装窗口</b>:日志要如实记下远端到底回了什么。
    /// 注入行的回显因此会出现在日志里 —— 那是排障线索,而且长注入的回显本来也剥不干净。
    /// </para>
    /// </remarks>
    public void OpenInjectionWindow(byte[] sentinel)
    {
        if (sentinel.Length == 0)
        {
            return;
        }
        _echoSuppressor = EchoSuppressor.OpenWindow(sentinel, TimeSpan.FromSeconds(10));
        ArmGateFlushTimer();
    }

    /// <summary>
    /// 等对端<b>安静下来</b>再执行 <paramref name="action" />(UI 线程)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 连上之后横幅、MOTD、rc 的欢迎语、第一个提示符会连着涌过来。注入窗口是"从现在起整段扣住",
    /// armed 得太早就会把这些一起吞掉 —— 用户登进去面对一块空屏,那比看见一行报错糟糕得多。
    /// 所以等:至少收到过一块输出,且此后静默满 <paramref name="idle" />,再动手。
    /// </para>
    /// <para>
    /// <paramref name="max" /> 是兜底:有的服务器登录后一直在刷东西(动态 MOTD、fortune、
    /// 实时日志),不能为此永远不注入。到点就注,大不了窗口里多扣几行 —— 到期照样放行。
    /// </para>
    /// <para>
    /// electerm 也在等(它等的是"第一块数据到达"),但只等到第一块就动手,横幅还没放完 ——
    /// 这里多等一个静默期,代价是几百毫秒,换的是横幅一个字节都不会丢。
    /// </para>
    /// </remarks>
    /// <param name="idle">静默多久算"安静了"。</param>
    /// <param name="max">最长等这么久,到点无条件执行。</param>
    /// <param name="action">
    /// 只执行一次的动作。<b>在线程池线程上执行,不编组回 UI 线程</b> ——
    /// 注入这条路上真正被碰的只有 <see cref="_echoSuppressor" />(volatile + 自身加锁)
    /// 与出站写队列(本来就是并发安全的)。刻意不编组是因为编组会把"能不能注入"绑死在
    /// "UI 线程此刻有没有在处理作业"上,而那件事在测试宿主里并不总是成立,
    /// 在真实界面卡顿时也会平白拖慢注入。真正非 UI 线程不可的那点收尾(定时兜底、
    /// 喂终端)各自再 Post 回去。
    /// </param>
    public void RunWhenOutputIdle(TimeSpan idle, TimeSpan max, Action action)
    {
        DateTime deadline = DateTime.UtcNow + max;
        var poll = TimeSpan.FromMilliseconds(50);
        bool ReadyNow()
        {
            // 流都读不了了(远端已关、或压根没接上),就不会再有输出了 —— 等什么安静。
            if (!_shellStream.CanRead)
            {
                return true;
            }
            return Volatile.Read(ref _sawOutput)
                   && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastOutputTicks), DateTimeKind.Utc) >= idle;
        }
        // 先同步判一次:条件已经成立时不该平白多等一个轮询周期,
        // 而且这让"流根本没接上"这种情形在调用点就地结束,不依赖任何定时器。
        if (ReadyNow())
        {
            action();
            return;
        }

        // 轮询放在线程池上、只把最后那一下 Post 回 UI 线程,而不是用 DispatcherTimer 连环续期:
        // 前者只依赖"UI 线程会处理 Post 进来的作业"这一条(输出泵本来就靠它),
        // 后者还额外依赖调度器的定时器队列被驱动 —— 而 headless 测试里那条队列不一定在跑。
        _ = Task.Run(async () =>
        {
            while (!_disposed && DateTime.UtcNow < deadline && !ReadyNow())
            {
                await Task.Delay(poll).ConfigureAwait(false);
            }
            if (_disposed)
            {
                return;
            }
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 这是个即发即弃的任务:不接住就是一个**无人观察**的异常 ——
                // 注入悄无声息地没发生,屏幕上什么都不显示,查都没处查。
                Error?.Invoke(ex);
            }
        });
    }

    /// <summary>最后一次收到远端输出的时刻(UTC ticks);读线程写、UI 线程读。</summary>
    private long _lastOutputTicks = DateTime.UtcNow.Ticks;

    /// <summary>收到过远端输出没有 —— 一个字节都还没来的时候不该判定为"安静"。</summary>
    private bool _sawOutput;

    /// <summary>
    /// 给注入窗口挂一个一次性的兜底定时器。
    /// </summary>
    /// <remarks>
    /// 吞噬阶段靠"下一块输出到来"推进,而注入失败时远端<b>恰恰不会再有输出</b> ——
    /// 报错和提示符都已经扣在抑制器手里,没人再来敲门,屏幕就空在那儿等着用户按回车。
    /// 定时器到点把它们放出来(<see cref="EchoSuppressor.ForceFlush" />),
    /// 因此屏幕最差也只是"和以前一样"。
    /// <para>
    /// 比抑制器自己的吞噬窗口(<see cref="EchoSuppressor.DefaultSwallowWindow" />)多给
    /// 250 毫秒:定时器只是兜底,正常路径上应该是 <see cref="FlushPending" /> 里那次
    /// Process 先把窗口关掉,而不是靠它。两个时长因此绑在同一个常量上,改一处即可。
    /// </para>
    /// </remarks>
    private void ArmGateFlushTimer()
    {
        // 定时器与随后的 FeedTerminal 都只能在 UI 线程上做,而武装动作本身可能来自线程池
        // (注入被推迟到"对端安静"之后,见 RunWhenOutputIdle)。所以先 Post 过去再挂。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ArmGateFlushTimer);
            return;
        }
        EchoSuppressor? armed = _echoSuppressor;
        DispatcherTimer.RunOnce(
            () =>
            {
                // 认实例而不是认"有没有抑制器":这几秒里可能又注入了一条命令(用户配的
                // 启动命令紧随其后),那时 _echoSuppressor 已经是**别人**了,不该被这次定时器收尾。
                if (_disposed || !ReferenceEquals(_echoSuppressor, armed) || armed is null)
                {
                    return;
                }
                // ForceFlush 只结束**吞噬**,抑制器随后退回继续剥针 —— 后面还排着
                // 第二、第三条注入的回显没剥,这里把实例丢掉就等于把它们漏到屏幕上。
                byte[] pending = armed.ForceFlush();
                if (armed.Expired)
                {
                    _echoSuppressor = null;
                }
                if (pending.Length > 0)
                {
                    FeedTerminal(pending, pending.Length);
                }
            },
            EchoSuppressor.DefaultSwallowWindow + TimeSpan.FromMilliseconds(250)
        );
    }

    /// <summary>
    /// 程序化注入:直写 PTY,不经终端控件的输入事件。连接初始化命令(用户配置的
    /// 启动命令)必须走这里——若走 WriteInput,注入里的 ESC 字节会把命令补全的行跟踪器
    /// (plan.md #16)打进未知态,SSH 标签的智能建议从连接起就全灭(实测取证)。
    /// </summary>
    public void SendRaw(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
        {
            return;
        }
        EnqueueOutbound(data);
    }

    /// <summary>
    /// 防空闲断开:会话静默满这么久就往 PTY 送一个 NUL,让服务端重新开始计空闲。
    /// <see cref="TimeSpan.Zero" /> = 关闭(默认)。
    /// </summary>
    /// <remarks>
    /// 与 SSH 的保活心跳互补而不重叠:心跳是协议层的,防的是 NAT 收连接;这一项防的是
    /// 服务端 shell 按空闲踢人(<c>TMOUT</c>、堡垒机超时),而那是按 tty 上有没有输入算的。
    /// 由宿主按会话配置设定,细节见 <see cref="AntiIdleKeeper" />。
    /// </remarks>
    public TimeSpan AntiIdleInterval
    {
        get => _antiIdle.Interval;
        set => _antiIdle.Interval = value;
    }

    /// <summary>此刻能不能注入防空闲字节。</summary>
    private bool CanInjectAntiIdle() => !_disposed && _shellStream.CanWrite;

    /// <summary>测试探针:同步跑一次防空闲的定时器回调。</summary>
    internal void AntiIdleTickForTest() => _antiIdle.TickForTest();

    /// <summary>
    /// 测试探针:返回的任务在"此刻已入队的所有写全部落到底层流"后完成。
    /// 借道队列本身实现(入队一个空载荷哨兵),因此对时序零假设——替代测试里的 Thread.Sleep。
    /// </summary>
    internal Task DrainWritesAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeQueue.Writer.TryWrite(new([], tcs)))
        {
            tcs.SetResult(); // 队列已封口(Dispose 后):没有在途写可等。
        }
        return tcs.Task;
    }

    /// <summary>启动后台读循环;仅允许调用一次,重复调用会抛出异常。</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("Bridge already started");
        }

        // 只启动读取,不要用换行符预热 shell —— 服务器在连接时本就会发送标语和提示符,
        // 多余的 '\n' 会制造出重复的提示符行。令牌在此处快照,因为 Dispose 会在 2 秒宽限后
        // 释放 _cts —— 仍在排空的循环此后不得再触碰 CTS 属性(令牌读取仍有效)。
        CancellationToken token = _cts.Token;
        _readTask = Task.Run(() => ReadLoopAsync(token));
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        // 更大的读取缓冲意味着更少的 await 与更大的自然批次。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        bool remoteClosed = false;

        // 结束的原因由流给出(它才知道是干净的 EOF 还是抛出来的),读循环只负责转述。
        ShellCloseReason closeReason = ShellCloseReason.Unknown;
        try
        {
            while (!token.IsCancellationRequested && _shellStream.CanRead)
            {
                int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    // EOF:远端已关闭通道(exit / 重启 / 连接断开)。到底是哪一种,问流。
                    remoteClosed = true;
                    closeReason = _shellStream.CloseReason;
                    break;
                }

                // 每块仍要一份自己的副本(读缓冲下一轮就被覆写,而 UI 线程晚一点才排空),
                // 但副本租自池:这里原先是 `new byte[bytesRead]`,cat 一个大文件时它就是
                // 整条输出链路上最大的一笔分配(每秒上千块、每块至多 16KB)。
                // 归还发生在 FlushPending 把它喂完之后,见 PendingChunk.Pooled。
                byte[] data = ArrayPool<byte>.Shared.Rent(bytesRead);
                buffer.AsSpan(0, bytesRead).CopyTo(data);

                // 记录/回放拿到的流与屏幕一致:注入的初始化脚本回显在此剥除。
                // 无论有无订阅者都要跑,否则抑制器的跨块状态会与实际流脱节。
                // 整块被剥光(或被扣下等下一块续判)时无事可记。
                // 抑制器与订阅者的签名都是精确长度的 byte[],故此处才物化 —— 两者都是
                // 冷路径(抑制器只活在连接后的最初几秒,日志/录制默认关闭),稳态不经过。
                if (_tapEchoSuppressor is not null || DataReceived is not null)
                {
                    byte[] logged = SuppressTapEcho(data.AsSpan(0, bytesRead).ToArray());
                    if (logged.Length > 0)
                    {
                        try
                        {
                            DataReceived?.Invoke(logged);
                        }
                        catch
                        {
                            // 日志订阅者异常不允许打断读循环。
                        }
                    }
                }

                // 不要为每次读取都 await 一次 UI 跳转。把分块入队并合并;读线程
                // 跟得上网络节奏,而 UI 以帧率排空。原始块零拷贝进合批队列(所有权移交队列)。
                EnqueueForFeed(new(data, bytesRead));

                // 入队之后再看积压:UI 排不过来就在这里等一等,让 SSH 流控把压力回传给远端。
                // 放在循环末尾而不是开头,是为了让本轮读到的数据先落进队列 —— 否则
                // 高水位时读到的那一块会在等待期间一直占着读缓冲。
                await ApplyBackpressureAsync(token).ConfigureAwait(false);
            }

            // 当流报告自身不再可读时,循环也会退出。
            if (!token.IsCancellationRequested)
            {
                remoteClosed = true;
                closeReason = _shellStream.CloseReason;
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭过程中预期会出现,不算错误
        }
        catch (ObjectDisposedException)
        {
            // 关闭过程中流已被释放,不算错误
        }
        catch (Exception ex)
        {
            // 抛到这里的都不是「远端 shell 正常退出」—— 那条路是干净的 EOF,不经过 catch。
            remoteClosed = true;
            closeReason = ShellCloseReason.ConnectionLost;
            Error?.Invoke(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 表示远端主动关闭,但不包括我们自身 Dispose() 驱动的拆除。
        if (remoteClosed && !_disposed)
        {
            Closed?.Invoke(closeReason);
        }
    }

    /// <summary>
    /// 旁路记录路径的回显抑制(读线程独占该实例)。未装配或已失效时原样返回,零开销。
    /// </summary>
    private byte[] SuppressTapEcho(byte[] data)
    {
        if (_tapEchoSuppressor is not { } suppressor)
        {
            return data;
        }
        byte[] result = suppressor.Process(data);
        if (suppressor.Expired)
        {
            result = AppendHeldTail(result, suppressor);
            _tapEchoSuppressor = null;
        }
        return result;
    }

    /// <summary>
    /// 弃用抑制器前把它扣住的块尾交还输出流。扣住的字节本该在下一次 Process 里放出来,
    /// 实例一弃用就没有下一次了——不接回来就是永久吞字节。
    /// 扣住的必然是本块的尾巴,故追加在后面。
    /// </summary>
    private static byte[] AppendHeldTail(byte[] head, EchoSuppressor suppressor)
    {
        byte[] tail = suppressor.TakeHeld();
        if (tail.Length == 0)
        {
            return head;
        }
        byte[] merged = new byte[head.Length + tail.Length];
        head.CopyTo(merged, 0);
        tail.CopyTo(merged, head.Length);
        return merged;
    }

    private void EnqueueForFeed(PendingChunk chunk)
    {
        // 记一笔"刚收到输出":RunWhenOutputIdle 靠它判断对端安静了没有。记在读线程而不是
        // UI 线程,是因为注入的时机该跟着**网络**走,不该跟着界面排空的节奏走。
        Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _sawOutput, true);
        lock (_pendingLock)
        {
            _pending.Add(chunk);
        }
        Interlocked.Add(ref _pendingBytes, chunk.Length);

        // 最多只调度一次待处理的 UI 刷新;后续分块搭它的便车。
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(FlushPending);
        }
    }

    /// <summary>
    /// 积压过高时把读线程按住,等 UI 把它排到低水位以下再继续。
    /// </summary>
    /// <remarks>
    /// 读线程一停,SSH 接收窗口不再推进,压力顺着流控回传到远端 —— 远端的 `cat` 自己会慢下来。
    /// 这是 OpenSSH 客户端的行为,也是"内存有上限"的唯一可靠办法:
    /// 只在本地丢弃或无限攒着,都是把问题留给用户。
    /// </remarks>
    private async Task ApplyBackpressureAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Read(ref _pendingBytes) <= HighWaterBytes)
        {
            return;
        }
        try
        {
            await _drainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose 与本次等待撞上了:读循环随即会因取消而退出。
        }
    }

    // 多 chunk 合批的复用缓冲:只增不缩,仅在 UI 线程的 FlushPending 内访问,
    // Feed 同步消费不留引用,因此跨帧复用安全。
    private byte[] _combineBuffer = [];

    // ---- 洪流控制:每帧解析预算 + 读线程背压 ----
    //
    // 合批本身是对的(它把上百次跨线程跳转压成每帧一次),但原先没有上限:
    // `cat` 一个几百 MB 的文件、或 `tail -f` 一个刷得很猛的日志,两帧之间能攒下几十 MB,
    // UI 线程在**一个** Dispatcher 回调里把它们全解析完 —— 期间界面冻结、滚动条不响应、
    // 别的标签也不刷新;内存则随读取速度无限增长(每块租自 ArrayPool,但池只是延迟归还,
    // 不限制总量)。
    //
    // 两道闸:
    //   ① 每帧最多解析 FeedBudgetBytes,剩下的以 Background 优先级续帧 —— 界面始终可交互;
    //   ② 积压超过 HighWaterBytes 时读线程等在 _drainGate 上,降到 LowWaterBytes 再放行。
    //      读线程一停,SSH 的接收窗口就不再推进,压力顺着流控自然回传到远端 ——
    //      远端的 `cat` 会自己慢下来。这正是 OpenSSH 客户端的行为。

    /// <summary>每帧最多交给模拟器解析的字节数。</summary>
    private const int FeedBudgetBytes = 1 << 20; // 1 MB

    /// <summary>积压高水位:超过它读线程就等。</summary>
    private const long HighWaterBytes = 8L << 20; // 8 MB

    /// <summary>积压低水位:降到它以下才放读线程继续。</summary>
    private const long LowWaterBytes = 2L << 20; // 2 MB

    /// <summary>当前 <see cref="_pending" /> 里积压的字节数。</summary>
    private long _pendingBytes;

    /// <summary>
    /// 读线程的等待闸。初值 0 = 关着;<see cref="ReleaseDrainGate" /> 放一次行。
    /// </summary>
    /// <remarks>
    /// 上限 1:重复放行不该攒出配额,否则读线程能连着冲过好几轮高水位。
    /// <para>
    /// <b>积压的实际上界是 <see cref="HighWaterBytes" /> + 两块。</b>一块来自"越界的那一次入队"
    /// (高水位是入队之后才判的);另一块来自一张陈旧许可 —— 积压跌到低水位时读线程可能
    /// 并没有在等,那次 <c>Release</c> 就留在信号量里,下一次 <c>WaitAsync</c> 会立刻拿到它、
    /// 不真的等,于是多放过一块。再下一轮就会真等住。以 16 KB 的读块算,超出上限约 32 KB,
    /// 不值得为它引入"有没有人在等"的额外记账。
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _drainGate = new(0, 1);

    /// <summary>当前积压字节数(背压回归用例读它)。</summary>
    internal long PendingBytesForTest => Interlocked.Read(ref _pendingBytes);

    /// <summary>
    /// 高水位(背压回归用例读它)。用例要等的是"读线程真的顶到闸上",
    /// 判据就是积压越过这个数 —— 让它从这里取,而不是在测试里另抄一份 8 MB。
    /// </summary>
    internal static long HighWaterBytesForTest => HighWaterBytes;

    /// <summary>最近一次 Feed 交出去的字节数(预算回归用例读它)。</summary>
    internal int LastFeedBytesForTest { get; private set; }

    /// <summary>放行等在闸上的读线程;没人等就是空操作。</summary>
    private void ReleaseDrainGate()
    {
        // CurrentCount 已经是 1 时再 Release 会抛 SemaphoreFullException。
        if (_drainGate.CurrentCount == 0)
        {
            try
            {
                _drainGate.Release();
            }
            catch (SemaphoreFullException)
            {
                // 与另一个放行者撞上了:闸已经开着,正是想要的结果。
            }
            catch (ObjectDisposedException)
            {
                // 已 Dispose:读循环也已经在退了。
            }
        }
    }

    private void FlushPending()
    {
        // 先重置,使排空期间到达的分块能调度一次全新刷新。
        Interlocked.Exchange(ref _flushScheduled, 0);

        bool more;
        int taken = 0;
        // 只在锁内摘取,拼接/喂入/归还都在锁外做 —— 读线程不会被 UI 的这段活儿挡住。
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            // 每帧只摘 FeedBudgetBytes,剩下的留到下一帧 —— 见 FeedBudgetBytes 的说明。
            // 「至少摘一块」是必须的:单块本身就超预算时若一块不摘,这里会空转成死循环。
            int index = 0;
            while (index < _pending.Count
                   && (taken == 0 || taken + _pending[index].Length <= FeedBudgetBytes))
            {
                taken += _pending[index].Length;
                _draining.Add(_pending[index]);
                index++;
            }
            _pending.RemoveRange(0, index);
            more = _pending.Count > 0;
        }
        long pendingNow = Interlocked.Add(ref _pendingBytes, -taken);
        // 降到低水位以下就放读线程继续跑(见 _drainGate)。
        if (pendingNow <= LowWaterBytes)
        {
            ReleaseDrainGate();
        }
        if (more)
        {
            // 续帧用 Background 而不是默认的 Normal:Avalonia 的 Render 优先级高于
            // Background、低于 Normal。用 Normal 续帧会把渲染饿死 —— 界面照样冻住,
            // 分片就等于白做。用 Background 则是"渲染完这一帧,再解析下一批"。
            Interlocked.Exchange(ref _flushScheduled, 1);
            Dispatcher.UIThread.Post(FlushPending, DispatcherPriority.Background);
        }
        try
        {
            if (_disposed)
            {
                return;
            }
            byte[] buffer;
            int length;
            if (_draining.Count == 1)
            {
                (buffer, length) = _draining[0];
            }
            else
            {
                int total = 0;
                for (int i = 0; i < _draining.Count; i++)
                {
                    total += _draining[i].Length;
                }
                if (_combineBuffer.Length < total)
                {
                    // 2 倍步进摊平增长成本,避免突发行情下反复重分配。
                    _combineBuffer = new byte[Math.Max(total, _combineBuffer.Length * 2)];
                }
                int offset = 0;
                for (int i = 0; i < _draining.Count; i++)
                {
                    PendingChunk chunk = _draining[i];
                    Array.Copy(chunk.Buffer, 0, _combineBuffer, offset, chunk.Length);
                    offset += chunk.Length;
                }
                buffer = _combineBuffer;
                length = total;
            }
            if (_echoSuppressor is { } suppressor)
            {
                // 抑制窗只覆盖连接后的最初几秒:此路径物化精确数组无妨,稳态热路径不经过。
                byte[] exact = buffer.AsSpan(0, length).ToArray();
                exact = suppressor.Process(exact);
                if (suppressor.Expired)
                {
                    exact = AppendHeldTail(exact, suppressor);
                    _echoSuppressor = null;
                }
                if (exact.Length == 0)
                {
                    return;
                }
                buffer = exact;
                length = exact.Length;
            }
            try
            {
                // 每次刷新只 Feed 一次 => 一次 Updated => 一次重绘,与分块数量无关。
                LastFeedBytesForTest = length;
                FeedTerminal(buffer, length);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }
        finally
        {
            // 归还必须发生在 Feed 之后(它同步消费,不留引用),且每条退出路径都要走到 ——
            // 提前 return(已 Dispose、抑制器把整块吃光)同样得还,否则就是池泄漏。
            for (int i = 0; i < _draining.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(_draining[i].Buffer);
            }
            _draining.Clear();
        }
    }

    /// <summary>
    /// 把合批结果喂给模拟器:生产中的具体实现(VelaTerminalControl)走 span 直喂
    /// (复用缓冲零物化);其它 ITerminalEmulator 实现(测试替身)回退 byte[] 语义——
    /// 接口不宜引入 span 成员,ref struct 参数无法被常规 mock 框架替身化。
    /// </summary>
    private void FeedTerminal(byte[] buffer, int length)
    {
        if (_terminal is Rendering.VelaTerminalControl control)
        {
            control.Feed(buffer.AsSpan(0, length));
        }
        else
        {
            _terminal.Feed(length == buffer.Length ? buffer : buffer[..length]);
        }
    }

    private void OnUserInput(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
        {
            return;
        }

        // 只入队不直写:击键与 SendRaw 都在 UI 线程触发,TryWrite 保序;真正的发送
        // 由唯一的写循环按序完成,杜绝对底层通道的并发 WriteAsync(见 _writeQueue 注释)。
        EnqueueOutbound(data);
    }

    /// <summary>
    /// 出站的唯一入口:入队,并把这一下记成一次活动。
    /// </summary>
    /// <remarks>
    /// 记账放在这里而不是各调用点,是因为"最近一次往对端写字节是什么时候"必须<b>一处不漏</b> ——
    /// 漏掉击键这一路,防空闲就会在用户正打字的时候插字节;漏掉注入这一路,它又会在
    /// 刚发过一发之后立刻再发一发。
    /// </remarks>
    private void EnqueueOutbound(byte[] data)
    {
        _antiIdle.NoteActivity();
        _writeQueue.Writer.TryWrite(new(data, null));
    }

    /// <summary>
    /// 唯一的出站写者:按入队顺序逐段写入并等待完成,一段未落盘绝不开始下一段。
    /// 上一段写入挂起期间(发送窗口收紧、网络延迟)攒下的后续段合并为一次写出——
    /// 语义上等价于按序逐段发送,只是少切几个 SSH 包。
    /// </summary>
    private async Task WriteLoopAsync()
    {
        while (await _writeQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_writeQueue.Reader.TryRead(out OutboundItem item))
            {
                byte[] payload = item.Data;
                List<TaskCompletionSource>? drains = null;
                CollectDrain(ref drains, item);

                // 排空本轮已积压的元素。只有攒到第二段非空载荷才物化合并缓冲:
                // 常态(单段 + 至多一个哨兵)保持零拷贝直传原数组。
                MemoryStream? merged = null;
                while (_writeQueue.Reader.TryRead(out OutboundItem more))
                {
                    CollectDrain(ref drains, more);
                    if (more.Data.Length == 0)
                    {
                        continue; // 哨兵:只收信号,不参与载荷。
                    }
                    if (payload.Length == 0)
                    {
                        payload = more.Data;
                        continue;
                    }
                    if (merged is null)
                    {
                        merged = new MemoryStream(payload.Length + more.Data.Length + 64);
                        merged.Write(payload);
                    }
                    merged.Write(more.Data);
                }
                if (merged is not null)
                {
                    payload = merged.ToArray();
                }
                if (payload.Length > 0 && !_disposed && _shellStream.CanWrite)
                {
                    try
                    {
                        await _shellStream.WriteAsync(payload, 0, payload.Length, CancellationToken.None).ConfigureAwait(false);
                        _shellStream.Flush();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 流已释放——拆除期间属正常情况
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(ex);
                    }
                }
                // 写成功、写失败、拆除弃段——排空信号一律置位:探针等的是"处理完",不是"送达"。
                drains?.ForEach(d => d.TrySetResult());
            }
        }
    }

    private static void CollectDrain(ref List<TaskCompletionSource>? drains, in OutboundItem item)
    {
        if (item.Drained is { } tcs)
        {
            (drains ??= []).Add(tcs);
        }
    }
}
