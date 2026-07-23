using System.Text;
using Avalonia.Headless;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 幽灵文本剩余部分的现算逻辑(<see cref="VelaTerminalControl.CurrentGhostRemainder" />):
/// 剩余 = 完整候选去掉"其前缀恰为光标左侧已回显文本之后缀"的最长重叠。此值纯由屏幕真实
/// 状态决定,与回显延迟无关,是消除逐键抖动/退格卡顿的关键不变式,故须锁定其边界行为。
/// </summary>
[TestClass]
[TestCategory("Ghost")]
public sealed class GhostTextRemainderTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    private static string? RemainderAfter(string echoed, string full)
    {
        string? result = null;
        _session
            .Dispatch(
                () =>
                {
                    using var control = new VelaTerminalControl();
                    control.Feed(Encoding.ASCII.GetBytes(echoed));
                    control.SetGhostSuggestion(full);
                    result = control.CurrentGhostRemainder();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        return result;
    }

    [TestMethod]
    public void PartiallyTyped_ReturnsTrailingRemainder() =>
        Assert.AreEqual("kout", RemainderAfter("git chec", "git checkout"));

    [TestMethod]
    public void FullyTyped_ReturnsNull() =>
        Assert.IsNull(RemainderAfter("git checkout", "git checkout"));

    [TestMethod]
    public void FullyTypedWithRepeatingPrefix_DoesNotFallBackToShorterOverlap() =>
        // "abcabc" 键满后,末 3 字符 "abc" 偶合候选前缀 "abc",绝不能因此误显 "abc"。
        Assert.IsNull(RemainderAfter("abcabc", "abcabc"));

    [TestMethod]
    public void DivergedInput_ReturnsNull() =>
        Assert.IsNull(RemainderAfter("git chez", "git checkout"));

    [TestMethod]
    public void RemainderBeginsWithSpace_WhenNextSuggestedCharIsSpace() =>
        // 命令里"输入空格的部分":光标停在词末,剩余以空格起头,同样须紧贴光标不跳。
        Assert.AreEqual(" -la", RemainderAfter("ls", "ls -la"));

    [TestMethod]
    public void PromptPrefixedLine_MatchesOnlyTrailingInput() =>
        Assert.AreEqual("kout", RemainderAfter("user@host:~$ git chec", "git checkout"));

    [TestMethod]
    public void EmptyLine_ReturnsNull() =>
        Assert.IsNull(RemainderAfter(string.Empty, "ls -la"));

    [TestMethod]
    public void NoSuggestionSet_ReturnsNull()
    {
        string? result = "sentinel";
        _session
            .Dispatch(
                () =>
                {
                    using var control = new VelaTerminalControl();
                    control.Feed(Encoding.ASCII.GetBytes("git chec"));
                    result = control.CurrentGhostRemainder();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        Assert.IsNull(result);
    }
}
