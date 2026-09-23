using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 本机 X Server 的进程管理:选显示号、SSH 自动启动时什么情况下不插手。
/// 端口探测全部注入,不真的拉起 VcXsrv。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public class VcXsrvLocalXServerTests
{
    private static ISettingsService Settings(XServerOptions options)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { XServer = options });
        return settings;
    }

    private static Func<int, CancellationToken, Task<bool>> InUse(params int[] displays) =>
        (display, _) => Task.FromResult(displays.Contains(display));

    /// <summary>自动模式跳过已被占用的号(用户另开的 VcXsrv / X410 常占着 :0)。</summary>
    [TestMethod]
    public async Task SelectFreeDisplay_SkipsDisplaysInUse()
    {
        Assert.AreEqual(0, await VcXsrvLocalXServer.SelectFreeDisplayAsync(InUse(), CancellationToken.None));
        Assert.AreEqual(2, await VcXsrvLocalXServer.SelectFreeDisplayAsync(InUse(0, 1), CancellationToken.None));
    }

    [TestMethod]
    public async Task SelectFreeDisplay_AllTaken_ReturnsNull()
    {
        int[] all = [.. Enumerable.Range(0, XServerOptions.MaxDisplayNumber + 1)];

        Assert.IsNull(await VcXsrvLocalXServer.SelectFreeDisplayAsync(InUse(all), CancellationToken.None));
    }

    /// <summary>用户关了「X11 转发时自动启动」:不接管,按老规矩走 DISPLAY / 默认值。</summary>
    [TestMethod]
    public async Task ResolveForwarding_AutoStartOff_DoesNotTakeOver()
    {
        using VcXsrvLocalXServer server = new(Settings(new XServerOptions { AutoStartForX11Forwarding = false }), InUse());

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.IsNull(resolution.Error);
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    /// <summary>
    /// 本机 :0 上已经有别的 X 服务端:用户已经有一个在用的显示,再开一个只会让窗口出现在意料之外的地方。
    /// </summary>
    [TestMethod]
    public async Task ResolveForwarding_ExistingServerOnDisplayZero_DoesNotTakeOver()
    {
        // 路径指向一个真实存在的文件,确保走到的是「:0 已被占用」这一条,而不是「找不到 VcXsrv」。
        string fake = Path.GetTempFileName();
        try
        {
            using VcXsrvLocalXServer server = new(Settings(new XServerOptions { ExecutablePath = fake }), InUse(0));

            XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

            Assert.IsNull(resolution.Display);
            Assert.IsNull(resolution.Error);
            Assert.AreEqual(XServerState.Stopped, server.State);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    /// <summary>没装 VcXsrv 的人不该在每次连接时收到一条提示:静默不接管。</summary>
    [TestMethod]
    public async Task ResolveForwarding_NotInstalled_IsSilent()
    {
        using VcXsrvLocalXServer server = new(
            Settings(new XServerOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-dir", "vcxsrv.exe") }),
            InUse());

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.IsNull(resolution.Error);
    }

    /// <summary>手动启动时找不到:要给出能照着做的原因(填的路径不存在)。</summary>
    [TestMethod]
    public async Task Start_ConfiguredPathMissing_FailsWithReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("本机 X Server 只在 Windows 上启用。");
        }
        string missing = Path.Combine(Path.GetTempPath(), "no-such-dir", "vcxsrv.exe");
        using VcXsrvLocalXServer server = new(Settings(new XServerOptions { ExecutablePath = missing }), InUse());

        XServerStartResult result = await server.StartAsync();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, missing);
        Assert.AreEqual(XServerState.Stopped, server.State);
        Assert.IsNull(server.Display);
    }

    /// <summary>固定显示号被占用:启动前就报出来,不去拉一个注定起不来的 VcXsrv。</summary>
    [TestMethod]
    public async Task Start_FixedDisplayInUse_FailsBeforeLaunching()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("本机 X Server 只在 Windows 上启用。");
        }
        string fake = Path.GetTempFileName();
        try
        {
            using VcXsrvLocalXServer server = new(
                Settings(new XServerOptions { ExecutablePath = fake, DisplayNumber = 4 }), InUse(4));

            XServerStartResult result = await server.StartAsync();

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, ":4");
            Assert.AreEqual(XServerState.Stopped, server.State);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [TestMethod]
    public async Task NonWindows_IsUnsupportedAndNeverTakesOver()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("只在非 Windows 平台上验证。");
        }
        using VcXsrvLocalXServer server = new(Settings(new XServerOptions()), InUse());

        Assert.IsFalse(server.IsSupported);
        Assert.IsNull(server.FindExecutable(null));
        Assert.IsFalse((await server.StartAsync()).Success);
        Assert.IsNull((await server.ResolveForwardingDisplayAsync()).Display);
    }
}
