namespace VelaShell.Core.Ssh;

/// <summary>
/// OpenSSH 用户证书的文件命名约定。
/// </summary>
/// <remarks>
/// <para>
/// ssh-keygen 签发用户证书时固定产出 <c>&lt;私钥文件名&gt;-cert.pub</c>,证书与私钥永远同目录成对存在。
/// 证书认证需要两个文件都填对(证书是 CA 的背书,签名始终由私钥出),而它们的文件名只差一个后缀 ——
/// 让用户在两个文件选择器里把同一个目录翻两遍没有意义,还容易挑到隔壁那把不匹配的私钥。
/// </para>
/// <para>
/// 放在 Core 而不是各自写进两个对话框:连接配置页与登录弹窗都要用它,重复一份的代价是
/// 将来改了后缀只改一处、另一处静默失效。
/// </para>
/// </remarks>
public static class OpenSshCertificate
{
    /// <summary>ssh-keygen 给用户证书文件加的固定后缀。</summary>
    public const string CertificateSuffix = "-cert.pub";

    /// <summary>
    /// 由证书路径反推与之配对的私钥路径。
    /// </summary>
    /// <param name="certificatePath">证书文件路径;为空或不合约定时推不出来。</param>
    /// <param name="fileExists">
    /// 判断文件是否存在的回调;默认走真实文件系统。留出这个口子是为了让用例不必真的往磁盘上摆文件。
    /// </param>
    /// <returns>推得出、且该私钥文件确实存在时返回其路径;否则返回 <c>null</c>。</returns>
    public static string? InferPrivateKeyPath(string? certificatePath, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(certificatePath)
            || !certificatePath.EndsWith(CertificateSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string candidate = certificatePath[..^CertificateSuffix.Length];
        // 文件名整个就是后缀(比如目录下一个光秃秃的 "-cert.pub"):推出来是空串,不是路径。
        if (candidate.Length == 0)
        {
            return null;
        }
        return (fileExists ?? Exists)(candidate) ? candidate : null;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 路径非法或不可访问就当推不出来。这只是个填表便利,绝不该因此把对话框顶掉 ——
            // 用户自己去选那个私钥,一切照旧。
            return false;
        }
    }
}
