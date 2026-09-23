// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §6.2

namespace VelaShell.Ssh.Sftp;

/// <summary>已确认写入的区间集合，用来算出**精确**的连续落盘长度。</summary>
/// <remarks>
/// <para>
/// 流水线满载时有 N 个在途 <c>WRITE</c>，应答顺序不保证。所以「服务端报告的文件长度」
/// 只是<b>已确认的最高偏移</b>，它前面可能留着空洞：
/// </para>
/// <code>
/// 偏移 0 ....... 1 MiB ....... 2 MiB ....... 3 MiB
/// 已确认：  [==========]        [====]        [====]
///                       ↑ 空洞          ↑ 空洞
/// 文件长度 = 3 MiB  ← 但 1–2 MiB 那段其实没写进去
/// </code>
/// <para>
/// <see cref="DurableLength"/> 给出的是「从 0 开始<b>连续</b>已确认」的长度 ——
/// 断点续传从这里续，精确，不用像盲退一个在途窗口那样去猜。
/// </para>
/// <para>
/// <b>为什么是有序数组而不是字典</b>：顺序写入时新区间几乎总是接在最后一个的尾部，
/// 合并之后集合大小恒为 1，代价 O(1)。只有乱序写入时集合才会增长，
/// 而上限就是在途请求数（≤ 256），完全可控。
/// </para>
/// </remarks>
internal sealed class AckedRangeSet
{
    private readonly List<(long Start, long End)> _ranges = [];
    private readonly Lock _lock = new();

    /// <summary>从 0 开始连续已确认的字节数。</summary>
    public long DurableLength
    {
        get
        {
            lock (_lock)
            {
                // 第一个区间不是从 0 开始 → 开头就有空洞 → 一个字节都不可信。
                return _ranges.Count > 0 && _ranges[0].Start == 0 ? _ranges[0].End : 0;
            }
        }
    }

    /// <summary>当前有几个不连续的区间（诊断用；顺序写时应当恒为 1）。</summary>
    public int RangeCount
    {
        get
        {
            lock (_lock)
            {
                return _ranges.Count;
            }
        }
    }

    /// <summary>已确认的最高偏移（对应「服务端报告的文件长度」）。</summary>
    public long HighestAckedOffset
    {
        get
        {
            lock (_lock)
            {
                return _ranges.Count > 0 ? _ranges[^1].End : 0;
            }
        }
    }

    /// <summary>并入一个已确认的区间 <c>[offset, offset + length)</c>。</summary>
    public void Add(long offset, long length)
    {
        if (length <= 0)
        {
            return;
        }

        long start = offset;
        long end = offset + length;

        lock (_lock)
        {
            // 顺序写的快路径：直接接在最后一个区间的尾部。
            if (_ranges.Count > 0)
            {
                (long lastStart, long lastEnd) = _ranges[^1];
                if (lastEnd == start)
                {
                    _ranges[^1] = (lastStart, end);
                    return;
                }
                if (lastEnd < start)
                {
                    _ranges.Add((start, end));
                    return;
                }
            }
            else
            {
                _ranges.Add((start, end));
                return;
            }

            // 乱序：二分找插入点，然后把与新区间相接或相交的都吞掉。
            int index = LowerBound(start);

            // 往前看一个：它可能正好接上或盖住我们。
            if (index > 0 && _ranges[index - 1].End >= start)
            {
                index--;
                start = Math.Min(start, _ranges[index].Start);
                end = Math.Max(end, _ranges[index].End);
            }

            int removeFrom = index;
            int removeCount = 0;
            while (index < _ranges.Count && _ranges[index].Start <= end)
            {
                end = Math.Max(end, _ranges[index].End);
                start = Math.Min(start, _ranges[index].Start);
                index++;
                removeCount++;
            }

            if (removeCount > 0)
            {
                _ranges.RemoveRange(removeFrom, removeCount);
            }

            _ranges.Insert(removeFrom, (start, end));
        }
    }

    /// <summary>第一个 <c>Start &gt;= value</c> 的下标。调用时必须持有锁。</summary>
    private int LowerBound(long value)
    {
        int low = 0;
        int high = _ranges.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_ranges[mid].Start < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }
}
