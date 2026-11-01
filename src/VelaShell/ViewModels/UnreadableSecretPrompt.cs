using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 登录框上「已存的密码解不开」那一句:是哪一份(连接自己的、还是共享凭据里的)、为什么解不开、怎么办。
/// </summary>
/// <remarks>
/// <para>
/// 「为什么」照本机加密密钥的解析结果说(<see cref="MasterKeyStatus" />,plan.md §253),与消息中心那几条同一口径:
/// 系统密钥链这次没打开 —— 密码没丢,解锁后重启就能照常读取,这次也可以先手输;
/// 加密它的密钥不在这台设备上(数据目录从别处拷来、条目被删、密钥文件被换过)—— 只能重新输入并存一份;
/// 解析结果说不出原因的(从别的设备同步来的密文、数据损坏)按后一种说。
/// </para>
/// <para>
/// 以前这种情形当成「有密码」,拿密文去试一次、认证失败才弹一个什么都不说的登录框(维护者 2026-10-25 定,第二轮第 20 条)。
/// 从窗口代码后置里拆出来,是为了不弹窗也能测。
/// </para>
/// </remarks>
public static class UnreadableSecretPrompt
{
    /// <summary>这次要用的那份已存机密在这台设备上解不开,登录框要说明一句。</summary>
    /// <param name="profile">要连接的配置。</param>
    /// <param name="credential">它引用的共享凭据;没引用或已删除时为 null。</param>
    /// <returns>要说明时为 true。</returns>
    public static bool Applies(SessionProfile profile, SharedCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AuthMethod == AuthMethod.Tailscale)
        {
            return false;
        }
        // 连接自己存的;引用共享凭据的配置自己不存密码,要看凭据里那份(本次在登录框里手输过的不算)。
        return profile.CredentialSource is null
            ? CredentialMaterial.HasUnreadableSecret(profile)
            : credential is not null && !CredentialMaterial.HasInline(profile) && CredentialMaterial.HasUnreadableSecret(credential);
    }

    /// <summary>说明文字。</summary>
    /// <param name="sharedCredentialName">解不开的是共享凭据里那份时为凭据名;连接自己存的为 null。</param>
    /// <param name="status">本机加密密钥的解析结果;拿不到时为 null(按说不出原因的那种说)。</param>
    /// <param name="masterPasswordLocked">主密码开着、此刻锁着(这时解不开就是因为锁着,先说这个)。</param>
    /// <returns>一段完整的说明。</returns>
    public static string Describe(string? sharedCredentialName, MasterKeyStatus? status, bool masterPasswordLocked = false)
    {
        string subject = sharedCredentialName is null
            ? Strings.Get("Auth_UnreadableSecretOwn")
            : Strings.Format("Auth_UnreadableSecretShared", sharedCredentialName);
        if (masterPasswordLocked)
        {
            return Strings.Format("Auth_UnreadableSecretLocked", subject);
        }
        if (status is { UnavailableKeyIds.Count: > 0 })
        {
            return Strings.Format("Auth_UnreadableSecretKeychainUnavailable", subject,
                status.KeychainName ?? string.Empty, status.KeychainError ?? string.Empty);
        }
        return status is { MissingKeyIds.Count: > 0 } or { ReplacedKeyFile: not null }
            ? Strings.Format("Auth_UnreadableSecretKeyMissing", subject)
            : Strings.Format("Auth_UnreadableSecretUnknown", subject);
    }
}
