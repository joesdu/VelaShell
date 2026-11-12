using System.Collections.Concurrent;
using Avalonia.Headless;
using NSubstitute;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Presentation.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class TunnelPanelViewModelTests
{
    private readonly SessionProfile _server;
    private readonly Guid _sessionId;
    private readonly ITunnelWorkflowService _workflowService;
    private readonly TunnelPanelViewModel _vm;

    public TunnelPanelViewModelTests()
    {
        _workflowService = Substitute.For<ITunnelWorkflowService>();
        _sessionId = Guid.NewGuid();
        _server = new() { Name = "srv", Host = "10.0.0.1", Username = "root" };

        // 面板以服务器为中心:后台连接器直接返回固定会话,存活检查恒真。
        _vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask);
        _vm.Servers.Add(_server);
        _vm.SelectedServer = _server;
    }

    // ———— 断线自动恢复 ————

    /// <summary>
    /// 承载会话掉线后,勾了「自动重连」的隧道会被自动重拨并重建 ——
    /// 这正是这个开关存在的意义,不然用户还是得手动点启动。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_SessionDropped_RebuildsAutoReconnectTunnel()
    {
        bool alive = true;
        var reconnectedSession = Guid.NewGuid();
        TunnelPanelViewModel vm = CreateVm(() => alive, _ => Task.FromResult(reconnectedSession));
        await SeedTunnelAsync(vm, autoReconnect: true);

        alive = false;
        TunnelInfo rebuilt = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(reconnectedSession, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(rebuilt));

        vm.RefreshLiveState();

        await WaitForAsync(() => vm.Tunnels.Count == 1 && vm.Tunnels[0].IsActive);
        Assert.AreEqual(rebuilt.Id, vm.Tunnels[0].Id, "条目应换成重建后的那条隧道。");
        // 重建前先清掉服务侧停止状态的旧记录,否则列表里会越积越多。
        await _workflowService.Received().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>没勾自动重连的隧道掉线后只标记为已停止,等用户自己决定 —— 默认不替用户重拨。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_SessionDropped_LeavesManualTunnelStopped()
    {
        bool alive = true;
        TunnelPanelViewModel vm = CreateVm(() => alive, _ => Task.FromResult(Guid.NewGuid()));
        await SeedTunnelAsync(vm, autoReconnect: false);
        _workflowService.ClearReceivedCalls();

        alive = false;
        vm.RefreshLiveState();
        await Task.Delay(150);

        Assert.HasCount(1, vm.Tunnels);
        Assert.IsFalse(vm.Tunnels[0].IsActive);
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 重拨失败后要退避,不能每一拍时钟都重来一次 —— 服务器真的下线时,
    /// 每 5 秒一次的重连会把凭据提示刷满屏幕。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_ReconnectFailure_BacksOffBeforeRetrying()
    {
        bool alive = true;
        bool serverIsDown = false;
        int attempts = 0;
        TunnelPanelViewModel vm = CreateVm(() => alive, _ =>
        {
            if (!serverIsDown)
            {
                return Task.FromResult(_sessionId);
            }
            Interlocked.Increment(ref attempts);
            return Task.FromException<Guid>(new InvalidOperationException("host down"));
        });
        await SeedTunnelAsync(vm, autoReconnect: true);

        alive = false;
        serverIsDown = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => Volatile.Read(ref attempts) == 1);

        // 紧接着的几拍都落在退避窗口内,不该再拨。
        vm.RefreshLiveState();
        vm.RefreshLiveState();
        await Task.Delay(150);

        Assert.AreEqual(1, Volatile.Read(ref attempts), "退避窗口内不该重复重拨。");
        Assert.IsNotNull(vm.ErrorMessage, "失败原因要让用户看得到。");
    }

    /// <summary>
    /// 自动恢复按「没人在场」连(主机指纹要人裁决时直接拒绝、不弹窗);败在指纹上就停下,不再一遍遍试 ——
    /// 再试多少次结果都一样,而在自动恢复不弹窗之前,每试一次就弹一遍确认框。
    /// 面板外也要报一声:只有用户手动启动、在弹窗里核对过,这些隧道才回得来。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_HostKeyRejected_StopsAutoReconnect()
    {
        bool alive = true;
        bool keyChanged = false;
        int attempts = 0;
        int automaticAttempts = 0;
        ConcurrentQueue<string> reported = [];
        TunnelPanelViewModel vm = CreateVm(() => alive, (_, automatic) =>
        {
            if (!keyChanged)
            {
                return Task.FromResult(_sessionId);
            }
            Interlocked.Increment(ref attempts);
            if (automatic)
            {
                Interlocked.Increment(ref automaticAttempts);
            }
            return Task.FromException<Guid>(new VelaSshHostKeyRejectedException("指纹要人确认"));
        }, reported.Enqueue);
        await SeedTunnelAsync(vm, autoReconnect: true);

        alive = false;
        keyChanged = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => !reported.IsEmpty);

        // 指纹被拒不记退避:没有那道拦截的话,紧接着的这几拍会马上再去试。
        vm.RefreshLiveState();
        vm.RefreshLiveState();
        await Task.Delay(150);

        Assert.AreEqual(1, Volatile.Read(ref attempts), "败在指纹上之后不该再自动重拨。");
        Assert.AreEqual(1, Volatile.Read(ref automaticAttempts), "自动恢复要按「没人在场」连。");
        Assert.HasCount(1, reported, "面板外也要报一声。");
    }

    /// <summary>用户手动启动(按「有人在场」连、会弹窗问他)、连上之后,这台的自动恢复照常恢复。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StartTunnel_AfterHostKeyRejection_ResumesAutoReconnect()
    {
        bool alive = true;
        bool keyChanged = false;
        int automaticAttempts = 0;
        bool? manualWasAutomatic = null;
        TunnelPanelViewModel vm = CreateVm(() => alive, (_, automatic) =>
        {
            if (automatic)
            {
                Interlocked.Increment(ref automaticAttempts);
            }
            if (keyChanged)
            {
                return Task.FromException<Guid>(new VelaSshHostKeyRejectedException("指纹要人确认"));
            }
            if (!automatic)
            {
                manualWasAutomatic = false;
            }
            alive = true;
            return Task.FromResult(_sessionId);
        }, _ => { });
        await SeedTunnelAsync(vm, autoReconnect: true);
        TunnelItemViewModel tunnel = vm.Tunnels[0];

        alive = false;
        keyChanged = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => Volatile.Read(ref automaticAttempts) == 1 && !tunnel.IsBusy);

        // 用户在弹窗里核对过、接受了:手动启动连上。
        keyChanged = false;
        manualWasAutomatic = null;
        await vm.StartTunnelCommand.Execute(tunnel.Id).FirstAsync();
        Assert.IsFalse(manualWasAutomatic, "手动启动要按「有人在场」连 —— 那一次会弹窗。");

        // 之后再掉线,自动恢复照常。
        alive = false;
        vm.RefreshLiveState();
        await WaitForAsync(() => Volatile.Read(ref automaticAttempts) == 2);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow("SshErr_TailscaleCheckCancelled")]
    [DataRow("SshErr_TailscaleRejected")]
    [DataRow("SshErr_TailscaleCheckTimeout")]
    [DataRow("SshErr_TailscaleAuthTimeout")]
    [DataRow("SshErr_TailscaleCheckRequired")]
    [DataRow("SshErr_TailscaleCheckUiUnavailable")]
    [DataRow("SshErr_TailscaleCheckFailed")]
    public async Task RefreshLiveState_TailscaleAuthenticationFailure_PausesWithoutChangingSavedPolicy(string errorKey)
    {
        bool alive = true;
        bool failAuthentication = false;
        int attempts = 0;
        List<string> errors = [];
        using TunnelPanelViewModel vm = CreateVm(() => alive, (_, automatic) =>
        {
            if (!failAuthentication)
            {
                return Task.FromResult(_sessionId);
            }
            Assert.IsTrue(automatic);
            attempts++;
            return Task.FromException<Guid>(TailscaleFailure(errorKey));
        }, errors.Add);
        await SeedTunnelAsync(vm, autoReconnect: true);
        TunnelItemViewModel original = vm.Tunnels[0];
        _workflowService.ClearReceivedCalls();

        alive = false;
        failAuthentication = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => attempts == 1 && !original.IsBusy);
        vm.RefreshLiveState();
        vm.RefreshLiveState();

        Assert.AreEqual(1, attempts, "认证暂停后刷新不得再次请求连接。");
        Assert.AreSame(original, vm.Tunnels[0]);
        Assert.IsFalse(original.IsActive);
        Assert.IsFalse(original.StoppedByUser, "认证暂停不是用户停止隧道。");
        Assert.IsTrue(original.Config.AutoReconnect, "不能通过修改保存的恢复策略来暂停认证。");
        if (errorKey == "SshErr_TailscaleCheckCancelled")
        {
            Assert.IsEmpty(errors);
            Assert.IsNull(vm.ErrorMessage);
        }
        else
        {
            Assert.AreSequenceEqual([Strings.Get(errorKey)], errors);
            Assert.AreEqual(Strings.Get(errorKey), vm.ErrorMessage);
        }
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 认证途中断线、没等到验证链接就超时不是授权结论:走普通失败的退避,而不是把这台服务器记成「认证要人处理」
    /// —— 曾经一律记进去,之后网络恢复也不再自动重建。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_TailscaleTransientFailure_BacksOffInsteadOfPausing()
    {
        bool alive = true;
        bool fail = false;
        int attempts = 0;
        List<string> errors = [];
        string transient = Strings.Get("SshErr_TailscaleAuthTimeout");
        using TunnelPanelViewModel vm = CreateVm(() => alive, (_, _) =>
        {
            if (!fail)
            {
                return Task.FromResult(_sessionId);
            }
            attempts++;
            return Task.FromException<Guid>(new VelaSshTailscaleAuthenticationException(transient, requiresUserAction: false));
        }, errors.Add);
        await SeedTunnelAsync(vm, autoReconnect: true);
        TunnelItemViewModel original = vm.Tunnels[0];

        alive = false;
        fail = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => attempts == 1 && !original.IsBusy);

        Assert.IsEmpty(errors, "临时失败不按「认证要人处理」报。");
        Assert.AreEqual(Strings.Format("Msg_TunnelAutoReconnectFailed", transient), vm.ErrorMessage, "走的是普通失败的退避分支。");
        Assert.IsTrue(original.Config.AutoReconnect);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow("SshErr_TailscaleCheckCancelled")]
    [DataRow("SshErr_TailscaleCheckRequired")]
    public async Task StartTunnel_TailscalePause_OnlySuccessfulManualConnectionRestoresNetworkRecovery(string errorKey)
    {
        bool alive = true;
        bool failAuthentication = false;
        int automaticAttempts = 0;
        int manualAttempts = 0;
        using TunnelPanelViewModel vm = CreateVm(() => alive, (_, automatic) =>
        {
            if (automatic)
            {
                automaticAttempts++;
            }
            else
            {
                manualAttempts++;
            }
            if (failAuthentication)
            {
                return Task.FromException<Guid>(TailscaleFailure(errorKey));
            }
            alive = true;
            return Task.FromResult(_sessionId);
        });
        await SeedTunnelAsync(vm, autoReconnect: true);
        TunnelItemViewModel original = vm.Tunnels[0];
        alive = false;
        failAuthentication = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => automaticAttempts == 1 && !original.IsBusy);

        await vm.StartTunnelCommand.Execute(original.Id).FirstAsync();
        Assert.AreEqual(2, manualAttempts, "暂停不能拦截显式手动尝试。");
        vm.RefreshLiveState();
        Assert.AreEqual(1, automaticAttempts, "失败的手动尝试不能清除暂停。");

        failAuthentication = false;
        await vm.StartTunnelCommand.Execute(original.Id).FirstAsync();
        Assert.AreEqual(3, manualAttempts);
        Assert.IsTrue(vm.Tunnels[0].IsActive);
        Assert.IsTrue(vm.Tunnels[0].AutoReconnect);
        Assert.IsNull(vm.ErrorMessage);
        Guid manuallyRebuiltId = vm.Tunnels[0].Id;

        alive = false;
        vm.RefreshLiveState();
        await WaitForAsync(() => automaticAttempts == 2 && vm.Tunnels[0].IsActive && vm.Tunnels[0].Id != manuallyRebuiltId);
        await _workflowService.Received().RemoveTunnelAsync(manuallyRebuiltId, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_TailscalePause_IsIsolatedPerProfile()
    {
        SessionProfile other = new() { Name = "other", Host = "10.0.0.2", Username = "root" };
        bool alive = true;
        int blockedAttempts = 0;
        int otherAttempts = 0;
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server, other]),
            (profile, automatic, _) =>
            {
                if (!automatic)
                {
                    return Task.FromResult(profile.Id);
                }
                if (profile.Id == _server.Id)
                {
                    blockedAttempts++;
                    return Task.FromException<Guid>(TailscaleFailure("SshErr_TailscaleRejected"));
                }
                otherAttempts++;
                return Task.FromResult(profile.Id);
            }, _ => alive);
        vm.Servers.Add(_server);
        vm.Servers.Add(other);
        vm.SelectedServer = _server;
        await SeedTunnelAsync(vm, autoReconnect: true);
        TunnelItemViewModel blocked = vm.Tunnels[0];
        vm.SelectedServer = other;
        await SeedTunnelAsync(vm, autoReconnect: true);

        alive = false;
        vm.RefreshLiveState();
        await WaitForAsync(() => blockedAttempts == 1 && otherAttempts == 1 && !blocked.IsBusy && !vm.Tunnels[0].IsBusy);
        vm.RefreshLiveState();
        await WaitForAsync(() => otherAttempts == 2 && !vm.Tunnels[0].IsBusy);

        Assert.AreEqual(1, blockedAttempts);
        Assert.IsTrue(vm.Tunnels[0].IsActive, "另一台服务器仍应按其原策略重建。");
        vm.SelectedServer = _server;
        Assert.AreSame(blocked, vm.Tunnels[0]);
        Assert.IsTrue(blocked.AutoReconnect);
    }

    private static Exception TailscaleFailure(string errorKey) =>
        errorKey == "SshErr_TailscaleCheckCancelled"
            ? new VelaSshAuthenticationCancelledException(Strings.Get(errorKey))
            : new VelaSshTailscaleAuthenticationException(Strings.Get(errorKey));

    /// <summary>
    /// 用户按停过的隧道,后来会话掉线也不该被自动恢复拉起来 ——
    /// 「掉线后自动重连」扛的是网络抖动,不是把用户刚按下的停止键撤销掉。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_DoesNotRebuild_TunnelTheUserStopped()
    {
        bool alive = true;
        TunnelPanelViewModel vm = CreateVm(() => alive, _ => Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: true);
        await vm.StopTunnelCommand.Execute(vm.Tunnels[0].Id).FirstAsync();
        _workflowService.ClearReceivedCalls();

        alive = false;
        vm.RefreshLiveState();
        await Task.Delay(150);

        Assert.IsFalse(vm.Tunnels[0].IsActive);
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    // ———— 启停途中的忙碌态 ————

    /// <summary>
    /// 启动要先连服务器再建转发,得等一会儿:途中启停键换成转圈、编辑键点不动、状态行写「正在启动」;
    /// 走完换回对应的键。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StartTunnel_InFlight_ShowsBusyUntilTheTunnelIsUp()
    {
        TunnelPanelViewModel vm = CreateVm(() => true, _ => Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: false, status: TunnelStatus.Stopped);
        TunnelItemViewModel stopped = vm.Tunnels[0];
        TaskCompletionSource<TunnelInfo> creating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(creating.Task);

        Task start = ExecuteAsync(vm.StartTunnelCommand, stopped.Id);
        await WaitForAsync(() => stopped.IsStarting);

        Assert.IsTrue(stopped.IsBusy);
        Assert.IsFalse(stopped.ShowStartButton, "启动途中不该再露出「启动」键。");
        Assert.IsFalse(stopped.ShowStopButton);
        Assert.IsFalse(stopped.CanEdit, "启动途中不该能改配置。");
        Assert.AreEqual(Strings.Get("Tunnel_Starting"), stopped.StatusText);

        creating.SetResult(CreateTunnelInfo(status: TunnelStatus.Active));
        await start;

        TunnelItemViewModel started = vm.Tunnels[0];
        Assert.IsTrue(started.IsActive);
        Assert.IsFalse(started.IsBusy);
        Assert.IsTrue(started.ShowStopButton, "起来之后换成「停止」键。");
    }

    /// <summary>停止同理:途中转圈、写「正在停止」,服务返回后换回「启动」键。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StopTunnel_InFlight_ShowsBusyUntilTheServiceReturns()
    {
        TunnelPanelViewModel vm = CreateVm(() => true, _ => Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: false);
        TunnelItemViewModel tunnel = vm.Tunnels[0];
        TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowService.StopTunnelAsync(tunnel.Id, Arg.Any<CancellationToken>()).Returns(stopping.Task);

        Task stop = ExecuteAsync(vm.StopTunnelCommand, tunnel.Id);
        await WaitForAsync(() => tunnel.IsStopping);

        Assert.IsFalse(tunnel.ShowStopButton, "停止途中不该再露出「停止」键。");
        Assert.IsFalse(tunnel.ShowStartButton);
        Assert.AreEqual(Strings.Get("Tunnel_Stopping"), tunnel.StatusText);

        stopping.SetResult();
        await stop;

        Assert.IsFalse(tunnel.IsBusy);
        Assert.IsFalse(tunnel.IsActive);
        Assert.IsTrue(tunnel.ShowStartButton, "停下之后换成「启动」键。");
    }

    /// <summary>启动失败:条目留在列表里,转圈撤掉、「启动」键回来,失败原因写在表单下。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StartTunnel_Failure_RestoresTheStartButton()
    {
        TunnelPanelViewModel vm = CreateVm(() => true, _ => Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: false, status: TunnelStatus.Stopped);
        TunnelItemViewModel stopped = vm.Tunnels[0];
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromException<TunnelInfo>(new InvalidOperationException("boom")));

        await vm.StartTunnelCommand.Execute(stopped.Id).FirstAsync();

        Assert.AreSame(stopped, vm.Tunnels[0]);
        Assert.IsFalse(stopped.IsBusy);
        Assert.IsTrue(stopped.ShowStartButton);
        Assert.IsNotNull(vm.ErrorMessage);
    }

    /// <summary>
    /// 自动恢复正在重拨、重建这条隧道时,它同样显示转圈;这时用户再点「启动」不能再建一条 ——
    /// 原先两条路各建各的,同一个本地端口被绑两次,后到的那条报端口占用。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StartTunnel_WhileAutoReconnectRebuildsIt_DoesNotCreateTwice()
    {
        bool alive = true;
        TaskCompletionSource<Guid> redial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool seeded = false;
        TunnelPanelViewModel vm = CreateVm(() => alive, _ => seeded ? redial.Task : Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: true);
        seeded = true;
        TunnelItemViewModel tunnel = vm.Tunnels[0];

        alive = false;
        vm.RefreshLiveState();
        await WaitForAsync(() => tunnel.IsStarting);
        _workflowService.ClearReceivedCalls();

        await vm.StartTunnelCommand.Execute(tunnel.Id).FirstAsync();
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        alive = true;
        redial.SetResult(Guid.NewGuid());
        await WaitForAsync(() => vm.Tunnels[0].IsActive && !tunnel.IsBusy);

        await _workflowService.Received(1).CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>不等命令走完就返回:用来观察启停途中的状态。</summary>
    private static async Task ExecuteAsync(ReactiveCommand<Guid, RxVoid> command, Guid id) =>
        await command.Execute(id).FirstAsync();

    /// <summary>自动重连开关随隧道配置往返:编辑既有隧道时表单要把它填回来。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task EditTunnel_RestoresAutoReconnectIntoForm()
    {
        TunnelPanelViewModel vm = CreateVm(() => true, _ => Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: true, status: TunnelStatus.Stopped);

        await vm.EditTunnelCommand.Execute(vm.Tunnels[0].Id).FirstAsync();

        Assert.IsTrue(vm.NewAutoReconnect);
    }

    /// <summary>新建表单里勾上自动重连,要原样落到隧道配置上。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_CarriesAutoReconnectIntoConfig()
    {
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(CreateTunnelInfo()));
        FillValidLocalForm();
        _vm.NewAutoReconnect = true;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.IsTrue(captured?.AutoReconnect);
    }

    /// <summary>取消/重置表单后自动重连开关回到默认的关闭。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task ResetForm_ClearsAutoReconnect()
    {
        _vm.NewAutoReconnect = true;

        await _vm.ResetFormCommand.Execute().FirstAsync();

        Assert.IsFalse(_vm.NewAutoReconnect);
    }

    // ———— 流量统计 ————

    /// <summary>统计行:没连接过就直说,有连接则给出连接数与可读的流量。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_StatsText_ReflectsCounters()
    {
        TunnelInfo info = CreateTunnelInfo();
        var item = new TunnelItemViewModel(info);
        Assert.AreEqual(Strings.Get("Tunnel_StatsNone"), item.StatsText);

        info.TotalConnections = 3;
        info.BytesTransferred = 1536;
        item.RefreshLive();
        Assert.AreEqual(Strings.Format("Tunnel_Stats", 3, "1.5 KB"), item.StatsText);

        info.ActiveConnections = 2;
        item.RefreshLive();
        Assert.AreEqual(Strings.Format("Tunnel_StatsLive", 3, 2, "1.5 KB"), item.StatsText);
    }

    // ———— 实时速率、连接列表、限速 ————

    /// <summary>
    /// 速率行:运行中写两个方向此刻的速率,设了限速一并写出;停着只写限速;都没有就整行不显示。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_TrafficText_ShowsRatesAndTheLimit()
    {
        TunnelInfo info = CreateTunnelInfo();
        info.SentBytesPerSecond = 1536;
        info.ReceivedBytesPerSecond = 2 * 1024 * 1024;
        var item = new TunnelItemViewModel(info);
        Assert.AreEqual("↑ 1.5 KB/s  ↓ 2.0 MB/s", item.TrafficText);
        Assert.IsNull(item.TrafficToolTip, "不限速时没有那句说明。");

        TunnelInfo limited = new()
        {
            Id = Guid.NewGuid(),
            Config = new()
            {
                Type = TunnelType.LocalForward,
                Name = "limited",
                LocalHost = "127.0.0.1",
                LocalPort = 5432,
                RemoteHost = "127.0.0.1",
                RemotePort = 5432,
                MaxBytesPerSecond = 512 * 1024
            },
            Status = TunnelStatus.Active,
            SessionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
        var limitedItem = new TunnelItemViewModel(limited);
        Assert.AreEqual($"↑ 0 B/s  ↓ 0 B/s · {Strings.Format("Tunnel_RateLimit", "512.0 KB")}", limitedItem.TrafficText);
        Assert.AreEqual(Strings.Get("Tunnel_RateLimitTip"), limitedItem.TrafficToolTip);

        limitedItem.Status = TunnelStatus.Stopped;
        Assert.AreEqual(Strings.Format("Tunnel_RateLimit", "512.0 KB"), limitedItem.TrafficText, "停着时只剩限多少。");

        item.Status = TunnelStatus.Stopped;
        Assert.IsNull(item.TrafficText);
        Assert.IsFalse(item.HasTrafficText);
    }

    /// <summary>
    /// 当前连接:默认收起(只显示「当前连接 N 条」);展开后逐条列出来源 → 目标、开了多久、两个方向各搬了多少,
    /// 至多 20 条,其余写「还有 N 条未列出」。来源给不出时写「来源未知」。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task TunnelItemViewModel_ConnectionList_ExpandsAndCapsTheList()
    {
        TunnelInfo info = CreateTunnelInfo();
        DateTime now = DateTime.UtcNow;
        info.Connections =
        [
            new(1, "127.0.0.1:53122", "db-server:3306", now.AddSeconds(-185), 1536, 2 * 1024 * 1024),
            new(2, null, "db-server:3306", now, 0, 0),
            .. Enumerable.Range(3, 23).Select(i => new TunnelConnectionInfo(i, $"127.0.0.1:{50000 + i}", "db-server:3306", now, 0, 0))
        ];
        var item = new TunnelItemViewModel(info);

        Assert.IsTrue(item.HasConnections);
        Assert.AreEqual(Strings.Format("Tunnel_ConnectionsToggle", 25), item.ConnectionsToggleText);
        Assert.IsFalse(item.IsConnectionListVisible, "默认收起。");
        Assert.IsEmpty(item.ConnectionRows);

        await item.ToggleConnectionsCommand.Execute().FirstAsync();

        Assert.IsTrue(item.IsConnectionListVisible);
        Assert.HasCount(TunnelItemViewModel.MaxListedConnections, item.ConnectionRows);
        Assert.AreEqual("127.0.0.1:53122 → db-server:3306", item.ConnectionRows[0].Route);
        Assert.AreEqual(Strings.Format("Tunnel_ConnectionDetail", "3m 05s", "1.5 KB", "2.0 MB"), item.ConnectionRows[0].Detail);
        Assert.AreEqual($"{Strings.Get("Tunnel_ConnectionSourceUnknown")} → db-server:3306", item.ConnectionRows[1].Route);
        Assert.AreEqual(5, item.HiddenConnectionCount);
        Assert.AreEqual(Strings.Format("Tunnel_ConnectionsMore", 5), item.HiddenConnectionsText);

        item.Status = TunnelStatus.Stopped;
        Assert.IsFalse(item.HasConnections, "停着的隧道不显示连接。");
        Assert.IsEmpty(item.ConnectionRows);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(0, "0s")]
    [DataRow(42, "42s")]
    [DataRow(185, "3m 05s")]
    [DataRow(3720, "1h 02m")]
    public void TunnelItemViewModel_FormatDuration(int seconds, string expected) =>
        Assert.AreEqual(expected, TunnelItemViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));

    /// <summary>
    /// 限速随表单往返:按 KB/s 填,配置里存字节 / 秒;0 是不限(存 null);编辑时填回来,取消后回到 0。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RateLimit_RoundTripsThroughTheForm()
    {
        Assert.AreEqual(0, _vm.NewRateLimitKiB, "默认不限。");
        List<TunnelConfig> captured = [];
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(captured.Add), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Stopped,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.IsNull(captured[0].MaxBytesPerSecond, "0 = 不限。");

        FillValidLocalForm(remotePort: 3307);
        _vm.NewRateLimitKiB = 512;
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.AreEqual(512 * 1024L, captured[1].MaxBytesPerSecond);
        Assert.AreEqual(0, _vm.NewRateLimitKiB, "建完表单复位。");

        await _vm.EditTunnelCommand.Execute(_vm.Tunnels[1].Id).FirstAsync();
        Assert.AreEqual(512, _vm.NewRateLimitKiB, "编辑时填回表单。");
        await _vm.ResetFormCommand.Execute().FirstAsync();
        Assert.AreEqual(0, _vm.NewRateLimitKiB);

        _vm.NewRateLimitKiB = -5;
        Assert.AreEqual(0, _vm.NewRateLimitKiB, "负数夹回 0。");
    }

    // ———— 远程动态转发与放行名单 ————

    /// <summary>
    /// 选「远程动态 SOCKS」:本地端口那栏收起,主机栏换成「服务器监听地址」,出现放行名单;
    /// 名单空着「创建」点不了,写错一条当场指出是哪条,写对了才能建。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void RemoteDynamic_FormNeedsAValidAllowList()
    {
        FillValidLocalForm();
        Assert.IsTrue(_vm.IsFormValid);
        Assert.IsFalse(_vm.IsPermitRemoteOpenVisible);

        _vm.NewTunnelTypeIndex = 3;

        Assert.AreEqual(TunnelType.RemoteDynamicForward, _vm.NewTunnelType);
        Assert.IsFalse(_vm.IsLocalPortVisible);
        Assert.IsTrue(_vm.IsRemoteTargetVisible);
        Assert.IsTrue(_vm.IsPermitRemoteOpenVisible);
        Assert.AreEqual(Strings.Get("Tunnel_ServerBindHostLabel"), _vm.RemoteHostLabel);
        Assert.AreEqual(Strings.Get("Tunnel_ListenOnServerLoopback"), _vm.LoopbackOptionText);
        Assert.IsFalse(_vm.IsFormValid, "放行名单空着不能建。");
        Assert.IsFalse(_vm.HasPermitRemoteOpenError, "空着不算写错,不报红字。");

        _vm.NewPermitRemoteOpen = "localhost:8080 intranet";
        Assert.IsFalse(_vm.IsFormValid);
        Assert.AreEqual(Strings.Format("Tunnel_PermitRemoteOpenInvalid", "intranet"), _vm.PermitRemoteOpenError);

        _vm.NewPermitRemoteOpen = "localhost:8080 intranet:*";
        Assert.IsTrue(_vm.IsFormValid);
        Assert.IsNull(_vm.PermitRemoteOpenError);

        _vm.NewLocalPort = 0;
        Assert.IsTrue(_vm.IsFormValid, "远程动态转发用不上本地端口。");

        _vm.NewTunnelTypeIndex = 0;
        Assert.IsTrue(_vm.IsLocalPortVisible);
        Assert.AreEqual(Strings.Get("Tunnel_TargetHostLabel"), _vm.RemoteHostLabel);
        Assert.IsFalse(_vm.IsFormValid, "换回本地转发,本地端口 0 又不合格了。");
    }

    /// <summary>
    /// 远程转发的主机与端口两栏是服务器上的监听端(目标是本机的服务):标签说「服务器监听地址」、勾选框说「只在服务器本机监听」。
    /// 曾经与本地转发共用「目标主机(服务器视角)」「转发到服务器本机」。本地转发不变。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void RemoteForward_LabelsTheServerListenEnd()
    {
        _vm.NewTunnelType = TunnelType.RemoteForward;
        Assert.AreEqual(Strings.Get("Tunnel_ServerBindHostLabel"), _vm.RemoteHostLabel);
        Assert.AreEqual(Strings.Get("Tunnel_ListenOnServerLoopback"), _vm.LoopbackOptionText);

        _vm.NewTunnelType = TunnelType.LocalForward;
        Assert.AreEqual(Strings.Get("Tunnel_TargetHostLabel"), _vm.RemoteHostLabel);
        Assert.AreEqual(Strings.Get("Tunnel_ForwardToLoopback"), _vm.LoopbackOptionText);
    }

    /// <summary>
    /// 本机监听没起来时服务层给的那句(在哪儿、为什么)原样显示 —— 哪怕底下是「地址已被占用」,
    /// 也不能被当成「本地端口 N 已被占用」(套接字路径上的占用不是端口的事)。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_BindFailure_ShowsTheServiceMessage()
    {
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromException<TunnelInfo>(new TunnelBindFailedException("本机没能在套接字上监听",
                            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse))));
        FillValidLocalForm();

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.AreEqual("本机没能在套接字上监听", _vm.ErrorMessage);
    }

    /// <summary>
    /// 远程动态转发随表单往返:服务器监听地址 / 端口进 RemoteHost / RemotePort,本地端口存 0,放行名单按条存;
    /// 编辑时填回表单,取消后名单清空。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RemoteDynamic_RoundTripsThroughTheForm()
    {
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Stopped,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        _vm.NewTunnelName = "back";
        _vm.NewTunnelType = TunnelType.RemoteDynamicForward;
        _vm.NewRemotePort = 1080;
        _vm.NewPermitRemoteOpen = "localhost:8080, *.corp.example:443";

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.IsNotNull(captured);
        Assert.AreEqual(TunnelType.RemoteDynamicForward, captured.Type);
        Assert.AreEqual("127.0.0.1", captured.RemoteHost, "默认只在服务器本机监听。");
        Assert.AreEqual(1080u, captured.RemotePort);
        Assert.AreEqual(0u, captured.LocalPort);
        Assert.AreSequenceEqual(["localhost:8080", "*.corp.example:443"], [.. captured.PermitRemoteOpen!]);
        Assert.AreEqual(string.Empty, _vm.NewPermitRemoteOpen, "建完表单复位。");
        Assert.AreEqual(TunnelType.LocalForward, _vm.NewTunnelType);

        await _vm.EditTunnelCommand.Execute(_vm.Tunnels[0].Id).FirstAsync();
        Assert.AreEqual(TunnelType.RemoteDynamicForward, _vm.NewTunnelType);
        Assert.AreEqual(3, _vm.NewTunnelTypeIndex);
        Assert.AreEqual("localhost:8080 *.corp.example:443", _vm.NewPermitRemoteOpen);
        Assert.AreEqual(27017, _vm.NewLocalPort, "存的 0 不填回表单的本地端口。");
        await _vm.ResetFormCommand.Execute().FirstAsync();
        Assert.AreEqual(string.Empty, _vm.NewPermitRemoteOpen);
    }

    /// <summary>列表里的远程动态隧道:徽标、路由、端点,以及放行了哪些(单独一条 any 写明「全部」)。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_RemoteDynamic_Display()
    {
        static TunnelInfo Info(IReadOnlyList<string> permit) => new()
        {
            Id = Guid.NewGuid(),
            Config = new()
            {
                Type = TunnelType.RemoteDynamicForward,
                Name = string.Empty,
                LocalHost = "127.0.0.1",
                LocalPort = 0,
                RemoteHost = "::1",
                RemotePort = 1080,
                PermitRemoteOpen = permit
            },
            Status = TunnelStatus.Stopped,
            SessionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
        var listed = new TunnelItemViewModel(Info(["localhost:8080", "*.corp.example:443"]));
        var any = new TunnelItemViewModel(Info([RemoteOpenRules.Any]));

        Assert.AreEqual(Strings.Get("Tunnel_FallbackRemoteDynamic"), listed.Name);
        Assert.AreEqual(Strings.Get("Tunnel_BadgeRemoteDynamic"), listed.TypeBadge);
        Assert.AreEqual(Strings.Format("Tunnel_RouteRemoteDynamic", "[::1]", 1080u), listed.DisplayRoute);
        Assert.AreEqual("[::1]:1080 (SOCKS5)", listed.EndpointSummary);
        Assert.AreEqual(Strings.Format("Tunnel_PermitRemoteOpenSummary", "localhost:8080 *.corp.example:443"), listed.PermitText);
        Assert.AreEqual(Strings.Get("Tunnel_PermitRemoteOpenAny"), any.PermitText);
        Assert.IsNull(new TunnelItemViewModel(CreateTunnelInfo()).PermitText, "别的类型没有这一行。");
    }

    // ———— Unix 域套接字 ————

    private static readonly string LocalSocket = Path.Combine(Path.GetTempPath(), "docker.sock");

    /// <summary>
    /// 「改用 Unix 域套接字」只对本地 / 远程转发有。本地转发两头各自可以是套接字:填了路径的那头置灰端口 / 主机栏,
    /// 只填一头也行;路径要写对(本机是完整路径、服务器是 / 开头),写错当场指出。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void UnixSocket_LocalForwardForm()
    {
        FillValidLocalForm();
        Assert.IsTrue(_vm.IsUnixSocketOptionVisible);
        Assert.IsFalse(_vm.IsUnixSocketFormVisible);
        _vm.NewTunnelType = TunnelType.DynamicForward;
        Assert.IsFalse(_vm.IsUnixSocketOptionVisible, "动态转发没有套接字的写法。");
        _vm.NewTunnelType = TunnelType.RemoteDynamicForward;
        Assert.IsFalse(_vm.IsUnixSocketOptionVisible);
        _vm.NewTunnelType = TunnelType.LocalForward;

        _vm.NewUseUnixSocket = true;
        Assert.IsTrue(_vm.IsUnixSocketFormVisible);
        Assert.AreEqual(Strings.Get("Tunnel_LocalSocketListenLabel"), _vm.LocalSocketLabel);
        Assert.AreEqual(Strings.Get("Tunnel_RemoteSocketTargetLabel"), _vm.RemoteSocketLabel);
        Assert.IsTrue(_vm.IsFormValid, "两栏都空着就是照旧用端口。");
        Assert.IsTrue(_vm.IsLocalPortEditable);

        _vm.NewRemoteSocketPath = "var/run/docker.sock";
        Assert.AreEqual(Strings.Get("Tunnel_RemoteSocketPathNotAbsolute"), _vm.RemoteSocketPathError);
        Assert.IsFalse(_vm.IsFormValid);
        _vm.NewRemoteSocketPath = "/var/run/docker.sock";
        Assert.IsNull(_vm.RemoteSocketPathError);
        Assert.IsFalse(_vm.IsRemoteEndpointEditable, "服务器那头改用套接字,目标主机与端口置灰。");
        Assert.IsFalse(_vm.IsRemoteHostEditable);
        Assert.IsTrue(_vm.IsLocalPortEditable, "本机这头没填,还是本地端口。");
        Assert.IsTrue(_vm.IsFormValid);

        _vm.NewLocalSocketPath = "docker.sock";
        Assert.AreEqual(Strings.Get("Tunnel_LocalSocketPathNotAbsolute"), _vm.LocalSocketPathError);
        Assert.IsFalse(_vm.IsFormValid);
        _vm.NewLocalSocketPath = Path.Combine(Path.GetTempPath(), new string('a', 200) + ".sock");
        Assert.StartsWith(Strings.Format("Tunnel_LocalSocketPathTooLong",
            System.Text.Encoding.UTF8.GetByteCount(_vm.NewLocalSocketPath), TunnelPanelViewModel.MaxLocalSocketPathBytes), _vm.LocalSocketPathError);
        _vm.NewLocalSocketPath = LocalSocket;
        Assert.IsNull(_vm.LocalSocketPathError);
        Assert.IsFalse(_vm.IsLocalPortEditable);
        _vm.NewLocalPort = 0;
        Assert.IsTrue(_vm.IsFormValid, "本机这头改用套接字,本地端口不再要求。");

        _vm.NewUseUnixSocket = false;
        Assert.IsTrue(_vm.IsLocalPortEditable, "取消勾选,端口栏回来。");
        Assert.IsFalse(_vm.IsFormValid, "又要本地端口了。");
    }

    /// <summary>
    /// 远程转发的套接字两头各自可以填(SSH 库四种组合都有,与本地转发一个口径):填了路径的那头置灰端口 / 主机栏,
    /// 只填一头也能建;两栏都空着就是照旧用端口。曾经勾了就两栏都要。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void UnixSocket_RemoteForwardEitherEnd()
    {
        FillValidLocalForm();
        _vm.NewTunnelType = TunnelType.RemoteForward;
        _vm.NewUseUnixSocket = true;

        Assert.AreEqual(Strings.Get("Tunnel_LocalSocketTargetLabel"), _vm.LocalSocketLabel);
        Assert.AreEqual(Strings.Get("Tunnel_RemoteSocketListenLabel"), _vm.RemoteSocketLabel);
        Assert.IsTrue(_vm.IsLocalPortEditable);
        Assert.IsTrue(_vm.IsRemoteEndpointEditable);
        Assert.IsTrue(_vm.IsFormValid, "两栏都空着就是照旧用端口。");

        // 服务器上的套接字 → 本机端口。
        _vm.NewRemoteSocketPath = "tmp/app.sock";
        Assert.AreEqual(Strings.Get("Tunnel_RemoteSocketPathNotAbsolute"), _vm.RemoteSocketPathError);
        Assert.IsFalse(_vm.IsFormValid);
        _vm.NewRemoteSocketPath = "/tmp/app.sock";
        Assert.IsFalse(_vm.IsRemoteEndpointEditable, "服务器那头改用套接字,监听地址与端口置灰。");
        Assert.IsTrue(_vm.IsLocalPortEditable, "本机这头没填,还是回连到本地端口。");
        Assert.IsTrue(_vm.IsFormValid);

        // 服务器端口 → 本机套接字。
        _vm.NewRemoteSocketPath = string.Empty;
        _vm.NewLocalSocketPath = LocalSocket;
        Assert.IsTrue(_vm.IsRemoteEndpointEditable);
        Assert.IsFalse(_vm.IsLocalPortEditable, "本机这头改用套接字,本地端口置灰。");
        _vm.NewLocalPort = 0;
        Assert.IsTrue(_vm.IsFormValid, "本机这头改用套接字,本地端口不再要求。");
        _vm.NewRemotePort = 0;
        Assert.IsFalse(_vm.IsFormValid, "服务器那头还是端口,端口照样要填。");
        _vm.NewRemotePort = 2375;

        // 两头都是。
        _vm.NewRemoteSocketPath = "/tmp/app.sock";
        Assert.IsTrue(_vm.IsFormValid);
    }

    /// <summary>套接字随表单往返:建出来的配置带着两个路径(没用上的那头为 null),编辑时填回、取消后清空。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task UnixSocket_RoundTripsThroughTheForm()
    {
        List<TunnelConfig> captured = [];
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(captured.Add), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Stopped,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        FillValidLocalForm();
        _vm.NewUseUnixSocket = true;
        _vm.NewRemoteSocketPath = " /var/run/docker.sock ";

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.IsNull(captured[0].LocalSocketPath, "本机那栏空着:照旧监听本地端口。");
        Assert.AreEqual("/var/run/docker.sock", captured[0].RemoteSocketPath);
        Assert.IsFalse(_vm.NewUseUnixSocket, "建完表单复位。");

        await _vm.EditTunnelCommand.Execute(_vm.Tunnels[0].Id).FirstAsync();
        Assert.IsTrue(_vm.NewUseUnixSocket);
        Assert.AreEqual("/var/run/docker.sock", _vm.NewRemoteSocketPath);
        Assert.AreEqual(string.Empty, _vm.NewLocalSocketPath);
        await _vm.ResetFormCommand.Execute().FirstAsync();
        Assert.IsFalse(_vm.NewUseUnixSocket);
        Assert.AreEqual(string.Empty, _vm.NewRemoteSocketPath);
    }

    /// <summary>
    /// 远程转发只有本机这头是套接字时照样建得出来:用上的那头带路径,服务器那头照旧是监听地址与端口;编辑时填回。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task UnixSocket_RemoteForwardWithOneSocketRoundTrips()
    {
        List<TunnelConfig> captured = [];
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(captured.Add), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Stopped,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        FillValidLocalForm();
        _vm.NewTunnelType = TunnelType.RemoteForward;
        _vm.ForwardToServerLoopback = true;
        _vm.NewRemotePort = 2375;
        _vm.NewUseUnixSocket = true;
        _vm.NewLocalSocketPath = $" {LocalSocket} ";

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.AreEqual(TunnelType.RemoteForward, captured[0].Type);
        Assert.AreEqual(LocalSocket, captured[0].LocalSocketPath);
        Assert.IsNull(captured[0].RemoteSocketPath, "服务器那栏空着:照旧在端口上监听。");
        Assert.AreEqual(("127.0.0.1", 2375u), (captured[0].RemoteHost, captured[0].RemotePort));

        await _vm.EditTunnelCommand.Execute(_vm.Tunnels[0].Id).FirstAsync();
        Assert.IsTrue(_vm.NewUseUnixSocket);
        Assert.AreEqual(LocalSocket, _vm.NewLocalSocketPath);
        Assert.AreEqual(string.Empty, _vm.NewRemoteSocketPath);
        Assert.IsTrue(_vm.IsRemoteEndpointEditable, "服务器那头还是端口,那两栏照常可改。");
        Assert.IsFalse(_vm.IsLocalPortEditable);
        await _vm.ResetFormCommand.Execute().FirstAsync();
    }

    /// <summary>列表里的套接字隧道:路由与端点写的是路径(不再拆成主机与端口)。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_UnixSocket_Display()
    {
        static TunnelInfo Info(TunnelType type, string? local, string? remote) => new()
        {
            Id = Guid.NewGuid(),
            Config = new()
            {
                Type = type,
                Name = "s",
                LocalHost = "127.0.0.1",
                LocalPort = 2375,
                RemoteHost = "127.0.0.1",
                RemotePort = 2375,
                LocalSocketPath = local,
                RemoteSocketPath = remote
            },
            Status = TunnelStatus.Stopped,
            SessionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };

        var toDocker = new TunnelItemViewModel(Info(TunnelType.LocalForward, null, "/var/run/docker.sock"));
        Assert.AreEqual(Strings.Format("Tunnel_RouteLocalSocket", "127.0.0.1:2375", "/var/run/docker.sock"), toDocker.DisplayRoute);
        Assert.AreEqual("127.0.0.1:2375 → /var/run/docker.sock", toDocker.EndpointSummary);

        var back = new TunnelItemViewModel(Info(TunnelType.RemoteForward, LocalSocket, "/tmp/app.sock"));
        Assert.AreEqual(Strings.Format("Tunnel_RouteRemoteSocket", "/tmp/app.sock", LocalSocket), back.DisplayRoute);
        Assert.AreEqual($"/tmp/app.sock → {LocalSocket}", back.EndpointSummary);

        // 远程转发只有一头是套接字:另一头照旧写 主机:端口(服务器的监听端、本机的目标)。
        var portToSocket = new TunnelItemViewModel(Info(TunnelType.RemoteForward, LocalSocket, null));
        Assert.AreEqual(Strings.Format("Tunnel_RouteRemoteSocket", "127.0.0.1:2375", LocalSocket), portToSocket.DisplayRoute);
        Assert.AreEqual($"127.0.0.1:2375 → {LocalSocket}", portToSocket.EndpointSummary);
        var socketToPort = new TunnelItemViewModel(Info(TunnelType.RemoteForward, null, "/tmp/pg.sock"));
        Assert.AreEqual(Strings.Format("Tunnel_RouteRemoteSocket", "/tmp/pg.sock", "127.0.0.1:2375"), socketToPort.DisplayRoute);
        Assert.AreEqual("/tmp/pg.sock → 127.0.0.1:2375", socketToPort.EndpointSummary);

        var tcp = new TunnelItemViewModel(Info(TunnelType.LocalForward, null, null));
        Assert.AreEqual(Strings.Format("Tunnel_RouteLocal", "127.0.0.1", 2375u, "127.0.0.1", 2375u), tcp.DisplayRoute, "没用套接字的照旧。");
    }

    /// <summary>面板开着时的每秒一拍:让服务同步读数,并刷各条隧道的流量行。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshTraffic_PullsStatisticsAndRefreshesTheRows()
    {
        TunnelInfo info = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(info));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        TunnelItemViewModel item = _vm.Tunnels.Single();
        List<string?> changed = [];
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _workflowService.ClearReceivedCalls();

        info.SentBytesPerSecond = 2048;
        _vm.RefreshTraffic();

        _workflowService.Received(1).RefreshStatistics();
        Assert.Contains(nameof(TunnelItemViewModel.TrafficText), changed);
        Assert.Contains(nameof(TunnelItemViewModel.ConnectionRows), changed);
        Assert.AreEqual("↑ 2.0 KB/s  ↓ 0 B/s", item.TrafficText);
    }

    /// <summary>时钟每拍都要让服务把底层读数同步到界面持有的 TunnelInfo 上。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void RefreshLiveState_PullsStatisticsFromService()
    {
        _vm.RefreshLiveState();

        _workflowService.Received().RefreshStatistics();
    }

    /// <summary>装一个独立的面板,便于用例自己控制会话存活与重拨结果。</summary>
    private TunnelPanelViewModel CreateVm(Func<bool> isAlive, Func<SessionProfile, Task<Guid>> connector) =>
        CreateVm(isAlive, (profile, _) => connector(profile));

    /// <param name="isAlive">后台会话是否还活着。</param>
    /// <param name="connector">后台连接;第二个参数 = 这是不是断线后的自动恢复。</param>
    /// <param name="errorReporter">面板外的错误提示出口。</param>
    private TunnelPanelViewModel CreateVm(
        Func<bool> isAlive, Func<SessionProfile, bool, Task<Guid>> connector, Action<string>? errorReporter = null)
    {
        var vm = new TunnelPanelViewModel(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (profile, automaticReconnect, _) => connector(profile, automaticReconnect),
            _ => isAlive(),
            _ => Task.CompletedTask,
            errorReporter: errorReporter is null ? null : (message, _) => errorReporter(message));
        vm.Servers.Add(_server);
        vm.SelectedServer = _server;
        return vm;
    }

    /// <summary>通过表单建出一条隧道,让面板处于"这台服务器上有一条隧道"的状态。</summary>
    private async Task SeedTunnelAsync(TunnelPanelViewModel vm, bool autoReconnect, TunnelStatus status = TunnelStatus.Active)
    {
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = status,
                            SessionId = callInfo.Arg<Guid>(),
                            CreatedAt = DateTime.UtcNow
                        }));
        vm.NewTunnelName = "seeded";
        vm.NewLocalHost = "127.0.0.1";
        vm.NewLocalPort = 3306;
        vm.NewRemotePort = 3306;
        vm.NewTunnelType = TunnelType.LocalForward;
        vm.NewAutoReconnect = autoReconnect;
        await vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.HasCount(1, vm.Tunnels);
    }

    /// <summary>等待某个条件成立(自动恢复是后台任务,不能建完就断言)。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.IsLessThanOrEqualTo(deadline, DateTime.UtcNow, "等待条件成立超时。");
            await Task.Delay(20);
        }
    }

    private static TunnelInfo CreateTunnelInfo(
        TunnelType type = TunnelType.LocalForward,
        TunnelStatus status = TunnelStatus.Active,
        string name = "test-tunnel",
        string localHost = "localhost",
        uint localPort = 3306,
        string remoteHost = "db-server",
        uint remotePort = 3306,
        long bytesTransferred = 0)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Config = new()
            {
                Type = type,
                Name = name,
                LocalHost = localHost,
                LocalPort = localPort,
                RemoteHost = remoteHost,
                RemotePort = remotePort
            },
            Status = status,
            SessionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = bytesTransferred
        };
    }

    /// <summary>填一份合法的本地转发表单(目标默认锁定服务器本机,这里显式解锁自填)。</summary>
    private void FillValidLocalForm(string remoteHost = "db-server", int remotePort = 3306)
    {
        _vm.NewTunnelName = "test-tunnel";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.ForwardToServerLoopback = false;
        _vm.NewRemoteHost = remoteHost;
        _vm.NewRemotePort = remotePort;
        _vm.NewTunnelType = TunnelType.LocalForward;
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_WithValidForm_AddsTunnelToList()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.HasCount(1, _vm.Tunnels);
        Assert.AreEqual("test-tunnel", _vm.Tunnels[0].Name);
        Assert.AreEqual(3306u, _vm.Tunnels[0].LocalPort);
        Assert.AreEqual("db-server", _vm.Tunnels[0].RemoteHost);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_WithoutSelectedServer_IsDisabled()
    {
        FillValidLocalForm();
        _vm.SelectedServer = null;
        Assert.IsFalse(await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_LoopbackDefault_TargetsServerItself()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(remoteHost: "127.0.0.1");
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 5432;
        _vm.NewRemotePort = 5432;
        // 默认 ForwardToServerLoopback = true:目标主机锁定 127.0.0.1(服务器视角)。
        Assert.IsTrue(_vm.ForwardToServerLoopback);
        Assert.IsFalse(_vm.IsRemoteHostEditable);
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.IsNotNull(captured);
        Assert.AreEqual("127.0.0.1", captured!.RemoteHost);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(0, 3306, false)]
    [DataRow(3306, 0, false)]
    [DataRow(-1, 3306, false)]
    [DataRow(3306, -1, false)]
    [DataRow(65536, 3306, false)]
    [DataRow(3306, 65536, false)]
    [DataRow(3306, 3306, true)]
    public async Task CreateTunnel_ValidatesPortRange(int localPort, int remotePort, bool expectedValid)
    {
        FillValidLocalForm(remotePort: remotePort);
        _vm.NewLocalPort = localPort;
        Assert.AreEqual(expectedValid, await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StopTunnel_ChangesTunnelStatusToStopped()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.AreEqual(TunnelStatus.Active, _vm.Tunnels[0].Status);
        await _vm.StopTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        Assert.AreEqual(TunnelStatus.Stopped, _vm.Tunnels[0].Status);
        await _workflowService.Received(1).StopTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_RemovesTunnelFromList()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.HasCount(1, _vm.Tunnels);
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        Assert.IsEmpty(_vm.Tunnels);
        // 删除统一走 RemoveTunnelAsync(活动中的由服务先停再移除)。
        await _workflowService.Received(1).RemoveTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task EditTunnel_ActiveTunnel_DoesNotEnterEditMode()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));

        await _vm.EditTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.IsFalse(_vm.IsEditing);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task EditTunnel_StoppedTunnel_EntersEditMode()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Stopped);
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));

        await _vm.EditTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.IsTrue(_vm.IsEditing);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_EditToolTip_TracksActiveStatus()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        List<string> changedProperties = [];
        itemVm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? string.Empty);

        Assert.AreEqual(Strings.Get("Tunnel_EditDisabledTip"), itemVm.EditToolTip);
        tunnelInfo.Status = TunnelStatus.Stopped;
        itemVm.RefreshLive();

        Assert.AreEqual(Strings.Get("Tunnel_EditTip"), itemVm.EditToolTip);
        Assert.Contains(nameof(TunnelItemViewModel.EditToolTip), changedProperties);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_WithoutConfirmationDelegate_IsNoOp()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));

        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.HasCount(1, _vm.Tunnels);
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_CancelledConfirmation_IsNoOp()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));
        _vm.ConfirmDelete = _ => Task.FromResult(false);

        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.HasCount(1, _vm.Tunnels);
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_ConfirmationReleasesBeforeMutation_ReResolvesItem()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));
        TaskCompletionSource<bool> confirmationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _vm.ConfirmDelete = async _ =>
        {
            confirmationStarted.SetResult(true);
            await releaseConfirmation.Task;
            return true;
        };

        Task deleteTask = _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        await confirmationStarted.Task;
        _vm.Tunnels.Clear();
        releaseConfirmation.SetResult(true);
        await deleteTask;

        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_ConcurrentRequests_RemoveOnlyOnce()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));
        TaskCompletionSource<bool> releaseConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int confirmationCount = 0;
        _vm.ConfirmDelete = async _ =>
        {
            Interlocked.Increment(ref confirmationCount);
            await releaseConfirmation.Task;
            return true;
        };
        _workflowService.RemoveTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>())
                        .Returns(Task.CompletedTask);

        Task first = _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        await Task.Delay(20);
        Task second = _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        releaseConfirmation.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, confirmationCount);
        await _workflowService.Received(1).RemoveTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
        Assert.IsEmpty(_vm.Tunnels);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow("", "localhost", 3306, "remote", 3306, true)] // 别名可选(设计 B3Rth)
    [DataRow("test", "", 3306, "remote", 3306, false)]
    [DataRow("test", "localhost", 0, "remote", 3306, false)]
    [DataRow("test", "localhost", 3306, "", 3306, false)]
    [DataRow("test", "localhost", 3306, "remote", 0, false)]
    [DataRow("test", "localhost", 3306, "remote", 3306, true)]
    public async Task PortValidation_RequiredFieldsMustBeNonEmptyNonZero(
        string name,
        string localHost,
        int localPort,
        string remoteHost,
        int remotePort,
        bool expectedValid)
    {
        _vm.ForwardToServerLoopback = false;
        _vm.NewTunnelName = name;
        _vm.NewLocalHost = localHost;
        _vm.NewLocalPort = localPort;
        _vm.NewRemoteHost = remoteHost;
        _vm.NewRemotePort = remotePort;
        Assert.AreEqual(expectedValid, await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DynamicForward_DoesNotRequireRemoteTarget()
    {
        _vm.NewTunnelName = string.Empty;
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 1080;
        _vm.NewRemoteHost = string.Empty;
        _vm.NewRemotePort = 0;
        _vm.NewTunnelTypeIndex = 2; // 动态 SOCKS
        Assert.IsTrue(await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_RemoteForward_RoutesViaWorkflowService()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.RemoteForward);
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm("web-server", 80);
        _vm.NewTunnelName = "remote-tunnel";
        _vm.NewLocalPort = 8080;
        _vm.NewTunnelType = TunnelType.RemoteForward;
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        await _workflowService.Received(1).CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        Assert.IsNotNull(captured);
        Assert.AreEqual(TunnelType.RemoteForward, captured!.Type);
        Assert.HasCount(1, _vm.Tunnels);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_DisplayRoute_LocalForward()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(localHost: "localhost",
            localPort: 3306,
            remoteHost: "db-server",
            remotePort: 3306);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        string expectedRoute = Strings.Format("Tunnel_RouteLocal", "localhost", 3306u, "db-server", 3306u);
        Assert.AreEqual(expectedRoute, itemVm.DisplayRoute);
        Assert.AreEqual(Strings.Get("Tunnel_BadgeLocal"), itemVm.TypeBadge);
        Assert.AreEqual("localhost:3306 → db-server:3306", itemVm.EndpointSummary);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_DisplayRoute_RemoteForward()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.RemoteForward,
            localHost: "127.0.0.1",
            localPort: 3000,
            remoteHost: "0.0.0.0",
            remotePort: 8080);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        string expectedRoute = Strings.Format("Tunnel_RouteRemote", "127.0.0.1", 3000u, 8080u);
        Assert.AreEqual(expectedRoute, itemVm.DisplayRoute);
        Assert.AreEqual(Strings.Get("Tunnel_BadgeRemote"), itemVm.TypeBadge);
        Assert.AreEqual("0.0.0.0:8080 → 127.0.0.1:3000", itemVm.EndpointSummary);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_DisplayRoute_DynamicForward()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.DynamicForward,
            localHost: "127.0.0.1",
            localPort: 1080);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        string expectedRoute = Strings.Format("Tunnel_RouteDynamic", "127.0.0.1", 1080u);
        Assert.AreEqual(expectedRoute, itemVm.DisplayRoute);
        Assert.AreEqual(Strings.Get("Tunnel_BadgeDynamic"), itemVm.TypeBadge);
        Assert.AreEqual("127.0.0.1:1080 (SOCKS5)", itemVm.EndpointSummary);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_BlankAlias_UsesLocalizedTypeName()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.LocalForward, name: "");
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual(Strings.Get("Tunnel_FallbackLocal"), itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_BlankAlias_Remote()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.RemoteForward, name: null!);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual(Strings.Get("Tunnel_FallbackRemote"), itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_BlankAlias_Dynamic()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.DynamicForward, name: "   ");
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual(Strings.Get("Tunnel_FallbackDynamic"), itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_NonBlankAlias_KeepsOriginal()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(name: "my-db-tunnel");
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual("my-db-tunnel", itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_ReflectsServiceSideStatusAndError()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        var itemVm = new TunnelItemViewModel(tunnelInfo);

        // 服务侧(会话断开/通道错误)直接改共享 TunnelInfo,条目应透传。
        tunnelInfo.Status = TunnelStatus.Stopped;
        tunnelInfo.LastError = "目标拒绝连接";
        itemVm.RefreshLive();
        Assert.AreEqual(TunnelStatus.Stopped, itemVm.Status);
        Assert.IsFalse(itemVm.IsActive);
        Assert.IsTrue(itemVm.HasError);
        Assert.AreEqual("目标拒绝连接", itemVm.LastError);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(0, "0 B")]
    [DataRow(1024, "1.0 KB")]
    [DataRow(1048576, "1.0 MB")]
    [DataRow(1073741824, "1.0 GB")]
    public void TunnelItemViewModel_BytesTransferred_FormatsCorrectly(long bytes, string expected) => Assert.AreEqual(expected, TunnelItemViewModel.FormatBytes(bytes));

    // ———— 程序启动时自动建立 ————

    /// <summary>
    /// 启动时只建勾了「程序启动时自动建立」的那几条;其余的一律是已停止、等用户手动启动 ——
    /// 启停状态本身从不落盘。之后打开面板,看到的就是这里建好的条目,不会再恢复出一份重复的。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartTunnelsAsync_StartsOnlyTheTunnelsMarkedForStartup()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelConfig onStartup = SavedConfig("on-startup", 5432, autoStart: true);
        TunnelConfig manual = SavedConfig("manual", 6379, autoStart: false);
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([onStartup, manual]));
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Active,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        List<string> errors = [];
        TunnelPanelViewModel vm = CreateVmWithStore(store, errors.Add);

        await vm.AutoStartTunnelsAsync();

        await _workflowService.Received(1).CreateTunnelAsync(_sessionId, onStartup, Arg.Any<CancellationToken>());
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), manual, Arg.Any<CancellationToken>());
        Assert.IsEmpty(errors);

        await vm.OpenAsync(_server.Id);
        Assert.HasCount(2, vm.Tunnels, "打开面板看到的应当是启动时建好的那份,不能再恢复出一份重复的。");
        Assert.IsTrue(vm.Tunnels.Single(t => t.Name == "on-startup").IsActive);
        Assert.IsFalse(vm.Tunnels.Single(t => t.Name == "manual").IsActive);
        Assert.IsTrue(vm.Tunnels.All(t => !t.IsBusy));
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartTunnelsAsync_ProfilePausedDuringEarlierStartup_RequiresManualRecovery() =>
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TunnelPanelViewModelTests).Assembly).RunOnUiAsync(async () =>
        {
            SessionProfile second = new() { Name = "second", Host = "10.0.0.2", Username = "root", AuthMethod = AuthMethod.Tailscale };
            IAppDataStore store = Substitute.For<IAppDataStore>();
            TunnelConfig firstConfig = SavedConfig("first-startup", 5432, autoStart: true);
            TunnelConfig secondConfig = SavedConfig("second-startup", 6379, autoStart: true, autoReconnect: true);
            store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<List<TunnelConfig>?>([firstConfig]));
            store.GetAsync<List<TunnelConfig>>("tunnels", second.Id.ToString("D"), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<List<TunnelConfig>?>([secondConfig]));
            TaskCompletionSource<Guid> firstAuthentication = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<bool> secondAttempts = [];
            bool secondAlive = false;
            _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                            .Returns(call => Task.FromResult(new TunnelInfo
                            {
                                Id = Guid.NewGuid(),
                                Config = call.Arg<TunnelConfig>(),
                                Status = TunnelStatus.Active,
                                SessionId = call.Arg<Guid>(),
                                CreatedAt = DateTime.UtcNow
                            }));
            using TunnelPanelViewModel vm = new(_workflowService,
                () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server, second]),
                (profile, automatic, _) =>
                {
                    if (profile.Id == _server.Id)
                    {
                        Assert.IsFalse(automatic, "首次启动仍可发起认证。");
                        firstStarted.TrySetResult();
                        return firstAuthentication.Task;
                    }
                    secondAttempts.Add(automatic);
                    if (secondAttempts.Count == 1)
                    {
                        return Task.FromException<Guid>(TailscaleFailure("SshErr_TailscaleCheckRequired"));
                    }
                    secondAlive = true;
                    return Task.FromResult(second.Id);
                }, id => id != second.Id || secondAlive, dataStore: store);
            Task startup = vm.AutoStartTunnelsAsync();
            try
            {
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(startup.IsCompleted);
                await vm.OpenAsync(second.Id);
                TunnelItemViewModel paused = vm.Tunnels.Single();
                vm.RefreshLiveState();
                Assert.AreSequenceEqual([true], secondAttempts);
                Assert.IsFalse(paused.IsBusy, "同步认证拒绝应已完成暂停和收尾。");
                Assert.AreEqual(Strings.Get("SshErr_TailscaleCheckRequired"), vm.ErrorMessage);

                firstAuthentication.TrySetResult(_sessionId);
                await startup.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreSequenceEqual([true], secondAttempts, "启动遍历轮到已暂停服务器时不得再认证。");
                vm.RefreshLiveState();
                Assert.AreSequenceEqual([true], secondAttempts, "后续普通刷新仍须尊重暂停。");
                Assert.AreSame(paused, vm.Tunnels.Single());
                await _workflowService.DidNotReceive().CreateTunnelAsync(second.Id, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());

                await vm.StartTunnelCommand.Execute(paused.Id).FirstAsync();
                Assert.AreSequenceEqual([true, false], secondAttempts, "显式手动启动必须允许重新认证。");
                Assert.IsTrue(vm.Tunnels.Single().IsActive);
                Guid manualTunnelId = vm.Tunnels.Single().Id;
                secondAlive = false;
                vm.RefreshLiveState();
                Assert.AreSequenceEqual([true, false, true], secondAttempts, "手动成功后应恢复普通断线重连。");
                Assert.IsTrue(vm.Tunnels.Single().IsActive);
                Assert.AreNotEqual(manualTunnelId, vm.Tunnels.Single().Id);
            }
            finally
            {
                firstAuthentication.TrySetResult(_sessionId);
                await startup.WaitAsync(TimeSpan.FromSeconds(10));
            }
        });

    /// <summary>一条都没勾「程序启动时自动建立」时,启动时连服务器都不拨。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartTunnelsAsync_NothingMarked_DoesNotConnect()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([SavedConfig("manual", 6379, autoStart: false)]));
        int dials = 0;
        TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) =>
            {
                dials++;
                return Task.FromResult(_sessionId);
            },
            _ => true,
            _ => Task.CompletedTask,
            store);

        await vm.AutoStartTunnelsAsync();

        Assert.AreEqual(0, dials);
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 连不上服务器:报到右下角(面板这时多半没开),报文里点名是哪台;条目留着、不转圈,等用户手动启动。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartTunnelsAsync_ConnectFailure_IsReportedAndLeavesTunnelStopped()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([SavedConfig("on-startup", 5432, autoStart: true)]));
        List<string> errors = [];
        TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => Task.FromException<Guid>(new InvalidOperationException("host down")),
            _ => true,
            _ => Task.CompletedTask,
            store,
            (message, _) => errors.Add(message));

        await vm.AutoStartTunnelsAsync();

        Assert.HasCount(1, errors);
        Assert.AreEqual(Strings.Format("Msg_TunnelAutoStartConnectFailed", _server.Name, "host down"), errors[0]);
        await vm.OpenAsync(_server.Id);
        Assert.IsFalse(vm.Tunnels[0].IsActive);
        Assert.IsFalse(vm.Tunnels[0].IsBusy);
        Assert.IsTrue(vm.Tunnels[0].ShowStartButton);
    }

    /// <summary>
    /// 连上了但这条建不起来(端口被占):报到右下角,点名是哪条隧道、哪个端口;
    /// 一条都没建成时专为它拨的后台连接随即断开。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartTunnelsAsync_TunnelFailure_IsReportedWithItsOwnPort()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([SavedConfig("pg", 5432, autoStart: true)]));
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromException<TunnelInfo>(new InvalidOperationException("bind",
                            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse))));
        List<string> errors = [];
        List<Guid> disconnected = [];
        TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => Task.FromResult(_sessionId),
            _ => true,
            id =>
            {
                disconnected.Add(id);
                return Task.CompletedTask;
            },
            store,
            (message, _) => errors.Add(message));

        await vm.AutoStartTunnelsAsync();

        Assert.HasCount(1, errors);
        // 端口取的是这条隧道自己的 5432,不是表单里默认的 27017。
        Assert.AreEqual(Strings.Format("Msg_TunnelAutoStartFailed", "pg", _server.Name,
            Strings.Format("Msg_LocalPortInUse", 5432u)), errors[0]);
        Assert.AreSequenceEqual([_sessionId], disconnected);
    }

    /// <summary>
    /// 连不上服务器:右下角那条提示带「重试」,点了就重连、把这台上勾了自启的隧道建起来,并报一声建好了 ——
    /// 原先用户得打开面板、切到那台服务器、再一条条点启动。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartTunnelsAsync_ConnectFailure_RetryReconnectsAndStartsTheTunnels()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelConfig onStartup = SavedConfig("on-startup", 5432, autoStart: true);
        TunnelConfig manual = SavedConfig("manual", 6379, autoStart: false);
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([onStartup, manual]));
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Active,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        int attempts = 0;
        List<(string Message, Action? Retry)> errors = [];
        List<string> infos = [];
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, automatic, _) =>
            {
                Assert.IsFalse(automatic, "重试是人点的,主机指纹照常可以问他。");
                return ++attempts == 1
                    ? Task.FromException<Guid>(new InvalidOperationException("host down"))
                    : Task.FromResult(_sessionId);
            },
            _ => true,
            _ => Task.CompletedTask,
            store,
            (message, retry) => errors.Add((message, retry)),
            infos.Add);

        await vm.AutoStartTunnelsAsync();

        (string message, Action? retry) = errors.Single();
        Assert.AreEqual(Strings.Format("Msg_TunnelAutoStartConnectFailed", _server.Name, "host down"), message);
        Assert.IsNotNull(retry, "连不上服务器的提示要带「重试」。");
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());

        retry();
        await WaitForAsync(() => infos.Count == 1);

        Assert.AreEqual(2, attempts);
        await _workflowService.Received(1).CreateTunnelAsync(_sessionId, onStartup, Arg.Any<CancellationToken>());
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), manual, Arg.Any<CancellationToken>());
        Assert.AreEqual(Strings.Format("Msg_TunnelAutoStartRetried", "on-startup", _server.Name), infos[0]);
        Assert.HasCount(1, errors, "重试成功不再报错。");
        await vm.OpenAsync(_server.Id);
        Assert.IsTrue(vm.Tunnels.Single(t => t.Name == "on-startup").IsActive);
        Assert.IsFalse(vm.Tunnels.Single(t => t.Name == "manual").IsActive, "没勾自启的那条,重试也不替用户建。");
    }

    /// <summary>
    /// 同一台上一条建不起来(端口被占):它那条提示的「重试」只重建它自己,已经起来的那条不动;
    /// 再失败照样报、照样带「重试」。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartTunnelsAsync_TunnelFailure_RetryRebuildsOnlyThatTunnel()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelConfig good = SavedConfig("good", 5432, autoStart: true);
        TunnelConfig busy = SavedConfig("busy", 6379, autoStart: true);
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([good, busy]));
        int busyAttempts = 0;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            TunnelConfig config = callInfo.Arg<TunnelConfig>();
                            if (ReferenceEquals(config, busy) && ++busyAttempts < 3)
                            {
                                return Task.FromException<TunnelInfo>(new InvalidOperationException("bind",
                                    new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse)));
                            }
                            return Task.FromResult(new TunnelInfo
                            {
                                Id = Guid.NewGuid(),
                                Config = config,
                                Status = TunnelStatus.Active,
                                SessionId = _sessionId,
                                CreatedAt = DateTime.UtcNow
                            });
                        });
        List<(string Message, Action? Retry)> errors = [];
        List<string> infos = [];
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask,
            store,
            (message, retry) => errors.Add((message, retry)),
            infos.Add);

        await vm.AutoStartTunnelsAsync();
        string expected = Strings.Format("Msg_TunnelAutoStartFailed", "busy", _server.Name, Strings.Format("Msg_LocalPortInUse", 6379u));
        Assert.AreEqual(expected, errors.Single().Message);

        errors.Single().Retry!();
        await WaitForAsync(() => errors.Count == 2);
        Assert.AreEqual(expected, errors[1].Message, "再失败照样报出来。");
        Assert.IsNotNull(errors[1].Retry, "再失败的提示同样能重试。");

        errors[1].Retry!();
        await WaitForAsync(() => infos.Count == 1);

        Assert.AreEqual(3, busyAttempts);
        await _workflowService.Received(1).CreateTunnelAsync(_sessionId, good, Arg.Any<CancellationToken>());
        Assert.AreEqual(Strings.Format("Msg_TunnelAutoStartRetried", "busy", _server.Name), infos[0]);
        await vm.OpenAsync(_server.Id);
        Assert.IsTrue(vm.Tunnels.All(t => t.IsActive));
        Assert.HasCount(2, vm.Tunnels);
    }

    /// <summary>
    /// 点「重试」之前用户已经在面板里把那条启动了(换成了新条目):重试什么也不做,不会再建出第二条。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartRetry_AfterTheUserStartedTheTunnel_DoesNothing()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelConfig config = SavedConfig("pg", 5432, autoStart: true);
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([config]));
        bool fail = true;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => fail
                            ? Task.FromException<TunnelInfo>(new InvalidOperationException("boom"))
                            : Task.FromResult(new TunnelInfo
                            {
                                Id = Guid.NewGuid(),
                                Config = callInfo.Arg<TunnelConfig>(),
                                Status = TunnelStatus.Active,
                                SessionId = _sessionId,
                                CreatedAt = DateTime.UtcNow
                            }));
        List<(string Message, Action? Retry)> errors = [];
        List<string> infos = [];
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask,
            store,
            (message, retry) => errors.Add((message, retry)),
            infos.Add);
        await vm.AutoStartTunnelsAsync();
        Action retry = errors.Single().Retry!;

        fail = false;
        await vm.OpenAsync(_server.Id);
        await vm.StartTunnelCommand.Execute(vm.Tunnels.Single().Id).FirstAsync();
        Assert.IsTrue(vm.Tunnels.Single().IsActive);
        _workflowService.ClearReceivedCalls();

        retry();

        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        Assert.IsEmpty(infos);
        Assert.HasCount(1, vm.Tunnels);
    }

    /// <summary>
    /// 启动时那一轮因为认证要人处理而停下的服务器,自动的不再试 —— 但提示上的「重试」是人点的,要照样去认证。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartRetry_IsNotBlockedByAnEarlierAuthenticationPause()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([SavedConfig("pg", 5432, autoStart: true, autoReconnect: true)]));
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Active,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        int attempts = 0;
        List<(string Message, Action? Retry)> errors = [];
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => ++attempts == 1
                ? Task.FromException<Guid>(TailscaleFailure("SshErr_TailscaleCheckRequired"))
                : Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask,
            store,
            (message, retry) => errors.Add((message, retry)));

        await vm.AutoStartTunnelsAsync();
        vm.RefreshLiveState();
        Assert.AreEqual(1, attempts, "认证要人处理:自动的不再试。");

        errors.Single().Retry!();
        await WaitForAsync(() => attempts == 2);

        await _workflowService.Received(1).CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>自动重连败在主机指纹上:那条提示不带「重试」—— 要的是用户去面板里手动启动、在弹窗里核对指纹。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_HostKeyRejected_ReportsWithoutRetry()
    {
        bool alive = true;
        bool reject = false;
        List<(string Message, Action? Retry)> errors = [];
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => reject
                ? Task.FromException<Guid>(new VelaSshHostKeyRejectedException("fingerprint"))
                : Task.FromResult(_sessionId),
            _ => alive,
            _ => Task.CompletedTask,
            errorReporter: (message, retry) => errors.Add((message, retry)));
        vm.Servers.Add(_server);
        vm.SelectedServer = _server;
        await SeedTunnelAsync(vm, autoReconnect: true);

        alive = false;
        reject = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => errors.Count == 1);

        Assert.IsNull(errors[0].Retry);
    }

    /// <summary>「程序启动时自动建立」随隧道配置往返:新建时写进配置、编辑时填回表单、取消后回到默认的不勾。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task AutoStartOption_RoundTripsThroughTheForm()
    {
        Assert.IsFalse(_vm.NewAutoStart, "默认不勾。");
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = TunnelStatus.Stopped,
                            SessionId = _sessionId,
                            CreatedAt = DateTime.UtcNow
                        }));
        FillValidLocalForm();
        _vm.NewAutoStart = true;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.IsTrue(captured?.AutoStart);
        Assert.IsFalse(_vm.NewAutoStart, "建完表单复位,回到不勾。");

        await _vm.EditTunnelCommand.Execute(_vm.Tunnels[0].Id).FirstAsync();
        Assert.IsTrue(_vm.NewAutoStart, "编辑时把它填回表单。");
        await _vm.ResetFormCommand.Execute().FirstAsync();
        Assert.IsFalse(_vm.NewAutoStart);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task TailscaleCancellation_StartOrActiveEdit_PreservesDefinitionAndServiceRecord(bool edit, bool callerCancellation)
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        List<TunnelConfig> stored = [];
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(_ => Task.FromResult<List<TunnelConfig>?>(stored));
        store.UpsertAsync("tunnels", _server.Id.ToString("D"), Arg.Any<List<TunnelConfig>>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 stored = [.. call.Arg<List<TunnelConfig>>()];
                 return Task.CompletedTask;
             });
        bool alive = true;
        int attempts = 0;
        TaskCompletionSource<Guid> authenticating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, automatic, ct) =>
            {
                Assert.IsFalse(automatic);
                attempts++;
                return attempts == 1 ? Task.FromResult(_sessionId) : authenticating.Task.WaitAsync(ct);
            }, _ => alive, dataStore: store);
        vm.Servers.Add(_server);
        vm.SelectedServer = _server;
        TunnelInfo serviceRecord = CreateTunnelInfo(status: TunnelStatus.Stopped);
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(call =>
                        {
                            serviceRecord = new TunnelInfo
                            {
                                Id = Guid.NewGuid(),
                                Config = call.Arg<TunnelConfig>(),
                                Status = TunnelStatus.Stopped,
                                SessionId = call.Arg<Guid>(),
                                CreatedAt = DateTime.UtcNow
                            };
                            return Task.FromResult(serviceRecord);
                        });
        vm.NewTunnelName = "original";
        vm.NewLocalHost = "127.0.0.1";
        vm.NewLocalPort = 3306;
        vm.NewRemotePort = 3306;
        vm.NewAutoReconnect = true;
        await vm.CreateTunnelCommand.Execute().FirstAsync();
        TunnelItemViewModel original = vm.Tunnels.Single();
        TunnelConfig originalConfig = stored.Single();
        Assert.AreSame(serviceRecord.Config, originalConfig);
        if (edit)
        {
            await vm.EditTunnelCommand.Execute(original.Id).FirstAsync();
            vm.NewTunnelName = "edited";
            // 编辑表单打开后状态发生变化,实际保存必须走活隧道的认证/重建分支。
            original.Status = TunnelStatus.Active;
        }
        TunnelStatus originalStatus = original.Status;
        alive = false;
        _workflowService.ClearReceivedCalls();
        store.ClearReceivedCalls();

        IObservable<RxVoid> command = edit ? vm.CreateTunnelCommand.Execute() : vm.StartTunnelCommand.Execute(original.Id);
        if (callerCancellation)
        {
            using IDisposable subscription = command.Subscribe();
            await WaitForAsync(() => attempts == 2 && original.IsBusy);
            subscription.Dispose();
            await WaitForAsync(() => !original.IsBusy);
        }
        else
        {
            Task operation = command.FirstAsync();
            await WaitForAsync(() => attempts == 2 && original.IsBusy);
            authenticating.SetException(TailscaleFailure("SshErr_TailscaleCheckCancelled"));
            await operation;
        }

        Assert.IsNull(vm.ErrorMessage);
        Assert.AreSame(original, vm.Tunnels.Single());
        Assert.AreSame(originalConfig, original.Config);
        Assert.AreSame(originalConfig, stored.Single());
        Assert.AreSame(originalConfig, serviceRecord.Config);
        Assert.AreEqual(originalStatus, serviceRecord.Status);
        Assert.AreEqual("original", original.Config.Name);
        Assert.IsFalse(original.IsBusy);
        Assert.IsFalse(vm.IsServerConnected);
        Assert.AreEqual(edit, vm.IsEditing);
        if (edit)
        {
            Assert.AreEqual("edited", vm.NewTunnelName, "取消不能抹掉当前编辑表单。");
        }
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().UpsertAsync("tunnels", Arg.Any<string>(), Arg.Any<List<TunnelConfig>>(), Arg.Any<CancellationToken>());
        // 保留性断言完成后模拟服务侧报告掉线,让活编辑用例也真正成为自动恢复候选。
        serviceRecord.Status = TunnelStatus.Stopped;
        vm.RefreshLiveState();
        vm.RefreshLiveState();
        Assert.AreEqual(2, attempts, "取消后刷新不能重新打开验证。");
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow("SshErr_TailscaleCheckCancelled")]
    [DataRow("SshErr_TailscaleRejected")]
    [DataRow("SshErr_TailscaleCheckTimeout")]
    [DataRow("SshErr_TailscaleAuthTimeout")]
    [DataRow("SshErr_TailscaleCheckRequired")]
    [DataRow("SshErr_TailscaleCheckUiUnavailable")]
    [DataRow("SshErr_TailscaleCheckFailed")]
    public async Task AutoStartTunnelsAsync_TailscaleFailure_PausesRefreshAndRetainsStartupDefinition(string errorKey)
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelConfig config = SavedConfig("startup", 5432, autoStart: true, autoReconnect: true);
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([config]));
        int attempts = 0;
        List<string> errors = [];
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, automatic, _) =>
            {
                Assert.IsFalse(automatic, "启动允许有人在场进行首次验证。");
                attempts++;
                return Task.FromException<Guid>(TailscaleFailure(errorKey));
            }, _ => false, dataStore: store, errorReporter: (message, _) => errors.Add(message));

        await vm.AutoStartTunnelsAsync();
        await vm.OpenAsync(_server.Id);
        TunnelItemViewModel original = vm.Tunnels.Single();
        vm.RefreshLiveState();
        vm.RefreshLiveState();

        Assert.AreEqual(1, attempts, "startup失败必须经Ensure登记暂停,不能在刷新时再次认证。");
        Assert.AreSame(config, original.Config);
        Assert.IsTrue(original.AutoReconnect);
        Assert.IsTrue(original.AutoStart);
        Assert.IsFalse(original.IsActive);
        Assert.IsFalse(original.IsBusy);
        Assert.IsTrue(original.ShowStartButton);
        Assert.IsFalse(vm.IsServerConnected);
        Assert.IsNull(vm.ErrorMessage);
        if (errorKey == "SshErr_TailscaleCheckCancelled")
        {
            Assert.IsEmpty(errors);
        }
        else
        {
            Assert.AreSequenceEqual([Strings.Format("Msg_TunnelAutoStartConnectFailed", _server.Name, Strings.Get(errorKey))], errors);
        }
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().UpsertAsync("tunnels", Arg.Any<string>(), Arg.Any<List<TunnelConfig>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow("SshErr_TailscaleCheckCancelled")]
    [DataRow("SshErr_TailscaleRejected")]
    [DataRow("caller")]
    public async Task CreateTunnel_TailscaleFailure_RetainsFormAndPausesExistingSavedTunnels(string failure)
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelConfig config = SavedConfig("existing", 5432, autoStart: false, autoReconnect: true);
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>([config]));
        int attempts = 0;
        TaskCompletionSource<Guid> authenticating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using TunnelPanelViewModel vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, automatic, ct) =>
            {
                Assert.IsFalse(automatic);
                attempts++;
                return authenticating.Task.WaitAsync(ct);
            }, _ => false, dataStore: store);
        await vm.OpenAsync(_server.Id);
        TunnelItemViewModel original = vm.Tunnels.Single();
        vm.NewTunnelName = "new-tunnel";
        vm.NewLocalHost = "127.0.0.1";
        vm.NewLocalPort = 6379;
        vm.NewRemotePort = 6379;

        if (failure == "caller")
        {
            using IDisposable subscription = vm.CreateTunnelCommand.Execute().Subscribe();
            await WaitForAsync(() => attempts == 1);
            Assert.AreEqual(Strings.Get("Msg_ConnectingInBackground"), vm.ServerStatusText);
            subscription.Dispose();
            await WaitForAsync(() => vm.ServerStatusText != Strings.Get("Msg_ConnectingInBackground"));
        }
        else
        {
            Task operation = vm.CreateTunnelCommand.Execute().FirstAsync();
            await WaitForAsync(() => attempts == 1);
            Assert.AreEqual(Strings.Get("Msg_ConnectingInBackground"), vm.ServerStatusText);
            authenticating.SetException(TailscaleFailure(failure));
            await operation;
        }

        Assert.AreSame(original, vm.Tunnels.Single());
        Assert.AreSame(config, original.Config);
        Assert.IsFalse(original.IsBusy);
        Assert.IsFalse(vm.IsServerConnected);
        Assert.AreEqual("new-tunnel", vm.NewTunnelName);
        Assert.AreEqual(6379, vm.NewLocalPort);
        if (failure == "SshErr_TailscaleRejected")
        {
            Assert.AreEqual(Strings.Get(failure), vm.ErrorMessage);
        }
        else
        {
            Assert.IsNull(vm.ErrorMessage);
        }
        vm.RefreshLiveState();
        vm.RefreshLiveState();
        Assert.AreEqual(1, attempts);
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().UpsertAsync("tunnels", Arg.Any<string>(), Arg.Any<List<TunnelConfig>>(), Arg.Any<CancellationToken>());
    }

    private static TunnelConfig SavedConfig(string name, uint port, bool autoStart, bool autoReconnect = false) => new()
    {
        Type = TunnelType.LocalForward,
        Name = name,
        LocalHost = "127.0.0.1",
        LocalPort = port,
        RemoteHost = "127.0.0.1",
        RemotePort = port,
        AutoStart = autoStart,
        AutoReconnect = autoReconnect
    };

    // ———— 面板拖拽位置 ————

    /// <summary>
    /// 拖动后的位置落在 <c>ui-layout/tunnel-panel</c> —— 与文件传输提示、消息中心同集合、各占一个文档 Id。
    /// 写进别人的 Id 会互相踩,这条钉住 Id 不被写错。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void PersistPanelPosition_WritesCurrentOffsetToStore()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelPanelViewModel vm = CreateVmWithStore(store);
        vm.PanelOffsetX = -420;
        vm.PanelOffsetY = 96;

        vm.PersistPanelPosition();

        _ = store.Received(1).UpsertAsync(
            "ui-layout",
            "tunnel-panel",
            Arg.Is<PanelPosition>(p => p.OffsetX == -420 && p.OffsetY == 96),
            Arg.Any<CancellationToken>());
    }

    /// <summary>构造时从存储恢复上次的位置 —— 这就是「再次打开回到原来的地方」。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task Construction_RestoresPersistedPanelPosition()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<PanelPosition>("ui-layout", "tunnel-panel", Arg.Any<CancellationToken>())
             .Returns(new PanelPosition { OffsetX = -300, OffsetY = 140 });

        TunnelPanelViewModel vm = CreateVmWithStore(store);

        await WaitForAsync(() => vm.PanelOffsetX != 0);
        Assert.AreEqual(-300, vm.PanelOffsetX);
        Assert.AreEqual(140, vm.PanelOffsetY);
    }

    /// <summary>没有存储(单元测试 / 精简宿主)时不该炸,位置退回默认锚点。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void WithoutStore_PanelPositionDefaultsToAnchorAndPersistIsHarmless()
    {
        Assert.AreEqual(0, _vm.PanelOffsetX);
        Assert.AreEqual(0, _vm.PanelOffsetY);
        _vm.PersistPanelPosition();
    }

    /// <summary>带持久化存储的面板(隧道配置持久化,重启后手动启动)。</summary>
    private TunnelPanelViewModel CreateVmWithStore(IAppDataStore store, Action<string>? errorReporter = null)
    {
        var vm = new TunnelPanelViewModel(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask,
            store,
            errorReporter is null ? null : (message, _) => errorReporter(message));
        vm.Servers.Add(_server);
        return vm;
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_PersistsConfigsToStore()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelPanelViewModel vm = CreateVmWithStore(store);
        vm.SelectedServer = _server;
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        vm.NewTunnelName = "test-tunnel";
        vm.NewLocalHost = "localhost";
        vm.NewLocalPort = 3306;
        vm.ForwardToServerLoopback = false;
        vm.NewRemoteHost = "db-server";
        vm.NewRemotePort = 3306;
        await vm.CreateTunnelCommand.Execute().FirstAsync();
        await store.Received(1).UpsertAsync(
            "tunnels",
            _server.Id.ToString("D"),
            Arg.Is<List<TunnelConfig>>(list => list.Count == 1 && list[0].Name == "test-tunnel"),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_PersistsEmptyList()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelPanelViewModel vm = CreateVmWithStore(store);
        vm.SelectedServer = _server;
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        vm.NewTunnelName = "t";
        vm.NewLocalHost = "localhost";
        vm.NewLocalPort = 3306;
        vm.ForwardToServerLoopback = false;
        vm.NewRemoteHost = "db-server";
        vm.NewRemotePort = 3306;
        await vm.CreateTunnelCommand.Execute().FirstAsync();
        vm.ConfirmDelete = _ => Task.FromResult(true);
        await vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        await store.Received(1).UpsertAsync(
            "tunnels",
            _server.Id.ToString("D"),
            Arg.Is<List<TunnelConfig>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task SelectServer_RestoresPersistedTunnels_StoppedAndNotAutoStarted()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        List<TunnelConfig> saved =
        [
            new()
            {
                Type = TunnelType.LocalForward,
                Name = "restored",
                LocalHost = "127.0.0.1",
                LocalPort = 8080,
                RemoteHost = "127.0.0.1",
                RemotePort = 80
            }
        ];
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>(saved));
        TunnelPanelViewModel vm = CreateVmWithStore(store);
        vm.SelectedServer = _server;

        // 恢复为"已停止",等待用户手动启动;绝不自动建立转发。
        Assert.HasCount(1, vm.Tunnels);
        Assert.AreEqual("restored", vm.Tunnels[0].Name);
        Assert.AreEqual(TunnelStatus.Stopped, vm.Tunnels[0].Status);
        Assert.IsFalse(vm.Tunnels[0].IsActive);
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_ResetsFormAfterSuccess()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm("server", 80);
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 8080;
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.AreEqual(string.Empty, _vm.NewTunnelName);
        Assert.AreEqual("127.0.0.1", _vm.NewLocalHost);
        Assert.AreEqual(27017, _vm.NewLocalPort);
        Assert.AreEqual(27017, _vm.NewRemotePort);
        Assert.AreEqual(TunnelType.LocalForward, _vm.NewTunnelType);
        // 复位后目标重新锁定服务器本机。
        Assert.IsTrue(_vm.ForwardToServerLoopback);
        Assert.AreEqual("127.0.0.1", _vm.NewRemoteHost);
    }

    /// <summary>
    /// 端口转发跑在 SSH 通道上,SFTP / FTP / 插件协议的会话给不了这条通道。
    /// 它们出现在服务器下拉框里,用户选中后只会撞上一次注定失败的后台连接。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task OpenAsync_ListsOnlySshProfiles()
    {
        SessionProfile ssh = new() { Name = "ssh", Host = "10.0.0.1", Username = "root" };
        SessionProfile sftp = new() { Name = "sftp", Host = "10.0.0.2", ConnectionType = ConnectionType.SFTP };
        SessionProfile ftp = new() { Name = "ftp", Host = "10.0.0.3", ConnectionType = ConnectionType.FTP };
        SessionProfile plugin = new() { Name = "s3", Host = "10.0.0.4", ConnectionType = ConnectionType.Plugin };
        TunnelPanelViewModel vm = new(
            _workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([sftp, ssh, ftp, plugin]),
            (_, _, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask);

        await vm.OpenAsync();

        Assert.HasCount(1, vm.Servers);
        Assert.AreSame(ssh, vm.Servers[0]);
        // 预选也只能落在 SSH 上:非 SSH 的 id 传进来时不该把它塞进列表当选中项。
        Assert.AreSame(ssh, vm.SelectedServer);
    }

    /// <summary>非 SSH 的 id 传进 <see cref="TunnelPanelViewModel.OpenAsync" /> 时不预选它。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task OpenAsync_NonSshPreselect_FallsBackToFirstSshServer()
    {
        SessionProfile ssh = new() { Name = "ssh", Host = "10.0.0.1", Username = "root" };
        SessionProfile ftp = new() { Name = "ftp", Host = "10.0.0.3", ConnectionType = ConnectionType.FTP };
        TunnelPanelViewModel vm = new(
            _workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([ssh, ftp]),
            (_, _, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask);

        await vm.OpenAsync(ftp.Id);

        Assert.AreSame(ssh, vm.SelectedServer);
    }
}
