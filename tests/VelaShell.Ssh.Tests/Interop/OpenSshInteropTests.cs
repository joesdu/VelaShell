// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/design/architecture.md §10.3（互操作矩阵）
//
// ⚠️ 这一组**要连一台真的 sshd**，所以标了 [TestCategory("Interop")]，
//    不进 PR 门禁。没有配好环境时它们会**跳过**而不是失败 ——
//    一个在本机永远红着的测试，很快就会被所有人忽略。
//
// 本机跑法（需要 docker）：
//   pwsh scripts/ssh/interop/Start-TestServer.ps1
//   dotnet test --filter "TestCategory=Interop"
//   pwsh scripts/ssh/interop/Stop-TestServer.ps1
//
// 环境变量：
//   VELASHELL_SSH_INTEROP_HOST      默认 127.0.0.1
//   VELASHELL_SSH_INTEROP_PORT      默认 2222
//   VELASHELL_SSH_INTEROP_USER      默认 velashell
//   VELASHELL_SSH_INTEROP_PASSWORD  默认 velashell
//   VELASHELL_SSH_INTEROP_KEY       可选：私钥文件路径

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Tests.Interop;

[TestClass]
[TestCategory("Interop")]
// ⚠️ **不并行。** 13 条用例同时连一台真的 sshd 会撞上 OpenSSH 的 MaxStartups
// （默认 10 个未认证并发连接），多出来的会在**发出版本标识串之前**就被丢掉 ——
// 症状是「对端在发出版本标识串之前关闭了连接」，看上去像我们的 bug。
[DoNotParallelize]
public sealed class OpenSshInteropTests
{
    private static string Host =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_HOST") ?? "127.0.0.1";

    private static int Port =>
        int.TryParse(
            Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_PORT"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            ? port
            : 2222;

    private static string User =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_USER") ?? "velashell";

    private static string Password =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_PASSWORD") ?? "velashell";

    private static string? KeyPath =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY");

    /// <summary>环境没配好就跳过，而不是失败。</summary>
    private static void RequireServer()
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP") != "1")
        {
            Assert.Inconclusive(
                "互操作用例需要一台真的 sshd。设 VELASHELL_SSH_INTEROP=1 并起好服务端再跑" +
                "（见 scripts/ssh/interop/Start-TestServer.ps1）。");
        }
    }

    private static SshConnectionOptions Options(
        SshAlgorithmSet? algorithms = null, IReadOnlyList<SshCredential>? credentials = null) =>
        new(User, Host, Port)
        {
            // 互操作测试里服务端每次重建，主机密钥每次都变 —— 这里不是在测 TOFU。
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = credentials ?? [new PasswordCredential(Password)],
            Algorithms = algorithms ?? SshAlgorithmSet.Default,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

    // ------------------------------------------------------------ 主线

    [TestMethod]
    public async Task 能连上真实的OpenSSH并跑命令()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();
        SshCommandOutput result = await connection.RunAsync("echo 你好 && uname -s");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains("你好", result.StandardOutput);
        Assert.IsNotNull(connection.HostKey);
    }

    [TestMethod]
    public async Task 每一种密钥交换算法都能与OpenSSH握手()
    {
        RequireServer();

        List<string> failed = [];

        foreach (string kex in SshAlgorithmSet.Default.KeyExchange)
        {
            if (SshAlgorithmNegotiator.IsIndicator(kex))
            {
                continue;
            }

            SshAlgorithmSet only = SshAlgorithmSet.Default with { KeyExchange = [kex] };

            try
            {
                await using SshConnection connection = await Options(only).ConnectAsync();
                SshCommandOutput r = await connection.RunAsync("true");
                Assert.AreEqual(0, r.ExitCode, kex);
            }
            catch (SshException ex)
            {
                // 对端不支持某个算法是**正常的**（比如老 OpenSSH 没有后量子混合）。
                // 记下来一起报，别一个失败就中断整张矩阵。
                failed.Add($"{kex}：{ex.Message}");
            }
        }

        // 至少要有一个能通 —— 一个都不通说明不是「对端不支持」，是我们坏了。
        Assert.IsLessThan(
            SshAlgorithmSet.Default.KeyExchange.Count, failed.Count,
            "没有任何一种密钥交换能与对端握手：" + Environment.NewLine + string.Join(Environment.NewLine, failed));

        Console.WriteLine(failed.Count == 0
            ? "全部密钥交换算法都通过了。"
            : "对端不支持这些（可能是正常的）：" + Environment.NewLine + string.Join(Environment.NewLine, failed));
    }

    [TestMethod]
    public async Task 每一种加密算法都能与OpenSSH收发()
    {
        RequireServer();

        List<string> failed = [];

        foreach (string cipher in SshAlgorithmSet.Default.EncryptionClientToServer)
        {
            SshAlgorithmSet only = SshAlgorithmSet.Default with
            {
                EncryptionClientToServer = [cipher],
                EncryptionServerToClient = [cipher],
            };

            try
            {
                await using SshConnection connection = await Options(only).ConnectAsync();
                SshCommandOutput r = await connection.RunAsync("echo ok");
                Assert.AreEqual("ok\n", r.StandardOutput, cipher);
            }
            catch (SshException ex)
            {
                failed.Add($"{cipher}：{ex.Message}");
            }
        }

        Assert.IsLessThan(
            SshAlgorithmSet.Default.EncryptionClientToServer.Count, failed.Count,
            "没有任何一种加密算法能与对端收发：" + Environment.NewLine + string.Join(Environment.NewLine, failed));
    }

    [TestMethod]
    public async Task 公钥认证能与OpenSSH对上()
    {
        RequireServer();

        if (string.IsNullOrEmpty(KeyPath) || !File.Exists(KeyPath))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_KEY。");
        }

        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(KeyPath);

        await using SshConnection connection =
            await Options(credentials: [new PublicKeyCredential(signer, KeyPath)]).ConnectAsync();

        SshCommandOutput result = await connection.RunAsync("id -un");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(User, result.StandardOutput);
    }

    /// <summary>
    /// **加密的** OpenSSH 私钥能连上真实 OpenSSH。
    /// </summary>
    /// <remarks>
    /// 这条验的是 <c>bcrypt_pbkdf</c> 那一整条链路，而它只有拿真 <c>ssh-keygen</c>
    /// 写出来的文件才验得了：加密侧要是也由我们写，两边会一起错，
    /// 自加密自解密照样通过，拿到别人的钥才静默失败。
    /// 单元测试那边用的是提交在仓库里的样本，这里用的是**刚刚现生成**的那把 ——
    /// 覆盖的是「这台机器上这一版 ssh-keygen 的默认产物」。
    /// </remarks>
    [TestMethod]
    public async Task 加密的私钥能与OpenSSH对上()
    {
        RequireServer();

        string? encrypted = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY_ENCRYPTED");
        string? passphrase = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY_PASSPHRASE");

        if (string.IsNullOrEmpty(encrypted) || !File.Exists(encrypted) || string.IsNullOrEmpty(passphrase))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_KEY_ENCRYPTED / _PASSPHRASE。");
        }

        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(encrypted, passphrase);

        await using SshConnection connection =
            await Options(credentials: [new PublicKeyCredential(signer, encrypted)]).ConnectAsync();

        SshCommandOutput result = await connection.RunAsync("id -un");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(User, result.StandardOutput);
    }

    /// <summary>口令错了要报口令错，而不是连到一半才失败。</summary>
    [TestMethod]
    public async Task 加密私钥口令错时在本地就报出来()
    {
        RequireServer();

        string? encrypted = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY_ENCRYPTED");
        if (string.IsNullOrEmpty(encrypted) || !File.Exists(encrypted))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_KEY_ENCRYPTED。");
        }

        SshPrivateKeyException ex = await Assert.ThrowsExactlyAsync<SshPrivateKeyException>(
            async () => await SshPrivateKeyFile.LoadAsync(encrypted, "definitely-not-it"));

        Assert.IsTrue(ex.NeedsPassphrase);
    }

    /// <summary>
    /// 用 OpenSSH 证书认证连上真实 OpenSSH（服务端配了 <c>TrustedUserCAKeys</c>）。
    /// </summary>
    /// <remarks>
    /// 证书认证有一处不对称，只有真服务端才验得出来：认证请求里那个「公钥算法名」
    /// 字段带 <c>-cert-v01@openssh.com</c> 后缀，而签名 blob 里写的是**普通**算法名。
    /// 两处写反的症状都是一句 <c>Permission denied (publickey)</c> ——
    /// 与「CA 不被信任」「主体不匹配」长得一模一样。
    /// </remarks>
    [TestMethod]
    public async Task 证书认证能与OpenSSH对上()
    {
        RequireServer();

        string? certPath = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_CERT");
        if (string.IsNullOrEmpty(KeyPath) || !File.Exists(KeyPath)
            || string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_CERT（Start-TestServer.ps1 会生成）。");
        }

        ISshSigner key = await SshPrivateKeyFile.LoadAsync(KeyPath);
        OpenSshCertificate certificate = await OpenSshCertificate.LoadAsync(certPath);
        SshCertificateSigner signer = SshCertificateSigner.Create(certificate, key);

        Assert.AreEqual(SshCertificateType.User, certificate.CertificateType);
        Assert.IsTrue(
            certificate.IsTimeValid(DateTimeOffset.UtcNow),
            "证书应当还在有效期内 —— 过期了这条用例验的就不是证书认证本身了。");

        await using SshConnection connection =
            await Options(credentials: [new PublicKeyCredential(signer, certPath)]).ConnectAsync();

        SshCommandOutput result = await connection.RunAsync("id -un");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(User, result.StandardOutput);
    }

    // ------------------------------------------------------------ 各层

    [TestMethod]
    public async Task 大量输出能完整收回来()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();

        // 4 MiB 的可预测内容 —— 窗口回补、分帧、背压全都要走一遍。
        SshCommandOutput result = await connection.RunAsync(
            "head -c 4194304 /dev/zero | tr '\\0' 'a'");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(4 * 1024 * 1024, result.StandardOutput.Length);
        Assert.IsTrue(result.StandardOutput.All(c => c == 'a'));
    }

    [TestMethod]
    public async Task 标准输入能送到真实的远端()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();
        await using SshCommand command = await connection.ExecuteAsync("cat");

        await command.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("喂进去的内容"));
        await command.CompleteStandardInputAsync();

        (SshCommandResult result, string stdout, _) = await command.ReadToEndAsync();

        Assert.AreEqual("喂进去的内容", stdout);
        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public async Task 退出码与信号都能如实拿到()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();

        SshCommandOutput code = await connection.RunAsync("exit 42");
        Assert.AreEqual(42, code.ExitCode);
        Assert.IsNull(code.Result.ExitSignalName);

        SshCommandOutput killed = await connection.RunAsync("kill -TERM $$");

        // 被信号杀死时**没有退出码** —— 不能凭空造一个 143 出来。
        Assert.IsNull(killed.ExitCode, "被信号杀死时不该有退出码");
        Assert.AreEqual("TERM", killed.Result.ExitSignalName);
    }

    [TestMethod]
    public async Task 伪终端能开起来()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();
        await using SshShell shell = await connection.OpenShellAsync(new SshShellOptions
        {
            TerminalType = "xterm-256color",
            Size = new TerminalSize(120, 40, 960, 800),
        });

        await shell.Input.WriteAsync(Encoding.UTF8.GetBytes("tty; exit\n"));
        await shell.Input.FlushAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        StringBuilder output = new();

        while (true)
        {
            System.IO.Pipelines.ReadResult read = await shell.Output.ReadAsync(timeout.Token);
            output.Append(Encoding.UTF8.GetString(read.Buffer.FirstSpan));
            shell.Output.AdvanceTo(read.Buffer.End);

            if (read.IsCompleted || output.ToString().Contains("/dev/pts", StringComparison.Ordinal))
            {
                break;
            }
        }

        // 有 pty 的话 tty 会报 /dev/pts/N；没有的话它报 "not a tty"。
        Assert.Contains("/dev/pts", output.ToString());
    }

    [TestMethod]
    public async Task SFTP能与真实的sftp_server对话()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);

        Assert.IsGreaterThan(0, sftp.WorkingDirectory.Length);
        Console.WriteLine($"工作目录：{sftp.WorkingDirectory}；能力：" +
            $"posix-rename={sftp.Capabilities.HasPosixRename}，" +
            $"limits={sftp.Capabilities.HasLimits}，块大小={sftp.BlockSize}");

        string path = $"{sftp.WorkingDirectory}/velashell-interop-{Guid.NewGuid():N}.bin";

        byte[] payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);

        try
        {
            await sftp.WriteAllBytesAsync(path, payload);
            byte[] back = await sftp.ReadAllBytesAsync(path);

            Assert.AreSequenceEqual(payload, back, "1 MiB 往返要一字节不差");

            SftpFileAttributes attributes = await sftp.GetAttributesAsync(path);
            Assert.AreEqual((ulong)payload.Length, attributes.Size);
            Assert.IsTrue(attributes.IsRegularFile);
        }
        finally
        {
            try
            {
                await sftp.DeleteFileAsync(path);
            }
            catch (SftpException)
            {
                // 清理失败不该让测试变红。
            }
        }
    }

    [TestMethod]
    public async Task 列真实目录能拿到符号链接()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);

        int total = 0;
        int links = 0;

        await foreach (SftpDirectoryEntry entry in sftp.EnumerateDirectoryAsync("/usr/lib"))
        {
            total++;
            if (entry.IsSymbolicLink)
            {
                links++;
                Assert.IsNotNull(entry.LinkTarget, $"{entry.Name} 是链接却没有目标");
            }

            if (total > 500)
            {
                break;
            }
        }

        Assert.IsGreaterThan(0, total, "/usr/lib 不该是空的");
        Console.WriteLine($"列了 {total} 项，其中 {links} 个符号链接。");
    }

    [TestMethod]
    public async Task 压缩能与OpenSSH协商上()
    {
        RequireServer();

        SshAlgorithmSet withCompression = SshAlgorithmSet.Default.WithCompression();

        await using SshConnection connection = await Options(withCompression).ConnectAsync();

        // ⚠️ **先断言真的谈成了。**
        //
        // 服务端不支持压缩时会静默落到 none，不会报错 —— 只看「输出对不对」
        // 的话，这条用例在压缩根本没开的情况下照样过，那它就什么都没验。
        Assert.IsNotNull(connection.Algorithms, "工厂建的连接应当带着协商结果");
        Assert.AreEqual(
            SshAlgorithmNames.ZlibOpenSsh,
            connection.Algorithms!.Value.CompressionServerToClient,
            "服务端 → 客户端方向应当谈成 zlib@openssh.com");
        Assert.AreEqual(
            SshAlgorithmNames.ZlibOpenSsh,
            connection.Algorithms!.Value.CompressionClientToServer,
            "客户端 → 服务端方向应当谈成 zlib@openssh.com");

        // 谈成了之后再验数据确实完好 —— 压缩流一旦错位，症状就是内容对不上。
        SshCommandOutput result = await connection.RunAsync(
            "for i in $(seq 1 2000); do echo '同一行反复出现，非常可压缩'; done");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);

        // ⚠️ 按**行数**断言，别按 `.Length`。
        // `.Length` 是字符数，而这一行是 13 个中日韩字符 + 换行 = 14 个字符、
        // 40 个字节。拿「> 50000」去卡字符数就会莫名其妙地挂 ——
        // 第一版正是这么写的，而它在真实服务端上一跑就红了。
        Assert.HasCount(
            2000, result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            "压缩流上的行数要一行不差");
        Assert.AreEqual(
            2000 * 40, Encoding.UTF8.GetByteCount(result.StandardOutput),
            "字节数也要一字节不差 —— 压缩流错位的典型症状就是少几段");
    }

    [TestMethod]
    public async Task 本地转发能穿过真实服务端()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();

        // 让服务端连它自己的 sshd —— 对端会回一个 SSH 版本标识串，
        // 那是一个不需要在容器里额外装东西就能验证的信号。
        //
        // ⚠️ 端口要用**容器里 sshd 实际监听的那个**（linuxserver 镜像固定是 2222），
        // 不是 22，也**不是映射到本机的那个**（`VELASHELL_SSH_INTEROP_PORT`）——
        // 隧道的目标是从服务端视角解析的。两者只在默认端口下恰好相等；
        // 换一个映射端口（比如 2222 被占用时 -Port 2224）就会得到「Connection refused」，
        // 看上去像转发坏了。
        const int SshdPortInsideContainer = 2222;
        await using SshChannel tunnel = await connection.OpenTcpTunnelAsync("127.0.0.1", SshdPortInsideContainer);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        System.IO.Pipelines.ReadResult read = await tunnel.StandardOutput.ReadAsync(timeout.Token);
        string banner = Encoding.ASCII.GetString(read.Buffer.FirstSpan);
        tunnel.StandardOutput.AdvanceTo(read.Buffer.End);

        StringAssert.StartsWith(banner, "SSH-2.0-", "隧道对面应当是那台 sshd");
    }

    /// <summary>
    /// 拿真实的 sshd 当跳板，经它的 direct-tcpip 再连它自己 —— 跑一条完整的 SSH-in-SSH。
    /// </summary>
    /// <remarks>
    /// 目标写 <c>127.0.0.1:2222</c>：从跳板的视角解析（velashell-docs/zh/ssh/spec/09 §5），指的是容器里的 sshd 自己。
    /// </remarks>
    [TestMethod]
    public async Task 经真实服务端做跳板连上目标()
    {
        RequireServer();

        SshConnectionOptions target = new(User, "127.0.0.1", 2222)
        {
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential(Password)],
            Dialer = Ssh.Transport.DialerChain.Jump(Options()),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        await using SshConnection connection = await target.ConnectAsync();
        SshCommandOutput result = await connection.RunAsync("echo 经跳板");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains("经跳板", result.StandardOutput);
    }

    [TestMethod]
    public async Task 保活探测能被真实服务端应答()
    {
        RequireServer();

        await using SshConnection connection = await Options().ConnectAsync();

        bool alive = await connection.SendKeepAliveAsync();

        // OpenSSH 不认识 keepalive@openssh.com 作为客户端发来的请求时会回
        // REQUEST_FAILURE —— **那也算有应答**，链路是活的。
        Assert.IsTrue(connection.IsAlive);
        Console.WriteLine($"保活应答：{alive}");
    }


    // ------------------------------------------------------------ X11 转发

    /// <summary>X11 用例还需要服务端开了 <c>X11Forwarding</c> 且装了 <c>xauth</c>。</summary>
    /// <remarks>
    /// 单独一个开关，因为常见镜像（比如 linuxserver/openssh-server）默认两样都没有。
    /// 用 <c>Start-TestServer.ps1 -X11</c> 起服务端。
    /// </remarks>
    private static void RequireX11Server()
    {
        RequireServer();

        if (Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_X11") != "1")
        {
            Assert.Inconclusive(
                "X11 互操作用例需要服务端开了 X11Forwarding 且装了 xauth。" +
                "用 scripts/ssh/interop/Start-TestServer.ps1 -X11 起，然后设 VELASHELL_SSH_INTEROP_X11=1。");
        }
    }

    [TestMethod]
    public async Task 真实服务端接受x11_req并把假cookie存进xauth()
    {
        // ⚠️ 这一条验的是**十六进制编码那个坑**。
        // x11-req 的 cookie 字段是十六进制文本；发成原始字节的话，
        // 远端 xauth 存进去的和我们校验的就对不上，
        // 而错误信息只会说「连接被拒绝」—— 指不到这里。
        RequireX11Server();

        await using SshConnection connection = await Options().ConnectAsync();

        SshExecutionOptions options = new()
        {
            X11 = new X11ForwardOptions { Trusted = true, Display = X11Display.Parse(":0") },
        };

        await using SshCommand command = await connection.ExecuteAsync(
            "echo DISPLAY=$DISPLAY; xauth list 2>/dev/null", options);

        Assert.IsNotNull(command.X11, "请求了就该拿得到转发器");
        string expected = Convert.ToHexStringLower(command.X11!.FakeCookie);

        (SshCommandResult result, string output, _) = await command.ReadToEndAsync();
        Assert.AreEqual(0, result.ExitCode);

        Assert.Contains("DISPLAY=localhost:", output, "sshd 应当给这条会话配上转发的显示");
        Assert.Contains(XAuthority.MitMagicCookie1, output);

        // 远端 xauth 里存的那个 cookie，必须**正好是我们发出去的假 cookie**。
        Assert.Contains(
expected,             output,
            $"远端 xauth 存的应当是我们发的假 cookie（{expected}）—— 对不上就是十六进制编码写错了");
    }

    [TestMethod]
    public async Task 真实服务端开回的x11通道会被接受并换成真cookie()
    {
        // 端到端：容器里的进程连上 sshd 配的 DISPLAY，sshd 于是开一条 x11
        // 通道回来；我们核对假 cookie、换成真 cookie，转给本机一个假的 X server。
        RequireX11Server();

        using FakeXServer xserver = FakeXServer.Start();
        string xauthority = WriteXAuthority(out byte[] realCookie);

        try
        {
            await using SshConnection connection = await Options().ConnectAsync();

            SshExecutionOptions options = new()
            {
                X11 = new X11ForwardOptions
                {
                    Trusted = true,
                    Display = xserver.Display,
                    XAuthorityPath = xauthority,
                },
            };

            // ⚠️ 建立报文要**从 stdin 喂进去**，不能拼进命令行 ——
            // 报文里的假 cookie 只有在 x11-req 发完之后才知道，
            // 而命令行必须在那之前就定下来。
            await using SshCommand command = await connection.ExecuteAsync(
                "base64 -d | nc -w 5 localhost 6010 >/dev/null 2>&1", options);

            Assert.IsNotNull(command.X11);

            byte[] setup = BuildX11Setup(command.X11!.FakeCookie.ToArray());
            byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(setup));

            await command.StandardInput.WriteAsync(encoded);
            await command.StandardInput.FlushAsync();
            await command.CompleteStandardInputAsync();

            byte[] arrived = await xserver.ReadSetupAsync();

            Assert.IsTrue(
                X11SetupMessage.TryParse(new ReadOnlySequence<byte>(arrived), out X11SetupMessage.Parsed parsed),
                "落到本机 X server 上的应当是一个完整的建立报文");

            Assert.AreSequenceEqual(
                realCookie, parsed.ProtocolData, "转给本机 X server 的必须是**真** cookie —— 假的那个只在 SSH 线上出现");

            Assert.AreEqual(1, command.X11!.AcceptedChannels);
            Assert.AreEqual(0, command.X11!.RejectedChannels);

            _ = await command.ReadToEndAsync();
        }
        finally
        {
            try { File.Delete(xauthority); } catch (IOException) { }
        }
    }

    /// <summary>拼一个带指定 cookie 的 X11 连接建立报文。</summary>
    private static byte[] BuildX11Setup(byte[] cookie)
    {
        byte[] name = Encoding.ASCII.GetBytes(XAuthority.MitMagicCookie1);
        int paddedName = (name.Length + 3) & ~3;
        int paddedData = (cookie.Length + 3) & ~3;

        byte[] message = new byte[X11SetupMessage.HeaderLength + paddedName + paddedData];
        message[0] = (byte)'B';                                            // 大端
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 11);      // protocol major
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), (ushort)cookie.Length);

        name.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength));
        cookie.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength + paddedName));
        return message;
    }

    /// <summary>摆一份只含通配条目的 <c>.Xauthority</c>，并交出里面那个真 cookie。</summary>
    private static string WriteXAuthority(out byte[] realCookie)
    {
        realCookie = RandomNumberGenerator.GetBytes(16);
        string path = Path.Combine(Path.GetTempPath(), $"velashell-xauth-{Guid.NewGuid():N}");

        ArrayBufferWriter<byte> buffer = new();
        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(two, XAuthority.FamilyWild);
        buffer.Write(two);

        WriteBlock(buffer, []);                       // address（通配族不看）
        WriteBlock(buffer, []);                       // display number（通配）
        WriteBlock(buffer, Encoding.ASCII.GetBytes(XAuthority.MitMagicCookie1));
        WriteBlock(buffer, realCookie);

        File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
        return path;

        static void WriteBlock(ArrayBufferWriter<byte> target, byte[] value)
        {
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length);
            target.Write(length);
            target.Write(value);
        }
    }

    /// <summary>一个只做一件事的假 X server：收下建立报文。</summary>
    private sealed class FakeXServer : IDisposable
    {
        private readonly Socket _listener;
        private readonly TaskCompletionSource<byte[]> _setup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Socket? _accepted;

        private FakeXServer(Socket listener, X11Display display)
        {
            _listener = listener;
            Display = display;
            _ = Task.Run(AcceptAsync);
        }

        public X11Display Display { get; }

        public static FakeXServer Start()
        {
            for (int number = 40; number < 80; number++)
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, X11Display.TcpPortBase + number));
                    socket.Listen(4);
                    return new FakeXServer(socket, X11Display.Parse($"localhost:{number}")!);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }

            throw new InvalidOperationException("找不到可用的 X11 端口。");
        }

        public async Task<byte[]> ReadSetupAsync() =>
            await _setup.Task.WaitAsync(TimeSpan.FromSeconds(20));

        private async Task AcceptAsync()
        {
            try
            {
                // 套接字要留着 —— 读完就关的话，搬运那一侧立刻拿到
                // 「对端已关闭读取端」，而那与被测行为毫无关系。
                _accepted = await _listener.AcceptAsync();

                byte[] buffer = new byte[256];
                int read = await _accepted.ReceiveAsync(buffer);
                _setup.TrySetResult(buffer[..read]);
            }
            catch (Exception ex)
            {
                _setup.TrySetException(ex);
            }
        }

        public void Dispose()
        {
            _accepted?.Dispose();
            _listener.Dispose();
        }
    }
}
