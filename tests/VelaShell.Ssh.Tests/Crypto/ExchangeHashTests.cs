// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §4（交换哈希）、§7（密钥派生）
//
// 规格里说这是「最值得先写测试的地方」：这里每一处顺序或编码错误，
// 表现都是同一句「签名验证失败」，而错的地方与报错的地方隔了整整一层。

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class ExchangeHashTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static SshExchangeHashInput Sample(
        SshKexValueEncoding publicEncoding = SshKexValueEncoding.ByteString,
        SshKexValueEncoding secretEncoding = SshKexValueEncoding.Mpint,
        byte[]? sharedSecret = null) => new()
        {
            ClientVersion = Bytes("SSH-2.0-Client"),
            ServerVersion = Bytes("SSH-2.0-Server"),
            ClientKexInit = [20, 1, 2, 3],
            ServerKexInit = [20, 4, 5, 6],
            HostKeyBlob = [7, 8, 9],
            ClientPublicValue = [0x11, 0x22],
            ServerPublicValue = [0x33, 0x44],
            SharedSecret = sharedSecret ?? [0x55, 0x66],
            PublicValueEncoding = publicEncoding,
            SharedSecretEncoding = secretEncoding,
        };

    // ------------------------------------------------------------ 逐字段手算

    [TestMethod]
    public void 交换哈希就是各字段按序拼接之后的哈希()
    {
        // 这条用例**手工拼一遍**再对照，等于把 RFC 4253 §8 的字段顺序钉死。
        // 只断言「两次算出来一样」是抓不到顺序错误的。
        SshExchangeHashInput input = Sample();
        byte[] actual = SshExchangeHash.Compute(HashAlgorithmName.SHA256, input);

        ArrayBufferWriter<byte> expected = new();
        SshDataWriterShim w = new(expected);
        w.String(Bytes("SSH-2.0-Client"));
        w.String(Bytes("SSH-2.0-Server"));
        w.String([20, 1, 2, 3]);
        w.String([20, 4, 5, 6]);
        w.String([7, 8, 9]);
        w.String([0x11, 0x22]);          // 公开值按 string
        w.String([0x33, 0x44]);
        w.Mpint([0x55, 0x66]);           // 共享密钥按 mpint

        Assert.AreSequenceEqual([.. SHA256.HashData(expected.WrittenSpan)], actual);
    }

    [TestMethod]
    public void 共享密钥的编码方式会改变结果()
    {
        // 这是整份 KEX 规格里最容易出错、也最难排查的一处（velashell-docs/zh/ssh/spec/03 §4.1）。
        //
        // ⚠️ 样本**必须**用最高位为 1 的共享密钥：最高位为 0 时两种编码恰好相同，
        //    拿那样的样本来断言「不同」永远会失败 —— 而那不是实现的问题，是用例的问题。
        //    下一条用例专门说明这个分水岭。
        byte[] highBitSecret = [0x95, 0x66];

        byte[] asMpint = SshExchangeHash.Compute(HashAlgorithmName.SHA256,
            Sample(secretEncoding: SshKexValueEncoding.Mpint, sharedSecret: highBitSecret));
        byte[] asString = SshExchangeHash.Compute(HashAlgorithmName.SHA256,
            Sample(secretEncoding: SshKexValueEncoding.ByteString, sharedSecret: highBitSecret));

        CollectionAssert.AreNotEqual(asMpint, asString);
    }

    [TestMethod]
    public void 最高位为一的共享密钥才是分水岭()
    {
        // 最高位为 0 时两种编码**恰好相同** —— 这正是「大约一半的连接能用」
        // 这种现象的来源：写错了也能跑通一半。
        byte[] lowBit = [0x7F, 0x00];
        Assert.AreEqual(
            Convert.ToHexString(SshExchangeHash.Compute(HashAlgorithmName.SHA256,
                Sample(secretEncoding: SshKexValueEncoding.Mpint, sharedSecret: lowBit))),
            Convert.ToHexString(SshExchangeHash.Compute(HashAlgorithmName.SHA256,
                Sample(secretEncoding: SshKexValueEncoding.ByteString, sharedSecret: lowBit))),
            "最高位为 0 时两种编码相同 —— 这就是为什么错误只在一半的连接上显形");

        byte[] highBit = [0x80, 0x00];
        Assert.AreNotEqual(
            Convert.ToHexString(SshExchangeHash.Compute(HashAlgorithmName.SHA256,
                Sample(secretEncoding: SshKexValueEncoding.Mpint, sharedSecret: highBit))),
            Convert.ToHexString(SshExchangeHash.Compute(HashAlgorithmName.SHA256,
                Sample(secretEncoding: SshKexValueEncoding.ByteString, sharedSecret: highBit))),
            "最高位为 1 时必须不同");
    }

    [TestMethod]
    public void 任何一个输入变了哈希就变()
    {
        byte[] baseline = SshExchangeHash.Compute(HashAlgorithmName.SHA256, Sample());

        SshExchangeHashInput changed = Sample();
        changed = changed with { };   // ref struct 不能 with，逐个构造
        foreach (Func<SshExchangeHashInput> mutate in Mutations())
        {
            byte[] other = SshExchangeHash.Compute(HashAlgorithmName.SHA256, mutate());
            CollectionAssert.AreNotEqual(baseline, other);
        }

        static IEnumerable<Func<SshExchangeHashInput>> Mutations()
        {
            yield return () => new SshExchangeHashInput
            {
                ClientVersion = Bytes("SSH-2.0-Other"),
                ServerVersion = Bytes("SSH-2.0-Server"),
                ClientKexInit = [20, 1, 2, 3],
                ServerKexInit = [20, 4, 5, 6],
                HostKeyBlob = [7, 8, 9],
                ClientPublicValue = [0x11, 0x22],
                ServerPublicValue = [0x33, 0x44],
                SharedSecret = [0x55, 0x66],
                PublicValueEncoding = SshKexValueEncoding.ByteString,
                SharedSecretEncoding = SshKexValueEncoding.Mpint,
            };
            yield return () => new SshExchangeHashInput
            {
                ClientVersion = Bytes("SSH-2.0-Client"),
                ServerVersion = Bytes("SSH-2.0-Server"),
                ClientKexInit = [20, 1, 2, 3],
                ServerKexInit = [20, 4, 5, 6],
                HostKeyBlob = [7, 8, 10],          // 主机密钥变了
                ClientPublicValue = [0x11, 0x22],
                ServerPublicValue = [0x33, 0x44],
                SharedSecret = [0x55, 0x66],
                PublicValueEncoding = SshKexValueEncoding.ByteString,
                SharedSecretEncoding = SshKexValueEncoding.Mpint,
            };
            yield return () => new SshExchangeHashInput
            {
                ClientVersion = Bytes("SSH-2.0-Client"),
                ServerVersion = Bytes("SSH-2.0-Server"),
                ClientKexInit = [20, 1, 2, 3],
                ServerKexInit = [20, 4, 5, 6],
                HostKeyBlob = [7, 8, 9],
                ClientPublicValue = [0x33, 0x44],   // 两个公开值互换 —— 顺序写反会漏掉这个
                ServerPublicValue = [0x11, 0x22],
                SharedSecret = [0x55, 0x66],
                PublicValueEncoding = SshKexValueEncoding.ByteString,
                SharedSecretEncoding = SshKexValueEncoding.Mpint,
            };
        }
    }

    // ------------------------------------------------------------ 密钥派生

    [TestMethod]
    public void 六把密钥互不相同()
    {
        byte[] k = [0x01, 0x02, 0x03];
        byte[] h = SHA256.HashData("hash"u8);

        Dictionary<char, string> keys = [];
        foreach (char letter in "ABCDEF")
        {
            byte[] key = SshExchangeHash.DeriveKey(
                HashAlgorithmName.SHA256, k, SshKexValueEncoding.Mpint, h, h, letter, 32);
            keys[letter] = Convert.ToHexString(key);
        }

        Assert.HasCount(6, keys.Values.Distinct(), "六把密钥必须两两不同");
    }

    [TestMethod]
    public void 派生的第一轮就是公式本身()
    {
        // K_x = HASH(K ‖ H ‖ "X" ‖ session_id)
        //
        // ⚠️ **只有 K 带长度前缀。** RFC 4253 §7.2 里那个 `‖` 是直接拼接：
        // K 之所以有前缀，是因为它本身就以 mpint 编码；
        // 而 H、字母、session_id 全是裸字节。
        //
        // 这一条曾经写错过（H 与 session_id 按 string 写），而且**错得很隐蔽**：
        // H 的计算走另一条代码路径（那里 string 前缀是对的），所以签名照样验得过，
        // 自写的测试服务端又用同一个函数派生密钥 —— 两边一样错，一路绿灯。
        // 直到接上真实的 OpenSSH 才暴露：握手全程顺利，第一个加密报文解不开。
        byte[] k = [0x42];
        byte[] h = SHA256.HashData("h"u8);
        byte[] sid = SHA256.HashData("sid"u8);

        ArrayBufferWriter<byte> expected = new();
        SshDataWriterShim w = new(expected);
        w.Mpint(k);              // 带前缀 —— mpint 编码的一部分
        w.Raw(h);                // 裸字节
        w.Byte((byte)'C');       // 裸字节
        w.Raw(sid);              // 裸字节

        byte[] actual = SshExchangeHash.DeriveKey(
            HashAlgorithmName.SHA256, k, SshKexValueEncoding.Mpint, h, sid, 'C', 32);

        Assert.AreSequenceEqual([.. SHA256.HashData(expected.WrittenSpan)], actual);
    }

    [TestMethod]
    public void 需要超过一个哈希块时会扩展()
    {
        // ChaCha20-Poly1305 要 64 字节密钥，而 SHA-256 只给 32 —— 必须扩展。
        // 扩展轮**不含字母与 session_id**，只有 K ‖ H ‖ 已生成的全部。
        byte[] k = [0x42];
        byte[] h = SHA256.HashData("h"u8);
        byte[] sid = SHA256.HashData("sid"u8);

        byte[] long64 = SshExchangeHash.DeriveKey(
            HashAlgorithmName.SHA256, k, SshKexValueEncoding.Mpint, h, sid, 'C', 64);
        byte[] short32 = SshExchangeHash.DeriveKey(
            HashAlgorithmName.SHA256, k, SshKexValueEncoding.Mpint, h, sid, 'C', 32);

        Assert.HasCount(64, long64);
        // 前 32 字节必须与只要 32 字节时相同 —— 扩展是**追加**，不是重算。
        Assert.AreSequenceEqual(short32, long64[..32]);

        // 第二段就是 HASH(K ‖ H ‖ K1)
        ArrayBufferWriter<byte> second = new();
        SshDataWriterShim w = new(second);
        w.Mpint(k);
        w.Raw(h);
        w.Raw(short32);
        Assert.AreSequenceEqual([.. SHA256.HashData(second.WrittenSpan)], long64[32..]);
    }

    [TestMethod]
    public void 会话标识不同则密钥不同()
    {
        // 首次 KEX 时 session_id == H；重协商时 H 变而 session_id 不变。
        // 把它们混成一个字段，症状是「重协商之后再开新通道做公钥认证会失败」。
        byte[] k = [0x42];
        byte[] h1 = SHA256.HashData("h1"u8);
        byte[] h2 = SHA256.HashData("h2"u8);

        byte[] a = SshExchangeHash.DeriveKey(HashAlgorithmName.SHA256, k, SshKexValueEncoding.Mpint, h2, h1, 'A', 32);
        byte[] b = SshExchangeHash.DeriveKey(HashAlgorithmName.SHA256, k, SshKexValueEncoding.Mpint, h2, h2, 'A', 32);
        CollectionAssert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void 长度为零时返回空数组()
    {
        byte[] key = SshExchangeHash.DeriveKey(
            HashAlgorithmName.SHA256, [1], SshKexValueEncoding.Mpint, [2], [3], 'A', 0);
        Assert.IsEmpty(key);
    }

    [TestMethod]
    public void 字母越界时抛出()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshExchangeHash.DeriveKey(
            HashAlgorithmName.SHA256, [1], SshKexValueEncoding.Mpint, [2], [3], 'G', 32));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshExchangeHash.DeriveKey(
            HashAlgorithmName.SHA256, [1], SshKexValueEncoding.Mpint, [2], [3], 'a', 32));
    }

    [TestMethod]
    public void 各哈希算法都能用()
    {
        foreach ((HashAlgorithmName name, int size) in new[]
                 {
                     (HashAlgorithmName.SHA256, 32),
                     (HashAlgorithmName.SHA384, 48),
                     (HashAlgorithmName.SHA512, 64),
                     (HashAlgorithmName.SHA1, 20),
                 })
        {
            byte[] hash = SshExchangeHash.Compute(name, Sample());
            Assert.HasCount(size, hash, $"{name.Name} 的输出长度");
        }
    }

    /// <summary>测试里手工拼字节用的小工具（与被测代码的编码器独立，免得同错同对）。</summary>
    private readonly struct SshDataWriterShim(ArrayBufferWriter<byte> output)
    {
        public void Byte(byte b) => output.Write(new byte[] { b });

        public void Raw(ReadOnlySpan<byte> value) => output.Write(value);

        public void String(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            output.Write(length);
            output.Write(value);
        }

        public void Mpint(ReadOnlySpan<byte> magnitude)
        {
            int start = 0;
            while (start < magnitude.Length && magnitude[start] == 0)
            {
                start++;
            }
            ReadOnlySpan<byte> trimmed = magnitude[start..];
            if (trimmed.IsEmpty)
            {
                String(ReadOnlySpan<byte>.Empty);
                return;
            }

            bool pad = (trimmed[0] & 0x80) != 0;
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(trimmed.Length + (pad ? 1 : 0)));
            output.Write(length);
            if (pad)
            {
                output.Write(new byte[] { 0 });
            }
            output.Write(trimmed);
        }
    }
}
