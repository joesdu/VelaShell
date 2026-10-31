using System.Globalization;
using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class MainWindowViewModelTests
{
    static MainWindowViewModelTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    [TestMethod]
    [TestCategory("UI")]
    public void MainWindowViewModel_Initializes_WithAllSubViewModels()
    {
        var vm = new MainWindowViewModel();

        Assert.IsNotNull(vm.Sidebar);
        Assert.IsNotNull(vm.Layout);
        Assert.IsNotNull(vm.StatusBar);
    }

    /// <summary>
    /// 消息中心带参数的动作:「重新上传」交给草稿重传那一套、结果用浮层说清楚(草稿已不在 → 警示,配置已删 → 错误);
    /// 不认识的 id 不执行(面板据此不收起,也不会拿同名的无参命令顶上)。
    /// 跑在 headless UI 线程上:正式程序里这一路就在 UI 线程上(浮层的计时器绑在 UI 调度器上),
    /// 放在线程池上跑会让 <c>Dispatcher.UIThread</c> 绑到一条不泵消息的线程上,拖累后面靠 Post 投浮层的用例。
    /// </summary>
    [TestMethod]
    [TestCategory("UI")]
    [TestCategory("ExternalEdit")]
    public Task ParameterizedNotificationCommands_RouteTheDraftReupload_AndReportTheOutcome() =>
        Avalonia.Headless.HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MainWindowViewModelTests).Assembly).RunOnUiAsync(async () =>
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vela-mw-reupload-{Guid.NewGuid():N}")).FullName;
        try
        {
            string draft = Directory.CreateDirectory(Path.Combine(root, "0a1b2c3d")).FullName;
            await File.WriteAllTextAsync(Path.Combine(draft, "app.conf"), "edited");
            RemoteEditDrafts.Write(draft, new RemoteEditMarker(RemoteEditDrafts.Dirty, "app.conf", "/etc/app.conf", "prod-web", Guid.NewGuid(), 0, default));
            var vm = new MainWindowViewModel
            {
                DraftReuploader = new RemoteEditDraftReuploader(root, _ => Task.FromResult<SessionProfile?>(null),
                    _ => Task.FromResult<IRemoteEditDraftTarget?>(null), null, null, () => [])
            };

            Assert.IsFalse(vm.InvokeParameterizedNotificationCommand("app.settings.about", "0a1b2c3d"), "不认识的带参动作不执行");

            await vm.ReuploadRemoteEditDraftAsync("0a1b2c3d");
            ToastViewModel profileMissing = vm.Toasts.Toasts.First();
            Assert.AreEqual(ToastSeverity.Error, profileMissing.Severity);
            Assert.AreEqual(Strings.Format("RemoteEditDraft_ProfileMissing", "app.conf", Path.Combine(draft, "app.conf")), profileMissing.Message);

            await vm.ReuploadRemoteEditDraftAsync("deadbeef");
            ToastViewModel gone = vm.Toasts.Toasts.First();
            Assert.AreEqual(ToastSeverity.Warning, gone.Severity);
            Assert.AreEqual(Strings.Get("RemoteEditDraft_Gone"), gone.Message);
            Assert.IsTrue(Directory.Exists(draft), "配置已删:草稿留着");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    [TestMethod]
    [TestCategory("UI")]
    public void OpenTunnelPanel_WithWorkflowService_OpensPanel()
    {
        ITunnelWorkflowService workflow = Substitute.For<ITunnelWorkflowService>();
        var vm = new MainWindowViewModel(tunnelWorkflowService: workflow);

        vm.OpenTunnelPanel();

        Assert.IsNotNull(vm.TunnelPanel);
        Assert.IsTrue(vm.IsTunnelPanelOpen);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void OpenTunnelPanel_WithOnlyCoreTunnelService_DoesNotBypassWorkflow()
    {
        ITunnelService tunnelService = Substitute.For<ITunnelService>();
        var vm = new MainWindowViewModel(tunnelService: tunnelService);

        vm.OpenTunnelPanel();

        Assert.IsNull(vm.TunnelPanel);
        Assert.IsFalse(vm.IsTunnelPanelOpen);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ToolsFilesCommand_IsRegistered_WithShortcut()
    {
        var vm = new MainWindowViewModel();

        CommandDescriptor? command = vm.Commands.Find("tools.files");

        Assert.IsNotNull(
            command,
            "SFTP file manager command must be wired so the panel can be opened."
        );
        // 键位归键位表管(设置里可改):命令面板显示的是键位表当前生效的那一个。
        Assert.AreEqual("Ctrl+Shift+F", ShortcutKeymap.Default.HintFor(command.Id, command.Shortcut));
    }

    /// <summary>「发送 Break」登记在命令面板里;没有活动的 SSH 会话时点不动(本地终端、插件终端的流不支持它)。</summary>
    [TestMethod]
    [TestCategory("UI")]
    public void SendBreakCommand_IsRegistered_AndNeedsAnSshSession()
    {
        var vm = new MainWindowViewModel();

        CommandDescriptor? command = vm.Commands.Find("session.break");

        Assert.IsNotNull(command);
        Assert.AreEqual(Strings.Get("Cmd_SendBreak"), command.Title);
        Assert.IsFalse(command.IsEnabled, "没有活动会话时不该能点");
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ToggleFileBrowser_RequiresConnectedSsh_AndKeepsStatePerTab()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new MainWindowViewModel(sftpService: sftp);

        Assert.IsFalse(vm.FileBrowser.IsVisible);
        Assert.IsFalse(vm.CanToggleFileBrowser);
        vm.ToggleFileBrowser();
        Assert.IsFalse(
            vm.FileBrowser.IsVisible,
            "No active SSH terminal must keep the panel closed."
        );

        var first = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = new() { Name = "one", Host = "one.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        var firstDocument = new TerminalDocument(first);
        vm.Layout.AddDocument(firstDocument);
        Assert.IsTrue(vm.CanToggleFileBrowser);
        Assert.IsTrue(
            vm.FileBrowser.IsVisible,
            "默认设置(自动打开)下,新标签的面板自动展示。"
        );

        vm.ToggleFileBrowser();
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        var second = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = new() { Name = "two", Host = "two.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        var secondDocument = new TerminalDocument(second);
        vm.Layout.AddDocument(secondDocument);
        Assert.IsTrue(
            vm.FileBrowser.IsVisible,
            "第一个标签隐藏面板不影响新标签:新标签按设置自动打开。"
        );

        // 面板开关是每个标签自己的状态:切回隐藏过的标签保持隐藏,切回打开的恢复显示。
        // 切标签走工作区(Q-02 之后它是唯一事实来源),ActiveTerminalTab 由它派生。
        vm.Layout.ActivateDocument(firstDocument);
        Assert.IsFalse(vm.FileBrowser.IsVisible, "切回曾隐藏面板的标签:面板保持隐藏。");
        vm.Layout.ActivateDocument(secondDocument);
        Assert.IsTrue(vm.FileBrowser.IsVisible, "切回面板开着的标签:面板自动恢复显示。");

        second.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsFalse(vm.CanToggleFileBrowser);

        var local = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            LocalShell = new("pwsh", "PowerShell", "pwsh.exe"),
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.Layout.AddDocument(new TerminalDocument(local));
        Assert.IsFalse(vm.CanToggleFileBrowser);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void ConnectedStateChange_AddsTargetAndEnablesCurrentTerminalExecution()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var library = new QuickCommandsViewModel(repository);
        var vm = new MainWindowViewModel(quickCommands: library);
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        var tab = new TerminalTabViewModel(emulator)
        {
            Profile = new() { Name = "server", Host = "server.example" },
        };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        QuickCommandRunnerViewModel runner = vm.Sidebar.QuickCommands!;
        Assert.IsEmpty(runner.Targets);

        tab.ConnectionStatus = SessionStatus.Connected;

        Assert.HasCount(1, runner.Targets);
        Assert.AreEqual(tab.Id, runner.Targets[0].Id);
        Assert.IsTrue(runner.CanRun);
        runner.SendCommand.Execute(library.AllCommands[0]).Subscribe();
        emulator.Received(1).WriteInput(Arg.Any<byte[]>());

        tab.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsEmpty(runner.Targets);
        Assert.IsFalse(runner.CanRun);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void QuickCommandExecution_RequestsFocusForActiveTerminal()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var library = new QuickCommandsViewModel(repository);
        var vm = new MainWindowViewModel(quickCommands: library);
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        var tab = new TerminalTabViewModel(emulator) { ConnectionStatus = SessionStatus.Connected };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        bool focusRequested = false;
        vm.TerminalFocusRequested += (_, _) => focusRequested = true;

        vm.Sidebar.QuickCommands!.SendCommand.Execute(library.AllCommands[0]).Subscribe();

        Assert.IsTrue(focusRequested);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_JoinPauseLeave_UpdatesTabState()
    {
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator());

        tab.JoinSyncChannel(SyncInputChannel.A);
        Assert.IsTrue(tab.IsInSyncChannel);
        Assert.AreEqual("A", tab.SyncChannelLetter);

        tab.ToggleSyncPauseCommand.Execute().Subscribe();
        Assert.IsTrue(tab.IsSyncPaused);

        // 改挂新频道时清除暂停态。
        tab.JoinSyncChannel(SyncInputChannel.B);
        Assert.AreEqual("B", tab.SyncChannelLetter);
        Assert.IsFalse(tab.IsSyncPaused);

        tab.LeaveSyncChannelCommand.Execute().Subscribe();
        Assert.IsFalse(tab.IsInSyncChannel);
        Assert.AreEqual(string.Empty, tab.SyncChannelLetter);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_CloseChannel_RemovesAllChannelMembersOnly()
    {
        var vm = new MainWindowViewModel();
        var first = new TerminalTabViewModel(FakeTerminal.Emulator());
        var second = new TerminalTabViewModel(FakeTerminal.Emulator());
        var other = new TerminalTabViewModel(FakeTerminal.Emulator());
        vm.Layout.AddDocument(new TerminalDocument(first));
        vm.Layout.AddDocument(new TerminalDocument(second));
        vm.Layout.AddDocument(new TerminalDocument(other));
        first.JoinSyncChannel(SyncInputChannel.A);
        second.JoinSyncChannel(SyncInputChannel.A);
        other.JoinSyncChannel(SyncInputChannel.B);

        first.CloseSyncChannelCommand.Execute().Subscribe();

        Assert.IsFalse(first.IsInSyncChannel);
        Assert.IsFalse(second.IsInSyncChannel);
        Assert.IsTrue(other.IsInSyncChannel);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_RemovedTab_LeavesChannel()
    {
        var vm = new MainWindowViewModel();
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator());
        vm.Layout.AddDocument(new TerminalDocument(tab));
        tab.JoinSyncChannel(SyncInputChannel.C);

        vm.Layout.RemoveDocument(vm.Layout.AllDocuments().OfType<TerminalDocument>().First(d => ReferenceEquals(d.Terminal, tab)));

        Assert.IsFalse(tab.IsInSyncChannel);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_ForwardedInput_BypassesPeerEmulatorInputEvents()
    {
        var vm = new MainWindowViewModel();
        ITerminalEmulator firstEmulator = FakeTerminal.Emulator();
        ITerminalEmulator secondEmulator = FakeTerminal.Emulator();
        var first = new TerminalTabViewModel(firstEmulator)
        {
            ConnectionStatus = SessionStatus.Connected,
        };
        var second = new TerminalTabViewModel(secondEmulator)
        {
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.Layout.AddDocument(new TerminalDocument(first));
        vm.Layout.AddDocument(new TerminalDocument(second));
        first.JoinSyncChannel(SyncInputChannel.A);
        second.JoinSyncChannel(SyncInputChannel.A);

        firstEmulator.TypedInput += Raise.Event<Action<byte[]>>("ls"u8.ToArray());

        // 转发必须直写桥(SendRaw),不得经接收端终端控件的输入 API——否则会二次
        // 触发 TypedInput(转发回环)并驱动非焦点标签的命令补全弹层。
        secondEmulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        secondEmulator.DidNotReceive().WriteTextInput(Arg.Any<string>());
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task SettingsSaved_ReappliesScrollbackToOpenTabs()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        var vm = new MainWindowViewModel(settingsService: settingsService);

        var tab = new TerminalTabViewModel(emulator);
        vm.Layout.AddDocument(new TerminalDocument(tab));

        // Saving settings must re-apply live values to already-open terminals (#3/#15/#21).
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { ScrollbackLines = 88_000 }
        );

        // The re-apply is marshalled through RxApp.MainThreadScheduler, which other test
        // classes may have initialized to an asynchronous scheduler — allow it to land.
        SpinWait.SpinUntil(() => emulator.ScrollbackLines == 88_000, TimeSpan.FromSeconds(5));
        Assert.AreEqual(88_000, emulator.ScrollbackLines);

        await tab.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("Startup")]
    public void PrepareForStartup_AppliesLayoutStateBeforeTheWindowIsShown()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        settingsService.GetStateAsync().Returns(Task.FromResult(new AppState { SidebarCollapsed = true }));
        var vm = new MainWindowViewModel(settingsService: settingsService);
        var settings = new AppSettings { Appearance = new() { MultiRowTabs = true } };

        vm.PrepareForStartup(settings);

        Assert.IsTrue(vm.Sidebar.IsCollapsed);
        Assert.IsTrue(vm.Layout.MultiRowTabs);
        settingsService.Received(1).GetStateAsync();
    }

    /// <summary>
    /// 设置「标签栏位置」→ 停靠层:启动时在首帧之前灌进去,保存设置当场生效;只认 <c>bottom</c>。
    /// 外观页的即时预览走的是同一个 <see cref="MainWindowViewModel.ApplyTabBarPosition" />。
    /// </summary>
    [TestMethod]
    [TestCategory("Startup")]
    public void TabBarPosition_ReachesTheDockLayout_AtStartupAndOnSave()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        settingsService.GetStateAsync().Returns(Task.FromResult(new AppState()));
        var vm = new MainWindowViewModel(settingsService: settingsService);
        Assert.IsFalse(vm.Layout.TabsAtBottom, "出厂在顶部");

        vm.PrepareForStartup(new AppSettings { Appearance = new() { TabBarPosition = "bottom" } });
        Assert.IsTrue(vm.Layout.TabsAtBottom);

        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { Appearance = new() { TabBarPosition = "top" } }
        );
        SpinWait.SpinUntil(() => !vm.Layout.TabsAtBottom, TimeSpan.FromSeconds(5));
        Assert.IsFalse(vm.Layout.TabsAtBottom, "保存设置当场生效,不等重启");

        vm.ApplyTabBarPosition(new AppearanceOptions { TabBarPosition = "Bottom" });
        Assert.IsFalse(vm.Layout.TabsAtBottom, "与设置页下拉同一口径:只认 bottom,别的值按顶部");
    }

    /// <summary>
    /// 标签条上的「+」:先让那一格成为活动窗格,再走与 Ctrl+T 同一个新建连接对话框。
    /// </summary>
    [TestMethod]
    public void OpenNewTabInPane_ActivatesThatPane_ThenOpensTheNewConnectionDialog()
    {
        var vm = new MainWindowViewModel();
        var left = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()));
        var right = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()));
        vm.Layout.AddDocument(left);
        vm.Layout.AddDocument(right);
        vm.Layout.SplitDocument(right, DockOrientation.Horizontal);
        Assert.AreSame(right, vm.Layout.ActiveDocument);
        int requested = 0;
        vm.NewConnectionDialog = () =>
        {
            requested++;
            return Task.CompletedTask;
        };

        vm.OpenNewTabInPane(vm.Layout.PrimaryGroup);

        Assert.AreSame(left, vm.Layout.ActiveDocument, "按下「+」的那一格成了活动窗格");
        Assert.AreEqual(1, requested, "打开新建连接");
        Assert.IsTrue(vm.Commands.Execute("session.new"));
        Assert.AreEqual(2, requested, "与 Ctrl+T 是同一个对话框");
    }

    /// <summary>
    /// 「+」的精确落点:布局里另有一块空窗格时,连出来的标签照样落进按下「+」的那一格
    /// (维护者 2026-10-25 定:按下的那格优先于空窗格)。新建连接对话框与连接流程一个参数都没加 ——
    /// 票据顺着异步上下文传到连接流程里建标签的那一下。
    /// </summary>
    [TestMethod]
    public async Task OpenNewTabInPane_TheNewTabLandsInThePressedPane_EvenWithAnEmptyPaneElsewhere()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SshSession>(new Core.Ssh.VelaSshConnectionException("连不上")));
        var vm = new MainWindowViewModel(workflow, Substitute.For<Core.Ssh.ISshConnectionService>(), FakeTerminal.Emulator);
        var left = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()));
        var right = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()));
        vm.Layout.AddDocument(left);
        vm.Layout.AddDocument(right);
        vm.Layout.SplitDocument(right, DockOrientation.Horizontal);
        // 再拆一次只剩一个标签的右格:原地留下一块空窗格(默认规则会把新标签放进它)。
        vm.Layout.SplitDocument(right, DockOrientation.Vertical);
        DockGroup empty = vm.Layout.AllGroups().Single(g => g.Documents.Count == 0);
        DockGroup pressed = vm.Layout.FindGroup(left)!;
        SessionProfile profile = new() { Name = "web", Host = "web.example.com", Username = "root", AuthMethod = AuthMethod.Password, Password = "x" };
        vm.NewConnectionDialog = async () =>
        {
            // 对话框是用户在点:流程真的让出过一次,票据还得认得出来。
            await Task.Yield();
            await vm.TryConnectProfileAsync(profile);
        };

        await vm.OpenNewTabInPaneAsync(pressed);

        TerminalTabViewModel tab = vm.TerminalTabs.Single(t => t.Profile?.Host == profile.Host);
        DockDocument document = vm.Layout.AllDocuments().Single(d => d is TerminalDocument { Terminal: var t } && ReferenceEquals(t, tab));
        Assert.AreSame(pressed, vm.Layout.FindGroup(document), "落进按下「+」的那一格");
        Assert.IsEmpty(empty.Documents, "空窗格不抢");

        // 流程走完票据就作废:之后再开的(比如 Ctrl+T)照默认规则落进空窗格。
        var later = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()));
        vm.Layout.AddDocument(later);
        Assert.AreSame(empty, vm.Layout.FindGroup(later));
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SettingsSaved_UpdatesQuickCommandsPanelVisibility()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            quickCommands: new QuickCommandsViewModel(repository)
        );

        Assert.IsFalse(vm.Sidebar.IsQuickCommandsVisible);
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { Appearance = new() { ShowQuickCommandsPanel = true } }
        );

        Assert.IsTrue(vm.Sidebar.IsQuickCommandsVisible);
    }

    /// <summary>
    /// 换语言后,C# 侧拼好存着的文案要跟着换:命令面板照着注册时取好的标题显示,
    /// X Server 按钮的提示、会话树的置顶菜单项与「未分组」也都是先取好的 ——
    /// {loc:Localize} 管不到它们(用户报:切英文后命令面板、X Server 提示仍是中文)。
    /// </summary>
    [TestMethod]
    [TestCategory("i18n")]
    public async Task LanguageChange_RefreshesTextComposedInCode()
    {
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo? defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        CultureInfo? defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            var localization = new LocalizationService();
            localization.SetLanguage("zh-CN");
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllGroupsAsync().Returns([]);
            repository.GetAllSessionsAsync()
                      .Returns([new SessionProfile { Id = Guid.NewGuid(), Name = "web", Host = "web.example.com", Username = "root" }]);
            var vm = new MainWindowViewModel(sessionRepository: repository, localization: localization);
            await vm.InitializeAsync();
            Assert.AreEqual("新建 SSH 连接", vm.Commands.Find("session.new")!.Title);
            Assert.AreEqual("启动 X Server", vm.XServer.ToolTip);
            SessionTreeNodeViewModel session = vm.Sidebar.SessionTree!.Nodes.Single();
            var raised = new List<string>();
            session.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

            localization.SetLanguage("en");

            CommandDescriptor command = vm.Commands.Find("session.new")!;
            Assert.AreEqual("New SSH Connection", command.Title);
            Assert.AreEqual("Session", command.Category);
            Assert.AreEqual("Start X Server", vm.XServer.ToolTip);
            Assert.Contains(nameof(SessionTreeNodeViewModel.PinToggleText), raised);
            Assert.Contains(nameof(SessionTreeNodeViewModel.StatusTagText), raised);
            Assert.AreEqual("Ungrouped", vm.Sidebar.SessionTree.GroupNodes.Single(node => node.Id == Guid.Empty).Name);
            Assert.AreEqual("No background tasks", vm.StatusBar.BackgroundTooltip);
        }
        finally
        {
            CultureInfo.CurrentUICulture = uiCulture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = defaultUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
        }
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task SyncProfilesApplied_RefreshesSessionTreeWithoutRestart()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        IGistSyncService syncService = Substitute.For<IGistSyncService>();
        var profiles = new List<SessionProfile>();
        repository.GetAllGroupsAsync().Returns([]);
        repository.GetAllSessionsAsync().Returns(_ => [.. profiles]);
        var vm = new MainWindowViewModel(
            sessionRepository: repository,
            gistSyncService: syncService
        );
        await vm.InitializeAsync();
        Assert.IsTrue(vm.Sidebar.SessionTree?.HasNoSessions);

        SessionProfile pulled = new()
        {
            Id = Guid.NewGuid(),
            Name = "pulled-server",
            Host = "pulled.example.com",
            Username = "root",
        };
        profiles.Add(pulled);

        syncService.ProfilesApplied += Raise.Event<EventHandler>(syncService, EventArgs.Empty);

        bool appeared = SpinWait.SpinUntil(
            () => vm.Sidebar.SessionTree?.Nodes.Any(node => node.Id == pulled.Id) == true,
            TimeSpan.FromSeconds(5)
        );
        Assert.IsTrue(appeared, "云同步拉取的连接应立即出现在会话树中。");
    }

    /// <summary>
    /// 云同步拉到「Webhook 告警开着却没有地址」的设置:消息中心里多一条带「前往安全审计设置」的提示;
    /// 同一份设置(同一个指纹)再来一次也只有一条(第二轮第 17 条)。
    /// </summary>
    [TestMethod]
    [TestCategory("Notifications")]
    public void SyncWebhookUrlMissing_PublishesOneNoticeToTheMessageCenter()
    {
        IGistSyncService syncService = Substitute.For<IGistSyncService>();
        var center = new Infrastructure.Notifications.NotificationCenter();
        var vm = new MainWindowViewModel(gistSyncService: syncService, notificationCenter: center);

        syncService.WebhookUrlMissing += Raise.Event<EventHandler<string>>(syncService, "0123456789abcdef");
        syncService.WebhookUrlMissing += Raise.Event<EventHandler<string>>(syncService, "0123456789abcdef");

        bool published = SpinWait.SpinUntil(
            () => center.Items.Any(item => item.Id.StartsWith(NotificationSources.SyncWebhookMissingIdPrefix, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        Assert.IsTrue(published, "拉到这种设置要在消息中心说一声");
        // 只看这一类:消息中心启动时还会投崩溃记录、外部编辑草稿之类,取决于跑测试的这台机器。
        NotificationItem notice = Assert.ContainsSingle(
            center.Items.Where(item => item.Id.StartsWith(NotificationSources.SyncWebhookMissingIdPrefix, StringComparison.Ordinal)),
            "同一份设置只留一条");
        Assert.AreEqual(NotificationSources.OpenSecuritySettingsCommandId, notice.Link?.CommandId);
        Assert.Contains(command => command.Id == NotificationSources.OpenSecuritySettingsCommandId, vm.Commands.All,
            "消息上的动作要有对应的命令,否则点了没反应");
    }

    [TestMethod]
    [TestCategory("UI")]
    public void SidebarViewModel_Initializes_WithCommands()
    {
        var vm = new SidebarViewModel();

        Assert.IsNotNull(vm.NotificationsCommand);
        Assert.IsNotNull(vm.RecentConnections);
    }

    [TestMethod]
    [TestCategory("Sidebar")]
    public async Task InitializeAsync_RestoresAndPersistsSidebarLayoutState()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());
        settingsService
            .GetStateAsync()
            .Returns(
                new AppState
                {
                    SidebarQuickCommandsExpanded = false,
                    SidebarQuickCommandsHeight = 245,
                    SidebarRecentConnectionsExpanded = true,
                    SidebarRecentConnectionsHeight = 275,
                }
            );
        var vm = new MainWindowViewModel(settingsService: settingsService);

        await vm.InitializeAsync();

        Assert.IsFalse(vm.Sidebar.QuickCommandsExpanded);
        Assert.AreEqual(245, vm.Sidebar.QuickCommandsHeight);
        Assert.IsTrue(vm.Sidebar.RecentConnectionsExpanded);
        Assert.AreEqual(275, vm.Sidebar.RecentConnectionsHeight);

        vm.Sidebar.QuickCommandsExpanded = true;
        vm.Sidebar.QuickCommandsHeight = 310;
        vm.Sidebar.RecentConnectionsExpanded = false;
        await vm.PersistSidebarStateAsync();

        await settingsService
            .Received()
            .SaveStateAsync(
                Arg.Is<AppState>(state =>
                    state.SidebarQuickCommandsExpanded
                    && state.SidebarQuickCommandsHeight == 310
                    && !state.SidebarRecentConnectionsExpanded
                    && state.SidebarRecentConnectionsHeight == 275
                )
            );
    }

    /// <summary>构造带设置桩的 VM 并完成初始化,用于验证 SFTP 面板按设置决定新标签初始状态。</summary>
    private static async Task<MainWindowViewModel> CreateInitializedVmAsync(
        bool autoOpenFileBrowser,
        ISettingsService? settingsServiceOut = null)
    {
        ISettingsService settingsService = settingsServiceOut ?? Substitute.For<ISettingsService>();
        settingsService
            .GetSettingsAsync()
            .Returns(new AppSettings
            {
                TerminalBehavior = new() { AutoOpenFileBrowser = autoOpenFileBrowser }
            });
        settingsService.GetStateAsync().Returns(new AppState());
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sftpService: Substitute.For<ISftpService>()
        );
        await vm.InitializeAsync();
        return vm;
    }

    private static async Task<MainWindowViewModel> CreateInitializedVmAsync(
        ISettingsService settingsService)
    {
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sftpService: Substitute.For<ISftpService>()
        );
        await vm.InitializeAsync();
        return vm;
    }

    private static TerminalTabViewModel CreateConnectedSshTab() =>
        new(FakeTerminal.Emulator())
        {
            Profile = new() { Name = "srv", Host = "srv.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_AutoOpenSettingOn_NewTabOpensBrowser()
    {
        MainWindowViewModel vm = await CreateInitializedVmAsync(autoOpenFileBrowser: true);

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));

        Assert.IsTrue(vm.FileBrowser.IsVisible, "开关开启:新连接的标签自动打开面板。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_AutoOpenSettingOff_NewTabKeepsBrowserHidden()
    {
        MainWindowViewModel vm = await CreateInitializedVmAsync(autoOpenFileBrowser: false);

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));

        Assert.IsFalse(vm.FileBrowser.IsVisible, "开关关闭:新连接的标签不自动打开面板。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_SettingSavedAtRuntime_AppliesToSubsequentNewTabs()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        MainWindowViewModel vm = await CreateInitializedVmAsync(
            autoOpenFileBrowser: false, settingsService);

        TerminalTabViewModel first = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(first));
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        // 运行中保存设置(开关改为开启):已开标签不受影响,之后新开的标签立即生效。
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = true } });

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));
        Assert.IsTrue(vm.FileBrowser.IsVisible, "保存后的开关状态对新标签立即生效。");

        vm.Activate(first);
        Assert.IsFalse(vm.FileBrowser.IsVisible, "已存在标签保持自己原有的面板状态。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_PanelClose_KeepsTabStateAndLeavesSettingUntouched()
    {
        var settingsService = new MemorySettingsService(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = true } });
        MainWindowViewModel vm = await CreateInitializedVmAsync(settingsService);

        TerminalTabViewModel first = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(first));
        Assert.IsTrue(vm.FileBrowser.IsVisible);

        // 模拟面板右上角关闭按钮:直接改当前面板实例的可见性。
        vm.FileBrowser.IsVisible = false;
        Assert.IsFalse(
            SpinWait.SpinUntil(
                () => !settingsService.Current.TerminalBehavior.AutoOpenFileBrowser,
                TimeSpan.FromMilliseconds(500)),
            "关面板只是该标签自己的状态,不能回写设置项(#377)。");
        Assert.AreEqual(0, settingsService.SaveCount, "关面板不应触发任何设置保存。");

        TerminalTabViewModel second = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(second));
        Assert.IsTrue(vm.FileBrowser.IsVisible, "设置没变,之后的新标签仍按设置自动打开。");

        vm.Activate(first);
        Assert.IsFalse(vm.FileBrowser.IsVisible, "切回关闭过面板的标签:保持关闭。");
        vm.Activate(second);
        Assert.IsTrue(vm.FileBrowser.IsVisible, "切回没动过的标签:保持打开。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_PanelOpen_LeavesSettingUntouched()
    {
        var settingsService = new MemorySettingsService(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = false } });
        MainWindowViewModel vm = await CreateInitializedVmAsync(settingsService);

        TerminalTabViewModel first = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(first));
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        // 标题栏「SFTP 文件浏览器」按钮。
        vm.ToggleFileBrowser();

        Assert.IsTrue(vm.FileBrowser.IsVisible);
        Assert.IsFalse(
            SpinWait.SpinUntil(
                () => settingsService.Current.TerminalBehavior.AutoOpenFileBrowser,
                TimeSpan.FromMilliseconds(500)),
            "开面板不能把设置项「连接后自动打开文件浏览器」打开(#377)。");
        Assert.AreEqual(0, settingsService.SaveCount, "开面板不应触发任何设置保存。");

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));
        Assert.IsFalse(vm.FileBrowser.IsVisible, "设置仍是关闭:之后的新标签不自动打开。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_ConnectingTab_HidesPreviousSessionPanelImmediately()
    {
        var settingsService = new MemorySettingsService(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = false } });
        MainWindowViewModel vm = await CreateInitializedVmAsync(settingsService);

        TerminalTabViewModel first = CreateConnectedSshTab();
        vm.Open(first);
        vm.ToggleFileBrowser();
        Assert.IsTrue(vm.FileBrowser.IsVisible, "手动打开:当前标签的面板展示。");

        // 新开一个标签:会话 id 要到握手完成才分配,这段时间下方不能还挂着
        // 上一个会话的文件面板(#385)。
        var connecting = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = new() { Name = "next", Host = "next.example" },
            ConnectionStatus = SessionStatus.Connecting,
        };
        vm.Open(connecting);

        Assert.IsFalse(
            vm.FileBrowser.IsVisible,
            "连接中的新标签必须立即收起上一个会话的面板,而不是等连上才消失(#385)。");
        Assert.AreEqual(
            Guid.Empty,
            vm.FileBrowser.SessionId,
            "连接中的新标签下方是空占位,不指向任何会话。");

        // 上一个标签自己的开关状态不受这次占位替换影响,切回去照旧展示。
        vm.Activate(first);
        Assert.IsTrue(vm.FileBrowser.IsVisible, "切回原标签:面板恢复展示。");
        Assert.AreEqual(first.SessionId, vm.FileBrowser.SessionId);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task ActiveTerminalTab_FollowsSavedProfileWhenSettingEnabled()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ISessionRepository sessionRepository = Substitute.For<ISessionRepository>();
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "server",
            Host = "server.example",
            Username = "root",
        };
        settingsService
            .GetSettingsAsync()
            .Returns(new AppSettings { General = new() { FollowActiveTerminalInExplorer = true } });
        settingsService.GetStateAsync().Returns(new AppState());
        sessionRepository.GetAllGroupsAsync().Returns([]);
        sessionRepository.GetAllSessionsAsync().Returns([profile]);
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sessionRepository: sessionRepository
        );
        await vm.InitializeAsync();

        vm.Layout.AddDocument(new TerminalDocument(
            new TerminalTabViewModel(FakeTerminal.Emulator()) { Profile = profile }
        ));

        Assert.AreEqual(profile.Id, vm.Sidebar.SessionTree?.SelectedNode?.Id);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task ActiveTerminalTab_DoesNotChangeTreeSelectionWhenSettingDisabled()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ISessionRepository sessionRepository = Substitute.For<ISessionRepository>();
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "server",
            Host = "server.example",
            Username = "root",
        };
        settingsService
            .GetSettingsAsync()
            .Returns(
                new AppSettings { General = new() { FollowActiveTerminalInExplorer = false } }
            );
        settingsService.GetStateAsync().Returns(new AppState());
        sessionRepository.GetAllGroupsAsync().Returns([]);
        sessionRepository.GetAllSessionsAsync().Returns([profile]);
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sessionRepository: sessionRepository
        );
        await vm.InitializeAsync();

        vm.Layout.AddDocument(new TerminalDocument(
            new TerminalTabViewModel(FakeTerminal.Emulator()) { Profile = profile }
        ));

        Assert.IsNull(vm.Sidebar.SessionTree?.SelectedNode);
    }

    [TestMethod]
    public async Task MultiRowTabs_FollowsTheSetting_AndTheTabMenuToggleIsPersisted()
    {
        var settingsService = new MemorySettingsService(
            new AppSettings { Appearance = new() { MultiRowTabs = true } });
        MainWindowViewModel vm = await CreateInitializedVmAsync(settingsService);

        Assert.IsTrue(vm.Layout.MultiRowTabs, "启动时把设置铺到布局上");
        Assert.AreEqual(0, settingsService.SaveCount, "把设置回灌到布局,不该绕回来再存一遍");

        // 标签右键菜单里的「多行显示标签页」改的就是这个开关。
        vm.Layout.MultiRowTabs = false;
        Assert.IsTrue(
            SpinWait.SpinUntil(() => settingsService.SaveCount == 1, TimeSpan.FromSeconds(5)),
            "菜单里切过之后要写回设置,设置页与下次启动都认这一个值");
        Assert.IsFalse(settingsService.Current.Appearance.MultiRowTabs);

        // 设置页保存 → 布局跟着变。
        var fromSettingsPage = new AppSettings { Appearance = new() { MultiRowTabs = true } };
        await settingsService.SaveSettingsAsync(fromSettingsPage);
        Assert.IsTrue(vm.Layout.MultiRowTabs);
        Assert.AreEqual(2, settingsService.SaveCount, "只有设置页那一次保存,回灌时不再多存");
    }

    private sealed class MemorySettingsService(AppSettings initial) : ISettingsService
    {
        private int _saveCount;

        public event Action<AppSettings>? SettingsSaved;

        public AppSettings Current { get; private set; } = initial;

        public int SaveCount => Volatile.Read(ref _saveCount);

        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Current);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            Current = settings;
            Interlocked.Increment(ref _saveCount);
            SettingsSaved?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<AppState> GetStateAsync() => Task.FromResult(new AppState());

        public Task SaveStateAsync(AppState state) => Task.CompletedTask;
    }
}
