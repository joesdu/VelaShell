using System.Reflection;
using VelaShell.Ssh.Session;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 当前在用的 SSH 后端是什么、什么版本 —— 关于页要显示它。
/// </summary>
/// <remarks>
/// <para>
/// 版本原先走 <c>PackageVersions</c>(编译期从 <c>Directory.Packages.props</c> 抄进程序集元数据),
/// 因为那时 SSH 库是个 NuGet 包,而关于页**不该为了取个版本号把它引进来**——
/// 库被刻意隔离在 Infrastructure 层。
/// </para>
/// <para>
/// 现在它是工程引用,压根没有包版本可查。于是反过来:<b>由持有它的那一层交出这两个字段</b>,
/// 关于页只管显示。分层没变,而且换库时改一处就够了 ——
/// 那个字符串包名的老办法换库时连编译错误都不会有(SSH.NET → Tmds.Ssh 那次就漏过一处,
/// 关于页的版本号静默变空)。
/// </para>
/// </remarks>
public static class SshBackend
{
    /// <summary>后端名称,关于页与依赖清单都用它。</summary>
    public const string Name = "VelaShell.Ssh";

    /// <summary>后端源码地址(2026-09-23 起在本仓库 src/VelaShell.Ssh,原独立仓库 VelaShellLabs/velashell-ssh)。</summary>
    public const string ProjectUrl = "https://github.com/joesdu/VelaShell/tree/main/src/VelaShell.Ssh";

    /// <summary>后端许可证文本地址。该目录按 MIT 授权,与本仓库其余部分不同。</summary>
    public const string LicenseUrl = "https://github.com/joesdu/VelaShell/blob/main/src/VelaShell.Ssh/LICENSE";

    /// <summary>后端许可证。</summary>
    public const string License = "MIT";

    /// <summary>
    /// 后端版本;取不到时为 <see langword="null" />,由调用方决定怎么降级。
    /// </summary>
    /// <remarks>
    /// 取 <see cref="AssemblyInformationalVersionAttribute" /> 而不是 <c>AssemblyVersion</c>:
    /// 后者不许带预发布后缀,而这个库现在正是 <c>0.0.1-dev</c> 这种 ——
    /// 显示成 <c>0.0.1</c> 会让人以为用的是个正式版。
    /// </remarks>
    public static string? Version { get; } = ReadVersion();

    private static string? ReadVersion()
    {
        // 用库里的一个类型去拿程序集,而不是按名字在已加载程序集里找:
        // 按名字找会在「还没连过任何主机、库尚未被加载」时落空,
        // 而关于页恰恰是那种刚启动就会被点开的地方。
        Assembly assembly = typeof(SshConnection).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink 会在后面拼 "+<提交哈希>";关于页不需要那一截。
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString();
    }
}
