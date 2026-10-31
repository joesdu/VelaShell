using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.HostKeys;
using static VelaShell.Infrastructure.Tests.Ssh.HostCaPolicyTests;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 信任库与 OpenSSH <c>known_hosts</c> 的导入 / 导出,以及手动添加受信 CA 时的校验(<see cref="KnownHostMarkers" />)。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class KnownHostsInteropTests : IDisposable
{
    private readonly string _directory;
    private readonly SonnetDbEngine _engine;
    private readonly SonnetDbHostKeyService _store;

    public KnownHostsInteropTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_khinterop_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _engine = new(Path.Combine(_directory, "sonnetdb"));
        _store = new(_engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>一把公钥写成 known_hosts 第二、三栏(去掉注释)。</summary>
    private static string KeyFields(string pubFile) => string.Join(' ', Fixture(pubFile).Split(' ')[..2]);

    private Task<KnownHostsImportResult> ImportAsync(string content, params (string Host, int Port)[] candidates) =>
        KnownHostsInterop.ImportAsync(_store, content, candidates);

    private async Task<List<KnownHost>> KnownAsync(string host, int port = 22) =>
        [.. await _store.FindKnownHostKeysAsync(host, port)];

    // ------------------------------------------------------------ 导入:普通行

    [TestMethod]
    public async Task 逗号分隔的多个主机与方括号端口各记一条()
    {
        KnownHostsImportResult result = await ImportAsync(
            $"a.example,10.0.0.5 {KeyFields("hostcert-key.pub")}\n[b.example]:2222 {KeyFields("hostcert-ecdsa.pub")}\n");

        Assert.AreEqual(3, result.Imported);
        Assert.HasCount(1, await KnownAsync("a.example"));
        Assert.HasCount(1, await KnownAsync("10.0.0.5"));
        KnownHost b = (await KnownAsync("b.example", 2222)).Single();
        Assert.AreEqual(Key("hostcert-ecdsa.pub").Sha256Fingerprint, b.Fingerprint);
        Assert.AreEqual(Convert.ToBase64String(Key("hostcert-ecdsa.pub").Blob.Span), b.HostKey, "公钥原文一起记下,导得回去");
        Assert.IsEmpty(await KnownAsync("b.example"), "2222 的那条不落到 22 上");
    }

    /// <summary>同一行里的取反一票否决(按库的规则):<c>a,b,!b</c> 只记 a。</summary>
    [TestMethod]
    public async Task 同一行的取反模式一票否决()
    {
        KnownHostsImportResult result = await ImportAsync($"a.example,b.example,!b.example {KeyFields("hostcert-key.pub")}");

        Assert.AreEqual(1, result.Imported);
        Assert.HasCount(1, await KnownAsync("a.example"));
        Assert.IsEmpty(await KnownAsync("b.example"));
    }

    [TestMethod]
    public async Task 带通配的普通行跳过并计数()
    {
        KnownHostsImportResult result = await ImportAsync($"*.example {KeyFields("hostcert-key.pub")}");

        Assert.AreEqual(0, result.Imported);
        Assert.AreEqual(1, result.Wildcards);
        Assert.IsEmpty(await _store.GetKnownHostsAsync());
    }

    /// <summary>散列行拿候选(已保存的连接、已信任的主机)逐个去对,对上的按候选的 host:port 记;对不上的计数。</summary>
    [TestMethod]
    public async Task 散列主机名按候选对上才记()
    {
        SshPublicKey key = Key("hostcert-key.pub");
        string hashed22 = KnownHostsFile.FormatEntry("web.example", 22, key, hashHostName: true);
        string hashed2222 = KnownHostsFile.FormatEntry("db.example", 2222, key, hashHostName: true);
        string unmatched = KnownHostsFile.FormatEntry("ghost.example", 22, key, hashHostName: true);

        KnownHostsImportResult result = await ImportAsync($"{hashed22}\n{hashed2222}\n{unmatched}\n",
            ("Web.Example", 22), ("db.example", 2222), ("db.example", 22));

        Assert.AreEqual(2, result.Imported);
        Assert.AreEqual(1, result.HashedUnmatched);
        Assert.HasCount(1, await KnownAsync("Web.Example"), "散列按小写算,记在候选原来的写法下");
        Assert.HasCount(1, await KnownAsync("db.example", 2222));
        Assert.IsEmpty(await KnownAsync("db.example", 22));
    }

    /// <summary>导入只加不改:这台主机已经信任了同一类型的另一把钥时不覆盖(悄悄换掉等于绕过「密钥变了」)。</summary>
    [TestMethod]
    public async Task 已信任同类型的另一把钥时不覆盖()
    {
        await _store.TrustHostKeyAsync("a.example", 22, "ssh-ed25519", "SHA256:someOtherKey");

        KnownHostsImportResult result = await ImportAsync($"a.example {KeyFields("hostcert-key.pub")}");

        Assert.AreEqual(0, result.Imported);
        Assert.AreEqual(1, result.Conflicts);
        Assert.AreEqual("SHA256:someOtherKey", (await KnownAsync("a.example")).Single().Fingerprint);
    }

    /// <summary>
    /// 换库之前存下的旧记录类型只是 <c>PublicKey</c> 这类占位词(说不清是哪种钥):文件里一把指纹不同的钥同样按冲突处理,
    /// 不并排记进去 —— 曾经按「类型不同」照加,校验只比指纹,用户确认过的那把与文件里的这把就都受信了。
    /// 记录里有公钥原文的按原文的类型判;真是别的类型的照旧并排加。
    /// </summary>
    [TestMethod]
    public async Task 类型是占位词的旧记录也算冲突_不并排加()
    {
        SshPublicKey ecdsa = Key("hostcert-ecdsa.pub");
        await _store.TrustHostKeyAsync("legacy.example", 22, "PublicKey", "SHA256:legacyConfirmedKey");
        await _store.TrustHostKeyAsync("blob.example", 22, "PublicKey", ecdsa.Sha256Fingerprint, Convert.ToBase64String(ecdsa.Blob.Span));
        await _store.TrustHostKeyAsync("typed.example", 22, "ssh-rsa", "SHA256:someRsaKey");

        KnownHostsImportResult result = await ImportAsync(
            $"legacy.example {KeyFields("hostcert-key.pub")}\nblob.example {KeyFields("hostcert-key.pub")}\ntyped.example {KeyFields("hostcert-key.pub")}\n");

        Assert.AreEqual(1, result.Conflicts, "只有占位词的那条算冲突");
        Assert.AreEqual(2, result.Imported, "原文解得出是 ECDSA 的、记着 ssh-rsa 的:文件里的 Ed25519 是另一种钥,照旧并排加");
        Assert.AreEqual("SHA256:legacyConfirmedKey", (await KnownAsync("legacy.example")).Single().Fingerprint, "不并排加第二把");
        Assert.HasCount(2, await KnownAsync("blob.example"));
        Assert.HasCount(2, await KnownAsync("typed.example"));
    }

    [TestMethod]
    public async Task 已经信任的同一把钥只补上公钥原文()
    {
        SshPublicKey key = Key("hostcert-key.pub");
        await _store.TrustHostKeyAsync("a.example", 22, key.PlainKeyType, key.Sha256Fingerprint);

        KnownHostsImportResult result = await ImportAsync($"a.example {KeyFields("hostcert-key.pub")}");

        Assert.AreEqual(1, result.AlreadyTrusted);
        Assert.AreEqual(Convert.ToBase64String(key.Blob.Span), (await KnownAsync("a.example")).Single().HostKey);
    }

    [TestMethod]
    public async Task 写坏的行与认不出的标记计为跳过()
    {
        KnownHostsImportResult result = await ImportAsync(
            "# 注释\n\nbroken-line-without-key\na.example ssh-ed25519 !!notbase64!!\n@revoke * " + KeyFields("hostcert-key.pub") + "\n");

        Assert.AreEqual(0, result.Imported);
        Assert.AreEqual(3, result.Unsupported);
        Assert.AreEqual(0, result.Revocations, "@revoke 不是 @revoked:认不出的标记整行跳过,不能按普通受信行去用");
        Assert.IsEmpty(await _store.GetKnownHostsAsync());
    }

    // ------------------------------------------------------------ 导入:标记行

    [TestMethod]
    public async Task 受信CA与吊销记录导入_被吊销的普通行不导入()
    {
        string content =
            $"@cert-authority *.example,!legacy.example {KeyFields("hostcert-ca.pub")} corp host CA\n" +
            $"@revoked * {KeyFields("hostcert-ecdsa.pub")}\n" +
            $"a.example {KeyFields("hostcert-ecdsa.pub")}\n" +
            $"b.example {KeyFields("hostcert-key.pub")}\n";

        KnownHostsImportResult result = await ImportAsync(content);

        Assert.AreEqual(1, result.Authorities);
        Assert.AreEqual(1, result.Revocations);
        Assert.AreEqual(1, result.RevokedSkipped);
        Assert.AreEqual(1, result.Imported);
        IReadOnlyList<KnownHostMarker> markers = await _store.GetMarkersAsync();
        KnownHostMarker ca = markers.Single(m => m.Kind == KnownHostMarkerKind.CertificateAuthority);
        Assert.AreEqual("*.example,!legacy.example", ca.HostPatterns);
        Assert.AreEqual("corp host CA", ca.Comment);
        Assert.AreEqual(Key("hostcert-ca.pub").Sha256Fingerprint, ca.Fingerprint);
        Assert.IsEmpty(await KnownAsync("a.example"));

        KnownHostsImportResult again = await ImportAsync(content);
        Assert.AreEqual(2, again.MarkersAlreadyPresent, "重复导入同一份不越积越多");
        Assert.HasCount(2, await _store.GetMarkersAsync());
    }

    // ------------------------------------------------------------ 导出

    [TestMethod]
    public async Task 导出的文件命令行ssh读得回来()
    {
        SshPublicKey key = Key("hostcert-key.pub");
        SshPublicKey ecdsa = Key("hostcert-ecdsa.pub");
        await _store.TrustHostKeyAsync("a.example", 22, key.PlainKeyType, key.Sha256Fingerprint, Convert.ToBase64String(key.Blob.Span));
        await _store.TrustHostKeyAsync("b.example", 2222, ecdsa.PlainKeyType, ecdsa.Sha256Fingerprint, Convert.ToBase64String(ecdsa.Blob.Span));
        await _store.TrustHostKeyAsync("old.example", 22, "ssh-rsa", "SHA256:fingerprintOnly");
        await ImportAsync($"@cert-authority *.example {KeyFields("hostcert-ca.pub")}");

        foreach (bool hash in new[] { false, true })
        {
            KnownHostsExport export = await KnownHostsInterop.ExportAsync(_store, hash);

            Assert.AreEqual(2, export.HostKeys);
            Assert.AreEqual(1, export.Markers);
            Assert.AreEqual(1, export.MissingPublicKey, "只记了指纹的写不出来");
            IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(export.Content);
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "a.example", 22, key).Status);
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "b.example", 2222, ecdsa).Status);
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "server.example", 22, Key("hostcert-key-cert.pub")).Status,
                "@cert-authority 行照样认得出 CA 签的主机证书");
            Assert.AreEqual(hash, export.Content.Contains("|1|", StringComparison.Ordinal));
            Assert.AreEqual(!hash, export.Content.Contains("a.example", StringComparison.Ordinal));
            Assert.Contains("@cert-authority *.example ", export.Content, "模式原样写出");
        }
    }
}
