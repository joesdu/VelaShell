using System.Text;
using Avalonia.Input;
using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 含 <c>{{变量}}</c> 的快捷命令:先问、再替换、再发;取消就一个字节都不发。
/// </summary>
[TestClass]
[TestCategory("QuickCommands")]
public class QuickCommandVariablesFlowTests
{
    static QuickCommandVariablesFlowTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Placeholders_AreAskedFor_AndTheRenderedCommandIsSent(bool executeAfterSend)
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        string? askedName = null;
        QuickCommandTemplate? askedTemplate = null;
        vm.QuickCommandVariablePrompt = (name, template, _) =>
        {
            askedName = name;
            askedTemplate = template;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(
                new Dictionary<string, string> { ["pod"] = "web-1", ["ns"] = "prod" });
        };

        Send(vm, "查看日志", "kubectl logs -f {{pod}} -n {{ns=default}}", executeAfterSend);

        emulator.Received(executeAfterSend ? 1 : 0).WriteKeyInput(Key.Enter, KeyModifiers.None);

        Assert.AreEqual("查看日志", askedName);
        Assert.HasCount(2, askedTemplate!.Variables);
        emulator.Received(1).WriteInput(Arg.Is<byte[]>(bytes =>
            Encoding.UTF8.GetString(bytes) == "kubectl logs -f web-1 -n prod"));
    }

    [TestMethod]
    public void CancellingThePrompt_SendsNothing()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        bool focusRequested = false;
        vm.TerminalFocusRequested += (_, _) => focusRequested = true;
        vm.QuickCommandVariablePrompt = (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        Send(vm, "查看日志", "kubectl logs -f {{pod}}", executeAfterSend: true);

        emulator.DidNotReceive().WriteKeyInput(Arg.Any<Key>(), Arg.Any<KeyModifiers>());
        emulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        Assert.IsFalse(focusRequested);
    }

    [TestMethod]
    public void CommandsWithoutPlaceholders_AreSentWithoutAsking()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        bool asked = false;
        vm.QuickCommandVariablePrompt = (_, _, _) =>
        {
            asked = true;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        };

        Send(vm, "状态", "docker inspect -f '{{.State.Status}}' web");

        Assert.IsFalse(asked, "Go 模板的 {{.State.Status}} 不是占位,不该弹框");
        emulator.Received(1).WriteInput(Arg.Is<byte[]>(bytes =>
            Encoding.UTF8.GetString(bytes) == "docker inspect -f '{{.State.Status}}' web"));
    }

    [TestMethod]
    public void TargetDisconnectedWhileAsking_IsSkipped()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        TerminalTabViewModel tab = vm.TerminalTabs.Single();
        var answer = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>();
        vm.QuickCommandVariablePrompt = (_, _, _) => answer.Task;

        Send(vm, "查看日志", "kubectl logs -f {{pod}}", executeAfterSend: true);
        tab.ConnectionStatus = SessionStatus.Disconnected;
        answer.SetResult(new Dictionary<string, string> { ["pod"] = "web-1" });

        emulator.DidNotReceive().WriteKeyInput(Arg.Any<Key>(), Arg.Any<KeyModifiers>());
        emulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
    }

    [TestMethod]
    public void SecondSend_PrefillsWhatWasTypedTheFirstTime()
    {
        (MainWindowViewModel vm, ITerminalEmulator _) = Connected();
        var command = new QuickCommand { Name = "查看日志", CommandText = "kubectl logs -f {{pod}} -n {{ns=default}} --token={{token}}" };
        List<IReadOnlyDictionary<string, string>> prefills = [];
        vm.QuickCommandVariablePrompt = (_, _, initial) =>
        {
            prefills.Add(initial);
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(
                new Dictionary<string, string> { ["pod"] = "web-1", ["ns"] = "prod", ["token"] = "t0k" });
        };

        Send(vm, command);
        Send(vm, command);

        Assert.AreEqual("", prefills[0]["pod"], "第一次没有可记的,用占位自己的默认值");
        Assert.AreEqual("default", prefills[0]["ns"]);
        Assert.AreEqual("web-1", prefills[1]["pod"], "pod 名常连着重复用:第二次预填上一次的值");
        Assert.AreEqual("prod", prefills[1]["ns"]);
        Assert.AreEqual("", prefills[1]["token"], "像凭据的占位不记");
    }

    [TestMethod]
    public async Task CompletionAndThePanel_AskTheSameWay_AndShareTheMemory()
    {
        (MainWindowViewModel vm, ITerminalEmulator _) = Connected();
        TerminalTabViewModel tab = vm.TerminalTabs.Single();
        var command = new QuickCommand { Name = "查看日志", CommandText = "kubectl logs -f {{pod}}" };
        List<IReadOnlyDictionary<string, string>> prefills = [];
        vm.QuickCommandVariablePrompt = (_, _, initial) =>
        {
            prefills.Add(initial);
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["pod"] = "web-1" });
        };
        // 补全那一路由宿主在建标签时接上;这里手动接上同一个入口(测试里的标签不是宿主建的)。
        tab.QuickCommandTextResolver ??= c => vm.ResolveQuickCommandTextAsync(c.Id, c.Name, c.CommandText);

        Send(vm, command);
        string? rendered = await tab.QuickCommandTextResolver(command);

        Assert.AreEqual("kubectl logs -f web-1", rendered);
        Assert.HasCount(2, prefills);
        Assert.AreEqual("web-1", prefills[1]["pod"], "在快捷命令区填过的值,补全里选同一条时照样预填");
    }

    [TestMethod]
    public async Task Resolve_WithoutPlaceholders_ReturnsTheTextWithoutAsking()
    {
        (MainWindowViewModel vm, ITerminalEmulator _) = Connected();
        bool asked = false;
        vm.QuickCommandVariablePrompt = (_, _, _) =>
        {
            asked = true;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        };

        Assert.AreEqual("uptime", await vm.ResolveQuickCommandTextAsync(Guid.NewGuid(), "x", "uptime"));
        Assert.IsFalse(asked);
    }

    private static void Send(MainWindowViewModel vm, QuickCommand command) =>
        vm.Sidebar.QuickCommands!.SendCommand.Execute(new QuickCommandViewModel(command)).Subscribe();

    private static (MainWindowViewModel Vm, ITerminalEmulator Emulator) Connected()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var vm = new MainWindowViewModel(quickCommands: new QuickCommandsViewModel(repository));
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        var tab = new TerminalTabViewModel(emulator) { ConnectionStatus = SessionStatus.Connected };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        return (vm, emulator);
    }

    private static void Send(MainWindowViewModel vm, string name, string commandText, bool executeAfterSend = false) =>
        vm.Sidebar.QuickCommands!.SendCommand
            .Execute(new QuickCommandViewModel(new QuickCommand { Name = name, CommandText = commandText, ExecuteAfterSend = executeAfterSend }))
            .Subscribe();
}
