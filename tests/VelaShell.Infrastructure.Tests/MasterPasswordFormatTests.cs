using System.Security.Cryptography;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Tests.Fixtures;
using static VelaShell.Infrastructure.Tests.MasterPasswordTestKit;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 主密码第二步的格式与解包:KDF 头、<c>VSMP1</c> 包裹、恢复码的往返;锁着时的语义(加不了密、解不开、不生新密钥);
/// 解锁、输错的退避、锁定;降级到不认 <c>VSMP1</c> 的旧版本不丢数据。密钥链用内存假实现,Windows 上 DPAPI 用真的。
/// </summary>
[TestClass]
public sealed class MasterPasswordFormatTests
{
    private const string Password = "correct horse battery staple";

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

    private string HeaderFile => _keyFile + MasterKeyResolver.HeaderSuffix;

    private MasterKeyResolver Resolver(IMasterKeyStore? store) => new(_keyFile, store);

    private AesSecretProtector Protector(IMasterKeyStore? store, TimeProvider? time = null) => new(Resolver(store), time);

    // ———— 格式 ————

    [TestMethod]
    public void Header_RoundTrips_AndRejectsDamagedOrOutOfRangeValues()
    {
        MasterPasswordHeader header = MasterPasswordHeader.CreateNew(RandomNumberGenerator.GetBytes(16), MasterPasswordHeaderState.Enabling);

        MasterPasswordHeader? read = MasterPasswordHeader.Parse(header.Serialize());

        Assert.IsNotNull(read);
        Assert.AreEqual(64 * 1024, read.MemoryKiB, "出厂参数 m = 64 MiB(维护者 2026-10-31 定)");
        Assert.AreEqual(3, read.Iterations);
        Assert.AreEqual(1, read.Parallelism);
        Assert.AreSequenceEqual(header.Salt, read.Salt);
        Assert.AreSequenceEqual(header.RecoverySalt!, read.RecoverySalt!);
        Assert.AreEqual(MasterPasswordHeaderState.Enabling, read.State);
        Assert.IsTrue(read.UsesCurrentParameters);
        string text = header.Serialize();
        Assert.IsNull(MasterPasswordHeader.Parse(text.Replace("m=65536", "m=999999999", StringComparison.Ordinal)), "参数被改大到能拖死解锁的程度:当成读不出来");
        Assert.IsNull(MasterPasswordHeader.Parse(text.Replace("kdf=argon2id", "kdf=pbkdf2", StringComparison.Ordinal)));
        Assert.IsNull(MasterPasswordHeader.Parse(text.Replace("version=1", "version=2", StringComparison.Ordinal)));
        Assert.IsNull(MasterPasswordHeader.Parse(text + "salt=AAAA\n"), "重复的键");
        Assert.IsNull(MasterPasswordHeader.Parse(text[..(text.Length / 2)]), "被截断");
        Assert.DoesNotContain("enc1:", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Envelope_RoundTrips_AndTheTagIsThePasswordCheck()
    {
        byte[] dek = RandomNumberGenerator.GetBytes(32);
        MasterPasswordHeader header = MasterPasswordHeader.CreateNew(RandomNumberGenerator.GetBytes(16), MasterPasswordHeaderState.Active);
        byte[] kek = MasterPasswordKdf.DerivePasswordKey(Utf8(Password), header);
        byte[] wrongKek = MasterPasswordKdf.DerivePasswordKey(Utf8(Password + "!"), header);
        WrapRecord record = MasterKeyEnvelope.Wrap(WrapKind.Password, header.PasswordWrapId, kek, dek);
        byte[] bytes = new MasterKeyEnvelope([record]).ToBytes();

        Assert.IsTrue(MasterKeyEnvelope.TryParse(bytes, out MasterKeyEnvelope? parsed));
        Assert.AreEqual(5 + 69, bytes.Length, "VSMP1 ‖ 一条记录");
        Assert.IsTrue(MasterKeyEnvelope.TryUnwrap(parsed!.Records[0], kek, out byte[] unwrapped));
        Assert.AreSequenceEqual(dek, unwrapped);
        Assert.IsFalse(MasterKeyEnvelope.TryUnwrap(parsed.Records[0], wrongKek, out _), "主密码不对:GCM 标签对不上,不另存校验值");
        WrapRecord asRecovery = parsed.Records[0] with { Kind = WrapKind.Recovery };
        Assert.IsFalse(MasterKeyEnvelope.TryUnwrap(asRecovery, kek, out _), "记录挪不到别的种类下去用(附加认证数据带着种类)");
        Assert.IsFalse(MasterKeyEnvelope.TryParse(bytes[..^1], out _), "长度不对");
        Assert.IsFalse(MasterKeyEnvelope.TryParse(dek, out _), "32 字节的明文密钥不是包裹");
        Assert.AreNotEqual(MasterKeyResolver.KeyIdOf(dek), Convert.ToHexStringLower(bytes.AsSpan(0, 16)));
    }

    [TestMethod]
    public void RecoveryCode_RoundTrips_ToleratesSloppyInput_AndCatchesTypos()
    {
        (string display, byte[] entropy) = RecoveryCode.Generate();

        Assert.AreEqual(7, display.Split('-').Length, "28 个字符、每 4 个一组");
        Assert.IsTrue(display.Split('-').All(static g => g.Length == 4));
        Assert.IsTrue(RecoveryCode.TryParse(display, out byte[] parsed));
        Assert.AreSequenceEqual(entropy, parsed);
        string sloppy = " " + display.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal).Replace('1', 'l').Replace('0', 'o') + " ";
        Assert.IsTrue(RecoveryCode.TryParse(sloppy, out byte[] again), "不分大小写,空格当分隔,l / o 当 1 / 0");
        Assert.AreSequenceEqual(entropy, again);
        char[] typo = display.ToCharArray();
        typo[0] = typo[0] == 'A' ? 'B' : 'A';
        Assert.IsFalse(RecoveryCode.TryParse(new string(typo), out _), "抄错一位:校验不通过");
        Assert.IsFalse(RecoveryCode.TryParse(display[..^1], out _), "少一位");
        Assert.IsFalse(RecoveryCode.TryParse("", out _));
    }

    // ———— 锁着时 ————

    [TestMethod]
    public void Locked_ProtectRefuses_TryUnprotectFails_AndNoNewKeyIsCreated()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        WrapEverything(Resolver(store), Password, withRecovery: false);
        int entries = store.Entries.Count;
        int saves = store.SaveCount;

        AesSecretProtector locked = Protector(store);

        Assert.IsTrue(locked.IsLocked);
        Assert.IsTrue(locked.GetKeyStatus().MasterPasswordEnabled, "主密码开没开看包裹本身");
        Assert.IsFalse(locked.TryUnprotect(cipher, out string? plain));
        Assert.IsNull(plain);
        Assert.AreEqual(cipher, locked.Unprotect(cipher), "解不开时原样交回密文(存回去不丢)");
        Assert.ThrowsExactly<SecretsLockedException>(() => locked.Protect("new password"));
        Assert.AreEqual(cipher, locked.Protect(cipher), "已经是密文的值照旧原样交回(幂等,锁着也不抛)");
        Assert.IsNull(locked.Protect(null));
        Assert.AreEqual(entries, store.Entries.Count, "锁着时不另起主密码管不到的新密钥");
        Assert.AreEqual(saves, store.SaveCount);
        Assert.IsFalse(File.Exists(_keyFile), "也不回落到本地密钥文件");
        Assert.IsTrue(store.Entries.Values.All(static v => MasterKeyEnvelope.IsEnvelope(v)));
    }

    [TestMethod]
    public void KeychainUnavailable_WhileProtected_StaysLocked_AndCreatesNoStopGapKey()
    {
        var store = new MemoryKeychain();
        _ = Protector(store).Protect("hunter2");
        WrapEverything(Resolver(store), Password, withRecovery: false);
        store.Unavailable = "no session bus";

        AesSecretProtector protector = Protector(store);

        Assert.IsTrue(protector.IsLocked);
        Assert.AreEqual(MasterPasswordProblem.KeysUnavailable, protector.GetKeyStatus().MasterPasswordProblem);
        Assert.IsFalse(File.Exists(_keyFile), "第一步在密钥链用不了时会另起一把本地密钥;主密码开着时不许");
        Assert.AreEqual(VaultUnlockStatus.NothingToUnlock, protector.Vault.Unlock(Utf8(Password)).Status, "密钥没取到:不算输错");
        Assert.AreEqual(TimeSpan.Zero, protector.Vault.RetryAfter);
    }

    [TestMethod]
    public void HeaderMissing_WithWrappedKeys_StaysLocked_AndKeepsEverything()
    {
        var store = new MemoryKeychain();
        _ = Protector(store).Protect("hunter2");
        WrapEverything(Resolver(store), Password, withRecovery: false);
        Dictionary<string, byte[]> before = store.Entries.ToDictionary(static e => e.Key, static e => e.Value.ToArray());
        File.Delete(HeaderFile);

        AesSecretProtector protector = Protector(store);

        Assert.IsTrue(protector.IsLocked);
        Assert.AreEqual(MasterPasswordProblem.HeaderMissing, protector.GetKeyStatus().MasterPasswordProblem);
        Assert.AreEqual(VaultUnlockStatus.NothingToUnlock, protector.Vault.Unlock(Utf8(Password)).Status);
        Assert.ThrowsExactly<SecretsLockedException>(() => protector.Protect("x"));
        Assert.AreEqual(before.Count, store.Entries.Count);
        foreach ((string id, byte[] value) in before)
        {
            Assert.AreSequenceEqual(value, store.Entries[id], "包裹过的条目一字不动");
        }
    }

    // ———— 解锁 ————

    [TestMethod]
    public void Unlock_WithThePassword_ReadsOldSecrets_AndEncryptsAgain()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        WrapEverything(Resolver(store), Password, withRecovery: false);
        AesSecretProtector protector = Protector(store);

        VaultUnlockOutcome outcome = protector.Vault.Unlock(Utf8(Password));

        Assert.AreEqual(VaultUnlockStatus.Unlocked, outcome.Status);
        Assert.IsFalse(protector.IsLocked);
        Assert.AreEqual("hunter2", protector.Unprotect(cipher), "已存的 enc1: 密文一字不改,解锁后照常解开");
        string fresh = protector.Protect("new")!;
        Assert.AreEqual("new", protector.Unprotect(fresh));
        Assert.AreEqual(VaultUnlockStatus.AlreadyUnlocked, protector.Vault.Unlock(Utf8(Password)).Status);
        Assert.IsTrue(store.Entries.Values.All(static v => MasterKeyEnvelope.IsEnvelope(v)), "解锁不把包裹写回明文");
    }

    [TestMethod]
    public void WrongPassword_BacksOff_1_2_4_UpTo30Seconds_WithoutLockingOut()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        WrapEverything(Resolver(store), Password, withRecovery: false);
        var clock = new ManualClock();
        AesSecretProtector protector = Protector(store, clock);

        List<double> waits = [];
        for (int i = 0; i < 7; i++)
        {
            VaultUnlockOutcome wrong = protector.Vault.Unlock(Utf8("wrong " + i));
            Assert.AreEqual(VaultUnlockStatus.WrongSecret, wrong.Status);
            waits.Add(wrong.RetryAfter.TotalSeconds);
            Assert.AreEqual(VaultUnlockStatus.Throttled, protector.Vault.Unlock(Utf8(Password)).Status, "等待没过之前,连对的也不试");
            clock.Advance(wrong.RetryAfter);
        }

        Assert.AreSequenceEqual(new double[] { 1, 2, 4, 8, 16, 30, 30 }, waits.ToArray());
        Assert.AreEqual(VaultUnlockStatus.Unlocked, protector.Vault.Unlock(Utf8(Password)).Status, "不锁死:等过之后输对照常解开");
        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.AreEqual(TimeSpan.Zero, protector.Vault.RetryAfter, "解开之后计数清零");
    }

    [TestMethod]
    public void RecoveryCode_AlsoUnlocks_AndATypoIsNotCountedAsAFailure()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        string code = WrapEverything(Resolver(store), Password, withRecovery: true)!;
        AesSecretProtector protector = Protector(store);

        Assert.IsTrue(protector.Vault.HasRecoveryCode);
        Assert.AreEqual(VaultUnlockStatus.MalformedRecoveryCode, protector.Vault.UnlockWithRecoveryCode(code[..^1] + "?").Status);
        Assert.AreEqual(TimeSpan.Zero, protector.Vault.RetryAfter, "抄错的恢复码不算一次输错");
        (string other, _) = RecoveryCode.Generate();
        Assert.AreEqual(VaultUnlockStatus.WrongSecret, protector.Vault.UnlockWithRecoveryCode(other).Status);
        Assert.AreEqual(VaultUnlockStatus.Throttled, protector.Vault.UnlockWithRecoveryCode(code).Status);
    }

    [TestMethod]
    public void RecoveryCode_UnlocksTheSameKeys()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        string code = WrapEverything(Resolver(store), Password, withRecovery: true)!;
        AesSecretProtector protector = Protector(store);

        Assert.AreEqual(VaultUnlockStatus.Unlocked, protector.Vault.UnlockWithRecoveryCode(code.ToLowerInvariant()).Status);

        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.IsTrue(protector.Vault.UnlockedWithRecoveryCode, "界面据此要求设新的主密码");
    }

    [TestMethod]
    public void NoRecoveryCode_WhenSkipped()
    {
        var store = new MemoryKeychain();
        _ = Protector(store).Protect("hunter2");
        WrapEverything(Resolver(store), Password, withRecovery: false);
        AesSecretProtector protector = Protector(store);
        (string code, _) = RecoveryCode.Generate();

        Assert.IsFalse(protector.Vault.HasRecoveryCode);
        Assert.AreEqual(VaultUnlockStatus.NoRecoveryCode, protector.Vault.UnlockWithRecoveryCode(code).Status);
    }

    [TestMethod]
    public void Lock_ZeroesTheKeys_AndLocksAgain()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        WrapEverything(Resolver(store), Password, withRecovery: false);
        AesSecretProtector protector = Protector(store);
        Assert.AreEqual(VaultUnlockStatus.Unlocked, protector.Vault.Unlock(Utf8(Password)).Status);
        byte[] held = protector.Vault.UseKeys(static ring => ring!.Current);

        Assert.IsTrue(protector.Vault.Lock());

        Assert.IsTrue(protector.IsLocked);
        Assert.IsTrue(held.All(static b => b == 0), "锁定清零内存里的密钥");
        Assert.IsFalse(protector.TryUnprotect(cipher, out _));
        Assert.ThrowsExactly<SecretsLockedException>(() => protector.Protect("x"));
        Assert.IsFalse(protector.Vault.Lock(), "本来就锁着");
        Assert.AreEqual(VaultUnlockStatus.Unlocked, protector.Vault.Unlock(Utf8(Password)).Status);
        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
    }

    [TestMethod]
    public void WithoutAMasterPassword_NothingChanges()
    {
        var store = new MemoryKeychain();
        AesSecretProtector protector = Protector(store);
        string cipher = protector.Protect("hunter2")!;

        Assert.IsFalse(protector.IsLocked);
        Assert.IsFalse(protector.GetKeyStatus().MasterPasswordEnabled);
        Assert.IsFalse(protector.Vault.Lock(), "没开主密码时锁定什么都不做");
        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
        Assert.AreEqual(VaultUnlockStatus.NotEnabled, protector.Vault.Unlock(Utf8(Password)).Status);
        Assert.IsFalse(File.Exists(HeaderFile));
    }

    [TestMethod]
    public void KeychainEntryThatUnwrapsToAnotherKey_IsNotUsed()
    {
        var store = new MemoryKeychain();
        _ = Protector(store).Protect("hunter2");
        WrapEverything(Resolver(store), Password, withRecovery: false);
        string id = store.Entries.Keys.Single();
        // 有人把另一把密钥包好了塞进这个条目名下(编号对不上):解得开也不用。
        MasterPasswordHeader header = MasterPasswordHeader.Parse(File.ReadAllText(HeaderFile))!;
        byte[] kek = MasterPasswordKdf.DerivePasswordKey(Utf8(Password), header);
        store.Entries[id] = new MasterKeyEnvelope([MasterKeyEnvelope.Wrap(WrapKind.Password, header.PasswordWrapId, kek, RandomNumberGenerator.GetBytes(32))]).ToBytes();

        AesSecretProtector protector = Protector(store);

        Assert.AreEqual(VaultUnlockStatus.WrongSecret, protector.Vault.Unlock(Utf8(Password)).Status);
        Assert.IsTrue(protector.IsLocked);
    }

    // ———— 中途断掉 ————

    [TestMethod]
    public void InterruptedEnable_BeforeAnyKeyWasWrapped_IsUndone()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        Assert.IsTrue(Resolver(store).TryWriteHeader(MasterPasswordHeader.CreateNew(null, MasterPasswordHeaderState.Enabling)));

        AesSecretProtector protector = Protector(store);

        Assert.IsFalse(protector.IsLocked, "开启连一把都没包就断了:当成没开");
        Assert.IsFalse(File.Exists(HeaderFile), "没用的头删掉");
        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
    }

    [TestMethod]
    public void InterruptedEnable_AfterSomeKeysWereWrapped_FinishesAtTheNextUnlock()
    {
        var store = new MemoryKeychain();
        string inKeychain = Protector(store).Protect("from the keychain")!;
        store.Unavailable = "locked keyring";
        string stopGap = Protector(store).Protect("from the stop-gap key")!; // 密钥链用不了的那一轮另起的本地那把
        store.Unavailable = null;
        _ = Protector(store).GetKeyStatus(); // 密钥链回来:本地那把并进去
        Assert.AreEqual(2, store.Entries.Count);
        WrapEverything(Resolver(store), Password, withRecovery: false, MasterPasswordHeaderState.Enabling);
        // 只包上了第一条就断了:第二条还是明文。
        string plainId = store.Entries.Keys.Last();
        MasterPasswordHeader header = MasterPasswordHeader.Parse(File.ReadAllText(HeaderFile))!;
        byte[] kek = MasterPasswordKdf.DerivePasswordKey(Utf8(Password), header);
        Assert.IsTrue(MasterKeyEnvelope.TryParse(store.Entries[plainId], out MasterKeyEnvelope? envelope));
        Assert.IsTrue(MasterKeyEnvelope.TryUnwrap(envelope!.Records[0], kek, out byte[] dek));
        store.Entries[plainId] = dek;

        AesSecretProtector protector = Protector(store);
        Assert.IsTrue(protector.IsLocked, "有一把已经包上了:按开着处理(明文那把锁着时也不用)");
        Assert.AreEqual(VaultUnlockStatus.Unlocked, protector.Vault.Unlock(Utf8(Password)).Status);

        Assert.AreEqual("from the stop-gap key", protector.Unprotect(stopGap));
        Assert.AreEqual("from the keychain", protector.Unprotect(inKeychain));
        Assert.IsTrue(MasterKeyEnvelope.IsEnvelope(store.Entries[plainId]), "解锁后把明文那把也包上");
        Assert.AreEqual(MasterPasswordHeaderState.Active, MasterPasswordHeader.Parse(File.ReadAllText(HeaderFile))!.State, "开启标记为完成");
    }

    [TestMethod]
    public void InterruptedDisable_FinishesAtTheNextUnlock()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        WrapEverything(Resolver(store), Password, withRecovery: false, MasterPasswordHeaderState.Disabling);

        AesSecretProtector protector = Protector(store);
        Assert.IsTrue(protector.IsLocked);
        Assert.AreEqual(VaultUnlockStatus.Unlocked, protector.Vault.Unlock(Utf8(Password)).Status);

        Assert.IsFalse(protector.GetKeyStatus().MasterPasswordEnabled, "关闭做到一半:解开之后接着关完");
        Assert.IsFalse(File.Exists(HeaderFile));
        Assert.AreEqual(MasterKeyResolver.KeySize, store.Entries.Values.Single().Length, "写回第一步的明文形态");
        Assert.AreEqual("hunter2", Protector(store).Unprotect(cipher), "下次启动不用主密码");
    }

    // ———— 降级 ————

    [TestMethod]
    public void Downgrade_OldVersionDoesNotOverwriteWrappedEntries_AndUpgradingBackUnlocksBoth()
    {
        var store = new MemoryKeychain();
        string before = Protector(store).Protect("saved before the downgrade")!;
        WrapEverything(Resolver(store), Password, withRecovery: false);
        Dictionary<string, byte[]> wrapped = store.Entries.ToDictionary(static e => e.Key, static e => e.Value.ToArray());

        // 旧版本(第一步)启动:认不出 VSMP1,当成「对不上编号」,另起一把新密钥。
        LegacyMasterKeyRing legacy = new LegacyMasterKeyResolver(_keyFile, store).Resolve();
        string duringDowngrade = EncryptWith(legacy.Current, "saved while downgraded");

        foreach ((string id, byte[] value) in wrapped)
        {
            Assert.AreSequenceEqual(value, store.Entries[id], "旧版本不覆盖包裹过的条目");
        }
        Assert.AreEqual(wrapped.Count + 1, store.Entries.Count);

        // 升回来:锁着;解锁后新旧两把都在,旧版本另起的那把被包上。
        AesSecretProtector protector = Protector(store);
        Assert.IsTrue(protector.IsLocked);
        Assert.IsFalse(protector.TryUnprotect(duringDowngrade, out _), "锁着时明文存着的那把也不用");
        Assert.AreEqual(VaultUnlockStatus.Unlocked, protector.Vault.Unlock(Utf8(Password)).Status);
        Assert.AreEqual("saved before the downgrade", protector.Unprotect(before));
        Assert.AreEqual("saved while downgraded", protector.Unprotect(duringDowngrade));
        Assert.IsTrue(store.Entries.Values.All(static v => MasterKeyEnvelope.IsEnvelope(v)), "旧版本另起的那把也包上了");
        Assert.IsFalse(File.Exists(_keyFile));
    }

    [TestMethod]
    public void Downgrade_WithoutAKeychain_OldVersionKeepsTheWrappedKeyFileAside()
    {
        // Windows(没有密钥链后端):包裹过的是 secret.key。旧版本读不懂,原样改名保留、另起新密钥 —— 不覆盖。
        string before = Protector(null).Protect("saved before the downgrade")!;
        WrapEverything(Resolver(null), Password, withRecovery: false);
        byte[] wrappedFile = File.ReadAllBytes(_keyFile);

        LegacyMasterKeyRing legacy = new LegacyMasterKeyResolver(_keyFile, null).Resolve();

        Assert.IsNotNull(legacy.Status.ReplacedKeyFile, "旧版本如实报告密钥文件被换过");
        Assert.AreSequenceEqual(wrappedFile, File.ReadAllBytes(legacy.Status.ReplacedKeyFile!), "包裹过的那份原样留着");
        // 升回来:头找不到它包裹的密钥(被旧版本挪开了),改名保留、照第一步继续,不卡在锁着解不开。
        AesSecretProtector protector = Protector(null);
        Assert.IsFalse(protector.IsLocked);
        Assert.IsNotNull(protector.GetKeyStatus().RetiredMasterPasswordHeader);
        Assert.IsTrue(File.Exists(protector.GetKeyStatus().RetiredMasterPasswordHeader!), "头改名保留,不删");
        Assert.IsFalse(protector.TryUnprotect(before, out _), "降级之前存的密码这时解不开(已知限制,见设计稿 §8)");
    }

    private static string EncryptWith(byte[] key, string plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = System.Text.Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        return StoredSecret.ProtectedPrefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }
}
