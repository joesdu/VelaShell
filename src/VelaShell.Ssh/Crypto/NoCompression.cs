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

/// <summary>不压缩。</summary>
internal sealed class NoCompression : ISshCompressor
{
    /// <summary>一个可以共用的实例。</summary>
    public static NoCompression Instance { get; } = new();

    /// <inheritdoc />
    public bool IsActive => false;

    /// <inheritdoc />
    public void Compress(ReadOnlySpan<byte> payload, IBufferWriter<byte> output) => output.Write(payload);

    /// <inheritdoc />
    public void Decompress(ReadOnlySequence<byte> payload, IBufferWriter<byte> output, int maxOutputLength)
    {
        foreach (ReadOnlyMemory<byte> segment in payload)
        {
            output.Write(segment.Span);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
