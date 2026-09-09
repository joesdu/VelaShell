namespace VelaShell.Infrastructure.Import;

/// <summary>把 <c>ssh_config</c> 里的路径写法(<c>~</c> 前缀、相对路径、正斜杠)还原成本机可用的绝对路径。</summary>
internal static class SshPathResolver
{
    /// <summary>当前用户主目录;取不到时为空串。</summary>
    public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary><c>~/.ssh</c> 目录(不保证存在)。</summary>
    public static string SshDirectory => Path.Combine(HomeDirectory, ".ssh");

    /// <summary>
    /// 展开一个配置里的路径:<c>~</c> / <c>%d</c> 换成主目录,正斜杠换成本机分隔符,
    /// 相对路径按 <paramref name="baseDirectory" />(通常是 <c>~/.ssh</c>)求绝对。
    /// </summary>
    /// <param name="value">配置里的原始路径。</param>
    /// <param name="baseDirectory">相对路径的基准目录。</param>
    /// <returns>绝对路径;<paramref name="value" /> 为空时返回空串。</returns>
    public static string Expand(string value, string baseDirectory)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return string.Empty;
        }

        string home = HomeDirectory;
        if (path is "~" && home.Length > 0)
        {
            return home;
        }
        if ((path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) && home.Length > 0)
        {
            path = Path.Combine(home, path[2..]);
        }
        else if ((path.StartsWith("%d/", StringComparison.Ordinal) || path.StartsWith("%d\\", StringComparison.Ordinal)) && home.Length > 0)
        {
            // %d 是 OpenSSH 的「本地用户主目录」记号,IdentityFile 里偶尔见到。
            path = Path.Combine(home, path[3..]);
        }

        path = path.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return Path.IsPathRooted(path) || baseDirectory.Length == 0
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(baseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value!; // 认不出来就原样留着,让用户在连接对话框里自己看见并修正。
        }
    }
}
