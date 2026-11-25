using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class FileBrowserViewModelTests
{
    private readonly Guid _sessionId;
    private readonly ISftpService _sftpService;
    private readonly FileBrowserViewModel _vm;

    public FileBrowserViewModelTests()
    {
        _sftpService = Substitute.For<ISftpService>();
        _sessionId = Guid.NewGuid();
        _vm = new(_sftpService, _sessionId);
    }

    [TestMethod]
    public async Task MappingNavigation_ReportsFailureAndPreservesLoadedRemoteContents()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToAsync("/home/user");
        RemoteFileInfoViewModel[] rows = [.. _vm.Files];
        _sftpService.ListDirectoryAsync(_sessionId, "/denied", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<List<RemoteFileInfo>>(new UnauthorizedAccessException("denied")));
        Assert.IsFalse(await _vm.NavigateForUploadMappingAsync("/denied", CancellationToken.None));
        Assert.AreEqual("/home/user", _vm.CurrentPath);
        Assert.IsTrue(rows.SequenceEqual(_vm.Files));
        Assert.IsNotNull(_vm.ErrorMessage);
    }

    [TestMethod]
    public async Task MappingNavigation_CannotCommitAfterCancellationWhenBackendIgnoresIt()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToAsync("/home/user");
        var delayed = new TaskCompletionSource<List<RemoteFileInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sftpService.ListDirectoryAsync(_sessionId, "/late", Arg.Any<CancellationToken>()).Returns(delayed.Task);
        using var cancellation = new CancellationTokenSource();
        Task<bool> pending = _vm.NavigateForUploadMappingAsync("/late", cancellation.Token);
        cancellation.Cancel();
        delayed.SetResult([]);
        Assert.IsFalse(await pending);
        Assert.AreEqual("/home/user", _vm.CurrentPath);
        Assert.IsTrue(_vm.Files.Any(file => file.Name == "readme.txt"));
        Assert.IsFalse(_vm.IsDirectoryLoading);
    }

    private static List<RemoteFileInfo> CreateTestFiles()
    {
        return
        [
            new()
            {
                Name = "documents",
                FullPath = "/home/user/documents",
                Size = 4096,
                Permissions = "drwxr-xr-x",
                IsDirectory = true,
                LastModified = DateTime.UtcNow.AddHours(-1),
                Owner = "user",
                Group = "user",
            },
            new()
            {
                Name = "readme.txt",
                FullPath = "/home/user/readme.txt",
                Size = 1234,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddDays(-2),
                Owner = "user",
                Group = "user",
            },
            new()
            {
                Name = "photo.jpg",
                FullPath = "/home/user/photo.jpg",
                Size = 3567890,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddMinutes(-30),
                Owner = "user",
                Group = "user",
            },
        ];
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task ListDirectory_PopulatesFilesCollection()
    {
        List<RemoteFileInfo> testFiles = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(testFiles));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        // Leading ".." parent row (§6), then name-ascending with directories grouped first.
        Assert.HasCount(4, _vm.Files);
        Assert.AreEqual("..", _vm.Files[0].Name);
        Assert.IsTrue(_vm.Files[0].IsParentEntry);
        Assert.AreEqual("documents", _vm.Files[1].Name);
        Assert.AreEqual("photo.jpg", _vm.Files[2].Name);
        Assert.AreEqual("readme.txt", _vm.Files[3].Name);
    }

    /// <summary>表头的剩余空间:进了目录就查(statvfs);查不到(FTP、服务端不支持)时不显示。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task FreeSpace_IsShownAfterNavigating_AndHiddenWhenUnknown()
    {
        _sftpService.ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _sftpService.GetSpaceAsync(_sessionId, "/data", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RemoteSpaceInfo?>(new RemoteSpaceInfo(50UL << 30, 12UL << 30, IsReadOnly: false)));
        _sftpService.GetSpaceAsync(_sessionId, "/ftp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RemoteSpaceInfo?>(null));

        await _vm.NavigateToCommand.Execute("/data").FirstAsync();
        Assert.AreEqual(
            Strings.Format("FileBrowser_FreeSpace", RemoteFileInfoViewModel.FormatSize(12L << 30), RemoteFileInfoViewModel.FormatSize(50L << 30)),
            _vm.FreeSpaceText);

        await _vm.NavigateToCommand.Execute("/ftp").FirstAsync();
        Assert.IsNull(_vm.FreeSpaceText, "查不到用量时不显示,而不是留着上一个目录的数");
    }

    /// <summary>上传前预检:目标分区可能放不下时先问;用户说不传就一个都不传。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Upload_AsksFirstWhenTheTargetMayRunOutOfSpace_AndStopsOnNo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "big.bin");
            await File.WriteAllBytesAsync(file, new byte[4096]);
            _sftpService.GetSpaceAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<RemoteSpaceInfo?>(new RemoteSpaceInfo(10_000, 1_000, IsReadOnly: false)));
            string? asked = null;
            _vm.ConfirmLowSpace = message =>
            {
                asked = message;
                return Task.FromResult(false);
            };

            await _vm.UploadLocalPathsAsync([file]);

            Assert.AreEqual(
                Strings.Format("FileBrowser_LowSpaceMessage", RemoteFileInfoViewModel.FormatSize(1_000), RemoteFileInfoViewModel.FormatSize(4096)),
                asked);
            await _sftpService.DidNotReceiveWithAnyArgs().UploadFileAsync(default, default!, default!, default, default, default);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>放得下、或者查不到用量:不问。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow(true, DisplayName = "放得下")]
    [DataRow(false, DisplayName = "查不到用量")]
    public async Task Upload_DoesNotAskWhenItFitsOrSpaceIsUnknown(bool known)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "small.bin");
            await File.WriteAllBytesAsync(file, new byte[100]);
            _sftpService.GetSpaceAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(known ? new RemoteSpaceInfo(10_000, 1_000, IsReadOnly: false) : null));
            bool asked = false;
            _vm.ConfirmLowSpace = _ =>
            {
                asked = true;
                return Task.FromResult(false);
            };

            await _vm.UploadLocalPathsAsync([file]);

            Assert.IsFalse(asked);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NavigateIntoFolder_UpdatesCurrentPath()
    {
        List<RemoteFileInfo> rootFiles = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rootFiles));
        var subFiles = new List<RemoteFileInfo>
        {
            new()
            {
                Name = "report.pdf",
                FullPath = "/home/user/documents/report.pdf",
                Size = 524288,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            },
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subFiles));
        await _vm.NavigateToCommand.Execute("/home/user/documents").FirstAsync();
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);
        Assert.HasCount(2, _vm.Files);
        Assert.AreEqual("..", _vm.Files[0].Name);
        Assert.AreEqual("report.pdf", _vm.Files[1].Name);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Activate_OnDirectory_NavigatesInto()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]); // "documents", IsDirectory
        await _vm.ActivateCommand.Execute(dir).FirstAsync();
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Activate_OnFile_DoesNotNavigate()
    {
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // "readme.txt", not a directory
        await _vm.ActivateCommand.Execute(file).FirstAsync();
        Assert.AreEqual("/", _vm.CurrentPath);
        await _sftpService
            .DidNotReceive()
            .ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_NavigatesToWorkingDirectory()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/root"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/root", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.LoadInitialCommand.Execute().FirstAsync();
        Assert.AreEqual("/root", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_FallsBackToRoot_WhenWorkingDirectoryUnavailable()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("no cwd"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.LoadInitialCommand.Execute().FirstAsync();
        Assert.AreEqual("/", _vm.CurrentPath);
    }

    /// <summary>
    /// 配置里的「默认打开路径」(FTP / FTPS 高级选项)排在候选路径的第一位:
    /// 上传目标常年是同一个目录,而 FTP 给的登录工作目录往往就是根。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_PrefersTheConfiguredInitialPath_OverTheWorkingDirectory()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/var/www/html", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.InitialRemotePath = "/var/www/html";

        await _vm.LoadInitialCommand.Execute().FirstAsync();

        Assert.AreEqual("/var/www/html", _vm.CurrentPath);
    }

    /// <summary>
    /// 路径配错(打错字、目录被删、账号被 chroot)不该把用户堵在一张报错的空白页上:
    /// 依次回退到登录工作目录、根目录,与家目录进不去时回退根目录是同一条纪律。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_WhenTheConfiguredPathCannotBeOpened_FallsBackToTheWorkingDirectory()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/home/deploy"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/nope", Arg.Any<CancellationToken>())
            .Returns<Task<List<RemoteFileInfo>>>(_ => throw new InvalidOperationException("550 No such directory"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/deploy", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.InitialRemotePath = "/nope";

        await _vm.LoadInitialCommand.Execute().FirstAsync();

        Assert.AreEqual("/home/deploy", _vm.CurrentPath);
        Assert.IsNull(_vm.ErrorMessage, "回退成功了就不该再挂着上一次的失败提示。");
    }

    // —— 列显示开关与新增的 所有者/分组/类型 列 ——————————————————————

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void Columns_AreAllVisibleByDefault()
    {
        Assert.IsTrue(_vm.ShowSizeColumn);
        Assert.IsTrue(_vm.ShowPermissionsColumn);
        Assert.IsTrue(_vm.ShowOwnerColumn);
        Assert.IsTrue(_vm.ShowGroupColumn);
        Assert.IsTrue(_vm.ShowTypeColumn);
        Assert.IsTrue(_vm.ShowModifiedColumn);
    }

    /// <summary>Grid 靠把列宽压成 0 来隐藏列:宽度、最小宽度与拖拽条要一起塌缩,漏一个就留白。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void HidingColumn_CollapsesWidthMinWidthAndSplitter()
    {
        _vm.ShowOwnerColumn = false;

        Assert.AreEqual(0, _vm.OwnerGridWidth.Value);
        Assert.AreEqual(0, _vm.OwnerGridMinWidth);
        Assert.AreEqual(0, _vm.OwnerSplitterWidth.Value);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ShowingColumn_RestoresUserWidth()
    {
        _vm.OwnerColumnWidth = new(140);
        _vm.ShowOwnerColumn = false;
        _vm.ShowOwnerColumn = true;

        Assert.AreEqual(140, _vm.OwnerGridWidth.Value);
        Assert.AreEqual(FileBrowserViewModel.MinOwnerWidth, _vm.OwnerGridMinWidth);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void TogglingColumn_ReportsChangeForPersistence()
    {
        List<(string Key, bool Visible)> toggles = [];
        _vm.ColumnVisibilityToggled = (key, visible) => toggles.Add((key, visible));

        _vm.ShowTypeColumn = false;
        _vm.ShowTypeColumn = false; // 同值重复设置不该再报一次

        Assert.HasCount(1, toggles);
        Assert.AreEqual(("type", false), toggles[0]);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ColumnWidth_IsClampedToItsMinimum()
    {
        _vm.OwnerColumnWidth = new(5);
        Assert.AreEqual(FileBrowserViewModel.MinOwnerWidth, _vm.OwnerColumnWidth.Value);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_ByOwner_OrdersAscending_WithDirectoriesFirst()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateOwnedFiles()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        _vm.SortCommand.Execute("owner").Subscribe();

        Assert.AreEqual("owner", _vm.SortColumn);
        Assert.AreEqual("..", _vm.Files[0].Name); // 父目录行仍置顶
        Assert.AreEqual("srv", _vm.Files[1].Name); // 目录仍成组在前(属主 zoe)
        Assert.AreEqual("alice.txt", _vm.Files[2].Name);
        Assert.AreEqual("bob.txt", _vm.Files[3].Name);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FileTypeDisplay_DescribesFoldersFilesAndExtensions()
    {
        // 断言比对本地化资源而非字面量:测试跑在哪个 UI 语言下都成立。
        Assert.AreEqual(Strings.Folder, TypeOf("srv", true));
        Assert.AreEqual(Strings.Format("Sftp_FileTypeExt", "PHP"), TypeOf("index.php", false));

        // 多重扩展名取最后一段。这里顺带盯住 Sftp_FileTypeExt 这条资源本身:
        // 上面那种“两边都走 Strings.Format”的写法在资源缺失时会一起退化成键名而依旧相等。
        Assert.Contains("GZ", TypeOf("archive.tar.gz", false));

        // 无扩展名、点开头的隐藏文件、以及不像扩展名的尾巴,都归为“文件”。
        Assert.AreEqual(Strings.File, TypeOf("README", false));
        Assert.AreEqual(Strings.File, TypeOf(".bashrc", false));
        Assert.AreEqual(Strings.File, TypeOf("backup.tar 副本", false));
        Assert.AreEqual(Strings.File, TypeOf("trailing.", false));
    }

    /// <summary>合成的 ".." 行没有类型/大小/时间可言,这些列对它应为空。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FileTypeDisplay_IsEmptyForParentRow()
    {
        Assert.AreEqual(
            string.Empty,
            RemoteFileInfoViewModel.CreateParentEntry("/home").FileTypeDisplay
        );
    }

    private static string TypeOf(string name, bool isDirectory) =>
        new RemoteFileInfoViewModel(
            new()
            {
                Name = name,
                FullPath = "/tmp/" + name,
                Size = 1,
                Permissions = "-rw-r--r--",
                IsDirectory = isDirectory,
                LastModified = DateTime.UtcNow,
                Owner = "root",
                Group = "root",
            }
        ).FileTypeDisplay;

    private static List<RemoteFileInfo> CreateOwnedFiles() =>
        [
            new()
            {
                Name = "bob.txt",
                FullPath = "/home/user/bob.txt",
                Size = 1,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "bob",
                Group = "staff",
            },
            new()
            {
                Name = "alice.txt",
                FullPath = "/home/user/alice.txt",
                Size = 2,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "alice",
                Group = "staff",
            },
            new()
            {
                Name = "srv",
                FullPath = "/home/user/srv",
                Size = 4096,
                Permissions = "drwxr-xr-x",
                IsDirectory = true,
                LastModified = DateTime.UtcNow,
                Owner = "zoe",
                Group = "staff",
            },
        ];

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_BySize_OrdersAscending_WithDirectoriesFirst()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        // Default sort is name-ascending: ".." pinned first, directory next, then files by name.
        Assert.AreEqual("..", _vm.Files[0].Name);
        Assert.AreEqual("documents", _vm.Files[1].Name);
        _vm.SortCommand.Execute("size").Subscribe();
        Assert.AreEqual("..", _vm.Files[0].Name); // parent row stays pinned
        Assert.AreEqual("documents", _vm.Files[1].Name); // directory stays grouped on top
        Assert.AreEqual("readme.txt", _vm.Files[2].Name); // 1234 bytes
        Assert.AreEqual("photo.jpg", _vm.Files[3].Name); // 3567890 bytes
        Assert.AreEqual("size", _vm.SortColumn);
        Assert.IsFalse(_vm.SortDescending);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_SameColumnTwice_FlipsDirection()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        _vm.SortCommand.Execute("size").Subscribe();
        Assert.IsFalse(_vm.SortDescending);
        _vm.SortCommand.Execute("size").Subscribe();
        Assert.IsTrue(_vm.SortDescending);
        Assert.AreEqual("..", _vm.Files[0].Name); // parent row stays pinned
        Assert.AreEqual("documents", _vm.Files[1].Name); // directory still first
        Assert.AreEqual("photo.jpg", _vm.Files[2].Name); // largest file first
        Assert.AreEqual("readme.txt", _vm.Files[3].Name);
        Assert.AreEqual(" ▼", _vm.SizeSortGlyph);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task GoUp_NavigatesToParentDirectory()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user/documents";
        await _vm.GoUpCommand.Execute().FirstAsync();
        Assert.AreEqual("/home/user", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Refresh_RelistsCurrentDirectory()
    {
        List<RemoteFileInfo> firstList = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(firstList));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        Assert.HasCount(4, _vm.Files); // ".." + 3 entries
        var secondList = new List<RemoteFileInfo> { firstList[0], firstList[1] };
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(secondList));
        await _vm.RefreshCommand.Execute().FirstAsync();
        Assert.HasCount(3, _vm.Files); // ".." + 2 entries
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow(0, "0 B")]
    [DataRow(500, "500.0 B")]
    [DataRow(1230, "1.2 KB")]
    [DataRow(3565158, "3.4 MB")]
    [DataRow(1181116006, "1.1 GB")]
    public void FormatSize_ReturnsHumanReadable(long bytes, string expected) =>
        Assert.AreEqual(expected, RemoteFileInfoViewModel.FormatSize(bytes));

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void RemoteFileInfoViewModel_ExposesPermissions()
    {
        var fileInfo = new RemoteFileInfo
        {
            Name = "test.sh",
            FullPath = "/home/user/test.sh",
            Size = 256,
            Permissions = "-rwxr-xr-x",
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Owner = "root",
            Group = "root",
        };
        var vm = new RemoteFileInfoViewModel(fileInfo);
        Assert.AreEqual("-rwxr-xr-x", vm.Permissions);
        Assert.IsFalse(vm.IsDirectory);
        Assert.AreEqual("file", vm.Icon);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ShowTransfers_ReopensTransferToast()
    {
        var toast = new FileTransferViewModel();
        _vm.TransferSink = toast;
        Assert.IsFalse(toast.IsPanelVisible);
        _vm.ShowTransfersCommand.Execute().Subscribe();
        Assert.IsTrue(toast.IsPanelVisible);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ToggleVisibility_TogglesIsVisible()
    {
        Assert.IsFalse(_vm.IsVisible);
        _vm.ToggleVisibilityCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.IsVisible);
        _vm.ToggleVisibilityCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsVisible);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task ErrorHandling_SetsErrorMessage()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/forbidden", Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Task.FromException<List<RemoteFileInfo>>(
                    new UnauthorizedAccessException("Permission denied")
                )
            );
        Exception? thrownEx = null;
        _vm.NavigateToCommand.ThrownExceptions.Subscribe(ex => thrownEx = ex);
        await _vm.NavigateToCommand.Execute("/forbidden").FirstAsync();
        Assert.IsFalse(string.IsNullOrEmpty(_vm.ErrorMessage));
        Assert.Contains("Permission denied", _vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void RemoteFileInfoViewModel_DirectoryShowsSizeAndPlainName()
    {
        var dirInfo = new RemoteFileInfo
        {
            Name = "docs",
            FullPath = "/home/user/docs",
            Size = 4096,
            Permissions = "drwxr-xr-x",
            IsDirectory = true,
            LastModified = DateTime.UtcNow,
            Owner = "user",
            Group = "user",
        };
        var vm = new RemoteFileInfoViewModel(dirInfo);

        // Directories list their reported size; the name is shown plainly (no trailing slash),
        // since the folder icon already distinguishes directories.
        Assert.AreEqual("4.0 KB", vm.FormattedSize);
        Assert.AreEqual("docs", vm.DisplayName);
        Assert.IsTrue(vm.IsDirectory);
        Assert.IsTrue(vm.IsRegularDirectory);
        Assert.AreEqual("folder", vm.Icon);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task GoUp_AtRoot_StaysAtRoot()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/";
        await _vm.GoUpCommand.Execute().FirstAsync();
        Assert.AreEqual("/", _vm.CurrentPath);
    }

    // --- Right-click context-menu actions ---

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFolder_PromptsAndCreatesUnderCurrentPath()
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("docs");
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.NewFolderCommand.Execute().FirstAsync();
        await _sftpService
            .Received(1)
            .CreateDirectoryAsync(_sessionId, "/home/user/docs", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewSymbolicLink_PromptsTargetThenName_AndCreatesUnderCurrentPath()
    {
        _vm.CurrentPath = "/srv/app";
        var prompts = new List<(string Title, string Initial)>();
        var answers = new Queue<string?>(["/srv/app/releases/42", "current"]);
        _vm.PromptForText = (title, initial) =>
        {
            prompts.Add((title, initial));
            return Task.FromResult(answers.Dequeue());
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, "/srv/app", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var row = new RemoteFileInfoViewModel(new()
        {
            Name = "42",
            FullPath = "/srv/app/releases/42",
            Size = 0,
            IsDirectory = true,
            Permissions = "drwxr-xr-x",
            LastModified = DateTime.UtcNow,
            Owner = "user",
            Group = "user"
        });

        await _vm.NewSymbolicLinkCommand.Execute(row).FirstAsync();

        Assert.AreEqual("/srv/app/releases/42", prompts[0].Initial, "右键某一行时,目标预填该行路径。");
        Assert.AreEqual("42", prompts[1].Initial, "名称预填目标的最后一段。");
        await _sftpService
            .Received(1)
            .CreateSymbolicLinkAsync(_sessionId, "/srv/app/current", "/srv/app/releases/42", Arg.Any<CancellationToken>());
    }

    /// <summary>后端不支持链接(插件协议、FTP 服务器没有 SITE SYMLINK)时,错误条如实说出来。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewSymbolicLink_BackendNotSupported_SurfacesTheError()
    {
        _vm.CurrentPath = "/bucket";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("target");
        _sftpService
            .CreateSymbolicLinkAsync(_sessionId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new NotSupportedException("no links here")));

        await _vm.NewSymbolicLinkCommand.Execute(null).FirstAsync();

        Assert.AreEqual("no links here", _vm.ErrorMessage);
    }

    /// <summary>文件夹下载不跟进嵌套的目录链接:链接可以指向 / 或指回祖先。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadRemoteEntries_DoesNotDescendIntoNestedDirectoryLinks()
    {
        string localRoot = Path.Combine(Path.GetTempPath(), $"vela-dl-links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localRoot);
        try
        {
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user/proj", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>
                {
                    new()
                    {
                        Name = "root",
                        FullPath = "/home/user/proj/root",
                        Size = 4096,
                        IsDirectory = true,
                        IsSymbolicLink = true,
                        LinkTarget = "/",
                        Permissions = "lrwxrwxrwx",
                        LastModified = DateTime.UtcNow,
                        Owner = "user",
                        Group = "user"
                    },
                }));
            var proj = new RemoteFileInfoViewModel(new()
            {
                Name = "proj",
                FullPath = "/home/user/proj",
                Size = 4096,
                IsDirectory = true,
                Permissions = "drwxr-xr-x",
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user"
            });

            await _vm.DownloadRemoteEntriesAsync([proj], localRoot);

            await _sftpService
                .DidNotReceive()
                .ListDirectoryAsync(_sessionId, "/home/user/proj/root", Arg.Any<CancellationToken>());
            Assert.IsNull(_vm.ErrorMessage);
        }
        finally
        {
            Directory.Delete(localRoot, true);
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void RemoteFileRow_Symlinks_PickLinkIconsAndSymlinkType()
    {
        static RemoteFileInfoViewModel Row(bool directory, bool link) => new(new()
        {
            Name = "x",
            FullPath = "/x",
            Size = 0,
            IsDirectory = directory,
            IsSymbolicLink = link,
            LinkTarget = link ? "/t" : null,
            Permissions = string.Empty,
            LastModified = DateTime.UtcNow,
            Owner = string.Empty,
            Group = string.Empty
        });

        RemoteFileInfoViewModel directoryLink = Row(true, true);
        RemoteFileInfoViewModel fileLink = Row(false, true);

        Assert.IsTrue(directoryLink.IsDirectoryLink);
        Assert.IsFalse(directoryLink.ShowsFolderIcon);
        Assert.IsTrue(directoryLink.IsRegularDirectory, "指向目录的链接仍可进入、仍用目录名配色。");
        Assert.IsTrue(fileLink.IsFileLink);
        Assert.IsFalse(fileLink.ShowsFileIcon);
        Assert.IsTrue(Row(true, false).ShowsFolderIcon);
        Assert.IsTrue(Row(false, false).ShowsFileIcon);
        Assert.AreEqual(Strings.Get("Sftp_SymlinkType"), directoryLink.FileTypeDisplay);
        Assert.AreEqual("→ /t", directoryLink.LinkTargetTip);
        Assert.IsNull(Row(false, false).LinkTargetTip);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFolder_Cancelled_DoesNothing()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(null);
        await _vm.NewFolderCommand.Execute().FirstAsync();
        await _sftpService
            .DidNotReceive()
            .CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape")]
    [DataRow("../escape")]
    [DataRow("nested/name")]
    [DataRow("nested\\name")]
    public async Task NewFolder_RejectsUnsafeLeafNameBeforeSftp(string name)
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(name);

        await _vm.NewFolderCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
        await _sftpService
            .DidNotReceive()
            .CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFile_PromptsAndCreatesUnderCurrentPath()
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("notes.txt");
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.NewFileCommand.Execute().FirstAsync();
        await _sftpService
            .Received(1)
            .CreateFileAsync(_sessionId, "/home/user/notes.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape")]
    [DataRow("../escape")]
    [DataRow("nested/name")]
    [DataRow("nested\\name")]
    public async Task NewFile_RejectsUnsafeLeafNameBeforeSftp(string name)
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(name);

        await _vm.NewFileCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
        await _sftpService
            .DidNotReceive()
            .CreateFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Rename_RenamesWithinParentDirectory()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("renamed.txt");
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.RenameCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .RenameAsync(
                _sessionId,
                "/home/user/readme.txt",
                "/home/user/renamed.txt",
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape")]
    [DataRow("../escape")]
    [DataRow("nested/name")]
    [DataRow("nested\\name")]
    public async Task Rename_RejectsUnsafeLeafNameBeforeSftp(string name)
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(name);
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);

        await _vm.RenameCommand.Execute(file).FirstAsync();

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
        await _sftpService
            .DidNotReceive()
            .RenameAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_RenamesToDestinationPath()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("/tmp/moved.txt");
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.MoveCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .RenameAsync(
                _sessionId,
                "/home/user/readme.txt",
                "/tmp/moved.txt",
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_WithSeveralSelected_AsksOnceForAFolderAndMovesThemAllIntoIt()
    {
        // 框选一片再"移动到":逐个问完整路径没法用,只问一次目标目录,各自按原名落进去。
        int prompts = 0;
        _vm.PromptForText = (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>("/srv/archive");
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1])); // readme.txt
        _vm.SelectedFiles.Add(new(files[2]));

        await _vm.MoveCommand.Execute(new(files[1])).FirstAsync();

        Assert.AreEqual(1, prompts, "批量移动只该问一次目标目录。");
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[1].FullPath, $"/srv/archive/{files[1].Name}", Arg.Any<CancellationToken>());
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[2].FullPath, $"/srv/archive/{files[2].Name}", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_WhenOneItemFails_StillMovesTheRestAndReportsTheFailure()
    {
        // 批量半途中断比"跑完再报告"更难收拾:一条失败不该把剩下的也拦住。
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("/srv/archive");
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _sftpService
            .RenameAsync(_sessionId, files[1].FullPath, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("permission denied")));
        _vm.SelectedFiles.Add(new(files[1]));
        _vm.SelectedFiles.Add(new(files[2]));

        await _vm.MoveCommand.Execute(new(files[1])).FirstAsync();

        // 失败的那条不该挡住后面的。
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[2].FullPath, $"/srv/archive/{files[2].Name}", Arg.Any<CancellationToken>());
        Assert.IsNotNull(_vm.ErrorMessage);
        Assert.Contains("permission denied", _vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_WithASingleItem_KeepsTheFullPathPromptSoRenamingStillWorks()
    {
        // n=1 时保留老用法:提示的是完整目标路径,可以顺手改名。
        string? prompted = null;
        _vm.PromptForText = (_, initial) =>
        {
            prompted = initial;
            return Task.FromResult<string?>("/tmp/renamed.txt");
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1]));

        await _vm.MoveCommand.Execute(new(files[1])).FirstAsync();

        Assert.AreEqual(files[1].FullPath, prompted, "单个移动应预填完整路径,而不是目录。");
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[1].FullPath, "/tmp/renamed.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CopyTo_WithSeveralSelected_AsksOnceAndCopiesEachIntoTheFolder()
    {
        int prompts = 0;
        _vm.PromptForText = (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>("/srv/backup");
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1]));
        _vm.SelectedFiles.Add(new(files[2]));

        await _vm.CopyToCommand.Execute(new(files[1])).FirstAsync();

        Assert.AreEqual(1, prompts, "批量复制只该问一次目标目录。");
        await _sftpService.Received(1).CopyAsync(
            _sessionId, files[1].FullPath, $"/srv/backup/{files[1].Name}",
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
        await _sftpService.Received(1).CopyAsync(
            _sessionId, files[2].FullPath, $"/srv/backup/{files[2].Name}",
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CopyPath_And_CopyName_WriteToClipboard()
    {
        string? copied = null;
        _vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // readme.txt
        await _vm.CopyPathCommand.Execute(file).FirstAsync();
        Assert.AreEqual("/home/user/readme.txt", copied);
        await _vm.CopyNameCommand.Execute(file).FirstAsync();
        Assert.AreEqual("readme.txt", copied);
    }

    /// <summary>
    /// 空白区右键的「复制当前文件夹路径」不需要选中任何条目 —— 它取的是 <c>CurrentPath</c>。
    /// 没有它,想拿当前目录的路径就得先退回上级、再右键那个文件夹。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CopyCurrentPath_CopiesCurrentDirectoryWithoutSelection()
    {
        string? copied = null;
        _vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        _vm.CurrentPath = "/root/nginx-backup-before-ubuntu-package";
        _vm.SelectedFiles.Clear();

        await _vm.CopyCurrentPathCommand.Execute().FirstAsync();

        Assert.AreEqual("/root/nginx-backup-before-ubuntu-package", copied);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_InvokesViewCallback()
    {
        RemoteFileInfoViewModel? shown = null;
        _vm.ShowFileProperties = (f, _) =>
        {
            shown = f;
            return Task.FromResult<FilePropertiesChange?>(null);
        };
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[0]);
        await _vm.PropertiesCommand.Execute(file).FirstAsync();
        Assert.AreSame(file, shown);
    }

    /// <summary>
    /// 「下载为…」:另存为对话框里点的「替换」就是这次的冲突决定 —— 不再按「文件已存在时」再问一遍、跳过或改名,
    /// 也不把那里原有的同名文件当成半截去续传(它比远端小也一样:那是用户确认要替换掉的文件)。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("ask")]
    [DataRow("skip")]
    [DataRow("rename")]
    public async Task DownloadItem_TheSaveDialogsReplaceIsTheConflictDecision(string policy)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-download-as-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "readme.txt");
            // 比远端(1234 字节)小:常规下载路径会把它当成半截、从第 10 字节接着下。
            await File.WriteAllBytesAsync(target, new byte[10]);
            _vm.TransferOptions.ConflictPolicy = policy;
            _vm.TransferOptions.ResumeEnabled = true;
            _vm.TransferOptions.TransferLogging = false;
            _vm.TransferOptions.NotifyOnComplete = false;
            _vm.ConfirmOverwrite = _ => throw new AssertFailedException("另存为对话框已经问过「要替换吗」,不该再弹冲突框。");
            RemoteFileInfo remote = CreateTestFiles()[1]; // /home/user/readme.txt,1234 字节
            _sftpService.GetFileInfoAsync(_sessionId, remote.FullPath, Arg.Any<CancellationToken>()).Returns(remote);
            _vm.PickSavePathForDownload = _ => Task.FromResult<string?>(target);

            await _vm.DownloadItemCommand.Execute(new RemoteFileInfoViewModel(remote)).FirstAsync();

            await _sftpService.Received(1).DownloadFileAsync(
                _sessionId, remote.FullPath, target, Arg.Any<IProgress<TransferProgress>?>(), 0, Arg.Any<CancellationToken>());
            Assert.ContainsSingle(
c => c.GetMethodInfo().Name == nameof(ISftpService.DownloadFileAsync),
                _sftpService.ReceivedCalls(), "只下这一份,落在用户选的名字上");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnFile_DownloadsToChosenPath()
    {
        _vm.PickSavePathForDownload = _ => Task.FromResult<string?>("C:/local/readme.txt");
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.DownloadItemCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .DownloadFileAsync(
                _sessionId,
                "/home/user/readme.txt",
                "C:/local/readme.txt",
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape.txt")]
    [DataRow("../escape.txt")]
    [DataRow("nested/name.txt")]
    [DataRow("nested\\name.txt")]
    public async Task DownloadAndOpen_RejectsUnsafeRemoteLeafNameBeforeLocalTransfer(string name)
    {
        string? openedPath = null;
        _vm.OpenLocalFile = path =>
        {
            openedPath = path;
            return Task.CompletedTask;
        };
        _sftpService
            .DownloadFileAsync(
                _sessionId,
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(Task.CompletedTask);
        var file = new RemoteFileInfoViewModel(
            new()
            {
                Name = name,
                FullPath = "/home/user/" + name,
                Size = 1,
                IsDirectory = false,
                Permissions = "-rw-r--r--",
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            }
        );

        await _vm.ActivateCommand.Execute(file).FirstAsync();

        Assert.IsNull(openedPath);
        await _sftpService
            .DidNotReceive()
            .DownloadFileAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
    }

    /// <summary>
    /// 双击打开的文件也要被监视,保存后自动回传。
    /// </summary>
    /// <remarks>
    /// #396 的真凶:双击走的是"下载 + 交给系统默认程序",全程没有 watcher,
    /// 而右键「使用默认编辑器打开」是有的 —— 两个入口在界面上长得一模一样。
    /// 报告人先用右键存了一次(成功),之后改用双击,于是看到的现象是
    /// 「第一次保存有效,后面再编辑保存就没有效果了」。
    /// </remarks>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DoubleClick_RegistersAnEditSessionThatUploadsOnSave()
    {
        RemoteEditSessionManager.CleanupAll();
        string? tempDirectory = null;
        try
        {
            string? openedPath = null;
            _vm.OpenLocalFile = path =>
            {
                openedPath = path;
                return Task.CompletedTask;
            };
            _sftpService
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/readme.txt",
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                )
                .Returns(callInfo =>
                {
                    File.WriteAllText(callInfo.ArgAt<string>(2), "from the server");
                    return Task.CompletedTask;
                });

            await _vm.ActivateCommand.Execute(new(CreateTestFiles()[1])).FirstAsync();

            Assert.IsNotNull(openedPath, "双击没有把文件交给系统默认程序打开。");
            RemoteEditSession session = RemoteEditSessionManager.ActiveSessions.Single();
            tempDirectory = Path.GetDirectoryName(session.LocalPath);
            Assert.AreEqual("/home/user/readme.txt", session.RemotePath);
            Assert.AreEqual(openedPath, session.LocalPath,
                            "打开的文件和被监视的文件不是同一个 —— 保存永远传不上去。");

            // 在编辑器里存一次:watcher 必须看见,并把它记成"待回传"。
            await File.WriteAllTextAsync(session.LocalPath, "edited locally");
            for (int i = 0; i < 2_000 && !session.HasPendingChange; i++)
            {
                await Task.Delay(5);
            }
            Assert.IsTrue(session.HasPendingChange, "双击打开的文件保存后没有被监视到。");
        }
        finally
        {
            // 这个会话故意留着一次没上传的改动,收尾时会<b>刻意保留</b>本地副本
            //(那是改动唯一的存身之处)。所以得自己把它删掉,别在 %TEMP% 下积草稿。
            // 限时给短:那次回传要回 UI 线程,而本用例不在 headless UI 线程上,永远等不到 ——
            // 用 CleanupAll() 的 5 秒预算就是白等满 5 秒。会话照样先摘掉、再逐个 Dispose,与超时多久无关。
            await RemoteEditSessionManager.CleanupAllAsync(TimeSpan.FromMilliseconds(200));
            if (tempDirectory is not null && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task OpenWithDefaultEditor_RejectsUnsafeRemoteLeafNameBeforeExternalEdit()
    {
        try
        {
            _vm.GetDefaultEditorPath = () => Task.FromResult<string?>("not-a-real-editor");
            var file = new RemoteFileInfoViewModel(
                new()
                {
                    Name = "../escape.txt",
                    FullPath = "/home/user/../escape.txt",
                    Size = 1,
                    IsDirectory = false,
                    Permissions = "-rw-r--r--",
                    LastModified = DateTime.UtcNow,
                    Owner = "user",
                    Group = "user",
                }
            );

            await _vm.OpenWithDefaultEditorCommand.Execute(file).FirstAsync();

            Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            RemoteEditSessionManager.CleanupAll();
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task OpenItem_RejectsUnsafeRemoteLeafNameBeforeSftp()
    {
        _vm.OpenInBuiltInEditor = (_, _, _, _, _) => Task.CompletedTask;
        var file = new RemoteFileInfoViewModel(
            new()
            {
                Name = "../escape.txt",
                FullPath = "/home/user/../escape.txt",
                Size = 1,
                IsDirectory = false,
                Permissions = "-rw-r--r--",
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            }
        );

        await _vm.OpenItemCommand.Execute(file).FirstAsync();

        await _sftpService
            .DidNotReceive()
            .DownloadFileAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task BuiltInRefresh_UsesOriginalSessionAndPath_AndStopsWhenSourceDetaches()
    {
        RemoteFileInfo remote = CreateTestFiles()[1];
        _sftpService.GetFileInfoAsync(_sessionId, remote.FullPath, Arg.Any<CancellationToken>()).Returns(remote);
        _sftpService.ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<RemoteFileInfo>());
        string staging = Path.Combine(Path.GetTempPath(), "vela-editor-callback-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool opened = false;
            _vm.OpenInBuiltInEditor = async (_, _, _, download, lifetime) =>
            {
                opened = true;
                await _vm.NavigateToAsync("/different-directory");
                await download(staging, CancellationToken.None);
                await _sftpService.Received(1).DownloadFileAsync(_sessionId, remote.FullPath, staging,
                                                              cancellationToken: Arg.Any<CancellationToken>());
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => download(staging, cancelled.Token));
                _vm.Detach();
                Assert.IsTrue(lifetime.IsCancellationRequested);
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => download(staging, CancellationToken.None));
                await _sftpService.Received(1).GetFileInfoAsync(_sessionId, remote.FullPath, Arg.Any<CancellationToken>());
            };
            await _vm.OpenItemCommand.Execute(new RemoteFileInfoViewModel(remote)).FirstAsync();
            Assert.IsTrue(opened);
            Assert.IsNull(_vm.ErrorMessage);
        }
        finally { File.Delete(staging); }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task BuiltInRefresh_ChecksCurrentRemoteSizeBeforeDownloading()
    {
        RemoteFileInfo remote = CreateTestFiles()[1];
        _sftpService.GetFileInfoAsync(_sessionId, remote.FullPath, Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo
        {
            Name = remote.Name, FullPath = remote.FullPath, Size = 5L * 1024 * 1024 + 1,
            IsDirectory = false, Permissions = remote.Permissions, LastModified = remote.LastModified,
            Owner = remote.Owner, Group = remote.Group
        });
        bool checkedSize = false;
        _vm.OpenInBuiltInEditor = async (_, _, _, download, _) =>
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => download("unused-stage-path", CancellationToken.None));
            checkedSize = true;
        };
        await _vm.OpenItemCommand.Execute(new RemoteFileInfoViewModel(remote)).FirstAsync();
        Assert.IsTrue(checkedSize);
        await _sftpService.Received(1).DownloadFileAsync(_sessionId, remote.FullPath, Arg.Any<string>(),
                                                       cancellationToken: Arg.Any<CancellationToken>());
        Assert.IsNull(_vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenConfirmed_DeletesItem()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.DeleteItemCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenProgressReported_UpdatesDeleteProgressBarState()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        IProgress<SftpDeleteProgress>? capturedProgress = null;
        _sftpService
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async ci =>
            {
                capturedProgress = ci.ArgAt<IProgress<SftpDeleteProgress>?>(2);
                capturedProgress?.Report(new(2, 4, "/home/user/readme.txt"));
                // Progress<T> 不当场回调:它把回调 Post 到构造时捕获的 SynchronizationContext —— 这里没有,于是落到线程池。
                // 替身若立刻返回,回调可能晚于删除收尾的 finally(进度归零)才跑,又把进度写回 50(macOS CI run 44440518454 撞上过)。
                // 等回调真的落定再返回:次序就此确定,也顺带验了用例名说的「进度条跟着更新」。
                Assert.IsTrue(SpinWait.SpinUntil(() => _vm.DeleteProgressPercent == 50d, TimeSpan.FromSeconds(5)),
                    "报告的 2/4 进度没反映到进度条上");
                Assert.IsFalse(_vm.IsDeleteProgressIndeterminate);
                await Task.CompletedTask;
            });
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);
        await _vm.DeleteItemCommand.Execute(file).FirstAsync();
        Assert.IsNotNull(capturedProgress);
        Assert.IsFalse(_vm.IsDeleteProgressVisible); // hidden again after completion
        Assert.AreEqual(0d, _vm.DeleteProgressPercent);
        Assert.IsFalse(_vm.IsDeleteProgressIndeterminate);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenDeclined_DoesNothing()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(false);
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);
        await _vm.DeleteItemCommand.Execute(file).FirstAsync();
        await _sftpService
            .DidNotReceive()
            .DeleteAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnDirectory_RecursivelyDownloadsIntoPickedFolder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            _sftpService
                .ListDirectoryAsync(
                    _sessionId,
                    "/home/user/documents",
                    Arg.Any<CancellationToken>()
                )
                .Returns(
                    Task.FromResult(
                        new List<RemoteFileInfo>
                        {
                            new()
                            {
                                Name = "report.pdf",
                                FullPath = "/home/user/documents/report.pdf",
                                Size = 1,
                                Permissions = "-rw-r--r--",
                                IsDirectory = false,
                                LastModified = DateTime.UtcNow,
                                Owner = "user",
                                Group = "user",
                            },
                        }
                    )
                );
            var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]); // documents (directory)
            await _vm.DownloadItemCommand.Execute(dir).FirstAsync();
            Assert.IsTrue(Directory.Exists(Path.Combine(tempRoot, "documents")));
            await _sftpService
                .Received(1)
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/documents/report.pdf",
                    Path.Combine(tempRoot, "documents", "report.pdf"),
                    Arg.Any<IProgress<TransferProgress>?>(),
                   cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnDirectory_RejectsUnsafeChildNameBeforeCreatingOrTransferring()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(
                        new List<RemoteFileInfo>
                        {
                            new()
                            {
                                Name = "../escape.txt",
                                FullPath = "/home/user/documents/../escape.txt",
                                Size = 1,
                                IsDirectory = false,
                                Permissions = "-rw-r--r--",
                                LastModified = DateTime.UtcNow,
                                Owner = "user",
                                Group = "user",
                            },
                        }
                    )
                );
            var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]);

            await _vm.DownloadItemCommand.Execute(dir).FirstAsync();

            Assert.IsFalse(File.Exists(Path.Combine(tempRoot, "escape.txt")));
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnDirectory_RejectsExistingSymlinkChildBeforeTransfer()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-download-{Guid.NewGuid():N}");
        string localDirectory = Path.Combine(tempRoot, "documents");
        string outside = Path.Combine(Path.GetTempPath(), $"vela-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDirectory);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(localDirectory, "linked");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                    or PlatformNotSupportedException
                    or IOException
            )
            {
                Assert.Inconclusive($"Symlink creation is unavailable: {ex.Message}");
                return;
            }

            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(
                        new List<RemoteFileInfo>
                        {
                            new()
                            {
                                Name = "linked",
                                FullPath = "/home/user/documents/linked",
                                Size = 4096,
                                IsDirectory = true,
                                Permissions = "drwxr-xr-x",
                                LastModified = DateTime.UtcNow,
                                Owner = "user",
                                Group = "user",
                            },
                        }
                    )
                );
            var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]);

            await _vm.DownloadItemCommand.Execute(dir).FirstAsync();

            Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .DidNotReceive()
                .ListDirectoryAsync(
                    _sessionId,
                    "/home/user/documents/linked",
                    Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task HiddenFiles_AreFilteredOut_UntilToggledOn()
    {
        List<RemoteFileInfo> files = CreateTestFiles();
        files.Add(
            new()
            {
                Name = ".bashrc",
                FullPath = "/home/user/.bashrc",
                Size = 100,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            }
        );
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(files));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        Assert.DoesNotContain(f => f.Name == ".bashrc", _vm.Files); // hidden by default
        _vm.ToggleHiddenFilesCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.ShowHiddenFiles);
        Assert.Contains(f => f.Name == ".bashrc", _vm.Files); // re-filtered without a re-list
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Activate_OnParentEntry_NavigatesUp()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        RemoteFileInfoViewModel parentRow = _vm.Files[0];
        Assert.IsTrue(parentRow.IsParentEntry);
        await _vm.ActivateCommand.Execute(parentRow).FirstAsync();
        Assert.AreEqual("/home", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RootDirectory_HasNoParentEntry()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToCommand.Execute("/").FirstAsync();
        Assert.DoesNotContain(f => f.IsParentEntry, _vm.Files);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NavigateFailure_PreservesPreviousPathAndRows()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToCommand.Execute("/home").FirstAsync();
        string[] previousRows = [.. _vm.Files.Select(file => file.DisplayName)];
        _sftpService
            .ListDirectoryAsync(_sessionId, "/denied", Arg.Any<CancellationToken>())
            .Returns<Task<List<RemoteFileInfo>>>(_ => throw new IOException("denied"));

        await _vm.NavigateToCommand.Execute("/denied").FirstAsync();

        Assert.AreEqual("/home", _vm.CurrentPath);
        Assert.AreSequenceEqual(previousRows, [.. _vm.Files.Select(file => file.DisplayName)]);
        Assert.AreEqual("denied", _vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RefreshCurrentDirectory_PreservesSelectionAndDoesNotRaiseDirectoryChanged()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        int changes = 0;
        _vm.DirectoryChanged += (_, _) => changes++;
        await _vm.NavigateToCommand.Execute("/home").FirstAsync();
        RemoteFileInfoViewModel selected = _vm.Files.First(file => !file.IsParentEntry);
        _vm.SelectedFiles.Add(selected);

        await _vm.RefreshCommand.Execute().FirstAsync();

        Assert.AreEqual(1, changes);
        Assert.ContainsSingle(file => file.FullPath == selected.FullPath, _vm.SelectedFiles);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RefreshCurrentDirectory_WhenUnchanged_DoesNotResetCollection()
    {
        List<RemoteFileInfo> files = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(files));
        await _vm.NavigateToCommand.Execute("/home").FirstAsync();
        int collectionChanges = 0;
        _vm.Files.CollectionChanged += (_, _) => collectionChanges++;

        await _vm.RefreshCommand.Execute().FirstAsync();

        Assert.AreEqual(0, collectionChanges);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task ConcurrentNavigation_LatestResultWins()
    {
        var slow = new TaskCompletionSource<List<RemoteFileInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fast = new TaskCompletionSource<List<RemoteFileInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _sftpService
            .ListDirectoryAsync(_sessionId, "/slow", Arg.Any<CancellationToken>())
            .Returns(slow.Task);
        _sftpService
            .ListDirectoryAsync(_sessionId, "/fast", Arg.Any<CancellationToken>())
            .Returns(fast.Task);
        using IDisposable slowSubscription = _vm.NavigateToCommand.Execute("/slow").Subscribe();
        using IDisposable fastSubscription = _vm.NavigateToCommand.Execute("/fast").Subscribe();

        fast.SetResult([]);
        await Task.Delay(20);
        slow.SetResult(CreateTestFiles());
        await Task.Delay(20);

        Assert.AreEqual("/fast", _vm.CurrentPath);
        Assert.IsFalse(_vm.IsDirectoryLoading);
    }

    // 回归:静默刷新(切回标签时的后台对账)绝不能盖过用户随后发起的导航。
    // 否则会出现"列表显示旧目录、面包屑却是新目录"——因行路径是绝对路径,
    // 后续删除/下载会作用到错误的文件。
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RefreshSilently_DoesNotOverwriteNewerNavigation()
    {
        // 初始定位到 /home/user(快速返回)。
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToCommand.Execute("/home/user").FirstAsync();

        // 让针对 /home/user 的下一次列举变慢:模拟静默刷新在途、尚未返回。
        var slowRefresh = new TaskCompletionSource<List<RemoteFileInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(slowRefresh.Task);
        // 子目录内容(用户随后导航进入),与刷新的过期内容明显不同。
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<RemoteFileInfo>
                    {
                        new()
                        {
                            Name = "subfile.txt",
                            FullPath = "/home/user/documents/subfile.txt",
                            Size = 1,
                            Permissions = "-rw-r--r--",
                            IsDirectory = false,
                            LastModified = DateTime.UtcNow,
                            Owner = "user",
                            Group = "user",
                        },
                    }
                )
            );

        // 静默刷新开始并阻塞在慢列举上。
        Task refresh = _vm.RefreshSilentlyAsync();

        // 用户导航进入子目录(快速完成并应用)。
        await _vm.NavigateToCommand.Execute("/home/user/documents").FirstAsync();
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);

        // 慢刷新此刻才返回,内容是过期的 /home/user 目录(含 stale.txt)。
        slowRefresh.SetResult(
            [
                new()
                {
                    Name = "stale.txt",
                    FullPath = "/home/user/stale.txt",
                    Size = 1,
                    Permissions = "-rw-r--r--",
                    IsDirectory = false,
                    LastModified = DateTime.UtcNow,
                    Owner = "user",
                    Group = "user",
                },
            ]
        );
        await refresh;

        // 过期刷新不得覆盖较新的导航:仍停在 /home/user/documents,列表是子目录内容。
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);
        Assert.Contains(file => file.Name == "subfile.txt", _vm.Files);
        Assert.DoesNotContain(file => file.Name == "stale.txt", _vm.Files);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void Breadcrumbs_SplitCurrentPathIntoClickableSegments()
    {
        _vm.CurrentPath = "/home/user/documents";
        IReadOnlyList<BreadcrumbSegment> crumbs = _vm.Breadcrumbs;
        Assert.HasCount(3, crumbs);
        Assert.AreEqual("home", crumbs[0].Name);
        Assert.AreEqual("/home", crumbs[0].Path);
        Assert.AreEqual("user", crumbs[1].Name);
        Assert.AreEqual("/home/user", crumbs[1].Path);
        Assert.AreEqual("documents", crumbs[2].Name);
        Assert.AreEqual("/home/user/documents", crumbs[2].Path);
    }

    // 属性弹窗已合并 chmod 与 chown(参考 WinSCP):ShowFileProperties 只带回改了的项,null = 取消。
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_WithChangedMode_AppliesChmod()
    {
        _vm.ShowFileProperties = (_, _) => Task.FromResult<FilePropertiesChange?>(new(755, null, null));
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.PropertiesCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .SetPermissionsAsync(
                _sessionId,
                "/home/user/readme.txt",
                755,
                Arg.Any<CancellationToken>()
            );
        await _sftpService
            .DidNotReceive()
            .SetOwnerAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow(false, DisplayName = "取消")]
    [DataRow(true, DisplayName = "确定但什么都没改")]
    public async Task Properties_CancelledOrUnchanged_ChangesNothing(bool confirmedWithoutChanges)
    {
        FilePropertiesChange? change = confirmedWithoutChanges ? new(null, null, null) : null;
        _vm.ShowFileProperties = (_, _) => Task.FromResult(change);
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);
        await _vm.PropertiesCommand.Execute(file).FirstAsync();
        await _sftpService
            .DidNotReceive()
            .SetPermissionsAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<short>(),
                Arg.Any<CancellationToken>()
            );
        await _sftpService
            .DidNotReceive()
            .SetOwnerAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            );
    }

    /// <summary>后端给的候选名单原样交给弹窗;null(FTP、插件协议)即属主/属组只读。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_PassesOwnerChoicesToDialog()
    {
        var choices = new RemoteOwnerChoices(["deploy", "root"], ["root", "www-data"]);
        _sftpService
            .GetOwnerChoicesAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RemoteOwnerChoices?>(choices));
        RemoteOwnerChoices? passed = null;
        _vm.ShowFileProperties = (_, c) =>
        {
            passed = c;
            return Task.FromResult<FilePropertiesChange?>(null);
        };
        await _vm.PropertiesCommand.Execute(new RemoteFileInfoViewModel(CreateTestFiles()[1])).FirstAsync();
        Assert.AreSame(choices, passed);
    }

    /// <summary>候选名单取不到不该挡住看属性:退回只读照样打开。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_WhenOwnerChoicesFail_StillOpensReadOnly()
    {
        _sftpService
            .GetOwnerChoicesAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RemoteOwnerChoices?>(new InvalidOperationException("session gone")));
        bool shown = false;
        RemoteOwnerChoices? passed = new([], []);
        _vm.ShowFileProperties = (_, c) =>
        {
            shown = true;
            passed = c;
            return Task.FromResult<FilePropertiesChange?>(null);
        };
        await _vm.PropertiesCommand.Execute(new RemoteFileInfoViewModel(CreateTestFiles()[1])).FirstAsync();
        Assert.IsTrue(shown);
        Assert.IsNull(passed);
        Assert.IsNull(_vm.ErrorMessage);
    }

    /// <summary>属主与权限都改了:先 chown 再 chmod,只改属主时属组传 null(沿用当前值)。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_WithChangedOwnerAndMode_ChownsThenChmods()
    {
        _vm.ShowFileProperties = (_, _) => Task.FromResult<FilePropertiesChange?>(new(640, "deploy", null));
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.PropertiesCommand.Execute(new RemoteFileInfoViewModel(CreateTestFiles()[1])).FirstAsync();
        Received.InOrder(() =>
        {
            _sftpService.SetOwnerAsync(_sessionId, "/home/user/readme.txt", "deploy", null, Arg.Any<CancellationToken>());
            _sftpService.SetPermissionsAsync(_sessionId, "/home/user/readme.txt", 640, Arg.Any<CancellationToken>());
        });
        Assert.IsNull(_vm.ErrorMessage);
    }

    /// <summary>chown 被拒:权限也不动,错误条说出原因,列表不必刷新(什么都没改)。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_ChownRejected_LeavesModeAloneAndShowsError()
    {
        _vm.ShowFileProperties = (_, _) => Task.FromResult<FilePropertiesChange?>(new(640, "root", "root"));
        _sftpService
            .SetOwnerAsync(_sessionId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Permission denied")));
        await _vm.PropertiesCommand.Execute(new RemoteFileInfoViewModel(CreateTestFiles()[1])).FirstAsync();
        await _sftpService
            .DidNotReceive()
            .SetPermissionsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<CancellationToken>());
        await _sftpService
            .DidNotReceive()
            .ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.AreEqual("Permission denied", _vm.ErrorMessage);
    }

    /// <summary>chown 成了、chmod 被拒:刷新让列表与远端对上,错误条不被刷新清掉。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_ChmodRejectedAfterChown_RefreshesAndKeepsError()
    {
        _vm.CurrentPath = "/home/user";
        _vm.ShowFileProperties = (_, _) => Task.FromResult<FilePropertiesChange?>(new(640, null, "www-data"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        _sftpService
            .SetPermissionsAsync(_sessionId, Arg.Any<string>(), Arg.Any<short>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Operation not permitted")));
        await _vm.PropertiesCommand.Execute(new RemoteFileInfoViewModel(CreateTestFiles()[1])).FirstAsync();
        await _sftpService
            .Received(1)
            .SetOwnerAsync(_sessionId, "/home/user/readme.txt", null, "www-data", Arg.Any<CancellationToken>());
        await _sftpService
            .Received(1)
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>());
        Assert.AreEqual("Operation not permitted", _vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DeleteSelected_ConfirmsOnceAndDeletesAll()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1])); // readme.txt
        _vm.SelectedFiles.Add(new(files[2])); // photo.jpg
        await _vm.DeleteSelectedCommand.Execute().FirstAsync();
        await _sftpService
            .Received(1)
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
        await _sftpService
            .Received(1)
            .DeleteAsync(
                _sessionId,
                "/home/user/photo.jpg",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DeleteSelected_SkipsParentEntry()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.SelectedFiles.Add(RemoteFileInfoViewModel.CreateParentEntry("/home"));
        await _vm.DeleteSelectedCommand.Execute().FirstAsync();
        await _sftpService
            .DidNotReceive()
            .DeleteAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    // PrepareDragOut 测试已随拖出下载功能一并移除(2ef75bf:仅保留右键菜单下载)。

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadSelected_WhenCancelled_StopsRemainingFiles_WithoutError()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            // 顺序传输语义(设置 → 文件传输 → 最大并发 = 1):并发 >1 时第二个文件可能已在飞行中。
            _vm.TransferOptions.MaxConcurrentTransfers = 1;
            List<RemoteFileInfo> files = CreateTestFiles();
            _vm.SelectedFiles.Add(new(files[1])); // readme.txt (first)
            _vm.SelectedFiles.Add(new(files[2])); // photo.jpg (should be skipped)

            // The first file's transfer is cancelled mid-flight.
            _sftpService
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/readme.txt",
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                )
                .Returns(_ => throw new OperationCanceledException());
            await _vm.DownloadSelectedCommand.Execute().FirstAsync();

            // The batch stops on cancellation: the second file is never attempted...
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/photo.jpg",
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            // ...and cancellation is not surfaced as an error.
            Assert.IsTrue(string.IsNullOrEmpty(_vm.ErrorMessage));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CancelDelete_StopsRemainingDeletes_AndKeepsThemListed()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1])); // readme.txt (first)
        _vm.SelectedFiles.Add(new(files[2])); // photo.jpg (should be skipped)

        // The user hits cancel while the first delete is running.
        _sftpService
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(ci =>
            {
                CancellationToken token = ci.ArgAt<CancellationToken>(3);
                _vm.CancelDeleteCommand.Execute().Subscribe();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        await _vm.DeleteSelectedCommand.Execute().FirstAsync();
        await _sftpService
            .DidNotReceive()
            .DeleteAsync(
                _sessionId,
                "/home/user/photo.jpg",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
        Assert.IsTrue(string.IsNullOrEmpty(_vm.ErrorMessage)); // cancellation is not an error
        Assert.IsFalse(_vm.IsDeleteProgressVisible); // overlay dismissed afterwards
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadSelected_DownloadsAllFilesIntoPickedFolder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            List<RemoteFileInfo> files = CreateTestFiles();
            _vm.SelectedFiles.Add(new(files[1])); // readme.txt
            _vm.SelectedFiles.Add(new(files[2])); // photo.jpg
            await _vm.DownloadSelectedCommand.Execute().FirstAsync();
            await _sftpService
                .Received(1)
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/readme.txt",
                    Path.Combine(tempRoot, "readme.txt"),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/photo.jpg",
                    Path.Combine(tempRoot, "photo.jpg"),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_MultiSelect_UploadsAllChosenFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string fileA = Path.Combine(tempRoot, "a.txt");
            string fileB = Path.Combine(tempRoot, "b.log");
            await File.WriteAllTextAsync(fileA, "A");
            await File.WriteAllTextAsync(fileB, "B");
            _vm.CurrentPath = "/home/user";
            _vm.PickLocalPathsForUpload = _ => Task.FromResult<IReadOnlyList<string>>([fileA, fileB]);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));
            await _vm.UploadCommand.Execute().FirstAsync();
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    fileA,
                    "/home/user/a.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    fileB,
                    "/home/user/b.log",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_KeepsTheDestinationCapturedBeforeThePickerOpens()
    {
        string file = Path.Combine(Path.GetTempPath(), "vela-upload-target-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "x");
        try
        {
            _vm.CurrentPath = "/original";
            _vm.PickLocalPathsForUpload = async destination =>
            {
                Assert.AreEqual("/original", destination);
                await Task.Yield();
                _vm.CurrentPath = "/changed-by-background";
                return new[] { file };
            };
            await _vm.UploadCommand.Execute().FirstAsync();
            await _sftpService.Received(1).UploadFileAsync(_sessionId, file,
                "/original/" + Path.GetFileName(file), Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_UsesMappingTargetInsteadOfEntryOrBackgroundDirectory()
    {
        string file = Path.Combine(Path.GetTempPath(), "vela-mapped-upload-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "mapped");
        try
        {
            _vm.CurrentPath = "/entry";
            _vm.PickUploadSelection = async entry =>
            {
                Assert.AreEqual("/entry", entry);
                await Task.Yield();
                _vm.CurrentPath = "/background";
                return new UploadSelectionResult([file], "/mapped", Guid.NewGuid());
            };
            await _vm.UploadCommand.Execute().FirstAsync();
            await _sftpService.Received(1).UploadFileAsync(_sessionId, file,
                "/mapped/" + Path.GetFileName(file), Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_RecursivelyUploadsFolderTree()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"vela-folder-upload-{Guid.NewGuid():N}"
        );
        string folder = Path.Combine(tempRoot, "assets");
        string nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            string rootFile = Path.Combine(folder, "root.txt");
            string nestedFile = Path.Combine(nested, "child.txt");
            await File.WriteAllTextAsync(rootFile, "root");
            await File.WriteAllTextAsync(nestedFile, "child");
            _vm.CurrentPath = "/home/user";
            _vm.PickLocalPathsForUpload = _ => Task.FromResult<IReadOnlyList<string>>([folder]);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));
            await _vm.UploadCommand.Execute().FirstAsync();
            await _sftpService
                .Received(1)
                .EnsureDirectoryAsync(
                    _sessionId,
                    "/home/user/assets",
                    Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .EnsureDirectoryAsync(
                    _sessionId,
                    "/home/user/assets/nested",
                    Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    rootFile,
                    "/home/user/assets/root.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    nestedFile,
                    "/home/user/assets/nested/child.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_MixedSelection_HandlesEachPathByItsOwnKind()
    {
        // 合并成一个上传入口的关键:一次选择里同时有文件和文件夹时,
        // 文件直接传,文件夹整棵递归 —— 不需要用户先想清楚"这次要点哪个菜单项"。
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-mixed-upload-{Guid.NewGuid():N}");
        string folder = Path.Combine(tempRoot, "assets");
        Directory.CreateDirectory(folder);
        try
        {
            string looseFile = Path.Combine(tempRoot, "notes.txt");
            string folderFile = Path.Combine(folder, "logo.png");
            await File.WriteAllTextAsync(looseFile, "notes");
            await File.WriteAllTextAsync(folderFile, "png");
            _vm.CurrentPath = "/home/user";
            _vm.PickLocalPathsForUpload = _ => Task.FromResult<IReadOnlyList<string>>([looseFile, folder]);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));

            await _vm.UploadCommand.Execute().FirstAsync();

            // 散落的文件:直接落在当前目录下。
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    looseFile,
                    "/home/user/notes.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            // 文件夹:建对应远端目录,内容按原结构传进去。
            await _sftpService
                .Received(1)
                .EnsureDirectoryAsync(_sessionId, "/home/user/assets", Arg.Any<CancellationToken>());
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    folderFile,
                    "/home/user/assets/logo.png",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void NormalizeUploadRoots_DropsDuplicatesAndPathsAlreadyInsideAPickedFolder()
    {
        string root = Path.Combine("C:", "work");
        string nested = Path.Combine(root, "src", "main.cs");
        string sibling = Path.Combine("C:", "work-notes", "todo.txt");

        IReadOnlyList<string> kept = FileBrowserViewModel.NormalizeUploadRoots(
            [root, nested, root + Path.DirectorySeparatorChar, sibling]
        );

        // nested 已经被 root 包住,再单列一次就会把同一个文件传两遍、写同一个远端路径。
        Assert.AreSequenceEqual([root, sibling], [.. kept], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void NormalizeUploadRoots_KeepsSiblingsWhoseNamesSharePrefix()
    {
        // "work" 不该把 "work-notes" 吞掉 —— 前缀判断必须落在目录分隔符上。
        string a = Path.Combine("C:", "work");
        string b = Path.Combine("C:", "work-notes");

        IReadOnlyList<string> kept = FileBrowserViewModel.NormalizeUploadRoots([a, b]);

        Assert.AreSequenceEqual([a, b], [.. kept], SequenceOrder.InAnyOrder);
    }

    // ---- 手动输入路径(#226)-------------------------------------------------

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_NavigatesToTypedPath_AndClosesEditor()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/data/app", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        Assert.IsTrue(_vm.IsPathEditing);
        Assert.AreEqual("/home/user", _vm.PathEditText, "切入输入态时应预填当前路径。");

        _vm.PathEditText = "/data/app/";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.AreEqual("/data/app", _vm.CurrentPath);
        Assert.IsFalse(_vm.IsPathEditing, "导航成功后应退回面包屑。");
    }

    /// <summary>路径栏里敲 ~用户名/…:请服务端展开(本地读不到远端的 passwd),跳到展开后的目录。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_ExpandsTildeUserOnTheServer()
    {
        _sftpService.ExpandPathAsync(_sessionId, "~alice/shared", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/srv/alice/shared"));
        _sftpService.ListDirectoryAsync(_sessionId, "/srv/alice/shared", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "~alice/shared";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.AreEqual("/srv/alice/shared", _vm.CurrentPath);
        Assert.IsFalse(_vm.IsPathEditing);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_KeepsEditorOpen_WhenTargetIsUnreadable()
    {
        // 输错路径 / 目标没权限是手动输入的常态:此时不能把输入框收掉,否则用户
        // 得从头再点一次才能改一个字母。
        _sftpService
            .ListDirectoryAsync(_sessionId, "/root/secret", Arg.Any<CancellationToken>())
            .Returns<Task<List<RemoteFileInfo>>>(_ => throw new UnauthorizedAccessException("denied"));
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "/root/secret";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.IsTrue(_vm.IsPathEditing, "导航失败应保持输入态,让用户就地改。");
        Assert.AreEqual("/home/user", _vm.CurrentPath, "失败不应改变当前目录。");
        Assert.IsFalse(string.IsNullOrEmpty(_vm.ErrorMessage));
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_ResolvesRelativeInputAgainstCurrentDirectory()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/logs", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "logs";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.AreEqual("/home/user/logs", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_ExpandsTildeUsingRemoteHome()
    {
        // 报告 #226 的场景:根目录不可读,只能靠直接敲路径进目标目录。~ 展开要问服务端。
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/data/data/com.termux/files/home"));
        _sftpService
            .ListDirectoryAsync(
                _sessionId,
                "/data/data/com.termux/files/home/storage",
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "~/storage";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.AreEqual("/data/data/com.termux/files/home/storage", _vm.CurrentPath);
        Assert.IsFalse(_vm.IsPathEditing);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_CancelKeepsCurrentDirectory()
    {
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "/somewhere/else";
        await _vm.CancelPathEditCommand.Execute().FirstAsync();

        Assert.IsFalse(_vm.IsPathEditing);
        Assert.AreEqual("/home/user", _vm.CurrentPath);
        await _sftpService
            .DidNotReceive()
            .ListDirectoryAsync(_sessionId, "/somewhere/else", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 一次 OSC 7 都没收到过就打开「跟随终端目录」= 点了什么都不会发生,必须说清为什么。
    /// Windows 远端(cmd.exe/PowerShell)就是这种情况:那串钩子只对 POSIX shell 注入(#305)。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FollowTerminal_WithoutAnyReport_ExplainsWhyNothingHappens()
    {
        _vm.FollowTerminal = true;

        Assert.AreEqual(Strings.Get("Sftp_FollowTerminalNoReport"), _vm.ErrorMessage);
    }

    /// <summary>关掉开关就把这句提示收回去,别赖在面板顶上。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FollowTerminal_TurnedOff_RemovesTheHint()
    {
        _vm.FollowTerminal = true;
        _vm.FollowTerminal = false;

        Assert.IsNull(_vm.ErrorMessage);
    }

    /// <summary>终端有上报的会话不该看到这句提示 —— 它说的是"没收到过上报"。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FollowTerminal_AfterAReport_ShowsNoHint()
    {
        _vm.OnTerminalWorkingDirectoryChanged("/srv/app");

        _vm.FollowTerminal = true;

        Assert.IsNull(_vm.ErrorMessage);
    }

    /// <summary>
    /// 换目录时,上一次列举要被<b>主动取消</b>,而不只是回来了不用。
    /// </summary>
    /// <remarks>
    /// 只做"过时结果检查"的话,A→B 连着点两下会让两次列举一起压在同一条 SFTP 通道上,
    /// 最想看的 B 反而排在 A 后面。目录越大越明显 —— 而"点错了赶紧点对的"恰恰是
    /// 大目录下最常见的操作。
    /// </remarks>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NavigatingAway_CancelsThePreviousListing()
    {
        var firstStarted = new TaskCompletionSource();
        CancellationToken firstToken = default;
        _sftpService
            .ListDirectoryAsync(_sessionId, "/slow", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                firstToken = call.Arg<CancellationToken>();
                firstStarted.TrySetResult();
                // 永远不自己完成:只有被取消才结束 —— 正是"大目录还在列"的样子。
                return Task.Delay(Timeout.Infinite, firstToken)
                           .ContinueWith(_ => new List<RemoteFileInfo>(), TaskContinuationOptions.None);
            });
        _sftpService
            .ListDirectoryAsync(_sessionId, "/fast", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        _vm.CurrentPath = "/slow";
        Task slow = _vm.RefreshCommand.Execute().FirstAsync().ToTask();
        await firstStarted.Task;

        _vm.CurrentPath = "/fast";
        await _vm.RefreshCommand.Execute().FirstAsync();

        Assert.IsTrue(firstToken.IsCancellationRequested, "上一次列举没有被取消,它还占着通道。");
        await slow;
    }
}
