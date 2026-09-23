// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.2  pty-req 的宽高四字段
//   RFC 4254 §6.7  window-change
//   RFC 4254 §8    终端模式编码
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.3

using System.Buffers;
using VelaShell.Ssh.Protocol;

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
/// 然后按四十亿列排版。那不是「尺寸不对」，是**乱码**，
/// 而报错会出现在远端程序里，指不回这里。所以在构造时就拦住。
/// </para>
/// </remarks>
public readonly record struct TerminalSize
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
    public TerminalSize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
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
    public static TerminalSize Default => new(80, 24);

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

/// <summary>终端模式的操作码（RFC 4254 §8）。</summary>
/// <remarks>
/// 只给常用的具名成员；其余的直接传裸数值即可 ——
/// 这张表不可能穷举，而未知的值本来就该原样传过去。
/// </remarks>
public enum TerminalModeOpcode : byte
{
    /// <summary>模式列表结束。<b>必须</b>以它结尾，漏掉 OpenSSH 会拒绝整个 <c>pty-req</c>。</summary>
    EndOfOptions = 0,

    /// <summary>中断字符，通常是 <c>^C</c>（VINTR）。</summary>
    InterruptCharacter = 1,

    /// <summary>退出字符（VQUIT）。</summary>
    QuitCharacter = 2,

    /// <summary>退格字符（VERASE）。</summary>
    EraseCharacter = 3,

    /// <summary>整行删除字符（VKILL）。</summary>
    KillCharacter = 4,

    /// <summary>文件结束字符，通常是 <c>^D</c>（VEOF）。</summary>
    EndOfFileCharacter = 5,

    /// <summary>输入时把 CR 转成 NL。</summary>
    MapCrToNlOnInput = 36,

    /// <summary>输入按 UTF-8 处理。</summary>
    Utf8Input = 42,

    /// <summary>规范模式（行编辑）。</summary>
    CanonicalInput = 51,

    /// <summary>回显输入。</summary>
    Echo = 53,

    /// <summary>输出时把 NL 转成 CR-NL。</summary>
    MapNlToCrNlOnOutput = 72,

    /// <summary>输入波特率。</summary>
    InputSpeed = 128,

    /// <summary>输出波特率。</summary>
    OutputSpeed = 129,
}

/// <summary>一组终端模式。</summary>
/// <remarks>
/// 不给就发一个只含 <see cref="TerminalModeOpcode.EndOfOptions"/> 的空表 ——
/// 那表示「用服务端的默认值」，是最常见也最安全的选择。
/// </remarks>
public sealed class TerminalModes
{
    private readonly List<(byte Opcode, uint Argument)> _entries = [];

    /// <summary>空的模式表：一切按服务端默认。</summary>
    /// <remarks>
    /// 每次返回一个**新实例**。<see cref="Set(byte, uint)"/> 会就地修改，
    /// 共享一个静态实例的话，任何一处 Set 都会污染所有其它调用方。
    /// </remarks>
    public static TerminalModes Empty => new();

    /// <summary>设一个具名模式。</summary>
    public TerminalModes Set(TerminalModeOpcode opcode, uint argument) =>
        Set((byte)opcode, argument);

    /// <summary>设一个裸操作码 —— 这张表不可能穷举，未知的值原样传过去。</summary>
    public TerminalModes Set(byte opcode, uint argument)
    {
        if (opcode == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode), "0 是模式表的结束标记，不能当成一个模式来设。");
        }

        // 160–255 是保留区：RFC 4254 §8 说解析方遇到未知的应当停止解析，
        // 所以往里塞东西会把后面所有模式一起弄丢。
        if (opcode >= 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                $"操作码 {opcode} 落在保留区（160–255）。对端遇到它会停止解析，" +
                "排在后面的模式会被一起丢掉。");
        }

        _entries.Add((opcode, argument));
        return this;
    }

    /// <summary>编码成 <c>pty-req</c> 里那个 string 的内容。</summary>
    internal byte[] Encode()
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        foreach ((byte opcode, uint argument) in _entries)
        {
            writer.WriteByte(opcode);
            writer.WriteUInt32(argument);
        }
        writer.WriteByte((byte)TerminalModeOpcode.EndOfOptions);
        return buffer.WrittenSpan.ToArray();
    }
}
