using VelaShell.XServer.Drawing;

namespace VelaShell.XServer.Tests.Drawing;

/// <summary>
/// RENDER 快路径的向量版(xs_plan F24)与标量版逐位一致:随机的源、目标、遮罩(含 0、255 这些捷径值,含 alpha 为 0 而颜色不为 0 的源),
/// 目标有 / 没有 alpha 两种。标量参照照抄 RenderCompositor 的 OverSolidMask / BlitImage 里的式子。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RenderSimdTests
{
    private static uint ScalarOverImage(uint s, uint d, bool dstAlpha)
    {
        uint sa = s >> 24;
        if (sa == 0)
        {
            return d;
        }
        if (sa == 255)
        {
            return dstAlpha ? s : s & 0x00FFFFFF;
        }
        uint inv = 255 - sa;
        uint oa = dstAlpha ? sa + Argb8.Div255((d >> 24) * inv) : 0;
        uint or = ((s >> 16) & 0xFF) + Argb8.Div255(((d >> 16) & 0xFF) * inv);
        uint og = ((s >> 8) & 0xFF) + Argb8.Div255(((d >> 8) & 0xFF) * inv);
        uint ob = (s & 0xFF) + Argb8.Div255((d & 0xFF) * inv);
        return (oa << 24) | (Math.Min(or, 255) << 16) | (Math.Min(og, 255) << 8) | Math.Min(ob, 255);
    }

    private static uint ScalarOverSolidMask(uint color, byte m, uint d, bool dstAlpha)
    {
        uint sa = color >> 24, sr = (color >> 16) & 0xFF, sg = (color >> 8) & 0xFF, sb = color & 0xFF;
        if (m == 0)
        {
            return d;
        }
        if (m == 255 && sa == 255)
        {
            return (dstAlpha ? 0xFF000000u : 0) | (sr << 16) | (sg << 8) | sb;
        }
        uint a = Argb8.Div255(sa * m), inv = 255 - a;
        uint oa = dstAlpha ? a + Argb8.Div255((d >> 24) * inv) : 0;
        uint or = Argb8.Div255(sr * m) + Argb8.Div255(((d >> 16) & 0xFF) * inv);
        uint og = Argb8.Div255(sg * m) + Argb8.Div255(((d >> 8) & 0xFF) * inv);
        uint ob = Argb8.Div255(sb * m) + Argb8.Div255((d & 0xFF) * inv);
        return (Math.Min(oa, 255) << 24) | (Math.Min(or, 255) << 16) | (Math.Min(og, 255) << 8) | Math.Min(ob, 255);
    }

    /// <summary>照抄 RenderCompositor.CombineRow 的 Over / Add(带遮罩、depthMask)。</summary>
    private static uint ScalarCombine(bool over, uint s, uint? maskPixel, uint d, bool dstAlpha, uint depthMask)
    {
        uint keep = dstAlpha ? 0xFFFFFFFFu : 0x00FFFFFFu;
        if (maskPixel is { } mp)
        {
            uint m = mp >> 24;
            s = m == 255 ? s : m == 0 ? 0 : Argb8.Scale(s, m);
        }
        uint sa = s >> 24;
        if (over)
        {
            if (sa == 0)
            {
                return d;
            }
            if (sa == 255)
            {
                return s & keep & depthMask;
            }
            uint inv = 255 - sa;
            uint oa = dstAlpha ? sa + Argb8.Div255((d >> 24) * inv) : 0;
            uint or = ((s >> 16) & 0xFF) + Argb8.Div255(((d >> 16) & 0xFF) * inv);
            uint og = ((s >> 8) & 0xFF) + Argb8.Div255(((d >> 8) & 0xFF) * inv);
            uint ob = (s & 0xFF) + Argb8.Div255((d & 0xFF) * inv);
            return ((Math.Min(oa, 255) << 24) | (Math.Min(or, 255) << 16) | (Math.Min(og, 255) << 8) | Math.Min(ob, 255)) & depthMask;
        }
        uint aa = dstAlpha ? Math.Min(sa + (d >> 24), 255) : 0;
        uint ar = Math.Min(((s >> 16) & 0xFF) + ((d >> 16) & 0xFF), 255);
        uint ag = Math.Min(((s >> 8) & 0xFF) + ((d >> 8) & 0xFF), 255);
        uint ab = Math.Min((s & 0xFF) + (d & 0xFF), 255);
        return ((aa << 24) | (ar << 16) | (ag << 8) | ab) & depthMask;
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, true)]
    [DataRow(false, false, false)]
    public void 通用路径的Over与Add向量版与标量版逐位一致(bool over, bool withMask, bool dstAlpha)
    {
        if (!RenderSimd.Enabled)
        {
            return;
        }
        Random random = new(over ? 11 : 22);
        foreach (uint depthMask in new[] { 0xFFFFFFFFu, 0x00FFFFFFu })
        {
            for (int round = 0; round < 100; round++)
            {
                int n = random.Next(0, 70);
                uint[] source = [.. Enumerable.Range(0, n).Select(_ => RandomPixel(random))];
                uint[] mask = withMask ? [.. Enumerable.Range(0, n).Select(_ => RandomPixel(random))] : [];
                uint[] destination = [.. Enumerable.Range(0, n).Select(_ => RandomPixel(random))];
                uint keep = (dstAlpha ? 0xFFFFFFFFu : 0x00FFFFFFu) & depthMask;
                uint[] expected = [.. Enumerable.Range(0, n).Select(i =>
                    ScalarCombine(over, source[i], withMask ? mask[i] : null, destination[i], dstAlpha, depthMask))];
                int done = over ? RenderSimd.OverRow(source, mask, destination, keep) : RenderSimd.AddRow(source, mask, destination, keep);
                Assert.AreEqual(n & ~3, done);
                CollectionAssert.AreEqual(expected[..done], destination[..done], $"第 {round} 轮");
            }
        }
    }

    private static uint RandomPixel(Random random) => random.Next(8) switch
    {
        0 => (uint)random.Next(0x1000000),                                // alpha 0(颜色不一定是 0)
        1 => 0xFF000000u | (uint)random.Next(0x1000000),                  // 不透明
        _ => (uint)random.NextInt64(0x100000000),
    };

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void 图像Over的向量版与标量版逐位一致(bool dstAlpha)
    {
        if (!RenderSimd.Enabled)
        {
            return;
        }
        Random random = new(1234);
        for (int round = 0; round < 200; round++)
        {
            int n = random.Next(0, 70);
            uint[] source = [.. Enumerable.Range(0, n).Select(_ => RandomPixel(random))];
            uint[] destination = [.. Enumerable.Range(0, n).Select(_ => RandomPixel(random))];
            uint[] expected = [.. source.Zip(destination, (s, d) => ScalarOverImage(s, d, dstAlpha))];
            int done = RenderSimd.OverImageRow(source, destination, dstAlpha);
            Assert.AreEqual(n & ~3, done);
            CollectionAssert.AreEqual(expected[..done], destination[..done], $"第 {round} 轮");
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void 纯色过遮罩Over的向量版与标量版逐位一致(bool dstAlpha)
    {
        if (!RenderSimd.Enabled)
        {
            return;
        }
        Random random = new(5678);
        for (int round = 0; round < 200; round++)
        {
            int n = random.Next(0, 70);
            uint color = RandomPixel(random);
            byte[] mask = [.. Enumerable.Range(0, n).Select(_ => random.Next(4) switch { 0 => (byte)0, 1 => (byte)255, _ => (byte)random.Next(256) })];
            if (round % 10 == 0)
            {
                Array.Clear(mask);   // 四个都为 0 的整块跳过
            }
            uint[] destination = [.. Enumerable.Range(0, n).Select(_ => RandomPixel(random))];
            uint[] expected = [.. mask.Zip(destination, (m, d) => ScalarOverSolidMask(color, m, d, dstAlpha))];
            int done = RenderSimd.OverSolidMaskRow(color, mask, destination, dstAlpha);
            Assert.AreEqual(n & ~3, done);
            CollectionAssert.AreEqual(expected[..done], destination[..done], $"第 {round} 轮,颜色 {color:X8}");
        }
    }
}
