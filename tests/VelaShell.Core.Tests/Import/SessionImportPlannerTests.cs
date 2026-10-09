using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Import;

/// <summary>
/// 导入规则:重复怎么判、三种处理方式各落到哪、分组 / 共享凭据 / 跳板引用怎么接到本机数据上。
/// </summary>
[TestClass]
[TestCategory("SessionArchive")]
public sealed class SessionImportPlannerTests
{
    private static SessionProfile Session(string name, string host, int port = 22, string user = "root", Guid? group = null) =>
        new() { Name = name, Host = host, Port = port, Username = user, GroupId = group };

    private static SessionImportDocument Json(SessionArchive archive, string? passphrase = null) =>
        SessionArchiveJson.Read("file.json", SessionArchiveJson.Serialize(archive, passphrase));

    private static SessionImportDocument Csv(string csv) => SessionCsv.Read("file.csv", csv);

    private static SessionImportPlan Plan(
        SessionImportDocument document,
        IReadOnlyList<SessionProfile>? sessions = null,
        IReadOnlyList<ServerGroup>? groups = null,
        IReadOnlyList<SharedCredential>? credentials = null) =>
        SessionImportPlanner.Plan(document, sessions ?? [], groups ?? [], credentials ?? []);

    private static SessionImportWriteSet Write(SessionImportPlan plan, SessionImportConflict conflict = SessionImportConflict.Skip,
        bool useFileGroups = true, Guid? target = null, IReadOnlyCollection<int>? selected = null) =>
        SessionImportPlanner.BuildWrites(plan, new SessionImportOptions
        {
            Conflict = conflict,
            UseFileGroups = useFileGroups,
            TargetGroupId = target,
            SelectedIndices = selected
        });

    // ———— 查重 ————

    [TestMethod]
    public void Plan_MatchesBySameIdFirst_ThenByEndpoint_PreferringTheSameName()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        SessionProfile twinA = Session("db-a", "10.0.0.2");
        SessionProfile twinB = Session("db", "10.0.0.2");
        SessionProfile renamedCopy = local.Clone();
        renamedCopy.Host = "10.9.9.9"; // 同 Id 但主机改过:仍是同一条
        SessionImportDocument document = Json(new SessionArchive
        {
            Sessions = [renamedCopy, Session("DB", "10.0.0.2"), Session("new", "10.0.0.3"), Session("other-user", "10.0.0.1", user: "admin")]
        });

        SessionImportPlan plan = Plan(document, [local, twinA, twinB]);

        Assert.AreEqual(SessionImportMatch.SameId, plan.Items[0].Match);
        Assert.AreSame(local, plan.Items[0].Existing);
        Assert.AreEqual(SessionImportMatch.SameEndpoint, plan.Items[1].Match);
        Assert.AreSame(twinB, plan.Items[1].Existing, "主机端口用户都一样的有两条时,挑同名的那条(不区分大小写)");
        Assert.AreEqual(SessionImportMatch.None, plan.Items[2].Match);
        Assert.AreEqual(SessionImportMatch.None, plan.Items[3].Match, "用户名不同就是另一个账号,不算重复");
    }

    [TestMethod]
    public void Plan_HostIsCaseInsensitive_UsernameIsNot()
    {
        SessionImportPlan plan = Plan(Csv("host,username\nWEB.example.com,root\nweb.example.com,Root\n"), [Session("w", "web.example.com")]);

        Assert.IsTrue(plan.Items[0].IsDuplicate);
        Assert.IsFalse(plan.Items[1].IsDuplicate);
    }

    // ———— 三种处理方式 ————

    [TestMethod]
    public void Skip_LeavesDuplicatesAlone_AndCreatesTheRest()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        SessionImportPlan plan = Plan(Csv("name,host,username\nweb2,10.0.0.1,root\nnew,10.0.0.2,root\n"), [local]);

        SessionImportWriteSet writes = Write(plan);

        Assert.AreEqual(1, writes.Created);
        Assert.AreEqual(0, writes.Updated);
        Assert.AreEqual(1, writes.Skipped);
        Assert.AreEqual("new", writes.Sessions.Single().Name);
    }

    [TestMethod]
    public void Overwrite_KeepsTheLocalIdAndSecrets_WhenTheFileCarriesNone()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        local.Password = "local-pw";
        local.PrivateKeyPassphrase = "local-phrase";
        local.LastConnectedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        SessionProfile incoming = local.Clone();
        incoming.Name = "web (renamed)";
        incoming.Port = 2200;

        SessionImportWriteSet writes = Write(Plan(Json(new SessionArchive { Sessions = [incoming] }), [local]), SessionImportConflict.Overwrite);

        SessionProfile written = writes.Sessions.Single();
        Assert.AreEqual(1, writes.Updated);
        Assert.AreEqual(local.Id, written.Id);
        Assert.AreEqual("web (renamed)", written.Name);
        Assert.AreEqual(2200, written.Port);
        Assert.AreEqual("local-pw", written.Password, "文件没带密码(导出时没勾敏感信息),覆盖不能把本机的密码抹掉");
        Assert.AreEqual("local-phrase", written.PrivateKeyPassphrase);
        Assert.AreEqual(local.LastConnectedAt, written.LastConnectedAt, "上次连接时间是本机状态,覆盖时留着");
        Assert.AreEqual(0, writes.SecretsImported);
    }

    [TestMethod]
    public void Overwrite_TakesTheFileSecrets_OnceUnlocked()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        local.Password = "old";
        SessionProfile incoming = local.Clone();
        incoming.Password = "new";
        SessionImportDocument document = Json(new SessionArchive { Sessions = [incoming] }, "pass-phrase");
        Assert.IsTrue(SessionArchiveJson.TryUnlock(document, "pass-phrase"));

        SessionImportWriteSet writes = Write(Plan(document, [local]), SessionImportConflict.Overwrite);

        Assert.AreEqual("new", writes.Sessions.Single().Password);
        Assert.AreEqual(1, writes.SecretsImported);
    }

    [TestMethod]
    public void KeepBoth_SavesACopyUnderANewId_AndRemapsReferencesInsideTheFile()
    {
        SessionProfile bastion = Session("bastion", "203.0.113.1");
        SessionProfile app = Session("app", "10.0.0.5");
        app.JumpHostProfileId = bastion.Id;
        SessionImportDocument document = Json(new SessionArchive { Sessions = [bastion.Clone(), app.Clone()] });

        SessionImportWriteSet writes = Write(Plan(document, [bastion, app]), SessionImportConflict.KeepBoth);

        Assert.AreEqual(2, writes.Created);
        SessionProfile copyBastion = writes.Sessions.Single(s => s.Name == "bastion");
        SessionProfile copyApp = writes.Sessions.Single(s => s.Name == "app");
        Assert.AreNotEqual(bastion.Id, copyBastion.Id);
        Assert.AreNotEqual(app.Id, copyApp.Id);
        Assert.AreEqual(copyBastion.Id, copyApp.JumpHostProfileId, "副本经副本跳,不能接回本机那台");
    }

    [TestMethod]
    public void NewSessions_KeepTheFileId_WhenItIsFree_AndGetANewOneOnCollision()
    {
        Guid shared = Guid.NewGuid();
        string csv = $"id,name,host\n{shared},first,10.0.0.1\n{shared},second,10.0.0.2\n";

        SessionImportWriteSet writes = Write(Plan(Csv(csv)));

        Assert.AreEqual(shared, writes.Sessions[0].Id, "沿用文件里的 Id:两台开着云同步的电脑导来导去,看到的仍是同一条");
        Assert.AreNotEqual(shared, writes.Sessions[1].Id, "文件里自己重复的 Id,第二条换新的");
    }

    [TestMethod]
    public void Overwrite_TheSameTargetTwice_SavesTheSecondAsANewSession()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        SessionImportPlan plan = Plan(Csv("name,host,username\nweb,10.0.0.1,root\nweb again,10.0.0.1,root\n"), [local]);

        SessionImportWriteSet writes = Write(plan, SessionImportConflict.Overwrite);

        Assert.AreEqual(1, writes.Updated);
        Assert.AreEqual(1, writes.Created, "同一条本机连接只能被覆盖一次,撞上的第二条另存");
    }

    [TestMethod]
    public void OnlySelectedValidRows_AreWritten()
    {
        SessionImportPlan plan = Plan(Csv("name,host,port\na,10.0.0.1,\nb,10.0.0.2,\nbroken,10.0.0.3,99999\n"));

        SessionImportWriteSet writes = Write(plan, selected: [1, 2]);

        Assert.AreEqual("b", writes.Sessions.Single().Name, "有错误的行就算勾上也不写");
        Assert.AreEqual(2, writes.Skipped);
    }

    // ———— 分组 ————

    [TestMethod]
    public void FileGroups_MapById_ThenByName_AndMissingOnesAreAppendedInFileOrder()
    {
        var sameId = new ServerGroup { Id = Guid.NewGuid(), Name = "Lab (renamed locally)", SortOrder = 0 };
        var sameName = new ServerGroup { Id = Guid.NewGuid(), Name = "Prod", SortOrder = 5 };
        var fileProd = new SessionArchiveGroup { Id = Guid.NewGuid(), Name = "prod", SortOrder = 0 };
        var fileLab = new SessionArchiveGroup { Id = sameId.Id, Name = "Lab", SortOrder = 1 };
        var fileZ = new SessionArchiveGroup { Id = Guid.NewGuid(), Name = "Zeta", SortOrder = 9 };
        var fileA = new SessionArchiveGroup { Id = Guid.NewGuid(), Name = "Alpha", SortOrder = 2 };
        SessionImportDocument document = Json(new SessionArchive
        {
            Groups = [fileProd, fileLab, fileZ, fileA],
            Sessions =
            [
                Session("p", "10.0.0.1", group: fileProd.Id),
                Session("l", "10.0.0.2", group: fileLab.Id),
                Session("z", "10.0.0.3", group: fileZ.Id),
                Session("a", "10.0.0.4", group: fileA.Id),
                Session("none", "10.0.0.5")
            ]
        });

        SessionImportWriteSet writes = Write(Plan(document, groups: [sameId, sameName]));

        Assert.AreEqual(sameName.Id, writes.Sessions.Single(s => s.Name == "p").GroupId, "同名(不区分大小写)并进已有分组");
        Assert.AreEqual(sameId.Id, writes.Sessions.Single(s => s.Name == "l").GroupId, "同 Id 的分组优先,哪怕本机改过名");
        Assert.IsNull(writes.Sessions.Single(s => s.Name == "none").GroupId);
        Assert.AreEqual(2, writes.GroupsCreated);
        ServerGroup alpha = writes.Groups.Single(g => g.Name == "Alpha");
        ServerGroup zeta = writes.Groups.Single(g => g.Name == "Zeta");
        Assert.AreEqual(fileA.Id, alpha.Id, "新建的分组沿用文件里的 Id");
        Assert.AreEqual(6, alpha.SortOrder, "新分组接在已有分组之后");
        Assert.AreEqual(7, zeta.SortOrder, "彼此之间按文件里的先后");
        Assert.AreSequenceEqual(new[] { writes.Sessions.Single(s => s.Name == "a").Id }, alpha.Sessions.ToArray());
    }

    [TestMethod]
    public void CsvGroups_ByName_CreateEachMissingGroupOnce()
    {
        SessionImportWriteSet writes = Write(Plan(Csv("name,host,group\na,1.1.1.1,Switches\nb,1.1.1.2,switches\nc,1.1.1.3,\n")));

        Assert.AreEqual(1, writes.GroupsCreated);
        Guid group = writes.Groups.Single().Id;
        Assert.AreEqual(group, writes.Sessions[0].GroupId);
        Assert.AreEqual(group, writes.Sessions[1].GroupId);
        Assert.IsNull(writes.Sessions[2].GroupId);
        Assert.AreEqual(0, writes.Groups.Single().SortOrder, "本机一个分组都没有时从 0 排起");
    }

    [TestMethod]
    public void TargetGroup_OverridesTheFileGroups()
    {
        var target = new ServerGroup { Id = Guid.NewGuid(), Name = "Imported" };
        SessionImportPlan plan = Plan(Csv("name,host,group\na,1.1.1.1,Switches\n"), groups: [target]);

        SessionImportWriteSet intoTarget = Write(plan, useFileGroups: false, target: target.Id);
        SessionImportWriteSet ungrouped = Write(plan, useFileGroups: false, target: null);
        SessionImportWriteSet vanished = Write(plan, useFileGroups: false, target: Guid.NewGuid());

        Assert.AreEqual(target.Id, intoTarget.Sessions.Single().GroupId);
        Assert.AreEqual(0, intoTarget.GroupsCreated);
        Assert.IsNull(ungrouped.Sessions.Single().GroupId);
        Assert.IsNull(vanished.Sessions.Single().GroupId, "目标分组在这期间被删掉了:放进未分组,而不是指向一个不存在的分组");
    }

    [TestMethod]
    public void Overwrite_MovingASessionToAnotherGroup_UpdatesBothMemberLists()
    {
        var oldGroup = new ServerGroup { Id = Guid.NewGuid(), Name = "Old" };
        var newGroup = new ServerGroup { Id = Guid.NewGuid(), Name = "New" };
        SessionProfile local = Session("web", "10.0.0.1", group: oldGroup.Id);
        oldGroup.Sessions.Add(local.Id);
        SessionImportPlan plan = Plan(Csv($"id,host,group\n{local.Id},10.0.0.1,New\n"), [local], [oldGroup, newGroup]);

        SessionImportWriteSet writes = Write(plan, SessionImportConflict.Overwrite);

        Assert.AreEqual(newGroup.Id, writes.Sessions.Single().GroupId);
        Assert.IsEmpty(writes.Groups.Single(g => g.Id == oldGroup.Id).Sessions);
        Assert.Contains(local.Id, writes.Groups.Single(g => g.Id == newGroup.Id).Sessions);
        Assert.HasCount(1, oldGroup.Sessions, "调用方传进来的分组对象不能被改");
    }

    // ———— CSV 覆盖 ————

    [TestMethod]
    public void CsvOverwrite_OnlyTouchesTheColumnsInTheFile()
    {
        var group = new ServerGroup { Id = Guid.NewGuid(), Name = "Prod" };
        SessionProfile local = Session("web", "10.0.0.1", port: 2200, group: group.Id);
        local.Password = "keep-me";
        local.Notes = "keep these notes";
        local.Terminal = new TerminalOverrides { Encoding = "GBK" };
        local.Ssh = new SshSessionOptions { Compression = true };
        local.Tags = ["prod"];

        SessionImportWriteSet writes = Write(Plan(Csv($"id,host,username\n{local.Id},10.0.0.1,deploy\n"), [local], [group]), SessionImportConflict.Overwrite);

        SessionProfile written = writes.Sessions.Single();
        Assert.AreEqual("deploy", written.Username);
        Assert.AreEqual("10.0.0.1", written.Host);
        Assert.AreEqual(2200, written.Port);
        Assert.AreEqual("keep-me", written.Password);
        Assert.AreEqual("keep these notes", written.Notes);
        Assert.AreEqual("GBK", written.Terminal!.Encoding, "终端设置是 CSV 表达不了的东西,导回来不能被冲掉");
        Assert.IsTrue(written.Ssh!.Compression);
        Assert.AreSequenceEqual(new[] { "prod" }, written.Tags.ToArray());
        Assert.AreEqual(group.Id, written.GroupId, "没有 group 列就不动分组");
    }

    [TestMethod]
    public void CsvOverwrite_AnEmptyPasswordCellMeansDoNotChange_AndEmptyNotesClear()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        local.Password = "keep-me";
        local.Notes = "old notes";

        SessionImportWriteSet writes = Write(
            Plan(Csv($"id,host,password,notes\n{local.Id},10.0.0.1,,\n"), [local]),
            SessionImportConflict.Overwrite);

        Assert.AreEqual("keep-me", writes.Sessions.Single().Password, "导出时密码列恒为空:空着导回来是「不改」,不是「清空」");
        Assert.IsNull(writes.Sessions.Single().Notes, "其余列清空就是清空");
    }

    [TestMethod]
    public void CsvOverwrite_ChangingTheProtocol_DropsTheOldProtocolSettings()
    {
        SessionProfile local = new()
        {
            Name = "bucket",
            Host = "s3.local",
            Port = 9000,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = "velashell.s3",
            PluginSettings = new() { ["region"] = "x" }
        };

        SessionImportWriteSet writes = Write(
            Plan(Csv($"id,host,port,protocol\n{local.Id},s3.local,22,sftp\n"), [local]),
            SessionImportConflict.Overwrite);

        SessionProfile written = writes.Sessions.Single();
        Assert.AreEqual(ConnectionType.SFTP, written.ConnectionType);
        Assert.IsNull(written.PluginProtocolId);
        Assert.IsNull(written.PluginSettings);
    }

    // ———— 跳板 ————

    [TestMethod]
    public void JumpHost_SkippedAsADuplicate_PointsAtTheLocalOne()
    {
        SessionProfile localBastion = Session("bastion", "203.0.113.1");
        SessionProfile fileBastion = Session("bastion", "203.0.113.1"); // 另一台电脑上的同一台堡垒机:Id 不同
        SessionProfile app = Session("app", "10.0.0.5");
        app.JumpHostProfileId = fileBastion.Id;

        SessionImportWriteSet writes = Write(Plan(Json(new SessionArchive { Sessions = [fileBastion, app] }), [localBastion]));

        Assert.AreEqual("app", writes.Sessions.Single().Name);
        Assert.AreEqual(localBastion.Id, writes.Sessions.Single().JumpHostProfileId, "跳过的堡垒机落到本机已有那台上");
        Assert.IsEmpty(writes.Warnings);
    }

    [TestMethod]
    public void CsvJumpHost_ResolvesInsideTheFileFirst_ThenLocally_ThenWarns()
    {
        SessionProfile localJump = Session("local-jump", "198.51.100.1");
        SessionProfile twin1 = Session("twin", "198.51.100.2");
        SessionProfile twin2 = Session("twin", "198.51.100.3");
        const string csv = "name,host,jump_host\n"
                           + "a,10.0.0.1,file-jump\n"
                           + "file-jump,10.0.0.2,\n"
                           + "b,10.0.0.3,LOCAL-JUMP\n"
                           + "c,10.0.0.4,nowhere\n"
                           + "d,10.0.0.5,twin\n"
                           + "e,10.0.0.6,e\n";

        SessionImportWriteSet writes = Write(Plan(Csv(csv), [localJump, twin1, twin2]));

        Dictionary<string, SessionProfile> byName = writes.Sessions.ToDictionary(static s => s.Name);
        Assert.AreEqual(byName["file-jump"].Id, byName["a"].JumpHostProfileId, "跳板写在后面也接得上");
        Assert.AreEqual(localJump.Id, byName["b"].JumpHostProfileId, "文件里没有就找本机同名的(不区分大小写)");
        Assert.IsNull(byName["c"].JumpHostProfileId);
        Assert.IsNull(byName["d"].JumpHostProfileId, "本机有两条同名的:宁可不接也不接错");
        Assert.IsNull(byName["e"].JumpHostProfileId, "不能拿自己当跳板");
        Assert.HasCount(3, writes.Warnings);
    }

    [TestMethod]
    public void JumpHost_Cycles_AreBroken()
    {
        const string csv = "name,host,jump_host\na,10.0.0.1,b\nb,10.0.0.2,a\nc,10.0.0.3,a\n";

        SessionImportWriteSet writes = Write(Plan(Csv(csv)));

        Dictionary<string, SessionProfile> byName = writes.Sessions.ToDictionary(static s => s.Name);
        Assert.IsTrue(byName["a"].JumpHostProfileId is null || byName["b"].JumpHostProfileId is null, "a→b→a 必须断开一环");
        Assert.IsFalse(byName["a"].JumpHostProfileId is null && byName["b"].JumpHostProfileId is null, "只断一环,另一条照旧");
        Assert.AreEqual(byName["a"].Id, byName["c"].JumpHostProfileId, "不在环上的不受影响");
        Assert.HasCount(1, writes.Warnings);
    }

    [TestMethod]
    public void JsonJumpHost_NotInTheFileAndNotLocal_BecomesDirect_WithAWarning()
    {
        SessionProfile app = Session("app", "10.0.0.5");
        app.JumpHostProfileId = Guid.NewGuid();

        SessionImportPlan plan = Plan(Json(new SessionArchive { Sessions = [app] }));
        SessionImportWriteSet writes = Write(plan);

        Assert.HasCount(1, plan.Items[0].Warnings, "预览里就该提示");
        Assert.IsNull(writes.Sessions.Single().JumpHostProfileId);
        Assert.HasCount(1, writes.Warnings);
    }

    // ———— 共享凭据 ————

    [TestMethod]
    public void JsonCredentials_SameIdOrNameReuseTheLocalOne_OtherwiseOneIsCreated()
    {
        var localById = new SharedCredential { Id = Guid.NewGuid(), Name = "by-id", Password = "local" };
        var localByName = new SharedCredential { Id = Guid.NewGuid(), Name = "Switch Admin" };
        var fileById = new SharedCredential { Id = localById.Id, Name = "renamed", Password = "file" };
        var fileByName = new SharedCredential { Id = Guid.NewGuid(), Name = "switch admin" };
        var fileNew = new SharedCredential { Id = Guid.NewGuid(), Name = "brand new", Username = "u" };
        SessionProfile a = Session("a", "10.0.0.1");
        a.CredentialSource = CredentialReference.ForShared(fileById.Id);
        SessionProfile b = Session("b", "10.0.0.2");
        b.CredentialSource = CredentialReference.ForShared(fileByName.Id);
        SessionProfile c = Session("c", "10.0.0.3");
        c.CredentialSource = CredentialReference.ForShared(fileNew.Id);
        SessionProfile d = Session("d", "10.0.0.4");
        d.CredentialSource = CredentialReference.ForShared(fileNew.Id);
        SessionImportDocument document = Json(new SessionArchive
        {
            Sessions = [a, b, c, d],
            SharedCredentials = [fileById, fileByName, fileNew]
        });

        SessionImportWriteSet writes = Write(Plan(document, credentials: [localById, localByName]));

        Dictionary<string, SessionProfile> byName = writes.Sessions.ToDictionary(static s => s.Name);
        Assert.IsTrue(byName["a"].CredentialSource!.TryGetSharedId(out Guid aId) && aId == localById.Id);
        Assert.IsTrue(byName["b"].CredentialSource!.TryGetSharedId(out Guid bId) && bId == localByName.Id);
        SharedCredential created = writes.SharedCredentials.Single();
        Assert.AreEqual("brand new", created.Name);
        Assert.IsTrue(byName["c"].CredentialSource!.TryGetSharedId(out Guid cId) && cId == created.Id);
        Assert.IsTrue(byName["d"].CredentialSource!.TryGetSharedId(out Guid dId) && dId == created.Id, "同一条凭据只建一次");
    }

    [TestMethod]
    public void CsvCredential_ByName_AndAMissingOneFallsBackToInlineAuth()
    {
        var local = new SharedCredential { Id = Guid.NewGuid(), Name = "Switch Admin" };
        const string csv = "name,host,credential\na,10.0.0.1,switch admin\nb,10.0.0.2,ghost\n";

        SessionImportPlan plan = Plan(Csv(csv), credentials: [local]);
        SessionImportWriteSet writes = Write(plan);

        Assert.IsTrue(writes.Sessions[0].CredentialSource!.TryGetSharedId(out Guid id) && id == local.Id);
        Assert.IsNull(writes.Sessions[1].CredentialSource);
        Assert.IsEmpty(plan.Items[0].Warnings);
        Assert.HasCount(1, plan.Items[1].Warnings, "预览里就提示找不到");
        Assert.IsEmpty(writes.SharedCredentials, "CSV 不会新建共享凭据(它表达不了凭据本身)");
    }

    // ———— 代码评审补的几条(#571) ————

    [TestMethod]
    public void Overwrite_TheRowWithTheSameIdWinsTheTarget_EvenIfAnEndpointMatchComesFirst()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        string csv = $"id,name,host,username\n,impostor,10.0.0.1,root\n{local.Id},web (real),10.0.0.1,root\n";

        SessionImportWriteSet writes = Write(Plan(Csv(csv), [local]), SessionImportConflict.Overwrite);

        Assert.AreEqual("web (real)", writes.Sessions.Single(s => s.Id == local.Id).Name, "同 Id 的那一行才是本机那条连接本身");
        Assert.AreEqual(1, writes.Updated);
        Assert.AreEqual(1, writes.Created, "按主机撞上的那一行另存");
    }

    [TestMethod]
    public void CsvRoundTrip_KeepsTheCredential_WhenItsNameIsNotUniqueLocally()
    {
        var mine = new SharedCredential { Id = Guid.NewGuid(), Name = "admin", AuthMethod = AuthMethod.PrivateKey };
        var other = new SharedCredential { Id = Guid.NewGuid(), Name = "admin" };
        SessionProfile local = Session("web", "10.0.0.1");
        local.CredentialSource = CredentialReference.ForShared(mine.Id);
        local.AuthMethod = AuthMethod.PrivateKey;
        string csv = SessionCsv.Write(new SessionArchive { Sessions = [local], SharedCredentials = [mine] });

        SessionImportPlan plan = Plan(Csv(csv), [local], credentials: [mine, other]);
        SessionImportWriteSet writes = Write(plan, SessionImportConflict.Overwrite);

        SessionProfile written = writes.Sessions.Single();
        Assert.IsTrue(written.CredentialSource!.TryGetSharedId(out Guid id) && id == mine.Id,
            "本机有两条叫 admin 的凭据:导回时认它原来引用的那一条,而不是把引用弄丢");
        Assert.IsEmpty(plan.Items[0].Warnings, "预览里也不该误报重名");
        Assert.AreEqual(AuthMethod.PrivateKey, written.AuthMethod, "auth 列照样导出,往返不会被推断成密码");
    }

    [TestMethod]
    public void CsvJumpHost_AmbiguousLocalName_FallsBackToTheTargetsCurrentJumpHost()
    {
        var bastion = Session("bastion", "203.0.113.1");
        var twin = Session("bastion", "203.0.113.2");
        SessionProfile local = Session("web", "10.0.0.1");
        local.JumpHostProfileId = twin.Id;

        SessionImportWriteSet writes = Write(
            Plan(Csv($"id,host,username,jump_host\n{local.Id},10.0.0.1,root,bastion\n"), [local, bastion, twin]),
            SessionImportConflict.Overwrite);

        Assert.AreEqual(twin.Id, writes.Sessions.Single().JumpHostProfileId);
        Assert.IsEmpty(writes.Warnings);
    }

    [TestMethod]
    public void ReusedCredential_GetsItsMissingSecretFromAnUnlockedFile_ButKeepsTheOnesItHas()
    {
        var missing = new SharedCredential { Id = Guid.NewGuid(), Name = "synced without passphrase" };
        var complete = new SharedCredential { Id = Guid.NewGuid(), Name = "complete", Password = "local" };
        var fileMissing = missing.Clone();
        fileMissing.Password = "from-file";
        var fileComplete = complete.Clone();
        fileComplete.Password = "file-should-not-win";
        SessionProfile a = Session("a", "10.0.0.1");
        a.CredentialSource = CredentialReference.ForShared(missing.Id);
        SessionProfile b = Session("b", "10.0.0.2");
        b.CredentialSource = CredentialReference.ForShared(complete.Id);
        SessionImportDocument document = Json(new SessionArchive { Sessions = [a, b], SharedCredentials = [fileMissing, fileComplete] }, "pass-phrase");
        Assert.IsTrue(SessionArchiveJson.TryUnlock(document, "pass-phrase"));

        SessionImportWriteSet writes = Write(Plan(document, credentials: [missing, complete]));

        SharedCredential saved = writes.SharedCredentials.Single();
        Assert.AreEqual(missing.Id, saved.Id);
        Assert.AreEqual("from-file", saved.Password);
        Assert.IsNull(missing.Password, "调用方传进来的对象不能被改");
    }

    [TestMethod]
    public void Preview_WarnsWhenOverwritingWouldSendASavedPasswordToANewHost()
    {
        SessionProfile withPassword = Session("db", "10.0.0.5");
        withPassword.Password = "saved";
        SessionProfile without = Session("web", "10.0.0.6");
        string csv = $"id,host,username\n{withPassword.Id},198.51.100.66,root\n{without.Id},198.51.100.67,root\n";

        SessionImportPlan plan = Plan(Csv(csv), [withPassword, without]);

        Assert.HasCount(1, plan.Items[0].Warnings);
        Assert.Contains("198.51.100.66", plan.Items[0].Warnings[0]);
        Assert.IsEmpty(plan.Items[1].Warnings, "没存密码的,改主机不必提醒");
    }

    [TestMethod]
    public void CsvOverwrite_UserAndPortSplitFromTheHostCell_AreApplied()
    {
        SessionProfile local = Session("web", "10.0.0.1");

        SessionImportWriteSet writes = Write(
            Plan(Csv($"id,host\n{local.Id},admin@10.0.0.9:2200\n"), [local]),
            SessionImportConflict.Overwrite);

        SessionProfile written = writes.Sessions.Single();
        Assert.AreEqual(("10.0.0.9", "admin", 2200), (written.Host, written.Username, written.Port),
            "表里没有 username / port 列,但这一格里写了:这一行就算给了这两列");
    }

    // ———— 隧道与统计 ————

    [TestMethod]
    public void Tunnels_FollowTheFinalId()
    {
        SessionProfile local = Session("web", "10.0.0.1");
        SessionProfile incoming = local.Clone();
        var tunnel = new TunnelConfig { Type = TunnelType.DynamicForward, Name = "socks", LocalHost = "127.0.0.1", LocalPort = 1080 };
        SessionImportDocument document = Json(new SessionArchive { Sessions = [incoming], Tunnels = new() { [incoming.Id] = [tunnel] } });

        SessionImportWriteSet keepBoth = Write(Plan(document, [local]), SessionImportConflict.KeepBoth);
        SessionImportWriteSet skip = Write(Plan(document, [local]));

        Guid copyId = keepBoth.Sessions.Single().Id;
        Assert.AreNotEqual(local.Id, copyId);
        Assert.AreEqual("socks", keepBoth.Tunnels[copyId].Single().Name);
        Assert.IsEmpty(skip.Tunnels, "跳过的连接不碰它的隧道");
    }

    [TestMethod]
    public void SecretsImported_CountsRowsThatBroughtASecret()
    {
        SessionImportWriteSet writes = Write(Plan(Csv("host,password\n10.0.0.1,pw\n10.0.0.2,\n")));

        Assert.AreEqual(1, writes.SecretsImported);
        Assert.IsTrue(writes.Sessions[0].RememberPassword);
    }
}
