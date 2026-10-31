using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>诊断包里一个插件的一行:id、版本、状态与运行方式;加载失败时带上原因。</summary>
public sealed record DiagnosticPluginInfo(string Id, string? Version, string State, string? HostMode, bool IsDevelopment, string? Error);

/// <summary>导出时可以另外勾选的一类日志。</summary>
public enum DiagnosticLogKind
{
    /// <summary>传输记录 <c>transfer-yyyyMMdd.log</c>(设置 → 文件传输 → 日志记录,目录可改):每次传输的本机与远端路径。</summary>
    Transfer,

    /// <summary>外部编辑日志 <c>remote-edit.log</c>:用外部编辑器打开、保存、回传远程文件的经过。</summary>
    RemoteEdit,

    /// <summary>VcXsrv 的输出 <c>vcxsrv.log</c>(Windows 上用外部 X 服务器时)。</summary>
    VcXsrv,

    /// <summary>某个插件自己的日志 <c>logs/plugins/&lt;id&gt;.log</c>(连同轮转下来的 <c>&lt;id&gt;.log.1</c>)。</summary>
    Plugin
}

/// <summary>可以另外勾选进包的一类日志:它是什么、有哪几份文件。</summary>
/// <param name="Id">勾选项的标识:<c>transfer</c>、<c>remote-edit</c>、<c>vcxsrv</c>、<c>plugin:&lt;插件 id&gt;</c>。</param>
/// <param name="Kind">哪一类。</param>
/// <param name="PluginId">插件日志的插件 id;其余为 null。</param>
/// <param name="Files">这一类的文件(完整路径,新的在前)。</param>
/// <param name="Bytes">这些文件现在一共多大。</param>
public sealed record DiagnosticLogOption(string Id, DiagnosticLogKind Kind, string? PluginId, IReadOnlyList<string> Files, long Bytes);

/// <summary>写一个诊断包要的材料。</summary>
/// <param name="AppVersion">应用版本(关于页显示的那个)。</param>
/// <param name="LogsDirectory">日志目录;没有时包里只有概况。</param>
/// <param name="Plugins">已发现的插件。</param>
/// <param name="Redactor">脱敏器;包里每个文件都过一遍。</param>
/// <param name="Optional">用户在导出框里另外勾上的几类日志(见 <see cref="DiagnosticBundle.FindOptionalLogs" />);不给就一类都不收。</param>
public sealed record DiagnosticBundleRequest(
    string AppVersion,
    string? LogsDirectory,
    IReadOnlyList<DiagnosticPluginInfo> Plugins,
    DiagnosticRedactor Redactor,
    IReadOnlyList<DiagnosticLogOption>? Optional = null);

/// <summary>写好的诊断包:收进去几份日志,各类脱敏换了多少处。</summary>
public sealed record DiagnosticBundleResult(int LogFiles, IReadOnlyDictionary<string, int> Redactions);

/// <summary>
/// 导出诊断包:日志、崩溃记录、版本与平台、已装插件,脱敏后打成一个 zip,只落在用户选的位置。
/// </summary>
/// <remarks>
/// <para>
/// 报障时用户原先要被教着去翻 <c>~/.velashell/logs</c>,还得自己判断哪几份有用、里面有没有不想给人看的东西。
/// 这里一次收齐,先脱敏再写(见 <see cref="DiagnosticRedactor" />),不上传、不外呼 —— 发不发、发给谁由用户决定。
/// </para>
/// <para>
/// <b>默认只收诊断日志 <c>velashell-*.log</c> 与崩溃记录 <c>crash-*.txt</c></b>。同一个目录里的传输记录、外部编辑日志、
/// <c>vcxsrv.log</c> 与按插件的日志满是本机与远端的路径、文件名,通用规则遮不了文件名;它们只在导出框里勾上了才收
/// (<see cref="DiagnosticLogOption" />,默认一个都不勾),照样遮盖。会话日志(<c>session-*.log</c>,终端原始输出)与其余文件一律不收
/// (维护者 2026-10-25 定)。
/// </para>
/// <para>
/// 设置与连接配置<b>不</b>收:那里有代理口令、密钥路径、共享凭据,脱敏规则兜不全。
/// </para>
/// </remarks>
public static class DiagnosticBundle
{
    /// <summary>
    /// 收进包的日志原文总量上限;从最新的往旧的收,放不下的那份跳过。默认那两类与勾选的那几类各算各的:
    /// 勾上的插件日志不该被一份刷屏的宿主日志挤掉,反过来也一样。
    /// </summary>
    internal const long MaxLogBytes = 32L * 1024 * 1024;

    /// <summary>插件日志在日志目录下的子目录(与 <c>PluginLogFiles</c> 的装配一致)。</summary>
    internal const string PluginLogsFolder = "plugins";

    /// <summary>
    /// 列出可以另外勾选的几类日志,只列眼下确有文件的;顺序就是导出框里的顺序(传输记录、外部编辑、VcXsrv、各插件按 id)。
    /// </summary>
    /// <param name="logsDirectory">日志目录。</param>
    /// <param name="transferLogDirectory">传输记录所在的目录(设置里可改;默认就是日志目录)。</param>
    public static IReadOnlyList<DiagnosticLogOption> FindOptionalLogs(string? logsDirectory, string? transferLogDirectory)
    {
        var options = new List<DiagnosticLogOption>();
        AddIfAny("transfer", DiagnosticLogKind.Transfer, null, Files(transferLogDirectory, "transfer-*.log"));
        AddIfAny("remote-edit", DiagnosticLogKind.RemoteEdit, null, Files(logsDirectory, "remote-edit.log"));
        AddIfAny("vcxsrv", DiagnosticLogKind.VcXsrv, null, Files(logsDirectory, "vcxsrv.log"));
        string? pluginLogs = string.IsNullOrWhiteSpace(logsDirectory) ? null : Path.Combine(logsDirectory, PluginLogsFolder);
        foreach (IGrouping<string, FileInfo> plugin in Files(pluginLogs, "*" + PluginLogFiles.CurrentSuffix)
                     .Concat(Files(pluginLogs, "*" + PluginLogFiles.RotatedSuffix))
                     .GroupBy(file => PluginIdOf(file.Name), StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            AddIfAny("plugin:" + plugin.Key, DiagnosticLogKind.Plugin, plugin.Key, plugin);
        }
        return options;

        void AddIfAny(string id, DiagnosticLogKind kind, string? pluginId, IEnumerable<FileInfo> files)
        {
            FileInfo[] found = [.. files.OrderByDescending(file => file.LastWriteTimeUtc)];
            if (found.Length > 0)
                options.Add(new(id, kind, pluginId, [.. found.Select(file => file.FullName)], found.Sum(file => file.Length)));
        }
    }

    /// <summary>
    /// <c>velashell.ai.log.1</c>(轮转下来的那份)与 <c>velashell.ai.log</c> 都归插件 <c>velashell.ai</c>。
    /// <c>x.1.log</c> 归插件 <c>x.1</c>:那是它的当前日志(旧版本的轮转名与它撞名,见 <see cref="PluginLogFiles" />)。
    /// </summary>
    private static string PluginIdOf(string fileName) =>
        fileName.EndsWith(PluginLogFiles.RotatedSuffix, StringComparison.Ordinal)
            ? fileName[..^PluginLogFiles.RotatedSuffix.Length]
            : fileName[..^PluginLogFiles.CurrentSuffix.Length];

    /// <summary>写诊断包。已有同名文件时覆盖。</summary>
    /// <param name="zipPath">包的落盘位置。</param>
    /// <param name="request">材料。</param>
    /// <param name="cancellationToken">取消。</param>
    public static async Task<DiagnosticBundleResult> WriteAsync(string zipPath, DiagnosticBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(request);

        // 先写到旁边的临时文件、写完再换名:中途失败或取消不在用户选的位置留下半个包。
        // 名字带随机段、只新建不覆盖:旁边预先放好的同名文件或符号链接不会被顺着打开、截断
        string temporary = $"{zipPath}.{Guid.NewGuid():N}.partial";
        int logFiles = 0;
        IReadOnlyList<DiagnosticLogOption> optional = request.Optional ?? [];
        // 插件 id 长得像域名(velashell.ai),概况里、插件日志里到处都是:告诉脱敏器它们不是主机
        request.Redactor.KeepNames(request.Plugins.Select(plugin => plugin.Id)
            .Concat(optional.Select(option => option.PluginId).OfType<string>()));
        var included = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo log in LogFiles(request.LogsDirectory))
                {
                    if (await AddLogAsync(log, "logs/" + log.Name).ConfigureAwait(false)) logFiles++;
                }
                var optionalFiles = optional
                    .SelectMany(option => option.Files.Select(path => (Option: option, File: new FileInfo(path))))
                    .Where(entry => entry.File.Exists)
                    .OrderByDescending(entry => entry.File.LastWriteTimeUtc)
                    .ToList();
                foreach ((DiagnosticLogOption option, FileInfo file) in WithinBudget(optionalFiles, entry => entry.File.Length))
                {
                    string entryName = option.Kind == DiagnosticLogKind.Plugin
                        ? $"logs/{PluginLogsFolder}/{file.Name}"
                        : "logs/" + file.Name;
                    if (!await AddLogAsync(file, entryName).ConfigureAwait(false)) continue;
                    logFiles++;
                    included.Add(option.Id);
                }
                await AddTextAsync(zip, "summary.txt", request.Redactor.Redact(Summary(request)), cancellationToken).ConfigureAwait(false);
                // 说明最后写:要列出前面各个文件一共换了多少处
                await AddTextAsync(zip, "README.txt", ReadMe(request.Redactor.Counts, logFiles,
                    [.. optional.Select(option => option.Id).Where(included.Contains).Distinct()]), cancellationToken).ConfigureAwait(false);

                async Task<bool> AddLogAsync(FileInfo log, string entryName)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // 传输记录的目录可以设成日志目录本身,同一份文件不收两遍
                    if (!names.Add(entryName)) return false;
                    string? text = await ReadSharedAsync(log, cancellationToken).ConfigureAwait(false);
                    if (text is null) return false;
                    await AddTextAsync(zip, entryName, request.Redactor.Redact(text), cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }
            File.Move(temporary, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        return new DiagnosticBundleResult(logFiles, new Dictionary<string, int>(request.Redactor.Counts));
    }

    /// <summary>默认收的那两类:日志目录顶层的诊断日志 <c>velashell-*.log</c> 与崩溃记录 <c>crash-*.txt</c>,新的在前,总量封顶。</summary>
    /// <remarks>
    /// 同一目录里的其余文件不在这里收:会话日志(<c>session-*.log</c>,终端原始输出)隐私说明里是单独的一类,永远不进包;
    /// 传输记录、外部编辑日志、<c>vcxsrv.log</c> 要用户勾选(<see cref="FindOptionalLogs" />)。
    /// </remarks>
    internal static IEnumerable<FileInfo> LogFiles(string? directory) =>
        WithinBudget(
            [
                .. Files(directory, "velashell-*.log")
                    .Concat(Files(directory, "crash-*.txt"))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
            ],
            file => file.Length);

    /// <summary>
    /// 从前往后收、总量封顶:放不下的跳过、接着往后收 —— 一份超大的日志不能把它后面的崩溃记录全挤掉,更不能让包里一份日志都没有。
    /// </summary>
    private static IEnumerable<T> WithinBudget<T>(IEnumerable<T> items, Func<T, long> length)
    {
        long total = 0;
        foreach (T item in items)
        {
            long bytes = length(item);
            if (total + bytes > MaxLogBytes) continue;
            total += bytes;
            yield return item;
        }
    }

    /// <summary>
    /// 目录顶层名字对得上 <paramref name="pattern" />(<c>前缀*后缀</c> 或完整文件名)的文件;目录不在或读不了就是空。
    /// 名字再按前缀与后缀精确核一遍:Windows 上 <c>*.log</c> 还会把 <c>a.logx</c> 一类 8.3 短名对得上的文件带进来。
    /// </summary>
    private static IEnumerable<FileInfo> Files(string? directory, string pattern)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return [];
        int star = pattern.IndexOf('*');
        string prefix = star < 0 ? pattern : pattern[..star];
        string suffix = star < 0 ? string.Empty : pattern[(star + 1)..];
        try
        {
            return
            [
                .. new DirectoryInfo(directory).EnumerateFiles(pattern)
                    .Where(file => star < 0
                        ? file.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                        : file.Name.Length > prefix.Length + suffix.Length
                          && file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                          && file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Summary(DiagnosticBundleRequest request)
    {
        var text = new StringBuilder();
        void Line(string key, object? value) => text.Append(CultureInfo.InvariantCulture, $"{key}: {value}\n");
        Line("VelaShell", request.AppVersion);
        Line("Generated", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Line("OS", RuntimeInformation.OSDescription);
        Line("OS architecture", RuntimeInformation.OSArchitecture);
        Line("Process architecture", RuntimeInformation.ProcessArchitecture);
        Line(".NET", RuntimeInformation.FrameworkDescription);
        Line("UI culture", CultureInfo.CurrentUICulture.Name);
        Line("Process uptime", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture));
        text.Append('\n').Append(CultureInfo.InvariantCulture, $"Plugins ({request.Plugins.Count}):\n");
        foreach (DiagnosticPluginInfo plugin in request.Plugins.OrderBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(CultureInfo.InvariantCulture, $"  {plugin.Id} {plugin.Version ?? "?"} state={plugin.State}");
            if (!string.IsNullOrEmpty(plugin.HostMode)) text.Append(" mode=").Append(plugin.HostMode);
            if (plugin.IsDevelopment) text.Append(" dev");
            if (!string.IsNullOrWhiteSpace(plugin.Error)) text.Append(" error=").Append(plugin.Error.ReplaceLineEndings(" "));
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string ReadMe(IReadOnlyDictionary<string, int> counts, int logFiles, List<string> optional)
    {
        var text = new StringBuilder();
        text.Append(Strings.Get("DiagBundle_ReadMeTitle")).Append("\n\n");
        text.Append(Strings.Get("DiagBundle_ReadMeLocalOnly")).Append("\n\n");
        text.Append(CultureInfo.InvariantCulture, $"logs/: {logFiles} file(s)    summary.txt: version, platform, plugins\n");
        // 勾选进包的那几类照勾选项的标识列出(英文,同上一行):收包的人一眼看得出这包里除了默认的还有什么
        text.Append(CultureInfo.InvariantCulture, $"optional logs: {(optional.Count == 0 ? "(none)" : string.Join(", ", optional))}\n");
        text.Append(Strings.Get("DiagBundle_ReadMeExcluded")).Append("\n\n");
        text.Append(Strings.Get("DiagBundle_ReadMeRedacted")).Append('\n');
        if (counts.Count == 0) text.Append("  (none)\n");
        foreach ((string kind, int count) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"  {kind}: {count}\n");
        }
        return text.ToString();
    }

    /// <summary>日志可能正被追加写入:按共享读打开。读不了的跳过,不让一份日志挡住整个包。</summary>
    private static async Task<string?> ReadSharedAsync(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
