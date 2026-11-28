using VelaShell.Plugin.Ai.Core.Runtime;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class AgentOrchestrationStateTests
{
    [TestMethod]
    public void PlanLifecycle_SeparatesPlanningExecutionAndVerification()
    {
        AgentPlan plan = new("ship change", ["tests pass"],
        [
            new AgentPlanStep("locate", "locate files", ["src"], ["inspect"]),
            new AgentPlanStep("change", "make change", ["src"], ["build"])
        ], ["stop after verification"]);

        AgentOrchestrationState state = AgentOrchestrationState.Create("run-1")
            .StartPlanning()
            .AcceptPlan(plan)
            .BeginCurrentStep()
            .CompleteCurrentStep()
            .BeginCurrentStep()
            .CompleteCurrentStep();

        Assert.AreEqual(AgentOrchestrationPhase.Verifying, state.Phase);
        Assert.HasCount(2, state.CompletedSteps);
        Assert.AreEqual(0, state.CurrentAttempt);

        state = state.ApplyVerification(AgentVerificationResult.Pass(["dotnet test"]));

        Assert.AreEqual(AgentOrchestrationPhase.Completed, state.Phase);
        Assert.IsTrue(state.LastVerification!.Passed);
    }

    [TestMethod]
    public void FailedVerification_PausesForRecoveryWithoutLosingPlan()
    {
        AgentPlan plan = new("repair", ["clean"],
            [new AgentPlanStep("fix", "fix issue")], ["stop"]);
        AgentOrchestrationState state = AgentOrchestrationState.Create("run-2")
            .StartPlanning().AcceptPlan(plan).BeginCurrentStep().CompleteCurrentStep();

        state = state.ApplyVerification(AgentVerificationResult.Fail(
            [new AgentVerificationIssue("tests", "tests failed", "high")]));

        Assert.AreEqual(AgentOrchestrationPhase.Paused, state.Phase);
        Assert.AreEqual("VerificationFailed", state.PauseReason);
        Assert.AreEqual("fix", state.Plan!.Steps[0].Id);
    }

    [TestMethod]
    public void TerminalState_RejectsFurtherMutation()
    {
        AgentOrchestrationState state = AgentOrchestrationState.Create("run-3")
            .StartPlanning()
            .AcceptPlan(new AgentPlan("done", [], [new AgentPlanStep("one", "one")], []))
            .BeginCurrentStep()
            .CompleteCurrentStep()
            .ApplyVerification(AgentVerificationResult.Pass());

        Assert.ThrowsExactly<InvalidOperationException>(state.Cancel);
        Assert.ThrowsExactly<InvalidOperationException>(state.StartPlanning);
    }
}
