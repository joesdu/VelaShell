using System.Collections.Concurrent;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Host;

namespace VelaShell.XServer.Tests.TestKit;

/// <summary>记下服务端对宿主的每一次通知,并能等某个条件成立。</summary>
internal sealed class RecordingHost : IXServerHost, IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly SemaphoreSlim _changed = new(0);

    public ConcurrentDictionary<uint, XTopLevelWindow> Mapped { get; } = new();

    public IReadOnlyCollection<string> Log => _log;

    public void Dispose() => _changed.Dispose();

    private void Note(string entry)
    {
        _log.Enqueue(entry);
        _changed.Release();
    }

    public void TopLevelMapped(XTopLevelWindow window)
    {
        Mapped[window.Id] = window;
        Note($"mapped {window.Id:x} {window.Width}x{window.Height}");
    }

    public void TopLevelUnmapped(XTopLevelWindow window)
    {
        Mapped.TryRemove(window.Id, out _);
        Note($"unmapped {window.Id:x}");
    }

    public void TopLevelChanged(XTopLevelWindow window) => Note($"changed {window.Id:x} '{window.Title}'");

    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage) => Note($"damaged {window.Id:x} {damage.Count}");

    public void CursorChanged(XTopLevelWindow? window, int cursorGlyph) => Note($"cursor {cursorGlyph}");

    public void Bell(int percent) => Note($"bell {percent}");

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
        uint[] buffer = new uint[window.Width * window.Height];
        (int w, int h) = window.CopyPixels(buffer);
        return (buffer, w, h);
    }
}
