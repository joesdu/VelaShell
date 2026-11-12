using System.Runtime.Versioning;
using System.Security.Cryptography;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 本机加密密钥交给系统密钥链(主密码保护第一步):迁移幂等、可中断,任何失败都停在「照旧能用」,
/// 密钥链暂时不可用、条目不在时已存的密文不会变成永远解不开。密钥链用内存假实现;
/// Windows 上的 DPAPI 用真的。
/// </summary>
[TestClass]
public sealed class MasterKeyResolverTests
{
    private string _root = null!;
    private string _keyFile = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _keyFile = Path.Combine(_root, "secret.key");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 留给临时目录清理。
        }
    }

    private string ReferenceFile => _keyFile + MasterKeyResolver.ReferenceSuffix;

    private AesSecretProtector Protector(IMasterKeyStore? store) => new(new MasterKeyResolver(_keyFile, store));

    private string[] ReferencedIds() =>
        [.. File.ReadAllLines(ReferenceFile).Where(static l => l.StartsWith("key=", StringComparison.Ordinal)).Select(static l => l[4..])];

    /// <summary>用旧版的方式(只用密钥文件)加密一段明文,返回密文与那把密钥。</summary>
    private (string Cipher, byte[] Key) EncryptWithLegacyKeyFile(string plaintext)
    {
        string cipher = new AesSecretProtector(_keyFile).Protect(plaintext)!;
        byte[] stored = File.ReadAllBytes(_keyFile);
        byte[] key = OperatingSystem.IsWindows() ? UnwrapDpapi(stored) : stored;
        return (cipher, key);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnwrapDpapi(byte[] wrapped) =>
        ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser);

    // ———— 全新安装 ————

    [TestMethod]
    public void FreshInstall_WithKeychain_NeverWritesAKeyFile()
    {
        var store = new FakeKeychain();

        string cipher = Protector(store).Protect("hunter2")!;

        Assert.IsFalse(File.Exists(_keyFile), "密钥链能用时,数据目录里不该出现密钥文件");
        string id = Assert.ContainsSingle(ReferencedIds());
        Assert.IsTrue(store.Entries.ContainsKey(id));
        Assert.AreEqual(id, MasterKeyResolver.KeyIdOf(store.Entries[id]), "条目编号由密钥派生");
        Assert.DoesNotContain(Convert.ToHexString(store.Entries[id]), File.ReadAllText(ReferenceFile),
            StringComparison.OrdinalIgnoreCase, "清单里只有编号,没有密钥");
        AesSecretProtector next = Protector(store);
        Assert.AreEqual("hunter2", next.Unprotect(cipher), "下次启动从密钥链取回同一把");
        Assert.AreEqual(MasterKeyProtection.SystemKeychain, next.GetKeyStatus().Protection);
        Assert.IsFalse(next.GetKeyStatus().HasUnreadableSecrets);
    }

    [TestMethod]
    public void FreshInstall_WithoutKeychain_FallsBackToTheKeyFileAndSaysWhy()
    {
        var store = new FakeKeychain { Unavailable = "Cannot autolaunch D-Bus without X11 $DISPLAY" };

        AesSecretProtector protector = Protector(store);
        string cipher = protector.Protect("hunter2")!;

        Assert.IsTrue(File.Exists(_keyFile), "回落:密钥照旧落到本地密钥文件");
        Assert.IsFalse(File.Exists(ReferenceFile));
        MasterKeyStatus status = protector.GetKeyStatus();
        Assert.AreEqual(OperatingSystem.IsWindows() ? MasterKeyProtection.UserBoundFile : MasterKeyProtection.PlainFile, status.Protection);
        Assert.AreEqual("Cannot autolaunch D-Bus without X11 $DISPLAY", status.KeychainError, "回落的原因要如实交给界面与日志");
        Assert.AreEqual("hunter2", Protector(store).Unprotect(cipher));
    }

    // ———— 存量迁移 ————

    [TestMethod]
    public void LegacyKeyFile_IsMovedIntoTheKeychain_AndOldCiphertextStillDecrypts()
    {
        (string cipher, byte[] key) = EncryptWithLegacyKeyFile("hunter2");
        var store = new FakeKeychain();

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher), "迁移无损:旧密文照样解得开");
        Assert.IsTrue(protector.GetKeyStatus().Migrated);
        Assert.AreEqual(MasterKeyProtection.SystemKeychain, protector.GetKeyStatus().Protection);
        Assert.IsFalse(File.Exists(_keyFile), "迁移成功后本地密钥文件删掉");
        Assert.IsEmpty(Directory.GetFiles(_root, "secret.key.deleting"), "中转文件也不留");
        string id = Assert.ContainsSingle(ReferencedIds());
        Assert.AreEqual(MasterKeyResolver.KeyIdOf(key), id);
        Assert.AreSequenceEqual(key, store.Entries[id], "密钥链里存的就是原来那把");
        Assert.AreEqual("hunter2", Protector(store).Unprotect(cipher), "迁移之后的下一次启动");
    }

    [TestMethod]
    public void Migration_IsIdempotent()
    {
        (string cipher, _) = EncryptWithLegacyKeyFile("hunter2");
        var store = new FakeKeychain();

        bool[] migrated = [.. Enumerable.Range(0, 3).Select(_ => Protector(store).GetKeyStatus().Migrated)];

        Assert.AreSequenceEqual([true, false, false], migrated);
        Assert.AreEqual(1, store.SaveCount, "同一把密钥只写一次");
        Assert.ContainsSingle(ReferencedIds());
        Assert.ContainsSingle(store.Entries);
        Assert.AreEqual("hunter2", Protector(store).Unprotect(cipher));
    }

    [TestMethod]
    public void Migration_InterruptedAfterTheKeychainWrite_FinishesOnTheNextStart()
    {
        // 上一次写进了密钥链,还没来得及记清单、删文件就断了。
        (string cipher, byte[] key) = EncryptWithLegacyKeyFile("hunter2");
        var store = new FakeKeychain();
        store.Entries[MasterKeyResolver.KeyIdOf(key)] = key;

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.AreEqual(0, store.SaveCount, "条目已在且一致,不再写第二遍");
        Assert.AreEqual(MasterKeyResolver.KeyIdOf(key), Assert.ContainsSingle(ReferencedIds()));
        Assert.IsFalse(File.Exists(_keyFile));
    }

    [TestMethod]
    public void Migration_InterruptedAfterTheReferenceWrite_RemovesTheLeftoverKeyFile()
    {
        // 清单记上了,删文件之前断了:本地那份已是多余的。
        (string cipher, byte[] key) = EncryptWithLegacyKeyFile("hunter2");
        var store = new FakeKeychain();
        _ = Protector(store).GetKeyStatus();
        WriteLegacyKeyFile(key); // 把那份文件放回去,等于删之前断掉

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.IsFalse(File.Exists(_keyFile));
        Assert.ContainsSingle(ReferencedIds(), "编号不重复登记");
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    public void Migration_InterruptedWhileDeleting_DoesNotLeaveAZeroKeyBehind()
    {
        // 改名成中转名之后、清零删除之前断了:中转文件下次启动收尾,绝不能被当成一把密钥读回来。
        (string cipher, _) = EncryptWithLegacyKeyFile("hunter2");
        var store = new FakeKeychain();
        _ = Protector(store).GetKeyStatus();
        File.WriteAllBytes(_keyFile + ".deleting", new byte[MasterKeyResolver.KeySize]);

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.IsFalse(File.Exists(_keyFile + ".deleting"));
        Assert.ContainsSingle(store.Entries, "全零的中转文件没有被当成新密钥迁进去");
    }

    [TestMethod]
    public void Migration_WhenTheKeychainRefusesTheWrite_KeepsTheKeyFileAndRetriesNextStart()
    {
        (string cipher, _) = EncryptWithLegacyKeyFile("hunter2");
        byte[] before = File.ReadAllBytes(_keyFile);
        var store = new FakeKeychain { SaveError = "OSStatus -128 (errSecUserCanceled: access was denied in the prompt)" };

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher), "迁移失败照旧能用");
        Assert.AreSequenceEqual(before, File.ReadAllBytes(_keyFile), "失败时密钥文件原样不动");
        Assert.IsFalse(File.Exists(ReferenceFile));
        Assert.IsFalse(protector.GetKeyStatus().Migrated);
        Assert.IsNotNull(protector.GetKeyStatus().KeychainError);

        store.SaveError = null;
        AesSecretProtector retried = Protector(store);
        Assert.IsTrue(retried.GetKeyStatus().Migrated, "下一次启动再试");
        Assert.AreEqual("hunter2", retried.Unprotect(cipher));
        Assert.IsFalse(File.Exists(_keyFile));
    }

    [TestMethod]
    public void Migration_WhenTheEntryCannotBeReadBack_KeepsTheKeyFile()
    {
        // 存进去了却读不回来(存到了锁着的集合、访问控制不认我们):不算迁移成功,文件不能删。
        (string cipher, _) = EncryptWithLegacyKeyFile("hunter2");
        byte[] before = File.ReadAllBytes(_keyFile);
        var store = new FakeKeychain { DropSaves = true };

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.AreSequenceEqual(before, File.ReadAllBytes(_keyFile));
        Assert.IsFalse(File.Exists(ReferenceFile));
        Assert.IsFalse(protector.GetKeyStatus().Migrated);
    }

    [TestMethod]
    public void Migration_WhenTheReferenceCannotBeWritten_KeepsTheKeyFile()
    {
        (string cipher, _) = EncryptWithLegacyKeyFile("hunter2");
        byte[] before = File.ReadAllBytes(_keyFile);
        // 清单的位置被一个同名目录占着:写不进去。
        Directory.CreateDirectory(ReferenceFile);
        var store = new FakeKeychain();

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.AreSequenceEqual(before, File.ReadAllBytes(_keyFile), "清单没记上,本地这份就不是多余的");
        Assert.IsFalse(protector.GetKeyStatus().Migrated);
    }

    // ———— 迁移之后:密钥链暂时不可用 / 条目不在 ————

    [TestMethod]
    public void KeychainUnavailableAfterMigration_LosesNothing_AndMergesTheStopGapKeyLater()
    {
        var store = new FakeKeychain();
        string oldCipher = Protector(store).Protect("old-password")!;
        string oldId = Assert.ContainsSingle(ReferencedIds());

        // 某次启动没有 D-Bus / 用户取消了解锁框。
        store.Unavailable = "org.freedesktop.secrets was not provided by any .service files";
        AesSecretProtector degraded = Protector(store);
        MasterKeyStatus status = degraded.GetKeyStatus();
        Assert.IsFalse(degraded.TryUnprotect(oldCipher, out _), "这一轮旧密文解不开 ……");
        Assert.AreEqual(oldCipher, degraded.Unprotect(oldCipher), "…… 但原样交回,存回去也不会丢");
        Assert.AreSequenceEqual([oldId], [.. status.UnavailableKeyIds]);
        Assert.IsTrue(status.HasUnreadableSecrets);
        string newCipher = degraded.Protect("new-password")!;
        Assert.IsTrue(File.Exists(_keyFile), "新密码先用本地临时密钥加密");
        Assert.AreSequenceEqual([oldId], ReferencedIds(), "清单里的旧编号原样留着");

        // 密钥链回来了。
        store.Unavailable = null;
        AesSecretProtector recovered = Protector(store);
        Assert.AreEqual("old-password", recovered.Unprotect(oldCipher));
        Assert.AreEqual("new-password", recovered.Unprotect(newCipher), "临时密钥并进了密钥链");
        Assert.IsFalse(File.Exists(_keyFile));
        Assert.HasCount(2, ReferencedIds());
        Assert.HasCount(2, store.Entries);
        Assert.IsFalse(recovered.GetKeyStatus().HasUnreadableSecrets);
    }

    [TestMethod]
    public void KeychainUnavailable_StopsAskingAfterTheFirstFailure()
    {
        // 每问一次都可能再弹一次解锁框:第一次用不了之后,其余条目不再去问。
        File.WriteAllLines(ReferenceFile, [.. Enumerable.Range(0, 3).Select(static i => "key=" + new string((char)('a' + i), 32))]);
        var store = new FakeKeychain { Unavailable = "locked" };

        MasterKeyStatus status = Protector(store).GetKeyStatus();

        Assert.AreEqual(1, store.LoadCount);
        Assert.HasCount(3, status.UnavailableKeyIds, "没去问的条目同样记为「这次没取到」");
    }

    [TestMethod]
    public void CopiedDataDirectory_WithoutTheKeychainEntry_KeepsTheOldReference_AndStartsANewKey()
    {
        var original = new FakeKeychain();
        string oldCipher = Protector(original).Protect("old-password")!;
        string oldId = Assert.ContainsSingle(ReferencedIds());

        // 数据目录原样拷到另一台电脑:那边的密钥链里没有这一条。
        var elsewhere = new FakeKeychain();
        AesSecretProtector protector = Protector(elsewhere);
        MasterKeyStatus status = protector.GetKeyStatus();

        Assert.AreSequenceEqual([oldId], [.. status.MissingKeyIds], "如实报告「找不到」,界面据此提示重新输入");
        Assert.IsFalse(protector.TryUnprotect(oldCipher, out _), "拷走的数据目录在别处解不开 —— 这正是这一步的目的");
        Assert.AreEqual("fresh", Protector(elsewhere).Unprotect(protector.Protect("fresh")), "新保存的密码照常可用");
        Assert.IsFalse(File.Exists(_keyFile), "新密钥进了那边的密钥链,不落明文文件");
        Assert.Contains(oldId, ReferencedIds(), "旧编号绝不自动丢弃:条目恢复后还能解");

        // 用户把条目恢复回来(或者把数据目录拷回原来的电脑)。
        elsewhere.Entries[oldId] = original.Entries[oldId];
        Assert.AreEqual("old-password", Protector(elsewhere).Unprotect(oldCipher));
    }

    [TestMethod]
    public void ReferenceOnAPlatformWithoutAKeychain_ReportsTheKeysAsMissing()
    {
        // 数据目录从 macOS / Linux 拷到了 Windows:这边没有密钥链后端。
        string oldCipher = Protector(new FakeKeychain()).Protect("old-password")!;
        string oldId = Assert.ContainsSingle(ReferencedIds());

        AesSecretProtector protector = Protector(store: null);

        Assert.AreSequenceEqual([oldId], [.. protector.GetKeyStatus().MissingKeyIds]);
        Assert.IsFalse(protector.TryUnprotect(oldCipher, out _));
        Assert.AreEqual("fresh", protector.Unprotect(protector.Protect("fresh")));
        Assert.IsTrue(File.Exists(_keyFile));
    }

    [TestMethod]
    public void EntryThatDoesNotMatchItsId_IsIgnoredAndNotOverwritten()
    {
        var store = new FakeKeychain();
        _ = Protector(store).GetKeyStatus();
        string id = Assert.ContainsSingle(ReferencedIds());
        byte[] tampered = RandomNumberGenerator.GetBytes(MasterKeyResolver.KeySize);
        store.Entries[id] = tampered;

        MasterKeyStatus status = Protector(store).GetKeyStatus();

        Assert.Contains(id, [.. status.MissingKeyIds], "内容对不上编号,不当成那把密钥用");
        Assert.AreSequenceEqual(tampered, store.Entries[id], "也不动它");
    }

    [TestMethod]
    public void CorruptReferenceFile_IsKeptAsideNotOverwritten()
    {
        File.WriteAllText(ReferenceFile, "garbage\n");
        var store = new FakeKeychain();

        _ = Protector(store).GetKeyStatus();

        string aside = Assert.ContainsSingle(Directory.GetFiles(_root, "secret.key.ref.unreadable-*"));
        Assert.AreEqual("garbage\n", File.ReadAllText(aside));
        Assert.ContainsSingle(ReferencedIds());
    }

    [TestMethod]
    public void UnreadableReferenceFile_IsNeverOverwritten()
    {
        string oldCipher = Protector(new FakeKeychain()).Protect("old-password")!;
        string before = File.ReadAllText(ReferenceFile);

        using (new FileStream(ReferenceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AesSecretProtector protector = Protector(new FakeKeychain());
            Assert.AreEqual("fresh", protector.Unprotect(protector.Protect("fresh")), "读不出清单也照常能用");
        }

        Assert.AreEqual(before, File.ReadAllText(ReferenceFile), "读不出来的那份清单不能被新清单盖掉");
        Assert.IsFalse(Protector(new FakeKeychain()).TryUnprotect(oldCipher, out _));
    }

    // ———— 本地密钥文件(Windows:真 DPAPI) ————

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Windows_KeyFileIsWrappedWithRealDpapi()
    {
        AesSecretProtector protector = new(_keyFile);
        string cipher = protector.Protect("hunter2")!;

        byte[] stored = File.ReadAllBytes(_keyFile);
        Assert.AreNotEqual(MasterKeyResolver.KeySize, stored.Length, "落盘的是 DPAPI 密文,不是 32 字节明文");
        Assert.HasCount(MasterKeyResolver.KeySize, UnwrapDpapi(stored));
        Assert.AreEqual(MasterKeyProtection.UserBoundFile, protector.GetKeyStatus().Protection);
        Assert.AreEqual("hunter2", new AesSecretProtector(_keyFile).Unprotect(cipher));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Windows_LegacyPlainKeyFile_IsRewrappedWithDpapi()
    {
        byte[] key = RandomNumberGenerator.GetBytes(MasterKeyResolver.KeySize);
        File.WriteAllBytes(_keyFile, key);

        _ = new AesSecretProtector(_keyFile).GetKeyStatus();

        Assert.AreSequenceEqual(key, UnwrapDpapi(File.ReadAllBytes(_keyFile)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Windows_DpapiBlobThatCannotBeUnwrapped_IsKeptAsideAndReported()
    {
        // 别的账户 / 别的电脑包裹的密钥:这里用带附加熵的真 DPAPI 密文模拟「解不开」。
        byte[] foreign = ProtectedData.Protect(RandomNumberGenerator.GetBytes(MasterKeyResolver.KeySize),
            "another account"u8.ToArray(), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyFile, foreign);

        AesSecretProtector protector = new(_keyFile);
        MasterKeyStatus status = protector.GetKeyStatus();

        Assert.IsNotNull(status.ReplacedKeyFile, "密钥被换过要如实报告");
        Assert.AreSequenceEqual(foreign, File.ReadAllBytes(status.ReplacedKeyFile), "解不开的那份原样保留");
        Assert.IsTrue(status.HasUnreadableSecrets);
        Assert.AreEqual("x", protector.Unprotect(protector.Protect("x")), "应用照常起得来");
    }

    // ———— 杂项 ————

    [TestMethod]
    public void KeyId_IsDeterministic_AndDoesNotRevealTheKey()
    {
        byte[] key = RandomNumberGenerator.GetBytes(MasterKeyResolver.KeySize);
        string id = MasterKeyResolver.KeyIdOf(key);

        Assert.AreEqual(id, MasterKeyResolver.KeyIdOf(key.ToArray()));
        Assert.MatchesRegex("^[0-9a-f]{32}$", id);
        Assert.AreNotEqual(id, MasterKeyResolver.KeyIdOf(RandomNumberGenerator.GetBytes(MasterKeyResolver.KeySize)));
        Assert.DoesNotContain(id, Convert.ToHexStringLower(key));
    }

    [TestMethod]
    public void TryUnprotect_TellsDecryptedFromUndecryptable()
    {
        AesSecretProtector protector = new(_keyFile);

        Assert.IsTrue(protector.TryUnprotect(protector.Protect("hunter2"), out string? plain));
        Assert.AreEqual("hunter2", plain);
        Assert.IsTrue(protector.TryUnprotect("legacy-plaintext", out plain), "无前缀的历史明文算解开了");
        Assert.AreEqual("legacy-plaintext", plain);
        Assert.IsFalse(protector.TryUnprotect("enc1:AAAA", out plain));
        Assert.IsNull(plain);
        Assert.IsFalse(protector.TryUnprotect("enc1:not-base64!", out _));
        Assert.AreEqual("enc1:AAAA", protector.Unprotect("enc1:AAAA"), "Unprotect 的历史契约不变:解不开原样交回");
    }

    [TestMethod]
    public void SecretService_WithoutLibsecret_IsUnavailableNotACrash()
    {
        var store = new SecretServiceMasterKeyStore(_root, "libvelashell-does-not-exist.so.0");

        MasterKeyStoreResult load = store.Load(new string('a', 32));
        MasterKeyStoreResult save = store.Save(new string('a', 32), new byte[MasterKeyResolver.KeySize]);

        Assert.AreEqual(MasterKeyStoreStatus.Unavailable, load.Status);
        Assert.AreEqual(MasterKeyStoreStatus.Unavailable, save.Status);
        Assert.IsFalse(string.IsNullOrEmpty(load.Error), "用不了的原因要写进日志");
    }

    [TestMethod]
    public void SecretService_FallsBackToTheKeyFile_WhenLibsecretIsMissing()
    {
        // 整条链:没装 libsecret 的机器上照旧落到本地密钥文件,并说明原因。
        AesSecretProtector protector = Protector(new SecretServiceMasterKeyStore(_root, "libvelashell-does-not-exist.so.0"));

        Assert.AreEqual("hunter2", protector.Unprotect(protector.Protect("hunter2")));
        Assert.IsTrue(File.Exists(_keyFile));
        Assert.IsNotNull(protector.GetKeyStatus().KeychainError);
    }

    [TestMethod]
    public void SecretService_StructLayout_MatchesLibsecretOn64Bit()
    {
        if (!Environment.Is64BitProcess)
        {
            Assert.Inconclusive("[SKIP] 布局数字按 64 位写的");
        }
        Assert.AreEqual((592, 16, 528, 16, 8), SecretServiceMasterKeyStore.Layout());
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.OSX)]
    public void MacKeychain_OnOtherPlatforms_IsUnavailable() => Assert.AreEqual(MasterKeyStoreStatus.Unavailable, new MacKeychainMasterKeyStore().Load(new string('a', 32)).Status);

    [TestMethod]
    public void DisabledByEnvironment_ReportsTheReason()
    {
        string? previous = Environment.GetEnvironmentVariable(MasterKeyStores.DisableEnvironmentVariable);
        Environment.SetEnvironmentVariable(MasterKeyStores.DisableEnvironmentVariable, "1");
        try
        {
            IMasterKeyStore? store = MasterKeyStores.ForCurrentPlatform(_root);
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                MasterKeyStoreResult result = store!.Load(new string('a', 32));
                Assert.AreEqual(MasterKeyStoreStatus.Unavailable, result.Status);
                Assert.Contains(MasterKeyStores.DisableEnvironmentVariable, result.Error!);
            }
            else
            {
                Assert.IsNull(store, "Windows 上密钥文件本来就以 DPAPI 包裹,没有密钥链后端");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MasterKeyStores.DisableEnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// 真密钥链往返:会往当前用户的钥匙串 / keyring 里写一条再删掉,所以默认不跑 ——
    /// 在 macOS / Linux 桌面上设 <c>VELASHELL_KEYCHAIN_TESTS=1</c> 手动跑(keyring 锁着时会弹解锁框)。
    /// </summary>
    [TestMethod]
    [TestCategory("SystemKeychain")]
    public void SystemKeychain_RoundTrip()
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_KEYCHAIN_TESTS") != "1")
        {
            Assert.Inconclusive("[SKIP] 设 VELASHELL_KEYCHAIN_TESTS=1 才往真密钥链里写");
        }
        byte[] key = RandomNumberGenerator.GetBytes(MasterKeyResolver.KeySize);
        string id = MasterKeyResolver.KeyIdOf(key);
        if (OperatingSystem.IsMacOS())
        {
            var store = new MacKeychainMasterKeyStore();
            try
            {
                AssertRoundTrip(store, id, key);
            }
            finally
            {
                _ = MacKeychainMasterKeyStore.Delete(id);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var store = new SecretServiceMasterKeyStore(_root);
            try
            {
                AssertRoundTrip(store, id, key);
            }
            finally
            {
                _ = store.Delete(id);
            }
        }
        else
        {
            Assert.Inconclusive("[SKIP] Windows 没有密钥链后端(密钥文件以 DPAPI 包裹,上面的用例已覆盖)");
        }

        static void AssertRoundTrip(IMasterKeyStore store, string id, byte[] key)
        {
            Assert.AreEqual(MasterKeyStoreStatus.NotFound, store.Load(id).Status, store.Load(id).Error);
            Assert.AreEqual(MasterKeyStoreStatus.Ok, store.Save(id, key).Status);
            Assert.AreEqual(MasterKeyStoreStatus.Ok, store.Save(id, key).Status, "同编号再存一次是覆盖,不是报错");
            MasterKeyStoreResult loaded = store.Load(id);
            Assert.AreEqual(MasterKeyStoreStatus.Ok, loaded.Status, loaded.Error);
            Assert.AreSequenceEqual(key, loaded.Key);
        }
    }

    private void WriteLegacyKeyFile(byte[] key)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(_keyFile, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
            return;
        }
        File.WriteAllBytes(_keyFile, key);
    }

    /// <summary>内存里的假密钥链:可以让它「用不了」、拒绝写入、或者写了读不回来。</summary>
    private sealed class FakeKeychain : IMasterKeyStore
    {
        public Dictionary<string, byte[]> Entries { get; } = [];

        /// <summary>非 null 时整个密钥链用不了(读写都是)。</summary>
        public string? Unavailable { get; set; }

        /// <summary>非 null 时只拒绝写入。</summary>
        public string? SaveError { get; set; }

        /// <summary>写入报成功却没存下(读回来找不到)。</summary>
        public bool DropSaves { get; init; }

        public int SaveCount { get; private set; }

        public int LoadCount { get; set; }

        public string Name => "Fake Keychain";

        public MasterKeyStoreResult Load(string keyId)
        {
            LoadCount++;
            if (Unavailable is { } error)
            {
                return MasterKeyStoreResult.Unavailable(error);
            }
            return Entries.TryGetValue(keyId, out byte[]? key) ? MasterKeyStoreResult.Found([.. key]) : MasterKeyStoreResult.Missing;
        }

        public MasterKeyStoreResult Save(string keyId, ReadOnlySpan<byte> key)
        {
            if ((Unavailable ?? SaveError) is { } error)
            {
                return MasterKeyStoreResult.Unavailable(error);
            }
            SaveCount++;
            if (!DropSaves)
            {
                Entries[keyId] = key.ToArray();
            }
            return MasterKeyStoreResult.Saved;
        }
    }
}
