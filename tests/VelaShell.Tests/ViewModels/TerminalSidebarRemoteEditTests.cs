using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 终端侧栏的文件面板开出来的外部编辑会话,随面板拆掉(标签关闭)一起收掉:
/// 没传完的那一次先传完,再拆 SFTP 通道;别的会话名下的编辑会话不受影响。
/// </summary>
/// <remarks>
/// 以前只有 SFTP / 双栏文件标签关闭时才调 <see cref="RemoteEditSessionManager.CloseScopeAsync" />,
/// 侧栏这一路的 watcher 一直挂到应用退出,对着一个早就没了的会话。
/// </remarks>
[TestClass]
[TestCategory("ExternalEdit")]
[DoNotParallelize]
public sealed class TerminalSidebarRemoteEditTests
{
    static TerminalSidebarRemoteEditTests()
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
            // 别的用例已经初始化过。
        }
    }

    [TestCleanup]
    public void CleanupRemoteEditSessions() => RemoteEditSessionManager.CleanupAll();

    [TestMethod]
    public async Task ClosingTheTerminalTab_FlushesTheLastSave_ThenEndsItsEditSessions_BeforeSftpCloses()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new MainWindowViewModel(sftpService: sftp);
        TerminalDocument document = AddConnectedTab(vm);
        Guid sessionId = document.Terminal.SessionId;
        var events = new List<string>();
        sftp.CloseSessionAsync(sessionId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Record(events, "sftp closed");
            return Task.CompletedTask;
        });

        RemoteEditSession session = await OpenAsync(sftp, sessionId, events);
        RemoteEditSession other = await OpenAsync(sftp, Guid.NewGuid(), events);
        await File.WriteAllTextAsync(session.LocalPath, "edited");
        await WaitUntilAsync(() => session.HasPendingChange || Snapshot(events).Count > 0);

        vm.Layout.CloseDocument(document);
        await vm.TerminalCloseTaskFor(document).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(session, RemoteEditSessionManager.ActiveSessions, "侧栏面板拆掉了,它名下的编辑会话还挂着");
        Assert.Contains(other, RemoteEditSessionManager.ActiveSessions, "别的会话名下的编辑会话不该被一起收掉");
        Assert.AreSequenceEqual(["uploaded /etc/app.conf", "sftp closed"], Snapshot(events),
            "改完立刻关标签:那一次保存要在拆 SFTP 通道之前传上去");
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(session.LocalPath)), "全传上去了就不该留副本");
    }

    /// <summary>没有编辑会话时照旧:驱逐面板、拆 SFTP 通道,一步都不多等。</summary>
    [TestMethod]
    public async Task ClosingATabWithoutEditSessions_StillClosesSftp()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new MainWindowViewModel(sftpService: sftp);
        TerminalDocument document = AddConnectedTab(vm);

        vm.Layout.CloseDocument(document);
        await vm.TerminalCloseTaskFor(document).WaitAsync(TimeSpan.FromSeconds(30));

        await sftp.Received(1).CloseSessionAsync(document.Terminal.SessionId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 断线(不是关标签):编辑会话随之结束,但外部编辑器多半还开着那份副本 —— 副本一个都不删,并用浮层说一声
    /// (面板这时已经被驱逐,它的报错栏没人看得见)。以前干净的会话照关标签那样删目录、一声不吭,
    /// 之后的保存再也传不回去(复查 file-panes F8)。
    /// </summary>
    [TestMethod]
    public async Task ConnectionLoss_EndsTheEditSessions_ButKeepsTheCopies_AndSaysSo()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("/home/u");
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        (MainWindowViewModel vm, TerminalTabViewModel tab) = await ConnectViaHostAsync(sftp);
        var events = new List<string>();
        RemoteEditSession session = await OpenAsync(sftp, tab.SessionId, events);
        string directory = Path.GetDirectoryName(session.LocalPath)!;
        try
        {
            tab.MarkDisconnected();
            await WaitUntilAsync(() => !RemoteEditSessionManager.ActiveSessions.Contains(session));

            Assert.IsTrue(File.Exists(session.LocalPath), "编辑器还开着这份副本:断线不删");
            Assert.AreEqual(RemoteEditDrafts.Clean, RemoteEditDrafts.Read(directory)?.State,
                "标记仍是干净:之后没动过,启动时到点清掉;断线后又存过,启动时按草稿列出来");
            string expected = Strings.Format("Svc_RemoteEditEndedByDisconnect", session.FileName, session.LocalPath);
            // 收尾链在线程池上跑,提示编组回 UI 线程推。
            for (int i = 0; i < 200 && !vm.Toasts.Toasts.Any(t => t.Message == expected); i++)
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }
                await Task.Delay(50);
            }
            Assert.Contains(t => t.Message == expected, vm.Toasts.Toasts);
            Assert.IsEmpty(Snapshot(events), "干净的副本没有可传的");
        }
        finally
        {
            RemoteEditSessionManager.TryDeleteDirectory(directory);
        }
    }

    /// <summary>经宿主真正的连接流程连上一个替身 SSH 会话(断线事件只对这样开出来的标签有订阅)。</summary>
    private static async Task<(MainWindowViewModel Vm, TerminalTabViewModel Tab)> ConnectViaHostAsync(ISftpService sftp)
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        shellStream.CanRead.Returns(true);
        shellStream.CanWrite.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new TaskCompletionSource<int>().Task);
        var profile = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "edit-box",
            Host = "edit-box.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
            ConnectionType = ConnectionType.SSH,
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new() { Host = profile.Host, Port = 22, Username = "root", AuthMethod = AuthMethod.Password, Password = "secret" },
            Status = SessionStatus.Connected,
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
                                         Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(),
                                         Arg.Any<CancellationToken>())
                 .Returns(shellStream);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => FakeTerminal.Emulator(), sftpService: sftp);
        TerminalTabViewModel tab = await vm.TryConnectProfileAsync(profile, null)
                                   ?? throw new AssertFailedException("标签没连上");
        Assert.AreEqual(session.SessionId, tab.SessionId);
        return (vm, tab);
    }

    private static TerminalDocument AddConnectedTab(MainWindowViewModel vm)
    {
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = new() { Name = "one", Host = "one.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        var document = new TerminalDocument(tab);
        vm.Layout.AddDocument(document);
        return document;
    }

    private static async Task<RemoteEditSession> OpenAsync(ISftpService sftp, Guid sessionId, List<string> events) =>
        await RemoteEditSessionManager.OpenAsync(new()
        {
            SftpService = sftp,
            SessionId = sessionId,
            RemotePath = "/etc/app.conf",
            FileName = "app.conf",
            OpenWith = RemoteEditOpenWith.Nothing,
            DownloadAsync = async (local, token) =>
            {
                await File.WriteAllTextAsync(local, "original", token);
                return true;
            },
            UploadAsync = (_, remote) =>
            {
                Record(events, "uploaded " + remote);
                return Task.CompletedTask;
            },
        }) ?? throw new AssertFailedException("编辑会话没开起来");

    private static void Record(List<string> events, string entry)
    {
        lock (events)
        {
            events.Add(entry);
        }
    }

    private static List<string> Snapshot(List<string> events)
    {
        lock (events)
        {
            return [.. events];
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("等了 10 秒,watcher 还没看到那次保存");
            }
            await Task.Delay(20);
        }
    }
}
