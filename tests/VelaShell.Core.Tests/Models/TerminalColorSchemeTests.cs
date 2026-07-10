using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

[TestClass]
public class TerminalColorSchemeTests
{
    [TestMethod]
    public void ApplyTo_ThenMatches_ShouldRoundTripForEveryBuiltInScheme()
    {
        foreach (var scheme in TerminalColorScheme.BuiltIn)
        {
            var appearance = new AppearanceOptions();
            scheme.ApplyTo(appearance);

            Assert.IsTrue(scheme.Matches(appearance), $"方案 {scheme.Name} 应用后应能反向匹配自身");

            // 反向匹配应唯一命中(内置方案两两不同)。
            var matched = TerminalColorScheme.BuiltIn.Count(s => s.Matches(appearance));
            Assert.AreEqual(1, matched, $"方案 {scheme.Name} 应恰好命中一个内置方案");
        }
    }

    [TestMethod]
    public void Matches_ShouldBeCaseInsensitiveOnHexValues()
    {
        var appearance = new AppearanceOptions();
        TerminalColorScheme.BuiltIn[1].ApplyTo(appearance);
        appearance.TerminalForeground = appearance.TerminalForeground.ToLowerInvariant();
        appearance.AnsiNormal = appearance.AnsiNormal.Select(c => c.ToLowerInvariant()).ToList();

        Assert.IsTrue(TerminalColorScheme.BuiltIn[1].Matches(appearance));
    }

    [TestMethod]
    public void Matches_ShouldFailWhenAnySingleColorIsCustomized()
    {
        var appearance = new AppearanceOptions();
        var scheme = TerminalColorScheme.BuiltIn[1];
        scheme.ApplyTo(appearance);
        appearance.AnsiBright[3] = "#123456";

        Assert.IsFalse(scheme.Matches(appearance), "改过单色后不应再匹配整套方案");
    }

    [TestMethod]
    public void Matches_DefaultAppearance_ShouldHitDraculaOnly()
    {
        // 出厂默认 AppearanceOptions 即 Dracula:设置页首次打开应选中首项而非空白。
        var appearance = new AppearanceOptions();

        Assert.IsTrue(TerminalColorScheme.BuiltIn[0].Matches(appearance));
        Assert.AreEqual(1, TerminalColorScheme.BuiltIn.Count(s => s.Matches(appearance)));
    }
}
