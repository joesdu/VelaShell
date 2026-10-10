// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 键盘焦点所在的 X 程序经 XIM 连着服务端(<see cref="X11ServerOptions.InputMethodName" />),有一个报了焦点的输入上下文
/// (<see cref="IX11ServerHost.InputMethodFocusChanged" />):宿主的输入法上屏的字(<see cref="X11Server.InjectText" />)经 XIM_COMMIT 交给它,
/// 候选框摆在它报的插入点。
/// </summary>
/// <param name="Window">输入上下文所在的顶层(单窗口模式下是屏幕窗口 <see cref="X11Server.Screen" />)。</param>
/// <param name="ClientDrawsPreedit">
/// 程序自己画预编辑(on-the-spot,XIMPreeditCallbacks):宿主不必叠画,把预编辑经 <see cref="X11Server.InjectPreedit" /> 交过去;
/// 为假时程序不画(over-the-spot 或根窗口风格),宿主自己在 <paramref name="Cursor" /> 处叠画。
/// </param>
/// <param name="Cursor">
/// 插入点:<paramref name="Window" /> 内区坐标里的一个竖条(宽 1、高一行,上沿按基线往上约八成行高),由程序的 XNSpotLocation 换算;
/// 程序没报插入点时为 null,宿主自己挑位置(比如最后一次点击处)。
/// </param>
public sealed record XInputMethodFocus(XTopLevelWindow Window, bool ClientDrawsPreedit, XRect? Cursor);
