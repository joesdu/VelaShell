using System.Collections.ObjectModel;
using System.Reflection;

namespace VelaShell.Services;

/// <summary>
/// 关于页展示的依赖版本:取自编译期写入本程序集的元数据(由 VelaShell.csproj 的
/// EmbedPackageVersions 目标从 Directory.Packages.props 的 PackageVersion 项生成)。
/// </summary>
/// <remarks>
/// 之所以走编译期而不是运行时探测,两条路都堵死了(详见 VelaShell.csproj 里那段注释):
/// · 读 .deps.json:Release 是 PublishSingleFile,该文件被打进 exe 的 bundle,磁盘上没有。
/// · 读程序集版本:与包版本不是一回事 —— 实测 SSH.NET 的程序集报 2025.1.0.1、
///   ReactiveUI 报 23.0.0.0,而包版本分别是 2025.1.0 和 23.2.28。
/// 按包名查还有个好处:不必引用类型。SSH.NET 被刻意隔离在 Infrastructure 层,
/// 关于页不该为了取个版本号把它引进来。
/// </remarks>
internal static class PackageVersions
{
    /// <summary>元数据键前缀,与 csproj 里 EmbedPackageVersions 写入的一致。</summary>
    private const string KeyPrefix = "PackageVersion:";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Cache =
        new(() => Read(typeof(PackageVersions).Assembly));

    /// <summary>取包版本;查不到返回 null,由调用方决定如何降级。</summary>
    public static string? Of(string packageId) =>
        Cache.Value.TryGetValue(packageId, out string? version) ? version : null;

    /// <summary>读取程序集里的包版本元数据。internal 供测试对着真实构建产物验证。</summary>
    internal static IReadOnlyDictionary<string, string> Read(Assembly assembly)
    {
        Dictionary<string, string> versions = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key.StartsWith(KeyPrefix, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(metadata.Value))
            {
                versions.TryAdd(metadata.Key[KeyPrefix.Length..], metadata.Value);
            }
        }
        return versions.Count > 0 ? versions : ReadOnlyDictionary<string, string>.Empty;
    }
}
