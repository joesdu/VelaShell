using System.Text;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 远端搜索按会话编码:输出按会话的文件名编码解(与 SFTP 同一个来源),命令里的目录、模式、文字编成会话编码的字节,
/// 以八进制转义交给远端还原;名字里的换行在记录里是 <c>\0</c>,解析时换回来。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class RemoteSearchEncodingTests
{
    private const string Chinese = "中文报告";

    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [TestMethod]
    public void DecodeRecord_UsesTheSessionEncoding_AndRestoresNewlines()
    {
        var gbk = Encoding.GetEncoding("GBK");
        byte[] record = [.. "f\t/srv/"u8.ToArray(), .. gbk.GetBytes(Chinese), 0, .. "x.txt"u8.ToArray()];

        Assert.AreEqual("f\t/srv/" + Chinese + "\nx.txt", RemoteSearch.DecodeRecord(record, gbk));
        Assert.AreNotEqual("f\t/srv/" + Chinese + "\nx.txt", RemoteSearch.DecodeRecord(record, null),
            "UTF-8 会话照旧按 UTF-8 解(GBK 字节解不出中文)");
        Assert.AreEqual("d\t/srv/" + Chinese, RemoteSearch.DecodeRecord(Encoding.UTF8.GetBytes("d\t/srv/" + Chinese), null));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("UTF-8")]
    [DataRow("utf8")]
    [DataRow("no-such-charset")]
    public void Resolve_Utf8BlankOrUnknown_MeansDefault(string? name) =>
        Assert.IsNull(RemoteFileNameEncoding.Resolve(name));

    [TestMethod]
    public void Resolve_LegacyCharset_IsTheSessionEncoding() =>
        Assert.AreEqual(936, RemoteFileNameEncoding.Resolve(" GBK ")?.CodePage);

    /// <summary>GBK 会话:三项用户输入都编成 GBK 字节、写成纯 ASCII 的八进制转义,脚本开头用 printf 还原。</summary>
    [TestMethod]
    public void BuildCommand_LegacyEncoding_PassesUserInputAsOctalEscapesOfTheSessionBytes()
    {
        var gbk = Encoding.GetEncoding("GBK");
        string command = RemoteSearch.BuildCommand(new("/srv/" + Chinese, Chinese, Chinese + "'$(id)"), RemoteShellKind.Bash, gbk);

        Assert.DoesNotContain(Chinese, command, "中文不能原样进命令(exec 请求只能是 UTF-8,远端要的是 GBK 字节)");
        Assert.Contains(" vela-search " + RemoteSha256.Quote(Escape(gbk, "/srv/" + Chinese)) + " ", command);
        Assert.Contains(" " + RemoteSha256.Quote(Escape(gbk, "*" + Chinese + "*")) + " '501' ", command);
        Assert.EndsWith(" " + RemoteSha256.Quote(Escape(gbk, Chinese + "'$(id)")), command);
        Assert.DoesNotContain("$(id)", command, "转义之后用户输入一个字节都不以原样出现");
        Assert.Contains("t=$(printf \"$5_\")", command, "脚本开头把参数还原成字节");
        Assert.IsTrue(Escape(gbk, "/srv").All(static c => c is '\\' or (>= '0' and <= '7')), "转义串只由 \\ddd 组成,拿它当 printf 格式串不会解释出转换");
    }

    /// <summary>会话编码里没有的字符(GBK 里的表情符号):整次退回原样的 UTF-8 参数,而不是把它换成「?」通配符。</summary>
    [TestMethod]
    public void BuildCommand_InputTheSessionEncodingCannotHold_FallsBackToPlainArguments()
    {
        var gbk = Encoding.GetEncoding("GBK");
        string command = RemoteSearch.BuildCommand(new("/srv", "😀"), RemoteShellKind.PosixSh, gbk);

        Assert.Contains(" '*😀*' ", command);
        Assert.Contains(" '/srv' ", command);
        Assert.DoesNotContain("printf \"$1_\"", command);
    }

    /// <summary>
    /// UTF-8 会话(最常见的配置)里遇到 GBK 字节的名字:与 SFTP 库同一条规则,解不开的每个字节记成 U+DC00+b,
    /// 不是 U+FFFD —— 双击结果定位过去时 SFTP 编得回原来那串字节,面板里也选得中那一行(复查 file-panes F4)。
    /// </summary>
    [TestMethod]
    public void DecodeRecord_Utf8Session_EscapesUndecodableBytesExactlyLikeSftp()
    {
        byte[] path = [.. "/srv/"u8.ToArray(), 0xD6, 0xD0, .. "/中.log"u8.ToArray()];
        byte[] record = [.. "f\t"u8.ToArray(), .. path];

        string decoded = RemoteSearch.DecodeRecord(record, null);

        Assert.AreEqual("f\t" + VelaShell.Ssh.Sftp.SftpNameCodec.Utf8.Decode(path), decoded);
        Assert.AreEqual("f\t/srv/\udcd6\udcd0/中.log", decoded);
        Assert.AreSequenceEqual(path, RemoteFileNameEncoding.EncodeExact(decoded[2..], null)!, "编回去还是原来那串字节");
        Assert.AreSequenceEqual(VelaShell.Ssh.Sftp.SftpNameCodec.Utf8.Encode(decoded[2..]), RemoteFileNameEncoding.EncodeExact(decoded[2..], null)!);
    }

    /// <summary>从一个不是 UTF-8 名字的目录起找(UTF-8 会话):目录名里的 U+DCxx 还原成原始字节交给远端,而不是写成 EF BF BD。</summary>
    [TestMethod]
    public void BuildCommand_Utf8Session_RootWithRawByteEscapes_PassesTheOriginalBytes()
    {
        string command = RemoteSearch.BuildCommand(new("/srv/\udcd6\udcd0", "*.log"), RemoteShellKind.Bash);

        Assert.Contains(" vela-search " + RemoteSha256.Quote(@"\057\163\162\166\057\326\320") + " ", command);
        Assert.Contains("printf \"$1_\"", command, "脚本开头把参数还原成字节");
        Assert.DoesNotContain("�", command);
    }

    /// <summary>
    /// Big5 / Shift_JIS 的双字节字符,后一个字节可以是 <c>\</c> <c>[</c> <c>]</c>(「功」A5 5C、「表」95 5C):编完之后那个字节前面补一个 <c>\</c>,
    /// fnmatch 才把它当字面量 —— 否则它把下一个字节转义掉,搜「功能」一个都找不到(复查 file-panes F5)。用户自己写的 ASCII 通配符照旧。
    /// </summary>
    [TestMethod]
    [DataRow("Big5", "功能", new byte[] { 0x2A, 0xA5, 0x5C, 0x5C, 0xAF, 0xE0, 0x2A })]
    [DataRow("shift_jis", "表*.txt", new byte[] { 0x95, 0x5C, 0x5C, 0x2A, 0x2E, 0x74, 0x78, 0x74 })]
    [DataRow("shift_jis", "ソ[ab]", new byte[] { 0x83, 0x5C, 0x5C, 0x5B, 0x61, 0x62, 0x5D })]
    public void BuildCommand_TrailBytesThatAreFnmatchMetacharacters_AreEscaped(string charset, string pattern, byte[] expected)
    {
        Encoding encoding = Encoding.GetEncoding(charset);

        string command = RemoteSearch.BuildCommand(new("/srv", pattern), RemoteShellKind.Bash, encoding);

        Assert.Contains(" " + RemoteSha256.Quote(Octal(expected)) + " '501' ", command);
    }

    private static string Escape(Encoding encoding, string value) =>
        string.Concat(encoding.GetBytes(value).Select(static b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));

    private static string Octal(byte[] bytes) =>
        string.Concat(bytes.Select(static b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
}
