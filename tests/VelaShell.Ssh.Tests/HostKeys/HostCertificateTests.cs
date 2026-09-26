// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.5（主机证书）
//           OpenSSH PROTOCOL.certkeys
//
// 证书样本全部由真 ssh-keygen 签发（hostcert-*，见 Keys/Fixtures/README.md）——
// 验签与字段边界要对着别人写的证书才验得出来：自己签自己验，两边会一起错。

using System.Buffers.Binary;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.HostKeys;

[TestClass]
[TestCategory("HostKeys")]
public sealed class HostCertificateTests
{
    private const string Host = "server.example";

    /// <summary>hostcert-window 的有效期是 2026-01-01 到 2027-01-01（ssh-keygen 按本地时间写）；取中间一刻。</summary>
    private static readonly DateTimeOffset InWindow = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    /// <summary>读一个 <c>.pub</c>（普通公钥或证书）里的 blob。</summary>
    private static byte[] ReadBlob(string fileName) =>
        Convert.FromBase64String(File.ReadAllText(FixturePath(fileName)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

    private static SshPublicKey LoadKey(string fileName) => SshPublicKey.Decode(ReadBlob(fileName));

    /// <summary>一行 known_hosts：<c>[marker] pattern type base64</c>。</summary>
    private static string Line(string marker, string pattern, string pubFile)
    {
        string[] fields = File.ReadAllText(FixturePath(pubFile)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (marker.Length == 0 ? "" : marker + " ") + $"{pattern} {fields[0]} {fields[1]}";
    }

    private static KnownHostLookup Lookup(string knownHosts, string certFile, string host = Host, DateTimeOffset? now = null) =>
        KnownHostsFile.Lookup(KnownHostsFile.Parse(knownHosts), host, 22, LoadKey(certFile), now ?? InWindow);

    // ------------------------------------------------------------ 解析

    [TestMethod]
    public void 主机证书的指纹是证书里那把钥的指纹()
    {
        SshPublicKey cert = LoadKey("hostcert-key-cert.pub");

        Assert.IsTrue(cert.IsCertificate);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519CertV01, cert.KeyType);
        Assert.AreEqual(SshCertificateType.Host, cert.Certificate?.CertificateType);
        Assert.AreSequenceEqual(ReadBlob("hostcert-key.pub"), cert.PlainKey.Blob.ToArray());

        // 标准答案来自 ssh-keygen -lf hostcert-key-cert.pub（它对证书显示的就是那把钥的指纹）。
        Assert.AreEqual("SHA256:wLu7cd+z0ej0HSq52O+kygizlsRxr0ICa8gDJSoA97A", cert.Sha256Fingerprint);
        Assert.AreEqual(cert.PlainKey.Sha256Fingerprint, cert.Sha256Fingerprint);
    }

    // ------------------------------------------------------------ CA 担保

    [TestMethod]
    public void CA担保的合格主机证书是已知的()
    {
        KnownHostLookup lookup = Lookup(Line("@cert-authority", "*.example", "hostcert-ca.pub"), "hostcert-key-cert.pub");

        Assert.AreEqual(KnownHostStatus.Known, lookup.Status, lookup.CertificateProblem);
        Assert.IsTrue(lookup.MatchedEntry?.IsCertificateAuthority);
    }

    [TestMethod]
    public void 证书里的每一种钥都能被CA担保()
    {
        string knownHosts = Line("@cert-authority", Host, "hostcert-ca.pub");

        foreach (string cert in new[] { "hostcert-ecdsa-cert.pub", "hostcert-rsa-cert.pub" })
        {
            KnownHostLookup lookup = Lookup(knownHosts, cert);
            Assert.AreEqual(KnownHostStatus.Known, lookup.Status, $"{cert}：{lookup.CertificateProblem}");
        }
    }

    [TestMethod]
    public void RSA_CA用SHA2签的证书认_用SHA1签的不认()
    {
        string knownHosts = Line("@cert-authority", Host, "hostcert-ca-rsa.pub");

        Assert.AreEqual(KnownHostStatus.Known, Lookup(knownHosts, "hostcert-rsasha512-cert.pub").Status);

        KnownHostLookup sha1 = Lookup(knownHosts, "hostcert-sha1-cert.pub");
        Assert.AreEqual(KnownHostStatus.CertificateInvalid, sha1.Status);
        Assert.Contains("ssh-rsa", sha1.CertificateProblem!);
    }

    [TestMethod]
    public void 过期与未生效的主机证书不合格()
    {
        string knownHosts = Line("@cert-authority", Host, "hostcert-ca.pub");

        Assert.AreEqual(KnownHostStatus.Known, Lookup(knownHosts, "hostcert-window-cert.pub", now: InWindow).Status);

        KnownHostLookup expired = Lookup(knownHosts, "hostcert-window-cert.pub", now: new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(KnownHostStatus.CertificateInvalid, expired.Status);
        Assert.Contains("过期", expired.CertificateProblem!);

        KnownHostLookup early = Lookup(knownHosts, "hostcert-window-cert.pub", now: new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(KnownHostStatus.CertificateInvalid, early.Status);
        Assert.Contains("生效", early.CertificateProblem!);
    }

    [TestMethod]
    public void 主体不含这台主机的证书不合格()
    {
        KnownHostLookup lookup = Lookup(Line("@cert-authority", "*", "hostcert-ca.pub"), "hostcert-key-cert.pub", host: "other.example");

        Assert.AreEqual(KnownHostStatus.CertificateInvalid, lookup.Status);
        Assert.Contains("other.example", lookup.CertificateProblem!);
    }

    [TestMethod]
    public void 主体比较不区分大小写且认地址()
    {
        string knownHosts = Line("@cert-authority", "*", "hostcert-ca.pub");

        Assert.AreEqual(KnownHostStatus.Known, Lookup(knownHosts, "hostcert-key-cert.pub", host: "SERVER.example").Status);
        Assert.AreEqual(KnownHostStatus.Known, Lookup(knownHosts, "hostcert-key-cert.pub", host: "10.0.0.1").Status);
    }

    [TestMethod]
    public void 没列主体的主机证书不认()
    {
        // 规范说空列表 = 对任何主体有效；对主机证书那等于一张证书冒充 CA 范围里的任何一台主机。
        KnownHostLookup lookup = Lookup(Line("@cert-authority", Host, "hostcert-ca.pub"), "hostcert-noprincipals-cert.pub");

        Assert.AreEqual(KnownHostStatus.CertificateInvalid, lookup.Status);
    }

    [TestMethod]
    public void 用户证书不能冒充主机证书()
    {
        KnownHostLookup lookup = Lookup(Line("@cert-authority", Host, "hostcert-ca.pub"), "hostcert-usertype-cert.pub");

        Assert.AreEqual(KnownHostStatus.CertificateInvalid, lookup.Status);
        Assert.Contains("用户证书", lookup.CertificateProblem!);
    }

    [TestMethod]
    public void 被改过的证书验不过CA签名()
    {
        // 改 nonce 里的一个字节：证书照样解析得出来、签发 CA 照样对得上，只有签名能发现它被改过。
        byte[] blob = ReadBlob("hostcert-key-cert.pub");
        int typeLength = (int)BinaryPrimitives.ReadUInt32BigEndian(blob);
        blob[4 + typeLength + 4] ^= 0x01;

        KnownHostLookup lookup = KnownHostsFile.Lookup(
            KnownHostsFile.Parse(Line("@cert-authority", Host, "hostcert-ca.pub")), Host, 22, SshPublicKey.Decode(blob), InWindow);

        Assert.AreEqual(KnownHostStatus.CertificateInvalid, lookup.Status);
        Assert.Contains("签名", lookup.CertificateProblem!);
    }

    // ------------------------------------------------------------ 没有 CA 担保

    [TestMethod]
    public void 没有CA行时证书按证书里那把钥查()
    {
        Assert.AreEqual(KnownHostStatus.Unknown, Lookup("", "hostcert-key-cert.pub").Status);
        Assert.AreEqual(
            KnownHostStatus.Known, Lookup(Line("", Host, "hostcert-key.pub"), "hostcert-key-cert.pub").Status,
            "记着证书里那把钥 —— 那就是这台主机");

        // 同类型的另一把 ed25519 记在这台主机名下：密钥变了，出示证书不能绕过它。
        KnownHostLookup changed = Lookup(Line("", Host, "hostcert-ca.pub"), "hostcert-key-cert.pub");
        Assert.AreEqual(KnownHostStatus.Changed, changed.Status);
    }

    [TestMethod]
    public void 单独记着的钥优先于证书检查()
    {
        // 证书已过期，但那把钥是明确记下来的 —— 它已经被信任，证书不必再看。
        string knownHosts = Line("@cert-authority", Host, "hostcert-ca.pub") + "\n" + Line("", Host, "hostcert-key.pub");

        KnownHostLookup lookup = Lookup(knownHosts, "hostcert-window-cert.pub", now: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(KnownHostStatus.Known, lookup.Status);
        Assert.IsFalse(lookup.MatchedEntry?.IsCertificateAuthority);
    }

    [TestMethod]
    public void 由CA管的主机出示没有担保的钥不当成没见过()
    {
        string knownHosts = Line("@cert-authority", Host, "hostcert-ca.pub");

        // 别的 CA 签的证书
        Assert.AreEqual(KnownHostStatus.OtherKeyTypesKnown, Lookup(knownHosts, "hostcert-othersigned-cert.pub").Status);

        // 一把普通钥
        KnownHostLookup plain = Lookup(knownHosts, "hostcert-key.pub");
        Assert.AreEqual(KnownHostStatus.OtherKeyTypesKnown, plain.Status);
        Assert.IsTrue(plain.ConflictingEntries[0].IsCertificateAuthority);
    }

    [TestMethod]
    public void CA行只管它的模式对上的主机()
    {
        Assert.AreEqual(
            KnownHostStatus.Unknown,
            Lookup(Line("@cert-authority", "*.corp", "hostcert-ca.pub"), "hostcert-key-cert.pub").Status);
    }

    // ------------------------------------------------------------ 吊销

    [TestMethod]
    public void 吊销CA或证书里的钥都作废这张证书()
    {
        string ca = Line("@cert-authority", Host, "hostcert-ca.pub");

        Assert.AreEqual(
            KnownHostStatus.Revoked,
            Lookup(ca + "\n" + Line("@revoked", "*", "hostcert-ca.pub"), "hostcert-key-cert.pub").Status,
            "吊销一个 CA 就作废它签过的全部证书");
        Assert.AreEqual(
            KnownHostStatus.Revoked,
            Lookup(ca + "\n" + Line("@revoked", "*", "hostcert-key.pub"), "hostcert-key-cert.pub").Status);
        Assert.AreEqual(
            KnownHostStatus.Revoked,
            Lookup(ca + "\n" + Line("@revoked", "*", "hostcert-key-cert.pub"), "hostcert-key-cert.pub").Status);
    }

    // ------------------------------------------------------------ 记录与排序

    [TestMethod]
    public void 记下的是证书里那把钥而不是证书()
    {
        string line = KnownHostsFile.FormatEntry(Host, 22, LoadKey("hostcert-key-cert.pub"));

        Assert.AreEqual(Line("", Host, "hostcert-key.pub"), line, "证书每次重签 blob 都会变，记证书等于下次必报「变了」");
    }

    [TestMethod]
    public void 有CA行时证书算法排到前面()
    {
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("@cert-authority", "*.example", "hostcert-ca.pub"));

        IReadOnlyList<string> types = KnownHostsFile.KnownKeyTypes(entries, Host, 22);
        Assert.Contains(SshAlgorithmNames.SshEd25519CertV01, types);
        Assert.Contains(SshAlgorithmNames.SshRsaCertV01, types);

        SshAlgorithmSet preferred = SshAlgorithmSet.Default.PreferHostKeyTypes([.. types]);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519CertV01, preferred.HostKey[0]);
        Assert.Contains(SshAlgorithmNames.RsaSha512CertV01, preferred.HostKey.Take(6).ToList(), "RSA 证书的签名算法名要映射回 ssh-rsa-cert-v01");

        // 默认清单：普通算法在前，证书变体在后。
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, SshAlgorithmSet.Default.HostKey[0]);
        Assert.DoesNotContain(SshAlgorithmNames.SshRsaCertV01, SshAlgorithmSet.Default.HostKey, "不含 SHA-1 的 ssh-rsa-cert-v01");
    }

    [TestMethod]
    public void 重协商只留下与钉住的钥同为证书或同为普通钥的算法()
    {
        // 钉住的是证书时，普通算法 ssh-ed25519 去掉后缀也「支持」—— 留着它，重协商可能谈成普通算法，
        // 服务端出示的就是那把钥而不是证书，钉住的比对失败，被当成换了主机密钥断开。
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.SshEd25519CertV01 },
            SshConnection.RestrictToPinnedHostKey(SshAlgorithmSet.Default, LoadKey("hostcert-key-cert.pub")).HostKey.ToArray());
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.RsaSha512CertV01, SshAlgorithmNames.RsaSha256CertV01 },
            SshConnection.RestrictToPinnedHostKey(SshAlgorithmSet.Default, LoadKey("hostcert-rsa-cert.pub")).HostKey.ToArray());
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.SshEd25519 },
            SshConnection.RestrictToPinnedHostKey(SshAlgorithmSet.Default, LoadKey("hostcert-key.pub")).HostKey.ToArray());
    }

    [TestMethod]
    public async Task 策略按CA接受合格证书且不落盘()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-kh-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, Line("@cert-authority", Host, "hostcert-ca.pub") + "\n", TestContext.CancellationToken);
        try
        {
            KnownHostsPolicy policy = new(path) { UnknownHost = UnknownHostBehavior.AcceptAndPersist };

            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context(LoadKey("hostcert-key-cert.pub")), TestContext.CancellationToken);
            Assert.AreEqual(SshHostKeyDecision.Accept, verdict.Decision, verdict.Message);

            SshHostKeyVerdict invalid = await policy.EvaluateAsync(Context(LoadKey("hostcert-noprincipals-cert.pub")), TestContext.CancellationToken);
            Assert.AreEqual(SshHostKeyDecision.Reject, invalid.Decision, "CA 管的主机证书不合格，不退回去按新主机接受");
            Assert.Contains("不合格", invalid.Message!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 没有CA时接受新主机记下的是那把钥()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-kh-{Guid.NewGuid():N}");
        try
        {
            KnownHostsPolicy policy = new(path) { UnknownHost = UnknownHostBehavior.AcceptAndPersist };
            SshHostKeyContext context = Context(LoadKey("hostcert-key-cert.pub"));

            SshHostKeyVerdict verdict = await policy.EvaluateAsync(context, TestContext.CancellationToken);
            Assert.AreEqual(SshHostKeyDecision.AcceptAndPersist, verdict.Decision);
            await policy.PersistAsync(context, TestContext.CancellationToken);

            string written = (await File.ReadAllTextAsync(path, TestContext.CancellationToken)).Trim();
            Assert.AreEqual(Line("", Host, "hostcert-key.pub"), written);

            // 下一次同一台主机出示（重签过的）证书，按记下的钥就是已知的。
            Assert.AreEqual(SshHostKeyDecision.Accept, (await policy.EvaluateAsync(context, TestContext.CancellationToken)).Decision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SshHostKeyContext Context(SshPublicKey key) => new()
    {
        Host = Host,
        Port = 22,
        Key = key,
        NegotiatedAlgorithm = key.SignatureAlgorithms[0],
    };

    // ------------------------------------------------------------ 握手

    /// <summary>对着出示主机证书的测试服务端握手。</summary>
    private async Task<SshKeyExchangeResult> HandshakeAsync(
        string privateKeyFile, string presentedFile, IReadOnlyList<string> serverAlgorithms,
        SshAlgorithmSet clientAlgorithms, IHostKeyPolicy? policy = null)
    {
        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(FixturePath(privateKeyFile), cancellationToken: TestContext.CancellationToken);
        var hostKey = TestHostKey.FromSigner(signer, ReadBlob(presentedFile), serverAlgorithms);

        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        await using TestSshServer server = new(serverStream, new TestSshServerOptions { HostKey = hostKey });
        await using SshPacketTransport clientTransport = new(clientStream);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        Task<TestSshServerHandshake> serverTask = server.HandshakeAsync(cts.Token);
        SshVersionExchangeResult versions = await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);

        SshKeyExchangeRunner runner = new(clientTransport, clientAlgorithms, policy ?? new DangerousAcceptAnyHostKeyPolicy());
        try
        {
            SshKeyExchangeResult result = await runner.RunAsync(versions, Host, 22, cancellationToken: cts.Token);
            await serverTask;
            return result;
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await serverTask;
            }
            catch (Exception)
            {
                // 客户端失败时服务端一侧必然跟着失败，那不是这里要看的。
            }
        }
    }

    private static SshAlgorithmSet PreferCertificates =>
        SshAlgorithmSet.Default.PreferHostKeyTypes([SshAlgorithmNames.SshEd25519CertV01, SshAlgorithmNames.SshRsaCertV01]);

    [TestMethod]
    public async Task 能与出示主机证书的服务端握手并按CA验过()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-kh-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, Line("@cert-authority", Host, "hostcert-ca.pub") + "\n", TestContext.CancellationToken);
        try
        {
            SshKeyExchangeResult result = await HandshakeAsync(
                "hostcert-key", "hostcert-key-cert.pub", [SshAlgorithmNames.SshEd25519CertV01, SshAlgorithmNames.SshEd25519],
                PreferCertificates, new KnownHostsPolicy(path) { UnknownHost = UnknownHostBehavior.Reject });

            Assert.AreEqual(SshAlgorithmNames.SshEd25519CertV01, result.Algorithms.HostKey);
            Assert.IsTrue(result.HostKey.IsCertificate);
            Assert.AreEqual("host-valid", result.HostKey.Certificate?.KeyId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RSA主机证书用SHA2签名算法握手()
    {
        // 签名 blob 里写 rsa-sha2-512，协商出的是 rsa-sha2-512-cert-v01：验签前要把后缀去掉。
        SshKeyExchangeResult result = await HandshakeAsync(
            "hostcert-rsa", "hostcert-rsa-cert.pub", [SshAlgorithmNames.RsaSha512CertV01], PreferCertificates);

        Assert.AreEqual(SshAlgorithmNames.RsaSha512CertV01, result.Algorithms.HostKey);
        Assert.AreEqual(SshAlgorithmNames.SshRsaCertV01, result.HostKey.KeyType);
    }

    [TestMethod]
    public async Task RSA主机证书里的钥也受长度下限约束()
    {
        // 回归：长度检查曾按 KeyType == "ssh-rsa" 判断，而 RSA 证书的类型串是 ssh-rsa-cert-v01 —— 检查整个落空。
        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(() => HandshakeAsync(
            "hostcert-rsa1024", "hostcert-rsa1024-cert.pub", [SshAlgorithmNames.RsaSha512CertV01], PreferCertificates));

        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
        Assert.Contains("1024", ex.Message);
    }

    [TestMethod]
    public async Task 谈成证书算法却出示普通钥被拒()
    {
        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(() => HandshakeAsync(
            "hostcert-key", "hostcert-key.pub", [SshAlgorithmNames.SshEd25519CertV01], PreferCertificates));
        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);

        // 反过来：谈成普通算法却出示证书。
        ex = await Assert.ThrowsExactlyAsync<SshConnectException>(() => HandshakeAsync(
            "hostcert-key", "hostcert-key-cert.pub", [SshAlgorithmNames.SshEd25519], SshAlgorithmSet.Default));
        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
    }
}
