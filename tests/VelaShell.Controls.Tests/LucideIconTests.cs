using Avalonia.Media;
using VelaShell.Controls.Controls;

namespace VelaShell.Controls.Tests;

[TestClass]
public class LucideIconTests
{
    [TestMethod]
    public void NewIcon_HasNoGeometryOrBrushByDefault()
    {
        LucideIcon icon = new();
        Assert.IsNull(icon.Data);
        Assert.IsNull(icon.Foreground);
    }

    [TestMethod]
    public void ForegroundProperty_RoundTrips()
    {
        LucideIcon icon = new() { Foreground = Brushes.Red };
        Assert.AreSame(Brushes.Red, icon.Foreground);
    }

    [TestMethod]
    public void ViewBoxDefaultsToLucidesTwentyFour() =>
        // 宿主自己的图标集全是 24。默认值一变,满屏图标当场改大小。
        Assert.AreEqual(24d, new LucideIcon().ViewBoxSize);

    [TestMethod]
    public void FillAndViewBoxRoundTrip()
    {
        // 这两个口子是给插件的实心品牌 logo 开的:那些 logo 的视图框往往是 1024 之类。
        LucideIcon icon = new() { Fill = Brushes.Red, ViewBoxSize = 1030 };
        Assert.AreSame(Brushes.Red, icon.Fill);
        Assert.AreEqual(1030d, icon.ViewBoxSize);
    }

    [TestMethod]
    public void FillIsNullByDefaultSoTheHostIconsStayStroked() =>
        // 默认不填充 —— 描边才是 lucide 的语言,填充会把每个字形变成一团实心块。
        Assert.IsNull(new LucideIcon().Fill);
}
