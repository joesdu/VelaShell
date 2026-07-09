using VelaShell.Core.Ssh;
using System.Text;

namespace VelaShell.Core.Tests.Ssh;

[TestClass]
[TestCategory("SshConnection")]
public class Utf8StreamDecoderTests
{
    [TestMethod]
    public void DecodeBytes_CompleteUtf8Sequences_ReturnsCorrectString()
    {
        var decoder = new Utf8StreamDecoder();
        var input = Encoding.UTF8.GetBytes("Hello World");

        var result = decoder.DecodeBytes(input);

        Assert.AreEqual("Hello World", result);
    }

    [TestMethod]
    public void DecodeBytes_EmptyInput_ReturnsEmptyString()
    {
        var decoder = new Utf8StreamDecoder();
        var input = Array.Empty<byte>();

        var result = decoder.DecodeBytes(input);

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void DecodeBytes_Split2ByteChar_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var fullChar = "é";
        var bytes = Encoding.UTF8.GetBytes(fullChar);
        Assert.AreEqual(2, bytes.Length);

        var result1 = decoder.DecodeBytes(new[] { bytes[0] });
        Assert.AreEqual(string.Empty, result1);

        var result2 = decoder.DecodeBytes(new[] { bytes[1] });
        Assert.AreEqual(fullChar, result2);
    }

    [TestMethod]
    public void DecodeBytes_Split3ByteCjkChar_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var cjkChar = "你";
        var bytes = Encoding.UTF8.GetBytes(cjkChar);
        Assert.AreEqual(3, bytes.Length);

        var result1 = decoder.DecodeBytes(new[] { bytes[0] });
        Assert.AreEqual(string.Empty, result1);

        var result2 = decoder.DecodeBytes(new[] { bytes[1] });
        Assert.AreEqual(string.Empty, result2);

        var result3 = decoder.DecodeBytes(new[] { bytes[2] });
        Assert.AreEqual(cjkChar, result3);
    }

    [TestMethod]
    public void DecodeBytes_Split3ByteCjkChar_TwoBytesThenOne_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var cjkChar = "好";
        var bytes = Encoding.UTF8.GetBytes(cjkChar);
        Assert.AreEqual(3, bytes.Length);

        var result1 = decoder.DecodeBytes(new[] { bytes[0], bytes[1] });
        Assert.AreEqual(string.Empty, result1);

        var result2 = decoder.DecodeBytes(new[] { bytes[2] });
        Assert.AreEqual(cjkChar, result2);
    }

    [TestMethod]
    public void DecodeBytes_Split4ByteEmoji_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var emoji = "😀";
        var bytes = Encoding.UTF8.GetBytes(emoji);
        Assert.AreEqual(4, bytes.Length);

        var result1 = decoder.DecodeBytes(new[] { bytes[0] });
        Assert.AreEqual(string.Empty, result1);

        var result2 = decoder.DecodeBytes(new[] { bytes[1] });
        Assert.AreEqual(string.Empty, result2);

        var result3 = decoder.DecodeBytes(new[] { bytes[2] });
        Assert.AreEqual(string.Empty, result3);

        var result4 = decoder.DecodeBytes(new[] { bytes[3] });
        Assert.AreEqual(emoji, result4);
    }

    [TestMethod]
    public void DecodeBytes_MixedCompleteAndSplit_DecodesCorrectly()
    {
        var decoder = new Utf8StreamDecoder();
        var text = "Hello你";
        var bytes = Encoding.UTF8.GetBytes(text);

        var result1 = decoder.DecodeBytes(bytes.Take(7).ToArray());
        Assert.AreEqual("Hello", result1);

        var result2 = decoder.DecodeBytes(bytes.Skip(7).ToArray());
        Assert.AreEqual("你", result2);
    }

    [TestMethod]
    public void DecodeBytes_MultipleSplitSequences_BuffersCorrectly()
    {
        var decoder = new Utf8StreamDecoder();
        var text = "你好世界";
        var bytes = Encoding.UTF8.GetBytes(text);

        foreach (var b in bytes.Take(bytes.Length - 1))
        {
            var result = decoder.DecodeBytes(new[] { b });
            if (result.Length > 0)
            {
                Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(result, "^[你好世]$"));
            }
        }

        var finalResult = decoder.DecodeBytes(new[] { bytes.Last() });
        Assert.AreEqual("界", finalResult);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public void DecodeBytes_InvalidUtf8_ProducesReplacementCharacter()
    {
        var decoder = new Utf8StreamDecoder();

        // 0xFF is never valid in UTF-8
        var invalidBytes = new byte[] { 0xFF, 0xFE };
        var result = decoder.DecodeBytes(invalidBytes);

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
        var incompleteBytes = new byte[] { 0xE4 };
        var result = decoder.DecodeBytes(incompleteBytes);
        Assert.AreEqual(string.Empty, result);

        var flushed = decoder.Flush();
        StringAssert.Contains(flushed, "�");
    }
}
