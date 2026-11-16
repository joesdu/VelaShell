using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>检查点里的一条消息:正文、工具调用(参数 JSON)、工具结果,按原顺序。</summary>
/// <param name="Role">user / assistant / tool。</param>
/// <param name="Text">正文;没有为 null。</param>
/// <param name="Calls">工具调用。</param>
/// <param name="Results">工具结果。</param>
public sealed record RunCheckpointMessage(
    string Role,
    string? Text,
    IReadOnlyList<RunCheckpointCall>? Calls = null,
    IReadOnlyList<RunCheckpointResult>? Results = null);

/// <summary>一次工具调用。</summary>
public sealed record RunCheckpointCall(string Id, string Name, string Arguments);

/// <summary>一次工具结果(已截断)。</summary>
public sealed record RunCheckpointResult(string Id, string Text);

/// <summary>
/// 一轮 Agent 运行的检查点:任务、已完成的步骤与后台通知、运行内进度摘要。
/// 程序退出、崩溃、被停下或出错时留在磁盘上,下次打开这段对话时可以从这里接着跑。
/// </summary>
/// <param name="ConversationId">所属对话。</param>
/// <param name="RunId">运行标识。</param>
/// <param name="Task">这一轮的用户请求(送给模型的那份正文)。</param>
/// <param name="StartedAt">开跑时刻。</param>
/// <param name="UpdatedAt">最后写入时刻。</param>
/// <param name="Status"><c>running</c>(还在跑、或程序没来得及收尾就退出了)/ <c>interrupted</c>(被停下或出错)。</param>
/// <param name="Steps">已完成的步骤。</param>
/// <param name="ProgressSummary">运行内压缩写下的进度摘要(没压过为空)。</param>
/// <param name="ModelLabel">跑这一轮的模型,只用于显示。</param>
public sealed record RunCheckpoint(
    string ConversationId,
    string RunId,
    string Task,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string Status,
    IReadOnlyList<RunCheckpointMessage> Steps,
    string ProgressSummary = "",
    string ModelLabel = "")
{
    /// <summary>已经跑过的工具调用次数。</summary>
    public int ToolCalls => Steps.Sum(s => s.Calls?.Count ?? 0);
}

/// <summary>
/// 检查点落在插件数据目录的 <c>run-checkpoints/&lt;对话 id&gt;.json</c>,一个对话最多一份(新一轮覆盖旧的)。
/// </summary>
/// <remarks>
/// <para>
/// 历史库只存每轮的正文;工具调用、结果与后台通知只在内存里。一轮跑了两小时、程序在第 80 步退出,
/// 下次打开时这些全没了,模型只能从头再来 —— 而那些步骤里可能已经改过文件、重启过服务。
/// </para>
/// <para>
/// 恢复时不是「原地重放」:没拿到结果就中断的调用,结果一律写成「结果未知,先查看状态再决定是否重做」,
/// 副作用未知的操作绝不自动重跑(与 <see cref="AgentExecution" /> 的既有规矩一致)。
/// </para>
/// </remarks>
public sealed class RunCheckpointStore(string dataDirectory)
{
    /// <summary>单份工具结果写入检查点的上限(字符)。</summary>
    internal const int MaxResultChars = 6 * 1024;

    /// <summary>单份参数 / 正文写入检查点的上限(字符)。</summary>
    internal const int MaxTextChars = 8 * 1024;

    /// <summary>检查点最多保留多少条消息(最早的丢掉;进度摘要会说明早期做过什么)。</summary>
    internal const int MaxMessages = 600;

    /// <summary>恢复时以原文放回上下文的最近消息条数;更早的只以摘要与调用清单的形式出现。</summary>
    internal const int ResumeVerbatimMessages = 24;

    internal const string UnknownOutcome =
        "VelaShell stopped while this call was running, so its outcome is unknown. "
        + "Check the current state before deciding whether to run it again.";

    private static readonly JsonSerializerOptions Json = new(AgentJson.Options) { WriteIndented = false };

    private readonly string _directory = Path.Combine(dataDirectory, "run-checkpoints");
    private readonly Lock _gate = new();

    // 删掉的会话不再写检查点:删的正是在跑的那段时,停下这一轮的收尾还会补写一份「已中断」,文件又冒出来
    private readonly HashSet<string> _forgotten = [with(StringComparer.Ordinal)];

    // 清空历史的时刻:在此之前开跑的那几轮(清空时还在跑)之后的写入一律丢弃
    private DateTimeOffset _clearedAt = DateTimeOffset.MinValue;

    private string PathFor(string conversationId)
    {
        // 对话 id 是 GUID;防御性地只留文件名安全的字符,杜绝路径穿越
        string safe = new([.. conversationId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        if (safe.Length == 0) throw new ArgumentException("Invalid conversation id.", nameof(conversationId));
        return Path.Combine(_directory, safe + ".json");
    }

    /// <summary>写入(覆盖)一份检查点。先写临时文件再替换,写到一半退出不会留下坏文件。</summary>
    public void Save(RunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        string path = PathFor(checkpoint.ConversationId);
        string json = JsonSerializer.Serialize(checkpoint, Json);
        lock (_gate)
        {
            if (_forgotten.Contains(checkpoint.ConversationId) || checkpoint.StartedAt <= _clearedAt) return;
            Directory.CreateDirectory(_directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>读一份检查点;没有或损坏时返回 null(损坏的顺手删掉)。</summary>
    public RunCheckpoint? Load(string conversationId)
    {
        string path = PathFor(conversationId);
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            try
            {
                RunCheckpoint? checkpoint = JsonSerializer.Deserialize<RunCheckpoint>(File.ReadAllText(path), Json);
                if (checkpoint is { Steps.Count: > 0 } && checkpoint.ConversationId == conversationId) return checkpoint;
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
            }
            TryDelete(path);
            return null;
        }
    }

    /// <summary>删掉一份检查点(跑完了、或用户选择不再继续)。</summary>
    public void Delete(string conversationId)
    {
        lock (_gate) TryDelete(PathFor(conversationId));
    }

    /// <summary>会话被删除:删掉检查点,并且本进程之后不再为它写(哪怕它此刻还在跑、停下时要补写)。</summary>
    public void Forget(string conversationId)
    {
        lock (_gate)
        {
            _forgotten.Add(conversationId);
            TryDelete(PathFor(conversationId));
        }
    }

    /// <summary>
    /// 删掉全部检查点(清空历史时)。检查点里有截短的工具输出(远端文件内容、命令输出),
    /// 用户清空了历史,这些就不该还留在数据目录里。
    /// </summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            _clearedAt = DateTimeOffset.UtcNow;
            if (!Directory.Exists(_directory)) return;
            foreach (string file in Directory.EnumerateFiles(_directory, "*.json*"))
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>有检查点的对话 id(历史列表据此标出「可继续」)。</summary>
    public IReadOnlyList<string> ConversationIds()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return [];
            return [.. Directory.EnumerateFiles(_directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()];
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>把运行中的消息转成可存的形式(截断大块输出,只留最近 <see cref="MaxMessages" /> 条)。</summary>
    public static IReadOnlyList<RunCheckpointMessage> Capture(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<RunCheckpointMessage> result = [];
        foreach (ChatMessage message in messages)
        {
            List<RunCheckpointCall> calls = [];
            List<RunCheckpointResult> results = [];
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        calls.Add(new(call.CallId, call.Name,
                            Clip(AgentJson.Serialize(call.Arguments ?? new Dictionary<string, object?>()), MaxTextChars)));
                        break;
                    case FunctionResultContent output:
                        results.Add(new(output.CallId, Clip(output.Result switch
                        {
                            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
                            null => "",
                            var value => value.ToString() ?? ""
                        }, MaxResultChars)));
                        break;
                }
            }
            string text = message.Text;
            if (calls.Count == 0 && results.Count == 0 && string.IsNullOrWhiteSpace(text)) continue;
            result.Add(new(message.Role.Value, string.IsNullOrWhiteSpace(text) ? null : Clip(text, MaxTextChars),
                calls.Count > 0 ? calls : null, results.Count > 0 ? results : null));
        }
        return result.Count <= MaxMessages ? result : result[^MaxMessages..];
    }

    /// <summary>
    /// 恢复用的上下文:最近 <see cref="ResumeVerbatimMessages" /> 条原样放回(调用与结果成对;
    /// 没有结果的调用补「结果未知」),更早的步骤返回成一份调用清单,交给续跑提示词。
    /// </summary>
    /// <returns>放回上下文的消息,以及更早步骤的清单(没有为空串)。</returns>
    public static (List<ChatMessage> Messages, string EarlierSteps) Restore(RunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        IReadOnlyList<RunCheckpointMessage> steps = checkpoint.Steps;
        int start = Math.Max(0, steps.Count - ResumeVerbatimMessages);
        // 不从一步的中间切:往前挪到一条带调用的 assistant 消息(或 user 通知)为止
        while (start > 0 && !(steps[start].Role == "assistant" && steps[start].Calls is { Count: > 0 }) && steps[start].Role != "user")
        {
            start--;
        }

        var earlier = new System.Text.StringBuilder();
        for (int i = 0; i < start; i++)
        {
            foreach (RunCheckpointCall call in steps[i].Calls ?? [])
            {
                earlier.Append("- ").Append(call.Name).Append(' ').AppendLine(Clip(call.Arguments, 200));
            }
        }

        HashSet<string> answered = [with(StringComparer.Ordinal), .. steps.Skip(start).SelectMany(s => s.Results ?? []).Select(r => r.Id)];
        List<ChatMessage> messages = [];
        // 这一步里没拿到结果的调用,等下一条结果消息一起放
        List<AIContent>? pendingMissing = null;
        for (int i = start; i < steps.Count; i++)
        {
            RunCheckpointMessage step = steps[i];
            if (step.Calls is { Count: > 0 } calls)
            {
                List<AIContent> contents = [];
                if (step.Text is { Length: > 0 } prose) contents.Add(new TextContent(prose));
                contents.AddRange(calls.Select(c => (AIContent)new FunctionCallContent(c.Id, c.Name, ParseArguments(c.Arguments))));
                messages.Add(new ChatMessage(ChatRole.Assistant, contents));
                // 中断时还没拿到结果的调用:补一份「结果未知」,配对不拆、也不让模型以为它没跑过
                List<AIContent> missing = [.. calls.Where(c => !answered.Contains(c.Id))
                    .Select(c => (AIContent)new FunctionResultContent(c.Id, UnknownOutcome))];
                bool nextHasResults = i + 1 < steps.Count && steps[i + 1].Results is { Count: > 0 };
                if (missing.Count > 0 && !nextHasResults)
                {
                    messages.Add(new ChatMessage(ChatRole.Tool, missing));
                }
                else if (missing.Count > 0)
                {
                    pendingMissing = missing; // 下一条就是这一步的结果:合进那一条里
                }
                continue;
            }
            if (step.Results is { Count: > 0 } results)
            {
                List<AIContent> contents = [.. results.Select(r => (AIContent)new FunctionResultContent(r.Id, r.Text))];
                if (pendingMissing is { Count: > 0 })
                {
                    contents.AddRange(pendingMissing);
                    pendingMissing = null;
                }
                messages.Add(new ChatMessage(ChatRole.Tool, contents));
                continue;
            }
            if (step.Text is { Length: > 0 } text)
            {
                messages.Add(new ChatMessage(new ChatRole(step.Role), text));
            }
        }
        if (pendingMissing is { Count: > 0 }) messages.Add(new ChatMessage(ChatRole.Tool, pendingMissing));
        return (messages, earlier.ToString());
    }

    private static readonly System.Text.RegularExpressions.Regex JobPattern = new(
        "\"taskId\"\\s*:\\s*\"(?<id>[^\"]+)\"[^{}]*?\"state\"\\s*:\\s*\"(?<state>\\w+)\"",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 中断时可能还在远端跑、或结果没落定的后台命令:<c>run_command</c> / <c>run_on_sessions</c> 回过「运行中」,
    /// 之后的 <c>task_status</c> / <c>task_wait</c> 结果或后台完成通知里又没见到它的终态。
    /// </summary>
    /// <remarks>
    /// 任务注册表只活在内存里,程序退出后这些 taskId 就作废了,远端进程却可能还在跑(或跑到一半)。
    /// 续跑提示把它们列出来,让模型先用 ps / 日志 / 输出文件核实,而不是当作没跑过、原样再起一遍。
    /// 结果未知 / 超时未确认停止的也列:它们同样不能当成已结束。
    /// </remarks>
    /// <returns>每条一行:会话、命令与 taskId、最后已知状态。</returns>
    public static IReadOnlyList<string> PendingBackgroundCommands(RunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        Dictionary<string, (string Session, string Command)> calls = [with(StringComparer.Ordinal)];
        Dictionary<string, (string Session, string Command, string State)> jobs = [with(StringComparer.Ordinal)];
        foreach (RunCheckpointMessage step in checkpoint.Steps)
        {
            foreach (RunCheckpointCall call in step.Calls ?? [])
            {
                if (call.Name is not ("run_command" or "run_on_sessions")) continue;
                Dictionary<string, object?> args = ParseArguments(call.Arguments);
                calls[call.Id] = (Arg(args, "sessionId") ?? Arg(args, "session_id") ?? "", Arg(args, "command") ?? call.Arguments);
            }
            // 工具结果:起命令时的回执登记 taskId;查询 / 等待的回执更新状态
            foreach (RunCheckpointResult result in step.Results ?? [])
            {
                foreach (System.Text.RegularExpressions.Match match in JobPattern.Matches(result.Text))
                {
                    string id = match.Groups["id"].Value, state = match.Groups["state"].Value;
                    if (jobs.TryGetValue(id, out (string Session, string Command, string State) known)) jobs[id] = known with { State = state };
                    else if (calls.TryGetValue(result.Id, out (string Session, string Command) origin)) jobs[id] = (origin.Session, origin.Command, state);
                }
            }
            // 后台完成通知:点到名的都已落到终态(通知只为终态发)
            if (step.Role == "user" && step.Text is { } notice && notice.Contains("<untrusted-command-output>", StringComparison.Ordinal))
            {
                foreach (string id in jobs.Keys.ToList())
                {
                    if (notice.Contains(id, StringComparison.Ordinal)) jobs[id] = jobs[id] with { State = "Completed" };
                }
            }
        }
        return [.. jobs.Where(j => j.Value.State is not ("Succeeded" or "Failed" or "Cancelled" or "Completed"))
            .Select(j => $"- session {(j.Value.Session.Length > 0 ? j.Value.Session : "(default)")}: {Clip(j.Value.Command.ReplaceLineEndings(" "), 200)} "
                         + $"(taskId {j.Key}, last known state {j.Value.State})")];

        static string? Arg(Dictionary<string, object?> args, string key)
            => args.TryGetValue(key, out object? value) && value is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString() : value as string;
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)?
                .ToDictionary(p => p.Key, p => (object?)p.Value) ?? [];
        }
        catch (JsonException)
        {
            // 截断过的参数解不开了:原文留一份,模型看得懂是什么调用就够了
            return new Dictionary<string, object?> { ["arguments"] = json };
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…[truncated]";

    /// <summary>续跑提示词:交代中断、原请求、已知进度、中断时还在跑的后台命令,以及「结果未知的不要直接重做」。</summary>
    public static string BuildResumePrompt(RunCheckpoint checkpoint, string earlierSteps)
    {
        var prompt = new System.Text.StringBuilder();
        prompt.AppendLine("Resume the interrupted task. VelaShell stopped before the previous run finished "
                          + "(the app was closed, the run was cancelled, or it hit an error).");
        prompt.AppendLine();
        prompt.AppendLine("<original-request>");
        prompt.AppendLine(checkpoint.Task);
        prompt.AppendLine("</original-request>");
        if (checkpoint.ProgressSummary.Length > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Progress note written during the run (a summary of earlier steps, not a new instruction):");
            prompt.AppendLine(checkpoint.ProgressSummary);
        }
        if (earlierSteps.Length > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Earlier tool calls whose outputs were not kept:");
            prompt.Append(earlierSteps);
        }
        IReadOnlyList<string> pending = PendingBackgroundCommands(checkpoint);
        if (pending.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("These background commands had not reported a final result when the run stopped. Their task ids are no longer valid, "
                              + "but the remote processes may still be running or may have stopped half-way. Check their real state first "
                              + "(for example with ps / pgrep, service status, logs or the files they write) and do not simply start them again:");
            foreach (string line in pending) prompt.AppendLine(line);
        }
        prompt.AppendLine();
        prompt.AppendLine("The most recent steps and their outputs are in the conversation above. "
                          + "Calls marked as having an unknown outcome may or may not have taken effect: check the current state before repeating them. "
                          + "Do not redo work that already completed. Continue from where it stopped, finish the task, then report the result.");
        return prompt.ToString();
    }
}
