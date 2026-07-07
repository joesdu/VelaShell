using PulseTerm.Presentation.ViewModels;

namespace PulseTerm.Presentation.Tests;

[TestClass]
[TestCategory("StatusBar")]
public class StatusBarNetworkTests
{
    [TestMethod]
    public void DownloadDominates_ShowsDownArrowAndRxRate()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(rxBytesPerSec: 4.2 * 1024 * 1024, txBytesPerSec: 20_000, hasRates: true);

        Assert.AreEqual("↓", vm.NetArrow);
        Assert.AreEqual("4.2 MB/s", vm.NetSpeed);
        Assert.IsTrue(vm.IsNetActive);
    }

    [TestMethod]
    public void UploadDominates_ShowsUpArrowAndTxRate()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(rxBytesPerSec: 1_000, txBytesPerSec: 512 * 1024, hasRates: true);

        Assert.AreEqual("↑", vm.NetArrow);
        Assert.AreEqual("512.0 KB/s", vm.NetSpeed);
        Assert.IsTrue(vm.IsNetActive);
    }

    [TestMethod]
    public void IdleLink_ArrowStaysMuted()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(rxBytesPerSec: 80, txBytesPerSec: 40, hasRates: true);

        Assert.AreEqual("↓", vm.NetArrow);
        Assert.IsFalse(vm.IsNetActive); // keepalive noise below the threshold
    }

    [TestMethod]
    public void NoRatesYet_ShowsZeroWithoutActivity()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(0, 0, hasRates: false);

        Assert.AreEqual("0 B/s", vm.NetSpeed);
        Assert.IsFalse(vm.IsNetActive);
    }

    [TestMethod]
    public void ClearSessionMetrics_HidesAllSegments()
    {
        var vm = new StatusBarViewModel();
        vm.CpuUsage = "23%";
        vm.MemUsage = "1.2G";
        vm.UpdateNetwork(2_000_000, 100, hasRates: true);

        vm.ClearSessionMetrics();

        Assert.AreEqual(string.Empty, vm.CpuUsage);
        Assert.AreEqual(string.Empty, vm.MemUsage);
        Assert.AreEqual(string.Empty, vm.NetSpeed);
        Assert.IsFalse(vm.IsNetActive);
    }
}
