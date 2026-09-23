namespace VelaShell.Infrastructure.XServer;

/// <summary>找 VcXsrv 的可执行文件。</summary>
/// <remarks>
/// 查找顺序:设置里填的路径(填的是目录也认,自动补上 <c>vcxsrv.exe</c>)→ 官方安装包的默认位置
/// (64 位与 32 位 Program Files)→ Scoop 的安装位置 → PATH。
/// 文件系统与环境变量都经参数注入,单测不碰真实机器。
/// </remarks>
internal static class VcXsrvLocator
{
    /// <summary>可执行文件名。</summary>
    public const string ExecutableName = "vcxsrv.exe";

    /// <summary>在真实机器上查找。</summary>
    public static string? Find(string? configuredPath) =>
        Find(configuredPath, File.Exists, Environment.GetEnvironmentVariable);

    /// <summary>可注入的查找(单测用)。</summary>
    /// <param name="configuredPath">设置里填的路径;留空走自动查找。</param>
    /// <param name="fileExists">文件是否存在。</param>
    /// <param name="getEnvironment">读环境变量。</param>
    public static string? Find(
        string? configuredPath,
        Func<string, bool> fileExists,
        Func<string, string?> getEnvironment)
    {
        // 配置了路径就只认它:用户明确指了一个,找不到应该报「你填的那个不存在」,
        // 而不是悄悄换成另一个版本的 VcXsrv。
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (fileExists(path))
            {
                return path;
            }
            string inDirectory = Path.Combine(path, ExecutableName);
            return fileExists(inDirectory) ? inDirectory : null;
        }

        foreach (string candidate in Candidates(getEnvironment))
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(Func<string, string?> getEnvironment)
    {
        foreach (string variable in (string[])["ProgramW6432", "ProgramFiles", "ProgramFiles(x86)"])
        {
            if (getEnvironment(variable) is { Length: > 0 } root)
            {
                yield return Path.Combine(root, "VcXsrv", ExecutableName);
            }
        }

        if (getEnvironment("SCOOP") is { Length: > 0 } scoop)
        {
            yield return Path.Combine(scoop, "apps", "vcxsrv", "current", ExecutableName);
        }
        if (getEnvironment("USERPROFILE") is { Length: > 0 } profile)
        {
            yield return Path.Combine(profile, "scoop", "apps", "vcxsrv", "current", ExecutableName);
        }

        if (getEnvironment("PATH") is { Length: > 0 } pathVariable)
        {
            foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return Path.Combine(directory.Trim('"'), ExecutableName);
            }
        }
    }
}
