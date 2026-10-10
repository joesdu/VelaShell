using System.Text;
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
        Assert.AreEqual(6u, count, "双缓冲 / 单缓冲 × 24 / 32 位,另加两个 4 倍多重采样的双缓冲配置");
        Assert.AreEqual(count * properties * 2, configs.U32(4), "reply length = 2 × 配置数 × 属性数");
        Assert.AreEqual(0x8013u, configs.U32(32), "第一对是 GLX_FBCONFIG_ID");

        XMessage visuals = await c.RequestAsync(glx, 14, b => b.U32(0));        // GetVisualConfigs
        Assert.AreEqual(6u, visuals.U32(8), "每个 TrueColor 视觉发布双缓冲、单缓冲与多重采样配置");
        Assert.AreEqual(RootVisual, visuals.U32(32));
        int visualConfigBytes = checked((int)visuals.U32(12) * 4);
        int secondVisual = 32 + visualConfigBytes;
        Assert.AreEqual(1u, visuals.U32(32 + 11 * 4), "第一个视觉保留双缓冲");
        Assert.AreEqual(RootVisual, visuals.U32(secondVisual));
        Assert.AreEqual(0u, visuals.U32(secondVisual + 11 * 4), "第二条配置是单缓冲");
        Assert.AreEqual(0x102u, visuals.U32(secondVisual + 27 * 4), "第二条配置关联单缓冲 FBConfig");

        XMessage badScreen = await c.RequestAsync(glx, 21, b => b.U32(1));
        Assert.IsTrue(badScreen.IsError);
        Assert.AreEqual(2, badScreen.Bytes[1], "屏幕不存在:BadValue");
    }

    /// <summary>
    /// 多重采样 FBConfig 与 GLX_EXT_libglvnd(xs_plan F22):两个 4 倍多重采样的双缓冲配置(SAMPLE_BUFFERS 1、SAMPLES 4);
    /// QueryServerString(GLX_VENDOR_NAMES_EXT)报 mesa;GetDrawableAttributes 的回复带 GLX_SCREEN;扩展串里列出 GLX_EXT_libglvnd。
    /// </summary>
    [TestMethod]
    public async Task 多重采样配置_libglvnd的厂商名与可绘对象的屏幕()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        static string Text(XMessage reply) => Encoding.Latin1.GetString(reply.Bytes, 32, (int)reply.U32(12)).TrimEnd('\0');
        static Dictionary<uint, uint> Pairs(XMessage reply, int at, uint count)
        {
            Dictionary<uint, uint> pairs = [];
            for (int i = 0; i < count; i++)
            {
                pairs[reply.U32(at + (8 * i))] = reply.U32(at + (8 * i) + 4);
            }
            return pairs;
        }

        Assert.AreEqual("mesa", Text(await c.RequestAsync(glx, 19, b => b.U32(0).U32(0x20F6))), "GLX_VENDOR_NAMES_EXT");
        Assert.Contains("GLX_EXT_libglvnd", Text(await c.RequestAsync(glx, 19, b => b.U32(0).U32(3))));

        XMessage configs = await c.RequestAsync(glx, 21, b => b.U32(0));
        uint count = configs.U32(8), properties = configs.U32(12);
        List<Dictionary<uint, uint>> all = [.. Enumerable.Range(0, (int)count).Select(i => Pairs(configs, 32 + (int)(i * properties * 8), properties))];
        List<Dictionary<uint, uint>> multisample = [.. all.Where(cfg => cfg[100000] == 1)];   // GLX_SAMPLE_BUFFERS
        Assert.HasCount(2, multisample);
        Assert.IsTrue(multisample.All(cfg => cfg[100001] == 4 && cfg[5] == 1), "4 倍、双缓冲");   // GLX_SAMPLES、GLX_DOUBLEBUFFER
        CollectionAssert.AreEquivalent(new uint[] { 24, 32 }, multisample.Select(cfg => cfg[2]).ToArray(), "24 / 32 位各一个");   // GLX_BUFFER_SIZE

        uint window = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        XMessage attributes = await c.RequestAsync(glx, 29, b => b.U32(window));   // GetDrawableAttributes(GLX 1.2 的窗口)
        Assert.AreEqual(0u, Pairs(attributes, 32, attributes.U32(8))[0x800C], "GLX_SCREEN");
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
        Assert.AreEqual(151 + 4, bad.Bytes[1], "GLXBadContextTag");
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
        Assert.AreSequenceEqual(new byte[] { 0x33, 0x66, 0x99, 0xFF }, pixels.Bytes[32..36]);
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

    private const uint Compile = 0x1300, UnsignedByteType = 0x1401, Color = 0x1800, OutOfMemory = 0x0505, InvalidValue = 0x0501;

    private static async Task<uint> GlErrorAsync(XTestClient c, byte glx, uint tag) => (await c.RequestAsync(glx, 115, b => b.U32(tag))).U32(8);

    [TestMethod]
    public async Task 显示列表的每次调用都计入展开预算_调一百万个空列表也会停下()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        uint first = (await c.RequestAsync(glx, 104, b => b.U32(tag).I32(2))).U32(8);   // GenLists:first 是 A,first + 1 是空列表 E
        Assert.IsLessThan(256u, first + 1, "列表名装得进一个字节");

        // A = CallLists(6 万个 E):E 是空的,旧实现里调它一条都不计。
        const int n = 60000;
        byte empty = (byte)(first + 1);
        await c.SendAsync(glx, 101, b => b.U32(tag).U32(first).U32(Compile));
        await RenderAsync(c, glx, tag, new Commands().Add(2, b => b.I32(n).U32(UnsignedByteType).Bytes([.. Enumerable.Repeat(empty, n)])));
        await c.SendAsync(glx, 102, b => b.U32(tag));

        // 再调 6 万次 A:一共 36 亿次调用。
        byte a = (byte)first;
        await RenderAsync(c, glx, tag, new Commands().Add(2, b => b.I32(n).U32(UnsignedByteType).Bytes([.. Enumerable.Repeat(a, n)])));
        Assert.AreEqual(OutOfMemory, await GlErrorAsync(c, glx, tag), "预算用完,记 OUT_OF_MEMORY");
    }

    [TestMethod]
    public async Task 显示列表按真正的工作量计费_调很多次清屏会停下并记OUT_OF_MEMORY()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host) { RequestWorkBudget = 2_000_000 };
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        uint list = (await c.RequestAsync(glx, 104, b => b.U32(tag).I32(1))).U32(8);
        Assert.IsLessThan(256u, list);

        // 列表里只有一条 Clear(COLOR | DEPTH):条数只算 1,工作量却是整个表面。
        await c.SendAsync(glx, 101, b => b.U32(tag).U32(list).U32(Compile));
        await RenderAsync(c, glx, tag, new Commands().Add(127, b => b.U32(0x4100)));
        await c.SendAsync(glx, 102, b => b.U32(tag));

        // 调 6 万次:按条数远没到 400 万的上限,按工作量早就超了。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int n = 60000;
        await RenderAsync(c, glx, tag, new Commands().Add(2, b => b.I32(n).U32(UnsignedByteType).Bytes([.. Enumerable.Repeat((byte)list, n)])));
        Assert.AreEqual(OutOfMemory, await GlErrorAsync(c, glx, tag), "工作量花光:OUT_OF_MEMORY");
        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds);

        // 下一个请求有新的预算:照常执行。
        await RenderAsync(c, glx, tag, new Commands().Add(1, b => b.U32(list)));
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
    }

    [TestMethod]
    public async Task GenLists与DeleteLists的range到2的31次方也立即返回()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        XMessage huge = await c.RequestAsync(glx, 104, b => b.U32(tag).I32(int.MaxValue));
        Assert.AreEqual(0u, huge.U32(8), "名字不够:不生成任何名字,返回 0");
        uint list = (await c.RequestAsync(glx, 104, b => b.U32(tag).I32(3))).U32(8);
        Assert.AreNotEqual(0u, list);

        await c.SendAsync(glx, 103, b => b.U32(tag).U32(0).I32(int.MaxValue));        // DeleteLists [0, 2^31)
        XMessage isList = await c.RequestAsync(glx, 141, b => b.U32(tag).U32(list));
        Assert.AreEqual(0u, isList.U32(8), "删掉了");
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
    }

    [TestMethod]
    public async Task DrawArrays不给数组时不空转_线宽夹到上限()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        await RenderAsync(c, glx, tag, new Commands()
            .Add(193, b => b.I32(int.MaxValue).I32(0).U32(Triangles).U32(0).U32(0))    // DrawArrays:20 亿个顶点、0 个数组,后面还有几个字节
            .Add(95, b => F(b, 1e9f))                                                  // LineWidth 10 亿
            .Add(8, b => F(b, 1, 1, 0))
            .Add(4, b => b.U32(1))                                                     // Begin(LINES)
            .Add(66, b => F(b, -1, 0)).Add(66, b => F(b, 1, 0))
            .Add(23));
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));
        Assert.AreEqual(0xFFFF00u, await PixelAsync(c, window, Width / 2, (Height / 2) - 10), "线宽夹到 64:整个窗口高度都盖住了");
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
    }

    /// <summary>DrawPixels 的参数:像素存储头(默认)、宽高、格式、类型,再是数据。</summary>
    private static XTestClient.Body DrawPixels(XTestClient.Body b, int width, int height, byte[] rgba) =>
        b.U8(0).U8(0).U16(0).I32(0).I32(0).I32(0).I32(4).I32(width).I32(height).U32(Rgba).U32(UnsignedByte).Bytes(rgba);

    [TestMethod]
    public async Task DrawPixels各行在数据里重叠_极小的放大倍数_只解码盖得住像素中心的源像素()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        // ROW_LENGTH = 1、对齐 1、LUMINANCE / UNSIGNED_BYTE:各行在数据里重叠,8000 × 8000 只要 15999 字节。
        const int size = 8000;
        byte[] data = new byte[size + size - 1];
        Array.Fill(data, (byte)0x80);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await RenderAsync(c, glx, tag, new Commands()
            .Add(34, b => F(b, -1, -1))                                                // 窗口 (0, 0)
            .Add(165, b => F(b, 1e-6f, 1e-6f))                                         // PixelZoom:整张图缩成不到一个像素
            .Add(173, b => b.U8(0).U8(0).U16(0).I32(1).I32(0).I32(0).I32(1).I32(size).I32(size).U32(0x1909).U32(UnsignedByte).Bytes(data)));
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        Assert.IsLessThan(2_000, watch.ElapsedMilliseconds, "原先逐个解码 6400 万个源像素");
    }

    [TestMethod]
    public async Task DrawPixels与CopyPixels按放大倍数画出_只走裁剪范围里的那部分()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        // 2×2:下面一行红、绿,上面一行蓝、白;从左下角 (0, 0) 起放大 10 倍。
        byte[] image = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        await RenderAsync(c, glx, tag, new Commands()
            .Add(127, b => b.U32(ColorBit))
            .Add(34, b => F(b, -1, -1))                                                // RasterPos2fv → 窗口 (0, 0)
            .Add(165, b => F(b, 10, 10))                                               // PixelZoom
            .Add(173, b => DrawPixels(b, 2, 2, image))
            .Add(34, b => F(b, 0, -1))                                                 // 窗口 (30, 0)
            .Add(165, b => F(b, 1, 1))
            .Add(172, b => b.I32(0).I32(0).I32(20).I32(20).U32(Color))                 // CopyPixels 左下 20×20
            .Add(34, b => F(b, -1, -1))
            .Add(172, b => b.I32(0).I32(0).I32(int.MaxValue).I32(int.MaxValue).U32(Color)));   // 巨大的源:只拷缓冲里有的
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));

        // X 的 y 向下:GL 窗口 y = 5 是 X 的第 34 行。
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 5, 34));
        Assert.AreEqual(0x00FF00u, await PixelAsync(c, window, 15, 34));
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 5, 24));
        Assert.AreEqual(0xFFFFFFu, await PixelAsync(c, window, 15, 24));
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 35, 34), "CopyPixels 拷到 (30, 0)");
        Assert.AreEqual(0xFFFFFFu, await PixelAsync(c, window, 45, 24));
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
    }

    [TestMethod]
    public async Task 像素矩形与位图的数据装不下声明的尺寸时作废()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        await RenderAsync(c, glx, tag, new Commands()
            .Add(34, b => F(b, -1, -1))
            .Add(173, b => DrawPixels(b, 100000, 100000, [1, 2, 3, 4])));             // 声称 10^10 个像素,只带 4 字节
        Assert.AreEqual(InvalidValue, await GlErrorAsync(c, glx, tag));

        await RenderAsync(c, glx, tag, new Commands()
            .Add(5, b => F(b.U8(0).U8(0).U16(0).I32(0).I32(0).I32(0).I32(1).I32(100000).I32(100000), 0, 0, 0, 0).U32(0xFFFFFFFF)));   // Bitmap
        Assert.AreEqual(InvalidValue, await GlErrorAsync(c, glx, tag));
    }

    [TestMethod]
    public async Task Begin与End之间的顶点有上限_超了记OUT_OF_MEMORY()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        await RenderAsync(c, glx, tag, new Commands().Add(4, b => b.U32(0)));   // Begin(POINTS)
        const int perRequest = 20000;
        for (int sent = 0; sent <= Gl.GlContext.MaxPrimitiveVertices; sent += perRequest)
        {
            Commands vertices = new();
            for (int i = 0; i < perRequest; i++)
            {
                vertices.Add(66, b => F(b, 0, 0));
            }
            await RenderAsync(c, glx, tag, vertices);
        }
        await RenderAsync(c, glx, tag, new Commands().Add(23));                  // End
        Assert.AreEqual(OutOfMemory, await GlErrorAsync(c, glx, tag));
    }

    [TestMethod]
    public async Task PrioritizeTextures声称的个数不按它分配_ReadPixels的回复大小有上限()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        await RenderAsync(c, glx, tag, new Commands().Add(4118, b => b.I32(int.MaxValue)));   // n = 2^31 − 1,后面没有数据
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        Assert.IsFalse(await c.NextAsync(m => m.IsError, 100).ContinueWith(t => t.IsCompletedSuccessfully), "没有 BadImplementation 之类的错误");

        // 8000 × 8000 的 RGBA FLOAT:1 GB 的回复。
        XMessage huge = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(0).I32(0).I32(8000).I32(8000).U32(Rgba).U32(0x1406).U8(0).U8(0).U16(0));
        Assert.IsTrue(huge.IsError);
        Assert.AreEqual(11, huge.Bytes[1], "BadAlloc");
        XMessage small = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(0).I32(0).I32(2).I32(2).U32(Rgba).U32(UnsignedByte).U8(0).U8(0).U16(0));
        Assert.IsTrue(small.IsReply, "正常大小照常回");
    }

    private const uint SingleBufferedRgb = 0x102, RgbaType = 0x8014;

    [TestMethod]
    public async Task 像素图释放后同一个ID的新像素图拿到新表面_不会读到上一个的内容()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint pixmap = c.NewId(), glxPixmap = c.NewId(), context = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(8).U16(8));
        await c.SendAsync(glx, 22, b => b.U32(0).U32(SingleBufferedRgb).U32(pixmap).U32(glxPixmap).U32(0));        // CreatePixmap(GLX 1.3)
        await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        uint tag = (await c.RequestAsync(glx, 26, b => b.U32(0).U32(glxPixmap).U32(glxPixmap).U32(context))).U32(8);
        await RenderAsync(c, glx, tag, new Commands().Add(130, b => F(b, 1, 0, 0, 1)).Add(127, b => b.U32(ColorBit)));
        XMessage red = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(0).I32(0).I32(1).I32(1).U32(Rgba).U32(UnsignedByte).U8(0).U8(0).U16(0));
        Assert.AreEqual(255, red.Bytes[32], "先画成红色");

        await c.RequestAsync(glx, 26, b => b.U32(tag).U32(0).U32(0).U32(0));                                          // 放下当前上下文
        await c.SendAsync(glx, 23, b => b.U32(glxPixmap));                                                              // DestroyPixmap(GLX)
        await c.SendAsync(54, 0, b => b.U32(pixmap));                                                                   // FreePixmap
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(8).U16(8));                                 // 同一个 ID 建新像素图
        await c.SendAsync(glx, 22, b => b.U32(0).U32(SingleBufferedRgb).U32(pixmap).U32(glxPixmap).U32(0));
        uint again = (await c.RequestAsync(glx, 26, b => b.U32(0).U32(glxPixmap).U32(glxPixmap).U32(context))).U32(8);
        XMessage fresh = await c.RequestAsync(glx, 111, b => b.U32(again).I32(0).I32(0).I32(1).I32(1).U32(Rgba).U32(UnsignedByte).U8(0).U8(0).U16(0));
        Assert.AreEqual(0, fresh.Bytes[32], "新像素图的表面是新的,不是上一个画过的红色");
    }

    [TestMethod]
    public async Task 每轮新建像素图画一次再释放_表面随FreePixmap释放_不攒到客户端断开()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint context = c.NewId();
        await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        for (int round = 0; round < 5; round++)
        {
            uint pixmap = c.NewId(), glxPixmap = c.NewId();   // XID 递增,很久才重用
            await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(64).U16(64));
            await c.SendAsync(glx, 22, b => b.U32(0).U32(SingleBufferedRgb).U32(pixmap).U32(glxPixmap).U32(0));
            uint tag = (await c.RequestAsync(glx, 26, b => b.U32(0).U32(glxPixmap).U32(glxPixmap).U32(context))).U32(8);
            await RenderAsync(c, glx, tag, new Commands().Add(127, b => b.U32(ColorBit)));
            await c.RequestAsync(glx, 26, b => b.U32(tag).U32(0).U32(0).U32(0));   // 放下当前上下文
            await c.SendAsync(glx, 23, b => b.U32(glxPixmap));                      // DestroyPixmap(GLX)
            await c.SendAsync(54, 0, b => b.U32(pixmap));                           // FreePixmap
        }
        await c.SyncAsync();
        Assert.AreEqual(0, await server.InvokeAsync(() => server.Glx.SurfaceCount), "原先每轮漏一份表面加一份像素缓冲");
    }

    [TestMethod]
    public async Task 单缓冲每个Render请求只拷画过的那一块_不盖掉窗口里别处X画的内容()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        uint context = c.NewId();
        await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        uint tag = (await c.RequestAsync(glx, 5, b => b.U32(window).U32(context).U32(0))).U32(8);

        await RenderAsync(c, glx, tag, new Commands().Add(130, b => F(b, 0, 0, 1, 1)).Add(127, b => b.U32(ColorBit)));
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 1, 1), "单缓冲:清除随 Render 请求就出现在窗口里");

        // 左上角用 X 画一块绿色;GL 接着只在右上角画一个小三角形。
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(window).U32(0x4).U32(0x00FF00));
        await c.SendAsync(70, 0, b => b.U32(window).U32(gc).I16(0).I16(0).U16(4).U16(4));
        await RenderAsync(c, glx, tag, new Commands()
            .Add(8, b => F(b, 1, 0, 0))
            .Add(4, b => b.U32(Triangles))
            .Add(66, b => F(b, 0.6f, 0.6f)).Add(66, b => F(b, 0.9f, 0.6f)).Add(66, b => F(b, 0.6f, 0.9f))
            .Add(23));
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 50, 6), "三角形拷进了窗口");
        Assert.AreEqual(0x00FF00u, await PixelAsync(c, window, 1, 1), "GL 没画的地方不重拷整块前缓冲,X 画的绿色还在");
    }

    [TestMethod]
    public async Task 客户端断开时它的Pbuffer表面随之释放()
    {
        await using X11Server server = new();
        XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint pbuffer = c.NewId(), context = c.NewId();
        await c.SendAsync(glx, 27, b => b.U32(0).U32(SingleBufferedRgb).U32(pbuffer).U32(2).U32(0x8041).U32(64).U32(0x8040).U32(64));   // CreatePbuffer 64×64
        await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        XMessage made = await c.RequestAsync(glx, 26, b => b.U32(0).U32(pbuffer).U32(pbuffer).U32(context));
        Assert.IsTrue(made.IsReply);
        Assert.AreEqual(1, await server.InvokeAsync(() => server.Glx.SurfaceCount));

        await c.DisconnectAsync(server);
        Assert.AreEqual(0, await server.InvokeAsync(() => server.Glx.SurfaceCount), "断开时表面随之释放");
    }

    [TestMethod]
    public void 显示列表与纹理记账_超了上限记OUT_OF_MEMORY_删掉之后销账()
    {
        Gl.GlContext gl = new(doubleBuffered: false, hasAlpha: false, share: null);
        gl.NewList(1, Compile);
        for (int i = 0; i < 70; i++)
        {
            gl.ExecuteOrCompile(130, new byte[1 << 20], bigEndian: false);   // 每条 1 MB,共 70 MB
        }
        gl.EndList();
        Assert.AreEqual(OutOfMemory, gl.GetError());
        Assert.IsLessThanOrEqualTo(Gl.GlShared.MaxListBytes, gl.Shared.ListBytes);
        gl.DeleteLists(1, 1);
        Assert.AreEqual(0L, gl.Shared.ListBytes, "删掉之后销账");

        // 纹理:4×4 的 RGBA 记 64 字节,删掉销账;名字总数有上限。
        gl.ExecuteOrCompile(4117, [.. BitConverter.GetBytes(Texture2D), .. BitConverter.GetBytes(7u)], bigEndian: false);   // BindTexture
        List<byte> image = [0, 0, 0, 0, .. new byte[16]];   // 像素存储头:不交换、MSB、行长 / 跳过 0、对齐 4(下面改)
        image[16] = 4;
        foreach (uint value in (uint[])[Texture2D, 0, Rgba, 4, 4, 0, Rgba, UnsignedByte])
        {
            image.AddRange(BitConverter.GetBytes(value));
        }
        image.AddRange(new byte[64]);
        gl.ExecuteOrCompile(110, [.. image], bigEndian: false);   // TexImage2D
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(64L, gl.Shared.TextureBytes);
        gl.DeleteTextures([7]);
        Assert.AreEqual(0L, gl.Shared.TextureBytes);

        Assert.IsNotNull(gl.GenTextures(Gl.GlShared.MaxTextures));
        Assert.IsNull(gl.GenTextures(1), "名字用完了");
    }

    [TestMethod]
    public async Task MakeCurrent因表面记不下账回BadAlloc时不留下任何效果_上下文随后还能用()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 1024 * 1024 }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        uint huge = c.NewId();   // 1000 × 1000(不映射):表面每像素 13 字节,超过每客户端 1 MiB 的账
        await c.SendAsync(1, 0, b => b.U32(huge).U32(c.RootWindow).I16(0).I16(0).U16(1000).U16(1000).U16(0).U16(1).U32(0).U32(0));
        uint context = c.NewId();
        await c.SendAsync(glx, 3, b => b.U32(context).U32(RootVisual).U32(0).U32(0).U8(0).U8(0).U16(0));

        XMessage refused = await c.RequestAsync(glx, 5, b => b.U32(huge).U32(context).U32(0));
        Assert.IsTrue(refused.IsError);
        Assert.AreEqual(11, refused.Bytes[1], "BadAlloc");

        XMessage made = await c.RequestAsync(glx, 5, b => b.U32(window).U32(context).U32(0));
        Assert.IsTrue(made.IsReply, "上一次失败没有把上下文挂在一个看不见的标签上:这次照常成为当前");
    }

    [TestMethod]
    public async Task 声明了的GL_EXT_abgr真能用_DrawPixels与ReadPixels按ABGR排分量()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        const uint abgr = 0x8000;

        // 一个像素:A = FF、B = 30、G = 20、R = 10。
        await RenderAsync(c, glx, tag, new Commands()
            .Add(34, b => F(b, -1, -1))
            .Add(173, b => b.U8(0).U8(0).U16(0).I32(0).I32(0).I32(0).I32(4).I32(1).I32(1).U32(abgr).U32(UnsignedByte)
                .Bytes([0xFF, 0x30, 0x20, 0x10])));
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag), "ABGR 是认识的格式");
        XMessage rgba = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(0).I32(0).I32(1).I32(1).U32(Rgba).U32(UnsignedByte).U8(0).U8(0).U16(0));
        Assert.AreSequenceEqual(new byte[] { 0x10, 0x20, 0x30, 0xFF }, rgba.Bytes[32..36]);
        XMessage back = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(0).I32(0).I32(1).I32(1).U32(abgr).U32(UnsignedByte).U8(0).U8(0).U16(0));
        Assert.AreSequenceEqual(new byte[] { 0xFF, 0x30, 0x20, 0x10 }, back.Bytes[32..36]);
    }

    [TestMethod]
    public async Task RenderLarge拼出来的长度与第一段声明的对不上回GLXBadLargeRequest()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        const byte badLargeRequest = 151 + 7;
        byte[] small = [.. BitConverter.GetBytes(0x3F800000u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0x3F800000u)];   // ClearColor 1, 0, 0, 1

        // 声明比实际多 1000 字节:最后一段到了还不够 —— 命令被截断了。
        await c.SendAsync(glx, 2, b => b.U32(tag).U16(1).U16(2).U32((uint)small.Length).U32((uint)(8 + small.Length + 1000)).U32(130).Bytes(small));
        XMessage truncated = await c.RequestAsync(glx, 2, b => b.U32(tag).U16(2).U16(2).U32(4).U32(0));
        Assert.IsTrue(truncated.IsError);
        Assert.AreEqual(badLargeRequest, truncated.Bytes[1]);

        // 声明正好是小参数,第二段却还带 8 字节:拼起来比声明的长。
        await c.SendAsync(glx, 2, b => b.U32(tag).U16(1).U16(2).U32((uint)small.Length).U32((uint)(8 + small.Length)).U32(130).Bytes(small));
        XMessage overlong = await c.RequestAsync(glx, 2, b => b.U32(tag).U16(2).U16(2).U32(8).U32(0).U32(0));
        Assert.IsTrue(overlong.IsError);
        Assert.AreEqual(badLargeRequest, overlong.Bytes[1]);
    }

    [TestMethod]
    public async Task TexSubImage的偏移加宽度在int上溢出时照样回INVALID_VALUE()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        static XTestClient.Body Store(XTestClient.Body b) => b.U8(0).U8(0).U16(0).I32(0).I32(0).I32(0).I32(1);

        await RenderAsync(c, glx, tag, new Commands()
            .Add(4117, b => b.U32(Texture2D).U32(1))                                                       // BindTexture 1
            .Add(110, b => Store(b).U32(Texture2D).I32(0).U32(Rgba).I32(2).I32(2).I32(0).U32(Rgba).U32(UnsignedByte)
                .Bytes(new byte[16])));                                                                     // TexImage2D 2×2
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));

        // xoffset = 2^31 − 1、width = 2:相加在 int 上溢出成负数。
        await RenderAsync(c, glx, tag, new Commands()
            .Add(4100, b => Store(b).U32(Texture2D).I32(0).I32(int.MaxValue).I32(0).I32(2).I32(2).U32(Rgba).U32(UnsignedByte).U32(0)
                .Bytes(new byte[16])));
        Assert.AreEqual(InvalidValue, await GlErrorAsync(c, glx, tag));
        Assert.IsFalse(await c.NextAsync(m => m.IsError, 100).ContinueWith(t => t.IsCompletedSuccessfully), "没有 BadImplementation");
    }

    [TestMethod]
    public async Task PopAttrib恢复了已删掉的纹理绑定_TexImage与CopyTexImage按绑定退回0处理_不回BadImplementation()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        static XTestClient.Body Store(XTestClient.Body b) => b.U8(0).U8(0).U16(0).I32(0).I32(0).I32(0).I32(1);
        const uint textureBit = 0x40000;

        await RenderAsync(c, glx, tag, new Commands()
            .Add(4117, b => b.U32(Texture2D).U32(5))      // BindTexture 5
            .Add(142, b => b.U32(textureBit)));           // PushAttrib(TEXTURE_BIT):记下绑定 5
        await c.SendAsync(glx, 144, b => b.U32(tag).I32(1).U32(5));   // DeleteTextures 5:绑定退回 0
        await RenderAsync(c, glx, tag, new Commands()
            .Add(141)                                     // PopAttrib:把已删掉的 5 恢复回来(合法的 GL 序列)
            .Add(110, b => Store(b).U32(Texture2D).I32(0).U32(Rgba).I32(2).I32(2).I32(0).U32(Rgba).U32(UnsignedByte)
                .Bytes(new byte[16]))                     // TexImage2D
            .Add(4120, b => b.U32(Texture2D).I32(0).U32(Rgba).I32(0).I32(0).I32(2).I32(2).I32(0)));   // CopyTexImage2D
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        Assert.IsFalse(await c.NextAsync(m => m.IsError, 100).ContinueWith(t => t.IsCompletedSuccessfully), "原先 NullReferenceException → BadImplementation");
        XMessage binding = await c.RequestAsync(glx, 117, b => b.U32(tag).U32(0x8069));   // GetIntegerv(TEXTURE_BINDING_2D)
        Assert.AreEqual(0u, binding.U32(16), "悬空的名字按删掉处理:绑定是 0");
    }

    /// <summary>有上限的内存账(直接驱动 GlContext 时代替服务端的每客户端账)。</summary>
    private sealed class LimitedAccount(long limit) : Gl.IGlMemoryAccount
    {
        public long InUse { get; private set; }

        public bool TryCharge(long bytes)
        {
            if (InUse + bytes > limit)
            {
                return false;
            }
            InUse += bytes;
            return true;
        }

        public void Refund(long bytes) => InUse -= bytes;
    }

    /// <summary>TexImage2D 渲染命令的正文:像素存储头(对齐 1)、target、level、内部格式、宽高、边框、格式、类型,再是数据(可以没有)。</summary>
    private static byte[] TexImage2DBody(int level, int width, int height, byte[]? data = null, uint target = Texture2D)
    {
        XTestClient.Body b = new(bigEndian: false);
        b.U8(0).U8(0).U16(0).I32(0).I32(0).I32(0).I32(1)
            .U32(target).I32(level).U32(Rgba).I32(width).I32(height).I32(0).U32(Rgba).U32(UnsignedByte);
        if (data is not null)
        {
            b.Bytes(data);
        }
        return b.ToArray();
    }

    private const uint InvalidEnum = 0x0500, InvalidOperation = 0x0502;

    [TestMethod]
    public void Enable与Disable只认识的开关_别的值记INVALID_ENUM不进状态()
    {
        Gl.GlContext gl = new(doubleBuffered: false, hasAlpha: false, share: null);
        int before = gl.State.Enabled.Count;
        for (uint cap = 0x10000; cap < 0x10100; cap++)
        {
            gl.ExecuteOrCompile(139, BitConverter.GetBytes(cap), bigEndian: false);   // Enable(垃圾值)
        }
        Assert.AreEqual(InvalidEnum, gl.GetError());
        Assert.AreEqual(before, gl.State.Enabled.Count, "原先每个垃圾值都进了开关集合,PushAttrib 再复制 16 份");
        Assert.IsFalse(gl.IsEnabled(0x10000));
        Assert.AreEqual(InvalidEnum, gl.GetError(), "IsEnabled 也认开关");
        gl.ExecuteOrCompile(138, BitConverter.GetBytes(0x10000u), bigEndian: false);   // Disable(垃圾值)
        Assert.AreEqual(InvalidEnum, gl.GetError());

        gl.ExecuteOrCompile(139, BitConverter.GetBytes(0x0DB7u), bigEndian: false);    // Enable(MAP2_VERTEX_3):求值器不实现,开关照样能拨
        gl.ExecuteOrCompile(139, BitConverter.GetBytes(0x3005u), bigEndian: false);    // CLIP_PLANE5
        Assert.AreEqual(0u, gl.GetError());
        Assert.IsTrue(gl.IsEnabled(0x0DB7));
        Assert.IsTrue(gl.IsEnabled(0x3005));
    }

    [TestMethod]
    public void 默认纹理_图元缓冲_空列表都记账_上下文释放后如数退还()
    {
        LimitedAccount account = new(8L << 20);
        Gl.GlContext gl = new(doubleBuffered: false, hasAlpha: false, share: null, account);

        gl.ExecuteOrCompile(110, TexImage2DBody(0, 1024, 1024), bigEndian: false);   // 名字 0 的默认 2D 纹理:4 MB
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(4L << 20, account.InUse, "默认纹理记在上下文的账上(原先不记)");
        gl.ExecuteOrCompile(110, TexImage2DBody(1, 2048, 2048), bigEndian: false);   // 再要 16 MB:超了
        Assert.AreEqual(OutOfMemory, gl.GetError());
        Assert.AreEqual(4L << 20, account.InUse, "记不下的不分配、账不变");

        long beforeList = account.InUse;
        gl.NewList(9, Compile);
        gl.EndList();
        Assert.AreEqual(Gl.GlShared.ListOverhead, account.InUse - beforeList, "空列表也记一份对象开销");

        long beforeVertices = account.InUse;
        gl.ExecuteOrCompile(4, BitConverter.GetBytes(0u), bigEndian: false);           // Begin(POINTS)
        for (int i = 0; i < 1000; i++)
        {
            gl.ExecuteOrCompile(66, new byte[8], bigEndian: false);                    // Vertex2fv
        }
        Assert.IsGreaterThan(beforeVertices, account.InUse, "Begin / End 之间攒的顶点记账");
        gl.ExecuteOrCompile(23, [], bigEndian: false);                                 // End

        gl.Release();
        Assert.AreEqual(0L, account.InUse, "上下文释放:默认纹理、图元缓冲、共享组的列表都退还");
    }

    [TestMethod]
    public async Task 间接上下文的纹理与表面记在客户端的内存账上_超了记OUT_OF_MEMORY_销毁后如数退还()
    {
        const long mib = 1024 * 1024;
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 16 * mib });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        long baseline = await server.InvokeAsync(() => server.MemoryInUse);

        uint pbuffer = c.NewId(), context = c.NewId();
        await c.SendAsync(glx, 27, b => b.U32(0).U32(SingleBufferedRgb).U32(pbuffer).U32(2).U32(0x8041).U32(64).U32(0x8040).U32(64));
        await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        uint tag = (await c.RequestAsync(glx, 26, b => b.U32(0).U32(pbuffer).U32(pbuffer).U32(context))).U32(8);
        long current = await server.InvokeAsync(() => server.MemoryInUse);
        Assert.IsGreaterThanOrEqualTo(baseline + Gl.GlContext.ObjectBytes + (64 * 64 * 9), current, "上下文对象与 64² 的表面记账");

        await RenderAsync(c, glx, tag, new Commands().Add(110, b => b.Bytes(TexImage2DBody(0, 1024, 1024))));   // 默认纹理 4 MB
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        await RenderAsync(c, glx, tag, new Commands().Add(110, b => b.Bytes(TexImage2DBody(0, 2048, 2048))));   // 换成 16 MB:超了 16 MiB
        Assert.AreEqual(OutOfMemory, await GlErrorAsync(c, glx, tag), "原先默认纹理不记账,192 MB 也照建");

        await c.RequestAsync(glx, 26, b => b.U32(tag).U32(0).U32(0).U32(0));   // 放下当前上下文
        await c.SendAsync(glx, 4, b => b.U32(context));                        // DestroyContext
        await c.SendAsync(glx, 28, b => b.U32(pbuffer));                       // DestroyPbuffer
        await c.SyncAsync();
        Assert.AreEqual(baseline, await server.InvokeAsync(() => server.MemoryInUse), "销毁之后上下文、纹理、表面的账全部退还");
    }

    [TestMethod]
    public async Task 不共享的上下文每个都记账_建再多也超不过每客户端的上限()
    {
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 1024 * 1024 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        for (int i = 0; i < 32; i++)
        {
            uint context = c.NewId();
            await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        }
        await c.SyncAsync();
        XMessage refused = await c.NextAsync(m => m.IsError, 500);   // 原先每个上下文(各带一个共享组)都不记账:一个错误都没有
        Assert.AreEqual(11, refused.Bytes[1], "BadAlloc");
    }

    [TestMethod]
    public async Task 客户端断开时它的上下文_共享组_表面的账全部退还()
    {
        await using X11Server server = new();
        long baseline = await server.InvokeAsync(() => server.MemoryInUse);
        XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint pbuffer = c.NewId(), context = c.NewId(), shared = c.NewId();
        await c.SendAsync(glx, 27, b => b.U32(0).U32(SingleBufferedRgb).U32(pbuffer).U32(2).U32(0x8041).U32(32).U32(0x8040).U32(32));
        await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        await c.SendAsync(glx, 24, b => b.U32(shared).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(context).U8(0).U8(0).U16(0));   // 共享 context 的名字空间
        uint tag = (await c.RequestAsync(glx, 26, b => b.U32(0).U32(pbuffer).U32(pbuffer).U32(shared))).U32(8);
        await RenderAsync(c, glx, tag, new Commands()
            .Add(4117, b => b.U32(Texture2D).U32(3))                                  // 有名字的纹理:记在共享组上
            .Add(110, b => b.Bytes(TexImage2DBody(0, 64, 64)))
            .Add(4, b => b.U32(0)).Add(66, b => F(b, 0, 0)));                         // Begin 了还没 End:图元缓冲
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        Assert.IsGreaterThan(baseline + (64 * 64 * 4), await server.InvokeAsync(() => server.MemoryInUse));

        await c.DisconnectAsync(server);
        Assert.AreEqual(baseline, await server.InvokeAsync(() => server.MemoryInUse), "还是当前的上下文、共享组、表面在断开时一并销账");
    }

    /// <summary>这个客户端在 <paramref name="ms" /> 毫秒内有没有收到 X 错误(先往返一次,之前的请求出错的话错误已经到了)。</summary>
    private static async Task<bool> AnyErrorAsync(XTestClient c, int ms = 100)
    {
        await c.SyncAsync();
        return await c.NextAsync(m => m.IsError, ms).ContinueWith(t => t.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task 代理目标的CopyTexSubImage回INVALID_ENUM_坐标为int最小值的CopyPixels照常_都不回BadImplementation()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        const uint proxyTexture2D = 0x8064;

        await RenderAsync(c, glx, tag, new Commands().Add(110, b => b.Bytes(TexImage2DBody(0, 4, 4, target: proxyTexture2D))));   // 代理:只记尺寸
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        await RenderAsync(c, glx, tag, new Commands()
            .Add(4122, b => b.U32(proxyTexture2D).I32(0).I32(0).I32(0).I32(0).I32(0).I32(2).I32(2)));   // CopyTexSubImage2D(PROXY_TEXTURE_2D, …)
        Assert.AreEqual(InvalidEnum, await GlErrorAsync(c, glx, tag));
        Assert.IsFalse(await AnyErrorAsync(c), "原先往代理级的空纹素里写:IndexOutOfRange → BadImplementation");

        await RenderAsync(c, glx, tag, new Commands()
            .Add(34, b => F(b, -1, -1))
            .Add(172, b => b.I32(int.MinValue).I32(int.MinValue).I32(10).I32(10).U32(Color)));          // CopyPixels(int.MinValue, int.MinValue, …)
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        Assert.IsFalse(await AnyErrorAsync(c), "原先 −x 转回 int 回绕:ArgumentOutOfRange → BadImplementation");
    }

    [TestMethod]
    public void TexImage与TexSubImage的数据装不下声明的尺寸时作废_不带数据仍然可以()
    {
        Gl.GlContext gl = new(doubleBuffered: false, hasAlpha: false, share: null);
        gl.ExecuteOrCompile(110, TexImage2DBody(0, 2048, 2048, data: [0xFF]), bigEndian: false);   // 声称 2048²,只带 1 字节
        Assert.AreEqual(InvalidValue, gl.GetError(), "原先照声明的尺寸逐个解码");
        Assert.AreEqual(0.0, gl.GetTexLevelParameter(Texture2D, 0, 0x1000)!.Value.Values[0], "这一级没有定义(TEXTURE_WIDTH 为 0)");

        gl.ExecuteOrCompile(110, TexImage2DBody(0, 4, 4), bigEndian: false);                       // 不带数据(客户端传 NULL):照常定义
        Assert.AreEqual(0u, gl.GetError());
        XTestClient.Body sub = new(bigEndian: false);
        sub.U8(0).U8(0).U16(0).I32(0).I32(0).I32(0).I32(1)
            .U32(Texture2D).I32(0).I32(0).I32(0).I32(4).I32(4).U32(Rgba).U32(UnsignedByte).U32(0).Bytes([1, 2, 3]);
        gl.ExecuteOrCompile(4100, sub.ToArray(), bigEndian: false);                                 // TexSubImage2D 4×4,只带 3 字节
        Assert.AreEqual(InvalidValue, gl.GetError());
    }

    [TestMethod]
    public async Task GetTexImage的回复大小有上限_超了回BadAlloc而不是把客户端顶断()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        const uint floatType = 0x1406;

        await RenderAsync(c, glx, tag, new Commands().Add(110, b => b.Bytes(TexImage2DBody(0, 2048, 2048))));   // 2048² 的默认纹理(不带数据)
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        XMessage huge = await c.RequestAsync(glx, 135, b => b.U32(tag).U32(Texture2D).I32(0).U32(Rgba).U32(floatType).U8(0).U8(0).U16(0));
        Assert.IsTrue(huge.IsError, "按 FLOAT 取是 64 MB:原先照打包,顶到输出积压上限、客户端被断开");
        Assert.AreEqual(11, huge.Bytes[1], "BadAlloc");

        await RenderAsync(c, glx, tag, new Commands().Add(110, b => b.Bytes(TexImage2DBody(0, 2, 2, data: new byte[16]))));
        XMessage small = await c.RequestAsync(glx, 135, b => b.U32(tag).U32(Texture2D).I32(0).U32(Rgba).U32(floatType).U8(0).U8(0).U16(0));
        Assert.IsTrue(small.IsReply, "正常大小照常回");
        Assert.AreEqual(2u, small.U32(16), "width");
    }

    /// <summary>直接驱动 GlContext 执行一条渲染命令(小端)。</summary>
    private static void Run(Gl.GlContext gl, ushort opcode, Action<XTestClient.Body>? parameters = null)
    {
        XTestClient.Body b = new(bigEndian: false);
        parameters?.Invoke(b);
        gl.ExecuteOrCompile(opcode, b.ToArray(), bigEndian: false);
    }

    /// <summary>一个绑在 <paramref name="width" /> × <paramref name="height" /> 单缓冲表面上的间接上下文(视口即整个表面)。</summary>
    private static (Gl.GlContext Gl, Gl.GlSurface Surface) DirectContext(int width = 8, int height = 8)
    {
        Gl.GlContext gl = new(doubleBuffered: false, hasAlpha: false, share: null);
        Gl.GlSurface surface = new(1, width, height, doubleBuffered: false, hasAlpha: false);
        gl.Bind(surface, surface);
        return (gl, surface);
    }

    /// <summary>表面上 GL 窗口坐标 (x, y) 的颜色(RGB;缓冲第 0 行在最上面)。</summary>
    private static uint SurfacePixel(Gl.GlSurface surface, int x, int y) => surface.Front[((surface.Height - 1 - y) * surface.Width) + x] & 0xFFFFFF;

    /// <summary>铺满视口、深度为 <paramref name="z" /> 的一个四边形。</summary>
    private static void FullQuad(Gl.GlContext gl, float r, float g, float b, float z = 0)
    {
        Run(gl, 8, p => F(p, r, g, b));
        Run(gl, 4, p => p.U32(Quads));
        Run(gl, 70, p => F(p, -1, -1, z));
        Run(gl, 70, p => F(p, 1, -1, z));
        Run(gl, 70, p => F(p, 1, 1, z));
        Run(gl, 70, p => F(p, -1, 1, z));
        Run(gl, 23);
    }

    /// <summary>
    /// 多边形的点画(xs_plan F23,§3.5.2):按窗口坐标 mod 32 取 32×32 图样的位,图样按 Bitmap 的规则解包(这里 lsbfirst);
    /// GetPolygonStipple 按原样交回。原先 PolygonStipple 吃掉不画,CAD 里表示剖面的点画填充画成实心。
    /// </summary>
    [TestMethod]
    public void 多边形的点画按窗口坐标取图样的位()
    {
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext();
        byte[] pattern = new byte[128];
        for (int row = 0; row < 32; row += 2)
        {
            pattern[(row * 4) + 0] = pattern[(row * 4) + 1] = pattern[(row * 4) + 2] = pattern[(row * 4) + 3] = 0x55;   // 偶数行的偶数列
        }
        Run(gl, 102, b => b.U8(0).U8(1).U16(0).U32(0).U32(0).U32(0).U32(4).Bytes(pattern));   // PolygonStipple(lsbfirst)
        Run(gl, 139, b => b.U32(0x0B42));                                                       // Enable(POLYGON_STIPPLE)
        Run(gl, 127, b => b.U32(ColorBit));
        FullQuad(gl, 1, 0, 0);
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0xFF0000u, SurfacePixel(surface, 0, 0));
        Assert.AreEqual(0u, SurfacePixel(surface, 1, 0), "奇数列不画");
        Assert.AreEqual(0u, SurfacePixel(surface, 0, 1), "奇数行不画");
        Assert.AreEqual(0xFF0000u, SurfacePixel(surface, 6, 6));
        CollectionAssert.AreEqual(pattern, gl.PolygonStippleBytes(lsbFirst: true));
    }

    /// <summary>
    /// 线的点画(xs_plan F23,§3.4.2):第 s 个片元看图样的第 (s / factor) mod 16 位,从起点往终点数(反着画的线从右边数起);
    /// LINES 的每条线段之前计数器清零。LINE_STIPPLE_PATTERN / REPEAT 报设的值。
    /// </summary>
    [TestMethod]
    public void 线的点画从起点数起_独立线段各自从头数()
    {
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext(16, 4);
        Run(gl, 94, b => b.I32(1).U16(0x00FF).U16(0));   // LineStipple(1, 0x00FF):前 8 个画,后 8 个不画
        Run(gl, 139, b => b.U32(0x0B24));                 // Enable(LINE_STIPPLE)
        Run(gl, 127, b => b.U32(ColorBit));
        Run(gl, 8, p => F(p, 0, 1, 0));
        Run(gl, 4, b => b.U32(1));                        // Begin(LINES)
        Run(gl, 66, p => F(p, -1, -0.25f));   // 窗口 y = 1.5,从左往右
        Run(gl, 66, p => F(p, 1, -0.25f));
        Run(gl, 66, p => F(p, 1, 0.25f));     // 窗口 y = 2.5,从右往左
        Run(gl, 66, p => F(p, -1, 0.25f));
        Run(gl, 23);
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0x00FF00u, SurfacePixel(surface, 0, 1));
        Assert.AreEqual(0x00FF00u, SurfacePixel(surface, 7, 1));
        Assert.AreEqual(0u, SurfacePixel(surface, 8, 1), "第 9 个片元起不画");
        Assert.AreEqual(0x00FF00u, SurfacePixel(surface, 15, 2), "反着画的那条从右边数起;计数器每段清零");
        Assert.AreEqual(0u, SurfacePixel(surface, 0, 2));
        Assert.AreEqual(0xFF, gl.Query(0x0B25)!.Value.Values[0]);
        Assert.AreEqual(1, gl.Query(0x0B26)!.Value.Values[0]);
    }

    [TestMethod]
    public void PolygonOffsetEXT的bias以深度范围为单位_同一深度的后画的面靠偏移挡住先画的()
    {
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext();
        Run(gl, 139, b => b.U32(DepthTest));
        Run(gl, 127, b => b.U32(ColorBit | DepthBit));
        FullQuad(gl, 1, 0, 0);                         // 红,窗口深度 0.5
        Run(gl, 139, b => b.U32(0x8037));               // Enable(POLYGON_OFFSET_FILL)
        Run(gl, 4098, b => F(b, 0, -0.1f));             // PolygonOffsetEXT(factor 0, bias −0.1)
        FullQuad(gl, 0, 1, 0);                         // 绿,同一深度:偏移后 0.4 < 0.5
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0x00FF00u, SurfacePixel(surface, 4, 4), "原先 bias 按 units / 2²⁴ 处理,等于没有偏移");
        Assert.AreEqual(-0.1, gl.Query(0x8039)!.Value.Values[0], 1e-6, "POLYGON_OFFSET_BIAS_EXT 按深度范围单位报");

        Run(gl, 192, b => F(b, 0, -4));                 // 核心的 PolygonOffset:units 仍以最小可分辨量为单位
        Assert.AreEqual(-4.0, gl.Query(0x2A00)!.Value.Values[0], 1e-6);
    }

    [TestMethod]
    public void 状态命令收到不合法的枚举值记INVALID_ENUM_状态不变()
    {
        (Gl.GlContext gl, _) = DirectContext();
        const uint garbage = 0x1234;
        (ushort Opcode, Action<XTestClient.Body> Parameters, uint Query, double Initial)[] cases =
        [
            (164, b => b.U32(garbage), 0x0B74, 0x0201),                        // DepthFunc → DEPTH_FUNC 仍是 LESS
            (101, b => b.U32(0x0408).U32(garbage), 0x0B40, 0x1B02),            // PolygonMode(FRONT_AND_BACK, 垃圾) → FILL
            (162, b => b.U32(garbage).I32(0).U32(0xFF), 0x0B92, 0x0207),       // StencilFunc → ALWAYS
            (163, b => b.U32(0x1E00).U32(garbage).U32(0x1E00), 0x0B95, 0x1E00),   // StencilOp → KEEP
            (160, b => b.U32(1).U32(0x0308), 0x0BE0, 0),                       // BlendFunc(ONE, SRC_ALPHA_SATURATE):只能当源 → 目标仍是 ZERO
            (126, b => b.U32(garbage), 0x0C01, 0x0404),                        // DrawBuffer → FRONT(单缓冲)
            (126, b => b.U32(0x0405), 0x0C01, 0x0404),                         // DrawBuffer(BACK):单缓冲没有后缓冲 → INVALID_OPERATION
            (171, b => b.U32(0x0408), 0x0C02, 0x0404),                         // ReadBuffer(FRONT_AND_BACK)不是读缓冲
            (111, b => b.U32(0x2300).U32(0x2200).U32(BitConverter.SingleToUInt32Bits(garbage)), 0x2200, 0x2100),   // TexEnvf(MODE) → MODULATE
            (80, b => b.U32(0x0B65).U32(BitConverter.SingleToUInt32Bits(garbage)), 0x0B65, 0x0800),               // Fogf(FOG_MODE) → EXP
            (104, b => b.U32(garbage), 0x0B54, 0x1D01),                        // ShadeModel → SMOOTH
            (4097, b => b.U32(garbage), 0x8009, 0x8006),                       // BlendEquation → FUNC_ADD
        ];
        foreach ((ushort opcode, Action<XTestClient.Body> parameters, uint query, double initial) in cases)
        {
            Run(gl, opcode, parameters);
            uint error = gl.GetError();
            Assert.IsTrue(error is InvalidEnum or InvalidOperation, $"操作码 {opcode}:错误 0x{error:X}");
            Assert.AreEqual(initial, gl.Query(query)!.Value.Values[0], $"操作码 {opcode}:状态不变");
        }
        Run(gl, 107, b => b.U32(Texture2D).U32(MinFilter).U32(garbage));     // TexParameteri(MIN_FILTER, 垃圾)
        Assert.AreEqual(InvalidEnum, gl.GetError());
        Assert.AreEqual(0x2702, gl.GetTexParameter(Texture2D, MinFilter)!.Value.Values[0], "仍是 NEAREST_MIPMAP_LINEAR");
    }

    [TestMethod]
    public void Begin与End之间只许指定顶点属性_DrawArrays不再并进外层图元()
    {
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext();
        Run(gl, 4, b => b.U32(Quads));                                        // Begin(QUADS)
        Run(gl, 8, b => F(b, 0, 0, 1));                                       // Color3fv:可以
        Run(gl, 70, b => F(b, -1, -1, 0));
        Run(gl, 70, b => F(b, 1, -1, 0));
        Run(gl, 139, b => b.U32(DepthTest));                                  // Enable:不行
        Assert.AreEqual(InvalidOperation, gl.GetError());
        Assert.IsFalse(gl.IsEnabled(DepthTest), "原先照常执行");
        Run(gl, 184);                                                          // PushMatrix:不行
        Assert.AreEqual(InvalidOperation, gl.GetError());
        Assert.AreEqual(1.0, gl.Query(0x0BA3)!.Value.Values[0], "模型视图栈深度不变");
        Run(gl, 193, b => b.I32(1).I32(1).U32(0).U32(0x1406).I32(2).U32(0x8074).Bytes(new byte[8]));   // DrawArrays(POINTS, 1 个顶点)
        Assert.AreEqual(InvalidOperation, gl.GetError());
        Assert.IsTrue(gl.InBeginEnd, "原先 DrawArrays 替外层执行了 End");
        Run(gl, 70, b => F(b, 1, 1, 0));
        Run(gl, 70, b => F(b, -1, 1, 0));
        Run(gl, 23);                                                          // End:外层的四边形照常画出
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0x0000FFu, SurfacePixel(surface, 4, 4));
    }

    [TestMethod]
    public void CallLists先核个数与类型_FOUR_BYTES解出全1的偏移不再误报INVALID_ENUM()
    {
        (Gl.GlContext gl, _) = DirectContext();
        gl.NewList(1, Compile);
        Run(gl, 139, b => b.U32(DepthTest));                                  // 列表 1:Enable(DEPTH_TEST)
        gl.EndList();
        Run(gl, 3, b => b.U32(2));                                            // ListBase 2:2 + 0xFFFFFFFF 回绕成 1
        Run(gl, 2, b => b.I32(1).U32(0x1409).Bytes([0xFF, 0xFF, 0xFF, 0xFF]));   // CallLists(1, FOUR_BYTES, FF FF FF FF)
        Assert.AreEqual(0u, gl.GetError(), "原先把解出的 0xFFFFFFFF 当成「类型不认识」");
        Assert.IsTrue(gl.IsEnabled(DepthTest), "调到了列表 1");

        Run(gl, 2, b => b.I32(-1).U32(UnsignedByte));
        Assert.AreEqual(InvalidValue, gl.GetError(), "n < 0");
        Run(gl, 2, b => b.I32(1).U32(0x1234).U32(0));
        Assert.AreEqual(InvalidEnum, gl.GetError());
    }

    [TestMethod]
    public void 边标记为假的边在线框模式下不画_镶嵌出来的内部对角线不出现()
    {
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext();
        Run(gl, 101, b => b.U32(0x0408).U32(0x1B01));                  // PolygonMode(FRONT_AND_BACK, LINE)
        Run(gl, 8, b => F(b, 1, 1, 1));
        // 像 GLU 镶嵌器那样把正方形拆成两个三角形,对角线 (−1, −1)–(1, 1) 标成非边界边。
        Run(gl, 4, b => b.U32(Triangles));
        Run(gl, 66, b => F(b, -1, -1));
        Run(gl, 66, b => F(b, 1, -1));
        Run(gl, 22, b => b.U8(0));                                     // EdgeFlagv(False):从下一个顶点出发的边不是边界边
        Run(gl, 66, b => F(b, 1, 1));
        Run(gl, 66, b => F(b, -1, -1));                                // 第二个三角形从对角线起
        Run(gl, 22, b => b.U8(1));
        Run(gl, 66, b => F(b, 1, 1));
        Run(gl, 66, b => F(b, -1, 1));
        Run(gl, 23);
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0xFFFFFFu, SurfacePixel(surface, 4, 0), "底边是边界边,画了");
        Assert.AreEqual(0u, SurfacePixel(surface, 4, 4), "原先边标记没人读,对角线照样画出来");
        Assert.AreEqual(0u, SurfacePixel(surface, 2, 2));
    }

    [TestMethod]
    public void GL_CLAMP配LINEAR在纹理边上与边框色混合()
    {
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext();
        const uint clamp = 0x2900, linear = 0x2601;
        Run(gl, 4117, b => b.U32(Texture2D).U32(1));
        Run(gl, 107, b => b.U32(Texture2D).U32(MinFilter).U32(linear));
        Run(gl, 107, b => b.U32(Texture2D).U32(MagFilter).U32(linear));
        Run(gl, 107, b => b.U32(Texture2D).U32(0x2802).U32(clamp));    // WRAP_S
        Run(gl, 107, b => b.U32(Texture2D).U32(0x2803).U32(clamp));    // WRAP_T
        Run(gl, 106, b => F(b.U32(Texture2D).U32(0x1004), 1, 0, 0, 1));   // TEXTURE_BORDER_COLOR 红
        Run(gl, 110, b => b.Bytes(TexImage2DBody(0, 2, 2, data: [.. Enumerable.Repeat((byte)0xFF, 16)])));   // 2×2 全白
        Run(gl, 139, b => b.U32(Texture2D));
        Run(gl, 4, b => b.U32(Quads));
        Run(gl, 54, b => F(b, 0, 0));                                  // TexCoord2fv + Vertex2fv
        Run(gl, 66, b => F(b, -1, -1));
        Run(gl, 54, b => F(b, 1, 0));
        Run(gl, 66, b => F(b, 1, -1));
        Run(gl, 54, b => F(b, 1, 1));
        Run(gl, 66, b => F(b, 1, 1));
        Run(gl, 54, b => F(b, 0, 1));
        Run(gl, 66, b => F(b, -1, 1));
        Run(gl, 23);
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0xFFFFFFu, SurfacePixel(surface, 4, 4), "中间只取图像里的纹素:白");
        uint corner = SurfacePixel(surface, 0, 0);
        Assert.AreEqual(0xFFu, corner >> 16, "红色分量满");
        Assert.IsLessThan(0xC0u, (corner >> 8) & 0xFF, "角上混进了红色的边框(原先下标夹在图像里,是纯白)");
    }

    [TestMethod]
    public void DrawArrays每个顶点的数据按ARRAY_INFO的顺序读()
    {
        // Mesa 的间接 GLX 实际发的样子(velashell-xclients 里抓的):ARRAY_INFO 依次是颜色(4 × UNSIGNED_BYTE)、边标记、顶点(2 × FLOAT),
        // 每个顶点的数据也按这个顺序:颜色 4 字节、边标记 1 字节补齐到 4、顶点 8 字节。
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext();
        const uint vertexArray = 0x8074, colorArray = 0x8076, edgeFlagArray = 0x8079, floatType = 0x1406;
        Run(gl, 193, b =>
        {
            b.I32(4).I32(3).U32(Quads)
                .U32(UnsignedByte).I32(4).U32(colorArray)
                .U32(UnsignedByte).I32(1).U32(edgeFlagArray)
                .U32(floatType).I32(2).U32(vertexArray);
            foreach ((float x, float y) in (ReadOnlySpan<(float, float)>)[(-1, -1), (1, -1), (1, 1), (-1, 1)])
            {
                b.Bytes([0, 0, 255, 255]).Bytes([1, 0, 0, 0]);
                F(b, x, y);
            }
        });
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0x0000FFu, SurfacePixel(surface, 4, 4), "原先按「边标记、纹理、颜色……」的固定次序读:颜色读成了边标记那 4 个字节");
    }

    [TestMethod]
    public async Task 当前窗口被销毁后渲染与查询照常不报错_WaitGL回GLXBadCurrentWindow()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        await c.SendAsync(4, 0, b => b.U32(window));                                         // DestroyWindow:上下文还是当前的
        await RenderAsync(c, glx, tag, new Commands().Add(130, b => F(b, 1, 0, 0, 1)).Add(127, b => b.U32(ColorBit)));
        XMessage error = await c.RequestAsync(glx, 115, b => b.U32(tag));                  // GetError
        Assert.IsTrue(error.IsReply, "原先每个请求都回 GLXBadDrawable,Xlib 默认的错误处理让程序退出");
        Assert.AreEqual(0u, error.U32(8));
        Assert.IsTrue((await c.RequestAsync(glx, 108, b => b.U32(tag))).IsReply, "Finish");

        XMessage wait = await c.RequestAsync(glx, 8, b => b.U32(tag));                      // WaitGL
        Assert.IsTrue(wait.IsError);
        Assert.AreEqual(151 + 5, wait.Bytes[1], "GLXBadCurrentWindow");
        Assert.AreEqual(window, wait.U32(4), "带的是没了的那个窗口");

        XMessage released = await c.RequestAsync(glx, 5, b => b.U32(0).U32(0).U32(tag));    // 放下:照常
        Assert.IsTrue(released.IsReply);
    }

    [TestMethod]
    public async Task 超过表面上限的窗口夹到上限_只渲染左下一块并记日志()
    {
        using RecordingHost host = new();
        List<string> log = [];
        await using X11Server server = new(new X11ServerOptions { Log = line => { lock (log) { log.Add(line); } } }, host);
        await server.InvokeAsync(() => server.Glx.MaxSurfacePixels = 32 * 32);             // 60 × 40 的窗口超了:夹到 32 × 32
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);                                // 原先回 BadAlloc

        XMessage viewport = await c.RequestAsync(glx, 117, b => b.U32(tag).U32(0x0BA2));
        Assert.AreEqual((uint)Width, viewport.U32(40), "视口仍按整个窗口初始化");
        Assert.AreEqual((uint)Height, viewport.U32(44));
        await RenderAsync(c, glx, tag, new Commands()
            .Add(126, b => b.U32(0x0404))
            .Add(130, b => F(b, 1, 0, 0, 1))
            .Add(127, b => b.U32(ColorBit)));
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 1, Height - 1), "左下角画上了");
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 31, Height - 32));
        Assert.AreEqual(0u, await PixelAsync(c, window, 1, 1), "表面之外(左上)没画");
        Assert.AreEqual(0u, await PixelAsync(c, window, 40, Height - 1), "表面之外(右下)没画");
        Assert.IsNotEmpty(await ServerLog.WaitForAsync(log, line => line.Contains("over the surface limit", StringComparison.Ordinal)), "记一行日志");
    }

    [TestMethod]
    public async Task GLX1点2的窗口按视觉的配置建表面_单缓冲上下文先绑过_双缓冲上下文照样要交换才上屏()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);

        uint single = c.NewId();
        await c.SendAsync(glx, 24, b => b.U32(single).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        uint tag = (await c.RequestAsync(glx, 5, b => b.U32(window).U32(single).U32(0))).U32(8);   // 单缓冲的上下文先绑到窗口上
        await RenderAsync(c, glx, tag, new Commands().Add(130, b => F(b, 0, 0, 1, 1)).Add(127, b => b.U32(ColorBit)));
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 3, 3), "单缓冲的上下文画前缓冲,随请求上屏");
        await c.RequestAsync(glx, 5, b => b.U32(0).U32(0).U32(tag));

        (_, uint doubleTag) = await CurrentAsync(c, glx, window);                             // 双缓冲的上下文(GLX 1.2 的 CreateContext)
        await RenderAsync(c, glx, doubleTag, new Commands().Add(130, b => F(b, 1, 0, 0, 1)).Add(127, b => b.U32(ColorBit)));
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 3, 3), "画在后缓冲:交换之前不上屏(原先表面是第一个上下文定的单缓冲,直接上屏)");
        await c.SendAsync(glx, 11, b => b.U32(doubleTag).U32(window));
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 3, 3));
    }

    [TestMethod]
    public async Task GL_EXT_texture_object的厂商私有请求按核心的纹理命令处理()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);

        XMessage generated = await c.RequestAsync(glx, 17, b => b.U32(13).U32(tag).I32(2));       // GenTexturesEXT
        Assert.IsTrue(generated.IsReply, "原先一律回 GLXUnsupportedPrivateRequest");
        Assert.AreEqual(2u, generated.U32(4), "两个名字");
        uint name = generated.U32(32);
        Assert.AreNotEqual(0u, name);
        await RenderAsync(c, glx, tag, new Commands().Add(4117, b => b.U32(Texture2D).U32(name)));   // BindTextureEXT 与核心同一个渲染命令
        Assert.AreEqual(1u, (await c.RequestAsync(glx, 17, b => b.U32(14).U32(tag).U32(name))).U32(8), "IsTextureEXT");
        XMessage resident = await c.RequestAsync(glx, 17, b => b.U32(11).U32(tag).I32(1).U32(name));   // AreTexturesResidentEXT
        Assert.AreEqual(1u, resident.U32(8));
        Assert.AreEqual(1, resident.Bytes[32]);

        await c.SendAsync(glx, 16, b => b.U32(12).U32(tag).I32(1).U32(name));                       // DeleteTexturesEXT(VendorPrivate,不回复)
        Assert.AreEqual(0u, (await c.RequestAsync(glx, 17, b => b.U32(14).U32(tag).U32(name))).U32(8), "删掉了");

        XMessage unknown = await c.RequestAsync(glx, 17, b => b.U32(99).U32(tag));
        Assert.AreEqual(151 + 8, unknown.Bytes[1], "别的厂商码仍是 GLXUnsupportedPrivateRequest");
    }

    [TestMethod]
    public async Task 第一次用到反馈模式时记一行日志_每个上下文一次_求值器不再算没实现()
    {
        using RecordingHost host = new();
        List<string> log = [];
        await using X11Server server = new(new X11ServerOptions { Log = line => { lock (log) { log.Add(line); } } }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        const uint feedback = 0x1C01, render = 0x1C00;

        for (int i = 0; i < 3; i++)
        {
            await c.SendAsync(glx, 107, b => b.U32(tag).U32(feedback));                               // RenderMode(FEEDBACK)
            await c.RequestAsync(glx, 107, b => b.U32(tag).U32(render));
        }
        await RenderAsync(c, glx, tag, new Commands().Add(155, b => b.U32(0x1B02).I32(0).I32(10)));  // EvalMesh1
        await c.SyncAsync();
        // 日志放锁之后才交出去,回复可能先到:等它出现再数(见 ServerLog)。
        string[] feedbackLines = await ServerLog.WaitForAsync(log, line => line.Contains("Feedback", StringComparison.Ordinal));
        Assert.HasCount(1, feedbackLines, "结果落空不再无迹可查,同一样只记一次");
        lock (log)
        {
            Assert.AreEqual(0, log.Count(line => line.Contains("Evaluators", StringComparison.Ordinal)), "求值器已经实现(EvalMesh1 的模式 FILL 只记 GL 错误)");
        }
    }

    /// <summary>
    /// 求值器(xs_plan F23,§5.1):Map1f 定义一条二次 Bézier 曲线,EvalCoord1 在 u 处求值当作顶点发出(t = (u − u1)/(u2 − u1));
    /// Map2f + AUTO_NORMAL 的平面片法线由偏导数叉乘得出;GetMap 交回 ORDER / DOMAIN / COEFF;参数错误照规范记 INVALID_VALUE / INVALID_ENUM。
    /// 原先求值器的命令一律吃掉,GLUT 的茶壶、GLU 的 NURBS 曲面画不出来。
    /// </summary>
    [TestMethod]
    public void 求值器的曲线与曲面_自动法线_GetMap()
    {
        (Gl.GlContext gl, Gl.GlSurface surface) = DirectContext(16, 16);
        const uint map1Vertex3 = 0x0D97, map2Vertex3 = 0x0DB7, autoNormal = 0x0D80;
        // 二次曲线:控制点 (−1,−1,0)、(0,1,0)、(1,−1,0),定义域 [2, 4]:u = 3 时 t = 0.5,点在 (0, 0, 0)。
        Run(gl, 144, b => F(b.U32(map1Vertex3), 2, 4).I32(3).Bytes(F(new XTestClient.Body(bigEndian: false), -1, -1, 0, 0, 1, 0, 1, -1, 0).ToArray()));
        Run(gl, 139, b => b.U32(map1Vertex3));                // Enable(MAP1_VERTEX_3)
        Run(gl, 8, p => F(p, 1, 0, 0));
        Run(gl, 4, b => b.U32(0));                             // Begin(POINTS)
        Run(gl, 152, b => F(b, 3));                            // EvalCoord1f(3)
        Run(gl, 23);
        Assert.AreEqual(0u, gl.GetError());
        Assert.AreEqual(0xFF0000u, SurfacePixel(surface, 8, 8), "曲线中点在原点:视口中央");

        Gl.GlContext.GlValue order = gl.GetMap(map1Vertex3, 0x0A01)!.Value;
        CollectionAssert.AreEqual(new double[] { 3 }, order.Values);
        CollectionAssert.AreEqual(new double[] { 2, 4 }, gl.GetMap(map1Vertex3, 0x0A02)!.Value.Values);
        CollectionAssert.AreEqual(new double[] { -1, -1, 0, 0, 1, 0, 1, -1, 0 }, gl.GetMap(map1Vertex3, 0x0A00)!.Value.Values);
        CollectionAssert.AreEqual(new double[] { 1 }, gl.GetMap(map2Vertex3, 0x0A01)!.Value.Values.Take(1).ToArray(), "没定义过的图:order 1 的常量图");

        Run(gl, 144, b => F(b.U32(map1Vertex3), 0, 0).I32(1).Bytes(new byte[12]));    // u1 = u2
        Assert.AreEqual(0x0501u, gl.GetError());
        Run(gl, 144, b => F(b.U32(map1Vertex3), 0, 1).I32(9).Bytes(new byte[9 * 12])); // order > MAX_EVAL_ORDER
        Assert.AreEqual(0x0501u, gl.GetError());
        Run(gl, 144, b => F(b.U32(0x1234), 0, 1).I32(1).Bytes(new byte[12]));          // 目标不认识
        Assert.AreEqual(0x0500u, gl.GetError());
        Run(gl, 148, b => F(b.I32(0), 0, 1));                                           // MapGrid1f(n = 0)
        Assert.AreEqual(0x0501u, gl.GetError());

        // 平面片 z = 0(双线性,控制点按 R_ij = (i·vorder + j)·k 排):u 沿 x、v 沿 y,自动法线 = ∂q/∂u × ∂q/∂v = +z。
        Run(gl, 146, b => F(b.U32(map2Vertex3), 0, 1).I32(2).Bytes(F(new XTestClient.Body(bigEndian: false), 0, 1).ToArray()).I32(2)
            .Bytes(F(new XTestClient.Body(bigEndian: false), -1, -1, 0, -1, 1, 0, 1, -1, 0, 1, 1, 0).ToArray()));
        Assert.AreEqual(0u, gl.GetError());
        Run(gl, 139, b => b.U32(map2Vertex3));
        Run(gl, 139, b => b.U32(autoNormal));
        Run(gl, 139, b => b.U32(0x0B50));                     // LIGHTING
        Run(gl, 139, b => b.U32(0x4000));                     // LIGHT0:默认在 +z 方向
        Run(gl, 127, b => b.U32(ColorBit));
        Run(gl, 150, b => b.I32(4).Bytes(F(new XTestClient.Body(bigEndian: false), 0, 1).ToArray()).I32(4)
            .Bytes(F(new XTestClient.Body(bigEndian: false), 0, 1).ToArray()));      // MapGrid2f 4×4
        Run(gl, 157, b => b.U32(0x1B02).I32(0).I32(4).I32(0).I32(4));                  // EvalMesh2(FILL)
        Assert.AreEqual(0u, gl.GetError());
        uint lit = SurfacePixel(surface, 8, 8);
        Assert.IsGreaterThan(0x80u, lit >> 16, $"整片铺满视口,被正面光照亮(漫反射 0.8):法线朝 +z(实得 {lit:X6};法线反了只剩环境光 0x0A)");
        CollectionAssert.AreEqual(new double[] { 4, 4 }, gl.Query(0x0DD3)!.Value.Values, "MAP2_GRID_SEGMENTS");
    }

    [TestMethod]
    public void 画一千个带光照的小三角形几乎不分配内存()
    {
        (Gl.GlContext gl, _) = DirectContext(256, 256);
        foreach (uint cap in (uint[])[0x0B50, 0x4000, 0x4001, 0x0B57, DepthTest, 0x3000])   // LIGHTING、LIGHT0、LIGHT1、COLOR_MATERIAL、深度测试、CLIP_PLANE0
        {
            Run(gl, 139, b => b.U32(cap));
        }
        byte[] color = new XTestClient.Body(bigEndian: false).U32(BitConverter.SingleToUInt32Bits(0.5f)).U32(0).U32(0).ToArray();
        byte[][] vertices = new byte[3000][];
        for (int i = 0; i < vertices.Length; i += 3)
        {
            float x = ((i % 90) / 50f) - 0.9f, y = ((i / 90) / 20f) - 0.9f;
            vertices[i] = F(new XTestClient.Body(bigEndian: false), x, y, 0).ToArray();
            vertices[i + 1] = F(new XTestClient.Body(bigEndian: false), x + 0.05f, y, 0).ToArray();
            vertices[i + 2] = F(new XTestClient.Body(bigEndian: false), x, y + 0.05f, 0).ToArray();
        }
        void Draw()
        {
            gl.ExecuteOrCompile(4, BitConverter.GetBytes(Triangles), bigEndian: false);
            foreach (byte[] v in vertices)
            {
                gl.ExecuteOrCompile(8, color, bigEndian: false);
                gl.ExecuteOrCompile(70, v, bigEndian: false);
            }
            gl.ExecuteOrCompile(23, [], bigEndian: false);
        }
        Draw();   // 图元缓冲长到位
        long before = GC.GetAllocatedBytesForCurrentThread();
        Draw();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0u, gl.GetError());
        Assert.IsLessThan(64 * 1024, allocated, $"原先每个三角形约 650 字节(裁剪的 List、迭代器、窗口坐标数组),一千个约 0.7 MB;这次 {allocated} 字节");
    }

    [TestMethod]
    public async Task 细长的斜三角形按扫描线只走覆盖到的那一段_不再白扫整个包围盒()
    {
        // 工作量预算是确定的代价计数:原先每个三角形先按整个包围盒(这里约 1024²)扣,二十个就是两千多万单位;
        // 现在每行只扣边函数解出来的那一段,二十个不到一百万。
        await using X11Server server = new() { RequestWorkBudget = 4_000_000 };
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint pbuffer = c.NewId(), context = c.NewId();
        await c.SendAsync(glx, 27, b => b.U32(0).U32(SingleBufferedRgb).U32(pbuffer).U32(2).U32(0x8041).U32(1024).U32(0x8040).U32(1024));
        await c.SendAsync(glx, 24, b => b.U32(context).U32(SingleBufferedRgb).U32(0).U32(RgbaType).U32(0).U8(0).U8(0).U16(0));
        uint tag = (await c.RequestAsync(glx, 26, b => b.U32(0).U32(pbuffer).U32(pbuffer).U32(context))).U32(8);

        Commands commands = new Commands().Add(8, b => F(b, 1, 1, 1)).Add(4, b => b.U32(Triangles));
        for (int i = 0; i < 20; i++)
        {
            float o = i * 0.01f;
            commands.Add(66, b => F(b, -1 + o, -1)).Add(66, b => F(b, 1 + o, 1)).Add(66, b => F(b, 1 + o + 0.003f, 1));
        }
        await RenderAsync(c, glx, tag, commands.Add(23));
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag), "原先按包围盒计费:OUT_OF_MEMORY");
        XMessage pixel = await c.RequestAsync(glx, 111, b => b.U32(tag).I32(512).I32(512).I32(1).I32(1).U32(Rgba).U32(UnsignedByte).U8(0).U8(0).U16(0));
        Assert.AreEqual(255, pixel.Bytes[32], "对角线上画上了");
    }

    [TestMethod]
    public void CallList不复制列表_PushAttrib只深拷选中的属性组()
    {
        (Gl.GlContext gl, _) = DirectContext();
        byte[] color = F(new XTestClient.Body(bigEndian: false), 0.1f, 0.2f, 0.3f).ToArray();
        gl.NewList(1, Compile);
        for (int i = 0; i < 1000; i++)
        {
            gl.ExecuteOrCompile(8, color, bigEndian: false);
        }
        gl.EndList();
        byte[] callList = BitConverter.GetBytes(1u);
        gl.ExecuteOrCompile(1, callList, bigEndian: false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            gl.ExecuteOrCompile(1, callList, bigEndian: false);
        }
        long callListBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsLessThan(64 * 1024, callListBytes, $"原先每次 CallList 都 ToArray 一份(一千条约 24 KB,一百次 2.4 MB);这次 {callListBytes} 字节");

        byte[] currentBit = BitConverter.GetBytes(1u);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            gl.ExecuteOrCompile(142, currentBit, bigEndian: false);   // PushAttrib(CURRENT_BIT)
            gl.ExecuteOrCompile(141, [], bigEndian: false);           // PopAttrib
        }
        long attribBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsLessThan(1024 * 1024, attribBytes, $"原先不看 mask 整份深拷(八个光源、两份材质……),一千次约 2 MB 多;这次 {attribBytes} 字节");
        Assert.AreEqual(0u, gl.GetError());

        // 选中的组照样恢复:PushAttrib(LIGHTING_BIT) 之后改光源,PopAttrib 改回来。
        Run(gl, 142, b => b.U32(0x40));
        Run(gl, 87, b => F(b.U32(0x4000).U32(0x1201), 0.25f, 0.5f, 0.75f, 1));   // Lightfv(LIGHT0, DIFFUSE)
        Run(gl, 141);
        Assert.AreEqual(1.0, gl.GetLight(0x4000, 0x1201)!.Value.Values[0], "LIGHT0 的漫反射恢复成初值 1");
    }

    [TestMethod]
    public async Task RenderLarge拼到一半的缓冲记在客户端的内存账上_拼完退还_超了回BadAlloc()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 24L * 1024 * 1024 }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        byte[] clearColor = [.. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(1f)];
        long baseline = await server.InvokeAsync(() => server.MemoryInUse);

        // 声明 256 KB 的正文(ClearColor 后面跟一大段填充),小参数一段、填充四段。
        const int body = 256 * 1024;
        await c.SendAsync(glx, 2, b => b.U32(tag).U16(1).U16(5).U32(16).U32(8 + body).U32(130).Bytes(clearColor));
        await c.SyncAsync();
        Assert.IsGreaterThanOrEqualTo(baseline + body, await server.InvokeAsync(() => server.MemoryInUse), "拼正文的缓冲按声明的长度记账");
        byte[] quarter = new byte[(body - 16) / 4];
        for (int number = 2; number <= 5; number++)
        {
            int n = number;
            await c.SendAsync(glx, 2, b => b.U32(tag).U16((ushort)n).U16(5).U32((uint)quarter.Length).Bytes(quarter));
        }
        await c.SyncAsync();
        Assert.AreEqual(baseline, await server.InvokeAsync(() => server.MemoryInUse), "拼完执行之后退还");
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));

        // 声明 32 MB:超过每客户端 24 MiB 的账,第一段就回 BadAlloc。
        XMessage refused = await c.RequestAsync(glx, 2, b => b.U32(tag).U16(1).U16(2).U32(16).U32(8 + (32 * 1024 * 1024)).U32(130).Bytes(clearColor));
        Assert.IsTrue(refused.IsError);
        Assert.AreEqual(11, refused.Bytes[1], "BadAlloc");
    }

    [TestMethod]
    public void GenLists与GenTextures接在用过的最大名字之后_名字快用完时回到前面找空位()
    {
        (Gl.GlContext gl, _) = DirectContext();
        Assert.AreEqual(1u, gl.GenLists(3));
        Assert.AreEqual(4u, gl.GenLists(1));
        gl.DeleteLists(1, 3);
        Assert.AreEqual(5u, gl.GenLists(2), "接在最大的名字之后");
        gl.NewList(uint.MaxValue - 1, Compile);
        gl.EndList();
        Assert.AreEqual(1u, gl.GenLists(3), "后面接不下了:回到前面找第一个够大的空档");

        uint[] textures = gl.GenTextures(2)!;
        Assert.AreSequenceEqual(new uint[] { 1, 2 }, textures);
        Run(gl, 4117, b => b.U32(Texture2D).U32(uint.MaxValue));                   // 绑一个最大的名字
        uint[] more = gl.GenTextures(2)!;
        Assert.AreSequenceEqual(new uint[] { 3, 4 }, more, "回到 1 起找没用过的");
        Assert.AreEqual(0u, gl.GetError());
    }

    [TestMethod]
    public async Task 双缓冲交换只拷与窗口里不一样的那一块_只记那一块的损伤_被X画过的地方照样补回来()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        Commands Frame(float x) => new Commands()
            .Add(130, b => F(b, 0, 0, 1, 1)).Add(127, b => b.U32(ColorBit))                                     // 整个后缓冲清成蓝
            .Add(8, b => F(b, 1, 0, 0)).Add(4, b => b.U32(Triangles))
            .Add(66, b => F(b, x, 0)).Add(66, b => F(b, x + 0.2f, 0)).Add(66, b => F(b, x, 0.2f)).Add(23);      // 一个小红三角形

        await RenderAsync(c, glx, tag, Frame(0));
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));
        await c.SyncAsync();
        await host.WaitForAsync(() => !host.DamageRects.IsEmpty);
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 32, 18));
        host.DamageRects.Clear();

        await RenderAsync(c, glx, tag, Frame(0.1f));                                                            // 下一帧:三角形挪一点
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));
        await c.SyncAsync();
        await host.WaitForAsync(() => !host.DamageRects.IsEmpty);
        long damaged = host.DamageRects.Sum(r => (long)r.Width * r.Height);
        Assert.IsLessThan(Width * Height / 4, damaged, $"原先每次交换整窗({Width}×{Height})拷贝、整窗记损伤;这次 {damaged} 像素");
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 35, 18), "挪过去的三角形上了屏");
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 31, 18), "原来的位置还原成蓝");

        // 窗口左上角被 X 画成绿色:下一次交换比的是窗口里实际的像素,照样补回 GL 的内容。
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(window).U32(0x4).U32(0x00FF00));
        await c.SendAsync(70, 0, b => b.U32(window).U32(gc).I16(0).I16(0).U16(4).U16(4));
        Assert.AreEqual(0x00FF00u, await PixelAsync(c, window, 1, 1));
        await RenderAsync(c, glx, tag, Frame(0.1f));
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, window, 1, 1), "交换把被 X 画过的地方补回来");
    }

    /// <summary>CreateContextAttribsARB 的参数:context、fbconfig、screen、share_list、isdirect、保留、num_attribs,再跟属性对。</summary>
    private static Action<XTestClient.Body> ContextAttribs(uint context, bool direct, params uint[] attributes) => b =>
    {
        b.U32(context).U32(0x101).U32(0).U32(0).U8(direct ? (byte)1 : (byte)0).U8(0).U16(0).U32((uint)(attributes.Length / 2));
        foreach (uint value in attributes)
        {
            b.U32(value);
        }
    };

    [TestMethod]
    public async Task CreateContextAttribsARB_直接上下文按核心profile登记_间接上下文只给1点1的兼容profile()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        const uint major = 0x2091, minor = 0x2092, profileMask = 0x9126, core = 1, compatibility = 2;
        const byte badValue = 2, badMatch = 8, glxBadFbConfig = 151 + 9, glxBadProfile = 151 + 13;

        XMessage extensions = await c.RequestAsync(glx, 18, b => b.U32(0));   // QueryExtensionsString
        string names = Encoding.Latin1.GetString(extensions.Bytes, 32, (int)extensions.U32(12));
        Assert.Contains("GLX_ARB_create_context ", names);
        Assert.Contains("GLX_ARB_create_context_profile", names);

        // SetClientInfoARB / SetClientInfo2ARB:收下、不回错(原先 BadRequest)。
        await c.SendAsync(glx, 33, b => b.U32(1).U32(4).U32(1).U32(0).U32(0).U32(4).U32(5));
        await c.SendAsync(glx, 35, b => b.U32(1).U32(4).U32(1).U32(0).U32(0).U32(4).U32(5).U32(core));

        // 直接上下文要 3.3 核心 profile:服务端只登记(GL 在客户端)。
        uint direct = c.NewId();
        await c.SendAsync(glx, 34, ContextAttribs(direct, direct: true, major, 3, minor, 3, profileMask, core));
        XMessage isDirect = await c.RequestAsync(glx, 6, b => b.U32(direct));
        Assert.IsTrue(isDirect.IsReply, "直接上下文登记上了");
        Assert.AreEqual(1, isDirect.Bytes[8]);

        // 间接上下文:核心 profile 没有、比 1.1 高的版本给不了、不认识的属性不收、没定义的版本不收。
        XMessage coreProfile = await c.RequestAsync(glx, 34, ContextAttribs(c.NewId(), direct: false, major, 3, minor, 3, profileMask, core));
        Assert.AreEqual(glxBadProfile, coreProfile.Bytes[1], "GLXBadProfileARB");
        XMessage compatibility33 = await c.RequestAsync(glx, 34, ContextAttribs(c.NewId(), direct: false, major, 3, minor, 3, profileMask, compatibility));
        Assert.AreEqual(glxBadFbConfig, compatibility33.Bytes[1], "兼容 profile 也要 3.3:配置给不了");
        XMessage version21 = await c.RequestAsync(glx, 34, ContextAttribs(c.NewId(), direct: false, major, 2, minor, 1));
        Assert.AreEqual(glxBadFbConfig, version21.Bytes[1]);
        XMessage undefined = await c.RequestAsync(glx, 34, ContextAttribs(c.NewId(), direct: false, major, 1, minor, 9));
        Assert.AreEqual(badMatch, undefined.Bytes[1], "1.9 不是定义过的版本");
        XMessage unknown = await c.RequestAsync(glx, 34, ContextAttribs(c.NewId(), direct: false, 0x31B3, 1));
        Assert.AreEqual(badValue, unknown.Bytes[1]);

        // 缺省属性(1.0)的间接上下文照常能用。
        uint window = await MapWindowAsync(c, host);
        uint indirect = c.NewId();
        await c.SendAsync(glx, 34, ContextAttribs(indirect, direct: false, major, 1, minor, 1));
        XMessage made = await c.RequestAsync(glx, 5, b => b.U32(window).U32(indirect).U32(0));
        Assert.IsTrue(made.IsReply, "MakeCurrent");
        uint tag = made.U32(8);
        await RenderAsync(c, glx, tag, new Commands().Add(126, b => b.U32(0x0404)).Add(130, b => F(b, 1, 0, 0, 1)).Add(127, b => b.U32(ColorBit)));
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, window, 3, 3));
    }

    [TestMethod]
    public async Task RenderMode只在之前是反馈或选择模式时回复()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        const uint render = 0x1C00, feedback = 0x1C01;

        ushort toFeedback = await c.SendAsync(glx, 107, b => b.U32(tag).U32(feedback));   // 之前是渲染模式:没有回复
        await c.SyncAsync();
        Assert.IsFalse(await c.NextAsync(m => m.Sequence == toFeedback, 100).ContinueWith(t => t.IsCompletedSuccessfully), "没有回复");

        XMessage back = await c.RequestAsync(glx, 107, b => b.U32(tag).U32(render));        // 之前是反馈模式:有回复
        Assert.IsTrue(back.IsReply);
        Assert.AreEqual(0u, back.U32(12), "n = 0:反馈不实现");
        Assert.AreEqual(render, back.U32(16), "new mode");
    }

    /// <summary>
    /// 选择模式(xs_plan F23,GL 1.5 §5.2):gluPickMatrix 式的拾取 —— 与裁剪体相交的图元命中,名字栈变动时写命中记录
    /// (名字个数、最小 / 最大深度乘 2³²−1、名字自底向上),不相交的不命中、被剔除的不命中;RenderMode(RENDER) 返回记录条数,
    /// 回复带选择数据;选择模式不画进帧缓冲。原先 RenderMode 一律回 0 条,CAD 程序的拾取全部落空。
    /// </summary>
    [TestMethod]
    public async Task 选择模式记下命中的名字与深度_不相交和被剔除的不命中()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte glx = await GlxAsync(c);
        uint window = await MapWindowAsync(c, host);
        (_, uint tag) = await CurrentAsync(c, glx, window);
        const uint render = 0x1C00, select = 0x1C02, cullFace = 0x0B44;

        await c.SendAsync(glx, 107, b => b.U32(tag).U32(select));                           // 没给 SelectBuffer:INVALID_OPERATION、留在渲染模式
        Assert.AreEqual(0x0502u, await GlErrorAsync(c, glx, tag));

        await c.SendAsync(glx, 106, b => b.U32(tag).I32(16));                                // SelectBuffer(16)
        await c.SendAsync(glx, 107, b => b.U32(tag).U32(select));                           // RenderMode(SELECT)
        await RenderAsync(c, glx, tag, new Commands()
            .Add(121)                                                                        // InitNames
            .Add(125, b => b.U32(7))                                                         // PushName 7
            .Add(4, b => b.U32(Triangles))
            .Add(70, b => F(b, -0.5f, -0.5f, 0)).Add(70, b => F(b, 0.5f, -0.5f, 0.5f)).Add(70, b => F(b, 0, 0.5f, -0.5f))
            .Add(23)
            .Add(122, b => b.U32(8))                                                         // LoadName 8:写 7 的记录
            .Add(4, b => b.U32(Triangles))                                                   // 整个在裁剪体外:不命中
            .Add(70, b => F(b, 2, 2, 0)).Add(70, b => F(b, 3, 2, 0)).Add(70, b => F(b, 2, 3, 0))
            .Add(23)
            .Add(122, b => b.U32(9))                                                         // LoadName 9:8 没命中,不写
            .Add(139, b => b.U32(cullFace))                                                  // Enable CULL_FACE(默认剔除背面)
            .Add(4, b => b.U32(Triangles))                                                   // 顺时针 = 背面:被剔除,不命中
            .Add(70, b => F(b, -0.5f, -0.5f, 0)).Add(70, b => F(b, 0, 0.5f, 0)).Add(70, b => F(b, 0.5f, -0.5f, 0))
            .Add(23)
            .Add(125, b => b.U32(10))                                                        // PushName 10:栈是 9、10
            .Add(4, b => b.U32(0))                                                           // Begin(POINTS)
            .Add(70, b => F(b, 0, 0, 1))
            .Add(23));
        XMessage done = await c.RequestAsync(glx, 107, b => b.U32(tag).U32(render));        // RenderMode(RENDER):写最后一条
        Assert.AreEqual(2, (int)done.U32(8), "两条命中记录");
        uint n = done.U32(12);
        uint[] data = [.. Enumerable.Range(0, (int)n).Select(i => done.U32(32 + (4 * i)))];
        CollectionAssert.AreEqual(new uint[]
        {
            1, Gl.GlContext.DepthValue(0.25f), Gl.GlContext.DepthValue(0.75f), 7,   // 窗口 z = (z + 1) / 2
            2, uint.MaxValue, uint.MaxValue, 9, 10,
        }, data);
        Assert.AreEqual(render, done.U32(16));
        Assert.AreEqual(0u, await GlErrorAsync(c, glx, tag));
        await c.SendAsync(glx, 11, b => b.U32(tag).U32(window));
        Assert.AreEqual(0u, await PixelAsync(c, window, Width / 2, Height / 2), "选择模式不画进帧缓冲");
    }

    /// <summary>选择数组写不下:能写多少写多少,RenderMode 返回 −1;名字栈的错误(空栈 LoadName / PopName、满了再 Push)。</summary>
    [TestMethod]
    public void 选择数组溢出时返回负一_名字栈的错误()
    {
        (Gl.GlContext gl, _) = DirectContext(16, 16);
        gl.SelectBuffer(5);
        Assert.AreEqual(0, gl.RenderMode(0x1C02, out _));
        Run(gl, 122, b => b.U32(1));                     // LoadName 空栈
        Assert.AreEqual(0x0502u, gl.GetError());
        Run(gl, 124);                                    // PopName 空栈
        Assert.AreEqual(0x0504u, gl.GetError());
        for (int i = 0; i < Gl.GlContext.MaxNameStackDepth; i++)
        {
            Run(gl, 125, b => b.U32((uint)i));
        }
        Run(gl, 125, b => b.U32(99));
        Assert.AreEqual(0x0503u, gl.GetError(), "满了再 Push:STACK_OVERFLOW");
        Run(gl, 4, b => b.U32(0));
        Run(gl, 70, b => F(b, 0, 0, 0));
        Run(gl, 23);
        Assert.AreEqual(-1, gl.RenderMode(0x1C00, out uint[] data), "记录写不下:−1");
        CollectionAssert.AreEqual(new uint[] { 64, Gl.GlContext.DepthValue(0.5f), Gl.GlContext.DepthValue(0.5f), 0, 1 }, data, "能写多少写多少");
        Assert.AreEqual(0, gl.NameStackDepth, "RenderMode 清空名字栈");
    }
}
