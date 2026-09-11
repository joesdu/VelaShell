using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 标签页的协议图标该按连接类型走。
/// </summary>
/// <remarks>
/// 与 <see cref="ConnectionAccentTests" /> 守的是相邻但不同的一件事:颜色答「哪一台机器」,
/// 图标答「哪一种连接」。这组用例钉的是后者的映射,以及**本地终端不画图标**那一条 ——
/// 没有配置就没有协议可言,硬塞一个图标只会让「这是不是远程会话」更难分。
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class ConnectionIconTests
{
    [TestMethod]
    public void SshGetsTheTerminalGlyph() =>
        Assert.AreEqual(
            ConnectionIcon.SshKey,
            ConnectionIcon.ResourceKeyFor(new() { ConnectionType = ConnectionType.SSH }));

    [TestMethod]
    public void FileProtocolsShareTheDriveGlyph()
    {
        // SFTP 与 FTP 的标签里装的是同一个双栏浏览器,理应同一个字形。
        Assert.AreEqual(
            ConnectionIcon.FileKey,
            ConnectionIcon.ResourceKeyFor(new() { ConnectionType = ConnectionType.SFTP }));
        Assert.AreEqual(
            ConnectionIcon.FileKey,
            ConnectionIcon.ResourceKeyFor(new() { ConnectionType = ConnectionType.FTP }));
    }

    [TestMethod]
    public void PluginProtocolsFallBackToTheGenericPlug() =>
        // 宿主不认识 Redis / 串口 / S3 是什么,也不该认识。插件自报图标之前一律通用插头。
        Assert.AreEqual(
            ConnectionIcon.PluginKey,
            ConnectionIcon.ResourceKeyFor(
                new() { ConnectionType = ConnectionType.Plugin, PluginProtocolId = "velashell.redis" }));

    [TestMethod]
    public void ALocalTerminalHasNoProfileAndThereforeNoIcon() =>
        Assert.IsNull(ConnectionIcon.ResourceKeyFor(null));

    // 「这些键在 Icons.axaml 里真的存在吗」不在这里验:那要一个活着的应用资源字典,
    // 见 Views/SessionTabIconUiTests。这里只验映射本身,不依赖 Application.Current ——
    // 依赖它就会变成一条看同程序集里谁先跑的用例。
}
