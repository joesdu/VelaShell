using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Services.MasterPassword;

namespace VelaShell.Tests.Services;

/// <summary>
/// 主密码的自动锁定:空闲 N 分钟(只看本应用的输入)、系统锁屏时;两项默认都关;主密码没开、已经锁着时什么都不做;
/// 解锁那一刻空闲计时清零。读不到锁屏状态时不锁(也不假装锁了)。
/// </summary>
[TestClass]
public sealed class MasterPasswordAutoLockTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Probe(bool? state) : ISystemSessionLockProbe
    {
        public bool? State { get; set; } = state;

        public bool IsSupported => State is not null;

        public bool TryRead(out bool locked)
        {
            locked = State == true;
            return State is not null;
        }
    }

    private static IMasterPasswordService Service(bool enabled = true, bool locked = false)
    {
        IMasterPasswordService service = Substitute.For<IMasterPasswordService>();
        service.IsEnabled.Returns(enabled);
        service.IsLocked.Returns(locked);
        service.LockNow().Returns(true);
        return service;
    }

    private static AppSettings Settings(int minutes, bool onSystemLock = false)
    {
        AppSettings settings = new();
        settings.General.MasterPasswordAutoLockMinutes = minutes;
        settings.General.MasterPasswordLockOnSystemLock = onSystemLock;
        return settings;
    }

    [TestMethod]
    public void Defaults_NeverLock()
    {
        IMasterPasswordService service = Service();
        var clock = new Clock();
        using var autoLock = new MasterPasswordAutoLock(service, () => new AppSettings(), new Probe(true), clock);

        clock.Now += TimeSpan.FromDays(1);

        Assert.IsFalse(autoLock.CheckOnce(), "默认不自动锁(维护者 2026-10-31 定)");
        service.DidNotReceive().LockNow();
    }

    [TestMethod]
    public void Idle_LocksAfterTheConfiguredMinutes_AndActivityResetsTheTimer()
    {
        IMasterPasswordService service = Service();
        var clock = new Clock();
        using var autoLock = new MasterPasswordAutoLock(service, () => Settings(15), new Probe(null), clock);

        clock.Now += TimeSpan.FromMinutes(14);
        Assert.IsFalse(autoLock.CheckOnce());
        autoLock.NoteActivity();
        clock.Now += TimeSpan.FromMinutes(14);
        Assert.IsFalse(autoLock.CheckOnce(), "中途有操作:重新计时");
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.IsTrue(autoLock.CheckOnce());

        service.Received(1).LockNow();
    }

    [TestMethod]
    public void Unlocking_RestartsTheIdleTimer()
    {
        IMasterPasswordService service = Service();
        var clock = new Clock();
        using var autoLock = new MasterPasswordAutoLock(service, () => Settings(5), new Probe(null), clock);

        clock.Now += TimeSpan.FromHours(2);
        service.Unlocked += Raise.Event();

        Assert.IsFalse(autoLock.CheckOnce(), "刚解开不因为之前空闲太久又锁上");
    }

    [TestMethod]
    public void SystemLock_LocksOnlyWhenTheStateIsKnown()
    {
        IMasterPasswordService service = Service();
        var probe = new Probe(false);
        using var autoLock = new MasterPasswordAutoLock(service, () => Settings(0, onSystemLock: true), probe, new Clock());

        Assert.IsFalse(autoLock.CheckOnce());
        probe.State = null;
        Assert.IsFalse(autoLock.CheckOnce(), "读不到锁屏状态:不锁,也不假装锁了");
        probe.State = true;
        Assert.IsTrue(autoLock.CheckOnce());
        service.Received(1).LockNow();
    }

    [TestMethod]
    public void WithoutAMasterPassword_OrAlreadyLocked_DoesNothing()
    {
        var clock = new Clock();
        IMasterPasswordService off = Service(enabled: false);
        IMasterPasswordService locked = Service(locked: true);
        using var a = new MasterPasswordAutoLock(off, () => Settings(1, true), new Probe(true), clock);
        using var b = new MasterPasswordAutoLock(locked, () => Settings(1, true), new Probe(true), clock);
        clock.Now += TimeSpan.FromHours(1);

        Assert.IsFalse(a.CheckOnce());
        Assert.IsFalse(b.CheckOnce());
        off.DidNotReceive().LockNow();
        locked.DidNotReceive().LockNow();
    }

    [TestMethod]
    public void WindowsProbe_ReadsThisUnlockedSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            TestContext.WriteLine("[SKIP] the session lock probe only exists on Windows");
            return;
        }
        ISystemSessionLockProbe probe = SystemSessionLockProbes.ForCurrentPlatform();

        Assert.IsTrue(probe.IsSupported, "WTSQuerySessionInformation(WTSSessionInfoEx) 读得到");
        Assert.IsTrue(probe.TryRead(out bool isLocked));
        Assert.IsFalse(isLocked, "跑测试的这个会话没锁屏(读错了偏移会读到 SessionState = 0,当成锁着)");
    }
}
