using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 远端搜索在真 sshd 上、五种登录 shell(bash / zsh / fish / dash / ash)下跑:BusyBox 的 find / grep(Alpine 靶机就是 BusyBox),
/// 结果对、注入不执行、到上限就停。
/// </summary>
/// <remarks>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-shells</c>(端口 2223,见 <c>tests/fixtures/ssh-shells/Dockerfile</c>)。
/// 连接走宿主真正的装配路径,探针、命令拼装、exec 通道、输出解析全是生产代码。夹具由 vela-bash 建在 /tmp 下,各账号都读得到。
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
[TestCategory("DockerIntegration")]
[DoNotParallelize]
public sealed class RemoteSearchDockerTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2223;
    private const string TestPassword = "velapass";

    private static readonly string Id = Guid.NewGuid().ToString("N")[..8];
    private static readonly string Root = $"/tmp/vela-search-{Id}";
    private const string NastyName = "it's $(touch PWNED) `touch PWNED2` ;& x.txt";

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void CreateFixture(TestContext context)
    {
        if (!ContainerReachable())
        {
            return;
        }
        RemoteShellProbe.ClearCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        VelaSshClientWrapper ssh = ConnectAsync("vela-bash", cts.Token).GetAwaiter().GetResult();
        try
        {
            RemoteCommandResult result = ssh.RunCommandDetailedAsync(
                "sh -c 'mkdir -p \"$1/app/logs\" \"$1/.hidden\" && printf \"listen 80\\n\" > \"$1/app/nginx.conf\" && " +
                "printf \"nothing\\n\" > \"$1/app/logs/access.log\" && printf \"x\\n\" > \"$1/$2\" && " +
                "i=0; while [ $i -lt 30 ]; do : > \"$1/app/logs/r$i.log\"; i=$((i+1)); done; chmod -R a+rX \"$1\"' fixture " +
                RemoteSha256.Quote(Root) + " " + RemoteSha256.Quote(NastyName), cts.Token).GetAwaiter().GetResult();
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
        }
        finally
        {
            ssh.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _ = context;
    }

    [ClassCleanup]
    public static void RemoveFixture()
    {
        if (!ContainerReachable())
        {
            return;
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        VelaSshClientWrapper ssh = ConnectAsync("vela-bash", cts.Token).GetAwaiter().GetResult();
        try
        {
            ssh.RunCommandDetailedAsync("rm -rf " + RemoteSha256.Quote(Root), cts.Token).GetAwaiter().GetResult();
        }
        finally
        {
            ssh.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        RemoteShellProbe.ClearCache();
    }

    [TestMethod]
    [Timeout(120_000)]
    [DataRow("vela-bash")]
    [DataRow("vela-zsh")]
    [DataRow("vela-fish")]
    [DataRow("vela-dash")]
    [DataRow("vela-ash")]
    public async Task EveryLoginShell_FindsByNameAndContent_AndNeverRunsUserInput(string user)
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync(user, TestContext.CancellationToken);
        RemoteSearchService service = Service(ssh, user, out Guid session);

        (RemoteSearchOutcome byName, List<RemoteSearchHit> names) = await SearchAsync(service, session, new(Root, "NGINX"));
        Assert.AreEqual(RemoteSearchStatus.Completed, byName.Status, byName.Detail);
        Assert.AreSequenceEqual([new RemoteSearchHit(Root + "/app/nginx.conf", false)], names);

        (_, List<RemoteSearchHit> dirs) = await SearchAsync(service, session, new(Root, "logs"));
        Assert.AreSequenceEqual([new RemoteSearchHit(Root + "/app/logs", true)], dirs);

        (RemoteSearchOutcome byContent, List<RemoteSearchHit> content) = await SearchAsync(service, session, new(Root, null, "LISTEN 80"));
        Assert.AreEqual(RemoteSearchStatus.Completed, byContent.Status, byContent.Detail);
        Assert.AreSequenceEqual([new RemoteSearchHit(Root + "/app/nginx.conf", false)], content);

        (_, List<RemoteSearchHit> nasty) = await SearchAsync(service, session, new(Root, NastyName, IgnoreCase: false));
        Assert.AreSequenceEqual([new RemoteSearchHit(Root + "/" + NastyName, false)], nasty, "名字里的引号与命令替换是名字的一部分");

        string marker = $"/tmp/vela-pwned-{Id}-{user}";
        await SearchAsync(service, session, new(Root, $"*$(touch {marker})*`touch {marker}b`'\\", $"';touch {marker}c;'$(touch {marker}d)"));
        RemoteCommandResult check = await ssh.RunCommandDetailedAsync(
            $"ls {marker}* 2>/dev/null; echo done", TestContext.CancellationToken);
        Assert.AreEqual("done", check.StandardOutput.Trim(), $"用户输入被当成命令执行了({user}):{check.StandardOutput}");
        RemoteCommandResult home = await ssh.RunCommandDetailedAsync("ls -a ~", TestContext.CancellationToken);
        Assert.DoesNotContain("PWNED", home.StandardOutput);
    }

    /// <summary>到上限就停:交出上限那么多条,如实说结果不全。</summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Limit_IsReportedAsTruncated()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync("vela-ash", TestContext.CancellationToken);
        RemoteSearchService service = Service(ssh, "vela-ash", out Guid session);

        (RemoteSearchOutcome outcome, List<RemoteSearchHit> hits) = await SearchAsync(service, session, new(Root, "*.log", MaxResults: 5));

        Assert.AreEqual(RemoteSearchStatus.Truncated, outcome.Status);
        Assert.HasCount(5, hits);

        // logs 下正好 31 个文件:根目录自己(名字也匹配 *)不能占名额,否则恰好装满时会误报「不全」、多一个时又报不出来。
        (RemoteSearchOutcome exact, List<RemoteSearchHit> all) = await SearchAsync(service, session, new(Root + "/app/logs", "*", MaxResults: 31));
        Assert.AreEqual(RemoteSearchStatus.Completed, exact.Status);
        Assert.HasCount(31, all);
        (RemoteSearchOutcome oneShort, _) = await SearchAsync(service, session, new(Root + "/app/logs", "*", MaxResults: 30));
        Assert.AreEqual(RemoteSearchStatus.Truncated, oneShort.Status);
    }

    /// <summary>
    /// 名字里带换行的文件与目录在 BusyBox 上各是一条结果(按 \0 分隔交给批处理、tr 对调之后 head 计数),按内容找同样;
    /// BusyBox 的 grep 没有 -Z,带换行的名字逐个 grep -q。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    [DataRow("vela-ash")]
    [DataRow("vela-fish")]
    public async Task NamesWithNewlines_AreOneHitEach_OnBusyBox(string user)
    {
        RequireContainer();
        string root = $"{Root}/newline-{user}";
        // 夹具目录归 vela-bash(ClassInitialize 建的),别的账号只读:夹具照样由它建。
        await using (VelaSshClientWrapper owner = await ConnectAsync("vela-bash", TestContext.CancellationToken))
        {
            RemoteCommandResult created = await owner.RunCommandDetailedAsync(
                "sh -c 'n=$(printf \"\\n_\"); n=${n%_}; mkdir -p \"$1/dir${n}x\" && " +
                "printf \"needle\\n\" > \"$1/two${n}lines.txt\" && printf \"needle\\n\" > \"$1/plain.txt\" && chmod -R a+rX \"$1\"' fixture " +
                RemoteSha256.Quote(root), TestContext.CancellationToken);
            Assert.AreEqual(0, created.ExitCode, created.StandardError);
        }
        await using VelaSshClientWrapper ssh = await ConnectAsync(user, TestContext.CancellationToken);
        RemoteSearchService service = Service(ssh, user, out Guid session);

        (RemoteSearchOutcome byName, List<RemoteSearchHit> names) = await SearchAsync(service, session, new(root, "*"));
        Assert.AreEqual(RemoteSearchStatus.Completed, byName.Status, byName.Detail);
        Assert.AreSequenceEqual(
        [
            new RemoteSearchHit(root + "/dir\nx", true),
            new RemoteSearchHit(root + "/plain.txt", false),
            new RemoteSearchHit(root + "/two\nlines.txt", false),
        ], names);

        (RemoteSearchOutcome byContent, List<RemoteSearchHit> content) = await SearchAsync(service, session, new(root, null, "NEEDLE"));
        Assert.AreEqual(RemoteSearchStatus.Completed, byContent.Status, byContent.Detail);
        Assert.AreSequenceEqual(
            [new RemoteSearchHit(root + "/plain.txt", false), new RemoteSearchHit(root + "/two\nlines.txt", false)], content);

        // 上限照样按条数算:名字里的换行不会让 head 多数一条。
        (RemoteSearchOutcome limited, List<RemoteSearchHit> two) = await SearchAsync(service, session, new(root, "*", MaxResults: 2));
        Assert.AreEqual(RemoteSearchStatus.Truncated, limited.Status);
        Assert.HasCount(2, two);
    }

    /// <summary>
    /// GBK 会话:GBK 字节的名字按会话编码显示成中文,从 GBK 名字的目录起找也找得到(参数编成 GBK 字节交给远端)。
    /// UTF-8 会话下同一批名字显示不出中文 —— 这就是改动之前 GBK 服务器上的样子。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task GbkSession_ShowsGbkNamesAndSearchesFromAGbkDirectory()
    {
        RequireContainer();
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var gbk = System.Text.Encoding.GetEncoding("GBK");
        const string Directory = "中文目录";
        const string File = "报告.txt";
        await using VelaSshClientWrapper ssh = await ConnectAsync("vela-bash", TestContext.CancellationToken);
        string root = $"{Root}/gbk";
        RemoteCommandResult created = await ssh.RunCommandDetailedAsync(
            "sh -c 'd=$(printf \"$2_\"); d=${d%_}; f=$(printf \"$3_\"); f=${f%_}; mkdir -p \"$1/$d\" && printf \"needle\\n\" > \"$1/$d/$f\"' fixture " +
            RemoteSha256.Quote(root) + " " + RemoteSha256.Quote(Octal(gbk, Directory)) + " " + RemoteSha256.Quote(Octal(gbk, File)),
            TestContext.CancellationToken);
        Assert.AreEqual(0, created.ExitCode, created.StandardError);

        RemoteSearchService gbkService = Service(ssh, "vela-bash", out Guid gbkSession, fileNameEncoding: "GBK");
        (RemoteSearchOutcome outcome, List<RemoteSearchHit> hits) = await SearchAsync(gbkService, gbkSession, new(root, "报告"));
        Assert.AreEqual(RemoteSearchStatus.Completed, outcome.Status, outcome.Detail);
        Assert.AreSequenceEqual([new RemoteSearchHit($"{root}/{Directory}/{File}", false)], hits);

        (_, List<RemoteSearchHit> fromGbkDirectory) = await SearchAsync(gbkService, gbkSession, new($"{root}/{Directory}", null, "needle"));
        Assert.AreSequenceEqual([new RemoteSearchHit($"{root}/{Directory}/{File}", false)], fromGbkDirectory,
            "从 GBK 名字的目录起找:目录名要编成 GBK 字节交给 find");

        RemoteSearchService utf8Service = Service(ssh, "vela-bash", out Guid utf8Session);
        (_, List<RemoteSearchHit> utf8) = await SearchAsync(utf8Service, utf8Session, new(root, "*.txt"));
        Assert.HasCount(1, utf8);
        Assert.DoesNotContain(Directory, utf8[0].Path, "UTF-8 会话照旧按 UTF-8 解:GBK 字节显示不出中文");
    }

    /// <summary>
    /// Big5 会话:「功」是 A5 5C,后一个字节恰好是 <c>\</c>。按名字找「功能」要找得到 <c>功能.txt</c>(那个字节在模式里得转义),
    /// 而不是把下一个字节转义掉、报「完成,0 项」(复查 file-panes F5)。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Big5Session_FindsNamesWhoseTrailByteIsABackslash()
    {
        RequireContainer();
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var big5 = System.Text.Encoding.GetEncoding("Big5");
        const string File = "功能.txt";
        await using VelaSshClientWrapper ssh = await ConnectAsync("vela-bash", TestContext.CancellationToken);
        string root = $"{Root}/big5";
        RemoteCommandResult created = await ssh.RunCommandDetailedAsync(
            "sh -c 'f=$(printf \"$2_\"); f=${f%_}; mkdir -p \"$1\" && printf \"x\\n\" > \"$1/$f\" && printf \"x\\n\" > \"$1/other.txt\"' fixture " +
            RemoteSha256.Quote(root) + " " + RemoteSha256.Quote(Octal(big5, File)),
            TestContext.CancellationToken);
        Assert.AreEqual(0, created.ExitCode, created.StandardError);

        RemoteSearchService service = Service(ssh, "vela-bash", out Guid session, fileNameEncoding: "Big5");
        (RemoteSearchOutcome outcome, List<RemoteSearchHit> hits) = await SearchAsync(service, session, new(root, "功能"));
        Assert.AreEqual(RemoteSearchStatus.Completed, outcome.Status, outcome.Detail);
        Assert.AreSequenceEqual([new RemoteSearchHit($"{root}/{File}", false)], hits);
    }

    private static string Octal(System.Text.Encoding encoding, string value) =>
        string.Concat(encoding.GetBytes(value).Select(static b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));

    private async Task<(RemoteSearchOutcome Outcome, List<RemoteSearchHit> Hits)> SearchAsync(
        RemoteSearchService service, Guid session, RemoteSearchQuery query)
    {
        var hits = new List<RemoteSearchHit>();
        RemoteSearchOutcome outcome = await service.SearchAsync(session, query, hit =>
        {
            lock (hits)
            {
                hits.Add(hit);
            }
            return ValueTask.CompletedTask;
        }, TestContext.CancellationToken);
        hits.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (outcome, hits);
    }

    private static RemoteSearchService Service(VelaSshClientWrapper ssh, string user, out Guid session, string? fileNameEncoding = null)
    {
        session = Guid.NewGuid();
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(session).Returns(ssh);
        connections.GetSession(session).Returns(new SshSession
        {
            SessionId = session,
            ConnectionInfo = new()
            {
                Host = TestHost,
                Port = TestPort,
                Username = user,
                AuthMethod = AuthMethod.Password,
                FileNameEncoding = fileNameEncoding,
            },
        });
        return new RemoteSearchService(connections);
    }

    private static async Task<VelaSshClientWrapper> ConnectAsync(string user, CancellationToken cancellationToken)
    {
        ConnectionInfo info = new()
        {
            Host = TestHost,
            Port = TestPort,
            Username = user,
            AuthMethod = AuthMethod.Password,
            Password = TestPassword,
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        await wrapper.ConnectAsync(cancellationToken);
        return wrapper;
    }

    private static bool ContainerReachable()
    {
        try
        {
            using TcpClient tcp = new();
            return tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>跳过记 Inconclusive 而不是"通过":全绿的报告里不能混着一行断言都没跑的用例。</summary>
    private static void RequireContainer()
    {
        if (!ContainerReachable())
        {
            Assert.Inconclusive($"[SKIP] ssh-shells 靶机 {TestHost}:{TestPort} 不可达。" +
                                "运行 'docker compose -f docker-compose.test.yml up -d --build ssh-shells'。");
        }
    }
}
