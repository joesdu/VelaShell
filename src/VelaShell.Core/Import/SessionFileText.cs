using System.Globalization;
using System.Text;

namespace VelaShell.Core.Import;

/// <summary>连接文件的字节 ↔ 文本:读时认 BOM、优先 UTF-8,退回本地 ANSI 代码页;写 CSV 时带 UTF-8 BOM。</summary>
public static class SessionFileText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 把文件字节解成文本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 顺序:BOM(UTF-8 / UTF-16)→ 严格 UTF-8 → 本地 ANSI 代码页。最后这一步不是摆设:中文 Windows 上的 Excel
    /// 「另存为 CSV(逗号分隔)」存的是 GBK,只有「CSV UTF-8」那一项才是 UTF-8 —— 而前者恰恰是默认的那一项。
    /// 严格 UTF-8 解不开的字节流几乎不可能是 UTF-8,这时按本地代码页解是对的那一种猜法。
    /// </para>
    /// <para>
    /// 代码页先看区域格式(<see cref="CultureInfo.CurrentCulture" />);它不是中日韩、而界面语言是的时候改用界面语言的代码页 ——
    /// 英文区域格式下用中文界面的人,手里的 CSV 多半还是 GBK。
    /// </para>
    /// </remarks>
    /// <param name="bytes">文件内容。</param>
    /// <returns>文本(不含 BOM)。</returns>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(span[3..]);
        }
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(span[2..]);
        }
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(span[2..]);
        }
        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return LegacyEncoding().GetString(span);
        }
    }

    /// <summary>把 CSV 文本编成带 BOM 的 UTF-8 —— 没有 BOM,Excel 会按本地代码页打开,中文全成乱码。</summary>
    /// <param name="text">CSV 文本。</param>
    /// <returns>文件字节。</returns>
    public static byte[] EncodeCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)];
    }

    /// <summary>JSON 写成不带 BOM 的 UTF-8(JSON 规范不许带 BOM)。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>文件字节。</returns>
    public static byte[] EncodeJson(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.UTF8.GetBytes(text);
    }

    private static Encoding LegacyEncoding()
    {
        int codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        int uiCodePage = CultureInfo.CurrentUICulture.TextInfo.ANSICodePage;
        if (!IsCjk(codePage) && IsCjk(uiCodePage))
        {
            codePage = uiCodePage;
        }
        try
        {
            // 直接问代码页提供程序,不依赖进程启动时有没有全局注册过它(单元测试里就没有)。
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    private static bool IsCjk(int codePage) => codePage is 932 or 936 or 949 or 950;
}
