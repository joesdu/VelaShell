using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Import;
using VelaShell.Presentation.ViewModels;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>导出对话框(#571):敏感信息默认不导,勾上必须设够长、两次一致的口令;CSV 一律不带口令。</summary>
[TestClass]
[TestCategory("SessionArchive")]
public class SessionExportViewModelTests
{
    private readonly ISessionArchiveService _service = Substitute.For<ISessionArchiveService>();
    private readonly SessionExportRequest _request = new([Guid.NewGuid(), Guid.NewGuid()], null);

    private async Task<SessionExportViewModel> CreateAsync(SessionExportPreview preview, SessionExportRequest? request = null)
    {
        _service.PreviewExportAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(preview);
        var vm = new SessionExportViewModel(_service, request ?? _request, new FixedTime(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero)));
        await vm.InitializeAsync();
        return vm;
    }

    [TestMethod]
    public async Task Initialize_ShowsTheCounts_AndTheAutoIncludedJumpHosts()
    {
        SessionExportViewModel vm = await CreateAsync(new SessionExportPreview(5, 3, 2, 2, 1));

        Assert.Contains("5", vm.Summary);
        Assert.IsTrue(vm.HasDependencyNote);
        Assert.Contains("2", vm.DependencyNote);
        Assert.IsTrue(vm.CanExport, "JSON、不带敏感信息:直接就能导");
    }

    [TestMethod]
    public async Task IncludeSecrets_IsUnavailable_WhenNothingHasASavedPassword()
    {
        SessionExportViewModel vm = await CreateAsync(new SessionExportPreview(2, 2, 0, 1, 0));

        Assert.IsFalse(vm.CanIncludeSecrets);
        vm.IncludeSecrets = true;
        Assert.IsNull(vm.PassphraseError, "勾了也没东西可导,不必拦着要口令");
        Assert.IsTrue(vm.CanExport);
    }

    [TestMethod]
    public async Task IncludeSecrets_RequiresALongEnoughMatchingPassphrase()
    {
        SessionExportViewModel vm = await CreateAsync(new SessionExportPreview(2, 2, 0, 1, 2));

        vm.IncludeSecrets = true;
        Assert.IsTrue(vm.ShowPassphraseFields);
        Assert.IsNotNull(vm.PassphraseError);
        Assert.IsFalse(vm.CanExport, "勾上敏感信息却没设口令:不能导");

        vm.Passphrase = SecureStringConvert.FromPlaintext("short");
        vm.PassphraseConfirm = SecureStringConvert.FromPlaintext("short");
        Assert.IsNotNull(vm.PassphraseError, $"少于 {SessionExportViewModel.MinPassphraseLength} 个字符");

        vm.Passphrase = SecureStringConvert.FromPlaintext("long enough");
        vm.PassphraseConfirm = SecureStringConvert.FromPlaintext("long enougH");
        Assert.IsNotNull(vm.PassphraseError, "两次不一致");

        vm.PassphraseConfirm = SecureStringConvert.FromPlaintext("long enough");
        Assert.IsNull(vm.PassphraseError);
        Assert.IsTrue(vm.CanExport);
    }

    [TestMethod]
    public async Task ExportTo_WritesTheFile_AndPassesThePassphraseOnlyWhenSecretsAreIncluded()
    {
        SessionExportViewModel vm = await CreateAsync(new SessionExportPreview(2, 2, 0, 1, 2));
        _service.ExportAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<SessionFileFormat>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionExportFile([1, 2, 3], 2, true));
        string path = Path.Combine(Path.GetTempPath(), $"vs-export-{Guid.NewGuid():N}.json");
        try
        {
            vm.IncludeSecrets = true;
            vm.Passphrase = SecureStringConvert.FromPlaintext("correct horse");
            vm.PassphraseConfirm = SecureStringConvert.FromPlaintext("correct horse");

            SessionExportResult? result = await vm.ExportToAsync(path);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.SecretsIncluded);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            await _service.Received(1).ExportAsync(_request.ProfileIds, SessionFileFormat.Json, "correct horse", Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Csv_NeverCarriesAPassphrase_EvenIfTheBoxWasTicked()
    {
        SessionExportViewModel vm = await CreateAsync(new SessionExportPreview(2, 2, 0, 1, 2));
        _service.ExportAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<SessionFileFormat>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionExportFile([], 2, false));
        vm.IncludeSecrets = true;
        vm.Format = SessionFileFormat.Csv;
        string path = Path.Combine(Path.GetTempPath(), $"vs-export-{Guid.NewGuid():N}.csv");
        try
        {
            Assert.IsFalse(vm.ShowPassphraseFields);
            Assert.IsTrue(vm.CanExport, "CSV 不需要口令");

            await vm.ExportToAsync(path);

            await _service.Received(1).ExportAsync(Arg.Any<IReadOnlyCollection<Guid>>(), SessionFileFormat.Csv, null, Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ExportTo_AWriteFailure_StaysInTheDialogWithTheReason()
    {
        SessionExportViewModel vm = await CreateAsync(new SessionExportPreview(1, 1, 0, 0, 0));
        _service.ExportAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<SessionFileFormat>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("disk full"));

        SessionExportResult? result = await vm.ExportToAsync(Path.Combine(Path.GetTempPath(), "never.json"));

        Assert.IsNull(result);
        Assert.Contains("disk full", vm.ErrorMessage!);
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task SuggestedFileName_CarriesTheGroupAndTheDate_AndFollowsTheFormat()
    {
        SessionExportViewModel vm = await CreateAsync(new SessionExportPreview(1, 1, 0, 1, 0), new SessionExportRequest([Guid.NewGuid()], "生产 环境/A"));

        Assert.AreEqual("velashell-生产-环境-A-20261009.json", vm.SuggestedFileName, "分组名里的空白与非法字符换成连字符");
        vm.Format = SessionFileFormat.Csv;
        Assert.AreEqual("velashell-生产-环境-A-20261009.csv", vm.SuggestedFileName);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
