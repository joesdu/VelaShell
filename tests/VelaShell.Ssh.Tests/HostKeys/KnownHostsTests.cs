// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH sshd(8) 的 SSH_KNOWN_HOSTS 章节;velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.HostKeys;

[TestClass]
[TestCategory("HostKeys")]
public sealed class KnownHostsTests
{
    private static SshPublicKey MakeKey() =>
        SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);

    private static string Line(string host, SshPublicKey key) =>
        $"{host} {key.KeyType} {Convert.ToBase64String(key.Blob.Span)}";

    private static SshHostKeyContext Context(string host, int port, SshPublicKey key) => new()
    {
        Host = host,
        Port = port,
        Key = key,
        NegotiatedAlgorithm = key.KeyType,
        HopKind = SshDialKind.Tcp,
    };

    // ------------------------------------------------------------ 解析

    [TestMethod]
    public void 注释与空行被跳过()
    {
        SshPublicKey key = MakeKey();
        string content =
            "# 这是注释\n" +
            "\n" +
            "   \n" +
            Line("example.com", key) + "\n";

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(content);

        Assert.HasCount(1, entries);
        Assert.AreEqual(4, entries[0].LineNumber, "行号要按原文件算，注释与空行也占行");
    }

    [TestMethod]
    public void 写坏的行被跳过而不是让整个文件不可用()
    {
        SshPublicKey key = MakeKey();
        string content =
            "这行完全不对\n" +
            "host ssh-ed25519 这不是合法的base64!!!\n" +
            "只有两段 ssh-ed25519\n" +
            Line("good.example.com", key) + "\n";

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(content);

        // 真实的 known_hosts 里什么都有。为其中一行报错，
        // 等于让用户所有已知主机一起失效。
        Assert.HasCount(1, entries);
        Assert.AreSequenceEqual(new[] { "good.example.com" }, [.. entries[0].Patterns]);
    }

    [TestMethod]
    public void 逗号分隔的多个主机名()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries =
            KnownHostsFile.Parse(Line("a.example.com,b.example.com,10.0.0.1", key));

        Assert.HasCount(3, entries[0].Patterns);

        foreach (string host in new[] { "a.example.com", "b.example.com", "10.0.0.1" })
        {
            Assert.AreEqual(
                KnownHostStatus.Known,
                KnownHostsFile.Lookup(entries, host, 22, key).Status, host);
        }
    }

    [TestMethod]
    public void 标记行被认出来()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            "@revoked " + Line("bad.example.com", key) + "\n" +
            "@cert-authority " + Line("*.example.com", key) + "\n");

        Assert.IsTrue(entries[0].IsRevoked);
        Assert.IsTrue(entries[1].IsCertificateAuthority);
    }

    // ------------------------------------------------------------ 查询

    [TestMethod]
    public void 主机与密钥都对上就是已知()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("example.com", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 22, key).Status);
    }

    [TestMethod]
    public void 没见过的主机是未知不是变了()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("other.example.com", key));

        // 「没见过」与「变了」是两件完全不同的事 —— 前者可以问，后者该拒。
        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "example.com", 22, key).Status);
    }

    [TestMethod]
    public void 主机对上但密钥不同就是变了并指出行号()
    {
        SshPublicKey known = MakeKey();
        SshPublicKey different = MakeKey();

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            "# 头一行是注释\n" + Line("example.com", known) + "\n");

        KnownHostLookup lookup = KnownHostsFile.Lookup(entries, "example.com", 22, different);

        Assert.AreEqual(KnownHostStatus.Changed, lookup.Status);
        Assert.HasCount(1, lookup.ConflictingEntries);

        // 行号要能指出来 —— 否则用户不知道该去删哪一行。
        Assert.AreEqual(2, lookup.ConflictingEntries[0].LineNumber);
    }

    [TestMethod]
    public void 同一台主机可以有多把不同类型的密钥()
    {
        var ed25519 = SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);
        var rsa = SshPublicKey.Decode(TestHostKey.Create("ssh-rsa").PublicKeyBlob);

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            Line("example.com", ed25519) + "\n" + Line("example.com", rsa) + "\n");

        // 两把都该算「已知」。只按「主机对上、密钥不同」就报「变了」的实现，
        // 会在服务器同时提供 ed25519 与 rsa 时随机报警。
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 22, ed25519).Status);
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 22, rsa).Status);
    }

    [TestMethod]
    public void 吊销压过一切()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            Line("example.com", key) + "\n" +
            "@revoked " + Line("example.com", key) + "\n");

        Assert.AreEqual(
            KnownHostStatus.Revoked,
            KnownHostsFile.Lookup(entries, "example.com", 22, key).Status,
            "哪怕别处还有一条「已知」，吊销也要赢");
    }

    [TestMethod]
    public void 非默认端口按方括号形式匹配()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("[example.com]:2222", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 2222, key).Status);
        Assert.AreEqual(
            KnownHostStatus.Unknown,
            KnownHostsFile.Lookup(entries, "example.com", 22, key).Status,
            "端口不同就是不同的条目 —— 22 端口上的那台机器可能完全是另一台");
    }

    [TestMethod]
    public void 通配模式()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("*.example.com", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "a.example.com", 22, key).Status);
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "b.c.example.com", 22, key).Status);
        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "example.org", 22, key).Status);
    }

    [TestMethod]
    public void 散列过的主机名也能匹配()
    {
        // OpenSSH 的 HashKnownHosts yes 在很多发行版上是默认 ——
        // 不支持它等于在那些机器上完全读不到已知主机。
        SshPublicKey key = MakeKey();
        byte[] salt = RandomNumberGenerator.GetBytes(20);

#pragma warning disable CA5350 // 格式由 OpenSSH 规定就是 HMAC-SHA1
        byte[] hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("secret.example.com"));
#pragma warning restore CA5350

        string hashedHost = string.Create(
            CultureInfo.InvariantCulture,
            $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}");

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line(hashedHost, key));

        Assert.IsTrue(entries[0].IsHashed);
        Assert.AreEqual(
            KnownHostStatus.Known,
            KnownHostsFile.Lookup(entries, "secret.example.com", 22, key).Status);
        Assert.AreEqual(
            KnownHostStatus.Unknown,
            KnownHostsFile.Lookup(entries, "other.example.com", 22, key).Status);
    }

    [TestMethod]
    public void 写出来的行能被自己读回去()
    {
        SshPublicKey key = MakeKey();

        foreach (bool hashed in new[] { false, true })
        {
            string line = KnownHostsFile.FormatEntry("example.com", 2222, key, hashed);
            IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(line);

            Assert.AreEqual(hashed, entries[0].IsHashed);
            Assert.AreEqual(
                KnownHostStatus.Known,
                KnownHostsFile.Lookup(entries, "example.com", 2222, key).Status,
                $"hashed={hashed}");
        }
    }

    // ------------------------------------------------------------ 策略

    [TestMethod]
    public async Task 已知主机直接通过()
    {
        SshPublicKey key = MakeKey();
        string path = await WriteTempAsync(Line("example.com", key));

        try
        {
            KnownHostsPolicy policy = new(path);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, key));
            Assert.AreEqual(SshHostKeyDecision.Accept, verdict.Decision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 密钥变了时拒绝并指出该去删哪一行()
    {
        SshPublicKey known = MakeKey();
        SshPublicKey different = MakeKey();
        string path = await WriteTempAsync(Line("example.com", known));

        try
        {
            KnownHostsPolicy policy = new(path);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, different));

            Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
            Assert.AreEqual(SshFailureReason.HostKeyChanged, verdict.Reason, "「变了」要与「不信任」分开报，界面上给的提示完全不同。");

            // 消息里必须把三件事说清楚：变了、可能是什么、下一步怎么办。
            Assert.Contains("变了", verdict.Message!);
            Assert.Contains("中间人", verdict.Message!);
            Assert.Contains("第 1 行", verdict.Message!);
            Assert.Contains(different.Sha256Fingerprint, verdict.Message!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 首次连接可以问并记下来()
    {
        SshPublicKey key = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        try
        {
            int asked = 0;
            KnownHostsPolicy policy = new(path, (_, _) =>
            {
                asked++;
                return ValueTask.FromResult(true);
            });

            SshHostKeyContext context = Context("new.example.com", 22, key);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(context);

            Assert.AreEqual(1, asked);
            Assert.AreEqual(SshHostKeyDecision.AcceptAndPersist, verdict.Decision);

            await policy.PersistAsync(context);

            // 记下来之后，下一次就该直接通过 —— 而且不再问。
            SshHostKeyVerdict second = await policy.EvaluateAsync(context);
            Assert.AreEqual(SshHostKeyDecision.Accept, second.Decision);
            Assert.AreEqual(1, asked, "已经记下来的主机不该再问一遍");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task 严格模式下未知主机直接拒绝()
    {
        SshPublicKey key = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        KnownHostsPolicy policy = new(path) { UnknownHost = UnknownHostBehavior.Reject };
        SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("new.example.com", 22, key));

        Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
        Assert.Contains(key.Sha256Fingerprint, verdict.Message!);
    }

    [TestMethod]
    public async Task 没配询问回调时不会悄悄放行()
    {
        SshPublicKey key = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        KnownHostsPolicy policy = new(path);   // UnknownHost = Ask，但没给回调
        SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("new.example.com", 22, key));

        // 问不了就该拒，不该默认放行 —— 默认放行等于关掉中间人防护。
        Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
    }

    [TestMethod]
    public async Task 吊销的密钥被拒并说明原因()
    {
        SshPublicKey key = MakeKey();
        string path = await WriteTempAsync("@revoked " + Line("example.com", key));

        try
        {
            KnownHostsPolicy policy = new(path);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, key));

            Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
            Assert.Contains("@revoked", verdict.Message!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteTempAsync(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, content + "\n");
        return path;
    }

    // ------------------------------------------------------------ 换一种类型

    [TestMethod]
    public void 只记着别的类型时不是没见过()
    {
        var ed25519 = SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);
        var ecdsa = SshPublicKey.Decode(TestHostKey.Create("ecdsa-sha2-nistp256").PublicKeyBlob);
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("example.com", ed25519));

        KnownHostLookup lookup = KnownHostsFile.Lookup(entries, "example.com", 22, ecdsa);

        // 中间人只要出示一种没记过的类型：当成「没见过」的话，「变了」的检查就被绕过去了。
        Assert.AreEqual(KnownHostStatus.OtherKeyTypesKnown, lookup.Status);
        Assert.HasCount(1, lookup.ConflictingEntries);
        CollectionAssert.AreEqual(
            new[] { "ssh-ed25519" }, KnownHostsFile.KnownKeyTypes(entries, "example.com", 22).ToArray());
    }

    [TestMethod]
    public async Task 只记着别的类型时连接受新主机的策略也拒绝且不写入()
    {
        var ed25519 = SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);
        var ecdsa = SshPublicKey.Decode(TestHostKey.Create("ecdsa-sha2-nistp256").PublicKeyBlob);
        string path = await WriteTempAsync(Line("example.com", ed25519));

        try
        {
            // accept-new：没见过的主机悄悄记下来 —— 正是这个模式最怕被换类型绕过去。
            KnownHostsPolicy policy = new(path) { UnknownHost = UnknownHostBehavior.AcceptAndPersist };
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, ecdsa));

            Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
            Assert.Contains("ssh-ed25519", verdict.Message!);
            Assert.HasCount(1, KnownHostsFile.Parse(await File.ReadAllTextAsync(path)), "不该写进任何东西");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void 已记下的类型排到主机密钥算法的最前面()
    {
        SshAlgorithmSet preferred = SshAlgorithmSet.Default.PreferHostKeyTypes(["ssh-rsa"]);

        // 协商以客户端顺序为准：已知类型排在前面，正常的服务端就谈成它。
        CollectionAssert.AreEquivalent(
            new[] { VelaShell.Ssh.Protocol.SshAlgorithmNames.RsaSha512, VelaShell.Ssh.Protocol.SshAlgorithmNames.RsaSha256 },
            preferred.HostKey.Take(2).ToArray());
        CollectionAssert.AreEquivalent(SshAlgorithmSet.Default.HostKey.ToArray(), preferred.HostKey.ToArray(), "只调顺序，不增删");
    }

    // ------------------------------------------------------------ 取反与追加

    [TestMethod]
    public void 取反模式对上时整行都不算这台主机()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("*.corp,!untrusted.corp", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "a.corp", 22, key).Status);
        Assert.AreEqual(
            KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "untrusted.corp", 22, key).Status,
            "写配置的人明确排除了它 —— 不能再经 *.corp 把密钥信给它");
    }

    [TestMethod]
    public async Task 文件末尾没有换行时追加不会把两条记录粘在一起()
    {
        SshPublicKey first = MakeKey();
        SshPublicKey second = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        try
        {
            // 手工编辑过的文件常常最后一行没有换行。
            await File.WriteAllTextAsync(path, Line("a.example.com", first));
            await KnownHostsFile.AppendAsync("b.example.com", 22, second, path);

            IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "a.example.com", 22, first).Status);
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "b.example.com", 22, second).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
