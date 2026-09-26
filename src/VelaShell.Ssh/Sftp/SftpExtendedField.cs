// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §5  ATTRS 的 extended_type / extended_data
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>ATTRS 里的一条厂商扩展字段（<c>extended_type</c> / <c>extended_data</c>）。</summary>
/// <param name="Type">扩展名（<c>name@domain</c>）。</param>
/// <param name="Data">扩展数据，原样。</param>
public readonly record struct SftpExtendedField(string Type, string Data);
