// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6.2    压缩:对**载荷**压缩,每个方向一个独立的、跨报文保留的 zlib 流
//   RFC 1950/1951    zlib 与 deflate 的容器与块格式(flush 语义出自这里)
//   OpenSSH PROTOCOL zlib@openssh.com —— 认证成功之后才开始压缩
//   行为规格:        velashell-docs/zh/ssh/spec/01-transport-framing.md §压缩

using System.Buffers;

namespace VelaShell.Ssh.Crypto;

/// <summary>一个方向上的压缩。</summary>
/// <remarks>
/// <para>
/// <b>压缩的是载荷，不是整个报文</b> —— 长度字段、填充、MAC 都在压缩之外。
/// </para>
/// <para>
/// <b>每个方向一个独立的流，而且跨报文保留状态。</b>
/// 这是压缩率的全部来源：SSH 报文都很小（一次按键才几十字节），
/// 每个报文各压各的几乎压不动；共用一本字典才有意义。
/// 代价是<b>丢一个报文就全乱了</b> —— 但 SSH 跑在 TCP 上，不会丢。
/// </para>
/// </remarks>
internal interface ISshCompressor : IDisposable
{
    /// <summary>这个方向现在到底压不压。</summary>
    bool IsActive { get; }

    /// <summary>压一个载荷。</summary>
    void Compress(ReadOnlySpan<byte> payload, IBufferWriter<byte> output);

    /// <summary>解一个载荷。</summary>
    void Decompress(ReadOnlySequence<byte> payload, IBufferWriter<byte> output, int maxOutputLength);
}
