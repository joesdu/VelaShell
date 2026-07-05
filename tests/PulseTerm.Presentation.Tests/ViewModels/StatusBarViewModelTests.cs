using PulseTerm.Core.Resources;
using PulseTerm.Presentation.ViewModels;

namespace PulseTerm.Presentation.Tests.ViewModels;

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
        var vm = new StatusBarViewModel();

        vm.Status = Strings.Connected;

        Assert.IsTrue(vm.IsConnected);
    }

    [TestMethod]
    public async Task StartUptimeTimer_UpdatesUptimeProperty()
    {
        var vm = new StatusBarViewModel();

        vm.StartUptimeTimer();
        await Task.Delay(1500);

        Assert.IsFalse(string.IsNullOrEmpty(vm.Uptime));
        vm.StopUptimeTimer();
    }
}
