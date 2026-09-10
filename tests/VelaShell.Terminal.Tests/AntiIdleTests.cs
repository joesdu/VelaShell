using NSubstitute;
using VelaShell.Core.Ssh;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 防空闲断开:按间隔往会话里送一个不可见字节,免得服务端 shell 嫌你闲把人踢掉。
/// </summary>
/// <remarks>
/// 这件事与 SSH 保活心跳容易被当成同一件,于是"配了保活还是被踢"成了永远查不明白的抱怨。
/// 心跳走协议层,对端的 sshd 看得见而 shell 看不见;踢人的 <c>TMOUT</c> 与堡垒机超时
/// 只认 tty 上有没有输入。这一组用例钉的正是这条界线的实现侧:该发的时候发、
/// 不该发的时候一个字节都没有。
/// </remarks>
[TestClass]
[TestCategory("TerminalBridge")]
public sealed class AntiIdleTests
{
    private const int IntervalSeconds = 60;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>一台手拨的时钟 + 一个收件箱:注入与否是纯逻辑,不必让测试去睡真实的一分钟。</summary>
    private sealed class Harness
    {
        public long NowMs;
        public bool CanSend = true;
        public List<byte[]> Sent { get; } = [];
        public AntiIdleKeeper Keeper { get; }

        public Harness() => Keeper = new(Sent.Add, () => CanSend, () => NowMs);

        public void Advance(TimeSpan span) => NowMs += (long)span.TotalMilliseconds;
    }

    [TestMethod]
    public void DisabledByDefault_SendsNothing()
    {
        // 默认必须是关的:本地终端与绝大多数会话根本不需要注入,而注入的字节终究是
        // 打进对端 tty 的 —— 默认打开等于给每一条会话都加了一份无人要求的风险。
        var h = new Harness();

        Assert.AreEqual(TimeSpan.Zero, h.Keeper.Interval);
        h.Advance(TimeSpan.FromHours(1));
        h.Keeper.TickForTest();

        Assert.AreEqual(0, h.Sent.Count);
    }

    [TestMethod]
    public void AfterAFullIdleInterval_ANulIsInjected()
    {
        var h = new Harness();
        h.Keeper.Interval = Interval;

        h.Advance(Interval);
        h.Keeper.TickForTest();

        Assert.AreEqual(1, h.Sent.Count);
        // 送空格会被 shell 当成用户输入留在命令行上,也会被 vim / less 当成按键吃掉;
        // NUL 在行规范里被丢弃,却照样刷新 tty 的读活动 —— 有输入,但什么也没发生。
        CollectionAssert.AreEqual(new byte[] { 0x00 }, h.Sent[0]);
    }

    [TestMethod]
    public void AKeystrokeInsideTheInterval_PostponesTheInjection()
    {
        // 用户自己的击键已经把服务端的空闲计时刷新了,我们再补一发既是浪费,
        // 也让"注入"出现在本不需要它的时刻。
        var h = new Harness();
        h.Keeper.Interval = Interval;

        h.Advance(TimeSpan.FromSeconds(30));
        h.Keeper.NoteActivity();
        h.Advance(TimeSpan.FromSeconds(40)); // 距开表 70 秒,距上次击键只有 40 秒。
        h.Keeper.TickForTest();

        Assert.AreEqual(0, h.Sent.Count, "计时该从最后一次出站写算起,而不是从上一次注入算起。");

        h.Advance(TimeSpan.FromSeconds(25)); // 距上次击键 65 秒,过线了。
        h.Keeper.TickForTest();

        Assert.AreEqual(1, h.Sent.Count);
    }

    [TestMethod]
    public void ItsOwnInjection_CountsAsActivity()
    {
        var h = new Harness();
        h.Keeper.Interval = Interval;

        h.Advance(Interval);
        h.Keeper.TickForTest();
        h.Keeper.TickForTest(); // 紧接着再醒一次(定时器抖动、宿主重排都可能)。

        Assert.AreEqual(1, h.Sent.Count, "刚发过就再发一发,等于把间隔缩成了零。");

        h.Advance(Interval);
        h.Keeper.TickForTest();

        Assert.AreEqual(2, h.Sent.Count);
    }

    [TestMethod]
    public void WhenItCannotSend_TheInjectionIsDeferredNotDropped()
    {
        // ZMODEM 传输期间那条流上跑的是协议帧,插一个字节进去轻则 CRC 错重传,重则整笔失败。
        // 而传输本身就是流量,服务端此刻并不认为会话空闲 —— 等它结束再补才是对的。
        var h = new Harness { CanSend = false };
        h.Keeper.Interval = Interval;

        h.Advance(Interval);
        h.Keeper.TickForTest();
        Assert.AreEqual(0, h.Sent.Count);

        h.CanSend = true;
        h.Keeper.TickForTest();
        Assert.AreEqual(1, h.Sent.Count, "拦下的那一发不能就此作废:窗口已经过去了,补发才不迟到。");
    }

    [TestMethod]
    public void SettingTheIntervalToZero_StopsItAtOnce()
    {
        var h = new Harness();
        h.Keeper.Interval = Interval;
        h.Advance(Interval);

        h.Keeper.Interval = TimeSpan.Zero;
        h.Keeper.TickForTest();

        Assert.AreEqual(0, h.Sent.Count);
    }

    [TestMethod]
    public void AfterDispose_NothingIsInjected()
    {
        // 关标签这条路上,桥先释放流再等读写循环退出;此时还有一发在路上就会打到一条正在拆的流。
        var h = new Harness();
        h.Keeper.Interval = Interval;
        h.Keeper.Dispose();

        h.Advance(TimeSpan.FromHours(1));
        h.Keeper.TickForTest();
        h.Keeper.Dispose(); // 可重复释放。

        Assert.AreEqual(0, h.Sent.Count);
    }

    [TestMethod]
    public async Task Bridge_WithoutConfiguration_InjectsNothing()
    {
        ITerminalEmulator terminal = Substitute.For<ITerminalEmulator>();
        IShellStreamWrapper stream = Substitute.For<IShellStreamWrapper>();
        stream.CanRead.Returns(false);
        stream.CanWrite.Returns(true);
        stream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(terminal, stream);
        Assert.AreEqual(TimeSpan.Zero, bridge.AntiIdleInterval);

        bridge.AntiIdleTickForTest();
        await bridge.DrainWritesAsync();

        await stream.DidNotReceive()
            .WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void Bridge_WithAnInterval_ReallyWritesToTheStream()
    {
        // 上面几条验的是决策,这一条验接线:定时器→出站队列→写循环→底层流,一段都不能断。
        ITerminalEmulator terminal = Substitute.For<ITerminalEmulator>();
        IShellStreamWrapper stream = Substitute.For<IShellStreamWrapper>();
        stream.CanRead.Returns(false);
        stream.CanWrite.Returns(true);
        List<byte[]> writes = [];
        stream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call =>
            {
                byte[] copy = ((byte[])call[0])[..(int)call[2]];
                lock (writes)
                {
                    writes.Add(copy);
                }
            });

        using var bridge = new SshTerminalBridge(terminal, stream);
        bridge.AntiIdleInterval = TimeSpan.FromMilliseconds(50);

        bool sawNul()
        {
            lock (writes)
            {
                return writes.Exists(w => w is [0x00]);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!sawNul() && sw.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
        }
        Assert.IsTrue(sawNul(), "配了间隔却什么都没发到流上 —— 这条路上有一段没接。");
    }
}
