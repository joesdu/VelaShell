// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>服务端宣告的读写上限（<c>limits@openssh.com</c>）。</summary>
/// <param name="MaxPacketLength">单个 SFTP 报文上限。</param>
/// <param name="MaxReadLength">单次 <c>READ</c> 的 length 上限。</param>
/// <param name="MaxWriteLength">单次 <c>WRITE</c> 的 data 上限。</param>
/// <param name="MaxOpenHandles">同时打开的句柄数上限（<c>0</c> = 服务端没说）。</param>
public readonly record struct SftpLimits(
    ulong MaxPacketLength,
    ulong MaxReadLength,
    ulong MaxWriteLength,
    ulong MaxOpenHandles)
{
    /// <summary>服务端没宣告 <c>limits@openssh.com</c> 时的保守默认。</summary>
    public static SftpLimits Conservative => new(
        MaxPacketLength: SftpProtocol.DefaultBlockSize + 1024,
        MaxReadLength: SftpProtocol.DefaultBlockSize,
        MaxWriteLength: SftpProtocol.DefaultBlockSize,
        MaxOpenHandles: 0);
}
