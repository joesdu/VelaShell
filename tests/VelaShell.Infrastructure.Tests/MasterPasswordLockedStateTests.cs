using System.Text.Json.Nodes;
using NSubstitute;
using SonnetDB.Documents;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Sync;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Testing;
using static VelaShell.Infrastructure.Tests.MasterPasswordTestKit;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 主密码锁着时整个应用的语义:闸口(非交互式不弹框、并发的交互式请求只弹一个框)、仓储把机密字段冻在库里那一份上
/// (新的存不进去,旧的也不被空值或锁定前解开的明文顶掉)、代理密码、云同步跳过、插件机密绝不交回 null。
/// </summary>
[TestClass]
public sealed class MasterPasswordLockedStateTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _root;
    private readonly string _keyFile;
    private readonly SonnetDbEngine _engine;

    public MasterPasswordLockedStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _keyFile = Path.Combine(_root, "secret.key");
        _engine = new(Path.Combine(_root, "sonnetdb"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 留给临时目录清理。
        }
    }

    private AesSecretProtector Unprotected() => new(new MasterKeyResolver(_keyFile, null));

    /// <summary>把数据目录照开启后的样子包起来,交回一个新启动(锁着)的保护器。</summary>
    private AesSecretProtector LockedAfterWrapping()
    {
        WrapEverything(new MasterKeyResolver(_keyFile, null), Password, withRecovery: false);
        AesSecretProtector locked = new(new MasterKeyResolver(_keyFile, null));
        Assert.IsTrue(locked.IsLocked);
        return locked;
    }

    // ———— 闸口 ————

    [TestMethod]
    public async Task Gate_NonInteractive_NeverPrompts()
    {
        _ = Unprotected().Protect("x");
        var prompt = new CountingPrompt();
        var service = new MasterPasswordService(LockedAfterWrapping(), () => prompt);

        Assert.IsFalse(await service.EnsureUnlockedAsync(interactive: false));

        Assert.AreEqual(0, prompt.Count, "自动重连、自动同步这类非交互式入口不弹框");
        Assert.IsTrue(service.IsLocked);
    }

    [TestMethod]
    public async Task Gate_ConcurrentInteractiveRequests_ShareOnePrompt()
    {
        _ = Unprotected().Protect("x");
        var prompt = new CountingPrompt { Hold = true };
        var service = new MasterPasswordService(LockedAfterWrapping(), () => prompt);
        prompt.OnPrompt = async () => (await service.UnlockAsync(Utf8(Password))).Status == MasterPasswordUnlockStatus.Unlocked;
        int unlockedEvents = 0;
        service.Unlocked += (_, _) => Interlocked.Increment(ref unlockedEvents);

        Task<bool>[] waiters = [.. Enumerable.Range(0, 10).Select(_ => service.EnsureUnlockedAsync(interactive: true))];
        await prompt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        prompt.Release.SetResult();
        bool[] results = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(results.All(static r => r), "十个标签同时恢复:都等同一个框的结果");
        Assert.AreEqual(1, prompt.Count, "同一时刻只有一个解锁框");
        Assert.AreEqual(1, unlockedEvents);
        Assert.IsTrue(await service.EnsureUnlockedAsync(interactive: true));
        Assert.AreEqual(1, prompt.Count, "解开之后不再弹");
    }

    [TestMethod]
    public async Task Gate_PromptCancelled_EveryWaiterGetsFalse_AndTheNextRequestPromptsAgain()
    {
        _ = Unprotected().Protect("x");
        var prompt = new CountingPrompt { Hold = true, OnPrompt = static () => Task.FromResult(false) };
        var service = new MasterPasswordService(LockedAfterWrapping(), () => prompt);

        Task<bool>[] waiters = [.. Enumerable.Range(0, 3).Select(_ => service.EnsureUnlockedAsync(interactive: true))];
        await prompt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        prompt.Release.SetResult();
        bool[] results = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(results.All(static r => !r), "取消之后排着的请求一起拿到 false,不再逐个弹");
        Assert.AreEqual(1, prompt.Count);
        prompt.Hold = false;
        Assert.IsFalse(await service.EnsureUnlockedAsync(interactive: true));
        Assert.AreEqual(2, prompt.Count, "下一次用户主动的操作再弹");
    }

    [TestMethod]
    public async Task Gate_WithoutAMasterPassword_IsAlwaysOpen()
    {
        var prompt = new CountingPrompt();
        var service = new MasterPasswordService(Unprotected(), () => prompt);

        Assert.IsTrue(await service.EnsureUnlockedAsync(interactive: false));
        Assert.IsTrue(await service.EnsureUnlockedAsync(interactive: true));
        Assert.AreEqual(0, prompt.Count);
        Assert.IsFalse(service.IsEnabled);
        Assert.IsFalse(service.LockNow());
    }

    [TestMethod]
    public async Task LockNow_RaisesLocked_AndUnlockRaisesUnlocked()
    {
        _ = Unprotected().Protect("x");
        var service = new MasterPasswordService(LockedAfterWrapping());
        int locked = 0;
        int unlocked = 0;
        service.Locked += (_, _) => locked++;
        service.Unlocked += (_, _) => Interlocked.Increment(ref unlocked);

        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, (await service.UnlockAsync(Utf8(Password))).Status);
        Assert.IsTrue(service.LockNow());

        Assert.AreEqual(1, unlocked);
        Assert.AreEqual(1, locked);
        Assert.IsTrue(service.IsLocked);
    }

    [TestMethod]
    public async Task UnlockAsync_ZeroesThePasswordBytes()
    {
        _ = Unprotected().Protect("x");
        var service = new MasterPasswordService(LockedAfterWrapping());
        byte[] password = Utf8(Password);

        await service.UnlockAsync(password);

        Assert.IsTrue(password.All(static b => b == 0), "交出所有权的口令字节用完清零");
    }

    // ———— 仓储 ————

    [TestMethod]
    public async Task SessionRepository_WhileLocked_KeepsTheStoredSecrets_ButSavesEverythingElse()
    {
        var before = new SonnetDbSessionRepository(_engine, Unprotected());
        SessionProfile profile = new()
        {
            Name = "db", Host = "db.example", Username = "root", AuthMethod = AuthMethod.Password, Password = "old-password",
            PluginSecrets = new() { ["token"] = "old-token" }
        };
        await before.SaveSessionAsync(profile);
        string storedPassword = (await RawProfileAsync(profile.Id))!["password"]!.GetValue<string>();
        var repository = new SonnetDbSessionRepository(_engine, LockedAfterWrapping());

        SessionProfile loaded = (await repository.GetSessionAsync(profile.Id))!;
        Assert.IsTrue(StoredSecret.IsUnreadable(loaded.Password), "锁着时载入的是密文原样");
        loaded.Name = "renamed while locked";
        loaded.Password = "typed while locked";
        loaded.PluginSecrets = null;
        await repository.SaveSessionAsync(loaded);
        SessionProfile stripped = loaded.Clone();
        stripped.Password = null; // 「记住密码」没勾、解锁取消后剥掉机密的副本
        await repository.SaveSessionAsync(stripped);

        JsonNode raw = (await RawProfileAsync(profile.Id))!;
        Assert.AreEqual("renamed while locked", raw["name"]!.GetValue<string>(), "其余字段照常存");
        Assert.AreEqual(storedPassword, raw["password"]!.GetValue<string>(), "密码冻在库里那一份上:不被明文、空值顶掉");
        Assert.DoesNotContain("typed while locked", raw.ToJsonString());
        Assert.IsNotNull(raw["pluginSecrets"]?["token"], "库里有、这次没带的插件机密也留着");
    }

    [TestMethod]
    public async Task SessionRepository_WhileLocked_ANewProfileIsSavedWithoutItsPassword()
    {
        _ = Unprotected().Protect("x");
        var repository = new SonnetDbSessionRepository(_engine, LockedAfterWrapping());
        SessionProfile profile = new() { Name = "new", Host = "h", Username = "u", AuthMethod = AuthMethod.Password, Password = "typed" };

        await repository.SaveSessionAsync(profile);

        JsonNode raw = (await RawProfileAsync(profile.Id))!;
        Assert.AreEqual("new", raw["name"]!.GetValue<string>());
        Assert.IsNull(raw["password"]?.GetValue<string>(), "锁着加不了密:不存明文,也不另起主密码管不到的密钥");
        Assert.AreEqual("typed", profile.Password, "调用方手里那份不改(这次连接照样用手输的)");
    }

    [TestMethod]
    public async Task SharedCredentials_WhileLocked_KeepTheStoredPassword()
    {
        var before = new SonnetDbSharedCredentialRepository(_engine, Unprotected());
        SharedCredential credential = new() { Name = "ops", Username = "ops", AuthMethod = AuthMethod.Password, Password = "old" };
        await before.SaveAsync(credential);
        AesSecretProtector locked = LockedAfterWrapping();
        var repository = new SonnetDbSharedCredentialRepository(_engine, locked);

        SharedCredential loaded = (await repository.GetAsync(credential.Id))!;
        loaded.Name = "ops (renamed)";
        loaded.Password = "new";
        await repository.SaveAsync(loaded);

        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, locked.Vault.Unlock(Utf8(Password)).Status);
        SharedCredential reread = (await repository.GetAsync(credential.Id))!;
        Assert.AreEqual("ops (renamed)", reread.Name);
        Assert.AreEqual("old", reread.Password, "锁着时改的密码没存进去,原来那份还在");
    }

    [TestMethod]
    public async Task ProxyPassword_WhileLocked_IsNeitherWipedNorReplaced_AndIsFilledInAfterUnlocking()
    {
        var first = new SonnetDbSettingsService(_engine, protector: Unprotected());
        AppSettings settings = await first.GetSettingsAsync();
        settings.Proxy.Type = "http";
        settings.Proxy.Host = "proxy.example";
        settings.Proxy.Port = 3128;
        settings.Proxy.Username = "alice";
        settings.Proxy.Password = "proxy-secret";
        await first.SaveSettingsAsync(settings);
        string stored = await StoredProxyPasswordAsync();
        AesSecretProtector locked = LockedAfterWrapping();

        var service = new SonnetDbSettingsService(_engine, protector: locked);
        AppSettings loaded = await service.GetSettingsAsync();
        Assert.AreEqual(string.Empty, loaded.Proxy.Password, "锁着解不开:交出空串,不把密文当密码");
        loaded.TerminalFontSize = 15;
        await service.SaveSettingsAsync(loaded);
        Assert.AreEqual(stored, await StoredProxyPasswordAsync(), "一次无关的保存不抹掉它");
        loaded.Proxy.Password = "typed while locked";
        await service.SaveSettingsAsync(loaded);
        Assert.AreEqual(stored, await StoredProxyPasswordAsync(), "锁着时新填的加不了密:不存,也不抛");

        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, locked.Vault.Unlock(Utf8(Password)).Status);
        var reopened = new SonnetDbSettingsService(_engine, protector: locked);
        AppSettings unlocked = await reopened.GetSettingsAsync();
        Assert.AreEqual("proxy-secret", unlocked.Proxy.Password);

        // 锁定前已经解开(内存里是明文)的那份:锁着时保存照旧写回原密文,不存成空的。
        Assert.IsTrue(locked.Vault.Lock());
        unlocked.TerminalFontSize = 16;
        await reopened.SaveSettingsAsync(unlocked);
        Assert.AreEqual(stored, await StoredProxyPasswordAsync());
    }

    [TestMethod]
    public async Task ProxyPassword_LockedAtStartup_IsFilledInRightAfterUnlocking()
    {
        var first = new SonnetDbSettingsService(_engine, protector: Unprotected());
        AppSettings settings = await first.GetSettingsAsync();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "proxy.example";
        settings.Proxy.Port = 1080;
        settings.Proxy.Password = "proxy-secret";
        await first.SaveSettingsAsync(settings);
        AesSecretProtector locked = LockedAfterWrapping();
        var service = new SonnetDbSettingsService(_engine, protector: locked);
        Assert.AreEqual(string.Empty, (await service.GetSettingsAsync()).Proxy.Password);

        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked, locked.Vault.Unlock(Utf8(Password)).Status);
        service.RevealProxyPasswordAfterUnlock();

        Assert.AreEqual("proxy-secret", (await service.GetSettingsAsync()).Proxy.Password, "不用等重启");
        Assert.AreEqual("proxy-secret", (await service.GetSnapshotAsync()).Proxy.Password, "代理解析读的快照也带上");
    }

    // ———— 云同步 ————

    [TestMethod]
    public async Task GistSync_WhileLocked_SkipsTheRound_InsteadOfAskingForANewToken()
    {
        ISecretProtector protector = Substitute.For<ISecretProtector>();
        protector.IsLocked.Returns(true);
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<SyncSettings>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new SyncSettings { Enabled = true, ProtectedToken = "enc1:token", GistId = "abc" });
        var service = new GistSyncService(Substitute.For<ISettingsService>(), Substitute.For<ISessionRepository>(), store,
            Substitute.For<IQuickCommandRepository>(), protector);

        SyncResult result = await service.SyncNowAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual(Strings.Get("SyncSvc_Locked"), result.Message, "不报成「令牌解不开,请重新填写」那种误导的错");
        protector.DidNotReceiveWithAnyArgs().TryUnprotect(default, out _);
    }

    // ———— 插件机密 ————

    [TestMethod]
    public async Task PluginSecrets_WhileLocked_AreNeverReadAsNull()
    {
        var inner = new FakeSecrets();
        inner.Values["token"] = "saved";
        IMasterPasswordGate gate = Substitute.For<IMasterPasswordGate>();
        gate.EnsureUnlockedAsync(true, Arg.Any<CancellationToken>()).Returns(false);
        var secrets = MasterPasswordGatedSecrets.Wrap(inner, gate);

        await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => secrets.GetAsync("token"),
            "取消了解锁:不交回 null(插件会以为不存在、让用户重填覆盖旧值)");
        await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => secrets.SetAsync("token", "new"));
        Assert.AreEqual("saved", inner.Values["token"], "锁着时写不进去,旧值不动");
        Assert.IsTrue(await secrets.DeleteAsync("token"), "删除不用解密,照常放行");
    }

    [TestMethod]
    public async Task PluginSecrets_WaitForTheInteractiveUnlock()
    {
        var inner = new FakeSecrets();
        inner.Values["token"] = "saved";
        IMasterPasswordGate gate = Substitute.For<IMasterPasswordGate>();
        gate.EnsureUnlockedAsync(true, Arg.Any<CancellationToken>()).Returns(true);

        Assert.AreEqual("saved", await MasterPasswordGatedSecrets.Wrap(inner, gate).GetAsync("token"));
        await gate.Received(1).EnsureUnlockedAsync(true, Arg.Any<CancellationToken>());
    }

    // ———— 帮手 ————

    private Task<JsonNode?> RawProfileAsync(Guid id) =>
        _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection, store =>
        {
            DocumentRow? row = store.Get(id.ToString("D"));
            return row is null ? null : JsonNode.Parse(row.Json);
        });

    private Task<string> StoredProxyPasswordAsync() =>
        _engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection, store =>
            JsonNode.Parse(store.Get("settings")!.Json)?["proxy"]?["password"]?.GetValue<string>() ?? string.Empty);

    /// <summary>假的解锁框:数一数弹了几次;可以让它停在框上等测试放行。</summary>
    private sealed class CountingPrompt : IMasterPasswordPrompt
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool Hold { get; set; }

        public Func<Task<bool>> OnPrompt { get; set; } = static () => Task.FromResult(false);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> PromptUnlockAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            Started.TrySetResult();
            if (Hold)
            {
                await Release.Task.ConfigureAwait(false);
            }
            return await OnPrompt().ConfigureAwait(false);
        }
    }
}
