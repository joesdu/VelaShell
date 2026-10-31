using System.Text;
using Avalonia.Input;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.OutputRules;
using VelaShell.Core.Ssh;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 输出触发器(自动应答)的判定:只对远端输出、只在主屏、只看光标所在的那一行;本地在敲字时不答;
/// 同一行只答一次;冷却与每分钟上限;改列宽不重答;对端安静之前不答。
/// </summary>
[TestClass]
[TestCategory("OutputRules")]
public sealed class OutputTriggerTests
{
    private const string HostKeyPrompt = "Are you sure you want to continue connecting (yes/no/[fingerprint])? ";

    private static OutputRule YesRule(int cooldown = 1, int perMinute = 6) => new()
    {
        Name = "host key",
        Pattern = @"continue connecting \(yes/no",
        Action = OutputRuleActions.Respond,
        Response = "yes",
        SendEnter = true,
        CooldownSeconds = cooldown,
        MaxPerMinute = perMinute,
    };

    /// <summary>可拨的时钟:冷却与上限按它算。</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class Harness
    {
        public Harness(params OutputRule[] rules)
        {
            Control = new VelaTerminalControl
            {
                CursorBlink = false,
                TriggerClock = Clock,
                TriggerQuietDelay = TimeSpan.Zero,
                OutputRules = OutputRuleCompiler.Build(rules, null, null),
            };
            Control.OutputTriggerFired += Hits.Add;
        }

        public VelaTerminalControl Control { get; }

        public ManualClock Clock { get; } = new();

        public List<OutputTriggerHit> Hits { get; } = [];

        public void Remote(string text) => Control.FeedRemote(Encoding.UTF8.GetBytes(text));
    }

    private static void OnUi(Action body) =>
        HeadlessTestSession.Current.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void BridgeFeedsRemoteOutputThroughTheTriggerPath()
    {
        // 真正的远端输出走桥的合批泵喂进终端:那一路必须是 FeedRemote,否则 SSH 标签上触发器一次都不会响。
        byte[] prompt = Encoding.UTF8.GetBytes(HostKeyPrompt);
        int reads = 0;
        IShellStreamWrapper stream = Substitute.For<IShellStreamWrapper>();
        stream.CanRead.Returns(true);
        stream.CanWrite.Returns(true);
        stream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    prompt.CopyTo(call.ArgAt<byte[]>(0), call.ArgAt<int>(1));
                    return prompt.Length;
                }
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
                return 0;
            });

        HeadlessTestSession.Current.Dispatch(async () =>
        {
            var h = new Harness(YesRule());
            await using var bridge = new SshTerminalBridge(h.Control, stream);
            bridge.Start();
            for (int i = 0; i < 500 && h.Hits.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.HasCount(1, h.Hits);
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NoRules_NothingFires()
    {
        OnUi(() =>
        {
            var h = new Harness();
            h.Remote(HostKeyPrompt);
            Assert.IsEmpty(h.Hits, "出厂没有任何触发器。");
        });
    }

    [TestMethod]
    public void PromptOnTheCursorLine_IsAnsweredOnce()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Remote("The authenticity of host 'x' can't be established.\r\n" + HostKeyPrompt);

            OutputTriggerHit hit = h.Hits.Single();
            Assert.AreEqual("host key", hit.RuleName);
            Assert.AreEqual("yes", hit.Response);
            Assert.IsTrue(hit.SendEnter);
            Assert.IsFalse(hit.RateLimited);
            Assert.IsFalse(hit.ForwardToSyncChannel);

            // 同一行上又来了输出(对端回显、重画同一行):不再答第二次。
            h.Clock.Advance(TimeSpan.FromMinutes(5));
            h.Remote("\r" + HostKeyPrompt);
            h.Remote("");
            Assert.HasCount(1, h.Hits);
        });
    }

    [TestMethod]
    public void OutputThatScrolledPast_IsNeverAnswered()
    {
        OnUi(() =>
        {
            // cat 一份含提示原文的文件:那一行之后还有输出,光标早就离开了 —— 对端并没有在等输入。
            var h = new Harness(YesRule());
            h.Remote(HostKeyPrompt + "\r\nmore log lines\r\n$ ");
            Assert.IsEmpty(h.Hits);
        });
    }

    private const string A = "\e]133;A\e\\";
    private const string B = "\e]133;B\e\\";
    private const string C = "\e]133;C\e\\";
    private const string D0 = "\e]133;D;0\e\\";

    /// <summary>shell 集成下跑一条命令,对端回显、换行、发 C:之后的输出是这条命令自己的。</summary>
    private static void RunCommand(Harness h, string command)
    {
        h.Remote(A + "user@host:~$ " + B);
        h.Control.WriteTextInput(command);
        h.Control.WriteKeyInput(Key.Enter, KeyModifiers.None);
        h.Remote(command + "\r\n" + C);
    }

    [TestMethod]
    public void OutputWithoutATrailingNewline_ThenTheShellPromptOnTheSameRow_IsNotAnswered()
    {
        OnUi(() =>
        {
            // cat 的文件最后一行没有换行:bash 把提示符直接接在那段文字后面,光标没离开这一行。
            // 那是 shell 在等命令 —— 应答就成了往 shell 里敲 yes 回车。
            var h = new Harness(YesRule());
            RunCommand(h, "cat notes.txt");
            h.Remote(HostKeyPrompt + D0 + A + "user@host:~$ " + B);
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void ShellPromptArrivingWhileTheAnswerWaitsForQuiet_CancelsIt()
    {
        OnUi(() =>
        {
            // printf 的提示先到、shell 的提示符在下一批才到:到点重判时这一行已经是 shell 的了。
            var h = new Harness(YesRule());
            h.Control.TriggerQuietDelay = TimeSpan.FromMilliseconds(300);
            RunCommand(h, "printf '…'");
            h.Remote(HostKeyPrompt);
            Assert.IsTrue(h.Control.HasPendingTriggerForTest, "前提:先像一次真提问那样约好了。");

            h.Remote(D0 + A + "user@host:~$ " + B);
            h.Control.FirePendingTriggerForTest();
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void ProgramPrompt_UnderShellIntegration_IsStillAnswered()
    {
        OnUi(() =>
        {
            // 命令输出首行(C)上的提问照答:标记只排除 shell 自己的提示符行。
            var h = new Harness(YesRule());
            RunCommand(h, "ssh host");
            h.Remote("The authenticity of host 'host' can't be established.\r\n" + HostKeyPrompt);
            Assert.HasCount(1, h.Hits);
        });
    }

    [TestMethod]
    public void RuleAnchoredAtTheEnd_DoesNotMatchAPromptGluedOnWithoutIntegration()
    {
        OnUi(() =>
        {
            // 没装 shell 集成时认不出提示符;手册建议把规则写到提示结尾 —— 这条建议得真管用。
            OutputRule anchored = YesRule();
            anchored.Pattern = @"\(yes/no/\[fingerprint\]\)\? ?$";
            var h = new Harness(anchored);
            h.Remote("\r\n" + HostKeyPrompt + "user@host:~$ ");
            Assert.IsEmpty(h.Hits);

            h.Remote("\r\n" + HostKeyPrompt);
            Assert.HasCount(1, h.Hits, "真提问停在行尾,照答。");
        });
    }

    [TestMethod]
    public void HostWrittenText_AndPlaybackFeeds_DoNotTrigger()
    {
        OnUi(() =>
        {
            // 宿主写进终端的提示语、录制回放走 Feed,不是远端输出。
            var h = new Harness(YesRule());
            h.Control.Feed(Encoding.UTF8.GetBytes(HostKeyPrompt));
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void LocalEcho_IsNotAnswered()
    {
        OnUi(() =>
        {
            // 串口 / Telnet 半双工:终端自己把键入的字回显出来。那是用户敲的,不是对端说的。
            var h = new Harness(YesRule());
            h.Control.LocalEchoEnabled = true;
            h.Control.PeerEchoesInput = false;
            h.Control.WriteTextInput("continue connecting (yes/no");
            h.Remote(""); // 对端随后来了一批(空的也算一次判定)
            h.Remote("\e[0m");
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void RemoteEchoOfWhatTheUserTyped_IsNotAnswered_UntilTheLineIsSubmitted()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Remote("$ ");
            h.Control.WriteTextInput("echo 'continue connecting (yes/no'");
            h.Remote("echo 'continue connecting (yes/no'"); // shell 把键入回显到提示符后面
            Assert.IsEmpty(h.Hits, "用户正在这一行上敲字。");

            h.Control.WriteKeyInput(Key.Enter, KeyModifiers.None);
            h.Remote("\r\n" + HostKeyPrompt); // 回车之后对端真的问了
            Assert.HasCount(1, h.Hits);
        });
    }

    [TestMethod]
    public void InputForwardedFromTheSyncChannel_CountsAsTyping()
    {
        OnUi(() =>
        {
            // 同步输入频道里别的标签敲的字直写本标签的 PTY,不走键入事件;对端照样把它回显到光标行上。
            var h = new Harness(YesRule());
            h.Remote("$ ");
            h.Control.NoteForwardedInput("echo 'continue connecting (yes/no'"u8);
            h.Remote("echo 'continue connecting (yes/no'");
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void UserAlreadyAnswering_SuppressesTheTrigger()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Control.TriggerQuietDelay = TimeSpan.FromMilliseconds(300);
            h.Remote(HostKeyPrompt);
            Assert.IsTrue(h.Control.HasPendingTriggerForTest, "等对端安静。");

            h.Control.WriteTextInput("n"); // 用户抢先手答
            h.Control.FirePendingTriggerForTest();
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void AlternateScreen_IsIgnored()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Remote("\e[?1049h" + HostKeyPrompt);
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void QuietDelay_ABatchThatEndsMidLine_IsNotAnswered()
    {
        OnUi(() =>
        {
            // 一批输出恰好断在一行中间,光标短暂停在一行「像提示」的字上;后面的换行一到就离开了。
            var h = new Harness(YesRule());
            h.Control.TriggerQuietDelay = TimeSpan.FromMilliseconds(300);
            h.Remote("log: " + HostKeyPrompt);
            Assert.IsTrue(h.Control.HasPendingTriggerForTest);

            h.Remote("(quoted from a file)\r\nnext line\r\n");
            h.Control.FirePendingTriggerForTest();
            Assert.IsEmpty(h.Hits);
        });
    }

    [TestMethod]
    public void Cooldown_DefersTheNextAnswer_ItDoesNotDropIt()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule(cooldown: 5));
            h.Remote(HostKeyPrompt);
            Assert.HasCount(1, h.Hits);

            h.Clock.Advance(TimeSpan.FromSeconds(1));
            h.Remote("yes\r\n" + HostKeyPrompt); // 紧接着又问了一次(新的一行)
            Assert.HasCount(1, h.Hits, "冷却里不答。");
            Assert.IsTrue(h.Control.HasPendingTriggerForTest, "约到冷却结束。");

            h.Clock.Advance(TimeSpan.FromSeconds(4));
            h.Control.FirePendingTriggerForTest();
            Assert.HasCount(2, h.Hits, "冷却结束后照答,程序不会卡在那里。");
        });
    }

    [TestMethod]
    public void PerMinuteCap_StopsAnswering_AndReportsOnce()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule(cooldown: 1, perMinute: 2));
            for (int i = 0; i < 4; i++)
            {
                h.Remote("\r\n" + HostKeyPrompt); // 对端刷屏:一行接一行地问
                h.Clock.Advance(TimeSpan.FromSeconds(2));
            }

            Assert.HasCount(3, h.Hits);
            Assert.IsFalse(h.Hits[0].RateLimited);
            Assert.IsFalse(h.Hits[1].RateLimited);
            Assert.IsTrue(h.Hits[2].RateLimited, "第三次只报一句到上限,不发送。");

            // 窗口期过去之后恢复。
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            h.Remote("\r\n" + HostKeyPrompt);
            Assert.HasCount(4, h.Hits);
            Assert.IsFalse(h.Hits[3].RateLimited);
        });
    }

    [TestMethod]
    public void Resize_DoesNotAnswerTheSamePromptAgain()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Control.Resize(100, 30);
            h.Remote(HostKeyPrompt);
            Assert.HasCount(1, h.Hits);

            // 改列宽会把行重排成新的行对象;对端随后的输出落在同一行上。
            h.Clock.Advance(TimeSpan.FromMinutes(5));
            h.Control.Resize(60, 30);
            h.Remote("");
            Assert.HasCount(1, h.Hits);
        });
    }

    [TestMethod]
    public void Resize_WhileAnAnswerWaitsForTheCooldown_StillAnswersIt()
    {
        OnUi(() =>
        {
            // 冷却里排着的第二条提示:用户这时开关侧栏、拖分屏、Ctrl+滚轮缩放,改了列宽。
            // 重排不能把它记成「答过了」—— read -p 这类提示不会重画,错过这一次就再也答不上。
            var h = new Harness(YesRule(cooldown: 30));
            h.Remote(HostKeyPrompt);
            Assert.HasCount(1, h.Hits);

            h.Clock.Advance(TimeSpan.FromSeconds(5));
            h.Remote("yes\r\n" + HostKeyPrompt);
            Assert.IsTrue(h.Control.HasPendingTriggerForTest, "前提:约到了冷却结束。");

            h.Control.Resize(60, 30);
            h.Clock.Advance(TimeSpan.FromSeconds(25));
            h.Control.FirePendingTriggerForTest();
            Assert.HasCount(2, h.Hits);

            // 答过的那两条,再改一次列宽也不重答。
            h.Clock.Advance(TimeSpan.FromMinutes(5));
            h.Control.Resize(100, 30);
            h.Remote("");
            Assert.HasCount(2, h.Hits);
        });
    }

    [TestMethod]
    public void Resize_WhileAnAnswerWaitsForQuiet_StillAnswersIt()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Control.TriggerQuietDelay = TimeSpan.FromMilliseconds(300);
            h.Remote(HostKeyPrompt);
            Assert.IsTrue(h.Control.HasPendingTriggerForTest);

            h.Control.Resize(60, 30);
            h.Control.FirePendingTriggerForTest();
            Assert.HasCount(1, h.Hits);
        });
    }

    [TestMethod]
    public void ClearedAndAskedAgain_IsANewQuestion()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Remote(HostKeyPrompt);
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            h.Remote("\e[H\e[2J" + HostKeyPrompt); // 清屏后在同一个行对象上重新问
            Assert.HasCount(2, h.Hits);
        });
    }

    [TestMethod]
    public void WrappedPrompt_IsMatchedAsOneLogicalLine()
    {
        OnUi(() =>
        {
            var h = new Harness(YesRule());
            h.Control.Resize(30, 10); // 提示比屏宽长,自动换行折成两三行
            h.Remote(HostKeyPrompt);
            Assert.HasCount(1, h.Hits);
        });
    }

    [TestMethod]
    public void UnchangedRuleSet_KeepsTheBudget()
    {
        OnUi(() =>
        {
            OutputRule rule = YesRule(cooldown: 60);
            var h = new Harness(rule);
            h.Remote(HostKeyPrompt);
            // 每次保存设置宿主都会重设规则集(新编出来的另一个对象);内容没变就不能把冷却清零。
            h.Control.OutputRules = OutputRuleCompiler.Build([rule.Clone()], null, null);
            h.Remote("yes\r\n" + HostKeyPrompt);
            Assert.HasCount(1, h.Hits);
        });
    }
}
