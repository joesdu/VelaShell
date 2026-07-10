using System.Text;
using System.Text.RegularExpressions;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

[TestClass]
[TestCategory("SshConnection")]
public class Utf8StreamDecoderTests
{
    [TestMethod]
    public void DecodeBytes_CompleteUtf8Sequences_ReturnsCorrectString()
    {
        var decoder = new Utf8StreamDecoder();
        byte[] input = Encoding.UTF8.GetBytes("Hello World");
        string result = decoder.DecodeBytes(input);
        Assert.AreEqual("Hello World", result);
    }

    [TestMethod]
    public void DecodeBytes_EmptyInput_ReturnsEmptyString()
    {
        var decoder = new Utf8StreamDecoder();
        byte[] input = [];
        string result = decoder.DecodeBytes(input);
        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void DecodeBytes_Split2ByteChar_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        string fullChar = "é";
        byte[] bytes = Encoding.UTF8.GetBytes(fullChar);
        Assert.AreEqual(2, bytes.Length);
        string result1 = decoder.DecodeBytes([bytes[0]]);
        Assert.AreEqual(string.Empty, result1);
        string result2 = decoder.DecodeBytes([bytes[1]]);
        Assert.AreEqual(fullChar, result2);
    }

    [TestMethod]
    public void DecodeBytes_Split3ByteCjkChar_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        string cjkChar = "你";
        byte[] bytes = Encoding.UTF8.GetBytes(cjkChar);
        Assert.AreEqual(3, bytes.Length);
        string result1 = decoder.DecodeBytes([bytes[0]]);
        Assert.AreEqual(string.Empty, result1);
        string result2 = decoder.DecodeBytes([bytes[1]]);
        Assert.AreEqual(string.Empty, result2);
        string result3 = decoder.DecodeBytes([bytes[2]]);
        Assert.AreEqual(cjkChar, result3);
    }

    [TestMethod]
    public void DecodeBytes_Split3ByteCjkChar_TwoBytesThenOne_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        string cjkChar = "好";
        byte[] bytes = Encoding.UTF8.GetBytes(cjkChar);
        Assert.AreEqual(3, bytes.Length);
        string result1 = decoder.DecodeBytes([bytes[0], bytes[1]]);
        Assert.AreEqual(string.Empty, result1);
        string result2 = decoder.DecodeBytes([bytes[2]]);
        Assert.AreEqual(cjkChar, result2);
    }

    [TestMethod]
    public void DecodeBytes_Split4ByteEmoji_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        string emoji = "😀";
        byte[] bytes = Encoding.UTF8.GetBytes(emoji);
        Assert.AreEqual(4, bytes.Length);
        string result1 = decoder.DecodeBytes([bytes[0]]);
        Assert.AreEqual(string.Empty, result1);
        string result2 = decoder.DecodeBytes([bytes[1]]);
        Assert.AreEqual(string.Empty, result2);
        string result3 = decoder.DecodeBytes([bytes[2]]);
        Assert.AreEqual(string.Empty, result3);
        string result4 = decoder.DecodeBytes([bytes[3]]);
        Assert.AreEqual(emoji, result4);
    }

    [TestMethod]
    public void DecodeBytes_MixedCompleteAndSplit_DecodesCorrectly()
    {
        var decoder = new Utf8StreamDecoder();
        string text = "Hello你";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string result1 = decoder.DecodeBytes(bytes.Take(7).ToArray());
        Assert.AreEqual("Hello", result1);
        string result2 = decoder.DecodeBytes(bytes.Skip(7).ToArray());
        Assert.AreEqual("你", result2);
    }

    [TestMethod]
    public void DecodeBytes_MultipleSplitSequences_BuffersCorrectly()
    {
        var decoder = new Utf8StreamDecoder();
        string text = "你好世界";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (byte b in bytes.Take(bytes.Length - 1))
        {
            string result = decoder.DecodeBytes([b]);
            if (result.Length > 0)
            {
                Assert.IsTrue(Regex.IsMatch(result, "^[你好世]$"));
            }
        }
        string finalResult = decoder.DecodeBytes([bytes.Last()]);
        Assert.AreEqual("界", finalResult);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public void DecodeBytes_InvalidUtf8_ProducesReplacementCharacter()
    {
        var decoder = new Utf8StreamDecoder();

        // 0xFF is never valid in UTF-8
        byte[] invalidBytes = [0xFF, 0xFE];
        string result = decoder.DecodeBytes(invalidBytes);

        // Flush to force any buffered invalid bytes to emit
        result += decoder.Flush();
        StringAssert.Contains(result, "�");
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public void Flush_IncompleteSequence_ProducesReplacementCharacter()
    {
        var decoder = new Utf8StreamDecoder();

        // First byte of a 3-byte sequence (0xE4 starts 你)
        byte[] incompleteBytes = [0xE4];
        string result = decoder.DecodeBytes(incompleteBytes);
        Assert.AreEqual(string.Empty, result);
        string flushed = decoder.Flush();
        StringAssert.Contains(flushed, "�");
    }
}
