using System.Globalization;
using System.Text;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>插件日志尾部的一次读取结果(插件管理页「查看日志」)。</summary>
/// <param name="Text">尾部正文;文件还不存在时为空串。</param>
/// <param name="FilePath">当前在写的那份日志文件;插件 id 不能当文件名时为 null。</param>
/// <param name="Truncated">是否只取了尾部(更早的内容在文件里、或已经轮转掉)。</param>
public sealed record PluginLogTail(string Text, string? FilePath, bool Truncated);

/// <summary>
/// 每个插件一份日志文件:<c>&lt;日志目录&gt;/plugins/&lt;插件 id&gt;.log</c>,写满 <see cref="MaxBytes" /> 就轮转成
/// <c>&lt;插件 id&gt;.log.1</c>(只留这一份旧的),每个插件最多占约两倍上限。
/// </summary>
/// <remarks>
/// <para>
/// 轮转名不能是 <c>&lt;插件 id&gt;.1.log</c>(旧版本就是这么叫的):插件 id 可以以 <c>.1</c> 结尾,
/// <c>acme.tool</c> 的轮转件就是 <c>acme.tool.1</c> 的当前日志 —— 轮转会盖掉别人的日志,卸载会删掉它,
/// 「查看日志」还会把别人的行当成自己的前文显示出来(一个插件就能往另一个插件的日志里塞几行假的 <c>[Host]</c>)。
/// 当前日志总以 <c>.log</c> 结尾,<c>.log.1</c> 不会是任何插件的当前日志。旧名字留下的文件由 <see cref="SweepUninstalled" /> 认领。
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
/// <param name="maxBytes">单个文件的上限(字节);到了就轮转。</param>
public sealed class PluginLogFiles(string directory, long maxBytes = PluginLogFiles.DefaultMaxBytes)
{
    /// <summary>默认单文件 1 MB:正常插件一天写不了几 KB,刷屏的插件也只占 2 MB。</summary>
    public const long DefaultMaxBytes = 1024 * 1024;

    /// <summary>「查看日志」默认取的尾部大小。</summary>
    public const int DefaultTailBytes = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _sizes = [with(StringComparer.Ordinal)];

    /// <summary>插件日志目录。</summary>
    public string Directory { get; } = directory;

    /// <summary>单个文件的上限(字节)。</summary>
    public long MaxBytes { get; } = maxBytes;

    /// <summary>当前日志的后缀。</summary>
    public const string CurrentSuffix = ".log";

    /// <summary>轮转下来那一份的后缀(为什么不是 <c>.1.log</c> 见类型说明)。</summary>
    public const string RotatedSuffix = ".log.1";

    /// <summary>旧版本的轮转后缀:与 id 以 <c>.1</c> 结尾的插件的当前日志撞名,只在 <see cref="SweepUninstalled" /> 里认。</summary>
    private const string LegacyRotatedSuffix = ".1" + CurrentSuffix;

    /// <summary>当前在写的那份日志;插件 id 不能当文件名时为 null。</summary>
    /// <param name="pluginId">插件 id。</param>
    public string? PathFor(string pluginId) => IsSafeFileName(pluginId) ? Path.Combine(Directory, pluginId + CurrentSuffix) : null;

    /// <summary>轮转下来的上一份日志;插件 id 不能当文件名时为 null。</summary>
    /// <param name="pluginId">插件 id。</param>
    public string? RotatedPathFor(string pluginId) => IsSafeFileName(pluginId) ? Path.Combine(Directory, pluginId + RotatedSuffix) : null;

    /// <summary>追加一行(自动加时间戳;文本里自带的换行原样保留,异常堆栈因此是完整的)。</summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="text">日志正文,如 <c>[Warning] 连不上服务器</c>。</param>
    public void Append(string pluginId, string text)
    {
        if (PathFor(pluginId) is not { } path || RotatedPathFor(pluginId) is not { } rotated)
        {
            return;
        }
        try
        {
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            long bytes = Encoding.UTF8.GetByteCount(line);
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (!_sizes.TryGetValue(pluginId, out long size))
                {
                    size = File.Exists(path) ? new FileInfo(path).Length : 0;
                }
                if (size > 0 && size + bytes > MaxBytes)
                {
                    File.Move(path, rotated, overwrite: true);
                    size = 0;
                }
                File.AppendAllText(path, line, Utf8NoBom);
                _sizes[pluginId] = size + bytes;
            }
        }
        catch
        {
            // 见类型注释:日志写失败绝不能影响调用方。下次重新取文件大小。
            lock (_gate)
            {
                _sizes.Remove(pluginId);
            }
        }
    }

    /// <summary>
    /// 启动清扫:删掉不在 <paramref name="installed" /> 里的插件留下的日志 —— 插件目录在宿主没运行时被删掉
    /// (手动删、换机器拷数据)也算卸载,卸载那一路的 <see cref="Delete" /> 没机会跑,与插件数据、消息中心的卸载清扫同一口径。
    /// 顺带把旧版本留下的轮转件 <c>&lt;id&gt;.1.log</c> 改名成 <c>&lt;id&gt;.log.1</c>,免得升级后「查看日志」的前文丢了。
    /// 失败静默忽略(删不掉的下次启动再试)。
    /// </summary>
    /// <remarks>
    /// 旧轮转名只在这里认,因为单看文件名分不出它是谁的:<c>x.1.log</c> 既可能是 <c>x</c> 的旧轮转件,
    /// 也可能是插件 <c>x.1</c> 的当前日志。有了"装着哪些插件"这份名单才分得清 ——
    /// <c>x.1</c> 装着,它就是 <c>x.1</c> 的当前日志,不动;只有 <c>x</c> 装着,才是 <c>x</c> 的旧轮转件,改名接上
    /// (<c>x.log.1</c> 已经有了就删掉旧的,那份更新);两个都没装,就是孤儿。日志目录里不像插件日志的文件一律不碰。
    /// </remarks>
    /// <param name="installed">仍在盘上的插件 id(含被禁用、清单无效的 —— 修好或重新启用后日志应仍在)。</param>
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
                    // 删不掉(被占用)就留着:有大小上限,下次启动再试。
                }
            }
        }
    }

    /// <summary>清扫一个文件(规则见 <see cref="SweepUninstalled" />)。调用方须持锁。</summary>
    private void SweepOne(string name, IReadOnlySet<string> installed)
    {
        string path = Path.Combine(Directory, name);
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
        _sizes.Remove(stem);
    }

    /// <summary>删掉这个插件的日志(卸载时随插件数据一起清)。失败静默忽略。</summary>
    /// <remarks>
    /// 不删旧名字的 <c>&lt;id&gt;.1.log</c>:它可能是插件 <c>&lt;id&gt;.1</c> 的当前日志。真是这个插件的旧轮转件的话,
    /// 下次启动的 <see cref="SweepUninstalled" /> 会把它当孤儿清掉。
    /// </remarks>
    /// <param name="pluginId">插件 id。</param>
    public void Delete(string pluginId)
    {
        if (PathFor(pluginId) is not { } path || RotatedPathFor(pluginId) is not { } rotated)
        {
            return;
        }
        lock (_gate)
        {
            _sizes.Remove(pluginId);
            foreach (string file in (string[])[path, rotated])
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 删不掉(被占用)就留着:有大小上限,不会越攒越多。
                }
            }
        }
    }

    /// <summary>
    /// 读日志尾部:取当前文件的最后 <paramref name="maxBytes" /> 字节,不够再从轮转下来的那份补。
    /// 从中间截断时丢掉不完整的第一行(也就不会切坏一个多字节字符)。读不了就返回空正文。
    /// </summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="maxBytes">最多读多少字节。</param>
    public PluginLogTail ReadTail(string pluginId, int maxBytes = DefaultTailBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (PathFor(pluginId) is not { } path || RotatedPathFor(pluginId) is not { } rotated)
        {
            return new(string.Empty, null, false);
        }
        try
        {
            lock (_gate)
            {
                (byte[] current, bool currentCut) = ReadEnd(path, maxBytes);
                byte[] older = [];
                bool olderCut = false;
                if (!currentCut && current.Length < maxBytes)
                {
                    (older, olderCut) = ReadEnd(rotated, maxBytes - current.Length);
                }
                bool cutMidLine = currentCut || olderCut;
                // 当前那份正好读满、没轮到旧的那份:旧内容还在,只是这次没取。
                bool olderLeftOut = older.Length == 0 && current.Length >= maxBytes && File.Exists(rotated);
                byte[] all = [.. older, .. current];
                int start = 0;
                if (cutMidLine)
                {
                    // 截在一行中间:从第一个换行之后开始。
                    int newline = Array.IndexOf(all, (byte)'\n');
                    start = newline < 0 ? all.Length : newline + 1;
                }
                return new(Encoding.UTF8.GetString(all, start, all.Length - start), path, cutMidLine || olderLeftOut);
            }
        }
        catch
        {
            return new(string.Empty, path, false);
        }
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
