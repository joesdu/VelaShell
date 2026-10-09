using VelaShell.Core.Models;

namespace VelaShell.Core.Credentials;

/// <summary>哪种连接能用哪种认证方式:SSH / SFTP 全都行,FTP 与插件协议只认密码,匿名 FTP 什么凭据都不发。</summary>
/// <remarks>
/// 共享凭据的「能不能给这条连接用」、批量修改认证、CSV 导入的校验共用这一条规则 ——
/// 三处各写一份,迟早会出现「批量改得进去、连接对话框却打不开」的那种不一致。
/// </remarks>
public static class CredentialCompatibility
{
    /// <summary>这种连接支不支持这种认证方式。</summary>
    /// <param name="type">连接类型。</param>
    /// <param name="method">认证方式。</param>
    /// <param name="anonymousFtp">是不是匿名 FTP(匿名时不发任何凭据)。</param>
    /// <returns>支持时为 true。</returns>
    public static bool Supports(ConnectionType type, AuthMethod method, bool anonymousFtp) =>
        type switch
        {
            ConnectionType.SSH or ConnectionType.SFTP => true,
            ConnectionType.FTP => method == AuthMethod.Password && !anonymousFtp,
            ConnectionType.Plugin => method == AuthMethod.Password,
            _ => false
        };

    /// <summary>这条连接支不支持这种认证方式。</summary>
    /// <param name="profile">连接。</param>
    /// <param name="method">认证方式。</param>
    /// <returns>支持时为 true。</returns>
    public static bool Supports(SessionProfile profile, AuthMethod method)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Supports(profile.ConnectionType, method, profile.Ftp?.Anonymous == true);
    }
}
