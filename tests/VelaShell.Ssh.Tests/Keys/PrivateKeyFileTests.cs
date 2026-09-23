// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH PROTOCOL.key;RFC 5958/5915/8017
//
// 这里的 OpenSSH 格式私钥是**按格式现拼出来的**，不是抄来的样本 ——
// 拼的过程本身就在验证我们对格式的理解，而且能覆盖所有密钥类型。

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class PrivateKeyFileTests
{
    /// <summary>按 openssh-key-v1 拼一个**未加密**的私钥文件。</summary>
    private static string BuildOpenSshKey(Action<SshDataWriterBox> writePrivateFields, byte[] publicBlob)
    {
        ArrayBufferWriter<byte> inner = new();
        SshDataWriterBox innerBox = new(inner);
        innerBox.WriteUInt32(0x1234_5678);   // checkint1
        innerBox.WriteUInt32(0x1234_5678);   // checkint2 —— 未加密时必须与前者相同
        writePrivateFields(innerBox);
        innerBox.WriteUtf8String("测试密钥");

        // 填充到 8 的倍数，内容是 1,2,3,…
        byte pad = 1;
        while (inner.WrittenCount % 8 != 0)
        {
            innerBox.WriteByte(pad++);
        }

        ArrayBufferWriter<byte> outer = new();
        outer.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));

        SshDataWriterBox outerBox = new(outer);
        outerBox.WriteUtf8String("none");    // ciphername
        outerBox.WriteUtf8String("none");    // kdfname
        outerBox.WriteString([]);            // kdfoptions
        outerBox.WriteUInt32(1);             // 密钥数
        outerBox.WriteString(publicBlob);
        outerBox.WriteString(inner.WrittenSpan);

        string base64 = Convert.ToBase64String(outer.WrittenSpan);
        StringBuilder pem = new();
        pem.AppendLine("-----BEGIN OPENSSH PRIVATE KEY-----");
        for (int i = 0; i < base64.Length; i += 70)
        {
            pem.AppendLine(base64[i..Math.Min(i + 70, base64.Length)]);
        }
        pem.AppendLine("-----END OPENSSH PRIVATE KEY-----");
        return pem.ToString();
    }

    /// <summary>把 ref struct 的写入器包一层，好在 lambda 里用。</summary>
    private sealed class SshDataWriterBox(ArrayBufferWriter<byte> output)
    {
        public void WriteByte(byte value)
        {
            SshDataWriter w = new(output);
            w.WriteByte(value);
        }

        public void WriteUInt32(uint value)
        {
            SshDataWriter w = new(output);
            w.WriteUInt32(value);
        }

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

    // ------------------------------------------------------------ 格式识别

    [TestMethod]
    public void 认得出各种PEM头()
    {
        Assert.AreEqual(
            SshPrivateKeyFormat.OpenSsh,
            SshPrivateKeyFile.DetectFormat("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END..."));
        Assert.AreEqual(
            SshPrivateKeyFormat.Pkcs8, SshPrivateKeyFile.DetectFormat("-----BEGIN PRIVATE KEY-----"));
        Assert.AreEqual(
            SshPrivateKeyFormat.Pkcs8Encrypted,
            SshPrivateKeyFile.DetectFormat("-----BEGIN ENCRYPTED PRIVATE KEY-----"));
        Assert.AreEqual(
            SshPrivateKeyFormat.Pkcs1Rsa, SshPrivateKeyFile.DetectFormat("-----BEGIN RSA PRIVATE KEY-----"));
        Assert.AreEqual(
            SshPrivateKeyFormat.Sec1Ec, SshPrivateKeyFile.DetectFormat("-----BEGIN EC PRIVATE KEY-----"));
        Assert.AreEqual(SshPrivateKeyFormat.Unknown, SshPrivateKeyFile.DetectFormat("这不是密钥"));
    }

    [TestMethod]
    public void PPK被交给PuTTY那条路()
    {
        // .ppk 现在是直接支持的（见 PuttyKeyTests）。
        // 这里只钉住「认得出来并且走对分支」：残缺的内容应当由
        // .ppk 解析器来报错，而不是落到「认不出格式」那个兜底分支。
        Assert.AreEqual(
            SshPrivateKeyFormat.Putty,
            SshPrivateKeyFile.DetectFormat("PuTTY-User-Key-File-3: ssh-ed25519\n…"));

        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse("PuTTY-User-Key-File-9: ssh-ed25519\nEncryption: none\n"));

        Assert.Contains("不支持的 .ppk 版本", error.Message);
    }

    // ------------------------------------------------------------ OpenSSH 格式

    [TestMethod]
    public async Task 读出Ed25519私钥并且能签能验()
    {
        Ed25519PrivateKeyParameters key = new(new SecureRandom());
        byte[] publicKey = key.GeneratePublicKey().GetEncoded();
        byte[] secret = [.. key.GetEncoded(), .. publicKey];   // 种子 ‖ 公钥

        ArrayBufferWriter<byte> blobBuffer = new();
        SshDataWriterBox blobBox = new(blobBuffer);
        blobBox.WriteUtf8String(SshAlgorithmNames.SshEd25519);
        blobBox.WriteString(publicKey);
        byte[] publicBlob = blobBuffer.WrittenSpan.ToArray();

        string pem = BuildOpenSshKey(
            w =>
            {
                w.WriteUtf8String(SshAlgorithmNames.SshEd25519);
                w.WriteString(publicKey);
                w.WriteString(secret);
            },
            publicBlob);

        ISshSigner signer = SshPrivateKeyFile.Parse(pem);

        Assert.AreEqual(SshAlgorithmNames.SshEd25519, signer.PublicKey.KeyType);
        Assert.AreSequenceEqual(publicBlob, signer.PublicKey.Blob.ToArray(), "从私钥导出的公钥要与文件里带的那份一致");

        // 真的签一次，再用解析出来的公钥验 —— 只比公钥是不够的，
        // 私钥的种子取错了（比如把 64 字节整个当种子）照样能得出正确的公钥 blob。
        byte[] data = Encoding.UTF8.GetBytes("要签的内容");
        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.SshEd25519);

        Assert.IsTrue(
            signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519),
            "签出来的东西必须能被对应的公钥验过");
    }

    [TestMethod]
    public async Task 读出RSA私钥并且能签能验()
    {
        using RSA rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);

        ArrayBufferWriter<byte> blobBuffer = new();
        SshDataWriterBox blobBox = new(blobBuffer);
        blobBox.WriteUtf8String(SshAlgorithmNames.SshRsa);
        blobBox.WriteMpint(p.Exponent!);
        blobBox.WriteMpint(p.Modulus!);
        byte[] publicBlob = blobBuffer.WrittenSpan.ToArray();

        string pem = BuildOpenSshKey(
            w =>
            {
                // OpenSSH 的字段顺序：n, e, d, iqmp, p, q —— 与 PKCS#1 不同。
                w.WriteUtf8String(SshAlgorithmNames.SshRsa);
                w.WriteMpint(p.Modulus!);
                w.WriteMpint(p.Exponent!);
                w.WriteMpint(p.D!);
                w.WriteMpint(p.InverseQ!);
                w.WriteMpint(p.P!);
                w.WriteMpint(p.Q!);
            },
            publicBlob);

        ISshSigner signer = SshPrivateKeyFile.Parse(pem);

        Assert.AreEqual(SshAlgorithmNames.SshRsa, signer.PublicKey.KeyType);

        // DP 与 DQ 是我们自己按 CRT 定义算出来的 —— 算错了这里就签不出正确的签名。
        byte[] data = Encoding.UTF8.GetBytes("要签的内容");
        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.RsaSha256);

        Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.RsaSha256));
    }

    [TestMethod]
    public async Task 读出ECDSA私钥并且能签能验()
    {
        foreach ((string algorithm, string curveName, int coordinate, ECCurve curve) in
            new[]
            {
                (SshAlgorithmNames.EcdsaSha2Nistp256, "nistp256", 32, ECCurve.NamedCurves.nistP256),
                (SshAlgorithmNames.EcdsaSha2Nistp384, "nistp384", 48, ECCurve.NamedCurves.nistP384),

                // nistp521 的坐标是 66 字节（521 位向上取整），不是 64 —— 这是常见的错处。
                (SshAlgorithmNames.EcdsaSha2Nistp521, "nistp521", 66, ECCurve.NamedCurves.nistP521),
            })
        {
            using ECDsa ecdsa = ECDsa.Create(curve);
            ECParameters p = ecdsa.ExportParameters(includePrivateParameters: true);

            byte[] point = new byte[1 + (coordinate * 2)];
            point[0] = 0x04;
            p.Q.X!.CopyTo(point.AsSpan(1 + coordinate - p.Q.X!.Length));
            p.Q.Y!.CopyTo(point.AsSpan(1 + (coordinate * 2) - p.Q.Y!.Length));

            ArrayBufferWriter<byte> blobBuffer = new();
            SshDataWriterBox blobBox = new(blobBuffer);
            blobBox.WriteUtf8String(algorithm);
            blobBox.WriteUtf8String(curveName);
            blobBox.WriteString(point);
            byte[] publicBlob = blobBuffer.WrittenSpan.ToArray();

            string pem = BuildOpenSshKey(
                w =>
                {
                    w.WriteUtf8String(algorithm);
                    w.WriteUtf8String(curveName);
                    w.WriteString(point);
                    w.WriteMpint(p.D!);
                },
                publicBlob);

            ISshSigner signer = SshPrivateKeyFile.Parse(pem);

            Assert.AreEqual(algorithm, signer.PublicKey.KeyType, curveName);

            byte[] data = Encoding.UTF8.GetBytes("要签的内容");
            byte[] signature = await signer.SignAsync(data, algorithm);
            Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, algorithm), curveName);
        }
    }

    [TestMethod]
    public void 校验字不匹配时报出来而不是读出垃圾()
    {
        Ed25519PrivateKeyParameters key = new(new SecureRandom());
        byte[] publicKey = key.GeneratePublicKey().GetEncoded();

        ArrayBufferWriter<byte> inner = new();
        SshDataWriterBox innerBox = new(inner);
        innerBox.WriteUInt32(0x1111_1111);
        innerBox.WriteUInt32(0x2222_2222);   // **故意不同**
        innerBox.WriteUtf8String(SshAlgorithmNames.SshEd25519);
        innerBox.WriteString(publicKey);
        innerBox.WriteString([.. key.GetEncoded(), .. publicKey]);
        innerBox.WriteUtf8String("");

        ArrayBufferWriter<byte> outer = new();
        outer.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        SshDataWriterBox outerBox = new(outer);
        outerBox.WriteUtf8String("none");
        outerBox.WriteUtf8String("none");
        outerBox.WriteString([]);
        outerBox.WriteUInt32(1);
        outerBox.WriteString([]);
        outerBox.WriteString(inner.WrittenSpan);

        string pem =
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" +
            Convert.ToBase64String(outer.WrittenSpan) + "\n" +
            "-----END OPENSSH PRIVATE KEY-----\n";

        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse(pem));

        Assert.Contains("校验字不匹配", error.Message);
    }

    /// <summary>
    /// 不认识的加密算法要说清「本库支持哪些」与「怎么换」，而不是甩一句读不了。
    /// </summary>
    /// <remarks>
    /// 能读加密私钥之后，这里剩下的唯一缺口就是 3DES 那一类过时算法 ——
    /// 为了读一种没人用的格式在安全库里带上 3DES 不划算，所以如实报错。
    /// 真实加密私钥的端到端用例在 <c>EncryptedOpenSshKeyTests</c>。
    /// </remarks>
    [TestMethod]
    public void 不支持的加密算法给出可照做的下一步()
    {
        ArrayBufferWriter<byte> outer = new();
        outer.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        SshDataWriterBox box = new(outer);
        box.WriteUtf8String("3des-cbc");
        box.WriteUtf8String("bcrypt");
        box.WriteString(new byte[24]);
        box.WriteUInt32(1);
        box.WriteString([]);
        box.WriteString(new byte[64]);

        string pem =
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" +
            Convert.ToBase64String(outer.WrittenSpan) + "\n" +
            "-----END OPENSSH PRIVATE KEY-----\n";

        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse(pem, "口令"));

        Assert.Contains("3des-cbc", error.Message);
        Assert.Contains("aes256-ctr", error.Message);
        Assert.Contains("ssh-keygen -p -Z", error.Message);
    }

    // ------------------------------------------------------------ BCL 能读的格式

    [TestMethod]
    public async Task 读出PKCS8的RSA私钥()
    {
        using RSA rsa = RSA.Create(2048);
        string pem = rsa.ExportPkcs8PrivateKeyPem();

        ISshSigner signer = SshPrivateKeyFile.Parse(pem);

        byte[] data = Encoding.UTF8.GetBytes("x");
        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.RsaSha512);
        Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.RsaSha512));
    }

    [TestMethod]
    public async Task 读出PKCS8的ECDSA私钥()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = ecdsa.ExportPkcs8PrivateKeyPem();

        ISshSigner signer = SshPrivateKeyFile.Parse(pem);

        byte[] data = Encoding.UTF8.GetBytes("x");
        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.EcdsaSha2Nistp256);
        Assert.IsTrue(
            signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.EcdsaSha2Nistp256));
    }

    [TestMethod]
    public async Task 读出带口令的PKCS8私钥()
    {
        using RSA rsa = RSA.Create(2048);
        PbeParameters pbe = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000);
        string pem = rsa.ExportEncryptedPkcs8PrivateKeyPem("正确的口令", pbe);

        ISshSigner signer = SshPrivateKeyFile.Parse(pem, "正确的口令");

        byte[] data = Encoding.UTF8.GetBytes("x");
        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.RsaSha256);
        Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.RsaSha256));
    }

    [TestMethod]
    public void 带口令的私钥没给口令时说清楚()
    {
        using RSA rsa = RSA.Create(2048);
        PbeParameters pbe = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000);
        string pem = rsa.ExportEncryptedPkcs8PrivateKeyPem("口令", pbe);

        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse(pem));

        // 「需要口令」与「口令不对」在界面上是两件事：前者该弹输入框，
        // 后者该说「口令不对，再试一次」。
        Assert.IsTrue(error.NeedsPassphrase);
        Assert.Contains("需要口令", error.Message);
    }

    [TestMethod]
    public void 口令不对时说得出是口令不对()
    {
        using RSA rsa = RSA.Create(2048);
        PbeParameters pbe = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000);
        string pem = rsa.ExportEncryptedPkcs8PrivateKeyPem("正确的", pbe);

        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse(pem, "错的"));

        Assert.IsTrue(error.NeedsPassphrase);
        Assert.Contains("口令多半不对", error.Message);
    }

    [TestMethod]
    public async Task 从文件读()
    {
        using RSA rsa = RSA.Create(2048);
        string path = Path.Combine(Path.GetTempPath(), $"velashell-key-{Guid.NewGuid():N}.pem");

        try
        {
            await File.WriteAllTextAsync(path, rsa.ExportPkcs8PrivateKeyPem());
            ISshSigner signer = await SshPrivateKeyFile.LoadAsync(path);
            Assert.AreEqual(SshAlgorithmNames.SshRsa, signer.PublicKey.KeyType);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
