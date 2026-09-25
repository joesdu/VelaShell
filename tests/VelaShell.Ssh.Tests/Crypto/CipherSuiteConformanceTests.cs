// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/01-transport-framing.md §1（帧结构与填充）、§2（三种形状）、§5（边界）
//
// 这一套是**所有密码套件共用的契约测试**：新增一个套件就在 AllSuites 里加一行，
// 下面这些性质自动适用于它。
//
// 原语本身（ChaCha20、Poly1305、AES-GCM）不在这里测 —— 它们来自 BouncyCastle 与 BCL，
// 各有自己的测试套件。这里测的是**我们拥有的那一层**：分帧、填充、对齐、
// 长度校验、先验后解的顺序、以及「NeedMoreData 时不得改动状态」。

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.Crypto;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class CipherSuiteConformanceTests
{
    private const int MaxPacket = SshPacketFormat.DefaultMaxPacketLength;

    /// <summary>造一对（发送用、接收用）同密钥的套件。</summary>
    private delegate (ISshCipherSuite Sender, ISshCipherSuite Receiver) SuiteFactory();

    private static IEnumerable<(string Name, SuiteFactory Factory)> AllSuites()
    {
        yield return ("plaintext", static () => (new PlaintextCipherSuite(), new PlaintextCipherSuite()));

        yield return ("aes128-gcm", static () =>
        {
            byte[] key = RandomNumberGenerator.GetBytes(16);
            byte[] iv = RandomNumberGenerator.GetBytes(12);
            return (new AesGcmCipherSuite(key, iv), new AesGcmCipherSuite(key, iv));
        }
        );

        yield return ("aes256-gcm", static () =>
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] iv = RandomNumberGenerator.GetBytes(12);
            return (new AesGcmCipherSuite(key, iv), new AesGcmCipherSuite(key, iv));
        }
        );

        yield return ("chacha20-poly1305", static () =>
        {
            byte[] material = RandomNumberGenerator.GetBytes(ChaCha20Poly1305CipherSuite.KeyMaterialBytes);
            return (new ChaCha20Poly1305CipherSuite(material),
                    new ChaCha20Poly1305CipherSuite(material));
        }
        );

        // AES-CTR × {EtM, MtE} × {SHA-1, SHA-256, SHA-512}：
        // 形状差异全在这几组里（长度字段加不加密、进不进对齐、MAC 多长）。
        foreach (int keyBytes in new[] { 16, 32 })
        {
            foreach (SshMacAlgorithm mac in new[]
                     { SshMacAlgorithm.HmacSha1, SshMacAlgorithm.HmacSha256, SshMacAlgorithm.HmacSha512 })
            {
                foreach (bool etm in new[] { true, false })
                {
                    int kb = keyBytes;
                    SshMacAlgorithm m = mac;
                    bool e = etm;
                    string label = $"aes{kb * 8}-ctr+{m}{(e ? "-etm" : "")}";

                    yield return (label, () =>
                    {
                        byte[] key = RandomNumberGenerator.GetBytes(kb);
                        byte[] iv = RandomNumberGenerator.GetBytes(16);
                        byte[] macKey = RandomNumberGenerator.GetBytes(64);
                        return (new AesCtrHmacCipherSuite(key, iv, m, macKey, e),
                                new AesCtrHmacCipherSuite(key, iv, m, macKey, e));
                    }
                    );
                }
            }
        }
    }

    private static byte[] Seal(ISshCipherSuite suite, ReadOnlySpan<byte> payload, uint seq)
    {
        ArrayBufferWriter<byte> writer = new();
        suite.Seal(payload, seq, writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// CTR 的密钥流用独立的参照算出来比：128 位计数器用 BigInteger 数，密钥流用 BCL 的 AES-ECB 逐块生成。
    /// </summary>
    /// <remarks>
    /// 往返测试证明不了这个 —— 封与拆用的是同一份计数器代码，错也一起错，照样往返得通，
    /// 而对端（OpenSSH）会在跨过 64 位边界的那个块上解出乱码。初始计数器的低 64 位设成 FF…FE，
    /// 第一个报文内部就跨过边界，第二个报文检查报文之间的推进也带了进位。
    /// </remarks>
    [TestMethod]
    public void Ctr计数器跨过64位边界时与参照实现一致()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        iv.AsSpan(8).Fill(0xFF);
        iv[15] = 0xFE;

        using AesCtrHmacCipherSuite suite = new(
            key, iv, SshMacAlgorithm.HmacSha256, RandomNumberGenerator.GetBytes(32), encryptThenMac: true);

        byte[] first = RandomNumberGenerator.GetBytes(40);
        byte[] second = RandomNumberGenerator.GetBytes(70);
        byte[] frame1 = Seal(suite, first, 0);
        byte[] frame2 = Seal(suite, second, 1);

        using Aes aes = Aes.Create();
        aes.Key = key;
        System.Numerics.BigInteger counter = new(iv, isUnsigned: true, isBigEndian: true);
        System.Numerics.BigInteger modulus = System.Numerics.BigInteger.One << 128;

        Assert.AreSequenceEqual(first, DecryptWithReference(frame1));
        Assert.AreSequenceEqual(second, DecryptWithReference(frame2));

        byte[] DecryptWithReference(byte[] frame)
        {
            // EtM：长度字段是明文，加密区紧跟其后。
            int packetLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame);
            byte[] region = frame[4..(4 + packetLength)];

            for (int offset = 0; offset < region.Length; offset += 16)
            {
                byte[] block = new byte[16];
                byte[] value = counter.ToByteArray(isUnsigned: true, isBigEndian: true);
                value.CopyTo(block, 16 - value.Length);
                counter = (counter + 1) % modulus;

                byte[] keyStream = aes.EncryptEcb(block, PaddingMode.None);
                for (int i = 0; i < 16 && offset + i < region.Length; i++)
                {
                    region[offset + i] ^= keyStream[i];
                }
            }

            int padding = region[0];
            return region[1..(packetLength - padding)];
        }
    }

    private static byte[] Open(ISshCipherSuite suite, byte[] frame, uint seq, out long consumed)
    {
        ArrayBufferWriter<byte> writer = new();
        SshOpenStatus status = suite.TryOpen(new ReadOnlySequence<byte>(frame), seq, MaxPacket, writer, out consumed);
        Assert.AreEqual(SshOpenStatus.Opened, status);
        return writer.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------------ 往返

    [TestMethod]
    public void 各套件的封装与拆封往返一致()
    {
        int[] payloadLengths = [1, 2, 7, 8, 9, 15, 16, 17, 63, 64, 65, 100, 1000, 32768];

        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                uint seq = 0;
                foreach (int length in payloadLengths)
                {
                    byte[] payload = RandomNumberGenerator.GetBytes(length);
                    byte[] frame = Seal(sender, payload, seq);
                    byte[] recovered = Open(receiver, frame, seq, out long consumed);

                    Assert.AreSequenceEqual(payload, recovered, $"{name}：{length} 字节载荷往返失真");
                    Assert.AreEqual(frame.Length, consumed, $"{name}：consumed 与帧长不符");
                    seq++;
                }
            }
        }
    }

    [TestMethod]
    public void 帧长满足对齐要求()
    {
        // 填充计算写错时，往返测试可能仍然通过（自己发自己收），
        // 但与真实对端就对不上。所以要单独断言对齐这个**外部可见**的性质。
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                CipherSuiteShape shape = sender.Shape;
                for (int length = 0; length < 200; length++)
                {
                    byte[] frame = Seal(sender, RandomNumberGenerator.GetBytes(length), (uint)length);
                    int aligned = frame.Length - shape.TagBytes
                                  - (shape.LengthInAlignment ? 0 : SshPacketFormat.LengthFieldBytes);
                    Assert.AreEqual(0, aligned % Math.Max(shape.BlockBytes, 8),
                        $"{name}：{length} 字节载荷的帧未按 {shape.BlockBytes} 对齐（对齐区 {aligned} 字节）");
                }
            }
        }
    }

    [TestMethod]
    public void 空载荷也能往返()
    {
        // 载荷为 0 的帧在协议里不会出现（至少有一个消息编号字节），
        // 但分帧层不该因此崩掉 —— 边界应当由上层拒绝，不是靠下层碰巧不支持。
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                byte[] frame = Seal(sender, [], 0);
                byte[] recovered = Open(receiver, frame, 0, out _);
                Assert.IsEmpty(recovered, name);
            }
        }
    }

    // ------------------------------------------------------------ 数据不足

    [TestMethod]
    public void 数据不足时返回NeedMoreData且不改动状态()
    {
        // 这条是**正确性的关键**：收包侧会在数据陆续到达时反复从同一位置重试。
        // 实现如果在 NeedMoreData 的路径上推进了计数器（GCM 的 invocation counter、
        // CTR 的计数器），第二次重试就会用错的密钥流 —— 症状是「大包收不了、小包正常」。
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                byte[] payload = RandomNumberGenerator.GetBytes(500);
                byte[] frame = Seal(sender, payload, 0);

                // 从 1 字节开始逐步喂，直到差一个字节为止，全程必须是 NeedMoreData。
                for (int prefix = 1; prefix < frame.Length; prefix++)
                {
                    ArrayBufferWriter<byte> writer = new();
                    SshOpenStatus status = receiver.TryOpen(
                        new ReadOnlySequence<byte>(frame.AsMemory(0, prefix)), 0, MaxPacket, writer, out long consumed);

                    Assert.AreEqual(SshOpenStatus.NeedMoreData, status, $"{name}：{prefix} 字节时不该成功");
                    Assert.AreEqual(0, consumed, $"{name}：未取出帧时 consumed 必须为 0");
                    Assert.AreEqual(0, writer.WrittenCount, $"{name}：未取出帧时不得写出载荷");
                }

                // 喂满之后必须能正常取出 —— 前面几百次重试没有污染状态。
                byte[] recovered = Open(receiver, frame, 0, out _);
                Assert.AreSequenceEqual(payload, recovered, $"{name}：重试之后状态被污染了");
            }
        }
    }

    [TestMethod]
    public void 连续多帧能逐帧取出()
    {
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                List<byte[]> payloads = [];
                List<byte> stream = [];
                for (uint i = 0; i < 20; i++)
                {
                    byte[] p = RandomNumberGenerator.GetBytes(10 + (int)i * 37);
                    payloads.Add(p);
                    stream.AddRange(Seal(sender, p, i));
                }

                ReadOnlySequence<byte> remaining = new([.. stream]);
                for (uint i = 0; i < 20; i++)
                {
                    ArrayBufferWriter<byte> writer = new();
                    SshOpenStatus status = receiver.TryOpen(remaining, i, MaxPacket, writer, out long consumed);
                    Assert.AreEqual(SshOpenStatus.Opened, status, $"{name}：第 {i} 帧");
                    Assert.AreSequenceEqual(payloads[(int)i], writer.WrittenSpan.ToArray(), $"{name}：第 {i} 帧内容");
                    remaining = remaining.Slice(consumed);
                }

                Assert.AreEqual(0, remaining.Length, $"{name}：应当恰好读完");
            }
        }
    }

    // ---------------------------------------------------------- 完整性与篡改

    [TestMethod]
    public void 篡改任意字节都会被发现()
    {
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                if (!sender.Shape.IsEncrypted)
                {
                    continue; // 明文套件没有完整性保护，这是它的定义
                }

                byte[] original = Seal(sender, RandomNumberGenerator.GetBytes(200), 0);

                for (int i = 0; i < original.Length; i++)
                {
                    byte[] tampered = [.. original];
                    tampered[i] ^= 0x01;

                    ArrayBufferWriter<byte> writer = new();
                    try
                    {
                        SshOpenStatus status = receiver.TryOpen(
                            new ReadOnlySequence<byte>(tampered), 0, MaxPacket, writer, out _);
                        // 改了长度字段可能让帧「看起来还没收齐」—— 那也是拒绝，可以接受。
                        Assert.AreNotEqual(SshOpenStatus.Opened, status,
                            $"{name}：第 {i} 字节被改动却通过了校验");
                    }
                    catch (SshFrameFormatException)
                    {
                        // 预期：要么校验失败，要么长度非法。
                    }
                }
            }
        }
    }

    [TestMethod]
    public void 报文失步会被发现()
    {
        // 攻击者无法删除或插入报文而不被发现 —— 这是协议自带的完整性保护，
        // 也正是 Terrapin 攻击要绕开的东西（它针对的是握手期这个保护还没生效的窗口）。
        //
        // ⚠️ **但各套件表达这件事的方式不同**，这是 velashell-docs/zh/ssh/spec/01 §2.1 特意写明的：
        //
        //   · chacha20-poly1305：nonce 就是报文序号 → 序号不符直接验不过。
        //   · AES-GCM          ：nonce 是**独立递增的 invocation counter**，
        //                        与序号无关（RFC 5647 §7.1）。删一帧会让收发两侧的
        //                        计数器错开，于是下一帧验不过。
        //
        // 所以这里测的是「跳过一帧」这个**可观察的攻击**，而不是「传错 sequenceNumber 参数」
        // —— 后者对 GCM 根本没有影响，按它写测试只会得出一个假结论。
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                if (!sender.Shape.IsEncrypted)
                {
                    continue; // 明文套件没有完整性保护，这是它的定义
                }

                byte[] first = Seal(sender, RandomNumberGenerator.GetBytes(100), 0);
                byte[] second = Seal(sender, RandomNumberGenerator.GetBytes(100), 1);
                _ = first; // 攻击者把第一帧丢掉了

                // 收方**自己**的帧计数仍是 0（它从没见过第一帧），却拿到了发方标记为 1 的那一帧。
                // 这是关键：序号不是报文里带的，是两端各自数出来的 —— 删一帧就必然失步。
                //   · chacha20-poly1305：调用方传的 0 ≠ 发方封装时用的 1 → 密钥流不同 → 验不过
                //   · AES-GCM          ：内部 invocation counter 0 ≠ 发方的 1 → nonce 不同 → 验不过
                ArrayBufferWriter<byte> writer = new();
                Assert.ThrowsExactly<SshFrameFormatException>(
                    () => receiver.TryOpen(new ReadOnlySequence<byte>(second), 0, MaxPacket, writer, out _),
                    $"{name}：跳过一帧之后应当验不过");
            }
        }
    }

    // ------------------------------------------------------------ 长度边界

    [TestMethod]
    public void 超过上限的长度字段被拒绝()
    {
        // 不检查就等于让一个还没被认证过的数字决定我们要等多少字节、分配多少内存。
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                byte[] frame = Seal(sender, RandomNumberGenerator.GetBytes(100), 0);
                ArrayBufferWriter<byte> writer = new();

                // 把上限压到 8 字节：这一帧的长度必然超限。
                Assert.ThrowsExactly<SshFrameFormatException>(
                    () => receiver.TryOpen(new ReadOnlySequence<byte>(frame), 0, maxPacketLength: 8, writer, out _),
                    $"{name}：超过 maxPacketLength 应当抛出");
            }
        }
    }

    // ------------------------------------------------------------ 形状自洽

    [TestMethod]
    public void 形状字段互相自洽()
    {
        foreach ((string name, SuiteFactory factory) in AllSuites())
        {
            (ISshCipherSuite sender, ISshCipherSuite receiver) = factory();
            using (sender)
            using (receiver)
            {
                CipherSuiteShape shape = sender.Shape;
                Assert.IsGreaterThanOrEqualTo(8, shape.BlockBytes, $"{name}：块大小至少为 8（RFC 4253 §6）");
                Assert.IsGreaterThanOrEqualTo(0, shape.TagBytes, name);
                Assert.IsGreaterThanOrEqualTo(0, shape.AadBytes, name);
                Assert.IsGreaterThanOrEqualTo(SshPacketFormat.LengthFieldBytes, shape.LengthProbeBytes,
                    $"{name}：至少要读到 4 字节才谈得上解析长度");
                Assert.AreEqual(shape.Shape(), receiver.Shape.Shape(), $"{name}：收发两侧形状必须一致");
            }
        }
    }
}

internal static class ShapeAssertExtensions
{
    /// <summary>把形状压成一个可比较的字符串，断言失败时一眼看出差在哪。</summary>
    public static string Shape(this CipherSuiteShape s) =>
        $"lenEnc={s.LengthIsEncrypted} aad={s.AadBytes} tag={s.TagBytes} " +
        $"block={s.BlockBytes} lenInAlign={s.LengthInAlignment} etm={s.EncryptThenMac} enc={s.IsEncrypted}";
}
