// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.certkeys —— *-cert-v01@openssh.com 的字段表、
//                                证书类型(1 用户 / 2 主机)、有效期与扩展
//   RFC 4251 §5               —— string / uint64 / name-list 的 wire 表示
//   行为规格: velashell-docs/zh/ssh/design/architecture.md §8 第 5 项
//             velashell-docs/zh/ssh/spec/03-key-exchange.md §5.5（主机证书的验证）

namespace VelaShell.Ssh.Keys;

/// <summary>证书是发给用户的还是发给主机的。</summary>
public enum SshCertificateType
{
    /// <summary>用户证书:拿它登录服务器。</summary>
    User = 1,

    /// <summary>主机证书:服务器拿它向客户端证明身份。</summary>
    Host = 2,
}
