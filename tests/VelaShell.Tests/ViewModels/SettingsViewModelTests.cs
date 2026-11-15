using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Services;
using VelaShell.Services.Update;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class SettingsViewModelTests
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;

    public SettingsViewModelTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _themeService = Substitute.For<IThemeService>();
    }

    private SettingsViewModel CreateVm(ISettingsPreviewService? previewService = null) =>
        new(_settingsService, _themeService, previewService: previewService);

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(true)]
    [DataRow(false)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "Linux update behavior requires Linux.")]
    public async Task LinuxUpdate_OpensReleasePageWithoutDownloading(bool writable)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns("2.0.0-beta.1");
        updates.CanSelfUpdate.Returns(writable);
        Uri? opened = null;
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates)
        {
            UpdatePageOpener = uri =>
            {
                opened = uri;
                return Task.FromResult(true);
            }
        };

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        Assert.AreEqual("https://github.com/joesdu/VelaShell/releases", opened?.AbsoluteUri);
        Assert.Contains("2.0.0-beta.1", vm.UpdateStatus);
        Assert.IsFalse(vm.UpdateReady);
        _ = updates.DidNotReceive().CanSelfUpdate;
        await updates.DidNotReceive().DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        updates.DidNotReceive().ApplyUpdateAndRestart();
    }

    /// <summary>认得出安装方式(这里是 deb):直接打开那个安装包的下载地址,状态行带着文件名与地址。</summary>
    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "Linux update behavior requires Linux.")]
    public async Task LinuxUpdate_KnownInstallOpensItsPackageDownload()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns("2.0.0");
        updates.AvailableUpdate.Returns(UpdateManifest.Parse(JsonSerializer.Serialize(new
        {
            version = "2.0.0",
            tag = "2.0.0",
            assets = new Dictionary<string, object>
            {
                ["linux-x64"] = new { name = "VelaShell-2.0.0-linux-x64.tar.gz", sha256 = new string('a', 64), size = 1 },
                ["linux-arm64"] = new { name = "VelaShell-2.0.0-linux-arm64.tar.gz", sha256 = new string('b', 64), size = 1 },
            }
        })));
        Uri? opened = null;
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates)
        {
            LinuxInstallProbe = () => LinuxInstallKind.Deb,
            UpdatePageOpener = uri =>
            {
                opened = uri;
                return Task.FromResult(true);
            }
        };

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        string expected = $"https://github.com/joesdu/VelaShell/releases/download/2.0.0/velashell_2.0.0_{arch}.deb";
        Assert.AreEqual(expected, opened?.AbsoluteUri);
        Assert.Contains(expected, vm.UpdateStatus);
        Assert.Contains($"velashell_2.0.0_{arch}.deb", vm.UpdateStatus);
        await updates.DidNotReceive().DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "Linux update behavior requires Linux.")]
    public async Task LinuxUpdate_BrowserFailureLeavesManualDownloadAddress()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns("2.0.0");
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates)
        {
            UpdatePageOpener = _ => Task.FromException<bool>(new InvalidOperationException("No browser"))
        };

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        Assert.Contains("https://github.com/joesdu/VelaShell/releases", vm.UpdateStatus);
        Assert.IsFalse(vm.UpdateReady);
        await updates.DidNotReceive().DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckUpdates_WithoutNewVersionDoesNotOpenBrowser()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(false);
        bool opened = false;
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates)
        {
            UpdatePageOpener = _ =>
            {
                opened = true;
                return Task.FromResult(true);
            }
        };

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        Assert.IsFalse(opened);
        Assert.AreEqual(Strings.Get("SetAbout_UpToDate"), vm.UpdateStatus);
        Assert.IsFalse(vm.UpdateReady);
    }

    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 只打开发布页,不在应用内下载。")]
    public async Task CheckUpdates_DownloadGivesUpAfterStall_ReportsFailureAndCanRetry()
    {
        // 更新服务的停滞看门狗重连用尽后抛 TimeoutException:状态要落到「下载失败」,
        // 检查更新按钮(命令)要恢复可用,而不是像以前那样永远停在「正在下载」。
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.CanSelfUpdate.Returns(true);
        updates.DownloadUpdateAsync(Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("stalled")));
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get("SetAbout_DownloadFailed"), vm.UpdateStatus);
        Assert.IsFalse(vm.UpdateReady);
        Assert.IsTrue(await vm.CheckUpdatesCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task UpdateFromRelease_DownloadsExactManifestWithoutCheckingOrChangingPreviewChannel()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CurrentVersion.Returns("1.0.0");
        updates.AvailableVersion.Returns("9.0.0");
        updates.CanSelfUpdate.Returns(true);
        UpdateManifest target = CreateUpdateManifest("2.0.0-beta.1");
        UpdateManifest? ready = null;
        updates.ReadyUpdate.Returns(_ => ready);
        updates.DownloadUpdateAsync(target, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                ready = target;
                return Task.CompletedTask;
            });
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates)
        {
            UpdateChannelIndex = 1
        };
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.UpdateFromReleaseAsync(target);

        await updates.Received(1).DownloadUpdateAsync(target,
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        await updates.DidNotReceive().DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        await updates.DidNotReceive().CheckForUpdateAsync();
        updates.DidNotReceive().ClearUpdateError();
        updates.DidNotReceive().ApplyUpdateAndRestart();
        updates.DidNotReceive().RepairUpdateState();
        Assert.AreEqual("preview", vm.General.UpdateChannel);
        Assert.AreEqual(1, vm.UpdateChannelIndex);
        Assert.AreEqual("1.0.0", vm.CurrentUpdateVersion);
        Assert.IsTrue(vm.CanSelfUpdate);
        Assert.AreSame(target, vm.ReadyUpdate);
        Assert.IsTrue(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(Strings.Format("SetAbout_UpdateReady", target.Version), vm.UpdateStatus);
        Assert.Contains(nameof(SettingsViewModel.ReadyUpdate), changed);
        Assert.Contains(nameof(SettingsViewModel.UpdateReady), changed);
    }

    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task UpdateFromRelease_FailureReleasesBusyStateAndSameTargetCanRetry()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        UpdateManifest target = CreateUpdateManifest();
        UpdateManifest? ready = null;
        updates.ReadyUpdate.Returns(_ => ready);
        int attempts = 0;
        updates.DownloadUpdateAsync(target, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++attempts == 1)
                {
                    return Task.FromException(new TimeoutException("stalled"));
                }
                ready = target;
                return Task.CompletedTask;
            });
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        await vm.UpdateFromReleaseAsync(target);

        Assert.AreEqual(Strings.Get("SetAbout_DownloadFailed"), vm.UpdateStatus);
        Assert.IsFalse(vm.IsUpdating);
        Assert.IsFalse(vm.UpdateReady);
        Assert.IsNull(vm.ReadyUpdate);
        Assert.IsTrue(await vm.CheckUpdatesCommand.CanExecute.FirstAsync());
        Assert.IsTrue(await vm.RepairUpdateCommand.CanExecute.FirstAsync());
        Assert.IsTrue(await vm.RestartToUpdateCommand.CanExecute.FirstAsync());

        await vm.UpdateFromReleaseAsync(target);

        Assert.AreEqual(2, attempts);
        Assert.AreSame(target, vm.ReadyUpdate);
        Assert.IsTrue(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(Strings.Format("SetAbout_UpdateReady", target.Version), vm.UpdateStatus);
        updates.DidNotReceive().ApplyUpdateAndRestart();
        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task UpdateFromRelease_BusyRejectsDuplicateAndAllCommandEntryPoints()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        UpdateManifest target = CreateUpdateManifest();
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.DownloadUpdateAsync(target, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(finish.Task);
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        Task download = vm.UpdateFromReleaseAsync(target);
        try
        {
            Assert.IsTrue(vm.IsUpdating);
            Assert.IsFalse(await vm.CheckUpdatesCommand.CanExecute.FirstAsync());
            Assert.IsFalse(await vm.RepairUpdateCommand.CanExecute.FirstAsync());
            Assert.IsFalse(await vm.RestartToUpdateCommand.CanExecute.FirstAsync());
            await vm.UpdateFromReleaseAsync(target);
            await vm.UpdateFromReleaseAsync(CreateUpdateManifest("3.0.0"));
            // 绕过按钮 CanExecute 核验方法自身的 guard,不能只靠界面禁用来互斥。
            await InvokeUpdateOperationAsync(vm, "CheckForUpdatesAsync");
            await InvokeUpdateOperationAsync(vm, "RepairUpdateStateAsync");
            await InvokeUpdateOperationAsync(vm, "RestartToUpdateAsync");

            Assert.IsTrue(vm.IsUpdating, "被拒绝的入口不能结束仍在下载的操作。");
            Assert.AreEqual(Strings.Get("ReleaseNotes_UpdateConflict"), vm.UpdateStatus);
            await updates.Received(1).DownloadUpdateAsync(target,
                Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
            await updates.DidNotReceive().CheckForUpdateAsync();
            updates.DidNotReceive().ClearUpdateError();
            updates.DidNotReceive().RepairUpdateState();
            updates.DidNotReceive().ApplyUpdateAndRestart();
        }
        finally
        {
            finish.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.IsFalse(vm.IsUpdating);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("CheckForUpdatesAsync")]
    [DataRow("RepairUpdateStateAsync")]
    [DataRow("RestartToUpdateAsync")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内换版。")]
    public async Task UpdateOperations_BlockExplicitDownloadWhileBusy(string operation)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        UpdateManifest target = CreateUpdateManifest();
        if (operation == "RestartToUpdateAsync")
        {
            updates.ReadyUpdate.Returns(target);
        }
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.CheckForUpdateAsync().Returns(_ =>
        {
            started.TrySetResult();
            return WaitForCheckAsync();
        });
        updates.RepairUpdateState().Returns(_ =>
        {
            started.TrySetResult();
            finish.Task.GetAwaiter().GetResult();
            return true;
        });
        updates.When(service => service.ApplyUpdateAndRestart()).Do(_ =>
        {
            started.TrySetResult();
            finish.Task.GetAwaiter().GetResult();
        });
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        Task pending = InvokeUpdateOperationAsync(vm, operation);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(vm.IsUpdating);
            await vm.UpdateFromReleaseAsync(target);
            Assert.AreEqual(Strings.Get("ReleaseNotes_UpdateConflict"), vm.UpdateStatus);
            Assert.IsTrue(vm.IsUpdating);
            await updates.DidNotReceive().DownloadUpdateAsync(Arg.Any<UpdateManifest>(),
                Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            finish.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.IsFalse(vm.IsUpdating);

        async Task<bool> WaitForCheckAsync()
        {
            await finish.Task;
            return false;
        }
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("same")]
    [DataRow("hash-case")]
    [DataRow("version")]
    [DataRow("tag")]
    [DataRow("name")]
    [DataRow("hash")]
    [DataRow("size")]
    public async Task UpdateFromRelease_ReadySnapshotIsReusedOnlyForSameTarget(string difference)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        UpdateManifest ready = CreateUpdateManifest();
        updates.ReadyUpdate.Returns(ready);
        UpdateManifest target = CreateUpdateManifest(
            version: difference == "version" ? "3.0.0" : "2.0.0",
            tag: difference == "tag" ? "other-tag" : null,
            name: difference == "name" ? "other.zip" : "update.zip",
            hash: difference == "hash" ? new string('b', 64)
                : new string(difference == "hash-case" ? 'A' : 'a', 64),
            size: difference == "size" ? 200 : 100);
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.UpdateFromReleaseAsync(target);

        bool same = difference is "same" or "hash-case";
        Assert.AreSame(ready, vm.ReadyUpdate);
        Assert.IsTrue(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(same ? Strings.Format("SetAbout_UpdateReady", ready.Version)
            : Strings.Get("ReleaseNotes_UpdateConflict"), vm.UpdateStatus);
        Assert.Contains(nameof(SettingsViewModel.ReadyUpdate), changed);
        Assert.Contains(nameof(SettingsViewModel.UpdateReady), changed);
        await updates.DidNotReceive().DownloadUpdateAsync(Arg.Any<UpdateManifest>(),
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        await updates.DidNotReceive().CheckForUpdateAsync();
        updates.DidNotReceive().ApplyUpdateAndRestart();
        updates.DidNotReceive().RepairUpdateState();
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckUpdates_PreservesReadySnapshotWithoutCheckingAgain()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        UpdateManifest ready = CreateUpdateManifest();
        updates.ReadyUpdate.Returns(ready);
        updates.AvailableVersion.Returns("9.0.0");
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        Assert.AreSame(ready, vm.ReadyUpdate);
        Assert.IsTrue(vm.UpdateReady);
        Assert.AreEqual(Strings.Format("SetAbout_UpdateReady", ready.Version), vm.UpdateStatus);
        await updates.DidNotReceive().CheckForUpdateAsync();
        updates.DidNotReceive().ClearUpdateError();
        await updates.DidNotReceive().DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task UpdateFromRelease_ReadyAppearingDuringDownloadIsPublishedEvenOnFailure(bool fails)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        UpdateManifest target = CreateUpdateManifest();
        UpdateManifest ready = CreateUpdateManifest("3.0.0");
        UpdateManifest? serviceReady = null;
        updates.ReadyUpdate.Returns(_ => serviceReady);
        updates.DownloadUpdateAsync(target, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                serviceReady = ready;
                return fails ? Task.FromException(new InvalidOperationException("conflict")) : Task.CompletedTask;
            });
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.UpdateFromReleaseAsync(target);

        Assert.AreSame(ready, vm.ReadyUpdate);
        Assert.IsTrue(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(fails ? Strings.Get("SetAbout_DownloadFailed")
            : Strings.Format("SetAbout_UpdateReady", ready.Version), vm.UpdateStatus);
        Assert.Contains(nameof(SettingsViewModel.ReadyUpdate), changed);
        Assert.Contains(nameof(SettingsViewModel.UpdateReady), changed);
        updates.DidNotReceive().ApplyUpdateAndRestart();
    }

    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task CheckUpdates_DownloadReturningWithoutReadySnapshotIsNotReportedReady()
    {
        // 下载正常返回却没有就绪快照(服务拿到空目标时以前就是这样),不能报「已就绪」:
        // 否则检查更新从此短路,重启又必然失败,只能在「已就绪 → 重启失败」之间兜圈。
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns("2.0.0");
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        Assert.IsFalse(vm.UpdateReady);
        Assert.IsNull(vm.ReadyUpdate);
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(Strings.Get("SetAbout_DownloadFailed"), vm.UpdateStatus);

        await vm.UpdateFromReleaseAsync(CreateUpdateManifest("3.0.0"));
        await vm.CheckUpdatesCommand.Execute().FirstAsync();
        await vm.RestartToUpdateCommand.Execute().FirstAsync();

        Assert.IsFalse(vm.UpdateReady);
        await updates.Received(2).CheckForUpdateAsync();
        await updates.Received(2).DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        await updates.Received(1).DownloadUpdateAsync(Arg.Any<UpdateManifest>(),
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        updates.DidNotReceive().ApplyUpdateAndRestart();
    }

    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内换版。")]
    public async Task RestartToUpdate_FailureThatDropsReadySnapshot_HidesRestartAndChecksAgain()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        UpdateManifest? ready = CreateUpdateManifest();
        updates.ReadyUpdate.Returns(_ => ready);
        updates.When(service => service.ApplyUpdateAndRestart()).Do(_ =>
        {
            // 服务换版前复核发现包已丢失或损坏:丢掉就绪快照再报错。
            ready = null;
            throw new InvalidDataException("missing");
        });
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        Assert.IsTrue(vm.UpdateReady);

        await vm.RestartToUpdateCommand.Execute().FirstAsync();

        Assert.IsNull(vm.ReadyUpdate);
        Assert.IsFalse(vm.UpdateReady, "包已不在,重启入口必须收起");
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(Strings.Get("SetAbout_ApplyFailed"), vm.UpdateStatus);

        await vm.CheckUpdatesCommand.Execute().FirstAsync();

        await updates.Received(1).CheckForUpdateAsync();
        Assert.AreEqual(Strings.Get("SetAbout_UpToDate"), vm.UpdateStatus);
        updates.Received(1).ApplyUpdateAndRestart();
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("RepairUpdateStateAsync")]
    [DataRow("RestartToUpdateAsync")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内换版。")]
    public async Task UpdateOperationFailure_DoesNotClearExistingReadySnapshot(string operation)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        UpdateManifest ready = CreateUpdateManifest();
        updates.ReadyUpdate.Returns(ready);
        updates.RepairUpdateState().Returns(_ => throw new IOException("locked"));
        updates.When(service => service.ApplyUpdateAndRestart()).Do(_ => throw new IOException("locked"));
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        await InvokeUpdateOperationAsync(vm, operation);

        Assert.AreSame(ready, vm.ReadyUpdate);
        Assert.IsTrue(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(Strings.Get(operation == "RepairUpdateStateAsync"
            ? "SetAbout_RepairFailed" : "SetAbout_ApplyFailed"), vm.UpdateStatus);
        await vm.UpdateFromReleaseAsync(ready);
        Assert.AreEqual(Strings.Format("SetAbout_UpdateReady", ready.Version), vm.UpdateStatus);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task RepairUpdate_ClearedReadySnapshotNotifiesBothPropertiesBeforeBecomingIdle()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        UpdateManifest? ready = CreateUpdateManifest();
        updates.ReadyUpdate.Returns(_ => ready);
        updates.RepairUpdateState().Returns(_ =>
        {
            ready = null;
            return true;
        });
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        List<(string? Property, bool Busy, bool Ready)> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add((e.PropertyName, vm.IsUpdating, vm.UpdateReady));

        await vm.RepairUpdateCommand.Execute().FirstAsync();

        Assert.IsNull(vm.ReadyUpdate);
        Assert.IsFalse(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.Contains(change => change is (nameof(SettingsViewModel.ReadyUpdate), true, false), changed);
        Assert.Contains(change => change is (nameof(SettingsViewModel.UpdateReady), true, false), changed);
        Assert.AreEqual(Strings.Get("SetAbout_RepairDone"), vm.UpdateStatus);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task UpdateFromRelease_UnsupportedInstallationNeverDownloads(bool storeManaged, bool writable)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.IsStoreManaged.Returns(storeManaged);
        updates.CanSelfUpdate.Returns(writable);
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        UpdateManifest target = CreateUpdateManifest();

        await vm.UpdateFromReleaseAsync(target);

        Assert.AreEqual(storeManaged ? Strings.Get("SetAbout_StoreManagedUpdates")
            : Strings.Format("SetAbout_UpdateAvailableManual", target.Version), vm.UpdateStatus);
        Assert.IsFalse(vm.IsUpdating);
        Assert.IsFalse(vm.UpdateReady);
        await updates.DidNotReceive().DownloadUpdateAsync(Arg.Any<UpdateManifest>(),
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        await updates.DidNotReceive().CheckForUpdateAsync();
        updates.DidNotReceive().ApplyUpdateAndRestart();
    }

    /// <summary>
    /// 自动下载更新:启动检查选定的目标在后台下完、校验完就绪,关于页随之出现「重启并更新」;
    /// 走的是无参数下载(启动检查选定的那个),不重新检查,也绝不自己换版重启。
    /// </summary>
    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task BackgroundDownload_DownloadsTheCheckedTargetAndShowsRestart()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        updates.AvailableVersion.Returns("2.0.0");
        UpdateManifest target = CreateUpdateManifest("2.0.0");
        UpdateManifest? ready = null;
        updates.ReadyUpdate.Returns(_ => ready);
        updates.DownloadUpdateAsync(Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                ready = target;
                return Task.CompletedTask;
            });
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        Assert.IsTrue(await vm.DownloadUpdateInBackgroundAsync());

        await updates.Received(1).DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        await updates.DidNotReceive().CheckForUpdateAsync();
        updates.DidNotReceive().ApplyUpdateAndRestart();
        Assert.IsTrue(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.AreEqual(Strings.Format("SetAbout_UpdateReady", "2.0.0"), vm.UpdateStatus);
    }

    /// <summary>已经有就绪的包(这次运行里关于页下过):认它,不再下载一遍。</summary>
    [TestMethod]
    [TestCategory("Update")]
    public async Task BackgroundDownload_ReadyPackageIsNotDownloadedAgain()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        updates.AvailableVersion.Returns("2.0.0");
        UpdateManifest ready = CreateUpdateManifest("2.0.0");
        updates.ReadyUpdate.Returns(ready);
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        Assert.IsTrue(await vm.DownloadUpdateInBackgroundAsync());

        await updates.DidNotReceive().DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        Assert.IsTrue(vm.UpdateReady);
        Assert.AreEqual(Strings.Format("SetAbout_UpdateReady", "2.0.0"), vm.UpdateStatus);
    }

    /// <summary>
    /// 后台下载失败不打扰:不抛、不报「下载失败,请稍后重试」(那是给点了按钮的人看的),
    /// 状态行只说下次启动会再试;忙状态释放,用户随时能自己点「检查更新」重来。
    /// </summary>
    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task BackgroundDownload_FailureIsQuietAndLeavesTheCheckButtonUsable()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        updates.AvailableVersion.Returns("2.0.0");
        updates.DownloadUpdateAsync(Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("offline")));
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);

        Assert.IsFalse(await vm.DownloadUpdateInBackgroundAsync());

        Assert.AreEqual(Strings.Format("SetAbout_AutoDownloadFailed", "2.0.0"), vm.UpdateStatus);
        Assert.IsFalse(vm.UpdateReady);
        Assert.IsFalse(vm.IsUpdating);
        Assert.IsTrue(await vm.CheckUpdatesCommand.CanExecute.FirstAsync());
    }

    /// <summary>
    /// 不能原地换版的安装(只读目录、商店版)、或者启动检查的结果已经没了:什么也不下,也不改状态行。
    /// </summary>
    [TestMethod]
    [TestCategory("Update")]
    [DataRow(false, false, "2.0.0")]
    [DataRow(true, true, "2.0.0")]
    [DataRow(false, true, null)]
    public async Task BackgroundDownload_NothingToDownloadStaysSilent(bool storeManaged, bool writable, string? available)
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.IsStoreManaged.Returns(storeManaged);
        updates.CanSelfUpdate.Returns(writable);
        updates.AvailableVersion.Returns(available);
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        string statusBefore = vm.UpdateStatus;

        Assert.IsFalse(await vm.DownloadUpdateInBackgroundAsync());

        await updates.DidNotReceive().DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(statusBefore, vm.UpdateStatus);
        Assert.IsFalse(vm.IsUpdating);
    }

    /// <summary>
    /// 用户恰好在关于页点了检查、那一路正在下载:后台这一路让开,不再并发起第二份下载,
    /// 也不把那边的进度文案换成冲突提示。
    /// </summary>
    [TestMethod]
    [TestCategory("Update")]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Linux, IgnoreMessage = "Linux 不在应用内下载。")]
    public async Task BackgroundDownload_StepsAsideWhileAnotherUpdateOperationRuns()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CanSelfUpdate.Returns(true);
        updates.AvailableVersion.Returns("2.0.0");
        updates.CheckForUpdateAsync().Returns(true);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.DownloadUpdateAsync(Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);
        SettingsViewModel vm = new(_settingsService, _themeService, updateService: updates);
        Task manual = InvokeUpdateOperationAsync(vm, "CheckForUpdatesAsync");
        try
        {
            Assert.IsTrue(vm.IsUpdating);
            string statusDuringManual = vm.UpdateStatus;

            Assert.IsFalse(await vm.DownloadUpdateInBackgroundAsync());

            Assert.AreEqual(statusDuringManual, vm.UpdateStatus);
            Assert.IsTrue(vm.IsUpdating, "后台这一路让开时不能结束关于页那一路的忙状态。");
        }
        finally
        {
            release.TrySetResult();
            await manual.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await updates.Received(1).DownloadUpdateAsync(
            Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
    }

    private static UpdateManifest CreateUpdateManifest(string version = "2.0.0", string? tag = null,
        string name = "update.zip", string? hash = null, long size = 100) =>
        UpdateManifest.Parse(JsonSerializer.Serialize(new
        {
            version,
            tag = tag ?? $"v{version}",
            assets = new Dictionary<string, object>
            {
                [UpdateManifest.CurrentRid() ?? "win-x64"] = new { name, sha256 = hash ?? new string('a', 64), size }
            }
        }));

    private static Task InvokeUpdateOperationAsync(SettingsViewModel vm, string method) =>
        (Task)typeof(SettingsViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;

    [TestMethod]
    [TestCategory("Update")]
    public void FormatDownloadStatus_PreparingAndUnknownTotal_UseDedicatedText()
    {
        long now = 1_000;
        UpdateDownloadTracker tracker = new(clock: () => now);

        // 一个字节都还没到(连接中 / 校验断点前缀):不显示「0% · 0 B/0 B · 0 B/s」,
        // 过了停滞阈值也不说连接可能已经中断。
        now += 60_000;
        Assert.AreEqual(Strings.Get("SetAbout_DownloadPreparing"), SettingsViewModel.FormatDownloadStatus(tracker));

        // 总量未知:只报已下载字节与速率,不拼出「0% · x/0 B」。
        tracker.Report(new UpdateDownloadProgress(2_048, 0, 1_024));
        Assert.AreEqual(
            Strings.Format("SetAbout_DownloadingSpeedNoTotal",
                TransferItemViewModel.FormatBytes(2_048), TransferItemViewModel.FormatSpeed(1_024)),
            SettingsViewModel.FormatDownloadStatus(tracker));

        now += (long)UpdateDownloadTracker.StallThreshold.TotalMilliseconds;
        Assert.AreEqual(
            Strings.Format("SetAbout_DownloadStalledNoTotal",
                TransferItemViewModel.FormatBytes(2_048),
                TransferItemViewModel.FormatTimeRemaining(UpdateDownloadTracker.StallThreshold)),
            SettingsViewModel.FormatDownloadStatus(tracker));
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task AppearanceOpacityChange_PreviewsEveryValueImmediately_WithoutSnapshot()
    {
        ISettingsPreviewService preview = new SettingsPreviewService();
        var opacityValues = new List<int>();
        var snapshots = new List<AppSettings>();
        preview.WindowOpacityPreviewRequested += opacityValues.Add;
        preview.PreviewRequested += snapshots.Add;
        _settingsService.GetSettingsAsync().Returns(new AppSettings());

        SettingsViewModel vm = CreateVm(preview);
        await vm.LoadCommand.Execute().FirstAsync();

        foreach (int value in new[] { 20, 30, 40, 50 })
        {
            vm.Appearance.WindowOpacityPercent = value;
        }

        Assert.AreSequenceEqual([20, 30, 40, 50], opacityValues);
        Assert.IsEmpty(snapshots);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task OpacityOnlyPreview_NotifyClosed_BroadcastsOriginalBaseline()
    {
        ISettingsPreviewService preview = new SettingsPreviewService();
        var snapshots = new List<AppSettings>();
        preview.PreviewRequested += snapshots.Add;
        var baseline = new AppSettings { Appearance = new() { WindowOpacityPercent = 80 } };
        _settingsService.GetSettingsAsync().Returns(baseline);

        SettingsViewModel vm = CreateVm(preview);
        await vm.LoadCommand.Execute().FirstAsync();
        vm.Appearance.WindowOpacityPercent = 40;
        vm.NotifyClosed();

        Assert.HasCount(1, snapshots);
        Assert.AreEqual(80, snapshots[0].Appearance.WindowOpacityPercent);
    }

    /// <summary>
    /// 语言要先于落盘切换:落盘触发的 SettingsSaved 会把设置重新下发到已打开的终端,
    /// 行号栏右键菜单等文案是下发时现取的 —— 晚一步换语言,它们就停在旧语言。
    /// </summary>
    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_SwitchesLanguageBeforePersisting()
    {
        ILocalizationService localization = Substitute.For<ILocalizationService>();
        var vm = new SettingsViewModel(_settingsService, _themeService, localizationService: localization)
        {
            Language = "en"
        };

        await vm.SaveCommand.Execute().FirstAsync();

        Received.InOrder(() =>
        {
            localization.SetLanguage("en");
            _settingsService.SaveSettingsAsync(Arg.Any<AppSettings>());
        });
    }

    /// <summary>
    /// 换语言时主题下拉的条目整表重建,ComboBox 在那一下把选中项清成 -1 推回来 ——
    /// 当成「回到默认主题」的话,切一次语言主题就被换掉了。
    /// </summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ThemeIndex_IgnoresTheClearedSelection()
    {
        SettingsViewModel vm = CreateVm();
        vm.Theme = "nord";

        vm.ThemeIndex = -1;

        Assert.AreEqual("nord", vm.Theme);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void LanguageDropdown_FirstItemFollowsTheSystem()
    {
        // 首项「跟随系统」对应空串(出厂值);GeneralSettingsPage 的条目顺序与 AvailableLanguages 一致
        SettingsViewModel vm = CreateVm();
        Assert.AreEqual(string.Empty, vm.Language);
        Assert.AreEqual(0, vm.LanguageIndex);

        vm.LanguageIndex = 2;
        Assert.AreEqual("en", vm.Language);
        vm.LanguageIndex = 0;
        Assert.AreEqual(string.Empty, vm.Language);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task LoadCommand_LoadsSettingsFromService()
    {
        var settings = new AppSettings
        {
            Language = "zh-CN",
            Theme = "light",
            TerminalFont = "Fira Code",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222,
            TerminalType = "vt220",
            TerminalEncoding = "GBK",
            Appearance = new() { ShowQuickCommandsPanel = true },
        };
        _settingsService.GetSettingsAsync().Returns(settings);

        SettingsViewModel vm = CreateVm();
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.AreEqual("zh-CN", vm.Language);
        Assert.AreEqual("light", vm.Theme);
        Assert.AreEqual("Fira Code", vm.TerminalFont);
        Assert.AreEqual(16, vm.TerminalFontSize);
        Assert.AreEqual(5000, vm.ScrollbackLines);
        Assert.AreEqual(2222, vm.DefaultPort);
        Assert.AreEqual("vt220", vm.TerminalType);
        Assert.AreEqual("GBK", vm.TerminalEncoding);
        Assert.IsTrue(vm.Appearance.ShowQuickCommandsPanel);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_PersistsToService()
    {
        SettingsViewModel vm = CreateVm();
        vm.Language = "zh-CN";
        vm.Theme = "light";
        vm.TerminalFont = "Cascadia Code";
        vm.TerminalFontSize = 18;
        vm.ScrollbackLines = 20000;
        vm.DefaultPort = 8022;
        vm.TerminalType = "xterm-256color";
        vm.TerminalEncoding = "UTF-8";
        vm.Appearance.ShowQuickCommandsPanel = true;

        await vm.SaveCommand.Execute().FirstAsync();

        await _settingsService
            .Received(1)
            .SaveSettingsAsync(
                Arg.Is<AppSettings>(s =>
                    s.Language == "zh-CN"
                    && s.Theme == "light"
                    && s.TerminalFont == "Cascadia Code"
                    && s.TerminalFontSize == 18
                    && s.ScrollbackLines == 20000
                    && s.DefaultPort == 8022
                    && s.TerminalType == "xterm-256color"
                    && s.TerminalEncoding == "UTF-8"
                    && s.Appearance.ShowQuickCommandsPanel
                )
            );
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_AppliesTheme()
    {
        SettingsViewModel vm = CreateVm();
        vm.Theme = "light";

        await vm.SaveCommand.Execute().FirstAsync();

        _themeService.Received(1).SetTheme("light");
    }

    /// <summary>代理设置载入:分组对象直绑回填,类型下拉索引与可编辑标志同步刷新。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public async Task LoadCommand_PopulatesProxyOptionsAndDerivedIndex()
    {
        var settings = new AppSettings
        {
            Proxy = new()
            {
                Type = "socks5",
                Host = "proxy.example",
                Port = 1080,
                Username = "u",
                Password = "p",
                ProxyDns = false,
            },
        };
        _settingsService.GetSettingsAsync().Returns(settings);

        SettingsViewModel vm = CreateVm();
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.AreSame(settings.Proxy, vm.Proxy);
        Assert.AreEqual(3, vm.ProxyTypeIndex);
        Assert.IsTrue(vm.IsProxyEditable);
        Assert.IsFalse(vm.Proxy.ProxyDns);
    }

    /// <summary>代理设置保存:类型索引写回字符串,主机/端口/凭据/DNS 开关一并落盘。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_PersistsProxyOptions()
    {
        SettingsViewModel vm = CreateVm();
        vm.ProxyTypeIndex = 2; // http
        vm.Proxy.Host = "proxy.example";
        vm.Proxy.Port = 3128;
        vm.Proxy.Username = "user";
        vm.Proxy.Password = "secret";
        vm.Proxy.ProxyDns = false;

        await vm.SaveCommand.Execute().FirstAsync();

        await _settingsService
            .Received(1)
            .SaveSettingsAsync(
                Arg.Is<AppSettings>(s =>
                    s.Proxy.Type == "http"
                    && s.Proxy.Host == "proxy.example"
                    && s.Proxy.Port == 3128
                    && s.Proxy.Username == "user"
                    && s.Proxy.Password == "secret"
                    && !s.Proxy.ProxyDns
                )
            );
    }

    /// <summary>代理类型索引与内部字符串的双向映射;仅 http/socks5 开放参数编辑。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ProxyTypeIndex_MapsTypeStringAndGatesEditability()
    {
        SettingsViewModel vm = CreateVm();

        // 出厂值是"不走代理" —— 见 ProxyOptions.Type 上的说明
        Assert.AreEqual(0, vm.ProxyTypeIndex);
        Assert.AreEqual("none", vm.Proxy.Type);
        Assert.IsFalse(vm.IsProxyEditable);

        vm.ProxyTypeIndex = 1;
        Assert.AreEqual("system", vm.Proxy.Type);
        Assert.IsFalse(vm.IsProxyEditable);

        vm.ProxyTypeIndex = 2;
        Assert.AreEqual("http", vm.Proxy.Type);
        Assert.IsTrue(vm.IsProxyEditable);

        vm.ProxyTypeIndex = 3;
        Assert.AreEqual("socks5", vm.Proxy.Type);
        Assert.IsTrue(vm.IsProxyEditable);

        vm.Proxy.Type = "none";
        Assert.AreEqual(0, vm.ProxyTypeIndex);
        Assert.IsFalse(vm.IsProxyEditable);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_ValidatesRequiredFields()
    {
        var vm = new ConnectionProfileViewModel
        {
            // Host and Username empty → SaveCommand not executable
            Host = "",
            Username = "",
        };
        bool canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);

        Assert.IsFalse(canExecute);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_AuthMethodToggle_SwitchesVisibility()
    {
        var vm = new ConnectionProfileViewModel();

        // Default is Password
        Assert.IsTrue(vm.IsPasswordAuth);
        Assert.IsFalse(vm.IsKeyAuth);

        vm.AuthMethod = AuthMethod.PrivateKey;

        Assert.IsFalse(vm.IsPasswordAuth);
        Assert.IsTrue(vm.IsKeyAuth);

        vm.AuthMethod = AuthMethod.Password;

        Assert.IsTrue(vm.IsPasswordAuth);
        Assert.IsFalse(vm.IsKeyAuth);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_TrustPermanentlyCommand_SetsResult()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-ed25519",
            "SHA256:abc123def456",
            HostKeyVerification.Unknown
        );

        Assert.IsNull(vm.Result);

        vm.TrustPermanentlyCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.TrustPermanently, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_TrustOnceCommand_SetsResult()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-ed25519",
            "SHA256:abc123def456",
            HostKeyVerification.Unknown
        );

        vm.TrustOnceCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.TrustOnce, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_CancelCommand_SetsReject()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-rsa",
            "SHA256:xyz789",
            HostKeyVerification.Unknown
        );

        vm.CancelCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.Reject, vm.Result);
    }

    /// <summary>给了指纹图才显示那一行(等宽字体的 17×9 图);没给就不占位。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_RandomArt_ShownOnlyWhenGiven()
    {
        var without = new HostKeyPromptViewModel("example.com", 22, "ssh-ed25519", "SHA256:abc", HostKeyVerification.Unknown);
        var with = new HostKeyPromptViewModel("example.com", 22, "ssh-ed25519", "SHA256:abc", HostKeyVerification.Unknown,
            randomArt: "+--[ED25519 256]--+");

        Assert.IsFalse(without.HasRandomArt);
        Assert.IsTrue(with.HasRandomArt);
        Assert.AreEqual("+--[ED25519 256]--+", with.RandomArt);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_ChangedKey_ShowsWarning()
    {
        var vmChanged = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:changed123",
            HostKeyVerification.Changed
        );

        Assert.IsTrue(vmChanged.IsChanged);

        var vmUnknown = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:unknown456",
            HostKeyVerification.Unknown
        );

        Assert.IsFalse(vmUnknown.IsChanged);
    }

    /// <summary>
    /// 指纹变更时必须把 known_hosts 里的旧指纹一并摆出来(#476):只喊一句"变了"
    /// 用户无从判断这是自己刚重装的那台,还是一次劫持。首次连接没有旧指纹,那一行不显示。
    /// </summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_ChangedKey_ShowsRecordedFingerprint()
    {
        var vmChanged = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:new",
            HostKeyVerification.Changed,
            "SHA256:old"
        );

        Assert.IsTrue(vmChanged.HasKnownFingerprint);
        Assert.AreEqual("SHA256:old", vmChanged.KnownFingerprint);
        Assert.AreNotEqual(vmChanged.AdviceText, string.Empty);

        var vmUnknown = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:new",
            HostKeyVerification.Unknown
        );

        Assert.IsFalse(vmUnknown.HasKnownFingerprint);
        Assert.AreNotEqual(vmChanged.AdviceText, vmUnknown.AdviceText, "变更与首次连接给的是两套建议");
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_PortValidation_AcceptsValidRange()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "test.example.com",
            Username = "admin",

            // Valid port
            Port = 22,
        };
        bool canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsTrue(canExecute);

        // Port 0 — invalid
        vm.Port = 0;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsFalse(canExecute);

        // Port 65535 — valid max
        vm.Port = 65535;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsTrue(canExecute);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_SaveCommand_ReturnsProfile()
    {
        var vm = new ConnectionProfileViewModel
        {
            Name = "My Server",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/home/user/.ssh/id_rsa",
        };

        SessionProfile? result = null;
        vm.SaveCommand.Execute().Subscribe(profile => result = profile);

        Assert.IsNotNull(result);
        Assert.AreEqual("My Server", result!.Name);
        Assert.AreEqual("192.168.1.100", result.Host);
        Assert.AreEqual(2222, result.Port);
        Assert.AreEqual("deploy", result.Username);
        Assert.AreEqual(AuthMethod.PrivateKey, result.AuthMethod);
        Assert.AreEqual("/home/user/.ssh/id_rsa", result.PrivateKeyPath);
    }

    /// <summary>
    /// 关于页的架构必须报真实架构名。旧实现是 <c>Is64BitOperatingSystem ? "x64" : "x86"</c>,
    /// 只答"是不是 64 位"—— arm64 被显示成 x64、arm 被显示成 x86。
    /// 这里用纯函数逐架构断言,不必真有 ARM 机器才能守住。
    /// </summary>
    [TestMethod]
    [DataRow(Architecture.X64, "x64")]
    [DataRow(Architecture.X86, "x86")]
    [DataRow(Architecture.Arm64, "arm64")]
    [DataRow(Architecture.Arm, "arm")]
    public void DescribeArchitecture_NativeRun_ShowsRealArchitectureName(
        Architecture architecture,
        string expected
    ) => Assert.AreEqual(expected, SettingsViewModel.DescribeArchitecture(architecture, architecture));

    /// <summary>
    /// 进程架构与系统架构不一致(x64 版跑在 arm64 的仿真层上)时两者都要显示。
    /// 自更新按**进程**架构选产物,装错架构的人会一直留在仿真轨上,关于页得看得出来。
    /// </summary>
    [TestMethod]
    public void DescribeArchitecture_Emulated_ShowsBothProcessAndOsArchitecture()
    {
        string text = SettingsViewModel.DescribeArchitecture(Architecture.X64, Architecture.Arm64);

        Assert.IsTrue(text.Contains("x64", StringComparison.Ordinal), $"缺进程架构:{text}");
        Assert.IsTrue(text.Contains("arm64", StringComparison.Ordinal), $"缺系统架构:{text}");
    }

    /// <summary>关于页整行以当前架构收尾(拼接本身没写错)。</summary>
    [TestMethod]
    public void AboutOs_EndsWithCurrentArchitecture()
    {
        string architecture = SettingsViewModel.DescribeArchitecture(
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture
        );

        Assert.EndsWith($"({architecture})", SettingsViewModel.AboutOs);
    }

    // ———— 快捷键参考页的搜索 ————
    // 全部断言只用键位(F2、F5、Ctrl+Shift+L)而不用动作名:动作名随界面语言变,
    // 键位不变,测试才不会因为跑测机器的语言不同而红。

    /// <summary>搜索一个只属于某一组的键位:其余分组整组消失,计数跟着收窄。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_KeepsOnlyMatchingRows()
    {
        SettingsViewModel vm = CreateVm();
        int total = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);

        vm.ShortcutFilter = "F2";

        Assert.HasCount(1, vm.FilteredShortcutGroups, "F2 只在 SFTP 文件管理器一组里出现(F5 那里与任务管理器各有一行)");
        Assert.HasCount(1, vm.FilteredShortcutGroups[0].FilteredItems);
        Assert.IsTrue(vm.HasShortcutMatches);
        Assert.IsGreaterThan(1, total, "全量表不该只有一条,否则下面的收窄断言没意义");
    }

    /// <summary>键帽是分开的("Ctrl" "Shift" "L"),但用户会照文档敲加号写法,两种都要搜得到。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_MatchesBothSpacedAndPlusJoinedKeys()
    {
        SettingsViewModel vm = CreateVm();

        vm.ShortcutFilter = "Ctrl+Shift+L";
        int withPlus = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);

        vm.ShortcutFilter = "Ctrl Shift L";
        int withSpaces = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);

        Assert.AreEqual(1, withPlus, "加号写法搜不到 Ctrl+Shift+L");
        Assert.AreEqual(1, withSpaces, "空格写法搜不到 Ctrl Shift L");
    }

    /// <summary>搜不到时页面要能显示空态提示,而不是留一片什么都没有的白。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_NoMatch_ReportsEmptyState()
    {
        SettingsViewModel vm = CreateVm();

        vm.ShortcutFilter = "这个键位不存在";

        Assert.IsEmpty(vm.FilteredShortcutGroups);
        Assert.IsFalse(vm.HasShortcutMatches);
    }

    /// <summary>清空搜索词(只剩空白也算清空)回到全量表。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_Cleared_RestoresFullCatalog()
    {
        SettingsViewModel vm = CreateVm();
        int total = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);
        vm.ShortcutFilter = "F5";

        vm.ShortcutFilter = "   ";

        Assert.HasCount(vm.ShortcutGroups.Length, vm.FilteredShortcutGroups, "只有空白的搜索词等于没搜");
        Assert.AreEqual(total, vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count));
    }

    // ———— 快捷键参考页的分组折叠 ————

    /// <summary>分组默认展开:参考页的首要用途是通读,折叠是用户主动收纳。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutGroups_DefaultToExpanded()
    {
        SettingsViewModel vm = CreateVm();

        Assert.IsTrue(vm.ShortcutGroups.All(group => group.IsExpanded));
    }

    /// <summary>
    /// 搜索命中的分组必须临时展开 —— 搜到了却因为分组是收起的而看不见,等于没搜。
    /// </summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_ForcesMatchingGroupOpen()
    {
        SettingsViewModel vm = CreateVm();
        foreach (ShortcutGroup group in vm.ShortcutGroups)
        {
            group.IsExpanded = false;
        }

        vm.ShortcutFilter = "F5";

        Assert.IsTrue(vm.FilteredShortcutGroups[0].IsExpanded, "命中的分组应被临时展开");
    }

    /// <summary>
    /// 搜索清空后要把用户原本的折叠态还回去:搜一次不该把人手动收起来的分组全打开。
    /// </summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_Cleared_RestoresCollapsedState()
    {
        SettingsViewModel vm = CreateVm();
        ShortcutGroup first = vm.ShortcutGroups[0];
        first.IsExpanded = false;

        vm.ShortcutFilter = "Ctrl";
        vm.ShortcutFilter = string.Empty;

        Assert.IsFalse(first.IsExpanded, "搜索结束后应还原为用户收起的状态");
    }
}
