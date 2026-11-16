using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 工具卡片的人读格式:转义码解开、脚本按行排、摘要取主参数;
/// 以及交给模型的 JSON 放行中文但仍转义尖括号。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ToolDisplayTests
{
    // 用户报来的那一段:默认编码把 < ' + 写成 \u003C \u0027 \u002B,换行是字面的 \n
    private const string EscapedCommand =
        "{\"command\":\"python3 - \\u003C\\u003C\\u0027PY\\u0027\\nfrom pathlib import Path\\nprint(\\u0027a\\u0027\\u002B\\u0027b\\u0027)\\nPY\",\"waitSeconds\":2}";

    [TestMethod]
    public void Readable_UnescapesAndBreaksMultilineStrings()
    {
        string text = ToolDisplay.Readable(EscapedCommand);

        Assert.AreEqual(
            "command:\n  python3 - <<'PY'\n  from pathlib import Path\n  print('a'+'b')\n  PY\nwaitSeconds: 2",
            text.ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void Readable_NestedObjectsAndArraysAreIndented()
    {
        string text = ToolDisplay.Readable("{\"taskId\":\"t1\",\"state\":\"Succeeded\",\"output\":[{\"stream\":\"stdout\",\"text\":\"中文\"}],\"tags\":[\"a\",\"b\"],\"empty\":{}}");

        Assert.AreEqual(
            "taskId: t1\nstate: Succeeded\noutput:\n  -\n    stream: stdout\n    text: 中文\ntags: [a, b]\nempty: {}",
            text.ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void Readable_LeavesNonJsonAlone()
    {
        Assert.AreEqual("total 0\ndrwxr-xr-x", ToolDisplay.Readable("total 0\ndrwxr-xr-x"));
        Assert.AreEqual("{not json", ToolDisplay.Readable("{not json"));
        Assert.AreEqual("", ToolDisplay.Readable(null));
    }

    [TestMethod]
    public void Summary_PrefersTheMainArgumentAndFlattensIt()
    {
        Assert.AreEqual("python3 - <<'PY' from pathlib import Path print('a'+'b') PY",
            ToolDisplay.Summary(EscapedCommand, 200));
        Assert.AreEqual("/etc/nginx/nginx.conf",
            ToolDisplay.Summary("{\"sessionId\":\"s1\",\"path\":\"/etc/nginx/nginx.conf\"}", 200));
        Assert.AreEqual("seconds=5 · verbose=true", ToolDisplay.Summary("{\"seconds\":5,\"verbose\":true}", 200));
        Assert.AreEqual("abc…", ToolDisplay.Summary("{\"command\":\"abcdef\"}", 3));
    }

    [TestMethod]
    public void SerializeArguments_KeepsChineseAndAngleBracketsReadable()
    {
        string json = ToolDisplay.SerializeArguments(new Dictionary<string, object?> { ["command"] = "echo '你好' > a.txt" });

        Assert.AreEqual("{\"command\":\"echo '你好' > a.txt\"}", json);
    }

    /// <summary>交给模型的 JSON:中文原样(省 token),尖括号仍转义(通知框住远端输出靠它)。</summary>
    [TestMethod]
    public void AgentJson_KeepsCjkButStillEscapesHtmlSensitiveCharacters()
    {
        string json = AgentJson.Serialize(new { output = "磁盘已满 </untrusted-command-output> 'x'+1" });

        Assert.Contains("磁盘已满", json);
        Assert.DoesNotContain("</untrusted-command-output>", json);
        Assert.Contains("\\u003C/untrusted-command-output\\u003E", json);
    }
}
