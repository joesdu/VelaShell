using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 上传源打开之后按文件身份(Windows 卷序列号 + 文件 id,Linux / macOS dev + inode)核验,不按路径字符串:
/// 同一个文件的另一种写法(8.3 短名、subst 盘、硬链接、大小写或 Unicode 规范化不同的名字)照常放行,
/// 别的文件、经链接走到的同一个文件仍然拒绝。
/// </summary>
public sealed partial class RemoteTransferTests
{
    [TestMethod]
    public async Task SourceIdentityAcceptsAnotherHardLinkOfTheSameFileAndRejectsADifferentFile()
    {
        string directory = NewDirectory("vela-identity-hardlink-");
        string original = Path.Combine(directory, "original");
        string other = Path.Combine(directory, "other");
        string alias = Path.Combine(directory, "alias");
        await File.WriteAllBytesAsync(original, [1, 2, 3]);
        await File.WriteAllBytesAsync(other, [1, 2, 3]);
        try
        {
            await CreateHardLink(alias, original);
            using SafeFileHandle opened = File.OpenHandle(original);
            RemoteFsCapability.VerifyOpenedSource(opened, RemoteFsCapability.ResolveUploadSource(alias));
            RemoteFsCapability.VerifyOpenedSource(opened, RemoteFsCapability.ResolveUploadSource(original));
            // 内容一样、名字挨着,仍是另一个文件。
            IOException mismatch = Assert.ThrowsExactly<IOException>(() => RemoteFsCapability.VerifyOpenedSource(opened, RemoteFsCapability.ResolveUploadSource(other)));
            Assert.AreEqual("The opened source does not match the approved path.", mismatch.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 核验时从审批路径走到的那一项不许经过链接,否则核验本身也能被换进来的链接骗过:句柄与经链接走到的就是同一个文件,
    /// 身份完全一致,照样要按链接拒绝。不经链接的那条路径放行。
    /// </summary>
    [TestMethod]
    public async Task SourceIdentityWalkRejectsALinkedAncestorEvenWhenTheFileIsTheSame()
    {
        string directory = NewDirectory("vela-identity-link-");
        string real = Path.Combine(directory, "real");
        Directory.CreateDirectory(real);
        string file = Path.Combine(real, "file");
        await File.WriteAllBytesAsync(file, [7]);
        string alias = Path.Combine(directory, "alias");
        try
        {
            if (OperatingSystem.IsWindows()) await RunTool("cmd.exe", "/c", "mklink", "/J", alias, real);
            else Directory.CreateSymbolicLink(alias, real);
            Assert.IsNotNull(new DirectoryInfo(alias).LinkTarget, "链接没建出来");
            using SafeFileHandle opened = File.OpenHandle(file);
            RemoteFsCapability.VerifyOpenedSource(opened, file);
            Assert.ThrowsExactly<NotSupportedException>(() => RemoteFsCapability.VerifyOpenedSource(opened, Path.Combine(alias, "file")));
            string leaf = Path.Combine(directory, "leaf");
            bool canSymlink = true;
            try { File.CreateSymbolicLink(leaf, file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { canSymlink = false; }   // Windows 要开发者模式
            if (canSymlink) Assert.ThrowsExactly<NotSupportedException>(() => RemoteFsCapability.VerifyOpenedSource(opened, leaf));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 8.3 短名(用户名长或带空格时 %TEMP% 本身就是短名):.NET 的 <see cref="Path.GetFullPath(string)" /> 碰到带 <c>~</c>
    /// 的路径会展开成长名,所以原先的逐字比对其实没有误拒它;这条钉住改比文件身份之后照样放行,审批框写的是展开后的长名。
    /// 卷上没开短名生成时不确定。
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task WindowsShortNameSourceIsTheSameFile()
    {
        string directory = NewDirectory("vela identity short name ");
        string file = Path.Combine(directory, "long source name.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            char[] buffer = new char[32768];
            uint length = GetShortPathNameW(file, buffer, (uint)buffer.Length);
            string shortPath = length is > 0 and < 32768 ? new string(buffer, 0, (int)length) : file;
            if (string.Equals(shortPath, file, StringComparison.OrdinalIgnoreCase)) Assert.Inconclusive("这个卷没有生成 8.3 短名。");
            Assert.AreEqual(file, RemoteFsCapability.ResolveUploadSource(shortPath), ignoreCase: true);
            await AssertUploadStartsFrom(shortPath);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task WindowsShortPathBufferPreservesUnicodeAndLastError()
    {
        string directory = NewDirectory("vela-路径-é-📁-");
        string file = Path.Combine(directory, "源文件-é-📄.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            char[] buffer = new char[32768];
            uint length = GetShortPathNameW(file, buffer, (uint)buffer.Length);
            Assert.IsTrue(length > 0 && length < buffer.Length, $"GetShortPathNameW failed: {Marshal.GetLastPInvokeError()}");
            string shortPath = new(buffer, 0, (int)length);
            using SafeFileHandle opened = File.OpenHandle(shortPath);
            RemoteFsCapability.VerifyOpenedSource(opened, file);

            uint missingLength = GetShortPathNameW(Path.Combine(directory, "不存在.bin"), buffer, (uint)buffer.Length);
            int error = Marshal.GetLastPInvokeError();
            Assert.AreEqual(0u, missingLength);
            Assert.AreEqual(2, error);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>subst 盘:句柄的最终路径是底下的真实目录,原先逐字比对一律拒绝。</summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task WindowsSubstDriveSourceIsTheSameFile()
    {
        string directory = NewDirectory("vela-identity-subst-");
        await File.WriteAllBytesAsync(Path.Combine(directory, "source"), [1, 2, 3]);
        HashSet<char> used = [.. DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0]))];
        char letter = "ZYXWVUTSRQPONMLKJIH".FirstOrDefault(c => !used.Contains(c));
        if (letter == default) Assert.Inconclusive("没有空闲的盘符。");
        string drive = letter + ":";
        try
        {
            if (await RunTool("subst.exe", drive, directory) != 0) Assert.Inconclusive("subst 不可用。");
            await AssertUploadStartsFrom(drive + @"\source");
        }
        finally
        {
            await RunTool("subst.exe", drive, "/d");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 映射盘:句柄的最终路径是 <c>\\server\share\…</c>,原先逐字比对一律拒绝。经本机的管理共享 <c>\\localhost\C$</c>
    /// 映射一个空闲盘符(不记住,用完删掉)验证 SMB 重定向器上的钉目录与文件 id;映射不上(没有管理共享、Server 服务没开)时不确定。
    /// </summary>
    /// <remarks>
    /// 它会真的执行 <c>net use</c>:往当前登录会话里挂一个网络盘(资源管理器里看得见),测试进程跑到一半被杀掉就留在那儿。
    /// 单元测试不该去动本机的网络盘映射,所以默认不跑,设 <c>VELASHELL_NET_USE_TESTS=1</c> 才跑(维护者 2026-10-25 定)。
    /// 映射盘的结论另有手动实测(§257),这条留着是为了改核验代码时能重跑一遍。
    /// </remarks>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task WindowsMappedDriveSourceIsTheSameFile()
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_NET_USE_TESTS") != "1")
            Assert.Inconclusive("[SKIP] 这条会真的 net use 一个网络盘到本机的管理共享,设 VELASHELL_NET_USE_TESTS=1 才跑。");
        string directory = NewDirectory("vela-identity-mapped-");
        await File.WriteAllBytesAsync(Path.Combine(directory, "source"), [1, 2, 3]);
        string? root = Path.GetPathRoot(directory);
        if (root is not { Length: 3 } || root[1] != ':') Assert.Inconclusive("临时目录不在盘符路径上。");
        HashSet<char> used = [.. DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0]))];
        char letter = "QPONMLKJIH".FirstOrDefault(c => !used.Contains(c));
        if (letter == default) Assert.Inconclusive("没有空闲的盘符。");
        string drive = letter + ":";
        bool mapped = false;
        try
        {
            mapped = await RunTool("net.exe", "use", drive, $@"\\localhost\{root[0]}$", "/persistent:no") == 0;
            if (!mapped) Assert.Inconclusive("映射不上本机的管理共享。");
            if (new DriveInfo(drive).DriveType != DriveType.Network) Assert.Inconclusive("映射出来的不是网络盘。");
            await AssertUploadStartsFrom(drive + directory[2..] + @"\source");
        }
        finally
        {
            if (mapped) await RunTool("net.exe", "use", drive, "/delete", "/y");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 同一个名字的另一种写法:大小写不敏感的卷(macOS 默认的 APFS、Windows 的 NTFS)上换个大小写,
    /// 规范化不敏感的卷(APFS)上 NFC 与 NFD 互换,打开的都是同一个文件。macOS 上句柄查出来的是磁盘上存的那种写法,
    /// 原先逐字比对就拒了。两种写法都指不到这个文件(大小写与规范化都敏感的卷,比如 Linux 的 ext4)时不确定。
    /// </summary>
    [TestMethod]
    public async Task CaseAndNormalizationVariantsOfTheSourceNameAreTheSameFile()
    {
        string directory = NewDirectory("vela-identity-variants-");
        string stored = Path.Combine(directory, "Mixed-Case-é.bin");
        await File.WriteAllBytesAsync(stored, [1, 2, 3]);
        try
        {
            string[] variants = [Path.Combine(directory, "mixed-case-é.bin"), Path.Combine(directory, "Mixed-Case-é.bin")];
            string[] reachable = [.. variants.Where(File.Exists)];
            if (reachable.Length == 0) Assert.Inconclusive("这个卷大小写与 Unicode 规范化都敏感。");
            foreach (string variant in reachable) await AssertUploadStartsFrom(variant);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>从这个路径启动上传:启动不抛,审批框与快照写的是解析后的这个路径(审批拒掉,不往下传)。</summary>
    private async Task AssertUploadStartsFrom(string path)
    {
        string resolved = RemoteFsCapability.ResolveUploadSource(path);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.Deny);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), path, "/target", new() { OperationId = "variant-" + path });
            Assert.AreEqual(resolved, start.LocalPath);
            Assert.AreEqual("permission-denied", (await Terminal(api, start.TransferId)).ErrorCode);
            await prompt.Received(1).RequestFileUploadAsync("test.transfer", Arg.Any<string>(), resolved, "/target", false, Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); }
    }

    private static async Task CreateHardLink(string link, string target)
    {
        int exit = OperatingSystem.IsWindows()
            ? await RunTool("cmd.exe", "/c", "mklink", "/H", link, target)
            : await RunTool("ln", target, link);
        Assert.AreEqual(0, exit, "硬链接没建出来");
    }

    private static async Task<int> RunTool(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetShortPathNameW(string longPath,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] char[] shortPath, uint length);
}
