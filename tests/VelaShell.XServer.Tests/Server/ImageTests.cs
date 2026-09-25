using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// PutImage 的各种格式:只解、只贴目标上画得到的那一块 —— 左 / 上被裁掉时,图像里的偏移必须算对;
/// 以及宿主读像素的 <see cref="XTopLevelWindow.ReadPixels" />。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class ImageTests
{
    private static async Task<(uint Window, XTopLevelWindow Handle)> MapWindowAsync(XTestClient c, RecordingHost host, int width = 64, int height = 48)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16((ushort)width).U16((ushort)height)
            .U16(0).U16(1).U32(0).U32(0x2).U32(0x000000));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return (id, host.Mapped[id]);
    }

    private static async Task<uint> CreateGcAsync(XTestClient c, uint drawable, uint foreground = 0, uint background = 0)
    {
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(drawable).U32(0x4 | 0x8).U32(foreground).U32(background));
        return gc;
    }

    [TestMethod]
    public async Task ZPixmap32位_左下被裁掉_只贴看得见的部分且丢掉高8位()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);
        uint gc = await CreateGcAsync(c, window);

        // 10×4 的图像贴在 (-3, 46):窗口里只看得见图像的第 3–9 列、第 0–1 行。
        const int width = 10, height = 4;
        byte[] data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BitConverter.TryWriteBytes(data.AsSpan(((y * width) + x) * 4), 0xAA000000u | (uint)(x << 8) | (uint)y | 0x100000u);
            }
        }
        await c.SendAsync(72, 2, b => b.U32(window).U32(gc).U16(width).U16(height).I16(-3).I16(46).U8(0).U8(24).U16(0).Bytes(data));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x100300u, px[(46 * w) + 0], "窗口 (0,46) = 图像 (3,0),高 8 位被深度掩掉");
        Assert.AreEqual(0x100901u, px[(47 * w) + 6], "窗口 (6,47) = 图像 (9,1)");
        Assert.AreEqual(0u, px[(46 * w) + 7], "图像只盖到 x = 6");
        Assert.AreEqual(0u, px[(45 * w) + 0], "图像从 y = 46 开始");
    }

    [TestMethod]
    public async Task Bitmap格式带左补_左边被裁掉时按图像列取位()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);
        uint gc = await CreateGcAsync(c, window, foreground: 0xFF0000, background: 0x00FF00);

        // 12×3、左补 5 位:每行 17 位 → 补齐到 4 字节。图像第 x 列置位当且仅当 x % 3 == 0。
        const int width = 12, height = 3, leftPad = 5;
        byte[] data = new byte[4 * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x += 3)
            {
                int bit = x + leftPad;
                data[(y * 4) + (bit >> 3)] |= (byte)(1 << (bit & 7));
            }
        }
        await c.SendAsync(72, 0, b => b.U32(window).U32(gc).U16(width).U16(height).I16(-4).I16(10).U8(leftPad).U8(1).U16(0).Bytes(data));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x00FF00u, px[(10 * w) + 0], "窗口 x=0 是图像第 4 列:没置位 → 背景");
        Assert.AreEqual(0xFF0000u, px[(11 * w) + 2], "窗口 x=2 是图像第 6 列:置位 → 前景");
        Assert.AreEqual(0xFF0000u, px[(12 * w) + 5], "窗口 x=5 是图像第 9 列");
        Assert.AreEqual(0u, px[(12 * w) + 8], "图像只盖到 x = 7");
    }

    [TestMethod]
    public async Task XYPixmap逐平面_左边被裁掉()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);
        uint gc = await CreateGcAsync(c, window);

        // 3×2、深度 24:24 个平面,高位在前,每个平面每行 4 字节。
        const int width = 3, height = 2, depth = 24, stride = 4;
        uint[] image = [0x111111, 0x123456, 0x222222, 0x333333, 0x444444, 0xABCDEF];
        byte[] data = new byte[stride * height * depth];
        for (int plane = 0; plane < depth; plane++)
        {
            uint bit = 1u << (depth - 1 - plane);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if ((image[(y * width) + x] & bit) != 0)
                    {
                        data[(plane * stride * height) + (y * stride) + (x >> 3)] |= (byte)(1 << (x & 7));
                    }
                }
            }
        }
        await c.SendAsync(72, 1, b => b.U32(window).U32(gc).U16(width).U16(height).I16(-1).I16(0).U8(0).U8(depth).U16(0).Bytes(data));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x123456u, px[0], "窗口 (0,0) = 图像 (1,0)");
        Assert.AreEqual(0xABCDEFu, px[w + 1], "窗口 (1,1) = 图像 (2,1)");
        Assert.AreEqual(0u, px[2], "图像只盖到 x = 1");
    }

    [TestMethod]
    public async Task ZPixmap16位贴进像素图_右边超出的部分丢掉_GetImage读回()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint pixmap = c.NewId();
        await c.SendAsync(53, 16, b => b.U32(pixmap).U32(c.RootWindow).U16(8).U16(2));
        uint gc = await CreateGcAsync(c, pixmap);

        // 5×2、16 位每像素(每行 10 字节补到 12),贴在 x = 5:只有前 3 列落在 8 宽的像素图里。
        const int width = 5, height = 2, stride = 12;
        byte[] data = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BitConverter.TryWriteBytes(data.AsSpan((y * stride) + (x * 2)), (ushort)(0x1000 + x + (y * 16)));
            }
        }
        await c.SendAsync(72, 2, b => b.U32(pixmap).U32(gc).U16(width).U16(height).I16(5).I16(0).U8(0).U8(16).U16(0).Bytes(data));
        XMessage image = await c.RequestAsync(73, 2, b => b.U32(pixmap).I16(0).I16(0).U16(8).U16(2).U32(0xFFFFFFFF));

        Assert.IsTrue(image.IsReply);
        Assert.AreEqual(16, image.Detail, "深度");
        ushort At(int x, int y) => BitConverter.ToUInt16(image.Bytes, 32 + (y * 16) + (x * 2));
        Assert.AreEqual(0x1000, At(5, 0));
        Assert.AreEqual(0x1000 + 2 + 16, At(7, 1));
        Assert.AreEqual(0, At(4, 0), "贴图从 x = 5 开始");
    }

    [TestMethod]
    public async Task PutImage格式与深度不配时回BadMatch_目标看不见也一样()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint window = c.NewId();
        // 不映射:画上去是空操作,但格式错误照样要报。
        await c.SendAsync(1, 24, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(16).U16(16).U16(0).U16(1).U32(0).U32(0));
        uint gc = await CreateGcAsync(c, window);
        XMessage bitmap = await c.RequestAsync(72, 0, b => b.U32(window).U32(gc).U16(1).U16(1).I16(0).I16(0).U8(0).U8(24).U16(0).U32(0));
        Assert.IsTrue(bitmap.IsError);
        Assert.AreEqual(8, bitmap.Detail, "Bitmap 格式的深度必须是 1 → BadMatch");
        XMessage zpixmap = await c.RequestAsync(72, 2, b => b.U32(window).U32(gc).U16(1).U16(1).I16(0).I16(0).U8(0).U8(32).U16(0).U32(0));
        Assert.IsTrue(zpixmap.IsError);
        Assert.AreEqual(8, zpixmap.Detail, "ZPixmap 的深度要与窗口一致 → BadMatch");
    }

    [TestMethod]
    public async Task ReadPixels的宽高取自此刻的缓冲_窗口改了尺寸就跟着变()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);

        (int Width, int Height, int Length) seen = default;
        Assert.IsTrue(handle.ReadPixels((pixels, w, h) => seen = (w, h, pixels.Length)));
        Assert.AreEqual((64, 48, 64 * 48), seen);

        // ConfigureWindow:宽 0x4、高 0x8。
        await c.SendAsync(12, 0, b => b.U32(window).U16(0x4 | 0x8).U16(0).U32(80).U32(50));
        await c.SyncAsync();
        Assert.IsTrue(handle.ReadPixels((pixels, w, h) => seen = (w, h, pixels.Length)));
        Assert.AreEqual((80, 50, 80 * 50), seen);

        await c.SendAsync(4, 0, b => b.U32(window));   // DestroyWindow
        await c.SyncAsync();
        Assert.IsFalse(handle.ReadPixels((_, _, _) => Assert.Fail("窗口已销毁,不该再调")));
    }
}
