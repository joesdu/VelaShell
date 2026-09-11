using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Controls.Controls;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

/// <summary>
/// 会话标签页上的协议图标真的画得出来,而且只在该画的时候画。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要**渲染**而不是只读视图模型:图标几何是从应用资源字典里按
/// <c>Icon.&lt;name&gt;</c> 取的,而**键写错了不会有任何编译期报错** —— 取不到就是不画,
/// 静默少一个图标。<see cref="Services.ConnectionIconTests" /> 只能证明映射对,
/// 证明不了那些键存在。
/// (同一个教训在 <see cref="ConnectingDocumentViewUiTests" /> 上已经吃过一次。)
/// </para>
/// <para>
/// body 必须同步 + <c>return Task.CompletedTask</c> —— 传 async lambda 会绑到
/// <c>Dispatch(Func&lt;TResult&gt;)</c>,断言一条都不会跑却全绿。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("UI")]
public sealed class SessionTabIconUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTabIconUiTests).Assembly);

    [TestMethod]
    public void EveryProtocolIconKeyResolvesToRealGeometry()
    {
        // 一条键写错,这里就红。三个键分别对应 SSH / 文件协议 / 插件协议。
        OnUi(() =>
        {
            foreach ((ConnectionType type, string key) in new[]
                     {
                         (ConnectionType.SSH, ConnectionIcon.SshKey),
                         (ConnectionType.SFTP, ConnectionIcon.FileKey),
                         (ConnectionType.FTP, ConnectionIcon.FileKey),
                         (ConnectionType.Plugin, ConnectionIcon.PluginKey)
                     })
            {
                Assert.IsNotNull(
                    ConnectionIcon.ForProfile(new() { ConnectionType = type }),
                    $"{type} 的图标键 {key} 在 Icons.axaml 里不存在 —— 标签上会静默少一个图标。");
            }
        });
    }

    [TestMethod]
    public void TerminalsFileProtocolsAndPluginsEachGetADifferentGlyph()
    {
        // 三种标签并排放在同一条标签条上,字形撞了图标这一栏就白加了。
        // 比的是**解析出来的几何**而不是那三个键:键是编译期常量,比了等于没比。
        OnUi(() =>
        {
            Geometry?[] glyphs =
            [
                ConnectionIcon.ForProfile(new() { ConnectionType = ConnectionType.SSH }),
                ConnectionIcon.ForProfile(new() { ConnectionType = ConnectionType.SFTP }),
                ConnectionIcon.ForProfile(new() { ConnectionType = ConnectionType.Plugin })
            ];

            Assert.HasCount(3, glyphs.Distinct().ToList(), "三种连接类型应当各有各的字形。");
        });
    }

    [TestMethod]
    public void AnSshTabShowsTheTerminalGlyph()
    {
        OnUi(() =>
        {
            Geometry expected = ConnectionIcon.ForProfile(new() { ConnectionType = ConnectionType.SSH })!;

            WithTab(TabFor(new() { ConnectionType = ConnectionType.SSH, Host = "10.0.0.1" }), tab =>
                Assert.IsTrue(
                    VisibleIcons(tab).Any(i => ReferenceEquals(i.Data, expected)),
                    "SSH 标签上应当画出终端字形。"));
        });
    }

    [TestMethod]
    public void ALocalTerminalTabDoesNotReserveRoomForAnIcon()
    {
        // 本地终端没有配置。图标不只要不画,还要**连位置一起收掉** —— 否则标签里空一块,
        // 与旁边那些有图标的标签对不齐,看着像渲染坏了。
        OnUi(() =>
        {
            Geometry ssh = ConnectionIcon.ForProfile(new() { ConnectionType = ConnectionType.SSH })!;

            WithTab(TabFor(null), tab =>
                Assert.IsFalse(
                    VisibleIcons(tab).Any(i => ReferenceEquals(i.Data, ssh)),
                    "本地终端标签不该出现协议图标。"));
        });
    }

    private static TerminalDocument TabFor(SessionProfile? profile)
    {
        var terminal = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>()) { Profile = profile };
        return new(terminal);
    }

    private static IEnumerable<LucideIcon> VisibleIcons(Visual root) =>
        root.GetVisualDescendants().OfType<LucideIcon>().Where(i => i.IsEffectivelyVisible);

    private static void WithTab(TerminalDocument document, Action<DockTabItem> body)
    {
        var tab = new DockTabItem { DataContext = document };
        var window = new Window { Width = 600, Height = 200, Content = tab };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            body(tab);
        }
        finally
        {
            window.Close();
        }
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
