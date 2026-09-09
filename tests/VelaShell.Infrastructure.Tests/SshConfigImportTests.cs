using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Import;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// OpenSSH <c>~/.ssh/config</c> 导入:取值规则(先出现者胜 + 通配兜底)、Include 展开、
/// IdentityFile / ProxyJump 的落地。
/// </summary>
[TestClass]
public class SshConfigImportTests
{
    private readonly List<string> _temp = [];

    /// <summary>删除本次测试写出的全部临时文件与目录。</summary>
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
                // 临时文件清不掉不该让测试变红。
            }
        }
    }

    /// <summary>基础字段:HostName / Port / User 按块取值,别名成为会话名。</summary>
    [TestMethod]
    public async Task Scan_ReadsHostNamePortUser()
    {
        string config = WriteConfig(
            """
            Host prod
                HostName 10.0.0.5
                Port 2222
                User deploy
            """);

        SessionImportScan scan = await ScanAsync(config);

        Assert.HasCount(1, scan.Items);
        ImportedSession item = scan.Items[0];
        Assert.AreEqual("prod", item.Name);
        Assert.AreEqual("10.0.0.5", item.Host);
        Assert.AreEqual(2222, item.Port);
        Assert.AreEqual("deploy", item.Username);
        Assert.AreEqual(ConnectionType.SSH, item.ConnectionType);
        Assert.IsFalse(item.HasEncryptedPassword);
        Assert.IsFalse(scan.MasterPasswordEnabled);
    }

    /// <summary>不写 HostName 时,别名本身就是主机名 —— 这是 ssh 的行为,导入必须一致。</summary>
    [TestMethod]
    public async Task Scan_AliasIsHostWhenHostNameMissing()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host build01
                User ci
            """));

        Assert.HasCount(1, scan.Items);
        Assert.AreEqual("build01", scan.Items[0].Host);
        Assert.AreEqual(22, scan.Items[0].Port);
    }

    /// <summary>
    /// 取值规则:具名块里的值胜过后面的 <c>Host *</c> 兜底块,兜底块只补具名块没写的那些键;
    /// 通配块自身不产出会话。
    /// </summary>
    [TestMethod]
    public async Task Scan_NamedBlockWinsOverWildcardDefaults()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host prod
                HostName 10.0.0.5
                User deploy

            Host staging
                HostName 10.0.0.6

            Host *
                User fallback
                Port 2200
            """));

        Assert.HasCount(2, scan.Items);
        ImportedSession prod = scan.Items.Single(i => i.Name == "prod");
        ImportedSession staging = scan.Items.Single(i => i.Name == "staging");
        Assert.AreEqual("deploy", prod.Username);   // 具名块先出现,胜出
        Assert.AreEqual("fallback", staging.Username); // 具名块没写 User,吃兜底
        Assert.AreEqual(2200, prod.Port);           // 两条都吃兜底端口
        Assert.AreEqual(2200, staging.Port);
    }

    /// <summary>模式块(通配 / 取反)不是一台可连的机器,不产出会话;取反把自己从匹配里排除。</summary>
    [TestMethod]
    public async Task Scan_SkipsPatternBlocksAndHonorsNegation()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host *.internal !secret.internal
                User intranet

            Host a.internal
                HostName 10.1.1.1

            Host secret.internal
                HostName 10.1.1.2
            """));

        // 只有两个字面量别名成为会话;`*.internal !secret.internal` 那行不产出。
        Assert.HasCount(2, scan.Items);
        Assert.AreEqual("intranet", scan.Items.Single(i => i.Name == "a.internal").Username);
        Assert.AreEqual(string.Empty, scan.Items.Single(i => i.Name == "secret.internal").Username);
    }

    /// <summary><c>Match</c> 块的条件要到连接时才有答案,整块跳过,其中的选项不得渗到任何会话上。</summary>
    [TestMethod]
    public async Task Scan_IgnoresMatchBlocks()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host prod
                HostName 10.0.0.5

            Match host prod exec "true"
                User conditional
            """));

        Assert.HasCount(1, scan.Items);
        Assert.AreEqual(string.Empty, scan.Items[0].Username);
    }

    /// <summary><c>Include</c> 就地展开,且被包含文件里的值参与同一套「先出现者胜」的取值。</summary>
    [TestMethod]
    public void Parse_ExpandsIncludeInPlace()
    {
        string directory = NewDirectory();
        File.WriteAllText(Path.Combine(directory, "extra.conf"),
            """
            Host inc01
                HostName 172.16.0.1
                User included
            """);
        string main = Path.Combine(directory, "config");
        File.WriteAllText(main,
            """
            Include extra.conf

            Host *
                User fallback
            """);

        IReadOnlyList<SshConfigBlock> blocks = SshConfigParser.ParseFile(main, directory);

        Assert.AreSequenceEqual((string[])["inc01"], (string[])[.. SshConfigParser.CollectHostAliases(blocks)]);
        IReadOnlyDictionary<string, string> options = SshConfigParser.ResolveOptions(blocks, "inc01");
        Assert.AreEqual("172.16.0.1", options["HostName"]);
        Assert.AreEqual("included", options["User"]); // 被包含文件先出现,胜过后面的兜底
    }

    /// <summary>互相 <c>Include</c> 不得转成死循环。</summary>
    [TestMethod]
    public void Parse_IncludeCycleTerminates()
    {
        string directory = NewDirectory();
        string a = Path.Combine(directory, "a.conf");
        string b = Path.Combine(directory, "b.conf");
        File.WriteAllText(a, "Include b.conf\nHost fromA\n    HostName 1.1.1.1\n");
        File.WriteAllText(b, "Include a.conf\nHost fromB\n    HostName 2.2.2.2\n");

        IReadOnlyList<SshConfigBlock> blocks = SshConfigParser.ParseFile(a, directory);

        Assert.AreSequenceEqual((string[])["fromB", "fromA"], (string[])[.. SshConfigParser.CollectHostAliases(blocks)]);
    }

    /// <summary><c>关键字=值</c> 写法、引号包裹的值与整行注释都要认。</summary>
    [TestMethod]
    public async Task Scan_ParsesEqualsFormQuotesAndComments()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            # 这一整行是注释
            Host=prod
                HostName="10.0.0.5"
                Port = 2022
                User   deploy
            """));

        Assert.HasCount(1, scan.Items);
        Assert.AreEqual("10.0.0.5", scan.Items[0].Host);
        Assert.AreEqual(2022, scan.Items[0].Port);
        Assert.AreEqual("deploy", scan.Items[0].Username);
    }

    /// <summary>IdentityFile 的 <c>~</c> 展开成主目录绝对路径,并让该会话以私钥认证方式落盘。</summary>
    [TestMethod]
    public async Task Import_IdentityFileBecomesPrivateKeyAuth()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host prod
                HostName 10.0.0.5
                User deploy
                IdentityFile ~/.ssh/id_prod
            """));

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_prod");
        Assert.AreEqual(expected, scan.Items[0].PrivateKeyPath);

        (FakeRepository repository, _) = await ImportAsync(scan.Items);
        SessionProfile profile = repository.Sessions.Single();
        Assert.AreEqual(AuthMethod.PrivateKey, profile.AuthMethod);
        Assert.AreEqual(expected, profile.PrivateKeyPath);
        Assert.IsFalse(profile.RememberPassword);
        Assert.IsNull(profile.Password);
    }

    /// <summary>没有 IdentityFile 的会话仍以密码认证落盘(密码留空,连接时再问)。</summary>
    [TestMethod]
    public async Task Import_WithoutIdentityFileStaysPasswordAuth()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig("Host plain\n    HostName 10.0.0.9\n"));

        (FakeRepository repository, _) = await ImportAsync(scan.Items);

        Assert.AreEqual(AuthMethod.Password, repository.Sessions.Single().AuthMethod);
        Assert.IsNull(repository.Sessions.Single().PrivateKeyPath);
    }

    /// <summary>
    /// <c>ProxyJump</c> 落成跳板引用:多跳取**离目标最近的最后一跳**,<c>user@host:port</c> 只认 host 段。
    /// </summary>
    [TestMethod]
    public async Task Import_ProxyJumpLinksToLastHop()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host edge
                HostName 203.0.113.1

            Host bastion
                HostName 10.0.0.1

            Host prod
                HostName 10.0.0.5
                ProxyJump edge,ops@bastion:2222
            """));

        ImportedSession prod = scan.Items.Single(i => i.Name == "prod");
        Assert.AreEqual("bastion", prod.JumpHostAlias);

        (FakeRepository repository, _) = await ImportAsync(scan.Items);
        SessionProfile bastion = repository.Sessions.Single(s => s.Name == "bastion");
        SessionProfile prodProfile = repository.Sessions.Single(s => s.Name == "prod");
        Assert.AreEqual(bastion.Id, prodProfile.JumpHostProfileId);
        Assert.IsNull(bastion.JumpHostProfileId);
    }

    /// <summary>跳板那条会话排在被跳会话之后导入时,第二趟仍要把引用接上。</summary>
    [TestMethod]
    public async Task Import_ProxyJumpLinksBackwardDeclaredHost()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host prod
                HostName 10.0.0.5
                ProxyJump bastion

            Host bastion
                HostName 10.0.0.1
            """));

        (FakeRepository repository, _) = await ImportAsync(scan.Items);

        Assert.AreEqual(
            repository.Sessions.Single(s => s.Name == "bastion").Id,
            repository.Sessions.Single(s => s.Name == "prod").JumpHostProfileId);
    }

    /// <summary>跳板不在本批次(未勾选或不在 config 里)时留直连,不去撞用户既有会话的同名配置。</summary>
    [TestMethod]
    public async Task Import_UnresolvedProxyJumpStaysDirect()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host prod
                HostName 10.0.0.5
                ProxyJump nowhere
            """));

        (FakeRepository repository, _) = await ImportAsync(scan.Items);

        Assert.IsNull(repository.Sessions.Single().JumpHostProfileId);
    }

    /// <summary>config 写出互相跳板的环时,断掉成环的那一条,两端都还能连。</summary>
    [TestMethod]
    public async Task Import_ProxyJumpCycleIsBroken()
    {
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host a
                HostName 10.0.0.1
                ProxyJump b

            Host b
                HostName 10.0.0.2
                ProxyJump a
            """));

        (FakeRepository repository, _) = await ImportAsync(scan.Items);

        SessionProfile a = repository.Sessions.Single(s => s.Name == "a");
        SessionProfile b = repository.Sessions.Single(s => s.Name == "b");
        // 先建立的一条保留,后一条会成环,被断成直连。
        Assert.IsTrue(a.JumpHostProfileId is null ^ b.JumpHostProfileId is null);
    }

    /// <summary>与仓储中已有的同 主机|端口|用户 会话重复时打上标记(由对话框决定跳过)。</summary>
    [TestMethod]
    public async Task Scan_MarksExistingTargetsAsDuplicates()
    {
        var existing = new SessionProfile { Name = "old", Host = "10.0.0.5", Port = 22, Username = "deploy" };
        SessionImportScan scan = await ScanAsync(WriteConfig(
            """
            Host prod
                HostName 10.0.0.5
                User deploy

            Host other
                HostName 10.0.0.6
                User deploy
            """), existing);

        Assert.IsTrue(scan.Items.Single(i => i.Name == "prod").AlreadyExists);
        Assert.IsFalse(scan.Items.Single(i => i.Name == "other").AlreadyExists);
    }

    /// <summary>探测不到配置文件时给出空扫描结果,而不是抛异常。</summary>
    [TestMethod]
    public async Task Scan_MissingFileYieldsEmpty()
    {
        SessionImportScan scan = await ScanAsync(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}"));

        Assert.IsEmpty(scan.Items);
    }

    /// <summary>本机真实 <c>~/.ssh/config</c> 的端到端冒烟:能读出来就不该抛,且每条都有主机名。</summary>
    [TestMethod]
    public async Task Scan_RealUserConfig_DoesNotThrow()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var service = new SshConfigImportService(repository);

        string? source = service.DetectDefaultSource();
        if (source is null)
        {
            Assert.Inconclusive("[SKIP] 本机没有 ~/.ssh/config。");
            return;
        }

        SessionImportScan scan = await service.ScanAsync(source);

        Assert.AreEqual(source, scan.Source);
        foreach (ImportedSession item in scan.Items)
        {
            Assert.IsNotEmpty(item.Host);
            Assert.IsTrue(item.Port is > 0 and <= 65535);
        }
    }

    private static async Task<SessionImportScan> ScanAsync(string configPath, params SessionProfile[] existing)
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>(existing)));
        return await new SshConfigImportService(repository).ScanAsync(configPath);
    }

    private static async Task<(FakeRepository Repository, SessionImportOutcome Outcome)> ImportAsync(IReadOnlyList<ImportedSession> items)
    {
        var repository = new FakeRepository();
        SessionImportOutcome outcome = await new SshConfigImportService(repository).ImportAsync(items, "SSH config");
        return (repository, outcome);
    }

    private string WriteConfig(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sshconfig-{Guid.NewGuid():N}");
        File.WriteAllText(path, content);
        _temp.Add(path);
        return path;
    }

    private string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sshconfig-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _temp.Add(path);
        return path;
    }

    /// <summary>只在内存里记账的仓储:导入写进来的配置按 id 去重保留最后一次写入。</summary>
    private sealed class FakeRepository : ISessionRepository
    {
        private readonly Dictionary<Guid, SessionProfile> _sessions = [];
        private readonly List<ServerGroup> _groups = [];

        public IReadOnlyList<SessionProfile> Sessions => [.. _sessions.Values];

        public IReadOnlyList<ServerGroup> Groups => _groups;

        public Task<List<ServerGroup>> GetAllGroupsAsync() => Task.FromResult(new List<ServerGroup>(_groups));

        public Task<List<SessionProfile>> GetAllSessionsAsync() => Task.FromResult(new List<SessionProfile>(_sessions.Values));

        public Task<SessionProfile?> GetSessionAsync(Guid id) =>
            Task.FromResult(_sessions.TryGetValue(id, out SessionProfile? profile) ? profile : null);

        public Task SaveSessionAsync(SessionProfile session)
        {
            _sessions[session.Id] = session;
            return Task.CompletedTask;
        }

        public Task DeleteSessionAsync(Guid id)
        {
            _sessions.Remove(id);
            return Task.CompletedTask;
        }

        public Task SaveGroupAsync(ServerGroup group)
        {
            _groups.RemoveAll(g => g.Id == group.Id);
            _groups.Add(group);
            return Task.CompletedTask;
        }

        public Task DeleteGroupAsync(Guid id)
        {
            _groups.RemoveAll(g => g.Id == id);
            return Task.CompletedTask;
        }
    }
}
