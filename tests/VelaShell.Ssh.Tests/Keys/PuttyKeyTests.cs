// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: PuTTY .ppk 格式;RFC 9106（Argon2）
//
// 这里的 .ppk 也是**按格式现拼出来的** —— 拼的过程本身在验证我们对格式的理解，
// 而且能覆盖加密与不加密、v2 与 v3。

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class PuttyKeyTests
{
    private const string MacPhrase = "putty-private-key-file-mac-key";

    /// <summary>按 .ppk 格式拼一个文件。</summary>
    private static string BuildPpk(
        int version,
        string algorithm,
        byte[] publicBlob,
        byte[] privateBlob,
        string comment = "测试密钥",
        string? passphrase = null,
        string argon2Variant = "Argon2id")
    {
        string encryption = passphrase is null ? "none" : "aes256-cbc";
        byte[] storedPrivate = privateBlob;
        byte[] macKey;

        List<string> kdfHeaders = [];

        if (passphrase is null)
        {
            macKey = version == 3
                ? new byte[32]
                : Sha1(Encoding.UTF8.GetBytes(MacPhrase));
        }
        else if (version == 3)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            const int memory = 8192;
            const int passes = 3;
            const int parallelism = 1;

            byte[] material = Argon2(passphrase, salt, memory, passes, parallelism, argon2Variant);
            byte[] key = material[..32];
            byte[] iv = material[32..48];
            macKey = material[48..80];

            storedPrivate = Encrypt(Pad(privateBlob), key, iv);

            kdfHeaders =
            [
                $"Key-Derivation: {argon2Variant}",
                $"Argon2-Memory: {memory}",
                $"Argon2-Passes: {passes}",
                $"Argon2-Parallelism: {parallelism}",
                $"Argon2-Salt: {Convert.ToHexString(salt).ToLowerInvariant()}",
            ];
        }
        else
        {
            byte[] pass = Encoding.UTF8.GetBytes(passphrase);
            byte[] first = Sha1([0, 0, 0, 0, .. pass]);
            byte[] second = Sha1([0, 0, 0, 1, .. pass]);
            byte[] key = [.. first, .. second[..12]];

            storedPrivate = Encrypt(Pad(privateBlob), key, new byte[16]);
            macKey = Sha1([.. Encoding.UTF8.GetBytes(MacPhrase), .. pass]);
        }

        // MAC 算的是**解密之后**的私钥区（含补齐的那几个字节）——
        // 这正是「口令不对」能被当场发现的原因。
        byte[] macPrivate = passphrase is null ? privateBlob : Pad(privateBlob);

        ArrayBufferWriter<byte> macInput = new();
        SshDataWriterBox box = new(macInput);
        box.WriteUtf8String(algorithm);
        box.WriteUtf8String(encryption);
        box.WriteUtf8String(comment);
        box.WriteString(publicBlob);
        box.WriteString(macPrivate);

        byte[] mac = version == 3
            ? HMACSHA256.HashData(macKey, macInput.WrittenSpan.ToArray())
            : HmacSha1(macKey, macInput.WrittenSpan.ToArray());

        StringBuilder text = new();
        text.Append(CultureInfo(), $"PuTTY-User-Key-File-{version}: {algorithm}\n");
        text.Append(CultureInfo(), $"Encryption: {encryption}\n");
        text.Append(CultureInfo(), $"Comment: {comment}\n");

        foreach (string header in kdfHeaders)
        {
            text.Append(header).Append('\n');
        }

        AppendBase64(text, "Public-Lines", publicBlob);
        AppendBase64(text, "Private-Lines", storedPrivate);
        text.Append(CultureInfo(), $"Private-MAC: {Convert.ToHexString(mac).ToLowerInvariant()}\n");

        return text.ToString();
    }

    private static System.Globalization.CultureInfo CultureInfo() =>
        System.Globalization.CultureInfo.InvariantCulture;

    private static void AppendBase64(StringBuilder text, string name, byte[] blob)
    {
        string base64 = Convert.ToBase64String(blob);
        List<string> lines = [];

        for (int i = 0; i < base64.Length; i += 64)
        {
            lines.Add(base64[i..Math.Min(i + 64, base64.Length)]);
        }

        if (lines.Count == 0)
        {
            lines.Add("");
        }

        text.Append(CultureInfo(), $"{name}: {lines.Count}\n");
        foreach (string line in lines)
        {
            text.Append(line).Append('\n');
        }
    }

#pragma warning disable CA5350 // .ppk v2 的 KDF 与 MAC 由格式规定就是 SHA-1
    private static byte[] Sha1(byte[] input) => SHA1.HashData(input);

    private static byte[] HmacSha1(byte[] key, byte[] data) => HMACSHA1.HashData(key, data);
#pragma warning restore CA5350

    private static byte[] Argon2(
        string passphrase, byte[] salt, int memory, int passes, int parallelism, string variant = "Argon2id")
    {
        int type = variant switch
        {
            "Argon2i" => Argon2Parameters.Argon2i,
            "Argon2d" => Argon2Parameters.Argon2d,
            _ => Argon2Parameters.Argon2id,
        };
        Argon2Parameters parameters = new Argon2Parameters.Builder(type)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(salt)
            .WithMemoryAsKB(memory)
            .WithIterations(passes)
            .WithParallelism(parallelism)
            .Build();

        Argon2BytesGenerator generator = new();
        generator.Init(parameters);

        byte[] output = new byte[80];
        generator.GenerateBytes(Encoding.UTF8.GetBytes(passphrase), output);
        return output;
    }

    /// <summary>补到 16 的倍数 —— .ppk 的私钥区是按字段自描述的，补什么都行。</summary>
    private static byte[] Pad(byte[] data)
    {
        int padded = (data.Length + 15) / 16 * 16;
        byte[] result = new byte[padded];
        data.CopyTo(result, 0);
        return result;
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private sealed class SshDataWriterBox(ArrayBufferWriter<byte> output)
    {
        public void WriteUtf8String(string value)
        {
            SshDataWriter w = new(output);
            w.WriteUtf8String(value);
        }

        public void WriteString(ReadOnlySpan<byte> value)
        {
            SshDataWriter w = new(output);
            w.WriteString(value);
        }

        public void WriteMpint(ReadOnlySpan<byte> magnitude)
        {
            SshDataWriter w = new(output);
            w.WriteMpint(magnitude);
        }
    }

    // ------------------------------------------------------------ 各类型

    private static (byte[] Public, byte[] Private, InMemorySshSigner Signer) MakeEd25519()
    {
        Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters key =
            new(new Org.BouncyCastle.Security.SecureRandom());

        byte[] publicKey = key.GeneratePublicKey().GetEncoded();

        ArrayBufferWriter<byte> pub = new();
        SshDataWriterBox pubBox = new(pub);
        pubBox.WriteUtf8String(SshAlgorithmNames.SshEd25519);
        pubBox.WriteString(publicKey);

        ArrayBufferWriter<byte> priv = new();
        SshDataWriterBox privBox = new(priv);
        privBox.WriteMpint(key.GetEncoded());

        return (pub.WrittenSpan.ToArray(), priv.WrittenSpan.ToArray(),
            InMemorySshSigner.FromEd25519(key.GetEncoded()));
    }

    // ------------------------------------------------------------ 用例

    [TestMethod]
    public void 认得出ppk()
    {
        Assert.IsTrue(PuttyPrivateKeyFile.IsPuttyKey("PuTTY-User-Key-File-3: ssh-ed25519\n…"));
        Assert.IsFalse(PuttyPrivateKeyFile.IsPuttyKey("-----BEGIN OPENSSH PRIVATE KEY-----"));

        Assert.AreEqual(
            SshPrivateKeyFormat.Putty,
            SshPrivateKeyFile.DetectFormat("PuTTY-User-Key-File-2: ssh-rsa\n…"));
    }

    [TestMethod]
    public async Task 读v3的未加密Ed25519()
    {
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv);
            ISshSigner signer = SshPrivateKeyFile.Parse(ppk);

            Assert.AreSequenceEqual(expected.PublicKey.Blob.ToArray(), signer.PublicKey.Blob.ToArray());

            byte[] data = Encoding.UTF8.GetBytes("签一下");
            byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.SshEd25519);
            Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519));
        }
    }

    [TestMethod]
    public async Task 读v2的未加密Ed25519()
    {
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(2, SshAlgorithmNames.SshEd25519, pub, priv);
            ISshSigner signer = SshPrivateKeyFile.Parse(ppk);

            byte[] data = Encoding.UTF8.GetBytes("x");
            byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.SshEd25519);
            Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519));
        }
    }

    [TestMethod]
    public async Task 读v3的加密Ed25519()
    {
        // ⚠️ 这一条与 OpenSSH 的加密私钥形成对比：
        //    .ppk v3 用 Argon2id，BouncyCastle 直接提供 —— 我们只是装配。
        //    OpenSSH 用 bcrypt_pbkdf，那要写 Blowfish 的密钥编排，做不了。
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv, passphrase: "正确的口令");
            ISshSigner signer = SshPrivateKeyFile.Parse(ppk, "正确的口令");

            Assert.AreSequenceEqual(expected.PublicKey.Blob.ToArray(), signer.PublicKey.Blob.ToArray());

            byte[] data = Encoding.UTF8.GetBytes("加密的也要能签");
            byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.SshEd25519);
            Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519));
        }
    }

    [TestMethod]
    public async Task 读v2的加密Ed25519()
    {
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(2, SshAlgorithmNames.SshEd25519, pub, priv, passphrase: "口令");
            ISshSigner signer = SshPrivateKeyFile.Parse(ppk, "口令");

            byte[] data = Encoding.UTF8.GetBytes("x");
            byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.SshEd25519);
            Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519));
        }
    }

    [TestMethod]
    public async Task 读RSA()
    {
        using var rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);

        ArrayBufferWriter<byte> pub = new();
        SshDataWriterBox pubBox = new(pub);
        pubBox.WriteUtf8String(SshAlgorithmNames.SshRsa);
        pubBox.WriteMpint(p.Exponent!);
        pubBox.WriteMpint(p.Modulus!);

        ArrayBufferWriter<byte> priv = new();
        SshDataWriterBox privBox = new(priv);
        privBox.WriteMpint(p.D!);
        privBox.WriteMpint(p.P!);
        privBox.WriteMpint(p.Q!);
        privBox.WriteMpint(p.InverseQ!);

        string ppk = BuildPpk(
            3, SshAlgorithmNames.SshRsa, pub.WrittenSpan.ToArray(), priv.WrittenSpan.ToArray());

        ISshSigner signer = SshPrivateKeyFile.Parse(ppk);

        byte[] data = Encoding.UTF8.GetBytes("x");
        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.RsaSha256);
        Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.RsaSha256));
    }

    [TestMethod]
    public async Task 读ECDSA()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = ecdsa.ExportParameters(includePrivateParameters: true);

        byte[] point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point.AsSpan(1 + 32 - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point.AsSpan(1 + 64 - p.Q.Y!.Length));

        ArrayBufferWriter<byte> pub = new();
        SshDataWriterBox pubBox = new(pub);
        pubBox.WriteUtf8String(SshAlgorithmNames.EcdsaSha2Nistp256);
        pubBox.WriteUtf8String("nistp256");
        pubBox.WriteString(point);

        ArrayBufferWriter<byte> priv = new();
        SshDataWriterBox privBox = new(priv);
        privBox.WriteMpint(p.D!);

        string ppk = BuildPpk(
            3, SshAlgorithmNames.EcdsaSha2Nistp256,
            pub.WrittenSpan.ToArray(), priv.WrittenSpan.ToArray());

        ISshSigner signer = SshPrivateKeyFile.Parse(ppk);

        byte[] data = Encoding.UTF8.GetBytes("x");
        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.EcdsaSha2Nistp256);
        Assert.IsTrue(
            signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.EcdsaSha2Nistp256));
    }

    // ------------------------------------------------------------ 失败路径

    [TestMethod]
    public void 加密的ppk没给口令时说清楚()
    {
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv, passphrase: "口令");

            SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
                () => SshPrivateKeyFile.Parse(ppk));

            Assert.IsTrue(error.NeedsPassphrase);
            Assert.Contains("需要口令", error.Message);
        }
    }

    [TestMethod]
    [DataRow("Argon2i")]
    [DataRow("Argon2d")]
    public void Argon2i与Argon2d的ppk也能读(string variant)
    {
        // 格式文档允许三种变体。一律按 Argon2id 算的话，这两种永远过不了 MAC，被报成「口令不对」。
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv, passphrase: "口令", argon2Variant: variant);
            ISshSigner signer = SshPrivateKeyFile.Parse(ppk, "口令");
            Assert.AreSequenceEqual(expected.PublicKey.Blob.ToArray(), signer.PublicKey.Blob.ToArray());
        }
    }

    [TestMethod]
    [DataRow("Argon2-Memory: 8192", "Argon2-Memory: 4194304", DisplayName = "内存 4 GiB")]
    [DataRow("Argon2-Passes: 3", "Argon2-Passes: 2000000000", DisplayName = "二十亿遍")]
    [DataRow("Argon2-Parallelism: 1", "Argon2-Parallelism: 0", DisplayName = "并行度 0")]
    public void Argon2参数超限时当场拒绝而不是先算(string original, string tampered)
    {
        // 这些参数在验 MAC 之前就要用上（MAC 密钥就是 Argon2 的输出）。不设上限的话，
        // 一个被改过的文件要么先让我们分配 4 GiB，要么让「读一把私钥」永远算不完。
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv, passphrase: "口令");
            Assert.Contains(original, ppk, "前提：改的是真实存在的那一行");

            SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
                () => SshPrivateKeyFile.Parse(ppk.Replace(original, tampered, StringComparison.Ordinal), "口令"));
            Assert.Contains("Argon2 参数不合理", error.Message);
        }
    }

    [TestMethod]
    public void 口令不对时说得出是口令不对()
    {
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv, passphrase: "正确的");

            SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
                () => SshPrivateKeyFile.Parse(ppk, "错的"));

            // 先验 MAC 再用私钥 —— 不然报出来的会是「参数不成立」之类，
            // 而真正的原因是口令打错了。
            Assert.IsTrue(error.NeedsPassphrase);
            Assert.Contains("口令多半不对", error.Message);
        }
    }

    [TestMethod]
    public void 文件被改过时MAC能发现()
    {
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string ppk = BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv, comment: "原始注释");

            // 只改注释 —— 公私钥都没动，解析照样能过，但 MAC 会对不上。
            string tampered = ppk.Replace("Comment: 原始注释", "Comment: 被改过", StringComparison.Ordinal);

            SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
                () => SshPrivateKeyFile.Parse(tampered));

            Assert.IsFalse(error.NeedsPassphrase, "这不是口令问题");
            Assert.Contains("MAC 对不上", error.Message);
        }
    }

    [TestMethod]
    public void 不支持的版本被拒()
    {
        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => PuttyPrivateKeyFile.Parse("PuTTY-User-Key-File-1: ssh-rsa\nEncryption: none\n"));

        Assert.Contains("不支持的 .ppk 版本", error.Message);
    }

    [TestMethod]
    public async Task 从文件读()
    {
        (byte[] pub, byte[] priv, InMemorySshSigner expected) = MakeEd25519();
        using (expected)
        {
            string path = Path.Combine(Path.GetTempPath(), $"velashell-{Guid.NewGuid():N}.ppk");
            try
            {
                await File.WriteAllTextAsync(path, BuildPpk(3, SshAlgorithmNames.SshEd25519, pub, priv));

                ISshSigner signer = await SshPrivateKeyFile.LoadAsync(path);
                Assert.AreSequenceEqual(
                    expected.PublicKey.Blob.ToArray(), signer.PublicKey.Blob.ToArray());
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
