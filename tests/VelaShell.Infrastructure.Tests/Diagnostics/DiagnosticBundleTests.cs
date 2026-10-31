using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.Infrastructure.Tests.Diagnostics;

/// <summary>
/// 诊断包:收齐日志与概况,落盘前脱敏;主机、用户名、本机路径、IP、邮箱与口令不出现在包里。
/// </summary>
[TestClass]
public sealed class DiagnosticBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"velashell_diagbundle_{Guid.NewGuid():N}");
    private string Logs => Path.Combine(_root, "logs");

    public DiagnosticBundleTests() => Directory.CreateDirectory(Logs);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void Redactor_ReplacesKnownHostsUsersAndPathsConsistentlyAndRespectsWordBoundaries()
    {
        var redactor = new DiagnosticRedactor(
            hosts: ["db1.prod.example.com", "10.20.30.40", "[2001:db8::7]", "localhost"],
            userNames: ["deploy", "root", "ab"],
            homeDirectory: @"C:\Users\Alice", localUserName: "Alice", machineName: "ALICE-PC");

        string text = redactor.Redact(
            "[SSH] deploy@db1.prod.example.com:22 连接结束\n"
            + "[SSH] deploy@10.20.30.40:22 again db1.prod.example.com\n"
            + @"open C:\Users\Alice\.velashell\logs and C:/Users/Alice/x on ALICE-PC as alice" + "\n"
            + "ipv6 2001:db8::7 root ab deployer redeploy 10.20.30.400 db1.prod.example.community\n");

        Assert.Contains("[SSH] <user-1>@<host-1>:22 连接结束", text);
        Assert.Contains("[SSH] <user-1>@<host-2>:22 again <host-1>", text, "同一个值换成同一个占位符");
        Assert.Contains(@"open ~\.velashell\logs and ~/x on <machine> as <local-user>", text);
        Assert.Contains("ipv6 <host-3> root ab deployer redeploy", text,
            "常见登录名与两个字符以内的值不换;词的一部分不换");
        Assert.DoesNotContain("10.20.30.40:", text);
        Assert.Contains("10.20.30.400", text, "不是合法 IPv4 的数字串不动");
        Assert.Contains("db1.prod.example.community", text, "更长的域名不是同一台机器");
        Assert.AreEqual(4, redactor.Counts["host"], "四处主机,其中一处是 IPv6");
    }

    [TestMethod]
    public void Redactor_ScrubsUnknownIpsEmailsAndSecretsButKeepsLoopback()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "connect 203.0.113.9 then 203.0.113.9 and 198.51.100.1, local 127.0.0.1 any 0.0.0.0\n"
            + "mail ops.team+alerts@corp.example.org\n"
            + "proxy password=hunter2; token: abc.def Authorization: Bearer sk-live-123== apikey=xyz\n");
        Assert.Contains("connect <ip-1> then <ip-1> and <ip-2>, local 127.0.0.1 any 0.0.0.0", text);
        Assert.Contains("mail <email>", text);
        Assert.Contains("password=<redacted>; token: <redacted>", text);
        Assert.Contains("Bearer <redacted>", text);
        Assert.Contains("apikey=<redacted>", text);
        foreach (string secret in new[] { "hunter2", "abc.def", "sk-live-123", "xyz", "corp.example.org" })
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    [TestMethod]
    public async Task Bundle_CollectsRedactedLogsCrashesAndSummaryWithoutLeavingPartialFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "[SSH] deploy@db1.example.net:22 连接结束\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "crash-20261019-101010-000.txt"), "at C:\\Users\\Alice\\app\nIP 203.0.113.9\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "notes.md"), "not a log");
        // 日志正被监听器追加写入时也要读得到
        await using var held = new FileStream(Path.Combine(Logs, "velashell-20261020.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await held.WriteAsync(Encoding.UTF8.GetBytes("live line from deploy\n"));
        await held.FlushAsync();

        var redactor = new DiagnosticRedactor(["db1.example.net"], ["deploy"], @"C:\Users\Alice", "Alice", "ALICE-PC");
        var request = new DiagnosticBundleRequest("v1.7.2", Logs,
            [new DiagnosticPluginInfo("velashell.ai", "2.1.0", "Running", "Isolated", false, @"failed at C:\Users\Alice\plugins")],
            redactor);
        string zipPath = Path.Combine(_root, "bundle.zip");

        DiagnosticBundleResult result = await DiagnosticBundle.WriteAsync(zipPath, request);

        Assert.AreEqual(3, result.LogFiles);
        Assert.IsFalse(File.Exists(zipPath + ".partial"));
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.AreSequenceEqual(
            ["logs/velashell-20261019.log", "logs/velashell-20261020.log", "logs/crash-20261019-101010-000.txt", "summary.txt", "README.txt"], [.. zip.Entries.Select(entry => entry.FullName)], SequenceOrder.InAnyOrder);
        string all = string.Join('\n', zip.Entries.Select(Read));
        foreach (string secret in new[] { "deploy", "db1.example.net", "Alice", "203.0.113.9" })
        {
            Assert.DoesNotContain(secret, all, $"包里不能出现 {secret}");
        }
        string summary = Read(zip.GetEntry("summary.txt")!);
        Assert.Contains("VelaShell: v1.7.2", summary);
        Assert.Contains("velashell.ai 2.1.0 state=Running mode=Isolated error=failed at ~\\plugins", summary);
        string readMe = Read(zip.GetEntry("README.txt")!);
        Assert.Contains("logs/: 3 file(s)", readMe);
        Assert.Contains("host: 1", readMe);
        Assert.Contains("ip: 1", readMe);
    }

    [TestMethod]
    public void Redactor_MasksSecretsWhoseNameContainsTheKeywordQuotedJsonBasicPemAndPrefixedTokens()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "env DB_PASSWORD=v-db MYSQL_ROOT_PASSWORD=v-root GITHUB_TOKEN=v-gh SECRET_KEY=v-sk AWS_SECRET_ACCESS_KEY=v-aws\n"
            + "oauth access_token=v-at refresh_token=v-rt client_secret=v-cs\n"
            + "conf password=\"v q\" passphrase='v-pp' {\"password\": \"v-json\", \"api_key\":\"v-jkey\"}\n"
            + "http Authorization: Basic dXNlcjpwYXNzd29yZA== proxy {\"Proxy-Authorization\": \"basic YWRtaW46czNjcjN0\"}\n"
            + "key -----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAA\r\nQyNTUxOQAAACD\r\n-----END OPENSSH PRIVATE KEY-----\n"
            + "aws AKIAIOSFODNN7EXAMPLE gh ghp_0123456789abcdefghijABCDEFGHIJ0123 slack xoxb-1234567890-abcdefghij openai sk-proj-ABCDEFGHIJKLMNOPQRST\n");

        Assert.Contains("env DB_PASSWORD=<redacted> MYSQL_ROOT_PASSWORD=<redacted> GITHUB_TOKEN=<redacted> SECRET_KEY=<redacted> AWS_SECRET_ACCESS_KEY=<redacted>\n", text);
        Assert.Contains("oauth access_token=<redacted> refresh_token=<redacted> client_secret=<redacted>\n", text);
        Assert.Contains("conf password=\"<redacted>\" passphrase='<redacted>' {\"password\": \"<redacted>\", \"api_key\":\"<redacted>\"}\n", text,
            "引号留着,JSON 还是 JSON");
        Assert.Contains("http Authorization: Basic <redacted> proxy {\"Proxy-Authorization\": \"basic <redacted>\"}\n", text);
        Assert.Contains("key -----BEGIN OPENSSH PRIVATE KEY-----\r\n<redacted>\r\n-----END OPENSSH PRIVATE KEY-----\n", text,
            "私钥正文整段换掉,首尾两行留着");
        Assert.Contains("aws <redacted> gh <redacted> slack <redacted> openai <redacted>\n", text);
        foreach (string secret in new[]
                 {
                     "v-db", "v-root", "v-gh", "v-sk", "v-aws", "v-at", "v-rt", "v-cs", "v q", "v-pp", "v-json", "v-jkey",
                     "dXNlcjpwYXNzd29yZA", "YWRtaW46czNjcjN0", "b3BlbnNzaC1rZXktdjEAAAAA", "QyNTUxOQAAACD",
                     "AKIAIOSFODNN7EXAMPLE", "ghp_", "xoxb-", "sk-proj"
                 })
        {
            Assert.DoesNotContain(secret, text);
        }
        Assert.AreEqual(19, redactor.Counts["secret"], "每处只算一次");
    }

    [TestMethod]
    public void Redactor_LeavesOrdinaryWordsAndPublicValuesAlone()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        const string text =
            "password authentication failed; Token refresh scheduled; secretary tokenizer passwordless keyboard-interactive\n"
            + "Basic authentication is disabled; basic settings saved\n"
            + "Could not load file or assembly 'Avalonia.Base, Culture=neutral, PublicKeyToken=c8d06bc3ff0f8fa3'\n"
            + "Password: \n";

        Assert.AreEqual(text, redactor.Redact(text));
        Assert.IsEmpty(redactor.Counts);
    }

    [TestMethod]
    public void Redactor_LeavesStackFrameSourceLocationsAlone()
    {
        // 崩溃记录的栈帧:文件名里带着关键字,冒号后面是行号 —— 原先换成了「PasswordAuthenticator.cs:<redacted> 87」
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        const string trace =
            "   at VelaShell.Ssh.Auth.PasswordAuthenticator.AuthenticateAsync() in /src/Ssh/Auth/PasswordAuthenticator.cs:line 87\n"
            + "   at VelaShell.Services.TokenStore.Load() in C:\\src\\VelaShell\\Services\\TokenStore.cs:line 12\n";

        Assert.AreEqual(trace, redactor.Redact(trace));
        Assert.IsEmpty(redactor.Counts);
        Assert.AreEqual("PasswordAuthenticator.cs:<redacted> TokenStore.cs=<redacted>",
            redactor.Redact("PasswordAuthenticator.cs:hunter2 TokenStore.cs=line"), "只有栈帧那一种写法不换");
    }

    [TestMethod]
    public void Redactor_HonoursEscapedQuotesAndEscapedJsonKeys()
    {
        // 值里转义过的引号不算收尾;JSON 字符串里再套一层 JSON(异常消息常见)时键名两边是 \"
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        Assert.AreEqual("{\"password\":\"<redacted>\"}", redactor.Redact("{\"password\":\"p\\\"ss123\"}"));
        Assert.AreEqual("failed: {\\\"client_secret\\\":\\\"<redacted>\\\"}",
            redactor.Redact("failed: {\\\"client_secret\\\":\\\"abc-123\\\"}"));
    }

    [TestMethod]
    public void Redactor_StaysLinearOnLongBlankRunsBeforeANumber()
    {
        // IPv4 的版本号豁免是往回看的:两段可变长的空白挨着时,一长串空白后面跟个数字就会平方级地回溯
        static TimeSpan Fastest(int blanks)
        {
            string text = new string(' ', blanks) + "10.0.0.1";
            TimeSpan best = TimeSpan.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                long start = Stopwatch.GetTimestamp();
                _ = new DiagnosticRedactor([], [], null, null, null).Redact(text);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed < best) best = elapsed;
            }
            return best;
        }

        TimeSpan small = Fastest(20_000);
        TimeSpan large = Fastest(80_000);

        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), small, $"20 000 个空白用了 {small}");
        Assert.IsLessThan(small * 8 + TimeSpan.FromMilliseconds(50), large, $"20 000 个用了 {small},80 000 个用了 {large}:应随长度线性增长");
    }

    [TestMethod]
    public void Redactor_StaysLinearOnLongRunsOfKeyLikeCharacters()
    {
        // 一长串全是「名字」里允许的字符、处处是关键字、末尾却没有分隔符:回溯式的写法在这里是平方乃至立方级
        static string Run(int length) => string.Concat(Enumerable.Repeat("password_token.", (length / 15) + 1))[..length];
        static TimeSpan Fastest(string text)
        {
            TimeSpan best = TimeSpan.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                long start = Stopwatch.GetTimestamp();
                _ = new DiagnosticRedactor([], [], null, null, null).Redact(text);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed < best) best = elapsed;
            }
            return best;
        }

        TimeSpan small = Fastest(Run(16 * 1024));
        TimeSpan large = Fastest(Run(64 * 1024));

        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), small, $"16 KB 用了 {small}");
        Assert.IsLessThan(small * 8 + TimeSpan.FromMilliseconds(50), large, $"16 KB 用了 {small},64 KB 用了 {large}:应随长度线性增长");
    }

    [TestMethod]
    public void Redactor_MasksIpsEndingASentenceAndIPv6ButNotVersionNumbers()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "Connection closed by 10.0.0.5. Then 10.0.0.6, done.\n"
            + "ipv6 2001:db8::1 and [fe80::1%en0]:22 again 2001:DB8:0:0:0:0:0:1 mapped ::ffff:203.0.113.7.\n"
            + "local ::1 [::1]:22 any :: clock 10:15:30 mac 00:1a:2b:3c:4d:5e\n"
            + "[Startup] VelaShell 0.0.1.0 pid=42\n"
            + "assembly 'Avalonia.Base, Version=12.1.4.0, Culture=neutral' plugin v2.1.0.3\n");

        Assert.Contains("Connection closed by <ip-1>. Then <ip-2>, done.\n", text, "句末的句号不挡住 IP");
        Assert.Contains("ipv6 <ip-4> and [<ip-5>%en0]:22 again <ip-4> mapped ::ffff:<ip-3>.\n", text,
            "同一个 IPv6 地址换种写法也是同一个占位符");
        Assert.Contains("local ::1 [::1]:22 any :: clock 10:15:30 mac 00:1a:2b:3c:4d:5e\n", text, "回环、未指定地址、时刻与 MAC 不动");
        Assert.Contains("[Startup] VelaShell 0.0.1.0 pid=42\n", text, "启动横幅里的程序集版本不是 IP");
        Assert.Contains("assembly 'Avalonia.Base, Version=12.1.4.0, Culture=neutral' plugin v2.1.0.3\n", text);
        Assert.AreEqual(6, redactor.Counts["ip"]);
    }

    [TestMethod]
    public void Redactor_CaseVariantsTheTokenTableCannotLookUpAreStillMasked()
    {
        // 正则的忽略大小写比字典宽:开尔文符号 K(U+212A)在正则里等于 k,OrdinalIgnoreCase 却不认 —— 原先这里抛 KeyNotFoundException
        var redactor = new DiagnosticRedactor(["kube-node1.example.com"], ["kevin"], null, null, null);

        string text = redactor.Redact("join \u212Aube-node1.example.com as \u212Aevin; again kube-node1.example.com as kevin");

        Assert.AreEqual("join <host-1> as <user-1>; again <host-1> as <user-1>", text);
    }

    [TestMethod]
    public async Task Bundle_ByDefaultCollectsOnlyTheMainLogsAndCrashReports()
    {
        // 会话日志(session-*.log)是终端原始输出,隐私说明里它是单独一类,永远不进包;传输记录、外部编辑日志、
        // vcxsrv.log 与按插件的日志满是路径与文件名,不勾就不收(维护者 2026-10-25 定)。目录里别的 .log 也不收
        await WriteLogsOfEveryKindAsync();
        string zipPath = Path.Combine(_root, "bundle.zip");

        DiagnosticBundleResult result = await DiagnosticBundle.WriteAsync(zipPath, EmptyRequest());

        Assert.AreEqual(2, result.LogFiles);
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.AreSequenceEqual(
            ["logs/velashell-20261019.log", "logs/crash-20261019-101010-000.txt", "summary.txt", "README.txt"], [.. zip.Entries.Select(entry => entry.FullName)], SequenceOrder.InAnyOrder);
        Assert.Contains("optional logs: (none)", Read(zip.GetEntry("README.txt")!));
    }

    [TestMethod]
    public async Task FindOptionalLogs_ListsEachKindThatHasFilesAndGroupsEachPluginsRotatedLog()
    {
        await WriteLogsOfEveryKindAsync();
        // 传输记录的目录可以改到别处:按设置里的那个目录找,日志目录里的旧记录不算
        string transfers = Path.Combine(_root, "transfer-logs");
        Directory.CreateDirectory(transfers);
        await File.WriteAllTextAsync(Path.Combine(transfers, "transfer-20261020.log"), "UPLOAD\n");
        await File.WriteAllTextAsync(Path.Combine(transfers, "transfer-20261021.log"), "DOWNLOAD\n");
        File.SetLastWriteTimeUtc(Path.Combine(transfers, "transfer-20261020.log"), DateTime.UtcNow.AddDays(-1));

        IReadOnlyList<DiagnosticLogOption> options = DiagnosticBundle.FindOptionalLogs(Logs, transfers);

        Assert.AreSequenceEqual(["transfer", "remote-edit", "vcxsrv", "plugin:velashell.ai", "plugin:velashell.redis"],
            [.. options.Select(option => option.Id)]);
        Assert.AreSequenceEqual([Path.Combine(transfers, "transfer-20261021.log"), Path.Combine(transfers, "transfer-20261020.log")],
            options[0].Files, "新的在前");
        DiagnosticLogOption ai = options.Single(option => option.Id == "plugin:velashell.ai");
        Assert.AreEqual(DiagnosticLogKind.Plugin, ai.Kind);
        Assert.AreEqual("velashell.ai", ai.PluginId);
        Assert.AreSequenceEqual(
            [Path.Combine(Logs, "plugins", "velashell.ai.log"), Path.Combine(Logs, "plugins", "velashell.ai.log.1")], [.. ai.Files], SequenceOrder.InAnyOrder, "轮转下来的那份归同一个插件");
        Assert.AreEqual(ai.Files.Sum(file => new FileInfo(file).Length), ai.Bytes);

        Assert.IsEmpty(DiagnosticBundle.FindOptionalLogs(Path.Combine(_root, "missing"), null), "目录不在就一类都没有");
    }

    /// <summary>
    /// id 只差一个 <c>.1</c> 的两个插件是两项:<c>acme.tool.1.log</c> 是插件 <c>acme.tool.1</c> 的当前日志,
    /// 不是 <c>acme.tool</c> 的轮转件(那个叫 <c>acme.tool.log.1</c>)。勾 <c>acme.tool</c> 不该把别人的日志一起收进包。
    /// </summary>
    [TestMethod]
    public async Task FindOptionalLogs_IdsOneDotOneApartAreTwoPlugins()
    {
        Directory.CreateDirectory(Path.Combine(Logs, "plugins"));
        await File.WriteAllTextAsync(Path.Combine(Logs, "plugins", "acme.tool.log"), "tool\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "plugins", "acme.tool.log.1"), "tool older\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "plugins", "acme.tool.1.log"), "tool.1\n");

        IReadOnlyList<DiagnosticLogOption> options = DiagnosticBundle.FindOptionalLogs(Logs, null);

        Assert.AreSequenceEqual(["plugin:acme.tool", "plugin:acme.tool.1"], [.. options.Select(option => option.Id)]);
        Assert.AreSequenceEqual(
            [Path.Combine(Logs, "plugins", "acme.tool.log"), Path.Combine(Logs, "plugins", "acme.tool.log.1")],
            [.. options[0].Files], SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([Path.Combine(Logs, "plugins", "acme.tool.1.log")], [.. options[1].Files]);
    }

    [TestMethod]
    public async Task Bundle_TickedLogsGoInMaskedAndUntickedOnesStayOut()
    {
        await WriteLogsOfEveryKindAsync();
        IReadOnlyList<DiagnosticLogOption> options = DiagnosticBundle.FindOptionalLogs(Logs, Logs);
        DiagnosticLogOption[] ticked = [.. options.Where(option => option.Id is "transfer" or "plugin:velashell.ai")];
        var request = new DiagnosticBundleRequest("v1", Logs, [], new DiagnosticRedactor([], [], null, null, null), ticked);
        string zipPath = Path.Combine(_root, "bundle.zip");

        DiagnosticBundleResult result = await DiagnosticBundle.WriteAsync(zipPath, request);

        Assert.AreEqual(5, result.LogFiles, "默认两份 + 传输记录一份 + 插件日志两份");
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.AreSequenceEqual(
            [
                "logs/velashell-20261019.log", "logs/crash-20261019-101010-000.txt", "logs/transfer-20261019.log",
                "logs/plugins/velashell.ai.log", "logs/plugins/velashell.ai.log.1", "summary.txt", "README.txt"
            ], [.. zip.Entries.Select(entry => entry.FullName)], SequenceOrder.InAnyOrder, "没勾的外部编辑日志、vcxsrv.log 与另一个插件的日志不进包");
        string transfer = Read(zip.GetEntry("logs/transfer-20261019.log")!);
        Assert.AreEqual("2026-10-19 10:10:10\tUPLOAD\tC:\\Users\\<user-1>\\Desktop\\app.tar.gz\t/home/<user-2>/app.tar.gz\tCOMPLETED\n", transfer,
            "勾上的照样遮盖:别人的家目录换掉名字那一段,文件名留着");
        string plugin = Read(zip.GetEntry("logs/plugins/velashell.ai.log")!);
        Assert.AreEqual("[Plugin:velashell.ai] calling https://<host-1>/v1 from <ip-1>\n", plugin,
            "插件 id 不是主机;插件连的私有地址照样遮");
        Assert.Contains("optional logs: transfer, plugin:velashell.ai", Read(zip.GetEntry("README.txt")!));
    }

    /// <summary>日志目录里每一类各放一份:默认那两类、会话日志、三类可选日志、两个插件的日志(一个带轮转),外加一份不认识的。</summary>
    private async Task WriteLogsOfEveryKindAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "[Startup] VelaShell\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "crash-20261019-101010-000.txt"), "UnhandledException\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "session-x.log"), "$ cat notes.txt\nprivate notes\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "session-db1-20261019-101010.log"), "$ psql\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "transfer-20261019.log"),
            "2026-10-19 10:10:10\tUPLOAD\tC:\\Users\\bob\\Desktop\\app.tar.gz\t/home/deploy/app.tar.gz\tCOMPLETED\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "remote-edit.log"), "[open] /home/deploy/.env\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "vcxsrv.log"), "winInitMultiWindowWM\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "other.log"), "someone else's log\n");
        Directory.CreateDirectory(Path.Combine(Logs, "plugins"));
        await File.WriteAllTextAsync(Path.Combine(Logs, "plugins", "velashell.ai.log"),
            "[Plugin:velashell.ai] calling https://llm.corp.example.com/v1 from 10.1.2.3\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "plugins", "velashell.ai.log.1"), "[Plugin:velashell.ai] older\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "plugins", "velashell.redis.log"), "[Plugin:velashell.redis] ping\n");
    }

    [TestMethod]
    public void Redactor_MasksBothHalvesOfTheDisconnectLineWhateverTheirShape()
    {
        // 断开日志行的格式是自己定的:单段的主机名、改过之前的旧主机、域账户式的用户名,不在已知来源里也换
        var redactor = new DiagnosticRedactor(["db1.example.net"], ["deploy"], null, null, null);
        string text = redactor.Redact(
            "10:00:00.000 [07] [SSH] deploy@db1.example.net:22 closed (connected since 2026-10-24 09:00:00 +08:00): closed locally\n"
            + "10:00:01.000 [07] [SSH] ops@old-db-3:2222 closed (connected since 2026-10-24 09:00:00 +08:00): KeepAliveTimeout(Open)\n"
            + "10:00:02.000 [07] [SSH] alice@corp@bastion:22 closed after 00:05:00: closed locally\n"
            + "10:00:03.000 [07] [SSH] root@[2001:db8::9]:22 closed (connected since 2026-10-24 09:00:00 +08:00): closed locally\n"
            + "10:00:04.000 [07] [SSH] jump-7:22 closed (connected since 2026-10-24 09:00:00 +08:00): closed locally\n"
            + "10:00:05.000 [07] [SSH] ops@10.0.0.5:22 closed (connected since 2026-10-24 09:00:00 +08:00): closed locally\n"
            + "10:00:06.000 [07] later old-db-3 again\n");

        Assert.Contains("[SSH] <user-1>@<host-1>:22 closed (connected since 2026-10-24 09:00:00 +08:00)", text, "已知的值用自己的占位符");
        Assert.Contains("[SSH] <user-2>@<host-2>:2222 closed", text);
        Assert.Contains("[SSH] <user-3>@<host-3>:22 closed after 00:05:00", text, "升级之前的旧写法也认;用户名取到最后一个 @");
        Assert.Contains("[SSH] root@[<ip-2>]:22 closed", text, "root 不换;IP 交给 IP 规则(IPv4 那一轮先编号)");
        Assert.Contains("[SSH] <host-4>:22 closed", text, "没有用户名时只有主机");
        Assert.Contains("[SSH] <user-2>@<ip-1>:22 closed", text, "同一个用户名同一个占位符");
        Assert.Contains("later old-db-3 again", text, "单段主机名离开那一行就认不出来了(隐私说明照实写了)");
        foreach (string value in new[] { "db1.example.net", "deploy@", "ops@", "alice", "bastion", "jump-7", "2001:db8::9", "10.0.0.5" })
        {
            Assert.DoesNotContain(value, text);
        }
    }

    [TestMethod]
    public void Redactor_MasksLowercaseDomainNamesThatNoKnownSourceListed()
    {
        // 漏遮:已知来源之外的主机 —— 连接改过之前的旧主机、异常消息里的主机、插件连的私有地址
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "No such host is known. (db-prod-3.corp.example.com:22)\n"
            + "proxy https://proxy.intranet.lan:3128/ failed; retry db-prod-3.corp.example.com.\n"
            + "resolved nas.local and gitlab.internal, then a.b.c.mycompany.cn\n");

        Assert.AreEqual(
            "No such host is known. (<host-1>:22)\n"
            + "proxy https://<host-2>:3128/ failed; retry <host-1>.\n"
            + "resolved <host-3> and <host-4>, then <host-5>\n", text);
        Assert.AreEqual(6, redactor.Counts["host"]);
    }

    [TestMethod]
    public void Redactor_LeavesTypeNamesFileNamesPluginIdsAndPublicServicesAlone()
    {
        // 误遮:大小写混写的类型名与程序集名、文件名(扩展名与顶级域撞车的那些不收)、插件 id、倒写的包标识、
        // VelaShell 自己联系的公共服务、看着像域名却以不认识的后缀结尾的
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        redactor.KeepNames(["velashell.ai"]);
        const string text =
            "at System.Net.Sockets.Socket.Connect() in VelaShell.Infrastructure.Ssh.SshBackend; using System.IO; Avalonia.Base\n"
            + "uploaded deploy.sh backup.zip notes.md main.rs script.py lib.so config.h.cc app.pro latest.json velashell-20261024.log\n"
            + "[Plugin:velashell.ai] ready; velashell.redis loaded; bundle com.velashell.app; org.freedesktop.portal.desktop\n"
            + "GET https://github.com/joesdu/VelaShell/releases/latest/download/latest.json via api.github.com, "
            + "objects.githubusercontent.com, feeds.easilynet.top\n"
            + "README.MD DB1.CORP.EXAMPLE.COM Example.Com x.community\n";

        Assert.AreEqual(text, redactor.Redact(text));
        Assert.IsEmpty(redactor.Counts);
    }

    [TestMethod]
    public void Redactor_MasksTheUserNameInHomeFoldersButKeepsTheRestOfThePath()
    {
        var redactor = new DiagnosticRedactor([], ["deploy"], @"C:\Users\Alice", "Alice", null);
        string text = redactor.Redact(
            @"open C:\Users\Alice\notes and C:\Users\John Smith\AppData\x and c:/users/bob/y and C:\Users\carol" + "\n"
            + "remote /home/deploy/app /home/eve/.ssh/config /var/home/frank/x /Users/grace/Library file:///home/eve/z\n"
            + @"skip C:\Users\Public\Documents C:\Users\Default\x /Users/Shared/y /home/ubuntu/z C:\Users\%USERNAME%\w /home/$USER/v" + "\n"
            + "urls https://example.org/home/about src/home/index /api/Users/42\n");

        Assert.AreEqual(
            @"open ~\notes and C:\Users\<user-2>\AppData\x and c:/users/<user-3>/y and C:\Users\<user-4>" + "\n"
            + "remote /home/<user-1>/app /home/<user-5>/.ssh/config /var/home/<user-6>/x /Users/<user-7>/Library file:///home/<user-5>/z\n"
            + @"skip C:\Users\Public\Documents C:\Users\Default\x /Users/Shared/y /home/ubuntu/z C:\Users\%USERNAME%\w /home/$USER/v" + "\n"
            + "urls https://<host-1>/home/about src/home/index /api/Users/42\n", text,
            "名字里可以有空格(后面紧跟着分隔符时);公共目录、常见登录名与占位写法不换;网址与相对路径里的 home 不是家目录");
    }

    [TestMethod]
    public void Redactor_TellsVersionNumbersFromIPv4ByWhatSurroundsThem()
    {
        // 光秃秃的 1.2.3.4 分不出是版本号还是 IP,宁可当 IP 遮;前后紧挨着字母、数字、下划线或更多一段数字的,
        // 以及 Version= / version: / VelaShell 后面的,都是版本号
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "bare 1.2.3.4 (5.6.7.8) host=9.9.9.9 [9.9.9.9]:22 to 9.9.9.9.\n"
            + "v1.2.3.4 V1.2.3.4 VelaShell_1.7.3.0_x64.msix pkg-1.2.3.4rc 1.2.3.4.5 10.1.2.3.4 Version=12.1.4.0 version: 2.0.0.1 "
            + "Version 3.1.0.0 VelaShell 0.0.1.0\n");

        Assert.AreEqual(
            "bare <ip-1> (<ip-2>) host=<ip-3> [<ip-3>]:22 to <ip-3>.\n"
            + "v1.2.3.4 V1.2.3.4 VelaShell_1.7.3.0_x64.msix pkg-1.2.3.4rc 1.2.3.4.5 10.1.2.3.4 Version=12.1.4.0 version: 2.0.0.1 "
            + "Version 3.1.0.0 VelaShell 0.0.1.0\n", text);
    }

    [TestMethod]
    public void Redactor_StaysLinearOnLongRunsTheNewRulesLookAt()
    {
        // 新加的三条规则各给一种最坏的输入:一长串点分的小写字母(末段永远不是顶级域)、
        // 用户目录后面一长串带空格却没有下一级分隔符的名字、断开日志行里一长串没有 @ 的用户名
        static TimeSpan Fastest(string text)
        {
            TimeSpan best = TimeSpan.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                long start = Stopwatch.GetTimestamp();
                _ = new DiagnosticRedactor([], [], null, null, null).Redact(text);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed < best) best = elapsed;
            }
            return best;
        }
        static string Inputs(int length) =>
            string.Concat(Enumerable.Repeat("ab.", length / 3)) + "1\n"
            + @"C:\Users\" + string.Concat(Enumerable.Repeat("a ", length / 2)) + "\n"
            + "[SSH] " + new string('u', length) + ":22 closed\n";

        TimeSpan small = Fastest(Inputs(16 * 1024));
        TimeSpan large = Fastest(Inputs(64 * 1024));

        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), small, $"16 KB 用了 {small}");
        Assert.IsLessThan(small * 8 + TimeSpan.FromMilliseconds(50), large, $"16 KB 用了 {small},64 KB 用了 {large}:应随长度线性增长");
    }

    [TestMethod]
    public async Task LogFiles_SkipsWhatNoLongerFitsAndKeepsCollectingOlderSmallerOnes()
    {
        // 从新到旧收:最新的一份单独就超过上限,跳过它;放不下的跳过,更旧、更小的照收 —— 崩溃记录不能被一份大日志挤掉
        string huge = Path.Combine(Logs, "velashell-huge.log");
        string big = Path.Combine(Logs, "velashell-big.log");
        string middle = Path.Combine(Logs, "velashell-middle.log");
        string crash = Path.Combine(Logs, "crash-20261016-101010-000.txt");
        await using (FileStream file = File.Create(huge)) file.SetLength(DiagnosticBundle.MaxLogBytes + 1);
        await using (FileStream file = File.Create(big)) file.SetLength(DiagnosticBundle.MaxLogBytes - 8);
        await File.WriteAllTextAsync(middle, "0123456789");
        await File.WriteAllTextAsync(crash, "crash");
        File.SetLastWriteTimeUtc(huge, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(big, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(crash, DateTime.UtcNow.AddDays(-3));

        Assert.AreSequenceEqual([big, crash], [.. DiagnosticBundle.LogFiles(Logs).Select(file => file.FullName)]);
    }

    [TestMethod]
    public async Task Bundle_FailureAfterTheTemporaryFileExistsLeavesNothingBehind()
    {
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "line\n");
        // 包的位置是个已有的目录:zip 整个写完、最后一步换名时才失败 —— 那时临时文件确实已经在了
        string zipPath = Path.Combine(_root, "bundle.zip");
        Directory.CreateDirectory(zipPath);

        Exception error = await Assert.ThrowsAsync<Exception>(() => DiagnosticBundle.WriteAsync(zipPath, EmptyRequest()));

        Assert.IsTrue(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.IsEmpty(Directory.GetFiles(_root, "*.partial"), "临时文件要删掉");
        Assert.IsEmpty(Directory.GetFileSystemEntries(zipPath));

        // 收日志收到一半被取消:同样不留下任何东西
        string cancelledPath = Path.Combine(_root, "cancelled.zip");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => DiagnosticBundle.WriteAsync(cancelledPath, EmptyRequest(), cancelled.Token));

        Assert.IsFalse(File.Exists(cancelledPath));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.partial"), "临时文件要删掉");
    }

    [TestMethod]
    public async Task Bundle_LeavesAFileAtTheOldFixedTemporaryNameAlone()
    {
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "line\n");
        string zipPath = Path.Combine(_root, "bundle.zip");
        // 临时文件名原先是固定的 <包>.partial:那里已有的文件会被截断、再被换名搬走
        string planted = zipPath + ".partial";
        await File.WriteAllTextAsync(planted, "someone else's file");

        await DiagnosticBundle.WriteAsync(zipPath, EmptyRequest());

        Assert.AreEqual("someone else's file", await File.ReadAllTextAsync(planted));
        Assert.AreSequenceEqual([planted], Directory.GetFiles(_root, "*.partial"), "自己的临时文件不留下");
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.IsNotNull(zip.GetEntry("logs/velashell-20261019.log"));
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task Bundle_DoesNotWriteThroughASymlinkPlantedAtTheOldFixedTemporaryName()
    {
        string victim = Path.Combine(_root, "victim.txt");
        await File.WriteAllTextAsync(victim, "keep me");
        string zipPath = Path.Combine(_root, "bundle.zip");
        File.CreateSymbolicLink(zipPath + ".partial", victim);

        await DiagnosticBundle.WriteAsync(zipPath, EmptyRequest());

        Assert.AreEqual("keep me", await File.ReadAllTextAsync(victim), "不能顺着链接把别的文件截断改写");
        Assert.IsNull(new FileInfo(zipPath).LinkTarget, "包是个普通文件,不是指向别处的链接");
    }

    private DiagnosticBundleRequest EmptyRequest() =>
        new("v1", Logs, [], new DiagnosticRedactor([], [], null, null, null));

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
