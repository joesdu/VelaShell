using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Terminal.Rendering;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Services;

/// <summary>
/// 终端命令补全选中一条带 <c>{{变量}}</c> 的快捷命令:先弹与快捷命令区同一个询问框,再把替换好的命令续写进命令行
/// (§132 记过「补全插入的仍是含占位的原文」,这组用例守的是它不再如此)。
/// </summary>
[TestClass]
[TestCategory("CommandSuggestions")]
public sealed class QuickCommandCompletionVariablesTests
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(QuickCommandCompletionVariablesTests).Assembly);

    private static readonly QuickCommand Logs = new()
    {
        Id = Guid.NewGuid(),
        Name = "查看日志",
        CommandText = "kubectl logs -f {{pod}}"
    };

    private static CommandSuggestionProvider Provider(CommandHistoryService history, params QuickCommand[] commands)
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        repository.LoadAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(new QuickCommandLoadResult(new QuickCommandData { Commands = [.. commands] })));
        return new(history, repository);
    }

    [TestMethod]
    public async Task QuickCommandCandidates_CarryTheirCommand_AndKnowWhenToAsk()
    {
        var plain = new QuickCommand { Name = "状态", CommandText = "kubectl get pods" };
        CommandSuggestionProvider provider = Provider(new CommandHistoryService(null), Logs, plain);

        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("kubectl", 8);

        CommandSuggestion logs = items.Single(s => s.Text == Logs.CommandText);
        Assert.AreSame(Logs, logs.Command);
        Assert.IsTrue(logs.HasVariables);
        Assert.IsFalse(items.Single(s => s.Text == plain.CommandText).HasVariables, "没有占位的照旧直接插入");
    }

    [TestMethod]
    public async Task HistoryEntryWithTheSameText_StillAsks()
    {
        // 早先按原文接受过的一条会留在历史里;去重时历史分高、会赢 —— 选中它照样得问变量。
        var history = new CommandHistoryService(null);
        history.Record(Logs.CommandText);
        CommandSuggestionProvider provider = Provider(history, Logs);

        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("kubectl", 8);

        Assert.IsTrue(items.Single(s => s.Text == Logs.CommandText).HasVariables);
    }

    [TestMethod]
    public async Task GoTemplateBraces_AreNotPlaceholders()
    {
        var inspect = new QuickCommand { Name = "状态", CommandText = "docker inspect -f '{{.State.Status}}' web" };
        CommandSuggestionProvider provider = Provider(new CommandHistoryService(null), inspect);

        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("docker", 8);

        Assert.IsFalse(items.Single(s => s.Text == inspect.CommandText).HasVariables);
    }

    [TestMethod]
    public Task Accepting_AsksForTheVariables_AndWritesTheRenderedCommand() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell(out List<byte> writes);
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        QuickCommand? asked = null;
        tab.QuickCommandTextResolver = command =>
        {
            asked = command;
            return Task.FromResult<string?>("kubectl logs -f web-1");
        };
        (Window window, TerminalTabView view) = Show(tab);
        try
        {
            control.WriteTextInput("kub");
            await DrainWritesAsync(tab);

            view.AcceptSuggestionForTest(new CommandSuggestion(Logs.CommandText, null, "QC", Logs));
            await view.QuickCommandAcceptForTest;
            await DrainWritesAsync(tab);

            Assert.AreSame(Logs, asked);
            Assert.AreEqual("kubectl logs -f web-1", Encoding.UTF8.GetString([.. writes]),
                "已键入的 kub 之后只续写替换好的剩余部分,不再是 {{pod}} 原文");
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public Task CancellingThePrompt_WritesNothing() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell(out List<byte> writes);
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        tab.QuickCommandTextResolver = _ => Task.FromResult<string?>(null);
        (Window window, TerminalTabView view) = Show(tab);
        try
        {
            control.WriteTextInput("kub");
            await DrainWritesAsync(tab);
            writes.Clear();

            view.AcceptSuggestionForTest(new CommandSuggestion(Logs.CommandText, null, "QC", Logs));
            await view.QuickCommandAcceptForTest;
            await DrainWritesAsync(tab);

            Assert.IsEmpty(writes, "取消就一个字节都不写,也不把含占位的原文写进去");
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public Task TabDisconnectedWhileAsking_WritesNothing() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell(out List<byte> writes);
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var answer = new TaskCompletionSource<string?>();
        tab.QuickCommandTextResolver = _ => answer.Task;
        (Window window, TerminalTabView view) = Show(tab);
        try
        {
            view.AcceptSuggestionForTest(new CommandSuggestion(Logs.CommandText, null, "QC", Logs));
            tab.ConnectionStatus = SessionStatus.Disconnected;
            answer.SetResult("kubectl logs -f web-1");
            await view.QuickCommandAcceptForTest;

            Assert.IsEmpty(writes);
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public Task PlainCandidate_IsInsertedWithoutAsking() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell(out List<byte> writes);
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        bool asked = false;
        tab.QuickCommandTextResolver = _ =>
        {
            asked = true;
            return Task.FromResult<string?>(null);
        };
        (Window window, TerminalTabView view) = Show(tab);
        try
        {
            var plain = new QuickCommand { Name = "状态", CommandText = "kubectl get pods" };
            view.AcceptSuggestionForTest(new CommandSuggestion(plain.CommandText, null, "QC", plain));
            await DrainWritesAsync(tab);

            Assert.IsFalse(asked);
            Assert.AreEqual("kubectl get pods", Encoding.UTF8.GetString([.. writes]));
        }
        finally
        {
            window.Close();
        }
    });

    private static readonly QuickCommand Mysql = new()
    {
        Id = Guid.NewGuid(),
        Name = "数据库列表",
        CommandText = "mysql -uroot -p{{password}} -e 'show databases'"
    };

    private const string MysqlRendered = "mysql -uroot -pS3cret! -e 'show databases'";

    /// <summary>宿主同一个询问入口:这里填密码 S3cret!。</summary>
    private static MainWindowViewModel Host()
    {
        var vm = new MainWindowViewModel(quickCommands: new QuickCommandsViewModel(Substitute.For<IQuickCommandRepository>()))
        {
            QuickCommandVariablePrompt = (_, _, _) =>
                Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["password"] = "S3cret!" }),
        };
        return vm;
    }

    /// <summary>shell 回显这一行(另起一行、带提示符),再按回车。</summary>
    private static void EchoAndSubmit(VelaTerminalControl control, string line)
    {
        control.Feed(Encoding.UTF8.GetBytes("\r\n$ " + line));
        control.WriteKeyInput(Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.None);
    }

    [TestMethod]
    public Task CredentialValues_FilledInFromCompletion_StayOutOfTheCommandHistory() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell(out _);
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var history = new CommandHistoryService(null);
        tab.CommandLineSubmitted += history.Record;
        MainWindowViewModel host = Host();
        // 与宿主建标签时接的是同一个入口(测试里的标签不是宿主建的)。
        tab.QuickCommandTextResolver = c => host.ResolveQuickCommandTextAsync(c.Id, c.Name, c.CommandText, tab);
        (Window window, TerminalTabView view) = Show(tab);
        try
        {
            control.WriteTextInput("mys");
            view.AcceptSuggestionForTest(new CommandSuggestion(Mysql.CommandText, null, "QC", Mysql));
            await view.QuickCommandAcceptForTest;
            Assert.AreEqual(MysqlRendered, tab.InputTracker.CurrentInput);
            EchoAndSubmit(control, MysqlRendered);

            control.WriteTextInput("ls -la");
            EchoAndSubmit(control, "ls -la");

            Assert.AreSequenceEqual(["ls -la"], history.Entries.ToArray(),
                "填了密码的那一行不进历史(否则下次敲 mysql 补全就把口令端出来);别的命令照记");
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public Task CredentialValues_SentFromThePanel_StayOutOfTheCommandHistory_EvenWhenRecalled() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell(out _);
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var history = new CommandHistoryService(null);
        tab.CommandLineSubmitted += history.Record;
        MainWindowViewModel host = Host();
        host.Layout.AddDocument(new TerminalDocument(tab));
        (Window window, TerminalTabView _) = Show(tab);
        try
        {
            host.Sidebar.QuickCommands!.SendCommand.Execute(new QuickCommandViewModel(Mysql)).Subscribe();
            Assert.AreEqual(MysqlRendered, tab.InputTracker.CurrentInput, "快捷命令区只写正文,回车由用户按");
            EchoAndSubmit(control, MysqlRendered);

            // 之后用 shell 的 ↑ 把它翻出来再跑一遍:这一行改从屏幕上取,照样认得出口令。
            control.WriteKeyInput(Avalonia.Input.Key.Up, Avalonia.Input.KeyModifiers.None);
            EchoAndSubmit(control, MysqlRendered);

            control.WriteTextInput("uptime");
            EchoAndSubmit(control, "uptime");

            Assert.AreSequenceEqual(["uptime"], history.Entries.ToArray());
        }
        finally
        {
            window.Close();
        }
    });

    private static (Window Window, TerminalTabView View) Show(TerminalTabViewModel tab)
    {
        var view = new TerminalTabView { DataContext = tab };
        var window = new Window { Width = 800, Height = 480, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static IShellStreamWrapper Shell(out List<byte> writes)
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(false);
        shell.CanWrite.Returns(true);
        var captured = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        writes = captured;
        return shell;
    }

    private static Task DrainWritesAsync(TerminalTabViewModel tab) =>
        (Task)typeof(Terminal.SshTerminalBridge)
            .GetMethod("DrainWritesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(tab.Bridge, null)!;
}
