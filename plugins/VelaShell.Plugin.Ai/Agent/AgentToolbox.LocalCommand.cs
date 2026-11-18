using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using VelaShell.Plugin.Ai.Configuration;

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
        public async Task<string> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            if (command?.Contains('\0') == true) return "Command contains a NUL character. No command was started.";
            if (string.IsNullOrWhiteSpace(command)) return "Command is empty. No command was started.";
            string workingDir = workspace.FullPath;
            string script = OperatingSystem.IsWindows()
                ? "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n" + command
                    + "\nif ($?) { exit 0 }; if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1"
                : command;
            string[] options = OperatingSystem.IsWindows()
                ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"] : ["-c"];
            CommandJobRunScope run = owner.CurrentRun;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token, shutdown);
            CancellationToken token = stop.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!await gate(Allowed).ConfigureAwait(false)) return "Local workspace permission is no longer available.";
                string? lastFailure = null;
                foreach (string executable in owner.LocalInterpreters())
                {
                    token.ThrowIfCancellationRequested();
                    var request = new ApprovalRequest("run_local_command",
                        $"Local computer: {Environment.MachineName}\nWorking directory: {workingDir}\nRuns with your account's permissions. Output will be sent to the model.\n{executable} {string.Join(' ', options)}\n{script}");
                    if (!await owner.ApproveAsync(request, token).ConfigureAwait(false))
                    {
                        return "Local command denied. Nothing was started.";
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
                    foreach (string option in options) start.ArgumentList.Add(option);
                    start.ArgumentList.Add(script);
                    using var process = new Process { StartInfo = start };
                    bool started = false;
                    // Selection changes and the approved launch are serialized on the desktop dispatcher.
                    if (!await gate(active =>
                        {
                            token.ThrowIfCancellationRequested();
                            if (run.HasDeniedExecution || !Allowed(active)) return false;
                            try
                            {
                                started = owner.StartLocalProcess(process);
                                if (!started) lastFailure = $"{executable}: no process was created.";
                            }
                            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                            {
                                lastFailure = $"{executable}: {ex.Message}";
                            }
                            return true;
                        }).ConfigureAwait(false))
                    {
                        return "Local command no longer authorized. Nothing was started.";
                    }

                    if (!started) continue;
                    stop.CancelAfter(TimeSpan.FromMinutes(15));
                    return await CollectResultAsync(process, token).ConfigureAwait(false);
                }
                return lastFailure is null ? "No supported local command interpreter is available (pwsh or Windows PowerShell on Windows, zsh or bash on Linux/macOS)."
                    : $"Could not start a local command interpreter. Last attempt: {lastFailure}";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !run.Token.IsCancellationRequested && !shutdown.IsCancellationRequested)
            {
                return "Local command timed out after 15 minutes.";
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                return $"Local command failed: {ex.Message}";
            }

            bool Allowed(IReadOnlyList<LocalWorkspace> active) => new LocalPathScope(active)
                .TryResolve(workspace.Workspace.Id, workspace.FullPath, true, out LocalPath current, out _)
                && current.Workspace.AllowRead && current.Workspace.AllowWrite && IsLocalWorkspaceRoot(current);
        }

        private static async Task<string> CollectResultAsync(Process process, CancellationToken token)
        {
            Task collection = Task.CompletedTask;
            try
            {
                process.StandardInput.Close();
                Task<string> output = DrainAsync(process.StandardOutput, token);
                Task<string> error = DrainAsync(process.StandardError, token);
                collection = Task.WhenAll(output, error, process.WaitForExitAsync(token));
                await collection.WaitAsync(token).ConfigureAwait(false);
                return $"Local command exit code: {process.ExitCode}\nstdout:\n{await output.ConfigureAwait(false)}\nstderr:\n{await error.ConfigureAwait(false)}";
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                }
                finally
                {
                    await collection.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }

        private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
        {
            char[] chunk = new char[2048];
            var text = new StringBuilder();
            bool truncated = false;
            while (await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false) is int count && count > 0)
            {
                int keep = Math.Min(count, MaxCommandOutput / 2 - text.Length);
                text.Append(chunk, 0, keep);
                truncated |= keep != count;
            }
            return truncated ? text + "\n[truncated]" : text.ToString();
        }
    }
}
