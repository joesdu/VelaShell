using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 连接结束时写进诊断日志的那一行:分得清掉线与本端关闭,带上对端的原因码与原话,记连接起始时刻,且永远只占一行。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class SshDisconnectLogTests
{
    /// <summary>东八区的某个时刻;日志里写本地时间并带偏移。</summary>
    private static readonly DateTimeOffset ConnectedAt = new(2026, 10, 24, 10, 15, 30, TimeSpan.FromHours(8));

    [TestMethod]
    public void LocalClose_IsLoggedAsSuchWithoutTheLibraryMessage()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.Aborted, SshPhase.Closed, "连接已释放");
        string line = SshDisconnectLog.Describe("root@db1:22", reason, ConnectedAt);
        Assert.AreEqual("[SSH] root@db1:22 closed (connected since 2026-10-24 10:15:30 +08:00): closed locally", line);
    }

    [TestMethod]
    public void PeerDisconnect_CarriesCodeAndSanitizedDescriptionOnOneLine()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.ClosedByPeer, SshPhase.Open, "对端断开了连接")
        {
            DisconnectReason = SshDisconnectReason.ByApplication,
            // 对端原话是不可信文本:换行与控制字符不能在日志里伪造出第二行
            PeerDescription = "idle timeout\r\n[SSH] fake line\u001b[31m"
        };
        string line = SshDisconnectLog.Describe("ops@[2001:db8::1]:2222", reason, ConnectedAt.ToOffset(TimeSpan.FromHours(-4.5)));
        Assert.StartsWith("[SSH] ops@[2001:db8::1]:2222 closed (connected since 2026-10-23 21:45:30 -04:30): ClosedByPeer(Open)", line);
        Assert.Contains("peer DISCONNECT ByApplication(11)", line);
        Assert.Contains("\"idle timeout [SSH] fake line?[31m\"", line, "换行收成一个空格,其余控制字符照库的规矩换成 ?");
        Assert.Contains(" - 对端断开了连接", line);
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\u001b", line);
    }

    [TestMethod]
    public void PeerDescription_BidiControlsAndUnicodeLineBreaksCannotReshapeTheLine()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.ClosedByPeer, SshPhase.Open, "对端断开了连接")
        {
            DisconnectReason = SshDisconnectReason.ByApplication,
            // U+2028 / U+2029 在不少查看器里就是换行;双向控制符能把显示顺序倒过来,伪造出另一段文字
            PeerDescription = "bye\u2028[SSH] fake line\u2029\u202Eevil\u202C \u2066x\u2069"
        };
        string line = SshDisconnectLog.Describe("u@h:22", reason, ConnectedAt);
        Assert.Contains("\"bye [SSH] fake line ?evil? ?x?\"", line);
        foreach (char c in "\u2028\u2029\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069")
        {
            Assert.DoesNotContain(c.ToString(), line);
        }
    }

    [TestMethod]
    public void TransportFailure_NamesTheInnerCauseAndCapsLongText()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.KeepAliveTimeout, SshPhase.Open, "保活超时",
            new IOException(new string('x', 1000)));
        string line = SshDisconnectLog.Describe("u@h:22", reason, ConnectedAt);
        Assert.StartsWith("[SSH] u@h:22 closed (connected since 2026-10-24 10:15:30 +08:00): KeepAliveTimeout(Open) - 保活超时 <- IOException: ", line);
        Assert.EndsWith("…", line);
        Assert.IsLessThan(400, line.Length);
    }

    [TestMethod]
    public async Task Watch_RecordsTheWallClockMomentItWasAttachedNotWhenTheConnectionEnded()
    {
        // 机器睡了一夜:墙上时钟走了 9 小时,单调时钟(Stopwatch)在 macOS / Linux 上一动不动。
        // 日志要记的是挂上的那一刻;结束时再取时钟、或者靠单调时钟量时长,都会把这一夜算错
        var clock = new ManualClock(ConnectedAt);
        var completion = new TaskCompletionSource<SshException>();
        var lines = new List<string>();

        Task watching = SshDisconnectLog.Watch(completion.Task, "deploy@db1:22", clock, lines.Add);
        clock.Now = ConnectedAt.AddHours(9);
        completion.SetResult(new SshConnectionClosedException(SshFailureReason.KeepAliveTimeout, SshPhase.Open, "保活超时"));
        await watching;

        Assert.HasCount(1, lines);
        Assert.StartsWith("[SSH] deploy@db1:22 closed (connected since 2026-10-24 10:15:30 +08:00): KeepAliveTimeout(Open)", lines[0]);
        Assert.AreEqual(0, clock.TimestampReads, "不再用单调时钟量时长");
    }

    /// <summary>手动拨的时钟:本地时区固定为东八区,并记下有没有人读单调时间戳。</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public int TimestampReads { get; private set; }

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

        public override long GetTimestamp()
        {
            TimestampReads++;
            return base.GetTimestamp();
        }
    }
}
