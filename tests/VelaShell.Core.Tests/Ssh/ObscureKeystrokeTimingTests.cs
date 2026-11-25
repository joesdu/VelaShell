using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 连接配置里的「按键时序混淆」一路接到库的 shell 选项上:开了才混淆,打开 shell 时终端顶部说一声;
/// 服务端不认 PING(只攒批、不发掩护)时用黄字说清 —— 不说的话用户以为已经藏住了。
/// </summary>
/// <remarks>走宿主真实的装配路径(<c>SshConnectionAssembler</c> → <c>VelaSshClientWrapper</c>),对端是环回上的测试服务端。</remarks>
[TestClass]
[TestCategory("SshConnection")]
public sealed class ObscureKeystrokeTimingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Off_ByDefault_NothingIsObscuredOrAnnounced()
    {
        await using LoopbackSshHost host = new(new TestChannelScript { CloseAfterScript = false, PingOnStart = [1] });

        IReadOnlyList<ShellStreamNotice> notices = await OpenShellNoticesAsync(host, new SshSessionOptions { Compression = true });

        Assert.DoesNotContain(n => n.Text == Strings.Get("Ssh_ObscureKeystrokeOn"), notices);
        Assert.DoesNotContain(n => n.Text == Strings.Get("Ssh_ObscureKeystrokeNoPing"), notices);
    }

    [TestMethod]
    public async Task On_WithAServerThatSpeaksPing_ObscuresAndSaysSoInGrey()
    {
        await using LoopbackSshHost host = new(new TestChannelScript { CloseAfterScript = false, PingOnStart = [1] });

        IReadOnlyList<ShellStreamNotice> notices =
            await OpenShellNoticesAsync(host, new SshSessionOptions { ObscureKeystrokeTiming = true });

        ShellStreamNotice notice = notices.Single(n => n.Text == Strings.Get("Ssh_ObscureKeystrokeOn"));
        Assert.IsFalse(notice.IsWarning);
    }

    [TestMethod]
    public async Task On_WithAServerWithoutPing_WarnsThatOnlyBatchingHappens()
    {
        await using LoopbackSshHost host = new(new TestChannelScript { CloseAfterScript = false });

        IReadOnlyList<ShellStreamNotice> notices =
            await OpenShellNoticesAsync(host, new SshSessionOptions { ObscureKeystrokeTiming = true });

        ShellStreamNotice notice = notices.Single(n => n.Text == Strings.Get("Ssh_ObscureKeystrokeNoPing"));
        Assert.IsTrue(notice.IsWarning, "没有掩护报文,按键间隔照样看得见:要用黄字提醒。");
    }

    private static async Task<IReadOnlyList<ShellStreamNotice>> OpenShellNoticesAsync(LoopbackSshHost host, SshSessionOptions ssh)
    {
        ConnectionInfo info = new()
        {
            Host = "127.0.0.1",
            Port = host.Port,
            Username = "joe",
            AuthMethod = AuthMethod.Password,
            Password = LoopbackSshHost.Password,
            Ssh = ssh,
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: FixtureHostKeys.Create(), settings: null, prompt: null, alerts: null, proxyResolver: null);
        await using VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh, banners: assembled.Banners);
        using CancellationTokenSource cts = new(Deadline);
        await wrapper.ConnectAsync(cts.Token);
        await using IShellStreamWrapper shell = await wrapper.CreateShellStreamAsync("xterm", 80, 24, 0, 0, 4096, cancellationToken: cts.Token);
        return [.. shell.Notices];
    }
}
