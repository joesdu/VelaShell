using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>窗口映射、Expose、绘图、文字、输入 —— 画出来的像素与收到的事件都直接断言。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class WindowAndDrawingTests
{
    private const uint ExposureMask = 0x8000, KeyPressMask = 0x1, ButtonPressMask = 0x4, StructureNotifyMask = 0x20000;

    /// <summary>建一个顶层窗口(背景色 + 事件掩码)、映射、等宿主看到它。</summary>
    private static async Task<(uint Window, XTopLevelWindow Handle)> MapWindowAsync(
        XTestClient c, RecordingHost host, uint background, uint eventMask, int width = 64, int height = 48)
    {
        uint id = c.NewId();
        // CreateWindow:value-mask = background-pixel(0x2) | event-mask(0x800)
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(20).I16(30).U16((ushort)width).U16((ushort)height)
            .U16(0).U16(1).U32(0).U32(0x802).U32(background).U32(eventMask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return (id, host.Mapped[id]);
    }

    private static async Task<uint> CreateGcAsync(XTestClient c, uint drawable, uint foreground)
    {
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(drawable).U32(0x4).U32(foreground));   // foreground
        return gc;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 映射顶层窗口会画背景并发Expose(bool bigEndian)
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server, bigEndian);

        (uint id, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x336699, ExposureMask | StructureNotifyMask);
        Assert.AreEqual(64, handle.Snapshot.Width);
        Assert.AreEqual(20, handle.Snapshot.X);

        XMessage map = await c.NextEventAsync(19);   // MapNotify
        Assert.AreEqual(id, map.U32(8));
        XMessage expose = await c.NextEventAsync(12);
        Assert.AreEqual(id, expose.U32(4));
        Assert.AreEqual(64, expose.U16(12));
        Assert.AreEqual(0, expose.U16(16), "count");

        (uint[] pixels, _, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x336699u, pixels[0]);
        Assert.AreEqual(0x336699u, pixels[^1]);
    }

    [TestMethod]
    public async Task 子窗口按堆叠裁剪背景且有边框()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint top, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x000000, 0);

        uint child = c.NewId();
        // 位置 (10,10)、内区 20×20、边框 2(border-pixel 0x8 = 红)、背景白。
        await c.SendAsync(1, 24, b => b.U32(child).U32(top).I16(10).I16(10).U16(20).U16(20).U16(2).U16(1).U32(0)
            .U32(0x2 | 0x8).U32(0xFFFFFF).U32(0xFF0000));
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0xFF0000u, px[(10 * w) + 10], "边框左上角");
        Assert.AreEqual(0xFFFFFFu, px[(12 * w) + 12], "内区左上角(10+2)");
        Assert.AreEqual(0x000000u, px[(40 * w) + 40], "父窗口背景");
    }

    [TestMethod]
    public async Task 取消映射子窗口露出父窗口与下面的兄弟_兄弟收到Expose()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint top, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0xFF0000, 0);   // 父窗口:红

        // 下面的 A:(0,0) 20×20 绿,选了 Exposure;上面的 B:(10,10) 20×20 蓝,后建、堆在 A 上面;B 里还套着白色的 C。
        uint a = c.NewId(), b = c.NewId(), inner = c.NewId();
        await c.SendAsync(1, 24, x => x.U32(a).U32(top).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0x802).U32(0x00FF00).U32(ExposureMask));
        await c.SendAsync(1, 24, x => x.U32(b).U32(top).I16(10).I16(10).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0x2).U32(0x0000FF));
        await c.SendAsync(1, 24, x => x.U32(inner).U32(b).I16(2).I16(2).U16(6).U16(6).U16(0).U16(1).U32(0).U32(0x2).U32(0xFFFFFF));
        foreach (uint id in (uint[])[a, b, inner])
        {
            await c.SendAsync(8, 0, x => x.U32(id));
        }
        await c.SyncAsync();
        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x0000FFu, px[(11 * w) + 11], "重叠处是上面的 B");
        Assert.AreEqual(0xFFFFFFu, px[(13 * w) + 13], "B 里的 C");
        XMessage mapped = await c.NextEventAsync(12);
        Assert.AreEqual((a, 20, 20), (mapped.U32(4), mapped.U16(12), mapped.U16(14)), "A 映射时的 Expose");

        // 取消映射只从父窗口这棵子树走起重画:父窗口的背景、下面的 A 都要补上,C 随 B 一起不见。
        await c.SendAsync(10, 0, x => x.U32(b));
        await c.SyncAsync();
        (px, w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x00FF00u, px[(11 * w) + 11], "重叠处露出下面的 A");
        Assert.AreEqual(0x00FF00u, px[(13 * w) + 13], "C 那块也露出 A");
        Assert.AreEqual(0xFF0000u, px[(25 * w) + 25], "A 之外露出父窗口的背景");
        XMessage exposed = await c.NextEventAsync(12);
        Assert.AreEqual((a, 10, 10, 10, 10), (exposed.U32(4), exposed.U16(8), exposed.U16(10), exposed.U16(12), exposed.U16(14)),
            "A 露出来的那一块(A 的坐标)发了 Expose");
    }

    [TestMethod]
    public async Task 填矩形与CopyArea画到像素上并回NoExposure()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0, 0);
        uint gc = await CreateGcAsync(c, win, 0x00FF00);

        await c.SendAsync(70, 0, b => b.U32(win).U32(gc).I16(2).I16(3).U16(5).U16(4));        // PolyFillRectangle
        await c.SendAsync(62, 0, b => b.U32(win).U32(win).U32(gc).I16(2).I16(3).I16(30).I16(20).U16(5).U16(4));   // CopyArea
        XMessage noExpose = await c.NextEventAsync(14);
        Assert.AreEqual(win, noExpose.U32(4));

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x00FF00u, px[(3 * w) + 2]);
        Assert.AreEqual(0x00FF00u, px[(6 * w) + 6], "矩形右下角 (2+5−1, 3+4−1)");
        Assert.AreEqual(0u, px[(7 * w) + 7], "矩形外");
        Assert.AreEqual(0x00FF00u, px[(20 * w) + 30], "拷过去的左上角");
        Assert.AreEqual(0x00FF00u, px[(23 * w) + 34]);
    }

    [TestMethod]
    public async Task GXxor画两遍等于没画()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x123456, 0);
        uint gc = c.NewId();
        // function = GXxor(6)、foreground、line-width 3(宽线走多边形并集,不能重复着色)
        await c.SendAsync(55, 0, b => b.U32(gc).U32(win).U32(0x1 | 0x4 | 0x10).U32(6).U32(0xFFFFFF).U32(3));
        for (int i = 0; i < 2; i++)
        {
            await c.SendAsync(65, 0, b => b.U32(win).U32(gc).I16(5).I16(5).I16(50).I16(40).I16(5).I16(40));   // PolyLine
        }
        await c.SyncAsync();
        (uint[] px, _, _) = RecordingHost.Snapshot(handle);
        Assert.IsTrue(px.All(p => p == 0x123456u), "画两遍 XOR 后应当完全复原");
    }

    [TestMethod]
    public async Task ImageText8用内置fixed字体画出字形()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x000000, 0);

        uint font = c.NewId();
        await c.SendAsync(45, 0, b => b.U32(font).U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("fixed")));
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(win).U32(0x4 | 0x8 | 0x4000).U32(0xFFFFFF).U32(0x0000FF).U32(font));
        await c.SendAsync(76, 2, b => b.U32(win).U32(gc).I16(4).I16(20).Bytes(Encoding.Latin1.GetBytes("Hi")));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        // 字形行框:x 4..16(两个 6 像素宽的字),y 20−11 .. 20+2。背景填成蓝,字形点是白。
        int white = 0, blue = 0;
        for (int y = 9; y < 22; y++)
        {
            for (int x = 4; x < 16; x++)
            {
                if (px[(y * w) + x] == 0xFFFFFFu) white++;
                if (px[(y * w) + x] == 0x0000FFu) blue++;
            }
        }
        Assert.IsGreaterThan(10, white, "应当有字形像素");
        Assert.IsGreaterThan(white, blue, "行框其余部分是背景色");
        Assert.AreEqual(0u, px[(20 * w) + 30], "行框外不动");
    }

    [TestMethod]
    public async Task 单字节字体的CHAR2B按16位数取字_byte1不为0时画default_char()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint pixmap = c.NewId(), font = c.NewId(), gc = c.NewId(), clear = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(20).U16(20));
        await c.SendAsync(45, 0, b => b.U32(font).U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("fixed")));
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4 | 0x8 | 0x4000).U32(0xFFFFFF).U32(0x000000).U32(font));
        await c.SendAsync(55, 0, b => b.U32(clear).U32(pixmap).U32(0x4).U32(0x000000));

        async Task<byte[]> DrawAsync(bool image, byte byte1, byte byte2)
        {
            await c.SendAsync(70, 0, b => b.U32(pixmap).U32(clear).I16(0).I16(0).U16(20).U16(20));   // 清成黑
            if (image)
            {
                await c.SendAsync(77, 1, b => b.U32(pixmap).U32(gc).I16(2).I16(14).U8(byte1).U8(byte2));   // ImageText16
            }
            else
            {
                await c.SendAsync(75, 0, b => b.U32(pixmap).U32(gc).I16(2).I16(14).U8(1).U8(0).U8(byte1).U8(byte2));   // PolyText16
            }
            XMessage pixels = await c.RequestAsync(73, 2, b => b.U32(pixmap).I16(0).I16(0).U16(20).U16(20).U32(0xFFFFFFFF));   // GetImage
            return pixels.Bytes[32..];
        }

        foreach (bool image in (bool[])[true, false])
        {
            byte[] fallback = await DrawAsync(image, 0, 0);       // default-char 是 0
            byte[] letter = await DrawAsync(image, 0, (byte)'A');
            byte[] outOfRange = await DrawAsync(image, 1, (byte)'A');   // 0x0141:单字节字体里没有
            CollectionAssert.AreNotEqual(letter, fallback);
            CollectionAssert.AreEqual(fallback, outOfRange, $"{(image ? "ImageText16" : "PolyText16")}:原先丢掉 byte1,画成了 A");
        }
    }

    [TestMethod]
    public async Task QueryFont返回fixed的度量与每个字符的CHARINFO()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint font = c.NewId();
        await c.SendAsync(45, 0, b => b.U32(font).U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("fixed")));
        XMessage reply = await c.RequestAsync(47, 0, b => b.U32(font));
        Assert.IsTrue(reply.IsReply);
        Assert.AreEqual(6, reply.I16(28), "max-bounds(从 24 起)的 character-width = 6");
        Assert.AreEqual(11, reply.I16(52), "font-ascent");
        Assert.AreEqual(2, reply.I16(54), "font-descent");
        uint charInfos = reply.U32(56);
        Assert.AreEqual(256u - reply.U16(40), charInfos, "单字节字体:min..255 每个一条");
    }

    [TestMethod]
    public async Task 字体的短名字_XLFD_没有的字号退到最接近的_没有的字族照旧BadName()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        async Task<int?> OpenWidthAsync(string name)
        {
            uint font = c.NewId();
            byte[] bytes = Encoding.Latin1.GetBytes(name);
            await c.SendAsync(45, 0, b => b.U32(font).U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
            XMessage reply = await c.RequestAsync(47, 0, b => b.U32(font));   // QueryFont
            return reply.IsReply ? reply.I16(28) : null;                       // max-bounds 的 character-width
        }

        // fonts.alias 的短名字现在都有自己的数据(原先退到 6x13 之类);目标没有随库带的 8x16(Sony)退到最接近的。
        Assert.AreEqual(8, await OpenWidthAsync("8x13"), "原先 BadName,后来退到 6x13");
        Assert.AreEqual(5, await OpenWidthAsync("5x7"));
        Assert.AreEqual(9, await OpenWidthAsync("9x18bold"));
        Assert.AreEqual(9, await OpenWidthAsync("8x16"));
        Assert.AreEqual(7, await OpenWidthAsync("-misc-fixed-medium-r-normal--14-*-*-*-*-*-iso8859-1"), "7x14");
        Assert.AreEqual(10, await OpenWidthAsync("-*-fixed-medium-r-*-*-*-200-75-75-*-*-iso8859-1"), "按 POINT_SIZE 20 磅、75 dpi 匹配上 10x20");
        Assert.AreEqual(9, await OpenWidthAsync("-misc-fixed-bold-r-normal--16-*-*-*-*-*-iso10646-1"), "粗体没有 16 像素:退到最接近的 9x15B");

        // 平均宽度翻倍要双宽字体(给宽字符配的):一样高的里面挑平均宽度最接近的,原先按名字的先后拿到 7x13 / 9x18。
        Assert.AreEqual(12, await OpenWidthAsync("-misc-fixed-medium-r-semicondensed--13-120-75-75-c-120-iso10646-1"), "12x13ja");
        Assert.AreEqual(18, await OpenWidthAsync("-misc-fixed-medium-r-normal--18-120-100-100-c-180-iso10646-1"), "18x18ja");
        // 平均宽度只在一样高的里面挑,高度仍然优先:20 像素只有 10x20,不会为了字宽 6 退到 13 像素的 6x13。
        Assert.AreEqual(10, await OpenWidthAsync("-misc-fixed-medium-r-normal--20-200-75-75-c-60-iso10646-1"), "10x20");

        // Adobe 75 / 100 dpi 的 Helvetica、Times、Courier 随库带:Motif / Xaw / Tk 的默认字体不再 BadName。
        Assert.IsNotNull(await OpenWidthAsync("-adobe-helvetica-medium-r-normal--12-*-*-*-*-*-iso8859-1"), "原先 BadName(xs_plan CP-16)");
        Assert.IsNotNull(await OpenWidthAsync("-adobe-times-bold-i-normal--17-120-100-100-p-*-iso8859-1"));
        Assert.AreEqual(7, await OpenWidthAsync("-adobe-courier-medium-r-normal--*-120-75-75-*-*-iso8859-1"), "Courier 12 磅 75 dpi 的字宽");
        Assert.IsNotNull(await OpenWidthAsync("-adobe-helvetica-medium-r-normal--13-*-*-*-*-*-iso8859-1"), "没有 13 像素:退到 12 或 14");
        Assert.IsNotNull(await OpenWidthAsync("variable"), "fonts.alias:Helvetica Bold 12 磅");
        Assert.IsNull(await OpenWidthAsync("-b&h-lucida-medium-r-normal-sans-12-*-*-*-*-*-iso8859-1"), "没有随库带的字族照旧 BadName");
        Assert.IsNull(await OpenWidthAsync("kanji16"), "别名的目标(JIS 的 16 点阵)没有随库带:BadName");
    }

    [TestMethod]
    public async Task 字体的单字节字符集按映射表从ISO10646字体派生_中日韩有字形()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        async Task<uint> OpenAsync(string name)
        {
            uint font = c.NewId();
            byte[] bytes = Encoding.Latin1.GetBytes(name);
            await c.SendAsync(45, 0, b => b.U32(font).U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
            return font;
        }
        // 一个字符的宽度(QueryTextExtents 的 overall-width);缺字时退到 default-char,没有就是 0。
        async Task<int> WidthAsync(uint font, int code) =>
            (int)(await c.RequestAsync(48, 1, b => b.U32(font).U8((byte)(code >> 8)).U8((byte)code).Pad())).U32(16);   // odd-length:最后两字节是填充
        async Task<string> PropertyAsync(uint font, string name)
        {
            XMessage reply = await c.RequestAsync(47, 0, b => b.U32(font));
            uint atom = (await c.RequestAsync(16, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(Encoding.Latin1.GetBytes(name)).Pad())).U32(8);
            for (int i = 0; i < reply.U16(46); i++)
            {
                if (reply.U32(60 + (8 * i)) == atom)
                {
                    XMessage value = await c.RequestAsync(17, 0, b => b.U32(reply.U32(64 + (8 * i))));   // GetAtomName
                    return Encoding.Latin1.GetString(value.Bytes, 32, value.U16(8));
                }
            }
            return "";
        }

        uint latin2 = await OpenAsync("-misc-fixed-medium-r-normal--13-120-75-75-c-80-iso8859-2");
        Assert.AreEqual(8, await WidthAsync(latin2, 0xA1), "ISO8859-2 的 0xA1 是 Ą(U+0104)");
        Assert.AreEqual("ISO8859", await PropertyAsync(latin2, "CHARSET_REGISTRY"));
        Assert.AreEqual("2", await PropertyAsync(latin2, "CHARSET_ENCODING"));
        uint koi8 = await OpenAsync("-misc-fixed-medium-r-normal--13-120-75-75-c-80-koi8-r");
        Assert.AreEqual(8, await WidthAsync(koi8, 0xC1), "KOI8-R 的 0xC1 是 а(U+0430)");
        Assert.AreEqual("KOI8", await PropertyAsync(koi8, "CHARSET_REGISTRY"));

        uint unicode = await OpenAsync("-misc-fixed-medium-r-semicondensed--13-120-75-75-c-60-iso10646-1");
        Assert.AreEqual(6, await WidthAsync(unicode, 0x03B1), "希腊文 α:原先裁掉了");
        Assert.AreEqual(6, await WidthAsync(unicode, 0x0436), "西里尔文 ж");
        uint ja = await OpenAsync("-misc-fixed-medium-r-normal-ja-13-*-*-*-*-*-iso10646-1");
        Assert.AreEqual(12, await WidthAsync(ja, 0x65E5), "12x13ja 的 日");
        uint unifont = await OpenAsync("-gnu-unifont-*-iso10646-1");
        Assert.AreEqual(16, await WidthAsync(unifont, 0x4E2D), "Unifont 的 中");
        Assert.AreEqual(16, await WidthAsync(unifont, 0xAC00), "Unifont 的 가");
        Assert.AreEqual(8, await WidthAsync(unifont, 'A'));
        uint k14 = await OpenAsync("k14");
        Assert.AreEqual(14, await WidthAsync(k14, 0x3021), "JIS X 0208 的双字节字体:0x3021 是 亜");

        // 经别名打开:FONT 是别名指向的真名,字符集属性跟着这个名字(ISO10646-1 的 BDF 以 ISO8859-1 打开);
        // BDF 里带引号的 "1" 是字符串属性,按原子回。
        uint fixedFont = await OpenAsync("fixed");
        Assert.AreEqual("-misc-fixed-medium-r-semicondensed--13-120-75-75-c-60-iso8859-1", await PropertyAsync(fixedFont, "FONT"));
        Assert.AreEqual("ISO8859", await PropertyAsync(fixedFont, "CHARSET_REGISTRY"));
        Assert.AreEqual("1", await PropertyAsync(fixedFont, "CHARSET_ENCODING"));
        Assert.AreEqual("Fixed", await PropertyAsync(fixedFont, "FAMILY_NAME"));
    }

    [TestMethod]
    public async Task 字体还没建好时在后台建_这个客户端的请求按序暂存_别的客户端照常()
    {
        await using X11Server server = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> prepared = [];
        server.UnpreparedFonts = names => [.. names];   // 一律当成还没建好
        server.PrepareFonts = async (fonts, _) =>
        {
            await gate.Task;
            prepared.AddRange(fonts);
        };
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        uint font = a.NewId();
        byte[] name = Encoding.Latin1.GetBytes("fixed");
        await a.SendAsync(45, 0, w => w.U32(font).U16((ushort)name.Length).U16(0).Bytes(name).Pad());   // OpenFont
        Task<XMessage> query = a.RequestAsync(47, 0, w => w.U32(font));                                  // QueryFont,排在后面

        Assert.IsTrue((await b.RequestAsync(43, 0)).IsReply, "别的客户端照常:原先整个执行线程持着像素锁解析字体");
        await a.SendAsync(43, 0);
        await Task.Delay(100);
        Assert.IsFalse(query.IsCompleted, "字体建好之前,这个客户端之后的请求暂存");

        gate.SetResult();
        XMessage reply = await query;
        Assert.IsTrue(reply.IsReply, "建好之后按原顺序执行:OpenFont 在先,QueryFont 拿得到字体");
        CollectionAssert.AreEqual(new[] { "fixed" }, prepared);

        // 建不出来(后台抛异常)也放回去照常执行,不会一直等。
        server.PrepareFonts = (_, _) => Task.FromException(new InvalidDataException("坏数据"));
        uint other = a.NewId();
        await a.SendAsync(45, 0, w => w.U32(other).U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        Assert.IsTrue((await a.RequestAsync(47, 0, w => w.U32(other))).IsReply);
    }

    [TestMethod]
    public async Task ListFonts按通配符匹配()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        string pattern = "-misc-fixed-medium-r-*-*-13-*-*-*-*-*-iso10646-1";
        XMessage reply = await c.RequestAsync(49, 0, b => b.U16(10).U16((ushort)pattern.Length).Bytes(Encoding.Latin1.GetBytes(pattern)));
        List<string> names = [];
        for (int i = 0, offset = 32; i < reply.U16(8); i++, offset += 1 + reply.Bytes[offset])
        {
            names.Add(Encoding.Latin1.GetString(reply.Bytes, offset + 1, reply.Bytes[offset]));
        }
        CollectionAssert.AreEqual(new[]
        {
            "-misc-fixed-medium-r-normal--13-120-75-75-c-70-iso10646-1",      // 7x13
            "-misc-fixed-medium-r-normal--13-120-75-75-c-80-iso10646-1",      // 8x13
            "-misc-fixed-medium-r-normal-ja-13-120-75-75-c-120-iso10646-1",   // 12x13ja
            "-misc-fixed-medium-r-semicondensed--13-120-75-75-c-60-iso10646-1",   // 6x13(fixed)
        }, names);

        // 只给一个的时候也是按序第一个;max 截断。
        XMessage one = await c.RequestAsync(49, 0, b => b.U16(1).U16((ushort)pattern.Length).Bytes(Encoding.Latin1.GetBytes(pattern)));
        Assert.AreEqual(1, one.U16(8));
    }

    [TestMethod]
    public async Task 宿主注入的点击与按键按键码投递并带正确坐标()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, _) = await MapWindowAsync(c, host, 0, ButtonPressMask | KeyPressMask);

        server.InjectPointerButton(host.Mapped[win], 7, 9, 1, pressed: true);
        XMessage press = await c.NextEventAsync(4);
        Assert.AreEqual(1, press.Detail, "button 1");
        Assert.AreEqual(win, press.U32(12), "event window");
        Assert.AreEqual(27, press.I16(20), "root-x = 20 + 7");
        Assert.AreEqual(7, press.I16(24), "event-x");
        Assert.AreEqual(9, press.I16(26), "event-y");
        server.InjectPointerButton(host.Mapped[win], 7, 9, 1, pressed: false);

        server.FocusTopLevel(host.Mapped[win]);
        server.InjectKey(XKeycodes.A, pressed: true);
        XMessage key = await c.NextEventAsync(2);
        Assert.AreEqual(XKeycodes.A, key.Detail);

        XMessage map = await c.RequestAsync(101, 0, b => b.U8(XKeycodes.A).U8(1).U16(0));
        Assert.AreEqual('a', map.U32(32));
        Assert.AreEqual('A', map.U32(36));
    }

    [TestMethod]
    public async Task 宿主缩放窗口时客户端收到ConfigureNotify与Expose()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0xABCDEF, ExposureMask | StructureNotifyMask);
        await c.NextEventAsync(12);

        server.ResizeTopLevel(host.Mapped[win], 120, 90);
        XMessage configure = await c.NextEventAsync(22);
        Assert.AreEqual(120, configure.U16(20));
        Assert.AreEqual(90, configure.U16(22));
        await c.NextEventAsync(12);
        (uint[] px, int w, int h) = RecordingHost.Snapshot(handle);
        Assert.AreEqual((120, 90), (w, h));
        Assert.AreEqual(0xABCDEFu, px[^1]);
    }

    [TestMethod]
    public async Task 关闭按钮对声明了WM_DELETE_WINDOW的窗口发ClientMessage()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, _) = await MapWindowAsync(c, host, 0, 0);

        uint protocols = (await c.RequestAsync(16, 0, b => b.U16(12).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_PROTOCOLS")))).U32(8);
        uint delete = (await c.RequestAsync(16, 0, b => b.U16(16).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_DELETE_WINDOW")))).U32(8);
        await c.SendAsync(18, 0, b => b.U32(win).U32(protocols).U32(4).U8(32).U8(0).U8(0).U8(0).U32(1).U32(delete));
        await c.SyncAsync();

        server.CloseTopLevel(host.Mapped[win]);
        XMessage message = await c.NextEventAsync(33);
        Assert.AreNotEqual(0, message.Kind & 0x80, "SendEvent 合成的事件带 sent 位");
        Assert.AreEqual(protocols, message.U32(8));
        Assert.AreEqual(delete, message.U32(12));
    }

    [TestMethod]
    public async Task 关闭时对声明了NET_WM_PING的窗口发ping_回了不打扰宿主_不回就报无响应_宿主可以强制结束()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        server.PingTimeout = TimeSpan.FromMilliseconds(200);
        await using XTestClient c = await XTestClient.ConnectAsync(server, label: "joe@build:22");
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0, 0);
        Assert.AreEqual("joe@build:22", handle.Snapshot.ClientLabel);

        uint protocols = (await c.RequestAsync(16, 0, b => b.U16(12).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_PROTOCOLS")))).U32(8);
        uint delete = (await c.RequestAsync(16, 0, b => b.U16(16).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_DELETE_WINDOW")))).U32(8);
        uint ping = (await c.RequestAsync(16, 0, b => b.U16(12).U16(0).Bytes(Encoding.Latin1.GetBytes("_NET_WM_PING")))).U32(8);
        await c.SendAsync(18, 0, b => b.U32(win).U32(protocols).U32(4).U8(32).U8(0).U8(0).U8(0).U32(2).U32(delete).U32(ping));
        await c.SyncAsync();

        // 第一次:回了 ping(原样发回根窗口,窗口字段是根)—— 宿主收不到无响应。
        server.CloseTopLevel(handle);
        await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 33 && m.U32(12) == delete);
        XMessage request = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 33 && m.U32(12) == ping);
        Assert.AreEqual(win, request.U32(20), "data[2] 是窗口");
        await c.SendAsync(25, 0, b => b.U32(c.RootWindow).U32(0x180000)
            .U8(33).U8(32).U16(0).U32(c.RootWindow).U32(protocols).U32(ping).U32(request.U32(16)).U32(win).U32(0).U32(0));
        await Task.Delay(400);
        Assert.IsFalse(host.Requests.OfType<XNotRespondingRequest>().Any(), "回了 ping 就不报");

        // 第二次:程序卡住了,不回 —— 时限一过请宿主处理。
        server.CloseTopLevel(handle);
        await host.WaitForAsync(() => host.Requests.OfType<XNotRespondingRequest>().Any(r => r.Window == handle));

        IReadOnlyList<XClientInfo> clients = await server.GetClientsAsync();
        XClientInfo info = clients.Single(i => i.Label == "joe@build:22");
        Assert.AreEqual(handle.Snapshot.ClientId, info.Id);
        Assert.Contains(handle, info.TopLevels);
        Assert.IsGreaterThan(0, info.ResourceCount);

        server.KillTopLevelClient(handle);   // 用户确认强制结束
        await host.WaitForAsync(() => !host.Mapped.ContainsKey(win));
        Assert.IsFalse((await server.GetClientsAsync()).Any(i => i.Label == "joe@build:22"));
    }

    [TestMethod]
    public async Task 以Retain模式断开的客户端_关闭它的窗口与DisconnectClient都销毁它留下的资源()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0, 0);
        uint protocols = (await c.RequestAsync(16, 0, b => b.U16(12).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_PROTOCOLS")))).U32(8);
        uint delete = (await c.RequestAsync(16, 0, b => b.U16(16).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_DELETE_WINDOW")))).U32(8);
        await c.SendAsync(18, 0, b => b.U32(win).U32(protocols).U32(4).U8(32).U8(0).U8(0).U8(0).U32(1).U32(delete));
        await c.SendAsync(112, 1, _ => { });   // SetCloseDownMode(RetainPermanent)
        await c.SyncAsync();
        Task serving = c.ServerTask;
        await c.DisposeAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue((await server.GetClientsAsync()).Single().Retained);

        // 原先:WM_DELETE_WINDOW 发给已经关掉的连接被丢弃,窗口成了关不掉的僵尸。
        server.CloseTopLevel(handle);
        await host.WaitForAsync(() => !host.Mapped.ContainsKey(win));
        Assert.IsEmpty(await server.GetClientsAsync());
    }

    [TestMethod]
    public async Task 标题变化通知宿主()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0, 0);
        byte[] title = Encoding.Latin1.GetBytes("xterm");
        await c.SendAsync(18, 0, b => b.U32(win).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)title.Length).Bytes(title));
        await host.WaitForAsync(() => handle.Snapshot.Title == "xterm");
    }

    [TestMethod]
    public async Task SendEvent不许发GenericEvent与没登记的事件码_收件人的协议流不会错位()
    {
        await using X11Server server = new();
        await using XTestClient sender = await XTestClient.ConnectAsync(server);
        await using XTestClient receiver = await XTestClient.ConnectAsync(server);
        uint window = receiver.NewId();
        await receiver.SendAsync(1, 0, b => b.U32(window).U32(receiver.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await receiver.SyncAsync();

        // GenericEvent:长度字段写 1000 —— 收件人会以为后面还有 4000 字节,把之后的回复、事件都当成它的一部分。
        byte[] generic = new byte[32];
        generic[0] = 35;
        generic[4] = 0xE8;
        generic[5] = 0x03;
        XMessage refused = await sender.RequestAsync(25, 0, b => b.U32(window).U32(0).Bytes(generic));
        Assert.IsTrue(refused.IsError);
        Assert.AreEqual(2, refused.Detail, "BadValue");

        byte[] unassigned = new byte[32];
        unassigned[0] = 50;   // 核心的 36–63 没有定义
        XMessage alsoRefused = await sender.RequestAsync(25, 0, b => b.U32(window).U32(0).Bytes(unassigned));
        Assert.AreEqual(2, alsoRefused.Detail, "BadValue");

        byte[] clientMessage = new byte[32];
        clientMessage[0] = 33;
        clientMessage[1] = 32;
        BitConverter.GetBytes(window).CopyTo(clientMessage, 4);
        await sender.SendAsync(25, 0, b => b.U32(window).U32(0).Bytes(clientMessage));   // 空掩码:发给窗口的创建者
        await sender.SyncAsync();
        XMessage delivered = await receiver.NextEventAsync(33);
        Assert.AreEqual(window, delivered.U32(4));
        XMessage focus = await receiver.RequestAsync(43, 0);
        Assert.IsTrue(focus.IsReply, "收件人的协议流没有错位");
    }

    [TestMethod]
    public async Task 顶层窗口没了之后宿主注入的松开照样生效_按钮不会一直按着()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0, 0x4);   // ButtonPress:按下时自动抓取
        server.InjectPointerButton(handle, 5, 5, 1, pressed: true);
        await c.NextEventAsync(4);

        await c.SendAsync(4, 0, b => b.U32(win));   // 弹出菜单一点就关:窗口在按钮松开之前销毁
        await c.SyncAsync();
        server.InjectPointerButton(handle, 5, 5, 1, pressed: false);
        XMessage pointer = await c.RequestAsync(38, 0, b => b.U32(c.RootWindow));   // QueryPointer
        Assert.AreEqual(0, pointer.U16(24) & 0x100, "Button1 松开了");
    }

    [TestMethod]
    public async Task CirculateNotify的place在第16字节()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint parent, _) = await MapWindowAsync(c, host, 0, 0x80000);   // SubstructureNotify
        uint lower = c.NewId(), upper = c.NewId();
        foreach (uint child in (uint[])[lower, upper])
        {
            await c.SendAsync(1, 0, b => b.U32(child).U32(parent).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
            await c.SendAsync(8, 0, b => b.U32(child));
        }

        await c.SendAsync(13, 1, b => b.U32(parent));   // CirculateWindow LowerHighest:最上面的 upper 沉到底
        XMessage lowered = await c.NextEventAsync(26);
        Assert.AreEqual(parent, lowered.U32(4), "event");
        Assert.AreEqual(upper, lowered.U32(8), "window");
        Assert.AreEqual(1, lowered.Bytes[16], "place = Bottom");

        await c.SendAsync(13, 0, b => b.U32(parent));   // RaiseLowest:又浮上来
        XMessage raised = await c.NextEventAsync(26);
        Assert.AreEqual(0, raised.Bytes[16], "place = Top");
    }
}
