using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// OSC 7(shell 上报当前工作目录 file://host/path)解析:驱动「文件浏览器跟随终端目录」。
/// 由 VelaShell 注入的 bash 提示符脚本发出;解析结果须为绝对路径,非法/非绝对一律不触发。
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public class Osc7WorkingDirectoryTests
{
    private static TerminalEmulator New() => new(20, 6, TerminalType.XtermColor256);

    private static string? CaptureCwd(string oscSequence)
    {
        TerminalEmulator e = New();
        string? captured = null;
        e.WorkingDirectoryChanged += path => captured = path;
        e.Feed(Encoding.UTF8.GetBytes(oscSequence));
        return captured;
    }

    [TestMethod]
    public void Osc7_WithHost_StTerminator_ExtractsPath() =>
        // ESC ] 7 ; file://host/root/temp ESC \
        Assert.AreEqual("/root/temp", CaptureCwd("\e]7;file://myhost/root/temp\e\\"));

    [TestMethod]
    public void Osc7_EmptyHost_ExtractsPath() => Assert.AreEqual("/var/log", CaptureCwd("\e]7;file:///var/log\e\\"));

    [TestMethod]
    public void Osc7_PercentEncoded_IsDecoded() => Assert.AreEqual("/a b/c", CaptureCwd("\e]7;file://h/a%20b/c\e\\"));

    [TestMethod]
    public void Osc7_NonFileScheme_DoesNotFire() => Assert.IsNull(CaptureCwd("\e]7;http://example.com/x\e\\"));

    [TestMethod]
    public void Osc7_MalformedNoPath_DoesNotFire() => Assert.IsNull(CaptureCwd("\e]7;file://host\e\\"));

    // ---- 别家的方言:我们自己只发 OSC 7,但认全部 ----
    // 为的是白捡已经为 VS Code / iTerm2 / Windows Terminal 配过 rc 的用户:
    // 他们什么都不用改,「跟随终端目录」就能工作。

    /// <summary>VS Code 的 shell 集成(electerm 等也在用):<c>OSC 633 ; P ; Cwd=&lt;路径&gt;</c>。</summary>
    [TestMethod]
    public void Osc633_Cwd_ExtractsPath() =>
        Assert.AreEqual("/srv/app", CaptureCwd("\e]633;P;Cwd=/srv/app\a"));

    /// <summary>
    /// 分号是 OSC 的字段分隔符,VS Code 因此把路径里的分号转义成 <c>\x3b</c> —— 要还原回去,
    /// 否则 <c>/srv/a;b</c> 会被截成 <c>/srv/a</c>。
    /// </summary>
    [TestMethod]
    public void Osc633_Cwd_UnescapesSemicolon() =>
        Assert.AreEqual("/srv/a;b", CaptureCwd("\e]633;P;Cwd=/srv/a\\x3bb\a"));

    /// <summary>发送方漏了转义、路径里带着裸分号时也要拼得回来(同 OSC 8 的 URI 处理)。</summary>
    [TestMethod]
    public void Osc633_Cwd_WithRawSemicolon_IsRejoined() =>
        Assert.AreEqual("/srv/a;b", CaptureCwd("\e]633;P;Cwd=/srv/a;b\a"));

    /// <summary>633 的 <c>A/B/C/D</c> 与 OSC 133 同义,不该被误读成目录。</summary>
    [TestMethod]
    public void Osc633_PromptMarks_DoNotFireCwd() => Assert.IsNull(CaptureCwd("\e]633;A\a\e]633;C\a\e]633;D;0\a"));

    /// <summary>iTerm2 的 shell 集成:<c>OSC 1337 ; CurrentDir=&lt;路径&gt;</c>。</summary>
    [TestMethod]
    public void Osc1337_CurrentDir_ExtractsPath() =>
        Assert.AreEqual("/home/slime", CaptureCwd("\e]1337;CurrentDir=/home/slime\a"));

    /// <summary>1337 还驮着一堆别的键(RemoteHost、SetMark…),不是 CurrentDir 的一律不管。</summary>
    [TestMethod]
    public void Osc1337_OtherKeys_DoNotFire() =>
        Assert.IsNull(CaptureCwd("\e]1337;RemoteHost=root@myhost\a"));

    /// <summary>ConEmu / Windows Terminal 的 <c>OSC 9 ; 9 ; &lt;路径&gt;</c>。</summary>
    [TestMethod]
    public void Osc9_9_ExtractsPath() => Assert.AreEqual("/opt/data", CaptureCwd("\e]9;9;/opt/data\a"));

    /// <summary>这条方言本就出自 Windows,盘符路径必须认。</summary>
    [TestMethod]
    public void Osc9_9_AcceptsWindowsDrivePath() =>
        Assert.AreEqual(@"C:\Users\slime", CaptureCwd("\e]9;9;C:\\Users\\slime\a"));

    /// <summary>
    /// 裸的 <c>OSC 9</c> 是 iTerm2 的桌面通知,和工作目录毫无关系 ——
    /// 把通知正文当路径去跳转,文件浏览器会当场跳到一个不存在的地方。
    /// </summary>
    [TestMethod]
    public void Osc9_Notification_DoesNotFire() => Assert.IsNull(CaptureCwd("\e]9;构建完成\a"));

    /// <summary>相对路径一律不认:跳过去只会跳错地方,不如不动。</summary>
    [TestMethod]
    public void PlainPath_WhenRelative_DoesNotFire() => Assert.IsNull(CaptureCwd("\e]633;P;Cwd=srv/app\a"));
}
