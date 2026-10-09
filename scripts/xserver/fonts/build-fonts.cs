#!/usr/bin/env dotnet
#:property PublishAot=false
// 重新生成 src/VelaShell.XServer/Fonts/Data:内置 X 服务端随库带的核心字体(xs_plan CP-16 / F21 方案 A、WN-M5)。
//
//   dotnet run scripts/xserver/fonts/build-fonts.cs                 (在仓库根目录跑;输出目录可以用第一个参数改)
//
// 数据是上游的原样字节(AGENTS.md §2 纪律 4「数据不是代码」):每份 BDF 逐字节不动、只用 Brotli 压缩,
// 存成 <目录>/<原文件名>.br;目录照 X.Org 安装后的字体目录分 misc / 75dpi / 100dpi,每个目录生成一份 mkfontdir 格式的
// fonts.dir(条数,然后每行「文件名 XLFD」,XLFD 取自 BDF 的 FONT 一行)。misc/fonts.alias 是 X.Org font-alias 的原文。
// charsets.txt 是单字节字符集到 Unicode 的映射表(由 .NET 的代码页表导出),服务端按它从 ISO10646-1 字体派生 ISO8859-x / KOI8 名字。
// LICENSE-xorg-fonts.txt / LICENSE-unifont.txt 是上游的许可原文(X.Org 各仓库的 COPYING;Unifont 的版权行与 SIL OFL 1.1),
// 与数据一起嵌进程序集 —— OFL 要求字体的每一份拷贝都带着许可。
//
// 上游固定在下面的提交 / 版本,下载的内容按固定的 SHA-256 核对:Unifont 核对发布的 .bdf.gz 本身;X.Org 的仓库核对从归档里挑出来的文件
// (GitLab 按提交现做的归档不保证逐字节稳定 —— 压缩参数、tar 头里的时间与属主随 GitLab 的版本变 —— 文件内容才是固定的)。
// 全部下载、核对完才动输出目录,哪一份对不上都原样不动。升级时改这里再重跑(对不上时会打印实际的摘要),并同步 Fonts/Data/README.md 与 NOTICE.md。
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

string output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("src", "VelaShell.XServer", "Fonts", "Data"));
if (!File.Exists(Path.Combine(output, "README.md")))
{
    Console.Error.WriteLine($"{output} 下没有 README.md:请在仓库根目录跑,或把 Fonts/Data 的路径作为第一个参数传进来。");
    return 1;
}

// X.Org 的字体仓库:仓库名、固定的提交、放进哪个字体目录、挑出来的文件(.bdf 与 COPYING)的摘要(见 Digest)。
(string Repo, string Commit, string Directory, string Sha256)[] xorg =
[
    ("misc-misc", "c4e2af05583764fae3d0b2aa20d5c93b031cbcbf", "misc", "52a58e34d311f885b8e4ccfb2e7d28f763f8740486934f483c6288a8c321076b"),
    ("cursor-misc", "73b2095391d5bcf326c903946de48d0710daa169", "misc", "c2be9dbb53976b74b289ed4f02f8ab32b5ef7cc747675c1d1936ee6576cbc751"),
    ("adobe-75dpi", "6189f2a653b7daa9566f9331bfed41813cbd8cb1", "75dpi", "ecbc9efca651b1a190f3b5dbfbdcc148364287d211f2f54b87be500b1960c54e"),
    ("adobe-100dpi", "13f867e9f5be0fcc4776f323f5d5f8f354d2a2c0", "100dpi", "2a26bba9816abeb02b8908cb0606c7dc314f9d4d5568de4fea5306eb50688c77"),
];
const string AliasCommit = "ebeee85f070dc12197ad98d1c849786f8e3be124";   // xorg/font/alias
const string AliasSha256 = "eb4ed272b5dbd99852f86ef66f830c6f85409e30fa0364b16657c23ebc697704";   // misc/fonts.alias 与 COPYING
const string UnifontVersion = "18.0.01";
const string UnifontSha256 = "2989e0211030d8219fd931aba599fc95c86bc327ce1423848ef949783b208257";
const string OflSha256 = "869692af094c57fb7258c57fe26820c759319603321d0ffeb278de3651763ded";   // unifoundry.com/OFL-1.1.txt

using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
Dictionary<string, List<(string File, string Xlfd)>> fontsDir = [];   // 目录 → (文件名, XLFD)
Dictionary<string, int[]> charsetTable = [];                          // 字符集名 → 0x80–0xFF 的码位
string charsets = Charsets(charsetTable);
StringBuilder xorgLicenses = new();
xorgLicenses.Append("Licenses of the X.Org fonts bundled with VelaShell.XServer (src/VelaShell.XServer/Fonts/Data).\n")
    .Append("Each section is the verbatim COPYING file of the upstream repository at the pinned commit.\n");

// 先全部下载、核对。
bool verified = true;
List<(string Repo, string Commit, string Directory, List<(string Name, byte[] Bytes)> Files)> xorgFiles = [];
foreach ((string repo, string commit, string directory, string sha256) in xorg)
{
    Console.WriteLine($"xorg/font/{repo} @ {commit[..12]}");
    List<(string Name, byte[] Bytes)> files = await ArchiveFilesAsync(repo, commit, name => name.EndsWith(".bdf", StringComparison.Ordinal) || name == "COPYING");
    verified &= Check($"xorg/font/{repo} 挑出来的 {files.Count} 个文件", Digest(files), sha256);
    xorgFiles.Add((repo, commit, directory, files));
}

Console.WriteLine($"GNU Unifont {UnifontVersion}");
byte[] unifontGz = await http.GetByteArrayAsync($"https://unifoundry.com/pub/unifont/unifont-{UnifontVersion}/font-builds/unifont-{UnifontVersion}.bdf.gz");
verified &= Check($"unifont-{UnifontVersion}.bdf.gz", Convert.ToHexStringLower(SHA256.HashData(unifontGz)), UnifontSha256);
byte[] ofl = await http.GetByteArrayAsync("https://unifoundry.com/OFL-1.1.txt");
verified &= Check("OFL-1.1.txt", Convert.ToHexStringLower(SHA256.HashData(ofl)), OflSha256);

Console.WriteLine($"xorg/font/alias @ {AliasCommit[..12]}");
List<(string Name, byte[] Bytes)> aliasFiles = await ArchiveFilesAsync("alias", AliasCommit, name => name is "misc/fonts.alias" or "COPYING");
verified &= Check($"xorg/font/alias 挑出来的 {aliasFiles.Count} 个文件", Digest(aliasFiles), AliasSha256);
if (!verified)
{
    Console.Error.WriteLine("有内容与固定的摘要对不上,输出目录没有动。");
    return 1;
}

// 旧数据整个换掉(包括早先放在 Data 根目录、裁剪过的那几份 BDF)。
foreach (string old in Directory.EnumerateFiles(output, "*.bdf").Concat(Directory.EnumerateFiles(output, "*.br", SearchOption.AllDirectories)))
{
    File.Delete(old);
}

foreach ((string repo, string commit, string directory, List<(string Name, byte[] Bytes)> files) in xorgFiles)
{
    foreach ((string name, byte[] bytes) in files)
    {
        if (name == "COPYING")
        {
            AppendLicense(repo, commit, directory, bytes);
        }
        else
        {
            Store(directory, name, bytes);
        }
    }
}

using (MemoryStream bdf = new())
{
    using (GZipStream gz = new(new MemoryStream(unifontGz), CompressionMode.Decompress))
    {
        gz.CopyTo(bdf);
    }
    Store("misc", "unifont.bdf", bdf.ToArray());
    string copyright = BdfProperty(bdf.ToArray(), "COPYRIGHT") ?? throw new InvalidDataException("unifont.bdf 没有 COPYRIGHT");
    await File.WriteAllTextAsync(Path.Combine(output, "LICENSE-unifont.txt"),
        $"GNU Unifont {UnifontVersion} (misc/unifont.bdf.br), from https://unifoundry.com/unifont/\n\n{copyright}\n\n"
        + "VelaShell.XServer redistributes this font under the SIL Open Font License, Version 1.1 (one of the two licenses offered),\n"
        + "unmodified apart from Brotli compression. The license follows.\n\n"
        + Encoding.UTF8.GetString(ofl).Replace("\r\n", "\n", StringComparison.Ordinal));
}

foreach ((string name, byte[] bytes) in aliasFiles)
{
    if (name == "COPYING")
    {
        AppendLicense("alias", AliasCommit, "misc/fonts.alias", bytes);
    }
    else
    {
        await File.WriteAllBytesAsync(Path.Combine(output, "misc", "fonts.alias"), bytes);
    }
}
await File.WriteAllTextAsync(Path.Combine(output, "LICENSE-xorg-fonts.txt"), xorgLicenses.ToString());

foreach ((string directory, List<(string File, string Xlfd)> entries) in fontsDir)
{
    StringBuilder dir = new();
    dir.Append(entries.Count).Append('\n');
    foreach ((string file, string xlfd) in entries.OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Xlfd, StringComparer.Ordinal))
    {
        dir.Append(file).Append(' ').Append(xlfd).Append('\n');
    }
    await File.WriteAllTextAsync(Path.Combine(output, directory, "fonts.dir"), dir.ToString());
}

await File.WriteAllTextAsync(Path.Combine(output, "charsets.txt"), charsets);
Console.WriteLine($"完成:{fontsDir.Values.Sum(d => d.Select(e => e.File).Distinct().Count())} 份字体、"
    + $"{fontsDir.Values.Sum(d => d.Count)} 个名字 → {output}");
return 0;

void Store(string directory, string name, byte[] bytes)
{
    string file = Path.GetFileName(name) + ".br";
    string xlfd = FontLine(bytes) ?? throw new InvalidDataException($"{name} 没有 FONT 一行");
    Directory.CreateDirectory(Path.Combine(output, directory));
    using (FileStream fs = File.Create(Path.Combine(output, directory, file)))
    using (BrotliStream brotli = new(fs, CompressionLevel.SmallestSize))
    {
        brotli.Write(bytes);
    }
    if (!fontsDir.TryGetValue(directory, out List<(string File, string Xlfd)>? entries))
    {
        fontsDir[directory] = entries = [];
    }
    // mkfontdir 的习惯:名字一律小写。ISO10646-1 的字体再按覆盖面派生单字节字符集的名字(同一个文件)。
    xlfd = xlfd.ToLowerInvariant();
    entries.Add((file, xlfd));
    if (xlfd.EndsWith("-iso10646-1", StringComparison.Ordinal))
    {
        string stem = xlfd[..^"iso10646-1".Length];
        entries.AddRange(DerivedCharsets(bytes, charsetTable).Select(charset => (file, stem + charset)));
    }
}

static bool Check(string what, string actual, string expected)
{
    if (actual == expected)
    {
        return true;
    }
    Console.Error.WriteLine($"{what} 的 SHA-256 对不上:固定的是 {expected},实际是 {actual}");
    return false;
}

// 从归档里挑出来的一组文件的摘要:按文件名排序,每个文件一行「文件名 文件内容的 SHA-256」,再对这几行整体算 SHA-256。
static string Digest(List<(string Name, byte[] Bytes)> files)
{
    StringBuilder lines = new();
    foreach ((string name, byte[] bytes) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
    {
        lines.Append(name).Append(' ').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
    }
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
}

void AppendLicense(string repo, string commit, string usedFor, byte[] copying) =>
    xorgLicenses.Append("\n==== xorg/font/").Append(repo).Append(" @ ").Append(commit).Append(" (").Append(usedFor).Append(") ====\n\n")
        .Append(Encoding.Latin1.GetString(copying).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()).Append('\n');

static string? BdfProperty(byte[] bdf, string name)
{
    using StreamReader reader = new(new MemoryStream(bdf), Encoding.UTF8);
    while (reader.ReadLine() is { } line && line != "ENDPROPERTIES")
    {
        if (line.StartsWith(name + " ", StringComparison.Ordinal))
        {
            return line[(name.Length + 1)..].Trim().Trim('"');
        }
    }
    return null;
}

static string? FontLine(byte[] bdf)
{
    using StreamReader reader = new(new MemoryStream(bdf), Encoding.Latin1);
    while (reader.ReadLine() is { } line)
    {
        if (line.StartsWith("FONT ", StringComparison.Ordinal))
        {
            return line[5..].Trim();
        }
        if (line.StartsWith("STARTCHAR", StringComparison.Ordinal))
        {
            break;
        }
    }
    return null;
}

async Task<List<(string Name, byte[] Bytes)>> ArchiveFilesAsync(string repo, string commit, Func<string, bool> pick)
{
    // GitLab 的仓库归档:顶层目录是 <repo>-<commit>/。
    string url = $"https://gitlab.freedesktop.org/xorg/font/{repo}/-/archive/{commit}/{repo}-{commit}.tar.gz";
    await using Stream archive = new GZipStream(new MemoryStream(await http.GetByteArrayAsync(url)), CompressionMode.Decompress);
    using TarReader tar = new(archive);
    List<(string, byte[])> files = [];
    while (await tar.GetNextEntryAsync() is { } entry)
    {
        int slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
        string name = slash < 0 ? entry.Name : entry.Name[(slash + 1)..];
        if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data && pick(name))
        {
            using MemoryStream bytes = new();
            await data.CopyToAsync(bytes);
            files.Add((name, bytes.ToArray()));
        }
    }
    return files;
}

// 单字节字符集:XLFD 的 CHARSET_REGISTRY-CHARSET_ENCODING → 代码页。只列 .NET 的代码页表里有的(没有 ISO8859-10 / 11 / 14 / 16)。
static string Charsets(Dictionary<string, int[]> table)
{
    (string Name, int CodePage)[] charsets =
    [
        ("iso8859-2", 28592), ("iso8859-3", 28593), ("iso8859-4", 28594), ("iso8859-5", 28595), ("iso8859-6", 28596),
        ("iso8859-7", 28597), ("iso8859-8", 28598), ("iso8859-9", 28599), ("iso8859-13", 28603), ("iso8859-15", 28605),
        ("koi8-r", 20866), ("koi8-u", 21866),
    ];
    StringBuilder text = new();
    text.Append("# 单字节字符集 → Unicode(由 scripts/xserver/fonts/build-fonts.cs 从 .NET 的代码页表导出,不要手改)。\n");
    text.Append("# 每行:XLFD 字符集名,然后 0x80–0xFF 各字节的 Unicode 码位(十六进制;- = 这个字节没有字符)。0x00–0x7F 一律与 ASCII 相同。\n");
    foreach ((string name, int codePage) in charsets)
    {
        Encoding encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)
            ?? throw new InvalidOperationException($"代码页 {codePage} 不可用");
        text.Append(name);
        foreach (int code in table[name] = Table(encoding))
        {
            text.Append(' ').Append(code < 0 ? "-" : code.ToString("x4", CultureInfo.InvariantCulture));
        }
        text.Append('\n');
    }
    return text.ToString();
}

// 0x80–0xFF 各字节的 Unicode 码位;没定义的字节(.NET 解成 U+FFFD 或私用区的 U+F7xx)为 -1。
static int[] Table(Encoding encoding) =>
    [.. Enumerable.Range(0x80, 0x80).Select(b => encoding.GetString([(byte)b])[0])
        .Select(c => c is '�' or (>= '' and <= '') ? -1 : c)];

// ISO10646-1 字体还以哪些单字节字符集的名字出现(与 mkfontdir -e 一样把它们写进 fonts.dir):
// ASCII 可见字符齐全,并且这个字符集里定义了的可见字符(U+00A0 起)一个不缺。ISO8859-1 就是 Unicode 的前 256 个码位。
static IEnumerable<string> DerivedCharsets(byte[] bdf, Dictionary<string, int[]> table)
{
    HashSet<int> codes = [];
    using (StreamReader reader = new(new MemoryStream(bdf), Encoding.Latin1))
    {
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("ENCODING ", StringComparison.Ordinal)
                && int.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture, out int code))
            {
                codes.Add(code);
            }
        }
    }
    if (!Enumerable.Range(0x20, 0x5F).All(codes.Contains))
    {
        yield break;
    }
    if (Enumerable.Range(0xA0, 0x60).All(codes.Contains))
    {
        yield return "iso8859-1";
    }
    foreach ((string name, int[] map) in table)
    {
        if (map.Where(c => c >= 0xA0).All(codes.Contains))
        {
            yield return name;
        }
    }
}
