namespace VelaShell.Services;

/// <summary>
/// Manages the <see cref="CancellationTokenSource" /> lifecycle for the sync-debounce
/// timer with a terminal shutdown gate. After <see cref="Shutdown" /> is called,
/// <see cref="TrySwapNew" /> atomically refuses to create new CTS objects so no
/// debounce work can start past the disposal boundary.
/// </summary>
internal sealed class SyncDebounceLifecycle
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private bool _shutdown;

    /// <summary>
    /// Atomically checks the shutdown gate and, if still live, replaces the current
    /// CTS with a fresh one (cancelling and disposing whatever was held). Returns
    /// <c>true</c> with a valid <paramref name="token" /> on success; returns <c>false</c>
    /// after <see cref="Shutdown" /> has been called \u2014 the caller must not start debounce
    /// work in that case.
    /// </summary>
    public bool TrySwapNew(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_shutdown)
            {
                token = CancellationToken.None;
                return false;
            }

            var next = new CancellationTokenSource();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = next;
            token = next.Token;
            return true;
        }
    }

    /// <summary>
    /// After a debounce delay completes, atomically verifies the shutdown gate and
    /// that <paramref name="token" /> is still the current debounce, then invokes
    /// <paramref name="start" /> while holding the gate so <see cref="Shutdown" /> cannot
    /// complete between the check and the delegate invocation. Returns <c>true</c> with
    /// the started task on success; returns <c>false</c> (task is null) if the lifecycle
    /// has been shut down or the token was superseded.
    /// </summary>
    public bool TryStartCurrent(CancellationToken token, Func<Task> start, out Task? task)
    {
        lock (_gate)
        {
            if (_shutdown || _cts is null || _cts.Token != token)
            {
                task = null;
                return false;
            }

            // Invoke the delegate while holding the lock \u2014 Shutdown cannot
            // complete between our check and the delegate invocation.
            task = start();

            // Clear the CTS \u2014 this debounce has been consumed.
            _cts.Dispose();
            _cts = null;

            return true;
        }
    }

    /// <summary>
    /// Terminal shutdown: cancels and disposes the current CTS (if any) and prevents
    /// any future <see cref="TrySwapNew" /> or <see cref="TryStartCurrent" /> from
    /// succeeding. Safe to call multiple times and safe to call when no CTS has ever
    /// been created.
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _shutdown = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
