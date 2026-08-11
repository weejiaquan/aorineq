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
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            run = _pending;
            _pending = null;
            if (run is null)
            {
                _cooldown = false; // idle: nothing arrived during the last interval
                return;
            }
        }
        lock (_runLock)
        {
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
        lock (_lock)
        {
            pending = _pending;
            _pending = null;
        }
        lock (_runLock) // taken even when pending is null: waits out an in-flight timer action
        {
            if (pending is null)
            {
                return;
            }
            try
            {
                pending();
            }
            catch (Exception)
            {
                // Same contract as OnTimer: actions own their error handling.
            }
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
