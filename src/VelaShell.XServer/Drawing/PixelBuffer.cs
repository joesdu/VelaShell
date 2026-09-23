// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer.Drawing;

/// <summary>
/// 一块软件帧缓冲:每像素一个 <see cref="uint" />,不论深度。
/// </summary>
/// <remarks>
/// 深度 1 存 0/1;深度 24 存 <c>0x00RRGGBB</c>(像素值就是 TrueColor 的 RGB,见架构 §7);
/// 深度 32 存 <c>0xAARRGGBB</c>。统一用 32 位是为了让光栅化只有一条代码路径 ——
/// 位图(深度 1)在 X 程序里只用作点画 / 裁剪 / 光标,量很小,多占的内存不值一提。
/// </remarks>
internal sealed class PixelBuffer
{
    public PixelBuffer(int width, int height, byte depth)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Depth = depth;
        Pixels = new uint[Width * Height];
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public byte Depth { get; }

    public uint[] Pixels { get; private set; }

    /// <summary>这个深度下像素值的有效位。</summary>
    public uint DepthMask => DepthMaskOf(Depth);

    public static uint DepthMaskOf(byte depth) => depth >= 32 ? 0xFFFFFFFFu : (1u << depth) - 1;

    public XRect Bounds => new(0, 0, Width, Height);

    public uint Get(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? Pixels[(y * Width) + x] : 0;

    /// <summary>改尺寸,左上角对齐保留原内容(bit-gravity NorthWest),新露出的部分填 <paramref name="fill" />。</summary>
    public void Resize(int width, int height, uint fill = 0)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }
        uint[] next = new uint[width * height];
        if (fill != 0)
        {
            Array.Fill(next, fill);
        }
        int copyW = Math.Min(width, Width);
        int copyH = Math.Min(height, Height);
        for (int y = 0; y < copyH; y++)
        {
            Array.Copy(Pixels, y * Width, next, y * width, copyW);
        }
        Pixels = next;
        Width = width;
        Height = height;
    }
}
