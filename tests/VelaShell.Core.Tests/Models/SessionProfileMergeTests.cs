using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 终端标签重连从库里重读配置、只叠上这次会话现敲的凭据(<see cref="SessionProfileMerge.ForReconnect" />);
/// 登录框交回的那份只把登录字段写进库里那份(<see cref="SessionProfileMerge.CopyLoginFields" />)。
/// </summary>
[TestClass]
public sealed class SessionProfileMergeTests
{
    private static SessionProfile Stored() => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod",
        Host = "prod.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        RememberPassword = false,
        PostAuthCommand = "tmux attach",
        Ssh = new() { KexAlgorithms = "+diffie-hellman-group14-sha1" },
        Terminal = new() { StartupDirectory = "/srv/new" },
    };

    /// <summary>标签手里那份:上次连接用的,其余字段是旧的,密码是这次会话在登录框里现敲的。</summary>
    private static SessionProfile TabCopyOf(SessionProfile stored)
    {
        SessionProfile copy = stored.Clone();
        copy.Name = "prod (old name)";
        copy.PostAuthCommand = "echo old";
        copy.Ssh = null;
        copy.Terminal = new() { StartupDirectory = "/srv/old" };
        copy.Password = "typed-now";
        return copy;
    }

    [TestMethod]
    public void Reconnect_TakesEverythingFromTheLibrary_AndKeepsTheTypedPassword()
    {
        SessionProfile stored = Stored();
        SessionProfile tab = TabCopyOf(stored);

        SessionProfile merged = SessionProfileMerge.ForReconnect(stored, tab);

        Assert.AreEqual("prod", merged.Name);
        Assert.AreEqual("tmux attach", merged.PostAuthCommand);
        Assert.AreEqual("+diffie-hellman-group14-sha1", merged.Ssh!.KexAlgorithms, "别处刚放开的算法这次就用上");
        Assert.AreEqual("/srv/new", merged.Terminal!.StartupDirectory);
        Assert.AreEqual("typed-now", merged.Password, "库里没记的密码,这次会话现敲的照样用");
        Assert.IsNull(stored.Password, "库里那份不动");
        Assert.AreNotSame(stored.Ssh, merged.Ssh, "交出去的是副本");
    }

    [TestMethod]
    public void Reconnect_PasswordInTheLibraryWins()
    {
        SessionProfile stored = Stored();
        stored.Password = "changed-elsewhere";
        stored.RememberPassword = true;

        SessionProfile merged = SessionProfileMerge.ForReconnect(stored, TabCopyOf(stored));

        Assert.AreEqual("changed-elsewhere", merged.Password);
    }

    /// <summary>库里存着的那份在这台设备上解不开(密文原样):等于没有,用这次会话现敲的。</summary>
    [TestMethod]
    public void Reconnect_UnreadableStoredPassword_IsReplacedByTheTypedOne()
    {
        SessionProfile stored = Stored();
        stored.Password = StoredSecret.ProtectedPrefix + "AAAA";

        SessionProfile merged = SessionProfileMerge.ForReconnect(stored, TabCopyOf(stored));

        Assert.AreEqual("typed-now", merged.Password);
    }

    /// <summary>别处把这条配置指向了另一台主机 / 另一个账号 / 另一种认证:为原来那个账号敲的口令不跟过去。</summary>
    [TestMethod]
    [DataRow("host")]
    [DataRow("port")]
    [DataRow("user")]
    [DataRow("auth")]
    [DataRow("credential")]
    [DataRow("protocol")]
    public void Reconnect_LoginIdentityChanged_DropsTheTypedPassword(string change)
    {
        SessionProfile stored = Stored();
        SessionProfile tab = TabCopyOf(stored);
        switch (change)
        {
            case "protocol":
                // 插件终端协议(Telnet → 串口之类):为原来那个协议的登录敲的口令不跟过去。
                stored.ConnectionType = tab.ConnectionType = ConnectionType.Plugin;
                stored.PluginProtocolId = "test.serial";
                tab.PluginProtocolId = "test.telnet";
                break;
            case "host":
                stored.Host = "elsewhere.example.com";
                break;
            case "port":
                stored.Port = 2222;
                break;
            case "user":
                stored.Username = "deploy";
                break;
            case "auth":
                stored.AuthMethod = AuthMethod.Agent;
                break;
            default:
                stored.CredentialSource = CredentialReference.ForShared(Guid.NewGuid());
                break;
        }

        SessionProfile merged = SessionProfileMerge.ForReconnect(stored, tab);

        Assert.IsNull(merged.Password);
    }

    [TestMethod]
    public void Reconnect_HostComparison_IgnoresCase()
    {
        SessionProfile stored = Stored();
        SessionProfile tab = TabCopyOf(stored);
        tab.Host = "PROD.example.com";

        Assert.AreEqual("typed-now", SessionProfileMerge.ForReconnect(stored, tab).Password);
    }

    /// <summary>私钥口令只跟着同一把私钥走;私钥路径本身是配置,以库里为准。</summary>
    [TestMethod]
    public void Reconnect_KeyPassphrase_FollowsTheSameKeyOnly()
    {
        SessionProfile stored = Stored();
        stored.AuthMethod = AuthMethod.PrivateKey;
        stored.PrivateKeyPath = "/keys/id_ed25519";
        SessionProfile tab = stored.Clone();
        tab.PrivateKeyPassphrase = "typed-phrase";

        Assert.AreEqual("typed-phrase", SessionProfileMerge.ForReconnect(stored, tab).PrivateKeyPassphrase);

        stored.PrivateKeyPath = "/keys/id_rsa";
        SessionProfile switched = SessionProfileMerge.ForReconnect(stored, tab);
        Assert.IsNull(switched.PrivateKeyPassphrase, "换了一把私钥,上一把的口令不能拿来试");
        Assert.AreEqual("/keys/id_rsa", switched.PrivateKeyPath);
    }

    /// <summary>引用共享凭据的配置:库里从不存认证材料,登录框里手输的那一份(路径与口令)原样叠上。</summary>
    [TestMethod]
    public void Reconnect_SharedCredentialProfile_KeepsTheInlineMaterialTypedInTheLoginDialog()
    {
        var reference = CredentialReference.ForShared(Guid.NewGuid());
        SessionProfile stored = Stored();
        stored.CredentialSource = reference;
        stored.AuthMethod = AuthMethod.PrivateKey;
        SessionProfile tab = stored.Clone();
        tab.PrivateKeyPath = "/keys/typed";
        tab.PrivateKeyPassphrase = "typed-phrase";

        SessionProfile merged = SessionProfileMerge.ForReconnect(stored, tab);

        Assert.AreEqual("/keys/typed", merged.PrivateKeyPath);
        Assert.AreEqual("typed-phrase", merged.PrivateKeyPassphrase);
        Assert.AreEqual(reference, merged.CredentialSource);
    }

    [TestMethod]
    public void Reconnect_NothingTyped_IsJustTheLibraryCopy()
    {
        SessionProfile stored = Stored();
        SessionProfile tab = stored.Clone();

        SessionProfile merged = SessionProfileMerge.ForReconnect(stored, tab);

        Assert.IsNull(merged.Password);
        Assert.AreEqual(stored.Id, merged.Id);
    }

    /// <summary>登录框交回的那份:只有登录那几项写进库里那份,别的字段(哪怕手里那份是旧的)一个不碰。</summary>
    [TestMethod]
    public void CopyLoginFields_TouchesOnlyTheLoginFields()
    {
        SessionProfile stored = Stored();
        SessionProfile prompted = TabCopyOf(stored);
        prompted.Username = "deploy";
        prompted.AuthMethod = AuthMethod.Certificate;
        prompted.RememberPassword = true;
        prompted.PrivateKeyPath = "/keys/id";
        prompted.PrivateKeyPassphrase = "phrase";
        prompted.CertificatePath = "/keys/id-cert.pub";

        SessionProfileMerge.CopyLoginFields(prompted, stored);

        Assert.AreEqual("deploy", stored.Username);
        Assert.AreEqual(AuthMethod.Certificate, stored.AuthMethod);
        Assert.IsTrue(stored.RememberPassword);
        Assert.AreEqual("typed-now", stored.Password);
        Assert.AreEqual("/keys/id", stored.PrivateKeyPath);
        Assert.AreEqual("phrase", stored.PrivateKeyPassphrase);
        Assert.AreEqual("/keys/id-cert.pub", stored.CertificatePath);
        Assert.AreEqual("prod", stored.Name, "名字、算法、认证后命令、初始目录以库里为准");
        Assert.AreEqual("tmux attach", stored.PostAuthCommand);
        Assert.AreEqual("+diffie-hellman-group14-sha1", stored.Ssh!.KexAlgorithms);
        Assert.AreEqual("/srv/new", stored.Terminal!.StartupDirectory);
    }

    [TestMethod]
    public void CopyLoginFields_SwitchingToTailscale_DropsTheCredentialReference()
    {
        SessionProfile stored = Stored();
        stored.CredentialSource = CredentialReference.ForShared(Guid.NewGuid());
        SessionProfile prompted = stored.Clone();
        prompted.AuthMethod = AuthMethod.Tailscale;
        CredentialMaterial.ClearTailscaleCredentials(prompted);

        SessionProfileMerge.CopyLoginFields(prompted, stored);

        Assert.AreEqual(AuthMethod.Tailscale, stored.AuthMethod);
        Assert.IsNull(stored.CredentialSource);
        Assert.IsFalse(stored.RememberPassword);
    }
}
