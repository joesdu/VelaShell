using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 主密码的恢复码:128 位随机数,外加 1 字节校验,写成 28 个 Crockford base32 字符、每 4 个一组
/// (<c>ABCD-EFGH-…</c>,共 7 组)。
/// </summary>
/// <remarks>
/// <para>
/// 开启主密码时生成一份(可跳过),由用户自己抄下;它与主密码一样能解开本机加密密钥(包裹里另有一条用它解的记录),
/// 而且同样要配着系统密钥链里的条目才有用 —— 只拿走数据目录与恢复码解不开。
/// </para>
/// <para>
/// 128 位随机数不怕离线穷举,所以派生用 HKDF 而不是 Argon2id;校验字节只为了把抄错一位说成「恢复码写错了」,
/// 而不是「恢复码不对」。字母表去掉了 I / L / O / U,输入时不分大小写,I、L 当 1、O 当 0,空格与连字符忽略。
/// </para>
/// </remarks>
internal static class RecoveryCode
{
    /// <summary>熵的字节数。</summary>
    public const int EntropySize = 16;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int EncodedLength = 28; // 17 字节 = 136 位 → 28 个 5 位字符(补 4 个零位)
    private const int GroupSize = 4;

    private static ReadOnlySpan<byte> ChecksumDomain => "VelaShell recovery code v1\0"u8;

    /// <summary>生成一份新的恢复码。</summary>
    /// <returns>给人看的写法与它的 16 字节熵(调用方用完清零)。</returns>
    public static (string Display, byte[] Entropy) Generate()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(EntropySize);
        return (Format(entropy), entropy);
    }

    /// <summary>16 字节熵 → 给人看的写法。</summary>
    public static string Format(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != EntropySize)
        {
            throw new ArgumentException(@"A recovery code carries 16 bytes.", nameof(entropy));
        }
        Span<byte> payload = stackalloc byte[EntropySize + 1];
        entropy.CopyTo(payload);
        payload[EntropySize] = Checksum(entropy);
        StringBuilder text = new(EncodedLength + (EncodedLength / GroupSize));
        int buffer = 0;
        int bits = 0;
        int written = 0;
        foreach (byte b in payload)
        {
            buffer = ((buffer << 8) | b) & 0x1FFF; // 只留还没写出去的那几位
            bits += 8;
            while (bits >= 5)
            {
                Append(text, Alphabet[(buffer >> (bits - 5)) & 31], ref written);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            Append(text, Alphabet[(buffer << (5 - bits)) & 31], ref written);
        }
        CryptographicOperations.ZeroMemory(payload);
        return text.ToString();

        static void Append(StringBuilder text, char c, ref int written)
        {
            if (written > 0 && written % GroupSize == 0)
            {
                text.Append('-');
            }
            text.Append(c);
            written++;
        }
    }

    /// <summary>
    /// 解析用户输入的恢复码。字符认不出、长度不对、校验不通过都为 false。
    /// </summary>
    /// <param name="text">用户输入。</param>
    /// <param name="entropy">解出的 16 字节(调用方用完清零)。</param>
    public static bool TryParse(string? text, out byte[] entropy)
    {
        entropy = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[EntropySize + 1];
        int buffer = 0;
        int bits = 0;
        int index = 0;
        int count = 0;
        foreach (char raw in text)
        {
            if (raw is '-' or ' ' or '\t')
            {
                continue;
            }
            int value = Decode(raw);
            if (value < 0 || ++count > EncodedLength)
            {
                return false;
            }
            buffer = ((buffer << 5) | value) & 0x1FFF;
            bits += 5;
            if (bits >= 8)
            {
                if (index == payload.Length)
                {
                    return false;
                }
                payload[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }
        // 28 个字符正好 17 字节外加 4 个补位零。
        if (count != EncodedLength || index != payload.Length || (buffer & ((1 << bits) - 1)) != 0)
        {
            CryptographicOperations.ZeroMemory(payload);
            return false;
        }
        bool valid = Checksum(payload[..EntropySize]) == payload[EntropySize];
        if (valid)
        {
            entropy = payload[..EntropySize].ToArray();
        }
        CryptographicOperations.ZeroMemory(payload);
        return valid;
    }

    private static int Decode(char c) => char.ToUpperInvariant(c) switch
    {
        'I' or 'L' => 1,
        'O' => 0,
        var upper => Alphabet.IndexOf(upper, StringComparison.Ordinal)
    };

    private static byte Checksum(ReadOnlySpan<byte> entropy)
    {
        Span<byte> input = stackalloc byte[ChecksumDomain.Length + EntropySize];
        ChecksumDomain.CopyTo(input);
        entropy.CopyTo(input[ChecksumDomain.Length..]);
        byte check = SHA256.HashData(input)[0];
        CryptographicOperations.ZeroMemory(input);
        return check;
    }
}
