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

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage) => Note($"damaged {window.Id:x} {damage.Count}");

    public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
    {
        Cursor = cursor;
        Note($"cursor {cursor.Shape}{(cursor.Image is { } image ? $" {image.Width}x{image.Height}" : "")}");
    }

    public void BellRequested(int volume) => Note($"bell {volume}");

    public void WindowManagerRequested(XWindowManagerRequest request)
    {
        Requests.Enqueue(request);
        Note($"wm {request.GetType().Name}");
    }

    public void ClipboardChanged(string text)
    {
        Clipboard = text;
        Note($"clipboard {text.Length}");
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
