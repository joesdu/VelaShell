using System.Globalization;
using System.Text;
using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Import;

/// <summary>CSV 表格:导出即模板,Excel 改完能原样导回;读的时候对分隔符、表头写法、编码都宽容。</summary>
[TestClass]
[TestCategory("SessionArchive")]
public sealed class SessionCsvTests
{
    private static string Header => string.Join(',', SessionCsv.Header);

    [TestMethod]
    public void WriteThenRead_RoundTripsTheCommonFields()
    {
        var group = new SessionArchiveGroup { Id = Guid.NewGuid(), Name = "生产, 主机房" };
        var credential = new SharedCredential { Id = Guid.NewGuid(), Name = "switch admin" };
        var bastion = new SessionProfile { Name = "bastion", Host = "203.0.113.1", Username = "jump", Password = "never-written" };
        var web = new SessionProfile
        {
            Name = "web \"01\"",
            Host = "10.0.0.11",
            Port = 2222,
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = @"C:\keys\id_ed25519",
            GroupId = group.Id,
            JumpHostProfileId = bastion.Id,
            Tags = ["web", "nginx"],
            Notes = "第一行\n第二行, 带逗号"
        };
        var ftp = new SessionProfile
        {
            Name = "files",
            Host = "ftp.example.com",
            Port = 990,
            ConnectionType = ConnectionType.FTP,
            Ftp = new FtpSettings { EncryptionMode = FtpEncryptionMode.Implicit },
            CredentialSource = CredentialReference.ForShared(credential.Id)
        };
        var archive = new SessionArchive { Groups = [group], Sessions = [bastion, web, ftp], SharedCredentials = [credential] };

        string csv = SessionCsv.Write(archive);
        SessionImportDocument document = SessionCsv.Read("export.csv", csv);

        Assert.DoesNotContain("never-written", csv, "CSV 一律不写机密");
        Assert.HasCount(3, document.Candidates);
        Assert.IsEmpty(document.Warnings);
        SessionImportCandidate readWeb = document.Candidates[1];
        Assert.IsTrue(readWeb.IsValid, string.Join("; ", readWeb.Errors));
        Assert.IsTrue(readWeb.HasFileId);
        Assert.AreEqual(web.Id, readWeb.Profile.Id);
        Assert.AreEqual("web \"01\"", readWeb.Profile.Name);
        Assert.AreEqual(2222, readWeb.Profile.Port);
        Assert.AreEqual("deploy", readWeb.Profile.Username);
        Assert.AreEqual(AuthMethod.PrivateKey, readWeb.Profile.AuthMethod);
        Assert.AreEqual(@"C:\keys\id_ed25519", readWeb.Profile.PrivateKeyPath);
        Assert.AreEqual("生产, 主机房", readWeb.GroupName);
        Assert.AreEqual("bastion", readWeb.JumpHostReference, "跳板按名称写,人看得懂");
        Assert.AreSequenceEqual(new[] { "web", "nginx" }, readWeb.Profile.Tags.ToArray());
        Assert.AreEqual("第一行\n第二行, 带逗号", readWeb.Profile.Notes);
        SessionImportCandidate readFtp = document.Candidates[2];
        Assert.AreEqual(ConnectionType.FTP, readFtp.Profile.ConnectionType);
        Assert.AreEqual(FtpEncryptionMode.Implicit, readFtp.Profile.Ftp!.EncryptionMode);
        Assert.AreEqual("switch admin", readFtp.CredentialReference);
        Assert.IsNull(document.Candidates[0].Profile.Password);
    }

    [TestMethod]
    public void Write_UsesTheIdForAJumpHostWhoseNameIsNotUnique()
    {
        var first = new SessionProfile { Name = "jump", Host = "1.1.1.1" };
        var second = new SessionProfile { Name = "jump", Host = "2.2.2.2" };
        var target = new SessionProfile { Name = "app", Host = "10.0.0.1", JumpHostProfileId = second.Id };

        SessionImportDocument document = SessionCsv.Read("x.csv", SessionCsv.Write(new SessionArchive { Sessions = [first, second, target] }));

        Assert.AreEqual(second.Id.ToString("D"), document.Candidates[2].JumpHostReference);
    }

    [TestMethod]
    public void Write_EscapesFormulaLikeCells_AndReadUndoesIt()
    {
        var archive = new SessionArchive
        {
            Sessions = [new SessionProfile { Name = "=HYPERLINK(\"http://x\")", Host = "h", Notes = "-rf 慎用" }]
        };

        string csv = SessionCsv.Write(archive);
        SessionImportCandidate read = SessionCsv.Read("x.csv", csv).Candidates.Single();

        Assert.Contains("'=HYPERLINK", csv, "以 = 开头的单元格要加单引号,Excel 才不会当公式执行");
        Assert.Contains("'-rf", csv);
        Assert.AreEqual("=HYPERLINK(\"http://x\")", read.Profile.Name, "读回来时去掉那个单引号,往返不丢字");
        Assert.AreEqual("-rf 慎用", read.Profile.Notes);
    }

    [TestMethod]
    public void Template_ParsesIntoTwoValidExampleRows()
    {
        SessionImportDocument document = SessionCsv.Read("template.csv", SessionCsv.Template());

        Assert.HasCount(2, document.Candidates);
        Assert.IsTrue(document.Candidates.All(static c => c.IsValid));
        Assert.IsTrue(document.Candidates.All(static c => c.Profile.Name.StartsWith("example-", StringComparison.Ordinal)));
        Assert.IsTrue(document.Candidates.All(static c => !c.HasFileId), "模板里的 id 列空着:导入的都是新连接");
        Assert.AreEqual("example-web", document.Candidates[1].JumpHostReference);
    }

    [TestMethod]
    public void Read_AcceptsSemicolonAndTabDelimiters()
    {
        SessionImportDocument semicolon = SessionCsv.Read("de.csv", "name;host;port\r\nweb;10.0.0.1;2200\r\n");
        SessionImportDocument tab = SessionCsv.Read("pasted.tsv", "name\thost\tport\nweb\t10.0.0.2\t2201\n");

        Assert.AreEqual("10.0.0.1", semicolon.Candidates.Single().Profile.Host);
        Assert.AreEqual(2200, semicolon.Candidates.Single().Profile.Port);
        Assert.AreEqual("10.0.0.2", tab.Candidates.Single().Profile.Host);
        Assert.AreEqual(2201, tab.Candidates.Single().Profile.Port);
    }

    [TestMethod]
    public void Read_AcceptsChineseHeaders_InAnyOrder_AndIgnoresUnknownColumns()
    {
        const string csv = "备注,主机,用户名,分组,端口,名称,机房位置\n核心交换机,192.168.1.1,admin,网络设备,23,core-sw,A01\n";

        SessionImportDocument document = SessionCsv.Read("zh.csv", csv);

        SessionImportCandidate row = document.Candidates.Single();
        Assert.IsTrue(row.IsValid, string.Join("; ", row.Errors));
        Assert.AreEqual("core-sw", row.Profile.Name);
        Assert.AreEqual("192.168.1.1", row.Profile.Host);
        Assert.AreEqual(23, row.Profile.Port);
        Assert.AreEqual("admin", row.Profile.Username);
        Assert.AreEqual("网络设备", row.GroupName);
        Assert.AreEqual("核心交换机", row.Profile.Notes);
        Assert.HasCount(1, document.Warnings, "不认识的列(机房位置)要告诉用户被忽略了");
        Assert.Contains("机房位置", document.Warnings[0]);
    }

    [TestMethod]
    public void Read_SplitsUserAtHostColonPort_WhenTheDedicatedColumnsAreEmpty()
    {
        const string csv = "host,username,port\nroot@10.0.0.5:2222,,\nops@10.0.0.6,admin,\n[2001:db8::1]:22,,\nfe80::1,,\n";

        IReadOnlyList<SessionImportCandidate> rows = SessionCsv.Read("x.csv", csv).Candidates;

        Assert.AreEqual(("10.0.0.5", "root", 2222), (rows[0].Profile.Host, rows[0].Profile.Username, rows[0].Profile.Port));
        Assert.AreEqual(("ops@10.0.0.6", "admin"), (rows[1].Profile.Host, rows[1].Profile.Username),
            "用户名一栏已经写了,主机里的 @ 就不拆(以专门那一栏为准)");
        Assert.AreEqual(("2001:db8::1", 22), (rows[2].Profile.Host, rows[2].Profile.Port));
        Assert.AreEqual("fe80::1", rows[3].Profile.Host, "裸 IPv6 有多个冒号,不能拆");
    }

    [TestMethod]
    public void Read_FillsDefaults_ByProtocol()
    {
        const string csv = "host,protocol\na,\nb,sftp\nc,ftp\nd,ftps-implicit\ne,FTPS\n";

        IReadOnlyList<SessionImportCandidate> rows = SessionCsv.Read("x.csv", csv).Candidates;

        Assert.AreEqual((ConnectionType.SSH, 22), (rows[0].Profile.ConnectionType, rows[0].Profile.Port));
        Assert.AreEqual((ConnectionType.SFTP, 22), (rows[1].Profile.ConnectionType, rows[1].Profile.Port));
        Assert.AreEqual((ConnectionType.FTP, 21, FtpEncryptionMode.Auto), (rows[2].Profile.ConnectionType, rows[2].Profile.Port, rows[2].Profile.Ftp!.EncryptionMode));
        Assert.AreEqual((990, FtpEncryptionMode.Implicit), (rows[3].Profile.Port, rows[3].Profile.Ftp!.EncryptionMode));
        Assert.AreEqual(FtpEncryptionMode.Explicit, rows[4].Profile.Ftp!.EncryptionMode, "协议名不区分大小写");
        Assert.AreEqual("a", rows[0].Profile.Name, "名称空着就用主机");
        Assert.AreEqual(AuthMethod.Password, rows[0].Profile.AuthMethod);
    }

    [TestMethod]
    public void Read_InfersTheAuthMethod_FromWhatIsGiven()
    {
        const string csv = "host,private_key,certificate,password\na,~/.ssh/id,,\nb,~/.ssh/id,~/.ssh/id-cert.pub,\nc,,,p@ss \n";

        IReadOnlyList<SessionImportCandidate> rows = SessionCsv.Read("x.csv", csv).Candidates;

        Assert.AreEqual(AuthMethod.PrivateKey, rows[0].Profile.AuthMethod);
        Assert.AreEqual(AuthMethod.Certificate, rows[1].Profile.AuthMethod);
        Assert.AreEqual(AuthMethod.Password, rows[2].Profile.AuthMethod);
        Assert.AreEqual("p@ss ", rows[2].Profile.Password, "密码不修剪首尾空白");
        Assert.IsTrue(rows[2].HasSecret);
    }

    [TestMethod]
    public void Read_ReportsRowErrors_WithLineNumbers_AndKeepsGoing()
    {
        const string csv = "name,host,port,protocol,auth\n"
                           + "ok,10.0.0.1,22,ssh,password\n"
                           + "no-host,,22,ssh,\n"
                           + "bad-port,10.0.0.3,99999,,\n"
                           + "bad-proto,10.0.0.4,22,gopher,\n"
                           + "ftp-key,10.0.0.5,21,ftp,key\n"
                           + "spaces,10.0.0 .6,22,,\n"
                           + "plugin,10.0.0.7,,plugin:velashell.telnet,\n"
                           + "\n"
                           + ",,,,\n";

        SessionImportDocument document = SessionCsv.Read("bad.csv", csv);

        Assert.HasCount(7, document.Candidates, "空行与只有逗号的行都跳过");
        Assert.IsTrue(document.Candidates[0].IsValid);
        Assert.IsTrue(document.Candidates.Skip(1).All(static c => !c.IsValid));
        Assert.AreEqual(3, document.Candidates[1].LineNumber, "行号从 1 起、含表头");
        Assert.AreEqual(8, document.Candidates[6].LineNumber);
    }

    [TestMethod]
    public void Read_ABadId_IsAWarning_NotAnError()
    {
        SessionImportCandidate row = SessionCsv.Read("x.csv", "id,host\nnot-a-guid,10.0.0.1\n").Candidates.Single();

        Assert.IsTrue(row.IsValid);
        Assert.IsFalse(row.HasFileId);
        Assert.HasCount(1, row.Warnings);
    }

    [TestMethod]
    public void Read_HandlesQuotedNewlinesAndDoubledQuotes()
    {
        const string csv = "name,host,notes\r\n\"a \"\"quoted\"\" name\",h1,\"line one\r\nline two\"\r\nplain,h2, \"spaced\"\r\n";

        IReadOnlyList<SessionImportCandidate> rows = SessionCsv.Read("x.csv", csv).Candidates;

        Assert.HasCount(2, rows);
        Assert.AreEqual("a \"quoted\" name", rows[0].Profile.Name);
        Assert.AreEqual("line one\nline two", rows[0].Profile.Notes);
        Assert.AreEqual(4, rows[1].LineNumber, "引号里的换行也要计进行号");
        Assert.AreEqual("spaced", rows[1].Profile.Notes, "逗号后带空格再起引号的也认");
    }

    [TestMethod]
    public void Read_WithoutAHostColumn_IsRejected()
    {
        Assert.ThrowsExactly<SessionFileFormatException>(() => SessionCsv.Read("x.csv", "name,port\na,22\n"));
        Assert.ThrowsExactly<SessionFileFormatException>(() => SessionCsv.Read("x.csv", "\r\n\r\n"));
    }

    [TestMethod]
    public void Read_PluginProtocolNeedsAnExplicitPort()
    {
        IReadOnlyList<SessionImportCandidate> rows = SessionCsv.Read("x.csv", "host,protocol,port\na,velashell.telnet,\nb,plugin:velashell.s3,443\n").Candidates;

        Assert.IsFalse(rows[0].IsValid);
        Assert.IsTrue(rows[1].IsValid);
        Assert.AreEqual("velashell.s3", rows[1].Profile.PluginProtocolId);
    }

    [TestMethod]
    public void Decode_UnderstandsBomUtf8_PlainUtf8_Utf16_AndFallsBackToTheLocalCodePage()
    {
        const string text = "name,host\n交换机,10.0.0.1\n";
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");

            Assert.AreEqual(text, SessionFileText.Decode(SessionFileText.EncodeCsv(text)));
            Assert.AreEqual(text, SessionFileText.Decode(Encoding.UTF8.GetBytes(text)));
            Assert.AreEqual(text, SessionFileText.Decode([.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes(text)]));
            Encoding gbk = CodePagesEncodingProvider.Instance.GetEncoding(936)!;
            Assert.AreEqual(text, SessionFileText.Decode(gbk.GetBytes(text)), "中文 Excel 默认存的 CSV 是 GBK");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [TestMethod]
    public void EncodeCsv_StartsWithAUtf8Bom()
    {
        byte[] bytes = SessionFileText.EncodeCsv(Header);

        Assert.AreSequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3], "没有 BOM,Excel 会按本地代码页打开,中文全成乱码");
    }
}
