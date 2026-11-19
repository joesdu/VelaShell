using System.Net.Sockets;
using Avalonia.Headless;
using Avalonia.Threading;
using NSubstitute;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class MainWindowSshFeatureTests
{
    /// <summary>开关开着时,每种 shell 都该拿到**自己那一段**,而不是千篇一律的 bash 钩子。</summary>
    /// <remarks>
    /// 旧版只有 bash 一段,守卫 <c>test -n "$BASH_VERSION"</c> 把 zsh 一并短路掉了 ——
    /// zsh 用户不报错,但「跟随终端目录」就是不工作。这组断言盯的正是那个洞。
    /// </remarks>
    [TestMethod]
    [DataRow(RemoteShellKind.Bash, "PROMPT_COMMAND")]
    [DataRow(RemoteShellKind.Zsh, "precmd_functions")]
    [DataRow(RemoteShellKind.Fish, "--on-variable PWD")]
    [DataRow(RemoteShellKind.PosixSh, "PS1=")]
    public void WorkingDirectoryScript_PicksScriptForShellKind(RemoteShellKind kind, string hookMechanism)
    {
        AppSettings settings = new();

        string script = MainWindowViewModel.WorkingDirectoryScript(settings, kind);
        ShellIntegrationInjection injection = ShellIntegrationScript.Build(kind);

        Assert.Contains(hookMechanism, script);
        Assert.Contains("vela_shell_osc7", script);
        Assert.Contains("]7;file://", script, "通用那条(给 tmux / 别家终端用)");
        Assert.Contains("]633;P;Cwd=", script, "VS Code 那条 —— 排在后面发,后到者为准");

        // 哨兵必须是"这一行自己会打出来的那条",而且整条序列都在里面 ——
        // 少了框架就会把 BEL 漏成一声真响的铃(见 ShellIntegrationInjection)。
        Assert.StartsWith("\e]633;P;VelaShell=", injection.Sentinel);
        Assert.EndsWith("\a", injection.Sentinel);
        Assert.Contains(injection.Sentinel[3..^1], injection.CommandLine, "哨兵的文本必须真的出现在被注入的那一行里");
        Assert.Contains(script, injection.CommandLine, "装载脚本必须原样落在被注入的那一行里");
    }

    /// <summary>
    /// 每次注入一枚新 nonce。固定串会让前一条注入的哨兵把后一条的窗口提前关掉 ——
    /// 重连、多开标签都会连着注入,这不是假想的情形。
    /// </summary>
    [TestMethod]
    public void ShellIntegrationBuild_UsesAFreshNoncePerInjection()
    {
        _ = new AppSettings();

        string first = ShellIntegrationScript.Build(RemoteShellKind.Bash).Sentinel;
        string second = ShellIntegrationScript.Build(RemoteShellKind.Bash).Sentinel;

        Assert.AreNotEqual(first, second);
    }

    /// <summary>
    /// 探不出种类(<see cref="RemoteShellKind.Unknown" />)与认定不是 POSIX
    /// (<see cref="RemoteShellKind.NonPosix" />,即 cmd.exe / PowerShell,#305)一律不注入。
    /// </summary>
    [TestMethod]
    [DataRow(RemoteShellKind.Unknown)]
    [DataRow(RemoteShellKind.NonPosix)]
    public void WorkingDirectoryScript_WithoutUsableShell_InjectsNothing(RemoteShellKind kind) =>
        Assert.IsEmpty(MainWindowViewModel.WorkingDirectoryScript(new(), kind));

    /// <summary>
    /// 关掉「上报终端工作目录」后必须一个字节都不注入(#286):用户报的就是每次开窗
    /// 终端里都要闪一串 test -n "$BASH_VERSION"。空串在 SendSilentCommand 里被直接丢弃,
    /// 连多余的回车都不会有。
    /// </summary>
    [TestMethod]
    public void WorkingDirectoryScript_WhenReportingDisabled_InjectsNothing()
    {
        AppSettings settings = new();
        settings.TerminalBehavior.ReportWorkingDirectory = false;

        Assert.IsEmpty(MainWindowViewModel.WorkingDirectoryScript(settings, RemoteShellKind.Bash));
    }

    /// <summary>
    /// 本机输入法的 XIM 桥(F5 第二步):X11 转发经内置 X Server(没指定显示地址、引擎是内置的)、开着「X 窗口里用本机输入法」时,
    /// 连接后连同目录钩子一起注入设 <c>XMODIFIERS=@im=velashell</c> 的那一句(已经设了的不动);任何一个条件不满足就不注入。
    /// 关掉「上报终端工作目录」也照样要探 shell 种类(否则不知道该用哪一种写法)。
    /// </summary>
    [TestMethod]
    public void InputMethodEnvironmentScript_OnlyWhenX11GoesToTheBuiltInServerWithTheHostInputMethod()
    {
        AppSettings settings = new();
        settings.TerminalBehavior.ReportWorkingDirectory = false;
        SessionProfile forwarding = new() { Ssh = new SshSessionOptions { X11Forwarding = true } };

        Assert.IsTrue(MainWindowViewModel.WantsInputMethodEnvironment(settings, forwarding), "目录上报关了也要探 shell 种类");
        Assert.AreEqual("[ -n \"${XMODIFIERS-}\" ] || export XMODIFIERS=@im=velashell",
            MainWindowViewModel.InputMethodEnvironmentScript(settings, forwarding, RemoteShellKind.Bash));
        Assert.AreEqual("set -q XMODIFIERS; or set -gx XMODIFIERS @im=velashell",
            MainWindowViewModel.InputMethodEnvironmentScript(settings, forwarding, RemoteShellKind.Fish));
        Assert.IsEmpty(MainWindowViewModel.InputMethodEnvironmentScript(settings, forwarding, RemoteShellKind.NonPosix), "cmd.exe / PowerShell 不注入");
        Assert.IsEmpty(MainWindowViewModel.InputMethodEnvironmentScript(settings, forwarding, RemoteShellKind.Unknown));

        Assert.IsFalse(MainWindowViewModel.WantsInputMethodEnvironment(settings, new SessionProfile()), "没开 X11 转发");
        Assert.IsFalse(MainWindowViewModel.WantsInputMethodEnvironment(settings,
            new SessionProfile { Ssh = new SshSessionOptions { X11Forwarding = true, X11Display = "localhost:0.0" } }), "指定了别的 X 服务端");
        settings.XServer.UseHostInputMethod = false;
        Assert.IsFalse(MainWindowViewModel.WantsInputMethodEnvironment(settings, forwarding), "不用本机输入法");
        settings.XServer.UseHostInputMethod = true;
        settings.XServer.Engine = Core.XServer.XServerEngines.VcXsrv;
        Assert.IsFalse(MainWindowViewModel.WantsInputMethodEnvironment(settings, forwarding), "用的是 VcXsrv");
    }

    /// <summary>
    /// 开着 X11 转发就注入 <c>GSK_RENDERER=cairo</c>(已经设了的不动),不论显示交给哪个 X 服务端:GTK 4 默认的 GL 经 SSH 只能退到 llvmpipe、
    /// 每帧把整窗像素发过来(gnome-calculator 点一下按钮 29 MB,cairo 0.15 MB)。与 <c>XMODIFIERS</c> 拼在同一句里;没开 X11 转发一个字节都不发。
    /// </summary>
    [TestMethod]
    public void X11EnvironmentScript_AddsGtkCairoRendererWheneverX11IsForwarded()
    {
        AppSettings settings = new();
        SessionProfile forwarding = new() { Ssh = new SshSessionOptions { X11Forwarding = true } };

        Assert.AreEqual("[ -n \"${XMODIFIERS-}\" ] || export XMODIFIERS=@im=velashell; [ -n \"${GSK_RENDERER-}\" ] || export GSK_RENDERER=cairo",
            MainWindowViewModel.X11EnvironmentScript(settings, forwarding, RemoteShellKind.Bash));
        Assert.AreEqual("set -q XMODIFIERS; or set -gx XMODIFIERS @im=velashell; set -q GSK_RENDERER; or set -gx GSK_RENDERER cairo",
            MainWindowViewModel.X11EnvironmentScript(settings, forwarding, RemoteShellKind.Fish));
        Assert.IsEmpty(MainWindowViewModel.X11EnvironmentScript(settings, forwarding, RemoteShellKind.NonPosix), "cmd.exe / PowerShell 不注入");

        settings.XServer.UseHostInputMethod = false;
        Assert.AreEqual("[ -n \"${GSK_RENDERER-}\" ] || export GSK_RENDERER=cairo",
            MainWindowViewModel.X11EnvironmentScript(settings, forwarding, RemoteShellKind.Zsh), "不用本机输入法也照样请 GTK 4 用 cairo");
        SessionProfile external = new() { Ssh = new SshSessionOptions { X11Forwarding = true, X11Display = "localhost:0.0" } };
        Assert.IsTrue(MainWindowViewModel.WantsRendererEnvironment(external), "显示交给别的 X 服务端时一样经 SSH 送整窗像素");
        Assert.IsFalse(MainWindowViewModel.WantsRendererEnvironment(new SessionProfile()), "没开 X11 转发");
        Assert.IsEmpty(MainWindowViewModel.X11EnvironmentScript(settings, new SessionProfile(), RemoteShellKind.Bash));
    }

    [TestMethod]
    public async Task ConnectProfileAsync_AddsTerminalTab_AndUpdatesStatusBar()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shellStream = Substitute.For<IShellStreamWrapper>();
        ITerminalEmulator? terminal = FakeTerminal.Emulator();

        // 模拟活连接:读循环阻塞在 ReadAsync(不立即 EOF),否则桥会异步触发 Closed 与连接
        // 结果竞态,把刚连上的标签翻回断开(真实连接不会立即 EOF)。
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(TestSupport.FakeShellReads.UntilCancelled);
        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shellStream);

        // 连接历史由工作流写入 SonnetDB;侧边栏“最近连接”刷新时从服务读取。
        IRecentConnectionService? recents = Substitute.For<IRecentConnectionService>();
        recents.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([
                   new() { ProfileId = profile.Id, Name = "Prod", GroupName = "生产环境", Host = profile.Host, Port = 22, Username = "root" }
               ]);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal, recentConnectionService: recents);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        Assert.AreEqual("Prod", tab.Title);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreSame(tab, vm.ActiveTerminalTab);
        Assert.HasCount(1, vm.TerminalTabs);
        // 设计 gzmsb 调整(cfe16d2):状态栏只显示"SSH • <显示名称>",不暴露用户名/IP/端口(安全要求)。
        Assert.AreEqual("SSH • Prod", vm.StatusBar.ConnectionInfo);
        Assert.AreEqual(Strings.Connected, vm.StatusBar.Status);
        Assert.HasCount(1, vm.Sidebar.RecentConnections.Connections);
        Assert.AreEqual("Prod - 生产环境", vm.Sidebar.RecentConnections.Connections[0].DisplayName);
    }

    /// <summary>
    /// 配置自带的「认证后执行命令」必须真的注入到这条会话的 shell 里(延迟 0 = 握手完立刻发)。
    /// 它与设置里那条全局命令是两件事,顺序固定「先全局、后本条」—— 与用户在两个界面上
    /// 看到的顺序一致。只断言本条命令确实落到了线上:全局那条由 WorkingDirectoryScript 的用例覆盖。
    /// </summary>
    [TestMethod]
    public async Task ConnectProfileAsync_InjectsThePerProfilePostAuthCommand()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shellStream = Substitute.For<IShellStreamWrapper>();
        ITerminalEmulator? terminal = FakeTerminal.Emulator();

        // 同上一个用例:读循环必须阻塞而不是立刻 EOF,否则桥会把刚连上的标签翻回断开。
        shellStream.CanRead.Returns(true);
        shellStream.CanWrite.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(TestSupport.FakeShellReads.UntilCancelled);

        // 出站写由桥的写循环在后台线程逐段刷出,不与 TryConnectProfileAsync 同步完成;
        // 用 TCS 等那一段字节真的落到流上,而不是 sleep 一个猜出来的毫秒数。
        var injected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       string payload = System.Text.Encoding.UTF8.GetString(
                           (byte[])callInfo[0], (int)callInfo[1], (int)callInfo[2]);
                       if (payload.Contains("tmux attach", StringComparison.Ordinal))
                       {
                           injected.TrySetResult(payload);
                       }
                       return Task.CompletedTask;
                   });

        var profile = new SessionProfile
        {
            Name = "dev",
            Host = "dev.example.com",
            Port = 22,
            Username = "dev",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
            PostAuthCommand = "  tmux attach  ",
            PostAuthCommandDelaySeconds = 0,
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
                                         Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(),
                                         Arg.Any<CancellationToken>())
                 .Returns(shellStream);

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);

        Assert.IsNotNull(tab);

        // 注入不再与握手同步完成:它推迟到「对端安静下来」之后才发(见
        // MainWindowViewModel.SendSessionInjections —— 注入窗口是整段扣住输出的,
        // armed 得太早会把横幅和第一个提示符一起吞掉)。这个替身流永远不吐数据,
        // 于是会一路等到 3 秒的上限才注入,所以这里给的余量比那个上限宽。
        string payload = await injected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 首尾空白在保存与注入两处都会被裁掉;注入本身按 SendSilentCommand 的约定
        // 前置一个空格、以 \n 收尾,并在整行最前面接上摘历史那段 —— 用户不该在方向键里
        // 看见自己没敲过的东西(那个前导空格只对配了 HISTCONTROL=ignorespace 的人有用,
        // 而它默认是空的)。摘历史在**前**,所以这一行的退出码仍由 tmux attach 自己决定。
        Assert.AreEqual($" {ShellHistoryScrub.Command}; tmux attach\n", payload);
    }

    [TestMethod]
    public async Task TryConnectProfileAsync_AuthFailure_DoesNotThrow_AndReportsError()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "wrong"
        };

        // 模拟服务端拒绝密码。必须抛 Core 的真实中立类型:VM 按类型匹配(ex is VelaSshAuthenticationException),
        // 这里以前伪造过一个同名假异常去迎合旧的类型名字符串匹配,结果生产路径从未被覆盖。
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationException("Permission denied (password)."));
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => FakeTerminal.Emulator());
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNull(tab);
        Assert.IsEmpty(vm.TerminalTabs);
        Assert.IsFalse(string.IsNullOrEmpty(vm.LastConnectionError));

        // 比对本地化资源而非中文字面量:该文案随 UI 语言变化,写死会让测试只在中文环境通过。
        // 提示后面还会换行附上底层库给出的具体原因(DescribeConnectionError 有意为之,便于用户诊断)。
        // 原先中间还有一句「本版不会两步验证」(F-11);动态码框接上之后那句不再成立,已撤掉。
        Assert.AreEqual(
            $"{Strings.Format("Msg_AuthFailed", "root@prod.example.com:22")}\nPermission denied (password).",
            vm.LastConnectionError);
    }

    [TestMethod]
    public async Task TryConnectProfileAsync_NetworkFailure_KeepsTab_WithInTabOverlay()
    {
        // 设计 yxjmg:网络/超时失败不销毁标签、不弹全局框,仅在标签页内显示失败覆盖层。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        var profile = new SessionProfile
        {
            Name = "LAN",
            Host = "192.168.1.50",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new SocketException());
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => FakeTerminal.Emulator());
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        Assert.HasCount(1, vm.TerminalTabs);
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsTrue(tab.ShowDisconnectedOverlay);
        Assert.IsTrue(tab.HasConnectionError);
        Assert.AreEqual(Strings.Get("Msg_ConnectionFailedTitle"), tab.DisconnectOverlayTitle);
        Assert.IsFalse(string.IsNullOrEmpty(vm.LastConnectionError));
    }

    [TestMethod]
    public async Task ReconnectTabAsync_ReusesSameTab_AndRestoresConnectedState()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shellStream = Substitute.For<IShellStreamWrapper>();
        // 模拟活连接:读循环阻塞在 ReadAsync,避免桥立即 EOF 触发 Closed 与连接结果竞态。
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(TestSupport.FakeShellReads.UntilCancelled);
        ITerminalEmulator? terminal = FakeTerminal.Emulator();
        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shellStream);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);

        // The remote drops (exit / reboot).
        tab.MarkDisconnected();
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);

        // Reconnect in place — same tab, not a new one.
        await vm.ReconnectTabAsync(tab);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.HasCount(1, vm.TerminalTabs);
        Assert.AreSame(tab, vm.TerminalTabs.First());
    }

    /// <summary>
    /// 重连败在主机指纹上:标签记下来,自动重连不再介入(<see cref="TerminalTabViewModel.HostKeyRejected" />)——
    /// 再试多少次结果都一样,而在这之前每试一次就再弹一遍指纹确认框。用户手动重连、连上之后解除。
    /// </summary>
    [TestMethod]
    public async Task ReconnectTabAsync_HostKeyRejected_BlocksAutoReconnectUntilAManualReconnectSucceeds()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shellStream = Substitute.For<IShellStreamWrapper>();
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(TestSupport.FakeShellReads.UntilCancelled);
        ITerminalEmulator? terminal = FakeTerminal.Emulator();
        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shellStream);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        tab.MarkDisconnected();

        // 服务器换了钥,用户在弹窗里拒绝了。
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SshSession>(new VelaSshHostKeyRejectedException("指纹被拒")));
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsTrue(tab.HasConnectionError);
        Assert.IsTrue(tab.HostKeyRejected, "败在指纹上的标签不该再被自动重连。");

        // 用户核对过、接受了:手动重连连上,自动重连照常。
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.IsFalse(tab.HostKeyRejected);
    }

    [TestMethod]
    public async Task TailscaleAuthenticationPause_BlocksWakeQueuedCountdownAndSettingsSavedUntilManualSuccess()
    {
        await RunOnReconnectUiAsync(async () =>
        {
            (MainWindowViewModel? vm, IConnectionWorkflowService? workflow, ISettingsService? settings, SessionProfile? profile) = CreateReconnectConsumer();
            string checkReason = $"ops@jump:22: {Strings.Get("SshErr_TailscaleCheckRequired")}";
            bool requiresCheck = true;
            workflow.ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>())
                .Returns(_ => requiresCheck
                    ? Task.FromException<SshSession>(new VelaSshTailscaleAuthenticationException(checkReason))
                    : Task.FromResult(ConnectedSession(profile)));
            try
            {
                TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
                Assert.IsNotNull(tab);
                tab.MarkDisconnected();
                Assert.AreEqual(1, tab.ReconnectAttempts, "真实断线必须先排入一秒倒计时,才能覆盖过期回调。");
                var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tab.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(TerminalTabViewModel.ConnectionStatus)
                        && tab.ConnectionStatus == SessionStatus.Disconnected
                        && tab.AuthenticationRequiresUserAction)
                    {
                        paused.TrySetResult();
                    }
                };

                vm.ReconnectAllAfterResume();
                await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await PassQueuedReconnectDeadlineAsync();

                Assert.IsTrue(tab.AuthenticationRequiresUserAction);
                Assert.IsFalse(tab.HostKeyRejected);
                Assert.IsFalse(tab.UserRequestedDisconnect);
                Assert.AreEqual(checkReason, tab.ConnectionError);
                await workflow.Received(1).ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>());
                vm.ReconnectAllAfterResume();

                // 同一配置的新标签是一次独立尝试,不继承旧标签的认证暂停。
                TerminalTabViewModel? fresh = await vm.TryConnectProfileAsync(profile);
                Assert.IsNotNull(fresh);
                Assert.AreNotSame(tab, fresh);
                Assert.IsFalse(fresh.AuthenticationRequiresUserAction);
                Assert.AreEqual(SessionStatus.Connected, fresh.ConnectionStatus);
                fresh.MarkConnectionFailed("网络故障:代理变更对照标签");
                AppSettings changed = ReconnectSettings("system");
                settings.GetSettingsAsync().Returns(changed);
                settings.SettingsSaved += Raise.Event<Action<AppSettings>>(changed);
                await PassQueuedReconnectDeadlineAsync();

                Assert.AreEqual(SessionStatus.Connected, fresh.ConnectionStatus, "SettingsSaved 的代理变更路径必须真实执行,不能只测筛选器。");
                Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
                Assert.IsTrue(tab.AuthenticationRequiresUserAction);
                Assert.AreEqual(checkReason, tab.ConnectionError);
                await workflow.Received(3).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
                await workflow.Received(1).ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>());
                fresh.MarkDisconnectedByUser();

                // 手动尝试允许进入,但网络失败与新的授权失败都不能提前解除认证暂停。
                workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<SshSession>(new SocketException()));
                await vm.ReconnectTabAsync(tab);
                Assert.IsTrue(tab.AuthenticationRequiresUserAction);
                string rejected = $"ops@jump:22: {Strings.Get("SshErr_TailscaleRejected")}";
                workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<SshSession>(new VelaSshTailscaleAuthenticationException(rejected)));
                await vm.ReconnectTabAsync(tab);
                Assert.IsTrue(tab.AuthenticationRequiresUserAction);
                Assert.AreEqual(rejected, tab.ConnectionError);

                workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(_ => ConnectedSession(profile));
                await vm.ReconnectTabAsync(tab);
                Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
                Assert.IsFalse(tab.AuthenticationRequiresUserAction);
                Assert.IsFalse(tab.HasConnectionError);

                requiresCheck = false;
                tab.MarkDisconnected();
                await PassQueuedReconnectDeadlineAsync();
                Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus, "成功解除暂停后,普通网络掉线要再次自动恢复。");
                await workflow.Received(2).ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>());

                requiresCheck = true;
                tab.MarkDisconnected();
                await PassQueuedReconnectDeadlineAsync();
                Assert.IsTrue(tab.AuthenticationRequiresUserAction, "下一次实际握手的新 check 要求不能被上一次成功缓存屏蔽。");
                Assert.AreEqual(checkReason, tab.ConnectionError);
                await workflow.Received(3).ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>());
            }
            finally
            {
                foreach (TerminalTabViewModel tab in vm.TerminalTabs.ToArray())
                {
                    tab.MarkDisconnectedByUser();
                    await tab.DisposeAsync();
                }
            }
        });
    }

    [TestMethod]
    public async Task TailscaleReconnectCancelled_UsesUserDisconnectAndAllowsManualRecovery()
    {
        await RunOnReconnectUiAsync(async () =>
        {
            (MainWindowViewModel? vm, IConnectionWorkflowService? workflow, ISettingsService _, SessionProfile? profile) = CreateReconnectConsumer();
            TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
            Assert.IsNotNull(tab);
            try
            {
                tab.MarkDisconnected();
                workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<SshSession>(new VelaSshAuthenticationCancelledException("cancelled")));
                await vm.ReconnectTabAsync(tab);

                Assert.IsTrue(tab.UserRequestedDisconnect);
                Assert.IsFalse(tab.AuthenticationRequiresUserAction);
                Assert.IsNull(vm.LastConnectionError);
                vm.ReconnectAllAfterResume();
                await PassQueuedReconnectDeadlineAsync();
                await workflow.DidNotReceive().ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>());

                workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(_ => ConnectedSession(profile));
                await vm.ReconnectTabAsync(tab);
                Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
                Assert.IsFalse(tab.UserRequestedDisconnect);
            }
            finally
            {
                tab.MarkDisconnectedByUser();
                await tab.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// 认证途中断线、没等到验证链接就超时不是授权结论:曾经同样被当成「要人处理」,后台重连就此永久暂停。
    /// 现在与别的认证方式一样按退避再试,下一次连上照常恢复。
    /// </summary>
    [TestMethod]
    public async Task TailscaleTransientAuthFailure_KeepsBackoffRetryInsteadOfPausing()
    {
        await RunOnReconnectUiAsync(async () =>
        {
            (MainWindowViewModel? vm, IConnectionWorkflowService? workflow, ISettingsService _, SessionProfile? profile) = CreateReconnectConsumer();
            string transient = $"ops@jump:22: {Strings.Get("SshErr_TailscaleAuthTimeout")}";
            bool fail = true;
            workflow.ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>())
                .Returns(_ => fail
                    ? Task.FromException<SshSession>(new VelaSshTailscaleAuthenticationException(transient, requiresUserAction: false))
                    : Task.FromResult(ConnectedSession(profile)));
            TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
            Assert.IsNotNull(tab);
            try
            {
                tab.MarkDisconnected();
                await PassQueuedReconnectDeadlineAsync();

                Assert.IsFalse(tab.AuthenticationRequiresUserAction, "临时失败不能暂停后台重连。");
                Assert.AreEqual(transient, tab.ConnectionError);
                await workflow.Received(1).ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>());

                fail = false;
                await PassQueuedReconnectDeadlineAsync();
                Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus, "下一次退避重试照常进行并连上。");
                await workflow.Received(2).ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>());
            }
            finally
            {
                tab.MarkDisconnectedByUser();
                await tab.DisposeAsync();
            }
        });
    }

    /// <summary>首次连接遇到同样的临时失败:留在失败覆盖层,不弹凭据框(Tailscale 没有凭据可问),也不记认证暂停。</summary>
    [TestMethod]
    public async Task TailscaleTransientFailureOnFirstConnect_ShowsFailureWithoutPromptOrPause()
    {
        await RunOnReconnectUiAsync(async () =>
        {
            (MainWindowViewModel? vm, IConnectionWorkflowService? workflow, ISettingsService _, SessionProfile? profile) = CreateReconnectConsumer();
            string transient = $"ops@jump:22: {Strings.Get("SshErr_TailscaleCheckFailed")}";
            workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SshSession>(new VelaSshTailscaleAuthenticationException(transient, requiresUserAction: false)));
            int prompts = 0;
            vm.InteractiveAuthenticator = _ =>
            {
                prompts++;
                return Task.FromResult<SessionProfile?>(null);
            };

            TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
            Assert.IsNotNull(tab);
            try
            {
                Assert.AreEqual(0, prompts);
                Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
                Assert.AreEqual(transient, tab.ConnectionError);
                Assert.IsFalse(tab.AuthenticationRequiresUserAction);
            }
            finally
            {
                tab.MarkDisconnectedByUser();
                await tab.DisposeAsync();
            }
        });
    }

    private static (MainWindowViewModel Vm, IConnectionWorkflowService Workflow, ISettingsService Settings, SessionProfile Profile) CreateReconnectConsumer()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(true);
        shell.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async args =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, (CancellationToken)args[3]);
                return 0;
            });
        ssh.GetClient(Arg.Any<Guid>()).Returns(client);
        client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
            Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shell);
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(ReconnectSettings("none"));
        var profile = new SessionProfile
        {
            Name = "target",
            Host = "target",
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "target-password",
            JumpHostProfileId = Guid.NewGuid()
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(_ => ConnectedSession(profile));
        workflow.ReconnectProfileAutomaticallyAsync(profile, Arg.Any<CancellationToken>()).Returns(_ => ConnectedSession(profile));
        return (new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator(), settingsService: settings), workflow, settings, profile);
    }

    private static AppSettings ReconnectSettings(string proxyType)
    {
        var settings = new AppSettings();
        settings.Proxy.Type = proxyType;
        settings.General.AutoReconnect = true;
        settings.General.ReconnectIntervalSeconds = 1;
        settings.General.NotifyOnDisconnect = false;
        settings.General.SoundAlerts = false;
        settings.TerminalBehavior.ReportWorkingDirectory = false;
        return settings;
    }

    private static SshSession ConnectedSession(SessionProfile profile) => new()
    {
        SessionId = Guid.NewGuid(),
        Status = SessionStatus.Connected,
        ConnectionInfo = new() { Host = profile.Host, Port = profile.Port, Username = profile.Username, AuthMethod = profile.AuthMethod }
    };

    private static async Task PassQueuedReconnectDeadlineAsync()
    {
        var sentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable timer = DispatcherTimer.RunOnce(() => sentinel.TrySetResult(), TimeSpan.FromMilliseconds(1400));
        await sentinel.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Task RunOnReconnectUiAsync(Func<Task> body) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MainWindowSshFeatureTests).Assembly).RunOnUiAsync(async () =>
        {
            ISequencer previous = RxSchedulers.MainThreadScheduler;
            try
            {
                RxSchedulers.MainThreadScheduler = new AvaloniaScheduler(Dispatcher.UIThread);
                await body();
            }
            finally
            {
                RxSchedulers.MainThreadScheduler = previous;
            }
        });

    [TestMethod]
    public async Task ReconnectTabAsync_WhenAlreadyConnected_IsNoOp()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ITerminalEmulator? terminal = FakeTerminal.Emulator();
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        var tab = new TerminalTabViewModel(terminal)
        {
            ConnectionStatus = SessionStatus.Connected,
            Profile = new() { Host = "h", Port = 22, Username = "u" }
        };
        await vm.ReconnectTabAsync(tab);

        // No connect attempt should have been made.
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
    }

    [TestMethod]
    public async Task InitializeAsync_LoadsRecentHistory_IntoRecentConnections()
    {
        IRecentConnectionService? recents = Substitute.For<IRecentConnectionService>();
        recents.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([
                   new() { Name = "Prod", GroupName = "生产环境", Host = "prod.example.com", Port = 22, Username = "root", ConnectedAt = DateTimeOffset.Now.AddHours(-2) },
                   new() { Name = "Dev", Host = "dev.example.com", Port = 22, Username = "pi", ConnectedAt = DateTimeOffset.Now.AddDays(-3) }
               ]);
        var vm = new MainWindowViewModel(recentConnectionService: recents);
        await vm.InitializeAsync();
        Assert.HasCount(2, vm.Sidebar.RecentConnections.Connections);
        // 名称是测试数据(照原样显示),相对时间是本地化文案 —— 后者必须比对资源。
        Assert.AreEqual("Prod - 生产环境", vm.Sidebar.RecentConnections.Connections[0].DisplayName);
        Assert.AreEqual(Strings.Format("Svc_HoursAgo", 2), vm.Sidebar.RecentConnections.Connections[0].RelativeTime);
        Assert.AreEqual("Dev", vm.Sidebar.RecentConnections.Connections[1].DisplayName);
        Assert.AreEqual(Strings.Format("Svc_DaysAgo", 3), vm.Sidebar.RecentConnections.Connections[1].RelativeTime);
    }

    [TestMethod]
    public async Task TryConnectRecentAsync_ReconstructsSftpType_WithoutCreatingTerminal()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        var entry = new RecentConnectionEntry
        {
            ConnectionType = ConnectionType.SFTP,
            Name = "Files",
            Host = "files.example.com",
            Port = 22,
            Username = "root",
        };

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => FakeTerminal.Emulator());

        TerminalTabViewModel? tab = await vm.TryConnectRecentAsync(entry);

        Assert.IsNull(tab);
        Assert.IsEmpty(vm.TerminalTabs);
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task OpenSftpForProfileAsync_ConnectsSshAndEnsuresExistingPanelVisible()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        ISftpService? sftpService = Substitute.For<ISftpService>();
        var profile = new SessionProfile
        {
            Name = "Files",
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
            },
            Status = SessionStatus.Connected,
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sftpService.GetWorkingDirectoryAsync(session.SessionId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult("/"));
        sftpService.ListDirectoryAsync(session.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            sftpService: sftpService);

        TerminalTabViewModel? tab = await vm.OpenSftpForProfileAsync(profile);

        Assert.IsNull(tab);
        Assert.IsEmpty(vm.TerminalTabs);
        Assert.HasCount(1, vm.Layout.AllDocuments().OfType<SftpDocument>());
        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        await sshClient.DidNotReceive().CreateShellStreamAsync(
            Arg.Any<string>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void StatusBar_FollowsActiveTab_ConnectionInfo()
    {
        var vm = new MainWindowViewModel();
        var tabA = new TerminalTabViewModel(FakeTerminal.Emulator(), Substitute.For<IShellStreamWrapper>())
        {
            Title = "A",
            ConnectionStatus = SessionStatus.Connected,
            ConnectionSummary = "SSH • a@host-a:22"
        };
        var tabB = new TerminalTabViewModel(FakeTerminal.Emulator(), Substitute.For<IShellStreamWrapper>())
        {
            Title = "B",
            ConnectionStatus = SessionStatus.Connected,
            ConnectionSummary = "SSH • b@host-b:22"
        };
        vm.Layout.AddDocument(new TerminalDocument(tabA));
        vm.Layout.AddDocument(new TerminalDocument(tabB)); // B becomes active
        Assert.AreEqual("SSH • b@host-b:22", vm.StatusBar.ConnectionInfo);
        vm.Activate(tabA); // switch back to A
        Assert.AreEqual("SSH • a@host-a:22", vm.StatusBar.ConnectionInfo);
    }
}
