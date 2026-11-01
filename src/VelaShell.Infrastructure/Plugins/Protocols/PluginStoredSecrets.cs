using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>
/// 交给插件之前的最后一道:已存的机密(密码、插件声明的机密字段)在这台设备上解不开 —— 载入时仍是密文原样(<see cref="StoredSecret" />)——
/// 时绝不原样交出去,也不交空串冒充「没设」:说清是哪一项,连接就此失败。
/// </summary>
/// <remarks>
/// <para>
/// 本机加密密钥换过、系统密钥链这次没能打开、数据从别的设备拷来,或者主密码锁着:仓储把解不开的机密原样交回(存回去时密文才不丢)。
/// 宿主自己的 SSH / FTP 早就认得这种值、不拿它去登录(0b3c6e6b);插件这几条路(文件协议、终端协议、工作台)以前照样把
/// <c>enc1:</c> 开头的密文当口令、令牌交给插件 —— 第三方代码拿到密文什么也做不了,只会拿它去试一次注定失败的认证;
/// 换成空串又让插件以为用户没填,转头去试匿名或者报一个误导人的错。
/// </para>
/// <para>
/// 界面上的入口在这之前自己先查一遍,按本机加密密钥的解析结果把原因说细(<c>UnreadableSecretPrompt</c>),密码那一项照旧由登录框问;
/// 走到这里的是不弹框的路(测试连接、后台重开、自动重连),报 <see cref="CredentialFailure.UnreadableSecret" />。
/// </para>
/// <para>
/// 只认插件声明过的机密字段:库里还留着、插件已经不声明的旧键插件不认识,不为它挡住连接 —— 但它解不开时同样不交出去
/// (<see cref="IsReadable" />,合并设置字典的两处都按它过滤)。
/// </para>
/// </remarks>
public static class PluginStoredSecrets
{
    /// <summary>这条配置要交给插件的已存机密里,在这台设备上解不开的那几项的显示名(按字段声明的顺序,密码在前)。</summary>
    /// <param name="profile">要交给插件的那份配置(共享凭据已解析)。</param>
    /// <param name="fields">插件声明的设置字段。</param>
    /// <param name="includePassword">这次会把密码交给插件(声明了 NoCredentials 的不交,也就不必查)。</param>
    /// <returns>解不开的各项的显示名;都解得开时为空。</returns>
    public static IReadOnlyList<string> UnreadableLabels(SessionProfile profile, IEnumerable<ProtocolSettingField> fields, bool includePassword)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(fields);
        List<string> labels = [];
        if (includePassword && StoredSecret.IsUnreadable(profile.Password))
        {
            labels.Add(Strings.Get("Plugin_SecretLabelPassword"));
        }
        if (profile.PluginSecrets is { Count: > 0 } secrets)
        {
            foreach (ProtocolSettingField field in fields)
            {
                if (secrets.TryGetValue(field.Key, out string? value) && StoredSecret.IsUnreadable(value))
                {
                    labels.Add(string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label);
                }
            }
        }
        return labels;
    }

    /// <summary>几项的显示名拼成一句里用的样子(「令牌」、「密码」)。</summary>
    /// <param name="labels">显示名。</param>
    /// <returns>拼好的一段。</returns>
    public static string FormatLabels(IEnumerable<string> labels) =>
        string.Join(Strings.Get("Plugin_SecretListSeparator"), labels.Select(static label => Strings.Format("Plugin_SecretLabelQuoted", label)));

    /// <summary>有解不开的就报 <see cref="CredentialFailure.UnreadableSecret" />,不交给插件。</summary>
    /// <param name="profile">要交给插件的那份配置(共享凭据已解析)。</param>
    /// <param name="fields">插件声明的设置字段。</param>
    /// <param name="includePassword">这次会把密码交给插件。</param>
    /// <exception cref="CredentialProviderException">有解不开的。</exception>
    public static void ThrowIfUnreadable(SessionProfile profile, IEnumerable<ProtocolSettingField> fields, bool includePassword)
    {
        IReadOnlyList<string> labels = UnreadableLabels(profile, fields, includePassword);
        if (labels.Count > 0)
        {
            throw new CredentialProviderException(
                CredentialFailure.UnreadableSecret,
                profile.CredentialSource?.ProviderId ?? string.Empty,
                Strings.Format("Plugin_StoredSecretUnreadable", FormatLabels(labels)));
        }
    }

    /// <summary>这个值可以交给插件:不是解不开的密文。</summary>
    /// <param name="value">已存的机密值。</param>
    /// <returns>可以交时为 true。</returns>
    internal static bool IsReadable(string? value) => !StoredSecret.IsUnreadable(value);
}
