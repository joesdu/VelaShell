namespace VelaShell.Plugin.Ai.Chat;

/// <summary>A deterministic diagnostic emitted while compiling a prompt or context.</summary>
public readonly record struct PromptDiagnostic(string Code, string Message, int? Index = null);

/// <summary>An ordered system-prompt section.</summary>
public readonly record struct PromptSection(string Id, string Text);

/// <summary>Immutable input to the host-neutral system-prompt compiler.</summary>
public sealed record PromptCompilationInput
{
    /// <summary>按给定顺序复制段落列表，保留编译时的拼接顺序。</summary>
    public PromptCompilationInput(IReadOnlyList<PromptSection> Sections)
    {
        ArgumentNullException.ThrowIfNull(Sections);
        this.Sections = [.. Sections];
    }

    /// <summary>待拼接的系统提示词段落，按输入顺序排列。</summary>
    public IReadOnlyList<PromptSection> Sections { get; }
}

/// <summary>Immutable result of compiling system-prompt sections.</summary>
public sealed record PromptCompilationResult
{
    /// <summary>保存编译后的提示词，并复制对应的段落标识和诊断列表。</summary>
    public PromptCompilationResult(string Text, IReadOnlyList<string> SectionIds,
        IReadOnlyList<PromptDiagnostic> Diagnostics)
    {
        this.Text = Text ?? throw new ArgumentNullException(nameof(Text));
        ArgumentNullException.ThrowIfNull(SectionIds);
        ArgumentNullException.ThrowIfNull(Diagnostics);
        this.SectionIds = [.. SectionIds];
        this.Diagnostics = [.. Diagnostics];
    }

    /// <summary>由参与编译的段落组成的系统提示词全文。</summary>
    public string Text { get; }
    /// <summary>实际写入提示词的各段标识，按输出顺序排列，可包含重复项。</summary>
    public IReadOnlyList<string> SectionIds { get; }
    /// <summary>编译时发现的段落问题，例如空段落或重复标识。</summary>
    public IReadOnlyList<PromptDiagnostic> Diagnostics { get; }
}

/// <summary>Compiles ordered prompt sections without depending on a host or UI framework.</summary>
public interface IPromptCompiler
{
    /// <summary>按输入顺序编译系统提示词，返回正文、实际采用的段落标识及诊断。</summary>
    PromptCompilationResult Compile(PromptCompilationInput input);
}

/// <summary>The default deterministic system-prompt compiler.</summary>
public sealed class PromptCompiler : IPromptCompiler
{
    /// <inheritdoc/>
    /// <remarks>跳过空文本，其余段落直接拼接，不插入分隔符。标识重复时记录诊断，但仍保留段落。</remarks>
    public PromptCompilationResult Compile(PromptCompilationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = new System.Text.StringBuilder();
        List<string> ids = [];
        List<PromptDiagnostic> diagnostics = [];
        HashSet<string> seen = [with(StringComparer.Ordinal)];

        for (int i = 0; i < input.Sections.Count; i++)
        {
            PromptSection section = input.Sections[i];
            if (string.IsNullOrEmpty(section.Text))
            {
                diagnostics.Add(new("prompt.empty-section", $"Section '{section.Id}' was skipped because it was empty.", i));
                continue;
            }

            if (!seen.Add(section.Id))
            {
                diagnostics.Add(new("prompt.duplicate-section", $"Section '{section.Id}' occurs more than once.", i));
            }

            ids.Add(section.Id);
            text.Append(section.Text);
        }

        return new PromptCompilationResult(text.ToString(), ids, diagnostics);
    }
}

/// <summary>The normalized role categories needed by context compilation.</summary>
public enum ContextMessageRole
{
    /// <summary>不属于其余标准类别的宿主消息角色。</summary>
    Other,
    /// <summary>提供系统指令的消息角色。</summary>
    System,
    /// <summary>用户消息角色，用于识别历史中的用户轮次起点。</summary>
    User,
    /// <summary>模型生成的回复或工具调用所属的助手角色。</summary>
    Assistant,
    /// <summary>用于回传工具执行结果的消息角色。</summary>
    Tool
}

/// <summary>The kind of tool content present in a context message.</summary>
public enum ContextToolPartKind
{
    /// <summary>工具调用请求，按调用标识与结果配对。</summary>
    Call,
    /// <summary>工具返回的结果，按调用标识查找对应请求。</summary>
    Result
}

/// <summary>A tool call or result identifier used to keep tool rounds paired.</summary>
public readonly record struct ContextToolPart(ContextToolPartKind Kind, string CallId);

/// <summary>Host-neutral facts about one context message.</summary>
public readonly record struct ContextMessageInfo(
    ContextMessageRole Role,
    string RoleKey,
    int EstimatedTokens,
    IReadOnlyList<ContextToolPart> ToolParts)
{
    /// <summary>归一化角色是否为用户，用于在裁剪历史时查找用户轮次的起点。</summary>
    public bool IsUser => Role == ContextMessageRole.User;
}

/// <summary>Result of removing orphaned tool content from one message.</summary>
public readonly record struct ContextMessageNormalization<TMessage>(
    TMessage Message,
    bool Included,
    int RemovedToolParts);

/// <summary>Immutable input to the host-neutral context compiler.</summary>
public sealed record ContextCompilationInput<TMessage>
{
    /// <summary>复制待编译的历史消息，并记录构造上下文所需的提示词、预算和摘要边界。</summary>
    /// <param name="systemPrompt">置于上下文开头的系统提示词。</param>
    /// <param name="history">按对话先后顺序排列的历史消息。</param>
    /// <param name="windowTokens">上下文窗口的 token 数；非正值表示不按预算裁剪。</param>
    /// <param name="reserveTokens">从窗口中预留、不分配给输入的 token 数；负值按零处理。</param>
    /// <param name="summary">代替已摘要前缀的文本；为空时不插入摘要消息。</param>
    /// <param name="summarizedThrough">已摘要的前缀消息数，也是首条未摘要消息的从零开始的索引。</param>
    public ContextCompilationInput(string systemPrompt, IReadOnlyList<TMessage> history,
        int windowTokens, int reserveTokens, string summary = "", int summarizedThrough = 0)
    {
        SystemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        ArgumentNullException.ThrowIfNull(history);
        History = [.. history];
        WindowTokens = windowTokens;
        ReserveTokens = reserveTokens;
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        SummarizedThrough = summarizedThrough;
    }

    /// <summary>作为上下文首条系统消息的文本。</summary>
    public string SystemPrompt { get; }
    /// <summary>按原始对话顺序保存的历史消息，包括已被摘要覆盖的前缀。</summary>
    public IReadOnlyList<TMessage> History { get; }
    /// <summary>用于计算输入预算的窗口容量（token 数）；非正值表示跳过预算裁剪。</summary>
    public int WindowTokens { get; }
    /// <summary>计算输入预算时先从窗口容量中扣除的预留 token 数。</summary>
    public int ReserveTokens { get; }
    /// <summary>已摘要前缀的替代文本；文本非空且确有前缀时作为用户消息插入。</summary>
    public string Summary { get; }
    /// <summary>历史开头已被摘要覆盖的消息数；编译时限制在零和历史消息总数之间。</summary>
    public int SummarizedThrough { get; }
}

/// <summary>Immutable result of host-neutral context compilation.</summary>
public sealed record ContextCompilationResult<TMessage>
{
    /// <summary>保存裁剪计数和最终 token 估算值，并复制编译得到的消息序列与诊断。</summary>
    public ContextCompilationResult(IReadOnlyList<TMessage> messages, int droppedMessages,
        int estimatedTokens, IReadOnlyList<PromptDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Messages = [.. messages];
        DroppedMessages = droppedMessages;
        EstimatedTokens = estimatedTokens;
        Diagnostics = [.. diagnostics];
    }

    /// <summary>供模型使用的有序消息：系统提示词、可选摘要和整理后的历史。</summary>
    public IReadOnlyList<TMessage> Messages { get; }
    /// <summary>在摘要边界之后、保留历史起点之前跳过的消息数，不含工具清理或角色合并减少的消息。</summary>
    public int DroppedMessages { get; }
    /// <summary>最终消息序列中每条消息的 token 估算值之和，并非模型实测用量。</summary>
    public int EstimatedTokens { get; }
    /// <summary>编译期间有关预算、裁剪、工具配对及消息合并的诊断。</summary>
    public IReadOnlyList<PromptDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Supplies the small host-specific boundary required by <see cref="PromptContextCompiler"/>.
/// The compiler itself only operates on these facts and never references a chat SDK.
/// </summary>
public interface IContextMessageAdapter<TMessage>
{
    /// <summary>提取归一化角色、用于合并的角色键、token 估算值及工具配对信息。</summary>
    ContextMessageInfo Describe(TMessage message);
    /// <summary>将系统提示词封装为宿主的系统消息。</summary>
    TMessage CreateSystem(string text);
    /// <summary>将文本封装为宿主的用户消息，以便用摘要代替历史前缀。</summary>
    TMessage CreateUser(string text);
    /// <summary>按先后顺序将两条角色键相同的相邻历史消息合并为一条。</summary>
    TMessage Merge(TMessage first, TMessage second);
    /// <summary>移除在保留历史中没有对应结果的工具调用，以及没有对应调用的结果。</summary>
    /// <param name="message">要检查工具内容的历史消息。</param>
    /// <param name="callIds">保留历史中出现的工具调用标识。</param>
    /// <param name="resultIds">保留历史中出现的工具结果所对应的调用标识。</param>
    /// <returns>清理后的消息、是否仍应保留它，以及移除的工具内容项数。</returns>
    ContextMessageNormalization<TMessage> RemoveOrphanedToolParts(TMessage message,
        IReadOnlySet<string> callIds, IReadOnlySet<string> resultIds);
}

/// <summary>Deterministic history budget, compaction, and tool-pairing compiler.</summary>
public sealed class PromptContextCompiler
{
    private const int AlwaysKeep = 4;
    private readonly IPromptCompiler _promptCompiler;

    /// <summary>指定系统提示词编译器；未提供时使用 <see cref="PromptCompiler"/>。</summary>
    public PromptContextCompiler(IPromptCompiler? promptCompiler = null)
        => _promptCompiler = promptCompiler ?? new PromptCompiler();

    /// <summary>用配置的提示词编译器处理有序段落，返回正文、段落标识与诊断。</summary>
    public PromptCompilationResult CompilePrompt(PromptCompilationInput input)
        => _promptCompiler.Compile(input);

    /// <summary>从摘要边界起按预算选取历史，清理未配对的工具内容并合并相邻同角色键的历史消息。</summary>
    /// <typeparam name="TMessage">宿主使用的消息类型。</typeparam>
    /// <param name="input">系统提示词、历史、可选摘要及窗口预算。</param>
    /// <param name="adapter">负责估算消息开销、识别角色与工具内容并构造输出消息的宿主适配器。</param>
    /// <returns>供模型使用的消息序列、裁剪计数、token 估算值及诊断；预算不是严格上限。</returns>
    public static ContextCompilationResult<TMessage> CompileContext<TMessage>(
        ContextCompilationInput<TMessage> input, IContextMessageAdapter<TMessage> adapter)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(adapter);

        IReadOnlyList<TMessage> history = input.History;
        int from = Math.Clamp(input.SummarizedThrough, 0, history.Count);
        bool hasSummary = input.Summary.Length > 0 && from > 0;
        TMessage system = adapter.CreateSystem(input.SystemPrompt);
        TMessage digest = default!;
        if (hasSummary)
        {
            digest = adapter.CreateUser(input.Summary);
        }
        List<PromptDiagnostic> diagnostics = [];

        int start = from;
        if (input.WindowTokens <= 0)
        {
            diagnostics.Add(new("context.unbounded-window", "Context window is unknown; history was not budget-trimmed."));
        }
        else
        {
            int budget = (int)((input.WindowTokens - Math.Max(0, input.ReserveTokens)) * 0.9);
            int fixedCost = Estimate(adapter, system) + (digest is null ? 0 : Estimate(adapter, digest));
            start = FirstIndexThatFits(adapter, fixedCost, history, from, Math.Max(budget, 0));
        }

        if (digest is null)
        {
            start = FirstUserFrom(adapter, history, start);
        }

        if (start > from)
        {
            diagnostics.Add(new("context.history-trimmed",
                $"Dropped {start - from} history message(s) before index {start}.", start));
        }

        var messages = new List<TMessage>(history.Count - start + 2) { system };
        if (digest is not null)
        {
            messages.Add(digest);
        }

        (HashSet<string> calls, HashSet<string> results) = CollectToolIds(adapter, history, start);
        ContextMessageInfo? previousInfo = null;
        for (int i = start; i < history.Count; i++)
        {
            TMessage message = history[i];
            ContextMessageNormalization<TMessage> normalized = adapter.RemoveOrphanedToolParts(message, calls, results);
            if (normalized.RemovedToolParts > 0)
            {
                diagnostics.Add(new("context.orphan-tool-content",
                    $"Removed {normalized.RemovedToolParts} orphaned tool content item(s).", i));
            }

            if (!normalized.Included)
            {
                continue;
            }

            ContextMessageInfo info = adapter.Describe(normalized.Message);
            if (messages.Count > 1 && previousInfo is { } previous && previous.RoleKey == info.RoleKey)
            {
                messages[^1] = adapter.Merge(messages[^1], normalized.Message);
                diagnostics.Add(new("context.adjacent-role-merged",
                    $"Merged adjacent '{info.RoleKey}' messages.", i));
                previousInfo = info;
                continue;
            }

            messages.Add(normalized.Message);
            previousInfo = info;
        }

        return new ContextCompilationResult<TMessage>(messages, start - from,
            Estimate(adapter, messages), diagnostics);
    }

    private static int FirstUserFrom<TMessage>(IContextMessageAdapter<TMessage> adapter,
        IReadOnlyList<TMessage> history, int start)
    {
        for (int i = start; i < history.Count; i++)
        {
            if (adapter.Describe(history[i]).IsUser)
            {
                return i;
            }
        }
        return start;
    }

    private static int FirstIndexThatFits<TMessage>(IContextMessageAdapter<TMessage> adapter,
        int fixedCost, IReadOnlyList<TMessage> history, int from, int budget)
    {
        int total = fixedCost + Estimate(adapter, history, from);
        int lastCuttable = Math.Max(from, history.Count - AlwaysKeep);
        int start = from;
        while (total > budget && start < lastCuttable)
        {
            total -= Estimate(adapter, history[start]);
            start++;
            while (start < lastCuttable && !adapter.Describe(history[start]).IsUser)
            {
                total -= Estimate(adapter, history[start]);
                start++;
            }
        }
        return start;
    }

    private static (HashSet<string> Calls, HashSet<string> Results) CollectToolIds<TMessage>(
        IContextMessageAdapter<TMessage> adapter, IReadOnlyList<TMessage> history, int start)
    {
        HashSet<string> calls = [with(StringComparer.Ordinal)];
        HashSet<string> results = [with(StringComparer.Ordinal)];
        for (int i = start; i < history.Count; i++)
        {
            foreach (ContextToolPart part in adapter.Describe(history[i]).ToolParts)
            {
                (part.Kind == ContextToolPartKind.Call ? calls : results).Add(part.CallId);
            }
        }
        return (calls, results);
    }

    private static int Estimate<TMessage>(IContextMessageAdapter<TMessage> adapter, TMessage message)
        => adapter.Describe(message).EstimatedTokens;

    private static int Estimate<TMessage>(IContextMessageAdapter<TMessage> adapter,
        IReadOnlyList<TMessage> messages, int from = 0)
    {
        int total = 0;
        for (int i = from; i < messages.Count; i++)
        {
            total += Estimate(adapter, messages[i]);
        }
        return total;
    }
}
