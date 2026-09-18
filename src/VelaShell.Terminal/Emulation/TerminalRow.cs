using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端网格中的一行:一个 <see cref="TerminalCell" /> 数组,外加一个 "wrapped" 标志,
/// 用于在改变列宽时重新排版软换行行,以及为复制而合并行。
/// </summary>
/// <remarks>
/// <para>
/// <b>存储可以比逻辑列宽短。</b><see cref="Columns" /> 始终是这一行「有多宽」,而
/// <c>_cells</c> 只存到最后一个有内容的格 —— 行退休进回滚区时由 <see cref="TrimToContent" />
/// 截短。尾部那段没存的格在读取时按 <c>default</c>(默认色、无属性的空格)合成。
/// </para>
/// <para>
/// 这么做是因为回滚缓冲整行满宽存储的代价按整个缓冲区放大:200 列 × 20 万行 × 16 B ≈
/// <b>640 MB / 标签页</b>,而典型日志行只有几十列非空。实测(<c>ScrollbackBenchmarks</c>)
/// 灌 1 万行 20 字符的日志,80 列占 13.6 MB、200 列占 31.9 MB —— 内容一模一样,
/// 内存却跟着列宽走。
/// </para>
/// <para>
/// <b>截短是无损的</b>:只丢弃与 <c>default</c> 逐字段相等的尾部格,而读取正是合成
/// <c>default</c>。带背景色的行尾(程序设了底色再换行,屏幕上看得见)不满足这个条件,
/// 不会被丢。任何写入路径都会先把存储补回逻辑列宽,所以"写了又读"永远拿到自己写的东西。
/// </para>
/// </remarks>
public sealed class TerminalRow(int columns)
{
    private TerminalCell[] _cells = new TerminalCell[columns];

    /// <summary>
    /// 与 <see cref="_cells" /> 平行的 OSC 8 超链接句柄数组(0 = 该格无链接);
    /// 整行都不带链接时为 null,一格不占。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不塞进 <see cref="TerminalCell" />。</b>单元格是 16 字节且不含托管引用,
    /// 由 <c>TerminalCellMemoryTests</c> 的两条断言把守;再加一个 <see cref="ushort" /> 会因对齐
    /// 涨到 20 字节 —— 回滚缓冲每标签页上百万格,那是 25% 的无条件涨幅,而带链接的行万里挑一。
    /// 平行数组把这笔账精确地记在真正有链接的那些行头上(它们多付 2 B/列),
    /// 其余行只多一个 null 引用字段。xterm.js 的 <c>_extendedAttrs</c> 是同一取舍。
    /// </para>
    /// <para>
    /// 长度始终跟随 <see cref="_cells" />;读越界返回 0(= 无链接),与索引器读越界合成
    /// <c>default</c> 单元格是同一套语义。
    /// </para>
    /// </remarks>
    private ushort[]? _links;

    /// <summary>当该行由自动换行结束(而非显式换行)时为 true。</summary>
    public bool Wrapped { get; set; }

    /// <summary>
    /// 该行最后收到输出的墙上时钟时间(行号/时间侧栏用)。Null 表示尚未写入过内容
    /// 的空行——侧栏据此对空行不显示时间。行对象在滚动/换行时按引用迁入 scrollback,时间戳随之保留。
    /// </summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>
    /// 本行的 OSC 133 语义标记(提示符行 / 命令输出首行);<see cref="PromptMark.None" /> 为普通行。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Timestamp" /> 同一套生命周期:行对象在滚动 / 换行时按引用迁入 scrollback,
    /// 标记随之保留;整行被擦空即作废;改列宽重排时由 <c>TerminalScreen.ReflowResize</c> 搬运。
    /// <b>刻意不像 <c>GutterFoldModel</c> 那样按行对象引用另建一张表</b> —— 那种表在 reflow
    /// 重建行对象时只能整体作废,而挂在行上的东西能跟着内容一起被搬过去。
    /// </remarks>
    public PromptMark Mark { get; set; }

    /// <summary>
    /// 本块命令的退出码(<c>OSC 133 ; D ; n</c>);仅 <see cref="Mark" /> 为
    /// <see cref="PromptMark.Prompt" /> 的行有意义,尚未结束或对端没报时为 null。
    /// </summary>
    /// <remarks>记在<b>提示符行</b>而不是输出末行:块的身份由提示符行代表,侧栏那个标记也画在这里。</remarks>
    public int? ExitCode { get; set; }

    /// <summary>本行的单元格(列)数量。</summary>
    public int Columns { get; private set; } = columns;

    /// <summary>
    /// 实际存储的列数;截短过的行小于 <see cref="Columns" />。
    /// </summary>
    /// <remarks>
    /// <see cref="Span" /> 只覆盖这一段,批量拷贝的调用方据此夹取值范围。
    /// </remarks>
    public int StoredColumns => _cells.Length;

    /// <summary>获取或设置指定列索引处的单元格。</summary>
    /// <remarks>
    /// 读越界不抛:截短过的行尾部本就没有存储,合成一个 <c>default</c> 空格返回 ——
    /// 那正是被丢掉的那些格的内容(见类型注释)。渲染、选区、搜索都按屏幕列宽整行扫,
    /// 让它们各自去判断行有多长只会把这个细节撒得到处都是。
    /// 写越界则先把存储补回逻辑列宽,保证"写进去的读得回来"。
    /// </remarks>
    public TerminalCell this[int col]
    {
        get => (uint)col < (uint)_cells.Length ? _cells[col] : default;
        set
        {
            EnsureStored();
            _cells[col] = value;
        }
    }

    /// <summary>本行是否存在 OSC 8 超链接(渲染与命中判定用来整行短路)。</summary>
    public bool HasLinks => _links is not null;

    /// <summary>指定列的 OSC 8 超链接句柄;0 表示该格不是链接。越界读返回 0。</summary>
    /// <remarks>句柄由 <see cref="HyperlinkTable" /> 分配,经它换回 URI。</remarks>
    public ushort LinkAt(int col) =>
        _links is not null && (uint)col < (uint)_links.Length ? _links[col] : (ushort)0;

    /// <summary>设置指定列的超链接句柄。</summary>
    /// <remarks>
    /// 写 0 到一行从未有过链接的行上是纯粹的空操作 —— 不分配数组。打印路径每格都会调用它
    /// (哪怕当前没有链接),靠的正是这条快路径:少了这一步,"在旧链接上覆写普通文本"
    /// 会留下点得开的幽灵链接。
    /// </remarks>
    public void SetLink(int col, ushort handle)
    {
        if (handle == 0 && _links is null)
        {
            return;
        }
        if ((uint)col >= (uint)Columns)
        {
            return;
        }
        EnsureStored();
        EnsureLinkStorage();
        _links[col] = handle;
    }

    /// <summary>把 <paramref name="start" />..<paramref name="endExclusive" /> 的超链接句柄整段设为同一值(裁剪到行边界)。</summary>
    public void SetLinkRange(int start, int endExclusive, ushort handle)
    {
        if (handle == 0 && _links is null)
        {
            return;
        }
        EnsureStored();
        EnsureLinkStorage();
        int from = Math.Max(0, start);
        int to = Math.Min(_links.Length, endExclusive);
        if (to > from)
        {
            _links.AsSpan(from, to - from).Fill(handle);
        }
    }

    /// <summary>就地分配/补齐链接数组到当前存储宽度。</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_links))]
    private void EnsureLinkStorage()
    {
        if (_links is null)
        {
            _links = new ushort[_cells.Length];
        }
        else if (_links.Length < _cells.Length)
        {
            Array.Resize(ref _links, _cells.Length);
        }
    }

    /// <summary>把链接数组整段清零;整行都没有链接了就把数组一并丢掉(回滚区行常见)。</summary>
    private void ClearLinks(int start, int endExclusive)
    {
        if (_links is null)
        {
            return;
        }
        int from = Math.Max(0, start);
        int to = Math.Min(_links.Length, endExclusive);
        if (to > from)
        {
            Array.Clear(_links, from, to - from);
        }
        // 清空后整行不再有链接就把数组丢掉:重绘型 shell 反复擦行,不回收的话一条早已滚走的
        // 链接会让这一行永远多背 2 B/列。只在本就有链接的行上走到这里,故不是热路径。
        if (Array.TrueForAll(_links, static h => h == 0))
        {
            _links = null;
        }
    }

    /// <summary>返回指定列处单元格的可变引用,用于就地编辑。</summary>
    public ref TerminalCell CellRef(int col)
    {
        // 交出去的是可写引用,调用方随时会往里写:必须先有完整存储。
        EnsureStored();
        return ref _cells[col];
    }

    /// <summary>
    /// 已存储单元格的只读切片(reflow 等批量拷贝路径用,免去逐格索引)。
    /// </summary>
    /// <remarks>
    /// <b>长度是 <see cref="StoredColumns" /> 而非 <see cref="Columns" />。</b>
    /// 刻意不在这里把存储补回满宽:reflow 会遍历整个回滚区,一路补满就把刚省下的内存
    /// 全还回去了。切片超出部分的语义与索引器一致 —— 默认空格。
    /// </remarks>
    public ReadOnlySpan<TerminalCell> Span => _cells;

    /// <summary>
    /// 整行单元格的可写切片,长度恒为 <see cref="Columns" />。
    /// </summary>
    /// <remarks>
    /// 批量写入路径(<c>TerminalEmulator.PrintRun</c>)用它:逐格走索引器的话,每一格都要
    /// 付一次边界判断加一次 <see cref="EnsureStored" /> 调用,而那两件事对一整段来说做一次就够。
    /// <b>拿到的切片在下一次改变本行存储的操作之后即失效</b>,别跨调用持有。
    /// </remarks>
    public Span<TerminalCell> WritableSpan
    {
        get
        {
            EnsureStored();
            return _cells;
        }
    }

    /// <summary>
    /// 把存储补回逻辑列宽(截短过才有实际动作)。
    /// </summary>
    /// <remarks>
    /// 补出来的格填 <c>default</c>,与截短时丢掉的逐字段相等 —— 所以这是个纯粹的
    /// 表示变换,内容不变。
    /// </remarks>
    private void EnsureStored()
    {
        if (_cells.Length >= Columns)
        {
            return;
        }
        var next = new TerminalCell[Columns];
        Array.Copy(_cells, next, _cells.Length);
        _cells = next;
        if (_links is not null)
        {
            Array.Resize(ref _links, Columns);
        }
    }

    /// <summary>
    /// 按内容截短存储:丢掉尾部那段与 <c>default</c> 相等的格。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 行退休进回滚区时调用。<b>只对确定不会再被写入的行调用</b> —— 活动屏上的行随时会被
    /// 写,截了也立刻被 <see cref="EnsureStored" /> 补回去,白折腾一趟。
    /// </para>
    /// <para>
    /// 判据是「与 <c>default</c> 逐字段相等」而不是「没有字符」:行尾带背景色的空格
    /// (程序设了底色再换行)屏幕上看得见,砍掉就是可见的画面变化。带色的行因此不会被截短,
    /// 这是对的 —— 它们本来就是有内容的。
    /// </para>
    /// </remarks>
    public void TrimToContent()
    {
        int keep = LastOccupiedForTrim() + 1;
        if (keep == 0)
        {
            _cells = [];
            _links = null;
            return;
        }
        if (keep * TrimDenominator > _cells.Length * TrimNumerator)
        {
            return;
        }
        var next = new TerminalCell[keep];
        Array.Copy(_cells, next, keep);
        _cells = next;
        if (_links is not null)
        {
            Array.Resize(ref _links, keep);
        }
    }

    /// <summary>
    /// 尾部截短的边界:最后一个「有内容」的格索引 —— 单元格不等于 <c>default</c>,
    /// 或该格挂着 OSC 8 链接(链接可以铺在空格上,那也算内容);全空行返回 -1。
    /// </summary>
    /// <remarks>
    /// 与原先的逐格 <c>while</c> 循环等价,只是把「找最后一个非 default 格」交给
    /// <see cref="LastNonDefaultCellIndex" /> 的向量扫描,链接那一路交给 BCL 的
    /// <c>LastIndexOfAnyExcept</c>。带链接的空白格也是内容:OSC 8 允许把链接铺在空格上
    /// (某些 TUI 用它做整行可点区域),只看单元格会把这段可点区域连同句柄一起砍掉。
    /// </remarks>
    private int LastOccupiedForTrim()
    {
        int last = LastNonDefaultCellIndex();
        if (_links is not null)
        {
            // 链接数组按设计与本行存储等长;越出单元格范围的部分原循环本就扫不到,一并夹掉。
            int limit = Math.Min(_links.Length, _cells.Length);
            int link = limit == 0 ? -1 : _links.AsSpan(0, limit).LastIndexOfAnyExcept((ushort)0);
            if (link > last)
            {
                last = link;
            }
        }
        return last;
    }

    /// <summary>
    /// 最后一个「非 <c>default</c>」单元格的索引;整行都是默认空格时返回 -1。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>按字节成块扫,而不是逐格比字段。</b><see cref="TerminalCell" /> 的 16 字节布局里没有
    /// 填充字节,且 <see cref="TerminalColor.Default" /> 的打包表示恒为 0 —— 于是「格 == default」
    /// 与「这 16 个字节全是 0」完全等价:任何一个字段非零都必落在某个非零字节上,反之亦然。
    /// 把单元格数组当作字节流,从尾部用 <see cref="Vector{T}" /> 成块判「这一块是不是全零」,
    /// 一次比较抵一整块(块宽 / 16 个格)。
    /// </para>
    /// <para>
    /// 命中非零块后,块内是<b>按格</b>从后往前确认(至多块宽 / 16 格,AVX2 上是 2 格),
    /// 而不是逐字节回扫 —— 后者在内容恰好顶到行尾(整行满宽的换行行,<c>cat</c> 长行的常见形态)
    /// 时,要从块尾一路数到该格的 Rune 字段,反而比原实现的一次结构体比较还慢。
    /// </para>
    /// <para>
    /// 这正是「行退休进回滚区」的热路径(每滚动一行走一次),而典型日志行尾部一大段都是空格 ——
    /// 那正是成块比较比逐格快的地方。16 字节布局这个前提由
    /// <c>TerminalCellMemoryTests.TerminalCell_StaysWithinPackedSize</c> 把守。
    /// </para>
    /// </remarks>
    private int LastNonDefaultCellIndex()
    {
        if (_cells.Length == 0)
        {
            return -1;
        }
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_cells.AsSpan());
        Vector<byte> zero = Vector<byte>.Zero;
        int width = Vector<byte>.Count;
        int i = bytes.Length - width;
        for (; i >= 0; i -= width)
        {
            // Vector<T> 的 != 是「逐通道全等」的取反,即「这一块里有没有非零字节」。
            if (new Vector<byte>(bytes.Slice(i, width)) != zero)
            {
                return LastNonDefaultCellIn(i, i + width);
            }
        }
        // 头部不足一个向量的余量:同样成块判断。
        return LastNonDefaultCellIn(0, i + width);
    }

    /// <summary>
    /// 字节区间 <paramref name="start" />..<paramref name="endExclusive" /> 内最后一个非 <c>default</c>
    /// 格的索引;区间内确实有非零字节时必然找到。两个端点都是 16 的倍数(见调用方),故与格边界对齐。
    /// </summary>
    private int LastNonDefaultCellIn(int start, int endExclusive)
    {
        if (endExclusive <= start)
        {
            return -1; // 空区间(行宽恰为向量宽的整数倍时,头部余量为空)。
        }
        int first = start / CellBytes;
        for (int c = ((endExclusive - 1) / CellBytes); c >= first; c--)
        {
            if (_cells[c] != default)
            {
                return c;
            }
        }
        return -1; // 不可达:调用方只在区间含非零字节时进入。
    }

    /// <summary>
    /// <see cref="TerminalCell" /> 的字节宽度。按字节扫的前提,与 16 字节布局绑定
    /// (由 <c>TerminalCellMemoryTests.TerminalCell_StaysWithinPackedSize</c> 把守)。
    /// </summary>
    private const int CellBytes = 16;

    /// <summary>
    /// 至少要能丢掉四分之一的格才值得截短(<c>keep / stored &lt;= 3/4</c>)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET 的数组没法原地缩短,截短就得<b>新分配一个小数组再拷贝</b>。近满宽的行上
    /// 这笔买卖是亏的:实测 80 列里写满 76 个字符时,加上截短后累计分配从 14.6 MB 涨到
    /// 26.4 MB(+80%),换来的常驻内存只省 5%。而每退休一行就走一次这里,是滚动的热路径。
    /// </para>
    /// <para>
    /// 门槛把收益留在真正有收益的地方:宽终端里跑短日志(200 列 × 20 字符)才是 P-07
    /// 说的那个 640 MB 场景,那里丢掉的是九成格子。
    /// </para>
    /// </remarks>
    private const int TrimNumerator = 3;

    /// <inheritdoc cref="TrimNumerator" />
    private const int TrimDenominator = 4;

    /// <summary>用给定单元格填满整行,并清除 wrapped 标志与时间戳。</summary>
    /// <remarks>
    /// 填充交给 <c>Span&lt;T&gt;.Fill</c> 而不是手写循环:BCL 对它有向量化实现,实测
    /// (200 格 × 16 B,Release,充分预热后)比逐格 <c>for</c> 快约 25%。
    /// <b>别在这里手写 Vector128 广播</b> —— 同一条件下的实测值与手写循环持平甚至更差,
    /// 因为 16 字节结构体的逐格拷贝本就一条指令一格,手工铺开没有额外空间。
    /// <see cref="ResetFor" /> 早已是同一写法,这里只是把落单的实现补齐。
    /// </remarks>
    public void Fill(in TerminalCell cell)
    {
        EnsureStored();
        _cells.AsSpan().Fill(cell);
        _links = null; // 整行被空白覆盖:链接随之作废。
        Wrapped = false;
        Timestamp = null; // 整行清空(擦除/复用作滚动新行)→ 视为未写入,时间戳作废。
        ClearMark();
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
        EnsureStored();
        int from = Math.Max(0, start);
        int to = Math.Min(_cells.Length, endExclusive);
        if (to > from)
        {
            // 同 Fill:交给 BCL 的向量化填充,不手写循环(理由见 Fill 的 remarks)。
            _cells.AsSpan(from, to - from).Fill(cell);
        }
        ClearLinks(start, endExclusive); // 擦掉的格连同它的链接一起没了。
        if (Timestamp is not null && LastNonBlank() < 0)
        {
            Wrapped = false;
            Timestamp = null;
            // OSC 133 标记跟着时间戳走同一条判据,理由也一样:重绘型 shell 每敲一个字符都要
            // ESC[K 擦到行尾,若"擦一下就掉标记",提示符行上的标记会在打字过程中不停闪掉。
            // 只有整行真的空了才算这一行没了。
            ClearMark();
        }
    }

    /// <summary>
    /// 硬性地增缩到精确宽度。仅用于不适用重新排版的场合(备用屏,其程序在改变列宽时整体重绘)——
    /// 主屏通过 <see cref="TerminalScreen" /> 的重新排版调整大小,以保留内容。
    /// </summary>
    public void Resize(int columns, in TerminalCell blank)
    {
        if (columns == _cells.Length && columns == Columns)
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
        if (_links is not null)
        {
            Array.Resize(ref _links, columns); // 增长部分补 0(新格无链接),截断部分随格丢弃
        }
        Columns = columns;
    }

    /// <summary>在 <paramref name="col" /> 处删除 <paramref name="count" /> 个单元格,并将尾部左移。</summary>
    public void DeleteCells(int col, int count, in TerminalCell blank)
    {
        EnsureStored();
        if (count <= 0 || col >= _cells.Length)
        {
            return;
        }
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col + count, _cells, col, _cells.Length - col - count);
        if (_links is not null)
        {
            // 链接随格左移:DCH 之后剩下的字符还是原来那些字符,链接归属不能错位。
            Array.Copy(_links, col + count, _links, col, _links.Length - col - count);
        }
        FillRange(_cells.Length - count, _cells.Length, blank);
    }

    /// <summary>在 <paramref name="col" /> 处插入 <paramref name="count" /> 个空白单元格,并将尾部右移。</summary>
    public void InsertCells(int col, int count, in TerminalCell blank)
    {
        EnsureStored();
        if (count <= 0 || col >= _cells.Length)
        {
            return;
        }
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col, _cells, col + count, _cells.Length - col - count);
        if (_links is not null)
        {
            Array.Copy(_links, col, _links, col + count, _links.Length - col - count);
        }
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

    /// <summary>
    /// 最后一个<b>被占用</b>的单元格索引 —— 有字符的格,或双宽字符的尾格;全空行返回 -1。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LastNonBlank" /> 的区别只在双宽字符的尾格上:尾格自身不承载字形
    /// (<c>Rune == 0</c>),但它是那个宽字符的一半,不能与"从没写过的空格子"混为一谈。
    /// <para>
    /// reflow 的收集步骤靠它区分两种同样 <c>Rune == 0</c> 的格子:
    /// </para>
    /// <list type="bullet">
    /// <item><b>宽字符尾格</b> —— 必须保留,否则前导格会被当成单宽字符,宽字符就散了。</item>
    /// <item><b>换行填充格</b> —— 双宽字符在行尾只剩一列放不下时,自动换行会在那里留下一个
    /// 永远不会被写入的空格子。它不是内容,重排时必须丢掉,否则每经一次 reflow 就在
    /// 断点处凭空多出一个空格(<c>"触发"</c> 变 <c>"触 发"</c>)。</item>
    /// </list>
    /// </remarks>
    public int LastOccupied()
    {
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            if (_cells[i].Rune != 0 || _cells[i].IsWideTrailing)
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

    /// <summary>
    /// 把 <see cref="GetText" /> 的内容写进 <paramref name="destination" />,返回写入的字符数;
    /// 空间不足时返回 -1(调用方据此扩容重试,已写入的内容作废)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GetText" /> 逐字等价,只是不物化 string。整缓冲区扫描的场合
    /// (<see cref="BufferSearch.FindAll" /> 每次按键要过一遍全部回滚行)用它,把
    /// "每行一个 StringBuilder + 一个 string"降为"每次搜索一个复用缓冲"。
    /// 一致性由 <c>BufferSearchTests.CopyTextTo_MatchesGetText</c> 把守。
    /// </remarks>
    /// <param name="destination">接收行文本的目标缓冲。</param>
    /// <returns>写入的字符数;缓冲不足时为 -1。</returns>
    public int CopyTextTo(Span<char> destination) => CopyTextTo(destination, default);

    /// <summary>
    /// 同 <see cref="CopyTextTo(Span{char})" />,并为写出的每个字符记下它来自哪一**屏幕列**。
    /// </summary>
    /// <remarks>
    /// <b>字符下标不等于屏幕列</b>,三种情形都会让两者错开:
    /// <list type="bullet">
    /// <item>宽字符(CJK)占两列却只产出一个字符,其尾格 <c>AppendTo</c> 写 0 个字符;</item>
    /// <item>组合标记(<c>e</c> + U+0301)产出两个字符却仍占一列;</item>
    /// <item>补充平面的 emoji 产出一对代理项(两个字符)。</item>
    /// </list>
    /// 拿字符下标当列用,高亮和自动选区就会整体左移 —— 在纯 ASCII 行上永远看不出来,
    /// 一遇中文或 emoji 立刻错位。<paramref name="columnOfChar" /> 传空即退化为不记录。
    /// </remarks>
    /// <param name="destination">接收行文本的目标缓冲。</param>
    /// <param name="columnOfChar">与 <paramref name="destination" /> 等长的列映射;传 <c>default</c> 表示不需要。</param>
    /// <returns>写入的字符数;任一缓冲不足时为 -1。</returns>
    public int CopyTextTo(Span<char> destination, Span<int> columnOfChar)
    {
        bool mapping = !columnOfChar.IsEmpty;
        int lastNonBlank = LastNonBlank();
        int written = 0;
        for (int col = 0; col <= lastNonBlank; col++)
        {
            int n = _cells[col].AppendTo(destination[written..]);
            if (n < 0 || (mapping && written + n > columnOfChar.Length))
            {
                return -1;
            }
            if (mapping)
            {
                columnOfChar.Slice(written, n).Fill(col);
            }
            written += n;
        }
        return written;
    }

    /// <summary>
    /// 把本行原地复位成指定宽度的空白行(等价于 <c>new TerminalRow(columns)</c> 后 <see cref="Fill" />),
    /// 宽度不变时连单元格数组都不重新分配。
    /// </summary>
    /// <remarks>
    /// 供 <see cref="TerminalScreen" /> 的 reflow 回收复用旧行对象。改列宽会对整个缓冲区重排,
    /// 每次都为上万行各 new 一个 <see cref="TerminalCell" /> 数组(1 万行 × 200 列 × 16B ≈ 32MB),
    /// 而拖拽改宽会连着触发几十次 —— 那些数组多数能活过一次 gen0 回收被提升,代价远不止分配本身。
    /// <b>只能对确定已经没人引用的行调用</b>(reflow 里旧行的内容已复制进收集缓冲,即为此)。
    /// </remarks>
    /// <param name="columns">复位后的列数。</param>
    /// <param name="blank">用于填充的空白单元格。</param>
    public void ResetFor(int columns, in TerminalCell blank)
    {
        if (_cells.Length != columns)
        {
            _cells = new TerminalCell[columns];
        }
        Columns = columns;
        _cells.AsSpan().Fill(blank);
        _links = null;
        Wrapped = false;
        Timestamp = null;
        ClearMark();
    }

    /// <summary>清掉本行的 OSC 133 标记与退出码(整行作废时调用)。</summary>
    private void ClearMark()
    {
        Mark = PromptMark.None;
        ExitCode = null;
    }

    /// <summary>创建本行的深拷贝,保留单元格、wrapped 标志与时间戳。</summary>
    /// <remarks>截短状态一并复制:拷贝一行不该悄悄把内存翻回满宽。</remarks>
    public TerminalRow Clone()
    {
        var clone = new TerminalRow(Columns)
        {
            Wrapped = Wrapped,
            Timestamp = Timestamp,
            Mark = Mark,
            ExitCode = ExitCode,
            _cells = new TerminalCell[_cells.Length]
        };
        Array.Copy(_cells, clone._cells, _cells.Length);
        if (_links is not null)
        {
            clone._links = (ushort[])_links.Clone();
        }
        return clone;
    }
}
