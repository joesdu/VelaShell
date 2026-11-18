using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.Ai.Agent;

public sealed partial class AgentToolbox
{
    /// <summary>在桌面调度器上读取当前工作区授权，并与工作区切换串行执行指定操作。</summary>
    /// <param name="action">接收当前工作区列表，检查授权并执行相应操作的回调。</param>
    /// <returns>回调返回的结果。</returns>
    public delegate Task<bool> LocalWorkspaceGate(Func<IReadOnlyList<LocalWorkspace>, bool> action);

    internal Func<Process, bool> StartLocalProcess { get; init; } = static process => process.Start();
    internal Func<string[]> LocalInterpreters { get; init; } = static () => FindLocalInterpreters(
        Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("SHELL"));

    /// <summary>默认执行死线(秒);到期后终止命令的整个进程树。</summary>
    internal const int DefaultLocalTimeoutSeconds = 15 * 60;

    /// <summary>shell 退出后等后台子进程放开输出管道的宽限;过了就不再读,免得工具一直挂到死线。</summary>
    internal static TimeSpan LocalOutputGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Windows 命令行上限是 32767 个字符,留出可执行文件路径与参数的余量。</summary>
    internal const int MaxWindowsCommandLine = 32_000;

    internal const string HeldPipeNote =
        "\n[VelaShell: the shell exited, but a background process it started kept the output stream open. "
        + "Stopped reading; that process may still be running and is not monitored.]\n";

    internal static string[] FindLocalInterpreters(string? path, string? shell)
    {
        var candidates = new List<string>();
        bool windows = OperatingSystem.IsWindows();
        string[] directories = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (windows)
        {
            foreach (string directory in directories) AddFrom(directory.Trim('"'), "pwsh.exe");
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(folder);
                AddFrom(root, Path.Combine("PowerShell", "7", "pwsh.exe"));
                AddFrom(root, Path.Combine("PowerShell", "7-preview", "pwsh.exe"));
            }
            AddFrom(Environment.GetFolderPath(Environment.SpecialFolder.System), Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"));
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (Path.GetFileName(shell) is "bash" or "zsh") Add(shell!);
            foreach (string name in new[] { "zsh", "bash" })
            {
                foreach (string directory in directories) AddFrom(directory, name);
                foreach (string directory in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" }) AddFrom(directory, name);
            }
        }
        return [.. candidates];

        void AddFrom(string directory, string name)
        {
            if (Path.IsPathFullyQualified(directory)) Add(Path.Combine(directory, name));
        }

        void Add(string candidate)
        {
            // Ignore relative PATH entries and control characters that could obscure the approval text.
            if (!Path.IsPathFullyQualified(candidate) || candidate.Any(char.IsControl)) return;
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if ((windows && fullPath.StartsWith(@"\\", StringComparison.Ordinal)) || !File.Exists(fullPath)) return;
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(fullPath) &
                        (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                {
                    return;
                }

                if (!candidates.Contains(fullPath, windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) candidates.Add(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // An unreadable or malformed entry must not hide other installed shells.
            }
        }
    }

    // Only constructed by an explicitly interactive local desktop turn. Neither the model nor
    // the current terminal selection supplies an executable, endpoint or working directory.
    private sealed class LocalCommandBinding(AgentToolbox owner, LocalPath workspace, LocalWorkspaceGate gate,
        CancellationToken shutdown)
    {
        public async Task<string> RunAsync(
            [Description("The command to run in the local shell (non-interactive, one-shot).")] string command,
            [Description("Seconds to wait before returning a running taskId (default 10, max 60). Expiry does NOT stop the command: "
                + "it keeps running in the background, task_status/task_wait/task_cancel apply, and you are notified when it finishes.")] int waitSeconds = 10,
            [Description("Execution deadline in seconds (default 900, max 86400). When it expires VelaShell terminates the command's "
                + "process tree; output received so far is kept.")] int timeoutSeconds = DefaultLocalTimeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            if (command?.Contains('\0') == true) return "Command contains a NUL character. No command was started.";
            if (string.IsNullOrWhiteSpace(command)) return "Command is empty. No command was started.";
            string workingDir = workspace.FullPath;
            string script = BuildScript(command);
            string[] options = OperatingSystem.IsWindows()
                ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
                : ["-c"];
            var deadline = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds <= 0 ? DefaultLocalTimeoutSeconds : timeoutSeconds, 1, 86400));
            CommandJobRunScope run = owner.CurrentRun;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token, shutdown);
            CancellationToken token = stop.Token;
            token.ThrowIfCancellationRequested();
            if (!await gate(Allowed).ConfigureAwait(false)) return "Local workspace permission is no longer available.";
            IReadOnlyDictionary<string, string>? environment = await LocalShellEnvironment.GetAsync(token).ConfigureAwait(false);
            string? lastFailure = null;
            foreach (string executable in owner.LocalInterpreters())
            {
                token.ThrowIfCancellationRequested();
                if (OperatingSystem.IsWindows() && CommandLineLength(executable, options, script) is var length and > MaxWindowsCommandLine)
                {
                    // 在审批之前就拦下:超长命令行哪个解释器都起不来,逐个审批再逐个失败只会让人白点
                    return $"The command is too long for the Windows command line ({length} characters; the limit is about {MaxWindowsCommandLine}). "
                        + "Nothing was started. Write the script to a .ps1 file in the workspace with write_local_file and run that file instead.";
                }
                var request = new ApprovalRequest("run_local_command", BuildApprovalText(executable, options, script, workingDir));
                if (!await owner.ApproveAsync(request, token).ConfigureAwait(false))
                {
                    return "The user DENIED this local command. Nothing was started. Do not retry it; ask the user how to proceed.";
                }
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // VelaShell 崩溃或被强退时连同命令一起结束(Windows 用作业对象,连孙进程也带走;Linux 用 PDEATHSIG)。
                // macOS 没有对应机制,那里仍可能留下孤儿进程。
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    start.KillOnParentExit = true;
                }
                if (environment is not null)
                {
                    foreach ((string key, string value) in environment) start.Environment[key] = value;
                }
                foreach (string option in options) start.ArgumentList.Add(option);
                start.ArgumentList.Add(script);
                var process = new Process { StartInfo = start };
                bool started = false;
                bool retry = false;
                string? fatal = null;
                // Selection changes and the approved launch are serialized on the desktop dispatcher.
                if (!await gate(active =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (run.HasDeniedExecution || !Allowed(active)) return false;
                        try
                        {
                            started = owner.StartLocalProcess(process);
                            if (!started) lastFailure = $"{executable}: no process was created.";
                            retry = !started;
                        }
                        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                        {
                            // 只有「找不到这个解释器」才换下一个;权限不足、命令行过长之类换了也一样失败
                            lastFailure = $"{executable}: {ex.Message}";
                            if (IsNotFound(ex)) retry = true;
                            else fatal = lastFailure;
                        }
                        return true;
                    }).ConfigureAwait(false))
                {
                    process.Dispose();
                    return "Local command no longer authorized. Nothing was started.";
                }

                if (!started)
                {
                    process.Dispose();
                    if (fatal is not null) return $"Could not start the local command interpreter: {fatal}. Nothing was started.";
                    if (retry) continue;
                    break;
                }
                return await AdoptAsync(run, process, command, deadline, waitSeconds, token).ConfigureAwait(false);
            }
            return lastFailure is null ? "No supported local command interpreter is available (pwsh or Windows PowerShell on Windows, zsh or bash on Linux/macOS)."
                : $"Could not start a local command interpreter. Last attempt: {lastFailure}";

            bool Allowed(IReadOnlyList<LocalWorkspace> active) => new LocalPathScope(active)
                .TryResolve(workspace.Workspace.Id, workspace.FullPath, true, out LocalPath current, out _)
                && current.Workspace.AllowRead && current.Workspace.AllowWrite && IsLocalWorkspaceRoot(current);
        }

        /// <summary>
        /// 把已启动的进程交给命令任务注册表:与远端命令同一套任务 id、task_status / task_wait / task_cancel 与完成通知。
        /// 短等之内结束就直接返回结果;否则转入后台,命令继续跑。
        /// </summary>
        private async Task<string> AdoptAsync(CommandJobRunScope run, Process process, string command, TimeSpan deadline,
            int waitSeconds, CancellationToken token)
        {
            string taskId;
            try
            {
                taskId = owner._jobs.StartLocal(run, command, deadline,
                    (output, cancel) => DriveAsync(process, deadline, output, cancel),
                    // 读取结果时重新确认工作区仍被允许;撤销后输出不再交出
                    readToken => gate(active => new LocalPathScope(active)
                        .TryResolve(workspace.Workspace.Id, workspace.FullPath, true, out LocalPath current, out _)
                        && current.Workspace.AllowRead && IsLocalWorkspaceRoot(current)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                // 登记失败(本轮任务额度用尽、轮次已收尾):进程已经起来了,不能放任它没人管
                KillTree(process);
                process.Dispose();
                return $"The local command could not be tracked and was terminated: {ex.Message}. Do not automatically replay it.";
            }
            try
            {
                CommandJobSnapshot snapshot = await owner._jobs.WaitAsync(run, taskId,
                    TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 60)), cancellationToken: token).ConfigureAwait(false);
                if (!snapshot.IsTerminal)
                {
                    owner._jobs.MarkBackground(run, taskId);
                }
                return FormatJob(snapshot);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await owner._jobs.CancelAsync(run, taskId).ConfigureAwait(false);
                throw;
            }
        }

        private static string BuildApprovalText(string executable, string[] options, string script, string workingDir)
        {
            (string visible, int hidden) = RevealInvisible(script);
            StringBuilder text = new StringBuilder()
                .Append("Local computer: ").AppendLine(Environment.MachineName)
                .Append("Working directory: ").AppendLine(workingDir)
                .AppendLine("Runs with your account's permissions. Output will be sent to the model.");
            if (hidden > 0)
            {
                text.Append("WARNING: the command contains ").Append(hidden)
                    .AppendLine(" invisible or direction-changing character(s), shown below as ⟦U+XXXX⟧. Check that it does what it appears to do.");
            }
            return text.Append(executable).Append(' ').AppendLine(string.Join(' ', options)).Append(visible).ToString();
        }
    }

    /// <summary>
    /// 驱动进程直到结束,输出逐块交给任务缓冲(界面实时可见,超时 / 取消时已收到的部分保留)。
    /// 取消与超时时终止整个进程树,并以确定的结局报告(进程确已终止,不是「结果未知」)。
    /// </summary>
    internal static async Task<ExecStreamResult> DriveAsync(Process process, TimeSpan deadline, IProgress<ExecOutput> output,
        CancellationToken cancel)
    {
        using var expiry = new CancellationTokenSource(deadline);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel, expiry.Token);
        using var reads = new CancellationTokenSource();
        Task pumps = Task.CompletedTask;
        try
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { /* 进程已经退出,stdin 管道已断 */ }
            pumps = Task.WhenAll(
                PumpAsync(process.StandardOutput, ExecStream.StandardOutput, output, reads.Token),
                PumpAsync(process.StandardError, ExecStream.StandardError, output, reads.Token));
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                // shell 已退出;它起的后台子进程若继承了输出管道,EOF 要等那个进程结束才来
                await pumps.WaitAsync(LocalOutputGrace, stop.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                reads.Cancel();
                output.Report(new ExecOutput(ExecStream.StandardError, HeldPipeNote));
            }
            return new ExecStreamResult(process.ExitCode, 0);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            bool stopped = KillTree(process);
            string tree = stopped ? "VelaShell terminated its process tree" : "VelaShell could not confirm that its processes stopped";
            if (expiry.IsCancellationRequested && !cancel.IsCancellationRequested)
            {
                throw new CommandJobRegistry.JobOutcomeException(CommandJobState.TimedOut,
                    $"Local command exceeded its {deadline.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s deadline; {tree}. Output received before that is kept.",
                    stopped);
            }
            throw new CommandJobRegistry.JobOutcomeException(CommandJobState.Cancelled,
                $"Local command was stopped; {tree}. Output received before that is kept.", stopped);
        }
        finally
        {
            reads.Cancel();
            await pumps.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            process.Dispose();
        }
    }

    private static async Task PumpAsync(StreamReader reader, ExecStream stream, IProgress<ExecOutput> output, CancellationToken token)
    {
        // 读原始字节自己解码:不少老程序不理会控制台代码页,直接按系统本地代码页(中文 Windows 上是 GBK)输出
        var decoder = new LocalOutputDecoder(LocalOutputDecoder.SystemLegacyEncoding);
        byte[] buffer = new byte[4096];
        try
        {
            while (await reader.BaseStream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) is int count && count > 0)
            {
                Emit(decoder.Decode(buffer.AsSpan(0, count), final: false));
            }
            Emit(decoder.Decode([], final: true));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 读取被取消(宽限到期 / 收尾)或管道已断:到此为止,已收到的输出都已交出
        }

        void Emit(string text)
        {
            if (text.Length > 0) output.Report(new ExecOutput(stream, text));
        }
    }

    /// <summary>
    /// 本机命令输出的解码:默认 UTF-8(包装脚本已把控制台切到 UTF-8,遵守它的程序都输出 UTF-8);
    /// 一旦遇到不合法的 UTF-8,说明这个程序按系统本地代码页输出,此后整条流改用那个代码页解码,不再出乱码。
    /// 跨块截断的多字节字符留到下一块拼上再解,不会被拆成两个替换符。
    /// </summary>
    internal sealed class LocalOutputDecoder(Encoding? legacy)
    {
        private byte[] _pending = [];
        private Decoder? _legacy;

        /// <summary>
        /// Windows 上系统的 ANSI 代码页(中文是 936);系统本身已是 UTF-8 或非 Windows 时为空,
        /// 那时不合法的字节照常显示为替换符。
        /// </summary>
        internal static Encoding? SystemLegacyEncoding { get; } = ResolveLegacy();

        /// <summary>是否已切换到本地代码页(测试用)。</summary>
        internal bool UsingLegacy => _legacy is not null;

        public string Decode(ReadOnlySpan<byte> chunk, bool final)
        {
            if (_legacy is not null)
            {
                return LegacyDecode(chunk, final);
            }
            byte[] data = _pending.Length == 0 ? chunk.ToArray() : [.. _pending, .. chunk];
            _pending = [];
            char[] chars = new char[data.Length + 1];
            OperationStatus status = Utf8.ToUtf16(data, chars, out int read, out int written,
                replaceInvalidSequences: legacy is null, isFinalBlock: final);
            string text = new(chars, 0, written);
            switch (status)
            {
                case OperationStatus.NeedMoreData:
                    _pending = data[read..];
                    return text;
                case OperationStatus.InvalidData when legacy is not null:
                    _legacy = legacy.GetDecoder();
                    return text + LegacyDecode(data.AsSpan(read), final);
                default:
                    return text;
            }
        }

        private string LegacyDecode(ReadOnlySpan<byte> bytes, bool final)
        {
            char[] chars = new char[_legacy!.GetCharCount(bytes, flush: false) + 4];
            int count = _legacy.GetChars(bytes, chars, flush: final);
            return new string(chars, 0, count);
        }

        private static Encoding? ResolveLegacy()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
            try
            {
                // 代码页 0 = 系统当前的 ANSI 代码页(.NET 自带的代码页编码提供程序在 Windows 上按 GetACP 解析)
                Encoding? encoding = CodePagesEncodingProvider.Instance.GetEncoding(0);
                return encoding is null || encoding.CodePage == Encoding.UTF8.CodePage ? null : encoding;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 审批卡显示用:把格式字符(双向覆盖、零宽字符等)和换行 / 制表以外的控制字符显成 ⟦U+XXXX⟧,
    /// 让用户看到的与实际执行的一致。只影响显示,执行的仍是原文。
    /// </summary>
    internal static (string Text, int Hidden) RevealInvisible(string text)
    {
        var builder = new StringBuilder(text.Length);
        int hidden = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool invisible = category is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                || (category == UnicodeCategory.Control && rune.Value is not ('\n' or '\r' or '\t'))
                || rune.Value is 0x2028 or 0x2029;
            if (invisible)
            {
                hidden++;
                builder.Append("⟦U+").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)).Append('⟧');
            }
            else
            {
                builder.Append(rune);
            }
        }
        return (builder.ToString(), hidden);
    }

    /// <summary>
    /// 包一层再交给解释器。Windows:前导设置与命令放在<b>同一行</b>(报错行号与原命令一致);
    /// 退出码以原生命令的 <c>$LASTEXITCODE</c> 为准 —— Windows PowerShell 5.1 会把重定向进来的 stderr
    /// 当成错误记录把 <c>$?</c> 置假,只因这个就报失败是误报;只有出现了别的错误才算失败。
    /// </summary>
    internal static string BuildScript(string command) => OperatingSystem.IsWindows()
        ? "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $global:LASTEXITCODE = 0; $Error.Clear(); "
            + command
            + "\n$__velaOk = $?"
            + "\nif ($global:LASTEXITCODE) { exit $global:LASTEXITCODE }"
            + "\nif ($__velaOk) { exit 0 }"
            + "\nif (@($Error | Where-Object { \"$($_.FullyQualifiedErrorId)\" -notlike 'NativeCommandError*' }).Count -eq 0) { exit 0 }"
            + "\nexit 1"
        // bash / zsh:以 `-` 开头的命令串会被当成 shell 自己的选项(`bash -c -V` 打印版本而不是执行)。
        // 前面垫一个空格就不再是选项,shell 执行时照常忽略这个空白;不依赖 `--`(zsh 上没实测过)
        : command.StartsWith('-') ? " " + command : command;

    /// <summary>按 Windows 的参数拼接规则估算命令行长度(含引号与转义的余量)。</summary>
    internal static int CommandLineLength(string executable, IEnumerable<string> options, string script)
    {
        int length = executable.Length + 3;
        foreach (string argument in options.Append(script))
        {
            length += argument.Length + argument.Count(c => c is '"' or '\\') + 3;
        }
        return length;
    }

    private static bool IsNotFound(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => true,
        // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND(Windows);Unix 上 ENOENT 同样是 2
        Win32Exception win32 => win32.NativeErrorCode is 2 or 3,
        _ => false
    };

    /// <summary>终止进程及其子孙;已退出视为成功。任何失败都吞下并如实返回,不盖掉调用方原本的结局。</summary>
    internal static bool KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException or InvalidOperationException or NotSupportedException)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// 本机命令的环境补充。macOS 上从访达 / 程序坞启动的应用拿不到登录 shell 的 PATH(Homebrew、dotnet 等都不在里面),
    /// 这里用登录 shell 解析一次并缓存;其它平台沿用桌面进程的环境。
    /// </summary>
    private static class LocalShellEnvironment
    {
        private const string Begin = "__VELA_PATH_BEGIN__", End = "__VELA_PATH_END__";
        private static readonly Lock Gate = new();
        private static Task<IReadOnlyDictionary<string, string>?>? _cached;

        public static Task<IReadOnlyDictionary<string, string>?> GetAsync(CancellationToken token)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
            }
            lock (Gate)
            {
                return (_cached ??= ResolveAsync()).WaitAsync(token);
            }
        }

        private static async Task<IReadOnlyDictionary<string, string>?> ResolveAsync()
        {
            string? shell = FindLocalInterpreters(Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("SHELL"))
                .FirstOrDefault();
            if (shell is null)
            {
                return null;
            }
            try
            {
                var start = new ProcessStartInfo(shell)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // 登录 shell 的问候语等会混进标准输出:用前后标记把 PATH 摘出来
                foreach (string argument in new[] { "-l", "-c", $"printf '%s%s%s' '{Begin}' \"$PATH\" '{End}'" })
                {
                    start.ArgumentList.Add(argument);
                }
                using var process = Process.Start(start);
                if (process is null)
                {
                    return null;
                }
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Task<string> read = process.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> discard = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillTree(process);
                    return null;
                }
                string text = await read.ConfigureAwait(false);
                await ((Task)discard).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                int begin = text.LastIndexOf(Begin, StringComparison.Ordinal);
                int end = text.LastIndexOf(End, StringComparison.Ordinal);
                if (begin < 0 || end <= begin)
                {
                    return null;
                }
                string path = text[(begin + Begin.Length)..end];
                return path.Length == 0 || path.Any(char.IsControl) ? null : new Dictionary<string, string> { ["PATH"] = path };
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
            {
                return null;
            }
        }
    }
}
