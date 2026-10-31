// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/00-overview.md §6

using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Crypto;

/// <summary>算法目录与实际实现是同一个口径：目录里说实现了的，工厂表造得出、连接前的校验放得过；没实现的那份一个都不在实现里。</summary>
[TestClass]
[TestCategory("Crypto")]
public sealed class AlgorithmCatalogTests
{
    [TestMethod]
    public void 目录里实现了的都造得出()
    {
        Assert.IsTrue(SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.KeyExchange).All(SshKeyExchangeFactory.IsSupported));
        Assert.IsTrue(SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Encryption).All(SshSessionKeys.IsSupportedEncryption));
        Assert.IsTrue(SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Mac).All(SshSessionKeys.IsSupportedMac));

        // 整份目录写成一个清单，连接前的校验要放得过。
        SshAlgorithmSet everything = new()
        {
            KeyExchange = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.KeyExchange),
            HostKey = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.HostKey),
            EncryptionClientToServer = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Encryption),
            EncryptionServerToClient = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Encryption),
            MacClientToServer = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Mac),
            MacServerToClient = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Mac),
            CompressionClientToServer = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Compression),
            CompressionServerToClient = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Compression),
        };
        everything.Validate();
    }

    [TestMethod]
    public void 默认清单与老算法都在目录里_默认的排在前面()
    {
        SshAlgorithmSet legacy = SshAlgorithmSet.Default.WithLegacyInterop();
        IReadOnlyList<string> kex = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.KeyExchange);

        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.KeyExchange], [.. kex.Take(SshAlgorithmSet.Default.KeyExchange.Count)]);
        Assert.IsTrue(legacy.HostKey.All(a => SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.HostKey, a)));
        Assert.IsTrue(legacy.MacClientToServer.All(a => SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.Mac, a)));
        Assert.IsTrue(SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.Compression, SshAlgorithmNames.ZlibOpenSsh));
    }

    /// <summary>FIPS 认可的清单：全都实现了、校验放得过，而且一个不认可的都不在里面。</summary>
    [TestMethod]
    public void FIPS清单只含认可的算法()
    {
        SshAlgorithmSet fips = SshAlgorithmSet.FipsApprovedOnly;
        fips.Validate();

        string[] all = [.. fips.KeyExchange, .. fips.HostKey, .. fips.EncryptionClientToServer, .. fips.MacClientToServer];
        // 群交换的群是服务端现给的素数，不是命名的群（spec/00 §6.6）。
        string[] forbidden = ["25519", "chacha20", "sntrup", "ssh-rsa", "sha1", "-cbc", "zlib", "group-exchange"];
        foreach (string algorithm in all)
        {
            Assert.DoesNotContain(f => algorithm.Contains(f, StringComparison.Ordinal), forbidden, $"不认可的算法混进来了：{algorithm}");
        }
        Assert.IsTrue(fips.HostKey.All(a => SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.HostKey, a)));
    }

    /// <summary>
    /// 〔spec/00 §6.6〕与 RHEL 10.2 的 FIPS 加密策略给 OpenSSH 的清单（<c>/usr/share/crypto-policies/FIPS/openssh.txt</c>，
    /// crypto-policies-20260216）比：主机密钥、MAC 是同一组；密钥交换只少一个群交换（服务端现给的群不是命名的群，维护者 2026-10-31 定），
    /// 加密只多一个 <c>aes192-ctr</c>。顺序不比 —— 协商以客户端的为准。曾经少了 group18，注释却说「一致」。
    /// </summary>
    [TestMethod]
    public void FIPS清单与RHEL的FIPS策略只差群交换与aes192ctr()
    {
        SshAlgorithmSet fips = SshAlgorithmSet.FipsApprovedOnly;
        string[] rhelKex =
        [
            "mlkem768nistp256-sha256", "mlkem1024nistp384-sha384",
            "ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521",
            "diffie-hellman-group-exchange-sha256", "diffie-hellman-group14-sha256",
            "diffie-hellman-group16-sha512", "diffie-hellman-group18-sha512",
        ];
        string[] rhelHostKey =
        [
            "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp256-cert-v01@openssh.com",
            "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp384-cert-v01@openssh.com",
            "ecdsa-sha2-nistp521", "ecdsa-sha2-nistp521-cert-v01@openssh.com",
            "rsa-sha2-256", "rsa-sha2-256-cert-v01@openssh.com", "rsa-sha2-512", "rsa-sha2-512-cert-v01@openssh.com",
        ];
        string[] rhelMac = ["hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com", "hmac-sha2-256", "hmac-sha2-512"];
        string[] rhelCipher = ["aes256-gcm@openssh.com", "aes256-ctr", "aes128-gcm@openssh.com", "aes128-ctr"];

        Assert.AreSequenceEqual(
            rhelKex.Where(static kex => kex != SshAlgorithmNames.DiffieHellmanGroupExchangeSha256).Order(StringComparer.Ordinal),
            fips.KeyExchange.Order(StringComparer.Ordinal));
        Assert.AreSequenceEqual(rhelHostKey.Order(StringComparer.Ordinal), fips.HostKey.Order(StringComparer.Ordinal));
        Assert.AreSequenceEqual(rhelMac.Order(StringComparer.Ordinal), fips.MacClientToServer.Order(StringComparer.Ordinal));
        Assert.AreSequenceEqual(
            rhelCipher.Append(SshAlgorithmNames.Aes192Ctr).Order(StringComparer.Ordinal),
            fips.EncryptionClientToServer.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 〔spec/00 §6.1〕FIPS 清单拿掉群交换不动默认清单：群交换照旧在里面（排在 DH 标准群之前），
    /// group18 排在 group16 之后、group14 之前（维护者 2026-10-31 确认 §251 的口径）。
    /// </summary>
    [TestMethod]
    public void 默认清单照旧有群交换_group18在group16与group14之间()
    {
        List<string> kex = [.. SshAlgorithmSet.Default.KeyExchange];
        int gex = kex.IndexOf(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256);
        int group16 = kex.IndexOf(SshAlgorithmNames.DiffieHellmanGroup16Sha512);
        int group18 = kex.IndexOf(SshAlgorithmNames.DiffieHellmanGroup18Sha512);
        int group14 = kex.IndexOf(SshAlgorithmNames.DiffieHellmanGroup14Sha256);

        Assert.IsGreaterThanOrEqualTo(0, gex, "默认清单里要有群交换");
        Assert.IsLessThan(group16, gex);
        Assert.IsLessThan(group18, group16);
        Assert.IsLessThan(group14, group18);
    }

    [TestMethod]
    public void 认得却没实现的一个都不在实现里()
    {
        foreach (SshAlgorithmCategory category in Enum.GetValues<SshAlgorithmCategory>())
        {
            IReadOnlyList<string> unimplemented = SshAlgorithmCatalog.KnownUnimplemented(category);
            Assert.IsNotEmpty(unimplemented, category.ToString());
            Assert.DoesNotContain(n => SshAlgorithmCatalog.IsImplemented(category, n), unimplemented, category.ToString());
        }

        Assert.IsFalse(SshKeyExchangeFactory.IsSupported("diffie-hellman-group1-sha1"));
        Assert.IsFalse(SshSessionKeys.IsSupportedEncryption("aes256-cbc"));
        Assert.IsFalse(SshSessionKeys.IsSupportedMac("umac-128-etm@openssh.com"));
    }
}
