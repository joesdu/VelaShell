using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>系统提示词分段组合:顺序、空段跳过、按本轮工具追加委派 / 团队指引,拼接结果与原先逐段相加逐字相同。</summary>
[TestClass]
public sealed class SystemPromptBuilderTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    [TestMethod]
    public void Build_ConcatenatesSectionsInOrder_ExactlyLikeTheOldStringSum()
    {
        AITool[] tools = [Tool("delegate_subagent"), Tool("team_run")];
        var builder = new SystemPromptBuilder()
            .Add(SystemPromptBuilder.Base, "You are the assistant.")
            .AddToolGuidance(tools)
            .Add(SystemPromptBuilder.Images, "\nImages.\n");

        CollectionAssert.AreEqual(new[] { SystemPromptBuilder.Base, SystemPromptBuilder.Delegation, SystemPromptBuilder.Team, SystemPromptBuilder.Images },
            builder.SectionIds.ToArray());
        Assert.AreEqual("You are the assistant." + SubagentRunner.DelegationGuidance + SubagentRunner.TeamGuidance + "\nImages.\n",
            builder.Build(), "分段之间不加分隔,与以前逐段相加的结果逐字相同");
    }

    [TestMethod]
    public void AddToolGuidance_OnlyForToolsActuallyExposed()
    {
        Assert.IsEmpty(new SystemPromptBuilder().AddToolGuidance(null).SectionIds);
        Assert.IsEmpty(new SystemPromptBuilder().AddToolGuidance([Tool("list_sessions")]).SectionIds);
        CollectionAssert.AreEqual(new[] { SystemPromptBuilder.Delegation },
            new SystemPromptBuilder().AddToolGuidance([Tool("delegate_subagent")]).SectionIds.ToArray(),
            "停用团队工具时不说团队");
    }

    [TestMethod]
    public void Add_SkipsEmptySections()
    {
        var builder = new SystemPromptBuilder()
            .Add(SystemPromptBuilder.Base, "base")
            .Add(SystemPromptBuilder.Images, null)
            .Add("extra", "");
        CollectionAssert.AreEqual(new[] { SystemPromptBuilder.Base }, builder.SectionIds.ToArray());
        Assert.AreEqual("base", builder.Build());
    }
}
