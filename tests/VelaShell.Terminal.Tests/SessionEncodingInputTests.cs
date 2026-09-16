using System.Text;
using Avalonia.Input;
using VelaShell.Core.Models;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 会话字符集对<b>输入</b>一侧的约束:发出去的字节必须与解码对端输出用的是同一套编码。
/// <para>
/// 从前输入写死 UTF-8,只有解码那一侧跟着设置走。于是把会话设成 GBK 之后,远端输出能正常
/// 显示,而键入的中文过去就是乱码 —— 更糟的是远端的行编辑(readline / zle)按 <c>LANG</c>
/// 的字符集数「字符」,字节不对时退格会删半个字、光标移动错位,不只是显示问题。
/// </para>
/// </summary>
[TestClass]
[TestCategory("SessionEncoding")]
public class SessionEncodingInputTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession:每类各起一个时,拆除会互相踩)。</summary>
    private static Avalonia.Headless.HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) =>
        // GBK / Big5 一族在旧代码页里,要注册之后才取得到(应用侧在 Program.Main 注册)。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static void OnUi(Action action) =>
        _session
            .Dispatch(
                () =>
                {
                    action();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

    [TestMethod]
    public void TextInput_IsEncodedWithTheSessionCharset() =>
        OnUi(() =>
        {
            using var control = new VelaTerminalControl();
            control.SetEncoding(Encoding.GetEncoding("GBK"));
            byte[]? sent = null;
            control.UserInput += bytes => sent = bytes;

            control.WriteTextInput("中文");

            Assert.AreSequenceEqual(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }, sent,
                "GBK 会话上键入中文,发出去的必须是 GBK 字节,而不是 UTF-8 的 E4 B8 AD…");
        });

    [TestMethod]
    public void TextInput_StaysUtf8WhenNothingChangedTheEncoding() =>
        OnUi(() =>
        {
            // 默认档不变:绝大多数会话是 UTF-8,这条守住「没人设过就还是原样」。
            using var control = new VelaTerminalControl();
            byte[]? sent = null;
            control.UserInput += bytes => sent = bytes;

            control.WriteTextInput("中文");

            Assert.AreSequenceEqual(Encoding.UTF8.GetBytes("中文"), sent);
        });

    [TestMethod]
    public void SwitchingTheEncoding_MovesInputAndOutputTogether() =>
        OnUi(() =>
        {
            // 状态栏上的热切:只换解码那一侧的话,用户接着键入的字节仍是上一套编码。
            using var control = new VelaTerminalControl();
            byte[]? sent = null;
            control.UserInput += bytes => sent = bytes;

            control.SetEncoding(Encoding.GetEncoding("GBK"));
            control.Feed([0xD6, 0xD0, 0xCE, 0xC4]);
            control.WriteTextInput("中文");

            Assert.AreEqual("中文", control.GetBufferLine(0).TrimEnd(), "输出侧按 GBK 解。");
            Assert.AreSequenceEqual(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }, sent, "输入侧按同一套编。");
        });

    [TestMethod]
    public void AsciiInput_IsByteIdenticalUnderEveryOfferedEncoding() =>
        OnUi(() =>
        {
            // 命令、路径、参数绝大多数是 ASCII;这一条守住「换编码不会悄悄改写它们」。
            byte[] expected = Encoding.ASCII.GetBytes("cd /var/log && ls -la | grep 'x~\\y'");
            foreach (string name in TerminalEncodings.All)
            {
                using var control = new VelaTerminalControl();
                control.SetEncoding(Encoding.GetEncoding(name));
                byte[]? sent = null;
                control.UserInput += bytes => sent = bytes;

                control.WriteTextInput("cd /var/log && ls -la | grep 'x~\\y'");

                Assert.AreSequenceEqual(expected, sent, $"{name} 改写了 ASCII 区。");
            }
        });

    [TestMethod]
    public void KeySequences_StayAsciiUnderANonUtf8Session() =>
        OnUi(() =>
        {
            // 按键是协议序列不是文本,任何字符集里都必须逐字节不变。
            using var control = new VelaTerminalControl();
            control.SetEncoding(Encoding.GetEncoding("Big5"));
            byte[]? sent = null;
            control.UserInput += bytes => sent = bytes;

            Assert.IsTrue(control.WriteKeyInput(Key.Up, KeyModifiers.None));

            Assert.AreSequenceEqual(Encoding.ASCII.GetBytes("\e[A"), sent);
        });

    [TestMethod]
    public void Paste_KeepsBracketMarkersAsciiAndEncodesOnlyTheBody() =>
        OnUi(() =>
        {
            using var control = new VelaTerminalControl();
            control.SetEncoding(Encoding.GetEncoding("GBK"));
            control.Feed(Encoding.ASCII.GetBytes("\e[?2004h"));
            byte[]? sent = null;
            control.UserInput += bytes => sent = bytes;

            control.WritePasteInput("中\n文");

            byte[] expected =
            [
                .. Encoding.ASCII.GetBytes("\e[200~"),
                0xD6, 0xD0, 0x0D, 0xCE, 0xC4,
                .. Encoding.ASCII.GetBytes("\e[201~"),
            ];
            Assert.AreSequenceEqual(expected, sent,
                "括号粘贴标记必须原样是 ASCII,正文按会话字符集;换行仍归一成 \\r。");
        });

    [TestMethod]
    public void UnmappableCharacters_DegradeToQuestionMarkRatherThanCorruptTheStream() =>
        OnUi(() =>
        {
            // GBK 表示不了 emoji。编码器兜底成 '?'(0x3F)—— 与 PuTTY 等同:丢字是字符集的
            // 限制,但字节流必须仍然是合法的、能被对端逐字节读下去的。
            using var control = new VelaTerminalControl();
            control.SetEncoding(Encoding.GetEncoding("GBK"));
            byte[]? sent = null;
            control.UserInput += bytes => sent = bytes;

            control.WriteTextInput("😀");

            Assert.AreSequenceEqual(new byte[] { 0x3F, 0x3F }, sent);
        });
}
