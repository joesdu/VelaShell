using System.Diagnostics;
using System.Security.Cryptography;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 可选主密码的闸口与管理入口:锁没锁、解锁(派生在线程池上做)、锁定,交互式解锁框的串行化。
/// </summary>
/// <remarks>
/// <para>
/// 事实来源是 <see cref="AesSecretProtector" /> 里的 <see cref="MasterKeyVault" />(包裹格式本身),这里不另存任何状态,
/// 设置里的 <c>MasterPasswordProtection</c> 改成什么都不影响它。
/// </para>
/// <para>
/// 解锁框只有一个:<see cref="EnsureUnlockedAsync" /> 的并发交互式请求共用同一个框的结果;用户取消后排着的请求一起拿到 false,
/// 不再逐个弹。界面层注册 <see cref="IMasterPasswordPrompt" />(容器里取不到时 —— headless、测试 —— 交互式请求也只能是 false)。
/// </para>
/// </remarks>
public sealed partial class MasterPasswordService : IMasterPasswordService
{
    private readonly AesSecretProtector _protector;
    private readonly Func<IMasterPasswordPrompt?> _prompt;
    private readonly Lock _promptGate = new();
    private Task<bool>? _pendingPrompt;

    /// <summary>构造。</summary>
    /// <param name="protector">应用的机密保护器(密钥与主密码那一层在它里面)。</param>
    /// <param name="prompt">取界面层的解锁框;null 或取不到时交互式请求一律为 false。</param>
    public MasterPasswordService(AesSecretProtector protector, Func<IMasterPasswordPrompt?>? prompt = null)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _prompt = prompt ?? (static () => null);
    }

    private MasterKeyVault Vault => _protector.Vault;

    /// <inheritdoc />
    public event EventHandler? Unlocked;

    /// <inheritdoc />
    public event EventHandler? Locked;

    /// <inheritdoc />
    public bool IsEnabled => Vault.IsProtected;

    /// <inheritdoc />
    public bool IsLocked => Vault.IsLocked;

    /// <inheritdoc />
    public bool HasRecoveryCode => Vault.HasRecoveryCode;

    /// <inheritdoc />
    public bool UnlockedWithRecoveryCode => Vault.UnlockedWithRecoveryCode;

    /// <inheritdoc />
    public TimeSpan RetryAfter => Vault.RetryAfter;

    /// <inheritdoc />
    public MasterKeyStatus GetStatus() => Vault.Status;

    /// <inheritdoc />
    public async Task<bool> EnsureUnlockedAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        if (!await this.IsLockedAsync().ConfigureAwait(false))
        {
            return true;
        }
        if (!interactive)
        {
            return false;
        }
        Task<bool> prompt;
        lock (_promptGate)
        {
            if (_pendingPrompt is not { IsCompleted: false })
            {
                _pendingPrompt = RunPromptAsync();
            }
            prompt = _pendingPrompt;
        }
        bool unlocked = await prompt.WaitAsync(cancellationToken).ConfigureAwait(false);
        return unlocked && !IsLocked;
    }

    private async Task<bool> RunPromptAsync()
    {
        // 让出一次:调用方先把这个任务挂到 _pendingPrompt 上,别的请求再进来时看得见它。
        await Task.Yield();
        if (_prompt() is not { } prompt)
        {
            return false;
        }
        try
        {
            return await prompt.PromptUnlockAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Secrets] The unlock prompt failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public Task<MasterPasswordUnlockResult> UnlockAsync(byte[] passwordUtf8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passwordUtf8);
        Task<MasterPasswordUnlockResult> work = Task.Run(() =>
        {
            try
            {
                return Finish(Vault.Unlock(passwordUtf8));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordUtf8);
            }
        }, CancellationToken.None);
        return work.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<MasterPasswordUnlockResult> UnlockWithRecoveryCodeAsync(string recoveryCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryCode);
        return Task.Run(() => Finish(Vault.UnlockWithRecoveryCode(recoveryCode)), CancellationToken.None).WaitAsync(cancellationToken);
    }

    /// <summary>解开了就广播(在线程池上;调用方取消了等待也照样广播 —— 解锁已经生效)。</summary>
    private MasterPasswordUnlockResult Finish(MasterPasswordUnlockResult result)
    {
        if (result.Status == MasterPasswordUnlockStatus.Unlocked)
        {
            RaiseUnlocked();
        }
        return result;
    }

    private void RaiseUnlocked()
    {
        try
        {
            Unlocked?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Secrets] An unlock listener failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public bool LockNow()
    {
        if (!Vault.Lock())
        {
            return false;
        }
        Trace.WriteLine("[Secrets] Locked: the local encryption keys were cleared from memory");
        try
        {
            Locked?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Secrets] A lock listener failed: {ex.Message}");
        }
        return true;
    }
}
