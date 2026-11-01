using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Services.MasterPassword;

/// <summary>
/// 主密码的自动锁定(设置 → 常规 → 隐私与安全):VelaShell 的窗口空闲 N 分钟后锁定、系统锁屏时锁定。默认都关(维护者 2026-10-31 定)。
/// </summary>
/// <remarks>
/// <para>
/// 「空闲」只看本应用窗口里的键盘、鼠标、滚轮、触摸 —— 在全部顶层窗口上挂隧道路由的类处理器(含已处理的事件),
/// 设置窗口、对话框里的操作也算。终端里一直在刷的输出不算操作。
/// </para>
/// <para>
/// 检查放在线程池上的定时器里(每 <see cref="CheckInterval" /> 一次):问锁没锁可能要先解析密钥、问系统锁屏是一次系统调用,都不该压 UI 线程。
/// 锁定 = <see cref="IMasterPasswordService.LockNow" />(清零内存里的密钥),之后的事(会话树重读、状态栏的锁形入口)由各自订阅的
/// <see cref="IMasterPasswordGate.Locked" /> 去做。解锁的那一刻把空闲计时清零,免得刚解开就因为之前空闲太久又锁上。
/// </para>
/// </remarks>
public sealed class MasterPasswordAutoLock : IDisposable
{
    /// <summary>检查间隔。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly IMasterPasswordService _masterPassword;
    private readonly Func<AppSettings?> _settings;
    private readonly ISystemSessionLockProbe _probe;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IDisposable> _handlers = [];
    private long _lastActivityTicks;

    /// <summary>构造(还没开始;见 <see cref="Start" />)。</summary>
    /// <param name="masterPassword">主密码服务。</param>
    /// <param name="settings">取此刻的设置快照(没有时为 null,当作两项都关)。</param>
    /// <param name="probe">系统锁屏状态的读法。</param>
    /// <param name="time">时钟(测试用)。</param>
    public MasterPasswordAutoLock(IMasterPasswordService masterPassword, Func<AppSettings?> settings,
        ISystemSessionLockProbe probe, TimeProvider? time = null)
    {
        _masterPassword = masterPassword ?? throw new ArgumentNullException(nameof(masterPassword));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = time ?? TimeProvider.System;
        NoteActivity();
        _masterPassword.Unlocked += OnUnlocked;
    }

    /// <summary>挂上输入监听、起定时器。只调一次(UI 线程上)。</summary>
    public void Start()
    {
        foreach (RoutedEvent routed in new RoutedEvent[]
                 {
                     InputElement.KeyDownEvent, InputElement.PointerPressedEvent, InputElement.PointerMovedEvent,
                     InputElement.PointerWheelChangedEvent, InputElement.TextInputEvent
                 })
        {
            _handlers.Add(routed.AddClassHandler(typeof(TopLevel), (_, _) => NoteActivity(),
                RoutingStrategies.Tunnel, handledEventsToo: true));
        }
        _ = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>记一次用户操作(输入监听调,测试也直接调)。</summary>
    public void NoteActivity() => Interlocked.Exchange(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);

    /// <summary>
    /// 检查一次:该锁就锁。主密码没开、已经锁着、两项都关时什么都不做。
    /// </summary>
    /// <returns>这一次锁上了为 true。</returns>
    public bool CheckOnce()
    {
        if (_settings() is not { } settings)
        {
            return false;
        }
        int idleMinutes = settings.General.MasterPasswordAutoLockMinutes;
        bool onSystemLock = settings.General.MasterPasswordLockOnSystemLock;
        if ((idleMinutes <= 0 && !onSystemLock) || !_masterPassword.IsEnabled || _masterPassword.IsLocked)
        {
            return false;
        }
        string? reason = null;
        if (idleMinutes > 0)
        {
            TimeSpan idle = _time.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
            if (idle >= TimeSpan.FromMinutes(idleMinutes))
            {
                reason = $"idle for {idleMinutes} minute(s)";
            }
        }
        if (reason is null && onSystemLock && _probe.TryRead(out bool locked) && locked)
        {
            reason = "the system session is locked";
        }
        if (reason is null || !_masterPassword.LockNow())
        {
            return false;
        }
        Trace.WriteLine($"[Secrets] Auto-locked: {reason}");
        return true;
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        using PeriodicTimer timer = new(CheckInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    CheckOnce();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Secrets] Auto-lock check failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 退出。
        }
    }

    private void OnUnlocked(object? sender, EventArgs e) => NoteActivity();

    /// <inheritdoc />
    public void Dispose()
    {
        _masterPassword.Unlocked -= OnUnlocked;
        _stop.Cancel();
        foreach (IDisposable handler in _handlers)
        {
            handler.Dispose();
        }
        _handlers.Clear();
        _stop.Dispose();
    }
}
