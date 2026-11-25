using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// 一处文本替换。<c>patch_local_file</c> / <c>patch_remote_file</c> 的 <c>edits</c> 数组里的一项。
/// </summary>
/// <param name="OldText">要被替换的原文。</param>
/// <param name="NewText">替换成的文本。</param>
/// <param name="ReplaceAll">替换所有出现处;默认要求恰好出现一次。</param>
public sealed record FileEdit(
    [property: Description("Exact text to replace, copied from the file WITHOUT the line-number prefixes that the read tools add.")] string OldText,
    [property: Description("Replacement text.")] string NewText,
    [property: Description("Replace every occurrence. Default false: OldText must then occur exactly once.")] bool ReplaceAll = false);

/// <summary>
/// 文件类工具的公共零件:按行区间读取、工作区内容搜索 / 文件名查找、远端 grep、多处编辑与文件改动事件。
/// </summary>
public sealed partial class AgentToolbox
{
    /// <summary>一次分段读取默认 / 最多返回的行数。</summary>
    internal const int MaxReadLines = 2000;

    /// <summary>单行超过这个长度就截断:一行压缩过的 JS 能把整个窗口吃光。</summary>
    private const int MaxReadLineChars = 2000;

    /// <summary>读完窗口后为了报出总行数,本地最多再往后扫多少字符。</summary>
    private const long LocalLineCountBudgetChars = 64L * 1024 * 1024;

    /// <summary>远端同上:往后多扫的每个字节都要经 SFTP 拉回来,所以给得小很多。</summary>
    private const long RemoteLineCountBudgetChars = 1024 * 1024;

    /// <summary>NUL 只在文件开头这么多字符里找:够认出绝大多数二进制格式,又不必扫完整份。</summary>
    private const int BinaryProbeChars = 8192;

    /// <summary>一次 <c>patch_*</c> 最多提交多少处编辑。</summary>
    private const int MaxEditsPerPatch = 50;

    /// <summary>搜索时遍历的文件数上限(防止对着一个超大目录树无止境地扫)。</summary>
    private const int MaxSearchFiles = 20000;

    /// <summary>大于这个尺寸的文件不参与内容搜索。</summary>
    private const long MaxSearchFileBytes = 16L * 1024 * 1024;

    /// <summary>搜索结果的总字符上限。</summary>
    private const int MaxSearchOutputChars = 64 * 1024;

    /// <summary>搜索结果里单行的字符上限。</summary>
    private const int MaxSearchLineChars = 500;

    /// <summary>搜索与查找默认跳过的目录:版本库元数据、依赖与构建产物,命中它们几乎从来不是想要的结果。</summary>
    internal static readonly string[] SkippedSearchDirectories =
        [".git", ".svn", ".hg", ".vs", ".idea", "node_modules", "bin", "obj", "__pycache__"];

    private static readonly HashSet<string> SkippedSearchDirectorySet = new(SkippedSearchDirectories, StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// write / patch / delete / rename(本地与远端)<b>成功写入之后</b>发出的改动通知,供聊天面板汇总本轮改了哪些文件。
    /// </summary>
    /// <remarks>
    /// 在执行工具的那条线程上同步调用,订阅方需要自己切回 UI 线程。订阅方抛出的异常会被吞掉,不影响工具结果。
    /// 子 Agent 的工具箱没有自己的订阅时沿用父工具箱的订阅。
    /// 只有存在订阅时才会为「改动前全文」多读一次文件。
    /// </remarks>
    public Action<FileChange>? FileChanged { get; set; }

    private Action<FileChange>? FileChangedHandler => FileChanged ?? _parentToolbox?.FileChanged;

    private void RaiseFileChanged(FileChange change)
    {
        if (FileChangedHandler is not { } handler)
        {
            return;
        }
        try
        {
            handler(change);
        }
        catch (Exception)
        {
            // 订阅方(界面)出错不能把已经成功的写入报成失败
        }
    }

    private static string LocalLocation(LocalPath path) => "local:" + path.Workspace.Id;

    private static string RemoteLocation(string sessionId) => "remote:" + sessionId;

    /// <summary>为改动事件读本地文件全文;不存在、超出上限或不是 UTF-8 时返回 null。</summary>
    private string? TryReadLocalTextForEvent(string fullPath, out bool? hasBom)
    {
        hasBom = null;
        try
        {
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > EffectiveLocalMaxFileBytes)
            {
                return null;
            }
            byte[] bytes = File.ReadAllBytes(fullPath);
            hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            return DecodeStrictUtf8(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>为改动事件读远端文件全文;不存在、是目录、超出上限或不是 UTF-8 时返回 null。</summary>
    private async Task<(bool Exists, bool IsDirectory, string? Text, bool? HasBom)> TryReadRemoteTextForEventAsync(
        string sessionId, string path, CancellationToken cancellationToken)
    {
        try
        {
            RemoteFileEntry? entry = await context.RemoteFs.StatAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return (false, false, null, null);
            }
            if (entry.IsDirectory)
            {
                return (true, true, null, null);
            }
            if (entry.Size > MaxFileBytes)
            {
                return (true, false, null, null);
            }
            byte[] bytes = await context.RemoteFs.ReadAllBytesAsync(sessionId, path, MaxFileBytes, cancellationToken)
                .ConfigureAwait(false);
            return (true, false, DecodeStrictUtf8(bytes), bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (true, false, null, null);
        }
    }

    private static string? DecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return LocalUtf8Strict.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // ---- 按行区间读取 ----

    /// <summary>按行区间读取的结果。</summary>
    internal readonly record struct LineWindow(
        string Text, int FirstLine, int LastLine, int? TotalLines, bool Binary, int LinesSeen);

    /// <summary>
    /// 从头顺序读,只把 [offset, offset+limit) 这段带行号放进结果;读完窗口后在预算内继续数总行数。
    /// </summary>
    /// <remarks>
    /// 顺序读而不是整份读进内存:本地大文件、远端只给顺序流(<see cref="IRemoteFsApi.OpenReadAsync" />)都适用。
    /// <paramref name="maxChars" /> 限制返回文本总量;超过时提前收住,由调用方提示从哪一行继续。
    /// </remarks>
    internal static async Task<LineWindow> ReadLineWindowAsync(TextReader reader, int offset, int limit, int maxChars,
        long countBudgetChars, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        int lineNo = 0;
        int first = 0;
        int last = 0;
        int probed = 0;
        bool windowClosed = false;
        long scannedAfter = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lineNo++;
            if (probed < BinaryProbeChars)
            {
                if (line.Contains('\0'))
                {
                    return new LineWindow("", 0, 0, null, Binary: true, lineNo);
                }
                probed += line.Length + 1;
            }
            if (lineNo < offset)
            {
                continue;
            }
            if (!windowClosed && first > 0 && last - first + 1 >= limit)
            {
                windowClosed = true;
            }
            if (!windowClosed)
            {
                string shown = line.Length > MaxReadLineChars
                    ? line[..MaxReadLineChars] + " … [line truncated]"
                    : line;
                if (first > 0 && sb.Length + shown.Length > maxChars)
                {
                    windowClosed = true;
                }
                else
                {
                    if (first == 0)
                    {
                        first = lineNo;
                    }
                    last = lineNo;
                    sb.Append(lineNo).Append('\t').Append(shown).Append('\n');
                    continue;
                }
            }
            scannedAfter += line.Length + 1;
            if (scannedAfter > countBudgetChars)
            {
                // 再往后数不划算了:报「后面还有」,不报总数
                return new LineWindow(sb.ToString(), first, last, null, Binary: false, lineNo);
            }
        }
        return new LineWindow(sb.ToString(), first, last, lineNo, Binary: false, lineNo);
    }

    /// <summary>把 <see cref="LineWindow" /> 排成给模型看的文本,截断时说明总行数与下一段的 offset。</summary>
    internal static string FormatLineWindow(LineWindow window, string toolName, int offset)
    {
        if (window.Binary)
        {
            return "The file appears to be binary (it contains NUL bytes); it cannot be read as text.";
        }
        if (window.LinesSeen == 0)
        {
            return "(file is empty)";
        }
        if (window.FirstLine == 0)
        {
            return $"offset {offset} is past the end of the file ({window.TotalLines?.ToString() ?? "unknown number of"} lines).";
        }
        bool more = window.TotalLines is not { } total || window.LastLine < total;
        if (!more)
        {
            return window.Text;
        }
        string footer = window.TotalLines is { } known
            ? $"[Showing lines {window.FirstLine}-{window.LastLine} of {known}. Call {toolName} with offset={window.LastLine + 1} to read more.]"
            : $"[Showing lines {window.FirstLine}-{window.LastLine}; the file continues (total line count not computed for very large files). "
              + $"Call {toolName} with offset={window.LastLine + 1} to read more.]";
        return window.Text + footer;
    }

    private static (int Offset, int Limit) NormalizeWindow(int offset, int limit)
        => (Math.Max(1, offset), limit <= 0 ? MaxReadLines : Math.Min(limit, MaxReadLines));

    // ---- 多处编辑 ----

    /// <summary>把旧参数(oldText/newText/replaceAll)与 edits 数组合成一份编辑清单。</summary>
    internal static IReadOnlyList<FileEdit>? CollectEdits(string? oldText, string? newText, bool replaceAll,
        FileEdit[]? edits, string writeTool, out string error)
    {
        error = "";
        var list = new List<FileEdit>();
        if (!string.IsNullOrEmpty(oldText))
        {
            list.Add(new FileEdit(oldText, newText ?? "", replaceAll));
        }
        if (edits is not null)
        {
            foreach (FileEdit? edit in edits)
            {
                if (edit is null)
                {
                    error = "edits must not contain null entries.";
                    return null;
                }
                list.Add(edit with { NewText = edit.NewText ?? "" });
            }
        }
        if (list.Count == 0)
        {
            error = $"old_text must not be empty (or pass a non-empty edits array); use {writeTool} to create a file.";
            return null;
        }
        if (list.Count > MaxEditsPerPatch)
        {
            error = $"Too many edits in one call (max {MaxEditsPerPatch}); split them up.";
            return null;
        }
        return list;
    }

    /// <summary>
    /// 依次应用每一处编辑(后一处作用在前一处的结果上);任何一处不成立就整体失败,一个字都不写。
    /// </summary>
    internal static string? ApplyEdits(string original, IReadOnlyList<FileEdit> edits, string displayPath,
        out int replacements, out string error)
    {
        replacements = 0;
        error = "";
        string text = original;
        for (int i = 0; i < edits.Count; i++)
        {
            FileEdit edit = edits[i];
            string label = edits.Count > 1 ? $"edits[{i}].old_text" : "old_text";
            if (string.IsNullOrEmpty(edit.OldText))
            {
                error = $"{label} must not be empty.";
                return null;
            }
            int occurrences = CountOccurrences(text, edit.OldText);
            if (occurrences == 0)
            {
                error = $"{label} was not found in {displayPath}"
                        + (i > 0 ? " (after applying the earlier edits)" : "")
                        + ". Read the file first and copy the snippet exactly (whitespace included, without the line-number prefixes). Nothing was changed.";
                return null;
            }
            if (occurrences > 1 && !edit.ReplaceAll)
            {
                error = $"{label} occurs {occurrences} times in {displayPath}; it must be unique. "
                        + "Include more surrounding lines so it matches exactly one place, or set replaceAll=true to replace every occurrence. Nothing was changed.";
                return null;
            }
            text = text.Replace(edit.OldText, edit.NewText, StringComparison.Ordinal);
            replacements += occurrences;
        }
        return text;
    }

    /// <summary>审批摘要:一处编辑沿用旧格式,多处则逐条列出。</summary>
    private static string DescribeEdits(string displayPath, IReadOnlyList<FileEdit> edits)
    {
        if (edits.Count == 1)
        {
            FileEdit only = edits[0];
            return $"{displayPath}{(only.ReplaceAll ? "  (replace all)" : "")}\n- {Clip(only.OldText)}\n+ {Clip(only.NewText)}";
        }
        StringBuilder sb = new StringBuilder(displayPath).Append($"  ({edits.Count} edits)");
        for (int i = 0; i < edits.Count; i++)
        {
            FileEdit edit = edits[i];
            sb.Append($"\n[{i + 1}]{(edit.ReplaceAll ? " (replace all)" : "")}")
              .Append("\n- ").Append(Clip(edit.OldText, 200))
              .Append("\n+ ").Append(Clip(edit.NewText, 200));
            if (sb.Length > 4000)
            {
                sb.Append($"\n…({edits.Count - i - 1} more edits)");
                break;
            }
        }
        return sb.ToString();
    }

    private static string DescribePatchResult(string displayPath, string original, string patched,
        IReadOnlyList<FileEdit> edits, int replacements)
        => edits.Count == 1 && replacements == 1
            ? $"Patched {displayPath} ({original.Length} → {patched.Length} chars)."
            : $"Patched {displayPath} ({edits.Count} edit(s), {replacements} replacement(s); {original.Length} → {patched.Length} chars).";

    // ---- 本地搜索 ----

    /// <summary>
    /// 把 glob 转成正则:<c>*</c> 不跨目录、<c>**</c> 跨任意层、<c>?</c> 一个字符、<c>{a,b}</c> 二选一。
    /// 反斜杠当作目录分隔符,路径一律按 <c>/</c> 比较。
    /// </summary>
    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        int braces = 0;
        string g = glob.Trim().Replace('\\', '/');
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            switch (c)
            {
                case '*' when i + 1 < g.Length && g[i + 1] == '*':
                    i++;
                    if (i + 1 < g.Length && g[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    braces++;
                    sb.Append("(?:");
                    break;
                case '}' when braces > 0:
                    braces--;
                    sb.Append(')');
                    break;
                case ',' when braces > 0:
                    sb.Append('|');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        for (; braces > 0; braces--)
        {
            sb.Append(')');
        }
        sb.Append('$');
        RegexOptions options = RegexOptions.CultureInvariant
                               | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(sb.ToString(), options, RegexTimeout);
    }

    /// <summary>glob 含 <c>/</c> 时按相对搜索根的路径匹配,否则只按文件名匹配(任意深度)。</summary>
    private static Func<string, bool>? CreateGlobFilter(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            return null;
        }
        Regex regex = GlobToRegex(glob);
        bool byPath = glob.Contains('/') || glob.Contains('\\');
        return relative => regex.IsMatch(byPath ? relative : relative[(relative.LastIndexOf('/') + 1)..]);
    }

    /// <summary>名称代理类重解析点(符号链接、目录联接)一律跳过:顺着它走就可能出了工作区。</summary>
    private static bool IsLink(FileSystemInfo info)
    {
        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }
        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// 深度优先列出搜索根下的文件(同层按名称排序),跳过链接、<see cref="SkippedSearchDirectories" /> 与读不了的目录。
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateSearchFiles(string baseDirectory, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(baseDirectory));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = stack.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = [.. directory.EnumerateFileSystemInfos("*", options).OrderBy(e => e.Name, StringComparer.Ordinal)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }
            var subdirectories = new List<DirectoryInfo>();
            foreach (FileSystemInfo entry in entries)
            {
                if (IsLink(entry))
                {
                    continue;
                }
                if (entry is DirectoryInfo sub)
                {
                    if (!SkippedSearchDirectorySet.Contains(sub.Name))
                    {
                        subdirectories.Add(sub);
                    }
                }
                else if (entry is FileInfo file)
                {
                    yield return file;
                }
            }
            for (int i = subdirectories.Count - 1; i >= 0; i--)
            {
                stack.Push(subdirectories[i]);
            }
        }
    }

    private static string ToSlashPath(string relative) => relative.Replace('\\', '/');

    private static bool LooksBinary(FileStream stream)
    {
        Span<byte> probe = stackalloc byte[BinaryProbeChars];
        int read = stream.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false);
        stream.Seek(0, SeekOrigin.Begin);
        return probe[..read].Contains((byte)0);
    }

    private async Task<string> SearchLocalFilesAsync(
        [Description("Text or regular expression to look for.")] string pattern,
        [Description("Treat pattern as a .NET regular expression instead of a plain substring (default false).")] bool isRegex = false,
        [Description("Directory (or single file) to search, relative to the workspace or absolute inside it. Default: the workspace root.")] string path = ".",
        [Description("Optional glob filter for file names, e.g. \"*.cs\", \"*.{ts,tsx}\" or \"src/**/*.json\" (patterns containing / match the path relative to `path`).")] string? glob = null,
        [Description("Lines of context to show before and after each match (0-10, default 0).")] int contextLines = 0,
        [Description("Maximum number of matching lines to return (default 100, max 1000).")] int maxMatches = 100,
        [Description("Case-insensitive matching (default false).")] bool ignoreCase = false,
        [Description("Workspace id from list_local_workspaces. Omit to use the configured default workspace.")] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return "pattern must not be empty.";
        }
        if (!TryResolveLocal(workspaceId, path, write: false, out LocalPath localPath, out string error))
        {
            return error;
        }
        bool isFile = File.Exists(localPath.FullPath);
        if (!isFile && !Directory.Exists(localPath.FullPath))
        {
            return $"No such path: {path}";
        }
        Func<string, bool> matcher;
        Func<string, bool>? filter;
        try
        {
            if (isRegex)
            {
                var regex = new Regex(pattern,
                    RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout);
                matcher = regex.IsMatch;
            }
            else
            {
                StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                matcher = line => line.Contains(pattern, comparison);
            }
            filter = CreateGlobFilter(glob);
        }
        catch (ArgumentException ex)
        {
            return $"Invalid regular expression or glob: {ex.Message}";
        }
        int contextCount = Math.Clamp(contextLines, 0, 10);
        int limit = Math.Clamp(maxMatches, 1, 1000);
        string root = localPath.Workspace.RootPath;
        try
        {
            return await Task.Run(() => SearchLocalCore(localPath.FullPath, isFile, root, matcher, filter, contextCount, limit,
                pattern, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RegexMatchTimeoutException)
        {
            return "The regular expression took too long to evaluate; simplify it.";
        }
        catch (Exception ex)
        {
            return $"Failed to search local files: {ex.Message}";
        }
    }

    private static string SearchLocalCore(string basePath, bool isFile, string workspaceRoot, Func<string, bool> matcher,
        Func<string, bool>? filter, int context, int maxMatches, string pattern, CancellationToken cancellationToken)
    {
        IEnumerable<FileInfo> files = isFile ? [new FileInfo(basePath)] : EnumerateSearchFiles(basePath, cancellationToken);
        var output = new StringBuilder();
        int matches = 0;
        int scanned = 0;
        int skippedBinary = 0;
        int skippedLarge = 0;
        string? stopReason = null;
        foreach (FileInfo file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isFile && filter is not null && !filter(ToSlashPath(Path.GetRelativePath(basePath, file.FullName))))
            {
                continue;
            }
            if (++scanned > MaxSearchFiles)
            {
                stopReason = $"Stopped after scanning {MaxSearchFiles} files; narrow `path` or `glob`.";
                break;
            }
            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                continue;
            }
            if (length > MaxSearchFileBytes)
            {
                skippedLarge++;
                continue;
            }
            string display = ToSlashPath(Path.GetRelativePath(workspaceRoot, file.FullName));
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                if (LooksBinary(stream))
                {
                    skippedBinary++;
                    continue;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                stopReason = SearchOneFile(reader, display, matcher, context, maxMatches, output, ref matches);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (stopReason is not null)
            {
                break;
            }
        }
        if (matches == 0)
        {
            var none = new StringBuilder($"No matches for '{pattern}' (scanned {Math.Min(scanned, MaxSearchFiles)} files");
            if (skippedBinary > 0) none.Append($", skipped {skippedBinary} binary");
            if (skippedLarge > 0) none.Append($", skipped {skippedLarge} larger than {MaxSearchFileBytes / (1024 * 1024)} MB");
            none.Append(").");
            if (stopReason is not null) none.Append(' ').Append(stopReason);
            return none.ToString();
        }
        if (stopReason is not null)
        {
            output.Append('[').Append(stopReason).Append(']').Append('\n');
        }
        return output.ToString();
    }

    /// <summary>扫一个文件,按 grep 的样子输出:命中行 <c>path:行号: 内容</c>,上下文行 <c>path-行号- 内容</c>,不相邻的段之间用 <c>--</c> 隔开。</summary>
    private static string? SearchOneFile(StreamReader reader, string display, Func<string, bool> matcher, int context,
        int maxMatches, StringBuilder output, ref int matches)
    {
        var before = new Queue<(int No, string Text)>();
        int afterRemaining = 0;
        int lastPrinted = 0;
        int lineNo = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (matcher(line))
            {
                if (matches >= maxMatches)
                {
                    return $"Stopped after {maxMatches} matches (maxMatches); narrow the pattern, path or glob to see the rest.";
                }
                int firstToPrint = before.Count > 0 ? before.Peek().No : lineNo;
                if (context > 0 && output.Length > 0 && (lastPrinted == 0 || firstToPrint > lastPrinted + 1))
                {
                    output.Append("--\n");
                }
                while (before.Count > 0)
                {
                    (int no, string text) = before.Dequeue();
                    AppendSearchLine(output, display, no, '-', text);
                }
                AppendSearchLine(output, display, lineNo, ':', line);
                lastPrinted = lineNo;
                afterRemaining = context;
                matches++;
            }
            else if (afterRemaining > 0)
            {
                AppendSearchLine(output, display, lineNo, '-', line);
                lastPrinted = lineNo;
                afterRemaining--;
            }
            else if (context > 0)
            {
                before.Enqueue((lineNo, line));
                if (before.Count > context)
                {
                    before.Dequeue();
                }
            }
            if (output.Length > MaxSearchOutputChars)
            {
                return $"Output limit reached after {matches} matches; narrow the pattern, path or glob to see the rest.";
            }
        }
        return null;
    }

    private static void AppendSearchLine(StringBuilder output, string display, int lineNo, char separator, string text)
    {
        string clipped = text.Length > MaxSearchLineChars ? text[..MaxSearchLineChars] + "…" : text;
        output.Append(display).Append(separator).Append(lineNo).Append(separator).Append(' ').Append(clipped).Append('\n');
    }

    private async Task<string> FindLocalFilesAsync(
        [Description("Glob pattern, e.g. \"*.csproj\", \"**/appsettings*.json\" or \"src/**/*.{ts,tsx}\". Patterns without / match file names at any depth.")] string pattern,
        [Description("Directory to search, relative to the workspace or absolute inside it. Default: the workspace root.")] string path = ".",
        [Description("Maximum number of paths to return (default 200, max 1000).")] int maxResults = 200,
        [Description("Workspace id from list_local_workspaces. Omit to use the configured default workspace.")] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "pattern must not be empty.";
        }
        if (!TryResolveLocal(workspaceId, path, write: false, out LocalPath localPath, out string error))
        {
            return error;
        }
        if (!Directory.Exists(localPath.FullPath))
        {
            return $"No such directory: {path}";
        }
        Func<string, bool> filter;
        try
        {
            filter = CreateGlobFilter(pattern)!;
        }
        catch (ArgumentException ex)
        {
            return $"Invalid glob: {ex.Message}";
        }
        int limit = Math.Clamp(maxResults, 1, 1000);
        string basePath = localPath.FullPath;
        string root = localPath.Workspace.RootPath;
        try
        {
            return await Task.Run(() =>
            {
                var sb = new StringBuilder();
                int found = 0;
                int scanned = 0;
                foreach (FileInfo file in EnumerateSearchFiles(basePath, cancellationToken))
                {
                    if (++scanned > MaxSearchFiles)
                    {
                        sb.Append($"[Stopped after scanning {MaxSearchFiles} files; narrow `path` or `pattern`.]\n");
                        break;
                    }
                    if (!filter(ToSlashPath(Path.GetRelativePath(basePath, file.FullName))))
                    {
                        continue;
                    }
                    if (found == limit)
                    {
                        sb.Append($"[More files match; only the first {limit} are listed. Narrow `path` or `pattern`.]\n");
                        break;
                    }
                    found++;
                    sb.Append(ToSlashPath(Path.GetRelativePath(root, file.FullName))).Append('\n');
                }
                return found == 0 ? $"No files match '{pattern}'." + (sb.Length > 0 ? " " + sb.ToString().TrimEnd() : "") : sb.ToString();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RegexMatchTimeoutException)
        {
            return "The glob took too long to evaluate; simplify it.";
        }
        catch (Exception ex)
        {
            return $"Failed to find local files: {ex.Message}";
        }
    }

    // ---- 远端搜索 ----

    /// <summary>POSIX shell 单引号转义:整个参数包进单引号,内部的 <c>'</c> 写成 <c>'\''</c>。</summary>
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// 展开一层花括号(<c>*.{cs,axaml}</c> → <c>*.cs</c>、<c>*.axaml</c>),并只保留最后一段 ——
    /// <c>grep --include</c> 只按文件名匹配,也不认花括号。
    /// </summary>
    internal static IReadOnlyList<string> ExpandIncludeGlob(string glob)
    {
        string name = glob.Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.Length == 0 || name == "**")
        {
            return [];
        }
        int open = name.IndexOf('{');
        int close = open >= 0 ? name.IndexOf('}', open + 1) : -1;
        if (open < 0 || close < 0)
        {
            return [name];
        }
        string prefix = name[..open];
        string suffix = name[(close + 1)..];
        return [.. name[(open + 1)..close].Split(',').Select(part => prefix + part + suffix).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// 拼出 <c>search_remote_files</c> 要跑的那条 grep。每个来自模型的值都是一个单引号包住的独立参数,
    /// pattern 走 <c>-e</c>、路径排在 <c>--</c> 之后,所以既拼不出第二条命令,也伪造不出额外的 grep 选项。
    /// </summary>
    internal static string BuildRemoteGrepCommand(string pattern, string path, bool isRegex, string? glob,
        int contextLines, bool ignoreCase, int maxOutputLines)
    {
        var args = new List<string> { "grep", "-rnIsH" };
        if (ignoreCase)
        {
            args.Add("-i");
        }
        args.Add(isRegex ? "-E" : "-F");
        if (contextLines > 0)
        {
            args.Add($"-C{contextLines}");
        }
        foreach (string directory in SkippedSearchDirectories)
        {
            args.Add("--exclude-dir=" + directory);
        }
        if (!string.IsNullOrWhiteSpace(glob))
        {
            foreach (string include in ExpandIncludeGlob(glob))
            {
                args.Add("--include=" + include);
            }
        }
        args.Add("-e");
        args.Add(pattern);
        args.Add("--");
        args.Add(path);
        return string.Join(' ', args.Select(ShellQuote)) + $" | head -n {maxOutputLines}";
    }

    /// <summary>
    /// 远端内容搜索。命令由工具自己拼、只调用 grep,结构上没有副作用,所以与 <c>system_overview</c> 一样不走审批;
    /// 它能读到的东西也不超过已经免审批的 <c>read_remote_file</c>。
    /// </summary>
    private async Task<string> SearchRemoteFilesAsync(
        [Description("Text or regular expression to look for.")] string pattern,
        [Description("Absolute remote directory (or file) to search recursively.")] string path,
        [Description("Treat pattern as a POSIX extended regular expression (grep -E syntax, NOT .NET/PCRE) instead of a fixed string (default false).")] bool isRegex = false,
        [Description("Optional file-name glob filter such as \"*.conf\" or \"*.{yml,yaml}\" (matched against base names only).")] string? glob = null,
        [Description("Lines of context to show before and after each match (0-10, default 0).")] int contextLines = 0,
        [Description("Maximum number of matching lines to return (default 100, max 1000).")] int maxMatches = 100,
        [Description("Case-insensitive matching (default false).")] bool ignoreCase = false,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        if (string.IsNullOrEmpty(pattern))
        {
            return "pattern must not be empty.";
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return "path must not be empty; pass an absolute remote directory such as /etc/nginx.";
        }
        if (pattern.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0 || path.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0
            || (glob is not null && glob.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0))
        {
            return "pattern, path and glob must be single-line text.";
        }
        try
        {
            if (await context.RemoteFs.StatAsync(id, path, cancellationToken).ConfigureAwait(false) is null)
            {
                return $"No such path: {path}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // stat 不通不代表 grep 不行(例如 SFTP 子系统被禁),交给 grep 自己判断
        }
        int contextCount = Math.Clamp(contextLines, 0, 10);
        int limit = Math.Clamp(maxMatches, 1, 1000);
        int maxLines = contextCount == 0 ? limit : limit * (2 * contextCount + 2);
        string command = BuildRemoteGrepCommand(pattern, path, isRegex, glob, contextCount, ignoreCase, maxLines);
        try
        {
            ExecResult result = await context.RemoteExec.RunAsync(id, command,
                new ExecOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
            string stdout = result.Output ?? "";
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return $"No matches for '{pattern}' under {path} (or the path is not readable, or grep is not installed).";
            }
            var sb = new StringBuilder();
            int lines = 0;
            foreach (string raw in stdout.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }
                lines++;
                sb.Append(line.Length > MaxSearchLineChars ? line[..MaxSearchLineChars] + "…" : line).Append('\n');
                if (sb.Length > MaxCommandOutput)
                {
                    sb.Append("[Output limit reached; narrow the pattern, path or glob to see the rest.]\n");
                    return sb.ToString();
                }
            }
            if (lines >= maxLines)
            {
                sb.Append($"[Output stopped at {maxLines} lines (maxMatches={limit}); narrow the pattern, path or glob to see the rest.]\n");
            }
            return sb.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return "The remote search timed out after 30 seconds; narrow `path` or add a `glob` filter.";
        }
        catch (Exception ex)
        {
            return $"Remote search failed: {ex.Message}";
        }
    }
}
