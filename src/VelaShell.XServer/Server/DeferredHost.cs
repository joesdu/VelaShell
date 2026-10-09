// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer.Server;

/// <summary>
/// 把对宿主的回调攒起来,等执行线程放掉像素锁之后按原顺序一次调完。
/// </summary>
/// <remarks>
/// <para>
/// 为什么:执行线程执行一批请求期间持有像素锁;宿主的 UI 线程读像素(<see cref="XTopLevelWindow.ReadPixels" />)也要这把锁。
/// 回调要是在持锁时调、宿主又在回调里同步等 UI 线程,两边就互相等死了。
/// </para>
/// <para>
/// 代价:同一批里先映射后改标题,宿主收到 TopLevelMapped 时窗口快照已经是改过标题的了 —— 快照总是最新的,
/// 回调的<b>顺序</b>不变。宿主回调抛的异常记进日志后吞掉,一个出错的宿主不会拖垮执行线程。
/// 只在执行线程上用。
/// </para>
/// </remarks>
internal sealed class DeferredHost(IX11ServerHost inner, Action<string> log) : IX11ServerHost
{
    /// <summary>攒下的回调;被合并掉、抵消掉的位置置 null。</summary>
    private readonly List<Action?> _pending = [];
    private readonly List<Action?> _running = [];

    // 同一批里的合并(xs_plan WN-S8):一个客户端循环改标题、反复映射 / 取消映射、狂发窗口管理器请求,原先每一下都排一个回调,
    // 宿主的 UI 线程就不停建、关原生窗口,队列无界增长。
    private readonly Dictionary<XTopLevelWindow, (int Index, XTopLevelChanges Changes)> _changed = [];
    private readonly Dictionary<XTopLevelWindow, int> _mapped = [];
    private int _cursorAt = -1, _clipboardAt = -1, _requests;
    private (XTopLevelWindow? Window, XCursor Cursor) _cursor;
    private XClipboardContent _clipboard = new();

    /// <summary>一批里最多交这么多个窗口管理器请求,多的丢掉(真实程序一批里不过一两个)。</summary>
    internal const int MaxRequestsPerBatch = 32;

    public void TopLevelMapped(XTopLevelWindow window)
    {
        _changed.Remove(window);   // 之后的变化不再并进映射之前的那一条
        _mapped[window] = _pending.Count;
        _pending.Add(() => inner.TopLevelMapped(window));
    }

    public void TopLevelUnmapped(XTopLevelWindow window)
    {
        _changed.Remove(window);
        if (_mapped.Remove(window, out int mappedAt))
        {
            _pending[mappedAt] = null;   // 同一批里映射了又取消映射:宿主根本不必知道,两条一起抵消
            return;
        }
        _pending.Add(() => inner.TopLevelUnmapped(window));
    }

    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes)
    {
        if (_changed.TryGetValue(window, out (int Index, XTopLevelChanges Changes) earlier))
        {
            // 同一批里同一个窗口的变化或起来、并成一条(放在第一次的位置;快照本来就总是最新的)。
            XTopLevelChanges merged = earlier.Changes | changes;
            _changed[window] = (earlier.Index, merged);
            _pending[earlier.Index] = () => inner.TopLevelChanged(window, merged);
            return;
        }
        _changed[window] = (_pending.Count, changes);
        _pending.Add(() => inner.TopLevelChanged(window, changes));
    }

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage) =>
        _pending.Add(() => inner.TopLevelDamaged(window, damage));

    /// <summary>光标只有最后一次有意义:同一批里只交最后一次(放在第一次的位置)。</summary>
    public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
    {
        _cursor = (window, cursor);
        if (_cursorAt < 0)
        {
            _cursorAt = _pending.Count;
            _pending.Add(() => inner.CursorChanged(_cursor.Window, _cursor.Cursor));
        }
    }

    /// <summary>
    /// 响铃合并、节流:一批里最多交一次(取最大音量),两次之间至少隔 <see cref="MinBellInterval" />。
    /// 一个循环发 4 字节 Bell 的客户端原先每批都往宿主的 UI 线程排成千上万次提示音,整个界面跟着卡死。
    /// </summary>
    public void BellRequested(int volume)
    {
        if (_pendingBellVolume >= 0)
        {
            _pendingBellVolume = Math.Max(_pendingBellVolume, volume);
            return;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastBell != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastBell, now) < MinBellInterval)
        {
            return;
        }
        _lastBell = now;
        _pendingBellVolume = volume;
        _pending.Add(() =>
        {
            int loudest = _pendingBellVolume;
            _pendingBellVolume = -1;
            inner.BellRequested(loudest);
        });
    }

    /// <summary>两次响铃之间至少隔这么久。</summary>
    internal static readonly TimeSpan MinBellInterval = TimeSpan.FromMilliseconds(100);

    private int _pendingBellVolume = -1;
    private long _lastBell;

    /// <summary>剪贴板同样只交最后一次。</summary>
    public void ClipboardChanged(string text) => ClipboardContentChanged(new XClipboardContent { Text = text });

    public void ClipboardContentChanged(XClipboardContent content)
    {
        _clipboard = content;
        if (_clipboardAt < 0)
        {
            _clipboardAt = _pending.Count;
            _pending.Add(() => inner.ClipboardContentChanged(_clipboard));
        }
    }

    /// <summary>单窗口模式:窗口管理器是远端的,服务端不向宿主提窗口管理器的请求(宿主手里也没有那些顶层)。</summary>
    public bool DropWindowManagerRequests { get; set; }

    public void WindowManagerRequested(XWindowManagerRequest request)
    {
        if (DropWindowManagerRequests)
        {
            return;
        }
        if (++_requests > MaxRequestsPerBatch)
        {
            if (_requests == MaxRequestsPerBatch + 1)
            {
                log($"window manager requests over {MaxRequestsPerBatch} in one batch: the rest are dropped");
            }
            return;
        }
        _pending.Add(() => inner.WindowManagerRequested(request));
    }

    public void ServerGrabStalled(XServerGrabStall stall) => _pending.Add(() => inner.ServerGrabStalled(stall));

    public void SystemTrayIconAdded(XTopLevelWindow icon, string title) => _pending.Add(() => inner.SystemTrayIconAdded(icon, title));

    public void SystemTrayIconRemoved(XTopLevelWindow icon) => _pending.Add(() => inner.SystemTrayIconRemoved(icon));

    private int _warpAt = -1;
    private (int X, int Y) _warp;

    /// <summary>挪指针:同一批里只交最后一次(拖拽中的程序每次移动都可能 Warp 回中心)。</summary>
    public void PointerWarped(int rootX, int rootY)
    {
        _warp = (rootX, rootY);
        if (_warpAt < 0)
        {
            _warpAt = _pending.Count;
            _pending.Add(() => inner.PointerWarped(_warp.X, _warp.Y));
        }
    }

    public void PointerConfinementChanged(XRect? area) => _pending.Add(() => inner.PointerConfinementChanged(area));

    public void ScreenSaverSuspensionChanged(bool suspended) => _pending.Add(() => inner.ScreenSaverSuspensionChanged(suspended));

    public void ScreenSaverReset() => _pending.Add(inner.ScreenSaverReset);

    public void TopLevelRedrawn(XTopLevelWindow window) => _pending.Add(() => inner.TopLevelRedrawn(window));

    /// <summary>调完攒下的回调。回调里再引起的回调(宿主同步调了注入方法 —— 那只是排工作项,不会同步回来)留到下一轮。</summary>
    public void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }
        _running.AddRange(_pending);
        _pending.Clear();
        _changed.Clear();
        _mapped.Clear();
        _requests = 0;
        foreach (Action? call in _running)
        {
            if (call is null)
            {
                continue;
            }
            try
            {
                call();
            }
            catch (Exception ex)
            {
                log($"host callback failed: {ex}");
            }
        }
        _running.Clear();
        _cursorAt = -1;
        _clipboardAt = -1;
        _warpAt = -1;
    }
}

/// <summary>没有宿主时的空实现(无头运行:测试与诊断)。</summary>
internal sealed class NullHost : IX11ServerHost
{
    public static readonly NullHost Instance = new();

    public void TopLevelMapped(XTopLevelWindow window)
    {
    }

    public void TopLevelUnmapped(XTopLevelWindow window)
    {
    }

    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes)
    {
    }

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
    {
    }

    public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
    {
    }

    public void BellRequested(int volume)
    {
    }

    public void ClipboardChanged(string text)
    {
    }
}
