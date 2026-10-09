using System.Reflection;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 启动预热只装插件 ALC 真装得到的程序集。CSharpMath.Rendering 在元数据里引着 CSharpMath.Editor,
/// 包里却没带 —— 照着引用硬装,每次启动都会白抛一个 FileNotFoundException 并记一条警告。
/// </summary>
[TestClass]
public sealed class PanelPreloadTests
{
    private static readonly HashSet<string> Platform = new(StringComparer.OrdinalIgnoreCase) { "System.Runtime", "Avalonia.Base" };

    private static string? Private(AssemblyName name)
        => name.Name == "CSharpMath.Rendering" ? @"plugins\velashell-ai\CSharpMath.Rendering.dll" : null;

    [TestMethod]
    public void MetadataOnlyReference_IsSkipped()
        => Assert.IsFalse(AiPlugin.CanResolve(new AssemblyName("CSharpMath.Editor, Version=1.0.0.0"), Private, Platform));

    [TestMethod]
    [DataRow("CSharpMath.Rendering")]
    [DataRow("System.Runtime")]
    [DataRow("avalonia.base")]
    public void PluginOrHostAssembly_IsPreloaded(string name)
        => Assert.IsTrue(AiPlugin.CanResolve(new AssemblyName(name), Private, Platform));

    [TestMethod]
    public void WithoutPlatformList_FallsBackToTryingEverything()
        => Assert.IsTrue(AiPlugin.CanResolve(new AssemblyName("CSharpMath.Editor"), Private, null));
}
