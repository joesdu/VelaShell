// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   B. Schneier, "Description of a New Variable-Length Key, 64-Bit Block Cipher
//     (Blowfish)", Fast Software Encryption 1993
//     —— P 数组与 S 盒取自 π 的十六进制位、F 函数、16 轮 Feistel、密钥编排
//   N. Provos, D. Mazières, "A Future-Adaptable Password Scheme",
//     USENIX Annual Technical Conference 1999
//     —— eksblowfish:把盐也喂进密钥编排(expandstate)
//   OpenBSD bcrypt_pbkdf(3) —— 迭代、异或累加与跨块分发的定义
//   行为规格: velashell-docs/zh/ssh/design/architecture.md §11.2.6
//
// ⚠️ 本文件是「不自己写密码学原语」(AGENTS.md §3.3)唯一的、有记录的例外。
//    例外的范围、理由与验证方式写在 §11.2.6,改动本文件前先读那一节。

using System.Numerics;
using System.Security.Cryptography;

namespace VelaShell.Ssh.Keys;

/// <summary>
/// <c>bcrypt_pbkdf</c> —— 加密的 OpenSSH 私钥所用的口令派生函数。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这一段是自己写的,而库里别处一律不写密码学原语。</b>
/// <c>bcrypt_pbkdf</c> 需要的不是 Blowfish 的分组加密,而是它的<b>密钥编排内部</b>
/// (<c>expandstate</c> / <c>expand0state</c>)。BCL 根本没有 Blowfish;
/// BouncyCastle 的 <c>BlowfishEngine</c> 只暴露 <c>Init</c> + <c>ProcessBlock</c>,
/// 它的 <c>BCrypt</c> 做的是 2<sup>cost</sup> 轮的标准 bcrypt,与这里要的
/// 64 轮 <c>expand0state</c> 变体不是一回事。**没有任何现成原语能凑出来。**
/// </para>
/// <para>
/// 不做这一段的代价是「加密的 OpenSSH 私钥读不了」—— 而那是 <c>ssh-keygen</c>
/// 带口令时的默认产物,也就是绝大多数人手里那把钥。权衡之后决定破例,
/// 同时把风险摁在三件事上:
/// </para>
/// <list type="number">
///   <item><b>初始表不手抄。</b>P 数组与 4 个 S 盒共 1042 个字全部由 π 的
///     十六进制位现算(<see cref="BuildInitialState"/>)—— 抄错一个字不会报错,
///     只会在特定输入上静默产出错误结果,而算出来的东西可以对着定义验。</item>
///   <item><b>只做 KDF,不做加密。</b>本类型不导出任何分组加密能力,
///     Blowfish 在这里是纯粹的内部细节,外面拿不到。</item>
///   <item><b>验证走真实产物。</b>用例拿 <c>ssh-keygen</c> 真生成的加密私钥
///     端到端对拍(解出来的公钥必须与 <c>.pub</c> 逐字节相同),
///     而不是自己造一份与实现一样错的测试桩。</item>
/// </list>
/// </remarks>
internal static class BcryptPbkdf
{
    /// <summary>一次 <c>bcrypt_hash</c> 产出的字节数。</summary>
    private const int HashBytes = 32;

    /// <summary>P 数组的长度(16 轮各一个,加上收尾的两个)。</summary>
    private const int PLength = 18;

    /// <summary>S 盒个数。</summary>
    private const int SBoxCount = 4;

    /// <summary>每个 S 盒的表项数。</summary>
    private const int SBoxSize = 256;

    /// <summary>Feistel 轮数。</summary>
    private const int Rounds = 16;

    /// <summary>
    /// <c>bcrypt_hash</c> 每轮加密的那段魔数,正好 32 字节 = 8 个 32 位字。
    /// </summary>
    private static ReadOnlySpan<byte> MagicText => "OxychromaticBlowfishSwatDynamite"u8;

    /// <summary>
    /// π 的小数部分十六进制位切出来的 1042 个字:前 18 个是 P 数组,
    /// 其后每 256 个是一个 S 盒。<b>算出来的,不是抄来的</b>(见类型说明第 1 条)。
    /// </summary>
    private static readonly uint[] InitialState = BuildInitialState();

    /// <summary>
    /// 只给用例看的初始表 —— <c>BlowfishTableTests</c> 拿它钉住首尾几个公认值,
    /// 这样即使没有加密私钥的样本,推导一旦被改坏也会当场红。
    /// </summary>
    internal static ReadOnlySpan<uint> InitialTables => InitialState;

    /// <summary>
    /// 从口令与盐派生任意长度的密钥材料。
    /// </summary>
    /// <param name="password">口令的 UTF-8 字节。</param>
    /// <param name="salt">盐(来自私钥文件的 kdfoptions)。</param>
    /// <param name="rounds">迭代轮数(来自私钥文件的 kdfoptions)。</param>
    /// <param name="output">输出缓冲区,长度即要派生的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rounds" /> 小于 1。</exception>
    /// <exception cref="ArgumentException">口令、盐或输出为空。</exception>
    public static void DeriveKey(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int rounds, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);

        if (password.IsEmpty)
        {
            throw new ArgumentException("口令不能为空。", nameof(password));
        }
        if (salt.IsEmpty)
        {
            throw new ArgumentException("盐不能为空。", nameof(salt));
        }
        if (output.IsEmpty)
        {
            throw new ArgumentException("要派生的长度不能是 0。", nameof(output));
        }

        int keyLength = output.Length;

        // 输出跨块**交错**分发,不是简单拼接:第 count 块的第 i 个字节落到
        // out[i * stride + (count - 1)]。这样任何一段输出都同时依赖多个块,
        // 截取前 N 字节也不会只用到第一块。
        int stride = ((keyLength + HashBytes) - 1) / HashBytes;
        int amount = ((keyLength + stride) - 1) / stride;

        Span<byte> passwordHash = stackalloc byte[SHA512.HashSizeInBytes];
        Span<byte> saltHash = stackalloc byte[SHA512.HashSizeInBytes];
        Span<byte> accumulated = stackalloc byte[HashBytes];
        Span<byte> current = stackalloc byte[HashBytes];
        Span<byte> counter = stackalloc byte[sizeof(uint)];

        try
        {
            SHA512.HashData(password, passwordHash);

            int remaining = keyLength;
            for (uint count = 1; remaining > 0; count++)
            {
                // 盐每块不同:sha512(salt ‖ be32(count))。没有这个计数器,
                // 所有块会算出同一个值,长输出就成了同一段的重复。
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(counter, count);
                using (IncrementalHash incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
                {
                    incremental.AppendData(salt);
                    incremental.AppendData(counter);
                    incremental.GetHashAndReset(saltHash);
                }

                BcryptHash(passwordHash, saltHash, current);
                current.CopyTo(accumulated);

                for (int round = 1; round < rounds; round++)
                {
                    // 下一轮的盐是**上一轮的输出**,这条链让轮数真的花掉时间。
                    SHA512.HashData(current, saltHash);
                    BcryptHash(passwordHash, saltHash, current);

                    // 异或累加而不是取最后一轮:任何一轮的结果都进了最终输出,
                    // 想跳过中间轮就必须把它们都算出来。
                    for (int i = 0; i < HashBytes; i++)
                    {
                        accumulated[i] ^= current[i];
                    }
                }

                int written = 0;
                for (int i = 0; i < amount; i++)
                {
                    int destination = (i * stride) + (int)(count - 1);
                    if (destination >= keyLength)
                    {
                        break;
                    }
                    output[destination] = accumulated[i];
                    written++;
                }

                if (written == 0)
                {
                    // 分发已经填不进任何新位置了 —— 再循环下去就是死循环。
                    break;
                }
                remaining -= written;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordHash);
            CryptographicOperations.ZeroMemory(saltHash);
            CryptographicOperations.ZeroMemory(accumulated);
            CryptographicOperations.ZeroMemory(current);
        }
    }

    /// <summary>
    /// <c>bcrypt_hash</c>:带盐的密钥编排跑满,再把魔数加密 64 次。
    /// </summary>
    /// <remarks>
    /// 代价全在这里 —— <c>expandstate</c> 之后还要 64 次
    /// 「用盐编排一遍、再用口令编排一遍」,每次都要重填 P 数组与 4 个 S 盒。
    /// 这正是 bcrypt 系列难以用硬件加速的原因:它要 4 KiB 的可写状态,
    /// 而 GPU / ASIC 上每个核心都摊不起这块内存。
    /// </remarks>
    private static void BcryptHash(
        ReadOnlySpan<byte> passwordHash, ReadOnlySpan<byte> saltHash, Span<byte> output)
    {
        uint[] state = new uint[InitialState.Length];
        try
        {
            InitialState.CopyTo(state, 0);

            ExpandState(state, saltHash, passwordHash);
            for (int i = 0; i < 64; i++)
            {
                ExpandKeyOnly(state, saltHash);
                ExpandKeyOnly(state, passwordHash);
            }

            // 魔数按大端读成 8 个字,再整体加密 64 次。
            Span<uint> block = stackalloc uint[MagicText.Length / sizeof(uint)];
            int cursor = 0;
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = NextWord(MagicText, ref cursor);
            }

            for (int i = 0; i < 64; i++)
            {
                for (int j = 0; j < block.Length; j += 2)
                {
                    Encipher(state, ref block[j], ref block[j + 1]);
                }
            }

            // 输出按**小端**写回。这一处与全书其余部分的大端约定相反,
            // 是 bcrypt_pbkdf 的既定行为 —— 写成大端会得到一把完全不同的钥。
            for (int i = 0; i < block.Length; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    output.Slice(i * sizeof(uint), sizeof(uint)), block[i]);
            }
        }
        finally
        {
            Array.Clear(state);
        }
    }

    /// <summary>
    /// 带盐的密钥编排(eksblowfish 的 <c>expandstate</c>):
    /// 口令异或进 P 数组,随后用盐作为不断推进的输入重填整张表。
    /// </summary>
    private static void ExpandState(uint[] state, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> key)
    {
        int keyCursor = 0;
        for (int i = 0; i < PLength; i++)
        {
            state[i] ^= NextWord(key, ref keyCursor);
        }

        int saltCursor = 0;
        uint left = 0;
        uint right = 0;

        for (int i = 0; i < PLength; i += 2)
        {
            left ^= NextWord(salt, ref saltCursor);
            right ^= NextWord(salt, ref saltCursor);
            Encipher(state, ref left, ref right);
            state[i] = left;
            state[i + 1] = right;
        }

        for (int box = 0; box < SBoxCount; box++)
        {
            int offset = PLength + (box * SBoxSize);
            for (int i = 0; i < SBoxSize; i += 2)
            {
                left ^= NextWord(salt, ref saltCursor);
                right ^= NextWord(salt, ref saltCursor);
                Encipher(state, ref left, ref right);
                state[offset + i] = left;
                state[offset + i + 1] = right;
            }
        }
    }

    /// <summary>
    /// 不带盐的密钥编排(<c>expand0state</c>):与 <see cref="ExpandState" /> 同形,
    /// 只是推进输入恒为全零,因而只有口令(或盐)通过 P 数组的那一次异或起作用。
    /// </summary>
    private static void ExpandKeyOnly(uint[] state, ReadOnlySpan<byte> key)
    {
        int keyCursor = 0;
        for (int i = 0; i < PLength; i++)
        {
            state[i] ^= NextWord(key, ref keyCursor);
        }

        uint left = 0;
        uint right = 0;

        for (int i = 0; i < PLength; i += 2)
        {
            Encipher(state, ref left, ref right);
            state[i] = left;
            state[i + 1] = right;
        }

        for (int box = 0; box < SBoxCount; box++)
        {
            int offset = PLength + (box * SBoxSize);
            for (int i = 0; i < SBoxSize; i += 2)
            {
                Encipher(state, ref left, ref right);
                state[offset + i] = left;
                state[offset + i + 1] = right;
            }
        }
    }

    /// <summary>Blowfish 的 16 轮 Feistel 网络。</summary>
    private static void Encipher(uint[] state, ref uint leftHalf, ref uint rightHalf)
    {
        uint left = leftHalf;
        uint right = rightHalf;

        for (int i = 0; i < Rounds; i++)
        {
            left ^= state[i];
            right ^= Mix(state, left);
            (left, right) = (right, left);
        }

        // 最后一轮多交换了一次,这里换回来;收尾的两个子密钥不参与 Feistel。
        (left, right) = (right, left);
        right ^= state[Rounds];
        left ^= state[Rounds + 1];

        leftHalf = left;
        rightHalf = right;
    }

    /// <summary>
    /// Blowfish 的 F 函数:把 32 位切成四个字节各查一张 S 盒,
    /// 再以「加、异或、加」混合 —— 加法与异或交替,是它抗差分分析的来源。
    /// </summary>
    private static uint Mix(uint[] state, uint value)
    {
        int s0 = PLength;
        int s1 = s0 + SBoxSize;
        int s2 = s1 + SBoxSize;
        int s3 = s2 + SBoxSize;

        uint a = state[s0 + (byte)(value >> 24)];
        uint b = state[s1 + (byte)(value >> 16)];
        uint c = state[s2 + (byte)(value >> 8)];
        uint d = state[s3 + (byte)value];

        return ((a + b) ^ c) + d;
    }

    /// <summary>
    /// 从输入里取 4 个字节拼成大端的 32 位字,读到末尾就回卷到开头。
    /// </summary>
    /// <remarks>
    /// 回卷是这套编排的定义的一部分:输入比要消耗的字节数短得多
    /// (64 字节要喂满 1042 个字),靠的就是循环读。
    /// </remarks>
    private static uint NextWord(ReadOnlySpan<byte> data, ref int cursor)
    {
        uint word = 0;
        for (int i = 0; i < sizeof(uint); i++)
        {
            if (cursor >= data.Length)
            {
                cursor = 0;
            }
            word = (word << 8) | data[cursor];
            cursor++;
        }
        return word;
    }

    /// <summary>
    /// 现算 P 数组与 S 盒:它们就是 π 小数部分的十六进制位,依次切成 32 位字。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么算而不抄。</b>这是 1042 个 32 位常量。抄错一个字,编译照过、
    /// 用例照过(只要测试桩也用同一张错表),只有对着真实的 OpenSSH 私钥才会
    /// 露出来 —— 而那时的症状是「口令明明对却解不开」,查起来毫无头绪。
    /// 从定义算出来则可以对着 π 验,且 <c>BlowfishTableTests</c> 会钉住
    /// 首尾几个字的公认值。
    /// </para>
    /// <para>
    /// 用 Machin 公式(π = 16·arctan(1/5) − 4·arctan(1/239))算到 33344 位再多留
    /// 128 位保护位。这只在**第一次读加密私钥**时跑一次,之后是静态表。
    /// </para>
    /// </remarks>
    private static uint[] BuildInitialState()
    {
        const int wordCount = PLength + (SBoxCount * SBoxSize);
        const int bits = wordCount * 32;
        const int guardBits = 128;
        const int totalBits = bits + guardBits;

        BigInteger scale = BigInteger.One << totalBits;
        BigInteger pi = (16 * ArcTangentReciprocal(5, totalBits)) - (4 * ArcTangentReciprocal(239, totalBits));

        // 只要小数部分:π 的整数部分是 3。
        BigInteger fraction = (pi - (3 * scale)) >> guardBits;

        uint[] words = new uint[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            // 先掩位再转:直接 (uint) 一个比 uint 大的 BigInteger 会抛 OverflowException。
            words[i] = (uint)((fraction >> (bits - (32 * (i + 1)))) & 0xFFFFFFFFu);
        }
        return words;
    }

    /// <summary>
    /// 定点计算 <c>arctan(1/x) · 2^<paramref name="totalBits" /></c>
    /// (Gregory 级数,x ≥ 5 时收敛足够快)。
    /// </summary>
    private static BigInteger ArcTangentReciprocal(int x, int totalBits)
    {
        BigInteger squared = (BigInteger)x * x;
        BigInteger term = (BigInteger.One << totalBits) / x;
        BigInteger sum = term;

        int index = 1;
        bool subtract = true;
        while (!term.IsZero)
        {
            term /= squared;
            BigInteger contribution = term / ((2 * index) + 1);
            sum += subtract ? -contribution : contribution;
            subtract = !subtract;
            index++;
        }
        return sum;
    }
}
