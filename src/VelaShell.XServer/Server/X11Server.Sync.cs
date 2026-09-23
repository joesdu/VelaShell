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

namespace VelaShell.XServer.Server;

/// <summary>SYNC 计数器。系统计数器(SERVERTIME / IDLETIME)的值由服务端按需算出。</summary>
internal sealed class XSyncCounter(uint id, XClient? owner, long value, string? systemName = null) : XResource(id, owner)
{
    public long Value { get; set; } = value;

    /// <summary>系统计数器的名字;普通计数器为 null。</summary>
    public string? SystemName { get; } = systemName;
}

/// <summary>SYNC 的触发器:计数器与等待值的比较。</summary>
internal sealed class XSyncTrigger
{
    public const uint PositiveTransition = 0, NegativeTransition = 1, PositiveComparison = 2, NegativeComparison = 3;

    public XSyncCounter? Counter { get; set; }

    public uint ValueType { get; set; }   // 0 Absolute,1 Relative(已换算成 Absolute 存在 WaitValue 里)

    public long WaitValue { get; set; }

    public uint TestType { get; set; }

    /// <summary>上一次求值时计数器的值(Transition 要看「从哪一侧跨过来」)。</summary>
    public long LastValue { get; set; }

    public bool Satisfied(long value) => TestType switch
    {
        PositiveComparison => value >= WaitValue,
        NegativeComparison => value <= WaitValue,
        PositiveTransition => LastValue < WaitValue && value >= WaitValue,
        _ => LastValue > WaitValue && value <= WaitValue,
    };
}

/// <summary>SYNC 报警器。</summary>
internal sealed class XSyncAlarm(uint id, XClient owner) : XResource(id, owner)
{
    public const byte Active = 0, Inactive = 1, Destroyed = 2;

    public XSyncTrigger Trigger { get; } = new();

    public long Delta { get; set; } = 1;

    public byte State { get; set; } = Active;

    /// <summary>要收 AlarmNotify 的客户端(创建者默认要;别的客户端经 ChangeAlarm 的 events 选)。</summary>
    public HashSet<XClient> Listeners { get; } = [];
}

/// <summary>SYNC 栅栏。</summary>
internal sealed class XSyncFence(uint id, XClient owner, bool triggered) : XResource(id, owner)
{
    public bool Triggered { get; set; } = triggered;
}

public sealed partial class X11Server
{
    private const byte SyncMajor = 140;
    private const byte SyncEventBase = 70;   // CounterNotify +0,AlarmNotify +1
    private const byte SyncErrorBase = 138;  // Counter +0,Alarm +1,Fence +2

    private const uint ServerTimeCounterId = 0x44, IdleTimeCounterId = 0x45;

    private XSyncCounter? _serverTimeCounter;
    private XSyncCounter? _idleTimeCounter;

    /// <summary>正在 Await 的客户端 → 它等的条件与暂存的请求。</summary>
    private readonly Dictionary<XClient, SyncWait> _syncWaits = [];

    private readonly List<XSyncAlarm> _alarms = [];

    private CancellationTokenSource? _syncTimer;

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
        Lookup<XSyncCounter>(id) ?? throw new XProtocolError((XErrorCode)SyncErrorBase, id);

    private XSyncAlarm Alarm(uint id) =>
        Lookup<XSyncAlarm>(id) ?? throw new XProtocolError((XErrorCode)(SyncErrorBase + 1), id);

    private XSyncFence Fence(uint id) =>
        Lookup<XSyncFence>(id) ?? throw new XProtocolError((XErrorCode)(SyncErrorBase + 2), id);

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
        if (t.Counter is { } counter)
        {
            long now = CounterValue(counter);
            if (t.ValueType == 1)
            {
                t.WaitValue = unchecked(now + t.WaitValue);   // Relative → Absolute(规范:在设置时换算)
                t.ValueType = 0;
            }
            t.LastValue = now;
        }
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
    }

    private void DestroyCounter(XSyncCounter counter)
    {
        RemoveResource(counter.Id);
        // 等它的 Await 以 destroyed = True 的 CounterNotify 结束;挂着它的报警器进入 Inactive(规范 DestroyCounter)。
        foreach ((XClient client, SyncWait wait) in _syncWaits.ToArray())
        {
            if (wait.Conditions.Any(cond => ReferenceEquals(cond.Trigger.Counter, counter)))
            {
                SendCounterNotify(client, wait, counter, destroyed: true);
                EndWait(client);
            }
        }
        foreach (XSyncAlarm alarm in _alarms)
        {
            if (ReferenceEquals(alarm.Trigger.Counter, counter))
            {
                alarm.Trigger.Counter = null;
                alarm.State = XSyncAlarm.Inactive;
            }
        }
    }

    private void DestroyAlarm(XSyncAlarm alarm)
    {
        RemoveResource(alarm.Id);
        _alarms.Remove(alarm);
        alarm.State = XSyncAlarm.Destroyed;
        SendAlarmNotify(alarm, CounterValue(alarm.Trigger.Counter ?? _serverTimeCounter!));
    }

    // ------------------------------------------------------------------ Await

    private void BeginWait(XClient c, SyncWait wait)
    {
        if (WaitSatisfied(wait, out _))
        {
            return;   // 条件已经成立:不用停
        }
        _syncWaits[c] = wait;
        ScheduleSyncTimer();
    }

    private bool WaitSatisfied(SyncWait wait, out XSyncCounter? firing)
    {
        firing = null;
        if (wait.Fences.Any(f => f.Triggered))
        {
            return true;
        }
        foreach ((XSyncTrigger trigger, _) in wait.Conditions)
        {
            if (trigger.Counter is { } counter && trigger.Satisfied(CounterValue(counter)))
            {
                firing = counter;
                return true;
            }
        }
        return false;
    }

    private void EndWait(XClient client)
    {
        if (!_syncWaits.Remove(client, out SyncWait? wait))
        {
            return;
        }
        foreach (WorkItem item in wait.Deferred)
        {
            RunItem(item);
        }
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

    /// <summary>计数器变了(或者时间到了、用户有了输入):检查所有 Await 与报警器。</summary>
    private void EvaluateSync()
    {
        if (_syncWaits.Count == 0 && _alarms.Count == 0)
        {
            return;
        }
        foreach ((XClient client, SyncWait wait) in _syncWaits.ToArray())
        {
            if (WaitSatisfied(wait, out XSyncCounter? firing))
            {
                if (firing is not null)
                {
                    SendCounterNotify(client, wait, firing, destroyed: false);
                }
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
            SendAlarmNotify(alarm, value);
            // 触发之后按 delta 推进等待值;比较型测试 delta 为 0、或推进会溢出时报警器停用。
            if (alarm.Delta == 0 && t.TestType is XSyncTrigger.PositiveComparison or XSyncTrigger.NegativeComparison)
            {
                alarm.State = XSyncAlarm.Inactive;
            }
            else
            {
                int guard = 0;
                do
                {
                    long next = unchecked(t.WaitValue + alarm.Delta);
                    if ((alarm.Delta > 0 && next < t.WaitValue) || (alarm.Delta < 0 && next > t.WaitValue) || ++guard > 1_000_000)
                    {
                        alarm.State = XSyncAlarm.Inactive;
                        break;
                    }
                    t.WaitValue = next;
                }
                while (alarm.Delta != 0 && t.TestType is XSyncTrigger.PositiveComparison or XSyncTrigger.NegativeComparison
                       && t.Satisfied(value));
            }
            t.LastValue = value;
        }
        ScheduleSyncTimer();
    }

    /// <summary>
    /// 有触发器挂在系统计数器上时,算出最早可能成立的时刻并定一个计时器。
    /// SERVERTIME 一毫秒一毫秒地涨;IDLETIME 在没有输入时同样一毫秒一毫秒地涨(有输入时由 NoteUserActivity 触发求值)。
    /// </summary>
    private void ScheduleSyncTimer()
    {
        long soonest = long.MaxValue;
        void Consider(XSyncTrigger t)
        {
            if (t.Counter is { SystemName: not null } counter
                && t.TestType is XSyncTrigger.PositiveComparison or XSyncTrigger.PositiveTransition)
            {
                soonest = Math.Min(soonest, Math.Max(1, t.WaitValue - CounterValue(counter)));
            }
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

        _syncTimer?.Cancel();
        _syncTimer?.Dispose();
        _syncTimer = null;
        if (soonest == long.MaxValue)
        {
            return;
        }
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _syncTimer = cts;
        _ = FireSyncTimerAsync(TimeSpan.FromMilliseconds(Math.Min(soonest, int.MaxValue)), cts.Token);
    }

    private async Task FireSyncTimerAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            Post(null, EvaluateSync);
        }
        catch (OperationCanceledException)
        {
            // 被新的计时器取代,或者服务端收工。
        }
    }

    // ------------------------------------------------------------------ 事件

    private void SendCounterNotify(XClient client, SyncWait wait, XSyncCounter counter, bool destroyed)
    {
        long value = counter.SystemName is null && destroyed ? counter.Value : CounterValue(counter);
        uint time = Now;
        List<(XSyncTrigger Trigger, long Threshold)> matching = [.. wait.Conditions.Where(cond => ReferenceEquals(cond.Trigger.Counter, counter))];
        for (int i = 0; i < matching.Count; i++)
        {
            (XSyncTrigger trigger, _) = matching[i];
            int remaining = matching.Count - 1 - i;
            client.Event(SyncEventBase, 0, w =>
            {
                w.U32(counter.Id);
                WriteInt64(w, trigger.WaitValue);
                WriteInt64(w, value);
                w.U32(time).U16((ushort)remaining).Bool(destroyed);
            });
        }
    }

    private void SendAlarmNotify(XSyncAlarm alarm, long counterValue)
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
                WriteInt64(w, alarm.Trigger.WaitValue);
                w.U32(time).U8(alarm.State);
            });
        }
    }

    /// <summary>客户端断开:它的 Await 作废,它不再收任何报警器的事件。</summary>
    private void CleanupSync(XClient client)
    {
        _syncWaits.Remove(client);
        // 别的客户端在等这个客户端的计数器:计数器随它一起没了,按「计数器被销毁」结束那些等待。
        foreach ((XClient waiter, SyncWait wait) in _syncWaits.ToArray())
        {
            if (wait.Conditions.FirstOrDefault(cond => cond.Trigger.Counter is { } k && !_resources.ContainsKey(k.Id)).Trigger?.Counter is { } gone)
            {
                SendCounterNotify(waiter, wait, gone, destroyed: true);
                EndWait(waiter);
            }
        }
        foreach (XSyncAlarm alarm in _alarms.ToArray())
        {
            alarm.Listeners.Remove(client);
            if (ReferenceEquals(alarm.Owner, client))
            {
                _alarms.Remove(alarm);
            }
        }
    }
}
