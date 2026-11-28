using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Agent.Policy;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class AgentPolicyTests
{
    private static readonly AgentPolicyEvaluator Evaluator = new();

    private static AgentPolicyRequest SafeRequest()
        => new("read_terminal", "read terminal", AgentSideEffect.None, isKnownSafe: true);

    private static AgentPolicyRequest UnsafeRequest()
        => new("write_remote_file", "write /etc/example", AgentSideEffect.Possible, isKnownSafe: false);

    [TestMethod]
    public void AskMode_RequiresApprovalForSideEffects() => AssertRequiresApproval(AgentApprovalMode.Ask);

    [TestMethod]
    public void ReadOnlyAuto_RequiresApprovalForSideEffects() => AssertRequiresApproval(AgentApprovalMode.ReadOnlyAuto);

    private static void AssertRequiresApproval(AgentApprovalMode mode)
    {
        AgentPolicyDecision result = Evaluator.Evaluate(UnsafeRequest(), mode);

        Assert.AreEqual(AgentPolicyDecisionKind.ApprovalRequired, result.Kind);
        Assert.IsTrue(result.RequiresApproval);
        Assert.IsFalse(result.CanExecute);
    }

    [TestMethod]
    public void ReadOnlyAuto_AllowsOnlyKnownSideEffectFreeOperations()
    {
        AgentPolicyDecision safe = Evaluator.Evaluate(SafeRequest(), AgentApprovalMode.ReadOnlyAuto);
        AgentPolicyDecision uncertain = Evaluator.Evaluate(
            new AgentPolicyRequest("inspect", "inspect", AgentSideEffect.None, isKnownSafe: false),
            AgentApprovalMode.ReadOnlyAuto);
        AgentPolicyDecision possible = Evaluator.Evaluate(UnsafeRequest(), AgentApprovalMode.ReadOnlyAuto);

        Assert.AreEqual(AgentPolicyDecisionKind.Allowed, safe.Kind);
        Assert.AreEqual(AgentPolicyDecisionKind.ApprovalRequired, uncertain.Kind);
        Assert.AreEqual(AgentPolicyDecisionKind.ApprovalRequired, possible.Kind);
    }

    [TestMethod]
    public void Bypass_AllowsPluginPolicyButDoesNotMakeRetrySafe()
    {
        AgentPolicyDecision allowed = Evaluator.Evaluate(UnsafeRequest(), AgentApprovalMode.Bypass);
        AgentRetryDecision retry = Evaluator.ClassifyRetry(new AgentRetryRequest(
            UnsafeRequest(), allowed, AgentPolicyFailureKind.Transient,
            executionStarted: false, outcomeKnown: true));

        Assert.AreEqual(AgentPolicyDecisionKind.Allowed, allowed.Kind);
        Assert.IsFalse(retry.IsSafe, "Bypass is approval policy, not proof that a side effect is safe to replay.");
    }

    [TestMethod]
    public async Task Gate_UsesApprovalPortAndReturnsImmutableDecisions()
    {
        AgentPolicyRequest request = UnsafeRequest();
        var asked = new List<AgentPolicyRequest>();
        var gate = new AgentPolicyGate(AgentApprovalMode.Ask, approvalPort: new CallbackApprovalPort(
            (value, _) =>
            {
                asked.Add(value);
                return ValueTask.FromResult(true);
            }));

        AgentPolicyDecision result = await gate.AuthorizeAsync(request);

        Assert.AreEqual(AgentPolicyDecisionKind.Allowed, result.Kind);
        Assert.AreEqual(request, asked.Single());
        Assert.IsFalse(result.RequiresApproval);
    }

    [TestMethod]
    public async Task MissingOrNegativeApproval_IsDeniedAndNeverAllowed()
    {
        AgentPolicyRequest request = UnsafeRequest();
        AgentPolicyDecision missing = await new AgentPolicyGate(AgentApprovalMode.Ask).AuthorizeAsync(request);
        AgentPolicyDecision rejected = await new AgentPolicyGate(AgentApprovalMode.Ask,
            approvalPort: new CallbackApprovalPort((_, _) => ValueTask.FromResult(false))).AuthorizeAsync(request);

        Assert.AreEqual(AgentPolicyDecisionKind.ApprovalDenied, missing.Kind);
        Assert.AreEqual(AgentPolicyDecisionKind.ApprovalDenied, rejected.Kind);
        Assert.IsFalse(missing.CanExecute);
        Assert.IsFalse(rejected.CanExecute);
    }

    [TestMethod]
    public async Task Adapter_MapsExistingApprovalRequestAndRunState()
    {
        ApprovalRequest? captured = null;
        bool denied = false;
        bool unknown = false;
        var adapter = new AgentPolicyAdapter(ApprovalMode.Ask,
            approvalHandler: request =>
            {
                captured = request;
                return Task.FromResult(true);
            },
            hasDeniedExecution: () => denied,
            hasUnknownOutcome: () => unknown,
            markExecutionDenied: () => denied = true);
        AgentPolicyRequest request = new("run_command", "df -h", AgentSideEffect.Possible,
            isKnownSafe: false, repeatKey: "df", origin: "child · analyst");

        AgentPolicyDecision allowed = await adapter.AuthorizeAsync(request);
        denied = true;
        AgentPolicyDecision deniedResult = await adapter.AuthorizeAsync(request);
        denied = false;
        unknown = true;
        AgentPolicyDecision unknownResult = await adapter.AuthorizeAsync(request);

        Assert.AreEqual(AgentPolicyDecisionKind.Allowed, allowed.Kind);
        Assert.AreEqual(new ApprovalRequest("run_command", "df -h", "df", "child · analyst"), captured);
        Assert.AreEqual(AgentPolicyDecisionKind.ExecutionDenied, deniedResult.Kind);
        Assert.AreEqual(AgentPolicyDecisionKind.UnknownOutcome, unknownResult.Kind);
    }

    [TestMethod]
    public async Task Adapter_DenialMarksRunWhenTheExistingStateProvidesMarker()
    {
        bool denied = false;
        var adapter = new AgentPolicyAdapter(ApprovalMode.Ask,
            approvalHandler: _ => Task.FromResult(false),
            hasDeniedExecution: () => denied,
            markExecutionDenied: () => denied = true);

        AgentPolicyDecision result = await adapter.AuthorizeAsync(UnsafeRequest());

        Assert.AreEqual(AgentPolicyDecisionKind.ExecutionDenied, result.Kind);
        Assert.IsTrue(denied);
        Assert.IsTrue(result.StopsRun);
    }

    [TestMethod]
    public void Retry_IsSafeOnlyForKnownSafeTransientWorkBeforeExecution()
    {
        AgentPolicyDecision allowed = Evaluator.Evaluate(SafeRequest(), AgentApprovalMode.ReadOnlyAuto);
        AgentRetryDecision safe = Evaluator.ClassifyRetry(new AgentRetryRequest(
            SafeRequest(), allowed, AgentPolicyFailureKind.Transient,
            executionStarted: false, outcomeKnown: true));
        AgentRetryDecision started = Evaluator.ClassifyRetry(new AgentRetryRequest(
            SafeRequest(), allowed, AgentPolicyFailureKind.Transient,
            executionStarted: true, outcomeKnown: true));
        AgentRetryDecision unknown = Evaluator.ClassifyRetry(new AgentRetryRequest(
            SafeRequest(), AgentPolicyDecision.UnknownOutcome(), AgentPolicyFailureKind.Transient,
            executionStarted: false, outcomeKnown: false));

        Assert.IsTrue(safe.IsSafe);
        Assert.AreEqual("transient-before-execution", safe.Reason);
        Assert.IsFalse(started.IsSafe);
        Assert.AreEqual("execution-started", started.Reason);
        Assert.IsFalse(unknown.IsSafe);
        Assert.AreEqual("unknown-outcome", unknown.Reason);
    }

    [TestMethod]
    public void ApprovalDenied_IsNeverRetryable() => AssertNeverRetryable(AgentPolicyDecision.ApprovalDenied());

    [TestMethod]
    public void ExecutionDenied_IsNeverRetryable() => AssertNeverRetryable(AgentPolicyDecision.ExecutionDenied());

    [TestMethod]
    public void UnknownOutcome_IsNeverRetryable() => AssertNeverRetryable(AgentPolicyDecision.UnknownOutcome());

    [TestMethod]
    public void ApprovalRequired_IsNeverRetryable() => AssertNeverRetryable(AgentPolicyDecision.ApprovalRequired());

    private static void AssertNeverRetryable(AgentPolicyDecision decision)
    {
        AgentRetryDecision result = Evaluator.ClassifyRetry(new AgentRetryRequest(
            SafeRequest(), decision, AgentPolicyFailureKind.Transient,
            executionStarted: false, outcomeKnown: true));

        Assert.IsFalse(result.IsSafe);
        Assert.IsFalse(decision.CanExecute);
    }

    private sealed class CallbackApprovalPort(
        Func<AgentPolicyRequest, CancellationToken, ValueTask<bool>> callback) : IAgentPolicyApprovalPort
    {
        public ValueTask<bool> RequestApprovalAsync(AgentPolicyRequest request, CancellationToken cancellationToken)
            => callback(request, cancellationToken);
    }
}
