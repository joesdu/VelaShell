using System.Globalization;
using System.Text;
using Avalonia.Input;

namespace VelaShell.Services.XServer;

/// <summary>
/// 系统剪贴板里的 HTML(X 程序的 <c>text/html</c> 与本机之间互通用):各平台的格式名与字节布局不同 ——
/// Windows 是「HTML Format」(CF_HTML:几行带字节偏移的头,再包一层 &lt;html&gt;&lt;body&gt; 与 StartFragment / EndFragment 注释),
/// macOS 是 <c>public.html</c>,其余(X11 / Wayland)就是 <c>text/html</c> 的 UTF-8。
/// </summary>
internal static class HtmlClipboard
{
    /// <summary>这个平台上 HTML 的剪贴板格式。</summary>
    public static DataFormat<byte[]> Format { get; } = DataFormat.CreateBytesPlatformFormat(
        OperatingSystem.IsWindows() ? "HTML Format" : OperatingSystem.IsMacOS() ? "public.html" : "text/html");

    /// <summary>HTML 片段 → 这个平台剪贴板里的字节。</summary>
    public static byte[] Encode(string html) => Encode(html, OperatingSystem.IsWindows());

    /// <summary>这个平台剪贴板里的字节 → HTML 片段;认不出来为 null。</summary>
    public static string? Decode(byte[] data) => Decode(data, OperatingSystem.IsWindows());

    private const string StartComment = "<!--StartFragment-->", EndComment = "<!--EndFragment-->";

    /// <summary>
    /// <paramref name="windows" /> 时按 CF_HTML 包:头里的 StartHTML / EndHTML / StartFragment / EndFragment 是从数据开头算的 UTF-8 字节偏移,
    /// 各占 10 位(先用固定宽度的占位排出头的长度,再填真实的数)。
    /// </summary>
    internal static byte[] Encode(string html, bool windows)
    {
        if (!windows)
        {
            return Encoding.UTF8.GetBytes(html);
        }
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        string prefix = "<html><body>" + StartComment, suffix = EndComment + "</body></html>";
        int headerLength = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        return Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, header, startHtml, endHtml, startFragment, endFragment) + prefix + html + suffix);
    }

    /// <summary>
    /// <paramref name="windows" /> 时按 CF_HTML 解:取 StartFragment 到 EndFragment 之间的字节(UTF-8);头不全时退到整段 HTML(StartHTML / EndHTML)。
    /// 否则整段按 UTF-8 解(带 UTF-16 的字节序标记时按 UTF-16)。
    /// </summary>
    internal static string? Decode(byte[] data, bool windows)
    {
        if (!windows)
        {
            return data is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(data, 2, data.Length - 2) : Encoding.UTF8.GetString(data);
        }
        string ascii = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 512));
        int? start = Offset(ascii, "StartFragment:") ?? Offset(ascii, "StartHTML:");
        int? end = Offset(ascii, "EndFragment:") ?? Offset(ascii, "EndHTML:");
        if (start is not { } s || end is not { } e || s < 0 || e > data.Length || e < s)
        {
            return null;
        }
        return Encoding.UTF8.GetString(data, s, e - s);

        static int? Offset(string header, string key)
        {
            int at = header.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }
            int from = at + key.Length, to = from;
            while (to < header.Length && char.IsAsciiDigit(header[to]))
            {
                to++;
            }
            return int.TryParse(header.AsSpan(from, to - from), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
        }
    }
}
