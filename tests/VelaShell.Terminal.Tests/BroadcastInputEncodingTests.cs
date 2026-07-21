using System.Text;
using Avalonia.Headless;
using Avalonia.Input;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Broadcast")]
public sealed class BroadcastInputEncodingTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

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
    public void WriteKeyInput_UsesEachTerminalCurrentCursorMode()
    {
        OnUi(() =>
        {
            using var normal = new VelaTerminalControl();
            using var application = new VelaTerminalControl();
            application.Feed(Encoding.ASCII.GetBytes("\e[?1h"));
            byte[]? normalBytes = null;
            byte[]? applicationBytes = null;
            normal.UserInput += bytes => normalBytes = bytes;
            application.UserInput += bytes => applicationBytes = bytes;

            Assert.IsTrue(normal.WriteKeyInput(Key.Up, KeyModifiers.None));
            Assert.IsTrue(application.WriteKeyInput(Key.Up, KeyModifiers.None));

            Assert.AreSequenceEqual(Encoding.ASCII.GetBytes("\e[A"), normalBytes);
            Assert.AreSequenceEqual(Encoding.ASCII.GetBytes("\eOA"), applicationBytes);
        });
    }

    [TestMethod]
    public void WriteTextInput_RaisesTypedAndUserInputWithoutLocalBuffer()
    {
        OnUi(() =>
        {
            using var control = new VelaTerminalControl();
            byte[]? typed = null;
            byte[]? sent = null;
            control.TypedInput += bytes => typed = bytes;
            control.UserInput += bytes => sent = bytes;

            control.WriteTextInput("中文");

            byte[] expected = Encoding.UTF8.GetBytes("中文");
            Assert.AreSequenceEqual(expected, typed);
            Assert.AreSequenceEqual(expected, sent);
            Assert.AreEqual(string.Empty, control.GetBufferLine(control.CursorRow));
        });
    }

    [TestMethod]
    public void WritePasteInput_HonorsBracketedPasteMode()
    {
        OnUi(() =>
        {
            using var control = new VelaTerminalControl();
            control.Feed(Encoding.ASCII.GetBytes("\e[?2004h"));
            byte[]? sent = null;
            control.UserInput += bytes => sent = bytes;

            control.WritePasteInput("one\ntwo");

            Assert.AreSequenceEqual(Encoding.UTF8.GetBytes("\e[200~one\rtwo\e[201~"), sent);
        });
    }
}
