using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using PulseTerm.Core.Ssh;

namespace PulseTerm.Terminal;

public class SshTerminalBridge : IDisposable
{
    private readonly ITerminalEmulator _terminal;
    private readonly IShellStreamWrapper _shellStream;
    private readonly CancellationTokenSource _cts;
    private Task? _readTask;
    private volatile bool _disposed;
    private int _started;

    // Output-batching pump: the read thread enqueues raw chunks and requests a single
    // coalesced flush on the UI thread, instead of marshaling + feeding once per read.
    // Under bursty output (apt/yum, cat, progress bars) this collapses hundreds of
    // cross-thread hops + full redraws into one Feed per frame.
    private readonly object _pendingLock = new();
    private readonly List<byte[]> _pending = new();
    private int _flushScheduled;

    public event Action<Exception>? Error;

    /// <summary>
    /// Raised when the remote side closes the channel (e.g. the shell ran <c>exit</c> or the
    /// server rebooted): the read loop ended on its own rather than via <see cref="Dispose"/>.
    /// Lets the session transition to a disconnected state that can be reconnected in place.
    /// Not raised during intentional teardown. Fired on the read thread — marshal as needed.
    /// </summary>
    public event Action? Closed;

    public SshTerminalBridge(ITerminalEmulator terminal, IShellStreamWrapper shellStream)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _cts = new CancellationTokenSource();

        _terminal.UserInput += OnUserInput;
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Bridge already started");

        // Only start reading. Do NOT prime the shell with a newline — the server already sends
        // its banner and prompt on connect, so an extra '\n' produces a duplicate prompt line.
        _readTask = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        // A larger read buffer means fewer awaits and larger natural batches.
        var buffer = new byte[16384];
        bool remoteClosed = false;

        try
        {
            while (!_cts.Token.IsCancellationRequested && _shellStream.CanRead)
            {
                var bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    // EOF: the remote closed the channel (exit / reboot / dropped connection).
                    remoteClosed = true;
                    break;
                }

                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);

                // Do NOT await a per-read UI hop. Queue the chunk and coalesce; the read
                // thread keeps pace with the network while the UI drains at frame rate.
                EnqueueForFeed(data);
            }

            // Loop also exits when the stream reports it can no longer be read.
            if (!_cts.Token.IsCancellationRequested)
                remoteClosed = true;
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — not an error
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed during shutdown — not an error
        }
        catch (Exception ex)
        {
            remoteClosed = true;
            Error?.Invoke(ex);
        }

        // Signal a remote-initiated close, but not our own Dispose()-driven teardown.
        if (remoteClosed && !_disposed)
            Closed?.Invoke();
    }

    private void EnqueueForFeed(byte[] data)
    {
        lock (_pendingLock)
            _pending.Add(data);

        // Schedule at most one pending UI flush; further chunks piggyback on it.
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            Dispatcher.UIThread.Post(FlushPending);
    }

    private void FlushPending()
    {
        // Reset first so chunks arriving during the drain schedule a fresh flush.
        Interlocked.Exchange(ref _flushScheduled, 0);

        byte[] combined;
        lock (_pendingLock)
        {
            int count = _pending.Count;
            if (count == 0)
                return;

            if (count == 1)
            {
                combined = _pending[0];
            }
            else
            {
                int total = 0;
                for (int i = 0; i < count; i++)
                    total += _pending[i].Length;

                combined = new byte[total];
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    var chunk = _pending[i];
                    Array.Copy(chunk, 0, combined, offset, chunk.Length);
                    offset += chunk.Length;
                }
            }

            _pending.Clear();
        }

        if (_disposed)
            return;

        try
        {
            // One Feed per flush => one Updated => one repaint, regardless of chunk count.
            _terminal.Feed(combined);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private void OnUserInput(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
            return;

        // Fire-and-forget with error handling — this is an event handler so async void is acceptable
        _ = WriteUserInputAsync(data);
    }

    private async Task WriteUserInputAsync(byte[] data)
    {
        try
        {
            await _shellStream.WriteAsync(data, 0, data.Length, CancellationToken.None).ConfigureAwait(false);
            _shellStream.Flush();
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed — expected during teardown
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _terminal.UserInput -= OnUserInput;

        _cts.Cancel();

        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Swallow faults from read task during dispose
        }

        _cts.Dispose();
        _shellStream.Dispose();
    }
}
