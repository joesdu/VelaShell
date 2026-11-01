// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7.4(分词:引号、反斜杠与注释)
//   期望值是对 Win32-OpenSSH 9.5p2 `ssh -G` 的黑盒观察(那一节的表),不是照实现推出来的。

using System.Diagnostics;
using System.Text;
using VelaShell.Ssh.Config;

namespace VelaShell.Ssh.Tests.Config;

[TestClass]
[TestCategory("Config")]
public sealed class SshConfigQuotingTests
{
    public TestContext TestContext { get; set; } = null!;

    private static SshHostConfig Resolve(string content, string host = "t") =>
        SshConfigFile.Resolve(SshConfigFile.Parse(content), host);

    /// <summary>一行 <c>HostKeyAlias &lt;写法&gt;</c> 读成什么（单个参数、原样交出的键，与观察时用的一样）。</summary>
    private static string? Alias(string written) => Resolve($"Host t\n    HostKeyAlias {written}\n").First("HostKeyAlias");

    /// <summary>
    /// spec/09 §7.4 的反斜杠表：只有 <c>\\</c>、<c>\"</c>、<c>\'</c>（引号外再加 <c>\</c> + 空格）是转义，别的 <c>\</c> 原样留着；
    /// 两种引号规则相同；引号可以夹在词中间。
    /// </summary>
    [TestMethod]
    [DataRow(@"a\\b", @"a\b")]
    [DataRow(@"""a\\b""", @"a\b")]
    [DataRow(@"'a\\b'", @"a\b")]
    [DataRow(@"a\""b", @"a""b")]
    [DataRow(@"""a\""b""", @"a""b")]
    [DataRow(@"'a\""b'", @"a""b")]
    [DataRow(@"a\'b", @"a'b")]
    [DataRow(@"""a\'b""", @"a'b")]
    [DataRow(@"'a\'b'", @"a'b")]
    [DataRow(@"a\ b", @"a b")]
    [DataRow(@"""a\ b""", @"a\ b")]
    [DataRow(@"'a\ b'", @"a\ b")]
    [DataRow("\"a\\\tb\"", "a\\\tb")]
    [DataRow(@"a\xb", @"a\xb")]
    [DataRow(@"""a\xb""", @"a\xb")]
    [DataRow(@"'a\xb'", @"a\xb")]
    [DataRow(@"a\tb", @"a\tb")]
    [DataRow(@"a\nb", @"a\nb")]
    [DataRow(@"a\#b", @"a\#b")]
    [DataRow(@"a\=b", @"a\=b")]
    [DataRow(@"a\%b", @"a\%b")]
    [DataRow(@"中\文", @"中\文")]
    [DataRow(@"a\", @"a\")]
    [DataRow(@"\", @"\")]
    [DataRow(@"a\\\b", @"a\\b")]
    [DataRow(@"a\\\\b", @"a\\b")]
    [DataRow(@"'\\\\'", @"\\")]
    [DataRow(@"""a\\""", @"a\")]
    [DataRow(@"'a""b'", @"a""b")]
    [DataRow(@"""a'b""", @"a'b")]
    [DataRow(@"a""b c""d", @"ab cd")]
    [DataRow(@"""a""'b'", @"ab")]
    [DataRow(@"a\""b""c d""", @"a""bc d")]
    [DataRow(@"""=a""", @"=a")]
    public void 反斜杠与引号照OpenSSH的读法(string written, string expected) =>
        Assert.AreEqual(expected, Alias(written), written);

    /// <summary>spec/09 §7.4「Windows 路径因此这样读」：普通路径原样；单个 <c>\\</c> 开头的 UNC 与命名管道被读成一个 <c>\</c>，与 <c>ssh</c> 一样。</summary>
    [TestMethod]
    [DataRow(@"IdentityFile C:\Users\me\.ssh\id_ed25519", "IdentityFile", @"C:\Users\me\.ssh\id_ed25519")]
    [DataRow(@"IdentityFile ""C:\Program Files\k""", "IdentityFile", @"C:\Program Files\k")]
    [DataRow(@"IdentityFile C:\Program\ Files\k", "IdentityFile", @"C:\Program Files\k")]
    [DataRow(@"IdentityFile \\server\share\k", "IdentityFile", @"\server\share\k")]
    [DataRow(@"IdentityFile \\\\server\share\k", "IdentityFile", @"\\server\share\k")]
    [DataRow(@"IdentityFile ""\\\\server\share\k""", "IdentityFile", @"\\server\share\k")]
    [DataRow(@"IdentityFile \\\\server\\share\\k", "IdentityFile", @"\\server\share\k")]
    [DataRow(@"IdentityAgent \\.\pipe\openssh-ssh-agent", "IdentityAgent", @"\.\pipe\openssh-ssh-agent")]
    [DataRow(@"IdentityAgent \\\\.\pipe\openssh-ssh-agent", "IdentityAgent", @"\\.\pipe\openssh-ssh-agent")]
    [DataRow(@"IdentityAgent ""\\\\.\\pipe\\my agent""", "IdentityAgent", @"\\.\pipe\my agent")]
    [DataRow(@"IdentityFile C:\dir\", "IdentityFile", @"C:\dir\")]
    [DataRow(@"IdentityFile ""C:\dir\\""", "IdentityFile", @"C:\dir\")]
    public void Windows路径与OpenSSH读得一样(string line, string key, string expected) =>
        Assert.AreEqual(expected, Resolve($"Host t\n    {line}\n").First(key), line);

    /// <summary>
    /// 不在引号里、落在词开头的 <c>#</c> 才开始注释：词中间的、引号里的、跟在 <c>\</c> 或转义空格后面的都是值。
    /// 〔历史〕曾经只看「前面是不是空白」：<c>a\ #b</c> 被截成 <c>a\</c>，<c>'a #b'</c> 被截成 <c>'a</c>。
    /// </summary>
    [TestMethod]
    [DataRow("a #b", "a")]
    [DataRow("a#b", "a#b")]
    [DataRow("\"a\"#b", "a#b")]
    [DataRow(@"a\ #b", "a #b")]
    [DataRow("'a #b'", "a #b")]
    [DataRow("'#a'", "#a")]
    [DataRow(@"\#a", @"\#a")]
    [DataRow("\"a\"   #c", "a")]
    public void 注释只从词开头的井号算起(string written, string expected) =>
        Assert.AreEqual(expected, Alias(written), written);

    /// <summary>关键字与值之间：空白或至多一个 <c>=</c>；关键字也可以带引号。</summary>
    [TestMethod]
    [DataRow("HostKeyAlias=a", "a")]
    [DataRow("HostKeyAlias = a", "a")]
    [DataRow("HostKeyAlias==a", "=a")]
    [DataRow("HostKeyAlias\ta", "a")]
    [DataRow("\"HostKeyAlias\" a", "a")]
    [DataRow("HostKeyAlias=\"a b\"", "a b")]
    public void 关键字之后至多一个等号(string line, string expected) =>
        Assert.AreEqual(expected, Resolve($"Host t\n    {line}\n").First("HostKeyAlias"), line);

    /// <summary>行尾的 <c>\</c> 不续行；CRLF 与 LF 读法相同（行尾空白先去掉，<c>a\ </c> 于是是 <c>a\</c>）。</summary>
    [TestMethod]
    public void 行尾反斜杠不续行_CRLF与LF一样()
    {
        foreach (string newline in new[] { "\n", "\r\n" })
        {
            SshHostConfig config = Resolve(string.Join(newline, "Host t", @"    HostKeyAlias a\", "    User bob", @"    IdentityFile C:\k\  ", ""));
            Assert.AreEqual(@"a\", config.HostKeyAlias, newline);
            Assert.AreEqual("bob", config.User, $"{newline}:下一行照常是新的一行");
            Assert.AreSequenceEqual([@"C:\k\"], [.. config.IdentityFiles], newline);
        }
    }

    /// <summary>
    /// 引号没配对的那一行不算（OpenSSH 整份配置不读，我们只丢这一行）；<c>Host</c> / <c>Match</c> 行没配对时开一个哪台主机都配不上的块 ——
    /// 只跳过那一行的话，它下面的设置会落进上一个块（这里是 <c>Host *</c>），对所有主机生效。
    /// </summary>
    [TestMethod]
    public void 引号没配对的那一行不算_Host行没配对时下面的设置不漏到别的块()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse(
            """
            Host *
                User everyone
            Host "broken
                User leaked
            Host t
                HostKeyAlias "C:\dir\"
                IdentityFile ~/.ssh/ok
            Match host "t
                Port 2200
            """);

        SshHostConfig t = SshConfigFile.Resolve(blocks, "t");
        Assert.AreEqual("everyone", t.User, "没配对的 Host 行下面那个 User 不能漏进 Host *");
        Assert.IsNull(t.HostKeyAlias, "末尾的 \\\" 是转义的引号，引号没关上，这一行不算");
        Assert.AreSequenceEqual(["~/.ssh/ok"], [.. t.IdentityFiles], "同一个块里别的行照常");
        Assert.AreEqual(22, t.Port, "没配对的 Match 行不生效");
        Assert.AreEqual("everyone", SshConfigFile.Resolve(blocks, "broken").User);
    }

    /// <summary><c>ProxyCommand</c> / <c>RemoteCommand</c> / <c>LocalCommand</c> / <c>KnownHostsCommand</c> 是到行尾的原文：引号、反斜杠、<c>#</c> 原样（交给 shell）。</summary>
    [TestMethod]
    public void 命令键是到行尾的原文()
    {
        SshHostConfig config = Resolve(
            """
            Host t
                ProxyCommand nc "a b" \\x \"y\" 'z' %h   #c
                RemoteCommand=echo "a b" \\x #z
                LocalCommand  a\\b "c"
                KnownHostsCommand a\\b "c" #d
                User "q r"
            """);

        Assert.AreEqual(@"nc ""a b"" \\x \""y\"" 'z' %h   #c", config.ProxyCommand);
        Assert.AreEqual(@"echo ""a b"" \\x #z", config.First("RemoteCommand"));
        Assert.AreEqual(@"a\\b ""c""", config.First("LocalCommand"));
        Assert.AreEqual(@"a\\b ""c"" #d", config.First("KnownHostsCommand"));
        Assert.AreEqual("q r", config.User, "别的键照常分词");
    }

    /// <summary>
    /// 带引号的 <c>Host</c> 模式与多个参数的键：引号里的空格不分词。
    /// 〔历史〕曾经 <c>Host x "t"</c> 的第二个模式带着引号；<c>UserKnownHostsFile "C:\a b\kh"</c> 只剩 <c>C:\a</c>；<c>SetEnv A="x y"</c> 的值被空格切断。
    /// </summary>
    [TestMethod]
    public void Host模式与多参数的键按词取()
    {
        SshHostConfig config = Resolve(
            """
            Host x "t" 'my host'
                UserKnownHostsFile "C:\a b\kh" C:\kh2
                GlobalKnownHostsFile 'C:\Program Files\kh' C:\kh3
                SetEnv A="x y" B=\\z C='q r'
                SendEnv "LC_*" -X
                PermitRemoteOpen "db:5432"  127.0.0.1:*
            """);

        Assert.AreEqual(@"C:\a b\kh C:\kh2", config.UserKnownHostsFile, "First:各参数以一个空格连接");
        Assert.AreSequenceEqual([@"C:\a b\kh", @"C:\kh2"], [.. config.Arguments("UserKnownHostsFile")]);
        Assert.AreSequenceEqual([@"C:\Program Files\kh", @"C:\kh3"], [.. config.GlobalKnownHostsFiles]);
        IReadOnlyDictionary<string, string> environment = config.SessionEnvironment();
        Assert.AreEqual("x y", environment["A"]);
        Assert.AreEqual(@"\z", environment["B"]);
        Assert.AreEqual("q r", environment["C"]);
        Assert.IsTrue(config.GetPermitRemoteOpen().Permits("db", 5432));
        Assert.AreEqual("x y", SshConfigFile.Resolve(SshConfigFile.Parse("Host \"my host\"\n    SetEnv A=\"x y\"\n"), "my host").SessionEnvironment()["A"],
            "带空格的 Host 模式是一个模式");
    }

    /// <summary>转发的两头同样照 §7.4 分词：带空格的套接字路径加引号就是一头；<c>onInvalid</c> 收下写错的那一行、其余照解。</summary>
    [TestMethod]
    public void 转发的两头按词取_写错的行交给onInvalid()
    {
        SshHostConfig config = Resolve(
            """
            Host t
                LocalForward "/tmp/my dir/a.sock" /var/run/docker.sock
                LocalForward 8443
                DynamicForward 1080
            """);

        List<string> invalid = [];
        IReadOnlyList<SshConfigForward> forwards = config.GetForwards(onInvalid: (line, _) => invalid.Add(line));

        Assert.HasCount(2, forwards);
        Assert.AreEqual("/tmp/my dir/a.sock", forwards[0].ListenSocketPath);
        Assert.AreEqual("/var/run/docker.sock", forwards[0].TargetSocketPath);
        Assert.AreEqual(@"""/tmp/my dir/a.sock"" /var/run/docker.sock", forwards[0].Line, "Line 是配置里的原文");
        Assert.AreEqual(1080, forwards[1].ListenPort);
        Assert.AreSequenceEqual(["LocalForward 8443"], invalid);
        Assert.ThrowsExactly<Diagnostics.SshConnectException>(() => config.GetForwards(), "不给 onInvalid 时写错一条就抛");
    }

    /// <summary>
    /// 转发里套接字那一头可以整个放进方括号，方括号里的原样是路径（spec/09 §7.4 的观察：路径里有 <c>:</c> 时 ssh 只认这种写法）；
    /// 方括号里没有 <c>/</c> 的照旧（IPv6 的 <c>[::1]:8080</c>）。
    /// </summary>
    [TestMethod]
    public void 转发的一头整个放进方括号是方括号里的路径()
    {
        SshHostConfig config = Resolve(
            """
            Host t
                LocalForward [C:/x.sock] /var/run/r.sock
                LocalForward "[C:/my dir/x.sock]" [/run/a:b.sock]
                LocalForward [::1]:8080 db:5432
                RemoteForward [/run/r:x.sock] [C:/Users/me/app.sock]
            """);

        IReadOnlyList<SshConfigForward> forwards = config.GetForwards();
        Assert.HasCount(4, forwards);
        Assert.AreEqual("C:/x.sock", forwards[0].ListenSocketPath);
        Assert.AreEqual("/var/run/r.sock", forwards[0].TargetSocketPath);
        Assert.AreEqual("C:/my dir/x.sock", forwards[1].ListenSocketPath);
        Assert.AreEqual("/run/a:b.sock", forwards[1].TargetSocketPath);
        Assert.AreEqual(("::1", 8080), (forwards[2].ListenAddress, forwards[2].ListenPort));
        Assert.IsNull(forwards[2].ListenSocketPath, "方括号里没有 / 的照旧是 IPv6");
        Assert.AreEqual("/run/r:x.sock", forwards[3].ListenSocketPath);
        Assert.AreEqual("C:/Users/me/app.sock", forwards[3].TargetSocketPath);
        Assert.AreEqual("[C:/x.sock] /var/run/r.sock", forwards[0].Line, "Line 还是原文");
    }

    /// <summary>
    /// 引号没配对而跳过的行交给 <c>onSkippedLine</c>（文件、行号、原文），<c>Include</c> 进来的文件里的、没配对的 <c>Include</c> 行本身也报 ——
    /// ssh 读到这样的行整份配置都不读，调用方要告诉使用者。
    /// </summary>
    [TestMethod]
    public async Task 引号没配对而跳过的行交给onSkippedLine()
    {
        string root = Path.Combine(Path.GetTempPath(), $"velashell-skipped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string included = Path.Combine(root, "extra.conf");
            await File.WriteAllTextAsync(included, "Host b\n    User \"bob\n    Port 2201\n", TestContext.CancellationToken);
            string main = Path.Combine(root, "config");
            await File.WriteAllTextAsync(main,
                $"Include \"{included.Replace('\\', '/')}\"\n" +
                "Include \"broken\n" +
                "Host a\n    HostKeyAlias \"C:\\dir\\\"\n    User alice\n" +
                "Host \"c\n    User carol\n",
                TestContext.CancellationToken);

            List<SshConfigSkippedLine> skipped = [];
            IReadOnlyList<SshConfigBlock> blocks = await SshConfigFile.LoadAsync(main, onSkippedLine: skipped.Add,
                cancellationToken: TestContext.CancellationToken);

            Assert.AreSequenceEqual(
                [
                    new SshConfigSkippedLine(Path.GetFullPath(included), 2, @"User ""bob"),
                    new SshConfigSkippedLine(Path.GetFullPath(main), 2, @"Include ""broken"),
                    new SshConfigSkippedLine(Path.GetFullPath(main), 4, @"HostKeyAlias ""C:\dir\"""),
                    new SshConfigSkippedLine(Path.GetFullPath(main), 6, @"Host ""c"),
                ],
                [.. skipped]);
            Assert.AreEqual("alice", SshConfigFile.Resolve(blocks, "a").User, "同一个块里别的行照常");
            Assert.AreEqual(2201, SshConfigFile.Resolve(blocks, "b").Port);
            Assert.IsEmpty(SshConfigFile.Parse(await File.ReadAllTextAsync(main, TestContext.CancellationToken)).Where(b => b.Patterns.Contains("c")), "纯文本解析不报，也不开 c 的块");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    /// <summary>
    /// 写出（<see cref="SshConfigFile.TryFormatArgument"/>）：能不改的地方不改；本库读回来与原值一致。
    /// 与 <c>ssh -G</c> 的核对在 <see cref="写出的参数ssh读回来也是原值"/>。
    /// </summary>
    [TestMethod]
    [DataRow(@"C:\Users\me\.ssh\id_ed25519", @"C:\Users\me\.ssh\id_ed25519")]
    [DataRow(@"\\.\pipe\openssh-ssh-agent", @"\\\\.\pipe\openssh-ssh-agent")]
    [DataRow(@"\\server\share\keys\id", @"\\\\server\share\keys\id")]
    [DataRow(@"C:\Program Files\VelaShell\id", @"""C:\Program Files\VelaShell\id""")]
    [DataRow(@"C:\my dir\", @"""C:\my dir\\""")]
    [DataRow(@"C:\dir\", @"C:\dir\")]
    [DataRow(@"~/.ssh/id_ed25519", @"~/.ssh/id_ed25519")]
    [DataRow(@"a""b", @"a\""b")]
    [DataRow(@"it's", @"it\'s")]
    [DataRow(@"x\""y", @"x\\\""y")]
    [DataRow(@"id_#work", @"""id_#work""")]
    [DataRow(@"=a", @"""=a""")]
    [DataRow("", @"""""")]
    [DataRow("a\tb", "\"a\tb\"")]
    public void 写出的参数本库读回来是原值(string value, string expected)
    {
        Assert.IsTrue(SshConfigFile.TryFormatArgument(value, out string? text), value);
        Assert.AreEqual(expected, text, value);
        Assert.AreEqual(value, Alias(text), $"{value} 写成 {text}");
        Assert.AreEqual(value, Resolve($"Host t\n    IdentityAgent {text}\n").First("IdentityAgent"), $"{value}(IdentityAgent)");
    }

    /// <summary>换行与 NUL 写不进 ssh_config 的一行（硬写的话下一行就成了另一条设置）。</summary>
    [TestMethod]
    [DataRow("joe\nProxyCommand calc")]
    [DataRow("joe\r")]
    [DataRow("a\0b")]
    public void 带换行或NUL的值写不出来(string value)
    {
        Assert.IsFalse(SshConfigFile.TryFormatArgument(value, out string? text));
        Assert.IsNull(text);
    }

    /// <summary>写出来要两头都认的一组值（spec/09 §7.4 末那条「逐个核对过」）。</summary>
    private static readonly string[] RoundTripValues =
    [
        @"\\.\pipe\openssh-ssh-agent",
        @"\\.\pipe\vela agent",
        @"\\server\share\keys\id_ed25519",
        @"\\server\share with space\k",
        @"C:\Users\me\.ssh\id_ed25519",
        @"C:\Program Files\VelaShell\id",
        @"C:\my dir\",
        @"C:\dir\",
        @"C:\a\\b",
        @"/home/me/.ssh/id_ed25519",   // 不放 ~ 开头的：IdentityAgent 在 ssh -G 里会把 ~ 展开
        @"/home/me/My Keys/id",
        @"a""b",
        @"it's",
        @"x\""y",
        @"x\'y'",
        @"id_#work",
        @"#lead",
        @"=a",
        "a\tb",
        @"\\\",
    ];

    /// <summary>
    /// 〔互操作〕<see cref="SshConfigFile.TryFormatArgument"/> 写出的值，本机 OpenSSH 的 <c>ssh -G</c> 读回来也是原值（spec/09 §7.4 是对
    /// Win32-OpenSSH 9.5p2 的观察）。本机没有 <c>ssh</c> 时判不了（Inconclusive）。
    /// </summary>
    [TestMethod]
    [TestCategory("Interop")]
    public async Task 写出的参数ssh读回来也是原值()
    {
        string? ssh = FindSsh();
        if (ssh is null)
        {
            TestContext.WriteLine("[SKIP] 本机没有 ssh，判不了。");
            Assert.Inconclusive("本机没有 ssh。");
        }

        string directory = Path.Combine(Path.GetTempPath(), $"velashell-sshg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            TestContext.WriteLine($"ssh -V:{await RunAsync(ssh, ["-V"])}");
            // HostKeyAlias 不用：ssh -G 把它转成小写再打印。这两个键原样打印。
            foreach (string key in new[] { "IdentityFile", "IdentityAgent" })
            {
                foreach (string value in RoundTripValues)
                {
                    Assert.IsTrue(SshConfigFile.TryFormatArgument(value, out string? text), value);
                    string path = Path.Combine(directory, "config");
                    await File.WriteAllTextAsync(path, $"Host t\n    {key} {text}\n", new UTF8Encoding(false), TestContext.CancellationToken);

                    string output = await RunAsync(ssh, ["-G", "-F", path, "t"]);
                    string prefix = key.ToLowerInvariant() + " ";
                    string? line = output.Split('\n').Select(static l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
                    Assert.AreEqual(value, line?[prefix.Length..], $"{key} {text}:ssh -G 读成「{line}」\n{output}");
                    Assert.AreEqual(value, SshConfigFile.Resolve(await SshConfigFile.LoadAsync(path, cancellationToken: TestContext.CancellationToken), "t").First(key), $"{key} {text}:本库");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? FindSsh()
    {
        if (OperatingSystem.IsWindows())
        {
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            if (File.Exists(system))
            {
                return system;
            }
        }
        string name = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private async Task<string> RunAsync(string program, string[] arguments)
    {
        ProcessStartInfo start = new(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        return await stdout + await stderr;
    }
}
