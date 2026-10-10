// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>一个 X 客户端某一刻的样子(<see cref="X11Server.GetClientsAsync" />)。</summary>
/// <param name="Id">客户端编号(它的资源 ID 的高位);<see cref="X11Server.DisconnectClient(int)" /> 用它。</param>
/// <param name="Label">
/// 宿主给这条连接起的名字(<see cref="X11Server.ServeAuthenticatedAsync(System.IO.Stream, string?, System.Threading.CancellationToken)" />);没给为 null。
/// </param>
/// <param name="Retained">已经以 Retain 模式断开(SetCloseDownMode),只剩资源还留着。</param>
/// <param name="ResourceCount">名下的资源数(窗口、像素图、GC、字体……)。</param>
/// <param name="MemoryBytes">记在它账上的内存,字节(见 <see cref="X11ServerOptions.MaxClientMemory" />)。</param>
/// <param name="TopLevels">它的顶层窗口(宿主见过的,映射着的)。</param>
/// <param name="Trust">信任级别(见 <see cref="XClientTrust" />)。</param>
/// <param name="HoldsServerGrab">
/// 它正抓着整个服务端(GrabServer):别的客户端的请求都在等它放开。抓得太久时另有 <see cref="IX11ServerHost.ServerGrabStalled" />。
/// </param>
public sealed record XClientInfo(int Id, string? Label, bool Retained, int ResourceCount, long MemoryBytes, IReadOnlyList<XTopLevelWindow> TopLevels,
    XClientTrust Trust = XClientTrust.Trusted, bool HoldsServerGrab = false);
