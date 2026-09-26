// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   PuTTY 的 .ppk 格式说明(PuTTY 用户手册附录 C / puttygen 文档)
//   RFC 9106  Argon2（v3 的口令派生）
//
// 为什么 .ppk 的加密能做而 OpenSSH 的不能：
//   · .ppk v2 的 KDF 是「SHA-1 拼几下」,v3 用 Argon2id ——
//     前者只需要 SHA-1(BCL 有),后者 BouncyCastle 直接提供。
//     两者都是**用现成原语装配**,不必自己写原语。
//   · OpenSSH 的 bcrypt_pbkdf 要拿到 Blowfish 的密钥编排内部,
//     那是原语本身。见 SshPrivateKeyFile。

using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

/// <summary>读 PuTTY 的 <c>.ppk</c> 私钥。</summary>
/// <remarks>
/// 支持 v2 与 v3，加密与不加密都行。
/// <para>
/// <b>MAC 会被校验。</b>不校验的话，一个被改过的 .ppk 可能让我们
/// 用一把「不是你以为的那把」密钥去认证 —— 而 .ppk 的公私钥部分
/// 是分开存的，改其中一半不会让解析失败。
/// </para>
/// </remarks>
internal static class PuttyPrivateKeyFile
{
    private const string MacKeyPhrase = "putty-private-key-file-mac-key";

    /// <summary>这段文本是不是 <c>.ppk</c>。</summary>
    public static bool IsPuttyKey(string text) =>
        text is not null && text.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal);

    /// <summary>解一段 <c>.ppk</c> 文本。</summary>
    public static InMemorySshSigner Parse(string text, string? passphrase = null, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string where = origin is null ? "" : $"（{origin}）";

        var file = PuttyFile.Parse(text, where);

        if (file.Encryption == "none")
        {
            VerifyMac(file, macKey: DeriveMacKey(file, passphrase: null), where);
            return BuildSigner(file.PublicBlob, file.PrivateBlob, file.Algorithm, where);
        }

        if (file.Encryption != "aes256-cbc")
        {
            throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"不支持的 .ppk 加密方式 {file.Encryption}{where}。");
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyPassphraseRequired, $"这把 .ppk 需要口令{where}。");
        }

        (byte[] key, byte[] iv, byte[] macKey) = DeriveKeys(file, passphrase, where);
        byte[] privateBlob = DecryptCbc(file.PrivateBlob, key, iv, where);

        PuttyFile decrypted = file with { PrivateBlob = privateBlob };

        // ⚠️ **先验 MAC 再用私钥。**口令错了的症状就是 MAC 对不上 ——
        //    不验的话我们会拿一堆乱码去构造密钥，报出来的错
        //    会是「RSA 参数不成立」之类，而真正的原因是「口令打错了」。
        VerifyMac(decrypted, macKey, where, wrongPassphraseLikely: true);

        return BuildSigner(file.PublicBlob, privateBlob, file.Algorithm, where);
    }

    // ------------------------------------------------------------ 文件结构

    private sealed record PuttyFile
    {
        public required int Version { get; init; }

        public required string Algorithm { get; init; }

        public required string Encryption { get; init; }

        public required string Comment { get; init; }

        public required byte[] PublicBlob { get; init; }

        public required byte[] PrivateBlob { get; init; }

        public required byte[] Mac { get; init; }

        // v3 才有。
        public string KeyDerivation { get; init; } = "";

        public int Argon2Memory { get; init; }

        public int Argon2Passes { get; init; }

        public int Argon2Parallelism { get; init; }

        public byte[] Argon2Salt { get; init; } = [];

        public static PuttyFile Parse(string text, string where)
        {
            // .ppk 的头部键名是大小写敏感的，序数比较正合适 ——
            // 而 string 的默认相等比较器就是序数比较。
            Dictionary<string, string> headers = [];
            byte[] publicBlob = [];
            byte[] privateBlob = [];

            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            int index = 0;
            int version = 0;

            while (index < lines.Length)
            {
                string line = lines[index++];
                if (line.Length == 0)
                {
                    continue;
                }

                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                {
                    continue;
                }

                string name = line[..colon];
                string value = line[(colon + 1)..].Trim();

                if (name.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
                {
                    version = int.Parse(
                        name["PuTTY-User-Key-File-".Length..], CultureInfo.InvariantCulture);
                    headers["Algorithm"] = value;
                    continue;
                }

                if (name is "Public-Lines" or "Private-Lines")
                {
                    int count = int.Parse(value, CultureInfo.InvariantCulture);
                    StringBuilder body = new();

                    for (int i = 0; i < count && index < lines.Length; i++)
                    {
                        body.Append(lines[index++].Trim());
                    }

                    byte[] blob;
                    try
                    {
                        blob = Convert.FromBase64String(body.ToString());
                    }
                    catch (FormatException ex)
                    {
                        throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $".ppk 的 {name} 段解不开{where}。", ex);
                    }

                    if (name == "Public-Lines")
                    {
                        publicBlob = blob;
                    }
                    else
                    {
                        privateBlob = blob;
                    }
                    continue;
                }

                headers[name] = value;
            }

            if (version is not (2 or 3))
            {
                throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                    $"不支持的 .ppk 版本 {version}{where}（本库支持 2 与 3）。");
            }

            return new PuttyFile
            {
                Version = version,
                Algorithm = headers.GetValueOrDefault("Algorithm", ""),
                Encryption = headers.GetValueOrDefault("Encryption", "none"),
                Comment = headers.GetValueOrDefault("Comment", ""),
                PublicBlob = publicBlob,
                PrivateBlob = privateBlob,
                Mac = ParseHex(headers.GetValueOrDefault("Private-MAC", ""), where),
                KeyDerivation = headers.GetValueOrDefault("Key-Derivation", ""),
                Argon2Memory = ParseInt(headers.GetValueOrDefault("Argon2-Memory", "0")),
                Argon2Passes = ParseInt(headers.GetValueOrDefault("Argon2-Passes", "0")),
                Argon2Parallelism = ParseInt(headers.GetValueOrDefault("Argon2-Parallelism", "0")),
                Argon2Salt = ParseHex(headers.GetValueOrDefault("Argon2-Salt", ""), where),
            };
        }

        private static int ParseInt(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : 0;

        private static byte[] ParseHex(string hex, string where)
        {
            if (hex.Length == 0)
            {
                return [];
            }

            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException ex)
            {
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $".ppk 里的十六进制字段格式不对{where}。", ex);
            }
        }
    }

    // ------------------------------------------------------------ 口令派生

    private static (byte[] Key, byte[] Iv, byte[] MacKey) DeriveKeys(
        PuttyFile file, string passphrase, string where)
    {
        if (file.Version == 3)
        {
            // v3：Argon2 一次产出 80 字节 —— 32 字节密钥 + 16 字节 IV + 32 字节 MAC 密钥。
            // 变体与参数的校验都在 Argon2 里。
            byte[] material = Argon2(file, passphrase, where);
            return (material[..32], material[32..48], material[48..80]);
        }

        // v2：两段 SHA-1 拼出 32 字节密钥，IV 全零，MAC 密钥另算。
        //
        // SHA-1 在这里不是当抗碰撞散列用的 —— 它是 PuTTY 定下的口令派生构造，
        // 换算法就读不了任何已有的 .ppk 了。
#pragma warning disable CA5350 // .ppk v2 的 KDF 由格式规定就是 SHA-1
        byte[] passBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] first = SHA1.HashData([0, 0, 0, 0, .. passBytes]);
        byte[] second = SHA1.HashData([0, 0, 0, 1, .. passBytes]);
#pragma warning restore CA5350

        byte[] key = [.. first, .. second[..12]];
        return (key, new byte[16], DeriveMacKey(file, passphrase));
    }

    /// <summary>Argon2 的内存参数上限（KiB）。PuTTYgen 默认 8 MiB。</summary>
    internal const int MaxArgon2MemoryKiB = 256 * 1024;

    /// <summary>Argon2 的遍数上限。PuTTYgen 按「约 100 ms」自动调，通常在十几遍。</summary>
    internal const int MaxArgon2Passes = 256;

    /// <summary>Argon2 的并行度上限。</summary>
    internal const int MaxArgon2Parallelism = 16;

    /// <summary>内存 × 遍数的上限（KiB·遍）：两个都顶到各自上限也要不了这么多。</summary>
    internal const long MaxArgon2Work = 8L * 1024 * 1024;

    private static byte[] Argon2(PuttyFile file, string passphrase, string where)
    {
        // 这三个参数来自文件，也就是来自不可信输入，而且**在验 MAC 之前**就要用上 ——
        // MAC 密钥本身就是 Argon2 的输出。不设上限的话，Argon2-Memory 写一个 4194304
        // 就让 BouncyCastle 先分配 4 GiB，Argon2-Passes 写一个二十亿就是永远算不完，
        // 而这段计算同步进行、中途停不下来。
        if (file.Argon2Memory < 8 * file.Argon2Parallelism
            || file.Argon2Memory > MaxArgon2MemoryKiB
            || file.Argon2Passes is < 1 or > MaxArgon2Passes
            || file.Argon2Parallelism is < 1 or > MaxArgon2Parallelism
            || (long)file.Argon2Memory * file.Argon2Passes > MaxArgon2Work
            || file.Argon2Salt.Length == 0)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                $".ppk 的 Argon2 参数不合理{where}（内存 {file.Argon2Memory} KiB、{file.Argon2Passes} 遍、" +
                $"并行度 {file.Argon2Parallelism}、盐 {file.Argon2Salt.Length} 字节）。" +
                $"上限是内存 {MaxArgon2MemoryKiB} KiB、{MaxArgon2Passes} 遍、并行度 {MaxArgon2Parallelism}。");
        }

        // 三种变体 PuTTY 都可能写（格式文档允许）。曾经一律按 Argon2id 算，
        // 另外两种的文件就永远过不了 MAC，被报成「口令不对」。
        int variant = file.KeyDerivation switch
        {
            "Argon2id" => Argon2Parameters.Argon2id,
            "Argon2i" => Argon2Parameters.Argon2i,
            "Argon2d" => Argon2Parameters.Argon2d,
            _ => throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"不支持的 .ppk v3 口令派生方式 {file.KeyDerivation}{where}。"),
        };

        Argon2Parameters parameters = new Argon2Parameters.Builder(variant)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(file.Argon2Salt)
            .WithMemoryAsKB(file.Argon2Memory)
            .WithIterations(file.Argon2Passes)
            .WithParallelism(file.Argon2Parallelism)
            .Build();

        Argon2BytesGenerator generator = new();
        generator.Init(parameters);

        byte[] output = new byte[80];
        generator.GenerateBytes(Encoding.UTF8.GetBytes(passphrase), output);
        return output;
    }

    private static byte[] DeriveMacKey(PuttyFile file, string? passphrase)
    {
        if (file.Version == 3)
        {
            // v3 的 MAC 密钥来自 Argon2 的输出，由调用方给。
            return [];
        }

#pragma warning disable CA5350 // 同上：格式规定
        byte[] input = passphrase is null
            ? Encoding.UTF8.GetBytes(MacKeyPhrase)
            : [.. Encoding.UTF8.GetBytes(MacKeyPhrase), .. Encoding.UTF8.GetBytes(passphrase)];

        return SHA1.HashData(input);
#pragma warning restore CA5350
    }

    private static byte[] DecryptCbc(byte[] ciphertext, byte[] key, byte[] iv, string where)
    {
        if (ciphertext.Length % 16 != 0)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                $".ppk 的私钥区长度不是 16 的倍数{where}，文件多半损坏了。");
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;

        // **不要让 AES 去掉填充。**.ppk 的私钥区没有 PKCS#7 填充，
        // 它是按内部字段长度自描述的；让 AES 按 PKCS#7 解会把最后一块判成非法。
        aes.Padding = PaddingMode.None;

        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>校验 MAC。</summary>
    /// <remarks>
    /// <para>
    /// MAC 算的是**解密之后**的私钥区 —— 这正是「口令不对」能被当场发现的原因。
    /// 若算的是密文，错口令会得到一个合法的 MAC，错误要等到用那把「密钥」
    /// 去签名时才冒出来，而那时的报错是「RSA 参数不成立」，与真正的原因无关。
    /// </para>
    /// <para>
    /// ⚠️ <b>私钥区在加密前会被补齐到块长，而格式文档没说清 MAC 覆不覆盖补的那几个字节。</b>
    /// 所以两种都试：先按「含补齐」算，不对再按「私钥字段的自然长度」算。
    /// 试两次的代价是一次 HMAC，而赌错的代价是读不了真实的 .ppk。
    /// </para>
    /// </remarks>
    private static void VerifyMac(
        PuttyFile file, byte[] macKey, string where, bool wrongPassphraseLikely = false)
    {
        if (file.Mac.Length == 0)
        {
            return;   // 没有 MAC 字段的老文件
        }

        foreach (int length in CandidateLengths(file))
        {
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteUtf8String(file.Algorithm);
            writer.WriteUtf8String(file.Encryption);
            writer.WriteUtf8String(file.Comment);
            writer.WriteString(file.PublicBlob);
            writer.WriteString(file.PrivateBlob.AsSpan(0, length));

            byte[] actual = file.Version == 3
                ? HMACSHA256.HashData(macKey, buffer.WrittenSpan.ToArray())
#pragma warning disable CA5350 // .ppk v2 的 MAC 由格式规定就是 HMAC-SHA1
                : HMACSHA1.HashData(macKey, buffer.WrittenSpan.ToArray());
#pragma warning restore CA5350

            if (CryptographicOperations.FixedTimeEquals(actual, file.Mac))
            {
                return;
            }
        }

        throw new SshPrivateKeyException(
            wrongPassphraseLikely ? SshFailureReason.KeyPassphraseIncorrect : SshFailureReason.KeyFormatInvalid,
            wrongPassphraseLikely
                ? $".ppk 的 MAC 对不上{where} —— 口令多半不对。"
                : $".ppk 的 MAC 对不上{where}，文件可能被改过或已损坏。");
    }

    /// <summary>MAC 可能覆盖到私钥区的哪些长度。</summary>
    private static IEnumerable<int> CandidateLengths(PuttyFile file)
    {
        yield return file.PrivateBlob.Length;

        int natural = NaturalLength(file.PrivateBlob, file.Algorithm);
        if (natural > 0 && natural != file.PrivateBlob.Length)
        {
            yield return natural;
        }
    }

    /// <summary>私钥字段本身占多少字节（不含补齐）。</summary>
    /// <remarks>私钥区是自描述的（一串 mpint），所以自然长度是算得出来的。</remarks>
    private static int NaturalLength(byte[] blob, string algorithm)
    {
        int fields = algorithm switch
        {
            SshAlgorithmNames.SshRsa => 4,
            SshAlgorithmNames.SshEd25519 => 1,
            SshAlgorithmNames.EcdsaSha2Nistp256 or SshAlgorithmNames.EcdsaSha2Nistp384
                or SshAlgorithmNames.EcdsaSha2Nistp521 => 1,
            _ => 0,
        };

        if (fields == 0)
        {
            return 0;
        }

        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(blob));
            for (int i = 0; i < fields; i++)
            {
                reader.ReadMpint(4096);
            }
            return (int)reader.Consumed;
        }
        catch (SshWireFormatException)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------ 造签名器

    private static InMemorySshSigner BuildSigner(
        byte[] publicBlob, byte[] privateBlob, string algorithm, string where)
    {
        SshDataReader priv = new(new ReadOnlySequence<byte>(privateBlob));

        return algorithm switch
        {
            SshAlgorithmNames.SshEd25519 => BuildEd25519(ref priv),
            SshAlgorithmNames.SshRsa => BuildRsa(publicBlob, ref priv, where),
            SshAlgorithmNames.EcdsaSha2Nistp256 or SshAlgorithmNames.EcdsaSha2Nistp384 or SshAlgorithmNames.EcdsaSha2Nistp521 => BuildEcdsa(publicBlob, ref priv, algorithm, where),
            _ => throw new SshPrivateKeyException(SshFailureReason.Unsupported, $".ppk 里是不支持的密钥类型 {algorithm}{where}。"),
        };
    }

    private static InMemorySshSigner BuildEd25519(scoped ref SshDataReader priv)
    {
        byte[] seed = priv.ReadMpint(256).ToArray();

        // mpint 可能带一个前导零，也可能短于 32 字节 —— 两种都要归一到 32。
        byte[] normalized = new byte[32];
        ReadOnlySpan<byte> trimmed = seed.Length > 32 ? seed.AsSpan(seed.Length - 32) : seed;
        trimmed.CopyTo(normalized.AsSpan(32 - trimmed.Length));

        return InMemorySshSigner.FromEd25519(normalized);
    }

    private static InMemorySshSigner BuildRsa(
        byte[] publicBlob, scoped ref SshDataReader priv, string where)
    {
        SshDataReader pub = new(new ReadOnlySequence<byte>(publicBlob));
        pub.ReadUtf8String(64);
        byte[] e = pub.ReadMpint(512).ToArray();
        byte[] n = pub.ReadMpint(4096).ToArray();

        byte[] d = priv.ReadMpint(4096).ToArray();
        byte[] p = priv.ReadMpint(4096).ToArray();
        byte[] q = priv.ReadMpint(4096).ToArray();
        byte[] iqmp = priv.ReadMpint(4096).ToArray();

        try
        {
            BigInteger bigD = new(d, isUnsigned: true, isBigEndian: true);
            BigInteger bigP = new(p, isUnsigned: true, isBigEndian: true);
            BigInteger bigQ = new(q, isUnsigned: true, isBigEndian: true);

            byte[] modulus = Trim(n);

            RSAParameters parameters = new()
            {
                Modulus = modulus,
                Exponent = Trim(e),
                D = Fixed(bigD, modulus.Length),
                P = Trim(p),
                Q = Trim(q),
                DP = Fixed(bigD % (bigP - BigInteger.One), Trim(p).Length),
                DQ = Fixed(bigD % (bigQ - BigInteger.One), Trim(q).Length),
                InverseQ = Fixed(new BigInteger(iqmp, isUnsigned: true, isBigEndian: true), Trim(p).Length),
            };

            var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return InMemorySshSigner.FromRsa(rsa);
        }
        catch (CryptographicException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $".ppk 里的 RSA 参数不成立{where}：{ex.Message}", ex);
        }
    }

    private static InMemorySshSigner BuildEcdsa(
        byte[] publicBlob, scoped ref SshDataReader priv, string algorithm, string where)
    {
        SshDataReader pub = new(new ReadOnlySequence<byte>(publicBlob));
        pub.ReadUtf8String(64);
        string curveName = pub.ReadUtf8String(64);
        byte[] point = pub.ReadStringAsArray(512);

        (ECCurve curve, int coordinate) = curveName switch
        {
            "nistp256" => (ECCurve.NamedCurves.nistP256, 32),
            "nistp384" => (ECCurve.NamedCurves.nistP384, 48),
            "nistp521" => (ECCurve.NamedCurves.nistP521, 66),
            _ => throw new SshPrivateKeyException(SshFailureReason.Unsupported, $".ppk 里是不支持的曲线 {curveName}{where}。"),
        };

        if (point.Length != 1 + (coordinate * 2) || point[0] != 0x04)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $".ppk 里的 {algorithm} 公开点格式不对{where}。");
        }

        byte[] d = priv.ReadMpint(512).ToArray();

        try
        {
            ECParameters parameters = new()
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = point.AsSpan(1, coordinate).ToArray(),
                    Y = point.AsSpan(1 + coordinate, coordinate).ToArray(),
                },
                D = Fixed(new BigInteger(d, isUnsigned: true, isBigEndian: true), coordinate),
            };

            var ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(parameters);
            return InMemorySshSigner.FromEcdsa(ecdsa);
        }
        catch (CryptographicException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $".ppk 里的 ECDSA 参数不成立{where}：{ex.Message}", ex);
        }
    }

    private static byte[] Trim(byte[] value)
    {
        int index = 0;
        while (index < value.Length - 1 && value[index] == 0)
        {
            index++;
        }
        return index == 0 ? value : value.AsSpan(index).ToArray();
    }

    private static byte[] Fixed(BigInteger value, int length)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

        if (bytes.Length == length)
        {
            return bytes;
        }

        if (bytes.Length > length)
        {
            return bytes.AsSpan(bytes.Length - length).ToArray();
        }

        byte[] padded = new byte[length];
        bytes.CopyTo(padded.AsSpan(length - bytes.Length));
        return padded;
    }
}
