using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 双栏文档的目录收藏(#621)落盘:只改库里那一条的收藏,与别处整条写回配置的路径互不覆盖;
/// 远程路径照原样收藏(<c>~</c> 按家目录展开、首尾空格不修剪);落盘失败时界面不留下没存上的收藏。
/// </summary>
/// <remarks>用真的 SonnetDB 仓储:几条写回路径之间谁盖掉谁,只有落到同一个库里才看得出来。</remarks>
[TestClass]
[TestCategory("Sftp")]
public sealed class SftpDocumentBookmarkTests
{
    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;
    private string _localDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_bookmarks_{Guid.NewGuid():N}");
        _localDir = Path.Combine(_directory, "local");
        Directory.CreateDirectory(_localDir);
        _engine = SonnetDbTestTemplate.Open(Path.Combine(_directory, "sonnetdb"));
        _repository = new SonnetDbSessionRepository(_engine, new AesSecretProtector(Path.Combine(_directory, "secret.key")));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // 临时目录,删不掉不影响结论。
        }
    }

    /// <summary>
    /// 文档手里那份配置是打开时的副本;之后在编辑框里改了名字和端口,再在文档里加收藏 ——
    /// 曾经整条写回文档那份,把名字和端口悄悄改了回去。
    /// </summary>
    [TestMethod]
    public async Task AddingABookmark_KeepsEditsSavedElsewhereAfterTheDocumentOpened()
    {
        SessionProfile profile = NewProfile();
        await _repository.SaveSessionAsync(profile);
        SessionProfile documentCopy = profile.Clone();
        profile.Name = "renamed";
        profile.Port = 2222;
        await _repository.SaveSessionAsync(profile);

        SftpDocumentViewModel document = await OpenAsync(documentCopy);
        try
        {
            await document.AddBookmarkAsync(Editor("logs", _localDir, "/var/log"));

            SessionProfile stored = (await _repository.GetSessionAsync(profile.Id))!;
            Assert.AreEqual(("renamed", 2222), (stored.Name, stored.Port), "收藏只该改收藏,不能把编辑框存下的改动回滚掉");
            Assert.AreEqual("/var/log", stored.DirectoryBookmarks.Single().RemotePath);
            Assert.AreEqual("/var/log", document.Bookmarks.Single().RemotePath);
            Assert.IsNull(document.BookmarkErrorMessage);
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    /// <summary>同一条连接开着两个文档、各拿一份副本:各加一条收藏,两条都得留下,后加的那个文档也看得到前一条。</summary>
    [TestMethod]
    public async Task TwoDocumentsOnOneConnection_DoNotOverwriteEachOthersBookmarks()
    {
        SessionProfile profile = NewProfile();
        await _repository.SaveSessionAsync(profile);
        SftpDocumentViewModel first = await OpenAsync(profile.Clone());
        SftpDocumentViewModel second = await OpenAsync(profile.Clone());
        try
        {
            await first.AddBookmarkAsync(Editor("logs", _localDir, "/var/log"));
            await second.AddBookmarkAsync(Editor("www", _localDir, "/srv/www"));

            SessionProfile stored = (await _repository.GetSessionAsync(profile.Id))!;
            Assert.AreSequenceEqual(["/var/log", "/srv/www"], [.. stored.DirectoryBookmarks.Select(static b => b.RemotePath)]);
            Assert.AreSequenceEqual(["/var/log", "/srv/www"], [.. second.Bookmarks.Select(static b => b.RemotePath)]);
        }
        finally
        {
            await first.CloseAsync();
            await second.CloseAsync();
        }
    }

    /// <summary>
    /// 需要输密码的连接:文档拿的是登录框交回的副本,资源管理器缓存里那份没有新收藏。
    /// 之后从资源管理器再连一次,连接成功的写回用的是那份旧副本 —— 曾经就此把收藏抹掉。
    /// </summary>
    [TestMethod]
    public async Task ConnectWriteBackFromAStaleCopy_KeepsBookmarksAddedInADocument()
    {
        SessionProfile treeCopy = NewProfile();
        await _repository.SaveSessionAsync(treeCopy);
        SftpDocumentViewModel document = await OpenAsync(treeCopy.Clone());
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ssh.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(new SshSession
            {
                ConnectionInfo = new() { Host = treeCopy.Host, Username = treeCopy.Username, AuthMethod = AuthMethod.Password },
                Status = SessionStatus.Connected,
            });
        try
        {
            await document.AddBookmarkAsync(Editor("logs", _localDir, "/var/log"));

            await new ConnectionWorkflowService(_repository, ssh).ConnectProfileAsync(treeCopy);

            SessionProfile stored = (await _repository.GetSessionAsync(treeCopy.Id))!;
            Assert.IsNotNull(stored.LastConnectedAt, "连接写回本身照常生效");
            Assert.AreEqual("/var/log", stored.DirectoryBookmarks.Single().RemotePath, "整条写回不能带走收藏");
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    /// <summary>传进来的是旧副本时,打开文档就要显示库里已有的收藏,不能让用户以为收藏丢了。</summary>
    [TestMethod]
    public async Task OpeningADocument_ShowsBookmarksTheStaleCopyDoesNotHave()
    {
        SessionProfile profile = NewProfile();
        await _repository.SaveSessionAsync(profile);
        SessionProfile staleCopy = profile.Clone();
        await _repository.UpdateDirectoryBookmarksAsync(profile.Id, bookmarks =>
        {
            bookmarks.Add(new RemoteDirectoryBookmark { Name = "logs", LocalPath = _localDir, RemotePath = "/var/log" });
            return true;
        });

        SftpDocumentViewModel document = await OpenAsync(staleCopy);
        try
        {
            Assert.AreEqual("/var/log", document.Bookmarks.Single().RemotePath);
            Assert.IsTrue(document.HasBookmarks);
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    /// <summary>
    /// 落盘抛出任何异常(不只是 IO / InvalidOperation / Argument 这三种),列表都原样不动、给出提示 ——
    /// 曾经先改界面后落盘,其余异常直接冒出去,没存上的收藏留在界面上。
    /// </summary>
    [TestMethod]
    public async Task SaveFailureOfAnyKind_LeavesNoUnsavedBookmarkBehind()
    {
        SessionProfile profile = NewProfile();
        SftpDocumentViewModel document = await OpenAsync(profile, static (_, _) =>
            Task.FromException<List<RemoteDirectoryBookmark>?>(new NotSupportedException("store is gone")));
        try
        {
            await document.AddBookmarkAsync(Editor("logs", _localDir, "/var/log"));

            Assert.IsEmpty(document.Bookmarks);
            Assert.IsEmpty(document.Profile.DirectoryBookmarks);
            Assert.AreEqual(Strings.Get("Bookmark_SaveFailed"), document.BookmarkErrorMessage);
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    /// <summary><c>~/logs</c> 按远程家目录展开 —— 曾经被当成相对路径,存成「当前目录/~/logs」。</summary>
    [TestMethod]
    public async Task TildePath_ExpandsToTheRemoteHome()
    {
        SessionProfile profile = NewProfile();
        await _repository.SaveSessionAsync(profile);
        SftpDocumentViewModel document = await OpenAsync(profile.Clone());
        try
        {
            await document.AddBookmarkAsync(Editor("logs", _localDir, "~/logs"));

            Assert.AreEqual("/home/joe/logs", document.Bookmarks.Single().RemotePath);
            Assert.AreEqual("/home/joe/logs", (await _repository.GetSessionAsync(profile.Id))!.DirectoryBookmarks.Single().RemotePath);
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    /// <summary>家目录问不到时宁可拒收,也不存一个错的路径。</summary>
    [TestMethod]
    public async Task TildePath_WithUnknownHome_IsRejected()
    {
        SessionProfile profile = NewProfile();
        await _repository.SaveSessionAsync(profile);
        ISftpService sftp = Sftp(home: null);
        SftpDocumentViewModel document = await OpenAsync(profile.Clone(), sftp: sftp);
        try
        {
            await document.AddBookmarkAsync(Editor("logs", _localDir, "~/logs"));

            Assert.IsEmpty(document.Bookmarks);
            Assert.IsEmpty((await _repository.GetSessionAsync(profile.Id))!.DirectoryBookmarks);
            Assert.AreEqual(Strings.Get("Bookmark_HomeUnavailable"), document.BookmarkErrorMessage);
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    /// <summary>首尾空格是目录名的一部分:修剪掉就收藏到了另一个目录。名称照常修剪。</summary>
    [TestMethod]
    public async Task RemotePath_KeepsLeadingAndTrailingSpaces()
    {
        SessionProfile profile = NewProfile();
        await _repository.SaveSessionAsync(profile);
        SftpDocumentViewModel document = await OpenAsync(profile.Clone());
        try
        {
            await document.AddBookmarkAsync(Editor("  data  ", _localDir, "/srv/ data /logs "));

            DirectoryBookmarkViewModel saved = document.Bookmarks.Single();
            Assert.AreEqual("/srv/ data /logs ", saved.RemotePath);
            Assert.AreEqual("data", saved.Name);
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    /// <summary>管理窗口传进来的编辑器照常生效:不依赖文档视图挂在视图模型上的那个回调(文档不在前台时它弹不出框)。</summary>
    [TestMethod]
    public async Task ExplicitEditor_WorksWithoutTheDocumentViewsDialog()
    {
        SessionProfile profile = NewProfile();
        await _repository.SaveSessionAsync(profile);
        SftpDocumentViewModel document = await OpenAsync(profile.Clone());
        try
        {
            Assert.IsNull(document.EditBookmarkDialog, "前提:文档视图从没建过");
            await document.AddBookmarkAsync(Editor("logs", _localDir, "/var/log"));
            DirectoryBookmarkViewModel added = document.Bookmarks.Single();

            await document.EditBookmarkAsync(added, Editor("var", _localDir, "/var"));
            Assert.AreEqual(("var", "/var"), (document.Bookmarks.Single().Name, document.Bookmarks.Single().RemotePath));

            await document.DeleteBookmarkAsync(document.Bookmarks.Single(), _ => Task.FromResult(true));
            Assert.IsEmpty(document.Bookmarks);
            Assert.IsEmpty((await _repository.GetSessionAsync(profile.Id))!.DirectoryBookmarks);
        }
        finally
        {
            await document.CloseAsync();
        }
    }

    private async Task<SftpDocumentViewModel> OpenAsync(
        SessionProfile profile,
        Func<Guid, Func<List<RemoteDirectoryBookmark>, bool>, Task<List<RemoteDirectoryBookmark>?>>? update = null,
        ISftpService? sftp = null)
    {
        var document = new SftpDocumentViewModel(
            profile,
            Guid.NewGuid(),
            static (_, _) => Task.CompletedTask,
            sftp ?? Sftp(home: "/home/joe"),
            new TransferOptions(),
            updateBookmarksAsync: update ?? _repository.UpdateDirectoryBookmarksAsync);
        await document.InitialLoadTask;
        return document;
    }

    private static ISftpService Sftp(string? home)
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        if (home is null)
        {
            sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<string>(new IOException("no home")));
        }
        else
        {
            sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(home);
        }
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        return sftp;
    }

    private static Func<RemoteDirectoryBookmark, Task<RemoteDirectoryBookmark?>> Editor(string name, string local, string remote) =>
        seed => Task.FromResult<RemoteDirectoryBookmark?>(new RemoteDirectoryBookmark
        {
            Id = seed.Id,
            Name = name,
            LocalPath = local,
            RemotePath = remote,
        });

    private static SessionProfile NewProfile() => new()
    {
        ConnectionType = ConnectionType.SFTP,
        Name = "files",
        Host = "files.example.com",
        Port = 22,
        Username = "joe",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };
}
