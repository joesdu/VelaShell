using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Services;
using VelaShell.Terminal.Rendering;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Services;

[TestClass]
[TestCategory("SharedPasswordFill")]
public sealed class SharedPasswordFillTests
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SharedPasswordFillTests).Assembly);

    [TestMethod]
    [DataRow("Password:", true)]
    [DataRow("Enter password: ", true)]
    [DataRow("[sudo] password for joe:", true)]
    [DataRow("joe@host's password:", true)]
    [DataRow("密码：", true)]
    [DataRow("请输入密码：", true)]
    [DataRow("Verification code:", false)]
    [DataRow("OTP:", false)]
    [DataRow("Enter passphrase for key 'x':", false)]
    [DataRow("log: password:", false)]
    [DataRow("$ echo password:", false)]
    [DataRow("Password: typed", false)]
    // git / svn 把对象引起来,URL 里的冒号不能挡住识别(#638)。
    [DataRow("Password for 'https://git.example.com:8000': ", true)]
    [DataRow("Password for 'https://joe@git.example.com:8000':", true)]
    [DataRow("Password for \"https://git.example.com:8443\":", true)]
    [DataRow("Password for user postgres:", true)]
    [DataRow("Username for 'https://git.example.com:8000':", false)]
    [DataRow("Password for 'https://git.example.com:8000': typed", false)]
    [DataRow("Password for 'https://a': 'b':", false)]
    // sudo-rs(Ubuntu 25.10 起的默认 sudo):标签后面是 PAM 的原话,输错一次之后照样再问一遍。
    [DataRow("[sudo: authenticate] Password:", true)]
    [DataRow("[sudo: authenticate] Password: ", true)]
    [DataRow("[sudo-rs: authenticate] Password:", true)]
    [DataRow("[su: authenticate] Password:", true)]
    [DataRow("[sudo: authenticate] 密码：", true)]
    // 本地化的经典 sudo 把用户名放在前面;doas 把用户与主机括起来。
    [DataRow("[sudo] joe 的密码：", true)]
    [DataRow("[sudo] joe のパスワード:", true)]
    [DataRow("[sudo] joe의 암호:", true)]
    [DataRow("doas (joe@host) password:", true)]
    // 标签只认 sudo 一族;别的方括号开头、标签后面不是口令的,都不算。
    [DataRow("[joe@host ~] password:", false)]
    [DataRow("[sudo: authenticate] Verification code:", false)]
    [DataRow("[sudo: authenticate] Password: typed", false)]
    [DataRow("sudo: Authentication failed, try again.", false)]
    public void PromptDetection_IsConservative(string line, bool expected) =>
        Assert.AreEqual(expected, InteractivePromptDetector.IsPasswordFillPrompt(line));

    [TestMethod]
    public Task Fill_UsesRealBridgeAndSessionEncoding_WithoutTypedInput_AndSubmitsInTheSameWrite() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        control.SetEncoding(Encoding.Unicode);
        control.Feed("Password:"u8.ToArray());
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        var id = Guid.NewGuid();
        repository.GetPasswordAsync(id).Returns("口令!42");
        int typed = 0;
        control.TypedInput += _ => typed++;
        using var peerControl = new VelaTerminalControl();
        IShellStreamWrapper peerShell = Shell();
        await using var peer = new TerminalTabViewModel(peerControl, peerShell) { ConnectionStatus = SessionStatus.Connected };
        var sync = new SyncInputCoordinator();
        sync.Attach(tab);
        sync.Attach(peer);
        tab.JoinSyncChannel(SyncInputChannel.A);
        peer.JoinSyncChannel(SyncInputChannel.A);
        var writes = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        using var request = new SharedPasswordFillRequest(tab, () => true);
        Assert.AreEqual(PasswordFillResult.Queued, await request.FillAsync(repository, id));
        await DrainWritesAsync(tab);
        // 正文按会话字符集编码,后面紧跟 Enter 键的字节(与真按 Enter 一样不转码);
        // 同一次写出,用户选完顺手敲的字符插不到密码与回车之间。
        Assert.AreSequenceEqual([.. Encoding.Unicode.GetBytes("口令!42"), (byte)'\r'], [.. writes]);
        await shell.Received(1).WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(0, typed);
        await DrainWritesAsync(peer);
        await peerShell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        sync.Detach(tab);
        sync.Detach(peer);
        Assert.AreEqual("", tab.InputTracker.CurrentInput);
        Assert.AreEqual(PasswordFillResult.Expired, await request.FillAsync(repository, id));
        await repository.Received(1).GetPasswordAsync(id);
    });

    [TestMethod]
    public Task Fill_SubmitsWithTheSameBytesAsTheEnterKey_IncludingNewLineMode() => Session.RunOnUiAsync(async () =>
    {
        // 远端开了换行模式(LNM,CSI 20 h)时 Enter 发 CR LF;替用户按的那一下要与真按 Enter 一致。
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        control.Feed("\e[20hPassword:"u8.ToArray());
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns("secret");
        List<byte> writes = CaptureWrites(shell);
        using var request = new SharedPasswordFillRequest(tab, () => true);
        Assert.AreEqual(PasswordFillResult.Queued, await request.FillAsync(repository, Guid.NewGuid()));
        await DrainWritesAsync(tab);
        Assert.AreEqual("secret\r\n", Encoding.UTF8.GetString([.. writes]));
        Assert.AreSequenceEqual(control.EncodeEnterKey(), "\r\n"u8.ToArray());
    });

    [TestMethod]
    [DataRow("bad\r")]
    [DataRow("bad\n")]
    [DataRow("bad\e")]
    [DataRow("bad\t")]
    [DataRow("中文")]
    public Task InvalidPassword_DoesNotSend(string password) => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        control.SetEncoding(Encoding.ASCII);
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns(password);
        using var request = new SharedPasswordFillRequest(tab, () => true);
        Assert.AreEqual(PasswordFillResult.InvalidPassword, await request.FillAsync(repository, Guid.NewGuid()));
        await DrainWritesAsync(tab);
        await shell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    });

    [TestMethod]
    [DataRow("input")]
    [DataRow("prompt")]
    [DataRow("screen")]
    [DataRow("disconnect")]
    [DataRow("cancel")]
    [DataRow("inactive")]
    [DataRow("encoding")]
    [DataRow("raw")]
    public Task ContextChangeDuringRead_RejectsLatePassword(string change) => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns(pending.Task);
        bool active = true;
        using var request = new SharedPasswordFillRequest(tab, () => active);
        Task<PasswordFillResult> fill = request.FillAsync(repository, Guid.NewGuid());
        switch (change)
        {
            case "input": control.WriteTextInput("x"); break;
            case "raw": tab.Bridge!.SendRaw("other-session-input"u8.ToArray()); break;
            case "prompt": control.Feed("changed"u8.ToArray()); break;
            case "screen": control.Feed("\e[?1049h\e[?1049l"u8.ToArray()); break;
            case "disconnect":
                tab.ConnectionStatus = SessionStatus.Disconnected;
                tab.ConnectionStatus = SessionStatus.Connected;
                break;
            case "cancel": request.Invalidate(); break;
            case "inactive": active = false; break;
            case "encoding": control.SetEncoding(Encoding.Unicode); break;
        }
        pending.SetResult("do-not-leak");
        Assert.AreEqual(PasswordFillResult.Expired, await fill);
        await DrainWritesAsync(tab);
        Assert.DoesNotContain(c => c.GetMethodInfo().Name == nameof(IShellStreamWrapper.WriteAsync)
            && ((byte[])c.GetArguments()[0]!).AsSpan().IndexOf("do-not-leak"u8) >= 0, shell.ReceivedCalls());
    });

    [TestMethod]
    public Task Popup_OpensOnlyOnRealPrompt_ListsUsablePasswords_AndPickingOneFillsAndSubmits() =>
        Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "production", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(
        [
            metadata,
            new(Guid.NewGuid(), "key", "root", AuthMethod.PrivateKey, true),
            new(Guid.NewGuid(), "missing", "", AuthMethod.Password, false),
            new(Guid.NewGuid(), "旧库无口令", "ops", AuthMethod.Password, true),
        ]);
        repository.GetPasswordAsync(metadata.Id).Returns("secret");
        var writes = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            control.Feed("Enter passphrase for key 'x':"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest, "passphrase 不是密码提示,不该弹列表。");
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            // 展开列表只读元数据;滤掉非密码认证与没有口令的条目。
            await repository.Received(1).ListMetadataAsync();
            await repository.DidNotReceive().GetPasswordAsync(Arg.Any<Guid>());
            Assert.AreSequenceEqual(
                ["production", "旧库无口令"], [.. view.PasswordItemsForTest.Select(item => item.Name)]);
            // 锚在光标行本身:列表落在提示行下沿之下,不遮住用户可能正在手敲的那一行,也不漂到别处。
            Rect anchor = view.PasswordPlacementRectForTest;
            Rect cursor = control.GetCursorRect();
            Assert.AreEqual(cursor.X, anchor.X, 0.5, "锚点应与光标同列。");
            Assert.IsTrue(anchor.Top <= cursor.Top && anchor.Bottom >= cursor.Bottom, "锚点矩形应盖住光标行。");
            Rect popup = PopupBounds(view, window);
            Rect cursorInWindow = CursorInWindow(control, window);
            Assert.AreEqual(cursorInWindow.X, popup.X, 0.5, "列表应与光标同列。");
            Assert.IsTrue(popup.Top >= cursorInWindow.Bottom && popup.Top <= cursorInWindow.Bottom + 4,
                $"列表应紧贴光标行下沿:popup={popup} cursor={cursorInWindow}");
            // 点一行即读取、填入并提交:密码正文紧跟一次回车,不带引号 —— 不用再按 Enter。
            view.PickPasswordItem(metadata);
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            Assert.AreEqual("secret\r", Encoding.UTF8.GetString([.. writes]));
            await repository.Received(1).GetPasswordAsync(metadata.Id);
            // 填完即收口:留着只会让人再点一次。
            Assert.IsFalse(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    /// <summary>
    /// sudo-rs(Ubuntu 25.10 起的默认 sudo)的提示是 <c>[sudo: authenticate] Password:</c>:原先只认经典 sudo 的
    /// <c>[sudo] password for joe:</c>,列表不弹,只能从命令面板手动打开。手输错一次之后,sudo 先回一个换行,
    /// 等完 PAM 的失败延迟再报错、再问一遍 —— 同一句提示,列表要再给一次。
    /// </summary>
    [TestMethod]
    public Task Popup_OpensForSudoRsPrompt_AndAgainAfterAFailedAttempt() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "joe", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            Dispatcher.UIThread.RunJobs();
            using WriteableBitmap? frame = window.CaptureRenderedFrame();
            control.Focus();
            control.Feed("joe@p3tiny:~$ sudo apt update -y\r\n[sudo: authenticate] Password: "u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, "sudo-rs 的口令提示应自动弹出共享密码列表。");
            // 用户没点列表、自己敲了口令:收口,同一提示不再冒出来。
            control.WriteTextInput("x");
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            // 回车后 sudo 先单独回一个换行,失败延迟过后报错并再问一遍。
            control.Feed("\r\n"u8.ToArray());
            Paint(window);
            control.Feed("sudo: Authentication failed, try again.\r\n[sudo: authenticate] Password: "u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, "输错之后的重新提示应再给一次列表。");
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Popup_StaysClosedWhenContextIsWrong_AndReopensOnlyForANewPrompt() =>
        Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            Dispatcher.UIThread.RunJobs();
            using WriteableBitmap? frame = window.CaptureRenderedFrame();
            // 终端没焦点:按键会去别处,不能弹出只走鼠标的列表。
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            control.Focus();
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Assert.IsTrue(control.IsFocused, "列表不得把焦点从终端抢走。");
            // 用户开始手敲同一提示:收口,且不再反复重开。
            control.WriteTextInput("x");
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest, "同一提示已开始手输时不能再次冒出入口。");
            // 备用屏(vim 等)里没有 shell 提示符这回事。
            control.Feed("\e[?1049h\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            // 回主屏后又是另一条提示(另一个主机/用户):这是新的一次询问,该给入口。
            control.Feed("\e[?1049l\r\e[2Kjoe@host's password:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, "换了一条提示应重新给出入口。");
            // 回滚到历史里时弹层失去锚点意义,收口。
            control.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat("x", control.Rows * 2))));
            Paint(window);
            control.ScrollOffset = control.MaxScrollOffset;
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            // 切走活动标签页:列表不能留在别人的终端上。
            control.ScrollOffset = 0;
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Popup_RecognizesGitPromptWrappedAcrossRows() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(
        [
            new(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true),
        ]);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            // 主机名比一整行还长:提示被自动换行折开,光标行只剩 "….example.com:8000': " 这截尾巴(#638)。
            string prompt = $"Password for 'https://{new string('g', control.Columns)}.example.com:8000': ";
            control.Feed(Encoding.UTF8.GetBytes(prompt));
            Paint(window);
            Assert.IsFalse(InteractivePromptDetector.IsPasswordFillPrompt(control.GetBufferLine(control.CursorRow)),
                           "前提:光标行单看认不出,提示确实折行了。");
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            // Esc 记下的是整段逻辑行:后续输出批次不能把同一条提示再弹出来。
            Press(window, Key.Escape, PhysicalKey.Escape);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            control.Feed("\e[0m"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest, "收口过的折行提示不该反复重开。");
            // 续行被原地重写成一条完整提示:上一行的折行标志已经陈旧,接起来反而认不出 —— 光标行自己要认得。
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Popup_RecognizesPromptWhoseTrailingSpaceWrappedOntoTheNextRow() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(
        [
            new(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true),
        ]);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            // 提示正好占满一行,尾随空格折到下一行:光标行只剩一个空格。输出批次的廉价预筛只看光标行,
            // 光标行全空白时必须照旧去拼上面的逻辑行,不能当成「不以冒号收尾」放掉。
            const string head = "Password for 'https://";
            string prompt = head + new string('g', control.Columns - head.Length - 2) + "':";
            Assert.AreEqual(control.Columns, prompt.Length);
            control.Feed(Encoding.UTF8.GetBytes(prompt + " "));
            Paint(window);
            Assert.AreEqual(1, control.CursorRow, "前提:尾随空格折到了下一行。");
            Assert.AreEqual(" ", control.GetBufferLine(control.CursorRow));
            Assert.IsTrue(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Popup_OnLastRow_FlipsAboveThePromptLine() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(
        [
            new(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true),
            new(Guid.NewGuid(), "staging", "ops", AuthMethod.Password, true),
        ]);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            // 输出刷满一屏后提示符落在末行(最大化时的常态):下面放不下,定位器把列表翻到上面去。
            control.Feed(Encoding.UTF8.GetBytes(new string('\n', control.Rows * 2) + "[sudo] password for joe: "));
            Paint(window);
            Assert.AreEqual(control.Rows - 1, control.CursorRow, "前提:提示符在末行。");
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Rect popup = PopupBounds(view, window);
            Rect cursor = CursorInWindow(control, window);
            Assert.IsTrue(popup.Bottom <= cursor.Top && popup.Bottom >= cursor.Top - 4,
                $"翻上去的列表应止于提示行上沿,不能盖住它:popup={popup} cursor={cursor}");
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Popup_KeyThatExpiresTheRequest_ClosesWithoutSuppressing_AndRePromptGetsAFreshRequest() =>
        Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        repository.GetPasswordAsync(metadata.Id).Returns("secret");
        List<byte> writes = CaptureWrites(shell);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("[sudo] password for joe: "u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            // 空口令上的 Backspace:跟踪的输入没变(InputChanged 不响),但字节已经发出去、请求随之作废。
            // 列表不能留着让人点 —— 点哪一项都只会回「已过期」。
            Press(window, Key.Back, PhysicalKey.Backspace);
            Assert.IsFalse(view.PasswordPopupOpenForTest, "被键入作废的列表应当收口。");
            // 这不是用户拒绝(Esc 才是):提示再画一次,入口照常回来。
            control.Feed("\r\e[2K[sudo] password for joe: "u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, "Backspace 不该把这一行记成已拒绝。");
            // 输错后重新提示:开着的列表换成新请求,点一下照样能填,而不是回「已过期」。
            control.Feed("\r\nSorry, try again.\r\n[sudo] password for joe: "u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            view.PickPasswordItem(metadata);
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            Assert.AreEqual("\u007Fsecret\r", Encoding.UTF8.GetString([.. writes]));
            Assert.IsFalse(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    /// <summary>
    /// 不经键入的出站输入(同步输入转来的、防空闲在计时器线程上发的 NUL)同样让列表收起 ——
    /// 曾经只认 TypedInput,这两种输入之后列表还开着,点哪项都只回「已过期」(plan.md §222 留下的收尾)。
    /// 收起不算用户拒绝:提示再画一次,入口照常回来。
    /// </summary>
    [TestMethod]
    [DataRow("sync")]
    [DataRow("timer-thread")]
    public Task Popup_OutboundInputThatBypassesTyping_ClosesTheListWithoutSuppressing(string source) =>
        Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true)]);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);

            if (source == "sync")
            {
                // 同步输入频道里别的标签转来的字节:走桥的 SendRaw,不触发本标签的 TypedInput。
                tab.WriteSyncInput("x"u8.ToArray());
            }
            else
            {
                // 防空闲的 NUL 由计时器线程入队:事件在那条线程上响,要封送回 UI 线程再收。
                await Task.Run(() => tab.Bridge!.SendRaw([0]));
            }
            Paint(window);

            Assert.IsFalse(view.PasswordPopupOpenForTest, "出站输入之后请求已作废,列表不该还开着。");
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, "这不是用户拒绝,提示再画一次入口要回来。");
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    /// <summary>
    /// 输出规则的自动应答走终端的后台输入口(不清选区、不拉回视图),但它照样是一次出站输入:
    /// 开着的请求要作废、桥的输入代次要前进 —— 否则应答之后点一项,密码就跟在 yes 后面交出去了。
    /// </summary>
    [TestMethod]
    public Task AutoResponse_ExpiresAnOpenRequest_LikeAnyOtherOutboundInput() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        control.Feed("Password:"u8.ToArray());
        using var request = new SharedPasswordFillRequest(tab, () => true);
        int expired = 0;
        request.Expired += () => expired++;
        long generation = tab.Bridge!.InputGeneration;
        Assert.IsTrue(request.IsValid, "前提:请求此刻有效。");

        control.WriteBackgroundInput("yes"u8.ToArray(), submit: true);

        Assert.AreEqual(1, expired);
        Assert.IsFalse(request.IsValid);
        Assert.IsGreaterThan(generation, tab.Bridge.InputGeneration);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns("secret");
        Assert.AreEqual(PasswordFillResult.Expired, await request.FillAsync(repository, Guid.NewGuid()));
    });

    [TestMethod]
    public Task PalettePicker_TakesTerminalFocus_SoKeysReachTheList() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        repository.GetPasswordAsync(metadata.Id).Returns("secret");
        List<byte> writes = CaptureWrites(shell);
        var palette = new TextBox();
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository, palette);
        try
        {
            Paint(window);
            // 从命令面板执行:焦点在面板的搜索框上,面板先收起、再执行命令,没人把焦点还给终端。
            palette.Focus();
            Paint(window);
            palette.IsVisible = false;
            Assert.IsFalse(control.IsFocused, "前提:焦点不在终端上。");
            view.OpenSharedPasswordPicker();
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Assert.IsTrue(control.IsFocused, "手动入口应把焦点交回终端,键位才到得了列表。");
            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.AreEqual(0, view.PasswordHighlightForTest);
            Press(window, Key.Enter, PhysicalKey.Enter);
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            Assert.AreEqual("secret\r", Encoding.UTF8.GetString([.. writes]));
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Picker_ReopenOrPlatformClose_DoesNotLeakRequestSubscriptions() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(
        [
            new(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true),
        ]);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            int typed = Subscribers(control, nameof(VelaTerminalControl.TypedInput));
            int output = Subscribers(control, nameof(VelaTerminalControl.OutputUpdated));
            // 列表开着时在命令面板里再点一次手动入口:旧请求要先作废,不能连同订阅一起留在控件上。
            view.OpenSharedPasswordPicker();
            view.OpenSharedPasswordPicker();
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Assert.AreEqual(typed + 1, Subscribers(control, nameof(VelaTerminalControl.TypedInput)));
            Assert.AreEqual(output + 1, Subscribers(control, nameof(VelaTerminalControl.OutputUpdated)));
            // 平台侧收起弹层(不经本类的收口):Closed 里统一收尾。
            view.FindControl<Popup>("PasswordPopup")!.IsOpen = false;
            Assert.AreEqual(typed, Subscribers(control, nameof(VelaTerminalControl.TypedInput)));
            Assert.AreEqual(output, Subscribers(control, nameof(VelaTerminalControl.OutputUpdated)));
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task ListingFailure_SaysReadFailed_NotThatNothingExists() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(Task.FromException<List<SharedCredentialMetadata>>(new IOException("database is locked")));
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Assert.IsEmpty(view.PasswordItemsForTest);
            // 读不出来不是「没有」:不能叫用户去设置里添加一份其实存在的凭据。
            Assert.AreEqual(Strings.Get("PasswordFill_ReadFailed"), view.PasswordStatusForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Keyboard_ArrowsOnlyHighlight_EnterFillsAndSubmits_LaterKeysStayOutOfThePassword() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var prod = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        var staging = new SharedCredentialMetadata(Guid.NewGuid(), "staging", "ops", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([prod, staging]);
        repository.GetPasswordAsync(staging.Id).Returns("secret");
        List<byte> writes = CaptureWrites(shell);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("Password for 'https://git.example.com:8000': "u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Assert.AreEqual(-1, view.PasswordHighlightForTest, "不预先高亮:提示符前直接回车是用户自己的提交。");
            // ↓ 落到第一项,↑ 首尾循环。只挪高亮:不读密码、不发送,方向键的 ESC 序列也不漏进口令。
            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.AreEqual(0, view.PasswordHighlightForTest);
            Press(window, Key.Up, PhysicalKey.ArrowUp);
            Assert.AreEqual(1, view.PasswordHighlightForTest);
            await DrainWritesAsync(tab);
            Assert.IsEmpty(writes);
            await repository.DidNotReceive().GetPasswordAsync(Arg.Any<Guid>());
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            // Enter 填入高亮的那一项并提交:正文紧跟一次回车,不用再按一次。
            Press(window, Key.Enter, PhysicalKey.Enter);
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            Assert.AreEqual("secret\r", Encoding.UTF8.GetString([.. writes]));
            await repository.Received(1).GetPasswordAsync(staging.Id);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            // 列表已收口:之后的按键都在提交之后、原样进终端,混不进口令(原先这里还得再按一次 Enter,
            // 中间顺手敲的字符就成了密码的一部分)。
            window.KeyTextInput("x");
            Press(window, Key.Enter, PhysicalKey.Enter);
            await DrainWritesAsync(tab);
            Assert.AreEqual("secret\rx\r", Encoding.UTF8.GetString([.. writes]));
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Keyboard_ClickingTheHighlightedRowStillFills() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        repository.GetPasswordAsync(metadata.Id).Returns("secret");
        List<byte> writes = CaptureWrites(shell);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.AreEqual(0, view.PasswordHighlightForTest);
            // 先用 ↓ 看了一眼,再拿鼠标点同一行:选中态不变就不会有 SelectionChanged,这一下不能白点。
            // 走真实鼠标输入:headless 下弹层是窗口里的覆盖层,自造的 RaiseEvent 指针事件不会让列表项选中。
            Control row = view.FindControl<ListBox>("PasswordList")!.ContainerFromIndex(0)!;
            Point point = row.TranslatePoint(new Point(20, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            Assert.AreEqual("secret\r", Encoding.UTF8.GetString([.. writes]));
            Assert.IsFalse(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Keyboard_EnterWithoutHighlight_GoesToTerminalAndReadsNothing() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(
        [
            new(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true),
        ]);
        List<byte> writes = CaptureWrites(shell);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Press(window, Key.Enter, PhysicalKey.Enter);
            await DrainWritesAsync(tab);
            Assert.AreEqual("\r", Encoding.UTF8.GetString([.. writes]));
            await repository.DidNotReceive().GetPasswordAsync(Arg.Any<Guid>());
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Keyboard_EnterDuringRead_IsSwallowedSoItCannotOvertakeThePassword() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(metadata.Id).Returns(pending.Task);
        List<byte> writes = CaptureWrites(shell);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Press(window, Key.Enter, PhysicalKey.Enter);
            await repository.Received(1).GetPasswordAsync(metadata.Id);
            // 解密还没回来,用户已经按下第二个 Enter(习惯性的「提交」)。放过去它就先于密码到达远端,
            // 交上去的是空口令,这次键入还会让在途的填入作废。方向键同样原地不动。
            // 填入自己会带一次回车,所以最后只该有一个 CR。
            Press(window, Key.Enter, PhysicalKey.Enter);
            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.AreEqual(-1, view.PasswordHighlightForTest);
            await DrainWritesAsync(tab);
            Assert.IsEmpty(writes);
            pending.SetResult("secret");
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            Assert.AreEqual("secret\r", Encoding.UTF8.GetString([.. writes]));
            Assert.IsFalse(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task LatePassword_KeepsPopupOpenAndSaysNothingWasSent() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(metadata.Id).Returns(pending.Task);
        var writes = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Dispatcher.UIThread.RunJobs();
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, $"光标行=[{control.GetBufferLine(control.CursorRow)}]");
            view.PickPasswordItem(metadata);
            await Task.Yield();
            // 读取期间别处有输入注入(同步输入的另一会话)→ 请求作废。这里不能用键入来造:
            // 键入本身就会先收口弹层,到不了"失败"分支。迟到的密码不发送,但必须**说明**
            // 没发出去 —— 悄悄收口会让用户以为已经填上了。
            tab.Bridge!.SendRaw("other-session-input"u8.ToArray());
            pending.SetResult("late-secret");
            // 等这次读取与填入整个跑完再断言。迟到结果的续体排在 UI 调度器上,让一次路轮不到它;
            // 原先靠后面 DrainWritesAsync 恰好多跨一跳才赶上,写入排空得快就读到空状态(macOS CI 偶发)。
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            // 唯一的写入是上面那次注入本身,密码没有跟出去。
            Assert.AreEqual("other-session-input", Encoding.UTF8.GetString([.. writes]));
            Assert.IsTrue(view.PasswordPopupOpenForTest, "失败要留着弹层,否则状态文字随弹层一起消失。");
            Assert.AreEqual(Strings.Get("PasswordFill_Expired"), view.PasswordStatusForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Picker_CancelWhileReading_DoesNotSendLateResult() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns([metadata]);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(metadata.Id).Returns(pending.Task);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Paint(window);
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            // 先确认真走到了读取:弹层没开时点选什么都不做,下面的「没发」就白断言了。
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            view.PickPasswordItem(metadata);
            await Task.Yield();
            await repository.Received(1).GetPasswordAsync(metadata.Id);
            // 读取在途:此刻用户按 Esc 收口(或切走窗口),迟到的密码不得再发。
            view.ClosePasswordPopupForTest();
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            pending.SetResult("late-secret");
            // 必须等迟到结果真的被处理过,下面的「没发」才有分量;只让一次路,断言时它可能还没跑。
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            await shell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task ReconnectDuringRead_DoesNotSendToOldOrNewTransport() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper oldShell = Shell();
        await using var tab = new TerminalTabViewModel(control, oldShell) { ConnectionStatus = SessionStatus.Connected };
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns(pending.Task);
        using var request = new SharedPasswordFillRequest(tab, () => true);
        Task<PasswordFillResult> result = request.FillAsync(repository, Guid.NewGuid());
        IShellStreamWrapper newShell = Shell();
        await tab.AttachTransportAsync(newShell);
        pending.SetResult("late-secret");
        Assert.AreEqual(PasswordFillResult.Expired, await result);
        await DrainWritesAsync(tab);
        await oldShell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await newShell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    });

    [TestMethod]
    public Task DeferredFocus_DoesNotStealFocusAfterTabSwitchOrDialogOpen() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell());
        using var otherControl = new VelaTerminalControl();
        await using var otherTab = new TerminalTabViewModel(otherControl, Shell());
        var main = new MainWindowViewModel();
        System.Reflection.PropertyInfo active = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ActiveTerminalTab))!;
        active.SetValue(main, tab);
        var view = new TerminalTabView { DataContext = tab };
        var input = new TextBox();
        var panel = new StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(view);
        var window = new Window { Width = 800, Height = 480, Content = panel, DataContext = main };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            input.Focus();
            view.FocusTerminal();
            active.SetValue(main, otherTab);
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(input.IsFocused, "排队后切换标签不得抢回焦点。");
            active.SetValue(main, tab);
            view.FocusTerminal();
            var dialogInput = new TextBox();
            var dialog = new Window { Content = dialogInput };
            Task result = dialog.ShowDialog(window);
            dialogInput.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(dialogInput.IsFocused, "排队后打开模态窗口不得抢回焦点。");
            dialog.Close();
            await result;
        }
        finally
        {
            window.Close();
        }
    });

    private static Task DrainWritesAsync(TerminalTabViewModel tab) =>
        (Task)typeof(Terminal.SshTerminalBridge)
            .GetMethod("DrainWritesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(tab.Bridge, null)!;

    private static IShellStreamWrapper Shell()
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(false);
        shell.CanWrite.Returns(true);
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return shell;
    }

    /// <summary>
    /// 把标签页挂进一个真实窗口,并把凭据仓储接到 <see cref="TerminalTabView.RepositoryFactory" /> 上。
    /// headless 宿主(<c>VelaHeadlessApp</c>)不是 <c>App</c>,没有 DI 容器,所以那条取数口必须替换。
    /// 返回值里的 <see cref="IDisposable" /> 负责还原。<paramref name="overlay" /> 是盖在终端上面、另一个能拿焦点的
    /// 控件(命令面板之类);与主窗口里的命令面板一样同格叠放,收起它不改变终端的布局。
    /// </summary>
    private static (Window Window, TerminalTabView View, IDisposable Restore) ShowTerminal(
        TerminalTabViewModel tab,
        ISharedCredentialRepository repository,
        Control? overlay = null)
    {
        var main = new MainWindowViewModel();
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ActiveTerminalTab))!.SetValue(main, tab);
        var view = new TerminalTabView { DataContext = tab };
        Control content = view;
        if (overlay is not null)
        {
            overlay.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            content = new Grid { Children = { view, overlay } };
        }
        var window = new Window { Width = 800, Height = 480, DataContext = main, Content = content };
        window.Show();
        Func<ISharedCredentialRepository?> original = TerminalTabView.RepositoryFactory;
        TerminalTabView.RepositoryFactory = () => repository;
        return (window, view, new Restore(original));
    }

    /// <summary>跑一遍待处理作业并重绘一帧,让 OutputUpdated 之后的界面状态落定。</summary>
    private static void Paint(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
    }

    /// <summary>经真实输入管线按一次键:落到聚焦的终端,先过视图的隧道键位。</summary>
    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Paint(window);
    }

    /// <summary>密码列表在窗口坐标里的实际位置(headless 下弹层是窗口里的覆盖层,可以直接换算)。</summary>
    private static Rect PopupBounds(TerminalTabView view, Window window)
    {
        Control child = view.FindControl<Popup>("PasswordPopup")!.Child!;
        return new Rect(child.TranslatePoint(default, window)!.Value, child.Bounds.Size);
    }

    /// <summary>光标格在窗口坐标里的矩形。</summary>
    private static Rect CursorInWindow(VelaTerminalControl control, Window window)
    {
        Rect cursor = control.GetCursorRect();
        return new Rect(control.TranslatePoint(cursor.TopLeft, window)!.Value, cursor.Size);
    }

    /// <summary>控件某个事件上挂着几个订阅者(查请求退订没有)。</summary>
    private static int Subscribers(VelaTerminalControl control, string eventName) =>
        (typeof(VelaTerminalControl)
            .GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(control) as Delegate)?.GetInvocationList().Length ?? 0;

    /// <summary>收集发往远端的全部字节(密码与按键都走这一个出口)。</summary>
    private static List<byte> CaptureWrites(IShellStreamWrapper shell)
    {
        var writes = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        return writes;
    }

    private sealed class Restore(Func<ISharedCredentialRepository?> original) : IDisposable
    {
        public void Dispose() => TerminalTabView.RepositoryFactory = original;
    }
}
