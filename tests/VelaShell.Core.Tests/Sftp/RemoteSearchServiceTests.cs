using System.Text;
using NSubstitute;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 远端搜索服务的收场口径:先判 POSIX、判否一条命令都不发;逐条交出结果,到上限、超时、取消、缺工具各报各的。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
[DoNotParallelize]
public sealed class RemoteSearchServiceTests
{
    private const string PosixAnswer = "vela-posix-42ok:5.2.21(1)-release:\n";

    private readonly ISshConnectionService _connections = Substitute.For<ISshConnectionService>();
    private readonly ISshClientWrapper _client = Substitute.For<ISshClientWrapper>();
    private readonly Guid _session = Guid.NewGuid();

    [TestInitialize]
    public void Setup()
    {
        RemoteShellProbe.ClearCache();
        _client.IsConnected.Returns(true);
        _connections.GetClient(_session).Returns(_client);
        // GetSession 不配:没有主机名就不进探针缓存,用例之间互不串味。
        _connections.GetSession(Arg.Any<Guid>()).Returns((Core.Models.SshSession?)null);
    }

    [TestCleanup]
    public void Cleanup() => RemoteShellProbe.ClearCache();

    /// <summary>Windows 的 cmd.exe 把探针原样回显:判成非 POSIX,搜索命令一条都不发(plan.md §18-B/E 的教训)。</summary>
    [TestMethod]
    public async Task NonPosixRemote_SaysUnsupported_AndSendsNoSearchCommand()
    {
        _client.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new RemoteCommandResult("printf 'vela-posix-%s%s:%s:%s\\n' ...\r\n", "", 1));

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", "x"), _ => ValueTask.CompletedTask);

        Assert.AreEqual(RemoteSearchStatus.NotPosix, outcome.Status);
        await _client.DidNotReceive().StreamCommandRecordsAsync(
            Arg.Any<string>(), Arg.Any<byte>(), Arg.Any<Func<ReadOnlyMemory<byte>, ValueTask>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>探针没人回答(exec 被禁、只开 SFTP 的账号):说「判定不了」,同样不发。</summary>
    [TestMethod]
    public async Task UnknownShell_SaysSo_AndSendsNoSearchCommand()
    {
        _client.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns<Task<RemoteCommandResult>>(_ => throw new InvalidOperationException("exec refused"));

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", "x"), _ => ValueTask.CompletedTask);

        Assert.AreEqual(RemoteSearchStatus.ShellUnknown, outcome.Status);
        await _client.DidNotReceive().StreamCommandRecordsAsync(
            Arg.Any<string>(), Arg.Any<byte>(), Arg.Any<Func<ReadOnlyMemory<byte>, ValueTask>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task NoSshClient_IsNoSession_AndNotAvailable()
    {
        var other = Guid.NewGuid();
        _connections.GetClient(other).Returns((ISshClientWrapper?)null);

        Assert.IsFalse(Service().IsAvailable(other));
        Assert.IsTrue(Service().IsAvailable(_session));
        Assert.AreEqual(RemoteSearchStatus.NoSession,
            (await Service().SearchAsync(other, new("/srv", "x"), _ => ValueTask.CompletedTask)).Status);
    }

    [TestMethod]
    public async Task PosixRemote_StreamsHits_SkipsNoise_AndStopsAtTheLimit()
    {
        PosixProbe();
        string? sent = null;
        StreamLines(command => sent = command, "Welcome!", "d\t/srv/app", "f\t/srv/app/a.log", "f\t/srv/app/b.log");
        var hits = new List<RemoteSearchHit>();

        RemoteSearchOutcome outcome = await Service().SearchAsync(
            _session, new("/srv", "*.log", MaxResults: 2), hit =>
            {
                hits.Add(hit);
                return ValueTask.CompletedTask;
            });

        Assert.AreEqual(RemoteSearchStatus.Truncated, outcome.Status);
        Assert.AreEqual(2, outcome.Count);
        Assert.AreSequenceEqual([new RemoteSearchHit("/srv/app", true), new RemoteSearchHit("/srv/app/a.log", false)], hits);
        Assert.IsNotNull(sent);
        Assert.StartsWith("sh -c '", sent);
        Assert.Contains(" '/srv' '*.log' '3' ", sent, "向远端多要一条,用来判断截断");
    }

    /// <summary>
    /// 收满上限之后远端还在走(find 经 -exec 的批处理往外写,head 退出停不下它;主 sh 在 wait 里等着):
    /// 第「上限 + 1」条一到服务就自己取消 —— 交给库的令牌被取消(库据此给远端发 TERM),马上报「到了上限」,
    /// 而不是一直挂着、等到时间上限再报「超时」(复查 file-panes F1)。
    /// </summary>
    [TestMethod]
    public async Task ReachingTheLimit_StopsTheRemoteCommand_AndReportsTruncatedRatherThanTimedOut()
    {
        PosixProbe();
        CancellationToken passed = default;
        _client.StreamCommandRecordsAsync(Arg.Any<string>(), Arg.Any<byte>(), Arg.Any<Func<ReadOnlyMemory<byte>, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns(async call =>
               {
                   passed = call.Arg<CancellationToken>();
                   Func<ReadOnlyMemory<byte>, ValueTask> onRecord = call.Arg<Func<ReadOnlyMemory<byte>, ValueTask>>();
                   foreach (string path in (string[])["f\t/srv/a.log", "f\t/srv/b.log", "f\t/srv/c.log", "f\t/srv/d.log"])
                   {
                       await onRecord(Encoding.UTF8.GetBytes(path));
                   }
                   // 远端的 find 还在走那棵大树:不取消就一直不结束。
                   await Task.Delay(Timeout.Infinite, passed);
                   return new RemoteCommandStreamResult(0, 4);
               });
        var hits = new List<RemoteSearchHit>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        RemoteSearchOutcome outcome = await new RemoteSearchService(_connections, TimeSpan.FromSeconds(10))
            .SearchAsync(_session, new("/srv", "*.log", MaxResults: 2), hit =>
            {
                hits.Add(hit);
                return ValueTask.CompletedTask;
            });

        Assert.AreEqual(new RemoteSearchOutcome(RemoteSearchStatus.Truncated, 2), outcome);
        Assert.IsTrue(passed.IsCancellationRequested, "交给库的令牌要被取消,库才会给远端发 TERM、关掉通道");
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed, "收满就停,不等时间上限");
        Assert.AreSequenceEqual([new RemoteSearchHit("/srv/a.log", false), new RemoteSearchHit("/srv/b.log", false)], hits);
    }

    [TestMethod]
    public async Task UnderTheLimit_IsCompleted()
    {
        PosixProbe();
        StreamLines(_ => { }, "f\t/srv/a.log");

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", "*.log"), _ => ValueTask.CompletedTask);

        Assert.AreEqual(new RemoteSearchOutcome(RemoteSearchStatus.Completed, 1), outcome);
    }

    [TestMethod]
    public async Task MissingTool_IsReportedInsteadOfNoResults()
    {
        PosixProbe();
        StreamLines(_ => { }, RemoteSearch.NoGrepMarker);

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", null, "text"), _ => ValueTask.CompletedTask);

        Assert.AreEqual(RemoteSearchStatus.ToolMissing, outcome.Status);
    }

    [TestMethod]
    public async Task FishLoginShell_IsQuotedTheFishWay()
    {
        _client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
               .Returns(new RemoteCommandResult("", "fish: ${ is not a valid variable in fish.", 127));
        _client.RunCommandDetailedAsync(RemoteShellProbe.FishProbeCommand, Arg.Any<CancellationToken>())
               .Returns(new RemoteCommandResult("vela-fish-3.7.1\n", "", 0));
        string? sent = null;
        StreamLines(command => sent = command);

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", @"a\b"), _ => ValueTask.CompletedTask);

        Assert.AreEqual(RemoteSearchStatus.Completed, outcome.Status);
        Assert.Contains(@"'*a\\b*'", sent!);
    }

    /// <summary>远端一直不结束:到了时间上限就停,交出已经找到的那些,并说明结果不全。</summary>
    [TestMethod]
    public async Task SlowRemote_TimesOut_KeepingWhatWasFound()
    {
        PosixProbe();
        _client.StreamCommandRecordsAsync(Arg.Any<string>(), Arg.Any<byte>(), Arg.Any<Func<ReadOnlyMemory<byte>, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns(async call =>
               {
                   Func<ReadOnlyMemory<byte>, ValueTask> onRecord = call.Arg<Func<ReadOnlyMemory<byte>, ValueTask>>();
                   await onRecord("f\t/srv/a.log"u8.ToArray());
                   await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                   return new RemoteCommandStreamResult(0, 1);
               });

        RemoteSearchOutcome outcome = await new RemoteSearchService(_connections, TimeSpan.FromMilliseconds(200))
            .SearchAsync(_session, new("/srv", "*.log"), _ => ValueTask.CompletedTask);

        Assert.AreEqual(new RemoteSearchOutcome(RemoteSearchStatus.TimedOut, 1), outcome);
    }

    [TestMethod]
    public async Task UserCancel_IsCancelled_NotTimedOut()
    {
        PosixProbe();
        using var cts = new CancellationTokenSource();
        _client.StreamCommandRecordsAsync(Arg.Any<string>(), Arg.Any<byte>(), Arg.Any<Func<ReadOnlyMemory<byte>, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns(async call =>
               {
                   await cts.CancelAsync();
                   await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                   return new RemoteCommandStreamResult(0, 0);
               });

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", "x"), _ => ValueTask.CompletedTask, cts.Token);

        Assert.AreEqual(RemoteSearchStatus.Cancelled, outcome.Status);
    }

    [TestMethod]
    public async Task ChannelError_IsFailedWithTheMessage()
    {
        PosixProbe();
        _client.StreamCommandRecordsAsync(Arg.Any<string>(), Arg.Any<byte>(), Arg.Any<Func<ReadOnlyMemory<byte>, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns<Task<RemoteCommandStreamResult>>(_ => throw new InvalidOperationException("channel open failed"));

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", "x"), _ => ValueTask.CompletedTask);

        Assert.AreEqual(new RemoteSearchOutcome(RemoteSearchStatus.Failed, 0, "channel open failed"), outcome);
    }

    /// <summary>
    /// GBK 会话:结果按 GBK 解(与 SFTP 文件名同一个来源),名字里被换成 \0 的换行换回来;命令里的参数是 GBK 字节的八进制转义。
    /// </summary>
    [TestMethod]
    public async Task LegacyEncodingSession_DecodesResultsInTheSessionEncoding_AndKeepsNewlinesInNames()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding("GBK");
        _connections.GetSession(_session).Returns(new Core.Models.SshSession
        {
            SessionId = _session,
            ConnectionInfo = new() { Host = "gbk.invalid", Username = "joe", AuthMethod = Core.Models.AuthMethod.Password, FileNameEncoding = "GBK" },
        });
        PosixProbe();
        string? sent = null;
        StreamRecords(command => sent = command,
            [.. "f\t/srv/"u8.ToArray(), .. gbk.GetBytes("中文报告.txt")],
            [.. "d\t/srv/two"u8.ToArray(), 0, .. "lines"u8.ToArray()]);
        var hits = new List<RemoteSearchHit>();

        RemoteSearchOutcome outcome = await Service().SearchAsync(_session, new("/srv", "报告"), hit =>
        {
            hits.Add(hit);
            return ValueTask.CompletedTask;
        });

        Assert.AreEqual(new RemoteSearchOutcome(RemoteSearchStatus.Completed, 2), outcome);
        Assert.AreSequenceEqual([new RemoteSearchHit("/srv/中文报告.txt", false), new RemoteSearchHit("/srv/two\nlines", true)], hits);
        Assert.DoesNotContain("报告", sent!, "GBK 会话里中文要编成 GBK 字节再转义,不能按 UTF-8 原样发");
    }

    private RemoteSearchService Service() => new(_connections);

    private void PosixProbe() =>
        _client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
               .Returns(new RemoteCommandResult(PosixAnswer, "", 0));

    private void StreamLines(Action<string> onCommand, params string[] lines) =>
        StreamRecords(onCommand, [.. lines.Select(static line => Encoding.UTF8.GetBytes(line))]);

    private void StreamRecords(Action<string> onCommand, params byte[][] records) =>
        _client.StreamCommandRecordsAsync(Arg.Any<string>(), (byte)'\n', Arg.Any<Func<ReadOnlyMemory<byte>, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns(async call =>
               {
                   onCommand(call.ArgAt<string>(0));
                   Func<ReadOnlyMemory<byte>, ValueTask> onRecord = call.Arg<Func<ReadOnlyMemory<byte>, ValueTask>>();
                   foreach (byte[] record in records)
                   {
                       await onRecord(record);
                   }
                   return new RemoteCommandStreamResult(0, records.Length);
               });
}
