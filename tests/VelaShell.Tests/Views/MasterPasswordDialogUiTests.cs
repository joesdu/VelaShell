using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Behaviors;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Localization;
using VelaShell.Security;
using VelaShell.Services.MasterPassword;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 主密码对话框与设置页那几行:解锁(输错的提示与退避、恢复码、恢复后设新密码)、开启(两次输入、确认、抄恢复码)、
/// 忘记;外壳与别的对话框同一规格、口令框走 SecurePasswordBox。服务用真的(临时数据目录,不碰系统密钥链)。
/// </summary>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class MasterPasswordDialogUiTests
{
    private const string Password = "correct horse battery staple";

    private static HeadlessUnitTestSession _session = null!;
    private string _root = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MasterPasswordDialogUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 留给临时目录清理。
        }
    }

    private string KeyFile => Path.Combine(_root, "secret.key");

    private static System.Security.SecureString Secure(string text) => SecureStringConvert.FromPlaintext(text)!;

    /// <summary>开好主密码、再「重启」一次:交回锁着的服务。</summary>
    private async Task<(MasterPasswordService Service, string? RecoveryCode)> LockedServiceAsync(bool withRecovery = true)
    {
        var first = new MasterPasswordService(new AesSecretProtector(KeyFile));
        MasterPasswordChangeResult enabled = await first.EnableAsync(System.Text.Encoding.UTF8.GetBytes(Password), withRecovery);
        Assert.AreEqual(MasterPasswordChangeStatus.Done, enabled.Status);
        var service = new MasterPasswordService(new AesSecretProtector(KeyFile));
        Assert.IsTrue(service.IsLocked);
        return (service, enabled.RecoveryCode);
    }

    [TestMethod]
    public async Task Unlock_WrongThenRight()
    {
        (MasterPasswordService service, _) = await LockedServiceAsync();
        var vm = new MasterPasswordDialogViewModel(service, MasterPasswordDialogMode.Unlock)
        {
            Password = Secure("not it")
        };
        Assert.IsFalse(await vm.ConfirmAsync());
        Assert.StartsWith(Strings.Get("MasterPassword_WrongPassword"), vm.ErrorText);
        Assert.IsFalse(vm.CanConfirm, "输错之后要等一会儿才能再试(1、2、4… 秒)");
        Assert.IsNull(vm.Password, "输错的那份清掉");
        Assert.IsTrue(service.IsLocked);

        await Task.Delay(service.RetryAfter + TimeSpan.FromMilliseconds(200));
        var again = new MasterPasswordDialogViewModel(service, MasterPasswordDialogMode.Unlock) { Password = Secure(Password) };
        Assert.IsTrue(await again.ConfirmAsync());
        Assert.IsTrue(again.Succeeded);
        Assert.IsFalse(service.IsLocked);
    }

    [TestMethod]
    public async Task RecoveryCode_UnlocksThenAsksForANewPassword()
    {
        (MasterPasswordService service, string? code) = await LockedServiceAsync();
        var vm = new MasterPasswordDialogViewModel(service, MasterPasswordDialogMode.Unlock) { UseRecoveryCode = true };
        Assert.IsTrue(vm.HasRecoveryCodeOnFile);
        Assert.IsTrue(vm.ShowsRecoveryInput);
        Assert.IsFalse(vm.ShowsPassword);

        vm.RecoveryCodeInput = code!;
        Assert.IsFalse(await vm.ConfirmAsync(), "解开之后不关框:接着设新的主密码");
        Assert.AreEqual(MasterPasswordDialogMode.SetAfterRecovery, vm.Mode);
        Assert.IsTrue(vm.Succeeded, "这一步取消也算解开了");
        Assert.IsFalse(service.IsLocked);

        vm.NewPassword = Secure("a brand new passphrase");
        vm.ConfirmPassword = Secure("a brand new passphrase");
        Assert.IsTrue(await vm.ConfirmAsync());

        var restarted = new MasterPasswordService(new AesSecretProtector(KeyFile));
        Assert.AreEqual(MasterPasswordUnlockStatus.Unlocked,
            (await restarted.UnlockAsync(System.Text.Encoding.UTF8.GetBytes("a brand new passphrase"))).Status);
    }

    [TestMethod]
    public async Task Enable_ChecksBothEntriesAndTheAcknowledgement_ThenShowsTheRecoveryCodeOnce()
    {
        var service = new MasterPasswordService(new AesSecretProtector(KeyFile));
        var vm = new MasterPasswordDialogViewModel(service, MasterPasswordDialogMode.Enable)
        {
            NewPassword = Secure("short"),
            ConfirmPassword = Secure("shorT")
        };
        Assert.IsTrue(vm.IsWeak, "短于 12 个字符:只提示,不拦");

        Assert.IsFalse(await vm.ConfirmAsync());
        Assert.AreEqual(Strings.Get("MasterPassword_Mismatch"), vm.ErrorText);
        vm.ConfirmPassword = Secure("short");
        Assert.IsFalse(await vm.ConfirmAsync());
        Assert.AreEqual(Strings.Get("MasterPassword_AcknowledgeRequired"), vm.ErrorText, "不勾「忘了就无法找回」不让开");
        Assert.IsFalse(service.IsEnabled);

        vm.AcknowledgeNoBackdoor = true;
        Assert.IsFalse(await vm.ConfirmAsync(), "开好之后不关框:先抄恢复码");
        Assert.AreEqual(MasterPasswordDialogMode.ShowRecoveryCode, vm.Mode);
        Assert.HasCount(7, vm.RecoveryCodeDisplay!.Split('-'));
        Assert.IsTrue(service.IsEnabled);
        Assert.IsTrue(await vm.ConfirmAsync(), "「我已抄下」关框");
        Assert.AreEqual(Strings.Get("MasterPassword_Enabled"), vm.CompletionNotice);
    }

    [TestMethod]
    public async Task Forget_StartsAFreshKey_AndSaysWhereTheOldOnesAre()
    {
        (MasterPasswordService service, _) = await LockedServiceAsync();
        var vm = new MasterPasswordDialogViewModel(service, MasterPasswordDialogMode.Unlock);

        vm.BeginForget();
        Assert.AreEqual(MasterPasswordDialogMode.Forget, vm.Mode);
        Assert.IsTrue(vm.IsDestructive, "确定按钮画成红框的轮廓按钮(DESIGN.md §5.1)");
        Assert.IsTrue(await vm.ConfirmAsync());

        Assert.IsFalse(service.IsEnabled);
        Assert.IsFalse(service.IsLocked);
        Assert.Contains(".forgotten-", vm.CompletionNotice);
    }

    [TestMethod]
    public async Task SettingsRows_FollowTheRealState_AndWriteTheMirrorFlag()
    {
        var service = new MasterPasswordService(new AesSecretProtector(KeyFile));
        GeneralOptions general = new() { MasterPasswordProtection = true }; // 设置里写着 true,其实没开
        var rows = new MasterPasswordSettingsViewModel(service, () => general, SystemSessionLockProbes.ForCurrentPlatform());

        await rows.RefreshAsync();
        Assert.IsFalse(rows.IsEnabled, "状态现问服务,不看设置里的开关");
        Assert.AreEqual(Strings.Get("SetGeneral_MasterPasswordStatusOff"), rows.StatusText);
        Assert.IsFalse(general.MasterPasswordProtection, "镜像照实际状态回写");

        Assert.AreEqual(MasterPasswordChangeStatus.Done, (await service.EnableAsync(System.Text.Encoding.UTF8.GetBytes(Password), false)).Status);
        await rows.RefreshAsync();
        Assert.IsTrue(rows.IsUnlocked);
        Assert.IsTrue(general.MasterPasswordProtection);
        await rows.LockNowAsync();
        Assert.IsTrue(rows.IsLocked);
        Assert.AreEqual(Strings.Get("SetGeneral_MasterPasswordStatusLocked") + " · " + Strings.Get("SetGeneral_MasterPasswordNoRecovery"),
            rows.StatusText);
    }

    [TestMethod]
    public Task TheDialog_FollowsTheDialogTitleBarSpec_AndUsesSecurePasswordBoxes() => _session.RunOnUiAsync(async () =>
    {
        (MasterPasswordService service, _) = await LockedServiceAsync();
        var view = new MasterPasswordDialogView { DataContext = new MasterPasswordDialogViewModel(service, MasterPasswordDialogMode.Unlock) };
        view.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            DialogTitleBarAssert.FollowsSpec(view);
            Assert.AreEqual(Strings.Get("MasterPassword_UnlockTitle"), view.Title);
            TextBox password = view.FindControl<TextBox>("PasswordBox")!;
            Assert.IsTrue(password.IsVisible);
            Assert.IsTrue(SecurePasswordBox.GetEnabled(password), "口令不进剪贴板、不进历史");
            Assert.IsTrue(SecurePasswordBox.GetEnabled(view.FindControl<TextBox>("NewPasswordBox")!));
            Assert.IsTrue(view.FindControl<Button>("ForgotButton")!.IsVisible);
            Assert.IsTrue(view.FindControl<Button>("UseRecoveryButton")!.IsVisible);
            Assert.IsFalse(view.FindControl<Button>("DangerButton")!.IsVisible);
            Assert.Contains(b => b.Name == "ConfirmButton" && b.IsVisible, view.GetVisualDescendants().OfType<Button>());
        }
        finally
        {
            view.Close();
        }
    });
}
