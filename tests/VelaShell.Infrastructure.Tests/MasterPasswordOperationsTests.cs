using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;
using static VelaShell.Infrastructure.Tests.MasterPasswordTestKit;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 开启 / 关闭 / 修改 / 忘记主密码、恢复码、rehash:每一步中断都不丢任何一把本机加密密钥(沿用第一步的假密钥链用例形状),
/// 已存的密文一字不改;设置里的镜像字段改成什么都不影响实际的保护状态。
/// </summary>
[TestClass]
public sealed class MasterPasswordOperationsTests
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a different long passphrase";

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

    private AesSecretProtector Protector(IMasterKeyStore? store) => new(new MasterKeyResolver(_keyFile, store));

    /// <summary>密钥链里两把密钥(一把原有、一把是密钥链暂时用不了那一轮另起后并进来的),各加密一段。</summary>
    private (string First, string Second) TwoKeys(MemoryKeychain store)
    {
        string first = Protector(store).Protect("first")!;
        store.Unavailable = "locked keyring";
        string second = Protector(store).Protect("second")!;
        store.Unavailable = null;
        _ = Protector(store).GetKeyStatus();
        Assert.AreEqual(2, store.Entries.Count);
        return (first, second);
    }

    private static void AssertReads(AesSecretProtector protector, (string First, string Second) cipher)
    {
        Assert.AreEqual("first", protector.Unprotect(cipher.First));
        Assert.AreEqual("second", protector.Unprotect(cipher.Second));
    }

    // ———— 开启 ————

    [TestMethod]
    public void Enable_WrapsEverySlot_LeavesTheCiphertextAlone_AndTheNextStartIsLocked()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        AesSecretProtector protector = Protector(store);

        MasterPasswordChangeResult result = protector.Vault.Enable(Utf8(Password), withRecoveryCode: true);

        Assert.AreEqual(MasterPasswordChangeStatus.Done, result.Status);
        Assert.IsTrue(RecoveryCode.TryParse(result.RecoveryCode, out _), "开启时交回一份恢复码");
        Assert.IsFalse(protector.IsLocked, "开启的这一次照样解着");
        AssertReads(protector, cipher);
        Assert.IsTrue(store.Entries.Values.All(static v => MasterKeyEnvelope.TryParse(v, out MasterKeyEnvelope? e) && e!.Records.Count == 2),
            "每个条目:主密码一条、恢复码一条");
        Assert.AreEqual(MasterPasswordHeaderState.Active, MasterPasswordHeader.Parse(File.ReadAllText(HeaderFile))!.State);
        Assert.IsFalse(File.Exists(_keyFile));

        AesSecretProtector next = Protector(store);
        Assert.IsTrue(next.IsLocked, "下次启动锁着");
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, next.Vault.Unlock(Utf8(Password)).Status);
        AssertReads(next, cipher);
        AesSecretProtector viaCode = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, viaCode.Vault.UnlockWithRecoveryCode(result.RecoveryCode!).Status);
        AssertReads(viaCode, cipher);
    }

    [TestMethod]
    public void Enable_WithoutAKeychain_WrapsTheKeyFile()
    {
        string cipher = Protector(null).Protect("hunter2")!;
        AesSecretProtector protector = Protector(null);

        Assert.AreEqual(MasterPasswordChangeStatus.Done, protector.Vault.Enable(Utf8(Password), withRecoveryCode: false).Status);

        AesSecretProtector next = Protector(null);
        Assert.IsTrue(next.IsLocked, "Windows / 回落的密钥文件:secret.key 里换成包裹(Windows 上外面再套 DPAPI)");
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, next.Vault.Unlock(Utf8(Password)).Status);
        Assert.AreEqual("hunter2", next.Unprotect(cipher));
    }

    [TestMethod]
    public void Enable_FailingHalfway_RollsEverySlotBack()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        Dictionary<string, byte[]> before = store.Entries.ToDictionary(static e => e.Key, static e => e.Value.ToArray());
        AesSecretProtector protector = Protector(store);
        store.FailOneSaveAfter = 1; // 第一个条目包上了,第二个写不进去(回滚写得进去)

        MasterPasswordChangeResult result = protector.Vault.Enable(Utf8(Password), withRecoveryCode: true);

        Assert.AreEqual(MasterPasswordChangeStatus.StorageFailed, result.Status);
        store.FailSavesAfter = -1;
        Assert.IsFalse(File.Exists(HeaderFile), "整体回到没开");
        foreach ((string id, byte[] value) in before)
        {
            CollectionAssert.AreEqual(value, store.Entries[id], "已经包上的那条写回明文");
        }
        Assert.IsFalse(protector.IsLocked);
        AesSecretProtector next = Protector(store);
        Assert.IsFalse(next.IsLocked);
        AssertReads(next, cipher);
    }

    [TestMethod]
    public void Enable_WhenTheRollbackAlsoFails_StaysOnWithThePasswordJustTyped()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        AesSecretProtector protector = Protector(store);
        store.FailSavesAfter = 1; // 第二个写不进去,回滚第一个也写不进去

        MasterPasswordChangeResult result = protector.Vault.Enable(Utf8(Password), withRecoveryCode: false);

        Assert.AreEqual(MasterPasswordChangeStatus.StorageFailedPartially, result.Status);
        store.FailSavesAfter = -1;
        AesSecretProtector next = Protector(store);
        Assert.IsTrue(next.IsLocked, "有一条已经只认刚输入的主密码:按开着处理,如实交代");
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, next.Vault.Unlock(Utf8(Password)).Status);
        AssertReads(next, cipher);
        Assert.IsTrue(store.Entries.Values.All(static v => MasterKeyEnvelope.IsEnvelope(v)), "解锁后把明文那条也包上");
    }

    [TestMethod]
    public void Enable_WhileTheKeychainIsUnavailable_DoesNothing()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        AesSecretProtector protector = Protector(store);
        store.Unavailable = "no session bus";

        Assert.AreEqual(MasterPasswordChangeStatus.KeysUnavailable, protector.Vault.Enable(Utf8(Password), false).Status);

        store.Unavailable = null;
        Assert.IsFalse(File.Exists(HeaderFile));
        Assert.IsTrue(store.Entries.Values.All(static v => v.Length == 32));
        AssertReads(Protector(store), cipher);
    }

    // ———— 关闭 ————

    [TestMethod]
    public void Disable_RestoresTheStepOneForm()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, Protector(store).Vault.Enable(Utf8(Password), true).Status);
        AesSecretProtector locked = Protector(store);

        Assert.AreEqual(MasterPasswordChangeStatus.Done, locked.Vault.Disable(Utf8(Password)).Status);

        Assert.IsFalse(locked.IsLocked, "关的这一次:密钥串现成解着");
        AssertReads(locked, cipher);
        Assert.IsFalse(File.Exists(HeaderFile));
        Assert.IsTrue(store.Entries.Values.All(static v => v.Length == 32), "写回第一步的明文形态");
        AesSecretProtector next = Protector(store);
        Assert.IsFalse(next.IsLocked);
        AssertReads(next, cipher);
    }

    [TestMethod]
    public void Disable_WithAWrongPassword_ChangesNothing_AndBacksOff()
    {
        var store = new MemoryKeychain();
        _ = TwoKeys(store);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, Protector(store).Vault.Enable(Utf8(Password), false).Status);
        Dictionary<string, byte[]> before = store.Entries.ToDictionary(static e => e.Key, static e => e.Value.ToArray());
        var clock = new ManualClock();
        AesSecretProtector locked = new(new MasterKeyResolver(_keyFile, store), clock);

        MasterPasswordChangeResult wrong = locked.Vault.Disable(Utf8("not it"));

        Assert.AreEqual(MasterPasswordChangeStatus.WrongPassword, wrong.Status);
        Assert.AreEqual(TimeSpan.FromSeconds(1), wrong.RetryAfter);
        Assert.AreEqual(MasterPasswordChangeStatus.Throttled, locked.Vault.Disable(Utf8(Password)).Status);
        foreach ((string id, byte[] value) in before)
        {
            CollectionAssert.AreEqual(value, store.Entries[id]);
        }
        Assert.IsTrue(File.Exists(HeaderFile));
    }

    [TestMethod]
    public void Disable_FailingHalfway_StaysOn_AndTheSamePasswordStillUnlocks()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, Protector(store).Vault.Enable(Utf8(Password), false).Status);
        AesSecretProtector locked = Protector(store);
        store.FailOneSaveAfter = 1;

        MasterPasswordChangeResult result = locked.Vault.Disable(Utf8(Password));

        Assert.AreEqual(MasterPasswordChangeStatus.StorageFailed, result.Status);
        store.FailSavesAfter = -1;
        AesSecretProtector next = Protector(store);
        Assert.IsTrue(next.IsLocked);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, next.Vault.Unlock(Utf8(Password)).Status);
        AssertReads(next, cipher);
    }

    // ———— 修改 ————

    [TestMethod]
    public void Change_OnlyTheNewPasswordUnlocks_AndTheRecoveryCodeStillWorks()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        string code = Protector(store).Vault.Enable(Utf8(Password), true).RecoveryCode!;
        AesSecretProtector locked = Protector(store);

        Assert.AreEqual(MasterPasswordChangeStatus.Done, locked.Vault.Change(Utf8(Password), Utf8(NewPassword)).Status);

        AssertReads(locked, cipher);
        Assert.IsTrue(store.Entries.Values.All(static v => MasterKeyEnvelope.TryParse(v, out MasterKeyEnvelope? e) && e!.Records.Count == 2),
            "旧主密码那条删掉了:新主密码一条、恢复码一条");
        Assert.AreEqual(MasterPasswordUnlockStatus.WrongSecret, Protector(store).Vault.Unlock(Utf8(Password)).Status);
        AesSecretProtector viaNew = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, viaNew.Vault.Unlock(Utf8(NewPassword)).Status);
        AssertReads(viaNew, cipher);
        AesSecretProtector viaCode = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, viaCode.Vault.UnlockWithRecoveryCode(code).Status, "恢复码不随主密码变");
    }

    [TestMethod]
    public void Change_FailingBeforeTheHeaderSwap_KeepsTheOldPassword()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, Protector(store).Vault.Enable(Utf8(Password), false).Status);
        AesSecretProtector locked = Protector(store);
        store.FailOneSaveAfter = 1;

        Assert.AreEqual(MasterPasswordChangeStatus.StorageFailed, locked.Vault.Change(Utf8(Password), Utf8(NewPassword)).Status);

        store.FailSavesAfter = -1;
        AesSecretProtector next = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, next.Vault.Unlock(Utf8(Password)).Status);
        AssertReads(next, cipher);
    }

    [TestMethod]
    public void Change_InterruptedAfterAddingTheNewRecord_TheOldPasswordStillUnlocks_AndTheLeftoverIsCleaned()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, Protector(store).Vault.Enable(Utf8(Password), false).Status);
        // 断在第①步之后:每个条目都多了一条新主密码的记录,头还是旧的。
        MasterPasswordHeader nextHeader = AddRecordForANewHeader(store, NewPassword);

        AesSecretProtector next = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.WrongSecret, next.Vault.Unlock(Utf8(NewPassword)).Status, "头没换:新主密码还不算数");
        AesSecretProtector viaOld = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, viaOld.Vault.Unlock(Utf8(Password)).Status);
        AssertReads(viaOld, cipher);
        Assert.IsTrue(store.Entries.Values.All(v => MasterKeyEnvelope.TryParse(v, out MasterKeyEnvelope? e)
                                                      && e!.Find(WrapKind.Password, nextHeader.PasswordWrapId) is null),
            "解锁后清掉对不上当前头的残留记录");
    }

    [TestMethod]
    public void Change_InterruptedAfterTheHeaderSwap_TheNewPasswordUnlocks()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, Protector(store).Vault.Enable(Utf8(Password), false).Status);
        // 断在第②步之后:头换成新的了,条目里旧记录还没删。
        MasterPasswordHeader nextHeader = AddRecordForANewHeader(store, NewPassword);
        Assert.IsTrue(new MasterKeyResolver(_keyFile, store).TryWriteHeader(nextHeader));

        AesSecretProtector next = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, next.Vault.Unlock(Utf8(NewPassword)).Status);
        AssertReads(next, cipher);
        Assert.IsTrue(store.Entries.Values.All(static v => MasterKeyEnvelope.TryParse(v, out MasterKeyEnvelope? e) && e!.Records.Count == 1),
            "旧主密码那条残留在解锁后清掉");
    }

    /// <summary>照修改第①步的样子:给每个条目加上一份用新头、新主密码包的记录(旧的不动)。返回那份新头(不落盘)。</summary>
    private MasterPasswordHeader AddRecordForANewHeader(MemoryKeychain store, string password)
    {
        MasterPasswordHeader current = MasterPasswordHeader.Parse(File.ReadAllText(HeaderFile))!;
        byte[] oldKek = MasterPasswordKdf.DerivePasswordKey(Utf8(Password), current);
        MasterPasswordHeader next = MasterPasswordHeader.CreateNew(current.RecoverySalt, MasterPasswordHeaderState.Active);
        byte[] newKek = MasterPasswordKdf.DerivePasswordKey(Utf8(password), next);
        foreach (string id in store.Entries.Keys.ToArray())
        {
            Assert.IsTrue(MasterKeyEnvelope.TryParse(store.Entries[id], out MasterKeyEnvelope? envelope));
            Assert.IsTrue(MasterKeyEnvelope.TryUnwrap(envelope!.Find(WrapKind.Password, current.PasswordWrapId)!, oldKek, out byte[] dek));
            store.Entries[id] = new MasterKeyEnvelope([.. envelope.Records, MasterKeyEnvelope.Wrap(WrapKind.Password, next.PasswordWrapId, newKek, dek)]).ToBytes();
        }
        return next;
    }

    // ———— 恢复码 / 忘记 ————

    [TestMethod]
    public void RecoveryCode_ThenANewPassword_KeepsEveryKey()
    {
        var store = new MemoryKeychain();
        (string, string) cipher = TwoKeys(store);
        string code = Protector(store).Vault.Enable(Utf8(Password), true).RecoveryCode!;
        AesSecretProtector locked = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, locked.Vault.UnlockWithRecoveryCode(code).Status);

        Assert.AreEqual(MasterPasswordChangeStatus.Done, locked.Vault.SetPasswordAfterRecovery(Utf8(NewPassword)).Status);

        Assert.IsFalse(locked.Vault.UnlockedWithRecoveryCode);
        AesSecretProtector viaNew = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, viaNew.Vault.Unlock(Utf8(NewPassword)).Status);
        AssertReads(viaNew, cipher);
        Assert.AreEqual(MasterPasswordUnlockStatus.WrongSecret, Protector(store).Vault.Unlock(Utf8(Password)).Status, "忘掉的旧主密码不再算数");
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, Protector(store).Vault.UnlockWithRecoveryCode(code).Status, "恢复码照样有效");
    }

    [TestMethod]
    public void SetPasswordAfterRecovery_OnlyAfterARecoveryUnlock()
    {
        var store = new MemoryKeychain();
        _ = TwoKeys(store);
        _ = Protector(store).Vault.Enable(Utf8(Password), true);
        AesSecretProtector locked = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, locked.Vault.Unlock(Utf8(Password)).Status);

        Assert.AreEqual(MasterPasswordChangeStatus.NotRecovered, locked.Vault.SetPasswordAfterRecovery(Utf8(NewPassword)).Status,
            "用主密码解开的不能绕过「输入当前主密码」去改");
    }

    [TestMethod]
    public void Forget_KeepsEverythingAside_AndStartsAFreshKey()
    {
        var store = new MemoryKeychain();
        (string First, string Second) cipher = TwoKeys(store);
        _ = Protector(store).Vault.Enable(Utf8(Password), false);
        Dictionary<string, byte[]> wrapped = store.Entries.ToDictionary(static e => e.Key, static e => e.Value.ToArray());
        AesSecretProtector locked = Protector(store);

        MasterPasswordChangeResult result = locked.Vault.Forget();

        Assert.AreEqual(MasterPasswordChangeStatus.Done, result.Status);
        Assert.IsFalse(locked.IsLocked);
        Assert.IsFalse(locked.GetKeyStatus().MasterPasswordEnabled, "忘记之后主密码关掉、另起新密钥");
        Assert.IsFalse(locked.TryUnprotect(cipher.First, out _), "已存的机密作废:没有后门");
        string fresh = locked.Protect("fresh")!;
        Assert.AreEqual("fresh", Protector(store).Unprotect(fresh));
        Assert.HasCount(1, Directory.GetFiles(_root, "secret.key.mp.forgotten-*"), "头改名保留,不删");
        Assert.HasCount(1, Directory.GetFiles(_root, "secret.key.ref.forgotten-*"), "旧条目的清单也留着");
        foreach ((string id, byte[] value) in wrapped)
        {
            CollectionAssert.AreEqual(value, store.Entries[id], "密钥链里的旧条目原样不动");
        }

        // 想起来了:把留着的文件改回原名,旧主密码照样解得开。
        foreach (string aside in Directory.GetFiles(_root, "*.forgotten-*"))
        {
            File.Move(aside, aside[..aside.IndexOf(".forgotten-", StringComparison.Ordinal)], overwrite: true);
        }
        AesSecretProtector remembered = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, remembered.Vault.Unlock(Utf8(Password)).Status);
        Assert.AreEqual("first", remembered.Unprotect(cipher.First));
    }

    [TestMethod]
    public void Unlock_WithOlderParameters_RehashesToTheCurrentOnes()
    {
        var store = new MemoryKeychain();
        string cipher = Protector(store).Protect("hunter2")!;
        // 以后调了参的样子:头里记着别的参数(这里用小一点的,跑得快)。
        var resolver = new MasterKeyResolver(_keyFile, store);
        var header = new MasterPasswordHeader
        {
            MemoryKiB = 1024, Iterations = 1, Parallelism = 1, Salt = RandomNumberGenerator.GetBytes(16), State = MasterPasswordHeaderState.Active
        };
        Assert.IsTrue(resolver.TryWriteHeader(header));
        byte[] kek = MasterPasswordKdf.DerivePasswordKey(Utf8(Password), header);
        string id = store.Entries.Keys.Single();
        store.Entries[id] = new MasterKeyEnvelope([MasterKeyEnvelope.Wrap(WrapKind.Password, header.PasswordWrapId, kek, store.Entries[id])]).ToBytes();

        AesSecretProtector locked = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, locked.Vault.Unlock(Utf8(Password)).Status);

        Assert.IsTrue(MasterPasswordHeader.Parse(File.ReadAllText(HeaderFile))!.UsesCurrentParameters, "下一次解锁成功时用新参数、新盐重新包裹");
        AesSecretProtector next = Protector(store);
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, next.Vault.Unlock(Utf8(Password)).Status);
        Assert.AreEqual("hunter2", next.Unprotect(cipher));
    }

    // ———— 设置里的镜像字段 ————

    [TestMethod]
    public void TheSettingsFlag_IsNotTheSourceOfTruth_AndDoesNotTravel()
    {
        AppSettings local = new();
        local.General.MasterPasswordProtection = false;
        AppSettings other = new();
        other.General.MasterPasswordProtection = true;

        string exported = SettingsTransfer.Export(other, [SettingsCategories.General], DateTime.UtcNow);
        JsonNode node = JsonNode.Parse(exported)!;
        Assert.IsNull(node["general"]?["masterPasswordProtection"], "导出不写");
        node["general"]!["masterPasswordProtection"] = true; // 手写进文件
        SettingsImportFile file = SettingsTransfer.Parse(node.ToJsonString())!;
        AppSettings merged = SettingsTransfer.Merge(local, file, [SettingsCategories.General]).Settings;
        Assert.IsFalse(merged.General.MasterPasswordProtection, "导入的文件手写了 true 也保留本机的");

        // 实际状态只看包裹:设置里写成什么都不会让保护「看起来开了」。
        string cipher = Protector(null).Protect("hunter2")!;
        AesSecretProtector protector = Protector(null);
        Assert.IsFalse(protector.GetKeyStatus().MasterPasswordEnabled);
        Assert.AreEqual("hunter2", protector.Unprotect(cipher));
    }

    // ———— 审计 ————

    [TestMethod]
    public async Task Service_AuditsEachChange_WithoutAnyPasswordMaterial()
    {
        var store = new MemoryKeychain();
        _ = TwoKeys(store);
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        List<AuditEntry> entries = [];
        await audit.WriteAsync(Arg.Do<AuditEntry>(entries.Add), Arg.Any<CancellationToken>());
        var service = new MasterPasswordService(Protector(store), audit: audit);

        Assert.AreEqual(MasterPasswordChangeStatus.Done, (await service.EnableAsync(Utf8(Password), true)).Status);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, (await service.ChangeAsync(Utf8(Password), Utf8(NewPassword))).Status);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, (await service.DisableAsync(Utf8(NewPassword))).Status);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, (await service.EnableAsync(Utf8(Password), false)).Status);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, (await service.ForgetAsync()).Status);

        CollectionAssert.AreEqual(
            new[] { AuditActions.MasterPasswordEnabled, AuditActions.MasterPasswordChanged, AuditActions.MasterPasswordDisabled,
                AuditActions.MasterPasswordEnabled, AuditActions.MasterPasswordReset },
            entries.Select(static e => e.Action).ToArray());
        Assert.IsTrue(entries.All(static e => e.Category == AuditCategories.Settings));
        Assert.IsTrue(entries.All(static e => !e.Detail.Contains("horse", StringComparison.Ordinal) && !e.Detail.Contains("passphrase", StringComparison.Ordinal)),
            "只记动作,不记任何口令材料");
    }
}
