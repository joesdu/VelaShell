using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VelaShell.Terminal.Tests;

/// <summary>连接初始化命令回显抑制器:整块剥除、跨块切分、双次命中、超窗放行、巧合前缀不误扣。</summary>
[TestClass]
public class EchoSuppressorTests
{
    private static readonly string Payload = "prompt_nl() { local c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl";
    private static byte[] Needle => Encoding.UTF8.GetBytes(Payload + "\r\n");

    private static string Run(EchoSuppressor s, params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var chunk in chunks)
            sb.Append(Encoding.UTF8.GetString(s.Process(Encoding.UTF8.GetBytes(chunk))));
        return sb.ToString();
    }

    [TestMethod]
    public void WholeChunk_EchoRemoved_SurroundingsKept()
    {
        var s = new EchoSuppressor(Needle, maxHits: 2, window: TimeSpan.FromSeconds(10));
        var result = Run(s, "banner\r\n " + Payload + "\r\npi@host:~$ ");

        Assert.AreEqual("banner\r\n pi@host:~$ ", result);
    }

    [TestMethod]
    public void TwoOccurrences_BothRemoved()
    {
        var s = new EchoSuppressor(Needle, maxHits: 2, window: TimeSpan.FromSeconds(10));
        var result = Run(s, " " + Payload + "\r\npi@host:~$  " + Payload + "\r\npi@host:~$ ");

        Assert.AreEqual(" pi@host:~$  pi@host:~$ ", result);
    }

    [TestMethod]
    public void SplitAcrossChunks_AtEveryBoundary_EchoStillRemoved()
    {
        var whole = "MOTD\r\n " + Payload + "\r\npi@host:~$ ";
        // 从 needle 中段任意切开(前 4 字节内的切分允许漏抑制,见 MinHold 注释)。
        var bytes = Encoding.UTF8.GetBytes(whole);
        int needleStart = whole.IndexOf(Payload, StringComparison.Ordinal);

        for (int split = needleStart + 4; split < bytes.Length - 1; split++)
        {
            var s = new EchoSuppressor(Needle, maxHits: 2, window: TimeSpan.FromSeconds(10));
            var part1 = Encoding.UTF8.GetString(s.Process(bytes.AsSpan(0, split).ToArray()));
            var part2 = Encoding.UTF8.GetString(s.Process(bytes.AsSpan(split).ToArray()));

            Assert.AreEqual("MOTD\r\n pi@host:~$ ", part1 + part2, $"split={split}");
        }
    }

    [TestMethod]
    public void HitsExhausted_FurtherIdenticalTextPassesThrough()
    {
        var s = new EchoSuppressor(Needle, maxHits: 1, window: TimeSpan.FromSeconds(10));
        var result = Run(s, Payload + "\r\n", Payload + "\r\n");

        Assert.AreEqual(Payload + "\r\n", result);
    }

    [TestMethod]
    public void ExpiredWindow_HeldPrefixReleased()
    {
        var s = new EchoSuppressor(Needle, maxHits: 2, window: TimeSpan.FromMilliseconds(1));
        // 先喂一个会被扣住的前缀,等窗口过期后,后续数据应携带被扣字节原样放行。
        var prefix = Payload[..10];
        var first = s.Process(Encoding.UTF8.GetBytes(prefix));
        System.Threading.Thread.Sleep(30);
        var second = s.Process(Encoding.UTF8.GetBytes("XYZ"));

        Assert.AreEqual(prefix + "XYZ",
            Encoding.UTF8.GetString(first) + Encoding.UTF8.GetString(second));
    }

    [TestMethod]
    public void ShortCoincidentalPrefixAtChunkTail_NotHeldBack()
    {
        var s = new EchoSuppressor(Needle, maxHits: 2, window: TimeSpan.FromSeconds(10));
        // 块尾是 needle 的前 2 字节("pr"),低于 MinHold,应立即放行不扣。
        var result = s.Process(Encoding.UTF8.GetBytes("pi@host:~$ pr"));

        Assert.AreEqual("pi@host:~$ pr", Encoding.UTF8.GetString(result));
    }
}
