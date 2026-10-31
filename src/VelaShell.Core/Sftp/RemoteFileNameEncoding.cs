using System.Text;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 远端文件名按什么编码解读:取会话的字符集(<c>ConnectionInfo.FileNameEncoding</c>,与终端同一个口径)。
/// SFTP 文件名(Infrastructure 的 <c>SftpConnectOptions</c>)与远端搜索的 exec 输出共用这一条规则,
/// 同一台机器上两处看到的名字才是同一个。
/// </summary>
/// <remarks>
/// <b>UTF-8、空白、认不出的名字一律给 <see langword="null" /></b>,调用方照各自的默认走:SFTP 交给库的 UTF-8 无损往返,
/// 远端搜索照旧按 UTF-8 解。认不出的名字(手改坏的配置)退回默认,而不是抛异常 —— 与终端那边 <c>ResolveEncoding</c> 退回 UTF-8 同理。
/// GBK / Big5 一族要 <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c> 之后才取得到(<c>Program.Main</c> 已注册);
/// 没注册时取不到,同样退回默认。UTF-8 按代码页判,<c>utf8</c>、<c>UTF-8</c> 之类的别名都算。
/// </remarks>
public static class RemoteFileNameEncoding
{
    /// <summary>字符集名 → 文件名编码;UTF-8、空白或认不出时为 <see langword="null" />。</summary>
    /// <param name="name">字符集名(如 <c>GBK</c>)。</param>
    public static Encoding? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(name.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
        return encoding.CodePage == Encoding.UTF8.CodePage ? null : encoding;
    }

    /// <summary>
    /// 远端给的一串名字字节 → 字符串,与 SFTP 库解文件名同一条规则(<c>VelaShell.Ssh.Sftp.SftpNameCodec</c>,
    /// 规格 velashell-docs <c>zh/ssh/spec/06-sftp.md</c> §4.7)。
    /// </summary>
    /// <remarks>
    /// 给了编码就按它解;<see langword="null" />(UTF-8)时合法的 UTF-8 照常解,解不开的每个字节 <c>b</c> 单独变成落单的低代理
    /// <c>U+DC00 + b</c> —— 不是 U+FFFD。U+FFFD 编回去是 <c>EF BF BD</c>,拿它去 SFTP 开、定位,到服务器的已经不是那串字节了;
    /// 同一个名字在远端搜索与文件面板里也就对不上(复查 file-panes F4)。
    /// </remarks>
    /// <param name="bytes">名字的原始字节。</param>
    /// <param name="encoding">会话的文件名编码(<see cref="Resolve" /> 的结果);null = UTF-8。</param>
    public static string Decode(ReadOnlySpan<byte> bytes, Encoding? encoding)
    {
        if (encoding is not null)
        {
            return encoding.GetString(bytes);
        }
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }
        var builder = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out Rune rune, out int consumed) == System.Buffers.OperationStatus.Done)
            {
                builder.Append(rune);
                bytes = bytes[consumed..];
            }
            else
            {
                // 一个字节一个字节地转义,下一个字节重新开始解:合法的部分不会被一起吞掉(与库里那一份同一个做法)。
                builder.Append((char)(0xDC00 + bytes[0]));
                bytes = bytes[1..];
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 字符串 → 远端那一串名字字节,与 <see cref="Decode" /> 互逆;编不<b>准</b>时返回 <see langword="null" />,而不是换成「?」或 U+FFFD。
    /// </summary>
    /// <remarks>
    /// 给了编码时按它严格编(编不进去的字符 → null);<see langword="null" />(UTF-8)时落单的 U+DC80–DCFF 还原成那个字节,
    /// 别的落单代理项编不准(→ null)。
    /// </remarks>
    /// <param name="text">名字。</param>
    /// <param name="encoding">会话的文件名编码;null = UTF-8。</param>
    public static byte[]? EncodeExact(string text, Encoding? encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (encoding is not null)
        {
            var strict = (Encoding)encoding.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
            try
            {
                return strict.GetBytes(text);
            }
            catch (EncoderFallbackException)
            {
                return null;
            }
        }
        var output = new List<byte>(text.Length + 8);
        Span<byte> buffer = stackalloc byte[4];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                int written = new Rune(c, text[++i]).EncodeToUtf8(buffer);
                output.AddRange(buffer[..written]);
            }
            else if (c is >= '\uDC80' and <= '\uDCFF')
            {
                output.Add((byte)(c - 0xDC00));
            }
            else if (Rune.TryCreate(c, out Rune rune))
            {
                output.AddRange(buffer[..rune.EncodeToUtf8(buffer)]);
            }
            else
            {
                return null;
            }
        }
        return [.. output];
    }
}
