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
/// 入口的闸口都插在既有构建点的最前面一行,不改构建点本身。重连例外:它先按 Id 从库里重读,闸口看重读之后的那份
/// (<see cref="ResolveUnlockedReconnectProfileAsync" />)。
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
        SessionProfile stripped = await StripSavedSecretsAsync(profile).ConfigureAwait(true);
        if (stripped.PluginSecrets is not { Count: > 0 })
        {
            // 插件机密被锁着时这次连不成(连接前的检查会说明「被主密码锁着」),不先说一句「请在登录框里手输」。
            Toasts.Info(Strings.Get("MasterPassword_ConnectWhileLocked"));
        }
        return stripped;
    }

    /// <summary>
    /// 重连(Ctrl+R / 回车 / 「重连」、自动重连)要用的那份配置,过了主密码的闸口:先按 Id 从库里重读
    /// (<see cref="ResolveReconnectProfileAsync" />),再看<b>重读之后</b>的那份用不用得到已存的机密 ——
    /// 标签开着时库里那份可能刚被改成存着密码(编辑框、云同步),只看标签副本就会把它放过去,锁着时拿密文去连
    /// (SSH 误报「解不开」,插件终端则是把密文原样交给插件)。手动重连锁着时弹框;自动重连不弹,标签记下来等解锁后重试。
    /// </summary>
    /// <param name="tab">要重连的标签。</param>
    /// <param name="copy">标签手里那份(不改动)。</param>
    /// <param name="automatic">自动重连(不弹框)。</param>
    /// <returns>
    /// 可以接着重连:要连的那份(这一次刚解开的话按库里重读过一遍,锁着时载入的密文换成了明文);
    /// 锁着没解开为 null(标签已经标成断开并说明原因,标签上那份不动)。
    /// </returns>
    private async Task<SessionProfile?> ResolveUnlockedReconnectProfileAsync(TerminalTabViewModel tab, SessionProfile copy, bool automatic)
    {
        SessionProfile profile = await ResolveReconnectProfileAsync(tab, copy).ConfigureAwait(true);
        if (_masterPassword is not { } gate || !MasterPasswordGuard.UsesSavedSecrets(profile))
        {
            return profile;
        }
        bool wasLocked = await gate.IsLockedAsync().ConfigureAwait(true);
        if (await gate.EnsureUnlockedAsync(interactive: !automatic).ConfigureAwait(true))
        {
            if (wasLocked)
            {
                // 这一次才解开:上面那份是锁着时读的,库里的机密还是密文,ForReconnect 也就让标签上的现敲值顶了上来。
                // 现在库里那份解得开了,以库里为准再读一遍(与没锁时重连拿到的一样)。
                profile = await ResolveReconnectProfileAsync(tab, copy).ConfigureAwait(true);
            }
            return await RefreshUnreadableSecretsAsync(profile).ConfigureAwait(true);
        }
        if (automatic)
        {
            _reconnectAfterUnlock.Add(tab);
        }
        LastConnectionError = Strings.Get(automatic ? "MasterPassword_AutoReconnectLocked" : "MasterPassword_ReconnectLocked");
        tab.MarkDisconnected(LastConnectionError);
        UpdateStatusBarForActiveTab();
        return null;
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

    /// <summary>
    /// 剥掉已存机密的一份副本(锁着、用户取消了解锁):密码与私钥口令这一次由登录框问。
    /// </summary>
    /// <remarks>
    /// 插件声明的机密字段没有登录框可问,剥成空就等于对插件装作「用户没填」。换成库里那一份里解不开的几项(锁着时就是全部,仍是密文原样):
    /// 连接前的检查(<see cref="UnreadablePluginSecretsAsync" />)据此说明它们被主密码锁着、这次不连,协议服务那一道也绝不把密文交出去。
    /// 只留解不开的:锁定前留在内存里的明文不拿去用,这一份也绝不能把明文带回来。不在库里的(临时配置)只能剥掉。
    /// </remarks>
    private async Task<SessionProfile> StripSavedSecretsAsync(SessionProfile profile)
    {
        SessionProfile copy = profile.Clone();
        copy.Password = null;
        copy.PrivateKeyPassphrase = null;
        copy.PluginSecrets = profile.PluginSecrets is { Count: > 0 }
            ? await LockedPluginSecretsAsync(profile.Id).ConfigureAwait(true)
            : null;
        return copy;
    }

    /// <summary>库里那份配置的插件机密里解不开的几项(锁着时载入的密文原样);没有、不在库里或读不出来时为 null。</summary>
    private async Task<Dictionary<string, string>?> LockedPluginSecretsAsync(Guid profileId)
    {
        if (_sessionRepository is null)
        {
            return null;
        }
        try
        {
            SessionProfile? stored = await _sessionRepository.GetSessionAsync(profileId).ConfigureAwait(true);
            Dictionary<string, string>? kept = stored?.PluginSecrets?
                .Where(static entry => StoredSecret.IsUnreadable(entry.Value))
                .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
            return kept is { Count: > 0 } ? kept : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine($"[Secrets] Could not read the locked plugin secrets of {profileId}: {ex.Message}");
            return null;
        }
    }
}
