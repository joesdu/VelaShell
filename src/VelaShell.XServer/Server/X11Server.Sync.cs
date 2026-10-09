// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Synchronization Extension Protocol, Version 3.1 —— §3「Types」(INT64、TRIGGER:counter / value-type
//   Absolute·Relative / wait-value / test-type PositiveTransition·NegativeTransition·PositiveComparison·
//   NegativeComparison;WAITCONDITION;ALARMSTATE)、§4「Errors」(Counter、Alarm、Fence)、
//   §5「Requests」(Initialize 0 … AwaitFence 19)、§6「Events」(CounterNotify、AlarmNotify)、
//   §7「System counters」(SERVERTIME、IDLETIME)
//
//   Await / AwaitFence 让发请求的客户端停下,直到某个条件成立:它之后的请求原样暂存(与 GrabServer 同一个手法),
//   条件成立时按原顺序放回。系统计数器随时间变化:有触发器挂在它们上面时,按「最早什么时候可能成立」定一个计时器,
//   到点再求一次值;用户一有输入,IDLETIME 归零也会重新求值(xss-lock、GNOME 的空闲检测靠这个)。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private const uint ServerTimeCounterId = 0x44, IdleTimeCounterId = 0x45;

    private XSyncCounter? _serverTimeCounter;
    private XSyncCounter? _idleTimeCounter;

    /// <summary>正在 Await 的客户端 → 它等的条件与暂存的请求。</summary>
    private readonly Dictionary<XClient, SyncWait> _syncWaits = [];

    private readonly List<XSyncAlarm> _alarms = [];

    private CancellationTokenSource? _syncTimer;

    /// <summary>当前计时器到点的时刻(<see cref="Now" /> 的刻度);没有计时器时为 long.MaxValue。</summary>
    private long _syncDeadline = long.MaxValue;

    /// <summary>系统计数器的计时器是否排着(测试用)。</summary>
    internal bool SyncTimerPending => _syncTimer is not null;

    /// <summary>求值进行中(EndWait 会就地执行暂存的请求,那些请求可能再次改计数器)。</summary>
    private bool _evaluatingSync;

    /// <summary>求值进行中又有东西变了:当前这一轮结束后再来一轮。</summary>
    private bool _syncDirty;

    private sealed class SyncWait
    {
        public List<(XSyncTrigger Trigger, long Threshold)> Conditions { get; } = [];

        public List<XSyncFence> Fences { get; } = [];

        public List<WorkItem> Deferred { get; } = [];
    }

    private void InitSyncCounters()
    {
        _serverTimeCounter = new XSyncCounter(ServerTimeCounterId, null, 0, "SERVERTIME");
        _idleTimeCounter = new XSyncCounter(IdleTimeCounterId, null, 0, "IDLETIME");
        _resources[ServerTimeCounterId] = _serverTimeCounter;
        _resources[IdleTimeCounterId] = _idleTimeCounter;
    }

    private long CounterValue(XSyncCounter counter) => counter.SystemName switch
    {
        "SERVERTIME" => _clock.ElapsedMilliseconds,
        "IDLETIME" => IdleMilliseconds,
        _ => counter.Value,
    };

    private XSyncCounter Counter(uint id) =>
        Use<XSyncCounter>(id) ?? throw new XProtocolError((XErrorCode)SyncErrorBase, id);

    private XSyncAlarm Alarm(uint id) =>
        Use<XSyncAlarm>(id) ?? throw new XProtocolError((XErrorCode)(SyncErrorBase + 1), id);

    private XSyncFence Fence(uint id) =>
        Use<XSyncFence>(id) ?? throw new XProtocolError((XErrorCode)(SyncErrorBase + 2), id);

    private static long ReadInt64(XRequestReader r)
    {
        int hi = r.I32();
        uint lo = r.U32();
        return ((long)hi << 32) | lo;
    }

    private static XWriter WriteInt64(XWriter w, long value) => w.I32((int)(value >> 32)).U32((uint)value);

    // ------------------------------------------------------------------ 请求

    private void Sync(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // Initialize
                c.Reply(0, w => w.U8(3).U8(1).Zero(22));
                break;
            case 1:   // ListSystemCounters
                {
                    XSyncCounter[] counters = [_serverTimeCounter!, _idleTimeCounter!];
                    c.Reply(0, w =>
                    {
                        w.U32((uint)counters.Length).Zero(20);
                        foreach (XSyncCounter counter in counters)
                        {
                            byte[] name = XWire.Latin1.GetBytes(counter.SystemName!);
                            w.U32(counter.Id);
                            WriteInt64(w, 1);   // 分辨率:1 毫秒
                            w.U16((ushort)name.Length).Bytes(name);
                            w.Zero(XWire.Pad(14 + name.Length) - (14 + name.Length));
                        }
                    });
                    break;
                }
            case 2:   // CreateCounter
                {
                    uint id = r.U32();
                    AddResource(c, new XSyncCounter(id, c, ReadInt64(r)));
                    break;
                }
            case 3:   // SetCounter
            case 4:   // ChangeCounter
                {
                    XSyncCounter counter = Counter(r.U32());
                    long value = ReadInt64(r);
                    if (counter.SystemName is not null)
                    {
                        throw new XProtocolError(XErrorCode.Access, counter.Id);
                    }
                    if (r.Data == 4)
                    {
                        long sum = unchecked(counter.Value + value);
                        if ((value > 0 && sum < counter.Value) || (value < 0 && sum > counter.Value))
                        {
                            throw new XProtocolError(XErrorCode.Value);   // 溢出
                        }
                        value = sum;
                    }
                    counter.Value = value;
                    EvaluateSync();
                    CheckRedrawSync(counter);   // _NET_WM_SYNC_REQUEST:客户端重画完了
                    break;
                }
            case 5:   // QueryCounter
                {
                    long value = CounterValue(Counter(r.U32()));
                    c.Reply(0, w => WriteInt64(w, value).Zero(16));
                    break;
                }
            case 6:   // DestroyCounter
                {
                    XSyncCounter counter = Counter(r.U32());
                    if (counter.SystemName is not null)
                    {
                        throw new XProtocolError(XErrorCode.Access, counter.Id);
                    }
                    DestroyCounter(counter);
                    break;
                }
            case 7:   // Await
                {
                    SyncWait wait = new();
                    while (r.Remaining >= 28)
                    {
                        XSyncTrigger trigger = ReadTrigger(r, 0xF);
                        long threshold = ReadInt64(r);
                        wait.Conditions.Add((trigger, threshold));
                    }
                    if (wait.Conditions.Count == 0)
                    {
                        throw new XProtocolError(XErrorCode.Value);   // 规范 Await:wait-list 为空回 Value(原先客户端从此挂住)
                    }
                    BeginWait(c, wait);
                    break;
                }
            case 8:   // ChangeAlarm
            case 9:   // CreateAlarm
                {
                    bool create = r.Data == 9;
                    uint id = r.U32();
                    uint mask = r.U32();
                    XSyncAlarm alarm = create ? new XSyncAlarm(id, c) : Alarm(id);
                    ApplyAlarmValues(c, alarm, mask, r);
                    if (create)
                    {
                        AddResource(c, alarm);
                        alarm.Listeners.Add(c);
                        _alarms.Add(alarm);
                    }
                    alarm.State = alarm.Trigger.Counter is null ? XSyncAlarm.Inactive : XSyncAlarm.Active;
                    EvaluateSync();
                    break;
                }
            case 10:  // QueryAlarm
                {
                    XSyncAlarm alarm = Alarm(r.U32());
                    XSyncTrigger t = alarm.Trigger;
                    bool events = alarm.Listeners.Contains(c);
                    c.Reply(0, w =>
                    {
                        w.U32(t.Counter?.Id ?? 0).U32(t.ValueType);
                        WriteInt64(w, t.WaitValue).U32(t.TestType);
                        WriteInt64(w, alarm.Delta).Bool(events).U8(alarm.State).Zero(2);
                    });
                    break;
                }
            case 11:  // DestroyAlarm
                DestroyAlarm(Alarm(r.U32()));
                break;
            case 12:  // SetPriority:我们只有一个执行线程,优先级无从谈起;校验参数后接受
            case 13:  // GetPriority
                {
                    uint id = r.U32();
                    if (id != 0 && Lookup<XResource>(id) is null && !_clients.ContainsKey((int)(id >> 21)))
                    {
                        throw new XProtocolError(XErrorCode.Match, id);
                    }
                    if (r.Data == 13)
                    {
                        c.Reply(0, w => w.I32(0).Zero(20));
                    }
                    break;
                }
            case 14:  // CreateFence
                {
                    CheckDrawable(r.U32());
                    uint id = r.U32();
                    AddResource(c, new XSyncFence(id, c, r.U8() != 0));
                    break;
                }
            case 15:  // TriggerFence
                TriggerFence(Fence(r.U32()));
                break;
            case 16:  // ResetFence:只能重置已触发的栅栏
                {
                    XSyncFence fence = Fence(r.U32());
                    if (!fence.Triggered)
                    {
                        throw new XProtocolError(XErrorCode.Match, fence.Id);
                    }
                    fence.Triggered = false;
                    break;
                }
            case 17:  // DestroyFence
                {
                    XSyncFence fence = Fence(r.U32());
                    RemoveResource(fence.Id);
                    FenceGone(fence);
                    RunReadyPresents();   // 等它的 PresentPixmap 不再等(Present 规范)
                    break;
                }
            case 18:  // QueryFence
                {
                    XSyncFence fence = Fence(r.U32());
                    c.Reply(0, w => w.Bool(fence.Triggered).Zero(23));
                    break;
                }
            case 19:  // AwaitFence
                {
                    SyncWait wait = new();
                    while (r.Remaining >= 4)
                    {
                        wait.Fences.Add(Fence(r.U32()));
                    }
                    if (wait.Fences.Count == 0)
                    {
                        break;   // 没有栅栏可等:规范没给这种情形的错误,按「没什么要等的」立即放行,而不是永远挂住
                    }
                    BeginWait(c, wait);
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>读一个 TRIGGER(按 ChangeAlarm 的掩码位:1 counter、2 value-type、4 value、8 test-type)。</summary>
    private XSyncTrigger ReadTrigger(XRequestReader r, uint mask, XSyncTrigger? into = null)
    {
        XSyncTrigger t = into ?? new XSyncTrigger();
        if ((mask & 1) != 0)
        {
            uint counterId = r.U32();
            t.Counter = counterId == 0 ? null : Counter(counterId);
        }
        if ((mask & 2) != 0)
        {
            t.ValueType = r.U32();
            if (t.ValueType > 1)
            {
                throw new XProtocolError(XErrorCode.Value, t.ValueType);
            }
        }
        if ((mask & 4) != 0)
        {
            t.WaitValue = ReadInt64(r);
        }
        if ((mask & 8) != 0)
        {
            t.TestType = r.U32();
            if (t.TestType > 3)
            {
                throw new XProtocolError(XErrorCode.Value, t.TestType);
            }
        }
        // 初始化:按 value-type 与 wait-value 算出测试值(规范 TRIGGER)。value-type 与 wait-value 本身照原样留着 ——
        // QueryAlarm 报的是它们,只改 value 的 ChangeAlarm 重新初始化时也要按原来的 value-type 解释(原先换算之后改回了 Absolute)。
        if (t.Counter is not { } counter)
        {
            if (t.ValueType == XSyncTrigger.Relative)
            {
                throw new XProtocolError(XErrorCode.Match);   // counter 为 None 时没有「相对于谁」
            }
            t.TestValue = t.WaitValue;
            return t;
        }
        long now = CounterValue(counter);
        Int128 test = t.ValueType == XSyncTrigger.Relative ? (Int128)now + t.WaitValue : t.WaitValue;
        if (test > long.MaxValue || test < long.MinValue)
        {
            throw new XProtocolError(XErrorCode.Value);   // 测试值超出 INT64
        }
        t.TestValue = (long)test;
        t.LastValue = now;
        return t;
    }

    private void ApplyAlarmValues(XClient c, XSyncAlarm alarm, uint mask, XRequestReader r)
    {
        ReadTrigger(r, mask & 0xF, alarm.Trigger);
        if ((mask & 16) != 0)
        {
            alarm.Delta = ReadInt64(r);
        }
        if ((mask & 32) != 0)
        {
            if (r.U32() != 0)
            {
                alarm.Listeners.Add(c);
            }
            else
            {
                alarm.Listeners.Remove(c);
            }
        }
        // 比较型测试的 delta 符号必须把等待值推离「已满足」的一侧,否则会无限触发(规范 CreateAlarm)。
        XSyncTrigger t = alarm.Trigger;
        if ((t.TestType == XSyncTrigger.PositiveComparison && alarm.Delta < 0)
            || (t.TestType == XSyncTrigger.NegativeComparison && alarm.Delta > 0))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
    }

    private void TriggerFence(XSyncFence fence)
    {
        fence.Triggered = true;
        EvaluateSync();
        RunReadyPresents();   // 以它为 wait-fence 的 PresentPixmap
    }

    private void DestroyCounter(XSyncCounter counter)
    {
        RemoveResource(counter.Id);
        CounterGone(c => ReferenceEquals(c, counter));
    }

    /// <summary>
    /// 计数器没了(DestroyCounter,或者创建它的客户端断开):挂着它的报警器的 counter 置为 None、进入 Inactive,并发一条
    /// state = Inactive 的 AlarmNotify;等它的 Await 结束,destroyed = True 的 CounterNotify 不论阈值一定发(规范 DestroyCounter)。
    /// 报警器先改,再结束等待(暂存的请求放回执行循环,之后才执行)。
    /// </summary>
    private void CounterGone(Func<XSyncCounter, bool> gone)
    {
        foreach (XSyncAlarm alarm in _alarms)
        {
            if (alarm.State != XSyncAlarm.Destroyed && alarm.Trigger.Counter is { } counter && gone(counter))
            {
                alarm.Trigger.Counter = null;
                alarm.State = XSyncAlarm.Inactive;
                SendAlarmNotify(alarm, counter.Value, alarm.Trigger.TestValue);
            }
        }
        foreach ((XClient client, SyncWait wait) in _syncWaits.ToArray())
        {
            if (_syncWaits.ContainsKey(client) && wait.Conditions.Any(cond => cond.Trigger.Counter is { } k && gone(k)))
            {
                SendCounterNotifies(client, wait, gone);
                foreach ((XSyncTrigger trigger, _) in wait.Conditions)
                {
                    if (trigger.Counter is { } k && gone(k))
                    {
                        trigger.Counter = null;
                    }
                }
                EndWait(client);
            }
        }
    }

    /// <summary>
    /// 报警器不再参与求值:标成 Destroyed,列表里攒够一半死项才压缩一次 —— 原先每次 List.Remove(O(n)),
    /// 求值时每个报警器又 List.Contains 一遍(O(n)),几千个报警器一轮求值就是几千万次比较。
    /// </summary>
    private void ForgetAlarm(XSyncAlarm alarm)
    {
        if (alarm.State == XSyncAlarm.Destroyed)
        {
            return;
        }
        alarm.State = XSyncAlarm.Destroyed;
        if (++_destroyedAlarms > _alarms.Count / 2)
        {
            _alarms.RemoveAll(a => a.State == XSyncAlarm.Destroyed);
            _destroyedAlarms = 0;
        }
    }

    /// <summary>列表里还留着的已销毁报警器个数(见 <see cref="ForgetAlarm" />)。</summary>
    private int _destroyedAlarms;

    private void DestroyAlarm(XSyncAlarm alarm)
    {
        RemoveResource(alarm.Id);
        ForgetAlarm(alarm);
        SendAlarmNotify(alarm, CounterValue(alarm.Trigger.Counter ?? _serverTimeCounter!), alarm.Trigger.TestValue);
    }

    // ------------------------------------------------------------------ Await

    private void BeginWait(XClient c, SyncWait wait)
    {
        if (WaitSatisfied(wait))
        {
            // 条件已经成立:不用停。CounterNotify 照样按阈值检查(规范 Await:「即使请求执行时就有触发器成立」)。
            SendCounterNotifies(c, wait, null);
            return;
        }
        _syncWaits[c] = wait;
        ScheduleSyncTimer();
    }

    private bool WaitSatisfied(SyncWait wait)
    {
        if (wait.Fences.Any(f => f.Triggered))
        {
            return true;
        }
        foreach ((XSyncTrigger trigger, _) in wait.Conditions)
        {
            // 规范 TRIGGER:「A trigger with a counter value of None and a valid test-type is always TRUE」(原先永远不成立,客户端挂住)。
            if (trigger.Counter is not { } counter || trigger.Satisfied(CounterValue(counter)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>栅栏被销毁(DestroyFence,或创建它的客户端断开):等它的 AwaitFence 一律放行(规范 DestroyFence)。原先它们永远挂着。</summary>
    private void FenceGone(XSyncFence fence)
    {
        foreach ((XClient client, SyncWait wait) in _syncWaits.ToArray())
        {
            if (_syncWaits.ContainsKey(client) && wait.Fences.Contains(fence))
            {
                EndWait(client);
            }
        }
    }

    private void EndWait(XClient client)
    {
        if (!_syncWaits.Remove(client, out SyncWait? wait))
        {
            return;
        }
        Requeue(wait.Deferred);
    }

    /// <summary>执行循环在跑一项工作之前问一句:这个客户端是不是在 Await 里?是就把请求暂存。</summary>
    private bool DeferIfWaiting(WorkItem item)
    {
        if (item.Client is { } client && _syncWaits.TryGetValue(client, out SyncWait? wait) && !client.Closed)
        {
            wait.Deferred.Add(item);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ 求值

    /// <summary>
    /// 用户有了输入、IDLETIME 即将归零:把挂在它上面的触发器的「上一次的值」记成归零前的空闲时长,
    /// 负向跨越才看得出「从上面掉下来」。返回是否有触发器挂在 IDLETIME 上。
    /// </summary>
    private bool NoteIdleReset(uint idleBefore)
    {
        if (_idleTimeCounter is not { } idle || (_syncWaits.Count == 0 && _alarms.Count == 0))
        {
            return false;
        }
        bool any = false;
        foreach (SyncWait wait in _syncWaits.Values)
        {
            foreach ((XSyncTrigger trigger, _) in wait.Conditions)
            {
                if (ReferenceEquals(trigger.Counter, idle))
                {
                    trigger.LastValue = idleBefore;
                    any = true;
                }
            }
        }
        foreach (XSyncAlarm alarm in _alarms)
        {
            if (alarm.State == XSyncAlarm.Active && ReferenceEquals(alarm.Trigger.Counter, idle))
            {
                alarm.Trigger.LastValue = idleBefore;
                any = true;
            }
        }
        return any;
    }

    /// <summary>计数器变了(或者时间到了、用户有了输入):检查所有 Await 与报警器。</summary>
    private void EvaluateSync()
    {
        if (_syncWaits.Count == 0 && _alarms.Count == 0)
        {
            return;
        }
        if (_evaluatingSync)
        {
            _syncDirty = true;   // 暂存的请求在求值途中又改了计数器:外层这一轮结束后再来一轮
            return;
        }
        _evaluatingSync = true;
        try
        {
            int rounds = 0;
            do
            {
                _syncDirty = false;
                EvaluateSyncOnce();
            }
            while (_syncDirty && ++rounds < 64);
        }
        finally
        {
            _evaluatingSync = false;
        }
        ScheduleSyncTimer();
    }

    private void EvaluateSyncOnce()
    {
        foreach ((XClient client, SyncWait wait) in _syncWaits.ToArray())
        {
            if (!_syncWaits.ContainsKey(client))
            {
                continue;   // 前面某个客户端的暂存请求把它的等待结束了(比如销毁了它等的计数器)
            }
            if (WaitSatisfied(wait))
            {
                SendCounterNotifies(client, wait, null);
                EndWait(client);
            }
            else
            {
                foreach ((XSyncTrigger trigger, _) in wait.Conditions)
                {
                    if (trigger.Counter is { } counter)
                    {
                        trigger.LastValue = CounterValue(counter);
                    }
                }
            }
        }
        foreach (XSyncAlarm alarm in _alarms.ToArray())
        {
            if (alarm.State != XSyncAlarm.Active || alarm.Trigger.Counter is not { } counter)
            {
                continue;
            }
            long value = CounterValue(counter);
            XSyncTrigger t = alarm.Trigger;
            if (!t.Satisfied(value))
            {
                t.LastValue = value;
                continue;
            }
            // 触发之后按 delta 推进等待值;比较型测试 delta 为 0、或推进会溢出时报警器停用 —— 规范:状态在发事件「之前」改,
            // 事件里的 state 是新状态(原先先发后改:delta = 0 的比较型报警器报 Active,随即却是 Inactive),alarm-value 是触发时的测试值。
            long alarmValue = t.TestValue;
            if (!AdvanceAlarm(alarm, value))
            {
                alarm.State = XSyncAlarm.Inactive;
            }
            t.LastValue = value;
            SendAlarmNotify(alarm, value, alarmValue);
        }
    }

    /// <summary>
    /// 报警器触发之后推进等待值(规范 CreateAlarm:反复加 delta 并重新初始化,直到触发器为假)。比较型直接算出要加几次:
    /// PositiveComparison 要 wait + k·delta &gt; 计数器,NegativeComparison 要 wait + k·delta &lt; 计数器 —— 原先一次一次地加,
    /// 超过一百万次就把报警器停用,而规范只在溢出时停用(delta = 1、计数器一下跳到几百万的报警器就此失效)。
    /// 跨越型重新初始化之后就是假的,只加一次。比较型 delta 为 0、或结果超出 INT64 时不改值,返回假。
    /// </summary>
    private static bool AdvanceAlarm(XSyncAlarm alarm, long value)
    {
        XSyncTrigger t = alarm.Trigger;
        long delta = alarm.Delta;
        bool comparison = t.TestType is XSyncTrigger.PositiveComparison or XSyncTrigger.NegativeComparison;
        if (comparison && delta == 0)
        {
            return false;
        }
        Int128 steps = 1;
        if (comparison)
        {
            Int128 gap = t.TestType == XSyncTrigger.PositiveComparison ? (Int128)value - t.TestValue : (Int128)t.TestValue - value;
            steps = gap < 0 ? 1 : (gap / Int128.Abs(delta)) + 1;
        }
        // 测试值与 wait-value 一同推进(Absolute 时两者相等;Relative 时 wait-value 是客户端给的偏移,同样加上 delta)。
        Int128 next = t.TestValue + (steps * delta), nextWait = t.WaitValue + (steps * delta);
        if (next > long.MaxValue || next < long.MinValue || nextWait > long.MaxValue || nextWait < long.MinValue)
        {
            return false;
        }
        t.TestValue = (long)next;
        t.WaitValue = (long)nextWait;
        return true;
    }

    /// <summary>
    /// 有触发器挂在系统计数器上时,算出最早可能成立的时刻并定一个计时器。
    /// SERVERTIME 一毫秒一毫秒地涨;IDLETIME 在没有输入时同样一毫秒一毫秒地涨(有输入时由 NoteInputActivity 触发求值)。
    /// </summary>
    private void ScheduleSyncTimer()
    {
        long soonest = long.MaxValue;
        void Consider(XSyncTrigger t)
        {
            if (t.Counter is not { SystemName: not null } counter
                || t.TestType is not (XSyncTrigger.PositiveComparison or XSyncTrigger.PositiveTransition))
            {
                return;
            }
            // 正向跨越已经越过了等待值:系统计数器只会往上涨,要先掉回等待值以下才可能再成立 —— 那只会是 IDLETIME 因用户输入归零,
            // 那时另有一次求值(NoteIdleReset)。原先照样按 Max(1, 等待值 − 当前值) 排计时器,等于每毫秒醒一次、持锁求值。
            if (t.TestType == XSyncTrigger.PositiveTransition && t.LastValue >= t.TestValue)
            {
                return;
            }
            soonest = Math.Min(soonest, Math.Max(1, t.TestValue - CounterValue(counter)));
        }
        foreach (SyncWait wait in _syncWaits.Values)
        {
            foreach ((XSyncTrigger trigger, _) in wait.Conditions)
            {
                Consider(trigger);
            }
        }
        foreach (XSyncAlarm alarm in _alarms)
        {
            if (alarm.State == XSyncAlarm.Active)
            {
                Consider(alarm.Trigger);
            }
        }

        if (soonest == long.MaxValue)
        {
            CancelSyncTimer();
            return;
        }
        long deadline = Now + soonest;
        if (_syncTimer is not null && _syncDeadline <= deadline)
        {
            return;   // 现有的计时器不晚于需要的时刻:它到点求值时会再排下一个,不必每次取消重建
        }
        CancelSyncTimer();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _syncTimer = cts;
        _syncDeadline = deadline;
        _ = FireSyncTimerAsync(TimeSpan.FromMilliseconds(Math.Min(soonest, int.MaxValue)), cts);
    }

    private void CancelSyncTimer()
    {
        _syncTimer?.Cancel();
        _syncTimer?.Dispose();
        _syncTimer = null;
        _syncDeadline = long.MaxValue;
    }

    private async Task FireSyncTimerAsync(TimeSpan delay, CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            Post(null, () =>
            {
                if (ReferenceEquals(_syncTimer, cts))
                {
                    CancelSyncTimer();   // 到点了:让下一次 ScheduleSyncTimer 重新排
                }
                EvaluateSync();
            });
        }
        catch (OperationCanceledException)
        {
            // 被新的计时器取代,或者服务端收工。
        }
    }

    // ------------------------------------------------------------------ 事件

    /// <summary>
    /// Await 结束时的 CounterNotify(规范 Await):每个触发器各查一次 —— 差值 = 计数器 − 测试值,Positive* 的差值不小于 event-threshold、
    /// Negative* 的不大于它才发(超出 INT64 不发;为假的触发器也可能发);计数器被销毁的那几个不论阈值一定发,destroyed = True。
    /// 一次 Await 的事件连着发,count 是后面还有几条。原先读了 event-threshold 却从不用,只给成立的那个计数器发。
    /// </summary>
    /// <param name="client">等待的客户端。</param>
    /// <param name="wait">它的等待条件。</param>
    /// <param name="destroyed">哪些计数器没了(null = 没有)。</param>
    private void SendCounterNotifies(XClient client, SyncWait wait, Func<XSyncCounter, bool>? destroyed)
    {
        List<(uint Counter, long WaitValue, long Value, bool Destroyed)> events = [];
        foreach ((XSyncTrigger trigger, long threshold) in wait.Conditions)
        {
            if (trigger.Counter is not { } counter)
            {
                continue;
            }
            if (destroyed?.Invoke(counter) == true)
            {
                events.Add((counter.Id, trigger.TestValue, counter.Value, true));
                continue;
            }
            long value = CounterValue(counter);
            Int128 difference = (Int128)value - trigger.TestValue;
            if (difference > long.MaxValue || difference < long.MinValue)
            {
                continue;
            }
            bool positive = trigger.TestType is XSyncTrigger.PositiveTransition or XSyncTrigger.PositiveComparison;
            if (positive ? difference >= threshold : difference <= threshold)
            {
                events.Add((counter.Id, trigger.TestValue, value, false));
            }
        }
        uint time = Now;
        for (int i = 0; i < events.Count; i++)
        {
            (uint id, long waitValue, long value, bool gone) = events[i];
            int remaining = events.Count - 1 - i;
            client.Event(SyncEventBase, 0, w =>
            {
                w.U32(id);
                WriteInt64(w, waitValue);
                WriteInt64(w, value);
                w.U32(time).U16((ushort)Math.Min(remaining, ushort.MaxValue)).Bool(gone);
            });
        }
    }

    /// <param name="alarm">报警器(状态已经是新的)。</param>
    /// <param name="counterValue">触发它的计数器值。</param>
    /// <param name="alarmValue">触发时的测试值。</param>
    private void SendAlarmNotify(XSyncAlarm alarm, long counterValue, long alarmValue)
    {
        uint time = Now;
        foreach (XClient client in alarm.Listeners)
        {
            if (client.Closed)
            {
                continue;
            }
            client.Event(SyncEventBase + 1, 0, w =>
            {
                w.U32(alarm.Id);
                WriteInt64(w, counterValue);
                WriteInt64(w, alarmValue);
                w.U32(time).U8(alarm.State);
            });
        }
    }

    /// <summary>客户端断开:它的 Await 作废,它不再收任何报警器的事件。</summary>
    private void CleanupSync(XClient client)
    {
        _syncWaits.Remove(client);
        foreach (XSyncAlarm alarm in _alarms)
        {
            alarm.Listeners.Remove(client);
        }
    }

    /// <summary>
    /// 客户端的资源销毁了(<see cref="Extension.ClientResourcesDestroyed" />):它的报警器作废;它的计数器没了,按「计数器被销毁」处理别人的
    /// 报警器与等待;等它的栅栏的 AwaitFence 放行。以 Retain 模式断开时资源还在,这些都不动(报警器照常触发、计数器与栅栏照常可等)。
    /// </summary>
    private void CleanupSyncResources(XClient client)
    {
        foreach (XSyncAlarm alarm in _alarms.ToArray())
        {
            if (ReferenceEquals(alarm.Owner, client))
            {
                ForgetAlarm(alarm);
            }
        }
        CounterGone(k => k.SystemName is null && !ReferenceEquals(Lookup<XSyncCounter>(k.Id), k));
        // 它的栅栏随它没了:等这些栅栏的 AwaitFence 放行。
        foreach ((XClient waiter, SyncWait wait) in _syncWaits.ToArray())
        {
            if (_syncWaits.ContainsKey(waiter) && wait.Fences.Any(f => !ReferenceEquals(Lookup<XSyncFence>(f.Id), f)))
            {
                EndWait(waiter);
            }
        }
    }
}
