using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests.ViewModels;

[TestClass]
public sealed class StatusBarViewModelTests
{
    [TestMethod]
    public void Constructor_SetsDefaultValues()
    {
        var vm = new StatusBarViewModel();

        Assert.AreEqual(Strings.Ready, vm.StatusText);
        Assert.AreEqual(string.Empty, vm.ConnectionInfo);
        Assert.AreEqual(Strings.Disconnected, vm.Status);
        Assert.AreEqual(string.Empty, vm.Latency);
        Assert.AreEqual("xterm-256color", vm.TerminalType);
        Assert.AreEqual("80×24", vm.WindowSize);
        Assert.AreEqual("UTF-8", vm.Encoding);
        Assert.AreEqual(string.Empty, vm.Uptime);
        Assert.IsFalse(vm.IsConnected);
    }

    [TestMethod]
    public void SetStatus_Connected_UpdatesIsConnected()
    {
        var vm = new StatusBarViewModel
        {
            Status = Strings.Connected
        };

        Assert.IsTrue(vm.IsConnected);
    }

    // 走虚拟时钟而不是真睡:原先 Delay(1500) 等一个 1 秒的线程池计时器,CI 机器忙时第一跳赶不上就红。
    [TestMethod]
    public void StartUptimeTimer_UpdatesUptimeProperty()
    {
        var clock = new VirtualClock();
        var vm = new StatusBarViewModel(clock);

        vm.StartUptimeTimer();
        Assert.AreEqual(string.Empty, vm.Uptime);

        clock.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.AreEqual("00:00:01", vm.Uptime);

        clock.AdvanceBy(TimeSpan.FromSeconds(61));
        Assert.AreEqual("00:01:02", vm.Uptime);

        vm.StopUptimeTimer();
        clock.AdvanceBy(TimeSpan.FromSeconds(5));
        Assert.AreEqual("00:01:02", vm.Uptime);
    }
}
