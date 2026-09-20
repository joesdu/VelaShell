using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把 BCL 能加载的私钥(RSA / ECDSA;PKCS#1、PKCS#8、SEC1,含加密 PKCS#8)序列化为
/// OpenSSH 私钥格式(未加密)。
/// </summary>
/// <remarks>
/// Tmds.Ssh 0.23 的私钥解析器【只认 OpenSSH 格式】(-----BEGIN OPENSSH PRIVATE KEY-----)。
/// 用户导入的传统 PEM —— PKCS#1(-----BEGIN RSA PRIVATE KEY-----)、PKCS#8
/// (-----BEGIN PRIVATE KEY-----)、加密 PKCS#8 —— 会被判 "Unsupported format" 而当作无可用凭据
/// 【跳过】,认证以 "skipped: publickey" 失败。这里在连接前把它们转成 OpenSSH 格式补齐兼容性。
/// Ed25519 传统 PEM 因 BCL 不支持而不在【转换】之列(但 Ed25519 几乎总是 OpenSSH 格式,Tmds 直接可读);
/// 本应用自己生成的 Ed25519 走 <see cref="SerializeEd25519" />,不经 BCL,不受这条限制。
/// </remarks>
internal static class OpenSshPrivateKey
{
    /// <summary>
    /// 尝试把任意 PEM 私钥转成 OpenSSH 格式(未加密)。已是 OpenSSH 格式或无法识别时返回
    /// <see langword="null" />(调用方应原样把文件路径交给 Tmds 处理)。
    /// </summary>
    /// <param name="pem">私钥 PEM 文本。</param>
    /// <param name="passphrase">加密私钥的口令(仅加密 PKCS#8 需要),可为 null/空。</param>
    public static char[]? TryConvertToOpenSsh(string pem, string? passphrase)
    {
        if (pem.TrimStart().StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal))
        {
            return null; // 已是 OpenSSH:原样交给 Tmds(它也负责按口令解密 OpenSSH 加密私钥)。
        }
        const string comment = "velashell-imported";
        if (TryLoadRsa(pem, passphrase, out RSAParameters rsa))
        {
            return SerializeRsa(rsa, comment).ToCharArray();
        }
        if (TryLoadEcdsa(pem, passphrase, out ECParameters ec, out string? sshName, out string? curveName, out int fieldLen))
        {
            return SerializeEcdsa(ec, sshName!, curveName!, fieldLen, comment).ToCharArray();
        }
        return null; // 未知格式/缺口令:交回原路径,让 Tmds 给出其原生错误。
    }

    /// <summary>把 RSA 私钥参数序列化为 OpenSSH 私钥 PEM(cipher/kdf=none,未加密)。</summary>
    public static string SerializeRsa(RSAParameters p, string comment)
    {
        byte[] publicBlob = BuildRsaPublicBlob(p);

        using var priv = new MemoryStream();
        WriteCheckInts(priv);
        WriteChunk(priv, "ssh-rsa"u8.ToArray());
        WriteChunk(priv, ToMpint(p.Modulus!));  // n
        WriteChunk(priv, ToMpint(p.Exponent!)); // e
        WriteChunk(priv, ToMpint(p.D!));         // d
        WriteChunk(priv, ToMpint(p.InverseQ!));  // iqmp = q^-1 mod p
        WriteChunk(priv, ToMpint(p.P!));         // p
        WriteChunk(priv, ToMpint(p.Q!));         // q
        WriteChunk(priv, Encoding.UTF8.GetBytes(comment));

        return Wrap(publicBlob, priv);
    }

    /// <summary>把 Ed25519 的 32 字节种子与公钥序列化为 OpenSSH 私钥 PEM(未加密)。</summary>
    /// <remarks>
    /// OpenSSH 的私钥字段存的是 <b>seed ‖ pub 共 64 字节</b>,不是那 32 字节种子本身
    /// —— 这是 RFC 8032 里 <c>ssh-keygen</c> 沿用 NaCl 的习惯(`crypto_sign` 的 secret key
    /// 就是这么拼的)。只写种子的话文件结构合法、`ssh-keygen -y` 也读得出来,
    /// 但公钥字段与私钥字段对不上,验证会当场失败。
    /// </remarks>
    /// <param name="seed">32 字节私钥种子。</param>
    /// <param name="publicKey">由种子导出的 32 字节公钥。</param>
    /// <param name="comment">写进私钥与公钥的注释。</param>
    public static string SerializeEd25519(ReadOnlySpan<byte> seed, byte[] publicKey, string comment)
    {
        byte[] publicBlob = BuildEd25519PublicBlob(publicKey);

        using var priv = new MemoryStream();
        WriteCheckInts(priv);
        WriteChunk(priv, "ssh-ed25519"u8.ToArray());
        WriteChunk(priv, publicKey);
        WriteChunk(priv, [.. seed, .. publicKey]); // seed ‖ pub,见上
        WriteChunk(priv, Encoding.UTF8.GetBytes(comment));

        return Wrap(publicBlob, priv);
    }

    /// <summary>
    /// 构造 OpenSSH ecdsa-sha2-* 公钥 blob:string 算法名 ‖ string 曲线名 ‖ string 未压缩公开点。
    /// </summary>
    /// <param name="q">公开点。</param>
    /// <param name="sshName">SSH 算法名,如 <c>ecdsa-sha2-nistp256</c>。</param>
    /// <param name="curveName">SSH 曲线名,如 <c>nistp256</c>。</param>
    /// <param name="fieldLen">该曲线的坐标定长字节数(32 / 48 / 66)。</param>
    public static byte[] BuildEcdsaPublicBlob(ECPoint q, string sshName, string curveName, int fieldLen)
    {
        using var stream = new MemoryStream();
        WriteChunk(stream, Encoding.ASCII.GetBytes(sshName));
        WriteChunk(stream, Encoding.ASCII.GetBytes(curveName));
        WriteChunk(stream, BuildEcPoint(q, fieldLen));
        return stream.ToArray();
    }

    /// <summary>构造 OpenSSH ssh-ed25519 公钥 blob:string "ssh-ed25519" ‖ string pub(32 字节定长)。</summary>
    /// <remarks>公钥是定长字节串,<b>不是 mpint</b> —— 不裁前导零也不补符号位,原样写进去。</remarks>
    public static byte[] BuildEd25519PublicBlob(byte[] publicKey)
    {
        using var stream = new MemoryStream();
        WriteChunk(stream, "ssh-ed25519"u8.ToArray());
        WriteChunk(stream, publicKey);
        return stream.ToArray();
    }

    /// <summary>把 ECDSA(nistp256/384/521)私钥序列化为 OpenSSH 私钥 PEM(未加密)。</summary>
    public static string SerializeEcdsa(ECParameters p, string sshName, string curveName, int fieldLen, string comment)
    {
        byte[] point = BuildEcPoint(p.Q, fieldLen); // 0x04 ‖ X ‖ Y(各定长)
        byte[] sshNameBytes = Encoding.ASCII.GetBytes(sshName);
        byte[] curveNameBytes = Encoding.ASCII.GetBytes(curveName);
        byte[] publicBlob = BuildEcdsaPublicBlob(p.Q, sshName, curveName, fieldLen);

        using var priv = new MemoryStream();
        WriteCheckInts(priv);
        WriteChunk(priv, sshNameBytes);
        WriteChunk(priv, curveNameBytes);
        WriteChunk(priv, point);
        WriteChunk(priv, ToMpint(p.D!)); // 私有标量
        WriteChunk(priv, Encoding.UTF8.GetBytes(comment));

        return Wrap(publicBlob, priv);
    }

    // ---- 加载(BCL)----------------------------------------------------------

    private static bool TryLoadRsa(string pem, string? passphrase, out RSAParameters parameters)
    {
        parameters = default;
        try
        {
            using var rsa = RSA.Create();
            ImportPem(rsa, pem, passphrase);
            parameters = rsa.ExportParameters(includePrivateParameters: true);
            return true;
        }
        catch
        {
            return false; // 非 RSA、加密但缺口令、或格式非 BCL 可读 —— 交由后续尝试/回退。
        }
    }

    private static bool TryLoadEcdsa(
        string pem, string? passphrase,
        out ECParameters parameters, out string? sshName, out string? curveName, out int fieldLen)
    {
        parameters = default;
        sshName = null;
        curveName = null;
        fieldLen = 0;
        try
        {
            using var ecdsa = ECDsa.Create();
            ImportPem(ecdsa, pem, passphrase);
            (sshName, curveName, fieldLen) = DescribeCurve(ecdsa.KeySize);
            if (sshName is null)
            {
                return false; // 非标准 NIST 曲线:不支持,回退。
            }
            parameters = ecdsa.ExportParameters(includePrivateParameters: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 曲线位数 → (SSH 算法名, SSH 曲线名, 坐标定长)。非标准 NIST 曲线返回一组 null / 0。
    /// </summary>
    /// <remarks>
    /// 导入转换与生成两条路都要这张表,放在一处 —— P-521 的坐标是 <b>66</b> 字节(521 位向上取整),
    /// 不是 65,这类数字抄第二遍就是抄错的开始。
    /// </remarks>
    public static (string? SshName, string? CurveName, int FieldLength) DescribeCurve(int keySize) =>
        keySize switch
        {
            256 => ("ecdsa-sha2-nistp256", "nistp256", 32),
            384 => ("ecdsa-sha2-nistp384", "nistp384", 48),
            521 => ("ecdsa-sha2-nistp521", "nistp521", 66),
            _ => (null, null, 0)
        };

    private static void ImportPem(AsymmetricAlgorithm key, string pem, string? passphrase)
    {
        if (!string.IsNullOrEmpty(passphrase))
        {
            try
            {
                key.ImportFromEncryptedPem(pem, passphrase);
                return;
            }
            catch
            {
                // 用户填了口令但密钥其实未加密:退回明文导入。
            }
        }
        key.ImportFromPem(pem);
    }

    // ---- OpenSSH 线格式助手 -------------------------------------------------

    /// <summary>套上外层容器与 PEM 封装:magic ‖ none ‖ none ‖ "" ‖ 密钥数=1 ‖ 公钥 blob ‖ 私钥段。</summary>
    private static string Wrap(byte[] publicBlob, MemoryStream privateSection)
    {
        for (byte pad = 1; privateSection.Length % 8 != 0; pad++) // cipher=none,块大小 8,递增填充
        {
            privateSection.WriteByte(pad);
        }

        using var outer = new MemoryStream();
        outer.Write("openssh-key-v1\0"u8); // 15 字节魔数,含结尾 NUL,非长度前缀
        WriteChunk(outer, "none"u8.ToArray()); // ciphername
        WriteChunk(outer, "none"u8.ToArray()); // kdfname
        WriteChunk(outer, []);                  // kdfoptions
        WriteUInt32(outer, 1);                   // 密钥数量
        WriteChunk(outer, publicBlob);
        WriteChunk(outer, privateSection.ToArray());

        string base64 = Convert.ToBase64String(outer.ToArray());
        var pem = new StringBuilder();
        pem.Append("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < base64.Length; i += 70)
        {
            pem.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        }
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return pem.ToString();
    }

    private static void WriteCheckInts(MemoryStream stream)
    {
        uint checkInt = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        WriteUInt32(stream, checkInt);
        WriteUInt32(stream, checkInt); // 两次相同,解密后自校验
    }

    private static byte[] BuildRsaPublicBlob(RSAParameters p)
    {
        using var stream = new MemoryStream();
        WriteChunk(stream, "ssh-rsa"u8.ToArray());
        WriteChunk(stream, ToMpint(p.Exponent!));
        WriteChunk(stream, ToMpint(p.Modulus!));
        return stream.ToArray();
    }

    /// <summary>EC 公开点的未压缩编码:0x04 ‖ X ‖ Y,X/Y 各左补零至曲线定长。</summary>
    private static byte[] BuildEcPoint(ECPoint q, int fieldLen)
    {
        byte[] point = new byte[1 + fieldLen * 2];
        point[0] = 0x04;
        LeftPadInto(q.X!, point.AsSpan(1, fieldLen));
        LeftPadInto(q.Y!, point.AsSpan(1 + fieldLen, fieldLen));
        return point;
    }

    private static void LeftPadInto(byte[] value, Span<byte> destination)
    {
        // BCL 通常已按定长返回;多余前导零裁掉、不足则左补零,保证落在 destination 尾部。
        int start = 0;
        while (start < value.Length - destination.Length && value[start] == 0)
        {
            start++;
        }
        ReadOnlySpan<byte> trimmed = value.AsSpan(start);
        destination.Clear();
        trimmed.CopyTo(destination[(destination.Length - trimmed.Length)..]);
    }

    /// <summary>转 SSH mpint:大端有符号,裁前导零后若最高位为 1 再补一个 0x00。</summary>
    private static byte[] ToMpint(byte[] value)
    {
        int start = 0;
        while (start < value.Length - 1 && value[start] == 0)
        {
            start++;
        }
        byte[] trimmed = value[start..];
        return trimmed.Length > 0 && (trimmed[0] & 0x80) != 0 ? [0, .. trimmed] : trimmed;
    }

    private static void WriteChunk(MemoryStream stream, byte[] data)
    {
        WriteUInt32(stream, (uint)data.Length);
        stream.Write(data);
    }

    private static void WriteUInt32(MemoryStream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
