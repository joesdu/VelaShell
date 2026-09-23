// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH PROTOCOL.key（openssh-key-v1 的加密私钥区）
//           OpenBSD bcrypt_pbkdf(3)
//
// **样本是真 ssh-keygen 生成的**，不是我们自己按格式拼的。
// 这一条在这里是硬要求：加密侧如果也由我们自己写，它和解密侧会一起错，
// 而那种错不会报错，只会在别人的钥上静默失败。样本怎么来的见 Fixtures/README.md。

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class EncryptedOpenSshKeyTests
{
    /// <summary>生成样本时用的口令，见 Fixtures/README.md。</summary>
    private const string Passphrase = "correct horse battery staple";

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    /// <summary>
    /// 从 <c>.pub</c> 里取出公钥 blob —— 它是 <c>ssh-keygen</c> 写的，
    /// 也就是这组用例里唯一的「标准答案」。
    /// </summary>
    private static byte[] ReadPublicBlob(string name)
    {
        string line = File.ReadAllText(FixturePath(name + ".pub"));
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsGreaterThanOrEqualTo(2, parts.Length, $"{name}.pub 的格式不对。");
        return Convert.FromBase64String(parts[1]);
    }

    /// <summary>
    /// 解出来的私钥，它的公钥必须与 <c>.pub</c> **逐字节**相同。
    /// </summary>
    /// <remarks>
    /// 这是整条链路唯一有意义的判据：口令 → bcrypt_pbkdf → 密钥/IV → 解密 →
    /// 私钥区 → 公钥。中间任何一步错一个比特，这里都对不上；
    /// 而中间任何一步「错得和我们的预期一致」，这里仍然对不上，
    /// 因为标准答案是 OpenSSH 写的。
    /// </remarks>
    [TestMethod]
    [DataRow("ed25519-aes256ctr", DisplayName = "ed25519 · aes256-ctr（ssh-keygen 默认）")]
    [DataRow("ed25519-aes256cbc", DisplayName = "ed25519 · aes256-cbc")]
    [DataRow("ed25519-aes128ctr", DisplayName = "ed25519 · aes128-ctr")]
    [DataRow("ed25519-aes256gcm", DisplayName = "ed25519 · aes256-gcm@openssh.com")]
    [DataRow("ed25519-chachapoly", DisplayName = "ed25519 · chacha20-poly1305@openssh.com")]
    [DataRow("ed25519-rounds64", DisplayName = "ed25519 · aes256-ctr · 64 轮")]
    [DataRow("rsa-aes256ctr", DisplayName = "rsa-2048 · aes256-ctr")]
    [DataRow("ecdsa-aes256ctr", DisplayName = "ecdsa-p256 · aes256-ctr")]
    public async Task 口令正确时_解出的公钥与ssh_keygen写的逐字节相同(string name)
    {
        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(FixturePath(name), Passphrase, TestContext.CancellationToken);

        Assert.AreSequenceEqual(
            ReadPublicBlob(name), signer.PublicKey.Blob.ToArray(), $"{name}：解出来的公钥与 ssh-keygen 写的 .pub 不一致。");
    }

    /// <summary>不加密的那一路不能被这次改动带坏。</summary>
    [TestMethod]
    public async Task 未加密的私钥仍然照常读()
    {
        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(
            FixturePath("ed25519-plain"), passphrase: null, TestContext.CancellationToken);

        Assert.AreSequenceEqual(ReadPublicBlob("ed25519-plain"), signer.PublicKey.Blob.ToArray());
    }

    /// <summary>
    /// 口令不对要说「口令不对」，并且 <see cref="SshPrivateKeyException.NeedsPassphrase" />
    /// 为 true —— 界面靠它决定是不是再弹一次输入框。
    /// </summary>
    /// <remarks>
    /// CTR / CBC 没有认证标签，口令错了照样"解"得出一堆随机字节，
    /// 要到校验字那一步才露馅；GCM / ChaCha20-Poly1305 在验标签时就判死。
    /// 两条路都必须给出同一种结论，否则界面只能按算法分别处理。
    /// </remarks>
    [TestMethod]
    [DataRow("ed25519-aes256ctr", DisplayName = "无标签：靠校验字发现")]
    [DataRow("ed25519-aes256cbc", DisplayName = "无标签（CBC）：靠校验字发现")]
    [DataRow("ed25519-aes256gcm", DisplayName = "有标签：验标签时就发现")]
    [DataRow("ed25519-chachapoly", DisplayName = "有标签（ChaCha）：验标签时就发现")]
    public async Task 口令不对时_报口令不对而不是文件损坏(string name)
    {
        SshPrivateKeyException ex = await Assert.ThrowsExactlyAsync<SshPrivateKeyException>(
            async () => await SshPrivateKeyFile.LoadAsync(
                FixturePath(name), "wrong passphrase", TestContext.CancellationToken));

        Assert.IsTrue(ex.NeedsPassphrase, $"{name}：口令错了却没把 NeedsPassphrase 置上。");
        Assert.Contains("口令", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>没给口令时也走同一条结论，而不是先崩在别处。</summary>
    [TestMethod]
    public async Task 加密私钥没给口令时_要求口令()
    {
        SshPrivateKeyException ex = await Assert.ThrowsExactlyAsync<SshPrivateKeyException>(
            async () => await SshPrivateKeyFile.LoadAsync(
                FixturePath("ed25519-aes256ctr"), passphrase: null, TestContext.CancellationToken));

        Assert.IsTrue(ex.NeedsPassphrase);
    }

    /// <summary>格式识别不该因为这次改动而变。</summary>
    [TestMethod]
    public void 加密的OpenSSH私钥仍然被识别为OpenSsh格式()
    {
        string pem = File.ReadAllText(FixturePath("ed25519-aes256ctr"));
        Assert.AreEqual(SshPrivateKeyFormat.OpenSsh, SshPrivateKeyFile.DetectFormat(pem));
    }

    /// <summary>
    /// 解出来的钥必须真的能签 —— 公钥对得上但私钥标量错了的情况，
    /// 只有让它签一次再验一次才抓得住。
    /// </summary>
    [TestMethod]
    [DataRow("ed25519-aes256ctr")]
    [DataRow("rsa-aes256ctr")]
    [DataRow("ecdsa-aes256ctr")]
    public async Task 解出来的私钥签名能被它自己的公钥验过(string name)
    {
        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(FixturePath(name), Passphrase, TestContext.CancellationToken);

        byte[] data = Encoding.UTF8.GetBytes("velashell-ssh 的签名自检数据");
        string algorithm = signer.SignatureAlgorithms[0];
        byte[] signature = await signer.SignAsync(data, algorithm, TestContext.CancellationToken);

        Assert.IsTrue(
            signer.PublicKey.VerifySignature(signature, data, algorithm),
            $"{name}：解出来的私钥签的名，它自己的公钥验不过。");
    }

    /// <summary>MSTest 注入的测试上下文。</summary>
    public TestContext TestContext { get; set; } = null!;
}

/// <summary>
/// 钉住 Blowfish 的初始表。
/// </summary>
/// <remarks>
/// P 数组与 S 盒是 π 小数部分的十六进制位，本库**现算**而不是手抄
/// （理由见 <c>BcryptPbkdf.BuildInitialState</c>）。这组断言的价值是：
/// 即使一个样本都没有，推导被改坏也会当场红，而不是等到某个用户的钥打不开。
/// </remarks>
[TestClass]
[TestCategory("Keys")]
public sealed class BlowfishTableTests
{
    [TestMethod]
    public void 初始表就是π的十六进制位()
    {
        ReadOnlySpan<uint> tables = BcryptPbkdf.InitialTables;

        Assert.HasCount(18 + (4 * 256), tables, "P 数组 18 个字 + 4 个各 256 项的 S 盒。");

        // π = 3.243F6A88 85A308D3 13198A2E 03707344 …
        Assert.AreEqual(0x243F6A88u, tables[0], "P[0]");
        Assert.AreEqual(0x85A308D3u, tables[1], "P[1]");
        Assert.AreEqual(0x13198A2Eu, tables[2], "P[2]");
        Assert.AreEqual(0x03707344u, tables[3], "P[3]");

        // 第 18 个字起是 S0。
        Assert.AreEqual(0xD1310BA6u, tables[18], "S0[0]");
        Assert.AreEqual(0x98DFB5ACu, tables[19], "S0[1]");

        // 末位 —— 少算或多算一个字都会在这里露出来。
        Assert.AreEqual(0x3AC372E6u, tables[^1], "S3[255]");
    }
}
