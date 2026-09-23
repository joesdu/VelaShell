using VelaShell.Core.Models;
using VelaShell.Core.XServer;

namespace VelaShell.Core.Tests.XServer;

/// <summary>
/// 设置页 → VcXsrv 命令行(<see cref="XServerCommandLine" />):设置页上看到的必须就是实际传过去的。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public class XServerCommandLineTests
{
    /// <summary>默认设置:多窗口、剪贴板 + PRIMARY、不抓键、WGL、无托盘、pc105,不带 -ac。</summary>
    [TestMethod]
    public void Defaults_ProduceTheExpectedArguments()
    {
        IReadOnlyList<string> args = XServerCommandLine.Build(new XServerOptions(), 0);

        Assert.AreSequenceEqual(
            [":0", "-multiwindow", "-clipboard", "-primary", "-nokeyhook", "-wgl", "-notrayicon",
             "-xkbmodel", "pc105", "-silent-dup-error"],
            [.. args]);
    }

    [TestMethod]
    [DataRow(XServerWindowModes.MultiWindow, "-multiwindow")]
    [DataRow(XServerWindowModes.NoDecoration, "-nodecoration")]
    [DataRow(XServerWindowModes.Fullscreen, "-fullscreen")]
    [DataRow(XServerWindowModes.Rootless, "-rootless")]
    public void WindowMode_MapsToItsSwitch(string mode, string expected)
    {
        IReadOnlyList<string> args = XServerCommandLine.Build(new XServerOptions { WindowMode = mode }, 0);

        Assert.AreEqual(expected, args[1]);
    }

    /// <summary>「一个大窗口」就是 VcXsrv 不带模式参数时的样子 —— 什么都不写,而不是写一个不存在的开关。</summary>
    [TestMethod]
    public void Windowed_AddsNoModeSwitch()
    {
        IReadOnlyList<string> args = XServerCommandLine.Build(new XServerOptions { WindowMode = XServerWindowModes.Windowed }, 3);

        Assert.AreEqual(":3", args[0]);
        Assert.AreEqual("-clipboard", args[1]);
        CollectionAssert.DoesNotContain(args.ToList(), "-multiwindow");
    }

    /// <summary>剪贴板关着时 PRIMARY 没有意义,命令行里不出现一个看着像生效了的开关。</summary>
    [TestMethod]
    public void ClipboardOff_OmitsPrimary()
    {
        IReadOnlyList<string> args = XServerCommandLine.Build(
            new XServerOptions { Clipboard = false, CopyOnSelection = true }, 0);

        CollectionAssert.Contains(args.ToList(), "-noclipboard");
        CollectionAssert.DoesNotContain(args.ToList(), "-primary");
        CollectionAssert.DoesNotContain(args.ToList(), "-noprimary");
    }

    [TestMethod]
    public void Toggles_AreWrittenExplicitlyBothWays()
    {
        IReadOnlyList<string> args = XServerCommandLine.Build(new XServerOptions
        {
            CopyOnSelection = false,
            KeyHook = true,
            NativeOpenGl = false,
            ShowTrayIcon = true,
            DisableAccessControl = true,
        }, 0);

        List<string> list = [.. args];
        CollectionAssert.Contains(list, "-noprimary");
        CollectionAssert.Contains(list, "-keyhook");
        CollectionAssert.Contains(list, "-nowgl");
        CollectionAssert.Contains(list, "-trayicon");
        CollectionAssert.Contains(list, "-ac");
    }

    /// <summary>「自动」布局不传 -xkblayout(VcXsrv 自己跟随 Windows);填了就成对出现。</summary>
    [TestMethod]
    public void KeyboardLayout_OnlyWhenSet()
    {
        List<string> auto = [.. XServerCommandLine.Build(new XServerOptions { KeyboardLayout = "" }, 0)];
        List<string> de = [.. XServerCommandLine.Build(new XServerOptions { KeyboardLayout = " de " }, 0)];

        CollectionAssert.DoesNotContain(auto, "-xkblayout");
        int at = de.IndexOf("-xkblayout");
        Assert.IsGreaterThanOrEqualTo(0, at);
        Assert.AreEqual("de", de[at + 1]);
    }

    /// <summary>附加参数排在最后,同一开关后写的覆盖先写的 —— 用户的逃生口。</summary>
    [TestMethod]
    public void ExtraArguments_ComeLast()
    {
        IReadOnlyList<string> args = XServerCommandLine.Build(
            new XServerOptions { ExtraArguments = "-noclipboard -dpi auto" }, 0, logFile: @"C:\logs\vcxsrv.log");

        Assert.AreSequenceEqual(["-noclipboard", "-dpi", "auto"], [.. args.TakeLast(3)]);
        int log = args.ToList().IndexOf("-logfile");
        Assert.AreEqual(@"C:\logs\vcxsrv.log", args[log + 1]);
        Assert.IsLessThan(args.Count - 3, log);
    }

    [TestMethod]
    public void NegativeDisplay_Throws() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => XServerCommandLine.Build(new XServerOptions(), -1));

    [TestMethod]
    [DataRow(null, new string[0])]
    [DataRow("   ", new string[0])]
    [DataRow("-a  -b", new[] { "-a", "-b" })]
    [DataRow("-logfile \"C:\\my logs\\x.log\" -once", new[] { "-logfile", "C:\\my logs\\x.log", "-once" })]
    [DataRow("-fp \"\"", new[] { "-fp", "" })]
    [DataRow("-screen 0 @1", new[] { "-screen", "0", "@1" })]
    public void SplitArguments_HonorsQuotesAndKeepsBackslashes(string? text, string[] expected) =>
        Assert.AreSequenceEqual(expected, [.. XServerCommandLine.SplitArguments(text)]);

    [TestMethod]
    public void DisplayAddress_AndPort()
    {
        Assert.AreEqual("localhost:2.0", XServerCommandLine.DisplayAddress(2));
        Assert.AreEqual(6002, XServerCommandLine.TcpPort(2));
    }

    /// <summary>帮助表里每一条的资源键都不重复,写法也不重复(合并掉 -help 原文里重复的两条之后)。</summary>
    [TestMethod]
    public void HelpCatalog_HasNoDuplicates()
    {
        List<XServerHelpEntry> entries = [.. XServerHelpCatalog.Groups.SelectMany(g => g.Entries)];

        Assert.HasCount(entries.Count, entries.Select(e => e.DescriptionKey).Distinct());
        Assert.HasCount(entries.Count, entries.Select(e => e.Syntax).Distinct());
        Assert.IsGreaterThan(80, entries.Count);
    }

    /// <summary>每个资源键在五种语言里都真有文案 —— 帮助表是运行期取词,静态的缺键检查看不见它。</summary>
    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void HelpCatalog_EveryKeyIsTranslated(string culture)
    {
        var manager = new System.Resources.ResourceManager("VelaShell.Core.Resources.Strings", typeof(XServerHelpCatalog).Assembly);
        System.Globalization.CultureInfo info = System.Globalization.CultureInfo.GetCultureInfo(culture);
        IEnumerable<string> keys = XServerHelpCatalog.Groups
            .SelectMany(g => g.Entries.Select(e => e.DescriptionKey).Prepend(g.TitleKey));

        foreach (string key in keys)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(manager.GetString(key, info)), $"{culture}: {key} 没有文案");
        }
    }
}
