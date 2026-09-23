// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 同一条连接上多条通道交替开关时，别的通道不受影响。
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §1

using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
public sealed class ChannelInterleavingTests
{
    /// <summary>
    /// 终端产品的常见形态：一个交互 shell 常驻，旁边不停地开一次性 exec 通道做探测。
    /// 每一轮探测开完关完，shell 都必须照常有来有回。
    /// </summary>
    [TestMethod]
    public async Task exec通道反复开关之后交互shell照常有回显()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            EchoStandardInput = true,
            WaitForClientEof = true,
            ExitCode = 0,
        });

        await using SshShell shell = await host.Connection.OpenShellAsync(cancellationToken: host.Token);

        for (int round = 0; round < 40; round++)
        {
            // 一次性探测：开、跑、关。
            SshCommandOutput probe = await host.Connection.RunAsync($"probe {round}", cancellationToken: host.Token);
            Assert.AreEqual(0, probe.ExitCode, $"第 {round} 轮探测没有正常结束");

            // shell 这边还得有来有回。
            string marker = $"ping-{round}\n";
            await shell.Input.WriteAsync(Encoding.ASCII.GetBytes(marker), host.Token);

            string echoed = await ReadAtLeastAsync(shell.Output, marker.Length, host.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), host.Token);
            Assert.AreEqual(marker, echoed, $"第 {round} 轮之后 shell 没有回显");
        }
    }

    /// <summary>
    /// <c>await</c> 回来之后<b>同步阻塞</b>当前线程（测试框架的轮询、UI 的同步等待都是这个形态）：
    /// 这时接收循环必须还在别的线程上照常收包。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 回归自 host 的集成测试：exec 探针的通道一关，接收循环在处理 <c>CHANNEL_CLOSE</c> 时
    /// 调了 <c>CancellationTokenSource.Cancel()</c> —— 它<b>同步</b>执行回调，
    /// 一串同步完成的续体（通道泵结束 → 探针的 DisposeAsync 恢复 → RunAsync 返回 → 调用方）
    /// 就在<b>接收循环的线程上</b>跑了起来。调用方接着 <c>Thread.Sleep</c> 轮询，
    /// 接收循环于是被整个挂住，shell 的回显躺在套接字里没人读。
    /// </para>
    /// <para>
    /// 规则：<b>接收循环与发送泵上绝不执行使用者的代码</b>（velashell-docs/zh/ssh/spec/05 §8）。
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task 调用方在await之后同步阻塞时接收循环不受影响()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            EchoStandardInput = true,
            WaitForClientEof = true,
            ExitCode = 0,
        });

        await using SshShell shell = await host.Connection.OpenShellAsync(cancellationToken: host.Token);

        for (int round = 0; round < 20; round++)
        {
            SshCommandOutput probe = await host.Connection.RunAsync($"probe {round}", cancellationToken: host.Token);
            Assert.AreEqual(0, probe.ExitCode);

            // 从这里起是同步代码 —— 不 await，就在 RunAsync 的续体所在的线程上阻塞着等回显。
            string marker = $"sync-{round}\n";
            _ = shell.Input.WriteAsync(Encoding.ASCII.GetBytes(marker), host.Token).AsTask();

            string echoed = "";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (shell.Output.TryRead(out ReadResult read))
                {
                    if (read.Buffer.Length >= marker.Length)
                    {
                        echoed = Encoding.ASCII.GetString(read.Buffer.Slice(0, marker.Length));
                        shell.Output.AdvanceTo(read.Buffer.GetPosition(marker.Length));
                        break;
                    }
                    shell.Output.AdvanceTo(read.Buffer.Start, read.Buffer.End);
                }
                Thread.Sleep(20);
            }

            Assert.AreEqual(marker, echoed, $"第 {round} 轮：调用方同步阻塞时收不到回显 —— 接收循环被挂在了调用方的续体上");
        }
    }

    private static async Task<string> ReadAtLeastAsync(PipeReader reader, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken);
            if (read.Buffer.Length >= count)
            {
                string text = Encoding.ASCII.GetString(read.Buffer.Slice(0, count));
                reader.AdvanceTo(read.Buffer.GetPosition(count));
                return text;
            }

            if (read.IsCompleted)
            {
                throw new AssertFailedException("shell 的输出提前结束了。");
            }

            reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
        }
    }
}
