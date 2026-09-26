// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.2     forwarded-tcpip
//   OpenSSH PROTOCOL  forwarded-streamlocal@openssh.com、auth-agent@openssh.com
//   行为规格:         velashell-docs/zh/ssh/spec/07-forwarding.md §4.1、§七;velashell-docs/zh/ssh/design/architecture.md §8 第 8 项

namespace VelaShell.Ssh.Channels;

/// <summary>处理<b>服务端发起</b>的通道。</summary>
/// <remarks>
/// <para>
/// 远程转发（<c>-R</c>）的回连、agent 转发都走这里：服务端收到一个连接之后，
/// 反过来向我们发起 <c>CHANNEL_OPEN</c>。
/// </para>
/// <para>
/// <b>没有登记处理器的类型一律明确拒绝</b>，不沉默 ——
/// 沉默会让对端一直等着那个永远不来的应答。
/// </para>
/// <para>
/// <c>internal</c>：它是本库几个转发器与连接之间的约定，不是扩展点 —— 公开的转发器用显式接口实现它，
/// 调用方看不到 <c>OnOpenAborted</c> 这类内部钩子（AGENTS.md §4.1）。
/// </para>
/// </remarks>
internal interface IIncomingChannelHandler
{
    /// <summary>决定用什么参数接这条通道。</summary>
    /// <param name="channelType">通道类型。</param>
    /// <param name="typeSpecificPayload">类型相关的字段（如转发的绑定地址与端口）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 抛异常等于拒绝这条通道，异常消息会作为拒绝理由发给对端。
    /// 远程转发在这里按「绑定地址 + 端口」查路由表 —— 查不到就该拒。
    /// </remarks>
    ValueTask<SshChannelOptions> GetOptionsAsync(
        string channelType, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken);

    /// <summary>接管一条已经打开的入站通道。</summary>
    /// <remarks>
    /// 调用方<b>不等</b>这个方法返回 —— 它多半要去连一个本地目标，
    /// 而接收循环不能停在任何一条通道上。
    /// 处理器负责这条通道的整个生命周期，包括释放它。
    /// </remarks>
    Task HandleAsync(
        SshChannel channel, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="GetOptionsAsync"/> 已经同意，但这条通道最终<b>没有</b>打开
    /// （本端通道数或窗口预算用尽、会话已经断开）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在 <see cref="GetOptionsAsync"/> 里占了资源（并发槽位之类）的处理器，
    /// 要在这里还回去 —— <see cref="HandleAsync"/> 不会再被调用了。
    /// 不还的话每拒一次漏一个，漏满之后这一类通道就再也开不出来。
    /// </para>
    /// <para>
    /// 同步、必须很快、不许抛：它跑在处理这次开通道请求的后台任务上（不在接收循环上 ——
    /// 决定已经挪到后台，velashell-docs/zh/ssh/spec/05 §8.1），而同一时刻可能有好几个开通道请求各自在跑，
    /// 实现要经得起并发调用。
    /// </para>
    /// </remarks>
    void OnOpenAborted(string channelType, ReadOnlyMemory<byte> typeSpecificPayload)
    {
    }
}
