// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Diagnostics;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Host;

namespace VelaShell.XServer.Server;

/// <summary>
/// 把对宿主的回调攒起来,等执行线程放掉 <see cref="X11Server.PixelLock" /> 之后按原顺序一次调完。
/// </summary>
/// <remarks>
/// <para>
/// 为什么:执行线程执行一批请求期间持有 PixelLock;宿主的 UI 线程读像素(<see cref="XTopLevelWindow.CopyPixels" />)也要这把锁。
/// 回调要是在持锁时调、宿主又在回调里同步等 UI 线程,两边就互相等死了。
/// </para>
/// <para>
/// 代价:同一批里先映射后改标题,宿主收到 TopLevelMapped 时窗口快照已经是改过标题的了 —— 快照总是最新的,
/// 回调的<b>顺序</b>不变。宿主回调抛的异常记到 Trace 后吞掉,一个出错的宿主不会拖垮执行线程。
/// 只在执行线程上用。
/// </para>
/// </remarks>
internal sealed class DeferredHost(IXServerHost inner) : IXServerHost
{
    private readonly List<Action> _pending = [];
    private readonly List<Action> _running = [];

    public void TopLevelMapped(XTopLevelWindow window) => _pending.Add(() => inner.TopLevelMapped(window));

    public void TopLevelUnmapped(XTopLevelWindow window) => _pending.Add(() => inner.TopLevelUnmapped(window));

    public void TopLevelChanged(XTopLevelWindow window) => _pending.Add(() => inner.TopLevelChanged(window));

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage) =>
        _pending.Add(() => inner.TopLevelDamaged(window, damage));

    public void CursorChanged(XTopLevelWindow? window, int cursorGlyph) => _pending.Add(() => inner.CursorChanged(window, cursorGlyph));

    public void Bell(int percent) => _pending.Add(() => inner.Bell(percent));

    public void ClipboardChanged(string text) => _pending.Add(() => inner.ClipboardChanged(text));

    public void WindowManagerRequest(XWindowManagerRequest request) => _pending.Add(() => inner.WindowManagerRequest(request));

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
                Trace.WriteLine($"[X11Server] host callback failed: {ex}");
            }
        }
        _running.Clear();
    }
}
