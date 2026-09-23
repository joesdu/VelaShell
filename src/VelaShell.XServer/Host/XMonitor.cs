// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer.Host;

/// <summary>
/// 一台显示器在根窗口(虚拟桌面)里占的矩形。RANDR、XINERAMA 都据此回答「有几台显示器、各在哪」。
/// </summary>
/// <param name="X">左上角在根窗口坐标里的位置。</param>
/// <param name="Y">左上角在根窗口坐标里的位置。</param>
/// <param name="Width">宽,像素。</param>
/// <param name="Height">高,像素。</param>
public sealed record XMonitor(int X, int Y, int Width, int Height)
{
    /// <summary>名字(RANDR 的输出名与显示器名),如 <c>DP-1</c>。</summary>
    public string Name { get; init; } = "default";

    /// <summary>物理宽,毫米;0 = 按 <see cref="XServerOptions.Dpi" /> 换算。</summary>
    public int WidthMillimeters { get; init; }

    /// <summary>物理高,毫米;0 = 按 <see cref="XServerOptions.Dpi" /> 换算。</summary>
    public int HeightMillimeters { get; init; }

    /// <summary>主显示器(面板、通知一类的东西摆在这上面)。没有哪台标了主显示器时第一台就是。</summary>
    public bool Primary { get; init; }

    /// <summary>刷新率,Hz。</summary>
    public int RefreshRate { get; init; } = 60;
}
