using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shapes = Avalonia.Controls.Shapes;

namespace VelaShell.Tests.Views;

/// <summary>
/// 右键菜单里两个字形 —— 勾号(PART_ToggleIconPresenter)与子菜单箭头(PART_ChevronPath)——
/// 的尺寸校准。加载真实的 DockStyles,对着真实的 Fluent 模板验证。
/// </summary>
/// <remarks>
/// 两者 Fluent 都按它默认的 14 号字定死了尺寸,而菜单用的是 11 号,不缩就比字大一号。
/// 这里盯着 RenderTransform 而不是 Width/Height,是因为那些尺寸是模板标记里的局部值,
/// 样式设不动 —— 设了既不报错也不生效。缩放是唯一能落地的路子,故本测试同时钉住
/// 「别改回 Width/Height」。
///
/// 这里只守机制与安全边界(缩放确实生效、不超过字高、不缩到看不清、布局不被扰动),
/// 不规定具体比例 —— 比例是照观感调的,该由眼睛拍板。别把某次调出来的数字写成断言,
/// 那样调一次观感就得改一次测试,测试也就不再说明任何事情。
/// </remarks>
[TestClass]
[TestCategory("MenuGlyphStyle")]
public class MenuGlyphStyleTests
{
    /// <summary>Fluent 模板里写死的字形尺寸(按它默认的 14 号字定的)。</summary>
    private const double FluentChevronWidth = 8;
    private const double FluentChevronHeight = 16;
    private const double FluentToggleSlot = 16;

    /// <summary>勾号图形本身的高度(16x13,窄于它 16x16 的布局槽)。</summary>
    private const double FluentCheckGlyphHeight = 13;

    /// <summary>菜单文字的实际行高(FontSize 11)。字形不该比它更高。</summary>
    private const double MenuTextHeight = 12;

    /// <summary>
    /// 字形渲染高度的下限。这是道防事故的底线(防手滑缩成一个点),不是规格 ——
    /// 具体缩放比例是照着观感定的,该由眼睛而不是这条断言来拍。
    /// </summary>
    private const double LegibleGlyphHeight = 7;

    /// <summary>DockStyles 给右键菜单钉死的字号。上面两个缩放比例就是按它算出来的。</summary>
    private const double MenuFontSize = 11;

    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App,否则谁先跑谁的样式说了算。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MenuGlyphStyleTests).Assembly);

    /// <summary>本次用例开出来的菜单与窗口,由 <see cref="CloseOpenedMenu" /> 收尾。</summary>
    private static ContextMenu? _openedMenu;
    private static Window? _openedWindow;

    /// <summary>
    /// 关掉本用例开出来的右键菜单与窗口。全程序集共用一条 headless UI 线程,残留的窗口/弹出层
    /// 会持续往 dispatcher 排工作项,而 <see cref="OpenMenu" /> 里的
    /// <c>Dispatcher.UIThread.RunJobs()</c> 要等队列排空才返回 —— 队列一直被填着,它就永不返回,
    /// UI 线程被占死,其后所有测试的 Dispatch 无限期排队。本类既是受害者也是泄漏者。
    /// </summary>
    [TestCleanup]
    public void CloseOpenedMenu() =>
        OnUi(() =>
        {
            _openedMenu?.Close();
            _openedMenu = null;
            _openedWindow?.Close();
            _openedWindow = null;
        });

    [TestMethod]
    public void SubmenuChevron_IsScaledDown_ToMatchTheMenuFontSize()
    {
        OnUi(() =>
        {
            Shapes.Path chevron = OpenMenu().Chevron;

            Assert.IsNotNull(chevron.RenderTransform, "子菜单箭头应被缩放:Fluent 的 8x16 是按 14 号字定的,菜单用的是 11 号。");

            double scale = UniformScaleOf(chevron.RenderTransform, "箭头");
            double rendered = FluentChevronHeight * scale;
            Assert.IsLessThanOrEqualTo(MenuTextHeight, rendered, "箭头渲染高度不应超过菜单文字的行高。");
            Assert.IsGreaterThanOrEqualTo(LegibleGlyphHeight, rendered, "也不该缩到看不清。");
        });
    }

    [TestMethod]
    public void CheckGlyph_IsScaledDown_ToMatchTheMenuFontSize()
    {
        OnUi(() =>
        {
            ContentControl toggle = OpenMenu().ToggleIcon;

            Assert.IsNotNull(toggle.RenderTransform, "勾号应被缩放:Fluent 的 16x13 同样是按 14 号字定的。");

            double scale = UniformScaleOf(toggle.RenderTransform, "勾号");
            double rendered = FluentCheckGlyphHeight * scale;
            Assert.IsLessThanOrEqualTo(MenuTextHeight, rendered, "勾号渲染高度不应超过菜单文字的行高。");
            Assert.IsGreaterThanOrEqualTo(LegibleGlyphHeight, rendered, "也不该缩到看不清。");
        });
    }

    /// <summary>
    /// 布局槽仍由模板决定 —— 缩放只改观感,不动排布,所以文字不会随勾选状态或箭头位移。
    /// </summary>
    [TestMethod]
    public void ScalingGlyphs_DoesNotDisturbTheMenuItemLayout()
    {
        OnUi(() =>
        {
            (_, ContentControl toggle, Shapes.Path chevron) = OpenMenu();

            Assert.AreEqual(FluentToggleSlot, toggle.Bounds.Width, "勾选列宽度应仍由模板决定。");
            Assert.AreEqual(FluentToggleSlot, toggle.Bounds.Height, "勾选列高度应仍由模板决定。");
            Assert.AreEqual(FluentChevronWidth, chevron.Bounds.Width, "箭头布局槽宽度应仍由模板决定。");
            Assert.AreEqual(FluentChevronHeight, chevron.Bounds.Height, "箭头布局槽高度应仍由模板决定。");
        });
    }

    private static double UniformScaleOf(ITransform? transform, string what)
    {
        Assert.IsNotNull(transform, $"{what}应被缩放。");
        Matrix m = transform.Value;
        Assert.AreEqual(m.M11, m.M22, $"{what}应等比缩放,不该被压扁。");
        Assert.IsLessThan(1.0, m.M11, $"{what}应是缩小。");
        return m.M11;
    }

    /// <summary>
    /// 字形缩放是按固定的 11 号菜单字号算死的,所以右键菜单必须不跟随「设置 → 外观 → 界面字号」——
    /// 否则调大字号后字形会相对变小、调小后又相对变大,这两个比例就失准了。
    /// 一旦哪天要让菜单跟随字号,缩放就得改成按字号推算,而不是常量。
    /// </summary>
    [TestMethod]
    public void MenuFontSize_DoesNotFollowTheUiFontSizeSetting_SoTheGlyphScalesStayValid()
    {
        OnUi(() =>
        {
            IResourceDictionary res = Application.Current!.Resources;

            // 对照窗口也必须关(理由同 CloseOpenedMenu):留着它会一直往 dispatcher 排工作项。
            Window? probe = null;
            try
            {
                // 模拟「设置 → 外观 → 界面字号 = 20」(见 MainWindow.ApplyWindowAppearance)。
                res["VelaUiFontSize"] = 20.0;
                res["ControlContentThemeFontSize"] = 20.0;
                Dispatcher.UIThread.RunJobs();

                // 对照:普通控件必须真的跟着变,否则下面那条断言是空的 —— 只能说明设置没生效。
                var button = new Button { Content = "确定" };
                probe = new Window { Width = 200, Height = 100, Content = button };
                probe.Show();
                Dispatcher.UIThread.RunJobs();
                probe.UpdateLayout();
                Assert.AreEqual(20.0, button.FontSize, "界面字号设置本应作用到普通控件上。");

                Assert.AreEqual(MenuFontSize, OpenMenu().Check.FontSize,
                                "右键菜单字号由 DockStyles 钉死,不应跟随界面字号 —— 字形缩放正是按它算的。");
            }
            finally
            {
                probe?.Close();
                res.Remove("VelaUiFontSize");
                res.Remove("ControlContentThemeFontSize");
            }
        });
    }

    /// <summary>弹出一个既有勾选项、又有子菜单项的右键菜单(与生产的菜单同形)。</summary>
    private static (MenuItem Check, ContentControl ToggleIcon, Shapes.Path Chevron) OpenMenu()
    {
        var check = new MenuItem { Header = "时间戳", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        var sub = new MenuItem { Header = "移动到分组" };
        sub.Items.Add(new MenuItem { Header = "未分组" });

        var menu = new ContextMenu();
        menu.Items.Add(check);
        menu.Items.Add(sub);

        var host = new Border { Width = 200, Height = 100, ContextMenu = menu };
        var window = new Window { Width = 400, Height = 300, Content = host };
        _openedWindow = window;
        _openedMenu = menu;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        menu.Open(host);
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel(check)?.UpdateLayout();

        ContentControl toggle = check.GetVisualDescendants().OfType<ContentControl>()
                                     .Single(c => c.Name == "PART_ToggleIconPresenter");
        Assert.IsTrue(toggle.IsVisible, "已勾选的项应显示勾号。");

        Shapes.Path chevron = sub.GetVisualDescendants().OfType<Shapes.Path>().Single(p => p.Name == "PART_ChevronPath");
        Assert.IsTrue(chevron.IsVisible, "有子菜单的项应显示箭头。");

        return (check, toggle, chevron);
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
