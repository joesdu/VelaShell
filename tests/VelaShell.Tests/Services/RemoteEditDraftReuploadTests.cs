using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Infrastructure.Notifications;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 消息中心里外部编辑草稿的「重新上传」:按标记里的连接配置连上那台服务器,远端在这之后被改过(或不在了、说不准)先问,
/// 成功后删草稿、撤消息;失败时两样都留着并说明原因。配置已删、没记着配置、远端目录不在、远端成了目录,各有明确提示。
/// </summary>
[TestClass]
[TestCategory("ExternalEdit")]
[TestCategory("Notifications")]
public sealed class RemoteEditDraftReuploadTests
{
    private const string Key = "0a1b2c3d";
    private const string RemotePath = "/etc/app.conf";
    private static readonly DateTime Synced = new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

    private readonly Guid _profileId = Guid.NewGuid();
    private string _root = string.Empty;
    private NotificationCenter _center = null!;

    [TestInitialize]
    public async Task CreateRoot()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vela-reupload-{Guid.NewGuid():N}")).FullName;
        _center = new();
        await _center.PublishAsync([new NotificationItem
        {
            Id = NotificationSources.RemoteEditDraftIdPrefix + Key,
            Kind = NotificationKind.System,
            Title = "draft",
            PublishedAt = DateTime.UtcNow
        }]);
    }

    [TestCleanup]
    public void RemoveRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DraftDirectory => Path.Combine(_root, Key);

    private string LocalCopy => Path.Combine(DraftDirectory, "app.conf");

    private bool MessageStillThere => _center.Items.Any(item => item.Id == NotificationSources.RemoteEditDraftIdPrefix + Key);

    /// <summary>远端还是上次一致时的那一份:不问,直接传;传完删草稿、撤消息,提示说清传到了哪。</summary>
    [TestMethod]
    public async Task UnchangedRemote_IsOverwrittenWithoutAsking_AndTheDraftIsSettled()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        var target = new FakeTarget { Remote = Remote(8, Synced) };
        var asked = new List<string>();

        RemoteEditDraftReuploadOutcome outcome = await Reuploader(target, asked).ReuploadAsync(Key);

        Assert.AreEqual(RemoteEditDraftReuploadResult.Uploaded, outcome.Result);
        Assert.IsEmpty(asked, "远端没被改过,不问");
        Assert.AreSequenceEqual([(LocalCopy, RemotePath)], target.Uploads);
        Assert.AreEqual("edited locally", target.UploadedContent);
        Assert.IsFalse(Directory.Exists(DraftDirectory), "传上去了,草稿删掉");
        Assert.IsFalse(MessageStillThere, "消息撤掉");
        Assert.Contains("prod-web:/etc/app.conf", outcome.Message);
    }

    /// <summary>
    /// 远端在这之后被改过(长度或修改时间对不上):先问;说不传就什么都不动。标记里没记远端样子的(旧版本、当时查不到)同样先问,
    /// 正文换成「说不准」;后端查不了远端的也问。说传才传。
    /// </summary>
    [TestMethod]
    [DataRow(8L, 1, "changed-time")]
    [DataRow(9L, 0, "changed-size")]
    [DataRow(null, 0, "no-baseline")]
    public async Task ChangedOrUnknownRemote_AsksFirst_AndDecliningTouchesNothing(long? baselineLength, int minutesLater, string name)
    {
        WriteDraft(remoteLength: baselineLength, remoteModified: baselineLength is null ? null : Synced);
        var target = new FakeTarget { Remote = Remote(8, Synced.AddMinutes(minutesLater)) };
        var asked = new List<string>();

        RemoteEditDraftReuploadOutcome declined = await Reuploader(target, asked, answer: false).ReuploadAsync(Key);

        Assert.AreEqual(RemoteEditDraftReuploadResult.Declined, declined.Result, name);
        Assert.HasCount(1, asked, name);
        Assert.Contains(RemotePath, asked[0], name);
        Assert.IsEmpty(target.Uploads, $"{name}:说不传就不传");
        Assert.IsTrue(File.Exists(LocalCopy), name);
        Assert.IsTrue(MessageStillThere, name);

        RemoteEditDraftReuploadOutcome accepted = await Reuploader(target, asked, answer: true).ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.Uploaded, accepted.Result, name);
        Assert.HasCount(1, target.Uploads, name);
    }

    /// <summary>问的正文:改过的说「被改过」并写出远端现在的大小;标记里没记的说「说不准」;后端查不了的说「查不到」。</summary>
    [TestMethod]
    public async Task ConflictPrompt_SaysWhatIsKnown()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        var asked = new List<string>();
        await Reuploader(new FakeTarget { Remote = Remote(4096, Synced.AddHours(2)) }, asked, answer: false).ReuploadAsync(Key);
        WriteDraft(remoteLength: null, remoteModified: null);
        await Reuploader(new FakeTarget { Remote = Remote(8, Synced) }, asked, answer: false).ReuploadAsync(Key);
        await Reuploader(new FakeTarget { StatThrows = new NotSupportedException() }, asked, answer: false).ReuploadAsync(Key);

        string where = "prod-web:" + RemotePath;
        Assert.AreEqual(Format("RemoteEditDraft_ConflictBody", where, VelaShell.ViewModels.RemoteFileInfoViewModel.FormatSize(4096),
            Synced.AddHours(2).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)), asked[0]);
        Assert.AreEqual(Format("RemoteEditDraft_ConflictUnknownBody", where, VelaShell.ViewModels.RemoteFileInfoViewModel.FormatSize(8),
            Synced.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)), asked[1], "标记里没记:哪怕远端看着一样也问");
        Assert.AreEqual(Format("RemoteEditDraft_ConflictNoInfoBody", where), asked[2]);
    }

    /// <summary>没有能问的人(没挂确认框):远端改过就不传,绝不静默覆盖。</summary>
    [TestMethod]
    public async Task WithoutAConfirmer_AChangedRemoteIsNeverOverwritten()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        var target = new FakeTarget { Remote = Remote(8, Synced.AddSeconds(1)) };
        var reuploader = new RemoteEditDraftReuploader(_root, FindProfile, _ => Task.FromResult<IRemoteEditDraftTarget?>(target),
            confirm: null, _center, () => []);

        RemoteEditDraftReuploadOutcome outcome = await reuploader.ReuploadAsync(Key);

        Assert.AreEqual(RemoteEditDraftReuploadResult.Declined, outcome.Result);
        Assert.IsEmpty(target.Uploads);
        Assert.IsTrue(MessageStillThere);
    }

    /// <summary>
    /// 远端文件不在了:所在目录还在就问要不要在原路径重新创建(说要才传);目录也不在了就不问、不传,提示写明是哪个目录。
    /// 远端那个路径成了目录:不传,明确提示。三种都留着草稿与消息。
    /// </summary>
    [TestMethod]
    public async Task MissingRemoteFile_AsksToRecreate_ButAMissingFolderOrADirectoryIsReported()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        var asked = new List<string>();

        var gone = new FakeTarget { Remote = null, ParentExists = true };
        RemoteEditDraftReuploadOutcome declined = await Reuploader(gone, asked, answer: false).ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.Declined, declined.Result);
        Assert.AreEqual(Format("RemoteEditDraft_RemoteMissingBody", "prod-web:" + RemotePath), asked.Single());
        Assert.IsEmpty(gone.Uploads);

        var noFolder = new FakeTarget { Remote = null, ParentExists = false };
        RemoteEditDraftReuploadOutcome folderMissing = await Reuploader(noFolder, asked, answer: true).ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.RemoteDirectoryMissing, folderMissing.Result);
        Assert.AreEqual(Format("RemoteEditDraft_RemoteDirMissing", "/etc", "app.conf"), folderMissing.Message);
        Assert.HasCount(1, asked, "目录不在了:不问");
        Assert.AreSequenceEqual(["/etc"], noFolder.DirectoryChecks);
        Assert.IsEmpty(noFolder.Uploads);

        var directory = new FakeTarget { Remote = Remote(4096, Synced, isDirectory: true) };
        RemoteEditDraftReuploadOutcome isDirectory = await Reuploader(directory, asked, answer: true).ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.RemoteIsDirectory, isDirectory.Result);
        Assert.IsEmpty(directory.Uploads);

        Assert.IsTrue(File.Exists(LocalCopy));
        Assert.IsTrue(MessageStillThere);

        RemoteEditDraftReuploadOutcome recreated = await Reuploader(gone, asked, answer: true).ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.Uploaded, recreated.Result, "说要重新创建才传");
        Assert.HasCount(1, gone.Uploads);
    }

    /// <summary>
    /// 连不上的几种:配置已删(不去连,提示写明副本位置)、标记里没记连接配置(快速连接)、连接那一路没连上(那一路已经报过原因,这里不再说)。
    /// 都留着草稿与消息。
    /// </summary>
    [TestMethod]
    public async Task ProfileMissing_UnknownProfile_AndNotConnected_KeepTheDraft()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        int connects = 0;
        var deleted = new RemoteEditDraftReuploader(_root, _ => Task.FromResult<SessionProfile?>(null),
            _ => { connects++; return Task.FromResult<IRemoteEditDraftTarget?>(new FakeTarget()); }, Confirm(true, []), _center, () => []);
        RemoteEditDraftReuploadOutcome profileMissing = await deleted.ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.ProfileMissing, profileMissing.Result);
        Assert.AreEqual(Format("RemoteEditDraft_ProfileMissing", "app.conf", LocalCopy), profileMissing.Message);
        Assert.AreEqual(0, connects, "配置已删:不去猜它原来连的是哪台");

        var notConnected = new RemoteEditDraftReuploader(_root, FindProfile, _ => Task.FromResult<IRemoteEditDraftTarget?>(null),
            Confirm(true, []), _center, () => []);
        RemoteEditDraftReuploadOutcome failedToConnect = await notConnected.ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.NotConnected, failedToConnect.Result);
        Assert.IsNull(failedToConnect.Message, "连接那一路报过原因了");

        WriteDraft(remoteLength: 8, remoteModified: Synced, profileId: null);
        RemoteEditDraftReuploadOutcome unknown = await Reuploader(new FakeTarget(), []).ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.NoProfile, unknown.Result);
        Assert.AreEqual(Format("RemoteEditDraft_NoProfile", "app.conf", LocalCopy), unknown.Message);

        Assert.IsTrue(File.Exists(LocalCopy));
        Assert.IsTrue(MessageStillThere);
    }

    /// <summary>草稿已经不在了(在文件夹里处理掉了):消息撤掉,如实说一声;子目录名不是本程序起的同样当作不在。</summary>
    [TestMethod]
    public async Task GoneDraft_WithdrawsItsMessage()
    {
        RemoteEditDraftReuploadOutcome outcome = await Reuploader(new FakeTarget(), []).ReuploadAsync(Key);

        Assert.AreEqual(RemoteEditDraftReuploadResult.DraftGone, outcome.Result);
        Assert.AreEqual(VelaShell.Core.Resources.Strings.Get("RemoteEditDraft_Gone"), outcome.Message);
        Assert.IsFalse(MessageStillThere);
        Assert.AreEqual(RemoteEditDraftReuploadResult.DraftGone, (await Reuploader(new FakeTarget(), []).ReuploadAsync("../x")).Result);
    }

    /// <summary>上传失败:草稿与消息都留着,提示带着原因;查远端失败同样。</summary>
    [TestMethod]
    public async Task UploadOrStatFailure_KeepsTheDraft_AndSaysWhy()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        var target = new FakeTarget { Remote = Remote(8, Synced), UploadThrows = new IOException("Permission denied") };

        RemoteEditDraftReuploadOutcome failed = await Reuploader(target, []).ReuploadAsync(Key);

        Assert.AreEqual(RemoteEditDraftReuploadResult.Failed, failed.Result);
        Assert.AreEqual(Format("RemoteEditDraft_Failed", "app.conf", "prod-web:" + RemotePath, "Permission denied"), failed.Message);
        Assert.IsTrue(File.Exists(LocalCopy));
        Assert.AreEqual(RemoteEditDrafts.Dirty, RemoteEditDrafts.Read(DraftDirectory)?.State, "草稿还是草稿");
        Assert.IsTrue(MessageStillThere);

        RemoteEditDraftReuploadOutcome statFailed = await Reuploader(new FakeTarget { StatThrows = new IOException("Connection reset") }, [])
            .ReuploadAsync(Key);
        Assert.AreEqual(RemoteEditDraftReuploadResult.Failed, statFailed.Result);
        Assert.Contains("Connection reset", statFailed.Message);
    }

    /// <summary>上传的过程中本机副本又被存了一次:远端拿到的是之前那一份,草稿与消息都留着。</summary>
    [TestMethod]
    public async Task CopyChangedDuringUpload_IsKept()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        var target = new FakeTarget
        {
            Remote = Remote(8, Synced),
            DuringUpload = () =>
            {
                File.WriteAllText(LocalCopy, "edited again while uploading, much longer");
                File.SetLastWriteTimeUtc(LocalCopy, DateTime.UtcNow.AddMinutes(1));
            }
        };

        RemoteEditDraftReuploadOutcome outcome = await Reuploader(target, []).ReuploadAsync(Key);

        Assert.AreEqual(RemoteEditDraftReuploadResult.UploadedButChanged, outcome.Result);
        Assert.IsTrue(File.Exists(LocalCopy));
        Assert.IsTrue(MessageStillThere);
    }

    /// <summary>同一份连点两下:第二下不再连、不再传。</summary>
    [TestMethod]
    public async Task SecondClickWhileTheFirstIsRunning_IsIgnored()
    {
        WriteDraft(remoteLength: 8, remoteModified: Synced);
        var gate = new TaskCompletionSource<IRemoteEditDraftTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int connects = 0;
        var reuploader = new RemoteEditDraftReuploader(_root, FindProfile, _ => { connects++; return gate.Task; }, Confirm(true, []), _center, () => []);

        Task<RemoteEditDraftReuploadOutcome> first = reuploader.ReuploadAsync(Key);
        while (connects == 0)
        {
            await Task.Delay(5);
        }
        RemoteEditDraftReuploadOutcome second = await reuploader.ReuploadAsync(Key);
        var target = new FakeTarget { Remote = Remote(8, Synced) };
        gate.SetResult(target);

        Assert.AreEqual(RemoteEditDraftReuploadResult.Busy, second.Result);
        Assert.AreEqual(RemoteEditDraftReuploadResult.Uploaded, (await first).Result);
        Assert.AreEqual(1, connects);
        Assert.HasCount(1, target.Uploads);
    }

    /// <summary>远端此刻的样子与标记里记着的比:长度与修改时间都对得上才算没改过;标记里没记、远端是目录、查不到的都不算。</summary>
    [TestMethod]
    public void MatchesBaseline_NeedsBothLengthAndTime()
    {
        var marker = new RemoteEditMarker(RemoteEditDrafts.Dirty, "app.conf", RemotePath, null, _profileId, 0, default, 8, Synced);
        Assert.IsTrue(RemoteEditDraftReuploader.MatchesBaseline(marker, Remote(8, Synced)));
        Assert.IsTrue(RemoteEditDraftReuploader.MatchesBaseline(marker, Remote(8, Synced.ToLocalTime())), "时区写法不同不算改过");
        Assert.IsFalse(RemoteEditDraftReuploader.MatchesBaseline(marker, Remote(9, Synced)));
        Assert.IsFalse(RemoteEditDraftReuploader.MatchesBaseline(marker, Remote(8, Synced.AddSeconds(1))));
        Assert.IsFalse(RemoteEditDraftReuploader.MatchesBaseline(marker, Remote(8, Synced, isDirectory: true)));
        Assert.IsFalse(RemoteEditDraftReuploader.MatchesBaseline(marker, null));
        Assert.IsFalse(RemoteEditDraftReuploader.MatchesBaseline(marker with { RemoteLength = null }, Remote(8, Synced)));
        Assert.IsFalse(RemoteEditDraftReuploader.MatchesBaseline(marker with { RemoteModifiedUtc = null }, Remote(8, Synced)));
    }

    private RemoteEditDraftReuploader Reuploader(FakeTarget target, List<string> asked, bool answer = true) =>
        new(_root, FindProfile, _ => Task.FromResult<IRemoteEditDraftTarget?>(target), Confirm(answer, asked), _center, () => []);

    private static Func<string, string, string, Task<bool>> Confirm(bool answer, List<string> asked) =>
        (_, body, _) =>
        {
            asked.Add(body);
            return Task.FromResult(answer);
        };

    private Task<SessionProfile?> FindProfile(Guid id) =>
        Task.FromResult<SessionProfile?>(id == _profileId ? new SessionProfile { Id = _profileId, Name = "prod-web", Host = "10.0.0.5" } : null);

    private void WriteDraft(long? remoteLength, DateTime? remoteModified, bool profile = true) =>
        WriteDraft(remoteLength, remoteModified, profile ? _profileId : null);

    private void WriteDraft(long? remoteLength, DateTime? remoteModified, Guid? profileId)
    {
        Directory.CreateDirectory(DraftDirectory);
        File.WriteAllText(LocalCopy, "edited locally");
        RemoteEditDrafts.Write(DraftDirectory, new RemoteEditMarker(RemoteEditDrafts.Dirty, "app.conf", RemotePath, "prod-web", profileId, 0, default,
            remoteLength, remoteModified));
    }

    private static string Format(string key, params object?[] args) => VelaShell.Core.Resources.Strings.Format(key, args);

    private static RemoteFileInfo Remote(long size, DateTime modifiedUtc, bool isDirectory = false) => new()
    {
        Name = "app.conf",
        FullPath = RemotePath,
        Size = size,
        Permissions = "rw-r--r--",
        IsDirectory = isDirectory,
        LastModified = modifiedUtc,
        Owner = "root",
        Group = "root"
    };

    private sealed class FakeTarget : IRemoteEditDraftTarget
    {
        public RemoteFileInfo? Remote { get; init; }

        public bool ParentExists { get; init; } = true;

        public Exception? StatThrows { get; init; }

        public Exception? UploadThrows { get; init; }

        public Action? DuringUpload { get; init; }

        public List<(string Local, string Remote)> Uploads { get; } = [];

        public List<string> DirectoryChecks { get; } = [];

        public string? UploadedContent { get; private set; }

        public Task<RemoteFileInfo?> StatAsync(string remotePath, CancellationToken cancellationToken) =>
            StatThrows is { } error ? Task.FromException<RemoteFileInfo?>(error) : Task.FromResult(Remote);

        public Task<bool> DirectoryExistsAsync(string remotePath, CancellationToken cancellationToken)
        {
            DirectoryChecks.Add(remotePath);
            return Task.FromResult(ParentExists);
        }

        public Task UploadAsync(string localPath, string remotePath)
        {
            if (UploadThrows is { } error)
            {
                return Task.FromException(error);
            }
            Uploads.Add((localPath, remotePath));
            UploadedContent = File.ReadAllText(localPath);
            DuringUpload?.Invoke();
            return Task.CompletedTask;
        }
    }
}
