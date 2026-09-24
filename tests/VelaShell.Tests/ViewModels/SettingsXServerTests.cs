using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.XServer;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>设置 → X Server 页的绑定层:下拉索引与字符串值的映射、找程序的状态行、命令行预览。</summary>
[TestClass]
[TestCategory("XServer")]
public class SettingsXServerTests
{
    private static SettingsViewModel CreateViewModel(ILocalXServer? server = null) =>
        new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>(), localXServer: server);

    [TestMethod]
    public void DisplayNumberIndex_ZeroIsAuto_OthersAreOffsetByOne()
    {
        SettingsViewModel vm = CreateViewModel();

        Assert.AreEqual(0, vm.XServerDisplayNumberIndex);
        Assert.HasCount(XServerOptions.MaxDisplayNumber + 2, vm.XServerDisplayNumbers);

        vm.XServerDisplayNumberIndex = 3;
        Assert.AreEqual(2, vm.XServer.DisplayNumber);
        Assert.AreEqual(":2", vm.XServerDisplayNumbers[vm.XServerDisplayNumberIndex]);

        vm.XServerDisplayNumberIndex = 0;
        Assert.AreEqual(XServerOptions.AutoDisplayNumber, vm.XServer.DisplayNumber);
    }

    /// <summary>窗口模式下拉的条目顺序与 <see cref="XServerWindowModes.All" /> 一一对应。</summary>
    [TestMethod]
    public void WindowModeIndex_FollowsTheCanonicalOrder()
    {
        SettingsViewModel vm = CreateViewModel();

        for (int i = 0; i < XServerWindowModes.All.Count; i++)
        {
            vm.XServerWindowModeIndex = i;
            Assert.AreEqual(XServerWindowModes.All[i], vm.XServer.WindowMode);
        }
    }

    /// <summary>首项是「自动」,存的是空串(不传 -xkblayout)。</summary>
    [TestMethod]
    public void KeyboardLayout_FirstItemIsAuto()
    {
        SettingsViewModel vm = CreateViewModel();

        vm.XServerKeyboardLayoutIndex = 0;

        Assert.AreEqual(string.Empty, vm.XServer.KeyboardLayout);
        Assert.AreEqual(0, vm.XServerKeyboardLayoutIndex);
    }

    /// <summary>表外的值(导入的、手改的)原样保留,下拉显示为空,不被悄悄改掉。</summary>
    [TestMethod]
    public void KeyboardLayout_UnknownValue_IsKeptAndShownAsNoSelection()
    {
        SettingsViewModel vm = CreateViewModel();
        vm.XServer.KeyboardLayout = "sk";

        Assert.AreEqual(-1, vm.XServerKeyboardLayoutIndex);
        vm.XServerKeyboardLayoutIndex = -1; // ComboBox 回写 -1 时不动值
        Assert.AreEqual("sk", vm.XServer.KeyboardLayout);
    }

    [TestMethod]
    public void KeyboardModel_DefaultIsPc105()
    {
        SettingsViewModel vm = CreateViewModel();

        Assert.AreEqual("pc105", vm.XServerKeyboardModels[vm.XServerKeyboardModelIndex].Value);
    }

    /// <summary>改路径那一栏会立刻重新查找,状态行跟着变。</summary>
    [TestMethod]
    public void Detection_FollowsTheExecutablePath()
    {
        ILocalXServer server = Substitute.For<ILocalXServer>();
        server.IsSupported.Returns(true);
        server.FindExecutable(Arg.Any<string?>()).Returns(call =>
            call.Arg<string?>() == @"D:\x\vcxsrv.exe" ? @"D:\x\vcxsrv.exe" : null);
        SettingsViewModel vm = CreateViewModel(server);

        vm.XServer.ExecutablePath = @"D:\x\vcxsrv.exe";
        Assert.IsTrue(vm.XServerExecutableFound);
        StringAssert.Contains(vm.XServerDetectionText, @"D:\x\vcxsrv.exe");

        vm.XServer.ExecutablePath = @"D:\nope.exe";
        Assert.IsFalse(vm.XServerExecutableFound);
        StringAssert.Contains(vm.XServerDetectionText, @"D:\nope.exe");
    }

    /// <summary>引擎默认内置;选了 VcXsrv 之后 VcXsrv 专属的几节才出现(只在 Windows 上能选)。</summary>
    [TestMethod]
    public void Engine_DefaultsToBuiltIn_AndSwitchesSections()
    {
        SettingsViewModel vm = CreateViewModel();
        Assert.AreEqual(0, vm.XServerEngineIndex);
        Assert.IsTrue(vm.XServerUsesBuiltIn);
        Assert.IsFalse(vm.XServerUsesVcXsrv);

        List<string?> raised = [];
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.XServerEngineIndex = 1;
        Assert.AreEqual(XServerEngines.VcXsrv, vm.XServer.Engine);
        Assert.AreEqual(OperatingSystem.IsWindows(), vm.XServerUsesVcXsrv, "VcXsrv 只在 Windows 上生效");
        CollectionAssert.Contains(raised, nameof(SettingsViewModel.XServerUsesVcXsrv));

        vm.XServerEngineIndex = 0;
        Assert.AreEqual(XServerEngines.BuiltIn, vm.XServer.Engine);
        Assert.IsTrue(vm.XServerUsesBuiltIn);
    }

    /// <summary>命令行预览随每一项实时更新;自动显示号写成「:自动」而不是一个猜出来的数。</summary>
    [TestMethod]
    public void CommandPreview_TracksChanges()
    {
        SettingsViewModel vm = CreateViewModel();
        List<string?> raised = [];
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        StringAssert.StartsWith(vm.XServerCommandPreview, "vcxsrv.exe :" + VelaShell.Core.Resources.Strings.Get("SetXServer_DisplayAuto") + " ");
        vm.XServer.KeyHook = true;

        CollectionAssert.Contains(raised, nameof(SettingsViewModel.XServerCommandPreview));
        StringAssert.Contains(vm.XServerCommandPreview, "-keyhook");

        vm.XServer.ExtraArguments = "-logfile \"C:\\my logs\\x.log\"";
        StringAssert.Contains(vm.XServerCommandPreview, "\"C:\\my logs\\x.log\"");
    }
}
