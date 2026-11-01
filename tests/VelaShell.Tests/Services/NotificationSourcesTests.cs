using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>
/// 决定这一轮往消息中心投什么。
/// </summary>
/// <remarks>
/// 这些判断原先埋在 <c>MainWindowViewModel</c> 里,想验一条"关掉推广之后促销条目不该进来"
/// 得先把三十个构造参数的主窗口造出来。拆成独立的决策之后每条规则都能单独摆上台面。
/// </remarks>
[TestClass]
[TestCategory("Notifications")]
public sealed class NotificationSourcesTests
{
    private static NotificationItem Item(string id, NotificationKind kind) =>
        new()
        {
            Id = id,
            Kind = kind,
            Title = id,
            PublishedAt = DateTime.UtcNow,
        };

    private static IAnnouncementFeed FeedWith(params NotificationItem[] items)
    {
        IAnnouncementFeed feed = Substitute.For<IAnnouncementFeed>();
        feed.FetchAsync(Arg.Any<CancellationToken>()).Returns(items);
        return feed;
    }

    [TestMethod]
    public async Task PromotionsAreDroppedWhenTheUserOptedOut()
    {
        IAnnouncementFeed feed = FeedWith(
            Item("news", NotificationKind.News),
            Item("sale", NotificationKind.Promotion));
        AppSettings settings = new() { Notifications = new() { AllowPromotions = false } };

        IReadOnlyList<NotificationItem> collected = await NotificationSources.CollectAsync(settings, feed, null);

        Assert.DoesNotContain(i => i.Kind == NotificationKind.Promotion, collected);
        Assert.Contains(i => i.Id == "news", collected, "公告本身照常收。");
    }

    [TestMethod]
    public async Task PromotionsComeThroughWhenAllowed()
    {
        IAnnouncementFeed feed = FeedWith(Item("sale", NotificationKind.Promotion));
        AppSettings settings = new() { Notifications = new() { AllowPromotions = true } };

        IReadOnlyList<NotificationItem> collected = await NotificationSources.CollectAsync(settings, feed, null);

        Assert.Contains(i => i.Id == "sale", collected);
    }

    /// <summary>「启动时检查更新」关掉时,连网络请求都不该发。</summary>
    /// <remarks>
    /// 两个开关是两件事:「通知我有更新」是消息中心的偏好,「启动时检查更新」是更新功能
    /// 自己的开关 —— 后者关掉的用户多半是不希望应用自己联网,那就一次也别联。
    /// </remarks>
    [TestMethod]
    public async Task TheUpdateCheckIsSkippedEntirelyWhenStartupChecksAreOff()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        AppSettings settings = new()
        {
            General = new() { CheckUpdatesOnStartup = false },
            Notifications = new() { NotifyUpdates = true },
        };

        await NotificationSources.CollectAsync(settings, null, updates);

        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    [TestMethod]
    public async Task TheUpdateCheckIsSkippedWhenUpdateNotificationsAreOff()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        AppSettings settings = new()
        {
            General = new() { CheckUpdatesOnStartup = true },
            Notifications = new() { NotifyUpdates = false },
        };

        await NotificationSources.CollectAsync(settings, null, updates);

        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    /// <summary>商店版不推更新消息。</summary>
    /// <remarks>
    /// 安装目录只读、更新由 Microsoft Store 接管,推一条"去关于页更新"只会把用户
    /// 送到一个什么也做不了的页面。
    /// </remarks>
    [TestMethod]
    public async Task AStoreManagedInstallIsNeverToldToUpdate()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.IsStoreManaged.Returns(true);

        Assert.IsNull(await NotificationSources.BuildUpdateNotificationAsync(updates));
        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    [TestMethod]
    public async Task AnOfflineUpdateCheckIsSwallowed()
    {
        // 离线或更新源不可达是常态,不该让铃铛变得不可用。
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync()
               .Returns<Task<bool>>(_ => throw new HttpRequestException("offline"));

        Assert.IsNull(await NotificationSources.BuildUpdateNotificationAsync(updates));
    }

    /// <summary>更新条目的 id 带版本号 —— 去重与"重新亮起未读"都靠它。</summary>
    /// <remarks>
    /// 同一个版本每次启动都会重投,消息中心按 id 跳过并**保住已读状态**;
    /// 真出了新版本则是一条新 id,会重新亮起未读。id 里不带版本号的话,
    /// 这两件事只能二选一。
    /// </remarks>
    [TestMethod]
    public async Task TheUpdateNotificationIsKeyedByVersion()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns("1.9.0");
        updates.CurrentVersion.Returns("1.8.0");

        NotificationItem? item = await NotificationSources.BuildUpdateNotificationAsync(updates);

        Assert.IsNotNull(item);
        Assert.AreEqual("update:1.9.0", item.Id);
        Assert.AreEqual(NotificationKind.Update, item.Kind);
        Assert.AreEqual("app.settings.about", item.Link?.CommandId);
    }

    [TestMethod]
    public async Task NoAvailableVersionMeansNoNotification()
    {
        // 服务说"有更新"却给不出版本号,是它自己的状态不一致;这时候投一条
        // 标题里带空版本的消息只会让人困惑。
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns((string?)null);

        Assert.IsNull(await NotificationSources.BuildUpdateNotificationAsync(updates));
    }

    [TestMethod]
    public async Task WithNoSourcesAtAllTheResultIsEmpty()
    {
        IReadOnlyList<NotificationItem> collected =
            await NotificationSources.CollectAsync(new AppSettings(), null, null);

        Assert.IsEmpty(collected);
    }

    // ---- 自动下载更新(设置 → 常规) ----

    /// <summary>
    /// 扮演后台下载入口(真实实现是 <c>SettingsViewModel.DownloadUpdateInBackgroundAsync</c>):
    /// 记下被调了几次,成功时让服务交出就绪快照,返回值与真实入口同一契约(是否已有就绪包)。
    /// </summary>
    private sealed class FakeBackgroundDownload(bool succeeds)
    {
        public int Calls { get; private set; }

        public UpdateManifest? Ready { get; private set; }

        public Task<bool> RunAsync()
        {
            Calls++;
            if (succeeds)
            {
                Ready = UpdateManifest.Parse(
                    """{"version":"1.9.0","tag":"1.9.0","assets":{"win-x64":{"name":"a.zip","sha256":"aa","size":1}}}""");
            }
            return Task.FromResult(Ready is not null);
        }
    }

    /// <summary>一台查得到 1.9.0、能原地换版的安装;就绪快照取自 <paramref name="download" />。</summary>
    private static IUpdateService UpdatableTo190(FakeBackgroundDownload download)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns("1.9.0");
        updates.CurrentVersion.Returns("1.8.0");
        updates.CanSelfUpdate.Returns(true);
        updates.ReadyUpdate.Returns(_ => download.Ready);
        return updates;
    }

    private static AppSettings AutoDownload(bool on, bool notify = true, bool checkOnStartup = true) => new()
    {
        General = new() { CheckUpdatesOnStartup = checkOnStartup, AutoDownloadUpdates = on },
        Notifications = new() { NotifyUpdates = notify },
    };

    /// <summary>开着自动下载:先在后台下完、校验完,再投「已下载好,重启即可更新」—— 而不是先投一条「有新版本」。</summary>
    [TestMethod]
    public async Task AutoDownloadOn_DownloadsFirstThenAnnouncesReady()
    {
        FakeBackgroundDownload background = new(succeeds: true);
        IUpdateService updates = UpdatableTo190(background);

        NotificationItem? item = await NotificationSources.CollectUpdateAsync(
            AutoDownload(on: true), updates, background.RunAsync);

        Assert.AreEqual(1, background.Calls);
        Assert.IsNotNull(item);
        Assert.AreEqual("update-ready:1.9.0", item.Id);
        Assert.AreEqual(Strings.Format("Notify_UpdateReadyTitle", "1.9.0"), item.Title);
        Assert.AreEqual("app.settings.about", item.Link?.CommandId, "重启入口在关于页。");
        updates.DidNotReceive().ApplyUpdateAndRestart();
    }

    /// <summary>关着自动下载:只检查、投「有新版本」,一个字节也不下。</summary>
    [TestMethod]
    public async Task AutoDownloadOff_OnlyChecks()
    {
        FakeBackgroundDownload background = new(succeeds: true);
        IUpdateService updates = UpdatableTo190(background);

        NotificationItem? item = await NotificationSources.CollectUpdateAsync(
            AutoDownload(on: false), updates, background.RunAsync);

        Assert.AreEqual(0, background.Calls);
        Assert.AreEqual("update:1.9.0", item?.Id);
    }

    /// <summary>下载失败不打扰:退回普通的「有新版本」,不投任何报错。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutoDownloadFailure_FallsBackToTheAvailableNotice(bool throws)
    {
        FakeBackgroundDownload background = new(succeeds: false);
        IUpdateService updates = UpdatableTo190(background);
        BackgroundUpdateDownload download = throws
            ? () => Task.FromException<bool>(new HttpRequestException("offline"))
            : background.RunAsync;

        NotificationItem? item = await NotificationSources.CollectUpdateAsync(AutoDownload(on: true), updates, download);

        Assert.AreEqual("update:1.9.0", item?.Id);
        Assert.AreEqual(NotificationKind.Update, item?.Kind);
    }

    /// <summary>
    /// 「提醒可用更新」关着、「自动下载更新」开着:照样检查并下载(重启入口在关于页等着),
    /// 只是不投消息。以前这种组合连检查都不做。
    /// </summary>
    [TestMethod]
    public async Task AutoDownloadWithNotificationsOff_DownloadsSilently()
    {
        FakeBackgroundDownload background = new(succeeds: true);
        IUpdateService updates = UpdatableTo190(background);

        NotificationItem? item = await NotificationSources.CollectUpdateAsync(
            AutoDownload(on: true, notify: false), updates, background.RunAsync);

        Assert.AreEqual(1, background.Calls);
        Assert.IsNull(item);
    }

    /// <summary>「启动时检查更新」是联网的总闸:关着时自动下载开着也一个请求都不发。</summary>
    [TestMethod]
    public async Task AutoDownloadNeverBypassesTheStartupCheckSwitch()
    {
        FakeBackgroundDownload background = new(succeeds: true);
        IUpdateService updates = UpdatableTo190(background);

        NotificationItem? item = await NotificationSources.CollectUpdateAsync(
            AutoDownload(on: true, checkOnStartup: false), updates, background.RunAsync);

        Assert.IsNull(item);
        Assert.AreEqual(0, background.Calls);
        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    /// <summary>不能原地换版的安装(Linux、只读目录):自动下载不起作用,照旧只投「有新版本」。</summary>
    [TestMethod]
    public async Task AutoDownloadSkipsInstallsThatCannotSelfUpdate()
    {
        FakeBackgroundDownload background = new(succeeds: true);
        IUpdateService updates = UpdatableTo190(background);
        updates.CanSelfUpdate.Returns(false);

        NotificationItem? item = await NotificationSources.CollectUpdateAsync(
            AutoDownload(on: true), updates, background.RunAsync);

        Assert.AreEqual(0, background.Calls);
        Assert.AreEqual("update:1.9.0", item?.Id);
    }

    /// <summary>没有新版本就不下载,也不投任何东西。</summary>
    [TestMethod]
    public async Task AutoDownloadDoesNothingWithoutAnUpdate()
    {
        FakeBackgroundDownload background = new(succeeds: true);
        IUpdateService updates = UpdatableTo190(background);
        updates.CheckForUpdateAsync().Returns(false);

        NotificationItem? item = await NotificationSources.CollectUpdateAsync(
            AutoDownload(on: true), updates, background.RunAsync);

        Assert.IsNull(item);
        Assert.AreEqual(0, background.Calls);
    }

    // ———— 本机加密密钥(主密码保护第一步:密钥交给系统密钥链) ————

    private static readonly DateTime Now = new(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>一切照常(包括这次顺利迁进了密钥链)不打扰 —— 用户无感是这一步的要求。</summary>
    [TestMethod]
    public void MasterKey_NothingToSay_WhenTheKeyIsInTheKeychain()
    {
        Assert.IsEmpty(NotificationSources.BuildMasterKeyNotifications(
            new() { Protection = MasterKeyProtection.SystemKeychain, KeychainName = "Secret Service", Migrated = true }, Now));
        Assert.IsEmpty(NotificationSources.BuildMasterKeyNotifications(
            new() { Protection = MasterKeyProtection.UserBoundFile }, Now), "Windows 上 DPAPI 包裹的密钥文件就是常态");
    }

    /// <summary>主密码开着却解不了锁(头不在):要说清只能「忘了主密码」另起新密钥;开着、锁着这一常态不打扰。</summary>
    [TestMethod]
    public void MasterPassword_HeaderMissing_IsAWarning_ButBeingLockedIsNot()
    {
        NotificationItem notice = Assert.ContainsSingle(NotificationSources.BuildMasterKeyNotifications(new()
        {
            Protection = MasterKeyProtection.SystemKeychain,
            MasterPasswordEnabled = true,
            MasterPasswordProblem = MasterPasswordProblem.HeaderMissing
        }, Now));
        Assert.AreEqual(NotificationSeverity.Warning, notice.Severity);
        Assert.AreEqual(Strings.Get("MasterPassword_ProblemHeaderMissing"), notice.Body);
        Assert.IsEmpty(NotificationSources.BuildMasterKeyNotifications(
            new() { Protection = MasterKeyProtection.SystemKeychain, MasterPasswordEnabled = true }, Now), "开着、锁着是常态");
    }

    /// <summary>头找不到它包裹的密钥、被改名保留(降级到旧版本之后):点名保留成的文件,说明主密码现在是关着的。</summary>
    [TestMethod]
    public void MasterPassword_RetiredHeader_NamesTheKeptFile()
    {
        NotificationItem notice = Assert.ContainsSingle(NotificationSources.BuildMasterKeyNotifications(new()
        {
            Protection = MasterKeyProtection.UserBoundFile,
            RetiredMasterPasswordHeader = Path.Combine("data", "secret.key.mp.orphaned-20261031120000")
        }, Now));
        Assert.AreEqual(Strings.Format("Notify_MasterPasswordRetiredBody", "secret.key.mp.orphaned-20261031120000"), notice.Body);
    }
    /// <summary>密钥链暂时用不了:要说清已存的密码这次解不开、但没有丢,以及原因。</summary>
    [TestMethod]
    public void MasterKey_KeychainUnavailable_SaysTheSecretsAreNotLostAndWhy()
    {
        NotificationItem notice = Assert.ContainsSingle(NotificationSources.BuildMasterKeyNotifications(new()
        {
            Protection = MasterKeyProtection.PlainFile,
            KeychainName = "Secret Service",
            KeychainError = "Cannot autolaunch D-Bus without X11 $DISPLAY",
            UnavailableKeyIds = ["0123456789abcdef0123456789abcdef"]
        }, Now));

        Assert.AreEqual(NotificationKind.System, notice.Kind);
        Assert.AreEqual(NotificationSeverity.Warning, notice.Severity);
        Assert.AreEqual(Strings.Get("Notify_MasterKeyUnavailableTitle"), notice.Title);
        Assert.Contains("Cannot autolaunch D-Bus without X11 $DISPLAY", notice.Body!);
        Assert.Contains("Secret Service", notice.Body!);
        Assert.AreEqual("master-key:unavailable:20261025", notice.Id, "可能反复发生:按天去重,而不是每次启动都再亮一条");
    }

    /// <summary>条目不在(数据目录从别处拷来):提示重新输入密码;同一批编号只提示一次。</summary>
    [TestMethod]
    public void MasterKey_MissingEntry_IsReportedOncePerKey()
    {
        MasterKeyStatus status = new()
        {
            Protection = MasterKeyProtection.SystemKeychain,
            KeychainName = "macOS Keychain",
            MissingKeyIds = ["0123456789abcdef0123456789abcdef"]
        };

        NotificationItem today = Assert.ContainsSingle(NotificationSources.BuildMasterKeyNotifications(status, Now));
        NotificationItem later = Assert.ContainsSingle(NotificationSources.BuildMasterKeyNotifications(status, Now.AddDays(3)));

        Assert.AreEqual(Strings.Get("Notify_MasterKeyMissingTitle"), today.Title);
        Assert.AreEqual(Strings.Get("Notify_MasterKeyMissingBody"), today.Body);
        Assert.AreEqual(today.Id, later.Id, "消息中心按 id 去重:换了电脑这件事只说一次");
    }

    /// <summary>密钥文件被换过:说出原文件留在了哪,找回原账户 / 电脑时有据可查。</summary>
    [TestMethod]
    public void MasterKey_ReplacedKeyFile_NamesTheFileThatWasKept()
    {
        NotificationItem notice = Assert.ContainsSingle(NotificationSources.BuildMasterKeyNotifications(new()
        {
            Protection = MasterKeyProtection.UserBoundFile,
            ReplacedKeyFile = Path.Combine("home", ".velashell", "secret.key.unreadable-20261025080000")
        }, Now));

        Assert.AreEqual(NotificationSeverity.Warning, notice.Severity);
        Assert.Contains("secret.key.unreadable-20261025080000", notice.Body!);
        Assert.AreEqual("master-key:replaced:secret.key.unreadable-20261025080000", notice.Id);
    }

    /// <summary>
    /// 云同步拉到「Webhook 告警开着却没有地址」:一条警告级的系统消息,带「前往安全审计设置」;
    /// id 带着那份设置的指纹,同一份设置投几次都只剩一条(第二轮第 17 条)。
    /// </summary>
    [TestMethod]
    public void SyncWebhookMissing_PointsToSecuritySettings_AndIsKeyedByTheSettings()
    {
        NotificationItem notice = NotificationSources.BuildSyncWebhookMissingNotification("0123456789abcdef", Now);
        NotificationItem again = NotificationSources.BuildSyncWebhookMissingNotification("0123456789abcdef", Now.AddHours(5));
        NotificationItem other = NotificationSources.BuildSyncWebhookMissingNotification("fedcba9876543210", Now);

        Assert.AreEqual(NotificationKind.System, notice.Kind);
        Assert.AreEqual(NotificationSeverity.Warning, notice.Severity);
        Assert.AreEqual(Strings.Get("Notify_SyncWebhookMissingTitle"), notice.Title);
        Assert.AreEqual(NotificationSources.OpenSecuritySettingsCommandId, notice.Link?.CommandId);
        Assert.AreEqual(notice.Id, again.Id);
        Assert.AreNotEqual(notice.Id, other.Id);
    }

    /// <summary>从没迁过、密钥链又用不了(没装 keyring 的机器):如实说密钥还在文件里,只说一次。</summary>
    [TestMethod]
    public void MasterKey_FileFallback_IsAnInfoNoticeShownOnce()
    {
        NotificationItem notice = Assert.ContainsSingle(NotificationSources.BuildMasterKeyNotifications(new()
        {
            Protection = MasterKeyProtection.PlainFile,
            KeychainName = "Secret Service",
            KeychainError = "libsecret-1.so.0 is not installed"
        }, Now));

        Assert.AreEqual(NotificationSeverity.Info, notice.Severity);
        Assert.AreEqual("master-key:file-fallback", notice.Id);
        Assert.Contains("libsecret-1.so.0 is not installed", notice.Body!);
    }
}
