using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接对话框的逐字段内联校验。
/// </summary>
/// <remarks>
/// 保存 / 连接 / 测试三个按钮本来就按"主机非空 + 用户名非空 + 端口范围"灰着,
/// 但用户只看到按钮点不动、看不到为什么。这些属性把原因写在字段下方;
/// 私钥路径那一条是**新增**的校验 —— 填错原先要等到连接失败再从笼统的认证错误里猜。
/// </remarks>
[TestClass]
[TestCategory("ConnectionProfile")]
public sealed class ConnectionProfileValidationTests
{
    private static ConnectionProfileViewModel NewViewModel() => new();

    [TestMethod]
    public void EmptyHost_ReportsAnError_AndClearsOnceFilled()
    {
        ConnectionProfileViewModel vm = NewViewModel();

        Assert.IsNotNull(vm.HostError, "刚打开时主机是空的,应当有提示。");

        vm.Host = "web.example.com";

        Assert.IsNull(vm.HostError);
    }

    [TestMethod]
    public void WhitespaceHost_StillCountsAsEmpty()
    {
        ConnectionProfileViewModel vm = NewViewModel();

        vm.Host = "   ";

        Assert.IsNotNull(vm.HostError);
    }

    [TestMethod]
    public void PortOutsideTheValidRange_ReportsAnError()
    {
        ConnectionProfileViewModel vm = NewViewModel();

        vm.Port = 0;
        Assert.IsNotNull(vm.PortError);

        vm.Port = 70000;
        Assert.IsNotNull(vm.PortError);

        vm.Port = 22;
        Assert.IsNull(vm.PortError);
    }

    [TestMethod]
    public void EmptyUsername_ReportsAnError()
    {
        ConnectionProfileViewModel vm = NewViewModel();

        Assert.IsNotNull(vm.UsernameError);

        vm.Username = "deploy";

        Assert.IsNull(vm.UsernameError);
    }

    [TestMethod]
    public void AnonymousFtp_DoesNotRequireAUsername()
    {
        // 按钮的 canExecute 早就为匿名 FTP 放行了;提示文案必须跟它一致,
        // 否则会出现"按钮亮着但下面写着请填用户名"。
        ConnectionProfileViewModel vm = NewViewModel();
        vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();
        vm.FtpAnonymous = true;

        Assert.IsNull(vm.UsernameError);
    }

    [TestMethod]
    public void PrivateKeyPath_IsNotCheckedForPasswordAuth()
    {
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.Password;
        vm.PrivateKeyPath = "/definitely/not/here/id_ed25519";

        Assert.IsNull(vm.PrivateKeyPathError, "密码认证下不该校验私钥路径。");
    }

    [TestMethod]
    public void MissingPrivateKeyFile_ReportsAnError()
    {
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.PrivateKey;
        vm.PrivateKeyPath = Path.Combine(Path.GetTempPath(), $"velashell-missing-{Guid.NewGuid():N}");

        Assert.IsNotNull(vm.PrivateKeyPathError);
    }

    [TestMethod]
    public void ExistingPrivateKeyFile_IsAccepted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"velashell-key-{Guid.NewGuid():N}");
        File.WriteAllText(path, "not a real key");
        try
        {
            ConnectionProfileViewModel vm = NewViewModel();
            vm.AuthMethod = AuthMethod.PrivateKey;
            vm.PrivateKeyPath = path;

            Assert.IsNull(vm.PrivateKeyPathError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EmptyPrivateKeyPath_IsNotAnError()
    {
        // 空路径的含义是"用默认密钥",不是填错了。
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.PrivateKey;
        vm.PrivateKeyPath = "";

        Assert.IsNull(vm.PrivateKeyPathError);
    }

    [TestMethod]
    public void ErrorProperties_RaiseChangeNotifications()
    {
        // 不发通知的话,字段下方那行提示只在对话框第一次画出来时是对的。
        ConnectionProfileViewModel vm = NewViewModel();
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Host = "h";
        vm.Port = 2222;
        vm.Username = "u";
        vm.AuthMethod = AuthMethod.PrivateKey;
        vm.PrivateKeyPath = "/tmp/whatever";

        Assert.Contains(nameof(ConnectionProfileViewModel.HostError), changed);
        Assert.Contains(nameof(ConnectionProfileViewModel.PortError), changed);
        Assert.Contains(nameof(ConnectionProfileViewModel.UsernameError), changed);
        Assert.Contains(nameof(ConnectionProfileViewModel.PrivateKeyPathError), changed);
    }

    [TestMethod]
    public void CertificateAuth_WithoutCertificatePath_ReportsAnError()
    {
        // 与私钥不同,证书没有任何默认位置可退:空路径本身就是错。
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.Certificate;

        Assert.IsNotNull(vm.CertificatePathError);
    }

    [TestMethod]
    public void CertificatePath_IsNotCheckedOutsideCertificateAuth()
    {
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.PrivateKey;
        vm.CertificatePath = "/definitely/not/here/id_ed25519-cert.pub";

        Assert.IsNull(vm.CertificatePathError, "密钥认证下不该校验证书路径。");
    }

    [TestMethod]
    public void MissingCertificateFile_ReportsAnError()
    {
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.Certificate;
        vm.CertificatePath = Path.Combine(Path.GetTempPath(), $"velashell-missing-{Guid.NewGuid():N}-cert.pub");

        Assert.IsNotNull(vm.CertificatePathError);
    }

    [TestMethod]
    public void ExistingCertificateFile_IsAccepted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"velashell-cert-{Guid.NewGuid():N}-cert.pub");
        File.WriteAllText(path, "not a real certificate");
        try
        {
            ConnectionProfileViewModel vm = NewViewModel();
            vm.AuthMethod = AuthMethod.Certificate;
            vm.CertificatePath = path;

            Assert.IsNull(vm.CertificatePathError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void CertificateAuth_TreatsEmptyPrivateKeyAsAnError()
    {
        // 同一个空字符串,两种认证下的含义相反:私钥认证是"用默认密钥"(合法),
        // 证书认证则是缺了签名用的那把私钥 —— 证书只是 CA 的背书,自己签不了名。
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.Certificate;
        vm.PrivateKeyPath = "";

        Assert.IsNotNull(vm.PrivateKeyPathError);
    }

    [TestMethod]
    public void CertificateAuth_KeepsTheKeyFieldsVisible()
    {
        // 证书页是密钥页再加一个证书字段,私钥与口令两栏必须照常出现 ——
        // 藏起来的话用户根本没地方填那把用来签名的私钥。
        ConnectionProfileViewModel vm = NewViewModel();

        vm.AuthMethod = AuthMethod.PrivateKey;
        Assert.IsTrue(vm.ShowsPrivateKeyFields);
        Assert.IsFalse(vm.IsCertAuth);

        vm.AuthMethod = AuthMethod.Certificate;
        Assert.IsTrue(vm.ShowsPrivateKeyFields);
        Assert.IsTrue(vm.IsCertAuth);
        Assert.IsFalse(vm.IsKeyAuth);
        Assert.IsFalse(vm.IsPasswordAuth);

        vm.AuthMethod = AuthMethod.Password;
        Assert.IsFalse(vm.ShowsPrivateKeyFields);
    }

    [TestMethod]
    public void SwitchingToFtp_FallsBackFromCertificateAuth()
    {
        // FTP 没有证书认证,而切过去之后认证方式下拉恰好是隐藏的:不归一化的话,
        // 界面上再没有任何途径把它切回来,保存下去的却仍是 Certificate。
        ConnectionProfileViewModel vm = NewViewModel();
        vm.AuthMethod = AuthMethod.Certificate;

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();

        Assert.AreEqual(AuthMethod.Password, vm.AuthMethod);
    }
}
