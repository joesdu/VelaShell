// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/design/architecture.md §10.2
//           半关闭语义: velashell-docs/zh/ssh/spec/07-forwarding.md §2.2

using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Transport;

[TestClass]
[TestCategory("Transport")]
public sealed class InMemoryTransportTests
{
    private static async Task<byte[]> ReadExactlyAsync(Stream s, int count, CancellationToken ct = default)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                throw new EndOfStreamException($"期望 {count} 字节，只读到 {read}。");
            }
            read += n;
        }
        return buffer;
    }

    // ------------------------------------------------------------ 基本连通

    [TestMethod]
    public async Task 一端写出的字节另一端读得到()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            await a.WriteAsync("hello"u8.ToArray());
            Assert.AreSequenceEqual("hello"u8.ToArray(), await ReadExactlyAsync(b, 5));
        }
    }

    [TestMethod]
    public async Task 双向独立()
    {
        // 两个方向是两条管道，互不干扰。共用一条会在双向同时传输时串话。
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            await a.WriteAsync("a->b"u8.ToArray());
            await b.WriteAsync("b->a"u8.ToArray());

            Assert.AreSequenceEqual("a->b"u8.ToArray(), await ReadExactlyAsync(b, 4));
            Assert.AreSequenceEqual("b->a"u8.ToArray(), await ReadExactlyAsync(a, 4));
        }
    }

    [TestMethod]
    public async Task 多次写入按顺序到达()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            for (int i = 0; i < 100; i++)
            {
                await a.WriteAsync(BitConverter.GetBytes(i));
            }

            byte[] all = await ReadExactlyAsync(b, 400);
            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(i, BitConverter.ToInt32(all, i * 4), $"第 {i} 个整数错位");
            }
        }
    }

    [TestMethod]
    public async Task 读取可以少于请求的长度()
    {
        // Stream 的契约：ReadAsync 返回的字节数可以少于请求。
        // 按「一次读满」写的调用方在真实网络上一定会出错，这里要能复现那个条件。
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            await a.WriteAsync("abc"u8.ToArray());
            byte[] buffer = new byte[1024];
            int n = await b.ReadAsync(buffer);
            Assert.AreEqual(3, n);
        }
    }

    // ------------------------------------------------------------ 半关闭

    [TestMethod]
    public async Task 关闭写端之后对端读到EOF()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            await a.WriteAsync("bye"u8.ToArray());
            a.CompleteWrites();

            Assert.AreSequenceEqual("bye"u8.ToArray(), await ReadExactlyAsync(b, 3));
            Assert.AreEqual(0, await b.ReadAsync(new byte[16]), "读完已发出的数据后应当是干净的 EOF");
        }
    }

    [TestMethod]
    public async Task 半关闭之后反方向仍然可用()
    {
        // 这条是转发路径的正确性要害（spec/07 §2.2）：
        // 把 EOF 当成「连接结束」会截断数据。典型症状是 curl 通过隧道 POST 完请求体
        // 后等响应，而我们在它关写端时把整条通道关了，响应永远收不到。
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            a.CompleteWrites();
            Assert.AreEqual(0, await b.ReadAsync(new byte[16]));

            // b 仍然能写，a 仍然能读
            Assert.IsTrue(b.CanWrite);
            Assert.IsTrue(a.CanRead);
            await b.WriteAsync("response"u8.ToArray());
            Assert.AreSequenceEqual("response"u8.ToArray(), await ReadExactlyAsync(a, 8));
        }
    }

    [TestMethod]
    public async Task 重复关闭写端是空操作()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            a.CompleteWrites();
            a.CompleteWrites();
            Assert.IsFalse(a.CanWrite);
        }
    }

    [TestMethod]
    public async Task 关闭写端后再写抛出()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            a.CompleteWrites();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await a.WriteAsync("x"u8.ToArray()));
        }
    }

    // ------------------------------------------------------------ 背压

    [TestMethod]
    public async Task 对端不读时写入被挡住()
    {
        // 背压是结构性的（架构原则 2）。这条用例在真实网络上极难稳定复现 ——
        // 而内存传输可以把水位调到几百字节，让它变成一个确定性的测试。
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair(
            new InMemoryTransportOptions { PauseWriterThreshold = 1024, ResumeWriterThreshold = 512 });

        await using (a)
        await using (b)
        {
            byte[] chunk = new byte[512];

            // 第一次：缓冲 512，未达水位，立即返回。
            await a.WriteAsync(chunk);

            // 第二次：缓冲到 1024，**达到** PauseWriterThreshold —— 从这一刻起写入方被挂起。
            // 注意是「达到」不是「超过」：写成 await 会让这一行自己永久阻塞。
            Task blocked = a.WriteAsync(chunk).AsTask();
            await Task.Delay(50);
            Assert.IsFalse(blocked.IsCompleted, "达到 PauseWriterThreshold 之后写入应当被挂起");

            // 对端读走之后降到 ResumeWriterThreshold 以下，写入方恢复。
            // 这 1024 字节**都已经在缓冲里**（WriteAsync 是先拷贝再等刷出）。
            _ = await ReadExactlyAsync(b, 1024);
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(blocked.IsCompletedSuccessfully);
        }
    }

    // ------------------------------------------------------------ 取消与释放

    [TestMethod]
    public async Task 读取可被取消()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            using CancellationTokenSource cts = new();
            Task<int> pending = b.ReadAsync(new byte[16], cts.Token).AsTask();
            await Task.Delay(20);
            Assert.IsFalse(pending.IsCompleted);

            await cts.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await pending);
        }
    }

    [TestMethod]
    public async Task 释放一端后对端读到EOF()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (b)
        {
            await a.DisposeAsync();
            Assert.AreEqual(0, await b.ReadAsync(new byte[16]));
        }
    }

    [TestMethod]
    public async Task 释放后再用抛出ObjectDisposedException()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (b)
        {
            await a.DisposeAsync();
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            {
                // 读取结果要用上：丢弃 ReadAsync 的返回值正是 CA2022 要防的那个经典 bug，
                // 而且这里用上它还能给出一句有用的失败消息。
                int read = await a.ReadAsync(new byte[16]);
                Assert.Fail($"应当抛出 ObjectDisposedException，却读到了 {read} 字节。");
            });
        }
    }

    [TestMethod]
    public async Task 重复释放是空操作()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await a.DisposeAsync();
        await a.DisposeAsync();
        a.Dispose();
        await b.DisposeAsync();
    }

    // ------------------------------------------------------------ 同步 API

    [TestMethod]
    public async Task 同步读写不被支持()
    {
        // 架构原则 1：异步是唯一形态。提供一个会阻塞线程池线程的同步壳，
        // 只会让使用者在不该同步的地方同步。
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        await using (b)
        {
            Assert.ThrowsExactly<NotSupportedException>(() => a.Read(new byte[1], 0, 1));
            Assert.ThrowsExactly<NotSupportedException>(() => a.Write(new byte[1], 0, 1));
            Assert.ThrowsExactly<NotSupportedException>(() => a.Seek(0, SeekOrigin.Begin));
            Assert.ThrowsExactly<NotSupportedException>(() => a.SetLength(0));
            a.Flush();   // 空操作，不该抛
        }
    }

    // ------------------------------------------------------------ 拨号器

    [TestMethod]
    public async Task 拨号器把服务端那一端交给回调()
    {
        SshDialTarget? seen = null;
        InMemoryDuplexStream? serverSide = null;

        ISshTransportDialer dialer = InMemoryTransport.CreateDialer((server, target, _) =>
        {
            serverSide = server;
            seen = target;
            return ValueTask.CompletedTask;
        });

        Assert.AreEqual(SshDialKind.InMemory, dialer.Kind);

        SshDialTarget target = SshDialTarget.Direct("example.com", 22);
        await using Stream client = await dialer.DialAsync(target);

        Assert.IsNotNull(seen);
        Assert.AreEqual("example.com", seen.EndPoint.Host);
        Assert.AreEqual(22, seen.EndPoint.Port);
        Assert.AreEqual(seen.EndPoint, seen.FinalDestination, "直连时这一跳就是最终目标");

        Assert.IsNotNull(serverSide);
        await using (serverSide)
        {
            await client.WriteAsync("SSH-2.0-Test\r\n"u8.ToArray());
            Assert.AreSequenceEqual("SSH-2.0-Test\r\n"u8.ToArray(), await ReadExactlyAsync(serverSide, 14));
        }
    }

    [TestMethod]
    public void 端点的字符串形式给IPv6加方括号()
    {
        Assert.AreEqual("example.com:22", new SshEndPoint("example.com", 22).ToString());
        Assert.AreEqual("[::1]:22", new SshEndPoint("::1", 22).ToString());
    }
}
