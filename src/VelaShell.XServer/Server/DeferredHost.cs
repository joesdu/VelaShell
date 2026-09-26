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
    private readonly List<Action> _pending = [];
    private readonly List<Action> _running = [];

    public void TopLevelMapped(XTopLevelWindow window) => _pending.Add(() => inner.TopLevelMapped(window));

    public void TopLevelUnmapped(XTopLevelWindow window) => _pending.Add(() => inner.TopLevelUnmapped(window));

    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes) =>
        _pending.Add(() => inner.TopLevelChanged(window, changes));

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage) =>
        _pending.Add(() => inner.TopLevelDamaged(window, damage));

    public void CursorChanged(XTopLevelWindow? window, XCursor cursor) => _pending.Add(() => inner.CursorChanged(window, cursor));

    public void BellRequested(int volume) => _pending.Add(() => inner.BellRequested(volume));

    public void ClipboardChanged(string text) => _pending.Add(() => inner.ClipboardChanged(text));

    public void WindowManagerRequested(XWindowManagerRequest request) => _pending.Add(() => inner.WindowManagerRequested(request));

    /// <summary>调完攒下的回调。回调里再引起的回调(宿主同步调了注入方法 —— 那只是排工作项,不会同步回来)留到下一轮。</summary>
    public void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }
        _running.AddRange(_pending);
        _pending.Clear();
        foreach (Action call in _running)
        {
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
