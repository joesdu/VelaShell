using VelaShell.Plugin.Ai.Chat.Skills;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>整个测试程序集只跑一次的准备。</summary>
[TestClass]
public static class TestAssemblySetup
{
    /// <summary>
    /// 用户级技能目录指到一个空的临时目录:开发机上真实的 <c>~/.velashell/skills</c> 不该混进任何用例的系统提示词。
    /// 需要技能的用例把技能放进本地工作区的 <c>.velashell/skills</c>,不改这个全局设置。
    /// </summary>
    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        string empty = Path.Combine(Path.GetTempPath(), "velashell-ai-tests-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        SkillCatalog.UserDirectoryOverride = empty;
    }
}
