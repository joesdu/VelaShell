using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Core.Sync;
using VelaShell.Tests.TestSupport;

namespace VelaShell.Tests.Services;

/// <summary>
/// 自动同步的接线(<see cref="App.WireAutoSync" />):哪些本机改动给主载荷打「本机改过」、哪些只排一次同步。
/// </summary>
/// <remarks>
/// 布局模板不在主载荷里(按 Id 逐条合并)。存一个模板若也给主载荷打上标记,双端都改的那一轮按时间比,
/// 刚存过模板的这台就成了「较新」,把自己旧的设置与连接整份推上去、盖掉别的设备刚改的。
/// </remarks>
[TestClass]
[TestCategory("Sync")]
public sealed class AutoSyncWiringTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AutoSyncWiringTests).Assembly);

    private static LayoutTemplate Template(string name) => new()
    {
        Name = name,
        Root = new() { Documents = [new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = name }] },
    };

    /// <summary>同步服务替身:每读一次同步配置就放行一次 <paramref name="reads" />,每同步一次放行一次 <paramref name="syncs" />。</summary>
    private static IGistSyncService Sync(SyncSettings config, SemaphoreSlim reads, SemaphoreSlim? syncs = null)
    {
        IGistSyncService sync = Substitute.For<IGistSyncService>();
        sync.GetSyncSettingsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            reads.Release();
            return Task.FromResult(config);
        });
        sync.SyncNowAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            syncs?.Release();
            return Task.FromResult(new SyncResult(SyncAction.UpToDate, true, "ok"));
        });
        return sync;
    }

    private static async Task WaitAsync(SemaphoreSlim signal, string what) =>
        Assert.IsTrue(await signal.WaitAsync(Timeout), $"等不到{what}");

    [TestMethod]
    public Task ALayoutTemplateChange_DoesNotMarkTheMainPayloadAsChanged() => Session.RunOnUiAsync(async () =>
    {
        var reads = new SemaphoreSlim(0);
        IGistSyncService sync = Sync(new SyncSettings(), reads);
        var templates = new LayoutTemplateStore(Substitute.For<IAppDataStore>());
        new App().WireAutoSync(sync, null, null, layoutTemplates: templates);
        await WaitAsync(reads, "启动那次读配置");

        await templates.SaveAsync(Template("rack-A"));
        // 排进来的那次同步先(按原先的写法)打标记、再读配置:读到配置时标记要打早就打了。
        await WaitAsync(reads, "模板改动排的那次同步");

        await sync.DidNotReceive().MarkLocalChangedAsync(Arg.Any<CancellationToken>());
    });

    [TestMethod]
    public Task ASettingsSave_StillMarksTheMainPayloadAsChanged() => Session.RunOnUiAsync(async () =>
    {
        var reads = new SemaphoreSlim(0);
        IGistSyncService sync = Sync(new SyncSettings(), reads);
        ISettingsService settings = Substitute.For<ISettingsService>();
        new App().WireAutoSync(sync, settings, null);
        await WaitAsync(reads, "启动那次读配置");

        settings.SettingsSaved += Raise.Event<Action<AppSettings>>(new AppSettings());
        await WaitAsync(reads, "设置保存排的那次同步");

        await sync.Received(1).MarkLocalChangedAsync(Arg.Any<CancellationToken>());
    });

    [TestMethod]
    public Task ALayoutTemplateChange_StillQueuesTheDebouncedSync() => Session.RunOnUiAsync(async () =>
    {
        var reads = new SemaphoreSlim(0);
        var syncs = new SemaphoreSlim(0);
        IGistSyncService sync = Sync(new SyncSettings { Enabled = true, AutoSync = true }, reads, syncs);
        var templates = new LayoutTemplateStore(Substitute.For<IAppDataStore>());
        new App().WireAutoSync(sync, null, null, layoutTemplates: templates);
        await WaitAsync(syncs, "启动那次同步");

        await templates.SaveAsync(Template("rack-A"));

        // 防抖 5 秒之后照样同步一次:模板在同步的核对 / 拉取那两条路上合并并推上去。
        await WaitAsync(syncs, "模板改动排的那次同步");
        await sync.DidNotReceive().MarkLocalChangedAsync(Arg.Any<CancellationToken>());
    });
}
