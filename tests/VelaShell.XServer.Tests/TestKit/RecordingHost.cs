using System.Collections.Concurrent;

namespace VelaShell.XServer.Tests.TestKit;

/// <summary>记下服务端对宿主的每一次通知,并能等某个条件成立。</summary>
internal sealed class RecordingHost : IX11ServerHost, IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly SemaphoreSlim _changed = new(0);

    public ConcurrentDictionary<uint, XTopLevelWindow> Mapped { get; } = new();

    public IReadOnlyCollection<string> Log => _log;

    /// <summary>最近一次 <see cref="CursorChanged" /> 收到的光标。</summary>
    public XCursor? Cursor { get; private set; }

    /// <summary>最近一次光标变化指名的顶层(null = 不在任何顶层上)。</summary>
    public XTopLevelWindow? CursorWindow { get; private set; }

    /// <summary>最近一次 <see cref="TopLevelChanged" /> 报告的变化。</summary>
    public XTopLevelChanges LastChanges { get; private set; }

    /// <summary>收到的窗口管理器请求(按顺序)。</summary>
    public ConcurrentQueue<XWindowManagerRequest> Requests { get; } = new();

    /// <summary>最近一次 <see cref="ClipboardChanged" /> 收到的文本。</summary>
    public string? Clipboard { get; private set; }

    public void Dispose() => _changed.Dispose();

    private void Note(string entry)
    {
        _log.Enqueue(entry);
        _changed.Release();
    }

    public void TopLevelMapped(XTopLevelWindow window)
    {
        Mapped[window.Id] = window;
        XTopLevelSnapshot s = window.Snapshot;
        Note($"mapped {window.Id:x} {s.Width}x{s.Height}");
    }

    public void TopLevelUnmapped(XTopLevelWindow window)
    {
        Mapped.TryRemove(window.Id, out _);
        Note($"unmapped {window.Id:x}");
    }

    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes)
    {
        LastChanges = changes;
        Note($"changed {window.Id:x} {changes} '{window.Snapshot.Title}'");
    }

    /// <summary>收到的损伤矩形(按顺序,不分窗口)。</summary>
    public ConcurrentQueue<XRect> DamageRects { get; } = new();

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
    {
        foreach (XRect rect in damage)
        {
            DamageRects.Enqueue(rect);
        }
        Note($"damaged {window.Id:x} {damage.Count}");
    }

    public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
    {
        Cursor = cursor;
        CursorWindow = window;
        Note($"cursor {cursor.Shape}{(cursor.Image is { } image ? $" {image.Width}x{image.Height}" : "")}");
    }

    public void BellRequested(int volume) => Note($"bell {volume}");

    public void WindowManagerRequested(XWindowManagerRequest request)
    {
        Requests.Enqueue(request);
        Note($"wm {request.GetType().Name}");
    }

    public ConcurrentDictionary<XTopLevelWindow, string> TrayIcons { get; } = new();

    public ConcurrentQueue<XTopLevelWindow> TrayIconsRemoved { get; } = new();

    public void SystemTrayIconAdded(XTopLevelWindow icon, string title)
    {
        TrayIcons[icon] = title;
        Note($"tray +{icon.Id:x} {title}");
    }

    public void SystemTrayIconRemoved(XTopLevelWindow icon)
    {
        TrayIcons.TryRemove(icon, out _);
        TrayIconsRemoved.Enqueue(icon);
        Note($"tray -{icon.Id:x}");
    }

    public ConcurrentQueue<XServerGrabStall> ServerGrabStalls { get; } = new();

    public void ServerGrabStalled(XServerGrabStall stall)
    {
        ServerGrabStalls.Enqueue(stall);
        Note($"server grab stalled by {stall.ClientId}");
    }

    public void ClipboardChanged(string text)
    {
        Clipboard = text;
        Note($"clipboard {text.Length}");
    }

    /// <summary><see cref="PointerWarped" /> 收到的根坐标,按先后。</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(int X, int Y)> Warps { get; } = new();

    /// <summary><see cref="PointerConfinementChanged" /> 收到的范围,按先后(null = 解除)。</summary>
    public System.Collections.Concurrent.ConcurrentQueue<XRect?> Confinements { get; } = new();

    public void PointerWarped(int rootX, int rootY)
    {
        Warps.Enqueue((rootX, rootY));
        Note($"warp {rootX},{rootY}");
    }

    public void PointerConfinementChanged(XRect? area)
    {
        Confinements.Enqueue(area);
        Note($"confine {area}");
    }

    /// <summary><see cref="TopLevelRedrawn" /> 报过的窗口,按先后。</summary>
    public System.Collections.Concurrent.ConcurrentQueue<XTopLevelWindow> Redrawn { get; } = new();

    public void TopLevelRedrawn(XTopLevelWindow window)
    {
        Redrawn.Enqueue(window);
        Note($"redrawn {window.Id:x}");
    }

    /// <summary><see cref="ScreenSaverSuspensionChanged" /> 收到的值,按先后。</summary>
    public System.Collections.Concurrent.ConcurrentQueue<bool> SaverSuspensions { get; } = new();

    /// <summary><see cref="ScreenSaverReset" /> 收到的次数。</summary>
    public int SaverResets => _saverResets;

    private int _saverResets;

    public void ScreenSaverSuspensionChanged(bool suspended)
    {
        SaverSuspensions.Enqueue(suspended);
        Note($"saver suspended {suspended}");
    }

    public void ScreenSaverReset()
    {
        Interlocked.Increment(ref _saverResets);
        Note("saver reset");
    }

    /// <summary>最近一次 <see cref="ClipboardContentChanged" /> 收到的整份内容(各种格式)。</summary>
    public XClipboardContent? ClipboardContent { get; private set; }

    public void ClipboardContentChanged(XClipboardContent content)
    {
        ClipboardContent = content;
        if (content.Text is { } text)
        {
            Clipboard = text;
        }
        Note($"clipboard content {content.Text?.Length} / {content.Html?.Length} / {content.Png.Length}");
    }

    /// <summary>等到条件成立(每次有新通知时重新检查)。</summary>
    public async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        using CancellationTokenSource cts = new(timeoutMs);
        while (!condition())
        {
            await _changed.WaitAsync(cts.Token);
        }
    }

    /// <summary>拷一份顶层窗口当前的像素。</summary>
    public static (uint[] Pixels, int Width, int Height) Snapshot(XTopLevelWindow window)
    {
        XTopLevelSnapshot s = window.Snapshot;
        uint[] buffer = new uint[s.Width * s.Height];
        (int w, int h) = window.CopyPixels(buffer);
        return (buffer, w, h);
    }
}
