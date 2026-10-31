using System.IO.Pipes;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Notifications;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.PluginHost;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件往消息中心发通知(SDK 2.1.3 的 <see cref="IUiApi.PostNotificationAsync" /> / <c>ui/notify</c>):
/// 宿主这边的校验、去重、静音、限频、映射与卸载清理,以及隔离模式经真实管道的往返。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class PluginNotificationTests
{
    private const string PluginId = "acme.backup";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IAppDataStore
    {
        private readonly Dictionary<string, object> _documents = [];

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_documents.GetValueOrDefault($"{collection}/{id}") as T);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(new List<T>());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            _documents[$"{collection}/{id}"] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _documents.Remove($"{collection}/{id}");
            return Task.CompletedTask;
        }
    }

    private static PluginNotification Note(string id, string title = "夜间备份失败") => new() { Id = id, Title = title };

    /// <summary>
    /// 一条插件消息进消息中心的样子:id 带上插件前缀、种类是「插件」、轻重一一对应、来源是宿主按插件身份填的显示名、
    /// 动作走插件自己的命令;文案按纯文本收拾 —— 控制字符与双向文本控制符去掉,超长截断。
    /// </summary>
    [TestMethod]
    public async Task Post_MapsToAPluginMessageWithSourceSeverityAndAction()
    {
        var center = new NotificationCenter();
        var service = new PluginNotificationService(center, displayNameOf: id => id == PluginId ? "Acme‮ Backup" : null);

        PluginNotificationOutcome outcome = await service.PostAsync(PluginId, new PluginNotification
        {
            Id = "backup-failed:2026-10-24",
            Title = "备份\n失败‮gnp.exe",
            Body = "第一行\r\n第二行\u0007" + new string('x', 2000),
            Level = PluginNotificationLevel.Critical,
            ActionCommandId = PluginId + ".show-log",
            ActionLabel = "查看日志"
        });

        Assert.AreEqual(PluginNotificationOutcome.Posted, outcome);
        NotificationItem item = center.Items.Single();
        Assert.AreEqual("plugin:acme.backup:backup-failed:2026-10-24", item.Id);
        Assert.AreEqual(NotificationKind.Plugin, item.Kind);
        Assert.AreEqual(NotificationSeverity.Critical, item.Severity);
        Assert.AreEqual("Acme Backup", item.Source, "双向控制符去掉,来源那一行伪装不了");
        Assert.AreEqual("备份 失败gnp.exe", item.Title, "标题一行:换行变空格,控制符去掉");
        StringAssert.StartsWith(item.Body, "第一行\n第二行x");
        Assert.AreEqual(PluginNotification.MaxBodyLength + 1, item.Body!.Length, "正文截断到上限,带一个省略号");
        Assert.AreEqual(PluginId + ".show-log", item.Link!.CommandId);
        Assert.AreEqual("查看日志", item.Link.Label);
        Assert.IsNull(item.Link.Url, "插件消息没有网址这条路");
        Assert.IsFalse(item.IsRead);
    }

    /// <summary>别人的命令、宿主命令、只有文案没有动作:不合格,抛 ArgumentException,列表里什么都不进。</summary>
    [TestMethod]
    [DataRow("other.plugin.run", null)]
    [DataRow("settings.open", null)]
    [DataRow(null, "看一下")]
    public async Task Post_RejectsActionsThatAreNotThePluginsOwn(string? command, string? label)
    {
        var center = new NotificationCenter();
        var service = new PluginNotificationService(center);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(PluginId,
            new PluginNotification { Id = "x", Title = "t", ActionCommandId = command, ActionLabel = label }));

        Assert.IsEmpty(center.Items);
    }

    /// <summary>
    /// 先去重、后限频:同 id 重投是 AlreadyPosted(不新增、不把已读变回未读、不算频率);每插件 60 秒里至多 6 条新消息,
    /// 第七条 RateLimited 不抛异常;窗口滑过去又能发;别的插件有自己的额度。
    /// </summary>
    [TestMethod]
    public async Task Post_DeduplicatesBeforeRateLimiting_PerPluginSlidingWindow()
    {
        var clock = new ManualClock();
        var center = new NotificationCenter();
        var service = new PluginNotificationService(center, time: clock);

        for (int i = 0; i < PluginNotificationService.MaxPerMinute; i++)
        {
            Assert.AreEqual(PluginNotificationOutcome.Posted, await service.PostAsync(PluginId, Note("n" + i)));
            clock.Now += TimeSpan.FromSeconds(5);
        }
        await center.MarkReadAsync("plugin:acme.backup:n0");

        Assert.AreEqual(PluginNotificationOutcome.RateLimited, await service.PostAsync(PluginId, Note("n6")));
        Assert.AreEqual(PluginNotificationOutcome.AlreadyPosted, await service.PostAsync(PluginId, Note("n0", "换了标题")));
        Assert.IsTrue(center.Items.Single(i => i.Id == "plugin:acme.backup:n0").IsRead, "重投不把已读变回未读");
        Assert.AreEqual("夜间备份失败", center.Items.Single(i => i.Id == "plugin:acme.backup:n0").Title, "也不改内容");
        Assert.AreEqual(PluginNotificationOutcome.Posted, await service.PostAsync("other.plugin", Note("n6")), "额度按插件算");

        clock.Now += TimeSpan.FromSeconds(31);   // 最早那条已滑出 60 秒窗口
        Assert.AreEqual(PluginNotificationOutcome.Posted, await service.PostAsync(PluginId, Note("n6")));
        Assert.AreEqual(PluginNotificationOutcome.RateLimited, await service.PostAsync(PluginId, Note("n7")));
        Assert.HasCount(PluginNotificationService.MaxPerMinute + 2, center.Items);
    }

    /// <summary>
    /// <b>一个插件在消息中心里至多留 <see cref="PluginNotificationService.MaxRetainedPerPlugin" /> 条,超出丢它自己最旧的</b>;
    /// 别的插件的、宿主自己的都不动。
    /// <para>
    /// 限频管不住"攒得多":每分钟换 6 个新 id 地发(不撞限频),原先半小时就把 200 条的消息中心占满,
    /// 最旧的宿主消息 —— 只发一次的「本机加密密钥找不到」—— 被挤掉就再也不会回来。这里发 250 条、跨四十多分钟。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Post_KeepsAtMostACapPerPlugin_DroppingItsOwnOldest_AndNeverAHostMessage()
    {
        var clock = new ManualClock();
        var center = new NotificationCenter();
        var service = new PluginNotificationService(center, time: clock);
        await center.PublishAsync([new NotificationItem
        {
            Id = "master-key:missing:abc", Kind = NotificationKind.System, Title = "找不到本机加密密钥",
            PublishedAt = clock.Now.UtcDateTime.AddDays(-1)
        }]);
        Assert.AreEqual(PluginNotificationOutcome.Posted, await service.PostAsync("acme.quiet", Note("weekly")));

        for (int i = 0; i < 250; i++)
        {
            clock.Now += TimeSpan.FromSeconds(11);   // 每分钟不到 6 条:一条都不撞限频
            Assert.AreEqual(PluginNotificationOutcome.Posted, await service.PostAsync(PluginId, Note($"backup-done:{i}")));
        }

        string[] chatty = [.. center.Items.Where(item => item.Id.StartsWith("plugin:acme.backup:", StringComparison.Ordinal)).Select(item => item.Id)];
        Assert.HasCount(PluginNotificationService.MaxRetainedPerPlugin, chatty, "一个插件留下的条数超过了上限。");
        Assert.AreSequenceEqual(
            [.. Enumerable.Range(250 - PluginNotificationService.MaxRetainedPerPlugin, PluginNotificationService.MaxRetainedPerPlugin)
                .Select(i => $"plugin:acme.backup:backup-done:{i}")],
            chatty, SequenceOrder.InAnyOrder, "留下的应是它自己最新的那些。");
        Assert.Contains(item => item.Id == "master-key:missing:abc", center.Items, "插件消息挤掉了只发一次的宿主消息。");
        Assert.Contains(item => item.Id == "plugin:acme.quiet:weekly", center.Items, "一个插件刷屏挤掉了别的插件的消息。");
        Assert.HasCount(PluginNotificationService.MaxRetainedPerPlugin + 2, center.Items);
    }

    /// <summary>
    /// 静音:返回 Muted、不进列表、不算频率;静音跨重启保留(落在应用数据里);恢复之后照常发。
    /// 已经发进列表的消息不受静音影响。
    /// </summary>
    [TestMethod]
    public async Task Mute_IsPerPluginPersistentAndDoesNotCountTowardsTheRate()
    {
        var store = new MemoryStore();
        var center = new NotificationCenter();
        var service = new PluginNotificationService(center, store);
        Assert.AreEqual(PluginNotificationOutcome.Posted, await service.PostAsync(PluginId, Note("before")));

        await service.SetMutedAsync(PluginId, true);
        for (int i = 0; i < 10; i++)
        {
            Assert.AreEqual(PluginNotificationOutcome.Muted, await service.PostAsync(PluginId, Note("muted-" + i)));
        }
        Assert.HasCount(1, center.Items, "静音不删已经发进来的");
        Assert.IsTrue(await new PluginNotificationService(center, store).IsMutedAsync(PluginId), "下次启动仍是静音");
        Assert.IsFalse(await service.IsMutedAsync("other.plugin"));

        await service.SetMutedAsync(PluginId, false);
        for (int i = 0; i < PluginNotificationService.MaxPerMinute - 1; i++)
        {
            Assert.AreEqual(PluginNotificationOutcome.Posted, await service.PostAsync(PluginId, Note("after-" + i)),
                "静音期间被挡下的那些不占额度");
        }
    }

    /// <summary>
    /// 卸载时删掉这个插件发的消息与静音记录,别的插件、宿主自己的消息不动;
    /// 启动清扫把不在已装列表里的插件的消息一并删掉(插件目录在宿主没运行时被删掉也算卸载)。
    /// </summary>
    [TestMethod]
    public async Task ForgetAndSweep_RemoveOnlyTheUninstalledPluginsMessages()
    {
        var center = new NotificationCenter();
        var service = new PluginNotificationService(center, new MemoryStore());
        await service.PostAsync(PluginId, Note("a"));
        await service.PostAsync(PluginId, Note("b"));
        await service.PostAsync("acme.backup2", Note("c"));
        await service.PostAsync("gone.plugin", Note("d"));
        await center.PublishAsync([new NotificationItem { Id = "update:9.9.9", Kind = NotificationKind.Update, Title = "新版本", PublishedAt = DateTime.UtcNow }]);
        await service.SetMutedAsync(PluginId, true);

        await service.ForgetPluginAsync(PluginId);

        Assert.AreSequenceEqual(["plugin:acme.backup2:c", "plugin:gone.plugin:d", "update:9.9.9"], [.. center.Items.Select(i => i.Id)], SequenceOrder.InAnyOrder, "前缀带冒号:acme.backup 不会顺手删掉 acme.backup2 的");
        Assert.IsFalse(await service.IsMutedAsync(PluginId), "重装之后从头来过");
        Assert.IsFalse(service.HasMessages(PluginId));

        await service.SweepUninstalledAsync(new HashSet<string>(StringComparer.Ordinal) { "acme.backup2" });

        Assert.AreSequenceEqual(["plugin:acme.backup2:c", "update:9.9.9"], [.. center.Items.Select(i => i.Id)], SequenceOrder.InAnyOrder);
    }

    /// <summary>
    /// 隔离插件经真实管道发通知:握手宣告支持,PluginHost 侧先按 SDK 规则本地校验,宿主按连接绑定的插件身份投递 ——
    /// 消息里写什么都冒充不了别的插件;旧宿主(握手没宣告)直接 NotSupported,一条 RPC 都不发。
    /// </summary>
    [TestMethod]
    public async Task IsolatedPlugin_PostsThroughThePipeUnderTheConnectionsIdentity()
    {
        var center = new NotificationCenter();
        var service = new PluginNotificationService(center, displayNameOf: _ => "Acme Backup");
        await using Pair pair = await Pair.Open();
        using PluginContext context = Context(new NotifyingUi(PluginId, service));
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", PluginId, "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        Assert.IsTrue(hello!.SupportsNotifications);
        var ui = new PluginHostUi(PluginId, new CollectingLogger(), pair._plugin, hello.SupportsNotifications);

        Assert.AreEqual(PluginNotificationOutcome.Posted, await ui.PostNotificationAsync(Note("over-the-pipe")));
        Assert.AreEqual(PluginNotificationOutcome.AlreadyPosted, await ui.PostNotificationAsync(Note("over-the-pipe")));
        await Assert.ThrowsAsync<ArgumentException>(() => ui.PostNotificationAsync(
            new PluginNotification { Id = "spoof", Title = "t", ActionCommandId = "other.plugin.run" }));

        NotificationItem item = center.Items.Single();
        Assert.AreEqual("plugin:acme.backup:over-the-pipe", item.Id);
        Assert.AreEqual("Acme Backup", item.Source);

        int calls = 0;
        await using Pair legacy = await Pair.Open();
        legacy._host.SetRequestHandler((_, _, _) => { calls++; return Task.FromResult<object?>(null); });
        var old = new PluginHostUi(PluginId, new CollectingLogger(), legacy._plugin, supportsNotifications: false);
        Assert.IsFalse(old.SupportsNotifications);
        await Assert.ThrowsAsync<NotSupportedException>(() => old.PostNotificationAsync(Note("x")));
        Assert.AreEqual(0, calls);
    }

    /// <summary>宿主没有消息中心(界面能力不支持)时握手不宣告,插件那边按旧宿主处理。</summary>
    [TestMethod]
    public async Task HandshakeDoesNotAdvertiseNotificationsWhenTheHostCannotTakeThem()
    {
        await using Pair pair = await Pair.Open();
        using PluginContext context = Context(new FakeUi { SupportsNotifications = false });
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", PluginId, "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));

        Assert.IsFalse(hello!.SupportsNotifications);
    }

    /// <summary>与宿主进程内 PluginUiApi 同一个做法的最小界面能力:通知转给共用的服务,发送方是构造时绑定的插件。</summary>
    private sealed class NotifyingUi(string pluginId, PluginNotificationService service) : IUiApi
    {
        public bool SupportsNotifications => true;

        public Task<PluginNotificationOutcome> PostNotificationAsync(PluginNotification notification, CancellationToken cancellationToken = default)
            => service.PostAsync(pluginId, notification, cancellationToken);

        public Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Pair : IAsyncDisposable
    {
        internal required RpcConnection _host;
        internal required RpcConnection _plugin;

        internal static async Task<Pair> Open()
        {
            string name = PluginProcessClient.CreatePipeName();
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task waiting = server.WaitForConnectionAsync();
            await client.ConnectAsync(5000);
            await waiting;
            var pair = new Pair { _host = new(server), _plugin = new(client) };
            pair._host.Start();
            pair._plugin.Start();
            return pair;
        }

        public async ValueTask DisposeAsync()
        {
            await _plugin.DisposeAsync();
            await _host.DisposeAsync();
        }
    }

    private static PluginContext Context(IUiApi ui) => new()
    {
        PluginId = PluginId,
        PluginVersion = "1",
        DataDirectory = Path.GetTempPath(),
        Host = new TestHostInfo(),
        Log = new CollectingLogger(),
        Storage = new InMemoryStorage(),
        TimeSeries = new InMemoryTimeSeries(),
        Sessions = new FakeSessions(),
        RemoteFs = new FakeRemoteFs(),
        RemoteExec = new FakeRemoteExec(),
        RemoteTunnel = new FakeRemoteTunnel(),
        TerminalView = new FakeTerminalViewApi(),
        Commands = new RecordingCommands(),
        Events = new PluginEventHub(new CollectingLogger(), null, null, null),
        Theme = new StaticHostTheme(),
        Ui = ui,
        Secrets = new FakeSecrets(),
        Clipboard = new FakeClipboard(),
        Terminal = new FakeTerminal(),
        Protocols = new UnavailableProtocols(),
        Workspaces = new UnavailableWorkspaces(),
        Shutdown = CancellationToken.None,
    };
}
