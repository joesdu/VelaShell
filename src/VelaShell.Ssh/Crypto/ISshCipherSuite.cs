// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/01-transport-framing.md §2、§3、§4

using System.Buffers;

namespace VelaShell.Ssh.Crypto;

/// <summary>一次拆帧尝试的结果。</summary>
public enum SshOpenStatus
{
    /// <summary>成功取出一帧载荷。</summary>
    Opened,

    /// <summary>缓冲区里还不够一整帧，等更多数据后重试。</summary>
    NeedMoreData,
}

/// <summary>
/// 一整套密码套件：加密算法 + 完整性算法的组合。
/// </summary>
/// <remarks>
/// <para>
/// <b>一个接口描述一整套</b>，而不是把「加密」与「MAC」拆成两族再去组合。
/// AEAD（GCM、ChaCha20-Poly1305）与「加密 + 独立 MAC」在分帧上的差异
/// 由 <see cref="Shape"/> 表达（velashell-docs/zh/ssh/spec/01-transport-framing.md §2）。
/// </para>
/// <para>
/// 实现是**有状态的**（CTR 的计数器、GCM 的 invocation counter），
/// 且每个方向一份。<b>不是线程安全的</b> —— 收发各自由单一的泵独占。
/// </para>
/// <para>
/// <b>安全要求</b>：<see cref="TryOpen"/> 必须**先验证完整性再解密**
/// （AEAD 与 EtM 都能做到）。做不到的组合（MtE）是历史包袱，
/// 它们本质上给对端提供了一个解密预言机 —— 这也是我们把 EtM 排在 MtE 之前的原因。
/// </para>
/// </remarks>
public interface ISshCipherSuite : IDisposable
{
    /// <summary>这套套件在分帧上的形状。</summary>
    CipherSuiteShape Shape { get; }

    /// <summary>
    /// 把一个载荷封装成一整帧（长度 + 填充长度 + 载荷 + 填充 + tag/MAC）写入 <paramref name="output"/>。
    /// </summary>
    /// <param name="payload">载荷，第一个字节是消息编号。</param>
    /// <param name="sequenceNumber">本帧的发送序号。</param>
    /// <param name="output">帧写到这里。</param>
    /// <remarks>
    /// 填充由实现按 <see cref="Shape"/> 计算，且**必须是密码学随机字节**。
    /// </remarks>
    void Seal(ReadOnlySpan<byte> payload, uint sequenceNumber, IBufferWriter<byte> output);

    /// <summary>
    /// 尝试从缓冲区里取出一整帧的载荷。
    /// </summary>
    /// <param name="input">已收到的字节，从某一帧的起始处开始。</param>
    /// <param name="sequenceNumber">本帧的接收序号。</param>
    /// <param name="maxPacketLength">允许的最大 <c>packet_length</c>。</param>
    /// <param name="payload">载荷写到这里（仅在返回 <see cref="SshOpenStatus.Opened"/> 时）。</param>
    /// <param name="consumed">本帧在 <paramref name="input"/> 中占用的字节数。</param>
    /// <returns>是否取出了一帧。</returns>
    /// <exception cref="SshFrameFormatException">
    /// 长度越界、填充非法、完整性校验失败。**调用方必须据此断开连接**，
    /// 不得重试或继续读（velashell-docs/zh/ssh/spec/01-transport-framing.md §5）。
    /// </exception>
    /// <remarks>
    /// 返回 <see cref="SshOpenStatus.NeedMoreData"/> 时，实现**禁止**改变任何内部状态
    /// （计数器、序号）—— 调用方会在收到更多数据后从同一位置重试。
    /// </remarks>
    SshOpenStatus TryOpen(
        ReadOnlySequence<byte> input,
        uint sequenceNumber,
        int maxPacketLength,
        IBufferWriter<byte> payload,
        out long consumed);
}
