using System.Reactive.Linq;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public class FileBrowserViewModelTests
{
    private readonly ISftpService _sftpService;
    private readonly Guid _sessionId;
    private readonly FileBrowserViewModel _vm;

    public FileBrowserViewModelTests()
    {
        _sftpService = Substitute.For<ISftpService>();
        _sessionId = Guid.NewGuid();
        _vm = new FileBrowserViewModel(_sftpService, _sessionId);
    }

    private static List<RemoteFileInfo> CreateTestFiles()
    {
        return new List<RemoteFileInfo>
        {
            new RemoteFileInfo
            {
                Name = "documents",
                FullPath = "/home/user/documents",
                Size = 4096,
                Permissions = "drwxr-xr-x",
                IsDirectory = true,
                LastModified = DateTime.UtcNow.AddHours(-1),
                Owner = "user",
                Group = "user"
            },
            new RemoteFileInfo
            {
                Name = "readme.txt",
                FullPath = "/home/user/readme.txt",
                Size = 1234,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddDays(-2),
                Owner = "user",
                Group = "user"
            },
            new RemoteFileInfo
            {
                Name = "photo.jpg",
                FullPath = "/home/user/photo.jpg",
                Size = 3567890,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddMinutes(-30),
                Owner = "user",
                Group = "user"
            }
        };
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task ListDirectory_PopulatesFilesCollection()
    {
        var testFiles = CreateTestFiles();
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(testFiles));

        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        // Default order is name-ascending with directories grouped first.
        Assert.AreEqual(3, _vm.Files.Count());
        Assert.AreEqual("documents", _vm.Files[0].Name);
        Assert.AreEqual("photo.jpg", _vm.Files[1].Name);
        Assert.AreEqual("readme.txt", _vm.Files[2].Name);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NavigateIntoFolder_UpdatesCurrentPath()
    {
        var rootFiles = CreateTestFiles();
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rootFiles));

        var subFiles = new List<RemoteFileInfo>
        {
            new RemoteFileInfo
            {
                Name = "report.pdf",
                FullPath = "/home/user/documents/report.pdf",
                Size = 524288,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user"
            }
        };
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subFiles));

        await _vm.NavigateToCommand.Execute("/home/user/documents").FirstAsync();

        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);
        Assert.AreEqual(1, _vm.Files.Count());
        Assert.AreEqual("report.pdf", _vm.Files[0].Name);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Activate_OnDirectory_NavigatesInto()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
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
        await _sftpService.DidNotReceive().ListDirectoryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_NavigatesToWorkingDirectory()
    {
        _sftpService.GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/root"));
        _sftpService.ListDirectoryAsync(_sessionId, "/root", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        await _vm.LoadInitialCommand.Execute().FirstAsync();

        Assert.AreEqual("/root", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_FallsBackToRoot_WhenWorkingDirectoryUnavailable()
    {
        _sftpService.GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("no cwd"));
        _sftpService.ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        await _vm.LoadInitialCommand.Execute().FirstAsync();

        Assert.AreEqual("/", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_BySize_OrdersAscending_WithDirectoriesFirst()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));

        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        // Default sort is name-ascending: directory first, then files by name.
        Assert.AreEqual("documents", _vm.Files[0].Name);

        _vm.SortCommand.Execute("size").Subscribe();

        Assert.AreEqual("documents", _vm.Files[0].Name);          // directory stays grouped on top
        Assert.AreEqual("readme.txt", _vm.Files[1].Name);          // 1234 bytes
        Assert.AreEqual("photo.jpg", _vm.Files[2].Name);           // 3567890 bytes
        Assert.AreEqual("size", _vm.SortColumn);
        Assert.IsFalse(_vm.SortDescending);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_SameColumnTwice_FlipsDirection()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));

        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        _vm.SortCommand.Execute("size").Subscribe();
        Assert.IsFalse(_vm.SortDescending);

        _vm.SortCommand.Execute("size").Subscribe();
        Assert.IsTrue(_vm.SortDescending);
        Assert.AreEqual("documents", _vm.Files[0].Name);           // directory still first
        Assert.AreEqual("photo.jpg", _vm.Files[1].Name);           // largest file first
        Assert.AreEqual("readme.txt", _vm.Files[2].Name);
        Assert.AreEqual(" ▼", _vm.SizeSortGlyph);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task GoUp_NavigatesToParentDirectory()
    {
        _sftpService.ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        _vm.CurrentPath = "/home/user/documents";
        await _vm.GoUpCommand.Execute().FirstAsync();

        Assert.AreEqual("/home/user", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Refresh_RelistsCurrentDirectory()
    {
        var firstList = CreateTestFiles();
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(firstList));

        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        Assert.AreEqual(3, _vm.Files.Count());

        var secondList = new List<RemoteFileInfo>
        {
            firstList[0],
            firstList[1]
        };
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(secondList));

        await _vm.RefreshCommand.Execute().FirstAsync();

        Assert.AreEqual(2, _vm.Files.Count());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow(0, "0 B")]
    [DataRow(500, "500.0 B")]
    [DataRow(1230, "1.2 KB")]
    [DataRow(3565158, "3.4 MB")]
    [DataRow(1181116006, "1.1 GB")]
    public void FormatSize_ReturnsHumanReadable(long bytes, string expected)
    {
        Assert.AreEqual(expected, RemoteFileInfoViewModel.FormatSize(bytes));
    }

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
            Group = "root"
        };

        var vm = new RemoteFileInfoViewModel(fileInfo);

        Assert.AreEqual("-rwxr-xr-x", vm.Permissions);
        Assert.IsFalse(vm.IsDirectory);
        Assert.AreEqual("file", vm.Icon);
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
        _sftpService.ListDirectoryAsync(_sessionId, "/forbidden", Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromException<List<RemoteFileInfo>>(new UnauthorizedAccessException("Permission denied")));

        Exception? thrownEx = null;
        _vm.NavigateToCommand.ThrownExceptions.Subscribe(ex => thrownEx = ex);

        await _vm.NavigateToCommand.Execute("/forbidden").FirstAsync();

        Assert.IsFalse(string.IsNullOrEmpty(_vm.ErrorMessage));
        StringAssert.Contains(_vm.ErrorMessage, "Permission denied");
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void RemoteFileInfoViewModel_DirectoryShowsDash()
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
            Group = "user"
        };

        var vm = new RemoteFileInfoViewModel(dirInfo);

        Assert.AreEqual("--", vm.FormattedSize);
        Assert.IsTrue(vm.IsDirectory);
        Assert.AreEqual("folder", vm.Icon);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task GoUp_AtRoot_StaysAtRoot()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
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
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        await _vm.NewFolderCommand.Execute().FirstAsync();

        await _sftpService.Received(1)
            .CreateDirectoryAsync(_sessionId, "/home/user/docs", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFolder_Cancelled_DoesNothing()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(null);

        await _vm.NewFolderCommand.Execute().FirstAsync();

        await _sftpService.DidNotReceive()
            .CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFile_PromptsAndCreatesUnderCurrentPath()
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("notes.txt");
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        await _vm.NewFileCommand.Execute().FirstAsync();

        await _sftpService.Received(1)
            .CreateFileAsync(_sessionId, "/home/user/notes.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Rename_RenamesWithinParentDirectory()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("renamed.txt");
        _sftpService.ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.RenameCommand.Execute(file).FirstAsync();

        await _sftpService.Received(1)
            .RenameAsync(_sessionId, "/home/user/readme.txt", "/home/user/renamed.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_RenamesToDestinationPath()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("/tmp/moved.txt");
        _sftpService.ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.MoveCommand.Execute(file).FirstAsync();

        await _sftpService.Received(1)
            .RenameAsync(_sessionId, "/home/user/readme.txt", "/tmp/moved.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CopyPath_And_CopyName_WriteToClipboard()
    {
        string? copied = null;
        _vm.CopyToClipboard = text => { copied = text; return Task.CompletedTask; };
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // readme.txt

        await _vm.CopyPathCommand.Execute(file).FirstAsync();
        Assert.AreEqual("/home/user/readme.txt", copied);

        await _vm.CopyNameCommand.Execute(file).FirstAsync();
        Assert.AreEqual("readme.txt", copied);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_InvokesViewCallback()
    {
        RemoteFileInfoViewModel? shown = null;
        _vm.ShowFileProperties = f => { shown = f; return Task.CompletedTask; };
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[0]);

        await _vm.PropertiesCommand.Execute(file).FirstAsync();

        Assert.AreSame(file, shown);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnFile_DownloadsToChosenPath()
    {
        _vm.PickSavePathForDownload = _ => Task.FromResult<string?>("C:/local/readme.txt");
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt

        await _vm.DownloadItemCommand.Execute(file).FirstAsync();

        await _sftpService.Received(1).DownloadFileAsync(
            _sessionId, "/home/user/readme.txt", "C:/local/readme.txt",
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenConfirmed_DeletesItem()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService.ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt

        await _vm.DeleteItemCommand.Execute(file).FirstAsync();

        await _sftpService.Received(1).DeleteAsync(_sessionId, "/home/user/readme.txt",
            Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenDeclined_DoesNothing()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(false);
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);

        await _vm.DeleteItemCommand.Execute(file).FirstAsync();

        await _sftpService.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnDirectory_DoesNothing()
    {
        _vm.PickSavePathForDownload = _ => Task.FromResult<string?>("C:/local/docs");
        var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]); // documents (directory)

        await _vm.DownloadItemCommand.Execute(dir).FirstAsync();

        await _sftpService.DidNotReceive().DownloadFileAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_MultiSelect_UploadsAllChosenFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pulse-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var fileA = Path.Combine(tempRoot, "a.txt");
            var fileB = Path.Combine(tempRoot, "b.log");
            await File.WriteAllTextAsync(fileA, "A");
            await File.WriteAllTextAsync(fileB, "B");

            _vm.CurrentPath = "/home/user";
            _vm.PickFilesForUpload = () => Task.FromResult<IReadOnlyList<string>>(new[] { fileA, fileB });
            _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));

            await _vm.UploadCommand.Execute().FirstAsync();

            await _sftpService.Received(1).UploadFileAsync(
                _sessionId,
                fileA,
                "/home/user/a.txt",
                Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<CancellationToken>());
            await _sftpService.Received(1).UploadFileAsync(
                _sessionId,
                fileB,
                "/home/user/b.log",
                Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadFolderCommand_RecursivelyUploadsFolderTree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pulse-folder-upload-{Guid.NewGuid():N}");
        var folder = Path.Combine(tempRoot, "assets");
        var nested = Path.Combine(folder, "nested");

        Directory.CreateDirectory(nested);
        try
        {
            var rootFile = Path.Combine(folder, "root.txt");
            var nestedFile = Path.Combine(nested, "child.txt");
            await File.WriteAllTextAsync(rootFile, "root");
            await File.WriteAllTextAsync(nestedFile, "child");

            _vm.CurrentPath = "/home/user";
            _vm.PickFolderForUpload = () => Task.FromResult<string?>(folder);
            _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));

            await _vm.UploadFolderCommand.Execute().FirstAsync();

            await _sftpService.Received(1)
                .EnsureDirectoryAsync(_sessionId, "/home/user/assets", Arg.Any<CancellationToken>());
            await _sftpService.Received(1)
                .EnsureDirectoryAsync(_sessionId, "/home/user/assets/nested", Arg.Any<CancellationToken>());

            await _sftpService.Received(1).UploadFileAsync(
                _sessionId,
                rootFile,
                "/home/user/assets/root.txt",
                Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<CancellationToken>());
            await _sftpService.Received(1).UploadFileAsync(
                _sessionId,
                nestedFile,
                "/home/user/assets/nested/child.txt",
                Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}
