using System.Globalization;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 宿主侧的受信主机 CA(<c>@cert-authority</c>)与吊销(<c>@revoked</c>):信任库存记录、<see cref="VelaHostKeyPolicy" />
/// 按 velashell-docs/zh/ssh/spec/03 §5.5 的顺序裁决。证书样本全部由真 ssh-keygen 签发
/// (<c>tests/VelaShell.Ssh.Tests/Keys/Fixtures/hostcert-*</c>,主体 <c>server.example,10.0.0.1</c>)。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class HostCaPolicyTests : IDisposable
{
    private const string Host = "server.example";

    private readonly string _directory;
    private readonly SonnetDbEngine _engine;
    private readonly SonnetDbHostKeyService _store;

    public HostCaPolicyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_hostca_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _engine = SonnetDbTestTemplate.Open(Path.Combine(_directory, "sonnetdb"));
        _store = new(_engine);

        // 拒绝理由的文案按英文核对:中文界面的那几句与库的中文原文措辞相同,在中文环境下核对不出用的是哪一套。
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _uiCulture;
        _engine.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ssh", "Fixtures", name)).Trim();

    internal static SshPublicKey Key(string name) => SshPublicKey.Parse(Fixture(name));

    private async Task AddCaAsync(string patterns, string caFile = "hostcert-ca.pub")
    {
        Assert.IsTrue(KnownHostMarkers.TryCreate(patterns, Fixture(caFile), null, out KnownHostMarker? marker, out string? error), error);
        Assert.IsTrue(await _store.AddMarkerAsync(marker));
    }

    private static SshHostKeyContext Context(SshPublicKey key, string host = Host, int port = 22) => new()
    {
        Host = host,
        Port = port,
        Key = key,
        NegotiatedAlgorithm = key.KeyType,
    };

    /// <summary>首次连接要问(开着「首次连接确认」):用来断言 CA 管的主机**没有**退回去问。</summary>
    private static ISettingsService AskingSettings()
    {
        AppSettings snapshot = new();
        snapshot.Security.ConfirmFirstFingerprint = true;
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.CurrentSnapshot.Returns(snapshot);
        return settings;
    }

    private static IHostKeyPrompt TrustingPrompt()
    {
        IHostKeyPrompt prompt = Substitute.For<IHostKeyPrompt>();
        prompt.DecideAsync(default!, default, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(HostKeyDecision.TrustPermanently);
        return prompt;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ------------------------------------------------------------ CA 担保

    [TestMethod]
    public async Task CA担保的合格主机证书直接放行_不问也不记()
    {
        await AddCaAsync("*.example");
        IHostKeyPrompt prompt = TrustingPrompt();

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, AskingSettings(), prompt, alerts: null)
            .EvaluateAsync(Context(Key("hostcert-key-cert.pub")));

        Assert.IsTrue(verdict.IsAccepted);
        Assert.AreEqual(SshHostKeyDecision.Accept, verdict.Decision, "CA 验过的不记进信任库:证书每次重签都会变");
        await prompt.DidNotReceiveWithAnyArgs().DecideAsync(default!, default, default!, default!, default, default, default, default);
        Assert.IsEmpty(await _store.GetKnownHostsAsync());
    }

    [TestMethod]
    public async Task 主体不含被连的主机名_拒绝且不退回首次确认()
    {
        await AddCaAsync("*.example");
        IHostKeyPrompt prompt = TrustingPrompt();

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, AskingSettings(), prompt, alerts: null)
            .EvaluateAsync(Context(Key("hostcert-key-cert.pub"), host: "other.example"));

        Assert.IsFalse(verdict.IsAccepted);
        Assert.AreEqual(SshFailureReason.HostKeyRejected, verdict.Reason);
        Assert.Contains("other.example", verdict.Message!);
        Assert.Contains("server.example", verdict.Message!, "说清证书签给了谁");
        await prompt.DidNotReceiveWithAnyArgs().DecideAsync(default!, default, default!, default!, default, default, default, default);

        // 原因与其它入口共用一套文案(SshInterop.LocalizeHostCertificateProblem),由库判定的那一条决定说哪句。
        OpenSshCertificateView cert = CertificateOf("hostcert-key-cert.pub");
        Assert.Contains(
            Strings.Format("SshErr_HostCertHostNotListed", cert.KeyId, string.Join(", ", cert.Principals), "other.example"), verdict.Message!);
    }

    /// <summary>证书上用来核对文案的几项。</summary>
    private sealed record OpenSshCertificateView(string KeyId, IReadOnlyList<string> Principals, DateTimeOffset? ValidAfter, DateTimeOffset? ValidBefore);

    private static OpenSshCertificateView CertificateOf(string fileName)
    {
        OpenSshCertificate certificate = Key(fileName).Certificate!;
        return new(certificate.KeyId, certificate.ValidPrincipals, certificate.ValidAfterTime, certificate.ValidBeforeTime);
    }

    [TestMethod]
    public async Task 主体按IP也认()
    {
        await AddCaAsync("10.0.0.*");

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, AskingSettings(), TrustingPrompt(), alerts: null)
            .EvaluateAsync(Context(Key("hostcert-key-cert.pub"), host: "10.0.0.1"));

        Assert.IsTrue(verdict.IsAccepted);
    }

    /// <summary>有效期按注入的时钟验:窗口内放行,过期与未生效都拒绝,并说清是哪一头。</summary>
    [TestMethod]
    public async Task 有效期内放行_过期与未生效拒绝()
    {
        await AddCaAsync(Host);
        SshPublicKey cert = Key("hostcert-window-cert.pub");   // 2026-01-01 到 2027-01-01

        SshHostKeyVerdict inside = await new VelaHostKeyPolicy(_store, null, null, null,
            timeProvider: new FixedTime(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))).EvaluateAsync(Context(cert));
        SshHostKeyVerdict expired = await new VelaHostKeyPolicy(_store, null, null, null,
            timeProvider: new FixedTime(new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero))).EvaluateAsync(Context(cert));
        SshHostKeyVerdict early = await new VelaHostKeyPolicy(_store, null, null, null,
            timeProvider: new FixedTime(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero))).EvaluateAsync(Context(cert));

        Assert.IsTrue(inside.IsAccepted);
        Assert.IsFalse(expired.IsAccepted);
        Assert.IsFalse(early.IsAccepted);
        Assert.AreNotEqual(expired.Message, early.Message, "过期与未生效是两句话");
        OpenSshCertificateView window = CertificateOf("hostcert-window-cert.pub");
        Assert.Contains(Strings.Format("SshErr_HostCertExpired", window.KeyId,
            window.ValidBefore!.Value.ToString("u", CultureInfo.InvariantCulture)), expired.Message!);
        Assert.Contains(Strings.Format("SshErr_HostCertNotYetValid", window.KeyId,
            window.ValidAfter!.Value.ToString("u", CultureInfo.InvariantCulture)), early.Message!);
        Assert.IsEmpty(await _store.GetKnownHostsAsync(), "不合格也不退回 TOFU 去记(关着首次确认时 TOFU 本来会记下)");
    }

    [TestMethod]
    [DataRow("hostcert-noprincipals-cert.pub", "SshErr_HostCertNoPrincipals")]
    [DataRow("hostcert-usertype-cert.pub", "SshErr_HostCertNotHostCertificate")]
    public async Task 没列主体与用户证书都拒绝(string certFile, string reasonKey)
    {
        await AddCaAsync("*");

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, null, null, null).EvaluateAsync(Context(Key(certFile)));

        Assert.IsFalse(verdict.IsAccepted);
        Assert.AreEqual(SshFailureReason.HostKeyRejected, verdict.Reason);
        Assert.IsEmpty(await _store.GetKnownHostsAsync());
        Assert.Contains(Strings.Format(reasonKey, CertificateOf(certFile).KeyId), verdict.Message!);
    }

    // ------------------------------------------------------------ CA 管的主机出示没有担保的钥

    /// <summary>规格 §5.5 第 4 条:CA 管的主机出示普通钥(或别的 CA 签的证书),那把钥又没单独记着 → 拒绝,不问、不记。</summary>
    [TestMethod]
    [DataRow("hostcert-key.pub")]
    [DataRow("hostcert-othersigned-cert.pub")]
    public async Task CA管的主机出示没有担保的钥_拒绝且不问不记(string keyFile)
    {
        await AddCaAsync("*.example");
        IHostKeyPrompt prompt = TrustingPrompt();

        SshHostKeyVerdict asked = await new VelaHostKeyPolicy(_store, AskingSettings(), prompt, alerts: null)
            .EvaluateAsync(Context(Key(keyFile)));
        // 不问的那条路(没有确认弹窗可用)上,没见过的主机本来按 TOFU 直接记下 —— CA 管的主机也不行。
        SshHostKeyVerdict silent = await new VelaHostKeyPolicy(_store, null, null, null).EvaluateAsync(Context(Key(keyFile)));

        Assert.IsFalse(asked.IsAccepted);
        Assert.IsFalse(silent.IsAccepted);
        Assert.AreEqual(SshFailureReason.HostKeyChanged, asked.Reason, "与「只记着别的类型」同样处理");
        Assert.Contains("*.example", asked.Message!);
        await prompt.DidNotReceiveWithAnyArgs().DecideAsync(default!, default, default!, default!, default, default, default, default);
        Assert.IsEmpty(await _store.GetKnownHostsAsync());
    }

    [TestMethod]
    public async Task 单独记着的钥优先_CA管的主机出示普通钥照样放行()
    {
        await AddCaAsync("*.example");
        SshPublicKey plain = Key("hostcert-key.pub");
        await _store.TrustHostKeyAsync(Host, 22, plain.PlainKeyType, plain.Sha256Fingerprint);

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, null, null, null).EvaluateAsync(Context(plain));

        Assert.IsTrue(verdict.IsAccepted);
    }

    [TestMethod]
    public async Task 单独记着的钥优先_证书不合格也放行()
    {
        await AddCaAsync("*.example");
        SshPublicKey cert = Key("hostcert-key-cert.pub");
        await _store.TrustHostKeyAsync("other.example", 22, cert.PlainKeyType, cert.Sha256Fingerprint);

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, null, null, null)
            .EvaluateAsync(Context(cert, host: "other.example"));

        Assert.IsTrue(verdict.IsAccepted, "主体对不上,但证书里那把钥本来就单独信任过");
    }

    // ------------------------------------------------------------ 模式

    /// <summary>取反一票否决:<c>*.example,!server.example</c> 不管 server.example,它退回原来的流程(问的时候摆出签发 CA)。</summary>
    [TestMethod]
    public async Task 取反模式把这台排除在外_退回原来的流程并摆出签发CA()
    {
        await AddCaAsync("*.example,!server.example");
        IHostKeyPrompt prompt = TrustingPrompt();
        SshPublicKey cert = Key("hostcert-key-cert.pub");

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, AskingSettings(), prompt, alerts: null)
            .EvaluateAsync(Context(cert));

        Assert.AreEqual(SshHostKeyDecision.AcceptAndPersist, verdict.Decision);
        await prompt.Received(1).DecideAsync(Host, 22, cert.PlainKeyType, cert.Sha256Fingerprint, HostKeyVerification.Unknown,
            Arg.Any<string?>(), Arg.Any<string?>(), Key("hostcert-ca.pub").Sha256Fingerprint);
    }

    [TestMethod]
    public async Task 非22端口按方括号模式匹配()
    {
        await AddCaAsync("[server.example]:2222");

        SshHostKeyVerdict on2222 = await new VelaHostKeyPolicy(_store, null, null, null)
            .EvaluateAsync(Context(Key("hostcert-key.pub"), port: 2222));
        SshHostKeyVerdict on22 = await new VelaHostKeyPolicy(_store, null, null, null)
            .EvaluateAsync(Context(Key("hostcert-key.pub"), port: 22));

        Assert.IsFalse(on2222.IsAccepted, "2222 归 CA 管,普通钥不退回 TOFU");
        Assert.AreEqual(SshHostKeyDecision.AcceptAndPersist, on22.Decision, "22 不归它管:照旧 TOFU");
    }

    [TestMethod]
    public async Task 散列过的模式也认()
    {
        // 散列行的主机名一栏借库的 FormatEntry 生成(HMAC-SHA1(salt, 小写主机名))。
        string hashedName = KnownHostsFile.FormatEntry(Host, 22, Key("hostcert-key.pub"), hashHostName: true).Split(' ')[0];
        string line = $"@cert-authority {hashedName} {Fixture("hostcert-ca.pub")}";
        Assert.IsTrue(KnownHostMarkers.TryCreate(null, line, null, out KnownHostMarker? marker, out string? error), error);
        await _store.AddMarkerAsync(marker);

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, null, null, null).EvaluateAsync(Context(Key("hostcert-key-cert.pub")));

        Assert.IsTrue(verdict.IsAccepted);
    }

    // ------------------------------------------------------------ 吊销

    [TestMethod]
    public async Task 吊销压过信任()
    {
        SshPublicKey plain = Key("hostcert-key.pub");
        await _store.TrustHostKeyAsync(Host, 22, plain.PlainKeyType, plain.Sha256Fingerprint);
        Assert.IsTrue(KnownHostMarkers.TryCreate(null, $"@revoked * {Fixture("hostcert-key.pub")}", null,
            out KnownHostMarker? revoked, out string? error), error);
        Assert.AreEqual(KnownHostMarkerKind.Revoked, revoked.Kind);
        await _store.AddMarkerAsync(revoked);
        ISecurityAlertService alerts = Substitute.For<ISecurityAlertService>();

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, null, null, alerts).EvaluateAsync(Context(plain));

        Assert.IsFalse(verdict.IsAccepted);
        Assert.AreEqual(SshFailureReason.HostKeyRejected, verdict.Reason);
        await alerts.Received(1).RaiseAsync("hostkey-rejected", Arg.Any<string>(), Arg.Any<object?>());
    }

    [TestMethod]
    public async Task 吊销CA就作废它签的全部证书()
    {
        await AddCaAsync("*.example");
        Assert.IsTrue(KnownHostMarkers.TryCreate(null, $"@revoked * {Fixture("hostcert-ca.pub")}", null,
            out KnownHostMarker? revoked, out string? error), error);
        await _store.AddMarkerAsync(revoked);

        SshHostKeyVerdict verdict = await new VelaHostKeyPolicy(_store, null, null, null).EvaluateAsync(Context(Key("hostcert-key-cert.pub")));

        Assert.IsFalse(verdict.IsAccepted);
    }

    /// <summary>读不出 CA / 吊销记录时连接失败(fail-closed),而不是当成没有 —— 那会把吊销了的钥放回首次确认。</summary>
    [TestMethod]
    public async Task 读不出CA记录时裁决照实失败()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.GetMarkersAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("数据库被锁"));
        store.VerifyHostKeyAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(HostKeyVerification.Unknown);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await new VelaHostKeyPolicy(store, null, null, null).EvaluateAsync(Context(Key("hostcert-key.pub"))));
    }

    // ------------------------------------------------------------ 算法排序

    /// <summary>有对上的 CA 时交出证书类型,证书算法排到前面;记着的普通类型仍在前(库只调顺序)。</summary>
    [TestMethod]
    public async Task 有对上的CA时证书算法排到前面()
    {
        await AddCaAsync("*.example");
        VelaHostKeyPolicy policy = new(_store, null, null, null);

        IReadOnlyList<string> covered = await policy.GetKnownKeyTypesAsync(Host, 22);
        IReadOnlyList<string> elsewhere = await policy.GetKnownKeyTypesAsync("host.other", 22);

        Assert.Contains("ssh-ed25519-cert-v01@openssh.com", covered);
        Assert.IsEmpty(elsewhere);
        SshAlgorithmSet preferred = SshAlgorithmSet.Default.PreferHostKeyTypes([.. covered]);
        Assert.AreEqual("ssh-ed25519-cert-v01@openssh.com", preferred.HostKey[0], "默认清单里证书排在后面,不提前就永远谈不成证书");

        SshPublicKey plain = Key("hostcert-key.pub");
        await _store.TrustHostKeyAsync(Host, 22, plain.PlainKeyType, plain.Sha256Fingerprint);
        IReadOnlyList<string> both = await policy.GetKnownKeyTypesAsync(Host, 22);
        Assert.AreEqual("ssh-ed25519", SshAlgorithmSet.Default.PreferHostKeyTypes([.. both]).HostKey[0],
            "普通钥也记着时普通算法仍在前");
    }

    [TestMethod]
    public async Task 读不出CA记录时算法排序只用记着的类型()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.GetMarkersAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("数据库被锁"));
        store.FindKnownHostKeysAsync(Host, 22, Arg.Any<CancellationToken>())
            .Returns([new KnownHost { Host = Host, Port = 22, KeyType = "ssh-rsa", Fingerprint = "SHA256:x" }]);

        Assert.AreSequenceEqual(["ssh-rsa"], [.. await new VelaHostKeyPolicy(store, null, null, null).GetKnownKeyTypesAsync(Host, 22)]);
    }

    // ------------------------------------------------------------ 公钥原文

    [TestMethod]
    public async Task 信任时记下公钥原文_老记录连上时补上()
    {
        SshPublicKey plain = Key("hostcert-key.pub");
        VelaHostKeyPolicy policy = new(_store, null, null, null);

        await policy.PersistAsync(Context(plain));
        Assert.AreEqual(Convert.ToBase64String(plain.Blob.Span), (await _store.GetKnownHostsAsync())[0].HostKey);

        // 只存了指纹的老记录(换库前、或这个字段加进来之前信任的):连上一次就补上。
        await _store.RemoveKnownHostAsync(Host, 22);
        await _store.TrustHostKeyAsync(Host, 22, plain.PlainKeyType, plain.Sha256Fingerprint);
        Assert.AreEqual("", (await _store.GetKnownHostsAsync())[0].HostKey);
        Assert.IsTrue((await policy.EvaluateAsync(Context(plain))).IsAccepted);
        Assert.AreEqual(Convert.ToBase64String(plain.Blob.Span), (await _store.GetKnownHostsAsync())[0].HostKey);
    }

    /// <summary>主机证书记下的是证书里那把钥的 blob,不是证书。</summary>
    [TestMethod]
    public async Task 主机证书记下的是证书里那把钥()
    {
        await new VelaHostKeyPolicy(_store, null, null, null).PersistAsync(Context(Key("hostcert-key-cert.pub")));

        Assert.AreEqual(Convert.ToBase64String(Key("hostcert-key.pub").Blob.Span), (await _store.GetKnownHostsAsync())[0].HostKey);
    }

    [TestMethod]
    public async Task 换了一把钥而没带原文时清掉旧原文()
    {
        SshPublicKey plain = Key("hostcert-key.pub");
        await _store.TrustHostKeyAsync(Host, 22, plain.PlainKeyType, plain.Sha256Fingerprint, Convert.ToBase64String(plain.Blob.Span));

        await _store.TrustHostKeyAsync(Host, 22, plain.PlainKeyType, plain.Sha256Fingerprint);
        Assert.AreNotEqual("", (await _store.GetKnownHostsAsync())[0].HostKey, "同一把钥:原文留着");

        await _store.TrustHostKeyAsync(Host, 22, plain.PlainKeyType, "SHA256:another");
        Assert.AreEqual("", (await _store.GetKnownHostsAsync())[0].HostKey, "换了钥:上一把的原文不能留着冒充");
    }

    // ------------------------------------------------------------ 信任库的记录

    [TestMethod]
    public async Task 同样的CA不重复加_删掉即不再生效()
    {
        await AddCaAsync("*.example");
        Assert.IsTrue(KnownHostMarkers.TryCreate("*.example", Fixture("hostcert-ca.pub"), null, out KnownHostMarker? again, out _));
        Assert.IsFalse(await _store.AddMarkerAsync(again));
        Assert.HasCount(1, await _store.GetMarkersAsync());

        await _store.RemoveMarkerAsync((await _store.GetMarkersAsync())[0].Id);

        Assert.IsEmpty(await _store.GetMarkersAsync());
        Assert.AreEqual(SshHostKeyDecision.AcceptAndPersist,
            (await new VelaHostKeyPolicy(_store, null, null, null).EvaluateAsync(Context(Key("hostcert-key.pub")))).Decision);
    }
}
