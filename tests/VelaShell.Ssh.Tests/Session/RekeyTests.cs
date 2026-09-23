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
        SshCommandOutput before = await host.Connection.RunAsync("一", cancellationToken: host.Token);
        Assert.AreEqual("第一次\n", before.StandardOutput);
        Assert.AreEqual(0, host.Connection.RekeyCount, "还没重协商过");

        SshNegotiatedAlgorithms? firstRound = host.Connection.Algorithms;
        Assert.IsNotNull(firstRound);

        // 服务端发起重协商 —— 这就是 OpenSSH 到了 RekeyLimit 时做的事。
        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        // ⚠️ 重协商完成之后**连接必须还能用**。这是整组用例的全部意义：
        // 换完密钥还能收发，才说明两边的新密钥真的对上了。
        SshCommandOutput after = await host.Connection.RunAsync("二", cancellationToken: host.Token);
        Assert.AreEqual("第一次\n", after.StandardOutput, "重协商之后数据还要一字节不差");
        Assert.AreEqual(0, after.ExitCode);

        Assert.AreEqual(1, host.Connection.RekeyCount, "应当记下发生过一次重协商");
        Assert.IsTrue(host.Connection.IsAlive, "重协商不该把连接弄坏");
    }

    [TestMethod]
    public async Task 重协商之后session_id不变()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("ok\n"),
            ExitCode = 0,
        });

        byte[] sessionIdBefore = [.. host.Connection.SessionId];

        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        // 〔RFC 4253 §7.2〕重协商时交换哈希 H 会变，但 session_id **永不改变**。
        // 混成一个字段的症状是「重协商之后再开新通道做公钥认证会失败」——
        // 一条极罕见的路径，所以这里钉住它。
        Assert.AreSequenceEqual(
            sessionIdBefore, [.. host.Connection.SessionId], "session_id 在重协商之后必须保持不变");

        SshCommandOutput output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
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
            host.Connection.Algorithms!.Value.CompressionServerToClient,
            "前提：压缩要真的谈成了，不然这条用例什么都没验");

        SshCommandOutput before = await host.Connection.RunAsync("压", cancellationToken: host.Token);
        Assert.AreSequenceEqual(compressible, Encoding.UTF8.GetBytes(before.StandardOutput));

        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        SshCommandOutput after = await host.Connection.RunAsync("压", cancellationToken: host.Token);
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

        (SshCommandResult result, string echoed, _) =
            await command.ReadToEndAsync(host.Token);

        Assert.AreEqual(
            Encoding.UTF8.GetString(payload), echoed,
            "重协商期间写的数据要一字节不差地按原顺序送达");
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, host.Connection.RekeyCount);
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

            SshCommandOutput output = await host.Connection.RunAsync("查", cancellationToken: host.Token);
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

        SshCommandOutput output = await host.Connection.RunAsync("查", cancellationToken: host.Token);
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

        SshCommandOutput output = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
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
            rekey: new SshRekeyPolicy(MaxBytes: 0, MaxPackets: SshRekeyPolicy.MinimumPackets),
            rekeyCheckInterval: TimeSpan.FromMilliseconds(30));

        Assert.AreEqual(0, host.Connection.RekeyCount);

        SshExecutionOptions options = new()
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
        SshCommandOutput after = await host.Connection.RunAsync("再来", cancellationToken: host.Token);
        Assert.AreEqual(0, after.ExitCode, "重协商之后连接还要能用");
    }

    [TestMethod]
    public void 阈值低于下限会被当场拒绝()
    {
        // 太频繁的重协商是一个自己给自己开的拒绝服务面（每次都要做非对称运算），
        // 所以下限不是建议值，是会抛异常的硬限制。
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshRekeyPolicy(MaxBytes: 1024).Validate());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshRekeyPolicy(MaxInterval: TimeSpan.FromSeconds(1)).Validate());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshRekeyPolicy(MaxPackets: 8).Validate());

        // 默认值与「关掉」都必须合法。
        SshRekeyPolicy.Default.Validate();
        SshRekeyPolicy.Disabled.Validate();
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
