using VelaShell.XServer.Drawing;
using VelaShell.XServer.Fonts;
using VelaShell.XServer.Resources;

namespace VelaShell.XServer.Tests.Drawing;

/// <summary>纯函数部分:区域运算、光栅化、字体目录、颜色名。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class UnitTests
{
    private static int Area(Region r) => r.Rects.Sum(x => x.Width * x.Height);

    [TestMethod]
    public void 区域减去再并回来面积守恒且互不重叠()
    {
        Region r = new(new XRect(0, 0, 10, 10));
        r.Subtract(new XRect(3, 3, 4, 4));
        Assert.AreEqual(100 - 16, Area(r));
        Assert.IsFalse(r.Contains(4, 4));
        Assert.IsTrue(r.Contains(0, 9));
        r.Union(new XRect(2, 2, 6, 6));
        Assert.AreEqual(100, Area(r));
        for (int i = 0; i < r.Rects.Count; i++)
        {
            for (int j = i + 1; j < r.Rects.Count; j++)
            {
                Assert.IsTrue(r.Rects[i].Intersect(r.Rects[j]).IsEmpty, "矩形之间不许重叠");
            }
        }
    }

    [TestMethod]
    public void 细线含两端点且CapNotLast不画末点()
    {
        PixelBuffer buffer = new(10, 10, 24);
        XGc gc = new(1, null, 24) { Foreground = 1 };
        Rasterizer raster = new(buffer, 0, 0, new Region(buffer.Bounds), gc);
        raster.PolyLine([(1, 1), (5, 1)]);
        Assert.AreEqual(5, buffer.Pixels.Count(p => p == 1));

        gc.CapStyle = 0;   // NotLast
        PixelBuffer b2 = new(10, 10, 24);
        new Rasterizer(b2, 0, 0, new Region(b2.Bounds), gc).PolyLine([(1, 1), (5, 1)]);
        Assert.AreEqual(4, b2.Pixels.Count(p => p == 1));
    }

    [TestMethod]
    public void 多边形按像素中心填充()
    {
        PixelBuffer buffer = new(20, 20, 24);
        XGc gc = new(1, null, 24) { Foreground = 7 };
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc)
            .FillPolygons([[(2.0, 2.0), (12.0, 2.0), (12.0, 12.0), (2.0, 12.0)]], winding: false);
        Assert.AreEqual(100, buffer.Pixels.Count(p => p == 7), "10×10 的方块正好 100 个像素");
        Assert.AreEqual(7u, buffer.Get(2, 2));
        Assert.AreEqual(0u, buffer.Get(12, 12));
    }

    [TestMethod]
    public void 裁剪区域之外不画()
    {
        PixelBuffer buffer = new(20, 20, 24);
        XGc gc = new(1, null, 24) { Foreground = 9, ClipRects = [new XRect(0, 0, 5, 5)] };
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 20, 20);
        Assert.AreEqual(25, buffer.Pixels.Count(p => p == 9));
    }

    [TestMethod]
    public void 平面掩码只改选中的位()
    {
        PixelBuffer buffer = new(1, 1, 24);
        buffer.Pixels[0] = 0x123456;
        XGc gc = new(1, null, 24) { Foreground = 0xFFFFFF, PlaneMask = 0x0000FF };
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 1, 1);
        Assert.AreEqual(0x1234FFu, buffer.Pixels[0]);
    }

    [TestMethod]
    public void 字体目录认别名XLFD与通配()
    {
        FontCatalog catalog = new();
        XFont fixedFont = catalog.Open("fixed")!;
        Assert.IsFalse(fixedFont.IsTwoByte);
        Assert.AreEqual(11, fixedFont.Ascent);
        Assert.IsNotNull(fixedFont.Lookup('A'));

        XFont unicode = catalog.Open("-misc-fixed-medium-r-semicondensed--13-120-75-75-c-60-iso10646-1")!;
        Assert.IsTrue(unicode.IsTwoByte);
        Assert.IsNotNull(unicode.Lookup(0x2500), "制表符 ─");
        Assert.IsNull(catalog.Open("-adobe-helvetica-*"));
        Assert.IsTrue(FontCatalog.WildcardMatch("*-FIXED-*-?3-*", "-misc-fixed-medium-r-normal--13-x"));
        Assert.IsNotEmpty(catalog.Match("*", 1000));
        Assert.AreEqual("cursor", catalog.Open("cursor")!.Name);
    }

    [TestMethod]
    [DataRow("red", 0xFFFF, 0, 0)]
    [DataRow("Light Gray", 0xD3D3, 0xD3D3, 0xD3D3)]
    [DataRow("gray100", 0xFFFF, 0xFFFF, 0xFFFF)]
    [DataRow("grey0", 0, 0, 0)]
    [DataRow("#f00", 0xF000, 0, 0)]
    [DataRow("#336699", 0x3300, 0x6600, 0x9900)]
    [DataRow("rgb:ff/80/0", 0xFFFF, 0x8080, 0)]
    public void 颜色名与数值写法(string spec, int r, int g, int b)
    {
        (ushort R, ushort G, ushort B)? rgb = ColorNames.Lookup(spec);
        Assert.IsNotNull(rgb);
        Assert.AreEqual(((ushort)r, (ushort)g, (ushort)b), rgb.Value);
    }

    [TestMethod]
    public void 认不出来的颜色名返回null() => Assert.IsNull(ColorNames.Lookup("not-a-colour"));
}
