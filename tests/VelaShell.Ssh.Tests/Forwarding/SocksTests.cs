// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §三
//
// SOCKS 握手也是纯协议层 —— 不需要 SSH，不需要 socket。

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class SocksTests
{
    /// <summary>把一段请求字节喂进握手，拿回目标与服务端写出的应答。</summary>
    private static async Task<(SocksTarget? Target, byte[] Reply)> RunAsync(byte[] request)
    {
        Pipe inbound = new(new PipeOptions(useSynchronizationContext: false));
        Pipe outbound = new(new PipeOptions(useSynchronizationContext: false));

        await inbound.Writer.WriteAsync(request);
        inbound.Writer.Complete();

        SocksTarget? target = await SocksHandshake.ReadRequestAsync(inbound.Reader, outbound.Writer);
        outbound.Writer.Complete();

        ArrayBufferWriter<byte> reply = new();
        while (true)
        {
            ReadResult read = await outbound.Reader.ReadAsync();
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
            {
                reply.Write(segment.Span);
            }
            outbound.Reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }

        return (target, reply.WrittenSpan.ToArray());
    }

    private static byte[] Greeting() => [0x05, 0x01, 0x00];

    [TestMethod]
    public async Task 域名目标原样交给服务端不在本地解析()
    {
        byte[] host = Encoding.ASCII.GetBytes("internal.corp.example");
        byte[] request =
        [
            .. Greeting(),
            0x05, 0x01, 0x00, 0x03,     // VER ‖ CONNECT ‖ RSV ‖ ATYP=域名
            (byte)host.Length, .. host,
            0x01, 0xBB,                 // 端口 443
        ];

        (SocksTarget? target, byte[] reply) = await RunAsync(request);

        // 〔决策〕**域名不在本地解析。**这是动态转发最重要的一条语义 ——
        // curl --socks5-hostname 依赖它。本地解析会导致
        // 「DNS 走本地、连接走隧道」的分裂，在内网域名场景下直接失效，
        // 而且泄漏了访问目标。
        Assert.IsNotNull(target);
        Assert.AreEqual("internal.corp.example", target.Value.Host);
        Assert.AreEqual(443, target.Value.Port);

        CollectionAssert.AreEqual(new byte[] { 0x05, 0x00 }, reply, "只该回一个方法协商应答");
    }

    [TestMethod]
    public async Task IPv4目标()
    {
        byte[] request =
        [
            .. Greeting(),
            0x05, 0x01, 0x00, 0x01,
            10, 0, 0, 9,
            0x00, 0x50,                 // 端口 80
        ];

        (SocksTarget? target, _) = await RunAsync(request);

        Assert.IsNotNull(target);
        Assert.AreEqual("10.0.0.9", target.Value.Host);
        Assert.AreEqual(80, target.Value.Port);
    }

    [TestMethod]
    public async Task IPv6目标()
    {
        byte[] address = [0x20, 0x01, 0x0d, 0xb8, .. new byte[11], 0x01];
        byte[] request =
        [
            .. Greeting(),
            0x05, 0x01, 0x00, 0x04,
            .. address,
            0x1F, 0x90,                 // 端口 8080
        ];

        (SocksTarget? target, _) = await RunAsync(request);

        Assert.IsNotNull(target);
        Assert.AreEqual("2001:db8::1", target.Value.Host);
        Assert.AreEqual(8080, target.Value.Port);
        Assert.AreEqual(0x04, target.Value.AddressType);
    }

    [TestMethod]
    public async Task 不是SOCKS5时直接断开而不是按5回应答()
    {
        // SOCKS4 的第一个字节是 0x04。按 SOCKS5 的格式回应答，
        // 会让对面按错误的格式解析 —— 那比不回更糟。
        (SocksTarget? target, byte[] reply) = await RunAsync([0x04, 0x01, 0x00, 0x50]);

        Assert.IsNull(target);
        Assert.AreEqual(0, reply.Length, "不认识的版本不回任何东西");
    }

    [TestMethod]
    public async Task 只接受无认证()
    {
        // 客户端只提供「用户名密码」认证（0x02）。
        (SocksTarget? target, byte[] reply) = await RunAsync([0x05, 0x01, 0x02]);

        Assert.IsNull(target);
        CollectionAssert.AreEqual(new byte[] { 0x05, 0xFF }, reply, "回「没有可接受的方法」");
    }

    [TestMethod]
    public async Task BIND与UDP命令回命令不支持()
    {
        byte[] request =
        [
            .. Greeting(),
            0x05, 0x02, 0x00, 0x01,     // CMD = BIND
            127, 0, 0, 1,
            0x00, 0x50,
        ];

        (SocksTarget? target, byte[] reply) = await RunAsync(request);

        Assert.IsNull(target);

        // 回 0x07 而不是沉默 —— 让对面知道是「不支持」而不是「连不上」。
        Assert.AreEqual(0x05, reply[2 + 0]);
        Assert.AreEqual((byte)SocksReply.CommandNotSupported, reply[2 + 1]);
    }

    [TestMethod]
    public async Task 握手中途断开不抛异常()
    {
        // 只发了半个问候。对面随时可能走，这不是异常路径。
        (SocksTarget? target, _) = await RunAsync([0x05]);
        Assert.IsNull(target);
    }

    [TestMethod]
    public void 失败原因被如实映射而不是一律回一般性失败()
    {
        // 应答码对不对是有实际后果的：curl 与浏览器会据此决定要不要重试、
        // 以及报给用户哪句话。
        Assert.AreEqual(
            SocksReply.NotAllowed,
            SocksHandshake.MapFailure(SshChannelOpenFailureReason.AdministrativelyProhibited),
            "服务端禁了转发 → 「规则不允许」，而不是「连接被拒」");

        Assert.AreEqual(
            SocksReply.ConnectionRefused,
            SocksHandshake.MapFailure(SshChannelOpenFailureReason.ConnectFailed),
            "目标连不上 → 「连接被拒」");

        Assert.AreEqual(
            SocksReply.GeneralFailure,
            SocksHandshake.MapFailure(SshChannelOpenFailureReason.ResourceShortage));
    }

    [TestMethod]
    public async Task 应答里带回正确的地址类型()
    {
        Pipe outbound = new(new PipeOptions(useSynchronizationContext: false));

        await SocksHandshake.WriteReplyAsync(outbound.Writer, SocksReply.Succeeded, addressType: 0x04);
        outbound.Writer.Complete();

        ReadResult read = await outbound.Reader.ReadAsync();
        byte[] reply = read.Buffer.ToArray();

        Assert.AreEqual(0x05, reply[0]);
        Assert.AreEqual(0x00, reply[1]);
        Assert.AreEqual(0x04, reply[3], "IPv6 的请求要回 IPv6 形状的应答");
        Assert.AreEqual(4 + 16 + 2, reply.Length);
    }
}
