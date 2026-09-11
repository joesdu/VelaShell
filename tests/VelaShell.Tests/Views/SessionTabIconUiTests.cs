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
using VelaShell.PluginSdk;
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
                    ConnectionIcon.ForSession(new() { ConnectionType = type }),
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
                ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.SSH })?.Geometry,
                ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.SFTP })?.Geometry,
                ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.Plugin })?.Geometry
            ];

            Assert.HasCount(3, glyphs.Distinct().ToList(), "三种连接类型应当各有各的字形。");
        });
    }

    [TestMethod]
    public void APluginSuppliedIconWinsOverTheGenericPlug()
    {
        // 插件自报的图标必须压过宿主的兜底,否则 Redis / 串口 / AI 面板全都长一个样。
        OnUi(() =>
        {
            TabIcon? plug = ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.Plugin });
            TabIcon? own = ConnectionIcon.ForSession(
                new() { ConnectionType = ConnectionType.Plugin },
                PluginIcon.Stroked("M4 4h16v16H4Z"));

            // 比 Bounds 而不是 ToString():Geometry.ToString() 给的是类型名,比了等于没比。
            Assert.IsNotNull(own);
            Assert.AreNotEqual(plug!.Geometry.Bounds, own!.Geometry.Bounds);
        });
    }

    [TestMethod]
    public void AFilledPluginIconCarriesItsViewBoxAndAFillBrush()
    {
        // 品牌 logo 那条路:视框与填充**必须一起到达渲染层**。
        // 少了视框,一个 1024 的 logo 会被按 24 缩放 —— 放大四十多倍,屏幕上什么也看不见。
        OnUi(() =>
        {
            TabIcon icon = ConnectionIcon.ForSession(
                new() { ConnectionType = ConnectionType.Plugin },
                PluginIcon.Filled("M4 4h16v16H4Z", viewBoxSize: 1024))!;

            Assert.AreEqual(1024d, icon.ViewBoxSize);
            Assert.IsNotNull(icon.Fill, "实心图标要带上填充画刷,否则 LucideIcon 仍按描边画。");
        });
    }

    [TestMethod]
    public void AMalformedPluginPathFallsBackInsteadOfThrowing()
    {
        // 一段畸形路径是插件的问题。宿主当作没给、退回通用插头 —— 绝不能把标签条顶掉。
        OnUi(() =>
        {
            TabIcon? plug = ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.Plugin });
            TabIcon? broken = ConnectionIcon.ForSession(
                new() { ConnectionType = ConnectionType.Plugin },
                PluginIcon.Stroked("这不是路径数据"));

            // 退回的必须**就是**资源字典里那个插头实例,不是碰巧长得像的另一个几何。
            Assert.IsNotNull(broken);
            Assert.AreSame(plug!.Geometry, broken!.Geometry, "应当退回通用插头。");
        });
    }

    [TestMethod]
    public void AnSshTabShowsTheTerminalGlyph()
    {
        OnUi(() =>
        {
            Geometry expected = ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.SSH })!.Geometry;

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
            Geometry ssh = ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.SSH })!.Geometry;

            WithTab(TabFor(null), tab =>
                Assert.IsFalse(
                    VisibleIcons(tab).Any(i => ReferenceEquals(i.Data, ssh)),
                    "本地终端标签不该出现协议图标。"));
        });
    }

    [TestMethod]
    public void APanelTabShowsThePluginsOwnFilledGlyphNotThePlug()
    {
        // AI 助手的聊天页走的是**面板**这条路(PanelOptions.Icon),不是描述符那条。
        // 2.0.3 漏掉的正是这一条,所以它单独钉一遍,而且要钉到渲染层:
        // 视框与填充中断任何一处,画出来都不是那个图标。
        OnUi(() =>
        {
            var document = new PluginDocument(
                "panel-1", "AI", "velashell.ai", new Border(),
                PluginIcon.Filled("M4 4h16v16H4Z", viewBoxSize: 1024));

            var tab = new PluginDockTabItem { DataContext = document };
            var window = new Window { Width = 600, Height = 200, Content = tab };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                LucideIcon glyph = VisibleIcons(tab)
                    .FirstOrDefault(i => ReferenceEquals(i.Data, document.TabIcon!.Geometry))
                    ?? throw new AssertFailedException("面板标签上没有画出插件自报的图标。");

                Assert.AreEqual(1024d, glyph.ViewBoxSize, "视框没到渲染层:按 24 缩放会把它放大四十多倍。");
                Assert.IsNotNull(glyph.Fill, "填充没到渲染层:实心 logo 会被描成一圈轮廓线。");
            }
            finally
            {
                window.Close();
            }
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
