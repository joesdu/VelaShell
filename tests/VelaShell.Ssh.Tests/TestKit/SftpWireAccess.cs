// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 测试桩要能拼出 SFTP 的外层帧,而那个方法是 internal ——
// 通过 InternalsVisibleTo 转发一次,好让桩代码读起来还是普通调用。

using System.Buffers;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>把 <c>SftpWire</c> 的内部成员转出来给测试桩用。</summary>
internal static class SftpWireAccess
{
    /// <summary>给一段载荷包上外层的 <c>length</c> 与 <c>type</c>。</summary>
    public static void WriteFrame(IBufferWriter<byte> output, SftpMessageType type, ReadOnlySpan<byte> payload) =>
        SftpWire.WriteFrame(output, type, payload);
}
