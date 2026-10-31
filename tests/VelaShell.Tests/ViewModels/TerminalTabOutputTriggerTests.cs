using System.Text;
using Avalonia.Input;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.OutputRules;
using VelaShell.Core.Ssh;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 自动应答的宿主一侧:命中报上来之后发了什么、走的哪条路、同步输入频道转不转、终端上方的提示。
/// 能不能应答(主屏、远端输出、光标行、冷却与上限)由终端控件判定,见 VelaShell.Terminal.Tests 的 <c>OutputTriggerTests</c>。
/// </summary>
[TestClass]
[TestCategory("OutputRules")]
public sealed class TerminalTabOutputTriggerTests
{
    private readonly ITerminalEmulator _terminal = FakeTerminal.Emulator();
    private readonly TerminalTabViewModel _vm;

    public TerminalTabOutputTriggerTests() => _vm = new(_terminal, Substitute.For<IShellStreamWrapper>());

    private void Fire(OutputTriggerHit hit) => _terminal.OutputTriggerFired += Raise.Event<Action<OutputTriggerHit>>(hit);

    private static OutputTriggerHit Hit(string response = "yes", bool enter = true, bool forward = false, bool limited = false) =>
        new(Guid.NewGuid(), "host key", response, enter, forward, limited);

    [TestMethod]
    public void Hit_TypesTheResponseThenPressesEnter_WithoutTheKeystrokeViewReset()
    {
        // 正文与回车一起交给终端的后台输入口:照样经键入出口发出,但不清选区、不把回滚中的视图拉回底部 ——
        // 快捷命令那条路(WriteInput / WriteKeyInput)是给用户自己点出来的输入用的。
        _vm.ConnectionStatus = SessionStatus.Connected;
        var flags = new List<(bool Suppressed, bool Programmatic)>();
        _terminal.When(t => t.WriteBackgroundInput(Arg.Any<byte[]>(), Arg.Any<bool>()))
            .Do(_ => flags.Add((_vm.IsSyncForwardSuppressed, _vm.IsProgrammaticInput)));

        Fire(Hit());

        _terminal.Received(1).WriteBackgroundInput(Arg.Is<byte[]>(b => b.SequenceEqual(Encoding.UTF8.GetBytes("yes"))), true);
        AssertNoKeystrokePath();
        Assert.AreSequenceEqual([(true, true)], flags, "默认不进同步输入频道;整段标成程序注入,补全弹层不弹。");
        Assert.IsFalse(_vm.IsSyncForwardSuppressed);
        Assert.IsFalse(_vm.IsProgrammaticInput);
        Assert.Contains("host key", _vm.AutoResponseNotice, "终端上方要说一声是哪条规则答的。");
    }

    [TestMethod]
    public void Hit_WithForwardChosen_GoesThroughTheSyncChannel()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        bool? suppressed = null;
        _terminal.When(t => t.WriteBackgroundInput(Arg.Any<byte[]>(), Arg.Any<bool>()))
            .Do(_ => suppressed = _vm.IsSyncForwardSuppressed);

        Fire(Hit(forward: true));

        Assert.IsFalse(suppressed, "规则明确选了转发,才经同步输入频道转给别的标签。");
    }

    [TestMethod]
    public void EnterOnly_SendsJustTheKey()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;

        Fire(Hit(response: ""));

        _terminal.Received(1).WriteBackgroundInput(Arg.Is<byte[]>(b => b.Length == 0), true);
        AssertNoKeystrokePath();
    }

    [TestMethod]
    public void ResponseIsEncodedInTheSessionCharset()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        Encoding gbk = CodePagesEncoding();
        _terminal.SessionEncoding.Returns(gbk);

        Fire(Hit(response: "是", enter: false));

        _terminal.Received(1).WriteBackgroundInput(Arg.Is<byte[]>(b => b.SequenceEqual(gbk.GetBytes("是"))), false);
        AssertNoKeystrokePath();
    }

    [TestMethod]
    public void RateLimited_OnlyShowsTheNotice()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;

        Fire(Hit(limited: true));

        _terminal.DidNotReceive().WriteBackgroundInput(Arg.Any<byte[]>(), Arg.Any<bool>());
        AssertNoKeystrokePath();
        Assert.Contains("host key", _vm.AutoResponseNotice);
    }

    [TestMethod]
    public void Disconnected_SendsNothing_AndSaysNothing()
    {
        Fire(Hit());

        _terminal.DidNotReceive().WriteBackgroundInput(Arg.Any<byte[]>(), Arg.Any<bool>());
        AssertNoKeystrokePath();
        Assert.IsNull(_vm.AutoResponseNotice);
    }

    [TestMethod]
    public async Task Disposed_Unsubscribes()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        await _vm.DisposeAsync();

        Fire(Hit());

        _terminal.DidNotReceive().WriteBackgroundInput(Arg.Any<byte[]>(), Arg.Any<bool>());
    }

    [TestMethod]
    public void QuickCommand_StillGoesThroughTheKeystrokePath()
    {
        // 对照:用户点的快捷命令 / 重跑照键入的口径收尾,别被应答的改动带走。
        _vm.ConnectionStatus = SessionStatus.Connected;

        Assert.IsTrue(_vm.TryRerunCommand("ls"));

        _terminal.Received(1).WriteInput(Arg.Any<byte[]>());
        _terminal.Received(1).WriteKeyInput(Key.Enter, KeyModifiers.None);
        _terminal.DidNotReceive().WriteBackgroundInput(Arg.Any<byte[]>(), Arg.Any<bool>());
    }

    /// <summary>应答没走用户键入那条会把视图拉回底部、清掉选区的路。</summary>
    private void AssertNoKeystrokePath()
    {
        _terminal.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        _terminal.DidNotReceive().WriteTextInput(Arg.Any<string>());
        _terminal.DidNotReceive().WriteKeyInput(Arg.Any<Key>(), Arg.Any<KeyModifiers>());
        _terminal.DidNotReceive().WritePasteInput(Arg.Any<string>());
    }

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GBK");
    }
}
