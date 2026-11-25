using System.Text;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>差异视图里一行的种类。</summary>
public enum DiffLineKind
{
    /// <summary>两边都有、未改动的上下文行。</summary>
    Context,

    /// <summary>新增行(只在新文本里)。</summary>
    Added,

    /// <summary>删除行(只在旧文本里)。</summary>
    Removed,

    /// <summary>折叠起来的一段未改动行(<see cref="DiffLine.HiddenCount" /> 行),显示成一行 <c>⋯</c>。</summary>
    Gap
}

/// <summary>
/// 差异里的一行。
/// </summary>
/// <param name="Kind">种类。</param>
/// <param name="OldNumber">旧文本里的行号(从 1 起);新增行与折叠行为 0。</param>
/// <param name="NewNumber">新文本里的行号(从 1 起);删除行与折叠行为 0。</param>
/// <param name="Text">行内容(不含换行符);折叠行为空。</param>
/// <param name="HiddenCount">折叠行藏起来的行数;其它种类为 0。</param>
public sealed record DiffLine(DiffLineKind Kind, int OldNumber, int NewNumber, string Text, int HiddenCount = 0)
{
    internal int HiddenStart { get; init; } = -1;
}

internal readonly record struct DiffTextSpan(int Start, int Length);

/// <summary>一次行级比对的结果:逐行对齐的全量结果,外加增删统计与「只留改动附近几行」的折叠视图。</summary>
public sealed class LineDiffResult
{
    internal LineDiffResult(IReadOnlyList<DiffLine> lines)
    {
        Lines = lines;
        foreach (DiffLine line in lines)
        {
            if (line.Kind == DiffLineKind.Added) Added++;
            else if (line.Kind == DiffLineKind.Removed) Removed++;
        }
    }

    internal (Dictionary<int, DiffTextSpan> Old, Dictionary<int, DiffTextSpan> New) ChangedSpans()
    {
        var oldSpans = new Dictionary<int, DiffTextSpan>();
        var newSpans = new Dictionary<int, DiffTextSpan>();
        int i = 0;
        while (i < Lines.Count)
        {
            if (Lines[i].Kind != DiffLineKind.Removed) { i++; continue; }
            int start = i;
            while (i < Lines.Count && Lines[i].Kind == DiffLineKind.Removed) i++;
            int pairs = Math.Min(i - start, Lines.Count - i);
            for (int offset = 0; offset < pairs; offset++)
            {
                DiffLine oldLine = Lines[start + offset], newLine = Lines[i + offset];
                if (newLine.Kind != DiffLineKind.Added) break;
                var (oldSpan, newSpan) = LineDiff.InlineSpans(oldLine.Text, newLine.Text);
                if (oldSpan.Length > 0) oldSpans.Add(oldLine.OldNumber, oldSpan);
                if (newSpan.Length > 0) newSpans.Add(newLine.NewNumber, newSpan);
            }
        }
        return (oldSpans, newSpans);
    }

    /// <summary>全量对齐结果(只有 Context / Added / Removed,不含折叠行)。</summary>
    public IReadOnlyList<DiffLine> Lines { get; }

    /// <summary>新增行数。</summary>
    public int Added { get; }

    /// <summary>删除行数。</summary>
    public int Removed { get; }

    /// <summary>两边是否有任何不同。</summary>
    public bool HasChanges => Added + Removed > 0;

    /// <summary>
    /// 折叠视图:每处改动前后各留 <paramref name="context" /> 行上下文,其余连续的未改动行压成一行
    /// <see cref="DiffLineKind.Gap" />。没有改动时返回空表。
    /// </summary>
    /// <param name="context">改动附近保留的上下文行数。</param>
    public IReadOnlyList<DiffLine> Collapse(int context = 3)
    {
        if (!HasChanges)
        {
            return [];
        }
        int count = Lines.Count;
        bool[] keep = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (Lines[i].Kind == DiffLineKind.Context) continue;
            int from = Math.Max(0, i - context), to = Math.Min(count - 1, i + context);
            for (int j = from; j <= to; j++) keep[j] = true;
        }
        var result = new List<DiffLine>();
        int hidden = 0;
        for (int i = 0; i < count; i++)
        {
            if (keep[i])
            {
                if (hidden > 0)
                {
                    result.Add(new DiffLine(DiffLineKind.Gap, 0, 0, "", hidden) { HiddenStart = i - hidden });
                    hidden = 0;
                }
                result.Add(Lines[i]);
            }
            else
            {
                hidden++;
            }
        }
        if (hidden > 0)
        {
            result.Add(new DiffLine(DiffLineKind.Gap, 0, 0, "", hidden) { HiddenStart = count - hidden });
        }
        return result;
    }

    /// <summary>
    /// git 风格的统一差异文本(<c>@@ -a,b +c,d @@</c> 分段,行首 <c>' ' / - / +</c>),不含文件头。
    /// 供复制与测试比对。
    /// </summary>
    /// <param name="context">改动附近保留的上下文行数。</param>
    public string ToUnified(int context = 3)
    {
        IReadOnlyList<DiffLine> collapsed = Collapse(context);
        var sb = new StringBuilder();
        int index = 0;
        while (index < collapsed.Count)
        {
            if (collapsed[index].Kind == DiffLineKind.Gap)
            {
                index++;
                continue;
            }
            int end = index;
            while (end < collapsed.Count && collapsed[end].Kind != DiffLineKind.Gap) end++;
            int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
            for (int i = index; i < end; i++)
            {
                DiffLine line = collapsed[i];
                if (line.Kind != DiffLineKind.Added)
                {
                    if (oldStart == 0) oldStart = line.OldNumber;
                    oldCount++;
                }
                if (line.Kind != DiffLineKind.Removed)
                {
                    if (newStart == 0) newStart = line.NewNumber;
                    newCount++;
                }
            }
            // 一侧为空时 git 写「上一行的行号,0」
            if (oldCount == 0) oldStart = PrecedingNumber(collapsed, index, old: true);
            if (newCount == 0) newStart = PrecedingNumber(collapsed, index, old: false);
            sb.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
              .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");
            for (int i = index; i < end; i++)
            {
                DiffLine line = collapsed[i];
                sb.Append(line.Kind switch
                {
                    DiffLineKind.Added => '+',
                    DiffLineKind.Removed => '-',
                    _ => ' '
                }).Append(line.Text).Append('\n');
            }
            index = end;
        }
        return sb.ToString();
    }

    /// <summary>某段开头之前、在这一侧最后出现的行号(找不到为 0)。</summary>
    private int PrecedingNumber(IReadOnlyList<DiffLine> collapsed, int index, bool old)
    {
        DiffLine first = collapsed[index];
        int at = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (ReferenceEquals(Lines[i], first))
            {
                at = i;
                break;
            }
        }
        for (int i = at - 1; i >= 0; i--)
        {
            int number = old ? Lines[i].OldNumber : Lines[i].NewNumber;
            if (number > 0) return number;
        }
        return 0;
    }
}

/// <summary>
/// 行级文本比对(Myers O(ND) 差分),供工具卡片与「已修改文件」列表画 git 风格的变更对比。
/// </summary>
/// <remarks>
/// <para>
/// 先剥掉两边相同的头尾,只对中间那段跑 Myers —— 改一处配置时中间往往只剩几行,比对几乎不花时间。
/// 编辑距离超过 <see cref="MaxEditDistance" /> 时不再找最短编辑脚本,中间那段直接按「整段删除 + 整段新增」给出:
/// 两份差异那么大的文本,逐行对齐本来也没有可读性,犯不着在界面线程上耗着。
/// </para>
/// <para>
/// 换行统一按 <c>\n</c> 切(<c>\r\n</c> 先归一),末尾的换行不单独算一行。同一处改动里删除行排在新增行之前,与 git 一致。
/// </para>
/// </remarks>
public static class LineDiff
{
    /// <summary>超过这个编辑距离就退回「整段删除 + 整段新增」。</summary>
    public const int MaxEditDistance = 2000;

    /// <summary>把文本切成行:<c>\r\n</c> / <c>\r</c> 归一成 <c>\n</c>,末尾换行不产生空行;null 与空串为零行。</summary>
    /// <param name="text">文本。</param>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }
        return normalized.Split('\n');
    }

    /// <summary>比对两段文本(null 视为空)。</summary>
    /// <param name="before">旧文本。</param>
    /// <param name="after">新文本。</param>
    public static LineDiffResult Compute(string? before, string? after) => Compute(before, after, false);

    internal static LineDiffResult Compute(string? before, string? after, bool ignoreWhitespace)
        => Compute(SplitLines(before), SplitLines(after), ignoreWhitespace);

    /// <summary>比对两组行。</summary>
    /// <param name="a">旧行。</param>
    /// <param name="b">新行。</param>
    public static LineDiffResult Compute(IReadOnlyList<string> a, IReadOnlyList<string> b) => Compute(a, b, false);

    private static LineDiffResult Compute(IReadOnlyList<string> a, IReadOnlyList<string> b, bool ignoreWhitespace)
    {
        int n = a.Count, m = b.Count;
        IReadOnlyList<string> oldKeys = ignoreWhitespace ? a.Select(line => line.Trim()).ToArray() : a;
        IReadOnlyList<string> newKeys = ignoreWhitespace ? b.Select(line => line.Trim()).ToArray() : b;
        int prefix = 0;
        while (prefix < n && prefix < m && string.Equals(oldKeys[prefix], newKeys[prefix], StringComparison.Ordinal)) prefix++;
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && string.Equals(oldKeys[n - 1 - suffix], newKeys[m - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var ops = new List<Op>(n + m);
        for (int i = 0; i < prefix; i++) ops.Add(Op.Equal);
        ops.AddRange(Middle(oldKeys, prefix, n - suffix, newKeys, prefix, m - suffix));
        for (int i = 0; i < suffix; i++) ops.Add(Op.Equal);
        Reorder(ops);

        var lines = new List<DiffLine>(ops.Count);
        int x = 0, y = 0;
        foreach (Op op in ops)
        {
            switch (op)
            {
                case Op.Equal:
                    lines.Add(new DiffLine(DiffLineKind.Context, x + 1, y + 1, b[y]));
                    x++;
                    y++;
                    break;
                case Op.Delete:
                    lines.Add(new DiffLine(DiffLineKind.Removed, x + 1, 0, a[x]));
                    x++;
                    break;
                default:
                    lines.Add(new DiffLine(DiffLineKind.Added, 0, y + 1, b[y]));
                    y++;
                    break;
            }
        }
        return new LineDiffResult(lines);
    }

    private enum Op : byte
    {
        Equal,
        Delete,
        Insert
    }

    /// <summary>
    /// 对 <c>a[aStart..aEnd)</c> 与 <c>b[bStart..bEnd)</c> 跑 Myers,给出最短编辑脚本。
    /// 每一步只存 k ∈ [-d, d] 那一截 V,回溯时按步取回。
    /// </summary>
    private static List<Op> Middle(IReadOnlyList<string> a, int aStart, int aEnd, IReadOnlyList<string> b, int bStart, int bEnd)
    {
        int n = aEnd - aStart, m = bEnd - bStart;
        var ops = new List<Op>(n + m);
        if (n == 0 || m == 0)
        {
            for (int i = 0; i < n; i++) ops.Add(Op.Delete);
            for (int i = 0; i < m; i++) ops.Add(Op.Insert);
            return ops;
        }

        int max = Math.Min(n + m, MaxEditDistance);
        int offset = max + 1;
        int[] v = new int[2 * max + 3];
        var snapshots = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && string.Equals(a[aStart + x], b[bStart + y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                }
            }
            int[] snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, 2 * d + 1);
            snapshots.Add(snapshot);
        }

        if (found < 0)
        {
            // 差得太多:不找最短脚本了,整段删、整段加
            for (int i = 0; i < n; i++) ops.Add(Op.Delete);
            for (int i = 0; i < m; i++) ops.Add(Op.Insert);
            return ops;
        }

        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            int[] prev = snapshots[d - 1];
            int k = cx - cy;
            int prevK = k == -d || (k != d && At(prev, d - 1, k - 1) < At(prev, d - 1, k + 1)) ? k + 1 : k - 1;
            int prevX = At(prev, d - 1, prevK);
            int prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                ops.Add(Op.Equal);
                cx--;
                cy--;
            }
            ops.Add(cx == prevX ? Op.Insert : Op.Delete);
            cx = prevX;
            cy = prevY;
        }
        while (cx > 0 && cy > 0)
        {
            ops.Add(Op.Equal);
            cx--;
            cy--;
        }
        ops.Reverse();
        return ops;

        static int At(int[] snapshot, int d, int k) => snapshot[k + d];
    }

    /// <summary>
    /// 共同前后缀之间的片段用于行内强调。极长行仅保留整行底色。
    /// </summary>
    internal static (DiffTextSpan Old, DiffTextSpan New) InlineSpans(string oldText, string newText)
    {
        const int maxLength = 16_000;
        if (oldText.Length > maxLength || newText.Length > maxLength || string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return default;
        }
        int prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
        int oldEnd = oldText.Length;
        int newEnd = newText.Length;
        while (oldEnd > prefix && newEnd > prefix && oldText[oldEnd - 1] == newText[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }
        if (prefix > 0 && prefix < oldText.Length && char.IsLowSurrogate(oldText[prefix])) prefix--;
        if (oldEnd < oldText.Length && char.IsLowSurrogate(oldText[oldEnd]))
        {
            oldEnd++;
            newEnd++;
        }
        return (new(prefix, oldEnd - prefix), new(prefix, newEnd - prefix));
    }

    private static void Reorder(List<Op> ops)
    {
        int i = 0;
        while (i < ops.Count)
        {
            if (ops[i] == Op.Equal)
            {
                i++;
                continue;
            }
            int end = i;
            int deletes = 0;
            while (end < ops.Count && ops[end] != Op.Equal)
            {
                if (ops[end] == Op.Delete) deletes++;
                end++;
            }
            for (int j = i; j < end; j++)
            {
                ops[j] = j - i < deletes ? Op.Delete : Op.Insert;
            }
            i = end;
        }
    }
}
