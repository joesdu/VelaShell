// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenGL Graphics with the X Window System, Version 1.4 —— §2.2「Rendering Contexts and Drawing Surfaces」
//   (GLX 可绘对象的颜色缓冲来自 FBConfig:双缓冲有前后两块,深度、模板等辅助缓冲没有对外的名字)。
//   The OpenGL Graphics System, Version 1.5 —— §4.2.1「Selecting a Buffer for Writing」(FRONT / BACK)。

namespace VelaShell.XServer.Gl;

/// <summary>
/// 一个 GLX 可绘对象的帧缓冲:颜色(0xAARRGGBB,按 X 的行序 —— 第 0 行在最上面)、深度与模板。
/// 前缓冲由服务端在刷新 / 交换时拷进对应的 X 窗口或像素图。
/// </summary>
internal sealed class GlSurface
{
    public GlSurface(int width, int height, bool doubleBuffered, bool hasAlpha)
    {
        DoubleBuffered = doubleBuffered;
        HasAlpha = hasAlpha;
        Front = [];
        Depth = [];
        Stencil = [];
        Resize(width, height);
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool DoubleBuffered { get; }

    public bool HasAlpha { get; }

    public uint[] Front { get; private set; }

    public uint[]? Back { get; private set; }

    public float[] Depth { get; private set; }

    public byte[] Stencil { get; private set; }

    /// <summary>前缓冲自上次拷出后被画过。</summary>
    public bool FrontDirty { get; set; }

    /// <summary>尺寸跟随 X 可绘对象;变了就重新分配(内容未定义,这里清零)。</summary>
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, 16384);
        height = Math.Clamp(height, 1, 16384);
        if (width == Width && height == Height)
        {
            return;
        }
        Width = width;
        Height = height;
        Front = new uint[width * height];
        Back = DoubleBuffered ? new uint[width * height] : null;
        Depth = new float[width * height];
        Array.Fill(Depth, 1f);
        Stencil = new byte[width * height];
    }

    /// <summary>颜色缓冲:BACK 只在双缓冲时存在,否则落到前缓冲。</summary>
    public uint[] Color(bool back) => back && Back is not null ? Back : Front;

    /// <summary>glXSwapBuffers:前后缓冲互换(交换后后缓冲的内容未定义 —— 这里就是交换前的前缓冲)。</summary>
    public void Swap()
    {
        if (Back is not null)
        {
            (Front, Back) = (Back, Front);
            FrontDirty = true;
        }
    }
}
