using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Crypto;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 连接配置里的算法偏好 → 库的算法清单:老算法开关、OpenSSH 写法的四个自定义清单、写错时的报法。
/// </summary>
/// <remarks>
/// 真实老设备(只剩 SHA-1 那几个)上的端到端用例在 <see cref="LegacyAlgorithmsIntegrationTests" />。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class SshAlgorithmPreferencesTests
{
    private const string Group14Sha1 = "diffie-hellman-group14-sha1";

    [TestMethod]
    public void NoOptions_IsTheLibraryDefault()
    {
        Assert.AreSame(SshAlgorithmSet.Default, SshAlgorithmPreferences.Build(null));
        Assert.AreEqual(SshAlgorithmSet.Default, SshAlgorithmPreferences.Build(new SshSessionOptions()));
    }

    [TestMethod]
    public void Legacy_AppendsTheSha1AlgorithmsAfterTheDefaults()
    {
        SshAlgorithmSet set = SshAlgorithmPreferences.Build(new SshSessionOptions { LegacyAlgorithms = true });

        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.KeyExchange, Group14Sha1], [.. set.KeyExchange],
            "追加在后面:对端有现代算法时照旧谈成现代算法");
        Assert.AreEqual("ssh-rsa", set.HostKey[^1]);
        CollectionAssert.IsSubsetOf(new[] { "hmac-sha1", "hmac-sha1-etm@openssh.com" }, set.MacClientToServer.ToArray());
        Assert.AreSequenceEqual([.. set.MacClientToServer], [.. set.MacServerToClient]);
        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.EncryptionClientToServer], [.. set.EncryptionClientToServer],
            "不含 CBC:本版没实现");
    }

    [TestMethod]
    public void Plus_AppendsOnlyWhatIsNotThereYet()
    {
        Assert.IsTrue(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.KeyExchange,
            $"+{Group14Sha1},curve25519-sha256", legacy: false, out IReadOnlyList<string> kex, out _));

        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.KeyExchange, Group14Sha1], [.. kex]);
    }

    [TestMethod]
    public void Minus_RemovesNamesAndWildcards()
    {
        // Terrapin 的常见缓解:关掉 chacha20-poly1305 与所有 EtM MAC
        Assert.IsTrue(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Cipher,
            "-chacha20*", legacy: false, out IReadOnlyList<string> ciphers, out _));
        Assert.IsTrue(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Mac,
            "-*-etm@openssh.com", legacy: false, out IReadOnlyList<string> macs, out _));

        Assert.DoesNotContain("chacha20-poly1305@openssh.com", [.. ciphers]);
        Assert.HasCount(SshAlgorithmSet.Default.EncryptionClientToServer.Count - 1, ciphers);
        Assert.AreSequenceEqual(["hmac-sha2-256", "hmac-sha2-512"], [.. macs]);
    }

    [TestMethod]
    public void Caret_MovesToTheFront_KeepingTheRestInOrder()
    {
        Assert.IsTrue(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Cipher,
            "^aes128-ctr", legacy: false, out IReadOnlyList<string> ciphers, out _));

        Assert.AreEqual("aes128-ctr", ciphers[0]);
        Assert.AreSequenceEqual(
            [.. SshAlgorithmSet.Default.EncryptionClientToServer.Where(c => c != "aes128-ctr")], [.. ciphers.Skip(1)]);
    }

    [TestMethod]
    public void NoPrefix_ReplacesTheList_InTheGivenOrder()
    {
        Assert.IsTrue(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Cipher,
            " aes256-ctr, aes128-ctr ,aes256-ctr ", legacy: false, out IReadOnlyList<string> ciphers, out _));

        Assert.AreSequenceEqual(["aes256-ctr", "aes128-ctr"], [.. ciphers]);
    }

    [TestMethod]
    public void TheDefaultsThatPrefixesWorkOn_FollowTheLegacySwitch()
    {
        Assert.IsTrue(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.HostKey,
            "-*cert*", legacy: true, out IReadOnlyList<string> hostKeys, out _));

        Assert.AreEqual("ssh-rsa", hostKeys[^1], "放开老算法之后,「默认」指的是含 ssh-rsa 的那一份");
        Assert.DoesNotContain(k => k.Contains("cert", StringComparison.Ordinal), hostKeys);
    }

    [TestMethod]
    public void AnUnimplementedAlgorithm_IsSaidToBeUnimplemented()
    {
        Assert.IsFalse(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Cipher,
            "+aes128-cbc", legacy: true, out _, out string? error));

        Assert.AreEqual(Strings.Format("Ssh_AlgoSpecUnimplemented", "aes128-cbc"), error,
            "抄过来的配置里最常见的就是 CBC:用户要知道的是「放开也没用」,不是「拼错了」");
    }

    [TestMethod]
    [DataRow("aes128-gmc@openssh.com")]
    [DataRow("-chacha20-poly1350@openssh.com")]
    public void AnUnknownName_IsReported_EvenInARemoval(string spec)
    {
        // 拼错了的删除项什么都删不掉,用户却以为已经关了
        Assert.IsFalse(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Cipher, spec, legacy: false, out _, out string? error));

        Assert.StartsWith(Strings.Format("Ssh_AlgoSpecUnknown", spec.TrimStart('-')), error);
    }

    [TestMethod]
    public void AWildcardThatMatchesNothing_IsFine()
    {
        Assert.IsTrue(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Cipher,
            "-*-cbc", legacy: false, out IReadOnlyList<string> ciphers, out _));

        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.EncryptionClientToServer], [.. ciphers]);
    }

    [TestMethod]
    public void RemovingEverything_IsAnError()
    {
        Assert.IsFalse(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Mac, "-*", legacy: false, out _, out string? error));

        Assert.AreEqual(Strings.Get("Ssh_AlgoSpecNothingLeft"), error);
    }

    [TestMethod]
    [DataRow("+")]
    [DataRow("- , ")]
    public void APrefixWithoutNames_IsAnError(string spec)
    {
        Assert.IsFalse(SshAlgorithmPreferences.TryApply(SshAlgorithmKind.Mac, spec, legacy: false, out _, out string? error));

        Assert.AreEqual(Strings.Get("Ssh_AlgoSpecEmpty"), error);
    }

    [TestMethod]
    public void Build_AppliesEveryList_BothDirections_AndIgnoresLegacyCompression()
    {
        SshAlgorithmSet set = SshAlgorithmPreferences.Build(new SshSessionOptions
        {
            Compression = true,
            LegacyAlgorithms = true,
            KexAlgorithms = "curve25519-sha256",
            HostKeyAlgorithms = "^ssh-rsa",
            Ciphers = "aes128-ctr",
            Macs = "hmac-sha1",
        });

        Assert.AreSequenceEqual(["curve25519-sha256"], [.. set.KeyExchange]);
        Assert.AreEqual("ssh-rsa", set.HostKey[0]);
        Assert.AreSequenceEqual(["aes128-ctr"], [.. set.EncryptionClientToServer]);
        Assert.AreSequenceEqual(["aes128-ctr"], [.. set.EncryptionServerToClient]);
        Assert.AreSequenceEqual(["hmac-sha1"], [.. set.MacClientToServer]);
        Assert.AreSequenceEqual(["hmac-sha1"], [.. set.MacServerToClient]);
        Assert.AreSequenceEqual(["none"], [.. set.CompressionClientToServer]);
        Assert.AreSequenceEqual(["none"], [.. set.CompressionServerToClient]);
    }

    [TestMethod]
    [DataRow(SshTerminalCompressionMode.UploadOnly, "zlib@openssh.com", "none")]
    [DataRow(SshTerminalCompressionMode.Bidirectional, "zlib@openssh.com", "zlib@openssh.com")]
    [DataRow((SshTerminalCompressionMode)99, "none", "none")]
    public void Build_OnlyOffersApprovedCompressionDirections(SshTerminalCompressionMode mode, string upload, string download)
    {
        SshAlgorithmSet set = SshAlgorithmPreferences.Build(new SshSessionOptions { TerminalCompression = mode });

        Assert.AreEqual(upload, set.CompressionClientToServer[0]);
        Assert.AreEqual(download, set.CompressionServerToClient[0]);
        Assert.AreEqual("none", set.CompressionClientToServer[^1]);
        Assert.AreEqual("none", set.CompressionServerToClient[^1]);
    }

    [TestMethod]
    public void Build_WithAHandEditedBadList_FailsWithAReadableConnectionError()
    {
        var options = new SshSessionOptions { Ciphers = "3des-cbc" };

        VelaSshConnectionException ex = Assert.ThrowsExactly<VelaSshConnectionException>(
            () => SshAlgorithmPreferences.Build(options));

        Assert.Contains("3des-cbc", ex.Message);
        Assert.Contains(Strings.Get("Ssh_AlgoKindEncryption"), ex.Message);
    }

    [TestMethod]
    public void TheAssemblerUsesThePreferences()
    {
        ConnectionInfo info = new()
        {
            Host = "h",
            Username = "u",
            AuthMethod = AuthMethod.Password,
            Ssh = new SshSessionOptions { LegacyAlgorithms = true },
        };

        Assert.AreEqual(Group14Sha1, SshConnectionAssembler.Algorithms(info).KeyExchange[^1]);
    }

    [TestMethod]
    public void OptionsWithOnlyAlgorithms_AreNotEmpty_AndSurviveAClone()
    {
        var options = new SshSessionOptions { LegacyAlgorithms = true, Macs = "+hmac-sha1" };

        Assert.IsFalse(options.IsEmpty, "只设了算法的配置也要存下来,不能因为「三项都没开」被当成空的丢掉");
        SshSessionOptions clone = options.Clone();
        Assert.IsTrue(clone.LegacyAlgorithms);
        Assert.AreEqual("+hmac-sha1", clone.Macs);
        Assert.IsTrue(new SshSessionOptions { Ciphers = "^aes128-ctr" }.HasCustomAlgorithms);
        Assert.IsFalse(new SshSessionOptions { Ciphers = "  " }.HasCustomAlgorithms);
    }
}
