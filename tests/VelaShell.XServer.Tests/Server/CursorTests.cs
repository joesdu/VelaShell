using System.Text;
using VelaShell.XServer.Fonts;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>光标:字形光标烙成图像、隐形指针,以及交给宿主的样子。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class CursorTests
{
    private static async Task<uint> OpenFontAsync(XTestClient c, string name)
    {
        uint font = c.NewId();
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        await c.SendAsync(45, 0, b => b.U32(font).U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return font;
    }

    /// <summary>CreateGlyphCursor:前景白、背景黑。</summary>
    private static Task<ushort> CreateGlyphCursorAsync(XTestClient c, uint cursor, uint sourceFont, uint maskFont, ushort sourceChar, ushort maskChar) =>
        c.SendAsync(94, 0, b => b.U32(cursor).U32(sourceFont).U32(maskFont).U16(sourceChar).U16(maskChar)
            .U16(0xFFFF).U16(0xFFFF).U16(0xFFFF).U16(0).U16(0).U16(0));

    /// <summary>建一个映射着的顶层、设上光标,指针移进去。</summary>
    private static async Task<XTopLevelWindow> PointAtCursorAsync(XTestClient c, RecordingHost host, X11Server server, uint cursor)
    {
        uint top = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(top).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x4000).U32(cursor));   // cursor
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        server.InjectPointerMotion(host.Mapped[top], 5, 5);
        return host.Mapped[top];
    }

    [TestMethod]
    public async Task XFIXES的GetCursorImage给出指针处光标的图像_ChangeCursor换掉光标的样子()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage query = await c.RequestAsync(98, 0, b => b.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("XFIXES")).Pad());
        byte xfixes = query.Bytes[9];
        await c.RequestAsync(xfixes, 0, b => b.U32(5).U32(0));

        // 2×2 的位图光标:对角线是前景红,其余背景蓝,热点 (1, 1)。
        uint bitmap = c.NewId(), gc = c.NewId(), arrow = c.NewId();
        await c.SendAsync(53, 1, b => b.U32(bitmap).U32(c.RootWindow).U16(2).U16(2));
        await c.SendAsync(55, 0, b => b.U32(gc).U32(bitmap).U32(0));
        await c.SendAsync(72, 2, b => b.U32(bitmap).U32(gc).U16(2).U16(2).I16(0).I16(0).U8(0).U8(1).U16(0).U32(0b01).U32(0b10));
        await c.SendAsync(93, 0, b => b.U32(arrow).U32(bitmap).U32(0).U16(0xFFFF).U16(0).U16(0).U16(0).U16(0).U16(0xFFFF).U16(1).U16(1));
        byte[] name = Encoding.Latin1.GetBytes("pointer");
        await c.SendAsync(xfixes, 23, b => b.U32(arrow).U16((ushort)name.Length).U16(0).Bytes(name).Pad());   // SetCursorName
        await PointAtCursorAsync(c, host, server, arrow);
        await host.WaitForAsync(() => host.Cursor?.Image is { Width: 2 });

        XMessage image = await c.RequestAsync(xfixes, 4);   // GetCursorImage
        Assert.AreEqual((2, 2), (image.U16(12), image.U16(14)), "原先一律 1×1 的透明像素");
        Assert.AreEqual((1, 1), (image.U16(16), image.U16(18)), "热点");
        uint[] pixels = [.. Enumerable.Range(0, 4).Select(i => image.U32(32 + (i * 4)))];
        CollectionAssert.AreEqual(new[] { 0xFFFF0000u, 0xFF0000FFu, 0xFF0000FFu, 0xFFFF0000u }, pixels);

        XMessage named = await c.RequestAsync(xfixes, 25);   // GetCursorImageAndName
        Assert.AreEqual(2, named.U16(12));
        int nameLength = named.U16(28);   // 24 是 cursor-atom,28 是 nbytes
        Assert.AreEqual("pointer", Encoding.Latin1.GetString(named.Bytes, 32 + (4 * 4), nameLength));

        // ChangeCursor:正在用的光标换成隐形指针的样子,宿主跟着变。
        uint nil = await OpenFontAsync(c, "nil2");
        uint hidden = c.NewId();
        await CreateGlyphCursorAsync(c, hidden, nil, nil, 'X', ' ');
        await c.SendAsync(xfixes, 26, b => b.U32(hidden).U32(arrow));
        await host.WaitForAsync(() => host.Cursor?.Shape == XCursorShape.Hidden);
        Assert.AreEqual(1, (await c.RequestAsync(xfixes, 4)).U16(12), "隐形指针没有图像:1×1 透明");
    }

    [TestMethod]
    public async Task nil2字体的空白字形光标是隐形指针_别的字体的字形烙成图像_cursor字体照旧按形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint nil = await OpenFontAsync(c, "nil2");
        uint hidden = c.NewId();
        await CreateGlyphCursorAsync(c, hidden, nil, nil, 'X', ' ');   // xterm 的隐形指针
        await PointAtCursorAsync(c, host, server, hidden);
        await host.WaitForAsync(() => host.Cursor?.Shape == XCursorShape.Hidden);
        Assert.IsNull(host.Cursor!.Image, "原先非 cursor 字体的字形一律按默认箭头");

        uint fixedFont = await OpenFontAsync(c, "fixed");
        uint glyph = c.NewId();
        await CreateGlyphCursorAsync(c, glyph, fixedFont, 0, 'A', 0);   // 没有掩码:整个字形方框都显示
        await c.SendAsync(2, 0, b => b.U32(host.Mapped.Keys.Single()).U32(0x4000).U32(glyph));
        await host.WaitForAsync(() => host.Cursor?.Image is not null);
        XCursorImage image = host.Cursor!.Image!;
        Assert.AreEqual((6, 13), (image.Width, image.Height), "6x13 的 A 的方框");
        Assert.IsTrue(image.Pixels.ToArray().All(p => p is 0xFFFFFFFF or 0xFF000000), "前景白、背景黑,全都显示");
        Assert.Contains(0xFFFFFFFFu, image.Pixels.ToArray());
        Assert.AreEqual(XCursorShape.Arrow, host.Cursor.Shape);

        uint cursorFont = await OpenFontAsync(c, "cursor");
        uint text = c.NewId();
        await CreateGlyphCursorAsync(c, text, cursorFont, cursorFont, 152, 153);   // xterm(I 形)
        await c.SendAsync(2, 0, b => b.U32(host.Mapped.Keys.Single()).U32(0x4000).U32(text));
        await host.WaitForAsync(() => host.Cursor?.Shape == XCursorShape.Text);

        XMessage error = await c.RequestAsync(94, 0, b => b.U32(c.NewId()).U32(fixedFont).U32(0).U16(0x1234).U16(0)
            .U16(0).U16(0).U16(0).U16(0).U16(0).U16(0));
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(2, error.Detail, "字体里没定义的字形:BadValue");
    }

    [TestMethod]
    public async Task cursor字体的字形光标有真实的位图_GetCursorImage给出_没有对应系统光标的字形交给宿主图像()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage query = await c.RequestAsync(98, 0, b => b.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("XFIXES")).Pad());
        byte xfixes = query.Bytes[9];
        await c.RequestAsync(xfixes, 0, b => b.U32(5).U32(0));
        uint cursorFont = await OpenFontAsync(c, "cursor");

        // xterm(152,掩码 153):宿主照旧显示系统的 I 形光标,GetCursorImage 给 cursor.bdf 里的字形 —— 原先只有 1×1 透明像素。
        uint text = c.NewId();
        await CreateGlyphCursorAsync(c, text, cursorFont, cursorFont, 152, 153);
        await PointAtCursorAsync(c, host, server, text);
        await host.WaitForAsync(() => host.Cursor?.Shape == XCursorShape.Text);
        Assert.IsNull(host.Cursor!.Image, "有对应系统光标的字形:宿主按形状显示系统光标");
        XMessage image = await c.RequestAsync(xfixes, 4);   // GetCursorImage
        Assert.AreEqual((9, 16), (image.U16(12), image.U16(14)), "源字形 7×14 与掩码字形 9×16 的方框并集");
        Assert.AreEqual((4, 8), (image.U16(16), image.U16(18)), "热点是两个字形的原点");
        uint[] pixels = [.. Enumerable.Range(0, 9 * 16).Select(i => image.U32(32 + (i * 4)))];
        Assert.AreEqual(0xFF000000u, pixels[0], "左上角:掩码为 1、在源字形的方框外,背景黑");
        Assert.AreEqual(0u, pixels[4], "第一行掩码的缺口:透明");
        Assert.AreEqual(0xFFFFFFFFu, pixels[(8 * 9) + 4], "竖线中间:源为 1,前景白");
        Assert.AreEqual(0xFF000000u, pixels[(8 * 9) + 3], "竖线旁边:只有掩码,背景黑");
        Assert.AreEqual(0u, pixels[(8 * 9) + 1], "掩码之外");

        // pencil(86,掩码 87):没有对应的系统光标,宿主拿到字形的图像(原先显示成箭头)。
        uint pencil = c.NewId();
        await CreateGlyphCursorAsync(c, pencil, cursorFont, cursorFont, 86, 87);
        await c.SendAsync(2, 0, b => b.U32(host.Mapped.Keys.Single()).U32(0x4000).U32(pencil));
        await host.WaitForAsync(() => host.Cursor?.Image is { Width: 13 });
        Assert.AreEqual((13, 16, 11, 15), (host.Cursor!.Image!.Width, host.Cursor.Image.Height, host.Cursor.Image.HotspotX, host.Cursor.Image.HotspotY));
        Assert.AreEqual(XCursorShape.Arrow, host.Cursor.Shape, "形状推不出来:箭头(宿主不能显示图像时用)");
    }

    [TestMethod]
    public void cursor字体是XOrg的cursor位图字体_字形与掩码成对()
    {
        XFont font = FontCatalog.Open("cursor")!;
        Assert.AreEqual(154, font.Glyphs.Count, "cursorfont 的 0–153:偶数是形状、奇数是它的掩码");
        XGlyph xterm = font.Glyphs[152];
        Assert.AreEqual((7, 14), (xterm.BitmapWidth, xterm.BitmapHeight));
        Assert.IsTrue(xterm.IsSet(3, 5), "I 形的竖线");
        Assert.IsFalse(xterm.IsSet(0, 5));
        Assert.AreEqual(0x80, font.Glyphs[153].Bits[1], "xterm_mask 第一行是 f782,宽 9:宽度以外多出的位清零");
    }
}
