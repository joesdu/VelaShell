// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

/// <summary>
/// 宿主手里的一个顶层窗口:身份(<see cref="Id" />)、属性快照(<see cref="Snapshot" />)与像素。
/// 宿主经 <see cref="X11Server" /> 的方法对它注入输入、移动 / 缩放 / 关闭时,就用这个对象指名。
/// </summary>
/// <remarks>
/// 同一个 X 窗口从映射到销毁(或被 reparent 走)一直是同一个对象;之后再用它调服务端的方法会被静默忽略 ——
/// 宿主与服务端之间总有这样的时间差,不算错误。
/// </remarks>
public sealed class XTopLevelWindow
{
    private readonly PixelGate _pixelGate;

    internal XTopLevelWindow(XWindow window, PixelGate pixelGate)
    {
        Window = window;
        Id = window.Id;
        _pixelGate = pixelGate;
    }

    /// <summary>窗口 ID(XID)。诊断、日志与宿主自己的索引用;调服务端的方法时传这个对象本身。</summary>
    public uint Id { get; }

    /// <summary>
    /// 当前的属性快照。服务端在执行线程上整份替换它(引用赋值是原子的),可以在任意线程上读;
    /// 要读多个字段时先取到局部变量里,见 <see cref="XTopLevelSnapshot" />。
    /// </summary>
    public XTopLevelSnapshot Snapshot
    {
        get => Volatile.Read(ref field);
        internal set => Volatile.Write(ref field, value);
    } = new();

    /// <summary>服务端这边的窗口(只在执行线程上碰)。</summary>
    internal XWindow Window { get; }

    /// <summary>这个句柄是不是由持有这把像素锁的服务端发出的。</summary>
    internal bool BelongsTo(PixelGate gate) => ReferenceEquals(_pixelGate, gate);

    /// <summary>
    /// 拷贝当前像素(深度 24 的窗口是 <c>0x00RRGGBB</c>,高 8 位无意义;<see cref="XTopLevelSnapshot.HasAlpha" /> 时是预乘的 <c>0xAARRGGBB</c>),
    /// 行优先,宽 × 高。<paramref name="destination" /> 不够大时只拷能放下的部分。
    /// </summary>
    /// <returns>实际拷贝时的 (宽, 高);窗口已没有缓冲时为 (0, 0)。</returns>
    /// <remarks>整窗拷一遍。每帧都要读的宿主用 <see cref="ReadPixels" />:只读变了的那几块,直接写进自己的位图,省掉这一趟中转。</remarks>
    public (int Width, int Height) CopyPixels(Span<uint> destination)
    {
        _pixelGate.EnterHost();
        try
        {
            if (Window.Buffer is not { } buffer)
            {
                return (0, 0);
            }
            int count = Math.Min(destination.Length, buffer.Width * buffer.Height);
            buffer.Pixels.AsSpan(0, count).CopyTo(destination);
            return (buffer.Width, buffer.Height);
        }
        finally
        {
            _pixelGate.ExitHost();
        }
    }

    /// <summary>
    /// 在像素锁里把当前像素交给 <paramref name="reader" />:行优先,<c>width × height</c> 个,格式同 <see cref="CopyPixels" />。
    /// 宽高取自此刻的缓冲本身 —— 宿主之前读到的快照尺寸可能已经过时(客户端刚改了尺寸)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="reader" /> 执行期间服务端不画(执行线程在等锁),<see cref="Snapshot" /> 也不会换,所以要快:只拷要的那几块,
    /// 不要在里面分配大块内存、等别的锁或同步调服务端的方法。宿主在等这把锁时,执行线程会尽快让出(执行完手上那一项就放锁,读完再拿)。
    /// </para>
    /// </remarks>
    /// <returns>窗口已没有缓冲(取消映射、销毁)时不调 <paramref name="reader" />,返回 <see langword="false" />。</returns>
    public bool ReadPixels(XPixelReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _pixelGate.EnterHost();
        try
        {
            if (Window.Buffer is not { } buffer)
            {
                return false;
            }
            reader(buffer.Pixels.AsSpan(0, buffer.Width * buffer.Height), buffer.Width, buffer.Height);
            return true;
        }
        finally
        {
            _pixelGate.ExitHost();
        }
    }
}

/// <summary>
/// 读顶层像素的回调(见 <see cref="XTopLevelWindow.ReadPixels" />):<paramref name="pixels" /> 行优先,
/// <paramref name="width" /> × <paramref name="height" /> 个,只在回调期间有效。
/// </summary>
public delegate void XPixelReader(ReadOnlySpan<uint> pixels, int width, int height);
