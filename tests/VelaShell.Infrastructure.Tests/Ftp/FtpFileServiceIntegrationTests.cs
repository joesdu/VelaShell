using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ftp;

namespace VelaShell.Infrastructure.Tests.Ftp;

/// <summary>
/// <see cref="FtpFileService" /> 打通真实 FTP 协议的端到端验证:对着
/// <see cref="LoopbackFtpServer" />(进程内、纯明文)跑登录、列目录、传输与目录操作。
/// <para>
/// 这里验证的是 Mock 验不到的那一段:PASV/EPSV 数据连接、Unix LIST 输出到
/// <see cref="RemoteFileInfo" /> 的解析、以及并发请求只复用一条控制连接。
/// FTPS 与 TLS 会话复用需要真实服务器(见 docs/FTP客户端可行性调研.md 第五节风险 1),不在此列。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Ftp")]
public class FtpFileServiceIntegrationTests
{
    private string _root = string.Empty;
    private LoopbackFtpServer _server = null!;
    private FtpFileService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vela-ftp-root-{Guid.NewGuid():N}");
        _server = new LoopbackFtpServer(_root);
        _service = new FtpFileService();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    [TestMethod]
    public async Task OpenSession_ThenList_ParsesEntriesFromUnixListing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "hello ftp");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));

        Guid sessionId = await OpenAsync();

        Assert.IsTrue(_service.OwnsSession(sessionId), "打开后该会话应归 FTP 后端所有(路由据此分派)。");
        Assert.AreEqual("/", await _service.GetWorkingDirectoryAsync(sessionId));

        List<RemoteFileInfo> entries = await _service.ListDirectoryAsync(sessionId, "/");

        Assert.HasCount(2, entries);
        RemoteFileInfo file = entries.Single(e => e.Name == "readme.txt");
        Assert.IsFalse(file.IsDirectory);
        Assert.AreEqual(9, file.Size);
        Assert.AreEqual("rw-r--r--", file.Permissions);   // 首位类型字符已剥掉
        Assert.AreEqual("deploy", file.Owner);            // FTP 给的是名字,不是 UID
        Assert.AreEqual("staff", file.Group);
        Assert.IsTrue(entries.Single(e => e.Name == "logs").IsDirectory);
    }

    [TestMethod]
    public async Task UploadThenDownload_RoundTripsContent()
    {
        Guid sessionId = await OpenAsync();
        string localSource = Path.Combine(_root, "..", $"vela-ftp-src-{Guid.NewGuid():N}.bin");
        string localTarget = $"{localSource}.out";
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 40_000) + "-tail");
        await File.WriteAllBytesAsync(localSource, payload);
        try
        {
            var uploadReports = new List<TransferProgress>();
            await _service.UploadFileAsync(sessionId, localSource, "/uploaded.bin",
                new Progress<TransferProgress>(uploadReports.Add));

            string remoteOnDisk = Path.Combine(_root, "uploaded.bin");
            Assert.IsTrue(File.Exists(remoteOnDisk), "上传后服务器根目录下应出现该文件。");
            byte[] uploaded = await File.ReadAllBytesAsync(remoteOnDisk);
            Assert.AreSequenceEqual(payload, uploaded);
            Assert.IsTrue(await _service.ExistsAsync(sessionId, "/uploaded.bin"));

            RemoteFileInfo info = await _service.GetFileInfoAsync(sessionId, "/uploaded.bin");
            Assert.AreEqual(payload.Length, info.Size);

            await _service.DownloadFileAsync(sessionId, "/uploaded.bin", localTarget);
            byte[] downloaded = await File.ReadAllBytesAsync(localTarget);
            Assert.AreSequenceEqual(payload, downloaded);
        }
        finally
        {
            File.Delete(localSource);
            File.Delete(localTarget);
        }
    }

    [TestMethod]
    public async Task OpenRead_StreamsSequentially_AndReleasesConnectionOnDispose()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "log.txt"), "line-1\nline-2\n");
        Guid sessionId = await OpenAsync();

        string content;
        await using (Stream stream = await _service.OpenReadAsync(sessionId, "/log.txt"))
        {
            Assert.IsFalse(stream.CanSeek, "FTP 数据流不可 Seek —— 这正是不能实现 ISftpClientWrapper 的原因。");
            using var reader = new StreamReader(stream, leaveOpen: true);
            content = await reader.ReadToEndAsync();
        }

        Assert.AreEqual("line-1\nline-2\n", content);
        // 流释放后连接必须已归还池子,否则后续操作会一直等在信号量上。
        List<RemoteFileInfo> entries = await _service.ListDirectoryAsync(sessionId, "/");
        Assert.HasCount(1, entries);
    }

    [TestMethod]
    public async Task DirectoryOperations_CreateRenameDelete()
    {
        Guid sessionId = await OpenAsync();

        await _service.CreateDirectoryAsync(sessionId, "/data");
        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "data")));

        await _service.CreateFileAsync(sessionId, "/data/note.txt");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "data", "note.txt")));

        await _service.RenameAsync(sessionId, "/data/note.txt", "/data/renamed.txt");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "data", "renamed.txt")));

        var deleteReports = new List<SftpDeleteProgress>();
        await _service.DeleteAsync(sessionId, "/data", new Progress<SftpDeleteProgress>(deleteReports.Add));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "data")), "递归删除应连同目录本身一起删掉。");
    }

    [TestMethod]
    public async Task MissingPath_TranslatesToPathNotFound()
    {
        Guid sessionId = await OpenAsync();

        await Assert.ThrowsExactlyAsync<VelaFtpPathNotFoundException>(
            () => _service.GetFileInfoAsync(sessionId, "/nope.txt"));
        Assert.IsFalse(await _service.ExistsAsync(sessionId, "/nope.txt"));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(16)]
    public async Task ConcurrentTransfers_ReuseSingleConnection_EvenWithLegacyLimit(int maxConnections)
    {
        // 旧配置即使允许多连接,并发请求也必须排队,不能先撞到服务端限制再降级。
        for (int i = 0; i < 4; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"f{i}.txt"), new string((char)('a' + i), 2048));
        }
        Guid sessionId = await OpenAsync(maxConnections: maxConnections);
        string outDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
                _service.DownloadFileAsync(sessionId, $"/f{i}.txt", Path.Combine(outDir, $"f{i}.txt"))));

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(2048, new FileInfo(Path.Combine(outDir, $"f{i}.txt")).Length);
            }
            Assert.AreEqual(1, _server.AcceptedConnections, "FTP 必须始终复用一条控制连接,不能按旧配置开多客户端。");
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    [TestMethod]
    public async Task OpenRead_QueuesTransfersAndMetadata_UntilStreamIsDisposed()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "held.txt"), "held", TestContext.CancellationToken);
        Guid sessionId = await OpenAsync(maxConnections: 4);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<List<RemoteFileInfo>> listing;
        Task upload;
        using var source = new MemoryStream([1, 2, 3]);
        await using (Stream input = await _service.OpenReadAsync(sessionId, "/held.txt", timeout.Token))
        {
            listing = _service.ListDirectoryAsync(sessionId, "/", timeout.Token);
            upload = _service.UploadStreamAsync(sessionId, source, "/queued.bin", source.Length,
                cancellationToken: timeout.Token);
            Assert.IsFalse(listing.IsCompleted, "目录刷新必须等待正在使用的唯一连接。");
            Assert.IsFalse(upload.IsCompleted, "上传不能为绕过下载租约而开启第二个客户端。");
            await input.CopyToAsync(Stream.Null, timeout.Token);
        }
        await Task.WhenAll(listing, upload).WaitAsync(timeout.Token);
        byte[] uploaded = await File.ReadAllBytesAsync(Path.Combine(_root, "queued.bin"), timeout.Token);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, uploaded);
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    [TestMethod]
    public async Task AnonymousLogin_SendsAnonymousCredentials()
    {
        Guid sessionId = await OpenAsync(anonymous: true);

        Assert.IsTrue(_service.OwnsSession(sessionId));
        Assert.AreEqual(FtpConnectionInfo.AnonymousUser, _server.LastUser);
        Assert.IsNotEmpty(_server.LastPassword ?? string.Empty);
    }

    [TestMethod]
    public async Task CloseSession_ReleasesOwnership()
    {
        Guid sessionId = await OpenAsync();
        Assert.IsTrue(_service.OwnsSession(sessionId));

        await _service.CloseSessionAsync(sessionId);

        Assert.IsFalse(_service.OwnsSession(sessionId));
        await Assert.ThrowsExactlyAsync<VelaFtpConnectionException>(
            () => _service.ListDirectoryAsync(sessionId, "/"));
    }

    /// <summary>
    /// 服务器只肯给一条控制连接(用户报的"仅支持单线程上传"那类)时,批量上传必须**全部成功**。
    /// </summary>
    /// <remarks>
    /// 修之前:第一个文件占住那唯一的连接,后面每个文件都去开第二条,被 421 顶回来 ——
    /// 用户看到的是"批量上传只成功一个,其余全失败",而且那个 <c>421</c> 被翻译成连接级异常,
    /// 顺带把整条会话标记成离线(树上的圆点变灰)。
    /// 修之后:池发现"已有活连接却开不出新连接",就把上限收成 1,后来的传输排队复用那一条。
    /// </remarks>
    [TestMethod]
    public async Task ConcurrentUploads_WhenServerAllowsOneConnection_AllSucceed()
    {
        _server.MaxConcurrentSessions = 1;
        string outDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            bool faulted = false;
            _service.SessionStateChanged += (_, change) => faulted |= change.State == FtpSessionState.Faulted;

            byte[] payload = [.. Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251))];
            var sources = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                string path = Path.Combine(outDir, $"batch{i}.bin");
                await File.WriteAllBytesAsync(path, payload, TestContext.CancellationToken);
                sources.Add(path);
            }
            Guid sessionId = await OpenAsync(maxConnections: 4);

            // 界面上的"最大并发传输数"就是这么发起的:所有文件同时开跑。
            await Task.WhenAll(sources.Select(source => _service.UploadFileAsync(
                sessionId,
                source,
                "/" + Path.GetFileName(source),
                progress: null,
                resumeOffset: 0,
                TestContext.CancellationToken)));

            foreach (string source in sources)
            {
                string landed = Path.Combine(_root, Path.GetFileName(source));
                Assert.IsTrue(File.Exists(landed), $"{Path.GetFileName(source)} 应已上传。");
                byte[] landedBytes = await File.ReadAllBytesAsync(landed, TestContext.CancellationToken);
                Assert.AreSequenceEqual(payload, landedBytes);
            }
            Assert.IsFalse(faulted, "退化成单连接是正常降级,不该把整条会话标记为离线。");
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    [TestMethod]
    [DataRow("425 Can't open data connection")]
    [DataRow("450 Transfer busy")]
    public async Task OpenRead_BusyBeforeReturningStream_DowngradesAndRetries(string reply)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "busy-read.bin"), [1, 2, 3], TestContext.CancellationToken);
        int opens = 0;
        _server.ReadFaultForPath = _ => Interlocked.Increment(ref opens) == 1
            ? new LoopbackFtpServer.ReadFault { RejectRetr = reply } : null;
        Guid session = await OpenAsync(maxConnections: 3);
        await using (Stream input = await _service.OpenReadAsync(session, "/busy-read.bin", TestContext.CancellationToken))
        {
            using var output = new MemoryStream();
            await input.CopyToAsync(output, TestContext.CancellationToken);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, output.ToArray());
        }
        Assert.AreEqual(2, opens);
    }

    [TestMethod]
    public async Task OpenRead_PersistentBusy_HasBoundedRetry()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "busy-read.bin"), [1], TestContext.CancellationToken);
        int opens = 0;
        _server.ReadFaultForPath = _ =>
        {
            Interlocked.Increment(ref opens);
            return new LoopbackFtpServer.ReadFault { RejectRetr = "450 Transfer busy" };
        };
        Guid session = await OpenAsync(maxConnections: 3);
        await Assert.ThrowsAsync<Exception>(() => _service.OpenReadAsync(session, "/busy-read.bin", TestContext.CancellationToken));
        Assert.AreEqual(3, opens);
    }

    /// <summary>
    /// 另一类服务器:控制连接随便开,但**一次只让传一个文件**(第二个 STOR 直接 450)。
    /// 池那条自适应看不到这种拒绝(连接开得出来),得由传输层收紧后重试。
    /// </summary>
    [TestMethod]
    public async Task ConcurrentUploads_WhenServerAllowsOneTransfer_AllSucceed()
    {
        _server.MaxConcurrentTransfers = 1;
        string outDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            byte[] payload = [.. Enumerable.Range(0, 8192).Select(static i => (byte)(i % 253))];
            var sources = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(outDir, $"busy{i}.bin");
                await File.WriteAllBytesAsync(path, payload, TestContext.CancellationToken);
                sources.Add(path);
            }
            Guid sessionId = await OpenAsync(maxConnections: 4);

            await Task.WhenAll(sources.Select(source => _service.UploadFileAsync(
                sessionId,
                source,
                "/" + Path.GetFileName(source),
                progress: null,
                resumeOffset: 0,
                TestContext.CancellationToken)));

            foreach (string source in sources)
            {
                string landed = Path.Combine(_root, Path.GetFileName(source));
                Assert.IsTrue(File.Exists(landed), $"{Path.GetFileName(source)} 应已上传。");
                byte[] landedBytes = await File.ReadAllBytesAsync(landed, TestContext.CancellationToken);
                Assert.AreSequenceEqual(payload, landedBytes);
            }
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    /// <summary>
    /// 目录同步在 FTP 上成立的前提:上传后用 MFMT 把远端时间对齐本地,再经 LIST 读回来,
    /// 比较器必须判「相同」。这一路上有三处会让它判成「不同」—— MFMT 按 UTC 发、LIST 按服务器
    /// 当地时间给且只到分钟、FluentFTP 不做时区换算 —— 单测用替身一处都测不到。
    /// </summary>
    [TestMethod]
    public async Task SetLastWriteTime_SurvivesTheListingRoundTrip_SoASyncCompareSeesNoDifference()
    {
        string localDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDir);
        try
        {
            string localFile = Path.Combine(localDir, "a.txt");
            await File.WriteAllTextAsync(localFile, "sync me", TestContext.CancellationToken);
            // 几小时前、秒不为 0:LIST 只给到分钟,这样才验得到「按分钟截断后相同」。
            DateTime stamp = DateTime.UtcNow.AddHours(-3);
            stamp = new DateTime(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, 37, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(localFile, stamp);
            Guid sessionId = await OpenAsync();

            await _service.UploadFileAsync(sessionId, localFile, "/a.txt", null, 0, TestContext.CancellationToken);
            Assert.AreNotEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "a.txt")), "刚上传的文件时间应当是现在,否则后面的断言验不出 MFMT");
            await _service.SetLastWriteTimeAsync(sessionId, "/a.txt", stamp, TestContext.CancellationToken);

            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "a.txt")), "服务器收到的应当是 UTC 时间");
            SyncTree remote = await DirectoryTreeScanner.ScanRemoteAsync(
                _service, sessionId, "/", SyncFileMask.Empty, inferPrecision: true, cancellationToken: TestContext.CancellationToken);
            SyncTree local = await DirectoryTreeScanner.ScanLocalAsync(localDir, SyncFileMask.Empty, cancellationToken: TestContext.CancellationToken);
            SyncItem listed = remote.Items.Single();
            Assert.AreEqual(SyncTimePrecision.Minute, listed.Precision);
            Assert.AreEqual(SyncComparisonState.Same,
                DirectoryComparer.Compare(local.Items, remote.Items, new SyncOptions()).Single().State);

            // 反证:本地再晚一分钟,就必须判成本地较新。
            File.SetLastWriteTimeUtc(localFile, stamp.AddMinutes(1));
            SyncTree touched = await DirectoryTreeScanner.ScanLocalAsync(localDir, SyncFileMask.Empty, cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(SyncComparisonState.LocalNewer,
                DirectoryComparer.Compare(touched.Items, remote.Items, new SyncOptions()).Single().State);
        }
        finally
        {
            Directory.Delete(localDir, true);
        }
    }

    /// <summary>
    /// 服务器与本机不在同一时区:LIST 给的是服务器墙上时间,不配偏移时整体错位、判成不同;
    /// 配上服务器时区,同一份文件就是「相同」。MLSD 给的是 UTC,配着同样的偏移也照样「相同」(不能再挪一遍)。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ServerInAnotherTimeZone_SyncCompareNeedsTheOffsetForListButNotForMlsd(bool mlsd)
    {
        // 服务器时区 = 本机偏移 + 3 小时(夹在 UTC−12…UTC+14 里):不管测试机在哪个时区,两边都一定不同。
        TimeSpan local = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        TimeSpan server = local + TimeSpan.FromHours(3) > TimeSpan.FromHours(14) ? local - TimeSpan.FromHours(3) : local + TimeSpan.FromHours(3);
        _server.ListClockOffset = server;
        _server.SupportsMlsd = mlsd;
        string localDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-tz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDir);
        try
        {
            // 同一个时刻落在两边(几小时前、秒为 0:LIST 只到分钟)。
            DateTime stamp = DateTime.UtcNow.AddHours(-5);
            stamp = new DateTime(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, 0, DateTimeKind.Utc);
            string localFile = Path.Combine(localDir, "a.txt");
            await File.WriteAllTextAsync(localFile, "same", TestContext.CancellationToken);
            File.SetLastWriteTimeUtc(localFile, stamp);
            string remoteFile = Path.Combine(_root, "a.txt");
            await File.WriteAllTextAsync(remoteFile, "same", TestContext.CancellationToken);
            File.SetLastWriteTimeUtc(remoteFile, stamp);
            Guid sessionId = await OpenAsync();
            SyncTree localTree = await DirectoryTreeScanner.ScanLocalAsync(localDir, SyncFileMask.Empty, cancellationToken: TestContext.CancellationToken);

            SyncTree withOffset = await DirectoryTreeScanner.ScanRemoteAsync(
                _service, sessionId, "/", SyncFileMask.Empty, inferPrecision: true, serverClockOffset: server,
                cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(stamp, withOffset.Items.Single().LastWriteTimeUtc);
            Assert.AreEqual(SyncComparisonState.Same,
                DirectoryComparer.Compare(localTree.Items, withOffset.Items, new SyncOptions()).Single().State);

            SyncTree withoutOffset = await DirectoryTreeScanner.ScanRemoteAsync(
                _service, sessionId, "/", SyncFileMask.Empty, inferPrecision: true,
                cancellationToken: TestContext.CancellationToken);
            SyncComparisonState legacy = DirectoryComparer.Compare(localTree.Items, withoutOffset.Items, new SyncOptions()).Single().State;
            if (mlsd)
            {
                Assert.AreEqual(SyncComparisonState.Same, legacy, "MLSD 给的是 UTC,本来就对得上");
            }
            else
            {
                Assert.AreNotEqual(SyncComparisonState.Same, legacy, "不配偏移时 LIST 的时间整体错开 —— 这正是要修的问题");
            }
        }
        finally
        {
            Directory.Delete(localDir, true);
        }
    }

    [TestMethod]
    public async Task SetLastWriteTime_OnAServerWithoutMfmt_ReportsNotSupported()
    {
        _server.SupportsMfmt = false;
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x", TestContext.CancellationToken);
        Guid sessionId = await OpenAsync();

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _service.SetLastWriteTimeAsync(sessionId, "/a.txt", DateTime.UtcNow, TestContext.CancellationToken));
    }

    /// <summary>服务器端 SHA-256:FluentFTP 经 FEAT 发现 XSHA256,摘要与本地算的一致;不存在的文件只让它自己为 null。</summary>
    [TestMethod]
    public async Task ComputeSha256_UsesTheServerSideCommand_AndMatchesTheLocalDigest()
    {
        byte[] payload = Encoding.UTF8.GetBytes("checksum me");
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.txt"), payload, TestContext.CancellationToken);
        Guid sessionId = await OpenAsync();

        IReadOnlyDictionary<string, string?> digests =
            await _service.ComputeSha256Async(sessionId, ["/a.txt", "/missing.txt"], TestContext.CancellationToken);

        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), digests["/a.txt"]);
        Assert.IsNull(digests["/missing.txt"]);
    }

    [TestMethod]
    public async Task ComputeSha256_OnAServerWithoutHashCommands_ReportsNotSupported()
    {
        _server.SupportsSha256 = false;
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x", TestContext.CancellationToken);
        Guid sessionId = await OpenAsync();

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _service.ComputeSha256Async(sessionId, ["/a.txt"], TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task OpenRead_TypeDependentSize_UsesBinarySizeOnceOnFirstLease()
    {
        byte[] payload = Encoding.UTF8.GetBytes("one\ntwo\nthree\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "text.txt"), payload);
        _server.SizeReplyForPath = (_, binary) => $"213 {payload.Length + (binary ? 0 : 3)}";
        Guid session = await OpenAsync(maxConnections: 1);

        await using (Stream stream = await _service.OpenReadAsync(session, "/text.txt"))
        {
            Assert.AreEqual(payload.Length, stream.Length, "TYPE A 的 SIZE 不能成为 Binary RETR 的精确长度。");
            using var received = new MemoryStream();
            await stream.CopyToAsync(received);
            Assert.AreSequenceEqual(payload, received.ToArray());
        }
        Assert.AreEqual(1, _server.RetrCounts["/text.txt"], "首次必须成功,不能靠重试修正 TYPE 状态。");
        Assert.AreEqual(1, _server.SizeCounts["/text.txt"], "OpenRead 不得重复 SIZE。");
        Assert.AreEqual(1, _server.TypeCommands, "公开 TYPE 缓存应阻止 OpenRead 再发 TYPE。");
        Assert.IsTrue(_server.SizeRequests.All(request => request.Binary));
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetFileInfo_ListingSizeDiffersFromBinarySize_RelaySucceedsWithoutRetry(bool mlsd)
    {
        byte[] payload = Encoding.UTF8.GetBytes("a\nb\nc\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "text.txt"), payload);
        _server.SupportsMlsd = mlsd;
        _server.ListingSizeForPath = (_, length) => length + 17;
        _server.SizeReplyForPath = (_, binary) => $"213 {payload.Length + (binary ? 0 : 3)}";
        Guid session = await OpenAsync(maxConnections: 1);
        RemoteFileInfo stat = await _service.GetFileInfoAsync(session, "/text.txt");
        Assert.AreEqual(payload.Length, stat.Size, "Relay stat 应优先使用 Binary SIZE,而非 LIST/MLST 大小。");
        (SftpService? target, Guid targetSession, ConcurrentDictionary<string, byte[]>? received) = CreateRelayTarget();

        await RemoteRelay.CopyFileAsync(_service, session, "/text.txt", target, targetSession, "/text.txt");

        Assert.AreSequenceEqual(payload, received["/text.txt"]);
        Assert.AreEqual(1, _server.RetrCounts["/text.txt"]);
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow("500 SIZE unsupported")]
    [DataRow("502 SIZE not implemented")]
    [DataRow("550 Size unavailable")]
    [DataRow("213 -1")]
    public async Task GetFileInfo_UnknownSize_DoesNotTrustListingAndRelayReadsToEof(string sizeReply)
    {
        byte[] payload = Encoding.UTF8.GetBytes("unknown\nlength\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "text.txt"), payload);
        _server.SupportsMlsd = true;
        _server.ListingSizeForPath = (_, length) => length + 100;
        _server.SizeReplyForPath = (_, _) => sizeReply;
        Guid session = await OpenAsync(maxConnections: 1);
        RemoteFileInfo stat = await _service.GetFileInfoAsync(session, "/text.txt");
        Assert.AreEqual(0, stat.Size, "无可信 SIZE 时沿用非正长未知策略,不能把 LIST 大小当精确值。");
        await using (Stream stream = await _service.OpenReadAsync(session, "/text.txt"))
        {
            Assert.AreEqual(-1, stream.Length, "流内部须区分未知长度与已知空文件。");
            await stream.CopyToAsync(Stream.Null);
        }
        (SftpService? target, Guid targetSession, ConcurrentDictionary<string, byte[]>? received) = CreateRelayTarget();
        await RemoteRelay.CopyFileAsync(_service, session, "/text.txt", target, targetSession, "/text.txt");
        Assert.AreSequenceEqual(payload, received["/text.txt"]);
        Assert.AreEqual(2, _server.RetrCounts["/text.txt"], "一次直接读加一次 relay,各自仅一次 RETR。");
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    private static (SftpService Service, Guid Session, ConcurrentDictionary<string, byte[]> Received) CreateRelayTarget()
    {
        var session = Guid.NewGuid();
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        ISftpClientWrapper client = Substitute.For<ISftpClientWrapper>();
        client.IsConnected.Returns(true);
        connections.GetSession(session).Returns(new SshSession
        {
            SessionId = session,
            ConnectionInfo = new() { Host = "mock-target", Username = "u", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected,
        });
        var received = new ConcurrentDictionary<string, byte[]>();
        client.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<RemoteUploadOptions>(),
                Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var output = new MemoryStream();
                await call.ArgAt<Stream>(0).CopyToAsync(output, call.ArgAt<CancellationToken>(4));
                Assert.IsTrue(received.TryAdd(call.ArgAt<string>(1), output.ToArray()));
            });
        return (new SftpService(connections, _ => client), session, received);
    }

    [TestMethod]
    public async Task OpenRead_AsyncDispose_WaitsFor226BeforeReturningTheOnlyLease()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "first payload");
        var fault = new LoopbackFtpServer.ReadFault { HoldReply = true };
        _server.ReadFaultForPath = _ => fault;
        Guid session = await OpenAsync(maxConnections: 1);
        Stream stream = await _service.OpenReadAsync(session, "/a.txt");
        await stream.CopyToAsync(Stream.Null);
        await fault.DataSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task closing = stream.DisposeAsync().AsTask();
        Task<RemoteFileInfo> next = _service.GetFileInfoAsync(session, "/a.txt");
        try
        {
            Assert.IsFalse(closing.IsCompleted, "纯 async disposal 必须等最终 226,不能仅关闭数据 socket 后归池。");
            Assert.IsFalse(next.IsCompleted, "226 尚未消费时租约必须仍独占。");
        }
        finally
        {
            fault.AllowReply.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            await next.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow("426 Transfer aborted", false)]
    [DataRow("451 Local error", false)]
    [DataRow("150 Not a completion", false)]
    [DataRow(null, false)]
    [DataRow(null, true)]
    public async Task OpenRead_InvalidCompletion_FailsAndReplacesConnection(string? reply, bool closeControl)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "payload");
        _server.ReadFaultForPath = _ => new() { Reply = reply, CloseControl = closeControl };
        Guid session = await OpenAsync(maxConnections: 1);
        // 不增加生产配置入口:只缩短这个测试的控制读超时,生产默认仍为 30 秒。
        var pools = (ConcurrentDictionary<Guid, FtpConnectionPool>)typeof(FtpFileService)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_service)!;
        using (FtpConnectionPool.Lease lease = await pools[session].RentAsync(TestContext.CancellationToken))
        {
            lease.Client.Config.ReadTimeout = 250;
        }
        Stream stream = await _service.OpenReadAsync(session, "/a.txt");
        await stream.CopyToAsync(Stream.Null);
        await Assert.ThrowsAsync<VelaFtpClientException>(() => stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)),
            "负回复、非完成回复、缺失回复和空回复都必须作为 FTP 失败返回,不能仅由测试超时打断。");
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.txt", Encoding.UTF8.GetBytes("payload"));
        Assert.AreEqual(2, _server.AcceptedConnections, "IsConnected 仍为 true 也不能复用未同步的控制连接。");
    }

    [TestMethod]
    [DataRow("426 Transfer aborted")]
    [DataRow(null)]
    public async Task OpenRead_UnknownSizeAndZeroData_StillRequiresSuccessfulCompletion(string? reply)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "nonempty source");
        _server.SupportsMlsd = true;
        _server.SizeReplyForPath = (_, _) => "550 Size unavailable";
        _server.ReadFaultForPath = _ => new() { DataLimit = 0, Reply = reply };
        Guid session = await OpenAsync(maxConnections: 1);
        var pools = (ConcurrentDictionary<Guid, FtpConnectionPool>)typeof(FtpFileService)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_service)!;
        using (FtpConnectionPool.Lease lease = await pools[session].RentAsync(TestContext.CancellationToken))
        {
            lease.Client.Config.ReadTimeout = 250;
        }
        (SftpService? target, Guid targetSession, ConcurrentDictionary<string, byte[]> _) = CreateRelayTarget();

        await Assert.ThrowsAsync<VelaFtpClientException>(() => RemoteRelay.CopyFileAsync(
            _service, session, "/a.txt", target, targetSession, "/a.txt").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(1, _server.RetrCounts["/a.txt"]);
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.txt", Encoding.UTF8.GetBytes("nonempty source"));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow("426 Transfer aborted", false)]
    [DataRow("451 Local error", false)]
    [DataRow("150 Not a completion", false)]
    [DataRow(null, false)]
    [DataRow(null, true)]
    public async Task Relay_SmallSourceInvalidCompletion_FailsBeforeWritingAndReleasesLease(string? reply, bool closeControl)
    {
        byte[] payload = Encoding.UTF8.GetBytes("complete data, failed completion");
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.txt"), payload);
        _server.SupportsMlsd = true;
        _server.ReadFaultForPath = _ => new() { Reply = reply, CloseControl = closeControl };
        Guid session = await OpenAsync(maxConnections: 1);
        var pools = (ConcurrentDictionary<Guid, FtpConnectionPool>)typeof(FtpFileService)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_service)!;
        using (FtpConnectionPool.Lease lease = await pools[session].RentAsync(TestContext.CancellationToken))
        {
            lease.Client.Config.ReadTimeout = 250;
        }
        (SftpService? target, Guid targetSession, ConcurrentDictionary<string, byte[]>? received) = CreateRelayTarget();
        bool writing = false;

        await Assert.ThrowsAsync<VelaFtpClientException>(() => RemoteRelay.CopyFileAsync(
            _service, session, "/a.txt", target, targetSession, "/a.txt", writing: () => writing = true)
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsFalse(writing, "完整字节不等于源成功:最终回复必须先验证,否则会破坏已有目标。");
        Assert.IsEmpty(received);
        Assert.AreEqual(1, _server.RetrCounts["/a.txt"]);
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.txt", payload);
        Assert.AreEqual(2, _server.AcceptedConnections, "失败 completion 必须丢弃连接,但归还唯一租约名额。");
    }

    [TestMethod]
    public async Task Relay_SmallSource_ReturnsOnlyFtpLeaseBeforeCallingUpload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("buffered before upload");
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.txt"), payload);
        _server.SupportsMlsd = true;
        Guid session = await OpenAsync(maxConnections: 1);
        ISftpService target = Substitute.For<ISftpService>();
        byte[]? received = null;
        bool writing = false;
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Assert.IsTrue(writing);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // 上传入口立即复用源会话的唯一租约:未等 226/Dispose 就调用目标时,这里会卡住并失败。
            RemoteFileInfo stat = await _service.GetFileInfoAsync(session, "/a.txt", timeout.Token);
            Assert.AreEqual(payload.LongLength, stat.Size);
            using var output = new MemoryStream();
            await call.ArgAt<Stream>(1).CopyToAsync(output, timeout.Token);
            received = output.ToArray();
        });

        await RemoteRelay.CopyFileAsync(_service, session, "/a.txt", target, Guid.NewGuid(), "/a.txt",
            writing: () => writing = true).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsNotNull(received);
        Assert.AreSequenceEqual(payload, received);
        Assert.AreEqual(1, _server.RetrCounts["/a.txt"]);
        Assert.AreEqual(1, _server.AcceptedConnections, "预读并确认成功后应复用原连接,不增加连接来躲过租约泄漏。");
        await target.ReceivedWithAnyArgs(1).UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OpenRead_EarlyAndMixedRepeatedDispose_DoesNotLeakOrReuseConnection(bool synchronous)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), new string('a', 8192));
        Guid session = await OpenAsync(maxConnections: 1);
        Stream stream = await _service.OpenReadAsync(session, "/a.txt");
        Assert.AreEqual(1, await stream.ReadAsync(new byte[1]));
        if (synchronous)
        {
            stream.Dispose();
        }
        else
        {
            await stream.DisposeAsync();
        }
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            if (i % 2 == 0) stream.Dispose();
            else await stream.DisposeAsync();
        })));
        await AssertReadableAsync(session, "/a.txt", Encoding.UTF8.GetBytes(new string('a', 8192)));
        Assert.AreEqual(2, _server.AcceptedConnections, "提前弃读必须丢弃连接并归还唯一名额。");
        Stream held = await _service.OpenReadAsync(session, "/a.txt");
        Task<Stream> waiting = _service.OpenReadAsync(session, "/a.txt");
        try
        {
            Assert.IsFalse(waiting.IsCompleted, "重复 Dispose 不能多释放名额,突破单连接上限。");
            await held.CopyToAsync(Stream.Null);
        }
        finally
        {
            await held.DisposeAsync();
        }
        await using Stream next = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await next.CopyToAsync(Stream.Null);
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    public async Task OpenRead_CancelledRead_PreservesCancellationAndReleasesLease()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "payload");
        var fault = new LoopbackFtpServer.ReadFault { HoldData = true, Reply = "426 Aborted" };
        _server.ReadFaultForPath = _ => fault;
        Guid session = await OpenAsync(maxConnections: 1);
        Stream stream = await _service.OpenReadAsync(session, "/a.txt");
        await fault.DataOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task<int> read = stream.ReadAsync(new byte[16], cancellation.Token).AsTask();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            fault.AllowData.TrySetResult();
        }
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.txt", Encoding.UTF8.GetBytes("payload"));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    public async Task OpenRead_CancelDuringCompletion_DiscardsConnectionAndPreservesCancellation()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "payload");
        var fault = new LoopbackFtpServer.ReadFault { HoldReply = true };
        _server.ReadFaultForPath = _ => fault;
        Guid session = await OpenAsync(maxConnections: 1);
        using var cancellation = new CancellationTokenSource();
        Stream stream = await _service.OpenReadAsync(session, "/a.txt", cancellation.Token);
        await stream.CopyToAsync(Stream.Null);
        Task closing = stream.DisposeAsync().AsTask();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => closing.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            fault.AllowReply.TrySetResult();
        }
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.txt", Encoding.UTF8.GetBytes("payload"));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(0, "226 Transfer complete")]
    [DataRow(7, "226 Transfer complete")]
    [DataRow(7, "426 Truncated")]
    public async Task OpenRead_TruncatedData_PreservesReadErrorAndDiscardsLease(int delivered, string reply)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), new string('a', 1024));
        _server.ReadFaultForPath = _ => new() { DataLimit = delivered, Reply = reply };
        Guid session = await OpenAsync(maxConnections: 1);
        Stream stream = await _service.OpenReadAsync(session, "/a.txt");
        RemoteTransferLengthException error = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() => stream.CopyToAsync(Stream.Null));
        Assert.AreEqual(1024, error.ExpectedBytes);
        Assert.AreEqual(delivered, error.ActualBytes);
        Assert.AreEqual(Core.Resources.Strings.Format("SftpSvc_RelayLengthMismatch", "/a.txt", 1024, delivered), error.Message);
        await stream.DisposeAsync();
        stream.Dispose();
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.txt", Encoding.UTF8.GetBytes(new string('a', 1024)));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    public async Task OpenRead_RelayWithPositiveStatButTruncatedData_CannotReportSuccess(int delivered)
    {
        byte[] payload = Payload(42);
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.bin"), payload);
        _server.SupportsMlsd = true;
        _server.ReadFaultForPath = _ => new() { DataLimit = delivered, Reply = "426 Truncated" };
        Guid session = await OpenAsync(maxConnections: 1);
        var targetSession = Guid.NewGuid();
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        ISftpClientWrapper client = Substitute.For<ISftpClientWrapper>();
        client.IsConnected.Returns(true);
        connections.GetSession(targetSession).Returns(new SshSession
        {
            SessionId = targetSession,
            ConnectionInfo = new() { Host = "mock-target", Username = "u", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected,
        });
        client.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<RemoteUploadOptions>(),
                Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.ArgAt<Stream>(0).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(4)));
        var target = new SftpService(connections, _ => client);
        bool writing = false;
        RemoteTransferLengthException error = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() => RemoteRelay.CopyFileAsync(
            _service, session, "/a.bin", target, targetSession, "/a.bin", writing: () => writing = true));
        Assert.IsFalse(writing, "正长度小顺序源必须先通过完整预读和长度验证,才允许触碰目标。");
        await client.DidNotReceiveWithAnyArgs().UploadAsync(null!, null!, null!, null, default);
        Assert.AreEqual(1, _server.RetrCounts["/a.bin"]);
        long expectedBytes = payload.LongLength;
        Assert.AreEqual(expectedBytes, error.ExpectedBytes);
        Assert.AreEqual(delivered, error.ActualBytes);
        Assert.AreEqual(Core.Resources.Strings.Format(
            "SftpSvc_RelayLengthMismatch", "/a.bin", payload.Length, delivered), error.Message);
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.bin", payload);
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OpenRead_EmptyFileAnd250Completion_MixedDisposeIsIdempotent(bool synchronous)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "empty"), []);
        _server.ReadFaultForPath = _ => new() { Reply = "250 Transfer complete" };
        Guid session = await OpenAsync(maxConnections: 1);
        Stream stream = await _service.OpenReadAsync(session, "/empty");
        await stream.CopyToAsync(Stream.Null);
        if (synchronous) stream.Dispose();
        else await stream.DisposeAsync();
        stream.Dispose();
        await stream.DisposeAsync();
        await AssertReadableAsync(session, "/empty", []);
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    [TestMethod]
    public async Task OpenRead_ConcurrentMixedDispose_WaitsForTheSameCompletion()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "payload");
        var fault = new LoopbackFtpServer.ReadFault { HoldReply = true };
        _server.ReadFaultForPath = _ => fault;
        Guid session = await OpenAsync(maxConnections: 1);
        Stream stream = await _service.OpenReadAsync(session, "/a.txt");
        await stream.CopyToAsync(Stream.Null);
        Task closing = stream.DisposeAsync().AsTask();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncClosing = Task.Run(() => { entered.SetResult(); stream.Dispose(); });
        await entered.Task;
        try
        {
            Assert.IsFalse(closing.IsCompleted);
            Assert.IsFalse(syncClosing.IsCompleted);
        }
        finally
        {
            fault.AllowReply.TrySetResult();
            await Task.WhenAll(closing, syncClosing).WaitAsync(TimeSpan.FromSeconds(10));
        }
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/a.txt", Encoding.UTF8.GetBytes("payload"));
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(3, false)]
    [DataRow(1, true)]
    [DataRow(3, true)]
    public async Task OpenRead_Relay2000DistinctFiles_LengthAndHashMatch(int concurrency, bool mixedMetadata)
    {
        const int count = 2000;
        for (int i = 0; i < count; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_root, $"f{i}.bin"), RelayPayload(i, mixedMetadata), TestContext.CancellationToken);
        }
        _server.SupportsMlsd = true;
        if (mixedMetadata)
        {
            _server.ListingSizeForPath = (_, length) => length + 31;
            _server.SizeReplyForPath = (path, binary) =>
            {
                int index = int.Parse(Path.GetFileNameWithoutExtension(path).AsSpan(1));
                if (index % 5 == 0) return "550 Size unavailable";
                return $"213 {RelayPayload(index, true).Length + (binary ? 0 : 7)}";
            };
        }
        Guid sourceSession = await OpenAsync(maxConnections: concurrency);
        var targetSession = Guid.NewGuid();
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        ISftpClientWrapper client = Substitute.For<ISftpClientWrapper>();
        client.IsConnected.Returns(true);
        connections.GetSession(targetSession).Returns(new SshSession
        {
            SessionId = targetSession,
            ConnectionInfo = new() { Host = "mock-target", Username = "u", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected,
        });
        var received = new ConcurrentDictionary<string, byte[]>();
        client.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<RemoteUploadOptions>(),
                Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var target = new MemoryStream();
                await call.ArgAt<Stream>(0).CopyToAsync(target, call.ArgAt<CancellationToken>(4));
                Assert.IsTrue(received.TryAdd(call.ArgAt<string>(1), target.ToArray()));
            });
        var targetService = new SftpService(connections, _ => client);
        await Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = TestContext.CancellationToken },
            async (i, token) => await RemoteRelay.CopyFileAsync(_service, sourceSession, $"/f{i}.bin",
                targetService, targetSession, $"/f{i}.bin", cancellationToken: token));
        Assert.HasCount(count, received);
        for (int i = 0; i < count; i++)
        {
            byte[] expected = RelayPayload(i, mixedMetadata);
            byte[] actual = received[$"/f{i}.bin"];
            Assert.AreEqual(1, _server.RetrCounts[$"/f{i}.bin"], $"f{i}.bin 应首次成功,不得重试 RETR");
            Assert.AreEqual(2, _server.SizeCounts[$"/f{i}.bin"], $"f{i}.bin stat/open 各一次 SIZE,OpenRead 不得隐式重查");
            Assert.HasCount(expected.Length, actual, $"f{i}.bin length");
            Assert.AreSequenceEqual(SHA256.HashData(expected), SHA256.HashData(actual), $"f{i}.bin SHA-256");
        }
        Assert.IsLessThanOrEqualTo(concurrency, _server.AcceptedConnections);
        Assert.AreEqual(_server.AcceptedConnections, _server.PassiveListenersCreated,
            "Passive listeners must be bounded by control sessions, not by the number of files.");
        if (concurrency == 1) Assert.AreEqual(1, _server.AcceptedConnections);
        Assert.IsTrue(_server.SizeRequests.All(request => request.Binary));
        Assert.AreEqual(_server.AcceptedConnections, _server.TypeCommands, "默认 Binary listing 每连接只需一次 TYPE,不得每文件盲发。");
        TestContext.WriteLine($"Relayed {count} distinct files; concurrency={concurrency}; mixedMetadata={mixedMetadata}; FTP connections={_server.AcceptedConnections}; TYPE commands={_server.TypeCommands}; each RETR=1; each SIZE=2; all lengths/SHA-256 match.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    public async Task DownloadStream_ReadOnlyDestination_RejectsBeforeRentOrRetr(int length)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), new byte[length]);
        Guid session = await OpenAsync(maxConnections: 1);
        using var destination = new MemoryStream([], writable: false);
        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => _service
            .DownloadStreamAsync(session, "/producer.bin", destination));
        Assert.AreEqual("destination", error.ParamName);
        Assert.IsEmpty(_server.RetrCounts);
        Assert.IsEmpty(_server.SizeRequests);
        Assert.AreEqual(1, _server.AcceptedConnections);
        Assert.IsTrue(destination.CanRead, "Validation must not close the caller-owned stream.");

        // An unavailable session must not hide the argument error: validation precedes renting.
        error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => _service
            .DownloadStreamAsync(Guid.NewGuid(), "/producer.bin", destination));
        Assert.AreEqual("destination", error.ParamName);
        Assert.IsEmpty(_server.RetrCounts);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(8193)]
    public async Task DownloadStream_MatchesOpenRead_FromSameSourceAndKeepsDestinationOpen(int length)
    {
        byte[] payload = new byte[length];
        new Random(17).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), payload);
        _server.SizeReplyForPath = (_, binary) => $"213 {length + (binary ? 0 : 17)}";
        Guid session = await OpenAsync(maxConnections: 1);
        using var destination = new MemoryStream();
        await _service.DownloadStreamAsync(session, "/producer.bin", destination);
        Assert.IsTrue(destination.CanWrite);
        Assert.AreSequenceEqual(payload, destination.ToArray());
        await AssertReadableAsync(session, "/producer.bin", payload);
        Assert.AreEqual(2, _server.RetrCounts["/producer.bin"]);
        Assert.AreEqual(1, _server.AcceptedConnections);
        Assert.IsTrue(_server.SizeRequests.All(request => request.Binary));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    public async Task DownloadStream_ShortDataEvenWith226_FailsLengthAndDiscardsLease(int delivered)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), Payload(42));
        _server.ReadFaultForPath = _ => new() { DataLimit = delivered };
        Guid session = await OpenAsync(maxConnections: 1);
        using var destination = new MemoryStream();
        RemoteTransferLengthException error = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(
            () => _service.DownloadStreamAsync(session, "/producer.bin", destination));
        long expectedBytes = Payload(42).Length;
        Assert.AreEqual(expectedBytes, error.ExpectedBytes);
        Assert.AreEqual(delivered, error.ActualBytes);
        Assert.IsTrue(destination.CanWrite);
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/producer.bin", Payload(42));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow("426 Transfer aborted", false)]
    [DataRow("451 Local error", false)]
    [DataRow("150 Not a completion", false)]
    [DataRow(null, false)]
    [DataRow(null, true)]
    public async Task DownloadStream_InvalidCompletion_FailsAndDiscardsLease(string? reply, bool closeControl)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), Payload(42));
        _server.ReadFaultForPath = _ => new() { Reply = reply, CloseControl = closeControl };
        Guid session = await OpenAsync(maxConnections: 1);
        await SetProducerTimeoutAsync(session);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<VelaFtpClientException>(() => _service
            .DownloadStreamAsync(session, "/producer.bin", destination).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(destination.CanWrite);
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/producer.bin", Payload(42));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadStream_CancellationDuringDataOrCompletion_DiscardsLease(bool completion)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), Payload(42));
        var fault = new LoopbackFtpServer.ReadFault { HoldData = !completion, HoldReply = completion };
        _server.ReadFaultForPath = _ => fault;
        Guid session = await OpenAsync(maxConnections: 1);
        using var cancellation = new CancellationTokenSource();
        using var destination = new MemoryStream();
        Task download = _service.DownloadStreamAsync(session, "/producer.bin", destination,
            cancellationToken: cancellation.Token);
        try
        {
            await (completion ? fault.DataSent.Task : fault.DataOpened.Task).WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(destination.CanWrite);
        }
        finally
        {
            fault.AllowData.TrySetResult();
            fault.AllowReply.TrySetResult();
        }
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/producer.bin", Payload(42));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DownloadStream_UnknownLengthAndNonSeekableDestination_ReadsToEof(bool empty, bool nonSeekable)
    {
        byte[] payload = empty ? [] : Payload(99);
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), payload);
        _server.SizeReplyForPath = (_, _) => "550 Size unavailable";
        _server.ReadFaultForPath = _ => new() { Reply = "250 Transfer complete" };
        Guid session = await OpenAsync(maxConnections: 1);
        using var storage = new MemoryStream();
        Stream destination = nonSeekable ? new SequentialDestination(storage) : storage;
        await _service.DownloadStreamAsync(session, "/producer.bin", destination);
        Assert.AreSequenceEqual(payload, storage.ToArray());
        Assert.IsTrue(destination.CanWrite);
        await AssertReadableAsync(session, "/producer.bin", payload);
        Assert.AreEqual(1, _server.AcceptedConnections);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    public async Task DownloadStream_UnderreportedSize_RejectsExcessData(int expectedBytes)
    {
        byte[] payload = Payload(42);
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), payload);
        _server.SizeReplyForPath = (_, _) => $"213 {expectedBytes}";
        Guid session = await OpenAsync(maxConnections: 1);
        using var destination = new MemoryStream();
        RemoteTransferLengthException error = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() => _service
            .DownloadStreamAsync(session, "/producer.bin", destination));
        Assert.AreEqual(expectedBytes, error.ExpectedBytes);
        Assert.AreEqual(payload.Length, error.ActualBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadStream_BusyCompletion_ReplaysOnlySeekableDestination(bool nonSeekable)
    {
        byte[] payload = Payload(42);
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), payload);
        _server.ReadFaultForPath = _ => _server.RetrCounts["/producer.bin"] == 1
            ? new() { Reply = "450 Transfer busy" } : null;
        Guid session = await OpenAsync(maxConnections: 1);
        using var storage = new MemoryStream();
        storage.Write([11, 22, 33]); // caller-owned prefix must not be overwritten by a whole-file retry
        Stream destination = nonSeekable ? new SequentialDestination(storage) : storage;
        Task download = _service.DownloadStreamAsync(session, "/producer.bin", destination);
        if (nonSeekable) await Assert.ThrowsAsync<VelaFtpClientException>(() => download);
        else await download;
        byte[] expected = [11, 22, 33, .. payload];
        Assert.AreSequenceEqual(expected, storage.ToArray());
        Assert.AreEqual(nonSeekable ? 1 : 2, _server.RetrCounts["/producer.bin"]);
        Assert.IsTrue(destination.CanWrite);
    }

    [TestMethod]
    public async Task DownloadStream_BusyBeforeData_RetriesWithoutAppendingAndHasBoundedBudget()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), Payload(42));
        _server.ReadFaultForPath = _ => new() { RejectRetr = "450 Transfer busy" };
        Guid session = await OpenAsync(maxConnections: 1);
        using var storage = new MemoryStream();
        await Assert.ThrowsAsync<VelaFtpClientException>(() => _service
            .DownloadStreamAsync(session, "/producer.bin", new SequentialDestination(storage)));
        Assert.AreEqual(3, _server.RetrCounts["/producer.bin"]);
        Assert.AreEqual(0, storage.Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadStream_DataResetOrEof_RemainsAnIntegrityFailure(bool reset)
    {
        byte[] payload = Payload(42);
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), payload);
        _server.ReadFaultForPath = _ => new() { DataLimit = 7, ResetData = reset, Reply = "426 Interrupted" };
        Guid session = await OpenAsync(maxConnections: 1);
        using var storage = new MemoryStream();
        RemoteTransferLengthException error = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() => _service
            .DownloadStreamAsync(session, "/producer.bin", new SequentialDestination(storage)));
        long expectedBytes = payload.Length;
        Assert.AreEqual(expectedBytes, error.ExpectedBytes);
        Assert.AreEqual(7L, error.ActualBytes);
        Assert.IsTrue(storage.CanWrite, "A remote failure must not close the caller-owned destination.");
        Assert.AreEqual(1, _server.RetrCounts["/producer.bin"]);
        Assert.AreEqual(7, storage.Length);
        Assert.IsEmpty(_server.RestartOffsets);
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/producer.bin", payload);
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    public async Task DownloadStream_LocalIOException_IsPreservedAndInternalRestRetriesAreBounded()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), Payload(42));
        // Deterministic IOException fault, not a claim that the user's remote server sent this fault.
        // 55.1 catches sink IOException in the same block as data reads and attempts REST.
        _server.ReadFaultForPath = _ => new() { Reply = "426 Interrupted" };
        Guid session = await OpenAsync(maxConnections: 1);
        var expected = new IOException("Unexpected EOF: injected local write failure");
        using var destination = new FailedDestination(expected);
        IOException actual = await Assert.ThrowsExactlyAsync<IOException>(() => _service
            .DownloadStreamAsync(session, "/producer.bin", destination).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(3, _server.RetrCounts["/producer.bin"]);
        Assert.AreEqual(1, destination.Writes, "A failed sink must not be called again by internal retries.");
        Assert.IsTrue(destination.CanWrite);
        _server.ReadFaultForPath = null;
        await AssertReadableAsync(session, "/producer.bin", Payload(42));
        Assert.AreEqual(2, _server.AcceptedConnections);
    }

    [TestMethod]
    public async Task FluentFtp551_KnownLengthShortRead_RestResumesWithoutSeekingDestination()
    {
        byte[] payload = Payload(42);
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), payload);
        _server.ReadFaultForPath = _ => _server.RetrCounts["/producer.bin"] == 1
            ? new() { DataLimit = 7, Reply = "426 Interrupted" } : null;
        using var client = new FluentFTP.AsyncFtpClient("127.0.0.1", "deploy", "secret123", _server.Port);
        await client.Connect(TestContext.CancellationToken);
        using var storage = new MemoryStream();
        bool ok = await client.DownloadStream(new SequentialDestination(storage), "/producer.bin",
            progress: new Progress<FluentFTP.FtpProgress>(), token: TestContext.CancellationToken);
        Assert.IsTrue(ok);
        Assert.AreSequenceEqual(payload, storage.ToArray());
        Assert.AreSequenceEqual([7], _server.RestartOffsets.ToArray());
        Assert.AreEqual(2, _server.RetrCounts["/producer.bin"]);
        Assert.AreEqual("226", client.LastReply.Code);
        Assert.AreEqual("RETR /producer.bin", client.LastReply.Command);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task DownloadStream_2000DistinctFiles_SingleRetrAndCorrectHash(int concurrency)
    {
        const int count = 2000;
        for (int i = 0; i < count; i++)
            await File.WriteAllBytesAsync(Path.Combine(_root, $"p{i}.bin"), RelayPayload(i, true));
        Guid session = await OpenAsync(maxConnections: concurrency);
        await Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = TestContext.CancellationToken },
            async (i, token) =>
            {
                using var storage = new MemoryStream();
                await _service.DownloadStreamAsync(session, $"/p{i}.bin", new SequentialDestination(storage), cancellationToken: token);
                Assert.AreSequenceEqual(SHA256.HashData(RelayPayload(i, true)), SHA256.HashData(storage.ToArray()));
                Assert.AreEqual(1, _server.RetrCounts[$"/p{i}.bin"]);
            });
        Assert.IsLessThanOrEqualTo(concurrency, _server.AcceptedConnections);
        Assert.AreEqual(_server.AcceptedConnections, _server.PassiveListenersCreated,
            "Passive listeners must be bounded by control sessions, not by the number of files.");
        Assert.IsTrue(_server.SizeRequests.All(request => request.Binary));
    }

    [TestMethod]
    public async Task DownloadStream_HeldCompletion_KeepsLeaseWithoutEnablingNoopDaemon()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), Payload(42));
        var fault = new LoopbackFtpServer.ReadFault { HoldReply = true };
        _server.ReadFaultForPath = _ => fault;
        Guid session = await OpenAsync(maxConnections: 1);
        using var storage = new MemoryStream();
        Task download = _service.DownloadStreamAsync(session, "/producer.bin", storage);
        await fault.DataSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string> next = _service.GetWorkingDirectoryAsync(session);
        try
        {
            Assert.IsFalse(download.IsCompleted);
            Assert.IsFalse(next.IsCompleted);
        }
        finally { fault.AllowReply.TrySetResult(); }
        await download.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("/", await next);
        Assert.AreEqual(1, _server.AcceptedConnections);
        var pools = (ConcurrentDictionary<Guid, FtpConnectionPool>)typeof(FtpFileService)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_service)!;
        using FtpConnectionPool.Lease lease = await pools[session].RentAsync(TestContext.CancellationToken);
        Assert.AreEqual(new FluentFTP.FtpConfig().NoopInterval, lease.Client.Config.NoopInterval);
        Assert.IsFalse(lease.Client.Config.Noop);
        Assert.IsNull(lease.Client.Status.NoopDaemonTask);
        Assert.AreEqual(FluentFTP.FtpNavigate.Manual, lease.Client.Config.Navigate);
    }

    [TestMethod]
    public void FluentFtp551_ZeroNoopInterval_IsClampedNotDisabled()
    {
        var config = new FluentFTP.FtpConfig { NoopInterval = 0 };
        Assert.AreEqual(1000, config.NoopInterval, "55.1 setter contradicts its XML comment; zero is not a disable switch.");
        Assert.IsFalse(config.Noop, "Connect must not start the daemon with the default configuration.");
    }

    [TestMethod]
    [DataRow(false, "226 Transfer complete", "RETR /producer.bin")]
    [DataRow(false, "250 Transfer complete", "RETR /producer.bin")]
    [DataRow(false, "150 Not a completion", "RETR /producer.bin")]
    [DataRow(true, "226 Transfer complete", "RETR /producer.bin, after IOException")]
    public async Task FluentFtp551_DownloadReplyCommand_IsAttributedByLibrary(bool shortRead, string reply, string command)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "producer.bin"), Payload(42));
        _server.ReadFaultForPath = _ => new() { DataLimit = shortRead ? 7 : null, Reply = reply };
        using var client = new FluentFTP.AsyncFtpClient("127.0.0.1", "deploy", "secret123", _server.Port);
        await client.Connect(TestContext.CancellationToken);
        using var destination = new MemoryStream();
        bool ok = await client.DownloadStream(destination, "/producer.bin",
            progress: shortRead ? new Progress<FluentFTP.FtpProgress>() : null, token: TestContext.CancellationToken);
        Assert.IsTrue(ok, "Dependency evidence: even preliminary 150 and short data plus 226 can return true.");
        Assert.AreEqual(reply[..3], client.LastReply.Code);
        Assert.AreEqual(command, client.LastReply.Command);
        Assert.AreEqual(shortRead ? 7 : Payload(42).Length, destination.Length);
    }

    /// <summary>
    /// 续传下载传到一半断掉,FluentFTP 自己的 REST 续读又被服务器拒了(<c>426</c>):这一次尝试已经往本地写了字节,
    /// 不能当成「数据连接一开就被拒」在服务内部把整段再跑一遍 —— 以前按 Append 打开、又从同一个 REST 偏移重下,
    /// 同一段字节写两遍,文件比远端长、内容是坏的,行还显示完成。失败要原样交给上层的重试策略;
    /// 上层按本地此刻的长度再续,最终内容与远端逐字节一致。
    /// </summary>
    [TestMethod]
    public async Task DownloadFile_ResumedAndInterruptedMidStream_SurfacesFailureWithoutDuplicatingBytes()
    {
        (byte[] payload, string local) = await PrepareResumedDownloadAsync();
        bool firstCall = true;
        _server.ReadFaultForPath = _ => !firstCall ? null : _server.RetrCounts["/resume.bin"] == 1
            // 从续传点送 7 个字节,复位落在客户端进行中的那次读上,再回 426(与真实服务器的形态一致)。
            ? new() { DataLimit = 7, ResetData = true, ResetDelayMs = 200, Reply = "426 Connection closed; transfer aborted" }
            // FluentFTP 自己从 REST 30007 续读的那一次:服务器直接拒掉。
            : new() { RejectRetr = "426 Connection closed; transfer aborted" };
        Guid session = await OpenAsync(maxConnections: 1);
        var progress = new Progress<TransferProgress>();
        try
        {
            await Assert.ThrowsAsync<VelaFtpClientException>(() => _service.DownloadFileAsync(
                session, "/resume.bin", local, progress, resumeOffset: ResumedPartial, TestContext.CancellationToken));
            long onDisk = new FileInfo(local).Length;
            Assert.IsLessThanOrEqualTo(payload.Length, onDisk, "失败的那一次不能让本地文件比远端还长。");

            // 上层的自动重试:按本地此刻的长度接着续。
            firstCall = false;
            await _service.DownloadFileAsync(session, "/resume.bin", local, progress, resumeOffset: onDisk, TestContext.CancellationToken);
            Assert.AreSequenceEqual(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(local, TestContext.CancellationToken)));
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>
    /// 同样传到一半,这回 REST 续读被「忙」顶回来:服务内部照常收成单连接再来一次,
    /// 但再来的那一次必须从这次调用的起点重写,不能接着上一次留下的尾巴再追加同一段。
    /// </summary>
    [TestMethod]
    public async Task DownloadFile_ResumedAndBusyAfterPartialData_ReplaysFromTheStartPointNotTheFileEnd()
    {
        (byte[] payload, string local) = await PrepareResumedDownloadAsync();
        _server.ReadFaultForPath = _ => _server.RetrCounts["/resume.bin"] switch
        {
            1 => new() { DataLimit = 7, ResetData = true, ResetDelayMs = 200, Reply = "426 Connection closed; transfer aborted" },
            2 => new() { RejectRetr = "450 Transfer busy, try again later" },
            _ => null,
        };
        Guid session = await OpenAsync(maxConnections: 1);
        try
        {
            await _service.DownloadFileAsync(session, "/resume.bin", local, new Progress<TransferProgress>(),
                resumeOffset: ResumedPartial, TestContext.CancellationToken);
            Assert.AreSequenceEqual(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(local, TestContext.CancellationToken)));
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>
    /// 从一条只能顺序读的源(插件源中转到 FTP 目标)上传,传到一半数据连接被掐断:这是一次断线,不是「数据连接一开就被拒」。
    /// 以前服务内部照「被拒」去重放,重放不了又改报一个「源流不能重放」,真正的原因(连接被复位)丢了;
    /// 现在原样交给上层的重试策略,也不在服务内部再发一次 STOR。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UploadStream_ResetMidStream_SurfacesTheOriginalFailureWithoutReplaying(bool seekable)
    {
        byte[] payload = new byte[4 * 1024 * 1024];
        new Random(11).NextBytes(payload);
        int armed = 1;
        _server.ResetStoreAfterBytes = _ => Interlocked.Exchange(ref armed, 0) == 1 ? 64 * 1024 : null;
        Guid session = await OpenAsync(maxConnections: 1);
        // FluentFTP 自己的 ResumeUploadAsync 失败后还要等一轮控制连接读超时;压短它,别让用例空等 30 秒。
        await SetProducerTimeoutAsync(session);
        using var storage = new MemoryStream(payload);
        Stream source = seekable ? storage : new SequentialSource(storage);
        VelaFtpClientException error = await Assert.ThrowsAsync<VelaFtpClientException>(() => _service.UploadStreamAsync(
            session, source, "/streamed.bin", payload.Length, cancellationToken: TestContext.CancellationToken));
        Assert.DoesNotContain("cannot be replayed", error.Message, error.ToString());
        Assert.IsTrue(error.ToString().Contains(nameof(System.Net.Sockets.SocketException), StringComparison.Ordinal),
            "真正的原因(数据连接被复位)要留在异常链里。" + error);
        // 原来那一次 STOR,加上 FluentFTP 自己就地续写的那一次 APPE;服务内部不再整段重放。
        Assert.IsLessThanOrEqualTo(2, _server.StoreCounts.GetValueOrDefault("/streamed.bin"), "传到一半的失败不该在服务内部再整段重传一遍。");
    }

    private sealed class SequentialSource(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 连接本身就开不出来(服务没了,拨号被复位):数据连接还没开过,谈不上「数据连接被拒」。
    /// 以前异常链里带着 SocketException 就被当成被拒、在服务内部再拨两回,上层每一次重试都白等三轮连接。
    /// </summary>
    [TestMethod]
    public async Task Download_WhenTheConnectionCannotBeOpened_DialsOncePerCall()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "gone.bin"), [1, 2, 3], TestContext.CancellationToken);
        // 第一次拨号(打开会话)直连;之后的拨号都走一个没人监听的 SOCKS5 端口 —— 拨号被拒,异常链里是 SocketException。
        var dead = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        dead.Start();
        int deadPort = ((System.Net.IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();
        var resolver = new DialCountingResolver(new(Core.Net.ProxyKind.Socks5, "127.0.0.1", deadPort));
        await using var service = new FtpFileService(resolver);
        Guid session = await service.OpenSessionAsync(new FtpConnectionInfo
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            Username = "deploy",
            Password = "secret123",
            Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None },
        });
        // 把池里那条已登录的连接作废,下一次租借只能重新拨号。
        var pools = (ConcurrentDictionary<Guid, FtpConnectionPool>)typeof(FtpFileService)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        using (FtpConnectionPool.Lease lease = await pools[session].RentAsync(TestContext.CancellationToken))
        {
            lease.Invalidate();
        }
        int before = resolver.Dials;
        string local = Path.Combine(Path.GetTempPath(), $"vela-ftp-gone-{Guid.NewGuid():N}.bin");
        try
        {
            await Assert.ThrowsAsync<VelaFtpConnectionException>(() => service.DownloadFileAsync(
                session, "/gone.bin", local, cancellationToken: TestContext.CancellationToken));
            Assert.AreEqual(1, resolver.Dials - before, "连不上就如实报,由上层的重试决定要不要再拨。");
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>头一回给直连,之后都给 <paramref name="later" />;顺带数一共拨了几次号。</summary>
    private sealed class DialCountingResolver(Core.Net.ProxyRoute later) : Core.Net.IProxyResolver
    {
        private int _dials;

        public int Dials => Volatile.Read(ref _dials);

        public Core.Net.ProxyRoute Resolve(string targetHost, int targetPort, string? schemeHint = null) =>
            Interlocked.Increment(ref _dials) == 1 ? Core.Net.ProxyRoute.Direct : later;
    }

    private const int ResumedPartial = 30_000;

    /// <summary>远端放一份 100 000 字节的文件,本地留它的前 <see cref="ResumedPartial" /> 字节作半截。</summary>
    private async Task<(byte[] Payload, string LocalPath)> PrepareResumedDownloadAsync()
    {
        byte[] payload = new byte[100_000];
        new Random(7).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_root, "resume.bin"), payload, TestContext.CancellationToken);
        string local = Path.Combine(Path.GetTempPath(), $"vela-ftp-resume-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(local, payload[..ResumedPartial], TestContext.CancellationToken);
        return (payload, local);
    }

    private sealed class FailedDestination(IOException failure) : MemoryStream
    {
        public int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.FromException(failure);
        }
    }

    private sealed class SequentialDestination(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private async Task SetProducerTimeoutAsync(Guid session)
    {
        var pools = (ConcurrentDictionary<Guid, FtpConnectionPool>)typeof(FtpFileService)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_service)!;
        using FtpConnectionPool.Lease lease = await pools[session].RentAsync(TestContext.CancellationToken);
        lease.Client.Config.ReadTimeout = 250;
    }

    private static byte[] RelayPayload(int index, bool mixedMetadata) => !mixedMetadata ? Payload(index) : (index % 4) switch
    {
        0 => [],
        1 => [(byte)index],
        2 => Encoding.UTF8.GetBytes($"file-{index}\n" + new string('t', index % 2048) + "\nend\n"),
        _ => Payload(index),
    };

    private static byte[] Payload(int index)
    {
        byte[] bytes = new byte[64 + index % 1024];
        new Random(index).NextBytes(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(), index);
        return bytes;
    }

    private async Task AssertReadableAsync(Guid session, string path, byte[] expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using Stream stream = await _service.OpenReadAsync(session, path, timeout.Token);
        using var target = new MemoryStream();
        await stream.CopyToAsync(target, timeout.Token);
        Assert.AreSequenceEqual(expected, target.ToArray());
    }

    /// <summary>MSTest 注入的测试上下文(取消令牌)。</summary>
    public TestContext TestContext { get; set; } = null!;

    private Task<Guid> OpenAsync(int maxConnections = 2, bool anonymous = false) =>
        _service.OpenSessionAsync(new FtpConnectionInfo
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            Username = anonymous ? string.Empty : "deploy",
            Password = anonymous ? null : "secret123",
            Settings = new FtpSettings
            {
                EncryptionMode = FtpEncryptionMode.None,
                Anonymous = anonymous,
                MaxConnections = maxConnections,
            },
        });
}
