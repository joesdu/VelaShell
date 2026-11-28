namespace VelaShell.Plugin.Ai.Core.Runtime;

/// <summary>一次 Agent 任务的显式阶段，避免把规划、执行和验证混成无限 ReAct 循环。</summary>
public enum AgentOrchestrationPhase
{
    /// <summary>任务刚建立，尚未开始规划，也没有当前步骤。</summary>
    Intake,
    /// <summary>已开始规划，等待接受计划后执行第一步。</summary>
    Planning,
    /// <summary>按计划顺序执行当前步骤；完成最后一步后转入验证。</summary>
    Executing,
    /// <summary>所有步骤均已标记完成，等待验证结果。</summary>
    Verifying,
    /// <summary>验证通过后的成功终态。</summary>
    Completed,
    /// <summary>显式记录失败原因后的终态；单纯验证不通过只会暂停。</summary>
    Failed,
    /// <summary>任务因请求或验证不通过而暂停；可接受计划并从第一步重新执行。</summary>
    Paused,
    /// <summary>任务已取消的终态，不再接受计划或推进步骤。</summary>
    Cancelled
}

/// <summary>规划器输出的一步。规划只描述目标和验收，不直接执行工具。</summary>
public sealed record AgentPlanStep
{
    /// <summary>步骤完成时记入已完成集合的标识；在同一计划内区分大小写。</summary>
    public string Id { get; }
    /// <summary>执行器应在本步骤达成的目标。</summary>
    public string Objective { get; }
    /// <summary>规划器列出的相关文件；未指定时为空列表。</summary>
    public IReadOnlyList<string> Files { get; }
    /// <summary>用于检查本步骤目标的验收项，不会由计划本身执行。</summary>
    public IReadOnlyList<string> Verification { get; }
    /// <summary>规划器给出的风险标签，不限于预定义值。</summary>
    public string Risk { get; }
    /// <summary>本步骤的尝试次数预算，包含首次执行。</summary>
    public int MaxAttempts { get; }

    /// <summary>描述计划中的一个步骤，但不开始执行。</summary>
    /// <param name="id">计划内唯一的非空白标识。</param>
    /// <param name="objective">非空白的步骤目标。</param>
    /// <param name="files">相关文件列表；传入 null 表示未列出文件。</param>
    /// <param name="verification">验收项列表；传入 null 表示未列出验收项。</param>
    /// <param name="risk">非空白风险标签，默认为 <c>low</c>。</param>
    /// <param name="maxAttempts">允许的总尝试次数，至少为 1；默认不重试。</param>
    public AgentPlanStep(string id, string objective, IReadOnlyList<string>? files = null,
        IReadOnlyList<string>? verification = null, string risk = "low", int maxAttempts = 1)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A plan step requires an id.", nameof(id));
        if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("A plan step requires an objective.", nameof(objective));
        if (string.IsNullOrWhiteSpace(risk)) throw new ArgumentException("A plan step requires a risk level.", nameof(risk));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        Id = id;
        Objective = objective;
        Files = files ?? [];
        Verification = verification ?? [];
        Risk = risk;
        MaxAttempts = maxAttempts;
    }
}

/// <summary>规划器生成的、可审计的任务计划。</summary>
public sealed record AgentPlan
{
    /// <summary>整份计划要达成的任务目标。</summary>
    public string Goal { get; }
    /// <summary>用来判断整体目标是否达成的验收标准，可为空列表。</summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; }
    /// <summary>实际执行顺序：从索引 0 开始，每完成一步再推进到下一步。</summary>
    public IReadOnlyList<AgentPlanStep> Steps { get; }
    /// <summary>规划器列出的停止条件；计划保存其描述，不负责判断条件是否满足。</summary>
    public IReadOnlyList<string> StopConditions { get; }
    /// <summary>计划创建时间，而非第一步开始执行的时间。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>按给定的顺序建立计划；拒绝空计划或标识重复的步骤。</summary>
    /// <param name="goal">非空白的总体目标。</param>
    /// <param name="acceptanceCriteria">总体验收标准；null 按空列表处理。</param>
    /// <param name="steps">至少一个步骤，标识按 <see cref="StringComparer.Ordinal"/> 比较必须唯一。</param>
    /// <param name="stopConditions">结束执行的条件描述；null 按空列表处理。</param>
    /// <param name="createdAt">要保留的创建时间；默认值表示使用当前 UTC 时间。</param>
    public AgentPlan(string goal, IReadOnlyList<string>? acceptanceCriteria,
        IReadOnlyList<AgentPlanStep>? steps, IReadOnlyList<string>? stopConditions,
        DateTimeOffset createdAt = default)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("A plan requires a goal.", nameof(goal));
        if (steps is null || steps.Count == 0) throw new ArgumentException("A plan requires at least one step.", nameof(steps));
        if (steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != steps.Count)
            throw new ArgumentException("Plan step ids must be unique.", nameof(steps));
        Goal = goal;
        AcceptanceCriteria = acceptanceCriteria ?? [];
        Steps = steps;
        StopConditions = stopConditions ?? [];
        CreatedAt = createdAt == default ? DateTimeOffset.UtcNow : createdAt;
    }
}

/// <summary>验证器发现的问题。</summary>
public sealed record AgentVerificationIssue(string Code, string Message, string Severity = "medium");

/// <summary>验证器对计划执行结果的独立判断。</summary>
public sealed record AgentVerificationResult(
    bool Passed,
    IReadOnlyList<AgentVerificationIssue> Issues,
    IReadOnlyList<string> Evidence,
    DateTimeOffset CompletedAt)
{
    /// <summary>记录验证通过，不附带问题。</summary>
    /// <param name="evidence">支持该判断的证据，按枚举顺序复制；null 表示无证据条目。</param>
    /// <returns>以当前 UTC 时间标记完成的通过结果。</returns>
    public static AgentVerificationResult Pass(IEnumerable<string>? evidence = null)
        => new(true, [], [.. evidence ?? []], DateTimeOffset.UtcNow);

    /// <summary>记录验证未通过，即使问题列表为空，结果仍为未通过。</summary>
    /// <param name="issues">按枚举顺序复制的问题；不能为 null。</param>
    /// <param name="evidence">支持该判断的证据，按枚举顺序复制；null 按空列表处理。</param>
    /// <returns>以当前 UTC 时间标记完成的未通过结果。</returns>
    public static AgentVerificationResult Fail(IEnumerable<AgentVerificationIssue> issues,
        IEnumerable<string>? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return new(false, [.. issues], [.. evidence ?? []], DateTimeOffset.UtcNow);
    }
}

/// <summary>宿主无关的 Agent 编排状态快照。</summary>
public sealed record AgentOrchestrationState(
    string RunId,
    AgentOrchestrationPhase Phase,
    AgentPlan? Plan,
    int CurrentStepIndex,
    int CurrentAttempt,
    IReadOnlySet<string> CompletedSteps,
    AgentVerificationResult? LastVerification,
    string? PauseReason,
    string? FailureReason,
    DateTimeOffset UpdatedAt)
{
    /// <summary>建立 Intake 快照，尚无计划、已完成步骤或验证结果。</summary>
    /// <param name="runId">用于关联本次运行的标识，不能为 null 或空字符串。</param>
    /// <returns>步骤索引为 -1、尝试次数为 0 的初始状态。</returns>
    public static AgentOrchestrationState Create(string runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        return new(runId, AgentOrchestrationPhase.Intake, null, -1, 0,
            new HashSet<string>(StringComparer.Ordinal), null, null, null, DateTimeOffset.UtcNow);
    }

    /// <summary>从 Intake 进入 Planning；其他阶段不允许开始规划。</summary>
    /// <returns>已更新阶段和时间的快照，尚未接受计划。</returns>
    public AgentOrchestrationState StartPlanning()
        => RequirePhase(AgentOrchestrationPhase.Intake) with
        {
            Phase = AgentOrchestrationPhase.Planning,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>在 Planning 或 Paused 接受计划，从第一步重新开始，而不是续跑暂停的步骤。</summary>
    /// <param name="plan">本次执行采用的非 null 计划。</param>
    /// <returns>进入 Executing 的快照；清空已完成步骤和暂停、失败原因，尝试次数归零，保留上次验证结果。</returns>
    public AgentOrchestrationState AcceptPlan(AgentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Phase is not AgentOrchestrationPhase.Planning and not AgentOrchestrationPhase.Paused)
            throw InvalidTransition(AgentOrchestrationPhase.Planning);
        return this with
        {
            Phase = AgentOrchestrationPhase.Executing,
            Plan = plan,
            CurrentStepIndex = 0,
            CurrentAttempt = 0,
            CompletedSteps = new HashSet<string>(StringComparer.Ordinal),
            PauseReason = null,
            FailureReason = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>为当前步骤登记一次尝试；本方法不检查步骤的尝试次数预算。</summary>
    /// <returns>步骤索引不变、尝试次数加一的快照，首次尝试为 1。</returns>
    /// <exception cref="InvalidOperationException">不在 Executing 阶段，或当前索引未指向计划内的步骤。</exception>
    public AgentOrchestrationState BeginCurrentStep()
    {
        EnsurePhase(AgentOrchestrationPhase.Executing);
        if (Plan is null || CurrentStepIndex < 0 || CurrentStepIndex >= Plan.Steps.Count)
            throw new InvalidOperationException("No executable plan step is available.");
        return this with { CurrentAttempt = CurrentAttempt + 1, UpdatedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>按标识登记当前步骤已完成并将尝试次数归零；不要求事先登记尝试。</summary>
    /// <returns>尚有步骤时指向下一步并保持 Executing；否则保留最后一步索引并进入 Verifying。</returns>
    /// <exception cref="InvalidOperationException">不在 Executing 阶段，或当前索引未指向计划内的步骤。</exception>
    public AgentOrchestrationState CompleteCurrentStep()
    {
        EnsurePhase(AgentOrchestrationPhase.Executing);
        if (Plan is null || CurrentStepIndex < 0 || CurrentStepIndex >= Plan.Steps.Count)
            throw new InvalidOperationException("No executable plan step is available.");
        var completed = new HashSet<string>(CompletedSteps, StringComparer.Ordinal)
        {
            Plan.Steps[CurrentStepIndex].Id
        };
        bool last = CurrentStepIndex == Plan.Steps.Count - 1;
        return this with
        {
            Phase = last ? AgentOrchestrationPhase.Verifying : AgentOrchestrationPhase.Executing,
            CurrentStepIndex = last ? CurrentStepIndex : CurrentStepIndex + 1,
            CurrentAttempt = 0,
            CompletedSteps = completed,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>仅在 Verifying 阶段记录验证结果，并根据是否通过选择下一阶段。</summary>
    /// <param name="result">验证器的非 null 判断，以其 Passed 标志为准。</param>
    /// <returns>通过时进入 Completed；未通过时进入 Paused，暂停原因为 <c>VerificationFailed</c>。</returns>
    public AgentOrchestrationState ApplyVerification(AgentVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsurePhase(AgentOrchestrationPhase.Verifying);
        return result.Passed
            ? this with { Phase = AgentOrchestrationPhase.Completed, LastVerification = result, UpdatedAt = DateTimeOffset.UtcNow }
            : this with
            {
                Phase = AgentOrchestrationPhase.Paused,
                LastVerification = result,
                PauseReason = "VerificationFailed",
                UpdatedAt = DateTimeOffset.UtcNow
            };
    }

    /// <summary>暂停任一非终态的运行；再次暂停会替换原有原因。</summary>
    /// <param name="reason">记录到快照中的非空白暂停原因。</param>
    /// <returns>保留当前步骤及尝试次数的 Paused 快照。</returns>
    public AgentOrchestrationState Pause(string reason)
    {
        EnsureNotTerminal();
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A pause requires a reason.", nameof(reason));
        return this with { Phase = AgentOrchestrationPhase.Paused, PauseReason = reason, UpdatedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>将任一非终态的运行标记为失败，包括已暂停的运行。</summary>
    /// <param name="reason">记录到快照中的非空白失败原因。</param>
    /// <returns>不再接受后续状态转换的 Failed 快照。</returns>
    public AgentOrchestrationState Fail(string reason)
    {
        EnsureNotTerminal();
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A failure requires a reason.", nameof(reason));
        return this with { Phase = AgentOrchestrationPhase.Failed, FailureReason = reason, UpdatedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>取消任一非终态的运行；已经结束的运行不能再次取消。</summary>
    /// <returns>保留已有步骤进度和验证记录的 Cancelled 终态快照。</returns>
    public AgentOrchestrationState Cancel()
        => EnsureNotTerminal() with { Phase = AgentOrchestrationPhase.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };

    private AgentOrchestrationState RequirePhase(AgentOrchestrationPhase phase)
    {
        EnsurePhase(phase);
        return this;
    }

    private void EnsurePhase(AgentOrchestrationPhase phase)
    {
        if (Phase != phase) throw InvalidTransition(phase);
    }

    private AgentOrchestrationState EnsureNotTerminal()
    {
        if (Phase is AgentOrchestrationPhase.Completed or AgentOrchestrationPhase.Failed or AgentOrchestrationPhase.Cancelled)
            throw new InvalidOperationException($"The run is already terminal ({Phase}).");
        return this;
    }

    private InvalidOperationException InvalidTransition(AgentOrchestrationPhase expected)
        => new($"Cannot move Agent run from {Phase} to an operation requiring {expected}.");
}
