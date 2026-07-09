using System.Reactive.Linq;
using PulseTerm.App.Security;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public sealed class AuthenticationDialogViewModelTests
{
    [TestMethod]
    public void Step1_ShowsConnectingTarget_AndFirstConnectionFingerprintHint()
    {
        var vm = new AuthenticationDialogViewModel("192.168.1.100", 22, "root");

        Assert.AreEqual(1, vm.Step);
        Assert.IsTrue(vm.IsStep1);
        Assert.AreEqual("身份验证 - 第 1 步", vm.HeaderTitle);
        Assert.AreEqual("正在连接 root@192.168.1.100:22", vm.TargetText);
        StringAssert.Contains(vm.FingerprintText, "首次连接");
    }

    [TestMethod]
    public void KnownHost_ShowsStoredFingerprint()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "root",
            knownFingerprint: "SHA256:xK3fAbCdEfGhIjKlMnOpQrStUvWxYz9mPq");

        StringAssert.Contains(vm.FingerprintText, "SHA256:xK3f");
        StringAssert.Contains(vm.FingerprintText, "已信任");
    }

    [TestMethod]
    public async Task Next_RequiresUsername_ThenAdvancesToStep2()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, username: null);

        Assert.IsFalse(await vm.NextCommand.CanExecute.FirstAsync());

        vm.Username = "root";
        Assert.IsTrue(await vm.NextCommand.CanExecute.FirstAsync());

        vm.NextCommand.Execute().Subscribe();
        Assert.AreEqual(2, vm.Step);
        Assert.AreEqual("身份验证 - 第 2 步", vm.HeaderTitle);
        Assert.AreEqual("root@h:22", vm.TargetText);
        Assert.AreEqual("用户名: root", vm.UsernameLine);

        vm.BackCommand.Execute().Subscribe();
        Assert.AreEqual(1, vm.Step);
    }

    [TestMethod]
    public async Task Login_PasswordMethod_ReturnsPasswordResult()
    {
        var vm = new AuthenticationDialogViewModel("h", 2222, "root");
        vm.NextCommand.Execute().Subscribe();

        Assert.IsTrue(vm.IsPasswordMethod);
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());

        vm.Password = SecureStringConvert.FromPlaintext("s3cret");
        vm.RememberPassword = false;
        Assert.IsTrue(await vm.LoginCommand.CanExecute.FirstAsync());

        AuthenticationResult? result = null;
        vm.LoginCommand.Subscribe(r => result = r);
        vm.LoginCommand.Execute().Subscribe();

        Assert.IsNotNull(result);
        Assert.AreEqual("root", result.Username);
        Assert.AreEqual(AuthMethod.Password, result.AuthMethod);
        Assert.AreEqual("s3cret", SecureStringConvert.ToPlaintext(result.Password));
        Assert.IsFalse(result.RememberPassword);
    }

    [TestMethod]
    public async Task Login_KeyMethod_ReturnsKeyResult()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "root");
        vm.NextCommand.Execute().Subscribe();
        vm.SelectKeyCommand.Execute().Subscribe();

        Assert.IsTrue(vm.IsKeyMethod);
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());

        vm.PrivateKeyPath = "C:/keys/id_ed25519";
        vm.PrivateKeyPassphrase = "pp";
        Assert.IsTrue(await vm.LoginCommand.CanExecute.FirstAsync());

        AuthenticationResult? result = null;
        vm.LoginCommand.Subscribe(r => result = r);
        vm.LoginCommand.Execute().Subscribe();

        Assert.IsNotNull(result);
        Assert.AreEqual(AuthMethod.PrivateKey, result.AuthMethod);
        Assert.AreEqual("C:/keys/id_ed25519", result.PrivateKeyPath);
        Assert.AreEqual("pp", result.PrivateKeyPassphrase);
        Assert.IsNull(result.Password);
    }
}
