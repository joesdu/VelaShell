// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.key  openssh-key-v1 容器格式
//   RFC 8017 §A.1.2       PKCS#1 RSAPrivateKey
//   RFC 5958              PKCS#8
//   RFC 5915              SEC1 EC 私钥
//   RFC 8410              PKCS#8 里的 Ed25519
//   行为规格:             velashell-docs/zh/ssh/design/architecture.md §8 第 5 项

using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

/// <summary>私钥文件的格式。</summary>
public enum SshPrivateKeyFormat
{
    /// <summary>认不出来。</summary>
    Unknown,

    /// <summary><c>-----BEGIN OPENSSH PRIVATE KEY-----</c>（OpenSSH 7.8 起的默认格式）。</summary>
    OpenSsh,

    /// <summary><c>-----BEGIN RSA PRIVATE KEY-----</c>（PKCS#1，老 OpenSSH 的默认）。</summary>
    Pkcs1Rsa,

    /// <summary><c>-----BEGIN EC PRIVATE KEY-----</c>（SEC1）。</summary>
    Sec1Ec,

    /// <summary><c>-----BEGIN PRIVATE KEY-----</c>（PKCS#8，未加密）。</summary>
    Pkcs8,

    /// <summary><c>-----BEGIN ENCRYPTED PRIVATE KEY-----</c>（PKCS#8，已加密）。</summary>
    Pkcs8Encrypted,

    /// <summary>PuTTY 的 <c>.ppk</c>（v2 或 v3）。</summary>
    Putty,
}

/// <summary>私钥文件读不出来。</summary>
public sealed class SshPrivateKeyException : SshException
{
    /// <summary>创建一个私钥读取异常。</summary>
    public SshPrivateKeyException(string message, Exception? innerException = null)
        : base(SshFailureReason.Unsupported, SshPhase.Authenticating, message, innerException)
    {
    }

    /// <summary>是不是因为需要口令（或口令不对）。</summary>
    public bool NeedsPassphrase { get; init; }
}

/// <summary>从文件或文本里读私钥。</summary>
public static class SshPrivateKeyFile
{
    private const string OpenSshMagic = "openssh-key-v1\0";

    /// <summary>KDF 轮数的上限。</summary>
    /// <remarks>
    /// 轮数来自文件本身，也就是来自不可信输入。<c>ssh-keygen</c> 默认写 16，
    /// <c>-a</c> 调到几百已经算重的了；这个上限是给「文件被改过」准备的，
    /// 没有它，一个伪造的 rounds 就能让「解一把私钥」挂在那里跑上几天。
    /// </remarks>
    private const uint MaxKdfRounds = 1_000_000;

    /// <summary>认一下这段 PEM 是什么格式。</summary>
    public static SshPrivateKeyFormat DetectFormat(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);

        if (PuttyPrivateKeyFile.IsPuttyKey(pem))
        {
            return SshPrivateKeyFormat.Putty;
        }
        if (pem.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.OpenSsh;
        }
        if (pem.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Pkcs8Encrypted;
        }
        if (pem.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Pkcs8;
        }
        if (pem.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Pkcs1Rsa;
        }
        if (pem.Contains("BEGIN EC PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Sec1Ec;
        }

        return SshPrivateKeyFormat.Unknown;
    }

    /// <summary>从文件读一把私钥。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="passphrase">口令；不需要就给 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// <b>读文件是异步的，解密是同步的 CPU 计算。</b>加密的 OpenSSH 私钥要跑
    /// <c>bcrypt_pbkdf</c>（默认 16 轮，高轮数可达秒级），PuTTY 的 <c>.ppk</c> v3 要跑 Argon2id ——
    /// 那是纯计算，没有可以 <c>await</c> 的 IO。
    /// </para>
    /// <para>
    /// 库<b>不替调用方</b>把它丢到线程池（那是「在库里 <c>Task.Run</c> 假装异步」，
    /// 而且服务端场景里只会多一次线程切换）。在 UI 线程上加载高轮数的加密私钥时，
    /// 由调用方决定是否 <c>await Task.Run(() =&gt; SshPrivateKeyFile.LoadAsync(...))</c>。
    /// </para>
    /// </remarks>
    public static async ValueTask<ISshSigner> LoadAsync(
        string path, string? passphrase = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string pem;
        try
        {
            pem = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new SshPrivateKeyException($"读不了私钥文件 {path}：{ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SshPrivateKeyException(
                $"没有权限读私钥文件 {path}。（Unix 上私钥应当是 0600。）", ex);
        }

        return Parse(pem, passphrase, path);
    }

    /// <summary>从一段 PEM 文本解出私钥。</summary>
    /// <param name="pem">PEM 文本。</param>
    /// <param name="passphrase">口令；不需要就给 <see langword="null"/>。</param>
    /// <param name="origin">出错消息里用来标明来源（通常是文件路径）。</param>
    public static ISshSigner Parse(string pem, string? passphrase = null, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(pem);

        SshPrivateKeyFormat format = DetectFormat(pem);
        string where = origin is null ? "" : $"（{origin}）";

        return format switch
        {
            SshPrivateKeyFormat.Putty => PuttyPrivateKeyFile.Parse(pem, passphrase, origin),
            SshPrivateKeyFormat.OpenSsh => ParseOpenSsh(pem, passphrase, where),
            SshPrivateKeyFormat.Pkcs8 or SshPrivateKeyFormat.Pkcs8Encrypted
                or SshPrivateKeyFormat.Pkcs1Rsa or SshPrivateKeyFormat.Sec1Ec =>
                ParseWithBcl(pem, passphrase, format, where),
            _ => throw new SshPrivateKeyException(
                $"认不出这个私钥格式{where}。支持的有：OpenSSH（BEGIN OPENSSH PRIVATE KEY）、" +
                "PKCS#8、PKCS#1（BEGIN RSA PRIVATE KEY）、SEC1（BEGIN EC PRIVATE KEY）、" +
                "以及 PuTTY 的 .ppk（v2 / v3）。"),
        };
    }

    // ------------------------------------------------------------ OpenSSH 格式

    /// <summary>解 <c>openssh-key-v1</c> 容器。</summary>
    /// <remarks>
    /// <code>
    /// "openssh-key-v1\0"
    /// string   ciphername
    /// string   kdfname
    /// string   kdfoptions
    /// uint32   密钥数 N
    /// string   公钥 1 … N
    /// string   加密并填充过的私钥区
    /// </code>
    /// </remarks>
    private static InMemorySshSigner ParseOpenSsh(string pem, string? passphrase, string where)
    {
        byte[] blob = DecodePemBody(pem, "OPENSSH PRIVATE KEY", where);

        byte[] magic = Encoding.ASCII.GetBytes(OpenSshMagic);
        if (blob.Length < magic.Length || !blob.AsSpan(0, magic.Length).SequenceEqual(magic))
        {
            throw new SshPrivateKeyException($"OpenSSH 私钥的魔数不对{where}。");
        }

        SshDataReader reader = new(new ReadOnlySequence<byte>(blob.AsMemory(magic.Length)));
        string cipherName = reader.ReadUtf8String(1024);
        string kdfName = reader.ReadUtf8String(1024);
        byte[] kdfOptions = reader.ReadStringAsArray(64 * 1024);
        uint keyCount = reader.ReadUInt32();

        if (keyCount != 1)
        {
            throw new SshPrivateKeyException(
                $"这个文件里有 {keyCount} 把密钥{where}，本库只处理一把。");
        }

        _ = reader.ReadString(64 * 1024);        // 公钥（下面从私钥里重新导出，不用它）
        byte[] privateSection = reader.ReadStringAsArray(1024 * 1024);

        // AEAD 的认证标签在那个 string 的**外面** —— 容器末尾的裸字节，不带长度前缀。
        // 非 AEAD 时这里是空的。误把它当成密文的尾部，会得到一个「开头解得出、
        // 标签永远验不过」的结果（流密码解前缀照样是对的），查起来极其费劲。
        byte[] tag = reader.ReadRemaining().ToArray();

        if (cipherName == "none" && kdfName == "none")
        {
            return ParseOpenSshPrivateSection(privateSection, encrypted: false, where);
        }

        byte[] decrypted = DecryptOpenSshSection(
            privateSection, tag, cipherName, kdfName, kdfOptions, passphrase, where);
        try
        {
            return ParseOpenSshPrivateSection(decrypted, encrypted: true, where);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    /// <summary>用 <c>bcrypt_pbkdf</c> 派生密钥，再解开私钥区。</summary>
    /// <remarks>
    /// <para>
    /// kdfoptions 的布局是 <c>string salt ‖ uint32 rounds</c>。
    /// 密钥与 IV 是**一次**派生出来的一整段材料，前面是密钥、紧接着是 IV ——
    /// 分两次派生会得到完全不同的值。
    /// </para>
    /// <para>
    /// 口令按 UTF-8 编码。ASCII 口令下这与任何实现都一致；非 ASCII 口令在各家客户端
    /// 之间本就不可移植（OpenSSH 直接用终端给的字节），而 UTF-8 是这里唯一讲得清的选择。
    /// </para>
    /// </remarks>
    private static byte[] DecryptOpenSshSection(
        byte[] section, byte[] tag, string cipherName, string kdfName, byte[] kdfOptions,
        string? passphrase, string where)
    {
        if (kdfName != "bcrypt")
        {
            throw new SshPrivateKeyException(
                $"不支持的 KDF “{kdfName}”{where}。OpenSSH 的加密私钥用的是 bcrypt。");
        }

        if (OpenSshKeyCipher.Describe(cipherName) is not { } shape)
        {
            throw new SshPrivateKeyException(
                $"不支持的加密算法 “{cipherName}”{where}。本库支持：" +
                string.Join("、", OpenSshKeyCipher.SupportedCipherNames) + "。" + Environment.NewLine +
                "换一种即可：ssh-keygen -p -Z aes256-ctr -f <私钥文件>");
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            throw new SshPrivateKeyException($"这是一把加密的 OpenSSH 私钥{where}，需要口令。")
            {
                NeedsPassphrase = true,
            };
        }

        byte[] salt;
        uint rounds;
        try
        {
            SshDataReader options = new(new ReadOnlySequence<byte>(kdfOptions));
            salt = options.ReadStringAsArray(1024);
            rounds = options.ReadUInt32();
        }
        catch (SshWireFormatException ex)
        {
            throw new SshPrivateKeyException($"OpenSSH 私钥的 kdfoptions 读不出来{where}。", ex);
        }

        if (salt.Length == 0 || rounds is 0 or > MaxKdfRounds)
        {
            // 这两个值来自文件，也就是来自不可信输入。上限不是洁癖：
            // 一个被改过的 rounds 能让「解一把私钥」挂在那里跑上几天。
            throw new SshPrivateKeyException(
                $"OpenSSH 私钥的 KDF 参数不合理{where}（盐 {salt.Length} 字节、{rounds} 轮）。");
        }

        if (section.Length == 0 || section.Length % shape.BlockBytes != 0)
        {
            throw new SshPrivateKeyException(
                $"OpenSSH 私钥的密文长度 {section.Length} 与算法 {cipherName} 对不上{where}，文件多半损坏了。");
        }

        if (tag.Length != shape.TagBytes)
        {
            throw new SshPrivateKeyException(
                $"OpenSSH 私钥的认证标签应当是 {shape.TagBytes} 字节，实际 {tag.Length} 字节{where}。");
        }

        byte[] material = new byte[shape.KeyBytes + shape.IvBytes];
        try
        {
            BcryptPbkdf.DeriveKey(Encoding.UTF8.GetBytes(passphrase), salt, (int)rounds, material);
            return OpenSshKeyCipher.Decrypt(cipherName, section, tag, material);
        }
        catch (CryptographicException ex)
        {
            // 认证类算法（GCM / ChaCha20-Poly1305）在这一步就能判定口令不对；
            // CTR / CBC 没有标签，要等下面校验字对不上才知道。
            throw new SshPrivateKeyException($"私钥解不开{where} —— 口令多半不对。", ex)
            {
                NeedsPassphrase = true,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    /// <summary>解私钥区（已经是明文的那一份）。</summary>
    /// <remarks>
    /// <code>
    /// uint32   checkint1
    /// uint32   checkint2   —— 必须与 checkint1 相同
    /// string   密钥类型
    /// …        类型相关字段
    /// string   注释
    /// byte[]   填充 1,2,3,…
    /// </code>
    /// </remarks>
    /// <param name="section">明文私钥区。</param>
    /// <param name="encrypted">这一份是不是刚解密出来的（决定校验字对不上时怎么报）。</param>
    /// <param name="where">出错消息里的来源标记。</param>
    private static InMemorySshSigner ParseOpenSshPrivateSection(byte[] section, bool encrypted, string where)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(section));

        uint check1 = reader.ReadUInt32();
        uint check2 = reader.ReadUInt32();

        if (check1 != check2)
        {
            // 两个校验字必然相同。CTR / CBC 没有认证标签，口令错了也照样“解”得出一堆
            // 随机字节 —— 这一步就是它们唯一的口令校验点，所以要按「口令不对」去报，
            // 而不是笼统地说文件坏了。
            throw new SshPrivateKeyException(
                encrypted
                    ? $"私钥解不开{where} —— 口令多半不对（校验字 {check1:X8} ≠ {check2:X8}）。"
                    : $"OpenSSH 私钥的校验字不匹配{where}（{check1:X8} ≠ {check2:X8}），文件多半损坏了。")
            {
                NeedsPassphrase = encrypted,
            };
        }

        string keyType = reader.ReadUtf8String(1024);

        return keyType switch
        {
            SshAlgorithmNames.SshEd25519 => ReadEd25519(ref reader, where),
            SshAlgorithmNames.SshRsa => ReadRsa(ref reader, where),
            SshAlgorithmNames.EcdsaSha2Nistp256 or SshAlgorithmNames.EcdsaSha2Nistp384
                or SshAlgorithmNames.EcdsaSha2Nistp521 => ReadEcdsa(ref reader, keyType, where),
            _ => throw new SshPrivateKeyException($"不支持的密钥类型 {keyType}{where}。"),
        };
    }

    private static InMemorySshSigner ReadEd25519(scoped ref SshDataReader reader, string where)
    {
        _ = reader.ReadString(64);                                  // 公钥
        byte[] secret = reader.ReadStringAsArray(128);              // 种子 ‖ 公钥

        if (secret.Length != 64)
        {
            throw new SshPrivateKeyException(
                $"Ed25519 私钥应当是 64 字节（种子 32 + 公钥 32），实际 {secret.Length} 字节{where}。");
        }

        return InMemorySshSigner.FromEd25519(secret.AsSpan(0, 32));
    }

    private static InMemorySshSigner ReadRsa(scoped ref SshDataReader reader, string where)
    {
        byte[] n = reader.ReadMpint(4096).ToArray();
        byte[] e = reader.ReadMpint(512).ToArray();
        byte[] d = reader.ReadMpint(4096).ToArray();
        byte[] iqmp = reader.ReadMpint(4096).ToArray();
        byte[] p = reader.ReadMpint(4096).ToArray();
        byte[] q = reader.ReadMpint(4096).ToArray();

        try
        {
            // OpenSSH 存的是 n/e/d/iqmp/p/q，而 RSAParameters 还要 DP 与 DQ。
            // 它们由 CRT 定义直接算出来：dp = d mod (p-1)、dq = d mod (q-1)。
            // 这是模运算，不是密码学原语。
            BigInteger bigD = ToPositive(d);
            BigInteger bigP = ToPositive(p);
            BigInteger bigQ = ToPositive(q);

            byte[] dp = ToFixedLength(bigD % (bigP - BigInteger.One), p.Length);
            byte[] dq = ToFixedLength(bigD % (bigQ - BigInteger.One), q.Length);

            RSAParameters parameters = new()
            {
                Modulus = TrimLeadingZero(n),
                Exponent = TrimLeadingZero(e),
                D = ToFixedLength(bigD, TrimLeadingZero(n).Length),
                P = TrimLeadingZero(p),
                Q = TrimLeadingZero(q),
                DP = dp,
                DQ = dq,
                InverseQ = ToFixedLength(ToPositive(iqmp), TrimLeadingZero(p).Length),
            };

            RSA rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return InMemorySshSigner.FromRsa(rsa);
        }
        catch (CryptographicException ex)
        {
            throw new SshPrivateKeyException($"RSA 私钥的参数不成立{where}：{ex.Message}", ex);
        }
    }

    private static InMemorySshSigner ReadEcdsa(scoped ref SshDataReader reader, string keyType, string where)
    {
        string curveName = reader.ReadUtf8String(64);
        byte[] point = reader.ReadStringAsArray(512);
        byte[] d = reader.ReadMpint(512).ToArray();

        (ECCurve curve, int coordinateBytes) = curveName switch
        {
            "nistp256" => (ECCurve.NamedCurves.nistP256, 32),
            "nistp384" => (ECCurve.NamedCurves.nistP384, 48),

            // nistp521 的坐标是 **66** 字节（521 位向上取整），不是 64。
            "nistp521" => (ECCurve.NamedCurves.nistP521, 66),
            _ => throw new SshPrivateKeyException($"不支持的曲线 {curveName}{where}。"),
        };

        if (point.Length != 1 + (coordinateBytes * 2) || point[0] != 0x04)
        {
            throw new SshPrivateKeyException(
                $"{keyType} 的公开点必须是未压缩形式（0x04 ‖ X ‖ Y），" +
                $"期望 {1 + (coordinateBytes * 2)} 字节，实际 {point.Length} 字节{where}。");
        }

        try
        {
            ECParameters parameters = new()
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = point.AsSpan(1, coordinateBytes).ToArray(),
                    Y = point.AsSpan(1 + coordinateBytes, coordinateBytes).ToArray(),
                },
                D = ToFixedLength(ToPositive(d), coordinateBytes),
            };

            ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(parameters);
            return InMemorySshSigner.FromEcdsa(ecdsa);
        }
        catch (CryptographicException ex)
        {
            throw new SshPrivateKeyException($"ECDSA 私钥的参数不成立{where}：{ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------ BCL 能直接读的格式

    private static InMemorySshSigner ParseWithBcl(
        string pem, string? passphrase, SshPrivateKeyFormat format, string where)
    {
        bool needsPassphrase = format == SshPrivateKeyFormat.Pkcs8Encrypted
            || pem.Contains("DEK-Info", StringComparison.Ordinal);

        if (needsPassphrase && string.IsNullOrEmpty(passphrase))
        {
            throw new SshPrivateKeyException($"这把私钥需要口令{where}。")
            {
                NeedsPassphrase = true,
            };
        }

        // 先按 RSA 试，再按 ECDSA 试 —— PEM 头部不总能区分
        // （PKCS#8 的 BEGIN PRIVATE KEY 对两者是一样的）。
        foreach (Func<InMemorySshSigner> attempt in new Func<InMemorySshSigner>[]
        {
            () => LoadRsaFromPem(pem, passphrase, needsPassphrase),
            () => LoadEcdsaFromPem(pem, passphrase, needsPassphrase),
        })
        {
            try
            {
                return attempt();
            }
            catch (CryptographicException)
            {
                // 换下一种试。真正的失败在循环外面报。
            }
            catch (ArgumentException)
            {
                // 同上。
            }
        }

        throw new SshPrivateKeyException(
            needsPassphrase
                ? $"私钥解不开{where} —— 口令多半不对。"
                : $"私钥读不出来{where}。它可能是 Ed25519 的 PKCS#8 " +
                  "（.NET 尚未支持导入这种），也可能文件已损坏。")
        {
            NeedsPassphrase = needsPassphrase,
        };
    }

    private static InMemorySshSigner LoadRsaFromPem(string pem, string? passphrase, bool encrypted)
    {
        RSA rsa = RSA.Create();
        if (encrypted)
        {
            rsa.ImportFromEncryptedPem(pem, passphrase);
        }
        else
        {
            rsa.ImportFromPem(pem);
        }
        return InMemorySshSigner.FromRsa(rsa);
    }

    private static InMemorySshSigner LoadEcdsaFromPem(string pem, string? passphrase, bool encrypted)
    {
        ECDsa ecdsa = ECDsa.Create();
        if (encrypted)
        {
            ecdsa.ImportFromEncryptedPem(pem, passphrase);
        }
        else
        {
            ecdsa.ImportFromPem(pem);
        }
        return InMemorySshSigner.FromEcdsa(ecdsa);
    }

    // ------------------------------------------------------------ 工具

    private static byte[] DecodePemBody(string pem, string label, string where)
    {
        string begin = $"-----BEGIN {label}-----";
        string end = $"-----END {label}-----";

        int start = pem.IndexOf(begin, StringComparison.Ordinal);
        int stop = pem.IndexOf(end, StringComparison.Ordinal);

        if (start < 0 || stop < 0 || stop <= start)
        {
            throw new SshPrivateKeyException($"PEM 的起止标记不完整{where}。");
        }

        string body = pem[(start + begin.Length)..stop];

        try
        {
            return Convert.FromBase64String(
                body.Replace("\r", "", StringComparison.Ordinal)
                    .Replace("\n", "", StringComparison.Ordinal)
                    .Replace(" ", "", StringComparison.Ordinal));
        }
        catch (FormatException ex)
        {
            throw new SshPrivateKeyException($"PEM 的 base64 正文解不开{where}。", ex);
        }
    }

    /// <summary>把大端字节当成**非负**整数读。</summary>
    private static BigInteger ToPositive(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    /// <summary>把整数写成固定长度的大端字节（左侧补零）。</summary>
    private static byte[] ToFixedLength(BigInteger value, int length)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

        if (bytes.Length == length)
        {
            return bytes;
        }

        if (bytes.Length > length)
        {
            // 只可能是前面多了零；真的超长说明参数不对。
            int extra = bytes.Length - length;
            for (int i = 0; i < extra; i++)
            {
                if (bytes[i] != 0)
                {
                    throw new SshPrivateKeyException("RSA/ECDSA 的参数比它该有的长度还长，文件多半损坏了。");
                }
            }
            return bytes.AsSpan(extra).ToArray();
        }

        byte[] padded = new byte[length];
        bytes.CopyTo(padded.AsSpan(length - bytes.Length));
        return padded;
    }

    /// <summary>去掉 mpint 为表示正数而加的那个前导零。</summary>
    private static byte[] TrimLeadingZero(byte[] value)
    {
        int index = 0;
        while (index < value.Length - 1 && value[index] == 0)
        {
            index++;
        }
        return index == 0 ? value : value.AsSpan(index).ToArray();
    }
}
