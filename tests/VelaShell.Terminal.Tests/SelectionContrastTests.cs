using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 选区高亮看不看得见(#选区对比度)。
/// <para>
/// 回归的是这么一个具体故障:选区色此前按半透明叠加绘制(暗 35% / 亮 25%),而各家方案的
/// 选区色本就是照不透明填充设计的、与自家背景只差一线,乘上不透明度后实测对比度只剩
/// 1.05:1 ~ 1.21:1 —— 拖完一段跟没拖一样,用户以为压根没选中。
/// </para>
/// </summary>
[TestClass]
[TestCategory("TerminalPalette")]
public class SelectionContrastTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>各家方案的背景 + 原生选区色。前四组是原本淡到看不见的那几套。</summary>
    private static IEnumerable<object[]> Schemes =>
    [
        ["Solarized Light", Hex(0xFDF6E3), Hex(0xEEE8D5)],
        ["Solarized Dark", Hex(0x002B36), Hex(0x073642)],
        ["One Light", Hex(0xFAFAFA), Hex(0xE5E5E6)],
        ["GitHub Light", Hex(0xFFFFFF), Hex(0xCFE4FB)],
        ["Dracula", Hex(0x282A36), Hex(0x44475A)],
        ["Tokyo Night", Hex(0x1A1B26), Hex(0x33467C)],
        ["Nord", Hex(0x2E3440), Hex(0x434C5E)],
        ["Alucard", Hex(0xFFFBEB), Hex(0xCFCFDE)],
        ["Monokai", Hex(0x272822), Hex(0x49483E)],
        ["Sakura", Hex(0xFFFBFD), Hex(0xF7D9E5)],
        ["Obsidian", Hex(0x131316), Hex(0x2E2E38)],
        ["Rosé Pine Dawn", Hex(0xFFFAF3), Hex(0xEADDD3)],
    ];

    [TestMethod]
    [DynamicData(nameof(Schemes))]
    public void Fill_AlwaysClearsTheVisibilityFloor(string name, Rgba background, Rgba selection)
    {
        Rgba fill = SelectionContrast.Fill(selection, background);

        double delta = Math.Abs(SelectionContrast.Lightness(fill) - SelectionContrast.Lightness(background));
        Assert.IsTrue(delta >= SelectionContrast.MinLightnessDelta - 0.01,
            $"{name}:选区底与终端底只差 L* {delta:F1},低于 {SelectionContrast.MinLightnessDelta} 就等于没画。");
        Assert.AreEqual(0xFF, fill.A, $"{name}:选区底必须不透明 —— 半透明正是原先看不见的根因。");
    }

    /// <summary>够用的方案不该被动:整定只托底,不夺方案自己的设计。</summary>
    [TestMethod]
    public void Fill_LeavesAlreadyVisibleSchemesAlone()
    {
        Assert.AreEqual(Hex(0x33467C), SelectionContrast.Fill(Hex(0x33467C), Hex(0x1A1B26)), "Tokyo Night");
        Assert.AreEqual(Hex(0x49483E), SelectionContrast.Fill(Hex(0x49483E), Hex(0x272822)), "Monokai");
    }

    /// <summary>推的方向由背景明暗定:暗底上提亮、亮底上压深。</summary>
    [TestMethod]
    public void Fill_PushesAwayFromTheBackground()
    {
        Rgba onDark = SelectionContrast.Fill(Hex(0x073642), Hex(0x002B36));
        Rgba onLight = SelectionContrast.Fill(Hex(0xEEE8D5), Hex(0xFDF6E3));

        Assert.IsTrue(SelectionContrast.Lightness(onDark) > SelectionContrast.Lightness(Hex(0x002B36)));
        Assert.IsTrue(SelectionContrast.Lightness(onLight) < SelectionContrast.Lightness(Hex(0xFDF6E3)));
    }

    /// <summary>选区色与背景撞成同一个色也要能推开(最坏情况不能死循环或原样返回)。</summary>
    [TestMethod]
    public void Fill_HandlesSelectionIdenticalToBackground()
    {
        foreach (Rgba bg in new[] { Hex(0x000000), Hex(0xFFFFFF), Hex(0x808080), Hex(0x282A36) })
        {
            Rgba fill = SelectionContrast.Fill(bg, bg);
            double delta = Math.Abs(SelectionContrast.Lightness(fill) - SelectionContrast.Lightness(bg));
            Assert.IsTrue(delta >= SelectionContrast.MinLightnessDelta - 0.01, $"背景 {bg.Packed:X8} 上推不开。");
        }
    }

    /// <summary>撞明度才换前景;不撞的一律原样,免得选中一段就把 ls --color 的配色抹平。</summary>
    [TestMethod]
    public void ReadableForeground_OnlyReplacesWhatWouldVanish()
    {
        Rgba fill = SelectionContrast.Fill(Hex(0x073642), Hex(0x002B36));

        Assert.AreEqual(Hex(0x839496), SelectionContrast.ReadableForeground(fill, Hex(0x839496)),
            "默认前景读得清就用默认前景。");
        Assert.IsTrue(
            Math.Abs(SelectionContrast.Brightness(SelectionContrast.ReadableForeground(fill, fill))
                - SelectionContrast.Brightness(fill)) >= SelectionContrast.MinForegroundDelta,
            "默认前景自己也撞的话得退到纯白/纯黑。");
    }

    /// <summary>控件层:三层配色叠完后落到调色板上的,是整定过的填充色。</summary>
    [TestMethod]
    public void Control_NormalizesSelectionAfterAllPaletteLayers()
    {
        OnUi(() =>
        {
            // Solarized Light 那套:选区 base2 与背景 base3 原生只差 L* 5,是最淡的一档。
            var control = new VelaTerminalControl
            {
                ThemePalette = new()
                {
                    Background = Hex(0xFDF6E3),
                    Foreground = Hex(0x657B83),
                    Selection = Hex(0xEEE8D5),
                },
            };

            TerminalPalette palette = control.PaletteForTest;
            double delta = Math.Abs(
                SelectionContrast.Lightness(palette.SelectionBackground)
                - SelectionContrast.Lightness(palette.DefaultBackground));

            Assert.AreEqual(0xFF, palette.SelectionBackground.A);
            Assert.IsTrue(delta >= SelectionContrast.MinLightnessDelta - 0.01, $"只差 L* {delta:F1}。");
        });
    }

    private static Rgba Hex(uint rgb) => Rgba.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
