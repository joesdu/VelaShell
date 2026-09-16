using System.Text;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 同步输入频道跨编码转发:频道里一台 GBK 的堡垒机和一台 UTF-8 的容器同框是常事。
/// <para>
/// 输入改为按会话字符集编码之后,原样转发原始字节就会串味 —— 源标签编出来的 GBK 字节
/// 落到 UTF-8 那一侧是四个替换字符。从前这条路碰巧没问题,只因为那时输入写死了 UTF-8。
/// </para>
/// </summary>
[TestClass]
[TestCategory("SyncInput")]
public sealed class SyncInputTranscodeTests
{
    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Gbk => Encoding.GetEncoding("GBK");

    [TestMethod]
    public void BytesAreRecodedForATargetWithADifferentCharset()
    {
        byte[] fromGbk = Gbk.GetBytes("中文");

        byte[] toUtf8 = SyncInputCoordinator.Transcode(fromGbk, Gbk, Encoding.UTF8);

        Assert.AreSequenceEqual(Encoding.UTF8.GetBytes("中文"), toUtf8);
        Assert.AreEqual("中文", Encoding.UTF8.GetString(toUtf8), "目标标签解出来必须还是原文。");
    }

    [TestMethod]
    public void BytesAreForwardedUntouchedWhenBothSidesShareTheCharset()
    {
        // 绝大多数频道两边同编码;这条路不该白白多一次解码再编码。
        byte[] data = Gbk.GetBytes("中文");

        byte[] forwarded = SyncInputCoordinator.Transcode(data, Gbk, Encoding.GetEncoding("GBK"));

        Assert.AreSame(data, forwarded);
    }

    [TestMethod]
    public void ControlSequencesSurviveTranscoding()
    {
        // 频道里转发的不只有文本:方向键、Ctrl+C、括号粘贴标记也走同一条路。
        byte[] keys = Encoding.ASCII.GetBytes("\e[A\e[200~ls\e[201~");

        byte[] forwarded = SyncInputCoordinator.Transcode(keys, Gbk, Encoding.UTF8);

        Assert.AreSequenceEqual(keys, forwarded);
    }

    [TestMethod]
    public void CharactersTheTargetCannotRepresentDegradeToQuestionMarks()
    {
        // 简体字进 Big5 是表示不了的;退化成 '?' 而不是把字节流弄成半截多字节序列。
        byte[] fromGbk = Gbk.GetBytes("测试");

        byte[] toBig5 = SyncInputCoordinator.Transcode(fromGbk, Gbk, Encoding.GetEncoding("Big5"));

        Assert.AreSequenceEqual(new byte[] { 0x3F, 0x3F }, toBig5);
    }
}
