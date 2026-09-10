using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 关掉一个还在握手的终端标签 = 不连了。
/// <para>
/// 标签在握手**开始前**就建好(#17),于是"关标签"与"取消连接"之间必须有一根绳:
/// 没有它的时候,关掉标签只是把标签移走,握手照旧在后台跑到底 —— 右下角「后台任务」
/// 里那条「连接中」赖着不走,几十秒后还要为一个早就没了的标签弹一句"无法连接"。
/// 这组用例守的就是这根绳:令牌被取消、圆环熄灭、不留错误提示。
/// </para>
/// </summary>
[TestClass]
public sealed class ConnectingTabCancellationTests
{
    private static SessionProfile Profile() =>
        new()
        {
            Name = "Debian13(测试服务器)",
            Host = "10.0.0.9",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "pass"
        };

    /// <summary>永远连不完的握手:只有令牌被取消时才结束。</summary>
    private static void NeverCompletes(
        IConnectionWorkflowService workflow,
        Action<CancellationToken> observe)
    {
        workflow
            .ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = (CancellationToken)call[1];
                observe(token);
                await Task.Delay(Timeout.Infinite, token);
                return null!;
            });
    }

    /// <summary>
    /// 握手途中关掉标签:令牌被取消、标签撤走、后台任务清空,且不弹失败提示。
    /// </summary>
    [TestMethod]
    public async Task ClosingAConnectingTab_CancelsTheHandshake()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        CancellationToken handshakeToken = default;
        NeverCompletes(workflow, token => handshakeToken = token);
        using var activity = new BackgroundActivityService();
        var vm = new MainWindowViewModel(
            workflow,
            ssh,
            () => Substitute.For<ITerminalEmulator>(),
            backgroundActivity: activity);

        Task<TerminalTabViewModel?> connecting = vm.TryConnectProfileAsync(Profile());

        // 标签在握手开始前就在了,右下角也已经登记了一条「连接中」。
        TerminalTabViewModel tab = vm.TerminalTabs.Single();
        Assert.AreEqual(SessionStatus.Connecting, tab.ConnectionStatus);
        Assert.HasCount(1, activity.Activities);

        vm.CloseTerminalTab(tab);

        Assert.IsNull(await connecting);
        Assert.IsTrue(handshakeToken.IsCancellationRequested, "关标签必须把握手的取消令牌拉断。");
        Assert.IsEmpty(vm.TerminalTabs);
        // 后台任务清单里不能留下一条为已关闭标签转着的「连接中」。
        Assert.IsEmpty(activity.Activities);
        // 用户自己关掉的标签不该再被报一句"无法连接"。
        Assert.IsNull(vm.LastConnectionError);
        Assert.IsEmpty(vm.Toasts.Toasts);
    }

    /// <summary>
    /// 取消恰好赶在握手完成之后:会话已经建起来了,标签却没了 —— 必须把它拆掉,
    /// 否则留下一条谁都看不见、也永远不会被关掉的连接。
    /// </summary>
    [TestMethod]
    public async Task HandshakeThatLandsAfterTheTabIsGone_TearsDownTheSession()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        var sessionId = Guid.NewGuid();
        var gate = new TaskCompletionSource<SshSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow
            .ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        var vm = new MainWindowViewModel(workflow, ssh, () => Substitute.For<ITerminalEmulator>());

        Task<TerminalTabViewModel?> connecting = vm.TryConnectProfileAsync(Profile());
        TerminalTabViewModel tab = vm.TerminalTabs.Single();
        vm.CloseTerminalTab(tab);

        // 标签已经没了,这时候握手才回来。
        gate.SetResult(new()
        {
            SessionId = sessionId,
            ConnectionInfo = new() { Host = "10.0.0.9", Port = 22, Username = "root", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected
        });

        Assert.IsNull(await connecting);
        Assert.IsEmpty(vm.TerminalTabs);
        Assert.IsNull(vm.LastConnectionError);
        // 拆会话是后台线程上的事(见 TeardownSshSession),给它一小段时间落地。
        for (int i = 0; i < 50 && workflow.ReceivedCalls().All(c => c.GetMethodInfo().Name != nameof(IConnectionWorkflowService.DisconnectAsync)); i++)
        {
            await Task.Delay(20);
        }
        await workflow.Received().DisconnectAsync(sessionId, Arg.Any<CancellationToken>());
    }
}
