// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4、§5.5

namespace VelaShell.Ssh.HostKeys;

/// <summary>没见过这台主机时怎么办。</summary>
public enum UnknownHostBehavior
{
    /// <summary>
    /// 问使用者（TOFU）。
    /// </summary>
    /// <remarks>
    /// 交互式客户端用这个。<b>裁决耗时不计入连接超时</b>，
    /// 所以弹窗可以一直摆着等用户看指纹。
    /// </remarks>
    Ask,

    /// <summary>直接接受并记下来。</summary>
    /// <remarks>⚠️ 等于 <c>StrictHostKeyChecking no</c> 的第一次，首连是盲信的。</remarks>
    AcceptAndPersist,

    /// <summary>拒绝。对应 <c>StrictHostKeyChecking yes</c>。</summary>
    Reject,
}
