using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests.Credentials;

/// <summary>
/// 共享凭据(#550)的落盘:凭据本身加密存,引用它的连接一律不存认证材料。真实 SonnetDB。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public sealed class SharedCredentialStorageTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly AesSecretProtector _protector;
    private readonly string _testDirectory;

    public SharedCredentialStorageTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_credtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = SonnetDbTestTemplate.Open(Path.Combine(_testDirectory, "sonnetdb"));
        _protector = new(Path.Combine(_testDirectory, "secret.key"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task Credential_RoundTrips_WithSecretsEncryptedAtRest()
    {
        var repo = new SonnetDbSharedCredentialRepository(_engine, _protector);
        var credential = new SharedCredential
        {
            Name = "switches",
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            Password = "plaintext-pass",
            PrivateKeyPassphrase = "plaintext-phrase",
            Notes = "机房"
        };

        await repo.SaveAsync(credential);

        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.SharedCredentialsCollection,
            store => store.Get(credential.Id.ToString("D"))!.Json);
        Assert.DoesNotContain("plaintext-pass", rawJson, "落盘 JSON 不应包含明文密码");
        Assert.DoesNotContain("plaintext-phrase", rawJson, "落盘 JSON 不应包含明文口令");
        SharedCredential? loaded = await repo.GetAsync(credential.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("plaintext-pass", loaded.Password, "读出的密码应已解密");
        Assert.AreEqual("plaintext-phrase", loaded.PrivateKeyPassphrase);
        Assert.AreEqual("admin", loaded.Username);
        Assert.AreEqual("机房", loaded.Notes);
        // 保存不改调用方手里那一份(界面还拿着它的明文)。
        Assert.AreEqual("plaintext-pass", credential.Password);
    }

    [TestMethod]
    public async Task GetAll_SortsByName_AndDeleteRemoves_RaisingChangedEachTime()
    {
        var repo = new SonnetDbSharedCredentialRepository(_engine, _protector);
        int changes = 0;
        repo.Changed += (_, _) => changes++;
        var beta = new SharedCredential { Name = "beta", Password = "b" };
        await repo.SaveAsync(beta);
        await repo.SaveAsync(new SharedCredential { Name = "Alpha", Password = "a" });

        List<SharedCredential> all = await repo.GetAllAsync();
        Assert.AreSequenceEqual(["Alpha", "beta"], [.. all.Select(c => c.Name)]);

        await repo.DeleteAsync(beta.Id);

        Assert.IsNull(await repo.GetAsync(beta.Id));
        Assert.HasCount(1, await repo.GetAllAsync());
        Assert.AreEqual(3, changes);
    }

    [TestMethod]
    public async Task ListMetadata_SortsByName_ExcludesSecrets_WithoutDecrypting()
    {
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var beta = new SharedCredential
        {
            Name = "Beta",
            Username = "admin",
            Password = "beta-password",
            PrivateKeyPassphrase = "beta-passphrase"
        };
        var alpha = new SharedCredential
        {
            Name = "alpha",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/key",
            PrivateKeyPassphrase = "alpha-passphrase"
        };
        var empty = new SharedCredential { Name = "charlie", Username = "guest", Password = string.Empty };
        await repo.SaveAsync(beta);
        await repo.SaveAsync(alpha);
        await repo.SaveAsync(empty);

        List<SharedCredentialMetadata> metadata = await repo.ListMetadataAsync();

        Assert.AreSequenceEqual(
            [
                new(alpha.Id, "alpha", "root", AuthMethod.PrivateKey, false),
                new(beta.Id, "Beta", "admin", AuthMethod.Password, true),
                new(empty.Id, "charlie", "guest", AuthMethod.Password, false)
            ], metadata);
        Assert.AreEqual(0, protector.UnprotectCalls, "枚举元数据不应解密任何字段,包括空字段");
        // 白名单同时钉住返回类型的形状,防止以后给摘要添回密码或私钥口令。
        Assert.AreSequenceEqual(
            ["AuthMethod", "HasPassword", "Id", "Name", "Username"],
            [.. typeof(SharedCredentialMetadata).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal)]);
    }

    [TestMethod]
    public async Task GetPassword_SelectedId_DecryptsOnlyItsPassword()
    {
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var selected = new SharedCredential
        {
            Name = "selected",
            Password = "selected-password",
            PrivateKeyPassphrase = "selected-passphrase"
        };
        await repo.SaveAsync(new SharedCredential
        {
            Name = "other",
            Password = "other-password",
            PrivateKeyPassphrase = "other-passphrase"
        });
        await repo.SaveAsync(selected);
        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.SharedCredentialsCollection, store => store.Get(selected.Id.ToString("D"))!.Json);
        using var stored = JsonDocument.Parse(rawJson);
        string ciphertext = stored.RootElement.GetProperty("password").GetString()!;
        Assert.StartsWith("enc1:", ciphertext);
        Assert.AreNotEqual(selected.Password, ciphertext);

        string? password = await repo.GetPasswordAsync(selected.Id);

        Assert.AreEqual("selected-password", password);
        Assert.AreEqual(1, protector.UnprotectCalls, "不得解密私钥口令或其他凭据");
        Assert.AreSequenceEqual([ciphertext], protector.UnprotectedValues);
    }

    [TestMethod]
    public async Task GetPassword_MissingOrDeletedId_ReturnsNull_WithoutDecrypting()
    {
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var deleted = new SharedCredential { Name = "deleted", Password = "deleted-password" };
        await repo.SaveAsync(deleted);
        await repo.SaveAsync(new SharedCredential { Name = "retained", Password = "retained-password" });
        await repo.DeleteAsync(deleted.Id);

        Assert.IsNull(await repo.GetPasswordAsync(Guid.NewGuid()));
        Assert.IsNull(await repo.GetPasswordAsync(deleted.Id));
        Assert.AreEqual(0, protector.UnprotectCalls, "缺失或已删除的 Id 不得触发其他凭据解密");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task GetPassword_NullOrEmptyPassword_ReturnsNull_WithoutDecrypting(string? password)
    {
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var credential = new SharedCredential
        {
            Name = "empty",
            Password = password,
            PrivateKeyPassphrase = "must-stay-encrypted"
        };
        await repo.SaveAsync(credential);

        Assert.IsNull(await repo.GetPasswordAsync(credential.Id));
        Assert.AreEqual(0, protector.UnprotectCalls, "空密码不得连带解密私钥口令");
    }

    [TestMethod]
    [DataRow(AuthMethod.PrivateKey)]
    [DataRow(AuthMethod.Certificate)]
    [DataRow(AuthMethod.Agent)]
    public async Task GetPassword_NonPasswordAuth_ReturnsNull_WithoutDecrypting(AuthMethod authMethod)
    {
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var credential = new SharedCredential
        {
            Name = "not-password",
            AuthMethod = authMethod,
            Password = "unused-password",
            PrivateKeyPath = "/key",
            PrivateKeyPassphrase = "must-stay-encrypted",
            CertificatePath = "/cert"
        };
        await repo.SaveAsync(credential);

        Assert.IsNull(await repo.GetPasswordAsync(credential.Id));
        Assert.AreEqual(0, protector.UnprotectCalls, "非密码认证即使残留密码也不得解密");
    }

    [TestMethod]
    [DataRow("enc1:not-base64!")]
    [DataRow("enc1:AQID")]
    public async Task GetPassword_MalformedCiphertext_ThrowsCryptographicException(string ciphertext)
    {
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var credential = new SharedCredential { Name = "corrupt", Password = ciphertext };
        // Protect 对 enc1: 幂等,因此可把损坏的历史密文原样存入真实数据库。
        await repo.SaveAsync(credential);
        Assert.AreEqual(ciphertext, _protector.Unprotect(ciphertext), "确认保护器的失败回退是原样返回");

        await Assert.ThrowsExactlyAsync<CryptographicException>(() => repo.GetPasswordAsync(credential.Id));

        Assert.AreEqual(1, protector.UnprotectCalls);
        Assert.AreSequenceEqual([ciphertext], protector.UnprotectedValues);
    }

    [TestMethod]
    public async Task GetPassword_TamperedAuthenticationTag_ThrowsCryptographicException()
    {
        string ciphertext = _protector.Protect("original-password")!;
        byte[] blob = Convert.FromBase64String(ciphertext["enc1:".Length..]);
        blob[12] ^= 1; // 仅破坏 GCM 认证标签,保留合法的 Base64 和完整密文长度。
        string tampered = "enc1:" + Convert.ToBase64String(blob);
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var credential = new SharedCredential { Name = "tampered", Password = tampered };
        await repo.SaveAsync(credential);
        Assert.AreEqual(tampered, _protector.Unprotect(tampered));

        await Assert.ThrowsExactlyAsync<CryptographicException>(() => repo.GetPasswordAsync(credential.Id));

        Assert.AreEqual(1, protector.UnprotectCalls);
        Assert.AreSequenceEqual([tampered], protector.UnprotectedValues);
    }

    [TestMethod]
    public async Task GetPassword_EncryptedPlaintextStartingWithEnc1_ReturnsPlaintext()
    {
        const string plaintext = "enc1:合法的明文密码";
        byte[] key = RandomNumberGenerator.GetBytes(32);
        // 使用保护器支持的历史明文密钥格式;Windows 首次加载时会照常迁移为 DPAPI 密钥。
        await File.WriteAllBytesAsync(Path.Combine(_testDirectory, "secret.key"), key);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] blob = new byte[12 + 16 + plainBytes.Length];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        // Protect 会原样保留 enc1: 输入,这里手工生成相同格式的真实 AES-GCM 密文。
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(blob.AsSpan(0, 12), plainBytes, blob.AsSpan(28), blob.AsSpan(12, 16));
        }
        string ciphertext = "enc1:" + Convert.ToBase64String(blob);
        var protector = new CountingSecretProtector(_protector);
        var repo = new SonnetDbSharedCredentialRepository(_engine, protector);
        var credential = new SharedCredential { Name = "prefix-password", Password = ciphertext };
        await repo.SaveAsync(credential);
        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.SharedCredentialsCollection, store => store.Get(credential.Id.ToString("D"))!.Json);
        using var stored = JsonDocument.Parse(rawJson);
        Assert.AreEqual(ciphertext, stored.RootElement.GetProperty("password").GetString());
        Assert.AreNotEqual(plaintext, ciphertext);

        Assert.AreEqual(plaintext, await repo.GetPasswordAsync(credential.Id), "不能仅因解密结果以 enc1: 开头就拒绝");
        Assert.AreEqual(1, protector.UnprotectCalls);
        Assert.AreSequenceEqual([ciphertext], protector.UnprotectedValues);
    }

    /// <summary>
    /// 仓储不变量:引用凭据的连接,不管从哪条保存路径进来,认证材料都不落盘 ——
    /// 用户名照常存(它可以是有意的覆盖)。
    /// </summary>
    [TestMethod]
    public async Task ReferencingProfile_NeverPersistsItsAuthMaterial()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var profile = new SessionProfile
        {
            Name = "web-01",
            Host = "10.0.0.1",
            Username = "deploy",
            AuthMethod = AuthMethod.Certificate,
            // 登录框里手输的那一份:这次连接用,不落盘。
            Password = "typed-pass",
            PrivateKeyPath = "/k",
            PrivateKeyPassphrase = "typed-phrase",
            CertificatePath = "/c",
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid())
        };

        await repo.SaveSessionAsync(profile);

        SessionProfile? loaded = await repo.GetSessionAsync(profile.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(profile.CredentialSource, loaded.CredentialSource);
        Assert.AreEqual("deploy", loaded.Username);
        Assert.IsNull(loaded.Password);
        Assert.IsNull(loaded.PrivateKeyPath);
        Assert.IsNull(loaded.PrivateKeyPassphrase);
        Assert.IsNull(loaded.CertificatePath);
        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json);
        Assert.DoesNotContain("typed-pass", rawJson);
        // 调用方那一份不动:它正拿着这份材料去连。
        Assert.AreEqual("typed-pass", profile.Password);
    }

    /// <summary>插件协议的机密字典不是认证材料,引用凭据时照样加密保存(不能被不变量顺手丢掉)。</summary>
    [TestMethod]
    public async Task ReferencingPluginProfile_StillEncryptsPluginSecrets()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var profile = new SessionProfile
        {
            Name = "s3",
            Host = "s3.example.com",
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = "velashell.s3",
            PluginSecrets = new() { ["sessionToken"] = "plaintext-token" },
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid())
        };

        await repo.SaveSessionAsync(profile);

        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json);
        Assert.DoesNotContain("plaintext-token", rawJson);
        SessionProfile? loaded = await repo.GetSessionAsync(profile.Id);
        Assert.AreEqual("plaintext-token", loaded!.PluginSecrets!["sessionToken"]);
    }

    /// <summary>只记录调用边界,加解密仍委托给夹具中的真实保护器。</summary>
    private sealed class CountingSecretProtector(ISecretProtector inner) : ISecretProtector
    {
        public List<string?> UnprotectedValues { get; } = [];

        public int UnprotectCalls => UnprotectedValues.Count;

        public string? Protect(string? plaintext) => inner.Protect(plaintext);

        public string? Unprotect(string? ciphertext)
        {
            UnprotectedValues.Add(ciphertext);
            return inner.Unprotect(ciphertext);
        }
    }
}
