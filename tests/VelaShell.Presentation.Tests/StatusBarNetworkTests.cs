using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests;

[TestClass]
[TestCategory("StatusBar")]
public class StatusBarNetworkTests
{
    [TestMethod]
    public void DownloadDominates_LightsDownArrow_ShowsRxRate()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(rxBytesPerSec: 4.2 * 1024 * 1024, txBytesPerSec: 200, hasRates: true);

        Assert.IsTrue(vm.IsNetDownActive);
        Assert.IsFalse(vm.IsNetUpActive); // tx is just keepalive noise
        Assert.AreEqual("4.2 MB/s", vm.NetSpeed);
    }

    [TestMethod]
    public void UploadDominates_LightsUpArrow_ShowsTxRate()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(rxBytesPerSec: 200, txBytesPerSec: 512 * 1024, hasRates: true);

        Assert.IsTrue(vm.IsNetUpActive);
        Assert.IsFalse(vm.IsNetDownActive);
        Assert.AreEqual("512.0 KB/s", vm.NetSpeed);
    }

    [TestMethod]
    public void BothDirectionsFlowing_LightBothArrows()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(rxBytesPerSec: 2 * 1024 * 1024, txBytesPerSec: 1 * 1024 * 1024, hasRates: true);

        Assert.IsTrue(vm.IsNetUpActive);
        Assert.IsTrue(vm.IsNetDownActive);
        Assert.AreEqual("2.0 MB/s", vm.NetSpeed); // dominant direction's rate
    }

    [TestMethod]
    public void IdleLink_BothArrowsMuted()
    {
        var vm = new StatusBarViewModel();

        vm.UpdateNetwork(rxBytesPerSec: 80, txBytesPerSec: 40, hasRates: true);

        Assert.IsFalse(vm.IsNetUpActive);
        Assert.IsFalse(vm.IsNetDownActive);
    }

    [TestMethod]
    public void BeforeFirstSample_ShowsIdlePlaceholders()
    {
        var vm = new StatusBarViewModel();

        // 用户要求: the three segments are visible from app start, just without data.
        Assert.AreEqual("--", vm.CpuUsage);
        Assert.AreEqual("--", vm.MemUsage);
        Assert.AreEqual("0 B/s", vm.NetSpeed);
        Assert.IsFalse(vm.IsNetUpActive);
        Assert.IsFalse(vm.IsNetDownActive);
    }

    [TestMethod]
    public void ClearSessionMetrics_ResetsToPlaceholders_NotEmpty()
    {
        var vm = new StatusBarViewModel();
        vm.CpuUsage = "23.45%";
        vm.MemUsage = "26.3%";
        vm.UpdateNetwork(2_000_000, 100, hasRates: true);

        vm.ClearSessionMetrics();

        Assert.AreEqual("--", vm.CpuUsage);
        Assert.AreEqual("--", vm.MemUsage);
        Assert.AreEqual("0 B/s", vm.NetSpeed);
        Assert.IsFalse(vm.IsNetUpActive);
        Assert.IsFalse(vm.IsNetDownActive);
    }
}
