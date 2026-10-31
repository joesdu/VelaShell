// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST 的语义
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §3

namespace VelaShell.Ssh.Channels;

/// <summary>一个方向上的窗口账。</summary>
/// <remarks>
/// <para>
/// 发送侧与接收侧共用这一个结构，但用法相反：
/// </para>
/// <list type="bullet">
///   <item><b>发送侧</b>：<see cref="TryConsume"/> 扣减，窗口为 0 时必须停下等回补；
///   <see cref="Add"/> 在收到 <c>WINDOW_ADJUST</c> 时补回。</item>
///   <item><b>接收侧</b>：<see cref="TryConsume"/> 在数据到达时扣减（超了就是对端违规），
///   <see cref="Add"/> 在数据被**消费**后补回。</item>
/// </list>
/// </remarks>
internal sealed class SshWindow(int initialSize)
{
    private readonly Lock _lock = new();
    private uint _remaining = (uint)initialSize;

    /// <summary>当前剩余字节数。</summary>
    public uint Remaining
    {
        get
        {
            lock (_lock)
            {
                return _remaining;
            }
        }
    }

    /// <summary>窗口当前的额定大小（自适应时会变）。</summary>
    public int Size { get; private set; } = initialSize;

    /// <summary>扣减。</summary>
    /// <returns>窗口够不够。<see langword="false"/> 时**一个字节都没扣**。</returns>
    public bool TryConsume(int bytes)
    {
        lock (_lock)
        {
            if (_remaining < (uint)bytes)
            {
                return false;
            }
            _remaining -= (uint)bytes;
            return true;
        }
    }

    /// <summary>回补。</summary>
    /// <returns>
    /// 补完是否溢出了 <c>uint32</c>。<b>溢出是明确的协议违规</b>（velashell-docs/zh/ssh/spec/05 §8），
    /// 对端在用它来撑爆我们的计数。
    /// </returns>
    public bool Add(uint bytes)
    {
        lock (_lock)
        {
            if (_remaining > uint.MaxValue - bytes)
            {
                return false;
            }
            _remaining += bytes;
            return true;
        }
    }

    /// <summary>把窗口的额定大小改掉（自适应用）。</summary>
    public void Resize(int newSize) => Size = newSize;
}
