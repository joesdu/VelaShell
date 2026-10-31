using VelaShell.Core.Credentials;
using VelaShell.Core.Data;

namespace VelaShell.Core.Models;

/// <summary>
/// 连接手里那份配置副本与库里那份之间的两条规则:终端标签重连时按哪份连,登录框交回的那份连上之后往库里写哪几项。
/// </summary>
/// <remarks>
/// <para>
/// 终端标签开着的这段时间里,库里那份可能已经被别处改过 —— 云同步拉下来的、编辑框里存的、会话健康巡检窗口里
/// 「放开这几个算法」写进去的。以前重连一律拿标签手里的副本去连,连上之后再把这份副本整份写回库里,
/// 别处刚改的就被旧副本盖掉了(plan.md §298 记下的欠账,维护者 2026-10-31 定:重连从库里重读,只叠上现敲的凭据)。
/// </para>
/// <para>
/// 两条规则放在一处:「哪些是这次会话现敲、没被记住的」与「登录框改得到哪几项」说的是同一组字段 ——
/// 用户名、认证方式、记住密码与认证材料(<see cref="SessionProfile.Password" /> / 私钥路径 / 私钥口令 / 证书路径)。
/// </para>
/// </remarks>
public static class SessionProfileMerge
{
    /// <summary>
    /// 终端标签重连要用的那份:库里最新那份的副本,只叠上标签副本里这次会话现敲、库里没有的认证材料。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>以库里为准</b>:地址、端口、算法、跳板、认证后命令、初始目录、终端覆盖……一律取库里那份;
    /// 库里存着的凭据也压过标签上的(那是更新的,或者本来就是同一份)。
    /// </para>
    /// <para>
    /// <b>只叠库里没有的</b>,而且只在<b>登录身份没变</b>时叠(连接类型、主机、端口、用户名、认证方式、凭据引用都相同):
    /// 别处把这条配置指向了另一台主机、另一个账号,这次会话里为原来那个账号敲的口令就不该跟过去 ——
    /// 尤其是指向另一台主机时,那等于把口令交给了别人。
    /// </para>
    /// <list type="bullet">
    /// <item>密码认证:库里没有密码(没勾记住密码、常规页的总开关关着,或者存着的那份在这台设备上解不开)时用标签上的。</item>
    /// <item>私钥 / 证书认证:私钥路径相同、库里没有口令时用标签上的口令。私钥路径与证书路径是配置不是机密,不从标签上搬。</item>
    /// <item>引用共享凭据的配置:库里从不存认证材料(仓储强制,#550),标签上有的只可能是登录框里手输的那一份,原样叠上。</item>
    /// </list>
    /// <para>
    /// 动态码一类 keyboard-interactive 的回答不在配置里(每次连接现问),这里无事可做。
    /// </para>
    /// </remarks>
    /// <param name="stored">库里最新那份(不改动)。</param>
    /// <param name="session">标签手里那份(上一次连接用的,可能带着现敲的凭据;不改动)。</param>
    /// <returns>新的一份,不与两者共享任何可变对象。</returns>
    public static SessionProfile ForReconnect(SessionProfile stored, SessionProfile session)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(session);
        SessionProfile merged = stored.Clone();
        if (!SameLogin(stored, session))
        {
            return merged;
        }
        if (merged.CredentialSource is not null)
        {
            if (!CredentialMaterial.HasInline(merged) && CredentialMaterial.HasInline(session))
            {
                merged.Password = session.Password;
                merged.PrivateKeyPath = session.PrivateKeyPath;
                merged.PrivateKeyPassphrase = session.PrivateKeyPassphrase;
                merged.CertificatePath = session.CertificatePath;
            }
            return merged;
        }
        switch (merged.AuthMethod)
        {
            case AuthMethod.Password when IsMissing(merged.Password) && IsUsable(session.Password):
                merged.Password = session.Password;
                break;
            case AuthMethod.PrivateKey or AuthMethod.Certificate
                when string.Equals(merged.PrivateKeyPath, session.PrivateKeyPath, StringComparison.Ordinal)
                     && IsMissing(merged.PrivateKeyPassphrase) && IsUsable(session.PrivateKeyPassphrase):
                merged.PrivateKeyPassphrase = session.PrivateKeyPassphrase;
                break;
        }
        return merged;
    }

    /// <summary>
    /// 登录框改得到的那几项从 <paramref name="source" /> 抄到 <paramref name="target" />:用户名、认证方式、记住密码、
    /// 密码、私钥路径、私钥口令、证书路径。其余字段一个不碰。
    /// </summary>
    /// <remarks>
    /// 登录框交回的那份连上之后,工作流按它把库里那份的这几项改掉(以前是把登录框那份整份写回去,
    /// 连带着它从会话树缓存里继承来的、可能已经过时的其余字段)。密码落不落盘照旧由「记住密码」与常规页的总开关决定,
    /// 引用共享凭据的配置不存认证材料、Tailscale 不存任何认证材料,都由仓储把关。
    /// </remarks>
    /// <param name="source">登录框交回、刚连上的那份。</param>
    /// <param name="target">库里那份(就地修改)。</param>
    public static void CopyLoginFields(SessionProfile source, SessionProfile target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        target.Username = source.Username;
        target.AuthMethod = source.AuthMethod;
        target.RememberPassword = source.RememberPassword;
        target.Password = source.Password;
        target.PrivateKeyPath = source.PrivateKeyPath;
        target.PrivateKeyPassphrase = source.PrivateKeyPassphrase;
        target.CertificatePath = source.CertificatePath;
        // 登录框里切到 Tailscale 时,交回的那份已经去掉了凭据引用与认证材料(见 CredentialMaterial.ClearTailscaleCredentials)。
        CredentialMaterial.ClearTailscaleCredentials(target);
    }

    /// <summary>登录身份没变:同一种连接、同一台主机的同一个端口、同一个账号、同一种认证、同一条凭据引用。</summary>
    private static bool SameLogin(SessionProfile stored, SessionProfile session) =>
        stored.ConnectionType == session.ConnectionType
        && string.Equals(stored.Host, session.Host, StringComparison.OrdinalIgnoreCase)
        && stored.Port == session.Port
        && string.Equals(stored.Username, session.Username, StringComparison.Ordinal)
        && stored.AuthMethod == session.AuthMethod
        && Equals(stored.CredentialSource, session.CredentialSource);

    /// <summary>库里这一项等于没有:空,或者是这台设备上解不开的密文(<see cref="StoredSecret" />)。</summary>
    private static bool IsMissing(string? secret) => string.IsNullOrEmpty(secret) || StoredSecret.IsUnreadable(secret);

    /// <summary>标签上这一项能拿来用:非空,也不是解不开的密文。</summary>
    private static bool IsUsable(string? secret) => !IsMissing(secret);
}
