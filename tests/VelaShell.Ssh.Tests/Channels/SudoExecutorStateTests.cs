using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Channels;

/// <summary>通过真实内存 SSH 通道执行宿主认证状态机；不模拟 sudo/PAM 的真实终端实现。</summary>
[TestClass]
public sealed class SudoExecutorStateTests
{
    private sealed class Prompt : ISudoPrompt
    {
        internal int _requests;
        internal Func<CancellationToken, Task<SudoPassword?>> _answer = _ => Task.FromResult<SudoPassword?>(new("密碼é🙂".AsSpan()));
        internal TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfirmAsync(SudoExecutionContext context, string command, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<SudoPassword?> RequestPasswordAsync(SudoExecutionContext context, string command, int attempt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            _asked.TrySetResult();
            return _answer(cancellationToken);
        }
    }

    private static string Marker(string command, string kind)
    {
        const string prefix = "\u001eVELA_AUTH_";
        string script = Script(command);
        int start = script.IndexOf(prefix, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        start += prefix.Length;
        int end = script.IndexOf('\u001f', start);
        Assert.IsGreaterThan(start, end);
        return "\u001eVELA_" + kind + "_" + script[start..end] + "\u001f";
    }

    /// <summary>还原交给登录 shell 的那层 <c>printf %b</c> 编码,得到 /bin/sh 真正执行的脚本。</summary>
    private static string Script(string command)
    {
        const string prefix = "exec /bin/sh -c 'eval \"$(printf %b \"";
        const string suffix = "\")\"'";
        Assert.StartsWith(prefix, command);
        Assert.EndsWith(suffix, command);
        string payload = command[prefix.Length..^suffix.Length];
        List<byte> bytes = [];
        for (int i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\')
            {
                bytes.Add(Convert.ToByte(payload.Substring(i + 2, 3), 8));
                i += 4;
            }
            else
            {
                bytes.Add((byte)payload[i]);
            }
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static Func<SudoExecutionEvent, ValueTask> Collect(List<SudoExecutionEvent> events) => entry =>
    {
        lock (events) { events.Add(entry); }
        return ValueTask.CompletedTask;
    };

    private static async Task SendFragmentedAsync(PipeWriter output, string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (byte value in bytes)
        {
            await output.WriteAsync(new byte[] { value }, cancellationToken);
        }
    }

    private static async Task<string> ReadPasswordLineAsync(PipeReader input, CancellationToken cancellationToken)
    {
        StringBuilder result = new();
        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken);
            byte[] bytes = read.Buffer.ToArray();
            input.AdvanceTo(read.Buffer.End);
            result.Append(Encoding.UTF8.GetString(bytes));
            if (result.ToString().Contains('\n', StringComparison.Ordinal))
            {
                return result.ToString();
            }
            Assert.IsFalse(read.IsCompleted, "收到密码前输入不能结束");
        }
    }

    [TestMethod]
    public async Task FragmentedAuthenticationWritesExactlyOneUtf8PasswordAndThenDisablesAuthentication()
    {
        Prompt prompt = new();
        string? sentPassword = null;
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExecHandler = async (command, input, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "AUTH"), ct);
                sentPassword = await ReadPasswordLineAsync(input, ct);
                await SendFragmentedAsync(output, Marker(command, "START") + "漢🙂" + Marker(command, "AUTH") + "tail", ct);
            }
        });
        List<SudoExecutionEvent> events = [];
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"),
            prompt, Collect(events), host.Token);
        Assert.AreEqual("密碼é🙂\n", sentPassword);
        Assert.AreEqual(1, prompt._requests);
        Assert.Contains(entry => entry.State == SudoExecutionState.Running, events);
        string text = string.Concat(events.Select(entry => entry.Text));
        Assert.Contains("漢🙂", text);
        Assert.Contains("tail", text);
        Assert.DoesNotContain("VELA_READY_", text);
        Assert.DoesNotContain("密碼", text);
        Assert.AreEqual(SudoExecutionOutcome.Unknown, result.Outcome, "剧本没有伪造真实进程退出码");
    }

    [TestMethod]
    public async Task EofRevokesPendingPasswordUiAndNeverWritesItsLateAnswer()
    {
        Prompt prompt = new();
        prompt._answer = async ct =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { prompt._cancelled.TrySetResult(); throw; }
            return null;
        };
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExecHandler = async (command, _, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "AUTH"), ct);
                await prompt._asked.Task.WaitAsync(ct);
                // 结束处理器使现有输出泵发送 EOF/CLOSE；不阻塞客户端读取泵。
            }
        });
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"),
            prompt, _ => ValueTask.CompletedTask, host.Token);
        await prompt._cancelled.Task.WaitAsync(host.Token);
        Assert.AreEqual(1, prompt._requests);
        Assert.IsEmpty(host.Channels.Observation.StandardInput);
        Assert.AreNotEqual(SudoExecutionOutcome.Exited, result.Outcome);
    }

    [TestMethod]
    public async Task PolicyDenialEchoingCommandNeverBecomesAStartedCommand()
    {
        Prompt prompt = new()
        {
            _answer = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }
        };
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExitCode = 1,
            ExecHandler = async (command, _, output, ct) =>
            {
                // sudo 回显的是 /bin/sh 还原后的脚本里的 root argv,而不是登录 shell 收到的编码形式。
                string script = Script(command);
                Assert.DoesNotContain(Marker(command, "START"), script,
                    "sudo argv 的诊断回显不能含有可被识别为已启动的原始标记");
                await SendFragmentedAsync(output, Marker(command, "READY") + "sudo: not allowed: " + script, ct);
            }
        });
        List<SudoExecutionEvent> events = [];
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"),
            prompt, Collect(events), host.Token);
        Assert.AreEqual(SudoExecutionOutcome.AuthenticationFailed, result.Outcome);
        Assert.DoesNotContain(entry => entry.State == SudoExecutionState.Running, events);
        Assert.IsTrue(events.All(entry => entry.Text is null));
        Assert.IsEmpty(host.Channels.Observation.StandardInput);
    }

    [TestMethod]
    public async Task PasswordlessStartNeverRequestsPasswordEvenWhenOutputImitatesPrompt()
    {
        Prompt prompt = new();
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExecHandler = async (command, _, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "START") + Marker(command, "AUTH") + "password:", ct);
            }
        });
        List<SudoExecutionEvent> events = [];
        await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"), prompt, Collect(events), host.Token);
        Assert.AreEqual(0, prompt._requests);
        Assert.Contains(entry => entry.State == SudoExecutionState.Running, events);
        Assert.IsEmpty(host.Channels.Observation.StandardInput);
    }

    /// <summary>
    /// root 命令吐出 GBK 日志、二进制或非 UTF-8 文件名是常事:按 U+FFFD 替换照常转发,不能把在跑的命令判成 Unknown。
    /// </summary>
    [TestMethod]
    public async Task NonUtf8OutputAfterStartIsReplacedAndTheCommandRunsToItsExitCode()
    {
        Prompt prompt = new();
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExitCode = 3,
            ExecHandler = async (command, _, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "START") + "before:", ct);
                // GBK 的「中文」、孤立的续字节、截断的四字节序列、0xFF。
                await output.WriteAsync(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4, 0x80, 0xF0, 0x9F, 0x0A, 0xFF }, ct);
                await SendFragmentedAsync(output, ":after", ct);
            }
        });
        List<SudoExecutionEvent> events = [];
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(host.Connection, "cat /var/log/legacy.log",
            new("plugin", "joe@test"), prompt, Collect(events), host.Token);
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Outcome);
        Assert.AreEqual(3, result.ExitCode);
        string text = string.Concat(events.Select(entry => entry.Text));
        Assert.Contains("before:", text);
        Assert.Contains("�", text);
        Assert.Contains(":after", text);
    }

    /// <summary>
    /// 认出启动标记后先报 Running,再撤销密码框、关 stdin。死线若落在这些等待之间,
    /// 调用方已经知道命令可能在跑(Unknown),不会把它当成没启动(TimedOut)再重试一遍。
    /// </summary>
    /// <remarks>
    /// 借挂起的密码框当探针:撤销它的回调就跑在 START 之后的第一个等待里,在那一刻让死线到期。
    /// </remarks>
    [TestMethod]
    public async Task RunningIsReportedBeforeAnyWaitAfterTheStartMarker()
    {
        List<SudoExecutionEvent> events = [];
        bool runningBeforeRevocation = false;
        Prompt prompt = new();
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExecHandler = async (command, _, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "AUTH"), ct);
                await prompt._asked.Task.WaitAsync(ct);
                await SendFragmentedAsync(output, Marker(command, "START") + "started", ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(host.Token);
        prompt._answer = async ct =>
        {
            TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using CancellationTokenRegistration registration = ct.Register(() =>
            {
                lock (events) { runningBeforeRevocation = events.Any(entry => entry.State == SudoExecutionState.Running); }
                deadline.Cancel();
                revoked.TrySetResult();
            });
            // 等探针自己完成；同一令牌的 Delay 可能先恢复并释放尚未执行的探针注册。
            await revoked.Task;
            return null;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => SudoCommandExecutor.ExecuteAsync(host.Connection, "systemctl restart nginx",
            new("plugin", "joe@test"), prompt, Collect(events), deadline.Token));
        Assert.IsTrue(runningBeforeRevocation, "Running 必须先于 START 之后的任何等待发出");
        Assert.Contains(entry => entry.State == SudoExecutionState.Running, events);
    }

    /// <summary>输出回调被逐条等待:接收方没收下这一段,执行就不往下走(隔离插件的背压靠它)。</summary>
    [TestMethod]
    public async Task OutputCallbackIsAwaitedBeforeTheExecutionCompletes()
    {
        Prompt prompt = new();
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExitCode = 0,
            ExecHandler = async (command, _, output, ct) =>
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "START") + "x", ct)
        });
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SudoExecutionResult> running = SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"), prompt,
            entry =>
            {
                if (entry.Text is null)
                {
                    return ValueTask.CompletedTask;
                }
                offered.TrySetResult();
                return new(accepted.Task);
            }, host.Token);
        await offered.Task.WaitAsync(host.Token);
        await Task.Delay(200, host.Token);
        Assert.IsFalse(running.IsCompleted, "接收方还没收下输出,执行不能先结束");
        accepted.SetResult();
        SudoExecutionResult result = await running.WaitAsync(host.Token);
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Outcome);
    }
}
