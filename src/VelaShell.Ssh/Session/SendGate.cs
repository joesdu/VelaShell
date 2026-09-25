// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  "...MUST NOT send any messages other than [transport layer messages]
//                   until the key exchange is complete."
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §8.2

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

/// <summary>闸门对一帧的裁决。</summary>
internal enum SendGateAdmission
{
    /// <summary>可以立即发送。</summary>
    Send,

    /// <summary>已暂存，开闸后按原顺序流出。</summary>
    Stashed,

    /// <summary>暂存区已满，调用方必须等待（背压）。这一帧**没有**被收下。</summary>
    StashFull,
}

/// <summary>
/// 重协商期间只放行传输层消息的那道闸。
/// </summary>
/// <typeparam name="TFrame">待发帧的载体类型。闸门只负责排序与放行，不关心它是什么。</typeparam>
/// <remarks>
/// <para>
/// RFC 4253 §7.1 的原话是：一旦发出 <c>KEXINIT</c>，在 <c>NEWKEYS</c> 之前
/// 禁止发送除传输层消息（编号 1–49）之外的任何报文。
/// <b>这个类就是那句话的直接实现</b> —— 规范怎么说，代码就怎么写。
/// </para>
/// <para>
/// <b>为什么不是「两个循环互相递信号量」</b>：常见的做法是让收包循环与发包循环
/// 通过一个共享的信号量互相等待（收包循环造一个信号量、发包循环把它替换成另一个、
/// 两边交叉持有）。那样能跑，但没有人敢动它，而且两条路径互相持有对方的同步原语，
/// 死锁的可能性只能靠推理排除，不能靠结构排除。
/// </para>
/// <para>
/// 闸门这条路没有这个问题：<b>只有发包泵会碰它</b>。收包循环通过
/// <see cref="Close"/> / <see cref="Open"/> 改一个布尔，不等待任何东西。
/// 因此**不存在两条路径互相等待的结构**，死锁在结构上就不可能。
/// </para>
/// <para>
/// 本类**不是线程安全的**，由单写者的发包泵独占；
/// <see cref="Close"/> / <see cref="Open"/> 也必须投递到发包泵上执行，
/// 而不是从收包循环直接调用。
/// </para>
/// </remarks>
internal sealed class SendGate<TFrame>(long maxStashBytes = SendGate<TFrame>.DefaultMaxStashBytes)
{
    /// <summary>
    /// 暂存区默认上限：16 MiB。
    /// </summary>
    /// <remarks>
    /// 有上限是必须的 —— 重协商期间对端若迟迟不完成，无界暂存就是一个内存耗尽面。
    /// 但**超限时是阻塞入队方，不是丢弃**：通道数据的顺序与完整性是语义的一部分，
    /// 在这里丢一帧，对端看到的是一个被截断的流，而且无从察觉。
    /// </remarks>
    public const long DefaultMaxStashBytes = 16L * 1024 * 1024;

    private readonly Queue<(TFrame Frame, int ByteLength)> _stash = new();
    private readonly long _maxStashBytes = maxStashBytes > 0
        ? maxStashBytes
        : throw new ArgumentOutOfRangeException(nameof(maxStashBytes));

    /// <summary>闸门是否开着（非重协商期间恒为开）。</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>暂存的帧数。</summary>
    public int StashedCount => _stash.Count;

    /// <summary>暂存的字节数。</summary>
    public long StashedBytes { get; private set; }

    /// <summary>暂存区是否已满。</summary>
    public bool IsStashFull => StashedBytes >= _maxStashBytes;

    /// <summary>
    /// 该消息编号是不是传输层消息（1–49），也就是重协商期间唯一允许发送的那一类。
    /// </summary>
    /// <remarks>
    /// 分区依据 RFC 4250 §4.1.2：1–19 传输层通用、20–29 算法协商、30–49 KEX 方法专用。
    /// 认证层从 50 起，连接层从 80 起 —— 那些都是闸门要挡下的。
    /// </remarks>
    public static bool IsTransportMessage(SshMessageNumber number) => (byte)number is >= 1 and <= 49;

    /// <summary>
    /// 关闸：进入重协商。
    /// </summary>
    /// <remarks>已经关着时再调用是空操作 —— 双方同时发起重协商是合法的。</remarks>
    public void Close() => IsOpen = false;

    /// <summary>
    /// 开闸：重协商完成。暂存的帧随后可由 <see cref="TryTakeStashed"/> 按原顺序取出。
    /// </summary>
    public void Open() => IsOpen = true;

    /// <summary>这个编号的帧现在交给 <see cref="Admit"/> 的话，会不会当场放行（而不是被暂存）。</summary>
    /// <remarks>与 <see cref="Admit"/> 的判断一致；只有唯一的写者（发送泵）调用，所以查完再放不会变。</remarks>
    public bool WouldSendNow(SshMessageNumber number) =>
        (IsOpen && _stash.Count == 0) || (!IsOpen && IsTransportMessage(number));

    /// <summary>
    /// 请求放行一帧。
    /// </summary>
    /// <param name="frame">待发的帧。</param>
    /// <param name="number">该帧的消息编号。</param>
    /// <param name="byteLength">该帧的载荷字节数，用于暂存区计量。</param>
    /// <returns>
    /// <see cref="SendGateAdmission.Send"/> 表示可以立即发送；
    /// <see cref="SendGateAdmission.Stashed"/> 表示已收下并暂存；
    /// <see cref="SendGateAdmission.StashFull"/> 表示**没有收下**，调用方必须等暂存区腾出空间。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 闸门开着时也可能返回 <see cref="SendGateAdmission.Stashed"/>：
    /// 只要暂存区还有东西没排空，新帧就必须排在它们后面。
    /// <b>保序是硬要求</b> —— 通道数据的顺序是语义的一部分。
    /// </para>
    /// </remarks>
    public SendGateAdmission Admit(TFrame frame, SshMessageNumber number, int byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);

        // 闸门开着且暂存区已排空 —— 直接走。这是绝大多数时候的路径。
        if (IsOpen && _stash.Count == 0)
        {
            return SendGateAdmission.Send;
        }

        // 重协商期间，传输层消息照常放行 —— 密钥交换本身就是靠它们完成的。
        // 但只有暂存区为空时才能插队：否则会把一个 KEXINIT 排到它之前的
        // 暂存帧前面去，而那些帧是开闸后要按原顺序流出的。
        // 传输层消息与被暂存的非传输层消息之间没有顺序约束（前者在 RFC 眼里
        // 本就是「可以在 kex 期间发送」的那一类），所以这一步是安全的。
        if (!IsOpen && IsTransportMessage(number))
        {
            return SendGateAdmission.Send;
        }

        if (IsStashFull)
        {
            return SendGateAdmission.StashFull;
        }

        _stash.Enqueue((frame, byteLength));
        StashedBytes += byteLength;
        return SendGateAdmission.Stashed;
    }

    /// <summary>
    /// 取出一帧暂存的帧（闸门开着时才会给）。
    /// </summary>
    /// <param name="frame">取出的帧。</param>
    /// <param name="byteLength">该帧入栈时登记的字节数。</param>
    /// <returns>取到返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 闸门仍关着时恒返回 <see langword="false"/> —— 暂存的帧必须等到重协商完成。
    /// </remarks>
    public bool TryTakeStashed(out TFrame? frame, out int byteLength)
    {
        if (!IsOpen || !_stash.TryDequeue(out (TFrame Frame, int ByteLength) item))
        {
            frame = default;
            byteLength = 0;
            return false;
        }

        frame = item.Frame;
        byteLength = item.ByteLength;
        StashedBytes -= item.ByteLength;
        return true;
    }

    /// <summary>
    /// 连接中止：丢弃暂存的帧并交给调用方处置（通常是把等待它们的操作以异常收尾）。
    /// </summary>
    public IReadOnlyList<TFrame> DrainForAbort()
    {
        if (_stash.Count == 0)
        {
            return [];
        }

        TFrame[] all = [.. _stash.Select(static x => x.Frame)];
        _stash.Clear();
        StashedBytes = 0;
        return all;
    }
}
