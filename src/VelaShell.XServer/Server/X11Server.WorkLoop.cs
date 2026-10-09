// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 1 节「Protocol Formats」(请求按序执行)、
//   「GrabServer」(独占期间不处理其他连接的请求)
//   架构:velashell-docs/zh/xserver/design/architecture.md §5(线程模型)

using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>执行线程一次持锁最多跑这么久,然后放锁让宿主读像素(宿主的 UI 线程在 ReadPixels 里等这把锁)。</summary>
    private static readonly long LockBudgetTicks = Stopwatch.Frequency / 250;   // 4 毫秒

    /// <summary>GrabServer 期间暂存的别的客户端的工作项。</summary>
    private readonly List<WorkItem> _deferred = [];

    private XClient? _serverGrabber;

    /// <summary>一项工作:客户端的一条请求(<see cref="Request" />),或者一段要在执行线程上跑的代码。</summary>
    /// <summary>执行线程上的一项工作:一段代码,或者一条请求(<see cref="Request" /> 是池里租来的缓冲,前 <see cref="RequestLength" /> 字节是请求)。</summary>
    private readonly record struct WorkItem(XClient? Client, Action? Action, byte[]? Request = null, int RequestLength = 0);

    /// <summary>把一件事排进执行线程。可以在任意线程上调。</summary>
    internal void Post(XClient? client, Action action) => _work.Writer.TryWrite(new WorkItem(client, action));

    /// <summary>把客户端的一条请求排进执行线程(不为每条请求分配闭包)。</summary>
    private void PostRequest(XClient client, byte[] request, int length) => _work.Writer.TryWrite(new WorkItem(client, null, request, length));

    /// <summary>排进执行线程并等它做完(连接建立等少数需要结果的地方用)。</summary>
    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _work.Writer.TryWrite(new WorkItem(null, () =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        if (!queued)
        {
            tcs.TrySetCanceled();   // 执行循环已经收工
        }
        return tcs.Task;
    }

    /// <summary>持锁期间产生的诊断日志:放锁之后再交给宿主(宿主的日志往往同步写文件,持锁写就是让 UI 线程陪着等磁盘)。</summary>
    private readonly List<string> _pendingLog = [];

    /// <summary>此刻持着像素锁的执行线程(没人持有为 0)。</summary>
    private int _lockThread;

    /// <summary>
    /// 客户端能成批触发的日志(协议错误、连接进出、字体没找到)每秒最多记这么多条(全部客户端合计);
    /// 再多只计数,下一次能记时补一行「没记的有几条」。
    /// </summary>
    private const int FrequentLogsPerSecond = 50;

    private long _frequentLogSecond;
    private int _frequentLogsThisSecond;
    private int _frequentLogsSuppressed;

    /// <summary>
    /// 诊断日志的唯一出口(<see cref="X11ServerOptions.Log" />)。执行线程持锁时先攒着,放锁之后按原顺序交出去;
    /// 连接的读写线程上直接交。宿主的日志委托抛的异常一律吞掉:原先放锁之后(DeferredHost 记「宿主回调失败」的那一行)直接调,
    /// 异常一路抛出执行循环,之后所有客户端都卡住,DisposeAsync 也在等执行循环时重抛、不收尾。
    /// </summary>
    internal void Log(string message)
    {
        if (_options.Log is not { } log)
        {
            return;
        }
        if (_lockThread == Environment.CurrentManagedThreadId)
        {
            _pendingLog.Add(message);
            return;
        }
        try
        {
            log(message);
        }
        catch (Exception)
        {
            // 宿主的日志出错不能拖垮执行线程(或者连接线程)。
        }
    }

    /// <summary>放锁之后:把持锁期间攒下的日志交给宿主。</summary>
    private void FlushLog()
    {
        if (_pendingLog.Count == 0 || _options.Log is not { } log)
        {
            return;
        }
        string[] lines = [.. _pendingLog];
        _pendingLog.Clear();
        foreach (string line in lines)
        {
            try
            {
                log(line);
            }
            catch (Exception)
            {
                // 宿主的日志出错不能拖垮执行线程。
            }
        }
    }

    /// <summary>
    /// 客户端能成批触发的日志按字节另有一道限额(全部客户端合计):先给这么多字节的余量,之后每秒补
    /// <see cref="FrequentLogBytesPerSecond" />。只数条数的话,每秒 50 条 × 64 KB 的字体名几十秒就写满宿主日志一天的额度(64 MB),
    /// 正常一天的日志只有几十 KB —— 真正要查的那几行就被挤掉了。
    /// </summary>
    private const int FrequentLogBurstBytes = 32 * 1024;

    /// <summary>见 <see cref="FrequentLogBurstBytes" />:持续刷屏时一天最多二十来 MB。</summary>
    private const int FrequentLogBytesPerSecond = 256;

    /// <summary>经 <see cref="LogFrequent" /> 记的一行最多这么多个字符。</summary>
    private const int MaxFrequentLogChars = 512;

    private double _frequentLogCredit = FrequentLogBurstBytes;
    private long _frequentLogCreditAt = Stopwatch.GetTimestamp();

    /// <summary>
    /// 这一条客户端能成批触发的日志要不要记:每秒最多 <see cref="FrequentLogsPerSecond" /> 条,字节另有限额(<see cref="FrequentLogBurstBytes" />)
    /// —— 一个客户端每秒能打出几十万条错误请求、连上又断开几千次,条条都记,日志文件一晚上就是几个 GB。
    /// 记了的话用 <see cref="LogFrequent" /> 写那一行。没记的只计数,下一次能记时先补一行「没记的有几条」。只在执行线程上调。
    /// </summary>
    private bool ShouldLogFrequent()
    {
        if (_options.Log is null)
        {
            return false;
        }
        long now = Stopwatch.GetTimestamp();
        long second = now / Stopwatch.Frequency;
        if (second != _frequentLogSecond)
        {
            _frequentLogSecond = second;
            _frequentLogsThisSecond = 0;
        }
        _frequentLogCredit = Math.Min(FrequentLogBurstBytes,
            _frequentLogCredit + (Stopwatch.GetElapsedTime(_frequentLogCreditAt, now).TotalSeconds * FrequentLogBytesPerSecond));
        _frequentLogCreditAt = now;
        if (_frequentLogsThisSecond >= FrequentLogsPerSecond || _frequentLogCredit <= 0)
        {
            _frequentLogsSuppressed++;
            return false;
        }
        _frequentLogsThisSecond++;
        if (_frequentLogsSuppressed > 0)
        {
            int suppressed = _frequentLogsSuppressed;
            _frequentLogsSuppressed = 0;
            LogFrequent($"{suppressed} more log lines were not written (limit {FrequentLogsPerSecond} lines and {FrequentLogBytesPerSecond} bytes per second)");
        }
        return true;
    }

    /// <summary>
    /// 写一行 <see cref="ShouldLogFrequent" /> 放行了的日志:整行按 <see cref="LogText" /> 去掉控制字符、截到 <see cref="MaxFrequentLogChars" />,
    /// 从字节限额里扣掉。
    /// </summary>
    private void LogFrequent(string message)
    {
        string line = LogText(message, MaxFrequentLogChars);
        _frequentLogCredit -= line.Length;
        Log(line);
    }

    /// <summary>
    /// 客户端给的字符串进日志之前用它:控制字符(CR / LF / ESC 等 C0、DEL、C1)与双向文字控制符写成 <c>\xNN</c> / <c>\uNNNN</c>,
    /// 超过 <paramref name="max" /> 个字符截断并注明原长。原先 OpenFont 的名字(最长 64 KB、什么字节都能带)原样进日志:
    /// 换行能伪造日志行,ESC 能往看日志的终端里注入控制序列。
    /// </summary>
    internal static string LogText(string text, int max = 200)
    {
        StringBuilder? escaped = null;
        int end = Math.Min(text.Length, max);
        for (int i = 0; i < end; i++)
        {
            char ch = text[i];
            bool control = ch is < (char)0x20 or >= '\u007F' and <= '\u009F' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩';
            if (control)
            {
                escaped ??= new StringBuilder(text, 0, i, end + 16);
                escaped.Append(ch <= 0xFF ? $"\\x{(int)ch:X2}" : $"\\u{(int)ch:X4}");
            }
            else
            {
                escaped?.Append(ch);
            }
        }
        string result = escaped?.ToString() ?? (end == text.Length ? text : text[..end]);
        return end == text.Length ? result : $"{result}…({text.Length} chars)";
    }

    /// <summary>已经记过完整调用栈的失败(「操作码.次操作码:异常类型」);同一种之后只记一行。</summary>
    private readonly HashSet<string> _reportedFailures = [];

    /// <summary>
    /// 记一次请求或工作项的意外失败(BadImplementation 之类 —— 都是我们自己的缺陷):每种(<paramref name="where" /> + 异常类型)
    /// 第一次记完整的调用栈,之后只记一行。原先每次都打完整的栈(几 KB),一个能稳定触发它的客户端很快就写满日志。
    /// 异常消息可能带着客户端给的值,经 <see cref="LogText" />。调用方先过 <see cref="ShouldLogFrequent" />。
    /// </summary>
    private void LogFailure(string head, string where, Exception ex)
    {
        string summary = $"{head} {ex.GetType().FullName}: {LogText(ex.Message)}";
        if (_reportedFailures.Count < 256 && _reportedFailures.Add($"{where}:{ex.GetType().FullName}"))
        {
            string full = $"{summary}{Environment.NewLine}{ex.StackTrace}";
            _frequentLogCredit -= full.Length;
            Log(full);
            return;
        }
        LogFrequent(summary);
    }

    /// <summary>
    /// 放回来的暂存请求(GrabServer 结束、SYNC 的 Await 等到了、XTEST 的延迟到了):执行循环先取它,再取通道。
    /// 原先就地一口气执行完 —— 最坏 255 个客户端 × 1024 条,全程持锁,绕过每批 4 毫秒的预算与对宿主的让行。
    /// 先于通道取保证了顺序:同一个客户端还在通道里的请求都比暂存的这些来得晚。
    /// </summary>
    private readonly Queue<WorkItem> _ready = new();

    /// <summary>把暂存的请求按原顺序放回执行循环(见 <see cref="_ready" />)。</summary>
    private void Requeue(List<WorkItem> items)
    {
        foreach (WorkItem item in items)
        {
            _ready.Enqueue(item);
        }
    }

    private bool TryTakeItem(ChannelReader<WorkItem> reader, out WorkItem item) =>
        _ready.TryDequeue(out item) || reader.TryRead(out item);

    private async Task RunLoopAsync()
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        try
        {
            while (_ready.Count != 0 || await reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                try
                {
                    RunBatch(reader);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 兜底:单项工作的异常 RunItem 已经接住了,能到这里的是放锁之后那几步(宿主回调、日志、损伤)里漏网的。
                    // 执行循环一退出,所有客户端都卡住,宁可记一行接着跑。
                    if (ShouldLogFrequent())
                    {
                        LogFailure("execution loop:", "loop", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
    }

    /// <summary>持锁跑一批工作项(到预算或宿主在等就停),放锁之后交日志、损伤与宿主回调。</summary>
    private void RunBatch(ChannelReader<WorkItem> reader)
    {
        // lock 不公平:刚放锁就再拿,等着读像素的宿主线程可能一直抢不到。宿主在等就先让它读完。
        _pixelGate.YieldToHost();
        lock (_pixelGate.Lock)
        {
            _lockThread = Environment.CurrentManagedThreadId;
            try
            {
                long deadline = Stopwatch.GetTimestamp() + LockBudgetTicks;
                while (TryTakeItem(reader, out WorkItem item))
                {
                    RunItem(item);
                    if (Stopwatch.GetTimestamp() >= deadline || _pixelGate.HostWaiting)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _lockThread = 0;
            }
        }
        // 宿主回调与日志一律在放锁之后调:回调里同步等 UI 线程、而 UI 线程正在 ReadPixels 里等这把锁,就是死锁。
        FlushLog();
        FlushDamage();
        _host.Flush();
    }

    private void RunItem(WorkItem item)
    {
        // SYNC 的 Await 期间,这个客户端之后的请求暂存,条件成立时放回。
        if (_syncWaits.Count != 0 && DeferIfWaiting(item))
        {
            return;
        }
        // XTEST 的 FakeInput 带了延迟:到点之前这个客户端之后的请求暂存。
        if (_fakeInputDelays.Count != 0 && DeferIfFakeInputPending(item))
        {
            return;
        }
        // GrabServer 期间,别人的请求原样暂存,Ungrab 后按原顺序放回(协议「GrabServer」)。
        if (_serverGrabber is { } grabber && item.Client is { } client && !ReferenceEquals(client, grabber) && !client.Closed)
        {
            _deferred.Add(item);
            return;
        }
        // 要用的字体还没建好:在后台建,这个客户端的这条与之后的请求暂存(见 DeferIfFontsLoading)。
        if (DeferIfFontsLoading(item))
        {
            return;
        }
        long started = Stopwatch.GetTimestamp();
        // 每项工作一份预算(X-1):扣光时请求回 Alloc,而不是持着像素锁跑上几分钟、让宿主界面陪着冻住。
        WorkBudget.Begin(RequestWorkBudget);
        try
        {
            if (item.Request is { } request)
            {
                ExecuteRequest(item.Client!, request, item.RequestLength);
            }
            else
            {
                item.Action!();
            }
        }
        catch (Exception ex)
        {
            if (ShouldLogFrequent())
            {
                LogFailure("work item failed:", "work", ex);
            }
        }
        finally
        {
            WorkBudget.End();
        }
        long elapsed = Stopwatch.GetTimestamp() - started;
        if (elapsed >= SlowItemTicks && ShouldLogFrequent())
        {
            // 预算之内的单项也可能慢(合法但昂贵的请求):点名客户端,宿主日志里才找得到是谁让界面卡了一下。
            string what = item.Request is { } r
                ? $"{item.Client} opcode {r[0]}{(r[0] >= XOpcode.FirstExtension ? $".{r[1]}" : "")}"
                : item.Client is { } owner ? $"{owner} (internal)" : "host or timer";
            LogFrequent($"slow work item: {what} held the pixel lock for {elapsed * 1000 / Stopwatch.Frequency} ms");
        }
    }

    /// <summary>一项工作持锁超过这么久就记一行日志(见 <see cref="RunItem" />)。</summary>
    private static readonly long SlowItemTicks = Stopwatch.Frequency / 4;   // 250 毫秒

    /// <summary>每项工作的工作量预算(<see cref="WorkBudget" />);测试可以调小。</summary>
    internal long RequestWorkBudget { get; set; } = WorkBudget.DefaultUnits;

    /// <summary>
    /// GrabServer(协议「GrabServer」):之后别的客户端的请求暂存,直到 UngrabServer、持有者断开,或者宿主
    /// <see cref="BreakGrabs" /> / <see cref="DisconnectClient(int)" />。持有者自己再抓一次是空操作。
    /// 抓着超过 <see cref="ServerGrabWarningDelay" /> 还有别人的请求在等,就记一行点名持有者:持有者挂住时(远端进程被
    /// SIGSTOP、SSH 断网而连接没断)所有会话的 X 程序都冻着,原先日志里一点痕迹也没有,用户与宿主都不知道该断开谁。
    /// </summary>
    private void GrabServer(XClient c)
    {
        if (ReferenceEquals(_serverGrabber, c))
        {
            return;
        }
        _serverGrabber = c;
        int epoch = ++_serverGrabEpoch;
        _ = DelayThenPostAsync((uint)ServerGrabWarningDelay.TotalMilliseconds, () => WarnLongServerGrab(epoch, 1), _lifetime.Token);
    }

    /// <summary>GrabServer 抓着这么久、还有别人的请求在等时记一行(之后每隔 6 倍这么久再记);测试可以调小。</summary>
    internal TimeSpan ServerGrabWarningDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>每次 GrabServer 加一:到点的提醒据此认出是不是还是同一次抓取。</summary>
    private int _serverGrabEpoch;

    private void WarnLongServerGrab(int epoch, int round)
    {
        if (_serverGrabber is not { } holder || epoch != _serverGrabEpoch)
        {
            return;   // 早就放开了(或者已经是另一次抓取)
        }
        if (_deferred.Count != 0)
        {
            Log($"{holder} has held the server grab for {(int)(ServerGrabWarningDelay.TotalSeconds * ((6 * (round - 1)) + 1))} s; "
                + $"{_deferred.Count} requests of other clients are waiting (BreakGrabs or disconnecting {holder} releases them)");
        }
        _ = DelayThenPostAsync((uint)(ServerGrabWarningDelay.TotalMilliseconds * 6), () => WarnLongServerGrab(epoch, round + 1), _lifetime.Token);
    }

    /// <summary>GrabServer 结束(或持有者断开):把暂存的请求按原顺序重新排进去。</summary>
    private void ReleaseServerGrab()
    {
        _serverGrabber = null;
        Requeue(_deferred);
        _deferred.Clear();
    }
}
