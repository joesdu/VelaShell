using System.Collections.Concurrent;
using System.Security.Cryptography;
using NSubstitute;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Infrastructure.Tests.Ftp;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 上传进度只跟着对端的 WRITE 确认走(#617)。<see cref="SftpUploadProgressIntegrationTests" /> 要真 OpenSSH 与账号,
/// CI 上永远跳过;这里换成内存里的 SFTP 服务端,由测试决定每条确认什么时候到 —— 包装层按 <c>DurableLength</c>
/// 采样、源读完后 Flush 期间每 200ms 补报这两件事,每次提交都验。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class SftpUploadAckProgressTests
{
    private const int BlockSize = 32 * 1024;
    private const int Blocks = 8;
    private const int RelayBufferBudget = 4 * 1024 * 1024;
    private const string RemotePath = "/home/joe/ack.bin";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Upload_ProgressAdvancesOnlyWhenWriteAcksArrive()
    {
        await using Harness harness = await Harness.StartAsync();
        byte[] data = RandomNumberGenerator.GetBytes(Blocks * BlockSize);
        using var source = new ObservedSource(data);
        var samples = new ConcurrentQueue<(ulong Bytes, bool AfterEof)>();
        Task upload = harness.Sftp.UploadAsync(source, RemotePath, bytes => samples.Enqueue((bytes, source.Eof.IsCompleted)));

        // 源读完、整份数据都已作为 WRITE 发出,一条确认都没放。
        await source.Eof.WaitAsync(Deadline);
        await EventuallyAsync(() => harness.SftpServer.HeldWriteReplies == Blocks);
        // Flush 等确认期间要按 200ms 补报,不能一直不出声。不按固定时长数拍数:慢 runner 上 500ms 里只排得上一拍
        // (macOS CI 实测),没有补报时这里会一直等到 Deadline 超时失败。
        await EventuallyAsync(() => samples.Count(static s => s.AfterEof) >= 2);
        Assert.IsFalse(upload.IsCompleted, "确认没到,上传不能完成。");
        Assert.IsTrue(samples.All(static s => s.Bytes == 0), "已发出、未确认的字节不能算进度。");

        // 放行三条:进度停在三块,不多不少;这一拍来自 Flush 的采样(源早就读完了)。
        Assert.AreEqual(3, harness.SftpServer.ReleaseHeldWrites(3));
        await EventuallyAsync(() => samples.Any(static s => s.Bytes == 3 * BlockSize));
        await Task.Delay(500);
        Assert.IsFalse(upload.IsCompleted);
        Assert.AreEqual((ulong)(3 * BlockSize), samples.Max(static s => s.Bytes), "进度只能到已确认的那三块。");

        // 全部放行:上传完成,最后一帧是全量,全程单调不回退。
        Assert.AreEqual(Blocks - 3, harness.SftpServer.ReleaseHeldWrites());
        await upload.WaitAsync(Deadline);
        ulong[] reported = [.. samples.Select(static s => s.Bytes)];
        Assert.AreEqual((ulong)data.Length, reported[^1]);
        for (int i = 1; i < reported.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(reported[i - 1], reported[i], "进度不能回退。");
        }
        Assert.AreSequenceEqual(data, [.. harness.SftpServer.Nodes[RemotePath].Content]);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(17)]
    [DataRow(BlockSize)]
    public async Task Upload_SourceFailureIsNotReplacedByCloseRefusal(int prefixLength)
    {
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions
        {
            FailCloseWith = SftpStatusCode.PermissionDenied,
        });
        var expected = new IOException("Controlled source read failure.");
        using var source = new FaultingSource(RandomNumberGenerator.GetBytes(prefixLength), expected);
        IOException actual = await Assert.ThrowsExactlyAsync<IOException>(
            () => harness.Sftp.UploadAsync(source, RemotePath).WaitAsync(Deadline));
        Assert.AreSame(expected, actual);
        var details = VelaSftpUploadFailureDetails.Get(actual);
        Assert.IsNotNull(details);
        Assert.AreEqual(prefixLength, details.DurableLength, "Sample after close flushes even a buffered short tail.");
        Assert.IsInstanceOfType<VelaSftpPermissionDeniedException>(details.CleanupException);
        Assert.AreEqual(TransferFailureKind.Transient, TransferRetryPolicy.Classify(actual));
        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount, "Source failure must still return the target handle.");
    }

    [TestMethod]
    public async Task Upload_TranslatedSourceFailureKeepsDetailsThroughOuterGuard()
    {
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions
        {
            FailCloseWith = SftpStatusCode.PermissionDenied,
        });
        var primary = new VelaShell.Ssh.Diagnostics.SshConnectionClosedException(
            VelaShell.Ssh.Diagnostics.SshFailureReason.ClosedByPeer, VelaShell.Ssh.Diagnostics.SshPhase.Open, "source closed");
        using var source = new FaultingSource(new byte[17], primary);
        VelaSshConnectionException actual = await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(
            () => harness.Sftp.UploadAsync(source, RemotePath).WaitAsync(Deadline));
        Assert.AreSame(primary, actual.InnerException);
        var details = VelaSftpUploadFailureDetails.Get(actual);
        Assert.IsNotNull(details, "The outer GuardedAsync must not translate twice and lose attached details.");
        Assert.AreEqual(17L, details.DurableLength);
        Assert.IsInstanceOfType<VelaSftpPermissionDeniedException>(details.CleanupException);
        Assert.AreEqual(TransferFailureKind.Transient, TransferRetryPolicy.Classify(actual));
    }

    [TestMethod]
    public async Task Upload_SmallFirstWriteWaitsForAck_ThenSucceedsWithoutRetry()
    {
        await using Harness harness = await Harness.StartAsync();
        byte[] data = RandomNumberGenerator.GetBytes(17);
        using var source = new ObservedSource(data);
        var samples = new ConcurrentQueue<ulong>();
        Task upload = harness.Sftp.UploadAsync(source, RemotePath, samples.Enqueue);
        await source.Eof.WaitAsync(Deadline);
        await EventuallyAsync(() => harness.SftpServer.HeldWriteReplies == 1);
        await EventuallyAsync(() => samples.Count >= 2);
        Assert.IsFalse(upload.IsCompleted);
        Assert.IsTrue(samples.All(bytes => bytes == 0));
        Assert.AreEqual(1, harness.SftpServer.WriteCount);
        harness.SftpServer.ReleaseHeldWrites();
        await upload.WaitAsync(Deadline);
        Assert.AreEqual(17UL, samples.Last());
        Assert.AreEqual(1, harness.SftpServer.WriteCount, "One attempt, exactly one first WRITE.");
        Assert.AreSequenceEqual(data, [.. harness.SftpServer.Nodes[RemotePath].Content]);
    }

    [TestMethod]
    public async Task Upload_FirstWriteRefusalRemainsPermanent_WithZeroDurable()
    {
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions
        {
            RejectWritesWith = SftpStatusCode.PermissionDenied,
            FailCloseWith = SftpStatusCode.Failure,
        });
        using var source = new MemoryStream(new byte[17]);
        VelaSftpTransferInterruptedException failure = await Assert.ThrowsExactlyAsync<VelaSftpTransferInterruptedException>(
            () => harness.Sftp.UploadAsync(source, RemotePath).WaitAsync(Deadline));
        Assert.IsTrue(failure.RefusedByServer);
        Assert.AreEqual(0L, failure.DurableLength);
        Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure));
        Assert.AreEqual(1, harness.SftpServer.WriteCount);
        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
    }

    [TestMethod]
    public async Task Upload_CloseOnlyRefusalIsStillAnUploadFailure()
    {
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions
        {
            FailCloseWith = SftpStatusCode.PermissionDenied,
        });
        using var source = new MemoryStream(new byte[17]);
        VelaSftpPermissionDeniedException failure = await Assert.ThrowsExactlyAsync<VelaSftpPermissionDeniedException>(
            () => harness.Sftp.UploadAsync(source, RemotePath).WaitAsync(Deadline));
        Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure));
        Assert.HasCount(17, harness.SftpServer.Nodes[RemotePath].Content);
        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
    }

    [TestMethod]
    public async Task Upload_SourceFailureAndUnacknowledgedWrite_KeepPrimaryAndZeroDurable()
    {
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions { HoldWriteReplies = true });
        var expected = new IOException("Controlled transient source failure.");
        using var source = new FaultingSource(new byte[BlockSize], expected);
        IOException actual = await Assert.ThrowsExactlyAsync<IOException>(
            () => harness.Sftp.UploadAsync(source, RemotePath).WaitAsync(Deadline));
        Assert.AreSame(expected, actual);
        var details = VelaSftpUploadFailureDetails.Get(actual);
        Assert.IsNotNull(details);
        Assert.AreEqual(0L, details.DurableLength);
        Assert.IsInstanceOfType<VelaSftpTransferInterruptedException>(details.CleanupException);
        Assert.AreEqual(TransferFailureKind.Transient, TransferRetryPolicy.Classify(actual));
        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
    }

    [TestMethod]
    public async Task Upload_ServiceRemembersSourceFailureDurable_AndResumesExactly()
    {
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var sessionId = Guid.NewGuid();
        await using SftpService service = CreateService(harness.Sftp, sessionId);
        byte[] data = RandomNumberGenerator.GetBytes(BlockSize + 97);
        var expected = new IOException("Controlled source failure after prefix.");
        using var broken = new FaultingSource(data[..BlockSize], expected);
        IOException failure = await Assert.ThrowsExactlyAsync<IOException>(
            () => service.UploadStreamAsync(sessionId, broken, RemotePath, data.Length));
        Assert.AreSame(expected, failure);
        Assert.AreEqual(BlockSize, VelaSftpUploadFailureDetails.Get(failure)?.DurableLength);
        Assert.IsNull(VelaSftpUploadFailureDetails.Get(failure)?.CleanupException);

        using var resumed = new ObservedSource(data);
        await service.UploadStreamAsync(sessionId, resumed, RemotePath, data.Length, resumeOffset: 1);
        Assert.AreEqual(BlockSize, resumed.LastDataReadOffset,
            "After reading the prefix for verification, upload must seek to the exact hint, not back off the in-flight window.");
        Assert.AreSequenceEqual(SHA256.HashData(data), SHA256.HashData([.. harness.SftpServer.Nodes[RemotePath].Content]));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Upload_MixedSizeBatch_FirstAttemptsSucceedAndHashesMatch(int concurrency)
    {
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var sessionId = Guid.NewGuid();
        await using SftpService service = CreateService(harness.Sftp, sessionId);
        int[] sizes = [0, 1, 17, 1023, BlockSize - 1, BlockSize, BlockSize + 1, 256 * 1024 + 17];
        (string Path, byte[] Data)[] files = [.. Enumerable.Range(0, 2000).Select(i =>
            (Path: $"/home/joe/batch-{i}.bin", Data: RandomNumberGenerator.GetBytes(sizes[i % sizes.Length])))];
        int attempts = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (file, ct) =>
        {
            Interlocked.Increment(ref attempts);
            using var source = new MemoryStream(file.Data, writable: false);
            await service.UploadStreamAsync(sessionId, source, file.Path, file.Data.Length, cancellationToken: ct);
        }).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual(files.Length, attempts, "No automatic retry or second attempt is used by this batch.");
        foreach ((string Path, byte[] Data) file in files)
        {
            using var actual = new MemoryStream();
            await harness.Sftp.DownloadAsync(file.Path, actual);
            Assert.AreEqual(file.Data.Length, actual.Length);
            Assert.AreSequenceEqual(SHA256.HashData(file.Data), SHA256.HashData(actual.ToArray()), file.Path);
        }
        Assert.AreEqual(files.Length * 2, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open),
            "Exactly one upload OPEN and one verification download OPEN per file.");
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(3, false)]
    [DataRow(1, true)]
    [DataRow(3, true)]
    public async Task Relay_FtpToActualSftpWrapper_2000FilesEachRetrievedAndOpenedOnce(int concurrency, bool remoteCommit)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-sftp-wire-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root) { SupportsMlsd = true };
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        int[] sizes = [0, 1, 17, 1023, BlockSize - 1, BlockSize, BlockSize + 1, 256 * 1024 + 17];
        (string Name, byte[] Data)[] files = [.. Enumerable.Range(0, 2000).Select(i =>
            (Name: $"relay-{i}.bin", Data: RandomNumberGenerator.GetBytes(sizes[i % sizes.Length])))];
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            foreach ((string Name, byte[] Data) file in files)
            {
                await File.WriteAllBytesAsync(Path.Combine(root, file.Name), file.Data, deadline.Token);
            }
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = concurrency },
            }, deadline.Token);
            int attempts = 0;
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = deadline.Token,
            }, async (file, ct) =>
            {
                Interlocked.Increment(ref attempts);
                if (remoteCommit)
                {
                    Assert.IsTrue(await RemoteRelay.TryCopyFileWithRemoteCommitAsync(ftp, sourceSession, "/" + file.Name,
                        target, targetSession, "/home/joe/" + file.Name, cancellationToken: ct));
                }
                else
                {
                    await RemoteRelay.CopyFileAsync(ftp, sourceSession, "/" + file.Name,
                        target, targetSession, "/home/joe/" + file.Name, cancellationToken: ct);
                }
            });
            Assert.AreEqual(files.Length, attempts, "No retry loop is involved: every transfer must succeed first time.");
            Assert.HasCount(files.Length, server.RetrCounts);
            foreach ((string Name, byte[] Data) file in files)
            {
                Assert.AreEqual(1, server.RetrCounts["/" + file.Name], "RETR must not be retried.");
                byte[] actual = [.. harness.SftpServer.Nodes["/home/joe/" + file.Name].Content];
                Assert.HasCount(file.Data.Length, actual, file.Name);
                Assert.AreSequenceEqual(SHA256.HashData(file.Data), SHA256.HashData(actual), file.Name);
            }
            Assert.AreEqual(files.Length, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open),
                "The SFTP target must be opened exactly once per file, without truncating retries.");
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Relay_ActualSftpToFtp_StreamsFilesWithOneFtpConnection(int concurrency)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-sftp-ftp-wire-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root) { SupportsMlsd = true, MaxConcurrentSessions = 1 };
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var sourceSession = Guid.NewGuid();
        await using SftpService source = CreateService(harness.Sftp, sourceSession);
        int[] sizes = [0, 1, BlockSize - 1, BlockSize, BlockSize + 1, RelayBufferBudget + 97];
        (string Name, byte[] Data)[] files = [.. sizes.Select((size, i) =>
            (Name: $"reverse-{i}.bin", Data: RandomNumberGenerator.GetBytes(size)))];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            foreach ((string Name, byte[] Data) file in files)
            {
                harness.SftpServer.AddFile("/home/joe/" + file.Name, file.Data, 0x180);
            }
            Guid targetSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                // A legacy profile must not create multiple clients, even for concurrent relays.
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 4 },
            }, deadline.Token);
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = deadline.Token,
            }, async (file, ct) =>
            {
                await RemoteRelay.CopyFileAsync(source, sourceSession, "/home/joe/" + file.Name,
                    ftp, targetSession, "/" + file.Name, cancellationToken: ct);
            });
            foreach ((string Name, byte[] Data) file in files)
            {
                byte[] actual = await File.ReadAllBytesAsync(Path.Combine(root, file.Name), deadline.Token);
                Assert.HasCount(file.Data.Length, actual, file.Name);
                Assert.AreSequenceEqual(SHA256.HashData(file.Data), SHA256.HashData(actual), file.Name);
            }
            Assert.AreEqual(1, server.AcceptedConnections, "Reverse relays must queue on the only FTP client.");
            // Read-only SFTP streams send CLOSE in the background, after DisposeAsync returns.
            await EventuallyAsync(() => harness.SftpServer.OpenHandleCount == 0);
            Assert.AreEqual(files.Length, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open),
                "Each SFTP source must be opened once, without a retry or staging download.");
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteCommit_LargeFtpSource_PublishesOnlyAfterFinalReply(bool failCompletion)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-relay-commit-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root)
        {
            SupportsMlsd = true,
            ReadFaultForPath = _ => new() { Reply = failCompletion ? "426 Transfer failed" : "226 Transfer complete" },
        };
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        byte[] original = [1, 2, 3];
        harness.SftpServer.AddFile(RemotePath, original, 0x180);
        byte[] payload = RandomNumberGenerator.GetBytes(RelayBufferBudget + 97);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), payload, deadline.Token);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            }, deadline.Token);
            Task<bool> copy = RemoteRelay.TryCopyFileWithRemoteCommitAsync(ftp, sourceSession, "/source.bin",
                target, targetSession, RemotePath, cancellationToken: deadline.Token);
            if (failCompletion) await Assert.ThrowsAsync<Exception>(() => copy);
            else Assert.IsTrue(await copy);
            byte[] actual = [.. harness.SftpServer.Nodes[RemotePath].Content];
            Assert.AreSequenceEqual(failCompletion ? original : payload, actual);
            Assert.DoesNotContain(path => path.Contains(".vela-relay-", StringComparison.Ordinal), harness.SftpServer.Nodes.Keys);
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(0, "226 Transfer complete")]
    [DataRow(17, "226 Transfer complete")]
    [DataRow(17, "426 Truncated")]
    public async Task Relay_TruncatedSmallFtpSource_FailsBeforeTargetOpenDespiteConfiguredCloseRefusal(int delivered, string reply)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-sftp-failure-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root)
        {
            SupportsMlsd = true,
            ReadFaultForPath = _ => new() { DataLimit = delivered, Reply = reply },
        };
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions
        {
            FailCloseWith = SftpStatusCode.PermissionDenied,
        });
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), new byte[97]);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            });
            RemoteTransferLengthException failure = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(
                () => RemoteRelay.CopyFileAsync(ftp, sourceSession, "/source.bin", target, targetSession, RemotePath).WaitAsync(Deadline));
            Assert.AreEqual(97L, failure.ExpectedBytes);
            Assert.AreEqual(delivered, failure.ActualBytes);
            Assert.AreEqual(Core.Resources.Strings.Format(
                "SftpSvc_RelayLengthMismatch", "/source.bin", 97, delivered), failure.Message);
            Assert.IsNull(VelaSftpUploadFailureDetails.Get(failure),
                "预读失败未上传,不能生成目标 durable/cleanup 详情,更不能让 CLOSE 拒绝替换长度主错误。");
            Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure),
                "源与元数据对不上不是链路故障:重试几次还是同一份数据,不该再连撞四次。");
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);
            Assert.AreEqual(0, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open));
            Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey(RemotePath));
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Relay_TruncatedLargeFtpSourceAndSftpCloseRefusal_PreserveLengthFailureAfterOpen()
    {
        const int expected = 2 * RelayBufferBudget + 97;
        const int delivered = RelayBufferBudget + 17;
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-sftp-large-failure-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root)
        {
            SupportsMlsd = true,
            ReadFaultForPath = _ => new() { DataLimit = delivered, Reply = "426 Truncated" },
        };
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions { FailCloseWith = SftpStatusCode.PermissionDenied });
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), new byte[expected]);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            });
            RemoteTransferLengthException failure = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() =>
                RemoteRelay.CopyFileAsync(ftp, sourceSession, "/source.bin", target, targetSession, RemotePath).WaitAsync(Deadline));
            Assert.AreEqual(expected, failure.ExpectedBytes);
            Assert.AreEqual(delivered, failure.ActualBytes);
            Assert.AreEqual(Core.Resources.Strings.Format(
                "SftpSvc_RelayLengthMismatch", "/source.bin", expected, delivered), failure.Message);
            var details = VelaSftpUploadFailureDetails.Get(failure);
            Assert.IsNotNull(details, "超过预算的大源应在预读全部完成前进入真实目标上传。");
            Assert.IsTrue(details.DurableLength is > 0 and <= delivered);
            Assert.AreEqual(harness.SftpServer.Nodes[RemotePath].Content.Count, details.DurableLength,
                "成功 WRITE ACK 的持久长度仍需保留,源错误与 CLOSE 错误不能令它归零或虚增。");
            Assert.IsInstanceOfType<VelaSftpPermissionDeniedException>(details.CleanupException);
            Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure),
                "源与元数据对不上重试无益;以前归成「临时」,同一份文件要白撞四次。");
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);
            Assert.ContainsSingle(t => t == SftpMessageType.Open, harness.SftpServer.ReceivedTypes);
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(RelayBufferBudget - 1, false)]
    [DataRow(RelayBufferBudget, false)]
    [DataRow(RelayBufferBudget + 1, false)]
    [DataRow(2 * RelayBufferBudget + 97, false)]
    [DataRow(256 * 1024 + 17, true)]
    public async Task Relay_FtpBufferBoundary_WaitsForCompletionOnlyForKnownSmallFiles(int size, bool unknownSize)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-sftp-boundary-{Guid.NewGuid():N}");
        var fault = new LoopbackFtpServer.ReadFault { HoldReply = true };
        var server = new LoopbackFtpServer(root)
        {
            SupportsMlsd = true,
            ReadFaultForPath = _ => fault,
        };
        if (unknownSize) server.SizeReplyForPath = (_, _) => "550 Size unavailable";
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task? relay = null;
        try
        {
            byte[] data = RandomNumberGenerator.GetBytes(size);
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), data, cancellation.Token);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            }, cancellation.Token);
            int writing = 0;
            relay = RemoteRelay.CopyFileAsync(ftp, sourceSession, "/source.bin", target, targetSession, RemotePath,
                writing: () => Interlocked.Increment(ref writing), cancellationToken: cancellation.Token);
            await fault.DataSent.Task.WaitAsync(cancellation.Token);
            if (!unknownSize && size <= RelayBufferBudget)
            {
                Assert.AreEqual(0, Volatile.Read(ref writing), "小文件必须确认 FTP 最终回复成功后才进入 writing。");
                Assert.AreEqual(0, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open));
                Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey(RemotePath));
            }
            else
            {
                await EventuallyAsync(() => harness.SftpServer.WriteCount > 0);
                Assert.AreEqual(1, Volatile.Read(ref writing), "大文件/未知长度不能等源 Dispose 完成才启动目标。");
            }
            Assert.IsFalse(relay.IsCompleted, "FTP 最终回复没放行,relay 不能报成功。");
            fault.AllowReply.TrySetResult();
            await relay.WaitAsync(cancellation.Token);
            byte[] actual = [.. harness.SftpServer.Nodes[RemotePath].Content];
            Assert.HasCount(data.Length, actual);
            Assert.AreSequenceEqual(SHA256.HashData(data), SHA256.HashData(actual));
            Assert.AreEqual(1, Volatile.Read(ref writing));
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);
            Assert.ContainsSingle(t => t == SftpMessageType.Open, harness.SftpServer.ReceivedTypes);
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            fault.AllowReply.TrySetResult();
            await cancellation.CancelAsync();
            if (relay is not null)
            {
                try { await relay.WaitAsync(Deadline); }
                catch (Exception) { /* 保留测试主体的失败,但先结束传输再拆夹具。 */ }
            }
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("426 Transfer aborted")]
    [DataRow("451 Local error")]
    public async Task Relay_SmallFtpCompletionFailure_NeverOpensActualSftpTarget(string reply)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-sftp-completion-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root)
        {
            SupportsMlsd = true,
            ReadFaultForPath = _ => new() { Reply = reply },
        };
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions { FailCloseWith = SftpStatusCode.PermissionDenied });
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), new byte[97]);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            });
            bool writing = false;
            VelaFtpClientException failure = await Assert.ThrowsAsync<VelaFtpClientException>(() =>
                RemoteRelay.CopyFileAsync(ftp, sourceSession, "/source.bin", target, targetSession, RemotePath,
                    writing: () => writing = true).WaitAsync(Deadline));
            Assert.IsFalse(writing);
            Assert.IsNull(VelaSftpUploadFailureDetails.Get(failure));
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);
            Assert.AreEqual(0, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open));
            Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey(RemotePath));
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    // Staged coverage is deliberately separate from Relay_* (RemoteRelay/OpenRead) above.
    // FTP uses loopback TCP; the actual SSH/SFTP wire stack uses the existing in-memory peer,
    // not OpenSSH. Only session discovery is substituted by CreateService.
    [TestMethod]
    [TestCategory("StagedWire")]
    [DoNotParallelize]
    [DataRow(1)]
    [DataRow(3)]
    public async Task StagedWire_FtpToSftp_2000FilesEachRetrievedAndOpenedOnce(int concurrency)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-staged-wire-batch-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root) { SupportsMlsd = true };
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        int[] sizes = [0, 1, 17, 1023, BlockSize - 1, BlockSize, BlockSize + 1, 256 * 1024 + 17];
        (string Name, byte[] Data)[] files = [.. Enumerable.Range(0, 2000).Select(i =>
            (Name: $"staged-{i}.bin", Data: RandomNumberGenerator.GetBytes(sizes[i % sizes.Length])))];
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            foreach ((string Name, byte[] Data) file in files)
                await File.WriteAllBytesAsync(Path.Combine(root, file.Name), file.Data, deadline.Token);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = concurrency },
            }, deadline.Token);
            int attempts = 0;
            int writing = 0;
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = deadline.Token,
            }, async (file, ct) =>
            {
                await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(ftp, sourceSession, "/" + file.Name, ct);
                Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey("/home/joe/" + file.Name),
                    "Preparation must not create this file on the target.");
                Interlocked.Increment(ref attempts);
                await staged.UploadAsync(target, targetSession, "/home/joe/" + file.Name,
                    writing: () => Interlocked.Increment(ref writing), cancellationToken: ct);
            });
            Assert.AreEqual(files.Length, attempts, "Exactly one explicit upload attempt per prepared cache, no retry loop.");
            Assert.AreEqual(files.Length, writing);
            Assert.HasCount(files.Length, server.RetrCounts);
            foreach ((string Name, byte[] Data) file in files)
            {
                Assert.AreEqual(1, server.RetrCounts["/" + file.Name], file.Name);
                byte[] actual = [.. harness.SftpServer.Nodes["/home/joe/" + file.Name].Content];
                Assert.HasCount(file.Data.Length, actual, file.Name);
                Assert.AreSequenceEqual(SHA256.HashData(file.Data), SHA256.HashData(actual), file.Name);
            }
            Assert.AreEqual(2000, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open),
                "Inspect peer storage without verification OPENs: exactly one wire upload OPEN per file.");
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("StagedWire")]
    [DoNotParallelize]
    public async Task StagedWire_FirstWriteRefusal_ReusesCacheOnReplacementTargetWithoutRetr()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-staged-wire-replacement-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root) { SupportsMlsd = true };
        await using var ftp = new FtpFileService();
        await using Harness rejected = await Harness.StartAsync(new TestSftpOptions
        {
            RejectWritesWith = SftpStatusCode.PermissionDenied,
        });
        await using Harness replacement = await Harness.StartAsync(new TestSftpOptions());
        var rejectedSession = Guid.NewGuid();
        var replacementSession = Guid.NewGuid();
        await using SftpService rejectedTarget = CreateService(rejected.Sftp, rejectedSession);
        await using SftpService replacementTarget = CreateService(replacement.Sftp, replacementSession);
        using var deadline = new CancellationTokenSource(Deadline);
        try
        {
            byte[] data = RandomNumberGenerator.GetBytes(17); // Exactly one first WRITE, refused by the peer.
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), data, deadline.Token);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            }, deadline.Token);
            await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(ftp, sourceSession, "/source.bin", deadline.Token);
            VelaSftpTransferInterruptedException failure = await Assert.ThrowsExactlyAsync<VelaSftpTransferInterruptedException>(
                () => staged.UploadAsync(rejectedTarget, rejectedSession, RemotePath, cancellationToken: deadline.Token));
            Assert.IsTrue(failure.RefusedByServer);
            Assert.AreEqual(0L, failure.DurableLength);
            Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure));
            Assert.AreEqual(1, rejected.SftpServer.WriteCount);
            Assert.IsEmpty(rejected.SftpServer.Nodes[RemotePath].Content);
            Assert.ContainsSingle(t => t == SftpMessageType.Open, rejected.SftpServer.ReceivedTypes);
            Assert.AreEqual(0, rejected.SftpServer.OpenHandleCount);
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);

            // Explicit target replacement, NOT same-session recovery or automatic retry of a permanent error.
            // The first peer keeps rejecting writes. Reuse this exact cache, with a fresh target and offset zero.
            await staged.UploadAsync(replacementTarget, replacementSession, RemotePath,
                resumeOffset: 0, cancellationToken: deadline.Token);
            byte[] actual = [.. replacement.SftpServer.Nodes[RemotePath].Content];
            Assert.HasCount(data.Length, actual);
            Assert.AreSequenceEqual(SHA256.HashData(data), SHA256.HashData(actual));
            Assert.HasCount(1, server.RetrCounts);
            Assert.AreEqual(1, server.RetrCounts["/source.bin"], "Reusing the prepared cache must not download the source again.");
            Assert.AreEqual(1, rejected.SftpServer.WriteCount, "The rejected target is not retried.");
            Assert.AreEqual(1, replacement.SftpServer.WriteCount);
            Assert.ContainsSingle(t => t == SftpMessageType.Open, replacement.SftpServer.ReceivedTypes);
            Assert.AreEqual(0, replacement.SftpServer.OpenHandleCount);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("StagedWire")]
    [DoNotParallelize]
    [DataRow(97, false, "426 Transfer aborted")]
    [DataRow(97, false, "451 Local error")]
    [DataRow(RelayBufferBudget + 97, false, "426 Transfer aborted")]
    [DataRow(RelayBufferBudget + 97, false, "451 Local error")]
    [DataRow(97, true, "426 Transfer aborted")]
    [DataRow(97, true, "451 Local error")]
    public async Task StagedWire_FtpNegativeCompletion_PrepareFailsBeforeTargetOpen(int size, bool unknownSize, string reply)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-staged-wire-completion-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root)
        {
            SupportsMlsd = true,
            ReadFaultForPath = _ => new() { Reply = reply },
        };
        if (unknownSize) server.SizeReplyForPath = (_, _) => "550 Size unavailable";
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), RandomNumberGenerator.GetBytes(size), deadline.Token);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            }, deadline.Token);
            bool prepared = false;
            bool writing = false;
            VelaFtpClientException failure = await Assert.ThrowsAsync<VelaFtpClientException>(async () =>
            {
                await using StagedRelaySource staged = await StagedRelaySource.PrepareAsync(ftp, sourceSession, "/source.bin", deadline.Token);
                prepared = true;
                await staged.UploadAsync(target, targetSession, RemotePath,
                    writing: () => writing = true, cancellationToken: deadline.Token);
            });
            Assert.IsFalse(prepared, "Even with all payload bytes delivered, a negative final reply must fail PrepareAsync.");
            Assert.IsFalse(writing);
            Assert.IsNull(VelaSftpUploadFailureDetails.Get(failure));
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);
            Assert.AreEqual(0, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open));
            Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey(RemotePath));
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("StagedWire")]
    [DoNotParallelize]
    [DataRow(RelayBufferBudget + 97, false)]
    [DataRow(256 * 1024 + 17, true)]
    [DataRow(RelayBufferBudget + 97, true)]
    public async Task StagedWire_LargeOrUnknownSource_WaitsForCompletionThenUploads(int size, bool unknownSize)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-staged-wire-large-{Guid.NewGuid():N}");
        var fault = new LoopbackFtpServer.ReadFault { HoldReply = true };
        var server = new LoopbackFtpServer(root) { SupportsMlsd = true, ReadFaultForPath = _ => fault };
        if (unknownSize) server.SizeReplyForPath = (_, _) => "550 Size unavailable";
        await using var ftp = new FtpFileService();
        await using Harness harness = await Harness.StartAsync(new TestSftpOptions());
        var targetSession = Guid.NewGuid();
        await using SftpService target = CreateService(harness.Sftp, targetSession);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<StagedRelaySource>? preparation = null;
        try
        {
            byte[] data = RandomNumberGenerator.GetBytes(size);
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), data, deadline.Token);
            Guid sourceSession = await ftp.OpenSessionAsync(new FtpConnectionInfo
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Settings = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 1 },
            }, deadline.Token);
            RemoteFileInfo info = await ftp.GetFileInfoAsync(sourceSession, "/source.bin", deadline.Token);
            Assert.AreEqual(unknownSize ? 0L : size, info.Size, "Unknown-size rows must actually reach staging without a known length.");
            preparation = StagedRelaySource.PrepareAsync(ftp, sourceSession, "/source.bin", deadline.Token);
            await fault.DataSent.Task.WaitAsync(deadline.Token);
            Assert.IsFalse(preparation.IsCompleted, "Staging must wait for the FTP final reply, even for large/unknown sources.");
            Assert.AreEqual(0, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Open));
            Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey(RemotePath));
            fault.AllowReply.TrySetResult();
            await using StagedRelaySource staged = await preparation.WaitAsync(deadline.Token);
            int writing = 0;
            await staged.UploadAsync(target, targetSession, RemotePath,
                writing: () => writing++, cancellationToken: deadline.Token);
            byte[] actual = [.. harness.SftpServer.Nodes[RemotePath].Content];
            Assert.HasCount(data.Length, actual);
            Assert.AreSequenceEqual(SHA256.HashData(data), SHA256.HashData(actual));
            Assert.AreEqual(1, writing);
            Assert.AreEqual(1, server.RetrCounts["/source.bin"]);
            Assert.ContainsSingle(t => t == SftpMessageType.Open, harness.SftpServer.ReceivedTypes);
            Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
        }
        finally
        {
            fault.AllowReply.TrySetResult();
            await deadline.CancelAsync();
            if (preparation is not null)
            {
                try
                {
                    // Also dispose a cache produced just as an assertion/cancellation failed.
                    StagedRelaySource staged = await preparation.WaitAsync(Deadline);
                    await staged.DisposeAsync();
                }
                catch (Exception) { /* Preserve the test failure while finishing preparation before fixture teardown. */ }
            }
            await ftp.DisposeAsync();
            server.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static SftpService CreateService(VelaSftpClientWrapper sftp, Guid sessionId)
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetSession(sessionId).Returns(new SshSession
        {
            SessionId = sessionId,
            Status = SessionStatus.Connected,
            ConnectionInfo = new() { Host = "test.invalid", Username = "joe", AuthMethod = AuthMethod.Password },
        });
        // Only session discovery is substituted; all target file operations use the actual wrapper and wire peer.
        return new SftpService(connections, _ => sftp);
    }

    private sealed class FaultingSource(byte[] prefix, Exception failure) : MemoryStream(prefix, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Position == Length ? ValueTask.FromException<int>(failure) : base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>记下源什么时候读到结尾:之后的回调都来自 Flush 的定时采样。</summary>
    private sealed class ObservedSource(byte[] data) : MemoryStream(data, writable: false)
    {
        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Eof => _eof.Task;
        public long LastDataReadOffset { get; private set; } = -1;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = Position;
            int read = Read(buffer.Span);
            if (read > 0)
            {
                LastDataReadOffset = offset;
            }
            if (read == 0)
            {
                _eof.TrySetResult();
            }
            return ValueTask.FromResult(read);
        }
    }

    /// <summary>握手 → 认证 → 通道 → SFTP 全在内存里跑;服务端扣住每个 WRITE 的确认。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly TestChannelServer _channelServer;
        private readonly Task _serverChannels;
        private readonly SshConnection _connection;
        private readonly CancellationTokenSource _cts;

        private Harness(TestSshServer server, TestChannelServer channelServer, Task serverChannels,
            SshConnection connection, TestSftpServer sftpServer, VelaSftpClientWrapper sftp, CancellationTokenSource cts)
        {
            _server = server;
            _channelServer = channelServer;
            _serverChannels = serverChannels;
            _connection = connection;
            SftpServer = sftpServer;
            Sftp = sftp;
            _cts = cts;
        }

        public TestSftpServer SftpServer { get; }

        public VelaSftpClientWrapper Sftp { get; }

        public static async Task<Harness> StartAsync(TestSftpOptions? options = null)
        {
            (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
            TestSshServer server = new(serverStream);
            SshPacketTransport clientTransport = new(clientStream);
            CancellationTokenSource cts = new(TimeSpan.FromMinutes(4));

            Task<TestSshServerHandshake> serverHandshake = server.HandshakeAsync(cts.Token);
            SshVersionExchangeResult versions = await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);
            SshKeyExchangeRunner runner = new(clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
            SshKeyExchangeResult kex = await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
            TestSshServerHandshake handshake = await serverHandshake;

            TestAuthServer authServer = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);
            SshAuthenticator authenticator = new(clientTransport, "joe", kex.SessionId);
            await authenticator.AuthenticateAsync([new PasswordCredential("hunter2")], cts.Token);
            await serverAuth;

            TestSftpServer sftpServer = new(options ?? new TestSftpOptions { HoldWriteReplies = true });
            TestChannelServer channelServer = new(server.Transport, new TestChannelScript { SubsystemHandler = sftpServer.RunAsync });
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex);
            connection.Start();
            // 与真 OpenSSH 那组用例同一套参数:固定 32 KiB 一块、不自适应,整份数据能一次全部在途。
            var sftp = new VelaSftpClientWrapper(ct => SftpFileSystem.ConnectAsync(connection, new SftpOptions
            {
                BlockSize = BlockSize,
                MaxInFlight = 64,
                IsPipelineDepthAdaptive = false,
                CloseTimeout = TimeSpan.FromSeconds(3),
            }, ct));
            await sftp.ConnectAsync(cts.Token);
            return new Harness(server, channelServer, serverChannels, connection, sftpServer, sftp, cts);
        }

        public async ValueTask DisposeAsync()
        {
            // 用例中途失败时服务端还扣着确认:先全放掉,关流才不会等满 CloseTimeout。
            SftpServer.ReleaseHeldWrites();
            await Sftp.DisposeAsync();
            await _cts.CancelAsync();
            await _connection.DisposeAsync();
            try
            {
                await _serverChannels;
            }
            catch (Exception)
            {
                // 收尾时被取消是预期的。
            }
            _channelServer.Dispose();
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }
}
