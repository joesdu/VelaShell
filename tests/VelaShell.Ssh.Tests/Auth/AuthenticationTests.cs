// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/04-authentication.md 全部
//
// 这是 M1 的第二块决定性测试：握手之后真的把认证跑完。
//
// 这里最值得看的两条：
//   · **部分成功（2FA）**。SSH 里没有「2FA 报文」，多因素就是
//     「USERAUTH_FAILURE 且 partial_success = true」。把它当失败处理，
//     库就永远连不上堡垒机 —— 而且症状是「密码明明是对的却说认证失败」。
//   · **公钥签名输入**。session_id 与请求载荷必须拼成一份连续字节再签；
//     分两处各拼一遍是这里唯一的出错方式，而它的症状只有一句「签名验证失败」。

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Auth;

[TestClass]
[TestCategory("Auth")]
public sealed class AuthenticationTests
{
    private sealed record AuthRun(
        SshAuthenticationResult? Result,
        SshAuthenticationException? Error,
        TestAuthObservation Observation,
        bool ServerSucceeded)
    {
        /// <summary>成功时的结果；失败时直接让断言看到那条异常的说明。</summary>
        public SshAuthenticationResult Succeeded =>
            Result ?? throw new AssertFailedException(
                $"认证本应成功，却失败了：{Error?.Message}{Environment.NewLine}{Error?.DescribeAttempts()}");

        public SshAuthenticationException Failed =>
            Error ?? throw new AssertFailedException("认证本应失败，却成功了。");
    }

    /// <summary>跑完握手 + 认证，两侧都收工。</summary>
    private static async Task<AuthRun> RunAsync(
        IReadOnlyList<SshCredential> credentials,
        TestAuthPolicy? policy = null,
        string userName = "joe",
        Func<SshPacketTransport, string, byte[], SshAuthenticator>? authenticatorFactory = null)
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();

        await using TestSshServer server = new(serverStream);
        SshPacketTransport clientTransport = new(clientStream);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        try
        {
            Task<TestSshServerHandshake> serverHandshake = server.HandshakeAsync(cts.Token);

            SshVersionExchangeResult versions =
                await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);

            SshKeyExchangeRunner runner = new(
                clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
            SshKeyExchangeResult kex =
                await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);

            TestSshServerHandshake handshake = await serverHandshake;

            // 服务端的认证侧与客户端**并发**跑：中间有多个「我发你答」的往返。
            TestAuthServer authServer = new(server.Transport, handshake.ExchangeHash, policy);
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);

            SshAuthenticator authenticator =
                (authenticatorFactory ?? DefaultAuthenticator)(clientTransport, userName, kex.SessionId);

            SshAuthenticationResult? result = null;
            SshAuthenticationException? error = null;
            try
            {
                result = await authenticator.AuthenticateAsync(credentials, cts.Token);
            }
            catch (SshAuthenticationException ex)
            {
                error = ex;
            }

            // 客户端收工后关掉自己这一半，让服务端读到 EOF 退出循环；
            // 不关的话认证失败的用例会把服务端永远晾在 ReadPacketAsync 上。
            await clientTransport.DisposeAsync();
            bool serverSucceeded = await serverAuth;

            return new AuthRun(result, error, authServer.Observation, serverSucceeded);
        }
        finally
        {
            await clientTransport.DisposeAsync();
        }
    }

    private static SshAuthenticator DefaultAuthenticator(
        SshPacketTransport transport, string userName, byte[] sessionId) =>
        new(transport, userName, sessionId);

    // ------------------------------------------------------------ 密码

    [TestMethod]
    public async Task 密码正确时认证成功()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy { AcceptPassword = "hunter2" });

        Assert.AreEqual(SshAlgorithmNames.AuthPassword, run.Succeeded.Method);
        Assert.IsTrue(run.ServerSucceeded, "服务端也应当认为认证成功了");

        // 第一条永远是 none —— 那是问出「服务端接受哪些方法」的唯一途径。
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.AuthNone, SshAlgorithmNames.AuthPassword }, run.Observation.RequestedMethods);
    }

    [TestMethod]
    public async Task 密码错误时抛出带逐条记录的异常()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("wrong")],
            new TestAuthPolicy { AcceptPassword = "hunter2" });

        SshAuthenticationException error = run.Failed;
        Assert.AreEqual(SshFailureReason.AuthenticationMethodExhausted, error.Reason);
        Assert.IsFalse(error.PartialSuccessAchieved);

        // 记录里要能分清「试过并失败」与「没试」。这正是它存在的理由：
        // 没有它，界面上只剩一句「用户名或密码不正确」。
        Assert.Contains(
            a => a.Method == SshAlgorithmNames.AuthPassword
                                    && a.Outcome == SshAuthOutcome.Failure, error.Attempts,
            $"应当记下 password 失败过：{Environment.NewLine}{error.DescribeAttempts()}");
    }

    [TestMethod]
    public async Task 用户名被如实发送()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy { AcceptPassword = "hunter2", ExpectedUserName = "张三" },
            userName: "张三");

        Assert.AreEqual(SshAlgorithmNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual(new[] { "张三" }, run.Observation.UserNames, "用户名是 UTF-8 string，非 ASCII 必须能原样过去");
    }

    [TestMethod]
    public async Task 按需取值的密码回调只在真要用时才被调用()
    {
        int calls = 0;
        PasswordCredential credential = new(_ =>
        {
            calls++;
            return ValueTask.FromResult("hunter2");
        });

        AuthRun run = await RunAsync([credential], new TestAuthPolicy { AcceptPassword = "hunter2" });

        Assert.AreEqual(SshAlgorithmNames.AuthPassword, run.Succeeded.Method);
        Assert.AreEqual(1, calls, "密码只该在真正发出请求时解出来一次");
    }

    [TestMethod]
    public async Task 服务端要求改密码时给出可读的原因()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy { AcceptPassword = "hunter2", RequestPasswordChange = true });

        // 本库不实现改密码流程，但**必须说清楚为什么连不上** ——
        // 「直接断开且不说原因」是用户最难自救的一种失败。
        SshAuthenticationException error = run.Failed;
        Assert.Contains(
            a => a.Detail is not null && a.Detail.Contains("修改密码", StringComparison.Ordinal), error.Attempts,
            $"应当说明服务端要求改密码：{Environment.NewLine}{error.DescribeAttempts()}");
    }

    // ------------------------------------------------------------ 公钥

    [TestMethod]
    public async Task 公钥认证成功且服务端验签通过()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);

        // 签名输入拼错了就走不到这里 —— 服务端是**独立**重算被签名数据的。
        Assert.IsTrue(run.Observation.AllSignaturesValid, "服务端必须能验证客户端的签名");
        Assert.AreEqual(1, run.Observation.PublicKeySignedCount);
    }

    [TestMethod]
    public async Task 本地私钥走一段式不做多余的探测往返()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreEqual(0, run.Observation.PublicKeyProbeCount,
            "本地签名很便宜，不值得为它多花一个往返（velashell-docs/zh/ssh/spec/04 §4.1）");
    }

    [TestMethod]
    public async Task 外部签名器先探测再签名()
    {
        using var inner = InMemorySshSigner.GenerateEd25519();
        ExpensiveSigner signer = new(inner);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [inner.PublicKey.Blob.ToArray()],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreEqual(1, run.Observation.PublicKeyProbeCount,
            "外部签名要先问「你认这把钥吗」——不能为一把服务端不认的钥让用户去按硬件键");
        Assert.AreEqual(1, signer.SignCalls, "探测被接受之后才真的签一次");
    }

    [TestMethod]
    public async Task 外部签名器在服务端不认这把钥时一次都不签()
    {
        using var inner = InMemorySshSigner.GenerateEd25519();
        ExpensiveSigner signer = new(inner);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [],   // 一把都不认
            });

        _ = run.Failed;
        Assert.AreEqual(1, run.Observation.PublicKeyProbeCount);
        Assert.AreEqual(0, signer.SignCalls,
            "两段式存在的全部意义就是这一条：不认就不签，用户不用白按一次硬件键");
    }

    [TestMethod]
    public async Task 服务端不认这把公钥时如实记录而不是说密码错()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer, "~/.ssh/id_ed25519")],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [],
            });

        SshAuthenticationException error = run.Failed;
        Assert.Contains(
            a => a.CredentialLabel == "~/.ssh/id_ed25519"
                                    && a.Outcome == SshAuthOutcome.Failure, error.Attempts,
            $"标签要原样进记录，好让用户知道是哪一把钥：{Environment.NewLine}{error.DescribeAttempts()}");
    }

    [TestMethod]
    public async Task 凭据取不到材料时记成跳过并继续试下一条()
    {
        using var good = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(new BrokenSigner(good.PublicKey), "坏掉的私钥文件"),
                new PublicKeyCredential(good, "好的私钥"),
            ],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [good.PublicKey.Blob.ToArray()],
            });

        // 一条凭据坏了不该打断整条链 —— 后面还有能用的。
        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.Contains(
            a => a.CredentialLabel == "坏掉的私钥文件"
                                            && a.Outcome == SshAuthOutcome.SkippedNoMaterial, run.Succeeded.Attempts,
            "「私钥读不出来」与「服务端不认这把钥」是两件事，记录里必须分得开");
    }

    [TestMethod]
    public async Task agent拒签时记成跳过并继续试下一条()
    {
        using var good = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(new RefusingAgentSigner(good.PublicKey), "agent: id_ed25519"),
                new PublicKeyCredential(good, "~/.ssh/id_ed25519"),
            ],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [good.PublicKey.Blob.ToArray()],
            });

        // SshAgentException 是 SshException —— 曾经它会直接打断整条凭据链。
        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.Contains(
            a => a.CredentialLabel == "agent: id_ed25519" && a.Outcome == SshAuthOutcome.SkippedNoMaterial,
            run.Succeeded.Attempts);
        Assert.AreEqual(1, run.Observation.PublicKeyProbeCount, "拒签发生在探测通过之后");
    }

    [TestMethod]
    public async Task 请求在途时回调抛出的异常照实抛出而不是当成跳过()
    {
        // 横幅回调在「密码请求已发出、SUCCESS 还没读」时抛异常。当成「跳过这条凭据」的话，
        // 客户端会报「所有方法都失败」—— 而服务端其实已经认证通过了。
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RunAsync(
                [new PasswordCredential("hunter2")],
                new TestAuthPolicy { AcceptPassword = "hunter2", BannersBeforeSuccess = ["维护通知"] },
                authenticatorFactory: static (transport, user, sessionId) => new SshAuthenticator(transport, user, sessionId)
                {
                    BannerHandler = static (text, _) => text == "维护通知"
                        ? throw new InvalidOperationException("界面已经关了")
                        : ValueTask.CompletedTask,
                }));

        Assert.AreEqual("界面已经关了", error.Message);
    }

    [TestMethod]
    public async Task 服务端宣告的server_sig_algs决定RSA用哪种签名算法()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                // 服务端只肯认 SHA-256 那一种。
                ServerSignatureAlgorithms = [SshAlgorithmNames.RsaSha256],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.RsaSha256 }, run.Observation.PublicKeySignatureAlgorithms, "我们自己更偏好 SHA-512，但服务端说只认 SHA-256 —— 就得听它的（RFC 8308）");
        Assert.Contains(
SshAlgorithmNames.RsaSha256, [.. run.Succeeded.ServerSignatureAlgorithms]);
    }

    [TestMethod]
    public async Task 没有server_sig_algs时用我们自己的第一偏好()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                ServerSignatureAlgorithms = null,
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.RsaSha512 }, run.Observation.PublicKeySignatureAlgorithms);
    }

    [TestMethod]
    public async Task 默认不会降级到SHA1的ssh_rsa()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                // 服务端（谎）称只认 SHA-1。
                ServerSignatureAlgorithms = [SshAlgorithmNames.SshRsa],
            });

        // 无条件跟着服务端降级，就把降级攻击的收益还回去了。
        CollectionAssert.DoesNotContain(
            run.Observation.PublicKeySignatureAlgorithms, SshAlgorithmNames.SshRsa,
            "默认不接受 SHA-1 的 ssh-rsa —— 需要它的人得显式打开 AllowSha1RsaSignatures");
    }

    [TestMethod]
    public async Task 显式打开之后才会使用SHA1的ssh_rsa()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                ServerSignatureAlgorithms = [SshAlgorithmNames.SshRsa],
            },
            authenticatorFactory: static (t, u, s) => new SshAuthenticator(t, u, s)
            {
                AllowSha1RsaSignatures = true,
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.SshRsa }, run.Observation.PublicKeySignatureAlgorithms, "开关打开之后才肯用它 —— 为的是还能连上停在 OpenSSH 7.x 的老机器");
    }

    // ------------------------------------------------------------ 键盘交互

    [TestMethod]
    public async Task 键盘交互单轮问答成功()
    {
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                RequiredMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Name = "两步验证",
                        Instruction = "请输入手机上的动态码。",
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.HasCount(1, seen);
        Assert.AreEqual("请输入手机上的动态码。", seen[0].Instruction);
        Assert.HasCount(1, seen[0].Prompts);
        Assert.IsFalse(seen[0].Prompts[0].Echo, "不回显的提示必须以密码方式采集");
        Assert.IsFalse(seen[0].IsInformationalOnly);
    }

    [TestMethod]
    public async Task 键盘交互支持多轮()
    {
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        seen.Count == 1 ? ["hunter2"] : ["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                RequiredMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Prompts = [("Password: ", false)],
                        ExpectedAnswers = ["hunter2"],
                    },
                    new TestKeyboardRound
                    {
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.HasCount(2, seen, "服务端可以来回问任意多轮");
    }

    [TestMethod]
    public async Task 纯展示轮仍然通知回调但不要求输入()
    {
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        challenge.IsInformationalOnly ? [] : ["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                RequiredMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Instruction = "请按下硬件令牌上的按钮。",
                        Prompts = [],
                        ExpectedAnswers = [],
                    },
                    new TestKeyboardRound
                    {
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.HasCount(2, seen);

        // 这一轮没有提示，但 Instruction 必须送到 —— 否则用户对着没反应的界面干等，
        // 而服务端正等他去按硬件令牌。
        Assert.IsTrue(seen[0].IsInformationalOnly);
        Assert.AreEqual("请按下硬件令牌上的按钮。", seen[0].Instruction);
        Assert.AreSequenceEqual(Array.Empty<string>(), [.. run.Observation.KeyboardAnswers[0]]);
    }

    [TestMethod]
    public async Task 密码凭据在服务端只开键盘交互时自动改走那条路()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                // 服务端根本不接受 password。
                OfferedMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                RequiredMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Prompts = [("Password: ", false)],
                        ExpectedAnswers = ["hunter2"],
                    },
                ],
            });

        // 大量服务器只开 keyboard-interactive，而它唯一的提示就是「Password:」。
        // 不做这一步，用户填了密码却连不上，也说不出为什么。
        Assert.AreEqual(SshAlgorithmNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.Contains(
            a => a.CredentialLabel.Contains("keyboard-interactive", StringComparison.Ordinal), run.Succeeded.Attempts,
            "记录里要看得出走的是桥接那条路");
    }

    [TestMethod]
    public async Task 关掉开关之后密码不再自动填进键盘交互()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2") { AlsoAnswerKeyboardInteractive = false }],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                RequiredMethods = [SshAlgorithmNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Prompts = [("Password: ", false)],
                        ExpectedAnswers = ["hunter2"],
                    },
                ],
            });

        // 真正的 2FA 场景要关掉它：第一条提示可能就是动态码，
        // 自动填密码只会白白消耗一次尝试。
        SshAuthenticationException error = run.Failed;
        Assert.AreEqual(SshFailureReason.TwoFactorRequired, error.Reason);
        Assert.DoesNotContain(SshAlgorithmNames.AuthKeyboardInteractive, run.Observation.RequestedMethods,
            "关掉之后连试都不该试");
    }

    // ------------------------------------------------------------ 部分成功（2FA）

    [TestMethod]
    public async Task 公钥加动态码的两步认证能走通()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(signer, "~/.ssh/id_ed25519"),
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthPublicKey, SshAlgorithmNames.AuthKeyboardInteractive],
                // **两个都要过。**第一个过了之后服务端回
                // FAILURE + partial_success = true —— 这就是 SSH 里 2FA 的全部表达方式。
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey, SshAlgorithmNames.AuthKeyboardInteractive],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Instruction = "请输入动态码。",
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.IsTrue(run.ServerSucceeded);

        // 把 partial_success 当失败处理的库会卡在这里：公钥明明过了，
        // 却被记成失败、跳过后续方法，最后报「认证失败」。
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.AuthPublicKey, SshAlgorithmNames.AuthKeyboardInteractive }, run.Observation.PassedMethods);

        Assert.Contains(
            a => a.Outcome == SshAuthOutcome.PartialSuccess, run.Succeeded.Attempts,
            $"公钥那一步要记成部分成功而不是失败：{Environment.NewLine}" +
            string.Join(Environment.NewLine, run.Succeeded.Attempts));
        Assert.HasCount(1, seen);
    }

    [TestMethod]
    public async Task 部分成功之后回头再试当时因方法不被接受而跳过的凭据()
    {
        // AuthenticationMethods publickey,password：服务端一开始只报 publickey，
        // 排在前面的口令凭据于是被跳过；公钥那一步过了之后服务端才开放 password ——
        // 不回头再扫一遍的话，认证以「凭据试完了」失败。
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PasswordCredential("hunter2") { AlsoAnswerKeyboardInteractive = false },
                new PublicKeyCredential(signer),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthPublicKey],
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey, SshAlgorithmNames.AuthPassword],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                AcceptPassword = "hunter2",
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.AuthPublicKey, SshAlgorithmNames.AuthPassword }, run.Observation.PassedMethods);
    }

    [TestMethod]
    public async Task 第四把钥才对时也能登上()
    {
        // 每把钥是不同的凭据，不是对同一个秘密的重试 —— 不能套「同一方法失败三次就不再试」。
        // agent 里有五把钥、对的是第四把，是很常见的情形。
        InMemorySshSigner[] keys = [.. Enumerable.Range(0, 5).Select(_ => InMemorySshSigner.GenerateEd25519())];
        try
        {
            AuthRun run = await RunAsync(
                [.. keys.Select(static k => new PublicKeyCredential(k))],
                new TestAuthPolicy
                {
                    RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                    AcceptedPublicKeys = [keys[3].PublicKey.Blob.ToArray()],
                });

            Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
            Assert.AreEqual(4, run.Observation.PublicKeySignedCount, "前三把各试一次，第四把成功，第五把不必试");
        }
        finally
        {
            foreach (InMemorySshSigner key in keys)
            {
                key.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task 第一步过了但第二步没配凭据时说得出卡在哪()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],   // 只配了公钥，没配动态码
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthPublicKey, SshAlgorithmNames.AuthKeyboardInteractive],
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey, SshAlgorithmNames.AuthKeyboardInteractive],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            });

        SshAuthenticationException error = run.Failed;

        // 「第一步过了，卡在第二步」与「一步都没过」对用户是完全不同的信息。
        Assert.IsTrue(error.PartialSuccessAchieved, "至少有一步是通过了的");
        Assert.AreEqual(SshFailureReason.TwoFactorRequired, error.Reason);
        Assert.Contains("键盘交互", error.Message,
            "界面要能说成「这台机器需要动态码」，而不是「用户名或密码不正确」");
    }

    // ------------------------------------------------------------ 方法调度

    [TestMethod]
    public async Task 服务端不接受的方法被跳过并记录原因()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(signer, "~/.ssh/id_ed25519"),
                new PasswordCredential("hunter2"),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthPassword],   // 不接受公钥
                RequiredMethods = [SshAlgorithmNames.AuthPassword],
                AcceptPassword = "hunter2",
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPassword, run.Succeeded.Method);

        // 公钥那条连发都不该发 —— 服务端已经说了它不接受。
        Assert.DoesNotContain(SshAlgorithmNames.AuthPublicKey, run.Observation.RequestedMethods);
        Assert.Contains(
            a => a.CredentialLabel == "~/.ssh/id_ed25519"
                                            && a.Outcome == SshAuthOutcome.SkippedNotOffered, run.Succeeded.Attempts);
    }

    [TestMethod]
    public async Task 按使用者给出的顺序依次尝试()
    {
        using var first = InMemorySshSigner.GenerateEd25519();
        using var second = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(first, "第一把"),
                new PublicKeyCredential(second, "第二把"),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthPublicKey],
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [second.PublicKey.Blob.ToArray()],   // 只认第二把
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);

        string[] labels = [.. run.Succeeded.Attempts.Select(a => a.CredentialLabel)];
        int firstIndex = Array.IndexOf(labels, "第一把");
        int secondIndex = Array.IndexOf(labels, "第二把");
        Assert.IsTrue(firstIndex >= 0 && secondIndex > firstIndex,
            $"顺序由使用者决定，不能由库自作主张地重排：{string.Join(" → ", labels)}");
    }

    [TestMethod]
    public async Task 服务端配了无认证时none直接成功()
    {
        AuthRun run = await RunAsync(
            [],
            new TestAuthPolicy
            {
                OfferedMethods = [],
                RequiredMethods = [SshAlgorithmNames.AuthNone],
            });

        // none 通常失败，但**不能假设它一定失败**。
        Assert.AreEqual(SshAlgorithmNames.AuthNone, run.Succeeded.Method);
        Assert.IsTrue(run.ServerSucceeded);
    }

    [TestMethod]
    public async Task 一条凭据都没配时异常里仍有服务端可用方法()
    {
        AuthRun run = await RunAsync(
            [],
            new TestAuthPolicy
            {
                OfferedMethods = [SshAlgorithmNames.AuthPassword, SshAlgorithmNames.AuthPublicKey],
                AcceptPassword = "hunter2",
            });

        SshAuthenticationException error = run.Failed;
        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.AuthPassword, SshAlgorithmNames.AuthPublicKey }, [.. error.ServerOffered], SequenceOrder.InAnyOrder, "none 探测拿回来的方法列表要留在异常里，这是用户唯一能看到的线索");
    }

    // ------------------------------------------------------------ 横幅

    [TestMethod]
    public async Task 横幅被送到回调并留在结果里()
    {
        List<string> received = [];

        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                Banners = ["未经授权的访问将被记录。", "第二条横幅。"],
            },
            authenticatorFactory: (t, u, s) => new SshAuthenticator(t, u, s)
            {
                BannerHandler = (text, _) =>
                {
                    received.Add(text);
                    return ValueTask.CompletedTask;
                },
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual(new[] { "未经授权的访问将被记录。", "第二条横幅。" }, received);
        Assert.AreSequenceEqual(received, run.Succeeded.Banner.ToArray());
    }

    // ------------------------------------------------------------ 测试替身

    /// <summary>假装签名很贵的签名器 —— 用来验证两段式。</summary>
    private sealed class ExpensiveSigner(ISshSigner inner) : ISshSigner
    {
        public SshPublicKey PublicKey => inner.PublicKey;

        public IReadOnlyList<string> SignatureAlgorithms => inner.SignatureAlgorithms;

        public bool IsLocalAndCheap => false;

        public int SignCalls { get; private set; }

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default)
        {
            SignCalls++;
            return inner.SignAsync(data, algorithm, cancellationToken);
        }
    }

    // ------------------------------------------------------------ 证书

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    private static async Task<SshCertificateSigner> LoadCertificateSignerAsync(string name)
    {
        ISshSigner inner = await SshPrivateKeyFile.LoadAsync(FixturePath(name));
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(FixturePath(name + "-cert.pub"));
        return SshCertificateSigner.Create(cert, inner);
    }

    /// <summary>
    /// 证书认证跑完整条 publickey：出示整张证书，签名由被签发的私钥出。
    /// </summary>
    /// <remarks>
    /// 服务端是**独立**重算被签名数据并自己验签的，所以这条用例真的能证明
    /// 「请求里那个算法名带后缀、签名 blob 里不带」这处不对称被处理对了 ——
    /// 处理错的症状是一句「签名验证失败」，从客户端这边什么也看不出来。
    /// </remarks>
    [TestMethod]
    [DataRow("cert-ed25519", "ssh-ed25519-cert-v01@openssh.com")]
    [DataRow("cert-rsa", "rsa-sha2-512-cert-v01@openssh.com")]
    [DataRow("cert-ecdsa", "ecdsa-sha2-nistp256-cert-v01@openssh.com")]
    public async Task 证书认证成功且服务端验得过签名(string name, string expectedAlgorithm)
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync(name);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                // 服务端认的是**整张证书**的字节，与真服务端出示什么就比什么一致。
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
            });

        Assert.AreEqual(SshAlgorithmNames.AuthPublicKey, run.Succeeded.Method);
        Assert.IsTrue(run.Observation.AllSignaturesValid, "服务端必须能验证证书认证的签名");
        Assert.AreEqual(1, run.Observation.PublicKeySignedCount);

        // 请求里那个「公钥算法名」字段必须带证书后缀。
        Assert.Contains(expectedAlgorithm, run.Observation.PublicKeySignatureAlgorithms);
    }

    [TestMethod]
    public async Task RSA证书默认也不用SHA1的签名算法()
    {
        // 禁用 SHA-1 的过滤曾经只比 ssh-rsa，RSA 证书那个 SHA-1 算法叫 ssh-rsa-cert-v01@openssh.com，
        // 于是拿证书登录时，服务端一说「只认它」，SHA-1 照样被挑出来用。
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-rsa");

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
                ServerSignatureAlgorithms = ["ssh-rsa-cert-v01@openssh.com"],
            });

        CollectionAssert.DoesNotContain(
            run.Observation.PublicKeySignatureAlgorithms, "ssh-rsa-cert-v01@openssh.com",
            "默认不用 SHA-1 —— 证书也一样");
    }

    /// <summary>证书走的仍然是 publickey，没有第三种认证方法。</summary>
    [TestMethod]
    public async Task 证书认证用的仍是publickey方法()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-ed25519");

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshAlgorithmNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
            });

        Assert.AreSequenceEqual(
            new[] { SshAlgorithmNames.AuthNone, SshAlgorithmNames.AuthPublicKey }, run.Observation.RequestedMethods);
    }

    /// <summary>取不到私钥材料的签名器 —— 模拟「私钥文件读不出来」。</summary>
    private sealed class BrokenSigner(SshPublicKey publicKey) : ISshSigner
    {
        public SshPublicKey PublicKey => publicKey;

        public IReadOnlyList<string> SignatureAlgorithms => [SshAlgorithmNames.SshEd25519];

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
            throw new IOException("私钥文件读不出来：拒绝访问。");
    }

    /// <summary>像 agent 那样先探测、签名时却被拒（用户在 ssh-add -c 的确认框里点了拒绝）。</summary>
    private sealed class RefusingAgentSigner(SshPublicKey publicKey) : ISshSigner
    {
        public SshPublicKey PublicKey => publicKey;

        public IReadOnlyList<string> SignatureAlgorithms => [SshAlgorithmNames.SshEd25519];

        public bool IsLocalAndCheap => false;

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
            throw new SshAgentException("agent 拒绝签名。");
    }
}
