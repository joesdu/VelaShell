using System.Reflection;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 连接诊断的协议轨迹(<see cref="SshPacketTrace" />):只记报文的元信息、绝不记内容,我们发出的认证报文连长度也不记;
/// 装配那一层只在给了轨迹时挂 SSH 库的报文旁路,跳板链上每一跳都记、各标各的。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class SshPacketTraceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------ 记录本身

    /// <summary>
    /// 口令认证的 USERAUTH_REQUEST、keyboard-interactive 的 INFO_RESPONSE 的长度减去固定的那几段就是口令 / 应答的长度:
    /// 我们发出的 50–79 一律不记长度。收到的认证报文(失败、成功、横幅、问题)不含用户的秘密,长度照记。
    /// </summary>
    [TestMethod]
    public void 我们发出的认证报文不记长度_别的照记()
    {
        SshPacketTrace trace = new();
        trace.Record("h", SshPacketDirection.Sent, 50, 77, 5, null);       // USERAUTH_REQUEST
        trace.Record("h", SshPacketDirection.Sent, 61, 30, 6, null);       // INFO_RESPONSE
        trace.Record("h", SshPacketDirection.Sent, 79, 30, 7, null);
        trace.Record("h", SshPacketDirection.Received, 51, 40, 5, null);   // USERAUTH_FAILURE
        trace.Record("h", SshPacketDirection.Received, 60, 90, 6, null);   // INFO_REQUEST
        trace.Record("h", SshPacketDirection.Sent, 20, 1200, 0, null);     // KEXINIT
        trace.Record("h", SshPacketDirection.Sent, 94, 300, 9, 0);         // CHANNEL_DATA

        IReadOnlyList<SshPacketTraceEntry> entries = trace.Snapshot();

        Assert.AreSequenceEqual([null, null, null, 40, 90, 1200, 300], [.. entries.Select(static e => e.Length)]);
        Assert.AreEqual(0u, entries[6].ChannelNumber);
    }

    [TestMethod]
    public void 只记前面那些_超过上限的只计数()
    {
        ManualClock time = new();
        SshPacketTrace trace = new(capacity: 2, timeProvider: time);
        trace.Record("h", SshPacketDirection.Sent, 20, 10, 0, null);
        time.Advance(TimeSpan.FromMilliseconds(12));
        trace.Record("h", SshPacketDirection.Received, 20, 10, 0, null);
        trace.Record("h", SshPacketDirection.Sent, 21, 1, 1, null);

        IReadOnlyList<SshPacketTraceEntry> entries = trace.Snapshot();
        Assert.HasCount(2, entries);
        Assert.AreEqual(1, trace.Dropped, "握手与认证都在最前面:后面的只计数");
        Assert.AreEqual(TimeSpan.FromMilliseconds(12), entries[1].Elapsed);
    }

    /// <summary>守卫:记录里没有能装内容的字段 —— 只有一个字符串(哪一跳),其余都是数。加了字节数组、字符串一类的字段就红。</summary>
    [TestMethod]
    public void 记录里没有能装内容的字段()
    {
        string[] carriers =
        [
            .. typeof(SshPacketTraceEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static p => p.PropertyType == typeof(string) || p.PropertyType.IsArray
                                   || p.PropertyType.Name.Contains("Memory", StringComparison.Ordinal))
                .Select(static p => p.Name),
        ];
        Assert.AreSequenceEqual([nameof(SshPacketTraceEntry.Hop)], carriers);
    }

    [TestMethod]
    public void 消息名字与编号段()
    {
        Assert.AreEqual("KEXINIT", SshMessageNames.Name(20));
        Assert.AreEqual("USERAUTH_REQUEST", SshMessageNames.Name(50));
        Assert.AreEqual("CHANNEL_FAILURE", SshMessageNames.Name(100));
        Assert.AreEqual("PING", SshMessageNames.Name(192));
        Assert.AreEqual(SshMessageRange.KeyExchangeMethod, SshMessageNames.RangeOf(31));
        Assert.AreEqual(SshMessageRange.AuthenticationMethod, SshMessageNames.RangeOf(60));
        Assert.AreEqual(SshMessageRange.LocalExtension, SshMessageNames.RangeOf(200));
        Assert.AreEqual(SshMessageRange.Unassigned, SshMessageNames.RangeOf(9));
        Assert.AreEqual(SshMessageRange.Named, SshMessageNames.RangeOf(52));
    }

    // ------------------------------------------------------------ 挂在真实连接上

    [TestMethod]
    public void 没给轨迹就不挂旁路()
    {
        Assert.IsNull(SshConnectionAssembler.PacketTapFor(null, "joe@h:22"), "平常的连接不挂旁路:库在收发循环上只多一次判空");
        Assert.IsNotNull(SshConnectionAssembler.PacketTapFor(new SshPacketTrace(), "joe@h:22"));
    }

    /// <summary>走宿主真实的装配路径连环回上的测试服务端:握手、认证的报文都记下了,发出的认证请求没有长度。</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task 直连时记下握手与认证()
    {
        await using LoopbackSshHost host = new();
        SshPacketTrace trace = new();
        ConnectionInfo info = Info(host.Port, trace: trace);

        await using (await ConnectAsync(info))
        {
        }

        IReadOnlyList<SshPacketTraceEntry> entries = trace.Snapshot();
        string hop = $"joe@127.0.0.1:{host.Port}";
        Assert.IsTrue(entries.All(e => e.Hop == hop));
        Assert.Contains(static e => e is { Direction: SshPacketDirection.Sent, MessageNumber: 20 }, entries, "发出的 KEXINIT");
        Assert.Contains(static e => e is { Direction: SshPacketDirection.Received, MessageNumber: 20 }, entries, "收到的 KEXINIT");
        Assert.Contains(static e => e is { Direction: SshPacketDirection.Sent, MessageNumber: 21 }, entries, "NEWKEYS");
        SshPacketTraceEntry[] requests = [.. entries.Where(static e => e is { Direction: SshPacketDirection.Sent, MessageNumber: 50 })];
        Assert.IsNotEmpty(requests, "认证请求");
        Assert.IsTrue(requests.All(static e => e.Length is null), "口令认证请求的长度不记");
        SshPacketTraceEntry success = entries.Single(static e => e.MessageNumber == 52);
        Assert.AreEqual(SshPacketDirection.Received, success.Direction);
        Assert.IsNotNull(success.Length, "收到的认证报文长度照记");
    }

    /// <summary>经跳板:两跳都记,各标各的;同一份轨迹由最外层的连接信息带下去。</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task 经跳板时两跳都记()
    {
        await using LoopbackSshHost jump = new()
        {
            JumpTarget = (new TestChannelScript { CloseAfterScript = false }, null),
        };
        SshPacketTrace trace = new();
        ConnectionInfo info = new()
        {
            Host = "target.internal",
            Port = 22,
            Username = "joe",
            AuthMethod = AuthMethod.Password,
            Password = LoopbackSshHost.Password,
            JumpHost = Info(jump.Port, user: "jumper"),
            PacketTrace = trace,
        };

        await using (await ConnectAsync(info))
        {
        }

        string[] hops = [.. trace.Snapshot().Select(static e => e.Hop).Distinct()];
        Assert.AreSequenceEqual([$"jumper@127.0.0.1:{jump.Port}", "joe@target.internal:22"], hops, SequenceOrder.InAnyOrder);
        Assert.Contains(static e => e is { Hop: "joe@target.internal:22", MessageNumber: 52 }, trace.Snapshot(), "终点那一跳的认证成功");
    }

    /// <summary>手拨的时钟(只管时间戳)。</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static ConnectionInfo Info(int port, string user = "joe", SshPacketTrace? trace = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = user,
        AuthMethod = AuthMethod.Password,
        Password = LoopbackSshHost.Password,
        PacketTrace = trace,
    };

    private static async Task<VelaSshClientWrapper> ConnectAsync(ConnectionInfo info)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        using CancellationTokenSource cts = new(Deadline);
        try
        {
            await wrapper.ConnectAsync(cts.Token);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }
}
