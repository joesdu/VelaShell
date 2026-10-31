using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Import;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Config;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 与 OpenSSH 互通的 <c>~/.ssh/config</c> 两个方向:导入补上的映射(算法清单、压缩、agent、转发、证书),
/// 以及反向的导出(<see cref="SshConfigExporter" />,导出的文件再用 SSH 库的解析器读回来核对)。
/// </summary>
[TestClass]
public sealed class SshConfigInteropTests
{
    private readonly List<string> _temp = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string path in _temp)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string WriteConfig(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sshconfig-interop-{Guid.NewGuid():N}");
        File.WriteAllText(path, content);
        _temp.Add(path);
        return path;
    }

    private string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sshconfig-interop-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _temp.Add(path);
        return path;
    }

    private static async Task<ImportedSession> ScanOneAsync(string configPath, string alias)
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        SessionImportScan scan = await new SshConfigImportService(repository).ScanAsync(configPath);
        return scan.Items.Single(i => i.Name == alias);
    }

    // ------------------------------------------------------------ 导入:算法清单

    /// <summary>
    /// §134 的自定义清单写法本来就是 ssh_config 的,原样带过来;本版没实现的名字(CBC、group1)去掉并说一声,
    /// 去掉之后一个不剩、或者拼错了的整条不导。
    /// </summary>
    [TestMethod]
    public async Task 算法清单原样带过来_没实现的去掉_拼错的整条不导()
    {
        string config = WriteConfig(
            """
            Host legacy
                Ciphers aes128-cbc,aes256-ctr
                KexAlgorithms +diffie-hellman-group1-sha1
                MACs -hmac-sha1
                HostKeyAlgorithms ^ssh-ed25519,rsa-sha2-512
            Host typo
                Ciphers aes256-ctrr
            """);

        ImportedSession legacy = await ScanOneAsync(config, "legacy");
        Assert.AreEqual("aes256-ctr", legacy.Ssh?.Ciphers);
        Assert.IsNull(legacy.Ssh?.KexAlgorithms, "只写了没实现的:留个空前缀等于什么都没写");
        Assert.AreEqual("-hmac-sha1", legacy.Ssh?.Macs);
        Assert.AreEqual("^ssh-ed25519,rsa-sha2-512", legacy.Ssh?.HostKeyAlgorithms);
        Assert.Contains(n => n.Contains("Ciphers") && n.Contains("aes128-cbc"), legacy.Notes);
        Assert.Contains(n => n.Contains("KexAlgorithms") && n.Contains("diffie-hellman-group1-sha1"), legacy.Notes);

        ImportedSession typo = await ScanOneAsync(config, "typo");
        Assert.IsNull(typo.Ssh?.Ciphers);
        Assert.Contains(n => n.Contains("Ciphers") && n.Contains("aes256-ctrr"), typo.Notes);
    }

    [TestMethod]
    public async Task 压缩与agent转发()
    {
        string config = WriteConfig(
            """
            Host a
                Compression yes
                ForwardAgent yes
            Host c
                User nobody
            """);

        ImportedSession a = await ScanOneAsync(config, "a");
        Assert.IsTrue(a.Ssh?.Compression);
        Assert.IsTrue(a.Ssh?.AgentForwarding);
        Assert.IsNull(a.Ssh?.AgentEndpoint, "ForwardAgent yes 且没写 IdentityAgent:转发系统默认的 agent");

        Assert.IsNull((await ScanOneAsync(config, "c")).Ssh, "一项都没开:存 null");
    }

    /// <summary>本机用得了的套接字路径(正斜杠,ssh_config 里写 Windows 路径也这么写)。</summary>
    private static string LocalAgentPath(string name) => Path.Combine(Path.GetTempPath(), name).Replace('\\', '/');

    /// <summary>别的平台上的路径:这台电脑上用不了。</summary>
    private static string ForeignAgentPath => OperatingSystem.IsWindows() ? "/run/user/1000/agent.sock" : "C:/agents/agent.sock";

    /// <summary>
    /// <c>IdentityAgent</c> / <c>ForwardAgent</c> 指向非默认的 agent:映射到连接的 agent 端点(一条连接只用一个,认证与转发共用)。
    /// <c>ForwardAgent yes</c> 转发 <c>IdentityAgent</c> 指的那个(与 OpenSSH 一致);认证走 agent、转发又指了另一个时转发不导;
    /// 认证不走 agent(私钥)时端点只给转发用;这台电脑上用不了的端点、没设的环境变量不导,都进说明。
    /// </summary>
    [TestMethod]
    public async Task 非默认的agent端点映射到连接的agent端点()
    {
        string pinned = LocalAgentPath("vs-pinned.sock");
        string other = LocalAgentPath("vs-other.sock");
        string config = WriteConfig(
            $"""
            Host pinned
                IdentityAgent {pinned}
                ForwardAgent yes
            Host pinnedfwd
                IdentityAgent {pinned}
                ForwardAgent {pinned}
            Host conflict
                IdentityAgent {pinned}
                ForwardAgent {other}
            Host keyfwd
                IdentityFile /keys/id
                ForwardAgent {other}
            Host keyidentity
                IdentityFile /keys/id
                IdentityAgent {pinned}
                ForwardAgent yes
            Host keyonly
                IdentityFile /keys/id
                IdentityAgent {pinned}
            Host foreign
                IdentityAgent {ForeignAgentPath}
            Host foreignfwd
                ForwardAgent {ForeignAgentPath}
            Host varunset
                ForwardAgent $VS_NO_SUCH_AGENT_VARIABLE
            """);

        foreach (string alias in new[] { "pinned", "pinnedfwd" })
        {
            ImportedSession item = await ScanOneAsync(config, alias);
            Assert.AreEqual(AuthMethod.Agent, item.AuthMethod, alias);
            Assert.AreEqual(pinned, item.Ssh?.AgentEndpoint, alias);
            Assert.IsTrue(item.Ssh?.AgentForwarding, $"{alias}:转发的就是认证用的那个");
            Assert.IsEmpty(item.Notes, alias);
        }

        ImportedSession conflict = await ScanOneAsync(config, "conflict");
        Assert.AreEqual(AuthMethod.Agent, conflict.AuthMethod);
        Assert.AreEqual(pinned, conflict.Ssh?.AgentEndpoint, "认证用的那个留着");
        Assert.IsFalse(conflict.Ssh?.AgentForwarding ?? false, "转发指了另一个:换成认证用的那个等于多交出几把钥,不导");
        Assert.Contains(n => n.Contains("ForwardAgent") && n.Contains(other), conflict.Notes);

        ImportedSession keyfwd = await ScanOneAsync(config, "keyfwd");
        Assert.IsNull(keyfwd.AuthMethod, "私钥认证");
        Assert.AreEqual(other, keyfwd.Ssh?.AgentEndpoint, "认证不走 agent:端点只给转发用");
        Assert.IsTrue(keyfwd.Ssh?.AgentForwarding);

        ImportedSession keyidentity = await ScanOneAsync(config, "keyidentity");
        Assert.AreEqual(pinned, keyidentity.Ssh?.AgentEndpoint, "ForwardAgent yes 转发 IdentityAgent 指的那个");
        Assert.IsTrue(keyidentity.Ssh?.AgentForwarding);

        Assert.IsNull((await ScanOneAsync(config, "keyonly")).Ssh, "私钥认证、不转发:端点用不上,不存");

        ImportedSession foreign = await ScanOneAsync(config, "foreign");
        Assert.IsNull(foreign.AuthMethod);
        Assert.IsNull(foreign.Ssh);
        Assert.Contains(n => n.Contains(ForeignAgentPath), foreign.Notes, "别的平台的路径这台电脑上用不了:说一声");

        ImportedSession foreignfwd = await ScanOneAsync(config, "foreignfwd");
        Assert.IsNull(foreignfwd.Ssh);
        Assert.Contains(n => n.Contains(ForeignAgentPath), foreignfwd.Notes);

        ImportedSession varunset = await ScanOneAsync(config, "varunset");
        Assert.IsNull(varunset.Ssh);
        Assert.Contains(n => n.Contains("VS_NO_SUCH_AGENT_VARIABLE"), varunset.Notes);
    }

    /// <summary>
    /// 维护者 2026-10-25 定的口径:<c>ForwardX11</c> / <c>ForwardX11Trusted</c> / <c>ObscureKeystrokeTiming</c> 只认明写的值,
    /// 没写按 VelaShell 自己的默认 —— 不转发 X11、转发时受信任、不混淆按键时序。OpenSSH 的两个默认(非受信任、混淆开着)不换算过来;
    /// 只写了 <c>ForwardX11 yes</c> 时信任级别与 ssh 不同,这一点进导入说明。认不出的值不导、说明一句。
    /// </summary>
    [TestMethod]
    public async Task X11与按键时序混淆只认明写的值()
    {
        string config = WriteConfig(
            """
            Host x11plain
                ForwardX11 yes
            Host x11untrusted
                ForwardX11 YES
                ForwardX11Trusted no
            Host x11trusted
                ForwardX11 yes
                ForwardX11Trusted yes
            Host x11off
                ForwardX11 no
                ForwardX11Trusted no
            Host obscure
                ObscureKeystrokeTiming yes
            Host obscureoff
                ObscureKeystrokeTiming no
            Host obscure20
                ObscureKeystrokeTiming interval:20
            Host obscure80
                ObscureKeystrokeTiming interval:80
            Host typo
                ForwardX11 yess
                ObscureKeystrokeTiming interval:0
            Host plain
                User nobody
            """);

        ImportedSession plain = await ScanOneAsync(config, "x11plain");
        Assert.IsTrue(plain.Ssh?.X11Forwarding);
        Assert.IsTrue(plain.Ssh?.X11Trusted, "没写 ForwardX11Trusted:按 VelaShell 的默认(受信任),不换算成 OpenSSH 的非受信任");
        Assert.Contains(n => n.Contains("ForwardX11Trusted"), plain.Notes, "信任级别与 ssh 不同,要说出来");

        ImportedSession untrusted = await ScanOneAsync(config, "x11untrusted");
        Assert.IsTrue(untrusted.Ssh?.X11Forwarding);
        Assert.IsFalse(untrusted.Ssh?.X11Trusted ?? true);
        Assert.IsEmpty(untrusted.Notes);

        ImportedSession trusted = await ScanOneAsync(config, "x11trusted");
        Assert.IsTrue(trusted.Ssh?.X11Forwarding);
        Assert.IsTrue(trusted.Ssh?.X11Trusted);
        Assert.IsEmpty(trusted.Notes);

        Assert.IsNull((await ScanOneAsync(config, "x11off")).Ssh, "没开 X11 时信任级别不算数:一项都没开,存 null");

        Assert.IsTrue((await ScanOneAsync(config, "obscure")).Ssh?.ObscureKeystrokeTiming);
        Assert.IsNull((await ScanOneAsync(config, "obscureoff")).Ssh);
        ImportedSession obscure20 = await ScanOneAsync(config, "obscure20");
        Assert.IsTrue(obscure20.Ssh?.ObscureKeystrokeTiming);
        Assert.IsEmpty(obscure20.Notes, "20 毫秒就是 VelaShell 的节拍,没什么要说的");
        ImportedSession obscure80 = await ScanOneAsync(config, "obscure80");
        Assert.IsTrue(obscure80.Ssh?.ObscureKeystrokeTiming, "interval:N 也是开");
        Assert.Contains(n => n.Contains("interval:80"), obscure80.Notes, "节拍固定 20 毫秒:不一样要说一声");

        ImportedSession typo = await ScanOneAsync(config, "typo");
        Assert.IsNull(typo.Ssh, "拼错的不当成 yes,也不当成 no 悄悄吞掉");
        Assert.Contains(n => n.Contains("ForwardX11 yess"), typo.Notes);
        Assert.Contains(n => n.Contains("ObscureKeystrokeTiming interval:0"), typo.Notes, "超出 1–1000 毫秒的节拍与库一样不认");

        ImportedSession nothing = await ScanOneAsync(config, "plain");
        Assert.IsNull(nothing.Ssh, "没写就按 VelaShell 的默认:OpenSSH 默认开着的按键时序混淆不跟着开");
    }

    /// <summary>
    /// 导出与导入对称:X11 的信任级别一定明写(两边的默认不同);按键时序混淆开着才写 —— 关着的不写 <c>no</c>
    /// (这个关键字 OpenSSH 9.5 才有,老版本读到不认识的关键字整份配置报错)。导出再导入,两项都回到原样。
    /// </summary>
    [TestMethod]
    public async Task X11与按键时序混淆导出后读得回来()
    {
        SessionProfile on = new()
        {
            Name = "on",
            Host = "10.0.0.21",
            Ssh = new SshSessionOptions { X11Forwarding = true, X11Trusted = false, ObscureKeystrokeTiming = true },
        };
        SessionProfile trusted = new()
        {
            Name = "trusted",
            Host = "10.0.0.22",
            Ssh = new SshSessionOptions { X11Forwarding = true, X11Trusted = true },
        };
        SessionProfile off = new() { Name = "off", Host = "10.0.0.23", Ssh = new SshSessionOptions { Compression = true } };

        SshConfigExport export = SshConfigExporter.Write(Archive(on, trusted, off));

        SshHostConfig onHost = ReadBack(export, "on");
        Assert.IsTrue(onHost.ForwardX11);
        Assert.AreEqual("no", onHost.First("ForwardX11Trusted"));
        Assert.AreEqual("yes", onHost.First("ObscureKeystrokeTiming"));
        Assert.AreEqual("yes", ReadBack(export, "trusted").First("ForwardX11Trusted"), "受信任也要明写:OpenSSH 没写时是非受信任");
        Assert.IsNull(ReadBack(export, "off").First("ObscureKeystrokeTiming"), "关着的不写,老版本 ssh 不认这个关键字");
        Assert.IsNull(ReadBack(export, "off").First("ForwardX11"));

        string path = WriteConfig(export.Content);
        foreach (SessionProfile expected in new[] { on, trusted, off })
        {
            ImportedSession imported = await ScanOneAsync(path, expected.Name);
            Assert.AreEqual(expected.Ssh!.X11Forwarding, imported.Ssh?.X11Forwarding ?? false, expected.Name);
            if (expected.Ssh.X11Forwarding)
            {
                Assert.AreEqual(expected.Ssh.X11Trusted, imported.Ssh!.X11Trusted, expected.Name);
            }
            Assert.AreEqual(expected.Ssh.ObscureKeystrokeTiming, imported.Ssh?.ObscureKeystrokeTiming ?? false, expected.Name);
            Assert.IsEmpty(imported.Notes, $"{expected.Name}:明写了信任级别,读回来没什么要说明的");
        }
    }

    // ------------------------------------------------------------ 导入:转发

    [TestMethod]
    public async Task 三种TCP转发落成隧道_写错的一行不连累别的行()
    {
        string config = WriteConfig(
            """
            Host fw
                LocalForward 8080 db.internal:5432
                LocalForward [::1]:9000 [2001:db8::5]:22
                LocalForward 8443
                LocalForward 0 db:5432
                RemoteForward 9090 localhost:80
                RemoteForward *:9091 10.0.0.9:443
                DynamicForward 1081
                DynamicForward *:1082
            """);

        ImportedSession fw = await ScanOneAsync(config, "fw");
        List<TunnelConfig> tunnels = [.. fw.Tunnels!];

        Assert.HasCount(6, tunnels);
        TunnelConfig local = tunnels[0];
        Assert.AreEqual(TunnelType.LocalForward, local.Type);
        Assert.AreEqual(("127.0.0.1", 8080u, "db.internal", 5432u), (local.LocalHost, local.LocalPort, local.RemoteHost, local.RemotePort));
        Assert.AreEqual(("::1", 9000u, "2001:db8::5", 22u), (tunnels[1].LocalHost, tunnels[1].LocalPort, tunnels[1].RemoteHost, tunnels[1].RemotePort));
        TunnelConfig remote = tunnels[2];
        Assert.AreEqual(TunnelType.RemoteForward, remote.Type);
        Assert.AreEqual(("127.0.0.1", 9090u, "localhost", 80u), (remote.RemoteHost, remote.RemotePort, remote.LocalHost, remote.LocalPort));
        Assert.AreEqual("0.0.0.0", tunnels[3].RemoteHost, "* 是全部网卡");
        Assert.AreEqual((TunnelType.DynamicForward, "127.0.0.1", 1081u), (tunnels[4].Type, tunnels[4].LocalHost, tunnels[4].LocalPort));
        Assert.AreEqual("0.0.0.0", tunnels[5].LocalHost);

        Assert.Contains(n => n.Contains("LocalForward 0 db:5432"), fw.Notes, "端口 0(连接时才定的端口)在隧道里没有对应的写法");
        Assert.Contains(n => n.Contains("LocalForward 8443"), fw.Notes, "写错的那一行不连累别的行");
    }

    [TestMethod]
    public async Task GatewayPorts与ClearAllForwardings()
    {
        string config = WriteConfig(
            """
            Host gw
                GatewayPorts yes
                LocalForward 8080 db:5432
            Host none
                ClearAllForwardings yes
                LocalForward 8080 db:5432
            """);

        Assert.AreEqual("0.0.0.0", (await ScanOneAsync(config, "gw")).Tunnels!.Single().LocalHost);
        Assert.IsNull((await ScanOneAsync(config, "none")).Tunnels);
    }

    /// <summary>本机一个完整的套接字路径(正斜杠:ssh_config 按「含 /」认套接字,Windows 上 C:/… 同样是完整路径)。</summary>
    private static string LocalSocket(string name) => Path.Combine(Path.GetTempPath(), name).Replace('\\', '/');

    /// <summary>
    /// 套接字写法 → 两个路径字段:本地转发两头各自可以是套接字;远程转发两头都是套接字才导,只有一头的跳过
    /// (SSH 库与隧道服务都只认两头 TCP、两头套接字两种)。本机用不了的路径(相对路径)跳过。
    /// </summary>
    [TestMethod]
    public async Task 套接字转发映射到两个路径字段()
    {
        string listen = LocalSocket("vs-import-l.sock");
        string back = LocalSocket("vs-import-app.sock");
        string config = WriteConfig(
            $"""
            Host sock
                LocalForward {listen} /var/run/docker.sock
                LocalForward 2375 /var/run/docker.sock
                LocalForward {LocalSocket("vs-import-m.sock")} db:5432
                LocalForward ~/vs-import-home.sock db:5433
                LocalForward sub/relative.sock db:5434
                RemoteForward /tmp/app.sock {back}
                RemoteForward /tmp/x.sock localhost:80
                RemoteForward 9000 {LocalSocket("vs-import-y.sock")}
            """);

        ImportedSession sock = await ScanOneAsync(config, "sock");
        List<TunnelConfig> tunnels = [.. sock.Tunnels!];

        Assert.HasCount(5, tunnels);
        Assert.AreEqual((TunnelType.LocalForward, listen, "/var/run/docker.sock"), (tunnels[0].Type, tunnels[0].LocalSocketPath, tunnels[0].RemoteSocketPath));
        Assert.AreEqual((2375u, null, "/var/run/docker.sock"), (tunnels[1].LocalPort, tunnels[1].LocalSocketPath, tunnels[1].RemoteSocketPath));
        Assert.AreEqual(("db", 5432u, null), (tunnels[2].RemoteHost, tunnels[2].RemotePort, tunnels[2].RemoteSocketPath));
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "vs-import-home.sock"),
            tunnels[3].LocalSocketPath, "开头的 ~ 展开成主目录");
        TunnelConfig remote = tunnels[4];
        Assert.AreEqual((TunnelType.RemoteForward, back, "/tmp/app.sock"), (remote.Type, remote.LocalSocketPath, remote.RemoteSocketPath),
            "远程转发:服务器上监听的套接字进 RemoteSocketPath,本机要连的进 LocalSocketPath");

        Assert.Contains(n => n.Contains("sub/relative.sock"), sock.Notes, "相对路径落在哪全看程序的当前目录");
        Assert.Contains(n => n.Contains("/tmp/x.sock localhost:80"), sock.Notes, "远程转发只有一头是套接字");
        Assert.Contains(n => n.Contains("RemoteForward 9000"), sock.Notes, "远程转发只有一头是套接字");
    }

    /// <summary>
    /// <c>RemoteForward</c> 不给目标 → 远程动态,放行名单取这台主机的 <c>PermitRemoteOpen</c>;没写(OpenSSH 这时按 any)、
    /// 写了 none 或写错的都不导 —— 隧道的放行名单没有默认值,不替用户当成全放。
    /// </summary>
    [TestMethod]
    public async Task 远程动态转发带上PermitRemoteOpen_没写就不导()
    {
        string config = WriteConfig(
            """
            Host listed
                RemoteForward 1080
                RemoteForward *:1081
                PermitRemoteOpen localhost:8080 *.corp.example:443 [::1]:*
            Host all
                RemoteForward 1080
                PermitRemoteOpen any
            Host unlisted
                RemoteForward 1080
            Host nothing
                RemoteForward 1080
                PermitRemoteOpen none
            Host typo
                RemoteForward 1080
                PermitRemoteOpen localhost
            Host onsocket
                RemoteForward /tmp/rd.sock
                PermitRemoteOpen any
            """);

        ImportedSession listed = await ScanOneAsync(config, "listed");
        List<TunnelConfig> tunnels = [.. listed.Tunnels!];
        Assert.HasCount(2, tunnels);
        Assert.AreEqual((TunnelType.RemoteDynamicForward, "127.0.0.1", 1080u, 0u), (tunnels[0].Type, tunnels[0].RemoteHost, tunnels[0].RemotePort, tunnels[0].LocalPort));
        Assert.AreEqual(("0.0.0.0", 1081u), (tunnels[1].RemoteHost, tunnels[1].RemotePort));
        Assert.AreSequenceEqual(["localhost:8080", "*.corp.example:443", "[::1]:*"], [.. tunnels[0].PermitRemoteOpen!]);
        Assert.AreSequenceEqual([.. tunnels[0].PermitRemoteOpen!], [.. tunnels[1].PermitRemoteOpen!], "一台主机的远程动态转发共用一份名单");

        Assert.AreSequenceEqual(["any"], [.. (await ScanOneAsync(config, "all")).Tunnels!.Single().PermitRemoteOpen!], "明写了 any 的照收");

        foreach (string alias in new[] { "unlisted", "nothing", "typo", "onsocket" })
        {
            ImportedSession skipped = await ScanOneAsync(config, alias);
            Assert.IsNull(skipped.Tunnels, alias);
            Assert.Contains(n => n.Contains("RemoteForward"), skipped.Notes, alias);
        }
        Assert.Contains(n => n.Contains("\"localhost\"") || n.Contains("「localhost」"), (await ScanOneAsync(config, "typo")).Notes,
            "点名写错的那一条");
    }

    // ------------------------------------------------------------ 导入:认证

    [TestMethod]
    public async Task CertificateFile映射到证书认证_私钥取同名的那把()
    {
        string config = WriteConfig(
            """
            Host cert
                IdentityFile /keys/other
                IdentityFile /keys/deploy
                CertificateFile /keys/deploy-cert.pub
            Host certonly
                CertificateFile /keys/ops-cert.pub
            """);

        ImportedSession cert = await ScanOneAsync(config, "cert");
        Assert.AreEqual(AuthMethod.Certificate, cert.AuthMethod);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy-cert.pub"), cert.CertificatePath);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy"), cert.PrivateKeyPath, "与证书同名的那把,不是第一把");
        Assert.Contains(n => n.Contains("other"), cert.Notes, "另一把私钥没导入,说一声");

        ImportedSession certOnly = await ScanOneAsync(config, "certonly");
        Assert.AreEqual(AuthMethod.Certificate, certOnly.AuthMethod);
        Assert.AreEqual(Path.GetFullPath("/keys/ops"), certOnly.PrivateKeyPath, "按 ssh-keygen 的命名约定反推");
    }

    /// <summary>OpenSSH 自己会读 <c>&lt;IdentityFile&gt;-cert.pub</c>:那个文件在本机存在时同样按证书认证导入,不存在的不猜。</summary>
    [TestMethod]
    public async Task IdentityFile旁边有证书时按证书认证()
    {
        string directory = NewDirectory();
        string withCert = Path.Combine(directory, "id_signed");
        File.WriteAllText(withCert, "");
        File.WriteAllText(withCert + "-cert.pub", "");
        string config = WriteConfig(
            $"""
            Host signed
                IdentityFile {withCert.Replace('\\', '/')}
            Host plain
                IdentityFile {Path.Combine(directory, "id_plain").Replace('\\', '/')}
            """);

        ImportedSession signed = await ScanOneAsync(config, "signed");
        Assert.AreEqual(AuthMethod.Certificate, signed.AuthMethod);
        Assert.AreEqual(Path.GetFullPath(withCert + "-cert.pub"), signed.CertificatePath);

        ImportedSession plain = await ScanOneAsync(config, "plain");
        Assert.IsNull(plain.AuthMethod, "没有证书:仍按私钥(写入器推断)");
        Assert.IsNull(plain.CertificatePath);
    }

    [TestMethod]
    public async Task IdentityAgent默认的agent映射到Agent认证()
    {
        string config = WriteConfig(
            """
            Host agent
                IdentityAgent SSH_AUTH_SOCK
            Host noagent
                IdentityAgent none
            Host otheragent
                IdentityAgent /run/user/1000/some-unusual-agent.sock
            Host keyed
                IdentityAgent SSH_AUTH_SOCK
                IdentityFile /keys/id
            """);

        Assert.AreEqual(AuthMethod.Agent, (await ScanOneAsync(config, "agent")).AuthMethod);
        Assert.IsNull((await ScanOneAsync(config, "noagent")).AuthMethod);
        ImportedSession other = await ScanOneAsync(config, "otheragent");
        if (OperatingSystem.IsWindows())
        {
            Assert.IsNull(other.AuthMethod, "Linux 的套接字路径在 Windows 上用不了");
            Assert.Contains(n => n.Contains("some-unusual-agent.sock"), other.Notes);
        }
        else
        {
            Assert.AreEqual(AuthMethod.Agent, other.AuthMethod);
            Assert.AreEqual("/run/user/1000/some-unusual-agent.sock", other.Ssh?.AgentEndpoint, "别的 agent:记成这条连接的 agent 端点");
        }
        Assert.IsNull((await ScanOneAsync(config, "keyed")).AuthMethod, "写了私钥就按私钥:VelaShell 一条连接只用一种认证");
    }

    // ------------------------------------------------------------ 导入:写入

    [TestMethod]
    public async Task 写入时带上证书_协议层选项与隧道()
    {
        string config = WriteConfig(
            """
            Host full
                HostName 10.0.0.8
                IdentityFile /keys/deploy
                CertificateFile /keys/deploy-cert.pub
                Compression yes
                Ciphers aes256-gcm@openssh.com
                LocalForward 8080 db:5432
            """);
        ImportedSession item = await ScanOneAsync(config, "full");

        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        List<SessionProfile> saved = [];
        repository.SaveSessionAsync(Arg.Do<SessionProfile>(saved.Add)).Returns(Task.CompletedTask);
        IAppDataStore store = Substitute.For<IAppDataStore>();

        await new SshConfigImportService(repository, store).ImportAsync([item], "SSH config");

        SessionProfile profile = saved.Single();
        Assert.AreEqual(AuthMethod.Certificate, profile.AuthMethod);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy-cert.pub"), profile.CertificatePath);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy"), profile.PrivateKeyPath);
        Assert.IsTrue(profile.Ssh?.Compression);
        Assert.AreEqual("aes256-gcm@openssh.com", profile.Ssh?.Ciphers);
        await store.Received(1).UpsertAsync("tunnels", profile.Id.ToString("D"),
            Arg.Is<List<TunnelConfig>>(list => list.Count == 1 && list[0].RemoteHost == "db" && list[0].RemotePort == 5432),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------ 导出

    private static SessionArchive Archive(params SessionProfile[] sessions) => new()
    {
        Application = "VelaShell test",
        ExportedAtUtc = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc),
        Sessions = [.. sessions],
    };

    private static SshHostConfig ReadBack(SshConfigExport export, string alias) =>
        SshConfigFile.Resolve(SshConfigFile.Parse(export.Content), alias);

    /// <summary>导出的文件拿 SSH 库的解析器读回来(导入用的同一个),每个字段都对得上。</summary>
    [TestMethod]
    public void 导出的ssh_config读得回来()
    {
        string home = Path.Combine(Path.GetTempPath(), "home-for-export");
        SessionProfile bastion = new() { Name = "bastion", Host = "203.0.113.10", Port = 2222, Username = "jump" };
        SessionProfile web = new()
        {
            Name = "web 01",
            Host = "2001:db8::10",
            Username = "deploy",
            AuthMethod = AuthMethod.Certificate,
            PrivateKeyPath = Path.Combine(home, ".ssh", "id_ed25519"),
            CertificatePath = Path.Combine(home, ".ssh", "id_ed25519-cert.pub"),
            JumpHostProfileId = bastion.Id,
            Ssh = new SshSessionOptions { Compression = true, AgentForwarding = true, Ciphers = "aes256-gcm@openssh.com" },
        };
        SessionProfile ftp = new() { Name = "files", Host = "ftp.example", ConnectionType = ConnectionType.FTP, Port = 21 };
        SessionArchive archive = Archive(bastion, web, ftp);
        archive.Tunnels = new()
        {
            [web.Id] =
            [
                new TunnelConfig { Type = TunnelType.LocalForward, Name = "", LocalHost = "127.0.0.1", LocalPort = 8080, RemoteHost = "db", RemotePort = 5432 },
                new TunnelConfig { Type = TunnelType.RemoteForward, Name = "", LocalHost = "::1", LocalPort = 80, RemoteHost = "127.0.0.1", RemotePort = 9090 },
                new TunnelConfig { Type = TunnelType.DynamicForward, Name = "", LocalHost = "127.0.0.1", LocalPort = 1080 },
            ],
        };

        SshConfigExport export = SshConfigExporter.Write(archive, home);

        Assert.AreEqual(2, export.Exported);
        Assert.AreEqual(1, export.Skipped, "FTP 写不进 ssh_config");
        Assert.DoesNotContain("ftp.example", export.Content);

        SshHostConfig jump = ReadBack(export, "bastion");
        Assert.AreEqual(("203.0.113.10", 2222, "jump"), (jump.HostName, jump.Port, jump.User));

        SshHostConfig host = ReadBack(export, "web-01");
        Assert.AreEqual("2001:db8::10", host.HostName);
        Assert.AreEqual(22, host.Port);
        Assert.AreEqual("deploy", host.User);
        Assert.AreEqual("bastion", host.ProxyJump);
        Assert.AreSequenceEqual(["~/.ssh/id_ed25519"], [.. host.IdentityFiles], "主目录下的路径写成 ~/…,换台机器也找得到");
        Assert.AreEqual("~/.ssh/id_ed25519-cert.pub", host.First("CertificateFile"));
        Assert.IsTrue(host.Compression);
        Assert.IsTrue(host.ForwardAgent);
        Assert.AreEqual("aes256-gcm@openssh.com", host.Ciphers);
        List<SshConfigForward> forwards = [.. host.GetForwards()];
        Assert.HasCount(3, forwards);
        Assert.AreEqual((SshConfigForwardKind.Local, 8080, "db", 5432), (forwards[0].Kind, forwards[0].ListenPort, forwards[0].TargetHost, forwards[0].TargetPort));
        Assert.AreEqual((SshConfigForwardKind.Remote, 9090, "::1", 80), (forwards[1].Kind, forwards[1].ListenPort, forwards[1].TargetHost, forwards[1].TargetPort));
        Assert.AreEqual((SshConfigForwardKind.Dynamic, 1080), (forwards[2].Kind, forwards[2].ListenPort));
        Assert.DoesNotContain("Password", export.Content.Replace("# Passwords and passphrases are never written.", ""));
    }

    /// <summary>
    /// 按连接指定的 agent 端点:认证走 agent 时写成 <c>IdentityAgent</c>,转发时 <c>ForwardAgent</c> 直接写端点(不写 yes);
    /// 主目录下的套接字写成 <c>~/…</c>;端点用不上(私钥认证、不转发)的不写。导出再导入,端点与转发回到原样。
    /// </summary>
    [TestMethod]
    public async Task agent端点导出后读得回来()
    {
        string home = Path.GetTempPath().TrimEnd('/', '\\');
        string socket = Path.Combine(home, "agents", "deploy.sock");
        SessionProfile agentAuth = new()
        {
            Name = "agentauth",
            Host = "10.0.0.31",
            AuthMethod = AuthMethod.Agent,
            Ssh = new SshSessionOptions { AgentEndpoint = socket, AgentForwarding = true },
        };
        SessionProfile keyForward = new()
        {
            Name = "keyforward",
            Host = "10.0.0.32",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/keys/id",
            Ssh = new SshSessionOptions { AgentEndpoint = "~/agents/deploy.sock", AgentForwarding = true },
        };
        SessionProfile unused = new()
        {
            Name = "unused",
            Host = "10.0.0.33",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/keys/id",
            Ssh = new SshSessionOptions { AgentEndpoint = socket },
        };
        SessionProfile pipe = new()
        {
            Name = "pipe",
            Host = "10.0.0.34",
            AuthMethod = AuthMethod.Agent,
            Ssh = new SshSessionOptions { AgentEndpoint = @"\\.\pipe\vela-pinned-agent" },
        };

        SshConfigExport export = SshConfigExporter.Write(Archive(agentAuth, keyForward, unused, pipe), home);

        Assert.AreEqual("~/agents/deploy.sock", ReadBack(export, "agentauth").First("IdentityAgent"));
        Assert.AreEqual("~/agents/deploy.sock", ReadBack(export, "agentauth").First("ForwardAgent"), "转发写端点,不写 yes");
        Assert.IsNull(ReadBack(export, "keyforward").First("IdentityAgent"), "私钥认证:IdentityAgent 不写");
        Assert.AreEqual("~/agents/deploy.sock", ReadBack(export, "keyforward").First("ForwardAgent"));
        Assert.IsNull(ReadBack(export, "unused").First("IdentityAgent"), "端点用不上就不写");
        Assert.IsNull(ReadBack(export, "unused").First("ForwardAgent"));
        Assert.AreEqual(@"\\.\pipe\vela-pinned-agent", ReadBack(export, "pipe").First("IdentityAgent"), "命名管道原样写");

        // 读回来:导出时把主目录写成了 ~,导入这边由库按真正的主目录展开 —— 这里改用真主目录下的路径核对往返。
        string realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SessionProfile roundTrip = new()
        {
            Name = "roundtrip",
            Host = "10.0.0.35",
            AuthMethod = AuthMethod.Agent,
            Ssh = new SshSessionOptions { AgentEndpoint = Path.Combine(realHome, "vs-agent.sock"), AgentForwarding = true },
        };
        string path = WriteConfig(SshConfigExporter.Write(Archive(roundTrip)).Content);
        ImportedSession imported = await ScanOneAsync(path, "roundtrip");
        Assert.AreEqual(AuthMethod.Agent, imported.AuthMethod);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(realHome, "vs-agent.sock")), Path.GetFullPath(imported.Ssh!.AgentEndpoint!));
        Assert.IsTrue(imported.Ssh.AgentForwarding);
        Assert.IsEmpty(imported.Notes);
    }

    /// <summary>
    /// 「SSH Agent」认证、用系统默认的 agent(没指定端点,最常见的那种):写成 <c>IdentityAgent SSH_AUTH_SOCK</c>
    /// (ssh_config(5) 里这个字面量就是「环境变量指的那个」),导入回来仍是 agent 认证、端点留空 —— 曾经什么都不写,再导入就成了密码认证。
    /// </summary>
    [TestMethod]
    public async Task 默认agent认证导出再导入仍是Agent认证()
    {
        SessionProfile forwarding = new()
        {
            Name = "default-agent",
            Host = "10.0.0.36",
            AuthMethod = AuthMethod.Agent,
            Ssh = new SshSessionOptions { AgentForwarding = true },
        };
        SessionProfile bare = new() { Name = "bare-agent", Host = "10.0.0.37", AuthMethod = AuthMethod.Agent };

        SshConfigExport export = SshConfigExporter.Write(Archive(forwarding, bare));

        Assert.AreEqual("SSH_AUTH_SOCK", ReadBack(export, "default-agent").First("IdentityAgent"));
        Assert.AreEqual("yes", ReadBack(export, "default-agent").First("ForwardAgent"), "默认的 agent 转发照旧写 yes");
        Assert.AreEqual("SSH_AUTH_SOCK", ReadBack(export, "bare-agent").First("IdentityAgent"), "没有 SSH 选项的也写");

        string path = WriteConfig(export.Content);
        foreach (string alias in new[] { "default-agent", "bare-agent" })
        {
            ImportedSession imported = await ScanOneAsync(path, alias);
            Assert.AreEqual(AuthMethod.Agent, imported.AuthMethod, alias);
            Assert.IsNull(imported.Ssh?.AgentEndpoint, $"{alias}:还是系统默认的那个");
            Assert.IsEmpty(imported.Notes, alias);
        }
        Assert.IsTrue((await ScanOneAsync(path, "default-agent")).Ssh!.AgentForwarding);
    }

    /// <summary>系统密钥库的钥 OpenSSH 用不了(没有文件):不写 IdentityFile,写成一行注释说明原来用的是哪把钥;再导入时注释不被读回。</summary>
    [TestMethod]
    public void 系统密钥库的钥写成注释()
    {
        SessionProfile tpm = new()
        {
            Name = "tpm",
            Host = "10.0.0.41",
            AuthMethod = AuthMethod.KeyStore,
            KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.PlatformProvider, KeyName = "deploy" },
        };

        SshConfigExport export = SshConfigExporter.Write(Archive(tpm));

        Assert.Contains("    # cng:Microsoft Platform Crypto Provider/deploy  (VelaShell:", export.Content);
        Assert.IsEmpty(ReadBack(export, "tpm").IdentityFiles);
        Assert.IsNull(ReadBack(export, "tpm").First("CertificateFile"), "没配证书就不写");
    }

    /// <summary>系统密钥库配了证书:钥照旧写成注释,证书照证书认证的口径写 CertificateFile(主目录下写 ~/…)。</summary>
    [TestMethod]
    public void 系统密钥库配的证书写成CertificateFile()
    {
        string home = Path.Combine(Path.GetTempPath(), "home-for-export");
        SessionProfile tpm = new()
        {
            Name = "tpm-ca",
            Host = "10.0.0.42",
            AuthMethod = AuthMethod.KeyStore,
            KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.PlatformProvider, KeyName = "deploy" },
            CertificatePath = Path.Combine(home, ".ssh", "deploy-cert.pub"),
        };

        SshConfigExport export = SshConfigExporter.Write(Archive(tpm), home);

        Assert.Contains("    # cng:Microsoft Platform Crypto Provider/deploy  (VelaShell:", export.Content);
        SshHostConfig host = ReadBack(export, "tpm-ca");
        Assert.IsEmpty(host.IdentityFiles, "钥在密钥库里,没有 IdentityFile 可写");
        Assert.AreEqual("~/.ssh/deploy-cert.pub", host.First("CertificateFile"));
    }

    [TestMethod]
    public void 别名去掉特殊字符_重名加序号()
    {
        SshConfigExport export = SshConfigExporter.Write(Archive(
            new SessionProfile { Name = "prod*", Host = "10.0.0.1" },
            new SessionProfile { Name = "prod", Host = "10.0.0.2" },
            new SessionProfile { Name = "  ", Host = "10.0.0.3" }));

        List<string> aliases = [.. SshConfigFile.Parse(export.Content).SelectMany(b => b.Patterns)];
        Assert.AreSequenceEqual(["prod", "prod-2", "10.0.0.3"], aliases);
    }

    /// <summary>引用共享凭据的连接:认证材料取自凭据,用户名填了的以连接为准。</summary>
    [TestMethod]
    public void 共享凭据的私钥与用户名()
    {
        SharedCredential credential = new() { Name = "ops", Username = "ops", AuthMethod = AuthMethod.PrivateKey, PrivateKeyPath = "/keys/ops" };
        SessionProfile session = new() { Name = "s", Host = "h", CredentialSource = CredentialReference.ForShared(credential.Id) };
        SessionArchive archive = Archive(session);
        archive.SharedCredentials = [credential];

        SshHostConfig host = ReadBack(SshConfigExporter.Write(archive), "s");

        Assert.AreEqual("ops", host.User);
        Assert.AreSequenceEqual(["/keys/ops"], [.. host.IdentityFiles]);
    }

    /// <summary>「放开老算法」在 ssh_config 里没有开关:没写清单的写成 +老算法,写了 + 的并成一条,写了 - / ^ 的写完整清单。</summary>
    [TestMethod]
    public void 放开老算法换算成ssh_config的写法()
    {
        Assert.IsNull(SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Cipher, null, legacy: false));
        Assert.AreEqual("-hmac-sha1", SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Mac, "-hmac-sha1", legacy: false));

        string? kex = SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, null, legacy: true);
        Assert.AreEqual("+diffie-hellman-group14-sha1", kex);
        Assert.AreEqual("+diffie-hellman-group14-sha1,curve25519-sha256",
            SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, "+curve25519-sha256", legacy: true));
        string? removed = SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, "-curve25519-sha256", legacy: true);
        Assert.IsNotNull(removed);
        Assert.IsFalse(removed.StartsWith('-'), "OpenSSH 一条里只能有一种前缀:写算出来的完整清单");
        Assert.Contains("diffie-hellman-group14-sha1", removed);
        Assert.DoesNotContain("curve25519-sha256,", removed + ",");
        Assert.AreEqual("aes256-ctr", SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Cipher, "aes256-ctr", legacy: true), "整个替换的与默认无关");
    }

    /// <summary>
    /// 「允许老算法」还放开 SHA-1 的 RSA 用户签名:导出写 <c>PubkeyAcceptedAlgorithms +ssh-rsa</c>(证书认证连同 RSA 证书的那个),
    /// 不写的话 OpenSSH 8.8 起在只认 ssh-rsa 的老设备上公钥登录必然失败。导入回来映射回这个开关,另三个清单的 + 老算法并回开关里。
    /// </summary>
    [TestMethod]
    public async Task 放开老算法连同SHA1的RSA签名导出_导入回来还是这个开关()
    {
        string home = Path.Combine(Path.GetTempPath(), "home-for-export");
        SessionProfile key = new()
        {
            Name = "old-switch",
            Host = "10.0.0.51",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/keys/id_rsa",
            Ssh = new SshSessionOptions { LegacyAlgorithms = true, Ciphers = "aes128-ctr" },
        };
        SessionProfile certificate = new()
        {
            Name = "old-cert",
            Host = "10.0.0.52",
            AuthMethod = AuthMethod.Certificate,
            PrivateKeyPath = "/keys/id_rsa",
            CertificatePath = "/keys/id_rsa-cert.pub",
            Ssh = new SshSessionOptions { LegacyAlgorithms = true },
        };
        SessionProfile modern = new() { Name = "modern", Host = "10.0.0.53", Ssh = new SshSessionOptions { Compression = true } };

        SshConfigExport export = SshConfigExporter.Write(Archive(key, certificate, modern), home);

        Assert.AreEqual("+ssh-rsa", ReadBack(export, "old-switch").PubkeyAcceptedAlgorithms);
        Assert.AreEqual("+ssh-rsa,ssh-rsa-cert-v01@openssh.com", ReadBack(export, "old-cert").PubkeyAcceptedAlgorithms);
        Assert.IsNull(ReadBack(export, "modern").PubkeyAcceptedAlgorithms, "没开老算法就不写");

        string path = WriteConfig(export.Content);
        foreach (string alias in new[] { "old-switch", "old-cert" })
        {
            ImportedSession imported = await ScanOneAsync(path, alias);
            Assert.IsTrue(imported.Ssh!.LegacyAlgorithms, alias);
            Assert.IsNull(imported.Ssh.KexAlgorithms, $"{alias}:+老算法并回开关,配置与导出前一样");
            Assert.IsNull(imported.Ssh.HostKeyAlgorithms, alias);
            Assert.IsNull(imported.Ssh.Macs, alias);
            Assert.IsEmpty(imported.Notes, alias);
        }
        Assert.AreEqual("aes128-ctr", (await ScanOneAsync(path, "old-switch")).Ssh!.Ciphers);
        Assert.IsFalse((await ScanOneAsync(path, "modern")).Ssh?.LegacyAlgorithms ?? false);

        // 再导出一次:与第一次写的一样(开关追加的那几个不重复写)。
        Assert.AreEqual("+diffie-hellman-group14-sha1",
            SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, "+diffie-hellman-group14-sha1", legacy: true));
    }

    /// <summary>
    /// 手写的 <c>PubkeyAcceptedAlgorithms</c>:留着 ssh-rsa(+ / ^ / 整份替换,旧名 PubkeyAcceptedKeyTypes 也认)→ 「允许老算法」,
    /// 开关放宽了文件没写的类别时说一句;<c>-ssh-rsa</c> 或不含它的不动开关。
    /// </summary>
    [TestMethod]
    public async Task PubkeyAcceptedAlgorithms放开ssh_rsa时映射到允许老算法()
    {
        string config = WriteConfig(
            """
            Host plus
                PubkeyAcceptedAlgorithms +ssh-rsa
            Host oldname
                PubkeyAcceptedKeyTypes ssh-ed25519,ssh-rsa
            Host minus
                PubkeyAcceptedAlgorithms -ssh-rsa
            Host modern
                PubkeyAcceptedAlgorithms ssh-ed25519,rsa-sha2-512
            """);

        ImportedSession plus = await ScanOneAsync(config, "plus");
        Assert.IsTrue(plus.Ssh!.LegacyAlgorithms);
        Assert.Contains(n => n.Contains("PubkeyAcceptedAlgorithms +ssh-rsa"), plus.Notes, "开关还放开了文件没写的 SHA-1 KEX / 主机密钥 / MAC:说一句");
        Assert.IsTrue((await ScanOneAsync(config, "oldname")).Ssh!.LegacyAlgorithms);
        Assert.IsFalse((await ScanOneAsync(config, "minus")).Ssh?.LegacyAlgorithms ?? false);
        Assert.IsFalse((await ScanOneAsync(config, "modern")).Ssh?.LegacyAlgorithms ?? false);
    }

    /// <summary>
    /// 存着的清单带空格、末尾逗号(VelaShell 自己的解析照收)时,写出去的值里没有空白也没有空项:
    /// OpenSSH 读到 <c>a, b</c> / <c>a,b,</c> 报 bad configuration option,整份配置(连同 Include 它的 ~/.ssh/config)都用不了。
    /// </summary>
    [TestMethod]
    public void 算法清单写成OpenSSH读得了的一条()
    {
        Assert.AreEqual("aes256-gcm@openssh.com,aes128-ctr",
            SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Cipher, "aes256-gcm@openssh.com, aes128-ctr", legacy: false));
        Assert.AreEqual("hmac-sha2-256", SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Mac, "hmac-sha2-256,", legacy: false));
        Assert.AreEqual("-chacha20-poly1305@openssh.com,aes128-ctr",
            SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Cipher, " -chacha20-poly1305@openssh.com ,\taes128-ctr ", legacy: false), "前缀留着");
        Assert.IsNull(SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Cipher, " , ", legacy: false), "一项都没有就是没写");
        Assert.AreEqual("+diffie-hellman-group14-sha1,curve25519-sha256",
            SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, "+ curve25519-sha256,", legacy: true));

        SessionProfile spaced = new()
        {
            Name = "spaced",
            Host = "10.0.0.9",
            Ssh = new SshSessionOptions { Ciphers = "aes256-gcm@openssh.com, aes128-ctr", Macs = "hmac-sha2-256,hmac-sha2-512," },
        };
        SshConfigExport export = SshConfigExporter.Write(Archive(spaced));

        Assert.Contains("    Ciphers aes256-gcm@openssh.com,aes128-ctr\n", export.Content);
        Assert.Contains("    MACs hmac-sha2-256,hmac-sha2-512\n", export.Content);
        Assert.AreEqual("aes256-gcm@openssh.com,aes128-ctr", ReadBack(export, "spaced").Ciphers);
    }

    private static TunnelConfig Tunnel(TunnelType type, string localHost = "127.0.0.1", uint localPort = 0, string remoteHost = "",
        uint remotePort = 0, string? localSocket = null, string? remoteSocket = null, IReadOnlyList<string>? permit = null) => new()
        {
            Type = type,
            Name = "",
            LocalHost = localHost,
            LocalPort = localPort,
            RemoteHost = remoteHost,
            RemotePort = remotePort,
            LocalSocketPath = localSocket,
            RemoteSocketPath = remoteSocket,
            PermitRemoteOpen = permit,
        };

    /// <summary>
    /// 套接字与远程动态的往返:导出的 ssh_config 再经「会话导入」读回来,隧道与原来的一样。
    /// 远程动态一定连同 <c>PermitRemoteOpen</c> 写出(没写时 OpenSSH 按 any);名单不同的、只有一头是套接字的写成注释,不会被读回来。
    /// </summary>
    [TestMethod]
    public async Task 套接字与远程动态导出后读得回来()
    {
        string listen = LocalSocket("vs-export-l.sock");
        string back = LocalSocket("vs-export-app.sock");
        SessionProfile host = new() { Name = "fw", Host = "10.0.0.7" };
        TunnelConfig[] representable =
        [
            Tunnel(TunnelType.LocalForward, localSocket: listen, remoteSocket: "/var/run/docker.sock"),
            Tunnel(TunnelType.LocalForward, localPort: 2375, remoteSocket: "/var/run/docker.sock"),
            Tunnel(TunnelType.LocalForward, localSocket: listen + "2", remoteHost: "db", remotePort: 5432),
            Tunnel(TunnelType.RemoteForward, localSocket: back, remoteSocket: "/tmp/app.sock"),
            Tunnel(TunnelType.RemoteDynamicForward, remoteHost: "127.0.0.1", remotePort: 1080, permit: ["localhost:8080", "*.corp.example:443"]),
            Tunnel(TunnelType.RemoteDynamicForward, remoteHost: "0.0.0.0", remotePort: 1081, permit: ["localhost:8080", "*.corp.example:443"]),
        ];
        TunnelConfig[] commentedOut =
        [
            Tunnel(TunnelType.RemoteDynamicForward, remoteHost: "127.0.0.1", remotePort: 1082, permit: ["any"]),
            Tunnel(TunnelType.RemoteDynamicForward, remoteHost: "127.0.0.1", remotePort: 1083),
            Tunnel(TunnelType.RemoteForward, localHost: "localhost", localPort: 80, remoteSocket: "/tmp/half.sock"),
        ];
        SessionArchive archive = Archive(host);
        archive.Tunnels = new() { [host.Id] = [.. representable, .. commentedOut] };

        SshConfigExport export = SshConfigExporter.Write(archive);

        Assert.Contains("PermitRemoteOpen localhost:8080 *.corp.example:443\n", export.Content);
        Assert.DoesNotContain("PermitRemoteOpen any", export.Content, "名单不同的那条不能把整台主机放宽成全放");
        Assert.Contains("# RemoteForward 127.0.0.1:1082", export.Content);
        Assert.Contains("# RemoteForward 127.0.0.1:1083", export.Content, "没有名单的远程动态:写出来 OpenSSH 就当 any");
        Assert.Contains("# RemoteForward /tmp/half.sock localhost:80", export.Content);

        string path = WriteConfig(export.Content);
        ImportedSession imported = await ScanOneAsync(path, "fw");
        List<TunnelConfig> readBack = [.. imported.Tunnels!];
        Assert.HasCount(representable.Length, readBack);
        for (int i = 0; i < representable.Length; i++)
        {
            TunnelConfig expected = representable[i];
            TunnelConfig actual = readBack[i];
            Assert.AreEqual(expected.Type, actual.Type, $"#{i}");
            Assert.AreEqual(expected.LocalSocketPath, actual.LocalSocketPath, $"#{i}");
            Assert.AreEqual(expected.RemoteSocketPath, actual.RemoteSocketPath, $"#{i}");
            if (expected.LocalSocketPath is null && expected.Type != TunnelType.RemoteDynamicForward)
            {
                Assert.AreEqual((expected.LocalHost, expected.LocalPort), (actual.LocalHost, actual.LocalPort), $"#{i}");
            }
            if (expected.RemoteSocketPath is null)
            {
                Assert.AreEqual((expected.RemoteHost, expected.RemotePort), (actual.RemoteHost, actual.RemotePort), $"#{i}");
            }
            Assert.AreSequenceEqual([.. expected.PermitRemoteOpen ?? []], [.. actual.PermitRemoteOpen ?? []], $"#{i}");
        }
    }
}
