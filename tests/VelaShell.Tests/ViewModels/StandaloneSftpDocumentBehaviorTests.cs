using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class StandaloneSftpDocumentBehaviorTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(StandaloneSftpDocumentBehaviorTests).Assembly);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task TryConnectProfileAsync_SftpProfile_DispatchesStandaloneFlowWithoutTerminal()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        int terminalFactoryCalls = 0;
        SessionProfile profile = CreateSftpProfile();

        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            () =>
            {
                terminalFactoryCalls++;
                return Substitute.For<ITerminalEmulator>();
            }
        );

        await vm.TryConnectProfileAsync(profile);

        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        Assert.AreEqual(0, terminalFactoryCalls, "SFTP must not create a terminal emulator.");
        Assert.IsEmpty(vm.TabBar.Tabs, "Standalone SFTP must not create a terminal tab.");
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task OpenSftpForProfileAsync_SftpProfile_UsesStandaloneDispatcher()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        SessionProfile profile = CreateSftpProfile();
        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            () => Substitute.For<ITerminalEmulator>()
        );

        await vm.OpenSftpForProfileAsync(profile);

        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        Assert.IsEmpty(vm.TabBar.Tabs, "Standalone SFTP must be represented by a document, not a terminal tab.");
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task OpenSftpForProfileAsync_SshProfile_DoesNotCreateShellTerminal()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        int terminalFactoryCalls = 0;
        SessionProfile profile = CreateSshProfile();
        SshSession session = new()
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password,
            },
            Status = SessionStatus.Connected,
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync(
                Arg.Any<string>(),
                Arg.Any<uint>(),
                Arg.Any<uint>(),
                Arg.Any<uint>(),
                Arg.Any<uint>(),
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()
            )
            .Returns(shellStream);

        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            () =>
            {
                terminalFactoryCalls++;
                return Substitute.For<ITerminalEmulator>();
            },
            sftpService: Substitute.For<ISftpService>()
        );

        await vm.OpenSftpForProfileAsync(profile);

        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        await sshClient.DidNotReceive().CreateShellStreamAsync(
            Arg.Any<string>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()
        );
        Assert.AreEqual(0, terminalFactoryCalls, "Explorer SFTP must not open a shell terminal first.");
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task OpenSftpCommand_SftpProfile_RaisesStandaloneOpenRequest()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        SessionProfile profile = CreateSftpProfile();
        repository.GetAllGroupsAsync().Returns([]);
        repository.GetAllSessionsAsync().Returns([profile]);
        var tree = new SessionTreeViewModel(repository);
        await tree.LoadCommand.Execute().FirstAsync();
        tree.SelectedNode = tree.Nodes.Single();
        SessionProfile? requested = null;
        tree.OpenSftpRequested += profile => requested = profile;

        await tree.OpenSftpCommand.Execute().FirstAsync();

        Assert.AreSame(profile, requested);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task SftpDocumentClose_WhenCallerWaitIsCancelled_StillDisconnectsEventually()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
            });
        var document = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions());
        using var cancellation = new CancellationTokenSource();

        Task wait = document.CloseAsync(cancellation.Token);
        await closeStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => wait);

        releaseClose.SetResult();
        await document.CloseAsync();
        await workflow.Received(1).DisconnectAsync(session.SessionId, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task SftpDocumentClose_WhenSerializerCloseFails_StillDisconnectsOnce()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("close failed")));
        var document = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => document.CloseAsync());
        await workflow.Received(1).DisconnectAsync(session.SessionId, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task CloseSftpDocumentAsync_WhenCloseCompletesOnWorker_UpdatesSessionTreeOnUiThread()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        repository.GetAllGroupsAsync().Returns([]);
        repository.GetAllSessionsAsync().Returns([profile]);
        ConfigureInitialLoad(sftp, session.SessionId);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int closeContinuationThread = 0;
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
                closeContinuationThread = Environment.CurrentManagedThreadId;
            });

        var vm = new MainWindowViewModel(
            workflow,
            sessionRepository: repository,
            sftpService: sftp);
        await vm.Sidebar.SessionTree!.LoadCommand.Execute().FirstAsync();
        SessionTreeNodeViewModel node = vm.Sidebar.SessionTree.Nodes.Single();
        vm.Sidebar.SessionTree.SetSessionStatus(profile.Id, SessionStatus.Connected);
        List<int> notificationThreads = [];
        void propertyChanged(object? _, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SessionTreeNodeViewModel.Status))
            {
                notificationThreads.Add(Environment.CurrentManagedThreadId);
            }
        }
        node.PropertyChanged += propertyChanged;

        var document = new SftpDocument(
            new SftpDocumentViewModel(
                profile,
                session,
                workflow,
                sftp,
                new TransferOptions()));
        MethodInfo closeMethod = typeof(MainWindowViewModel).GetMethod(
            "CloseSftpDocumentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            await _session.Dispatch(async () =>
            {
                int uiThread = Environment.CurrentManagedThreadId;
                var invocation = Task.Run(() => (Task)closeMethod.Invoke(vm, [document])!);
                await closeStarted.Task;
                await Task.Run(releaseClose.SetResult);
                await invocation;

                Assert.AreEqual(SessionStatus.Disconnected, node.Status);
                Assert.AreNotEqual(uiThread, closeContinuationThread);
                Assert.IsNotEmpty(notificationThreads);
                Assert.IsTrue(notificationThreads.All(thread => thread == uiThread));
            }, CancellationToken.None);
        }
        finally
        {
            node.PropertyChanged -= propertyChanged;
        }
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task CloseSftpDocumentAsync_WhenCloseFaultsOnWorker_UpdatesLastConnectionErrorOnUiThread()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        ConfigureInitialLoad(sftp, session.SessionId);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int closeContinuationThread = 0;
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
                closeContinuationThread = Environment.CurrentManagedThreadId;
                throw new InvalidOperationException("close failed");
            });

        var vm = new MainWindowViewModel(workflow, sftpService: sftp);
        List<int> notificationThreads = [];
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.LastConnectionError))
            {
                notificationThreads.Add(Environment.CurrentManagedThreadId);
            }
        };
        var document = new SftpDocument(
            new SftpDocumentViewModel(
                profile,
                session,
                workflow,
                sftp,
                new TransferOptions()));
        MethodInfo closeMethod = typeof(MainWindowViewModel).GetMethod(
            "CloseSftpDocumentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await _session.Dispatch(async () =>
        {
            int uiThread = Environment.CurrentManagedThreadId;
            var invocation = Task.Run(() => (Task)closeMethod.Invoke(vm, [document])!);
            await closeStarted.Task;
            await Task.Run(releaseClose.SetResult);
            await invocation;

            Assert.AreEqual("close failed", vm.LastConnectionError);
            Assert.AreNotEqual(uiThread, closeContinuationThread);
            Assert.IsNotEmpty(notificationThreads);
            Assert.IsTrue(notificationThreads.All(thread => thread == uiThread));
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task MainWindowClose_WaitsForAllStandaloneSftpDocumentsBeforeFinalClose()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionProfile firstProfile = CreateSftpProfile();
        SessionProfile secondProfile = CreateSftpProfile();
        SshSession firstSession = CreateSession(firstProfile.Id);
        SshSession secondSession = CreateSession(secondProfile.Id);
        ConfigureInitialLoad(sftp, firstSession.SessionId);
        ConfigureInitialLoad(sftp, secondSession.SessionId);
        sftp.CloseSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                closeStarted.TrySetResult();
                await releaseClose.Task;
            });

        await _session.Dispatch(async () =>
        {
            var vm = new MainWindowViewModel(workflow, sftpService: sftp);
            vm.Layout.AddDocument(new SftpDocument(
                new SftpDocumentViewModel(
                    firstProfile,
                    firstSession,
                    workflow,
                    sftp,
                    new TransferOptions())));
            vm.Layout.AddDocument(new SftpDocument(
                new SftpDocumentViewModel(
                    secondProfile,
                    secondSession,
                    workflow,
                    sftp,
                    new TransferOptions())));
            var window = new MainWindow { DataContext = vm };
            var finalClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => finalClose.TrySetResult();

            window.Show();
            window.Close();
            await closeStarted.Task;

            Assert.IsTrue(window.IsVisible, "The first close must be deferred while SFTP cleanup is blocked.");
            Assert.IsFalse(finalClose.Task.IsCompleted, "The final close must wait for standalone SFTP cleanup.");
            await sftp.Received(1).CloseSessionAsync(firstSession.SessionId, Arg.Any<CancellationToken>());
            await sftp.Received(1).CloseSessionAsync(secondSession.SessionId, Arg.Any<CancellationToken>());
            _ = workflow.DidNotReceiveWithAnyArgs().DisconnectAsync(default);

            releaseClose.SetResult();
            await finalClose.Task;
            await workflow.Received(1).DisconnectAsync(firstSession.SessionId);
            await workflow.Received(1).DisconnectAsync(secondSession.SessionId);
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task MainWindowClose_WhenDocumentCleanupAlreadyStarted_StillWaitsForIt()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        ConfigureInitialLoad(sftp, session.SessionId);
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
            });

        await _session.Dispatch(async () =>
        {
            var vm = new MainWindowViewModel(workflow, sftpService: sftp);
            var document = new SftpDocument(
                new SftpDocumentViewModel(
                    profile,
                    session,
                    workflow,
                    sftp,
                    new TransferOptions()));
            vm.Layout.AddDocument(document);
            vm.Layout.CloseDocument(document);
            await closeStarted.Task;

            var window = new MainWindow { DataContext = vm };
            var finalClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => finalClose.TrySetResult();
            window.Show();
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.IsTrue(window.IsVisible, "The window must remain open while the removed document is still closing.");
            Assert.IsFalse(finalClose.Task.IsCompleted, "Shutdown must await an already-running document close.");
            releaseClose.SetResult();
            await finalClose.Task;
            await sftp.Received(1).CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>());
            await workflow.Received(1).DisconnectAsync(session.SessionId);
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task MainWindowClose_StandaloneSftpCountsAsConnectedForConfirmation()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        ConfigureInitialLoad(sftp, session.SessionId);

        await _session.Dispatch(() =>
        {
            var vm = new MainWindowViewModel(workflow, sftpService: sftp);
            vm.Layout.AddDocument(new SftpDocument(
                new SftpDocumentViewModel(
                    profile,
                    session,
                    workflow,
                    sftp,
                    new TransferOptions())));
            var window = new MainWindow { DataContext = vm };
            MethodInfo hasConnected = typeof(MainWindow).GetMethod(
                "HasConnectedSessions",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.IsTrue((bool)hasConnected.Invoke(window, null)!);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task MainWindowClose_PersistsConnectedTerminalAndStandaloneSftpProfiles()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        SessionProfile sftpProfile = CreateSftpProfile();
        SshSession sftpSession = CreateSession(sftpProfile.Id);
        ConfigureInitialLoad(sftp, sftpSession.SessionId);
        SessionProfile terminalProfile = CreateSshProfile();
        var settings = new AppSettings();
        settings.General.RestoreSessionsOnStartup = true;

        await _session.Dispatch(() =>
        {
            var vm = new MainWindowViewModel(workflow, sftpService: sftp);
            vm.TabBar.AddTab(new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
            {
                Profile = terminalProfile,
                ConnectionStatus = SessionStatus.Connected,
            });
            vm.Layout.AddDocument(new SftpDocument(
                new SftpDocumentViewModel(
                    sftpProfile,
                    sftpSession,
                    workflow,
                    sftp,
                    new TransferOptions())));
            var window = new MainWindow { DataContext = vm };
            typeof(MainWindow)
                .GetField("_settingsService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, settingsService);
            MethodInfo persist = typeof(MainWindow).GetMethod(
                "PersistWindowBounds",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            persist.Invoke(window, [settings]);

            Assert.AreSequenceEqual(
                [terminalProfile.Id, sftpProfile.Id], settings.General.LastOpenProfileIds, SequenceOrder.InAnyOrder);
            settingsService.Received(1).SaveSettingsAsync(settings);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public void SftpDocument_ConnectionTooltipUsesNeutralConnectionIdentity()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);
        var viewModel = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions());
        var document = new SftpDocument(viewModel);

        Assert.AreEqual("Files · SFTP · root@files.example.com:22", document.ConnectionTooltip);
    }

    private static void ConfigureInitialLoad(ISftpService sftp, Guid sessionId)
    {
        sftp.GetWorkingDirectoryAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/"));
        sftp.ListDirectoryAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
    }

    private static SshSession CreateSession(Guid? sessionId = null) => new()
    {
        SessionId = sessionId ?? Guid.NewGuid(),
        ConnectionInfo = new()
        {
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
        },
        Status = SessionStatus.Connected,
    };

    private static SessionProfile CreateSftpProfile() => new()
    {
        ConnectionType = ConnectionType.SFTP,
        Name = "Files",
        Host = "files.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };

    private static SessionProfile CreateSshProfile() => new()
    {
        ConnectionType = ConnectionType.SSH,
        Name = "Server",
        Host = "server.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };
}
