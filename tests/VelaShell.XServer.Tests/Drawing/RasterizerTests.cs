using VelaShell.XServer.Drawing;
using VelaShell.XServer.Resources;

namespace VelaShell.XServer.Tests.Drawing;

/// <summary>
/// 光栅化先与裁剪求交:画出来的像素必须与「从头走到尾、逐像素判裁剪」完全一样 —— 用随机的线与裁剪区域对拍一个照搬原算法的参照实现。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RasterizerTests
{
    private const int Size = 64;

    /// <summary>参照:原先的写法 —— 从起点逐步走到终点,每一步都问一次裁剪(虚线的走位逐像素推进)。</summary>
    private static void ReferenceLine(PixelBuffer buffer, Region clip, int x1, int y1, int x2, int y2, bool drawLast,
        byte[]? dashes, ref int dashIndex, ref int dashRemaining)
    {
        int dx = Math.Abs(x2 - x1), sx = x1 < x2 ? 1 : -1;
        int dy = -Math.Abs(y2 - y1), sy = y1 < y2 ? 1 : -1;
        int err = dx + dy;
        int x = x1, y = y1;
        while (true)
        {
            bool last = x == x2 && y == y2;
            if (!last || drawLast)
            {
                bool on = dashes is null || dashIndex % 2 == 0;
                if (on && clip.Contains(x, y))
                {
                    buffer.Pixels[(y * buffer.Width) + x] ^= 0xFFFFFF;   // GXxor:画两次会互相抵消,能看出重复画
                }
                if (dashes is not null)
                {
                    dashRemaining--;
                    if (dashRemaining <= 0)
                    {
                        dashIndex = (dashIndex + 1) % dashes.Length;
                        dashRemaining = dashes[dashIndex];
                    }
                }
            }
            if (last)
            {
                break;
            }
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private static Region RandomClip(Random random)
    {
        Region clip = new();
        int rects = random.Next(1, 4);
        for (int i = 0; i < rects; i++)
        {
            int x = random.Next(0, Size), y = random.Next(0, Size);
            clip.Union(new XRect(x, y, random.Next(1, Size - x + 1), random.Next(1, Size - y + 1)));
        }
        return clip;
    }

    private static int RandomCoordinate(Random random) => random.Next(4) switch
    {
        0 => random.Next(-3000, 3000),        // 大多在窗口外
        1 => random.Next(-32768, 32768),     // 坐标的整个范围
        _ => random.Next(-20, Size + 20),     // 窗口附近
    };

    [TestMethod]
    public void 细线只走与裁剪相交的一段_像素与逐步走完全一致()
    {
        Random random = new(20261006);
        for (int round = 0; round < 3000; round++)
        {
            Region clip = RandomClip(random);
            int x1 = RandomCoordinate(random), y1 = RandomCoordinate(random), x2 = RandomCoordinate(random), y2 = RandomCoordinate(random);
            bool drawLast = random.Next(2) == 0;

            PixelBuffer actual = new(Size, Size, 24);
            XGc gc = new(1, null, 24) { Foreground = 0xFFFFFF, Function = 6 };
            new Rasterizer(actual, 0, 0, clip, gc).ThinLine(x1, y1, x2, y2, drawLast);

            PixelBuffer expected = new(Size, Size, 24);
            int index = 0, remaining = 0;
            ReferenceLine(expected, clip, x1, y1, x2, y2, drawLast, null, ref index, ref remaining);
            CollectionAssert.AreEqual(expected.Pixels, actual.Pixels, $"第 {round} 条:({x1},{y1})–({x2},{y2}) drawLast={drawLast}");
        }
    }

    [TestMethod]
    public void 虚线跨段接着走_跳过的部分也推进图案()
    {
        Random random = new(7);
        byte[] dashes = [3, 2, 5];
        for (int round = 0; round < 500; round++)
        {
            Region clip = RandomClip(random);
            XGc gc = new(1, null, 24) { Foreground = 0xFFFFFF, Function = 6, LineStyle = 1, Dashes = dashes, DashOffset = (ushort)random.Next(10) };
            PixelBuffer actual = new(Size, Size, 24);
            Rasterizer raster = new(actual, 0, 0, clip, gc);
            Rasterizer.DashState dash = raster.NewDashState();

            PixelBuffer expected = new(Size, Size, 24);
            Rasterizer.DashState reference = new Rasterizer(new PixelBuffer(1, 1, 24), 0, 0, new Region(), gc).NewDashState();
            int index = reference.Index, remaining = reference.Remaining;

            int x = RandomCoordinate(random), y = RandomCoordinate(random);
            for (int segment = 0; segment < 4; segment++)
            {
                int nx = RandomCoordinate(random), ny = RandomCoordinate(random);
                bool drawLast = segment == 3;
                raster.ThinLine(x, y, nx, ny, drawLast, dash);
                ReferenceLine(expected, clip, x, y, nx, ny, drawLast, dashes, ref index, ref remaining);
                (x, y) = (nx, ny);
            }
            CollectionAssert.AreEqual(expected.Pixels, actual.Pixels, $"第 {round} 组");
        }
    }

    [TestMethod]
    public void 填充矩形先与裁剪求交()
    {
        PixelBuffer buffer = new(10, 10, 24);
        XGc gc = new(1, null, 24) { Foreground = 5 };
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(3, -32768, 2, 65535);
        Assert.AreEqual(20, buffer.Pixels.Count(p => p == 5), "整列 2 × 10 个像素");
    }

    [TestMethod]
    public void 平铺填充按行整段拷_与逐像素取模的结果相同()
    {
        Random random = new(5);
        for (int round = 0; round < 300; round++)
        {
            PixelBuffer tileBuffer = new(random.Next(1, 9), random.Next(1, 9), 24);
            for (int i = 0; i < tileBuffer.Pixels.Length; i++)
            {
                tileBuffer.Pixels[i] = (uint)random.Next() & 0xFFFFFF;
            }
            XGc gc = new(1, null, 24)
            {
                FillStyle = 1,
                Tile = new XPixmap(2, null, tileBuffer),
                TileStipXOrigin = (short)random.Next(-20, 20),
                TileStipYOrigin = (short)random.Next(-20, 20),
            };
            Region clip = RandomClip(random);
            int ox = random.Next(-5, 5), oy = random.Next(-5, 5);
            PixelBuffer actual = new(Size, Size, 24);
            new Rasterizer(actual, ox, oy, clip, gc).FillRect(random.Next(-10, Size), random.Next(-10, Size), random.Next(1, 60), random.Next(1, 60));

            // 参照:每个画到的像素都应当是平铺图在 (x − 原点) 取模处的值。
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    uint p = actual.Get(x, y);
                    if (p == 0)
                    {
                        continue;
                    }
                    int dx = x - ox, dy = y - oy;
                    uint expected = tileBuffer.Get(Mod(dx - gc.TileStipXOrigin, tileBuffer.Width), Mod(dy - gc.TileStipYOrigin, tileBuffer.Height));
                    Assert.AreEqual(expected, p, $"第 {round} 组 ({x},{y})");
                }
            }
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;
    }

    [TestMethod]
    public void 缓冲改尺寸就地挪行时与新建数组的结果相同_新露出的部分按填充值()
    {
        Random random = new(11);
        PixelBuffer buffer = new(40, 30, 24);
        for (int i = 0; i < buffer.Pixels.Length; i++)
        {
            buffer.Pixels[i] = (uint)i;
        }
        uint[,] expected = Snapshot(buffer);
        int allocations = 0;
        for (int round = 0; round < 400; round++)
        {
            int width = random.Next(1, 70), height = random.Next(1, 50);
            uint fill = random.Next(3) == 0 ? (uint)random.Next() : 0;
            uint[] before = buffer.Pixels;
            buffer.Resize(width, height, fill);
            allocations += ReferenceEquals(before, buffer.Pixels) ? 0 : 1;
            uint[,] next = new uint[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    next[x, y] = x < expected.GetLength(0) && y < expected.GetLength(1) ? expected[x, y] : fill;
                }
            }
            expected = next;
            CollectionAssert.AreEqual(expected, Snapshot(buffer), $"第 {round} 次:{width}×{height}");
            // 偶尔画一笔,让内容不只是初始值。
            buffer.Pixels[random.Next(width * height)] = (uint)round;
            expected = Snapshot(buffer);
        }
        Assert.IsLessThan(400, allocations, "放得下时就地挪,不必每次都新建数组");

        static uint[,] Snapshot(PixelBuffer b)
        {
            uint[,] s = new uint[b.Width, b.Height];
            for (int y = 0; y < b.Height; y++)
            {
                for (int x = 0; x < b.Width; x++)
                {
                    s[x, y] = b.Get(x, y);
                }
            }
            return s;
        }
    }

    [TestMethod]
    public void GC裁剪区域按平移量缓存_换了裁剪矩形或原点就重建()
    {
        XGc gc = new(1, null, 24) { Foreground = 1, ClipRects = [new XRect(0, 0, 2, 2)] };
        PixelBuffer buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual(4, buffer.Pixels.Count(p => p == 1));

        gc.ClipXOrigin = 5;   // 原点变了:裁剪区域跟着挪
        buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual((1u, 0u), (buffer.Get(5, 0), buffer.Get(0, 0)));

        gc.ClipRects = [new XRect(0, 0, 3, 1)];   // 整份换掉:缓存作废
        buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual(3, buffer.Pixels.Count(p => p == 1));

        gc.ClipRects = null;   // 不裁剪:可见区域原样用
        buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(new XRect(-5, -5, 30, 30)), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual(100, buffer.Pixels.Count(p => p == 1), "可见区域伸出缓冲时仍与缓冲求交");
    }

    // ------------------------------------------------------------------ 宽线与弧的 line-style / join-style / cap-style

    private static PixelBuffer Stroke(XGc gc, Action<Rasterizer> draw, int size = Size)
    {
        PixelBuffer buffer = new(size, size, 24);
        draw(new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc));
        return buffer;
    }

    [TestMethod]
    public void 宽线的OnOffDash只画偶数段_DoubleDash的奇数段用背景色()
    {
        // lw = 3 的水平线 y = 10:第 9..11 行;虚线 [4, 4] 从 x = 2 量起 —— 偶数段 [2, 6)、[10, 14),奇数段 [6, 10)。
        XGc onOff = new(1, null, 24) { Foreground = 1, LineWidth = 3, LineStyle = 1, Dashes = [4, 4] };
        PixelBuffer a = Stroke(onOff, r => r.PolyLine([(2, 10), (42, 10)]));
        Assert.AreEqual((1u, 0u, 1u), (a.Get(3, 10), a.Get(7, 10), a.Get(11, 9)), "偶数段画、奇数段不画");

        XGc doubleDash = new(1, null, 24) { Foreground = 1, Background = 2, LineWidth = 3, LineStyle = 2, Dashes = [4, 4] };
        PixelBuffer b = Stroke(doubleDash, r => r.PolyLine([(2, 10), (42, 10)]));
        Assert.AreEqual((1u, 2u, 1u), (b.Get(3, 10), b.Get(7, 11), b.Get(11, 10)), "奇数段按背景色画");
    }

    [TestMethod]
    public void 宽线DoubleDash的像素与Solid完全相同且每个只画一次()
    {
        // 协议:DoubleDash 两种段合起来的像素集合与 Solid 相同。GXxor、前景背景都是全 1:画两次的像素会被抵消。
        Random random = new(42);
        for (int round = 0; round < 200; round++)
        {
            List<(int X, int Y)> points = [.. Enumerable.Range(0, random.Next(2, 6)).Select(_ => (random.Next(-10, Size + 10), random.Next(-10, Size + 10)))];
            bool closed = random.Next(3) == 0;
            if (closed)
            {
                points.Add(points[0]);
            }
            XGc gc = new(1, null, 24)
            {
                Foreground = 0xFFFFFF,
                Background = 0xFFFFFF,
                Function = 6,
                LineWidth = (ushort)random.Next(1, 12),
                JoinStyle = (byte)random.Next(3),
                CapStyle = (byte)random.Next(4),
                Dashes = [(byte)random.Next(1, 9), (byte)random.Next(1, 9), (byte)random.Next(1, 9)],
                DashOffset = (ushort)random.Next(20),
            };
            PixelBuffer solid = Stroke(gc, r => r.PolyLine(points, closed));
            gc.LineStyle = 2;
            PixelBuffer dashed = Stroke(gc, r => r.PolyLine(points, closed));
            CollectionAssert.AreEqual(solid.Pixels, dashed.Pixels, $"第 {round} 条:{string.Join(" ", points)} closed={closed} lw={gc.LineWidth}");
        }
    }

    [TestMethod]
    public void 宽线的接头按join_style_Miter是尖角_Bevel切掉_Round是圆()
    {
        // lw = 6 的矩形边框 (10,10)–(30,30):外沿在 7。角上的像素 (7,7) 只有 Miter 盖得住;
        // Bevel 的斜边是 x + y = 17,(8,8) 在外、(9,9) 在内;Round 的圆心 (10,10)、半径 3,(8,8) 在内。
        (int X, int Y)[] rect = [(10, 10), (30, 10), (30, 30), (10, 30), (10, 10)];
        PixelBuffer miter = Stroke(new XGc(1, null, 24) { Foreground = 1, LineWidth = 6, JoinStyle = 0 }, r => r.PolyLine(rect, closed: true));
        PixelBuffer round = Stroke(new XGc(1, null, 24) { Foreground = 1, LineWidth = 6, JoinStyle = 1 }, r => r.PolyLine(rect, closed: true));
        PixelBuffer bevel = Stroke(new XGc(1, null, 24) { Foreground = 1, LineWidth = 6, JoinStyle = 2 }, r => r.PolyLine(rect, closed: true));
        Assert.AreEqual((1u, 1u, 1u, 1u), (miter.Get(7, 7), miter.Get(32, 7), miter.Get(7, 32), miter.Get(32, 32)), "四个角都是方的");
        Assert.AreEqual((0u, 1u), (round.Get(7, 7), round.Get(8, 8)));
        Assert.AreEqual((0u, 0u, 1u), (bevel.Get(7, 7), bevel.Get(8, 8), bevel.Get(9, 9)));
        Assert.AreEqual(26 * 26 - (14 * 14), miter.Pixels.Count(p => p == 1), "Miter 的边框正好是两个方块之差");
    }

    [TestMethod]
    public void 端点重合的宽线按端帽画_Projecting是方块_Round是圆_Butt什么都不画()
    {
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 4, CapStyle = 3 };
        PixelBuffer projecting = Stroke(gc, r => r.PolyLine([(20, 20), (20, 20)]));
        Assert.AreEqual(16, projecting.Pixels.Count(p => p == 1), "与坐标轴对齐、边长为线宽的方块");
        Assert.AreEqual((1u, 1u, 0u), (projecting.Get(18, 18), projecting.Get(21, 21), projecting.Get(22, 20)));
        gc.CapStyle = 2;
        Assert.IsGreaterThan(8, Stroke(gc, r => r.PolyLine([(20, 20), (20, 20)])).Pixels.Count(p => p == 1), "直径为线宽的圆");
        gc.CapStyle = 1;
        Assert.AreEqual(0, Stroke(gc, r => r.PolyLine([(20, 20), (20, 20)])).Pixels.Count(p => p == 1));
    }

    [TestMethod]
    public void 宽弧两端按cap_style加端帽()
    {
        // 圆心 (30,30)、半径 20 的弧从 0° 逆时针到 90°:起点 (50,30) 处沿弧往上走,端帽朝下伸。
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 6, CapStyle = 1 };
        Assert.AreEqual(0u, Stroke(gc, r => r.Arc(10, 10, 40, 40, 0, 90 * 64)).Get(50, 32), "Butt:端面齐着起点");
        gc.CapStyle = 3;
        PixelBuffer projecting = Stroke(gc, r => r.Arc(10, 10, 40, 40, 0, 90 * 64));
        Assert.AreEqual((1u, 0u), (projecting.Get(50, 32), projecting.Get(50, 33)), "Projecting:往外伸半个线宽");
        Assert.AreEqual((1u, 0u), (projecting.Get(28, 10), projecting.Get(26, 10)), "终点 (30,10) 处朝左伸");
        gc.CapStyle = 2;
        Assert.AreEqual(1u, Stroke(gc, r => r.Arc(10, 10, 40, 40, 0, 90 * 64)).Get(50, 32), "Round:半圆");
    }

    [TestMethod]
    public void 弧也按line_style画虚线()
    {
        XGc gc = new(1, null, 24) { Foreground = 1 };
        int solidThin = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        gc.LineStyle = 1;
        gc.Dashes = [3, 3];
        int dashedThin = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        Assert.IsTrue(dashedThin > solidThin / 3 && dashedThin < solidThin * 2 / 3, $"细弧:实线 {solidThin}、虚线 {dashedThin}");

        gc.LineStyle = 0;
        gc.LineWidth = 4;
        int solidWide = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        gc.LineStyle = 1;
        gc.Dashes = [6, 6];
        int dashedWide = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        Assert.IsTrue(dashedWide > solidWide / 3 && dashedWide < solidWide * 2 / 3, $"宽弧:实线 {solidWide}、虚线 {dashedWide}");

        // DoubleDash 的宽弧:两种段合起来就是实线。
        gc.LineStyle = 2;
        gc.Background = 2;
        PixelBuffer doubleDash = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64));
        Assert.AreEqual(solidWide, doubleDash.Pixels.Count(p => p != 0));
        Assert.IsGreaterThan(solidWide / 3, doubleDash.Pixels.Count(p => p == 2), "奇数段用背景色");
    }

    [TestMethod]
    public void 宽弧的形状只取决于宽高与线宽_挪一个像素就整体挪一个像素()
    {
        // lw = 1 时外框原先按 Math.Round(x − 0.5) 取整(银行家舍入):x = 4 与 x = 5 都取到 4,两条弧画在同一处。
        foreach (ushort lw in new ushort[] { 1, 3, 4 })
        {
            XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = lw };
            PixelBuffer at4 = Stroke(gc, r => r.Arc(4, 4, 21, 15, 0, 360 * 64));
            PixelBuffer at5 = Stroke(gc, r => r.Arc(5, 4, 21, 15, 0, 360 * 64));
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size - 1; x++)
                {
                    Assert.AreEqual(at4.Get(x, y), at5.Get(x + 1, y), $"lw = {lw}:({x},{y})");
                }
            }
        }
    }

    // ------------------------------------------------------------------ PolyArc:首尾相接的弧

    /// <summary>圆心 (cx, cy)、半径 r 的圆上从 start° 起、跨 extent° 的弧(逆时针为正)。</summary>
    private static (int X, int Y, int W, int H, int A1, int A2) CircleArc(int cx, int cy, int r, int start, int extent) =>
        (cx - r, cy - r, 2 * r, 2 * r, start * 64, extent * 64);

    // 一个拐角:A 是圆心 (30,50) 的圆从 180° 顺时针 90°,终点 (30,30) 处朝右走;B 是圆心 (50,30) 的圆从 180° 逆时针 90°,
    // 从 (30,30) 朝下走 —— 与折线 (10,30) → (30,30) → (30,50) 在 (30,30) 处的转角相同。
    private static readonly (int X, int Y, int W, int H, int A1, int A2) CornerA = CircleArc(30, 50, 20, 180, -90);
    private static readonly (int X, int Y, int W, int H, int A1, int A2) CornerB = CircleArc(50, 30, 20, 180, 90);

    /// <summary>C:圆心 (30,50) 的下半圆,从 (50,50)(B 的终点)顺时针到 (10,50)(A 的起点)。B → C → A 首尾相接。</summary>
    private static readonly (int X, int Y, int W, int H, int A1, int A2) LowerHalf = CircleArc(30, 50, 20, 0, -180);

    /// <summary>G:圆心 (50,30) 的圆从 0° 起逆时针 90°,起点 (70,30) 谁都不接。</summary>
    private static readonly (int X, int Y, int W, int H, int A1, int A2) Loose = CircleArc(50, 30, 20, 0, 90);

    /// <summary>
    /// 拐角外侧 R = [31,40) × [20,30) 里画了的像素数:A 的环带在 x ≤ 30、B 的在 y ≥ 30,都伸不到那里,只有接头(或端帽)画得到。
    /// lw = 7 时接头的边都落在半像素上,不碰像素中心。
    /// </summary>
    private static int OutsideCorner(PixelBuffer buffer)
    {
        int count = 0;
        for (int y = 20; y < 30; y++)
        {
            for (int x = 31; x < 40; x++)
            {
                count += buffer.Get(x, y) != 0 ? 1 : 0;
            }
        }
        return count;
    }

    /// <summary>每条弧单独画(相当于各自一串:都加端帽、虚线各自从 dash-offset 开始)。</summary>
    private static void EachAlone(Rasterizer raster, params (int X, int Y, int W, int H, int A1, int A2)[] arcs)
    {
        foreach ((int X, int Y, int W, int H, int A1, int A2) arc in arcs)
        {
            raster.PolyArc([arc]);
        }
    }

    [TestMethod]
    public void 相接的两条宽弧按join_style接_外侧与同样转角的折线接头相同_不再各自加端帽()
    {
        // 外侧 R 里:Miter 是 3 × 3 的方块,Round 是半径 3.5 的圆的一角,Bevel 是斜边 x − y = 3.5 下面的三角。
        int[] expected = [9, 6, 3];
        for (byte join = 0; join < 3; join++)
        {
            XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 7, JoinStyle = join, CapStyle = 1 };
            PixelBuffer joined = Stroke(gc, r => r.PolyArc([CornerA, CornerB]));
            PixelBuffer line = Stroke(gc, r => r.PolyLine([(10, 30), (30, 30), (30, 50)]));
            Assert.AreEqual(expected[join], OutsideCorner(joined), $"join-style {join}");
            for (int y = 20; y < 30; y++)
            {
                for (int x = 31; x < 40; x++)
                {
                    Assert.AreEqual(line.Get(x, y), joined.Get(x, y), $"join-style {join}:({x},{y}) 与折线的接头不同");
                }
            }
            Assert.AreEqual(0, OutsideCorner(Stroke(gc, r => EachAlone(r, CornerA, CornerB))), "各自加 Butt 端帽时外侧什么都没有");
        }

        // 两个 Projecting 端帽在接点上合起来正好是 Miter 的方块;相接之后接点上不加端帽,Bevel 只剩三角。
        XGc projecting = new(1, null, 24) { Foreground = 1, LineWidth = 7, JoinStyle = 2, CapStyle = 3 };
        Assert.AreEqual(3, OutsideCorner(Stroke(projecting, r => r.PolyArc([CornerA, CornerB]))));
        Assert.AreEqual(9, OutsideCorner(Stroke(projecting, r => EachAlone(r, CornerA, CornerB))));
    }

    [TestMethod]
    public void 首尾相接的一串宽弧是闭合路径_哪里都不加端帽()
    {
        // B → C → A:A 的终点 (30,30) 又回到 B 的起点,那里按 Miter 接(外侧 R 里 9 个像素);cap-style 换成什么像素都不变。
        PixelBuffer? first = null;
        foreach (byte cap in new byte[] { 1, 2, 3 })
        {
            XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 7, JoinStyle = 0, CapStyle = cap };
            PixelBuffer closed = Stroke(gc, r => r.PolyArc([CornerB, LowerHalf, CornerA]), size: 80);
            Assert.AreEqual(9, OutsideCorner(closed), $"cap-style {cap}:接缝处是 Miter 接头");
            first ??= closed;
            CollectionAssert.AreEqual(first.Pixels, closed.Pixels, $"cap-style {cap}");
        }

        // 同一个椭圆的四段 90° 弧拼成整圈:与一条 360° 的弧逐个像素相同,没有端帽(lw = 20 时四个接点上的 Projecting 端帽会伸出环带),
        // 0° 处水平的端面上那一排像素也不丢(分开算的两段端面差几个末位,那一排像素中心会两边都不算)。
        (int X, int Y, int W, int H, int A1, int A2)[] quarters = [.. Enumerable.Range(0, 4).Select(k => (10, 10, 50, 30, k * 90 * 64, 90 * 64))];
        XGc wide = new(1, null, 24) { Foreground = 1, LineWidth = 20, CapStyle = 3 };
        PixelBuffer whole = Stroke(wide, r => r.PolyArc([(10, 10, 50, 30, 0, 360 * 64)]), size: 80);
        CollectionAssert.AreEqual(whole.Pixels, Stroke(wide, r => r.PolyArc(quarters), size: 80).Pixels, "四段拼成的整圈");
        Assert.AreEqual((0u, 1u), (whole.Get(69, 16), Stroke(wide, r => EachAlone(r, quarters), size: 80).Get(69, 16)), "各自加端帽时 0° 处伸出 Projecting 端帽");
    }

    [TestMethod]
    public void 相接的弧虚线接着走_不相接的从dash_offset重新开始()
    {
        foreach (ushort lw in new ushort[] { 0, 4 })
        {
            // 拐角 A → B:两条弧各长约 31 个像素。虚线 [40, 100]:接着走时第一个偶数段盖住整条 A、再伸进 B 九个像素左右,
            // B 的后半截落在奇数段里;B 若从 dash-offset 重新开始,整条 B 都在第一个偶数段里。
            XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = lw, LineStyle = 1, Dashes = [40, 100], CapStyle = 1 };
            PixelBuffer joined = Stroke(gc, r => r.PolyArc([CornerA, CornerB]));
            PixelBuffer reset = Stroke(gc, r => EachAlone(r, CornerA, CornerB));
            Assert.IsGreaterThan(0, Count(joined, 28, 31, 34, 36), $"lw = {lw}:B 的开头还在第一个偶数段里");
            Assert.AreEqual(0, Count(joined, 40, 45, 49, 53), $"lw = {lw}:B 的后半截落在奇数段里");
            Assert.IsGreaterThan(0, Count(reset, 40, 45, 49, 53), $"lw = {lw}:各自从 dash-offset 开始时 B 全程都画");

            // 圆心 (30,30) 的圆上 0° → 90° 与 90° → 180° 两段:同一个椭圆上接着走的两段,与一条 0° → 180° 的弧逐个像素相同。
            gc.Dashes = [5, 3];
            gc.DashOffset = 2;
            gc.CapStyle = 2;
            gc.Function = 6;
            (int X, int Y, int W, int H, int A1, int A2) first = CircleArc(30, 30, 20, 0, 90);
            PixelBuffer halves = Stroke(gc, r => r.PolyArc([first, CircleArc(30, 30, 20, 90, 90)]));
            CollectionAssert.AreEqual(Stroke(gc, r => r.PolyArc([CircleArc(30, 30, 20, 0, 180)])).Pixels, halves.Pixels, $"lw = {lw}:同一个圆上的两段");

            // 第二段往下挪一个像素就不相接了:各画各的,第二段从 dash-offset 重新开始。
            (int X, int Y, int W, int H, int A1, int A2) moved = CircleArc(30, 31, 20, 90, 90);
            CollectionAssert.AreEqual(Stroke(gc, r => EachAlone(r, first, moved)).Pixels, Stroke(gc, r => r.PolyArc([first, moved])).Pixels,
                $"lw = {lw}:不相接");
        }

        // [x0, x1) × [y0, y1) 里画了的像素数。
        static int Count(PixelBuffer b, int x0, int y0, int x1, int y1) =>
            Enumerable.Range(y0, y1 - y0).Sum(y => Enumerable.Range(x0, x1 - x0).Count(x => b.Get(x, y) != 0));
    }

    [TestMethod]
    public void 相接的弧整串一次填_GXxor下接缝不画两次()
    {
        // GXxor、前景背景都是全 1:画两次的像素会被抵消,与 GXcopy 的结果逐个比就看得出来。
        (int X, int Y, int W, int H, int A1, int A2)[][] chains =
        [
            [CornerA, CornerB],
            [CornerB, LowerHalf, CornerA],
            [.. Enumerable.Range(0, 4).Select(k => (10, 10, 41, 25, k * 90 * 64, 90 * 64))],
        ];
        foreach ((int X, int Y, int W, int H, int A1, int A2)[] chain in chains)
        {
            foreach (ushort lw in new ushort[] { 0, 3, 8 })
            {
                for (int style = 0; style < 4 * 3 * 2; style++)
                {
                    XGc gc = new(1, null, 24)
                    {
                        Foreground = 0xFFFFFF,
                        Background = 0xFFFFFF,
                        Function = 3,
                        LineWidth = lw,
                        CapStyle = (byte)(style % 4),
                        JoinStyle = (byte)(style / 4 % 3),
                        LineStyle = (byte)(style / 12 * 2),
                        Dashes = [7, 4],
                    };
                    PixelBuffer copy = Stroke(gc, r => r.PolyArc(chain), size: 80);
                    gc.Function = 6;
                    PixelBuffer xor = Stroke(gc, r => r.PolyArc(chain), size: 80);
                    CollectionAssert.AreEqual(copy.Pixels, xor.Pixels,
                        $"{chain.Length} 条弧,lw = {lw},cap {gc.CapStyle},join {gc.JoinStyle},line-style {gc.LineStyle}");
                }
            }
        }
    }

    [TestMethod]
    public void 随机的一串相接宽弧_DoubleDash与Solid像素相同且每个只画一次()
    {
        Random random = new(64);
        for (int round = 0; round < 300; round++)
        {
            // 从一个整点出发:每条弧取整数半径的圆,起角是 0° / 90° / 180° / 270° 之一、跨 90° 的倍数 —— 端点都在整点上,一条接一条。
            List<(int X, int Y, int W, int H, int A1, int A2)> chain = [];
            (int x, int y) = (random.Next(10, Size - 10), random.Next(10, Size - 10));
            for (int k = random.Next(2, 6); k > 0; k--)
            {
                int radius = random.Next(1, 16), start = random.Next(4) * 90, extent = random.Next(1, 5) * 90 * (random.Next(2) == 0 ? 1 : -1);
                (int dx, int dy) = Offset(start, radius);
                (int cx, int cy) = (x - dx, y - dy);
                chain.Add(CircleArc(cx, cy, radius, start, extent));
                (dx, dy) = Offset(start + extent, radius);
                (x, y) = (cx + dx, cy + dy);
            }
            XGc gc = new(1, null, 24)
            {
                Foreground = 0xFFFFFF,
                Background = 0xFFFFFF,
                Function = 6,
                LineWidth = (ushort)random.Next(1, 12),
                JoinStyle = (byte)random.Next(3),
                CapStyle = (byte)random.Next(4),
                Dashes = [(byte)random.Next(1, 9), (byte)random.Next(1, 9)],
                DashOffset = (ushort)random.Next(20),
            };
            string what = $"第 {round} 串:{string.Join(" ", chain)} lw={gc.LineWidth} join={gc.JoinStyle} cap={gc.CapStyle}";
            PixelBuffer solid = Stroke(gc, r => r.PolyArc(chain));
            gc.Function = 3;
            CollectionAssert.AreEqual(Stroke(gc, r => r.PolyArc(chain)).Pixels, solid.Pixels, $"{what}:GXxor 下有像素画了两次");
            gc.Function = 6;
            gc.LineStyle = 2;
            CollectionAssert.AreEqual(solid.Pixels, Stroke(gc, r => r.PolyArc(chain)).Pixels, $"{what}:DoubleDash 与 Solid 不同");
        }

        // 圆上角度 a(0° / 90° / 180° / 270°)处的点相对圆心的偏移(y 朝下)。
        static (int Dx, int Dy) Offset(int angle, int radius) => (((angle % 360) + 360) % 360) switch
        {
            0 => (radius, 0),
            90 => (0, -radius),
            180 => (-radius, 0),
            _ => (0, radius),
        };
    }

    [TestMethod]
    public void 拐角接点上外侧半个端面的像素都画到()
    {
        // A:圆心 (61,24)、半径 10,从 180° 逆时针 270° 到顶点 (61,14),在那里朝左走;B:圆心 (73,14)、半径 12,从 (61,14) 起朝下走。
        // A 的终点端面是竖线 x = 61,外侧那一半 (61,6)–(61,14) 一边是 A 的环带、一边是 Bevel 的三角;按浮点算的 cos(π/2) 不是 0,
        // 两边若各按差几个末位的端面取像素,压在线上的像素中心会两边都不算。
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 16, JoinStyle = 2, CapStyle = 1 };
        PixelBuffer corner = Stroke(gc, r => r.PolyArc([(51, 14, 20, 20, 180 * 64, 270 * 64), (61, 2, 24, 24, 180 * 64, 90 * 64)]), size: 80);
        for (int y = 7; y < 14; y++)
        {
            Assert.AreEqual(1u, corner.Get(61, y), $"(61,{y})");
        }

        // 随机的拐角:两个整数半径的圆在 0° / 90° / 180° / 270° 处相接,A 终点端面上外侧那一半里压在线上的像素都要画到。
        Random random = new(99);
        for (int round = 0; round < 3000; round++)
        {
            (int cx, int cy, int radius, int start, int extent) a = (random.Next(25, 55), random.Next(25, 55), random.Next(3, 25), random.Next(4) * 90, 0);
            a.extent = random.Next(1, 4) * 90 * (random.Next(2) == 0 ? 1 : -1);
            (int px, int py) = (a.cx + RoundCos(a.start + a.extent, a.radius), a.cy - RoundSin(a.start + a.extent, a.radius));
            (int radius, int start, int extent) b = (random.Next(3, 25), random.Next(4) * 90, random.Next(1, 4) * 90 * (random.Next(2) == 0 ? 1 : -1));
            (int bx, int by) = (px - RoundCos(b.start, b.radius), py + RoundSin(b.start, b.radius));
            int lw = random.Next(2, 2 * Math.Min(a.radius, b.radius));
            gc = new(1, null, 24) { Foreground = 1, LineWidth = (ushort)lw, JoinStyle = (byte)random.Next(3), CapStyle = 1 };
            PixelBuffer joined = Stroke(gc, r => r.PolyArc([CircleArc(a.cx, a.cy, a.radius, a.start, a.extent), CircleArc(bx, by, b.radius, b.start, b.extent)]), size: 80);

            // 切向:逆时针的弧在角 t 处朝 (−sin t, −cos t)(y 朝下),顺时针的反过来。
            (int ux, int uy) = (-RoundSin(a.start + a.extent, 1) * Math.Sign(a.extent), -RoundCos(a.start + a.extent, 1) * Math.Sign(a.extent));
            (int vx, int vy) = (-RoundSin(b.start, 1) * Math.Sign(b.extent), -RoundCos(b.start, 1) * Math.Sign(b.extent));
            int cross = (ux * vy) - (uy * vx);
            if (cross == 0)
            {
                continue;   // 切线连续或折返:没有外侧
            }
            int side = cross < 0 ? 1 : -1;
            (int nx, int ny) = (-uy * side, ux * side);
            for (int s = 1; s < lw / 2.0 - (gc.JoinStyle == 1 ? 0.6 : 0); s++)
            {
                (int x, int y) = (px + (nx * s), py + (ny * s));
                if (x is >= 0 and < 80 && y is >= 0 and < 80)
                {
                    Assert.AreEqual(1u, joined.Get(x, y), $"第 {round} 组:A {a} B {b} lw={lw} join={gc.JoinStyle}:({x},{y})");
                }
            }
        }

        static int RoundCos(int degrees, int r) => (((degrees % 360) + 360) % 360) switch { 0 => r, 180 => -r, _ => 0 };
        static int RoundSin(int degrees, int r) => (((degrees % 360) + 360) % 360) switch { 90 => r, 270 => -r, _ => 0 };
    }

    [TestMethod]
    public void 端点相差不到半个像素也算相接_接头照样补上()
    {
        // A:圆心 (20,40)、半径 20,从 135° 顺时针到 45°,终点 (34.14,25.86) 处朝右下走;B:外接框 (28,20,40,40),圆心 (48,40),
        // 从 135° 顺时针到 45°,起点 (33.86,25.86) 与 A 的终点差 0.28 个像素,朝右上走。V 字的底在接点上,Miter 的尖朝下。
        (int X, int Y, int W, int H, int A1, int A2) a = CircleArc(20, 40, 20, 135, -90), b = CircleArc(48, 40, 20, 135, -90);
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 8, JoinStyle = 0, CapStyle = 1 };
        PixelBuffer joined = Stroke(gc, r => r.PolyArc([a, b]), size: 80);
        PixelBuffer apart = Stroke(gc, r => EachAlone(r, a, b), size: 80);
        Assert.AreEqual((1u, 0u), (joined.Get(34, 29), apart.Get(34, 29)), "接点正下方只有 Miter 接头画得到");
        foreach (uint p in apart.Pixels.Select((v, i) => v == 0 ? 1u : joined.Pixels[i]))
        {
            Assert.AreEqual(1u, p, "两条环带本身的像素都在");
        }

        // 圆端帽各自画时在接点上叠在一起;相接之后整串一次填,GXxor 下不抵消。
        XGc xor = new(1, null, 24) { Foreground = 0xFFFFFF, Function = 6, LineWidth = 8, JoinStyle = 1, CapStyle = 2 };
        PixelBuffer once = Stroke(xor, r => r.PolyArc([a, b]), size: 80);
        xor.Function = 3;
        CollectionAssert.AreEqual(Stroke(xor, r => r.PolyArc([a, b]), size: 80).Pixels, once.Pixels);
    }

    [TestMethod]
    public void 不相接的弧照旧各自加端帽_半个像素之差不算重合()
    {
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 7, JoinStyle = 2, CapStyle = 3 };

        // [A, B, G]:A 接 B(外侧只有 Bevel 的三角),G 的起点 (70,30) 谁都不接 —— 它照旧自己加端帽,与单独画一样。
        PixelBuffer mixed = Stroke(gc, r => r.PolyArc([CornerA, CornerB, Loose]), size: 80);
        Assert.AreEqual(3, OutsideCorner(mixed), "A 与 B 相接");
        Assert.AreEqual(1u, mixed.Get(70, 32), "G 起点的 Projecting 端帽(G 从 (70,30) 往上走,端帽朝下伸)");
        CollectionAssert.AreEqual(Stroke(gc, r => { r.PolyArc([CornerA, CornerB]); r.PolyArc([Loose]); }, size: 80).Pixels, mixed.Pixels);

        // E 的外接框 (30,10,40,41):圆心 (50,30.5),起点 (30,30.5) 与 A 的终点 (30,30) 正好差半个像素 —— 不算重合,各自加端帽。
        (int X, int Y, int W, int H, int A1, int A2) e = (30, 10, 40, 41, 180 * 64, 90 * 64);
        PixelBuffer halfApart = Stroke(gc, r => r.PolyArc([CornerA, e]), size: 80);
        Assert.AreEqual(9, OutsideCorner(halfApart), "A 终点的 Projecting 端帽");
        CollectionAssert.AreEqual(Stroke(gc, r => EachAlone(r, CornerA, e), size: 80).Pixels, halfApart.Pixels);
    }

    [TestMethod]
    public void 最后一条弧的终点与第一条的起点重合时也相接()
    {
        // [B, G, A]:B 与 G、G 与 A 都不相接,但 A 的终点就是 B 的起点 —— A 接 B,拐角处是 Bevel 接头,而不是两个 Projecting 端帽。
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 7, JoinStyle = 2, CapStyle = 3 };
        PixelBuffer wrapped = Stroke(gc, r => r.PolyArc([CornerB, Loose, CornerA]), size: 80);
        Assert.AreEqual(3, OutsideCorner(wrapped));
        CollectionAssert.AreEqual(Stroke(gc, r => { r.PolyArc([CornerA, CornerB]); r.PolyArc([Loose]); }, size: 80).Pixels, wrapped.Pixels);
    }

    [TestMethod]
    public void 宽或高为0的细弧连成虚线_与同一条折线逐像素相同()
    {
        // 宽为 0 的弧从 (10,50) 走到 (10,10),高为 0 的弧再从 (10,10) 走到 (50,10):两条扁弧就是这条折线,
        // 虚线的相位要跨过接点接着走(扁弧在 ArcStroke 里另走一支)。
        XGc thin = new(1, null, 24) { Foreground = 1, LineStyle = 1, Dashes = [7, 5], DashOffset = 3 };
        CollectionAssert.AreEqual(
            Stroke(thin, r => r.PolyLine([(10, 50), (10, 10), (50, 10)])).Pixels,
            Stroke(thin, r => r.PolyArc([(10, 10, 0, 40, -90 * 64, 180 * 64), (10, 10, 40, 0, 180 * 64, -180 * 64)])).Pixels);
    }
}
