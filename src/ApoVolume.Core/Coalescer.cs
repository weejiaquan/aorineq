namespace ApoVolume.Core;

/// <summary>
/// Runs the first posted action near-immediately on a ThreadPool thread (never synchronously
/// on the caller), then at most one action per interval, always executing the most recently
/// posted action (trailing edge).
/// </summary>
public sealed class Coalescer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly object _runLock = new();
    private Action? _pending;
    // Monotonic post ordering: _pendingSeq (under _lock) stamps each posted action, _lastRunSeq
    // (under _runLock) records the newest stamp that has executed. A dequeued action only runs if
    // its stamp is newer — so when Flush() races the timer thread, whichever runs second sees the
    // other's newer stamp and skips, and an older action can never overwrite a newer one's effect.
    private long _pendingSeq;
    private long _lastRunSeq;
    private bool _cooldown;
    private bool _disposed;

    public Coalescer(TimeSpan interval)
    {
        _interval = interval;
        _timer = new Timer(_ => OnTimer());
    }

    public void Post(Action action)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _pending = action; // latest-wins: overwrites whatever hasn't run yet
            _pendingSeq++;
            if (_cooldown)
            {
                return; // already scheduled; will be picked up by the current cooldown cycle
            }
            _cooldown = true;
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); // fire promptly, off the caller's thread
        }
    }

    private void OnTimer()
    {
        Action? run;
        long seq;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            run = _pending;
            seq = _pendingSeq;
            _pending = null;
            if (run is null)
            {
                _cooldown = false; // idle: nothing arrived during the last interval
                return;
            }
        }
        RunIfNewest(run, seq);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            // Re-arm for the cooldown window: catches anything posted while `run` executed.
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Synchronously runs the pending action (if any) on the calling thread. Also acts as a
    /// barrier: if the timer thread is mid-action, this blocks until it finishes, so on return
    /// every posted action has actually executed. Used where a write must land before the caller
    /// proceeds (e.g. before relaunching elevated), and by <see cref="Dispose"/> so a trailing
    /// write is never silently dropped at shutdown.
    /// </summary>
    public void Flush()
    {
        Action? pending;
        long seq;
        lock (_lock)
        {
            pending = _pending;
            seq = _pendingSeq;
            _pending = null;
        }
        if (pending is not null)
        {
            RunIfNewest(pending, seq);
            return;
        }
        // Nothing to dequeue, but the newest posted action (stamp `seq`) may be in flight on the
        // timer thread — dequeued from _pending yet not executed. Merely acquiring _runLock isn't
        // enough (the timer thread may not hold it yet), so wait until that stamp has actually run.
        lock (_runLock)
        {
            while (_lastRunSeq < seq)
            {
                Monitor.Wait(_runLock);
            }
        }
    }

    /// <summary>Runs the dequeued action under <see cref="_runLock"/> unless an action with a
    /// newer stamp already ran — the timer thread and <see cref="Flush"/> can dequeue in one order
    /// and reach <see cref="_runLock"/> in the other, and this check keeps a stale action from
    /// executing (and overwriting the newer one's effect) after that reordering.</summary>
    private void RunIfNewest(Action run, long seq)
    {
        lock (_runLock)
        {
            if (seq > _lastRunSeq)
            {
                // Advanced before run() so a throwing action still releases Flush waiters.
                _lastRunSeq = seq;
                try
                {
                    run();
                }
                catch (Exception)
                {
                    // Actions own their error handling. This guard only prevents an unhandled
                    // exception on the ThreadPool timer thread from killing the process.
                }
            }
            Monitor.PulseAll(_runLock); // wake Flush waiters on the run and skip paths alike
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _timer.Dispose();
        Flush(); // the trailing action must land, not be dropped with it
    }
}
