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
    public async Task 源picture的AlphaMap替换源alpha并参与Composite()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint sourcePixmap = c.NewId(), alphaPixmap = c.NewId();
        await c.SendAsync(53, 32, b => b.U32(sourcePixmap).U32(s.Window).U16(4).U16(4));
        await c.SendAsync(53, 8, b => b.U32(alphaPixmap).U32(s.Window).U16(4).U16(4));

        uint source = c.NewId(), alpha = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(source).U32(sourcePixmap).U32(s.Formats.Argb32).U32(0));
        await c.SendAsync(s.Major, 4, b => b.U32(alpha).U32(alphaPixmap).U32(s.Formats.A8).U32(0));

        // 源是完全不透明的蓝色，alpha-map 是 50% alpha。
        await c.SendAsync(s.Major, 26, b => b.U8(1).U8(0).U8(0).U8(0).U32(source)
            .U16(0).U16(0).U16(0xFFFF).U16(0xFFFF).I16(0).I16(0).U16(4).U16(4));
        await c.SendAsync(s.Major, 26, b => b.U8(1).U8(0).U8(0).U8(0).U32(alpha)
            .U16(0).U16(0).U16(0).U16(0x8000).I16(0).I16(0).U16(4).U16(4));

        // ChangePicture 的 bit 1 是 alpha-map。
        await c.SendAsync(s.Major, 5, b => b.U32(source).U32(1u << 1).U32(alpha));
        await c.SendAsync(s.Major, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(10).I16(5).U16(4).U16(4));
        await c.SyncAsync();

        Assert.AreEqual(0x7F7FFFu, s.Pixel(11, 6));
        Assert.AreEqual(0xFFFFFFu, s.Pixel(9, 6));
    }

    [TestMethod]
    public async Task 纯色源也能挂AlphaMap_源alpha按它减半()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;

        // 4×4 的 a8 像素图做 alpha-map,每个像素 alpha 0x80:PolyFillRectangle 用 0x80 作前景色。
        uint amPixmap = c.NewId();
        await c.SendAsync(53, 8, b => b.U32(amPixmap).U32(s.Window).U16(4).U16(4));
        uint amGc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(amGc).U32(amPixmap).U32(0x4).U32(0x80808080));
        await c.SendAsync(70, 0, b => b.U32(amPixmap).U32(amGc).I16(0).I16(0).U16(4).U16(4));
        uint am = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(am).U32(amPixmap).U32(s.Formats.A8).U32(0));

        // 源是 CreateSolidFill 的不透明红(没有 drawable),挂上 alpha-map 后 Over 到白底。
        uint source = c.NewId();
        await c.SendAsync(s.Major, 33, b => b.U32(source).U16(0xFFFF).U16(0).U16(0).U16(0xFFFF));
        await c.SendAsync(s.Major, 5, b => b.U32(source).U32(1u << 1).U32(am));
        await c.SendAsync(s.Major, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(2).I16(2).U16(4).U16(4));
        await c.SyncAsync();

        // 源变成 alpha 0x80 的红(预乘 0x80800000);Over 到白底:红 0x80 + 0xFF·0.5 = 0xFF,绿 / 蓝 0xFF·0.5 = 0x7F。
        Assert.AreEqual(0xFF7F7Fu, s.Pixel(3, 3));
    }

    [TestMethod]
    public async Task AlphaMap只作用一层_先挂上再给alpha_map挂alpha_map时后者不生效()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint sourcePixmap = c.NewId(), alphaPixmap = c.NewId(), innerPixmap = c.NewId();
        await c.SendAsync(53, 32, b => b.U32(sourcePixmap).U32(s.Window).U16(4).U16(4));
        await c.SendAsync(53, 8, b => b.U32(alphaPixmap).U32(s.Window).U16(4).U16(4));
        await c.SendAsync(53, 8, b => b.U32(innerPixmap).U32(s.Window).U16(4).U16(4));

        uint source = c.NewId(), alpha = c.NewId(), inner = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(source).U32(sourcePixmap).U32(s.Formats.Argb32).U32(0));
        await c.SendAsync(s.Major, 4, b => b.U32(alpha).U32(alphaPixmap).U32(s.Formats.A8).U32(0));
        await c.SendAsync(s.Major, 4, b => b.U32(inner).U32(innerPixmap).U32(s.Formats.A8).U32(0));

        // 源不透明蓝;alpha-map 50%;alpha-map 自己的 alpha-map 全透明(像素图建好就是 0)。
        await c.SendAsync(s.Major, 26, b => b.U8(1).U8(0).U8(0).U8(0).U32(source)
            .U16(0).U16(0).U16(0xFFFF).U16(0xFFFF).I16(0).I16(0).U16(4).U16(4));
        await c.SendAsync(s.Major, 26, b => b.U8(1).U8(0).U8(0).U8(0).U32(alpha)
            .U16(0).U16(0).U16(0).U16(0x8000).I16(0).I16(0).U16(4).U16(4));

        // 先 source → alpha,再 alpha → inner:ChangePicture 只核新挂上的那张有没有 alpha-map,这个顺序拦不住,
        // 一张张接下去就是任意长的链。合成时只用一层,inner 不起作用。
        await c.SendAsync(s.Major, 5, b => b.U32(source).U32(1u << 1).U32(alpha));
        await c.SendAsync(s.Major, 5, b => b.U32(alpha).U32(1u << 1).U32(inner));
        await c.SendAsync(s.Major, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(10).I16(5).U16(4).U16(4));
        await c.SyncAsync();

        Assert.AreEqual(0x7F7FFFu, s.Pixel(11, 6));
    }

    [TestMethod]
    public async Task AlphaMap接成很长的链_Composite不递归不崩()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint sourcePixmap = c.NewId(), alphaPixmap = c.NewId();
        await c.SendAsync(53, 32, b => b.U32(sourcePixmap).U32(s.Window).U16(4).U16(4));
        await c.SendAsync(53, 8, b => b.U32(alphaPixmap).U32(s.Window).U16(4).U16(4));
        uint source = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(source).U32(sourcePixmap).U32(s.Formats.Argb32).U32(0));
        await c.SendAsync(s.Major, 26, b => b.U8(1).U8(0).U8(0).U8(0).U32(source)
            .U16(0).U16(0).U16(0xFFFF).U16(0xFFFF).I16(0).I16(0).U16(4).U16(4));

        // 同一张 a8 像素图上建 N 张 picture,依次把上一张的 alpha-map 设成下一张。每一步新挂上的那张都还没有
        // alpha-map,ChangePicture 都接受;合成时若顺着链往下解,每一环一层递归,栈溢出在 .NET 里接不住,整个进程会崩。
        const int Links = 100_000;
        uint previous = source;
        for (int i = 0; i < Links; i++)
        {
            uint next = c.NewId();
            uint link = previous;
            await c.SendAsync(s.Major, 4, b => b.U32(next).U32(alphaPixmap).U32(s.Formats.A8).U32(0));
            await c.SendAsync(s.Major, 5, b => b.U32(link).U32(1u << 1).U32(next));
            previous = next;
        }
        await c.SendAsync(s.Major, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(10).I16(5).U16(4).U16(4));
        await c.SyncAsync();

        // alpha-map 全 0:源整个透明,白底不变。
        Assert.AreEqual(0xFFFFFFu, s.Pixel(11, 6));
    }

    /// <summary>建一张 <paramref name="depth" /> 位、w × h 的像素图和它上面的 picture。</summary>
    private static async Task<(uint Pixmap, uint Picture)> PixmapPictureAsync(Setup s, byte depth, uint format, ushort w, ushort h)
    {
        XTestClient c = s.Client;
        uint pixmap = c.NewId(), picture = c.NewId();
        await c.SendAsync(53, depth, b => b.U32(pixmap).U32(s.Window).U16(w).U16(h));
        await c.SendAsync(s.Major, 4, b => b.U32(picture).U32(pixmap).U32(format).U32(0));
        return (pixmap, picture);
    }

    /// <summary>FillRectangles(预乘的 16 位颜色)。</summary>
    private static Task<ushort> FillAsync(Setup s, byte op, uint picture, ushort a, ushort r, ushort g, ushort b, short x, short y, ushort w, ushort h) =>
        s.Client.SendAsync(s.Major, 26, body => body.U8(op).U8(0).U8(0).U8(0).U32(picture)
            .U16(r).U16(g).U16(b).U16(a).I16(x).I16(y).U16(w).U16(h));

    /// <summary>核心 GetImage(ZPixmap)取一个像素的原始值:深度 8 取一个字节,深度 24 / 32 取四个字节。</summary>
    private static async Task<uint> RawPixelAsync(XTestClient c, uint drawable, short x, short y, bool oneByte)
    {
        XMessage image = await c.RequestAsync(73, 2, b => b.U32(drawable).I16(x).I16(y).U16(1).U16(1).U32(0xFFFFFFFF));
        Assert.IsFalse(image.IsError);
        return oneByte ? image.Bytes[32] : image.U32(32);
    }

    [TestMethod]
    public async Task 源带变换时AlphaMap跟着变换取样()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        (_, uint source) = await PixmapPictureAsync(s, 32, s.Formats.Argb32, 4, 4);
        (_, uint alpha) = await PixmapPictureAsync(s, 8, s.Formats.A8, 4, 4);
        await FillAsync(s, 1, source, 0xFFFF, 0, 0, 0xFFFF, 0, 0, 4, 4);   // 不透明蓝
        await FillAsync(s, 1, alpha, 0xFFFF, 0, 0, 0, 2, 0, 2, 4);         // alpha-map 只有右边两列不透明
        await c.SendAsync(s.Major, 5, b => b.U32(source).U32(1u << 1).U32(alpha));
        // 变换把目标的 x 映到源的 x + 2:目标上的两列取的是源与 alpha-map 的右边两列。
        await c.SendAsync(s.Major, 28, b => b.U32(source)
            .I32(0x10000).I32(0).I32(0x20000)
            .I32(0).I32(0x10000).I32(0)
            .I32(0).I32(0).I32(0x10000));
        await c.SendAsync(s.Major, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(10).I16(5).U16(2).U16(4));
        await c.SyncAsync();

        // 原先 alpha-map 按变换前的坐标取到左边两列的 0,什么都没画上。
        Assert.AreEqual(0x0000FFu, s.Pixel(10, 5));
        Assert.AreEqual(0x0000FFu, s.Pixel(11, 8));
        Assert.AreEqual(0xFFFFFFu, s.Pixel(12, 5));
    }

    [TestMethod]
    public async Task AlphaMap只换alpha通道_颜色通道照原样()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        (_, uint source) = await PixmapPictureAsync(s, 32, s.Formats.Argb32, 4, 4);
        (_, uint alpha) = await PixmapPictureAsync(s, 8, s.Formats.A8, 4, 4);
        await FillAsync(s, 1, source, 0x8080, 0x8080, 0, 0, 0, 0, 4, 4);   // 预乘的半透明红:a = r = 0x80
        await FillAsync(s, 1, alpha, 0xFFFF, 0, 0, 0, 0, 0, 4, 4);
        await c.SendAsync(s.Major, 5, b => b.U32(source).U32(1u << 1).U32(alpha));
        await c.SendAsync(s.Major, 8, b => b.U8(1).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(10).I16(5).U16(4).U16(4));
        await c.SyncAsync();

        // 像素是 (a 0xFF, r 0x80, g 0, b 0),Src 写进窗口就是 0x800000。原先颜色按 drawable 的 alpha 除、再乘 alpha-map 的,成了 0xFF0000。
        Assert.AreEqual(0x800000u, s.Pixel(11, 6));
    }

    [TestMethod]
    public async Task 目标的AlphaMap_合成按它的alpha算_颜色写回drawable_alpha写回alpha_map_范围之外不画()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        // 白底窗口的 picture 挂一张 8×8、全 0 的 a8 alpha-map,原点在 (2,2)。
        (uint alphaPixmap, uint alpha) = await PixmapPictureAsync(s, 8, s.Formats.A8, 8, 8);
        await c.SendAsync(s.Major, 5, b => b.U32(s.Picture).U32((1u << 1) | (1u << 2) | (1u << 3)).U32(alpha).U32(2).U32(2));

        // Atop:结果 = 源 × αd + 目标 × (1 − αs)。目标的 alpha 来自 alpha-map(0),颜色是白底的 50%;不挂 alpha-map 时 αd = 1,是 0xFF7F7F。
        await FillAsync(s, 9, s.Picture, 0x8000, 0x8000, 0, 0, 0, 0, 20, 20);
        await c.SyncAsync();
        Assert.AreEqual(0x7F7F7Fu, s.Pixel(5, 5));
        Assert.AreEqual(0xFFFFFFu, s.Pixel(1, 1), "alpha-map 范围之外不画");
        Assert.AreEqual(0xFFFFFFu, s.Pixel(12, 12), "alpha-map 范围之外不画");
        Assert.AreEqual(0u, await RawPixelAsync(c, alphaPixmap, 3, 3, oneByte: true), "Atop 不改目标的 alpha:仍是 0");

        // Over:结果的 alpha = αs + αd × (1 − αs) = 0.5,写回 alpha-map(原先 alpha-map 一直不变)。
        await FillAsync(s, 3, s.Picture, 0x8000, 0x8000, 0, 0, 0, 0, 20, 20);
        await c.SyncAsync();
        Assert.AreEqual(0x80u, await RawPixelAsync(c, alphaPixmap, 3, 3, oneByte: true));
    }

    [TestMethod]
    public async Task 挂了AlphaMap的源与目标是同一张像素图时先读完再写()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        // 1×8 的像素图,每行一个不同的颜色;同一张像素图上两张 picture:一张挂上全不透明的 alpha-map 当源,一张当目标。
        (uint pixmap, uint dst) = await PixmapPictureAsync(s, 32, s.Formats.Argb32, 1, 8);
        uint source = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(source).U32(pixmap).U32(s.Formats.Argb32).U32(0));
        (_, uint alpha) = await PixmapPictureAsync(s, 8, s.Formats.A8, 1, 8);
        await FillAsync(s, 1, alpha, 0xFFFF, 0, 0, 0, 0, 0, 1, 8);
        await c.SendAsync(s.Major, 5, b => b.U32(source).U32(1u << 1).U32(alpha));
        for (short y = 0; y < 8; y++)
        {
            ushort v = (ushort)(0x1111 * (y + 1));
            await FillAsync(s, 1, dst, 0xFFFF, v, v, v, 0, y, 1, 1);
        }
        // 整列往下挪一行。逐行合成时目标在源下面:原先没看出挂了 alpha-map 的源读的就是目标,第 0 行的颜色一路传到底。
        await c.SendAsync(s.Major, 8, b => b.U8(1).U8(0).U8(0).U8(0).U32(source).U32(0).U32(dst)
            .I16(0).I16(0).I16(0).I16(0).I16(0).I16(1).U16(1).U16(7));
        await c.SyncAsync();
        for (short y = 1; y < 8; y++)
        {
            uint want = 0x111111u * (uint)y;
            Assert.AreEqual(want, await RawPixelAsync(c, pixmap, 0, y, oneByte: false) & 0xFFFFFF, $"第 {y} 行");
        }
    }

    [TestMethod]
    public async Task AddGlyphs的尺寸与个数按不会回绕的算法核长度_回BadLength而不是分配几个GB()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint glyphSet = c.NewId();
        await c.SendAsync(s.Major, 17, b => b.U32(glyphSet).U32(s.Formats.Argb32));
        // 32768² 的 a8r8g8b8:每行 131072 字节 × 32768 行 = 2³²,按 int 算回绕成 0 —— 原先长度检查放行、随后分配 4 GB。
        XMessage huge = await c.RequestAsync(s.Major, 20, b => b.U32(glyphSet).U32(1).U32(65)
            .U16(32768).U16(32768).I16(0).I16(0).I16(0).I16(0));
        Assert.IsTrue(huge.IsError);
        Assert.AreEqual(16, huge.Detail, "BadLength");

        // 个数 ≥ 2³¹:按 int 读是负数。
        XMessage negative = await c.RequestAsync(s.Major, 20, b => b.U32(glyphSet).U32(0x80000000));
        Assert.IsTrue(negative.IsError);
        Assert.AreEqual(16, negative.Detail, "BadLength 而不是 BadImplementation");

        // 渐变的色标个数同理(CreateLinearGradient)。
        XMessage stops = await c.RequestAsync(s.Major, 34, b => b.U32(c.NewId()).I32(0).I32(0).I32(0x10000).I32(0).U32(0x80000000));
        Assert.IsTrue(stops.IsError);
        Assert.AreEqual(16, stops.Detail);
    }

    [TestMethod]
    public async Task 源picture的裁剪也限制读_裁剪之外的目标不合成()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        // 10×10 的红像素图做源,源 picture 只留左上 5×5。
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(s.Window).U16(10).U16(10));
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4).U32(0xFF0000));
        await c.SendAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(0).I16(0).U16(10).U16(10));
        uint source = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(source).U32(pixmap).U32(s.Formats.Rgb24).U32(0));
        await c.SendAsync(s.Major, 6, b => b.U32(source).I16(0).I16(0).I16(0).I16(0).U16(5).U16(5));   // SetPictureClipRectangles

        // Src 合成 10×10 到白底窗口的 (20, 5):RENDER 规范说 clip-mask 也限制读,裁剪之外的源读不到、对应的目标不动。
        await c.SendAsync(s.Major, 8, b => b.U8(1).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(20).I16(5).U16(10).U16(10));
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, s.Pixel(22, 7), "裁剪之内照常合成");
        Assert.AreEqual(0xFFFFFFu, s.Pixel(27, 12), "裁剪之外不合成");
    }

    [TestMethod]
    public async Task CreateCursor的热点落在图外回BadMatch()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint pixmap = c.NewId();
        await c.SendAsync(53, 32, b => b.U32(pixmap).U32(s.Window).U16(16).U16(16));
        uint picture = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(picture).U32(pixmap).U32(s.Formats.Argb32).U32(0));
        XMessage outside = await c.RequestAsync(s.Major, 27, b => b.U32(c.NewId()).U32(picture).U16(16).U16(3));
        Assert.IsTrue(outside.IsError);
        Assert.AreEqual(8, outside.Detail, "BadMatch");
        ushort inside = await c.SendAsync(s.Major, 27, b => b.U32(c.NewId()).U32(picture).U16(15).U16(15));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextAsync(m => m.IsError && m.Sequence == inside, 100));
    }

    [TestMethod]
    public async Task FreeGlyphs里有一个不存在时哪个都不释放()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint glyphSet = c.NewId();
        await c.SendAsync(s.Major, 17, b => b.U32(glyphSet).U32(s.Formats.A8));
        await c.SendAsync(s.Major, 20, b => b.U32(glyphSet).U32(1).U32(65).U16(1).U16(1).I16(0).I16(0).I16(1).I16(0).U32(0xFF));
        // [65, 66]:66 不存在 —— BadGlyph,而且 65 不能已经释放了(原先边核对边释放)。
        XMessage bad = await c.RequestAsync(s.Major, 22, b => b.U32(glyphSet).U32(65).U32(66));
        Assert.IsTrue(bad.IsError);
        ushort again = await c.SendAsync(s.Major, 22, b => b.U32(glyphSet).U32(65));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextAsync(m => m.IsError && m.Sequence == again, 150), "65 还在,这次释放成功");
    }

    [TestMethod]
    public async Task 带遮罩格式的字形_遮罩只按目标上可写的一块分配()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        // 40×20 的顶层里一个 32000×32000 的子窗口:可绘对象很大,可写的只有顶层那一小块。
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(s.Window).I16(0).I16(0).U16(32000).U16(32000).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(child));
        uint picture = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(picture).U32(child).U32(s.Formats.Rgb24).U32(0));
        uint glyphSet = c.NewId();
        await c.SendAsync(s.Major, 17, b => b.U32(glyphSet).U32(s.Formats.A8));
        await c.SendAsync(s.Major, 20, b => b.U32(glyphSet).U32(1).U32(65)
            .U16(2).U16(2).I16(0).I16(0).I16(0).I16(0)
            .U32(0x0000FFFF).U32(0x0000FFFF));
        uint black = c.NewId();
        await c.SendAsync(s.Major, 33, b => b.U32(black).U16(0).U16(0).U16(0).U16(0xFFFF));

        // 两个字形相距 31000:外接矩形约 10^9 像素。原先按它分配遮罩(a8 就是 1 GB)。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await c.SendAsync(s.Major, 23, b => b.U8(3).U8(0).U8(0).U8(0).U32(black).U32(picture).U32(s.Formats.A8).U32(glyphSet)
            .I16(0).I16(0)
            .U8(1).U8(0).U8(0).U8(0).I16(5).I16(5).U8(65).U8(0).U8(0).U8(0)
            .U8(1).U8(0).U8(0).U8(0).I16(31000).I16(31000).U8(65).U8(0).U8(0).U8(0));
        await c.SyncAsync();
        Assert.AreEqual(0x000000u, s.Pixel(5, 5), "第一个字形画上了");
        Assert.IsLessThan(2_000, watch.ElapsedMilliseconds);
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
    public async Task 渐变的色标越界或没排好序回BadValue()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        static Action<XTestClient.Body> Linear(uint id, int first, int second) => b => b.U32(id).I32(0).I32(0).I32(40 << 16).I32(0).U32(2)
            .I32(first).I32(second)
            .U16(0).U16(0).U16(0).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF);
        XMessage reversed = await c.RequestAsync(s.Major, 34, Linear(c.NewId(), 1 << 16, 0));
        Assert.IsTrue(reversed.IsError);
        Assert.AreEqual(2, reversed.Detail, "BadValue:没按大小排好");
        XMessage outside = await c.RequestAsync(s.Major, 34, Linear(c.NewId(), 0, 2 << 16));
        Assert.IsTrue(outside.IsError);
        Assert.AreEqual(2, outside.Detail, "BadValue:色标超过 1");
        // 相等的色标(硬过渡)照收。
        uint equal = c.NewId();
        await c.SendAsync(s.Major, 34, Linear(equal, 1 << 15, 1 << 15));
        await c.SendAsync(s.Major, 8, b => b.U8(1).U8(0).U8(0).U8(0).U32(equal).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(0).I16(0).U16(40).U16(20));
        await c.SyncAsync();
        Assert.AreEqual(0x000000u, s.Pixel(5, 10) & 0xFF);
        Assert.AreEqual(0xFFu, s.Pixel(35, 10) & 0xFF);
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
        Assert.IsLessThan(8u, left, $"左端接近黑:{left}");
        Assert.IsTrue(mid is > 120 and < 140, $"中间接近灰:{mid}");
        Assert.IsGreaterThan(248u, right, $"右端接近白:{right}");
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

    [TestMethod]
    public async Task 同一张picture往下错一行Composite到自己_结果像先读完源再写()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        (ushort R, ushort G, ushort B)[] rows = [(0xFFFF, 0, 0), (0, 0xFFFF, 0), (0, 0, 0xFFFF)];
        for (int y = 0; y < rows.Length; y++)
        {
            (ushort r, ushort g, ushort b) = rows[y];
            short row = (short)y;
            await c.SendAsync(s.Major, 26, x => x.U8(1).U8(0).U8(0).U8(0).U32(s.Picture)
                .U16(r).U16(g).U16(b).U16(0xFFFF).I16(0).I16(row).U16(10).U16(1));   // FillRectangles Src:红、绿、蓝三行
        }
        // Src,源 (0, 0) → 目标 (0, 1):整块往下挪一行。逐行从上往下做的话,第 2、3 行读到的是刚写进去的红。
        await c.SendAsync(s.Major, 8, x => x.U8(1).U8(0).U8(0).U8(0).U32(s.Picture).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(0).I16(1).U16(10).U16(3));
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, s.Pixel(5, 1));
        Assert.AreEqual(0x00FF00u, s.Pixel(5, 2), "原来第 1 行的绿");
        Assert.AreEqual(0x0000FFu, s.Pixel(5, 3), "原来第 2 行的蓝");
    }
    [TestMethod]
    public async Task 伸出顶层的子窗口做源合成到另一个顶层_只取缓冲里的部分_不回BadImplementation()
    {
        await using Setup s = await SetupAsync();
        XTestClient c = s.Client;
        uint other = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(other).U32(c.RootWindow).I16(100).I16(0).U16(40).U16(20).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(other));
        await s.Host.WaitForAsync(() => s.Host.Mapped.ContainsKey(other));
        uint child = c.NewId();   // 左边 20 列伸出它的顶层
        await c.SendAsync(1, 0, b => b.U32(child).U32(other).I16(-20).I16(0).U16(40).U16(10).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0x0000FF));
        await c.SendAsync(8, 0, b => b.U32(child));
        uint source = c.NewId();
        await c.SendAsync(s.Major, 4, b => b.U32(source).U32(child).U32(s.Formats.Rgb24).U32(0));

        // Src,源 (0, 0) 40×10 → 目标 (0, 10)。原先快路径只查 picture 尺寸,按缓冲取下标时越界。
        await c.SendAsync(s.Major, 8, b => b.U8(1).U8(0).U8(0).U8(0).U32(source).U32(0).U32(s.Picture)
            .I16(0).I16(0).I16(0).I16(0).I16(0).I16(10).U16(40).U16(10));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextAsync(m => m.IsError, 200));
        Assert.AreEqual(0x0000FFu, s.Pixel(25, 12), "源在缓冲里的部分照常取到");
    }
}
