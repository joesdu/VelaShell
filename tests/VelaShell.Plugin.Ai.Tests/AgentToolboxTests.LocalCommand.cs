using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 本机命令(run_local_command):接入命令任务机制后的后台运行、超时保留输出、进程树清理、
/// 孙进程占住管道、Windows 命令行长度、PowerShell 5.1 退出码与审批显示。
/// </summary>
public sealed partial class AgentToolboxTests
{
    private static string Shell(string windows, string unix) => OperatingSystem.IsWindows() ? windows : unix;

    private static async Task<(AgentToolbox Toolbox, CommandJobRunScope Run, AIFunction Cli, TestPluginContext Context)> LocalCliAsync(
        Func<ApprovalRequest, Task<bool>>? approve = null, Func<string[]>? interpreters = null, bool allowWrite = true)
    {
        var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context)
        {
            LocalWorkspaces = [new LocalWorkspace { Id = "work", RootPath = Environment.CurrentDirectory, AllowWrite = allowWrite }],
            LocalDefaultWorkspaceId = "work",
            Approval = approve is null ? ApprovalMode.Bypass : ApprovalMode.Ask,
            ApprovalHandler = approve ?? (_ => Task.FromResult(true)),
            LocalInterpreters = interpreters ?? (() => AgentToolbox.FindLocalInterpreters(
                Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("SHELL")))
        };
        CommandJobRunScope run = toolbox.BeginRun("desktop", Guid.NewGuid().ToString("N"));
        AIFunction? cli = toolbox.CreateTools(ChatMode.Agent, runScope: run, interactiveLocal: true,
                workspaceGate: action => Task.FromResult(action(toolbox.LocalWorkspaces)))
            .OfType<AIFunction>().SingleOrDefault(t => t.Name == "run_local_command");
        await Task.CompletedTask;
        return (toolbox, run, cli!, context);
    }

    private static async Task<string> RunLocalAsync(AIFunction cli, string command, int? waitSeconds = null, int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["command"] = command };
        if (waitSeconds is { } wait) args["waitSeconds"] = wait;
        if (timeoutSeconds is { } timeout) args["timeoutSeconds"] = timeout;
        return (await cli.InvokeAsync([with(args)], cancellationToken))?.ToString() ?? "";
    }

    private static (string State, int? Exit, string Stdout, string Stderr, string? Error) ReadJob(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement[] output = [.. root.GetProperty("output").EnumerateArray()];
        string Join(string stream) => string.Concat(output.Where(o => o.GetProperty("Stream").GetString() == stream)
            .Select(o => o.GetProperty("Text").GetString()));
        return (root.GetProperty("state").GetString()!,
            root.GetProperty("exitCode").ValueKind == JsonValueKind.Number ? root.GetProperty("exitCode").GetInt32() : null,
            Join("stdout"), Join("stderr"),
            root.GetProperty("error").ValueKind == JsonValueKind.String ? root.GetProperty("error").GetString() : null);
    }

    [TestMethod]
    public async Task LocalCommand_StillRunningAfterTheShortWait_ContinuesInTheBackgroundAndNotifies()
    {
        (AgentToolbox toolbox, CommandJobRunScope run, AIFunction cli, TestPluginContext context) = await LocalCliAsync();
        await using (toolbox)
        await using (run)
        using (context)
        {
            string first = await RunLocalAsync(cli, Shell("Start-Sleep -Milliseconds 1500; Write-Output done", "sleep 1.5; echo done"), waitSeconds: 0);
            Assert.AreEqual("Running", ReadJob(first).State, "短等到期不停止命令,转入后台");
            Assert.IsTrue(run.HasActiveJobs);
            IReadOnlyList<CommandJobSnapshot> delivered = await run.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));
            CommandJobSnapshot done = delivered.Single();
            Assert.AreEqual(CommandJobState.Succeeded, done.State, "完成后像远端任务一样通知模型");
            Assert.AreEqual(0, done.ExitCode);
            StringAssert.Contains(string.Concat(done.Output.Select(o => o.Text)), "done");
        }
    }

    [TestMethod]
    public async Task LocalCommand_Timeout_KeepsOutputSoFar_AndTerminatesTheProcessTree()
    {
        (AgentToolbox toolbox, CommandJobRunScope run, AIFunction cli, TestPluginContext context) = await LocalCliAsync();
        await using (toolbox)
        await using (run)
        using (context)
        {
            var watch = Stopwatch.StartNew();
            string result = await RunLocalAsync(cli, Shell("Write-Output before; Start-Sleep 60", "echo before; sleep 60"),
                waitSeconds: 30, timeoutSeconds: 2);
            (string state, int? exit, string stdout, _, string? error) = ReadJob(result);
            Assert.AreEqual("TimedOut", state);
            Assert.IsNull(exit);
            StringAssert.Contains(stdout, "before", "超时前收到的输出要保留");
            StringAssert.Contains(error ?? "", "terminated its process tree");
            Assert.IsLessThan(TimeSpan.FromSeconds(20), watch.Elapsed);
            Assert.IsFalse(run.HasUnknownOutcome, "进程树已确认终止,不是「结果未知」,不必暂停整轮");
        }
    }

    [TestMethod]
    public async Task LocalCommand_BackgroundChildHoldingTheOutputPipe_DoesNotHangTheTool()
    {
        (AgentToolbox toolbox, CommandJobRunScope run, AIFunction cli, TestPluginContext context) = await LocalCliAsync();
        await using (toolbox)
        await using (run)
        using (context)
        {
            var watch = Stopwatch.StartNew();
            // shell 立刻退出,但留下一个继承了标准输出、还要跑 8 秒的子进程
            string result = await RunLocalAsync(cli,
                Shell("Write-Output started; cmd.exe /d /c \"start /b ping -n 9 127.0.0.1 >nul\"", "echo started; sleep 8 &"),
                waitSeconds: 30);
            (string state, int? exit, string stdout, string stderr, _) = ReadJob(result);
            Assert.IsLessThan(TimeSpan.FromSeconds(7), watch.Elapsed, $"不能等到后台子进程退出才返回;实际 {watch.Elapsed}");
            Assert.AreEqual("Succeeded", state);
            Assert.AreEqual(0, exit);
            StringAssert.Contains(stdout, "started");
            if (stderr.Length > 0)
            {
                StringAssert.Contains(stderr, "kept the output stream open");
            }
        }
    }

    [TestMethod]
    public async Task LocalCommand_UserStop_TerminatesTheProcessAndIsNotAnUnknownOutcome()
    {
        (AgentToolbox toolbox, CommandJobRunScope run, AIFunction cli, TestPluginContext context) = await LocalCliAsync();
        await using (toolbox)
        await using (run)
        using (context)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                RunLocalAsync(cli, Shell("Start-Sleep 60", "sleep 60"), waitSeconds: 60, cancellationToken: stop.Token));
            for (int i = 0; i < 100 && run.HasActiveJobs; i++) await Task.Delay(50);
            Assert.IsFalse(run.HasUnknownOutcome, "本机命令被停时进程树已终止,结局确定");
        }
    }

    [TestMethod]
    public async Task LocalCommand_TooLongForTheWindowsCommandLine_IsRefusedBeforeApproval()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only limit.");
        }
        int approvals = 0;
        (AgentToolbox toolbox, CommandJobRunScope run, AIFunction cli, TestPluginContext context) =
            await LocalCliAsync(approve: _ => { approvals++; return Task.FromResult(true); });
        await using (toolbox)
        await using (run)
        using (context)
        {
            string result = await RunLocalAsync(cli, "Write-Output '" + new string('x', 40_000) + "'");
            StringAssert.Contains(result, "too long for the Windows command line");
            Assert.AreEqual(0, approvals, "起不来的命令不该让用户逐个解释器去批");
            Assert.IsFalse(run.HasActiveJobs);
        }
    }

    [TestMethod]
    [DataRow("cmd.exe /d /c \"echo warn 1>&2 & exit 0\" 2>&1", 0)]
    [DataRow("cmd.exe /d /c exit 3", 3)]
    [DataRow("Get-Item -LiteralPath 'Z:\\definitely\\missing\\vela'", 1)]
    [DataRow("Write-Output ok", 0)]
    public async Task LocalCommand_ExitCodeFollowsNativeCommands_InWindowsPowerShell51(string command, int expected)
    {
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(legacy))
        {
            Assert.Inconclusive("Needs Windows PowerShell 5.1.");
        }
        (AgentToolbox toolbox, CommandJobRunScope run, AIFunction cli, TestPluginContext context) =
            await LocalCliAsync(interpreters: () => [legacy]);
        await using (toolbox)
        await using (run)
        using (context)
        {
            // 5.1 把重定向进来的 stderr 当成错误记录、把 $? 置假;只因为这个就报退出码 1 是误报
            Assert.AreEqual(expected, ReadJob(await RunLocalAsync(cli, command, waitSeconds: 30)).Exit);
        }
    }

    [TestMethod]
    public async Task LocalCommand_ReadOnlyWorkspace_DoesNotExposeTheTool()
    {
        (AgentToolbox toolbox, CommandJobRunScope run, AIFunction? cli, TestPluginContext context) = await LocalCliAsync(allowWrite: false);
        await using (toolbox)
        await using (run)
        using (context)
        {
            Assert.IsNull(cli, "运行时要求可写,只读工作区注册了也永远跑不了");
        }
    }

    [TestMethod]
    public void LocalCommand_ApprovalTextRevealsInvisibleAndBidiCharacters()
    {
        (string text, int hidden) = AgentToolbox.RevealInvisible("rm -rf ./build\u202E/txt.exe\u200B\nls\tx");
        Assert.AreEqual(2, hidden);
        Assert.AreEqual("rm -rf ./build⟦U+202E⟧/txt.exe⟦U+200B⟧\nls\tx", text, "换行和制表照常显示,方向覆盖与零宽字符显形");
        Assert.AreEqual(("plain 中文", 0), AgentToolbox.RevealInvisible("plain 中文"));
    }

    [TestMethod]
    public void LocalCommand_WindowsWrapperKeepsTheCommandOnTheFirstLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual("echo hi", AgentToolbox.BuildScript("echo hi"));
            // bash -c -V 会打印版本而不是执行 -V;垫一个空格后不再被当成 shell 选项
            Assert.AreEqual(" -V", AgentToolbox.BuildScript("-V"));
            return;
        }
        string script = AgentToolbox.BuildScript("throw 'boom'");
        StringAssert.EndsWith(script.Split('\n')[0], "throw 'boom'", "报错行号要与原命令一致,前导设置不能单独占一行");
    }

    [TestMethod]
    public void LocalOutputDecoder_Utf8SplitAcrossChunks_IsReassembled()
    {
        var decoder = new AgentToolbox.LocalOutputDecoder(null);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("构建成功 ✓");
        var text = new System.Text.StringBuilder();
        // 逐字节喂进去:每个多字节字符都会被截断在块边界上
        foreach (byte b in bytes)
        {
            text.Append(decoder.Decode([b], final: false));
        }
        text.Append(decoder.Decode([], final: true));
        Assert.AreEqual("构建成功 ✓", text.ToString());
        Assert.IsFalse(decoder.UsingLegacy);
    }

    [TestMethod]
    public void LocalOutputDecoder_LegacyCodePageOutput_SwitchesInsteadOfMojibake()
    {
        System.Text.Encoding gbk = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(936)!;
        var decoder = new AgentToolbox.LocalOutputDecoder(gbk);
        byte[] first = System.Text.Encoding.UTF8.GetBytes("ok\n");
        byte[] legacy = gbk.GetBytes("正在 Ping 本机,共 4 次\n");
        string text = decoder.Decode(first, final: false)
            + decoder.Decode(legacy.AsSpan(0, 5), final: false)
            + decoder.Decode(legacy.AsSpan(5), final: false)
            + decoder.Decode([], final: true);
        Assert.AreEqual("ok\n正在 Ping 本机,共 4 次\n", text, "GBK 输出按本地代码页解码,跨块也不拆字");
        Assert.IsTrue(decoder.UsingLegacy);

        var noFallback = new AgentToolbox.LocalOutputDecoder(null);
        StringAssert.Contains(noFallback.Decode(legacy, final: true), "�", "没有本地代码页可退时照常显示替换符,不抛异常");
    }

    [TestMethod]
    public void SteeringQueue_MergedMessages_AreUserInitiatedOnlyIfAllAre()
    {
        var queue = new SteeringQueue();
        queue.Enqueue(new SteeringMessage("a", "a", new ChatMessage(ChatRole.User, "a")));
        queue.Enqueue(new SteeringMessage("b", "b", new ChatMessage(ChatRole.User, "b")));
        Assert.IsTrue(queue.DrainMerged()!.UserInitiated, "用户排队的消息仍算用户发起,能拿到本机命令");
        queue.Enqueue(new SteeringMessage("a", "a", new ChatMessage(ChatRole.User, "a")));
        queue.Enqueue(new SteeringMessage("log", "log", new ChatMessage(ChatRole.User, "log"), UserInitiated: false));
        Assert.IsFalse(queue.DrainMerged()!.UserInitiated, "混进了外部注入的文本,整条都不算");
    }
}
