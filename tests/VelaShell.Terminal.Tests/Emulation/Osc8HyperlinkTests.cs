using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// OSC 8 显式超链接(<c>OSC 8 ; params ; URI ST</c> … <c>OSC 8 ; ; ST</c>):
/// 打印路径给每一格盖上链接句柄,句柄经 <see cref="HyperlinkTable" /> 换回 URI。
/// </summary>
/// <remarks>
/// 断言的是<b>句柄落在哪些格上</b>而不是"屏幕上是不是蓝的" —— 渲染只是句柄的一个消费者,
/// 而句柄的正确性要在擦除、覆写、删除字符、重排(reflow)之后都成立。
/// </remarks>
[TestClass]
[TestCategory("Emulator")]
public class Osc8HyperlinkTests
{
    private const string Uri = "https://example.com/a";

    private static TerminalEmulator New(int cols = 20, int rows = 6) => new(cols, rows, TerminalType.XtermColor256);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    /// <summary>把一段文本包进一条 OSC 8 链接里(开链接 → 文本 → 关链接)。</summary>
    private static string Link(string uri, string text, string parameters = "") =>
        $"\e]8;{parameters};{uri}\e\\{text}\e]8;;\e\\";

    /// <summary>活动屏第 <paramref name="row" /> 行第 <paramref name="col" /> 列上的链接地址(无则 null)。</summary>
    private static string? UriAt(TerminalEmulator e, int col, int row = 0) =>
        e.Hyperlinks.UriOf(e.Screen.ActiveLine(row).LinkAt(col));

    [TestMethod]
    public void LinkedRun_CarriesTheUriOnEveryCell_AndStopsAtTheCloser()
    {
        TerminalEmulator e = New();
        Feed(e, Link(Uri, "docs") + " tail");

        for (int col = 0; col < 4; col++)
        {
            Assert.AreEqual(Uri, UriAt(e, col), $"第 {col} 列在链接内,应当带上地址。");
        }
        Assert.IsNull(UriAt(e, 4), "关闭序列之后的文本不该再属于这条链接。");
        Assert.IsNull(UriAt(e, 5));
        Assert.AreEqual("docs tail", e.Screen.ActiveLine(0).GetText());
    }

    [TestMethod]
    public void UriContainingSemicolons_IsNotTruncated()
    {
        // 分号是 OSC 的字段分隔符,却在查询串里完全合法。只取 p[2] 会把地址拦腰截断,
        // 点开就是另一个页面 —— 这正是要把 p[2..] 重新拼回来的原因。
        const string withSemis = "https://example.com/q?a=1;b=2;c=3";
        TerminalEmulator e = New(40);
        Feed(e, Link(withSemis, "x"));

        Assert.AreEqual(withSemis, UriAt(e, 0));
    }

    [TestMethod]
    public void SameIdAndUri_ShareOneHandle_DifferentId_DoesNot()
    {
        TerminalEmulator e = New(40);
        Feed(e, Link(Uri, "a", "id=one") + Link(Uri, "b", "id=one") + Link(Uri, "c", "id=two"));

        TerminalRow row = e.Screen.ActiveLine(0);
        Assert.AreEqual(row.LinkAt(0), row.LinkAt(1), "id 与 URI 都相同应当是同一条链接。");
        Assert.AreNotEqual(row.LinkAt(0), row.LinkAt(2), "id 不同即为两条链接(规范用它区分被换行拆开的同一条)。");
        Assert.AreEqual(Uri, UriAt(e, 2), "不过它们指向同一个地址。");
    }

    [TestMethod]
    public void UnknownParameterKeys_AreIgnored()
    {
        TerminalEmulator e = New(40);
        Feed(e, Link(Uri, "x", "foo=bar:id=k:baz=1"));

        Assert.AreEqual(Uri, UriAt(e, 0));
    }

    [TestMethod]
    [DataRow("javascript:alert(1)", DisplayName = "javascript:")]
    [DataRow("file:///C:/Windows/System32/cmd.exe", DisplayName = "file:")]
    [DataRow("vscode://x/y", DisplayName = "未知自定义 scheme")]
    [DataRow("not-a-uri", DisplayName = "根本不是 URI")]
    public void DisallowedScheme_ProducesNoClickableLink(string uri)
    {
        // 终端输出是不可信输入,而点开它走的是系统 shell 关联。白名单之外一律不驻留:
        // 文本照常显示,只是点不开,也不画下划线("看起来能点"= "真的能点")。
        TerminalEmulator e = New(40);
        Feed(e, Link(uri, "click"));

        Assert.IsNull(UriAt(e, 0));
        Assert.AreEqual(0, e.Hyperlinks.Count);
        Assert.AreEqual("click", e.Screen.ActiveLine(0).GetText(), "锚文本本身一个字都不能少。");
    }

    [TestMethod]
    public void UriWithControlCharacters_IsRejected()
    {
        // 控制字符不可能出现在合法 URI 里,却能把悬停提示里的地址撑成另一副样子。
        var table = new HyperlinkTable();
        Assert.AreEqual(0, table.Intern(null, "https://exa\u0007mple.com"));
    }

    [TestMethod]
    public void OverwritingALinkedCell_ClearsTheLink()
    {
        // 幽灵链接回归:光标回到链接上覆写普通文本,那一格必须彻底不再可点。
        TerminalEmulator e = New();
        Feed(e, Link(Uri, "docs"));
        Feed(e, "\e[1;1H"); // 回到行首
        Feed(e, "XY");

        Assert.IsNull(UriAt(e, 0));
        Assert.IsNull(UriAt(e, 1));
        Assert.AreEqual(Uri, UriAt(e, 2), "没被覆写的那半段仍然是链接。");
    }

    [TestMethod]
    public void ErasingALine_ClearsItsLinks()
    {
        TerminalEmulator e = New();
        Feed(e, Link(Uri, "docs"));
        Feed(e, "\e[1;1H\e[2K"); // EL 2:整行擦除

        Assert.IsNull(UriAt(e, 0));
        Assert.IsFalse(e.Screen.ActiveLine(0).HasLinks, "整行没有链接了,平行数组应当被丢掉。");
    }

    [TestMethod]
    public void DeletingCharacters_ShiftsLinksWithTheirCells()
    {
        // DCH 之后剩下的还是原来那些字符,链接归属不能错位。
        TerminalEmulator e = New();
        Feed(e, "ab" + Link(Uri, "cd"));
        Feed(e, "\e[1;1H\e[2P"); // 删掉行首两格 "ab"

        Assert.AreEqual("cd", e.Screen.ActiveLine(0).GetText());
        Assert.AreEqual(Uri, UriAt(e, 0));
        Assert.AreEqual(Uri, UriAt(e, 1));
    }

    [TestMethod]
    public void WideCharacterTrailingCell_CarriesTheLinkToo()
    {
        // 尾格与前导格同属一个字符:漏了它,宽字符链接的右半格点不开,悬停时手型在半个字上闪。
        TerminalEmulator e = New();
        Feed(e, Link(Uri, "\u4E2D"));

        Assert.AreEqual(Uri, UriAt(e, 0));
        Assert.AreEqual(Uri, UriAt(e, 1));
        Assert.IsTrue(e.Screen.ActiveLine(0)[1].IsWideTrailing);
    }

    [TestMethod]
    public void BatchPrintFastPath_StampsLinksToo()
    {
        // PrintRun 是纯 ASCII 洪流的快路径,它绕开逐格索引器直写切片 ——
        // 漏掉链接的话,长链接文本会整段失效,而短的(走逐字符路径)看着好好的。
        TerminalEmulator e = New(60);
        Feed(e, Link(Uri, "abcdefghijklmnopqrstuvwxyz"));

        Assert.IsGreaterThan(0, e.PrintRunCharsForTest, "本用例必须真的走到批量快路径,否则它什么也没验。");
        for (int col = 0; col < 26; col++)
        {
            Assert.AreEqual(Uri, UriAt(e, col), $"第 {col} 列。");
        }
    }

    [TestMethod]
    public void Reflow_CarriesLinksThroughAColumnResize()
    {
        // 拖一下窗口宽度就把所有可点链接弄丢,是最容易漏掉的一处 —— 重排走的是
        // "收集单元格 → 按新宽度重发"这条独立路径,链接必须与单元格同行同列地一起穿过去。
        TerminalEmulator e = New(20, 4);
        Feed(e, "xx" + Link(Uri, "0123456789012345"));

        e.Resize(10, 4);

        int found = 0;
        for (int row = 0; row < e.Screen.TotalRows; row++)
        {
            TerminalRow line = e.Screen.ViewLine(row);
            for (int col = 0; col < 10; col++)
            {
                if (e.Hyperlinks.UriOf(line.LinkAt(col)) == Uri)
                {
                    found++;
                }
            }
        }
        Assert.AreEqual(16, found, "重排前后带链接的格数应当一格不差。");
    }

    [TestMethod]
    public void FullReset_ClearsTheTable()
    {
        // RIS 之后整个缓冲区(含回滚)已被清空,没有任何格还引用旧句柄 ——
        // 这是唯一能安全整表回收的时机。
        TerminalEmulator e = New();
        Feed(e, Link(Uri, "docs"));
        Assert.AreEqual(1, e.Hyperlinks.Count);

        Feed(e, "\ec"); // RIS

        Assert.AreEqual(0, e.Hyperlinks.Count);
        Assert.IsNull(UriAt(e, 0));
    }

    [TestMethod]
    public void SgrReset_DoesNotCloseTheLink()
    {
        // OSC 8 与 SGR 相互独立:只有 `OSC 8 ; ; ST` 才关链接。
        // 高亮完锚文本顺手 SGR 0 是极常见的写法,在这里把链接关掉就等于大面积失效。
        TerminalEmulator e = New();
        Feed(e, $"\e]8;;{Uri}\e\\\e[1mbold\e[0mplain\e]8;;\e\\");

        Assert.AreEqual(Uri, UriAt(e, 0));
        Assert.AreEqual(Uri, UriAt(e, 5), "SGR 0 之后的文本仍在同一条链接内。");
    }

    [TestMethod]
    public void TableIsCapped_AndDegradesToPlainText()
    {
        // 表没有引用计数(回滚区里的格随时可能引用任意一条旧链接),上限就是它的内存账。
        // 撞上限之后新链接不可点,文本本身一个字不少。
        var table = new HyperlinkTable();
        for (int i = 0; i < HyperlinkTable.MaxEntries; i++)
        {
            Assert.AreNotEqual(0, table.Intern(null, $"https://example.com/{i}"));
        }
        Assert.AreEqual(HyperlinkTable.MaxEntries, table.Count);
        Assert.AreEqual(0, table.Intern(null, "https://example.com/overflow"));
        Assert.AreNotEqual(0, table.Intern(null, "https://example.com/7"), "已在表内的链接不受上限影响。");
    }

    [TestMethod]
    public void OverlyLongUri_IsRejected()
    {
        var table = new HyperlinkTable();
        string tooLong = "https://example.com/" + new string('a', HyperlinkTable.MaxUriLength);
        Assert.AreEqual(0, table.Intern(null, tooLong));
    }

    [TestMethod]
    public void ClosingWithoutAUriField_AlsoClosesTheLink()
    {
        // `OSC 8 ; ST`(连 URI 字段都没有)是野生脚本里常见的写法,按关闭处理。
        TerminalEmulator e = New();
        Feed(e, $"\e]8;;{Uri}\e\\ab\e]8;\e\\cd");

        Assert.AreEqual(Uri, UriAt(e, 1));
        Assert.IsNull(UriAt(e, 2));
    }
}
