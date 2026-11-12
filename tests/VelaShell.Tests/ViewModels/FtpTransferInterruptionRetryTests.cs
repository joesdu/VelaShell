using System.Security.Cryptography;
using Avalonia.Headless;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Tests.Ftp;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// FTP 传输到一半数据连接被中止、控制连接还好好的:传输失败重试(设置 → 文件传输 → 失败自动重试)按临时失败再试,
/// 会话不标失效(树上的圆点不变灰),落下的文件与源逐字节一致。
/// </summary>
/// <remarks>
/// 走真实的环回 FTP 服务器、真实的 <see cref="FtpFileService" /> 与文件面板的重试层,不靠替身抛异常。
/// 中断用「干净的数据流 + 426」造,不靠复位:复位在 macOS 上读成干净的 EOF、在 Windows / Linux 上是 ConnectionReset,
/// FluentFTP 走的路因此看平台;干净的 EOF + 426 在三个平台上是同一条路。
/// 跑在 headless UI 线程上,理由见 <see cref="TransferRetryTests" />。
/// </remarks>
[TestClass]
[TestCategory("FileTransfer")]
[TestCategory("Ftp")]
public sealed class FtpTransferInterruptionRetryTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FtpTransferInterruptionRetryTests).Assembly);

    /// <summary>上传:服务器只落下前 64 KiB 就回 426;重试接着传(续传开着)或整份重传(关着),结果都与源一致。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AnUploadAbortedMidTransfer_IsRetried_WithoutFaultingTheSession(bool resume)
    {
        await using var rig = new Rig(resume);
        byte[] payload = new byte[1024 * 1024];
        new Random(41).NextBytes(payload);
        string local = Path.Combine(rig.LocalDirectory, "report.bin");
        await File.WriteAllBytesAsync(local, payload);
        int armed = 1;
        rig.Server.AbortStoreAfterBytes = _ => Interlocked.Exchange(ref armed, 0) == 1 ? 64 * 1024 : null;

        await rig.RunAsync(browser => browser.UploadLocalPathsAsync([local]));

        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status, rig.Browser?.ErrorMessage);
        Assert.HasCount(1, rig.Delays, "数据连接中断按临时失败重试了一次");
        Assert.AreEqual(2, rig.Server.StoreCounts["/report.bin"]);
        Assert.AreSequenceEqual(SHA256.HashData(payload),
            SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rig.Root, "report.bin"))));
        Assert.IsFalse(rig.Faulted, "数据连接中途断开不是会话掉线,树上的圆点不能变灰。");
    }

    /// <summary>
    /// 下载:读到 7000 字节数据流就结束、回 426(FluentFTP 若自己 REST 续读,那一次也回 426);
    /// 重试从本地那半截接着下(续传开着)或整份重下(关着),结果都与远端一致。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ADownloadAbortedMidTransfer_IsRetried_WithoutFaultingTheSession(bool resume)
    {
        await using var rig = new Rig(resume);
        byte[] payload = new byte[100_000];
        new Random(43).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(rig.Root, "data.bin"), payload);
        int retrieves = 0;
        rig.Server.ReadFaultForPath = path => path != "/data.bin"
            ? null
            : Interlocked.Increment(ref retrieves) == 1
                ? new() { DataLimit = 7_000, Reply = "426 Connection closed; transfer aborted" }
                : new() { RejectRetr = "426 Connection closed; transfer aborted" };
        // 第一次尝试失败之后就不再注入:后面的续传核实与重试都照常读。
        rig.OnRetry = () => rig.Server.ReadFaultForPath = null;

        await rig.RunAsync(async browser =>
        {
            RemoteFileInfo info = await rig.Ftp.GetFileInfoAsync(browser.SessionId, "/data.bin");
            await browser.DownloadRemoteEntriesAsync([new RemoteFileInfoViewModel(info)], rig.LocalDirectory);
        });

        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status, rig.Browser?.ErrorMessage);
        Assert.HasCount(1, rig.Delays, "数据连接中断按临时失败重试了一次");
        Assert.AreSequenceEqual(SHA256.HashData(payload),
            SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rig.LocalDirectory, "data.bin"))));
        Assert.IsFalse(rig.Faulted, "数据连接中途断开不是会话掉线,树上的圆点不能变灰。");
    }

    /// <summary>
    /// 服务器不认 APPE、上传被 426 中止:续传开着时重试先按远端那半截续(APPE 被 502 拒),因为那半截是这次传输自己写的,
    /// 改为从头整份重传 —— 不报永久的「不支持」,不经冲突策略,也不多算一次重试;续传关着本来就整份重传。
    /// 冲突策略设成「跳过」:要是这一步错走了冲突策略,这一项会以「跳过」收尾而不是传完。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AnInterruptedUpload_OnAServerWithoutAppe_IsRetriedFromTheStart(bool resume)
    {
        await using var rig = new Rig(resume, conflictPolicy: "skip");
        rig.Server.SupportsAppe = false;
        byte[] payload = new byte[1024 * 1024];
        new Random(59).NextBytes(payload);
        string local = Path.Combine(rig.LocalDirectory, "noappe.bin");
        await File.WriteAllBytesAsync(local, payload);
        int armed = 1;
        rig.Server.AbortStoreAfterBytes = _ => Interlocked.Exchange(ref armed, 0) == 1 ? 64 * 1024 : null;

        await rig.RunAsync(browser => browser.UploadLocalPathsAsync([local]));

        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status, rig.Browser?.ErrorMessage);
        Assert.HasCount(1, rig.Delays, "只有数据连接中断那一次重试;「续不了改从头」不另算一次");
        Assert.AreEqual(resume ? 1 : 0, rig.Server.UnsupportedResumeCommands, "续传开着时确实先试了续传、被服务器拒了");
        Assert.AreEqual(2, rig.Server.StoreCounts["/noappe.bin"], "中止的那一次 STOR + 从头重传的那一次 STOR");
        Assert.AreSequenceEqual(SHA256.HashData(payload),
            SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rig.Root, "noappe.bin"))));
        Assert.IsFalse(rig.Faulted);
    }

    /// <summary>
    /// 服务器不认 REST、下载被 426 中止:续传开着时重试先核实本地那半截、再按偏移续读(REST 被 502 拒),
    /// 那半截是这次传输自己写的,改为从头整份重下;结果与远端一致,会话不变灰。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AnInterruptedDownload_OnAServerWithoutRest_IsRetriedFromTheStart(bool resume)
    {
        await using var rig = new Rig(resume, conflictPolicy: "skip");
        rig.Server.SupportsRest = false;
        byte[] payload = new byte[100_000];
        new Random(61).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(rig.Root, "norest.bin"), payload);
        int retrieves = 0;
        rig.Server.ReadFaultForPath = path => path == "/norest.bin" && Interlocked.Increment(ref retrieves) == 1
            ? new() { DataLimit = 7_000, Reply = "426 Connection closed; transfer aborted" }
            : null;

        await rig.RunAsync(async browser =>
        {
            RemoteFileInfo info = await rig.Ftp.GetFileInfoAsync(browser.SessionId, "/norest.bin");
            await browser.DownloadRemoteEntriesAsync([new RemoteFileInfoViewModel(info)], rig.LocalDirectory);
        });

        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status, rig.Browser?.ErrorMessage);
        Assert.HasCount(1, rig.Delays, "只有数据连接中断那一次重试;「续不了改从头」不另算一次");
        // 中止的那一次 + (续传开着:核实本地那半截读的一段) + 从头重下的那一次。REST 被拒都发生在 RETR 之前。
        Assert.AreEqual(resume ? 3 : 2, rig.Server.RetrCounts["/norest.bin"]);
        Assert.AreSequenceEqual(SHA256.HashData(payload),
            SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rig.LocalDirectory, "norest.bin"))));
        Assert.IsFalse(rig.Faulted);
    }

    /// <summary>
    /// 服务器上本来就有一份较短的同名文件(不是这次写的),续传开着、服务器却不认 APPE:续不了时<b>不</b>悄悄整份覆盖,
    /// 照同名冲突策略办 —— 「跳过」就原样留着那份文件、这一项按跳过收尾;「覆盖」才整份重传。
    /// </summary>
    [TestMethod]
    [DataRow("skip")]
    [DataRow("overwrite")]
    public async Task APlannedResumeOfAnExistingFile_OnAServerWithoutAppe_FollowsTheConflictPolicy(string policy)
    {
        await using var rig = new Rig(resume: true, conflictPolicy: policy);
        rig.Server.SupportsAppe = false;
        byte[] payload = new byte[100_000];
        new Random(67).NextBytes(payload);
        string local = Path.Combine(rig.LocalDirectory, "existing.bin");
        await File.WriteAllBytesAsync(local, payload);
        Directory.CreateDirectory(rig.Root);
        await File.WriteAllBytesAsync(Path.Combine(rig.Root, "existing.bin"), payload[..30_000]);

        await rig.RunAsync(browser => browser.UploadLocalPathsAsync([local]));

        byte[] landed = await File.ReadAllBytesAsync(Path.Combine(rig.Root, "existing.bin"));
        Assert.AreEqual(1, rig.Server.UnsupportedResumeCommands, "先按续传试了、被服务器拒了");
        Assert.IsEmpty(rig.Delays, "「续不了」不是链路问题,不重试");
        if (policy == "skip")
        {
            Assert.AreEqual(TransferStatus.Cancelled, rig.Sink.Transfers.Single().Status, "按跳过收尾");
            Assert.AreSequenceEqual(payload[..30_000], landed, "那份已有的文件原样留着");
        }
        else
        {
            Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status, rig.Browser?.ErrorMessage);
            Assert.AreSequenceEqual(SHA256.HashData(payload), SHA256.HashData(landed));
        }
        Assert.IsFalse(rig.Faulted);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly bool _resume;
        private readonly string _conflictPolicy;

        public Rig(bool resume, string conflictPolicy = "overwrite")
        {
            _resume = resume;
            _conflictPolicy = conflictPolicy;
            Root = Path.Combine(Path.GetTempPath(), $"vela-ftp-retry-root-{Guid.NewGuid():N}");
            LocalDirectory = Path.Combine(Path.GetTempPath(), $"vela-ftp-retry-local-{Guid.NewGuid():N}");
            Directory.CreateDirectory(LocalDirectory);
            Server = new LoopbackFtpServer(Root);
            Ftp.SessionStateChanged += (_, change) => Faulted |= change.State == FtpSessionState.Faulted;
        }

        public string Root { get; }

        public string LocalDirectory { get; }

        public LoopbackFtpServer Server { get; }

        public FtpFileService Ftp { get; } = new();

        public FileTransferViewModel Sink { get; } = new(null);

        public FileBrowserViewModel? Browser { get; private set; }

        public List<TimeSpan> Delays { get; } = [];

        public Action? OnRetry { get; set; }

        public bool Faulted { get; private set; }

        /// <summary>连上环回服务器,建一个文件面板(重试照真实设置走,只是退避不真的等),在 UI 线程上跑 <paramref name="action" />。</summary>
        public Task RunAsync(Func<FileBrowserViewModel, Task> action) => _session.RunOnUiAsync(async () =>
        {
            Guid sessionId = await Ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = Server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            });
            Browser = new FileBrowserViewModel(Ftp, sessionId)
            {
                TransferSink = Sink,
                TransferOptions = new()
                {
                    ConflictPolicy = _conflictPolicy,
                    ResumeEnabled = _resume,
                    AutoCleanTempFiles = false,
                    TransferLogging = false,
                    NotifyOnComplete = false,
                    TransferMaxRetries = 3,
                },
                RetryDelay = (delay, _) =>
                {
                    Delays.Add(delay);
                    OnRetry?.Invoke();
                    return Task.CompletedTask;
                },
            };
            await action(Browser);
        });

        public async ValueTask DisposeAsync()
        {
            await Ftp.DisposeAsync();
            Server.Dispose();
            foreach (string directory in new[] { Root, LocalDirectory })
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // 临时目录删不掉不影响断言。
                }
            }
        }
    }
}
