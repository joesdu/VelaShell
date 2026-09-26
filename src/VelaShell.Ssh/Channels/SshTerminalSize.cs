// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.2  pty-req 的宽高四字段
//   RFC 4254 §6.7  window-change
//   RFC 4254 §8    终端模式编码
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.3

namespace VelaShell.Ssh.Channels;

/// <summary>终端尺寸。</summary>
/// <remarks>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/05 §5.3〕<b>像素尺寸是一等公民，不恒为 0。</b>
/// </para>
/// <para>
/// 依赖像素尺寸的程序是真实存在的 —— sixel 图像、kitty 图形协议，
/// 以及任何要按像素排版的 TUI。把它写死成 0 就等于告诉远端「不知道」，
/// 而那会让这些程序退化或者干脆不工作。
/// </para>
/// <para>
/// 像素值由使用者给（终端控件知道自己的字形尺寸），库不猜。
/// 使用者给 0 时照常发 0，语义仍是「不知道」。
/// </para>
/// <para>
/// ⚠️ <b>四个字段都不接受负数。</b>线上是 <c>uint32</c>，
/// <c>-1</c> 无声无息地变成 <c>4294967295</c> —— 远端照单全收，
/// 然后按四十亿列排版。那不是「尺寸不对」，是<b>乱码</b>，
/// 而报错会出现在远端程序里，指不回这里。所以在构造时就拦住。
/// </para>
/// </remarks>
public readonly record struct SshTerminalSize
{
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    /// <summary>建一个终端尺寸。</summary>
    /// <param name="columns">宽（字符列数）。</param>
    /// <param name="rows">高（字符行数）。</param>
    /// <param name="pixelWidth">宽（像素）。<c>0</c> 表示「不知道」。</param>
    /// <param name="pixelHeight">高（像素）。<c>0</c> 表示「不知道」。</param>
    /// <exception cref="ArgumentOutOfRangeException">任何一个是负数。</exception>
    /// <remarks>
    /// 没写成位置式记录，是因为那样四个属性只能是自动属性，插不进校验；
    /// 换成自己写的 init 访问器之后，构造与 <c>with</c> 都会走到校验。
    /// <c>Deconstruct</c> 在下面手写补回来了。
    /// </remarks>
    public SshTerminalSize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        _columns = NonNegative(columns, nameof(columns));
        _rows = NonNegative(rows, nameof(rows));
        _pixelWidth = NonNegative(pixelWidth, nameof(pixelWidth));
        _pixelHeight = NonNegative(pixelHeight, nameof(pixelHeight));
    }

    /// <summary>宽（字符列数）。</summary>
    public int Columns
    {
        get => _columns;
        init => _columns = NonNegative(value, nameof(Columns));
    }

    /// <summary>高（字符行数）。</summary>
    public int Rows
    {
        get => _rows;
        init => _rows = NonNegative(value, nameof(Rows));
    }

    /// <summary>宽（像素）。<c>0</c> 表示「不知道」。</summary>
    public int PixelWidth
    {
        get => _pixelWidth;
        init => _pixelWidth = NonNegative(value, nameof(PixelWidth));
    }

    /// <summary>高（像素）。<c>0</c> 表示「不知道」。</summary>
    public int PixelHeight
    {
        get => _pixelHeight;
        init => _pixelHeight = NonNegative(value, nameof(PixelHeight));
    }

    /// <summary>一个常见的默认尺寸，80×24，像素未知。</summary>
    public static SshTerminalSize Default => new(80, 24);

    /// <summary>解构成四个字段。</summary>
    public void Deconstruct(out int columns, out int rows, out int pixelWidth, out int pixelHeight)
    {
        columns = _columns;
        rows = _rows;
        pixelWidth = _pixelWidth;
        pixelHeight = _pixelHeight;
    }

    private static int NonNegative(int value, string name) =>
        value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(
                name, value,
                "终端尺寸不能是负数 —— 线上是 uint32，负数会变成一个接近四十亿的值发出去。");
}
