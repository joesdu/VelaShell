using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VelaShell.Tests.Integration;

[TestClass]
public class CrossPlatformPublishTests : IDisposable
{
    private readonly string _publishOutputDir;

    public TestContext TestContext { get; set; } = null!;

    public CrossPlatformPublishTests()
    {
        _publishOutputDir = Path.Combine(Path.GetTempPath(), $"velashell_publish_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_publishOutputDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_publishOutputDir))
                Directory.Delete(_publishOutputDir, true);
        }
        catch
        {
        }
    }

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "src", "VelaShell.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not find solution root. Expected src/VelaShell.slnx in an ancestor directory.");
    }

    private (int exitCode, string output, string error) RunDotnetPublish(string rid)
    {
        var solutionRoot = FindSolutionRoot();
        var projectPath = Path.Combine(solutionRoot, "src", "VelaShell.App");
        var outputDir = Path.Combine(_publishOutputDir, rid);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectPath}\" -r {rid} --self-contained -c Release -o \"{outputDir}\" /p:PublishSingleFile=true",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = solutionRoot
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(300_000);

        return (process.ExitCode, stdout, stderr);
    }

    private static bool IsNativeRid(string rid)
    {
        return rid switch
        {
            "osx-arm64" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture == Architecture.Arm64,
            "osx-x64" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture == Architecture.X64,
            "win-x64" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.OSArchitecture == Architecture.X64,
            "linux-x64" => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64,
            _ => false
        };
    }

    private bool SkipIfNotNativeRid(string rid)
    {
        // These tests run actual `dotnet publish` which takes several minutes.
        // Only run when VELASHELL_PUBLISH_TESTS=1 environment variable is set.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELASHELL_PUBLISH_TESTS")))
        {
            TestContext.WriteLine($"[SKIP] Publish tests are opt-in. Set VELASHELL_PUBLISH_TESTS=1 to enable. (RID: {rid})");
            return true;
        }

        if (!IsNativeRid(rid))
        {
            TestContext.WriteLine($"[SKIP] Skipping publish test for {rid}: current platform is {RuntimeInformation.RuntimeIdentifier}. Cross-compilation for non-native RIDs may not be supported without additional workloads.");
            return true;
        }
        return false;
    }

    [TestMethod]
    [TestCategory("CrossPlatform")]
    public void Publish_OsxArm64_Succeeds()
    {
        const string rid = "osx-arm64";
        if (SkipIfNotNativeRid(rid)) return;

        var (exitCode, stdout, stderr) = RunDotnetPublish(rid);

        Assert.AreEqual(0, exitCode,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var outputDir = Path.Combine(_publishOutputDir, rid);
        Assert.IsTrue(Directory.Exists(outputDir));
        Assert.IsTrue(Directory.GetFiles(outputDir).Any(),
            $"publish output for {rid} should contain files");
    }

    [TestMethod]
    [TestCategory("CrossPlatform")]
    public void Publish_WinX64_Succeeds()
    {
        const string rid = "win-x64";
        if (SkipIfNotNativeRid(rid)) return;

        var (exitCode, stdout, stderr) = RunDotnetPublish(rid);

        Assert.AreEqual(0, exitCode,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var outputDir = Path.Combine(_publishOutputDir, rid);
        Assert.IsTrue(Directory.Exists(outputDir));
        Assert.IsTrue(Directory.GetFiles(outputDir).Any(),
            $"publish output for {rid} should contain files");
    }

    [TestMethod]
    [TestCategory("CrossPlatform")]
    public void Publish_LinuxX64_Succeeds()
    {
        const string rid = "linux-x64";
        if (SkipIfNotNativeRid(rid)) return;

        var (exitCode, stdout, stderr) = RunDotnetPublish(rid);

        Assert.AreEqual(0, exitCode,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var outputDir = Path.Combine(_publishOutputDir, rid);
        Assert.IsTrue(Directory.Exists(outputDir));
        Assert.IsTrue(Directory.GetFiles(outputDir).Any(),
            $"publish output for {rid} should contain files");
    }
}
