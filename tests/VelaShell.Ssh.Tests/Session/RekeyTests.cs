// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §八(重协商)
//           velashell-docs/zh/ssh/spec/01-transport-framing.md §六(压缩上下文随密钥重置)
//
// 这一组回答的是一个**运维问题**，不是一个协议细节：
// OpenSSH 的 RekeyLimit 默认 1 GiB / 1 小时，到点它自己发 KEXINIT。
// 客户端接不住的表现不是「少个功能」，而是
// **挂了一下午的 shell 忽然断了**、**传到一半的大文件断了**。

using System.Text;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class RekeyTests
{
    [TestMethod]
    public async Task 服务端发起的重协商能接住并且连接继续可用()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("第一次\n"),
            ExitCode = 0,
        });

        // 先跑一条命令，确认连接本来是好的。
        SshCommandResult before = await host.Connection.RunAsync("一", cancellationToken: host.Token);
        Assert.AreEqual("第一次\n", before.StandardOutput);
        Assert.AreEqual(0, host.Connection.RekeyCount, "还没重协商过");

        SshNegotiatedAlgorithms? firstRound = host.Connection.Algorithms;
        Assert.IsNotNull(firstRound);

        // 服务端发起重协商 —— 这就是 OpenSSH 到了 RekeyLimit 时做的事。
        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        // ⚠️ 重协商完成之后**连接必须还能用**。这是整组用例的全部意义：
        // 换完密钥还能收发，才说明两边的新密钥真的对上了。
        SshCommandResult after = await host.Connection.RunAsync("二", cancellationToken: host.Token);
        Assert.AreEqual("第一次\n", after.StandardOutput, "重协商之后数据还要一字节不差");
        Assert.AreEqual(0, after.ExitCode);

        Assert.AreEqual(1, host.Connection.RekeyCount, "应当记下发生过一次重协商");
        Assert.IsTrue(host.Connection.IsAlive, "重协商不该把连接弄坏");
    }

    /// <summary>
    /// 测试桩自己的回归：服务端发起重协商时，「我们发过 KEXINIT」必须在它上线之前就记下。
    /// </summary>
    /// <remarks>
    /// 以前是发送返回之后才记。客户端回 KEXINIT 够快时，收包循环读到「没发过」，
    /// 把它当成客户端发起的重协商再发一份 KEXINIT，两边从此对不上 —— Linux CI 上偶发的
    /// 「服务端发起的重协商…」25 秒超时就是它。这里把发送之后的窗口撑到 300ms，
    /// 修复前稳定复现、修复后通过。
    /// </remarks>
    [TestMethod]
    public async Task 客户端的KEXINIT赶在服务端发送返回之前到达也能完成重协商()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("ok\n"),
            ExitCode = 0,
        });
        host.Channels.DelayAfterRekeyKexInitSent = TimeSpan.FromMilliseconds(300);

        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        SshCommandResult output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
        Assert.AreEqual("ok\n", output.StandardOutput);
        Assert.AreEqual(1, host.Connection.RekeyCount, "只该有一次重协商 —— 两份 KEXINIT 说明被当成了两次");
    }

    /// <summary>
    /// 严格 KEX 是整条连接的属性：重协商的 KEXINIT 里不再带标记，NEWKEYS 之后照样要归零序号。
    /// </summary>
    /// <remarks>
    /// 只挑 nonce 或 MAC 依赖序号的套件 —— AES-GCM 的 nonce 不看序号，两边序号对不上也照样能解，
    /// 这个缺陷在默认配置（有 AES-NI 时选 GCM）下正是被它掩盖的。
    /// </remarks>
    [TestMethod]
    [DataRow(SshAlgorithmNames.ChaCha20Poly1305)]
    [DataRow(SshAlgorithmNames.Aes256Ctr)]
    public async Task 严格KEX下重协商之后序号照样归零_依赖序号的套件能继续收发(string cipher)
    {
        SshAlgorithmSet algorithms = SshAlgorithmSet.Default with
        {
            EncryptionClientToServer = [cipher],
            EncryptionServerToClient = [cipher],
        };

        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("ok\n"), ExitCode = 0 },
            algorithms);

        Assert.IsTrue(host.Connection.Algorithms.StrictKeyExchange, "首次交换应当谈成严格 KEX");
        Assert.AreEqual(cipher, host.Connection.Algorithms.EncryptionServerToClient);

        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        SshCommandResult output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
        Assert.AreEqual("ok\n", output.StandardOutput, "重协商之后第一个报文就该解得开");
        Assert.IsTrue(host.Connection.Algorithms.StrictKeyExchange, "严格 KEX 不会因为重协商而消失");
    }

    /// <summary>
    /// 一次交换正在接收循环上跑的时候，主动发起（使用者调用或阈值监视循环）必须是空操作。
    /// </summary>
    /// <remarks>
    /// 曾经「已经在谈了」只看「我们发过、对端还没回」那一段：交换一开始标记就被清掉，
    /// 而阈值要等交换完成才归零 —— 监视循环在交换中途又发了一个 KEXINIT，对端当场断连。
    /// 「报文数到阈值会自己发起重协商」那条用例在满跑并行时约四分之一的概率栽在这里。
    /// </remarks>
    [TestMethod]
    public async Task 交换进行中再发起重协商是空操作()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("ok\n"),
            ExitCode = 0,
        });

        // 服务端发起；收到客户端的 KEXINIT 之后停住 —— 这时客户端的交换正在接收循环上等应答。
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Channels.HoldRekeyCompletionUntil = release.Task;
        Task<TestSshServerHandshake> serverRekey = host.Channels.RequestRekeyAsync();
        await host.Channels.RekeyHeld.Task.WaitAsync(host.Token);

        await host.Connection.StartRekeyAsync(host.Token);

        release.SetResult();
        await serverRekey.WaitAsync(host.Token);
        SshCommandResult output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
        Assert.AreEqual("ok\n", output.StandardOutput);
        Assert.AreEqual(1, host.Connection.RekeyCount, "中途那次发起不该变成第二次交换");
    }

    /// <summary>
    /// 重协商期间闸门关着、发送一律暂存 —— 对端永远不完成的话，连接不能无声地停在那里。
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "我们发起、对端一直不回 KEXINIT")]
    [DataRow(false, DisplayName = "对端发起、交换卡在半路")]
    public async Task 重协商超时就断开而不是永远暂存(bool weInitiate)
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("ok\n"), ExitCode = 0 },
            rekeyTimeout: TimeSpan.FromMilliseconds(300));

        // 服务端收到客户端的 KEXINIT 之后就停住，再也不往下走。
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Channels.HoldRekeyCompletionUntil = never.Task;

        if (weInitiate)
        {
            await host.Connection.StartRekeyAsync(host.Token);
        }
        else
        {
            _ = host.Channels.RequestRekeyAsync();
        }

        while (host.Connection.IsAlive)
        {
            await Task.Delay(20, host.Token);
        }

        SshConnectionClosedException error = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await host.Connection.RunAsync("ok", cancellationToken: host.Token));
        Assert.AreEqual(SshFailureReason.Timeout, error.Reason);
        Assert.AreEqual(SshPhase.Rekeying, error.Phase);
    }

    /// <summary>重协商钉住首次的主机密钥：不再问策略，换了钥就断（spec/03 §8.4）。</summary>
    [TestMethod]
    public async Task 重协商不再询问主机密钥策略()
    {
        CountingPolicy policy = new();
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("ok\n"), ExitCode = 0 },
            hostKeyPolicy: policy);
        Assert.AreEqual(1, policy.Evaluations, "首次交换问一次");

        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        // 交互式策略在这里弹窗的话，接收循环正停着等它 —— 所有通道一起卡住。
        Assert.AreEqual(1, policy.Evaluations, "重协商不该再问：钉住首次的密钥就够了");
        SshCommandResult output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
        Assert.AreEqual("ok\n", output.StandardOutput);
    }

    [TestMethod]
    public async Task 重协商时换了主机密钥就以HostKeyChanged断开()
    {
        // 签名是对的 —— 新钥确实持有对应私钥；问题在于它不是首次那一把。
        // 宽松的策略（全部接受）也挡不住这一条：它根本不该被问到。
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("ok\n"), ExitCode = 0 },
            rekeyHostKeyType: SshAlgorithmNames.SshEd25519);
        host.ServerLoopMayFail = true;

        _ = host.Channels.RequestRekeyAsync();

        while (host.Connection.IsAlive)
        {
            await Task.Delay(20, host.Token);
        }

        SshConnectionClosedException error = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await host.Connection.RunAsync("ok", cancellationToken: host.Token));
        Assert.AreEqual(SshFailureReason.HostKeyChanged, error.Reason);
        Assert.AreEqual(SshPhase.Rekeying, error.Phase);
    }

    private sealed class CountingPolicy : VelaShell.Ssh.HostKeys.IHostKeyPolicy
    {
        private int _evaluations;

        public int Evaluations => Volatile.Read(ref _evaluations);

        public ValueTask<VelaShell.Ssh.HostKeys.SshHostKeyVerdict> EvaluateAsync(
            VelaShell.Ssh.HostKeys.SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _evaluations);
            return ValueTask.FromResult(VelaShell.Ssh.HostKeys.SshHostKeyVerdict.Accept);
        }
    }

    [TestMethod]
    public async Task 重协商之后session_id不变()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("ok\n"),
            ExitCode = 0,
        });

        byte[] sessionIdBefore = host.Connection.SessionId.ToArray();

        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        // 〔RFC 4253 §7.2〕重协商时交换哈希 H 会变，但 session_id **永不改变**。
        // 混成一个字段的症状是「重协商之后再开新通道做公钥认证会失败」——
        // 一条极罕见的路径，所以这里钉住它。
        Assert.AreSequenceEqual(
            sessionIdBefore, host.Connection.SessionId.ToArray(), "session_id 在重协商之后必须保持不变");

        SshCommandResult output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
        Assert.AreEqual(0, output.ExitCode);
    }

    [TestMethod]
    public async Task 开着压缩重协商之后数据依然对得上()
    {
        // 这一条钉的是 velashell-docs/zh/ssh/spec/01 §六：**压缩上下文必须随密钥一起重置**。
        //
        // 不重置的症状极具迷惑性：重协商之前一切正常，之后对端解压失败 ——
        // 而那时已经完全看不出是压缩的问题了。所以必须有一条用例
        // 跨过重协商去读数据。
        byte[] compressible = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("同一行反复出现，非常可压缩\n", 200)));

        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript { StandardOutput = compressible, ExitCode = 0 },
            SshAlgorithmSet.Default.WithCompression());

        Assert.AreEqual(
            SshAlgorithmNames.ZlibOpenSsh,
            host.Connection.Algorithms.CompressionServerToClient,
            "前提：压缩要真的谈成了，不然这条用例什么都没验");

        SshCommandResult before = await host.Connection.RunAsync("压", cancellationToken: host.Token);
        Assert.AreSequenceEqual(compressible, Encoding.UTF8.GetBytes(before.StandardOutput));

        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        SshCommandResult after = await host.Connection.RunAsync("压", cancellationToken: host.Token);
        Assert.AreSequenceEqual(
            compressible, Encoding.UTF8.GetBytes(after.StandardOutput), "重协商之后压缩流要能继续对上 —— 两边的压缩上下文都重置了才行");

        Assert.AreEqual(1, host.Connection.RekeyCount);
    }

    [TestMethod]
    public async Task 重协商期间发出的通道数据会被暂存并在开闸后按序送达()
    {
        // 〔RFC 4253 §7.1〕发出 KEXINIT 到 NEWKEYS 之间只许发传输层消息。
        // 所以这期间写进 stdin 的数据必须**暂存**，不能直接发出去，
        // 也不能丢 —— 开闸之后按原顺序流出。
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript
            {
                EchoStandardInput = true,
                WaitForClientEof = true,
                ExitCode = 0,
            });

        await using SshCommand command =
            await host.Connection.ExecuteAsync("回显", cancellationToken: host.Token);

        // 一边重协商，一边往 stdin 写 —— 这是最容易出错的时刻。
        Task<TestSshServerHandshake> rekey = host.Channels.RequestRekeyAsync();

        byte[] payload = Encoding.UTF8.GetBytes("重协商期间写进去的这一段必须完整回来");
        await command.StandardInput.WriteAsync(payload, host.Token);
        await command.StandardInput.FlushAsync(host.Token);

        await rekey.WaitAsync(host.Token);
        await command.CompleteStandardInputAsync(host.Token);

        (SshExitStatus result, string echoed, _) =
            await command.ReadToEndAsync(host.Token);

        Assert.AreEqual(
            Encoding.UTF8.GetString(payload), echoed,
            "重协商期间写的数据要一字节不差地按原顺序送达");
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, host.Connection.RekeyCount);
    }

    [TestMethod]
    public async Task 重协商期间分几次写的数据各自暂存_开闸后逐字节送达()
    {
        // 通道数据的缓冲是池里租的，发送方一返回就还回去。暂存时不复制一份的话，
        // 下一块会租到同一个数组把它盖掉 —— 开闸后发出去的是几份「最后那一块」。
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript
            {
                EchoStandardInput = true,
                WaitForClientEof = true,
                ExitCode = 0,
            });

        await using SshCommand command =
            await host.Connection.ExecuteAsync("回显", cancellationToken: host.Token);

        // 服务端收到客户端的 KEXINIT 之后停住：客户端的闸门这时是关着的，数据只能暂存。
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Channels.HoldRekeyCompletionUntil = release.Task;
        Task<TestSshServerHandshake> rekey = host.Channels.RequestRekeyAsync();
        await host.Channels.RekeyHeld.Task.WaitAsync(host.Token);

        StringBuilder expected = new();
        long total = 0;
        for (int i = 0; i < 4; i++)
        {
            byte[] chunk = Encoding.UTF8.GetBytes($"第{i}块：{new string((char)('a' + i), 300)}");
            await command.StandardInput.WriteAsync(chunk, host.Token);
            await command.StandardInput.FlushAsync(host.Token);
            total += chunk.Length;

            // 这一块已经交给会话（被暂存了）再写下一块 —— 各自成帧。
            await command.Channel.WaitStandardInputSentAsync(total, host.Token);
            expected.Append(Encoding.UTF8.GetString(chunk));
        }

        release.SetResult();
        await rekey.WaitAsync(host.Token);
        await command.CompleteStandardInputAsync(host.Token);

        (SshExitStatus result, string echoed, _) = await command.ReadToEndAsync(host.Token);

        Assert.AreEqual(expected.ToString(), echoed, "暂存的每一块都要是它自己，而不是被后来的块盖掉");
        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public async Task 连着重协商两次也没问题()
    {
        // 长连接上重协商会反复发生（每 1 GiB 或每小时一次）。
        // 只验一次通不过「第二次用的还是第一次的状态」这类 bug。
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("还活着\n"),
            ExitCode = 0,
        });

        for (int round = 1; round <= 2; round++)
        {
            await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

            SshCommandResult output = await host.Connection.RunAsync("查", cancellationToken: host.Token);
            Assert.AreEqual("还活着\n", output.StandardOutput, $"第 {round} 次重协商之后就读不到数据了");
            Assert.AreEqual(round, host.Connection.RekeyCount);
        }
    }

    [TestMethod]
    public async Task 我们主动发起的重协商也能谈成()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("主动换过了\n"),
            ExitCode = 0,
        });

        // StartRekeyAsync 只负责把我们的 KEXINIT 发出去就返回 ——
        // 剩下的由接收循环在对端的 KEXINIT 到达时接着做。
        await host.Connection.StartRekeyAsync(host.Token);

        // 所以这里要等它真的谈完，而不是假设一返回就完事了。
        await WaitForRekeyAsync(host, expected: 1);

        SshCommandResult output = await host.Connection.RunAsync("查", cancellationToken: host.Token);
        Assert.AreEqual("主动换过了\n", output.StandardOutput, "我们发起的重协商之后连接还要能用");
        Assert.AreEqual(1, host.Channels.Observation.RekeysHandled, "服务端也应当认为谈成了一次");
    }

    [TestMethod]
    public async Task 重协商中再发起是空操作()
    {
        // 重复发 KEXINIT 是协议违规（对端会认为这是第二次重协商）。
        // 所以正在谈的时候再调一次必须什么都不做。
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("ok\n"),
            ExitCode = 0,
        });

        await host.Connection.StartRekeyAsync(host.Token);
        await host.Connection.StartRekeyAsync(host.Token);   // 这一次应当被忽略
        await host.Connection.StartRekeyAsync(host.Token);

        await WaitForRekeyAsync(host, expected: 1);

        // 只谈成一次 —— 要是三次 KEXINIT 都发出去了，这里会是 3，
        // 或者连接干脆已经挂了。
        Assert.AreEqual(1, host.Connection.RekeyCount);
        Assert.AreEqual(1, host.Channels.Observation.RekeysHandled);

        SshCommandResult output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
        Assert.AreEqual(0, output.ExitCode);
    }

    [TestMethod]
    public async Task 报文数到阈值会自己发起重协商()
    {
        // 这一条验的是**监视循环真的在盯**，不只是 StartRekeyAsync 能手动调。
        //
        // 用报文数那条阈值：字节与时长的下限是 64 MiB / 1 分钟，用例里够不着；
        // 报文数的下限是 1024，够得着。
        //
        // ⚠️ **怎么把报文数攒上去，试错了两次**：
        //   ① 反复跑命令 —— 每条是开通道+请求+数据+退出状态+关闭一整套往返，
        //      满跑并行时慢到撞 25 秒超时。
        //   ② 往 stdin 灌 4000 个一字节的块 —— stdin 泵会把它们合并，
        //      实测 4000 次写只产生了**一两个**报文（整条连接才 10 个）。
        //
        // 真正可控的是**对端的分片粒度**：服务端按我们宣告的 max packet 切分，
        // 所以把它调到 256 字节，再让服务端吐 300 KB，就是一千多个报文 ——
        // 便宜、确定，而且顺带验了「重协商发生在传输中途也不会弄坏数据」。
        const int maxPacket = 256;
        byte[] bulk = new byte[300 * 1024];
        Random.Shared.NextBytes(bulk);

        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript { StandardOutput = bulk, ExitCode = 0 },
            rekey: new SshRekeyPolicy(maxBytes: 0, maxPackets: SshRekeyPolicy.MinimumPackets),
            rekeyCheckInterval: TimeSpan.FromMilliseconds(30));

        Assert.AreEqual(0, host.Connection.RekeyCount);

        SshCommandOptions options = new()
        {
            Channel = SshChannelOptions.Default with { ReceiveMaxPacketBytes = maxPacket },
        };

        await using SshCommand command =
            await host.Connection.ExecuteAsync("灌", options, host.Token);

        long received = 0;
        while (true)
        {
            System.IO.Pipelines.ReadResult read =
                await command.StandardOutput.ReadAsync(host.Token);
            received += read.Buffer.Length;
            command.StandardOutput.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }

        Assert.AreEqual(bulk.Length, received, "重协商发生在传输中途也不能弄丢数据");
        Assert.IsGreaterThan(
            SshRekeyPolicy.MinimumPackets, host.Connection.PacketsReceived,
            $"前提：报文数要真的越过阈值，实际收了 {host.Connection.PacketsReceived} 个");

        await WaitForRekeyAsync(host, expected: 1);

        Assert.IsNotNull(host.Connection.LastRekeyReason, "主动发起时要说清是哪条阈值触发的");
        Assert.Contains(
"报文数", host.Connection.LastRekeyReason!,
            $"应当是报文数那条触发的，实际：{host.Connection.LastRekeyReason}");

        // 换完密钥连接还要能用。
        SshCommandResult after = await host.Connection.RunAsync("再来", cancellationToken: host.Token);
        Assert.AreEqual(0, after.ExitCode, "重协商之后连接还要能用");
    }

    [TestMethod]
    public void 阈值低于下限会被当场拒绝()
    {
        // 太频繁的重协商是一个自己给自己开的拒绝服务面（每次都要做非对称运算），
        // 所以下限不是建议值，是会抛异常的硬限制。
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshRekeyPolicy(maxBytes: 1024));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshRekeyPolicy(maxInterval: TimeSpan.FromSeconds(1)));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshRekeyPolicy(maxPackets: 8));

        // 默认值与「关掉」都必须合法（能构造出来本身就说明过了校验）。
        Assert.IsFalse(SshRekeyPolicy.Disabled.IsEnabled, "关掉之后不该有任何阈值是开的");
        Assert.IsTrue(SshRekeyPolicy.Default.IsEnabled, "默认必须是开着的 —— 它防的是 nonce 回绕");

        // 全零的结构体就该是「什么都不做」。第一版不是这样：MaxInterval 的
        // default 被翻译成 1 小时，于是 Disabled 里的时长那一条根本关不掉。
        SshRekeyPolicy zeroed = default;
        Assert.AreEqual(SshRekeyPolicy.Disabled, zeroed);
    }

    /// <summary>等到重协商真的谈完。</summary>
    /// <remarks>
    /// <c>StartRekeyAsync</c> 发完 KEXINIT 就返回，所以用例不能假设它一返回就谈完了。
    /// 这里轮询 <c>RekeyCount</c> —— 等的是一个**确定会发生的终止事件**，
    /// 超时只是防挂死的兜底。
    /// </remarks>
    private static async Task WaitForRekeyAsync(TestSshServerHost host, int expected)
    {
        for (int i = 0; i < 200 && host.Connection.RekeyCount < expected; i++)
        {
            await Task.Delay(25, host.Token);
        }

        Assert.AreEqual(
            expected, host.Connection.RekeyCount,
            $"等不到重协商完成 —— 要么 KEXINIT 没发出去，要么接收循环没接住。" +
            $"（这条连接至今发了 {host.Connection.PacketsSent} 个报文、收了 {host.Connection.PacketsReceived} 个）");
    }
}
