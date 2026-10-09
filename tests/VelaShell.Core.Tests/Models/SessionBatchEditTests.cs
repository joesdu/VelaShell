using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>批量修改:只动勾了的项,协议不支持的跳过并计数,跳板不许接出环。</summary>
[TestClass]
[TestCategory("SessionBatchEdit")]
public sealed class SessionBatchEditTests
{
    private static SessionProfile Ssh(string name) => new() { Name = name, Host = name + ".example.com", Username = "root" };

    [TestMethod]
    public void Apply_ChangesOnlyTheGivenFields_AndReturnsCopies()
    {
        SessionProfile a = Ssh("a");
        a.Notes = "keep";
        SessionProfile b = Ssh("b");
        b.Username = "deploy";
        b.Port = 2222;

        SessionBatchEditResult result = new SessionBatchEdit { Username = " deploy ", Port = 2222 }.Apply([a, b], [a, b]);

        SessionProfile changed = result.Changed.Single();
        Assert.AreEqual(a.Id, changed.Id, "b 本来就是这个值,没有变化的不在结果里");
        Assert.AreEqual("deploy", changed.Username, "用户名去首尾空白");
        Assert.AreEqual(2222, changed.Port);
        Assert.AreEqual("keep", changed.Notes);
        Assert.AreEqual("root", a.Username, "原对象不动");
    }

    [TestMethod]
    public void Apply_PrivateKeyAuth_SkipsFtpAndPlugins()
    {
        SessionProfile ssh = Ssh("ssh");
        var ftp = new SessionProfile { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP, Ftp = new FtpSettings() };
        var plugin = new SessionProfile { Name = "s3", Host = "s", ConnectionType = ConnectionType.Plugin, PluginProtocolId = "velashell.s3" };
        var edit = new SessionBatchEdit
        {
            Auth = new SessionBatchAuth { Kind = SessionBatchAuthKind.PrivateKey, PrivateKeyPath = " ~/.ssh/id_ed25519 " }
        };

        SessionBatchEditResult result = edit.Apply([ssh, ftp, plugin], [ssh, ftp, plugin]);

        Assert.AreEqual(2, result.AuthSkipped);
        SessionProfile changed = result.Changed.Single();
        Assert.AreEqual(AuthMethod.PrivateKey, changed.AuthMethod);
        Assert.AreEqual("~/.ssh/id_ed25519", changed.PrivateKeyPath);
    }

    [TestMethod]
    public void Apply_PasswordAuth_ReachesFtpButNotAnonymousFtp()
    {
        var ftp = new SessionProfile { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP, Ftp = new FtpSettings() };
        var anonymous = new SessionProfile { Name = "anon", Host = "a", ConnectionType = ConnectionType.FTP, Ftp = new FtpSettings { Anonymous = true } };
        ftp.CredentialSource = CredentialReference.ForShared(Guid.NewGuid());

        SessionBatchEditResult result = new SessionBatchEdit
        {
            Auth = new SessionBatchAuth { Kind = SessionBatchAuthKind.Password, Password = "pw" }
        }.Apply([ftp, anonymous], [ftp, anonymous]);

        Assert.AreEqual(1, result.AuthSkipped);
        SessionProfile changed = result.Changed.Single();
        Assert.AreEqual("pw", changed.Password);
        Assert.IsTrue(changed.RememberPassword);
        Assert.IsNull(changed.CredentialSource, "改成单独的密码就不再引用共享凭据");
    }

    [TestMethod]
    public void Apply_SharedCredential_SetsTheReference_AndClearsInlineMaterial()
    {
        SessionProfile ssh = Ssh("ssh");
        ssh.Password = "old";
        ssh.PrivateKeyPath = "~/.ssh/old";
        var credential = new SharedCredential { Id = Guid.NewGuid(), Name = "ops", AuthMethod = AuthMethod.PrivateKey };
        var ftp = new SessionProfile { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP };

        SessionBatchEditResult result = new SessionBatchEdit
        {
            Auth = new SessionBatchAuth { Kind = SessionBatchAuthKind.SharedCredential, Credential = credential }
        }.Apply([ssh, ftp], [ssh, ftp]);

        Assert.AreEqual(1, result.AuthSkipped, "私钥类的共享凭据给不了 FTP");
        SessionProfile changed = result.Changed.Single();
        Assert.IsTrue(changed.CredentialSource!.TryGetSharedId(out Guid id) && id == credential.Id);
        Assert.IsNull(changed.Password);
        Assert.IsNull(changed.PrivateKeyPath);
    }

    [TestMethod]
    public void Apply_JumpHost_OnlyForSshAndSftp_NeverToItself()
    {
        SessionProfile bastion = Ssh("bastion");
        SessionProfile app = Ssh("app");
        var sftp = new SessionProfile { Name = "files", Host = "x", ConnectionType = ConnectionType.SFTP };
        var ftp = new SessionProfile { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP };

        SessionBatchEditResult result = new SessionBatchEdit { ChangeJumpHost = true, JumpHostProfileId = bastion.Id }
            .Apply([app, sftp, ftp], [bastion, app, sftp, ftp]);

        Assert.AreEqual(1, result.JumpHostSkipped);
        Assert.IsTrue(result.Changed.All(p => p.JumpHostProfileId == bastion.Id));
        Assert.HasCount(2, result.Changed);
    }

    [TestMethod]
    public void Apply_JumpHost_ThatWouldCloseACycle_IsSkippedForAll()
    {
        SessionProfile a = Ssh("a");
        SessionProfile b = Ssh("b");
        SessionProfile c = Ssh("c");
        c.JumpHostProfileId = a.Id; // c 经 a 跳;再让 a、b 经 c 跳,a→c→a 成环

        SessionBatchEditResult result = new SessionBatchEdit { ChangeJumpHost = true, JumpHostProfileId = c.Id }.Apply([a, b], [a, b, c]);

        Assert.AreEqual(2, result.JumpHostSkipped);
        Assert.IsEmpty(result.Changed);
    }

    [TestMethod]
    public void Apply_ClearingTheJumpHost_IsAChangeToDirect()
    {
        SessionProfile app = Ssh("app");
        app.JumpHostProfileId = Guid.NewGuid();

        SessionBatchEditResult result = new SessionBatchEdit { ChangeJumpHost = true, JumpHostProfileId = null }.Apply([app], [app]);

        Assert.IsNull(result.Changed.Single().JumpHostProfileId);
    }

    [TestMethod]
    public void HasChanges_IsFalse_ForAnEmptyEdit()
    {
        Assert.IsFalse(new SessionBatchEdit().HasChanges);
        Assert.IsTrue(new SessionBatchEdit { ChangeJumpHost = true }.HasChanges);
    }
}
