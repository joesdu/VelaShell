using System.Text.Json.Nodes;
using SonnetDB.Documents;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Infrastructure.Net;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 设置里的代理密码落盘加密(<see cref="SonnetDbSettingsService" /> 经 <c>ISecretProtector</c>):
/// 库里躺着的是密文,交给调用方的是明文;存量明文第一次保存时换成密文;解不开的那份既不当密码交出去,也不被一次保存抹掉。
/// </summary>
[TestClass]
public sealed class SettingsProxyPasswordTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly string _testDirectory;
    private readonly AesSecretProtector _protector;

    public SettingsProxyPasswordTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_proxypw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
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
    public async Task Save_StoresTheProxyPasswordEncrypted_AndReadsItBackAsPlaintext()
    {
        var service = new SonnetDbSettingsService(_engine, protector: _protector);
        AppSettings settings = await service.GetSettingsAsync();
        UseHttpProxy(settings, "s3cret-proxy");
        await service.SaveSettingsAsync(settings);

        string raw = await RawSettingsJsonAsync();
        Assert.DoesNotContain("s3cret-proxy", raw, "库里不该有代理密码的明文");
        Assert.StartsWith("enc1:", StoredProxyPassword(raw));
        Assert.AreEqual("s3cret-proxy", service.CurrentSnapshot!.Proxy.Password, "内存里的快照是明文");
        Assert.AreEqual("s3cret-proxy", settings.Proxy.Password, "调用方手里那份对象不被改成密文");

        var reopened = new SonnetDbSettingsService(_engine, protector: _protector);
        Assert.AreEqual("s3cret-proxy", (await reopened.GetSettingsAsync()).Proxy.Password);
    }

    [TestMethod]
    public async Task LegacyPlaintext_IsReadAsIs_AndEncryptedOnTheFirstSave()
    {
        AppSettings legacy = new();
        UseHttpProxy(legacy, "old-plain");
        await WriteRawSettingsJsonAsync(SonnetDbJson.Serialize(legacy));

        var service = new SonnetDbSettingsService(_engine, protector: _protector);
        AppSettings loaded = await service.GetSettingsAsync();
        Assert.AreEqual("old-plain", loaded.Proxy.Password, "存量明文照常读得出来");
        Assert.AreEqual("old-plain", StoredProxyPassword(await RawSettingsJsonAsync()), "只读不写:没保存之前库里不动");

        loaded.TerminalFontSize = 15;
        await service.SaveSettingsAsync(loaded);

        string raw = await RawSettingsJsonAsync();
        Assert.DoesNotContain("old-plain", raw);
        Assert.AreEqual("old-plain", _protector.Unprotect(StoredProxyPassword(raw)));
    }

    [TestMethod]
    public async Task LegacySettingsFile_IsImportedWithTheProxyPasswordEncrypted()
    {
        string legacyDirectory = Path.Combine(_testDirectory, "legacy");
        Directory.CreateDirectory(legacyDirectory);
        AppSettings legacy = new();
        UseHttpProxy(legacy, "from-json-file");
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "settings.json"), SonnetDbJson.Serialize(legacy));

        var service = new SonnetDbSettingsService(_engine, [legacyDirectory], _protector);
        Assert.AreEqual("from-json-file", (await service.GetSettingsAsync()).Proxy.Password);

        string raw = await RawSettingsJsonAsync();
        Assert.DoesNotContain("from-json-file", raw, "从旧 settings.json 导进库里的那一次就该是密文");
    }

    [TestMethod]
    public async Task UndecryptablePassword_IsHandedOutEmpty_ButSurvivesUnrelatedSaves()
    {
        await SaveWithProtectorAsync(_protector, "kept-across-keys");
        string original = StoredProxyPassword(await RawSettingsJsonAsync());

        // 换了一把本机密钥(数据目录从别处拷来、系统密钥链这次没打开):解不开。
        var otherKey = new AesSecretProtector(Path.Combine(_testDirectory, "other.key"));
        var service = new SonnetDbSettingsService(_engine, protector: otherKey);
        AppSettings loaded = await service.GetSettingsAsync();
        Assert.AreEqual(string.Empty, loaded.Proxy.Password, "解不开的密文不能当密码交给代理");

        loaded.Appearance.LastWindowWidth = 1234;
        await service.SaveSettingsAsync(loaded);

        Assert.AreEqual(original, StoredProxyPassword(await RawSettingsJsonAsync()), "一次无关的保存不能把密码抹掉");
        var backWithTheRightKey = new SonnetDbSettingsService(_engine, protector: _protector);
        Assert.AreEqual("kept-across-keys", (await backWithTheRightKey.GetSettingsAsync()).Proxy.Password,
            "密钥回来之后照常解开");
    }

    [TestMethod]
    public async Task UndecryptablePassword_IsReplacedWhenRetyped_AndDroppedWhenTheProxyChanges()
    {
        await SaveWithProtectorAsync(_protector, "old-secret");
        var otherKey = new AesSecretProtector(Path.Combine(_testDirectory, "other.key"));

        var retyped = new SonnetDbSettingsService(_engine, protector: otherKey);
        AppSettings settings = await retyped.GetSettingsAsync();
        settings.Proxy.Password = "new-secret";
        await retyped.SaveSettingsAsync(settings);
        Assert.AreEqual("new-secret", otherKey.Unprotect(StoredProxyPassword(await RawSettingsJsonAsync())),
            "重填的这一份用当前的密钥存,这台设备解得开");

        await SaveWithProtectorAsync(_protector, "old-secret");
        var moved = new SonnetDbSettingsService(_engine, protector: otherKey);
        settings = await moved.GetSettingsAsync();
        settings.Proxy.Host = "another-proxy.example";
        await moved.SaveSettingsAsync(settings);
        Assert.AreEqual(string.Empty, StoredProxyPassword(await RawSettingsJsonAsync()),
            "换了一台代理:旧密码不属于它,与导入、同步拉取同一口径");
    }

    /// <summary>
    /// 启动路径不等本机加密密钥:系统密钥链还在等用户解锁时,启动快照照常交出(代理密码先空着),
    /// 主窗口不被它卡住;密钥好了密码自己填回快照。期间要密码的读取(设置页、代理解析)等它填好,
    /// 第一次用代理就带着对的密码。
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task StartupSnapshot_DoesNotWaitForTheKeychain_AndThePasswordIsFilledInAfterwards()
    {
        string keyFile = Path.Combine(_testDirectory, "secret.key");
        await SaveWithProtectorAsync(_protector, "s3cret-proxy");
        using var keychain = new GatedKeychain();
        var slowKey = new AesSecretProtector(new MasterKeyResolver(keyFile, keychain));
        // App 启动时的预热:在后台解析密钥,卡在密钥链上。
        Task warmUp = Task.Run(slowKey.GetKeyStatus);
        Assert.IsTrue(keychain.Entered.Wait(TimeSpan.FromSeconds(10)), "预热应已在问密钥链");
        var service = new SonnetDbSettingsService(_engine, protector: slowKey);
        try
        {
            Task<AppSettings> startup = Task.Run(service.GetStartupSnapshot);
            Assert.IsTrue(startup.Wait(TimeSpan.FromSeconds(10)), "启动快照不能等密钥链(UI 线程在等它)");
            AppSettings snapshot = startup.Result;
            Assert.AreEqual("proxy.example", snapshot.Proxy.Host, "不是机密的设置照常读出来");
            Assert.AreEqual(string.Empty, snapshot.Proxy.Password, "密码等密钥好了再填");
            Assert.AreSame(snapshot, service.CurrentSnapshot, "快照照常发布(快捷键、启动外观都读它)");

            Task<AppSettings> settingsPage = service.GetSettingsAsync();
            Task<ProxyRoute> firstConnection = Task.Run(() => new ProxyResolver(service).Resolve("example.com", 22));
            // 主窗口关闭时把窗口尺寸写进它拿着的那份快照、再存回来。
            snapshot.Appearance.LastWindowWidth = 1234;
            Task closing = service.SaveSettingsAsync(snapshot);
            await Task.Delay(200);
            Assert.IsFalse(settingsPage.IsCompleted, "要改了存回去的那份必须带着密码,得等");
            Assert.IsFalse(firstConnection.IsCompleted, "第一次用代理要等密码填好");
            Assert.IsFalse(closing.IsCompleted, "保存等密码填好,不把一份缺密码的设置存回去");

            keychain.Release();

            Assert.AreEqual("s3cret-proxy", (await settingsPage).Proxy.Password);
            Assert.AreEqual("s3cret-proxy", (await firstConnection).Password);
            await closing;
            await service.SecretsLoaded;
            Assert.AreEqual("s3cret-proxy", service.CurrentSnapshot!.Proxy.Password, "填好之后的快照带着密码");
            Assert.AreEqual("s3cret-proxy", snapshot.Proxy.Password, "就地填进启动时交出去的那份");
            Assert.AreEqual("s3cret-proxy", _protector.Unprotect(StoredProxyPassword(await RawSettingsJsonAsync())),
                "存回来的那份没有把库里的密码抹掉");
        }
        finally
        {
            keychain.Release();
            await warmUp;
        }
    }

    /// <summary>问一次就卡住、放行之后答「用不了」的密钥链(keyring 锁着,用户最后点了取消)。</summary>
    private sealed class GatedKeychain : IMasterKeyStore, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public ManualResetEventSlim Entered { get; } = new(false);

        public string Name => "Gated Keychain";

        public void Release() => _gate.Set();

        public MasterKeyStoreResult Load(string keyId) => Wait();

        public MasterKeyStoreResult Save(string keyId, ReadOnlySpan<byte> key) => Wait();

        private MasterKeyStoreResult Wait()
        {
            Entered.Set();
            _gate.Wait();
            return MasterKeyStoreResult.Unavailable("locked");
        }

        public void Dispose()
        {
            _gate.Dispose();
            Entered.Dispose();
        }
    }

    private async Task SaveWithProtectorAsync(AesSecretProtector protector, string password)
    {
        var service = new SonnetDbSettingsService(_engine, protector: protector);
        AppSettings settings = await service.GetSettingsAsync();
        UseHttpProxy(settings, password);
        await service.SaveSettingsAsync(settings);
    }

    private static void UseHttpProxy(AppSettings settings, string password)
    {
        settings.Proxy.Type = "http";
        settings.Proxy.Host = "proxy.example";
        settings.Proxy.Port = 3128;
        settings.Proxy.Username = "alice";
        settings.Proxy.Password = password;
    }

    private Task<string> RawSettingsJsonAsync() =>
        _engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection, store =>
        {
            DocumentRow? row = store.Get("settings");
            return row?.Json ?? string.Empty;
        });

    private async Task WriteRawSettingsJsonAsync(string json) =>
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, store =>
        {
            store.Upsert("settings", json);
            return null;
        });

    private static string StoredProxyPassword(string raw) =>
        JsonNode.Parse(raw)?["proxy"]?["password"]?.GetValue<string>() ?? string.Empty;
}
