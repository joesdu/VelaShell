using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public sealed class RemoteEditSessionManagerTests
{
    [TestCleanup]
    public void CleanupRemoteEditSessions() => RemoteEditSessionManager.CleanupAll();

    [TestMethod]
    [TestCategory("ExternalEdit")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape.txt")]
    [DataRow("../escape.txt")]
    [DataRow("nested/name.txt")]
    [DataRow("nested\\name.txt")]
    public async Task OpenAsync_RejectsUnsafeRemoteLeafNameBeforeTempOrEditor(string fileName)
    {
        RemoteEditSessionManager.CleanupAll();
        ISftpService sftpService = Substitute.For<ISftpService>();

        // 数子目录,而不是断言 temp 根不存在:那个根是 %TEMP% 下的固定路径,
        // 上一次运行留下的草稿目录(上传失败时刻意保留的那些)会让"根不存在"永久失败,
        // 而它本来要说的只是"这次调用没在下面建东西"。
        int before = CountTempChildren();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RemoteEditSessionManager.OpenAsync(
                new()
                {
                    SftpService = sftpService,
                    SessionId = Guid.NewGuid(),
                    RemotePath = "/home/user/" + fileName,
                    FileName = fileName,
                    OpenWith = RemoteEditOpenWith.ConfiguredEditor,
                    EditorCommand = "not-a-real-editor",
                }
            )
        );

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), exception.Message);
        await sftpService
            .DidNotReceive()
            .DownloadFileAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
        Assert.AreEqual(before, CountTempChildren(), "非法文件名却已经在 temp 下建了目录。");
    }

    /// <summary>
    /// 经管理器开一个会话(谁也不打开、下载走桩),这样 <c>ActiveSessions</c> 里才有它 ——
    /// 直接 new 出来的 <c>SessionFixture</c> 没在管理器登记,测不了"会话有没有被收掉"。
    /// </summary>
    private static async Task<RemoteEditSession> OpenStubSessionAsync(Func<string, string, Task>? uploadAsync = null)
    {
        RemoteEditSession? session = await RemoteEditSessionManager.OpenAsync(new()
        {
            SftpService = Substitute.For<ISftpService>(),
            SessionId = Guid.NewGuid(),
            RemotePath = "/etc/app.conf",
            FileName = "app.conf",
            OpenWith = RemoteEditOpenWith.Nothing,
            DownloadAsync = async (local, _) =>
            {
                await File.WriteAllTextAsync(local, "original");
                return true;
            },
            UploadAsync = uploadAsync ?? ((_, _) => Task.CompletedTask),
        });
        Assert.IsNotNull(session);
        return session;
    }

    /// <summary>remote-edit 临时根下现有的子目录数(不存在算 0)。</summary>
    private static int CountTempChildren() =>
        Directory.Exists(RemoteEditSessionManager.TempRoot)
            ? Directory.GetFileSystemEntries(RemoteEditSessionManager.TempRoot).Length
            : 0;

    // ———————————————————— 反复保存(#396) ————————————————————
    //
    // 这条链路真正的失败形态不是"一次都不传",而是"传了一次之后就不动了" ——
    // 用户的原话:「第一次保存有效,后面再编辑保存就没有效果了」。
    // 旧用例只覆盖了收尾那一下,没有任何一条按住 watcher 连存几次。

    /// <summary>连着存三次就得传三次。一次都不能少,顺序也不能乱。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task ConsecutiveSaves_AreEachUploaded()
    {
        using var fixture = new SessionFixture();

        await fixture.SaveAndWaitForUploadAsync("first", 1);
        await fixture.SaveAndWaitForUploadAsync("second", 2);
        await fixture.SaveAndWaitForUploadAsync("third", 3);

        CollectionAssert.AreEqual(
            (string[])["first", "second", "third"],
            fixture.UploadedContents,
            "保存了三次,远端只拿到了其中一部分 —— 这就是 #396 报的那个形态。");
    }

    /// <summary>
    /// 「安全保存」(写临时文件 → 替换原文件)也算保存。
    /// </summary>
    /// <remarks>
    /// 很多编辑器不直接改原文件,而是写一个临时文件再顶上去(Notepad--、VS Code、
    /// 带 writebackup 的 vim)。这类保存只会触发 <see cref="FileSystemWatcher.Renamed" />
    /// / <see cref="FileSystemWatcher.Created" />,不订 <c>NotifyFilters.FileName</c> 的话一次都看不见。
    /// </remarks>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task AtomicSave_ViaReplace_IsUploaded()
    {
        using var fixture = new SessionFixture();

        await fixture.AtomicSaveAndWaitForUploadAsync("replaced by the editor", 1);

        Assert.AreEqual("replaced by the editor", fixture.UploadedContents.Single());
    }

    /// <summary>
    /// 「启动即返回」的进程退出之后,同一个文件的后续保存仍然要回传。
    /// </summary>
    /// <remarks>
    /// 旧实现在进程退出时按「3 秒启发式」直接决定生死 —— 单实例编辑器
    /// (Notepad--、Notepad++、VS Code)把文件转交给已有实例后引导进程就退出,
    /// 冷启动慢一点就被误判成"编辑器关了",停 watcher、删临时目录,此后每一次保存无声丢失。
    /// 现在时长只决定"要不要去找接手的实例":找不到就<b>继续守着</b>,绝不据此收摊。
    /// </remarks>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task WhenABootstrapProcessExitsQuickly_TheSessionKeepsWatching()
    {
        using var fixture = new SessionFixture();

        await fixture.SaveAndWaitForUploadAsync("before exit", 1);
        bool closed = await fixture.Session.OnEditorProcessExitedAsync(null, TimeSpan.FromSeconds(1));

        Assert.IsFalse(closed, "引导进程一退就把会话收了 —— #396 的第二条复现路径又回来了。");
        await fixture.SaveAndWaitForUploadAsync("after exit", 2);
        CollectionAssert.AreEqual((string[])["before exit", "after exit"], fixture.UploadedContents);
    }

    /// <summary>
    /// 编辑器活了一阵才退出 = 用户把它关了:会话就此结束,「正在编辑」里那一行要走掉。
    /// </summary>
    /// <remarks>
    /// 用户的原话:「我明明编辑器都关掉了,传输列表还显示正在编辑」。
    /// 上一版把进程退出整个降级成"只补传",于是那一行谁也收不掉。
    /// </remarks>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task WhenTheEditorItselfExits_TheSessionEnds()
    {
        RemoteEditSessionManager.CleanupAll();
        RemoteEditSession session = await OpenStubSessionAsync();

        bool closed = await session.OnEditorProcessExitedAsync(null, TimeSpan.FromMinutes(3));

        Assert.IsTrue(closed);
        Assert.IsEmpty(RemoteEditSessionManager.ActiveSessions,
                       "编辑器关掉了,「正在编辑」里那一行还挂着。");
    }

    /// <summary>编辑器关掉时,末次保存必须先落到远端,再收会话。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task WhenTheEditorExits_ThePendingSaveIsUploadedBeforeClosing()
    {
        RemoteEditSessionManager.CleanupAll();
        List<string> uploads = [];
        RemoteEditSession session = await OpenStubSessionAsync(async (local, _) =>
            uploads.Add(await File.ReadAllTextAsync(local)));

        // 存一次,但不等防抖到点就"关掉编辑器"。
        await File.WriteAllTextAsync(session.LocalPath, "last words");
        for (int i = 0; i < 2_000 && !session.HasPendingChange; i++)
        {
            await Task.Delay(5);
        }

        await session.OnEditorProcessExitedAsync(null, TimeSpan.FromMinutes(3));

        Assert.AreEqual("last words", uploads.SingleOrDefault(),
                        "会话收掉了,可最后那次保存没传上去 —— 这才是真正会丢东西的那一边。");
        Assert.IsEmpty(RemoteEditSessionManager.ActiveSessions);
    }

    /// <summary>防抖窗口里连存两次只传一次,但传的必须是最后那份内容。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task SavesInsideOneDebounceWindow_UploadTheLatestContentOnce()
    {
        using var fixture = new SessionFixture();

        await File.WriteAllTextAsync(fixture.LocalPath, "intermediate");
        await fixture.WaitForPendingAsync();
        await File.WriteAllTextAsync(fixture.LocalPath, "final");
        await fixture.WaitForUploadCountAsync(1);

        Assert.AreEqual("final", fixture.UploadedContents[^1]);
    }

    /// <summary>关掉自动上传:改动照样记账,但不主动传;点了「立即上传」才传。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task WithAutoUploadOff_ChangesAreTrackedButNotUploadedUntilAsked()
    {
        using var fixture = new SessionFixture(autoUpload: false);

        await File.WriteAllTextAsync(fixture.LocalPath, "held back");
        await fixture.WaitForPendingAsync();
        // 防抖窗口是 600ms;等够它再断言"没传",否则测的只是"还没轮到"。
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.IsEmpty(fixture.UploadedContents, "自动上传关着却还是传了。");
        Assert.IsTrue(fixture.Session.HasPendingChange, "改动没被记账,用户会以为它不存在。");

        await fixture.Session.UploadNowAsync();

        Assert.AreEqual("held back", fixture.UploadedContents.Single());
    }

    /// <summary>自动上传从关到开:攒着的那次要当场兑现,不该等用户再存一遍。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task TurningAutoUploadBackOn_FlushesTheHeldChange()
    {
        using var fixture = new SessionFixture(autoUpload: false);

        await File.WriteAllTextAsync(fixture.LocalPath, "held back");
        await fixture.WaitForPendingAsync();

        fixture.Session.AutoUpload = true;
        await fixture.WaitForUploadCountAsync(1);

        Assert.AreEqual("held back", fixture.UploadedContents.Single());
    }

    /// <summary>上传失败之后再点一次「立即上传」要能真的重来一次。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task AfterAFailedUpload_UploadNowRetries()
    {
        using var fixture = new SessionFixture { FailUploads = true };

        await File.WriteAllTextAsync(fixture.LocalPath, "precious");
        await fixture.WaitForStateAsync(RemoteEditState.Failed);

        fixture.FailUploads = false;
        await fixture.Session.UploadNowAsync();

        Assert.AreEqual("precious", fixture.UploadedContents.Single());
        Assert.AreEqual(RemoteEditState.Watching, fixture.Session.Snapshot().State);
    }

    /// <summary>同一个远程文件重复打开只该有一个会话、一份本地副本。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task OpeningTheSameRemoteFileTwice_ReusesOneSession()
    {
        RemoteEditSessionManager.CleanupAll();
        var sessionId = Guid.NewGuid();
        ISftpService sftpService = Substitute.For<ISftpService>();

        RemoteEditRequest Request() => new()
        {
            SftpService = sftpService,
            SessionId = sessionId,
            RemotePath = "/etc/app.conf",
            FileName = "app.conf",
            OpenWith = RemoteEditOpenWith.Nothing,
            DownloadAsync = async (local, _) =>
            {
                await File.WriteAllTextAsync(local, "original");
                return true;
            },
            UploadAsync = (_, _) => Task.CompletedTask,
        };

        RemoteEditSession? first = await RemoteEditSessionManager.OpenAsync(Request());
        RemoteEditSession? second = await RemoteEditSessionManager.OpenAsync(Request());

        Assert.IsNotNull(first);
        Assert.AreSame(first, second, "同一个远程文件开出了两个会话 —— 两个 watcher 对着两份副本互相覆盖。");
        Assert.HasCount(1, RemoteEditSessionManager.ActiveSessions);
    }

    /// <summary>复用时若本地还有没传上去的改动,绝不能拿远端内容盖掉它。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task ReopeningWithUnuploadedChanges_KeepsTheLocalDraft()
    {
        RemoteEditSessionManager.CleanupAll();
        var sessionId = Guid.NewGuid();
        ISftpService sftpService = Substitute.For<ISftpService>();
        List<string> errors = [];

        RemoteEditRequest Request() => new()
        {
            SftpService = sftpService,
            SessionId = sessionId,
            RemotePath = "/etc/app.conf",
            FileName = "app.conf",
            OpenWith = RemoteEditOpenWith.Nothing,
            AutoUpload = false,
            OnError = errors.Add,
            DownloadAsync = async (local, _) =>
            {
                await File.WriteAllTextAsync(local, "server side");
                return true;
            },
            UploadAsync = (_, _) => Task.CompletedTask,
        };

        RemoteEditSession session = (await RemoteEditSessionManager.OpenAsync(Request()))!;
        await File.WriteAllTextAsync(session.LocalPath, "my unsaved work");
        for (int i = 0; i < 2_000 && !session.HasPendingChange; i++)
        {
            await Task.Delay(5);
        }

        await RemoteEditSessionManager.OpenAsync(Request());

        Assert.AreEqual("my unsaved work", await File.ReadAllTextAsync(session.LocalPath),
                        "重新打开时用远端内容盖掉了还没上传的本地改动 —— 那份改动就此没了。");
        Assert.IsNotEmpty(errors, "没告诉用户为什么看到的不是远端最新内容。");
    }

    // ———————————————————— 编辑器退出后的上传收尾 ————————————————————
    //
    // 这里以前是"退出后无条件等 1.5 秒,然后停 watcher、删临时目录"。1.5 秒要同时装下
    // 600ms 防抖 + 等文件解锁 + 一次真实网络上传 —— 慢链路上根本不够。超时的后果不是慢,
    // 而是把用户刚存的内容连同本地副本一起删掉,远端还是旧的,且不报错。

    /// <summary>末次保存还在防抖窗口里就收尾:那一次必须被补传上去。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task Shutdown_FlushesASaveThatIsStillInsideTheDebounceWindow()
    {
        using var fixture = new SessionFixture();

        await fixture.SaveAsync("edited by the user");
        // 防抖是 600ms;这里刻意在它到点之前就收尾。
        bool landed = await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(landed);
        Assert.HasCount(1, fixture.UploadedContents, "防抖窗口里的那次保存被丢掉了。");
        Assert.AreEqual("edited by the user", fixture.UploadedContents[0]);
    }

    /// <summary>上传比旧的 1.5 秒还慢时,收尾要等它真的完成。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task Shutdown_WaitsForAnUploadSlowerThanTheOldFixedDelay()
    {
        using var fixture = new SessionFixture { UploadDelay = TimeSpan.FromSeconds(3) };

        await fixture.SaveAsync("slow link");
        bool landed = await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(landed, "上传还没跑完就宣告收尾完成。");
        Assert.HasCount(1, fixture.UploadedContents);
    }

    /// <summary>上传成功后临时副本才可以删。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task AfterASuccessfulUpload_TheLocalCopyIsCleanedUp()
    {
        using var fixture = new SessionFixture();

        await fixture.SaveAsync("done");
        await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));
        fixture.Session.Dispose();

        Assert.IsFalse(Directory.Exists(fixture.Directory));
    }

    /// <summary>
    /// 上传失败时保留本地副本,并把它在哪儿告诉用户。
    /// </summary>
    /// <remarks>
    /// 远端没拿到这份内容,本地副本就是它唯一的存身之处 —— 旧实现照删不误。
    /// </remarks>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task WhenTheUploadFails_TheDraftIsKeptAndReported()
    {
        using var fixture = new SessionFixture { FailUploads = true };

        await fixture.SaveAsync("precious changes");
        bool landed = await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));
        fixture.Session.Dispose();

        Assert.IsFalse(landed);
        Assert.IsTrue(File.Exists(fixture.LocalPath), "上传失败却把本地副本删了 —— 改动就此丢失。");
        Assert.IsTrue(
            fixture.Errors.Any(e => e.Contains(fixture.LocalPath, StringComparison.Ordinal)),
            "没有告诉用户草稿留在哪儿。");
    }

    /// <summary>没有任何改动就收尾:不该凭空上传一次。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task Shutdown_WithoutAnyEdit_UploadsNothing()
    {
        using var fixture = new SessionFixture();

        Assert.IsTrue(await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30)));
        Assert.IsEmpty(fixture.UploadedContents);
    }

    /// <summary>
    /// 一个直接驱动的编辑会话:不起编辑器进程,上传走可控回调。
    /// </summary>
    private sealed class SessionFixture : IDisposable
    {
        private readonly List<string> _uploads = [];

        /// <param name="autoUpload">
        /// 会话的自动上传开关。<b>只能走构造参数</b>:对象初始化器在构造函数<i>之后</i>才赋值,
        /// 而 watcher 在构造时就挂上了 —— 写成 init 属性的话它永远只会读到默认值。
        /// </param>
        public SessionFixture(bool autoUpload = true)
        {
            Directory = Path.Combine(Path.GetTempPath(), $"vela-extedit-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            LocalPath = Path.Combine(Directory, "app.conf");
            File.WriteAllText(LocalPath, "original");
            Session = new(
                new()
                {
                    SftpService = Substitute.For<ISftpService>(),
                    SessionId = Guid.NewGuid(),
                    RemotePath = "/etc/app.conf",
                    FileName = "app.conf",
                    OpenWith = RemoteEditOpenWith.Nothing,
                    AutoUpload = autoUpload,
                    OnError = Errors.Add,
                    UploadAsync = UploadAsync,
                },
                LocalPath);
        }

        public string Directory { get; }

        public string LocalPath { get; }

        public RemoteEditSession Session { get; }

        /// <summary>每次上传时本地文件的内容(按上传顺序)。</summary>
        public string[] UploadedContents
        {
            get
            {
                lock (_uploads)
                {
                    return [.. _uploads];
                }
            }
        }

        public List<string> Errors { get; } = [];

        /// <summary>模拟慢链路。</summary>
        public TimeSpan UploadDelay { get; init; }

        /// <summary>模拟上传失败(断网、权限)。可中途改回,用来测重试。</summary>
        public bool FailUploads { get; set; }

        /// <summary>写一次文件,并等到 watcher 真的看见它 —— 不用固定 Sleep 赌时序。</summary>
        public async Task SaveAsync(string content)
        {
            await File.WriteAllTextAsync(LocalPath, content);
            await WaitForPendingAsync();
        }

        /// <summary>存一次并等这次回传真的落地(按累计上传次数判定,不靠固定延时)。</summary>
        public async Task SaveAndWaitForUploadAsync(string content, int expectedUploads)
        {
            await File.WriteAllTextAsync(LocalPath, content);
            await WaitForUploadCountAsync(expectedUploads);
        }

        /// <summary>按"安全保存"的方式落盘:写临时文件再替换掉原文件。</summary>
        public async Task AtomicSaveAndWaitForUploadAsync(string content, int expectedUploads)
        {
            string staging = LocalPath + ".tmp";
            await File.WriteAllTextAsync(staging, content);
            File.Replace(staging, LocalPath, null, true);
            await WaitForUploadCountAsync(expectedUploads);
        }

        /// <summary>等 watcher 记下"有一次改动还没传"。</summary>
        public async Task WaitForPendingAsync()
        {
            for (int i = 0; i < 2_000 && !Session.HasPendingChange; i++)
            {
                await Task.Delay(5);
            }
            Assert.IsTrue(Session.HasPendingChange, "文件监视没能在超时内看到这次写入。");
        }

        public async Task WaitForUploadCountAsync(int expected)
        {
            for (int i = 0; i < 3_000 && UploadedContents.Length < expected; i++)
            {
                await Task.Delay(5);
            }
            Assert.AreEqual(expected, UploadedContents.Length,
                            $"等不到第 {expected} 次回传 —— 监视或防抖在第一次之后就不再工作了。");
        }

        public async Task WaitForStateAsync(RemoteEditState state)
        {
            for (int i = 0; i < 3_000 && Session.Snapshot().State != state; i++)
            {
                await Task.Delay(5);
            }
            Assert.AreEqual(state, Session.Snapshot().State);
        }

        public void Dispose()
        {
            Session.Dispose();
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, true);
                }
            }
            catch (IOException)
            {
                // 清理是尽力而为。
            }
        }

        private async Task UploadAsync(string localPath, string remotePath)
        {
            if (UploadDelay > TimeSpan.Zero)
            {
                await Task.Delay(UploadDelay);
            }
            if (FailUploads)
            {
                throw new IOException("connection reset");
            }
            string content = await File.ReadAllTextAsync(localPath);
            lock (_uploads)
            {
                _uploads.Add(content);
            }
        }
    }
}
