using System.Diagnostics;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 写出来的 <c>.Xauthority</c> 与真实的 xauth 互通:xauth 列得出我们登记的那条,两边改写时都留着对方的记录;
/// 锁文件按同一套 <c>-c</c> / <c>-l</c> 的名字,xauth 看见它们就不写。
/// </summary>
/// <remarks>
/// <para>
/// 用 XServer 互操作靶场的镜像 <c>velashell-xclients</c> 里的 xauth(<c>docker build -t velashell-xclients scripts/xserver/interop</c>),
/// 并设环境变量 <c>VELASHELL_XSERVER_INTEROP=1</c>。条件不满足时早退并在 TestContext 里写 <c>[SKIP]</c> ——
/// MSTest 会把早退记为通过,**看 [SKIP] 行才知道跑没跑**。
/// </para>
/// <para>
/// 文件按 base64 经命令行送进容器、改完从标准输出取回,不挂目录:Windows 上的 Docker Desktop 挂本机目录时有的机器会一直卡住。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Interop")]
public sealed class XAuthorityInteropTests
{
    private const string Image = "velashell-xclients";
    private const string Marker = "----8<----";

    private static readonly byte[] Cookie = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    private string _directory = "";

    public TestContext TestContext { get; set; } = null!;

    private string FilePath => Path.Combine(_directory, "Xauthority");

    [TestInitialize]
    public void Setup() => _directory = Directory.CreateTempSubdirectory("vx-xauth-interop-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private bool ShouldSkip()
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_XSERVER_INTEROP") != "1")
        {
            TestContext.WriteLine("[SKIP] 没有设 VELASHELL_XSERVER_INTEROP=1");
            return true;
        }
        return false;
    }

    /// <summary>
    /// 把本机的这个文件(没有就是空的)送进容器的 <c>/tmp/Xauthority</c>,跑 <paramref name="commands" />(<c>$A</c> 是这个路径),
    /// 再把文件取回来写回本机。返回最后一条命令的退出码与输出。
    /// </summary>
    private async Task<(int ExitCode, string Output)> XauthAsync(string commands, int timeoutSeconds = 90)
    {
        string content = File.Exists(FilePath) ? Convert.ToBase64String(File.ReadAllBytes(FilePath)) : "";
        string script = $"A=/tmp/Xauthority; printf '%s' '{content}' | base64 -d > $A; {commands}; status=$?; "
                        + $"echo '{Marker}'; base64 -w0 $A; exit $status";
        ProcessStartInfo start = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in (string[])["run", "--rm", Image, "sh", "-c", script])
        {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        string output = await stdout;
        int marker = output.LastIndexOf(Marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, marker, output + await stderr);
        File.WriteAllBytes(FilePath, Convert.FromBase64String(output[(marker + Marker.Length)..].Trim()));
        string text = output[..marker] + await stderr;
        TestContext.WriteLine($"{commands} → {process.ExitCode}\n{text}");
        return (process.ExitCode, text);
    }

    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task xauth列得出我们登记的那条_两边改写都留着对方的记录()
    {
        if (ShouldSkip())
        {
            return;
        }
        // xauth 先写一条(用户桌面的显示 0),我们再登记显示 10:两条都在。
        Assert.AreEqual(0, (await XauthAsync("xauth -f $A add box/unix:0 MIT-MAGIC-COOKIE-1 abcdef")).ExitCode);
        Assert.IsTrue(XAuthorityFile.Add(FilePath, "box", 10, Cookie));
        (int exit, string list) = await XauthAsync("xauth -f $A list");
        Assert.AreEqual(0, exit, list);
        Assert.Contains("box/unix:0  MIT-MAGIC-COOKIE-1  abcdef", list);
        Assert.Contains("box/unix:10  MIT-MAGIC-COOKIE-1  000102030405060708090a0b0c0d0e0f", list);

        // xauth 再改写一遍(加一条显示 11),我们的那条留着;我们撤掉自己的,xauth 的两条留着。
        Assert.AreEqual(0, (await XauthAsync("xauth -f $A add box/unix:11 MIT-MAGIC-COOKIE-1 0011")).ExitCode);
        Assert.IsTrue(XAuthorityFile.Remove(FilePath, "box", 10, Cookie));
        (_, list) = await XauthAsync("xauth -f $A list");
        Assert.Contains("box/unix:0  MIT-MAGIC-COOKIE-1  abcdef", list);
        Assert.Contains("box/unix:11  MIT-MAGIC-COOKIE-1  0011", list);
        Assert.DoesNotContain("box/unix:10 ", list);
    }

    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task 锁文件的名字与xauth的一样_xauth看见它们就不写()
    {
        if (ShouldSkip())
        {
            return;
        }
        Assert.IsTrue(XAuthorityFile.Add(FilePath, "box", 10, Cookie));
        byte[] before = File.ReadAllBytes(FilePath);

        // XAuthorityFile 改写时拿的锁:先建「文件名-c」、再建「文件名-l」(XAuthorityFileTests 核对过这两个名字)。xauth 看见它们,
        // 重试一阵之后放弃。
        (int exit, string output) = await XauthAsync("touch $A-c $A-l; xauth -f $A add box/unix:12 MIT-MAGIC-COOKIE-1 0012");
        Assert.AreNotEqual(0, exit, output);
        Assert.Contains("timeout in locking", output);
        Assert.AreSequenceEqual(before, File.ReadAllBytes(FilePath), "文件原样不动");
    }
}
