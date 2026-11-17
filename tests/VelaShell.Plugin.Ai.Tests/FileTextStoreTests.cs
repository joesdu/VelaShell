using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>差异标签保存时换行照原文件:LF 的远端配置不能因为在 Windows 上编辑而混进 CRLF。</summary>
[TestClass]
public sealed class FileTextStoreTests
{
    [TestMethod]
    [DataRow("a\nb\n", "x\r\ny\nz", "x\ny\nz")]
    [DataRow("a\r\nb\r\n", "x\r\ny\nz", "x\r\ny\r\nz")]
    [DataRow("a\r\nb\nc\r\n", "x\ny", "x\r\ny")]
    [DataRow("one line", "x\r\ny", "x\r\ny")]
    [DataRow(null, "x\r\ny", "x\r\ny")]
    public void MatchLineEndings_FollowsDominantStyleOfOriginal(string? reference, string text, string expected)
        => Assert.AreEqual(expected, FileTextStore.MatchLineEndings(text, reference));

    [TestMethod]
    public void MatchLineEndings_LeavesOtherSeparatorsAlone()
    {
        string ls = ((char)0x2028).ToString();
        string text = "a\fb" + ls + "c\rd\r\ne";
        Assert.AreEqual("a\fb" + ls + "c\rd\ne", FileTextStore.MatchLineEndings(text, "x\ny"));
    }
}
