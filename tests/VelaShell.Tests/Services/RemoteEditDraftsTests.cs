using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Sftp;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 外部编辑副本的崩溃兜底(维护者 2026-10-25 定):每个编辑会话落一个状态标记,
/// 启动时干净的 7 天后清、有未传改动的(含没有标记的旧副本)一律当草稿 —— 永不自动删,在消息中心列出来。
/// </summary>
[TestClass]
[TestCategory("ExternalEdit")]
public sealed class RemoteEditDraftsTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now - TimeSpan.FromDays(8);
    private static readonly DateTime Recent = Now - TimeSpan.FromDays(1);

    private string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vela-drafts-{Guid.NewGuid():N}")).FullName;

    [TestCleanup]
    public void RemoveRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>会话一开就是干净的;存一次变脏;传上去变回干净;传不上去收尾时照样是脏的、子目录留着。</summary>
    [TestMethod]
    public async Task Session_KeepsItsMarkerInStep_WithWhatTheRemoteHas()
    {
        string directory = Path.Combine(_root, "0a1b2c3d");
        Directory.CreateDirectory(directory);
        string localPath = Path.Combine(directory, "app.conf");
        await File.WriteAllTextAsync(localPath, "original");
        bool fail = false;
        int uploads = 0;
        var session = new RemoteEditSession(new()
        {
            SftpService = Substitute.For<ISftpService>(),
            SessionId = Guid.NewGuid(),
            RemotePath = "/etc/app.conf",
            FileName = "app.conf",
            Server = "prod-web",
            ProfileId = Guid.Empty,
            UploadAsync = (_, _) =>
            {
                Interlocked.Increment(ref uploads);
                return fail ? Task.FromException(new IOException("link down")) : Task.CompletedTask;
            },
        }, localPath);
        try
        {
            RemoteEditMarker? opened = RemoteEditDrafts.Read(directory);
            Assert.IsNotNull(opened);
            Assert.AreEqual(RemoteEditDrafts.Clean, opened.State);
            Assert.AreEqual("app.conf", opened.FileName);
            Assert.AreEqual("/etc/app.conf", opened.RemotePath);
            Assert.AreEqual("prod-web", opened.Server);
            Assert.AreEqual(new FileInfo(localPath).Length, opened.Length);
            Assert.IsEmpty(RemoteEditDrafts.ListDrafts(_root), "刚下载下来的副本与远端一致,不是草稿");
            // macOS 的 FSEvents 会在启用 watcher 之后才补投下载那一下的创建事件:副本没动过,那不是保存,一次都不传
            // (#676 曾把这里放宽成「等那次多余的回传落地」,复查 build-ci-tests F2 改回来)。
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.AreEqual(0, Volatile.Read(ref uploads), "没动过的副本不回传");
            Assert.AreEqual(RemoteEditDrafts.Clean, RemoteEditDrafts.Read(directory)?.State);

            await File.WriteAllTextAsync(localPath, "edited once");
            RemoteEditMarker? uploaded = null;
            await WaitUntilAsync(() => Volatile.Read(ref uploads) == 1 && !session.HasPendingChange
                && session.State == RemoteEditState.Watching && (uploaded = RemoteEditDrafts.Read(directory))?.State == RemoteEditDrafts.Clean
                && uploaded.LastWriteUtc == new FileInfo(localPath).LastWriteTimeUtc);
            Assert.AreEqual(RemoteEditDrafts.Clean, uploaded?.State, "传上去之后变回干净");
            Assert.AreEqual(new FileInfo(localPath).LastWriteTimeUtc, uploaded?.LastWriteUtc,
                "干净标记记的是传上去的那一份");

            fail = true;
            await File.WriteAllTextAsync(localPath, "edited twice");
            await WaitUntilAsync(() => session.State == RemoteEditState.Failed);
            Assert.AreEqual(RemoteEditDrafts.Dirty, RemoteEditDrafts.Read(directory)?.State);
            RemoteEditDraft draft = RemoteEditDrafts.ListDrafts(_root).Single();
            Assert.AreEqual(localPath, draft.LocalPath);
            Assert.AreEqual("/etc/app.conf", draft.RemotePath);
        }
        finally
        {
            session.Dispose();
        }
        Assert.IsTrue(File.Exists(localPath), "传不上去的改动留着");
        Assert.AreEqual(RemoteEditDrafts.Dirty, RemoteEditDrafts.Read(directory)?.State);
    }

    /// <summary>
    /// watcher 报了事件、副本却还是与远端一致的那一份(长度与修改时间都没变):不回传、标记仍是干净。
    /// macOS 的 FSEvents 补投下载那一下的创建事件就是这样;这里用「改名挪开再挪回来」在各平台上造同样的事件(复查 build-ci-tests F2)。
    /// </summary>
    [TestMethod]
    public async Task WatcherEvent_OnAnUnchangedCopy_UploadsNothing()
    {
        string directory = Path.Combine(_root, "0a1b2c3e");
        Directory.CreateDirectory(directory);
        string localPath = Path.Combine(directory, "app.conf");
        await File.WriteAllTextAsync(localPath, "original");
        int uploads = 0;
        var session = new RemoteEditSession(new()
        {
            SftpService = Substitute.For<ISftpService>(),
            SessionId = Guid.NewGuid(),
            RemotePath = "/etc/app.conf",
            FileName = "app.conf",
            UploadAsync = (_, _) =>
            {
                Interlocked.Increment(ref uploads);
                return Task.CompletedTask;
            },
        }, localPath);
        try
        {
            string aside = localPath + ".moved";
            File.Move(localPath, aside);
            File.Move(aside, localPath);
            // 防抖 600ms 之后才会传;多等一会儿,确认真的一次都没传。
            await Task.Delay(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => !session.HasPendingChange && session.State == RemoteEditState.Watching);

            Assert.AreEqual(0, Volatile.Read(ref uploads), "副本没动过,事件不是保存");
            Assert.AreEqual(RemoteEditDrafts.Clean, RemoteEditDrafts.Read(directory)?.State);
            Assert.IsEmpty(RemoteEditDrafts.ListDrafts(_root));

            // 反证:真改了照样传。
            await File.WriteAllTextAsync(localPath, "edited");
            await WaitUntilAsync(() => Volatile.Read(ref uploads) == 1 && !session.HasPendingChange && session.State == RemoteEditState.Watching);
        }
        finally
        {
            session.Dispose();
        }
        Assert.IsFalse(Directory.Exists(directory), "全传上去了,收尾删掉副本");
    }

    /// <summary>
    /// 干净且 7 天没碰过的清掉;干净但最近碰过的、带着改动的、没有标记的、标记说干净副本却对不上的、正开着的,一个都不删。
    /// </summary>
    [TestMethod]
    public void Sweep_RemovesOnlyStaleCleanCopies_AndListsEverythingThatMightHoldChanges()
    {
        string staleClean = CleanCopy("00000001", Old);
        string recentClean = CleanCopy("00000002", Recent);
        string dirty = Copy("00000003", Old, RemoteEditDrafts.Dirty);
        string legacy = Copy("00000004", Old, state: null);
        string changedAfterClean = CleanCopy("00000005", Old);
        File.WriteAllText(Path.Combine(changedAfterClean, "app.conf"), "edited after the marker said clean");
        Touch(changedAfterClean, Old);
        File.SetLastWriteTimeUtc(Path.Combine(changedAfterClean, "app.conf"), Old.AddMinutes(1));
        string active = Copy("00000006", Old, RemoteEditDrafts.Dirty);
        string notOurs = Copy("not-ours", Old, state: null);
        string empty = Path.Combine(_root, "00000007");
        Directory.CreateDirectory(empty);
        Directory.SetLastWriteTimeUtc(empty, Old);

        int removed = RemoteEditDrafts.SweepStaleClean(_root, Now, [active]);

        Assert.AreEqual(2, removed);
        Assert.IsFalse(Directory.Exists(staleClean), "干净且 7 天没碰过:清掉");
        Assert.IsFalse(Directory.Exists(empty), "空壳没有可丢的东西:到点清掉");
        foreach (string kept in new[] { recentClean, dirty, legacy, changedAfterClean, active, notOurs })
        {
            Assert.IsTrue(Directory.Exists(kept), $"{Path.GetFileName(kept)} 不该被删");
        }

        IReadOnlyList<RemoteEditDraft> drafts = RemoteEditDrafts.ListDrafts(_root, [active]);
        Assert.AreSequenceEqual(
            ["00000003", "00000004", "00000005"],
            drafts.Select(static d => Path.GetFileName(d.Directory)).Order(StringComparer.Ordinal).ToList(),
            "有改动的、没有标记的、副本对不上干净标记的都是草稿;正开着的与不是本程序起的不列");
        RemoteEditDraft unknown = drafts.Single(d => d.Directory == legacy);
        Assert.IsNull(unknown.RemotePath, "旧版本留下的没有标记,说不出远端路径");
        Assert.AreEqual("app.conf", unknown.FileName);
    }

    [TestMethod]
    public void Notifications_OnePerDraft_WithTheOpenFolderAction_AndStaleOnesAreWithdrawn()
    {
        var known = new RemoteEditDraft(Path.Combine(_root, "0000000a"), Path.Combine(_root, "0000000a", "app.conf"), "app.conf",
            "/etc/app.conf", "prod-web", Old);
        var unknown = new RemoteEditDraft(Path.Combine(_root, "0000000b"), Path.Combine(_root, "0000000b", "x.txt"), "x.txt",
            null, null, Recent);

        IReadOnlyList<NotificationItem> items = NotificationSources.BuildRemoteEditDraftNotifications([known, unknown]);

        Assert.HasCount(2, items);
        NotificationItem first = items[0];
        Assert.AreEqual(NotificationSources.RemoteEditDraftIdPrefix + "0000000a", first.Id, "id 跟着草稿子目录:每次启动重投都去重、保住已读");
        Assert.AreEqual(NotificationKind.System, first.Kind);
        Assert.IsLessThan(TimeSpan.FromMinutes(1), DateTime.UtcNow - first.PublishedAt, "发布时间取发现它的时刻:几个月前的草稿也排在最上面,不会一投进来就沉底");
        Assert.AreEqual(NotificationSources.OpenRemoteEditDraftsCommandId, first.Link?.CommandId);
        Assert.Contains("prod-web:/etc/app.conf", first.Body);
        Assert.Contains(known.LocalPath, first.Body);
        Assert.Contains(unknown.LocalPath, items[1].Body);

        NotificationItem gone = items[1];
        NotificationItem unrelated = new() { Id = "update:2.0", Kind = NotificationKind.Update, Title = "u", PublishedAt = Now };
        Assert.AreSequenceEqual([gone.Id], NotificationSources.StaleRemoteEditDraftIds([first, gone, unrelated], [known]).ToList(),
            "草稿处理完删掉了,那一条撤掉;别的消息不动");
    }

    /// <summary>启动时:每份草稿投一条;消息中心里还挂着、草稿却已经删掉的那一条撤掉。</summary>
    [TestMethod]
    public async Task Startup_PublishesTheDrafts_AndWithdrawsNotificationsWhoseDraftIsGone()
    {
        _ = Copy("00000003", Old, RemoteEditDrafts.Dirty);
        _ = CleanCopy("00000002", Recent);
        INotificationCenter center = Substitute.For<INotificationCenter>();
        string gone = NotificationSources.RemoteEditDraftIdPrefix + "deadbeef";
        center.Items.Returns([new NotificationItem { Id = gone, Kind = NotificationKind.System, Title = "t", PublishedAt = Old }]);

        await VelaShell.ViewModels.MainWindowViewModel.PublishRemoteEditDraftsAsync(center, _root);

        await center.Received(1).RemoveAsync(gone, Arg.Any<CancellationToken>());
        await center.Received(1).PublishAsync(
            Arg.Is<IEnumerable<NotificationItem>>(items => items.Single().Id == NotificationSources.RemoteEditDraftIdPrefix + "00000003"),
            Arg.Any<CancellationToken>());
    }

    private string CleanCopy(string name, DateTime touched)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "app.conf");
        File.WriteAllText(copy, "as on the server");
        File.SetLastWriteTimeUtc(copy, touched);
        var info = new FileInfo(copy);
        RemoteEditDrafts.Write(directory, new RemoteEditMarker(RemoteEditDrafts.Clean, "app.conf", "/etc/app.conf", null, null,
            info.Length, info.LastWriteTimeUtc));
        Touch(directory, touched);
        return directory;
    }

    private string Copy(string name, DateTime touched, string? state)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app.conf"), "changes not uploaded");
        if (state is not null)
        {
            RemoteEditDrafts.Write(directory, new RemoteEditMarker(state, "app.conf", "/etc/app.conf", "prod-web", null, 0, default));
        }
        Touch(directory, touched);
        return directory;
    }

    /// <summary>把子目录与它里面每一项的修改时间都设到 <paramref name="when" />(最后设目录本身:建文件会改它)。</summary>
    private static void Touch(string directory, DateTime when)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            File.SetLastWriteTimeUtc(file, when);
        }
        Directory.SetLastWriteTimeUtc(directory, when);
    }

    private static async Task WaitUntilAsync(Func<bool> condition,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            // 文件事件随时会让状态再次变脏;观察到满足条件就返回,不要在循环外重新取样。
            if (condition()) return;
            await Task.Delay(5);
        }
        Assert.Fail($"等了 10 秒,编辑会话还没走到预期的处境: {expression}");
    }
}
