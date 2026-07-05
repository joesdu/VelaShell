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

        Assert.AreEqual(3, _vm.Files.Count());
        Assert.AreEqual("documents", _vm.Files[0].Name);
        Assert.AreEqual("readme.txt", _vm.Files[1].Name);
        Assert.AreEqual("photo.jpg", _vm.Files[2].Name);
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
}
