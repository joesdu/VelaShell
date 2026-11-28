using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Chat.Runtime;
using VelaShell.Plugin.Ai.Core.Runtime;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class AgentRuntimeSessionTests
{
    [TestMethod]
    public void Session_ProjectsLifecycleIntoStateAndEvents()
    {
        var events = new AgentRunEventLog();
        var session = new AgentRuntimeSession("run-1", events);
        AgentPlan plan = new("deliver", ["verified"],
            [new AgentPlanStep("change", "make change", ["file.cs"], ["test"])], ["stop"]);

        session.StartPlanning();
        session.AcceptPlan(plan);
        session.BeginCurrentStep();
        session.CompleteCurrentStep();
        session.BeginVerification();
        session.ApplyVerification(AgentVerificationResult.Pass(["test"]));

        Assert.AreEqual(AgentOrchestrationPhase.Completed, session.State.Phase);
        Assert.Contains(e => e.Kind == AgentRunEventKind.PlanStarted, events.Snapshot);
        Assert.Contains(e => e.Kind == AgentRunEventKind.VerificationCompleted, events.Snapshot);
        Assert.Contains(e => e.Kind == AgentRunEventKind.RunCompleted, events.Snapshot);
    }

    [TestMethod]
    public void Session_RejectsVerificationBeforePlanCompletes()
    {
        var session = new AgentRuntimeSession("run-2");
        session.StartPlanning();
        session.AcceptPlan(new AgentPlan("deliver", [],
            [new AgentPlanStep("change", "make change")], []));

        Assert.ThrowsExactly<InvalidOperationException>(session.BeginVerification);
        Assert.AreEqual(AgentOrchestrationPhase.Executing, session.State.Phase);
    }
}
