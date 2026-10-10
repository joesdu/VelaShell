// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 一个客户端 GrabServer 抓着太久、别的客户端的请求都在等(<see cref="IX11ServerHost.ServerGrabStalled" />)。
/// 持有者挂住时(远端进程被 SIGSTOP、SSH 断网而连接没断)所有会话的 X 程序都冻着,宿主据此提示用户断开它
/// (<see cref="X11Server.DisconnectClient(int)" />)或解除抓取(<see cref="X11Server.BreakGrabs" />)。
/// </summary>
/// <param name="ClientId">持有者的编号(<see cref="XClientInfo.Id" />)。</param>
/// <param name="ClientLabel">宿主给持有者这条连接起的名字(<see cref="XClientInfo.Label" />);没给为 null。</param>
/// <param name="Held">已经抓了多久。</param>
/// <param name="WaitingRequests">别的客户端暂存着、等它放开的请求数。</param>
public sealed record XServerGrabStall(int ClientId, string? ClientLabel, TimeSpan Held, int WaitingRequests);
