using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Ssh;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 输出洪流下的每帧解析预算与读线程背压。
/// </summary>
/// <remarks>
/// <para>
/// 合批本身是对的,但原先没有上限:<c>cat</c> 一个几百 MB 的文件时,两帧之间能攒下几十 MB,
/// UI 线程在**一个** Dispatcher 回调里全解析完 —— 界面冻结、滚动条不响应、别的标签也不刷新;
/// 内存则随读取速度无限增长。
/// </para>
/// <para>
/// 这些用例钉住两条:每次 Feed 不超过预算(界面始终可交互),积压不超过高水位(内存有上限)。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("TerminalBridge")]
public sealed class TerminalBridgeFloodTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    /// <summary>记录每次 Feed 的长度。</summary>
    private sealed class FeedLog
    {
        public List<int> Lengths { get; } = [];

        public long Total { get; private set; }

        public void Record(int length)
        {
            Lengths.Add(length);
            Total += length;
        }
    }

    /// <summary>按 16 KB 一块吐固定总量、吐完给 EOF 的假流;返回它已交出的字节数。</summary>
    private static IShellStreamWrapper FloodStream(long totalBytes, Func<long> emitted, Action<int> onEmit)
    {
        _ = emitted;
        long remaining = totalBytes;
        IShellStreamWrapper stream = Substitute.For<IShellStreamWrapper>();
        stream.CanRead.Returns(true);
        stream.CanWrite.Returns(true);
        stream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (remaining <= 0)
                {
                    return Task.FromResult(0);
                }
                byte[] buffer = call.ArgAt<byte[]>(0);
                int offset = call.ArgAt<int>(1);
                int count = call.ArgAt<int>(2);
                int n = (int)Math.Min(Math.Min(16 * 1024, count), remaining);
                buffer.AsSpan(offset, n).Fill((byte)'x');
                remaining -= n;
                onEmit(n);
                return Task.FromResult(n);
            });
        return stream;
    }

    /// <summary>把流灌完,期间按帧排空;返回喂入记录、积压峰值与流交出的总量。</summary>
    private static (FeedLog Fed, long PeakPending, long Emitted) Flood(long totalBytes)
    {
        FeedLog fed = new();
        long emitted = 0;
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        emulator.When(e => e.Feed(Arg.Any<byte[]>()))
            .Do(call => fed.Record(call.ArgAt<byte[]>(0).Length));
        IShellStreamWrapper stream = FloodStream(totalBytes, () => emitted, n => emitted += n);
        long peak = 0;

        Session.Dispatch(() =>
        {
            using var bridge = new SshTerminalBridge(emulator, stream);
            bridge.Start();

            // 模拟真实的帧节奏:反复把派发队列跑一轮,直到不再有新数据被喂进来。
            //
            // ⚠️ 空闲计数**必须等第一个字节到了才开始**。读循环由 Task.Run 排进线程池,
            // 池里没有空闲工作线程时注入新线程有节流(饱和后约每 500ms 一条);若一上来就计,
            // 50 轮(约 50ms)能在读线程还没被调度时就走完,于是喂入为空、
            // `Assert.IsNotEmpty(fed.Lengths)` 挂在一个与被测行为无关的理由上。
            // 这与 DisposeReleasesAReadLoopWaitingOnBackpressure 那条是同一个根因。
            int idle = 0;
            bool started = false;
            for (int i = 0; i < 20_000 && idle < 50; i++)
            {
                long pending = bridge.PendingBytesForTest;
                peak = Math.Max(peak, pending);
                long before = fed.Total;
                Dispatcher.UIThread.RunJobs();
                started |= fed.Total > 0 || pending > 0;
                idle = started && fed.Total == before && pending == 0 ? idle + 1 : 0;
                if (idle < 50)
                {
                    Thread.Sleep(1);
                }
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

        return (fed, peak, emitted);
    }

    [TestMethod]
    public void NoSingleFeedExceedsTheBudget()
    {
        // 这是"界面还能动"的直接量度:一次 Feed 就是 UI 线程一次不可打断的解析。
        (FeedLog fed, _, _) = Flood(24L * 1024 * 1024);

        Assert.IsNotEmpty(fed.Lengths);
        int budget = 1 << 20;
        foreach (int length in fed.Lengths)
        {
            Assert.IsLessThanOrEqualTo(budget, length,
                $"单次 Feed 交了 {length} 字节,超过每帧预算 {budget} —— UI 线程会在这一下里卡住。");
        }
    }

    [TestMethod]
    public void BacklogStaysUnderTheHighWaterMark()
    {
        // 内存上限:读线程必须被按住,而不是一路攒到几十 MB。
        //
        // 余量是**两块**,不是一块(见 SshTerminalBridge._drainGate 的说明):
        // 一块来自越界的那次入队(高水位在入队之后才判),另一块来自一张陈旧许可 ——
        // 积压跌到低水位时读线程可能没在等,那次 Release 留在信号量里,
        // 下一次 WaitAsync 立刻拿到它、不真的等,于是多放过一块。
        // 关键是它**有界**:24 MB 的洪流里积压峰值仍然贴着 8 MB,而不是一路涨上去。
        (_, long peak, _) = Flood(24L * 1024 * 1024);

        long highWater = 8L << 20;
        Assert.IsLessThanOrEqualTo(highWater + (2 * 16 * 1024), peak,
            $"积压峰值 {peak} 字节,超过高水位 {highWater} 两块以上 —— 背压没生效。");
    }

    [TestMethod]
    public void EveryByteStillArrives()
    {
        // 分片与背压都不许丢数据 —— 少一个字节就是屏幕上少一段输出。
        (FeedLog fed, _, long emitted) = Flood(8L * 1024 * 1024);

        Assert.AreEqual(emitted, fed.Total);
    }

    [TestMethod]
    public void ASmallBurstStillArrivesInOneFeed()
    {
        // 预算不该把正常的小批输出也切碎:一次 Feed = 一次重绘,切碎就是白白多刷几次屏。
        (FeedLog fed, _, long emitted) = Flood(32 * 1024);

        Assert.AreEqual(emitted, fed.Total);
        Assert.IsLessThanOrEqualTo(3, fed.Lengths.Count,
            "几十 KB 的输出被切成了很多次 Feed —— 预算切得太碎。");
    }

    /// <summary>
    /// 轮询等待条件成立;超时返回 false。
    /// </summary>
    /// <remarks>
    /// <b>不要用固定 <c>Thread.Sleep</c> 代替它。</b>读循环跑在**线程池**上
    /// (<c>SshTerminalBridge.Start</c> 里的 <c>Task.Run</c>),而池里没有空闲工作线程时,
    /// 注入新线程是**有节流的**(饱和后约每 500ms 才多一条)。于是"睡 N 毫秒,读线程总该
    /// 跑起来了吧"这个假设在核数少的机器上直接不成立 —— 见下面那条用例的说明。
    /// <para>
    /// 调用方跑在 headless UI 线程上,这里的 <c>Sleep</c> 顺带保证了 <c>FlushPending</c>
    /// 不会被派发,积压因此只增不减 —— 正是等待条件所需要的。
    /// </para>
    /// </remarks>
    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }
            // 让出 CPU:CI 上核数少,忙等会把读线程本身饿住。
            Thread.Sleep(2);
        }
        return condition();
    }

    [TestMethod]
    public void DisposeReleasesAReadLoopWaitingOnBackpressure()
    {
        // 读线程可能正等在背压闸上。关标签时若不放行它,Dispose 会白等满 2 秒超时。
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        IShellStreamWrapper stream = FloodStream(64L * 1024 * 1024, static () => 0, static _ => { });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        Session.Dispatch(() =>
        {
            var bridge = new SshTerminalBridge(emulator, stream);
            bridge.Start();
            // 只跑一轮派发;此后 UI 线程不再排空,积压只增不减,读线程会一路冲到高水位。
            Dispatcher.UIThread.RunJobs();

            // 等的是**真正的前提**:积压越过高水位 = 读线程已经(或即将)停在闸上。
            //
            // 原先这里是 `Thread.Sleep(150)` + 断言积压 > 0,两处都不对:
            //   · 150ms 是拿固定时长赌"读线程已经跑起来了"。读循环由 Task.Run 排进线程池,
            //     池里空闲工作线程不够时,注入新线程约每 500ms 才多一条 —— 赌不赢。
            //     `DOTNET_PROCESSOR_COUNT=2` 下本条用例单跑必挂,加
            //     `DOTNET_ThreadPool_ForceMinWorkerThreads=10` 立刻转绿,根因即此。
            //     macos-latest 只有 3 核,与别的测试程序集抢 CPU 时就会撞上(CI run 34159705858)。
            //   · "积压 > 0" 比这条用例真正需要的前提**弱得多**:只攒了一块 16 KB 时读线程
            //     根本不在闸上,Dispose 当然快 —— 断言照样绿,却什么也没验到。
            Assert.IsTrue(
                WaitUntil(() => bridge.PendingBytesForTest > SshTerminalBridge.HighWaterBytesForTest,
                          TimeSpan.FromSeconds(20)),
                $"20 秒内积压没能越过高水位(现为 {bridge.PendingBytesForTest} 字节,高水位 " +
                $"{SshTerminalBridge.HighWaterBytesForTest})—— 读线程没顶到闸上,这条用例就没量到东西。");

            elapsed.Restart();
            bridge.Dispose();
            elapsed.Stop();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsLessThan(1500, elapsed.ElapsedMilliseconds,
            $"Dispose 花了 {elapsed.ElapsedMilliseconds}ms —— 读线程八成一直挂在背压闸上等到超时。");
    }
}
