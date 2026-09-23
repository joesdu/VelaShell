// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.5 / §7.5.6 / §7.5.7

using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class X11PrimitiveTests
{
    // ------------------------------------------------------------ DISPLAY 解析

    [TestMethod]
    public void 解析常见的DISPLAY形态()
    {
        AssertDisplay(":0", host: "", number: 0, screen: 0);
        AssertDisplay(":10.2", host: "", number: 10, screen: 2);
        AssertDisplay("unix:0", host: "unix", number: 0, screen: 0);
        AssertDisplay("localhost:11", host: "localhost", number: 11, screen: 0);
        AssertDisplay("box.example.com:0.1", host: "box.example.com", number: 0, screen: 1);

        // IPv6 要按**最后一个**冒号切，否则地址本身的冒号会把它切碎。
        AssertDisplay("[::1]:0", host: "::1", number: 0, screen: 0);
    }

    [TestMethod]
    public void 解析不了的DISPLAY返回null而不是抛()
    {
        // DISPLAY 没设、或者设成奇怪的值，是很常见的状态 ——
        // 调用方要的是「能不能用」，不是一个异常。
        Assert.IsNull(X11Display.Parse(null));
        Assert.IsNull(X11Display.Parse(""));
        Assert.IsNull(X11Display.Parse("   "));
        Assert.IsNull(X11Display.Parse("没有冒号"));
        Assert.IsNull(X11Display.Parse(":"));
        Assert.IsNull(X11Display.Parse(":abc"));
        Assert.IsNull(X11Display.Parse(":-1"));
        Assert.IsNull(X11Display.Parse(":0.abc"));
    }

    [TestMethod]
    public void MacOS的launchd套接字路径按路径处理()
    {
        // launchd 会把 DISPLAY 设成一个套接字路径。按 host:N 去切会得到
        // 一个荒谬的「主机名」，然后连不上。
        X11Display? display = X11Display.Parse(
            "/private/tmp/com.apple.launchd.AbC/org.xquartz:0");

        Assert.IsNotNull(display);
        Assert.IsTrue(display.IsLocal);
        Assert.AreEqual("/private/tmp/com.apple.launchd.AbC/org.xquartz:0", display.UnixSocketPath);
    }

    [TestMethod]
    public void 本机显示的候选端点包含套接字与回环TCP()
    {
        X11Display display = X11Display.Parse(":0")!;
        IReadOnlyList<EndPoint> candidates = display.GetCandidateEndPoints();

        // 至少要有回环 TCP —— Windows 上的 VcXsrv 只听这个。
        Assert.Contains(
            e =>
                e.Address.Equals(IPAddress.Loopback) && e.Port == X11Display.TcpPortBase, candidates.OfType<IPEndPoint>(),
            "本机显示 :0 应当能走 127.0.0.1:6000");

        if (!OperatingSystem.IsWindows() && Socket.OSSupportsUnixDomainSockets)
        {
            Assert.IsNotEmpty(
                candidates.OfType<UnixDomainSocketEndPoint>(),
                "非 Windows 上应当先试 Unix 套接字");
        }
    }

    [TestMethod]
    public void 远程显示只走TCP且端口是6000加显示号()
    {
        X11Display display = X11Display.Parse("box.example.com:7")!;

        Assert.IsFalse(display.IsLocal);
        IReadOnlyList<EndPoint> candidates = display.GetCandidateEndPoints();

        DnsEndPoint only = (DnsEndPoint)candidates.Single();
        Assert.AreEqual("box.example.com", only.Host);
        Assert.AreEqual(X11Display.TcpPortBase + 7, only.Port);
    }

    // ------------------------------------------------------------ .Xauthority

    [TestMethod]
    public void 解析Xauthority并按显示号与主机名匹配()
    {
        byte[] cookie = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] other = [9, 9, 9, 9];

        byte[] file =
        [
            .. Entry(XAuthority.FamilyLocal, "别的机器", "0", XAuthority.MitMagicCookie1, other),
            .. Entry(XAuthority.FamilyLocal, Environment.MachineName, "0", XAuthority.MitMagicCookie1, cookie),
        ];

        IReadOnlyList<XAuthorityEntry> entries = XAuthority.Parse(file);
        Assert.HasCount(2, entries);

        byte[]? found = XAuthority.FindCookie(entries, X11Display.Parse(":0")!);
        Assert.AreSequenceEqual(cookie, found, "应当挑本机主机名那一条");
    }

    [TestMethod]
    public void 只认MIT_MAGIC_COOKIE_1()
    {
        // XDM-AUTHORIZATION-1 一律跳过 —— 与 OpenSSH 一致。
        byte[] file = Entry(
            XAuthority.FamilyWild, "", "0", "XDM-AUTHORIZATION-1", [1, 2, 3, 4]);

        Assert.IsNull(
            XAuthority.FindCookie(XAuthority.Parse(file), X11Display.Parse(":0")!),
            "不支持的授权协议不该被当成可用的 cookie");
    }

    [TestMethod]
    public void 显示号对不上就不匹配()
    {
        byte[] file = Entry(
            XAuthority.FamilyWild, "", "3", XAuthority.MitMagicCookie1, [1, 2, 3, 4]);

        Assert.IsNull(XAuthority.FindCookie(XAuthority.Parse(file), X11Display.Parse(":0")!));
        Assert.IsNotNull(XAuthority.FindCookie(XAuthority.Parse(file), X11Display.Parse(":3")!));
    }

    [TestMethod]
    public void 截断的Xauthority不抛异常且前面的记录仍然可用()
    {
        // 文件可能正被别的程序写入。一个半截的文件不该让整条连接失败。
        byte[] good = Entry(
            XAuthority.FamilyWild, "", "0", XAuthority.MitMagicCookie1, [7, 7, 7, 7]);

        byte[] file = [.. good, .. good.AsSpan(0, good.Length / 2)];

        IReadOnlyList<XAuthorityEntry> entries = XAuthority.Parse(file);
        Assert.HasCount(1, entries, "截断处之前的记录要保留");
        Assert.AreSequenceEqual(
            new byte[] { 7, 7, 7, 7 }, XAuthority.FindCookie(entries, X11Display.Parse(":0")!));
    }

    // ------------------------------------------------------------ 连接建立报文

    [TestMethod]
    public void 两种字节序的建立报文都能解()
    {
        // ⚠️ 只按一种字节序解析的症状是「某些客户端能连，某些连不上」——
        //    而那看上去完全像随机故障。
        foreach (bool bigEndian in new[] { true, false })
        {
            byte[] cookie = [0xAA, 0xBB, 0xCC, 0xDD];
            byte[] message = BuildSetup(bigEndian, XAuthority.MitMagicCookie1, cookie);

            Assert.IsTrue(
                X11SetupMessage.TryParse(new ReadOnlySequence<byte>(message), out X11SetupMessage.Parsed parsed),
                $"bigEndian={bigEndian} 的报文应当能解");

            Assert.AreEqual(bigEndian, parsed.BigEndian);
            Assert.AreEqual(XAuthority.MitMagicCookie1, parsed.ProtocolName);
            Assert.AreSequenceEqual(cookie, parsed.ProtocolData);
            Assert.AreEqual(message.Length, parsed.TotalLength);
        }
    }

    [TestMethod]
    public void 报文没收全时返回false而不是报错()
    {
        // 建立报文可能分几次到达。「还没收全」不是错误。
        byte[] message = BuildSetup(true, XAuthority.MitMagicCookie1, [1, 2, 3, 4]);

        for (int partial = 0; partial < message.Length; partial++)
        {
            Assert.IsFalse(
                X11SetupMessage.TryParse(new ReadOnlySequence<byte>(message.AsMemory(0, partial)), out _),
                $"只收到 {partial} 字节时应当继续等");
        }

        Assert.IsTrue(X11SetupMessage.TryParse(new ReadOnlySequence<byte>(message), out _));
    }

    [TestMethod]
    public void 字节序标记非法要明确报错()
    {
        byte[] message = BuildSetup(true, XAuthority.MitMagicCookie1, [1, 2, 3, 4]);
        message[0] = (byte)'?';

        FormatException error = Assert.ThrowsExactly<FormatException>(
            () => X11SetupMessage.TryParse(new ReadOnlySequence<byte>(message), out _));

        Assert.Contains("字节序", error.Message);
    }

    [TestMethod]
    public void 假cookie对上了才换成真cookie()
    {
        byte[] fake = X11SetupMessage.CreateFakeCookie();
        byte[] real = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];

        byte[] message = BuildSetup(true, XAuthority.MitMagicCookie1, fake);
        Assert.IsTrue(X11SetupMessage.TryParse(new ReadOnlySequence<byte>(message), out X11SetupMessage.Parsed parsed));

        Assert.IsTrue(
            X11SetupMessage.TryRewriteCookie(message, parsed, fake, real, out byte[] rewritten),
            "假 cookie 对上了就应当放行");

        // 换出来的报文要能再解一遍，而且里面是**真** cookie。
        Assert.IsTrue(X11SetupMessage.TryParse(new ReadOnlySequence<byte>(rewritten), out X11SetupMessage.Parsed again));
        Assert.AreSequenceEqual(real, again.ProtocolData, "转给本机 X server 的必须是真 cookie");
        Assert.AreEqual(XAuthority.MitMagicCookie1, again.ProtocolName);
        Assert.IsTrue(again.BigEndian);
    }

    [TestMethod]
    public void 假cookie对不上就拒绝()
    {
        // ⚠️ 这是 X11 转发**唯一**的门。放过去就等于把本机显示交给任何人。
        byte[] fake = X11SetupMessage.CreateFakeCookie();
        byte[] wrong = X11SetupMessage.CreateFakeCookie();
        byte[] real = [1, 2, 3, 4];

        byte[] message = BuildSetup(false, XAuthority.MitMagicCookie1, wrong);
        Assert.IsTrue(X11SetupMessage.TryParse(new ReadOnlySequence<byte>(message), out X11SetupMessage.Parsed parsed));

        Assert.IsFalse(
            X11SetupMessage.TryRewriteCookie(message, parsed, fake, real, out _),
            "cookie 不对必须拒绝");
    }

    [TestMethod]
    public void 别的授权协议一律拒绝()
    {
        byte[] fake = X11SetupMessage.CreateFakeCookie();
        byte[] message = BuildSetup(true, "XDM-AUTHORIZATION-1", fake);

        Assert.IsTrue(X11SetupMessage.TryParse(new ReadOnlySequence<byte>(message), out X11SetupMessage.Parsed parsed));
        Assert.IsFalse(
            X11SetupMessage.TryRewriteCookie(message, parsed, fake, [1, 2, 3, 4], out _),
            "即使 cookie 字节相同，非 MIT-MAGIC-COOKIE-1 也不该放行");
    }

    [TestMethod]
    public void 假cookie是随机的且够长()
    {
        byte[] a = X11SetupMessage.CreateFakeCookie();
        byte[] b = X11SetupMessage.CreateFakeCookie();

        Assert.HasCount(16, a);
        Assert.IsFalse(a.SequenceEqual(b), "两次生成不该相同");
    }

    [TestMethod]
    public void Cookie按十六进制文本发出去()
    {
        // ⚠️ x11-req 的 cookie 字段是**十六进制字符串**，不是原始字节。
        //    发原始字节的症状是远端 xauth 存进去的和我们校验的对不上。
        Assert.AreEqual("00ff10", X11SetupMessage.ToHex([0x00, 0xFF, 0x10]));
    }

    // ------------------------------------------------------------ 辅助

    private static void AssertDisplay(string value, string host, int number, int screen)
    {
        X11Display? display = X11Display.Parse(value);
        Assert.IsNotNull(display, $"应当能解析 {value}");
        Assert.AreEqual(host, display.Host, $"{value} 的 host");
        Assert.AreEqual(number, display.Number, $"{value} 的显示号");
        Assert.AreEqual(screen, display.Screen, $"{value} 的屏幕号");
    }

    /// <summary>拼一条 <c>.Xauthority</c> 记录。</summary>
    private static byte[] Entry(int family, string address, string number, string name, byte[] data)
    {
        ArrayBufferWriter<byte> buffer = new();

        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)family);
        buffer.Write(two);

        WriteBlock(buffer, Encoding.ASCII.GetBytes(address));
        WriteBlock(buffer, Encoding.ASCII.GetBytes(number));
        WriteBlock(buffer, Encoding.ASCII.GetBytes(name));
        WriteBlock(buffer, data);

        return buffer.WrittenSpan.ToArray();

        static void WriteBlock(ArrayBufferWriter<byte> target, byte[] value)
        {
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length);
            target.Write(length);
            target.Write(value);
        }
    }

    /// <summary>拼一个 X11 连接建立报文。</summary>
    private static byte[] BuildSetup(bool bigEndian, string protocolName, byte[] cookie)
    {
        byte[] name = Encoding.ASCII.GetBytes(protocolName);
        int paddedName = (name.Length + 3) & ~3;
        int paddedData = (cookie.Length + 3) & ~3;

        byte[] message = new byte[X11SetupMessage.HeaderLength + paddedName + paddedData];
        message[0] = bigEndian ? (byte)'B' : (byte)'l';

        WriteUInt16(message.AsSpan(2), 11, bigEndian);   // protocol major
        WriteUInt16(message.AsSpan(4), 0, bigEndian);    // protocol minor
        WriteUInt16(message.AsSpan(6), (ushort)name.Length, bigEndian);
        WriteUInt16(message.AsSpan(8), (ushort)cookie.Length, bigEndian);

        name.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength));
        cookie.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength + paddedName));

        return message;

        static void WriteUInt16(Span<byte> span, ushort value, bool bigEndian)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(span, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span, value);
            }
        }
    }
}
