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
        var white = Argb.Gray(1);
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
        var row = new Argb[3];
        g.FetchRow(49, 0, row.AsSpan(0, 2));   // 像素中心 49.5 / 50.5
        Assert.AreEqual(0.495f, row[0].R, 1e-3);
        g.FetchRow(150, 0, row.AsSpan(0, 1));
        Assert.AreEqual(1f, row[0].R, 1e-6, "Pad 越界取端点颜色");
        g.Repeat = RenderSource.RepeatNone;
        g.FetchRow(150, 0, row.AsSpan(0, 1));
        Assert.AreEqual(0f, row[0].A, "None 越界透明");
    }

    [TestMethod]
    public void 多色标渐变按二分找色标_与逐个找的结果相同_相等的色标是硬过渡()
    {
        // 0, 0.1, 0.1, 0.2, ..., 0.9, 1:每一段一种颜色,0.1 处两个色标相等(硬过渡)。
        List<double> stops = [0];
        List<Argb> colors = [new Argb(1, 0, 0, 0)];
        for (int i = 1; i <= 10; i++)
        {
            stops.Add(i / 10.0);
            colors.Add(new Argb(1, i / 10f, 0, 0));
            if (i == 1)
            {
                stops.Add(0.1);
                colors.Add(new Argb(1, 0, 1, 0));
            }
        }
        LinearGradientSource g = new(0, 0, 1000, 0, [.. stops], [.. colors]) { Repeat = RenderSource.RepeatPad };
        var row = new Argb[1000];
        g.FetchRow(0, 0, row);
        for (int x = 0; x < row.Length; x++)
        {
            double t = (x + 0.5) / 1000;
            int i = 1;
            while (stops[i] < t)
            {
                i++;   // 参照:逐个往后找
            }
            double span = stops[i] - stops[i - 1];
            float f = span <= 0 ? 1 : (float)((t - stops[i - 1]) / span);
            float expectedRed = colors[i - 1].R + ((colors[i].R - colors[i - 1].R) * f);
            Assert.AreEqual(expectedRed, row[x].R, 1e-5, $"x = {x}");
        }
        Assert.IsGreaterThan(0.4f, row[150].G, "0.1 处硬过渡到绿色,往后渐变到下一个色标");
        Assert.AreEqual(0f, row[99].G, 1e-6, "硬过渡之前没有绿色");
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
        var row = new Argb[1];
        source.FetchRow(0, 0, row);
        Assert.AreEqual(0.5f, row[0].R, 1e-6);
    }

    [TestMethod]
    public void Argb8的Scale与逐通道Div255一致()
    {
        Random random = new(28);
        for (uint m = 0; m <= 255; m++)
        {
            for (int k = 0; k < 64; k++)
            {
                uint p = (uint)random.NextInt64(0, 1L << 32);
                uint expected = 0;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    expected |= Argb8.Div255(((p >> shift) & 0xFF) * m) << shift;
                }
                Assert.AreEqual(expected, Argb8.Scale(p, m), $"p = 0x{p:X8},m = {m}");
            }
        }
    }

    /// <summary>8 位预乘的随机像素(颜色不超过 alpha)。</summary>
    private static uint RandomPremultiplied(Random random)
    {
        uint a = (uint)random.Next(256);
        return (a << 24) | ((uint)random.Next((int)a + 1) << 16) | ((uint)random.Next((int)a + 1) << 8) | (uint)random.Next((int)a + 1);
    }

    private static PixelBuffer RandomBuffer(Random random, int width, int height, byte depth)
    {
        PixelBuffer buffer = new(width, height, depth);
        for (int i = 0; i < buffer.Pixels.Length; i++)
        {
            buffer.Pixels[i] = RandomPremultiplied(random) & buffer.DepthMask;
        }
        return buffer;
    }

    /// <summary>全部合法的运算:Porter-Duff、Disjoint、Conjoint 与 PDF 混合模式。</summary>
    private static readonly byte[] AllOps = [.. Enumerable.Range(0, 256).Select(i => (byte)i).Where(RenderOps.IsValid)];

    /// <summary>
    /// 带边界值的 8 位预乘像素:一半取自 alpha 为 0 / 1 / 127 / 128 / 254 / 255、颜色为 0 / 等于 alpha / 一半 / 三通道相等的组合,
    /// 一半随机。a8 存 alpha 字节,a1 存随机的一位,24 位深度掩掉 alpha 字节。
    /// </summary>
    private static PixelBuffer EdgeBuffer(Random random, int width, int height, byte depth)
    {
        uint[] alphas = [0, 1, 127, 128, 254, 255];
        PixelBuffer buffer = new(width, height, depth);
        for (int i = 0; i < buffer.Pixels.Length; i++)
        {
            uint p;
            if (random.Next(2) == 0)
            {
                p = RandomPremultiplied(random);
            }
            else
            {
                uint a = alphas[random.Next(alphas.Length)];
                uint[] colors = [0, a, a / 2, (uint)random.Next((int)a + 1)];
                uint gray = colors[random.Next(colors.Length)];
                p = random.Next(3) == 0
                    ? (a << 24) | (gray << 16) | (gray << 8) | gray
                    : (a << 24) | (colors[random.Next(4)] << 16) | (colors[random.Next(4)] << 8) | colors[random.Next(4)];
            }
            // 只有 alpha 的格式存的是 alpha:a8 取 alpha 字节,a1 随机一位。
            buffer.Pixels[i] = depth switch { 8 => p >> 24, 1 => (uint)random.Next(2), _ => p & buffer.DepthMask };
        }
        return buffer;
    }

    /// <summary>同 <see cref="FloatComposite" />,但给编码之前的结果 alpha(浮点);跳过的像素给目标原来的 alpha。</summary>
    private static float[] FloatAlpha(byte op, RenderSource src, RenderSource? mask, PixelBuffer dst, PictFormat format)
    {
        float[] result = new float[dst.Pixels.Length];
        var s = new Argb[dst.Width];
        var m = new Argb[dst.Width];
        for (int y = 0; y < dst.Height; y++)
        {
            src.FetchRow(0, y, s);
            mask?.FetchRow(0, y, m);
            for (int x = 0; x < dst.Width; x++)
            {
                Argb sc = s[x];
                if (mask is not null)
                {
                    float ma = m[x].A;
                    sc = new Argb(sc.A * ma, sc.R * ma, sc.G * ma, sc.B * ma);
                }
                int i = (y * dst.Width) + x;
                Argb d = format.Decode(dst.Pixels[i]);
                result[i] = op == RenderOps.Over && sc.A <= 0 ? d.A : RenderOps.Combine(op, sc, Argb.Gray(sc.A), d).A;
            }
        }
        return result;
    }

    /// <summary>通用路径的浮点算法(源与遮罩取浮点、按 alpha 乘遮罩、RenderOps.Combine、按目标格式编码):整数路径拿它当真值。</summary>
    private static uint[] FloatComposite(byte op, RenderSource src, RenderSource? mask, PixelBuffer dst, PictFormat format)
    {
        uint[] result = (uint[])dst.Pixels.Clone();
        var s = new Argb[dst.Width];
        var m = new Argb[dst.Width];
        for (int y = 0; y < dst.Height; y++)
        {
            src.FetchRow(0, y, s);
            mask?.FetchRow(0, y, m);
            for (int x = 0; x < dst.Width; x++)
            {
                Argb sc = s[x];
                if (mask is not null)
                {
                    float ma = m[x].A;
                    sc = new Argb(sc.A * ma, sc.R * ma, sc.G * ma, sc.B * ma);
                }
                if (op == RenderOps.Over && sc.A <= 0)
                {
                    continue;
                }
                int i = (y * dst.Width) + x;
                result[i] = format.Encode(RenderOps.Combine(op, sc, Argb.Gray(sc.A), format.Decode(result[i]))) & dst.DepthMask;
            }
        }
        return result;
    }

    /// <summary>
    /// 8888 目标上的 Src / Over / Add 走整数:源量化成 8 位、乘遮罩、目标乘 (1 − αs) 各取整一次,
    /// 与全程浮点最多差 2(字形快路径也是这样算的)。渐变、变换、各种 repeat、各种源格式与遮罩都在取样里,一起对一遍。
    /// </summary>
    [TestMethod]
    public void 整数合成与浮点合成只差取整()
    {
        const int size = 40;
        Random random = new(28);
        Argb[] colors = [new(1, 1, 0, 0), new(0.5f, 0, 1, 0), new(0.5f, 0, 0, 1), new(0.2f, 1, 1, 1)];
        double[] stops = [0, 0.4, 0.4, 1];   // 中间一对重合的色标:硬边
        double c = Math.Cos(0.35) * 0.7, sn = Math.Sin(0.35) * 0.7;
        double[] rotate = [c, -sn, 3.25, sn, c, -2.5, 0, 0, 1];
        double[] projective = [1, 0.1, 0, 0, 1, 0, 0.004, 0.002, 1];
        PixelBuffer argbImage = RandomBuffer(random, 17, 13, 32);
        PixelBuffer rgbImage = RandomBuffer(random, 17, 13, 24);
        PixelBuffer r5g6b5Image = RandomBuffer(random, 17, 13, 16);
        PixelBuffer a8Image = RandomBuffer(random, 23, 19, 8);
        byte[] maskBytes = new byte[size * size];
        random.NextBytes(maskBytes);

        List<(string Name, Func<RenderSource> Make)> sources = [];
        foreach (byte repeat in (byte[])[RenderSource.RepeatNone, RenderSource.RepeatNormal, RenderSource.RepeatPad, RenderSource.RepeatReflect])
        {
            sources.Add(($"线性 repeat {repeat}", () => new LinearGradientSource(3, 2, 21, 13, stops, colors) { Repeat = repeat }));
            sources.Add(($"径向 repeat {repeat}", () => new RadialGradientSource(12, 12, 2, 20, 16, 15, stops, colors) { Repeat = repeat }));
            sources.Add(($"像素图旋转双线性 repeat {repeat}",
                () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = repeat, Bilinear = true, Transform = rotate }));
            sources.Add(($"像素图平移 repeat {repeat}", () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = repeat }));
        }
        sources.Add(("锥形", () => new ConicalGradientSource(20, 20, 30, stops, colors)));
        sources.Add(("线性 + 射影变换", () => new LinearGradientSource(0, 0, 40, 0, stops, colors) { Repeat = RenderSource.RepeatReflect, Transform = projective }));
        sources.Add(("x8r8g8b8 旋转最近邻", () => new ImageSource(rgbImage, 0, 0, 17, 13, PictFormat.X8R8G8B8) { Repeat = RenderSource.RepeatNormal, Transform = rotate }));
        sources.Add(("r5g6b5 平铺", () => new ImageSource(r5g6b5Image, 0, 0, 17, 13, PictFormat.R5G6B5) { Repeat = RenderSource.RepeatNormal }));
        sources.Add(("纯色", () => new SolidSource(new Argb(0.6f, 0.3f, 0.5f, 0.1f))));

        List<(string Name, Func<RenderSource?> Make)> masks =
        [
            ("无遮罩", () => null),
            ("单字节遮罩", () => new ByteMaskSource(maskBytes, 0, 0, size, size)),
            ("a8 像素图遮罩双线性", () => new ImageSource(a8Image, 0, 0, 23, 19, PictFormat.A8) { Repeat = RenderSource.RepeatNormal, Bilinear = true, Transform = rotate }),
        ];

        int worst = 0, cases = 0;
        foreach ((PictFormat format, byte depth) in ((PictFormat, byte)[])[(PictFormat.A8R8G8B8, 32), (PictFormat.X8R8G8B8, 24)])
        {
            foreach (byte op in (byte[])[RenderOps.Src, RenderOps.Over, RenderOps.Add])
            {
                foreach ((string sourceName, Func<RenderSource> makeSource) in sources)
                {
                    foreach ((string maskName, Func<RenderSource?> makeMask) in masks)
                    {
                        PixelBuffer dst = RandomBuffer(random, size, size, depth);
                        uint[] expected = FloatComposite(op, makeSource(), makeMask(), dst, format);
                        RenderTarget target = new(dst, 0, 0, format, [new XRect(0, 0, size, size)]);
                        RenderCompositor.Composite(op, makeSource(), makeMask(), false, target, 0, 0, 0, 0, 0, 0, size, size);
                        cases++;
                        for (int i = 0; i < expected.Length; i++)
                        {
                            for (int shift = 0; shift < 32; shift += 8)
                            {
                                int diff = Math.Abs((int)((expected[i] >> shift) & 0xFF) - (int)((dst.Pixels[i] >> shift) & 0xFF));
                                worst = Math.Max(worst, diff);
                                Assert.IsLessThanOrEqualTo(2, diff,
                                    $"{format.Depth} 位目标、op {op}、{sourceName}、{maskName}:像素 {i} 期望 0x{expected[i]:X8},实际 0x{dst.Pixels[i]:X8}");
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine($"{cases} 种组合,最大通道差 {worst}");
    }

    [TestMethod]
    public void a8目标的全部运算走整数_与浮点只差取整()
    {
        const int size = 24;
        Random random = new(81);
        PixelBuffer a8Image = EdgeBuffer(random, 17, 13, 8);
        PixelBuffer argbImage = EdgeBuffer(random, 17, 13, 32);
        byte[] maskBytes = new byte[size * size];
        random.NextBytes(maskBytes);
        maskBytes[0] = 0;
        maskBytes[1] = 255;
        double c = Math.Cos(0.4), sn = Math.Sin(0.4);
        double[] rotate = [c, -sn, 2.5, sn, c, -1.5, 0, 0, 1];
        // 源本身是 8 位像素(或 k / 255 的纯色)时,整数路径与浮点的差只来自因子取整到 8 位与结果取整,最多 1;
        // 双线性的源在浮点版里是没量化的插值,整数路径先量化成 8 位(差半级),再经因子放大,放宽到 2。
        List<(string Name, Func<RenderSource> Make, int Limit)> sources =
        [
            ("a8 平铺", () => new ImageSource(a8Image, 0, 0, 17, 13, PictFormat.A8) { Repeat = RenderSource.RepeatNormal }, 1),
            ("argb 平铺", () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = RenderSource.RepeatNormal }, 1),
            ("argb 旋转双线性", () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = RenderSource.RepeatReflect, Bilinear = true, Transform = rotate }, 2),
            ("纯色", () => new SolidSource(new Argb(102 / 255f, 26 / 255f, 51 / 255f, 77 / 255f)), 1),
        ];
        int worst = 0;
        foreach (byte op in AllOps)
        {
            foreach ((string name, Func<RenderSource> make, int limit) in sources)
            {
                foreach (bool withMask in (bool[])[false, true])
                {
                    PixelBuffer dst = EdgeBuffer(random, size, size, 8);
                    RenderSource? mask = withMask ? new ByteMaskSource(maskBytes, 0, 0, size, size) : null;
                    uint[] expected = FloatComposite(op, make(), mask, dst, PictFormat.A8);
                    RenderTarget target = new(dst, 0, 0, PictFormat.A8, [new XRect(0, 0, size, size)]);
                    RenderCompositor.Composite(op, make(), mask, false, target, 0, 0, 0, 0, 0, 0, size, size);
                    for (int i = 0; i < expected.Length; i++)
                    {
                        int diff = Math.Abs((int)expected[i] - (int)dst.Pixels[i]);
                        worst = Math.Max(worst, diff);
                        Assert.IsLessThanOrEqualTo(limit, diff, $"op 0x{op:X2}、{name}、遮罩 {withMask}:像素 {i} 期望 {expected[i]},实际 {dst.Pixels[i]}");
                    }
                }
            }
        }
        Console.WriteLine($"a8 目标最大差 {worst}");
    }

    /// <summary>
    /// 8888 目标上的 Porter-Duff、Disjoint、Conjoint 全部走整数。8 位像素(或 k / 255 的纯色)的源最多差 1:不带除法的运算差在
    /// 源 × 遮罩与因子各取整到 8 位,带除法的运算(Saturate、Disjoint、Conjoint)因子按 1/65025 算、只剩最后一次取整。
    /// 双线性与渐变的源在浮点版里没量化,整数路径先量化成 8 位(差半级),放宽到 2。
    /// </summary>
    [TestMethod]
    public void a8r8g8b8与x8r8g8b8目标的PorterDuff与Disjoint和Conjoint走整数_与浮点只差取整()
    {
        const int size = 24;
        Random random = new(37);
        PixelBuffer a8Image = EdgeBuffer(random, 17, 13, 8);
        PixelBuffer argbImage = EdgeBuffer(random, 17, 13, 32);
        PixelBuffer rgbImage = EdgeBuffer(random, 17, 13, 24);
        PixelBuffer a8Mask = EdgeBuffer(random, size, size, 8);
        byte[] maskBytes = new byte[size * size];
        random.NextBytes(maskBytes);
        maskBytes[0] = 0;
        maskBytes[1] = 255;
        double c = Math.Cos(0.4), sn = Math.Sin(0.4);
        double[] rotate = [c, -sn, 2.5, sn, c, -1.5, 0, 0, 1];
        Argb[] colors = [new(1, 1, 0, 0), new(0.5f, 0, 0.5f, 0), new(0, 0, 0, 0), new(0.2f, 1, 1, 1)];
        List<(string Name, Func<RenderSource> Make, int Limit)> sources =
        [
            ("纯色半透明", () => new SolidSource(new Argb(128 / 255f, 64 / 255f, 0, 128 / 255f)), 1),
            ("纯色不透明", () => new SolidSource(new Argb(1, 10 / 255f, 128 / 255f, 1)), 1),
            ("x8r8g8b8 图像", () => new ImageSource(rgbImage, 0, 0, 17, 13, PictFormat.X8R8G8B8) { Repeat = RenderSource.RepeatNormal }, 1),
            ("argb 图像", () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = RenderSource.RepeatNormal }, 1),
            ("a8 图像", () => new ImageSource(a8Image, 0, 0, 17, 13, PictFormat.A8) { Repeat = RenderSource.RepeatReflect }, 1),
            ("argb 旋转双线性", () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = RenderSource.RepeatReflect, Bilinear = true, Transform = rotate }, 2),
            ("线性渐变", () => new LinearGradientSource(3, 2, 21, 13, [0, 0.4, 0.4, 1], colors) { Repeat = RenderSource.RepeatReflect }, 2),
        ];
        List<(string Name, Func<RenderSource?> Make)> masks =
        [
            ("无遮罩", () => null),
            ("单字节遮罩", () => new ByteMaskSource(maskBytes, 0, 0, size, size)),
            ("a8 像素图遮罩", () => new ImageSource(a8Mask, 0, 0, size, size, PictFormat.A8)),
        ];
        int worst = 0;
        foreach ((PictFormat format, byte depth) in ((PictFormat, byte)[])[(PictFormat.A8R8G8B8, 32), (PictFormat.X8R8G8B8, 24)])
        {
            foreach (byte op in AllOps.Where(o => o < 0x30))
            {
                foreach ((string sourceName, Func<RenderSource> makeSource, int limit) in sources)
                {
                    foreach ((string maskName, Func<RenderSource?> makeMask) in masks)
                    {
                        PixelBuffer dst = EdgeBuffer(random, size, size, depth);
                        uint[] expected = FloatComposite(op, makeSource(), makeMask(), dst, format);
                        RenderTarget target = new(dst, 0, 0, format, [new XRect(0, 0, size, size)]);
                        RenderCompositor.Composite(op, makeSource(), makeMask(), false, target, 0, 0, 0, 0, 0, 0, size, size);
                        for (int i = 0; i < expected.Length; i++)
                        {
                            int diff = MaxChannelDiff(expected[i], dst.Pixels[i]);
                            worst = Math.Max(worst, diff);
                            Assert.IsLessThanOrEqualTo(limit, diff,
                                $"{format.Depth} 位目标、op 0x{op:X2}、{sourceName}、{maskName}:像素 {i} 期望 0x{expected[i]:X8},实际 0x{dst.Pixels[i]:X8}");
                        }
                    }
                }
            }
        }
        Console.WriteLine($"8888 目标 Porter-Duff / Disjoint / Conjoint 最大通道差 {worst}");
    }

    /// <summary>两个 0xAARRGGBB 四个通道里最大的差。</summary>
    private static int MaxChannelDiff(uint a, uint b)
    {
        int worst = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            worst = Math.Max(worst, Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)));
        }
        return worst;
    }

    /// <summary>
    /// a1 目标:全部运算走整数,只算 alpha、按 ≥ 0.5 取 1(同浮点版的编码)。源与遮罩都是 8 位像素时与浮点版逐位相同 ——
    /// a1 的 alpha 只有 0 / 1,各因子不是 0、1 就是源 alpha 或 1 − 源 alpha,而阈值 0.5 = 127.5 / 255 正好落在 8 位取整的分界上。
    /// 双线性的源在浮点版里没量化:整数路径先把它量化成 8 位,alpha 至多挪半级(乘上遮罩只会更小),所以只允许在浮点结果离 0.5
    /// 不到半级的像素上不同。
    /// </summary>
    [TestMethod]
    public void a1目标的全部运算走整数_与浮点逐位相同()
    {
        const int size = 24;
        Random random = new(19);
        PixelBuffer a8Image = EdgeBuffer(random, 17, 13, 8);
        PixelBuffer argbImage = EdgeBuffer(random, 17, 13, 32);
        PixelBuffer rgbImage = EdgeBuffer(random, 17, 13, 24);
        PixelBuffer a8Mask = EdgeBuffer(random, size, size, 8);
        byte[] maskBytes = new byte[size * size];
        random.NextBytes(maskBytes);
        maskBytes[0] = 0;
        maskBytes[1] = 255;
        maskBytes[2] = 128;
        double c = Math.Cos(0.4), sn = Math.Sin(0.4);
        double[] rotate = [c, -sn, 2.5, sn, c, -1.5, 0, 0, 1];
        List<(string Name, Func<RenderSource> Make, bool Exact)> sources =
        [
            ("纯色半透明", () => new SolidSource(new Argb(128 / 255f, 64 / 255f, 0, 128 / 255f)), true),
            ("纯色 alpha 127", () => new SolidSource(new Argb(127 / 255f, 127 / 255f, 127 / 255f, 127 / 255f)), true),
            ("纯色不透明", () => new SolidSource(new Argb(1, 10 / 255f, 128 / 255f, 1)), true),
            ("x8r8g8b8 图像", () => new ImageSource(rgbImage, 0, 0, 17, 13, PictFormat.X8R8G8B8) { Repeat = RenderSource.RepeatNormal }, true),
            ("argb 图像", () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = RenderSource.RepeatNormal }, true),
            ("a8 图像", () => new ImageSource(a8Image, 0, 0, 17, 13, PictFormat.A8) { Repeat = RenderSource.RepeatReflect }, true),
            ("argb 旋转双线性", () => new ImageSource(argbImage, 0, 0, 17, 13, PictFormat.A8R8G8B8) { Repeat = RenderSource.RepeatReflect, Bilinear = true, Transform = rotate }, false),
        ];
        List<(string Name, Func<RenderSource?> Make)> masks =
        [
            ("无遮罩", () => null),
            ("单字节遮罩", () => new ByteMaskSource(maskBytes, 0, 0, size, size)),
            ("a8 像素图遮罩", () => new ImageSource(a8Mask, 0, 0, size, size, PictFormat.A8)),
        ];
        int nearThreshold = 0;
        foreach (byte op in AllOps)
        {
            foreach ((string sourceName, Func<RenderSource> makeSource, bool exact) in sources)
            {
                foreach ((string maskName, Func<RenderSource?> makeMask) in masks)
                {
                    PixelBuffer dst = EdgeBuffer(random, size, size, 1);
                    uint[] expected = FloatComposite(op, makeSource(), makeMask(), dst, PictFormat.A1);
                    float[] alpha = FloatAlpha(op, makeSource(), makeMask(), dst, PictFormat.A1);
                    RenderTarget target = new(dst, 0, 0, PictFormat.A1, [new XRect(0, 0, size, size)]);
                    RenderCompositor.Composite(op, makeSource(), makeMask(), false, target, 0, 0, 0, 0, 0, 0, size, size);
                    for (int i = 0; i < expected.Length; i++)
                    {
                        if (expected[i] == dst.Pixels[i])
                        {
                            continue;
                        }
                        string what = $"op 0x{op:X2}、{sourceName}、{maskName}:像素 {i} 期望 {expected[i]},实际 {dst.Pixels[i]}(浮点 alpha {alpha[i]})";
                        Assert.IsFalse(exact, what);
                        Assert.IsLessThanOrEqualTo(0.5 / 255 + 1e-6, Math.Abs(alpha[i] - 0.5), what);
                        nearThreshold++;
                    }
                }
            }
        }
        Console.WriteLine($"a1 目标:双线性源在阈值附近不同的像素 {nearThreshold} 个");
    }

    [TestMethod]
    public void 源与目标同缓冲时只拷变换后读得到的那一块_取样结果不变()
    {
        // 窗口的 picture 至多 256 MB:原先源带变换或重复时每条请求整张拷一遍。现在按四个角变换后的外接矩形估算,
        // 拷出来的源在要读的范围里逐像素与原来相同。
        Random random = new(9);
        PixelBuffer image = RandomBuffer(random, 300, 200, 32);
        double c = Math.Cos(0.3) * 1.3, sn = Math.Sin(0.3) * 1.3;
        double[][] transforms =
        [
            [2, 0, 7.5, 0, 2, -3.25, 0, 0, 1],                 // 缩小一半(矩阵把目标坐标映回源坐标)
            [0.5, 0, 0, 0, 0.5, 0, 0, 0, 1],                   // 放大两倍
            [c, -sn, 40, sn, c, 10, 0, 0, 1],                  // 旋转
            [1, 0.1, 0, 0, 1, 0, 0.002, 0.001, 1],             // 投影
            [1, 0, 0, 0, 1, 0, 0.01, 0, -1],                   // 齐次坐标在矩形里变号
        ];
        for (int round = 0; round < 400; round++)
        {
            ImageSource source = new(image, 0, 0, 300, 200, PictFormat.A8R8G8B8)
            {
                Repeat = (byte)random.Next(4),
                Bilinear = random.Next(2) == 0,
                Transform = random.Next(5) == 0 ? null : transforms[random.Next(transforms.Length)],
            };
            XRect needed = new(random.Next(-50, 320), random.Next(-50, 220), random.Next(1, 40), random.Next(1, 40));
            ImageSource detached = source.Detach(needed);
            uint[] expected = new uint[needed.Width], actual = new uint[needed.Width];
            Argb[] expectedF = new Argb[needed.Width], actualF = new Argb[needed.Width];
            for (int y = needed.Y; y < needed.Bottom; y++)
            {
                source.FetchRow8888(needed.X, y, expected);
                detached.FetchRow8888(needed.X, y, actual);
                CollectionAssert.AreEqual(expected, actual, $"第 {round} 组 {needed} 第 {y} 行(repeat {source.Repeat}、双线性 {source.Bilinear})");
                source.FetchRow(needed.X, y, expectedF);
                detached.FetchRow(needed.X, y, actualF);
                CollectionAssert.AreEqual(expectedF, actualF, $"第 {round} 组 {needed} 第 {y} 行(浮点)");
            }
        }

        // 放大两倍、重复平铺,读 20×20:要拷的只有 10×10 左右,而不是整张 300×200。
        ImageSource tiled = new(image, 0, 0, 300, 200, PictFormat.A8R8G8B8) { Repeat = RenderSource.RepeatNormal, Bilinear = true, Transform = transforms[1] };
        PixelBuffer copy = tiled.Detach(new XRect(40, 40, 20, 20)).Buffer;
        Assert.IsLessThanOrEqualTo(14 * 14, copy.Width * copy.Height, $"拷了 {copy.Width}×{copy.Height}");
    }
}
