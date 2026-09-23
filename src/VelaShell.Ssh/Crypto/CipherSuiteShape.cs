// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6      Binary Packet Protocol
//   RFC 5647 §7.1    AES-GCM 的长度字段是明文 AAD、且不参与对齐
//   OpenSSH PROTOCOL.chacha20poly1305
//   OpenSSH PROTOCOL 的 *-etm@openssh.com
//   行为规格:        velashell-docs/zh/ssh/spec/01-transport-framing.md §2

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// 一套密码套件在**分帧**上的形状。
/// </summary>
/// <remarks>
/// <para>
/// 不同套件在分帧上的差异只有这几个维度。把它们抽成数据，帧层就不必为每种算法
/// 写一遍「先读几个字节、先解密还是先验证」的分支 —— 那些分支正是
/// AEAD 与非 AEAD 混在一起时最容易出错的地方。
/// </para>
/// <para>
/// ⚠️ <see cref="LengthIsEncrypted"/> 由「加密算法 + MAC 算法」的**组合**决定，
/// 不是加密算法单独决定：AES-CTR 配 MtE 时长度是密文，配 EtM 时长度是明文。
/// 因此这个结构由套件的**组装函数**产出，而不是两个独立枚举的笛卡尔积。
/// </para>
/// </remarks>
public readonly record struct CipherSuiteShape
{
    /// <summary>
    /// 4 字节的 <c>packet_length</c> 是否被加密。
    /// </summary>
    /// <remarks>
    /// 为 <see langword="true"/> 时帧层不能直接读前 4 字节，必须先交给套件解出长度。
    /// </remarks>
    public required bool LengthIsEncrypted { get; init; }

    /// <summary>
    /// 参与完整性计算但不加密的前缀字节数。
    /// </summary>
    /// <remarks>AES-GCM 为 4（长度字段作为 AAD）；ChaCha20-Poly1305 与非 AEAD 为 0。</remarks>
    public required int AadBytes { get; init; }

    /// <summary>AEAD tag 或 MAC 的字节数。<c>none</c> 套件为 0。</summary>
    public required int TagBytes { get; init; }

    /// <summary>填充对齐的块大小。<b>至少为 8</b>（RFC 4253 §6）。</summary>
    public required int BlockBytes { get; init; }

    /// <summary>
    /// 4 字节的长度字段是否计入填充对齐。
    /// </summary>
    /// <remarks>
    /// 普通套件为 <see langword="true"/>（对齐的是 <c>4 + 1 + payload + padding</c>）。
    /// <b>AEAD 套件为 <see langword="false"/></b> —— RFC 5647 与
    /// OpenSSH 的 chacha20-poly1305 都规定对齐的是 <c>1 + payload + padding</c>，
    /// 不含长度字段。
    /// <para>
    /// 这一条是 AEAD 实现最常见的错位来源：写错了在某些包长上才会暴露，
    /// 短包全对、长包忽然对不上。
    /// </para>
    /// </remarks>
    public required bool LengthInAlignment { get; init; }

    /// <summary>
    /// MAC 覆盖的是密文（Encrypt-then-MAC）还是明文（MAC-then-Encrypt）。
    /// </summary>
    /// <remarks>AEAD 套件自带完整性，此值无意义，约定为 <see langword="true"/>。</remarks>
    public required bool EncryptThenMac { get; init; }

    /// <summary>这套套件是否提供保密性（<c>none</c> 之外都是）。</summary>
    public required bool IsEncrypted { get; init; }

    /// <summary>
    /// 帧头中可以直接读到长度之前，至少需要多少字节。
    /// </summary>
    /// <remarks>
    /// 长度是明文时为 4；长度被加密时，套件可能需要一整个块才能解出它
    /// （AES-CTR/CBC 要 <see cref="BlockBytes"/>，ChaCha20 只要 4）。
    /// </remarks>
    public required int LengthProbeBytes { get; init; }

    /// <summary>不加密、不认证的握手期套件形状。</summary>
    public static CipherSuiteShape Plaintext { get; } = new()
    {
        LengthIsEncrypted = false,
        AadBytes = 0,
        TagBytes = 0,
        BlockBytes = 8,
        LengthInAlignment = true,
        EncryptThenMac = true,
        IsEncrypted = false,
        LengthProbeBytes = 4,
    };
}
