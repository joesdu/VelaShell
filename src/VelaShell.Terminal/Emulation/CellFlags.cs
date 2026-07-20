namespace VelaShell.Terminal.Emulation;

/// <summary>
/// <see cref="TerminalCell" /> 所承载的显示属性。对应 xterm 类终端支持的 SGR
/// (Select Graphic Rendition,选图属性)属性集。
/// </summary>
[Flags]
public enum CellFlags : ushort
{
    /// <summary>无显示属性。</summary>
    None = 0,

    /// <summary>加粗或增强亮度的文本(SGR 1)。</summary>
    Bold = 1 << 0,

    /// <summary>暗淡或减弱亮度的文本(SGR 2)。</summary>
    Dim = 1 << 1,

    /// <summary>斜体文本(SGR 3)。</summary>
    Italic = 1 << 2,

    /// <summary>带下划线的文本(SGR 4)。</summary>
    Underline = 1 << 3,

    /// <summary>闪烁的文本(SGR 5)。</summary>
    Blink = 1 << 4,

    /// <summary>在渲染时交换前景色与背景色(SGR 7)。</summary>
    Inverse = 1 << 5,

    /// <summary>文本不被绘制(SGR 8)。</summary>
    Invisible = 1 << 6,
    /// <summary>带删除线的文本(SGR 9)。</summary>
    Strikethrough = 1 << 7,

    /// <summary>双下划线文本(SGR 21)。</summary>
    DoubleUnderline = 1 << 8,

    /// <summary>一个双宽字符的第二个单元格,自身不承载字形。</summary>
    WideTrailing = 1 << 9,

    /// <summary>该单元格由 DEC 特殊图形字符集(制线字符)写入。</summary>
    Protected = 1 << 10
}
