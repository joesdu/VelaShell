using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 主窗口与可选主密码的接线:连接的各个构建点之前先过闸口,自动重连锁着时不弹框、记下来等解锁后重试一次,
/// 解锁 / 锁定时重读会话树(锁着时载入的密码是密文,锁定后树上不该还留着明文)。
/// </summary>
/// <remarks>
/// <para>
/// 交互式(用户主动连、手动重连、打开文档):锁着就弹解锁框(同一时刻只有一个框,启动时恢复十个标签也只弹一次);
/// 用户取消了,锁定前留在内存里的明文也不拿去用 —— 剥掉这一份的已存机密,登录框问这一次的密码,
/// 连上之后存回去时仓储照旧不动库里那一份(锁着加不了密)。
/// </para>
/// <para>
/// 非交互式(自动重连、睡眠唤醒后的批量重连、隧道自动重建):不弹框,标签的失败覆盖层说明已锁定,
/// 解锁成功后对这些挂起的标签各重试一次。理由见 velashell-docs《主密码保护设计》§6。
/// </para>
/// <para>
/// 闸口都插在既有构建点的最前面一行,不改构建点本身。
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    private readonly IMasterPasswordGate? _masterPassword;

    /// <summary>锁着时被拦下的自动重连:解锁后各重试一次。</summary>
    private readonly HashSet<TerminalTabViewModel> _reconnectAfterUnlock = [];

    /// <summary>主密码的闸口(状态栏的锁形入口、设置页用);没有时为 null。</summary>
    internal IMasterPasswordGate? MasterPassword => _masterPassword;

    private void WireMasterPassword(IMasterPasswordGate? gate)
    {
        if (gate is null)
        {
            return;
        }
        gate.Unlocked += (_, _) => RxSchedulers.MainThreadScheduler.Schedule(OnSecretsUnlocked);
        gate.Locked += (_, _) => RxSchedulers.MainThreadScheduler.Schedule(OnSecretsLocked);
        if (gate is IMasterPasswordService service)
        {
            service.StateChanged += (_, _) => RxSchedulers.MainThreadScheduler.Schedule(() => _ = RefreshMasterPasswordStatusAsync());
        }
    }

    /// <summary>
    /// 主窗口出现之后弹一次解锁框(维护者 2026-10-31 定:可取消;取消后状态栏留着锁形入口,点它再解锁)。
    /// 启动时恢复的那几个会话同时也在等解锁 —— 它们与这里共用同一个框。
    /// </summary>
    internal async Task PromptUnlockAtStartupAsync()
    {
        if (_masterPassword is not { } gate)
        {
            return;
        }
        try
        {
            await RefreshMasterPasswordStatusAsync().ConfigureAwait(true);
            if (await gate.IsLockedAsync().ConfigureAwait(true))
            {
                await gate.EnsureUnlockedAsync(interactive: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Secrets] The startup unlock prompt failed: {ex.Message}");
        }
    }

    /// <summary>状态栏的锁形入口:开没开、锁没锁(问状态可能要等密钥解析,放到线程池上问)。</summary>
    internal async Task RefreshMasterPasswordStatusAsync()
    {
        if (_masterPassword is not { } gate)
        {
            return;
        }
        (bool enabled, bool locked) = await Task.Run(() => (gate.IsEnabled, gate.IsLocked)).ConfigureAwait(true);
        StatusBar.MasterPasswordCommand ??= ReactiveCommand.CreateFromTask(ToggleMasterPasswordLockAsync);
        StatusBar.IsMasterPasswordEnabled = enabled;
        StatusBar.IsMasterPasswordLocked = locked;
    }

    /// <summary>点锁形入口:锁着 → 弹解锁框;解着 → 立即锁定。</summary>
    private async Task ToggleMasterPasswordLockAsync()
    {
        if (_masterPassword is not { } gate)
        {
            return;
        }
        if (await gate.IsLockedAsync().ConfigureAwait(true))
        {
            await gate.EnsureUnlockedAsync(interactive: true).ConfigureAwait(true);
        }
        else if (gate is IMasterPasswordService service && service.LockNow())
        {
            Toasts.Info(Strings.Get("MasterPassword_LockedNow"));
        }
        await RefreshMasterPasswordStatusAsync().ConfigureAwait(true);
    }

    /// <summary>解锁之后:重读会话树(锁着时载入的是密文),挂起的自动重连各重试一次。</summary>
    internal void OnSecretsUnlocked()
    {
        _ = RefreshMasterPasswordStatusAsync();
        _ = RefreshSessionTreeAsync();
        TerminalTabViewModel[] pending = [.. _reconnectAfterUnlock];
        _reconnectAfterUnlock.Clear();
        foreach (TerminalTabViewModel tab in pending)
        {
            if (FindDocument(tab) is not null && tab.ConnectionStatus == SessionStatus.Disconnected && !tab.UserRequestedDisconnect)
            {
                _ = AutoReconnectTabAsync(tab);
            }
        }
        TunnelPanel?.RetryAfterUnlock();
    }

    /// <summary>锁定之后:重读会话树,树上不再留着锁定前解开的明文(锁着时仓储交回的是密文)。</summary>
    internal void OnSecretsLocked()
    {
        _ = RefreshMasterPasswordStatusAsync();
        _ = RefreshSessionTreeAsync();
    }

    /// <summary>
    /// 交互式连接入口的闸口:这条连接要用已存的机密、主密码又锁着时先弹解锁框。
    /// </summary>
    /// <returns>
    /// 解开了(或本来就解着):这一份,锁着时载入的密文换成库里现在解得开的明文;
    /// 用户取消了解锁:剥掉已存机密的副本(登录框问这一次的密码)。
    /// </returns>
    private async Task<SessionProfile> UnlockForConnectAsync(SessionProfile profile)
    {
        if (_masterPassword is not { } gate || !MasterPasswordGuard.UsesSavedSecrets(profile))
        {
            return profile;
        }
        if (await gate.EnsureUnlockedAsync(interactive: true).ConfigureAwait(true))
        {
            return await RefreshUnreadableSecretsAsync(profile).ConfigureAwait(true);
        }
        Toasts.Info(Strings.Get("MasterPassword_ConnectWhileLocked"));
        return StripSavedSecrets(profile);
    }

    /// <summary>
    /// 重连(Ctrl+R / 回车 / 「重连」、自动重连)的闸口:手动重连锁着时弹框;自动重连不弹,标签记下来等解锁后重试。
    /// </summary>
    /// <returns>可以接着重连为 true;锁着没解开为 false(标签已经标成断开并说明原因)。</returns>
    private async Task<bool> UnlockForReconnectAsync(TerminalTabViewModel tab, bool automatic)
    {
        if (_masterPassword is not { } gate || tab.Profile is not { } profile || !MasterPasswordGuard.UsesSavedSecrets(profile))
        {
            return true;
        }
        if (await gate.EnsureUnlockedAsync(interactive: !automatic).ConfigureAwait(true))
        {
            await RefreshUnreadableSecretsAsync(profile).ConfigureAwait(true);
            return true;
        }
        if (automatic)
        {
            _reconnectAfterUnlock.Add(tab);
        }
        LastConnectionError = Strings.Get(automatic ? "MasterPassword_AutoReconnectLocked" : "MasterPassword_ReconnectLocked");
        tab.MarkDisconnected(LastConnectionError);
        UpdateStatusBarForActiveTab();
        return false;
    }

    /// <summary>
    /// 隧道面板后台连接的闸口:手动启动同 <see cref="UnlockForConnectAsync" />;自动重建锁着时不弹框,
    /// 抛 <see cref="SecretsLockedException" /> 交给面板说明并等解锁。
    /// </summary>
    private async Task<SessionProfile> UnlockForTunnelAsync(SessionProfile profile, bool automaticReconnect)
    {
        if (!automaticReconnect)
        {
            return await UnlockForConnectAsync(profile).ConfigureAwait(true);
        }
        if (_masterPassword is not { } gate || !MasterPasswordGuard.UsesSavedSecrets(profile))
        {
            return profile;
        }
        if (await gate.EnsureUnlockedAsync(interactive: false).ConfigureAwait(true))
        {
            return await RefreshUnreadableSecretsAsync(profile).ConfigureAwait(true);
        }
        throw new SecretsLockedException(Strings.Get("MasterPassword_TunnelLocked"));
    }

    /// <summary>
    /// 锁着时载入的那几项(仍是密文)在解锁之后换成库里现在解得开的明文;就地改(会话树缓存、标签那一份)。
    /// 已经是明文的(本次手输的)不动。不在库里的(临时连接)原样交回。
    /// </summary>
    private async Task<SessionProfile> RefreshUnreadableSecretsAsync(SessionProfile profile)
    {
        bool unreadable = StoredSecret.IsUnreadable(profile.Password) || StoredSecret.IsUnreadable(profile.PrivateKeyPassphrase)
                          || profile.PluginSecrets?.Values.Any(StoredSecret.IsUnreadable) == true;
        if (!unreadable || _sessionRepository is null)
        {
            return profile;
        }
        SessionProfile? stored;
        try
        {
            stored = await _sessionRepository.GetSessionAsync(profile.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Secrets] Could not re-read the saved secrets after unlocking: {ex.Message}");
            return profile;
        }
        if (stored is null)
        {
            return profile;
        }
        if (StoredSecret.IsUnreadable(profile.Password))
        {
            profile.Password = stored.Password;
        }
        if (StoredSecret.IsUnreadable(profile.PrivateKeyPassphrase))
        {
            profile.PrivateKeyPassphrase = stored.PrivateKeyPassphrase;
        }
        if (profile.PluginSecrets is { } secrets && stored.PluginSecrets is { } storedSecrets)
        {
            foreach (string name in secrets.Keys.Where(k => StoredSecret.IsUnreadable(secrets[k])).ToArray())
            {
                if (storedSecrets.TryGetValue(name, out string? value))
                {
                    secrets[name] = value;
                }
            }
        }
        return profile;
    }

    /// <summary>剥掉已存机密的一份副本(锁着、用户取消了解锁:这一次手输)。</summary>
    private static SessionProfile StripSavedSecrets(SessionProfile profile)
    {
        SessionProfile copy = profile.Clone();
        copy.Password = null;
        copy.PrivateKeyPassphrase = null;
        copy.PluginSecrets = null;
        return copy;
    }
}
