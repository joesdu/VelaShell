using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>找 VcXsrv:文件系统与环境变量全部注入,不碰真实机器。</summary>
[TestClass]
[TestCategory("XServer")]
public class VcXsrvLocatorTests
{
    private static readonly string ProgramFiles = Path.Combine("C:", "Program Files");
    private static readonly string Installed = Path.Combine(ProgramFiles, "VcXsrv", "vcxsrv.exe");

    private static Func<string, string?> Env(Dictionary<string, string> values) =>
        name => values.GetValueOrDefault(name);

    [TestMethod]
    public void DefaultInstallLocation_IsFound()
    {
        string? found = VcXsrvLocator.Find(
            null, path => path == Installed, Env(new() { ["ProgramFiles"] = ProgramFiles }));

        Assert.AreEqual(Installed, found);
    }

    /// <summary>填的是目录也认:用户常常把安装目录而不是 exe 粘进来。</summary>
    [TestMethod]
    public void ConfiguredDirectory_IsCompletedWithExecutableName()
    {
        string dir = Path.Combine("D:", "tools", "VcXsrv");
        string exe = Path.Combine(dir, "vcxsrv.exe");

        Assert.AreEqual(exe, VcXsrvLocator.Find(dir, path => path == exe, Env([])));
    }

    [TestMethod]
    public void ConfiguredQuotedPath_IsUnquoted()
    {
        string exe = Path.Combine("D:", "my tools", "vcxsrv.exe");

        Assert.AreEqual(exe, VcXsrvLocator.Find($"\"{exe}\"", path => path == exe, Env([])));
    }

    /// <summary>
    /// 配了路径却找不到:报「你填的那个不存在」,而不是悄悄换成默认位置上的另一个 VcXsrv。
    /// </summary>
    [TestMethod]
    public void ConfiguredButMissing_DoesNotFallBackToDefaults()
    {
        string? found = VcXsrvLocator.Find(
            Path.Combine("D:", "missing", "vcxsrv.exe"),
            path => path == Installed,
            Env(new() { ["ProgramFiles"] = ProgramFiles }));

        Assert.IsNull(found);
    }

    [TestMethod]
    public void PathVariable_IsSearchedLast()
    {
        string onPath = Path.Combine("E:", "bin", "vcxsrv.exe");

        string? found = VcXsrvLocator.Find(
            null,
            path => path == onPath,
            Env(new() { ["PATH"] = string.Join(Path.PathSeparator, Path.Combine("E:", "other"), Path.Combine("E:", "bin")) }));

        Assert.AreEqual(onPath, found);
    }

    [TestMethod]
    public void NothingInstalled_ReturnsNull() =>
        Assert.IsNull(VcXsrvLocator.Find(null, _ => false, Env(new() { ["ProgramFiles"] = ProgramFiles })));
}
