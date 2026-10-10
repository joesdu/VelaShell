using System.Text;
using VelaShell.Services.XServer;

namespace VelaShell.Tests.XServer;

/// <summary>系统剪贴板里的 HTML(xs_plan F15):Windows 的 CF_HTML 包与解,其余平台就是 UTF-8。</summary>
[TestClass]
[TestCategory("XServer")]
public sealed class HtmlClipboardTests
{
    [TestMethod]
    public void CF_HTML_头里的字节偏移指着片段_解回来就是原样()
    {
        const string html = "<b>表格</b> café";
        byte[] data = HtmlClipboard.Encode(html, windows: true);
        string text = Encoding.UTF8.GetString(data);
        StringAssert.StartsWith(text, "Version:0.9\r\nStartHTML:");
        int start = int.Parse(text.Split("StartFragment:")[1][..10], System.Globalization.CultureInfo.InvariantCulture);
        int end = int.Parse(text.Split("EndFragment:")[1][..10], System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(html, Encoding.UTF8.GetString(data, start, end - start), "偏移按 UTF-8 字节算");
        Assert.AreEqual(html, HtmlClipboard.Decode(data, windows: true));
        int startHtml = int.Parse(text.Split("StartHTML:")[1][..10], System.Globalization.CultureInfo.InvariantCulture);
        StringAssert.StartsWith(Encoding.UTF8.GetString(data, startHtml, data.Length - startHtml), "<html><body><!--StartFragment-->");
    }

    [TestMethod]
    public void 别的平台是UTF8_认不出的CF_HTML给null()
    {
        Assert.AreEqual("<i>x</i>", HtmlClipboard.Decode(HtmlClipboard.Encode("<i>x</i>", windows: false), windows: false));
        Assert.AreEqual("<p>ü</p>", HtmlClipboard.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<p>ü</p>")], windows: false), "带 UTF-16 字节序标记的");
        Assert.IsNull(HtmlClipboard.Decode(Encoding.ASCII.GetBytes("Version:0.9\r\n<html>"), windows: true), "没有偏移的头");
        Assert.IsNull(HtmlClipboard.Decode(Encoding.ASCII.GetBytes("StartFragment:0000000100\r\nEndFragment:0000000010\r\n"), windows: true), "偏移倒过来");
    }
}
