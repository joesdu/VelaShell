using System.Text;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Fonts;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;

namespace VelaShell.XServer.Tests.Drawing;

/// <summary>纯函数部分:区域运算、光栅化、字体目录、颜色名、请求读取。</summary>
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

    private static PixelBuffer Draw(Action<Rasterizer> draw, Action<XGc>? setup = null)
    {
        PixelBuffer buffer = new(24, 24, 24);
        XGc gc = new(1, null, 24) { Foreground = 7 };
        setup?.Invoke(gc);
        draw(new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc));
        return buffer;
    }

    private static int[] LitRows(PixelBuffer b) =>
        [.. Enumerable.Range(0, b.Height).Where(y => Enumerable.Range(0, b.Width).Any(x => b.Get(x, y) == 7))];

    [TestMethod]
    public void 宽线与多边形在整数坐标采样_整数坐标就是像素中心()
    {
        // lw = 1 的水平线 y = 10 画在第 10 行(原先在 row + 0.5 采样,画到了第 9 行)。
        PixelBuffer one = Draw(r => r.PolyLine([(2, 10), (12, 10)]), gc => gc.LineWidth = 1);
        CollectionAssert.AreEqual(new[] { 10 }, LitRows(one));
        Assert.AreEqual(10, one.Pixels.Count(p => p == 7), "CapButt:左闭右开,x = 2..11");

        PixelBuffer three = Draw(r => r.PolyLine([(2, 10), (12, 10)]), gc => gc.LineWidth = 3);
        CollectionAssert.AreEqual(new[] { 9, 10, 11 }, LitRows(three));

        // 同一个矩形边框,lw = 0 与 lw = 1 画在同一批像素上(原先错开一像素)。
        (int, int)[] outline = [(5, 5), (10, 5), (10, 10), (5, 10), (5, 5)];
        PixelBuffer thin = Draw(r => r.PolyLine(outline, closed: true));
        PixelBuffer wide = Draw(r => r.PolyLine(outline, closed: true), gc => gc.LineWidth = 1);
        CollectionAssert.AreEqual(thin.Pixels.ToArray(), wide.Pixels.ToArray());

        // 三角形 (0,0)(10,0)(0,10):第 y 行是 x ∈ [0, 10 − y),共 10 + 9 + … + 1 = 55 个像素(原先 45)。
        PixelBuffer triangle = Draw(r => r.FillPolygons([[(0.0, 0.0), (10.0, 0.0), (0.0, 10.0)]], winding: false));
        Assert.AreEqual(55, triangle.Pixels.Count(p => p == 7));

        // FillArc (0,0,20,20) 的圆心在像素中心 (10,10):填出来的像素关于 x = 10、y = 10 对称(原先关于 9.5 对称)。
        PixelBuffer disc = Draw(r => r.FillArc(0, 0, 20, 20, 0, 360 * 64));
        for (int y = 1; y < 20; y++)
        {
            for (int x = 1; x < 20; x++)
            {
                Assert.AreEqual(disc.Get(x, y), disc.Get(20 - x, y), $"({x},{y}) 与左右镜像");
                Assert.AreEqual(disc.Get(x, y), disc.Get(x, 20 - y), $"({x},{y}) 与上下镜像");
            }
        }
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
        XFont fixedFont = FontCatalog.Open("fixed")!;
        Assert.IsFalse(fixedFont.IsTwoByte);
        Assert.AreEqual(11, fixedFont.Ascent);
        Assert.IsNotNull(fixedFont.Lookup('A'));

        XFont unicode = FontCatalog.Open("-misc-fixed-medium-r-semicondensed--13-120-75-75-c-60-iso10646-1")!;
        Assert.IsTrue(unicode.IsTwoByte);
        Assert.IsNotNull(unicode.Lookup(0x2500), "制表符 ─");
        Assert.IsNotNull(FontCatalog.Open("-adobe-helvetica-*"));
        Assert.IsNull(FontCatalog.Open("-b&h-lucida-*"), "Lucida 没有随库带");
        Assert.IsTrue(FontCatalog.WildcardMatch("*-FIXED-*-?3-*", "-misc-fixed-medium-r-normal--13-x"));
        Assert.IsNotEmpty(FontCatalog.Match("*", 1000));
        Assert.AreEqual("cursor", FontCatalog.Open("cursor")!.Name);
        Assert.AreSame(FontCatalog.Open("fixed"), FontCatalog.Open("6x13"), "同一份字体只建一次,整个进程共享");
        Assert.DoesNotContain("kanji16", FontCatalog.Match("*", 10000), "别名的目标没有随库带:不列出来");
    }

    [TestMethod]
    public void BDF解析_字形号编码_带引号的属性_位图按行打包()
    {
        byte[] bdf = Encoding.Latin1.GetBytes("""
            STARTFONT 2.1
            FONT test
            STARTPROPERTIES 2
            COPYRIGHT "These ""glyphs"" are unencumbered"
            FONT_ASCENT 4
            ENDPROPERTIES
            CHARS 2
            STARTCHAR a
            ENCODING 65
            DWIDTH 10 0
            BBX 10 2 -1 -1
            BITMAP
            FFC0
            8040
            ENDCHAR
            STARTCHAR shape
            ENCODING -1 7
            DWIDTH 3 0
            BBX 3 1 0 0
            BITMAP
            FF
            ENDCHAR
            ENDFONT
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        BdfFont font = BdfParser.Parse(bdf);
        Assert.AreEqual(new BdfProperty("These \"glyphs\" are unencumbered", true), font.Properties["COPYRIGHT"]);
        Assert.AreEqual(new BdfProperty("4", false), font.Properties["FONT_ASCENT"], "不带引号的是整数");
        Assert.AreEqual(4, font.FontAscent);
        XGlyph a = font.Glyphs[65];
        Assert.AreEqual(new XCharInfo(-1, 9, 10, 1, 1), a.Info);
        Assert.AreEqual(2, a.Stride);
        Assert.IsTrue(a.IsSet(9, 0));
        Assert.IsTrue(a.IsSet(0, 1) && a.IsSet(9, 1) && !a.IsSet(5, 1));
        XGlyph shape = font.Glyphs[7];   // ENCODING -1 7:第二个数是字形号(cursor 字体这样编号)
        CollectionAssert.AreEqual(new byte[] { 0xE0 }, shape.Bits, "宽 3:宽度以外多出的位清零");
    }

    [TestMethod]
    [DataRow("red", 0xFFFF, 0, 0)]
    [DataRow("Light Gray", 0xD3D3, 0xD3D3, 0xD3D3)]
    [DataRow("gray100", 0xFFFF, 0xFFFF, 0xFFFF)]
    [DataRow("grey0", 0, 0, 0)]
    [DataRow("#f00", 0xF000, 0, 0)]
    [DataRow("#336699", 0x3300, 0x6600, 0x9900)]
    [DataRow("rgb:ff/80/0", 0xFFFF, 0x8080, 0)]
    [DataRow("red3", 0xCDCD, 0, 0)]
    [DataRow("VioletRed4", 0x8B8B, 0x2222, 0x5252)]
    [DataRow("dark slate gray", 0x2F2F, 0x4F4F, 0x4F4F)]
    [DataRow("gray", 0xBEBE, 0xBEBE, 0xBEBE)]
    [DataRow("Blue1", 0, 0, 0xFFFF)]
    public void 颜色名与数值写法(string spec, int r, int g, int b)
    {
        (ushort R, ushort G, ushort B)? rgb = ColorNames.Lookup(spec);
        Assert.IsNotNull(rgb);
        Assert.AreEqual(((ushort)r, (ushort)g, (ushort)b), rgb.Value);
    }

    [TestMethod]
    public void 认不出来的颜色名返回null() => Assert.IsNull(ColorNames.Lookup("not-a-colour"));

    [TestMethod]
    public void 请求读取器只读到请求的长度_池里租来的缓冲后面的旧字节读不到()
    {
        // 池里租来的缓冲比请求长,后面还留着上一条请求的字节(0xAA)。
        byte[] buffer = new byte[64];
        Array.Fill(buffer, (byte)0xAA);
        byte[] request = [7, 0, 3, 0, 1, 0, 0, 0, 2, 0, 0, 0];   // 头 + 两个 CARD32,共 12 字节
        request.CopyTo(buffer, 0);
        XRequestReader r = new(buffer, request.Length, bigEndian: false);
        Assert.AreEqual(12, r.Length);
        Assert.AreEqual(8, r.Remaining);
        Assert.AreEqual(1u, r.U32());
        Assert.AreEqual(2u, r.U32());
        Assert.HasCount(0, r.Rest(), "Rest 不含请求之后的字节");
        XProtocolError error = Assert.ThrowsExactly<XProtocolError>(() => r.U32());
        Assert.AreEqual(XErrorCode.Length, error.Code, "读过请求的长度是 BadLength,不会读到后面的旧字节");
    }

    [TestMethod]
    public void 属性追加共用留了余量的存储_旧版本看到的值不变()
    {
        Windowing.XProperty a = new(31, 8, "ab"u8.ToArray());
        Windowing.XProperty b = a.Append("cd"u8, null);
        Windowing.XProperty c = b.Append("ef"u8, null);
        Windowing.XProperty fork = b.Append("XY"u8, null);   // 从旧版本接着写:不能改到 c
        CollectionAssert.AreEqual("ab"u8.ToArray(), a.Data.ToArray());
        CollectionAssert.AreEqual("abcd"u8.ToArray(), b.Data.ToArray());
        CollectionAssert.AreEqual("abcdef"u8.ToArray(), c.Data.ToArray());
        CollectionAssert.AreEqual("abcdXY"u8.ToArray(), fork.Data.ToArray());
        Assert.AreEqual(31u, fork.Type);
    }
}
