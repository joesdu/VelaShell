// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §2.2;velashell-docs/zh/ssh/spec/08-failures.md §5.2

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>拨号链上「每一跳的结果」的记账与异常改写。</summary>
/// <remarks>
/// 规则只有两条（velashell-docs/zh/ssh/spec/09 §2.2）：跳按<b>从近到远</b>排；
/// 内层给出的跳信息<b>原样保留</b>，外层只在后面追加自己的。
/// </remarks>
internal static class DialHops
{
    public static SshHopInfo Hop(
        SshDialKind kind, SshEndPoint target, bool succeeded, long startedAtTicks, string? detail = null) =>
        new(kind, target.ToString(), succeeded, TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAtTicks), detail);

    /// <summary>
    /// 内层拨号器失败了：保留它的跳信息；它没给的话，补一条「这一跳失败」。
    /// </summary>
    public static IReadOnlyList<SshHopInfo> FromInnerFailure(
        Exception failure, SshDialKind innerKind, SshEndPoint innerTarget, long startedAtTicks) =>
        failure is SshConnectException { Hops.Count: > 0 } connect
            ? connect.Hops
            : [Hop(innerKind, innerTarget, succeeded: false, startedAtTicks, failure.Message)];

    /// <summary>把一个失败改写成带跳信息的 <see cref="SshConnectException"/>，原因码不变。</summary>
    /// <param name="failure">原来的失败。</param>
    /// <param name="message">给人看的一句话（通常在原消息前面加上「是哪一跳」）。</param>
    /// <param name="hops">链路上每一跳的结果。</param>
    public static SshConnectException Rewrap(Exception failure, string message, IReadOnlyList<SshHopInfo> hops)
    {
        (SshFailureReason reason, SshPhase phase) = failure is SshException ssh
            ? (ssh.Reason, SshPhase.Dialing)
            : (SshFailureReason.ProxyRefused, SshPhase.Dialing);

        return new SshConnectException(reason, phase, message, failure) { Hops = hops };
    }
}
