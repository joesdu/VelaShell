namespace VelaShell.Plugin.Ai.Agent.Policy;

/// <summary>Approval modes understood by the host-neutral policy layer.</summary>
public enum AgentApprovalMode
{
    /// <summary>Ask for approval for every operation that is not automatically safe.</summary>
    Ask,

    /// <summary>Automatically allow only operations proven to be side-effect free.</summary>
    ReadOnlyAuto,

    /// <summary>Allow plugin-level operations without an approval callback.</summary>
    Bypass
}

/// <summary>How much state an operation may change.</summary>
public enum AgentSideEffect
{
    /// <summary>The operation is known not to change state.</summary>
    None,

    /// <summary>The operation may change state.</summary>
    Possible,

    /// <summary>The operation's side effects cannot be established.</summary>
    Unknown
}

/// <summary>The result of evaluating an operation against the current policy.</summary>
public enum AgentPolicyDecisionKind
{
    /// <summary>本次操作已通过策略检查，可以开始执行。</summary>
    Allowed,
    /// <summary>执行前还须取得批准。</summary>
    ApprovalRequired,
    /// <summary>本次操作未获批准，不得执行。</summary>
    ApprovalDenied,
    /// <summary>当前运行已被禁止继续执行，应停止本轮。</summary>
    ExecutionDenied,
    /// <summary>先前操作的结果无法确认，应停止本轮以免重复产生副作用。</summary>
    UnknownOutcome
}

/// <summary>The failure categories used for conservative automatic retry decisions.</summary>
public enum AgentPolicyFailureKind
{
    /// <summary>故障可能自行消失，能否重试还取决于执行状态和副作用。</summary>
    Transient,
    /// <summary>预计重复同一请求仍会失败，不应自动重试。</summary>
    Permanent,
    /// <summary>操作被取消，不应将取消视为可重试的暂时性故障。</summary>
    Cancelled,
    /// <summary>操作未获批准，不能通过重试绕过审批。</summary>
    ApprovalDenied,
    /// <summary>当前运行已禁止执行，不能在本轮重试。</summary>
    ExecutionDenied,
    /// <summary>无法确定上次尝试的结果，重试可能重复已发生的操作。</summary>
    UnknownOutcome
}

/// <summary>Whether an operation can be retried without risking a duplicate side effect.</summary>
public enum AgentRetryClassification
{
    /// <summary>策略已确认可以安全地自动重试。</summary>
    Safe,
    /// <summary>策略未确认重试安全，不应自动再次执行。</summary>
    Unsafe
}

/// <summary>Immutable input to the policy evaluator.</summary>
public sealed record AgentPolicyRequest
{
    /// <summary>Creates an operation request without retaining mutable host state.</summary>
    public AgentPolicyRequest(string operation, string detail, AgentSideEffect sideEffect,
        bool isKnownSafe, string? repeatKey = null, string? origin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(detail);
        Operation = operation;
        Detail = detail;
        SideEffect = sideEffect;
        IsKnownSafe = isKnownSafe;
        RepeatKey = repeatKey;
        Origin = origin;
    }

    /// <summary>请求执行的操作名称，例如工具名。</summary>
    public string Operation { get; }
    /// <summary>供审批者核对的操作详情，例如命令原文或目标路径。</summary>
    public string Detail { get; }
    /// <summary>该操作是否可能改变状态；无法确定时与明确无副作用区分处理。</summary>
    public AgentSideEffect SideEffect { get; }
    /// <summary>调用方是否已确认操作安全；只读自动放行还要求操作无副作用。</summary>
    public bool IsKnownSafe { get; }
    /// <summary>用于在本次会话记住同类操作批准的键；为空引用时不提供“总是允许”选项。</summary>
    public string? RepeatKey { get; }
    /// <summary>供审批者辨认发起操作的子 Agent；主 Agent 发起时为 <see langword="null"/>。</summary>
    public string? Origin { get; }
}

/// <summary>Immutable input to retry classification.</summary>
public sealed record AgentRetryRequest
{
    /// <summary>Creates retry input with explicit execution and outcome evidence.</summary>
    public AgentRetryRequest(AgentPolicyRequest operation, AgentPolicyDecision decision,
        AgentPolicyFailureKind failure, bool executionStarted, bool outcomeKnown)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(decision);
        Operation = operation;
        Decision = decision;
        Failure = failure;
        ExecutionStarted = executionStarted;
        OutcomeKnown = outcomeKnown;
    }

    /// <summary>待重试的原始请求，包含判断重复执行风险所需的副作用信息。</summary>
    public AgentPolicyRequest Operation { get; }
    /// <summary>上次尝试的策略决定，用于确认该操作是否曾获准执行。</summary>
    public AgentPolicyDecision Decision { get; }
    /// <summary>上次尝试失败的类别，用于区分暂时性故障与拒绝、取消等情况。</summary>
    public AgentPolicyFailureKind Failure { get; }
    /// <summary>上次尝试是否已开始执行，而不只是完成了授权。</summary>
    public bool ExecutionStarted { get; }
    /// <summary>上次尝试的结果是否已确认；结果不明时不能安全地自动重试。</summary>
    public bool OutcomeKnown { get; }
}

/// <summary>Immutable policy result. Instances can only be created with a valid decision kind.</summary>
public sealed record AgentPolicyDecision
{
    private AgentPolicyDecision(AgentPolicyDecisionKind kind, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Kind = kind;
        Reason = reason;
    }

    /// <summary>本次策略决定的类别，决定是否可执行、需审批或应停止运行。</summary>
    public AgentPolicyDecisionKind Kind { get; }
    /// <summary>说明为何作出此决定的非空白原因，例如 <c>approval-unavailable</c>。</summary>
    public string Reason { get; }
    /// <summary>仅当决定为 <see cref="AgentPolicyDecisionKind.Allowed"/> 时允许执行。</summary>
    public bool CanExecute => Kind == AgentPolicyDecisionKind.Allowed;
    /// <summary>是否仍需取得审批答复才能执行操作。</summary>
    public bool RequiresApproval => Kind == AgentPolicyDecisionKind.ApprovalRequired;
    /// <summary>是否应停止本轮运行；仅执行被禁止或先前操作结果不明时为真。</summary>
    public bool StopsRun => Kind is AgentPolicyDecisionKind.ExecutionDenied or AgentPolicyDecisionKind.UnknownOutcome;

    /// <summary>创建允许执行本次操作的决定，并记录放行原因。</summary>
    public static AgentPolicyDecision Allowed(string reason = "allowed")
        => new(AgentPolicyDecisionKind.Allowed, reason);

    /// <summary>创建等待审批的决定；本方法不会发起审批。</summary>
    public static AgentPolicyDecision ApprovalRequired(string reason = "approval-required")
        => new(AgentPolicyDecisionKind.ApprovalRequired, reason);

    /// <summary>创建本次操作未获批准的决定，区别于禁止整轮继续执行。</summary>
    public static AgentPolicyDecision ApprovalDenied(string reason = "approval-denied")
        => new(AgentPolicyDecisionKind.ApprovalDenied, reason);

    /// <summary>创建禁止继续执行的决定，通知调用方停止本轮运行。</summary>
    public static AgentPolicyDecision ExecutionDenied(string reason = "execution-denied")
        => new(AgentPolicyDecisionKind.ExecutionDenied, reason);

    /// <summary>创建因先前操作结果不明而应停止本轮运行的决定。</summary>
    public static AgentPolicyDecision UnknownOutcome(string reason = "unknown-outcome")
        => new(AgentPolicyDecisionKind.UnknownOutcome, reason);
}

/// <summary>Immutable retry result.</summary>
public sealed record AgentRetryDecision
{
    private AgentRetryDecision(AgentRetryClassification classification, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Classification = classification;
        Reason = reason;
    }

    /// <summary>策略对再次执行该操作是否安全的判定。</summary>
    public AgentRetryClassification Classification { get; }
    /// <summary>说明为何可以或不应自动重试的非空白原因。</summary>
    public string Reason { get; }
    /// <summary>仅当策略明确判定重试安全时为真。</summary>
    public bool IsSafe => Classification == AgentRetryClassification.Safe;

    /// <summary>创建可安全重试的判定；默认原因为执行开始前发生暂时性故障。</summary>
    public static AgentRetryDecision Safe(string reason = "transient-before-execution")
        => new(AgentRetryClassification.Safe, reason);

    /// <summary>创建不应自动重试的判定，并记录阻止重试的原因。</summary>
    public static AgentRetryDecision Unsafe(string reason)
        => new(AgentRetryClassification.Unsafe, reason);
}

/// <summary>Pure evaluation port used by hosts and adapters.</summary>
public interface IAgentPolicyEvaluator
{
    /// <summary>根据请求的安全信息和审批模式决定是否放行，不发起审批或执行操作。</summary>
    AgentPolicyDecision Evaluate(AgentPolicyRequest request, AgentApprovalMode approvalMode);
    /// <summary>根据原授权、故障类别、执行进度和已知结果判断自动重试是否安全；不执行重试。</summary>
    AgentRetryDecision ClassifyRetry(AgentRetryRequest request);
}

/// <summary>Approval callback port; it has no authority to change the policy or retry an operation.</summary>
public interface IAgentPolicyApprovalPort
{
    /// <summary>向审批者询问是否允许本次操作，返回审批答复而不执行操作。</summary>
    /// <param name="request">包含操作名称、详情和来源的审批请求。</param>
    /// <param name="cancellationToken">用于取消等待审批的令牌。</param>
    /// <returns>审批通过时为真，未通过时为假。</returns>
    ValueTask<bool> RequestApprovalAsync(AgentPolicyRequest request, CancellationToken cancellationToken);
}

/// <summary>Asynchronous policy gate suitable for an execution host.</summary>
public interface IAgentPolicyGate
{
    /// <summary>Evaluates the request without invoking an approval callback.</summary>
    AgentPolicyDecision Preflight(AgentPolicyRequest request);

    /// <summary>预检请求，仅在策略要求时等待审批；返回是否允许执行的决定，不启动操作。</summary>
    /// <param name="request">待授权的操作及其安全信息。</param>
    /// <param name="cancellationToken">用于取消等待审批的令牌。</param>
    /// <returns>供执行方检查的策略决定；只有明确放行后才可执行。</returns>
    ValueTask<AgentPolicyDecision> AuthorizeAsync(AgentPolicyRequest request, CancellationToken cancellationToken = default);
    /// <inheritdoc cref="IAgentPolicyEvaluator.ClassifyRetry"/>
    AgentRetryDecision ClassifyRetry(AgentRetryRequest request);
}
