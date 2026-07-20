namespace VelaShell.Terminal.Emulation;

/// <summary>
/// DEC 特殊图形("制线")字符集的翻译,该字符集把 ASCII 0x60-0x7E 映射到制线字符与方块字形。
/// 通过 <c>ESC ( 0</c> 选择,被 ncurses/TUI 程序大量使用。
/// </summary>
public static class Charsets
{
    // 以 (rune - 0x60) 为下标索引。'\0' 表示"不翻译"。
    private static readonly char[] DecSpecial =
    [
        '◆', // ` 菱形
        '▒', // a 棋盘
        '␉', // b HT
        '␌', // c FF
        '␍', // d CR
        '␊', // e LF
        '°', // f 度
        '±', // g 加减
        '␤', // h NL
        '␋', // i VT
        '┘', // j 右下角
        '┐', // k 右上角
        '┌', // l 左上角
        '└', // m 左下角
        '┼', // n 十字交叉线
        '⎺', // o 扫描线 1
        '⎻', // p 扫描线 3
        '─', // q 水平线
        '⎼', // r 扫描线 7
        '⎽', // s 扫描线 9
        '├', // t 左T形
        '┤', // u 右T形
        '┴', // v 下T形
        '┬', // w 上T形
        '│', // x 垂直线
        '≤', // y 小于等于
        '≥', // z 大于等于
        'π', // { 圆周率
        '≠', // | 不等于
        '£', // } 英镑符号
        '·'  // ~ 中点
    ];

    /// <summary>
    /// 通过一个 rune 进行 DEC 特殊图形字符集翻译。0x60-0x7E 范围之外的 rune 原样透传;
    /// 范围内的则映射到对应的制线/方块字形。
    /// </summary>
    /// <param name="rune">输入的字符码点。</param>
    /// <returns>翻译后的码点;若未映射则返回原始 <paramref name="rune" />。</returns>
    public static int MapDecSpecial(int rune)
    {
        if (rune is < 0x60 or > 0x7E)
        {
            return rune;
        }
        char mapped = DecSpecial[rune - 0x60];
        return mapped;
    }
}
