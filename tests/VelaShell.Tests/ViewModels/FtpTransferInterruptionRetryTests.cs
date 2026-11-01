using System.Security.Cryptography;
using Avalonia.Headless;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
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

    private sealed class Rig : IAsyncDisposable
    {
        private readonly bool _resume;

        public Rig(bool resume)
        {
            _resume = resume;
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
                    ConflictPolicy = "overwrite",
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
