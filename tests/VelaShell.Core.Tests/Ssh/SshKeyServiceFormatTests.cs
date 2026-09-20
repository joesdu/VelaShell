using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Tmds.Ssh;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 生成的私钥必须是 Tmds.Ssh 能加载的 OpenSSH 格式。曾用 <c>ExportRSAPrivateKeyPem()</c> 写 PKCS#1
/// (-----BEGIN RSA PRIVATE KEY-----),Tmds.Ssh 0.23 判 "Unsupported format" 而跳过 publickey,
/// 用户用本应用生成的密钥无法登录(诊断第 4 步:no methods failed、skipped publickey)。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public class SshKeyServiceFormatTests
{
    [TestMethod]
    [DataRow(SshKeyAlgorithm.Ed25519, 0, DisplayName = "Ed25519(默认)")]
    [DataRow(SshKeyAlgorithm.Ecdsa, 256, DisplayName = "ECDSA nistp256")]
    [DataRow(SshKeyAlgorithm.Ecdsa, 384, DisplayName = "ECDSA nistp384")]
    [DataRow(SshKeyAlgorithm.Ecdsa, 521, DisplayName = "ECDSA nistp521")]
    [DataRow(SshKeyAlgorithm.Rsa, 2048, DisplayName = "RSA 2048")]
    public async Task GeneratedKey_IsOpenSshFormat_AndLoadableByTmdsSsh(SshKeyAlgorithm algorithm, int bits)
    {
        string dir = NewTempDir();
        try
        {
            var svc = new SshKeyService(dir);
            SshKeyInfo info = await svc.GenerateKeyAsync("id_test", algorithm, bits);

            string pem = await File.ReadAllTextAsync(info.PrivateKeyPath);
            Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", pem,
                "私钥必须是 OpenSSH 格式,否则 Tmds.Ssh 无法加载");

            // Tmds.Ssh 真正加载一遍:能取出密钥即证明格式被接受(不抛 Unsupported format)。
            var cred = new PrivateKeyCredential(info.PrivateKeyPath, null, "test");
            MethodInfo load = typeof(PrivateKeyCredential).GetMethod("LoadKeyAsync",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            object valueTask = load.Invoke(cred, [CancellationToken.None])!;
            var t = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
            await t; // 不抛即通过
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// Ed25519 私钥里的公钥字段必须与 <c>.pub</c> 一致,私钥字段必须是 <b>seed ‖ pub</b> 的 64 字节。
    /// </summary>
    /// <remarks>
    /// 这条单独立着,是因为「只写 32 字节种子」是一个 <b>Tmds.Ssh 加载得动、结构也完全合法</b>的错 ——
    /// 上面那条一路绿灯,直到真去连一台服务器才会以签名验证失败告终。这里直接把封装拆开对字节。
    /// </remarks>
    [TestMethod]
    public async Task GeneratedEd25519Key_PrivateBlobCarriesSeedAndMatchingPublicKey()
    {
        string dir = NewTempDir();
        try
        {
            var svc = new SshKeyService(dir);
            SshKeyInfo info = await svc.GenerateKeyAsync("id_ed25519");

            Assert.AreEqual("ED25519", info.Type);
            Assert.StartsWith("ssh-ed25519 ", info.PublicKeyLine);
            byte[] pubBlob = Convert.FromBase64String(info.PublicKeyLine!.Split(' ')[1]);

            byte[] inner = UnwrapOpenSshPrivateKey(await File.ReadAllTextAsync(info.PrivateKeyPath));
            int offset = 0;
            CollectionAssert.AreEqual(pubBlob, ReadChunk(inner, ref offset),
                "私钥外层的公钥 blob 应与 .pub 逐字节一致");
            byte[] section = ReadChunk(inner, ref offset);

            int inner2 = 8; // 两个 checkint
            Assert.AreEqual("ssh-ed25519", Encoding.ASCII.GetString(ReadChunk(section, ref inner2)));
            byte[] publicKey = ReadChunk(section, ref inner2);
            byte[] secret = ReadChunk(section, ref inner2);
            Assert.AreEqual(32, publicKey.Length);
            Assert.AreEqual(64, secret.Length, "OpenSSH 的 ed25519 私钥字段是 seed ‖ pub,不是那 32 字节种子");
            CollectionAssert.AreEqual(publicKey, secret[32..], "私钥字段后 32 字节必须就是公钥");
            CollectionAssert.AreEqual(publicKey, pubBlob[^32..], "公钥 blob 尾部的定长公钥应与私钥段一致");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "velashell-keytest-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>剥掉 PEM 外壳与 15 字节魔数,返回 openssh-key-v1 容器里「密钥数」之后的部分。</summary>
    private static byte[] UnwrapOpenSshPrivateKey(string pem)
    {
        string base64 = string.Concat(pem.Split('\n')
                                         .Select(l => l.Trim())
                                         .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        byte[] outer = Convert.FromBase64String(base64);

        int offset = "openssh-key-v1\0".Length;
        ReadChunk(outer, ref offset); // ciphername
        ReadChunk(outer, ref offset); // kdfname
        ReadChunk(outer, ref offset); // kdfoptions
        offset += 4;                  // 密钥数量
        return outer[offset..];
    }

    private static byte[] ReadChunk(byte[] blob, ref int offset)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset, 4));
        offset += 4;
        byte[] chunk = blob.AsSpan(offset, length).ToArray();
        offset += length;
        return chunk;
    }
}
