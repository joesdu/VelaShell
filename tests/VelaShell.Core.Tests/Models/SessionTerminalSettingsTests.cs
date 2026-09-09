using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 「全局设置 + 会话级覆盖」合成生效值的规则。
/// </summary>
/// <remarks>
/// 规则本身只有一条(覆盖项不为空就用它),但调用点有五六处 —— 建标签、握手、重连、
/// 插件终端、设置热更新。各写一遍 <c>profile.Terminal?.X ?? settings.Y</c> 的结果必然是
/// 某一处漏了,表现成"覆盖在新建标签时生效、重连之后又变回全局",而那种 bug 没人会往这里想。
/// </remarks>
[TestClass]
public sealed class SessionTerminalSettingsTests
{
    private static AppSettings Global() =>
        new() { TerminalType = "xterm-256color", TerminalEncoding = "UTF-8" };

    [TestMethod]
    public void WithoutOverridesEverythingFollowsTheGlobalSettings()
    {
        SessionProfile profile = new() { Name = "plain" };
        AppSettings settings = Global();

        Assert.AreEqual("xterm-256color", SessionTerminalSettings.TerminalType(profile, settings));
        Assert.AreEqual("UTF-8", SessionTerminalSettings.Encoding(profile, settings));
        Assert.AreEqual(settings.General.KeepAliveSeconds, SessionTerminalSettings.KeepAliveSeconds(profile, settings));
        Assert.IsNull(SessionTerminalSettings.ColorScheme(profile));
        Assert.IsNull(SessionTerminalSettings.TabColor(profile));
        Assert.IsNull(SessionTerminalSettings.StartupDirectory(profile));
    }

    [TestMethod]
    public void AnOverrideWins()
    {
        // 覆盖需求最强的一项:同一个人同时连 UTF-8 的容器和 GBK 的老服务器,
        // 全局只能配一个,另一边就是满屏乱码。
        SessionProfile profile = new()
        {
            Terminal = new() { Encoding = "GBK", TerminalType = "vt220", KeepAliveSeconds = 15 }
        };

        Assert.AreEqual("GBK", SessionTerminalSettings.Encoding(profile, Global()));
        Assert.AreEqual("vt220", SessionTerminalSettings.TerminalType(profile, Global()));
        Assert.AreEqual(15, SessionTerminalSettings.KeepAliveSeconds(profile, Global()));
    }

    [TestMethod]
    public void ANullProfileFallsBackToGlobal()
    {
        // 本地终端、插件借用的终端视图都没有会话配置,那时整套覆盖自然不适用 ——
        // 不能因此抛,那几条路径都在启动/连接的热路上。
        Assert.AreEqual("xterm-256color", SessionTerminalSettings.TerminalType(null, Global()));
        Assert.AreEqual("UTF-8", SessionTerminalSettings.Encoding(null, Global()));
        Assert.IsNull(SessionTerminalSettings.ColorScheme(null));
    }

    [TestMethod]
    public void AnEmptyOverrideIsNotAnOverride()
    {
        // 界面把某一栏清空之后存下来的是空串,不是 null(下拉/输入框都可能这样)。
        // 空串当作"没覆盖",否则终端会拿一个空编码名去解析。
        SessionProfile profile = new()
        {
            Terminal = new() { Encoding = "   ", TerminalType = "", TabColor = "  " }
        };

        Assert.AreEqual("UTF-8", SessionTerminalSettings.Encoding(profile, Global()));
        Assert.AreEqual("xterm-256color", SessionTerminalSettings.TerminalType(profile, Global()));
        Assert.IsNull(SessionTerminalSettings.TabColor(profile));
    }

    [TestMethod]
    public void ABrokenGlobalEncodingStillYieldsSomethingUsable()
    {
        // 全局那一项本身也可能是空(旧配置、手改坏的文件)。兜到 UTF-8,
        // 而不是让下游的编码解析抛在连接路径上。
        AppSettings broken = new() { TerminalEncoding = "" };

        Assert.AreEqual("UTF-8", SessionTerminalSettings.Encoding(new SessionProfile(), broken));
    }

    [TestMethod]
    public void AnUnknownColorSchemeIsTreatedAsNoOverride()
    {
        // 用新版选过某个方案再退回旧版就是这个情形:那时应当照常显示全局配色,
        // 而不是空白一片。
        SessionProfile profile = new() { Terminal = new() { ColorScheme = "方案名不存在" } };

        Assert.IsNull(SessionTerminalSettings.ColorScheme(profile));
    }

    [TestMethod]
    public void AKnownColorSchemeResolvesToTheBuiltInOne()
    {
        string name = TerminalColorScheme.BuiltIn[1].Name;
        SessionProfile profile = new() { Terminal = new() { ColorScheme = name } };

        Assert.AreEqual(name, SessionTerminalSettings.ColorScheme(profile)?.Name);
    }

    [TestMethod]
    public void KeepAliveIsClampedAtTheSameCeilingAsTheGlobalOne()
    {
        // 配置文件是可以手改的,一个 99999 等于悄悄关掉了保活 —— 而用户完全无从知道。
        TerminalOverrides overrides = new() { KeepAliveSeconds = 99_999 };
        Assert.AreEqual(TerminalOverrides.MaxKeepAliveSeconds, overrides.KeepAliveSeconds);

        overrides.KeepAliveSeconds = -5;
        Assert.AreEqual(0, overrides.KeepAliveSeconds, "负数没有意义,归零(= 关闭)。");

        overrides.KeepAliveSeconds = null;
        Assert.IsNull(overrides.KeepAliveSeconds, "null 是「跟随全局」,不该被钳成 0。");
    }

    [TestMethod]
    public void AnAllEmptyOverrideObjectReportsItself()
    {
        // 界面把六项都清空之后应当存回 null 而不是一个全空对象:后者会让"有没有覆盖"
        // 这件事有了两种表示,也给每条老配置的落盘 JSON 平白多出一段。
        Assert.IsTrue(new TerminalOverrides().IsEmpty);
        Assert.IsTrue(new TerminalOverrides { Encoding = "  ", TabColor = "" }.IsEmpty);
        Assert.IsFalse(new TerminalOverrides { KeepAliveSeconds = 0 }.IsEmpty,
            "显式设成 0(关闭保活)是一次真实的覆盖,不是「没设」。");
    }

    /// <summary>
    /// agent 转发的三态解析:配置里表过态就听它,没表态才跟随全局。
    /// </summary>
    /// <remarks>
    /// 这条比其它覆盖项要紧一档:它是安全开关。把「显式关掉」误判成「跟随全局」,
    /// 用户在某台机器上明确关掉的转发就会因为改了一次全局默认而被悄悄打开 ——
    /// 那正是三态存在的全部理由。
    /// </remarks>
    [TestMethod]
    [DataRow(null, false, false)]
    [DataRow(null, true, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    public void AgentForwardingFollowsTheProfileFirstThenTheGlobalDefault(
        bool? perProfile, bool globalDefault, bool expected)
    {
        SessionProfile profile = new() { AgentForwarding = perProfile };
        AppSettings settings = new();
        settings.Keys.AgentForwardingEnabled = globalDefault;

        Assert.AreEqual(expected, SessionTerminalSettings.AgentForwarding(profile, settings));
    }

    /// <summary>
    /// 没有会话配置(本地终端、插件借用的终端)时一律不转发,哪怕全局默认是开的:
    /// 那些路径上根本没有 SSH 连接可转发,跟随全局只会平白多一次注定失败的尝试。
    /// </summary>
    [TestMethod]
    public void AgentForwardingIsOffWhenThereIsNoProfile()
    {
        AppSettings settings = new();
        settings.Keys.AgentForwardingEnabled = true;

        Assert.IsFalse(SessionTerminalSettings.AgentForwarding(null, settings));
    }

    /// <summary>默认必须是关的。转发把私钥的使用权交给远端,这个默认值不该被顺手改掉。</summary>
    [TestMethod]
    public void AgentForwardingDefaultsToOff()
    {
        Assert.IsFalse(new AppSettings().Keys.AgentForwardingEnabled);
        Assert.IsNull(new SessionProfile().AgentForwarding, "新配置不表态 = 跟随全局");
        Assert.IsTrue(new AppSettings().Keys.AuditAgentSignRequests, "签名审计默认开:那是这项能力唯一的证据");
    }
}
