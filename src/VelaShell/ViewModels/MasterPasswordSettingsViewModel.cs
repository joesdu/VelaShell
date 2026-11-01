using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services.MasterPassword;

namespace VelaShell.ViewModels;

/// <summary>
/// 设置 → 常规 → 隐私与安全里的主密码那几行(设置审计 R-04):开没开、锁没锁、有没有恢复码,开启 / 修改 / 关闭 / 解锁 / 立即锁定,
/// 以及自动锁定的两项(空闲 N 分钟、系统锁屏时;后者只在读得到锁屏状态的平台上显示)。
/// </summary>
/// <remarks>
/// 状态一律现问主密码服务(事实来源是包裹格式),设置里的 <see cref="GeneralOptions.MasterPasswordProtection" /> 只是照实际状态回写的镜像。
/// </remarks>
public sealed class MasterPasswordSettingsViewModel : ReactiveObject
{
    private readonly IMasterPasswordService? _service;
    private readonly Func<GeneralOptions?> _general;

    /// <summary>构造。</summary>
    /// <param name="service">主密码服务;没有时整块不显示。</param>
    /// <param name="general">设置页此刻的常规选项(回写镜像字段用)。</param>
    /// <param name="probe">系统锁屏状态的读法。</param>
    public MasterPasswordSettingsViewModel(IMasterPasswordService? service, Func<GeneralOptions?> general, ISystemSessionLockProbe probe)
    {
        _service = service;
        _general = general ?? throw new ArgumentNullException(nameof(general));
        ArgumentNullException.ThrowIfNull(probe);
        SystemLockSupported = probe.IsSupported;
        if (service is not null)
        {
            void Refresh(object? sender, EventArgs e) => RxSchedulers.MainThreadScheduler.Schedule(() => _ = RefreshAsync());
            service.StateChanged += Refresh;
            service.Locked += Refresh;
            service.Unlocked += Refresh;
        }
    }

    /// <summary>有主密码服务(没有时整块不显示)。</summary>
    public bool IsAvailable => _service is not null;

    /// <summary>主密码服务。</summary>
    internal IMasterPasswordService? Service => _service;

    /// <summary>这台电脑读得到系统锁屏状态(读不到就不显示「系统锁屏时锁定」,免得开了却不起作用)。</summary>
    public bool SystemLockSupported { get; }

    /// <summary>主密码开着。</summary>
    public bool IsEnabled
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>开着且锁着。</summary>
    public bool IsLocked
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>开着且解着(显示「立即锁定」)。</summary>
    public bool IsUnlocked
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态一行(未开启 / 已开启 · 已解锁 / 已开启 · 锁着,以及有没有恢复码)。</summary>
    public string StatusText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>上一次操作的结果说明;没有时为 null。</summary>
    public string? Notice
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>现问一遍状态(问之前可能要先解析密钥,放到线程池上)。</summary>
    public async Task RefreshAsync()
    {
        if (_service is not { } service)
        {
            return;
        }
        (bool enabled, bool locked, bool recovery) = await Task.Run(() => (service.IsEnabled, service.IsLocked, service.HasRecoveryCode))
            .ConfigureAwait(true);
        IsEnabled = enabled;
        IsLocked = enabled && locked;
        IsUnlocked = enabled && !locked;
        StatusText = !enabled
            ? Strings.Get("SetGeneral_MasterPasswordStatusOff")
            : Strings.Get(locked ? "SetGeneral_MasterPasswordStatusLocked" : "SetGeneral_MasterPasswordStatusUnlocked")
              + " · " + Strings.Get(recovery ? "SetGeneral_MasterPasswordRecovery" : "SetGeneral_MasterPasswordNoRecovery");
        if (_general() is { } general)
        {
            // 镜像字段照实际状态回写(随下一次保存设置落盘;它不决定任何事)。
            general.MasterPasswordProtection = enabled;
        }
    }

    /// <summary>给某一种操作准备对话框的视图模型。</summary>
    public MasterPasswordDialogViewModel? CreateDialog(MasterPasswordDialogMode mode) =>
        _service is { } service ? new MasterPasswordDialogViewModel(service, mode) : null;

    /// <summary>对话框关了之后:记下结果说明、刷新状态。</summary>
    public async Task AfterDialogAsync(MasterPasswordDialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (dialog.CompletionNotice is { } notice)
        {
            Notice = notice;
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>解锁(弹解锁框)。</summary>
    public async Task UnlockAsync()
    {
        if (_service is { } service)
        {
            await service.EnsureUnlockedAsync(interactive: true).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>立即锁定。</summary>
    public async Task LockNowAsync()
    {
        if (_service?.LockNow() == true)
        {
            Notice = Strings.Get("MasterPassword_LockedNow");
        }
        await RefreshAsync().ConfigureAwait(true);
    }
}
