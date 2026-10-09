// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 一条连接的信任级别(SECURITY 扩展的 trust-level):宿主经
/// <see cref="X11Server.ServeAuthenticatedAsync(System.IO.Stream, string?, XClientTrust, System.Threading.CancellationToken)" /> 指明,
/// 或者客户端用 SecurityGenerateAuthorization 签出的 cookie 连进来时由那个授权决定。
/// </summary>
public enum XClientTrust
{
    /// <summary>受信:与本机程序一样,能看、能改整个显示(<c>ssh -Y</c>)。</summary>
    Trusted,

    /// <summary>
    /// 非受信(<c>ssh -X</c>):只看得见、改得了非受信客户端自己的窗口与资源;拿不到 XTEST、别人窗口的图像与属性、
    /// 不属于它的键盘输入,不能改键位表与键盘设置(SECURITY 规范第三章)。
    /// </summary>
    Untrusted,
}
