using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Notifications;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>消息中心面板:跳转优先级、已读联动、筛选与相对时间。</summary>
[TestClass]
[TestCategory("Notifications")]
public class NotificationPanelViewModelTests
{
    private static NotificationItem Item(string id, NotificationLink? link = null, bool read = false) =>
        new()
        {
            Id = id,
            Kind = NotificationKind.News,
            Title = $"消息 {id}",
            PublishedAt = DateTime.UtcNow,
            IsRead = read,
            Link = link
        };

    private static async Task<NotificationCenter> SeededCenterAsync(params NotificationItem[] items)
    {
        var center = new NotificationCenter();
        await center.PublishAsync(items);
        return center;
    }

    /// <summary>
    /// 站内命令优先于外链:站内能办的事不该把人赶去浏览器。
    /// 更新提醒正是这条路径 —— 点一下直接落到设置的关于页,而不是打开下载页。
    /// </summary>
    [TestMethod]
    public async Task Activate_PrefersInAppCommandOverUrl()
    {
        NotificationCenter center = await SeededCenterAsync(Item("both", new()
        {
            Label = "查看",
            CommandId = "app.settings.about",
            Url = "https://example.com/release"
        }));
        string? invokedCommand = null;
        var openedUrls = new List<string>();
        var vm = new NotificationPanelViewModel(center,
            id => { invokedCommand = id; return true; },
            url => { openedUrls.Add(url); return Task.CompletedTask; });

        await vm.ActivateCommand.Execute("both").FirstAsync();

        Assert.AreEqual("app.settings.about", invokedCommand);
        Assert.IsEmpty(openedUrls, "站内跳成功后不该再开浏览器。");
    }

    /// <summary>命令没注册(返回 false)时退回外链,而不是什么都不做。</summary>
    [TestMethod]
    public async Task Activate_FallsBackToUrl_WhenCommandMissing()
    {
        NotificationCenter center = await SeededCenterAsync(Item("fallback", new()
        {
            Label = "查看",
            CommandId = "plugin.not.installed",
            Url = "https://example.com/post"
        }));
        var openedUrls = new List<string>();
        var vm = new NotificationPanelViewModel(center, _ => false, url =>
        {
            openedUrls.Add(url);
            return Task.CompletedTask;
        });

        await vm.ActivateCommand.Execute("fallback").FirstAsync();

        Assert.AreSequenceEqual(["https://example.com/post"], openedUrls);
    }

    /// <summary>
    /// 带参数的去处交给带参数的那一路,不走命令注册表;那一路不认识(返回 false)或者没挂时,不退回不带参数的同名命令 ——
    /// 丢了参数去做,做的就是另一件事。不带参数的(旧版本落盘的消息)照旧走注册表。
    /// </summary>
    [TestMethod]
    public async Task Activate_RoutesParameterizedLinksToTheirOwnInvoker_AndNeverDropsTheParameter()
    {
        NotificationCenter center = await SeededCenterAsync(
            Item("with-parameter", new() { Label = "重新上传", CommandId = "files.remoteEditDrafts.reupload", CommandParameter = "0a1b2c3d" }),
            Item("legacy", new() { Label = "打开所在文件夹", CommandId = "files.remoteEditDrafts.open" }));
        var plain = new List<string>();
        var parameterized = new List<(string Id, string Parameter)>();
        int closed = 0;
        var vm = new NotificationPanelViewModel(center,
            id => { plain.Add(id); return true; },
            parameterizedCommandInvoker: (id, parameter) => { parameterized.Add((id, parameter)); return true; });
        vm.CloseRequested += (_, _) => closed++;

        await vm.ActivateCommand.Execute("with-parameter").FirstAsync();
        await vm.ActivateCommand.Execute("legacy").FirstAsync();

        Assert.AreSequenceEqual([("files.remoteEditDrafts.reupload", "0a1b2c3d")], parameterized);
        Assert.AreSequenceEqual(["files.remoteEditDrafts.open"], plain, "不带参数的旧消息照旧走命令注册表");
        Assert.AreEqual(2, closed);

        // 带参数的那一路没挂:什么都不做,也不拿同名命令顶上
        plain.Clear();
        var withoutParameterized = new NotificationPanelViewModel(center, id => { plain.Add(id); return true; });
        await withoutParameterized.ActivateCommand.Execute("with-parameter").FirstAsync();
        Assert.IsEmpty(plain);
    }

    /// <summary>
    /// 第二个动作:点了标已读、带着参数执行、收起面板;只认站内命令(外链一律不开),
    /// 主动作是外链(那一行左端摆着主机名)或者没有主动作时不显示。
    /// </summary>
    [TestMethod]
    public async Task ActivateSecondary_RunsTheSecondCommandWithItsParameter_AndOnlyInAppOnes()
    {
        NotificationItem draft = new()
        {
            Id = "remote-edit-draft:0a1b2c3d",
            Kind = NotificationKind.System,
            Title = "草稿",
            PublishedAt = DateTime.UtcNow,
            Link = new() { Label = "重新上传", CommandId = "files.remoteEditDrafts.reupload", CommandParameter = "0a1b2c3d" },
            SecondaryLink = new() { Label = "打开所在文件夹", CommandId = "files.remoteEditDrafts.open", CommandParameter = "0a1b2c3d" }
        };
        NotificationItem urlSecondary = new()
        {
            Id = "url-secondary",
            Kind = NotificationKind.News,
            Title = "外链不能当第二个动作",
            PublishedAt = DateTime.UtcNow,
            Link = new() { Label = "查看", CommandId = "app.settings.about" },
            SecondaryLink = new() { Label = "阅读", Url = "https://example.com" }
        };
        NotificationItem externalPrimary = new()
        {
            Id = "external-primary",
            Kind = NotificationKind.News,
            Title = "主动作是外链",
            PublishedAt = DateTime.UtcNow,
            Link = new() { Label = "阅读全文", Url = "https://example.com/post" },
            SecondaryLink = new() { Label = "查看", CommandId = "app.settings.about" }
        };
        NotificationItem noPrimary = new()
        {
            Id = "no-primary",
            Kind = NotificationKind.News,
            Title = "没有主动作",
            PublishedAt = DateTime.UtcNow,
            SecondaryLink = new() { Label = "查看", CommandId = "app.settings.about" }
        };
        NotificationCenter center = await SeededCenterAsync(draft, urlSecondary, externalPrimary, noPrimary);
        var parameterized = new List<(string Id, string Parameter)>();
        var plain = new List<string>();
        var urls = new List<string>();
        int closed = 0;
        var vm = new NotificationPanelViewModel(center,
            id => { plain.Add(id); return true; },
            url => { urls.Add(url); return Task.CompletedTask; },
            parameterizedCommandInvoker: (id, parameter) => { parameterized.Add((id, parameter)); return true; });
        vm.CloseRequested += (_, _) => closed++;

        NotificationItemViewModel row = vm.Items.Single(item => item.Id == draft.Id);
        Assert.IsTrue(row.HasSecondaryLink);
        Assert.AreEqual("打开所在文件夹", row.SecondaryLinkLabel);
        Assert.IsFalse(vm.Items.Single(item => item.Id == "url-secondary").HasSecondaryLink, "第二个动作只认站内命令");
        Assert.IsFalse(vm.Items.Single(item => item.Id == "external-primary").HasSecondaryLink, "主动作是外链时左端是主机名");
        Assert.IsFalse(vm.Items.Single(item => item.Id == "no-primary").HasSecondaryLink);

        await vm.ActivateSecondaryCommand.Execute(draft.Id).FirstAsync();

        Assert.AreSequenceEqual([("files.remoteEditDrafts.open", "0a1b2c3d")], parameterized, "点第二个动作,不跑主动作");
        Assert.IsTrue(center.Items.Single(item => item.Id == draft.Id).IsRead);
        Assert.AreEqual(1, closed);

        foreach (string id in new[] { "url-secondary", "external-primary", "no-primary" })
        {
            await vm.ActivateSecondaryCommand.Execute(id).FirstAsync();
        }
        Assert.IsEmpty(plain);
        Assert.IsEmpty(urls);
        Assert.AreEqual(1, closed);
    }

    /// <summary>非 https 的外链一律不开 —— 这是远端来的地址落地前的最后一道闸。</summary>
    [TestMethod]
    public async Task Activate_RefusesNonHttpsUrl()
    {
        NotificationCenter center = await SeededCenterAsync(Item("plain", new()
        {
            Label = "查看",
            Url = "http://example.com"
        }));
        var openedUrls = new List<string>();
        var vm = new NotificationPanelViewModel(center, null, url =>
        {
            openedUrls.Add(url);
            return Task.CompletedTask;
        });

        await vm.ActivateCommand.Execute("plain").FirstAsync();

        Assert.IsEmpty(openedUrls);
    }

    /// <summary>点开一条就把它标为已读,未读数跟着落。</summary>
    [TestMethod]
    public async Task Activate_MarksRead()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"), Item("b"));
        var vm = new NotificationPanelViewModel(center);
        Assert.AreEqual(2, vm.UnreadCount);

        await vm.ActivateCommand.Execute("a").FirstAsync();

        Assert.AreEqual(1, vm.UnreadCount);
        Assert.IsFalse(vm.Items.Single(item => item.Id == "a").IsUnread);
    }

    /// <summary>没有去处的消息点了也不报错,只是标记已读。</summary>
    [TestMethod]
    public async Task Activate_HandlesItemWithoutLink()
    {
        NotificationCenter center = await SeededCenterAsync(Item("plain"));
        var vm = new NotificationPanelViewModel(center);

        await vm.ActivateCommand.Execute("plain").FirstAsync();

        Assert.AreEqual(0, vm.UnreadCount);
    }

    /// <summary>「只看未读」筛掉已读条目,取消勾选后恢复。</summary>
    [TestMethod]
    public async Task UnreadOnly_FiltersList()
    {
        NotificationCenter center = await SeededCenterAsync(Item("unread"), Item("read", read: true));
        var vm = new NotificationPanelViewModel(center);
        Assert.HasCount(2, vm.Items);

        vm.UnreadOnly = true;
        Assert.HasCount(1, vm.Items);
        Assert.AreEqual("unread", vm.Items[0].Id);

        vm.UnreadOnly = false;
        Assert.HasCount(2, vm.Items);
    }

    /// <summary>全部已读后,「只看未读」下列表为空并给出对应的空态文案。</summary>
    [TestMethod]
    public async Task MarkAllRead_EmptiesUnreadFilter()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"), Item("b"));
        var vm = new NotificationPanelViewModel(center) { UnreadOnly = true };

        await vm.MarkAllReadCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.IsEmpty);
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_EmptyUnread"), vm.EmptyHint);
    }

    /// <summary>删除与清空要反映到列表上。</summary>
    [TestMethod]
    public async Task Remove_And_Clear_UpdateList()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"), Item("b"));
        var vm = new NotificationPanelViewModel(center);

        await vm.RemoveCommand.Execute("a").FirstAsync();
        Assert.HasCount(1, vm.Items);

        await vm.ClearCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.IsEmpty);
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_Empty"), vm.EmptyHint);
    }

    /// <summary>外链条目要把主机名摆出来,让用户在点之前就知道会被带去哪个站点。</summary>
    [TestMethod]
    public void ItemViewModel_ExposesLinkHost_ForExternalLinksOnly()
    {
        var external = new NotificationItemViewModel(Item("ext", new() { Label = "读全文", Url = "https://news.example.com/a" }));
        var inApp = new NotificationItemViewModel(Item("in", new() { Label = "查看", CommandId = "app.settings.about" }));

        Assert.AreEqual("news.example.com", external.LinkHost);
        Assert.IsTrue(external.HasLinkHost);
        Assert.IsNull(inApp.LinkHost, "站内跳转没有外部主机可显示。");
        Assert.IsFalse(inApp.HasLinkHost);
    }

    /// <summary>
    /// 插件发来的消息标「插件」徽标与「来自 插件名」一行 —— 一眼分得出是插件说的,不是 VelaShell 自己说的;
    /// 宿主自己的消息没有来源行。点它走插件自己的命令(命令注册表里有占位时会惰性激活插件)。
    /// </summary>
    [TestMethod]
    public async Task ItemViewModel_ShowsPluginBadgeAndSource_AndActivatesThePluginCommand()
    {
        NotificationItem plugin = new()
        {
            Id = "plugin:acme.backup:failed",
            Kind = NotificationKind.Plugin,
            Title = "夜间备份失败",
            PublishedAt = DateTime.UtcNow,
            Source = "Acme Backup",
            Link = new() { Label = "", CommandId = "acme.backup.show-log" }
        };
        var vm = new NotificationItemViewModel(plugin);
        var host = new NotificationItemViewModel(Item("update:1"));

        Assert.AreEqual(Core.Resources.Strings.Get("Notify_KindPlugin"), vm.KindBadge);
        Assert.IsTrue(vm.HasSource);
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_Source", "Acme Backup"), vm.SourceText);
        Assert.Contains("Acme Backup", vm.SourceText);
        Assert.IsFalse(host.HasSource);
        Assert.AreEqual(string.Empty, host.SourceText);
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_LinkOpen"), vm.LinkLabel, "插件没给动作文案时用通用的「查看」");

        NotificationCenter center = await SeededCenterAsync(plugin);
        string? invoked = null;
        var panel = new NotificationPanelViewModel(center, id => { invoked = id; return true; });
        await panel.ActivateCommand.Execute(plugin.Id).FirstAsync();
        Assert.AreEqual("acme.backup.show-log", invoked);
    }

    /// <summary>
    /// 拖动后的位置落在 <c>ui-layout/notification-panel</c> —— 与文件传输提示同集合、
    /// 各占一个文档 Id。两个浮层写进同一个 Id 会互相踩,这条钉住 Id 不被写错。
    /// </summary>
    [TestMethod]
    public async Task PersistPanelPosition_WritesCurrentOffsetToStore()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"));
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var vm = new NotificationPanelViewModel(center, dataStore: store)
        {
            PanelOffsetX = 240,
            PanelOffsetY = -180
        };

        vm.PersistPanelPosition();

        // 断言本身返回 Task(方法是 async 的),丢弃它以免 CS4014 —— 调用记录是同步的。
        _ = store.Received(1).UpsertAsync(
            "ui-layout",
            "notification-panel",
            Arg.Is<PanelPosition>(p => p.OffsetX == 240 && p.OffsetY == -180),
            Arg.Any<CancellationToken>());
    }

    /// <summary>构造时从存储恢复上次的位置 —— 这就是「再次打开回到原有位置」。</summary>
    [TestMethod]
    public async Task Construction_RestoresPersistedPanelPosition()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"));
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<PanelPosition>("ui-layout", "notification-panel", Arg.Any<CancellationToken>())
             .Returns(new PanelPosition { OffsetX = 120, OffsetY = -64 });

        var vm = new NotificationPanelViewModel(center, dataStore: store);

        // 恢复是异步的,给它一次调度机会。
        await Task.Yield();
        await Task.Delay(50);

        Assert.AreEqual(120, vm.PanelOffsetX);
        Assert.AreEqual(-64, vm.PanelOffsetY);
    }

    /// <summary>没有存储(单元测试/精简宿主)时不该炸,位置退回默认锚点。</summary>
    [TestMethod]
    public async Task WithoutStore_PanelPositionDefaultsToAnchorAndPersistIsHarmless()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"));
        var vm = new NotificationPanelViewModel(center);

        Assert.AreEqual(0, vm.PanelOffsetX);
        Assert.AreEqual(0, vm.PanelOffsetY);
        vm.PersistPanelPosition();
    }

    /// <summary>相对时间按量级切换单位;源端时钟偏到未来时一律当作「刚刚」。</summary>
    [TestMethod]
    public void ItemViewModel_FormatsRelativeTime()
    {
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_TimeJustNow"), NotificationItemViewModel.FormatRelative(TimeSpan.FromSeconds(20)));
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_TimeJustNow"), NotificationItemViewModel.FormatRelative(TimeSpan.FromMinutes(-5)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeMinutes", 5), NotificationItemViewModel.FormatRelative(TimeSpan.FromMinutes(5)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeHours", 3), NotificationItemViewModel.FormatRelative(TimeSpan.FromHours(3)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeDays", 2), NotificationItemViewModel.FormatRelative(TimeSpan.FromDays(2)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeMonths", 2), NotificationItemViewModel.FormatRelative(TimeSpan.FromDays(70)));
    }
}
