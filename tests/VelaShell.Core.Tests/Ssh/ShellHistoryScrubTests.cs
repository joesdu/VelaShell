using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 摘历史那段代码的<b>拼装</b>规则。shell 语义由真 bash 的
/// <c>ShellHistoryScrubShellTests</c> 验,这里只钉住"拼出来的东西长什么样"。
/// </summary>
[TestClass]
public sealed class ShellHistoryScrubTests
{
    /// <summary>
    /// 摘历史必须接在<b>前面</b>。
    /// </summary>
    /// <remarks>
    /// 缀在后面的话,这一行的退出码就成了收尾动作的 0,而不是用户那条命令自己的 ——
    /// starship / powerlevel10k 会把上一条的退出码画在提示符上,一条失败的
    /// 「认证后执行命令」于是变得毫无痕迹。
    /// </remarks>
    [TestMethod]
    public void TheScrubGoesInFront_SoTheCommandStillDecidesTheExitStatus()
    {
        string line = ShellHistoryScrub.Prepend("cd '/var/log'");

        Assert.StartsWith(ShellHistoryScrub.Command, line);
        Assert.EndsWith("; cd '/var/log'", line);
    }

    /// <summary>没有命令就没有历史要摘:不能凭空多发一行(那本身就会在历史里留一条)。</summary>
    [TestMethod]
    public void WithoutACommand_NothingIsAdded()
    {
        Assert.AreEqual(string.Empty, ShellHistoryScrub.Prepend(string.Empty));
        Assert.AreEqual("   ", ShellHistoryScrub.Prepend("   "));
    }

    /// <summary>
    /// 两条不变量:非 bash 一律短路(zsh 没有 <c>history -d</c>,fish 连解析都过不去),
    /// 以及"删之前先认一认"的那个记号必须真的出现在这一行里。
    /// </summary>
    /// <remarks>
    /// 记号就是那个临时变量名本身。少了它,遇上配了 <c>HISTCONTROL=ignorespace</c> 的用户 ——
    /// 那时我们这行根本没进历史 —— 删掉的就是人家上一条真命令。
    /// </remarks>
    [TestMethod]
    public void ItGuardsOnBashAndCarriesItsOwnMarker()
    {
        Assert.Contains("test -n \"${BASH_VERSION:-}\"", ShellHistoryScrub.Command);
        Assert.Contains(ShellHistoryScrub.Marker, ShellHistoryScrub.Command);
        Assert.Contains("builtin history -d", ShellHistoryScrub.Command);
    }
}
