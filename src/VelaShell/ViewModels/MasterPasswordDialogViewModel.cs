using System.Security;
using ReactiveUI;
using VelaShell.Core.Data;
using VelaShell.Core.Resources;
using VelaShell.Security;

namespace VelaShell.ViewModels;

/// <summary>主密码对话框的几种样子。</summary>
public enum MasterPasswordDialogMode
{
    /// <summary>解锁(主密码或恢复码)。</summary>
    Unlock,

    /// <summary>开启。</summary>
    Enable,

    /// <summary>修改。</summary>
    Change,

    /// <summary>关闭。</summary>
    Disable,

    /// <summary>开启之后给用户抄恢复码。</summary>
    ShowRecoveryCode,

    /// <summary>用恢复码解开之后设新的主密码。</summary>
    SetAfterRecovery,

    /// <summary>忘了主密码:说明后果、确认后另起新密钥。</summary>
    Forget
}

/// <summary>
/// 主密码对话框(解锁 / 开启 / 修改 / 关闭 / 抄恢复码 / 恢复后设新密码 / 忘记)的视图模型。
/// </summary>
/// <remarks>
/// <para>
/// 口令走 <see cref="SecureString" />(<c>SecurePasswordBox</c>:不进剪贴板、不进历史),交给服务时才编成 UTF-8 字节,服务用完清零。
/// 派生在线程池上做(64 MiB、几百毫秒),这期间 <see cref="IsBusy" /> 为 true、按钮变灰。
/// </para>
/// <para>
/// 输错之后服务按 1、2、4… 秒退避(上限 30 秒),这里把剩余秒数倒数给用户看,倒数完才能再试 —— 不锁死、不删任何东西。
/// </para>
/// </remarks>
public sealed class MasterPasswordDialogViewModel : ReactiveObject
{
    private readonly IMasterPasswordService _service;
    private CancellationTokenSource? _countdown;

    /// <summary>构造。</summary>
    /// <param name="service">主密码服务。</param>
    /// <param name="mode">一开始的样子。</param>
    public MasterPasswordDialogViewModel(IMasterPasswordService service, MasterPasswordDialogMode mode)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Mode = mode;
        HasRecoveryCodeOnFile = service.HasRecoveryCode;
        if (mode == MasterPasswordDialogMode.Unlock)
        {
            ProblemText = DescribeProblem(service.GetStatus());
        }
        if (service.RetryAfter > TimeSpan.Zero)
        {
            StartCountdown(service.RetryAfter);
        }
    }

    /// <summary>此刻的样子。</summary>
    public MasterPasswordDialogMode Mode
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ErrorText = null;
            foreach (string name in new[]
                     {
                         nameof(Title), nameof(Description), nameof(ConfirmText), nameof(CancelText), nameof(ShowsPassword),
                         nameof(ShowsNewPassword), nameof(ShowsRecoveryInput), nameof(ShowsRecoveryDisplay), nameof(ShowsEnableOptions),
                         nameof(ShowsUnlockLinks), nameof(IsDestructive), nameof(PasswordLabel), nameof(ShowsStrengthHint)
                     })
            {
                this.RaisePropertyChanged(name);
            }
        }
    }

    /// <summary>标题。</summary>
    public string Title => Strings.Get(Mode switch
    {
        MasterPasswordDialogMode.Enable => "MasterPassword_EnableTitle",
        MasterPasswordDialogMode.Change => "MasterPassword_ChangeTitle",
        MasterPasswordDialogMode.Disable => "MasterPassword_DisableTitle",
        MasterPasswordDialogMode.ShowRecoveryCode => "MasterPassword_RecoveryTitle",
        MasterPasswordDialogMode.SetAfterRecovery => "MasterPassword_AfterRecoveryTitle",
        MasterPasswordDialogMode.Forget => "MasterPassword_ForgetTitle",
        _ => "MasterPassword_UnlockTitle"
    });

    /// <summary>说明。</summary>
    public string Description => Strings.Get(Mode switch
    {
        MasterPasswordDialogMode.Enable => "MasterPassword_EnableDesc",
        MasterPasswordDialogMode.Change => "MasterPassword_ChangeDesc",
        MasterPasswordDialogMode.Disable => "MasterPassword_DisableDesc",
        MasterPasswordDialogMode.ShowRecoveryCode => "MasterPassword_RecoveryDesc",
        MasterPasswordDialogMode.SetAfterRecovery => "MasterPassword_AfterRecoveryDesc",
        MasterPasswordDialogMode.Forget => "MasterPassword_ForgetDesc",
        _ => "MasterPassword_UnlockDesc"
    });

    /// <summary>确定按钮的字。</summary>
    public string ConfirmText => Strings.Get(Mode switch
    {
        MasterPasswordDialogMode.Enable => "MasterPassword_EnableButton",
        MasterPasswordDialogMode.Change => "MasterPassword_ChangeButton",
        MasterPasswordDialogMode.Disable => "MasterPassword_DisableButton",
        MasterPasswordDialogMode.ShowRecoveryCode => "MasterPassword_RecoveryWritten",
        MasterPasswordDialogMode.SetAfterRecovery => "MasterPassword_SetNewButton",
        MasterPasswordDialogMode.Forget => "MasterPassword_ForgetButton",
        _ => "MasterPassword_UnlockButton"
    });

    /// <summary>取消按钮的字(恢复后设新密码那一步是「以后再说」:已经解开了)。</summary>
    public string CancelText => Strings.Get(Mode == MasterPasswordDialogMode.SetAfterRecovery ? "MasterPassword_Skip" : "Cancel");

    /// <summary>主密码输入框上方的字。</summary>
    public string PasswordLabel => Strings.Get(Mode is MasterPasswordDialogMode.Change or MasterPasswordDialogMode.Disable
        ? "MasterPassword_CurrentPasswordLabel"
        : "MasterPassword_PasswordLabel");

    /// <summary>显示主密码(当前主密码)输入框。</summary>
    public bool ShowsPassword => Mode switch
    {
        MasterPasswordDialogMode.Unlock => !UseRecoveryCode,
        MasterPasswordDialogMode.Change or MasterPasswordDialogMode.Disable => true,
        _ => false
    };

    /// <summary>显示新主密码与再输一次。</summary>
    public bool ShowsNewPassword => Mode is MasterPasswordDialogMode.Enable or MasterPasswordDialogMode.Change or MasterPasswordDialogMode.SetAfterRecovery;

    /// <summary>显示强度提示(开启、修改、恢复后设新密码时)。</summary>
    public bool ShowsStrengthHint => ShowsNewPassword;

    /// <summary>显示恢复码输入框。</summary>
    public bool ShowsRecoveryInput => Mode == MasterPasswordDialogMode.Unlock && UseRecoveryCode;

    /// <summary>显示生成出来的恢复码。</summary>
    public bool ShowsRecoveryDisplay => Mode == MasterPasswordDialogMode.ShowRecoveryCode;

    /// <summary>显示开启时的两个勾选。</summary>
    public bool ShowsEnableOptions => Mode == MasterPasswordDialogMode.Enable;

    /// <summary>显示解锁框底部的两个链接(用恢复码 / 忘了主密码)。</summary>
    public bool ShowsUnlockLinks => Mode == MasterPasswordDialogMode.Unlock;

    /// <summary>确定是破坏性的(忘记、关闭):按钮按 DESIGN.md §5.1 画成红框。</summary>
    public bool IsDestructive => Mode is MasterPasswordDialogMode.Forget or MasterPasswordDialogMode.Disable;

    /// <summary>主密码 / 当前主密码。</summary>
    public SecureString? Password
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>新主密码。</summary>
    public SecureString? NewPassword
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsWeak));
        }
    }

    /// <summary>再输一次。</summary>
    public SecureString? ConfirmPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>新主密码短于 12 个字符(只提示,不拦:离线猜测只能靠长度挡住)。</summary>
    public bool IsWeak => (NewPassword?.Length ?? 0) is > 0 and < 12;

    /// <summary>解锁时改用恢复码。</summary>
    public bool UseRecoveryCode
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ErrorText = null;
            this.RaisePropertyChanged(nameof(ShowsPassword));
            this.RaisePropertyChanged(nameof(ShowsRecoveryInput));
            this.RaisePropertyChanged(nameof(RecoveryToggleText));
        }
    }

    /// <summary>「用恢复码解锁」/「用主密码解锁」那个链接的字。</summary>
    public string RecoveryToggleText => Strings.Get(UseRecoveryCode ? "MasterPassword_UsePassword" : "MasterPassword_UseRecoveryCode");

    /// <summary>恢复码输入。</summary>
    public string RecoveryCodeInput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>开启时同时生成恢复码(默认勾上)。</summary>
    public bool CreateRecoveryCode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>开启时确认「忘了就无法找回」(不勾不让开)。</summary>
    public bool AcknowledgeNoBackdoor
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>这份数据目录有恢复码(解锁框据此显示「用恢复码解锁」)。</summary>
    public bool HasRecoveryCodeOnFile { get; private set; }

    /// <summary>开启后生成的恢复码(给用户抄)。</summary>
    public string? RecoveryCodeDisplay
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>解不了锁的原因(头不在、密钥链没打开);没有问题时为 null。</summary>
    public string? ProblemText { get; }

    /// <summary>有解不了锁的原因(这时只能取消或「忘了主密码」)。</summary>
    public bool HasProblem => ProblemText is not null;

    /// <summary>错误 / 结果说明。</summary>
    public string? ErrorText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>正在派生密钥 / 写存储。</summary>
    public bool IsBusy
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    /// <summary>输错之后还要等几秒(0 = 不用等)。</summary>
    public int WaitSeconds
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    /// <summary>确定按钮能不能按。</summary>
    public bool CanConfirm => !IsBusy && WaitSeconds == 0;

    /// <summary>这一次对话框的结果:解开了 / 做完了。</summary>
    public bool Succeeded { get; private set; }

    /// <summary>做完之后给主窗口的一句提示(开启、关闭、修改、忘记);没有时为 null。</summary>
    public string? CompletionNotice { get; private set; }

    /// <summary>切到「忘了主密码」。</summary>
    public void BeginForget() => Mode = MasterPasswordDialogMode.Forget;

    /// <summary>按下确定。</summary>
    /// <returns>该关掉对话框为 true;还要留着(输错、换到下一步)为 false。</returns>
    public async Task<bool> ConfirmAsync()
    {
        if (!CanConfirm)
        {
            return false;
        }
        ErrorText = null;
        switch (Mode)
        {
            case MasterPasswordDialogMode.ShowRecoveryCode:
                return true;
            case MasterPasswordDialogMode.Unlock when HasProblem:
                ErrorText = ProblemText;
                return false;
        }
        if (ValidateInput() is { } invalid)
        {
            ErrorText = invalid;
            return false;
        }
        IsBusy = true;
        try
        {
            return Mode switch
            {
                MasterPasswordDialogMode.Unlock => UseRecoveryCode ? await UnlockWithRecoveryAsync() : await UnlockAsync(),
                MasterPasswordDialogMode.Enable => await EnableAsync(),
                MasterPasswordDialogMode.Change => Finish(await _service.ChangeAsync(SecureStringConvert.ToUtf8(Password),
                    SecureStringConvert.ToUtf8(NewPassword)), "MasterPassword_Changed"),
                MasterPasswordDialogMode.Disable => Finish(await _service.DisableAsync(SecureStringConvert.ToUtf8(Password)), "MasterPassword_Disabled"),
                MasterPasswordDialogMode.SetAfterRecovery => Finish(await _service.SetPasswordAfterRecoveryAsync(SecureStringConvert.ToUtf8(NewPassword)),
                    "MasterPassword_Changed"),
                MasterPasswordDialogMode.Forget => Finish(await _service.ForgetAsync(), "MasterPassword_Forgotten"),
                _ => false
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>取消(恢复后设新密码那一步取消 = 先不设,已经解开了)。</summary>
    public void Cancel()
    {
        if (Mode == MasterPasswordDialogMode.SetAfterRecovery)
        {
            Succeeded = true;
        }
        _countdown?.Cancel();
    }

    private string? ValidateInput()
    {
        if (ShowsPassword && (Password?.Length ?? 0) == 0)
        {
            return Strings.Get("MasterPassword_Empty");
        }
        if (ShowsRecoveryInput && string.IsNullOrWhiteSpace(RecoveryCodeInput))
        {
            return Strings.Get("MasterPassword_MalformedRecoveryCode");
        }
        if (ShowsNewPassword)
        {
            if ((NewPassword?.Length ?? 0) == 0)
            {
                return Strings.Get("MasterPassword_Empty");
            }
            byte[] next = SecureStringConvert.ToUtf8(NewPassword);
            byte[] again = SecureStringConvert.ToUtf8(ConfirmPassword);
            bool same = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(next, again);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(next);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(again);
            if (!same)
            {
                return Strings.Get("MasterPassword_Mismatch");
            }
        }
        if (Mode == MasterPasswordDialogMode.Enable && !AcknowledgeNoBackdoor)
        {
            return Strings.Get("MasterPassword_AcknowledgeRequired");
        }
        return null;
    }

    private async Task<bool> UnlockAsync()
    {
        MasterPasswordUnlockResult result = await _service.UnlockAsync(SecureStringConvert.ToUtf8(Password));
        return AfterUnlock(result, recovery: false);
    }

    private async Task<bool> UnlockWithRecoveryAsync()
    {
        MasterPasswordUnlockResult result = await _service.UnlockWithRecoveryCodeAsync(RecoveryCodeInput);
        return AfterUnlock(result, recovery: true);
    }

    private bool AfterUnlock(MasterPasswordUnlockResult result, bool recovery)
    {
        switch (result.Status)
        {
            case MasterPasswordUnlockStatus.Unlocked when recovery:
                Succeeded = true;
                RecoveryCodeInput = string.Empty;
                Mode = MasterPasswordDialogMode.SetAfterRecovery;
                return false;
            case MasterPasswordUnlockStatus.Unlocked or MasterPasswordUnlockStatus.AlreadyUnlocked or MasterPasswordUnlockStatus.NotEnabled:
                Succeeded = true;
                return true;
            case MasterPasswordUnlockStatus.WrongSecret:
                ErrorText = Strings.Get(recovery ? "MasterPassword_WrongRecoveryCode" : "MasterPassword_WrongPassword");
                Password = null;
                StartCountdown(result.RetryAfter);
                return false;
            case MasterPasswordUnlockStatus.Throttled:
                StartCountdown(result.RetryAfter);
                return false;
            case MasterPasswordUnlockStatus.MalformedRecoveryCode:
                ErrorText = Strings.Get("MasterPassword_MalformedRecoveryCode");
                return false;
            case MasterPasswordUnlockStatus.NoRecoveryCode:
                ErrorText = Strings.Get("MasterPassword_NoRecoveryCode");
                return false;
            default:
                ErrorText = DescribeProblem(_service.GetStatus()) ?? Strings.Get("MasterPassword_NothingToUnlock");
                return false;
        }
    }

    private async Task<bool> EnableAsync()
    {
        MasterPasswordChangeResult result = await _service.EnableAsync(SecureStringConvert.ToUtf8(NewPassword), CreateRecoveryCode);
        if (!Finish(result, "MasterPassword_Enabled"))
        {
            return false;
        }
        if (result.RecoveryCode is { } code)
        {
            RecoveryCodeDisplay = code;
            Mode = MasterPasswordDialogMode.ShowRecoveryCode;
            return false;
        }
        return true;
    }

    /// <summary>开启 / 关闭 / 修改 / 忘记的结果:做完了记下提示并关框,否则把原因摆在框上。</summary>
    private bool Finish(MasterPasswordChangeResult result, string doneKey)
    {
        switch (result.Status)
        {
            case MasterPasswordChangeStatus.Done:
                Succeeded = true;
                CompletionNotice = doneKey == "MasterPassword_Forgotten"
                    ? Strings.Format(doneKey, result.Detail ?? string.Empty)
                    : Strings.Get(doneKey);
                return true;
            case MasterPasswordChangeStatus.WrongPassword:
                ErrorText = Strings.Get("MasterPassword_WrongPassword");
                Password = null;
                StartCountdown(result.RetryAfter);
                return false;
            case MasterPasswordChangeStatus.Throttled:
                StartCountdown(result.RetryAfter);
                return false;
            case MasterPasswordChangeStatus.KeysUnavailable:
                ErrorText = Strings.Get("MasterPassword_KeysUnavailable");
                return false;
            case MasterPasswordChangeStatus.StorageFailedPartially:
                // 已经按开着处理:框关掉,提示要用刚输入的主密码。
                Succeeded = true;
                CompletionNotice = Strings.Format("MasterPassword_StorageFailedPartially", result.Detail ?? string.Empty);
                return true;
            default:
                ErrorText = Strings.Format("MasterPassword_StorageFailed", result.Detail ?? string.Empty);
                return false;
        }
    }

    /// <summary>倒数输错之后的等待(UI 线程上,一秒一跳)。</summary>
    private void StartCountdown(TimeSpan wait)
    {
        _countdown?.Cancel();
        if (wait <= TimeSpan.Zero)
        {
            WaitSeconds = 0;
            return;
        }
        var cts = new CancellationTokenSource();
        _countdown = cts;
        _ = CountdownAsync((int)Math.Ceiling(wait.TotalSeconds), cts.Token);
    }

    private async Task CountdownAsync(int seconds, CancellationToken token)
    {
        string? prefix = ErrorText;
        try
        {
            for (int left = seconds; left > 0; left--)
            {
                WaitSeconds = left;
                ErrorText = (prefix is null ? string.Empty : prefix + " ") + Strings.Format("MasterPassword_RetryIn", left);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            ErrorText = prefix;
        }
        catch (OperationCanceledException)
        {
            // 对话框关了。
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                WaitSeconds = 0;
            }
        }
    }

    /// <summary>解不了锁的原因(给解锁框与消息中心);没有问题时为 null。</summary>
    internal static string? DescribeProblem(MasterKeyStatus status) => status.MasterPasswordProblem switch
    {
        MasterPasswordProblem.HeaderMissing => Strings.Get("MasterPassword_ProblemHeaderMissing"),
        MasterPasswordProblem.HeaderUnreadable => Strings.Get("MasterPassword_ProblemHeaderUnreadable"),
        MasterPasswordProblem.KeysUnavailable => Strings.Format("MasterPassword_ProblemKeysUnavailable",
            status.KeychainName ?? string.Empty, status.KeychainError ?? string.Empty),
        _ => null
    };
}
