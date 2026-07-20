using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端网格中的一行:一个固定长度的 <see cref="TerminalCell" /> 数组,外加一个 "wrapped" 标志,
/// 用于在改变列宽时重新排版软换行行,以及为复制而合并行。
/// </summary>
public sealed class TerminalRow(int columns)
{
    private TerminalCell[] _cells = new TerminalCell[columns];

    /// <summary>当该行由自动换行结束(而非显式换行)时为 true。</summary>
    public bool Wrapped { get; set; }

    /// <summary>
    /// 该行最后收到输出的墙上时钟时间(行号/时间侧栏用)。Null 表示尚未写入过内容
    /// 的空行——侧栏据此对空行不显示时间。行对象在滚动/换行时按引用迁入 scrollback,时间戳随之保留。
    /// </summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>本行的单元格(列)数量。</summary>
    public int Columns => _cells.Length;

    /// <summary>获取或设置指定列索引处的单元格。</summary>
    public TerminalCell this[int col]
    {
        get => _cells[col];
        set => _cells[col] = value;
    }

    /// <summary>返回指定列处单元格的可变引用,用于就地编辑。</summary>
    public ref TerminalCell CellRef(int col) => ref _cells[col];

    /// <summary>用给定单元格填满整行,并清除 wrapped 标志与时间戳。</summary>
    public void Fill(in TerminalCell cell)
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = cell;
        }
        Wrapped = false;
        Timestamp = null; // 整行清空(擦除/复用作滚动新行)→ 视为未写入,时间戳作废。
    }

    /// <summary>
    /// 把 <paramref name="start" />..<paramref name="endExclusive" /> 范围内的单元格用给定单元格填充,
    /// 并裁剪到本行边界。若擦完整行已空,时间戳一并作废(与 <see cref="Fill" /> 同一不变量)。
    /// </summary>
    /// <remarks>
    /// 这里必须与 <see cref="Fill" /> 守同一条「空行 = 未写入 = 无时间戳」的规矩:重绘型 shell
    /// (PSReadLine 等)清行用的是 ESC[K(EL 0,擦到行尾)而非 ESC[2K,走的正是这里。少了这一步,
    /// 行被擦空却留着时间戳,侧栏据 Timestamp 认定「有内容」→ 提示符下方的空行凭空显示时间,
    /// 折叠导引线也跟着画过光标位置把光标盖住。
    /// </remarks>
    public void FillRange(int start, int endExclusive, in TerminalCell cell)
    {
        for (int i = Math.Max(0, start); i < Math.Min(_cells.Length, endExclusive); i++)
        {
            _cells[i] = cell;
        }
        if (Timestamp is not null && LastNonBlank() < 0)
        {
            Wrapped = false;
            Timestamp = null;
        }
    }

    /// <summary>
    /// 硬性地增缩到精确宽度。仅用于不适用重新排版的场合(备用屏,其程序在改变列宽时整体重绘)——
    /// 主屏通过 <see cref="TerminalScreen" /> 的重新排版调整大小,以保留内容。
    /// </summary>
    public void Resize(int columns, in TerminalCell blank)
    {
        if (columns == _cells.Length)
        {
            return;
        }
        var next = new TerminalCell[columns];
        int copy = Math.Min(columns, _cells.Length);
        Array.Copy(_cells, next, copy);
        for (int i = copy; i < columns; i++)
        {
            next[i] = blank;
        }
        _cells = next;
    }

    /// <summary>在 <paramref name="col" /> 处删除 <paramref name="count" /> 个单元格,并将尾部左移。</summary>
    public void DeleteCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _cells.Length)
        {
            return;
        }
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col + count, _cells, col, _cells.Length - col - count);
        FillRange(_cells.Length - count, _cells.Length, blank);
    }

    /// <summary>在 <paramref name="col" /> 处插入 <paramref name="count" /> 个空白单元格,并将尾部右移。</summary>
    public void InsertCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _cells.Length)
        {
            return;
        }
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col, _cells, col + count, _cells.Length - col - count);
        FillRange(col, col + count, blank);
    }

    /// <summary>最后一个有内容的单元格索引;全空行返回 -1。</summary>
    public int LastNonBlank()
    {
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            if (_cells[i].Rune != 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>本行截至最后一个非空单元格的文本(尾部空格已裁剪)。</summary>
    public string GetText()
    {
        var sb = new StringBuilder(_cells.Length);
        int lastNonBlank = LastNonBlank();
        for (int i = 0; i <= lastNonBlank; i++)
        {
            _cells[i].AppendText(sb);
        }
        return sb.ToString();
    }

    /// <summary>创建本行的深拷贝,保留单元格、wrapped 标志与时间戳。</summary>
    public TerminalRow Clone()
    {
        var clone = new TerminalRow(_cells.Length) { Wrapped = Wrapped, Timestamp = Timestamp };
        Array.Copy(_cells, clone._cells, _cells.Length);
        return clone;
    }
}
