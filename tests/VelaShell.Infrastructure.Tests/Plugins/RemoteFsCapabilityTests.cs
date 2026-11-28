using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件远程文件能力的进度桥(#617):满格那一帧以传输任务的结果为准 —— 只发一次,
/// 而且在上传真正成功(fsync / CLOSE 之后)才发;失败了就不发。
/// </summary>
[TestClass]
public sealed class RemoteFsCapabilityTests
{
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly ISftpService _sftp = Substitute.For<ISftpService>();
    private readonly ISshConnectionService _connections = Substitute.For<ISshConnectionService>();

    public RemoteFsCapabilityTests() =>
        _connections.GetSession(_sessionId).Returns(new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new() { Host = "files.example.com", Username = "joe", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected,
        });

    /// <summary>
    /// 宿主条目上的链接字段随 <see cref="IRemoteFsApi" /> 交给插件(SDK 2.1.3 的 init 属性):AI 插件这类调用方
    /// 据此知道删除、递归只该处理链接本身。非链接不带目标文本。
    /// </summary>
    [TestMethod]
    public async Task ListAndStat_CarrySymbolicLinkFieldsToPlugins()
    {
        RemoteFileInfo link = new()
        {
            Name = "current",
            FullPath = "/srv/current",
            Size = 0,
            Permissions = "lrwxrwxrwx",
            IsDirectory = true,
            IsSymbolicLink = true,
            LinkTarget = "releases/42",
            LastModified = DateTime.UnixEpoch,
            Owner = "joe",
            Group = "joe"
        };
        RemoteFileInfo file = new()
        {
            Name = "a.txt",
            FullPath = "/srv/a.txt",
            Size = 3,
            Permissions = "-rw-r--r--",
            IsDirectory = false,
            LinkTarget = "stale",
            LastModified = DateTime.UnixEpoch,
            Owner = "joe",
            Group = "joe"
        };
        _sftp.ListDirectoryAsync(_sessionId, "/srv", Arg.Any<CancellationToken>()).Returns([link, file]);
        _sftp.GetFileInfoAsync(_sessionId, "/srv/current", Arg.Any<CancellationToken>()).Returns(link);
        var api = new RemoteFsCapability(_sftp, _connections);

        IReadOnlyList<RemoteFileEntry> entries = await api.ListDirectoryAsync(_sessionId.ToString(), "/srv");
        RemoteFileEntry? stat = await api.StatAsync(_sessionId.ToString(), "/srv/current");

        Assert.IsTrue(entries[0].IsSymbolicLink);
        Assert.AreEqual("releases/42", entries[0].LinkTarget);
        Assert.IsFalse(entries[1].IsSymbolicLink);
        Assert.IsNull(entries[1].LinkTarget);
        Assert.IsTrue(stat!.IsSymbolicLink);
        Assert.AreEqual("releases/42", stat.LinkTarget);
    }

    /// <summary>
    /// 宿主上传在最后一批确认到齐时就报满格(那时 fsync / CLOSE 还没做),成功收尾时可能再报一遍;
    /// 插件拿满格当「完成」,曾经因此收到两次完成、而且早于真正的结果。
    /// </summary>
    [TestMethod]
    public async Task Upload_ReportsCompletionOnce_AfterTheTransferSucceeds()
    {
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<TransferProgress>? hostProgress = null;
        _sftp.UploadFileAsync(_sessionId, "local.bin", "/remote.bin", Arg.Any<IProgress<TransferProgress>?>(), 0, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                hostProgress = call.ArgAt<IProgress<TransferProgress>?>(3);
                hostProgress!.Report(Frame(40));
                hostProgress.Report(Frame(100)); // 最后一批确认到齐,还要等 fsync / CLOSE。
                return closing.Task;
            });
        var plugin = new Recorder();

        Task upload = StartUpload(plugin);
        Assert.IsFalse(upload.IsCompleted);
        Assert.AreEqual(new RemoteTransferProgress(40, 100), plugin.Frames.Single(), "中间帧照常转发");

        hostProgress!.Report(Frame(100)); // 宿主成功收尾时那一帧。
        closing.SetResult();
        await upload;

        Assert.AreSequenceEqual([new RemoteTransferProgress(40, 100), new RemoteTransferProgress(100, 100)], [.. plugin.Frames],
            "满格只在任务成功后报一次");
    }

    /// <summary>CLOSE / fsync 失败:字节虽然全确认了,插件也不能收到满格。</summary>
    [TestMethod]
    public async Task Upload_WhenCloseFails_NeverReportsCompletion()
    {
        _sftp.UploadFileAsync(_sessionId, "local.bin", "/remote.bin", Arg.Any<IProgress<TransferProgress>?>(), 0, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<IProgress<TransferProgress>?>(3)!.Report(Frame(100));
                return Task.FromException(new IOException("close failed"));
            });
        var plugin = new Recorder();

        await Assert.ThrowsExactlyAsync<IOException>(() => StartUpload(plugin));

        Assert.DoesNotContain(static f => f.TransferredBytes >= f.TotalBytes, plugin.Frames);
    }

    private Task StartUpload(IProgress<RemoteTransferProgress> progress)
    {
        // Progress<T> 在创建时捕获上下文。同步投递让断言只依赖传输状态,不依赖线程池调度。
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            return new RemoteFsCapability(_sftp, _connections)
                .UploadFileAsync(_sessionId.ToString(), "local.bin", "/remote.bin", progress);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private static TransferProgress Frame(long bytes) => new()
    {
        FileName = "remote.bin",
        BytesTransferred = bytes,
        TotalBytes = 100,
        Percentage = (int)bytes,
        SpeedBytesPerSecond = 0,
        EstimatedTimeRemaining = TimeSpan.Zero,
    };

    /// <summary>同步记下插件收到的每一帧(线程安全:桥可能在别的线程上转发)。</summary>
    private sealed class Recorder : IProgress<RemoteTransferProgress>
    {
        private readonly List<RemoteTransferProgress> _frames = [];

        public List<RemoteTransferProgress> Frames
        {
            get
            {
                lock (_frames)
                {
                    return [.. _frames];
                }
            }
        }

        public void Report(RemoteTransferProgress value)
        {
            lock (_frames)
            {
                _frames.Add(value);
            }
        }
    }
}
