using System.Globalization;
using System.Text;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>插件日志尾部的一次读取结果(插件管理页「查看日志」)。</summary>
/// <param name="Text">尾部正文;文件还不存在时为空串。</param>
/// <param name="FilePath">当前在写的那份日志文件;插件 id 不能当文件名时为 null。</param>
/// <param name="Truncated">是否只取了尾部(更早的内容在文件里、或已经轮转掉)。</param>
public sealed record PluginLogTail(string Text, string? FilePath, bool Truncated);

/// <summary>
/// 每个插件每天一份日志文件:<c>&lt;日志目录&gt;/plugins/&lt;安全插件 id&gt;-yyyyMMdd.log</c>。
/// 不做大小轮转,启动时按天清理过期文件。
/// </summary>
/// <remarks>
/// <para>
/// 新格式把日期放在安全插件 id 与 <c>.log</c> 之间,因此不同日期和相近 id 不会互相覆盖。
/// 旧版 <c>.log</c>、<c>.log.1</c> 与 <c>.1.log</c> 文件由 <see cref="SweepUninstalled" /> 按兼容规则清理或接续。
/// </para>
/// <para>
/// 插件的日志原先只进宿主的 Trace(<c>velashell-yyyyMMdd.log</c>,带 <c>[Plugin:id]</c> / <c>[PluginHost:id]</c> 前缀),
/// 与宿主自己的几十种诊断输出混在一起;异常堆栈的续行没有前缀,按前缀筛会丢掉;一天的文件能到 64 MB,
/// 为看一个插件的最后几十行去扫它不划算。用户报「插件不工作」时第一件要看的就是这个插件自己的日志,
/// 所以按插件 id 另落一份 —— 宿主日志里那份照旧,诊断包也照旧只收宿主日志(这个子目录不进包)。
/// </para>
/// <para>
/// 写入的来源都在宿主侧,不需要 SDK 新契约:插件经 <c>IPluginLogger</c> 写的(进程内直接调、隔离进程经 RPC 转来)、
/// 隔离进程的 stdout / stderr(未处理异常的堆栈就在这里),以及宿主替它记的激活 / 失败 / 崩溃。
/// </para>
/// <para><b>任何情况下都不抛异常</b>(同 <c>RollingFileTraceListener</c>):日志写不进去是小事,把插件或宿主带崩不是。</para>
/// </remarks>
/// <param name="directory">插件日志目录(不存在时第一次写入时建)。</param>
/// <param name="maxBytes">兼容旧调用方保留的参数;按天日志不再按大小轮转。</param>
public sealed class PluginLogFiles(string directory, long maxBytes = PluginLogFiles.DefaultMaxBytes)
{
    /// <summary>兼容旧调用方保留的单文件上限常量;按天日志不使用。</summary>
    public const long DefaultMaxBytes = 1024 * 1024;

    /// <summary>插件日志默认保留天数。</summary>
    public const int DefaultRetentionDays = 7;

    /// <summary>「查看日志」默认取的尾部大小。</summary>
    public const int DefaultTailBytes = 256 * 1024;

    private readonly Lock _gate = new();

    /// <summary>插件日志目录。</summary>
    public string Directory { get; } = directory;

    /// <summary>兼容旧调用方保留的单个文件上限;新格式不按大小轮转。</summary>
    public long MaxBytes { get; } = maxBytes;

    /// <summary>旧格式当前日志的后缀。</summary>
    public const string CurrentSuffix = ".log";

    /// <summary>旧格式轮转日志的后缀。</summary>
    public const string RotatedSuffix = ".log.1";

    /// <summary>按天日志的日期部分格式。</summary>
    public const string DateSuffixFormat = "yyyyMMdd";

    /// <summary>旧版本的轮转后缀:与 id 以 <c>.1</c> 结尾的插件的当前日志撞名。</summary>
    private const string LegacyRotatedSuffix = ".1" + CurrentSuffix;

    /// <summary>当天日志路径;插件 id 不能当文件名时为 null。</summary>
    public string? PathFor(string pluginId) => IsSafeFileName(pluginId)
        ? Path.Combine(Directory, pluginId + "-" + DateTime.Now.ToString(DateSuffixFormat, CultureInfo.InvariantCulture) + CurrentSuffix)
        : null;

    /// <summary>旧格式轮转日志路径,供现有调用方兼容使用。</summary>
    public string? RotatedPathFor(string pluginId) => IsSafeFileName(pluginId) ? Path.Combine(Directory, pluginId + RotatedSuffix) : null;

    /// <summary>追加一行到当天文件(自动加时间戳;文本里自带的换行原样保留)。</summary>
    public void Append(string pluginId, string text)
    {
        if (PathFor(pluginId) is not { } path)
        {
            return;
        }
        try
        {
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(path, line, Utf8NoBom);
            }
        }
        catch
        {
            // 日志写失败绝不能影响调用方。
        }
    }

    /// <summary>
    /// 启动清扫:删掉不在 <paramref name="installed" /> 里的按天日志,并兼容清理旧版的
    /// <c>&lt;id&gt;.log</c>、<c>&lt;id&gt;.log.1</c> 与 <c>&lt;id&gt;.1.log</c>。
    /// 旧版轮转名在插件 id 可能以 <c>.1</c> 结尾时有歧义,只有名单能确认归属;确认属于仍安装插件时会接回
    /// <c>&lt;id&gt;.log.1</c>,以保留旧日志读取语义。失败静默忽略。
    /// </summary>
    public void SweepUninstalled(IReadOnlySet<string> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        lock (_gate)
        {
            string[] names;
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return;
                }
                names = [.. System.IO.Directory.EnumerateFiles(Directory).Select(Path.GetFileName).OfType<string>()];
            }
            catch
            {
                return;
            }
            foreach (string name in names)
            {
                try
                {
                    SweepOne(name, installed);
                }
                catch
                {
                    // 删不掉(被占用)就留着,下次启动再试。
                }
            }
        }
    }

    /// <summary>启动时清理超过保留期的按天日志,并按文件时间清理无法解析日期的旧日志。</summary>
    public void CleanupExpired(int retentionDays = DefaultRetentionDays)
    {
        if (retentionDays < 1)
        {
            return;
        }
        DateTime today = DateTime.Now.Date;
        DateTime firstKeptDate = today.AddDays(-(retentionDays - 1));
        DateTime oldCutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        lock (_gate)
        {
            string[] names;
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return;
                }
                names = [.. System.IO.Directory.EnumerateFiles(Directory).Select(Path.GetFileName).OfType<string>()];
            }
            catch
            {
                return;
            }
            foreach (string name in names)
            {
                try
                {
                    if (TryParseDailyFileName(name, out _, out DateTime date))
                    {
                        if (date < firstKeptDate)
                        {
                            File.Delete(Path.Combine(Directory, name));
                        }
                    }
                    else if (TryParseLegacyFileName(name, out _)
                             && File.GetLastWriteTimeUtc(Path.Combine(Directory, name)) < oldCutoffUtc)
                    {
                        File.Delete(Path.Combine(Directory, name));
                    }
                }
                catch
                {
                    // 单个文件失败不影响其它日志,下次启动再试。
                }
            }
        }
    }

    /// <summary>清扫一个文件(规则见 <see cref="SweepUninstalled" />)。调用方须持锁。</summary>
    private void SweepOne(string name, IReadOnlySet<string> installed)
    {
        string path = Path.Combine(Directory, name);
        if (TryParseDailyFileName(name, out string? dailyId, out _))
        {
            if (dailyId is not null && !installed.Contains(dailyId))
            {
                File.Delete(path);
            }
            return;
        }
        if (name.EndsWith(RotatedSuffix, StringComparison.Ordinal))
        {
            string id = name[..^RotatedSuffix.Length];
            if (IsSafeFileName(id) && !installed.Contains(id))
            {
                File.Delete(path);
            }
            return;
        }
        if (!name.EndsWith(CurrentSuffix, StringComparison.Ordinal))
        {
            return;
        }
        string stem = name[..^CurrentSuffix.Length];
        if (!IsSafeFileName(stem) || installed.Contains(stem))
        {
            return;
        }
        if (name.EndsWith(LegacyRotatedSuffix, StringComparison.Ordinal)
            && name[..^LegacyRotatedSuffix.Length] is { } owner
            && installed.Contains(owner)
            && RotatedPathFor(owner) is { } rotated)
        {
            if (File.Exists(rotated))
            {
                File.Delete(path);
            }
            else
            {
                File.Move(path, rotated);
            }
            return;
        }
        File.Delete(path);
    }

    /// <summary>删掉这个插件的全部按天日志,并清掉无歧义的旧格式文件。失败静默忽略。</summary>
    public void Delete(string pluginId)
    {
        if (!IsSafeFileName(pluginId))
        {
            return;
        }
        lock (_gate)
        {
            string[] names;
            try
            {
                names = System.IO.Directory.Exists(Directory)
                    ? [.. System.IO.Directory.EnumerateFiles(Directory).Select(Path.GetFileName).OfType<string>()]
                    : [];
            }
            catch
            {
                return;
            }
            foreach (string name in names)
            {
                if (!IsOwnedBy(pluginId, name))
                {
                    continue;
                }
                try
                {
                    File.Delete(Path.Combine(Directory, name));
                }
                catch
                {
                    // 删不掉(被占用)就留着,下次启动再试。
                }
            }
        }
    }

    /// <summary>
    /// 读日志尾部:新格式只读取磁盘上实际存在的最新日期文件;旧格式没有日期时保留当前+轮转的读取兼容。
    /// 从中间截断时丢掉不完整的第一行(也就不会切坏一个多字节字符)。读不了就返回空正文。
    /// </summary>
    public PluginLogTail ReadTail(string pluginId, int maxBytes = DefaultTailBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (!IsSafeFileName(pluginId))
        {
            return new(string.Empty, null, false);
        }
        try
        {
            lock (_gate)
            {
                string? latest = FindLatestDailyPath(pluginId);
                if (latest is not null)
                {
                    return ReadSingleTail(latest, maxBytes);
                }

                string? current = Path.Combine(Directory, pluginId + CurrentSuffix);
                string? rotated = Path.Combine(Directory, pluginId + RotatedSuffix);
                bool hasCurrent = File.Exists(current);
                bool hasRotated = File.Exists(rotated);
                if (!hasCurrent && !hasRotated)
                {
                    return new(string.Empty, null, false);
                }
                (byte[] currentBytes, bool currentCut) = ReadEnd(current, maxBytes);
                byte[] older = [];
                bool olderCut = false;
                if (!currentCut && currentBytes.Length < maxBytes)
                {
                    (older, olderCut) = ReadEnd(rotated, maxBytes - currentBytes.Length);
                }
                bool cutMidLine = currentCut || olderCut;
                bool olderLeftOut = older.Length == 0 && currentBytes.Length >= maxBytes && hasRotated;
                byte[] all = [.. older, .. currentBytes];
                int start = cutMidLine ? FirstCompleteLineOffset(all) : 0;
                string path = hasCurrent ? current : rotated;
                return new(Encoding.UTF8.GetString(all, start, all.Length - start), path, cutMidLine || olderLeftOut);
            }
        }
        catch
        {
            return new(string.Empty, null, false);
        }
    }

    private static PluginLogTail ReadSingleTail(string path, int maxBytes)
    {
        (byte[] bytes, bool cut) = ReadEnd(path, maxBytes);
        int start = cut ? FirstCompleteLineOffset(bytes) : 0;
        return new(Encoding.UTF8.GetString(bytes, start, bytes.Length - start), path, cut);
    }

    private string? FindLatestDailyPath(string pluginId)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return null;
        }
        string? latestPath = null;
        DateTime latestDate = DateTime.MinValue;
        DateTime latestWrite = DateTime.MinValue;
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory))
        {
            string name = Path.GetFileName(path);
            if (!TryParseDailyFileName(name, out string? id, out DateTime date)
                || !string.Equals(id, pluginId, StringComparison.Ordinal))
            {
                continue;
            }
            DateTime write = File.GetLastWriteTimeUtc(path);
            if (date > latestDate || date == latestDate && write > latestWrite)
            {
                latestPath = path;
                latestDate = date;
                latestWrite = write;
            }
        }
        return latestPath;
    }

    private static bool IsOwnedBy(string pluginId, string name)
    {
        if (TryParseDailyFileName(name, out string? dailyId, out _))
        {
            return string.Equals(dailyId, pluginId, StringComparison.Ordinal);
        }
        return name.Equals(pluginId + CurrentSuffix, StringComparison.Ordinal)
            || name.Equals(pluginId + RotatedSuffix, StringComparison.Ordinal)
            || name.Equals(pluginId + LegacyRotatedSuffix, StringComparison.Ordinal);
    }

    private static bool TryParseDailyFileName(string name, out string? pluginId, out DateTime date)
    {
        pluginId = null;
        date = default;
        if (!name.EndsWith(CurrentSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        string stem = name[..^CurrentSuffix.Length];
        int separator = stem.Length - DateSuffixFormat.Length - 1;
        if (separator < 1 || stem[separator] != '-')
        {
            return false;
        }
        string dateText = stem[(separator + 1)..];
        if (!DateTime.TryParseExact(dateText, DateSuffixFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
        {
            return false;
        }
        string id = stem[..separator];
        if (!IsSafeFileName(id))
        {
            date = default;
            return false;
        }
        pluginId = id;
        return true;
    }

    private static bool TryParseLegacyFileName(string name, out string? pluginId)
    {
        pluginId = null;
        if (name.EndsWith(RotatedSuffix, StringComparison.Ordinal))
        {
            string id = name[..^RotatedSuffix.Length];
            if (!IsSafeFileName(id)) return false;
            pluginId = id;
            return true;
        }
        if (!name.EndsWith(CurrentSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        string stem = name[..^CurrentSuffix.Length];
        if (!IsSafeFileName(stem))
        {
            return false;
        }
        pluginId = stem;
        return true;
    }

    internal static bool TryGetPluginLogInfo(string fileName, out string? pluginId, out DateTime? date)
    {
        if (TryParseDailyFileName(fileName, out pluginId, out DateTime dailyDate))
        {
            date = dailyDate;
            return true;
        }
        date = null;
        return TryParseLegacyFileName(fileName, out pluginId);
    }

    private static int FirstCompleteLineOffset(byte[] bytes)
    {
        int newline = Array.IndexOf(bytes, (byte)'\n');
        return newline < 0 ? bytes.Length : newline + 1;
    }

    /// <summary>读文件最后 <paramref name="maxBytes" /> 字节;返回内容与是否从中间截断。文件不存在时为空。</summary>
    private static (byte[] Bytes, bool Cut) ReadEnd(string path, int maxBytes)
    {
        if (maxBytes <= 0 || !File.Exists(path))
        {
            return ([], false);
        }
        // 共享读写与删除:读的时候插件照写、照轮转。
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        int count = (int)Math.Min(length, maxBytes);
        stream.Seek(length - count, SeekOrigin.Begin);
        byte[] buffer = new byte[count];
        stream.ReadExactly(buffer);
        // 从文件开头读起时去掉 UTF-8 BOM(手动编辑过、或别的工具写过的文件可能带),免得正文里夹一个零宽字符。
        if (length == count && buffer.AsSpan().StartsWith(Utf8Bom))
        {
            buffer = buffer[Utf8Bom.Length..];
        }
        return (buffer, length > count);
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>不带 BOM:新文件开头的 BOM 读尾部时会夹在正文里,轮转之后更是夹在中间。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>插件 id 的字符集是 <c>[a-z0-9.-]</c>;这里再守一道,免得一个怪 id 拼出目录外的路径。</summary>
    private static bool IsSafeFileName(string pluginId) =>
        !string.IsNullOrEmpty(pluginId) && pluginId.Length <= 128
        && char.IsAsciiLetterOrDigit(pluginId[0])
        && pluginId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
