using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests.TestSupport;

/// <summary>
/// 测试用例开 <see cref="SonnetDbEngine" /> 的统一入口:新库从「已建好 schema 的模板库」拷一份再开,不每条用例从零建库。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么</b>:在空目录上开库要建 11 个文档集合与 3 个时序 measurement,本机实测约 540 ms,其中约 460 ms 是建 schema
/// (重开一个已建好的库只要 ~80 ms);而且这段开销在 Windows 上尤其重 —— GitHub 的 Windows runner 上,每条用例都新开一个库的
/// 测试类比 Linux 慢 10–40 倍(run 44429052817:QuickCommandsViewModelTests 61.9 秒 vs 1.5 秒,ConfigAuditTests 29.0 秒 vs 1.2 秒)。
/// 拷一份 68 个小文件、共 3 KiB 的模板再开,本机实测 ~84 ms。
/// </para>
/// <para>
/// <b>不改变用例看到的东西</b>:模板就是同一个进程里用生产的 <see cref="SonnetDbEngine" /> 在空目录上建出来、正常关掉的库,
/// 拷过去再开,走的是「重启后开已有的库」那条生产路径,schema 与刚建出来的完全一样。
/// 目标目录已存在且非空时(用例先往里放了东西,比如孤儿段文件)不拷,照常开库。
/// 验「空目录建库」本身的用例(SonnetDbPersistenceTests、SonnetDbSegmentTempSweepTests)仍直接 <c>new SonnetDbEngine</c>。
/// </para>
/// </remarks>
internal static class SonnetDbTestTemplate
{
    private static readonly Lazy<string> Template = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>在 <paramref name="rootDirectory" /> 开库;目录不存在或为空时先把模板库拷过去。</summary>
    public static SonnetDbEngine Open(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory) || !Directory.EnumerateFileSystemEntries(rootDirectory).Any())
        {
            CopyDirectory(Template.Value, rootDirectory);
        }
        return new SonnetDbEngine(rootDirectory);
    }

    /// <summary>同 <see cref="Open(string)" />,库放在 <paramref name="paths" /> 指定的 SonnetDB 目录。</summary>
    public static SonnetDbEngine Open(VelaShellStoragePaths paths) => Open(paths.SonnetDbDirectory);

    private static string Build()
    {
        string template = Path.Combine(Path.GetTempPath(), "velashell-tests-sonnetdb-template-" + Guid.NewGuid().ToString("N"));
        new SonnetDbEngine(template).Dispose();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(template, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        return template;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }
}
