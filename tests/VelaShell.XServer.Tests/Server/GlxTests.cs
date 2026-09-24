using System.Text;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>GLX:配置与版本查询、直接 / 间接上下文、glXRender 的软件渲染(清除、三角形、深度、显示列表、纹理)、RenderLarge、GL 查询。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class GlxTests
{
    private const uint RootVisual = 0x21;
    private const int Width = 60, Height = 40;

    // GL 枚举
    private const uint Triangles = 4, Quads = 7, ColorBit = 0x4000, DepthBit = 0x100, DepthTest = 0x0B71, Texture2D = 0x0DE1,
        MinFilter = 0x2801, MagFilter = 0x2800, Nearest = 0x2600, Rgba = 0x1908, UnsignedByte = 0x1401;

    private static async Task<byte> GlxAsync(XTestClient c)
    {
        byte[] name = Encoding.Latin1.GetBytes("GLX");
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        Assert.AreEqual(1, q.Bytes[8], "GLX 在扩展列表里");
        return q.Bytes[9];
    }

    private static async Task<uint> MapWindowAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(Width).U16(Height).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    /// <summary>建间接上下文、绑到窗口上,返回 (上下文, 标签)。</summary>
    private static async Task<(uint Context, uint Tag)> CurrentAsync(XTestClient c, byte glx, uint window)
    {
        uint context = c.NewId();
        await c.SendAsync(glx, 3, b => b.U32(context).U32(RootVisual).U32(0).U32(0).U8(0).U8(0).U16(0));   // CreateContext,间接
        XMessage made = await c.RequestAsync(glx, 5, b => b.U32(window).U32(context).U32(0));             // MakeCurrent
        Assert.IsFalse(made.IsError, "MakeCurrent");
        uint tag = made.U32(8);
        Assert.AreNotEqual(0u, tag);
        return (context, tag);
    }

    /// <summary>拼一串渲染命令(每条 2 字节长度含头、2 字节操作码,补齐到 4 字节)。</summary>
    private sealed class Commands
    {
        private readonly List<byte> _bytes = [];

        public Commands Add(ushort opcode, Action<XTestClient.Body>? parameters = null)
        {
            XTestClient.Body p = new(bigEndian: false);
            parameters?.Invoke(p);
            p.Pad();
            byte[] body = p.ToArray();
            int length = 4 + body.Length;
            _bytes.AddRange(BitConverter.GetBytes((ushort)length));
            _bytes.AddRange(BitConverter.GetBytes(opcode));
            _bytes.AddRange(body);
            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private static XTestClient.Body F(XTestClient.Body b, params float[] values)
    {
        foreach (float v in values)
        {
            b.U32(BitConverter.SingleToUInt32Bits(v));
        }
        return b;
    }

    private static Task<ushort> RenderAsync(XTestClient c, byte glx, uint tag, Commands commands) =>
        c.SendAsync(glx, 1, b => b.U32(tag).Bytes(commands.ToArray()));

    /// <summary>X 窗口坐标 (x, y) 的像素(RGB)。</summary>
    private static async Task<uint> PixelAsync(XTestClient c, uint window, int x, int y)
    {
        XMessage image = await c.RequestAsync(73, 2, b => b.U32(window).I16((short)x).I16((short)y).U16(1).U16(1).U32(0xFFFFFFFF));
        Assert.IsFalse(image.IsError);
        return image.U32(32) & 0xFFFFFF;
    }

    [TestMethod]
    public async Task 版本服务端串与配置列表()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);

        XMessage version = await c.RequestAsync(glx, 7, b => b.U32(1).U32(4));
        Assert.AreEqual(1u, version.U32(8));
        Assert.AreEqual(4u, version.U32(12));

        XMessage vendor = await c.RequestAsync(glx, 19, b => b.U32(0).U32(1));   // QueryServerString(GLX_VENDOR)
        int n = (int)vendor.U32(12);
        Assert.AreEqual("VelaShell\0", Encoding.Latin1.GetString(vendor.Bytes, 32, n), "串带结尾的 NUL");

        XMessage configs = await c.RequestAsync(glx, 21, b => b.U32(0));        // GetFBConfigs
        uint count = configs.U32(8), properties = configs.U32(12);
        Assert.AreEqual(4u, count);
        Assert.AreEqual(count * properties * 2, configs.U32(4), "reply length = 2 × 配置数 × 属性数");
        Assert.AreEqual(0x8013u, configs.U32(32), "第一对是 GLX_FBCONFIG_ID");

        XMessage visuals = await c.RequestAsync(glx, 14, b => b.U32(0));        // GetVisualConfigs
        Assert.AreEqual(2u, visuals.U32(8), "每个 TrueColor 视觉一条");
        Assert.AreEqual(RootVisual, visuals.U32(32));

        XMessage badScreen = await c.RequestAsync(glx, 21, b => b.U32(1));
        Assert.IsTrue(badScreen.IsError);
        Assert.AreEqual(2, badScreen.Bytes[1], "屏幕不存在:BadValue");
    }

    [TestMethod]
    public async Task 直接上下文只做登记()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint context = c.NewId();
        await c.SendAsync(glx, 3, b => b.U32(context).U32(RootVisual).U32(0).U32(0).U8(1).U8(0).U16(0));
        XMessage direct = await c.RequestAsync(glx, 6, b => b.U32(context));   // IsDirect
        Assert.AreEqual(1, direct.Bytes[8]);
        XMessage query = await c.RequestAsync(glx, 25, b => b.U32(context));   // QueryContext
        Assert.AreEqual(3u, query.U32(8), "FBCONFIG_ID、RENDER_TYPE、SCREEN");
        Assert.AreEqual(0x8013u, query.U32(32));
        Assert.AreEqual(0x101u, query.U32(36), "视觉 0x21 的双缓冲配置");
    }

    [TestMethod]
    public async Task 间接渲染_清除与三角形_交换后出现在窗口里()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        await RenderAsync(c, glx, tag, new Commands()
            .Add(130, b => F(b, 0, 0, 1, 1))                           // ClearColor 蓝
            .Add(127, b => b.U32(ColorBit | DepthBit))                 // Clear
            .Add(8, b => F(b, 1, 0, 0))                                // Color3fv 红
            .Add(4, b => b.U32(Triangles))                             // Begin
            .Add(66, b => F(b, -1, -1)).Add(66, b => F(b, 1, -1)).Add(66, b => F(b, 0, 1))   // Vertex2fv
            .Add(23));                                                 // End
        Assert.AreEqual(0u, await PixelAsync(c, window, Width / 2, Height / 2), "双缓冲:交换之前窗口里还没有");
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));       // SwapBuffers
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, Width / 2, Height / 2), "三角形");
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 0, 0), "三角形外是清除色");

        XMessage error = await c.RequestAsync(glx, 115, b => b.U32(tag));   // GetError
        Assert.AreEqual(0u, error.U32(8));
        XMessage bad = await c.RequestAsync(glx, 115, b => b.U32(tag + 100));
        Assert.IsTrue(bad.IsError);
        Assert.AreEqual(150 + 4, bad.Bytes[1], "GLXBadContextTag");
    }

    [TestMethod]
    public async Task 深度测试与显示列表()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        XMessage lists = await c.RequestAsync(glx, 104, b => b.U32(tag).I32(1));   // GenLists
        uint list = lists.U32(8);
        Assert.AreNotEqual(0u, list);
        // 列表:一个画在远处(z = 0.5)的红色四边形。COMPILE 模式下编译时不画。
        await c.SendAsync(glx, 101, b => b.U32(tag).U32(list).U32(0x1300));       // NewList
        await RenderAsync(c, glx, tag, new Commands()
            .Add(8, b => F(b, 1, 0, 0))
            .Add(4, b => b.U32(Quads))
            .Add(70, b => F(b, -1, -1, 0.5f)).Add(70, b => F(b, 1, -1, 0.5f)).Add(70, b => F(b, 1, 1, 0.5f)).Add(70, b => F(b, -1, 1, 0.5f))
            .Add(23));
        await c.SendAsync(glx, 102, b => b.U32(tag));                              // EndList
        XMessage isList = await c.RequestAsync(glx, 141, b => b.U32(tag).U32(list));
        Assert.AreEqual(1u, isList.U32(8));

        await RenderAsync(c, glx, tag, new Commands()
            .Add(139, b => b.U32(DepthTest))
            .Add(127, b => b.U32(ColorBit | DepthBit))
            .Add(8, b => F(b, 0, 1, 0))                                // 近处(z = -0.5)的绿色,盖住左半边
            .Add(4, b => b.U32(Quads))
            .Add(70, b => F(b, -1, -1, -0.5f)).Add(70, b => F(b, 0, -1, -0.5f)).Add(70, b => F(b, 0, 1, -0.5f)).Add(70, b => F(b, -1, 1, -0.5f))
            .Add(23)
            .Add(1, b => b.U32(list)));                                // CallList:远处的红色在后画,只在右半边露出
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));
        Assert.AreEqual(0x00FF00u, await PixelAsync(c, window, 10, 20), "近处的绿色挡住后画的红色");
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 50, 20), "右半边是列表画的红色");
    }

    [TestMethod]
    public async Task 纹理映射_经RenderLarge上传()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        XMessage names = await c.RequestAsync(glx, 145, b => b.U32(tag).I32(1));   // GenTextures
        uint texture = names.U32(32);
        await RenderAsync(c, glx, tag, new Commands()
            .Add(4117, b => b.U32(Texture2D).U32(texture))             // BindTexture
            .Add(107, b => b.U32(Texture2D).U32(MinFilter).U32(Nearest))
            .Add(107, b => b.U32(Texture2D).U32(MagFilter).U32(Nearest)));

        // TexImage2D 2×2 RGBA(第 0 行在下):红 绿 / 蓝 白。拆成三条 RenderLarge:小参数一条,图像两条。
        byte[] image = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        XTestClient.Body small = new(bigEndian: false);
        small.U8(0).U8(0).U16(0).U32(0).U32(0).U32(0).U32(4)            // swap、lsb、row length、skip rows、skip pixels、alignment
            .U32(Texture2D).I32(0).U32(Rgba).I32(2).I32(2).I32(0).U32(Rgba).U32(UnsignedByte);
        byte[] smallBytes = small.ToArray();
        await c.SendAsync(glx, 2, b => b.U32(tag).U16(1).U16(3).U32((uint)smallBytes.Length)
            .U32((uint)(8 + smallBytes.Length + image.Length)).U32(110).Bytes(smallBytes));
        await c.SendAsync(glx, 2, b => b.U32(tag).U16(2).U16(3).U32(8).Bytes(image[..8]));
        await c.SendAsync(glx, 2, b => b.U32(tag).U16(3).U16(3).U32(8).Bytes(image[8..]));

        await RenderAsync(c, glx, tag, new Commands()
            .Add(139, b => b.U32(Texture2D))
            .Add(127, b => b.U32(ColorBit))
            .Add(4, b => b.U32(Quads))
            .Add(54, b => F(b, 0, 0)).Add(66, b => F(b, -1, -1))          // TexCoord2fv + Vertex2fv
            .Add(54, b => F(b, 1, 0)).Add(66, b => F(b, 1, -1))
            .Add(54, b => F(b, 1, 1)).Add(66, b => F(b, 1, 1))
            .Add(54, b => F(b, 0, 1)).Add(66, b => F(b, -1, 1))
            .Add(23));
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 5, Height - 5), "左下:红");
        Assert.AreEqual(0x00FF00u, await PixelAsync(c, window, Width - 5, Height - 5), "右下:绿");
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 5, 5), "左上:蓝");
        Assert.AreEqual(0xFFFFFFu, await PixelAsync(c, window, Width - 5, 5), "右上:白");
    }

    [TestMethod]
    public async Task GL查询与ReadPixels()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        XMessage viewport = await c.RequestAsync(glx, 117, b => b.U32(tag).U32(0x0BA2));   // GetIntegerv(VIEWPORT)
        Assert.AreEqual(4u, viewport.U32(12));
        Assert.AreEqual((uint)Width, viewport.U32(40), "第一次成为当前时视口是窗口尺寸");
        Assert.AreEqual((uint)Height, viewport.U32(44));

        XMessage lights = await c.RequestAsync(glx, 117, b => b.U32(tag).U32(0x0D31));     // MAX_LIGHTS:n = 1 时值在第 16 字节
        Assert.AreEqual(1u, lights.U32(12));
        Assert.AreEqual(8u, lights.U32(16));

        XMessage versionString = await c.RequestAsync(glx, 129, b => b.U32(tag).U32(0x1F02));   // GetString(VERSION)
        Assert.StartsWith("1.1", Encoding.Latin1.GetString(versionString.Bytes, 32, (int)versionString.U32(12)));

        await c.RequestAsync(glx, 117, b => b.U32(tag).U32(0xFFFF));                         // 不认识的 pname
        XMessage error = await c.RequestAsync(glx, 115, b => b.U32(tag));
        Assert.AreEqual(0x0500u, error.U32(8), "INVALID_ENUM");

        await RenderAsync(c, glx, tag, new Commands()
            .Add(126, b => b.U32(0x0404))                               // DrawBuffer(FRONT):直接进窗口
            .Add(130, b => F(b, 0.2f, 0.4f, 0.6f, 1))
            .Add(127, b => b.U32(ColorBit)));
        Assert.AreEqual(0x336699u, await PixelAsync(c, window, 3, 3), "画前缓冲不用交换,请求处理完就拷进窗口");
        XMessage pixels = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(0).I32(0).I32(1).I32(1)
            .U32(Rgba).U32(UnsignedByte).U8(0).U8(0).U16(0));          // ReadPixels(读缓冲仍是 BACK:里面是 0)
        Assert.AreEqual(1u, pixels.U32(4), "一行补齐到 4 字节");
        await RenderAsync(c, glx, tag, new Commands().Add(171, b => b.U32(0x0404)));   // ReadBuffer(FRONT)
        pixels = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(0).I32(0).I32(1).I32(1).U32(Rgba).U32(UnsignedByte).U8(0).U8(0).U16(0));
        CollectionAssert.AreEqual(new byte[] { 0x33, 0x66, 0x99, 0xFF }, pixels.Bytes[32..36]);
    }

    [TestMethod]
    public async Task 显示列表互相调用不会卡住执行线程()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        // 列表 L 调用自己两次:嵌套上限 64 层,不设预算就是 2^64 次展开。
        uint list = (await c.RequestAsync(glx, 104, b => b.U32(tag).I32(1))).U32(8);
        await c.SendAsync(glx, 101, b => b.U32(tag).U32(list).U32(0x1300));
        await RenderAsync(c, glx, tag, new Commands().Add(1, b => b.U32(list)).Add(1, b => b.U32(list)));
        await c.SendAsync(glx, 102, b => b.U32(tag));
        await RenderAsync(c, glx, tag, new Commands().Add(1, b => b.U32(list)));
        XMessage error = await c.RequestAsync(glx, 115, b => b.U32(tag));
        Assert.AreEqual(0x0505u, error.U32(8), "超出预算:OUT_OF_MEMORY");
    }
}
