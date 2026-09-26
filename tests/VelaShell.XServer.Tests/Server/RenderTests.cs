using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>RENDER 扩展:格式查询、picture、Composite、FillRectangles、字形、梯形、渐变。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RenderTests
{
    private sealed record Formats(uint Argb32, uint Rgb24, uint A8);

    private sealed record Setup(X11Server Server, RecordingHost Host, XTestClient Client, byte Major, Formats Formats, uint Window, uint Picture, XTopLevelWindow Handle)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            Host.Dispose();
        }

        public uint Pixel(int x, int y)
        {
            (uint[] px, int w, _) = RecordingHost.Snapshot(Handle);
            return px[(y * w) + x] & 0xFFFFFF;
        }
    }

    /// <summary>映射一个 40×20、白底的顶层窗口,并在它上面建一个 x8r8g8b8 的 picture。</summary>
    private static async Task<Setup> SetupAsync()
    {
        RecordingHost host = new();
        X11Server server = new(host: host);
        XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("RENDER")).Pad());
        Assert.AreEqual(1, q.Bytes[8], "RENDER 应当存在");
        byte major = q.Bytes[9];

        XMessage f = await c.RequestAsync(major, 1);
        int count = (int)f.U32(8);
        uint argb = 0, rgb = 0, a8 = 0;
        for (int i = 0; i < count; i++)
        {
            int o = 32 + (i * 28);
            uint id = f.U32(o);
            byte depth = f.Bytes[o + 5];
            ushort alphaMask = f.U16(o + 22), redMask = f.U16(o + 10);
            if (depth == 32 && alphaMask == 0xFF && redMask == 0xFF) argb = id;
            if (depth == 24 && redMask == 0xFF) rgb = id;
            if (depth == 8 && alphaMask == 0xFF && redMask == 0) a8 = id;
        }
        Assert.AreNotEqual(0u, argb);
        Assert.AreNotEqual(0u, rgb);
        Assert.AreNotEqual(0u, a8);

        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(20).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        await c.SendAsync(8, 0, b => b.U32(window));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(window));
        uint picture = c.NewId();
        await c.SendAsync(major, 4, b => b.U32(picture).U32(window).U32(rgb).U32(0));
        return new Setup(server, host, c, major, new Formats(argb, rgb, a8), window, picture, host.Mapped[window]);
    }

    [TestMethod]
    public async Task QueryVersion报0点11()
    {
        await using Setup s = await SetupAsync();
        XMessage v = await s.Client.RequestAsync(s.Major, 0, b => b.U32(0).U32(11));
        Assert.AreEqual(0u, v.U32(8));
        Assert.AreEqual(11u, v.U32(12));
    }

    [TestMethod]
    public async Task FillRectangles半透明红叠在白底上()
    {
        await using Setup s = await SetupAsync();
        // 预乘的 50% 红:red = alpha = 0x8000。
        await s.Client.SendAsync(s.Major, 26, b => b.U8(3).U8(0).U8(0).U8(0).U32(s.Picture)
            .U16(0x8000).U16(0).U16(0).U16(0x8000).I16(2).I16(2).U16(4).U16(4));
        await s.Client.SyncAsync();
        Assert.AreEqual(0xFF7F7Fu, s.Pixel(3, 3));
        Assert.AreEqual(0xFFFFFFu, s.Pixel(10, 10));
    }

    [TestMethod]
    public async Task 深度32像素图经Composite叠到窗口()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint pixmap = c.NewId();
        await c.SendAsync(53, 32, b => b.U32(pixmap).U32(s.Window).U16(8).U16(8));
        uint src = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(src).U32(pixmap).U32(s.Formats.Argb32).U32(0));
        // 像素图全填不透明蓝(Src)。
        await c.SendAsync(s.Major, 26, b => b.U8(1).U8(0).U8(0).U8(0).U32(src)
            .U16(0).U16(0).U16(0xFFFF).U16(0xFFFF).I16(0).I16(0).U16(8).U16(8));
        await c.SendAsync(s.Major, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(src).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(10).I16(5).U16(8).U16(8));
        await c.SyncAsync();
        Assert.AreEqual(0x0000FFu, s.Pixel(12, 7));
        Assert.AreEqual(0xFFFFFFu, s.Pixel(9, 7));
    }

    [TestMethod]
    public async Task 字形用纯色源画到窗口()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint glyphSet = c.NewId();
        await c.SendAsync(s.Major, 17, b => b.U32(glyphSet).U32(s.Formats.A8));
        // 一个 2×2、全不透明的字形,原点在左上角,前进 3 像素。a8 每行按 4 字节对齐。
        await c.SendAsync(s.Major, 20, b => b.U32(glyphSet).U32(1).U32(65)
            .U16(2).U16(2).I16(0).I16(0).I16(3).I16(0)
            .U32(0x0000FFFF).U32(0x0000FFFF));
        uint black = c.NewId();
        await c.SendAsync(s.Major, 33, b => b.U32(black).U16(0).U16(0).U16(0).U16(0xFFFF));
        // CompositeGlyphs8:两个 'A',从 (5, 5) 开始。
        await c.SendAsync(s.Major, 23, b => b.U8(3).U8(0).U8(0).U8(0).U32(black).U32(s.Picture).U32(0).U32(glyphSet)
            .I16(0).I16(0).U8(2).U8(0).U8(0).U8(0).I16(5).I16(5).U8(65).U8(65).U8(0).U8(0));
        await c.SyncAsync();
        Assert.AreEqual(0x000000u, s.Pixel(5, 5));
        Assert.AreEqual(0x000000u, s.Pixel(9, 6), "第二个字形从 x = 8 开始");
        Assert.AreEqual(0xFFFFFFu, s.Pixel(7, 5), "两个字形之间的空隙");
    }

    [TestMethod]
    public async Task 带a8遮罩格式的梯形边缘是半覆盖()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint black = c.NewId();
        await c.SendAsync(s.Major, 33, b => b.U32(black).U16(0).U16(0).U16(0).U16(0xFFFF));
        static int F(double v) => (int)(v * 65536);
        // 矩形梯形:x 从 2.5 到 6,y 从 1 到 4。
        await c.SendAsync(s.Major, 10, b => b.U8(3).U8(0).U8(0).U8(0).U32(black).U32(s.Picture).U32(s.Formats.A8).I16(0).I16(0)
            .I32(F(1)).I32(F(4))
            .I32(F(2.5)).I32(F(1)).I32(F(2.5)).I32(F(4))
            .I32(F(6)).I32(F(1)).I32(F(6)).I32(F(4)));
        await c.SyncAsync();
        Assert.AreEqual(0x000000u, s.Pixel(4, 2));
        uint edge = s.Pixel(2, 2) & 0xFF;
        Assert.IsTrue(edge is >= 0x7E and <= 0x82, $"左边缘半覆盖:{edge:x}");
        Assert.AreEqual(0xFFFFFFu, s.Pixel(6, 2));
    }

    [TestMethod]
    public async Task 线性渐变从黑到白()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint gradient = c.NewId();
        await c.SendAsync(s.Major, 34, b => b.U32(gradient).I32(0).I32(0).I32(40 << 16).I32(0).U32(2)
            .I32(0).I32(1 << 16)
            .U16(0).U16(0).U16(0).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF));
        await c.SendAsync(s.Major, 8, b => b.U8(1).U8(0).U8(0).U8(0).U32(gradient).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(0).I16(0).U16(40).U16(20));
        await c.SyncAsync();
        uint left = s.Pixel(0, 10) & 0xFF, mid = s.Pixel(20, 10) & 0xFF, right = s.Pixel(39, 10) & 0xFF;
        Assert.IsTrue(left < 8, $"左端接近黑:{left}");
        Assert.IsTrue(mid is > 120 and < 140, $"中间接近灰:{mid}");
        Assert.IsTrue(right > 248, $"右端接近白:{right}");
    }

    [TestMethod]
    public async Task 坏op报BadPictOp_坏picture报BadPicture()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        XMessage badOp = await c.RequestAsync(s.Major, 26, b => b.U8(99).U8(0).U8(0).U8(0).U32(s.Picture).U16(0).U16(0).U16(0).U16(0));
        Assert.IsTrue(badOp.IsError);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("RENDER")).Pad());
        byte errorBase = q.Bytes[11];
        Assert.AreEqual(errorBase + 2, badOp.Bytes[1]);
        XMessage badPicture = await c.RequestAsync(s.Major, 7, b => b.U32(0x123456));
        Assert.AreEqual(errorBase + 1, badPicture.Bytes[1]);
    }
}
