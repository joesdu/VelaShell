using System.IO.Compression;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Chat.Extensions;

/// <summary>一个已安装的扩展包(清单读不出来时 <see cref="Error" /> 说明原因)。</summary>
/// <param name="Id">扩展包 id(安装目录名)。</param>
/// <param name="Directory">安装目录。</param>
/// <param name="Manifest">清单;读不出来为 null。</param>
/// <param name="Error">清单读不出来的原因。</param>
internal sealed record InstalledPack(string Id, string Directory, ExtensionManifest? Manifest, string? Error);

/// <summary>待安装的扩展包:清单已校验,文件在 <see cref="Directory" />(压缩包解在临时目录里,用完 <see cref="Dispose" /> 删掉)。</summary>
internal sealed class StagedPack(ExtensionManifest manifest, string directory, string? temporaryRoot) : IDisposable
{
    /// <summary>已校验的清单。</summary>
    public ExtensionManifest Manifest { get; } = manifest;

    /// <summary>扩展包文件所在目录(清单在它根下)。</summary>
    public string Directory { get; } = directory;

    /// <summary>删掉解压用的临时目录(从文件夹安装时没有)。</summary>
    public void Dispose()
    {
        if (temporaryRoot is null) return;
        try { System.IO.Directory.Delete(temporaryRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// 扩展包的安装、列出与卸载(ai-plan §12.4)。装在插件数据目录的 <c>extensions/&lt;id&gt;/</c> 下 ——
/// 那是装进来的产物而不是用户自己写的东西,卸载插件时随之清除是对的。
/// </summary>
/// <remarks>
/// <para>
/// 来源可以是文件夹或 <c>.zip</c>;清单在根下,或在压缩包里唯一的顶层文件夹下(常见的打包方式)。
/// 压缩包逐项检查落点(不许越出解压目录)、项数与解压后总大小;文件夹不跟随符号链接 / 联接点。
/// </para>
/// <para>
/// 安装先复制到同级的临时目录,成功后再换掉旧版本,中途失败不会留下半装好的扩展包。
/// 这里只管文件;启用状态与用户确认过的 MCP 指纹记在设置里(<see cref="ExtensionPackState" />)。
/// </para>
/// </remarks>
internal sealed class ExtensionPackStore(string dataDirectory)
{
    /// <summary>最多多少个文件。</summary>
    public const int MaxFiles = 20_000;

    /// <summary>解压 / 复制后的总大小上限(字节)。</summary>
    public const long MaxTotalBytes = 200L * 1024 * 1024;

    /// <summary>扩展包安装在这个目录下,一包一个子目录。</summary>
    public string Root => Path.Combine(dataDirectory, "extensions");

    /// <summary>读一个来源(文件夹或 <c>.zip</c>)并校验清单,不安装。</summary>
    public static StagedPack Stage(string source)
    {
        if (Directory.Exists(source))
        {
            return new StagedPack(ExtensionManifest.Load(source), Path.GetFullPath(source), null);
        }
        if (!File.Exists(source) || !string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose an extension pack folder or a .zip file.");
        }
        string temp = Path.Combine(Path.GetTempPath(), "velashell-extension-" + Guid.NewGuid().ToString("N"));
        try
        {
            Extract(source, temp);
            string packDirectory = File.Exists(Path.Combine(temp, ExtensionManifest.FileName))
                ? temp
                : Directory.GetDirectories(temp) is [var only] && File.Exists(Path.Combine(only, ExtensionManifest.FileName))
                    ? only
                    : throw new InvalidDataException($"{ExtensionManifest.FileName} was not found at the root of the archive.");
            return new StagedPack(ExtensionManifest.Load(packDirectory), packDirectory, temp);
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>安装(同 id 的旧版本被替换);返回安装好的扩展包。</summary>
    public InstalledPack Install(StagedPack staged)
    {
        Directory.CreateDirectory(Root);
        string target = Path.Combine(Root, staged.Manifest.Id);
        string staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(staged.Directory, staging);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return new InstalledPack(staged.Manifest.Id, target, ExtensionManifest.Load(target), null);
    }

    /// <summary>卸载(删掉安装目录);没装过也不报错。</summary>
    public void Uninstall(string id)
    {
        if (!ExtensionManifest.IsValidId(id)) throw new ArgumentException("Invalid extension pack id.", nameof(id));
        string target = Path.Combine(Root, id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    /// <summary>列出已安装的扩展包(按 id 排序);清单读不出来的带原因。</summary>
    public IReadOnlyList<InstalledPack> List()
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<InstalledPack>();
        foreach (string directory in Directory.GetDirectories(Root).Order(StringComparer.Ordinal))
        {
            string id = Path.GetFileName(directory);
            if (id.StartsWith('.')) continue; // 安装中的临时目录
            try
            {
                var manifest = ExtensionManifest.Load(directory);
                result.Add(manifest.Id == id
                    ? new InstalledPack(id, directory, manifest, null)
                    : new InstalledPack(id, directory, null, $"The manifest id '{manifest.Id}' does not match the folder name."));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                result.Add(new InstalledPack(id, directory, null, ex.Message));
            }
        }
        return result;
    }

    private static void Extract(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxFiles) throw new InvalidDataException($"The archive has more than {MaxFiles} entries.");
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            total += entry.Length;
            if (total > MaxTotalBytes) throw new InvalidDataException($"The archive expands to more than {MaxTotalBytes / 1024 / 1024} MB.");
            string full = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            // zip-slip:任何一项落到解压目录之外都整包拒绝
            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"The archive entry '{entry.FullName}' points outside the extension pack.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: false);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        int files = 0;
        long total = 0;
        var pending = new Stack<(string From, string To)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            (string from, string to) = pending.Pop();
            Directory.CreateDirectory(to);
            foreach (FileSystemInfo item in new DirectoryInfo(from).EnumerateFileSystemInfos())
            {
                // 不跟随符号链接 / 联接点:否则一条链接就能把扩展包目录外的东西复制进来
                if (item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                string target = Path.Combine(to, item.Name);
                if (item is DirectoryInfo directory)
                {
                    pending.Push((directory.FullName, target));
                    continue;
                }
                var file = (FileInfo)item;
                if (++files > MaxFiles) throw new InvalidDataException($"The extension pack has more than {MaxFiles} files.");
                total += file.Length;
                if (total > MaxTotalBytes) throw new InvalidDataException($"The extension pack is larger than {MaxTotalBytes / 1024 / 1024} MB.");
                file.CopyTo(target);
            }
        }
    }
}
