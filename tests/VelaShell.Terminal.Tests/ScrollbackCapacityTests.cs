using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 回滚容量(设置 → 终端 → 回滚行数)的生效路径。
/// </summary>
/// <remarks>
/// 设置页、<c>AppSettings</c> 与 <c>TerminalSettingsApplier</c> 这条线一直是通的,
/// 生效那一端却有两个洞:调小不当场裁(等下一次滚动,而一个跑完就停住的标签页没有下一次),
/// 以及写的是<b>当前</b>缓冲区 —— 在 vim / htop 里保存设置,值落到了备用屏上,
/// 主屏一个字没改。两条都是「拨了开关什么也没发生」,这里各钉一颗钉子。
/// </remarks>
[TestClass]
[TestCategory("Scrollback")]
public sealed class ScrollbackCapacityTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static TerminalEmulator EmulatorWithHistory(int lines, int scrollback)
    {
        var e = new TerminalEmulator(40, 10, TerminalType.XtermColor256, scrollback);
        e.Feed(Encoding.UTF8.GetBytes(Lines(lines)));
        return e;
    }

    private static string Lines(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append('L').Append(i).Append("\r\n");
        }
        return sb.ToString();
    }

    [TestMethod]
    public void LoweringTheCapacityTrimsOnTheSpot()
    {
        TerminalEmulator e = EmulatorWithHistory(500, scrollback: 1000);
        Assert.IsGreaterThan(400, e.Screen.ScrollbackCount, "样本本身要先攒够历史。");

        e.ScrollbackLines = 100;

        // 不喂任何新输出 —— 内存要在改设置的那一刻就还回去,而不是等下一次滚动。
        Assert.AreEqual(100, e.Screen.ScrollbackCount);
    }

    [TestMethod]
    public void RaisingTheCapacityKeepsEverythingAlreadyThere()
    {
        TerminalEmulator e = EmulatorWithHistory(300, scrollback: 200);
        Assert.AreEqual(200, e.Screen.ScrollbackCount);

        e.ScrollbackLines = 5000;

        Assert.AreEqual(200, e.Screen.ScrollbackCount, "调大只是抬上限,已经裁掉的行回不来,留着的一行也不该丢。");
    }

    [TestMethod]
    public void ANegativeCapacityIsClampedRatherThanThrowing()
    {
        // 上游有 AppSettings.ClampNumbers 与设置页的 NumericUpDown 两道闸,但插件能力面
        // (PluginTerminalViewApi)也能写这个属性 —— 兜底钳在最里面一层。
        TerminalEmulator e = EmulatorWithHistory(50, scrollback: 1000);

        e.ScrollbackLines = -1;

        Assert.AreEqual(0, e.ScrollbackLines);
        Assert.AreEqual(0, e.Screen.ScrollbackCount);
    }

    [TestMethod]
    public void SettingItOnTheAlternateScreenLandsOnTheMainScreen()
    {
        var e = new TerminalEmulator(40, 10, TerminalType.XtermColor256, 1000);
        e.Feed(Encoding.UTF8.GetBytes("\e[?1049h"));
        Assert.IsTrue(e.IsAlternateScreen, "样本应当已切到备用屏。");

        e.ScrollbackLines = 5000;

        Assert.AreEqual(0, e.Screen.MaxScrollback, "备用屏的容量恒为 0 —— 给了它容量,vim 里就能往回滚出历史,改窗口大小还会连带触发它本不该做的 reflow。");
        e.Feed(Encoding.UTF8.GetBytes(Lines(200)));
        Assert.AreEqual(0, e.Screen.ScrollbackCount, "备用屏的退休行不该进历史。");

        e.Feed(Encoding.UTF8.GetBytes("\e[?1049l"));
        Assert.IsFalse(e.IsAlternateScreen);
        Assert.AreEqual(5000, e.ScrollbackLines, "在 vim 里保存的设置,退出 vim 后必须是生效的。");
        Assert.AreEqual(5000, e.Screen.MaxScrollback);
    }

    [TestMethod]
    public void TheControlReadsBackTheMainScreenValueWhileOnTheAlternateScreen()
    {
        Session.Dispatch(() =>
        {
            var control = new VelaTerminalControl { ScrollbackLines = 4000 };
            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            control.Feed(Encoding.UTF8.GetBytes("\e[?1049h"));
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(control.IsAlternateScreenActive);

            // 读回来的若是当前缓冲区,这里会是备用屏的 0 —— 设置页再打开就显示成 0。
            Assert.AreEqual(4000, control.ScrollbackLines);
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ShrinkingPullsTheScrollPositionBackIntoRange()
    {
        Session.Dispatch(() =>
        {
            var control = new VelaTerminalControl();
            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            control.Feed(Encoding.UTF8.GetBytes(Lines(500)));
            Dispatcher.UIThread.RunJobs();
            control.ScrollOffset = 300;
            Assert.AreEqual(300, control.ScrollOffset, "样本要先停在历史里,而不是跟着底部。");

            control.ScrollbackLines = 50;

            Assert.AreEqual(50, control.MaxScrollOffset);
            Assert.IsLessThanOrEqualTo(
                control.MaxScrollOffset, control.ScrollOffset,
                $"裁剪后视图停在了一行不存在的历史上(offset={control.ScrollOffset} > max={control.MaxScrollOffset})。");
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
