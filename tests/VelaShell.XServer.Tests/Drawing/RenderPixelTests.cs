using VelaShell.XServer.Drawing;

namespace VelaShell.XServer.Tests.Drawing;

/// <summary>RENDER 的纯像素部分:格式编解码、合成运算、渐变、覆盖率。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RenderPixelTests
{
    private static Argb Combine(byte op, Argb s, Argb d) => RenderOps.Combine(op, s, Argb.Gray(s.A), d);

    [TestMethod]
    public void 格式编解码往返且缺的通道按规则补()
    {
        Argb c = PictFormat.A8R8G8B8.Decode(0x80402010);
        Assert.AreEqual(0x80402010u, PictFormat.A8R8G8B8.Encode(c));
        Assert.AreEqual(1f, PictFormat.X8R8G8B8.Decode(0x00FF0000).A, "没有 alpha 通道的格式 alpha 是 1");
        Assert.AreEqual(0f, PictFormat.A8.Decode(0xFF).R, "只有 alpha 的格式颜色是 0");
        Assert.AreEqual(0xF800u, PictFormat.R5G6B5.Encode(new Argb(1, 1, 0, 0)));
        Assert.AreEqual(1u, PictFormat.A1.Encode(Argb.Gray(0.6f)));
    }

    [TestMethod]
    public void Over与Src与Add的基本结果()
    {
        Argb halfRed = new(0.5f, 0.5f, 0, 0);
        Argb white = Argb.Gray(1);
        Argb over = Combine(RenderOps.Over, halfRed, white);
        Assert.AreEqual(1f, over.A, 1e-6);
        Assert.AreEqual(1f, over.R, 1e-6);
        Assert.AreEqual(0.5f, over.G, 1e-6);

        Assert.AreEqual(0.5f, Combine(RenderOps.Src, halfRed, white).A, 1e-6);
        Assert.AreEqual(1f, Combine(RenderOps.Add, halfRed, white).R, 1e-6, "Add 饱和到 1");
        Assert.AreEqual(0f, Combine(RenderOps.Clear, halfRed, white).A);
    }

    [TestMethod]
    public void Disjoint与Conjoint的Over在极端alpha下与PorterDuff一致()
    {
        Argb opaque = new(1, 0.2f, 0.4f, 0.6f);
        Argb dst = new(1, 1, 1, 1);
        foreach (byte op in (byte[])[0x13, 0x23])
        {
            Argb r = Combine(op, opaque, dst);
            Assert.AreEqual(0.2f, r.R, 1e-6, $"op 0x{op:x}:不透明源盖住目标");
        }
        Argb transparent = default;
        Argb kept = Combine(0x23, transparent, dst);
        Assert.AreEqual(1f, kept.R, 1e-6, "透明源 Conjoint Over 不改目标");
    }

    [TestMethod]
    public void 混合模式Multiply与Screen()
    {
        Argb gray = new(1, 0.5f, 0.5f, 0.5f);
        Argb dst = new(1, 0.5f, 1f, 0f);
        Argb m = Combine(0x30, gray, dst);
        Assert.AreEqual(0.25f, m.R, 1e-6);
        Assert.AreEqual(0.5f, m.G, 1e-6);
        Argb s = Combine(0x31, gray, dst);
        Assert.AreEqual(0.75f, s.R, 1e-6);
        Assert.AreEqual(1f, s.G, 1e-6);
    }

    [TestMethod]
    public void 线性渐变的中点与Pad和None()
    {
        LinearGradientSource g = new(0, 0, 100, 0, [0, 1], [new Argb(1, 0, 0, 0), new Argb(1, 1, 1, 1)]) { Repeat = RenderSource.RepeatPad };
        Argb[] row = new Argb[3];
        g.FetchRow(49, 0, row.AsSpan(0, 2));   // 像素中心 49.5 / 50.5
        Assert.AreEqual(0.495f, row[0].R, 1e-3);
        g.FetchRow(150, 0, row.AsSpan(0, 1));
        Assert.AreEqual(1f, row[0].R, 1e-6, "Pad 越界取端点颜色");
        g.Repeat = RenderSource.RepeatNone;
        g.FetchRow(150, 0, row.AsSpan(0, 1));
        Assert.AreEqual(0f, row[0].A, "None 越界透明");
    }

    [TestMethod]
    public void 梯形覆盖率在半像素边上是一半()
    {
        CoverageMask mask = new(new XRect(0, 0, 4, 2));
        // 竖直边在 x = 1.5 与 3,覆盖 0 ≤ y < 2。
        mask.AddTrapezoid(0, 2, new CoverageMask.Line(1.5, 0, 1.5, 2), new CoverageMask.Line(3, 0, 3, 2));
        Assert.AreEqual(0f, mask.Alpha[0], 1e-6);
        Assert.AreEqual(0.5f, mask.Alpha[1], 1e-6);
        Assert.AreEqual(1f, mask.Alpha[2], 1e-6);
        Assert.AreEqual(0f, mask.Alpha[3], 1e-6);
    }

    [TestMethod]
    public void 三角形面积守恒()
    {
        CoverageMask mask = new(new XRect(0, 0, 10, 10));
        mask.AddTriangle((0, 0), (8, 0), (0, 8));
        Assert.AreEqual(32f, mask.Alpha.Sum(), 0.2f);
    }

    [TestMethod]
    public void 变换加双线性取到相邻像素的平均()
    {
        PixelBuffer buffer = new(2, 1, 32);
        buffer.Pixels[0] = 0xFF000000;
        buffer.Pixels[1] = 0xFFFFFFFF;
        ImageSource source = new(buffer, 0, 0, 2, 1, PictFormat.A8R8G8B8)
        {
            Repeat = RenderSource.RepeatPad,
            Bilinear = true,
            Transform = [1, 0, 0.5, 0, 1, 0, 0, 0, 1],   // 向右平移半个像素
        };
        Argb[] row = new Argb[1];
        source.FetchRow(0, 0, row);
        Assert.AreEqual(0.5f, row[0].R, 1e-6);
    }
}
