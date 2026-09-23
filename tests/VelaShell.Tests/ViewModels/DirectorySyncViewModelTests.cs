using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 同步窗口的端到端行为:比较 → 预览 → 同步 → 复查,SHA-256 优先与回退,以及「保持远端目录最新」。
/// 本地是真实的临时目录,远端是一个会随上传/删除/改时间一起变化、并记着文件内容的内存目录树 ——
/// 复查那一步要看到的正是这些变化,静态的替身验不出「同步完了到底对齐没有」。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class DirectorySyncViewModelTests
{
    private const string RemoteRoot = "/srv";
    private readonly Guid _session = Guid.NewGuid();
    private string _local = string.Empty;
    private FakeRemote _remote = null!;
    private FileBrowserViewModel _browser = null!;

    [TestInitialize]
    public void Setup()
    {
        _local = Path.Combine(Path.GetTempPath(), $"vela-sync-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_local);
        _remote = new();
        _remote.AddDirectory(RemoteRoot);
        _browser = new(_remote.Service, _session)
        {
            // 不写传输日志、不响提示音:测试不该往用户目录里落文件。
            TransferOptions = new() { TransferLogging = false, NotifyOnComplete = false },
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_local, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    [TestMethod]
    public async Task CompareThenSynchronize_UploadsDeletesAlignsTimes_AndTheRecheckFindsNothingLeft()
    {
        DateTime localNew = WriteLocal("new.txt", "brand new", DateTime.UtcNow.AddHours(-1));
        WriteLocal("changed.txt", "edited locally", DateTime.UtcNow.AddMinutes(-10));
        _remote.AddFile(RemoteRoot + "/changed.txt", "old", DateTime.UtcNow.AddHours(-2));
        _remote.AddFile(RemoteRoot + "/extra.txt", "extra", DateTime.UtcNow.AddHours(-2));
        var confirmations = new List<string>();
        DirectorySyncViewModel vm = CreateViewModel(new() { DeleteExtraneous = true });
        vm.ConfirmAsync = message =>
        {
            confirmations.Add(message);
            return Task.FromResult(true);
        };

        await vm.CompareCommand.Execute().FirstAsync();

        Assert.IsNull(vm.ErrorText, vm.ErrorText);
        Assert.AreSequenceEqual(
            ["changed.txt:Upload", "extra.txt:DeleteRemote", "new.txt:Upload"],
            [.. vm.Items.Select(i => $"{i.Action.RelativePath}:{i.Action.Kind}")],
            SequenceOrder.InAnyOrder);

        await vm.SynchronizeCommand.Execute().FirstAsync();

        Assert.HasCount(1, confirmations, "有删除时必须先确认");
        Assert.AreSequenceEqual([RemoteRoot + "/changed.txt", RemoteRoot + "/new.txt"], [.. _remote.Uploads], SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([RemoteRoot + "/extra.txt"], [.. _remote.Deletes]);
        Assert.AreEqual(localNew, _remote.TimesSet[RemoteRoot + "/new.txt"], "上传后远端时间必须对齐本地,否则下次比较会判成远端较新");
        Assert.IsFalse(vm.HasItems, "复查后不该还有剩余操作");
        Assert.AreEqual(Strings.Format("Sync_DoneVerified", 3, 0), vm.StatusText);
    }

    [TestMethod]
    public async Task UncheckedRows_AreNotExecuted()
    {
        WriteLocal("keep.txt", "a", DateTime.UtcNow.AddHours(-1));
        WriteLocal("skip.txt", "b", DateTime.UtcNow.AddHours(-1));
        DirectorySyncViewModel vm = CreateViewModel(new());
        await vm.CompareCommand.Execute().FirstAsync();

        vm.Items.Single(i => i.Action.RelativePath == "skip.txt").IsChecked = false;
        await vm.SynchronizeCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual([RemoteRoot + "/keep.txt"], [.. _remote.Uploads]);
        Assert.AreEqual(Strings.Format("Sync_DoneRemaining", 1, 0, 1), vm.StatusText, "没勾的那一项复查时仍然待同步");
    }

    [TestMethod]
    public async Task ToLocal_DownloadsIntoNewFolders_AndStampsTheRemoteTime()
    {
        var remoteTime = new DateTime(2026, 8, 1, 9, 15, 42, DateTimeKind.Utc);
        _remote.AddDirectory(RemoteRoot + "/logs");
        _remote.AddFile(RemoteRoot + "/logs/app.log", "log line", remoteTime);
        DirectorySyncViewModel vm = CreateViewModel(new() { Direction = SyncDirection.ToLocal });

        await vm.CompareCommand.Execute().FirstAsync();
        Assert.AreSequenceEqual(
            ["logs:CreateLocalDirectory", "logs/app.log:Download"],
            [.. vm.Items.Select(i => $"{i.Action.RelativePath}:{i.Action.Kind}")]);
        await vm.SynchronizeCommand.Execute().FirstAsync();

        string downloaded = Path.Combine(_local, "logs", "app.log");
        Assert.AreEqual("log line", await File.ReadAllTextAsync(downloaded));
        Assert.AreEqual(remoteTime, File.GetLastWriteTimeUtc(downloaded));
        Assert.IsFalse(vm.HasItems);
    }

    [TestMethod]
    public async Task Checksum_IdenticalContentWithADifferentTime_IsNotTransferred()
    {
        WriteLocal("a.txt", "same bytes", DateTime.UtcNow.AddHours(-1));
        _remote.AddFile(RemoteRoot + "/a.txt", "same bytes", DateTime.UtcNow.AddHours(-5));
        _remote.SupportsSha256 = true;
        DirectorySyncViewModel vm = CreateViewModel(new());

        await vm.CompareCommand.Execute().FirstAsync();

        Assert.IsFalse(vm.HasItems, "内容相同、只是本地时间较新:不该上传");
        Assert.AreEqual(Strings.Get("Sync_NothingToDo"), vm.StatusText);
        Assert.IsNull(vm.NoteText, vm.NoteText);

        // 对照:关掉校验就退回按时间比较,本地较新 → 上传。
        vm.CompareByChecksum = false;
        await vm.CompareCommand.Execute().FirstAsync();
        Assert.AreSequenceEqual(["a.txt:Upload"], [.. vm.Items.Select(i => $"{i.Action.RelativePath}:{i.Action.Kind}")]);
    }

    [TestMethod]
    public async Task Checksum_SameSizeAndTimeButDifferentContent_IsUploaded()
    {
        var stamp = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        WriteLocal("a.txt", "AAAA", stamp);
        _remote.AddFile(RemoteRoot + "/a.txt", "BBBB", stamp);
        _remote.SupportsSha256 = true;
        DirectorySyncViewModel vm = CreateViewModel(new());

        await vm.CompareCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(
            ["a.txt:Upload"],
            [.. vm.Items.Select(i => $"{i.Action.RelativePath}:{i.Action.Kind}")],
            message: "大小与时间都一样、内容却变了:只有摘要看得出来");
    }

    [TestMethod]
    public async Task Checksum_Unsupported_FallsBackToSizeAndTime_AndSaysSo()
    {
        WriteLocal("a.txt", "same bytes", DateTime.UtcNow.AddHours(-1));
        _remote.AddFile(RemoteRoot + "/a.txt", "same bytes", DateTime.UtcNow.AddHours(-5));
        _remote.SupportsSha256 = false;
        DirectorySyncViewModel vm = CreateViewModel(new());

        await vm.CompareCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(["a.txt:Upload"], [.. vm.Items.Select(i => $"{i.Action.RelativePath}:{i.Action.Kind}")]);
        Assert.Contains(Strings.Format("Sync_NoteChecksumUnsupported", FakeRemote.UnsupportedReason), vm.NoteText);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task RemoteNamesThatAreInvalidLocally_AreRefused_NotWrittenSomewhereElse()
    {
        _remote.AddFile(RemoteRoot + "/bad:name.txt", "x", DateTime.UtcNow);
        DirectorySyncViewModel vm = CreateViewModel(new() { Direction = SyncDirection.ToLocal });

        await vm.CompareCommand.Execute().FirstAsync();
        await vm.SynchronizeCommand.Execute().FirstAsync();

        await _remote.Service.DidNotReceive().DownloadFileAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
            Arg.Any<long>(), Arg.Any<CancellationToken>());
        Assert.Contains("bad:name.txt", vm.ErrorText);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_local));
    }

    [TestMethod]
    public void ChangingAnOption_DiscardsThePreview_AndBothDirectionForcesSafeChoices()
    {
        DirectorySyncViewModel vm = CreateViewModel(new() { Mode = SyncMode.Mirror, DeleteExtraneous = true });
        Assert.IsTrue(vm.CanDeleteExtraneous);

        vm.Direction = SyncDirection.Both;

        Assert.AreEqual(SyncMode.Synchronize, vm.Mode, "双向没有源,镜像无从谈起");
        Assert.IsFalse(vm.CanChooseMode);
        Assert.IsFalse(vm.CanDeleteExtraneous);
    }

    [TestMethod]
    public async Task ChangingAnOptionAfterCompare_ClearsTheStalePlan()
    {
        WriteLocal("a.txt", "a", DateTime.UtcNow.AddHours(-1));
        DirectorySyncViewModel vm = CreateViewModel(new());
        await vm.CompareCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.HasItems);

        vm.FileMask = "*.log";

        Assert.IsFalse(vm.HasItems, "按旧选项算出来的计划不能拿新选项的名义去执行");
        Assert.IsFalse(vm.CanSynchronize);
    }

    [TestMethod]
    public async Task KeepRemoteUpToDate_SyncsOnStart_ThenUploadsNewFoldersAndFiles()
    {
        WriteLocal("existing.txt", "x", DateTime.UtcNow.AddHours(-1));
        Action<string, bool>? onChanged = null;
        bool watcherDisposed = false;
        DirectorySyncViewModel vm = CreateViewModel(new());
        vm.WatchFactory = (_, callback) =>
        {
            onChanged = callback;
            return new Callback(() => watcherDisposed = true);
        };

        await vm.ToggleWatchCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.IsWatching);
        Assert.AreSequenceEqual([RemoteRoot + "/existing.txt"], [.. _remote.Uploads], message: "开始时先完整对齐一次");

        Directory.CreateDirectory(Path.Combine(_local, "sub"));
        WriteLocal("sub/nested.txt", "y", DateTime.UtcNow);
        onChanged!(Path.Combine(_local, "sub"), true);
        onChanged(Path.Combine(_local, "sub", "nested.txt"), true);
        await vm.FlushPendingAsync();

        Assert.Contains(RemoteRoot + "/sub/nested.txt", _remote.Uploads);
        await _remote.Service.Received().EnsureDirectoryAsync(_session, RemoteRoot + "/sub", Arg.Any<CancellationToken>());
        Assert.HasCount(2, _remote.Uploads, "已经对齐的 existing.txt 不该再传一遍");

        await vm.ToggleWatchCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.IsWatching);
        Assert.IsTrue(watcherDisposed);
    }

    [TestMethod]
    public async Task Runner_WhenCancelledBeforeTransfersFinish_NeverDeletes()
    {
        _remote.AddFile(RemoteRoot + "/extra.txt", "x", DateTime.UtcNow);
        var runner = new DirectorySyncRunner(_remote.Service, _session, _browser);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        DirectorySyncResult result = await runner.ExecuteAsync(
            _local,
            RemoteRoot,
            [new SyncAction(SyncActionKind.DeleteRemote, "extra.txt", false, null,
                new SyncItem("extra.txt", RemoteRoot + "/extra.txt", false, 1, DateTime.UtcNow))],
            null,
            cts.Token);

        Assert.IsTrue(result.Cancelled);
        Assert.IsEmpty(_remote.Deletes);
    }

    private DirectorySyncViewModel CreateViewModel(DirectorySyncSettings settings) =>
        new(_remote.Service, _session, _browser, settings, _local, RemoteRoot, inferRemotePrecision: false, "srv");

    private DateTime WriteLocal(string relativePath, string content, DateTime utc)
    {
        string path = Path.Combine(_local, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, utc);
        return File.GetLastWriteTimeUtc(path);
    }

    private sealed class Callback(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private readonly record struct RemoteEntry(bool IsDirectory, byte[] Content, DateTime Utc);

    /// <summary>
    /// 内存里的远端目录树:上传、删除、建目录、改时间都会改动它,列举读的也是它;记着文件内容,
    /// 所以服务器端 SHA-256 算出来的是真摘要。<see cref="SupportsSha256" /> 关着时像一台没有 sha256sum 的主机。
    /// </summary>
    private sealed class FakeRemote
    {
        public const string UnsupportedReason = "no sha256sum on this host";

        private readonly Dictionary<string, RemoteEntry> _entries = [with(StringComparer.Ordinal)];
        private readonly Lock _lock = new();

        public FakeRemote()
        {
            Service = Substitute.For<ISftpService>();
            Service.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(List(call.ArgAt<string>(1))));
            Service.EnsureDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    AddDirectory(call.ArgAt<string>(1));
                    return Task.CompletedTask;
                });
            Service.UploadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    string remote = call.ArgAt<string>(2);
                    byte[] content = File.ReadAllBytes(call.ArgAt<string>(1));
                    lock (_lock)
                    {
                        Uploads.Add(remote);
                        // 与真实服务器一样:刚上传的文件时间是「现在」,要靠随后的改时间才对齐。
                        _entries[remote] = new(false, content, DateTime.UtcNow);
                    }
                    return Task.CompletedTask;
                });
            Service.DownloadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    RemoteEntry entry;
                    lock (_lock)
                    {
                        entry = _entries[call.ArgAt<string>(1)];
                    }
                    File.WriteAllBytes(call.ArgAt<string>(2), entry.Content);
                    return Task.CompletedTask;
                });
            Service.SetLastWriteTimeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    string path = call.ArgAt<string>(1);
                    lock (_lock)
                    {
                        TimesSet[path] = call.ArgAt<DateTime>(2);
                        _entries[path] = _entries[path] with { Utc = call.ArgAt<DateTime>(2) };
                    }
                    return Task.CompletedTask;
                });
            Service.DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    string path = call.ArgAt<string>(1);
                    lock (_lock)
                    {
                        Deletes.Add(path);
                        foreach (string key in _entries.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                        {
                            _entries.Remove(key);
                        }
                    }
                    return Task.CompletedTask;
                });
            Service.ComputeSha256Async(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (!SupportsSha256)
                    {
                        return Task.FromException<IReadOnlyDictionary<string, string?>>(new NotSupportedException(UnsupportedReason));
                    }
                    var result = new Dictionary<string, string?>(StringComparer.Ordinal);
                    lock (_lock)
                    {
                        foreach (string path in call.ArgAt<IReadOnlyList<string>>(1))
                        {
                            result[path] = _entries.TryGetValue(path, out RemoteEntry entry) && !entry.IsDirectory
                                ? Convert.ToHexStringLower(SHA256.HashData(entry.Content))
                                : null;
                        }
                    }
                    return Task.FromResult<IReadOnlyDictionary<string, string?>>(result);
                });
        }

        public ISftpService Service { get; }

        public bool SupportsSha256 { get; set; }

        public List<string> Uploads { get; } = [];

        public List<string> Deletes { get; } = [];

        public Dictionary<string, DateTime> TimesSet { get; } = [with(StringComparer.Ordinal)];

        public void AddDirectory(string path)
        {
            lock (_lock)
            {
                _entries.TryAdd(path, new(true, [], DateTime.UtcNow));
            }
        }

        public void AddFile(string path, string content, DateTime utc)
        {
            lock (_lock)
            {
                _entries[path] = new(false, Encoding.UTF8.GetBytes(content), utc);
            }
        }

        private List<RemoteFileInfo> List(string directory)
        {
            lock (_lock)
            {
                return
                [
                    .. _entries
                        .Where(e => e.Key != directory && Parent(e.Key) == directory)
                        .Select(e => new RemoteFileInfo
                        {
                            Name = e.Key[(e.Key.LastIndexOf('/') + 1)..],
                            FullPath = e.Key,
                            Size = e.Value.Content.Length,
                            IsDirectory = e.Value.IsDirectory,
                            // 与 SFTP 后端一致:给本地时区的时间。
                            LastModified = e.Value.Utc.ToLocalTime(),
                            Permissions = "rw-r--r--",
                            Owner = "u",
                            Group = "g",
                        }),
                ];
            }
        }

        private static string Parent(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path[..slash];
        }
    }
}
