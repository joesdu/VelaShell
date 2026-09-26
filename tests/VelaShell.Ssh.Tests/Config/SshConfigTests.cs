// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH ssh_config(5)

using VelaShell.Ssh.Config;

namespace VelaShell.Ssh.Tests.Config;

[TestClass]
[TestCategory("Config")]
public sealed class SshConfigTests
{
    private static SshHostConfig Resolve(string content, string host) =>
        SshConfigFile.Resolve(SshConfigFile.Parse(content), host);

    [TestMethod]
    public void 基本的Host块()
    {
        SshHostConfig config = Resolve(
            """
            Host myserver
                HostName 10.0.0.9
                User joe
                Port 2222
            """,
            "myserver");

        Assert.AreEqual("10.0.0.9", config.HostName);
        Assert.AreEqual("joe", config.User);
        Assert.AreEqual(2222, config.Port);
    }

    [TestMethod]
    public void 没有HostName时用查询的名字()
    {
        SshHostConfig config = Resolve("Host x\n    User joe", "x");

        Assert.AreEqual("x", config.HostName);
        Assert.AreEqual(22, config.Port, "没写 Port 就是 22");
    }

    [TestMethod]
    public void 先出现的值赢()
    {
        // ⚠️ 这是 ssh_config 与**大多数配置格式相反**的地方。
        // 按「后来者覆盖」实现的话，Host * 里的兜底值会把前面的具体设置吃掉。
        SshHostConfig config = Resolve(
            """
            Host specific
                User 具体的

            Host *
                User 兜底的
                Port 2222
            """,
            "specific");

        Assert.AreEqual("具体的", config.User, "先出现的赢");
        Assert.AreEqual(2222, config.Port, "前面没写的才从兜底块里取");
    }

    [TestMethod]
    public void 文件开头的设置对所有主机生效()
    {
        SshHostConfig config = Resolve(
            """
            Compression yes

            Host a
                User joe
            """,
            "a");

        Assert.IsTrue(config.Compression);
        Assert.AreEqual("joe", config.User);
    }

    [TestMethod]
    public void 通配与取反()
    {
        string content =
            """
            Host *.internal !secret.internal
                User 内网用户

            Host *
                User 默认用户
            """;

        Assert.AreEqual("内网用户", Resolve(content, "web.internal").User);

        // 取反模式对上 → **整个块都不适用**，哪怕别的模式也对上了。
        Assert.AreEqual("默认用户", Resolve(content, "secret.internal").User);
        Assert.AreEqual("默认用户", Resolve(content, "example.com").User);
    }

    [TestMethod]
    public void IdentityFile可以出现多次且保序()
    {
        SshHostConfig config = Resolve(
            """
            Host x
                IdentityFile ~/.ssh/id_ed25519
                IdentityFile ~/.ssh/id_rsa
            """,
            "x");

        Assert.AreSequenceEqual(
            new[] { "~/.ssh/id_ed25519", "~/.ssh/id_rsa" }, [.. config.IdentityFiles], "顺序就是尝试顺序，不能重排");
    }

    [TestMethod]
    public void 键不区分大小写()
    {
        SshHostConfig config = Resolve("Host x\n    hostname 10.0.0.1\n    USER joe", "x");

        Assert.AreEqual("10.0.0.1", config.HostName);
        Assert.AreEqual("joe", config.User);
    }

    [TestMethod]
    public void 等号与引号都认()
    {
        SshHostConfig config = Resolve(
            """
            Host x
                HostName=10.0.0.1
                IdentityFile "C:\带 空格\id_ed25519"
            """,
            "x");

        Assert.AreEqual("10.0.0.1", config.HostName);
        Assert.AreSequenceEqual(new[] { @"C:\带 空格\id_ed25519" }, [.. config.IdentityFiles]);
    }

    [TestMethod]
    public void 注释被剥掉()
    {
        SshHostConfig config = Resolve(
            """
            # 整行注释
            Host x
                User joe   # 行尾注释
            """,
            "x");

        Assert.AreEqual("joe", config.User);
    }

    [TestMethod]
    public void 纯文本解析时Include被跳过而不是让文件不可用()
    {
        // Parse 是纯文本解析：没有基准目录，也不该去碰文件系统。
        // 但**跳过**而不是报错 —— 报错会让一份正常配置完全读不了。
        // 要展开 Include 就走 LoadAsync（见下面那几条）。
        SshHostConfig config = Resolve(
            """
            Include ~/.ssh/config.d/*

            Host x
                User joe
            """,
            "x");

        Assert.AreEqual("joe", config.User);
    }

    [TestMethod]
    public void Match_exec默认不执行任何命令()
    {
        // ⚠️ 这一条是安全断言，不是功能断言。
        //
        // `Match exec` 意味着**解析一份配置文件就能在本机跑任意程序**，
        // 而配置文件常常是从别处拷来的、同步过来的、别人给的。
        // 所以默认不执行 —— 带 exec 条件的块一律不匹配。
        SshHostConfig config = Resolve(
            """
            Match exec "echo 我被执行了"
                User 不该生效

            Host *
                User joe
            """,
            "x");

        Assert.AreEqual("joe", config.User, "没有求值器时 Match exec 块必须不匹配");
    }

    [TestMethod]
    public void Match_exec要执行必须由调用方显式传求值器()
    {
        // 传了才执行，而且执行什么由调用方说了算 ——
        // 「要不要跑外部命令」这个决定明确地落在调用方身上。
        List<string> asked = [];

        SshHostConfig config = SshConfigFile.Resolve(
            SshConfigFile.Parse(
                """
                Match exec "test -f /etc/special"
                    User 特殊用户

                Host *
                    User joe
                """),
            new SshConfigMatchContext
            {
                Host = "x",
                ExecEvaluator = command =>
                {
                    asked.Add(command);
                    return true;
                },
            });

        Assert.AreEqual("特殊用户", config.User);
        Assert.HasCount(1, asked, "求值器应当被问过一次");
    }

    [TestMethod]
    public void Match按用户与本机用户筛选()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse(
            """
            Match host prod-* user deploy
                IdentityFile ~/.ssh/deploy_key

            Host *
                IdentityFile ~/.ssh/id_ed25519
            """);

        // 主机与用户都对上 —— 用 deploy 那把。
        SshHostConfig matched = SshConfigFile.Resolve(blocks, new SshConfigMatchContext
        {
            Host = "prod-web-1",
            User = "deploy",
        });
        Assert.AreEqual("~/.ssh/deploy_key", matched.IdentityFiles[0]);

        // 用户不对 —— 那个块整个不适用（条件之间是与）。
        SshHostConfig otherUser = SshConfigFile.Resolve(blocks, new SshConfigMatchContext
        {
            Host = "prod-web-1",
            User = "joe",
        });
        Assert.AreEqual("~/.ssh/id_ed25519", otherUser.IdentityFiles[0]);

        // 连用户都不知道 —— **信息不足就不匹配**，不猜。
        SshHostConfig unknown = SshConfigFile.Resolve(blocks, SshConfigMatchContext.ForHost("prod-web-1"));
        Assert.AreEqual("~/.ssh/id_ed25519", unknown.IdentityFiles[0], "不知道用户时不该猜一个");
    }

    [TestMethod]
    public void Match支持取反与all()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse(
            """
            Match !host *.internal
                StrictHostKeyChecking yes

            Match all
                Port 2222
            """);

        SshHostConfig external = SshConfigFile.Resolve(blocks, SshConfigMatchContext.ForHost("example.com"));
        Assert.AreEqual("yes", external.StrictHostKeyChecking);
        Assert.AreEqual(2222, external.Port, "Match all 对所有主机生效");

        SshHostConfig internalHost = SshConfigFile.Resolve(
            blocks, SshConfigMatchContext.ForHost("db.internal"));
        Assert.IsNull(internalHost.StrictHostKeyChecking, "取反的条件对上了，那个块就不适用");
        Assert.AreEqual(2222, internalHost.Port);
    }

    [TestMethod]
    public void Match的canonical与final不匹配()
    {
        // 我们不做主机名规范化。静默当成 true 会让一份为规范化写的配置
        // 在我们这里产生完全不同的结果 —— 宁可不匹配。
        SshHostConfig config = Resolve(
            """
            Match canonical
                User 不该生效

            Host *
                User joe
            """,
            "x");

        Assert.AreEqual("joe", config.User);
    }

    [TestMethod]
    public async Task Include能把另一份配置就地展开()
    {
        string root = Path.Combine(Path.GetTempPath(), $"velashell-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "conf.d"));

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "conf.d", "10-work.conf"),
                """
                Host work
                    HostName work.example.com
                    User joe
                """);

            await File.WriteAllTextAsync(
                Path.Combine(root, "config"),
                """
                Include conf.d/*.conf

                Host *
                    Port 2200
                """);

            IReadOnlyList<SshConfigBlock> blocks =
                await SshConfigFile.LoadAsync(Path.Combine(root, "config"));

            SshHostConfig work = SshConfigFile.Resolve(blocks, "work");
            Assert.AreEqual("work.example.com", work.HostName, "被包含文件里的设置要生效");
            Assert.AreEqual("joe", work.User);
            Assert.AreEqual(2200, work.Port, "包含它的那份文件里的设置也要生效");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Include写在中间时就地展开_之后的设置排在被包含内容后面()
    {
        // ssh_config 是「先出现的值赢」，所以被包含的内容排在哪里决定了结果。
        // 曾经整份文件解完才把被包含的内容接在后面：写在 Include 之后的设置反倒赢了。
        string root = Path.Combine(Path.GetTempPath(), $"velashell-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "mid.conf"), """
                Host target
                    HostName mid.example.com
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "config"), """
                Host target
                    User before
                Include mid.conf
                Host target
                    HostName after.example.com
                """);

            SshHostConfig config = SshConfigFile.Resolve(
                await SshConfigFile.LoadAsync(Path.Combine(root, "config")), "target");

            Assert.AreEqual("mid.example.com", config.HostName, "被包含的内容排在 Include 那一行的位置上");
            Assert.AreEqual("before", config.User);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task 同一个文件在两个Host块里各包含一次都生效()
    {
        // 带条件的包含：Include 写在 Host 块里，被包含的设置只归这个块。
        // 同一个文件包含两次是正常写法 —— 环检测只该看当前这条包含链。
        string root = Path.Combine(Path.GetTempPath(), $"velashell-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "common.conf"), "Port 2022\n");
            await File.WriteAllTextAsync(Path.Combine(root, "config"), """
                Host a
                    Include common.conf
                Host b
                    Include common.conf
                """);

            IReadOnlyList<SshConfigBlock> blocks = await SshConfigFile.LoadAsync(Path.Combine(root, "config"));

            Assert.AreEqual(2022, SshConfigFile.Resolve(blocks, "a").Port);
            Assert.AreEqual(2022, SshConfigFile.Resolve(blocks, "b").Port, "第二次包含不能被当成环跳过");
            Assert.AreEqual(22, SshConfigFile.Resolve(blocks, "c").Port, "块外的主机不受它影响");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task 带条件的包含里新开的块仍受外层条件约束_包含之后回到外层块()
    {
        // 被包含的文件自己开了一个 Host 块。曾经：那个块对所有主机无条件生效（外层条件丢了），
        // 而 Include 之后、本属于 Host work 的设置落进了被包含文件的最后一个块 —— 同样对所有主机生效。
        string root = Path.Combine(Path.GetTempPath(), $"velashell-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "work.conf"), """
                Host *
                    ProxyJump bastion
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "config"), """
                Host work
                    Include work.conf
                    User after-include
                """);

            IReadOnlyList<SshConfigBlock> blocks = await SshConfigFile.LoadAsync(Path.Combine(root, "config"));

            SshHostConfig work = SshConfigFile.Resolve(blocks, "work");
            Assert.AreEqual("bastion", work.ProxyJump, "外层条件满足时，被包含文件里的块照常生效");
            Assert.AreEqual("after-include", work.User, "Include 之后的设置仍归 Include 所在的块");

            SshHostConfig other = SshConfigFile.Resolve(blocks, "other");
            Assert.IsNull(other.ProxyJump, "外层条件不满足时，被包含文件里的块不生效");
            Assert.IsNull(other.User, "Include 之后的设置不能落进被包含文件的块里");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Match取反遇上判不了的条件时整块不生效()
    {
        // 判不了（不执行 exec、不做规范化、不知道远端用户）曾经算成「不满足」，
        // 一个 ! 就把它变成了「满足」—— 写配置的人想排除的情形反而生效了。
        SshHostConfig config = Resolve(
            """
            Match !exec "test -f /etc/special"
                User 不该生效
            Match !canonical
                Port 2222
            Match !user root
                IdentityFile ~/.ssh/不该生效
            Host *
                User joe
            """,
            "x");

        Assert.AreEqual("joe", config.User);
        Assert.AreEqual(22, config.Port);
        Assert.IsEmpty(config.IdentityFiles);
    }

    [TestMethod]
    public void Match_host比的是HostName改写之后的名字()
    {
        // Match host 对的是真正要连的主机名；使用者输入的别名由 originalhost 与 Host 块去比。
        SshHostConfig config = Resolve(
            """
            Host alias
                HostName real.example.com
            Match host real.example.com
                User matched
            Match originalhost alias
                Port 2200
            Match host alias
                IdentityFile ~/.ssh/不该生效
            """,
            "alias");

        Assert.AreEqual("real.example.com", config.HostName);
        Assert.AreEqual("matched", config.User);
        Assert.AreEqual(2200, config.Port);
        Assert.IsEmpty(config.IdentityFiles);
    }

    [TestMethod]
    public async Task Include互相引用不会死循环()
    {
        // ⚠️ 这一条是**挂死防护**，不是功能。
        // `a` include `b`，`b` 又 include `a` —— 很容易写出来，
        // 而没有环检测的表现是「读配置的时候整个进程不动了」。
        string root = Path.Combine(Path.GetTempPath(), $"velashell-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "a.conf"),
                """
                Include b.conf

                Host a
                    User 甲
                """);

            await File.WriteAllTextAsync(
                Path.Combine(root, "b.conf"),
                """
                Include a.conf

                Host b
                    User 乙
                """);

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            IReadOnlyList<SshConfigBlock> blocks =
                await SshConfigFile.LoadAsync(Path.Combine(root, "a.conf"), cancellationToken: timeout.Token);

            // 两边的设置都要在，而且必须**返回**而不是转圈。
            Assert.AreEqual("甲", SshConfigFile.Resolve(blocks, "a").User);
            Assert.AreEqual("乙", SshConfigFile.Resolve(blocks, "b").User);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Include的通配结果是排好序的()
    {
        // 目录枚举的顺序在不同文件系统上不一样，而 ssh_config 是
        // **先出现的值赢** —— 顺序不定就意味着结果不定。
        string root = Path.Combine(Path.GetTempPath(), $"velashell-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "20-late.conf"), "Host x\n    User 后来的\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "10-early.conf"), "Host x\n    User 先来的\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "config"), "Include *.conf\n");

            IReadOnlyList<SshConfigBlock> blocks =
                await SshConfigFile.LoadAsync(Path.Combine(root, "config"));

            Assert.AreEqual(
                "先来的", SshConfigFile.Resolve(blocks, "x").User,
                "10- 要排在 20- 前面，而先出现的值赢");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void 其它常用项()
    {
        SshHostConfig config = Resolve(
            """
            Host jump-target
                ProxyJump bastion.example.com
                IdentitiesOnly yes
                ForwardAgent yes
                StrictHostKeyChecking accept-new
                UserKnownHostsFile ~/.ssh/known_hosts_work
                ServerAliveInterval 30
                ServerAliveCountMax 5
            """,
            "jump-target");

        Assert.AreEqual("bastion.example.com", config.ProxyJump);
        Assert.IsTrue(config.IdentitiesOnly);
        Assert.IsTrue(config.ForwardAgent);
        Assert.AreEqual("accept-new", config.StrictHostKeyChecking);
        Assert.AreEqual("~/.ssh/known_hosts_work", config.UserKnownHostsFile);
        Assert.AreEqual(30, config.ServerAliveInterval);
        Assert.AreEqual(5, config.ServerAliveCountMax);
    }

    [TestMethod]
    public void 一个Host行上可以有多个模式()
    {
        string content = "Host a b c\n    User joe";

        foreach (string host in new[] { "a", "b", "c" })
        {
            Assert.AreEqual("joe", Resolve(content, host).User, host);
        }

        Assert.IsNull(Resolve(content, "d").User);
    }

    [TestMethod]
    public async Task 文件不存在时返回空而不是抛()
    {
        IReadOnlyList<SshConfigBlock> blocks = await SshConfigFile.LoadAsync(
            Path.Combine(Path.GetTempPath(), $"不存在的-{Guid.NewGuid():N}"));

        // 第一次用的机器上它本来就不存在。那不是错误。
        Assert.IsEmpty(blocks);
    }
}
