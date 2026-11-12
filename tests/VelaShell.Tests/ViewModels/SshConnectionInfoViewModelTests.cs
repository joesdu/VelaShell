using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Terminal.Rendering;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接信息面板(会话标签右键 → 连接信息):按块摆出库的只读属性,能刷新、能整份复制成文本;没连着时如实说。
/// </summary>
[TestClass]
[TestCategory("ConnectionInfo")]
public sealed class SshConnectionInfoViewModelTests
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SshConnectionInfoViewModelTests).Assembly);

    private static SshConnectionDetails Sample() => new()
    {
        Target = "ops@10.0.0.5:22",
        CapturedAt = new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero),
        IsAlive = true,
        PeerVersion = "SSH-2.0-OpenSSH_9.6p1",
        HostKey = new("ssh-ed25519", 256, "SHA256:abcdEFGH", IsCertificate: false),
        SessionId = "0a1b2c",
        PeerSupportsPing = true,
        Algorithms = new()
        {
            KeyExchange = "mlkem768x25519-sha256",
            HostKey = "ssh-ed25519",
            EncryptionClientToServer = "aes256-gcm@openssh.com",
            EncryptionServerToClient = "chacha20-poly1305@openssh.com",
            CompressionClientToServer = "none",
            CompressionServerToClient = "none",
            StrictKeyExchange = true,
            ExtensionNegotiation = true,
        },
        AuthenticationMethod = "publickey",
        ServerSignatureAlgorithms = ["ssh-ed25519", "rsa-sha2-512"],
        Timings = new(TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(3), TimeSpan.FromSeconds(4.5), TimeSpan.FromMilliseconds(250)),
        LastRoundTrip = TimeSpan.FromMilliseconds(23),
        RekeyCount = 2,
        LastRekey = new(SshRekeyReason.Bytes, 1_073_741_824, 1_073_741_824, TimeSpan.FromMilliseconds(40)),
        BytesSent = 2048,
        BytesReceived = 5 * 1024 * 1024,
        PacketsSent = 30,
        PacketsReceived = 900,
        Channels =
        [
            new(0, "session", SshChannelPhase.Open, new DateTimeOffset(2026, 10, 24, 9, 17, 55, TimeSpan.Zero), 1024, 4096),
            new(3, "direct-tcpip", SshChannelPhase.HalfClosed, null, 0, 0),
        ],
        Proxy = "socks5 127.0.0.1:1080",
        Hops = [new("jumper@bastion:22", "SSH-2.0-OpenSSH_8.0", "curve25519-sha256", "rsa-sha2-512", "aes128-ctr", "password", IsAlive: false)],
        ObscureKeystrokeTiming = true,
    };

    private static string ValueOf(SshConnectionInfoViewModel vm, string sectionKey, string labelKey) =>
        vm.Sections.Single(s => s.Title == Strings.Get(sectionKey)).Fields.Single(f => f.Label == Strings.Get(labelKey)).Value;

    [TestMethod]
    public void Sections_SpellOutWhatTheLibraryKnows()
    {
        var vm = new SshConnectionInfoViewModel("prod", Sample);

        Assert.IsTrue(vm.HasDetails);
        Assert.AreSequenceEqual(
            [.. new[] { "SshInfo_SecPeer", "SshInfo_SecAlgorithms", "SshInfo_SecAuth", "SshInfo_SecTiming", "SshInfo_SecTraffic" }.Select(Strings.Get)], [.. vm.Sections.Select(s => s.Title)]);
        Assert.AreEqual("SSH-2.0-OpenSSH_9.6p1", ValueOf(vm, "SshInfo_SecPeer", "SshInfo_PeerVersion"));
        Assert.AreEqual("SHA256:abcdEFGH", ValueOf(vm, "SshInfo_SecPeer", "SshInfo_Fingerprint"));
        Assert.AreEqual(Strings.Format("SshInfo_HostKeyValue", "ssh-ed25519", 256), ValueOf(vm, "SshInfo_SecPeer", "SshInfo_HostKey"));
        // 两个方向不一样时分开写,一样时只写一个。
        Assert.AreEqual(Strings.Format("SshInfo_TwoWay", "aes256-gcm@openssh.com", "chacha20-poly1305@openssh.com"),
            ValueOf(vm, "SshInfo_SecAlgorithms", "Ssh_AlgoKindEncryption"));
        Assert.AreEqual("none", ValueOf(vm, "SshInfo_SecAlgorithms", "SshInfo_Compression"));
        // AEAD 没有单独的 MAC:说清是加密自带的,而不是留空让人以为没有完整性保护。
        Assert.AreEqual(Strings.Get("SshInfo_MacAead"), ValueOf(vm, "SshInfo_SecAlgorithms", "Ssh_AlgoKindMac"));
        Assert.AreEqual("ssh-ed25519, rsa-sha2-512", ValueOf(vm, "SshInfo_SecAuth", "SshInfo_ServerSigAlgs"));
        Assert.AreEqual("4.50 s", ValueOf(vm, "SshInfo_SecTiming", "SshInfo_TimeKex"));
        Assert.AreEqual("23 ms", ValueOf(vm, "SshInfo_SecTiming", "SshInfo_RoundTrip"));
        Assert.AreEqual(Strings.Format("SshInfo_RekeyBytes", "1,073,741,824", "1,073,741,824"),
            ValueOf(vm, "SshInfo_SecTraffic", "SshInfo_LastRekey"));
        Assert.AreEqual(Strings.Get("SshInfo_KeystrokeOn"), ValueOf(vm, "SshInfo_SecTraffic", "SshInfo_KeystrokeTiming"));
    }

    [TestMethod]
    public void ChannelsAndRoute_AreListedRowByRow()
    {
        var vm = new SshConnectionInfoViewModel("prod", Sample);

        Assert.AreEqual(Strings.Format("SshInfo_SecChannels", 2), vm.ChannelsTitle);
        ConnectionInfoChannelRow shell = vm.Channels[0];
        Assert.AreEqual("session", shell.Type);
        Assert.AreEqual(Strings.Get("SshInfo_PhaseOpen"), shell.Phase);
        Assert.AreEqual("12:05", shell.Age, "开了多久按取快照的时刻算。");
        Assert.AreEqual("4.0 KB", shell.Received);
        Assert.AreEqual(Strings.Get("SshInfo_PhaseHalfClosed"), vm.Channels[1].Phase);
        Assert.AreEqual("—", vm.Channels[1].Age);

        Assert.IsTrue(vm.HasRoute);
        Assert.HasCount(2, vm.Route);
        Assert.AreEqual("socks5 127.0.0.1:1080", vm.Route[0].Target);
        Assert.AreEqual(Strings.Get("SshInfo_Proxy"), vm.Route[0].Summary);
        Assert.AreEqual(2, vm.Route[1].Index);
        Assert.AreEqual("jumper@bastion:22", vm.Route[1].Target);
        Assert.AreEqual("SSH-2.0-OpenSSH_8.0 · curve25519-sha256 · rsa-sha2-512 · aes128-ctr · password", vm.Route[1].Summary);
        Assert.IsTrue(vm.Route[1].IsDown);
    }

    [TestMethod]
    public void CopyText_HoldsTheSameThingAsThePanel()
    {
        var vm = new SshConnectionInfoViewModel("prod", Sample);

        string text = vm.BuildText();

        Assert.Contains(vm.Subtitle, text);
        Assert.Contains($"[{Strings.Get("SshInfo_SecAlgorithms")}]", text);
        Assert.Contains($"  {Strings.Get("Ssh_AlgoKindKex")}: mlkem768x25519-sha256", text);
        Assert.Contains($"[{vm.ChannelsTitle}]", text);
        Assert.Contains("#0  session", text);
        Assert.Contains($"[{Strings.Get("SshInfo_SecRoute")}]", text);
        Assert.Contains($"2. jumper@bastion:22  SSH-2.0-OpenSSH_8.0", text);
        Assert.Contains($"({Strings.Get("SshInfo_HopDown")})", text);
    }

    [TestMethod]
    public async Task NotConnected_SaysSo_AndRefreshFollowsTheSource()
    {
        SshConnectionDetails? current = null;
        int reads = 0;
        var vm = new SshConnectionInfoViewModel("prod", () =>
        {
            reads++;
            return current;
        });

        Assert.IsTrue(vm.IsUnavailable);
        Assert.IsEmpty(vm.Sections);
        Assert.IsFalse(vm.HasRoute);
        Assert.AreEqual(string.Empty, vm.BuildText());
        Assert.IsFalse(await vm.CopyCommand.CanExecute.FirstAsync());

        current = Sample();
        vm.RefreshCommand.Execute().Subscribe();
        Assert.IsTrue(vm.HasDetails);
        Assert.AreEqual(2, reads, "刷新就是再取一份,不缓存。");

        current = null;
        vm.Refresh();
        Assert.IsTrue(vm.IsUnavailable, "会话断了、标签关了,刷新之后如实说没有连着。");
    }

    [TestMethod]
    public void SourceThatIsBeingTornDown_CountsAsNotConnected()
    {
        var vm = new SshConnectionInfoViewModel("prod", () => throw new ObjectDisposedException("client"));

        Assert.IsTrue(vm.IsUnavailable);
    }

    [TestMethod]
    public Task Copy_HandsTheTextToTheClipboard_AndShowsTheTick() => Session.RunOnUiAsync(async () =>
    {
        var vm = new SshConnectionInfoViewModel("prod", Sample);
        string? copied = null;
        vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await vm.CopyCommand.Execute().FirstAsync();

        Assert.AreEqual(vm.BuildText(), copied);
        Assert.IsTrue(vm.Copied);
    });

    /// <summary>窗口挂上视图模型能画出来;标题栏的两个图标按钮都有读屏名字(DESIGN.md §4.2)。</summary>
    [TestMethod]
    public Task Window_RendersTheBlocks_AndTitleActionsHaveNames() => Session.RunOnUiAsync(() =>
    {
        var window = new SshConnectionInfoWindow { DataContext = new SshConnectionInfoViewModel("prod", Sample) };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using WriteableBitmap? frame = window.CaptureRenderedFrame();

            Button copy = window.FindControl<Button>("CopyButton")!;
            Button refresh = window.FindControl<Button>("RefreshButton")!;
            Assert.AreEqual(Strings.Get("SshInfo_Copy"), Avalonia.Automation.AutomationProperties.GetName(copy));
            Assert.AreEqual(Strings.Get("SshInfo_Refresh"), Avalonia.Automation.AutomationProperties.GetName(refresh));
            Assert.IsTrue(copy.IsEffectivelyEnabled);
            string[] texts = [.. window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
            Assert.Contains(Strings.Get("SshInfo_SecPeer"), texts);
            Assert.Contains(Strings.Format("SshInfo_SecChannels", 2), texts);
            Assert.Contains(Strings.Get("SshInfo_SecRoute"), texts);
        }
        finally
        {
            window.Close();
        }
        return Task.CompletedTask;
    });

    /// <summary>标签右键「连接信息」只对 SSH 会话标签有效:本地终端与插件终端协议没有可看的。</summary>
    [TestMethod]
    public Task TabMenuRequest_OnlyForSshSessionTabs() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var ssh = new TerminalTabViewModel(control)
        {
            Profile = new SessionProfile { Name = "prod", Host = "10.0.0.5", Username = "ops", ConnectionType = ConnectionType.SSH },
        };
        using var pluginControl = new VelaTerminalControl();
        await using var plugin = new TerminalTabViewModel(pluginControl)
        {
            Profile = new SessionProfile { Name = "telnet", Host = "10.0.0.6", ConnectionType = ConnectionType.Plugin },
        };
        int raised = 0;
        ssh.ConnectionInfoRequested += (_, _) => raised++;
        plugin.ConnectionInfoRequested += (_, _) => raised++;

        Assert.IsTrue(ssh.IsSshTerminal);
        Assert.IsFalse(plugin.IsSshTerminal);
        ssh.RequestConnectionInfo();
        plugin.RequestConnectionInfo();
        Assert.AreEqual(1, raised);

        var main = new MainWindowViewModel();
        List<TerminalTabViewModel> requested = [];
        main.ConnectionInfoRequested += requested.Add;
        main.OpenConnectionInfo(ssh);
        main.OpenConnectionInfo(plugin);
        Assert.AreSequenceEqual([ssh], requested);
        // 没有 SSH 会话服务(或标签断着)时面板如实说没有连着,而不是抛。
        SshConnectionInfoViewModel panel = main.CreateConnectionInfo(ssh);
        Assert.AreEqual("prod", panel.SessionName);
        Assert.IsTrue(panel.IsUnavailable);
    });
}
