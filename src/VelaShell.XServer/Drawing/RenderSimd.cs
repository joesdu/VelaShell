// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— §9「Composite」与 §4「Operators」的 Over(dst = src + (1 − αsrc)·dst,预乘)。
//   这里只是 RenderCompositor 两条快路径(纯色 IN 单字节遮罩 OVER 8888、8888 图像 OVER 8888)的向量版:每个字节通道上做的运算
//   与标量版逐项相同(乘、Div255 的 (t + 128 + ((t + 128) >> 8)) >> 8、相加、夹到 255),结果逐位一致,只是一次算 4 个像素。
//   用 System.Runtime.Intrinsics 的跨平台 Vector128(x64 的 SSE2 / Arm64 的 AdvSIMD);不加速或是大端机时调用方走标量版。

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VelaShell.XServer.Drawing;

internal static class RenderSimd
{
    /// <summary>能用向量版:硬件加速、小端(像素 0xAARRGGBB 在内存里是 B、G、R、A)。</summary>
    public static bool Enabled { get; } = Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    /// <summary>
    /// 把每个像素的 alpha 字节铺满它的四个字节:32 位通道右移 24 位再乘 0x01010101。不用逐字节的 Shuffle —— 实测它在这里
    /// 没有被编成一条指令,一行字形反倒比标量版慢两三倍。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> SpreadAlpha(Vector128<byte> pixels) =>
        ((pixels.AsUInt32() >>> 24) * 0x01010101u).AsByte();

    /// <summary>
    /// 8888 图像 OVER 8888 目标的一行:每个通道 out = s + Div255(d·(255 − αs)),夹到 255;αs = 0 的像素不碰目标(与标量版一样,
    /// 哪怕源的颜色不为 0);目标没有 alpha 时结果的 alpha 字节清零。返回处理了几个像素(其余的由调用方按标量处理)。
    /// </summary>
    public static int OverImageRow(ReadOnlySpan<uint> source, Span<uint> destination, bool dstAlpha) =>
        OverRow(source, default, destination, dstAlpha ? 0xFFFFFFFFu : 0x00FFFFFFu);

    /// <summary>
    /// 通用整数路径(RenderCompositor.CombineRow)的 Over 一行:<paramref name="mask" /> 不为空时源先逐通道乘遮罩的 alpha
    /// (Div255,同 <see cref="Argb8.Scale" />),然后同 <see cref="OverImageRow" />;写入的像素按 <paramref name="keep" />
    /// (目标没有 alpha 时清 alpha 字节、再与深度掩码相与)截,αs = 0 的像素不碰。返回处理了几个像素。
    /// </summary>
    public static int OverRow(ReadOnlySpan<uint> source, ReadOnlySpan<uint> mask, Span<uint> destination, uint keep)
    {
        int n = Math.Min(source.Length, destination.Length) & ~3;
        if (!mask.IsEmpty)
        {
            n = Math.Min(n, mask.Length & ~3);
        }
        ReadOnlySpan<byte> s = MemoryMarshal.AsBytes(source);
        Span<byte> d = MemoryMarshal.AsBytes(destination);
        Vector128<uint> alphaMask = Vector128.Create(keep);
        for (int i = 0; i < n; i += 4)
        {
            Vector128<byte> src = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(s), (nuint)(i * 4));
            if (!mask.IsEmpty)
            {
                Vector128<uint> m = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(mask), (nuint)i);
                src = MultiplyDiv255(src, ((m >>> 24) * 0x01010101u).AsByte());
            }
            Vector128<byte> dst = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(d), (nuint)(i * 4));
            Vector128<byte> alpha = SpreadAlpha(src);
            Vector128<byte> inv = Vector128<byte>.AllBitsSet - alpha;
            Vector128<byte> blended = (AddScaled(src, dst, inv).AsUInt32() & alphaMask).AsByte();
            // αs = 0 的像素原样留着(连 xRGB 目标那个无意义的高字节也不碰,与标量版逐位一致)。
            Vector128.ConditionalSelect(Vector128.Equals(alpha, Vector128<byte>.Zero), dst, blended)
                .StoreUnsafe(ref MemoryMarshal.GetReference(d), (nuint)(i * 4));
        }
        return n;
    }

    /// <summary>
    /// 纯色 IN 单字节遮罩 OVER 8888 目标的一行:每个通道 out = Div255(c·m) + Div255(d·(255 − Div255(αc·m))),夹到 255;
    /// 目标没有 alpha 时 alpha 字节清零。m = 0 与 m = 255 且纯色不透明的情形,这个式子算出来恰好就是标量版的两个捷径(不碰、整块覆盖)。
    /// <paramref name="color" /> 是 0xAARRGGBB 的纯色。返回处理了几个像素。
    /// </summary>
    public static int OverSolidMaskRow(uint color, ReadOnlySpan<byte> mask, Span<uint> destination, bool dstAlpha)
    {
        int n = Math.Min(mask.Length, destination.Length) & ~3;
        Span<byte> d = MemoryMarshal.AsBytes(destination);
        Vector128<byte> c = Vector128.Create(color).AsByte();
        Vector128<uint> alphaMask = Vector128.Create(dstAlpha ? 0xFFFFFFFFu : 0x00FFFFFFu);
        for (int i = 0; i < n; i += 4)
        {
            uint m4 = Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in mask[i]));
            if (m4 == 0)
            {
                continue;   // 四个都不覆盖:目标不变
            }
            // 每个像素的遮罩字节铺满它的四个通道:放进各自的 32 位通道再乘 0x01010101。
            Vector128<byte> m = (Vector128.Create((uint)mask[i], mask[i + 1], mask[i + 2], mask[i + 3]) * 0x01010101u).AsByte();
            Vector128<byte> dst = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(d), (nuint)(i * 4));
            Vector128<byte> cm = MultiplyDiv255(c, m);   // alpha 字节上是 a = Div255(αc·m)
            Vector128<byte> inv = Vector128<byte>.AllBitsSet - SpreadAlpha(cm);
            Vector128<byte> blended = (AddScaled(cm, dst, inv).AsUInt32() & alphaMask).AsByte();
            // 遮罩为 0 的像素原样留着(同上)。
            Vector128.ConditionalSelect(Vector128.Equals(m, Vector128<byte>.Zero), dst, blended)
                .StoreUnsafe(ref MemoryMarshal.GetReference(d), (nuint)(i * 4));
        }
        return n;
    }

    /// <summary>
    /// 通用整数路径的 Add 一行:源(有遮罩时先乘遮罩的 alpha)与目标逐通道饱和相加;写入的像素按 <paramref name="keep" /> 截。
    /// 与标量版一样每个像素都写(alpha 为 0、颜色不为 0 的源照样加)。返回处理了几个像素。
    /// </summary>
    public static int AddRow(ReadOnlySpan<uint> source, ReadOnlySpan<uint> mask, Span<uint> destination, uint keep)
    {
        int n = Math.Min(source.Length, destination.Length) & ~3;
        if (!mask.IsEmpty)
        {
            n = Math.Min(n, mask.Length & ~3);
        }
        ReadOnlySpan<byte> s = MemoryMarshal.AsBytes(source);
        Span<byte> d = MemoryMarshal.AsBytes(destination);
        Vector128<uint> keepMask = Vector128.Create(keep);
        Vector128<byte> one = Vector128<byte>.AllBitsSet;
        for (int i = 0; i < n; i += 4)
        {
            Vector128<byte> src = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(s), (nuint)(i * 4));
            if (!mask.IsEmpty)
            {
                Vector128<uint> m = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(mask), (nuint)i);
                src = MultiplyDiv255(src, ((m >>> 24) * 0x01010101u).AsByte());
            }
            Vector128<byte> dst = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(d), (nuint)(i * 4));
            // AddScaled 乘 255:Div255(d·255) 恰好是 d,于是 = min(s + d, 255)。
            (AddScaled(src, dst, one).AsUInt32() & keepMask).AsByte().StoreUnsafe(ref MemoryMarshal.GetReference(d), (nuint)(i * 4));
        }
        return n;
    }

    /// <summary>逐字节 min(a + Div255(b·k), 255)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AddScaled(Vector128<byte> a, Vector128<byte> b, Vector128<byte> k)
    {
        (Vector128<ushort> aLo, Vector128<ushort> aHi) = Vector128.Widen(a);
        (Vector128<ushort> bLo, Vector128<ushort> bHi) = Vector128.Widen(b);
        (Vector128<ushort> kLo, Vector128<ushort> kHi) = Vector128.Widen(k);
        Vector128<ushort> max = Vector128.Create((ushort)255);
        Vector128<ushort> lo = Vector128.Min(aLo + Div255(bLo * kLo), max);
        Vector128<ushort> hi = Vector128.Min(aHi + Div255(bHi * kHi), max);
        return Vector128.Narrow(lo, hi);
    }

    /// <summary>逐字节 Div255(a·b)(结果 ≤ 255,不用夹)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> MultiplyDiv255(Vector128<byte> a, Vector128<byte> b)
    {
        (Vector128<ushort> aLo, Vector128<ushort> aHi) = Vector128.Widen(a);
        (Vector128<ushort> bLo, Vector128<ushort> bHi) = Vector128.Widen(b);
        return Vector128.Narrow(Div255(aLo * bLo), Div255(aHi * bHi));
    }

    /// <summary>与 <see cref="Argb8.Div255" /> 相同的取整:t ≤ 255·255 时 (t + 128 + ((t + 128) >> 8)) >> 8,16 位里不会溢出。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Div255(Vector128<ushort> t)
    {
        t += Vector128.Create((ushort)128);
        return (t + Vector128.ShiftRightLogical(t, 8)) >>> 8;
    }
}
